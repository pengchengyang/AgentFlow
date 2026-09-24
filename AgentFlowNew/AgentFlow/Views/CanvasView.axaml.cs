// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CanvasView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    // ---- Layout constants ----
    private const double NodeWidth = 280;
    private const double PadX = 16;
    private const double HeaderHeight = 68;
    private const double InputRow = 32;
    private const double OutputRow = 28;
    private const double ParamRow = 62;

    private static readonly Color ColorBg = Color.Parse("#F9F5EB");
    private static readonly Color ColorDot = Color.Parse("#E4DBCD");
    private static readonly Color ColorTitle = Color.Parse("#111827");
    private static readonly Color ColorTypeId = Color.Parse("#6B7280");
    private static readonly Color ColorMuted = Color.Parse("#9CA3AF");
    private static readonly Color ColorBorder = Color.Parse("#94A3B8");
    private static readonly Color ColorDivider = Color.Parse("#F3F4F6");
    private static readonly Color ColorSelected = Color.Parse("#F59E0B");
    private static readonly Color ColorLine = Color.Parse("#3F3F46");
    private static readonly Color ColorIn = Color.Parse("#3B82F6");
    private static readonly Color ColorOut = Color.Parse("#8B5CF6");
    private static readonly Color ColorPinBorder = Color.Parse("#D1D5DB");
    private static readonly Color ColorParamValue = Color.Parse("#6B7280");

    private readonly Dictionary<NodeViewModel, NodeLayout> _layouts = new();

    public GraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
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
        public Rect Body = default;
        public List<Rect> InputHits = new();
        public List<Rect> OutputHits = new();
        public List<(string Label, string Value, double Y)> Params = new();
    }

    private NodeLayout ComputeLayout(NodeViewModel node)
    {
        var loc = node.Location;
        var l = new NodeLayout { Node = node };
        double y = HeaderHeight;

        int nIn = node.Inputs.Count;
        if (nIn > 0)
        {
            y += 6;
            for (int i = 0; i < nIn; i++)
            {
                var pin = node.Inputs[i];
                pin.Anchor = new Point(loc.X, loc.Y + y + InputRow / 2);
                l.InputHits.Add(new Rect(loc.X - 8, loc.Y + y, 64, InputRow));
                y += InputRow;
            }
        }

        if (node.HasParameters)
        {
            y += 8;
            foreach (var p in node.Parameters)
            {
                l.Params.Add((p.Label, p.Value, loc.Y + y));
                y += ParamRow;
            }
        }

        int nOut = node.Outputs.Count;
        if (nOut > 0)
        {
            y += 8;
            for (int i = 0; i < nOut; i++)
            {
                var pin = node.Outputs[i];
                pin.Anchor = new Point(loc.X + NodeWidth, loc.Y + y + OutputRow / 2);
                l.OutputHits.Add(new Rect(loc.X + NodeWidth - 56, loc.Y + y, 64, OutputRow));
                y += OutputRow;
            }
        }

        y += 12;
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
            DrawDots(ctx);
            RefreshLayouts();

            foreach (var conn in vm.Connections)
                DrawBezier(ctx, conn.Source.Anchor, conn.Target.Anchor, conn.IsSelected);

            if (vm.IsConnecting && vm.ConnectSource is not null)
                DrawBezier(ctx, vm.ConnectSource.Anchor, vm.Pointer, false, dashed: true);

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

    private void DrawDots(DrawingContext ctx)
    {
        var vm = Vm;
        if (vm is null) return;
        var brush = new SolidColorBrush(ColorDot);
        double spacing = 20;
        // Keep on-screen spacing roughly constant while zooming so dot density stays stable
        // and the render loop stays bounded even at low zoom (smooth wheel-zoom).
        while (spacing * vm.Zoom < 14) spacing *= 2;
        double ox = Math.Floor((0 - vm.PanX) / vm.Zoom / spacing) * spacing;
        double oy = Math.Floor((0 - vm.PanY) / vm.Zoom / spacing) * spacing;
        double w = Bounds.Width / vm.Zoom;
        double h = Bounds.Height / vm.Zoom;
        for (double x = ox; x < w; x += spacing)
            for (double y = oy; y < h; y += spacing)
                ctx.DrawEllipse(brush, null, new Point(x, y), 1, 1);
    }

    private void DrawNode(DrawingContext ctx, NodeViewModel node)
    {
        var l = _layouts[node];
        var b = l.Body;

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)), null,
            new RoundedRect(new Rect(b.X, b.Y + 3, b.Width, b.Height), 12, 12));

        ctx.DrawRectangle(Brushes.White,
            new Pen(new SolidColorBrush(node.IsSelected ? ColorSelected : ColorBorder), node.IsSelected ? 2 : 1),
            new RoundedRect(b, 12, 12));

        double x = b.X, y = b.Y;

        DrawText(ctx, "◆", new Point(x + PadX, y + 14), node.Accent, 15, FontWeight.SemiBold);
        DrawText(ctx, node.Title, new Point(x + PadX + 22, y + 15), new SolidColorBrush(ColorTitle), 14, FontWeight.SemiBold);
        DrawText(ctx, node.TypeId, new Point(x + PadX, y + 42), new SolidColorBrush(ColorTypeId), 12);
        ctx.DrawLine(new Pen(new SolidColorBrush(ColorDivider)),
            new Point(x + PadX, y + HeaderHeight), new Point(x + NodeWidth - PadX, y + HeaderHeight));

        for (int i = 0; i < node.Inputs.Count; i++)
        {
            var pin = node.Inputs[i];
            DrawPinDot(ctx, pin.Anchor, pin.IsConnected, ColorIn);
            DrawText(ctx, pin.Name, new Point(x + 18, pin.Anchor.Y - 8), new SolidColorBrush(ColorTitle), 12);
        }

        foreach (var (label, value, py) in l.Params)
        {
            DrawText(ctx, label, new Point(x + PadX, py), new SolidColorBrush(ColorTitle), 12, FontWeight.Medium);
            DrawText(ctx, value, new Point(x + PadX, py + 20), new SolidColorBrush(ColorParamValue), 12);
        }

        for (int i = 0; i < node.Outputs.Count; i++)
        {
            var pin = node.Outputs[i];
            DrawPinDot(ctx, pin.Anchor, pin.IsConnected, ColorOut);
            double w = TextWidth(pin.Name, 12);
            DrawText(ctx, pin.Name, new Point(pin.Anchor.X - w - 10, pin.Anchor.Y - 8), new SolidColorBrush(ColorTitle), 12);
        }
    }

    private static void DrawPinDot(DrawingContext ctx, Point c, bool connected, Color accent)
    {
        if (connected)
            ctx.DrawEllipse(new SolidColorBrush(accent), null, c, 6, 6);
        else
            ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(ColorPinBorder), 1.5), c, 5, 5);
    }

    private void DrawBezier(DrawingContext ctx, Point start, Point end, bool selected, bool dashed = false)
    {
        var (p1, p2) = BezierControlPoints(start, end);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(p1, p2, end);
            g.EndFigure(false);
        }
        var pen = new Pen(new SolidColorBrush(selected ? ColorSelected : ColorLine), selected ? 2.6 : 1.8);
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
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);
        ctx.DrawText(ft, pos);
    }

    private double TextWidth(string text, double size, FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, Brushes.Black);
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

        if (HitTestNode(pos) is { } node)
        {
            vm.BringToFront(node);
            // Ctrl+Left-click: toggle this node's selection while keeping others; plain left-click: single-select.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                vm.ToggleSelection(node);
            else
                vm.SelectOnly(node);
            // Double-click a node: request the parameter dialog (business logic in the ViewModel).
            if (e.ClickCount >= 2)
            {
                vm.OpenNodeParameters(node);
                e.Handled = true;
                return;
            }

            vm.BeginDrag(node, pos);
            e.Pointer.Capture(this);
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

    /// <summary>Show the right-click context menu for the selected node at the given screen position (Send / Delete). Presentation only; actions are bound to ViewModel commands.</summary>
    private void ShowNodeContextMenu(NodeViewModel node)
    {
        var menu = new ContextMenu();

        var send = new MenuItem { Header = "Send" };
        send.Click += (_, _) =>
        {
            if (Vm is { } vm) vm.SendNodeCommand.Execute(node);
        };

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) =>
        {
            if (Vm is { } vm) vm.RemoveNodeCommand.Execute(node);
        };

        menu.Items.Add(send);
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
            || e.PropertyName == nameof(NodeViewModel.ZIndex))
            InvalidateVisual();
    }
}
