# AgentFlow 最小运行示例

`Demo.Cli` 和 `Demo.Desktop` 只演示如何加载工作流、加载插件以及执行
`Initialize`、`Run`、`Stop`、`Shutdown`，不包含节点编辑器、画布或属性面板。

## 在仓库内构建

在仓库根目录执行：

```powershell
dotnet build .\Samples\Demo.Cli\Demo.Cli.csproj -c Debug -p:Platform=x64
dotnet build .\Samples\Demo.Desktop\Demo.Desktop.csproj -c Debug -p:Platform=x64
```

仓库内构建会引用 `AgentFlow.Core` 等公共项目，并自动编译和复制 `Plugins`。

## 在二进制 SDK 内构建

SDK 中的目录结构为：

```text
AgentFlow.Sdk.Binaries/
├─ SharedDlls/
├─ Demo.Cli/
└─ Demo.Desktop/
```

进入对应 Demo 目录后执行：

```powershell
dotnet build .\Demo.Cli.csproj -c Debug -p:Platform=x64
dotnet build .\Demo.Desktop.csproj -c Debug -p:Platform=x64
```

SDK 内的 Demo 通过 `..\SharedDlls\x64\<Configuration>` 引用二进制程序集，并复制
该配置下的 `Plugins`。因此不要把 Debug、Release 的 Demo 与另一配置的 DLL 混用。

## 使用命令行示例

```powershell
.\Demo.Cli.exe .\example.workflow.json
.\Demo.Cli.exe --workflow .\example.workflow.json --plugin-dir .\Plugins
```

默认插件目录是 `Demo.Cli.exe` 输出目录下的 `Plugins`。退出码约定：

- `0`：运行成功
- `1`：运行失败
- `2`：参数、工作流或插件目录错误
- `130`：用户取消

`Demo.Desktop` 选择工作流后点击 `Run`，退出程序时会依次执行 `Stop` 和 `Shutdown`。
