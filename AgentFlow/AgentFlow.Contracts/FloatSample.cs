// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="FloatSample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.FLOAT"/> sample.</summary>
public sealed class FloatSample : Sample
{
    public override DataType Type => DataType.FLOAT;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static FloatSample CreateSample() => new FloatSample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.FLOAT => FloatValue,
            _ => null,
        };

    public override void SetValue(object? value, string key = "")
    {
        Key = key;
        if (value is float f)
            FloatValue = f;
    }
}
