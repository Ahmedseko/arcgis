# Model training notebooks

Not run as part of the add-in - these are Kaggle notebooks (free GPU) for training
models the add-in's Python backend can later load, kept here as the reference recipe
rather than a one-off scratch script (see git history for how each model's defaults were
picked, same as `land_clearing.py`'s own tuning notes).

## `road_segmentation_massachusetts.ipynb`

Trains a small U-Net to predict road **centerlines** (not filled road-surface area) from
1m/px aerial RGB tiles on the [Massachusetts Roads Dataset](https://www.kaggle.com/datasets/balraj98/massachusetts-roads-dataset)
(same resolution class as this project's own real orthophoto - see README's Road/Trail
Extraction accuracy section - and its masks are already road centerlines, not filled
road-surface area like DeepGlobe's). Exports to ONNX (`opset=13`, matching
`sawit_detector.onnx`) so it can plug into `road_extraction.py` via `onnxruntime` -
already a dependency in the `arcgispro-py3` env, no new one needed.

**Not a finished model as-is** - it's only ever seen Massachusetts roads. Fine-tune it on
your own digitized ground truth (`hasil digit/digitasi jalan.shp` + its source
orthophoto, uploaded as a private Kaggle dataset) before trusting it on a real site - see
the notebook's own "Next steps" section at the bottom for exactly how.

To run: upload to [kaggle.com](https://www.kaggle.com/code) (or paste cells into a new
notebook), attach the Massachusetts Roads Dataset as input, set the accelerator to GPU,
Run All.
