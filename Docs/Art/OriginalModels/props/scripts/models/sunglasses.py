"""Sunglasses - folded pair lying lenses-up with both temples stacked on top.

MessyRoom catalogue t2 / flat / 0.150 x 0.050 x 0.040 m; no catalog row, so the
module carries ``DOC_ROW``.

The lenses are not separate shells: the front is one height-field slab whose two
lens zones are recessed 4.5 mm below the surrounding rim, and those recessed
faces - and only those - carry the ``glass`` material.  Frame, bridge and both
lenses are therefore one object with two materials, which is how a low-poly pair
of glasses should be built.

The temples are folded: each one is a tapered bar that has rotated 180 degrees at
its hinge and lies across the back of the front, the second one on top of the
first.  They are welded in with Exact boolean unions, so the prop is a single
closed shell, and the stack of bars is what gives the folded pair its 40 mm
height - the catalogue's third axis - while the front itself stays flat on the
surface.

Three material slots (2026-11-15 material pass):

* ``plastic_grey`` - frame and both folded temples;
* ``ink`` - the two recessed lens zones, a very dark cool grey-blue (linear
  0.030/0.035/0.045, roughness 0.24 for the glass sheen).  It is darker and
  cooler than the frame, so the lens recess reads as glass rather than as a hole;
* ``metal`` - the hinge end of each temple bar: each bar is built as two
  prisms that overlap by 6 mm at the fold, a 24 mm metal hinge segment and the
  grey arm.  Face-centre material assignment could not do this: a prism has one
  face per side, so its centre sits at mid-length and only the 9 mm end cap came
  back metal, which the front then swallowed.
"""

import bmesh

from models._room_shapes import grid_slab
from prop_lib import prism

ID = 'sunglasses'
NAME_ZH = '太阳镜'
CATEGORY = 'accessories'
SOURCING = 'model ourselves：镜框 + 内凹镜片区 + 折起镜腿，单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.150
TRI_BUDGET = 300
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.150', 'game_longest_dim_m': '0.150'}
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '镜框灰塑料', 'linear_rgb': [0.230, 0.235,
                                                                0.240],
     'roughness': 0.48},
    {'key': 'ink', 'name_zh': '镜片墨色', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.24},
    {'key': 'metal', 'name_zh': '铰链金属', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
]
NOTES = ('Front: one 0.150 x 0.050 x 0.014 m slab on an 8 x 4 grid, the two lens '
         'zones recessed to 0.0095 m and painted ink (same shell, no separate '
         'lens).  Two folded temple bars (tapered 9 mm wide, 14 mm and 16 mm '
         'thick) stack on the back of the front and are boolean-unioned in: '
         'total 0.150 x 0.050 x 0.040 m.  The outer 24 mm of each bar is metal '
         '(hinge segment, a separate prism overlapping the arm by 6 mm); frame and '
         'arms are the same mid grey plastic.')

HALF_X = 0.075
HALF_Y = 0.025
FRAME_TOP = 0.0140
LENS_TOP = 0.0095
LENS_CX = 0.038
LENS_HW = 0.026
LENS_HH = 0.019


def top(x, y):
    if abs(y) < LENS_HH:
        for cx in (-LENS_CX, LENS_CX):
            if abs(x - cx) < LENS_HW:
                return LENS_TOP
    return FRAME_TOP


def is_lens(centre):
    return 0.0085 < centre.z < 0.0105


def temple(quad, z0, thick):
    bm = prism(quad, thick, start=z0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def split_temple(quad, x_split, overlap):
    """Two tapered quads that share a 6 mm band around ``x_split``.

    ``quad`` is the bar's plan outline in order; the split points are
    interpolated on its two long edges, so the hinge segment and the arm keep the
    bar's taper instead of becoming rectangles.
    """
    (ax, ay), (bx, by), (cx, cy), (dx, dy) = quad

    def edge_y(x0, y0, x1, y1, x):
        t = (x - x0) / (x1 - x0)
        return y0 + (y1 - y0) * t

    y_head = edge_y(ax, ay, bx, by, x_split)
    y_tail = edge_y(dx, dy, cx, cy, x_split)
    tip = [(ax, ay), (x_split + overlap, edge_y(ax, ay, bx, by,
                                                x_split + overlap)),
           (x_split + overlap, edge_y(dx, dy, cx, cy, x_split + overlap)),
           (dx, dy)]
    arm = [(x_split - overlap, edge_y(ax, ay, bx, by, x_split - overlap)),
           (bx, by), (cx, cy),
           (x_split - overlap, edge_y(dx, dy, cx, cy, x_split - overlap))]
    del y_head, y_tail
    return tip, arm


def build(b):
    front = grid_slab(8, 4, -HALF_X, HALF_X, -HALF_Y, HALF_Y, top, bottom_z=0.0)
    front_ob = b.add(front, 'plastic_grey', (0.230, 0.235, 0.240), 0.48,
                     smooth=False, name='front',
                     extras=[('ink', (0.030, 0.035, 0.045), 0.24, is_lens)])

    # Folded temples: tapered bars lying across the back of the front, the
    # second one on top of the first.  Each bar starts 8 mm below the front's top
    # surface: with only a 2 mm bite the Exact solver left 9 non-manifold edges
    # where the bar's underside met the slab's grid lines, and sinking them to
    # mid-thickness makes each cut a clean single loop.  They are unioned one at
    # a time (welding both into one tool made the solver drop the overlap and
    # return two shells).
    bar_a = [(-0.072, -0.0245), (0.045, -0.0175), (0.045, -0.0085),
             (-0.072, -0.0155)]
    bar_b = [(0.072, -0.0245), (-0.040, -0.0195), (-0.040, -0.0105),
             (0.072, -0.0155)]
    tip_a, arm_a = split_temple(bar_a, -0.046, 0.006)
    tip_b, arm_b = split_temple(bar_b, 0.036, 0.006)

    grey = (0.230, 0.235, 0.240)
    steel = (0.430, 0.450, 0.470)
    tools = [
        b.add(temple(tip_a, 0.006, 0.020), 'metal', steel, 0.32,
              smooth=False, name='hinge_a'),
        b.add(temple(arm_a, 0.006, 0.020), 'plastic_grey', grey, 0.48,
              smooth=False, name='temple_a'),
        b.add(temple(tip_b, 0.018, 0.022), 'metal', steel, 0.32,
              smooth=False, name='hinge_b'),
        b.add(temple(arm_b, 0.018, 0.022), 'plastic_grey', grey, 0.48,
              smooth=False, name='temple_b'),
    ]

    b.boolean_union(front_ob, tools)
