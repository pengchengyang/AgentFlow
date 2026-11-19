# AgentFlow 多语言外部节点架构设计

- 状态：Implemented (initial release)
- 日期：2026-11-14
- 适用范围：AgentFlow Core / Desktop / CLI / 外部语言 Worker
- 当前阶段：Core / Python / C / C++ SDK、示例、CLI 多实例隔离、Python 独立 EXE、C/C++ Release 静态打包、主工程增量自动构建和 PATH 隔离干净环境验收均已完成

---

## 0. 初版实现状态

已实现：

- 运行时 `Plugins/External/<当前标准 RID>/` 动态扫描、manifest 校验、`NodeRegistry` 工厂接入。
- `ExternalNodeProxy`，对外表现为普通 `BaseNode`。
- Windows Named Pipe / Linux Unix Domain Socket NDJSON 传输。
- `hello / welcome / register / registered / ready / heartbeat` 握手。
- `node.create / configure / command / state / error` 生命周期。
- `pin.receive / pin.send`。
- `broadcast.subscribe / unsubscribe / publish / receive`，与当前运行实例的 `BroadcastKit.ITopicBus` 双向桥接；Batch Run 使用实例级 TopicBus，Editor Run 回退到进程级 `TopicBus.Instance`。
- Worker 心跳超时、进程退出状态、注册声明与 manifest 一致性校验。
- Python SDK 与 `Node.PythonUpper`，支持生命周期、Pin、广播、日志、心跳。
- C SDK（C ABI）与 `Node.CUpper` 示例，Windows/Linux 条件编译传输。
- C++ 薄封装与 `Node.CppUpper` 示例。
- FakeCore 工具：`Tools/ExternalWorkerTestClient`，已验证 Python、C、C++ Worker 的生命周期、Pin 和双向广播桥接。
- C/C++ SDK 与示例已使用 MSVC/CMake 实际编译，并完成 FakeCore 端到端验证。
- CLI/Desktop Batch Run 的每个工作流实例都会创建独立 `ExternalNodeRuntime` 和独立 TopicBus；同 Package 两个并发实例会启动两个独立 Worker 进程，广播订阅也互不可见。
- 工作流加载完成后会预启动其使用的外部 Worker，并创建 Worker 侧节点实例以建立广播订阅；不会调用 `init/run`，运行前 `InitializeAsync` 仍会兜底确认 Worker 已就绪。
- GUI 节点属性对话框已增加只读 Package 信息区，显示 Package ID、版本、作者、版权、许可证、仓库等 manifest 元数据。

已完成发布链路：

- 统一编排脚本 `Scripts/build_external_nodes.py` 已支持 doctor / discover / build / verify / package / clean。
- `Node.PythonUpper` 已使用 PyInstaller 6.22.3 onedir 打包为独立 EXE，产物包含 `Worker/Node.PythonUpper.exe` 与完整 `_internal/`。
- C/C++ Worker Runtime 已改为静态库；Windows Release 使用 `/MT`，dumpbin 检查只依赖系统 `KERNEL32.dll`。
- Release 产物已生成 `ReleaseManifest.json`、`README.txt`、`build-report.json`、`verification.log` 和 `checksums.sha256`。
- 已在 ZIP 解压后的独立目录中，用排除 Python / CMake / 编译器路径的 PATH 完成三种 Worker 的 FakeCore 生命周期、Pin 和双向广播验证。
- `package-sdk-dll.py` 已把 Debug/Release 外部节点按标准 RID 收进各自的 `SharedDlls/x64/<Configuration>/Plugins/External/<RID>`，Release 在入库前执行 FakeCore 验证。

仍需后续处理：

- 本轮“干净环境”是 PATH 隔离 + ZIP 解压独立目录验证，不是全新 Windows 虚拟机/Windows Sandbox 的物理级验收；正式对外发版前仍建议补一次真实干净 VM 验收。
- Worker 崩溃自动重启第一版不实现。

---
---

## 0.1 当前 SDK 简化 API（Implemented）

外部语言业务节点现在分为两层：

- **Simplified API**：开发者只写业务回调和必要的上下文操作。
- **Advanced API**：底层 Runtime、传输、队列、生命周期分发以及原有高级接口继续保留。

命名规则：

- SDK 目录使用 `Python`、`C`、`Cpp`。
- Python 包使用 `AgentFlowWorker`，模块与类使用 PascalCase。
- C/C++ 源文件和公开头文件使用 PascalCase，例如 `AgentFlowNode.c`、`AgentFlowNode.h`、`AgentFlowNode.hpp`。
- 新公开函数、类型、回调和示例入口使用 PascalCase。
- NDJSON 协议字段和消息类型属于协议契约，不随 SDK 命名规则改名。

### Python Simplified API

~~~python
from AgentFlowWorker import ExternalNode, NodeContext, RunWorker, Sample

class UpperNode(ExternalNode):
    def OnSample(self, context: NodeContext, pin: str, sample: Sample | None) -> None:
        if pin != "in":
            context.EmitError("PIN_UNKNOWN", f"unknown input pin {pin}")
            return
        value = "" if sample is None else sample.as_string()
        context.SendString("out", value.upper(), "out")

if __name__ == "__main__":
    raise SystemExit(RunWorker({"Node.PythonUpper": UpperNode}))
~~~

可选业务回调为：`Configure / Init / Run / Stop / Shutdown / OnSample / OnBroadcast`。

### C Simplified API

~~~c
static void UpperOnSample(
    AgentFlowNodeContext *Context,
    const char *Pin,
    const AgentFlowSample *Sample) {
    (void)Pin;
    const char *value = Sample != NULL && Sample->string_value != NULL
        ? Sample->string_value : "";
    AgentFlowSendString(Context, "out", value, "out");
}

int main(int argc, char **argv) {
    AgentFlowNodeDefinition definition = {0};
    definition.NodeType = "Node.CUpper";
    definition.Callbacks.OnSample = UpperOnSample;
    return AgentFlowRunNodes(&definition, 1, argc, argv);
}
~~~

C 节点实例状态通过 `StateSize`、`StateInit`、`StateDestroy` 管理，并在回调中通过
`AgentFlowGetState(Context)` 获取。禁止用进程级静态变量保存节点实例业务状态。

### C++ Simplified API

~~~cpp
class UpperNode final : public AgentFlow::ExternalNode {
public:
    void OnSample(AgentFlow::NodeContext& context,
                  const std::string& pin,
                  const AgentFlow::Sample& sample) override {
        if (pin != "in") return;
        context.SendString("out", sample.AsString(), "out");
    }
};

int main(int argc, char** argv) {
    AgentFlow::NodeApplication application;
    application.RegisterNode<UpperNode>("Node.CppUpper");
    return application.Run(argc, argv);
}
~~~

三个示例的打包入口统一为 `Worker/Main.py`、`Worker/Main.c`、`Worker/Main.cpp`。

已经验证：

- Python / C / C++ SDK 均通过 Release 构建。
- 三个 Demo 均通过 PATH 隔离下的 FakeCore 生命周期、Pin、双向广播验证。
- Python 业务节点仅依赖 `ExternalNode` 和 `RunWorker`，C/C++ 业务节点仅依赖
  `AgentFlowNode` 高层封装；底层协议保持高级 API。
## 1. 决策摘要

| 项目 | 决策 |
|---|---|
| 外部节点发现方式 | 运行时动态发现 |
| 外部节点存放位置 | `Plugins/External/<RID>/` 下，按包目录组织 |
| 外部节点加载方式 | 扫描 manifest，不通过普通 C# DLL 插件反射加载 |
| GUI 节点类型 | 每个外部节点类型独立展示，不使用一个通用节点表示所有外部节点 |
| 外部节点代理 | Core 中创建 `ExternalNodeProxy`，对外仍表现为普通 `BaseNode` |
| Core 修改方式 | 采用增量扩展点，不重构工作流、Pin 路由和生命周期调度 |
| Worker 进程粒度 | 一个 Worker 包对应一个 Worker 进程，可承载多个节点实例 |
| 打包粒度 | 每个节点独立打包；一个 Package 首版建议只声明一个节点类型 |
| NodeCommand | 第一版不支持 |
| `GetService<T>` | 第一版不支持 |
| Pin 动态增加/删除 | 第一版不支持 |
| 广播 | 运行期间尽力投递，Core 统一转发 |
| 进程启动时机 | 加载工作流时启动被该工作流使用的外部 Worker |
| Stop 行为 | 停止节点运行，但保留 Worker 进程，允许再次 Run |
| Shutdown 行为 | 关闭 Worker 进程并释放资源 |
| 生产传输建议 | Windows Named Pipe；Linux Unix Domain Socket |
| 通信格式 | NDJSON，UTF-8，每条消息一行 |
| Sample 传输 | 语言中立结构；FLOAT/DOUBLE 优先使用 IEEE 754 位模式 |

---

## 2. 目标与非目标

### 2.1 目标

- 允许 Python、C、C++ 等外部语言编写节点。
- 外部节点在 GUI 中作为独立节点类型出现。
- 外部节点具备与 C# `BaseNode` 一致的 `Initialize/Run/Stop/Shutdown` 生命周期。
- 外部节点可以读写普通 Pin。
- 外部节点可以参与 Core 统一广播。
- Core 仍负责工作流图、Pin 路由、广播 TopicBus、节点状态和运行调度。
- 外部 Worker 不直接连接其他 Worker。
- 每个外部节点可以独立打包和部署。
- 一个 Worker 进程可以承载多个节点实例，并通过 `nodeInstanceId` 隔离消息。
- 不破坏现有 C# 插件加载方式和现有工作流运行逻辑。

### 2.2 非目标

第一版不实现：

- 外部节点运行时动态增加/删除 Pin。
- 外部节点 NodeCommand 右键菜单命令。
- 外部节点任意 `GetService<T>`。
- Worker 之间直接通信。
- 跨机器 Worker。
- 广播持久化和可靠重试。
- 复杂事务、分布式一致性。
- 外部节点热加载代码后保持运行状态。

---

## 3. 术语

| 术语 | 含义 |
|---|---|
| Core | AgentFlow Core，负责工作流图、节点生命周期、Pin 路由和广播 TopicBus |
| 内部节点 | 现有 C# DLL 插件节点 |
| 外部节点 | Python/C/C++ 等非 .NET 节点 |
| Package | `Plugins/External/<RID>/<package-id>/` 下的一个独立外部节点包 |
| Worker | Package 启动出的外部语言进程 |
| Proxy | Core 内的 `ExternalNodeProxy : BaseNode`，代表一个外部节点实例 |
| WorkerSupervisor | 负责启动、监督、停止 Worker 进程的组件 |
| nodeInstanceId | Core 分配给节点实例的 ID，沿用现有 BaseNode.InstanceId |
| workerId | Core 分配给一个 Worker 连接的 ID |
| runId | 一次工作流运行 ID |
| Topic | 广播主题 |
| Sample | Pin 上传输的类型化数据 |

### 3.1 命名规则

建议统一采用以下规则：

| 对象 | 规则 |
|---|---|
| packageId | 全局唯一，建议使用点分层级名称，例如 `Node.PythonUpper` |
| nodeType | 全局唯一，建议与 packageId 保持稳定关联，例如 `Node.PythonUpper` |
| Pin 名称 | Package 内稳定，节点内唯一，建议使用小写短名称，如 `in`、`out` |
| 参数名称 | 节点内唯一，建议保持大小写稳定，保存后不随意改名 |
| Topic 名称 | 全局稳定，建议使用点分层级名称，如 `FACT.UserSettings` |
| workerId | Core 分配的进程连接标识，不是 Package ID |
| nodeInstanceId | Core 分配的节点实例 ID，沿用 `BaseNode.InstanceId` |
| runId | 一次工作流运行实例的 ID |
| manifestVersion | manifest 文件格式版本 |
| protocolVersion | Worker 与 Core 的通信协议版本 |
| sdkVersion | 外部语言 Worker SDK 版本 |
| packageVersion | 外部节点包业务版本 |

约束：

- `packageId`、`nodeType` 不允许包含路径分隔符。
- `nodeType` 按现有注册规则进行不区分大小写的重复检查。
- Pin 和参数名称一旦进入工作流，应视为兼容性契约。
- 修改 Pin 名称、方向或 DataType 会影响已保存工作流，必须按版本升级处理。
- Topic 名称不得随手改名，否则已有广播订阅会失效。

### 3.2 协议术语和 UI 功能术语区分

需要注意两个容易混淆的概念：

```text
node.command
    外部协议中的生命周期命令，承载 init / run / stop / shutdown

NodeCommand
    项目现有 C# BaseNode 的一个 UI 右键菜单功能
```

第一版外部节点不支持 UI `NodeCommand`。协议中如果继续使用 `node.command`，文档和代码中必须注明它只表示生命周期命令，不代表菜单命令功能。

---

## 4. 整体架构

```text
+------------------------------+
|          AgentFlow Core      |
|                              |
|  Workflow Graph              |
|  Pin Routing                 |
|  Node Lifecycle              |
|  BroadcastKit / TopicBus     |
+--------------+---------------+
               |
               | 注册 NodeDescriptor / 创建 ExternalNodeProxy
               |
+--------------v---------------+
| AgentFlow.MultilingualHost   |
|                              |
| ExternalNodeCatalog          |
| WorkerSupervisor             |
| Named Pipe / UDS Transport   |
| NDJSON Protocol              |
+--------------+---------------+
               |
               | 启动、监督、通信
               |
     +---------+---------+----------------+
     |                   |                |
     v                   v                v
Python Worker       C++ Worker        C Worker
     |                   |                |
     +-------------------+----------------+
               Core 统一路由 Pin 和 Broadcast
```

### 4.1 Core 职责

Core 保持以下职责：

- 工作流图结构。
- Pin 连接关系。
- `Initialize/Run/Stop/Shutdown` 调度。
- 节点状态和失败处理。
- Pin 点对点路由。
- 广播 TopicBus 和订阅关系。
- 日志和运行控制。

Core 不负责：

- 外部进程启动细节。
- Named Pipe/UDS/stdio 具体实现。
- Python/C/C++ 语言 SDK。
- 跨语言 JSON 序列化细节。

### 4.2 新增动态库职责

建议新增项目：

```text
AgentFlow.MultilingualHost
```

首版可以是一个动态库，职责如下：

1. 扫描 `Plugins/External/<当前标准 RID>/`。
2. 读取并校验 `plugin.json`。
3. 生成外部节点描述。
4. 启动 Worker 进程。
5. 建立 Named Pipe/UDS 连接。
6. 收发 NDJSON。
7. 监督 Worker 退出、超时和崩溃。
8. 维护 Worker 与多个节点实例的映射。
9. 将广播转发给 Core Broadcast Gateway。

该动态库不直接修改工作流图，也不直接连接 Worker 之间的业务 Pin。

### 4.3 ExternalNodeProxy 职责

Core 内每个外部节点实例对应一个：

```text
ExternalNodeProxy : BaseNode
```

Proxy 对 Core 来说就是普通节点：

- 有普通输入 Pin。
- 有普通输出 Pin。
- 有普通 `NodeParameter`。
- 实现 `Initialize/Run/Stop/Shutdown`。
- 通过 `ReceiveSample` 把上游输入转发给 Worker。
- 收到 Worker 的 `pin.send` 后调用输出 Pin。
- 收到广播后按 Core TopicBus 语义处理。

---

## 5. 目录和动态发现

### 5.1 目录结构

源码目录和大写规则：

```text
Plugins/
    Node.TcpClient/
    Node.FACTLauncher/
    ...
Plugins/External/
    Python/
        Node.PythonUpper/
    C/
        Node.CUpper/
    Cpp/
        Node.CppUpper/
```

构建产物复制到每个宿主输出目录的 `Plugins/External/<RID>/`：

```text
Plugins/
    External/
        win-x64/
            Node.PythonUpper/
                plugin.json
                README.md
                Licenses/
                Worker/
                    Node.PythonUpper.exe
                    _internal/
                        ...
            Node.CUpper/
                plugin.json
                README.md
                Licenses/
                Worker/
                    Node.CUpper.exe
        linux-x64/
            Node.PythonUpper/
            Node.CUpper/
        linux-arm64/
        linux-arm/
```

支持的标准 RID：

```text
win-x64
win-arm64
linux-x64
linux-arm64
linux-arm
```

`Plugins/External` 下同时保存按语言分类的源码和构建清单，以及按 RID 分类的运行时产物。源码放在 `Python/`、`C/`、`Cpp/` 等目录；运行时产物只放在 `win-x64/`、`linux-x64/` 等 RID 子目录。不要直接修改 RID 产物目录。

### 5.1.1 Worker Package 自包含规则

每个外部节点 Package 是一个独立文件夹，`Worker/` 是完整部署单元。EXE、DLL/SO、Python 打包目录、资源文件和运行时依赖全部放在 Package 内。

整个 `Worker/` 目录必须作为一个整体复制、安装、升级和删除，不能只复制 EXE，也不能依赖 Package 外部的文件。

自包含要求：

- `plugin.json` 只使用相对路径。
- Worker 启动参数中的 `${WORKER_ENDPOINT}` 和 `${WORKER_TOKEN}` 由 Core 替换。
- EXE 和它需要的 DLL/SO 全部位于当前 Package 的 `Worker/` 内。
- Python PyInstaller `onedir` 产物必须完整复制，包括 EXE、`_internal/`、数据文件和第三方原生 DLL。
- 不能依赖开发机绝对路径、当前工作目录或全局 `PATH`。
- 不能在发布包中混放 Debug 和 Release 文件。
- 不能用未经版本控制的共享 DLL 目录代替 Package 依赖。
- 许可证、NOTICE 和第三方依赖声明应随 Package 一起发布。

如果 DLL/SO 放在 `Worker/lib/` 等子目录：

- Windows 必须配置 DLL 搜索目录或程序集清单，不能依赖全局 `PATH`。
- Linux 必须配置 `RPATH` / `$ORIGIN`。
- macOS 必须配置 `@loader_path`。

第一版按“每个节点独立打包”处理：

```text
一个 Package = 一个 Worker 可执行文件包
一个 Package 首版建议只声明一个 nodeType
```

如果后续一个 Worker 承载多个 nodeType，这些 nodeType 必须共享同一个 Package 和同一个 `Worker/` 目录，不能按 nodeType 再拆分出互相独立的依赖目录。

说明：

- `Plugins/External/` 不是内部 C# DLL 插件扫描目录。
- 当前内部 PluginLoader 只枚举 `Plugins/` 根目录 DLL，不递归扫描子目录。
- 外部节点扫描器只读取 `Plugins/External/<当前标准 RID>/`，不会加载错误平台的 Worker。
- 如果以后 PluginLoader 改为递归扫描，必须显式排除 `Plugins/External/`。
- 外部节点不会要求 Core 加载其 Python/C/C++ 代码为 .NET DLL。

### 5.2 Package 规则

`Plugins/External/<当前标准 RID>/` 下每个直接子目录就是一个 Package。

建议一个 Package 首版只声明一个节点类型：

```text
Node.PythonUpper
```

未来允许一个 Package 声明多个节点类型，但必须明确版本和兼容性。

扫描规则：

