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
    # A real-ish UTM Zone 50S location, not (0, 0) - only matters for the newer
    # exclude-fc test below, which writes a real feature class (CreateFeatureclass):
    # a brand-new fc's default XY domain for a real projected CRS doesn't necessarily
    # span all the way down to (0, 0), which isn't a real location any actual drone
    # orthophoto would ever be at anyway. The other tests only check relative pixel
    # positions (px/py), never geo_x/geo_y, so this has no effect on them.
    lower_left = arcpy.Point(500_000, 9_500_000)
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


def test_exclude_blurry_drops_smooth_crown_keeps_textured_one():
    # A smooth Gaussian blob (same shape _make_synthetic_tif's crowns already use) has
    # almost no high-frequency detail - a stand-in for a blurred/stitching-seam region.
    # Real canopy texture (leaves/fronds) gives much higher local Laplacian variance,
    # simulated here as noise confined to the crown. See detector.BLUR_VARIANCE_MIN.
    SHARP_CROWN = (150, 150)
    BLURRY_CROWN = (250, 250)
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")

        rng = np.random.default_rng(0)
        arr = np.full((3, SIZE, SIZE), 40, dtype=np.uint8)
        ys, xs = np.mgrid[0:SIZE, 0:SIZE]
        for (cx, cy), textured in [(SHARP_CROWN, True), (BLURRY_CROWN, False)]:
            dist2 = (xs - cx) ** 2 + (ys - cy) ** 2
            blob = np.exp(-dist2 / (2 * 12.0 ** 2))
            green = blob * 180
            if textured:
                green = green + rng.uniform(-40, 40, size=(SIZE, SIZE)) * (blob > 0.05)
            arr[1] = np.clip(arr[1] + green, 0, 255)
        raster = arcpy.NumPyArrayToRaster(arr, arcpy.Point(0, 0), PX_SIZE_M, PX_SIZE_M)
        raster.save(tif_path)
        del raster  # drop the file handle now - see test_land_clearing_e2e.py's own
                    # note on this, same Windows file-lock-on-cleanup issue.
        arcpy.management.DefineProjection(tif_path, arcpy.SpatialReference(32750))

        trees, _ = detect_trees(
            tif_path, sigma_px=12, exg_threshold=30, min_smooth=20,
            mode='forest', min_density=0.0, extra_scales=[], exclude_blurry=True)

        found = [(t['px'], t['py']) for t in trees]
        assert any(abs(px - SHARP_CROWN[0]) <= 2 and abs(py - SHARP_CROWN[1]) <= 2 for px, py in found), \
            f"textured/sharp crown should still be detected: {found}"
        assert not any(abs(px - BLURRY_CROWN[0]) <= 2 and abs(py - BLURRY_CROWN[1]) <= 2 for px, py in found), \
            f"smooth/blurry crown should be filtered out: {found}"


def test_detect_cli_exclude_fc_removes_points_inside_polygon():
    # CLI-level check (subprocess, real detect.py) for the --exclude-fc erase path
    # (2026-08-17, parity with detect_clearing.py's own --exclude-fc) - detector.py's
    # own detect_trees() is already covered by the tests above, this one exercises the
    # new PairwiseErase + before/after count arithmetic in detect.py's main() instead.
    import json
    import subprocess
    import sys

    DETECT_PY = os.path.join(os.path.dirname(__file__), "detect.py")

    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        _make_synthetic_tif(tif_path)

        trees, _ = detect_trees(
            tif_path, sigma_px=12, exg_threshold=30, min_smooth=20,
            mode='forest', min_density=0.0, extra_scales=[])
        assert len(trees) == len(CROWNS_PX)

        gdb = os.path.join(tmp, "scratch.gdb")
        arcpy.management.CreateFileGDB(tmp, "scratch.gdb")
        sr = arcpy.SpatialReference(32750)

        # A 2m circle around one real detected tree's own geo coordinates - guaranteed to
        # cover exactly that one point, unlike guessing world coords from CROWNS_PX by hand.
        target = trees[0]
        exclude_fc = os.path.join(gdb, "exclude_area")
        circle = arcpy.PointGeometry(arcpy.Point(target["geo_x"], target["geo_y"]), sr).buffer(2.0)
        arcpy.management.CopyFeatures([circle], exclude_fc)

        output_fc = os.path.join(gdb, "detected_pts")
        summary_path = os.path.join(tmp, "summary.json")
        proc = subprocess.run(
            [sys.executable, DETECT_PY, "--raster", tif_path, "--profile", "Natural Forest",
             "--output-fc", output_fc, "--summary", summary_path,
             "--sigma", "12", "--exg-threshold", "30", "--min-smooth", "20",
             "--exclude-fc", exclude_fc],
            capture_output=True, text=True,
        )
        assert proc.returncode == 0, proc.stdout + proc.stderr

        with open(summary_path) as f:
            summary = json.load(f)
        assert summary["excluded_by_area_count"] == 1, summary
        assert summary["tree_count"] == len(CROWNS_PX) - 1, summary
        assert int(arcpy.management.GetCount(output_fc)[0]) == len(CROWNS_PX) - 1

        # Same Windows file-lock-on-cleanup issue as the raster `del` elsewhere in this
        # file, gdb-schema-lock flavor: this process's own arcpy connection (CopyFeatures/
        # GetCount above) keeps scratch.gdb's .lock file open otherwise, and the `with`
        # block's cleanup can't delete a locked file.
        arcpy.management.ClearWorkspaceCache(gdb)


if __name__ == "__main__":
    test_detects_planted_crowns()
    test_detects_planted_crowns_across_multiple_blocks()
    test_exclude_blurry_drops_smooth_crown_keeps_textured_one()
    test_detect_cli_exclude_fc_removes_points_inside_polygon()
    print("OK")
