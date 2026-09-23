// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PinModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Models;

/// <summary>
/// Pin domain model: wraps the contract-layer <see cref="BasePin"/> and exposes
/// non-visual pin data (name / data type / direction / required) to the UI read-only.
/// All non-UI elements come from <see cref="BasePin"/>.
/// </summary>
public sealed class PinModel
{
    /// <summary>The underlying contract definition (including Send/Receive hooks and other non-UI logic).</summary>
    public BasePin basePin { get; }

    public string Name => basePin.Name;
    public Type DataType => basePin.DataType;
    public PinDirection Direction => basePin.Direction;
    public bool Required => basePin.Required;

    public bool IsInput => Direction == PinDirection.Input;
    public bool IsOutput => Direction == PinDirection.Output;

    public PinModel(BasePin definition)
    {
        basePin = definition;
    }
}
