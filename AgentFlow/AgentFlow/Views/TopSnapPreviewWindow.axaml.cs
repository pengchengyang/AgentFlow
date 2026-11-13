// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="TopSnapPreviewWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentFlow.Views;

/// <summary>
/// Non-interactive maximize preview shown above the desktop while the title bar is in the top snap zone.
/// </summary>
public partial class TopSnapPreviewWindow : Window
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(120);

    private bool _previewVisible;
    private bool _windowShown;
    private bool _hasEverShown;
    private bool _closed;
    private int _hideVersion;

    public TopSnapPreviewWindow()
    {
        InitializeComponent();
        PreviewSurface.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = FadeDuration,
                Easing = new Avalonia.Animation.Easings.QuadraticEaseOut()
            }
        };
    }

    public void ShowPreview(PixelRect workArea, double scaling)
    {
        _hideVersion++;

        var previewBounds = workArea.ToRect(scaling);
        Position = new PixelPoint(workArea.X, workArea.Y);
        Width = Math.Max(1.0, previewBounds.Width);
        Height = Math.Max(1.0, previewBounds.Height);

        if (_previewVisible)
            return;

        _previewVisible = true;
        PreviewSurface.Opacity = 0.0;

        if (!_windowShown)
        {
            Show();
            _windowShown = true;
            _hasEverShown = true;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_previewVisible)
                    PreviewSurface.Opacity = 1.0;
            },
            DispatcherPriority.Render);
    }

    public async void HidePreview()
    {
        if (!_previewVisible)
            return;

        _previewVisible = false;
        int hideVersion = ++_hideVersion;
        PreviewSurface.Opacity = 0.0;

        await Task.Delay(FadeDuration).ConfigureAwait(true);
        if (_previewVisible || hideVersion != _hideVersion || !_windowShown)
            return;

        Hide();
        _windowShown = false;
    }

    public void ClosePreview()
    {
        _previewVisible = false;
        _hideVersion++;
        _windowShown = false;

        if (_hasEverShown && !_closed)
        {
            _closed = true;
            Close();
        }
    }
}
