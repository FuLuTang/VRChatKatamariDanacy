"""Cookie - 7 cm biscuit with five raised chocolate-chip domes.

The previous version pressed four deep wells into the top at 0/90/180/270 and
painted the surrounding faces dark.  At preview size that read as a pie with
four wedges cut out, not as a biscuit: the dents reached the rim and the dark
patches were sectors, not chips.  This version keeps the same diameter, the
same 0.9 cm overall thickness, the same pivot and the same filenames, but the
top is now a low dome with five small domes unioned on top; each dome is solid
geometry with a rounded shoulder and its own dark material, so the chips cast
a real bump instead of inverting the surface.

The base disc uses 24 segments, the same six-scallop outline and the same
sampled angles as before, which is what keeps the final X/Y exactly
0.070000 x 0.068856 m after ``finish`` scales the longest axis.  The chips are
5.8 mm domes with a 3.4 mm base; their tops reach the old 9.5 mm raw height, so
the final Z stays 0.009372 m.  No texture, no UVs, one closed shell after the
exact union.
"""

import math

from prop_lib import disc_grid, lathe

ID = 'cookie'
NAME_ZH = '饼干'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.07
CSV_GAME_LONGEST_M = 0.09
SOURCING = 'model ourselves：Ø7 cm、厚 0.9 cm 圆饼，5 颗凸起巧克力豆'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.07
TRI_BUDGET = 340
MATERIALS = [
    {'key': 'dough', 'name_zh': '饼干面团', 'linear_rgb': [0.420, 0.240, 0.100],
     'roughness': 0.80},
    {'key': 'chip', 'name_zh': '巧克力豆', 'linear_rgb': [0.055, 0.030, 0.020],
     'roughness': 0.60},
]
NOTES = ('24 segments, two top rings (0.62 / 1.0), flat underside: 144 base '
         'triangles.  Five chip domes, each 6-sided with a 3.4 mm base and a '
         '3.7 mm rise, are unioned into the base; the visible result is about '
         '326 triangles after the exact solver.  Chips sit at (0.003, 0.001) '
         'and on a 0.0205 m ring at 35/125/215/305 degrees, well inside the '
         'outline.  The six-scallop 3.5% outline is unchanged from the old '
         'cookie; with the same 24 sampled angles the raw AABB and the finish '
         'scale are unchanged, so the exported size and pivot match exactly.')

SEGMENTS = 24
RING_FRACTIONS = [0.62, 1.0]
RADIUS = 0.035
THICK_CENTRE = 0.0070
DOME = 0.0012
BASE_TOP_EDGE = THICK_CENTRE - DOME

CHIP_RADIUS = 0.0034
CHIP_BASE_Z = 0.0058
CHIP_HEIGHT = 0.0037
CHIP_PROFILE = [
    (0.0000, 0.0000),   # buried bottom centre
    (0.0034, 0.0000),   # base ring, below the biscuit surface
    (0.0018, 0.0030),   # rounded shoulder
    (0.0000, 0.0037),   # top, the old 9.5 mm raw height
]
CHIP_RING_R = 0.0205
CHIP_ANGLES = [35.0, 125.0, 215.0, 305.0]
CHIP_POSITIONS = [(0.0030, 0.0010)] + [
    (CHIP_RING_R * math.cos(math.radians(a)),
     CHIP_RING_R * math.sin(math.radians(a))) for a in CHIP_ANGLES]


def outline(angle):
    """Six shallow scallops so the edge reads as crumbled, not stamped."""
    return RADIUS * (1.0 + 0.035 * math.sin(6.0 * angle + 0.4))


def top(x, y):
    z = THICK_CENTRE - DOME * ((x * x + y * y) / (RADIUS * RADIUS))
    return z


def _chip_mesh(cx, cy):
    bm = lathe(CHIP_PROFILE, segments=6)
    for v in bm.verts:
        v.co.x += cx
        v.co.y += cy
        v.co.z += CHIP_BASE_Z
    return bm


def build(b):
    base = b.add(disc_grid(SEGMENTS, RING_FRACTIONS, top, bottom_z=0.0,
                           radius=RADIUS, radius_fn=outline),
                 'dough', (0.420, 0.240, 0.100), 0.80, smooth=False,
                 name='biscuit')
    chips = []
    for i, (cx, cy) in enumerate(CHIP_POSITIONS):
        chips.append(b.add(_chip_mesh(cx, cy), 'chip',
                           (0.055, 0.030, 0.020), 0.60, smooth=True,
                           name='chip%d' % i))
    b.boolean_union(base, chips)

    # Assign after the union: the modifier's material transfer is unreliable,
    # and the chip faces are exactly the ones standing above the biscuit top.
    me = base.data
    me.materials.clear()
    me.materials.append(b.material('dough', (0.420, 0.240, 0.100), 0.80))
    me.materials.append(b.material('chip', (0.055, 0.030, 0.020), 0.60))
    for poly in me.polygons:
        c = poly.center
        near = any((c.x - cx) ** 2 + (c.y - cy) ** 2
                   < (CHIP_RADIUS * 0.96) ** 2
                   for (cx, cy) in CHIP_POSITIONS)
        poly.material_index = 1 if near and c.z > top(c.x, c.y) + 0.0005 else 0
