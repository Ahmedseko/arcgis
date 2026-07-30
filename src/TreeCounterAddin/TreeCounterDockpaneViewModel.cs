using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Detection itself runs in backend/detect.py (ported ExG + YOLO pipeline from
    // qgis_plugin/tree_counter) via PythonBackendService; this ViewModel only handles
    // picking inputs/parameters and loading the resulting feature class onto the map.
    internal class TreeCounterDockpaneViewModel : DockPane
    {
        private const string DockPaneId = "TreeCounterAddin_Dockpane";

        private static readonly Dictionary<string, (int Sigma, double ExgThreshold, double MinSmooth)> ProfileDefaults = new()
        {
            ["Natural Forest"] = (75, 18, 10),
            ["Oil Palm Plantation"] = (20, 10, 30),
        };

        private CancellationTokenSource _runCts;

        public TreeCounterDockpaneViewModel()
        {
            // Load previously-saved keys (DPAPI-encrypted, see ApiKeyStore) so the user
            // isn't stuck retyping API keys every time they reopen ArcGIS Pro.
            foreach (var kv in ApiKeyStore.Load())
                _apiKeysByProvider[kv.Key] = kv.Value;
            if (_apiKeysByProvider.TryGetValue(_selectedProvider, out var savedKey))
                _apiKey = savedKey; // set the backing field directly - no save-on-load, no UI to notify yet

            // Ribbon status labels (RibbonControls.cs) subscribe to this static event at
            // their own construction time - which can happen before this dockpane instance
            // exists at all (DockPaneManager.Find lazily creates it on first use). Relying
            // on ArcGIS's own Button.OnUpdate() ribbon-refresh timing alone left the labels
            // stuck on their placeholder text even after the panel was opened and used.
            PropertyChanged += (_, __) => RibbonStateChanged?.Invoke();

            // DockPaneManager only makes Find(DockPaneId) resolve to "this" AFTER the
            // constructor returns - firing synchronously here means every subscriber's
            // Instance lookup (which calls Find()) still gets null back, so the very first
            // refresh (right when the panel first opens) silently did nothing. Defer past
            // construction so Find() actually succeeds by the time subscribers run.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RibbonStateChanged?.Invoke());
        }

        // See the constructor comment above - fired on every property change so ribbon
        // status labels stay live without depending on OnUpdate() timing.
        public static event Action RibbonStateChanged;

        internal static void Show()
        {
            FrameworkApplication.DockPaneManager.Find(DockPaneId)?.Activate();
        }

        // Lets ribbon-level shortcut buttons/status labels (RibbonControls.cs) reach the
        // same singleton the dockpane view binds to, without needing the panel open.
        internal static TreeCounterDockpaneViewModel Instance =>
            FrameworkApplication.DockPaneManager.Find(DockPaneId) as TreeCounterDockpaneViewModel;

        private string _heading = "Forestry Toolkit";
        public string Heading
        {
            get => _heading;
            set => SetProperty(ref _heading, value);
        }

        public ObservableCollection<string> RasterLayers { get; } = new();
        public ObservableCollection<string> Profiles { get; } = new(ProfileDefaults.Keys);
        public ObservableCollection<string> PolygonLayers { get; } = new();
        public ObservableCollection<string> PointLayers { get; } = new();
        public ObservableCollection<string> GpxLayers { get; } = new();

        private string _selectedRasterLayer;
        public string SelectedRasterLayer
        {
            get => _selectedRasterLayer;
            set => SetProperty(ref _selectedRasterLayer, value);
        }

        private string _selectedProfile = "Oil Palm Plantation";
        public string SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (SetProperty(ref _selectedProfile, value) && ProfileDefaults.TryGetValue(value, out var d))
                {
                    Sigma = d.Sigma;
                    ExgThreshold = d.ExgThreshold;
                    MinSmooth = d.MinSmooth;
                }
            }
        }

        private int _sigma = ProfileDefaults["Oil Palm Plantation"].Sigma;
        public int Sigma
        {
            get => _sigma;
            set => SetProperty(ref _sigma, value);
        }

        private double _exgThreshold = ProfileDefaults["Oil Palm Plantation"].ExgThreshold;
        public double ExgThreshold
        {
            get => _exgThreshold;
            set => SetProperty(ref _exgThreshold, value);
        }

        private double _minSmooth = ProfileDefaults["Oil Palm Plantation"].MinSmooth;
        public double MinSmooth
        {
            get => _minSmooth;
            set => SetProperty(ref _minSmooth, value);
        }

        private static readonly Dictionary<string, string[]> ModelsByProvider = new()
        {
            ["Gemini"] = new[] { "gemini-3.5-flash", "gemini-3.1-flash-lite", "gemini-3.1-pro", "gemini-2.5-flash" },
            ["OpenAI"] = new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini" },
            ["Claude"] = new[] { "claude-haiku-4-5", "claude-sonnet-5", "claude-opus-5" },
        };

        // Keyed by provider name so switching Provider doesn't clobber a key you already
        // typed for another provider - each provider remembers its own key.
        private readonly Dictionary<string, string> _apiKeysByProvider = new();

        public ObservableCollection<string> Providers { get; } = new(ModelsByProvider.Keys);
        public ObservableCollection<string> Models { get; } = new(ModelsByProvider["Gemini"]);

        private string _selectedProvider = "Gemini";
        public string SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                var previous = _selectedProvider;
                if (SetProperty(ref _selectedProvider, value))
                {
                    _apiKeysByProvider[previous] = ApiKey ?? "";

                    Models.Clear();
                    foreach (var m in ModelsByProvider[value])
                        Models.Add(m);
                    SelectedModel = Models.FirstOrDefault();

                    ApiKey = _apiKeysByProvider.TryGetValue(value, out var savedKey) ? savedKey : "";
                }
            }
        }

        private string _selectedModel = ModelsByProvider["Gemini"][0];
        public string SelectedModel
        {
            get => _selectedModel;
            set => SetProperty(ref _selectedModel, value);
        }

        private string _apiKey;
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    _apiKeysByProvider[SelectedProvider] = value ?? "";
                    ApiKeyStore.Save(_apiKeysByProvider);
                }
            }
        }

        private string _testKeyStatus;
        public string TestKeyStatus
        {
            get => _testKeyStatus;
            set => SetProperty(ref _testKeyStatus, value);
        }

        private bool _isTestingKey;
        public bool IsTestingKey
        {
            get => _isTestingKey;
            set => SetProperty(ref _isTestingKey, value);
        }

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set => SetProperty(ref _isRunning, value);
        }

        private int _progress;
        public int Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        // Populated after a successful run - surfaced on the ribbon's "Last: N trees / X ha"
        // status label. -1 means "no completed run yet" (distinct from "0 trees found").
        private int _lastTreeCount = -1;
        public int LastTreeCount
        {
            get => _lastTreeCount;
            set => SetProperty(ref _lastTreeCount, value);
        }

        private double _lastAreaHa;
        public double LastAreaHa
        {
            get => _lastAreaHa;
            set => SetProperty(ref _lastAreaHa, value);
        }

        private string _selectedPolygonLayer;
        public string SelectedPolygonLayer
        {
            get => _selectedPolygonLayer;
            set => SetProperty(ref _selectedPolygonLayer, value);
        }

        private double _cellWidth = 50;
        public double CellWidth
        {
            get => _cellWidth;
            set => SetProperty(ref _cellWidth, value);
        }

        private double _cellHeight = 50;
        public double CellHeight
        {
            get => _cellHeight;
            set => SetProperty(ref _cellHeight, value);
        }

        private bool _isCreatingFishnet;
        public bool IsCreatingFishnet
        {
            get => _isCreatingFishnet;
            set => SetProperty(ref _isCreatingFishnet, value);
        }

        private string _fishnetStatus = "";
        public string FishnetStatus
        {
            get => _fishnetStatus;
            set => SetProperty(ref _fishnetStatus, value);
        }

        // GPX only supports waypoints (points) and tracks (lines) - no filled areas - so
        // polygon layers get their boundary converted to a line first (PolygonToLine) before
        // being handed to FeaturesToGPX. GpxLayers therefore lists point/line/polygon layers,
        // unlike PointLayers (used by Biomass estimation) which stays point-only.
        private string _selectedGpxLayer;
        public string SelectedGpxLayer
        {
            get => _selectedGpxLayer;
            set => SetProperty(ref _selectedGpxLayer, value);
        }

        private bool _isExportingGpx;
        public bool IsExportingGpx
        {
            get => _isExportingGpx;
            set => SetProperty(ref _isExportingGpx, value);
        }

        private string _gpxStatus = "";
        public string GpxStatus
        {
            get => _gpxStatus;
            set => SetProperty(ref _gpxStatus, value);
        }

        private bool _isImportingExcel;
        public bool IsImportingExcel
        {
            get => _isImportingExcel;
            set => SetProperty(ref _isImportingExcel, value);
        }

        private string _importExcelStatus = "";
        public string ImportExcelStatus
        {
            get => _importExcelStatus;
            set => SetProperty(ref _importExcelStatus, value);
        }

        private string _templateStatus = "";
        public string TemplateStatus
        {
            get => _templateStatus;
            set => SetProperty(ref _templateStatus, value);
        }

        // --- Sliver polygon detection ---
        private string _selectedSliverLayer;
        public string SelectedSliverLayer
        {
            get => _selectedSliverLayer;
            set => SetProperty(ref _selectedSliverLayer, value);
        }

        private bool _isDetectingSlivers;
        public bool IsDetectingSlivers
        {
            get => _isDetectingSlivers;
            set => SetProperty(ref _isDetectingSlivers, value);
        }

        private string _sliverStatus = "";
        public string SliverStatus
        {
            get => _sliverStatus;
            set => SetProperty(ref _sliverStatus, value);
        }

        // --- Geotagged photos import ---
        private bool _isImportingPhotos;
        public bool IsImportingPhotos
        {
            get => _isImportingPhotos;
            set => SetProperty(ref _isImportingPhotos, value);
        }

        private string _photoImportStatus = "";
        public string PhotoImportStatus
        {
            get => _photoImportStatus;
            set => SetProperty(ref _photoImportStatus, value);
        }

        // --- Biomass / carbon estimation ---
        private string _selectedBiomassLayer;
        public string SelectedBiomassLayer
        {
            get => _selectedBiomassLayer;
            set => SetProperty(ref _selectedBiomassLayer, value);
        }

        // Defaults are generic tropical-forest IPCC Tier 1 values (wood density ~600 kg/m3,
        // biomass expansion factor ~1.5, root-to-shoot ratio 0.37, carbon fraction 0.47) -
        // meant to be edited per species mix/region, not treated as exact.
        private double _woodDensity = 600;
        public double WoodDensity
        {
            get => _woodDensity;
            set => SetProperty(ref _woodDensity, value);
        }

        private double _biomassExpansionFactor = 1.5;
        public double BiomassExpansionFactor
        {
            get => _biomassExpansionFactor;
            set => SetProperty(ref _biomassExpansionFactor, value);
        }

        private double _rootShootRatio = 0.37;
        public double RootShootRatio
        {
            get => _rootShootRatio;
            set => SetProperty(ref _rootShootRatio, value);
        }

        private double _carbonFraction = 0.47;
        public double CarbonFraction
        {
            get => _carbonFraction;
            set => SetProperty(ref _carbonFraction, value);
        }

        private bool _isEstimatingBiomass;
        public bool IsEstimatingBiomass
        {
            get => _isEstimatingBiomass;
            set => SetProperty(ref _isEstimatingBiomass, value);
        }

        private string _biomassStatus = "";
        public string BiomassStatus
        {
            get => _biomassStatus;
            set => SetProperty(ref _biomassStatus, value);
        }

        // --- Slope from DEM ---
        private string _selectedDemLayer;
        public string SelectedDemLayer
        {
            get => _selectedDemLayer;
            set => SetProperty(ref _selectedDemLayer, value);
        }

        private bool _isComputingSlope;
        public bool IsComputingSlope
        {
            get => _isComputingSlope;
            set => SetProperty(ref _isComputingSlope, value);
        }

        private string _slopeStatus = "";
        public string SlopeStatus
        {
            get => _slopeStatus;
            set => SetProperty(ref _slopeStatus, value);
        }

        public ICommand RunDetectionCommand => new RelayCommand(async () => await RunDetectionAsync(), () => !IsRunning && SelectedRasterLayer != null);
        public ICommand RefreshLayersCommand => new RelayCommand(async () => await RefreshRasterLayersAsync(), () => !IsRunning);
        public ICommand CancelCommand => new RelayCommand(() => _runCts?.Cancel(), () => IsRunning);
        public ICommand TestKeyCommand => new RelayCommand(async () => await TestKeyAsync(), () => !IsTestingKey && !string.IsNullOrWhiteSpace(ApiKey));
        public ICommand CreateFishnetCommand => new RelayCommand(async () => await CreateFishnetAsync(), () => !IsCreatingFishnet && SelectedPolygonLayer != null);
        public ICommand ExportGpxCommand => new RelayCommand(async () => await ExportGpxAsync(), () => !IsExportingGpx && SelectedGpxLayer != null);
        public ICommand ImportExcelCommand => new RelayCommand(async () => await ImportExcelAsync(), () => !IsImportingExcel);
        public ICommand DownloadTemplateCommand => new RelayCommand(() => DownloadTemplate(), () => true);
        public ICommand DetectSliversCommand => new RelayCommand(async () => await DetectSliversAsync(), () => !IsDetectingSlivers && SelectedSliverLayer != null);
        public ICommand ImportPhotosCommand => new RelayCommand(async () => await ImportPhotosAsync(), () => !IsImportingPhotos);
        public ICommand EstimateBiomassCommand => new RelayCommand(async () => await EstimateBiomassAsync(), () => !IsEstimatingBiomass && SelectedBiomassLayer != null);
        public ICommand ComputeSlopeCommand => new RelayCommand(async () => await ComputeSlopeAsync(), () => !IsComputingSlope && SelectedDemLayer != null);

        protected override async void OnShow(bool isVisible)
        {
            if (!isVisible) return;
            await RefreshRasterLayersAsync();
        }

        private async Task RefreshRasterLayersAsync()
        {
            // Errors and the "nothing found" case both need to reach StatusText - this
            // runs from OnShow (an async void override the framework doesn't wrap for us),
            // so an unhandled exception here would otherwise vanish silently and the panel
            // would just look permanently empty with no clue why.
            try
            {
                if (MapView.Active == null)
                {
                    StatusText = "No active map view. Open a map first.";
                    return;
                }

                var (rasterNames, polygonNames, pointNames, polylineNames) = await QueuedTask.Run(() =>
                {
                    var layers = MapView.Active.Map.GetLayersAsFlattenedList();
                    var rasters = layers.OfType<RasterLayer>().Select(l => l.Name).ToList();
                    var polygons = layers.OfType<FeatureLayer>()
                        .Where(l => l.ShapeType == esriGeometryType.esriGeometryPolygon)
                        .Select(l => l.Name).ToList();
                    var points = layers.OfType<FeatureLayer>()
                        .Where(l => l.ShapeType == esriGeometryType.esriGeometryPoint)
                        .Select(l => l.Name).ToList();
                    var polylines = layers.OfType<FeatureLayer>()
                        .Where(l => l.ShapeType == esriGeometryType.esriGeometryPolyline)
                        .Select(l => l.Name).ToList();
                    return (rasters, polygons, points, polylines);
                });

                RasterLayers.Clear();
                foreach (var name in rasterNames)
                    RasterLayers.Add(name);

                PolygonLayers.Clear();
                foreach (var name in polygonNames)
                    PolygonLayers.Add(name);
                if (SelectedPolygonLayer == null || !PolygonLayers.Contains(SelectedPolygonLayer))
                    SelectedPolygonLayer = PolygonLayers.FirstOrDefault();
                if (SelectedSliverLayer == null || !PolygonLayers.Contains(SelectedSliverLayer))
                    SelectedSliverLayer = PolygonLayers.FirstOrDefault();

                PointLayers.Clear();
                foreach (var name in pointNames)
                    PointLayers.Add(name);
                if (SelectedBiomassLayer == null || !PointLayers.Contains(SelectedBiomassLayer))
                    SelectedBiomassLayer = PointLayers.FirstOrDefault();

                // GPX export accepts points, lines, and polygons (polygons get their boundary
                // converted to a line first) - polygons listed first since a TC/fishnet
                // boundary track is the more common use case than dropping individual points.
                GpxLayers.Clear();
                foreach (var name in polygonNames.Concat(polylineNames).Concat(pointNames))
                    GpxLayers.Add(name);
                if (SelectedGpxLayer == null || !GpxLayers.Contains(SelectedGpxLayer))
                    SelectedGpxLayer = GpxLayers.FirstOrDefault();

                if (SelectedDemLayer == null || !RasterLayers.Contains(SelectedDemLayer))
                    SelectedDemLayer = RasterLayers.FirstOrDefault();

                if (RasterLayers.Count == 0)
                {
                    StatusText = "No raster layers in the active map. Add one, then click Refresh.";
                }
                else if (SelectedRasterLayer == null || !RasterLayers.Contains(SelectedRasterLayer))
                {
                    SelectedRasterLayer = RasterLayers[0];
                    StatusText = "Ready.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to list raster layers: {ex.Message}";
            }
        }

        private async Task RunDetectionAsync()
        {
            IsRunning = true;
            Progress = 0;
            StatusText = $"Running detection ({SelectedProfile})...";
            _runCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    StatusText = "No active map view. Open a map first.";
                    return;
                }

                // Capture the target map now, not at completion time - the scan runs in the
                // background (async subprocess, no UI-thread blocking) so the user can switch
                // to a different map while it's running. MapView.Active at completion would
                // then point at whatever map they're looking at, not the one that was scanned.
                var map = MapView.Active.Map;

                var rasterPath = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList()
                        .OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedRasterLayer)
                        ?.GetPath()?.LocalPath);

                if (rasterPath == null)
                {
                    StatusText = "Raster layer not found - click Refresh and pick a layer again.";
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    StatusText = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }

                var fcName = $"TreeCounter_{(SelectedProfile == "Oil Palm Plantation" ? "palm" : "forest")}_{DateTime.Now:yyyyMMdd_HHmmss}";
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, fcName);

                var request = new DetectionRequest(
                    rasterPath, SelectedProfile, outputFc, Sigma, ExgThreshold, MinSmooth,
                    Provider: string.IsNullOrWhiteSpace(ApiKey) ? null : SelectedProvider.ToLowerInvariant(),
                    ApiKey: string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
                    Model: string.IsNullOrWhiteSpace(ApiKey) ? null : SelectedModel);

                var dispatcher = System.Windows.Application.Current.Dispatcher;
                var result = await PythonBackendService.RunDetectionAsync(
                    request,
                    pct => dispatcher.BeginInvoke(() => Progress = pct),
                    stage => dispatcher.BeginInvoke(() => StatusText = stage),
                    _runCts.Token);

                if (result.Cancelled)
                {
                    StatusText = "Cancelled.";
                }
                else if (result.Success)
                {
                    await AddResultLayerAsync(map, result.OutputFc, fcName);
                    LastTreeCount = result.TreeCount;
                    LastAreaHa = result.AreaHa;
                    StatusText = $"Done: {result.TreeCount} trees detected across {result.AreaHa:F1} ha scanned.";
                }
                else
                {
                    StatusText = $"Failed: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                // RelayCommand's execute delegate is fire-and-forget (async void semantics) -
                // without this catch, any exception here (e.g. Project.Current access,
                // QueuedTask failures) disappears with zero visible effect, which is exactly
                // the "I click it and nothing happens" symptom.
                StatusText = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                IsRunning = false;
            }
        }

        private async Task TestKeyAsync()
        {
            IsTestingKey = true;
            TestKeyStatus = "Testing...";
            try
            {
                var (ok, message) = await PythonBackendService.TestApiKeyAsync(
                    SelectedProvider.ToLowerInvariant(), ApiKey, SelectedModel);
                TestKeyStatus = (ok ? "OK: " : "Failed: ") + message;
            }
            catch (Exception ex)
            {
                TestKeyStatus = "Failed: " + ex.Message;
            }
            finally
            {
                IsTestingKey = false;
            }
        }

        // Reuses Esri's own CreateFishnet + PairwiseClip GP tools rather than building a
        // grid algorithm from scratch - CreateFishnet only covers the polygon's bounding
        // extent (not its actual shape), so the clip step afterward is still required.
        private async Task CreateFishnetAsync()
        {
            IsCreatingFishnet = true;
            FishnetStatus = "Creating fishnet...";
            try
            {
                if (MapView.Active == null)
                {
                    FishnetStatus = "No active map view. Open a map first.";
                    return;
                }
                if (CellWidth <= 0 || CellHeight <= 0)
                {
                    FishnetStatus = "Cell size must be greater than 0.";
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    FishnetStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }

                var map = MapView.Active.Map;
                var polyLayer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedPolygonLayer));
                if (polyLayer == null)
                {
                    FishnetStatus = "Polygon layer not found - click Refresh and pick again.";
                    return;
                }

                var (polyPath, extent) = await QueuedTask.Run(() =>
                    (polyLayer.GetPath()?.LocalPath, polyLayer.QueryExtent()));
                if (polyPath == null || extent == null || extent.IsEmpty)
                {
                    FishnetStatus = "Failed to read the polygon layer's extent.";
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fishnetFc = Path.Combine(project.DefaultGeodatabasePath, $"Fishnet_tmp_{stamp}");
                var clippedFc = Path.Combine(project.DefaultGeodatabasePath, $"Fishnet_{stamp}");

                // origin = bottom-left corner, y-axis point = any point straight above the
                // origin (defines "up" for the grid, not a distance), corner = top-right -
                // giving both origin and corner makes CreateFishnet ignore number_rows/columns.
                var origin = $"{extent.XMin} {extent.YMin}";
                var yAxisPoint = $"{extent.XMin} {extent.YMin + 10}";
                var corner = $"{extent.XMax} {extent.YMax}";

                // Without this, CreateFishnet's output has no spatial reference (ArcGIS Pro
                // then shows "Unknown Coordinate System" and won't draw it) - the tool
                // doesn't inherit the source polygon's CRS on its own, it has to be passed
                // as an explicit environment. Passing just RefreshProjectItems (Default minus
                // AddOutputsToMap) on all three calls stops ArcGIS Pro's "add output datasets
                // to map" behavior from auto-adding the throwaway fishnetFc (which flashed on
                // the map, then vanished once Delete removed it) and from double-adding the
                // clipped result we add ourselves below - GPExecuteToolFlags.None (dropping
                // both Default flags) throws a NullReferenceException deep inside Esri's own
                // execute_helper.eval_modal, so RefreshProjectItems has to stay set.
                //
                // If the source polygon itself has no defined coordinate system,
                // extent.SpatialReference comes back null - passing that straight into
                // MakeEnvironmentArray/ExecuteToolAsync throws a NullReferenceException deep
                // inside the GP plumbing (not a normal IGPResult failure), so it has to be
                // skipped rather than passed through.
                var noCrsWarning = extent.SpatialReference == null;
                var environments = noCrsWarning
                    ? null
                    : Geoprocessing.MakeEnvironmentArray(outputCoordinateSystem: extent.SpatialReference);

                // Named "cancelToken" argument forces the compiler to pick the plain
                // (string, values, environments, CancellationToken?, callback, flags) overload
                // instead of the one taking a CancelableProgressor - that one runs through
                // Esri's internal execute_helper.eval_modal, which throws a
                // NullReferenceException for this tool/argument combination on this install.
                var createResult = await Geoprocessing.ExecuteToolAsync("management.CreateFishnet",
                    Geoprocessing.MakeValueArray(fishnetFc, origin, yAxisPoint, CellWidth, CellHeight,
                        0, 0, corner, "NO_LABELS", "", "POLYGON"),
                    environments, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (createResult.IsFailed)
                {
                    FishnetStatus = $"Failed to create fishnet: {createResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                var clipResult = await Geoprocessing.ExecuteToolAsync("analysis.PairwiseClip",
                    Geoprocessing.MakeValueArray(fishnetFc, polyPath, clippedFc),
                    environments, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (clipResult.IsFailed)
                {
                    FishnetStatus = $"Failed to clip fishnet: {clipResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                await Geoprocessing.ExecuteToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(fishnetFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                var (cellCount, totalAreaHa) = await QueuedTask.Run(() =>
                {
                    var newLayer = LayerFactory.Instance.CreateLayer(new Uri(clippedFc), map, layerName: Path.GetFileName(clippedFc)) as FeatureLayer;
                    using var featureClass = newLayer?.GetFeatureClass();
                    if (featureClass == null) return (0, 0.0);

                    using var cursor = featureClass.Search(null, false);
                    int count = 0;
                    double totalArea = 0;
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        if (feature.GetShape() is Polygon poly)
                            totalArea += poly.Area;
                        count++;
                    }
                    return (count, totalArea / 10000.0);
                });

                FishnetStatus = $"Done: {cellCount} cells, {totalAreaHa:F2} ha total, clipped to \"{SelectedPolygonLayer}\"." +
                    (noCrsWarning ? " Warning: source polygon has no coordinate system defined - define its projection for accurate results." : "");
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(Path.GetTempPath(), "ForestryToolkit_fishnet_error.log");
                File.WriteAllText(logPath, ex.ToString());
                FishnetStatus = $"Unexpected error: {ex.Message} (full details: {logPath})";
            }
            finally
            {
                IsCreatingFishnet = false;
            }
        }

        // Uses Esri's own conversion.FeaturesToGPX GP tool - avoids hand-writing GPX XML.
        // GPX itself has no "filled area" concept (only waypoints and tracks), so a polygon
        // layer's boundary is converted to a line first (management.PolygonToLine) and that
        // line is what actually gets exported as a track - points/polylines go straight in
        // as waypoints/tracks. The resulting .gpx works directly with Garmin devices,
        // BaseCamp, or Garmin Connect (all just import a standard GPX file).
        private async Task ExportGpxAsync()
        {
            IsExportingGpx = true;
            GpxStatus = "Exporting...";
            string tempLineFc = null;
            try
            {
                if (MapView.Active == null)
                {
                    GpxStatus = "No active map view. Open a map first.";
                    return;
                }

                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedGpxLayer));
                if (layer == null)
                {
                    GpxStatus = "Layer not found - click Refresh and pick again.";
                    return;
                }

                var (sourcePath, shapeType) = await QueuedTask.Run(() => (layer.GetPath()?.LocalPath, layer.ShapeType));
                if (sourcePath == null)
                {
                    GpxStatus = "Failed to read the layer's data path.";
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    FileName = SelectedGpxLayer + ".gpx",
                    Filter = "GPX files (*.gpx)|*.gpx",
                    DefaultExt = ".gpx"
                };
                if (dialog.ShowDialog() != true)
                {
                    GpxStatus = "Cancelled.";
                    return;
                }

                var gpxSourcePath = sourcePath;
                if (shapeType == esriGeometryType.esriGeometryPolygon)
                {
                    var project = Project.Current;
                    if (project == null)
                    {
                        GpxStatus = "No open ArcGIS Pro project. Create or open one first.";
                        return;
                    }

                    GpxStatus = "Converting polygon boundary to a line...";
                    tempLineFc = Path.Combine(project.DefaultGeodatabasePath, $"GpxBoundary_tmp_{DateTime.Now:yyyyMMdd_HHmmss}");

                    var lineResult = await Geoprocessing.ExecuteToolAsync("management.PolygonToLine",
                        Geoprocessing.MakeValueArray(sourcePath, tempLineFc),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    if (lineResult.IsFailed)
                    {
                        GpxStatus = $"Failed to convert polygon boundary: {lineResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                        return;
                    }
                    gpxSourcePath = tempLineFc;
                }

                var result = await Geoprocessing.ExecuteToolAsync("conversion.FeaturesToGPX",
                    Geoprocessing.MakeValueArray(gpxSourcePath, dialog.FileName, "", "", "", ""),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (result.IsFailed)
                {
                    GpxStatus = $"Failed to export GPX: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                GpxStatus = $"Done: exported to {dialog.FileName}";
            }
            catch (Exception ex)
            {
                GpxStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                if (tempLineFc != null)
                {
                    await Geoprocessing.ExecuteToolAsync("management.Delete",
                        Geoprocessing.MakeValueArray(tempLineFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                }
                IsExportingGpx = false;
            }
        }

        // Reuses Esri's own ExcelToTable + XYTableToPoint GP tools rather than adding an
        // Excel-parsing library - reads the field crew's "DATA POHON" sheet (species,
        // diameter, height, volume, X/Y from GPS, etc.) straight into a point layer.
        // Coordinate system is fixed to WGS 1984 UTM Zone 50S (WKID 32750) to match how
        // this org's field GPS units and existing TC polygons are surveyed.
        private const int CruisingDataWkid = 32750;

        private async Task ImportExcelAsync()
        {
            IsImportingExcel = true;
            ImportExcelStatus = "";
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Excel files (*.xlsx;*.xls)|*.xlsx;*.xls",
                    Title = "Select Timber Cruising Excel File"
                };
                if (dialog.ShowDialog() != true)
                {
                    ImportExcelStatus = "Cancelled.";
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    ImportExcelStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }
                if (MapView.Active == null)
                {
                    ImportExcelStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;

                ImportExcelStatus = "Reading Excel sheet...";

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var tableFc = Path.Combine(project.DefaultGeodatabasePath, $"CruisingImport_tmp_{stamp}");
                var pointFc = Path.Combine(project.DefaultGeodatabasePath, $"CruisingTrees_{stamp}");

                // "TREE DATA" is this add-in's own English template sheet name; "DATA POHON"
                // is kept as a fallback for older cruising exports already using that sheet
                // name from before the template existed.
                IGPResult tableResult = null;
                foreach (var sheetName in new[] { "TREE DATA", "DATA POHON" })
                {
                    tableResult = await Geoprocessing.ExecuteToolAsync("conversion.ExcelToTable",
                        Geoprocessing.MakeValueArray(dialog.FileName, tableFc, sheetName),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    if (!tableResult.IsFailed) break;
                }
                if (tableResult.IsFailed)
                {
                    ImportExcelStatus = $"Failed to read Excel (expected a \"TREE DATA\" sheet): " +
                        $"{tableResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                ImportExcelStatus = "Converting X/Y to points...";

                var pointResult = await Geoprocessing.ExecuteToolAsync("management.XYTableToPoint",
                    Geoprocessing.MakeValueArray(tableFc, pointFc, "X", "Y", "", CruisingDataWkid),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (pointResult.IsFailed)
                {
                    ImportExcelStatus = $"Failed to convert to points: {pointResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                await Geoprocessing.ExecuteToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(tableFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                var (treeCount, totalVolume) = await QueuedTask.Run(() =>
                {
                    var newLayer = LayerFactory.Instance.CreateLayer(new Uri(pointFc), map, layerName: Path.GetFileName(pointFc)) as FeatureLayer;
                    using var featureClass = newLayer?.GetFeatureClass();
                    if (featureClass == null) return (0, 0.0);

                    // Field-name matched loosely ("StartsWith") rather than exact "Volume" -
                    // ExcelToTable sanitizes headers like "Volume (m3)" into names such as
                    // "Volume_m3_", and this has to work for both that and a bare "Volume".
                    var volumeField = featureClass.GetDefinition().GetFields()
                        .FirstOrDefault(f => f.Name.StartsWith("Volume", StringComparison.OrdinalIgnoreCase))?.Name;

                    using var cursor = featureClass.Search(null, false);
                    int count = 0;
                    double totalVol = 0;
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        count++;
                        if (volumeField != null && feature[volumeField] is double v)
                            totalVol += v;
                    }
                    return (count, totalVol);
                });

                ImportExcelStatus = $"Done: imported {treeCount} trees, {totalVolume:F2} m3 total volume.";
            }
            catch (Exception ex)
            {
                ImportExcelStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsImportingExcel = false;
            }
        }

        // Sliver = a polygon that's technically valid geometry but visually just a thin
        // leftover fragment (e.g. a fishnet cell clipped along a near-tangent boundary edge,
        // or a stray degenerate ring left over in a source shapefile). ArcGIS has no
        // dedicated "sliver detector" tool. Two auto-calibrated signals, either one enough
        // to flag a part:
        //  - Area far below this layer's own median part size (catches small fragments) -
        //    but a long, thin sliver can have a middling area, so area alone isn't enough.
        //  - Thinness ratio (4*pi*Area/Perimeter^2, 1=circle, ->0 as a shape thins out) far
        //    below this layer's own median thinness - catches long/thin shapes regardless
        //    of their absolute size.
        // Computed per PART (Polygon.Parts), not per feature - summing Area/Length across a
        // multipart feature's disjoint rings distorts the thinness ratio and can hide a
        // genuinely thin ring inside an otherwise normal multi-island feature.
        private const double SliverAreaFraction = 0.1;
        private const double SliverThinnessFraction = 0.5;

        private async Task DetectSliversAsync()
        {
            IsDetectingSlivers = true;
            SliverStatus = "Scanning...";
            try
            {
                if (MapView.Active == null)
                {
                    SliverStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedSliverLayer));
                if (layer == null)
                {
                    SliverStatus = "Layer not found - click Refresh and pick again.";
                    return;
                }

                var (oids, totalArea, medianAreaHa, areaThresholdHa, medianThinness, thinnessThreshold) = await QueuedTask.Run(() =>
                {
                    using var featureClass = layer.GetFeatureClass();
                    using var cursor = featureClass.Search(null, false);
                    var parts = new List<(long Oid, double Area, double Thinness)>();
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        if (feature.GetShape() is not Polygon poly) continue;

                        foreach (var part in poly.Parts)
                        {
                            if (new PolygonBuilderEx(part, AttributeFlags.None, poly.SpatialReference).ToGeometry() is not Polygon ring
                                || ring.Length <= 0)
                                continue;
                            var thinness = 4 * Math.PI * ring.Area / (ring.Length * ring.Length);
                            parts.Add((feature.GetObjectID(), ring.Area, thinness));
                        }
                    }
                    if (parts.Count == 0)
                        return (new List<long>(), 0.0, 0.0, 0.0, 0.0, 0.0);

                    double Median(IEnumerable<double> values)
                    {
                        var sorted = values.OrderBy(v => v).ToList();
                        var mid = sorted.Count / 2;
                        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
                    }

                    var medianArea = Median(parts.Select(p => p.Area));
                    var medianThin = Median(parts.Select(p => p.Thinness));
                    var areaThreshold = medianArea * SliverAreaFraction;
                    var thinThreshold = medianThin * SliverThinnessFraction;

                    var flagged = parts.Where(p => p.Area < areaThreshold || p.Thinness < thinThreshold).ToList();
                    var flaggedOids = flagged.Select(p => p.Oid).Distinct().ToList();

                    return (flaggedOids, flagged.Sum(p => p.Area), medianArea / 10000.0, areaThreshold / 10000.0, medianThin, thinThreshold);
                });

                if (oids.Count == 0)
                {
                    SliverStatus = $"No slivers found (median part {medianAreaHa:F4} ha / thinness {medianThinness:F2}; " +
                        $"flagging under {areaThresholdHa:F4} ha or thinness {thinnessThreshold:F2}).";
                    return;
                }

                await QueuedTask.Run(() =>
                {
                    var oidField = layer.GetFeatureClass().GetDefinition().GetObjectIDField();
                    layer.Select(new QueryFilter { WhereClause = $"{oidField} IN ({string.Join(",", oids)})" },
                        SelectionCombinationMethod.New);
                });

                SliverStatus = $"Found {oids.Count} sliver polygon(s) (area < {areaThresholdHa:F4} ha or thinness < {thinnessThreshold:F2}), " +
                    $"{totalArea / 10000.0:F4} ha total - selected on map.";
            }
            catch (Exception ex)
            {
                SliverStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsDetectingSlivers = false;
            }
        }

        // Reuses Esri's own GeotagPhotos GP tool - reads GPS EXIF data straight from field
        // photo JPEGs and creates a point per photo with the photo auto-attached (ArcGIS
        // Pro's built-in feature attachments), so clicking a point in the default pop-up
        // shows/enlarges the photo without any custom viewer code. GeotagPhotos itself only
        // accepts a whole folder, not a file list - so specific photos picked here are first
        // copied into a throwaway staging folder, which is deleted again afterward. This is
        // a one-time snapshot import, not a watched folder: photos added to the source
        // folder later need a fresh Import Photos... run, they won't appear automatically.
        private async Task ImportPhotosAsync()
        {
            IsImportingPhotos = true;
            PhotoImportStatus = "";
            string stagingDir = null;
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select geotagged field photos",
                    Filter = "Photos (*.jpg;*.jpeg)|*.jpg;*.jpeg|All files (*.*)|*.*",
                    Multiselect = true
                };
                if (dialog.ShowDialog() != true)
                {
                    PhotoImportStatus = "Cancelled.";
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    PhotoImportStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }
                if (MapView.Active == null)
                {
                    PhotoImportStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                stagingDir = Path.Combine(Path.GetTempPath(), $"ForestryToolkit_photos_{stamp}");
                Directory.CreateDirectory(stagingDir);
                foreach (var file in dialog.FileNames)
                    File.Copy(file, Path.Combine(stagingDir, Path.GetFileName(file)), overwrite: true);

                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"FieldPhotos_{stamp}");

                PhotoImportStatus = "Reading photo GPS tags...";

                var result = await Geoprocessing.ExecuteToolAsync("management.GeotagPhotos",
                    Geoprocessing.MakeValueArray(stagingDir, outputFc),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (result.IsFailed)
                {
                    PhotoImportStatus = $"Failed to geotag photos: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                var count = await QueuedTask.Run(() =>
                {
                    var newLayer = LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: Path.GetFileName(outputFc)) as FeatureLayer;
                    using var featureClass = newLayer?.GetFeatureClass();
                    return featureClass?.GetCount() ?? 0;
                });

                PhotoImportStatus = $"Done: {count} geotagged photo(s) added with attachments - click a point on the map to view its photo.";
            }
            catch (Exception ex)
            {
                PhotoImportStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                if (stagingDir != null && Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
                IsImportingPhotos = false;
            }
        }

        // Volume-based (BEF/IPCC Tier 1 style) biomass estimate rather than a diameter/height
        // allometric equation - the cruising Excel already has per-tree Volume from this
        // org's own local volume tables, which is more reliable than a generic species-blind
        // allometric formula. Defaults are generic tropical-forest values (see property
        // comments); this is an approximation meant to be tuned per species mix/region, not
        // a substitute for a proper carbon inventory.
        private async Task EstimateBiomassAsync()
        {
            IsEstimatingBiomass = true;
            BiomassStatus = "Calculating...";
            try
            {
                if (MapView.Active == null)
                {
                    BiomassStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedBiomassLayer));
                if (layer == null)
                {
                    BiomassStatus = "Layer not found - click Refresh and pick again.";
                    return;
                }

                var totalVolume = await QueuedTask.Run(() =>
                {
                    using var featureClass = layer.GetFeatureClass();
                    var volumeField = featureClass.GetDefinition().GetFields()
                        .FirstOrDefault(f => f.Name.StartsWith("Volume", StringComparison.OrdinalIgnoreCase))?.Name;
                    if (volumeField == null) return (double?)null;

                    using var cursor = featureClass.Search(null, false);
                    double sum = 0;
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        if (feature[volumeField] is double v)
                            sum += v;
                    }
                    return sum;
                });

                if (totalVolume == null)
                {
                    BiomassStatus = "Layer has no Volume field - import cruising data with volume first.";
                    return;
                }

                var aboveGroundBiomassKg = totalVolume.Value * WoodDensity * BiomassExpansionFactor;
                var totalBiomassKg = aboveGroundBiomassKg * (1 + RootShootRatio);
                var carbonKg = totalBiomassKg * CarbonFraction;
                var co2eKg = carbonKg * 3.667;

                BiomassStatus = $"Done: {totalVolume.Value:F1} m3 -> {totalBiomassKg / 1000:F2} t biomass, " +
                    $"{carbonKg / 1000:F2} t carbon, {co2eKg / 1000:F2} t CO2e.";
            }
            catch (Exception ex)
            {
                BiomassStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsEstimatingBiomass = false;
            }
        }

        // Reuses Esri's Spatial Analyst sa.Slope GP tool - requires a Spatial Analyst
        // license; if unavailable, the tool fails and that failure surfaces via the normal
        // IsFailed/ErrorMessages path below like any other GP failure in this file.
        private async Task ComputeSlopeAsync()
        {
            IsComputingSlope = true;
            SlopeStatus = "Computing slope...";
            try
            {
                var project = Project.Current;
                if (project == null)
                {
                    SlopeStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }
                if (MapView.Active == null)
                {
                    SlopeStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;
                var demLayer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedDemLayer));
                if (demLayer == null)
                {
                    SlopeStatus = "DEM layer not found - click Refresh and pick again.";
                    return;
                }

                var (demPath, bandCount) = await QueuedTask.Run(() =>
                    (demLayer.GetPath()?.LocalPath, demLayer.GetRaster()?.GetBandCount() ?? 0));
                if (demPath == null)
                {
                    SlopeStatus = "Failed to read the DEM layer's data path.";
                    return;
                }
                // A DEM is a single-band elevation surface (pixel value = meters of
                // height) - a multi-band raster is almost certainly an RGB/multispectral
                // orthophoto, not elevation. Running Slope on that reads color values as
                // if they were height, producing meaningless output with no error from the
                // GP tool itself (it doesn't know the data's semantic meaning, only its
                // shape) - this is a plain band-count check, not a real DEM validator.
                if (bandCount > 1)
                {
                    SlopeStatus = $"\"{SelectedDemLayer}\" has {bandCount} bands - that's an RGB/multispectral image, not a DEM. " +
                        "Pick a single-band elevation raster instead.";
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var slopeRaster = Path.Combine(project.DefaultGeodatabasePath, $"Slope_{stamp}");

                var result = await Geoprocessing.ExecuteToolAsync("sa.Slope",
                    Geoprocessing.MakeValueArray(demPath, slopeRaster, "PERCENT_RISE", 1),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (result.IsFailed)
                {
                    SlopeStatus = $"Failed to compute slope: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                await QueuedTask.Run(() =>
                    LayerFactory.Instance.CreateLayer(new Uri(slopeRaster), map, layerName: Path.GetFileName(slopeRaster)));

                SlopeStatus = "Done: slope raster (% rise) added to map - see Symbology pane for accessibility classification.";
            }
            catch (Exception ex)
            {
                SlopeStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsComputingSlope = false;
            }
        }

        // The template ships as a static file (Templates\TreeCruisingTemplate.xlsx) rather
        // than being generated on demand - same reasoning as backend/ in deploy.ps1:
        // resolved via BaseDirectory since after install this runs from
        // %LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{guid}\, not the repo checkout.
        private void DownloadTemplate()
        {
            try
            {
                var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "TreeCruisingTemplate.xlsx");
                if (!File.Exists(bundledPath))
                {
                    TemplateStatus = "Template file missing from add-in install - reinstall the add-in.";
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    FileName = "TreeCruisingTemplate.xlsx",
                    Filter = "Excel files (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx"
                };
                if (dialog.ShowDialog() != true)
                {
                    TemplateStatus = "Cancelled.";
                    return;
                }

                File.Copy(bundledPath, dialog.FileName, overwrite: true);
                TemplateStatus = $"Done: saved to {dialog.FileName}";
            }
            catch (Exception ex)
            {
                TemplateStatus = $"Unexpected error: {ex.Message}";
            }
        }

        private async Task AddResultLayerAsync(Map map, string outputFc, string layerName)
        {
            await QueuedTask.Run(() =>
            {
                if (map == null) return;

                if (LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: layerName) is not FeatureLayer layer)
                    return;

                var color = SelectedProfile == "Oil Palm Plantation"
                    ? ColorFactory.Instance.CreateRGBColor(230, 76, 60)
                    : ColorFactory.Instance.CreateRGBColor(39, 174, 96);
                var symbol = SymbolFactory.Instance.ConstructPointSymbol(color, 6, SimpleMarkerStyle.Circle);
                layer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
            });
        }
    }

    internal class TreeCounterDockpaneShowButton : Button
    {
        protected override void OnClick() => TreeCounterDockpaneViewModel.Show();
    }
}
