# Forestry Toolkit (ArcGIS Pro Add-in)

An ArcGIS Pro dockpane + ribbon tab covering the common steps of a timber
cruising/forestry workflow: tree detection from drone orthophotos (ported from
the QGIS plugin `qgis_plugin/tree_counter`, LandTree Analyzer), fishnet grid
generation, field-data import (Excel, geotagged photos, photo-watermark OCR),
sliver-polygon/biomass/slope/riparian-buffer analysis, a cruising summary
report, GPX export, and a custom photo popup tool.

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
- `backend/land_clearing.py` - port of `detect_land_clearing` (same ExG math,
  inverted: low vegetation greenness = bare/cleared ground). Writes a 0/1
  mask *raster* instead of the QGIS original's GDAL/OGR polygon WKT output -
  this add-in doesn't depend on GDAL/OGR (see `raster_io.py`'s own comment),
  so vectorization uses `conversion.RasterToPolygon` instead (in
  `detect_clearing.py`, with `SIMPLIFY` rather than `NO_SIMPLIFY` - the
  latter traces every raster cell's exact edge, which looked like a jagged
  "staircase" rather than a human-digitized boundary). Chunked/blocked like
  `detector.detect_trees`, unlike the QGIS original (which reads the whole
  raster at once) - except the opening/closing smoothing pass (denoise +
  generalize the boundary, also added after a real result looked "too busy"),
  which runs once on the whole assembled mask rather than per-block, since a
  per-block pass can't smooth across a block boundary anyway. Also has an
  unproven `method="obia"` prototype (`build_cleared_mask_obia`, `--method obia`
  on the CLI): segments each block into superpixels with `skimage`'s SLIC first
  and classifies whole segments instead of pixels, aiming for the same smooth-
  boundary result natively instead of via the opening/closing pass - not yet
  validated against a real orthophoto, so `method="exg"` stays the default.
- `backend/detect_clearing.py` - CLI entry point for land clearing detection:
  runs the mask scan, vectorizes it, filters by minimum area, optionally
  erases an "already cleared" exclude area, writes the result + JSON summary.
- `backend/compare_detections.py` - CLI entry point for the Compare Changes
  feature: reads two Tree Detection point feature classes, runs
  `detector.compare_detections()`'s greedy nearest-neighbor match, writes the
  unmatched points back out as "Lost"/"New" point feature classes.

**Not ported** (out of scope for "count trees"): `compute_heterogeneity_raster`
(contrast preview) - a QGIS plugin helper for manually picking where to draw
an exclude mask before running `detect_land_clearing`, separate from the
detection itself and not requested. (`detect_land_clearing` and AI vision
validation *were* both ported, unlike an older note that used to say
otherwise - see `backend/land_clearing.py`, `backend/validator.py`, and the
Analyze/Settings tabs.)

## Features & Usage

Open the panel via the **Forestry Toolkit** ribbon tab -> **Forestry Toolkit**
button (large, left). It has 4 tabs: **Prepare**, **Field Data**, **Analyze**,
**Settings**. Every long-running feature shows its progress/result in a status
line under its own section; click **Refresh** (top of the panel) any time a
layer you just added doesn't show up in a dropdown yet.

**Cancel**, where available, stops the operation after its *current* step
finishes (e.g. mid-way through a chain of GP tool calls) - it's cooperative
cancellation, not an instant kill, so there can be a short delay before the
status line shows "Cancelled."

### Prepare tab

- **Fishnet Generator** - pick a planning polygon layer, set cell width/height
  (map units), click **Create Fishnet**. Generates a grid over the polygon's
  extent and clips it to the polygon's actual shape, with a `Cell_ID` field
  for referencing cells in the field. *Cancel: yes.*
- **Export to GPS (GPX)** - pick any point/line/polygon layer, click **Export
  to GPX...** and choose where to save. Polygon layers get their boundary
  turned into a line first (GPX has no "filled area" concept). Opens directly
  in Garmin BaseCamp/Garmin Connect or any GPS device that reads GPX. *Cancel:
  no (each export is a couple of quick GP calls; not worth the added UI).*

### Field Data tab

