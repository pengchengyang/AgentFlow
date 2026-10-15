// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FaultDiagnosisAgentNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FaultDiagnosisAgent;

/// <summary>
/// Demo agent node: &quot;Fault Diagnosis Agent&quot;. This is a placeholder used to verify the editor
/// UI (palette, canvas, parameter dialog, wiring). It simply forwards its string input
/// to its output and logs, and may be removed later.
/// </summary>
public sealed class FaultDiagnosisAgentNode : BaseNode
{
    public override string Name => "Fault Diagnosis Agent";
    public override string DisplayName { get; set; } = string.Empty;
    public override string Category => "AI";

    /// <summary>Input pin.</summary>
    public BasePin InputPin { get; } = new("Input", DataType.STRING, PinDirection.Input);

    /// <summary>Output pin.</summary>
    public BasePin OutputPin { get; } = new("Output", DataType.STRING, PinDirection.Output);

    public FaultDiagnosisAgentNode()
    {
        Uuid = "90CC5C07-2277-4115-BC8C-976AC3554D9F";
        AddParam();
        AddPins();
    }

    /// <summary>Create the node's input / output pins.</summary>
    public override void AddPins()
    {
        AddInputPin(InputPin);
        AddOutputPin(OutputPin);
    }

    /// <summary>Declare the node's parameters.</summary>
    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Model", typeof(string), "default", isEditable: true, group: "Settings", zone: "General"));
        AddParameter(new NodeParameter("Description", typeof(string), "Fault Diagnosis Agent demo node", isEditable: true, group: "Settings", zone: "General"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Model", out var m) && m is not null) Param("Model").Value = m.ToString() ?? "";
        if (parameters.TryGetValue("Description", out var d) && d is not null) Param("Description").Value = d.ToString() ?? "";
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            var input = context.GetInput<string>("Input") ?? "";
            var sample = StringSample.CreateSample();
            sample.SetValue(input, "Output");
            context.SetOutput("Output", sample);
            context.Logger.LogInformation("{Node}: ran (input length {Len})", Name, input.Length);
        }
        finally
        {
            Running = false;
        }
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }
}
