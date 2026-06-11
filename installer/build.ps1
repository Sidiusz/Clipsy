<#
.SYNOPSIS
    Build the Clipsy installer.

.DESCRIPTION
    Publishes the WinUI 3 app self-contained for win-x64 and then compiles
    the Inno Setup script into installer\output\Clipsy-Setup-<ver>.exe.
    Auto-installs Inno Setup 6 if it is not already on the machine, using
    winget when available, otherwise a direct download from jrsoftware.org.

.PARAMETER Version
    Override the version that ends up in the installer file name.
    If omitted, the script reads the repo-root version file.

.PARAMETER Configuration
    Build configuration. Default Release.

.PARAMETER SkipInnoInstall
    Do not auto-install Inno Setup. The script will publish and stop with
    exit code 2 if ISCC.exe is not found.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
    powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 0.2.0

    Or double-click BuildInstaller.cmd at the repo root.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipInnoInstall
)

$ErrorActionPreference = "Stop"
$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot "..")
$project    = Join-Path $repoRoot "Clipsy\Clipsy.csproj"
$publishDir = Join-Path $repoRoot "Clipsy\bin\publish\win-x64"
$iss        = Join-Path $PSScriptRoot "Clipsy.iss"
$outputDir  = Join-Path $PSScriptRoot "output"
$versionFile = Join-Path $repoRoot "version"
$legacyVersionTxt = Join-Path $PSScriptRoot "version.txt"
$syncScript = Join-Path $PSScriptRoot "sync_version.ps1"

function Resolve-Version {
    param([string]$RequestedVersion)

    if ($RequestedVersion) { return $RequestedVersion.Trim() }

    if (Test-Path -LiteralPath $versionFile) {
        $fileVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        if ($fileVersion) { return $fileVersion }
    }

    if (Test-Path -LiteralPath $legacyVersionTxt) {
        $fileVersion = (Get-Content -LiteralPath $legacyVersionTxt -Raw).Trim()
        if ($fileVersion) { return $fileVersion }
    }

    $csprojText = Get-Content -LiteralPath $project -Raw
    if ($csprojText -match '<Version>(.*?)</Version>') {
        $fromProject = $Matches[1].Trim()
        if ($fromProject) { return $fromProject }
    }

    return "1.0.0"
}

function Find-Iscc {
    $pf   = $env:ProgramFiles
    $pf86 = [System.Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    $candidates = @(
        (Join-Path $pf   "Inno Setup 6\ISCC.exe"),
        (Join-Path $pf86 "Inno Setup 6\ISCC.exe"),
        (Join-Path $pf   "Inno Setup 5\ISCC.exe"),
        (Join-Path $pf86 "Inno Setup 5\ISCC.exe")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

function Install-InnoSetup {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "Installing Inno Setup via winget (UAC may prompt)..." -ForegroundColor Cyan
        try {
            $wingetArgs = @(
                "install","--id","JRSoftware.InnoSetup","-e","--silent",
                "--accept-source-agreements","--accept-package-agreements",
                "--scope","machine"
            )
            & winget @wingetArgs
            if ($LASTEXITCODE -eq 0) {
                Start-Sleep -Seconds 2
                $found = Find-Iscc
                if ($found) { return $found }
            } else {
                Write-Host "winget exit $LASTEXITCODE - falling back to direct download." -ForegroundColor Yellow
            }
        } catch {
            Write-Host ("winget failed ({0}) - falling back to direct download." -f $_) -ForegroundColor Yellow
        }
    }

    $url     = "https://jrsoftware.org/download.php/is.exe"
    $tempExe = Join-Path ([System.IO.Path]::GetTempPath()) "innosetup-installer.exe"
    Write-Host "Downloading Inno Setup from $url ..." -ForegroundColor Cyan

    $oldProgress = $ProgressPreference
    try {
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $url -OutFile $tempExe -UseBasicParsing
    } finally {
        $ProgressPreference = $oldProgress
    }
    if (-not (Test-Path $tempExe)) { throw "Inno Setup download failed." }

    Write-Host "Running Inno Setup installer silently (UAC may prompt)..." -ForegroundColor Cyan
    $proc = Start-Process -FilePath $tempExe `
        -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-" `
        -Wait -PassThru
    Remove-Item $tempExe -Force -ErrorAction SilentlyContinue
    if ($proc.ExitCode -ne 0) { throw "Inno Setup installer exited with code $($proc.ExitCode)." }

    Start-Sleep -Seconds 1
    $found = Find-Iscc
    if (-not $found) { throw "Inno Setup install ran but ISCC.exe is still missing." }
    return $found
}

$Version = Resolve-Version $Version

# Keep all versioned files aligned and repair app.manifest if it was corrupted by
# an earlier script run.
if (Test-Path -LiteralPath $syncScript) {
    & $syncScript -Root $repoRoot -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "Version sync failed with exit $LASTEXITCODE" }
}

# ---------- Publish ----------

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

# ---------- Inno Setup ----------

$iscc = Find-Iscc
if (-not $iscc) {
    if ($SkipInnoInstall) {
        Write-Host ""
        Write-Host "Inno Setup not found and -SkipInnoInstall is set." -ForegroundColor Yellow
        Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isdl.php and re-run." -ForegroundColor Yellow
        Write-Host "Publish output ready at: $publishDir" -ForegroundColor Yellow
        exit 2
    }
    try {
        $iscc = Install-InnoSetup
    } catch {
        Write-Host ""
        Write-Host ("Automatic Inno Setup install failed: {0}" -f $_) -ForegroundColor Red
        Write-Host "Install manually from https://jrsoftware.org/isdl.php and re-run." -ForegroundColor Yellow
        Write-Host "Publish output ready at: $publishDir" -ForegroundColor Yellow
        exit 2
    }
}

Write-Host "Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DClipsyVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }

# ---------- Portable zip ----------
# Same publish output as the installer, zipped for the GitHub release.
$zipPath = Join-Path $outputDir "Clipsy-$Version-win-x64.zip"
Write-Host "Creating portable archive $zipPath ..." -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Done. Output: $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir | Format-Table Name, Length, LastWriteTime
