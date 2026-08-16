"""
Originally ported from qgis_plugin/tree_counter/validator.py (validate_trees and its
dependencies _ask_gemini/_crop_jpeg_b64) - since extended in this add-in with a shared
validate_crops core (also used by detect_clearing.py for land-clearing polygons, see its
module docstring), which is why the id/crop pairing is now generic rather than tree-only.

Optional, online, opt-in: only called from detect.py / detect_clearing.py when an API key
is provided. Crops each candidate (with a red crosshair overlay) and asks the selected
vision model to confirm Y/N per crop, in batches. Tree crops are sliced from the in-memory
RasterData array (see raster_io.py); land-clearing crops are read windowed per-polygon
instead (raster_io.read_window) since there are far fewer of them, each already localized.

Gemini, OpenAI, and Claude are all called via raw urllib (no extra pip
installs needed in ArcGIS Pro's conda env) - same reasoning as the original
Gemini-only port.
"""
import base64
import io
import json
import time
import urllib.error
import urllib.request

import numpy as np

_PROMPTS = {
    'forest': (
        'You are analyzing {n} aerial drone images (top-down, ~3-5 cm/px) of tropical forest, '
        'each centered with a red crosshair marking one candidate tree location. '
        'Look at the FULL crop, not just the center, to judge surrounding context. '
        'Reply Y ONLY if the crosshair sits on a mature, established tree crown '
        '(large rounded/oval canopy, clearly taller/more developed than its surroundings). '
        'Reply N if the crosshair sits on: bare/disturbed soil, a road or track, cut logs or woody debris, '
        'a small isolated shrub or sapling regrowth (especially inside a cleared/logged patch), '
        'grass, or if it is ambiguous. '
        'Reply format - one line: 1:Y 2:N 3:Y ...'
    ),
    'palm': (
        'You are analyzing {n} aerial drone images (top-down, ~3-5 cm/px) of oil palm plantation, '
        'each centered with a red crosshair marking one candidate palm location. '
        'Reply Y if the crosshair is on or near the center of an oil palm crown '
        '(radial star-shaped fronds radiating outward, viewed from above - '
        'either a large mature crown or a smaller young/juvenile palm with the same '
        'radial frond pattern). A crosshair landing within the crown area - even '
        'on a frond slightly off-center - should be Y, as long as the crown '
        'structure is clearly the dominant feature around the crosshair. '
        'Reply N ONLY if the crosshair is clearly on: bare soil, a road or track, '
        'grass or weeds between palms, other non-palm ground-cover, or the open '
        'gap between two separate crowns with no crown structure at the crosshair. '
        'Reply format - one line: 1:Y 2:N 3:Y ...'
    ),
    'clearing': (
        'You are analyzing {n} aerial drone images (top-down, ~3-5 cm/px) of tropical forest/plantation. '
        'Each image is a crop centered on one candidate cleared/bare-ground area flagged by a '
        'color-threshold algorithm (a red crosshair marks its center) - the crop shows the whole '
        'flagged area plus some surrounding context. '
        'Reply Y if the area is genuinely bare/disturbed ground: exposed soil, cut/felled vegetation, '
        'logging debris, construction, or a road/track. '
        'Reply N if it is actually a false positive: water (river/pond), deep shadow, a rooftop, '
        'or still covered in live vegetation. '
        'Reply format - one line: 1:Y 2:N 3:Y ...'
    ),
}


def _draw_crosshair_jpeg_b64(rgb):
    from PIL import Image, ImageDraw
    img = Image.fromarray(rgb).resize((220, 220), Image.LANCZOS).convert('RGB')

    draw = ImageDraw.Draw(img)
    cx, cy, s = 110, 110, 14
    draw.line([(cx - s, cy), (cx + s, cy)], fill=(255, 0, 0), width=3)
    draw.line([(cx, cy - s), (cx, cy + s)], fill=(255, 0, 0), width=3)
    draw.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], outline=(255, 0, 0), width=2)

    buf = io.BytesIO()
    img.save(buf, format='JPEG', quality=85)
    return base64.b64encode(buf.getvalue()).decode('utf-8')


