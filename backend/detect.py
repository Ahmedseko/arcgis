"""
LandTree Analyzer detection backend for the ArcGIS Pro add-in.

Run under ArcGIS Pro's own python (arcgispro-py3 conda env), which already
has arcpy + numpy + scipy. Install onnxruntime + pillow into that same env
for the Oil Palm Plantation YOLO profile:

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" -m pip install onnxruntime pillow

Called by TreeCounterAddin/PythonBackendService.cs as:
    python detect.py --raster <path> --profile "Natural Forest|Oil Palm Plantation" \
        --output-fc <feature class path> --summary <json path> \
        [--sigma N] [--exg-threshold N] [--min-smooth N] [--conf-threshold N] \
        [--exclude-cleared] \
        [--ai-provider gemini|openai|claude] [--api-key KEY] [--ai-model NAME]

Writes the detected points to --output-fc (created fresh, same spatial
reference as --raster) and a JSON summary to --summary:
    {"tree_count": int, "output_fc": str, "area_ha": float, "filtered_cleared_count": int}

--exclude-cleared reuses land_clearing.build_cleared_mask (see that module) to drop any
candidate whose pixel falls on cleared/bare ground - added after visual validation
against a real orthophoto (2026-07-31) showed false-positive points on bare
soil/roads that pass the ExG vegetation threshold on their own (small residual
weeds/soil-color artifacts). Roughly doubles run time (a second full raster
scan) since it's a real accuracy fix, not a free one - left as an opt-in
checkbox in the add-in rather than always-on.

Algorithm: Natural Forest always uses the ExG + Gaussian matched filter
detector (detector.detect_trees). Oil Palm Plantation uses the local YOLOv8n
ONNX primary detector (yolo_detector.detect_trees_yolo_primary) when the
model + onnxruntime are available, falling back to the same ExG detector
otherwise - this matches qgis_plugin/tree_counter/tree_counter_dialog.py's
_DetectWorker exactly (its "Kebun Sawit" mode).

If --api-key is given, candidates are additionally validated against the
selected AI vision provider (validator.validate_trees) before being written
out - optional, online, opt-in (matches qgis_plugin's separate "Validate with
Gemini" step, folded into one call here for a simpler single-button add-in
UX, and extended beyond Gemini to also support OpenAI and Claude).
"""
import argparse
import json
import sys

import arcpy

from detector import PROFILES, detect_trees
from raster_io import RasterInfo

PROFILE_MODE = {"Natural Forest": "forest", "Oil Palm Plantation": "palm"}
# See the --exclude-cleared filtering block in detect() for why this differs from
# land_clearing.DEFAULT_EXG_THRESHOLD (18).
CLEARED_FILTER_EXG_THRESHOLD = 30
DEFAULT_MODEL_BY_PROVIDER = {
    "gemini": "gemini-3.5-flash",
    "openai": "gpt-4o-mini",
    "claude": "claude-haiku-4-5",
}


def _write_feature_class(trees, raster_path, output_fc):
    sr = arcpy.Describe(raster_path).spatialReference
    out_path, out_name = output_fc.rsplit("\\", 1) if "\\" in output_fc else output_fc.rsplit("/", 1)

    if arcpy.Exists(output_fc):
        arcpy.management.Delete(output_fc)
    arcpy.management.CreateFeatureclass(out_path, out_name, "POINT", spatial_reference=sr)
    arcpy.management.AddField(output_fc, "crown_r", "DOUBLE")
    arcpy.management.AddField(output_fc, "sigma_px", "LONG")
    arcpy.management.AddField(output_fc, "score", "DOUBLE")
    arcpy.management.AddField(output_fc, "source", "TEXT", field_length=10)

    fields = ["SHAPE@XY", "crown_r", "sigma_px", "score", "source"]
    with arcpy.da.InsertCursor(output_fc, fields) as cursor:
        for t in trees:
            cursor.insertRow((
                (t["geo_x"], t["geo_y"]),
                t.get("crown_r", 0.0),
                t.get("sigma", 0),
                t.get("score", 0.0),
                t.get("source", "exg"),
            ))
    return output_fc


