// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LoginDialogViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Generic;
using AgentFlow.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>Available login roles, in descending permission order (Admin has the highest).</summary>
public enum Role { Admin, Engineer, Operator }

/// <summary>
/// Login dialog ViewModel. Handles the role dropdown, password validation, and saving
/// role passwords to .NET UserSettings. The password equals the role name (Admin /
/// Engineer / Operator). Window presentation and lifecycle are handled by the View layer.
/// </summary>
public partial class LoginDialogViewModel : ViewModelBase
{
    private const string PasswordKeyPrefix = "Password.";

    private readonly UserSettings _settings;

    /// <summary>Roles shown in the dropdown.</summary>
    public IReadOnlyList<string> Roles { get; } = new[] { "Admin", "Engineer", "Operator" };

    [ObservableProperty]
    private string _selectedRole = nameof(Role.Admin);

    [ObservableProperty]
    private string _password = nameof(Role.Admin); // Dev: prefill Admin password so no typing needed.

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True when the last login attempt failed (drives the error message visibility).</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>Request close: bool result = true only when logged in as Admin.</summary>
    public event EventHandler<bool>? RequestClose;

    public LoginDialogViewModel()
    {
        // Save each role's password to .NET UserSettings; the password equals the role name.
        _settings = UserSettings.Load();
        foreach (var role in Roles)
        {
            if (string.IsNullOrEmpty(_settings.Get(PasswordKey(role))))
                _settings.Set(PasswordKey(role), role);
        }
        _settings.Save();
    }

    private static string PasswordKey(string role) => PasswordKeyPrefix + role;

    partial void OnSelectedRoleChanged(string value) => ErrorMessage = null;

    /// <summary>Validate the password: login succeeds when it matches the role name; Admin has the highest permission.</summary>
    [RelayCommand]
    private void Confirm()
    {
        var stored = _settings.Get(PasswordKey(SelectedRole), SelectedRole);
        if (string.Equals(Password, stored, StringComparison.Ordinal))
        {
            _settings.Set("CurrentRole", SelectedRole);
            _settings.Save();
            HasError = false;
            RequestClose?.Invoke(this, SelectedRole == nameof(Role.Admin));
        }
        else
        {
            ErrorMessage = "Incorrect password.";
            HasError = true;
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}
