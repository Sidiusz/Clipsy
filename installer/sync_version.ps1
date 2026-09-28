<#
.SYNOPSIS
    Sync version values across Clipsy project files.

.PARAMETER Root
    Repository root directory.

.PARAMETER Version
    Semver-like version in major.minor.patch format.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$full = "$Version.0"

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $Root 'version'), $Version + [Environment]::NewLine, $utf8NoBom)

function Update-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Transform
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    $content = [System.IO.File]::ReadAllText($Path)
    $updated = & $Transform $content
    if ($updated -ne $content) {
        # No BOM: Windows PowerShell's -Encoding utf8 would add one.
        [System.IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
    }
}

$manifest = Join-Path $Root 'Clipsy\app.manifest'
Update-File $manifest { param($text)
    $text -replace '(<assemblyIdentity[^>]*\sversion=")[^"]+("[^>]*>)', ('${1}' + $full + '${2}')
}

$csproj = Join-Path $Root 'Clipsy\Clipsy.csproj'
Update-File $csproj { param($text)
    $text = $text -replace '<Version>.*?</Version>', ('<Version>' + $Version + '</Version>')
    $text = $text -replace '<AssemblyVersion>.*?</AssemblyVersion>', ('<AssemblyVersion>' + $full + '</AssemblyVersion>')
    $text = $text -replace '<FileVersion>.*?</FileVersion>', ('<FileVersion>' + $full + '</FileVersion>')
    $text -replace '<InformationalVersion>.*?</InformationalVersion>', ('<InformationalVersion>' + $Version + '</InformationalVersion>')
}

$installerScript = Join-Path $Root 'installer\Clipsy.iss'
Update-File $installerScript { param($text) $text -replace '(#define ClipsyVersion ")([^"]+)(")', ('${1}' + $Version + '${3}') }

Write-Host ('Version synced to ' + $Version + ' / ' + $full)
exit 0