- **Import Timber Cruising Excel** - click **Download Template...** first if
  you don't have the sheet format yet (ships as
  `Templates/TreeCruisingTemplate.xlsx`); it needs a `TREE DATA` sheet
  (species, diameter, height, volume, X/Y). Pick the matching coordinate
  system (Indonesian UTM zones by name, or "Other" to enter a WKID by hand),
  click **Import Excel...** and choose the file. *Cancel: no (each import is a
  couple of quick GP calls).*
- **Geotagged Field Photos** - click **Import Photos...**, pick JPEGs that
  already have GPS EXIF data (most phone/GPS-camera photos do by default).
  Creates one point per photo with the photo attached, so clicking the point
  in ArcGIS Pro's normal pop-up shows/enlarges it. *Cancel: no (reads EXIF
  tags only, no network/heavy processing).*
- **Photo Coordinate OCR (no EXIF GPS)** - for photos where the coordinates
  are burned into the image itself (e.g. a "GPS Map Camera"-style watermark)
  but EXIF GPS is missing/blank. Pick the watermark format (**UTM Grid** or
  **Latitude/Longitude**) and, for UTM, a default zone/hemisphere used only if
  a photo's own zone letter can't be read. Click **Scan Photos...**, pick the
  JPEGs - this runs fully offline (bundled Tesseract OCR, nothing leaves the
  machine). A review window then shows every photo's detected X/Y so each one
  can be checked/corrected/excluded before anything is created; points are
  only written after clicking **Create Points** in that window. *Cancel: no
  (OCR runs before the review window opens - close the review window without
  clicking Create Points to back out instead).*

### Analyze tab

- **Tree Detection** - pick a raster layer and a profile (**Natural Forest**
  or **Oil Palm Plantation**; advanced sigma/ExG-threshold/min-smooth
  parameters are on the **Settings** tab), click **Detect Trees**. Runs the
  ported ExG/YOLO pipeline as a background subprocess - safe to switch to a
  different map while it runs. Result loads as a point layer (green =
  forest, red = oil palm). The **"Exclude cleared/bare ground"** checkbox
  (on by default) drops candidates that land on bare soil/roads/open
  ground - added after visual validation against a real orthophoto showed
  false positives there; it roughly doubles run time (a second full raster
  scan), so turn it off if you've already confirmed it's not needed for a
  given site. *Cancel: yes.*
- **Land Clearing Detection** - the opposite of Tree Detection: flags
  bare/cleared ground (low vegetation greenness) instead of tree crowns,
  ported from the same QGIS plugin's `detect_land_clearing`. Pick a raster
  layer, optionally an "exclude area" polygon layer (e.g. area already known
  to be cleared/harvested, so results only show *new* clearings), a minimum
  area in hectares, click **Detect Clearing**. Runs as a background Python
  subprocess (same as Tree Detection), safe against large orthophotos - the
  scan itself is chunked/blocked so memory stays bounded regardless of raster
  size. Ponds/rivers are excluded automatically (they have no vegetation
  greenness either, but read dark/blue rather than bright/reddish like bare
  soil - see `WATER_BRIGHTNESS_MAX` in `backend/land_clearing.py`, added
  2026-08-03 after a real result flagged two ponds as "cleared"). *Cancel:
  yes.*
- **Road/Trail Extraction** - pulls road/trail centerlines out of the same
  bare-ground signal Land Clearing Detection uses (roads read as "cleared"
  too), skeletonized down to a single line (`skimage.morphology.skeletonize`)
  instead of a filled area, then vectorized with arcpy's own
  `conversion.RasterToPolyline` GP tool - see `backend/road_extraction.py`.
  Not a port of
  [microsoft/RoadDetections](https://github.com/microsoft/RoadDetections)'s
  own approach (its segmentation model is Keras/Python 3.6 trained on
  100cm/px satellite imagery - the wrong resolution regime for our ~5cm/px
  drone orthophotos); its C# geometry-generation module's job (thinning +
  graph construction + graph optimization to turn a mask into a line
  network) is already covered here by skimage + arcpy's own tool, no new
  algorithm needed. Pick a raster layer, a minimum dangle length (drops
  short stub segments skeletonize leaves at noisy mask edges), click
  **Extract Roads**. *Cancel: yes.*