1. 先按当前进程操作系统和 CPU 架构解析标准 RID，再只扫描 `Plugins/External/<RID>` 的直接子目录。
2. Package 目录必须包含 `plugin.json`。
3. `plugin.json` 必须声明 `manifestVersion`、`packageId`、`nodeTypes`、`worker`。
4. Package 目录名和 `packageId` 应一致。
5. Worker 可执行文件路径必须相对于 Package 目录。
6. 扫描阶段只读取 manifest，不执行 Worker。
7. manifest 校验失败的 Package 只记录错误，不影响其他 Package。
8. 节点类型名重复时，必须报错并拒绝注册。
9. 外部节点类型不能覆盖同名 C# 内部节点。

### 5.2.1 plugin.json 正式字段规范

第一版应维护独立的 `plugin.schema.json`，对 `plugin.json` 做机器校验。文档中的示例不能替代 schema。

顶层字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `manifestVersion` | integer | 是 | 首版固定为 `1` |
| `packageId` | string | 是 | Package 全局唯一 ID |
| `displayName` | string | 是 | GUI 显示名称 |
| `version` | string | 是 | Package 业务版本，建议 SemVer |
| `description` | string | 否 | Package 说明 |
| `metadata` | object | 否 | Package 的作者、版权、许可证等展示元数据，见 5.2.2 |
| `worker` | object | 是 | Worker 启动配置 |
| `nodeTypes` | array | 是 | 至少包含一个节点类型 |

Worker 字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `protocolVersion` | integer | 是 | 外部协议主版本 |
| `executable` | string | 是 | 相对于 Package 根目录 |
| `arguments` | string[] | 是 | 可包含 `${WORKER_ENDPOINT}`、`${WORKER_TOKEN}` |
| `workingDirectory` | string | 否 | 相对于 Package 根目录 |
| `startTimeoutMs` | integer | 否 | Worker 启动和注册超时 |
| `heartbeatIntervalMs` | integer | 否 | 心跳周期 |
| `capabilities` | string[] | 否 | 声明支持的协议能力 |

nodeType 字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `nodeType` | string | 是 | 节点类型全局唯一 ID |
| `displayName` | string | 是 | 节点面板显示名称 |
| `category` | string | 是 | 节点分类 |
| `description` | string | 否 | 节点说明 |
| `pins` | array | 是 | 固定 Pin 列表 |
| `parameters` | array | 否 | 参数列表 |
| `subscriptions` | string[] | 否 | 默认订阅的广播 Topic |

Pin 字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `name` | string | 是 | Pin 名，节点内唯一 |
| `direction` | `input` / `output` | 是 | Pin 方向 |
| `dataType` | string | 是 | STRING、INT32、INT64、BOOL、FLOAT、DOUBLE、BIN |
| `required` | boolean | 否 | 是否强制连接，默认 `true` |
| `description` | string | 否 | Pin 用途说明 |
| `allowNull` | boolean | 否 | 是否允许 null Sample，默认 `true` |

参数字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `name` | string | 是 | 参数名，节点内唯一 |
| `type` | string | 是 | `string`、`bool`、`int32`、`int64`、`float`、`double` |
| `displayName` | string | 否 | 属性面板显示名 |
| `description` | string | 否 | 参数说明 |
| `defaultValue` | any | 否 | 必须与 `type` 一致 |
| `editable` | boolean | 否 | 是否允许编辑，默认 `true` |
| `zone` | string | 否 | 属性面板左侧分区 |
| `group` | string | 否 | 分区内分组 |
| `editor` | string | 否 | `auto`、`text`、`singleSelect`、`multiSelect` |
| `options` | string[] | 条件必填 | 单选/多选参数必须提供 |
| `sensitive` | boolean | 否 | 是否为敏感参数，默认 `false` |

校验规则：

- `packageId`、`nodeType` 不得为空。
- `worker.executable` 必须存在，且路径必须位于 Package 目录内。
- 所有 Pin 名称必须在节点内唯一。
- 所有参数名称必须在节点内唯一。
- 输入 Pin 和输出 Pin 的 `dataType` 必须是已支持类型。
- `required` 只表示工作流连接校验，不代表 Worker 必须等待该 Pin 有值。
- 第一版不允许动态 Pin，manifest 中不能存在动态 Pin 声明。
- 参数默认值必须能转换为声明类型。
- 单选/多选参数的 `options` 必须非空且无重复值。
- Worker 注册信息中的 Package、nodeType、Pin 和参数必须与 manifest 一致。
- 不一致时 Package 加载失败并上报明确错误，不允许静默修正。
- `metadata` 中的路径字段必须是 Package 内相对路径；`licenseFile` 声明后文件必须存在。
- `license` 建议使用 SPDX 标识符；未知许可证值应记录警告。
- `version`、`metadata.copyright` 和 `metadata.license` 不允许是空白字符串。

GUI 只根据 manifest 显示节点、Pin 和参数。Worker 注册用于二次校验，不能反过来替代 manifest。

### 5.2.2 Package 元数据、版权和二进制版本信息

外部节点的版本、作者、版权、许可证等信息必须区分两个层面，不能混为一谈。

#### 5.2.2.1 AgentFlow 节点属性与详情

AgentFlow GUI 中显示的节点属性、Package 详情和版权信息，统一以 `plugin.json` 的 `metadata` 为唯一数据源。Python、C、C++ 节点的业务代码不负责提供这些静态元数据，Core 读取 manifest 后将其附加到 `ExternalNodeDefinition` 和 `ExternalNodeProxy`，GUI 直接显示。

推荐结构：

```json
{
  "version": "1.0.0",
  "description": "将输入字符串追加后缀",
  "metadata": {
    "publisher": "Damon Yang",
    "author": "Damon Yang",
    "authorEmail": "damon_ypc@126.com",
    "copyright": "Copyright (c) 2026 Damon Yang. All rights reserved.",
    "license": "MIT",
    "licenseFile": "Licenses/LICENSE",
    "homepage": "https://example.com",
    "repository": "https://github.com/example/agentflow-external"
  }
}
```

`metadata` 字段：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `publisher` | string | 否 | 发布者或组织名称 |
| `author` | string | 否 | 作者或维护者名称 |
| `authorEmail` | string | 否 | 作者联系邮箱 |
| `copyright` | string | 否 | 版权声明，建议包含年份和权利人 |
| `license` | string | 否 | SPDX 许可证标识符，例如 `MIT`、`Apache-2.0` |
| `licenseFile` | string | 否 | Package 内许可证文件相对路径 |
| `homepage` | string | 否 | 项目主页 URL |
| `repository` | string | 否 | 源码仓库 URL |

约定：

- 元数据属于 Package，所有 `nodeTypes` 默认继承，不在每个节点类型中重复版本和版权。
- 节点级 `description` 只描述节点用途；Package 级 `description` 描述整个外部节点包。
- `metadata` 是 GUI 展示和包管理数据的权威来源。
- Worker 的注册信息只需要报告 `packageVersion`、`sdkVersion`、`protocolVersion` 等运行时信息，不能用 Worker 上报值覆盖 manifest。
- GUI 节点属性面板建议展示：Package ID、节点类型、包版本、作者/发布者、版权、许可证、主页、Worker 语言、协议版本、SDK 版本和可执行文件路径。
- 不建议在节点实例中允许用户修改这些只读元数据。

#### 5.2.2.2 Windows EXE/DLL 文件属性

Windows 文件资源管理器中的“属性 -> 详细信息”属于二进制资源元数据，与 `plugin.json` 是两套独立数据，需要各语言在构建时写入。

Python 使用 PyInstaller 打包时，通过 `--version-file version_info.txt` 或 `.spec` 中的 `version=` 写入 Windows EXE 的 `CompanyName`、`FileDescription`、`FileVersion`、`ProductName`、`ProductVersion`、`LegalCopyright` 和 `OriginalFilename` 等字段。

C 和 C++ 使用同一个 Windows 资源脚本，例如 `resources/version.rc`，在其中定义 `VS_VERSION_INFO`，并在 CMake 中把资源文件加入目标：

```cmake
target_sources(AgentFlow.CppUpper PRIVATE resources/version.rc)
```

MSVC 会编译该资源并写入 EXE；MinGW 路径可使用 `windres`，但第一版不以 MinGW 为主。

Linux 没有 Windows 那种统一的文件“详细信息”属性页。Linux Package 的元数据以 `plugin.json`、`Licenses/LICENSE`、`THIRD_PARTY_NOTICES.txt` 以及可选的 `Worker --version` 输出为准，不要求依赖 ELF 自定义字段。

构建脚本应将 `plugin.json.version`、`metadata.copyright`、Python/C/C++ 写入的 EXE 版本资源进行一致性检查。发现不一致时应报告错误或至少报告警告，但 AgentFlow 运行时始终以 `plugin.json` 为准，不允许 GUI 直接解析 PE 资源。

第三方依赖的版权和许可证不能被节点作者改写。每个 Package 应至少包含：

```text
Licenses/
    LICENSE
    THIRD_PARTY_NOTICES.txt
```

Python Runtime、PyInstaller、C/C++ 静态链接库和 Windows MSVC Runtime 等分发要求必须在 `THIRD_PARTY_NOTICES.txt` 中记录。

### 5.3 动态发现与注册

当前内部插件路径：

```text
PluginLoader.LoadFromDirectory(Plugins)
    -> 反射扫描 DLL
    -> 发现 BaseNode 子类
    -> 注册 NodeDescriptor
```

外部节点路径：

```text
ExternalNodeCatalog.LoadFromDirectory(Plugins/External/<RID>)
    -> 枚举 Package 目录
    -> 读取 plugin.json
    -> 校验 manifest
    -> 生成 ExternalNodeDefinition
    -> 为每个 nodeType 生成 NodeDescriptor
    -> 注册到同一个 NodeRegistry
```

GUI 和 WorkflowHost 共享同一个 `NodeRegistry`，因此：

- GUI 节点面板无需区分内部节点和外部节点。
- 工作流保存的节点类型名仍然使用同一个 node type id。
- 加载工作流时通过 NodeRegistry 找到对应 descriptor。
- 节点实例仍由 Core 分配 `InstanceId`。

### 5.4 为什么需要 Core 增量扩展点

当前 `NodeDescriptor` 主要以 `Type` 表示节点类并通过 `Activator.CreateInstance` 创建实例。

动态外部节点需要：

```text
同一个 ExternalNodeProxy 类型
+ 不同 manifest definition
= 不同的外部节点类型
```

因此建议采用“方案 C”的增量扩展方式：

- `NodeDescriptor` 增加可选的实例工厂。
- 工厂可以接收 `ExternalNodeDefinition` 并创建带正确 `Name/DisplayName/Pins/Parameters` 的 Proxy。
- 现有内部插件继续使用 `Activator.CreateInstance`，行为不变。
- 外部 NodeDescriptor 使用工厂创建实例。
- WorkflowHost 增加一个加载外部节点目录的入口。

这是增量扩展，不改变：

- 现有内部插件加载流程。
- 工作流图结构。
- Pin 路由。
- 广播语义。
- Run/Stop/Shutdown 的调度顺序。

### 5.5 启动时机

用户已决定：加载工作流时启动 Worker，因为此时可能需要广播。

建议定义：

1. 打开工作流或加载工作流定义时，扫描 `Plugins/External/<当前标准 RID>`。
2. 工作流中实际使用的外部节点才启动对应 Package 的 Worker。
3. 一个工作流实例中，同一个 Package 只启动一个 Worker 进程。
4. 该 Worker 进程承载此 Package 的多个节点实例。
5. Worker 启动后立即注册并订阅广播，不等待 `Run`。
6. `Initialize` 仍然只在工作流开始运行阶段调用。
7. 工作流关闭、应用退出或节点被永久删除时发送 `shutdown`。

资源影响：

- 加载工作流就会启动外部进程，可能增加启动时间和内存占用。
- 首版接受该成本，以支持加载阶段的广播订阅。
- 如果以后需要延迟启动，可以增加 manifest 配置 `startMode: load | firstRun`。

### 5.6 没有外部节点时的原生快速路径

如果 `Plugins/External/<当前标准 RID>/` 不存在、为空、没有 `plugin.json` 或没有有效外部节点定义，必须直接走原生 C# 路径：

- 不创建 `ExternalNodeCatalog`。
- 不初始化 `MultilingualHost`。
- 不创建 WorkerSupervisor。
- 不创建 Named Pipe / UDS。
- 不加载外部协议组件。
- 不启动外部进程。
- 只加载原生 C# 插件。
- 只注册 C# `NodeDescriptor`。
- WorkflowHost 完全沿用现有原生节点调度路径。

建议判断逻辑：

```text
HasExternalPackages == false
    -> 原生 C# 快速路径
```

目录不存在是正常情况，不应上报 Error，最多记录 Debug/Info。

即使存在外部 Package，也要根据当前工作流是否实际包含外部 nodeType 决定是否启动 Worker：

```text
扫描到外部 Package
    ↓
当前工作流是否包含外部 nodeType？
    ↓ 否
不启动 Worker，不创建 Pipe，只运行原生节点

    ↓ 是
启动对应 Package 的 Worker
创建对应 Named Pipe / UDS
创建 ExternalNodeProxy
```

建议使用明确开关：

```text
ExternalNodesEnabled =
    ExternalPackagesFound &&
    WorkflowContainsExternalNodeType
```

不能把所有工作流都绑定到外部节点系统的初始化流程。

### 5.7 外部 Package 扫描、刷新和热加载

第一版建议：

```text
应用启动时扫描一次 Plugins/External/<当前标准 RID>
+ 提供手动刷新按钮
不在工作流运行中热加载外部 Package
```

规则：

- 应用启动时扫描外部 Package，并注册到 NodeRegistry。
- GUI 节点面板刷新时重新扫描 `Plugins/External/<当前标准 RID>`。
- 工作流运行期间禁止新增、删除或替换外部 Package。
- manifest 修改后必须手动刷新或重启应用。
- Package 无效时只记录错误，不影响其他 Package 和原生 C# 节点。
- 删除 Package 前必须确认该 Package 没有运行中的 Worker。
- 替换 Package 前必须先停止相关工作流。
- 如果工作流引用了已删除的 nodeType，加载时给出明确缺失节点错误。
- Worker 已启动时，不能通过修改 manifest 动态改变已有 Pin 和参数定义。
- 后续如需热加载，必须设计版本号、实例迁移和 Worker 重启策略；第一版不实现。

### 5.8 ExternalNodeCatalog 与 WorkflowHost 集成规则

建议扫描结果结构：

```text
ExternalCatalogResult
├─ Packages
├─ NodeDescriptors
├─ Diagnostics
└─ HasValidExternalNodes
```

规则：

- `ExternalCatalogResult.HasValidExternalNodes=false` 时，WorkflowHost 不注入 MultilingualHost。
- 单个 Package 损坏只加入 Diagnostics，不使原生 C# 插件加载失败。
- Package 扫描顺序稳定，避免 GUI 节点顺序随机变化。
- 同名 nodeType 必须在 NodeRegistry 注册前检测并拒绝。
- 外部 nodeType 与 C# 原生 nodeType 冲突时拒绝外部节点。
- NodeDescriptor 工厂必须绑定到对应 ExternalNodeDefinition。
- `ExternalNodeProxy` 创建时必须携带 manifest 的 Pin 和参数元数据。
- WorkflowHost 使用惰性依赖：只有工作流实际出现外部 nodeType 才创建 WorkerSupervisor。
- GUI 刷新时替换 catalog，不替换正在运行的 WorkflowHost 外部实例。
- 删除 Package 后，旧工作流加载必须报告 `NODE_TYPE_MISSING`。
- Package 目录不存在、为空或无有效 manifest 时，返回空结果而不是异常。
- 任何外部组件初始化失败都不能影响纯 C# 工作流。

---

## 6. 进程拓扑和生命周期

### 6.1 进程拓扑

决策：

```text
一个 Package -> 一个 Worker 进程
一个 Worker 进程 -> 多个节点实例
```

示例工作流：

```text
PythonUpper-1
PythonUpper-2
CppSuffix-1
CSuffix-1
```

启动后：

```text
Package Node.PythonUpper
    WorkerProcess P1
        nodeInstanceId 1001
        nodeInstanceId 1002

Package Node.CppSuffix
    WorkerProcess P2
        nodeInstanceId 1003

Package Node.CSuffix
    WorkerProcess P3
        nodeInstanceId 1004
```

一个 Worker 进程内所有消息都必须带 `nodeInstanceId`，避免多个节点实例串消息。

### 6.2 进程生命周期与节点生命周期分离

Worker 进程状态：

```text
Starting
Connected
Ready
ShuttingDown
Exited
Failed
```

节点实例状态：

```text
Created
Attached
Initializing
Initialized
Connecting
Running
Stopping
Stopped
ShuttingDown
Shutdown
Failed
```

Core 的 `State/Running` 仍由 Proxy 映射到 C# `BaseNode`。

### 6.3 生命周期调用

| C# BaseNode | Worker SDK 回调 | 说明 |
|---|---|---|
| `Configure` | `configure` | 参数变化时调用 |
| `Initialize` | `init` | 每次 Run 前调用 |
| `Run` | `run` | 节点进入运行态 |
| `Stop` | `stop` | 释放运行资源，但 Worker 进程保留 |
| `Shutdown` | `shutdown` | 节点实例最终销毁或应用退出 |
| `ReceiveSample` | `on_pin_receive` | Core 转发输入 Sample |
| `SetOutput` / `SendSample` | `send_sample` | Worker 发送输出 Sample |

生命周期语义：

- `init` 成功后节点才允许 `run`。
- `run` 成功后 Core 才把对应节点的 Pin 输入转发给 Worker。
- `stop` 后不再转发输入，但 Worker 进程和广播订阅可以保留。
- `stop` 成功后允许再次 `init/run`。
- Worker 崩溃时，该 Worker 内所有节点实例都标记 Failed。
- `shutdown` 后不允许再次运行该 Worker，必须重新启动进程。
- `shutdown` 必须幂等。

### 6.4 广播和 Loaded 状态

因为 Worker 在加载工作流时启动，第一版建议：

- 节点实例创建后即可订阅广播。
- 广播按 Topic 投递到 Worker。
- Worker SDK 将广播按 `nodeInstanceId` 分派给节点实例。
- 节点在 `Attached/Initialized/Running/Stopped` 状态均可收到广播。
- 节点业务代码可根据自身状态选择处理或忽略。
- 如果节点需要等到 `init` 后才处理业务广播，由 Worker SDK 或节点自己缓冲。
- 第一版不要求 Core 保存广播历史。

---

## 7. 传输设计

### 7.1 生产传输

Windows：

```text
Named Pipe
```

Linux：

```text
Unix Domain Socket
```

Core 作为服务端，Worker 作为客户端。

推荐 Windows 端点格式：

```text
\\.\pipe\AgentFlow\<hostSessionId>\<workflowInstanceId>\<packageHash>\<workerLaunchId>
```

示例：

```text
\\.\pipe\AgentFlow\6fd71e0c\workflow-9b2a\3f66c1d8\launch-01f3a8
```

推荐 Linux 端点格式：

```text
$XDG_RUNTIME_DIR/agentflow/<hostSessionId>/<workflowInstanceId>/<packageHash>-<workerLaunchId>.sock
```

端点字段：

| 字段 | 作用 |
|---|---|
| `hostSessionId` | AgentFlow 主程序或 CLI 本次启动的唯一 ID |
| `workflowInstanceId` | 当前工作流运行实例 ID |
| `packageHash` | Package ID 的短哈希，避免名称过长 |
| `workerLaunchId` | 每次启动 Worker 时生成的新 GUID 或 nonce |

