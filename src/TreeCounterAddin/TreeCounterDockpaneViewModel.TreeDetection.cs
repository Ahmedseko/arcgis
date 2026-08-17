using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Detection itself runs in backend/detect.py (ported ExG + YOLO pipeline from
    // qgis_plugin/tree_counter) via PythonBackendService; this ViewModel only handles
    // picking inputs/parameters and loading the resulting feature class onto the map.
    internal partial class TreeCounterDockpaneViewModel
    {
        private static readonly Dictionary<string, (int Sigma, double ExgThreshold, double MinSmooth)> ProfileDefaults = new()
        {
            ["Natural Forest"] = (75, 18, 10),
            ["Oil Palm Plantation"] = (20, 10, 30),
        };

        private CancellationTokenSource _runCts;

        public ObservableCollection<string> Profiles { get; } = new(ProfileDefaults.Keys);

        private string _selectedRasterLayer;
        public string SelectedRasterLayer
        {
            get => _selectedRasterLayer;
            set => SetProperty(ref _selectedRasterLayer, value);
        }

        private string _selectedProfile = "Oil Palm Plantation";
        public string SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (SetProperty(ref _selectedProfile, value) && ProfileDefaults.TryGetValue(value, out var d))
                {
                    Sigma = d.Sigma;
                    ExgThreshold = d.ExgThreshold;
                    MinSmooth = d.MinSmooth;
                }
            }
        }

        private int _sigma = ProfileDefaults["Oil Palm Plantation"].Sigma;
        public int Sigma
        {
            get => _sigma;
            set => SetProperty(ref _sigma, value);
        }

        private double _exgThreshold = ProfileDefaults["Oil Palm Plantation"].ExgThreshold;
        public double ExgThreshold
        {
            get => _exgThreshold;
            set => SetProperty(ref _exgThreshold, value);
        }

        private double _minSmooth = ProfileDefaults["Oil Palm Plantation"].MinSmooth;
        public double MinSmooth
        {
            get => _minSmooth;
            set => SetProperty(ref _minSmooth, value);
        }

        private static readonly Dictionary<string, string[]> ModelsByProvider = new()
        {
            ["Gemini"] = new[] { "gemini-3.5-flash", "gemini-3.1-flash-lite", "gemini-3.1-pro", "gemini-2.5-flash" },
            ["OpenAI"] = new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini" },
            ["Claude"] = new[] { "claude-haiku-4-5", "claude-sonnet-5", "claude-opus-5" },
        };

        // Keyed by provider name so switching Provider doesn't clobber a key you already
        // typed for another provider - each provider remembers its own key.
        private readonly Dictionary<string, string> _apiKeysByProvider = new();

        public ObservableCollection<string> Providers { get; } = new(ModelsByProvider.Keys);
        public ObservableCollection<string> Models { get; } = new(ModelsByProvider["Gemini"]);

        private string _selectedProvider = "Gemini";
        public string SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                var previous = _selectedProvider;
                if (SetProperty(ref _selectedProvider, value))
                {
                    _apiKeysByProvider[previous] = ApiKey ?? "";

                    Models.Clear();
                    foreach (var m in ModelsByProvider[value])
                        Models.Add(m);
                    SelectedModel = Models.FirstOrDefault();

                    ApiKey = _apiKeysByProvider.TryGetValue(value, out var savedKey) ? savedKey : "";
                }
            }
        }

        private string _selectedModel = ModelsByProvider["Gemini"][0];
        public string SelectedModel
        {
            get => _selectedModel;
            set => SetProperty(ref _selectedModel, value);
        }

        private string _apiKey;
        public string ApiKey
        {
            get => _apiKey;
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    _apiKeysByProvider[SelectedProvider] = value ?? "";
                    ApiKeyStore.Save(_apiKeysByProvider);
                }
            }
        }

        // Separate from "is ApiKey filled in" - a real report (2026-08-16) asked for a way
        // to turn AI Vision Validation off without clearing/losing the saved key, e.g. to
        // run a faster key-free pass first. Defaults on so an unchanged panel behaves
        // exactly as before this existed (AI runs whenever a key is present).
        private bool _useAiValidation = true;
        public bool UseAiValidation
        {
            get => _useAiValidation;
            set { if (SetProperty(ref _useAiValidation, value)) SaveSettings(); }
        }

        private string _testKeyStatus;
        public string TestKeyStatus
        {
            get => _testKeyStatus;
            set => SetProperty(ref _testKeyStatus, value);
        }

        private bool _isTestingKey;
        public bool IsTestingKey
        {
            get => _isTestingKey;
            set => SetProperty(ref _isTestingKey, value);
        }

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set => SetProperty(ref _isRunning, value);
        }

        private int _progress;
        public int Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        // Populated after a successful run - surfaced on the ribbon's "Last: N trees / X ha"
        // status label. -1 means "no completed run yet" (distinct from "0 trees found").
        private int _lastTreeCount = -1;
        public int LastTreeCount
        {
            get => _lastTreeCount;
            set => SetProperty(ref _lastTreeCount, value);
        }

        private double _lastAreaHa;
        public double LastAreaHa
        {
            get => _lastAreaHa;
            set => SetProperty(ref _lastAreaHa, value);
        }

        // Added after visual validation against a real orthophoto (2026-07-31) showed
        // false-positive points on bare soil/roads (small residual weeds/soil-color
        // artifacts pass the ExG vegetation threshold on their own). Reuses
        // land_clearing.py's mask scan to drop them - defaults on since it's a real
        // accuracy fix, but it's a second full raster scan (roughly doubles run time), so
        // it's left overridable rather than hardcoded.
        private bool _excludeClearedLand = true;
        public bool ExcludeClearedLand
        {
            get => _excludeClearedLand;
            set => SetProperty(ref _excludeClearedLand, value);
        }

        public ICommand RunDetectionCommand => new RelayCommand(async () => await RunDetectionAsync(), () => !IsRunning && SelectedRasterLayer != null);
        public ICommand CancelCommand => new RelayCommand(() => _runCts?.Cancel(), () => IsRunning);
        public ICommand TestKeyCommand => new RelayCommand(async () => await TestKeyAsync(), () => !IsTestingKey && !string.IsNullOrWhiteSpace(ApiKey));

        private async Task RunDetectionAsync()
        {
            IsRunning = true;
            Progress = 0;
            StatusText = Tr($"Running detection ({SelectedProfile})...", $"Menjalankan deteksi ({SelectedProfile})...");
            _runCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    StatusText = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }

                // Capture the target map now, not at completion time - the scan runs in the
                // background (async subprocess, no UI-thread blocking) so the user can switch
                // to a different map while it's running. MapView.Active at completion would
                // then point at whatever map they're looking at, not the one that was scanned.
                var map = MapView.Active.Map;

                var rasterPath = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList()
                        .OfType<RasterLayer>()
                        .FirstOrDefault(l => l.Name == SelectedRasterLayer)
                        ?.GetPath()?.LocalPath);

                if (rasterPath == null)
                {
                    StatusText = Tr("Raster layer not found - click Refresh and pick a layer again.",
                        "Layer raster tidak ditemukan - klik Refresh dan pilih layer lagi.");
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    StatusText = Tr("No open ArcGIS Pro project. Create or open one first.",
                        "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var fcName = $"TreeCounter_{(SelectedProfile == "Oil Palm Plantation" ? "palm" : "forest")}_{DateTime.Now:yyyyMMdd_HHmmss}";
                var outputFc = Path.Combine(project.DefaultGeodatabasePath, fcName);

                var aiEnabled = UseAiValidation && !string.IsNullOrWhiteSpace(ApiKey);
                var request = new DetectionRequest(
                    rasterPath, SelectedProfile, outputFc, Sigma, ExgThreshold, MinSmooth,
                    ExcludeClearedLand: ExcludeClearedLand,
                    Provider: aiEnabled ? SelectedProvider.ToLowerInvariant() : null,
                    ApiKey: aiEnabled ? ApiKey : null,
                    Model: aiEnabled ? SelectedModel : null);

                var dispatcher = System.Windows.Application.Current.Dispatcher;
                var result = await PythonBackendService.RunDetectionAsync(
                    request,
                    pct => dispatcher.BeginInvoke(() => Progress = pct),
                    stage => dispatcher.BeginInvoke(() => StatusText = stage),
                    _runCts.Token);

                if (result.Cancelled)
                {
                    StatusText = Tr("Cancelled.", "Dibatalkan.");
                }
                else if (result.Success)
                {
                    await AddResultLayerAsync(map, result.OutputFc, fcName);
                    LastTreeCount = result.TreeCount;
                    LastAreaHa = result.AreaHa;
                    // Explicit confirmation that AI Vision Validation actually ran, shown
                    // even when it rejected nothing - a real report (2026-08-16) found that
                    // with an API key entered but no visible mention of it in the final
                    // status, there was no way to tell "it ran and found nothing wrong" apart
                    // from "it silently never ran" (e.g. a bad key - validate_trees fails
                    // open on request errors, so a broken key still returns a normal-looking
                    // result otherwise indistinguishable from success).
                    var aiNote = aiEnabled
                        ? Tr($" (Validated with {SelectedProvider} - {result.RejectedByAiCount} rejected.)",
                             $" (Divalidasi dengan {SelectedProvider} - {result.RejectedByAiCount} ditolak.)") : "";
                    var clearedNote = result.FilteredClearedCount > 0
                        ? Tr($" ({result.FilteredClearedCount} false positive(s) on cleared ground filtered out.)",
                             $" ({result.FilteredClearedCount} false positive di tanah terbuka telah disaring.)") : "";
                    StatusText = Tr($"Done: {result.TreeCount} trees detected across {result.AreaHa:F1} ha scanned.",
                        $"Selesai: {result.TreeCount} pohon terdeteksi di {result.AreaHa:F1} ha yang dipindai.") +
                        clearedNote + aiNote;
                }
                else
                {
                    StatusText = Tr($"Failed: {result.ErrorMessage}", $"Gagal: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                // RelayCommand's execute delegate is fire-and-forget (async void semantics) -
                // without this catch, any exception here (e.g. Project.Current access,
                // QueuedTask failures) disappears with zero visible effect, which is exactly
                // the "I click it and nothing happens" symptom.
                StatusText = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                IsRunning = false;
            }
        }

        private async Task TestKeyAsync()
        {
            IsTestingKey = true;
            TestKeyStatus = Tr("Testing...", "Menguji...");
            try
            {
                var (ok, message) = await PythonBackendService.TestApiKeyAsync(
                    SelectedProvider.ToLowerInvariant(), ApiKey, SelectedModel);
                TestKeyStatus = Tr(ok ? "OK: " : "Failed: ", ok ? "OK: " : "Gagal: ") + message;
            }
            catch (Exception ex)
            {
                TestKeyStatus = Tr("Failed: ", "Gagal: ") + ex.Message;
            }
            finally
            {
                IsTestingKey = false;
            }
        }

        private async Task AddResultLayerAsync(Map map, string outputFc, string layerName)
        {
            await QueuedTask.Run(() =>
            {
                if (map == null) return;

                if (LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: layerName) is not FeatureLayer layer)
                    return;

                var color = SelectedProfile == "Oil Palm Plantation"
                    ? ColorFactory.Instance.CreateRGBColor(230, 76, 60)
                    : ColorFactory.Instance.CreateRGBColor(39, 174, 96);
                var symbol = SymbolFactory.Instance.ConstructPointSymbol(color, 6, SimpleMarkerStyle.Circle);
                layer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
            });
        }
    }
}
