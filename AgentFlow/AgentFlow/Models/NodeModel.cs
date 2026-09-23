using AgentFlow.Contracts;

namespace AgentFlow.Models;

/// <summary>
/// Node 领域模型：包装契约层 <see cref="BaseNode"/>，向 UI 只读暴露节点的非界面数据
/// （类型 ID / 显示名 / 分类 / 实例 ID / 输入输出 pin / 参数）。
/// 纯 UI 相关元素（如画布坐标、选中态、层叠顺序）保留在 View/ViewModel 层。
/// </summary>
public sealed class NodeModel
{
    /// <summary>底层契约节点实例。</summary>
    public BaseNode Node { get; }

    public string TypeId => Node.TypeId;
    public string DisplayName => Node.DisplayName;
    public string Category => Node.Category;
    public int InstanceId => Node.InstanceId;

    public IReadOnlyList<PinModel> Inputs { get; }
    public IReadOnlyList<PinModel> Outputs { get; }
    public IReadOnlyList<ParameterDefinition> Parameters { get; }

    public NodeModel(BaseNode node)
    {
        Node = node;
        Inputs = node.InputPins.Select(p => new PinModel(p)).ToList();
        Outputs = node.OutputPins.Select(p => new PinModel(p)).ToList();
        Parameters = node.Parameters;
    }
}
