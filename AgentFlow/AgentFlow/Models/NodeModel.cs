// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Models;

/// <summary>
/// Node domain model: wraps the contract-layer <see cref="BaseNode"/> and exposes
/// non-visual node data (type ID / display name / category / instance ID / input-output
/// pins / parameters) to the UI in a read-only fashion. Pure UI concerns (canvas
/// position, selection state, z-order) stay in the View / ViewModel layer.
/// </summary>
public sealed class NodeModel
{
    /// <summary>The underlying contract node instance.</summary>
    public BaseNode Node { get; }

    public string TypeId => Node.TypeId;
    public string DisplayName => Node.DisplayName;
    public string Category => Node.Category;
    public int InstanceId => Node.InstanceId;

    public IReadOnlyList<PinModel> Inputs { get; }
    public IReadOnlyList<PinModel> Outputs { get; }
    public IReadOnlyList<ParameterDefinition> Parameters { get; }

    public NodeModel(BaseNode node)
    {
        Node = node;
        Inputs = node.InputPins.Select(p => new PinModel(p)).ToList();
        Outputs = node.OutputPins.Select(p => new PinModel(p)).ToList();
        Parameters = node.Parameters;
    }
}
