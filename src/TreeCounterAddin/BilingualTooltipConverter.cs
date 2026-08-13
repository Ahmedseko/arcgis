using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace TreeCounterAddin
{
    // Bilingual field-level tooltips (Flight Mission Planner's parameter grid, currently
    // the only place these are used) - reuses the same IsHelpEnglish flag the Help tab
    // already exposes (see TreeCounterDockpaneViewModel.Help.cs) rather than adding a
    // second, independent language switch just for tooltips, so picking a language once
    // in Help also drives these. ConverterParameter is a short lookup key (e.g. "Altitude"),
    // not the tooltip text itself, so the actual English/Indonesian sentences live in one
    // readable dictionary here instead of being inlined into the XAML on every field.
    public class BilingualTooltipConverter : IValueConverter
    {
        public static readonly Dictionary<string, (string En, string Id)> Tooltips = new()
        {
            ["Altitude"] = (
                "Flight height above the ground/takeoff point. Higher altitude covers more area per photo but produces a coarser GSD.",
                "Ketinggian terbang di atas titik takeoff. Makin tinggi, area tercakup per foto makin luas tapi detail (GSD) makin kasar."),
            ["Gsd"] = (
                "Ground sample distance - how many cm on the ground one pixel covers. Lower = more detail, more photos needed. 5cm matches this toolkit's own detection defaults.",
                "Ground sample distance - berapa cm di tanah yang terwakili 1 piksel. Makin kecil = makin detail, tapi butuh lebih banyak foto. 5cm cocok dengan default deteksi di toolkit ini."),
            ["ImageWidth"] = (
                "Your drone camera's photo width in pixels - check its specs. Determines each photo's ground footprint together with GSD.",
                "Lebar foto kamera drone Anda dalam piksel (cek spesifikasi drone). Menentukan luas cakupan tanah per foto bersama GSD."),
            ["ImageHeight"] = (
                "Your drone camera's photo height in pixels - check its specs. Determines each photo's ground footprint together with GSD.",
                "Tinggi foto kamera drone Anda dalam piksel (cek spesifikasi drone). Menentukan luas cakupan tanah per foto bersama GSD."),
            ["FrontOverlap"] = (
                "Overlap between consecutive photos along a flight line. Higher = better 3D reconstruction, more photos/flight time.",
                "Overlap antar foto berurutan di satu garis terbang. Makin tinggi = rekonstruksi 3D lebih baik, tapi lebih banyak foto/waktu terbang."),
            ["SideOverlap"] = (
                "Overlap between adjacent flight lines. Higher = denser coverage, more lines needed.",
                "Overlap antar garis terbang yang bersebelahan. Makin tinggi = cakupan lebih rapat, garis lebih banyak."),
            ["Direction"] = (
                "Compass bearing the flight lines run. 0 = lines run north-south. Try a different angle if the site is diagonal/elongated.",
                "Arah kompas garis terbang. 0 = garis terbang utara-selatan. Coba sudut lain kalau bentuk lokasi memanjang miring."),
            ["Speed"] = (
                "Drone cruise speed - used to estimate flight time and split missions by battery.",
                "Kecepatan jelajah drone - dipakai untuk estimasi waktu terbang dan pembagian misi per baterai."),
            ["Battery"] = (
                "Missions longer than this get split into separate battery-sized parts.",
                "Misi yang lebih lama dari ini otomatis dipecah jadi beberapa bagian sesuai kapasitas baterai."),
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isEnglish = value is bool b && b;
            if (parameter is string key && Tooltips.TryGetValue(key, out var pair))
                return isEnglish ? pair.En : pair.Id;
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
