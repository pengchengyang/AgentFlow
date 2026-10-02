// -----------------------------------------------------------------------
// <copyright company="Your Company" file="ReActAgentNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.RegularExpressions;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.ReActAgent;

/// <summary>
/// A generic ReAct (Reasoning + Acting) agent node.
/// <para>
/// The node runs an internal <c>Thought -> Action -> Observation</c> loop until the model
/// produces a final answer or the step limit is reached. The tools it may call (e.g. query
/// logs, query knowledge base) are injected services resolved from
/// <see cref="INodeContext.GetService{T}"/> via <see cref="IAgentToolProvider"/>; the LLM is
/// resolved via <see cref="ILLMClient"/>. This node is a single generic type reused by every
/// workflow; its behaviour is configured entirely through its parameters (model, system
/// prompt, max steps, temperature).
/// </para>
/// </summary>
public sealed class ReActAgentNode : BaseNode
{
    // Structured-output markers the model is asked to emit.
    private const string FinalMarker = "FINAL:";
    private const string ActionMarker = "ACTION:";
    private const string DefaultSystemPrompt =
        "You are an expert factory log analyst. You reason step by step. " +
        "When you need data, emit exactly one line:\n" +
        "ACTION: <toolName>(<input>)\n" +
        "Then you will receive an observation. When you have enough information to answer, " +
        "emit exactly one line:\n" +
        "FINAL: <your answer>";

    private string _question = "";

    public override string Name => "ReAct Agent";
    public override string? DisplayName { get; set; } = string.Empty;
    public override string Category => "AI";

    /// <summary>Question input pin (from an upstream node / user).</summary>
    private readonly BasePin _inPin = new("Question", DataType.STRING, PinDirection.Input, required: true);

    /// <summary>Final answer output pin (pushed downstream).</summary>
    private readonly BasePin _outPin = new("Answer", DataType.STRING, PinDirection.Output);

    public ReActAgentNode()
    {
        Uuid = "C4E12A9B-3D6F-4B7E-9A1C-5F2E8B0D4A77";
        AddParam();
        AddPins();
    }

    /// <summary>Create the node's input / output pins (required by <see cref="BaseNode"/>).</summary>
    public override void AddPins()
    {
        AddInputPin(_inPin);
        AddOutputPin(_outPin);
    }

