// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGSHandlerNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace Node.CFGSHandler;

/// <summary>
/// Contract-only CFGS handler node. The input and output pins are reserved for the future
/// CFGS launch/stop workflow, but no business processing is implemented yet.
/// </summary>
public sealed class CFGSHandlerNode : BaseNode
{
    private readonly BasePin _rawFromCfgInputPin =
        new("in_str_raw_from_cfg", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _fromUpperInputPin =
        new("in_str_from_upper", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _userSettingsInputPin =
        new("in_str_user_settings", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _cfgStartReturnInputPin =
        new("in_int64_cfgstart_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _cfgExitReturnInputPin =
        new("in_int64_cfgexit_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _cfgStartReturnStringInputPin =
        new("in_str_cfgstart_ret", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _cfgExitReturnStringInputPin =
        new("in_str_cfgexit_ret", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _launchCfgOutputPin =
        new("out_str_launch_cfg", DataType.STRING, PinDirection.Output);

    private readonly BasePin _stopCfgOutputPin =
        new("out_stop_cfg", DataType.STRING, PinDirection.Output);

    private readonly BasePin _confirmCfgExitOutputPin =
        new("out_str_confirmcfg_exit", DataType.STRING, PinDirection.Output);

    public CFGSHandlerNode()
    {
        Uuid = "CEC9E7C3-7F5D-48BD-BA1D-CCBDF22E7820";
        AddParam();
        AddPins();
    }

    public override string Name => "CFGS Handler";
    public override string DisplayName { get; set; } = "CFGS Handler";
    public override string Category => "Protocol";

    public override void AddPins()
    {
        AddInputPin(_rawFromCfgInputPin);
        AddInputPin(_fromUpperInputPin);
        AddInputPin(_userSettingsInputPin);
        AddInputPin(_cfgStartReturnInputPin);
        AddInputPin(_cfgExitReturnInputPin);
        AddInputPin(_cfgStartReturnStringInputPin);
        AddInputPin(_cfgExitReturnStringInputPin);
        AddOutputPin(_launchCfgOutputPin);
        AddOutputPin(_stopCfgOutputPin);
        AddOutputPin(_confirmCfgExitOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}