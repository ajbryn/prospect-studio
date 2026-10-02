<#
.SYNOPSIS
    Packages the prospect-studio-lite folder into prospect-studio-lite.plugin.

.DESCRIPTION
    A .plugin file is a zip archive whose root is the plugin folder's contents
    (.claude-plugin/plugin.json, skills/, shared/, README.md), with forward-slash
    entry names. The archive is built to a temp file and then moved into place, so
    a failed run leaves the existing .plugin untouched.

.PARAMETER PluginDir
    The plugin source folder. Defaults to prospect-studio-lite next to this script.

.PARAMETER OutFile
    The archive to write. Defaults to <PluginDir>.plugin next to the plugin folder.

.EXAMPLE
    pwsh ./package-plugin-lite.ps1

.EXAMPLE
    pwsh ./package-plugin-lite.ps1 -OutFile C:\temp\test.plugin
#>
[CmdletBinding()]
param(
    [string]$PluginDir = (Join-Path $PSScriptRoot 'prospect-studio-lite'),
    [string]$OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Never shipped: build leftovers and OS clutter.
$excludedDirs = @('__pycache__', '.git', '.pytest_cache', 'node_modules')
$excludedFiles = @('*.pyc', '.DS_Store', 'Thumbs.db', 'desktop.ini', '*.plugin', '*.plugin.tmp')

$PluginDir = (Resolve-Path -LiteralPath $PluginDir).Path.TrimEnd('\', '/')
if (-not $OutFile) {
    $OutFile = Join-Path (Split-Path $PluginDir -Parent) ((Split-Path $PluginDir -Leaf) + '.plugin')
}
$OutFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)

# A broken manifest produces a plugin that fails to install, so check it before packaging.
$manifestPath = Join-Path $PluginDir '.claude-plugin\plugin.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Not a plugin folder: $manifestPath is missing."
}
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "plugin.json is not valid JSON: $($_.Exception.Message)"
}
if (-not $manifest.PSObject.Properties['name'] -or -not $manifest.name) {
    throw "plugin.json has no 'name'."
}
$version = if ($manifest.PSObject.Properties['version']) { $manifest.version } else { '(no version)' }

$files = @(
    Get-ChildItem -LiteralPath $PluginDir -Recurse -File -Force | ForEach-Object {
        $relative = $_.FullName.Substring($PluginDir.Length + 1).Replace('\', '/')
        $segments = $relative.Split('/')
        $inExcludedDir = @($segments | Select-Object -SkipLast 1 | Where-Object { $excludedDirs -contains $_ }).Count -gt 0
        $name = $_.Name
        $isExcludedFile = @($excludedFiles | Where-Object { $name -like $_ }).Count -gt 0
        if (-not $inExcludedDir -and -not $isExcludedFile) {
            [pscustomobject]@{ FullName = $_.FullName; Entry = $relative; Length = $_.Length }
        }
    } | Sort-Object -Property Entry
)

$skillCount = @($files | Where-Object { $_.Entry -like 'skills/*/SKILL.md' }).Count
if ($skillCount -eq 0) {
    Write-Warning 'No skills/*/SKILL.md files found; the plugin will install with no skills.'
}

$tempFile = "$OutFile.tmp"
if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force }

try {
    $zip = [System.IO.Compression.ZipFile]::Open($tempFile, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $file.Entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
    Move-Item -LiteralPath $tempFile -Destination $OutFile -Force
}
catch {
    if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force }
    throw
}

$files | ForEach-Object { Write-Host ('  {0,8:N0}  {1}' -f $_.Length, $_.Entry) }
$sizeKb = [math]::Round((Get-Item -LiteralPath $OutFile).Length / 1KB, 1)
Write-Host ''
Write-Host "Packaged $($manifest.name) $version - $($files.Count) files, $skillCount skills, $sizeKb KB"
Write-Host "  -> $OutFile"