最关键的是 `workerLaunchId`，它必须在每次 Worker 进程启动时重新生成。不能只用 packageId、nodeType、workflowId 或 processId 作为唯一依据。

端点规则：

- Core 是端点创建者，Worker 只连接。
- Core 必须先创建端点，再启动 Worker。
- 不能使用固定名称，例如全局固定的 `AgentFlowWorkerPipe`。
- 不同工作流实例必须使用不同端点。
- 同一工作流内同一个 Package 可以共用一个 Worker 和一个端点。
- Worker 退出后删除对应端点。
- Linux UDS 路径有长度限制，不能把完整 Package 名称和长路径全部拼入，建议使用短 GUID 和 Package 哈希。
- Linux 需要处理上次异常退出残留的 `.sock` 文件，但不能误删其他运行实例的 socket。
- 端点名称不得包含敏感信息。
- Named Pipe 必须使用 ACL，UDS 路径权限建议为 `0600`。
- 端点参数通过 `--endpoint` 传给 Worker，不通过匿名管道句柄。

### 7.2 stdio 的定位

`stdin/stdout + NDJSON` 可以保留为：

- 开发调试模式。
- 单元测试和协议 Mock。
- 简单 Worker 启动场景。

但不作为生产默认传输，因为：

- stdout 容易污染。
- 不支持 Worker 独立重连。
- 不支持长期多实例监督。
- 不适合同时承载 Pin 和广播。

### 7.3 一个连接承载所有消息

每条 Worker 连接同时承载：

```text
worker.register
node.create
node.command
node.state
pin.receive
pin.send
broadcast.subscribe
broadcast.publish
broadcast.receive
log
error
heartbeat
```

不存在单独的：

```text
Pin 通道
广播通道
日志通道
```

日志和广播都是协议消息类型。

### 7.4 单写入器

Core 和 Worker 两端都必须保证每个连接只有一个串行写入器。

原因：

- Pin 输出、广播、生命周期结果和日志可能并发产生。
- 多条 JSON 行不能交错。

建议：

```text
业务线程 -> 有界发送队列 -> 单写入任务 -> Named Pipe/UDS
```

### 7.5 外部进程启动与连接流程

外部节点 Worker 是独立进程，但生产环境不使用匿名管道。Core 必须先创建唯一的 Named Pipe / Unix Domain Socket 监听端点，再启动 Worker 进程。

完整流程：

```text
Core
 ├─ 创建唯一 Named Pipe / UDS 端点
 ├─ 生成随机 token
 ├─ 启动 Worker 进程
 │    ├─ --endpoint \\.\pipe\... 或 /run/...sock
 │    └─ --token ...
 ├─ 等待 Worker 连接
 ├─ 完成 worker.hello / worker.register
 └─ 等待 worker.ready
```

Worker 启动参数示例：

```text
Worker.exe --endpoint "\\.\pipe\AgentFlow.<hostSessionId>.<workflowId>.<packageId>" --token "<random-token>"
```

`${WORKER_ENDPOINT}` 代表的是：

- Windows 的 Named Pipe 名称。
- Linux 的 UDS 路径。

它不是匿名管道句柄，也不是预先把管道句柄交给子进程。

必须保证：

- Core 先创建端点，再启动 Worker 进程。
- 不能先启动 Worker，再临时创建端点。
- Worker 作为客户端主动连接端点。
- Worker 连接成功后完成 hello、register 和 ready。
- 连接失败通过启动错误上报，不能只在 stderr 打印。
- 端点名称包含 session、workflow、package 等唯一信息，避免多个 AgentFlow 实例冲突。
- token 用于避免错误进程连接或跨实例误连。
- 正式生产环境不使用匿名管道。
- `stdin/stdout + NDJSON` 只用于调试、测试和协议 Mock。

不使用匿名管道的原因：

- 匿名管道通常依赖父子进程句柄继承。
- Windows 和 Linux 的句柄继承机制不同。
- Worker 重启和重新连接不方便。
- 不适合长期监督 Worker。
- 不适合一个 Worker 承载多个节点实例。
- 不方便独立调试和测试。

Worker 启动参数由 `plugin.json` 的 `worker.arguments` 声明，Core 负责替换 `${WORKER_ENDPOINT}` 和 `${WORKER_TOKEN}`。

---

## 8. 协议设计

### 8.1 基本约定

- UTF-8。
- NDJSON，一行一个 JSON object。
- 每行后跟 `\n`。
- 接收端兼容 `\r\n`。
- 单行最大长度必须有上限。
- 所有消息必须带 `protocolVersion`。
- 所有节点消息必须带 `nodeInstanceId`。
- 同一节点输入输出必须带 `sequence`。
- 所有请求/响应必须带 `messageId` 和 `correlationId`（如适用）。

### 8.2 通用消息字段

```json
{
  "protocolVersion": 1,
  "type": "pin.receive",
  "messageId": "msg-1001",
  "correlationId": "cmd-2001",
  "workerId": "worker-py-1",
  "nodeInstanceId": 1001,
  "runId": "run-123",
  "sequence": 12,
  "timestampUtc": "2026-11-14T09:00:00Z"
}
```

### 8.3 消息类型

| 消息 | 方向 | 说明 |
|---|---|---|
| `worker.hello` | Worker -> Core | 连接后发送进程、Package、协议版本和能力 |
| `worker.welcome` | Core -> Worker | 接受 hello，返回 workerId 和超时配置 |
| `worker.register` | Worker -> Core | Worker 注册 Package 和 nodeType 信息 |
| `worker.registered` | Core -> Worker | 注册成功 |
| `worker.ready` | Worker -> Core | 接收、发送、队列和订阅能力已就绪 |
| `worker.heartbeat` | 双向 | 心跳 |
| `node.create` | Core -> Worker | 创建一个节点实例 |
| `node.created` | Worker -> Core | 节点实例创建结果 |
| `node.configure` | Core -> Worker | 下发或更新参数 |
| `node.configure.result` | Worker -> Core | 参数配置结果 |
| `node.command` | Core -> Worker | 生命周期命令 init/run/stop/shutdown |
| `node.command.result` | Worker -> Core | 生命周期命令结果 |
| `node.state` | Worker -> Core | 节点状态变化 |
| `node.error` | Worker -> Core | 节点错误 |
| `pin.receive` | Core -> Worker | 输入 Pin Sample |
| `pin.send` | Worker -> Core | 输出 Pin Sample |
| `broadcast.subscribe` | Worker -> Core | 订阅 Topic |
| `broadcast.unsubscribe` | Worker -> Core | 取消订阅 |
| `broadcast.subscription` | Core -> Worker | 订阅结果 |
| `broadcast.publish` | Worker -> Core | 发布广播 |
| `broadcast.receive` | Core -> Worker | Core 投递广播 |
| `log` | Worker -> Core | 结构化日志 |
| `error` | 双向 | 协议或运行错误 |

`node.command` 只表示生命周期命令 `init/run/stop/shutdown`，不是 C# BaseNode 的 UI 右键菜单 `NodeCommand` 功能。

### 8.4 注册流程

完整 hello/welcome/register/registered/ready 流程见 `8.9`。本节只展示注册消息本身。

Worker 启动后发送：

```json
{
  "protocolVersion": 1,
  "type": "worker.register",
  "packageId": "Node.PythonUpper",
  "workerVersion": "1.0.0",
  "processId": 12345,
  "capabilities": ["pin", "broadcast", "log", "lifecycle"]
}
```

Core 返回：

```json
{
  "protocolVersion": 1,
  "type": "worker.registered",
  "workerId": "worker-py-1",
  "heartbeatIntervalMs": 5000,
  "commandTimeoutMs": 30000
}
```

### 8.5 创建节点实例

Core：

```json
{
  "protocolVersion": 1,
  "type": "node.create",
  "messageId": "msg-create-1",
  "workerId": "worker-py-1",
  "nodeInstanceId": 1001,
  "nodeType": "Node.PythonUpper",
  "parameters": {
    "Suffix": ":py"
  }
}
```

Worker：

```json
{
  "protocolVersion": 1,
  "type": "node.created",
  "messageId": "msg-create-result-1",
  "correlationId": "msg-create-1",
  "nodeInstanceId": 1001,
  "status": "ok"
}
```

### 8.6 生命周期命令

Core：

```json
{
  "protocolVersion": 1,
  "type": "node.command",
  "messageId": "msg-cmd-1",
  "nodeInstanceId": 1001,
  "command": "init"
}
```

Worker：

```json
{
  "protocolVersion": 1,
  "type": "node.command.result",
  "messageId": "msg-result-1",
  "correlationId": "msg-cmd-1",
  "nodeInstanceId": 1001,
  "command": "init",
  "status": "ok"
}
```

支持：

```text
init
run
stop
shutdown
```

失败：

```json
{
  "protocolVersion": 1,
  "type": "node.command.result",
  "correlationId": "msg-cmd-1",
  "nodeInstanceId": 1001,
  "command": "init",
  "status": "error",
  "code": "NODE_INIT_FAILED",
  "message": "configuration file not found"
}
```

### 8.7 Pin 输入和输出

Core 到 Worker：

```json
{
  "protocolVersion": 1,
  "type": "pin.receive",
  "messageId": "msg-pin-in-1",
  "nodeInstanceId": 1001,
  "runId": "run-123",
  "targetPin": "in",
  "sequence": 1,
  "sample": {
    "key": "in",
    "dataType": "STRING",
    "encoding": "utf8",
    "value": "hello"
  }
}
```

Worker 到 Core：

```json
{
  "protocolVersion": 1,
  "type": "pin.send",
  "messageId": "msg-pin-out-1",
  "nodeInstanceId": 1001,
  "runId": "run-123",
  "sourcePin": "out",
  "sequence": 1,
  "sample": {
    "key": "out",
    "dataType": "STRING",
    "encoding": "utf8",
    "value": "hello:py"
  }
}
```

### 8.8 广播

订阅：

```json
{
  "protocolVersion": 1,
  "type": "broadcast.subscribe",
  "messageId": "msg-sub-1",
  "nodeInstanceId": 1001,
  "topic": "FACT.UserSettings"
}
```

订阅结果：

```json
{
  "protocolVersion": 1,
  "type": "broadcast.subscription",
  "correlationId": "msg-sub-1",
  "nodeInstanceId": 1001,
  "topic": "FACT.UserSettings",
  "status": "ok"
}
```

发布：

```json
{
  "protocolVersion": 1,
  "type": "broadcast.publish",
  "messageId": "msg-pub-1",
  "nodeInstanceId": 1001,
  "runId": "run-123",
  "topic": "FACT.UserSettings",
  "payloadKind": "dictionary_sample",
  "payload": {
    "CMD": {
      "dataType": "STRING",
      "encoding": "utf8",
      "value": "C:\\AC710\\FACT.exe"
    }
  }
}
```

Core 向订阅者投递：

```json
{
  "protocolVersion": 1,
  "type": "broadcast.receive",
  "messageId": "msg-bc-1",
  "targetNodeInstanceId": 1002,
  "sourceNodeInstanceId": 1001,
  "runId": "run-123",
  "topic": "FACT.UserSettings",
  "broadcastId": "bc-1001",
  "sequence": 1,
  "payloadKind": "dictionary_sample",
  "payload": {
    "CMD": {
      "dataType": "STRING",
      "encoding": "utf8",
      "value": "C:\\AC710\\FACT.exe"
    }
  }
}
```

规则：

- Worker 只向 Core 发布。
- Core 查询 TopicBus 订阅关系。
- 本地 .NET 节点和外部节点都通过 Core 接收。
- 默认不向原发布者回传。
- Worker 断开时删除其所有订阅。
- 广播尽力投递，不等待 ACK。
- 发送队列必须有界。
- 广播队列满时的策略首版建议：记录警告并丢弃新消息，不断开其他节点。
- 广播消息带 `broadcastId` 和 `sequence`，用于诊断和重复检测。

### 8.9 启动、注册和 Ready 流程

统一流程：

```text
Worker 连接成功
    -> worker.hello
Core
    -> worker.welcome
Worker
    -> worker.register
Core
    -> worker.registered
Worker 完成队列、读写线程和广播订阅初始化
    -> worker.ready
Core
    -> node.create / node.configure / node.command
```

Worker hello：

```json
{
  "protocolVersion": 1,
  "type": "worker.hello",
  "messageId": "msg-hello-1",
  "packageId": "Node.PythonUpper",
  "packageVersion": "1.0.0",
  "sdkVersion": "1.0.0",
  "processId": 12345,
  "capabilities": ["pin", "broadcast", "log", "lifecycle"]
}
```

Core welcome：

```json
{
  "protocolVersion": 1,
  "type": "worker.welcome",
  "messageId": "msg-welcome-1",
  "correlationId": "msg-hello-1",
  "workerId": "worker-py-1",
  "heartbeatIntervalMs": 5000,
  "heartbeatTimeoutMs": 15000,
  "commandTimeoutMs": 30000,
  "maxLineBytes": 1048576
}
```

Worker 注册：

```json
{
  "protocolVersion": 1,
  "type": "worker.register",
  "messageId": "msg-register-1",
  "workerId": "worker-py-1",
  "packageId": "Node.PythonUpper",
  "nodeTypes": [
    {
      "nodeType": "Node.PythonUpper",
      "version": "1.0.0"
    }
  ],
  "capabilities": ["pin", "broadcast", "log", "lifecycle"]
}
```

Core 注册成功：

```json
{
  "protocolVersion": 1,
  "type": "worker.registered",
  "messageId": "msg-registered-1",
  "correlationId": "msg-register-1",
  "workerId": "worker-py-1"
}
```

Worker 就绪：

```json
{
  "protocolVersion": 1,
  "type": "worker.ready",
  "messageId": "msg-ready-1",
  "workerId": "worker-py-1",
  "subscriptionsActive": true
}
```

规则：

- `worker.ready` 之前 Core 不能发送 `node.create`、`node.configure` 或 `node.command`。
- Worker 必须在 `worker.ready` 前完成接收线程、发送线程、队列和默认广播订阅初始化。
- hello、welcome、register、registered、ready 每一步都必须有超时。
- 任一步失败都应终止 Worker 并上报启动失败。
- 版本不支持时 Core 应返回协议错误，不能继续注册。

### 8.10 参数配置协议

`node.create` 可以携带初始参数。参数后续变化使用 `node.configure`。

Core 到 Worker：

```json
{
  "protocolVersion": 1,
  "type": "node.configure",
  "messageId": "msg-config-1",
  "nodeInstanceId": 1001,
  "parameters": {
    "Suffix": ":py2"
  },
  "replaceAll": true
}
```

Worker 返回：

```json
{
  "protocolVersion": 1,
  "type": "node.configure.result",
  "messageId": "msg-config-result-1",
  "correlationId": "msg-config-1",
  "nodeInstanceId": 1001,
  "status": "ok"
}
```

规则：

- Worker 必须在创建节点实例时校验参数类型。
- `replaceAll=true` 表示当前参数集合被完整替换。
- 第一版建议参数修改只在节点非 Running 状态生效。
- Running 状态收到参数修改时，Worker 可以拒绝并返回明确错误，或由 Core 在 UI 层阻止 Apply。
- 参数修改不得动态修改 Pin 数量和 DataType；动态 Pin 第一版不支持。
- Worker 必须在收到 configure 后更新本地参数缓存，下一次 init/run 使用新值。
- configure 必须幂等，重复下发相同参数不能产生额外副作用。

### 8.11 心跳、状态、错误和日志

#### worker.heartbeat

建议由 Worker 周期性主动发送，Core 可以返回确认：

```json
{
  "protocolVersion": 1,
  "type": "worker.heartbeat",
  "messageId": "msg-hb-1",
  "workerId": "worker-py-1",
  "timestampUtc": "2026-11-14T09:00:00Z",
  "sequence": 42
}
```

规则：

- 心跳周期由 `worker.welcome` 下发。
- Core 超过 `heartbeatTimeoutMs` 未收到心跳时，将 Worker 标记 Failed。
- 第一版不自动重连。
- 心跳只表示 Worker 主循环和传输仍存活，不代表所有节点业务线程正常。
- 节点执行线程如果永久阻塞，需要通过命令超时或其他健康信号单独处理。

#### node.state

```json
{
  "protocolVersion": 1,
  "type": "node.state",
  "messageId": "msg-state-1",
  "nodeInstanceId": 1001,
  "state": "Running",
  "previousState": "Initialized",
  "timestampUtc": "2026-11-14T09:00:00Z"
}
```

#### node.error

```json
{
  "protocolVersion": 1,
  "type": "node.error",
  "messageId": "msg-node-error-1",
  "nodeInstanceId": 1001,
  "code": "NODE_RUN_FAILED",
  "severity": "error",
  "message": "failed to open settings file",
  "fatal": true,
  "nodeState": "Failed",
  "exceptionType": "FileNotFoundError"
}
```

#### error

```json
{
  "protocolVersion": 1,
  "type": "error",
  "messageId": "msg-error-1",
  "correlationId": "msg-pin-out-1",
  "workerId": "worker-py-1",
  "nodeInstanceId": 1001,
  "code": "PIN_UNKNOWN",
  "severity": "error",
  "message": "unknown output pin out",
  "fatal": false
}
```

规则：

- `node.error` 表示节点业务错误。
- `error` 表示协议、传输、注册或路由错误。
- `fatal=true` 表示该错误必须导致节点或 Worker 失败。
- `fatal=false` 只记录错误，不自动停止工作流。
- 错误消息不能包含未脱敏的密码、token 或完整敏感配置。
- `exceptionType` 可以发送，stack trace 应作为受限 debug 字段或仅本地日志。

#### log

```json
{
  "protocolVersion": 1,
  "type": "log",
  "messageId": "msg-log-1",
  "workerId": "worker-py-1",
  "nodeInstanceId": 1001,
  "runId": "run-123",
  "level": "info",
  "message": "processing input",
  "fields": {
    "pin": "in",
    "sequence": 3
  },
  "timestampUtc": "2026-11-14T09:00:00Z"
}
```

规则：

- 日志级别：trace、debug、info、warning、error、critical。
- 日志消息有最大长度和频率限制。
- Worker 的 stdout/stderr 不能作为正常业务日志通道。
- Core 负责记录 Worker PID、退出码和最后日志，便于排查。
- 日志不得阻塞业务线程，必须通过有界 outbound queue 发送。

### 8.12 协议版本兼容

版本字段：

```text
manifestVersion
protocolVersion
sdkVersion
packageVersion
```

规则：

- `protocolVersion` 使用主版本号。
- Core 只接受自己支持的同主版本协议。
- 收到不支持版本时，Core 返回 `PROTOCOL_VERSION_UNSUPPORTED` 并停止 Worker。
- 次版本变化只能增加可选字段和新消息，不能改变旧字段语义。
- 删除字段、改变类型或改变消息语义必须升级主版本。
- `sdkVersion` 用于诊断，不代替 `protocolVersion` 校验。
- `packageVersion` 用于 Package 升级和回滚。
- Worker 注册信息中的协议版本必须与 `worker.hello` 一致。
- manifest、Worker 注册和实际 SDK 版本不一致时必须明确报错。

### 8.13 Pin 投递、ACK 和背压

第一版建议：

