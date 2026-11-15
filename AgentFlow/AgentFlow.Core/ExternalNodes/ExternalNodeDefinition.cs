// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalNodeDefinition.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>Fully validated external node package and one nodeType declaration.</summary>
public sealed record ExternalNodeDefinition(
    string PackageDirectory,
    ExternalPackageManifest Manifest,
    ExternalNodeTypeManifest NodeType,
    string WorkerExecutablePath,
    string WorkerWorkingDirectory,
    NodePackageMetadata PackageMetadata,
    IReadOnlyList<BasePin> InputPins,
    IReadOnlyList<BasePin> OutputPins,
    IReadOnlyList<ParameterDefinition> Parameters)
{
    public string PackageId => Manifest.PackageId;
    public string WorkerProtocolVersion => Manifest.Worker.ProtocolVersion.ToString();
    public IReadOnlyList<string> Subscriptions => NodeType.Subscriptions;
}

public sealed class ExternalManifestException : Exception
{
    public ExternalManifestException(string message) : base(message) { }
    public ExternalManifestException(string message, Exception innerException) : base(message, innerException) { }
}