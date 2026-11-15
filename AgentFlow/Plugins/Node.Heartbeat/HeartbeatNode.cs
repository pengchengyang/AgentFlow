// -----------------------------------------------------------------------
// <copyright company="Your Company" file="HeartbeatNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.Heartbeat;

/// <summary>
/// Forwards every input sample immediately and repeatedly publishes a cloned copy of the most
/// recent sample on a fixed periodic cadence. The periodic timer keeps running even when no new
/// input arrives; it sends nothing until the first sample has been received.
/// </summary>
public sealed class HeartbeatNode : BaseNode
{
    private static readonly string[] DataTypeOptions =
    {
        "string", "int32", "int64", "float", "double", "binary", "bool"
    };

    private readonly BasePin _inputPin =
        new("input", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _outputPin =
        new("output", DataType.STRING, PinDirection.Output);

    private readonly object _stateGate = new();
    private readonly object _lifecycleGate = new();

    private Sample? _latestSample;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private ILogger? _logger;

    public HeartbeatNode()
    {
        Uuid = "DF63993F-568A-4158-81B5-7675E4368387";
        AddParam();
        AddPins();
        ApplyDataType(Param("DataType").Value);
    }

    public override string Name => "Heartbeat";
    public override string DisplayName { get; set; } = "Heartbeat";
    public override string Category => "General";

    public override void AddPins()
    {
        AddInputPin(_inputPin);
        AddOutputPin(_outputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "DataType", typeof(string), "string",
            isEditable: true, group: "General", zone: "HeartbeatZone",
            editor: ParameterEditorKind.SingleSelect,
            options: DataTypeOptions));
        AddParameter(new NodeParameter(
            "IntervalMilliseconds", typeof(int), 1000,
            isEditable: true, group: "General", zone: "HeartbeatZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("DataType", out var dataType) && dataType is not null)
            Param("DataType").Value = ParameterValues.GetString(dataType, "string") ?? "string";

        if (parameters.TryGetValue("IntervalMilliseconds", out var interval))
            Param("IntervalMilliseconds").Value = ParameterValues.GetInt(interval, 1000);

        ApplyDataType(Param("DataType").Value);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        await StopHeartbeatAsync().ConfigureAwait(false);

        _logger = context.Logger;
        Running = true;

        lock (_stateGate)
        {
            _latestSample = null;
        }

        var intervalMilliseconds = ParameterValues.GetInt(Param("IntervalMilliseconds").Value, 1000);
        if (intervalMilliseconds <= 0)
        {
            _logger?.LogWarning(
                "Heartbeat interval {IntervalMs}ms is not positive; immediate forwarding remains enabled, but periodic heartbeat sending is disabled.",
                intervalMilliseconds);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => HeartbeatLoopAsync(intervalMilliseconds, cts.Token));

        lock (_lifecycleGate)
        {
            _heartbeatCts = cts;
            _heartbeatTask = task;
        }

        _logger?.LogInformation(
            "Heartbeat started with a fixed periodic interval of {IntervalMs}ms.",
            intervalMilliseconds);
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = false;
        await StopHeartbeatAsync().ConfigureAwait(false);
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => StopHeartbeatAsync();

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!ReferenceEquals(pin, _inputPin))
        {
            _logger?.LogWarning("Heartbeat ignored a sample from an unknown pin {Pin}.", pin.Name);
            return;
        }

        if (!Running)
        {
            _logger?.LogWarning("Heartbeat ignored input on {Pin} because the node is not running.", pin.Name);
            return;
        }

        if (value is null)
        {
            _logger?.LogWarning("Heartbeat ignored a null sample on {Pin}.", pin.Name);
            return;
        }

        if (value.Type != _inputPin.DataType)
        {
            _logger?.LogWarning(
                "Heartbeat ignored a {ActualType} sample on {Pin}; the configured pin type is {ExpectedType}.",
                value.Type,
                pin.Name,
                _inputPin.DataType);
            return;
        }

        Sample cachedSample;
        try
        {
            // The cached value must be independent because Sample objects are mutable.
            cachedSample = CloneSample(value);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Heartbeat could not cache the sample received on {Pin}.", pin.Name);
            return;
        }

        _outputPin.SendSampleWithDispatchLog(value, $"immediate {value.Type}", LogLevel.Trace);

        lock (_stateGate)
        {
            _latestSample = cachedSample;
        }
    }

    private async Task HeartbeatLoopAsync(int intervalMilliseconds, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMilliseconds));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!Running)
                {
                    _logger?.LogWarning("Heartbeat periodic loop stopped because the node is no longer running.");
                    return;
                }

                Sample? snapshot;
                lock (_stateGate)
                {
                    snapshot = _latestSample is null ? null : CloneSample(_latestSample);
                }

                if (snapshot is null)
                    continue;

                _outputPin.SendSampleWithDispatchLog(snapshot, $"periodic {snapshot.Type}", LogLevel.Trace);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the workflow or node stops.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Heartbeat periodic loop failed.");
        }
    }

    private async Task StopHeartbeatAsync()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_lifecycleGate)
        {
            cts = _heartbeatCts;
            task = _heartbeatTask;
            _heartbeatCts = null;
            _heartbeatTask = null;
        }

        if (cts is null)
        {
            lock (_stateGate)
            {
                _latestSample = null;
            }
            return;
        }

        try
        {
            cts.Cancel();
            if (task is not null)
                await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Heartbeat failed while stopping the periodic loop.");
        }
        finally
        {
            cts.Dispose();
            lock (_stateGate)
            {
                _latestSample = null;
            }
        }
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private void ApplyDataType(object? value)
    {
        var dataType = ParseDataType(value);
        _inputPin.DataType = dataType;
        _outputPin.DataType = dataType;
    }

    private static DataType ParseDataType(object? value)
    {
        return (ParameterValues.GetString(value) ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "string" => DataType.STRING,
            "int32" => DataType.INT32,
            "int64" => DataType.INT64,
            "float" => DataType.FLOAT,
            "double" => DataType.DOUBLE,
            "binary" => DataType.BIN,
            "bool" => DataType.BOOL,
            _ => DataType.STRING,
        };
    }

    private static Sample CloneSample(Sample sample)
    {
        switch (sample)
        {
            case StringSample source:
            {
                var clone = StringSample.CreateSample();
                clone.SetValue(source.StringValue, source.Key);
                return clone;
            }
            case Int32Sample source:
            {
                var clone = Int32Sample.CreateSample();
                clone.SetValue(source.Int32Value, source.Key);
                return clone;
            }
            case Int64Sample source:
            {
                var clone = Int64Sample.CreateSample();
                clone.SetValue(source.Int64Value, source.Key);
                return clone;
            }
            case FloatSample source:
            {
                var clone = FloatSample.CreateSample();
                clone.SetValue(source.FloatValue, source.Key);
                return clone;
            }
            case DoubleSample source:
            {
                var clone = DoubleSample.CreateSample();
                clone.SetValue(source.DoubleValue, source.Key);
                return clone;
            }
            case BoolSample source:
            {
                var clone = BoolSample.CreateSample();
                clone.SetValue(source.BoolValue, source.Key);
                return clone;
            }
            case BinSample source:
            {
                var clone = BinSample.CreateSample();
                clone.SetValue(source.BinValue?.ToArray(), source.Key);
                return clone;
            }
            default:
                throw new NotSupportedException(
                    $"Heartbeat cannot clone sample type '{sample.GetType().FullName}'.");
        }
    }
}