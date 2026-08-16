using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    internal partial class TreeCounterDockpaneViewModel
    {
        private bool _isImportingExcel;
        public bool IsImportingExcel
        {
            get => _isImportingExcel;
            set => SetProperty(ref _isImportingExcel, value);
        }

        private string _importExcelStatus = "";
        public string ImportExcelStatus
        {
            get => _importExcelStatus;
            set => SetProperty(ref _importExcelStatus, value);
        }

        private string _templateStatus = "";
        public string TemplateStatus
        {
            get => _templateStatus;
            set => SetProperty(ref _templateStatus, value);
        }

        // ArcGIS Pro's own "browse coordinate systems" picker isn't exposed as a public
        // control to add-ins (checked ArcGIS.Desktop.Mapping/Framework/Core - no such API),
        // and asking someone to type a raw WKID number isn't realistic for most users - so
        // this offers Indonesia's UTM zones by name instead, with "Other" as an escape hatch
        // for the rare case of a different/non-UTM coordinate system.
        private const string OtherZoneLabel = "Other (enter WKID manually)";

        private static readonly (string Label, int Wkid)[] IndonesianUtmZones =
        {
            ("WGS 1984 UTM Zone 46N", 32646), ("WGS 1984 UTM Zone 46S", 32746),
            ("WGS 1984 UTM Zone 47N", 32647), ("WGS 1984 UTM Zone 47S", 32747),
            ("WGS 1984 UTM Zone 48N", 32648), ("WGS 1984 UTM Zone 48S", 32748),
            ("WGS 1984 UTM Zone 49N", 32649), ("WGS 1984 UTM Zone 49S", 32749),
            ("WGS 1984 UTM Zone 50N", 32650), ("WGS 1984 UTM Zone 50S", 32750),
            ("WGS 1984 UTM Zone 51N", 32651), ("WGS 1984 UTM Zone 51S", 32751),
            ("WGS 1984 UTM Zone 52N", 32652), ("WGS 1984 UTM Zone 52S", 32752),
            ("WGS 1984 UTM Zone 53N", 32653), ("WGS 1984 UTM Zone 53S", 32753),
            ("WGS 1984 UTM Zone 54N", 32654), ("WGS 1984 UTM Zone 54S", 32754),
        };

        public ObservableCollection<string> UtmZoneOptions { get; } =
            new(IndonesianUtmZones.Select(z => z.Label).Append(OtherZoneLabel));

        private string _selectedUtmZoneLabel = "WGS 1984 UTM Zone 50S";
        public string SelectedUtmZoneLabel
        {
            get => _selectedUtmZoneLabel;
            set
            {
                if (!SetProperty(ref _selectedUtmZoneLabel, value)) return;
                IsOtherZoneSelected = value == OtherZoneLabel;
                var match = Array.Find(IndonesianUtmZones, z => z.Label == value);
                if (match.Label != null) CruisingWkid = match.Wkid;
            }
        }

        private bool _isOtherZoneSelected;
        public bool IsOtherZoneSelected
        {
            get => _isOtherZoneSelected;
            set => SetProperty(ref _isOtherZoneSelected, value);
        }

        // Default 32750 = WGS 1984 UTM Zone 50S, matching this org's field GPS units and
        // existing TC polygons. Set directly by SelectedUtmZoneLabel for the common case;
        // only edited by hand when "Other" is picked.
        private int _cruisingWkid = 32750;
        public int CruisingWkid
        {
            get => _cruisingWkid;
            set { if (SetProperty(ref _cruisingWkid, value)) SaveSettings(); }
        }

        private CancellationTokenSource _importExcelCts;

        public ICommand ImportExcelCommand => new RelayCommand(async () => await ImportExcelAsync(), () => !IsImportingExcel);
        public ICommand CancelImportExcelCommand => new RelayCommand(() => _importExcelCts?.Cancel(), () => IsImportingExcel);
        public ICommand DownloadTemplateCommand => new RelayCommand(() => DownloadTemplate(), () => true);

        // Reuses Esri's own ExcelToTable + XYTableToPoint GP tools rather than adding an
        // Excel-parsing library - reads the field crew's "TREE DATA"/"DATA POHON" sheet
        // (species, diameter, height, volume, X/Y from GPS, etc.) straight into a point layer.
        private async Task ImportExcelAsync()
        {
            IsImportingExcel = true;
            ImportExcelStatus = "";
            _importExcelCts = new CancellationTokenSource();
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Excel files (*.xlsx;*.xls)|*.xlsx;*.xls",
                    Title = Tr("Select Timber Cruising Excel File", "Pilih File Excel Timber Cruising")
                };
                if (dialog.ShowDialog() != true)
                {
                    ImportExcelStatus = Tr("Cancelled.", "Dibatalkan.");
                    return;
                }

                var project = Project.Current;
                if (project == null)
                {
                    ImportExcelStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }
                if (MapView.Active == null)
                {
                    ImportExcelStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var map = MapView.Active.Map;

                ImportExcelStatus = Tr("Reading Excel sheet...", "Membaca sheet Excel...");

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var tableFc = Path.Combine(project.DefaultGeodatabasePath, $"CruisingImport_tmp_{stamp}");
                var pointFc = Path.Combine(project.DefaultGeodatabasePath, $"CruisingTrees_{stamp}");

                // "TREE DATA" is this add-in's own English template sheet name; "DATA POHON"
                // is kept as a fallback for older cruising exports already using that sheet
                // name from before the template existed.
                IGPResult tableResult = null;
                foreach (var sheetName in new[] { "TREE DATA", "DATA POHON" })
                {
                    tableResult = await Geoprocessing.ExecuteToolAsync("conversion.ExcelToTable",
                        Geoprocessing.MakeValueArray(dialog.FileName, tableFc, sheetName),
                        null, cancelToken: _importExcelCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                    if (!tableResult.IsFailed) break;
                }
                if (tableResult.IsFailed)
                {
                    ImportExcelStatus = Tr(
                        $"Failed to read Excel (expected a \"TREE DATA\" sheet): {tableResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal membaca Excel (diharapkan ada sheet \"TREE DATA\"): {tableResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                ImportExcelStatus = Tr("Converting X/Y to points...", "Mengonversi X/Y jadi titik...");

                var pointResult = await Geoprocessing.ExecuteToolAsync("management.XYTableToPoint",
                    Geoprocessing.MakeValueArray(tableFc, pointFc, "X", "Y", "", CruisingWkid),
                    null, cancelToken: _importExcelCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (pointResult.IsFailed)
                {
                    ImportExcelStatus = Tr($"Failed to convert to points: {pointResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal mengonversi jadi titik: {pointResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                await Geoprocessing.ExecuteToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(tableFc), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                var (treeCount, totalVolume) = await QueuedTask.Run(() =>
                {
                    var newLayer = LayerFactory.Instance.CreateLayer(new Uri(pointFc), map, layerName: Path.GetFileName(pointFc)) as FeatureLayer;
                    using var featureClass = newLayer?.GetFeatureClass();
                    if (featureClass == null) return (0, 0.0);

                    // Field-name matched loosely ("StartsWith") rather than exact "Volume" -
                    // ExcelToTable sanitizes headers like "Volume (m3)" into names such as
                    // "Volume_m3_", and this has to work for both that and a bare "Volume".
                    var volumeField = featureClass.GetDefinition().GetFields()
                        .FirstOrDefault(f => f.Name.StartsWith("Volume", StringComparison.OrdinalIgnoreCase))?.Name;

                    // Same loose match as the Cruising Summary Report's species detection -
                    // "Species" from the English template, "Jenis Pohon"/"Jenis_Pohon" from
                    // older Indonesian exports.
                    var speciesField = featureClass.GetDefinition().GetFields()
                        .FirstOrDefault(f =>
                            f.Name.IndexOf("species", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            f.Name.IndexOf("jenis", StringComparison.OrdinalIgnoreCase) >= 0)?.Name;
                    if (speciesField != null && newLayer != null)
                        SetPopupTitle(newLayer, $"{{{speciesField}}}");

                    using var cursor = featureClass.Search(null, false);
                    int count = 0;
                    double totalVol = 0;
                    while (cursor.MoveNext())
                    {
                        using var feature = (Feature)cursor.Current;
                        count++;
                        if (volumeField != null && feature[volumeField] is double v)
                            totalVol += v;
                    }
                    return (count, totalVol);
                });

                ImportExcelStatus = Tr($"Done: imported {treeCount} trees, {totalVolume:F2} m3 total volume.",
                    $"Selesai: {treeCount} pohon diimpor, total volume {totalVolume:F2} m3.");
            }
            catch (OperationCanceledException)
            {
                ImportExcelStatus = Tr("Cancelled.", "Dibatalkan.");
            }
            catch (Exception ex)
            {
                ImportExcelStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _importExcelCts?.Dispose();
                _importExcelCts = null;
                IsImportingExcel = false;
            }
        }

        // The template ships as a static file (Templates\TreeCruisingTemplate.xlsx) rather
        // than being generated on demand - same reasoning as backend/ in deploy.ps1. Resolved
        // via Assembly.Location (not AppDomain.CurrentDomain.BaseDirectory, which points at
        // ArcGISPro.exe's own folder, not this add-in's AssemblyCache folder - see
        // PythonBackendService.cs's BackendDir comment) since after install this runs from
        // %LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{guid}\, not the repo checkout.
        private void DownloadTemplate()
        {
            try
            {
                var bundledPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    "Templates", "TreeCruisingTemplate.xlsx");
                if (!File.Exists(bundledPath))
                {
                    TemplateStatus = Tr("Template file missing from add-in install - reinstall the add-in.",
                        "File template hilang dari instalasi add-in - install ulang add-in-nya.");
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    FileName = "TreeCruisingTemplate.xlsx",
                    Filter = "Excel files (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx"
                };
                if (dialog.ShowDialog() != true)
                {
                    TemplateStatus = Tr("Cancelled.", "Dibatalkan.");
                    return;
                }

                File.Copy(bundledPath, dialog.FileName, overwrite: true);
                TemplateStatus = Tr($"Done: saved to {dialog.FileName}", $"Selesai: disimpan ke {dialog.FileName}");
            }
            catch (Exception ex)
            {
                TemplateStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
        }
    }
}
