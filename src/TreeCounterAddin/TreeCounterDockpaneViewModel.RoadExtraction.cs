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
    // Road/trail centerline extraction - reuses land_clearing.py's bare-ground mask
    // (roads read as "cleared" too) skeletonized down to a centerline, then vectorized
    // with arcpy's own RasterToPolyline GP tool - see backend/road_extraction.py.
    // Same PythonBackendService subprocess pattern as Land Clearing Detection.
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedRoadRasterLayer;
        public string SelectedRoadRasterLayer
        {
            get => _selectedRoadRasterLayer;
            set => SetProperty(ref _selectedRoadRasterLayer, value);
        }

        // 5m - drops the short dangling stubs skeletonize tends to leave at noisy
        // mask edges, without needing a whole separate min-area-style cleanup pass
        // (RasterToPolyline's own minimum_dangle_length parameter already does this).
        private double _minDangleM = 5.0;
        public double MinDangleM
        {
            get => _minDangleM;
            set => SetProperty(ref _minDangleM, value);
        }

        private bool _isExtractingRoads;
        public bool IsExtractingRoads
        {
            get => _isExtractingRoads;
            set => SetProperty(ref _isExtractingRoads, value);
        }

        private int _roadExtractionProgress;
        public int RoadExtractionProgress
        {
            get => _roadExtractionProgress;
            set => SetProperty(ref _roadExtractionProgress, value);
        }

        private string _roadExtractionStatus = "";
        public string RoadExtractionStatus
        {
            get => _roadExtractionStatus;
            set => SetProperty(ref _roadExtractionStatus, value);
        }

        private CancellationTokenSource _roadExtractionCts;

        public ICommand ExtractRoadsCommand => new RelayCommand(async () => await ExtractRoadsAsync(),
            () => !IsExtractingRoads && SelectedRoadRasterLayer != null);
        public ICommand CancelRoadExtractionCommand => new RelayCommand(() => _roadExtractionCts?.Cancel(), () => IsExtractingRoads);

        private async Task ExtractRoadsAsync()
        {
            IsExtractingRoads = true;
            RoadExtractionProgress = 0;
            RoadExtractionStatus = Tr("Scanning for road/trail centerlines...", "Memindai centerline jalan/jalur...");
            _roadExtractionCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    RoadExtractionStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    RoadExtractionStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var map = MapView.Active.Map;
                var rasterPath = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedRoadRasterLayer)?.GetPath()?.LocalPath);

                if (rasterPath == null)
                {
                    RoadExtractionStatus = Tr("Raster layer not found - click Refresh and pick a layer again.",
                        "Layer raster tidak ditemukan - klik Refresh dan pilih layer lagi.");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"Roads_{stamp}");

                // Provider/ApiKey/Model/UseAiValidation are the same shared Settings-tab AI
                // Vision Validation fields Tree Detection/Land Clearing use - see
                // TreeDetection.cs.
                var aiEnabled = UseAiValidation && !string.IsNullOrWhiteSpace(ApiKey);
                var request = new RoadExtractionRequest(
                    rasterPath, outputFc, ROAD_EXG_THRESHOLD, DEFAULT_SMOOTH_PX, MinDangleM,
                    Provider: aiEnabled ? SelectedProvider.ToLowerInvariant() : null,
                    ApiKey: aiEnabled ? ApiKey : null,
                    Model: aiEnabled ? SelectedModel : null);

                var dispatcher = System.Windows.Application.Current.Dispatcher;
                var result = await PythonBackendService.RunRoadExtractionAsync(
                    request,
                    pct => dispatcher.BeginInvoke(() => RoadExtractionProgress = pct),
                    stage => dispatcher.BeginInvoke(() => RoadExtractionStatus = stage),
                    _roadExtractionCts.Token);

                if (result.Cancelled)
                {
                    RoadExtractionStatus = Tr("Cancelled.", "Dibatalkan.");
                }
                else if (result.Success)
                {
                    await QueuedTask.Run(() =>
                    {
                        if (LayerFactory.Instance.CreateLayer(new Uri(result.OutputFc), map, layerName: Path.GetFileName(result.OutputFc)) is not FeatureLayer newLayer)
                            return;
                        var symbol = SymbolFactory.Instance.ConstructLineSymbol(
                            ColorFactory.Instance.CreateRGBColor(240, 200, 40), 2.0);
                        newLayer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
                    });
                    // Same explicit-even-at-zero AI confirmation as Tree Detection/Land
                    // Clearing - see TreeDetection.cs's comment for why.
                    var aiNote = aiEnabled
                        ? Tr($" (Validated with {SelectedProvider} - {result.RejectedByAiCount} rejected.)",
                             $" (Divalidasi dengan {SelectedProvider} - {result.RejectedByAiCount} ditolak.)") : "";
                    RoadExtractionStatus = Tr(
                        result.LineCount == 0
                            ? "Done: no road/trail centerlines found."
                            : $"Done: {result.LineCount} segment(s) found, {result.LengthKm:F2} km total.",
                        result.LineCount == 0
                            ? "Selesai: tidak ada centerline jalan/jalur ditemukan."
                            : $"Selesai: {result.LineCount} segmen ditemukan, total {result.LengthKm:F2} km.") +
                        aiNote;
                }
                else
                {
                    RoadExtractionStatus = Tr($"Failed: {result.ErrorMessage}", $"Gagal: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                RoadExtractionStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _roadExtractionCts?.Dispose();
                _roadExtractionCts = null;
                IsExtractingRoads = false;
            }
        }

        // Deliberately NOT LandClearing.cs's DEFAULT_EXG_THRESHOLD (18) - validated
        // separately (2026-08-11, real ground truth: hasil digit/digitasi jalan.shp vs.
        // its source orthophoto) against the standard road-network "buffer method"
        // accuracy metric: exg_threshold=8 scored F1 60.3% vs. 53.5% at 18, since a
        // centerline (skeletonize is sensitive to the full mask width) benefits from a
        // stricter/narrower mask more than land_clearing.py's own area-overlap target
        // does - see backend/road_extraction.py's DEFAULT_ROAD_EXG_THRESHOLD docstring.
        private const double ROAD_EXG_THRESHOLD = 8;

        // Own copy, not LandClearing.cs's (that one became a bindable
        // LandClearingSmoothPx property, no longer a fixed constant to share).
        private const double DEFAULT_SMOOTH_PX = 3;
    }
}
