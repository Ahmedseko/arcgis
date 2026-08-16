using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Change detection between two Tree Detection runs of the same site over time -
    // wraps backend/detector.py's compare_detections() (already ported from the QGIS
    // plugin, see README's "Not done" list) via a new compare_detections.py CLI, same
    // PythonBackendService subprocess pattern as Tree/Land Clearing Detection.
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedCompareOldLayer;
        public string SelectedCompareOldLayer
        {
            get => _selectedCompareOldLayer;
            set => SetProperty(ref _selectedCompareOldLayer, value);
        }

        private string _selectedCompareNewLayer;
        public string SelectedCompareNewLayer
        {
            get => _selectedCompareNewLayer;
            set => SetProperty(ref _selectedCompareNewLayer, value);
        }

        // Matches compare_detections.py's own DEFAULT_MAX_DIST_M - comfortably under
        // typical tree spacing while covering re-run centroid jitter.
        private double _compareMaxDistM = 3.0;
        public double CompareMaxDistM
        {
            get => _compareMaxDistM;
            set => SetProperty(ref _compareMaxDistM, value);
        }

        private bool _isComparingChanges;
        public bool IsComparingChanges
        {
            get => _isComparingChanges;
            set => SetProperty(ref _isComparingChanges, value);
        }

        private string _compareChangesStatus = "";
        public string CompareChangesStatus
        {
            get => _compareChangesStatus;
            set => SetProperty(ref _compareChangesStatus, value);
        }

        public ICommand CompareChangesCommand => new RelayCommand(async () => await CompareChangesAsync(),
            () => !IsComparingChanges && SelectedCompareOldLayer != null && SelectedCompareNewLayer != null);

        private async Task CompareChangesAsync()
        {
            IsComparingChanges = true;
            CompareChangesStatus = Tr("Comparing...", "Membandingkan...");
            try
            {
                if (MapView.Active == null)
                {
                    CompareChangesStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                if (SelectedCompareOldLayer == SelectedCompareNewLayer)
                {
                    CompareChangesStatus = Tr("Pick two different point layers to compare.", "Pilih dua layer titik yang berbeda untuk dibandingkan.");
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    CompareChangesStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var map = MapView.Active.Map;
                var (oldPath, newPath) = await QueuedTask.Run(() =>
                {
                    var layers = map.GetLayersAsFlattenedList().OfType<FeatureLayer>();
                    var oldFc = layers.FirstOrDefault(l => l.Name == SelectedCompareOldLayer)?.GetPath()?.LocalPath;
                    var newFc = layers.FirstOrDefault(l => l.Name == SelectedCompareNewLayer)?.GetPath()?.LocalPath;
                    return (oldFc, newFc);
                });

                if (oldPath == null || newPath == null)
                {
                    CompareChangesStatus = Tr("Layer not found - click Refresh and pick layers again.", "Layer tidak ditemukan - klik Refresh dan pilih layer lagi.");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var request = new CompareChangesRequest(
                    oldPath, newPath,
                    Path.Combine(project.DefaultGeodatabasePath, $"Lost_{stamp}"),
                    Path.Combine(project.DefaultGeodatabasePath, $"New_{stamp}"),
                    CompareMaxDistM);

                var result = await PythonBackendService.RunCompareChangesAsync(request);

                if (result.Success)
                {
                    await QueuedTask.Run(() =>
                    {
                        AddComparePointLayer(map, result.LostFc, 200, 40, 40);
                        AddComparePointLayer(map, result.NewFc, 40, 180, 60);
                    });
                    CompareChangesStatus = Tr($"Done: {result.LostCount} lost, {result.NewCount} new, {result.MatchedCount} matched.",
                        $"Selesai: {result.LostCount} hilang, {result.NewCount} baru, {result.MatchedCount} cocok.");
                }
                else
                {
                    CompareChangesStatus = Tr($"Failed: {result.ErrorMessage}", $"Gagal: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                CompareChangesStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                IsComparingChanges = false;
            }
        }

        // Must run on the MCT (QueuedTask) - shared by both the lost/new layer adds above.
        private static void AddComparePointLayer(Map map, string fc, int r, int g, int b)
        {
            if (LayerFactory.Instance.CreateLayer(new Uri(fc), map, layerName: Path.GetFileName(fc)) is not FeatureLayer layer)
                return;
            var symbol = SymbolFactory.Instance.ConstructPointSymbol(ColorFactory.Instance.CreateRGBColor(r, g, b), 6);
            layer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
        }
    }
}
