using Avalonia.Controls;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.DialogRequested += OnDialogRequested;
            vm.ClearRequested += OnClearRequested;
            vm.LoginRequested += OnLoginRequested;
        }
    }

    /// <summary>在顶层窗口之上打开参数配置对话框。仅做展示与窗口生命周期，不含业务逻辑。</summary>
    private async void OnDialogRequested(object? sender, NodeParameterDialogViewModel dialog)
    {
        var win = new NodeParamDlgWindow { DataContext = dialog };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    /// <summary>在顶层窗口之上打开清空画布确认对话框（modal）。仅做展示与窗口生命周期。</summary>
    private async void OnClearRequested(object? sender, ConfirmDialogViewModel dialog)
    {
        var win = new ConfirmDialogWindow { DataContext = dialog };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }

    /// <summary>在顶层窗口之上打开居中登录对话框（modal）。仅做展示与窗口生命周期。</summary>
    private async void OnLoginRequested(object? sender, LoginDialogViewModel dialog)
    {
        var win = new LoginDialogWindow { DataContext = dialog };
        if (TopLevel.GetTopLevel(this) is Window owner)
            await win.ShowDialog(owner);
        else
            win.Show();
    }
}

