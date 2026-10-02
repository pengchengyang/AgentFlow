// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Int32Sample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.INT32"/> sample.</summary>
public sealed class Int32Sample : Sample
{
    public override DataType Type => DataType.INT32;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static Int32Sample CreateSample() => new Int32Sample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.INT32 => Int32Value,
            _ => null,
        };

    public override void SetValue(string key, object? value)
    {
        Key = key;
        if (value is int i)
            Int32Value = i;
    }
}




