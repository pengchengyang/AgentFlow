// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Core;
using AgentFlow.Core.ExternalNodes;
using Hosting.Logging;
using Microsoft.Extensions.Logging;

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

if (!TryParseArguments(args, out var workflowPath, out var pluginDirectory, out var error))
{
    Console.Error.WriteLine(error);
    PrintUsage();
    return 2;
}

if (!File.Exists(workflowPath))
{
    Console.Error.WriteLine($"Workflow file was not found: {workflowPath}");
    return 2;
}

if (!Directory.Exists(pluginDirectory))
{
    Console.Error.WriteLine($"Plugin directory was not found: {pluginDirectory}");
    return 2;
}

AppLog.ConfigureHost("DemoCli");
using var loggerFactory = AppLog.CreateLoggerFactory();
var logger = loggerFactory.CreateLogger("Demo.Cli");
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    logger.LogInformation("Demo.Cli loading workflow {WorkflowPath} with plugins from {PluginDirectory}.",
        workflowPath, pluginDirectory);

    var registry = new NodeRegistry();
    using var pluginLoader = new PluginLoader(loggerFactory.CreateLogger(nameof(PluginLoader)));
    pluginLoader.LoadFromDirectory(pluginDirectory, registry);
    if (registry.Nodes.Count == 0)
        throw new InvalidOperationException($"No node types were loaded from '{pluginDirectory}'.");

    await using var scope = registry.CreateWorkflowScope(
        loggerFactory.CreateLogger(nameof(ExternalNodeRuntime)));
    var graph = EditorJsonLoader.Load(workflowPath, scope.Registry);
    var engine = new WorkflowEngine(scope.Registry, loggerFactory);

    await engine.RunOnceAsync(graph, cancellation.Token);
    logger.LogInformation("Demo.Cli workflow completed successfully.");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    logger.LogInformation("Demo.Cli workflow was cancelled.");
    return 130;
}
catch (Exception exception)
{
    logger.LogError(exception, "Demo.Cli failed: {Message}", exception.Message);
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally
{
    AppLog.Close();
}

static bool TryParseArguments(
    string[] args,
    out string workflowPath,
    out string pluginDirectory,
    out string? error)
{
    workflowPath = string.Empty;
    pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
    error = null;

    for (var index = 0; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "--workflow":
            case "-w":
                if (++index >= args.Length)
                {
                    error = $"Missing workflow path after {args[index - 1]}.";
                    return false;
                }
                workflowPath = Path.GetFullPath(args[index]);
                break;

            case "--plugin-dir":
            case "-p":
                if (++index >= args.Length)
                {
                    error = $"Missing plugin directory after {args[index - 1]}.";
                    return false;
                }
                pluginDirectory = Path.GetFullPath(args[index]);
                break;

            default:
                if (workflowPath.Length == 0)
                    workflowPath = Path.GetFullPath(args[index]);
                else
                {
                    error = $"Unexpected argument: {args[index]}";
                    return false;
                }
                break;
        }
    }

    if (workflowPath.Length == 0)
    {
        error = "A workflow path is required.";
        return false;
    }

    return true;
}

static void PrintUsage()
{
    Console.WriteLine("Demo.Cli - minimal AgentFlow workflow runner");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  Demo.Cli <workflow.rw> [--plugin-dir <dir>]");
    Console.WriteLine("  Demo.Cli --workflow <workflow.rw> --plugin-dir <dir>");
    Console.WriteLine();
    Console.WriteLine("Defaults:");
    Console.WriteLine("  plugin directory: <Demo.Cli output>/Plugins");
    Console.WriteLine("  exit code 0: success, 1: failure, 2: invalid arguments, 130: cancelled");
}