- **Compare Changes** - change detection between two Tree Detection runs of
  the same site over time. Pick the old and new detection point layers and a
  match distance (meters - covers re-run centroid jitter, not just exact
  pixel repeats), click **Compare**. Wraps `detector.compare_detections()`'s
  greedy nearest-neighbor matching (already ported, just needed a UI - see
  `backend/compare_detections.py`); old points with no match load as a red
  "Lost" layer (likely felled), new points with no match load as a green
  "New" layer (likely regrowth/previously missed). *Cancel: no (a single
  quick nearest-neighbor match, no GP tool chain).*
- **Sliver Polygon Detection** - pick a polygon layer, click **Detect
  Slivers**. Auto-calibrates against that layer's own median part size/shape
  (no fixed threshold to tune) and selects the flagged slivers on the map.
  *Cancel: no (a single in-memory scan, no GP tool calls to chain).*
- **Biomass & Carbon Estimation** - pick a point layer that has a `Volume`
  field (from an Excel import), click **Estimate**. Uses the wood
  density/BEF/root-shoot-ratio/carbon-fraction constants on the **Settings**
  tab (generic tropical-forest defaults - edit them for your species mix/
  region) and adds per-tree `Biomass_kg`/`Carbon_kg` fields. *Cancel: no (a
  couple of quick GP calls).*
- **Slope from DEM** - pick a single-band elevation raster, click **Compute
  Slope**. Requires a licensed Spatial Analyst extension. Output is
  classified into forestry accessibility bands (green <=15% easy, yellow
  15-25% moderate, orange 25-40% difficult, red >40% restricted) instead of a
  plain grayscale stretch. *Cancel: yes.*
- **Riparian Buffer Check** - pick a river/stream layer and a planning
  polygon layer, set the buffer distance (meters - no fixed legal number is
  assumed, since this varies by regulation/river class), click **Check
  Buffer**. Buffers the river and intersects it with the plan; if nothing
  overlaps, no extra layer is added. *Cancel: yes.*
- **Cruising Summary Report** - pick a point layer with both `Volume` and
  `Species` fields, click **Generate Report...** and choose where to save.
  Produces a species x volume summary spreadsheet (sum + count per species).
  *Cancel: yes.*

### Settings tab

- **Advanced Detection Parameters** - sigma / ExG threshold / min-smooth for
  Tree Detection; auto-filled per profile, editable per run.
- **AI Vision Validation** - optional Gemini/OpenAI/Claude API key + model, to
  validate Tree Detection results. Keys are saved encrypted (DPAPI) per
  provider, so switching providers doesn't lose the other's key. **Test
  Key** checks it works before running a full detection.
- Fishnet cell size, cruising coordinate system, and biomass constants are
  saved automatically as they're changed (plain JSON, no dialog needed) and
  restored next time ArcGIS Pro opens.

### Photo Popup (ribbon tool, not in the panel)

A custom map tool for viewing a point's attached photo inline, since ArcGIS
Pro's native pop-up only shows a hierarchical field list, not the photo
itself. Click **Photo Popup** in the ribbon's **Analysis** group (next to
**Detect Trees**), then click a point that has a photo attachment (from
Geotagged Field Photos or Photo Coordinate OCR) - a floating card with the
photo appears, anchored to that point (it follows if you pan/zoom, and
disappears if the point scrolls out of view). Left-click is reserved for this
while the tool is active, so:

- Pan with **right-click-drag**, zoom with the **scroll wheel** (both work
  normally).
- To go back to normal left-click-drag panning/selection, press **Esc** or
  switch to the **Explore** tool (Map tab -> Navigate group) - ribbon tool
  buttons aren't on/off toggles, they're "which tool is active right now."

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

Tests (run with Pro's bundled python, all already passing in this
environment):

```powershell
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" backend\test_detect.py             # CLI smoke test (argparse, exit codes)
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" backend\test_pipeline_e2e.py        # e2e: synthetic raster -> detect_trees -> detected positions
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" backend\test_land_clearing_e2e.py   # e2e: synthetic raster -> detect_land_clearing -> mask matches planted patch
& "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" backend\test_compare_detections_e2e.py  # e2e: feature classes -> compare_detections -> lost/new feature classes
```

## Status (Tree Detection / Python backend)

Done: algorithm port (ExG for forest + YOLO for oil palm), feature class
output + auto-load onto the map with symbology, advanced parameter controls
in the DockPane.

**Not done:**

