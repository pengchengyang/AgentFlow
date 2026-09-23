using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// 通用确认对话框 ViewModel。点击 Confirm / Cancel（或按 Enter / Esc）分别触发
/// <see cref="RequestClose"/>（bool：true = 确认，false = 取消）。窗口展示与生命周期由 View 层处理。
/// </summary>
public partial class ConfirmDialogViewModel : ViewModelBase
{
    /// <summary>窗口标题。</summary>
    public string Title { get; }

    /// <summary>提示正文。</summary>
    public string Message { get; }

    /// <summary>确认按钮文案。</summary>
    public string ConfirmText { get; }

    /// <summary>取消按钮文案。</summary>
    public string CancelText { get; }

    /// <summary>关闭请求：true = 确认，false = 取消 / Esc。</summary>
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
