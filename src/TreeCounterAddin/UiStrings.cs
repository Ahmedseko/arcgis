using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace TreeCounterAddin
{
    // General-purpose bilingual UI text (tab headers, section headers, field labels, hint
    // lines, buttons, checkboxes) - the app-wide follow-on to BilingualTooltipConverter.cs
    // (tooltips only) and the Help tab's own English/Indonesian text, after a real report
    // (2026-08-16) found a single bilingual dropdown (Color Reference Sampler's category
    // picker) looking inconsistent sitting inside an otherwise English-only panel, and
    // asked for the whole app to switch consistently instead. Reuses the same IsHelpEnglish
    // flag already driving those two - one language switch, not three independent ones.
    //
    // Left deliberately untranslated (stays literal English in the XAML, no key needed):
    // internal-only chrome that isn't really "content" - x:Key/StaticResource names, the
    // "Forestry Toolkit" brand name itself, the About tab's technology/developer/source
    // credits (a fixed factual block, not instructional UI), and format-string patterns
    // embedded in bindings ({0}%). Status/progress messages set from C# code-behind (e.g.
    // "Done: N trees detected...") are a separate, much larger followup - not covered here,
    // since each one lives inline in its own feature's .cs file rather than in this XAML.
    public class UiTextConverter : IValueConverter
    {
        public static readonly Dictionary<string, (string En, string Id)> Text = new()
        {
            // ---- Top bar ----
            ["Layers"] = ("Layers", "Layer"),
            ["Refresh"] = ("Refresh", "Refresh"),
            ["RefreshTooltip"] = ("Refresh the raster/polygon/point layer lists used throughout this panel",
                "Refresh daftar layer raster/poligon/titik yang dipakai di seluruh panel ini"),

            // ---- Tab headers ----
            ["Tab_Prepare"] = ("Prepare", "Prepare"),
            ["Tab_FieldData"] = ("Field Data", "Field Data"),
            ["Tab_Analyze"] = ("Analyze", "Analyze"),
            ["Tab_Favorites"] = ("Favorites", "Favorites"),
            ["Tab_History"] = ("History", "History"),
            ["Tab_Settings"] = ("Settings", "Settings"),
            ["Tab_Help"] = ("Help", "Bantuan"),
            ["Tab_About"] = ("About", "Tentang"),

            // ---- Flight Mission Planner ----
            ["FlightPlanner_Header"] = ("Flight Mission Planner", "Flight Mission Planner"),
            ["FlightPlanner_Hint"] = (
                "Generates a coverage flight plan (waypoints + a battery-based mission split) over a survey area polygon, for planning a drone orthophoto capture before flying.",
                "Membuat rencana terbang cakupan (waypoint + pembagian misi per baterai) di atas poligon area survei, untuk merencanakan pengambilan orthophoto drone sebelum terbang."),
            ["FlightPlanner_SurveyAreaLabel"] = ("Survey area polygon layer", "Layer poligon area survei"),
            ["FlightPlanner_AltitudeLabel"] = ("Altitude (m)", "Altitude (m)"),
            ["FlightPlanner_GsdLabel"] = ("GSD (cm/px)", "GSD (cm/px)"),
            ["FlightPlanner_ImageWidthLabel"] = ("Image width (px)", "Lebar gambar (px)"),
            ["FlightPlanner_ImageHeightLabel"] = ("Image height (px)", "Tinggi gambar (px)"),
            ["FlightPlanner_FrontOverlapLabel"] = ("Front overlap (%)", "Overlap depan (%)"),
            ["FlightPlanner_SideOverlapLabel"] = ("Side overlap (%)", "Overlap samping (%)"),
            ["FlightPlanner_DirectionLabel"] = ("Flight direction (deg, 0 = N-S lines)", "Arah terbang (derajat, 0 = garis utara-selatan)"),
            ["FlightPlanner_Vertical"] = ("Vertical", "Vertikal"),
            ["FlightPlanner_VerticalTooltip"] = ("Vertical - lines run north-south (0 deg).", "Vertikal - garis membujur utara-selatan (0 derajat)."),
            ["FlightPlanner_Horizontal"] = ("Horizontal", "Horizontal"),
            ["FlightPlanner_HorizontalTooltip"] = ("Horizontal - lines run east-west (90 deg).", "Horizontal - garis membujur timur-barat (90 derajat)."),
            ["FlightPlanner_Suggest"] = ("Suggest", "Suggest"),
            ["FlightPlanner_SuggestTooltip"] = (
                "Analyzes the survey polygon's shape and fills in the direction that fits it best (fewer, longer coverage lines instead of many short zigzag columns) - usually a better fit than a plain Vertical/Horizontal guess.",
                "Menganalisis bentuk poligon survei dan mengisi otomatis arah yang paling pas (garis cakupan lebih sedikit dan panjang, bukan banyak kolom zig-zag pendek) - biasanya lebih pas daripada tebakan Vertikal/Horizontal biasa."),
            ["FlightPlanner_SpeedLabel"] = ("Speed (m/s)", "Kecepatan (m/s)"),
            ["FlightPlanner_BatteryLabel"] = ("Max flight time / battery (min)", "Waktu terbang maks / baterai (menit)"),
            ["FlightPlanner_CrossHatch"] = ("Cross-hatch (fly a second pass at 90°, for better 3D reconstruction)",
                "Cross-hatch (terbang pass kedua di 90°, untuk rekonstruksi 3D lebih baik)"),
            ["FlightPlanner_CorridorMode"] = ("Corridor mode (follow a centerline instead of a straight-line grid)",
                "Corridor mode (mengikuti centerline, bukan grid garis lurus)"),
            ["FlightPlanner_CorridorModeTooltip"] = (
                "For a winding, narrow feature (river, road, pipeline) - flies passes that follow the centerline's own curvature instead of one fixed direction, which can't fit a shape that bends back on itself.",
                "Untuk objek berkelok dan sempit (sungai, jalan, pipa) - jalur terbang mengikuti lekukan centerline itu sendiri, bukan satu arah tetap yang tidak akan pas untuk bentuk yang membelok balik."),
            ["FlightPlanner_CenterlineLabel"] = ("Centerline layer", "Layer centerline"),
            ["FlightPlanner_GenerateMission"] = ("Generate Mission", "Generate Mission"),
            ["FlightPlanner_ExportFormatLabel"] = ("Export format", "Format export"),
            ["FlightPlanner_DroneModelLabel"] = ("Drone model (sets the DJI-required code in the KMZ file)",
                "Model drone (menentukan kode yang diwajibkan DJI di file KMZ)"),
            ["FlightPlanner_ExportMission"] = ("Export Mission...", "Export Mission..."),

            // ---- Fishnet Generator ----
            ["Fishnet_Header"] = ("Fishnet Generator", "Fishnet Generator"),
            ["Fishnet_PlanningLayerLabel"] = ("Planning polygon layer", "Layer poligon rencana"),
            ["Fishnet_CellSizeLabel"] = ("Cell size (map units)", "Ukuran sel (satuan map)"),
            ["Fishnet_Create"] = ("Create Fishnet", "Create Fishnet"),
            ["Cancel"] = ("Cancel", "Batal"),

            // ---- Export to GPX ----
            ["Gpx_Header"] = ("Export to GPS (GPX)", "Export to GPS (GPX)"),
            ["Gpx_Hint"] = (
                "Polygons/lines export as tracks (boundary walked in the field), points export as waypoints. Works directly with Garmin devices, BaseCamp, or Garmin Connect.",
                "Poligon/garis diekspor sebagai track (batas yang dijalani di lapangan), titik diekspor sebagai waypoint. Langsung kompatibel dengan perangkat Garmin, BaseCamp, atau Garmin Connect."),
            ["Layer"] = ("Layer", "Layer"),
            ["Gpx_Export"] = ("Export to GPX", "Export to GPX"),

            // ---- Import Timber Cruising Excel ----
            ["ExcelImport_Header"] = ("Import Timber Cruising Excel", "Import Timber Cruising Excel"),
            ["ExcelImport_Hint"] = (
                "Reads the \"TREE DATA\" sheet (species, diameter, height, volume, GPS X/Y) into a point layer.",
                "Membaca sheet \"TREE DATA\" (spesies, diameter, tinggi, volume, GPS X/Y) menjadi layer titik."),
            ["ExcelImport_DownloadTemplate"] = ("Download Template...", "Download Template..."),
            ["ExcelImport_CoordSystemLabel"] = ("Coordinate system", "Sistem koordinat"),
            ["ExcelImport_WkidLabel"] = ("WKID", "WKID"),
            ["ExcelImport_Import"] = ("Import Excel...", "Import Excel..."),

            // ---- Geotagged Field Photos ----
            ["PhotoImport_Header"] = ("Geotagged Field Photos", "Geotagged Field Photos"),
            ["PhotoImport_Hint"] = (
                "Pick one or more geotagged JPEGs (GPS EXIF data required) - each becomes a point with the photo attached; click its pop-up to view/enlarge. One-time import, not a watched folder - re-run this for photos added later.",
                "Pilih satu atau beberapa JPEG bergeotag (butuh data GPS EXIF) - tiap foto jadi satu titik dengan foto terlampir; klik pop-up-nya untuk melihat/memperbesar. Impor sekali jalan, bukan folder yang dipantau terus - jalankan lagi untuk foto yang ditambahkan belakangan."),
            ["PhotoImport_Import"] = ("Import Photos...", "Import Photos..."),

            // ---- Photo Coordinate OCR ----
            ["PhotoOcr_Header"] = ("Photo Coordinate OCR (no EXIF GPS)", "Photo Coordinate OCR (tanpa EXIF GPS)"),
            ["PhotoOcr_Hint"] = (
                "For photos with coordinates burned into the watermark but no EXIF GPS (see Geotagged Field Photos for photos that do have EXIF GPS). Runs fully offline - nothing leaves this computer. Every result must be reviewed and confirmed before any point is created.",
                "Untuk foto yang koordinatnya \"dicetak\" di watermark tapi tidak ada EXIF GPS (lihat Geotagged Field Photos untuk foto yang punya EXIF GPS). Berjalan sepenuhnya offline - tidak ada yang dikirim keluar dari komputer ini. Setiap hasil harus ditinjau dan dikonfirmasi dulu sebelum jadi titik."),
            ["PhotoOcr_FormatLabel"] = ("Watermark format", "Format watermark"),
            ["PhotoOcr_DefaultZoneLabel"] = ("Default zone/hemisphere (used only if a photo's zone letter can't be read)",
                "Zona/hemisphere default (dipakai hanya kalau huruf zona foto tidak terbaca)"),
            ["PhotoOcr_SelectPhotos"] = ("Select Photos to Scan...", "Select Photos to Scan..."),
            ["PhotoOcr_SelectPhotosTooltip"] = ("Opens a file picker to choose photos first, then scans whichever ones you select.",
                "Membuka file picker untuk pilih foto dulu, lalu memindai foto-foto yang dipilih."),

            // ---- Cruising Summary Report ----
            ["CruisingReport_Header"] = ("Cruising Summary Report", "Cruising Summary Report"),
            ["CruisingReport_Hint"] = (
                "Builds a species x volume summary spreadsheet from an imported cruising layer (needs Volume and Species fields) - a data table, not a printable map layout.",
                "Membuat spreadsheet ringkasan spesies x volume dari layer cruising yang sudah diimpor (butuh field Volume dan Species) - tabel data, bukan layout peta siap cetak."),
            ["CruisingReport_LayerLabel"] = ("Cruising point layer", "Layer titik cruising"),
            ["CruisingReport_Generate"] = ("Generate Report...", "Generate Report..."),

            // ---- Tree Detection ----
            ["TreeDetection_Header"] = ("Tree Detection", "Tree Detection"),
            ["RasterLayerLabel"] = ("Raster layer", "Layer raster"),
            ["TreeDetection_ProfileLabel"] = ("Detection profile", "Profil deteksi"),
            ["TreeDetection_ExcludeCleared"] = ("Exclude cleared/bare ground (recommended, roughly doubles run time)",
                "Kecualikan tanah gundul/terbuka (disarankan, waktu proses kira-kira dua kali lipat)"),
            ["TreeDetection_Detect"] = ("Detect Trees", "Detect Trees"),

            // ---- Land Clearing Detection ----
            ["LandClearing_Header"] = ("Land Clearing Detection", "Land Clearing Detection"),
            ["LandClearing_Hint"] = (
                "Opposite of tree detection - flags bare/cleared ground (low vegetation greenness) instead of tree crowns. Optionally exclude an already-known area (e.g. previously harvested) so results only show new clearings.",
                "Kebalikan dari deteksi pohon - menandai tanah gundul/terbuka (kehijauan vegetasi rendah), bukan tajuk pohon. Bisa opsional kecualikan area yang sudah diketahui (mis. bekas panen) supaya hasilnya hanya bukaan yang benar-benar baru."),
            ["LandClearing_ExcludeAreaLabel"] = ("Exclude area layer (optional)", "Layer area yang dikecualikan (opsional)"),
            ["LandClearing_MinAreaLabel"] = ("Minimum area (hectares)", "Luas minimum (hektar)"),
            ["LandClearing_Detect"] = ("Detect Clearing", "Detect Clearing"),

            // ---- Color Reference Sampler ----
            ["ColorSampler_Header"] = ("Color Reference Sampler", "Color Reference Sampler"),
            ["ColorSampler_Hint"] = (
                "Click points on the raster to record their exact RGB/ExG value - for calibrating the ExG thresholds above against real colors instead of guessing. Fill in Label/Class afterward directly in the new layer's own Attribute Table.",
                "Klik titik-titik pada raster untuk mencatat nilai RGB/ExG persisnya - untuk mengkalibrasi ambang batas ExG di atas terhadap warna nyata, bukan tebakan. Isi Label/Class belakangan langsung di Attribute Table layer barunya."),
            ["ColorSampler_CategoryLabel"] = ("Category (one per session - Stop Sampling to switch)",
                "Kategori (satu per sesi - Stop Sampling dulu untuk ganti)"),
            ["ColorSampler_Start"] = ("Start Sampling", "Start Sampling"),
            ["ColorSampler_Stop"] = ("Stop Sampling", "Stop Sampling"),

            // ---- Road/Trail Extraction ----
            ["RoadExtraction_Header"] = ("Road/Trail Extraction", "Road/Trail Extraction"),
            ["RoadExtraction_Hint"] = (
                "Extracts road/trail centerlines from bare-ground areas (same signal Land Clearing Detection uses) - skeletonized down to a single line down the middle of each road instead of a filled area.",
                "Menarik garis tengah jalan/jalur dari area tanah gundul (sinyal yang sama dipakai Land Clearing Detection) - ditipiskan jadi satu garis di tengah jalan, bukan area terisi penuh."),
            ["RoadExtraction_MinDangleLabel"] = ("Drop stubs shorter than (meters)", "Buang sisa fragmen lebih pendek dari (meter)"),
            ["RoadExtraction_Extract"] = ("Extract Roads", "Extract Roads"),

            // ---- Compare Changes ----
            ["CompareChanges_Header"] = ("Compare Changes", "Compare Changes"),
            ["CompareChanges_Hint"] = (
                "Compares two Tree Detection runs of the same site over time - trees in the old run with no match nearby are likely felled/lost, trees in the new run with no match are likely new/regrowth.",
                "Membandingkan dua hasil Tree Detection di lokasi yang sama pada waktu berbeda - pohon di hasil lama tanpa pasangan kemungkinan sudah ditebang/hilang, pohon di hasil baru tanpa pasangan kemungkinan baru tumbuh."),
            ["CompareChanges_OldLabel"] = ("Old detection (point layer)", "Deteksi lama (layer titik)"),
            ["CompareChanges_NewLabel"] = ("New detection (point layer)", "Deteksi baru (layer titik)"),
            ["CompareChanges_MatchDistLabel"] = ("Match distance (meters)", "Jarak pencocokan (meter)"),
            ["CompareChanges_Compare"] = ("Compare", "Compare"),

            // ---- Sliver Polygon Detection ----
            ["Sliver_Header"] = ("Sliver Polygon Detection", "Sliver Polygon Detection"),
            ["Sliver_Hint"] = (
                "Reads every polygon's area and shape first, then auto-flags whatever is far smaller or far thinner/more elongated than typical for this layer - no manual threshold needed. Works on fishnet/grid output or a raw boundary layer.",
                "Membaca luas dan bentuk tiap poligon dulu, lalu otomatis menandai yang jauh lebih kecil atau jauh lebih tipis/memanjang dari biasanya di layer ini - tidak perlu ambang batas manual. Bekerja pada hasil fishnet/grid atau layer batas mentah."),
            ["Sliver_LayerLabel"] = ("Polygon layer", "Layer poligon"),
            ["Sliver_Detect"] = ("Detect Slivers", "Detect Slivers"),

            // ---- Biomass & Carbon Estimation ----
            ["Biomass_Header"] = ("Biomass & Carbon Estimation", "Biomass & Carbon Estimation"),
            ["Biomass_Hint"] = (
                "Volume-based estimate (IPCC Tier 1 style) from a point layer's Volume field - approximate, tune the defaults for your species mix/region.",
                "Estimasi berbasis volume (gaya IPCC Tier 1) dari field Volume sebuah layer titik - perkiraan, sesuaikan default untuk campuran spesies/wilayah Anda."),
            ["Biomass_LayerLabel"] = ("Point layer (with Volume field)", "Layer titik (dengan field Volume)"),
            ["Biomass_WoodDensityLabel"] = ("Wood density (kg/m3)", "Berat jenis kayu (kg/m3)"),
            ["Biomass_ExpansionFactorLabel"] = ("Biomass expansion factor", "Faktor ekspansi biomassa"),
            ["Biomass_RootShootLabel"] = ("Root-to-shoot ratio", "Rasio akar-tajuk"),
            ["Biomass_CarbonFractionLabel"] = ("Carbon fraction", "Fraksi karbon"),
            ["Biomass_Estimate"] = ("Estimate", "Estimate"),

            // ---- Slope from DEM ----
            ["Slope_Header"] = ("Slope from DEM", "Slope from DEM"),
            ["Slope_Hint"] = (
                "Computes slope (% rise) from a DEM raster to help assess logging accessibility - requires the Spatial Analyst extension.",
                "Menghitung kemiringan (% rise) dari raster DEM untuk membantu menilai aksesibilitas penebangan - butuh ekstensi Spatial Analyst."),
            ["Slope_DemLabel"] = ("DEM raster layer", "Layer raster DEM"),
            ["Slope_Compute"] = ("Compute Slope", "Compute Slope"),

            // ---- Riparian Buffer Check ----
            ["Riparian_Header"] = ("Riparian Buffer Check", "Riparian Buffer Check"),
            ["Riparian_Hint"] = (
                "Buffers the river/stream layer by the given distance and flags whatever part of the planning polygon falls inside it. No fixed legal distance is assumed - enter the width your regulation requires.",
                "Membuat buffer dari layer sungai/aliran sejauh jarak yang ditentukan dan menandai bagian poligon rencana yang jatuh di dalamnya. Tidak ada jarak hukum baku - masukkan lebar sesuai regulasi yang berlaku."),
            ["Riparian_RiverLabel"] = ("River/stream layer", "Layer sungai/aliran"),
            ["Riparian_PlanLabel"] = ("Planning polygon layer", "Layer poligon rencana"),
            ["Riparian_DistanceLabel"] = ("Buffer distance (meters)", "Jarak buffer (meter)"),
            ["Riparian_Check"] = ("Check Buffer", "Check Buffer"),

            // ---- Favorites tab ----
            ["Favorites_SearchLabel"] = ("Search layer", "Cari layer"),
            ["Favorites_SearchTooltip"] = ("Filter the layer list below by name", "Filter daftar layer di bawah berdasarkan nama"),
            ["Favorites_Add"] = ("★ Add", "★ Tambah"),
            ["Favorites_Remove"] = ("Remove from Favorites", "Hapus dari Favorites"),
            ["Favorites_ToggleVisibility"] = ("Toggle this layer's visibility", "Nyala/matikan visibility layer ini"),

            // ---- History tab ----
            ["History_Heading"] = ("Recent activity, newest first", "Aktivitas terbaru, terbaru di atas"),
            ["Clear"] = ("Clear", "Clear"),

            // ---- Settings tab ----
            ["AdvancedParams_Header"] = ("Advanced Detection Parameters", "Advanced Detection Parameters"),
            ["AdvancedParams_SigmaLabel"] = ("Sigma (px)", "Sigma (px)"),
            ["AdvancedParams_ExgLabel"] = ("ExG Threshold", "ExG Threshold"),
            ["AdvancedParams_MinSmoothLabel"] = ("Min Smooth", "Min Smooth"),

            ["LandClearingParams_Header"] = ("Land Clearing Parameters", "Land Clearing Parameters"),
            ["LandClearingParams_Hint"] = (
                "Result too rough/fragmented, or missing narrower real clearings? Lower Opening and/or raise Closing - each site's imagery (camera, lighting, soil color) can need different smoothing than the defaults.",
                "Hasil terlalu kasar/terpecah, atau bukaan sempit asli terlewat? Turunkan Opening dan/atau naikkan Closing - citra tiap lokasi (kamera, pencahayaan, warna tanah) bisa butuh smoothing berbeda dari default."),
            ["LandClearingParams_ExgLabel"] = ("ExG Threshold", "ExG Threshold"),
            ["LandClearingParams_ExgTooltip"] = (
                "Below this, a pixel counts as bare/cleared ground instead of vegetation. Lower catches more area as cleared (risks false positives); higher catches less (risks missing real clearings).",
                "Di bawah nilai ini, piksel dihitung tanah gundul/terbuka, bukan vegetasi. Makin rendah, makin banyak area terhitung bukaan (risiko false positive); makin tinggi, makin sedikit (risiko bukaan asli terlewat)."),
            ["LandClearingParams_SmoothLabel"] = ("Smooth (px)", "Smooth (px)"),
            ["LandClearingParams_SmoothTooltip"] = ("Gaussian blur applied before thresholding - reduces speckle from individual noisy pixels.",
                "Gaussian blur diterapkan sebelum thresholding - mengurangi speckle dari piksel-piksel noise."),
            ["LandClearingParams_OpeningLabel"] = ("Opening (erosion)", "Opening (erosion)"),
            ["LandClearingParams_OpeningTooltip"] = (
                "Strips small false 'cleared' specks - but can also erase real clearings narrower than this many pixels. Lower if real narrow clearings are missing.",
                "Membuang bercak 'bukaan' palsu yang kecil - tapi juga bisa menghapus bukaan asli yang lebih sempit dari sekian piksel ini. Turunkan kalau bukaan sempit asli ikut terlewat."),
            ["LandClearingParams_ClosingLabel"] = ("Closing (dilation)", "Closing (dilation)"),
            ["LandClearingParams_ClosingTooltip"] = (
                "Fills small gaps and merges nearby fragments into one shape - raise for smoother, more human-digitization-like boundaries instead of many small blobs.",
                "Mengisi celah kecil dan menggabungkan fragmen berdekatan jadi satu bentuk - naikkan untuk batas lebih halus, seperti hasil digitasi manusia, bukan banyak blob kecil."),
            ["LandClearingParams_FillHoleLabel"] = ("Fill holes ≤ (m²)", "Isi lubang ≤ (m²)"),
            ["LandClearingParams_FillHoleTooltip"] = (
                "Fills small interior holes (vegetation patches left standing inside an otherwise-cleared area) so the result is one solid polygon like manual digitization - large real forest islands are left alone. 0 = don't fill. Matches the original QGIS plugin's own default.",
                "Mengisi lubang kecil di dalam poligon (bercak vegetasi yang tersisa di dalam area bukaan) supaya hasilnya jadi satu poligon solid seperti digitasi manual - pulau hutan besar asli dibiarkan. 0 = jangan isi. Sesuai default plugin QGIS aslinya."),
            ["LandClearingParams_FreshColor"] = ("Fresh color filter (also require bright + reddish soil - excludes roads/rivers)",
                "Filter warna segar (wajib juga tanah cerah + kemerahan - mengecualikan jalan/sungai)"),
            ["LandClearingParams_BrightMinLabel"] = ("Brightness minimum", "Kecerahan minimum"),
            ["LandClearingParams_BrightMinTooltip"] = ("How bright (0-255) raw RGB has to be to count as bare soil, not a darker road/river.",
                "Seberapa cerah (0-255) RGB mentah harus supaya dihitung tanah gundul, bukan jalan/sungai yang lebih gelap."),

            ["AiValidation_Header"] = ("AI Vision Validation", "AI Vision Validation"),
            ["AiValidation_Hint"] = ("Optional - confirms Tree Detection, Land Clearing, and Road Extraction results via Gemini, OpenAI, or Claude.",
                "Opsional - mengonfirmasi hasil Tree Detection, Land Clearing, dan Road Extraction lewat Gemini, OpenAI, atau Claude."),
            ["AiValidation_Enable"] = ("Enable AI Vision Validation", "Aktifkan AI Vision Validation"),
            ["AiValidation_EnableTooltip"] = ("Turn AI validation off without losing the key/provider below - e.g. to run a faster pass without it.",
                "Matikan AI validation tanpa kehilangan key/provider di bawah - misalnya untuk proses lebih cepat tanpa AI."),
            ["AiValidation_ProviderLabel"] = ("Provider", "Provider"),
            ["AiValidation_ModelLabel"] = ("Model", "Model"),
            ["AiValidation_TestKey"] = ("Test Key", "Test Key"),

            // ---- About tab ----
            ["About_TechHeader"] = ("Technology", "Teknologi"),
            ["About_DeveloperHeader"] = ("Developer", "Developer"),
            ["About_SourceHeader"] = ("Source", "Source"),
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isEnglish = value is bool b && b;
            if (parameter is string key && Text.TryGetValue(key, out var pair))
                return isEnglish ? pair.En : pair.Id;
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
