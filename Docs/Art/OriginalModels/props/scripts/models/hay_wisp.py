"""Hay wisp - 8.5 cm pinch of dry hay, tied with a dark twine band.

DungBeetleMeadow catalogue t0: 0.085 x 0.045 x 0.030 m, shape_class long,
216/256 tris, Z0 巢穴口 scatter.

One closed swept shell (a bundle, not a bush): the cross-section is an ellipse
carrying SIX deep lobes (12 segments = two samples per strand), so from the end
the wisp is one closed outline while from the side it separates into six
pressed strands - the failure mode for "a few blades of grass" would be several
shells.  The lobe depth rises from 42 % mid-bundle to 55 % at both tips, so the
ends break up into straw points, and the lobe phase drifts 0.35 rad per station
so the strands cross instead of running in six parallel grooves.

Two things were missing after the first review (one flat straw slot, no
detail): a tied band and a colour break.  Stations 0.0080-0.0195 carry a 6 %
radius swell - the twine wrap - and are split into straw_dark shoulders with a
2.5 mm ink cord between them, so the wisp reads as "hay tied up", not as a
yellow pod.

Colour: straw body (0.368), straw_dark band (0.171), ink cord (0.035).
"""

import math

from prop_lib import TAU, tube

ID = 'hay_wisp'
NAME_ZH = '一撮干草'
CATEGORY = 'grass_plants'
SOURCING = ('model ourselves：DungBeetleMeadow catalogue t0 '
            '0.085×0.045×0.030 m 干草絮（Z0 巢穴口散落物）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.085
TRI_BUDGET = 256
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.085', 'game_longest_dim_m': '0.085'}
STRAW = (0.470, 0.360, 0.140)
STRAW_DARK = (0.230, 0.165, 0.060)
INK = (0.030, 0.035, 0.045)
MATERIALS = [
    {'key': 'straw', 'name_zh': '干草土黄', 'linear_rgb': list(STRAW),
     'roughness': 0.90},
    {'key': 'straw_dark', 'name_zh': '草束阴影', 'linear_rgb': list(STRAW_DARK),
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '捆绳暗带', 'linear_rgb': list(INK),
     'roughness': 0.75},
]
NOTES = ('9 stations x 12 segments swept elliptical section with six lobes '
         '(depth 42 % mid, 55 % at the tips, phase +0.35 rad per station): 192 '
         'side triangles + 24 cap = 216, one closed shell, no boolean.  '
         'Half-width 0.0163 m, half-height 0.0118 m at the fattest station; '
         'taper list 0.42/0.86/1.00/1.04/1.06/1.06/1.04/0.82/0.40.  Tie band '
         'x 0.0080-0.0195 with a 6 % radius swell: straw_dark shoulders and a '
         '2.5 mm ink cord at x 0.0130-0.0155.  The shell is shifted so its '
         'lowest vertex sits 0.4 mm above z = 0.  Overall 0.0850 x 0.0455 x '
         '0.0300 m.')

# (x, taper factor, z centre)
STATIONS = [
    (-0.0425, 0.42, 0.0166),
    (-0.0300, 0.86, 0.0176),
    (-0.0140, 1.00, 0.0181),
    (0.0080, 1.04, 0.0183),
    (0.0130, 1.06, 0.0182),
    (0.0155, 1.06, 0.0181),
    (0.0195, 1.04, 0.0179),
    (0.0300, 0.82, 0.0170),
    (0.0400, 0.40, 0.0160),
]
HALF_Y = 0.0163
HALF_Z = 0.0118
LOBE_DEPTH = 0.42
LOBE_TWIST = 0.35
BAND_LO, BAND_HI = 0.0080, 0.0195
CORD_LO, CORD_HI = 0.0130, 0.0155


def _section(index, angle, radius):
    """Ellipse with six lengthwise strands; deeper at both tips."""
    c, s = math.cos(angle), math.sin(angle)
    base = HALF_Y * HALF_Z / math.sqrt((HALF_Y * c) ** 2
                                       + (HALF_Z * s) ** 2)
    depth = LOBE_DEPTH + 0.13 * abs(index - 3.5) / 3.5
    lobe = 1.0 + depth * math.cos(6.0 * angle + math.pi + LOBE_TWIST * index)
    return radius * base * lobe


def build(b):
    points = [(x, 0.0, z) for x, _t, z in STATIONS]
    radii = [t for _x, t, _z in STATIONS]
    bm = tube(points, radii, segments=12, section_fn=_section)
    zmin = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z -= zmin - 0.0004

    def is_cord(centre):
        return CORD_LO <= centre.x <= CORD_HI

    def is_band(centre):
        return (BAND_LO <= centre.x < CORD_LO) or (CORD_HI < centre.x <= BAND_HI)

    b.add(bm, 'straw', STRAW, 0.90, smooth=False, name='wisp',
          extras=[('straw_dark', STRAW_DARK, 0.92, is_band),
                  ('ink', INK, 0.75, is_cord)])
