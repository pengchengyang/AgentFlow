// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogPanelView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentFlow.Services;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class LogPanelView : UserControl
{
    private DateTimeOffset _lastEmptyPress;
    private Point _lastEmptyPos;
    private bool _hasLastEmptyPress;
    private ScrollViewer? _scrollViewer;
    private LogPanelViewModel? _viewModel;
    private bool _scrollPending;

    public LogPanelView()
    {
        InitializeComponent();
        // Catch pointer presses anywhere in the panel so empty-area double-click works
        // regardless of which inner element is hit.
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => AttachViewModel(DataContext as LogPanelViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => RequestScrollToEnd();
        LogListBox.AddHandler(InputElement.PointerWheelChangedEvent, OnLogPointerWheelChanged,
            RoutingStrategies.Tunnel);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as LogPanelViewModel);
        LogListBox.ApplyTemplate();
        AttachScrollViewer();
        if (_scrollViewer is null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AttachScrollViewer();
                RequestScrollToEnd();
            }, DispatcherPriority.Background);
        }

        RequestScrollToEnd();
    }

    private void AttachScrollViewer()
    {
        var scrollViewer = LogListBox.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null || ReferenceEquals(scrollViewer, _scrollViewer))
            return;

        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnScrollChanged;

        _scrollViewer = scrollViewer;
        _scrollViewer.ScrollChanged += OnScrollChanged;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => AttachViewModel(null);

    private void AttachViewModel(LogPanelViewModel? viewModel)
    {
        if (_viewModel == viewModel) return;

        if (_viewModel is not null)
        {
            _viewModel.LogsRefreshed -= OnLogsRefreshed;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.LogsRefreshed += OnLogsRefreshed;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RequestScrollToEnd();
    }

    private void OnLogsRefreshed(object? sender, EventArgs e) => RequestScrollToEnd();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogPanelViewModel.IsAutoScrollEnabled)
            && _viewModel is { IsAutoScrollEnabled: true })
        {
            RequestScrollToEnd();
        }
    }

    /// <summary>Follow newly appended logs unless the user has taken over scrolling.</summary>
    private void RequestScrollToEnd()
    {
        if (_viewModel is not { IsAutoScrollEnabled: true } || _scrollPending)
            return;

        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (_viewModel is not { IsAutoScrollEnabled: true })
                return;

            ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        if (_scrollViewer is null) return;

        var maxY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (_scrollViewer.Offset.Y >= maxY - 0.5)
            return;

        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, maxY);
    }

    private void DisableAutoScroll()
    {
        if (_viewModel is { IsAutoScrollEnabled: true })
            _viewModel.IsAutoScrollEnabled = false;
    }

    private void OnLogPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        => DisableAutoScroll();

    /// <summary>
    /// Fallback for touch / scrollbar / accessibility scrolling that does not raise pointer
    /// events. Layout-driven extent changes are ignored so filtering and new entries cannot
    /// be mistaken for user scrolling.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is not { IsAutoScrollEnabled: true } || _scrollViewer is null)
            return;
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0 || e.OffsetDelta.Y == 0)
            return;

        var maxY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (_scrollViewer.Offset.Y >= maxY - 0.5)
            return;

        // A real offset change without an extent/viewport change means the user moved the list.
        DisableAutoScroll();
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        // Right-click: select the clicked log row (so the Copy menu acts on it).
        if (props.IsRightButtonPressed)
        {
            if ((e.Source as StyledElement)?.DataContext is UiLogEntry clicked
                && LogListBox.SelectedItems is { } selected
                && !selected.Contains(clicked))
            {
                selected.Clear();
                selected.Add(clicked);
            }
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // Ignore interactive controls (buttons, toggle, search box, scrollbar) and log rows.
        if (e.Source is StyledElement src)
        {
            if (src is Visual v)
            {
                if (v is Button or TextBox or ToggleSwitch
                    || v.FindAncestorOfType<Button>() != null
                    || v.FindAncestorOfType<TextBox>() != null
                    || v.FindAncestorOfType<ToggleSwitch>() != null)
                    return;

                if (v is ScrollBar || v.FindAncestorOfType<ScrollBar>() != null)
                {
                    // The user grabbed the scrollbar: leave follow mode immediately.
                    DisableAutoScroll();
                    return;
                }
            }

            if (src.DataContext is UiLogEntry)
            {
                _hasLastEmptyPress = false;
                return;
            }
        }

        // Double-click on empty area toggles expand/collapse.
        var now = DateTimeOffset.UtcNow;
        var pos = e.GetPosition(this);
        var isDouble = _hasLastEmptyPress
            && (now - _lastEmptyPress).TotalMilliseconds < 500
            && Math.Abs(pos.X - _lastEmptyPos.X) < 10
            && Math.Abs(pos.Y - _lastEmptyPos.Y) < 10;
        _lastEmptyPress = now;
        _lastEmptyPos = pos;
        _hasLastEmptyPress = true;

        if (isDouble)
        {
            _hasLastEmptyPress = false;
            if (DataContext is ViewModels.LogPanelViewModel vm && vm.ToggleExpandedCommand.CanExecute(null))
                vm.ToggleExpandedCommand.Execute(null);
        }
    }

    /// <summary>Ctrl+C copies the selected log entries; scroll keys leave follow mode.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
            DisableAutoScroll();

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopySelected();
            e.Handled = true;
        }
    }

    private void CopyMenu_Click(object? sender, RoutedEventArgs e) => CopySelected();

    private async void CopySelected()
    {
        try
        {
            var selected = LogListBox.SelectedItems;
            if (selected is null) return;
            var text = BuildClipboardText(selected.OfType<UiLogEntry>());
            if (string.IsNullOrEmpty(text)) return;
            if (TopLevel.GetTopLevel(this) is { } top && top.Clipboard is { } cb)
                await cb.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            // Clipboard can be unavailable (headless / remote session); never crash the UI.
            System.Diagnostics.Debug.WriteLine($"CopySelected failed: {ex.Message}");
        }
    }

    private static string BuildClipboardText(System.Collections.Generic.IEnumerable<UiLogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var en in entries)
            sb.AppendLine($"{en.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {en.Level,-7} {en.Source,-16}{en.RawMessage}");
        return sb.ToString().TrimEnd('\r', '\n');
    }
}
