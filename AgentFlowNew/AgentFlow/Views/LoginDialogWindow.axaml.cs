// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LoginDialogWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Controls;
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
        Opened += OnOpened;
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
}
