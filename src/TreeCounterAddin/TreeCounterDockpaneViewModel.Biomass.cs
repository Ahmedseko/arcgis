using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    internal partial class TreeCounterDockpaneViewModel
    {
        private string _selectedBiomassLayer;
        public string SelectedBiomassLayer
        {
            get => _selectedBiomassLayer;
            set => SetProperty(ref _selectedBiomassLayer, value);
        }

        // Defaults are generic tropical-forest IPCC Tier 1 values (wood density ~600 kg/m3,
        // biomass expansion factor ~1.5, root-to-shoot ratio 0.37, carbon fraction 0.47) -
        // meant to be edited per species mix/region, not treated as exact.
        private double _woodDensity = 600;
        public double WoodDensity
        {
            get => _woodDensity;
            set { if (SetProperty(ref _woodDensity, value)) SaveSettings(); }
        }

        private double _biomassExpansionFactor = 1.5;
        public double BiomassExpansionFactor
        {
            get => _biomassExpansionFactor;
            set { if (SetProperty(ref _biomassExpansionFactor, value)) SaveSettings(); }
        }

        private double _rootShootRatio = 0.37;
        public double RootShootRatio
        {
            get => _rootShootRatio;
            set { if (SetProperty(ref _rootShootRatio, value)) SaveSettings(); }
        }

        private double _carbonFraction = 0.47;
        public double CarbonFraction
        {
            get => _carbonFraction;
            set { if (SetProperty(ref _carbonFraction, value)) SaveSettings(); }
        }

        private bool _isEstimatingBiomass;
        public bool IsEstimatingBiomass
        {
            get => _isEstimatingBiomass;
            set => SetProperty(ref _isEstimatingBiomass, value);
        }

        private string _biomassStatus = "";
        public string BiomassStatus
        {
            get => _biomassStatus;
            set => SetProperty(ref _biomassStatus, value);
        }

        public ICommand EstimateBiomassCommand => new RelayCommand(async () => await EstimateBiomassAsync(), () => !IsEstimatingBiomass && SelectedBiomassLayer != null);

        // Volume-based (BEF/IPCC Tier 1 style) biomass estimate rather than a diameter/height
        // allometric equation - the cruising Excel already has per-tree Volume from this
        // org's own local volume tables, which is more reliable than a generic species-blind
        // allometric formula. Defaults are generic tropical-forest values (see property
        // comments); this is an approximation meant to be tuned per species mix/region, not
        // a substitute for a proper carbon inventory.
        private async Task EstimateBiomassAsync()
        {
            IsEstimatingBiomass = true;
            BiomassStatus = "Calculating...";
            try
            {
                if (MapView.Active == null)
                {
                    BiomassStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedBiomassLayer));
                if (layer == null)
                {
                    BiomassStatus = "Layer not found - click Refresh and pick again.";
                    return;
                }

                var (totalVolume, volumeField, sourcePath) = await QueuedTask.Run(() =>
                {
                    using var featureClass = layer.GetFeatureClass();
                    var volField = featureClass.GetDefinition().GetFields()
                        .FirstOrDefault(f => f.Name.StartsWith("Volume", StringComparison.OrdinalIgnoreCase))?.Name;
                    if (volField == null) return ((double?)null, (string)null, (string)null);

                    using var cursor = featureClass.Search(null, false);
                    double sum = 0;
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        if (feature[volField] is double v)
                            sum += v;
                    }
                    return ((double?)sum, volField, layer.GetPath()?.LocalPath);
                });

                if (totalVolume == null)
                {
                    BiomassStatus = "Layer has no Volume field - import cruising data with volume first.";
                    return;
                }

                var (totalBiomassKg, carbonKg, co2eKg) = BiomassMath.Estimate(
                    totalVolume.Value, WoodDensity, BiomassExpansionFactor, RootShootRatio, CarbonFraction);

                // Per-tree Biomass_kg/Carbon_kg fields, alongside the aggregate summary above -
                // same formula as BiomassMath.Estimate but evaluated per-row via
                // CalculateField's Python expression, so it doesn't need a second C# cursor
                // pass or an EditOperation to write values back. Soft-fail: the aggregate
                // numbers above are already useful without these fields.
                async Task<bool> AddAndCalcFieldAsync(string field, string expression)
                {
                    var addResult = await Geoprocessing.ExecuteToolAsync("management.AddField",
                        Geoprocessing.MakeValueArray(sourcePath, field, "DOUBLE"),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    if (addResult.IsFailed) return false;
                    var calcResult = await Geoprocessing.ExecuteToolAsync("management.CalculateField",
                        Geoprocessing.MakeValueArray(sourcePath, field, expression, "PYTHON3"),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    return !calcResult.IsFailed;
                }

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var biomassOk = sourcePath != null && await AddAndCalcFieldAsync("Biomass_kg",
                    $"!{volumeField}! * {WoodDensity.ToString(inv)} * {BiomassExpansionFactor.ToString(inv)} * (1 + {RootShootRatio.ToString(inv)})");
                var carbonOk = biomassOk && await AddAndCalcFieldAsync("Carbon_kg", $"!Biomass_kg! * {CarbonFraction.ToString(inv)}");

                BiomassStatus = $"Done: {totalVolume.Value:F1} m3 -> {totalBiomassKg / 1000:F2} t biomass, " +
                    $"{carbonKg / 1000:F2} t carbon, {co2eKg / 1000:F2} t CO2e." +
                    (carbonOk ? " Per-tree Biomass_kg/Carbon_kg fields added." : " Note: per-tree fields could not be added.");
            }
            catch (Exception ex)
            {
                BiomassStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsEstimatingBiomass = false;
            }
        }
    }
}
