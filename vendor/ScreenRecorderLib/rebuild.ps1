[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$OutputDirectory,
    [switch]$SkipNativeBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$SourceDirectory) { $SourceDirectory = Join-Path $repoRoot '.tools/ScreenRecorderLib' }
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$commit = '39ad1e2f1750fa06669b73743dbbaa25371dec21'
if (!(Test-Path (Join-Path $SourceDirectory '.git'))) {
    & git clone --depth 1 --branch v6.6.0 https://github.com/sskodje/ScreenRecorderLib.git $SourceDirectory
    if ($LASTEXITCODE -ne 0) { throw 'ScreenRecorderLib checkout failed.' }
}
$actual = & git -C $SourceDirectory rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $commit) { throw "Expected upstream commit $commit." }
$patch = Join-Path $PSScriptRoot 'capture.patch'
$checkPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'SilentlyContinue'
    & git -C $SourceDirectory apply --check $patch 2>$null
    $needsPatch = $LASTEXITCODE -eq 0
    if (!$needsPatch) {
        & git -C $SourceDirectory apply --reverse --check $patch 2>$null
        $alreadyPatched = $LASTEXITCODE -eq 0
    }
} finally { $ErrorActionPreference = $checkPreference }
if ($needsPatch) {
    & git -C $SourceDirectory apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Applying recorder patches failed.' }
} else {
    if (!$alreadyPatched) { throw 'The source does not match the recorder patches.' }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FramePacing.h') -Destination (Join-Path $SourceDirectory 'ScreenRecorderLibNative/FramePacing.h') -Force
& (Join-Path $PSScriptRoot 'test-frame-pacing.ps1')
if (!$SkipNativeBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $msbuild = & $vswhere -latest -products '*' -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1
    if (!$msbuild) { throw 'Visual Studio MSBuild is required.' }
    $sdkRoot = (Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots').KitsRoot10
    $sdkVersion = Get-ChildItem (Join-Path $sdkRoot 'Lib') -Directory | Where-Object { [version]$_.Name -ge [version]'10.0.26100.0' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    if (!$sdkVersion) { throw 'Windows SDK 10.0.26100 or newer is required.' }
    foreach ($platform in @('x64', 'ARM64')) {
        $previousLinkOptions = $env:_LINK_
        try {
            $armSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/NETFXSDK/4.8.1/Lib/um/arm64'
            if ($platform -eq 'ARM64' -and (Test-Path $armSdk)) {
                $env:_LINK_ = $previousLinkOptions + ' /LIBPATH:"' + $armSdk + '"'
            }
            & $msbuild (Join-Path $SourceDirectory 'ScreenRecorderLib/ScreenRecorderLib.vcxproj') /m /nologo /v:minimal /p:Configuration=Release "/p:Platform=$platform" "/p:PlatformTarget=$platform" "/p:WindowsTargetPlatformVersion=$sdkVersion"
            if ($LASTEXITCODE -ne 0) { throw "Native $platform build failed." }
        } finally { $env:_LINK_ = $previousLinkOptions }
    }
}
foreach ($platform in @('x64', 'ARM64')) {
    if (!(Test-Path (Join-Path $SourceDirectory "ScreenRecorderLib/bin/$platform/Release/ScreenRecorderLib.dll"))) {
        throw "Missing native $platform build."
    }
}
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot 'vendor/packages' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet pack (Join-Path $PSScriptRoot 'Package.csproj') -c Release -o $output "/p:NativeRecorderSourceDirectory=$SourceDirectory" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Recorder package creation failed.' }
Get-FileHash (Join-Path $output 'ScreenRecorderLib.6.6.0-clipsy.2.nupkg') -Algorithm SHA256
