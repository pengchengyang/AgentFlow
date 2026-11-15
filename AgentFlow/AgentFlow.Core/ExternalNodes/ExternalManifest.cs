// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalManifest.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Core.ExternalNodes;

/// <summary>Serializable representation of Plugins/External/&lt;RID&gt;/&lt;package&gt;/plugin.json.</summary>
public sealed class ExternalPackageManifest
{
    public int ManifestVersion { get; set; } = 1;
    public string PackageId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public ExternalPackageMetadataManifest? Metadata { get; set; }
    public ExternalWorkerManifest Worker { get; set; } = new();
    public List<ExternalNodeTypeManifest> NodeTypes { get; set; } = new();
}

public sealed class ExternalPackageMetadataManifest
{
    public string? Publisher { get; set; }
    public string? Author { get; set; }
    public string? AuthorEmail { get; set; }
    public string? Copyright { get; set; }
    public string? License { get; set; }
    public string? LicenseFile { get; set; }
    public string? Homepage { get; set; }
    public string? Repository { get; set; }
}

public sealed class ExternalWorkerManifest
{
    public int ProtocolVersion { get; set; } = 1;
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public int StartTimeoutMs { get; set; } = 30000;
    public int HeartbeatIntervalMs { get; set; } = 5000;
    public int HeartbeatTimeoutMs { get; set; } = 15000;
    public int CommandTimeoutMs { get; set; } = 30000;
    public int MaxLineBytes { get; set; } = 1_048_576;
    public List<string> Capabilities { get; set; } = new();
}

public sealed class ExternalNodeTypeManifest
{
    public string NodeType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "External";
    public string? Description { get; set; }
    public List<ExternalPinManifest> Pins { get; set; } = new();
    public List<ExternalParameterManifest> Parameters { get; set; } = new();
    public List<string> Subscriptions { get; set; } = new();
}

public sealed class ExternalPinManifest
{
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? Description { get; set; }
    public bool AllowNull { get; set; } = true;
}

public sealed class ExternalParameterManifest
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public System.Text.Json.JsonElement? DefaultValue { get; set; }
    public bool Editable { get; set; } = true;
    public string? Zone { get; set; }
    public string? Group { get; set; }
    public string? Editor { get; set; }
    public List<string>? Options { get; set; }
    public bool Sensitive { get; set; }
}