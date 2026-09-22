using System.Collections.Generic;
using AgentFlow.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>Available login roles, in descending permission order (Admin has the highest).</summary>
public enum Role { Admin, Engineer, Operator }

/// <summary>
/// 登录对话框 ViewModel。负责角色下拉、密码校验以及把角色口令保存到 .NET UserSettings。
/// 口令与角色名一致（Admin/Engineer/Operator）。窗口展示与生命周期由 View 层处理。
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
        // 把各角色口令保存到 .NET UserSettings；口令与角色名一致。
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

    /// <summary>校验口令：与角色名一致则登录成功；Admin 拥有最高权限。</summary>
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
