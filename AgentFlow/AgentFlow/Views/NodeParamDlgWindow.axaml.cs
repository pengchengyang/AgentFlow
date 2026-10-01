// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParamDlgWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgentFlow.ViewModels;
using AgentFlow.Services;

namespace AgentFlow.Views;

/// <summary>
/// Node parameter dialog window. Presentation and window lifecycle only:
/// listens to the ViewModel's RequestClose event to close the window; no business logic.
/// </summary>
public partial class NodeParamDlgWindow : Window
{
    public NodeParamDlgWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Opened += OnOpened;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }
        };
        Opened += (_, _) => Opacity = 1;
        UpdateMaximizeRestoreIcon();
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is NodeParameterDialogViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
            vm.RequestApplyConfirm += OnApplyConfirmRequested;
        }
    }
    private void OnRequestClose(object? sender, bool result)
    {
        if (DataContext is NodeParameterDialogViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(result);
    }

    /// <summary>Ask whether to apply unsaved zone edits before switching; presentation and lifecycle only.</summary>
    private async void OnApplyConfirmRequested(object? sender, System.EventArgs e)
    {
        if (DataContext is not NodeParameterDialogViewModel vm) return;
        var confirm = new ConfirmDialogViewModel(
            Loc.Instance["ConfirmApplyTitle"],
            Loc.Instance["ConfirmApplyMessage"]);
        var win = new ConfirmDialogWindow { DataContext = confirm };
        bool? result = null;
        confirm.RequestClose += (_, r) =>
        {
            result = r;
            win.Close();
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
        if (result == true)
            vm.ApplyPendingZoneChanges();
        else
            vm.DiscardPendingZoneChanges();
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
}


