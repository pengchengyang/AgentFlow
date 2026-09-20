namespace AgentFlow.Contracts;

/// <summary>
/// Node parameter declaration. Used by the UI property panel to generate
/// editors automatically, and for JSON serialization / type conversion.
/// </summary>
public sealed record ParameterDefinition(
    string Name,
    Type DataType,
    string DisplayName,
    object? DefaultValue = null,
    string? Description = null);
