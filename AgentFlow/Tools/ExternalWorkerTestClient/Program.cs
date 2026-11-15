// -----------------------------------------------------------------------
// Copyright (c) 2026 Damon Yang. All rights reserved.
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using AgentFlow.Core.ExternalNodes;
using BroadcastKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ExternalWorkerTestClient;

/// <summary>Minimal FakeCore harness for exercising one external Worker Package.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var packagePath = GetOption(args, "--package") ?? Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Plugins", "External", "Python", "Node.PythonUpper"));
        var inputText = GetOption(args, "--input") ?? "hello";
        var lowerInputText = GetOption(args, "--lower-input") ?? inputText.ToUpperInvariant();
        var prepareOnly = args.Any(argument => string.Equals(argument, "--prepare-only", StringComparison.Ordinal));
        var timeout = TimeSpan.FromSeconds(30);

        if (!Directory.Exists(packagePath))
        {
            Console.Error.WriteLine($"Package directory not found: {packagePath}");
            return 2;
        }

        ExternalNodeRuntime? runtime = null;
        ExternalNodeProxy? proxy = null;
        try
        {
            var definition = ExternalManifestLoader.LoadPackage(packagePath).Single();
            runtime = new ExternalNodeRuntime(new ConsoleLogger());
            var context = new TestNodeContext();
            proxy = new ExternalNodeProxy(definition, runtime)
            {
                InstanceId = 1001,
                DisplayName = definition.NodeType.DisplayName
            };

            proxy.Configure(new Dictionary<string, object?>
            {
                ["Suffix"] = ":fakecore"
            });

            Console.WriteLine($"package={definition.PackageId} node={definition.NodeType.NodeType}");
            await proxy.PrepareAsync(CancellationToken.None);
            Console.WriteLine("prepared");
            if (prepareOnly)
            {
                await proxy.ReleaseAsync(CancellationToken.None);
                Console.WriteLine("released");
                return 0;
            }
            await proxy.Initialize(context, CancellationToken.None);
            Console.WriteLine("initialized");

            var workerBroadcastReceived = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var workerBroadcastSubscription = TopicBus.Instance.Subscribe(
                "Demo.External.Stats",
                message =>
                {
                    workerBroadcastReceived.TrySetResult(message.Payload);
                    return Task.CompletedTask;
                });

            var pingAckReceived = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var pingAckSubscription = TopicBus.Instance.Subscribe(
                "Demo.External.PingAck",
                message =>
                {
                    pingAckReceived.TrySetResult(message.Payload);
                    return Task.CompletedTask;
                });

            await proxy.Run(context, CancellationToken.None);
            Console.WriteLine("running");

            await workerBroadcastReceived.Task.WaitAsync(timeout);
            Console.WriteLine("worker-broadcast-bridged");

            await TopicBus.Instance.PublishAsync("Demo.External.Ping", "core-to-worker");
            await pingAckReceived.Task.WaitAsync(timeout);
            Console.WriteLine("core-broadcast-bridged");

            var upperInputPin = proxy.InputPins.Single(pin => pin.Name == "input_string_toupper");
            var lowerInputPin = proxy.InputPins.Single(pin => pin.Name == "input_string_tolower");
            var upperSample = StringSample.CreateSample();
            upperSample.SetValue(inputText, "input_string_toupper");
            proxy.ReceiveSample(upperInputPin, upperSample);
            var lowerSample = StringSample.CreateSample();
            lowerSample.SetValue(lowerInputText, "input_string_tolower");
            proxy.ReceiveSample(lowerInputPin, lowerSample);

            var upperOutputTask = context.GetOutput("output_string_toupper").Task.WaitAsync(timeout);
            var lowerOutputTask = context.GetOutput("output_string_tolower").Task.WaitAsync(timeout);
            var upperOutput = await upperOutputTask;
            var lowerOutput = await lowerOutputTask;
            Console.WriteLine($"output_string_toupper={upperOutput?.StringValue ?? "<null>"}");
            Console.WriteLine($"output_string_tolower={lowerOutput?.StringValue ?? "<null>"}");

            await proxy.Stop(context, CancellationToken.None);
            Console.WriteLine("stopped");
            await proxy.Shutdown(context, CancellationToken.None);
            Console.WriteLine("shutdown");
            var upperExpected = inputText.ToUpperInvariant() + ":fakecore";
            var lowerExpected = lowerInputText.ToLowerInvariant() + ":fakecore";
            return upperOutput?.StringValue == upperExpected && lowerOutput?.StringValue == lowerExpected ? 0 : 3;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Timed out waiting for external node output.");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception is not null)
                Console.WriteLine(exception);
        }
    }

    private sealed class TestNodeContext : INodeContext
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<Sample?>> _outputs = new();
        public ILogger Logger { get; } = new ConsoleLogger();

        public TaskCompletionSource<Sample?> GetOutput(string pinName) =>
            _outputs.GetOrAdd(pinName, _ => new TaskCompletionSource<Sample?>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        public BasePin GetInputPin(string name) => throw new NotSupportedException();
        public BasePin GetOutputPin(string name) => throw new NotSupportedException();
        public T? GetInput<T>(string pinName) => default;
        public bool HasInput(string pinName) => false;
        public bool TryGetInput<T>(string pinName, out T? value) { value = default; return false; }
        public Task<Sample?> WaitForInputAsync(string pinName, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => Task.FromResult<Sample?>(null);

        public void SetOutput(string pinName, Sample? value)
        {
            GetOutput(pinName).TrySetResult(value);
        }

        public T? GetService<T>() => default;
    }
}



