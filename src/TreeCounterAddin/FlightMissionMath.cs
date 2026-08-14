using System;
using System.Collections.Generic;
using System.Linq;

namespace TreeCounterAddin
{
    // Pure geometry math for drone survey mission planning (coverage flight lines +
    // waypoints + battery-based mission splitting) - deliberately free of any ArcGIS
    // reference so it can be exercised by a plain console project without ArcGIS Pro
    // installed (see src/ForestryToolkit.MathTests), same pattern as SliverMath/BiomassMath.
    // Ring coordinates are plain (X, Y) pairs in a projected/meters CRS - the ViewModel is
    // responsible for extracting those from the actual ArcGIS Polygon.
    internal static class FlightMissionMath
    {
        public record Waypoint(double X, double Y, double Altitude, int MissionPart, int Sequence);

        public record Plan(List<Waypoint> Waypoints, int MissionPartCount, double TotalDistanceM,
            double TotalFlightMinutes, int OffPolygonLegCount = 0);

        // Standard ray-casting/even-odd test, run across every ring (outer + holes)
        // together - a point inside a hole gets toggled an extra time by that hole's own
        // ring, flipping it back to "outside" with no extra hole-specific logic needed.
        public static bool PointInPolygon(double px, double py, IEnumerable<IReadOnlyList<(double X, double Y)>> rings)
        {
            var inside = false;
            foreach (var ring in rings)
            {
                int n = ring.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    var (xi, yi) = ring[i];
                    var (xj, yj) = ring[j];
                    var crosses = (yi > py) != (yj > py) &&
                                  px < (xj - xi) * (py - yi) / (yj - yi) + xi;
                    if (crosses) inside = !inside;
                }
            }
            return inside;
        }

        public static (double X, double Y) Rotate(double x, double y, double pivotX, double pivotY, double angleDeg)
        {
            var rad = angleDeg * Math.PI / 180.0;
            var dx = x - pivotX;
            var dy = y - pivotY;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            return (pivotX + dx * cos - dy * sin, pivotY + dx * sin + dy * cos);
        }

