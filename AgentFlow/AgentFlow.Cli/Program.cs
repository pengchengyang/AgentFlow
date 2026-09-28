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
var graph = WorkflowGraph.Load(workflowPath);

var guiBridge = new InProcessGuiBridge();
// CLI demo: print messages that nodes publish to the external GUI.
guiBridge.Subscribe("result", msg =>
    Console.WriteLine($">>> [GuiBridge] topic={msg.Topic}, payload={msg.Payload}"));

var engine = new WorkflowEngine(registry, loggerFactory, guiBridge);

// Ctrl+C / SIGINT lets the user stop a running workflow. The cancellation token flows into
// NodeInstanceManager.RunWorkflowAsync, which always runs the stop phase before exiting.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    logger.LogWarning("Stop requested (Ctrl+C). Stopping workflow...");
    e.Cancel = true; // let the app finish cleanup instead of being killed immediately
    cts.Cancel();
};

try
{
    await engine.RunWorkflowAsync(graph, cts.Token);
}
catch (OperationCanceledException)
{
    logger.LogInformation("Workflow stopped by user.");
}
