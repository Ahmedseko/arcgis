"""
Ported from qgis_plugin/tree_counter/validator.py - only validate_trees and its
dependencies (_ask_gemini, _crop_jpeg_b64). validate_land_polygons is not ported
(land-clearing detection is out of scope, see detector.py's module docstring).

Optional, online, opt-in: only called from detect.py when an API key is
provided. Crops each candidate tree (with a red crosshair overlay) and asks
the selected vision model to confirm Y/N per crop, in batches. Crops are
sliced from the in-memory RasterData array (see raster_io.py) instead of
re-reading windowed crops from disk per candidate like the GDAL original did.

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
}


def _crop_jpeg_b64(rd, px, py, pad_px):
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


def validate_trees(rd, trees, api_key, sigma_px=75, batch_size=16,
                    model='gemini-3.5-flash', profile='forest',
                    progress_cb=None, request_delay=4.5, provider='gemini'):
    """
    rd: raster_io.RasterData (already loaded).
    trees: candidate list from detector.detect_trees / yolo_detector.detect_trees_yolo_primary.
    provider: 'gemini' | 'openai' | 'claude'.
    Returns (validated_trees, stats).
    """
    ask = _ASK_BY_PROVIDER.get(provider, _ask_gemini)
    pad_px = max(int(sigma_px * 3), 60)
    validated, rejected, errors = [], 0, 0
    last_error = ''
    total = max(len(trees), 1)

    for start in range(0, len(trees), batch_size):
        batch = trees[start:start + batch_size]
        crops, idx_map = [], {}
        for i, tree in enumerate(batch):
            b64 = _crop_jpeg_b64(rd, tree['px'], tree['py'], pad_px)
            if b64:
                pos = len(crops) + 1
                crops.append(b64)
                idx_map[pos] = i
        if crops:
            try:
                answers = _ask_gemini(api_key, crops, model, profile)
                for pos, bi in idx_map.items():
                    if answers.get(pos, True):
                        validated.append(batch[bi])
                    else:
                        rejected += 1
            except Exception as e:
                validated.extend(batch)
                errors += 1
                last_error = str(e)
                if 'QUOTA_EXCEEDED' in last_error:
                    validated.extend(trees[start + len(batch):])
                    break
        time.sleep(request_delay)
        if progress_cb:
            progress_cb(int((start + len(batch)) / total * 100))

    return validated, {'rejected': rejected, 'api_errors': errors, 'last_error': last_error}
