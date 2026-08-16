using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
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
        private bool _isImportingPhotos;
        public bool IsImportingPhotos
        {
            get => _isImportingPhotos;
            set => SetProperty(ref _isImportingPhotos, value);
        }

        private string _photoImportStatus = "";
        public string PhotoImportStatus
        {
            get => _photoImportStatus;
            set => SetProperty(ref _photoImportStatus, value);
        }

        private CancellationTokenSource _importPhotosCts;

        public ICommand ImportPhotosCommand => new RelayCommand(async () => await ImportPhotosAsync(), () => !IsImportingPhotos);
        public ICommand CancelImportPhotosCommand => new RelayCommand(() => _importPhotosCts?.Cancel(), () => IsImportingPhotos);

        // Reuses Esri's own GeotagPhotos GP tool - reads GPS EXIF data straight from field
        // photo JPEGs and creates a point per photo with the photo auto-attached (ArcGIS
        // Pro's built-in feature attachments), so clicking a point in the default pop-up
        // shows/enlarges the photo without any custom viewer code. GeotagPhotos itself only
        // accepts a whole folder, not a file list - so specific photos picked here are first
        // copied into a throwaway staging folder, which is deleted again afterward. This is
        // a one-time snapshot import, not a watched folder: photos added to the source
        // folder later need a fresh Import Photos... run, they won't appear automatically.
        private async Task ImportPhotosAsync()
        {
            IsImportingPhotos = true;
            PhotoImportStatus = "";
            string stagingDir = null;
            _importPhotosCts = new CancellationTokenSource();
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = Tr("Select geotagged field photos", "Pilih foto lapangan bergeotag"),
                    Filter = "Photos (*.jpg;*.jpeg)|*.jpg;*.jpeg|All files (*.*)|*.*",
                    Multiselect = true
                };
                if (dialog.ShowDialog() != true)
                {
                    PhotoImportStatus = Tr("Cancelled.", "Dibatalkan.");
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    PhotoImportStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }
                if (MapView.Active == null)
                {
                    PhotoImportStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var map = MapView.Active.Map;

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                stagingDir = Path.Combine(Path.GetTempPath(), $"ForestryToolkit_photos_{stamp}");
                Directory.CreateDirectory(stagingDir);
                foreach (var file in dialog.FileNames)
                    File.Copy(file, Path.Combine(stagingDir, Path.GetFileName(file)), overwrite: true);

                var outputFc = Path.Combine(project.DefaultGeodatabasePath, $"FieldPhotos_{stamp}");

                PhotoImportStatus = Tr("Reading photo GPS tags...", "Membaca tag GPS foto...");

                var result = await Geoprocessing.ExecuteToolAsync("management.GeotagPhotos",
                    Geoprocessing.MakeValueArray(stagingDir, outputFc),
                    null, cancelToken: _importPhotosCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (result.IsFailed)
                {
                    // ErrorMessages can come back empty even on a genuine failure (seen with
                    // GeotagPhotos when none of the input photos have any GPS EXIF at all,
                    // which is a normal/expected case - not every source has geotagged
                    // photos to begin with) - Messages (all severities) is checked as a
                    // fallback so the real reason still surfaces instead of "(no details)".
                    var detail = result.ErrorMessages?.FirstOrDefault()?.Text
                        ?? result.Messages?.FirstOrDefault()?.Text
                        ?? Tr("no GP message returned - most likely none of the selected photos have GPS EXIF data",
                              "tidak ada pesan GP - kemungkinan besar tidak ada foto yang dipilih punya data GPS EXIF");
                    PhotoImportStatus = Tr($"Failed to geotag photos: {detail}", $"Gagal geotag foto: {detail}");
                    return;
                }

                var count = await QueuedTask.Run(() =>
                {
                    var newLayer = LayerFactory.Instance.CreateLayer(new Uri(outputFc), map, layerName: Path.GetFileName(outputFc)) as FeatureLayer;
                    using var featureClass = newLayer?.GetFeatureClass();
                    if (featureClass == null) return 0L;

                    // GeotagPhotos' own output field name for the photo's file name isn't
                    // pinned down here to an exact literal - found dynamically so the popup
                    // title still works even if that name differs across ArcGIS versions.
                    var nameField = featureClass.GetDefinition().GetFields()
                        .FirstOrDefault(f => f.Name.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)?.Name;
                    if (nameField != null && newLayer != null)
                        SetPopupTitle(newLayer, $"{{{nameField}}}");

                    return featureClass.GetCount();
                });

                PhotoImportStatus = Tr($"Done: {count} geotagged photo(s) added with attachments - click a point on the map to view its photo.",
                    $"Selesai: {count} foto bergeotag ditambahkan dengan lampiran - klik titik di map untuk melihat fotonya.");
            }
            catch (OperationCanceledException)
            {
                PhotoImportStatus = Tr("Cancelled.", "Dibatalkan.");
            }
            catch (Exception ex)
            {
                PhotoImportStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                if (stagingDir != null && Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
                _importPhotosCts?.Dispose();
                _importPhotosCts = null;
                IsImportingPhotos = false;
            }
        }
    }
}
