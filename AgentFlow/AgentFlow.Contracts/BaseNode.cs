// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BaseNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json.Nodes;
using System.Text.Json;

namespace AgentFlow.Contracts;

/// <summary>
/// Base class for every AgentFlow node. A node declares its pins by calling
/// <see cref="AddInputPin"/> / <see cref="AddOutputPin"/> (the pin sets are stored in
/// mutable lists, so a node can append pins as needed), and provides <see cref="FunctionName"/>,
/// <see cref="DisplayName"/>, <see cref="Configure"/> and <see cref="Run"/>.
/// <see cref="Initialize"/> and <see cref="Stop"/> default to no-ops; <see cref="Run"/> must be implemented.
/// </summary>
public abstract class BaseNode
{
    private readonly List<BasePin> _inputPins = new();
    private readonly List<BasePin> _outputPins = new();
    private readonly List<NodeParameter> _nodeParameters = new();

    /// <summary>Names of the built-in General parameters that every node owns (not shown in the property-panel <see cref="Parameters"/> list).</summary>
    private static readonly HashSet<string> BuiltInParamNames = new(StringComparer.Ordinal)
    {
        "DisplayName", "DependsOn", "Uuid", "FunctionName", "InstanceId"
    };

    protected BaseNode()
    {
        // Uuid is intentionally left empty here. Each derived class assigns its own
        // value as the unique type identifier of that subclass.
        AddParam();
    }

    /// <summary>Node type id (globally unique, e.g. "basic.add"). Serialized into JSON.</summary>
    public abstract string FunctionName { get; }

    /// <summary>Unique id of this node instance. Assigned/managed by AgentFlow.Core.</summary>
    public int InstanceId { get; set; }

    /// <summary>
    /// Instance id of another node this node depends on before it may run (optional).
    /// Serialized into the node's <c>logic</c> blob so dependencies persist across saves.
    /// </summary>
    public int DependsOn { get; set; }

    /// <summary>
    /// Runtime-only state: true while this node is currently executing.
    /// This value is intentionally NOT serialized into JSON.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Unique type identifier of this subclass. It is left empty in <see cref="BaseNode"/>
    /// and must be assigned by each derived class (representing the unique type of that node).
    /// </summary>
    public string Uuid { get; protected set; } = string.Empty;

    /// <summary>Display name in the UI.</summary>
    public abstract string DisplayName { get; }

    /// <summary>
    /// User-editable instance display name (shown on canvas). Persisted as the editable
    /// DisplayName General parameter and to <c>logic.name</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Node category (used by the palette grouping and accent color).</summary>
    public abstract string Category { get; }

    /// <summary>All input pin definitions (the left-hand ports), append via <see cref="AddInputPin"/>.</summary>
    public IReadOnlyList<BasePin> InputPins => _inputPins;

    /// <summary>All output pin definitions (the right-hand ports), append via <see cref="AddOutputPin"/>.</summary>
    public IReadOnlyList<BasePin> OutputPins => _outputPins;

    /// <summary>Append an input pin to the input list.</summary>
    public void AddInputPin(BasePin pin) => _inputPins.Add(pin);

    /// <summary>Append an output pin to the output list.</summary>
    public void AddOutputPin(BasePin pin) => _outputPins.Add(pin);

    /// <summary>
    /// Parameter declarations (used to auto-generate the property panel). Derived automatically
    /// from the parameters declared via <see cref="AddParam"/> / <see cref="AddParameter"/>, so a
    /// node declares its parameters only once and the base class surfaces them for both the
    /// property panel and JSON serialization. The built-in General parameters
    /// (<see cref="BuiltInParamNames"/>) are excluded from this list.
    /// </summary>
    public IReadOnlyList<ParameterDefinition> Parameters =>
        _nodeParameters
            .Where(p => !string.IsNullOrEmpty(p.Name) && !BuiltInParamNames.Contains(p.Name))
            .Select(p => new ParameterDefinition(p.Name, p.Type, p.Name, p.Value))
            .ToList();

    /// <summary>Parameters attached to this node (type + value + editable flag).</summary>
    public IReadOnlyList<NodeParameter> NodeParameters => _nodeParameters;

    /// <summary>
    /// Append a parameter (type + value + editable flag) to this node. The parameter's
    /// <see cref="NodeParameter.Group"/> is taken from the <see cref="NodeParameter"/> itself.
    /// </summary>
    public void AddParameter(NodeParameter parameter)
    {
        _nodeParameters.Add(parameter);
    }

