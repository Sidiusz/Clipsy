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

function Update-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Transform
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    $content = Get-Content -LiteralPath $Path -Raw
    $updated = & $Transform $content
    if ($updated -ne $content) {
        Set-Content -LiteralPath $Path -Value $updated -Encoding utf8
    }
}

$manifest = Join-Path $Root 'Clipsy\app.manifest'
Update-File $manifest { param($text)
    $text = $text -replace '^<\?xml\s+version="[^"]+"\s+encoding="UTF-8"\s+standalone="yes"\?>', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    $text = $text -replace 'manifestVersion="[^"]+"', 'manifestVersion="1.0"'
    $text = $text -replace '(<assemblyIdentity[^>]*\sversion=")[^"]+("[^>]*>)', ('${1}' + $full + '${2}')
    return $text
}

$packageManifest = Join-Path $Root 'Clipsy\Package.appxmanifest'
Update-File $packageManifest { param($text)
    $text = $text -replace '(<Identity[^>]*\sVersion=")[^"]+("[^>]*>)', ('${1}' + $full + '${2}')
    return $text
}

$csproj = Join-Path $Root 'Clipsy\Clipsy.csproj'
Update-File $csproj { param($text)
    if ($text -match '<Version>.*?</Version>') {
        $text = $text -replace '<Version>.*?</Version>', ('<Version>' + $Version + '</Version>')
    } else {
        $marker = '    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>' + [Environment]::NewLine
        $insert = $marker + '    <Version>' + $Version + '</Version>' + [Environment]::NewLine + '    <AssemblyVersion>' + $full + '</AssemblyVersion>' + [Environment]::NewLine + '    <FileVersion>' + $full + '</FileVersion>' + [Environment]::NewLine + '    <InformationalVersion>' + $Version + '</InformationalVersion>' + [Environment]::NewLine
        if ($text.Contains($marker)) {
            $text = $text.Replace($marker, $insert)
        }
    }
    if ($text -match '<AssemblyVersion>.*?</AssemblyVersion>') {
        $text = $text -replace '<AssemblyVersion>.*?</AssemblyVersion>', ('<AssemblyVersion>' + $full + '</AssemblyVersion>')
    }
    if ($text -match '<FileVersion>.*?</FileVersion>') {
        $text = $text -replace '<FileVersion>.*?</FileVersion>', ('<FileVersion>' + $full + '</FileVersion>')
    }
    if ($text -match '<InformationalVersion>.*?</InformationalVersion>') {
        $text = $text -replace '<InformationalVersion>.*?</InformationalVersion>', ('<InformationalVersion>' + $Version + '</InformationalVersion>')
    }
    return $text
}

$settings = Join-Path $Root 'Clipsy\Views\Settings\SettingsWindow.xaml.cs'
Update-File $settings { param($text) $text -replace 'return v == null \? "[^"]+" : \$"\{v\.Major\}\.\{v\.Minor\}\.\{v\.Build\}";', 'return UpdateService.CurrentVersion();' }

$updateService = Join-Path $Root 'Clipsy\Services\UpdateService.cs'
Update-File $updateService { param($text) $text -replace 'Clipsy/\d+(?:\.\d+)* \(\+github\.com/Sidiusz/Clipsy\)', ('Clipsy/' + $Version + ' (+github.com/Sidiusz/Clipsy)') }

$installerScript = Join-Path $Root 'installer\Clipsy.iss'
Update-File $installerScript { param($text) $text -replace '(#define ClipsyVersion ")([^"]+)(")', ('${1}' + $Version + '${3}') }

$legacyVersionTxt = Join-Path $Root 'installer\version.txt'
Update-File $legacyVersionTxt { param($text) ($Version + [Environment]::NewLine) }

Write-Host ('Version synced to ' + $Version + ' / ' + $full)
exit 0
