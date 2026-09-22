using Avalonia.Controls;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// 登录对话框窗口。仅负责展示与窗口生命周期：监听 ViewModel 的 RequestClose 事件关闭窗口，
/// 不含任何业务逻辑。
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