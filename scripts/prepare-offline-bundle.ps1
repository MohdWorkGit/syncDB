#requires -Version 5.1
<#
.SYNOPSIS
    Gathers everything needed to edit and rebuild syncDB on an air-gapped machine,
    into a single gitignored folder (offline-bundle/ by default).

.DESCRIPTION
    Run this on an INTERNET-CONNECTED machine. It produces:
      - nuget-packages/  : the full NuGet dependency closure (offline restore source)
      - publish/         : (optional) self-contained .NET build, ready to run
      - installers/      : (optional) .NET SDK offline installer
      - MANIFEST.txt     : what's inside + how to use it on the target

    syncDB is a single .NET project (SyncDb.csproj) with no frontend, so there is
    no npm/Node step. Copy the resulting folder to the air-gapped machine and
    follow MANIFEST.txt.

.PARAMETER OutDir
    Output folder. Default: ./offline-bundle (gitignored).

.PARAMETER Runtime
    Runtime identifier for the self-contained publish. Default: win-x64.

.PARAMETER IncludeBuild
    Also produce a self-contained publish of the syncdb executable.

.PARAMETER IncludeInstallers
    Best-effort download of the .NET SDK offline installer, pinned to the EXACT
    SDK version on this machine (from `dotnet --version`) so the air-gapped build
    uses the same SDK. Asset kind follows -Runtime (win=.exe, linux=.tar.gz,
    osx=.pkg). If the download fails, the MANIFEST lists the URL to fetch manually.

.PARAMETER Clean
    Delete the output folder before starting.

.EXAMPLE
    ./scripts/prepare-offline-bundle.ps1

.EXAMPLE
    ./scripts/prepare-offline-bundle.ps1 -IncludeBuild -IncludeInstallers -Clean
#>
[CmdletBinding()]
param(
    [string]$OutDir = "offline-bundle",
    [string]$Runtime = "win-x64",
    [switch]$IncludeBuild,
    [switch]$IncludeInstallers,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# --- Resolve paths relative to the repo root (parent of this script's folder) ---
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project  = Join-Path $RepoRoot 'SyncDb.csproj'

if (-not (Test-Path $Project)) {
    throw "Could not find SyncDb.csproj at '$Project'. Run this from the repo (scripts/ folder)."
}

# Make OutDir absolute under the repo root if a relative path was given
if (-not [System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path $RepoRoot $OutDir
}

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  [ok] $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "  [!!] $msg" -ForegroundColor Yellow }

# --- Preflight: required toolchain ---
Write-Step "Preflight"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "'dotnet' not found on PATH. Install the .NET 10 SDK first." }
$dotnetVersion = (& dotnet --version).Trim()
Write-Ok ".NET SDK $dotnetVersion"

# --- Prepare output folder ---
if ($Clean -and (Test-Path $OutDir)) {
    Write-Step "Cleaning $OutDir"
    Remove-Item -Recurse -Force $OutDir
}
$null = New-Item -ItemType Directory -Force -Path $OutDir

$NugetDir     = Join-Path $OutDir 'nuget-packages'
$InstallerDir = Join-Path $OutDir 'installers'

# --- 1. NuGet dependency closure ---
Write-Step "Restoring NuGet dependency closure -> nuget-packages/"
$null = New-Item -ItemType Directory -Force -Path $NugetDir
# Restore into a local global-style packages folder. This captures the full
# transitive closure, including the runtime-specific assets for $Runtime.
& dotnet restore $Project --packages $NugetDir --runtime $Runtime
& dotnet restore $Project --packages $NugetDir   # also the no-RID graph, for editing
$nupkgCount = (Get-ChildItem -Path $NugetDir -Recurse -Filter *.nupkg -ErrorAction SilentlyContinue).Count
Write-Ok "$nupkgCount .nupkg files cached"

# Emit a nuget.config the target can drop next to the .csproj to restore offline.
$nugetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- Copy this file next to SyncDb.csproj on the air-gapped machine. -->
<configuration>
  <config>
    <!-- Point the global packages folder at the bundled cache. -->
    <add key="globalPackagesFolder" value="OFFLINE_BUNDLE_PATH/nuget-packages" />
  </config>
  <packageSources>
    <clear />
    <add key="offline" value="OFFLINE_BUNDLE_PATH/nuget-packages" />
  </packageSources>
</configuration>
'@
Set-Content -Path (Join-Path $OutDir 'nuget.config.template') -Value $nugetConfig -Encoding UTF8
Write-Ok "Wrote nuget.config.template"