- `pin.send` 是 Worker -> Core 的单向投递，不等待下游节点 ACK。
- Worker SDK 的 `send_sample` 成功只表示已成功放入本地 outbound queue。
- outbound queue 满时，`send_sample` 必须返回明确错误，不能无限阻塞。
- Core 收到未知 Pin、类型不匹配或无效 Sample 时，返回 `error`，并使用原消息的 `messageId` 作为 correlationId。
- Core 对未连接输出 Pin 可以记录 warning，但不应导致 Worker 崩溃。
- Worker 不感知工作流连接拓扑。
- 每个节点实例的 `pin.send` 顺序必须按 outbound sequence 保持。
- Core 按接收顺序路由，不能因线程调度打乱同一节点的 Pin 输出。
- 第一版不做端到端交付保证，只保证尽力投递和错误可诊断。

### 8.14 messageId、correlationId 和 sequence

规则：

- `messageId` 在 workerId 范围内唯一。
- 请求和响应通过 `correlationId` 关联。
- `sequence` 在同一 nodeInstanceId 的输入或输出方向上单调递增。
- `runId` 用于区分同一次或不同次工作流运行。
- `runId` 变化后旧运行的延迟 Sample 必须被丢弃或拒绝。
- `broadcastId` 用于广播诊断和重复检测。
- 日志不要求严格 sequence，但应带 timestampUtc 和 runId。
- 所有 ID 由发送方生成，接收方不得假定其格式为整数，建议使用字符串。

### 8.15 正式 schema、枚举和 test vectors

第一版必须提供机器可校验的 schema：

```text
Contracts/
├─ plugin.schema.json
├─ worker-message.schema.json
└─ test-vectors/
   ├─ worker-startup.json
   ├─ node-lifecycle.json
   ├─ pin-samples.json
   ├─ broadcast.json
   └─ errors.json
```

`worker.register` 的 `nodeTypes` 使用对象数组：

```json
{
  "protocolVersion": 1,
  "type": "worker.register",
  "messageId": "msg-register-1",
  "workerId": "worker-py-1",
  "packageId": "Node.PythonUpper",
  "nodeTypes": [
    {
      "nodeType": "Node.PythonUpper",
      "version": "1.0.0"
    }
  ],
  "capabilities": ["pin", "broadcast", "log", "lifecycle"]
}
```

规则：

- `nodeType` 必须与 manifest 完全一致。
- `version` 必须与 manifest packageVersion 兼容。
- Core 收到重复 nodeType 注册时拒绝该 Package。
- Core 负责再次校验 manifest、nodeTypes、Pin 和参数一致性。
- 协议 schema 必须允许未来增加可选字段，但不能改变已有字段类型或含义。

广播 `payloadKind` 第一版枚举：

```text
sample
samples
dictionary_sample
dictionary_samples
```

示例：

```json
{
  "payloadKind": "dictionary_sample",
  "payload": {
    "CMD": {
      "dataType": "STRING",
      "encoding": "utf8",
      "value": "C:\\AC710\\FACT.exe"
    }
  }
}
```

```json
{
  "payloadKind": "samples",
  "payload": [
    {"dataType": "STRING", "encoding": "utf8", "value": "A"},
    {"dataType": "STRING", "encoding": "utf8", "value": "B"}
  ]
}
```

```json
{
  "payloadKind": "dictionary_samples",
  "payload": {
    "Items": [
      {"dataType": "STRING", "encoding": "utf8", "value": "A"},
      {"dataType": "STRING", "encoding": "utf8", "value": "B"}
    ]
  }
}
```

`sample` 表示单个 Sample 对象。`samples` 表示 Sample 数组。`dictionary_sample` 表示 `string -> Sample`。`dictionary_samples` 表示 `string -> Sample[]`。

协议枚举：

```text
node.command: init | run | stop | shutdown
node.state: Created | Attached | Initializing | Initialized | Connecting | Running | Stopping | Stopped | ShuttingDown | Shutdown | Failed
command.result.status: ok | error
log.level: trace | debug | info | warning | error | critical
error.severity: info | warning | error | critical
```

Schema 规则：

- manifest 中未知字段默认允许忽略，但协议关键字段未知不能替代必填字段。
- 未知 `node.command` 和未知 `payloadKind` 必须返回协议错误。
- 所有消息必须有 `protocolVersion` 和 `type`。
- 请求和响应必须有 `messageId`；响应必须有 `correlationId`。
- 长期节点消息必须有 `nodeInstanceId`。
- `workerId` 在 post-register 消息中必须存在。
- `runId` 在 Pin 和广播业务消息中存在，保证可以隔离不同运行。
- Sample schema 不得包含无法序列化的对象。
- test vectors 必须覆盖合法消息和典型非法消息。

### 8.16 生命周期状态转移表

标准创建和启动顺序：

```text
node.create
    -> node.created
node.configure（可选或由 node.create 参数完成）
    -> node.configure.result
node.command(init)
    -> node.command.result
node.command(run)
    -> node.command.result
```

标准停止顺序：

```text
node.command(stop)
    -> node.command.result
node.command(shutdown)
    -> node.command.result
    -> Worker 清理节点实例
```

状态转移：

| 当前状态 | Configure | Init | Run | Stop | Shutdown |
|---|---|---|---|---|---|
| Created | 允许 | 允许 | 禁止 | 禁止 | 允许 |
| Attached | 允许 | 允许 | 禁止 | 禁止 | 允许 |
| Initialized | 允许 | 重新 Init 需拒绝或先 Stop | 允许 | 禁止 | 允许 |
| Running | 默认禁止 | 禁止 | 禁止 | 允许 | 允许 |
| Stopped | 允许 | 允许 | 允许 | 允许且幂等 | 允许 |
| Failed | 允许 | 允许重新初始化 | 禁止 | 允许清理 | 允许 |
| ShuttingDown | 禁止 | 禁止 | 禁止 | 禁止 | 幂等 |
| Shutdown | 禁止 | 禁止 | 禁止 | 禁止 | 幂等 |

规则：

- `node.created` 返回后节点状态为 Created 或 Attached。
- `node.create` 携带初始参数；随后 `node.configure` 可以覆盖。
- `init` 每次 Run 前执行。
- `run` 返回成功后状态为 Running。
- `stop` 后状态为 Stopped，允许再次 init/run。
- `shutdown` 后不可再次运行。
- `Failed` 节点是否可 Reset 由实现明确；第一版建议允许 Stop/Shutdown 清理。
- Running 中后台线程 fatal 错误应发送 `node.error`，并将节点状态变为 Failed。
- 后台线程 fatal 错误是否停止整个工作流由 fatal 和 NodeErrorPolicy 决定。
- 状态消息和 command.result 顺序必须一致，不能先报告 Running 后报告 run 失败。

### 8.17 能力协商和响应状态

`capabilities` 第一版可选值：

```text
pin
broadcast
log
lifecycle
```

能力规则：

- `lifecycle` 是必需能力。
- `pin` 是普通节点必需能力。
- 只有 NodeType 声明广播时才要求 `broadcast`。
- `log` 为建议能力，不支持时 Core 仍可通过 stderr 采集启动错误。
- Worker 不声明但 manifest 使用对应功能时，Core 拒绝加载该 Package。
- 未知能力允许忽略，但 Core 必须记录警告。

响应状态：

- `status: ok` 表示命令成功完成。
- `status: error` 表示命令失败。
- 失败响应必须包含 `code` 和 `message`。
- 超时不使用普通 error 响应表示，而由 Core 的 timeout 逻辑处理。
- Core 不应依赖可选字段存在来判断成功，必须以 `status` 为准。
- 成功响应可以附带 `warnings`，但不得改变状态语义。

错误对象统一字段：

```json
{
  "code": "NODE_RUN_FAILED",
  "severity": "error",
  "message": "failed to start background worker",
  "fatal": true,
  "detail": "optional structured detail",
  "timestampUtc": "2026-11-14T09:00:00Z"
}
```

---

## 9. BaseNode 能力映射

### 9.1 支持的能力

第一版支持：

- 固定输入 Pin。
- 固定输出 Pin。
- 基础参数。
- `Initialize/Run/Stop/Shutdown`。
- Pin 输入输出。
- 广播订阅和发布。
- 结构化日志。
- 失败上报。

### 9.2 暂不支持的能力

第一版不支持：

- NodeCommand。
- 任意 `GetService<T>`。
- 运行时动态 Pin。
- 复杂参数编辑器联动。
- 外部节点直接依赖 .NET 接口。

### 9.3 INodeContext 映射

Worker SDK 应提供对应能力：

| C# INodeContext | Worker SDK |
|---|---|
| `GetInput<T>` | `get_input` |
| `HasInput` | `has_input` |
| `TryGetInput<T>` | `try_get_input` |
| `WaitForInputAsync` | `wait_for_input` |
| `SetOutput` | `send_sample` / `set_output` |
| `Logger` | `log` 协议消息 |
| `GetService<T>` | 第一版不支持 |

Worker SDK 应在本地维护输入 Pin 的最后值和等待条件，不要把工作流连接关系暴露给外部节点。

### 9.4 参数映射规则

外部节点参数应映射现有 `NodeParameter` 和 `ParameterDescriptor` 语义：

| manifest type | C# 类型 | Python 建议 | C/C++ 建议 |
|---|---|---|---|
| `string` | `string` | `str` | UTF-8 字符串 |
| `bool` | `bool` | `bool` | `bool` |
| `int32` | `int` | `int` | `int32_t` |
| `int64` | `long` | `int` | `int64_t` |
| `float` | `float` | `float` | `float` |
| `double` | `double` | `float` | `double` |

编辑器映射：

- `auto`：沿用默认文本输入。
- `text`：文本输入。
- `singleSelect`：显示 `options` 中一个选项。
- `multiSelect`：显示 `options` 多选，首版仍可存为分号分隔字符串。

参数规则：

- `defaultValue` 必须符合声明类型。
- 参数名和类型进入工作流保存格式，应保持稳定。
- `zone` 和 `group` 仅影响属性面板排列，不影响 Worker 业务语义。
- `editable=false` 的参数由 Core 保存和下发，但 GUI 不显示编辑控件。
- `sensitive=true` 的参数不得写入普通日志。
- 第一版不支持任意嵌套 JSON 对象作为参数。
- 第一版不支持参数值改变 Pin 数量或 Pin DataType。
- Worker 收到 `node.configure` 后必须完成类型转换和校验。
- 参数校验失败必须返回 `node.configure.result` 或 `node.error`。
- Running 状态下的参数修改默认拒绝，或要求先 Stop 再 Apply。

### 9.5 输入、输出和 Sample 行为

#### 输入 Sample

- Core 只在节点 Running 时投递普通 `pin.receive`。
- Worker SDK 按 nodeInstanceId 和维护的 inbox 顺序处理。
- 同一节点实例的输入事件保持 FIFO。
- 每个输入 Pin 保存最后一次有效 Sample。
- `has_input` 表示该 Pin 是否至少收到过一次 Sample。
- `get_input` 返回最后一次 Sample，不消费历史。
- `try_get_input` 在没有 Sample 时返回 null/缺席，而不是抛出业务异常。
- `wait_for_input` 已有 Sample 时立即返回；没有时等待新 Sample。
- `runId` 变化后，旧 runId 的延迟输入必须丢弃。
- 输入队列满时拒绝新业务 Sample 并上报错误，不能阻塞 Control 事件。
- `pin.receive` 的 Sample DataType 必须与 Pin 声明一致。
- 不允许 Worker 根据输入值自行新增 Pin。

#### 输出 Sample

- Worker 通过 `send_sample` / `set_output` 产生 `pin.send`。
- `pin.send` 只表示发送请求，不表示下游节点已处理。
- Worker SDK 的返回表示是否成功进入本地 outbound queue。
- outbound queue 满时必须返回明确错误。
- Core 负责校验 Pin 存在、方向为 output、DataType 匹配和 Sample 有效。
- Core 对未连接输出 Pin 可以记录 warning。
- 同一节点实例的输出 sequence 必须单调递增。
- Stop 后默认不再允许发送业务 Sample。
- Shutdown 前应尝试 flush 已接受的 outbound queue，超时后允许丢弃并记录错误。

#### null 和缺失值

- 缺失 Sample 使用 `sample: null` 或协议中明确的无 Sample 表示。
- 空字符串 `""`、整数 `0`、布尔 `false` 都不是缺失值。
- `allowNull=false` 的 Pin 收到 null 时应返回协议错误。
- `allowNull=true` 的 Pin 必须把 null 作为有效输入事件处理，不能等同于“没有收到”。
- `Sample.Key` 默认使用 Pin 名；Core 转发时不做隐式改写。
- Worker 可以保留 Sample Key，但不得依赖 Key 替代 targetPin/sourcePin。

---

## 10. Sample 设计

### 10.1 支持类型

当前 C# `DataType`：

```text
STRING
INT32
INT64
BOOL
FLOAT
DOUBLE
BIN
```

外部协议继续使用同一组类型名。

### 10.2 通用结构

```json
{
  "key": "pin-name",
  "dataType": "STRING",
  "encoding": "utf8",
  "value": "..."
}
```

`Sample?` 用 `sample: null` 表示没有 Sample。

### 10.3 编码规则

| 类型 | encoding | value |
|---|---|---|
| STRING | `utf8` | JSON string |
| INT32 | `decimal` | JSON number 或字符串 |
| INT64 | `decimal-string` | 十进制字符串 |
| BOOL | `boolean` | `true` / `false` |
| FLOAT | `ieee754-binary32-bits-hex` | 8 位十六进制字符串 |
| DOUBLE | `ieee754-binary64-bits-hex` | 16 位十六进制字符串 |
| BIN | `base64` | Base64 字符串；大二进制后续使用共享内存引用 |

### 10.4 浮点精确传输

FLOAT：

```json
{
  "key": "f",
  "dataType": "FLOAT",
  "encoding": "ieee754-binary32-bits-hex",
  "value": "40490fdb"
}
```

DOUBLE：

```json
{
  "key": "d",
  "dataType": "DOUBLE",
  "encoding": "ieee754-binary64-bits-hex",
  "value": "400921fb54442d18"
}
```

必须保留：

- NaN
- +Infinity
- -Infinity
- -0.0
- Subnormal

### 10.5 Sample Key

- `Sample.Key` 必填，默认可使用 Pin 名。
- Core 转发时不做隐式重新赋值。
- Worker SDK 必须保留 `key`。
- 接收节点可通过 key 判断样本来源或业务含义。

### 10.6 BIN 大小限制

- 单行 NDJSON 不能无限大。
- 第一版可限制 BIN Base64 大小，例如 1 MB。
- 超过限制应返回协议错误。
- 第二版可增加共享内存或文件引用。

### 10.7 多语言 Sample 声明与 SDK 绑定

结论：Python、C、C++ 各自都需要一个本语言的 `Sample` 类型及编解码实现，但这些文件不应由每个节点插件重复声明。

正确关系是：

```text
一份协议定义作为唯一标准
        ↓
各语言 SDK 提供本语言 Sample 类型和编解码
        ↓
每个节点插件只使用 SDK，不重复定义 Sample
```

也就是说：

- 物理上每种语言有一份 Sample 绑定文件。
- 语义上只有一份协议定义。
- 这些文件是同一协议的不同语言表现，不是三套独立标准。
- 每个节点插件不应各自维护一份 Sample 定义。

推荐目录：

```text
SDK/
├─ Protocol/
│  └─ sample.schema.json          # 唯一协议标准
├─ Python/
│  └─ AgentFlowWorker/
│     └─ sample.py                # Python Sample + 编解码
├─ C/
│  ├─ Include/
│  │  └─ agentflow_sample.h       # C Sample + API 声明
│  └─ Src/
│     └─ agentflow_sample.c       # C 编解码实现
└─ Cpp/
   └─ Include/
      └─ agentflow_sample.hpp     # C++ 封装，可复用 C ABI
```

外部节点 Package 只负责业务逻辑：

```text
Plugins/External/<RID>/my-python-node/
├─ plugin.json
└─ Worker/
   └─ Main.py                     # import SDK，不重复定义 Sample
```

协议中的 Sample 示例：

```json
{
  "key": "input",
  "dataType": "STRING",
  "encoding": "utf8",
  "value": "hello"
}
```

```json
{
  "key": "temperature",
  "dataType": "DOUBLE",
  "encoding": "ieee754-binary64-bits-hex",
  "value": "400921fb54442d18"
}
```

Python SDK 示例：

```python
from dataclasses import dataclass
from typing import Any

@dataclass
class Sample:
    key: str
    data_type: str
    encoding: str
    value: Any
```

```python
from AgentFlowWorker import Sample

sample = Sample(
    key="input",
    data_type="STRING",
    encoding="utf8",
    value="hello",
)
```

C SDK 示例：

```c
typedef enum {
    AF_DATA_STRING,
    AF_DATA_INT32,
    AF_DATA_INT64,
    AF_DATA_BOOL,
    AF_DATA_FLOAT,
    AF_DATA_DOUBLE,
    AF_DATA_BIN
} AF_DataType;

typedef struct {
    char* key;
    AF_DataType data_type;
    char* encoding;
    void* value;
    size_t value_size;
} AFSample;
```

```c
AFSample sample;
af_sample_init_string(&sample, "input", "hello");

af_sample_destroy(&sample);
```

C 实现需要明确：

- `key`、字符串和 BIN 数据的内存归属。
- 谁负责释放内存。
- 所有文本统一使用 UTF-8。
- FLOAT/DOUBLE 应保存或按位保留原始 IEEE 754 位模式。

C++ 不建议单独维护一套完全不同的底层结构。推荐让 C++ SDK 复用 C ABI，再在上层提供 C++ 封装：

```cpp
extern "C" {
#include "agentflow_sample.h"
}
```

```cpp
class Sample {
public:
    static Sample string(std::string key, std::string value);
    static Sample int64(std::string key, int64_t value);
    static Sample doubleBits(std::string key, uint64_t bits);
};
```

```cpp
auto sample = Sample::string("input", "hello");
```

各语言类型映射：

| 类型 | Python | C | C++ |
|---|---|---|---|
| STRING | `str` | `char*` + 长度/所有权 | `std::string` |
| INT32 | `int` | `int32_t` | `int32_t` |
| INT64 | `int` | `int64_t` | `int64_t` |
| BOOL | `bool` | `bool` / `uint8_t` | `bool` |
| FLOAT | float 或原始 32 位值 | `float` / `uint32_t` bits | `float` / `uint32_t` bits |
| DOUBLE | float 或原始 64 位值 | `double` / `uint64_t` bits | `double` / `uint64_t` bits |
| BIN | `bytes` | `uint8_t*` + size | `std::vector<uint8_t>` |

实现约束：

- 所有语言 SDK 必须遵守相同的 `dataType`、`encoding`、`value` 字段语义。
- 节点插件通过 SDK 构造和读取 Sample，不自行实现 NDJSON 协议细节。
- C# 内部 `Sample` 不应直接视为外部协议结构；Core 应在内部 Sample 与外部通信 DTO 之间做映射。
- 如果协议定义发生变化，应统一更新协议文件和各语言 SDK，而不是只修改某一个节点。
- 第一版可以由人工维护三套 SDK；后续建议从协议 schema 生成类型和常量，减少漂移风险。

---

## 11. Worker SDK 设计

每种语言提供语义相同的 SDK：

