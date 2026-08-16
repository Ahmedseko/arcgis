using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
// Alias to resolve against ArcGIS.Desktop.Mapping.FieldDescription (also in scope via the
// other using directives already in this file) - both are real, unrelated types with the
// same name; this file only ever means the DDL (schema-creation) one.
using FieldDescription = ArcGIS.Core.Data.DDL.FieldDescription;
using ShapeDescription = ArcGIS.Core.Data.DDL.ShapeDescription;
using FeatureClassDescription = ArcGIS.Core.Data.DDL.FeatureClassDescription;
using SchemaBuilder = ArcGIS.Core.Data.DDL.SchemaBuilder;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // "Color Reference Sampler" (Analyze tab) - click points on a raster to record their
    // exact RGB/ExG value into a real feature class, so ExG thresholds (Land Clearing/Road
    // Extraction/Tree Detection's cleared-land filter) can be calibrated against real
    // labeled colors instead of eyeballed screenshots - see ColorSamplerMapTool.cs for the
    // actual per-click pixel read. Label/Class are left blank here on purpose - filled in
    // afterward by the user directly in the layer's own Attribute Table (ArcGIS Pro already
    // has a perfectly good editable table UI; no need to build a second one).
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

        private int _colorSampleCount;
        public int ColorSampleCount
        {
            get => _colorSampleCount;
            set => SetProperty(ref _colorSampleCount, value);
        }

        private string _colorSamplerStatus = "";
        public string ColorSamplerStatus
        {
            get => _colorSamplerStatus;
            set => SetProperty(ref _colorSamplerStatus, value);
        }

        // Kept open for the whole sampling session (opened in StartColorSamplingAsync,
        // closed in StopColorSamplingAsync) instead of reopening per click - a fresh
        // Geodatabase connection per click would add lag to what's supposed to feel instant
        // for rapid successive clicks.
        private Geodatabase _colorSamplerGdb;
        private FeatureClass _colorSamplerFeatureClass;
        private string _colorSamplerFcPath;

        public ICommand StartColorSamplingCommand => new RelayCommand(async () => await StartColorSamplingAsync(),
            () => !IsColorSampling && SelectedColorSamplerRasterLayer != null);
        public ICommand StopColorSamplingCommand => new RelayCommand(async () => await StopColorSamplingAsync(), () => IsColorSampling);

        private async Task StartColorSamplingAsync()
        {
            try
            {
                var project = Project.Current;
                if (project == null) { ColorSamplerStatus = "No open ArcGIS Pro project. Create or open one first."; return; }
                if (MapView.Active == null) { ColorSamplerStatus = "No active map view. Open a map first."; return; }

                var map = MapView.Active.Map;
                var sr = map.SpatialReference;

                if (_colorSamplerFeatureClass == null)
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _colorSamplerFcPath = Path.Combine(project.DefaultGeodatabasePath, $"ColorReference_{stamp}");

                    await QueuedTask.Run(() =>
                    {
                        CreateColorReferenceFeatureClass(_colorSamplerFcPath, sr);
                        _colorSamplerGdb = new Geodatabase(new FileGeodatabaseConnectionPath(
                            new Uri(Path.GetDirectoryName(_colorSamplerFcPath))));
                        _colorSamplerFeatureClass = _colorSamplerGdb.OpenDataset<FeatureClass>(Path.GetFileName(_colorSamplerFcPath));

                        if (LayerFactory.Instance.CreateLayer(new Uri(_colorSamplerFcPath), map,
                                layerName: Path.GetFileName(_colorSamplerFcPath)) is FeatureLayer newLayer)
                        {
                            var symbol = SymbolFactory.Instance.ConstructPointSymbol(
                                ColorFactory.Instance.CreateRGBColor(235, 235, 225), 7, SimpleMarkerStyle.Circle);
                            newLayer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
                        }
                    });
                    ColorSampleCount = 0;
                }

                await FrameworkApplication.SetCurrentToolAsync("TreeCounterAddin_ColorSamplerTool");
                IsColorSampling = true;
                ColorSamplerStatus = $"Sampling active - click on the raster to add points. {ColorSampleCount} sample(s) so far.";
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
            ColorSamplerStatus = $"Stopped. {ColorSampleCount} sample(s) saved to " +
                $"{(_colorSamplerFcPath != null ? Path.GetFileName(_colorSamplerFcPath) : "")}. " +
                "Fill in Label/Class in its Attribute Table.";
        }

        // Runs on the MCT (called from SchemaBuilder's own thread requirement).
        private static void CreateColorReferenceFeatureClass(string fcPath, SpatialReference sr)
        {
            var gdbFolder = Path.GetDirectoryName(fcPath);
            var fcName = Path.GetFileName(fcPath);
            using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbFolder)));

            var fields = new List<FieldDescription>
            {
                FieldDescription.CreateStringField("Label", 254),
                FieldDescription.CreateStringField("Class", 60),
                FieldDescription.CreateIntegerField("R"),
                FieldDescription.CreateIntegerField("G"),
                FieldDescription.CreateIntegerField("B"),
                new FieldDescription("ExG", FieldType.Double),
            };
            var shapeDescription = new ShapeDescription(GeometryType.Point, sr);
            var fcDescription = new FeatureClassDescription(fcName, fields, shapeDescription);

            var schemaBuilder = new SchemaBuilder(geodatabase);
            schemaBuilder.Create(fcDescription);
            if (!schemaBuilder.Build())
                throw new Exception(string.Join("; ", schemaBuilder.ErrorMessages));
        }

        // Called from ColorSamplerMapTool.HandleMouseDownAsync, already on the MCT.
        internal void AddColorSample(MapPoint point, int r, int g, int b, double exgValue)
        {
            if (_colorSamplerFeatureClass == null) return;
            try
            {
                _colorSamplerGdb.ApplyEdits(() =>
                {
                    using var insertCursor = _colorSamplerFeatureClass.CreateInsertCursor();
                    using var rowBuffer = _colorSamplerFeatureClass.CreateRowBuffer();
                    rowBuffer["Shape"] = point;
                    rowBuffer["Label"] = "";
                    rowBuffer["Class"] = "";
                    rowBuffer["R"] = r;
                    rowBuffer["G"] = g;
                    rowBuffer["B"] = b;
                    rowBuffer["ExG"] = exgValue;
                    insertCursor.Insert(rowBuffer);
                    insertCursor.Flush();
                });

                ColorSampleCount++;
                ColorSamplerStatus = $"Sampling active - {ColorSampleCount} sample(s) so far. " +
                    $"Last: RGB({r},{g},{b}), ExG {(exgValue >= 0 ? "+" : "")}{exgValue:F0}.";
            }
            catch (Exception ex)
            {
                ColorSamplerStatus = $"Failed to save sample: {ex.Message}";
            }
        }
    }
}