- Backend pipeline confirmed working against a real large drone orthophoto
  (54150x36052 px, 4-band, ~6.3 GB, "Natural Forest" profile) via direct CLI
  run of `detect.py` - completed without error/memory issues, 7,369 trees
  over ~660 ha. `detect_clearing.py` confirmed working against the same
  orthophoto too - 389 cleared/bare-ground polygons, ~30.7 ha total.
- Visual validation against that same real orthophoto (2026-07-31, from the
  ArcGIS Pro UI) found real accuracy issues, not just "does it run without
  crashing": (1) false-positive points on bare/cleared ground - partially
  addressed by the "Exclude cleared/bare ground" option + a stricter
  `min_density` (see `detector.py`/`detect.py`; reduced 7,369 -> 7,294
  points, 36 explicitly filtered as on cleared ground vs. only 3 before the
  threshold fix), (2) one real tree crown sometimes split into multiple
  points (Sigma likely too small for that crown's actual size), (3) many
  real crowns missed entirely, (4) false positives on visibly
  blurred/stitching-artifact regions of the orthomosaic. (2)-(3) are still
  open - would need a round of Sigma/threshold recalibration against real
  ground truth (the way `land_clearing.py`'s morphology constants got
  tuned). (4) has a candidate fix now: `detect_trees`'/`detect.py`'s
  `exclude_blurry` option (2026-08-03, opt-in, off by default) drops
  candidates in low local-detail regions ("variance of Laplacian", the
  classic blur-detection metric - see `BLUR_VARIANCE_MIN` in
  `detector.py`) computed straight off each block's own pixels, no extra
  raster pass needed. Same status as `land_clearing.py`'s `fresh_color`
  flag: the threshold was picked by eye against a synthetic blur/texture
  test, not swept against a real blurred orthophoto region - try it
  site-by-site (`--exclude-blurry` on `detect.py`) rather than trusting it
  as a universal default.
- Land Clearing Detection's boundary also looked "too busy/jagged, not like
  human digitization" on first visual check - fixed with an opening+closing
  smoothing pass on the mask plus switching `RasterToPolygon` to `SIMPLIFY`
  (see `land_clearing.py`/`detect_clearing.py`); polygon count on the same
  real orthophoto dropped from 389 to 317 (small noise fragments removed)
  while total area stayed essentially the same (~30.7 -> ~30.9 ha).
- First quantitative accuracy check (2026-08-02) against a real
  human-digitized ground-truth shapefile (a 1 m/px orthophoto tile + its
  matching "Land Aktif...Disturb" polygons from `D:\Data Bukaan\Digitasi
  Juli 2026`): the opening pass above (10 iterations) was trading recall for
  precision more than intended - 73.1% recall / 92.4% precision (F1 81.6%)
  vs. 80.0%/77.2% (F1 78.6%) with opening removed entirely. A sweep over
  opening=0..10 found opening=6 Pareto-dominates the original 10 on this
  ground truth (76.2% recall / 88.4% precision, F1 81.9% - both metrics
  better, not a trade-off), now the default in `land_clearing.py`. The
  QGIS original's `fresh_color` road/river color filter was also ported as
  an opt-in `--fresh-color`/`--bright-min` CLI flag, but measured worse on
  this tile (67.9% recall) so stays off by default - a knob for site-by-site
  tuning against that site's own ground truth, not a universal fix.
- Real orthophoto also showed ponds inside the survey area flagged as
  "cleared" (2026-08-03 - two water bodies near a road/bare-soil patch,
  visually confirmed) - water has no vegetation greenness either (same low
  ExG as bare soil) but reads dark/blue rather than bright/reddish, so it's
  now excluded unconditionally (`WATER_BRIGHTNESS_MAX` in
  `land_clearing.py`) rather than gated behind the `fresh_color` flag above.
  Picked by eye against the reported false positive, not swept against
  ground truth like `OPENING_ITERATIONS` was - revisit if it starts clipping
  real dark/wet bare soil on some site.
- "Compare Changes" (diff two Tree Detection runs over time) now has a
  DockPane UI (Analyze tab) - `detector.compare_detections()`'s
  nearest-neighbor matching was already ported, just needed
  `backend/compare_detections.py` (CLI) + `PythonBackendService`/DockPane
  wiring (2026-08-03).
