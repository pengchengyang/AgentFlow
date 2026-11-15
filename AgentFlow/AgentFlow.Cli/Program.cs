// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Core;
using Hosting.Logging;
using Microsoft.Extensions.Logging;

// AgentFlow.Cli - headless runner entry point.
// Usage:
//   AgentFlow.Cli [workflow.rw] [pluginDir]
//   AgentFlow.Cli run.rw --instances 5
//   AgentFlow.Cli --plugin-dir Plugins `
//       --workflow a.workflow.rw --instances 3 `
//       --workflow b.workflow.rw --instances 2 `
//       --max-concurrency 4 --failure-policy ContinueOthers `
//       --run-mode Auto --output-json results.json

const string DefaultWorkflowPath = "sample.workflow.rw";

if (!TryParseArguments(args, out var parsed, out var parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

if (parsed.ShowHelp)
{
    PrintUsage();
    Environment.ExitCode = 0;
    return;
}

var workflowSpecs = parsed.WorkflowSpecs;
if (workflowSpecs.Count == 0)
{
    // Keep the bundled JSON sample runnable while the repository transitions to .rw files.
    var legacyDefaultPath = Path.ChangeExtension(DefaultWorkflowPath, ".json");
    var resolvedDefaultPath = File.Exists(DefaultWorkflowPath)
        ? DefaultWorkflowPath
        : File.Exists(legacyDefaultPath) ? legacyDefaultPath : DefaultWorkflowPath;
    workflowSpecs.Add(new WorkflowSpec(resolvedDefaultPath, 1));
}

var pluginDir = parsed.PluginDir ?? Path.Combine(AppContext.BaseDirectory, "Plugins");
var runOptions = new WorkflowRunOptions(
    parsed.MaxConcurrency,
    parsed.FailurePolicy,
    parsed.RunMode);

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

IReadOnlyList<WorkflowRunResult> results = Array.Empty<WorkflowRunResult>();
var exitCode = 0;
try
{
    logger.LogInformation("Loading plugins from: {Dir}", pluginDir);
    var registry = new NodeRegistry();
    using var pluginLoader = new PluginLoader(loggerFactory.CreateLogger(nameof(PluginLoader)));
    pluginLoader.LoadFromDirectory(pluginDir, registry);
    logger.LogInformation("Registered {Count} node types", registry.Nodes.Count);
    if (registry.Nodes.Count == 0)
        throw new InvalidOperationException($"No node types were loaded from '{pluginDir}'.");

    var requests = BuildRequests(workflowSpecs);
    logger.LogInformation(
        "Starting {InstanceCount} workflow instance(s) from {WorkflowCount} workflow configuration(s). " +
        "MaxConcurrency={MaxConcurrency}, FailurePolicy={FailurePolicy}, RunMode={RunMode}.",
        requests.Count,
        workflowSpecs.Count,
        parsed.MaxConcurrency == 0 ? "unlimited" : parsed.MaxConcurrency,
        parsed.FailurePolicy,
        parsed.RunMode);

    var manager = new WorkflowRunManager(
        registry,
        loggerFactory,
        request => AppLog.CreateLoggerFactory(request.RunId),
        runOptions);

    manager.SessionChanged += snapshot =>
        logger.LogDebug(
            "Workflow instance state changed: RunId={RunId}, Workflow={Workflow}, Ordinal={Ordinal}, " +
            "State={State}, ExitCode={ExitCode}, Error={Error}, CleanupError={CleanupError}, EffectiveError={EffectiveError}",
            snapshot.RunId,
            snapshot.WorkflowSource,
            snapshot.Ordinal,
            snapshot.State,
            snapshot.ExitCode,
            snapshot.Error,
            snapshot.CleanupError,
            snapshot.EffectiveError);

    results = await manager.RunAsync(requests, runOptions, cts.Token);

    foreach (var result in results)
    {
        logger.LogInformation(
            "Workflow instance result: RunId={RunId}, Workflow={WorkflowPath}, Ordinal={Ordinal}, " +
            "State={State}, ExitCode={ExitCode}, StartedAt={StartedAt:O}, FinishedAt={FinishedAt:O}, " +
            "DurationMs={DurationMs}, Error={Error}, CleanupError={CleanupError}, EffectiveError={EffectiveError}",
            result.RunId,
            result.WorkflowSource,
            result.Ordinal,
            result.State,
            result.ExitCode,
            result.StartedAt,
            result.FinishedAt,
            result.Duration.TotalMilliseconds,
            result.Error,
            result.CleanupError,
            result.EffectiveError);
    }

    var succeeded = results.Count(r => r.State == WorkflowRunState.Succeeded);
    var failed = results.Count(r => r.State == WorkflowRunState.Failed);
    var cancelled = results.Count(r => r.State == WorkflowRunState.Cancelled);
    logger.LogInformation(
        "Workflow instance summary: Total={Total}, Succeeded={Succeeded}, Failed={Failed}, " +
        "Cancelled={Cancelled}, FailurePolicy={FailurePolicy}, MaxConcurrency={MaxConcurrency}, RunMode={RunMode}.",
        results.Count,
        succeeded,
        failed,
        cancelled,
        parsed.FailurePolicy,
        parsed.MaxConcurrency == 0 ? "unlimited" : parsed.MaxConcurrency,
        parsed.RunMode);

    exitCode = stopRequests > 0
        ? 130
        : results.All(r => r.State == WorkflowRunState.Succeeded && r.ExitCode == 0) ? 0 : 1;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    logger.LogInformation("Workflow startup was cancelled by Ctrl+C.");
    exitCode = 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "AgentFlow.Cli failed: {Message}", ex.Message);
    exitCode = 1;
}
finally
{
    if (!string.IsNullOrWhiteSpace(parsed.OutputJsonPath))
    {
        try
        {
            await WriteResultsJsonAsync(parsed.OutputJsonPath!, results, runOptions, exitCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write JSON result file '{Path}'.", parsed.OutputJsonPath);
            if (exitCode == 0)
                exitCode = 1;
        }
    }

    Environment.ExitCode = exitCode;
    AppLog.Close();
}


static IReadOnlyList<WorkflowRunRequest> BuildRequests(IReadOnlyList<WorkflowSpec> specs)
{
    var requests = new List<WorkflowRunRequest>();
    var runNumber = 0;

    foreach (var spec in specs)
    {
        var fullPath = Path.GetFullPath(spec.WorkflowPath);
        for (var ordinal = 1; ordinal <= spec.InstanceCount; ordinal++)
        {
            runNumber++;
            requests.Add(WorkflowRunRequest.FromFile(
                $"wf-{runNumber:00}",
                fullPath,
                ordinal,
                $"{Path.GetFileName(fullPath)} #{ordinal}"));
        }
    }

    return requests;
}

static bool TryParseArguments(
    string[] args,
    out CliArguments parsed,
    out string? parseError)
{
    var specs = new List<WorkflowSpec>();
    string? pluginDir = null;
    var maxConcurrency = 0;
    var failurePolicy = WorkflowRunFailurePolicy.ContinueOthers;
    var runMode = WorkflowRunMode.RunOnce;
    string? outputJsonPath = null;
    var showHelp = false;
    parseError = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "-h":
            case "--help":
                showHelp = true;
                break;

            case "--plugin-dir":
                if (!TryReadValue(args, ref i, arg, out pluginDir, out parseError))
                    { parsed = default!; return false; }
                break;

            case "-w":
            case "--workflow":
                if (!TryReadValue(args, ref i, arg, out var workflowPath, out parseError))
                    { parsed = default!; return false; }
                specs.Add(new WorkflowSpec(workflowPath!, 1));
                break;

            case "-n":
            case "--instances":
                if (!TryReadValue(args, ref i, arg, out var instanceText, out parseError))
                    { parsed = default!; return false; }
                if (specs.Count == 0)
                {
                    parseError = "--instances must follow a workflow path or --workflow.";
                    parsed = default!;
                    return false;
                }
                if (!int.TryParse(instanceText, out var instanceCount) || instanceCount <= 0)
                {
                    parseError = "--instances must be an integer greater than zero.";
                    parsed = default!;
                    return false;
                }
                specs[^1] = specs[^1] with { InstanceCount = instanceCount };
                break;

            case "-c":
            case "--max-concurrency":
                if (!TryReadValue(args, ref i, arg, out var concurrencyText, out parseError))
                    { parsed = default!; return false; }
                if (!int.TryParse(concurrencyText, out maxConcurrency) || maxConcurrency < 0)
                {
                    parseError = "--max-concurrency must be zero (unlimited) or a non-negative integer.";
                    parsed = default!;
                    return false;
                }
                break;

            case "--failure-policy":
                if (!TryReadValue(args, ref i, arg, out var policyText, out parseError))
                    { parsed = default!; return false; }
                if (!TryParseFailurePolicy(policyText, out failurePolicy))
                {
                    parseError = "--failure-policy must be ContinueOthers/continue or CancelBatch/cancel.";
                    parsed = default!;
                    return false;
                }
                break;

            case "--run-mode":
                if (!TryReadValue(args, ref i, arg, out var modeText, out parseError))
                    { parsed = default!; return false; }
                if (!TryParseRunMode(modeText, out runMode))
                {
                    parseError = "--run-mode must be RunOnce, Auto, or Persistent.";
                    parsed = default!;
                    return false;
                }
                break;

            case "--output-json":
                if (!TryReadValue(args, ref i, arg, out var jsonPath, out parseError))
                    { parsed = default!; return false; }
                outputJsonPath = jsonPath;
                break;

            default:
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    parseError = $"Unknown option: {arg}";
                    parsed = default!;
                    return false;
                }

                // Backward compatibility: AgentFlow.Cli <workflow.rw|workflow.json> [pluginDir].
                if (specs.Count == 0)
                {
                    specs.Add(new WorkflowSpec(arg, 1));
                }
                else if (pluginDir is null)
                {
                    pluginDir = arg;
                }
                else
                {
                    parseError = $"Unexpected positional argument: {arg}";
                    parsed = default!;
                    return false;
                }
                break;
        }
    }

    parsed = new CliArguments(
        specs,
        pluginDir,
        maxConcurrency,
        failurePolicy,
        runMode,
        outputJsonPath,
        showHelp);
    return true;
}

