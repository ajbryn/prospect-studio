#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes the Prospect Studio MCP server and prints the Claude Desktop registration snippet.

.DESCRIPTION
    Publishes ProspectStudio.Mcp as a self-contained win-x64 Release build into dist/mcp, then
    prints a claude_desktop_config.json snippet to paste by hand.

    This script never reads or writes the Claude Desktop configuration file. It prints; you paste.

.PARAMETER Output
    Publish directory. Defaults to dist/mcp in the repository root.

.PARAMETER Workspace
    Value for PROSPECT_STUDIO_HOME in the printed snippet. Defaults to
    "$env:USERPROFILE\Documents\Prospect Studio".

.PARAMETER TrackingBaseUrl
    Value for PS_TRACKING_BASE_URL in the printed snippet.

.EXAMPLE
    pwsh tools/publish-mcp.ps1
#>
[CmdletBinding()]
param(
    [string] $Output,
    [string] $Workspace,
    [string] $TrackingBaseUrl = 'https://example.com/lp?code={code}'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ProspectStudio.Mcp\ProspectStudio.Mcp.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Could not find $project."
}

if (-not $Output) {
    $Output = Join-Path $repoRoot 'dist\mcp'
}

if (-not $Workspace) {
    $Workspace = Join-Path $env:USERPROFILE 'Documents\Prospect Studio'
}

Write-Host "Publishing $project" -ForegroundColor Cyan
Write-Host "  configuration : Release"
Write-Host "  runtime       : win-x64 (self-contained)"
Write-Host "  output        : $Output"
Write-Host ''

dotnet publish $project --configuration Release --runtime win-x64 --self-contained --output $Output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $Output 'ProspectStudio.Mcp.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish finished but $exe is missing."
}

$exePath = (Resolve-Path -LiteralPath $exe).Path

Write-Host ''
Write-Host 'Published:' -ForegroundColor Green
Write-Host "  $exePath"
Write-Host ''
Write-Host 'Checking the published server (doctor)...' -ForegroundColor Cyan
& $exePath doctor
if ($LASTEXITCODE -ne 0) {
    throw "The published server failed to run 'doctor' (exit code $LASTEXITCODE)."
}

$snippet = [ordered]@{
    mcpServers = [ordered]@{
        'prospect-studio' = [ordered]@{
            command = $exePath
            env     = [ordered]@{
                PROSPECT_STUDIO_HOME = $Workspace
                PS_TRACKING_BASE_URL = $TrackingBaseUrl
                CENSUS_API_KEY       = ''
            }
        }
    }
} | ConvertTo-Json -Depth 6

Write-Host ''
Write-Host 'Paste this into claude_desktop_config.json (merge with any servers already there):' -ForegroundColor Yellow
Write-Host ''
Write-Output $snippet
Write-Host ''
Write-Host 'Where to put it:' -ForegroundColor Yellow
Write-Host '  %APPDATA%\Claude\claude_desktop_config.json'
Write-Host '  Claude Desktop > Settings > Developer > Edit Config opens it, but on Windows MSIX builds'
Write-Host '  that button can open a different file than the app loads - check the path in the title bar.'
Write-Host '  Restart Claude Desktop afterwards, then ask: "What is the Prospect Studio status?"'
Write-Host ''
Write-Host 'This script does not touch claude_desktop_config.json.' -ForegroundColor DarkGray
