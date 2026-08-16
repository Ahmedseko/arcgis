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

        // For a winding, narrow linear feature (river, road, pipeline corridor) - flies passes
        // that follow the centerline's own curvature instead of the straight-line grid, which
        // can't fit a single direction to a shape that bends back on itself (real report,
        // 2026-08-15: a serpentine river-corridor polygon). Needs a separate centerline layer
        // since the survey polygon alone doesn't say which way the corridor actually runs.
        private bool _corridorMode;
        public bool CorridorMode
        {
            get => _corridorMode;
            set => SetProperty(ref _corridorMode, value);
        }

        private string _selectedCorridorCenterlineLayer;
        public string SelectedCorridorCenterlineLayer
        {
            get => _selectedCorridorCenterlineLayer;
            set => SetProperty(ref _selectedCorridorCenterlineLayer, value);
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
            FlightMissionStatus = Tr("Generating flight plan...", "Membuat rencana terbang...");
            try
            {
                if (MapView.Active == null)
                {
                    FlightMissionStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    FlightMissionStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var (outer, holes, sr, readError) = await ReadSurveyPolygonAsync();
                if (readError != null)
                {
                    FlightMissionStatus = readError;
                    return;
                }

                IReadOnlyList<(double X, double Y)> centerline = null;
                if (CorridorMode)
                {
                    var (line, centerlineError) = await ReadCenterlineAsync();
                    if (centerlineError != null)
                    {
                        FlightMissionStatus = centerlineError;
                        return;
                    }
                    centerline = line;
                }

                var (plan, error) = await QueuedTask.Run(() =>
                {
                    var generated = CorridorMode
                        ? FlightMissionMath.GenerateCorridorPlan(centerline, outer, holes, FlightAltitudeM,
                            FlightGsdCmPerPx, FlightImageWidthPx, FlightImageHeightPx, FlightFrontOverlapPct,
                            FlightSideOverlapPct, FlightSpeedMs, MaxFlightMinutesPerBattery)
                        : FlightMissionMath.GenerateCoveragePlan(outer, holes, FlightAltitudeM, FlightGsdCmPerPx,
                            FlightImageWidthPx, FlightImageHeightPx, FlightFrontOverlapPct, FlightSideOverlapPct,
                            FlightDirectionDeg, FlightSpeedMs, MaxFlightMinutesPerBattery, CrossHatch);
                    if (generated.Waypoints.Count == 0 && !CorridorMode)
                        return (generated, Tr("No waypoints generated. ", "Tidak ada waypoint dibuat. ") +
                            FlightMissionMath.DescribeCoverageFailure(outer, FlightGsdCmPerPx, FlightImageWidthPx,
                                FlightImageHeightPx, FlightFrontOverlapPct, FlightSideOverlapPct, FlightDirectionDeg));
                    if (generated.Waypoints.Count == 0)
                        return (generated, Tr(
                            "No waypoints generated - the centerline doesn't appear to run through the survey polygon. Check both layers cover the same area.",
                            "Tidak ada waypoint dibuat - centerline sepertinya tidak melewati poligon survei. Cek kedua layer mencakup area yang sama."));
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
                    FlightMissionStatus = Tr("Failed to create the waypoints feature class.", "Gagal membuat feature class waypoint.");
                    return;
                }
                var createPathOk = await CreatePathFeatureClassAsync(pathFc, sr);
                if (!createPathOk)
                {
                    FlightMissionStatus = Tr("Failed to create the flight path feature class.", "Gagal membuat feature class jalur terbang.");
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

                var warning = Tr(
                    plan.OffPolygonLegCount > 0
                        ? $" Heads up: {plan.OffPolygonLegCount} transit leg(s) may cut outside the survey polygon near a concave/irregular part of its boundary - check the flight path layer on the map before flying, and consider editing the polygon boundary to be more precise there."
                        : "",
                    plan.OffPolygonLegCount > 0
                        ? $" Perhatian: {plan.OffPolygonLegCount} transit leg mungkin memotong keluar poligon survei di bagian batas yang cekung/tidak beraturan - cek layer jalur terbang di map sebelum terbang, dan pertimbangkan mengedit batas poligon supaya lebih presisi di situ."
                        : "");
                FlightMissionStatus = Tr(
                    $"Done: {plan.Waypoints.Count} waypoints, {plan.MissionPartCount} mission part(s) (~{plan.TotalFlightMinutes:F1} min flight time, {plan.TotalDistanceM / 1000.0:F2} km total). Pick an export format below and click Export Mission to save for your drone app.",
                    $"Selesai: {plan.Waypoints.Count} waypoint, {plan.MissionPartCount} bagian misi (~{plan.TotalFlightMinutes:F1} menit waktu terbang, total {plan.TotalDistanceM / 1000.0:F2} km). Pilih format export di bawah dan klik Export Mission untuk menyimpan sesuai aplikasi drone Anda.")
                    + warning;
            }
            catch (Exception ex)
            {
                FlightMissionStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
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
                return (null, null, null, Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu."));

            var map = MapView.Active.Map;
            var layer = await QueuedTask.Run(() =>
                map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == SelectedFlightPlanningLayer));
            if (layer == null)
                return (null, null, null, Tr("Layer not found - click Refresh and pick again.", "Layer tidak ditemukan - klik Refresh dan pilih lagi."));

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
                        return (null, null, null, Tr(
                            $"{selection.GetCount()} features are selected in \"{layer.Name}\" - Flight Mission Planner works on one survey area at a time. Select just the one block/parcel you want and try again.",
                            $"{selection.GetCount()} fitur terpilih di \"{layer.Name}\" - Flight Mission Planner bekerja pada satu area survei per waktu. Pilih hanya satu blok/parsel yang diinginkan dan coba lagi."));
                    using var selCursor = selection.Search();
                    selCursor.MoveNext();
                    using var selFeature = (Feature)selCursor.Current;
                    poly = selFeature.GetShape() as Polygon;
                }
                else
                {
                    var total = featureClass.GetCount();
                    if (total == 0)
                        return (null, null, null, Tr("No features in the selected layer.", "Tidak ada fitur di layer yang dipilih."));
                    if (total > 1)
                        return (null, null, null, Tr(
                            $"\"{layer.Name}\" has {total} features and none is selected - Flight Mission Planner works on one survey area at a time. Select the specific parcel/block on the map and try again.",
                            $"\"{layer.Name}\" punya {total} fitur dan tidak ada yang dipilih - Flight Mission Planner bekerja pada satu area survei per waktu. Pilih parsel/blok tertentu di map dan coba lagi."));
                    using var cursor = featureClass.Search(null, false);
                    cursor.MoveNext();
                    using var onlyFeature = (Feature)cursor.Current;
                    poly = onlyFeature.GetShape() as Polygon;
                }

                if (poly == null)
                    return (null, null, null, Tr("Selected feature isn't a polygon.", "Fitur terpilih bukan poligon."));
                if (poly.SpatialReference == null || poly.SpatialReference.IsGeographic)
                    return (null, null, null, Tr(
                        "The layer must be in a projected coordinate system (meters) - line spacing/direction are computed in real-world distance, not degrees.",
                        "Layer harus dalam sistem koordinat proyeksi (meter) - jarak/arah garis dihitung dalam jarak dunia-nyata, bukan derajat."));

                // Every ring, biggest first - the biggest is the actual survey boundary, any
                // smaller ones are holes (islands) to route around. Same "outer ring + hole
                // rings" shape SliverDetection.cs already reads off Polygon.Parts.
                var rings = poly.Parts
                    .Select(part => part.Select(seg => (seg.StartPoint.X, seg.StartPoint.Y)).ToList())
                    .Where(part => part.Count >= 3)
                    .OrderByDescending(part => Math.Abs(SignedArea(part)))
                    .ToList();
                if (rings.Count == 0)
                    return (null, null, null, Tr("Selected polygon has no usable rings.", "Poligon terpilih tidak punya ring yang bisa dipakai."));

                var outer = (IReadOnlyList<(double X, double Y)>)rings[0];
                var holes = rings.Skip(1).Select(r => (IReadOnlyList<(double X, double Y)>)r).ToList();
                return (outer, holes, poly.SpatialReference, (string)null);
            });
        }

        // Corridor Mode's centerline input - same single-feature-selection guard as
        // ReadSurveyPolygonAsync, for the same reason (a layer with several separate lines has
        // no single "the" centerline to fly).
        private async Task<(IReadOnlyList<(double X, double Y)> Centerline, string Error)> ReadCenterlineAsync()
        {
            if (MapView.Active == null)
                return (null, Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu."));
            if (SelectedCorridorCenterlineLayer == null)
                return (null, Tr("Corridor mode needs a centerline layer - pick one above.", "Corridor mode butuh layer centerline - pilih satu di atas."));

            var map = MapView.Active.Map;
            var layer = await QueuedTask.Run(() =>
                map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == SelectedCorridorCenterlineLayer));
            if (layer == null)
                return (null, Tr("Centerline layer not found - click Refresh and pick again.", "Layer centerline tidak ditemukan - klik Refresh dan pilih lagi."));

            return await QueuedTask.Run(() =>
            {
                using var featureClass = layer.GetFeatureClass();
                var selection = layer.GetSelection();
                Polyline line;
                if (selection != null && selection.GetCount() > 0)
                {
                    if (selection.GetCount() > 1)
                        return (null, Tr(
                            $"{selection.GetCount()} features are selected in \"{layer.Name}\" - Corridor Mode follows one centerline at a time. Select just the one you want and try again.",
                            $"{selection.GetCount()} fitur terpilih di \"{layer.Name}\" - Corridor Mode mengikuti satu centerline per waktu. Pilih hanya satu yang diinginkan dan coba lagi."));
                    using var selCursor = selection.Search();
                    selCursor.MoveNext();
                    using var selFeature = (Feature)selCursor.Current;
                    line = selFeature.GetShape() as Polyline;
                }
                else
                {
                    var total = featureClass.GetCount();
                    if (total == 0)
                        return (null, Tr("No features in the selected centerline layer.", "Tidak ada fitur di layer centerline yang dipilih."));
                    if (total > 1)
                        return (null, Tr(
                            $"\"{layer.Name}\" has {total} features and none is selected - Corridor Mode follows one centerline at a time. Select the specific line on the map and try again.",
                            $"\"{layer.Name}\" punya {total} fitur dan tidak ada yang dipilih - Corridor Mode mengikuti satu centerline per waktu. Pilih garis tertentu di map dan coba lagi."));
                    using var cursor = featureClass.Search(null, false);
                    cursor.MoveNext();
                    using var onlyFeature = (Feature)cursor.Current;
                    line = onlyFeature.GetShape() as Polyline;
                }

                if (line == null)
                    return (null, Tr("Selected centerline feature isn't a line.", "Fitur centerline terpilih bukan garis."));
                if (line.SpatialReference == null || line.SpatialReference.IsGeographic)
                    return (null, Tr("The centerline layer must be in a projected coordinate system (meters).", "Layer centerline harus dalam sistem koordinat proyeksi (meter)."));

                // Longest part only, if the line has several disconnected pieces - a corridor
                // centerline should be one continuous path, and picking the longest is a safer
                // default than silently only using the first part (which could be a stray
                // fragment shorter than the real corridor).
                var parts = line.Parts.Select(part =>
                {
                    var pts = part.Select(seg => (seg.StartPoint.X, seg.StartPoint.Y)).ToList();
                    pts.Add((part[^1].EndPoint.X, part[^1].EndPoint.Y));
                    return pts;
                }).OrderByDescending(p =>
                {
                    double len = 0;
                    for (var i = 0; i < p.Count - 1; i++)
                        len += Math.Sqrt(Math.Pow(p[i + 1].Item1 - p[i].Item1, 2) + Math.Pow(p[i + 1].Item2 - p[i].Item2, 2));
                    return len;
                }).ToList();

                if (parts.Count == 0 || parts[0].Count < 2)
                    return (null, Tr("Selected centerline has no usable path.", "Centerline terpilih tidak punya jalur yang bisa dipakai."));

                return ((IReadOnlyList<(double X, double Y)>)parts[0], (string)null);
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
            FlightMissionStatus = Tr("Analyzing polygon shape...", "Menganalisis bentuk poligon...");
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
                var comparison = Tr(
                    suggestion.LinesAtCurrent > suggestion.LinesAtBest
                        ? $"{suggestion.LinesAtBest} lines needed here vs {suggestion.LinesAtCurrent} at the previous {previousDeg}° - fewer, longer coverage lines."
                        : $"{suggestion.LinesAtBest} lines needed - the previous {previousDeg}° was already this good.",
                    suggestion.LinesAtCurrent > suggestion.LinesAtBest
                        ? $"{suggestion.LinesAtBest} garis dibutuhkan di sini vs {suggestion.LinesAtCurrent} di {previousDeg}° sebelumnya - garis cakupan lebih sedikit dan panjang."
                        : $"{suggestion.LinesAtBest} garis dibutuhkan - {previousDeg}° sebelumnya sudah cukup baik.");
                FlightMissionStatus = Tr(
                    $"Tested all {suggestion.AnglesTested} possible compass angles against the survey polygon's actual shape. Best fit: {FlightDirectionDeg}° ({comparison}) Click Generate Mission to use it.",
                    $"Menguji semua {suggestion.AnglesTested} kemungkinan sudut kompas terhadap bentuk asli poligon survei. Paling pas: {FlightDirectionDeg}° ({comparison}) Klik Generate Mission untuk memakainya.");
            }
            catch (Exception ex)
            {
                FlightMissionStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
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
                    FlightMissionStatus = Tr("Export cancelled.", "Export dibatalkan.");
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

                var formatNote = Tr(
                    isKmz
                        ? $"DJI Pilot 2 WPML mission for {SelectedDroneModel}, with a take-photo action set at every waypoint. Review altitude/home point/RC-lost behavior inside DJI Pilot 2 before flying, same as any imported mission."
                        : "Litchi Mission Hub CSV format (latitude, longitude, altitude(m)) - import inside the Litchi app, not DJI Fly/Pilot 2 (they don't take CSV directly). This file only has the flight path - turn on photo capture in Litchi's own mission settings (e.g. \"Take photos\" per waypoint, or a distance/time interval) before flying, since this toolkit can't verify Litchi's per-waypoint action CSV columns without testing them in the app first.",
                    isKmz
                        ? $"Misi WPML DJI Pilot 2 untuk {SelectedDroneModel}, dengan aksi take-photo diset di tiap waypoint. Cek ulang altitude/home point/perilaku RC-lost di dalam DJI Pilot 2 sebelum terbang, sama seperti misi impor lainnya."
                        : "Format CSV Litchi Mission Hub (latitude, longitude, altitude(m)) - import di dalam aplikasi Litchi, bukan DJI Fly/Pilot 2 (mereka tidak menerima CSV langsung). File ini hanya berisi jalur terbang - aktifkan pengambilan foto di pengaturan misi Litchi sendiri (mis. \"Take photos\" per waypoint, atau interval jarak/waktu) sebelum terbang, karena toolkit ini tidak bisa memverifikasi kolom CSV aksi per-waypoint Litchi tanpa mengujinya di aplikasi dulu.");
                FlightMissionStatus = Tr(
                    $"Done: exported {_lastFlightPlan.Waypoints.Count} waypoints to {writtenFiles.Count} file(s) ({string.Join(", ", writtenFiles.Select(Path.GetFileName))}). ",
                    $"Selesai: {_lastFlightPlan.Waypoints.Count} waypoint diekspor ke {writtenFiles.Count} file ({string.Join(", ", writtenFiles.Select(Path.GetFileName))}). ")
                    + formatNote;
            }
            catch (Exception ex)
            {
                FlightMissionStatus = Tr($"Unexpected error exporting: {ex.Message}", $"Error tak terduga saat ekspor: {ex.Message}");
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
