// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeCommandInfo.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Core;

/// <summary>
/// UI-facing snapshot of one node command. The editor sees only this lightweight DTO and never
/// reads the contract-level <see cref="AgentFlow.Contracts.NodeCommand"/> directly.
/// </summary>
public sealed record NodeCommandInfo(
    string Id,
    string Title,
    string? IconKey,
    string? Group,
    int Order,
    bool IsEnabled,
    bool RequiresConfirmation);
