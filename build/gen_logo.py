# -*- coding: utf-8 -*-
"""
把设计稿 logo（白底 + 深灰图案的 PNG）加工成插件可用的图标资源。

当前设计稿：C:\\Users\\XJH\\Desktop\\x输入法.png
  · 681x614，白底，主色 #333333（深灰）；
  · 内容是**宽版字标**「X输入法」——整体 572x329（宽高比 1.74），
    左侧一个方框，方框右边竖线中部留缺口（「输」字正好从缺口穿出去）。

产出（写入 src/Xime.Core/Resources/）：
  XimeLogo.ico  多尺寸图标（16/24/32/48/64/128/256），用于窗体标题栏、任务栏
  XimeLogo.png  字标原比例的透明 PNG（256x148 + 3.5% 透明边距），用于界面内展示

处理要点：
  1. 自动裁掉设计稿四周的白边，再补一圈留白，让图案尽量充满图标；
  2. 把「白色背景」转成透明 —— 用亮度做 alpha 映射，
     纯白 → 全透明、图案色 → 全不透明、抗锯齿边缘保留半透明过渡，
     这样图标放在浅色或深色主题下都不会出现难看的白色方块；
     RGB 统一取图案色，让 alpha 单独承载「覆盖率」，避免抗锯齿边缘
     在浅色底上二次变淡（换成细笔画的字标后这一点尤其明显）。
  3. 16/24/32px 改用矢量绘制的小尺寸标识（方框 + X，右边同样留缺口），
     因为字标缩到 32px 以下，「输入法」三个字只剩一团虚影。

注：早期设计稿右下角带「豆包AI生成」水印，脚本里曾有针对**上一稿方形外框**
几何的修补函数；换成本稿（水印区纯白、方框移到左侧）后该函数会产生错误结果，
已删除。若后续换回带水印的稿子，需按新稿几何重写修补逻辑。
"""

import os
from collections import Counter
from PIL import Image, ImageDraw

SRC = r"C:\Users\XJH\Desktop\x输入法.png"
OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "src", "Xime.Core", "Resources")

# 图案四周留白（相对图案长边的比例）。留一点边距，避免字标顶到图标边缘。
PAD_RATIO = 0.03

ICO_SIZES = [256, 128, 64, 48, 32, 24, 16]

# 这些尺寸改用「方框 + 粗体 X」的简化标识。
# 实测对比（模拟任务栏深色背景）：完整 logo 里的「X输入法」在 32px 以下
# 笔画像一团虚影，缩到 16px 只剩一个灰糊块；而标题栏(16px)、任务栏(32px)
# 恰恰是图标最常被看到的位置，因此小尺寸只保留辨识度最高的元素 —— 方框 + X。
# 48px 及以上仍使用完整字标，保证品牌字样在资源管理器"大图标"等视图中出现。
SIMPLIFIED_SIZES = [32, 24, 16]

# 完整字标的 alpha 增益（按尺寸）。设计稿的笔画很细（方框线仅 5px / 字标长边 574px），
# 缩小到 48/64px 时单像素峰值覆盖率掉到 30~50%，字标会"发灰发虚"；
# 给 alpha 一点增益把细笔画压回来，纯属小尺寸可读性补偿，不改图案颜色。
# 128px 以上本来就够实，不需要增益。
WORDMARK_ALPHA_BOOST = {48: 1.55, 64: 1.45, 128: 1.12}

# ---- 简化标识的几何参数（按设计稿实测值折算，保证小尺寸也"像"原稿）----
FRAME_ASPECT = 313.0 / 329.0     # 方框宽/高（实测 313x329）
FRAME_GAP = (0.240, 0.760)       # 右边竖线的缺口，占方框高度的比例区间
FRAME_STROKE = 0.085             # 方框线宽（相对画布边长）
FRAME_INSET = 0.045              # 方框距画布边缘
X_STROKE = 0.150                 # X 笔画宽（相对画布边长）
X_PAD = 0.290                    # X 距方框内边（相对方框宽）


