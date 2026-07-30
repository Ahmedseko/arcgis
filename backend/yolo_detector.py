"""
Ported from qgis_plugin/tree_counter/yolo_detector.py - only
detect_trees_yolo_primary and its dependencies (_decode_nms, _nms, _dedup,
_preprocess). That is the function actually wired to "Kebun Sawit" (Oil Palm
Plantation) in the QGIS dialog (tree_counter_dialog.py _DetectWorker): YOLO alone as primary
detector, no ExG candidates/sliding-window hybrid - F1 90.4% vs 72.7% for
the older ExG+refine+sliding-window path (qgis_plugin/AGENTS.md, 2026-07-13).
The older hybrid (refine_exg_candidates/sliding_window_scan/validate_trees_yolo)
is intentionally not ported: superseded, more code, worse accuracy.

Crops are sliced straight out of the in-memory RasterData arrays (raster_io.py
loads the whole raster once) instead of re-reading windowed crops from disk
per window like the GDAL original did.
"""
import os
import numpy as np

from raster_io import load_rgb
from detector import REFERENCE_GSD_M

MODEL_PATH = os.path.join(os.path.dirname(__file__), 'sawit_detector.onnx')

_session = None


def model_available():
    return os.path.exists(MODEL_PATH)


def _session_get():
    global _session
    if _session is None:
        import onnxruntime as ort
        _session = ort.InferenceSession(MODEL_PATH, providers=['CPUExecutionProvider'])
    return _session


def _preprocess(crop_rgb):
    from PIL import Image
    img = Image.fromarray(crop_rgb).resize((640, 640), Image.BILINEAR)
    x = np.array(img, dtype=np.float32) / 255.0
    return x.transpose(2, 0, 1)[None]


def _nms(boxes_xyxy, scores, iou_thr):
    """Greedy standard NMS (no cv2). Returns kept indices."""
    x1, y1, x2, y2 = boxes_xyxy[:, 0], boxes_xyxy[:, 1], boxes_xyxy[:, 2], boxes_xyxy[:, 3]
    areas = (x2 - x1) * (y2 - y1)
    order = scores.argsort()[::-1]
    keep = []
    while order.size > 0:
        i = order[0]
        keep.append(i)
        xx1 = np.maximum(x1[i], x1[order[1:]])
        yy1 = np.maximum(y1[i], y1[order[1:]])
        xx2 = np.minimum(x2[i], x2[order[1:]])
        yy2 = np.minimum(y2[i], y2[order[1:]])
        w = np.maximum(0.0, xx2 - xx1)
        h = np.maximum(0.0, yy2 - yy1)
        inter = w * h
        iou = inter / (areas[i] + areas[order[1:]] - inter)
        order = order[1:][iou <= iou_thr]
    return keep


def _decode_nms(raw, conf_thr, iou_thr=0.45):
    """Decode YOLOv8 ONNX output (1, 5, 8400) + NMS -> list (cx, cy, score) in 640-space."""
    pred = raw[0]
    scores = pred[4]
    mask = scores > conf_thr
    if not mask.any():
        return []
    cx, cy, w, h = pred[0, mask], pred[1, mask], pred[2, mask], pred[3, mask]
    boxes = np.stack([cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2], axis=1)
    keep = _nms(boxes, scores[mask], iou_thr)
    if not keep:
        return []
    keep = np.array(keep)
    return list(zip(cx[keep], cy[keep], scores[mask][keep]))


def _dedup(trees, radius_factor=0.3, floor_px=48):
    """
    Remove duplicates via connected-components clustering (see qgis_plugin
    yolo_detector.py._dedup docstring for why greedy pairwise NMS fails on
    chains of nearby points - unchanged here). Pair radius = smaller sigma
    of the two points x radius_factor, floored at floor_px.
    """
    if len(trees) < 2:
        return trees
    from scipy.spatial import cKDTree
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components

    n = len(trees)
    coords = np.array([[t['px'], t['py']] for t in trees], dtype=np.float32)
    sigmas = np.array([t.get('sigma', 20) for t in trees], dtype=np.float32)

    max_r = max(float(np.max(sigmas)) * radius_factor, floor_px)
    kd = cKDTree(coords)
    candidate_pairs = kd.query_pairs(r=max_r, output_type='ndarray')

    rows, cols = [], []
    for i, j in candidate_pairs:
        pair_r = max(min(sigmas[i], sigmas[j]) * radius_factor, floor_px)
        dist = float(np.sqrt(np.sum((coords[i] - coords[j]) ** 2)))
        if dist < pair_r:
            rows.append(i)
            cols.append(j)

    graph = coo_matrix((np.ones(len(rows)), (rows, cols)), shape=(n, n))
    n_components, labels = connected_components(graph, directed=False)

    keep_idx = []
    for c in range(n_components):
        members = np.where(labels == c)[0]
        best = members[np.argmax([trees[k].get('score', 0) for k in members])]
        keep_idx.append(int(best))
    return [trees[i] for i in keep_idx]


def detect_trees_yolo_primary(raster_path, conf_threshold=0.25, progress_cb=None):
    """
    YOLO as the primary detector for Kebun Sawit: ~1024px physical windows
    (scaled to image resolution), 200px overlap so crowns on window borders
    stay whole in at least one window, per-window NMS + connected-components
    dedup across windows.
    """
    rd = load_rgb(raster_path)
    r, g, b = rd.r, rd.g, rd.b
    H, W = rd.H, rd.W
    px_size = rd.px_size

    scale = REFERENCE_GSD_M / px_size if px_size > 0 else 1.0
    sigma_eff = max(1, int(round(20 * scale)))

    win = max(256, int(round(1024 * scale)))
    overlap = int(round(200 * scale))
    stride = max(1, win - overlap)

    xs = list(range(0, max(W - win, 0) + 1, stride))
    if xs[-1] + win < W:
        xs.append(W - win)
    ys = list(range(0, max(H - win, 0) + 1, stride))
    if ys[-1] + win < H:
        ys.append(H - win)
    windows = [(max(0, xo), max(0, yo)) for yo in ys for xo in xs]

    sess = _session_get()
    trees = []
    total = max(len(windows), 1)
    for idx, (xo, yo) in enumerate(windows):
        cw = min(win, W - xo)
        ch = min(win, H - yo)
        if cw < 64 or ch < 64:
            continue
        crop = np.stack([
            r[yo:yo + ch, xo:xo + cw],
            g[yo:yo + ch, xo:xo + cw],
            b[yo:yo + ch, xo:xo + cw],
        ], axis=-1).astype(np.uint8)
        raw = sess.run(None, {sess.get_inputs()[0].name: _preprocess(crop)})[0]
        for cx640, cy640, score in _decode_nms(raw, conf_threshold):
            px = int(round(xo + cx640 * (cw / 640.0)))
            py = int(round(yo + cy640 * (ch / 640.0)))
            geo_x, geo_y = rd.geo_xy(px, py)
            trees.append({
                'px': px, 'py': py,
                'geo_x': geo_x, 'geo_y': geo_y,
                'sigma': sigma_eff,
                'crown_r': round(sigma_eff * px_size, 2),
                'score': float(score),
                'source': 'yolo',
            })
        if progress_cb:
            progress_cb(int((idx + 1) / total * 100))

    trees = _dedup(trees, radius_factor=0.0, floor_px=max(1, int(round(50 * scale))))
    return trees, {'xmin': rd.xmin, 'ymax': rd.ymax, 'px_size': px_size}
