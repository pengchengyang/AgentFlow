<#
.SYNOPSIS
    Fails when a source file is not valid UTF-8 without BOM (for example a file saved as GBK,
    or a BOM that was added by an editor).

.DESCRIPTION
    Scans the repository (skipping obj/, bin/ and .git/) and strictly decodes every source,
    project and documentation file. A file that cannot be decoded as UTF-8, or that starts
    with a UTF-8 BOM, is reported and makes the script exit with code 1.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-encodings.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-encodings.ps1 -Root C:\Codes\AgentFlow\AgentFlow\AgentFlow
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    if (-not $Root) {
        $Root = (Get-Location).Path
    }
}

$extensions = @(
    '.cs', '.axaml', '.xaml', '.json', '.md', '.csproj', '.targets', '.props',
    '.pubxml', '.sln', '.ps1', '.psm1', '.sh', '.yml', '.yaml'
)
$encoding = New-Object System.Text.UTF8Encoding($false, $true)

$files = Get-ChildItem -Path $Root -Recurse -File |
    Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() -and $_.FullName -notmatch '\\(obj|bin|\.git)\\' }

$notUtf8 = New-Object System.Collections.Generic.List[string]
$withBom = New-Object System.Collections.Generic.List[string]
$scanned = 0

foreach ($file in $files) {
    $scanned++
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)

    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $withBom.Add($file.FullName.Substring($Root.Length).TrimStart('\', '/'))
    }

    try {
        [void]$encoding.GetString($bytes)
    } catch {
        $notUtf8.Add($file.FullName.Substring($Root.Length).TrimStart('\', '/'))
    }
}

if ($notUtf8.Count -gt 0) {
    Write-Host "ERROR: $($notUtf8.Count) file(s) are not valid UTF-8:" -ForegroundColor Red
    $notUtf8 | ForEach-Object { Write-Host "  $_" }
}

if ($withBom.Count -gt 0) {
    Write-Host "ERROR: $($withBom.Count) file(s) start with a UTF-8 BOM (policy: UTF-8 without BOM):" -ForegroundColor Red
    $withBom | ForEach-Object { Write-Host "  $_" }
}

if ($notUtf8.Count -gt 0 -or $withBom.Count -gt 0) {
    exit 1
}

Write-Host "OK: $scanned source files are UTF-8 without BOM."
exit 0
