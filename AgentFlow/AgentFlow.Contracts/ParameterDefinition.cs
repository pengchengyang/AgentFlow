// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ParameterDefinition.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

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