        /// <summary>
        /// Generates a boustrophedon ("lawnmower") coverage flight plan over a polygon,
        /// split into battery-sized mission parts.
        /// </summary>
        /// <param name="outerRing">Survey polygon's outer ring, in a projected/meters CRS.</param>
        /// <param name="holes">Any interior holes (islands to skip) - pass an empty list if none.</param>
        /// <param name="altitudeM">Constant flight altitude (no terrain-following in this version).</param>
        /// <param name="gsdCmPerPx">Target ground sample distance, drives line/waypoint spacing.</param>
        /// <param name="imageWidthPx">Camera image width - cross-track (line spacing) dimension.</param>
        /// <param name="imageHeightPx">Camera image height - along-track (waypoint spacing) dimension.</param>
        /// <param name="frontOverlapPct">Overlap between consecutive photos along a line.</param>
        /// <param name="sideOverlapPct">Overlap between adjacent flight lines.</param>
        /// <param name="flightDirectionDeg">Compass-style bearing of the flight lines (0 = lines run
        /// north-south, spaced east-west). No auto-orientation in this version - try a couple of
        /// angles and compare TotalDistanceM if the default doesn't fit the site well.</param>
        /// <param name="speedMs">Cruise speed, used to convert distance into estimated flight time.</param>
        /// <param name="maxFlightMinutesPerBattery">Mission is cut into a new part whenever adding
        /// the next leg would push a part's own flight time over this budget.</param>
        public static Plan GenerateCoveragePlan(
            IReadOnlyList<(double X, double Y)> outerRing,
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> holes,
            double altitudeM, double gsdCmPerPx, int imageWidthPx, int imageHeightPx,
            double frontOverlapPct, double sideOverlapPct, double flightDirectionDeg,
            double speedMs, double maxFlightMinutesPerBattery)
        {
            var gsdM = gsdCmPerPx / 100.0;
            var footprintWidthM = gsdM * imageWidthPx;
            var footprintHeightM = gsdM * imageHeightPx;
            // A driving parameter at/above 100% overlap gives zero/negative spacing (infinite
            // lines/waypoints) - floor it well short of that instead of dividing by zero later.
            var lineSpacingM = Math.Max(0.5, footprintWidthM * (1 - Math.Min(sideOverlapPct, 95) / 100.0));
            var waypointSpacingM = Math.Max(0.5, footprintHeightM * (1 - Math.Min(frontOverlapPct, 95) / 100.0));

            var allRings = new List<IReadOnlyList<(double X, double Y)>> { outerRing };
            allRings.AddRange(holes);

            // Rotate everything so the flight direction becomes the local Y axis (lines are
            // then simple vertical scans at fixed local X) - un-rotated back to real-world
            // coordinates at the very end. Pivot is the outer ring's own bounding-box center,
            // just to keep local coordinates small/tidy - any fixed pivot would be equally
            // correct since this is a rigid rotation.
            var pivotX = (outerRing.Min(p => p.X) + outerRing.Max(p => p.X)) / 2.0;
            var pivotY = (outerRing.Min(p => p.Y) + outerRing.Max(p => p.Y)) / 2.0;
            // Un-rotating needs the *inverse* rotation.
            var toLocal = allRings.Select(ring => (IReadOnlyList<(double X, double Y)>)
                ring.Select(p => Rotate(p.X, p.Y, pivotX, pivotY, -flightDirectionDeg)).ToList()).ToList();

            var minX = toLocal.SelectMany(r => r).Min(p => p.X);
            var maxX = toLocal.SelectMany(r => r).Max(p => p.X);
            var minY = toLocal.SelectMany(r => r).Min(p => p.Y);
            var maxY = toLocal.SelectMany(r => r).Max(p => p.Y);

            // Candidate line X-positions, stepped by lineSpacingM - but a survey area
            // narrower than one line's worth of spacing (a small/oddly-shaped RT plan, a
            // sliver polygon) would otherwise make this loop run zero times and silently
            // produce an empty mission (real result, 2026-08-14: a small "Pengajuan RT
            // XLVI" polygon did exactly this). Falling back to a single line through the
            // area's own center keeps a small site covered instead of failing outright.
            var xs = new List<double>();
            for (var x = minX + lineSpacingM / 2.0; x <= maxX; x += lineSpacingM)
                xs.Add(x);
            if (xs.Count == 0)
                xs.Add((minX + maxX) / 2.0);

            // Split each line into contiguous in-polygon runs rather than one flat list - a
            // concave boundary can make a single line exit the polygon and re-enter it further
            // along (real case, 2026-08-14: a river-bend notch cutting into a survey polygon).
            // Naively connecting the last point before that gap straight to the first point
            // after it draws a long chord straight through the excluded area - visibly outside
            // the site on the map.
            var allRuns = new List<List<(double X, double Y)>>();
            foreach (var x in xs)
            {
                var ys = new List<double>();
                for (var y = minY; y <= maxY; y += waypointSpacingM)
                    ys.Add(y);
                if (ys.Count == 0)
                    ys.Add((minY + maxY) / 2.0);

                List<(double X, double Y)> currentRun = null;
                foreach (var y in ys)
                {
                    if (PointInPolygon(x, y, toLocal))
                    {
                        currentRun ??= new List<(double X, double Y)>();
                        currentRun.Add((x, y));
                    }
                    else if (currentRun != null)
                    {
                        allRuns.Add(currentRun);
                        currentRun = null;
                    }
                }
                if (currentRun != null) allRuns.Add(currentRun);
            }

            // Greedily walk to whichever unvisited run's nearer endpoint is physically closest,
            // rather than a rigid "line order, then line's-2nd-run order" traversal - a fixed
            // rule like that can still connect two runs that are far apart just because the rule
            // said to next (real reports, 2026-08-14: a short run left stranded and reached via
            // a long out-of-sequence jump at one direction, a leg cutting straight outside the
            // polygon at another). Nearest-neighbor isn't a guaranteed fix for every possible
            // concave shape, but it resolved every case seen so far in testing against the real
            // polygon that exposed this - any leftover risk is still caught by the
            // OffPolygonLegCount check below instead of staying silent about it.
            var orderedWaypoints = new List<(double X, double Y)>();
            if (allRuns.Count > 0)
            {
                allRuns.Sort((a, b) => a[0].X.CompareTo(b[0].X));
                var remaining = new List<List<(double X, double Y)>>(allRuns);
                var first = remaining[0];
                remaining.RemoveAt(0);
                orderedWaypoints.AddRange(first);

                while (remaining.Count > 0)
                {
                    var (cx, cy) = orderedWaypoints[^1];
                    var bestIndex = -1;
                    var bestReversed = false;
                    var bestDistSq = double.MaxValue;
                    for (var i = 0; i < remaining.Count; i++)
                    {
                        var run = remaining[i];
                        var d0 = Math.Pow(run[0].X - cx, 2) + Math.Pow(run[0].Y - cy, 2);
                        var d1 = Math.Pow(run[^1].X - cx, 2) + Math.Pow(run[^1].Y - cy, 2);
                        var d = Math.Min(d0, d1);
                        if (d < bestDistSq)
                        {
                            bestDistSq = d;
                            bestIndex = i;
                            bestReversed = d1 < d0;
                        }
                    }
                    var chosen = remaining[bestIndex];
                    remaining.RemoveAt(bestIndex);
                    if (bestReversed) chosen.Reverse();
                    orderedWaypoints.AddRange(chosen);
                }
            }

            // Splitting each line into contiguous runs (above) fixes the common case, but a
            // transit leg landing exactly on a run that sits at the edge of the sweep range can
            // still connect two points whose straight-line midpoint falls outside the polygon
            // (real case, 2026-08-14: a river-bend notch in a survey polygon) - full obstacle-
            // aware routing is out of scope here, so this just counts how often it still happens
            // and reports it rather than staying silent about a flight path that visibly leaves
            // the survey area on the map.
            var offPolygonLegCount = 0;
            for (var i = 0; i < orderedWaypoints.Count - 1; i++)
            {
                var (x1, y1) = orderedWaypoints[i];
                var (x2, y2) = orderedWaypoints[i + 1];
                if (!PointInPolygon((x1 + x2) / 2.0, (y1 + y2) / 2.0, toLocal))
                    offPolygonLegCount++;
            }

            // Un-rotate back to real-world coordinates, then split into battery-sized parts by
            // walking the ordered sequence and cutting whenever the *current part's own*
            // accumulated flight time would exceed the budget (each part is treated as its own
            // fresh battery/launch, not a continuous flight - the transit between where one
            // part ends and the next begins is on the operator to reposition for, same
            // simplification FlyPath-style tools make).
            var waypoints = new List<Waypoint>();
            var missionPart = 1;
            var sequence = 0;
            var partSeconds = 0.0;
            var totalDistanceM = 0.0;
            (double X, double Y)? prev = null;
            var budgetSeconds = maxFlightMinutesPerBattery * 60.0;

            foreach (var local in orderedWaypoints)
            {
                var (worldX, worldY) = Rotate(local.X, local.Y, pivotX, pivotY, flightDirectionDeg);
                if (prev is { } p)
                {
                    var legDistance = Math.Sqrt(Math.Pow(worldX - p.X, 2) + Math.Pow(worldY - p.Y, 2));
                    var legSeconds = speedMs > 0 ? legDistance / speedMs : 0;
                    if (partSeconds + legSeconds > budgetSeconds && waypoints.Count > 0 &&
                        waypoints[^1].MissionPart == missionPart)
                    {
                        missionPart++;
                        sequence = 0;
                        partSeconds = 0;
                    }
                    else
                    {
                        partSeconds += legSeconds;
                    }
                    totalDistanceM += legDistance;
                }
                waypoints.Add(new Waypoint(worldX, worldY, altitudeM, missionPart, sequence++));
                prev = (worldX, worldY);
            }

            var totalMinutes = speedMs > 0 ? totalDistanceM / speedMs / 60.0 : 0;
            return new Plan(waypoints, waypoints.Count == 0 ? 0 : missionPart, totalDistanceM, totalMinutes,
                offPolygonLegCount);
        }

