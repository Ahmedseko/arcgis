# Forestry Toolkit (ArcGIS Pro Add-in) — Notes for Claude

See [README.md](README.md) for architecture/build/deploy instructions. This file is
the hard-won "don't repeat these mistakes" list.

## Critical: .esriAddinX package layout

The zip must have **binaries under an `Install/` subfolder**, with `Config.daml` at
the zip **root**:

```text
TreeCounterAddin.esriAddinX (zip)
├── Config.daml              <- root
└── Install/
    ├── TreeCounterAddin.dll
    ├── TreeCounterAddin.deps.json
    ├── TreeCounterAddin.runtimeconfig.json
    └── ... (all other output DLLs)
```

Confirmed by decompiling `ArcGIS.Desktop.Framework.dll`
(`Registry.Script.ExtractFolder`, called with `sourceFolder = "Install"`): it only
extracts zip entries whose **first path segment is literally `"Install"`**; entries at
the zip root are silently skipped. Getting this wrong is uniquely nasty to debug:

- `UnpackAddIn()` still returns `true` even when extraction found nothing (the
  `if (UnzipToAssemblyCache(...))` has no `else`), so nothing looks like it failed.
- Config.daml still parses fine (it's a root-level entry) and the ribbon tab/button
  **render correctly** with the right caption/tooltip - completely normal-looking.
- The actual DLL never reaches `%LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{guid}\`,
  so `Factory.GetType()` can't find the module class, logs a `TypeNotFound` error to
  ArcGIS Pro's internal EventLog (not written to disk unless diagnostic logging is
  explicitly enabled - it isn't, by default), and returns null.
- Result: clicking the button does **precisely nothing**. No exception, no crash, no
  dialog, no visible log anywhere. `deploy.ps1` builds this correctly - if you ever
  hand-roll packaging again, get this right first.

**How to verify an add-in actually loaded** (skip everything else and check this
first if the UI seems unresponsive):

```powershell
(Get-Process ArcGISPro).Modules | Where-Object { $_.ModuleName -like "*YourAddin*" }
```

Empty result = the DLL never made it into the process, regardless of what the ribbon
looks like or what the install dialog said.

## Other things that bit us

- **`Config.daml` schema validation**: `tab`, `group`, and `button` elements all
  require a `keytip` attribute (`CT_Tab`, `CT_Group`, `CT_Command` in the real
  schema). Missing it doesn't error visibly - the ribbon still renders - but don't
  trust that. Validate for real before assuming DAML is fine:

  ```powershell
  $xsdPath = "C:\Program Files\ArcGIS\Pro\bin\ArcGIS.Desktop.Framework.xsd"
  $schemas = New-Object System.Xml.Schema.XmlSchemaSet
  $schemas.Add("http://schemas.esri.com/DADF/Registry", $xsdPath) | Out-Null
  $settings = New-Object System.Xml.XmlReaderSettings
  $settings.ValidationType = [System.Xml.ValidationType]::Schema
  $settings.Schemas = $schemas
  $settings.add_ValidationEventHandler({ param($s,$e) Write-Host "$($e.Severity): $($e.Message)" })
  $reader = [System.Xml.XmlReader]::Create("src\TreeCounterAddin\Config.daml", $settings)
  try { while ($reader.Read()) {} } finally { $reader.Close() }
  ```

  Note: the schema referenced by `xsi:schemaLocation` in most sample Config.daml files
  (`http://Server/schemas/Desktop/CondensedTypedConfiguration.xsd`) is a dummy,
  non-fetchable placeholder. The real one is
  `C:\Program Files\ArcGIS\Pro\bin\ArcGIS.Desktop.Framework.xsd` - not anything under
  `Pro\Resources\XmlSchema\` (those are for a different, older/native DADF
  extensibility mechanism, not the public Add-In SDK).

- **Unsigned add-ins need the interactive install dialog, every time content
  changes.** Double-clicking the `.esriAddinX` (or `RegisterAddIn.exe <path>` without
  `/s`) pops "Esri ArcGIS Add-In Installation Utility" - click **Install Add-In**.
  This is required, not just cosmetic. `deploy.ps1` runs `RegisterAddIn.exe` without
  `/s` on purpose so this dialog always appears - don't add `/s` back.

- **`.csproj` pattern**: this project references ArcGIS Pro's own installed DLLs
  directly via `<Reference HintPath="C:\Program Files\ArcGIS\Pro\bin\...">` with
  `CopyLocal=false`, matching Esri's own sample
  (`Framework/DockpaneSimple/DockpaneSimple.csproj` in
  `Esri/arcgis-pro-sdk-community-samples`) - **not** a `PackageReference` to
  `Esri.ArcGISPro.Extensions30`. That NuGet package ships ref-only (compile-time)
  assemblies with no `lib/` folder, meant to pair with `EnableDynamicLoading`'s
  isolated plugin `AssemblyLoadContext` - but ArcGIS Pro's add-in host doesn't use
  that pattern. When in doubt about project structure, diff against a real Esri
  sample rather than reconstructing from memory.

- **`dotnet build` can't package.** Esri's `PackageArcGISContents` MSBuild target
  (whether from the NuGet package or `C:\Program Files\ArcGIS\Pro\bin\Esri.ProApp.SDK.Desktop.targets`)
  uses an inline `CodeTaskFactory` task, unsupported by .NET Core's MSBuild
  (`MSB4801`). Only classic .NET Framework MSBuild (bundled with the full Visual
  Studio IDE) can run it. `deploy.ps1` works around this with
  `-p:SkipEsriPackaging=true` (a `Directory.Build.targets` override, see
  [src/TreeCounterAddin/Directory.Build.targets](src/TreeCounterAddin/Directory.Build.targets))
  plus hand-rolled zip + `RegisterAddIn.exe` - only relevant if you don't have full
  Visual Studio available.

- **Output path has a TFM subfolder**: `bin\x64\Debug\net10.0-windows\`, not
  `bin\x64\Debug\`. `deploy.ps1`'s `$outDir` already accounts for this - if you change
  `TargetFramework` in the `.csproj`, update `$outDir` to match or you'll silently
  package a stale build.
