"""
Land clearing (bukaan lahan) detection CLI - see land_clearing.py for the algorithm.

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), same as detect.py.

Called by TreeCounterAddin/PythonBackendService.cs as:
    python detect_clearing.py --raster <path> --output-fc <feature class path> \
        --summary <json path> [--exg-threshold N] [--smooth-px N] [--method exg|obia] \
        [--fresh-color] [--bright-min N] [--min-area-m2 N] [--fill-hole-area-m2 N] \
        [--exclude-fc <polygon feature class to erase, e.g. already-harvested area>]

Writes the detected clearing polygons to --output-fc and a JSON summary to --summary:
    {"polygon_count": int, "output_fc": str, "area_ha": float}
"""
import argparse
import json
import sys

import arcpy

from land_clearing import (
    detect_land_clearing, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX,
    OPENING_ITERATIONS, CLOSING_ITERATIONS,
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raster", required=True)
    parser.add_argument("--output-fc", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--exg-threshold", type=float, default=DEFAULT_EXG_THRESHOLD)
    parser.add_argument("--smooth-px", type=float, default=DEFAULT_SMOOTH_PX)
    parser.add_argument("--opening-iterations", type=int, default=OPENING_ITERATIONS,
                         help="Erosion pass that strips small false 'cleared' specks - lower keeps narrower real clearings from being eroded away")
    parser.add_argument("--closing-iterations", type=int, default=CLOSING_ITERATIONS,
                         help="Dilation pass that fills small gaps/merges nearby fragments - higher gives smoother, more human-digitization-like boundaries")
    parser.add_argument("--method", choices=["exg", "obia"], default="exg",
                         help="obia = SLIC-superpixel prototype, see land_clearing.py")
    parser.add_argument("--fresh-color", action="store_true",
                         help="Also require bright+reddish RGB (filters roads/rivers) - see land_clearing.py")
    parser.add_argument("--bright-min", type=float, default=120.0)
    parser.add_argument("--min-area-m2", type=float, default=100.0)
    parser.add_argument("--fill-hole-area-m2", type=float, default=2000.0,
                         help="Fill interior holes (patches of vegetation inside a cleared polygon) smaller than "
                              "this, so the result is one solid polygon like manual digitization instead of "
                              "holed-through - large real forest islands are left alone. 0 = don't fill. Matches "
                              "the original QGIS plugin's 'Isi lubang' default (2000), which this ArcGIS port had "
                              "been missing entirely (real report, 2026-08-16) - the port instead leaned on "
                              "heavier pixel-level opening/closing, which measurably helps on the one site it was "
                              "tuned against but doesn't reproduce the same solid, hole-free look this achieves "
                              "at the vector level regardless of site.")
    parser.add_argument("--exclude-fc", default=None,
                         help="Polygon feature class to erase from results (e.g. already-harvested area)")
    args = parser.parse_args()

    try:
        if not arcpy.Exists(args.raster):
            raise FileNotFoundError(f"Raster not found: {args.raster}")

        gdb = args.output_fc.rsplit("\\", 1)[0] if "\\" in args.output_fc else args.output_fc.rsplit("/", 1)[0]
        mask_raster = f"{gdb}\\LandClearingMask_tmp"
        raw_fc = f"{gdb}\\LandClearingRaw_tmp"
        holefilled_fc = f"{gdb}\\LandClearingHoleFilled_tmp"
        smoothed_fc = f"{gdb}\\LandClearingSmoothed_tmp"

        print("STAGE Scanning for cleared/bare ground...", flush=True)
        detect_land_clearing(
            args.raster, mask_raster,
            exg_threshold=args.exg_threshold, smooth_px=args.smooth_px, method=args.method,
            fresh_color=args.fresh_color, bright_min=args.bright_min,
            opening_iterations=args.opening_iterations, closing_iterations=args.closing_iterations,
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
        print("PROGRESS 80", flush=True)

        # Fill small interior holes (patches of vegetation the pixel-level detection left
        # standing inside an otherwise-cleared area) - a plain pixel threshold+morphology
        # pipeline can't cheaply do this the way a human digitizer naturally would (ignore
        # small gaps, keep real forest islands), but at the vector level it's exactly what
        # EliminatePolygonPart's CONTAINED_ONLY mode does: only touches holes actually
        # inside another part of the SAME feature, never a separate small polygon nearby
        # (that's what --min-area-m2 below is for instead).
        current_fc = raw_fc
        if args.fill_hole_area_m2 > 0:
            print("STAGE Filling small interior gaps...", flush=True)
            arcpy.management.EliminatePolygonPart(
                current_fc, holefilled_fc, "AREA", f"{args.fill_hole_area_m2} SquareMeters",
                None, "CONTAINED_ONLY")
            arcpy.management.Delete(current_fc)
            current_fc = holefilled_fc
        print("PROGRESS 85", flush=True)

        # Rounds the "staircase" pixel-boundary corners RasterToPolygon's own SIMPLIFY
        # option doesn't fully smooth out, into the kind of gently-curved boundary a human
        # tracing the area by eye would draw - same purpose as the QGIS original's Chaikin
        # smoothing pass, PAEK is this toolkit's closest built-in equivalent. Tolerance is
        # tied to the source raster's own pixel size so it scales with resolution instead
        # of needing a per-raster manual value.
        print("STAGE Smoothing boundaries...", flush=True)
        cell_size = arcpy.Raster(args.raster).meanCellWidth
        arcpy.cartography.SmoothPolygon(current_fc, smoothed_fc, "PAEK", f"{cell_size * 3} Meters")
        arcpy.management.Delete(current_fc)
        current_fc = smoothed_fc
        print("PROGRESS 90", flush=True)

        # Drops tiny slivers (a single stray bare pixel patch, stitching artifacts) below
        # the requested minimum area - computed on the final (hole-filled, smoothed)
        # geometry so the threshold applies to what the map actually shows, not an
        # intermediate shape. Deleted in place from the selection rather than copying the
        # "keep" set out to yet another temp feature class.
        arcpy.management.MakeFeatureLayer(current_fc, "cleared_lyr")
        arcpy.management.SelectLayerByAttribute("cleared_lyr", "NEW_SELECTION", f"Shape_Area < {args.min_area_m2}")
        if int(arcpy.management.GetCount("cleared_lyr")[0]) > 0:
            arcpy.management.DeleteFeatures("cleared_lyr")
        arcpy.management.SelectLayerByAttribute("cleared_lyr", "CLEAR_SELECTION")

        if args.exclude_fc:
            print("STAGE Excluding already-cleared area...", flush=True)
            arcpy.analysis.PairwiseErase(current_fc, args.exclude_fc, args.output_fc)
            arcpy.management.Delete(current_fc)
        else:
            arcpy.management.Rename(current_fc, args.output_fc)

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
