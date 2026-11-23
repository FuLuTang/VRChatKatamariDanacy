"""生成大厅面板的 uGUI 精灵（方向一「暖木纸屋」：能点的东西有厚度和落影，只看的屏是平的），
并把每张精灵的导入设置（尺寸、9-slice 边框）写进 hub_layout.json 的 "sprites" 段。

    python3 Tools/hubart/build_hub_sprites.py

只依赖 pycairo（本机系统 Python 已有）。产物：
  VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Art/Hub/Sprites/<名>.png   （不生成 .meta，交给 Unity）
  Tools/hubart/hub_layout.json 的 "sprites"                                  （装配器据此设导入参数）

精灵是预先上色的（Image.color 保持白色），这样 Button 用 Sprite Swap 四态时，悬停的金黄外圈和按钮本身的
颜色可以同时存在；颜色和规格见 Docs/Design/StoryMode/HUB_PANEL_PREFAB.md「方向一外观」。
颜色只在这里写一份：改色就改 COLOURS 再重跑。
"""

import json
import math
import os

import cairo

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
LAYOUT = os.path.join(HERE, "hub_layout.json")
ASSET_DIR = "Assets/VRC_Katamari/Art/Hub/Sprites"
OUT_DIR = os.path.join(REPO, "VRChatKatamariDanacyUnity", ASSET_DIR)

COLOURS = {
    "paper": "#ECE3CF",       # 纸米：卡片、能点的板
    "ink": "#3D3F3B",         # 墨灰
    "gold": "#E8C14C",        # 悬停外圈、「上次玩的」
    "tomato": "#D9503C", "tomato_edge": "#9E3427",   # 剧情主按钮
    "brick": "#B4614A", "brick_edge": "#8A4535",     # 对战主按钮、对战卡条
    "oat": "#E2D8C2", "oat_edge": "#BFB399",         # 次按钮、翻页、设置箭头
    "disabled": "#CFC9BC", "disabled_edge": "#B3AC9E",
    "sage": "#88A27E",        # 剧情卡条
}


def rgb(h, a=1.0):
    h = h.lstrip("#")
    return (int(h[0:2], 16) / 255, int(h[2:4], 16) / 255, int(h[4:6], 16) / 255, a)


def rrect(c, x, y, w, h, r):
    r = max(0.0, min(r, w / 2, h / 2))
    c.new_sub_path()
    c.arc(x + w - r, y + r, r, -math.pi / 2, 0)
    c.arc(x + w - r, y + h - r, r, 0, math.pi / 2)
    c.arc(x + r, y + h - r, r, math.pi / 2, math.pi)
    c.arc(x + r, y + r, r, math.pi, 1.5 * math.pi)
    c.close_path()


def soft_shadow(c, x, y, w, h, r, alpha, spread):
    """Cheap blur: concentric rounded rects with falling alpha."""
    steps = max(1, int(spread))
    for i in range(steps, 0, -1):
        rrect(c, x - i, y - i, w + 2 * i, h + 2 * i, r + i)
        c.set_source_rgba(0.12, 0.09, 0.05, alpha * (1 - i / (steps + 1)) / steps * 2.2)
        c.fill()


def surface(w, h):
    s = cairo.ImageSurface(cairo.FORMAT_ARGB32, w, h)
    return s, cairo.Context(s)


# Each builder returns (surface, border[l, b, r, t]) in pixels.

def card(state):
    """Level card face (the card Button's target graphic). 128 x 128, 9-slice border 40.
    Normal: paper, soft shadow; Highlighted: + gold ring; Pressed: shadow nearly gone;
    Disabled: paper greyed. Radius 28 px (= 46 mm at the card's ~1.6 mm/px slice scale)."""
    W = H = 128
    s, c = surface(W, H)
    pad, r = 12, 28
    x, y, w, h = pad, pad - 4, W - 2 * pad, H - 2 * pad
    face = COLOURS["paper"] if state != "Disabled" else "#E3DED2"
    if state == "Pressed":
        y += 3
        soft_shadow(c, x, y + 2, w, h, r, 0.10, 3)
    else:
        soft_shadow(c, x, y + 6, w, h, r, 0.22, 7)
    if state == "Highlighted":
        rrect(c, x - 7, y - 7, w + 14, h + 14, r + 7)
        c.set_source_rgba(*rgb(COLOURS["gold"]))
        c.fill()
    rrect(c, x, y, w, h, r)
    c.set_source_rgba(*rgb(face))
    c.fill()
    return s, [40, 40, 40, 40]


