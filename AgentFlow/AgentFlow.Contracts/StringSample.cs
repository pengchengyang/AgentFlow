// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="StringSample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>A <see cref="DataType.STRING"/> sample.</summary>
public sealed class StringSample : Sample
{
    public override DataType Type => DataType.STRING;

    /// <summary>Identifier assigned through <see cref="SetValue"/>.</summary>
    public override string Key { get; set; } = "";

    public static StringSample CreateSample() => new StringSample();

    public override object? GetValue(DataType type) =>
        type switch
        {
            DataType.STRING => StringValue,
            _ => null,
        };

    public override void SetValue(object? value, string key = "")
    {
        Key = key;
        if (value is string s)
            StringValue = s;
    }
}





