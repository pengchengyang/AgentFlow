// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="DragFormats.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Input;
using AgentFlow.ViewModels;

namespace AgentFlow.Services;

/// <summary>
/// Shared drag-and-drop data formats. The palette (drag source) uses the very same
/// <see cref="DataFormat{T}"/> instance that a future drop target would, so the
/// in-process format equality check succeeds.
/// </summary>
internal static class DragFormats
{
    public static readonly DataFormat<PaletteItem> PaletteItem =
        DataFormat.CreateInProcessFormat<PaletteItem>("AgentFlow.PaletteItem");
}
