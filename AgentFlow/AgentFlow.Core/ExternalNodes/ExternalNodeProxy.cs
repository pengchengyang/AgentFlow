// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalNodeProxy.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>
/// Core-side BaseNode facade for one external Worker node instance. Pin and lifecycle calls are
/// translated into the multilingual NDJSON protocol; the rest of AgentFlow sees a normal BaseNode.
/// </summary>
public sealed class ExternalNodeProxy : BaseNode
{
    private readonly ExternalNodeDefinition _definition;
    private readonly ExternalNodeRuntime _runtime;
    private readonly object _parameterGate = new();
    private readonly object _inputGate = new();
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Sample?>> _pendingInputs = new(StringComparer.Ordinal);
    private ExternalWorkerProcess? _worker;
    private INodeContext? _context;
    private string? _runId;
    private bool _created;
    private bool _shutDown;
    private long _sequence;
    private int _faultReported;

    public ExternalNodeProxy(ExternalNodeDefinition definition, ExternalNodeRuntime runtime)
    {
        _definition = definition;
        _runtime = runtime;
        Uuid = definition.NodeType.NodeType;
        DisplayName = definition.NodeType.DisplayName;
        AddParam();
        AddPins();
        foreach (var parameter in definition.Parameters)
            SetParameterValue(parameter.Name, parameter.DefaultValue);
    }

    public override string Name => _definition.NodeType.NodeType;
    public override string DisplayName { get; set; }
    public override string Category => _definition.NodeType.Category;
    public ExternalNodeDefinition Definition => _definition;

    protected override void AddParam()
    {
        base.AddParam();
        foreach (var parameter in _definition.Parameters)
        {
            AddParameter(new NodeParameter(
                parameter.Name,
                parameter.DataType,
                parameter.DefaultValue,
                isEditable: true,
                group: "General",
                editor: parameter.Editor,
                options: parameter.Options));
        }
    }

    public override void AddPins()
    {
        foreach (var pin in _definition.InputPins)
            AddInputPin(new BasePin(pin.Name, pin.DataType, AgentFlow.Contracts.PinDirection.Input, pin.Required));
        foreach (var pin in _definition.OutputPins)
            AddOutputPin(new BasePin(pin.Name, pin.DataType, AgentFlow.Contracts.PinDirection.Output, pin.Required));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.Count == 0)
            return;

        lock (_parameterGate)
        {
            foreach (var pair in parameters)
            {
                if (pair.Key is "Name" or "Uuid" or "InstanceId" or "DisplayName" or "DependsOn")
                    continue;
                _parameters[pair.Key] = pair.Value;
                SetParameterValue(pair.Key, pair.Value);
            }
        }

