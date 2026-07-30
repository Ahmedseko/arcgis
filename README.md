# Forestry Toolkit (ArcGIS Pro Add-in)

Tree detection/counting ported from the QGIS plugin `qgis_plugin/tree_counter`
(LandTree Analyzer) to ArcGIS Pro, using the ArcGIS Pro SDK for .NET, plus a
fishnet grid generator (clipped to a planning polygon) for timber cruising.

## Architecture

Hybrid: native UI in .NET, detection logic stays in Python (run through
ArcGIS Pro's own bundled Python). Why: the detection pipeline (ExG + Gaussian
matched filter + YOLOv8n ONNX for oil palm) is numpy/scipy-heavy and has been
through a lot of tuning/bugfixing (see `qgis_plugin/AGENTS.md`) - rewriting it
by hand in C# risks reintroducing the same bugs without the ground-truth
harness that already exists there.

```text
src/TreeCounterAddin/     .NET add-in: ribbon button + WPF DockPane (UI)
backend/                  Python port of qgis_plugin/tree_counter, invoked
                          as a subprocess by PythonBackendService.cs
```

- `TreeCounterModule.cs` - module registration.
- `TreeCounterDockpaneViewModel.cs` / `TreeCounterDockpaneView.xaml` - panel:
  pick the active raster layer, profile (Natural Forest / Oil Palm
  Plantation), advanced parameters (sigma, ExG threshold, min smooth), run
  button, status. On success, the result is loaded as a point layer onto the
  active map with simple symbology (red = oil palm, green = forest).
- `PythonBackendService.cs` - shells out to
  `arcgispro-py3\python.exe backend/detect.py`, reads the JSON result summary.
- `backend/raster_io.py` - reads an RGB(+alpha) raster via
  `arcpy.RasterToNumPyArray` plus geotransform info, shared by `detector.py`
  and `yolo_detector.py`.
- `backend/detector.py` - port of `detect_trees`/`compare_detections` from
  `qgis_plugin/tree_counter/detector.py` (ExG + Gaussian matched filter, used
  for the Natural Forest profile, and as the Oil Palm fallback when the YOLO
  model isn't available).
- `backend/yolo_detector.py` - port of `detect_trees_yolo_primary` from
  `qgis_plugin/tree_counter/yolo_detector.py` (local YOLOv8n ONNX as the
  primary detector for Oil Palm Plantation - F1 90.4% vs 72.7% for the older
  hybrid ExG+YOLO path, see `qgis_plugin/AGENTS.md` 2026-07-13; the older
  hybrid path is intentionally not ported since it's superseded).
- `backend/detect.py` - CLI entry point: picks the algorithm per profile,
  writes results to a feature class (`arcpy.da.InsertCursor`) + a JSON summary.
- `backend/sawit_detector.onnx` - YOLOv8n model, copied from
  `qgis_plugin/tree_counter/sawit_detector.onnx`.

**Not ported** (out of scope for "count trees"): `detect_land_clearing`
(cleared-land detection), `compute_heterogeneity_raster` (contrast preview),
Gemini Vision AI validation (`validator.py`) - these QGIS plugin features are
separate from the tree-counting flow and weren't requested.

## Build & Deploy

Requires **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`) - the
ArcGIS Pro 3.7 SDK (`Esri.ArcGISPro.Extensions30`) targets
`net10.0-windows7.0`.

**If you have the full Visual Studio IDE** (not just Build Tools) with the
.NET desktop workload: open `TreeCounterPro.sln`, hit Build. Esri's
`PackageArcGISContents` target automatically packages the `.esriAddinX` and
registers it with ArcGIS Pro via `RegisterAddIn.exe`.

**If you only have the `dotnet` CLI / VS Build Tools** (like this
environment): Esri's packaging target uses an inline `CodeTaskFactory` task,
which the .NET Core flavor of MSBuild doesn't support (`dotnet build` fails
at that step even though the C#/XAML compilation itself succeeds). Use the
`deploy.ps1` script instead - it builds via `dotnet build` then replicates
Esri's packaging step by hand (zips into a `.esriAddinX` + calls
`RegisterAddIn.exe`):

```powershell
.\deploy.ps1
```

This pops the **"Esri ArcGIS Add-In Installation Utility"** dialog (the add-in isn't
digitally signed) - click **Install Add-In**. This step is not optional: skipping it
(or using `RegisterAddIn.exe /s`) leaves the file sitting in the AddIns folder but
ArcGIS Pro never actually loads the DLL - no error anywhere, the ribbon tab/button
still render (pure DAML), but nothing they do has any effect. Confirmed by checking
`Get-Process ArcGISPro | Select Modules` - the DLL was entirely absent from the
process until this dialog was clicked once.

Then restart ArcGIS Pro and check the **Forestry Toolkit** ribbon tab.

Also note: every attribute the schema marks `required` actually has to be there -
`Config.daml`'s `tab`/`group`/`button` elements all require a `keytip` attribute,
which isn't obvious from most samples using short values like `T1`/`G1`/`B1` and is
easy to miss by hand. Validate against the real schema before chasing runtime ghosts:

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

## Python backend

Runs under Pro's bundled conda env (`arcgispro-py3`) so `arcpy` is available.
For the Oil Palm Plantation profile (YOLO), install `onnxruntime` + `pillow`
into that same env (already installed in this environment):

```powershell
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" -m pip install onnxruntime pillow
```

Tests (run with Pro's bundled python, both already passing in this
environment):

```powershell
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" backend\test_detect.py         # CLI smoke test (argparse, exit codes)
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" backend\test_pipeline_e2e.py    # e2e: synthetic raster -> detect_trees -> detected positions
```

## Status

Done: algorithm port (ExG for forest + YOLO for oil palm), feature class
output + auto-load onto the map with symbology, advanced parameter controls
in the DockPane.

**Not done:**

- A "Compare Changes" feature (diff two detection runs over time) from the
  QGIS plugin - `compare_detections()` is already ported in
  `backend/detector.py` (generic function, no extra porting needed), but
  there's no UI for it in the DockPane yet. Add it if actually needed.
- Not yet tested against a real drone orthophoto (only a synthetic raster +
  smoke test so far) - run it from the ArcGIS Pro UI against a real TIF for
  visual validation.