def pill(face_key, edge_key, state):
    """Solid button: a pill with a bottom thickness band (edge colour) and a soft shadow.
    256 x 128; border 56 left/right (round ends stay round), 40 top/bottom."""
    W, H = 256, 128
    s, c = surface(W, H)
    face, edge = COLOURS[face_key], COLOURS[edge_key]
    if state == "Disabled":
        face, edge = COLOURS["disabled"], COLOURS["disabled_edge"]
    x, w, h = 18, W - 36, 72
    y = 16
    depth = 12
    if state == "Pressed":
        y += 9
        depth = 3
    r = h / 2
    shadow_alpha = 0.06 if state == "Pressed" else 0.18
    soft_shadow(c, x, y + depth + 4, w, h, r, shadow_alpha, 5)
    if state == "Highlighted":
        rrect(c, x - 8, y - 8, w + 16, h + depth + 16, r + 8)
        c.set_source_rgba(*rgb(COLOURS["gold"]))
        c.fill()
    rrect(c, x, y + depth, w, h, r)
    c.set_source_rgba(*rgb(edge))
    c.fill()
    rrect(c, x, y, w, h, r)
    c.set_source_rgba(*rgb(face))
    c.fill()
    c.save()
    rrect(c, x, y, w, h, r)
    c.clip()
    c.rectangle(x, y, w, h * 0.22)
    c.set_source_rgba(1, 1, 1, 0.14)
    c.fill()
    c.restore()
    return s, [56, 40, 56, 40]


def rounded():
    """Plain white rounded rectangle, tinted by Image.color (board face, title plaque, chips,
    setting stripes, screen rows). 128 x 128, radius 48, border 48."""
    s, c = surface(128, 128)
    rrect(c, 0, 0, 128, 128, 48)
    c.set_source_rgba(1, 1, 1, 1)
    c.fill()
    return s, [48, 48, 48, 48]


def strip(versus):
    """The card's left edge strip: sage solid (story) or brick with darker diagonal stripes
    (versus; readable without colour). 32 x 512, stretched along the card height; border
    0 (simple stretch, the card height is fixed)."""
    W, H = 32, 512
    s, c = surface(W, H)
    c.rectangle(0, 0, W, H)
    c.set_source_rgba(*rgb(COLOURS["brick" if versus else "sage"]))
    c.fill()
    if versus:
        c.set_source_rgba(*rgb(COLOURS["brick_edge"]))
        c.set_line_width(9)
        for k in range(-2, H // 24 + 3):
            c.move_to(-8, k * 24 + 30)
            c.line_to(W + 8, k * 24)
        c.stroke()
    return s, [0, 0, 0, 0]


def versus_badge():
    """Shield badge behind the versus card's 「对战」 label (TMP child). 256 x 192, no slicing."""
    W, H = 256, 192
    s, c = surface(W, H)
    x0, y0, w = 16, 8, W - 32
    c.move_to(x0, y0)
    c.line_to(x0 + w, y0)
    c.line_to(x0 + w, y0 + 110)
    c.curve_to(x0 + w, y0 + 150, x0 + w / 2, y0 + 176, x0 + w / 2, y0 + 176)
    c.curve_to(x0 + w / 2, y0 + 176, x0, y0 + 150, x0, y0 + 110)
    c.close_path()
    c.set_source_rgba(*rgb(COLOURS["brick"]))
    c.fill()
    return s, [0, 0, 0, 0]


def specs():
    out = []
    for state in ("Normal", "Highlighted", "Pressed", "Disabled"):
        out.append(("HubCard_" + state, lambda st=state: card(st)))
    for kind, face, edge in (("Go", "tomato", "tomato_edge"), ("Versus", "brick", "brick_edge"),
                             ("Plain", "oat", "oat_edge")):
        for state in ("Normal", "Highlighted", "Pressed", "Disabled"):
            out.append(("HubButton%s_%s" % (kind, state),
                        lambda f=face, e=edge, st=state: pill(f, e, st)))
    out.append(("HubRounded", rounded))
    out.append(("HubStrip_Story", lambda: strip(False)))
    out.append(("HubStrip_Versus", lambda: strip(True)))
    out.append(("HubBadge_Versus", versus_badge))
    return out


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    entries = []
    for name, build in specs():
        s, border = build()
        path = os.path.join(OUT_DIR, name + ".png")
        s.write_to_png(path)
        size = max(s.get_width(), s.get_height())
        entries.append({"name": name, "file": ASSET_DIR + "/" + name + ".png",
                        "maxSize": 1 << (size - 1).bit_length(), "border": border})
    with open(LAYOUT, encoding="utf-8") as fh:
        layout = json.load(fh)
    layout["sprites"] = entries
    with open(LAYOUT, "w", encoding="utf-8") as fh:
        json.dump(layout, fh, ensure_ascii=False, indent=1)
        fh.write("\n")
    print("[hubsprites] %d sprites -> %s; hub_layout.json sprites updated" % (
        len(entries), os.path.relpath(OUT_DIR, REPO)))


if __name__ == "__main__":
    main()
