"""
End-to-end self-check for land_clearing.detect_land_clearing against a synthetic raster:
proves the mask lands on a known bare/cleared patch and leaves the green background
alone - same style as test_pipeline_e2e.py for the tree detector. Run with ArcGIS Pro's
python:

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" test_land_clearing_e2e.py
"""
import os
import tempfile

import arcpy
import numpy as np

from land_clearing import detect_land_clearing

SIZE = 400
PX_SIZE_M = 0.05
# Bare/cleared rectangle: rows 150-250, cols 100-300 (100x200 px = 5x10m = 50 m2)
CLEARED_ROWS = slice(150, 250)
CLEARED_COLS = slice(100, 300)


def _make_synthetic_tif(path):
    arr = np.zeros((3, SIZE, SIZE), dtype=np.uint8)
    arr[1] = 180  # green background (high ExG: 2*180 - 40 - 40 = 280)
    arr[0] = 40
    arr[2] = 40
    # Bare soil patch: grayish-brown, low ExG (2*110 - 150 - 90 = -20)
    arr[0][CLEARED_ROWS, CLEARED_COLS] = 150
    arr[1][CLEARED_ROWS, CLEARED_COLS] = 110
    arr[2][CLEARED_ROWS, CLEARED_COLS] = 90
    lower_left = arcpy.Point(0, 0)
    raster = arcpy.NumPyArrayToRaster(arr, lower_left, PX_SIZE_M, PX_SIZE_M)
    raster.save(path)
    arcpy.management.DefineProjection(path, arcpy.SpatialReference(32750))


def _assert_mask_matches_patch(mask_path, tolerance=0.02):
    mask = arcpy.RasterToNumPyArray(mask_path, nodata_to_value=0)
    patch = np.zeros((SIZE, SIZE), dtype=bool)
    patch[CLEARED_ROWS, CLEARED_COLS] = True

    # binary_closing (fill_holes) can nibble a couple pixels off the patch edge, and the
    # smoothing/threshold isn't pixel-exact either - allow a small mismatch fraction
    # rather than requiring an exact match.
    mismatched = (mask.astype(bool) != patch).sum()
    fraction = mismatched / patch.size
    assert fraction < tolerance, f"mask differs from planted patch on {fraction:.1%} of pixels"

    # Background well away from the patch (and from the ~15px edge margin the block
    # loop's overlap/crop doesn't touch) must stay unflagged.
    assert mask[20, 20] == 0, "background falsely flagged as cleared"
    assert mask[200, 200] == 1, "patch center not flagged as cleared"


def test_detects_bare_patch():
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        mask_path = os.path.join(tmp, "mask.tif")
        _make_synthetic_tif(tif_path)

        detect_land_clearing(tif_path, mask_path, exg_threshold=18, smooth_px=3)
        _assert_mask_matches_patch(mask_path)


def test_detects_bare_patch_across_multiple_blocks():
    # block_size=150 forces several block reads over the 400px-tall raster (the patch
    # spans rows 150-250, so it straddles a block boundary) - exercises the
    # crop-to-core seam logic a single-block run can't touch at all.
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        mask_path = os.path.join(tmp, "mask.tif")
        _make_synthetic_tif(tif_path)

        detect_land_clearing(tif_path, mask_path, exg_threshold=18, smooth_px=3,
                              block_size=150, overlap=20)
        _assert_mask_matches_patch(mask_path)


def test_detects_bare_patch_obia():
    # Superpixel classification isn't pixel-exact at edges the way the ExG method +
    # morphology is (segments straddling the true boundary vote by majority), so this
    # allows a looser tolerance than the exg-method tests above.
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        mask_path = os.path.join(tmp, "mask.tif")
        _make_synthetic_tif(tif_path)

        detect_land_clearing(tif_path, mask_path, exg_threshold=18, method="obia")
        _assert_mask_matches_patch(mask_path, tolerance=0.08)


if __name__ == "__main__":
    test_detects_bare_patch()
    test_detects_bare_patch_across_multiple_blocks()
    test_detects_bare_patch_obia()
    print("OK")
