// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeContextMenu.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentFlow.Core;

namespace AgentFlow.Views;

/// <summary>
/// The node right-click menu body. Its visuals and row layout live in
/// <c>NodeContextMenu.axaml</c>; this code-behind only builds the bindable row model,
/// keeps the old pixel-exact width measurement, and dispatches the public events.
/// The transparent topmost host window is still owned by <see cref="CanvasView"/>.
/// </summary>
public sealed partial class NodeContextMenu : UserControl
{
    // ---- Layout metrics shared with NodeContextMenu.axaml ----
    private const double PadX = 12;
    private const double IconSlotWidth = 22;   // reserved left slot (icons not wired yet)
    private const double IconTextGap = 8;
    private const double MinMenuWidth = 180;
    private const double MaxMenuWidth = 280;

    private static readonly FontFamily AppFont = new(
        "avares://AgentFlow/Assets/Fonts/Inter-Regular.otf#Inter, avares://AgentFlow/Assets/Fonts/NotoSansSC-Regular.otf#Noto Sans CJK SC");

    private IReadOnlyList<NodeCommandInfo> _items = Array.Empty<NodeCommandInfo>();
    private bool _showDelete;
    private bool _showCopy;
    private Window? _hostWindow;

    /// <summary>The node commands to show, already ordered by the Core layer.</summary>
    public IReadOnlyList<NodeCommandInfo> Items
    {
        get => _items;
        set
        {
            _items = value ?? Array.Empty<NodeCommandInfo>();
            RebuildRows();
        }
    }
    /// <summary>When true, a "Delete" row is appended at the bottom after a divider.</summary>
    public bool ShowDelete
    {
        get => _showDelete;
        set
        {
            _showDelete = value;
            RebuildRows();
        }
    }

    /// <summary>When true, a "Copy" row is shown at the top of the menu.</summary>
    public bool ShowCopy
    {
        get => _showCopy;
        set
        {
            _showCopy = value;
            RebuildRows();
        }
    }

    /// <summary>
    /// The flattened visual rows (commands plus the optional trailing Delete row).
    /// The AXAML item template binds to this collection.
    /// </summary>
    public ObservableCollection<NodeContextMenuRow> Rows { get; } = new();

    /// <summary>Raised when the user picks a node command row.</summary>
    public event Action<NodeCommandInfo>? CommandSelected;

    /// <summary>Raised when the user picks the Copy row.</summary>
    public event Action? CopySelected;
    /// <summary>Raised when the user picks the Delete row.</summary>
    public event Action? DeleteSelected;

    /// <summary>Raised when the menu wants to close itself (e.g. Esc, or after a row was picked).</summary>
    public event Action? CloseRequested;

    public NodeContextMenu()
    {
        InitializeComponent();

        // This menu is self-bound: the AXAML ItemsControl reads Rows from this control.
        DataContext = this;

        RebuildRows();
    }

    private void RebuildRows()
    {
        var rows = new List<NodeContextMenuRow>();

        NodeContextMenuRow? previous = null;
        if (_showCopy)
        {
            previous = new NodeContextMenuRow(
                command: null, isDelete: false, isEnabled: true,
                "Copy", showDivider: false, isCopy: true);
            rows.Add(previous);
        }
        foreach (var command in _items)
        {
            bool showDivider = previous is not null
                && !string.IsNullOrEmpty(command.Group)
                && command.Group != previous.Group;

            var row = new NodeContextMenuRow(
                command, isDelete: false, isEnabled: command.IsEnabled,
                command.Title, showDivider);
            rows.Add(row);
            previous = row;
        }

        if (_showDelete)
        {
            rows.Add(new NodeContextMenuRow(
                command: null, isDelete: true, isEnabled: true,
                "Delete", showDivider: previous is not null));
        }

        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);

        // Keep the previous FormattedText-based width so text is measured with
        // exactly the same font and metrics as the old DrawingContext renderer.
        Width = MeasureMenuWidth(rows);
    }

    private static double MeasureMenuWidth(IReadOnlyList<NodeContextMenuRow> rows)
    {
        double MaxTextWidth(string text) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(AppFont), 12, Brushes.Black).Width;

        double max = 0;
        foreach (var row in rows) max = Math.Max(max, MaxTextWidth(row.Title));

        double width = PadX * 2 + IconSlotWidth + IconTextGap + max;
        return Math.Clamp(width, MinMenuWidth, MaxMenuWidth);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The host Window owns focus, not this UserControl, so listen on the Window for
        // Esc. This keeps the old "Esc closes the menu" behavior even though the row body
        // no longer takes keyboard focus.
        DetachHostWindow();
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _hostWindow = window;
            window.KeyDown += OnHostKeyDown;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachHostWindow();
        base.OnDetachedFromVisualTree(e);
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null) return;
        _hostWindow.KeyDown -= OnHostKeyDown;
        _hostWindow = null;
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseRequested?.Invoke();
        e.Handled = true;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not NodeContextMenuRow row)
        {
            return;
        }

        e.Handled = true;
        if (!row.IsEnabled) return;

        // Close the menu FIRST, and defer the actual action to the next dispatcher frame.
        // This guarantees the host window is fully dismissed before the command runs —
        // otherwise a confirmation dialog (or any other modal the command shows) pops up
        // while this topmost menu is still on screen, floating above the dialog.
        CloseRequested?.Invoke();

        Dispatcher.UIThread.Post(
            () =>
            {
                if (row.IsCopy)
                {
                    CopySelected?.Invoke();
                }
                else if (row.IsDelete)
                {
                    DeleteSelected?.Invoke();
                }
                else if (row.Command is not null)
                {
                    CommandSelected?.Invoke(row.Command);
                }
            },
            DispatcherPriority.Input);
    }

}

/// <summary>
/// One bindable visual row in <see cref="NodeContextMenu"/>. Commands and the optional
/// trailing Delete row are flattened into this shape so the AXAML template only has to
/// render a title, a foreground brush, and an optional divider.
/// </summary>
public sealed class NodeContextMenuRow
{
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#0F172A"));
    private static readonly IBrush TextDisabledBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush TextDangerBrush = new SolidColorBrush(Color.Parse("#B91C1C"));

    public NodeContextMenuRow(
        NodeCommandInfo? command,
        bool isDelete,
        bool isEnabled,
        string title,
        bool showDivider,
        bool isCopy = false)
    {
        Command = command;
        IsCopy = isCopy;
        IsDelete = isDelete;
        IsEnabled = isEnabled;
        Title = title;
        Group = command?.Group;
        ShowDivider = showDivider;
        Foreground = !isEnabled
            ? TextDisabledBrush
            : isDelete
                ? TextDangerBrush
                : TextBrush;
    }

    /// <summary>The command executed by this row, or null for the Delete row.</summary>
    public NodeCommandInfo? Command { get; }

    /// <summary>True for the built-in Copy row.</summary>
    public bool IsCopy { get; }
    /// <summary>True for the appended Delete row.</summary>
    public bool IsDelete { get; }

    /// <summary>False rows are display-only and do not react to the pointer.</summary>
    public bool IsEnabled { get; }

    /// <summary>The row label.</summary>
    public string Title { get; }

    /// <summary>The command group, used to decide where separators go.</summary>
    public string? Group { get; }

    /// <summary>True when this row is the first row of a new command group or the Delete row.</summary>
    public bool ShowDivider { get; }

    /// <summary>Enabled/disabled/danger text color for the label.</summary>
    public IBrush Foreground { get; }
}