static bool TryReadValue(
    string[] args,
    ref int index,
    string option,
    out string? value,
    out string? parseError)
{
    value = null;
    parseError = null;
    if (++index < args.Length)
    {
        value = args[index];
        return true;
    }

    parseError = $"Missing value after {option}.";
    return false;
}

static bool TryParseFailurePolicy(string? value, out WorkflowRunFailurePolicy policy)
{
    policy = WorkflowRunFailurePolicy.ContinueOthers;
    if (string.IsNullOrWhiteSpace(value))
        return false;

    switch (value.Trim().ToLowerInvariant())
    {
        case "continue":
        case "continueothers":
        case "continue-others":
            policy = WorkflowRunFailurePolicy.ContinueOthers;
            return true;
        case "cancel":
        case "cancelbatch":
        case "cancel-batch":
            policy = WorkflowRunFailurePolicy.CancelBatch;
            return true;
        default:
            return false;
    }
}

static bool TryParseRunMode(string? value, out WorkflowRunMode mode)
{
    mode = WorkflowRunMode.RunOnce;
    if (string.IsNullOrWhiteSpace(value))
        return false;

    switch (value.Trim().ToLowerInvariant())
    {
        case "runonce":
        case "run-once":
        case "oneshot":
        case "one-shot":
            mode = WorkflowRunMode.RunOnce;
            return true;
        case "auto":
            mode = WorkflowRunMode.Auto;
            return true;
        case "persistent":
        case "continuous":
            mode = WorkflowRunMode.Persistent;
            return true;
        default:
            return false;
    }
}

