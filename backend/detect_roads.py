"""
Road/trail centerline extraction CLI - see road_extraction.py for the algorithm.

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), same as detect_clearing.py.

Called by TreeCounterAddin/PythonBackendService.cs as:
    python detect_roads.py --raster <path> --output-fc <polyline feature class path> \
        --summary <json path> [--exg-threshold N] [--smooth-px N] [--min-dangle-m N] \
        [--ai-provider gemini|openai|claude] [--api-key KEY] [--ai-model NAME]

Writes the extracted road/trail centerlines to --output-fc and a JSON summary to
--summary: {"line_count": int, "output_fc": str, "length_km": float, "rejected_by_ai_count": int}

If --api-key is given, each surviving segment is additionally cropped (extent + margin,
see raster_io.read_window) and validated against the selected AI vision provider
(validator.validate_crops) before being written out - same pattern as detect_clearing.py's
polygons, extended here after a real report (2026-08-16) asked for it.
"""
import argparse
import json
import sys

import arcpy

from land_clearing import DEFAULT_SMOOTH_PX
from road_extraction import (
    extract_road_skeleton_raster, _drop_short_bridges,
    DEFAULT_ROAD_EXG_THRESHOLD, MAX_ROAD_WIDTH_M,
)
from detect import DEFAULT_MODEL_BY_PROVIDER


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raster", required=True)
    parser.add_argument("--output-fc", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--exg-threshold", type=float, default=DEFAULT_ROAD_EXG_THRESHOLD)
    parser.add_argument("--smooth-px", type=float, default=DEFAULT_SMOOTH_PX)
    parser.add_argument("--min-dangle-m", type=float, default=5.0,
                         help="Drop dangling stubs and short inter-junction bridges shorter than this (skeletonize noise)")
    parser.add_argument("--max-width-m", type=float, default=MAX_ROAD_WIDTH_M,
                         help="Drop bare-ground regions wider than this (quarry pits, wide cleared yards - not roads; see road_extraction.py). 0 disables.")
    parser.add_argument("--mask-source", choices=["exg", "unet"], default="exg",
                         help="'unet' uses road_unet.onnx (see backend/training/) instead of the ExG threshold - "
                              "not yet fine-tuned on local imagery, measured worse than 'exg' on real ground truth "
                              "(README's Road/Trail Extraction accuracy section), opt-in until that changes")
    parser.add_argument("--unet-threshold", type=float, default=0.5,
                         help="Road-probability cutoff for --mask-source unet")
    parser.add_argument("--ai-provider", choices=list(DEFAULT_MODEL_BY_PROVIDER), default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--ai-model", default=None)
    args = parser.parse_args()

    try:
        if not arcpy.Exists(args.raster):
            raise FileNotFoundError(f"Raster not found: {args.raster}")

        gdb = args.output_fc.rsplit("\\", 1)[0] if "\\" in args.output_fc else args.output_fc.rsplit("/", 1)[0]
        skel_raster = f"{gdb}\\RoadSkeleton_tmp"

        print("STAGE Scanning for road/trail centerlines...", flush=True)
        extract_road_skeleton_raster(
            args.raster, skel_raster, exg_threshold=args.exg_threshold, smooth_px=args.smooth_px,
            max_width_m=args.max_width_m, mask_source=args.mask_source, unet_threshold=args.unet_threshold,
            progress_cb=lambda p: print(f"PROGRESS {p}", flush=True))
        print("PROGRESS 96", flush=True)

        print("STAGE Vectorizing centerlines...", flush=True)
        # ZERO background (default) - skeleton is 0/1 with 0 as NoData already (see
        # value_to_nodata above), SIMPLIFY for the same reason detect_clearing.py uses it
        # on RasterToPolygon: NO_SIMPLIFY traces the exact pixel staircase at ~0.05m/px.
        arcpy.conversion.RasterToPolyline(skel_raster, args.output_fc, "ZERO", args.min_dangle_m, "SIMPLIFY")
        arcpy.management.Delete(skel_raster)

        # RasterToPolyline's own minimum_dangle_length only drops dangling stubs (one
        # free end) - this catches the other shape skeletonize noise takes, a short
        # segment bridging two nearby junctions (see _drop_short_bridges).
        _drop_short_bridges(args.output_fc, args.min_dangle_m)
        print("PROGRESS 97", flush=True)

        # Optional AI Vision Validation (see module docstring) - same extent+margin crop
        # approach as detect_clearing.py's polygons; SHAPE@'s .extent works the same way
        # for a polyline segment as it does for a polygon.
        rejected_by_ai_count = 0
        if args.api_key:
            from raster_io import RasterInfo, read_window
            from validator import validate_crops, _whole_jpeg_b64

            provider = args.ai_provider or "gemini"
            model = args.ai_model or DEFAULT_MODEL_BY_PROVIDER.get(provider, DEFAULT_MODEL_BY_PROVIDER["gemini"])
            print(f"STAGE Validating road/trail segments with {provider.capitalize()} ({model})...", flush=True)

            info = RasterInfo(args.raster)
            MARGIN_PX, MIN_HALF, MAX_HALF = 20, 60, 400
            pairs = []
            with arcpy.da.SearchCursor(args.output_fc, ["OID@", "SHAPE@"]) as cursor:
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
                pairs, args.api_key, model=model, profile="road", provider=provider,
                progress_cb=lambda p: print(f"PROGRESS {97 + int(p * 0.02)}", flush=True))
            rejected_oids = [oid for oid, _ in pairs if oid not in kept_oids]
            rejected_by_ai_count = len(rejected_oids)
            if rejected_oids:
                oid_field = arcpy.Describe(args.output_fc).OIDFieldName
                arcpy.management.MakeFeatureLayer(args.output_fc, "ai_reject_lyr")
                arcpy.management.SelectLayerByAttribute(
                    "ai_reject_lyr", "NEW_SELECTION",
                    f"{oid_field} IN ({','.join(str(o) for o in rejected_oids)})")
                arcpy.management.DeleteFeatures("ai_reject_lyr")
        print("PROGRESS 99", flush=True)

        count = int(arcpy.management.GetCount(args.output_fc)[0])
        total_length_m = 0.0
        with arcpy.da.SearchCursor(args.output_fc, ["SHAPE@LENGTH"]) as cursor:
            for (length,) in cursor:
                total_length_m += length
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    with open(args.summary, "w", encoding="utf-8") as f:
        json.dump({
            "line_count": count, "output_fc": args.output_fc, "length_km": total_length_m / 1000.0,
            "rejected_by_ai_count": rejected_by_ai_count,
        }, f)
    print("PROGRESS 100", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
