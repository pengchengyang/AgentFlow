// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowGraph.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFlow.Core;

/// <summary>A node instance in the graph.</summary>
public sealed class NodeSpec
{
    public string Id { get; set; } = "";
    public string TypeId { get; set; } = "";

    /// <summary>User-editable instance name shown on the canvas (falls back to the type display name).</summary>
    public string? Name { get; set; }

    /// <summary>Start-up priority, mirroring ALC's "Startup Priority": lower runs earlier; bigger means later.</summary>
    public int Priority { get; set; }

    public Dictionary<string, object?> Parameters { get; set; } = new();
    // UI layout info (written by the editor, ignored by the engine).
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A pin-to-pin connection.</summary>
public sealed class ConnectionSpec
{
    public string FromNode { get; set; } = "";
    public string FromPin { get; set; } = "";
    public string ToNode { get; set; } = "";
    public string ToPin { get; set; } = "";
}

/// <summary>
/// Workflow graph: nodes + connections. Serializable to JSON
/// (records how nodes relate to each other).
/// </summary>
public sealed class WorkflowGraph
{
    public List<NodeSpec> Nodes { get; set; } = new();
    public List<ConnectionSpec> Connections { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WorkflowGraph FromJson(string json) =>
        JsonSerializer.Deserialize<WorkflowGraph>(json, JsonOptions)
            ?? throw new InvalidDataException("Workflow JSON is empty or malformed");

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public static WorkflowGraph Load(string path) => FromJson(File.ReadAllText(path));
}
