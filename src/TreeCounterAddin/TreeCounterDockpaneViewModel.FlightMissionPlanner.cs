using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Drone survey mission planning ("Flight Mission Planner") - draw/pick a survey area
    // polygon, get a boustrophedon coverage flight plan (waypoints + a battery-based mission
    // split) back, exportable as CSV for whatever drone app the field crew actually uses.
    // The coverage-geometry math itself lives in FlightMissionMath.cs (plain coordinates, no
    // ArcGIS reference - see its own header comment) - this file only does the ArcGIS-specific
    // plumbing: reading the polygon, writing results as map layers, CSV export.
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedFlightPlanningLayer;
        public string SelectedFlightPlanningLayer
        {
            get => _selectedFlightPlanningLayer;
            set => SetProperty(ref _selectedFlightPlanningLayer, value);
        }

        private double _flightAltitudeM = 100;
        public double FlightAltitudeM
        {
            get => _flightAltitudeM;
            set => SetProperty(ref _flightAltitudeM, value);
        }

        // 5 (cm/px) matches REFERENCE_GSD_M (0.05) already used throughout the Python
        // detection backend - this is the GSD the rest of the toolkit is tuned around, so a
        // survey flown at this default produces imagery every other feature already expects.
        private double _flightGsdCmPerPx = 5;
        public double FlightGsdCmPerPx
        {
            get => _flightGsdCmPerPx;
            set => SetProperty(ref _flightGsdCmPerPx, value);
        }

        private int _flightImageWidthPx = 4000;
        public int FlightImageWidthPx
        {
            get => _flightImageWidthPx;
            set => SetProperty(ref _flightImageWidthPx, value);
        }

        private int _flightImageHeightPx = 3000;
        public int FlightImageHeightPx
        {
            get => _flightImageHeightPx;
            set => SetProperty(ref _flightImageHeightPx, value);
        }

        // 80/70 - standard photogrammetry defaults (e.g. Pix4Dcapture/DroneDeploy ship the
        // same numbers) for reliable 3D reconstruction, not tuned specifically for this project.
        private double _flightFrontOverlapPct = 80;
        public double FlightFrontOverlapPct
        {
            get => _flightFrontOverlapPct;
            set => SetProperty(ref _flightFrontOverlapPct, value);
        }

        private double _flightSideOverlapPct = 70;
        public double FlightSideOverlapPct
        {
            get => _flightSideOverlapPct;
            set => SetProperty(ref _flightSideOverlapPct, value);
        }

        private double _flightDirectionDeg;
        public double FlightDirectionDeg
        {
            get => _flightDirectionDeg;
            set => SetProperty(ref _flightDirectionDeg, value);
        }

        private double _flightSpeedMs = 8;
        public double FlightSpeedMs
        {
            get => _flightSpeedMs;
            set => SetProperty(ref _flightSpeedMs, value);
        }

        private double _maxFlightMinutesPerBattery = 20;
        public double MaxFlightMinutesPerBattery
        {
            get => _maxFlightMinutesPerBattery;
            set => SetProperty(ref _maxFlightMinutesPerBattery, value);
        }

        // Also flies a second pass at 90 deg from the main direction - standard photogrammetry
        // technique for better 3D reconstruction (building facades and other vertical features
        // get seen from more angles than a single-direction grid manages). Roughly doubles
        // flight time/mission parts/photo count, so it's opt-in, not the default.
        private bool _crossHatch;
        public bool CrossHatch
        {
            get => _crossHatch;
            set => SetProperty(ref _crossHatch, value);
        }

        // Two export formats because no single format actually covers "every DJI drone":
        // Litchi CSV works with the consumer lineup (Mavic 3 Classic, Air/Mini series - the
        // apps for those, DJI Fly, have no waypoint-mission import at all), DJI Pilot 2 KMZ
        // only works with the enterprise lineup (Mavic 3 Enterprise, Matrice 30/300/350) and
        // needs a drone-specific code baked into the file (see WpmlBuilder.cs).
        public List<string> ExportFormats { get; } = new()
        {
            "Litchi CSV (works with most DJI drones)",
            "DJI Pilot 2 KMZ (Mavic 3 Enterprise / Matrice series only)"
        };

        private string _selectedExportFormat = "Litchi CSV (works with most DJI drones)";
        public string SelectedExportFormat
        {
            get => _selectedExportFormat;
            set
            {
                if (!SetProperty(ref _selectedExportFormat, value)) return;
                IsKmzFormatSelected = value != null && value.StartsWith("DJI Pilot 2");
            }
        }

        private bool _isKmzFormatSelected;
        public bool IsKmzFormatSelected
        {
            get => _isKmzFormatSelected;
            set => SetProperty(ref _isKmzFormatSelected, value);
        }

        public List<string> DroneModelNames { get; } = WpmlBuilder.DronePresets.Select(p => p.Label).ToList();

        private string _selectedDroneModel = WpmlBuilder.DronePresets[0].Label;
        public string SelectedDroneModel
        {
            get => _selectedDroneModel;
            set => SetProperty(ref _selectedDroneModel, value);
        }

        private bool _isGeneratingMission;
        public bool IsGeneratingMission
        {
            get => _isGeneratingMission;
            set => SetProperty(ref _isGeneratingMission, value);
        }

        private string _flightMissionStatus = "";
        public string FlightMissionStatus
        {
            get => _flightMissionStatus;
            set => SetProperty(ref _flightMissionStatus, value);
        }

        // Kept around so "Export Mission" reuses the exact plan already on the map instead of
        // silently regenerating (and possibly drifting from what's displayed if a parameter
        // box was edited in between).
        private FlightMissionMath.Plan _lastFlightPlan;
        private SpatialReference _lastFlightPlanSpatialReference;

        public ICommand GenerateMissionCommand => new RelayCommand(async () => await GenerateMissionAsync(),
            () => !IsGeneratingMission && SelectedFlightPlanningLayer != null);
        public ICommand ExportMissionCommand => new RelayCommand(async () => await ExportMissionAsync(),
            () => _lastFlightPlan != null);

        private async Task GenerateMissionAsync()
        {
            IsGeneratingMission = true;
            FlightMissionStatus = "Generating flight plan...";
            try
            {
                if (MapView.Active == null)
                {
                    FlightMissionStatus = "No active map view. Open a map first.";
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    FlightMissionStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }

                var (outer, holes, sr, readError) = await ReadSurveyPolygonAsync();
                if (readError != null)
                {
                    FlightMissionStatus = readError;
                    return;
                }

                var (plan, error) = await QueuedTask.Run(() =>
                {
                    var generated = FlightMissionMath.GenerateCoveragePlan(
                        outer, holes, FlightAltitudeM, FlightGsdCmPerPx, FlightImageWidthPx, FlightImageHeightPx,
                        FlightFrontOverlapPct, FlightSideOverlapPct, FlightDirectionDeg, FlightSpeedMs,
                        MaxFlightMinutesPerBattery, CrossHatch);
                    if (generated.Waypoints.Count == 0)
                        return (generated, "No waypoints generated. " +
                            FlightMissionMath.DescribeCoverageFailure(outer, FlightGsdCmPerPx, FlightImageWidthPx,
                                FlightImageHeightPx, FlightFrontOverlapPct, FlightSideOverlapPct, FlightDirectionDeg));
                    return (generated, (string)null);
                });

                if (error != null)
                {
                    FlightMissionStatus = error;
                    return;
                }

                var map = MapView.Active.Map;
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var waypointsFc = Path.Combine(project.DefaultGeodatabasePath, $"FlightWaypoints_{stamp}");
                var pathFc = Path.Combine(project.DefaultGeodatabasePath, $"FlightPath_{stamp}");

                var createOk = await CreateWaypointFeatureClassAsync(waypointsFc, sr);
                if (!createOk)
                {
                    FlightMissionStatus = "Failed to create the waypoints feature class.";
                    return;
                }
                var createPathOk = await CreatePathFeatureClassAsync(pathFc, sr);
                if (!createPathOk)
                {
                    FlightMissionStatus = "Failed to create the flight path feature class.";
                    return;
                }

                await QueuedTask.Run(() =>
                {
                    InsertWaypoints(waypointsFc, sr, plan.Waypoints);
                    InsertPathLines(pathFc, sr, plan.Waypoints);

                    if (LayerFactory.Instance.CreateLayer(new Uri(pathFc), map, layerName: Path.GetFileName(pathFc)) is FeatureLayer pathLayer)
                    {
                        var lineSymbol = SymbolFactory.Instance.ConstructLineSymbol(
                            ColorFactory.Instance.CreateRGBColor(0, 160, 220), 1.5);
                        pathLayer.SetRenderer(new CIMSimpleRenderer { Symbol = lineSymbol.MakeSymbolReference() });
                    }
                    if (LayerFactory.Instance.CreateLayer(new Uri(waypointsFc), map, layerName: Path.GetFileName(waypointsFc)) is FeatureLayer wpLayer)
                    {
                        var ptSymbol = SymbolFactory.Instance.ConstructPointSymbol(
                            ColorFactory.Instance.CreateRGBColor(255, 140, 0), 4);
                        wpLayer.SetRenderer(new CIMSimpleRenderer { Symbol = ptSymbol.MakeSymbolReference() });
                    }
                });

                _lastFlightPlan = plan;
                _lastFlightPlanSpatialReference = sr;

                var warning = plan.OffPolygonLegCount > 0
                    ? $" Heads up: {plan.OffPolygonLegCount} transit leg(s) may cut outside the survey " +
                        "polygon near a concave/irregular part of its boundary - check the flight path " +
                        "layer on the map before flying, and consider editing the polygon boundary to be " +
                        "more precise there."
                    : "";
                FlightMissionStatus = $"Done: {plan.Waypoints.Count} waypoints, {plan.MissionPartCount} mission " +
                    $"part(s) (~{plan.TotalFlightMinutes:F1} min flight time, {plan.TotalDistanceM / 1000.0:F2} km " +
                    $"total). Pick an export format below and click Export Mission to save for your drone app.{warning}";
            }
            catch (Exception ex)
            {
                FlightMissionStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsGeneratingMission = false;
            }
        }

        // Shared by GenerateMissionAsync and SuggestDirectionAsync - resolves
        // SelectedFlightPlanningLayer down to one unambiguous polygon's outer ring + holes.
        private async Task<(IReadOnlyList<(double X, double Y)> Outer,
            List<IReadOnlyList<(double X, double Y)>> Holes, SpatialReference Sr, string Error)> ReadSurveyPolygonAsync()
        {
            if (MapView.Active == null)
                return (null, null, null, "No active map view. Open a map first.");

            var map = MapView.Active.Map;
            var layer = await QueuedTask.Run(() =>
                map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == SelectedFlightPlanningLayer));
            if (layer == null)
                return (null, null, null, "Layer not found - click Refresh and pick again.");

            return await QueuedTask.Run(() =>
            {
                using var featureClass = layer.GetFeatureClass();

                // A layer with several separate features (parcels/blocks) has no single "the"
                // survey boundary - silently flying whichever record the cursor happens to
                // return first is how a real report (2026-08-14, "Pengajuan RT XLVI", 13
                // features from a 1,547 m2 sliver up to a 696,172 m2 block) ended up planning a
                // mission over a tiny, oddly-shaped parcel nobody meant to fly. Require an
                // explicit single-feature selection whenever the layer isn't unambiguous.
                var selection = layer.GetSelection();
                Polygon poly;
                if (selection != null && selection.GetCount() > 0)
                {
                    if (selection.GetCount() > 1)
                        return (null, null, null, $"{selection.GetCount()} features are selected in " +
                            $"\"{layer.Name}\" - Flight Mission Planner works on one survey area at a time. " +
                            "Select just the one block/parcel you want and try again.");
                    using var selCursor = selection.Search();
                    selCursor.MoveNext();
                    using var selFeature = (Feature)selCursor.Current;
                    poly = selFeature.GetShape() as Polygon;
                }
                else
                {
                    var total = featureClass.GetCount();
                    if (total == 0)
                        return (null, null, null, "No features in the selected layer.");
                    if (total > 1)
                        return (null, null, null, $"\"{layer.Name}\" has {total} features and none is " +
                            "selected - Flight Mission Planner works on one survey area at a time. Select " +
                            "the specific parcel/block on the map and try again.");
                    using var cursor = featureClass.Search(null, false);
                    cursor.MoveNext();
                    using var onlyFeature = (Feature)cursor.Current;
                    poly = onlyFeature.GetShape() as Polygon;
                }

                if (poly == null)
                    return (null, null, null, "Selected feature isn't a polygon.");
                if (poly.SpatialReference == null || poly.SpatialReference.IsGeographic)
                    return (null, null, null, "The layer must be in a projected coordinate system (meters) - " +
                        "line spacing/direction are computed in real-world distance, not degrees.");

                // Every ring, biggest first - the biggest is the actual survey boundary, any
                // smaller ones are holes (islands) to route around. Same "outer ring + hole
                // rings" shape SliverDetection.cs already reads off Polygon.Parts.
                var rings = poly.Parts
                    .Select(part => part.Select(seg => (seg.StartPoint.X, seg.StartPoint.Y)).ToList())
                    .Where(part => part.Count >= 3)
                    .OrderByDescending(part => Math.Abs(SignedArea(part)))
                    .ToList();
                if (rings.Count == 0)
                    return (null, null, null, "Selected polygon has no usable rings.");

                var outer = (IReadOnlyList<(double X, double Y)>)rings[0];
                var holes = rings.Skip(1).Select(r => (IReadOnlyList<(double X, double Y)>)r).ToList();
                return (outer, holes, poly.SpatialReference, (string)null);
            });
        }

        public ICommand SuggestDirectionCommand => new RelayCommand(async () => await SuggestDirectionAsync(),
            () => SelectedFlightPlanningLayer != null);

        // Quick manual overrides for the common cases, alongside Suggest - a site that's
        // obviously a rectangle running due north-south/east-west doesn't need the 180-angle
        // search, and some users just want to try both without typing exact degrees.
        public ICommand SetDirectionVerticalCommand => new RelayCommand(() => FlightDirectionDeg = 0);
        public ICommand SetDirectionHorizontalCommand => new RelayCommand(() => FlightDirectionDeg = 90);

        // Picks the compass bearing that minimizes the number of coverage lines needed, i.e.
        // aligns flight lines with the survey polygon's own long axis instead of cutting across
        // it. A direction perpendicular to an elongated/irregular site's shape (the default 0
        // deg is exactly this for an east-west site) chops coverage into many short zigzag
        // columns of very different lengths - real report (2026-08-14, "drone flight path", a
        // 2844x804m site): 0 deg needed ~47 lines of 6-21 points each (highly uneven, lots of
        // steep diagonal jumps between columns); ~92 deg needed only ~13 lines of far more
        // uniform length. Doesn't touch FlightDirectionDeg unless the user clicks this - a
        // manually-chosen direction (e.g. to match a client's preferred flight orientation)
        // should never get silently overwritten by Generate Mission itself.
        private async Task SuggestDirectionAsync()
        {
            FlightMissionStatus = "Analyzing polygon shape...";
            try
            {
                var (outer, _, _, error) = await ReadSurveyPolygonAsync();
                if (error != null)
                {
                    FlightMissionStatus = error;
                    return;
                }
                var previousDeg = FlightDirectionDeg;
                var suggestion = await QueuedTask.Run(() => FlightMissionMath.SuggestDirection(
                    outer, FlightGsdCmPerPx, FlightImageWidthPx, FlightSideOverlapPct, FlightDirectionDeg));
                FlightDirectionDeg = suggestion.BestDegrees;
                var comparison = suggestion.LinesAtCurrent > suggestion.LinesAtBest
                    ? $"{suggestion.LinesAtBest} lines needed here vs {suggestion.LinesAtCurrent} at the " +
                        $"previous {previousDeg}° - fewer, longer coverage lines."
                    : $"{suggestion.LinesAtBest} lines needed - the previous {previousDeg}° was already this good.";
                FlightMissionStatus = $"Tested all {suggestion.AnglesTested} possible compass angles against " +
                    $"the survey polygon's actual shape. Best fit: {FlightDirectionDeg}° ({comparison}) " +
                    "Click Generate Mission to use it.";
            }
            catch (Exception ex)
            {
                FlightMissionStatus = $"Unexpected error: {ex.Message}";
            }
        }

        // Two export formats because no single format actually covers "every DJI drone" (see
        // the comment on ExportFormats above): Litchi CSV for the consumer lineup, DJI Pilot 2
        // KMZ for the enterprise lineup. Both model "one file = one flight route", so a battery
        // split into multiple mission parts becomes one file per part either way.
        private async Task ExportMissionAsync()
        {
            if (_lastFlightPlan == null) return;
            try
            {
                var isKmz = IsKmzFormatSelected;
                var dialog = new SaveFileDialog
                {
                    FileName = isKmz ? "FlightMission.kmz" : "FlightMission.csv",
                    Filter = isKmz ? "DJI WPML mission (*.kmz)|*.kmz" : "CSV files (*.csv)|*.csv",
                    DefaultExt = isKmz ? ".kmz" : ".csv"
                };
                if (dialog.ShowDialog() != true)
                {
                    FlightMissionStatus = "Export cancelled.";
                    return;
                }

                var wgs84 = SpatialReferences.WGS84;
                var byPart = await QueuedTask.Run(() =>
                {
                    var result = new Dictionary<int, List<(double Lat, double Lon, double AltitudeM)>>();
                    foreach (var wp in _lastFlightPlan.Waypoints.OrderBy(w => w.MissionPart).ThenBy(w => w.Sequence))
                    {
                        if (!result.TryGetValue(wp.MissionPart, out var points))
                            result[wp.MissionPart] = points = new List<(double, double, double)>();
                        var point = MapPointBuilderEx.CreateMapPoint(wp.X, wp.Y, _lastFlightPlanSpatialReference);
                        var projected = GeometryEngine.Instance.Project(point, wgs84) as MapPoint;
                        points.Add((projected?.Y ?? 0, projected?.X ?? 0, wp.Altitude));
                    }
                    return result;
                });

                var baseName = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? "",
                    Path.GetFileNameWithoutExtension(dialog.FileName));
                var writtenFiles = new List<string>();
                var ext = isKmz ? "kmz" : "csv";

                if (isKmz)
                {
                    var drone = WpmlBuilder.DronePresets.FirstOrDefault(p => p.Label == SelectedDroneModel)
                        ?? WpmlBuilder.DronePresets[0];
                    foreach (var (part, points) in byPart.OrderBy(kv => kv.Key))
                    {
                        var path = byPart.Count > 1 ? $"{baseName}_part{part}.{ext}" : dialog.FileName;
                        if (File.Exists(path)) File.Delete(path);
                        var kml = WpmlBuilder.BuildTemplateKml(points, FlightSpeedMs, drone);
                        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                        using (var entryStream = archive.CreateEntry("wpmz/template.kml").Open())
                        using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                            await writer.WriteAsync(kml);
                        writtenFiles.Add(path);
                    }
                }
                else
                {
                    foreach (var (part, points) in byPart.OrderBy(kv => kv.Key))
                    {
                        var path = byPart.Count > 1 ? $"{baseName}_part{part}.{ext}" : dialog.FileName;
                        var lines = new List<string> { "latitude,longitude,altitude(m)" };
                        lines.AddRange(points.Select(p => string.Join(",", new[]
                        {
                            p.Lat.ToString("F7", CultureInfo.InvariantCulture),
                            p.Lon.ToString("F7", CultureInfo.InvariantCulture),
                            p.AltitudeM.ToString("F1", CultureInfo.InvariantCulture),
                        })));
                        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);
                        writtenFiles.Add(path);
                    }
                }

                var formatNote = isKmz
                    ? $"DJI Pilot 2 WPML mission for {SelectedDroneModel}, with a take-photo action set at " +
                        "every waypoint. Review altitude/home point/RC-lost behavior inside DJI Pilot 2 " +
                        "before flying, same as any imported mission."
                    : "Litchi Mission Hub CSV format (latitude, longitude, altitude(m)) - import inside the " +
                        "Litchi app, not DJI Fly/Pilot 2 (they don't take CSV directly). This file only has " +
                        "the flight path - turn on photo capture in Litchi's own mission settings (e.g. " +
                        "\"Take photos\" per waypoint, or a distance/time interval) before flying, since this " +
                        "toolkit can't verify Litchi's per-waypoint action CSV columns without testing them " +
                        "in the app first.";
                FlightMissionStatus = $"Done: exported {_lastFlightPlan.Waypoints.Count} waypoints to " +
                    $"{writtenFiles.Count} file(s) ({string.Join(", ", writtenFiles.Select(Path.GetFileName))}). " +
                    formatNote;
            }
            catch (Exception ex)
            {
                FlightMissionStatus = $"Unexpected error exporting: {ex.Message}";
            }
        }

        // Shoelace formula - only the sign matters here (used to rank rings by |area|), not
        // the value itself.
        private static double SignedArea(List<(double X, double Y)> ring)
        {
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var (x1, y1) = ring[i];
                var (x2, y2) = ring[(i + 1) % ring.Count];
                sum += x1 * y2 - x2 * y1;
            }
            return sum / 2.0;
        }

        // Schema only (CreateFeatureclass + AddField GP tools, same pattern Fishnet.cs already
        // uses for schema work) - the actual per-waypoint geometry is written afterward with a
        // plain InsertCursor (see InsertWaypoints/InsertPathLines), since GP tools have no way
        // to take "here are N custom XY points" as an input.
        private static async Task<bool> CreateWaypointFeatureClassAsync(string fc, SpatialReference sr)
        {
            var gdb = Path.GetDirectoryName(fc);
            var name = Path.GetFileName(fc);
            var createResult = await Geoprocessing.ExecuteToolAsync("management.CreateFeatureclass",
                Geoprocessing.MakeValueArray(gdb, name, "POINT", "", "DISABLED", "DISABLED", sr),
                null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
            if (createResult.IsFailed) return false;

            foreach (var (field, type) in new[] { ("MissionPart", "LONG"), ("Sequence", "LONG"), ("Altitude_m", "DOUBLE") })
            {
                var addResult = await Geoprocessing.ExecuteToolAsync("management.AddField",
                    Geoprocessing.MakeValueArray(fc, field, type),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (addResult.IsFailed) return false;
            }
            return true;
        }

        private static async Task<bool> CreatePathFeatureClassAsync(string fc, SpatialReference sr)
        {
            var gdb = Path.GetDirectoryName(fc);
            var name = Path.GetFileName(fc);
            var createResult = await Geoprocessing.ExecuteToolAsync("management.CreateFeatureclass",
                Geoprocessing.MakeValueArray(gdb, name, "POLYLINE", "", "DISABLED", "DISABLED", sr),
                null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
            if (createResult.IsFailed) return false;

            var addResult = await Geoprocessing.ExecuteToolAsync("management.AddField",
                Geoprocessing.MakeValueArray(fc, "MissionPart", "LONG"),
                null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
            return !addResult.IsFailed;
        }

        // Must run on the MCT (QueuedTask).
        private static void InsertWaypoints(string fc, SpatialReference sr, List<FlightMissionMath.Waypoint> waypoints)
        {
            using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(Path.GetDirectoryName(fc))));
            using var featureClass = geodatabase.OpenDataset<ArcGIS.Core.Data.FeatureClass>(Path.GetFileName(fc));
            using var insertCursor = featureClass.CreateInsertCursor();
            foreach (var wp in waypoints)
            {
                using var rowBuffer = featureClass.CreateRowBuffer();
                rowBuffer[featureClass.GetDefinition().GetShapeField()] = MapPointBuilderEx.CreateMapPoint(wp.X, wp.Y, sr);
                rowBuffer["MissionPart"] = wp.MissionPart;
                rowBuffer["Sequence"] = wp.Sequence;
                rowBuffer["Altitude_m"] = wp.Altitude;
                insertCursor.Insert(rowBuffer);
            }
            insertCursor.Flush();
        }

        private static void InsertPathLines(string fc, SpatialReference sr, List<FlightMissionMath.Waypoint> waypoints)
        {
            using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(Path.GetDirectoryName(fc))));
            using var featureClass = geodatabase.OpenDataset<ArcGIS.Core.Data.FeatureClass>(Path.GetFileName(fc));
            using var insertCursor = featureClass.CreateInsertCursor();
            foreach (var group in waypoints.GroupBy(w => w.MissionPart).OrderBy(g => g.Key))
            {
                var points = group.OrderBy(w => w.Sequence)
                    .Select(w => MapPointBuilderEx.CreateMapPoint(w.X, w.Y, sr)).ToList();
                if (points.Count < 2) continue;

                using var rowBuffer = featureClass.CreateRowBuffer();
                rowBuffer[featureClass.GetDefinition().GetShapeField()] = PolylineBuilderEx.CreatePolyline(points, sr);
                rowBuffer["MissionPart"] = group.Key;
                insertCursor.Insert(rowBuffer);
            }
            insertCursor.Flush();
        }
    }
}