def _lum(px):
    """感知亮度（ITU-R BT.601）。"""
    return 0.299 * px[0] + 0.587 * px[1] + 0.114 * px[2]


def load_opaque(path):
    """
    读入设计稿并压成不透明 RGB。

    若源图带 alpha，先合到白底上 —— 这样后续「按亮度做 alpha 映射」的
    逻辑不用再单独处理源 alpha。
    """
    raw = Image.open(path)
    if raw.mode in ("RGBA", "LA") or "transparency" in raw.info:
        raw = raw.convert("RGBA")
        raw = Image.alpha_composite(
            Image.new("RGBA", raw.size, (255, 255, 255, 255)), raw)
    return raw.convert("RGB")


def auto_thresholds(im):
    """
    从图片自身推断「背景色」与「图案色」，据此算出透明化阈值。

    设计稿的背景不一定是纯白，图案色也不一定是最深的黑；
    写死 240/120 这类魔法数会随着换稿失效，所以改为：
    取出现次数最多的颜色当背景、暗色里出现最多的当图案色，
    再按两者亮度差取比例定阈值。

    返回 (white_cut, ink_cut, bg, ink)
    """
    small = im.convert("RGB").resize((320, 320), Image.Resampling.NEAREST)
    px = small.load()
    counts = Counter(px[x, y]
                     for y in range(small.height)
                     for x in range(small.width))

    bg = counts.most_common(1)[0][0]

    darks = [(c, n) for c, n in counts.items() if _lum(c) < 200]
    if darks:
        darks.sort(key=lambda t: -t[1])
        ink = darks[0][0]
    else:
        ink = (0, 0, 0)

    lb, li = _lum(bg), _lum(ink)
    span = max(lb - li, 1.0)

    # 亮度高于 white_cut → 完全透明；低于 ink_cut → 完全不透明；
    # 中间线性过渡，保留抗锯齿边缘的半透明。
    return li + span * 0.90, li + span * 0.28, bg, ink


def make_simplified(size, ink):
    """
    绘制简化标识：带缺口的方框 + 居中大号 X（透明底）。

    全部用矢量线条绘制，不依赖任何字体文件；
    先按 8 倍超采样画，再 LANCZOS 缩小 → 笔画边缘平滑无锯齿。
    方框右侧保留与设计稿一致的缺口，让简化标识和完整字标一眼认得出是同一套。
    """
    SS = 8
    S = size * SS
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(canvas)

    lw = max(1, int(round(S * FRAME_STROKE)))
    inset = int(round(S * FRAME_INSET))

    # 绘图区：方框本身略高于宽，等比内接于画布
    fh = S - 2 * inset
    fw_box = int(round(fh * FRAME_ASPECT))
    x0 = (S - fw_box) // 2
    y0 = inset
    x1 = x0 + fw_box
    y1 = y0 + fh

    # 完整方框
    d.rectangle([x0, y0, x1, y1], outline=ink + (255,), width=lw)

    # 擦掉右边竖线中部的缺口（设计稿里「输」字正从这里穿出去）
    gy0 = y0 + (y1 - y0) * FRAME_GAP[0]
    gy1 = y0 + (y1 - y0) * FRAME_GAP[1]
    d.rectangle([x1 - lw * 2, gy0, x1 + lw * 2, gy1], fill=(0, 0, 0, 0))

    # 居中粗体 X（比原稿笔画更粗，小尺寸下才立得住）
    xw = max(1, int(round(S * X_STROKE)))
    pad = int(round(fw_box * X_PAD))
    cx0, cx1 = x0 + pad, x1 - pad
    cy0, cy1 = y0 + pad, y1 - pad
    d.line([(cx0, cy0), (cx1, cy1)], fill=ink + (255,), width=xw)
    d.line([(cx1, cy0), (cx0, cy1)], fill=ink + (255,), width=xw)

    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def dominant_ink(out):
    """取图案里最深的颜色，作为简化标识的描边色（与完整字标同色）。"""
    px = out.load()
    W, H = out.size
    best, best_sum = (51, 51, 51), None
    for y in range(0, H, 2):
        for x in range(0, W, 2):
            r, g, b, a = px[x, y]
            if a < 200:
                continue
            s = r + g + b
            if best_sum is None or s < best_sum:
                best_sum, best = s, (r, g, b)
    return best


