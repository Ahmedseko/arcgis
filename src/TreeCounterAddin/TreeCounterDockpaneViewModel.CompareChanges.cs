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
            CompareChangesStatus = "Comparing...";
            try
            {
                if (MapView.Active == null)
                {
                    CompareChangesStatus = "No active map view. Open a map first.";
                    return;
                }
                if (SelectedCompareOldLayer == SelectedCompareNewLayer)
                {
                    CompareChangesStatus = "Pick two different point layers to compare.";
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    CompareChangesStatus = "No open ArcGIS Pro project. Create or open one first.";
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
                    CompareChangesStatus = "Layer not found - click Refresh and pick layers again.";
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
                    CompareChangesStatus = $"Done: {result.LostCount} lost, {result.NewCount} new, {result.MatchedCount} matched.";
                }
                else
                {
                    CompareChangesStatus = $"Failed: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                CompareChangesStatus = $"Unexpected error: {ex.Message}";
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