    /// <summary>
    /// Hook for subclasses to declare their parameters via <see cref="AddParameter"/>.
    /// Invoked once when the node is constructed, so the base class can automatically
    /// serialize / deserialize every declared parameter to JSON (grouped under the
    /// <c>parameters</c> root of the logic blob). The base implementation registers the
    /// mandatory per-node fields as a <c>General</c> group: DisplayName and DependsOn are
    /// editable; Uuid, FunctionName and InstanceId are read-only.
    /// </summary>
    protected virtual void AddParam()
    {
        AddParameter(new NodeParameter("DisplayName", typeof(string), Name, isEditable: true, group: "General"));
        AddParameter(new NodeParameter("DependsOn", typeof(int), DependsOn, isEditable: true, group: "General"));
        AddParameter(new NodeParameter("Uuid", typeof(string), Uuid, isEditable: false, group: "General"));
        AddParameter(new NodeParameter("FunctionName", typeof(string), FunctionName, isEditable: false, group: "General"));
        AddParameter(new NodeParameter("InstanceId", typeof(int), InstanceId, isEditable: false, group: "General"));
    }

    /// <summary>Apply parameters (from JSON deserialization / property panel).</summary>
    public abstract void Configure(IReadOnlyDictionary<string, object?> parameters);

    /// <summary>Refresh the built-in General parameter values from the live property values.</summary>
    public void RefreshBuiltInParameters()
    {
        SetBuiltInValue("DisplayName", Name);
        SetBuiltInValue("DependsOn", DependsOn);
        SetBuiltInValue("Uuid", Uuid);
        SetBuiltInValue("FunctionName", FunctionName);
        SetBuiltInValue("InstanceId", InstanceId);
    }

    /// <summary>Push edited built-in General parameter values back onto the node properties.</summary>
    public void ApplyBuiltInParameters()
    {
        var display = BuiltInParam("DisplayName")?.Value?.ToString();
        if (display is not null) Name = display;
        if (BuiltInParam("DependsOn")?.Value is { } dep && int.TryParse(dep.ToString(), out var d))
            DependsOn = d;
    }

    private NodeParameter? BuiltInParam(string name) =>
        _nodeParameters.FirstOrDefault(p => p.Name == name);

    private void SetBuiltInValue(string name, object? value)
    {
        var npp = _nodeParameters.FirstOrDefault(x => x.Name == name);
        if (npp is not null) npp.Value = value;
    }

    /// <summary>
    /// Serialize this node's logical parameters into the given JSON object.
    /// The base implementation writes the mandatory instance identity
    /// (<see cref="Name"/>, <see cref="Uuid"/>, <see cref="FunctionName"/>, <see cref="InstanceId"/>)
    /// first, then serializes every parameter declared via <see cref="AddParam"/> /
    /// <see cref="AddParameter"/> (grouped under the <c>parameters</c> root of the logic blob).
    /// Subclasses must not need to know about UI state.
    /// </summary>
    public void SerializeParameters(JsonObject json)
    {
        json["name"] = Name;
        json["uuid"] = Uuid;
        json["functionName"] = FunctionName;
        json["instanceId"] = InstanceId;
        RefreshBuiltInParameters();
        SerializeGroupedParameters(json);
    }

