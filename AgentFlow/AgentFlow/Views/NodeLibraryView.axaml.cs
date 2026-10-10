// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeLibraryView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AgentFlow.ViewModels;
using AgentFlow.Services;
namespace AgentFlow.Views;

/// <summary>
/// Right-side node library: double-click a node to add it, or press-and-drag it onto the canvas.
/// Shares <see cref="DragFormats.PaletteItem"/> with <see cref="CanvasView"/>.
/// </summary>
public partial class NodeLibraryView : UserControl
{
    private const double DragThreshold = 4.0;
    private PointerPressedEventArgs? _pendingPress;
    private PaletteItem? _pendingItem;

    public NodeLibraryView()
    {
        InitializeComponent();
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PaletteItem item })
        {
            _pendingPress = e;
            _pendingItem = item;
        }
    }

    private void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingPress is null || _pendingItem is null)
            return;

        var origin = _pendingPress.GetPosition((Visual)sender!);
        var current = e.GetPosition((Visual)sender!);
        var delta = current - origin;
        if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < DragThreshold)
            return;
        var press = _pendingPress;
        var item = _pendingItem;
        _pendingPress = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DragFormats.PaletteItem, item));
        _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Release only clears pending state; node creation now happens on double-click.
        _pendingPress = null;
        _pendingItem = null;
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PaletteItem item } && DataContext is NodeLibraryViewModel vm)
            vm.AddNodeCommand.Execute(item);

        _pendingPress = null;
        _pendingItem = null;
    }
}

