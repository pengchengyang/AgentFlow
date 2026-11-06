// -----------------------------------------------------------------------
// <copyright company="Your Company" file="SenderNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.Sender;

/// <summary>
/// Command-driven source node: the user invokes "Send Once", "Continuous Send" or
/// "Stop Continuous Send" from the editor context menu. Continuous sends snapshot all
/// parameters when the loop starts, so a running workflow never observes parameter changes.
/// For INT32 and INT64 sources, each successful continuous send advances the value by one.
/// </summary>
public sealed class SenderNode : BaseNode
{
    private static readonly string[] DataTypeOptions =
    {
        "string", "int32", "int64", "float", "double", "binary", "bool"
    };

    private readonly BasePin _outPin =
        new("output", DataType.STRING, PinDirection.Output);

    private readonly object _continuousGate = new();
    private CancellationTokenSource? _continuousCts;
    private Task? _continuousTask;

    public SenderNode()
    {
        Uuid = "D2C82B31-E6FD-4763-A16B-16B8AAEFB65F";
        AddParam();
        AddPins();
        ApplyDataType(Param("DataType").Value);
    }

    public override string Name => "Sender";
    public override string DisplayName { get; set; } = "Sender";
    public override string Category => "General";

    public override void AddPins()
    {
        AddOutputPin(_outPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "DataType", typeof(string), "string",
            isEditable: true, group: "General", zone: "SenderZone",
            editor: ParameterEditorKind.SingleSelect,
            options: DataTypeOptions));
        AddParameter(new NodeParameter(
            "Value", typeof(string), "0",
            isEditable: true, group: "General", zone: "SenderZone"));
        AddParameter(new NodeParameter(
            "IntervalMs", typeof(int), 1000,
            isEditable: true, group: "General", zone: "SenderZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("DataType", out var dt) && dt is not null)
            Param("DataType").Value = ParameterValues.GetString(dt, "string") ?? "string";

        ApplyDataType(Param("DataType").Value);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        // Run establishes the running state; it does not start sending by itself because all
        // sending is driven by the explicit context-menu commands.
        await StopContinuousAsync(context.Logger).ConfigureAwait(false);
        Running = true;
    }

    public override async Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        await StopContinuousAsync(context.Logger).ConfigureAwait(false);
    }

    protected override void AddCommands()
    {
        AddCommand(new NodeCommand
        {
            Id = "send-once",
            Title = "Send Once",
            Group = "Send",
            Order = 10,
            CanExecute = () => Running && !IsContinuousActive,
            Execute = (ctx, _) =>
            {
                if (!Running)
                {
                    ctx.Logger.LogWarning("Sender ignored Send Once: the node is not running.");
                    return Task.CompletedTask;
                }

                SendOnce(ctx.Logger);
                return Task.CompletedTask;
            }
        });

        AddCommand(new NodeCommand
        {
            Id = "start-continuous",
            Title = "Continuous Send",
            Group = "Send",
            Order = 20,
            CanExecute = () => Running && !IsContinuousActive,
            Execute = (ctx, _) =>
            {
                StartContinuous(ctx);
                return Task.CompletedTask;
            }
        });

        AddCommand(new NodeCommand
        {
            Id = "stop-continuous",
            Title = "Stop Continuous Send",
            Group = "Send",
            Order = 30,
            CanExecute = () => IsContinuousActive,
            Execute = async (ctx, _) =>
            {
                if (!IsContinuousActive)
                {
                    ctx.Logger.LogWarning("Sender ignored Stop Continuous Send: no continuous send is active.");
                    return;
                }

                await StopContinuousAsync(ctx.Logger).ConfigureAwait(false);
            }
        });
    }

    private bool IsContinuousActive
    {
        get
        {
            lock (_continuousGate)
                return _continuousTask is { IsCompleted: false };
        }
    }

    private void SendOnce(ILogger logger)
    {
        var dataType = ParseDataType(Param("DataType").Value);
        var rawValue = ParameterValues.GetString(Param("Value").Value, "") ?? "";

        if (!TryParseValue(dataType, rawValue, out var value, out var error))
        {
            logger.LogWarning("Sender ignored Send Once: {Error}", error);
            return;
        }

        if (_outPin.DataType != dataType)
        {
            logger.LogWarning(
                "Sender ignored Send Once: selected DataType {Selected} does not match output pin type {PinType}.",
                dataType, _outPin.DataType);
            return;
        }

        try
        {
            if (_outPin.SendSample(CreateSample(dataType, value)))
                logger.LogInformation("Sender sent {Type} value {Value}.", dataType, FormatValue(dataType, value));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sender failed to send once.");
        }
    }

    private void StartContinuous(NodeCommandContext ctx)
    {
        if (!Running)
        {
            ctx.Logger.LogWarning("Sender ignored Continuous Send: the node is not running.");
            return;
        }

        var dataType = ParseDataType(Param("DataType").Value);
        var rawValue = ParameterValues.GetString(Param("Value").Value, "") ?? "";
        var rawInterval = ParameterValues.GetString(Param("IntervalMs").Value, "1000") ?? "1000";

        if (!TryParseValue(dataType, rawValue, out var value, out var error))
        {
            ctx.Logger.LogWarning("Sender could not start continuous send: {Error}", error);
            return;
        }

        if (!int.TryParse(rawInterval.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMs)
            || intervalMs < 1)
        {
            ctx.Logger.LogWarning(
                "Sender could not start continuous send: IntervalMs must be an integer >= 1 (got '{Interval}').",
                rawInterval);
            return;
        }

        if (_outPin.DataType != dataType)
        {
            ctx.Logger.LogWarning(
                "Sender could not start continuous send: selected DataType {Selected} does not match output pin type {PinType}.",
                dataType, _outPin.DataType);
            return;
        }

        CancellationTokenSource cts;
        Task task;

        lock (_continuousGate)
        {
            if (_continuousTask is { IsCompleted: false })
            {
                ctx.Logger.LogWarning("Sender ignored Continuous Send: a continuous send is already active.");
                return;
            }

            _continuousCts?.Dispose();
            cts = new CancellationTokenSource();
            _continuousCts = cts;
            task = Task.Run(() => ContinuousLoopAsync(dataType, value, intervalMs, ctx.Logger, cts.Token));
            _continuousTask = task;
        }

        ctx.Logger.LogInformation(
            "Sender started continuous send: {Type} value {Value}, interval {IntervalMs}ms.",
            dataType, FormatValue(dataType, value), intervalMs);
    }

