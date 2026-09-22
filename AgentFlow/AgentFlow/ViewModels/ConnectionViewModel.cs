using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// 连线 ViewModel：一条从输出 pin 到输入 pin 的贝塞尔曲线。
/// 完全自绘，不依赖 Nodify。
/// </summary>
public partial class ConnectionViewModel : ViewModelBase
{
    public PinViewModel Source { get; }
    public PinViewModel Target { get; }

    [ObservableProperty]
    private bool _isSelected;

    // 贝塞尔曲线四个控制点（Canvas 坐标）
    [ObservableProperty] private Point _start;
    [ObservableProperty] private Point _p1;
    [ObservableProperty] private Point _p2;
    [ObservableProperty] private Point _end;

    public ConnectionViewModel(PinViewModel source, PinViewModel target)
    {
        Source = source;
        Target = target;
    }

    /// <summary>根据起止点重算贝塞尔控制点（LangFlow 风格平滑曲线）。</summary>
    public void UpdateGeometry(Point start, Point end)
    {
        Start = start;
        End = end;
        var dx = end.X - start.X;
        P1 = new Point(start.X + 3 * dx / 8, start.Y + 0);
        P2 = new Point(start.X + 5 * dx / 8, end.Y);
    }
}
