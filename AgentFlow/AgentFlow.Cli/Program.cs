// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Diagnostics;
using AgentFlow.Core;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using Hosting.Logging;

// AgentFlow.Cli - headless runner entry point.
// Usage:
//   AgentFlow.Cli [workflow.json] [pluginDir]
//   AgentFlow.Cli run.json --instances 5
//   AgentFlow.Cli --plugin-dir plugins `
//       --workflow a.workflow.json --instances 3 `
//       --workflow b.workflow.json --instances 2
//
// --instances applies to the most recently declared workflow and defaults to 1.
// Every configured instance is run independently in the same process.

const string DefaultWorkflowPath = "sample.workflow.json";

if (!TryParseArguments(args, out var workflowSpecs, out var pluginDir, out var parseError, out var showHelp))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

if (showHelp)
{
    PrintUsage();
    Environment.ExitCode = 0;
    return;
}

if (workflowSpecs.Count == 0)
    workflowSpecs.Add(new WorkflowSpec(DefaultWorkflowPath, 1));

pluginDir ??= Path.Combine(AppContext.BaseDirectory, "plugins");

// One log file for the whole CLI process (Core + all plugins): ./Logs/AgentFlow-Cli-.log
AppLog.ConfigureHost("AgentFlow");

using var loggerFactory = AppLog.CreateLoggerFactory();
var logger = loggerFactory.CreateLogger("AgentFlow.Cli");

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

    logger.LogWarning("Stop requested (Ctrl+C). Cancelling every workflow instance...");
    e.Cancel = true; // let every instance finish cleanup instead of being killed immediately
    cts.Cancel();
};

IReadOnlyList<WorkflowInstanceResult> results = Array.Empty<WorkflowInstanceResult>();
try
{
    logger.LogInformation("Loading plugins from: {Dir}", pluginDir);
    var registry = new NodeRegistry();
    new PluginLoader(loggerFactory.CreateLogger(nameof(PluginLoader))).LoadFromDirectory(pluginDir, registry);
    logger.LogInformation("Registered {Count} node types", registry.Nodes.Count);
    if (registry.Nodes.Count == 0)
        throw new InvalidOperationException($"No node types were loaded from '{pluginDir}'.");

    var requests = BuildRequests(workflowSpecs);
    logger.LogInformation(
        "Starting {InstanceCount} workflow instance(s) from {WorkflowCount} workflow configuration(s).",
        requests.Count, workflowSpecs.Count);

    // Task.Run deliberately receives CancellationToken.None: each instance always returns a
    // result record and performs its own Stop/Shutdown cleanup. The global token is passed to
    // WorkflowEngine instead, so Ctrl+C cancels every instance without losing their records.
    var tasks = requests
        .Select(request => Task.Run(
            () => RunWorkflowInstanceAsync(request, registry, cts.Token),
            CancellationToken.None))
        .ToList();

    results = await Task.WhenAll(tasks);

    foreach (var result in results)
    {
        logger.LogInformation(
            "Workflow instance result: RunId={RunId}, Workflow={WorkflowPath}, Ordinal={Ordinal}, ExitCode={ExitCode}, StartedAt={StartedAt:O}, FinishedAt={FinishedAt:O}, Exception={Exception}",
            result.RunId,
            result.WorkflowPath,
            result.Ordinal,
            result.ExitCode,
            result.StartedAt,
            result.FinishedAt,
            result.Error);
    }

    var succeeded = results.Count(r => r.ExitCode == 0);
    var failed = results.Count(r => r.ExitCode != 0 && r.ExitCode != 130);
    var cancelled = results.Count(r => r.ExitCode == 130);
    logger.LogInformation(
        "Workflow instance summary: Total={Total}, Succeeded={Succeeded}, Failed={Failed}, Cancelled={Cancelled}, CtrlCRequested={CtrlCRequested}",
        results.Count, succeeded, failed, cancelled, stopRequests > 0);

    Environment.ExitCode = stopRequests > 0
        ? 130
        : results.Any(r => r.ExitCode != 0) ? 1 : 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    logger.LogInformation("Workflow startup was cancelled by Ctrl+C.");
    Environment.ExitCode = 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "AgentFlow.Cli failed: {Message}", ex.Message);
    Environment.ExitCode = 1;
}
finally
{
    AppLog.Close();
}