    private async Task ContinuousLoopAsync(
        DataType dataType,
        object? value,
        int intervalMs,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!Running)
                {
                    logger.LogWarning("Sender stopped continuous send because the node is no longer running.");
                    return;
                }

                try
                {
                    _outPin.SendSample(CreateSample(dataType, value));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Sender stopped continuous send after a send failure.");
                    return;
                }

                //if (dataType is DataType.INT32 or DataType.INT64)
                //    value = IncrementIntegerValue(dataType, value);

                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the workflow or the Stop Continuous Send command stops the loop.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sender continuous send loop failed.");
        }
    }

    private async Task StopContinuousAsync(ILogger logger)
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_continuousGate)
        {
            cts = _continuousCts;
            task = _continuousTask;
            _continuousCts = null;
            _continuousTask = null;
        }

        if (cts is null && task is null)
            return;

        cts?.Cancel();

        try
        {
            if (task is not null)
                await task.ConfigureAwait(false);

            logger.LogInformation("Sender stopped continuous send.");
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sender failed while stopping continuous send.");
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private void ApplyDataType(object? value)
    {
        var type = ParseDataType(value);
        _outPin.DataType = type;
    }

    private static DataType ParseDataType(object? value)
    {
        return (ParameterValues.GetString(value, "string") ?? "").Trim().ToLowerInvariant() switch
        {
            "string" => DataType.STRING,
            "int32" => DataType.INT32,
            "int64" => DataType.INT64,
            "float" => DataType.FLOAT,
            "double" => DataType.DOUBLE,
            "binary" => DataType.BIN,
            "bool" => DataType.BOOL,
            _ => DataType.STRING,
        };
    }

    private static bool TryParseValue(DataType dataType, string rawValue, out object? value, out string error)
    {
        var text = rawValue.Trim();
        value = null;
        error = "";

        switch (dataType)
        {
            case DataType.STRING:
                value = rawValue;
                return true;

            case DataType.INT32:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32))
                {
                    value = i32;
                    return true;
                }
                error = $"Value '{rawValue}' is not a valid int32.";
                return false;

            case DataType.INT64:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64))
                {
                    value = i64;
                    return true;
                }
                error = $"Value '{rawValue}' is not a valid int64.";
                return false;

            case DataType.FLOAT:
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    value = f;
                    return true;
                }
                error = $"Value '{rawValue}' is not a valid float.";
                return false;

            case DataType.DOUBLE:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    value = d;
                    return true;
                }
                error = $"Value '{rawValue}' is not a valid double.";
                return false;

            case DataType.BOOL:
                switch (text.ToLowerInvariant())
                {
                    case "true":
                    case "1":
                        value = true;
                        return true;
                    case "false":
                    case "0":
                        value = false;
                        return true;
                    default:
                        error = $"Value '{rawValue}' is not a valid bool (use true/false/1/0).";
                        return false;
                }

            case DataType.BIN:
                var hex = text;
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = hex[2..];

                hex = string.Concat(hex.Where(c => !char.IsWhiteSpace(c)));
                if (hex.Length == 0)
                {
                    value = Array.Empty<byte>();
                    return true;
                }

                if (hex.Length % 2 != 0)
                {
                    error = $"Value '{rawValue}' has an odd number of hexadecimal digits.";
                    return false;
                }

                try
                {
                    value = Convert.FromHexString(hex);
                    return true;
                }
                catch (FormatException)
                {
                    error = $"Value '{rawValue}' is not valid hexadecimal.";
                    return false;
                }

            default:
                error = $"Unsupported DataType {dataType}.";
                return false;
        }
    }

    private static Sample CreateSample(DataType dataType, object? value)
    {
        Sample sample = dataType switch
        {
            DataType.STRING => StringSample.CreateSample(),
            DataType.INT32 => Int32Sample.CreateSample(),
            DataType.INT64 => Int64Sample.CreateSample(),
            DataType.FLOAT => FloatSample.CreateSample(),
            DataType.DOUBLE => DoubleSample.CreateSample(),
            DataType.BIN => BinSample.CreateSample(),
            DataType.BOOL => BoolSample.CreateSample(),
            _ => throw new InvalidOperationException($"Unsupported DataType {dataType}."),
        };

        if (dataType == DataType.BIN && value is byte[] bytes)
            sample.SetValue(bytes.ToArray(), "output");
        else
            sample.SetValue(value, "output");

        return sample;
    }

    private static object? IncrementIntegerValue(DataType dataType, object? value)
    {
        return dataType switch
        {
            DataType.INT32 when value is int int32 => unchecked(int32 + 1),
            DataType.INT64 when value is long int64 => unchecked(int64 + 1),
            _ => value,
        };
    }

    private static string FormatValue(DataType dataType, object? value)
    {
        if (dataType == DataType.BIN && value is byte[] bytes)
            return bytes.Length == 0
                ? "(empty)"
                : string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)";
    }
}
