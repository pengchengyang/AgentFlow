// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="DecimalSample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.DECIMAL"/> sample.</summary>
public sealed class DecimalSample : Sample
{
    public override DataType Type => DataType.DECIMAL;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static DecimalSample CreateSample() => new DecimalSample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.DECIMAL => DecimalValue,
            _ => null,
        };

    public override void SetValue(string key, object? value)
    {
        Key = key;
        if (value is decimal d)
            DecimalValue = d;
    }
}