# --- 2. Optional: self-contained build ---
if ($IncludeBuild) {
    Write-Step "Building self-contained syncdb -> publish/"
    $PubOut = Join-Path $OutDir 'publish'
    & dotnet publish $Project -c Release -r $Runtime --self-contained `
        --packages $NugetDir -o $PubOut
    Write-Ok "syncdb published ($Runtime, self-contained)"
}

# --- 3. Optional: offline installer (best effort) ---
# Pin the installer to the EXACT SDK version on this machine ($dotnetVersion, from
# `dotnet --version`), so the air-gapped machine builds with the same SDK that
# produced this bundle. The asset kind follows the target runtime: Windows uses
# the .exe, Linux the .tar.gz, macOS the .pkg. Host: builds.dotnet.microsoft.com
# (the current CDN; the older dotnetcli.azureedge.net is being retired).
$sdkExt = switch -Wildcard ($Runtime) {
    'win-*'   { 'exe' }
    'linux-*' { 'tar.gz' }
    'osx-*'   { 'pkg' }
    default   { 'exe' }
}
$dotnetSdkFile = "dotnet-sdk-$dotnetVersion-$Runtime.$sdkExt"
$dotnetSdkUrl  = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$dotnetVersion/$dotnetSdkFile"
if ($IncludeInstallers) {
    Write-Step "Downloading offline installer (SDK $dotnetVersion, $Runtime) -> installers/"
    $null = New-Item -ItemType Directory -Force -Path $InstallerDir
    $dest = Join-Path $InstallerDir $dotnetSdkFile
    try {
        Write-Host "  downloading $dotnetSdkFile ..."
        Invoke-WebRequest -Uri $dotnetSdkUrl -OutFile $dest -UseBasicParsing
        Write-Ok $dotnetSdkFile
    } catch {
        Write-Warn2 "Failed to download the .NET SDK: $($_.Exception.Message)"
        Write-Warn2 "Download manually from: $dotnetSdkUrl"
    }
}

# --- 4. Manifest ---
Write-Step "Writing MANIFEST.txt"
$now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
if ($IncludeBuild) {
    $buildLine = "publish/               Self-contained syncdb build, ready to run (syncdb.exe)."
} else {
    $buildLine = "(publish/ not included - rerun with -IncludeBuild)"
}
if ($IncludeInstallers) {
    $installerLine = "installers/            .NET SDK $dotnetVersion offline installer ($dotnetSdkFile)."
} else {
    $installerLine = "(installers not included - rerun with -IncludeInstallers, or download manually below)"
}
$manifest = @"
syncDB - Offline Build Bundle
Generated: $now
Built with: .NET SDK $dotnetVersion
Target runtime: $Runtime

CONTENTS
--------
nuget-packages/        NuGet dependency closure ($nupkgCount packages).
nuget.config.template  Drop next to SyncDb.csproj (replace OFFLINE_BUNDLE_PATH).
$buildLine
$installerLine

INSTALLER TO BRING (if not in installers/)
------------------------------------------
.NET SDK $dotnetVersion (offline, $Runtime) - the SAME version that built this bundle:
  $dotnetSdkUrl

USAGE ON THE AIR-GAPPED MACHINE
-------------------------------
1. Install the .NET 10 SDK from installers/ (or your own copy). A self-contained
   publish/ build (with -IncludeBuild) runs without the SDK installed at all.

2. Copy this whole bundle somewhere stable, e.g. C:\offline-bundle.

3. NuGet (choose ONE):
   a) Copy nuget.config.template next to SyncDb.csproj, rename to nuget.config,
      and replace OFFLINE_BUNDLE_PATH with the bundle's full path; then:
         dotnet restore SyncDb.csproj
   b) Or restore straight against the cache:
         dotnet restore SyncDb.csproj --packages C:\offline-bundle\nuget-packages

4. Rebuild:
      dotnet build SyncDb.csproj -c Release

5. Configure and run:
      copy .env.example .env   # then edit credentials / table names / pacing
      dotnet run               # or run publish\syncdb.exe if you used -IncludeBuild

See DEPLOY-AIRGAPPED.md (Part B) for the full walkthrough, and README.md for the
configuration reference.
"@
Set-Content -Path (Join-Path $OutDir 'MANIFEST.txt') -Value $manifest -Encoding UTF8
Write-Ok "MANIFEST.txt written"

Write-Step "Done"
Write-Host "Bundle ready at: $OutDir" -ForegroundColor Green
Write-Host "Verify it offline (disable networking) before carrying it across - see DEPLOY-AIRGAPPED.md A4." -ForegroundColor Green
