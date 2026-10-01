// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CloseConfirmDialogWindow.axaml.cs">
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
/// Close-confirmation dialog window. Presentation and window lifecycle only:
/// listens to the ViewModel's <see cref="CloseConfirmDialogViewModel.RequestClose"/>
/// event to close the window and return the chosen result (0=Cancel, 1=Discard, 2=Save&Exit).
/// </summary>
public partial class CloseConfirmDialogWindow : Window
{
    public CloseConfirmDialogWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Opened += OnOpened;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }
        };
        Opened += (_, _) => Opacity = 1;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is CloseConfirmDialogViewModel vm)
            vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, int result)
    {
        if (DataContext is CloseConfirmDialogViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(result);
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