def boost_alpha(rgba, k):
    """把 alpha 整体乘以 k（截顶 255），用于补偿细笔画缩小后的覆盖率损失。"""
    if k == 1.0:
        return rgba
    r, g, b, a = rgba.split()
    a = a.point(lambda v: min(255, int(round(v * k))))
    return Image.merge("RGBA", (r, g, b, a))


def write_ico(frames, path):
    """
    手工拼装 .ico，每个尺寸以 **PNG** 编码写入（Vista 起 Windows 支持）。

    Pillow 的 ICO 保存接口只能对整份文件统一缩放，无法为个别尺寸换源图，
    因此这里自己写 ICONDIR / ICONDIRENTRY，便于小尺寸使用简化标识。
    """
    import struct
    import io

    blobs = []
    for f in frames:
        buf = io.BytesIO()
        f.save(buf, format="PNG", optimize=True)
        blobs.append(buf.getvalue())

    n = len(frames)
    header = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16 * n
    entries, data = b"", b""
    for f, blob in zip(frames, blobs):
        s = f.size[0]
        entries += struct.pack(
            "<BBBBHHII",
            0 if s >= 256 else s,       # 宽（256 记 0）
            0 if s >= 256 else s,       # 高
            0, 0,                       # 调色板数 / 保留
            1, 32,                      # 颜色平面 / 位深
            len(blob), offset)
        data += blob
        offset += len(blob)

    with open(path, "wb") as fp:
        fp.write(header + entries + data)


