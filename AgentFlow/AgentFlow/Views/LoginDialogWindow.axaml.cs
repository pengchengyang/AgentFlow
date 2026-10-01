// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LoginDialogWindow.axaml.cs">
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
/// Login dialog window. Presentation and window lifecycle only: listens to the
/// ViewModel's RequestClose event to close the window; no business logic.
/// </summary>
public partial class LoginDialogWindow : Window
{
    public LoginDialogWindow()
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
        if (DataContext is LoginDialogViewModel vm)
            vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool isAdmin)
    {
        if (DataContext is LoginDialogViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(isAdmin);
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


