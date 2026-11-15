"""Sticky note - one 76 mm square sheet, 4 mm thick, one corner curling up.

MessyRoom catalogue t0 / flat / 0.076 x 0.076 x 0.004 m.  ``prop_catalog.csv``
has no row for this id, so the module carries ``DOC_ROW``.

The note is a single closed plate, not a stack of sheets: a 3 x 3 height field
over a matching underside.  The curl is real geometry: the bottom-right corner
lifts by 3 mm with *both* its surfaces, so the sheet peels off the table there
and still lies flat on the rest of its area.

Three material slots (2026-11-15 material pass), all on that one closed shell:

* ``paper`` - the sheet itself;
* ``paper_dark`` - the underside where the curl has left the table, i.e. the
  raw back of the sheet you see when a corner lifts.  Assigned by face centre
  height, so it costs no triangles;
* ``ink`` - one thin handwritten stroke: a 1.5 mm wide, 0.5 mm proud plate
  welded into the sheet by an Exact boolean union.  No lettering, so no brand
  and no font: the stroke is a single closed prism.

The 3 x 3 grid is the whole triangle budget: 18 top + 18 underside + 24 rim =
60 triangles, plus the 12 of the stroke and the union seam.
"""

import math

import bmesh

from models._room_shapes import clamp, grid_slab
from prop_lib import prism

ID = 'sticky_note'
NAME_ZH = '便利贴'
CATEGORY = 'paper_stationery'
SOURCING = 'model ourselves：76 mm 便利贴，一片略卷角薄板 + 一条手写线'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.076
TRI_BUDGET = 96
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.076', 'game_longest_dim_m': '0.076'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '便签纸', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'paper_dark', 'name_zh': '卷角背面', 'linear_rgb': [0.360, 0.320,
                                                               0.250],
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '手写线', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('3 x 3 grid over a matching underside, 0.076 x 0.076 m, 4.0 mm thick, '
         'one 3 mm curl at the +X-Y corner (both surfaces lift, so the sheet is '
         'still flat everywhere else).  Three slots: paper sheet, paper_dark '
         'curl underside (face-centre height, no extra geometry) and an ink '
         'handwriting stroke - a 1.5 mm wide prism raised 0.5 mm and welded in '
         'by Exact boolean union.  No lettering.')
HALF = 0.038
THICK = 0.0040
CURL = 0.0030
# handwritten stroke: one straight bar, A -> B, 1.5 mm wide, 0.5 mm proud
STROKE_A = (-0.021, 0.0070)
STROKE_B = (0.013, -0.0035)
STROKE_W = 0.00075
STROKE_Z0 = 0.00350
STROKE_Z1 = 0.00480


def curl(x, y):
    u = clamp((x / HALF + 1.0) * 0.5)
    v = clamp((1.0 - y / HALF) * 0.5)
    return CURL * (u ** 2.2) * (v ** 2.2)


def top(x, y):
    return THICK + curl(x, y) + 0.0003 * math.sin(5.0 * x / HALF + 0.7)


def is_back(centre):
    """Everything but the printed face: the sheet's underside and cut edge.

    The lifted corner of the curl is part of that underside, so the corner you
    see peeling off the table is paper_dark without any extra geometry.
    """
    return centre.z < 0.0035


def stroke():
    ax, ay = STROKE_A
    bx, by = STROKE_B
    dx, dy = bx - ax, by - ay
    length = math.hypot(dx, dy)
    nx, ny = -dy / length * STROKE_W, dx / length * STROKE_W
    poly = [(ax + nx, ay + ny), (bx + nx, by + ny),
            (bx - nx, by - ny), (ax - nx, ay - ny)]
    bm = prism(poly, STROKE_Z1 - STROKE_Z0, start=STROKE_Z0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(b):
    bm = grid_slab(3, 3, -HALF, HALF, -HALF, HALF, top,
                   bottom_fn=lambda x, y: curl(x, y))
    note = b.add(bm, 'paper', (0.800, 0.775, 0.700), 0.90, smooth=False,
                 name='note',
                 extras=[('paper_dark', (0.360, 0.320, 0.250), 0.92,
                          is_back)])
    ink = b.add(stroke(), 'ink', (0.030, 0.035, 0.045), 0.75, smooth=False,
                name='stroke')
    b.boolean_union(note, [ink])
