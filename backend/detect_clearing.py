"""
Land clearing (bukaan lahan) detection CLI - see land_clearing.py for the algorithm.

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), same as detect.py.

Called by TreeCounterAddin/PythonBackendService.cs as:
    python detect_clearing.py --raster <path> --output-fc <feature class path> \
        --summary <json path> [--exg-threshold N] [--smooth-px N] [--method exg|obia] \
        [--fresh-color] [--bright-min N] [--min-area-m2 N] [--fill-hole-area-m2 N] \
        [--exclude-fc <polygon feature class to erase, e.g. already-harvested area>] \
        [--ai-provider gemini|openai|claude] [--api-key KEY] [--ai-model NAME]

Writes the detected clearing polygons to --output-fc and a JSON summary to --summary:
    {"polygon_count": int, "output_fc": str, "area_ha": float, "rejected_by_ai_count": int}

If --api-key is given, each surviving polygon is additionally cropped (extent + margin,
see raster_io.read_window) and validated against the selected AI vision provider
(validator.validate_crops) before being written out - same optional/opt-in AI Vision
Validation pattern as detect.py's tree candidates, extended here after a real report
(2026-08-16) asked for it.
"""
import argparse
import json
import sys

import arcpy

from land_clearing import (
    detect_land_clearing, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX,
    OPENING_ITERATIONS, CLOSING_ITERATIONS,
)
from detect import DEFAULT_MODEL_BY_PROVIDER


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
    parser.add_argument("--ai-provider", choices=list(DEFAULT_MODEL_BY_PROVIDER), default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--ai-model", default=None)
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
        # smoothing pass, PAEK is this toolkit's closest built-in equivalent.
        #
        # Tolerance = closing_iterations * cell_size * 3, not just a flat cell_size * 3
        # (~0.17m at this raster's ~0.058m/px - what this used to be). A real report
        # (2026-08-16, real GMW site imagery) found the result still visibly "blocky"/
        # jigsaw-puzzle-edged despite this step - the diamond-shaped notches binary_opening/
        # closing leave behind scale with closing_iterations pixels (~15px ~ 0.87m here),
        # not with the raw pixel size, so a tolerance smaller than that barely touches them.
        # Tested against the real reported crop: 0.17m (unchanged), 1m still visibly blocky;
        # 2-3m genuinely smooth, human-digitization-like. Scaling by closing_iterations
        # keeps this correct if that setting or the raster's resolution changes, instead of
        # a value re-tuned for one specific site.
        print("STAGE Smoothing boundaries...", flush=True)
        cell_size = arcpy.Raster(args.raster).meanCellWidth
        smooth_tolerance_m = max(args.closing_iterations * cell_size * 3, 0.5)
        arcpy.cartography.SmoothPolygon(current_fc, smoothed_fc, "PAEK", f"{smooth_tolerance_m} Meters")
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
        print("PROGRESS 92", flush=True)

        # Optional AI Vision Validation (see module docstring) - runs after the min-area
        # filter so API calls are only spent on polygons already worth showing, and before
        # --exclude-fc so an excluded/already-harvested area never gets sent to the API at
        # all. Crops each polygon's own extent (+ margin, capped so one huge clearing
        # doesn't balloon the request) via raster_io.read_window rather than loading the
        # whole raster like validate_trees does - clearing candidates are already a short,
        # known list of small windows, not scattered points across the full image.
        rejected_by_ai_count = 0
        if args.api_key:
            from raster_io import RasterInfo, read_window
            from validator import validate_crops, _whole_jpeg_b64

            provider = args.ai_provider or "gemini"
            model = args.ai_model or DEFAULT_MODEL_BY_PROVIDER.get(provider, DEFAULT_MODEL_BY_PROVIDER["gemini"])
            print(f"STAGE Validating cleared areas with {provider.capitalize()} ({model})...", flush=True)

            info = RasterInfo(args.raster)
            MARGIN_PX, MIN_HALF, MAX_HALF = 20, 60, 400
            pairs = []
            with arcpy.da.SearchCursor(current_fc, ["OID@", "SHAPE@"]) as cursor:
                for oid, shape in cursor:
                    ext = shape.extent
                    px_xmin = (ext.XMin - info.xmin) / info.px_size
                    px_xmax = (ext.XMax - info.xmin) / info.px_size
                    px_ymin = (info.ymax - ext.YMax) / info.px_size
                    px_ymax = (info.ymax - ext.YMin) / info.px_size
                    cx, cy = (px_xmin + px_xmax) / 2, (px_ymin + px_ymax) / 2
                    half_w = min(max((px_xmax - px_xmin) / 2 + MARGIN_PX, MIN_HALF), MAX_HALF)
                    half_h = min(max((px_ymax - px_ymin) / 2 + MARGIN_PX, MIN_HALF), MAX_HALF)
                    x_off = max(0, min(int(cx - half_w), info.W - 1))
                    y_off = max(0, min(int(cy - half_h), info.H - 1))
                    w = min(int(half_w * 2), info.W - x_off)
                    h = min(int(half_h * 2), info.H - y_off)
                    b64 = _whole_jpeg_b64(read_window(args.raster, info, x_off, y_off, w, h))
                    if b64:
                        pairs.append((oid, b64))

            kept_oids, _ = validate_crops(
                pairs, args.api_key, model=model, profile="clearing", provider=provider,
                progress_cb=lambda p: print(f"PROGRESS {92 + int(p * 0.06)}", flush=True))
            rejected_oids = [oid for oid, _ in pairs if oid not in kept_oids]
            rejected_by_ai_count = len(rejected_oids)
            if rejected_oids:
                oid_field = arcpy.Describe(current_fc).OIDFieldName
                arcpy.management.MakeFeatureLayer(current_fc, "ai_reject_lyr")
                arcpy.management.SelectLayerByAttribute(
                    "ai_reject_lyr", "NEW_SELECTION",
                    f"{oid_field} IN ({','.join(str(o) for o in rejected_oids)})")
                arcpy.management.DeleteFeatures("ai_reject_lyr")
        print("PROGRESS 98", flush=True)

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
        json.dump({
            "polygon_count": count, "output_fc": args.output_fc, "area_ha": total_area_m2 / 10000.0,
            "rejected_by_ai_count": rejected_by_ai_count,
        }, f)
    print("PROGRESS 100", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
