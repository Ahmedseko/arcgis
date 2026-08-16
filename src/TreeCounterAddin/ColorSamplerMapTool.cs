using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Ribbon-registered (Config.daml), dockpane-activated (TreeCounterDockpaneViewModel.
    // ColorSampler.cs's StartColorSamplingCommand calls FrameworkApplication.SetCurrentToolAsync)
    // map tool: each click asks the running pixel-sample worker process (started in
    // StartColorSamplingAsync, see its own comment) for the RGB pixel under the cursor and
    // adds it as a buffered sample, for calibrating ExG thresholds against real sampled
    // colors instead of eyeballed screenshots (real report, 2026-08-16).
    //
    // Only ever touches the map to get the click's coordinates - no ArcGIS.Core.Data.Raster
    // access here at all. An earlier version read the pixel directly via Raster.MapToPixel/
    // GetPixelValue and crashed ArcGIS Pro outright on the very first click; see
    // TreeCounterDockpaneViewModel.ColorSampler.cs's comment for why.
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

        protected override async Task HandleMouseDownAsync(MapViewMouseButtonEventArgs e)
        {
            var mapView = MapView.Active;
            var vm = TreeCounterDockpaneViewModel.Instance;
            if (mapView == null || vm == null) return;

            var mapPoint = await QueuedTask.Run(() => mapView.ClientToMap(e.ClientPoint));

            var (ok, r, g, b) = await vm.SamplePixelAsync(mapPoint.X, mapPoint.Y);
            if (!ok)
            {
                vm.ColorSamplerStatus = vm.Tr("Clicked outside the raster (or worker not running) - no pixel there.",
                    "Klik di luar raster (atau worker belum berjalan) - tidak ada piksel di situ.");
                return;
            }

            var exgValue = 2.0 * g - r - b;
            vm.AddColorSample(mapPoint.X, mapPoint.Y, r, g, b, exgValue);
        }
    }
}
