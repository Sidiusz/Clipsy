<#
.SYNOPSIS
    Build the Clipsy installer.

.DESCRIPTION
    Publishes the WinUI 3 app self-contained for win-x64 and then compiles
    the Inno Setup script into installer\output\Clipsy-Setup-<ver>.exe.

.PARAMETER Version
    Override the version that ends up in the installer file name. Default
    is read from Clipsy.csproj's <Version> property or 0.1.0.

.PARAMETER Configuration
    Build configuration. Default Release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
    powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 0.2.0

    Or double-click BuildInstaller.cmd at the repo root.
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "Clipsy\Clipsy.csproj"
$publishDir = Join-Path $repoRoot "Clipsy\bin\publish\win-x64"
$iss = Join-Path $PSScriptRoot "Clipsy.iss"
$outputDir = Join-Path $PSScriptRoot "output"

if (Test-Path $publishDir) {
    Write-Host "Cleaning previous publish output..."
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir  | Out-Null

Write-Host "Publishing Clipsy ($Configuration / win-x64)..." -ForegroundColor Cyan
& dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:WindowsAppSDKSelfContained=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit $LASTEXITCODE" }

$pf86 = ${env:ProgramFiles(x86)}
$isccCandidates = @(
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $pf86 "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 5\ISCC.exe"),
    (Join-Path $pf86 "Inno Setup 5\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup compiler (ISCC.exe) not found." -ForegroundColor Yellow
    Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isdl.php and re-run." -ForegroundColor Yellow
    Write-Host "Publish output is ready at: $publishDir" -ForegroundColor Yellow
    exit 2
}

Write-Host "Compiling installer with $iscc..." -ForegroundColor Cyan
& $iscc "/DClipsyVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }

Write-Host "Done. Output: $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir | Format-Table Name, Length, LastWriteTime
