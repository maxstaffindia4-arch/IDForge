"""Fix contact number in assets/background.png: '07103' -> '01703'.

Photoshop rasterized the original; PIL's rasterizer differs slightly, so instead
of re-rendering we COPY the already-rendered '1' and '7' glyphs from elsewhere in
the same string into the two target cells. Digits are tabular (equal advance),
so cell-for-cell swap is pixel-exact.

Steps:
  1. Per-character template matching (NCC) locates every glyph's pen origin.
  2. The '7' and '1' inside '07103' get their cells swapped with donor '1'/'7'
     glyphs rendered by Photoshop elsewhere in the same line.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFont, ImageDraw

BG = Path(r"F:\Web Design\ID Card Tool\assets\background.png")
FONT = Path(r"F:\Web Design\ID Card Tool\assets\fonts\SourceSans3-SemiBold.ttf")
TEXT = "Contact: 01712-109-683, 07103-790-950"
SIZE = 25

# region of interest around the contact line (abs coords)
RX0, RY0, RX1, RY1 = 95, 865, 570, 925


def ink_mask(img_arr):
    return ((img_arr[:, :, 3] > 100) & (img_arr[:, :, :3].sum(axis=2) < 400)).astype(np.float32)


def render_char(ch, font, ox, oy, W, H):
    c = Image.new("L", (W, H), 0)
    ImageDraw.Draw(c).text((ox, oy), ch, font=font, fill=255)
    return (np.array(c) > 100).astype(np.float32)


def ncc_at(strip, templ, x, y):
    """Normalized cross-correlation of templ (tight mask) placed at (x,y) in strip."""
    th, tw = templ.shape
    sub = strip[y:y + th, x:x + tw]
    if sub.shape != templ.shape:
        return -1.0
    a = templ - templ.mean()
    b = sub - sub.mean()
    denom = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / denom) if denom > 0 else -1.0


def main():
    im = Image.open(BG).convert("RGBA")
    arr = np.array(im)
    strip = ink_mask(arr[RY0:RY1, RX0:RX1])
    H, W = strip.shape

    font = ImageFont.truetype(str(FONT), SIZE)

    # Render each char alone; record tight ink box relative to pen origin (0,0).
    chars = list(TEXT)
    adv = [font.getlength(ch) for ch in chars]
    templates = []   # (tight mask, ink_dx, ink_dy) ink offsets from pen origin
    for ch in chars:
        tile = Image.new("L", (80, 60), 0)
        ImageDraw.Draw(tile).text((10, 10), ch, font=font, fill=255)
        ta = np.array(tile)
        ys, xs = np.where(ta > 100)
        if len(ys) == 0:
            templates.append((np.zeros((1, 1), np.float32), 0, 0))
            continue
        x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
        templ = (ta[y0:y1, x0:x1] > 100).astype(np.float32)
        # ink offset from pen origin: tile drawn at (10,10), pen origin maps to (10,10)
        templates.append((templ, x0 - 10, y0 - 10))

    # Locate baseline/pen-origin y by matching the first char 'C'.
    # Search pen-origin x near left edge of ink (orig ink starts abs x=108).
    best = None
    tC, dxC, dyC = templates[0]
    for oy in range(-40, 5):           # pen origin y relative guesses
        for ox in range(5, 25):
            x = ox + dxC - RX0 + RX0  # placeholder
    # simpler: scan strip for 'C' template over plausible region
    th, tw = tC.shape
    best = (-2, None, None)
    for y in range(0, H - th):
        for x in range(0, W - tw):
            v = ncc_at(strip, tC, x, y)
            if v > best[0]:
                best = (v, x, y)
    score, cx, cy = best
    print(f"'C' match score={score:.3f} at strip ({cx},{cy}) -> abs ({cx+RX0},{cy+RY0})")
    # pen origin for char0: ink placed at (cx,cy) means pen origin at cx - dxC? No:
    # template ink box top-left = pen_origin + (dxC, dyC) in strip coords.
    pen0_x = cx - dxC
    pen0_y = cy - dyC
    print("pen origin char0 (strip coords):", pen0_x, pen0_y)

    # Track chars left->right. Pen origin_i = pen0 + cumsum(adv).
    cum = [0.0]
    for a in adv:
        cum.append(cum[-1] + a)

    placements = []  # per char: (pen_x, pen_y, score)
    running_dx = 0.0
    for i, (ch, (templ, dx, dy)) in enumerate(zip(chars, templates)):
        th, tw = templ.shape
        pred_x = pen0_x + cum[i] + running_dx
        # search window +-10 px
        best = (-2, None)
        y0 = int(round(pen0_y + dy))
        for x in range(int(pred_x + dx) - 10, int(pred_x + dx) + 11):
            xx = x
            if xx < 0 or xx + tw > W or y0 < 0 or y0 + th > H:
                continue
            v = ncc_at(strip, templ, xx, y0)
            if v > best[0]:
                best = (v, xx)
        v, mx = best
        if mx is None:
            placements.append((pred_x, pen0_y, 0.0))
            continue
        # matched ink top-left = mx; pen origin x = mx - dx
        actual_pen_x = mx - dx
        running_dx = actual_pen_x - (pen0_x + cum[i])
        placements.append((actual_pen_x, pen0_y, v))

    for i, (ch, (px, py, v)) in enumerate(zip(chars, placements)):
        print(f"{i:2d} {ch!r} pen_x={px:7.1f} score={v:.2f}")

    # ---------- swap glyphs ----------
    # cell x-range = [pen_x, pen_x + advance). Ink y-band per char from its
    # template offsets (pen_y identical for all chars -> same baseline).
    pen_y = placements[0][1]

    def cell_bounds(i):
        ch = chars[i]
        templ, dx, dy = templates[i]
        px, py, _ = placements[i]
        x0 = int(round(px))
        x1 = int(round(px + adv[i]))
        # ink y band: template ink top/bottom relative to pen origin, padded
        y0 = int(round(pen_y + dy)) - 2
        y1 = int(round(pen_y + dy + templ.shape[0])) + 2
        return x0, x1, y0, y1, dx, dy, templ.shape

    def extract_ink(i):
        """Returns (ink_mask_rel, x0,y0) of actual PNG ink inside char i's cell."""
        x0, x1, y0, y1, dx, dy, (th, tw) = cell_bounds(i)
        sub = strip[y0:y1, x0:x1]
        ink = sub > 0.5
        ys, xs = np.where(ink)
        if len(ys) == 0:
            return None
        ix0, ix1, iy0, iy1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
        return ink[iy0:iy1, ix0:ix1], x0 + ix0, y0 + iy0

    # donors: '1' at idx10, '7' at idx11 (both inside '01712', not in target)
    donor1 = extract_ink(10)
    donor7 = extract_ink(11)
    assert donor1 and donor7, "donor glyphs not found"
    (m1, x1a, y1a), (m7, x7a, y7a) = donor1, donor7

    # targets: '7' at idx25, '1' at idx26 (the '07103' -> '01703' swap)
    t7 = cell_bounds(25)   # cell that should become '1'
    t1 = cell_bounds(26)   # cell that should become '7'

    # cell-relative offsets of donor ink (from donor pen origin)
    d1_pen = placements[10][0]
    d7_pen = placements[11][0]
    rel1 = (x1a - d1_pen, y1a - pen_y)   # (dx, dy) of donor '1' ink rel. to its pen origin
    rel7 = (x7a - d7_pen, y7a - pen_y)

    out = arr.copy()
    region = out[RY0:RY1, RX0:RX1]

    def clear_cell(cb):
        x0, x1, y0, y1, *_ = cb
        # clear only ink pixels (mask>0.5) inside the cell -> restore white bg
        sub_mask = strip[y0:y1, x0:x1] > 0.5
        region[y0:y1, x0:x1][sub_mask] = (255, 255, 255, 255)

    def paste_donor(cb, mask, rel):
        x0, x1, y0, y1, *_ = cb
        px, py, _ = placements[25 if cb is t7 else 26]
        # ink top-left in strip coords = pen_x + rel_dx, pen_y + rel_dy
        ix = int(round(px + rel[0]))
        iy = int(round(pen_y + rel[1]))
        h, w = mask.shape
        src = arr[RY0 + iy:RY0 + iy + h, RX0 + ix:RX0 + ix + w]
        # copy RGBA where donor mask is ink
        tgt = region[iy:iy + h, ix:ix + w]
        m = mask.astype(bool)
        tgt[m] = src[m]

    clear_cell(t7); clear_cell(t1)
    # after clearing, 'strip' mask is stale — recompute not needed since paste
    # writes absolute pixels; but paste uses arr (original) for donor colors —
    # donor areas are untouched, so fine.
    paste_donor(t7, m1, rel1)   # '7' cell gets '1'
    paste_donor(t1, m7, rel7)   # '1' cell gets '7'

    out[RY0:RY1, RX0:RX1] = region

    # verify: diff confined to the two target cells (+-1px)
    diff = np.any(arr[RY0:RY1, RX0:RX1] != out[RY0:RY1, RX0:RX1], axis=2)
    ys, xs = np.where(diff)
    xlo = min(t7[0], t1[0]) - 1
    xhi = max(t7[1], t1[1]) + 1
    bad = [(int(x + RX0), int(y + RY0)) for y, x in zip(ys, xs)
           if not (xlo <= x < xhi)]
    print("diff px outside target cells:", len(bad), bad[:10])

    # verify new strip ink in target cells matches donors (NCC)
    newstrip = ink_mask(out[RY0:RY1, RX0:RX1])
    for name, cb, donor_mask, rel in (("cell25(now '1')", t7, m1, rel1),
                                    ("cell26(now '7')", t1, m7, rel7)):
        x0, x1, y0, y1, *_ = cb
        px, py, _ = (placements[25] if cb is t7 else placements[26])
        ix = int(round(px + rel[0])); iy = int(round(pen_y + rel[1]))
        h, w = donor_mask.shape
        sub = newstrip[iy:iy + h, ix:ix + w]
        a = donor_mask - donor_mask.mean(); b = sub - sub.mean()
        ncc = float((a * b).sum() / np.sqrt((a * a).sum() * (b * b).sum()))
        print(name, "NCC vs donor:", round(ncc, 3))

    if "--apply" in sys.argv:
        Image.fromarray(out).save(BG)
        print("SAVED", BG)
    else:
        # save preview crop for inspection
        Image.fromarray(out[870:925, 90:560]).save(
            Path(__file__).parent / "contact_fix_preview.png")
        print("dry run; preview written")


if __name__ == "__main__":
    sys.exit(main())
