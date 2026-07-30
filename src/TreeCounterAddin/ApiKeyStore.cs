using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TreeCounterAddin
{
    // Persists per-provider AI vision API keys across ArcGIS Pro sessions, so the user
    // doesn't have to retype every key each time they reopen the panel. Encrypted at rest
    // with Windows DPAPI (CurrentUser scope) via ProtectedData - only readable by the same
    // Windows account on the same machine, without hand-rolling crypt32.dll P/Invoke.
    internal static class ApiKeyStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LandTreeAnalyzer", "apikeys.dat");

        public static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return new();
                var encrypted = File.ReadAllBytes(StorePath);
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch
            {
                // Missing/corrupt file, or DPAPI can't decrypt (different user/machine) -
                // fail safe to "no saved keys" instead of crashing the panel on open.
                return new();
            }
        }

        public static void Save(Dictionary<string, string> keysByProvider)
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(keysByProvider);
                var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(StorePath, encrypted);
            }
            catch
            {
                // Best-effort - a save failure shouldn't block detection.
            }
        }
    }
}
