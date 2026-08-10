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
            RoadExtractionStatus = "Scanning for road/trail centerlines...";
            _roadExtractionCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    RoadExtractionStatus = "No active map view. Open a map first.";
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    RoadExtractionStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }

                var map = MapView.Active.Map;
                var rasterPath = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedRoadRasterLayer)?.GetPath()?.LocalPath);

                if (rasterPath == null)
                {
                    RoadExtractionStatus = "Raster layer not found - click Refresh and pick a layer again.";
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"Roads_{stamp}");

                var request = new RoadExtractionRequest(
                    rasterPath, outputFc, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX, MinDangleM);

                var dispatcher = System.Windows.Application.Current.Dispatcher;
                var result = await PythonBackendService.RunRoadExtractionAsync(
                    request,
                    pct => dispatcher.BeginInvoke(() => RoadExtractionProgress = pct),
                    stage => dispatcher.BeginInvoke(() => RoadExtractionStatus = stage),
                    _roadExtractionCts.Token);

                if (result.Cancelled)
                {
                    RoadExtractionStatus = "Cancelled.";
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
                    RoadExtractionStatus = result.LineCount == 0
                        ? "Done: no road/trail centerlines found."
                        : $"Done: {result.LineCount} segment(s) found, {result.LengthKm:F2} km total.";
                }
                else
                {
                    RoadExtractionStatus = $"Failed: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                RoadExtractionStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                _roadExtractionCts?.Dispose();
                _roadExtractionCts = null;
                IsExtractingRoads = false;
            }
        }
    }
}
