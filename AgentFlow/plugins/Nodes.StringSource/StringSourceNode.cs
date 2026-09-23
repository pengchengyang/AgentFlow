// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="StringSourceNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Nodes.StringSource;

/// <summary>
/// String source node: a single OUTPUT pin that emits a (configurable) string.
/// </summary>
[Node("nodes.string-source", "String Source", "Input")]
public sealed class StringSourceNode : BaseNode
{
    private string _text = "Hello, AgentFlow!";

    public override string TypeId => "nodes.string-source";
    public override string DisplayName => "String Source";

    public StringSourceNode()
    {
        AddOutputPin(new("Output", typeof(string), PinDirection.Output));
    }

    public override IReadOnlyList<ParameterDefinition> Parameters =>
    [
        new("Text", typeof(string), "Text", "Hello, AgentFlow!", "The string emitted on the output pin")
    ];

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Text", out var v) && v is not null)
            _text = v.ToString() ?? string.Empty;
    }

    /// <summary>Append this node's own logical field to the serialization JSON.</summary>
    protected override void OnSerializeParameters(JsonObject json)
    {
        json["text"] = _text;
    }

    /// <summary>Restore this node's own logical field from the deserialization JSON.</summary>
    protected override void OnDeserializeParameters(JsonObject json)
    {
        _text = json["text"]?.GetValue<string>() ?? _text;
    }

    public override Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("String Source emitted: {Text}", _text);
        // SetOutput -> output pin Send -> every connected input pin Receive.
        context.SetOutput("Output", _text);
        return Task.CompletedTask;
    }
}
