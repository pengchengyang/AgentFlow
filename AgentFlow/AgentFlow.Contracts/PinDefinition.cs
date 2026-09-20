namespace AgentFlow.Contracts;

/// <summary>Pin direction.</summary>
public enum PinDirection
{
    Input,
    Output
}

/// <summary>
/// Pin definition: an input/output port of a node.
/// Connections between pins form the data flow between plugin dlls.
/// </summary>
public sealed record PinDefinition(
    string Name,
    Type DataType,
    PinDirection Direction,
    bool Required = true);
