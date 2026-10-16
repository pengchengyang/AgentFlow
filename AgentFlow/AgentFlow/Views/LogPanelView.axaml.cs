// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogPanelView.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AgentFlow.Services;

namespace AgentFlow.Views;

public partial class LogPanelView : UserControl
{
    private DateTimeOffset _lastEmptyPress;
    private Point _lastEmptyPos;
    private bool _hasLastEmptyPress;

    public LogPanelView()
    {
        InitializeComponent();
        // Catch pointer presses anywhere in the panel so empty-area double-click works
        // regardless of which inner element is hit.
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        // Right-click: select the clicked log row (so the Copy menu acts on it).
        if (props.IsRightButtonPressed)
        {
            if ((e.Source as StyledElement)?.DataContext is UiLogEntry clicked
                && !LogListBox.SelectedItems.Contains(clicked))
            {
                LogListBox.SelectedItems.Clear();
                LogListBox.SelectedItems.Add(clicked);
            }
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // Ignore interactive controls (buttons, search box) and log rows.
        if (e.Source is StyledElement src)
        {
            if (src is Visual v && (v.FindAncestorOfType<Button>() != null
                || v.FindAncestorOfType<TextBox>() != null))
                return;

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
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentflow_logtoggle.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " toggle" + Environment.NewLine);
            if (DataContext is ViewModels.LogPanelViewModel vm && vm.ToggleExpandedCommand.CanExecute(null))
                vm.ToggleExpandedCommand.Execute(null);
        }
    }

    /// <summary>Ctrl+C copies the selected log entries to the clipboard.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopySelected();
            e.Handled = true;
        }
    }

    private void CopyMenu_Click(object? sender, RoutedEventArgs e) => CopySelected();

    private async void CopySelected()
    {
        var text = BuildClipboardText(LogListBox.SelectedItems.OfType<UiLogEntry>());
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this) is { } top && top.Clipboard is { } cb)
            await cb.SetTextAsync(text);
    }

    private static string BuildClipboardText(System.Collections.Generic.IEnumerable<UiLogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var en in entries)
            sb.AppendLine($"{en.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {en.Level,-7} {en.Source,-16}{en.RawMessage}");
        return sb.ToString().TrimEnd('\r', '\n');
    }
}

