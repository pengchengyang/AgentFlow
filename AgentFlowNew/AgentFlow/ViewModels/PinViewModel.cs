// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="PinViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Models;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// Pin ViewModel: a pure presentation layer responsible for drawing a pin dot
/// and its name on the canvas. It does not maintain connections, hold references
/// to the opposite pin, or implement Send/Receive — all connect / disconnect / data
/// transfer is managed by <see cref="AgentFlow.Core.EditorGraph"/>. Non-visual data
/// (name / type / direction) comes from <see cref="PinModel"/> (which wraps the
/// contract-layer <see cref="AgentFlow.Contracts.BasePin"/>).
/// </summary>
public partial class PinViewModel : ViewModelBase
{
    /// <summary>The owning node (GUI ViewModel).</summary>
    public NodeViewModel Node { get; }

    /// <summary>The domain model (wraps the contract-layer BasePin).</summary>
    public PinModel Pin { get; }

    public string Name => Pin.Name;
    public Type DataType => Pin.DataType;
    public string TypeName => Pin.DataType.Name;
    public bool Required => Pin.Required;
    public bool IsInput => Pin.IsInput;
    public bool IsOutput => Pin.IsOutput;

    /// <summary>Canvas anchor position (written by CanvasView during layout).</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>Whether the pin is connected (updated by the Core EditorGraph on connect / disconnect).</summary>
    [ObservableProperty]
    private bool _isConnected;

    public PinViewModel(NodeViewModel node, PinModel pin)
    {
        Node = node;
        Pin = pin;
    }
}
