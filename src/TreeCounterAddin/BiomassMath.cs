namespace TreeCounterAddin
{
    // Pure volume-based (IPCC Tier 1 style) biomass/carbon math - deliberately free of any
    // ArcGIS reference so it can be exercised by a plain console project without ArcGIS Pro
    // installed (see src/ForestryToolkit.MathTests).
    internal static class BiomassMath
    {
        // 3.667 = 44/12, the molecular weight ratio for converting carbon mass to CO2
        // equivalent mass - a standard constant in carbon accounting, not a tuning knob.
        private const double CarbonToCo2e = 3.667;

        public static (double BiomassKg, double CarbonKg, double Co2eKg) Estimate(
            double totalVolumeM3, double woodDensityKgM3, double biomassExpansionFactor,
            double rootShootRatio, double carbonFraction)
        {
            var aboveGroundBiomassKg = totalVolumeM3 * woodDensityKgM3 * biomassExpansionFactor;
            var totalBiomassKg = aboveGroundBiomassKg * (1 + rootShootRatio);
            var carbonKg = totalBiomassKg * carbonFraction;
            var co2eKg = carbonKg * CarbonToCo2e;
            return (totalBiomassKg, carbonKg, co2eKg);
        }
    }
}