def _crop_jpeg_b64(rd, px, py, pad_px):
    """Fixed-radius square crop around one point (px,py) - e.g. a tree/palm candidate."""
    xo = max(0, px - pad_px)
    yo = max(0, py - pad_px)
    xe = min(rd.W, px + pad_px)
    ye = min(rd.H, py + pad_px)
    w, h = xe - xo, ye - yo
    if w < 10 or h < 10:
        return None
    rgb = np.stack([
        rd.r[yo:ye, xo:xe], rd.g[yo:ye, xo:xe], rd.b[yo:ye, xo:xe],
    ], axis=-1).astype(np.uint8)
    return _draw_crosshair_jpeg_b64(rgb)


def _whole_jpeg_b64(rd):
    """Render the ENTIRE given RasterData - for validate_crops callers (e.g. land-clearing
    polygons) that already windowed rd tightly around their own area of interest via
    raster_io.read_window, instead of a fixed-radius crop around a single point."""
    if rd.W < 10 or rd.H < 10:
        return None
    rgb = np.stack([rd.r, rd.g, rd.b], axis=-1).astype(np.uint8)
    return _draw_crosshair_jpeg_b64(rgb)


def _http_post_json(url, body, headers, max_retries=2, base_delay=4.0, max_wait=15.0):
    req = urllib.request.Request(url, data=body, headers=headers, method='POST')
    attempt = 0
    while True:
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read())
        except urllib.error.HTTPError as e:
            try:
                detail = e.read().decode('utf-8', errors='ignore')[:400]
            except Exception:
                detail = str(e)
            if e.code == 429 and attempt < max_retries:
                retry_after = e.headers.get('Retry-After')
                wait = float(retry_after) if retry_after else base_delay * (2 ** attempt)
                time.sleep(min(wait, max_wait))
                attempt += 1
                continue
            if e.code == 429:
                raise RuntimeError('QUOTA_EXCEEDED: HTTP 429: ' + detail)
            raise RuntimeError('HTTP %d: %s' % (e.code, detail))


def _parse_answers(text):
    answers = {}
    for token in text.strip().split():
        if ':' in token:
            idx_s, ans = token.split(':', 1)
            try:
                answers[int(idx_s)] = ans.strip().upper().startswith('Y')
            except ValueError:
                pass
    return answers


def _ask_gemini(api_key, crops_b64, model, profile, **retry_kw):
    n = len(crops_b64)
    prompt = _PROMPTS.get(profile, _PROMPTS['forest']).format(n=n)
    url = ('https://generativelanguage.googleapis.com/v1beta/models/'
           + model + ':generateContent?key=' + api_key)
    parts = [{'text': prompt}]
    for i, b64 in enumerate(crops_b64, 1):
        parts.append({'text': '[%d]' % i})
        parts.append({'inline_data': {'mime_type': 'image/jpeg', 'data': b64}})
    body = json.dumps({'contents': [{'parts': parts}]}).encode('utf-8')
    result = _http_post_json(url, body, {'Content-Type': 'application/json'}, **retry_kw)
    return _parse_answers(result['candidates'][0]['content']['parts'][0]['text'])


def _ask_openai(api_key, crops_b64, model, profile, **retry_kw):
    n = len(crops_b64)
    prompt = _PROMPTS.get(profile, _PROMPTS['forest']).format(n=n)
    content = [{'type': 'text', 'text': prompt}]
    for i, b64 in enumerate(crops_b64, 1):
        content.append({'type': 'text', 'text': '[%d]' % i})
        content.append({'type': 'image_url', 'image_url': {'url': 'data:image/jpeg;base64,' + b64}})
    body = json.dumps({'model': model, 'messages': [{'role': 'user', 'content': content}], 'max_tokens': 500}).encode('utf-8')
    headers = {'Content-Type': 'application/json', 'Authorization': 'Bearer ' + api_key}
    result = _http_post_json('https://api.openai.com/v1/chat/completions', body, headers, **retry_kw)
    return _parse_answers(result['choices'][0]['message']['content'])


