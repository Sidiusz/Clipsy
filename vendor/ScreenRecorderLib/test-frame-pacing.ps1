$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$compiler = & $vswhere -latest -products '*' -find 'VC/Tools/MSVC/**/bin/Hostx64/x64/cl.exe' | Select-Object -Last 1
if (!$compiler) { throw 'The Microsoft C++ x64 compiler is required.' }
$toolRoot = Split-Path (Split-Path (Split-Path (Split-Path $compiler -Parent) -Parent) -Parent) -Parent
$sdkRoot = (Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots').KitsRoot10
$sdkVersion = Get-ChildItem (Join-Path $sdkRoot 'Lib') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'um/x64/kernel32.lib') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
$sdkInclude = Join-Path $sdkRoot "Include/$sdkVersion"
$saved = @{ INCLUDE = $env:INCLUDE; LIB = $env:LIB; PATH = $env:PATH }
try {
    $env:INCLUDE = (Join-Path $toolRoot 'include') + ';' + (Join-Path $sdkInclude 'ucrt') + ';' + (Join-Path $sdkInclude 'shared') + ';' + (Join-Path $sdkInclude 'um')
    $env:LIB = (Join-Path $toolRoot 'lib/x64') + ';' + (Join-Path $sdkRoot "Lib/$sdkVersion/ucrt/x64") + ';' + (Join-Path $sdkRoot "Lib/$sdkVersion/um/x64")
    $env:PATH = (Split-Path $compiler -Parent) + ';' + $env:PATH
    $buildDir = Join-Path $repoRoot '.tools/frame-pacing-tests'
    New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
    $exe = Join-Path $buildDir 'FramePacingTests.exe'
    & $compiler /nologo /EHsc /std:c++17 /O2 /MT (Join-Path $PSScriptRoot 'FramePacingTests.cpp') "/Fo$(Join-Path $buildDir 'FramePacingTests.obj')" "/Fe$exe"
    if ($LASTEXITCODE -ne 0) { throw 'Frame pacing test compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "Frame pacing tests failed: $LASTEXITCODE" }
} finally {
    $env:INCLUDE = $saved.INCLUDE
    $env:LIB = $saved.LIB
    $env:PATH = $saved.PATH
}
