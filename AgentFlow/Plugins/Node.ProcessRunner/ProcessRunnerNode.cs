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

    public ProcessRunnerNode()
    {
        Uuid = "185BDBA6-8EF9-4D14-95B3-22EBFFDCF7CD";
        AddParam();
        AddPins();
    }

    public override string Name => "Process Runner";
    public override string DisplayName { get; set; } = "Process Runner";
    public override string Category => "System";

    public override void AddPins()
    {
        AddInputPin(_commandPin);
        AddOutputPin(_stringResultPin);
        AddOutputPin(_exitCodePin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
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

            process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Windows did not return a process handle for the command.");

            Interlocked.Exchange(ref _activeProcess, process);
            var stdoutTask = ReadProcessStreamAsync(
                process.StandardOutput,
                "STDOUT",
                cancellationToken);
            var stderrTask = ReadProcessStreamAsync(
                process.StandardError,
                "STDERR",
                cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogTrace("ProcessRunner command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ProcessRunner failed to start or monitor the elevated command.");
            PublishResult(LaunchFailureExitCode, null);
        }
        finally
        {
            Interlocked.Exchange(ref _activeProcess, null);
            process?.Dispose();

            if (tempDirectory is not null)
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogTrace(ex, "ProcessRunner could not remove temporary files at {Path}.", tempDirectory);
                }
            }

            if (gateEntered)
                _processGate.Release();
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

    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}


