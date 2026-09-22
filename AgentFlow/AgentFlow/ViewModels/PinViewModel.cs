using AgentFlow.Core;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.ViewModels;

/// <summary>
/// Pin ViewModel：纯外观层，只负责在画布上显示一个 pin 圆点和它的名称。
/// 不维护连接关系、不持有对端 pin 引用、不实现 Send/Receive——
/// 所有连接/断开/数据传输都由 <see cref="AgentFlow.Core.EditorGraph"/> 管理，
/// 输出 pin 通过 RuntimeOutputPin.Targets 直接持有输入 pin 引用，
/// 数据走 Send→Receive 穿透。
/// </summary>
public partial class PinViewModel : ViewModelBase
{
    /// <summary>所属节点（GUI 视图模型）。</summary>
    public NodeViewModel Node { get; }

    /// <summary>Core 层提供的静态元数据（名称/类型/方向/是否必填）。</summary>
    public PinDescriptor Definition { get; }

    public string Name => Definition.Name;
    public PinDirection Direction => Definition.Direction;
    public string TypeName => Definition.DataType.Name;
    public bool Required => Definition.Required;
    public bool IsInput => Direction == PinDirection.Input;
    public bool IsOutput => Direction == PinDirection.Output;

    /// <summary>画布锚点位置（由 CanvasView 布局时写入）。</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>是否已连接（由 Core 层 EditorGraph 连接/断开时通知更新）。</summary>
    [ObservableProperty]
    private bool _isConnected;

    public PinViewModel(NodeViewModel node, PinDescriptor definition)
    {
        Node = node;
        Definition = definition;
    }
}