```text
Python Worker SDK
C Worker SDK
C++ Worker SDK
```

### 11.1 对外 API

建议统一暴露：

```text
register
create_node
configure
init
run
stop
shutdown
on_pin_receive
send_sample
subscribe
unsubscribe
publish
log
```

### 11.2 节点作者接口

Python 示例：

```python
class Node:
    def configure(self, parameters):
        pass

    def init(self, ctx):
        pass

    def run(self, ctx):
        pass

    def stop(self, ctx):
        pass

    def shutdown(self, ctx):
        pass

    def on_pin_receive(self, pin_name, sample, ctx):
        pass
```

C/C++ 提供对应函数或结构体回调。

### 11.3 一个 Worker 内多节点

Worker SDK 必须维护：

```text
nodeInstanceId -> NodeInstance
```

每个节点实例独立拥有：

- 参数
- 输入 Pin 缓存
- 运行状态
- 广播订阅
- 生命周期
- 日志上下文

### 11.4 公共 Worker Runtime 与事件模型

需要先明确一个边界：

> `Init`、`Run`、`Stop`、`Shutdown` 不是 Sample，而是控制命令或生命周期事件。Sample 只承载 Pin 上的业务数据。

公共 SDK 不应为每种事件分别设计一套互不兼容的机制，而应统一进入明确的事件模型：

```text
InboundEvent =
    RegisterPackage
    CreateNode
    ConfigureNode
    InitCommand
    RunCommand
    StopCommand
    ShutdownCommand
    PinSample
    BroadcastMessage
    TransportDisconnected

OutboundEvent =
    LifecycleResult
    PinSend
    BroadcastPublish
    Log
    Heartbeat
    Error
```

这样 SDK 可以统一处理事件优先级、线程切换、错误传播和发送顺序。节点业务代码不需要知道消息来自 Pipe、Socket 还是测试模式。

每种语言的 Worker SDK 都应封装成完整的 `WorkerRuntime`，而不是只提供几个线程安全队列工具函数：

```text
WorkerRuntime
├─ Connect
├─ Handshake
├─ Register
├─ Ready
├─ ReaderLoop
├─ WriterLoop
├─ QueueManager
├─ StateMachine
├─ ErrorReporter
└─ Shutdown
```

### 11.5 Worker 启动和连接顺序

外部节点主程序启动时就必须完成连接，不能等第一个 Sample 到来后才连接：

```text
1. 解析 Core 传入的 endpoint、token、packageId
2. 连接 Named Pipe / Unix Domain Socket
3. 发送 worker.hello
4. 注册 package 和 nodeType 信息
5. 启动 transport reader 和 writer
6. 创建 inbound / outbound 队列
7. 发送 worker.ready
8. 开始接收 Core 命令
```

必须区分以下状态：

```text
进程已启动
管道已连接
Worker 已注册
Worker 已 Ready
节点实例已创建
节点已 Initialize
节点 Running
节点 Stopped
Worker Shutdown
```

`worker.ready` 不应只表示“进程已经启动”，而应表示：

- 连接成功
- 协议握手成功
- 注册成功
- 接收线程和发送线程已启动
- 队列已创建
- 可以接收 Core 命令

在 `worker.ready` 之前，不能开始处理 Init、Run、Pin Sample 或 Broadcast。

由于工作流加载时可能已经需要广播能力，Worker 应在节点实例创建并进入可接收状态后立即订阅广播，而不是等到第一次 Run 才订阅。

### 11.6 线程与队列模型

不建议所有节点实例共用一个业务队列，否则一个节点阻塞会拖慢同一 Worker 内的其他节点。

推荐模型：

```text
Transport reader thread
        ↓
Control queue
        ↓
Per-node inbox queue
        ↓
Per-node executor thread
        ↓
Node business callbacks
        ↓
Outbound queue
        ↓
Single writer thread
        ↓
Named Pipe / Unix Domain Socket
```

核心原则：

1. 每个 `nodeInstanceId` 拥有独立 inbox。
2. 同一节点实例内部按 FIFO 顺序处理事件。
3. 所有出站消息通过单一 writer 发送。
4. transport reader 不直接调用节点业务函数。
5. 节点回调不直接写 Pipe/Socket。
6. Stop/Shutdown 不能排在大量业务 Sample 后面长时间得不到处理。
7. Pin Sample、广播和出站队列应有容量上限，避免无限积压。

第一版推荐：

- 一个 transport reader 线程
- 每个节点实例一个执行线程
- 一个 transport writer 线程
- 每个节点一个有界线程安全 inbox
- 一个独立控制队列

这样某个节点的业务阻塞不会阻塞其他节点，也不会破坏 Pipe 的单写入约束。

### 11.7 生命周期状态和队列语义

生命周期函数对外表现为“请求”，SDK 将其转换为事件，再由节点执行线程调用对应回调：

```text
Core 发送 Init
    ↓
SDK 将 InitCommand 放入节点 control inbox
    ↓
节点执行线程按顺序调用 on_init
    ↓
结果写入 outbound queue
    ↓
writer 发送给 Core
```

状态要求：

- `Init` 只允许从 Loaded 状态进入。
- `Run` 只允许从 Initialized/Stopped 状态进入。
- `Stop` 可以重复调用，但必须幂等。
- `Shutdown` 必须最终执行，且之后不能再次 Run。
- 回调抛异常时，节点进入 `Faulted`。
- `Faulted` 后不能继续对外表现为 Running。
- Stop/Shutdown 必须释放节点资源，但第一版 Stop 后保留 Worker 进程。

必须区分 Stop 和 Shutdown：

```text
Stop:
    停止业务处理
    清理 Run 期间资源
    保留 Worker、连接、注册信息和节点实例
    后续允许再次 Run

Shutdown:
    最终清理节点实例
    清理 Worker 资源
    关闭连接
    进程退出
```

建议控制事件优先级高于普通业务事件：

```text
Shutdown
Stop
Init / Run
Transport 错误
普通 Pin Sample
普通 Broadcast
```

至少需要保证 Stop 不会因为 Pin Sample 队列积压而永远得不到执行。

### 11.8 Sample 入队和内存所有权

Sample 入队以后，原始栈变量可能已经失效，因此必须提前定义所有权规则：

- 入队采用深拷贝，还是转移所有权
- 字符串和 BIN 数据由谁释放
- 队列销毁时如何处理尚未消费的 Sample
- 节点回调是否允许保留 Sample 引用
- 队列满时返回错误、阻塞还是丢弃

推荐第一版：

- Python：队列中保存不可变副本或只读对象。
- C++：使用 RAII 所有权或 `std::shared_ptr`。
- C：提供明确的 `sample_clone()` 和 `sample_destroy()`，入队后由队列拥有。

不能让每个节点自行定义内存规则，否则容易出现悬空指针、重复释放和跨线程生命周期错误。

### 11.9 公共 SDK 模块

Python SDK 建议至少包含：

```text
runtime.py          # bootstrap、连接、注册、主循环
transport.py        # Named Pipe / UDS 读写
protocol.py         # NDJSON 消息定义
event_queue.py      # 线程安全队列
sample.py           # Sample
states.py           # 节点状态机
errors.py           # 协议错误和业务错误
log.py              # 通过 Core 转发日志
node.py             # 节点接口
```

C/C++ SDK 建议至少包含：

```text
worker_runtime.h/.c
transport_namedpipe.h/.c
transport_unix_socket.h/.c
event_queue.h/.c
sample.h/.c
protocol.h/.c
state.h
errors.h
log.h
```

节点插件只提供业务回调和 manifest，不直接实现 Pipe 连接、线程队列、NDJSON 和错误处理。

### 11.9.1 C/C++ 跨平台代码组织

C/C++ 可以跨 Windows 和 Linux 编译，但建议采用“公共代码 + 平台后端”方式，不要把大量 `#ifdef` 分散在业务代码中。

推荐目录：

```text
SDK/C/
├─ Include/
│  ├─ worker_runtime.h
│  ├─ transport.h
│  ├─ sample.h
│  └─ protocol.h
└─ Src/
   ├─ worker_runtime.c
   ├─ sample.c
   ├─ protocol.c
   ├─ transport_windows.c
   └─ transport_posix.c
```

公共接口示例：

```c
typedef struct AF_Transport AF_Transport;

int af_transport_connect(const char* endpoint, AF_Transport** out_transport);
int af_transport_write(AF_Transport* transport, const void* data, size_t size);
int af_transport_read(AF_Transport* transport, void* buffer, size_t capacity);
void af_transport_close(AF_Transport* transport);
```

Windows 后端：

- `CreateFile`
- `WaitNamedPipe`
- `ReadFile`
- `WriteFile`
- `CloseHandle`
- Windows Job Object 用于进程树清理

Linux 后端：

- `socket`
- `connect`
- `poll` / `select`
- `read` / `write`
- `close` / `unlink`
- 进程组或 PID 树清理

CMake 推荐按平台选择源文件：

```cmake
if (WIN32)
    target_sources(agentflow_worker PRIVATE Src/transport_windows.c)
elseif (UNIX)
    target_sources(agentflow_worker PRIVATE Src/transport_posix.c)
endif()
```

可定义平台宏：

```cmake
if (WIN32)
    target_compile_definitions(agentflow_worker PRIVATE AF_PLATFORM_WINDOWS=1)
elseif (UNIX)
    target_compile_definitions(agentflow_worker PRIVATE AF_PLATFORM_POSIX=1)
endif()
```

只在必要处使用条件编译：

```c
#if defined(AF_PLATFORM_WINDOWS)
    ...
#elif defined(AF_PLATFORM_POSIX)
    ...
#endif
```

可以共用：

- NDJSON 协议
- Sample 数据结构和编解码
- 线程安全队列
- 节点状态机
- Worker Runtime
- 生命周期分发
- 广播接口
- 日志接口
- 错误码
- manifest 读取和基础校验

平台分别实现：

- Named Pipe / UDS
- DLL / SO 加载
- Windows / Linux 进程树清理
- DLL 搜索路径与 `RPATH` / `$ORIGIN`
- Windows Job Object
- Linux 父进程死亡处理

编写约束：

- Windows 主要判断 `_WIN32`，不要主要依赖 `_WIN64`。
- Linux/POSIX 使用 `__linux__`、`__unix__`。
- MSVC 特有代码使用 `_MSC_VER`。
- 业务节点代码不应包含大量 `#ifdef _WIN32`。
- 平台差异必须封装在 transport、process、filesystem 等独立模块中。
- C 源文件由 C 编译器编译，C++ 包装层由 C++ 编译器编译。
- C++ Demo 通过 C ABI 复用 C 实现。
- Windows 和 Linux 必须分别构建，不能生成一个跨平台二进制。
- CI 应分别执行 Windows 和 Linux 构建和测试。

### 11.10 必须提前定义的边界

除生命周期和队列外，还需要明确以下问题。

#### 队列满策略

线程安全队列不能默认无限增长。建议：

- Pin Sample 队列：有界。
- Broadcast 队列：有界。
- Control 队列：不丢弃，但只保存少量生命周期命令。
- Outbound 队列：有界，满时让节点收到错误。

不能因为 Sample 积压导致 Stop 或 Shutdown 无法执行。

#### 广播与运行状态

需要明确：

- 节点创建后立即订阅广播，还是 Run 后订阅。
- 节点 Stopped 时广播是否仍然入队。
- 未 Run 时收到广播是保存、忽略还是调用独立回调。

根据当前“加载工作流时可能需要广播”的要求，建议节点创建并 Ready 后即可订阅，消息进入有界队列，由节点业务自行决定是否在 Run 前处理。

#### 消息顺序

至少需要定义：

- 同一节点的输入是否严格 FIFO。
- Pin Sample 和 Broadcast 是否共用顺序。
- 多线程生成输出时如何保证 sequence。
- Core 重发相同 sequence 时是否去重。

建议每个节点维护输入序号和输出序号，不依赖线程调度顺序。

#### 单一写线程

多个节点、多个线程同时直接写 Pipe/Socket 会导致消息交错、半行写入和连接状态混乱，因此所有出站消息必须进入统一队列：

```text
PinSend
BroadcastPublish
Log
LifecycleResult
Heartbeat
Error
```

由唯一 writer 序列化并写入。

#### 异常处理

每个生命周期和业务回调都必须由统一包装器执行：

```text
try
    call node handler
catch
    record log
    report error
    move node to Faulted
    report run failure if needed
```

不能因为 Python exception、C++ exception 或 C 返回错误码未检查，导致 Worker 静默退出。

#### 断连和父进程退出

需要规定：

- Core 断开后 Worker 如何处理。
- Worker 是否自动重连。
- Core 崩溃后 Worker 是否会变成孤儿进程。
- Windows 是否使用 Job Object 连带终止。
- Linux 如何监听父进程死亡。

第一版建议断连即标记 Worker 不可用，不自动重连；节点进入 Faulted，等待 Core 终止或 Shutdown。

#### 心跳和超时

即使连接仍存在，也需要区分：

- 管道连接正常。
- Worker 主线程卡死。
- 节点业务线程阻塞。
- 消息长时间无响应。

建议 SDK 提供 heartbeat 和命令超时，避免 Core 永久等待。

#### 启动失败

以下情况必须统一上报为 Worker 启动错误：

- 找不到 Pipe。
- 连接超时。
- token 校验失败。
- 协议版本不支持。
- manifest 与注册信息不一致。
- 节点类型不存在。
- 独立 EXE 缺少运行库。

不能只在 stderr 打印一行文本。

#### 日志通道

stdout/stderr 可能被 Python、C、C++ 依赖库自身使用。业务日志应通过协议发送给 Core，stderr 只保留最后级别的致命错误。

#### Pin 元数据校验

Worker SDK 应在注册阶段校验：

- Pin 名称。
- Pin 方向。
- DataType。
- Sample Key。
- 是否存在第一版未支持的动态 Pin。

这样 GUI 和 Core 可以在运行前发现 manifest 与 Worker 注册信息不一致的情况。

#### 版本兼容

协议消息至少应携带：

- Protocol version。
- SDK version。
- Package version。
- Core 支持的最低 SDK 版本。
- Node type 注册版本。

避免不同电脑部署的新旧 Worker 出现难以排查的协议不兼容问题。

#### Stop 期间的数据处理

需要明确 Stop 时：

- 队列中尚未处理的 Sample 是清空还是保留。
- 正在执行的节点函数是否等待完成。
- 清空时是否需要回应确认。
- Stop 超时后是强制 Faulted 还是继续等待。

第一版建议：

- Stop 发出后不再接收新的业务 Sample。
- 已有队列允许由实现清空。
- 当前回调等待完成，或超时后标记 Faulted。
- Stop 成功后再次 Run 时使用新的队列状态。

### 11.11 第一版推荐决策

为了控制首版复杂度，建议固定以下规则：

- 一个 Worker 进程承载多个节点实例，但每个实例有独立 inbox。
- 一个 transport reader、一个 transport writer、每个节点一个执行线程。
- 所有队列有界。
- Control 事件不丢弃，业务事件在队列满时拒绝或显式报错。
- Worker 启动时立即连接并完成注册、Ready。
- Stop 只停止节点，保留 Worker；Shutdown 才结束进程。
- 节点 Ready 后即可订阅广播，广播采用运行期间尽力投递。
- 节点输出、广播和日志全部进入 outbound queue，由单一 writer 发送。
- 回调异常统一捕获，节点进入 Faulted。
- 第一版不自动重连，不自动重启 Worker。
- 第一版不支持动态 Pin、NodeCommand 和 `GetService<T>`。

这样 Python、C、C++ 节点可以共享同一套 Worker Runtime 语义，并与 C# 原生节点的 Init、Run、Stop、Shutdown 生命周期保持一致。

### 11.12 多语言节点开发示例与模板

多语言节点 Demo 是正式交付内容，不是只在架构文档中描述几个接口。Python、C、C++ 至少各提供一个最小可运行 Demo，开发者可以复制后改造成实际业务节点。

#### Demo 位置

仓库中实际提供：

```text
Plugins/External/
├─ Python/
│  └─ Node.PythonUpper/
├─ C/
│  └─ Node.CUpper/
└─ Cpp/
   └─ Node.CppUpper/
```

每个 Demo 都应能单独阅读、编译或运行，避免依赖其他 Demo 的业务代码。

#### Demo 的统一内容

每个 Demo 至少包含：

```text
external-node-demo/
├─ plugin.json
├─ README.md
└─ Worker/
   ├─ Main.py
   ├─ Main.c
   └─ Main.cpp
```

实际每个 Demo 只保留对应语言的入口文件。

Demo 必须演示：

- Worker 启动后立即连接 Named Pipe / Unix Domain Socket。
- 完成 `worker.hello`、注册和 `worker.ready`。
- 创建节点实例。
- 实现 `Init`。
- 实现 `Run`。
- 实现 `Stop`。
- 实现 `Shutdown`。
- 接收一个 STRING 输入 Sample。
- 向一个 STRING 输出 Pin 发送 Sample。
- 发送一条广播。
- 发送日志。
- 正确处理异常和 Stop。
- 有明确的编译、启动、调试和连接说明。

#### Python Demo

建议目录：

```text
Plugins/External/Python/Node.PythonUpper/
├─ plugin.json
├─ README.md
├─ requirements.txt
└─ Worker/
   └─ Main.py
```

Demo 入口只负责启动 Worker Runtime：

```python
runtime = WorkerRuntime.from_manifest(...)
runtime.register_node(DemoNode())
runtime.connect()
runtime.run_forever()
```

业务节点只实现生命周期和 Pin 回调：

```python
class DemoNode:
    def init(self, ctx):
        pass

    def run(self, ctx):
        pass

    def stop(self, ctx):
        pass

    def shutdown(self, ctx):
        pass

    def on_pin_receive(self, pin_name, sample, ctx):
        pass
```

README 还需要说明如何用 PyInstaller 打包为不依赖目标电脑 Python 环境的独立 EXE。

#### C/C++ Demo

C 和 C++ 不应各自维护一套协议实现。底层协议、Sample、线程队列、Worker Runtime 和传输代码统一由 C SDK 实现，C++ 通过 C ABI 复用这套实现。

推荐 SDK 结构：

```text
SDK/
├─ C/
│  ├─ Include/
│  │  ├─ worker_runtime.h
│  │  ├─ sample.h
│  │  └─ protocol.h
│  └─ Src/
│     ├─ worker_runtime.c
│     ├─ sample.c
│     └─ protocol.c
└─ Cpp/
   └─ Include/
      └─ worker_runtime.hpp
```

C Demo：

```text
Plugins/External/C/Node.CUpper/
├─ plugin.json
├─ README.md
└─ Worker/
   └─ Main.c
```

C++ Demo：

```text
Plugins/External/Cpp/Node.CppUpper/
├─ plugin.json
├─ README.md
└─ Worker/
   └─ Main.cpp
```

C++ SDK 在底层包含 C 头文件：

```cpp
extern "C" {
#include "worker_runtime.h"
}
```

然后提供 C++ 便利封装：

```cpp
WorkerRuntime runtime = WorkerRuntime::from_manifest(...);
runtime.on_init(...);
runtime.on_run(...);
runtime.on_stop(...);
runtime.on_shutdown(...);
runtime.on_pin_receive(...);
```

C++ 封装底层仍然调用同一套 C Runtime、C Sample 和 C 协议实现。

