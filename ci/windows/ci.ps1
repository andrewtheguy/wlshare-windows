#!/usr/bin/env pwsh
#Requires -Version 7
#
# Run this repo's checks natively on this Windows machine.
#
#   pwsh ci/windows/ci.ps1               # the default jobs
#   pwsh ci/windows/ci.ps1 core app      # only these
#   pwsh ci/windows/ci.ps1 -List         # what jobs exist
#
# From the Linux checkout this is reached through ci\windows\remote.ps1, which
# ships this working tree to the windows-ci-build VM and runs it there with no
# arguments — the default jobs.
#
# Jobs:
#   core     the Rust core: cargo test, then clippy with warnings denied
#   app      the core DLL in debug, and a Debug build of the app against it
#   package  the release build and the MSI a release ships
#
# `app` is not in the default set because `package` builds everything it does,
# in Release; it is the quicker loop on a Windows box. It installs nothing and
# changes no machine state, so it is safe to run anywhere.
param(
    [switch] $List,
    [Parameter(ValueFromRemainingArguments = $true)][string[]] $Jobs
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
Set-Location $Root

$AllJobs = 'core', 'app', 'package'
$DefaultJobs = 'core', 'package'
if ($List) {
    Write-Host "available: $($AllJobs -join ' ')"
    Write-Host "default:   $($DefaultJobs -join ' ')"
    exit 0
}
if (-not $Jobs) { $Jobs = $DefaultJobs }
foreach ($job in $Jobs) {
    if ($job -notin $AllJobs) { throw "unknown job: $job (available: $($AllJobs -join ' '))" }
}

function Invoke-Step([string] $Name, [scriptblock] $Run) {
    Write-Host ''
    Write-Host "== $Name =="
    & $Run
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

Write-Host '== toolchain =='
& rustc --version
& cargo --version
& dotnet --version
if ($env:CARGO_TARGET_DIR) { Write-Host "   CARGO_TARGET_DIR=$env:CARGO_TARGET_DIR" }
if ($env:NUGET_PACKAGES) { Write-Host "   NUGET_PACKAGES=$env:NUGET_PACKAGES" }

foreach ($job in $Jobs) {
    switch ($job) {
        'core' {
            Push-Location core
            try {
                Invoke-Step 'core: cargo test' { cargo test }
                Invoke-Step 'core: cargo clippy' { cargo clippy --all-targets -- -D warnings }
            } finally { Pop-Location }
        }
        'app' {
            Invoke-Step 'app: the core' { & (Join-Path $Root 'build-core.ps1') debug }
            Invoke-Step 'app: dotnet build' { dotnet build wlshare-windows.slnx -c Debug }
        }
        'package' {
            # The same script the release workflow runs, so this job is the
            # rehearsal for a release and not an imitation of one.
            Invoke-Step 'package: the MSI' { & (Join-Path $Root 'scripts' 'package-windows.ps1') }
        }
    }
}

Write-Host ''
Write-Host "[ci] $($Jobs -join ' ') passed"