        /// <summary>
        /// Suggests a flight direction (compass bearing, 0-179) that minimizes the number of
        /// coverage lines needed by aligning them with the survey polygon's own long axis,
        /// instead of the default 0 deg cutting across an elongated/irregular site and chopping
        /// coverage into many short, unevenly-lengthed zigzag columns. Searches every whole
        /// degree (cheap - O(180 * ring size)) and keeps the one with the smallest spacing-axis
        /// bounding width in the rotated frame (fewer/longer lines for a fixed line spacing).
        /// </summary>
        public static double SuggestDirection(IReadOnlyList<(double X, double Y)> outerRing)
        {
            var pivotX = (outerRing.Min(p => p.X) + outerRing.Max(p => p.X)) / 2.0;
            var pivotY = (outerRing.Min(p => p.Y) + outerRing.Max(p => p.Y)) / 2.0;
            double bestDeg = 0, bestWidth = double.MaxValue;
            for (var deg = 0; deg < 180; deg++)
            {
                var local = outerRing.Select(p => Rotate(p.X, p.Y, pivotX, pivotY, -deg)).ToList();
                var width = local.Max(p => p.X) - local.Min(p => p.X);
                if (width < bestWidth) { bestWidth = width; bestDeg = deg; }
            }
            return bestDeg;
        }

