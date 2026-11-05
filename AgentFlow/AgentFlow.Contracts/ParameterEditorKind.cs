// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ParameterEditorKind.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Hint for the property dialog: how an editable parameter should be presented.
/// Automatic is the default and keeps the existing text-box behavior. MultiSelect
/// shows the declared <see cref="NodeParameter.Options"/> as a multi-choice dropdown
/// and stores a semicolon-separated string; SingleSelect shows the same choices as a
/// one-choice dropdown and stores exactly one option value.
/// </summary>
public enum ParameterEditorKind
{
    Auto,
    Text,
    MultiSelect,
    SingleSelect,
}
