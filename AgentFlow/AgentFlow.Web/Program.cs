// -----------------------------------------------------------------------
// WebAssembly (browser) entry point for the Avalonia UI.
//
// This reuses the shared AgentFlow UI library unchanged: the whole Avalonia
// UI is compiled to WASM (Route 1). The workflow graph is persisted through
// the HTTP backend (HttpWorkflowStore) instead of the local filesystem.
// -----------------------------------------------------------------------

using System.Runtime.Versioning;
using System.Threading.Tasks;
using AgentFlow;
using AgentFlow.ViewModels;
using AgentFlow.Services;
using Avalonia;
using Avalonia.Browser;

[assembly: SupportedOSPlatform("browser")]

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        // Web 端工作流图通过后端 API 存取（后端托管同一套 Core 引擎）。
        MainViewModel.DefaultWorkflowStore = new HttpWorkflowStore(new HttpClient(), "/api/workflow");

        return BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}


