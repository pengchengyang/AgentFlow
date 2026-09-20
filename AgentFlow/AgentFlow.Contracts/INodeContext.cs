using Microsoft.Extensions.Logging;

namespace AgentFlow.Contracts;

/// <summary>
/// Node execution context: read input pins, write output pins, logging, GuiBridge.
/// Provided by the workflow engine.
/// </summary>
public interface INodeContext
{
    ILogger Logger { get; }

    IGuiBridge Gui { get; }

    /// <summary>Read the value of an input pin (received from an upstream output pin).</summary>
    T? GetInput<T>(string pinName);

    /// <summary>Write a value to an output pin (pushed to all connected downstream input pins).</summary>
    void SetOutput(string pinName, object? value);
}
