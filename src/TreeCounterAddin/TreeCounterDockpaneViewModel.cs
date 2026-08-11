using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Mapping.Events;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Split into partial-class files by feature area - see
    // TreeCounterDockpaneViewModel.TreeDetection.cs, .Fishnet.cs, .Gpx.cs,
    // .ExcelImport.cs, .SliverDetection.cs, .PhotoImport.cs, .Biomass.cs, and .Slope.cs.
    // This file holds only the DockPane boilerplate and the shared layer-list refresh
    // that every feature's combo boxes depend on.
    internal partial class TreeCounterDockpaneViewModel : DockPane
    {
        private const string DockPaneId = "TreeCounterAddin_Dockpane";

        // Single source of truth for the version string shown in both the ribbon's About
        // button (RibbonControls.cs) and the panel's own About tab - keep this in sync with
        // Config.daml's AddInInfo version by hand (DAML has no runtime-readable API for it
        // that's simpler than just duplicating the literal).
        public const string AppVersion = "0.1.0";

        public TreeCounterDockpaneViewModel()
        {
            // Load previously-saved keys (DPAPI-encrypted, see ApiKeyStore) so the user
            // isn't stuck retyping API keys every time they reopen ArcGIS Pro.
            foreach (var kv in ApiKeyStore.Load())
                _apiKeysByProvider[kv.Key] = kv.Value;
            if (_apiKeysByProvider.TryGetValue(_selectedProvider, out var savedKey))
                _apiKey = savedKey; // set the backing field directly - no save-on-load, no UI to notify yet

            // Same reasoning as the API keys above - set backing fields directly (declared in
            // the Fishnet/ExcelImport/Biomass partial files, reachable here since partial
            // classes share one member set) rather than the properties, so loading a saved
            // value doesn't immediately re-trigger a save.
            var settings = SettingsStore.Load();
            _cellWidth = settings.CellWidth;
            _cellHeight = settings.CellHeight;
            _cruisingWkid = settings.CruisingWkid;
            _woodDensity = settings.WoodDensity;
            _biomassExpansionFactor = settings.BiomassExpansionFactor;
            _rootShootRatio = settings.RootShootRatio;
            _carbonFraction = settings.CarbonFraction;

            // Reverse-lookup the saved WKID back to its zone label so the dropdown shows the
            // right zone on restart instead of resetting to the "WGS 1984 UTM Zone 50S"
            // default - falls back to "Other" if it's a WKID outside the Indonesian UTM list.
            var savedZone = Array.Find(IndonesianUtmZones, z => z.Wkid == _cruisingWkid);
            _selectedUtmZoneLabel = savedZone.Label ?? OtherZoneLabel;
            _isOtherZoneSelected = savedZone.Label == null;

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

            // Auto-refresh the layer lists instead of relying on the user to remember to
            // click Refresh - OnShow already covers "panel just opened", but that alone
            // missed the common case of the panel already being pinned open from a saved
            // layout (no OnShow(true) fires again on the next launch) or a layer being
            // added/removed while the panel stays open (e.g. loading a new orthophoto, or
            // this add-in's own result layers landing on the map after a detection run).
            // This DockPane instance lives for the whole ArcGIS Pro session (DockPaneManager
            // keeps one singleton, never disposed early), so subscribing once here without
            // ever unsubscribing is the standard pattern - same as RibbonStateChanged above.
            ActiveMapViewChangedEvent.Subscribe(_ => OnLayersOrMapChanged());
            LayersAddedEvent.Subscribe(_ => OnLayersOrMapChanged());
            LayersRemovedEvent.Subscribe(_ => OnLayersOrMapChanged());
        }

        private async void OnLayersOrMapChanged() => await RefreshRasterLayersAsync();

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
        public ObservableCollection<string> PolygonLayers { get; } = new();
        public ObservableCollection<string> PointLayers { get; } = new();
        public ObservableCollection<string> GpxLayers { get; } = new();

        public ICommand RefreshLayersCommand => new RelayCommand(async () => await RefreshRasterLayersAsync(), () => !IsRunning);

        // Called from each relevant property setter (Fishnet cell size, cruising WKID,
        // biomass constants) so a change is saved immediately - same on-every-set pattern
        // ApiKey already uses for API keys, just without encryption since none of these
        // values are secret.
        private void SaveSettings() => SettingsStore.Save(new SettingsStore.Settings
        {
            CellWidth = CellWidth,
            CellHeight = CellHeight,
            CruisingWkid = CruisingWkid,
            WoodDensity = WoodDensity,
            BiomassExpansionFactor = BiomassExpansionFactor,
            RootShootRatio = RootShootRatio,
            CarbonFraction = CarbonFraction,
        });

        protected override async void OnShow(bool isVisible)
        {
            if (!isVisible) return;
            await RefreshRasterLayersAsync();
        }

        // Shared across every feature's layer combo box - populates RasterLayers,
        // PolygonLayers, PointLayers, and GpxLayers in one map scan, and defaults each
        // feature's Selected* layer if it's unset or no longer present in the map.
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

                var (rasterNames, polygonNames, pointNames, polylineNames, allLayers) = await QueuedTask.Run(() =>
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
                    var all = layers.Select(l => (l.Name, l.IsVisible)).ToList();
                    return (rasters, polygons, points, polylines, all);
                });

                SyncFavorites(allLayers);

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
                if (SelectedBufferPlanLayer == null || !PolygonLayers.Contains(SelectedBufferPlanLayer))
                    SelectedBufferPlanLayer = PolygonLayers.FirstOrDefault();

                PointLayers.Clear();
                foreach (var name in pointNames)
                    PointLayers.Add(name);
                if (SelectedBiomassLayer == null || !PointLayers.Contains(SelectedBiomassLayer))
                    SelectedBiomassLayer = PointLayers.FirstOrDefault();
                if (SelectedReportLayer == null || !PointLayers.Contains(SelectedReportLayer))
                    SelectedReportLayer = PointLayers.FirstOrDefault();

                // GPX export accepts points, lines, and polygons (polygons get their boundary
                // converted to a line first) - polygons listed first since a TC/fishnet
                // boundary track is the more common use case than dropping individual points.
                GpxLayers.Clear();
                foreach (var name in polygonNames.Concat(polylineNames).Concat(pointNames))
                    GpxLayers.Add(name);
                if (SelectedGpxLayer == null || !GpxLayers.Contains(SelectedGpxLayer))
                    SelectedGpxLayer = GpxLayers.FirstOrDefault();
                if (SelectedRiverLayer == null || !GpxLayers.Contains(SelectedRiverLayer))
                    SelectedRiverLayer = GpxLayers.FirstOrDefault();

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

        // Sets a per-feature popup title (e.g. "{FileName}") instead of ArcGIS Pro's default
        // "OBJECTID: 5" - titleExpression uses the standard Pro popup {FieldName} templating
        // syntax. Must run on the MCT (QueuedTask). Best-effort: a layer with attachments
        // still shows its photo in the default popup even if this fails, so failures here
        // are swallowed rather than surfaced as a feature-breaking error.
        private static void SetPopupTitle(FeatureLayer layer, string titleExpression)
        {
            try
            {
                if (layer.GetDefinition() is not CIMFeatureLayer definition) return;
                definition.PopupInfo ??= new CIMPopupInfo();
                definition.PopupInfo.Title = titleExpression;
                definition.ShowPopups = true;
                layer.SetDefinition(definition);
            }
            catch
            {
                // Best-effort cosmetic touch - the layer/attachments still work without it.
            }
        }
    }

    internal class TreeCounterDockpaneShowButton : Button
    {
        protected override void OnClick() => TreeCounterDockpaneViewModel.Show();
    }
}
