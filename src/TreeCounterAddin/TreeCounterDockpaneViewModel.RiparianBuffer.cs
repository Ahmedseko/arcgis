using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
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
    internal partial class TreeCounterDockpaneViewModel
    {
        // River layer combo reuses GpxLayers (already populated with point/line/polygon
        // layer names) - rivers are commonly digitized as lines, sometimes polygons for
        // wide ones, so the same broad list fits without needing a dedicated collection.
        private string _selectedRiverLayer;
        public string SelectedRiverLayer
        {
            get => _selectedRiverLayer;
            set => SetProperty(ref _selectedRiverLayer, value);
        }

        private string _selectedBufferPlanLayer;
        public string SelectedBufferPlanLayer
        {
            get => _selectedBufferPlanLayer;
            set => SetProperty(ref _selectedBufferPlanLayer, value);
        }

        // No single legal number is asserted here - riparian buffer widths vary by
        // regulation/river class, so this stays a user-entered value rather than a
        // hardcoded "compliance" threshold.
        private double _bufferDistanceM = 50;
        public double BufferDistanceM
        {
            get => _bufferDistanceM;
            set => SetProperty(ref _bufferDistanceM, value);
        }

        private bool _isCheckingBuffer;
        public bool IsCheckingBuffer
        {
            get => _isCheckingBuffer;
            set => SetProperty(ref _isCheckingBuffer, value);
        }

        private string _bufferStatus = "";
        public string BufferStatus
        {
            get => _bufferStatus;
            set => SetProperty(ref _bufferStatus, value);
        }

        private CancellationTokenSource _bufferCts;

        public ICommand CheckRiparianBufferCommand => new RelayCommand(async () => await CheckRiparianBufferAsync(),
            () => !IsCheckingBuffer && SelectedRiverLayer != null && SelectedBufferPlanLayer != null);
        public ICommand CancelRiparianBufferCommand => new RelayCommand(() => _bufferCts?.Cancel(), () => IsCheckingBuffer);

        // Reuses Esri's own Buffer + Intersect GP tools - buffers the river/stream layer by
        // the given distance, then intersects that buffer with the planning polygon to find
        // (and highlight) whatever part of the plan falls inside the riparian setback.
        private async Task CheckRiparianBufferAsync()
        {
            IsCheckingBuffer = true;
            BufferStatus = Tr("Checking...", "Memeriksa...");
            _bufferCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    BufferStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                if (BufferDistanceM <= 0)
                {
                    BufferStatus = Tr("Buffer distance must be greater than 0.", "Jarak buffer harus lebih besar dari 0.");
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    BufferStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var map = MapView.Active.Map;
                var (riverLayer, planLayer) = await QueuedTask.Run(() =>
                {
                    var layers = map.GetLayersAsFlattenedList().OfType<FeatureLayer>();
                    return (layers.FirstOrDefault(l => l.Name == SelectedRiverLayer),
                            layers.FirstOrDefault(l => l.Name == SelectedBufferPlanLayer));
                });
                if (riverLayer == null || planLayer == null)
                {
                    BufferStatus = Tr("Layer not found - click Refresh and pick again.", "Layer tidak ditemukan - klik Refresh dan pilih lagi.");
                    return;
                }

                var (riverPath, planPath) = await QueuedTask.Run(() =>
                    (riverLayer.GetPath()?.LocalPath, planLayer.GetPath()?.LocalPath));
                if (riverPath == null || planPath == null)
                {
                    BufferStatus = Tr("Failed to read the layers' data paths.", "Gagal membaca path data layer-layer tersebut.");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var bufferFc = Path.Combine(project.DefaultGeodatabasePath, $"RiverBuffer_tmp_{stamp}");
                var conflictFc = Path.Combine(project.DefaultGeodatabasePath, $"BufferConflict_{stamp}");

                var bufferResult = await Geoprocessing.ExecuteToolAsync("analysis.PairwiseBuffer",
                    Geoprocessing.MakeValueArray(riverPath, bufferFc, $"{BufferDistanceM} Meters", "ALL"),
                    null, cancelToken: _bufferCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (bufferResult.IsFailed)
                {
                    BufferStatus = Tr($"Failed to buffer river layer: {bufferResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal membuat buffer layer sungai: {bufferResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                var intersectResult = await Geoprocessing.ExecuteToolAsync("analysis.PairwiseIntersect",
                    Geoprocessing.MakeValueArray($"{planPath};{bufferFc}", conflictFc),
                    null, cancelToken: _bufferCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (intersectResult.IsFailed)
                {
                    BufferStatus = Tr($"Failed to intersect with buffer: {intersectResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal intersect dengan buffer: {intersectResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                await Geoprocessing.ExecuteToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(bufferFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                var (conflictLayer, count, areaHa) = await QueuedTask.Run(() =>
                {
                    var newLayer = LayerFactory.Instance.CreateLayer(new Uri(conflictFc), map, layerName: Path.GetFileName(conflictFc)) as FeatureLayer;
                    using var featureClass = newLayer?.GetFeatureClass();
                    if (featureClass == null) return (newLayer, 0, 0.0);

                    using var cursor = featureClass.Search(null, false);
                    int c = 0;
                    double area = 0;
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        if (feature.GetShape() is Polygon poly)
                            area += poly.Area;
                        c++;
                    }
                    return (newLayer, c, area / 10000.0);
                });

                if (count == 0)
                {
                    // No conflict - remove the empty layer/dataset instead of leaving clutter
                    // behind for the common (compliant) case.
                    if (conflictLayer != null)
                        await QueuedTask.Run(() => map.RemoveLayer(conflictLayer));
                    await Geoprocessing.ExecuteToolAsync("management.Delete",
                        Geoprocessing.MakeValueArray(conflictFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    BufferStatus = Tr($"No conflict - \"{SelectedBufferPlanLayer}\" stays outside the {BufferDistanceM:F0} m buffer around \"{SelectedRiverLayer}\".",
                        $"Tidak ada konflik - \"{SelectedBufferPlanLayer}\" tetap di luar buffer {BufferDistanceM:F0} m di sekitar \"{SelectedRiverLayer}\".");
                    return;
                }

                BufferStatus = Tr($"Found {areaHa:F4} ha of \"{SelectedBufferPlanLayer}\" within {BufferDistanceM:F0} m of \"{SelectedRiverLayer}\" - added as a layer.",
                    $"Ditemukan {areaHa:F4} ha dari \"{SelectedBufferPlanLayer}\" dalam radius {BufferDistanceM:F0} m dari \"{SelectedRiverLayer}\" - ditambahkan sebagai layer.");
            }
            catch (OperationCanceledException)
            {
                BufferStatus = Tr("Cancelled.", "Dibatalkan.");
            }
            catch (Exception ex)
            {
                BufferStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _bufferCts?.Dispose();
                _bufferCts = null;
                IsCheckingBuffer = false;
            }
        }
    }
}
