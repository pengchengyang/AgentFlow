// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CloseConfirmDialogViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// Three-button close confirmation: Cancel (stay), Discard (exit without saving),
/// SaveAndExit (persist then close). Raises <see cref="RequestClose"/> with the chosen outcome.
/// </summary>
public partial class CloseConfirmDialogViewModel : ViewModelBase
{
    public string Title { get; }
    public string Message { get; }

    /// <summary>Result: 0 = cancel, 1 = discard/exit, 2 = save then exit.</summary>
    public event EventHandler<int>? RequestClose;

    public CloseConfirmDialogViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, 0);

    [RelayCommand]
    private void Discard() => RequestClose?.Invoke(this, 1);

    [RelayCommand]
    private void SaveAndExit() => RequestClose?.Invoke(this, 2);
}
