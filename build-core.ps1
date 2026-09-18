#!/usr/bin/env pwsh
#Requires -Version 7
#
# Build the Rust core into the DLL the app P/Invokes.
#
#   pwsh ./build-core.ps1            # release
#   pwsh ./build-core.ps1 debug      # debug, for a faster edit-build-run loop
#
# The result is dist\wlshare_client_core.dll, which WlshareViewer.csproj copies
# beside the .exe. There is no pinned release zip to download, the way
# ../ezvpn-windows gets its DLL: the core is in this repo, so it is built from
# source every time, and its one consumer is the app next to it.
param(
    [ValidateSet('release', 'debug')]
    [string] $Build = 'release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'cargo not found — install Rust (rustup, the MSVC toolchain)' }

# Named explicitly rather than left to the host default: the app is x64, and a
# DLL for anything else loads with BadImageFormatException, which says nothing
# about why.
$Target = if ($env:WLSHARE_CORE_TARGET) { $env:WLSHARE_CORE_TARGET } else { 'x86_64-pc-windows-msvc' }
$Flags = @('build', '--target', $Target)
if ($Build -eq 'release') { $Flags += '--release' }

Write-Host "[core] cargo $($Flags -join ' ')"
Push-Location core
try {
    & cargo @Flags
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

# The CI VM sets a machine-wide CARGO_TARGET_DIR outside the workspace, so the
# DLL is looked for where cargo actually put it.
$TargetDir = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $PSScriptRoot 'core' 'target' }
$Dll = Join-Path $TargetDir $Target $Build 'wlshare_client_core.dll'
if (-not (Test-Path $Dll)) { throw "no DLL at $Dll" }

New-Item -ItemType Directory -Force -Path dist | Out-Null
Copy-Item $Dll dist -Force
$Size = [math]::Round((Get-Item dist\wlshare_client_core.dll).Length / 1MB, 1)
Write-Host "[core] dist\wlshare_client_core.dll ($Size MB)"
