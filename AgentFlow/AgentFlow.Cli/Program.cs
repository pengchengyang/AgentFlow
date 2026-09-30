// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Core;
using Microsoft.Extensions.Logging;

// AgentFlow.Cli - headless runner entry point.
// Usage: AgentFlow.Cli <workflow.json> [pluginDir]
// The workflow JSON is always parsed the same way as the GUI (Core's EditorJsonLoader):
// node type/params come from each node's `logic` blob and pin-id connections are resolved
// to logical (node, pin) connections, so the headless CLI runs the exact same logic.

var workflowPath = args.Length > 0 ? args[0] : "sample.workflow.json";
var pluginDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "plugins");

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddProvider(new ConsoleLoggerProvider());
});
var logger = loggerFactory.CreateLogger("AgentFlow.Cli");

logger.LogInformation("Loading plugins from: {Dir}", pluginDir);
var registry = new NodeRegistry();
new PluginLoader(loggerFactory.CreateLogger(nameof(PluginLoader))).LoadFromDirectory(pluginDir, registry);
logger.LogInformation("Registered {Count} node types", registry.Nodes.Count);

logger.LogInformation("Loading workflow: {Path}", workflowPath);
var graph = EditorJsonLoader.Load(workflowPath, registry);

var guiBridge = new InProcessGuiBridge();
// CLI demo: print messages that nodes publish to the external GUI.
guiBridge.Subscribe("result", msg =>
    Console.WriteLine($">>> [GuiBridge] topic={msg.Topic}, payload={msg.Payload}"));

var engine = new WorkflowEngine(registry, loggerFactory, guiBridge);

// Unified persistent model: start = initialize + run (no auto-stop). The workflow keeps
// running until the user presses Ctrl+C, which triggers Stop to tear down long-running nodes.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    logger.LogWarning("Stop requested (Ctrl+C). Stopping workflow...");
    e.Cancel = true; // let the app finish cleanup instead of being killed immediately
    cts.Cancel();
};

try
{
    await engine.StartAsync(graph, cts.Token);
    logger.LogInformation("Workflow running. Press Ctrl+C to stop.");

    // Keep the process alive (letting background / long-running nodes execute) until Ctrl+C.
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C pressed - fall through to the finally block which stops the workflow.
    }
}
finally
{
    await engine.StopAsync(graph);
    logger.LogInformation("Workflow stopped.");
}