static async Task WriteResultsJsonAsync(
    string outputPath,
    IReadOnlyList<WorkflowRunResult> results,
    WorkflowRunOptions options,
    int exitCode)
{
    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);

    var payload = new
    {
        SchemaVersion = 1,
        GeneratedAt = DateTimeOffset.UtcNow,
        ExitCode = exitCode,
        Options = new
        {
            MaxConcurrency = options.MaxConcurrency,
            FailurePolicy = options.FailurePolicy.ToString(),
            RunMode = options.RunMode.ToString()
        },
        Summary = new
        {
            Total = results.Count,
            Succeeded = results.Count(r => r.State == WorkflowRunState.Succeeded),
            Failed = results.Count(r => r.State == WorkflowRunState.Failed),
            Cancelled = results.Count(r => r.State == WorkflowRunState.Cancelled)
        },
        Results = results.Select(result => new
        {
            RunId = result.RunId,
            WorkflowSource = result.WorkflowSource,
            Ordinal = result.Ordinal,
            State = result.State.ToString(),
            ExitCode = result.ExitCode,
            StartedAt = result.StartedAt,
            FinishedAt = result.FinishedAt,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Error = result.Error,
            CleanupError = result.CleanupError,
            EffectiveError = result.EffectiveError
        }).ToArray()
    };

    await using var stream = File.Create(fullPath);
    await JsonSerializer.SerializeAsync(
        stream,
        payload,
        new JsonSerializerOptions { WriteIndented = true },
        CancellationToken.None);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  AgentFlow.Cli [workflow.rw] [pluginDir]");
    Console.WriteLine("  AgentFlow.Cli run.json --instances 5");
    Console.WriteLine("  AgentFlow.Cli --plugin-dir Plugins --workflow a.workflow.rw --instances 3 --workflow b.workflow.rw --instances 2");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -w, --workflow <path>       Add a workflow file. May be specified multiple times.");
    Console.WriteLine("  -n, --instances <N>         Run N independent instances of the most recent workflow.");
    Console.WriteLine("                              Defaults to 1; reset after each new workflow.");
    Console.WriteLine("  -c, --max-concurrency <N>   Maximum simultaneous instances; 0 = unlimited (default).");
    Console.WriteLine("      --failure-policy <P>    ContinueOthers/continue (default) or CancelBatch/cancel.");
    Console.WriteLine("      --run-mode <M>          RunOnce (default), Auto, or Persistent.");
    Console.WriteLine("      --output-json <path>    Write one stable JSON result object for this batch.");
    Console.WriteLine("      --plugin-dir <dir>      Plugin directory. Defaults to <app>/Plugins.");
    Console.WriteLine("  -h, --help                  Show this help.");
    Console.WriteLine();
    Console.WriteLine("Exit codes: 0 = all instances succeeded; 1 = at least one failed/cancelled; 2 = invalid arguments; 130 = Ctrl+C.");
}

internal sealed record WorkflowSpec(string WorkflowPath, int InstanceCount);

internal sealed record CliArguments(
    List<WorkflowSpec> WorkflowSpecs,
    string? PluginDir,
    int MaxConcurrency,
    WorkflowRunFailurePolicy FailurePolicy,
    WorkflowRunMode RunMode,
    string? OutputJsonPath,
    bool ShowHelp);
