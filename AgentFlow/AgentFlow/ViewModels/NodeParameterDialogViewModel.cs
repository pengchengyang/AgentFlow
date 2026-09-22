using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentFlow.ViewModels;

/// <summary>
/// 双击节点弹出的“参数配置”对话框 ViewModel。
/// 拥有参数集合、确认/取消命令，并在确认时把编辑后的值写回节点。
/// 只负责业务/状态；窗口的展示与生命周期由 View 层处理。
/// </summary>
public partial class NodeParameterDialogViewModel : ViewModelBase
{
    /// <summary>被编辑的目标节点。</summary>
    public NodeViewModel Node { get; }

    /// <summary>对话框标题。</summary>
    public string Title => Node.Title;

    /// <summary>可编辑的参数行集合（编辑的是副本，确认才写回）。</summary>
    public ObservableCollection<ParameterEditRow> Parameters { get; } = new();

    /// <summary>是否没有任何参数（用于显示空状态提示）。</summary>
    public bool HasNoParameters => Parameters.Count == 0;

    /// <summary>请求关闭窗口：参数 true=确认，false=取消。由 View 层监听执行 Close。</summary>
    public event EventHandler<bool>? RequestClose;

    public NodeParameterDialogViewModel(NodeViewModel node)
    {
        Node = node;
        foreach (var p in node.Parameters)
            Parameters.Add(new ParameterEditRow(p.Key, p.Label, p.Value, p.TypeName));
    }

    /// <summary>确认：把编辑后的值写回节点参数，然后请求关闭。</summary>
    [RelayCommand]
    private void Confirm()
    {
        foreach (var row in Parameters)
        {
            var target = Node.Parameters.FirstOrDefault(p => p.Key == row.Key);
            if (target is not null)
                target.Value = row.Value;
        }
        RequestClose?.Invoke(this, true);
    }

    /// <summary>取消：不写回任何值，直接请求关闭。</summary>
    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

/// <summary>对话框里的一行参数编辑项（Key/Label 只读，Value 可绑定编辑）。</summary>
public partial class ParameterEditRow : ViewModelBase
{
    public string Key { get; }
    public string Label { get; }
    public string TypeName { get; }

    [ObservableProperty]
    private string _value;

    public ParameterEditRow(string key, string label, string value, string typeName)
    {
        Key = key;
        Label = label;
        TypeName = typeName;
        _value = value;
    }
}