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

        // Bindable now (Settings tab > Land Clearing Parameters) rather than the hardcoded
        // DEFAULT_EXG_THRESHOLD/DEFAULT_SMOOTH_PX/OPENING_ITERATIONS/CLOSING_ITERATIONS
        // constants this used to always pass through unchanged - a real report (2026-08-16)
        // found the default smoothing (tuned against one specific site's ground truth) gave
        // a rougher, more fragmented result on a different site's imagery than the same
        // detection in QGIS, with narrower real clearings eroded away entirely. Defaults here
        // match backend/land_clearing.py's own module defaults, so an unchanged panel behaves
        // exactly as before.
        //
        // ExG default raised 18 -> 26 (2026-08-17) - see land_clearing.py's own
        // DEFAULT_EXG_THRESHOLD comment for the real 721-sample Color Reference Sampler
        // dataset this came from (recall 48.2% -> 92.7%, precision also improved).
        private double _landClearingExgThreshold = 26;
        public double LandClearingExgThreshold
        {
            get => _landClearingExgThreshold;
            set => SetProperty(ref _landClearingExgThreshold, value);
        }

        private double _landClearingSmoothPx = 3;
        public double LandClearingSmoothPx
        {
            get => _landClearingSmoothPx;
            set => SetProperty(ref _landClearingSmoothPx, value);
        }

        // Erosion pass that strips small false "cleared" specks - lower keeps narrower real
        // clearings from being eroded away entirely instead of just removing noise.
        private int _landClearingOpeningIterations = 6;
        public int LandClearingOpeningIterations
        {
            get => _landClearingOpeningIterations;
            set => SetProperty(ref _landClearingOpeningIterations, value);
        }

        // Dilation pass that fills small gaps/merges nearby fragments - higher gives
        // smoother, more human-digitization-like boundaries instead of many small blobs.
        private int _landClearingClosingIterations = 15;
        public int LandClearingClosingIterations
        {
            get => _landClearingClosingIterations;
            set => SetProperty(ref _landClearingClosingIterations, value);
        }

        // Also requires bright + reddish raw RGB - the ExG-only threshold reads roads/rivers
        // as "cleared" too since they also have low greenness, but they're darker/bluer than
        // freshly bared soil. Off by default (matches the QGIS original) - a real accuracy
        // check found it a more targeted false-positive filter than the opening/closing
        // smoothing above for roads/rivers specifically inflating results.
        private bool _landClearingFreshColor;
        public bool LandClearingFreshColor
        {
            get => _landClearingFreshColor;
            set => SetProperty(ref _landClearingFreshColor, value);
        }

        private double _landClearingBrightMin = 120;
        public double LandClearingBrightMin
        {
            get => _landClearingBrightMin;
            set => SetProperty(ref _landClearingBrightMin, value);
        }

        // Fills small interior holes (vegetation patches left standing inside an otherwise-
        // cleared area) at the vector level, after RasterToPolygon - the missing piece found
        // 2026-08-16 comparing this port against the original QGIS plugin's "Isi lubang"
        // control (same 2000 m2 default), which is what actually gives QGIS's result its
        // solid, human-digitization look regardless of site, more than the pixel-level
        // opening/closing above does on its own.
        private double _landClearingFillHoleAreaM2 = 2000;
        public double LandClearingFillHoleAreaM2
        {
            get => _landClearingFillHoleAreaM2;
            set => SetProperty(ref _landClearingFillHoleAreaM2, value);
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
            LandClearingStatus = Tr("Scanning for cleared/bare ground...", "Memindai bukaan/tanah terbuka...");
            _landClearingCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    LandClearingStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    LandClearingStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
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
                    LandClearingStatus = Tr("Raster layer not found - click Refresh and pick a layer again.",
                        "Layer raster tidak ditemukan - klik Refresh dan pilih layer lagi.");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"LandClearing_{stamp}");

                // Provider/ApiKey/Model/UseAiValidation are the same Settings-tab AI Vision
                // Validation fields Tree Detection uses (TreeDetection.cs) - shared across
                // both features.
                var aiEnabled = UseAiValidation && !string.IsNullOrWhiteSpace(ApiKey);
                var request = new LandClearingRequest(
                    rasterPath, outputFc, LandClearingExgThreshold, LandClearingSmoothPx,
                    MinClearingAreaHa * 10000.0, excludeFcPath,
                    LandClearingOpeningIterations, LandClearingClosingIterations,
                    LandClearingFreshColor, LandClearingBrightMin, LandClearingFillHoleAreaM2,
                    Provider: aiEnabled ? SelectedProvider.ToLowerInvariant() : null,
                    ApiKey: aiEnabled ? ApiKey : null,
                    Model: aiEnabled ? SelectedModel : null);

                var dispatcher = System.Windows.Application.Current.Dispatcher;
                var result = await PythonBackendService.RunLandClearingAsync(
                    request,
                    pct => dispatcher.BeginInvoke(() => LandClearingProgress = pct),
                    stage => dispatcher.BeginInvoke(() => LandClearingStatus = stage),
                    _landClearingCts.Token);

                if (result.Cancelled)
                {
                    LandClearingStatus = Tr("Cancelled.", "Dibatalkan.");
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
                    // Same explicit-even-at-zero AI confirmation as Tree Detection - see its
                    // comment for why (a silently-failed key looks identical to "AI found
                    // nothing wrong" without this).
                    var aiNote = aiEnabled
                        ? Tr($" (Validated with {SelectedProvider} - {result.RejectedByAiCount} rejected.)",
                             $" (Divalidasi dengan {SelectedProvider} - {result.RejectedByAiCount} ditolak.)") : "";
                    LandClearingStatus = Tr(
                        result.PolygonCount == 0
                            ? "Done: no cleared/bare areas found above the minimum area."
                            : $"Done: {result.PolygonCount} cleared area(s) found, {result.AreaHa:F2} ha total.",
                        result.PolygonCount == 0
                            ? "Selesai: tidak ada area bukaan/terbuka di atas luas minimum."
                            : $"Selesai: {result.PolygonCount} area bukaan ditemukan, total {result.AreaHa:F2} ha.") +
                        aiNote;
                }
                else
                {
                    LandClearingStatus = Tr($"Failed: {result.ErrorMessage}", $"Gagal: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                LandClearingStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _landClearingCts?.Dispose();
                _landClearingCts = null;
                IsDetectingLandClearing = false;
            }
        }
    }
}
