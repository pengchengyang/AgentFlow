// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalNodeCatalog.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace AgentFlow.Core.ExternalNodes;

public sealed record ExternalNodeCatalogResult(
    int RegisteredNodeTypes,
    ExternalNodeRuntime? Runtime);

/// <summary>Scans Plugins/External/&lt;standard-RID&gt;/&lt;package&gt; and registers node descriptors.</summary>
public static class ExternalNodeCatalog
{
    public static ExternalNodeCatalogResult RegisterFromDirectory(
        string externalDirectory,
        NodeRegistry registry,
        ILogger logger)
    {
        if (!Directory.Exists(externalDirectory))
            return new ExternalNodeCatalogResult(0, null);

        var runtimeIdentifier = ExternalRuntimeIdentifier.Current;
        var platformDirectory = Path.Combine(externalDirectory, runtimeIdentifier);
        if (!Directory.Exists(platformDirectory))
        {
            logger.LogDebug(
                "No external node package directory exists for runtime {RuntimeIdentifier}: {PlatformDirectory}",
                runtimeIdentifier,
                platformDirectory);
            return new ExternalNodeCatalogResult(0, null);
        }

        var packageDirectories = Directory.EnumerateDirectories(platformDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "plugin.json")))
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (packageDirectories.Count == 0)
            return new ExternalNodeCatalogResult(0, null);

        logger.LogInformation(
            "Scanning external node packages for runtime {RuntimeIdentifier} from {PlatformDirectory}.",
            runtimeIdentifier,
            platformDirectory);

        ExternalNodeRuntime? runtime = null;
        var registered = 0;
        foreach (var packageDirectory in packageDirectories)
        {
            try
            {
                var definitions = ExternalManifestLoader.LoadPackage(packageDirectory);
                runtime ??= new ExternalNodeRuntime(logger);
                foreach (var definition in definitions)
                {
                    registry.Register(new NodeDescriptor(
                        definition.NodeType.NodeType,
                        definition.NodeType.DisplayName,
                        definition.NodeType.Category,
                        typeof(ExternalNodeProxy),
                        definition.InputPins,
                        definition.OutputPins,
                        definition.Parameters,
                        () => new ExternalNodeProxy(definition, runtime),
                        definition.PackageMetadata,
                        definition));
                    registered++;
                    logger.LogInformation(
                        "Registered external node {NodeType} from package {PackageId} {Version}.",
                        definition.NodeType.NodeType,
                        definition.PackageId,
                        definition.Manifest.Version);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load external node Package: {PackageDirectory}", packageDirectory);
            }
        }

        return new ExternalNodeCatalogResult(registered, registered > 0 ? runtime : null);
    }
}
