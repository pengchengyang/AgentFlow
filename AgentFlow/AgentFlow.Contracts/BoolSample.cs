// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BoolSample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.BOOL"/> sample.</summary>
public sealed class BoolSample : Sample
{
    public override DataType Type => DataType.BOOL;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static BoolSample CreateSample() => new BoolSample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.BOOL => BoolValue,
            _ => null,
        };

    public override void SetValue(string key, object? value)
    {
        Key = key;
        if (value is bool b)
            BoolValue = b;
    }
}




