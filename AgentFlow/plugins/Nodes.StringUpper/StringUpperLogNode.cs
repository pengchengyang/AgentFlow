// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="StringUpperLogNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Nodes.StringUpper;

/// <summary>
/// String to-upper logger: a single INPUT pin. Whatever string it receives is
/// converted to upper case and written to the log.
/// </summary>
public sealed class StringUpperLogNode : BaseNode
{
    public override string TypeId => "nodes.string-upper";
    public override string DisplayName => "String To Upper / Log";
    public override string Category => "Logic";

    public StringUpperLogNode()
    {
        Uuid = "nodes.string-upper";
        AddInputPin(new("Input", typeof(string), PinDirection.Input));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            var value = context.GetInput<string>("Input") ?? string.Empty;
            var upper = value.ToUpperInvariant();
            context.Logger.LogInformation("String To Upper (log): {Value}", upper);
            return Task.CompletedTask;
        }
        finally
        {
            Running = false;
        }
    }
    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }
}
