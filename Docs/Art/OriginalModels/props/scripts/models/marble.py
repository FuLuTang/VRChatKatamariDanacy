"""Marble - 16 mm opaque glass marble with a helical colour ribbon.

A 12-segment lathe sphere (7 profile points, 120 triangles) whose surface is
split into three material slots: the blue-green glass body, a pale cream ribbon
that winds about one and a half turns from the bottom pole to the top, and a dark
chitin thread running along the ribbon's trailing edge.  A lathe is used instead
of a UV sphere because 12 segments puts a vertex on both +X and +Y, so the built
AABB is exactly 0.016 x 0.016 m.

Material pass (2026-11-15 rework): the old body was a 0.36/0.52/0.52 pale teal
with an amber band - two light slots and no dark one, so the marble read as one
washed-out ball.  The body is now the darker glass_blue palette key, the ribbon
is the pale shell key (0.56 vs 0.36 linear peak, a clear two-tone read) and the
dark thread gives the piece its required low-value slot without a single extra
triangle.

A real internal swirl would need a transparent shell, which this prop set forbids
(flat opaque Principled colours only, no transparency), so the ribbon is a colour
band on the surface.  The band is a deterministic function of the face centre, so
a rebuild produces the same geometry digest.
"""

import math

from prop_lib import lathe

ID = 'marble'
NAME_ZH = '玻璃弹珠'
CATEGORY = 'toy'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.016', 'game_longest_dim_m': '0.016'}
SOURCING = 'model ourselves：Ø0.016 m 低多边形球 + 表面螺旋彩带（材质分割）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.016
TRI_BUDGET = 120
MATERIALS = [
    {'key': 'glass_blue', 'name_zh': '青玻璃芯', 'linear_rgb': [0.180, 0.330, 0.360],
     'roughness': 0.22},
    {'key': 'shell', 'name_zh': '螺旋浅色带', 'linear_rgb': [0.560, 0.470, 0.330],
     'roughness': 0.42},
    {'key': 'chitin', 'name_zh': '螺旋暗线', 'linear_rgb': [0.090, 0.075, 0.060],
     'roughness': 0.30},
]
NOTES = ('12 segments, 7 profile points = 120 triangles, radius 8 mm, both poles '
         'on the Z axis, bottom pole on z = 0.  Three slots sampled from the face '
         'centre: phase = 1 turn of azimuth + 0.5 rises of height; the cream band '
         'is the first 0.22 of the phase and the dark thread the next 0.12, so '
         'the pair runs diagonally instead of breaking into patches (one azimuth '
         'step advances the phase by 1/12 and one ring by 1/12).  Two light slots '
         'plus one low-value slot; no transparency, no UVs.')

SEGMENTS = 12
RADIUS = 0.008
AZIMUTH_TURNS = 1.0
HEIGHT_RISES = 0.5
BAND_WIDTH = 0.22
THREAD_WIDTH = 0.12

GLASS = (0.180, 0.330, 0.360)
SHELL = (0.560, 0.470, 0.330)
CHITIN = (0.090, 0.075, 0.060)

# Spherical profile sampled at the band boundaries, bottom pole to top pole.
PROFILE = [
    (0.0000, 0.0000),
    (0.0048, 0.0016),
    (0.0069, 0.0040),
    (0.0080, 0.0080),
    (0.0069, 0.0120),
    (0.0048, 0.0144),
    (0.0000, 0.0160),
]


def phase(centre):
    theta = math.atan2(centre.y, centre.x) / (2.0 * math.pi)
    rise = centre.z / (2.0 * RADIUS)
    return (AZIMUTH_TURNS * theta + HEIGHT_RISES * rise) % 1.0


def swirl(centre):
    """True on the pale helical ribbon faces."""
    return phase(centre) < BAND_WIDTH


def thread(centre):
    """True on the dark thread trailing the ribbon."""
    return BAND_WIDTH <= phase(centre) < BAND_WIDTH + THREAD_WIDTH


def build(b):
    b.add(lathe(PROFILE, segments=SEGMENTS),
          'glass_blue', GLASS, 0.22, smooth=True,
          extras=[('shell', SHELL, 0.42, swirl),
                  ('chitin', CHITIN, 0.30, thread)])
