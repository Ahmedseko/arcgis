using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TreeCounterAddin
{
    // Builds a DJI WPML template.kml - the file DJI Pilot 2 actually imports for waypoint
    // missions, for the enterprise drone lineup (Mavic 3 Enterprise, Matrice 30/300/350
    // series). Spec: https://developer.dji.com/doc/cloud-api-tutorial/en/api-reference/dji-wpml/
    // Deliberately produces only template.kml, not the paired waylines.wpml - DJI Pilot 2
    // auto-generates the execution file from the template on import (confirmed in DJI's own
    // docs), so shipping just the template halves the XML surface that has to match DJI's
    // schema exactly.
    //
    // Consumer drones (Mavic 3 Classic, Air/Mini series) run DJI Fly, which has no waypoint
    // mission import at all (confirmed against DJI's own support docs, 2026) - those users
    // need the Litchi CSV export instead (see ExportMissionCsvAsync/DescribeCoverageFailure's
    // sibling in TreeCounterDockpaneViewModel.FlightMissionPlanner.cs), since Litchi is the
    // one third-party flight app that both imports an external waypoint file *and* actually
    // supports the consumer DJI lineup.
    //
    // Pure string-building, zero ArcGIS reference (same pattern as FlightMissionMath.cs) so
    // it's testable from ForestryToolkit.MathTests without ArcGIS Pro installed - the ViewModel
    // only handles projecting coordinates to WGS84 and zipping the result into a .kmz.
    internal static class WpmlBuilder
    {
        // droneEnumValue/payloadEnumValue per DJI's published table (common-element.md in the
        // WPML spec) - these are required, drone-specific codes; get one wrong and DJI Pilot 2
        // rejects the whole import, so this list is deliberately restricted to the models DJI's
        // own docs confirm.
        public record DronePreset(string Label, int DroneEnumValue, int? DroneSubEnumValue, int PayloadEnumValue);

        public static readonly List<DronePreset> DronePresets = new()
        {
            new("Mavic 3 Enterprise (M3E)", 77, 0, 66),
            new("Mavic 3 Enterprise (M3T)", 77, 1, 67),
            new("Mavic 3 Enterprise (M3M)", 77, 2, 68),
            new("Matrice 3D", 91, 0, 80),
            new("Matrice 3TD", 91, 1, 81),
            new("Matrice 30", 67, 0, 52),
            new("Matrice 30T", 67, 1, 53),
            new("Matrice 300 RTK + Zenmuse H20", 60, null, 42),
            new("Matrice 300 RTK + Zenmuse H20T", 60, null, 43),
            new("Matrice 350 RTK + Zenmuse H20", 89, null, 42),
            new("Matrice 350 RTK + Zenmuse H20T", 89, null, 43),
        };

        public static string BuildTemplateKml(
            IReadOnlyList<(double Lat, double Lon, double AltitudeM)> waypoints,
            double speedMs, DronePreset drone)
        {
            if (waypoints.Count == 0)
                throw new ArgumentException("Need at least one waypoint.", nameof(waypoints));

            var speed = Math.Max(1, speedMs).ToString("F1", CultureInfo.InvariantCulture);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<kml xmlns=\"http://www.opengis.net/kml/2.2\" xmlns:wpml=\"http://www.dji.com/wpmz/1.0.2\">\n");
            sb.Append("<Document>\n");
            sb.Append("<wpml:author>ForestryToolkit</wpml:author>\n");
            sb.Append($"<wpml:createTime>{nowMs}</wpml:createTime>\n");
            sb.Append($"<wpml:updateTime>{nowMs}</wpml:updateTime>\n");
            sb.Append("<wpml:missionConfig>\n");
            sb.Append("<wpml:flyToWaylineMode>safely</wpml:flyToWaylineMode>\n");
            sb.Append("<wpml:finishAction>goHome</wpml:finishAction>\n");
            sb.Append("<wpml:exitOnRCLost>goContinue</wpml:exitOnRCLost>\n");
            sb.Append("<wpml:executeRCLostAction>hover</wpml:executeRCLostAction>\n");
            // Fixed conservative default, same as DJI's own reference example - review/adjust
            // this in DJI Pilot 2 itself before flying, same as every other imported mission.
            sb.Append("<wpml:takeOffSecurityHeight>20</wpml:takeOffSecurityHeight>\n");
            sb.Append($"<wpml:globalTransitionalSpeed>{speed}</wpml:globalTransitionalSpeed>\n");
            sb.Append("<wpml:droneInfo>\n");
            sb.Append($"<wpml:droneEnumValue>{drone.DroneEnumValue}</wpml:droneEnumValue>\n");
            if (drone.DroneSubEnumValue is { } sub)
                sb.Append($"<wpml:droneSubEnumValue>{sub}</wpml:droneSubEnumValue>\n");
            sb.Append("</wpml:droneInfo>\n");
            sb.Append("<wpml:payloadInfo>\n");
            sb.Append($"<wpml:payloadEnumValue>{drone.PayloadEnumValue}</wpml:payloadEnumValue>\n");
            sb.Append("<wpml:payloadPositionIndex>0</wpml:payloadPositionIndex>\n");
            sb.Append("</wpml:payloadInfo>\n");
            sb.Append("</wpml:missionConfig>\n");
            sb.Append("<Folder>\n");
            sb.Append("<wpml:templateType>waypoint</wpml:templateType>\n");
            sb.Append("<wpml:templateId>0</wpml:templateId>\n");
            sb.Append("<wpml:waylineCoordinateSysParam>\n");
            sb.Append("<wpml:coordinateMode>WGS84</wpml:coordinateMode>\n");
            // relativeToStartPoint (AGL from the takeoff point), not EGM96/absolute - this
            // toolkit's altitude input is a constant height above ground/takeoff, not an
            // absolute geoid elevation (no DEM/terrain source feeds this).
            sb.Append("<wpml:heightMode>relativeToStartPoint</wpml:heightMode>\n");
            sb.Append("<wpml:positioningType>GPS</wpml:positioningType>\n");
            sb.Append("</wpml:waylineCoordinateSysParam>\n");
            sb.Append($"<wpml:autoFlightSpeed>{speed}</wpml:autoFlightSpeed>\n");
            sb.Append("<wpml:gimbalPitchMode>manual</wpml:gimbalPitchMode>\n");
            sb.Append("<wpml:globalWaypointTurnMode>toPointAndStopWithDiscontinuityCurvature</wpml:globalWaypointTurnMode>\n");
            sb.Append("<wpml:globalUseStraightLine>0</wpml:globalUseStraightLine>\n");

            for (var i = 0; i < waypoints.Count; i++)
            {
                var (lat, lon, altitudeM) = waypoints[i];
                var lonS = lon.ToString("F7", CultureInfo.InvariantCulture);
                var latS = lat.ToString("F7", CultureInfo.InvariantCulture);
                var altS = altitudeM.ToString("F1", CultureInfo.InvariantCulture);
                sb.Append("<Placemark>\n");
                sb.Append($"<Point><coordinates>{lonS},{latS}</coordinates></Point>\n");
                sb.Append($"<wpml:index>{i}</wpml:index>\n");
                sb.Append($"<wpml:ellipsoidHeight>{altS}</wpml:ellipsoidHeight>\n");
                sb.Append($"<wpml:height>{altS}</wpml:height>\n");
                sb.Append("<wpml:useGlobalHeight>0</wpml:useGlobalHeight>\n");
                sb.Append("<wpml:useGlobalSpeed>1</wpml:useGlobalSpeed>\n");
                sb.Append("<wpml:useGlobalHeadingParam>1</wpml:useGlobalHeadingParam>\n");
                sb.Append("<wpml:useGlobalTurnParam>1</wpml:useGlobalTurnParam>\n");
                sb.Append("</Placemark>\n");
            }

            sb.Append("</Folder>\n</Document>\n</kml>\n");
            return sb.ToString();
        }
    }
}
