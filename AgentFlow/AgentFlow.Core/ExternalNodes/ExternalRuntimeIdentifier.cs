// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalRuntimeIdentifier.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>
/// Resolves the standard .NET Runtime Identifier used to select external packages.
/// Supported package directories are win-x64, win-arm64, linux-x64, linux-arm64 and linux-arm.
/// </summary>
public static class ExternalRuntimeIdentifier
{
    public const string WinX64 = "win-x64";
    public const string WinArm64 = "win-arm64";
    public const string LinuxX64 = "linux-x64";
    public const string LinuxArm64 = "linux-arm64";
    public const string LinuxArm = "linux-arm";

    public static IReadOnlyList<string> Supported { get; } =
        new[] { WinX64, WinArm64, LinuxX64, LinuxArm64, LinuxArm };

    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => WinX64,
                Architecture.Arm64 => WinArm64,
                _ => $"{WinX64}-unsupported-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => LinuxX64,
                Architecture.Arm64 => LinuxArm64,
                Architecture.Arm => LinuxArm,
                _ => $"{LinuxX64}-unsupported-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"
            };
        }

        return $"unsupported-{RuntimeInformation.OSDescription.Replace(' ', '-').ToLowerInvariant()}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }
}
