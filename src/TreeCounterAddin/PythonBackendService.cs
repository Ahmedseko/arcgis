using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TreeCounterAddin
{
    internal record DetectionRequest(
        string RasterPath, string Profile, string OutputFc,
        int Sigma, double ExgThreshold, double MinSmooth,
        string Provider = null, string ApiKey = null, string Model = null);

    internal record DetectionResult(bool Success, bool Cancelled, int TreeCount, string OutputFc, string ErrorMessage, double AreaHa = 0);

    internal record LandClearingRequest(
        string RasterPath, string OutputFc, double ExgThreshold, double SmoothPx, double MinAreaM2, string ExcludeFc = null);

    internal record LandClearingResult(bool Success, bool Cancelled, int PolygonCount, string OutputFc, string ErrorMessage, double AreaHa = 0);

    // Shells out to the ArcGIS Pro conda python + backend/detect.py (ported ExG matched-filter
    // + YOLOv8n ONNX pipeline from qgis_plugin/tree_counter, plus optional Gemini Vision
    // validation) instead of reimplementing the numpy/scipy-heavy detection math in C#.
    internal static class PythonBackendService
    {
        private static readonly string ProPythonExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ArcGIS", "Pro", "bin", "Python", "envs", "arcgispro-py3", "python.exe");

        // ArcGIS Pro loads add-ins from %LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{guid}\
        // (a copy extracted from the .esriAddinX package) into a custom AssemblyLoadContext -
        // AppDomain.CurrentDomain.BaseDirectory still points at ArcGISPro.exe's own folder
        // (there's one AppDomain for the whole process), so it can't be used to find files
        // shipped alongside this specific DLL. Assembly.Location does give the right folder.
        // backend/ is copied into the package (see deploy.ps1) as a sibling of the DLL.
        private static readonly string BackendDir = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            "backend");
        private static readonly string DetectScript = Path.Combine(BackendDir, "detect.py");
        private static readonly string DetectClearingScript = Path.Combine(BackendDir, "detect_clearing.py");
        private static readonly string CheckApiKeyScript = Path.Combine(BackendDir, "check_api_key.py");

        public static async Task<DetectionResult> RunDetectionAsync(
            DetectionRequest request, Action<int> onProgress = null, Action<string> onStage = null,
            CancellationToken cancellationToken = default)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(DetectScript);

            if (!File.Exists(scriptPath))
                return new DetectionResult(false, false, 0, null, $"Script not found: {scriptPath}");

            var summaryPath = Path.Combine(Path.GetTempPath(), $"tree_counter_{Guid.NewGuid():N}.json");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--raster"); psi.ArgumentList.Add(request.RasterPath);
            psi.ArgumentList.Add("--profile"); psi.ArgumentList.Add(request.Profile);
            psi.ArgumentList.Add("--output-fc"); psi.ArgumentList.Add(request.OutputFc);
            psi.ArgumentList.Add("--summary"); psi.ArgumentList.Add(summaryPath);
            psi.ArgumentList.Add("--sigma"); psi.ArgumentList.Add(request.Sigma.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--exg-threshold"); psi.ArgumentList.Add(request.ExgThreshold.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--min-smooth"); psi.ArgumentList.Add(request.MinSmooth.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                psi.ArgumentList.Add("--ai-provider"); psi.ArgumentList.Add(request.Provider ?? "gemini");
                psi.ArgumentList.Add("--api-key"); psi.ArgumentList.Add(request.ApiKey);
                if (!string.IsNullOrWhiteSpace(request.Model))
                {
                    psi.ArgumentList.Add("--ai-model"); psi.ArgumentList.Add(request.Model);
                }
            }

            try
            {
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("PROGRESS ") && int.TryParse(e.Data.AsSpan(9), out var pct))
                        onProgress?.Invoke(pct);
                    else if (e.Data.StartsWith("STAGE "))
                        onStage?.Invoke(e.Data[6..]);
                };
                process.Start();
                process.BeginOutputReadLine();

                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* already exited */ }
                });

                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (cancellationToken.IsCancellationRequested)
                    return new DetectionResult(false, true, 0, null, "Cancelled.");

                if (process.ExitCode != 0)
                {
                    // A nonzero exit with empty stderr means python.exe died without a
                    // catchable Python-level exception (e.g. OS-level out-of-memory kill,
                    // native crash in numpy/scipy) - say so instead of showing a blank message.
                    var message = string.IsNullOrWhiteSpace(stderr)
                        ? $"python.exe exited with code {process.ExitCode} and no error output (possible out-of-memory or native crash)."
                        : stderr;
                    return new DetectionResult(false, false, 0, null, message);
                }

                var json = await File.ReadAllTextAsync(summaryPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new DetectionResult(
                    true, false,
                    root.GetProperty("tree_count").GetInt32(),
                    root.GetProperty("output_fc").GetString(),
                    null,
                    root.GetProperty("area_ha").GetDouble());
            }
            catch (Exception ex)
            {
                return new DetectionResult(false, false, 0, null, ex.Message);
            }
        }

        // Shells out to backend/detect_clearing.py (land_clearing.py's chunked ExG-low
        // detector + arcpy RasterToPolygon) - same subprocess/PROGRESS/STAGE contract as
        // RunDetectionAsync above, just a different script and result shape (polygons,
        // not points).
        public static async Task<LandClearingResult> RunLandClearingAsync(
            LandClearingRequest request, Action<int> onProgress = null, Action<string> onStage = null,
            CancellationToken cancellationToken = default)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(DetectClearingScript);

            if (!File.Exists(scriptPath))
                return new LandClearingResult(false, false, 0, null, $"Script not found: {scriptPath}");

            var summaryPath = Path.Combine(Path.GetTempPath(), $"land_clearing_{Guid.NewGuid():N}.json");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--raster"); psi.ArgumentList.Add(request.RasterPath);
            psi.ArgumentList.Add("--output-fc"); psi.ArgumentList.Add(request.OutputFc);
            psi.ArgumentList.Add("--summary"); psi.ArgumentList.Add(summaryPath);
            psi.ArgumentList.Add("--exg-threshold"); psi.ArgumentList.Add(request.ExgThreshold.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--smooth-px"); psi.ArgumentList.Add(request.SmoothPx.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--min-area-m2"); psi.ArgumentList.Add(request.MinAreaM2.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(request.ExcludeFc))
            {
                psi.ArgumentList.Add("--exclude-fc"); psi.ArgumentList.Add(request.ExcludeFc);
            }

            try
            {
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("PROGRESS ") && int.TryParse(e.Data.AsSpan(9), out var pct))
                        onProgress?.Invoke(pct);
                    else if (e.Data.StartsWith("STAGE "))
                        onStage?.Invoke(e.Data[6..]);
                };
                process.Start();
                process.BeginOutputReadLine();

                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* already exited */ }
                });

                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (cancellationToken.IsCancellationRequested)
                    return new LandClearingResult(false, true, 0, null, "Cancelled.");

                if (process.ExitCode != 0)
                {
                    var message = string.IsNullOrWhiteSpace(stderr)
                        ? $"python.exe exited with code {process.ExitCode} and no error output (possible out-of-memory or native crash)."
                        : stderr;
                    return new LandClearingResult(false, false, 0, null, message);
                }

                var json = await File.ReadAllTextAsync(summaryPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new LandClearingResult(
                    true, false,
                    root.GetProperty("polygon_count").GetInt32(),
                    root.GetProperty("output_fc").GetString(),
                    null,
                    root.GetProperty("area_ha").GetDouble());
            }
            catch (Exception ex)
            {
                return new LandClearingResult(false, false, 0, null, ex.Message);
            }
        }

        // Raster-free round trip via backend/check_api_key.py, for the "Test Key" button -
        // much cheaper/faster than kicking off a full detection just to validate a key.
        public static async Task<(bool Ok, string Message)> TestApiKeyAsync(
            string provider, string apiKey, string model, CancellationToken cancellationToken = default)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(CheckApiKeyScript);
            if (!File.Exists(scriptPath))
                return (false, $"Script not found: {scriptPath}");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--provider"); psi.ArgumentList.Add(provider);
            psi.ArgumentList.Add("--api-key"); psi.ArgumentList.Add(apiKey);
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* already exited */ }
                });

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var message = string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
                return (process.ExitCode == 0, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
