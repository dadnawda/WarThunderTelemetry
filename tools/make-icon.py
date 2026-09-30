"""生成应用图标 WarThunderTelemetry.ico。

风格：抬头显示器（HUD）—— 深色底 + 青色准星 + 荧光绿地平线。
刻意保持元素少、对比强，保证 16x16 下仍能辨认。

用法：
    python tools/make-icon.py
输出：
    src/WarThunderTelemetry.App/app.ico
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

# 与悬浮窗默认配色保持一致
BG_TOP = (16, 24, 32, 255)
BG_BOTTOM = (27, 42, 51, 255)
CYAN = (57, 200, 255, 255)
GREEN = (57, 255, 20, 255)
WHITE = (235, 245, 250, 255)

BASE = 256
OUT = Path(__file__).resolve().parent.parent / "src" / "WarThunderTelemetry.App" / "app.ico"


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def vertical_gradient(size: int, top: tuple, bottom: tuple) -> Image.Image:
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        grad.putpixel(
            (0, y),
            tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)),
        )
    return grad.resize((size, size), Image.NEAREST).convert("RGBA")


def draw_icon() -> Image.Image:
    img = vertical_gradient(BASE, BG_TOP, BG_BOTTOM)
    d = ImageDraw.Draw(img)

    s = BASE
    unit = s / 256  # 便于按 256 基准换算

    # ---- 外框 ----
    border = max(2, round(7 * unit))
    d.rounded_rectangle(
        (border / 2, border / 2, s - 1 - border / 2, s - 1 - border / 2),
        radius=round(46 * unit),
        outline=CYAN,
        width=border,
    )

    # ---- 地平线（居中偏下，加一条更暗的副线做出 HUD 层次）----
    hy = round(140 * unit)
    line_w = max(2, round(7 * unit))
    d.line((round(30 * unit), hy, round(226 * unit), hy), fill=GREEN, width=line_w)

    # ---- 中央准星：两侧短横杠 ----
    cy = round(128 * unit)
    bar_w = max(2, round(8 * unit))
    d.line((round(58 * unit), cy, round(100 * unit), cy), fill=CYAN, width=bar_w)
    d.line((round(156 * unit), cy, round(198 * unit), cy), fill=CYAN, width=bar_w)

    # ---- 中央机体符号（小圆点 + 上竖短线）----
    r = round(9 * unit)
    d.ellipse((round(128 * unit) - r, cy - r, round(128 * unit) + r, cy + r), fill=WHITE)
    d.line(
        (round(128 * unit), cy - round(26 * unit), round(128 * unit), cy - round(13 * unit)),
        fill=CYAN,
        width=max(2, round(6 * unit)),
    )

    # ---- 右下角速度刻度（三小段，小尺寸下可省）----
    for i, length in enumerate((18, 13, 9)):
        y = round((178 + i * 16) * unit)
        d.line(
            (round(180 * unit), y, round((180 + length) * unit), y),
            fill=CYAN,
            width=max(1, round(4 * unit)),
        )

    img.putalpha(rounded_mask(BASE, round(46 * unit)))
    return img


def main() -> None:
    icon = draw_icon()
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    icon.save(OUT, format="ICO", sizes=sizes)
    print(f"已生成: {OUT}")
    print(f"尺寸: {', '.join(f'{w}x{h}' for w, h in sizes)}")


if __name__ == "__main__":
    main()