#### C/C++“同一套代码”的边界

可以共用：

- 协议结构。
- NDJSON 编解码。
- Sample 数据结构。
- Named Pipe / UDS 传输。
- 线程安全队列。
- Worker Runtime。
- 状态机。
- 错误码。
- 日志和广播接口。
- C ABI 头文件。

不建议强行共用：

- 把 C 源文件直接交给 C++ 编译器编译。
- 将 C 节点回调签名和 C++ 类接口强绑定。
- 强制 C++ 节点完全使用 C 风格接口。

推荐关系：

```text
C Runtime / C ABI      ← 唯一底层实现
        ↑
C Node Demo            C++ Wrapper + C++ Node Demo
```

因此，“C 和 C++ 共用同一套”准确含义是：C 是底层唯一实现，C++ 兼容并复用 C SDK，而不是要求 C 和 C++ 的两个入口源文件完全相同。

#### Demo README 必须包含的内容

每个 Demo 的 README 至少说明：

1. Demo 功能说明。
2. 目录结构。
3. `plugin.json` 字段解释。
4. 如何编译。
5. 如何生成命名管道 / UDS 地址。
6. 如何启动 Worker。
7. 如何从 Core 加载。
8. 如何验证 Init、Run、Stop、Shutdown。
9. 如何验证输入和输出 Sample。
10. 如何验证广播。
11. 常见错误和排查方法。
12. Python 如何打包成独立 EXE。
13. C/C++ 如何复用同一 SDK。
14. 最小协议消息示例。
15. Debug 模式和 Core 正式加载模式的差异。

#### Demo 验收标准

每个 Demo 必须满足：

- Python Demo 能作为独立 EXE 在不安装 Python 环境的目标电脑上启动。
- C Demo 使用统一 C Runtime 编译运行。
- C++ Demo 复用 C Runtime，不复制另一套协议实现。
- 三个 Demo 都能完成完整生命周期。
- 三个 Demo 都能接收 Sample、发送 Sample、发送广播。
- 三个 Demo 在 Stop 后都能再次 Run。
- 三个 Demo 在 Shutdown 后都能正常退出。
- 出现异常时能通过协议上报错误，而不是静默退出。

### 11.13 生命周期精确语义

外部节点生命周期必须与 C# `BaseNode` 保持一致。

状态建议：

```text
Created
    ↓
Attached
    ↓
Initializing
    ↓
Initialized
    ↓
Running
    ↓
Stopping
    ↓
Stopped
    ↓
Initializing（允许再次 Run）
    ↓
ShuttingDown
    ↓
Shutdown
```

规则：

- `Init` 每次 Run 前都调用，负责重新创建运行时资源。
- `Init` 成功后才能 `Run`。
- `Init` 失败后节点进入 Failed，不能直接 Run。
- `Run` 回调应在节点已经具备运行条件后返回，不应阻塞到整个工作流结束。
- 如果节点需要连接、等待资源或后台线程启动，可以先保持 Connecting，达到可运行状态后再标记 Running。
- `Run` 返回成功表示节点进入可运行状态，不表示工作流结束。
- `Stop` 必须释放 Run 期间创建的资源，并且可以重复调用。
- `Stop` 后允许再次 `Init/Run`。
- Stop 超时后节点和对应 Worker 需要标记 Failed，并进入强制清理流程。
- `Shutdown` 是最终清理，只能在应用退出或 Worker 被永久销毁时执行。
- 如果节点在 Running 状态直接收到 Shutdown，Worker SDK 应视情况先执行 Stop 再执行 Shutdown。
- Shutdown 必须幂等。
- Shutdown 后节点实例不能再次 Run。
- 一个节点 Faulted 是否影响同 Worker 的其他节点，由 `fatal` 和错误策略决定。
- `fatal=true` 应停止相关工作流；`fatal=false` 只记录错误。

### 11.14 新节点脚手架和开发者快速开始

建议提供：

```text
Scripts/new_external_node.py
```

示例：

```text
python Scripts/new_external_node.py --language python --id Node.PythonUpper
python Scripts/new_external_node.py --language c --id Node.CUpper
python Scripts/new_external_node.py --language cpp --id Node.CppUpper
```

脚手架至少生成：

```text
Package/
├─ plugin.json
├─ build.json
├─ README.md
├─ Licenses/
└─ Worker/
   ├─ Main.py / Main.c / Main.cpp
   └─ Worker.config.json
```

开发者快速开始流程：

1. 使用脚手架创建 Package。
2. 修改 `packageId`、`nodeType`、显示名称、分类。
3. 声明输入/输出 Pin 和参数。
4. 实现 Init、Run、Stop、Shutdown。
5. 实现输入 Sample 和输出 Sample。
6. 实现广播订阅和发布。
7. 使用 FakeCore 进行本地协议测试。
8. 使用构建脚本生成 Debug 产物。
9. 安装到 `Plugins/External/<RID>` 并在 GUI 加载工作流。
10. 验证参数 Apply、Stop/Run、Shutdown。
11. 使用 Release 构建和干净环境做最终验收。

建议 README 模板包含：

- Package 功能说明
- manifest 字段说明
- 依赖要求
- 编译方法
- 本地调试方法
- 协议日志位置
- 参数和 Pin 说明
- 已知限制

### 11.15 FakeCore 调试工具

开发外部节点不能每次都依赖完整 GUI 或完整 CLI 工作流。

建议提供：

```text
ExternalWorkerTestClient
```

功能：

- 创建临时 Named Pipe / UDS
- 启动 Worker
- 接收并打印 NDJSON trace
- 发送 hello、welcome、register、ready 流程
- 创建节点实例
- 下发 configure
- 发送 init/run/stop/shutdown
- 发送 pin.receive
- 接收 pin.send
- 订阅和发布测试广播
- 模拟 Worker 崩溃
- 测试队列满、超时和非法消息

命令示例：

```text
ExternalWorkerTestClient.exe --package Plugins/External/win-x64/Node.PythonUpper --scenario lifecycle
ExternalWorkerTestClient.exe --package Plugins/External/win-x64/Node.PythonUpper --scenario pin
ExternalWorkerTestClient.exe --package Plugins/External/win-x64/Node.PythonUpper --scenario broadcast
ExternalWorkerTestClient.exe --package Plugins/External/win-x64/Node.PythonUpper --trace trace.ndjson
```

必须支持：

- `send init`
- `send run`
- `send pin`
- `subscribe broadcast`
- `publish broadcast`
- `stop`
- `shutdown`
- dump protocol trace

### 11.16 多工作流和 Worker 进程作用域

需要明确 Worker 进程作用域：

```text
每个工作流运行实例
    每个 Package
        一个 Worker 进程
```

具体规则：

- 同一个工作流实例内，同一个 Package 的多个节点实例共用一个 Worker。
- 两个同时运行的工作流实例如果使用同一个 Package，应各自启动独立 Worker。
- 两个工作流实例不能共享同一个 Worker 的节点运行状态。
- 每个 Worker 使用独立 endpoint，包含 hostSessionId、workflowInstanceId/runId 和 packageId。
- 同一 Package 的只读 SDK 资源可以由同一进程加载，但节点实例运行状态必须按 nodeInstanceId 隔离。
- Worker Stop 只停止节点，不停止 Worker；Workflow Host 关闭时才发送 Shutdown。
- CLI 多工作流运行时，每个实例独立返回退出码和错误结果。
- 需要记录 workflowInstanceId、runId、workerId、packageId、PID 的映射关系。

第一版不实现跨工作流共享 Worker；如果以后需要，必须重新定义状态、队列和广播隔离。

初版实现：

- `NodeRegistry.CreateWorkflowScope(logger)` 为每条工作流复制内部描述符，并为外部节点创建新的 `ExternalNodeRuntime`。
- CLI 的每个 `WorkflowInstanceRequest` 使用自己的 registry scope，并在 RunOnce 结束后释放该 scope。
- 已验证同一 Package 的两条并发工作流实例启动不同 PID，并分别返回退出码 0。

### 11.17 SDK API Reference

三种语言必须提供语义相同的接口，但各自保留本语言习惯。

#### Python API

建议接口：

```python
runtime = WorkerRuntime.from_manifest(manifest_path)
runtime.register_node("Node.PyUpper", PyUpperNode)
runtime.run_forever()
```

节点接口：

```python
class PyUpperNode:
    def configure(self, ctx, parameters): ...
    def init(self, ctx): ...
    def run(self, ctx): ...
    def stop(self, ctx): ...
    def shutdown(self, ctx): ...
    def on_sample(self, ctx, pin_name, sample): ...
    def on_broadcast(self, ctx, topic, payload): ...
```

Context 至少提供：

```text
params
inputs
send_sample
send_string
send_int32
send_int64
send_bool
send_float
send_double
send_bin
has_input
get_input
wait_for_input
subscribe
unsubscribe
publish
log
error
```

线程规则：

- 每个节点实例拥有独立执行线程。
- `configure/init/run/stop/shutdown/on_sample/on_broadcast` 默认不在同一时刻并发执行同一实例。
- `send_sample`、`publish`、`log` 必须允许从节点后台线程调用。
- 所有出站调用必须进入线程安全 outbound queue。
- 回调异常由 SDK 捕获并转换为结构化 error。
- Python asyncio 可以作为 SDK 内部实现细节，但节点作者默认使用同步回调。
- 如果支持 async 回调，必须明确其运行事件循环和取消方式。

#### C API

C API 使用不透明句柄：

```c
typedef struct AF_WorkerRuntime AF_WorkerRuntime;
typedef struct AF_NodeContext AF_NodeContext;
typedef struct AF_Sample AF_Sample;
```

建议 API：

```c
int af_worker_runtime_create_from_manifest(
    const char* manifest_path,
    AF_WorkerRuntime** out_runtime);

int af_worker_register_node(
    AF_WorkerRuntime* runtime,
    const char* node_type,
    const AF_NodeCallbacks* callbacks,
    void* user_data);

int af_worker_run_forever(AF_WorkerRuntime* runtime);
void af_worker_runtime_destroy(AF_WorkerRuntime* runtime);
```

Sample API：

```c
int af_sample_create_string(const char* key, const char* value, AF_Sample** out);
int af_sample_create_int64(const char* key, int64_t value, AF_Sample** out);
int af_sample_create_double_bits(const char* key, uint64_t bits, AF_Sample** out);
void af_sample_destroy(AF_Sample* sample);
```

发送 API：

```c
int af_node_send_sample(AF_NodeContext* ctx, const char* pin, const AF_Sample* sample);
int af_node_send_string(AF_NodeContext* ctx, const char* pin, const char* key, const char* value);
```

C 规则：

- 所有返回 `int` 的函数返回 0 表示成功，非 0 表示错误。
- 外部创建的 Sample 默认由创建者释放。
- `send_sample` 成功只表示进入 outbound queue，不表示下游收到。
- `af_node_send_sample` 可以在后台线程调用，但底层必须线程安全。
- 回调必须返回统一 `AF_Error` 或错误码结构。
- `user_data` 生命周期由节点作者保证至少持续到 Shutdown。
- C ABI 不得传递 C++ 异常、STL 对象或函数名称 mangling。
- SDK 必须导出稳定 C 符号。

#### C++ API

C++ 只做 C ABI 封装：

```cpp
auto runtime = WorkerRuntime::from_manifest("plugin.json");
runtime.register_node<PyUpperNode>("Node.PyUpper");
runtime.run_forever();
```

C++ 规则：

- 底层直接调用 C Worker Runtime。
- C++ 回调中的异常必须在 wrapper 内捕获并转换为 C 错误码。
- Sample 使用 RAII 包装 C `AF_Sample`。
- C++ 后台线程发送必须通过线程安全 wrapper。
- C++ 不复制协议、队列和 transport 实现。
- 必须明确最低 C++ 标准，建议 C++17。

#### 生命周期取消和后台错误

必须定义：

- `stop` 可以设置取消标志，唤醒阻塞的 `wait_for_input`。
- 节点回调收到 CancellationToken 或等价取消对象。
- Run 返回后启动的后台线程如果失败，必须调用 `node.error`。
- 后台线程错误是否 fatal 由业务代码明确填写。
- Worker/SDK 不得静默吞掉后台线程异常。
- Shutdown 必须请求后台线程退出，并在超时后记录错误。
- 已发送但未 flush 的 Sample 在 Stop/Shutdown 时的处理规则必须一致。

### 11.18 脚手架、模板和构建模板

#### new_external_node.py

建议参数：

```text
--language python|c|cpp
--id <packageId>
--node-type <nodeType>
--display-name <name>
--category <category>
--output <目录>
--force
```

生成结构：

```text
<Package>/
├─ plugin.json
├─ build.json
├─ README.md
├─ Licenses/
└─ Worker/
   ├─ Main.py / Main.c / Main.cpp
   └─ CMakeLists.txt（C/C++）
```

模板要求：

- 包含最小可运行 Worker
- 包含一个输入 Pin 和一个输出 Pin
- 包含一个 String 参数
- 包含 Init、Run、Stop、Shutdown
- 包含日志和错误示例
- 包含广播示例
- 包含 FakeCore 运行说明
- 包含 Debug/Release 构建说明
- 不包含开发机绝对路径

#### Python 模板

```python
from AgentFlowWorker import WorkerRuntime

class DemoNode:
    def init(self, ctx):
        pass

    def run(self, ctx):
        ctx.log_info("node started")

    def stop(self, ctx):
        pass

    def shutdown(self, ctx):
        pass

    def on_sample(self, ctx, pin_name, sample):
        if pin_name == "in" and sample.type == "STRING":
            ctx.send_string("out", sample.string_value.upper(), key="out")

runtime = WorkerRuntime.from_manifest("plugin.json")
runtime.register_node("Node.PythonUpper", DemoNode)
runtime.run_forever()
```

这只是模板骨架，实际 SDK 方法名必须和正式 API 一致。

#### C/C++ CMakeLists.txt 模板

```cmake
cmake_minimum_required(VERSION 3.20)
project(AgentFlowExternalNode CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(AgentFlowExternalNode
    Main.cpp
)

target_link_libraries(AgentFlowExternalNode PRIVATE AgentFlowWorker)

if (WIN32)
    set_property(TARGET AgentFlowExternalNode PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
```

C 节点：

```cmake
project(AgentFlowExternalCNode C)
add_executable(AgentFlowExternalCNode Main.c)
target_link_libraries(AgentFlowExternalCNode PRIVATE AgentFlowWorker)
```

必须定义：

- SDK 静态库目标名称。
- Include 目录。
- 输出目录。
- Debug/Release 配置。
- Windows `/MT`。
- Linux 静态 libstdc++ / libgcc 配置。
- 依赖复制规则。

#### 完整示例交付物

每个语言 Demo 必须包含：

- `plugin.json`
- `build.json`
- Worker 入口
- 构建文件
- README
- FakeCore 测试命令
- 预期协议 trace
- 最小 plugin schema 校验示例

三个 Demo 的验收流程一致：

```text
hello -> register -> ready
node.create -> configure -> init -> run
pin.receive -> pin.send
broadcast.subscribe -> broadcast.publish -> broadcast.receive
stop -> run again
shutdown
```

### 11.19 SDK API 冻结清单

实现 SDK 前必须先冻结接口，不能在模板和文档中继续使用“建议接口”。

#### Python 冻结项

```python
runtime = WorkerRuntime.from_manifest(manifest_path)

runtime.register_node_factory(
    "Node.PyUpper",
    lambda ctx: PyUpperNode()
)

runtime.run_forever()
```

必须明确：

- 第二个参数使用工厂函数，而不是裸类或单例对象。
- 每次 `node.create` 调用工厂创建独立节点实例。
- 多节点实例不得共享运行状态。
- `WorkerRuntime.from_manifest` 的路径基于 EXE 所在目录或 Package 根目录解析。
- `run_forever` 阻塞到 Worker Shutdown。
- `stop` 和 `shutdown` 可被取消。

Python `Sample` 建议定义：

```text
type
key
encoding
value
```

并提供类型化读取：

```python
sample.type
sample.as_string()
sample.as_bool()
sample.as_int32()
sample.as_int64()
sample.as_float()
sample.as_double()
sample.as_bin()
```

Context 正式接口：

```python
ctx.param_string(name, default=None)
ctx.param_bool(name, default=None)
ctx.param_int32(name, default=None)
ctx.param_int64(name, default=None)
ctx.param_float(name, default=None)
ctx.param_double(name, default=None)

ctx.has_input(pin)
ctx.get_input(pin)
ctx.wait_for_input(pin, timeout_ms=None)

ctx.send_sample(pin, sample)
ctx.send_string(pin, value, key="")
ctx.send_int32(pin, value, key="")
ctx.send_int64(pin, value, key="")
ctx.send_bool(pin, value, key="")
ctx.send_float(pin, value, key="")
ctx.send_double(pin, value, key="")
ctx.send_bin(pin, data, key="")

ctx.log_info(message, fields=None)
ctx.log_warning(message, fields=None)
ctx.log_error(message, fields=None)
ctx.emit_error(code, message, fatal=False)
```

Python 规则：

- `send_*` 可以从后台线程调用。
- 发送成功只表示进入 outbound queue。
- 队列满抛出 SDK 异常或返回明确错误。
- `wait_for_input` 支持 Stop 取消。
- Callback 异常统一映射到 lifecycle error 或 node.error。
- 节点作者不应直接访问 Pipe、Socket 或 outbound queue。

#### C ABI 冻结项

必须补齐：

```c
typedef struct AF_NodeCallbacks {
    void* user_data;
    int (*configure)(void* user_data, AF_NodeContext* ctx, const AF_ValueMap* params);
    int (*init)(void* user_data, AF_NodeContext* ctx, AF_CancellationToken token);
    int (*run)(void* user_data, AF_NodeContext* ctx, AF_CancellationToken token);
    int (*stop)(void* user_data, AF_NodeContext* ctx, AF_CancellationToken token);
    int (*shutdown)(void* user_data, AF_NodeContext* ctx, AF_CancellationToken token);
    int (*on_sample)(void* user_data, AF_NodeContext* ctx, const char* pin, const AF_Sample* sample);
    int (*on_broadcast)(void* user_data, AF_NodeContext* ctx, const char* topic, const AF_BroadcastPayload* payload);
} AF_NodeCallbacks;
```

C API 必须明确：

- `AF_Error` 完整结构
- `AF_ValueMap` 参数读取 API
- `AF_CancellationToken`
- `AF_BroadcastPayload`
- `AF_Logger`
- Sample 的 clone/destroy
- 后台线程发送 API 的线程安全
- 回调返回值 0/非 0 统一语义
- C ABI 导出宏，例如 `AF_EXPORT`
- SDK 版本查询函数

#### C++ 冻结项

```cpp
class WorkerRuntime {
public:
    static WorkerRuntime from_manifest(const std::string& path);
    template<typename TNode>
    void register_node(const std::string& node_type);
    void run_forever();
};
```

必须定义节点基类或回调接口、`NodeContext`、`Sample` RAII、异常转换和最低 C++ 标准。

### 11.20 build.schema.json 和构建脚本输出

第一版应提供：

```text
Contracts/build.schema.json
```

必要字段：

