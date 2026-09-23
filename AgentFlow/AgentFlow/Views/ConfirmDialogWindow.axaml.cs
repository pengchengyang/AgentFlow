// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ConfirmDialogWindow.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia.Controls;
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
        Opened += OnOpened;
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
}
