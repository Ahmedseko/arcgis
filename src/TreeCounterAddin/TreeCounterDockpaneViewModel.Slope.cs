using ArcGIS.Core.CIM;
using ArcGIS.Core.Licensing;
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
        private string _selectedDemLayer;
        public string SelectedDemLayer
        {
            get => _selectedDemLayer;
            set => SetProperty(ref _selectedDemLayer, value);
        }

        private bool _isComputingSlope;
        public bool IsComputingSlope
        {
            get => _isComputingSlope;
            set => SetProperty(ref _isComputingSlope, value);
        }

        private string _slopeStatus = "";
        public string SlopeStatus
        {
            get => _slopeStatus;
            set => SetProperty(ref _slopeStatus, value);
        }

        private CancellationTokenSource _slopeCts;

        public ICommand ComputeSlopeCommand => new RelayCommand(async () => await ComputeSlopeAsync(), () => !IsComputingSlope && SelectedDemLayer != null);
        public ICommand CancelSlopeCommand => new RelayCommand(() => _slopeCts?.Cancel(), () => IsComputingSlope);

        // Reuses Esri's Spatial Analyst sa.Slope GP tool - requires a Spatial Analyst
        // license; if unavailable, the tool fails and that failure surfaces via the normal
        // IsFailed/ErrorMessages path below like any other GP failure in this file.
        private async Task ComputeSlopeAsync()
        {
            IsComputingSlope = true;
            SlopeStatus = "Computing slope...";
            _slopeCts = new CancellationTokenSource();
            try
            {
                var project = Project.Current;
                if (project == null)
                {
                    SlopeStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }
                if (MapView.Active == null)
                {
                    SlopeStatus = "No active map view. Open a map first.";
                    return;
                }

                // Checked upfront (GetExpirationDate returns null if unavailable, without
                // consuming a concurrent-use seat the way CheckoutLicense would) so an
                // unlicensed Spatial Analyst fails fast with a clear reason, instead of only
                // surfacing after the sa.Slope GP call has already run and failed.
                var hasSpatialAnalyst = await QueuedTask.Run(() =>
                    LicenseInformation.GetExpirationDate(LicenseCodes.SpatialAnalyst) != null);
                if (!hasSpatialAnalyst)
                {
                    SlopeStatus = "Spatial Analyst extension is not licensed - Slope needs it. Check ArcGIS Pro's licensing/extensions.";
                    return;
                }

                var map = MapView.Active.Map;
                var demLayer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedDemLayer));
                if (demLayer == null)
                {
                    SlopeStatus = "DEM layer not found - click Refresh and pick again.";
                    return;
                }

                var (demPath, bandCount) = await QueuedTask.Run(() =>
                    (demLayer.GetPath()?.LocalPath, demLayer.GetRaster()?.GetBandCount() ?? 0));
                if (demPath == null)
                {
                    SlopeStatus = "Failed to read the DEM layer's data path.";
                    return;
                }
                // A DEM is a single-band elevation surface (pixel value = meters of
                // height) - a multi-band raster is almost certainly an RGB/multispectral
                // orthophoto, not elevation. Running Slope on that reads color values as
                // if they were height, producing meaningless output with no error from the
                // GP tool itself (it doesn't know the data's semantic meaning, only its
                // shape) - this is a plain band-count check, not a real DEM validator.
                if (bandCount > 1)
                {
                    SlopeStatus = $"\"{SelectedDemLayer}\" has {bandCount} bands - that's an RGB/multispectral image, not a DEM. " +
                        "Pick a single-band elevation raster instead.";
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var slopeRaster = Path.Combine(project.DefaultGeodatabasePath, $"Slope_{stamp}");

                var result = await Geoprocessing.ExecuteToolAsync("sa.Slope",
                    Geoprocessing.MakeValueArray(demPath, slopeRaster, "PERCENT_RISE", 1),
                    null, cancelToken: _slopeCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (result.IsFailed)
                {
                    SlopeStatus = $"Failed to compute slope: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}";
                    return;
                }

                // Classified into standard forestry accessibility bands (<=15% easy,
                // 15-25% moderate, 25-40% difficult, >40% restricted) rather than left on
                // ArcGIS Pro's default grayscale stretch, so the map reads as an
                // accessibility map at a glance instead of needing manual symbology setup.
                // ClassifyColorizerDefinition's classification method only seeds a starting
                // colorizer with the right number of breaks - the actual break values/colors
                // below are overwritten with these fixed bands regardless of what the
                // auto-classification computed from the data.
                await QueuedTask.Run(() =>
                {
                    if (LayerFactory.Instance.CreateLayer(new Uri(slopeRaster), map, layerName: Path.GetFileName(slopeRaster)) is not RasterLayer slopeLayer)
                        return;

                    var colorizerDef = new ClassifyColorizerDefinition
                    {
                        FieldName = "Value",
                        NumberOfClasses = 4,
                        ClassificationType = ClassificationMethod.EqualInterval
                    };
                    if (slopeLayer.CreateColorizer(colorizerDef) is not CIMRasterClassifyColorizer colorizer
                        || colorizer.ClassBreaks == null || colorizer.ClassBreaks.Length < 4)
                        return;

                    var breaks = colorizer.ClassBreaks;
                    breaks[0].UpperBound = 15;
                    breaks[0].Label = "<= 15% (easy)";
                    breaks[0].Color = ColorFactory.Instance.CreateRGBColor(120, 198, 121);
                    breaks[1].UpperBound = 25;
                    breaks[1].Label = "15-25% (moderate)";
                    breaks[1].Color = ColorFactory.Instance.CreateRGBColor(255, 237, 111);
                    breaks[2].UpperBound = 40;
                    breaks[2].Label = "25-40% (difficult)";
                    breaks[2].Color = ColorFactory.Instance.CreateRGBColor(252, 141, 89);
                    breaks[3].UpperBound = Math.Max(breaks[3].UpperBound, 999);
                    breaks[3].Label = "> 40% (restricted)";
                    breaks[3].Color = ColorFactory.Instance.CreateRGBColor(215, 48, 39);

                    slopeLayer.SetColorizer(colorizer);
                });

                SlopeStatus = "Done: slope raster (% rise) added to map, classified into accessibility bands " +
                    "(green <=15%, yellow 15-25%, orange 25-40%, red >40%).";
            }
            catch (OperationCanceledException)
            {
                SlopeStatus = "Cancelled.";
            }
            catch (Exception ex)
            {
                SlopeStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                _slopeCts?.Dispose();
                _slopeCts = null;
                IsComputingSlope = false;
            }
        }
    }
}
