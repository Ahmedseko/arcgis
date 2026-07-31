"""
Land clearing (bukaan lahan) detection - ported from
qgis_plugin/tree_counter/detector.py's detect_land_clearing.

Opposite of tree detection: look for LOW ExG (2G-R-B), not high - bare soil, roads, and
recently cleared/harvested ground read as low vegetation greenness. RGB-only, no NIR/NDVI
needed - same ExG math and color calibration already used for tree detection.

Ported to produce a boolean mask RASTER instead of GDAL/OGR polygon WKT (the QGIS
original's approach) - this add-in doesn't depend on GDAL/OGR (see raster_io.py's own
comment on why), and ArcGIS Pro's own conversion.RasterToPolygon GP tool already does
vectorization well, so detect_clearing.py (the CLI) handles that step instead of
reimplementing a polygonizer here.

Processes in horizontal blocks (like detector.detect_trees) so memory stays bounded on
large orthophotos, unlike the QGIS original (which reads the whole raster at once - its
own docstring already flags this as a "ponytail" shortcut to revisit if it became a
problem in the field; on this add-in's real test orthophotos (see README), it would be:
the RGB float32 arrays alone are tens of GB for a large drone mosaic).

Not ported (skipped for this first pass - add back if actually needed in the field):
- exclude_wkt's buffer_m "ring" restriction (limit detection to a ring around an
  excluded polygon) - detect_clearing.py's --exclude-fc does a plain erase instead,
  which covers the common "don't flag area already harvested" case.
- fresh_color filtering (bright + reddish soil-only filter, to tell fresh cleared
  ground apart from roads/rivers) - a site-specific calibration knob from the QGIS
  plugin's own field testing, not obviously needed until this is field-tested here too.
"""
import arcpy
import numpy as np
from scipy import ndimage

from raster_io import RasterInfo, read_block

DEFAULT_EXG_THRESHOLD = 18
DEFAULT_SMOOTH_PX = 3


def build_cleared_mask(raster_path, exg_threshold=DEFAULT_EXG_THRESHOLD, smooth_px=DEFAULT_SMOOTH_PX,
                        fill_holes=True, block_size=3000, overlap=150, progress_cb=None):
    """
    Returns (mask, info): mask is a full-raster-size uint8 numpy array (1 = cleared/bare
    ground, indexed [row, col] same as detector.detect_trees' 'py'/'px'), info is the
    RasterInfo. Split out from detect_land_clearing (which additionally writes this to an
    arcpy raster + vectorizes it) so detect.py can reuse just the mask, in-memory, to
    filter out tree candidates that land on bare ground - without needing a raster/
    feature class round trip for that.
    """
    info = RasterInfo(raster_path)
    H, W = info.H, info.W

    # Small enough to hold the WHOLE mask in memory even for a very large raster (1
    # byte/pixel here vs. the 4+ bytes/pixel needed to hold the source RGB block itself) -
    # unlike the RGB data, only the classified 0/1 result needs to outlive each block.
    mask = np.zeros((H, W), dtype=np.uint8)

    total_blocks = max(1, (H + block_size - 1) // block_size)
    block_num = 0
    y = 0
    while y < H:
        h = min(block_size + overlap, H - y)
        rd = read_block(raster_path, info, y, h)
        r, g, b, valid = rd.r, rd.g, rd.b, rd.valid

        exg = 2.0 * g - r - b
        if smooth_px and smooth_px > 1:
            exg = ndimage.gaussian_filter(exg, smooth_px)

        cleared = valid & (exg < exg_threshold)
        if fill_holes:
            # Closes small holes (e.g. a solitary tree left standing in the middle of a
            # clearing) so clearing polygons come out as one solid area instead of full
            # of tiny holes - this is about the boundary of the cleared area, not
            # preserving every individual surviving tree.
            cleared = ndimage.binary_closing(cleared, iterations=2)

        # This block's overlap tail duplicates the start of the next block - same
        # crop-to-core idea detector.detect_trees uses for its point candidates, applied
        # to the mask instead so the seam isn't left jagged/duplicated between blocks.
        core_h = min(block_size, h)
        mask[y:y + core_h, :] = cleared[:core_h, :]

        block_num += 1
        if progress_cb:
            progress_cb(int(block_num / total_blocks * 80))
        y += block_size

    if progress_cb:
        progress_cb(90)
    return mask, info


def detect_land_clearing(raster_path, output_mask_raster, exg_threshold=DEFAULT_EXG_THRESHOLD,
                          smooth_px=DEFAULT_SMOOTH_PX, fill_holes=True,
                          block_size=3000, overlap=150, progress_cb=None):
    """
    Writes a single-band raster to output_mask_raster (same extent/spatial reference as
    raster_path): 1 = cleared/bare ground, NoData everywhere else (so
    conversion.RasterToPolygon in detect_clearing.py only ever vectorizes the cleared
    class, with nothing else to filter out afterward). Returns output_mask_raster.
    """
    mask, info = build_cleared_mask(raster_path, exg_threshold, smooth_px, fill_holes,
                                     block_size, overlap, progress_cb)
    lower_left = arcpy.Point(info.xmin, info.ymax - info.H * info.px_size)
    raster = arcpy.NumPyArrayToRaster(mask, lower_left, info.px_size, info.px_size, value_to_nodata=0)
    raster.save(output_mask_raster)
    arcpy.management.DefineProjection(output_mask_raster, arcpy.Raster(raster_path).spatialReference)
    return output_mask_raster
