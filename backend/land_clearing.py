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

# Was 18 (the QGIS original's own default) until a real Color Reference Sampler dataset
# (2026-08-17, 721 field-collected RGB/ExG samples across 6 categories via this add-in's
# own sampler tool - see TreeCounterDockpaneViewModel.ColorSampler.cs) showed that value
# badly under-catching real clearings: only 48.2% recall against the 110 confirmed
# "Bukaan/tanah terbuka" samples in that set. A threshold sweep against the full labeled
# set (110 positive vs. 587 negative - Forest canopy/Sungai-air/Bangunan-atap/Alat berat)
# found 26 maximizes F1 - recall 92.7%, and precision *also* improves slightly (47.2% vs
# 39.8%) - not a trade-off. Buildings/water remain a real, threshold-independent source of
# false positives at any cutoff (14%/17% of those samples read as "cleared" regardless -
# their RGB genuinely overlaps bare soil's), a separate problem this alone doesn't solve.
DEFAULT_EXG_THRESHOLD = 26
DEFAULT_SMOOTH_PX = 3

# Water (ponds/rivers) also reads as low-ExG "cleared" - it has no vegetation greenness
# either. Distinguished from bare soil the same way fresh_color tells soil apart from
# roads/rivers (see build_cleared_mask's docstring): soil is bright and reddish (r >= b),
# water is dark and neutral-to-blue (b >= r). Unlike fresh_color this isn't optional - a
# pond flagged as "cleared land" is always wrong, not a site-specific precision/recall
# trade-off - so it's applied unconditionally rather than gated behind a CLI flag.
#
# Was 90 (picked by eye, never swept against real data) until the same 721-sample Color
# Reference Sampler dataset used for DEFAULT_EXG_THRESHOLD above (2026-08-17) showed it
# was doing essentially nothing: of the 61 real "Sungai/air" samples that actually read as
# low-ExG "cleared" (i.e. the ones this rule exists to rescue), 90 caught 0 of them - real
# water here tends to be turbid/silty (common for tropical rivers), not the dark clean
# water the old value assumed. Raising to 150 catches 43/61 (70%) with zero real
# "Bukaan/tanah terbuka" samples wrongly excluded, at any value up to 255 in this dataset -
# the b>=r "blueish" check is what's actually doing the discriminating, brightness barely
# matters once past a low floor. The remaining 18/61 water samples read reddish/muddy
# enough (r > b) that no brightness cutoff can save them - same genuine, color-based limit
# as DEFAULT_EXG_THRESHOLD's building/roof note above.
WATER_BRIGHTNESS_MAX = 150.0

