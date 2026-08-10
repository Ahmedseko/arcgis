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

from land_clearing import build_cleared_mask, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX

# Real result (2026-08-10, a real orthophoto with one road + one fork near a small
# clearing): skeletonize produced 65 line segments, most of them short fragments (many
# under 10-15m) - a normal skeletonize artifact from any mask whose edge isn't perfectly
# smooth (a few noisy boundary pixels read as a tiny "hair" branch perpendicular to the
# real centerline), not real forks. RasterToPolyline's own minimum_dangle_length only
# catches free-hanging dangles, not short segments already bridging two junctions, so it
# didn't clean these up on its own.
# ponytail: PRUNE_LENGTH_M picked by eye against that one real result, not swept against
# ground truth - revisit if it's still too noisy or starts eating real short driveways.
PRUNE_LENGTH_M = 8.0


def _prune_skeleton_spurs(skeleton, iterations):
    """
    Iteratively erodes free endpoints (skeleton pixels with exactly one - or zero, for
    an isolated speck - neighbor) off the skeleton, `iterations` times. A spur/speck
    shorter than `iterations` px vanishes entirely well before the loop ends; a real
    through-line or junction only loses `iterations` px off its own tips (negligible
    against a real road's length, and RasterToPolyline's minimum_dangle_length is a
    second, coarser safety net for whatever's left after this).

    Caveat: a real road that runs off the edge of the raster tile also reads as an
    "endpoint" there (nothing beyond the tile boundary to connect to) and loses the same
    `iterations` px at that cut edge - fine for a real orthophoto tile (typically
    hundreds of meters across, so a few meters off a tile-edge crossing is noise), but
    see test_road_extraction_e2e.py's own note on why its small synthetic raster
    disables pruning entirely rather than hitting this.
    """
    skeleton = skeleton.copy()
    kernel = np.ones((3, 3), dtype=np.uint8)
    for _ in range(iterations):
        neighbor_count = ndimage.convolve(skeleton, kernel, mode='constant') - skeleton
        endpoints = (skeleton == 1) & (neighbor_count <= 1)
        if not endpoints.any():
            break
        skeleton[endpoints] = 0
    return skeleton


def build_road_skeleton(raster_path, exg_threshold=DEFAULT_EXG_THRESHOLD,
                         smooth_px=DEFAULT_SMOOTH_PX, prune_length_m=PRUNE_LENGTH_M,
                         progress_cb=None):
    """
    Returns (skeleton, info): skeleton is a full-raster-size uint8 array (1 = centerline
    pixel), info is the RasterInfo - same shape/indexing as land_clearing.build_cleared_mask.
    """
    mask_progress = (lambda p: progress_cb(int(p * 0.85))) if progress_cb else None
    mask, info = build_cleared_mask(raster_path, exg_threshold=exg_threshold,
                                     smooth_px=smooth_px, progress_cb=mask_progress)
    if progress_cb:
        progress_cb(90)
    # skeletonize needs a plain bool array, not the uint8 mask build_cleared_mask returns.
    skeleton = skeletonize(mask.astype(bool)).astype(np.uint8)
    if prune_length_m and info.px_size > 0:
        prune_px = max(1, int(round(prune_length_m / info.px_size)))
        skeleton = _prune_skeleton_spurs(skeleton, prune_px)
    if progress_cb:
        progress_cb(95)
    return skeleton, info


def extract_road_skeleton_raster(raster_path, output_mask_raster, exg_threshold=DEFAULT_EXG_THRESHOLD,
                                  smooth_px=DEFAULT_SMOOTH_PX, prune_length_m=PRUNE_LENGTH_M, progress_cb=None):
    """
    Writes the skeleton (see build_road_skeleton) as a single-band raster to
    output_mask_raster (same extent/spatial reference as raster_path): 1 = centerline
    pixel, NoData everywhere else - mirrors land_clearing.detect_land_clearing, split
    out the same way so detect_roads.py's CLI only has to add the RasterToPolyline step.
    Returns output_mask_raster.
    """
    skeleton, info = build_road_skeleton(raster_path, exg_threshold, smooth_px, prune_length_m, progress_cb)
    lower_left = arcpy.Point(info.xmin, info.ymax - info.H * info.px_size)
    raster = arcpy.NumPyArrayToRaster(skeleton, lower_left, info.px_size, info.px_size, value_to_nodata=0)
    raster.save(output_mask_raster)
    arcpy.management.DefineProjection(output_mask_raster, arcpy.Raster(raster_path).spatialReference)
    return output_mask_raster
