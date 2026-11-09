param([Parameter(Mandatory)] [string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install the Visual Studio C++ x64 build tools and Windows SDK.' }
$setup = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$source = Join-Path $PSScriptRoot '../native/windows_ocr_worker.cpp'
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$object = (New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot '../obj/native')).FullName
$build = 'call "{0}" >nul && cl /nologo /std:c++20 /utf-8 /O2 /MT /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fo:"{2}\windows_ocr_worker.obj" /Fe:"{3}\vision-windows-ocr.exe" /link /OPT:REF /OPT:ICF' -f $setup,$source,$object,$output
cmd.exe /d /c $build
