"""
Smoke test for detect.py's CLI contract (argument parsing, exit codes).
Does not test detection accuracy - just proves the C# <-> Python subprocess wiring
holds together in isolation. Run with ArcGIS Pro's python (needs arcpy import to succeed):

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" test_detect.py
"""
import subprocess
import sys
import tempfile
from pathlib import Path

DETECT_PY = Path(__file__).parent / "detect.py"


def test_missing_raster_fails_with_nonzero_exit():
    with tempfile.TemporaryDirectory() as tmp:
        summary = Path(tmp) / "result.json"
        proc = subprocess.run(
            [sys.executable, str(DETECT_PY), "--raster", "does_not_exist.tif",
             "--profile", "Oil Palm Plantation",
             "--output-fc", str(Path(tmp) / "out.gdb" / "pts"),
             "--summary", str(summary)],
            capture_output=True, text=True,
        )
        assert proc.returncode != 0, proc.stdout + proc.stderr
        assert not summary.exists()


def test_bad_profile_rejected_by_argparse():
    with tempfile.TemporaryDirectory() as tmp:
        summary = Path(tmp) / "result.json"
        proc = subprocess.run(
            [sys.executable, str(DETECT_PY), "--raster", "x.tif",
             "--profile", "Not A Profile",
             "--output-fc", str(Path(tmp) / "out.gdb" / "pts"),
             "--summary", str(summary)],
            capture_output=True, text=True,
        )
        assert proc.returncode != 0
        assert "invalid choice" in proc.stderr.lower()


if __name__ == "__main__":
    test_missing_raster_fails_with_nonzero_exit()
    test_bad_profile_rejected_by_argparse()
    print("OK")