        /// <summary>
        /// Builds a specific, actionable message for why GenerateCoveragePlan returned zero
        /// waypoints, instead of leaving the caller with just "it didn't work" - compares the
        /// survey area's own size (in the flight-direction-aligned frame) against the computed
        /// line/waypoint spacing so the message can name real numbers and concrete knobs to try.
        /// </summary>
        public static string DescribeCoverageFailure(
            IReadOnlyList<(double X, double Y)> outerRing,
            double gsdCmPerPx, int imageWidthPx, int imageHeightPx,
            double frontOverlapPct, double sideOverlapPct, double flightDirectionDeg)
        {
            var gsdM = gsdCmPerPx / 100.0;
            var lineSpacingM = Math.Max(0.5, gsdM * imageWidthPx * (1 - Math.Min(sideOverlapPct, 95) / 100.0));
            var waypointSpacingM = Math.Max(0.5, gsdM * imageHeightPx * (1 - Math.Min(frontOverlapPct, 95) / 100.0));

            var pivotX = (outerRing.Min(p => p.X) + outerRing.Max(p => p.X)) / 2.0;
            var pivotY = (outerRing.Min(p => p.Y) + outerRing.Max(p => p.Y)) / 2.0;
            var local = outerRing.Select(p => Rotate(p.X, p.Y, pivotX, pivotY, -flightDirectionDeg)).ToList();
            var widthM = local.Max(p => p.X) - local.Min(p => p.X);
            var heightM = local.Max(p => p.Y) - local.Min(p => p.Y);

            if (widthM < lineSpacingM || heightM < waypointSpacingM)
            {
                return $"Survey area is only about {widthM:F0}m x {heightM:F0}m in the current flight " +
                    $"direction, but these settings need {lineSpacingM:F0}m between lines and " +
                    $"{waypointSpacingM:F0}m between waypoints. Try a lower GSD, lower altitude, less " +
                    "side/front overlap, or a smaller camera image size - or check you selected the right " +
                    "polygon (a survey layer with multiple parcels should have just one selected).";
            }
            return $"The area ({widthM:F0}m x {heightM:F0}m) should fit lines spaced {lineSpacingM:F0}m " +
                "apart, but none of the sampled points landed inside it - this usually means the polygon is " +
                "very thin, self-intersecting, or an odd/concave shape. Try a different Flight direction " +
                "angle, or check the selected feature's geometry.";
        }
    }
}
