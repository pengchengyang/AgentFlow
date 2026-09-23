using AgentFlow.Contracts;

namespace AgentFlow.Models;

/// <summary>
/// Pin 领域模型：包装契约层 <see cref="BasePin"/>，向 UI 只读暴露 pin 的非界面数据
/// （名称 / 数据类型 / 方向 / 是否必填）。所有非 UI 元素都源自 <see cref="BasePin"/>。
/// </summary>
public sealed class PinModel
{
    /// <summary>底层契约定义（含 Send/Receive 钩子等非 UI 逻辑）。</summary>
    public BasePin basePin { get; }

    public string Name => basePin.Name;
    public Type DataType => basePin.DataType;
    public PinDirection Direction => basePin.Direction;
    public bool Required => basePin.Required;

    public bool IsInput => Direction == PinDirection.Input;
    public bool IsOutput => Direction == PinDirection.Output;

    public PinModel(BasePin definition)
    {
        basePin = definition;
    }
}
