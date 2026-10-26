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
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AgentFlow.Services;
using AgentFlow.Core;
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
    // Running indicator: a lucide "refresh-cw" spinner drawn in the node header. Green + rotating
    // while the node is running, amber + rotating while it is still starting up (connecting), red +
    // static when its start failed, and gray + static when idle.
    private static readonly StreamGeometry RefreshIndicatorGeometry = StreamGeometry.Parse(
        "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 " +
        "M21 3v5h-5 " +
        "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 " +
        "M8 16H3v5");
    private static readonly Color ColorRunning = Color.Parse("#16A34A");
    private static readonly Color ColorConnecting = Color.Parse("#F59E0B");
    private static readonly Color ColorFailed = Color.Parse("#EF4444");

    private readonly DispatcherTimer _spinTimer;
    private double _spinAngle;   // current rotation (degrees) of the running indicators

    // Hover tooltip state for a node title that was ellipsized (Plan A).
    private bool _hoverTitleActive;
    private string? _hoverTitleText;
    private Point _hoverTitleScreen;

    // The currently-open right-click node menu, if any. Only one menu is allowed at a time;
    // opening a new one always closes the previous one first.
    private Window? _openNodeMenuWindow;
    // Last right-click position in screen coords, used to place the menu window.
    private PixelPoint _lastRightClickScreenPos;


    public GraphCanvas()
    {
        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _spinTimer.Tick += OnSpinTick;

        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
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

    // ================= Running indicator animation =================

    /// <summary>Advance the spin angle on each timer tick (only while a node is running).</summary>
    private void OnSpinTick(object? sender, EventArgs e)
    {
        _spinAngle = (_spinAngle + 2.0) % 360;   // ~60°/s ≈ one rotation every 6s (slow, smooth)
        InvalidateVisual();
    }

    /// <summary>Start the spinner while any node is executing or still starting up; stop when all are idle (avoids wasted redraws).</summary>
    private void UpdateSpinAnimation()
    {
        bool anyActive = Vm?.Nodes.Any(n => n.Running || n.Connecting) ?? false;
        if (anyActive && !_spinTimer.IsEnabled) _spinTimer.Start();
        else if (!anyActive && _spinTimer.IsEnabled) _spinTimer.Stop();
    }

    // ================= Layout =================

    private sealed class NodeLayout
    {
        public required NodeViewModel Node { get; init; }
        public double Height;
        public double HeaderBottom;
        public Rect Body = default;
        public List<Rect> InputHits = new();
        public List<Rect> OutputHits = new();
        public List<(Rect Rect, string Text)> HoverRegions = new();

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
        var bg = R("CanvasBg", Color.Parse("#F6F8FB"));
        ctx.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, Bounds.Width, Bounds.Height));
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
            UpdateSpinAnimation();
        }

        // Hover tooltip is drawn in screen space (not world space) so it does not pan/zoom.
        if (_hoverTitleActive && _hoverTitleText is not null)
            DrawTooltip(ctx, _hoverTitleText, _hoverTitleScreen);
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
        var idText = $"#{node.InstanceId}";
        double idW = TextWidth(idText, 9);
        const double iconSize = 13;
        double instanceTextLeft = x + NodeWidth - PadX - idW;
        double iconCenterX = instanceTextLeft - 7 - iconSize / 2.0;

        // Title is ellipsized so it never collides with the instance tag / running indicator.
        double titleLeftX = badgeCenter.X + badgeR + 8;
        double titleMaxWidth = (iconCenterX - iconSize / 2.0 - 4) - titleLeftX;
        if (titleMaxWidth < 0) titleMaxWidth = 0;
        string titleText = Ellipsize(node.Title, 11, FontWeight.SemiBold, titleMaxWidth);
        DrawText(ctx, titleText, new Point(titleLeftX, y + 12.5),
            new SolidColorBrush(ColorTitle), 11, FontWeight.SemiBold, titleMaxWidth);
        if (TextWidth(node.Title, 11, FontWeight.SemiBold) > titleMaxWidth + 0.5)
            l.HoverRegions.Add((new Rect(titleLeftX, y, titleMaxWidth, HeaderHeight), node.Title));

        DrawText(ctx, idText, new Point(x + NodeWidth - PadX - idW, y + 15),
            new SolidColorBrush(ColorMuted), 9);

        // Running indicator: spinner at the node's top-right, immediately to the left of the
        // "#instance" tag. Green while the node is started (it keeps rotating only while the node is
        // actually executing — a one-shot node that already finished stays solid green), amber while
        // it is still starting up (connecting), red when the start failed, gray + static when idle.
        var iconCenter = new Point(iconCenterX, y + 15 + 9 / 2.0); // vertically centered on the "#instance" tag
        var indicatorColor = node.Started ? ColorRunning
            : node.Connecting ? ColorConnecting
            : node.Failed ? ColorFailed
            : ColorMuted;
        var indicatorAngle = node.Running || node.Connecting ? _spinAngle : 0;
        DrawRefreshIndicator(ctx, iconCenter, iconSize, indicatorColor, indicatorAngle);

        // Meta rows below the header tint: Name (type id) and DependsOn, both ellipsized to fit the card width.
        double metaMaxWidth = NodeWidth - PadX * 2;
        DrawText(ctx, Ellipsize(node.Name, 10, FontWeight.Normal, metaMaxWidth),
            new Point(x + PadX, y + HeaderHeight + 6),
            new SolidColorBrush(ColorName), 10);
        if (TextWidth(node.Name, 10, FontWeight.Normal) > metaMaxWidth + 0.5)
            l.HoverRegions.Add((new Rect(x + PadX, y + HeaderHeight + 6, metaMaxWidth, 18), node.Name));
        var dependsText = $"DependsOn: {node.DependsOn}";
        DrawText(ctx, Ellipsize(dependsText, 10, FontWeight.Normal, metaMaxWidth),
            new Point(x + PadX, y + HeaderHeight + 24),
            new SolidColorBrush(ColorMuted), 10);
        if (TextWidth(dependsText, 10, FontWeight.Normal) > metaMaxWidth + 0.5)
            l.HoverRegions.Add((new Rect(x + PadX, y + HeaderHeight + 24, metaMaxWidth, 18), dependsText));

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
    /// <summary>Draw the lucide "refresh-cw" spinner centered at <paramref name="center"/>, scaled to
    /// <paramref name="size"/> px and rotated by <paramref name="angleDeg"/> around its own center.</summary>
    private void DrawRefreshIndicator(DrawingContext ctx, Point center, double size, Color color, double angleDeg)
    {
        double scale = size / 24.0;
        double rad = angleDeg * Math.PI / 180.0;
        // Avalonia Matrix is row-vector: the LEFT factor is applied first, so the order below
        // moves the icon's own center (12,12) to the origin, scales, rotates around its own
        // center, then translates to the requested <paramref name="center"/> on the canvas.
        var m = Matrix.CreateTranslation(-12, -12)
              * Matrix.CreateScale(new Vector(scale, scale))
              * Matrix.CreateRotation(rad)
              * Matrix.CreateTranslation(center.X, center.Y);
        using (ctx.PushTransform(m))
        {
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2.0), RefreshIndicatorGeometry);
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

    private void DrawText(DrawingContext ctx, string text, Point pos, IBrush brush, double size,
        FontWeight weight = FontWeight.Normal, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(AppFont, FontStyle.Normal, weight), size, brush);
        if (double.IsFinite(maxWidth) && maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        ctx.DrawText(ft, pos);
    }
    private double TextWidth(string text, double size, FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(AppFont, FontStyle.Normal, weight), size, Brushes.Black);
        return ft.Width;
    }

    /// <summary>Return <paramref name="text"/> truncated with a trailing "…" so it fits within <paramref name="maxWidth"/>.</summary>
    private string Ellipsize(string text, double size, FontWeight weight, double maxWidth)
    {
        if (maxWidth <= 0 || TextWidth(text, size, weight) <= maxWidth)
            return text;
        int k = text.Length;
        while (k > 0)
        {
            var candidate = text[..k] + "…";
            if (TextWidth(candidate, size, weight) <= maxWidth)
                return candidate;
            k--;
        }
        return "…";
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

        // Any press on the canvas dismisses the open right-click menu first.
        // (Clicks inside the menu itself hit the Popup, not the canvas, so they don't land here.)
        CloseNodeContextMenu();

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
            _lastRightClickScreenPos = this.PointToScreen(e.GetPosition(this));
            if (HitTestNode(pos) is { } rn)
            {
                vm.BringToFront(rn);
                vm.SelectOnly(rn);
                ShowNodeContextMenu(rn);
            }
            else
            {
                CloseNodeContextMenu();
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
            ClearHover();
            vm.UpdatePan(screen);
            InvalidateVisual();
            return;
        }
        var world = ScreenToWorld(screen);
        if (vm.IsDragging)
        {
            ClearHover();
            vm.UpdateDrag(world);
            InvalidateVisual();
        }
        else if (vm.IsConnecting)
        {
            ClearHover();
            vm.UpdateConnect(world);
            InvalidateVisual();
        }
        else
        {
            UpdateHover(world, screen);
        }
    }

    /// <summary>Reset the hover-tooltip state (no longer over a truncated title).</summary>
    private void ClearHover()
    {
        if (_hoverTitleActive)
        {
            _hoverTitleActive = false;
            _hoverTitleText = null;
            InvalidateVisual();
        }
    }

    /// <summary>Update whether the pointer is over a node title that was ellipsized (Plan A).</summary>
    private void UpdateHover(Point world, Point screen)
    {
        var active = false;
        string? text = null;
        var topNode = HitTestNode(world);
        if (topNode is not null && _layouts.TryGetValue(topNode, out var l))
        {
            foreach (var region in l.HoverRegions)
            {
                if (region.Rect.Contains(world))
                {
                    active = true;
                    text = region.Text;
                    break;
                }
            }
        }
        if (active != _hoverTitleActive || text != _hoverTitleText)
        {
            _hoverTitleActive = active;
            _hoverTitleText = text;
            _hoverTitleScreen = screen;
            InvalidateVisual();
        }
    }

    /// <summary>Draw a small dark tooltip near the cursor with the full (untruncated) node title.</summary>
    private void DrawTooltip(DrawingContext ctx, string text, Point screenPos)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(AppFont, FontStyle.Normal, FontWeight.Normal), 11, Brushes.White);
        double w = ft.Width + 16;
        double h = ft.Height + 10;
        var pos = new Point(screenPos.X + 12, screenPos.Y + 14);
        pos = new Point(Math.Min(pos.X, Math.Max(0, Bounds.Width - w - 4)),
                        Math.Min(pos.Y, Math.Max(0, Bounds.Height - h - 4)));
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 15, 23, 42)), null,
            new RoundedRect(new Rect(pos, new Size(w, h)), 6, 6));
        ctx.DrawText(ft, new Point(pos.X + 8, pos.Y + 5));
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

    /// <summary>Close any currently-open right-click node menu, if one is open.</summary>
    private void CloseNodeContextMenu()
    {
        if (_openNodeMenuWindow is null) return;
        var old = _openNodeMenuWindow;
        _openNodeMenuWindow = null;
        old.Close();
        old.Content = null;
    }

    /// <summary>
    /// Show the node right-click menu. The menu body is the fully self-drawn
    /// <see cref="NodeContextMenu"/> (visual language matches the node cards); an Avalonia
    /// <see cref="Popup"/> only provides the top-level popup shell (pointer placement,
    /// click-outside-to-close, Esc). Every action is dispatched through the view model.
    /// </summary>
    private void ShowNodeContextMenu(NodeViewModel node)
    {
        var vm = Vm;
        if (vm is null) return;

        // Only one menu may be open at a time: close the previous one first.
        CloseNodeContextMenu();

        var commands = vm.GetNodeCommands(node);

        var menu = new NodeContextMenu
        {
            Items = commands,
            ShowDelete = true,
        };
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        void Close()
        {
            if (_openNodeMenuWindow is null) return;
            _openNodeMenuWindow.Close();
        }

        menu.CommandSelected += async cmd =>
        {
            Close();
            await vm.ExecuteNodeCommandAsync(node, cmd);
        };
        menu.DeleteSelected += () =>
        {
            Close();
            vm.RemoveNodeCommand.Execute(node);
        };
        menu.CloseRequested += Close;

        var window = new Window
        {
            Content = menu,
            Title = "",
            ShowInTaskbar = false,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None },
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            Focusable = true,
        };

        // Position the window at the right-click screen position.
        window.Position = new PixelPoint(_lastRightClickScreenPos.X + 4, _lastRightClickScreenPos.Y + 4);

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openNodeMenuWindow, window))
            {
                _openNodeMenuWindow = null;
            }
        };

        // Click outside the window closes it (light dismiss).
        window.Deactivated += (_, _) =>
        {
            window.Close();
        };

        _openNodeMenuWindow = window;
        window.Show();
        window.Activate();
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
            || e.PropertyName == nameof(NodeViewModel.DependsOn)
            || e.PropertyName == nameof(NodeViewModel.Running)
            || e.PropertyName == nameof(NodeViewModel.Started)
            || e.PropertyName == nameof(NodeViewModel.Connecting)
            || e.PropertyName == nameof(NodeViewModel.Failed))
            InvalidateVisual();
        if (e.PropertyName is nameof(NodeViewModel.Running)
            or nameof(NodeViewModel.Started)
            or nameof(NodeViewModel.Connecting)
            or nameof(NodeViewModel.Failed))
            UpdateSpinAnimation();
    }
}




