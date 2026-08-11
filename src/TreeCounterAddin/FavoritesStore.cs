using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TreeCounterAddin
{
    // Persists favorited layer names per-project (keyed by the .aprx path) - same plain-
    // JSON, best-effort pattern as SettingsStore, just keyed by project since a layer name
    // is only meaningful within the map/project it came from (a "Slope_2026..." layer name
    // is common enough across different sites' projects that a single flat favorites list
    // would leak between them). One shared file rather than one per project folder, so
    // favorites don't litter/get lost if a project folder is moved or zipped up to share -
    // matches SettingsStore/ApiKeyStore's own %LOCALAPPDATA% location.
    internal static class FavoritesStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LandTreeAnalyzer", "favorites.json");

        private static Dictionary<string, List<string>> LoadAll()
        {
            try
            {
                if (!File.Exists(StorePath)) return new Dictionary<string, List<string>>();
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(StorePath))
                       ?? new Dictionary<string, List<string>>();
            }
            catch
            {
                // Missing/corrupt file - fail safe to "no favorites" instead of crashing the panel.
                return new Dictionary<string, List<string>>();
            }
        }

        public static HashSet<string> Load(string projectUri)
        {
            if (string.IsNullOrEmpty(projectUri)) return new HashSet<string>();
            var all = LoadAll();
            return all.TryGetValue(projectUri, out var names) ? new HashSet<string>(names) : new HashSet<string>();
        }

        public static void Save(string projectUri, IEnumerable<string> favoriteNames)
        {
            if (string.IsNullOrEmpty(projectUri)) return;
            try
            {
                var all = LoadAll();
                all[projectUri] = favoriteNames.ToList();
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(all));
            }
            catch
            {
                // Best-effort - a save failure shouldn't block anything the user is doing.
            }
        }
    }
}
