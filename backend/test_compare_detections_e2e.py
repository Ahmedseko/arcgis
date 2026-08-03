"""
End-to-end self-check for compare_detections.py's arcpy plumbing (feature class in,
feature class out) - detector.compare_detections() itself is pure-Python matching
logic, already exercised indirectly here. Run with ArcGIS Pro's python:

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" test_compare_detections_e2e.py
"""
import os
import tempfile

import arcpy

from compare_detections import _read_points, _write_points
from detector import compare_detections

SR = arcpy.SpatialReference(32750)


def _make_point_fc(gdb, name, points):
    fc = os.path.join(gdb, name)
    arcpy.management.CreateFeatureclass(gdb, name, "POINT", spatial_reference=SR)
    with arcpy.da.InsertCursor(fc, ["SHAPE@XY"]) as cursor:
        for xy in points:
            cursor.insertRow([xy])
    return fc


def test_compare_roundtrip():
    with tempfile.TemporaryDirectory() as tmp:
        gdb = os.path.join(tmp, "scratch.gdb")
        arcpy.management.CreateFileGDB(tmp, "scratch.gdb")

        # Coordinates must fall inside UTM zone 50S's usual easting/northing domain (a
        # file gdb feature class rejects out-of-domain coordinates outright) - offset
        # from a realistic false easting/northing instead of the origin.
        BASE = (500000, 9200000)

        def pt(dx, dy):
            return (BASE[0] + dx, BASE[1] + dy)

        # pt(0,0) and pt(100,100) survive unchanged, pt(50,50) is felled, pt(200,200) is new.
        old_fc = _make_point_fc(gdb, "old_pts", [pt(0, 0), pt(100, 100), pt(50, 50)])
        new_fc = _make_point_fc(gdb, "new_pts", [pt(0.5, 0.5), pt(100, 99.5), pt(200, 200)])

        old_points = _read_points(old_fc, SR)
        new_points = _read_points(new_fc, SR)
        result = compare_detections(old_points, new_points, max_dist_m=3.0)

        assert result["matched"] == 2, result
        assert len(result["lost"]) == 1, result
        assert len(result["new"]) == 1, result

        lost_fc = os.path.join(gdb, "lost_pts")
        new_out_fc = os.path.join(gdb, "changed_pts")
        _write_points(lost_fc, result["lost"], SR)
        _write_points(new_out_fc, result["new"], SR)

        assert int(arcpy.management.GetCount(lost_fc)[0]) == 1
        assert int(arcpy.management.GetCount(new_out_fc)[0]) == 1

        # arcpy keeps a schema lock on the gdb from the cursors/CreateFeatureclass calls
        # above even after their own `with` blocks exit - without this, tempdir cleanup
        # fails to delete the (still-locked) .gdb on Windows.
        arcpy.management.ClearWorkspaceCache(gdb)


if __name__ == "__main__":
    test_compare_roundtrip()
    print("OK")
