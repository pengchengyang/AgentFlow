param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDir = Join-Path $root 'Plugins\Windows\Src'
$outputFile = Join-Path $root 'Plugins\Windows\win-x64\NativeUpperCase\Node.NativeUpperCase.Windows.dll'
$buildDir = Join-Path $root "artifacts\WindowsNative\$Configuration"
$stateDir = Join-Path $root 'artifacts\WindowsNative'
$lockPath = Join-Path $stateDir '.build.lock'
$stampPath = Join-Path $stateDir 'build.stamp'
$scriptPath = $PSCommandPath

function Find-CMake {
    $versionedCandidates = @()
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Filter 'cmake-*' -ErrorAction SilentlyContinue) {
        $candidate = Join-Path $directory.FullName 'bin\cmake.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $versionText = $directory.Name -replace '^cmake-', ''
        $dashIndex = $versionText.IndexOf('-')
        if ($dashIndex -ge 0) {
            $versionText = $versionText.Substring(0, $dashIndex)
        }

        $version = [version]'0.0.0'
        try { $version = [version]$versionText } catch { }

        $versionedCandidates += [pscustomobject]@{
            Path = [System.IO.Path]::GetFullPath($candidate)
            Version = $version
        }
    }

    $portable = $versionedCandidates |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1
    if ($null -ne $portable) {
        return $portable.Path
    }

    $system = Get-Command cmake -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $system) {
        return $system.Source
    }

    throw 'CMake was not found. Place portable CMake under the repository root (cmake-*/bin/cmake.exe), or add cmake.exe to PATH.'
}

function Get-CMakeHelpText([string]$cmakePath) {
    return ((& $cmakePath --help 2>&1 | Out-String).Trim())
}

function Find-VisualStudioGenerator([string]$helpText) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        return $null
    }

    $versions = @(& $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion 2>$null)
    $sortedVersions = $versions |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique |
        Sort-Object -Property @{ Expression = { try { [version]$_ } catch { [version]'0.0' } } } -Descending

    foreach ($versionText in $sortedVersions) {
        $version = try { [version]$versionText } catch { [version]'0.0' }
        switch ($version.Major) {
            18 {
                if ($helpText.Contains('Visual Studio 18 2026')) {
                    return 'Visual Studio 18 2026'
                }
            }
            17 {
                if ($helpText.Contains('Visual Studio 17 2022')) {
                    return 'Visual Studio 17 2022'
                }
            }
        }
    }

    return $null
}

function Select-CMakeGenerator([string]$cmakePath) {
    $helpText = Get-CMakeHelpText $cmakePath
    $visualStudioGenerator = Find-VisualStudioGenerator $helpText
    if ($null -ne $visualStudioGenerator) {
        return [pscustomobject]@{
            Name = $visualStudioGenerator
            Architecture = 'x64'
            FreshSupported = $helpText.Contains('--fresh')
        }
    }

    if ((Get-Command ninja -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1) -and
        $helpText.Contains('Ninja')) {
        return [pscustomobject]@{
            Name = 'Ninja'
            Architecture = $null
            FreshSupported = $helpText.Contains('--fresh')
        }
    }

    throw 'No supported Visual Studio C++ toolchain or Ninja generator was found. Install Visual Studio C++ tools for x64, or make Ninja and the MSVC compiler available in PATH.'
}

function Get-Sha256Hex([byte[]]$bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
        return (($hash | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-BuildFingerprint([string]$cmakePath, [string]$generator, [string]$architecture) {
    $fileHashes = New-Object System.Text.StringBuilder
    [void]$fileHashes.AppendLine("Configuration=$Configuration")
    [void]$fileHashes.AppendLine("Generator=$generator")
    [void]$fileHashes.AppendLine("Architecture=$architecture")
    [void]$fileHashes.AppendLine((& $cmakePath --version | Out-String).Trim())

    $sourceFiles = Get-ChildItem -LiteralPath $sourceDir -Recurse -File |
        Sort-Object -Property FullName
    if (@($sourceFiles).Count -eq 0) {
        throw "No native source files were found under $sourceDir."
    }

    foreach ($file in $sourceFiles) {
        $relativePath = $file.FullName.Substring($sourceDir.Length).TrimStart('\', '/')
        [void]$fileHashes.AppendLine("Source=$relativePath")
        [void]$fileHashes.AppendLine((Get-Sha256Hex ([System.IO.File]::ReadAllBytes($file.FullName))))
    }

    [void]$fileHashes.AppendLine('Source=Scripts/build_windows_native.ps1')
    [void]$fileHashes.AppendLine((Get-Sha256Hex ([System.IO.File]::ReadAllBytes($scriptPath))))

    return Get-Sha256Hex ([System.Text.Encoding]::UTF8.GetBytes($fileHashes.ToString()))
}

if (-not (Test-Path -LiteralPath $sourceDir -PathType Container)) {
    throw "Windows native source directory was not found: $sourceDir"
}

[System.IO.Directory]::CreateDirectory($buildDir) | Out-Null
[System.IO.Directory]::CreateDirectory($stateDir) | Out-Null

$cmake = Find-CMake
$generator = Select-CMakeGenerator $cmake
$fingerprint = Get-BuildFingerprint $cmake $generator.Name $generator.Architecture

$lock = $null
for ($attempt = 0; $attempt -lt 1200; $attempt++) {
    try {
        $lock = [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        break
    }
    catch [System.IO.IOException] {
        Start-Sleep -Milliseconds 500
    }
}

if ($null -eq $lock) {
    throw "Timed out waiting for the Windows native build lock: $lockPath"
}

try {
    $existingStamp = $null
    if (Test-Path -LiteralPath $stampPath -PathType Leaf) {
        $existingStamp = ([System.IO.File]::ReadAllText($stampPath)).Trim()
    }

    if ((Test-Path -LiteralPath $outputFile -PathType Leaf) -and $existingStamp -eq $fingerprint) {
        Write-Host "Windows native nodes are up to date ($Configuration)."
    }
    else {
        Write-Host "Configuring Windows native nodes ($Configuration) with $($generator.Name)..."
        $configureArgs = @()
        if ($generator.FreshSupported) {
            $configureArgs += '--fresh'
        }
        $configureArgs += @('-S', $sourceDir, '-B', $buildDir, '-G', $generator.Name)
        if (-not [string]::IsNullOrWhiteSpace($generator.Architecture)) {
            $configureArgs += @('-A', $generator.Architecture)
        }

        & $cmake @configureArgs
        if ($LASTEXITCODE -ne 0) {
            throw "CMake configure failed with exit code $LASTEXITCODE."
        }

        Write-Host "Building Windows native nodes ($Configuration)..."
        & $cmake --build $buildDir --config $Configuration --parallel
        if ($LASTEXITCODE -ne 0) {
            throw "CMake build failed with exit code $LASTEXITCODE."
        }

        if (-not (Test-Path -LiteralPath $outputFile -PathType Leaf)) {
            throw "CMake completed successfully but the expected native node DLL was not produced: $outputFile"
        }

        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($stampPath, $fingerprint, $utf8)
        Write-Host "Windows native nodes built under Plugins\Windows\win-x64."
    }
}
finally {
    if ($null -ne $lock) {
        $lock.Dispose()
    }
}
