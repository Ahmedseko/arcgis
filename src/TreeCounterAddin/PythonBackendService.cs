using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TreeCounterAddin
{
    internal record DetectionRequest(
        string RasterPath, string Profile, string OutputFc,
        int Sigma, double ExgThreshold, double MinSmooth, bool ExcludeClearedLand = false,
        string ExcludeFc = null,
        string Provider = null, string ApiKey = null, string Model = null);

    internal record DetectionResult(bool Success, bool Cancelled, int TreeCount, string OutputFc, string ErrorMessage, double AreaHa = 0, int FilteredClearedCount = 0, int ExcludedByAreaCount = 0, int RejectedByAiCount = 0);

    internal record LandClearingRequest(
        string RasterPath, string OutputFc, double ExgThreshold, double SmoothPx, double MinAreaM2, string ExcludeFc = null,
        int OpeningIterations = 6, int ClosingIterations = 15, bool FreshColor = false, double BrightMin = 120.0,
        double FillHoleAreaM2 = 2000.0, string Provider = null, string ApiKey = null, string Model = null);

    internal record LandClearingResult(bool Success, bool Cancelled, int PolygonCount, string OutputFc, string ErrorMessage, double AreaHa = 0, int RejectedByAiCount = 0);

    internal record RoadExtractionRequest(
        string RasterPath, string OutputFc, double ExgThreshold, double SmoothPx, double MinDangleM,
        double MaxWidthM = 0,
        string Provider = null, string ApiKey = null, string Model = null);

    internal record RoadExtractionResult(bool Success, bool Cancelled, int LineCount, string OutputFc, string ErrorMessage, double LengthKm = 0, int RejectedByAiCount = 0);

    internal record CompareChangesRequest(
        string OldFc, string NewFc, string OutputLostFc, string OutputNewFc, double MaxDistM);

    internal record CompareChangesResult(
        bool Success, int LostCount, int NewCount, int MatchedCount, string LostFc, string NewFc, string ErrorMessage);

    internal record ColorSample(double X, double Y, int R, int G, int B, double ExG, string Category = "");

    internal record SaveColorSamplesResult(bool Success, string OutputFc, int Count, string ErrorMessage);

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
        private static readonly string DetectRoadsScript = Path.Combine(BackendDir, "detect_roads.py");
        private static readonly string CheckApiKeyScript = Path.Combine(BackendDir, "check_api_key.py");
        private static readonly string CompareDetectionsScript = Path.Combine(BackendDir, "compare_detections.py");
        private static readonly string SaveColorSamplesScript = Path.Combine(BackendDir, "save_color_samples.py");
        private static readonly string PixelSampleServerScript = Path.Combine(BackendDir, "pixel_sample_server.py");

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
            if (request.ExcludeClearedLand)
            {
                psi.ArgumentList.Add("--exclude-cleared");
            }
            if (!string.IsNullOrWhiteSpace(request.ExcludeFc))
            {
                psi.ArgumentList.Add("--exclude-fc"); psi.ArgumentList.Add(request.ExcludeFc);
            }
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
                    root.GetProperty("area_ha").GetDouble(),
                    root.TryGetProperty("filtered_cleared_count", out var fc) ? fc.GetInt32() : 0,
                    root.TryGetProperty("excluded_by_area_count", out var ec) ? ec.GetInt32() : 0,
                    root.TryGetProperty("rejected_by_ai_count", out var rc) ? rc.GetInt32() : 0);
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
            psi.ArgumentList.Add("--opening-iterations"); psi.ArgumentList.Add(request.OpeningIterations.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--closing-iterations"); psi.ArgumentList.Add(request.ClosingIterations.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--min-area-m2"); psi.ArgumentList.Add(request.MinAreaM2.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--fill-hole-area-m2"); psi.ArgumentList.Add(request.FillHoleAreaM2.ToString(CultureInfo.InvariantCulture));
            if (request.FreshColor)
            {
                psi.ArgumentList.Add("--fresh-color");
                psi.ArgumentList.Add("--bright-min"); psi.ArgumentList.Add(request.BrightMin.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrWhiteSpace(request.ExcludeFc))
            {
                psi.ArgumentList.Add("--exclude-fc"); psi.ArgumentList.Add(request.ExcludeFc);
            }
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
                    root.GetProperty("area_ha").GetDouble(),
                    root.TryGetProperty("rejected_by_ai_count", out var rc) ? rc.GetInt32() : 0);
            }
            catch (Exception ex)
            {
                return new LandClearingResult(false, false, 0, null, ex.Message);
            }
        }

        // Shells out to backend/detect_roads.py (road_extraction.py's chunked ExG-low
        // mask reused from land_clearing.py + skimage skeletonize + arcpy's own
        // RasterToPolyline) - same subprocess/PROGRESS/STAGE/cancel contract as
        // RunLandClearingAsync above, just a different script and result shape
        // (polylines, not polygons).
        public static async Task<RoadExtractionResult> RunRoadExtractionAsync(
            RoadExtractionRequest request, Action<int> onProgress = null, Action<string> onStage = null,
            CancellationToken cancellationToken = default)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(DetectRoadsScript);

            if (!File.Exists(scriptPath))
                return new RoadExtractionResult(false, false, 0, null, $"Script not found: {scriptPath}");

            var summaryPath = Path.Combine(Path.GetTempPath(), $"road_extraction_{Guid.NewGuid():N}.json");

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
            psi.ArgumentList.Add("--min-dangle-m"); psi.ArgumentList.Add(request.MinDangleM.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--max-width-m"); psi.ArgumentList.Add(request.MaxWidthM.ToString(CultureInfo.InvariantCulture));
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
                    return new RoadExtractionResult(false, true, 0, null, "Cancelled.");

                if (process.ExitCode != 0)
                {
                    var message = string.IsNullOrWhiteSpace(stderr)
                        ? $"python.exe exited with code {process.ExitCode} and no error output (possible out-of-memory or native crash)."
                        : stderr;
                    return new RoadExtractionResult(false, false, 0, null, message);
                }

                var json = await File.ReadAllTextAsync(summaryPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new RoadExtractionResult(
                    true, false,
                    root.GetProperty("line_count").GetInt32(),
                    root.GetProperty("output_fc").GetString(),
                    null,
                    root.GetProperty("length_km").GetDouble(),
                    root.TryGetProperty("rejected_by_ai_count", out var rc) ? rc.GetInt32() : 0);
            }
            catch (Exception ex)
            {
                return new RoadExtractionResult(false, false, 0, null, ex.Message);
            }
        }

        // Shells out to backend/compare_detections.py - a quick cKDTree nearest-neighbor
        // match (no chunked raster scan), so unlike RunDetectionAsync/RunLandClearingAsync
        // this has no PROGRESS/STAGE reporting or cancellation, same as TestApiKeyAsync
        // below - just run to completion and read the summary.
        public static async Task<CompareChangesResult> RunCompareChangesAsync(
            CompareChangesRequest request, CancellationToken cancellationToken = default)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(CompareDetectionsScript);

            if (!File.Exists(scriptPath))
                return new CompareChangesResult(false, 0, 0, 0, null, null, $"Script not found: {scriptPath}");

            var summaryPath = Path.Combine(Path.GetTempPath(), $"compare_changes_{Guid.NewGuid():N}.json");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--old-fc"); psi.ArgumentList.Add(request.OldFc);
            psi.ArgumentList.Add("--new-fc"); psi.ArgumentList.Add(request.NewFc);
            psi.ArgumentList.Add("--output-lost-fc"); psi.ArgumentList.Add(request.OutputLostFc);
            psi.ArgumentList.Add("--output-new-fc"); psi.ArgumentList.Add(request.OutputNewFc);
            psi.ArgumentList.Add("--summary"); psi.ArgumentList.Add(summaryPath);
            psi.ArgumentList.Add("--max-dist-m"); psi.ArgumentList.Add(request.MaxDistM.ToString(CultureInfo.InvariantCulture));

            try
            {
                using var process = new Process { StartInfo = psi };

                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* already exited */ }
                });

                process.Start();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var message = string.IsNullOrWhiteSpace(stderr)
                        ? $"python.exe exited with code {process.ExitCode} and no error output."
                        : stderr;
                    return new CompareChangesResult(false, 0, 0, 0, null, null, message);
                }

                var json = await File.ReadAllTextAsync(summaryPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new CompareChangesResult(
                    true,
                    root.GetProperty("lost_count").GetInt32(),
                    root.GetProperty("new_count").GetInt32(),
                    root.GetProperty("matched_count").GetInt32(),
                    root.GetProperty("lost_fc").GetString(),
                    root.GetProperty("new_fc").GetString(),
                    null);
            }
            catch (Exception ex)
            {
                return new CompareChangesResult(false, 0, 0, 0, null, null, ex.Message);
            }
        }

        // Shells out to backend/save_color_samples.py - deliberately arcpy-based feature
        // class creation (same pattern as detect.py's _write_feature_class) rather than
        // ArcGIS.Core.Data.DDL/SchemaBuilder directly in C#, after that crashed ArcGIS Pro
        // outright at creation time in testing (real report, 2026-08-16). Called once, when
        // the user clicks Stop Sampling - not per click, so the samples list is built up
        // entirely in memory (TreeCounterDockpaneViewModel.ColorSampler.cs) first.
        public static async Task<SaveColorSamplesResult> SaveColorSamplesAsync(
            string referenceRasterPath, string outputFc, IReadOnlyList<ColorSample> samples,
            CancellationToken cancellationToken = default)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(SaveColorSamplesScript);
            if (!File.Exists(scriptPath))
                return new SaveColorSamplesResult(false, null, 0, $"Script not found: {scriptPath}");

            var samplesJsonPath = Path.Combine(Path.GetTempPath(), $"color_samples_{Guid.NewGuid():N}.json");
            var summaryPath = Path.Combine(Path.GetTempPath(), $"color_samples_summary_{Guid.NewGuid():N}.json");

            await File.WriteAllTextAsync(samplesJsonPath, JsonSerializer.Serialize(samples.Select(s =>
                new { x = s.X, y = s.Y, r = s.R, g = s.G, b = s.B, exg = s.ExG, cls = s.Category })), cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--reference-raster"); psi.ArgumentList.Add(referenceRasterPath);
            psi.ArgumentList.Add("--output-fc"); psi.ArgumentList.Add(outputFc);
            psi.ArgumentList.Add("--samples-json"); psi.ArgumentList.Add(samplesJsonPath);
            psi.ArgumentList.Add("--summary"); psi.ArgumentList.Add(summaryPath);

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var message = string.IsNullOrWhiteSpace(stderr)
                        ? $"python.exe exited with code {process.ExitCode} and no error output."
                        : stderr;
                    return new SaveColorSamplesResult(false, null, 0, message);
                }

                var json = await File.ReadAllTextAsync(summaryPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new SaveColorSamplesResult(true, root.GetProperty("output_fc").GetString(), root.GetProperty("count").GetInt32(), null);
            }
            catch (Exception ex)
            {
                return new SaveColorSamplesResult(false, null, 0, ex.Message);
            }
            finally
            {
                try { File.Delete(samplesJsonPath); } catch { /* best effort */ }
                try { File.Delete(summaryPath); } catch { /* best effort */ }
            }
        }

        // Starts backend/pixel_sample_server.py as a long-lived worker (stdin/stdout line
        // protocol, see that script's own comment for why this exists as a standing
        // process instead of one-shot calls) - caller owns the returned Process: write
        // "x,y\n" to StandardInput, read one reply line from StandardOutput per request,
        // and close StandardInput (or Kill) to end it. Not wrapped in a Task since starting
        // a process is synchronous/cheap - only per-request I/O needs to be awaited, done
        // by the caller directly against the returned Process's own streams.
        public static Process StartPixelSampleWorker(string rasterPath)
        {
            var pythonExe = File.Exists(ProPythonExe) ? ProPythonExe : "python";
            var scriptPath = Path.GetFullPath(PixelSampleServerScript);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--raster"); psi.ArgumentList.Add(rasterPath);

            var process = new Process { StartInfo = psi };
            process.Start();
            return process;
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
