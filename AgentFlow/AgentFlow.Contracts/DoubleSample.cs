// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="DoubleSample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.DOUBLE"/> sample.</summary>
public sealed class DoubleSample : Sample
{
    public override DataType Type => DataType.DOUBLE;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static DoubleSample CreateSample() => new DoubleSample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.DOUBLE => DoubleValue,
            _ => null,
        };

    public override void SetValue(string key, object? value)
    {
        Key = key;
        if (value is double d)
            DoubleValue = d;
    }
}