static async Task<WorkflowInstanceResult> RunWorkflowInstanceAsync(
    WorkflowInstanceRequest request,
    NodeRegistry registry,
    CancellationToken cancellationToken)
{
    var startedAt = DateTimeOffset.Now;
    var stopwatch = Stopwatch.StartNew();
    using var instanceLoggerFactory = AppLog.CreateLoggerFactory(request.RunId);
    var logger = instanceLoggerFactory.CreateLogger("AgentFlow.Cli.Instance");
    var exitCode = 0;
    string? error = null;

    try
    {
        logger.LogInformation(
            "Loading workflow for instance {RunId}: {WorkflowPath} (ordinal {Ordinal})",
            request.RunId, request.WorkflowPath, request.Ordinal);

        // A fresh graph is required per instance: WorkflowEngine gives each node instance its own
        // runtime pins and NodeInstanceManager. Never share a loaded graph across instances.
        var graph = EditorJsonLoader.Load(request.WorkflowPath, registry);
        var engine = new WorkflowEngine(registry, instanceLoggerFactory);

        logger.LogInformation(
            "Executing workflow instance {RunId}: initialize -> run -> stop -> shutdown.",
            request.RunId);
        await engine.RunOnceAsync(graph, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Workflow instance {RunId} completed successfully.", request.RunId);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        exitCode = 130;
        error = "Cancelled by Ctrl+C.";
        logger.LogInformation("Workflow instance {RunId} cancelled by Ctrl+C.", request.RunId);
    }
    catch (Exception ex)
    {
        exitCode = 1;
        error = ex.GetType().Name + ": " + ex.Message;
        logger.LogError(ex, "Workflow instance {RunId} failed: {Message}", request.RunId, ex.Message);
    }
    finally
    {
        stopwatch.Stop();
    }

    var finishedAt = DateTimeOffset.Now;
    logger.LogInformation(
        "Workflow instance finished: RunId={RunId}, ExitCode={ExitCode}, DurationMs={DurationMs}",
        request.RunId, exitCode, stopwatch.ElapsedMilliseconds);

    return new WorkflowInstanceResult(
        request.RunId,
        request.WorkflowPath,
        request.Ordinal,
        exitCode,
        startedAt,
        finishedAt,
        error);
}

static IReadOnlyList<WorkflowInstanceRequest> BuildRequests(IReadOnlyList<WorkflowSpec> specs)
{
    var requests = new List<WorkflowInstanceRequest>();
    var runNumber = 0;

    foreach (var spec in specs)
    {
        for (var ordinal = 1; ordinal <= spec.InstanceCount; ordinal++)
        {
            runNumber++;
            requests.Add(new WorkflowInstanceRequest(
                $"wf-{runNumber:00}",
                Path.GetFullPath(spec.WorkflowPath),
                ordinal));
        }
    }

    return requests;
}

static bool TryParseArguments(
    string[] args,
    out List<WorkflowSpec> workflowSpecs,
    out string? pluginDir,
    out string? parseError,
    out bool showHelp)
{
    workflowSpecs = new List<WorkflowSpec>();
    pluginDir = null;
    parseError = null;
    showHelp = false;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "-h":
            case "--help":
                showHelp = true;
                return true;

            case "--plugin-dir":
                if (++i >= args.Length)
                {
                    parseError = "Missing value after --plugin-dir.";
                    return false;
                }
                pluginDir = args[i];
                break;

            case "-w":
            case "--workflow":
                if (++i >= args.Length)
                {
                    parseError = $"Missing value after {arg}.";
                    return false;
                }
                workflowSpecs.Add(new WorkflowSpec(args[i], 1));
                break;

            case "-n":
            case "--instances":
                if (++i >= args.Length)
                {
                    parseError = $"Missing value after {arg}.";
                    return false;
                }
                if (workflowSpecs.Count == 0)
                {
                    parseError = "--instances must follow a workflow path or --workflow.";
                    return false;
                }
                if (!int.TryParse(args[i], out var instanceCount) || instanceCount <= 0)
                {
                    parseError = "--instances must be an integer greater than zero.";
                    return false;
                }
                workflowSpecs[^1] = workflowSpecs[^1] with { InstanceCount = instanceCount };
                break;

            default:
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    parseError = $"Unknown option: {arg}";
                    return false;
                }

                // Backward compatibility: AgentFlow.Cli <workflow.json> [pluginDir].
                if (workflowSpecs.Count == 0)
                {
                    workflowSpecs.Add(new WorkflowSpec(arg, 1));
                }
                else if (pluginDir is null)
                {
                    pluginDir = arg;
                }
                else
                {
                    parseError = $"Unexpected positional argument: {arg}";
                    return false;
                }
                break;
        }
    }

    return true;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  AgentFlow.Cli [workflow.json] [pluginDir]");
    Console.WriteLine("  AgentFlow.Cli run.json --instances 5");
    Console.WriteLine("  AgentFlow.Cli --plugin-dir plugins --workflow a.workflow.json --instances 3 --workflow b.workflow.json --instances 2");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -w, --workflow <path>    Add a workflow file. May be specified multiple times.");
    Console.WriteLine("  -n, --instances <N>      Run N independent instances of the most recently added workflow.");
    Console.WriteLine("                           Defaults to 1; reset after each new workflow.");
    Console.WriteLine("      --plugin-dir <dir>   Plugin directory. Defaults to <app>/plugins.");
    Console.WriteLine("  -h, --help               Show this help.");
    Console.WriteLine();
    Console.WriteLine("Exit codes: 0 = all instances succeeded; 1 = at least one failed; 130 = Ctrl+C.");
}

internal sealed record WorkflowSpec(string WorkflowPath, int InstanceCount);

internal sealed record WorkflowInstanceRequest(string RunId, string WorkflowPath, int Ordinal);

internal sealed record WorkflowInstanceResult(
    string RunId,
    string WorkflowPath,
    int Ordinal,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? Error);
