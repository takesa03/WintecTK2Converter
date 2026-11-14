# WintecTK2Converter のアイコン (赤系統。2026-11-14 に採用)
# 必要なもの: Python 3 と Pillow (pip install pillow)
# 「HDMIキャプチャー NDI 出力」のアイコンの構成 (角丸の背景・線で描いたモニター・後ろにずらした枠・画面の星) を生かし、
# 画面の中に GPS 衛星 (本体・太陽電池パネル・アンテナと電波) を置く。
import math, sys
from PIL import Image, ImageDraw, ImageFilter

S = 4                      # 描画は 1024px (= 256 x 4) で行い、縮小して滑らかにする
N = 256 * S


def sc(v):
    return int(round(v * S))


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


# ---- 色 (赤系統) ----
BG_TOP = (36, 10, 16)       # 背景 (上)
BG_BOT = (20, 6, 10)        # 背景 (下)
SCREEN = (44, 12, 20)       # 画面の中
LINE = (255, 74, 74)        # モニターの線
LINE_GLOW = (255, 40, 40)
ECHO = (200, 50, 60)        # 後ろの枠
STAR = (255, 205, 205)
PANEL = (220, 40, 52)       # 太陽電池パネル
PANEL_HI = (255, 120, 120)  # パネルの格子
BODY = (255, 196, 196)      # 衛星の本体
BODY_SH = (210, 120, 128)
WAVE = (255, 92, 92)        # 電波


def rounded_mask(size, box, r):
    m = Image.new('L', size, 0)
    ImageDraw.Draw(m).rounded_rectangle(box, r, fill=255)
    return m


