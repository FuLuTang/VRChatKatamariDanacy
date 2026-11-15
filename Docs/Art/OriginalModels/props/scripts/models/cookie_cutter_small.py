"""Small cookie cutter - 100 x 80 x 25 mm open oval ring with a rolled top edge.

The catalog asks for a thin-walled ring whose middle is genuinely open, with a
curled rim.  Like the tape roll this is a revolved *closed cross-section loop*
(bottom cutting edge, outer wall, rolled top, inner side, back down the inner
wall) with both lathe caps off, so the seam rings weld in ``finish`` and the
result is one closed shell around an empty opening.  The oval comes from a
per-segment radius factor that maps the revolved circle onto an ellipse with
semi-axes 50 x 40 mm; the same factor scales the 1.8 mm wall, so the wall stays
thin all the way round.

16 segments put a vertex exactly on +X and +Y, which is what makes the built AABB
exactly 0.100 x 0.080 m; 5 bands give 160 triangles, inside the 180 budget.

Material pass (2026-11-15 rework): one 0.62/0.63/0.65 bright steel slot read as a
pale grey ring.  Three slots now, still no extra geometry: a brass bead on the
rolled rim, steel on the outer wall and the cutting bevel, and the dark oxidised
slot on every face whose normal points back at the axis (the inner wall and the
bead's inner flank).  That inward normal test is what separates the inside of a
thin ring from its outside - a radius or height test cannot.
"""

import math

from prop_lib import lathe

ID = 'cookie_cutter_small'
NAME_ZH = '小饼干模'
CATEGORY = 'tableware'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.100', 'game_longest_dim_m': '0.100'}
SOURCING = 'model ourselves：0.100 x 0.080 x 0.025 m 椭圆薄壁模圈，顶部卷边'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.100
TRI_BUDGET = 180
MATERIALS = [
    {'key': 'metal', 'name_zh': '不锈钢外壁', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'brass', 'name_zh': '黄铜卷边', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'metal_dark', 'name_zh': '内壁氧化', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
]
NOTES = ('16 segments x 5 bands = 160 triangles, one closed shell with a real '
         'through opening.  Outer ellipse 0.100 x 0.080 m (semi-axes 0.050 / '
         '0.040), height 0.0250 m.  Wall 1.8 mm at the cutting edge, widening to '
         '3.8 mm under the rolled bead; the bead is the 46.2 mm inner ring at '
         '22.5 mm height that bulges into the opening, so the top edge reads as a '
         'roll instead of a knife edge.  The profile starts and ends at '
         '(0.0482, 0.0000) with no caps, so finish() welds the seam.  Slots: brass '
         'above z 22.8 mm (both bead bands), metal_dark where the face normal has '
         'a radial component below -0.3 (inner wall and bead inner flank), steel '
         'elsewhere.')

SEGMENTS = 16
SEMI_X = 0.0500
SEMI_Y = 0.0400
BEAD_Z = 0.0228

METAL = (0.430, 0.450, 0.470)
BRASS = (0.520, 0.380, 0.120)
METAL_DARK = (0.090, 0.095, 0.105)

PROFILE = [
    (0.0482, 0.0000),   # inner bottom edge (seam, repeated at the end)
    (0.0500, 0.0008),   # outer bottom bevel
    (0.0500, 0.0225),   # outer wall
    (0.0478, 0.0250),   # rolled bead crown
    (0.0462, 0.0225),   # bead inner side, bulging into the opening
    (0.0482, 0.0000),   # inner wall back down to the cutting edge
]


def oval(point_index, segment_index, radius):
    """Map the revolved circle onto a 50 x 40 mm ellipse."""
    a = 2.0 * math.pi * segment_index / SEGMENTS
    c, s = math.cos(a), math.sin(a)
    k = SEMI_X / SEMI_Y
    return radius / math.sqrt(c * c + (k * s) * (k * s))


def bead(centre):
    """Brass rolled bead: everything above the bead shoulder."""
    return centre.z > BEAD_Z


def inner(centre, normal):
    """Faces turned towards the axis: the ring's inside and the bead flank."""
    r = math.hypot(centre.x, centre.y)
    if r < 1e-9:
        return False
    return (normal.x * centre.x + normal.y * centre.y) / r < -0.3


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS, cap_start=False, cap_end=False,
               radius_fn=oval)
    b.add(bm, 'metal', METAL, 0.32, smooth=True)
    info = b.finish(TARGET_LONGEST_M)

    # The inward-facing slot needs the face normal, which the centre-only
    # predicate in prop_lib cannot see, so all three slots are reassigned here.
    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    zmin = min(v.co.z for v in me.vertices)
    for poly in me.polygons:
        c = poly.center
        if c.z - zmin > BEAD_Z:
            poly.material_index = 1
        elif inner(c, poly.normal):
            poly.material_index = 2
        else:
            poly.material_index = 0
    return info
