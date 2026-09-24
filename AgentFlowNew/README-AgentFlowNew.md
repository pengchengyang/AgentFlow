# AgentFlowNew — VS Code + .NET 10 开发副本

这是从原 VS2022(net8) 工程拷贝出来的独立副本，**全部工程已切换到 net10.0**，原工程不受影响。

## 环境要求
- **.NET 10 SDK**：本机装在 `C:\Users\admin\.dotnet`（10.0.401）。
- 本目录有 `global.json` 固定 SDK=10.0.401。
- **注意**：系统 `dotnet`（Program Files 的 .NET 9）找不到用户级 SDK，所以下面所有命令都**直接用 .NET 10 的 dotnet**：
  ```powershell
  C:\Users\admin\.dotnet\dotnet.exe  <命令>
  ```
  VS Code 的 `.vscode/tasks.json` 已经用 `${userHome}\.dotnet\dotnet.exe` 指好了，终端里也可先执行：
  ```powershell
  $env:PATH = "$env:USERPROFILE\.dotnet;" + $env:PATH
  ```

## 常用命令（在 VS Code 终端里）

```powershell
# 编译整个解决方案
C:\Users\admin\.dotnet\dotnet.exe build AgentFlow.sln

# 运行桌面端（方式 B：改 .axaml 存盘 → 应用实时刷新 = 热重载）
C:\Users\admin\.dotnet\dotnet.exe watch run --project AgentFlow.Desktop\AgentFlow.Desktop.csproj

# 或直接运行（无热重载）
C:\Users\admin\.dotnet\dotnet.exe run --project AgentFlow.Desktop\AgentFlow.Desktop.csproj

# 运行 Web (WASM)
C:\Users\admin\.dotnet\dotnet.exe run --project AgentFlow.Web\AgentFlow.Web.csproj
```

## VS Code 操作（推荐）
1. 用 VS Code 打开本文件夹（`AgentFlowNew`）。
2. 安装扩展 **Avalonia for VS Code**（.axaml 高亮 + 预览）。
3. 菜单 **Terminal → Run Task** 选择：
   - `Build AgentFlow (net10)`
   - `Run AgentFlow Desktop (Hot Reload)` ← 最常用
4. 调试：装 **C# Dev Kit** 后按 **F5**（用 `.vscode/launch.json`）。

## 预览 .axaml 的两种方式
- **方式 A（单个 View 预览）**：装官方 Avalonia 扩展后，打开 `.axaml` → 命令面板（Ctrl+Shift+P）→ `Avalonia: Preview`。
- **方式 B（看真实应用 + 热重载）**：`dotnet watch run` 跑桌面端，改颜色/样式后**保存**，应用实时刷新。

> 不需要 Visual Studio，不需要拖拽设计器，也不需要 VS 2026。

## 说明
- Web 项目是 `net10.0-browser`（Avalonia 12 的浏览器运行时只支持 net10），照常编译。
- 桌面 + 插件已全部 `net10.0`。

## 方式 C：用 PreviewHost 单独预览 .axaml（最省心，推荐先试这个）
`PreviewHost` 是一个**独立的极简 Avalonia 窗口**，专用来练“改 .axaml → 保存 → 实时刷新”，不碰 AgentFlow 主程序。
1. 在 VS Code 打开本文件夹，**重载窗口**：`Ctrl+Shift+P` → 输入 `Developer: Reload Window` → 回车。
2. `Ctrl+Shift+P` → 输入 `Tasks: Run Task` → 回车 → 选 **`Preview .axaml (Hot Reload)`**。
3. 会弹出一个标题为“AgentFlow 预览…”的小窗口。
4. 打开 `PreviewHost/MainWindow.axaml`，把按钮的 `Background="SteelBlue"` 改成 `Background="Red"`，按 `Ctrl+S` 保存。
5. 预览窗口会自动重启并显示成红色——这就证明你的开发链路通了。
6. 之后改任何 `.axaml` 里的颜色/样式，保存即可看到效果。

> 终端里手动启动同款：
> ```powershell
> $env:PATH = "$env:USERPROFILE\.dotnet;" + $env:PATH
> cd 'D:\Codes\AgentFlow\AgentFlow\AgentFlowNew'
> dotnet watch run --project PreviewHost\PreviewHost.csproj
> ```

## 关于 VS Code 任务的 dotnet 路径（重要）
任务统一用 `"command": "dotnet"`，靠 `.vscode/settings.json` 里的
`"terminal.integrated.env.windows.PATH"` 把 `C:\Users\admin\.dotnet` 放到最前面，
这样终端里 `dotnet` 就是 .NET 10，不会再出现 `\c:\Users\admin.dotnet\dotnet.exe` 打不开的问题。