def detect(raster_path: str, profile: str, sigma=None, exg_threshold=None,
           min_smooth=None, conf_threshold=0.25, exclude_cleared_land=False,
           exclude_blurry=False, progress_cb=None, stage_cb=None,
           ai_provider=None, api_key=None, ai_model=None) -> tuple:
    if not arcpy.Exists(raster_path):
        raise FileNotFoundError(f"Raster not found: {raster_path}")

    mode = PROFILE_MODE[profile]
    defaults = PROFILES[mode]
    sigma = defaults["sigma_px"] if sigma is None else sigma
    exg_threshold = defaults["exg_threshold"] if exg_threshold is None else exg_threshold
    min_smooth = defaults["min_smooth"] if min_smooth is None else min_smooth

    # Detection normally gets the full 0-100 range. Each optional stage after it
    # (cleared-land filtering, AI validation) claims a slice off the end instead - up to
    # three stages sharing 0-100 between them. AI validation's own 85-100 slice is
    # unchanged from before this option existed, on purpose (its progress math didn't need
    # to move just because a new option now sits earlier in the pipeline).
    extra_stages = int(bool(exclude_cleared_land)) + int(bool(api_key))
    detect_ceiling = 100 if extra_stages == 0 else (85 if extra_stages == 1 else 60)
    detect_progress = progress_cb
    if progress_cb and extra_stages:
        detect_progress = lambda p: progress_cb(int(p * detect_ceiling / 100))

    if stage_cb:
        stage_cb("Detecting trees...")

    if mode == "palm":
        from yolo_detector import model_available, detect_trees_yolo_primary
        if model_available():
            trees, _ = detect_trees_yolo_primary(
                raster_path, conf_threshold=conf_threshold, progress_cb=detect_progress)
        else:
            trees, _ = detect_trees(
                raster_path, sigma_px=sigma, exg_threshold=exg_threshold,
                min_smooth=min_smooth, mode=mode, progress_cb=detect_progress,
                exclude_blurry=exclude_blurry)
    else:
        trees, _ = detect_trees(
            raster_path, sigma_px=sigma, exg_threshold=exg_threshold,
            min_smooth=min_smooth, mode=mode, progress_cb=detect_progress,
            exclude_blurry=exclude_blurry)

    filtered_cleared_count = 0
    if exclude_cleared_land:
        from land_clearing import build_cleared_mask
        if stage_cb:
            stage_cb("Filtering out points on cleared/bare ground...")
        clearing_progress = (lambda p: progress_cb(detect_ceiling + int(p * (85 - detect_ceiling) / 100))) if progress_cb else None
        # Deliberately NOT land_clearing.DEFAULT_EXG_THRESHOLD (18) - that's the same
        # value the tree detector itself uses as its OWN vegetation cutoff, which makes
        # the two checks structurally near-mutually-exclusive: a point the tree detector
        # accepted (exg > 18 at its refined centroid) can almost never also satisfy
        # "cleared" (exg < 18) at that same pixel. Confirmed empirically (2026-07-31):
        # only 3 of 7369 points got filtered this way on a real orthophoto with visibly
        # many false positives in open areas. A meaningfully higher threshold here gives
        # real overlap room - a point can be real "vegetation" by the tree detector's
        # own (lower) bar while still reading as "basically bare" by this stricter one.
        mask, _ = build_cleared_mask(raster_path, exg_threshold=CLEARED_FILTER_EXG_THRESHOLD, progress_cb=clearing_progress)
        before = len(trees)
        # 'py'/'px' are already absolute pixel row/col in the full raster (see
        # detector.detect_trees), same indexing the mask uses.
        trees = [t for t in trees if mask[t['py'], t['px']] == 0]
        filtered_cleared_count = before - len(trees)

    if api_key:
        from raster_io import load_rgb
        from validator import validate_trees
        provider = ai_provider or "gemini"
        model = ai_model or DEFAULT_MODEL_BY_PROVIDER.get(provider, DEFAULT_MODEL_BY_PROVIDER["gemini"])
        if stage_cb:
            stage_cb(f"Validating {len(trees)} candidates with {provider.capitalize()} ({model})...")
        rd = load_rgb(raster_path)
        validate_progress = (lambda p: progress_cb(85 + int(p * 0.15))) if progress_cb else None
        trees, _ = validate_trees(
            rd, trees, api_key, sigma_px=sigma, model=model,
            profile=mode, provider=provider, progress_cb=validate_progress)

    if progress_cb:
        progress_cb(100)
    return trees, mode, filtered_cleared_count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raster", required=True)
    parser.add_argument("--profile", required=True, choices=list(PROFILE_MODE))
    parser.add_argument("--output-fc", required=True, help="Feature class path to create")
    parser.add_argument("--summary", required=True, help="Path to write JSON result summary")
    parser.add_argument("--sigma", type=int, default=None)
    parser.add_argument("--exg-threshold", type=float, default=None)
    parser.add_argument("--min-smooth", type=float, default=None)
    parser.add_argument("--conf-threshold", type=float, default=0.25)
    parser.add_argument("--exclude-cleared", action="store_true",
                         help="Drop candidates that fall on cleared/bare ground (see land_clearing.py) - roughly doubles run time")
    parser.add_argument("--exclude-blurry", action="store_true",
                         help="Drop candidates in blurred/stitching-seam regions (see detector.BLUR_VARIANCE_MIN) - unvalidated against ground truth, opt-in")
    parser.add_argument("--ai-provider", choices=list(DEFAULT_MODEL_BY_PROVIDER), default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--ai-model", default=None)
    args = parser.parse_args()

    try:
        trees, _, filtered_cleared_count = detect(
            args.raster, args.profile, sigma=args.sigma,
            exg_threshold=args.exg_threshold, min_smooth=args.min_smooth,
            conf_threshold=args.conf_threshold, exclude_cleared_land=args.exclude_cleared,
            exclude_blurry=args.exclude_blurry,
            ai_provider=args.ai_provider, api_key=args.api_key, ai_model=args.ai_model,
            progress_cb=lambda p: print(f"PROGRESS {p}", flush=True),
            stage_cb=lambda s: print(f"STAGE {s}", flush=True),
        )
        output_fc = _write_feature_class(trees, args.raster, args.output_fc)
        # Total scanned area (the whole raster extent, not the area covered by detected
        # crowns) - same value regardless of how many trees were found.
        info = RasterInfo(args.raster)
        area_ha = (info.W * info.H * info.px_size ** 2) / 10000.0
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    with open(args.summary, "w", encoding="utf-8") as f:
        json.dump({
            "tree_count": len(trees), "output_fc": output_fc, "area_ha": area_ha,
            "filtered_cleared_count": filtered_cleared_count,
        }, f)
    return 0


if __name__ == "__main__":
    sys.exit(main())
