"""
Shared raster loading for detector.py / yolo_detector.py.
"""
import arcpy
import numpy as np


class RasterInfo:
    """Cheap metadata-only describe - no pixel data read."""
    def __init__(self, raster_path):
        raster = arcpy.Raster(raster_path)
        extent = raster.extent
        px_size = raster.meanCellWidth
        if px_size < 0.01:  # geographic CRS (degrees) -> approximate meters
            px_size *= 111320.0
        self.xmin, self.ymax, self.px_size = extent.XMin, extent.YMax, px_size
        self.W, self.H = raster.width, raster.height
        self.band_count = raster.bandCount


class RasterData:
    def __init__(self, r, g, b, valid, xmin, ymax, px_size):
        self.r, self.g, self.b, self.valid = r, g, b, valid
        self.xmin, self.ymax, self.px_size = xmin, ymax, px_size
        self.H, self.W = r.shape

    def geo_xy(self, px, py):
        return self.xmin + px * self.px_size, self.ymax - py * self.px_size


def _to_raster_data(arr, xmin, ymax, px_size):
    if arr.ndim != 3 or arr.shape[0] < 3:
        raise ValueError("Raster must have at least 3 bands (RGB)")
    r, g, b = arr[0], arr[1], arr[2]
    if arr.shape[0] >= 4:
        valid = arr[3] > 0
    else:
        valid = (r > 0) | (g > 0) | (b > 0)
        # exclude white nodata padding (R=G=B~255) on clipped TIFs
        valid = valid & ~((r > 240) & (g > 240) & (b > 240))
    return RasterData(r, g, b, valid, xmin, ymax, px_size)


def load_rgb(raster_path):
    # arcpy.Describe() only exposes meanCellWidth/meanCellHeight on a raster's
    # per-band children, not the top-level RasterDataset describe object -
    # arcpy.Raster(path) has them directly as properties.
    info = RasterInfo(raster_path)
    arr = arcpy.RasterToNumPyArray(raster_path, nodata_to_value=0).astype(np.float32)
    return _to_raster_data(arr, info.xmin, info.ymax, info.px_size)


def read_block(raster_path, info: RasterInfo, y_offset: int, block_h: int):
    """
    Read the horizontal strip of rows [y_offset, y_offset + actual_h) as a
    RasterData whose own (xmin, ymax) already accounts for the y_offset, so
    rd.geo_xy(px_local, py_local) returns correct absolute map coordinates
    directly from block-local pixel indices - callers only need y_offset
    themselves for cross-block bookkeeping (dedup keys, edge checks), not for
    geo conversion.

    arcpy.RasterToNumPyArray windows by map coordinate (lower-left corner),
    not row/col offset like GDAL's ReadAsArray(xoff, yoff, ...) - this
    converts the row offset to the matching lower-left Y once per block.
    """
    actual_h = min(block_h, info.H - y_offset)
    lower_left_y = info.ymax - (y_offset + actual_h) * info.px_size
    lower_left = arcpy.Point(info.xmin, lower_left_y)
    arr = arcpy.RasterToNumPyArray(
        raster_path, lower_left, info.W, actual_h, nodata_to_value=0
    ).astype(np.float32)
    block_ymax = info.ymax - y_offset * info.px_size
    return _to_raster_data(arr, info.xmin, block_ymax, info.px_size)