    /// <summary>Declare the node's parameters. The mandatory base parameters (Name/Uuid/InstanceId/DisplayName/DependsOn)
    /// come from <see cref="BaseNode.AddParam"/>; the agent-specific ones are added here.</summary>
    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter("Model", typeof(string), "qwen2.5:7b", isEditable: true, group: "General", zone: "AgentZone"));
        AddParameter(new NodeParameter("SystemPrompt", typeof(string), DefaultSystemPrompt, isEditable: true, group: "General", zone: "AgentZone"));
        AddParameter(new NodeParameter("MaxSteps", typeof(int), 5, isEditable: true, group: "General", zone: "AgentZone"));
        AddParameter(new NodeParameter("Temperature", typeof(double), 0.2, isEditable: true, group: "General", zone: "AgentZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Model", out var m) && m is not null) Param("Model").Value = m.ToString() ?? "";
        if (parameters.TryGetValue("SystemPrompt", out var sp) && sp is not null) Param("SystemPrompt").Value = sp.ToString() ?? DefaultSystemPrompt;
        if (parameters.TryGetValue("MaxSteps", out var ms) && ms is not null) Param("MaxSteps").Value = Convert.ToInt32(ms);
        if (parameters.TryGetValue("Temperature", out var t) && t is not null) Param("Temperature").Value = Convert.ToDouble(t);
    }

    /// <summary>Look up a declared parameter by name.</summary>
    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    /// <summary>Capture the question arriving on the Question input pin.</summary>
    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (pin.Id == _inPin.Id && value is StringSample ss && ss.StringValue is { } s)
            _question = s;
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("ReActAgent initialized (model {Model}).", Param("Model").Value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Run the ReAct loop: repeatedly ask the model, parse its structured reply, call the
    /// requested tool with the resulting observation, until a final answer or the step limit.
    /// </summary>
    public override async Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            var llm = context.GetService<ILLMClient>();
            var tools = context.GetService<IAgentToolProvider>();
            if (llm is null || tools is null)
            {
                context.Logger.LogWarning("ReActAgent: ILLMClient or IAgentToolProvider not available on the context.");
                SetAnswer(context, "Agent not configured: LLM or tool services are unavailable.");
                return;
            }

            var systemPrompt = Param("SystemPrompt").Value?.ToString() ?? DefaultSystemPrompt;
            var maxSteps = Convert.ToInt32(Param("MaxSteps").Value ?? 5);
            var question = string.IsNullOrWhiteSpace(_question)
                ? Param("SystemPrompt").Value?.ToString() ?? ""
                : _question;

            var observation = "";
            var finalAnswer = "";
            var finished = false;

            for (var step = 0; step < maxSteps && !finished; step++)
            {
                ct.ThrowIfCancellationRequested();

                var userPrompt = BuildPrompt(question, observation, tools.Tools);
                var response = await llm.CompleteAsync(systemPrompt, userPrompt, ct);
                context.Logger.LogInformation("ReAct step {Step}: {Response}", step + 1, response);

                if (TryParseFinal(response, out var answer))
                {
                    finalAnswer = answer;
                    finished = true;
                }
                else if (TryParseAction(response, out var toolName, out var toolInput))
                {
                    var tool = tools.Find(toolName);
                    observation = tool is null
                        ? $"Unknown tool: {toolName}"
                        : await tool.ExecuteAsync(toolInput, ct);
                    context.Logger.LogInformation("ReAct tool {Tool} -> {Observation}", toolName, observation);
                }
                else
                {
                    // The model gave no action and no final marker; treat the whole reply as the answer.
                    finalAnswer = response;
                    finished = true;
                }
            }

            if (!finished)
                finalAnswer = $"Reached the maximum step limit ({maxSteps}) without a final answer.";

            context.Logger.LogInformation("ReActAgent finished with answer: {Answer}", finalAnswer);
            SetAnswer(context, finalAnswer);
        }
        catch (OperationCanceledException)
        {
            context.Logger.LogWarning("ReActAgent run cancelled.");
            throw;
        }
        finally
        {
            Running = false;
        }
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    /// <summary>Build the user prompt for the next ReAct step, listing available tools.</summary>
    private static string BuildPrompt(string question, string observation, IReadOnlyList<IAgentTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Question: " + question);
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        if (tools.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var tool in tools)
            sb.AppendLine($"  - {tool.Name}: {tool.Description}");
        if (!string.IsNullOrWhiteSpace(observation))
        {
            sb.AppendLine();
            sb.AppendLine("Observation: " + observation);
        }
        return sb.ToString();
    }

    private static bool TryParseFinal(string text, out string answer)
    {
        answer = "";
        if (text.IndexOf(FinalMarker, StringComparison.Ordinal) is var idx && idx >= 0)
        {
            answer = text[(idx + FinalMarker.Length)..].Trim();
            return true;
        }
        return false;
    }

    private static bool TryParseAction(string text, out string tool, out string input)
    {
        tool = "";
        input = "";
        var match = Regex.Match(text, $@"{ActionMarker}\s*(\w+)\s*\(\s*([^)]*)\s*\)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;
        tool = match.Groups[1].Value;
        input = match.Groups[2].Value.Trim();
        return true;
    }

    /// <summary>Push the final answer to the Answer output pin (and the context output).</summary>
    private void SetAnswer(INodeContext context, string answer)
    {
        var sample = StringSample.CreateSample();
        sample.SetValue(answer, "Answer");
        _outPin.SendSample(sample);
        context.SetOutput("Answer", sample);
    }
}