# Denoise/generalize passes, applied once to the FULL assembled mask (not per-block - see
# below). Repeated small-kernel passes (scipy's own recommended efficient approach) rather
# than one big structuring element: iterations=N with the default 3x3 cross approximates a
# radius-N diamond at a fraction of the cost of an actual NxN kernel.
#
# OPENING_ITERATIONS was originally 10 (added 2026-07-31 to fix a result that looked "too
# busy/jagged, not like human digitization") - measured against real human-digitized
# ground truth on a 1m/px orthophoto tile on 2026-08-02 (see D:\Data Bukaan\Digitasi Juli
# 2026, the Lampunut tile + its "...Land_Aktif-selesai_Disturb" shapefile), that was
# erasing legitimate small clearings along with noise: recall 73.1%/precision 92.4%
# (F1 81.6%) at 10 vs. recall 80.0%/precision 77.2% (F1 78.6%) with opening removed
# entirely - too much real area lost either way. A sweep over opening=0..10 (closing held
# at 15) found opening=6 Pareto-dominates the original 10 on this ground truth (recall
# 76.2% vs 73.1%, F1 81.9% vs 81.6% - both better, not a trade-off), so that's the new
# default. If a future site's imagery still under- or over-detects, re-run the sweep
# (script left as reference logic, not checked in - see git history for this commit's
# message) against that site's own digitized ground truth rather than guessing a value.
OPENING_ITERATIONS = 6  # first: strip small false "cleared" specks inside real vegetation
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
                        fill_holes=True, fresh_color=False, bright_min=120.0,
                        opening_iterations=OPENING_ITERATIONS, closing_iterations=CLOSING_ITERATIONS,
                        block_size=3000, overlap=150, progress_cb=None):
    """
    Returns (mask, info): mask is a full-raster-size uint8 numpy array (1 = cleared/bare
    ground, indexed [row, col] same as detector.detect_trees' 'py'/'px'), info is the
    RasterInfo. Split out from detect_land_clearing (which additionally writes this to an
    arcpy raster + vectorizes it) so detect.py can reuse just the mask, in-memory, to
    filter out tree candidates that land on bare ground - without needing a raster/
    feature class round trip for that.

    fresh_color, ported from the QGIS original (detector.py's detect_land_clearing):
    also require bright + reddish raw RGB (brightness > bright_min and R >= B) - the
    ExG-only threshold reads roads/rivers as "cleared" too since they also have low
    greenness, but they're darker/bluer than freshly bared soil. Off by default like the
    original; a real accuracy check (2026-08-02, see the "ponytail:" note above
    CLOSING_ITERATIONS) found it a more targeted false-positive filter than blob-size
    erosion (the opening pass removed there) - worth trying on site imagery where
    roads/rivers are inflating false positives.

    opening_iterations/closing_iterations override the OPENING_ITERATIONS/CLOSING_ITERATIONS
    module defaults (tuned against one specific site's ground truth - see that comment) -
    exposed as parameters, not just constants, because a real report (2026-08-16, a
    different site's imagery) found the result "rougher" and missing narrower real
    clearings than the same detection in QGIS: the global defaults don't necessarily suit
    every site's imagery (different camera, lighting, soil color), and there's no ground
    truth for every site to re-run the sweep against. Raising closing smooths/merges
    fragments into more human-digitization-like blobs; lowering opening keeps narrower
    real clearings from being eroded away entirely.
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

        brightness = (r + g + b) / 3.0
        water = (brightness < WATER_BRIGHTNESS_MAX) & (b >= r)
        cleared = valid & (exg < exg_threshold) & ~water
        if fresh_color:
            cleared = cleared & (brightness > bright_min) & (r >= b)

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
        mask = ndimage.binary_opening(mask, iterations=opening_iterations)
        mask = ndimage.binary_closing(mask, iterations=closing_iterations).astype(np.uint8)

    if progress_cb:
        progress_cb(90)
    return mask, info


DEFAULT_SEGMENT_PX = 20  # ~1.2m at this raster's ~0.058m/px - same generalization scale
                          # the OPENING/CLOSING_ITERATIONS comment above targets by hand


def build_cleared_mask_obia(raster_path, exg_threshold=DEFAULT_EXG_THRESHOLD,
                             segment_px=DEFAULT_SEGMENT_PX, compactness=10.0,
                             block_size=3000, overlap=150, progress_cb=None):
    """
    OBIA prototype (see the "ponytail:" note above CLOSING_ITERATIONS): segments each
    block into superpixels with skimage's SLIC first, then classifies whole segments by
    mean ExG instead of thresholding pixel-by-pixel - segment boundaries already follow
    real color edges, so this is meant to produce a clearing boundary close to
    build_cleared_mask's post-hoc closing result without a separate smoothing pass. Same
    row-block splitting as build_cleared_mask, so a superpixel that straddles a block
    boundary can still show a seam - untested yet against a real gigapixel orthophoto
    (see the deferred-work note this prototype fulfills).
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
        # segments is dense 0..num_labels-1 *within the masked region* (skimage's
        # guarantee), but slic(mask=...) labels every pixel OUTSIDE the mask -1, which
        # bincount can't index - drop those before counting (this org's real orthophotos
        # always have some nodata border/edge pixels in at least one block, so this isn't
        # a hypothetical: it crashed the first time this prototype ran against a real
        # raster, 2026-08-16). The final `& valid` already zeroes those pixels out of the
        # result regardless of what cleared_by_label[-1] would otherwise resolve to.
        in_mask = segments >= 0
        num_labels = int(segments.max()) + 1
        sums = np.bincount(segments[in_mask], weights=exg[in_mask], minlength=num_labels)
        counts = np.bincount(segments[in_mask], minlength=num_labels)
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
                          fresh_color=False, bright_min=120.0,
                          opening_iterations=OPENING_ITERATIONS, closing_iterations=CLOSING_ITERATIONS,
                          block_size=3000, overlap=150, progress_cb=None, method="exg"):
    """
    Writes a single-band raster to output_mask_raster (same extent/spatial reference as
    raster_path): 1 = cleared/bare ground, NoData everywhere else (so
    conversion.RasterToPolygon in detect_clearing.py only ever vectorizes the cleared
    class, with nothing else to filter out afterward). Returns output_mask_raster.

    method="exg" (default) is build_cleared_mask; method="obia" is the SLIC-based
    build_cleared_mask_obia prototype - see its docstring. fresh_color/bright_min/
    opening_iterations/closing_iterations only apply to method="exg" - see
    build_cleared_mask's docstring.
    """
    if method == "obia":
        mask, info = build_cleared_mask_obia(raster_path, exg_threshold, block_size=block_size,
                                              overlap=overlap, progress_cb=progress_cb)
    else:
        mask, info = build_cleared_mask(raster_path, exg_threshold, smooth_px, fill_holes,
                                         fresh_color, bright_min, opening_iterations, closing_iterations,
                                         block_size, overlap, progress_cb)
    lower_left = arcpy.Point(info.xmin, info.ymax - info.H * info.px_size)
    raster = arcpy.NumPyArrayToRaster(mask, lower_left, info.px_size, info.px_size, value_to_nodata=0)
    raster.save(output_mask_raster)
    arcpy.management.DefineProjection(output_mask_raster, arcpy.Raster(raster_path).spatialReference)
    return output_mask_raster
