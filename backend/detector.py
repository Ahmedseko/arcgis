"""
Ported from qgis_plugin/tree_counter/detector.py (detect_trees, _global_nms,
_refine_centroid, compare_detections). Core ExG/matched-filter math and the
block-processing loop (block_size/overlap) are unchanged - only raster I/O
swapped from GDAL windowed ReadAsArray to arcpy windowed RasterToNumPyArray
(see raster_io.read_block). An earlier version of this port loaded the whole
raster into memory in one call instead of blocking - measurably much slower
than the QGIS plugin on large real orthophotos (a big Gaussian filter over
one huge array thrashes CPU cache; many smaller ones don't), so it's back to
matching the original block loop.

Not ported: detect_land_clearing, compute_heterogeneity_raster - not in
scope for the ArcGIS add-in yet (only tree detection was requested).
"""
import numpy as np
from scipy import ndimage

from raster_io import RasterInfo, read_block

REFERENCE_GSD_M = 0.05

PROFILES = {
    'forest': dict(sigma_px=75, exg_threshold=18, min_smooth=10, min_density=0.45, extra_scales=[], refine_radius=None),
    'palm':   dict(sigma_px=20, exg_threshold=10, min_smooth=30, min_density=0.0,  extra_scales=[12], refine_radius=20),
}


def _global_nms(trees, min_dist_px=None):
    """
    Greedy NMS: drop points within radius of an already-kept point, in list
    order. If min_dist_px is None, use each point's own radius ('sigma' field)
    - used for multi-scale NMS: sort large->small scale before calling this,
    so large-scale detections suppress nearby small-scale ones (instead of
    being treated as separate trees).
    """
    if len(trees) <= 1:
        return trees
    try:
        from scipy.spatial import cKDTree
        coords = np.array([[t['px'], t['py']] for t in trees], dtype=np.float32)
        kd = cKDTree(coords)
        keep = np.ones(len(trees), dtype=bool)
        for i in range(len(trees)):
            if not keep[i]:
                continue
            radius = min_dist_px if min_dist_px is not None else trees[i].get('sigma', 1)
            neighbors = kd.query_ball_point(coords[i], radius)
            for j in neighbors:
                if j != i:
                    keep[j] = False
    except Exception:
        return trees
    return [t for t, k in zip(trees, keep) if k]


def _refine_centroid(veg_mask, exg, px_i, py_i, radius):
    """
    Shift the point from the raw matched-filter peak to the vegetation
    (ExG) mass centroid in the surrounding window - the peak from the
    Gaussian blur can be slightly off from the actual crown shape,
    especially near neighboring crowns; a mass-based centroid hugs the
    visual crown center more closely.

    A flat-weight square window (previous approach) could pick up part of
    a NEIGHBORING crown at the window corners in dense plantations, pulling
    the point away from the true crown center - confirmed via ground-truth
    evaluation (2026-07-04), some position errors >2m. Added Gaussian decay
    weighting by distance from the original peak, so pixels near the window
    edge/corner (more likely to belong to a neighbor) contribute far less
    than pixels near the center.
    """
    h, w = veg_mask.shape
    y0, y1 = max(0, py_i - radius), min(h, py_i + radius + 1)
    x0, x1 = max(0, px_i - radius), min(w, px_i + radius + 1)
    ys, xs = np.mgrid[y0:y1, x0:x1]
    dist2 = (xs - px_i) ** 2 + (ys - py_i) ** 2
    # decay sigma = radius/2 -> weight at the window edge (dist=radius) drops
    # to ~e^-2 (~13%) of the center weight, enough to damp neighbor
    # contributions without losing the crown's own edge information.
    decay = np.exp(-dist2 / (2 * (radius / 2.0) ** 2))
    local_w = np.clip(exg[y0:y1, x0:x1], 0, None) * veg_mask[y0:y1, x0:x1] * decay
    total = local_w.sum()
    if total <= 0:
        return px_i, py_i
    cy = float((ys * local_w).sum() / total)
    cx = float((xs * local_w).sum() / total)
    return int(round(cx)), int(round(cy))


