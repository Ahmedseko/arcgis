"""
Writes buffered Color Reference Sampler points to a point feature class - see
ColorSamplerMapTool.cs (reads the RGB pixel per click, in C#) and
TreeCounterDockpaneViewModel.ColorSampler.cs (buffers clicks in memory, flushes them here
once on Stop Sampling).

Deliberately a plain CreateFeatureclass + InsertCursor script (same proven pattern as
detect.py's _write_feature_class) rather than doing this in C# via the DDL/SchemaBuilder
API - a first attempt at building the feature class directly in C# crashed ArcGIS Pro
outright at creation time, before a single point was ever added (real report, 2026-08-16).
Whatever the exact native cause, this sidesteps that code path entirely by reusing the
same arcpy-based creation every other feature class in this add-in already goes through
without issue.

Called by TreeCounterAddin/PythonBackendService.cs as:
    python save_color_samples.py --reference-raster <path> --output-fc <fc path> \
        --samples-json <path> --summary <path>

--samples-json: [{"x": .., "y": .., "r": .., "g": .., "b": .., "exg": .., "cls": ..}, ...] -
x/y already in the same spatial reference as --reference-raster (the map view's own SR at
click time, which is what this add-in's other detection results are already created in
too). "cls" is the user's currently-selected sample category (see SampleCategories in
TreeCounterDockpaneViewModel.ColorSampler.cs) - may be "" for older/uncategorized samples.

Writes --summary: {"output_fc": str, "count": int}
"""
import argparse
import json
import sys

import arcpy


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference-raster", required=True,
                         help="Raster whose spatial reference the new points are created in")
    parser.add_argument("--output-fc", required=True)
    parser.add_argument("--samples-json", required=True)
    parser.add_argument("--summary", required=True)
    args = parser.parse_args()

    try:
        with open(args.samples_json, "r", encoding="utf-8") as f:
            samples = json.load(f)

        out_path, out_name = (args.output_fc.rsplit("\\", 1) if "\\" in args.output_fc
                               else args.output_fc.rsplit("/", 1))
        sr = arcpy.Describe(args.reference_raster).spatialReference
        arcpy.management.CreateFeatureclass(out_path, out_name, "POINT", spatial_reference=sr)
        arcpy.management.AddField(args.output_fc, "Label", "TEXT", field_length=254)
        arcpy.management.AddField(args.output_fc, "Class", "TEXT", field_length=60)
        arcpy.management.AddField(args.output_fc, "R", "LONG")
        arcpy.management.AddField(args.output_fc, "G", "LONG")
        arcpy.management.AddField(args.output_fc, "B", "LONG")
        arcpy.management.AddField(args.output_fc, "ExG", "DOUBLE")

        fields = ["SHAPE@XY", "Label", "Class", "R", "G", "B", "ExG"]
        with arcpy.da.InsertCursor(args.output_fc, fields) as cursor:
            for s in samples:
                cursor.insertRow(((s["x"], s["y"]), "", s.get("cls", ""), s["r"], s["g"], s["b"], s["exg"]))
        count = len(samples)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    with open(args.summary, "w", encoding="utf-8") as f:
        json.dump({"output_fc": args.output_fc, "count": count}, f)
    return 0


if __name__ == "__main__":
    sys.exit(main())
