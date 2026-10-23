// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeContextMenu.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AgentFlow.Core;

namespace AgentFlow.Views;

/// <summary>
/// A fully self-drawn right-click menu for canvas nodes. It is hosted inside an Avalonia
/// <see cref="Popup"/> (which handles screen-edge placement, click-outside-to-close and Esc),
/// but every pixel of the menu body — card, shadows, items, hover, dividers — is drawn here
/// with <see cref="DrawingContext"/>, so the visual language matches the self-drawn
/// <see cref="GraphCanvas"/> node cards instead of the native Fluent menu look.
/// </summary>
public sealed class NodeContextMenu : Control
{
    // ---- Layout metrics (mirror the compact node-card design language) ----
    private const double ItemHeight = 32;
    private const double PadY = 6;
    private const double PadX = 12;
    private const double IconSlotWidth = 22;   // reserved left slot (icon rendering not wired yet)
    private const double IconTextGap = 8;
    private const double MinMenuWidth = 180;
    private const double MaxMenuWidth = 280;
    private const double CornerRadius = 10;
    private const double DividerPaddingX = 10;

    // ---- Palette (same tokens as GraphCanvas) ----
    private static readonly Color ColorCardBg = Color.Parse("#FFFFFF");
    private static readonly Color ColorBorder = Color.Parse("#E2E8F0");
    private static readonly Color ColorText = Color.Parse("#0F172A");
    private static readonly Color ColorTextDisabled = Color.Parse("#94A3B8");
    private static readonly Color ColorTextDanger = Color.Parse("#B91C1C");
    private static readonly Color ColorHoverBg = Color.Parse("#EDE9FE");     // light violet, echoes selected ring
    private static readonly Color ColorDivider = Color.Parse("#F1F2F5");

    private static readonly FontFamily AppFont = new(
        "avares://AgentFlow/Assets/Fonts/Inter-Regular.otf#Inter, avares://AgentFlow/Assets/Fonts/NotoSansSC-Regular.otf#Noto Sans CJK SC");

    private IReadOnlyList<NodeCommandInfo> _items = Array.Empty<NodeCommandInfo>();
    private bool _showDelete;
    private int _hoverIndex = -1;   // index into the flattened visual row list, -1 = none

    /// <summary>The node commands to show, already ordered by the Core layer.</summary>
    public IReadOnlyList<NodeCommandInfo> Items
    {
        get => _items;
        set { _items = value ?? Array.Empty<NodeCommandInfo>(); InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>When true, a "Delete" row is appended at the bottom after a divider.</summary>
    public bool ShowDelete
    {
        get => _showDelete;
        set { _showDelete = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Raised when the user picks a node command row.</summary>
    public event Action<NodeCommandInfo>? CommandSelected;

    /// <summary>Raised when the user picks the Delete row.</summary>
    public event Action? DeleteSelected;

    /// <summary>Raised when the menu wants to close itself (e.g. Esc, or after a row was picked).</summary>
    public event Action? CloseRequested;

    // ---- Visual row model: flatten commands + optional trailing Delete into one list ----
    private sealed class Row
    {
        public NodeCommandInfo? Command;   // null => Delete row
        public bool IsDelete;
        public bool IsEnabled = true;
        public string Title = "";
        public string? Group;
    }

    private List<Row> BuildRows()
    {
        var rows = new List<Row>();
        foreach (var c in _items)
        {
            rows.Add(new Row
            {
                Command = c,
                IsEnabled = c.IsEnabled,
                Title = c.Title,
                Group = c.Group,
            });
        }
        if (_showDelete)
        {
            rows.Add(new Row { IsDelete = true, IsEnabled = true, Title = "Delete" });
        }
        return rows;
    }

    private double MeasureMenuWidth()
    {
        double max = 0;
        var ft = new FormattedText("Ag", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(AppFont), 12, Brushes.Black);
        double Measure(string s) =>
            new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(AppFont), 12, Brushes.Black).Width;

        foreach (var r in BuildRows()) max = Math.Max(max, Measure(r.Title));
        double w = PadX * 2 + IconSlotWidth + IconTextGap + max;
        return Math.Clamp(w, MinMenuWidth, MaxMenuWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = BuildRows();
        double w = MeasureMenuWidth();
        double h = PadY * 2 + rows.Count * ItemHeight;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        var rows = BuildRows();
        if (rows.Count == 0) return;

        // Card body: pure rounded rect, no shadow (shadow would be clipped by the popup's
        // rectangular window and leak as a hard corner).
        ctx.DrawRectangle(new SolidColorBrush(ColorCardBg),
            new Pen(new SolidColorBrush(ColorBorder), 1),
            new RoundedRect(bounds, CornerRadius, CornerRadius));

        double y = PadY;
        Row? prev = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (prev is not null && !string.IsNullOrEmpty(row.Group) && row.Group != prev.Group)
            {
                ctx.DrawRectangle(new SolidColorBrush(ColorDivider), null,
                    new Rect(DividerPaddingX, y - 0.5, bounds.Width - DividerPaddingX * 2, 1));
            }
            if (row.IsDelete && prev is not null)
            {
                ctx.DrawRectangle(new SolidColorBrush(ColorDivider), null,
                    new Rect(DividerPaddingX, y - 0.5, bounds.Width - DividerPaddingX * 2, 1));
            }

            var rowRect = new Rect(0, y, bounds.Width, ItemHeight);
            if (i == _hoverIndex && row.IsEnabled)
            {
                ctx.DrawRectangle(new SolidColorBrush(ColorHoverBg), null, rowRect);
            }

            IBrush fg = !row.IsEnabled
                ? new SolidColorBrush(ColorTextDisabled)
                : row.IsDelete
                    ? new SolidColorBrush(ColorTextDanger)
                    : new SolidColorBrush(ColorText);
            var ft = new FormattedText(row.Title, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(AppFont, FontStyle.Normal, FontWeight.Normal),
                12, fg);
            double textX = PadX + IconSlotWidth + IconTextGap;
            double textY = y + (ItemHeight - ft.Height) / 2;
            ctx.DrawText(ft, new Point(textX, textY));

            y += ItemHeight;
            prev = row;
        }
    }

    // ================= Interaction =================

    private int RowIndexAt(Point p)
    {
        var rows = BuildRows();
        double y = PadY;
        for (int i = 0; i < rows.Count; i++)
        {
            if (p.Y >= y && p.Y < y + ItemHeight) return i;
            y += ItemHeight;
        }
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetCurrentPoint(this).Position;
        var rows = BuildRows();
        int idx = RowIndexAt(p);
        // Disabled rows don't highlight.
        if (idx >= 0 && !rows[idx].IsEnabled) idx = -1;
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this).Position;
        var rows = BuildRows();
        int idx = RowIndexAt(p);
        e.Handled = true;

        if (idx < 0 || !rows[idx].IsEnabled) return;

        var row = rows[idx];

        // Close the menu FIRST, and defer the actual action to the next dispatcher frame.
        // This guarantees the popup window is fully dismissed before the command runs —
        // otherwise a confirmation dialog (or any other modal the command shows) pops up
        // while this topmost popup is still on screen, floating above the dialog.
        CloseRequested?.Invoke();

        Dispatcher.UIThread.Post(() =>
        {
            if (row.IsDelete)
            {
                DeleteSelected?.Invoke();
            }
            else if (row.Command is not null)
            {
                CommandSelected?.Invoke(row.Command);
            }
        }, DispatcherPriority.Input);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
        }
    }
}