def detect_trees(raster_path, sigma_px=75, exg_threshold=25, min_smooth=15,
                  mode='forest', block_size=3000, overlap=150, progress_cb=None,
                  min_density=None, extra_scales=None):
    """
    Returns (trees, raster_info)
    trees: list of dict {px, py, geo_x, geo_y, crown_r, sigma}
    raster_info: dict {xmin, ymax, px_size}

    Processes the raster in horizontal strips (block_size rows + overlap) so
    memory and the Gaussian filter's working set stay bounded regardless of
    raster size - matching qgis_plugin's GDAL block loop instead of loading
    the whole image into one array (see raster_io.read_block).
    """
    profile_defaults = PROFILES.get(mode, PROFILES['forest'])
    if min_density is None:
        min_density = profile_defaults.get('min_density', 0.45)
    if extra_scales is None:
        extra_scales = profile_defaults.get('extra_scales', [])
    refine_radius = profile_defaults.get('refine_radius', None)

    info = RasterInfo(raster_path)
    H, W, px_size = info.H, info.W, info.px_size

    # Auto-scale spatial parameters to the image's actual resolution (see
    # REFERENCE_GSD_M). sigma_px/extra_scales/refine_radius come in as
    # "sigma at the reference GSD of 5 cm/px"; multiply by scale so the
    # PHYSICAL crown size being searched for stays the same regardless of
    # image resolution. At 5 cm/px, scale=1.0 -> exact no-op.
    scale = REFERENCE_GSD_M / px_size if px_size > 0 else 1.0
    sigma_px = max(1, int(round(sigma_px * scale)))
    extra_scales = [max(1, int(round(s * scale))) for s in extra_scales]
    if refine_radius is not None:
        refine_radius = max(1, int(round(refine_radius * scale)))

    scales = sorted(set([sigma_px] + list(extra_scales)), reverse=True)
    fp_by_scale = {s: np.ones((s * 2 + 1, s * 2 + 1), dtype=bool) for s in scales}
    crown_r_by_scale = {s: round(s * px_size, 2) for s in scales}

    trees = []
    seen = set()
    total_blocks = max(1, (H + block_size - 1) // block_size)
    block_num = 0
    y = 0

    while y < H:
        h = min(block_size + overlap, H - y)
        rd = read_block(raster_path, info, y, h)
        r, g, b, valid = rd.r, rd.g, rd.b, rd.valid

        exg = 2.0 * g - r - b
        veg_mask = (valid & (exg > exg_threshold)).astype(np.float32)

        for s in scales:
            # Keep candidates away from the edge of nodata holes inside the
            # canopy (photo-stitching artifacts) AND from the outer edge of
            # this block - scipy's 'reflect' padding can produce false signal
            # right at the first/last row or column, and crowns cut by the
            # edge don't have an intact true center.
            margin_px = max(15, s // 4)
            dist_edge = ndimage.distance_transform_edt(valid)
            valid_core = dist_edge > margin_px
            valid_core[:margin_px, :] = False
            valid_core[-margin_px:, :] = False
            valid_core[:, :margin_px] = False
            valid_core[:, -margin_px:] = False

            signal = ndimage.gaussian_filter(np.where(veg_mask > 0, exg, 0.0), sigma=s)

            # Vegetation density around the point (~1.5x crown radius). For
            # forest, filters out isolated shrub/regrowth clumps in cleared
            # areas (low density). For palm, min_density defaults to 0 so this
            # gate is effectively inactive - palm crowns are legitimately
            # spaced with visible bare ground around them, not a sign of trouble.
            density = ndimage.uniform_filter(veg_mask, size=int(s * 3))
            density_ok = density > min_density

            lmax = (
                (signal == ndimage.maximum_filter(signal, footprint=fp_by_scale[s]))
                & valid_core
                & (signal > min_smooth)
                & density_ok
            )

            labeled, n = ndimage.label(lmax)
            if n == 0:
                continue
            for py_l, px_l in ndimage.center_of_mass(lmax, labeled, range(1, n + 1)):
                py_i, px_i = int(py_l), int(px_l)
                # Shift to the vegetation mass centroid - hugs the visual
                # crown center more closely than the raw Gaussian-blur peak.
                r_refine = refine_radius if refine_radius is not None else s
                px_i, py_i = _refine_centroid(veg_mask, exg, px_i, py_i, r_refine)
                py_abs = py_i + y

                # This block's overlap tail duplicates the start of the next
                # block - skip candidates landing in it (unless this is the
                # last block, which has no next block to catch them).
                if py_abs >= y + block_size and (y + h) < H:
                    continue
                key = (px_i, py_abs, s)
                if key in seen:
                    continue
                seen.add(key)
                if not (0 <= py_i < h and 0 <= px_i < W):
                    continue
                if not valid[py_i, px_i]:
                    continue

                geo_x, geo_y = rd.geo_xy(px_i, py_i)
                trees.append({
                    'px': px_i, 'py': py_abs,
                    'geo_x': geo_x, 'geo_y': geo_y,
                    'crown_r': crown_r_by_scale[s],
                    'sigma': s,
                })

        block_num += 1
        if progress_cb:
            progress_cb(int(block_num / total_blocks * 90))
        y += block_size

    # Global multi-scale NMS: the list mixes all scales/blocks from the loop
    # above - sort large->small first (see _global_nms) then suppress
    # neighbors within each point's own sigma radius.
    trees.sort(key=lambda t: -t['sigma'])
    trees = _global_nms(trees)

    if progress_cb:
        progress_cb(100)
    return trees, {'xmin': info.xmin, 'ymax': info.ymax, 'px_size': px_size}


def compare_detections(old_points, new_points, max_dist_m):
    """
    Greedy nearest-neighbor matching between two point sets (x, y) in the
    same CRS/units (meters). Used for change detection over time: old points
    without a match = likely felled/lost, new points without a match =
    likely new/regrowth/previously undetected.
    Returns dict {lost: [(x,y)...], new: [(x,y)...], matched: int}
    """
    if not old_points or not new_points:
        return {'lost': list(old_points), 'new': list(new_points), 'matched': 0}

    from scipy.spatial import cKDTree
    old_arr = np.array(old_points, dtype=np.float64)
    new_arr = np.array(new_points, dtype=np.float64)
    tree_new = cKDTree(new_arr)

    dist, idx = tree_new.query(old_arr, distance_upper_bound=max_dist_m)

    matched_new_idx = set()
    lost = []
    matched = 0
    for i in range(len(old_arr)):
        j = idx[i]
        if np.isfinite(dist[i]) and j < len(new_arr) and j not in matched_new_idx:
            matched_new_idx.add(j)
            matched += 1
        else:
            lost.append((old_arr[i, 0], old_arr[i, 1]))

    new_unmatched = [(new_arr[j, 0], new_arr[j, 1])
                      for j in range(len(new_arr)) if j not in matched_new_idx]
    return {'lost': lost, 'new': new_unmatched, 'matched': matched}
