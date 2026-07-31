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
            set { if (SetProperty(ref _cellWidth, value)) SaveSettings(); }
        }

        private double _cellHeight = 50;
        public double CellHeight
        {
            get => _cellHeight;
            set { if (SetProperty(ref _cellHeight, value)) SaveSettings(); }
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

        private CancellationTokenSource _fishnetCts;

        public ICommand CreateFishnetCommand => new RelayCommand(async () => await CreateFishnetAsync(), () => !IsCreatingFishnet && SelectedPolygonLayer != null);
        public ICommand CancelFishnetCommand => new RelayCommand(() => _fishnetCts?.Cancel(), () => IsCreatingFishnet);

        // Reuses Esri's own CreateFishnet + PairwiseClip GP tools rather than building a
        // grid algorithm from scratch - CreateFishnet only covers the polygon's bounding
        // extent (not its actual shape), so the clip step afterward is still required.
        private async Task CreateFishnetAsync()
        {
            IsCreatingFishnet = true;
            FishnetStatus = "Creating fishnet...";
            _fishnetCts = new CancellationTokenSource();
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
                    environments, cancelToken: _fishnetCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (createResult.IsFailed)
                {
                    FishnetStatus = $"Failed to create fishnet: {createResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                var clipResult = await Geoprocessing.ExecuteToolAsync("analysis.PairwiseClip",
                    Geoprocessing.MakeValueArray(fishnetFc, polyPath, clippedFc),
                    environments, cancelToken: _fishnetCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (clipResult.IsFailed)
                {
                    FishnetStatus = $"Failed to clip fishnet: {clipResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                await Geoprocessing.ExecuteToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(fishnetFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                // Gives field crews something to reference over radio/notes ("cell 42"),
                // rather than every cell in a grid looking identical. Soft-fail: the fishnet
                // itself is already usable without it, so a failure here is noted in the
                // final status rather than aborting the whole operation.
                var cellIdAdded = true;
                var addFieldResult = await Geoprocessing.ExecuteToolAsync("management.AddField",
                    Geoprocessing.MakeValueArray(clippedFc, "Cell_ID", "LONG"),
                    null, cancelToken: _fishnetCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (addFieldResult.IsFailed)
                {
                    cellIdAdded = false;
                }
                else
                {
                    var calcResult = await Geoprocessing.ExecuteToolAsync("management.CalculateField",
                        Geoprocessing.MakeValueArray(clippedFc, "Cell_ID", "!OBJECTID!", "PYTHON3"),
                        null, cancelToken: _fishnetCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                    cellIdAdded = !calcResult.IsFailed;
                }

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
                    (cellIdAdded ? "" : " Note: Cell_ID field could not be added.") +
                    (noCrsWarning ? " Warning: source polygon has no coordinate system defined - define its projection for accurate results." : "");
            }
            catch (OperationCanceledException)
            {
                FishnetStatus = "Cancelled.";
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(Path.GetTempPath(), "ForestryToolkit_fishnet_error.log");
                File.WriteAllText(logPath, ex.ToString());
                FishnetStatus = $"Unexpected error: {ex.Message} (full details: {logPath})";
            }
            finally
            {
                _fishnetCts?.Dispose();
                _fishnetCts = null;
                IsCreatingFishnet = false;
            }
        }
    }
}
