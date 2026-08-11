"""
U-Net-based road-probability mask - an alternative to land_clearing.py's ExG threshold
as the input road_extraction.py skeletonizes. Trained on the Massachusetts Roads Dataset
(1m/px, centerline masks) via backend/training/road_segmentation_massachusetts.ipynb -
see that notebook + backend/training/README.md for how road_unet.onnx was produced, and
README's Road/Trail Extraction accuracy section for how this compares to the ExG
baseline (F1 60.3%) on real ground truth.

Not fine-tuned on any local imagery yet - a base model trained purely on Massachusetts
roads (see the notebook's own "Next steps" section for fine-tuning it before trusting it
on a real site). Same "onnxruntime, no PyTorch in arcgispro-py3" pattern as
yolo_detector.py's oil-palm model - model_available()/_session_get() mirror that module
directly.
"""
import os

import numpy as np
from scipy.special import expit as sigmoid

from raster_io import RasterInfo, read_block

MODEL_PATH = os.path.join(os.path.dirname(__file__), 'road_unet.onnx')

# Matches the training notebook's CROP_SIZE - the model has only ever seen 512x512
# tiles, so inference runs in windows of the same size rather than feeding it a whole
# multi-thousand-pixel block at once (also keeps CPU memory/time bounded, same reason
# yolo_detector.py windows the oil-palm scan instead of running YOLO on a full raster).
WINDOW_PX = 512
OVERLAP_PX = 64

_session = None


def model_available():
    return os.path.exists(MODEL_PATH)


def _session_get():
    global _session
    if _session is None:
        import onnxruntime as ort
        _session = ort.InferenceSession(MODEL_PATH, providers=['CPUExecutionProvider'])
    return _session


def _predict_probability(r, g, b, valid):
    """
    Runs the U-Net over one block in WINDOW_PX tiles (sliding window - overwrite, not
    blend, on the overlap band; simplest option and consistent with every other
    crop-to-core window loop in this codebase, not a seam-blending pass). Returns a
    float32 road-probability array the same shape as the block.
    """
    H, W = r.shape
    sess = _session_get()
    input_name = sess.get_inputs()[0].name
    prob = np.zeros((H, W), dtype=np.float32)

    stride = max(1, WINDOW_PX - OVERLAP_PX)
    ys = list(range(0, max(H - WINDOW_PX, 0) + 1, stride)) or [0]
    if ys[-1] + WINDOW_PX < H:
        ys.append(max(0, H - WINDOW_PX))
    xs = list(range(0, max(W - WINDOW_PX, 0) + 1, stride)) or [0]
    if xs[-1] + WINDOW_PX < W:
        xs.append(max(0, W - WINDOW_PX))

    for y0 in ys:
        for x0 in xs:
            h = min(WINDOW_PX, H - y0)
            w = min(WINDOW_PX, W - x0)
            if h <= 0 or w <= 0:
                continue
            img = np.stack([
                r[y0:y0 + h, x0:x0 + w],
                g[y0:y0 + h, x0:x0 + w],
                b[y0:y0 + h, x0:x0 + w],
            ], axis=0) / 255.0
            inp = img.astype(np.float32)[None]  # (1, 3, h, w)
            logits = sess.run(None, {input_name: inp})[0][0, 0]  # (h, w)
            prob[y0:y0 + h, x0:x0 + w] = sigmoid(logits)

    return prob * valid


def build_unet_mask(raster_path, threshold=0.5, block_size=3000, overlap=150, progress_cb=None):
    """
    Returns (mask, info): mask is a full-raster-size uint8 array (1 = road pixel per the
    U-Net), same shape/contract/block-loop structure as land_clearing.build_cleared_mask
    so it's a drop-in alternative mask source for road_extraction.py.
    """
    info = RasterInfo(raster_path)
    H, W = info.H, info.W
    mask = np.zeros((H, W), dtype=np.uint8)

    total_blocks = max(1, (H + block_size - 1) // block_size)
    block_num = 0
    y = 0
    while y < H:
        h = min(block_size + overlap, H - y)
        rd = read_block(raster_path, info, y, h)
        prob = _predict_probability(rd.r, rd.g, rd.b, rd.valid)
        road = prob > threshold

        # This block's overlap tail duplicates the start of the next block - same
        # crop-to-core idea land_clearing.py's own block loop uses.
        core_h = min(block_size, h)
        mask[y:y + core_h, :] = road[:core_h, :]

        block_num += 1
        if progress_cb:
            progress_cb(int(block_num / total_blocks * 90))
        y += block_size

    if progress_cb:
        progress_cb(95)
    return mask, info
