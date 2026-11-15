param(
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [Parameter(Mandatory = $true)]
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
$outputDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($Output))
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$entries = @()
foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe' } | Sort-Object FullName) {
    $relativePath = $file.FullName.Substring($resolvedRoot.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
    $assemblyVersion = $null
    if ($file.Extension -eq '.dll') {
        try {
            $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName).Version.ToString()
        }
        catch {
            $assemblyVersion = $null
        }
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $hashBytes = $sha256.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }
        $hashText = ($hashBytes | ForEach-Object { $_.ToString('x2') }) -join ''
    }
    finally {
        $sha256.Dispose()
    }
    $entries += [ordered]@{
        relativePath = $relativePath
        fileName = $file.Name
        assemblyVersion = $assemblyVersion
        fileVersion = $file.VersionInfo.FileVersion
        productVersion = $file.VersionInfo.ProductVersion
        length = $file.Length
        sha256 = $hashText
    }
}

$document = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    root = '.'
    fileCount = $entries.Count
    files = $entries
}
$document | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Output -Encoding UTF8
Write-Output "Binary version report written: $Output ($($entries.Count) files)"
