// -----------------------------------------------------------------------
// <copyright company="Your Company" file="ProcessRunnerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.ProcessRunner;

/// <summary>
/// Runs a Windows command through <c>cmd.exe</c>. AgentFlow must already run with an elevated
/// administrator token; child processes inherit that token and no UAC prompt is raised. The
/// exit code is always published on the INT64 output pin, and captured stdout/stderr is
/// published on the STRING output pin when available.
/// </summary>
public sealed class ProcessRunnerNode : BaseNode
{
    private const long LaunchFailureExitCode = -1;
    private const long StartTimeoutExitCode = -2;
    private const long ExecutionTimeoutExitCode = -3;
    private const int DefaultStartTimeoutMilliseconds = 20000;
    private const int DefaultExecutionTimeoutMilliseconds = 0;
    private const string StartTimeoutParameterName = "StartTimeoutMilliseconds";
    private const string ExecutionTimeoutParameterName = "ExecutionTimeoutMilliseconds";

    private readonly BasePin _commandPin =
        new("input_string_cmd", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _stringResultPin =
        new("output_string_ret", DataType.STRING, PinDirection.Output);

    private readonly BasePin _exitCodePin =
        new("output_int64_ret", DataType.INT64, PinDirection.Output);

    private readonly SemaphoreSlim _processGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Process? _activeProcess;
    private ILogger? _logger;
    private int _startTimeoutMilliseconds = DefaultStartTimeoutMilliseconds;
    private int _executionTimeoutMilliseconds = DefaultExecutionTimeoutMilliseconds;

    public ProcessRunnerNode()
    {
        Uuid = "185BDBA6-8EF9-4D14-95B3-22EBFFDCF7CD";
        AddParam();
        AddPins();
    }

    public override string Name => "Process Runner";
    public override string DisplayName { get; set; } = "Process Runner";
    public override string Category => "C";

    public override void AddPins()
    {
        AddInputPin(_commandPin);
        AddOutputPin(_stringResultPin);
        AddOutputPin(_exitCodePin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            StartTimeoutParameterName,
            typeof(int),
            DefaultStartTimeoutMilliseconds,
            isEditable: true,
            group: "Runtime",
            zone: "ProcessRunnerZone"));
        AddParameter(new NodeParameter(
            ExecutionTimeoutParameterName,
            typeof(int),
            DefaultExecutionTimeoutMilliseconds,
            isEditable: true,
            group: "Runtime",
            zone: "ProcessRunnerZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        var startTimeout = parameters.TryGetValue(StartTimeoutParameterName, out var startValue)
            ? ParameterValues.GetInt(startValue, DefaultStartTimeoutMilliseconds)
            : _startTimeoutMilliseconds;
        var executionTimeout = parameters.TryGetValue(ExecutionTimeoutParameterName, out var executionValue)
            ? ParameterValues.GetInt(executionValue, DefaultExecutionTimeoutMilliseconds)
            : _executionTimeoutMilliseconds;

        _startTimeoutMilliseconds = Math.Max(0, startTimeout);
        _executionTimeoutMilliseconds = Math.Max(0, executionTimeout);
        Param(StartTimeoutParameterName).Value = _startTimeoutMilliseconds;
        Param(ExecutionTimeoutParameterName).Value = _executionTimeoutMilliseconds;
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = true;
        _logger.LogTrace("ProcessRunner is ready for a Windows command.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _runCancellation?.Cancel();

        var activeProcess = Interlocked.Exchange(ref _activeProcess, null);
        if (activeProcess is { HasExited: false })
        {
            try
            {
                activeProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "ProcessRunner could not terminate the command process.");
            }
        }

        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _commandPin))
        {
            if (value is not StringSample input ||
                input.StringValue is not { } command ||
                string.IsNullOrWhiteSpace(command))
            {
                _logger?.LogError(
                    "ProcessRunner ignored invalid command on {Pin}: expected a non-empty StringSample, got {Type}.",
                    pin.Name,
                    value?.Type.ToString() ?? "null");
                return;
            }

            var cancellationToken = _runCancellation?.Token ?? CancellationToken.None;
            _ = ExecuteCommandAsync(command, cancellationToken);
        }
    }

    private async Task ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        string? tempDirectory = null;
        Process? process = null;
        CancellationTokenSource? executionCancellation = null;
        var gateEntered = false;

