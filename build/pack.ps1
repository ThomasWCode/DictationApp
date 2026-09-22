<#
.SYNOPSIS
  Publishes DictationApp and packs a Velopack installer (Setup.exe + delta-capable release feed).

.DESCRIPTION
  Requires the .NET 8 SDK and the Velopack CLI: `dotnet tool install -g vpk`.
  Output goes to artifacts/releases. Upload that folder to a GitHub Release so the in-app
  UpdateCheckService (GithubSource) can find it.

.EXAMPLE
  ./build/pack.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [switch] $SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts/publish"
$releaseDir = Join-Path $root "artifacts/releases"

Write-Host "Publishing $Configuration/$Runtime (self-contained: $($SelfContained.IsPresent))..."
dotnet publish (Join-Path $root "src/DictationApp/DictationApp.csproj") `
    -c $Configuration -r $Runtime --self-contained:$($SelfContained.IsPresent) `
    -p:Version=$Version -p:DebugType=embedded -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Velopack CLI not found. Run: dotnet tool install -g vpk"
}

Write-Host "Packing with Velopack..."
New-Item -ItemType Directory -Force $releaseDir | Out-Null
vpk pack `
    --packId DictationApp `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe DictationApp.exe `
    --packTitle "DictationApp" `
    --packAuthors "ThomasWCode" `
    --icon (Join-Path $PSScriptRoot "app.ico") `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "Done. Installer and release feed in $releaseDir"
Get-ChildItem $releaseDir | Select-Object Name, Length | Format-Table -AutoSize
