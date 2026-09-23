using Avalonia.Controls;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// 确认对话框窗口。仅负责展示与窗口生命周期：监听 ViewModel 的 <see cref="ConfirmDialogViewModel.RequestClose"/>
/// 事件关闭窗口，并返回确认结果（bool），不含任何业务逻辑。
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
