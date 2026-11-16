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
        ILogger logger,
        IReadOnlyCollection<string>? requestedNodeTypes = null)
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

        var loadedPackages = new ExternalPackageLoadResult[packageDirectories.Count];
        Parallel.For(0, packageDirectories.Count, index =>
        {
            var packageDirectory = packageDirectories[index];
            try
            {
                var definitions = ExternalManifestLoader.LoadPackage(packageDirectory);
                loadedPackages[index] = new ExternalPackageLoadResult(
                    packageDirectory,
                    definitions,
                    null);
            }
            catch (Exception ex)
            {
                loadedPackages[index] = new ExternalPackageLoadResult(
                    packageDirectory,
                    Array.Empty<ExternalNodeDefinition>(),
                    ex);
            }
        });

        var requestedTypeSet = requestedNodeTypes is null
            ? null
            : new HashSet<string>(requestedNodeTypes, StringComparer.OrdinalIgnoreCase);
        ExternalNodeRuntime? runtime = null;
        var registered = 0;
        foreach (var package in loadedPackages.OrderBy(item => item.PackageDirectory, StringComparer.OrdinalIgnoreCase))
        {
            if (package.Error is not null)
            {
                logger.LogError(
                    package.Error,
                    "Failed to load external node Package: {PackageDirectory}",
                    package.PackageDirectory);
                continue;
            }

            foreach (var definition in package.Definitions)
            {
                if (requestedTypeSet is not null && !requestedTypeSet.Contains(definition.NodeType.NodeType))
                    continue;

                runtime ??= new ExternalNodeRuntime(logger);
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

        return new ExternalNodeCatalogResult(registered, registered > 0 ? runtime : null);
    }

    private sealed record ExternalPackageLoadResult(
        string PackageDirectory,
        IReadOnlyList<ExternalNodeDefinition> Definitions,
        Exception? Error);

}
