// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Sample.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Base class for a typed data sample used to transfer values between pins / nodes.
/// Concrete subclasses expose their <see cref="DataType"/>, a <see cref="Key"/> used to
/// match a sample between the sending and receiving sides, plus a static
/// <c>CreateSample</c> factory, and <see cref="GetValue"/> / <see cref="SetValue"/> accessors.
/// Nullable storage fields cover every supported data type. These samples are used only for
/// in-memory data transfer and are not persisted.
/// </summary>
public abstract class Sample
{
    /// <summary>The concrete data type of this sample.</summary>
    public abstract DataType Type { get; }

    /// <summary>
    /// Identifier (e.g. the pin / connection name) used to match this sample between the
    /// sending and receiving sides. Assigned through <see cref="SetValue"/>.
    /// </summary>
    public abstract string Key { get; set; }

    // Nullable typed storage, one field per supported data type.
    public string? StringValue;
    public int? Int32Value;
    public long? Int64Value;
    public bool? BoolValue;
    public decimal? DecimalValue;
    public double? DoubleValue;
    public byte[]? BinValue;

    /// <summary>Return the value stored for the requested <paramref name="type"/>, or null if not present.</summary>
    public abstract object? GetValue(DataType type);

    /// <summary>Store <paramref name="value"/> and assign the optional <paramref name="key"/> identifier.</summary>
    public abstract void SetValue(object? value, string key = "");
}







