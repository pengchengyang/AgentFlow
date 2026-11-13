// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeParamDlgWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using AgentFlow.ViewModels;
using AgentFlow.Services;

namespace AgentFlow.Views;

/// <summary>
/// Node parameter dialog window. Presentation and window lifecycle only:
/// listens to the ViewModel's RequestClose event to close the window; no business logic.
/// </summary>
public partial class NodeParamDlgWindow : Window
{
    private Flyout? _multiSelectFlyout;

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
        if (DataContext is NodeParamDlgViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
            vm.RequestApplyConfirm += OnApplyConfirmRequested;
        }
    }
    private void OnRequestClose(object? sender, bool result)
    {
        if (DataContext is NodeParamDlgViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(result);
    }

    /// <summary>Ask whether to apply unsaved zone edits before switching; presentation and lifecycle only.</summary>
    private async void OnApplyConfirmRequested(object? sender, System.EventArgs e)
    {
        if (DataContext is not NodeParamDlgViewModel vm) return;
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

    /// <summary>
    /// Open a light-dismiss flyout for a multi-select row. The flyout is built from the row's
    /// option collection; its DataContext is the row, so checkbox bindings stay alive while it is open.
    /// </summary>
    private void OnMultiSelectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ParameterEditRow row)
            return;
        if (Resources["MultiSelectOptionTemplate"] is not IDataTemplate template)
            return;

        if (_multiSelectFlyout is { IsOpen: true } previous)
            previous.IsOpen = false;

        var list = new ItemsControl
        {
            ItemsSource = row.Options,
            ItemTemplate = template,
            DataContext = row,
        };
        var content = new Border
        {
            Padding = new Thickness(4),
            Width = 140,
            Child = new ScrollViewer
            {
                Content = list,
                MaxHeight = 160,
            },
        };
        var flyout = new Flyout
        {
            Content = content,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            VerticalOffset = 2,
            FlyoutPresenterTheme = Resources["ParamMultiSelectFlyoutPresenter"] as ControlTheme,
        };
        _multiSelectFlyout = flyout;
        // Use the whole edit field as the anchor: right alignment puts the popup under the
        // arrow, while its top edge starts below the field instead of inside the arrow's bounds.
        flyout.ShowAt(button);
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




