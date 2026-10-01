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

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Only prompt when there are unsaved changes.
        if (DataContext is not MainWindowViewModel mvm || mvm.Main is not { IsDirty: true })
            return;

        e.Cancel = true;

        var dialog = new CloseConfirmDialogViewModel(
            "Unsaved Changes",
            "The current workflow has unsaved changes. What would you like to do?");

        var win = new CloseConfirmDialogWindow { DataContext = dialog };
        int result = await win.ShowDialog<int>(this);

        switch (result)
        {
            case 1: // Discard / exit without saving
                mvm.Main.IsDirty = false;
                Close();
                break;
            case 2: // Save then exit
                var path = Path.Combine(AppContext.BaseDirectory, "workflow.json");
                mvm.Main.SaveToPath(path);
                Close();
                break;
            // case 0 (Cancel): do nothing, window stays open.
        }
    }
}