| 字段 | 说明 |
|---|---|
| language | python/c/cpp |
| buildSystem | pyinstaller/cmake |
| entry | Worker 入口 |
| sourceDir | C/C++ 源码目录 |
| target | CMake 目标 |
| artifactName | 产物名称 |
| packageMode | onedir/executable |
| requirements | Python 依赖文件 |
| pythonVersion | Python 版本 |
| toolchain | 可选工具链 |
| outputDir | 输出目录 |
| copyRuntimeDeps | 是否复制依赖 |
| extraArgs | 可选构建参数 |

构建脚本必须输出：

- Debug/Release
- 标准 RID：win-x64、win-arm64、linux-x64、linux-arm64、linux-arm
- 每包独立构建日志
- `build-report.json`
- 文件哈希
- 依赖检查结果
- 失败包汇总

`package-sdk-dll.py` 会按 `--rid` 调用 `build_external_nodes.py` 构建 Debug/Release 外部节点；Release 执行 FakeCore 验证，再把通过验证的产物、报告和哈希收进对应配置的 `Plugins/External/<RID>`。默认使用当前构建主机 RID；可重复传 `--rid` 收集多个平台已经构建好的产物。

SDK 根目录与 `SharedDlls` 同级还会生成 `Demo.Cli` 和 `Demo.Desktop` 两个最小运行示例工程。它们只负责选择工作流、加载 SDK 插件、运行和停止，不包含节点编辑、画布或属性面板。

### 11.21 FakeCore 场景文件格式

建议使用 YAML 或 JSON 测试场景：

```yaml
scenario: lifecycle
package: Node.PythonUpper
steps:
  - expect: worker.ready
  - send: node.create
    nodeType: Node.PythonUpper
    nodeInstanceId: 1001
    parameters:
      Suffix: ":py"
  - send: node.configure
    parameters:
      Suffix: ":py2"
  - send: node.command
    command: init
  - send: node.command
    command: run
  - send: pin.receive
    targetPin: in
    sample:
      dataType: STRING
      encoding: utf8
      value: hello
  - expect: pin.send
    sourcePin: out
  - send: node.command
    command: stop
  - send: node.command
    command: run
  - send: node.command
    command: shutdown
```

场景文件必须支持：

- expect worker.ready
- send node.create
- send node.configure
- send init
- send run
- send pin.receive
- expect pin.send
- subscribe/publish broadcast
- send stop
- send run again
- send shutdown
- 模拟超时、非法消息、Worker crash
- 自动断言关键字段
- 输出 trace.ndjson

---

## 12. 失败、超时和清理

必须定义：

```text
registerTimeoutMs
nodeCreateTimeoutMs
initTimeoutMs
runTimeoutMs
stopTimeoutMs
shutdownTimeoutMs
heartbeatIntervalMs
heartbeatTimeoutMs
maxLineBytes
maxOutboundQueue
```

规则：

- Worker 注册超时：启动失败。
- `node.create` 超时：节点加载失败。
- `init` 失败：工作流启动失败。
- `run` 失败：节点和相应工作流失败。
- `stop` 超时：强制终止 Worker，所有节点标记 Failed。
- `shutdown` 超时：强制终止进程。
- 心跳超时：Worker 标记 Failed。
- 连接断开：删除广播订阅，标记节点 Failed。
- Worker 退出：记录退出码、stderr 和最后日志。
- Windows 使用 Job Object 保证子进程树清理。
- Linux 使用进程组或 PID 树清理。

### 12.1 错误码和错误策略

错误码建议按大类命名：

```text
WORKER_*
PROTOCOL_*
PACKAGE_*
NODE_*
PIN_*
BROADCAST_*
```

建议错误码：

```text
WORKER_START_FAILED
WORKER_CONNECT_TIMEOUT
WORKER_HEARTBEAT_TIMEOUT
WORKER_CRASHED
PROTOCOL_VERSION_UNSUPPORTED
PROTOCOL_INVALID_MESSAGE
PROTOCOL_LINE_TOO_LARGE
PACKAGE_MANIFEST_INVALID
PACKAGE_DEPENDENCY_MISSING
PACKAGE_DUPLICATE_NODE_TYPE
NODE_CREATE_FAILED
NODE_CONFIGURE_FAILED
NODE_INIT_FAILED
NODE_RUN_FAILED
NODE_STOP_FAILED
NODE_SHUTDOWN_FAILED
NODE_INVALID_STATE
PIN_UNKNOWN
PIN_TYPE_MISMATCH
PIN_QUEUE_FULL
PIN_SEND_REJECTED
BROADCAST_TOPIC_INVALID
BROADCAST_QUEUE_FULL
```

错误对象至少包含：

```text
code
severity
message
nodeInstanceId（如适用）
workerId
correlationId（如适用）
fatal
timestampUtc
```

规则：

- `fatal=true` 的错误应停止当前节点，并按 NodeErrorPolicy 影响工作流。
- `fatal=false` 的错误只记录并继续运行。
- 第一版外部节点默认按 FailFast 处理。
- NodeErrorPolicy 的映射必须明确到 Worker SDK，不能只依赖字符串判断。
- 协议错误通常视为 Worker 级错误。
- Pin 类型错误属于节点业务错误或路由错误，需标记是否 fatal。
- Worker 崩溃时，同 Worker 内所有节点标记 Failed。
- 一个节点 Faulted 不等于整个 Worker 一定退出。
- 错误消息必须可结构化解析，不能只放一段无法分类的文本。

### 12.2 诊断信息

建议记录：

- workflowInstanceId
- runId
- workerId
- packageId
- nodeInstanceId
- processId
- 错误码
- correlationId
- 最后一条协议消息
- Worker 退出码
- stderr 最后若干行

日志限制：

- 单条日志有最大长度。
- 高频日志需要采样或限流。
- 大对象输出需要截断。
- 敏感参数不得输出。
- stdout/stderr 只用于本地调试或最后级别错误。
- 业务日志必须通过 `log` 协议发送给 Core。
- Debug 模式可以保存完整 NDJSON trace，但默认关闭。
- Release 模式默认不记录完整 protocol trace。

### 12.3 常见问题排查

| 问题 | 常见原因 | 排查方向 |
|---|---|---|
| 管道连接失败 | endpoint 不存在、token 错误、ACL 拒绝 | 检查 Core 是否先创建端点、Worker 参数和权限 |
| `worker.ready` 超时 | Worker 未启动、注册失败、队列初始化异常 | 查看 Worker stderr、协议 trace 和执行阶段 |
| manifest 加载失败 | JSON 非法、字段缺失、路径不存在 | 使用 `plugin.schema.json` 校验 |
| nodeType 不存在 | manifest 与 Worker 注册不一致 | 比较 packageId、nodeType 和 register 消息 |
| Python Worker 启动失败 | 缺少 `_internal`、依赖未收集、DLL 缺失 | 在无 Python 环境测试，检查 PyInstaller 日志 |
| C/C++ Worker 启动失败 | VC Runtime/DLL/SO 缺失、glibc 不兼容 | 使用 `dumpbin /dependents` 或 `ldd` |
| UDS 路径太长 | 完整路径超过平台限制 | 使用短 GUID 和 packageHash |
| UDS 残留文件 | 上次异常退出未删除 `.sock` | 检查是否有活动 Worker，再安全清理 |
| token 不匹配 | 参数替换错误或启动了旧 Worker | 检查启动命令和 Worker 日志 |
| 参数类型错误 | manifest 类型与 Worker 转换不一致 | 检查 `node.configure.result` 和参数 schema |
| Pin 类型不匹配 | 输入输出 DataType 不一致 | 检查 manifest 和 `pin.send` error |
| 队列满 | 下游处理慢或 outbound 队列太小 | 检查队列容量、背压和日志速率 |
| Worker 崩溃 | 未捕获异常、原生崩溃、内存错误 | 保存退出码、stderr 和最后的 protocol trace |
| stop 超时 | 业务线程阻塞或不响应取消 | 检查 CancellationToken 和后台线程退出 |
| 广播收不到 | 没有订阅、Topic 不一致、队列满 | 检查 subscribe 结果和 Core TopicBus |

排查顺序建议：

1. 检查 Package 是否被扫描到。
2. 检查 manifest schema。
3. 检查 Worker 是否能独立启动。
4. 检查 endpoint 和 token。
5. 检查 hello/register/ready 流程。
6. 检查 node.create/configure。
7. 检查 init/run 生命周期结果。
8. 检查 Pin 和广播 trace。
9. 检查进程退出码和依赖。

---

## 13. 安全设计

第一版安全边界：

- 只允许加载可信外部 Package。
- 外部 Worker 只连接本机 Named Pipe/UDS。
- 不使用网络 TCP 端口作为默认生产传输。
- Worker 可执行文件路径必须限制在 Package 目录内。
- 禁止通过 shell 启动，避免命令注入。
- Named Pipe 使用 ACL，限制当前用户或当前会话。
- Linux UDS 路径权限建议 `0600`，放在用户私有运行目录。
- 注册使用随机启动令牌。
- 限制消息大小和行长度。
- 限制连接数、节点实例数和队列长度。
- 记录 Package ID、Worker ID、PID、退出码和版本。
- 后续可增加 Package 签名和哈希校验。

令牌和安全参数传递注意事项：

- 启动令牌放在命令行参数中可能被本机进程查看工具看到。
- 第一版可以接受本机可信环境下的短生命周期随机 token。
- 更稳妥的方式是通过受限环境变量或临时受限配置文件传递 token。
- token 只应在 Worker 启动和握手阶段使用，不写入普通日志。
- Core 端应在连接后校验 token，并在有限次失败后拒绝连接。
- Named Pipe 名称和 UDS 路径不应包含敏感信息。
- Worker 启动目录和可执行文件路径必须经过规范化并验证仍在 Package 目录内。
- 外部 Worker 默认按可信代码执行，不提供网络和文件系统沙箱。

---

## 14. 打包和部署

### 14.1 内部 C# 插件

保持现状：

```text
Plugins/*.dll
```

### 14.2 外部 Python Package

建议使用 PyInstaller `onedir`：

```text
Node.PythonUpper/
    plugin.json
    Worker/
        AgentFlow.PythonUpper.exe
        _internal/
```

优点：

- 新电脑无需安装 Python。
- 每个节点独立打包。
- 依赖隔离。
- 一个节点升级不影响其他节点。

### 14.3 C/C++ Package

直接发布本地可执行文件：

```text
Node.CppSuffix/
    plugin.json
    Worker/
        AgentFlow.CppSuffix.exe
```

Windows 建议明确 MSVC Runtime 方案；如需完全独立，可使用静态运行时或满足部署要求。

### 14.4 Package manifest

示意：

```json
{
  "manifestVersion": 1,
  "packageId": "Node.PythonUpper",
  "displayName": "Python Upper",
  "version": "1.0.0",
  "description": "Python 外部节点示例",
  "metadata": {
    "publisher": "Damon Yang",
    "author": "Damon Yang",
    "copyright": "Copyright (c) 2026 Damon Yang. All rights reserved.",
    "license": "MIT",
    "licenseFile": "Licenses/LICENSE"
  },
  "worker": {
    "protocolVersion": 1,
    "executable": "Worker/AgentFlow.PythonUpper.exe",
    "arguments": [
      "--endpoint",
      "${WORKER_ENDPOINT}",
      "--token",
      "${WORKER_TOKEN}"
    ]
  },
  "nodeTypes": [
    {
      "nodeType": "Node.PythonUpper",
      "displayName": "Python Upper",
      "category": "E",
      "pins": [
        {"name": "in", "direction": "input", "dataType": "STRING", "required": true},
        {"name": "out", "direction": "output", "dataType": "STRING", "required": true}
      ],
      "parameters": [
        {
          "name": "Suffix",
          "type": "string",
          "defaultValue": ":py",
          "editable": true,
          "group": "General"
        }
      ]
    }
  ]
}
```

说明：

- 参数和 Pin 元数据来自 manifest，GUI 不需要启动 Worker。
- `node.create` 时 Core 把参数值发送给 Worker。
- `worker.register` 可再次校验 Package、协议和节点类型。
- `arguments` 中的 `${WORKER_ENDPOINT}` 由 Core 按平台替换。
- Windows 替换为 Named Pipe 名称。
- Linux 替换为 UDS 路径。
- `metadata` 属于 Package，所有 nodeType 默认继承；GUI 展示元数据以 manifest 为准。
- Windows EXE 的版本资源必须由 PyInstaller version file 或 C/C++ `.rc` 文件在构建时写入，不能替代 manifest。
- 第一版每个 Package 建议只声明一个 nodeType。

### 14.5 构建、打包和验证脚本

Python、C、C++ 节点的构建与打包统一由一个 Python 编排脚本负责：

```text
Scripts/build_external_nodes.py
```

建议分工：

```text
build_external_nodes.py
    发现节点包
    校验 build.json
    打包 Python 节点
    调用 CMake 编译 C/C++ 节点
    输出 Debug/Release 产物
    执行产物自检
    生成构建报告

package-sdk-dll.py
    构建 Debug/Release 外部节点
    执行 Release FakeCore 验证
    收集已经验证的产物
    收集 C# DLL、契约源码、SDK 头文件
    生成并压缩最终 SDK
```

不要把全部编译、打包和 SDK 压缩逻辑都塞进 BAT 文件，否则后续 Linux 和 CI 难以复用。

#### build.json

运行时元数据放在 `plugin.json`，构建工具配置建议放在包目录下的 `build.json`，避免把构建路径和工具链信息混入运行协议。

Python 示例：

```json
{
  "language": "python",
  "buildSystem": "pyinstaller",
  "entry": "Worker/Main.py",
  "artifactName": "PythonDemoNode",
  "packageMode": "onedir",
  "requirements": "Worker/requirements.txt",
  "pythonVersion": "3.12"
}
```

C 示例：

```json
{
  "language": "c",
  "buildSystem": "cmake",
  "sourceDir": "Worker",
  "target": "CFactDemoNode",
  "artifactName": "CFactDemoNode"
}
```

C++ 示例：

```json
{
  "language": "cpp",
  "buildSystem": "cmake",
  "sourceDir": "Worker",
  "target": "CppFactDemoNode",
  "artifactName": "CppFactDemoNode",
  "useCWorkerRuntime": true
}
```

`useCWorkerRuntime` 用于明确 C++ Worker 复用 C SDK，不复制另一套协议、Sample 和队列实现。

#### 统一命令

```text
python Scripts/build_external_nodes.py doctor
python Scripts/build_external_nodes.py discover
python Scripts/build_external_nodes.py build --all --config Debug --rid win-x64
python Scripts/build_external_nodes.py build --all --config Release --rid linux-x64
python Scripts/build_external_nodes.py build --package Node.PythonUpper --config Release --rid win-x64
python Scripts/build_external_nodes.py build --language cpp --config Release --rid linux-arm64
python Scripts/build_external_nodes.py verify --config Release --rid win-x64
python Scripts/build_external_nodes.py package --config Release --rid win-x64
python Scripts/build_external_nodes.py sync --all --config Debug --rid win-x64 --destination <runtime-Plugins-External> --incremental
python Scripts/build_external_nodes.py clean --all
```

建议参数：

```text
--config Debug|Release
--rid win-x64|win-arm64|linux-x64|linux-arm64|linux-arm
--all
--package <目录名>
--language python|c|cpp
--output <目录>
--clean
--verbose
--dry-run
--fail-fast
--jobs N
```

默认策略：

- `build` 自动发现并构建所有外部节点包。
- 单个包失败时记录错误并继续构建其他包。
- 最终有一项失败时脚本返回非 0。
- `--fail-fast` 时第一个失败立即停止。
- `--dry-run` 只打印将执行的命令。

#### 输出目录

```text
artifacts/External/
├─ Debug/
│  ├─ win-x64/
│  │  ├─ Node.PythonUpper/
│  │  ├─ Node.CUpper/
│  │  └─ Node.CppUpper/
│  ├─ linux-x64/
│  ├─ linux-arm64/
│  └─ linux-arm/
└─ Release/
   ├─ win-x64/
   ├─ linux-x64/
   ├─ linux-arm64/
   └─ linux-arm/
```

不要把构建过程直接覆盖到正式 `Plugins/External` 运行目录。先输出到 `artifacts/External`，通过验证后再发布。

`--rid` 必须与实际构建主机匹配。PyInstaller、C/C++ 编译器和 FakeCore 验证都应在目标平台执行：

```text
Windows x64      -> win-x64
Windows ARM64    -> win-arm64
Linux x86_64     -> linux-x64
Linux ARM64      -> linux-arm64
Linux ARM32      -> linux-arm
```

默认只构建当前主机的 RID。需要从一个 SDK 收集多个 RID 时，应在对应平台分别构建，再用可重复的 `--rid` 参数打包。不要把 Windows 上生成的 `.exe` 放入 Linux RID 目录，也不要把 Linux ELF 可执行文件放入 Windows RID 目录。

#### 主工程自动构建

`Directory.Build.targets` 在 Desktop/CLI 的普通 Build 和 Publish 后调用：

```text
python Scripts/build_external_nodes.py sync --all --config <Configuration> --rid <RID> --destination <runtime-Plugins-External> --incremental
```

规则：

- 计算 `Plugins/External` 源码指纹，排除 RID 产物目录、`build/`、`dist/`、`__pycache__/` 和编译产物。
- 源码指纹未变化且 `artifacts/External/<Configuration>/<RID>` 完整时，不重复执行 PyInstaller/CMake，只重新复制到宿主输出目录。
- 首次构建、源码变化或产物缺失时才重新构建。
- 普通 Build 复制到 `$(OutDir)Plugins/External/<RID>`。
- Publish 同时复制到 `$(PublishDir)Plugins/External/<RID>` 和 `$(OutDir)Plugins/External/<RID>`。
- Desktop 与 CLI 并行构建时使用构建锁，避免同时写同一外部节点产物。

#### Python 节点打包

使用 PyInstaller `onedir`：

```text
1. 创建隔离的虚拟环境
2. 安装锁定版本的 requirements
3. 安装固定版本的 PyInstaller
4. 执行 PyInstaller onedir
5. 复制 plugin.json、配置和资源
6. 执行产物自检
7. 生成 checksum 和构建报告
```

要求：

- 目标电脑不需要安装 Python。
- 固定 Python、PyInstaller 和第三方依赖版本。
- 所有第三方原生 DLL 一并收集。
- 不得依赖开发机虚拟环境路径。
- 不得依赖目标电脑 `PATH` 中的 Python。
- 不得在运行时执行 `pip install`。
- 资源路径基于 EXE 所在目录解析。

#### C/C++ 节点编译

Python 脚本不直接拼接编译器参数，而由 CMake 负责实际编译：

```text
Python Build Orchestrator
        ↓
CMake / CMakePresets.json
        ↓
Visual Studio / Ninja / GCC / Clang
```

至少提供：

```text
debug-win-x64
debug-win-arm64
debug-linux-x64
debug-linux-arm64
debug-linux-arm
release-win-x64
release-win-arm64
release-linux-x64
release-linux-arm64
release-linux-arm
```

第一版建议将 C Worker Runtime 构建为静态库：

- C 是唯一的底层 Runtime 实现。
- C++ 节点链接同一个 C Runtime。
- 减少额外 DLL/SO 路径问题。
- 后续如果需要多个外部节点共享更新，再考虑动态库。

### 14.6 新系统独立运行要求

必须区分：

```text
构建环境依赖：
    Python
    pip
    PyInstaller
    CMake
    Visual Studio / GCC / Clang

目标运行环境依赖：
    Release 产物应不需要上述开发工具
```

