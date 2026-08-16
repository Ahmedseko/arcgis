using ArcGIS.Core.Data.Raster;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Ribbon-registered (Config.daml), dockpane-activated (TreeCounterDockpaneViewModel.
    // ColorSampler.cs's StartColorSamplingCommand calls FrameworkApplication.SetCurrentToolAsync)
    // map tool: each click reads the RGB pixel directly under the cursor from the selected
    // raster layer and adds it as a labeled point to a reference feature class, so ExG
    // thresholds can be calibrated against real sampled colors instead of eyeballed
    // screenshots (real report, 2026-08-16).
    //
    // Reads the pixel directly in C# (ArcGIS.Core.Data.Raster.Raster.MapToPixel +
    // GetPixelValue - same pattern as Esri's own CustomRasterIdentify sample) rather than
    // shelling out to Python per click like every other raster operation in this add-in -
    // a subprocess launch (~1-2s, mostly arcpy import) per click would make rapid
    // successive sampling unusably laggy; this needs to feel instant.
    internal class ColorSamplerMapTool : MapTool
    {
        public ColorSamplerMapTool()
        {
            IsSketchTool = false;
        }

        protected override void OnToolMouseDown(MapViewMouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                e.Handled = true;
        }

        protected override Task HandleMouseDownAsync(MapViewMouseButtonEventArgs e)
        {
            return QueuedTask.Run(() =>
            {
                var mapView = MapView.Active;
                var vm = TreeCounterDockpaneViewModel.Instance;
                if (mapView == null || vm == null) return;

                var rasterLayer = mapView.Map.GetLayersAsFlattenedList()
                    .OfType<RasterLayer>()
                    .FirstOrDefault(l => l.Name == vm.SelectedColorSamplerRasterLayer);
                if (rasterLayer == null)
                {
                    vm.ColorSamplerStatus = "Raster layer not found - pick one and click Start Sampling again.";
                    return;
                }

                var mapPoint = mapView.ClientToMap(e.ClientPoint);

                using var raster = rasterLayer.GetRaster();
                // Tells the raster to expect incoming coordinates in the map's own spatial
                // reference (same as Esri's CustomRasterIdentify sample) - without this,
                // MapToPixel silently misreads whenever the raster's stored SR differs from
                // whatever the map view happens to be displayed in.
                raster.SetSpatialReference(mapView.Map.SpatialReference);

                var (row, col) = raster.MapToPixel(mapPoint.X, mapPoint.Y);
                if (row < 0 || col < 0 || row >= raster.GetHeight() || col >= raster.GetWidth())
                {
                    vm.ColorSamplerStatus = "Clicked outside the raster - no pixel there.";
                    return;
                }

                var bandCount = raster.GetBandCount();
                var r = Convert.ToInt32(raster.GetPixelValue(0, row, col));
                var g = Convert.ToInt32(raster.GetPixelValue(1, row, col));
                var b = bandCount > 2 ? Convert.ToInt32(raster.GetPixelValue(2, row, col)) : 0;
                var exgValue = 2.0 * g - r - b;

                vm.AddColorSample(mapPoint, r, g, b, exgValue);
            });
        }
    }
}
