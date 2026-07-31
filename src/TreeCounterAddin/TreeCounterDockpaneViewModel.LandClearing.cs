using ArcGIS.Core.Data;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Ported from qgis_plugin/tree_counter/detector.py's detect_land_clearing - see
    // backend/land_clearing.py for the algorithm and why it's a raster-mask + GP-tool-
    // vectorize port instead of the QGIS original's GDAL/OGR polygonizer. Detection itself
    // runs as a Python subprocess (same PythonBackendService pattern as Tree Detection),
    // not a GP tool chain, since the ExG scan itself is the same numpy/scipy pipeline.
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedLandClearingRasterLayer;
        public string SelectedLandClearingRasterLayer
        {
            get => _selectedLandClearingRasterLayer;
            set => SetProperty(ref _selectedLandClearingRasterLayer, value);
        }

        // Optional - left unselected (null) means "don't exclude anything". Reuses
        // PolygonLayers (already refreshed by RefreshRasterLayersAsync) rather than a
        // second collection, since there's nothing land-clearing-specific about the list
        // of candidate polygon layers itself.
        private string _selectedExcludeAreaLayer;
        public string SelectedExcludeAreaLayer
        {
            get => _selectedExcludeAreaLayer;
            set => SetProperty(ref _selectedExcludeAreaLayer, value);
        }

        // 0.01 ha = 100 m2, matching the QGIS plugin's own default min_area_m2.
        private double _minClearingAreaHa = 0.01;
        public double MinClearingAreaHa
        {
            get => _minClearingAreaHa;
            set => SetProperty(ref _minClearingAreaHa, value);
        }

        private bool _isDetectingLandClearing;
        public bool IsDetectingLandClearing
        {
            get => _isDetectingLandClearing;
            set => SetProperty(ref _isDetectingLandClearing, value);
        }

        private int _landClearingProgress;
        public int LandClearingProgress
        {
            get => _landClearingProgress;
            set => SetProperty(ref _landClearingProgress, value);
        }

        private string _landClearingStatus = "";
        public string LandClearingStatus
        {
            get => _landClearingStatus;
            set => SetProperty(ref _landClearingStatus, value);
        }

        private CancellationTokenSource _landClearingCts;

        public ICommand DetectLandClearingCommand => new RelayCommand(async () => await DetectLandClearingAsync(),
            () => !IsDetectingLandClearing && SelectedLandClearingRasterLayer != null);
        public ICommand CancelLandClearingCommand => new RelayCommand(() => _landClearingCts?.Cancel(), () => IsDetectingLandClearing);

        private async Task DetectLandClearingAsync()
        {
            IsDetectingLandClearing = true;
            LandClearingProgress = 0;
            LandClearingStatus = "Scanning for cleared/bare ground...";
            _landClearingCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    LandClearingStatus = "No active map view. Open a map first.";
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    LandClearingStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }

                var map = MapView.Active.Map;
                var (rasterPath, excludeFcPath) = await QueuedTask.Run(() =>
                {
                    var layers = map.GetLayersAsFlattenedList();
                    var raster = layers.OfType<RasterLayer>().FirstOrDefault(l => l.Name == SelectedLandClearingRasterLayer)
                        ?.GetPath()?.LocalPath;
                    var exclude = SelectedExcludeAreaLayer == null ? null
                        : layers.OfType<FeatureLayer>().FirstOrDefault(l => l.Name == SelectedExcludeAreaLayer)?.GetPath()?.LocalPath;
                    return (raster, exclude);
                });

                if (rasterPath == null)
                {
                    LandClearingStatus = "Raster layer not found - click Refresh and pick a layer again.";
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"LandClearing_{stamp}");

                var request = new LandClearingRequest(
                    rasterPath, outputFc, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX,
                    MinClearingAreaHa * 10000.0, excludeFcPath);

                var dispatcher = System.Windows.Application.Current.Dispatcher;
                var result = await PythonBackendService.RunLandClearingAsync(
                    request,
                    pct => dispatcher.BeginInvoke(() => LandClearingProgress = pct),
                    stage => dispatcher.BeginInvoke(() => LandClearingStatus = stage),
                    _landClearingCts.Token);

                if (result.Cancelled)
                {
                    LandClearingStatus = "Cancelled.";
                }
                else if (result.Success)
                {
                    await QueuedTask.Run(() =>
                    {
                        if (LayerFactory.Instance.CreateLayer(new Uri(result.OutputFc), map, layerName: Path.GetFileName(result.OutputFc)) is not FeatureLayer newLayer)
                            return;
                        var symbol = SymbolFactory.Instance.ConstructPolygonSymbol(
                            ColorFactory.Instance.CreateRGBColor(200, 120, 60, 60), SimpleFillStyle.Solid,
                            SymbolFactory.Instance.ConstructStroke(ColorFactory.Instance.CreateRGBColor(160, 90, 40), 1.5));
                        newLayer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
                    });
                    LandClearingStatus = result.PolygonCount == 0
                        ? "Done: no cleared/bare areas found above the minimum area."
                        : $"Done: {result.PolygonCount} cleared area(s) found, {result.AreaHa:F2} ha total.";
                }
                else
                {
                    LandClearingStatus = $"Failed: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                LandClearingStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                _landClearingCts?.Dispose();
                _landClearingCts = null;
                IsDetectingLandClearing = false;
            }
        }

        // Matches backend/land_clearing.py's own defaults - not exposed as UI sliders yet
        // (unlike Tree Detection's Advanced Parameters), since there's no field-tested
        // reason yet to need per-run tuning here. Add controls if that changes.
        private const double DEFAULT_EXG_THRESHOLD = 18;
        private const double DEFAULT_SMOOTH_PX = 3;
    }
}
