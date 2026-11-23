"""生成大厅面板和结算卡共用的字符集与子集字体，供 Unity 操作员生成 TMP 字体资产。
参见 Docs/Design/StoryMode/HUB_PANEL_PREFAB.md「字体」。

    python3 Tools/hubart/build_hub_font.py            # charset + font
    python3 Tools/hubart/build_hub_font.py --charset-only

产物：
  Tools/hubart/hub_panel_charset.txt
      排序后的一行码位：可打印 ASCII、面板与结算卡文案、关名、设置箭头和指定保留字。
      NUnit 的 HubPanelCharsetTests 用它检查大厅面板格式化器的实际输出。
  VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans-Bold.otf
      取 Noto Sans CJK SC Bold 的子集并按 SIL OFL 1.1 改名为 HubPanelSans。
      来源为系统 fonts-noto-cjk 包（NotoSansCJK-Bold.ttc 索引 2 为 SC，覆盖日语假名）。
      同目录保留 OFL.txt；仅在缺少许可文件时生成。

生成字体需要 fontTools（可装在虚拟环境里）；--charset-only 只依赖 Python 3。
"""

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
RUNTIME = os.path.join(REPO, "VRChatKatamariDanacyUnity", "Assets", "VRC_Katamari", "Runtime")
# 新增或移动文案时更新此表，只登记实际写入大厅面板或结算卡的来源；不要扫描目录。
# 第 2 项是字符串字面量的选取规则。Launcher 只取传入结算卡的导航提示，不取大厅状态或日志。
# 结算卡的呈交标题、计量、星标和评语来自 Presenter；若之后增加独立的关卡评语覆盖，也要登记其来源。
STRING_LITERAL = r'"((?:[^"\\\n]|\\.)*)"'
TEXT_SOURCES = (
    ("Story/Hub/HubPanelTextMath.cs", STRING_LITERAL),
    ("Story/StoryResultPresenter.cs", STRING_LITERAL),
    ("Hub/HubLevelLauncher.cs", r"\bSetReturnHint\(\s*" + STRING_LITERAL + r"\s*\)"),
)
LEVELS = os.path.join(REPO, "Docs", "Design", "StoryMode", "hub_levels.json")
CHARSET = os.path.join(HERE, "hub_panel_charset.txt")
FONT_DIR = os.path.join(REPO, "VRChatKatamariDanacyUnity", "Assets", "VRC_Katamari", "Art", "Hub",
                        "Fonts")
FONT_OUT = os.path.join(FONT_DIR, "HubPanelSans-Bold.otf")
SOURCE_TTC = "/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc"
SOURCE_INDEX = 2  # 简体中文版 Noto Sans CJK Bold
SOURCE_LICENCE = "/usr/share/doc/fonts-noto-cjk/copyright"
FAMILY = "HubPanelSans"
SETTINGS_ARROWS = "◀▶"
# 指定保留、但不来自上面生成源的字（不扫描进行中 HUD）：
#   结算卡正文用字：余击剩去坐当流程第
#   大厅第一批面板设计稿（.work 设计稿 hub_batch1：对战卡、对战屏、准备区、观战、翻页）的新字与
#   「2–4 人」的连接号；这些文案还没进 HubPanelTextMath，写进代码后可从这里删掉重复的字。
REQUIRED_EXTRA = ("余击剩去坐当流程第"
                  "交人他传位其分剧加名圈备定情战扣条瞰站观走跟送钟锁闲随项鸟"
                  "–")
# 本次只补字：保留旧字符集已有、当前生成源不再提供的码位，避免共享字体回退。
LEGACY_EXTRA = "松離"


def collect():
    chars = set(chr(c) for c in range(0x20, 0x7F))
    for path, pattern in TEXT_SOURCES:
        with open(os.path.join(RUNTIME, path), encoding="utf-8") as fh:
            for literal in re.findall(pattern, fh.read()):
                chars.update(literal)
    with open(LEVELS, encoding="utf-8") as fh:
        levels = json.load(fh)

    def walk(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "name" and isinstance(value, str):
                    chars.update(value)
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(levels)
    chars.update(SETTINGS_ARROWS)
    chars.update(REQUIRED_EXTRA)
    chars.update(LEGACY_EXTRA)
    return "".join(sorted(c for c in chars if ord(c) >= 0x20 and c != "\\"))


def licence_text():
    """The OFL 1.1 text from the Debian copyright file (its 'License: SIL-1.1' stanza)."""
    out = []
    inside = False
    with open(SOURCE_LICENCE, encoding="utf-8") as fh:
        for line in fh:
            if line.startswith("License: SIL-1.1"):
                inside = True
                continue
            if inside:
                if line.strip() and not line.startswith(" "):
                    break
                text = line[1:] if line.startswith(" ") else line
                out.append("" if text.strip() == "." else text.rstrip("\n"))
    return ("HubPanelSans-Bold.otf is a subset of Noto Sans CJK SC Bold.\n"
            "Copyright 2014-2021 Adobe (http://www.adobe.com/), with Reserved Font Name 'Source'.\n"
            "Modified (subset to the hub panels' characters, renamed HubPanelSans) for VRC Katamari.\n\n"
            + "\n".join(out).strip() + "\n")


def build_font(charset):
    from fontTools import subset
    from fontTools.ttLib import TTCollection

    font = TTCollection(SOURCE_TTC).fonts[SOURCE_INDEX]
    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.notdef_outline = True
    subsetter = subset.Subsetter(options)
    subsetter.populate(text=charset)
    subsetter.subset(font)
    name = font["name"]
    for record in list(name.names):
        if record.nameID in (1, 4, 16):
            record.string = FAMILY if record.nameID != 4 else FAMILY + " Bold"
        elif record.nameID == 6:
            record.string = FAMILY + "-Bold"
        elif record.nameID == 3:
            record.string = FAMILY + "-Bold;subset"
    if "CFF " in font:
        cff = font["CFF "].cff
        cff.fontNames = [FAMILY + "-Bold"]
        top = cff.topDictIndex[0]
        top.FullName = FAMILY + " Bold"
        top.FamilyName = FAMILY
    os.makedirs(FONT_DIR, exist_ok=True)
    font.save(FONT_OUT)
    licence = os.path.join(FONT_DIR, "OFL.txt")
    if not os.path.exists(licence):
        with open(licence, "x", encoding="utf-8") as fh:
            fh.write(licence_text())
    cmap = font.getBestCmap()
    missing = [c for c in charset if ord(c) not in cmap and c != " "]
    print("[hubfont] %s bytes=%d glyphs=%d missing=%d%s" % (
        os.path.relpath(FONT_OUT, REPO), os.path.getsize(FONT_OUT), len(font.getGlyphOrder()),
        len(missing), "" if not missing else " (" + "".join(missing) + ")"))
    return not missing


def main():
    charset = collect()
    with open(CHARSET, "w", encoding="utf-8") as fh:
        fh.write(charset + "\n")
    print("[hubfont] %s chars=%d non-ascii=%d" % (
        os.path.relpath(CHARSET, REPO), len(charset), sum(1 for c in charset if ord(c) > 0x7E)))
    if "--charset-only" in sys.argv:
        return 0
    return 0 if build_font(charset) else 1


if __name__ == "__main__":
    sys.exit(main())
