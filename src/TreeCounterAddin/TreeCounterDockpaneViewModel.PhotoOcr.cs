using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    internal partial class TreeCounterDockpaneViewModel
    {
        public ObservableCollection<string> OcrFormatOptions { get; } = new() { "UTM Grid", "Latitude/Longitude" };

        private string _ocrFormat = "UTM Grid";
        public string OcrFormat
        {
            get => _ocrFormat;
            set
            {
                if (!SetProperty(ref _ocrFormat, value)) return;
                IsUtmFormatSelected = value == "UTM Grid";
            }
        }

        // Backed separately (rather than a bare computed property off OcrFormat) so the
        // XAML Visibility binding gets its own PropertyChanged notification - same reasoning
        // as IsOtherZoneSelected in the cruising-import WKID picker.
        private bool _isUtmFormatSelected = true;
        public bool IsUtmFormatSelected
        {
            get => _isUtmFormatSelected;
            set => SetProperty(ref _isUtmFormatSelected, value);
        }

        // Only used as a fallback when a UTM-format photo's zone/band letter itself isn't
        // readable in the watermark (see OcrCoordinateReader.TryParseBareEastingNorthing) -
        // works for any UTM zone worldwide, not just Indonesia's.
        private int _ocrDefaultZone = 50;
        public int OcrDefaultZone
        {
            get => _ocrDefaultZone;
            set => SetProperty(ref _ocrDefaultZone, value);
        }

        public ObservableCollection<string> OcrHemisphereOptions { get; } = new() { "Southern (S)", "Northern (N)" };

        private string _ocrDefaultHemisphere = "Southern (S)";
        public string OcrDefaultHemisphere
        {
            get => _ocrDefaultHemisphere;
            set => SetProperty(ref _ocrDefaultHemisphere, value);
        }

        private bool _isScanningPhotos;
        public bool IsScanningPhotos
        {
            get => _isScanningPhotos;
            set => SetProperty(ref _isScanningPhotos, value);
        }

        private string _photoOcrStatus = "";
        public string PhotoOcrStatus
        {
            get => _photoOcrStatus;
            set => SetProperty(ref _photoOcrStatus, value);
        }

        public ICommand ScanPhotosForCoordinatesCommand => new RelayCommand(async () => await ScanPhotosForCoordinatesAsync(), () => !IsScanningPhotos);

        // For photos whose EXIF GPS is missing/blank (see ImportPhotosAsync for the
        // EXIF-based path) but that still show coordinates burned into the image itself -
        // reads them with local/offline OCR (Tesseract, bundled tessdata), then always
        // requires manual review before anything is written to the map: a misread OCR
        // digit produces a wrong-but-plausible-looking location with no error, unlike a
        // missing EXIF value which fails safely and visibly.
        private async Task ScanPhotosForCoordinatesAsync()
        {
            IsScanningPhotos = true;
            PhotoOcrStatus = "";
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select photos to scan for coordinates",
                    Filter = "Photos (*.jpg;*.jpeg)|*.jpg;*.jpeg",
                    Multiselect = true
                };
                if (dialog.ShowDialog() != true)
                {
                    PhotoOcrStatus = "Cancelled.";
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    PhotoOcrStatus = "No open ArcGIS Pro project. Create or open one first.";
                    return;
                }
                if (MapView.Active == null)
                {
                    PhotoOcrStatus = "No active map view. Open a map first.";
                    return;
                }
                var map = MapView.Active.Map;

                // AppDomain.CurrentDomain.BaseDirectory points at ArcGISPro.exe's own folder,
                // not this add-in's AssemblyCache folder - see PythonBackendService.cs's
                // BackendDir comment for why Assembly.Location has to be used instead.
                var tessdataPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    "tessdata");
                if (!File.Exists(Path.Combine(tessdataPath, "eng.traineddata")))
                {
                    PhotoOcrStatus = "OCR language data missing from add-in install - reinstall the add-in.";
                    return;
                }

                PhotoOcrStatus = $"Scanning {dialog.FileNames.Length} photo(s) with OCR...";

                var expectUtm = IsUtmFormatSelected;
                var defaultZone = OcrDefaultZone;
                var defaultIsSouth = OcrDefaultHemisphere == "Southern (S)";

                // CPU-bound native OCR calls + file I/O only - no ArcGIS Core objects
                // touched, so a plain background Task.Run is enough (no QueuedTask needed).
                var rows = await Task.Run(() =>
                {
                    var list = new List<OcrRow>();
                    foreach (var path in dialog.FileNames)
                    {
                        try
                        {
                            var result = OcrCoordinateReader.ReadCoordinates(tessdataPath, path, expectUtm, defaultZone, defaultIsSouth);
                            list.Add(new OcrRow
                            {
                                FileName = Path.GetFileName(path),
                                PhotoPath = path,
                                X = result.X,
                                Y = result.Y,
                                Wkid = result.Wkid,
                                RawText = result.RawText?.Replace("\r", " ").Replace("\n", " ").Trim(),
                                Include = result.X.HasValue && result.Y.HasValue && result.Wkid.HasValue
                            });
                        }
                        catch (Exception ex)
                        {
                            list.Add(new OcrRow
                            {
                                FileName = Path.GetFileName(path),
                                PhotoPath = path,
                                RawText = $"OCR failed: {ex.Message}",
                                Include = false
                            });
                        }
                    }
                    return list;
                });

                var window = new OcrReviewWindow(rows);
                if (window.ShowDialog() != true || window.ConfirmedRows.Count == 0)
                {
                    PhotoOcrStatus = "Cancelled - no points created.";
                    return;
                }

                // Grouped by WKID rather than assuming one for the whole batch - usually
                // everything's the same zone, but this stays correct if a photo or two got a
                // different zone/format confirmed.
                var createdLayers = 0;
                var totalPoints = 0;
                var failures = new List<string>();
                foreach (var group in window.ConfirmedRows.GroupBy(r => r.Wkid.Value))
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + group.Key;
                    var csvPath = Path.Combine(Path.GetTempPath(), $"OcrPoints_{stamp}.csv");
                    using (var writer = new StreamWriter(csvPath, false))
                    {
                        // PhotoPath rides along as a plain attribute so AddAttachments below
                        // can look it up from the output feature class itself afterward -
                        // no separate match table needed.
                        writer.WriteLine("FileName,X,Y,PhotoPath");
                        foreach (var row in group)
                        {
                            var name = row.FileName.Contains(',') ? $"\"{row.FileName}\"" : row.FileName;
                            var path = row.PhotoPath.Contains(',') ? $"\"{row.PhotoPath}\"" : row.PhotoPath;
                            writer.WriteLine($"{name},{row.X.Value.ToString(CultureInfo.InvariantCulture)},{row.Y.Value.ToString(CultureInfo.InvariantCulture)},{path}");
                        }
                    }

                    var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"OcrPhotoPoints_{stamp}");
                    var result = await Geoprocessing.ExecuteToolAsync("management.XYTableToPoint",
                        Geoprocessing.MakeValueArray(csvPath, outputFc, "X", "Y", "", group.Key),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                    File.Delete(csvPath);

                    if (result.IsFailed)
                    {
                        failures.Add($"WKID {group.Key}: {result.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}");
                        continue;
                    }

                    // Attaches each point's own source photo (matched to itself via
                    // OBJECTID/PhotoPath, both already on this same table) so the default
                    // ArcGIS Pro pop-up shows/enlarges the photo when a point is clicked -
                    // same experience as Geotagged Field Photos, for photos that got here via
                    // OCR instead of EXIF. Soft-fail: the points are already usable without
                    // photo attachments if this doesn't work out.
                    await Geoprocessing.ExecuteToolAsync("management.EnableAttachments",
                        Geoprocessing.MakeValueArray(outputFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);
                    await Geoprocessing.ExecuteToolAsync("management.AddAttachments",
                        Geoprocessing.MakeValueArray(outputFc, "OBJECTID", outputFc, "OBJECTID", "PhotoPath"),
                        null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                    await QueuedTask.Run(() =>
                    {
                        if (LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: Path.GetFileName(outputFc)) is FeatureLayer newLayer)
                            SetPopupTitle(newLayer, "{FileName}");
                    });
                    createdLayers++;
                    totalPoints += group.Count();
                }

                var summary = createdLayers == 0
                    ? "Failed to create any points."
                    : $"Done: {totalPoints} point(s) created from confirmed OCR coordinates across {createdLayers} layer(s).";
                PhotoOcrStatus = failures.Count == 0 ? summary : $"{summary} Failures: {string.Join("; ", failures)}";
            }
            catch (Exception ex)
            {
                PhotoOcrStatus = $"Unexpected error: {ex.Message}";
            }
            finally
            {
                IsScanningPhotos = false;
            }
        }
    }
}
