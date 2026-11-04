// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Core;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using Hosting.Logging;

// AgentFlow.Cli - headless runner entry point.
// Usage: AgentFlow.Cli <workflow.json> [pluginDir]
// The workflow JSON is always parsed the same way as the GUI (Core's EditorJsonLoader):
// node type/params come from each node's `logic` blob and pin-id connections are resolved
// to logical (node, pin) connections, so the headless CLI runs the exact same logic.

var workflowPath = args.Length > 0 ? args[0] : "sample.workflow.json";
var pluginDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "plugins");

// One log file for the whole CLI process (Core + all plugins): ./Logs/AgentFlow-Cli-.log
AppLog.ConfigureHost("AgentFlow");

using var loggerFactory = AppLog.CreateLoggerFactory();
var logger = loggerFactory.CreateLogger("AgentFlow.Cli");

var exitCode = 0;
WorkflowGraph? graph = null;
WorkflowEngine? engine = null;

using var cts = new CancellationTokenSource();
var stopRequests = 0;
Console.CancelKeyPress += (_, e) =>
{
    if (Interlocked.Exchange(ref stopRequests, 1) == 1)
    {
        // Second Ctrl+C: force immediate termination if a plugin ignores Stop.
        e.Cancel = false;
        return;
    }

    logger.LogWarning("Stop requested (Ctrl+C). Stopping workflow...");
    e.Cancel = true; // let the app finish cleanup instead of being killed immediately
    cts.Cancel();
};

try
{
    logger.LogInformation("Loading plugins from: {Dir}", pluginDir);
    var registry = new NodeRegistry();
    new PluginLoader(loggerFactory.CreateLogger(nameof(PluginLoader))).LoadFromDirectory(pluginDir, registry);
    logger.LogInformation("Registered {Count} node types", registry.Nodes.Count);
    if (registry.Nodes.Count == 0)
        throw new InvalidOperationException($"No node types were loaded from '{pluginDir}'.");

    logger.LogInformation("Loading workflow: {Path}", workflowPath);
    var loadedGraph = EditorJsonLoader.Load(workflowPath, registry);
    graph = loadedGraph;

    engine = new WorkflowEngine(registry, loggerFactory);

    // Unified persistent model: start = initialize + run (no auto-stop). The workflow keeps
    // running until Ctrl+C, but a node failure (FailFast / exhausted Retry / Isolate) must not
    // leave the process waiting forever: the loop below detects the failed state and exits 1.
    await engine.StartAsync(loadedGraph, cts.Token);
    logger.LogInformation("Workflow running. Press Ctrl+C to stop.");

    while (!cts.IsCancellationRequested)
    {
        if (engine.HasFailedNodes)
        {
            logger.LogError("Workflow stopped because at least one node failed.");
            exitCode = 1;
            break;
        }

        if (!engine.IsActive)
        {
            // No nodes at all (or every node already reset): nothing left to keep the process alive.
            logger.LogInformation("Workflow has no active nodes; exiting.");
            break;
        }

        try
        {
            await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            exitCode = 130; // conventional SIGINT exit code
            break;
        }
    }

    if (stopRequests > 0 && exitCode == 0)
        exitCode = 130;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // Ctrl+C during startup: treat it as an intentional stop, not as an initialization failure.
    logger.LogInformation("Workflow start cancelled by Ctrl+C.");
    exitCode = 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "AgentFlow.Cli failed: {Message}", ex.Message);
    if (exitCode == 0) exitCode = 1;
}
finally
{
    if (engine is not null && graph is not null)
    {
        try
        {
            await engine.StopAsync(graph);
            logger.LogInformation("Workflow stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stopping the workflow failed: {Message}", ex.Message);
            if (exitCode == 0) exitCode = 1;
        }
    }

    AppLog.Close();
    Environment.ExitCode = exitCode;
}
