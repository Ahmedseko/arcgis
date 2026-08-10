"""
End-to-end self-check for road_extraction.py against a synthetic raster: a straight
bare-soil "road" strip through a green background should skeletonize down to a thin
centerline near the strip's middle row, then vectorize into a small number of
polylines running the length of the strip. Run with ArcGIS Pro's python:

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" test_road_extraction_e2e.py
"""
import os
import tempfile

import arcpy
import numpy as np

from road_extraction import (
    build_road_skeleton, extract_road_skeleton_raster,
    _prune_skeleton_spurs, _remove_wide_regions,
)

SIZE = 400
PX_SIZE_M = 0.05
# Bare/road strip: full width, 20px tall, centered on row 200 - same bare-soil color
# land_clearing.py's own test uses.
ROAD_ROWS = slice(190, 210)


def _make_synthetic_tif(path):
    arr = np.zeros((3, SIZE, SIZE), dtype=np.uint8)
    arr[1] = 180  # green background (high ExG)
    arr[0] = 40
    arr[2] = 40
    arr[0][ROAD_ROWS, :] = 150  # grayish-brown road strip (low ExG)
    arr[1][ROAD_ROWS, :] = 110
    arr[2][ROAD_ROWS, :] = 90
    # Coordinates must fall inside UTM zone 50S's usual easting/northing domain (a file
    # gdb feature class rejects out-of-domain coordinates outright, see
    # test_compare_detections_e2e.py's own note on this) - offset from a realistic false
    # easting/northing instead of the origin.
    raster = arcpy.NumPyArrayToRaster(arr, arcpy.Point(500000, 9200000), PX_SIZE_M, PX_SIZE_M)
    raster.save(path)
    del raster  # release the file lock before this function's caller tries to clean up
    arcpy.management.DefineProjection(path, arcpy.SpatialReference(32750))


def test_remove_wide_regions_drops_quarry_keeps_narrow_road():
    # Pure-numpy mask (no raster round trip needed): a 10px-wide "road" strip connected
    # to an 80x80 "quarry" blob - width_px=40 (a 20px-radius disk) should erase the wide
    # blob's core (and its footprint) while leaving the narrow strip untouched, proving
    # it tells a road apart from adjacent wide bare ground by shape, not just presence.
    mask = np.zeros((200, 200), dtype=bool)
    mask[95:105, :] = True  # 10px-wide horizontal strip (the "road")
    mask[60:140, 150:200] = True  # 80x80 wide blob (the "quarry"), touching the strip

    filtered = _remove_wide_regions(mask, width_px=40)

    assert filtered[100, 10:100].all(), "narrow road strip should survive untouched"
    assert not filtered[100, 170:190].any(), "wide quarry blob should be removed"


def test_prune_removes_short_spur_keeps_real_branch():
    # Pure-numpy skeleton (no raster round trip needed - _prune_skeleton_spurs doesn't
    # touch arcpy): a 100px main line, a 5px "noise" spur off it, and a 20px real branch.
    # Pruning with iterations=8 should erase the 5px spur entirely but leave most of the
    # 20px branch intact (only its tip erodes) - proves it tells noise from a real fork
    # by length, the same problem the 65-fragment real result (2026-08-10) had.
    skeleton = np.zeros((100, 100), dtype=np.uint8)
    skeleton[50, 0:100] = 1  # main line, row 50
    skeleton[45:50, 50] = 1  # 5px noise spur off column 50
    skeleton[50:70, 80] = 1  # 20px real branch off column 80

    pruned = _prune_skeleton_spurs(skeleton, iterations=8)

    assert pruned[45:48, 50].sum() == 0, "short noise spur should be fully removed"
    assert pruned[50, 20:80].sum() > 50, "main line should survive mostly intact"
    assert pruned[50:62, 80].sum() > 10, "real 20px branch should mostly survive, not be treated as noise"


def test_skeleton_follows_strip_centerline():
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        _make_synthetic_tif(tif_path)

        skeleton, _ = build_road_skeleton(tif_path, exg_threshold=18, smooth_px=0)

        ys, xs = np.nonzero(skeleton)
        assert len(ys) > 0, "skeleton found no centerline pixels at all"
        # Should be thin (order of SIZE pixels for a SIZE-long strip), nowhere near the
        # strip's full area (SIZE * 20) - proves it's a line, not still a blob.
        assert len(ys) < SIZE * 3, f"skeleton isn't thin: {len(ys)} pixels"
        # Skeleton pixels should sit near the strip's middle row (200), not its edges.
        assert abs(float(np.mean(ys)) - 200) < 5, f"skeleton centered at row {np.mean(ys):.1f}, expected ~200"


def test_full_pipeline_vectorizes_to_polyline():
    with tempfile.TemporaryDirectory() as tmp:
        tif_path = os.path.join(tmp, "synthetic.tif")
        _make_synthetic_tif(tif_path)

        gdb = os.path.join(tmp, "scratch.gdb")
        arcpy.management.CreateFileGDB(tmp, "scratch.gdb")
        skel_raster = os.path.join(gdb, "RoadSkeleton_tmp")
        out_fc = os.path.join(gdb, "roads")

        # prune_length_m=0 (disabled): this test's own strip spans the raster's full
        # 20m width, so both its ends touch the raster boundary and read as "endpoints"
        # to _prune_skeleton_spurs the same as a real free-hanging spur would - pruning
        # is exercised by its own dedicated unit test above instead, on a synthetic
        # skeleton large enough that a real interior spur is distinguishable from a
        # raster-edge cutoff.
        extract_road_skeleton_raster(tif_path, skel_raster, exg_threshold=18, smooth_px=0, prune_length_m=0)
        arcpy.conversion.RasterToPolyline(skel_raster, out_fc, "ZERO", 5.0, "SIMPLIFY")

        count = int(arcpy.management.GetCount(out_fc)[0])
        assert count > 0, "no road centerline features produced"
        total_length_m = 0.0
        with arcpy.da.SearchCursor(out_fc, ["SHAPE@LENGTH"]) as cursor:
            for (length,) in cursor:
                total_length_m += length
        # The strip spans the full 400px width (20m at 0.05m/px) - allow generous slack
        # for skeletonize endpoint trimming and the min-dangle-length drop.
        assert total_length_m > 10.0, f"total centerline length too short: {total_length_m}m"

        arcpy.management.ClearWorkspaceCache(gdb)


if __name__ == "__main__":
    test_remove_wide_regions_drops_quarry_keeps_narrow_road()
    test_prune_removes_short_spur_keeps_real_branch()
    test_skeleton_follows_strip_centerline()
    test_full_pipeline_vectorizes_to_polyline()
    print("OK")
