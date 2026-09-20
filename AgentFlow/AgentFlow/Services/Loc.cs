using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentFlow.Services;

/// <summary>
/// Lightweight localization service with Chinese/English switching.
/// English is the default display language.
/// XAML usage: {Binding [Key], Source={x:Static s:Loc.Instance}}
/// </summary>
public sealed class Loc : ObservableObject
{
    public static Loc Instance { get; } = new();

    private static readonly Dictionary<string, (string En, string Zh)> Texts = new()
    {
        ["Run"] = ("Run", "运行"),
        ["Stop"] = ("Stop", "停止"),
        ["Save"] = ("Save", "保存"),
        ["Load"] = ("Load", "加载"),
        ["ClearLog"] = ("Clear", "清空日志"),
        ["Components"] = ("Components", "组件"),
        ["Properties"] = ("Properties", "Properties"),
        ["Parameters"] = ("Parameters", "参数"),
        ["Log"] = ("Logs", "日志"),
        ["DeleteNode"] = ("Delete", "删除节点"),
        ["Search"] = ("Search nodes...", "搜索节点..."),
        ["Ready"] = ("Ready", "就绪"),
        ["Running"] = ("Running...", "运行中..."),
        ["RunCompleted"] = ("Completed", "运行完成"),
        ["RunCancelled"] = ("Cancelled", "已取消"),
        ["RunFailed"] = ("Failed", "运行失败"),
        ["SavedTo"] = ("Saved", "已保存"),
        ["LoadedFrom"] = ("Loaded", "已加载"),
        ["NoWorkflowFile"] = ("workflow.json not found", "没有找到 workflow.json"),
        ["LoadFailed"] = ("Load failed", "加载失败"),
        ["NodesLoaded"] = ("Loaded {0} node types", "已加载 {0} 种节点"),
        ["ConnRejected"] = ("Connection rejected", "连接被拒绝"),
        ["ErrMustOutToIn"] = ("Must connect an output pin to an input pin", "必须从输出 Pin 连到输入 Pin"),
        ["ErrSelfConnect"] = ("Cannot connect a node to itself", "不能连接到自身"),
        ["ErrTypeMismatch"] = ("Type mismatch", "类型不匹配"),
        ["ErrPinOccupied"] = ("Input pin already connected", "输入 Pin 已有连接"),
        ["ErrorPrefix"] = ("Error", "错误"),
        ["DisplayName"] = ("Display Name", "显示名称"),
        ["Priority"] = ("Priority", "优先级"),
        ["ValidationFailed"] = ("Validation failed", "校验失败"),
    };

    private string _lang = "en";

    public string CurrentLanguage => _lang;

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (!Texts.TryGetValue(key, out var t)) return key;
        return _lang == "zh" ? t.Zh : t.En;
    }

    public string Fmt(string key, params object[] args) => string.Format(Get(key), args);

    public void Switch(string lang)
    {
        if (_lang == lang) return;
        _lang = lang;
        OnPropertyChanged("Item[]");      // Refresh all indexer bindings.
        OnPropertyChanged(nameof(CurrentLanguage));
    }
}
