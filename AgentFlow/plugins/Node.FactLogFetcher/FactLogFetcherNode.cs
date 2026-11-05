// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FactLogFetcherNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FactLogFetcher;

/// <summary>
/// Default skeleton for retrieving logs associated with a fact. The actual log source
/// integration is intentionally deferred; the node currently validates and records input.
/// </summary>
public sealed class FactLogFetcherNode : BaseNode
{
    private readonly BasePin _factIdPin =
        new("factId", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _logPin =
        new("log", DataType.STRING, PinDirection.Output);

    private readonly object _factGate = new();
    private ILogger? _logger;
    private string? _lastFactId;

    public FactLogFetcherNode()
    {
        Uuid = "15B5C750-76A9-424D-A8DE-18FDCDC58687";
        AddParam();
        AddPins();
    }

    public override string Name => "Fact Log Fetcher";
    public override string DisplayName { get; set; } = "Fact Log Fetcher";
    public override string Category => "Logging";

    public override void AddPins()
    {
        AddInputPin(_factIdPin);
        AddOutputPin(_logPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "LogSource", typeof(string), "", isEditable: true,
            group: "Source", zone: "FactLogFetcherZone"));
        AddParameter(new NodeParameter(
            "QueryLimit", typeof(int), 100, isEditable: true,
            group: "Query", zone: "FactLogFetcherZone"));
        AddParameter(new NodeParameter(
            "TimeoutSeconds", typeof(int), 30, isEditable: true,
            group: "Query", zone: "FactLogFetcherZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("LogSource", out var source))
            Param("LogSource").Value = ParameterValues.GetString(source, "") ?? "";
        if (parameters.TryGetValue("QueryLimit", out var limit))
            Param("QueryLimit").Value = ParameterValues.GetInt(limit, 100);
        if (parameters.TryGetValue("TimeoutSeconds", out var timeout))
            Param("TimeoutSeconds").Value = ParameterValues.GetInt(timeout, 30);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _logger?.LogTrace("FactLogFetcher is running with source {LogSource}.", Param("LogSource").Value);
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
        if (!ReferenceEquals(pin, _factIdPin) || value is not StringSample factId ||
            factId.StringValue is not { } factIdText)
        {
            return;
        }

        lock (_factGate)
            _lastFactId = factIdText;

        _logger?.LogTrace("FactLogFetcher received fact id {FactId}.", factIdText);

        // TODO: Query the configured log source and send the resulting log via _logPin.
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}