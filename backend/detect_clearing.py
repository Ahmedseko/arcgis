"""
Land clearing (bukaan lahan) detection CLI - see land_clearing.py for the algorithm.

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), same as detect.py.

Called by TreeCounterAddin/PythonBackendService.cs as:
    python detect_clearing.py --raster <path> --output-fc <feature class path> \
        --summary <json path> [--exg-threshold N] [--smooth-px N] [--min-area-m2 N] \
        [--exclude-fc <polygon feature class to erase, e.g. already-harvested area>]

Writes the detected clearing polygons to --output-fc and a JSON summary to --summary:
    {"polygon_count": int, "output_fc": str, "area_ha": float}
"""
import argparse
import json
import sys

import arcpy

from land_clearing import detect_land_clearing, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raster", required=True)
    parser.add_argument("--output-fc", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--exg-threshold", type=float, default=DEFAULT_EXG_THRESHOLD)
    parser.add_argument("--smooth-px", type=float, default=DEFAULT_SMOOTH_PX)
    parser.add_argument("--min-area-m2", type=float, default=100.0)
    parser.add_argument("--exclude-fc", default=None,
                         help="Polygon feature class to erase from results (e.g. already-harvested area)")
    args = parser.parse_args()

    try:
        if not arcpy.Exists(args.raster):
            raise FileNotFoundError(f"Raster not found: {args.raster}")

        gdb = args.output_fc.rsplit("\\", 1)[0] if "\\" in args.output_fc else args.output_fc.rsplit("/", 1)[0]
        mask_raster = f"{gdb}\\LandClearingMask_tmp"
        raw_fc = f"{gdb}\\LandClearingRaw_tmp"

        print("STAGE Scanning for cleared/bare ground...", flush=True)
        detect_land_clearing(
            args.raster, mask_raster,
            exg_threshold=args.exg_threshold, smooth_px=args.smooth_px,
            progress_cb=lambda p: print(f"PROGRESS {p}", flush=True),
        )

        print("STAGE Vectorizing cleared areas...", flush=True)
        # NoData everywhere except the cleared class (see land_clearing.py) - every
        # polygon RasterToPolygon produces here is already a clearing, nothing else to
        # filter out of the raw output by class/value.
        # SIMPLIFY (not NO_SIMPLIFY) - NO_SIMPLIFY traces every raster cell's exact
        # boundary, which at ~0.06 m/px produces a visibly "staircase"/pixel-jagged
        # polygon edge instead of a smooth, human-digitization-like boundary (confirmed
        # against a real result, 2026-07-31).
        arcpy.conversion.RasterToPolygon(mask_raster, raw_fc, "SIMPLIFY", "Value")
        arcpy.management.Delete(mask_raster)
        print("PROGRESS 92", flush=True)

        # Drops tiny slivers (a single stray bare pixel patch, stitching artifacts) below
        # the requested minimum area - deleted in place from the selection rather than
        # copying the "keep" set out to yet another temp feature class.
        arcpy.management.MakeFeatureLayer(raw_fc, "cleared_lyr")
        arcpy.management.SelectLayerByAttribute("cleared_lyr", "NEW_SELECTION", f"Shape_Area < {args.min_area_m2}")
        if int(arcpy.management.GetCount("cleared_lyr")[0]) > 0:
            arcpy.management.DeleteFeatures("cleared_lyr")
        arcpy.management.SelectLayerByAttribute("cleared_lyr", "CLEAR_SELECTION")

        if args.exclude_fc:
            print("STAGE Excluding already-cleared area...", flush=True)
            arcpy.analysis.PairwiseErase(raw_fc, args.exclude_fc, args.output_fc)
            arcpy.management.Delete(raw_fc)
        else:
            arcpy.management.Rename(raw_fc, args.output_fc)

        count = int(arcpy.management.GetCount(args.output_fc)[0])
        total_area_m2 = 0.0
        with arcpy.da.SearchCursor(args.output_fc, ["SHAPE@AREA"]) as cursor:
            for (area,) in cursor:
                total_area_m2 += area
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    with open(args.summary, "w", encoding="utf-8") as f:
        json.dump({"polygon_count": count, "output_fc": args.output_fc, "area_ha": total_area_m2 / 10000.0}, f)
    print("PROGRESS 100", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
