// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FactCallerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FactCaller;

/// <summary>
/// Default skeleton for a fact lookup node. It declares the request / fact pins and
/// the endpoint configuration expected by the future business implementation.
/// </summary>
public sealed class FactCallerNode : BaseNode
{
    private readonly BasePin _requestPin =
        new("request", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _factPin =
        new("fact", DataType.STRING, PinDirection.Output);

    private readonly object _requestGate = new();
    private ILogger? _logger;
    private string? _lastRequest;

    public FactCallerNode()
    {
        Uuid = "1617370B-3D5A-4685-85E6-A9BFE294E44D";
        AddParam();
        AddPins();
    }

    public override string Name => "Fact Caller";
    public override string DisplayName { get; set; } = "Fact Caller";
    public override string Category => "Data";

    public override void AddPins()
    {
        AddInputPin(_requestPin);
        AddOutputPin(_factPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "Endpoint", typeof(string), "", isEditable: true,
            group: "Connection", zone: "FactCallerZone"));
        AddParameter(new NodeParameter(
            "TimeoutSeconds", typeof(int), 30, isEditable: true,
            group: "Connection", zone: "FactCallerZone"));
        AddParameter(new NodeParameter(
            "FactKey", typeof(string), "", isEditable: true,
            group: "Request", zone: "FactCallerZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Endpoint", out var endpoint))
            Param("Endpoint").Value = ParameterValues.GetString(endpoint, "") ?? "";
        if (parameters.TryGetValue("TimeoutSeconds", out var timeout))
            Param("TimeoutSeconds").Value = ParameterValues.GetInt(timeout, 30);
        if (parameters.TryGetValue("FactKey", out var factKey))
            Param("FactKey").Value = ParameterValues.GetString(factKey, "") ?? "";
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _logger?.LogTrace("FactCaller is running with endpoint {Endpoint}.", Param("Endpoint").Value);
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!ReferenceEquals(pin, _requestPin) || value is not StringSample request ||
            request.StringValue is not { } requestText)
        {
            return;
        }

        lock (_requestGate)
            _lastRequest = requestText;

        _logger?.LogTrace("FactCaller received request for fact key {FactKey}: {Request}",
            Param("FactKey").Value, requestText);

        // TODO: Call the configured fact service and send the resulting fact via _factPin.
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}