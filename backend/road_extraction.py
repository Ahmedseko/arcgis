"""
Road/trail centerline extraction - see detect_roads.py for the CLI/vectorization half.

Skeletonizes land_clearing.py's bare-ground mask (roads read as "cleared" too - same
low-vegetation-greenness signal as bare soil, and now also excludes water/rivers, see
WATER_BRIGHTNESS_MAX) down to 1px-wide centerlines with skimage.morphology.skeletonize,
so arcpy.conversion.RasterToPolyline (see detect_roads.py) traces a sensible line down
the middle of each road instead of tracing the edges of a many-pixel-wide blob.

Not a port of github.com/microsoft/RoadDetections' own approach: its segmentation model
is Keras/Python 3.6 trained on 100cm/px satellite imagery - wrong resolution regime for
our ~5cm/px drone orthophotos and awkward to run inside arcgispro-py3. Its C#
geometry-generation module (custom thinning + graph construction + graph-optimization
to turn a mask into a line network) is the part worth imitating - skimage's skeletonize
+ arcpy's own RasterToPolyline GP tool already do that job for us, no new code needed.
"""
import arcpy
import numpy as np
from scipy import ndimage
from skimage.morphology import skeletonize

from land_clearing import build_cleared_mask, DEFAULT_SMOOTH_PX

# Deliberately NOT land_clearing.DEFAULT_EXG_THRESHOLD (18) - that value is tuned for
# accurate AREA overlap with human-digitized clearing polygons (a different accuracy
# target), not centerline position. Validated instead (2026-08-11) against a real
# human-digitized road shapefile (hasil digit/digitasi jalan.shp, ~6.2km, 11 segments)
# over its actual source orthophoto (260726_Bypass AKT_1m.tif) using the standard
# "buffer method" road-network accuracy metric (Wiedemann et al. 1998 - completeness =
# ground-truth length within a buffer of the extraction / total ground-truth length;
# correctness = extraction length within a buffer of the ground truth / total extraction
# length): sweeping exg_threshold from -10 to 30 (max_width_m=0, min_dangle_m=5, 10m
# buffer) peaked at exg_threshold=8, F1 60.3% (correctness 58.3%/completeness 62.4%) vs.
# 53.5% at the shared default of 18 - a stricter (lower) threshold shrinks the mask
# closer to the actual driving surface, which matters more for a centerline (skeletonize
# is sensitive to the full mask width) than it does for land_clearing.py's own area-
# overlap target. min_dangle_m was swept too (3-15m) and made no measurable difference
# (F1 flat at 60.3%) - left at its existing 5m default.
DEFAULT_ROAD_EXG_THRESHOLD = 8

# Real result (2026-08-10, a real orthophoto with one road + one fork near a small
# clearing): skeletonize produced 65 line segments, most of them short fragments (many
# under 10-15m) - a normal skeletonize artifact from any mask whose edge isn't perfectly
# smooth (a few noisy boundary pixels read as a tiny "hair" branch perpendicular to the
# real centerline), not real forks. RasterToPolyline's own minimum_dangle_length catches
# free-hanging dangles (one end unconnected) but not a short segment already bridging two
# junctions (both ends connected) - a first attempt at pruning those at the raster/pixel
# level (checked-in briefly, then reverted 2026-08-11) turned out unreliable: a branch
# pixel diagonally touching two unrelated columns of a long straight line is
# indistinguishable, by simple 8-connected neighbor counting, from a real 3-way junction,
# so short bridges kept surviving misclassified as junctions. _drop_short_bridges below
# does the same job on the vectorized *output* instead (see detect_roads.py) - polyline
# endpoints are exact float coordinates from RasterToPolyline's own topology, no pixel-
# adjacency ambiguity to get wrong.

# Same real result, second problem: the extracted line visibly wandered off the actual
# road surface into the wide bare dirt/quarry/stockpile ground alongside it. Root cause -
# land_clearing.py's mask flags ALL bare ground, road or not, and skeletonize follows the
# medial axis of whatever shape it's given: on a uniform-width strip that axis IS the
# road's centerline, but on a wide, irregular blob (a quarry pit, a wide cleared yard) the
# medial axis just traces that blob's own shape, which isn't a road at all. Filtering the
# mask down to only its "thin enough to plausibly be a road" parts before skeletonizing
# fixes this at the source instead of trying to clean up the resulting wander after the
# fact - same idea real river-centerline extraction uses to exclude lakes from a water
# mask before skeletonizing it into a channel network.
#
# ponytail: OFF by default (max_width_m=0 below), unlike DEFAULT_ROAD_EXG_THRESHOLD above
# - a first attempt at 12m wiped an entire real road result to 0 features (2026-08-10).
# Confirmed with real ground-truth numbers the next day (2026-08-11, same accuracy method
# as DEFAULT_ROAD_EXG_THRESHOLD's own validation): every max_width_m value swept from 15
# to 50m scored WORSE than 0 (F1 dropped from the 0-baseline's 53.5% to 0-18% across that
# whole range) - on this site the bare-ground corridor (road + graded shoulder/
# embankment) runs wider than 50m for long stretches, not just at isolated quarry
# pockets like assumed, so this one blanket threshold can't tell "wide road" from "wide
# quarry" here at any tested value. Same status as land_clearing.py's fresh_color flag: a
# knob to try on a site with narrower quarry pockets (via detect_roads.py's
# --max-width-m), not a default fix.
MAX_ROAD_WIDTH_M = 0


