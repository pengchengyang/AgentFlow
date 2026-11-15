# -----------------------------------------------------------------------
# <copyright company="Rolling Wireless SARL" file="Build-Release.ps1">
#     Copyright (c) Rolling Wireless SARL. All rights reserved.
#     Author: Damon Yang (damon.yang@rollingwireless.com)
# </copyright>
# -----------------------------------------------------------------------

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release", "All")]
    [string]$Configuration = "All",

    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-arm")]
    [string[]]$Rids = @(),

    [switch]$SkipExternal,
    [switch]$SkipTests,
    [switch]$SkipPackage,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $Root "AgentFlow.sln"
$TestProject = Join-Path $Root "tests\WorkflowRun.IntegrationTests\WorkflowRun.IntegrationTests.csproj"
$PackageScript = Join-Path $Root "package-sdk-dll.py"
$Configurations = if ($Configuration -eq "All") { @("Debug", "Release") } else { @($Configuration) }

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $display = "$File $($Arguments -join ' ')"
    Write-Host "`n[Build-Release] $display" -ForegroundColor Cyan
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $display"
    }
}

function Invoke-DotNetBuild {
    param([string]$BuildConfiguration)

    $arguments = @(
        "build", $Solution,
        "-c", $BuildConfiguration,
        "-p:Platform=x64",
        "-p:AgentFlowExternalNodeEnabled=$(-not $SkipExternal.IsPresent)"
    )
    if ($NoRestore) { $arguments += "--no-restore" }
    Invoke-Checked -File "dotnet" -Arguments $arguments
}

function Invoke-IntegrationTests {
    param([string]$BuildConfiguration)

    if ($SkipTests) { return }
    $arguments = @(
        "run", "--project", $TestProject,
        "-c", $BuildConfiguration,
        "-p:Platform=x64",
        "--no-build"
    )
    Invoke-Checked -File "dotnet" -Arguments $arguments
}

function Invoke-SdkPackage {
    if ($SkipPackage) { return }

    $arguments = @($PackageScript)
    if ($SkipExternal) { $arguments += "--skip-external" }
    foreach ($rid in $Rids) {
        $arguments += @("--rid", $rid)
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        throw "python was not found on PATH; the SDK package step requires Python."
    }

    Invoke-Checked -File "python" -Arguments $arguments
}

foreach ($buildConfiguration in $Configurations) {
    Invoke-DotNetBuild -BuildConfiguration $buildConfiguration
    Invoke-IntegrationTests -BuildConfiguration $buildConfiguration
}

Invoke-SdkPackage

Write-Host "`n[Build-Release] Completed successfully." -ForegroundColor Green