        var snapshot = GetParameterSnapshot();
        _ = ConfigureWorkerAsync(snapshot);
    }

    /// <summary>Start the Worker process and create its node instance for load-stage subscriptions.</summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_shutDown)
            throw new InvalidOperationException($"External node '{Name}' was already shut down.");
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task Initialize(INodeContext context, CancellationToken cancellationToken = default)
    {
        if (_shutDown)
            throw new InvalidOperationException($"External node '{Name}' was already shut down.");
        _context = context;
        SetConnecting();

        ExternalWorkerProcess? worker = null;
        try
        {
            worker = await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await worker.CommandAsync(InstanceId, "init", cancellationToken).ConfigureAwait(false);
            SetIdle();
        }
        catch
        {
            _created = false;
            if (worker is not null)
            {
                try { await worker.RemoveNodeAsync(InstanceId, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            SetFailed();
            throw;
        }
    }

    public override async Task Run(INodeContext context, CancellationToken cancellationToken = default)
    {
        if (_worker is null || !_created)
            throw new InvalidOperationException($"External node '{Name}' must be initialized before Run.");
        _context = context;
        _runId = Guid.NewGuid().ToString("N");
        await _worker.CommandAsync(InstanceId, "run", cancellationToken, _runId).ConfigureAwait(false);
        Running = true;
        await FlushPendingInputsAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task Stop(INodeContext context, CancellationToken cancellationToken = default)
    {
        Running = false;
        if (_worker is null || !_created)
            return;
        try
        {
            await _worker.CommandAsync(InstanceId, "stop", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _context?.Logger.LogWarning(ex, "External node '{Node}' stop command failed.", DisplayName);
        }
        finally
        {
            Running = false;
        }
    }

    public override async Task Shutdown(INodeContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        await ReleaseAsync(context.Logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Release this node from the Worker when the editor removes it before/after Run.</summary>
    public Task ReleaseAsync(CancellationToken cancellationToken = default) =>
        ReleaseAsync(_context?.Logger, cancellationToken);

    private async Task ReleaseAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        if (_shutDown)
            return;

        _shutDown = true;
        Running = false;
        var worker = _worker;
        if (worker is null)
            return;

        if (_created)
        {
            try
            {
                await worker.CommandAsync(InstanceId, "shutdown", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "External node '{Node}' shutdown command failed.", DisplayName);
            }
        }

        _created = false;
        try
        {
            await worker.RemoveNodeAsync(InstanceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "External node '{Node}' could not be released from the Worker.", DisplayName);
        }
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (pin.Direction != AgentFlow.Contracts.PinDirection.Input)
            return;

        if (_worker is null || !_created || !_worker.IsReady || !Running)
        {
            lock (_inputGate)
            {
                if (!_pendingInputs.TryGetValue(pin.Name, out var queue))
                {
                    queue = new Queue<Sample?>();
                    _pendingInputs[pin.Name] = queue;
                }
                queue.Enqueue(value);
            }
            return;
        }

        _ = SendInputAsync(pin.Name, value, CancellationToken.None);
    }

    public void HandleWorkerPinSend(string sourcePin, JsonNode? sampleNode)
    {
        var pin = OutputPins.FirstOrDefault(pin =>
            string.Equals(pin.Name, sourcePin, StringComparison.Ordinal));
        if (pin is null)
            throw new InvalidDataException($"External node '{Name}' sent unknown output pin '{sourcePin}'.");

        var sample = ExternalSampleCodec.FromProtocol(sampleNode, pin.DataType, sourcePin);
        if (_context is null)
            throw new InvalidOperationException($"External node '{Name}' has no active Core context.");
        _context.SetOutput(sourcePin, sample);
    }

    public void HandleWorkerState(string? state)
    {
        if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
            SetFailed();
        else if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            SetRunning();
        else if (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase))
            Running = false;
    }

    private async Task<ExternalWorkerProcess> EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_created && _worker is not null)
            return _worker;

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_created && _worker is not null)
                return _worker;

            var worker = _worker ??= _runtime.GetWorker(_definition);
            await worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var values = GetParameterSnapshot();
            await worker.CreateNodeAsync(this, values, cancellationToken).ConfigureAwait(false);
            _created = true;
            return worker;
        }
        finally
        {
            _createGate.Release();
        }
    }

    private async Task ConfigureWorkerAsync(IReadOnlyDictionary<string, object?> parameters)
    {
        var worker = _worker;
        if (worker is null || !_created || !worker.IsReady)
            return;

        try
        {
            await worker.ConfigureNodeAsync(InstanceId, parameters, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _faultReported, 1) == 0)
                _context?.Logger.LogError(ex, "External node '{Node}' configuration failed.", DisplayName);
            SetFailed();
        }
    }

    private async Task FlushPendingInputsAsync(CancellationToken cancellationToken)
    {
        List<(string Pin, Sample? Sample)> pending = new();
        lock (_inputGate)
        {
            foreach (var pair in _pendingInputs)
            {
                while (pair.Value.Count > 0)
                    pending.Add((pair.Key, pair.Value.Dequeue()));
            }
            _pendingInputs.Clear();
        }

        foreach (var item in pending)
            await SendInputAsync(item.Pin, item.Sample, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInputAsync(string pinName, Sample? sample, CancellationToken cancellationToken)
    {
        var worker = _worker;
        if (worker is null)
            return;

        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var encoded = ExternalSampleCodec.ToProtocol(sample, pinName);
            await worker.SendPinReceiveAsync(
                InstanceId, pinName, sequence, _runId, encoded, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _faultReported, 1) == 0)
            {
                _context?.Logger.LogError(ex,
                    "External node '{Node}' could not deliver input on pin '{Pin}'.", DisplayName, pinName);
            }
            SetFailed();
        }
    }

    private IReadOnlyDictionary<string, object?> GetParameterSnapshot()
    {
        lock (_parameterGate)
            return new Dictionary<string, object?>(_parameters, StringComparer.Ordinal);
    }

    private void SetParameterValue(string name, object? value)
    {
        var parameter = NodeParameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, name, StringComparison.Ordinal));
        if (parameter is not null)
            parameter.Value = value;
    }
}




