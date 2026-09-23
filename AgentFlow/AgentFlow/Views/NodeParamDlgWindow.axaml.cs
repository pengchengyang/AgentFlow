using Avalonia.Controls;
using AgentFlow.ViewModels;

namespace AgentFlow.Views;

/// <summary>
/// 参数配置对话框窗口。仅负责展示与窗口生命周期：
/// 监听 ViewModel 的 RequestClose 事件来关闭窗口，不包含任何业务逻辑。
/// </summary>
public partial class NodeParamDlgWindow : Window
{
    public NodeParamDlgWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is NodeParameterDialogViewModel vm)
            vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool result)
    {
        if (DataContext is NodeParameterDialogViewModel vm)
            vm.RequestClose -= OnRequestClose;
        Close(result);
    }
}
