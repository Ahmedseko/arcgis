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
from skimage.morphology import skeletonize

from land_clearing import build_cleared_mask, DEFAULT_EXG_THRESHOLD, DEFAULT_SMOOTH_PX


def build_road_skeleton(raster_path, exg_threshold=DEFAULT_EXG_THRESHOLD,
                         smooth_px=DEFAULT_SMOOTH_PX, progress_cb=None):
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
    if progress_cb:
        progress_cb(95)
    return skeleton, info


def extract_road_skeleton_raster(raster_path, output_mask_raster, exg_threshold=DEFAULT_EXG_THRESHOLD,
                                  smooth_px=DEFAULT_SMOOTH_PX, progress_cb=None):
    """
    Writes the skeleton (see build_road_skeleton) as a single-band raster to
    output_mask_raster (same extent/spatial reference as raster_path): 1 = centerline
    pixel, NoData everywhere else - mirrors land_clearing.detect_land_clearing, split
    out the same way so detect_roads.py's CLI only has to add the RasterToPolyline step.
    Returns output_mask_raster.
    """
    skeleton, info = build_road_skeleton(raster_path, exg_threshold, smooth_px, progress_cb)
    lower_left = arcpy.Point(info.xmin, info.ymax - info.H * info.px_size)
    raster = arcpy.NumPyArrayToRaster(skeleton, lower_left, info.px_size, info.px_size, value_to_nodata=0)
    raster.save(output_mask_raster)
    arcpy.management.DefineProjection(output_mask_raster, arcpy.Raster(raster_path).spatialReference)
    return output_mask_raster
