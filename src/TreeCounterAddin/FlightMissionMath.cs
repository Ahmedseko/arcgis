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

        public record Plan(List<Waypoint> Waypoints, int MissionPartCount, double TotalDistanceM, double TotalFlightMinutes);

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

            var orderedWaypoints = new List<(double X, double Y)>();
            var lineParity = 0;
            foreach (var x in xs)
            {
                var ys = new List<double>();
                for (var y = minY; y <= maxY; y += waypointSpacingM)
                    ys.Add(y);
                if (ys.Count == 0)
                    ys.Add((minY + maxY) / 2.0);

                var linePoints = ys.Where(y => PointInPolygon(x, y, toLocal)).Select(y => (x, y)).ToList();
                if (linePoints.Count == 0) continue;

                // Boustrophedon: alternate direction line-to-line so consecutive lines connect
                // at their nearest ends instead of the drone jumping back across the whole site
                // after every single pass.
                if (lineParity % 2 == 1) linePoints.Reverse();
                orderedWaypoints.AddRange(linePoints);
                lineParity++;
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
            return new Plan(waypoints, waypoints.Count == 0 ? 0 : missionPart, totalDistanceM, totalMinutes);
        }
    }
}
