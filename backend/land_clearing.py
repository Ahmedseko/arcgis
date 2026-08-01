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
from skimage.segmentation import slic

from raster_io import RasterInfo, read_block

DEFAULT_EXG_THRESHOLD = 18
DEFAULT_SMOOTH_PX = 3

# Denoise/generalize passes, applied once to the FULL assembled mask (not per-block - see
# below). Repeated small-kernel passes (scipy's own recommended efficient approach) rather
# than one big structuring element: iterations=N with the default 3x3 cross approximates a
# radius-N diamond at a fraction of the cost of an actual NxN kernel. At this raster's
# ~0.058 m/px, ~10-15 iterations generalizes at roughly the 0.6-0.9 m scale - closer to how
# a person would actually trace a clearing boundary by hand than the raw pixel-exact mask
# (added after a real result looked "too busy/jagged, not like human digitization" -
# 2026-07-31).
OPENING_ITERATIONS = 10  # first: strip small false "cleared" specks inside real vegetation
CLOSING_ITERATIONS = 15  # then: fill small gaps/holes inside real clearings

# ponytail: threshold-per-pixel + morphology is patching jaggedness after the fact, not
# preventing it. The alternative is OBIA - segment first (superpixels), classify per-object
# instead of per-pixel, which yields smooth boundaries natively. skimage.segmentation.slic
# (SLIC) is the portable, non-GEE equivalent of the SNIC algorithm GEE-based OBIA workflows
# use, and has documented precedent on exactly this kind of data (SLIC-UAV, Wagner et al.
# 2020, arXiv:2006.06624 - drone RGB orthomosaics over Indonesian forest restoration
# concessions). Prototyped below as build_cleared_mask_obia/method="obia" - still uses this
# module's simple row-block splitting rather than a real superpixel-aware tiling strategy,
# so a superpixel straddling a block boundary can still seam; untested against a real
# gigapixel orthophoto. Promote to the default once that's been checked against a real
# result the way OPENING/CLOSING_ITERATIONS above were.


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

        # This block's overlap tail duplicates the start of the next block - same
        # crop-to-core idea detector.detect_trees uses for its point candidates, applied
        # to the mask instead so the seam isn't left jagged/duplicated between blocks.
        core_h = min(block_size, h)
        mask[y:y + core_h, :] = cleared[:core_h, :]

        block_num += 1
        if progress_cb:
            progress_cb(int(block_num / total_blocks * 70))
        y += block_size

    if fill_holes:
        # Done once on the whole assembled mask instead of per-block: a per-block pass
        # can't smooth across a block boundary (each block only sees its own slice), and
        # running it globally is simpler besides - the mask is already small enough (1
        # byte/px) to hold and process whole.
        if progress_cb:
            progress_cb(80)
        mask = ndimage.binary_opening(mask, iterations=OPENING_ITERATIONS)
        mask = ndimage.binary_closing(mask, iterations=CLOSING_ITERATIONS).astype(np.uint8)

    if progress_cb:
        progress_cb(90)
    return mask, info


DEFAULT_SEGMENT_PX = 20  # ~1.2m at this raster's ~0.058m/px - same generalization scale
                          # the OPENING/CLOSING_ITERATIONS comment above targets by hand


def build_cleared_mask_obia(raster_path, exg_threshold=DEFAULT_EXG_THRESHOLD,
                             segment_px=DEFAULT_SEGMENT_PX, compactness=10.0,
                             block_size=3000, overlap=150, progress_cb=None):
    """
    OBIA prototype (see the "ponytail:" note above OPENING_ITERATIONS): segments each
    block into superpixels with skimage's SLIC first, then classifies whole segments by
    mean ExG instead of thresholding pixel-by-pixel - segment boundaries already follow
    real color edges, so this is meant to produce a clearing boundary close to
    build_cleared_mask's post-hoc opening/closing result without a separate smoothing
    pass. Same row-block splitting as build_cleared_mask, so a superpixel that straddles
    a block boundary can still show a seam - untested yet against a real gigapixel
    orthophoto (see the deferred-work note this prototype fulfills).
    """
    info = RasterInfo(raster_path)
    H, W = info.H, info.W
    mask = np.zeros((H, W), dtype=np.uint8)

    total_blocks = max(1, (H + block_size - 1) // block_size)
    block_num = 0
    y = 0
    while y < H:
        h = min(block_size + overlap, H - y)
        rd = read_block(raster_path, info, y, h)
        r, g, b, valid = rd.r, rd.g, rd.b, rd.valid

        exg = 2.0 * g - r - b
        image = np.stack([r, g, b], axis=-1) / 255.0
        n_segments = max(1, (rd.H * rd.W) // (segment_px * segment_px))
        segments = slic(image, n_segments=n_segments, compactness=compactness,
                         mask=valid, start_label=0, channel_axis=-1)

        # Per-segment mean ExG via bincount instead of scipy.ndimage.mean/a Python loop -
        # segments is already a dense 0..num_labels-1 label image (skimage's guarantee),
        # so a weighted bincount is the vectorized equivalent of a groupby-mean.
        num_labels = int(segments.max()) + 1
        sums = np.bincount(segments.ravel(), weights=exg.ravel(), minlength=num_labels)
        counts = np.bincount(segments.ravel(), minlength=num_labels)
        cleared_by_label = (sums / np.maximum(counts, 1)) < exg_threshold
        cleared = cleared_by_label[segments] & valid

        core_h = min(block_size, h)
        mask[y:y + core_h, :] = cleared[:core_h, :]

        block_num += 1
        if progress_cb:
            progress_cb(int(block_num / total_blocks * 90))
        y += block_size

    if progress_cb:
        progress_cb(95)
    return mask, info


def detect_land_clearing(raster_path, output_mask_raster, exg_threshold=DEFAULT_EXG_THRESHOLD,
                          smooth_px=DEFAULT_SMOOTH_PX, fill_holes=True,
                          block_size=3000, overlap=150, progress_cb=None, method="exg"):
    """
    Writes a single-band raster to output_mask_raster (same extent/spatial reference as
    raster_path): 1 = cleared/bare ground, NoData everywhere else (so
    conversion.RasterToPolygon in detect_clearing.py only ever vectorizes the cleared
    class, with nothing else to filter out afterward). Returns output_mask_raster.

    method="exg" (default) is build_cleared_mask; method="obia" is the SLIC-based
    build_cleared_mask_obia prototype - see its docstring.
    """
    if method == "obia":
        mask, info = build_cleared_mask_obia(raster_path, exg_threshold, block_size=block_size,
                                              overlap=overlap, progress_cb=progress_cb)
    else:
        mask, info = build_cleared_mask(raster_path, exg_threshold, smooth_px, fill_holes,
                                         block_size, overlap, progress_cb)
    lower_left = arcpy.Point(info.xmin, info.ymax - info.H * info.px_size)
    raster = arcpy.NumPyArrayToRaster(mask, lower_left, info.px_size, info.px_size, value_to_nodata=0)
    raster.save(output_mask_raster)
    arcpy.management.DefineProjection(output_mask_raster, arcpy.Raster(raster_path).spatialReference)
    return output_mask_raster
