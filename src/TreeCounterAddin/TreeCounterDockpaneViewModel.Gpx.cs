using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    internal partial class TreeCounterDockpaneViewModel
    {
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

        public ICommand ExportGpxCommand => new RelayCommand(async () => await ExportGpxAsync(), () => !IsExportingGpx && SelectedGpxLayer != null);

        // Uses Esri's own conversion.FeaturesToGPX GP tool - avoids hand-writing GPX XML.
        // GPX itself has no "filled area" concept (only waypoints and tracks), so a polygon
        // layer's boundary is converted to a line first (management.PolygonToLine) and that
        // line is what actually gets exported as a track - points/polylines go straight in
        // as waypoints/tracks. The resulting .gpx works directly with Garmin devices,
        // BaseCamp, or Garmin Connect (all just import a standard GPX file).
        private async Task ExportGpxAsync()
        {
            IsExportingGpx = true;
            GpxStatus = Tr("Exporting...", "Mengekspor...");
            string tempLineFc = null;
            try
            {
                if (MapView.Active == null)
                {
                    GpxStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }

                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedGpxLayer));
                if (layer == null)
                {
                    GpxStatus = Tr("Layer not found - click Refresh and pick again.", "Layer tidak ditemukan - klik Refresh dan pilih lagi.");
                    return;
                }

                var (sourcePath, shapeType) = await QueuedTask.Run(() => (layer.GetPath()?.LocalPath, layer.ShapeType));
                if (sourcePath == null)
                {
                    GpxStatus = Tr("Failed to read the layer's data path.", "Gagal membaca path data layer.");
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
                    GpxStatus = Tr("Cancelled.", "Dibatalkan.");
                    return;
                }

                var gpxSourcePath = sourcePath;
                if (shapeType == esriGeometryType.esriGeometryPolygon)
                {
                    var project = Project.Current;
                    if (project == null)
                    {
                        GpxStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                        return;
                    }

                    GpxStatus = Tr("Converting polygon boundary to a line...", "Mengonversi batas poligon jadi garis...");
                    tempLineFc = Path.Combine(project.DefaultGeodatabasePath, $"GpxBoundary_tmp_{DateTime.Now:yyyyMMdd_HHmmss}");

                    var lineResult = await Geoprocessing.ExecuteToolAsync("management.PolygonToLine",
                        Geoprocessing.MakeValueArray(sourcePath, tempLineFc),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    if (lineResult.IsFailed)
                    {
                        GpxStatus = Tr($"Failed to convert polygon boundary: {lineResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                            $"Gagal mengonversi batas poligon: {lineResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                        return;
                    }
                    gpxSourcePath = tempLineFc;
                }

                var result = await Geoprocessing.ExecuteToolAsync("conversion.FeaturesToGPX",
                    Geoprocessing.MakeValueArray(gpxSourcePath, dialog.FileName, "", "", "", ""),
                    null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (result.IsFailed)
                {
                    GpxStatus = Tr($"Failed to export GPX: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal mengekspor GPX: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                GpxStatus = Tr($"Done: exported to {dialog.FileName}", $"Selesai: diekspor ke {dialog.FileName}");
            }
            catch (Exception ex)
            {
                GpxStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
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
    }
}