    /// <summary>
    /// Restore this node's logical parameters from the given JSON object.
    /// The base implementation restores the mandatory identity fields, then
    /// deserializes every parameter declared via <see cref="AddParam"/> /
    /// <see cref="AddParameter"/> (grouped under the <c>parameters</c> root of the logic blob).
    /// </summary>
    public void DeserializeParameters(JsonObject json)
    {
        var uuid = json["uuid"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uuid))
            Uuid = uuid;
        var instanceId = json["instanceId"]?.GetValue<int>();
        if (instanceId.HasValue)
            InstanceId = instanceId.Value;
        var name = json["name"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(name))
            Name = name;
        DeserializeGroupedParameters(json);
        RefreshBuiltInParameters();
    }

    /// <summary>
    /// Write parameters that were added via <see cref="AddParameter"/> as JSON object fields,
    /// keyed by their <see cref="NodeParameter.Group"/>. All groups are placed under the
    /// <c>parameters</c> root inside the logic blob; each group field contains an array of
    /// complete <see cref="NodeParameter"/> objects so the group/name/type/value/editable state
    /// survives serialization.
    /// </summary>
    private void SerializeGroupedParameters(JsonObject json)
    {
        var parameters = json["parameters"] as JsonObject;
        if (parameters is null)
        {
            parameters = new JsonObject();
            json["parameters"] = parameters;
        }

        foreach (var group in _nodeParameters
                     .Where(p => !string.IsNullOrEmpty(p.Name))
                     .GroupBy(p => p.Group ?? string.Empty))
        {
            var arr = new JsonArray();
            foreach (var p in group)
            {
                var obj = new JsonObject
                {
                    ["name"] = p.Name,
                    ["type"] = p.Type.AssemblyQualifiedName ?? p.Type.FullName ?? p.Type.Name,
                    ["isEditable"] = p.IsEditable,
                    ["group"] = p.Group ?? string.Empty,
                    ["value"] = p.Value is null ? null : JsonSerializer.SerializeToNode(p.Value, p.Type)
                };
                arr.Add(obj);
            }
            parameters[group.Key] = arr;
        }
    }

    /// <summary>
    /// Restore parameters that were serialized by <see cref="SerializeGroupedParameters"/>.
    /// The groups are expected under the <c>parameters</c> root inside the logic blob; for
    /// compatibility, the previous flat group object/array form is also accepted.
    /// </summary>
    private void DeserializeGroupedParameters(JsonObject json)
    {
        // New format: groups live under the "parameters" root inside the logic blob.
        if (json["parameters"] is JsonObject parameters)
        {
            DeserializeParameterGroups(parameters);
            return;
        }

        // Legacy compatibility: some older files wrote groups directly at the logic root.
        DeserializeParameterGroups(json);
    }

    private void DeserializeParameterGroups(JsonObject container)
    {
        foreach (var group in container)
        {
            switch (group.Value)
            {
                case JsonArray arr:
                    foreach (var item in arr)
                    {
                        if (item is not JsonObject obj)
                            continue;

                        var name = ReadString(obj, "name", "Name");
                        if (string.IsNullOrEmpty(name))
                            continue;

                        var np = _nodeParameters.FirstOrDefault(p =>
                            p.Name == name && (p.Group ?? string.Empty) == group.Key);
                        if (np is null)
                        {
                            np = CreateNodeParameterFromJson(obj, group.Key);
                            if (np is null)
                                continue;
                            _nodeParameters.Add(np);
                        }
                        else
                        {
                            np.Group = group.Key;
                        }

                        if (ReadString(obj, "type", "Type") is string typeName &&
                            Type.GetType(typeName) is { } restoredType)
                        {
                            np.Type = restoredType;
                        }

                        var valueNode = obj["value"] ?? obj["Value"];
                        np.Value = valueNode is null
                            ? null
                            : JsonSerializer.Deserialize(valueNode.ToJsonString(), np.Type);

                        if ((obj["isEditable"] ?? obj["IsEditable"])?.GetValue<bool>() is bool editable)
                            np.IsEditable = editable;
                    }
                    break;

                case JsonObject groupObj:
                    foreach (var param in groupObj)
                    {
                        // Skip reserved keys in the legacy fallback path.
                        if (param.Key is "uuid" or "functionName" or "instanceId" or "dependsOn" or "parameters")
                            continue;

                        var np = _nodeParameters.FirstOrDefault(p =>
                            p.Name == param.Key && (p.Group ?? string.Empty) == group.Key);
                        if (np is null)
                            continue;

                        np.Value = param.Value is null
                            ? null
                            : JsonSerializer.Deserialize(param.Value.ToJsonString(), np.Type);
                    }
                    break;
            }
        }
    }

    private NodeParameter? CreateNodeParameterFromJson(JsonObject obj, string groupName)
    {
        var name = ReadString(obj, "name", "Name");
        if (string.IsNullOrEmpty(name))
            return null;

        var typeName = ReadString(obj, "type", "Type");
        var type = string.IsNullOrEmpty(typeName) ? typeof(string) : Type.GetType(typeName) ?? typeof(string);
        var valueNode = obj["value"] ?? obj["Value"];
        var value = valueNode is null ? null : JsonSerializer.Deserialize(valueNode.ToJsonString(), type);
        var isEditable = (obj["isEditable"] ?? obj["IsEditable"])?.GetValue<bool>() ?? true;

        return new NodeParameter(name, type, value, isEditable, groupName);
    }

    private static string? ReadString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj[key]?.GetValue<string>() is { } value)
                return value;
        }
        return null;
    }

    /// <summary>One-time setup before a run starts. Nodes must implement this.</summary>
    public abstract Task Initialize(INodeContext context, CancellationToken cancellationToken = default);

    /// <summary>Run body; must set <see cref="Running"/> true/false around node-specific work.</summary>
    public abstract Task Run(INodeContext context, CancellationToken cancellationToken = default);

    /// <summary>Stop body; must set <see cref="Running"/> to false.</summary>
    public abstract Task Stop(INodeContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called by an input pin when data arrives from an upstream output pin.
    /// The default implementation is a no-op for nodes that only read inputs
    /// inside <see cref="Run"/>.
    /// </summary>
    public virtual void Receive(INodeContext context, BasePin pin, object? value) { }
}