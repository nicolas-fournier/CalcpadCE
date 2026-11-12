<#
  Builds the CalcpadCE (Avalonia) Windows installer end to end:
    1. publishes a self-contained win-x64 build
    2. signs the app exe with a local self-signed certificate (sign.ps1)
    3. compiles the Inno Setup installer
    4. signs the installer

  Requirements: .NET SDK, Inno Setup 6 (ISCC.exe), Windows SDK (signtool).
  Usage:  pwsh .\build-installer.ps1   (optionally -NoSign)
#>
param([switch]$NoSign)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$proj = Split-Path $here -Parent                 # ...\Calcpad.Avalonia
$repo = Split-Path $proj -Parent                 # repo root
$csproj = Join-Path $proj 'Calcpad.Avalonia.csproj'
$publish = Join-Path $proj 'bin\Release\net10.0\win-x64\publish'

# Version from Directory.Build.props
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$ver = @($props.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $ver) { $ver = '7.6.6-preview' }
Write-Host "=== CalcpadCE $ver ===" -ForegroundColor Cyan

Write-Host "Publishing self-contained win-x64..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$appExe = Join-Path $publish 'CalcpadAvalonia.exe'
if (-not $NoSign) { & (Join-Path $here 'sign.ps1') -File $appExe }

Write-Host "Compiling the Inno Setup installer..." -ForegroundColor Cyan
$iscc = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found." }
& $iscc "/DAppVer=$ver" (Join-Path $here 'CalcpadCE.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$setup = Join-Path $here "output\CalcpadCE-$ver-setup.exe"
if (-not $NoSign) { & (Join-Path $here 'sign.ps1') -File $setup }

Write-Host "`nDone. Installer: $setup" -ForegroundColor Green
