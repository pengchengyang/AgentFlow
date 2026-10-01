// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ConfirmDialogWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// Confirmation dialog window. Presentation and window lifecycle only: listens to the
/// ViewModel's <see cref="ConfirmDialogViewModel.RequestClose"/> event to close the window
/// and return the confirmation result (bool); no business logic.
/// </summary>
public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }
        };
        Opened += OnOpened;
        Opened += (_, _) => Opacity = 1;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is ConfirmDialogViewModel vm)
            vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool confirmed)
    {
        if (DataContext is ConfirmDialogViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(confirmed);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