def build():
    im = load_opaque(SRC)
    print("原图尺寸:", im.size)

    # ---- 0) 依据图片自身推断背景色 / 图案色，算出透明化阈值 ----
    white_cut, ink_cut, bg, ink_src = auto_thresholds(im)
    print("背景色 %s (亮度 %.1f) / 图案色 %s (亮度 %.1f)"
          % (bg, _lum(bg), ink_src, _lum(ink_src)))
    print("阈值: 透明 >%.1f / 不透明 <%.1f" % (white_cut, ink_cut))

    # ---- 1) 找内容边界（非背景区域）----
    bw = im.convert("L").point(lambda v: 0 if v > white_cut else 255)
    bbox = bw.getbbox()
    if bbox is None:
        raise SystemExit("未能识别出图案区域（整张图都是背景色？）")
    print("内容边界:", bbox)

    # ---- 2) 补留白成正方形（等比内接，不裁掉任何内容）----
    #
    # 注意：不能用"以内容中心裁正方形"的写法 —— 字标是宽高比 1.74 的宽图，
    # 那样裁出来的正方形里上下会空掉一半，图案在图标里显得又小又飘。
    # 这里改为：按内容包围盒裁下来，再等比内接到正方形画布中央，
    # 画布用背景色填充（不是黑色），随后统一走透明化。
    l, t, r, b = bbox
    content = im.crop((l, t, r, b))
    cw, ch = content.size
    side = int(round(max(cw, ch) * (1 + 2 * PAD_RATIO)))
    canvas = Image.new("RGB", (side, side), bg)
    canvas.paste(content, ((side - cw) // 2, (side - ch) // 2))
    crop = canvas
    print("补白后:", crop.size, "（图案 %dx%d，占比 %.0f%%）"
          % (cw, ch, 100.0 * cw / side))

    # ---- 3) 背景 → 透明（亮度映射 alpha，RGB 统一为图案色）----
    def to_alpha(v):
        if v >= white_cut:
            return 0
        if v <= ink_cut:
            return 255
        return int(round((white_cut - v) * 255.0 / (white_cut - ink_cut)))

    alpha = crop.convert("L").point(to_alpha)
    out = Image.new("RGBA", crop.size, ink_src + (255,))
    out.putalpha(alpha)

    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)

    # ---- 4) 界面展示用 PNG ----
    #
    # 这里**裁到字标本身**（574x332 → 256x148），不做正方形留白：
    # 设置窗体底部状态条只有 40px 高，若 PNG 是正方形的，
    # PictureBox 按 Zoom 缩放时会先被上下的空白占掉一半高度，
    # 字标只剩 20px 高；紧凑比例下字标才能占满整条高度。
    png_path = os.path.join(OUT_DIR, "XimeLogo.png")
    tw = 256
    th = max(1, int(round(tw * ch / float(cw))))
    body = Image.new("RGBA", content.size, ink_src + (255,))
    body.putalpha(content.convert("L").point(to_alpha))
    body = body.resize((tw, th), Image.Resampling.LANCZOS)

    # 再补一圈透明边距（3.5%）。字标是紧贴内容包围盒裁出来的，
    # 不留边的话方框线正好压在 PNG 边界上 —— 状态条把它缩到 36px 高绘制时，
    # 顶边那一行会被削掉（实测状态条里的方框缺一条顶线）。留边后不会。
    pad = max(2, int(round(tw * 0.035)))
    png = Image.new("RGBA", (tw + 2 * pad, th + 2 * pad), (0, 0, 0, 0))
    png.paste(body, (pad, pad))
    png.save(png_path)
    print("已写出:", png_path, os.path.getsize(png_path), "字节",
          "(%dx%d，字标 %dx%d + %dpx 透明边距)"
          % (png.width, png.height, tw, th, pad))

    # ---- 5) 多尺寸 ICO ----
    #
    # 不用 out.save(..., sizes=[...])：Pillow 无法对单个尺寸指定不同的源图。
    # 改为手工拼装 ICO：
    #   · 16/24/32px → make_simplified() 的简化标识
    #   · 其余尺寸   → 完整字标缩放
    # 所有条目都以 PNG 编码写回（Vista 起支持），比 BMP 编码小得多。
    ink = dominant_ink(out)
    print("简化标识描边色:", ink)

    frames = []
    for s in ICO_SIZES:
        if s in SIMPLIFIED_SIZES:
            frames.append(make_simplified(s, ink))
        else:
            frames.append(boost_alpha(out.resize((s, s), Image.Resampling.LANCZOS),
                                      WORDMARK_ALPHA_BOOST.get(s, 1.0)))

    ico_path = os.path.join(OUT_DIR, "XimeLogo.ico")
    write_ico(frames, ico_path)
    print("已写出:", ico_path, os.path.getsize(ico_path), "字节")

    # ---- 6) 尺寸对照预览（浅底 / 深底两行，方便肉眼确认可辨性）----
    margin, gap = 10, 8
    total_w = sum(ICO_SIZES) + gap * (len(ICO_SIZES) + 1)
    row_h = max(ICO_SIZES) + 2 * margin
    sheet = Image.new("RGB", (total_w, row_h * 2), (255, 255, 255))
    # 深色行：把半透明图标合成到深灰底上，模拟任务栏 / 深色主题
    dark = Image.new("RGB", (total_w, row_h), (32, 34, 38))
    for base in (sheet, dark):
        x = gap
        for f in frames:
            s = f.size[0]
            base.paste(f, (x, (row_h - s) // 2), f)
            x += s + gap
    sheet.paste(dark, (0, row_h))
    prev = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "_logo_sizes_preview.png")
    sheet.save(prev)
    print("尺寸对照预览:", prev, "（上排浅底 / 下排深底）")


if __name__ == "__main__":
    build()
