// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CanvasView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AgentFlow.Services;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class CanvasView : UserControl
{
    public CanvasView()
    {
        InitializeComponent();
    }
}

/// <summary>
/// A pure Avalonia Canvas: node cards, pin dots and Bezier curves are all drawn
/// by <see cref="Render"/> through <see cref="DrawingContext"/>. This is a template-less
/// <see cref="Control"/>, so the self-drawn content is never overridden by a template.
/// This view only handles: layout calculation, self-drawn rendering, hit-testing,
/// and forwarding raw pointer / keyboard input to the <see cref="CanvasViewModel"/>.
/// All business logic (selection, drag, wiring, pan, zoom, delete) lives in the ViewModel.
/// </summary>
public sealed class GraphCanvas : Control
{
    // ---- Layout constants (design: compact 240px card with a single title row) ----
    private const double NodeWidth = 240;
    private const double PadX = 14;
    private const double HeaderHeight = 36;   // title row only: title + "#instance" on the right
    private const double InputRow = 28;
    private const double OutputRow = 28;

    // ---- Theme colors: resolved once from Colors.axaml resources (fall back to defaults) ----
    private static Color R(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key, out var v) == true && v is Color c)
            return c;
        return fallback;
    }

    private static readonly Color ColorBg = R("CanvasBg", Color.Parse("#F6F8FB"));
    private static readonly Color ColorTitle = R("NodeTitle", Color.Parse("#0F172A"));
    private static readonly Color ColorName = R("NodeName", Color.Parse("#64748B"));
    private static readonly Color ColorMuted = R("NodeMuted", Color.Parse("#94A3B8"));
    private static readonly Color ColorBorder = R("NodeBorder", Color.Parse("#E2E8F0"));
    private static readonly Color ColorDivider = R("NodeDivider", Color.Parse("#F1F2F5"));
    private static readonly Color ColorSelected = R("NodeSelected", Color.Parse("#7C3AED"));
    private static readonly Color ColorLine = R("ConnLine", Color.Parse("#A8B3C2"));
    private static readonly Color ColorIn = R("PinInput", Color.Parse("#0EA5E9"));
    private static readonly Color ColorOut = R("PinOutput", Color.Parse("#8B5CF6"));
    private static readonly Color ColorPinBorder = R("PinBorder", Color.Parse("#CBD5E1"));
    private static readonly Color ColorShadow = R("NodeShadow", Color.Parse("#33243266"));
    private static readonly Color ColorGlow = Color.Parse("#EDE9FE");
    private static readonly Color ColorSelectedBorder = Color.Parse("#8B5CF6");


    // App font stack (Inter for Latin, Noto CJK fallback) — mirrors the global App.axaml font so the
    // self-drawn canvas text matches the rest of the UI instead of using FontFamily.Default.
    private static readonly FontFamily AppFont = new("avares://AgentFlow/Assets/Fonts/Inter-Regular.otf#Inter, avares://AgentFlow/Assets/Fonts/NotoSansSC-Regular.otf#Noto Sans CJK SC");
    private static readonly StreamGeometry StarGeometry = StreamGeometry.Parse(
        "M316.9 18C311.6 7 300.4 0 288.1 0s-23.4 7-28.8 18L195 150.3 51.4 171.5c-12 1.8-22 10.2-25.7 21.7s-.7 24.2 7.9 32.7L137.8 329 113.2 474.7c-2 12 3 24.2 12.9 31.3s23 8 33.8 2.3l128.3-68.5 128.3 68.5c10.8 5.7 23.9 4.9 33.8-2.3s14.9-19.3 12.9-31.3L438.5 329 542.7 225.9c8.6-8.5 11.7-21.2 7.9-32.7s-13.7-19.9-25.7-21.7L381.2 150.3 316.9 18z");

    private readonly Dictionary<NodeViewModel, NodeLayout> _layouts = new();

    public GraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnCanvasDragOver);
        DragDrop.AddDropHandler(this, OnCanvasDrop);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CanvasViewModel vm)
            {
                vm.Nodes.CollectionChanged += OnNodesChanged;
                vm.Connections.CollectionChanged += (_, _) => InvalidateVisual();
                foreach (var n in vm.Nodes) n.PropertyChanged += OnNodePropertyChanged;
                vm.PropertyChanged += OnVmPropertyChanged;
            }
        };
    }

    private CanvasViewModel? Vm => DataContext as CanvasViewModel;

    // ================= Layout =================

    private sealed class NodeLayout
    {
        public required NodeViewModel Node { get; init; }
        public double Height;
        public double HeaderBottom;
        public Rect Body = default;
        public List<Rect> InputHits = new();
        public List<Rect> OutputHits = new();

    }

    /// <summary>Header height (relative to node top). The design card shows only the title row.</summary>
    private static double ComputeHeaderBottom(NodeViewModel node) => HeaderHeight;

    private NodeLayout ComputeLayout(NodeViewModel node)
    {
        var loc = node.Location;
        var l = new NodeLayout { Node = node };

        int nIn = node.Inputs.Count;
        int nOut = node.Outputs.Count;
        int rows = Math.Max(nIn, nOut);
        double topPad = 4;
        double metaRows = 2;   // Name + DependsOn lines between header and pin rows
        double metaH = 18;
        double content = rows * InputRow;
        double headerBottom = ComputeHeaderBottom(node);
        l.HeaderBottom = headerBottom;
        double bodyStart = headerBottom + topPad + metaRows * metaH;

        // Left inputs: vertically centered within the body span based on input count.
        if (nIn > 0)
        {
            double start = bodyStart + (content - nIn * InputRow) / 2;
            for (int i = 0; i < nIn; i++)
            {
                var pin = node.Inputs[i];
                pin.Anchor = new Point(loc.X, loc.Y + start + i * InputRow + InputRow / 2);
                l.InputHits.Add(new Rect(loc.X - 8, loc.Y + start + i * InputRow, 64, InputRow));
            }
        }

        // Right outputs: vertically centered within the body span based on output count.
        if (nOut > 0)
        {
            double start = bodyStart + (content - nOut * InputRow) / 2;
            for (int i = 0; i < nOut; i++)
            {
                var pin = node.Outputs[i];
                pin.Anchor = new Point(loc.X + NodeWidth, loc.Y + start + i * InputRow + InputRow / 2);
                l.OutputHits.Add(new Rect(loc.X + NodeWidth - 56, loc.Y + start + i * InputRow, 64, InputRow));
            }
        }

        double y = bodyStart + content + 10;
        l.Height = y;
        l.Body = new Rect(loc.X, loc.Y, NodeWidth, y);
        _layouts[node] = l;
        return l;
    }
    private void RefreshLayouts()
    {
        var vm = Vm;
        if (vm is null) return;
        _layouts.Clear();
        foreach (var n in vm.Nodes) ComputeLayout(n);
    }

    // ================= Render =================

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.DrawRectangle(new SolidColorBrush(ColorBg), null, new Rect(0, 0, Bounds.Width, Bounds.Height));
        var vm = Vm;
        if (vm is null) return;

        using (ctx.PushTransform(ViewTransform()))
        {
            RefreshLayouts();

            foreach (var conn in vm.Connections)
                DrawBezier(ctx, conn.Source.Anchor, conn.Target.Anchor, conn.IsSelected);

            if (vm.IsConnecting && vm.ConnectSource is not null)
                DrawBezier(ctx, vm.ConnectSource.Anchor, vm.Pointer, false, dashed: true,
                    accent: vm.ConnectSource.Node.Accent.Color);

            foreach (var n in BottomFirstNodes(vm))
                DrawNode(ctx, n);
        }
    }

    private Matrix ViewTransform()
    {
        var vm = Vm;
        if (vm is null) return Matrix.Identity;
        return Matrix.CreateScale(new Vector(vm.Zoom, vm.Zoom))
            * Matrix.CreateTranslation(new Vector(vm.PanX, vm.PanY));
    }

    private Point ScreenToWorld(Point p)
    {
        var vm = Vm;
        if (vm is null) return p;
        return new Point((p.X - vm.PanX) / vm.Zoom, (p.Y - vm.PanY) / vm.Zoom);
    }

    private void DrawNode(DrawingContext ctx, NodeViewModel node)
    {
        var l = _layouts[node];
        var b = l.Body;

        const double radius = 16;

        // Soft, diffused drop shadow (airy design: two offset layers).
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x0A, 0x0F, 0x17, 0x2A)), null,
            new RoundedRect(new Rect(b.X, b.Y + 6, b.Width, b.Height), radius, radius));
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0x0F, 0x17, 0x2A)), null,
            new RoundedRect(new Rect(b.X, b.Y + 3, b.Width, b.Height), radius, radius));

        // Selected glow: soft amber halo behind the card (ResetBg #FEF3C7).
        if (node.IsSelected)
        {
            var glow = new Rect(b.X - 4, b.Y - 4, b.Width + 8, b.Height + 8);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x3D, ColorGlow.R, ColorGlow.G, ColorGlow.B)),
                null, new RoundedRect(glow, radius + 4, radius + 4));
        }

        // White card: thin gray border normally, purple ring when selected.
        ctx.DrawRectangle(Brushes.White,
            new Pen(new SolidColorBrush(node.IsSelected ? ColorSelectedBorder : ColorBorder), node.IsSelected ? 1.4 : 1.0),
            new RoundedRect(b, radius, radius));

        double x = b.X, y = b.Y;

        // Header tint band: category color at low opacity, only the top corners rounded.
        var hgeo = new StreamGeometry();
        using (var g = hgeo.Open())
        {
            g.BeginFigure(new Point(x, y + HeaderHeight), true);
            g.LineTo(new Point(x, y + radius));
            g.ArcTo(new Point(x + radius, y), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(x + NodeWidth - radius, y));
            g.ArcTo(new Point(x + NodeWidth, y + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(x + NodeWidth, y + HeaderHeight));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(node.AccentTint, null, hgeo);

        // Circular badge: solid accent color + white initial letter of the node Name.
        const double badgeR = 11;
        var badgeCenter = new Point(x + PadX + badgeR, y + HeaderHeight / 2);
        ctx.DrawEllipse(node.Accent, null, badgeCenter, badgeR, badgeR);
        string letter = FirstLetter(node.Name);
        double lw = TextWidth(letter, 11, FontWeight.Bold);
        DrawText(ctx, letter, new Point(badgeCenter.X - lw / 2, badgeCenter.Y - 7),
            Brushes.White, 11, FontWeight.Bold);

        // Title (right of the badge) = DisplayName (fallback to Name); "#instance" tag on the right.
        DrawText(ctx, node.Title, new Point(badgeCenter.X + badgeR + 8, y + 12.5),
            new SolidColorBrush(ColorTitle), 11, FontWeight.SemiBold);
        var idText = $"#{node.InstanceId}";
        double idW = TextWidth(idText, 9);
        DrawText(ctx, idText, new Point(x + NodeWidth - PadX - idW, y + 15),
            new SolidColorBrush(ColorMuted), 9);

        // Meta rows below the header tint: Name (type id) and DependsOn.
        DrawText(ctx, node.Name, new Point(x + PadX, y + HeaderHeight + 6),
            new SolidColorBrush(ColorName), 10);
        DrawText(ctx, $"DependsOn: {node.DependsOn}", new Point(x + PadX, y + HeaderHeight + 24),
            new SolidColorBrush(ColorMuted), 10);

        // Pin rows: hollow / filled dot in the node's category color, straight on the card edge.
        for (int i = 0; i < node.Inputs.Count; i++)
        {
            var pin = node.Inputs[i];
            DrawPinDot(ctx, pin.Anchor, pin.IsConnected, node.Accent.Color);
            DrawText(ctx, pin.Name, new Point(x + 14, pin.Anchor.Y - 6), new SolidColorBrush(ColorName), 10);
        }

        for (int i = 0; i < node.Outputs.Count; i++)
        {
            var pin = node.Outputs[i];
            DrawPinDot(ctx, pin.Anchor, pin.IsConnected, node.Accent.Color);
            double w = TextWidth(pin.Name, 10);
            DrawText(ctx, pin.Name, new Point(pin.Anchor.X - w - 12, pin.Anchor.Y - 6), new SolidColorBrush(ColorName), 10);
        }
    }

    /// <summary>First alphabetic letter of the node title, uppercased (for the circular header badge).</summary>
    private static string FirstLetter(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "?";
        foreach (var ch in title)
            if (char.IsLetter(ch)) return char.ToUpperInvariant(ch).ToString();
        return "?";
    }

    /// <summary>Linear-interpolate <paramref name="c"/> toward <paramref name="target"/> by <paramref name="t"/> (0..1).</summary>
    private static Color Mix(Color c, Color target, double t)
    {
        double Inv(double a, double b) => a + (b - a) * t;
        return Color.FromArgb(
            (byte)Inv(c.A, target.A),
            (byte)Inv(c.R, target.R),
            (byte)Inv(c.G, target.G),
            (byte)Inv(c.B, target.B));
    }
    private static void DrawPinDot(DrawingContext ctx, Point c, bool connected, Color accent)
    {
        if (connected)
        {
            // Soft accent halo behind the solid dot.
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)), null, c, 7.5, 7.5);
            ctx.DrawEllipse(new SolidColorBrush(accent), null, c, 5, 5);
        }
        else
        {
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)), null, c, 7, 7);
            ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Mix(accent, Colors.White, 0.35)), 1.2), c, 4, 4);
        }
    }

    private void DrawBezier(DrawingContext ctx, Point start, Point end, bool selected, bool dashed = false, Color? accent = null)
    {
        var (p1, p2) = BezierControlPoints(start, end);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(p1, p2, end);
            g.EndFigure(false);
        }
        // Palette: default gray-blue (#A8B3C2), selected brand purple (#7C3AED);
        // the dashed connect-preview line keeps its source accent so the user sees where it originates.
        var color = selected ? ColorSelected : (accent ?? ColorLine);
        var pen = new Pen(new SolidColorBrush(color), selected ? 2.0 : 1.5);
        if (dashed)
            pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        ctx.DrawGeometry(null, pen, geo);
    }

    private static (Point P1, Point P2) BezierControlPoints(Point start, Point end)
    {
        var dx = end.X - start.X;
        return (new Point(start.X + 3 * dx / 8, start.Y), new Point(start.X + 5 * dx / 8, end.Y));
    }

    private static Point BezierPoint(Point s, Point p1, Point p2, Point e, double t)
    {
        double mt = 1 - t;
        double a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
        return new Point(a * s.X + b * p1.X + c * p2.X + d * e.X,
                         a * s.Y + b * p1.Y + c * p2.Y + d * e.Y);
    }

    private double DistanceToBezier(Point p, Point start, Point end)
    {
        var (p1, p2) = BezierControlPoints(start, end);
        double min = double.MaxValue;
        var prev = start;
        for (int i = 1; i <= 20; i++)
        {
            var pt = BezierPoint(start, p1, p2, end, i / 20.0);
            min = Math.Min(min, DistanceToSegment(p, prev, pt));
            prev = pt;
        }
        return min;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double px = a.X + t * dx, py = a.Y + t * dy;
        double ox = p.X - px, oy = p.Y - py;
        return Math.Sqrt(ox * ox + oy * oy);
    }

    private void DrawText(DrawingContext ctx, string text, Point pos, IBrush brush, double size, FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(AppFont, FontStyle.Normal, weight), size, brush);
        ctx.DrawText(ft, pos);
    }

    private double TextWidth(string text, double size, FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(AppFont, FontStyle.Normal, weight), size, Brushes.Black);
        return ft.Width;
    }

    // ================= Z-order =================

    /// <summary>Nodes in top-to-bottom order for hit testing (highest z / last-drawn first).</summary>
    private IEnumerable<NodeViewModel> TopFirstNodes(CanvasViewModel vm)
        => vm.Nodes.Select((n, i) => (n, i))
            .OrderByDescending(x => x.n.ZIndex)
            .ThenByDescending(x => x.i)
            .Select(x => x.n);

    /// <summary>Nodes in bottom-to-top order for rendering (highest z drawn last = on top).</summary>
    private IEnumerable<NodeViewModel> BottomFirstNodes(CanvasViewModel vm)
        => vm.Nodes.Select((n, i) => (n, i))
            .OrderBy(x => x.n.ZIndex)
            .ThenBy(x => x.i)
            .Select(x => x.n);

    // ================= Hit testing =================

    private NodeViewModel? HitTestNode(Point pos)
    {
        var vm = Vm;
        if (vm is null) return null;
        foreach (var node in TopFirstNodes(vm))
            if (_layouts.TryGetValue(node, out var l) && l.Body.Contains(pos)) return node;
        return null;
    }

    private PinViewModel? HitTestInputPin(Point pos)
    {
        var vm = Vm;
        if (vm is null) return null;
        foreach (var node in TopFirstNodes(vm))
        {
            if (!_layouts.TryGetValue(node, out var l)) continue;
            for (int i = 0; i < l.InputHits.Count; i++)
                if (l.InputHits[i].Contains(pos)) return node.Inputs[i];
        }
        return null;
    }

    private PinViewModel? HitTestOutputPin(Point pos)
    {
        var vm = Vm;
        if (vm is null) return null;
        foreach (var node in TopFirstNodes(vm))
        {
            if (!_layouts.TryGetValue(node, out var l)) continue;
            for (int i = 0; i < l.OutputHits.Count; i++)
                if (l.OutputHits[i].Contains(pos)) return node.Outputs[i];
        }
        return null;
    }

    private ConnectionViewModel? HitTestConnection(Point pos)
    {
        var vm = Vm;
        if (vm is null) return null;
        foreach (var c in vm.Connections)
            if (DistanceToBezier(pos, c.Source.Anchor, c.Target.Anchor) < 7) return c;
        return null;
    }

    // ================= Interaction (input only; business logic in CanvasViewModel) =================

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Items.Any(i => i.Formats.Contains(DragFormats.PaletteItem))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        var item = e.DataTransfer.Items
            .FirstOrDefault(i => i.Formats.Contains(DragFormats.PaletteItem))
            ?.TryGetRaw(DragFormats.PaletteItem) as PaletteItem;
        if (item is null) return;
        var world = ScreenToWorld(e.GetPosition(this));
        vm.AddNodeAt(item, world);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var vm = Vm;
        if (vm is null) return;

        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            vm.BeginPan(e.GetPosition(this));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var pos = ScreenToWorld(e.GetPosition(this));

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (HitTestNode(pos) is { } rn)
            {
                vm.BringToFront(rn);
                vm.SelectOnly(rn);
                ShowNodeContextMenu(rn);
            }
            else
            {
                vm.SelectOnly(null);
            }
            e.Handled = true;
            return;
        }

        // When nodes overlap, only the topmost node under the cursor is reachable:
        // check its own pins first, then treat the click as its body (bring-to-front,
        // select, drag). Never fall through to a cross-node pin search here, which
        // could pick a lower overlapping node's pin instead of the visible top node.
        var topNodeAtPos = HitTestNode(pos);
        if (topNodeAtPos is not null)
        {
            if (_layouts.TryGetValue(topNodeAtPos, out var topLayout))
            {
                for (int i = 0; i < topLayout.OutputHits.Count; i++)
                    if (topLayout.OutputHits[i].Contains(pos))
                    {
                        vm.BeginConnect(topNodeAtPos.Outputs[i], pos);
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                for (int i = 0; i < topLayout.InputHits.Count; i++)
                    if (topLayout.InputHits[i].Contains(pos))
                    {
                        vm.SelectOnly(topNodeAtPos);
                        if (topNodeAtPos.Inputs[i].IsConnected)
                            vm.DisconnectInputPin(topNodeAtPos.Inputs[i]);
                        e.Handled = true;
                        return;
                    }
            }

            // Body of the topmost node: raise it, select it, then drag / open params.
            vm.BringToFront(topNodeAtPos);
            // Ctrl+Left-click: toggle this node's selection while keeping others; plain left-click: single-select.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                vm.ToggleSelection(topNodeAtPos);
            else
                vm.SelectOnly(topNodeAtPos);
            // Double-click a node: request the parameter dialog (business logic in the ViewModel).
            if (e.ClickCount >= 2)
            {
                vm.OpenNodeParameters(topNodeAtPos);
                e.Handled = true;
                return;
            }

            vm.BeginDrag(topNodeAtPos, pos);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // No node body under the cursor: a pin dot may still be reachable where it
        // overhangs just outside the card edge, so fall back to a global (top-first)
        // pin search.
        if (HitTestOutputPin(pos) is { } outPin)
        {
            vm.BeginConnect(outPin, pos);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (HitTestInputPin(pos) is { } inPin)
        {
            vm.SelectOnly(inPin.Node);
            if (inPin.IsConnected)
                vm.DisconnectInputPin(inPin);
            e.Handled = true;
            return;
        }
        if (HitTestConnection(pos) is { } conn)
        {
            vm.RemoveConnection(conn);
            e.Handled = true;
            return;
        }

        vm.SelectOnly(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var vm = Vm;
        if (vm is null) return;
        var screen = e.GetPosition(this);
        if (vm.IsPanning)
        {
            vm.UpdatePan(screen);
            InvalidateVisual();
            return;
        }
        var world = ScreenToWorld(screen);
        if (vm.IsDragging)
        {
            vm.UpdateDrag(world);
            InvalidateVisual();
        }
        else if (vm.IsConnecting)
        {
            vm.UpdateConnect(world);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var vm = Vm;
        if (vm is null) { e.Pointer.Capture(null); return; }

        if (vm.IsPanning)
        {
            vm.EndPan();
            e.Pointer.Capture(null);
            return;
        }

        if (vm.IsConnecting)
        {
            var target = HitTestInputPin(ScreenToWorld(e.GetPosition(this)));
            vm.EndConnect(target);
            InvalidateVisual();
        }

        if (vm.IsDragging)
            vm.EndDrag();

        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Vm is { } vm)
        {
            var p = e.GetPosition(this);
            vm.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (Vm is { } vm) vm.DeleteSelection();
            e.Handled = true;
        }
    }

    /// <summary>Show the right-click context menu for the selected node at the given screen position (Delete). Presentation only; actions are bound to ViewModel commands.</summary>
    private void ShowNodeContextMenu(NodeViewModel node)
    {
        var menu = new ContextMenu();

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) =>
        {
            if (Vm is { } vm) vm.RemoveNodeCommand.Execute(node);
        };

        menu.Items.Add(delete);
        this.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(this);
    }

    // ================= Data change subscriptions =================

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (NodeViewModel n in e.NewItems) n.PropertyChanged += OnNodePropertyChanged;
        if (e.OldItems is not null)
            foreach (NodeViewModel n in e.OldItems) n.PropertyChanged -= OnNodePropertyChanged;
        RefreshLayouts();
        InvalidateVisual();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasViewModel.Zoom)
            or nameof(CanvasViewModel.PanX)
            or nameof(CanvasViewModel.PanY))
            InvalidateVisual();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NodeViewModel n) return;
        if (e.PropertyName == nameof(NodeViewModel.Location))
            ComputeLayout(n);
        if (e.PropertyName == nameof(NodeViewModel.Location)
            || e.PropertyName == nameof(NodeViewModel.IsSelected)
            || e.PropertyName == nameof(NodeViewModel.ZIndex)
            || e.PropertyName == nameof(NodeViewModel.Title)
            || e.PropertyName == nameof(NodeViewModel.DependsOn))
            InvalidateVisual();
    }
}