def _remove_wide_regions(mask, width_px):
    """
    Zeroes out mask regions wider than width_px, leaving only its narrow (road-like)
    parts - opening with an iterated small kernel isolates the "core" of anything wide
    enough to survive erode-then-dilate at that radius (same iterated-3x3-kernel idiom
    land_clearing.py's OPENING_ITERATIONS already uses, here to isolate what's too FAT to
    be a road instead of what's too small to be a clearing), then that core is dilated
    back out to roughly its original footprint and subtracted from the mask.
    """
    radius_iters = max(1, width_px // 2)
    wide_core = ndimage.binary_opening(mask, iterations=radius_iters)
    wide_footprint = ndimage.binary_dilation(wide_core, iterations=radius_iters)
    return mask & ~wide_footprint


def _drop_short_bridges(fc, min_length_m, max_passes=5):
    """
    Deletes short polyline features (< min_length_m) whose BOTH endpoints are shared
    with at least one other feature - a "bridge" squeezed directly between two
    junctions, as opposed to a dangling stub (one free end), which
    conversion.RasterToPolyline's own minimum_dangle_length already drops during
    vectorization. Repeats (removing a bridge can turn a former junction into a
    dead-end, exposing a newly-prunable dangle one level in) up to max_passes.

    Endpoint matching is exact-coordinate (rounded to mm) rather than a spatial
    tolerance search - safe here because every feature came out of the same
    RasterToPolyline call, so genuinely-shared endpoints land on identical raster grid
    coordinates, not just nearby ones.
    """
    for _ in range(max_passes):
        with arcpy.da.SearchCursor(fc, ["OID@", "SHAPE@", "SHAPE@LENGTH"]) as cursor:
            rows = [(oid, shape.firstPoint, shape.lastPoint, length) for oid, shape, length in cursor]

        def key(pt):
            return (round(pt.X, 3), round(pt.Y, 3))

        endpoint_count = {}
        for _oid, p0, p1, _length in rows:
            for k in (key(p0), key(p1)):
                endpoint_count[k] = endpoint_count.get(k, 0) + 1

        to_delete = {
            oid for oid, p0, p1, length in rows
            if length < min_length_m and endpoint_count[key(p0)] > 1 and endpoint_count[key(p1)] > 1
        }
        if not to_delete:
            break
        with arcpy.da.UpdateCursor(fc, ["OID@"]) as cursor:
            for row in cursor:
                if row[0] in to_delete:
                    cursor.deleteRow()


def build_road_skeleton(raster_path, exg_threshold=DEFAULT_ROAD_EXG_THRESHOLD,
                         smooth_px=DEFAULT_SMOOTH_PX, max_width_m=MAX_ROAD_WIDTH_M,
                         progress_cb=None):
    """
    Returns (skeleton, info): skeleton is a full-raster-size uint8 array (1 = centerline
    pixel), info is the RasterInfo - same shape/indexing as land_clearing.build_cleared_mask.
    """
    mask_progress = (lambda p: progress_cb(int(p * 0.85))) if progress_cb else None
    mask, info = build_cleared_mask(raster_path, exg_threshold=exg_threshold,
                                     smooth_px=smooth_px, progress_cb=mask_progress)
    if progress_cb:
        progress_cb(88)
    if max_width_m and info.px_size > 0:
        mask = _remove_wide_regions(mask, max(1, int(round(max_width_m / info.px_size))))
    if progress_cb:
        progress_cb(90)
    # skeletonize needs a plain bool array, not the uint8 mask build_cleared_mask returns.
    skeleton = skeletonize(mask.astype(bool)).astype(np.uint8)
    if progress_cb:
        progress_cb(95)
    return skeleton, info


def extract_road_skeleton_raster(raster_path, output_mask_raster, exg_threshold=DEFAULT_ROAD_EXG_THRESHOLD,
                                  smooth_px=DEFAULT_SMOOTH_PX, max_width_m=MAX_ROAD_WIDTH_M,
                                  progress_cb=None):
    """
    Writes the skeleton (see build_road_skeleton) as a single-band raster to
    output_mask_raster (same extent/spatial reference as raster_path): 1 = centerline
    pixel, NoData everywhere else - mirrors land_clearing.detect_land_clearing, split
    out the same way so detect_roads.py's CLI only has to add the RasterToPolyline step.
    Returns output_mask_raster.
    """
    skeleton, info = build_road_skeleton(raster_path, exg_threshold, smooth_px,
                                          max_width_m, progress_cb)
    lower_left = arcpy.Point(info.xmin, info.ymax - info.H * info.px_size)
    raster = arcpy.NumPyArrayToRaster(skeleton, lower_left, info.px_size, info.px_size, value_to_nodata=0)
    raster.save(output_mask_raster)
    arcpy.management.DefineProjection(output_mask_raster, arcpy.Raster(raster_path).spatialReference)
    return output_mask_raster
