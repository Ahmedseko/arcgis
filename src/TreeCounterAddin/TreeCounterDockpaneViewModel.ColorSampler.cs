using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // "Color Reference Sampler" (Analyze tab) - click points on a raster to record their
    // exact RGB/ExG value, so ExG thresholds (Land Clearing/Road Extraction/Tree
    // Detection's cleared-land filter) can be calibrated against real labeled colors
    // instead of eyeballed screenshots - see ColorSamplerMapTool.cs for the actual
    // per-click pixel read.
    //
    // Samples are buffered here in memory while sampling is active and only written out
    // as a real feature class once, via backend/save_color_samples.py, when the user
    // clicks Stop - a first version created/wrote the feature class directly in C# via
    // ArcGIS.Core.Data.DDL/SchemaBuilder, which crashed ArcGIS Pro outright the moment
    // Start Sampling was clicked (real report, 2026-08-16), before a single point was ever
    // added. Whatever the exact native cause, going through the same proven arcpy-based
    // creation every other feature class in this add-in already uses sidesteps it entirely.
    // Label/Class are left blank - filled in afterward by the user directly in the new
    // layer's own Attribute Table (ArcGIS Pro already has a perfectly good editable table
    // UI; no need to build a second one).
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedColorSamplerRasterLayer;
        public string SelectedColorSamplerRasterLayer
        {
            get => _selectedColorSamplerRasterLayer;
            set => SetProperty(ref _selectedColorSamplerRasterLayer, value);
        }

        private bool _isColorSampling;
        public bool IsColorSampling
        {
            get => _isColorSampling;
            set => SetProperty(ref _isColorSampling, value);
        }

        private string _colorSamplerStatus = "";
        public string ColorSamplerStatus
        {
            get => _colorSamplerStatus;
            set => SetProperty(ref _colorSamplerStatus, value);
        }

        private readonly List<ColorSample> _pendingColorSamples = new();

        public ICommand StartColorSamplingCommand => new RelayCommand(async () => await StartColorSamplingAsync(),
            () => !IsColorSampling && SelectedColorSamplerRasterLayer != null);
        public ICommand StopColorSamplingCommand => new RelayCommand(async () => await StopColorSamplingAsync(), () => IsColorSampling);

        private async Task StartColorSamplingAsync()
        {
            try
            {
                if (MapView.Active == null) { ColorSamplerStatus = "No active map view. Open a map first."; return; }

                _pendingColorSamples.Clear();
                await FrameworkApplication.SetCurrentToolAsync("TreeCounterAddin_ColorSamplerTool");
                IsColorSampling = true;
                ColorSamplerStatus = "Sampling active - click on the raster to add points. 0 sample(s) so far.";
            }
            catch (Exception ex)
            {
                ColorSamplerStatus = $"Unexpected error: {ex.Message}";
            }
        }

        private async Task StopColorSamplingAsync()
        {
            try { await FrameworkApplication.SetCurrentToolAsync("esri_mapping_exploreTool"); }
            catch { /* best-effort - not critical if this fails */ }
            IsColorSampling = false;

            if (_pendingColorSamples.Count == 0)
            {
                ColorSamplerStatus = "Stopped - no samples were taken.";
                return;
            }

            var project = Project.Current;
            if (project == null) { ColorSamplerStatus = "No open ArcGIS Pro project - samples weren't saved."; return; }

            var map = MapView.Active?.Map;
            var rasterPath = await QueuedTask.Run(() =>
                map?.GetLayersAsFlattenedList().OfType<RasterLayer>()
                    .FirstOrDefault(l => l.Name == SelectedColorSamplerRasterLayer)?.GetPath()?.LocalPath);
            if (rasterPath == null)
            {
                ColorSamplerStatus = "Raster layer no longer found - samples weren't saved.";
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"ColorReference_{stamp}");
            var samples = _pendingColorSamples.ToList();

            ColorSamplerStatus = $"Saving {samples.Count} sample(s)...";
            var result = await PythonBackendService.SaveColorSamplesAsync(rasterPath, outputFc, samples);

            if (!result.Success)
            {
                ColorSamplerStatus = $"Failed to save samples: {result.ErrorMessage}";
                return;
            }

            await QueuedTask.Run(() =>
            {
                if (map == null) return;
                if (LayerFactory.Instance.CreateLayer(new Uri(result.OutputFc), map, layerName: Path.GetFileName(result.OutputFc)) is not FeatureLayer newLayer)
                    return;
                var symbol = SymbolFactory.Instance.ConstructPointSymbol(
                    ColorFactory.Instance.CreateRGBColor(235, 235, 225), 7, SimpleMarkerStyle.Circle);
                newLayer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
            });

            _pendingColorSamples.Clear();
            ColorSamplerStatus = $"Saved {result.Count} sample(s) to {Path.GetFileName(result.OutputFc)}. Fill in Label/Class in its Attribute Table.";
        }

        // Called from ColorSamplerMapTool.HandleMouseDownAsync, already on the MCT - just
        // buffers in memory, no I/O, so rapid successive clicks stay instant.
        internal void AddColorSample(double mapX, double mapY, int r, int g, int b, double exgValue)
        {
            _pendingColorSamples.Add(new ColorSample(mapX, mapY, r, g, b, exgValue));
            ColorSamplerStatus = $"Sampling active - {_pendingColorSamples.Count} sample(s) so far. " +
                $"Last: RGB({r},{g},{b}), ExG {(exgValue >= 0 ? "+" : "")}{exgValue:F0}.";
        }
    }
}
