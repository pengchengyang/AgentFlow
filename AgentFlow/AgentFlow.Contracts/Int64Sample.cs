// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Int64Sample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.INT64"/> sample.</summary>
public sealed class Int64Sample : Sample
{
    public override DataType Type => DataType.INT64;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static Int64Sample CreateSample() => new Int64Sample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.INT64 => Int64Value,
            _ => null,
        };

    public override void SetValue(string key, object? value)
    {
        Key = key;
        if (value is long l)
            Int64Value = l;
    }
}




