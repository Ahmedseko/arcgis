using ArcGIS.Desktop.Framework;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Static reference text for the DockPane's Help and About tabs - no backend calls, no
    // arcpy, just content + a language toggle. Kept in its own partial file/class purely
    // because of its size (a full how-to-use guide), same reasoning every other feature
    // area already gets its own file.
    internal partial class TreeCounterDockpaneViewModel
    {
        private bool _isHelpEnglish = true;
        public bool IsHelpEnglish
        {
            get => _isHelpEnglish;
            set
            {
                SetProperty(ref _isHelpEnglish, value);
                IsHelpIndonesian = !value;
            }
        }

        // Set directly by IsHelpEnglish's setter for the common case (same pattern
        // SelectedUtmZoneLabel/IsOtherZoneSelected in ExcelImport.cs already uses) - always
        // the exact opposite, so the two TextBlocks in the Help tab can never both be
        // visible (or both hidden) at once.
        private bool _isHelpIndonesian;
        public bool IsHelpIndonesian
        {
            get => _isHelpIndonesian;
            private set => SetProperty(ref _isHelpIndonesian, value);
        }

        public ICommand ShowHelpEnglishCommand => new RelayCommand(() => IsHelpEnglish = true);
        public ICommand ShowHelpIndonesianCommand => new RelayCommand(() => IsHelpEnglish = false);

        public string AboutVersionText => $"Version {AppVersion}";

        public string HelpEnglishText { get; } = """
            FORESTRY TOOLKIT — HOW TO USE

            This panel is organized into four working tabs (Prepare, Field Data, Analyze,
            Settings) plus this Help tab and an About tab. Every tool follows the same
            pattern: pick the layer(s) it needs from a dropdown, set any parameters, click
            its action button, and watch the status line underneath for progress or errors.
            Most long-running tools show a progress bar and a Cancel button - it's always
            safe to cancel.

            The "Layers" / "Refresh" bar at the top rescans the active map for raster,
            polygon, and point layers. The panel now refreshes those lists automatically
            whenever you switch maps or add/remove a layer, so you shouldn't need to click
            Refresh often - it's still there for the rare case something doesn't update on
            its own (e.g. a layer's geometry type changed without being removed/re-added).

            Favorites (just below that bar, visible on every tab)
            Flags layers you use often, for a Contents pane that otherwise fills up with
            one-off results over time. Pick a layer from the dropdown and click "★ Add" -
            it appears in the list below with a checkbox (toggles that layer's visibility
            directly) and a "✕" button (removes it from Favorites). This never renames or
            otherwise modifies the actual layer - a name-prefix approach was considered and
            rejected because a layer's Name also drives its legend text in a printed
            layout, so favoriting something would have silently changed what prints on a
            map. Favorites are remembered per project (a small file on this computer, nothing
            written into the project itself).


            ═══ PREPARE TAB ═══

            Fishnet Generator
            Splits a planning/concession polygon into a grid of equal-sized cells, for
            laying out cruising plots. Pick your planning polygon layer, set the cell width
            and height (in your map's coordinate system units - meters, if it's a projected
            CRS like UTM), and click Create Fishnet. The output is a new polygon layer
            clipped to your boundary.

            Export to GPS (GPX)
            Converts a layer to a .gpx file for a handheld GPS unit (Garmin) or apps like
            BaseCamp/Garmin Connect. Polygon and line layers export as tracks (so you can
            walk or drive the boundary in the field); point layers export as waypoints.
            Pick the layer, click Export to GPX, and choose where to save the file.


            ═══ FIELD DATA TAB ═══

            Import Timber Cruising Excel
            Reads a specific spreadsheet template - click "Download Template..." first if
            you don't already have it - specifically its "TREE DATA" sheet, which expects
            columns for species, diameter, height, volume, and GPS X/Y. Pick the coordinate
            system your GPS coordinates were recorded in (choose your UTM zone from the
            list, or "Other" plus a custom WKID if it's not Indonesian UTM), then click
            Import Excel. The result is a point layer with one point per cruised tree.

            Geotagged Field Photos
            Imports photos that already have GPS location saved in their EXIF metadata
            (most phone cameras and dedicated GPS cameras do this automatically). Each
            photo becomes a map point; click it to view/enlarge the photo in a popup card.
            This is a one-time import, not a watched folder - if you add more photos later,
            run it again to bring in the new ones (it won't duplicate ones already imported
            in the same run, but re-running does re-import if you point it at the same
            photos again).

            Photo Coordinate OCR (no EXIF GPS)
            For photos where the coordinates are burned into the image itself as a
            watermark/overlay (common with GPS camera apps) instead of stored in EXIF -
            this reads that printed text instead. Pick the watermark's format from the
            dropdown, and if it's UTM, a default zone/hemisphere to fall back on when a
            specific photo's zone letter can't be read automatically. Click Scan Photos.
            This runs entirely offline - nothing about your photos is uploaded anywhere.
            Every detected coordinate is shown to you for review and must be confirmed
            before it becomes a map point, so a misread digit can't silently create a
            wrong-location point.

            Cruising Summary Report
            Builds a species-by-volume summary spreadsheet from a cruising point layer that
            already has Volume and Species fields (e.g. the output of the Excel import
            above). This produces a data table for reporting purposes, not a printable map
            layout.


            ═══ ANALYZE TAB ═══

            Tree Detection
            The core feature. Pick a raster layer (a drone orthophoto) and a detection
            profile:
              • Natural Forest - a color-and-shape algorithm (a vegetation-greenness index
                combined with a matched filter) tuned for irregular natural tree crowns.
              • Oil Palm Plantation - an AI model (YOLOv8) trained specifically on oil palm
                crowns, better suited to a plantation's regular planting grid.
            Detection runs as a background process - it's safe to keep working, even switch
            to a different map, while it runs. "Exclude cleared/bare ground" (on by
            default) drops false-positive points that land on bare soil, roads, or open
            ground, at the cost of roughly doubling the run time (it needs a second scan of
            the image) - turn it off once you've confirmed a given site doesn't need it.
            The result is a point layer, one point per detected tree/crown, colored green
            for the forest profile or red for oil palm.

            Land Clearing Detection
            The opposite of Tree Detection - flags bare/cleared ground instead of tree
            crowns, from the same kind of imagery. You can optionally pick an "exclude
            area" polygon layer (e.g. ground you already know was cleared before, like a
            previous harvest block) so the result only shows genuinely new clearings, and
            set a minimum area in hectares to ignore tiny noise patches. Ponds and rivers
            are automatically excluded from results. The output is a polygon layer of
            cleared/bare areas.

            Road/Trail Extraction
            Pulls road and trail centerlines out of the same bare-ground signal Land
            Clearing Detection uses, then thins them down to a single line running down the
            middle of the road instead of a filled area. "Drop stubs shorter than (meters)"
            cleans up short leftover fragments - 5m is a reasonable starting point; raise it
            if the result still looks noisy/fragmented. The output is a line layer of
            extracted centerlines. Known limitation: because it's built on the same
            bare-ground signal, the traced line can sometimes wander into bare ground next
            to the road that isn't actually the road itself (e.g. a quarry or stockpile
            area) - this is still being refined.

            Compare Changes
            Detects change between two separate Tree Detection runs of the same area taken
            at different times (e.g. this year vs. last year). Pick the old run's point
            layer, the new run's point layer, and a match distance in meters (how far apart
            two points can be and still count as "the same tree", allowing for small
            re-detection jitter between runs). The result is two new point layers: "Lost"
            (red - trees present in the old run with no match in the new one, likely
            felled) and "New" (green - trees in the new run with no match in the old one,
            likely new growth or previously missed).

            Sliver Polygon Detection
            Automatically finds unusually small or unusually thin/elongated polygons in a
            polygon layer - no manual size threshold to set, it calibrates itself against
            that layer's own typical polygon size and shape. Useful for spotting digitizing
            mistakes or fishnet cells cut down to slivers by an irregular boundary. Flagged
            polygons are selected directly on the map.

            Biomass & Carbon Estimation
            Estimates above-ground biomass and carbon stock from a point layer that has a
            Volume field (e.g. your cruising data), using an IPCC Tier-1-style calculation.
            Four constants can be tuned for your species mix/region: wood density, biomass
            expansion factor, root-to-shoot ratio, and carbon fraction. The defaults are
            generic global averages, not calibrated to any specific species - adjust them
            if you have better local figures.

            Slope from DEM
            Computes slope (percent rise) from a Digital Elevation Model raster, to help
            judge how accessible an area is for logging equipment and road-building.
            Requires the ArcGIS Spatial Analyst extension to be licensed and enabled.

            Riparian Buffer Check
            Buffers a river/stream layer by a distance you specify - there's no built-in
            legal default, enter whatever your regulation requires - and flags whichever
            part of your planning polygon falls inside that buffer, so you can see at a
            glance which harvest blocks encroach on a protected riparian zone.


            ═══ SETTINGS TAB ═══

            Advanced Detection Parameters
            Manual overrides for Tree Detection's algorithm: Sigma (expected crown radius
            in pixels), ExG Threshold (how green a pixel must read to count as
            vegetation), and Min Smooth (the minimum matched-filter response needed to
            count as a detection). Each detection profile already ships with tuned
            defaults - only change these if you understand the algorithm and a specific
            site genuinely needs different values.

            AI Vision Validation
            An optional extra check that sends each detected tree's image crop to an AI
            vision model (Google Gemini, OpenAI, or Anthropic Claude) to confirm it's
            really a tree before keeping it, reducing false positives further. Requires an
            internet connection and an API key from whichever provider you choose. Your key
            is stored encrypted on this computer only (never sent anywhere except that
            provider's own API). Use "Test Key" to confirm it works before enabling AI
            validation on a real detection run.


            ═══ GENERAL TIPS ═══

            • If a result layer looks empty or wrong, check that tool's status line first -
              most failures explain themselves there (e.g. "no active map view", "layer not
              found - click Refresh").
            • Detection and extraction results always load onto the active map automatically
              with sensible default colors/symbology - you can restyle them like any other
              layer afterward.
            • Nothing in this add-in uploads your imagery anywhere by default. The only
              feature that sends data off this computer is optional AI Vision Validation
              (Settings tab), and only the small cropped tree images you've explicitly
              opted into checking - never the full orthophoto.
            """;

        public string HelpIndonesianText { get; } = """
            FORESTRY TOOLKIT — CARA PAKAI

            Panel ini terbagi jadi empat tab kerja (Prepare, Field Data, Analyze, Settings)
            ditambah tab Help ini dan tab About. Semua alat di sini polanya sama: pilih
            layer yang dibutuhkan dari dropdown, atur parameternya kalau ada, klik tombol
            aksinya, lalu lihat baris status di bawahnya untuk progress atau pesan error.
            Kebanyakan proses yang lama menampilkan progress bar dan tombol Cancel - aman
            dibatalkan kapan saja.

            Baris "Layers" / "Refresh" di bagian atas memindai ulang map yang aktif untuk
            layer raster, poligon, dan titik. Daftar ini sekarang otomatis ter-refresh
            setiap kali Anda ganti map atau menambah/menghapus layer, jadi tidak perlu
            sering-sering klik Refresh manual - tombolnya tetap ada untuk kasus langka
            ketika sesuatu tidak ter-update sendiri (misalnya tipe geometri sebuah layer
            berubah tanpa dihapus/ditambah ulang).

            Favorites (tepat di bawah baris itu, kelihatan di semua tab)
            Menandai layer yang sering Anda pakai, supaya Contents pane yang lama-lama
            penuh hasil sekali-pakai tetap mudah ditelusuri. Pilih layer dari dropdown lalu
            klik "★ Add" - layer itu muncul di daftar bawah dengan checkbox (langsung
            toggle visibility layer itu) dan tombol "✕" (hapus dari Favorites). Ini tidak
            pernah rename atau mengubah layer aslinya sama sekali - pendekatan awal (kasih
            prefix nama bintang) sempat dipertimbangkan lalu dibatalkan karena Name sebuah
            layer juga menentukan teks legend-nya di layout cetak, jadi favoritkan sesuatu
            akan diam-diam mengubah apa yang tercetak di peta. Favorites diingat per
            project (file kecil di komputer ini, tidak ditulis ke dalam project itu
            sendiri).


            ═══ TAB PREPARE ═══

            Fishnet Generator
            Membagi poligon rencana/konsesi menjadi grid sel berukuran sama, untuk menata
            plot cruising. Pilih layer poligon rencana, atur lebar dan tinggi sel (dalam
            satuan sistem koordinat map Anda - meter, kalau CRS-nya proyeksi seperti UTM),
            lalu klik Create Fishnet. Hasilnya berupa layer poligon baru yang sudah
            dipotong sesuai batas area Anda.

            Export to GPS (GPX)
            Mengonversi sebuah layer menjadi file .gpx untuk perangkat GPS genggam (Garmin)
            atau aplikasi seperti BaseCamp/Garmin Connect. Layer poligon dan garis
            diekspor sebagai track (supaya bisa dijalani/dikendarai di lapangan mengikuti
            batasnya); layer titik diekspor sebagai waypoint. Pilih layer-nya, klik Export
            to GPX, lalu tentukan lokasi penyimpanan filenya.


            ═══ TAB FIELD DATA ═══

            Import Timber Cruising Excel
            Membaca template spreadsheet tertentu - klik "Download Template..." dulu kalau
            belum punya - khususnya sheet "TREE DATA", yang mengharapkan kolom spesies,
            diameter, tinggi, volume, dan GPS X/Y. Pilih sistem koordinat tempat GPS Anda
            merekam koordinatnya (pilih zona UTM dari daftar, atau "Other" plus WKID
            khusus kalau bukan UTM Indonesia), lalu klik Import Excel. Hasilnya berupa
            layer titik, satu titik per pohon yang di-cruising.

            Geotagged Field Photos
            Mengimpor foto yang sudah punya lokasi GPS tersimpan di metadata EXIF-nya
            (kebanyakan kamera HP dan kamera GPS khusus melakukan ini otomatis). Tiap foto
            jadi satu titik di map; klik titiknya untuk melihat/memperbesar foto lewat
            kartu popup. Ini impor sekali jalan, bukan folder yang dipantau terus - kalau
            nanti ada foto tambahan, jalankan lagi untuk memasukkan foto barunya.

            Photo Coordinate OCR (tanpa EXIF GPS)
            Untuk foto yang koordinatnya "dicetak" langsung di gambar sebagai watermark
            (umum dari aplikasi kamera GPS) alih-alih tersimpan di EXIF - fitur ini
            membaca teks tercetak itu. Pilih format watermark-nya dari dropdown, dan kalau
            formatnya UTM, tentukan zona/hemisphere default sebagai cadangan kalau huruf
            zona di foto tertentu tidak terbaca otomatis. Klik Scan Photos. Semua prosesnya
            berjalan offline sepenuhnya - tidak ada foto yang diunggah ke mana pun. Setiap
            koordinat yang terdeteksi ditampilkan untuk Anda tinjau dan harus dikonfirmasi
            dulu sebelum jadi titik di map, supaya angka yang salah terbaca tidak
            diam-diam membuat titik di lokasi yang salah.

            Cruising Summary Report
            Membuat spreadsheet ringkasan spesies-vs-volume dari layer titik cruising yang
            sudah punya field Volume dan Species (misalnya hasil dari Import Excel di
            atas). Hasilnya tabel data untuk laporan, bukan layout peta yang siap cetak.


            ═══ TAB ANALYZE ═══

            Tree Detection
            Fitur intinya. Pilih layer raster (orthophoto drone) dan profil deteksi:
              • Natural Forest - algoritma berbasis warna dan bentuk (indeks kehijauan
                vegetasi dikombinasikan dengan matched filter) yang disetel untuk tajuk
                pohon alami yang tidak beraturan.
              • Oil Palm Plantation - model AI (YOLOv8) yang dilatih khusus untuk tajuk
                sawit, lebih cocok untuk pola tanam teratur perkebunan.
            Deteksi berjalan di background - aman untuk tetap bekerja, bahkan pindah ke
            map lain, sambil prosesnya berjalan. "Exclude cleared/bare ground" (aktif
            secara default) membuang titik false-positive yang jatuh di tanah gundul,
            jalan, atau area terbuka, dengan konsekuensi waktu proses kira-kira dua kali
            lipat (perlu scan gambar sekali lagi) - matikan setelah yakin sebuah lokasi
            tidak membutuhkannya. Hasilnya layer titik, satu titik per pohon/tajuk yang
            terdeteksi, warna hijau untuk profil forest atau merah untuk oil palm.

            Land Clearing Detection
            Kebalikan dari Tree Detection - menandai tanah gundul/terbuka alih-alih tajuk
            pohon, dari jenis citra yang sama. Bisa opsional pilih layer poligon "exclude
            area" (misalnya area yang sudah diketahui pernah dibuka sebelumnya, seperti
            blok panen lama) supaya hasilnya hanya menunjukkan bukaan yang benar-benar
            baru, dan atur luas minimum dalam hektar untuk mengabaikan bercak noise kecil.
            Kolam dan sungai otomatis dikecualikan dari hasil. Keluarannya layer poligon
            area gundul/terbuka.

            Road/Trail Extraction
            Menarik garis tengah jalan/jalur dari sinyal tanah gundul yang sama dipakai
            Land Clearing Detection, lalu ditipiskan jadi satu garis di tengah jalan,
            bukan area terisi penuh. "Drop stubs shorter than (meters)" membersihkan
            sisa fragmen pendek - 5m titik awal yang wajar; naikkan kalau hasilnya masih
            terlihat berisik/terpecah-pecah. Keluarannya layer garis berupa centerline
            hasil ekstraksi. Keterbatasan yang diketahui: karena dibangun dari sinyal
            tanah gundul yang sama, garis yang ditarik kadang bisa melenceng ke tanah
            gundul di sebelah jalan yang sebenarnya bukan jalan (misalnya area quarry atau
            tumpukan material) - ini masih terus disempurnakan.

            Compare Changes
            Mendeteksi perubahan antara dua hasil Tree Detection terpisah di area yang
            sama pada waktu berbeda (misalnya tahun ini vs tahun lalu). Pilih layer titik
            hasil lama, layer titik hasil baru, dan jarak pencocokan dalam meter (seberapa
            jauh dua titik boleh berbeda posisi dan tetap dianggap "pohon yang sama",
            untuk mentolerir sedikit pergeseran deteksi antar-run). Hasilnya dua layer
            titik baru: "Lost" (merah - pohon ada di hasil lama tapi tidak ada pasangannya
            di hasil baru, kemungkinan sudah ditebang) dan "New" (hijau - pohon di hasil
            baru tanpa pasangan di hasil lama, kemungkinan tumbuh baru atau sebelumnya
            terlewat).

            Sliver Polygon Detection
            Otomatis mencari poligon yang ukurannya jauh lebih kecil atau bentuknya jauh
            lebih tipis/memanjang dari biasanya di sebuah layer poligon - tidak perlu atur
            ambang batas manual, kalibrasinya otomatis mengikuti ukuran dan bentuk khas
            poligon di layer itu sendiri. Berguna untuk menemukan kesalahan digitasi atau
            sel fishnet yang terpotong jadi sliver akibat batas yang tidak rata. Poligon
            yang ditandai langsung terpilih di map.

            Biomass & Carbon Estimation
            Mengestimasi biomassa atas permukaan dan stok karbon dari layer titik yang
            punya field Volume (misalnya data cruising Anda), memakai perhitungan gaya
            IPCC Tier 1. Empat konstanta bisa disesuaikan untuk campuran spesies/wilayah
            Anda: berat jenis kayu, faktor ekspansi biomassa, rasio akar-tajuk, dan
            fraksi karbon. Nilai defaultnya rata-rata global generik, bukan hasil
            kalibrasi untuk spesies tertentu - sesuaikan kalau Anda punya angka lokal yang
            lebih akurat.

            Slope from DEM
            Menghitung kemiringan lereng (persen rise) dari raster Digital Elevation
            Model, untuk membantu menilai seberapa mudah diakses sebuah area untuk alat
            berat penebangan dan pembuatan jalan. Membutuhkan ekstensi ArcGIS Spatial
            Analyst yang berlisensi dan aktif.

            Riparian Buffer Check
            Membuat buffer dari layer sungai/aliran sejauh jarak yang Anda tentukan -
            tidak ada nilai default hukum bawaan, masukkan sesuai yang diwajibkan regulasi
            Anda - dan menandai bagian mana dari poligon rencana Anda yang jatuh di dalam
            buffer tersebut, supaya bisa langsung terlihat blok tebang mana yang masuk ke
            zona riparian terlindungi.


            ═══ TAB SETTINGS ═══

            Advanced Detection Parameters
            Override manual untuk algoritma Tree Detection: Sigma (perkiraan radius tajuk
            dalam piksel), ExG Threshold (seberapa hijau sebuah piksel harus terbaca
            supaya dihitung vegetasi), dan Min Smooth (respons matched-filter minimum
            supaya dihitung sebagai deteksi). Tiap profil deteksi sudah punya nilai
            default yang sudah disetel - ubah ini hanya kalau Anda paham algoritmanya dan
            lokasi tertentu memang butuh nilai berbeda.

            AI Vision Validation
            Pengecekan tambahan opsional yang mengirim potongan gambar tiap pohon
            terdeteksi ke model AI vision (Google Gemini, OpenAI, atau Anthropic Claude)
            untuk memastikan itu memang pohon sebelum disimpan, mengurangi false positive
            lebih jauh. Butuh koneksi internet dan API key dari provider pilihan Anda.
            Key Anda disimpan terenkripsi hanya di komputer ini (tidak pernah dikirim ke
            mana pun selain API resmi provider tersebut). Pakai "Test Key" untuk
            memastikan key-nya berfungsi sebelum mengaktifkan AI validation di deteksi
            sungguhan.


            ═══ TIPS UMUM ═══

            • Kalau hasil sebuah layer terlihat kosong atau salah, cek dulu baris status
              alat itu - kebanyakan kegagalan sudah menjelaskan sendiri di sana (misalnya
              "no active map view", "layer not found - click Refresh").
            • Hasil deteksi dan ekstraksi selalu otomatis dimuat ke map aktif dengan warna/
              simbologi default yang masuk akal - bisa diubah gayanya seperti layer lain
              setelahnya.
            • Tidak ada bagian add-in ini yang mengunggah citra Anda ke mana pun secara
              default. Satu-satunya fitur yang mengirim data keluar dari komputer ini
              adalah AI Vision Validation (tab Settings, opsional), dan itu pun hanya
              potongan kecil gambar pohon yang secara eksplisit Anda aktifkan untuk
              dicek - bukan orthophoto utuh.
            """;
    }
}
