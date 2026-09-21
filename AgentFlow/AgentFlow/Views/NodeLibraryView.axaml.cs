using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// Right-side node library: click a node to add it, or press-and-drag it onto the canvas.
/// Shares <see cref="DragFormats.PaletteItem"/> with <see cref="CanvasView"/>.
/// </summary>
public partial class NodeLibraryView : UserControl
{
    private const double DragThreshold = 4.0;
    private PointerPressedEventArgs? _pendingPress;
    private PaletteItem? _pendingItem;
    private bool _dragStarted;

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
            _dragStarted = false;
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

        _dragStarted = true;
        var press = _pendingPress;
        var item = _pendingItem;
        _pendingPress = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DragFormats.PaletteItem, item));
        _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A plain click (no drag) adds the node at the default location.
        if (!_dragStarted && _pendingItem is not null && DataContext is MainViewModel vm)
            vm.AddNodeCommand.Execute(_pendingItem);

        _pendingPress = null;
        _pendingItem = null;
        _dragStarted = false;
    }
}
