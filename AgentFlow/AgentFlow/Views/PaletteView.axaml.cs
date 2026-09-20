using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class PaletteView : UserControl
{
    private const double DragThreshold = 4.0;
    private PointerPressedEventArgs? _pendingDrag;

    public PaletteView()
    {
        InitializeComponent();
    }

    /// <summary>Remember the press so a real drag can be told apart from a plain click.</summary>
    private void OnPaletteItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pendingDrag = e;
    }

    /// <summary>
    /// Once the pointer has travelled far enough, hand the palette item to the platform
    /// drag-and-drop system (Avalonia 12 uses the async DataTransfer API).
    /// </summary>
    private void OnPaletteItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingDrag is null) return;

        var origin = _pendingDrag.GetPosition((Visual)sender!);
        var current = e.GetPosition((Visual)sender!);
        var delta = current - origin;
        if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < DragThreshold)
            return;

        // Consume the press so the underlying Button doesn't also fire a click-to-add.
        var press = _pendingDrag;
        _pendingDrag = null;

        if (sender is Control { DataContext: PaletteItem item })
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DragFormats.PaletteItem, item));
            _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
        }
    }

    private void OnPaletteItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pendingDrag = null;
    }
}
