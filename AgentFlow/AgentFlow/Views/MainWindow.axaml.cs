// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeRestoreIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateMaximizeRestoreIcon();
    }

    private void UpdateMaximizeRestoreIcon()
    {
        if (MaximizeIcon is null || RestoreIcon is null)
            return;

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;

        // Rounded floating frame only while windowed; a maximized window fills the screen.
        if (RootBorder is not null)
            RootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestoreButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            MaxRestoreButton_Click(sender, e);
        else
            BeginMoveDrag(e);
    }

    private bool _allowClose;
    private bool _closing;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_allowClose) return;
        if (DataContext is not MainWindowViewModel mvm || mvm.Main is null) return;
        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        _closing = true;
        e.Cancel = true;
        var main = mvm.Main;
        try
        {
            // Ask about unsaved changes first; the workflow keeps running while the user decides.
            bool saveRequested = false;
            if (main.IsDirty)
            {
                var dialog = new CloseConfirmDlgViewModel(
                    "Unsaved Changes",
                    "The current workflow has unsaved changes. What would you like to do?");
                var win = new CloseConfirmDlgWindow { DataContext = dialog };
                int result = await win.ShowDialog<int>(this);

                if (result == 0) return;             // Cancel: keep the window open.
                saveRequested = result == 2;
            }

            // Stop first: saving while nodes run is rejected by the edit gate, and the window must
            // never close while nodes still own ports/sockets/files.
            await main.StopForShutdownAsync();
            if (saveRequested && !await main.SaveCurrentAsync())
            {
                // Save failed: keep the window, and let the user keep editing / start again.
                main.CancelShutdown();
                return;
            }
            main.IsDirty = false;
            main.DisposeBroadcastDemo();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            main.CancelShutdown();
            main.StatusText = $"{main.L["ErrorPrefix"]}: {ex.Message}";
        }
        finally
        {
            _closing = false;
        }
    }
}