def make(path_png=None):
    img = Image.new('RGBA', (N, N), (0, 0, 0, 0))

    # 背景: 角丸の正方形、上から下へのグラデーション
    grad = Image.new('RGBA', (N, N))
    gd = ImageDraw.Draw(grad)
    for y in range(N):
        gd.line([(0, y), (N, y)], fill=lerp(BG_TOP, BG_BOT, y / N) + (255,))
    img.paste(grad, (0, 0), rounded_mask((N, N), (0, 0, N - 1, N - 1), sc(54)))

    # モニターの位置 (HDMI のアイコンとほぼ同じ配置)
    mx0, my0, mx1, my1 = 22, 50, 184, 160
    r = 12

    # 後ろにずらした枠 (3 枚、だんだん薄く)
    layer = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    for k, a in ((3, 70), (2, 110), (1, 160)):
        dx, dy = 14 * k, 13 * k
        ld.rounded_rectangle((sc(mx0 + dx), sc(my0 + dy), sc(mx1 + dx), sc(my1 + dy)), sc(r), outline=ECHO + (a,), width=sc(3))
    img.alpha_composite(layer)

    # 画面 (中を塗ってから、線を光らせて描く)
    scr = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    ImageDraw.Draw(scr).rounded_rectangle((sc(mx0), sc(my0), sc(mx1), sc(my1)), sc(r), fill=SCREEN + (255,))
    img.alpha_composite(scr)

    # 星
    st = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    sd = ImageDraw.Draw(st)
    stars = [(44, 66, 1.6), (70, 60, 1.2), (110, 64, 1.4), (150, 72, 1.8), (166, 98, 1.2), (60, 92, 1.0),
             (40, 132, 1.4), (92, 142, 1.2), (138, 146, 1.6), (168, 134, 1.0), (128, 92, 1.0), (82, 112, 1.1)]
    for x, y, rr in stars:
        sd.ellipse((sc(x - rr), sc(y - rr), sc(x + rr), sc(y + rr)), fill=STAR + (210,))
    img.alpha_composite(st)

    # ---- GPS 衛星 (画面の中央やや左上、斜め 25 度) ----
    sat = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    d = ImageDraw.Draw(sat)
    cx, cy = 92, 98
    ang = math.radians(-25)
    ca, sa = math.cos(ang), math.sin(ang)

    def rot(x, y):
        return (sc(cx + x * ca - y * sa), sc(cy + x * sa + y * ca))

    def poly(pts, **kw):
        d.polygon([rot(x, y) for x, y in pts], **kw)

    def line(p, q, fill, w):
        d.line([rot(*p), rot(*q)], fill=fill, width=sc(w))

    # 太陽電池パネル (左右)
    for side in (-1, 1):
        x0, x1 = (side * 12, side * 50)
        lo, hi = min(x0, x1), max(x0, x1)
        poly([(lo, -10), (hi, -10), (hi, 10), (lo, 10)], fill=PANEL + (255,), outline=PANEL_HI + (255,))
        for gx in range(int(lo) + 9, int(hi), 9):
            line((gx, -10), (gx, 10), PANEL_HI + (200,), 1.1)
        line((lo, 0), (hi, 0), PANEL_HI + (200,), 1.1)
        # パネルと本体をつなぐ腕
        line((side * 8, 0), (side * 12, 0), BODY + (255,), 2.2)
    # 本体
    poly([(-8, -11), (8, -11), (8, 11), (-8, 11)], fill=BODY + (255,))
    poly([(2, -11), (8, -11), (8, 11), (2, 11)], fill=BODY_SH + (255,))
    # アンテナ (下向きの皿)
    poly([(-6, 11), (6, 11), (3, 17), (-3, 17)], fill=BODY + (255,))
    line((0, 17), (0, 22), BODY + (255,), 1.6)

    # 光らせる (衛星とモニターの線)
    glow_src = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    gd2 = ImageDraw.Draw(glow_src)
    gd2.rounded_rectangle((sc(mx0), sc(my0), sc(mx1), sc(my1)), sc(r), outline=LINE_GLOW + (255,), width=sc(6))
    glow = glow_src.filter(ImageFilter.GaussianBlur(sc(5)))
    img.alpha_composite(glow)

    # 電波 (アンテナの先から、右下へ広がる 3 本の弧)
    wv = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    wd = ImageDraw.Draw(wv)
    ax, ay = rot(0, 24)
    base = math.degrees(math.atan2(ca, -sa))   # アンテナの向き (本体の下方向)
    for i, (rad, a) in enumerate(((14, 255), (26, 200), (38, 140))):
        rr = sc(rad)
        wd.arc((ax - rr, ay - rr, ax + rr, ay + rr), base - 32, base + 32, fill=WAVE + (a,), width=sc(3.2))
    img.alpha_composite(wv)
    sat_glow = sat.filter(ImageFilter.GaussianBlur(sc(3)))
    img.alpha_composite(sat_glow)
    img.alpha_composite(sat)

    # モニターの線 (くっきり)
    ol = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    od = ImageDraw.Draw(ol)
    od.rounded_rectangle((sc(mx0), sc(my0), sc(mx1), sc(my1)), sc(r), outline=LINE + (255,), width=sc(5))
    # スタンド
    midx = (mx0 + mx1) / 2
    od.rectangle((sc(midx - 4), sc(my1 + 2), sc(midx + 4), sc(my1 + 18)), fill=LINE + (255,))
    od.rounded_rectangle((sc(midx - 26), sc(my1 + 16), sc(midx + 26), sc(my1 + 22)), sc(3), fill=LINE + (255,))
    img.alpha_composite(ol)

    out = img.resize((256, 256), Image.LANCZOS)
    if path_png:
        out.save(path_png)
    return out


if __name__ == '__main__':
    # 使い方: python tools/make_icon.py
    #   gui/AppIcon.ico (16〜256 px) を作り直す。--preview を付けると、各サイズを並べた見本 (tools/icon_preview.png) も作る
    import os
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    icon = make()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    ico = os.path.join(root, 'gui', 'AppIcon.ico')
    icon.save(ico, format='ICO', sizes=[(s, s) for s in sizes])
    print('saved', ico)
    if '--preview' in sys.argv:
        prev = [256, 64, 48, 32, 24, 16]
        W = sum(prev) + 20 * (len(prev) + 1)
        sheet = Image.new('RGBA', (W, 2 * (256 + 40)), (255, 255, 255, 255))
        ImageDraw.Draw(sheet).rectangle((0, 256 + 40, W, 2 * (256 + 40)), fill=(32, 32, 32, 255))
        x = 20
        for s in prev:
            im = icon.resize((s, s), Image.LANCZOS)
            for row in (0, 1):
                sheet.alpha_composite(im, (x, row * (256 + 40) + 20 + (256 - s) // 2))
            x += s + 20
        p = os.path.join(root, 'tools', 'icon_preview.png')
        sheet.save(p)
        print('saved', p)
