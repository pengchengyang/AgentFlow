// -----------------------------------------------------------------------
// <copyright company="Your Company" file="DatabaseWriterNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.DatabaseWriter;

/// <summary>
/// Default skeleton for a database sink. Configuration and lifecycle are in place;
/// persistence and transaction behavior will be implemented by the future business layer.
/// </summary>
public sealed class DatabaseWriterNode : BaseNode
{
    private readonly BasePin _dataPin =
        new("data", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _statusPin =
        new("status", DataType.STRING, PinDirection.Output);

    private readonly object _dataGate = new();
    private ILogger? _logger;
    private string? _lastData;

    public DatabaseWriterNode()
    {
        Uuid = "0A7931E7-0B68-4D05-863F-FE63E4070CF6";
        AddParam();
        AddPins();
    }

    public override string Name => "Database Writer";
    public override string DisplayName { get; set; } = "Database Writer";
    public override string Category => "Database";

    public override void AddPins()
    {
        AddInputPin(_dataPin);
        AddOutputPin(_statusPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "ConnectionString", typeof(string), "", isEditable: true,
            group: "Connection", zone: "DatabaseWriterZone"));
        AddParameter(new NodeParameter(
            "TableName", typeof(string), "", isEditable: true,
            group: "Target", zone: "DatabaseWriterZone"));
        AddParameter(new NodeParameter(
            "BatchSize", typeof(int), 1, isEditable: true,
            group: "Write", zone: "DatabaseWriterZone"));
        AddParameter(new NodeParameter(
            "TimeoutSeconds", typeof(int), 30, isEditable: true,
            group: "Connection", zone: "DatabaseWriterZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("ConnectionString", out var connectionString))
            Param("ConnectionString").Value = ParameterValues.GetString(connectionString, "") ?? "";
        if (parameters.TryGetValue("TableName", out var tableName))
            Param("TableName").Value = ParameterValues.GetString(tableName, "") ?? "";
        if (parameters.TryGetValue("BatchSize", out var batchSize))
            Param("BatchSize").Value = ParameterValues.GetInt(batchSize, 1);
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
        _logger?.LogTrace("DatabaseWriter is running for table {TableName}.", Param("TableName").Value);
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
        if (!ReferenceEquals(pin, _dataPin) || value is not StringSample data ||
            data.StringValue is not { } dataText)
        {
            return;
        }

        lock (_dataGate)
            _lastData = dataText;

        _logger?.LogTrace("DatabaseWriter received data for table {TableName}: {Data}",
            Param("TableName").Value, dataText);

        // TODO: Write data using the configured connection and publish a status sample.
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}