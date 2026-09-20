using Avalonia.Controls;
using Avalonia.Input;
using AgentFlow.ViewModels;
using Nodify.Avalonia;

namespace AgentFlow.Views;

public partial class CanvasView : UserControl
{
    private NodifyEditor _editor = null!;

    public CanvasView()
    {
        InitializeComponent();
        _editor = this.FindControl<NodifyEditor>("Editor")!;
    }

    /// <summary>
    /// Handles a palette item dropped onto the canvas: converts the pointer to graph-space
    /// coordinates and asks the view model to create the node there.
    /// </summary>
    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is null)
            return;
        var item = e.DataTransfer.TryGetValue(DragFormats.PaletteItem);
        if (item is null)
            return;
        if (DataContext is not MainViewModel vm)
            return;

        var graphLocation = _editor.GetLocationInsideEditor(e);
        vm.AddNodeAt(item, graphLocation);
        e.Handled = true;
    }
}

