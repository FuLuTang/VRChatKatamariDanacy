"""Writes the hub panels' character set and the subset font the Unity operator turns into the
panels' TextMesh Pro font asset (Docs/Design/StoryMode/HUB_PANEL_PREFAB.md, "Font").

    python3 Tools/hubart/build_hub_font.py            # charset + font
    python3 Tools/hubart/build_hub_font.py --charset-only

Outputs:
  Tools/hubart/hub_panel_charset.txt
      Every character the panels can show: printable ASCII, every non-ASCII character in the
      string literals of HubPanelTextMath.cs (all three languages), the level names in
      Docs/Design/StoryMode/hub_levels.json, and the two settings arrows. One line, sorted.
      The NUnit test HubPanelCharsetTests checks the real formatter output against this file,
      so a new word that is missing here fails a test instead of drawing a square.
  VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans-Bold.otf
      Noto Sans CJK SC Bold reduced to that character set (fontTools subsetter), renamed
      "HubPanelSans" because a subset is a Modified Version under the SIL Open Font License 1.1;
      OFL.txt (the licence text) is written next to it. Source: the system's
      fonts-noto-cjk package (/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc, index 2 =
      SC). SC covers the kana used by the Japanese strings.

Needs fontTools for the font (pip install fonttools, e.g. in a venv); --charset-only needs
nothing beyond Python 3.
"""

import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
TEXT_MATH = os.path.join(REPO, "VRChatKatamariDanacyUnity", "Assets", "VRC_Katamari", "Runtime",
                         "Story", "Hub", "HubPanelTextMath.cs")
LEVELS = os.path.join(REPO, "Docs", "Design", "StoryMode", "hub_levels.json")
CHARSET = os.path.join(HERE, "hub_panel_charset.txt")
FONT_DIR = os.path.join(REPO, "VRChatKatamariDanacyUnity", "Assets", "VRC_Katamari", "Art", "Hub",
                        "Fonts")
FONT_OUT = os.path.join(FONT_DIR, "HubPanelSans-Bold.otf")
SOURCE_TTC = "/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc"
SOURCE_INDEX = 2  # Noto Sans CJK SC Bold
SOURCE_LICENCE = "/usr/share/doc/fonts-noto-cjk/copyright"
FAMILY = "HubPanelSans"
EXTRA = "◀▶"


def collect():
    chars = set(chr(c) for c in range(0x20, 0x7F))
    with open(TEXT_MATH, encoding="utf-8") as fh:
        source = fh.read()
    for literal in re.findall(r'"((?:[^"\\\n]|\\.)*)"', source):
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
    chars.update(EXTRA)
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
    with open(os.path.join(FONT_DIR, "OFL.txt"), "w", encoding="utf-8") as fh:
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
