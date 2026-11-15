"""Candle - 150 mm tall, 50 mm wide pillar candle on a small brass tray.

The catalog gives 0.150 x 0.050 x 0.050 m and asks for a body, a wick and a
small base tray, one shell overall.  The model stands (a tray under a candle only
makes sense upright), so the 150 mm is the height and the 50 mm the tray
diameter: one lathe from the recessed tray underside, over the 6 mm tray rim,
in to the 31 mm candle foot, straight up the body, over the top bevel and up a
2 mm wick cone on the axis.  Because the wick is the last profile segment of the
same revolution, the whole candle including the wick is one closed surface.

12 segments and 8 bands give 192 triangles, inside the 180-triangle baseline
budget x 1.6 (288) if the profile keeps its seam groove.

Material pass (2026-11-15 rework): the old body was a 0.82 cream that clipped to
white, with a dark wick as the only second slot - the tray and the body were the
same colour, so the base disappeared.  Three slots now: warm paper-white wax, an
ink wick and a brass tray, and a 0.3 mm inward mould seam at 100 mm breaks the
long straight body so the wax reads as wax instead of a white cylinder.
"""

from prop_lib import lathe

ID = 'candle'
NAME_ZH = '小蜡烛'
CATEGORY = 'debris'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.150', 'game_longest_dim_m': '0.150'}
SOURCING = 'model ourselves：Ø0.050 x 0.150 m 小蜡烛，蜡体 + 烛芯 + 底部小托盘'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.150
TRI_BUDGET = 240
MATERIALS = [
    {'key': 'paper', 'name_zh': '暖白蜡体', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.55},
    {'key': 'ink', 'name_zh': '烛芯炭黑', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'brass', 'name_zh': '黄铜托盘', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
]
NOTES = ('12 segments, 10 profile points, 9 bands = 216 triangles.  Tray diameter '
         '0.0500 m and 6 mm tall with a 1.5 mm recessed underside; the candle '
         'body is 31 mm across and runs from the tray dish at 9 mm to 139 mm with '
         'a 0.3 mm mould seam groove at 99-101 mm; the top bevel closes to a 2 mm '
         'wick cone whose tip is the 0.1500 m height.  Slots: wax = paper key, '
         'wick = everything above z 146.8 mm, brass tray = every face below '
         '9.2 mm.  Upright because a base tray only reads when the candle '
         'stands.')

SEGMENTS = 12
WICK_FROM = 0.1468
TRAY_TOP = 0.0092

PAPER = (0.800, 0.775, 0.700)
INK = (0.030, 0.035, 0.045)
BRASS = (0.520, 0.380, 0.120)

PROFILE = [
    (0.0000, 0.0015),   # recessed tray underside centre
    (0.0250, 0.0000),   # tray rim, ground contact
    (0.0250, 0.0060),   # tray rim top
    (0.0155, 0.0090),   # tray dish into the candle foot
    (0.0155, 0.0990),   # candle body, lower half
    (0.0152, 0.1000),   # mould seam groove in
    (0.0155, 0.1010),   # mould seam groove out
    (0.0155, 0.1390),   # candle body, upper half
    (0.0135, 0.1462),   # top bevel
    (0.0016, 0.1470),   # flat top around the wick
    (0.0000, 0.1500),   # wick tip
]


def wick(centre):
    """Dark wick cone above the candle top."""
    return centre.z > WICK_FROM


def tray(centre):
    """Brass tray under the candle foot."""
    return centre.z < TRAY_TOP


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'paper', PAPER, 0.55, smooth=True,
          extras=[('ink', INK, 0.75, wick),
                  ('brass', BRASS, 0.30, tray)])
