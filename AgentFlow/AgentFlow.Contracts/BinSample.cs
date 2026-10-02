// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BinSample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.BIN"/> sample.</summary>
public sealed class BinSample : Sample
{
    public override DataType Type => DataType.BIN;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static BinSample CreateSample() => new BinSample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.BIN => BinValue,
            _ => null,
        };

    public override void SetValue(object? value, string key = "")
    {
        Key = key;
        if (value is byte[] b)
            BinValue = b;
    }
}





