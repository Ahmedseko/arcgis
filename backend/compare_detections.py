"""
Compare Changes CLI - change detection between two Tree Detection runs of the same
site over time. The matching itself (greedy nearest-neighbor via scipy's cKDTree) is
detector.py's compare_detections(), already ported from the QGIS plugin - this just
reads two point feature classes, calls it, and writes the unmatched points back out
as two point feature classes (old points with no match in the new run = likely
felled/lost; new points with no match in the old run = likely new/regrowth/missed
before).

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), same as detect.py:
    python compare_detections.py --old-fc <path> --new-fc <path> \
        --output-lost-fc <path> --output-new-fc <path> --summary <json path> \
        [--max-dist-m N]
"""
import argparse
import json
import sys

import arcpy

from detector import compare_detections

# A detection point re-run on the same tree rarely lands exactly on the old pixel
# (matched-filter/YOLO centroid jitter, orthomosaic alignment drift) - 3m is comfortably
# under typical tree spacing while covering that jitter. Not swept against ground truth
# like land_clearing.py's morphology constants; tune per-site if matches look wrong.
DEFAULT_MAX_DIST_M = 3.0


def _read_points(fc, sr):
    with arcpy.da.SearchCursor(fc, ["SHAPE@XY"], spatial_reference=sr) as cursor:
        return [row[0] for row in cursor]


def _write_points(fc, points, sr):
    if arcpy.Exists(fc):
        arcpy.management.Delete(fc)
    gdb, name = fc.rsplit("\\", 1)
    arcpy.management.CreateFeatureclass(gdb, name, "POINT", spatial_reference=sr)
    with arcpy.da.InsertCursor(fc, ["SHAPE@XY"]) as cursor:
        for xy in points:
            cursor.insertRow([xy])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--old-fc", required=True)
    parser.add_argument("--new-fc", required=True)
    parser.add_argument("--output-lost-fc", required=True)
    parser.add_argument("--output-new-fc", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--max-dist-m", type=float, default=DEFAULT_MAX_DIST_M)
    args = parser.parse_args()

    try:
        if not arcpy.Exists(args.old_fc):
            raise FileNotFoundError(f"Old detection layer not found: {args.old_fc}")
        if not arcpy.Exists(args.new_fc):
            raise FileNotFoundError(f"New detection layer not found: {args.new_fc}")

        # Reproject on read (spatial_reference= on SearchCursor) rather than assuming
        # both runs share a CRS - compare_detections needs both point sets in the same
        # units (meters) to measure max_dist_m correctly.
        sr = arcpy.Describe(args.old_fc).spatialReference
        old_points = _read_points(args.old_fc, sr)
        new_points = _read_points(args.new_fc, sr)

        result = compare_detections(old_points, new_points, args.max_dist_m)

        _write_points(args.output_lost_fc, result["lost"], sr)
        _write_points(args.output_new_fc, result["new"], sr)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    with open(args.summary, "w", encoding="utf-8") as f:
        json.dump({
            "lost_count": len(result["lost"]),
            "new_count": len(result["new"]),
            "matched_count": result["matched"],
            "lost_fc": args.output_lost_fc,
            "new_fc": args.output_new_fc,
        }, f)
    return 0


if __name__ == "__main__":
    sys.exit(main())
