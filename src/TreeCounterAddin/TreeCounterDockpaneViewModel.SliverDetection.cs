using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedSliverLayer;
        public string SelectedSliverLayer
        {
            get => _selectedSliverLayer;
            set => SetProperty(ref _selectedSliverLayer, value);
        }

        private bool _isDetectingSlivers;
        public bool IsDetectingSlivers
        {
            get => _isDetectingSlivers;
            set => SetProperty(ref _isDetectingSlivers, value);
        }

        private string _sliverStatus = "";
        public string SliverStatus
        {
            get => _sliverStatus;
            set => SetProperty(ref _sliverStatus, value);
        }

        public ICommand DetectSliversCommand => new RelayCommand(async () => await DetectSliversAsync(), () => !IsDetectingSlivers && SelectedSliverLayer != null);

        // Sliver = a polygon that's technically valid geometry but visually just a thin
        // leftover fragment (e.g. a fishnet cell clipped along a near-tangent boundary edge,
        // or a stray degenerate ring left over in a source shapefile). ArcGIS has no
        // dedicated "sliver detector" tool. Two auto-calibrated signals, either one enough
        // to flag a part:
        //  - Area far below this layer's own median part size (catches small fragments) -
        //    but a long, thin sliver can have a middling area, so area alone isn't enough.
        //  - Thinness ratio (4*pi*Area/Perimeter^2, 1=circle, ->0 as a shape thins out) far
        //    below this layer's own median thinness - catches long/thin shapes regardless
        //    of their absolute size.
        // Computed per PART (Polygon.Parts), not per feature - summing Area/Length across a
        // multipart feature's disjoint rings distorts the thinness ratio and can hide a
        // genuinely thin ring inside an otherwise normal multi-island feature.
        private const double SliverAreaFraction = 0.1;
        private const double SliverThinnessFraction = 0.5;

        private async Task DetectSliversAsync()
        {
            IsDetectingSlivers = true;
            SliverStatus = "Scanning...";
            try
            {
                if (MapView.Active == null)
                {
                    SliverStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedSliverLayer));
                if (layer == null)
                {
                    SliverStatus = "Layer not found - click Refresh and pick again.";
                    return;
                }

                var (oids, totalArea, medianAreaHa, areaThresholdHa, medianThinness, thinnessThreshold) = await QueuedTask.Run(() =>
                {
                    using var featureClass = layer.GetFeatureClass();
                    using var cursor = featureClass.Search(null, false);
                    var parts = new List<(long Oid, double Area, double Thinness)>();
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        if (feature.GetShape() is not Polygon poly) continue;

                        foreach (var part in poly.Parts)
                        {
                            if (new PolygonBuilderEx(part, AttributeFlags.None, poly.SpatialReference).ToGeometry() is not Polygon ring
                                || ring.Length <= 0)
                                continue;
                            var thinness = SliverMath.Thinness(ring.Area, ring.Length);
                            parts.Add((feature.GetObjectID(), ring.Area, thinness));
                        }
                    }
                    if (parts.Count == 0)
                        return (new List<long>(), 0.0, 0.0, 0.0, 0.0, 0.0);

                    var medianArea = SliverMath.Median(parts.Select(p => p.Area));
                    var medianThin = SliverMath.Median(parts.Select(p => p.Thinness));
                    var areaThreshold = medianArea * SliverAreaFraction;
                    var thinThreshold = medianThin * SliverThinnessFraction;

                    var flagged = parts.Where(p => p.Area < areaThreshold || p.Thinness < thinThreshold).ToList();
                    var flaggedOids = flagged.Select(p => p.Oid).Distinct().ToList();

                    return (flaggedOids, flagged.Sum(p => p.Area), medianArea / 10000.0, areaThreshold / 10000.0, medianThin, thinThreshold);
                });

                if (oids.Count == 0)
                {
                    SliverStatus = $"No slivers found (median part {medianAreaHa:F4} ha / thinness {medianThinness:F2}; " +
                        $"flagging under {areaThresholdHa:F4} ha or thinness {thinnessThreshold:F2}).";
                    return;
                }

                await QueuedTask.Run(() =>
                {
                    var oidField = layer.GetFeatureClass().GetDefinition().GetObjectIDField();
                    layer.Select(new QueryFilter { WhereClause = $"{oidField} IN ({string.Join(",", oids)})" },
                        SelectionCombinationMethod.New);
                });

                SliverStatus = $"Found {oids.Count} sliver polygon(s) (area < {areaThresholdHa:F4} ha or thinness < {thinnessThreshold:F2}), " +
                    $"{totalArea / 10000.0:F4} ha total - selected on map.";
            }
            catch (Exception ex)
            {
                SliverStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsDetectingSlivers = false;
            }
        }
    }
}
