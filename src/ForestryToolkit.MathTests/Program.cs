using System;
using System.Xml.Linq;
using TreeCounterAddin;

// Plain self-check, no test framework - run with `dotnet run` from this folder. Exits
// non-zero if any check fails, so it's usable in a script/CI step later without needing
// ArcGIS Pro installed (these two math classes have zero ArcGIS dependency).
var failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    if (!condition) failures++;
}

bool Close(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

// --- SliverMath.Thinness ---
// A circle's thinness is exactly 1 regardless of radius (4*pi*A / P^2 with A=pi*r^2, P=2*pi*r).
var circleArea = Math.PI * 10 * 10;
var circlePerimeter = 2 * Math.PI * 10;
Check("Thinness(circle) ~= 1.0", Close(SliverMath.Thinness(circleArea, circlePerimeter), 1.0, 1e-9));

// A long thin rectangle (100 x 1) should read as clearly non-circular.
var thinRectThinness = SliverMath.Thinness(area: 100, perimeter: 2 * (100 + 1));
Check("Thinness(100x1 rect) < 0.1", thinRectThinness < 0.1);

// Degenerate ring (zero/negative perimeter) is treated as maximally thin, not a divide-by-zero.
Check("Thinness(perimeter<=0) == 0", SliverMath.Thinness(area: 5, perimeter: 0) == 0);

// --- SliverMath.Median ---
Check("Median(odd count) == middle value", SliverMath.Median(new[] { 3.0, 1.0, 2.0 }) == 2.0);
Check("Median(even count) == average of middle two", SliverMath.Median(new[] { 1.0, 2.0, 3.0, 4.0 }) == 2.5);
Check("Median(single value) == that value", SliverMath.Median(new[] { 7.0 }) == 7.0);

// --- BiomassMath.Estimate ---
// Hand-calculated expectation: AGB = 10*600*1.5 = 9000; total = 9000*1.37 = 12330;
// carbon = 12330*0.47 = 5795.1; CO2e = 5795.1*3.667 ~= 21250.63.
var (biomassKg, carbonKg, co2eKg) = BiomassMath.Estimate(
    totalVolumeM3: 10, woodDensityKgM3: 600, biomassExpansionFactor: 1.5,
    rootShootRatio: 0.37, carbonFraction: 0.47);
Check("Biomass(10 m3 @ defaults) == 12330 kg", Close(biomassKg, 12330, 0.01));
Check("Carbon(10 m3 @ defaults) == 5795.1 kg", Close(carbonKg, 5795.1, 0.01));
Check("CO2e(10 m3 @ defaults) ~= 21250.63 kg", Close(co2eKg, 21250.6317, 0.01));

// Zero volume must not produce NaN/negative output from any of the multipliers.
var (zeroBiomass, zeroCarbon, zeroCo2e) = BiomassMath.Estimate(0, 600, 1.5, 0.37, 0.47);
Check("Biomass(0 m3) == 0", zeroBiomass == 0 && zeroCarbon == 0 && zeroCo2e == 0);

// --- FlightMissionMath.PointInPolygon ---
var square = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
Check("PointInPolygon: center of square is inside", FlightMissionMath.PointInPolygon(5, 5, new[] { square }));
Check("PointInPolygon: far outside point is outside", !FlightMissionMath.PointInPolygon(50, 50, new[] { square }));
var hole = new List<(double X, double Y)> { (3, 3), (7, 3), (7, 7), (3, 7) };
Check("PointInPolygon: point inside a hole reads as outside", !FlightMissionMath.PointInPolygon(5, 5, new[] { square, hole }));
Check("PointInPolygon: point outside the hole but inside the ring still reads as inside", FlightMissionMath.PointInPolygon(1, 1, new[] { square, hole }));

// --- FlightMissionMath.Rotate ---
// Rotating (1,0) by 90 degrees around the origin should land on (0,1) (within FP tolerance).
var (rx, ry) = FlightMissionMath.Rotate(1, 0, 0, 0, 90);
Check("Rotate((1,0), 90deg) ~= (0,1)", Close(rx, 0, 1e-9) && Close(ry, 1, 1e-9));
// Rotate then rotate back by the same angle (negated) must return to the original point.
var (bx, by) = FlightMissionMath.Rotate(rx, ry, 0, 0, -90);
Check("Rotate then inverse-rotate returns to start", Close(bx, 1, 1e-9) && Close(by, 0, 1e-9));

// --- FlightMissionMath.GenerateCoveragePlan ---
// 100x100m square, GSD/image size chosen so line spacing = waypoint spacing = 10m (footprint
// 20x20m at 50% overlap both ways) - makes the expected waypoint count easy to hand-check.
var surveyArea = new List<(double X, double Y)> { (0, 0), (100, 0), (100, 100), (0, 100) };
var plan = FlightMissionMath.GenerateCoveragePlan(
    surveyArea, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 50, gsdCmPerPx: 2, imageWidthPx: 1000, imageHeightPx: 1000,
    frontOverlapPct: 50, sideOverlapPct: 50, flightDirectionDeg: 0,
    speedMs: 8, maxFlightMinutesPerBattery: 20);
Check("GenerateCoveragePlan: produces waypoints", plan.Waypoints.Count > 0);
Check("GenerateCoveragePlan: every waypoint keeps the requested altitude",
    plan.Waypoints.All(w => w.Altitude == 50));
Check("GenerateCoveragePlan: sequence restarts at 0 for every mission part",
    plan.Waypoints.Where(w => w.Sequence == 0).Select(w => w.MissionPart).Distinct().Count() == plan.MissionPartCount);
Check("GenerateCoveragePlan: roughly 10 lines x 11 waypoints for a 100x100m square at 10m spacing",
    plan.Waypoints.Count is >= 90 and <= 121);

// Real report (2026-08-14): a coverage column stopped at whatever fixed-grid waypoint-spacing
// multiple happened to fall inside the polygon, leaving a visible gap between the flat-cut end
// of the column and a diagonal/tapered polygon edge above it, instead of following the edge.
// Right triangle (0,0)-(100,0)-(100,100): the diagonal hypotenuse means each column's true top
// boundary is y=x, not a multiple of the 10m waypoint spacing - the topmost waypoint on any
// column should land within a couple meters of that true boundary (the small edge-inset safety
// margin), not stop up to a full waypoint-spacing (10m) short of it.
var triangleArea = new List<(double X, double Y)> { (0, 0), (100, 0), (100, 100) };
var trianglePlan = FlightMissionMath.GenerateCoveragePlan(
    triangleArea, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 50, gsdCmPerPx: 2, imageWidthPx: 1000, imageHeightPx: 1000,
    frontOverlapPct: 50, sideOverlapPct: 50, flightDirectionDeg: 0,
    speedMs: 8, maxFlightMinutesPerBattery: 20);
var worstEdgeGap = trianglePlan.Waypoints
    .GroupBy(w => Math.Round(w.X, 3))
    .Select(g => g.Key - g.Max(w => w.Y)) // true boundary (y=x) minus the topmost waypoint's Y
    .Max();
Check("GenerateCoveragePlan: coverage columns follow a diagonal edge instead of stopping short in a flat cut",
    worstEdgeGap <= 3.0);

// A real result (2026-08-14): a small survey polygon (narrower than one line's worth of
// spacing) produced zero waypoints - the line-position loop ran zero times and there was
// no fallback. This is that exact scenario reproduced: a 20x20m polygon with the same
// settings shown in that report (5cm/px GSD, 4000px image width, 70% side overlap ->
// 60m line spacing, wider than the whole polygon).
var smallArea = new List<(double X, double Y)> { (0, 0), (20, 0), (20, 20), (0, 20) };
var smallPlan = FlightMissionMath.GenerateCoveragePlan(
    smallArea, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 100, gsdCmPerPx: 5, imageWidthPx: 4000, imageHeightPx: 3000,
    frontOverlapPct: 80, sideOverlapPct: 70, flightDirectionDeg: 0,
    speedMs: 8, maxFlightMinutesPerBattery: 20);
Check("GenerateCoveragePlan: a survey area smaller than the line spacing still gets waypoints",
    smallPlan.Waypoints.Count > 0);

// A real report (2026-08-14): the *actual* zero-waypoint cause turned out to be a
// multi-feature layer where the wrong (tiny) feature got picked, not a genuinely too-small
// single polygon - but the user asked for the tool to explain itself either way. This checks
// the diagnostic message names real numbers instead of a generic "check your settings".
var failureMessage = FlightMissionMath.DescribeCoverageFailure(
    smallArea, gsdCmPerPx: 5, imageWidthPx: 4000, imageHeightPx: 3000,
    frontOverlapPct: 80, sideOverlapPct: 70, flightDirectionDeg: 0);
Check("DescribeCoverageFailure: names the computed line spacing for a too-small area",
    failureMessage.Contains("60m"));
Check("DescribeCoverageFailure: names the area's own size",
    failureMessage.Contains("20m"));

// --- FlightMissionMath.SuggestDirection ---
// Real report (2026-08-14, "drone flight path"): an elongated, irregular 2844x804m site
// running roughly east-west was flown with the default 0deg (north-south lines), chopping
// coverage into ~47 short, unevenly-lengthed zigzag columns with steep diagonal jumps between
// them. This is that exact polygon - the suggestion should land close to the ~92deg that
// actually fits its long axis, not near the default 0.
var realSite = new List<(double X, double Y)>
{
    (254174.35, 9642745.51), (254215.05, 9642254.52), (253947.93, 9642137.49),
    (253177.10, 9641941.60), (251655.78, 9642028.10), (251370.85, 9642460.58),
    (251416.64, 9642661.56), (253146.57, 9642521.64),
};
var suggestion = FlightMissionMath.SuggestDirection(
    realSite, gsdCmPerPx: 5, imageWidthPx: 4000, sideOverlapPct: 70, currentDirectionDeg: 0);
Check("SuggestDirection: aligns an east-west elongated site near 90deg, not near 0",
    suggestion.BestDegrees is >= 80 and <= 100);
Check("SuggestDirection: reports genuinely fewer lines at the best angle than at the previous 0deg",
    suggestion.LinesAtBest < suggestion.LinesAtCurrent);
Check("SuggestDirection: tested all 180 candidate angles",
    suggestion.AnglesTested == 180);

// --- GenerateCoveragePlan on a concave polygon ---
// This site has one reflex (concave) vertex (a river-bend notch), which makes several lines
// cross out of the polygon and back in depending on flight direction. Two real reports,
// 2026-08-14: at 0deg one of the short leftover segments looked "disconnected" (reached via a
// long out-of-sequence jump under the old rigid line/pass ordering); at 90deg a leg cut
// straight outside the polygon through the notch. Greedy nearest-neighbor ordering (always
// walk to whichever unvisited run's nearer end is physically closest) fixes both at these
// exact angles - 92deg keeps a narrower residual (a true general fix needs real obstacle-aware
// path planning, out of scope here), so that one's checked separately with a looser bound.
foreach (var deg in new[] { 0.0, 90.0 })
{
    var checkPlan = FlightMissionMath.GenerateCoveragePlan(
        realSite, new List<IReadOnlyList<(double X, double Y)>>(),
        altitudeM: 100, gsdCmPerPx: 5, imageWidthPx: 4000, imageHeightPx: 3000,
        frontOverlapPct: 80, sideOverlapPct: 70, flightDirectionDeg: deg,
        speedMs: 8, maxFlightMinutesPerBattery: 20);
    Check($"GenerateCoveragePlan: nearest-neighbor ordering keeps every leg inside a concave polygon at {deg}deg",
        checkPlan.OffPolygonLegCount == 0);
}
var concavePlan = FlightMissionMath.GenerateCoveragePlan(
    realSite, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 100, gsdCmPerPx: 5, imageWidthPx: 4000, imageHeightPx: 3000,
    frontOverlapPct: 80, sideOverlapPct: 70, flightDirectionDeg: 92,
    speedMs: 8, maxFlightMinutesPerBattery: 20);
Check("GenerateCoveragePlan: multi-start nearest-neighbor keeps every leg inside a concave polygon at 92deg",
    concavePlan.OffPolygonLegCount == 0);

// A short battery budget on the same site must split the single-battery plan into more parts.
var splitPlan = FlightMissionMath.GenerateCoveragePlan(
    surveyArea, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 50, gsdCmPerPx: 2, imageWidthPx: 1000, imageHeightPx: 1000,
    frontOverlapPct: 50, sideOverlapPct: 50, flightDirectionDeg: 0,
    speedMs: 8, maxFlightMinutesPerBattery: 1);
Check("GenerateCoveragePlan: a tight battery budget splits into multiple mission parts",
    splitPlan.MissionPartCount > plan.MissionPartCount);

// A part boundary should share an exact coordinate (a "seam" waypoint) between the closing
// waypoint of the part that's ending and the opening waypoint of the next one, so the next
// battery's takeoff lines up exactly with where the previous one left off, instead of each part
// just picking up wherever the sequence happened to land.
var part1Last = splitPlan.Waypoints.Last(w => w.MissionPart == 1);
var part2First = splitPlan.Waypoints.First(w => w.MissionPart == 2);
Check("GenerateCoveragePlan: consecutive mission parts share a seam waypoint",
    Math.Abs(part1Last.X - part2First.X) < 1e-6 && Math.Abs(part1Last.Y - part2First.Y) < 1e-6);

// --- GenerateCoveragePlan cross-hatch mode ---
// A second pass at +90deg, appended as further mission parts - roughly doubles waypoints/parts
// and the two passes should actually run perpendicular to each other, not just be a duplicate.
var crossHatchPlan = FlightMissionMath.GenerateCoveragePlan(
    surveyArea, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 50, gsdCmPerPx: 2, imageWidthPx: 1000, imageHeightPx: 1000,
    frontOverlapPct: 50, sideOverlapPct: 50, flightDirectionDeg: 0,
    speedMs: 8, maxFlightMinutesPerBattery: 20, crossHatch: true);
Check("GenerateCoveragePlan: cross-hatch roughly doubles the mission parts",
    crossHatchPlan.MissionPartCount == plan.MissionPartCount * 2);
Check("GenerateCoveragePlan: cross-hatch roughly doubles the waypoint count",
    crossHatchPlan.Waypoints.Count > plan.Waypoints.Count * 1.8);
Check("GenerateCoveragePlan: cross-hatch's second pass parts continue the numbering, not restart at 1",
    crossHatchPlan.Waypoints.Any(w => w.MissionPart == plan.MissionPartCount + 1));

// --- FlightMissionMath.GenerateCorridorPlan ---
// Real report (2026-08-15): a serpentine river-corridor polygon exposed that a single global
// flight direction (however well-optimized) can't fit a shape that bends back on itself. This
// is a simpler L-shaped corridor (a 90deg bend, not a full serpentine, but the same "direction
// changes along the length" property) - a 40m-wide corridor around a centerline going
// (0,0)->(0,100)->(100,100), hand-built as an L-shaped polygon rather than a real buffer.
var corridorCenterline = new List<(double X, double Y)> { (0, 0), (0, 100), (100, 100) };
var corridorPolygon = new List<(double X, double Y)>
{
    (-20, 0), (-20, 120), (100, 120), (100, 80), (20, 80), (20, 0),
};
var corridorPlan = FlightMissionMath.GenerateCorridorPlan(
    corridorCenterline, corridorPolygon, new List<IReadOnlyList<(double X, double Y)>>(),
    altitudeM: 50, gsdCmPerPx: 2, imageWidthPx: 1000, imageHeightPx: 1000,
    frontOverlapPct: 50, sideOverlapPct: 50, speedMs: 8, maxFlightMinutesPerBattery: 20);
Check("GenerateCorridorPlan: produces waypoints for an L-shaped corridor",
    corridorPlan.Waypoints.Count > 0);
Check("GenerateCorridorPlan: keeps every leg inside the corridor polygon despite the 90deg bend",
    corridorPlan.OffPolygonLegCount == 0);
Check("GenerateCorridorPlan: covers both arms of the corridor, not just the start",
    corridorPlan.Waypoints.Any(w => w.Y < 20) && corridorPlan.Waypoints.Any(w => w.X > 80));

// --- WpmlBuilder.BuildTemplateKml ---
// Real-world risk here isn't a math bug, it's a malformed/incomplete XML that DJI Pilot 2
// silently rejects - the one check worth having is "does this actually parse, and does it
// carry the drone-specific codes that make or break the import".
var m30 = WpmlBuilder.DronePresets.First(p => p.Label == "Matrice 30");
var wpmlXml = WpmlBuilder.BuildTemplateKml(
    new List<(double Lat, double Lon, double AltitudeM)> { (-6.2, 106.8, 100), (-6.201, 106.801, 100) },
    speedMs: 8, m30);
XDocument parsedKml = null;
try { parsedKml = XDocument.Parse(wpmlXml); } catch { /* leave null, Check below fails */ }
Check("BuildTemplateKml: produces well-formed XML", parsedKml != null);
Check("BuildTemplateKml: one Placemark per waypoint",
    parsedKml?.Descendants().Count(e => e.Name.LocalName == "Placemark") == 2);
Check("BuildTemplateKml: carries the selected drone's enum codes",
    wpmlXml.Contains("<wpml:droneEnumValue>67</wpml:droneEnumValue>") &&
    wpmlXml.Contains("<wpml:payloadEnumValue>52</wpml:payloadEnumValue>"));
Check("BuildTemplateKml: fires a takePhoto action group at every waypoint",
    parsedKml?.Descendants().Count(e => e.Name.LocalName == "actionGroup") == 2 &&
    parsedKml?.Descendants().Count(e => e.Name.LocalName == "actionActuatorFunc" && e.Value == "takePhoto") == 2);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;
