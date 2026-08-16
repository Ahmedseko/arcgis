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
        // Scoped down from a full PDF/Layout report (map + legend + table) to a species x
        // volume summary spreadsheet - a Layout-based export would mean building CIM Layout
        // elements/map frames, an API surface nothing else in this add-in touches yet, which
        // is a lot of untested new ground for a one-shot feature. This still auto-builds the
        // table that's normally assembled by hand, using the same Statistics + TableToExcel
        // GP tools pattern already proven elsewhere in this file.
        private string _selectedReportLayer;
        public string SelectedReportLayer
        {
            get => _selectedReportLayer;
            set => SetProperty(ref _selectedReportLayer, value);
        }

        private bool _isGeneratingReport;
        public bool IsGeneratingReport
        {
            get => _isGeneratingReport;
            set => SetProperty(ref _isGeneratingReport, value);
        }

        private string _reportStatus = "";
        public string ReportStatus
        {
            get => _reportStatus;
            set => SetProperty(ref _reportStatus, value);
        }

        private CancellationTokenSource _reportCts;

        public ICommand GenerateReportCommand => new RelayCommand(async () => await GenerateReportAsync(), () => !IsGeneratingReport && SelectedReportLayer != null);
        public ICommand CancelReportCommand => new RelayCommand(() => _reportCts?.Cancel(), () => IsGeneratingReport);

        private async Task GenerateReportAsync()
        {
            IsGeneratingReport = true;
            ReportStatus = Tr("Generating report...", "Membuat laporan...");
            _reportCts = new CancellationTokenSource();
            try
            {
                if (MapView.Active == null)
                {
                    ReportStatus = Tr("No active map view. Open a map first.", "Tidak ada map view aktif. Buka map dulu.");
                    return;
                }
                var project = Project.Current;
                if (project == null)
                {
                    ReportStatus = Tr("No open ArcGIS Pro project. Create or open one first.", "Tidak ada project ArcGIS Pro yang terbuka. Buat atau buka satu dulu.");
                    return;
                }

                var map = MapView.Active.Map;
                var layer = await QueuedTask.Run(() =>
                    map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name == SelectedReportLayer));
                if (layer == null)
                {
                    ReportStatus = Tr("Layer not found - click Refresh and pick again.", "Layer tidak ditemukan - klik Refresh dan pilih lagi.");
                    return;
                }

                var (sourcePath, volumeField, speciesField) = await QueuedTask.Run(() =>
                {
                    using var featureClass = layer.GetFeatureClass();
                    var fields = featureClass.GetDefinition().GetFields();
                    var vol = fields.FirstOrDefault(f => f.Name.StartsWith("Volume", StringComparison.OrdinalIgnoreCase))?.Name;
                    var species = fields.FirstOrDefault(f =>
                        f.Name.IndexOf("species", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        f.Name.IndexOf("jenis", StringComparison.OrdinalIgnoreCase) >= 0)?.Name;
                    return (layer.GetPath()?.LocalPath, vol, species);
                });

                if (sourcePath == null)
                {
                    ReportStatus = Tr("Failed to read the layer's data path.", "Gagal membaca path data layer.");
                    return;
                }
                if (volumeField == null || speciesField == null)
                {
                    ReportStatus = Tr("Layer needs both a Volume field and a Species field to build a summary - import cruising data first.",
                        "Layer butuh field Volume dan field Species untuk membuat ringkasan - import data cruising dulu.");
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    FileName = $"{SelectedReportLayer}_Summary.xlsx",
                    Filter = "Excel files (*.xlsx)|*.xlsx",
                    DefaultExt = ".xlsx"
                };
                if (dialog.ShowDialog() != true)
                {
                    ReportStatus = Tr("Cancelled.", "Dibatalkan.");
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var statsTable = Path.Combine(project.DefaultGeodatabasePath, $"CruisingSummary_tmp_{stamp}");

                var statsResult = await Geoprocessing.ExecuteToolAsync("analysis.Statistics",
                    Geoprocessing.MakeValueArray(sourcePath, statsTable, $"{volumeField} SUM;{volumeField} COUNT", speciesField),
                    null, cancelToken: _reportCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);
                if (statsResult.IsFailed)
                {
                    ReportStatus = Tr($"Failed to summarize: {statsResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal membuat ringkasan: {statsResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                var exportResult = await Geoprocessing.ExecuteToolAsync("conversion.TableToExcel",
                    Geoprocessing.MakeValueArray(statsTable, dialog.FileName),
                    null, cancelToken: _reportCts.Token, flags: GPExecuteToolFlags.RefreshProjectItems);

                await Geoprocessing.ExecuteToolAsync("management.Delete",
                    Geoprocessing.MakeValueArray(statsTable), null, cancelToken: null, flags: GPExecuteToolFlags.RefreshProjectItems);

                if (exportResult.IsFailed)
                {
                    ReportStatus = Tr($"Failed to export to Excel: {exportResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(no details)"}",
                        $"Gagal ekspor ke Excel: {exportResult.ErrorMessages?.FirstOrDefault()?.Text ?? "(tidak ada detail)"}");
                    return;
                }

                ReportStatus = Tr($"Done: species/volume summary saved to {dialog.FileName}", $"Selesai: ringkasan spesies/volume disimpan ke {dialog.FileName}");
            }
            catch (OperationCanceledException)
            {
                ReportStatus = Tr("Cancelled.", "Dibatalkan.");
            }
            catch (Exception ex)
            {
                ReportStatus = Tr($"Unexpected error: {ex.Message}", $"Error tak terduga: {ex.Message}");
            }
            finally
            {
                _reportCts?.Dispose();
                _reportCts = null;
                IsGeneratingReport = false;
            }
        }
    }
}
