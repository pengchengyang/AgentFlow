// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ConfirmDialogViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// Generic confirmation dialog ViewModel. Clicking Confirm / Cancel (or pressing
/// Enter / Esc) raises <see cref="RequestClose"/> (bool: true = confirm, false = cancel).
/// Window presentation and lifecycle are handled by the View layer.
/// </summary>
public partial class ConfirmDialogViewModel : ViewModelBase
{
    /// <summary>Window title.</summary>
    public string Title { get; }

    /// <summary>Prompt message body.</summary>
    public string Message { get; }

    /// <summary>Confirm button label.</summary>
    public string ConfirmText { get; }

    /// <summary>Cancel button label.</summary>
    public string CancelText { get; }

    /// <summary>Close request: true = confirm, false = cancel / Esc.</summary>
    public event EventHandler<bool>? RequestClose;

    public ConfirmDialogViewModel(string title, string message,
        string confirmText = "Confirm", string cancelText = "Cancel")
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
    }

    [RelayCommand]
    private void Confirm() => RequestClose?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}
