using AgentFlow.Models;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// Pin ViewModel：纯外观层，负责在画布上显示一个 pin 圆点和它的名称。
/// 不维护连接关系、不持有对端 pin 引用、不实现 Send/Receive——
/// 所有连接/断开/数据传输都由 <see cref="AgentFlow.Core.EditorGraph"/> 管理。
/// 非界面数据（名称/类型/方向）来自 <see cref="PinModel"/>（包装契约层 <see cref="AgentFlow.Contracts.BasePin"/>）。
/// </summary>
public partial class PinViewModel : ViewModelBase
{
    /// <summary>所属节点（GUI 视图模型）。</summary>
    public NodeViewModel Node { get; }

    /// <summary>领域模型（包装契约层 BasePin）。</summary>
    public PinModel Pin { get; }

    public string Name => Pin.Name;
    public Type DataType => Pin.DataType;
    public string TypeName => Pin.DataType.Name;
    public bool Required => Pin.Required;
    public bool IsInput => Pin.IsInput;
    public bool IsOutput => Pin.IsOutput;

    /// <summary>画布锚点位置（由 CanvasView 布局时写入）。</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>是否已连接（由 Core 层 EditorGraph 连接/断开时通知更新）。</summary>
    [ObservableProperty]
    private bool _isConnected;

    public PinViewModel(NodeViewModel node, PinModel pin)
    {
        Node = node;
        Pin = pin;
    }
}
