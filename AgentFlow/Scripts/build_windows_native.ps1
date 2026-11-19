param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDir = Join-Path $root 'Plugins\Windows\Src'
$buildDir = Join-Path $root "artifacts\WindowsNative\$Configuration"
$cmake = (Get-Command cmake -ErrorAction Stop).Source

Write-Host "Configuring Windows native nodes ($Configuration)..."
& $cmake -S $sourceDir -B $buildDir -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }

Write-Host "Building Windows native nodes ($Configuration)..."
& $cmake --build $buildDir --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "CMake build failed with exit code $LASTEXITCODE." }

Write-Host "Windows native nodes built under Plugins\Windows\win-x64."