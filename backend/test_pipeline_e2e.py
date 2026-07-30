"""
End-to-end self-check for the ported ExG detector against a synthetic raster:
proves arcpy.RasterToNumPyArray I/O + detect_trees() actually finds planted
blobs at (roughly) the right place - the CLI smoke test (test_detect.py) only
checks argument parsing, not that the ported algorithm works against a real
arcpy raster. Run with ArcGIS Pro's python:

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" test_pipeline_e2e.py
"""
import os
import tempfile

import arcpy
import numpy as np

from detector import detect_trees

CROWNS_PX = [(80, 80), (200, 200), (300, 100)]
SIZE = 400
PX_SIZE_M = 0.05  # matches REFERENCE_GSD_M so profile params need no rescaling


def _make_synthetic_tif(path):
    arr = np.full((3, SIZE, SIZE), 40, dtype=np.uint8)  # dark bare-soil background
    ys, xs = np.mgrid[0:SIZE, 0:SIZE]
    for cx, cy in CROWNS_PX:
        dist2 = (xs - cx) ** 2 + (ys - cy) ** 2
        blob = np.exp(-dist2 / (2 * 12.0 ** 2))
        arr[1] = np.clip(arr[1] + blob * 180, 0, 255)  # bright green crown
    lower_left = arcpy.Point(0, 0)
    raster = arcpy.NumPyArrayToRaster(arr, lower_left, PX_SIZE_M, PX_SIZE_M)
    raster.save(path)
    arcpy.management.DefineProjection(path, arcpy.SpatialReference(32750))


def test_detects_planted_crowns():
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        _make_synthetic_tif(tif_path)

        trees, info = detect_trees(
            tif_path, sigma_px=12, exg_threshold=30, min_smooth=20,
            mode='forest', min_density=0.0, extra_scales=[])

        assert len(trees) == len(CROWNS_PX), f"expected {len(CROWNS_PX)} trees, got {len(trees)}: {trees}"

        # NumPyArrayToRaster's lower_left places row 0 of the array at the TOP
        # of the raster (arcpy flips Y), so expected py in the detector's
        # top-down pixel space matches the array row index directly.
        found = sorted((t['px'], t['py']) for t in trees)
        expected = sorted(CROWNS_PX)
        for (fx, fy), (ex, ey) in zip(found, expected):
            assert abs(fx - ex) <= 2 and abs(fy - ey) <= 2, f"detected {(fx, fy)} too far from planted {(ex, ey)}"


def test_detects_planted_crowns_across_multiple_blocks():
    # block_size=150 forces several block reads over the 400px-tall raster
    # (default 3000 would cover it in one block, same as the whole-raster
    # path this replaced) - exercises the cross-block dedup/boundary-skip
    # logic that a single-block run can't touch at all. overlap is lowered
    # to keep block_size:overlap in the same ballpark as real usage
    # (3000:150 = 20:1) - the default 150 overlap next to a 100px block_size
    # would eat nearly the whole block via the edge-margin exclusion, which
    # is a real property of the algorithm (matches the original QGIS plugin's
    # block loop) but not one this test is trying to exercise.
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        _make_synthetic_tif(tif_path)

        trees, info = detect_trees(
            tif_path, sigma_px=12, exg_threshold=30, min_smooth=20,
            mode='forest', min_density=0.0, extra_scales=[],
            block_size=150, overlap=20)

        assert len(trees) == len(CROWNS_PX), f"expected {len(CROWNS_PX)} trees, got {len(trees)}: {trees}"

        found = sorted((t['px'], t['py']) for t in trees)
        expected = sorted(CROWNS_PX)
        for (fx, fy), (ex, ey) in zip(found, expected):
            assert abs(fx - ex) <= 2 and abs(fy - ey) <= 2, f"detected {(fx, fy)} too far from planted {(ex, ey)}"


if __name__ == "__main__":
    test_detects_planted_crowns()
    test_detects_planted_crowns_across_multiple_blocks()
    print("OK")