def _ask_claude(api_key, crops_b64, model, profile, **retry_kw):
    n = len(crops_b64)
    prompt = _PROMPTS.get(profile, _PROMPTS['forest']).format(n=n)
    content = [{'type': 'text', 'text': prompt}]
    for i, b64 in enumerate(crops_b64, 1):
        content.append({'type': 'text', 'text': '[%d]' % i})
        content.append({'type': 'image', 'source': {'type': 'base64', 'media_type': 'image/jpeg', 'data': b64}})
    body = json.dumps({'model': model, 'max_tokens': 500, 'messages': [{'role': 'user', 'content': content}]}).encode('utf-8')
    headers = {'Content-Type': 'application/json', 'x-api-key': api_key, 'anthropic-version': '2023-06-01'}
    result = _http_post_json('https://api.anthropic.com/v1/messages', body, headers, **retry_kw)
    return _parse_answers(result['content'][0]['text'])


_ASK_BY_PROVIDER = {'gemini': _ask_gemini, 'openai': _ask_openai, 'claude': _ask_claude}


def test_api_key(provider, api_key, model):
    """Minimal round-trip call to confirm a key/model pair actually works. Returns (ok, message)."""
    try:
        if provider == 'openai':
            body = json.dumps({'model': model, 'messages': [{'role': 'user', 'content': 'Reply with OK.'}], 'max_tokens': 5}).encode('utf-8')
            headers = {'Content-Type': 'application/json', 'Authorization': 'Bearer ' + api_key}
            url = 'https://api.openai.com/v1/chat/completions'
        elif provider == 'claude':
            body = json.dumps({'model': model, 'max_tokens': 5, 'messages': [{'role': 'user', 'content': 'Reply with OK.'}]}).encode('utf-8')
            headers = {'Content-Type': 'application/json', 'x-api-key': api_key, 'anthropic-version': '2023-06-01'}
            url = 'https://api.anthropic.com/v1/messages'
        else:
            body = json.dumps({'contents': [{'parts': [{'text': 'Reply with OK.'}]}]}).encode('utf-8')
            headers = {'Content-Type': 'application/json'}
            url = 'https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent?key=%s' % (model, api_key)
        _http_post_json(url, body, headers, max_retries=0)
        return True, 'Key is valid.'
    except Exception as e:
        msg = str(e)
        if msg.startswith('QUOTA_EXCEEDED'):
            return False, 'Rate-limited/quota exceeded (key may still be valid).'
        if 'credit balance' in msg.lower():
            return False, ('This key has no API credit balance. Anthropic/OpenAI/Gemini API keys are '
                'billed separately (prepaid credits) from a chat subscription - add credits in that '
                "provider's console/billing page, then Test Key again.")
        return False, msg


