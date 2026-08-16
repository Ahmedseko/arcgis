"""
Long-lived pixel-color sampling worker for the Color Reference Sampler (see
ColorSamplerMapTool.cs / TreeCounterDockpaneViewModel.ColorSampler.cs).

A direct-in-C# raster pixel read (ArcGIS.Core.Data.Raster.Raster.MapToPixel/GetPixelValue,
following Esri's own CustomRasterIdentify sample) crashed ArcGIS Pro outright on the very
first click (real report, 2026-08-16) - most likely because RasterLayer.GetRaster() hands
back the layer's own live rendering raster object rather than an independent one, and this
feature was mutating (SetSpatialReference) and disposing it out from under the renderer.
Rather than keep guessing at ArcGIS.Core.Data.Raster's exact object-lifetime rules with no
stack trace to go on, this reads pixels the same proven way every other raster operation in
this add-in already does (arcpy, in a separate process) - but as one long-lived worker
instead of a fresh subprocess per click (~1-2s of arcpy-import latency on every single
click otherwise), so sampling still feels responsive after the first click pays that cost
once.

Protocol: one "x,y\n" line on stdin per request (map coordinates, same spatial reference as
--raster). Replies on stdout with one "r,g,b\n" line, or "NODATA\n" if the point falls
outside the raster. Runs until stdin closes (EOF) - TreeCounterDockpaneViewModel.
ColorSampler.cs closes the process's standard input on Stop Sampling to end it.

Called by PythonBackendService.cs as: python pixel_sample_server.py --raster <path>
"""
import argparse
import sys

import arcpy


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--raster", required=True)
    args = parser.parse_args()

    raster = arcpy.Raster(args.raster)
    extent = raster.extent
    px_size = raster.meanCellWidth
    xmin, ymax = extent.XMin, extent.YMax
    width, height = raster.width, raster.height

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            x_str, y_str = line.split(",")
            x, y = float(x_str), float(y_str)
            col = int((x - xmin) / px_size)
            row = int((ymax - y) / px_size)
            if col < 0 or row < 0 or col >= width or row >= height:
                print("NODATA", flush=True)
                continue

            lower_left = arcpy.Point(xmin + col * px_size, ymax - (row + 1) * px_size)
            arr = arcpy.RasterToNumPyArray(args.raster, lower_left, 1, 1, nodata_to_value=0)
            if arr.ndim == 3:  # (bands, 1, 1)
                r = int(arr[0, 0, 0])
                g = int(arr[1, 0, 0])
                b = int(arr[2, 0, 0]) if arr.shape[0] > 2 else 0
            else:  # single-band raster
                r = g = b = int(arr[0, 0])
            print(f"{r},{g},{b}", flush=True)
        except Exception as exc:
            print(f"ERROR {exc}", flush=True)

    return 0


if __name__ == "__main__":
    sys.exit(main())
