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

        // Same edge-crossing test as PointInPolygon, but returns the actual Y of every
        // crossing instead of a single in/out boolean - the standard scanline-fill technique.
        // Sorted, these pair up into exact inside-intervals (crossings[0]-crossings[1] is
        // inside, crossings[1]-crossings[2] is outside, and so on - holes flip the parity
        // automatically, same as PointInPolygon). Used so a coverage line's first/last waypoint
        // lands exactly on the polygon boundary instead of stopping short at whatever point
        // happens to fall on the fixed waypoint-spacing grid (real report, 2026-08-14: a
        // diagonal/tapered polygon edge left a visible gap between the flat-cut end of a
        // coverage column and the actual boundary above it).
        private static List<double> VerticalLineCrossings(double px, IEnumerable<IReadOnlyList<(double X, double Y)>> rings)
        {
            var crossings = new List<double>();
            foreach (var ring in rings)
            {
                int n = ring.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    var (xi, yi) = ring[i];
                    var (xj, yj) = ring[j];
                    if ((xi > px) != (xj > px))
                        crossings.Add((yj - yi) * (px - xi) / (xj - xi) + yi);
                }
            }
            crossings.Sort();
            return crossings;
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
        /// <param name="crossHatch">Also flies a second pass at flightDirectionDeg+90 and appends
        /// it as further mission parts - two perpendicular passes over the same site, a standard
        /// photogrammetry technique for better 3D reconstruction (building facades and other
        /// vertical features get seen from more angles than a single-direction grid manages).</param>
        public static Plan GenerateCoveragePlan(
            IReadOnlyList<(double X, double Y)> outerRing,
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> holes,
            double altitudeM, double gsdCmPerPx, int imageWidthPx, int imageHeightPx,
            double frontOverlapPct, double sideOverlapPct, double flightDirectionDeg,
            double speedMs, double maxFlightMinutesPerBattery, bool crossHatch = false)
        {
            var primary = GenerateSinglePassPlan(outerRing, holes, altitudeM, gsdCmPerPx, imageWidthPx,
                imageHeightPx, frontOverlapPct, sideOverlapPct, flightDirectionDeg, speedMs,
                maxFlightMinutesPerBattery);
            if (!crossHatch) return primary;

            var secondary = GenerateSinglePassPlan(outerRing, holes, altitudeM, gsdCmPerPx, imageWidthPx,
                imageHeightPx, frontOverlapPct, sideOverlapPct, flightDirectionDeg + 90, speedMs,
                maxFlightMinutesPerBattery);

            var partOffset = primary.MissionPartCount;
            var mergedWaypoints = new List<Waypoint>(primary.Waypoints);
            mergedWaypoints.AddRange(secondary.Waypoints.Select(w => w with { MissionPart = w.MissionPart + partOffset }));

            return new Plan(
                mergedWaypoints,
                primary.MissionPartCount + secondary.MissionPartCount,
                primary.TotalDistanceM + secondary.TotalDistanceM,
                primary.TotalFlightMinutes + secondary.TotalFlightMinutes,
                primary.OffPolygonLegCount + secondary.OffPolygonLegCount);
        }

        private static Plan GenerateSinglePassPlan(
            IReadOnlyList<(double X, double Y)> outerRing,
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> holes,
            double altitudeM, double gsdCmPerPx, int imageWidthPx, int imageHeightPx,
            double frontOverlapPct, double sideOverlapPct, double flightDirectionDeg,
            double speedMs, double maxFlightMinutesPerBattery)
        {
            var gsdM = gsdCmPerPx / 100.0;
            var footprintHeightM = gsdM * imageHeightPx;
            var lineSpacingM = LineSpacingM(gsdCmPerPx, imageWidthPx, sideOverlapPct);
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
            // the site on the map. Each run's own start/end come from the actual polygon
            // boundary crossings (not the fixed waypoint-spacing grid), so a column follows a
            // diagonal/tapered edge right up to it instead of stopping short in a flat cut.
            var allRuns = new List<List<(double X, double Y)>>();
            foreach (var x in xs)
            {
                var crossings = VerticalLineCrossings(x, toLocal);
                for (var k = 0; k + 1 < crossings.Count; k += 2)
                {
                    // A tiny safety inset off the literal boundary - two adjacent lines' runs
                    // now start right at the edge, so the straight transit leg connecting them
                    // "cuts the corner" near any vertex where the boundary changes direction
                    // between those two lines. Landing exactly on the boundary made that chord
                    // noticeably likely to clip outside (real report, 2026-08-14: 22 legs on
                    // this polygon); 2m fixes essentially all of it while staying visually
                    // indistinguishable from following the edge exactly (the previous flat-cut
                    // gap this whole change fixes was 15-30m, not 2m).
                    const double edgeInsetM = 2.0;
                    var span = crossings[k + 1] - crossings[k];
                    if (span < 1e-6) continue; // degenerate/tangent crossing
                    var inset = Math.Min(edgeInsetM, span / 2.0 - 1e-6);
                    var yStart = crossings[k] + inset;
                    var yEnd = crossings[k + 1] - inset;
                    if (yEnd - yStart < 1e-6)
                        yStart = yEnd = (crossings[k] + crossings[k + 1]) / 2.0;

                    var run = new List<(double X, double Y)> { (x, yStart) };
                    var y = yStart + waypointSpacingM;
                    while (y < yEnd - 1e-6)
                    {
                        run.Add((x, y));
                        y += waypointSpacingM;
                    }
                    run.Add((x, yEnd));
                    allRuns.Add(run);
                }
            }

            var (orderedLocal, offPolygonLegCount) = OrderRuns(allRuns, toLocal);
            var orderedWorld = orderedLocal.Select(p => Rotate(p.X, p.Y, pivotX, pivotY, flightDirectionDeg)).ToList();
            var (waypoints, missionPartCount, totalDistanceM, totalMinutes) =
                SplitIntoMissionParts(orderedWorld, altitudeM, speedMs, maxFlightMinutesPerBattery);
            return new Plan(waypoints, missionPartCount, totalDistanceM, totalMinutes, offPolygonLegCount);
        }

        // Shared by GenerateSinglePassPlan and GenerateCorridorPlan - orders a set of already-
        // computed coverage runs into one flight sequence. Greedily walks to whichever unvisited
        // run's nearer endpoint is physically closest, rather than a rigid "line order, then
        // line's-2nd-run order" traversal - a fixed rule like that can still connect two runs
        // that are far apart just because the rule said to next (real reports, 2026-08-14: a
        // short run left stranded and reached via a long out-of-sequence jump at one direction,
        // a leg cutting straight outside the polygon at another). A single nearest-neighbor
        // construction can still land on a tour with one unlucky long detour purely from where
        // it happened to start, so this tries every possible starting run (and direction) and
        // keeps whichever tour has the fewest off-polygon legs, then the shortest worst-case
        // transit - cheap (O(runs^3), trivial for anything short of hundreds of lines) and
        // consistently finds a materially better tour (verified against the real river-bend-
        // notch polygon). Not a guaranteed fix for every possible concave shape, so the returned
        // OffPolygonLegCount is a leftover-risk count to surface to the user, not silently drop.
        private static (List<(double X, double Y)> Waypoints, int OffPolygonLegCount) OrderRuns(
            List<List<(double X, double Y)>> allRuns, List<IReadOnlyList<(double X, double Y)>> rings)
        {
            if (allRuns.Count == 0) return (new List<(double X, double Y)>(), 0);

            List<(double X, double Y)> BuildTour(int startIndex, bool reverseStart)
            {
                var remaining = new List<List<(double X, double Y)>>(allRuns);
                var current = new List<(double X, double Y)>(remaining[startIndex]);
                remaining.RemoveAt(startIndex);
                if (reverseStart) current.Reverse();
                var tourRuns = new List<List<(double X, double Y)>> { current };
                while (remaining.Count > 0)
                {
                    var (cx, cy) = tourRuns[^1][^1];
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
                    var chosen = new List<(double X, double Y)>(remaining[bestIndex]);
                    remaining.RemoveAt(bestIndex);
                    if (bestReversed) chosen.Reverse();
                    tourRuns.Add(chosen);
                }
                var flat = new List<(double X, double Y)>();
                foreach (var run in tourRuns) flat.AddRange(run);
                return flat;
            }

            (int OffPolygonCount, double MaxTurnM) ScoreTour(List<(double X, double Y)> tour)
            {
                var offCount = 0;
                var maxTurn = 0.0;
                for (var i = 0; i < tour.Count - 1; i++)
                {
                    var (x1, y1) = tour[i];
                    var (x2, y2) = tour[i + 1];
                    if (!PointInPolygon((x1 + x2) / 2.0, (y1 + y2) / 2.0, rings))
                        offCount++;
                    var d = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
                    if (d > maxTurn) maxTurn = d;
                }
                return (offCount, maxTurn);
            }

            var startCandidates = allRuns.Count <= 60 ? allRuns.Count : 1;
            List<(double X, double Y)> bestTour = null;
            var bestScore = (OffPolygonCount: int.MaxValue, MaxTurnM: double.MaxValue);
            for (var startIndex = 0; startIndex < startCandidates; startIndex++)
            {
                foreach (var reverseStart in new[] { false, true })
                {
                    var tour = BuildTour(startIndex, reverseStart);
                    var score = ScoreTour(tour);
                    if (score.OffPolygonCount < bestScore.OffPolygonCount ||
                        (score.OffPolygonCount == bestScore.OffPolygonCount && score.MaxTurnM < bestScore.MaxTurnM))
                    {
                        bestScore = score;
                        bestTour = tour;
                    }
                }
            }

            return (bestTour, bestScore.OffPolygonCount);
        }

        // Shared by GenerateSinglePassPlan and GenerateCorridorPlan - walks an already-ordered,
        // real-world-coordinate waypoint sequence and cuts it into battery-sized mission parts
        // whenever adding the next leg would push the *current part's own* accumulated flight
        // time over budget (each part is treated as its own fresh battery/launch, not a
        // continuous flight - the transit between where one part ends and the next begins is on
        // the operator to reposition for). The cut point is repeated as a seam waypoint - the
        // closing waypoint of the part that's ending *and* the opening waypoint of the next one -
        // so the two parts share an exact coordinate instead of each just picking up wherever the
        // sequence happened to land; the next battery's takeoff lines up exactly with where the
        // previous one left off.
        private static (List<Waypoint> Waypoints, int MissionPartCount, double TotalDistanceM, double TotalMinutes)
            SplitIntoMissionParts(List<(double X, double Y)> orderedPoints, double altitudeM, double speedMs,
                double maxFlightMinutesPerBattery)
        {
            var waypoints = new List<Waypoint>();
            var missionPart = 1;
            var sequence = 0;
            var partSeconds = 0.0;
            var totalDistanceM = 0.0;
            (double X, double Y)? prev = null;
            var budgetSeconds = maxFlightMinutesPerBattery * 60.0;

            foreach (var point in orderedPoints)
            {
                if (prev is { } p)
                {
                    var legDistance = Math.Sqrt(Math.Pow(point.X - p.X, 2) + Math.Pow(point.Y - p.Y, 2));
                    var legSeconds = speedMs > 0 ? legDistance / speedMs : 0;
                    if (partSeconds + legSeconds > budgetSeconds && waypoints.Count > 0 &&
                        waypoints[^1].MissionPart == missionPart)
                    {
                        waypoints.Add(new Waypoint(point.X, point.Y, altitudeM, missionPart, sequence++));
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
                waypoints.Add(new Waypoint(point.X, point.Y, altitudeM, missionPart, sequence++));
                prev = (point.X, point.Y);
            }

            var totalMinutes = speedMs > 0 ? totalDistanceM / speedMs / 60.0 : 0;
            return (waypoints, waypoints.Count == 0 ? 0 : missionPart, totalDistanceM, totalMinutes);
        }

        /// <summary>
        /// Generates a coverage flight plan for a winding, narrow linear feature (river, road,
        /// pipeline corridor) by flying parallel passes that each follow the given centerline's
        /// own curvature, offset sideways lane by lane - instead of the straight-line grid
        /// GenerateCoveragePlan uses, which can't fit a single global direction to a shape that
        /// bends back on itself (real report, 2026-08-15: a serpentine river-corridor polygon
        /// where even the best single direction still left one flagged leg). Lanes are found
        /// adaptively (keep adding lanes outward from the centerline until a full round on both
        /// sides comes up empty) instead of computing the corridor's width up front, so it
        /// naturally handles a width that varies along the corridor's length.
        /// </summary>
        /// <param name="centerline">The corridor's centerline, in order - e.g. a digitized river
        /// or road centerline. Densified internally at the along-track waypoint spacing.</param>
        /// <param name="outerRing">The actual survey/coverage boundary (a buffer around the
        /// centerline, or a hand-digitized corridor outline) - lanes are clipped to this, same
        /// as GenerateCoveragePlan's polygon.</param>
        public static Plan GenerateCorridorPlan(
            IReadOnlyList<(double X, double Y)> centerline,
            IReadOnlyList<(double X, double Y)> outerRing,
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> holes,
            double altitudeM, double gsdCmPerPx, int imageWidthPx, int imageHeightPx,
            double frontOverlapPct, double sideOverlapPct,
            double speedMs, double maxFlightMinutesPerBattery)
        {
            var gsdM = gsdCmPerPx / 100.0;
            var lineSpacingM = LineSpacingM(gsdCmPerPx, imageWidthPx, sideOverlapPct);
            var waypointSpacingM = Math.Max(0.5, gsdM * imageHeightPx * (1 - Math.Min(frontOverlapPct, 95) / 100.0));

            var allRings = new List<IReadOnlyList<(double X, double Y)>> { outerRing };
            allRings.AddRange(holes);

            var samples = ResampleCenterline(centerline, waypointSpacingM);
            if (samples.Count == 0)
                return new Plan(new List<Waypoint>(), 0, 0, 0);

            // One lane's worth of runs at a given perpendicular offset from the centerline -
            // split into contiguous in-polygon runs for the same reason GenerateSinglePassPlan
            // does: the corridor's width can pinch to nothing and widen again partway along its
            // length, and naively connecting across that gap would draw a chord through the
            // excluded area.
            List<List<(double X, double Y)>> LaneRuns(double offset)
            {
                var runs = new List<List<(double X, double Y)>>();
                List<(double X, double Y)> current = null;
                foreach (var (point, tangent) in samples)
                {
                    // Perpendicular to the tangent, rotated 90 deg: (dx,dy) -> (-dy,dx).
                    var px = point.X - tangent.Dy * offset;
                    var py = point.Y + tangent.Dx * offset;
                    if (PointInPolygon(px, py, allRings))
                    {
                        current ??= new List<(double X, double Y)>();
                        current.Add((px, py));
                    }
                    else if (current != null)
                    {
                        runs.Add(current);
                        current = null;
                    }
                }
                if (current != null) runs.Add(current);
                return runs;
            }

            var allRuns = new List<List<(double X, double Y)>>();
            allRuns.AddRange(LaneRuns(0)); // the centerline lane itself
            for (var lane = 1; lane <= 500; lane++) // hard cap against a runaway loop on bad input
            {
                var positive = LaneRuns(lane * lineSpacingM);
                var negative = LaneRuns(-lane * lineSpacingM);
                if (positive.Count == 0 && negative.Count == 0) break; // both sides empty - corridor's full width is covered
                allRuns.AddRange(positive);
                allRuns.AddRange(negative);
            }

            var (orderedPoints, offPolygonLegCount) = OrderRuns(allRuns, allRings);
            var (waypoints, missionPartCount, totalDistanceM, totalMinutes) =
                SplitIntoMissionParts(orderedPoints, altitudeM, speedMs, maxFlightMinutesPerBattery);
            return new Plan(waypoints, missionPartCount, totalDistanceM, totalMinutes, offPolygonLegCount);
        }

        // Walks the centerline at fixed arc-length steps, interpolating both the point and the
        // local (unit) tangent direction at each step - the tangent is what lets each lane's
        // offset follow the centerline's own curvature instead of a single global direction.
        private static List<((double X, double Y) Point, (double Dx, double Dy) Tangent)> ResampleCenterline(
            IReadOnlyList<(double X, double Y)> centerline, double stepM)
        {
            var points = new List<(double X, double Y)>();
            if (centerline.Count < 2)
                return new List<((double X, double Y), (double, double))>();

            var segLengths = new List<double>();
            double total = 0;
            for (var i = 0; i < centerline.Count - 1; i++)
            {
                var d = Math.Sqrt(Math.Pow(centerline[i + 1].X - centerline[i].X, 2) +
                                   Math.Pow(centerline[i + 1].Y - centerline[i].Y, 2));
                segLengths.Add(d);
                total += d;
            }
            if (total < 1e-6)
                return new List<((double X, double Y), (double, double))>();

            for (var dist = 0.0; dist <= total; dist += stepM)
            {
                var acc = 0.0;
                var segIdx = 0;
                while (segIdx < segLengths.Count - 1 && acc + segLengths[segIdx] < dist)
                {
                    acc += segLengths[segIdx];
                    segIdx++;
                }
                var segLen = segLengths[segIdx];
                var t = segLen > 1e-6 ? (dist - acc) / segLen : 0;
                var p0 = centerline[segIdx];
                var p1 = centerline[segIdx + 1];
                points.Add((p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t));
            }

            // Tangent from a few samples behind to a few samples ahead, not the raw
            // segment-to-segment direction - a real centerline vertex (a river bend, a road
            // corner) makes the raw direction jump discontinuously right at that point, which
            // makes each lane's sideways offset cut across the corridor's corner instead of
            // curving through it (real test, 2026-08-15: an L-shaped 90deg-bend corridor).
            // Averaging over a window smooths that jump into a gradual turn.
            const int window = 3;
            var result = new List<((double X, double Y), (double, double))>();
            for (var i = 0; i < points.Count; i++)
            {
                var behind = points[Math.Max(0, i - window)];
                var ahead = points[Math.Min(points.Count - 1, i + window)];
                var dx = ahead.X - behind.X;
                var dy = ahead.Y - behind.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-6) { dx /= len; dy /= len; } else { dx = 1; dy = 0; }
                result.Add((points[i], (dx, dy)));
            }
            return result;
        }

        // Shared by GenerateCoveragePlan, DescribeCoverageFailure and SuggestDirection - how far
        // apart adjacent flight lines need to be for the requested GSD/camera/side-overlap combo.
        // A driving parameter at/above 100% overlap gives zero/negative spacing (infinite lines)
        // - floor it well short of that instead of dividing by zero later.
        private static double LineSpacingM(double gsdCmPerPx, int imageWidthPx, double sideOverlapPct) =>
            Math.Max(0.5, (gsdCmPerPx / 100.0) * imageWidthPx * (1 - Math.Min(sideOverlapPct, 95) / 100.0));

        public record DirectionSuggestion(double BestDegrees, int AnglesTested, int LinesAtBest, int LinesAtCurrent);

        /// <summary>
        /// Suggests a flight direction (compass bearing, 0-179) that minimizes the number of
        /// coverage lines needed by aligning them with the survey polygon's own long axis,
        /// instead of the default 0 deg cutting across an elongated/irregular site and chopping
        /// coverage into many short, unevenly-lengthed zigzag columns. Genuinely tests every
        /// whole-degree angle against the polygon's real vertices (cheap - O(180 * ring size),
        /// so it finishes in milliseconds even though it's a full search, not a shortcut) and
        /// keeps the one needing the fewest lines at the requested line spacing. Returns the
        /// actual line counts at the best angle and at whatever direction was already set, so
        /// the caller can show concrete before/after numbers instead of just a bare angle.
        /// </summary>
        public static DirectionSuggestion SuggestDirection(
            IReadOnlyList<(double X, double Y)> outerRing,
            double gsdCmPerPx, int imageWidthPx, double sideOverlapPct, double currentDirectionDeg)
        {
            var lineSpacingM = LineSpacingM(gsdCmPerPx, imageWidthPx, sideOverlapPct);
            var pivotX = (outerRing.Min(p => p.X) + outerRing.Max(p => p.X)) / 2.0;
            var pivotY = (outerRing.Min(p => p.Y) + outerRing.Max(p => p.Y)) / 2.0;

            double WidthAtDeg(double deg)
            {
                var local = outerRing.Select(p => Rotate(p.X, p.Y, pivotX, pivotY, -deg));
                return local.Max(p => p.X) - local.Min(p => p.X);
            }
            int LinesFor(double widthM) => Math.Max(1, (int)Math.Ceiling(widthM / lineSpacingM));

            double bestDeg = 0, bestWidth = double.MaxValue;
            const int anglesTested = 180;
            for (var deg = 0; deg < anglesTested; deg++)
            {
                var width = WidthAtDeg(deg);
                if (width < bestWidth) { bestWidth = width; bestDeg = deg; }
            }

            var currentNormalized = ((currentDirectionDeg % 180) + 180) % 180;
            var linesAtCurrent = LinesFor(WidthAtDeg(currentNormalized));
            return new DirectionSuggestion(bestDeg, anglesTested, LinesFor(bestWidth), linesAtCurrent);
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
            var lineSpacingM = LineSpacingM(gsdCmPerPx, imageWidthPx, sideOverlapPct);
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
