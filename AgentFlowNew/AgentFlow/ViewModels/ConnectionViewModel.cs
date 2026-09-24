// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ConnectionViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// Connection ViewModel: a Bezier curve from an output pin to an input pin.
/// Fully self-drawn; does not depend on Nodify.
/// </summary>
public partial class ConnectionViewModel : ViewModelBase
{
    public PinViewModel Source { get; }
    public PinViewModel Target { get; }

    [ObservableProperty]
    private bool _isSelected;

    // The four Bezier control points (Canvas coordinates)
    [ObservableProperty] private Point _start;
    [ObservableProperty] private Point _p1;
    [ObservableProperty] private Point _p2;
    [ObservableProperty] private Point _end;

    public ConnectionViewModel(PinViewModel source, PinViewModel target)
    {
        Source = source;
        Target = target;
    }

    /// <summary>Recompute the Bezier control points from the endpoints (LangFlow-style smooth curve).</summary>
    public void UpdateGeometry(Point start, Point end)
    {
        Start = start;
        End = end;
        var dx = end.X - start.X;
        P1 = new Point(start.X + 3 * dx / 8, start.Y + 0);
        P2 = new Point(start.X + 5 * dx / 8, end.Y);
    }
}
