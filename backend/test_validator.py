"""
Self-check for validator._crop_jpeg_b64 (pure image cropping/overlay, no network -
_ask_gemini/validate_trees need a real API key so they're not covered here).
Run with any Python that has numpy + Pillow (doesn't need arcpy):

    "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" test_validator.py
"""
import base64
import numpy as np

from validator import _crop_jpeg_b64


class _FakeRasterData:
    def __init__(self, size=200):
        self.r = np.full((size, size), 40, dtype=np.float32)
        self.g = np.full((size, size), 180, dtype=np.float32)
        self.b = np.full((size, size), 40, dtype=np.float32)
        self.H, self.W = size, size


def test_crop_returns_valid_jpeg_b64():
    rd = _FakeRasterData()
    b64 = _crop_jpeg_b64(rd, px=100, py=100, pad_px=50)
    assert b64 is not None
    raw = base64.b64decode(b64)
    assert raw[:2] == b"\xff\xd8"  # JPEG magic bytes


def test_crop_near_edge_still_valid():
    rd = _FakeRasterData()
    b64 = _crop_jpeg_b64(rd, px=5, py=5, pad_px=50)
    assert b64 is not None


def test_crop_too_small_returns_none():
    rd = _FakeRasterData(size=20)
    b64 = _crop_jpeg_b64(rd, px=0, py=0, pad_px=2)
    assert b64 is None


if __name__ == "__main__":
    test_crop_returns_valid_jpeg_b64()
    test_crop_near_edge_still_valid()
    test_crop_too_small_returns_none()
    print("OK")
