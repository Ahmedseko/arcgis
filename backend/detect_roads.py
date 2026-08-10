"""
Road/trail centerline extraction CLI - see road_extraction.py for the algorithm.

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), same as detect_clearing.py.

Called by TreeCounterAddin/PythonBackendService.cs as:
    python detect_roads.py --raster <path> --output-fc <polyline feature class path> \
        --summary <json path> [--exg-threshold N] [--smooth-px N] [--min-dangle-m N]

Writes the extracted road/trail centerlines to --output-fc and a JSON summary to
--summary: {"line_count": int, "output_fc": str, "length_km": float}
"""
import argparse
import json
import sys

import arcpy

from land_clearing import DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX
from road_extraction import extract_road_skeleton_raster, PRUNE_LENGTH_M, MAX_ROAD_WIDTH_M


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raster", required=True)
    parser.add_argument("--output-fc", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--exg-threshold", type=float, default=DEFAULT_EXG_THRESHOLD)
    parser.add_argument("--smooth-px", type=float, default=DEFAULT_SMOOTH_PX)
    parser.add_argument("--min-dangle-m", type=float, default=5.0,
                         help="Drop dangling line stubs shorter than this (skeletonize noise)")
    parser.add_argument("--prune-length-m", type=float, default=PRUNE_LENGTH_M,
                         help="Erode skeleton spurs/specks shorter than this before vectorizing (see road_extraction.py)")
    parser.add_argument("--max-width-m", type=float, default=MAX_ROAD_WIDTH_M,
                         help="Drop bare-ground regions wider than this (quarry pits, wide cleared yards - not roads; see road_extraction.py). 0 disables.")
    args = parser.parse_args()

    try:
        if not arcpy.Exists(args.raster):
            raise FileNotFoundError(f"Raster not found: {args.raster}")

        gdb = args.output_fc.rsplit("\\", 1)[0] if "\\" in args.output_fc else args.output_fc.rsplit("/", 1)[0]
        skel_raster = f"{gdb}\\RoadSkeleton_tmp"

        print("STAGE Scanning for road/trail centerlines...", flush=True)
        extract_road_skeleton_raster(
            args.raster, skel_raster, exg_threshold=args.exg_threshold, smooth_px=args.smooth_px,
            prune_length_m=args.prune_length_m, max_width_m=args.max_width_m,
            progress_cb=lambda p: print(f"PROGRESS {p}", flush=True))
        print("PROGRESS 96", flush=True)

        print("STAGE Vectorizing centerlines...", flush=True)
        # ZERO background (default) - skeleton is 0/1 with 0 as NoData already (see
        # value_to_nodata above), SIMPLIFY for the same reason detect_clearing.py uses it
        # on RasterToPolygon: NO_SIMPLIFY traces the exact pixel staircase at ~0.05m/px.
        arcpy.conversion.RasterToPolyline(skel_raster, args.output_fc, "ZERO", args.min_dangle_m, "SIMPLIFY")
        arcpy.management.Delete(skel_raster)

        count = int(arcpy.management.GetCount(args.output_fc)[0])
        total_length_m = 0.0
        with arcpy.da.SearchCursor(args.output_fc, ["SHAPE@LENGTH"]) as cursor:
            for (length,) in cursor:
                total_length_m += length
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    with open(args.summary, "w", encoding="utf-8") as f:
        json.dump({"line_count": count, "output_fc": args.output_fc, "length_km": total_length_m / 1000.0}, f)
    print("PROGRESS 100", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
