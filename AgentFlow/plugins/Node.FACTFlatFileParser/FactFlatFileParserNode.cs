// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FactFlatFileParserNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FACTFlatFileParser;

/// <summary>
/// FACT flat-file parser skeleton. Parsing behavior will be implemented in a later change.
/// </summary>
public sealed class FactFlatFileParserNode : BaseNode
{
    private readonly BasePin _runInputPin =
        new("input_string_run", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _contentsOutputPin =
        new("output_string_contents", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;

    public FactFlatFileParserNode()
    {
        Uuid = "167802E5-B5E7-4A35-BEF4-710D4A7C4FDC";
        AddParam();
        AddPins();
    }

    public override string Name => "FACT Flat File Parser";
    public override string DisplayName { get; set; } = "FACT Flat File Parser";
    public override string Category => "Parsing";

    public override void AddPins()
    {
        AddInputPin(_runInputPin);
        AddOutputPin(_contentsOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        // This skeleton has no configurable parameters yet.
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = true;
        _logger?.LogTrace("FACT Flat File Parser is running.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger?.LogTrace("FACT Flat File Parser stopped.");
        _logger = null;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!ReferenceEquals(pin, _runInputPin) ||
            value is not StringSample { StringValue: { } inputText })
        {
            return;
        }

        _logger?.LogTrace(
            "FACT Flat File Parser received run input through {Pin}: {Input}. Parsing is not implemented yet.",
            pin.Name,
            inputText);
    }
}
