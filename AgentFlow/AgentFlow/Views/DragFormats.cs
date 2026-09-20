using Avalonia.Input;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// Shared drag-and-drop data formats. Both the palette (drag source) and the canvas
/// (drop target) must use the very same <see cref="DataFormat{T}"/> instance so the
/// in-process format equality check succeeds.
/// </summary>
internal static class DragFormats
{
    public static readonly DataFormat<PaletteItem> PaletteItem =
        DataFormat.CreateInProcessFormat<PaletteItem>("AgentFlow.PaletteItem");
}
