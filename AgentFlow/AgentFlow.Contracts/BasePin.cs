namespace AgentFlow.Contracts;

/// <summary>Pin direction.</summary>
public enum PinDirection
{
    Input,
    Output
}

/// <summary>
/// Pin 定义：节点的输入/输出端口契约。
/// <para>
/// 两种用法：
/// 1) 纯元数据（默认）：<c>new BasePin("Value", typeof(double), PinDirection.Output)</c>，
///    此时 Send/Receive 只做透传。
/// 2) 带自定义行为：子类化本类覆盖 <see cref="OnReceive"/> / <see cref="OnSend"/>，
///    或用 <see cref="Input{T}(string, Action{object?}?, bool)"/> / <see cref="Output{T}(string, Action{object?}?, bool)"/>
///    工厂传入 lambda，让具体节点在 pin 级别加入校验、清洗、序列化、日志等业务逻辑。
/// </para>
/// 节点之间的数据流：上游 OUTPUT pin 调 Send(value) → 对每个连接的 INPUT pin 调 Receive(value)，
/// 中间会依次经过 OnSend / OnReceive 钩子。
/// </summary>
public class BasePin
{
    /// <summary>Pin 名称（节点内唯一）。</summary>
    public string Name { get; }

    /// <summary>数据类型（连接校验用）。</summary>
    public Type DataType { get; }

    /// <summary>方向：输入还是输出。</summary>
    public PinDirection Direction { get; }

    /// <summary>是否必填（未连接时报校验错误）。</summary>
    public bool Required { get; }

    /// <summary>
    /// Pin 固定标识符：创建实例时生成，对每个 pin 实例（含继承自本类的运行时 pin）保持不变，
    /// 可用于在连接 / 序列化 / 匹配时稳定地标识同一个 pin。
    /// </summary>
    public string Uuid { get; }

    /// <summary>
    /// 已连接的下游输入 pin 引用（仅输出 pin 有意义；输入 pin 恒为 null）。
    /// 建立连接时由引擎赋值，初始化 / 断开连接时为 null。
    /// </summary>
    public BasePin? ConnectedInput { get; set; }

    public BasePin(string name, Type dataType, PinDirection direction, bool required = true)
    {
        Name = name;
        DataType = dataType;
        Direction = direction;
        Required = required;
        Uuid = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// INPUT pin：收到上游 OUTPUT pin 推来的数据时调用。
    /// 默认空实现；子类可覆盖以做数据校验、类型转换、日志、事件驱动唤醒等。
    /// </summary>
    /// <param name="value">上游推来的值（运行时可能为 null）。</param>
    public virtual void OnReceive(object? value) { }

    /// <summary>
    /// OUTPUT pin：数据发送到下游 INPUT pin 之前调用。
    /// 默认空实现；子类可覆盖以做序列化、脱敏、采样、日志等。
    /// </summary>
    /// <param name="value">即将发送给下游的值。</param>
    public virtual void OnSend(object? value) { }

    // ---- 便捷工厂：用 lambda 注入 pin 行为，无需写子类 ----

    /// <summary>声明一个输入 pin，可选传入收到数据时的回调。</summary>
    public static BasePin Input<T>(string name, Action<object?>? onReceive = null, bool required = true)
        => new DelegatePin(name, typeof(T), PinDirection.Input, required, onReceive, null);

    /// <summary>声明一个输出 pin，可选传入发送数据前的回调。</summary>
    public static BasePin Output<T>(string name, Action<object?>? onSend = null, bool required = true)
        => new DelegatePin(name, typeof(T), PinDirection.Output, required, null, onSend);

    /// <summary>内部实现：把 lambda 包成 BasePin。</summary>
    private sealed class DelegatePin : BasePin
    {
        private readonly Action<object?>? _onReceive;
        private readonly Action<object?>? _onSend;

        public DelegatePin(string name, Type dataType, PinDirection direction, bool required,
                          Action<object?>? onReceive, Action<object?>? onSend)
            : base(name, dataType, direction, required)
        {
            _onReceive = onReceive;
            _onSend = onSend;
        }

        public override void OnReceive(object? value) => _onReceive?.Invoke(value);
        public override void OnSend(object? value) => _onSend?.Invoke(value);
    }
}
