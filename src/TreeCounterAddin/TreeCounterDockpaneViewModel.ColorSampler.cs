using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // "Color Reference Sampler" (Analyze tab) - click points on a raster to record their
    // exact RGB/ExG value, so ExG thresholds (Land Clearing/Road Extraction/Tree
    // Detection's cleared-land filter) can be calibrated against real labeled colors
    // instead of eyeballed screenshots - see ColorSamplerMapTool.cs for the click side.
    //
    // Two real crashes shaped this file's design (2026-08-16), both native ArcGIS Pro
    // crashes with no catchable exception/stack trace:
    // 1. Feature class creation directly in C# via ArcGIS.Core.Data.DDL/SchemaBuilder
    //    crashed on the very first "Start Sampling" click, before any point was even
    //    added - fixed by only ever creating the feature class via arcpy in a subprocess
    //    (backend/save_color_samples.py), same as every other feature class in this add-in
    //    already does, called once (on Stop Sampling) rather than per click.
    // 2. Reading the pixel directly via ArcGIS.Core.Data.Raster.Raster.MapToPixel/
    //    GetPixelValue (RasterLayer.GetRaster()) crashed on the first map click - most
    //    likely because GetRaster() hands back the layer's own live rendering object, and
    //    this was mutating (SetSpatialReference) and disposing it out from under the
    //    renderer. Fixed by never touching ArcGIS.Core.Data.Raster at all: a long-lived
    //    backend/pixel_sample_server.py worker process (started here, one per sampling
    //    session) answers "x,y" pixel-color requests over stdin/stdout instead - the same
    //    proven arcpy raster-read path every other feature already uses, just kept warm as
    //    a standing process so per-click round trips stay fast (a fresh subprocess per
    //    click would add ~1-2s of arcpy-import latency to every single click).
    //
    // Label/Class are left blank on save - filled in afterward by the user directly in the
    // new layer's own Attribute Table (ArcGIS Pro already has a perfectly good editable
    // table UI; no need to build a second one).
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

        // "Sticky" category - stays selected across clicks so a cluster of same-class
        // points (e.g. walking along a forest edge) doesn't need re-picking per click;
        // switch it, then keep clicking for the next class. ComboBox is editable (see
        // XAML) so a category outside this preset list can still be typed - the fc's own
        // Class field stays plain free text either way (2026-08-16 decision), this list is
        // just a fast-entry aid, not a hard constraint.
        //
        // Bilingual (2026-08-16 follow-up report) - reuses the same IsHelpEnglish flag the
        // Help tab already exposes and BilingualTooltipConverter.cs already reuses for
        // Flight Mission Planner's tooltips, rather than adding a second independent
        // language switch. RefreshSampleCategories is called from IsHelpEnglish's own
        // setter (Help.cs) so switching language there updates this list too.
        private static readonly (string En, string Id)[] SampleCategoryPairs =
        {
            ("Forest canopy", "Hutan/kanopi rapat"),
            ("Low vegetation/regrowth", "Vegetasi rendah/regrowth"),
            ("Cleared/bare ground", "Bukaan/tanah terbuka"),
            ("Felled-tree debris", "Serpihan tebangan/kayu"),
            ("Road/track", "Jalan/jalur"),
            ("Water/river", "Sungai/air"),
            ("Shadow", "Bayangan"),
            ("Heavy equipment/vehicle", "Alat berat/kendaraan"),
            ("Building/roof", "Bangunan/atap"),
            ("Other/unsure", "Lainnya/tidak yakin"),
        };

        public ObservableCollection<string> SampleCategories { get; } = new();

        private string _selectedSampleCategory;
        public string SelectedSampleCategory
        {
            get => _selectedSampleCategory;
            set => SetProperty(ref _selectedSampleCategory, value);
        }

        // Called once from the constructor (IsHelpEnglish's field initializer doesn't run
        // its property setter, so nothing would populate this list at startup otherwise)
        // and again from IsHelpEnglish's setter on every language switch. Keeps whichever
        // category concept was selected, just re-displayed in the new language, instead of
        // resetting back to the first item.
        private void RefreshSampleCategories()
        {
            var previousIndex = SampleCategories.IndexOf(SelectedSampleCategory);
            SampleCategories.Clear();
            foreach (var (en, id) in SampleCategoryPairs)
                SampleCategories.Add(IsHelpEnglish ? en : id);
            SelectedSampleCategory = previousIndex >= 0 && previousIndex < SampleCategories.Count
                ? SampleCategories[previousIndex] : SampleCategories[0];
        }

        private readonly List<ColorSample> _pendingColorSamples = new();
        private Process _pixelSampleWorker;
        private string _colorSamplerRasterPath;

        public ICommand StartColorSamplingCommand => new RelayCommand(async () => await StartColorSamplingAsync(),
            () => !IsColorSampling && SelectedColorSamplerRasterLayer != null);
        public ICommand StopColorSamplingCommand => new RelayCommand(async () => await StopColorSamplingAsync(), () => IsColorSampling);

        private async Task StartColorSamplingAsync()
        {
            try
            {
                if (MapView.Active == null) { ColorSamplerStatus = "No active map view. Open a map first."; return; }

                var map = MapView.Active.Map;
                var rasterPath = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedColorSamplerRasterLayer)?.GetPath()?.LocalPath);
                if (rasterPath == null)
                {
                    ColorSamplerStatus = "Raster layer not found - click Refresh and pick a layer again.";
                    return;
                }

                StopPixelSampleWorker();
                _colorSamplerRasterPath = rasterPath;
                _pixelSampleWorker = PythonBackendService.StartPixelSampleWorker(rasterPath);
                _pendingColorSamples.Clear();

                await FrameworkApplication.SetCurrentToolAsync("TreeCounterAddin_ColorSamplerTool");
                IsColorSampling = true;
                ColorSamplerStatus = $"Sampling active - category '{SelectedSampleCategory}' - click on the raster to add points. 0 sample(s) so far.";
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
            StopPixelSampleWorker();

            if (_pendingColorSamples.Count == 0)
            {
                ColorSamplerStatus = "Stopped - no samples were taken.";
                return;
            }

            var project = Project.Current;
            if (project == null) { ColorSamplerStatus = "No open ArcGIS Pro project - samples weren't saved."; return; }

            var map = MapView.Active?.Map;
            var rasterPath = _colorSamplerRasterPath;
            if (rasterPath == null)
            {
                ColorSamplerStatus = "Raster path lost - samples weren't saved.";
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

        // Called from ColorSamplerMapTool.HandleMouseDownAsync. Round-trips one request to
        // the standing pixel_sample_server.py worker (see class comment) - not on the MCT,
        // just async stream I/O against an already-running process.
        internal async Task<(bool Ok, int R, int G, int B)> SamplePixelAsync(double mapX, double mapY)
        {
            if (_pixelSampleWorker == null || _pixelSampleWorker.HasExited) return (false, 0, 0, 0);
            try
            {
                await _pixelSampleWorker.StandardInput.WriteLineAsync(
                    mapX.ToString(CultureInfo.InvariantCulture) + "," + mapY.ToString(CultureInfo.InvariantCulture));
                await _pixelSampleWorker.StandardInput.FlushAsync();

                var line = await _pixelSampleWorker.StandardOutput.ReadLineAsync();
                if (line == null || line == "NODATA" || line.StartsWith("ERROR")) return (false, 0, 0, 0);

                var parts = line.Split(',');
                return (true, int.Parse(parts[0], CultureInfo.InvariantCulture),
                    int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            catch
            {
                return (false, 0, 0, 0);
            }
        }

        // Buffers in memory only, no I/O - keeps AddColorSample itself instant regardless
        // of how the RGB was obtained. Tags every sample with whatever category is
        // currently selected (see SelectedSampleCategory) rather than requiring a per-click
        // prompt - a real report (2026-08-16) noted samples are otherwise impossible to
        // remember/relabel accurately after the fact.
        internal void AddColorSample(double mapX, double mapY, int r, int g, int b, double exgValue)
        {
            _pendingColorSamples.Add(new ColorSample(mapX, mapY, r, g, b, exgValue, SelectedSampleCategory ?? ""));
            ColorSamplerStatus = $"Sampling active - category '{SelectedSampleCategory}' - {_pendingColorSamples.Count} sample(s) so far. " +
                $"Last: RGB({r},{g},{b}), ExG {(exgValue >= 0 ? "+" : "")}{exgValue:F0}.";
        }

        private void StopPixelSampleWorker()
        {
            if (_pixelSampleWorker == null) return;
            try
            {
                if (!_pixelSampleWorker.HasExited)
                {
                    // Closing stdin sends EOF - pixel_sample_server.py's `for line in
                    // sys.stdin` loop ends naturally and the process exits on its own,
                    // rather than being killed mid-read.
                    _pixelSampleWorker.StandardInput.Close();
                    _pixelSampleWorker.WaitForExit(2000);
                    if (!_pixelSampleWorker.HasExited) _pixelSampleWorker.Kill();
                }
            }
            catch { /* best effort - not critical if this fails */ }
            finally
            {
                _pixelSampleWorker.Dispose();
                _pixelSampleWorker = null;
            }
        }
    }
}
