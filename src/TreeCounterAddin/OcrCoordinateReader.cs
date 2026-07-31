using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using Tesseract;

namespace TreeCounterAddin
{
    // Reads GPS coordinates burned into a photo's visible watermark (e.g. from a "GPS Map
    // Camera"-style app) via local/offline OCR - for photos whose EXIF GPS is missing or
    // blank (see ForestryToolkit's Geotagged Field Photos feature for photos that DO have
    // real EXIF GPS). Runs entirely on-device (Tesseract, bundled tessdata) - no photo or
    // text ever leaves the machine.
    internal static class OcrCoordinateReader
    {
        // X/Y instead of Lon/Lat naming - a UTM-format watermark gives Easting/Northing in
        // meters (see below), a decimal-degree watermark gives Longitude/Latitude in
        // degrees. Wkid tells the caller which one it is.
        public record Result(string RawText, double? X, double? Y, int? Wkid);

        // Full form: "50M 264156 9987378" - UTM zone number + MGRS latitude band letter,
        // then easting/northing in meters (this org's actual field camera watermark).
        private static readonly Regex UtmGridWithZone =
            new(@"\b(\d{1,2})\s*([C-HJ-NP-Z])\s+(\d{5,7})\s+(\d{6,8})\b", RegexOptions.Compiled);

        // Fallback form: bare "264156 9987378" with no zone/band prefix visible/readable -
        // the caller's chosen default zone + hemisphere is used for these.
        private static readonly Regex BareEastingNorthing =
            new(@"\b(\d{5,7})\s+(\d{6,8})\b", RegexOptions.Compiled);

        // Decimal-degree watermark ("-2.123456, 113.456789" or "2.123456 S 113.456789 E")
        // from other camera apps.
        private static readonly Regex DecimalDegrees =
            new(@"(-?\d{1,3}\.\d{3,8})\s*°?\s*([NSEW])?", RegexOptions.Compiled);

        // expectUtm reflects the user's explicit "photos use UTM Grid / Lat-Long" choice in
        // the panel - kept as two separate code paths rather than trying both blindly,
        // since a UTM easting could otherwise be misread as a decimal-degree number or vice
        // versa. defaultZone/defaultIsSouth only matter for expectUtm=true photos where the
        // zone+band prefix itself isn't readable.
        // Verified against this org's actual watermark font/layout via a standalone harness
        // (not shippable test infra - see conversation notes): the "fast" trained data
        // confidently misreads its digits (5<->S, 9<->Q, 0<->O), and the default page
        // segmentation gets confused whenever the small Google-Maps-style thumbnail some
        // apps overlay sits in the same crop as the text. "best" trained data + SingleBlock
        // segmentation + excluding the right-hand thumbnail region fixed all three.
        public static Result ReadCoordinates(string tessdataPath, string photoPath, bool expectUtm, int defaultZone, bool defaultIsSouth)
        {
            using var engine = new TesseractEngine(tessdataPath, "eng", EngineMode.Default);

            // OCR-ing the whole photo reads the surrounding scene (trees, gravel, people) as
            // noise and returns garbage text - these watermark apps put the text as a block
            // along the bottom-left of the frame (bottom-right is often a small map preview
            // thumbnail instead, excluded here since mixing it into the same crop confuses
            // page segmentation). Falls back to the full image if nothing parseable came out
            // of the crop, in case a photo's watermark sits somewhere else entirely.
            var croppedText = RunOcr(engine, LoadCroppedBottomLeftRegion(photoPath, 0.4, 0.75));
            var result = expectUtm
                ? TryParseUtmGrid(croppedText) ?? TryParseBareEastingNorthing(croppedText, defaultZone, defaultIsSouth)
                : ParseDecimalDegrees(croppedText);
            if (result != null && result.X.HasValue) return result;

            var fullText = RunOcr(engine, Pix.LoadFromFile(photoPath));
            return expectUtm
                ? TryParseUtmGrid(fullText) ?? TryParseBareEastingNorthing(fullText, defaultZone, defaultIsSouth) ?? new Result(fullText, null, null, null)
                : ParseDecimalDegrees(fullText);
        }

        private static string RunOcr(TesseractEngine engine, Pix image)
        {
            using (image)
            // SingleBlock (assume one uniform block of text) reads this watermark style far
            // more reliably than the default Auto segmentation, which tends to fragment it
            // into scattered lines and drop whole rows (including the coordinate line).
            using (var page = engine.Process(image, PageSegMode.SingleBlock))
                return page.GetText() ?? "";
        }

        private static Pix LoadCroppedBottomLeftRegion(string photoPath, double bottomFraction, double leftFraction)
        {
            var decoder = BitmapDecoder.Create(new Uri(photoPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var cropHeight = Math.Max(1, (int)(frame.PixelHeight * bottomFraction));
            var cropWidth = Math.Max(1, (int)(frame.PixelWidth * leftFraction));
            var top = frame.PixelHeight - cropHeight;
            var cropped = new CroppedBitmap(frame, new Int32Rect(0, top, cropWidth, cropHeight));

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            encoder.Save(ms);
            return Pix.LoadFromMemory(ms.ToArray());
        }

        private static Result TryParseUtmGrid(string text)
        {
            var m = UtmGridWithZone.Match(text);
            if (!m.Success) return null;

            if (!int.TryParse(m.Groups[1].Value, out var zone) || zone < 1 || zone > 60)
                return null;
            if (!double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var easting))
                return null;
            if (!double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var northing))
                return null;

            // MGRS latitude bands C-M are south of the equator, N-X are north (I and O are
            // never used, but excluding them from the pattern is enough - no need to special
            // case them here).
            var band = m.Groups[2].Value[0];
            var isSouth = band <= 'M';
            var wkid = (isSouth ? 32700 : 32600) + zone;

            return new Result(text, easting, northing, wkid);
        }

        private static Result TryParseBareEastingNorthing(string text, int defaultZone, bool defaultIsSouth)
        {
            var m = BareEastingNorthing.Match(text);
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var easting))
                return null;
            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var northing))
                return null;

            var wkid = (defaultIsSouth ? 32700 : 32600) + defaultZone;
            return new Result(text, easting, northing, wkid);
        }

        private static Result ParseDecimalDegrees(string text)
        {
            double? lat = null, lon = null;
            foreach (Match m in DecimalDegrees.Matches(text))
            {
                if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                var hemisphere = m.Groups[2].Success ? m.Groups[2].Value : null;
                if (hemisphere is "S" or "W")
                    value = -Math.Abs(value);

                // A latitude/longitude pair is disambiguated purely by valid range here
                // (first plausible latitude, then first plausible longitude) - reasonable
                // worldwide since |latitude| can never exceed 90 while most longitudes of
                // interest do, but it's still just a heuristic, which is exactly why every
                // result goes through manual review before becoming a point.
                if (lat == null && Math.Abs(value) <= 90)
                    lat = value;
                else if (lon == null && Math.Abs(value) <= 180)
                    lon = value;
            }

            var wkid = lat.HasValue && lon.HasValue ? 4326 : (int?)null;
            return new Result(text, lon, lat, wkid);
        }
    }
}