        try
        {
            await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;

            if (!OperatingSystem.IsWindows())
            {
                _logger?.LogError("ProcessRunner only supports Windows elevated process execution.");
                PublishResult(LaunchFailureExitCode, null);
                return;
            }

            if (!IsProcessElevated())
            {
                _logger?.LogError(
                    "ProcessRunner requires AgentFlow to be launched with administrator privileges; the command was not started.");
                PublishResult(LaunchFailureExitCode, null);
                return;
            }

            tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "AgentFlow",
                "ProcessRunner",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            var commandFile = Path.Combine(tempDirectory, "command.cmd");
            await File.WriteAllTextAsync(
                commandFile,
                command,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c \"\"{commandFile}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = await StartProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (process is null)
                throw new InvalidOperationException("Windows did not return a process handle for the command.");

            Interlocked.Exchange(ref _activeProcess, process);
            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_executionTimeoutMilliseconds > 0)
                executionCancellation.CancelAfter(_executionTimeoutMilliseconds);

            var stdoutTask = ReadProcessStreamAsync(
                process.StandardOutput,
                "STDOUT",
                CancellationToken.None);
            var stderrTask = ReadProcessStreamAsync(
                process.StandardError,
                "STDERR",
                CancellationToken.None);
            await process.WaitForExitAsync(executionCancellation.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = process.ExitCode;
            var output = CombineOutput(stdout, stderr);

            _logger?.LogTrace(
                "ProcessRunner command exited with code {ExitCode}; captured output length {OutputLength}.",
                exitCode,
                output.Length);

            PublishResult(exitCode, output);
        }
        catch (TimeoutException)
        {
            _logger?.LogError(
                "ProcessRunner failed to start the command within {TimeoutMilliseconds} milliseconds.",
                _startTimeoutMilliseconds);
            PublishResult(StartTimeoutExitCode, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process, "cancellation");
            _logger?.LogTrace("ProcessRunner command was cancelled.");
        }
        catch (OperationCanceledException) when (executionCancellation?.IsCancellationRequested == true)
        {
            KillProcessTree(process, "execution timeout");
            _logger?.LogError(
                "ProcessRunner terminated the command after execution timeout of {TimeoutMilliseconds} milliseconds.",
                _executionTimeoutMilliseconds);
            PublishResult(ExecutionTimeoutExitCode, null);
        }
        catch (Exception ex)
        {
            KillProcessTree(process, "failure");
            _logger?.LogError(ex, "ProcessRunner failed to start or monitor the elevated command.");
            PublishResult(LaunchFailureExitCode, null);
        }
        finally
        {
            Interlocked.Exchange(ref _activeProcess, null);
            executionCancellation?.Dispose();
            process?.Dispose();

            if (tempDirectory is not null)
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "ProcessRunner could not remove temporary files at {Path}.", tempDirectory);
                }
            }

            if (gateEntered)
                _processGate.Release();
        }
    }

    private async Task<Process?> StartProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var startTask = Task.Run(() => Process.Start(startInfo), CancellationToken.None);
        try
        {
            if (_startTimeoutMilliseconds == 0)
                return await startTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            return await startTask
                .WaitAsync(TimeSpan.FromMilliseconds(_startTimeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ScheduleLateStartCleanup(startTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            ScheduleLateStartCleanup(startTask);
            throw;
        }
    }

    private void ScheduleLateStartCleanup(Task<Process?> startTask)
    {
        _ = startTask.ContinueWith(
            completedTask =>
            {
                if (completedTask.Status == TaskStatus.RanToCompletion &&
                    completedTask.Result is { } lateProcess)
                {
                    try
                    {
                        if (!lateProcess.HasExited)
                            lateProcess.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(
                            ex,
                            "ProcessRunner could not terminate process pid={Pid} started after the timeout.",
                            lateProcess.Id);
                    }
                    finally
                    {
                        lateProcess.Dispose();
                    }

                    return;
                }

                if (completedTask.IsFaulted && completedTask.Exception is { } exception)
                {
                    _logger?.LogError(
                        exception,
                        "ProcessRunner observed a late process-start failure after the timeout.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task DrainProcessReadersAsync(
        Task<string>? stdoutTask,
        Task<string>? stderrTask)
    {
        var readers = new[] { stdoutTask, stderrTask }
            .Where(static task => task is not null)
            .Cast<Task>();

        try
        {
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch
        {
            // Reader failures are best-effort after the process tree has already been terminated.
        }
    }
    private void KillProcessTree(Process? process, string reason)
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
            _logger?.LogWarning(
                "ProcessRunner terminated active process pid={Pid} after {Reason}.",
                process.Id,
                reason);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "ProcessRunner could not terminate active process pid={Pid} after {Reason}.",
                process.Id,
                reason);
        }
    }

    private static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task<string> ReadProcessStreamAsync(
        StreamReader reader,
        string streamName,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            output.AppendLine(line);

            if (!string.IsNullOrWhiteSpace(line))
            {
                _logger?.LogInformation(
                    "ProcessRunner [{Stream}] {Line}",
                    streamName,
                    line);
            }
        }

        return output.ToString();
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;

        if (string.IsNullOrWhiteSpace(stdout))
            return stderr;

        return stdout.TrimEnd() + Environment.NewLine + stderr;
    }

    private void PublishResult(long exitCode, string? output)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            var stringSample = StringSample.CreateSample();
            stringSample.StringValue = output;
            _stringResultPin.SendSampleWithDispatchLog(stringSample, "captured command output", LogLevel.Information);
        }

        var exitCodeSample = Int64Sample.CreateSample();
        exitCodeSample.Int64Value = exitCode;
        _exitCodePin.SendSampleWithDispatchLog(exitCodeSample, $"exit code {exitCode}", LogLevel.Information);
    }

    private NodeParameter Param(string name) => NodeParameters.First(parameter => parameter.Name == name);

    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}