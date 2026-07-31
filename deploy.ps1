<#
.SYNOPSIS
Build + package + register the Forestry Toolkit add-in without full Visual Studio.

Esri's own packaging target (PackageArcGISContents, from the Esri.ArcGISPro.Extensions30
NuGet package) uses an inline CodeTaskFactory task, which only classic .NET-Framework
MSBuild (bundled with the Visual Studio IDE) supports - `dotnet build` can't run it.
This script does the same job by hand: `dotnet build` compiles fine on its own, then we
zip the output + Config.daml into a .esriAddinX and call RegisterAddIn.exe ourselves.

If you have the full Visual Studio IDE with the ArcGIS Pro SDK, you don't need this -
just build the solution there and Esri's own target handles packaging/registration.
#>
param(
    [string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"

$proj = Join-Path $PSScriptRoot "src\TreeCounterAddin"
dotnet build (Join-Path $proj "TreeCounterAddin.csproj") -c $Configuration -p:Platform=x64 -p:SkipEsriPackaging=true
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$outDir = Join-Path $proj "bin\x64\$Configuration\net10.0-windows"
$zipPath = Join-Path $outDir "TreeCounterAddin.esriAddinX"
if (Test-Path $zipPath) { Remove-Item $zipPath }

$staging = Join-Path $env:TEMP "TreeCounterAddin_addin_staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

# CRITICAL: binaries must live under an "Install\" subfolder inside the zip - ArcGIS
# Pro's own unpacker (Script.ExtractFolder, decompiled from ArcGIS.Desktop.Framework.dll)
# only extracts zip entries whose first path segment is literally "Install"; anything at
# the zip root (besides Config.daml) is silently skipped, and the extract-to-AssemblyCache
# step is never treated as fatal (UnpackAddIn() still returns true), so nothing else
# indicates a problem - Config.daml still parses and the ribbon tab/button render
# normally, but TreeCounterAddin.dll never reaches the AssemblyCache and Factory.GetType()
# can never find "TreeCounterAddin.TreeCounterModule" - no exception, no crash, and
# clicking the button just does nothing. Config.daml itself must stay at the zip root.
$installDir = Join-Path $staging "Install"
New-Item -ItemType Directory -Path $installDir | Out-Null
Copy-Item (Join-Path $outDir "*") -Destination $installDir -Recurse
Copy-Item (Join-Path $proj "Config.daml") -Destination $staging

# backend/ ships inside the package (as a sibling of the DLL under Install\) rather than
# being referenced by repo-relative path - PythonBackendService.cs resolves it via
# AppDomain.CurrentDomain.BaseDirectory, which after install points into
# %LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{guid}\, not this repo checkout. Runtime
# files only - test_*.py and __pycache__ don't need to travel with the package.
$backendDest = Join-Path $installDir "backend"
New-Item -ItemType Directory -Path $backendDest | Out-Null
Get-ChildItem (Join-Path $PSScriptRoot "backend") -File |
    Where-Object { $_.Name -notlike "test_*" } |
    Copy-Item -Destination $backendDest

# Templates/ (e.g. the Excel cruising-data template) ships the same way as backend/ -
# resolved at runtime via AppDomain.CurrentDomain.BaseDirectory, not a repo-relative path.
Copy-Item (Join-Path $proj "Templates") -Destination $installDir -Recurse

# tessdata/ (Tesseract OCR language data for Photo Coordinate OCR) - same reasoning as
# Templates/ above. The Tesseract.dll + native x64/x86 runtime DLLs themselves are already
# covered by the "$outDir\*" copy above (NuGet places them straight in the build output).
Copy-Item (Join-Path $proj "tessdata") -Destination $installDir -Recurse

Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath
Remove-Item $staging -Recurse -Force
Write-Host "Packaged: $zipPath"

$registerExe = "C:\Program Files\ArcGIS\Pro\bin\RegisterAddIn.exe"
if (Test-Path $registerExe) {
    # No /s (silent): this add-in isn't digitally signed, and RegisterAddIn.exe's
    # silent mode copies the file into place WITHOUT actually completing installation
    # for unsigned add-ins - ArcGIS Pro's DLL never gets loaded even though the file
    # is sitting in the right folder, with zero error anywhere. Running interactively
    # pops the "Esri ArcGIS Add-In Installation Utility" dialog - click "Install Add-In".
    & $registerExe $zipPath
    Write-Host "A confirmation dialog should have opened - click 'Install Add-In' there."
    Write-Host "Then restart ArcGIS Pro to see the 'Forestry Toolkit' tab."
} else {
    Write-Warning "RegisterAddIn.exe not found at $registerExe - install the .esriAddinX manually (double-click it)."
}