def validate_crops(id_crop_pairs, api_key, model='gemini-3.5-flash', profile='forest',
                    batch_size=16, progress_cb=None, request_delay=4.5, provider='gemini'):
    """
    id_crop_pairs: list of (id, jpeg_b64) - already-cropped images, any id type (a tree's
    list index, a land-clearing polygon's OID, ...) the caller uses to match answers back
    to its own candidates.
    provider: 'gemini' | 'openai' | 'claude'.
    Returns (kept_ids: set, stats) - "kept" means the AI answered Y, OR a request/parse
    failure happened and this candidate fails open (kept rather than silently dropped, same
    as if AI validation had never run).
    """
    ask = _ASK_BY_PROVIDER.get(provider, _ask_gemini)
    kept, rejected, errors = set(), 0, 0
    last_error = ''
    total = max(len(id_crop_pairs), 1)

    for start in range(0, len(id_crop_pairs), batch_size):
        batch = id_crop_pairs[start:start + batch_size]
        try:
            answers = ask(api_key, [b64 for _, b64 in batch], model, profile)
            for pos, (item_id, _) in enumerate(batch, 1):
                if answers.get(pos, True):
                    kept.add(item_id)
                else:
                    rejected += 1
        except Exception as e:
            for item_id, _ in batch:
                kept.add(item_id)
            errors += 1
            last_error = str(e)
            if 'QUOTA_EXCEEDED' in last_error:
                for item_id, _ in id_crop_pairs[start + len(batch):]:
                    kept.add(item_id)
                break
        time.sleep(request_delay)
        if progress_cb:
            progress_cb(int((start + len(batch)) / total * 100))

    return kept, {'rejected': rejected, 'api_errors': errors, 'last_error': last_error}


def validate_trees(rd, trees, api_key, sigma_px=75, batch_size=16,
                    model='gemini-3.5-flash', profile='forest',
                    progress_cb=None, request_delay=4.5, provider='gemini'):
    """
    rd: raster_io.RasterData (already loaded).
    trees: candidate list from detector.detect_trees / yolo_detector.detect_trees_yolo_primary.
    Returns (validated_trees, stats). Trees too close to the raster edge for a full crop are
    silently dropped (never validated, never counted as rejected) - same as before this was
    rebuilt on top of validate_crops.
    """
    pad_px = max(int(sigma_px * 3), 60)
    pairs = []
    for i, tree in enumerate(trees):
        b64 = _crop_jpeg_b64(rd, tree['px'], tree['py'], pad_px)
        if b64:
            pairs.append((i, b64))

    kept, stats = validate_crops(pairs, api_key, model=model, profile=profile,
                                  batch_size=batch_size, progress_cb=progress_cb,
                                  request_delay=request_delay, provider=provider)
    validated = [trees[i] for i, _ in pairs if i in kept]
    return validated, stats


if __name__ == '__main__':
    # ponytail: no network - swaps in a fake provider to exercise validate_crops' branching
    # (Y/N parsing, generic-exception fail-open, QUOTA_EXCEEDED fail-open-the-rest) without
    # hitting a real API.
    def _fake_ask_alternating(api_key, crops_b64, model, profile):
        return {i: (i % 2 == 1) for i in range(1, len(crops_b64) + 1)}  # odd->Y, even->N

    def _fake_ask_error(api_key, crops_b64, model, profile):
        raise RuntimeError('boom')

    def _fake_ask_quota(api_key, crops_b64, model, profile):
        raise RuntimeError('QUOTA_EXCEEDED: HTTP 429: rate limited')

    pairs = [(i, f'crop{i}') for i in range(4)]  # ids 0..3

    _ASK_BY_PROVIDER['fake'] = _fake_ask_alternating
    kept, stats = validate_crops(pairs, 'key', batch_size=16, request_delay=0, provider='fake')
    assert kept == {0, 2}, kept  # positions 1,3 (ids 0,2) -> Y
    assert stats['rejected'] == 2, stats

    _ASK_BY_PROVIDER['fake'] = _fake_ask_error
    kept, stats = validate_crops(pairs, 'key', batch_size=16, request_delay=0, provider='fake')
    assert kept == {0, 1, 2, 3}, kept  # generic error -> whole batch fails open
    assert stats['api_errors'] == 1, stats

    _ASK_BY_PROVIDER['fake'] = _fake_ask_quota
    kept, stats = validate_crops(pairs, 'key', batch_size=2, request_delay=0, provider='fake')
    assert kept == {0, 1, 2, 3}, kept  # quota on batch 1 -> that batch AND the rest fail open
    assert stats['rejected'] == 0, stats

    print('validator.py self-check OK')
