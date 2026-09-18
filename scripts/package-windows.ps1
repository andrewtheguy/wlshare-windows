#!/usr/bin/env pwsh
#Requires -Version 7
#
# Build the app for distribution and wrap it in an MSI.
#
#   pwsh scripts/package-windows.ps1     # dist\package\WlshareViewer-windows-x64.msi
#
# This is the whole of what .github/workflows/release.yml runs, so a local run
# produces the same installer the release carries: there is nothing in the
# workflow that only a runner can do.
#
# Nothing here is signed — there is no certificate to sign with — so SmartScreen
# warns about a downloaded installer. The README says how to get past it.
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Root = (Get-Location).Path

function Invoke-Native([string] $What, [scriptblock] $Run) {
    Write-Host ''
    Write-Host "== $What =="
    & $Run
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

$Out = Join-Path $Root 'dist' 'package'
$Publish = Join-Path $Root 'build' 'publish'
foreach ($dir in $Out, $Publish) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Invoke-Native 'the core' { & (Join-Path $Root 'build-core.ps1') release }

Invoke-Native 'publish the app' {
    dotnet publish src\WlshareViewer\WlshareViewer.csproj -c Release -r win-x64 --self-contained -o $Publish
}
foreach ($needed in 'WlshareViewer.exe', 'WlshareViewer.pri', 'wlshare_client_core.dll', 'wlshare.ico') {
    if (-not (Test-Path (Join-Path $Publish $needed))) { throw "$needed is missing from $Publish" }
}

Invoke-Native 'the MSI' {
    dotnet build installer\WlshareViewer.Installer.wixproj -c Release "-p:PublishDir=$Publish"
}

# The release tag is v<this>. It is read back out of the .exe that was just
# published rather than out of Directory.Build.props, so that what is tagged is
# what shipped.
$Version = (Get-Item (Join-Path $Publish 'WlshareViewer.exe')).VersionInfo.ProductVersion.Split('+')[0]
$Msi = Join-Path $Out 'WlshareViewer-windows-x64.msi'
Copy-Item installer\bin\Release\wlshare.msi $Msi

Push-Location $Out
try {
    # The format sha256sum and shasum both check: the hash, two spaces, the name.
    Get-ChildItem *.msi | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
        Set-Content -NoNewline:$false SHA256SUMS
} finally { Pop-Location }
Set-Content (Join-Path $Out 'VERSION') $Version

Write-Host ''
Write-Host "[package] $Msi ($([math]::Round((Get-Item $Msi).Length / 1MB, 1)) MB), version $Version"
Get-Content (Join-Path $Out 'SHA256SUMS')
