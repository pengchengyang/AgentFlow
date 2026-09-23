// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CanvasViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using Avalonia;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// View-model for the canvas. Owns the canvas view transform (zoom/pan) and the
/// interaction state/operations (drag, connect, pan, selection, delete). Model mutations are
/// delegated to <see cref="MainViewModel"/>. 1 view &lt;-&gt; 1 view-model.
/// </summary>
public partial class CanvasViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    // ---- View transform (zoom / pan) ----
    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    // ---- Interaction state ----
    private bool _draggingNode;
    private NodeViewModel? _dragNode;
    private Point _dragOffset;

    private bool _connecting;
    private PinViewModel? _connectSource;
    private Point _pointer;

    private bool _panning;
    private Point _panStartPointer;
    private double _panStartX;
    private double _panStartY;

    public CanvasViewModel(MainViewModel main)
    {
        _main = main;
    }

    /// <summary>Formatted zoom percentage shown in the status bar (e.g. &quot;100%&quot;).</summary>
    public string ZoomText => $"{Zoom * 100:0}%";

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    // ---- Data exposed for rendering ----
    public ObservableCollection<NodeViewModel> Nodes => _main.Nodes;
    public ObservableCollection<ConnectionViewModel> Connections => _main.Connections;
    public bool IsConnecting => _connecting;
    public PinViewModel? ConnectSource => _connectSource;
    public Point Pointer => _pointer;
    public bool IsDragging => _draggingNode;
    public bool IsPanning => _panning;

    // ---- Zoom / pan ----
    public void ZoomAt(double cursorX, double cursorY, double factor)
    {
        double newZoom = Math.Clamp(Zoom * factor, 0.2, 5.0);
        double wX = (cursorX - PanX) / Zoom;
        double wY = (cursorY - PanY) / Zoom;
        Zoom = newZoom;
        PanX = cursorX - wX * Zoom;
        PanY = cursorY - wY * Zoom;
    }

    public void ResetZoom()
    {
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
    }

    // ---- Selection & editing (delegate to main) ----
    public void SelectOnly(NodeViewModel? node) => _main.SelectOnly(node);
    public void ToggleSelection(NodeViewModel node) => _main.ToggleSelection(node);
    public void BringToFront(NodeViewModel node) => _main.BringToFront(node);
    public void DeleteSelection() => _main.DeleteSelection();
    public void DisconnectInputPin(PinViewModel input) => _main.DisconnectInputPin(input);
    public void RemoveConnection(ConnectionViewModel connection) => _main.RemoveConnection(connection);
    public void OpenNodeParameters(NodeViewModel node) => _main.OpenNodeParametersCommand.Execute(node);
    public ICommand SendNodeCommand => _main.SendNodeCommand;
    public ICommand RemoveNodeCommand => _main.RemoveNodeCommand;

    // ---- Panning (middle mouse drag) ----
    public void BeginPan(Point pointer)
    {
        _panning = true;
        _panStartPointer = pointer;
        _panStartX = PanX;
        _panStartY = PanY;
    }

    public void UpdatePan(Point screen)
    {
        PanX = _panStartX + (screen.X - _panStartPointer.X);
        PanY = _panStartY + (screen.Y - _panStartPointer.Y);
    }

    public void EndPan() => _panning = false;

    // ---- Connecting (output pin drag) ----
    public void BeginConnect(PinViewModel outPin, Point pointer)
    {
        _connecting = true;
        _connectSource = outPin;
        _pointer = pointer;
        SelectOnly(outPin.Node);
    }

    public void UpdateConnect(Point pointer) => _pointer = pointer;

    public void EndConnect(PinViewModel? target)
    {
        if (target is not null && _connectSource is not null && !ReferenceEquals(target, _connectSource))
            _main.TryCreateConnection(_connectSource, target);
        _connecting = false;
        _connectSource = null;
    }

    public void CancelConnect()
    {
        _connecting = false;
        _connectSource = null;
    }

    // ---- Node dragging ----
    public void BeginDrag(NodeViewModel node, Point pointer)
    {
        _draggingNode = true;
        _dragNode = node;
        _dragOffset = pointer - node.Location;
    }

    public void UpdateDrag(Point pointer)
    {
        if (_dragNode is not null)
            _dragNode.Location = pointer - _dragOffset;
    }

    public void EndDrag()
    {
        _draggingNode = false;
        _dragNode = null;
    }
}
