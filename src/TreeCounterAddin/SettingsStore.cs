using System;
using System.IO;
using System.Text.Json;

namespace TreeCounterAddin
{
    // Persists non-secret user preferences (fishnet cell size, cruising import WKID,
    // biomass/carbon constants) across ArcGIS Pro sessions - plain JSON, no encryption
    // needed since nothing here is sensitive (unlike ApiKeyStore, which handles API keys).
    internal static class SettingsStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LandTreeAnalyzer", "settings.json");

        public class Settings
        {
            public double CellWidth { get; set; } = 50;
            public double CellHeight { get; set; } = 50;
            public int CruisingWkid { get; set; } = 32750;
            public double WoodDensity { get; set; } = 600;
            public double BiomassExpansionFactor { get; set; } = 1.5;
            public double RootShootRatio { get; set; } = 0.37;
            public double CarbonFraction { get; set; } = 0.47;

            // Added 2026-08-17 - was in-memory-only (always reset to true on every ArcGIS
            // Pro restart/add-in reinstall regardless of what the user last picked), which
            // silently re-enabled AI Vision Validation after a real report of it running up
            // an unexpected API bill. Same on-every-set persistence as everything else here.
            public bool UseAiValidation { get; set; } = true;
        }

        public static Settings Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return new Settings();
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(StorePath)) ?? new Settings();
            }
            catch
            {
                // Missing/corrupt file - fail safe to defaults instead of crashing the panel on open.
                return new Settings();
            }
        }

        public static void Save(Settings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(settings));
            }
            catch
            {
                // Best-effort - a save failure shouldn't block anything the user is doing.
            }
        }
    }
}
