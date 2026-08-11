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
    _drop_short_bridges, _remove_wide_regions,
)

SR = arcpy.SpatialReference(32750)

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


def test_drop_short_bridges_removes_bridge_keeps_dangles():
    # A real result (2026-08-11) showed RasterToPolyline's own minimum_dangle_length
    # doesn't touch a short segment bridging two junctions (only free-hanging dangles) -
    # a raster/pixel-level fix for this (checked in briefly, then reverted) turned out
    # unreliable (see road_extraction.py's module docstring on why), so this operates on
    # the vectorized output instead: junction A --3m bridge-- junction B, each also
    # connected to a long (50m) dangling arm plus junction A has an extra 30m branch.
    # Only the 3m bridge should be deleted - the dangling arms/branch survive untouched
    # (matching RasterToPolyline's own dangle-keeping behavior for anything long enough).
    with tempfile.TemporaryDirectory() as tmp:
        gdb = os.path.join(tmp, "scratch.gdb")
        arcpy.management.CreateFileGDB(tmp, "scratch.gdb")
        fc = os.path.join(gdb, "roads")
        arcpy.management.CreateFeatureclass(gdb, "roads", "POLYLINE", spatial_reference=SR)

        BASE = (500000, 9200000)
        a = arcpy.Point(*BASE)
        b = arcpy.Point(BASE[0] + 3, BASE[1])
        left_end = arcpy.Point(BASE[0] - 50, BASE[1])
        right_end = arcpy.Point(BASE[0] + 53, BASE[1])
        branch_end = arcpy.Point(BASE[0], BASE[1] + 30)

        lines = {
            "main_left": (left_end, a),
            "main_right": (b, right_end),
            "bridge": (a, b),
            "long_branch": (a, branch_end),
        }
        with arcpy.da.InsertCursor(fc, ["SHAPE@"]) as cursor:
            for p0, p1 in lines.values():
                cursor.insertRow([arcpy.Polyline(arcpy.Array([p0, p1]), SR)])

        _drop_short_bridges(fc, min_length_m=5.0)

        assert int(arcpy.management.GetCount(fc)[0]) == 3, "only the 3m bridge should be deleted"
        total_length_m = 0.0
        with arcpy.da.SearchCursor(fc, ["SHAPE@LENGTH"]) as cursor:
            for (length,) in cursor:
                total_length_m += length
        assert abs(total_length_m - 130.0) < 0.01, f"remaining 3 lines should total 130m, got {total_length_m}"

        arcpy.management.ClearWorkspaceCache(gdb)


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

        extract_road_skeleton_raster(tif_path, skel_raster, exg_threshold=18, smooth_px=0)
        arcpy.conversion.RasterToPolyline(skel_raster, out_fc, "ZERO", 5.0, "SIMPLIFY")
        _drop_short_bridges(out_fc, min_length_m=5.0)

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
    test_drop_short_bridges_removes_bridge_keeps_dangles()
    test_skeleton_follows_strip_centerline()
    test_full_pipeline_vectorizes_to_polyline()
    print("OK")