最终要求是：

> Release 产物复制到全新的目标电脑后，不安装 Python、CMake、编译器或开发环境也能启动并正常工作。

构建时允许依赖 Python、PyInstaller、CMake 和编译器；运行时不依赖这些开发工具。外部 Worker 只需要随包发布的 EXE、运行库和 Core 提供的 Named Pipe / UDS 端点即可运行。

外部节点进程通过端点参数连接 Core，不使用需要额外服务或开发环境支持的匿名管道方案。

#### 支持矩阵

首版建议明确支持范围，例如：

```text
Windows 10/11 x64
Ubuntu 22.04/24.04 x64
```

不同操作系统必须分别构建，Windows 构建出的 Worker 不能假设能在 Linux 运行。  
Linux 还必须明确支持的 glibc 下限，或在足够老的兼容环境 / musl 环境构建。

#### Python 产物要求

Python 节点发布包应包含：

```text
python-demo/
├─ PythonDemoNode.exe
├─ _internal/
│  ├─ python DLL
│  ├─ 第三方依赖
│  └─ 运行资源
├─ plugin.json
└─ README.md
```

Release 验收时目标电脑：

- 没有 Python。
- 没有 pip。
- 没有虚拟环境。
- 没有 PyInstaller。
- 无法从 `PATH` 找到 Python。
- Worker 仍能启动并完成握手。

#### C/C++ 产物要求

Windows：

- Release 建议使用 `/MT` 静态链接 MSVC Runtime。
- 不依赖开发机的 Visual Studio 安装目录。
- 使用 `dumpbin /dependents` 检查是否缺少 `VCRUNTIME140.dll`、`MSVCP140.dll` 等。
- 无法静态链接的第三方 DLL 必须随包发布。

Linux：

- 使用 `ldd` 检查动态库依赖。
- 尽量静态链接 libgcc / libstdc++。
- glibc 仍可能是动态依赖，因此必须声明支持的发行版和 glibc 范围。
- 第三方 SO 无法静态链接时必须随包发布。

Release 包不得包含开发机绝对路径，不得依赖当前工作目录，也不得假设 SDK 位于某个固定安装目录。

当前脚本会在打包前扫描文本型运行文件，拒绝包含项目根目录绝对路径的 Release 包；验证日志中的临时目录也会替换为 `<isolated-clean-room>`。

#### 构建环境检查

脚本应支持：

```text
python Scripts/build_external_nodes.py doctor
```

检查：

- Python 版本。
- pip 是否可用。
- PyInstaller 是否安装且版本正确。
- CMake 是否安装且版本正确。
- C/C++ 编译器是否存在。
- 目标架构是否支持。
- 输出目录是否可写。
- 构建工具链版本是否满足要求。

#### 产物验证

脚本应支持：

```text
python Scripts/build_external_nodes.py verify --config Release
```

验证内容：

- 产物目录完整。
- `plugin.json` 存在且合法。
- Worker 可执行文件存在。
- Worker 能启动。
- 能连接测试用 Named Pipe / UDS。
- 能完成 hello、注册、ready。
- 能完成 Init、Run、Stop、Shutdown。
- 能接收和发送 Sample。
- 能发送广播。
- 没有缺失依赖或开发机绝对路径。

建议提供独立的最小测试宿主：

```text
ExternalWorkerSmokeTest
```

它创建临时 Named Pipe / UDS，模拟 Core 完成握手和生命周期测试，不要求启动完整工作流。

#### 干净环境验收

Release 不能只在开发电脑上验证。开发电脑可能已经安装了 Python、VC Runtime、CMake 等组件，会掩盖真实依赖。

至少应在以下环境测试：

- 全新 Windows 虚拟机或 Windows Sandbox。
- 干净 Linux 容器或新虚拟机。
- 清空 PATH 中的 Python 和开发工具路径。
- 目标电脑不安装 Visual Studio。
- 目标电脑不安装 CMake。
- 目标电脑不安装 Python。

本轮实际验收记录（2026-11-14）：

- Python Demo 已生成独立 EXE；在 Worker 子进程 PATH 不含 Python、pip、PyInstaller、CMake、cl.exe、link.exe 的环境中完成握手、生命周期、Pin 和广播测试。
- C/C++ Demo 在 Worker 子进程 PATH 不含编译器和 CMake 的环境中完成同类测试。
- 三个 Package 均从 `<RID> 对应的 AgentFlow.ExternalNodes.Release.<RID>.zip` 解压到独立目录后再次通过 FakeCore 验证。
- C/C++ Release 的 dumpbin 依赖仅包含 `KERNEL32.dll`；未发现 `VCRUNTIME140.dll`、`MSVCP140.dll` 等动态 MSVC Runtime 依赖。
- 上述验证不能替代真实全新 OS 虚拟机；它的价值是排除当前开发机 PATH、SDK 目录和构建目录的偶然依赖。

验收标准：

- Python Demo 在没有 Python 的新电脑上可作为独立 EXE 启动。
- C Demo 在没有编译器的目标电脑上可运行。
- C++ Demo 复用 C Runtime，并在没有编译器的目标电脑上可运行。
- 三个 Demo 都能完成完整生命周期。
- Stop 后可再次 Run。
- Shutdown 后正常退出。
- 异常通过协议上报，不静默崩溃。
- 构建报告记录工具链版本、依赖版本、哈希和验证结果。

Debug 产物可以依赖更多开发环境组件，但必须明确说明它不是最终部署包。SDK 发布包中只应收录通过 Release 干净环境验证的产物。

### 14.7 Package 自包含部署验收

构建和打包脚本必须把整个 `Worker/` 目录作为部署单元，不允许将不同 Package 的依赖混合到公共目录中。

发布前应检查：

- `plugin.json` 声明的可执行文件存在。
- `Worker/` 内包含 EXE 及其全部运行依赖。
- Python 产物包含完整的 `_internal/` 和数据文件。
- C/C++ 产物包含无法静态链接的 DLL/SO。
- 没有指向开发机的绝对路径。
- 没有依赖其他 Package 的相对路径。
- 没有从全局 `PATH` 加载运行依赖。
- Debug 和 Release 文件没有混用。
- 资源文件、许可证和 NOTICE 已随包发布。
- 单独的 Package 复制到新环境后可以独立启动。

构建脚本必须：

- 按 Package 设置独立输出目录。
- 复制整个 Worker 构建产物，不只复制主 EXE。
- 不把本次构建产物与上一次残留文件混在一起。
- 为每个文件生成哈希或构建报告。
- 在产物自检失败时拒绝发布 Package。

C/C++ 额外检查：

- Windows 使用 `dumpbin /dependents` 检查 DLL 依赖。
- Linux 使用 `ldd` 检查 SO 和 glibc 依赖。
- 使用 `$ORIGIN` / `RPATH` / DLL 搜索路径时，必须验证干净环境和不同启动目录下都能正常工作。
- 无法静态链接的第三方库必须随 Package 发布。

Python 额外检查：

- 不安装 Python 的目标电脑可以启动 EXE。
- `_internal/` 完整存在。
- 第三方原生 DLL 被 PyInstaller 正确收集。
- 不依赖开发机虚拟环境路径。
- 资源加载基于 EXE 所在目录，而不是当前工作目录。

### 14.8 版本升级、兼容和回滚

版本字段用途：

```text
manifestVersion: plugin.json 格式版本
protocolVersion: Core 与 Worker 通信协议版本
sdkVersion: Python/C/C++ SDK 版本
packageVersion: 外部节点业务版本
```

兼容规则：

- `packageVersion` 建议使用 SemVer。
- `protocolVersion` 主版本相同才允许连接。
- 增加可选字段、可选消息和可选能力可以保持次版本兼容。
- 删除字段、改变字段含义或改变消息顺序必须升级主版本。
- Worker 注册的 protocolVersion 必须与 manifest 一致。
- SDK 版本只用于诊断和兼容提示，不替代协议版本校验。
- Core 应支持一个明确的最小 SDK/协议版本范围。
- 不兼容时 Core 拒绝加载 Worker，并显示明确错误。

工作流兼容：

- 修改 Pin 名称、方向或 DataType 属于破坏性变更。
- 修改参数名或参数类型属于破坏性变更。
- 删除 Pin 或参数需要在工作流加载时给出明确迁移错误。
- 新增可选参数和可选 Pin 应保证旧工作流仍可加载。
- 第一版不支持外部节点热升级。
- 升级流程建议：停止工作流、替换 Package、重新加载、验证版本。
- Package 回滚必须保留旧版本文件夹或通过构建产物重新部署。
- 每个发布 Package 应记录文件哈希和构建报告。
- SDK 发布包应包含协议版本和兼容矩阵。

### 14.9 C/C++ SDK ABI 兼容规则

第一版建议定义：

```text
AF_SDK_ABI_VERSION = 1
```

规则：

- C API 使用稳定符号 `extern "C"` 导出。
- 不允许向 C ABI 传递 STL 类型、C++ 异常或编译器相关对象。
- 结构体字段只能追加，不能随意修改已有字段大小、顺序或语义。
- 新增 API 使用新函数名，不能改变旧函数签名。
- SDK 必须提供运行时版本查询函数，例如 `af_worker_sdk_version()`。
- Core 和 Worker 握手时校验 `sdkVersion` 和 `protocolVersion`。
- C++ wrapper 可以随 SDK 版本变化，但底层 C ABI 必须稳定。
- Windows 静态库命名、Linux `.a` 命名和 Include 目录必须固定。
- Release SDK 必须附带 ABI 版本和兼容矩阵。
- Worker 不得自行假设 SDK ABI 版本。

兼容判断建议：

```text
protocolVersion 主版本一致
+ AF_SDK_ABI_VERSION 一致
+ 支持的 SDK 版本范围内
= 允许加载
```

不满足时必须拒绝加载并上报明确错误。

---

## 15. Core 增量改造清单

只做增量改造，不重写现有架构：

1. `NodeDescriptor` 支持可选工厂。
2. `NodeRegistry.CreateInstance` 优先使用工厂创建 Proxy。
3. 新增 `ExternalNodeCatalog` 和 `ExternalNodeDefinition`。
4. `WorkflowHost` 增加外部节点目录加载入口。
5. 加载顺序：
   - 先扫描内部 C# 插件。
   - 再扫描 `Plugins/External/<当前标准 RID>`。
   - 重复 nodeType 必须报错。
6. 工作流加载时创建 `ExternalNodeProxy`。
7. Proxy 在实例创建/工作流加载阶段启动 Worker。
8. Proxy 在 `Initialize/Run/Stop/Shutdown` 中映射远程命令。
9. Core Broadcast Gateway 与现有 TopicBus 对接。
10. 现有工作流图、Pin 路由和节点调度顺序不变。
11. 增加 `plugin.schema.json` 和 manifest 校验器。
12. WorkerSupervisor 实现 hello、welcome、register、registered、ready 状态机。
13. `ExternalNodeProxy` 支持 `Configure` 映射 `node.configure`。
14. 明确每个工作流运行实例的 Worker 作用域和 endpoint 唯一性。
15. 增加 FakeCore 测试工具和开发者脚手架脚本。

---

## 16. 测试计划

必须覆盖：

- manifest 解析和版本校验。
- 重复 nodeType 冲突。
- 无效 Package 隔离失败。
- 动态发现和 GUI descriptor。
- 一个 Worker 多个节点实例。
- `init/run/stop/shutdown` 成功路径。
- Stop 后再次 Run。
- Worker 崩溃。
- init/run/stop/shutdown 超时。
- Pin 输入输出。
- 所有 DataType。
- float/double 位模式 round-trip。
- Sample null 与空值区分。
- 广播订阅、发布、分发和断开清理。
- 广播在 Loaded/Stopped 状态的行为。
- Named Pipe 在 Windows 的 ACL 和连接测试。
- UDS 在 Linux 的权限和清理测试。
- 大消息限制。
- 并发写入是否串行。
- Worker 退出后进程树清理。
- PyInstaller 包启动和协议测试。
- plugin.json JSON Schema 校验。
- worker.hello/welcome/register/registered/ready 顺序和超时。
- node.configure 参数更新和类型校验。
- Run 返回后节点仍保持 Running 的语义。
- Stop 超时、重复 Stop 和再次 Run。
- 输入队列满、输出队列满和控制事件优先级。
- pin.send 未连接 Pin、类型错误和未知 Pin。
- null Sample 与缺失 Sample 区分。
- 结构化 error、node.error、log 字段校验。
- NodeErrorPolicy 和 fatal 错误映射。
- 同一 Package 多工作流实例的 Worker 隔离。
- protocolVersion/packageVersion 不兼容和升级回滚。
- 启动 token 不进入日志。
- FakeCore 生命周期、Pin、广播和崩溃场景。
- 外部目录不存在、为空和无有效 manifest 时走原生 C# 快速路径。
- 工作流没有外部 nodeType 时不创建 Worker、Pipe 或 UDS。
- plugin.schema.json 和 worker-message.schema.json 校验。
- test-vectors 合法和非法消息覆盖。
- worker.register nodeTypes 对象结构和重复 nodeType。
- payloadKind 四种广播负载 round-trip。
- Python、C、C++ SDK API 行为一致性。
- C ABI 内存所有权、导出符号和 ABI 版本。
- 后台线程错误在上报后节点状态正确。
- stop 取消阻塞 wait_for_input 和后台线程。
- 外部 Package 刷新、删除和运行期间禁止热加载。
- 原生节点在外部包损坏时仍能正常加载运行。
- ExternalCatalogResult 空结果和损坏 Package 隔离。
- 生命周期状态转移表所有合法和非法组合。
- capabilities 必需能力、可选能力和未知能力。
- build.schema.json 校验和 build-report.json 输出。
- FakeCore 场景文件、自动断言和 trace.ndjson。
- SDK API 冻结项和 Python/C/C++ API 一致性。

---

## 17. 实施阶段

### 阶段 1：设计确认

- 本文件评审并通过。
- 确认 Package 只包含一个节点类型还是一个 Package 可包含多个。
- 确认广播在 Stopped 状态是否继续处理。
- 确认 Worker 崩溃后是否自动重启；首版建议不自动重启。

### 阶段 2：Core 增量扩展 + Fake Worker

- NodeDescriptor 工厂。
- ExternalNodeCatalog。
- `plugin.schema.json` 和 manifest 校验。
- ExternalNodeProxy。
- WorkerSupervisor 握手和 ready 状态机。
- 用测试 Worker / FakeCore 验证协议。

### 阶段 3：Python Worker SDK + 独立 EXE

- Python SDK。
- `new_external_node.py` Python 脚手架。
- 一个 Python Echo/Suffix 节点。
- PyInstaller onedir。
- FakeCore 生命周期、Pin、广播和崩溃测试。
- 验证生命周期、Pin、日志。

### 阶段 4：广播

- 订阅、发布、接收。
- Worker 断开清理。
- 加载阶段广播可用。

### 阶段 5：C++ 和 C Worker SDK

- 验证跨语言 Sample。
- 验证 Named Pipe/UDS 客户端。
- 验证异常和超时。

### 阶段 6：打包和发布

- 外部 Package 打包。
- SDK 打包脚本按标准 RID 补充 `Plugins/External/<RID>`。
- 文档更新。

---

## 18. 风险与待确认问题

1. 一个 Package 是否严格只允许一个 nodeType？
   - 当前建议：首版一个 Package 一个 nodeType，以保持独立打包和隔离。
2. 一个 Worker 承载多个节点实例时，不同实例是否允许共享全局资源？
   - 当前建议：允许共享只读资源，不允许共享运行状态。
3. 广播在 `Stopped` 状态是否继续投递？
   - 当前建议：订阅保持，Worker 可选择继续处理控制类 Topic，数据类 Topic由节点自己忽略。
4. Worker 崩溃后是否自动重启？
   - 当前建议：不自动重启；标记节点失败并停止相关工作流。
5. NodeCommand 和 GetService 后续是否需要？
   - 当前第一版明确不支持。
6. 动态 Pin 是否永远不支持？
   - 当前第一版不支持；以后如需修改 Pin，要重新设计 GUI 同步和连接校验。
7. 外部 Package 是否允许加载第三方原生库？
   - 需要明确依赖打包和信任策略。
8. 外部 Worker 是否允许访问网络和文件系统？
   - 第一版按可信节点处理；后续可增加沙箱和权限配置。

---

## 19. 总结

本设计采用：

```text
Core 统一图调度和路由
+ Plugins/External/<当前标准 RID> 动态 manifest 发现
+ NodeRegistry 工厂扩展
+ ExternalNodeProxy
+ MultilingualHost 进程监督
+ Named Pipe / Unix Domain Socket
+ NDJSON 协议
+ Python/C/C++ Worker SDK
```

该方案的优势：

- 不破坏现有 C# 节点和内部插件机制。
- 外部节点可以作为独立节点类型出现在 GUI。
- 外部节点具备一致的生命周期。
- 一个 Worker 可承载多个节点实例。
- 广播仍然由 Core 统一分发。
- 每个外部节点可以独立打包。
- 后续可以从 Named Pipe/UDS 扩展到其他传输，而无需改变节点业务代码。

---

## 20. 新增外部节点实施清单

开发者新增一个外部节点时，按以下顺序执行：

1. 使用 `new_external_node.py` 创建 Package 骨架。
2. 编写 `plugin.json`，声明 packageId、nodeType、版本、Pin 和参数。
3. 编写 `build.json`，声明语言、构建系统、入口和目标。
4. 使用对应语言 SDK 创建 Worker 入口。
5. 注册节点业务对象。
6. 实现 Configure、Init、Run、Stop、Shutdown。
7. 实现输入 Sample 处理。
8. 实现输出 Sample 发送。
9. 实现广播订阅和发布。
10. 实现结构化日志、错误上报和心跳。
11. 使用 FakeCore 测试 hello、register、ready 和生命周期。
12. 测试 Pin 类型、null、队列满、超时和 Stop/Run 两次。
13. 使用构建脚本生成 Debug 产物并安装到 `Plugins/External/<RID>`。
14. 在 GUI 中加载工作流，验证参数、Pin、日志、广播和状态。
15. 使用 CLI 验证多工作流并发和 Worker 进程隔离。
16. 使用 Release 构建，在新系统上验证无 Python、无 CMake、无编译器也能运行。
17. 生成构建报告、哈希和版本兼容信息。
18. 更新 README、许可证、NOTICE 和部署说明。

每个 Package 交付前必须确认：

- `plugin.json` 通过 schema 校验。
- `Worker/` 自包含。
- Worker 启动后立即连接 Named Pipe/UDS。
- 不依赖匿名管道或开发机绝对路径。
- `worker.ready` 之前不处理业务命令。
- Stop 后可再次 Run。
- Shutdown 幂等并正常退出。
- 失败通过结构化协议上报。
- Release 产物在干净系统验证通过。


## 外部 Worker 队列边界

Core 侧的 Worker 事件通道使用有界通道（当前容量 256），避免 Worker 异常刷消息时无限占用内存。ExternalNodeProxy 对每个输入 Pin 的待发送缓存设置上限（当前 1024 个 Sample）；超过上限时记录 Error、将该外部节点标记为 Failed，并拒绝新输入，而不是静默丢弃。后续 SDK 的 Worker 内部业务队列仍应使用各自语言的有界队列，并在队列满时上报结构化错误。
