// -----------------------------------------------------------------------
// <copyright company="Your Company" file="ProcessRunnerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.ProcessRunner;

/// <summary>
/// Runs a Windows command through an elevated helper process. The command is supplied
/// as a single command-line string. The process exit code is always published on the
/// INT64 output pin; captured stdout/stderr is published on the STRING output pin when
/// available.
/// </summary>
public sealed class ProcessRunnerNode : BaseNode
{
    private const long LaunchFailureExitCode = -1;
    private const int UserCancelledErrorCode = 1223;

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
                _logger?.LogTrace(ex, "ProcessRunner could not terminate the elevated helper process.");
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
        var gateEntered = false;

        try
        {
            await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;

            if (!OperatingSystem.IsWindows())
            {
                _logger?.LogWarning("ProcessRunner only supports elevated process execution on Windows.");
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
            var stdoutFile = Path.Combine(tempDirectory, "stdout.txt");
            var stderrFile = Path.Combine(tempDirectory, "stderr.txt");
            var exitCodeFile = Path.Combine(tempDirectory, "exit-code.txt");
            var helperFile = Path.Combine(tempDirectory, "run-elevated.ps1");

            await File.WriteAllTextAsync(
                commandFile,
                command,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                helperFile,
                BuildElevatedHelperScript(commandFile, stdoutFile, stderrFile, exitCodeFile),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{helperFile}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var helperProcess = Process.Start(startInfo);
            if (helperProcess is null)
                throw new InvalidOperationException("Windows did not return a process handle for the elevated helper.");

            Interlocked.Exchange(ref _activeProcess, helperProcess);
            await helperProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _activeProcess, null);

            var exitCode = await ReadExitCodeAsync(exitCodeFile, helperProcess)
                .ConfigureAwait(false);
            var output = await ReadCombinedOutputAsync(stdoutFile, stderrFile, cancellationToken)
                .ConfigureAwait(false);

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
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledErrorCode)
        {
            _logger?.LogWarning("ProcessRunner elevation was cancelled by the user.");
            PublishResult(LaunchFailureExitCode, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ProcessRunner failed to start or monitor the elevated command.");
            PublishResult(LaunchFailureExitCode, null);
        }
        finally
        {
            Interlocked.Exchange(ref _activeProcess, null);

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

    private void PublishResult(long exitCode, string? output)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            var stringSample = StringSample.CreateSample();
            stringSample.StringValue = output;
            _stringResultPin.SendSample(stringSample);
        }

        var exitCodeSample = Int64Sample.CreateSample();
        exitCodeSample.Int64Value = exitCode;
        _exitCodePin.SendSample(exitCodeSample);
    }

    private static async Task<long> ReadExitCodeAsync(string exitCodeFile, Process helperProcess)
    {
        if (File.Exists(exitCodeFile))
        {
            var text = await File.ReadAllTextAsync(exitCodeFile).ConfigureAwait(false);
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
                return exitCode;
        }

        return helperProcess.ExitCode;
    }

    private static async Task<string> ReadCombinedOutputAsync(
        string stdoutFile,
        string stderrFile,
        CancellationToken cancellationToken)
    {
        var stdout = File.Exists(stdoutFile)
            ? await File.ReadAllTextAsync(stdoutFile, cancellationToken).ConfigureAwait(false)
            : "";
        var stderr = File.Exists(stderrFile)
            ? await File.ReadAllTextAsync(stderrFile, cancellationToken).ConfigureAwait(false)
            : "";

        if (string.IsNullOrWhiteSpace(stderr))
            return stdout;

        if (string.IsNullOrWhiteSpace(stdout))
            return stderr;

        return stdout.TrimEnd() + Environment.NewLine + stderr;
    }

    private static string BuildElevatedHelperScript(
        string commandFile,
        string stdoutFile,
        string stderrFile,
        string exitCodeFile)
    {
        return $$"""
$ErrorActionPreference = 'Stop'
$commandFile = '{{EscapePowerShellLiteral(commandFile)}}'
$stdoutFile = '{{EscapePowerShellLiteral(stdoutFile)}}'
$stderrFile = '{{EscapePowerShellLiteral(stderrFile)}}'
$exitCodeFile = '{{EscapePowerShellLiteral(exitCodeFile)}}'
$process = $null
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $env:ComSpec
    $startInfo.Arguments = '/d /s /c ""' + $commandFile + '""'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    [IO.File]::WriteAllText($stdoutFile, $stdout, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($stderrFile, $stderr, [Text.Encoding]::UTF8)

    $exitCode = $process.ExitCode
    [IO.File]::WriteAllText($exitCodeFile, [string]$exitCode, [Text.Encoding]::UTF8)
    exit $exitCode
}
catch {
    [IO.File]::WriteAllText($stderrFile, $_.Exception.ToString(), [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($exitCodeFile, '-1', [Text.Encoding]::UTF8)
    exit -1
}
""";
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
