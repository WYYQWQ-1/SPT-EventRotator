"""生成 EventRotator 图标候选（512px PNG）。

三版：
  A  渐变圆角方块 + 深色菱形 + 琥珀/蓝双弧箭头（当前网页那一版放大）
  B  深色方块 + 发光双弧箭头（无菱形，最简洁）
  C  渐变方块 + 菱形描边 + 双弧箭头（细线极简）

用法：python tools/make_icon.py
输出：assets/icon-A.png / icon-B.png / icon-C.png 及 128px 版本
"""
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W = 512
OUT = Path(__file__).resolve().parent.parent / "assets"
OUT.mkdir(parents=True, exist_ok=True)

AMBER = (255, 194, 60)
BLUE = (87, 182, 255)
DARK = (8, 16, 31)


def rounded_mask(size, radius, ss=4):
    s = size * ss
    m = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius * ss, fill=255)
    return m.resize((size, size), Image.LANCZOS)


def diagonal_gradient(size, c1, c2):
    y, x = np.mgrid[0:size, 0:size]
    t = (x + y) / (2.0 * (size - 1))
    t = t[..., None]
    arr = (np.array(c1) * (1 - t) + np.array(c2) * t).astype(np.uint8)
    return Image.fromarray(arr, "RGB").convert("RGBA")


def arc_arrow(draw, cx, cy, r, a0, a1, color, width, head=1.0):
    """画一段圆弧 + 末端实心箭头（角度单位：度，0=右，顺时针为正）。"""
    draw.arc([cx - r, cy - r, cx + r, cy + r], a0, a1, fill=color, width=width)
    rad = math.radians(a1)
    ex, ey = cx + r * math.cos(rad), cy + r * math.sin(rad)
    # 切线方向
    tx, ty = -math.sin(rad), math.cos(rad)
    nx, ny = math.cos(rad), math.sin(rad)
    L = width * head * 1.5
    p1 = (ex + tx * L, ey + ty * L)
    p2 = (ex + nx * width * 0.95, ey + ny * width * 0.95)
    p3 = (ex - nx * width * 0.95, ey - ny * width * 0.95)
    draw.polygon([p1, p2, p3], fill=color)


def ring_arrows(img, cx, cy, r, width, glow=False):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    # 上弧：左 → 右（琥珀）
    arc_arrow(d, cx, cy, r, 160, 340, AMBER, width)
    # 下弧：右 → 左（蓝）
    arc_arrow(d, cx, cy, r, 340, 160, BLUE, width)
    if glow:
        blur = layer.filter(ImageFilter.GaussianBlur(width * 0.9))
        img.alpha_composite(Image.eval(blur, lambda v: v))
        img.alpha_composite(blur)
    img.alpha_composite(layer)


def diamond(draw, cx, cy, r, fill=None, outline=None, width=6):
    pts = [(cx, cy - r), (cx + r, cy), (cx, cy + r), (cx - r, cy)]
    if fill:
        draw.polygon(pts, fill=fill)
    if outline:
        draw.line(pts + [pts[0]], fill=outline, width=width, joint="curve")


def variant_a():
    img = diagonal_gradient(W, AMBER, BLUE)
    img.putalpha(rounded_mask(W, 108))
    inner = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(inner)
    diamond(d, W / 2, W / 2, 176, fill=DARK + (242,))
    img.alpha_composite(inner)
    ring_arrows(img, W / 2, W / 2, 104, 26)
    return img


def variant_b():
    img = Image.new("RGBA", (W, W), DARK + (255,))
    img.putalpha(rounded_mask(W, 108))
    ring_arrows(img, W / 2, W / 2, 132, 34, glow=True)
    return img


def variant_c():
    img = diagonal_gradient(W, AMBER, BLUE)
    img.putalpha(rounded_mask(W, 108))
    inner = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(inner)
    diamond(d, W / 2, W / 2, 170, outline=DARK + (235,), width=10)
    img.alpha_composite(inner)
    ring_arrows(img, W / 2, W / 2, 92, 22)
    return img


def main():
    for name, fn in (("A", variant_a), ("B", variant_b), ("C", variant_c)):
        im = fn()
        big = OUT / f"icon-{name}.png"
        im.save(big)
        im.resize((128, 128), Image.LANCZOS).save(OUT / f"icon-{name}-128.png")
        # 网页里直接用 data URI，体积要小：256 足够（46px 显示 × 高 DPI）
        im.resize((256, 256), Image.LANCZOS).save(OUT / f"icon-{name}-256.png", optimize=True)
        print(f"{big.name}: {big.stat().st_size/1024:.0f} KB, 256 版 {(OUT / f'icon-{name}-256.png').stat().st_size/1024:.0f} KB")


if __name__ == "__main__":
    main()
