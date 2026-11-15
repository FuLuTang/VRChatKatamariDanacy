"""Paperback book - closed book lying face up, cover over a page block.

MessyRoom catalogue t2 / flat / 0.180 x 0.110 x 0.025 m; no catalog row, so the
module carries ``DOC_ROW``.

The pages are not a separate block: the solid is one ``stack`` of six rings, so
the page block is the middle 19.4 mm of the *same* closed shell and the covers
are the 2.2-2.8 mm lips above and below it.  That gives the real paperback
silhouette - covers slightly larger than the pages, a visible step all round -
without a second shell, and no buried faces at all.

The page block is inset on the fore-edge, the head and the tail but *not* on the
spine side, which is why the inset fades out towards -Y: a paperback's spine is
a solid cover, so the page band must not wrap around it.

Three material slots (2026-11-15 material pass):

* ``plastic_blue`` - the cover boards (top and bottom), which is what you see
  from above;
* ``paper`` - the page band, so the fore-edge, head and tail read as a cream
  cut;
* ``ink`` - the spine wall (one step darker than the blue cover, and it is the
  spine you read from the side) plus a raised 0.4 mm title plate on the cover.
  The plate is a 12-triangle prism welded in by Exact boolean union, so the
  "title" is a geometric block with no lettering.

12-point rounded-rect outline, 5 ring transitions: 120 + 24 cap triangles = 144
of the budget, plus the title plate.
"""

import math

import bmesh

from models._room_shapes import clamp, stack
from prop_lib import prism

ID = 'book_paperback'
NAME_ZH = '平装书'
CATEGORY = 'paper_stationery'
SOURCING = 'model ourselves：封面 + 书脊 + 书页切口，单壳阶梯实体'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.180
TRI_BUDGET = 240
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.180', 'game_longest_dim_m': '0.180'}
MATERIALS = [
    {'key': 'plastic_blue', 'name_zh': '封面蓝', 'linear_rgb': [0.075, 0.145,
                                                             0.400],
     'roughness': 0.48},
    {'key': 'paper', 'name_zh': '书页米白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '书脊/书名块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.72},
]
NOTES = ('Six-ring stack: bottom cover 0-2.2 mm, page block inset 1.6 mm from '
         '2.8 to 22.2 mm, top cover 22.8-25.0 mm.  Inset fades out on the '
         'spine side (-Y) so the spine stays solid.  plastic_blue = cover '
         'boards, paper = page band, ink = spine wall and the 110 x 11 x 0.4 mm '
         'raised title plate unioned onto the front cover.  0.180 x 0.110 x '
         '0.025 m.')

def rounded_rect(width, depth, radius, corner_segments=2):
    """Local copy of the shared outline helper (see prop_lib.rounded_rect)."""
    from prop_lib import rounded_rect as _rr
    return _rr(width, depth, radius, corner_segments)


COVER = rounded_rect(0.180, 0.110, 0.004, corner_segments=2)
COVER_Z = 0.0022
PAGE_Z0 = 0.0028
PAGE_Z1 = 0.0222
TOP_Z = 0.0250
PAGE_INSET = 0.0016
SPINE_Y = -0.055
SPINE_BAND = 0.0040
TITLE_X = 0.055
TITLE_Y = (-0.0400, -0.0290)
TITLE_Z0 = TOP_Z - 0.0001
TITLE_Z1 = TOP_Z + 0.0004


def page_outline():
    pts = []
    for (x, y) in COVER:
        # 0 on the spine edge, 1 once 10 mm inboard of it
        t = clamp((y - SPINE_Y) / 0.010)
        d = math.hypot(x, y) or 1.0
        k = PAGE_INSET * t
        pts.append((x - x / d * k, y - y / d * k))
    return pts


def is_page(centre):
    return PAGE_Z0 + 0.0002 < centre.z < PAGE_Z1 - 0.0002


def is_spine(centre):
    """The spine wall: the -Y band of the solid, through the whole height."""
    return centre.y < SPINE_Y + SPINE_BAND


def title_plate():
    poly = [(-TITLE_X, TITLE_Y[0]), (TITLE_X, TITLE_Y[0]),
            (TITLE_X, TITLE_Y[1]), (-TITLE_X, TITLE_Y[1])]
    bm = prism(poly, TITLE_Z1 - TITLE_Z0, start=TITLE_Z0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(b):
    page = page_outline()
    bm = stack([
        (0.0, COVER),
        (COVER_Z, COVER),
        (PAGE_Z0, page),
        (PAGE_Z1, page),
        (PAGE_Z1 + 0.0006, COVER),
        (TOP_Z, COVER),
    ])
    book = b.add(bm, 'plastic_blue', (0.075, 0.145, 0.400), 0.48, smooth=False,
                 name='book',
                 extras=[('paper', (0.800, 0.775, 0.700), 0.90, is_page),
                         ('ink', (0.030, 0.035, 0.045), 0.72, is_spine)])
    title = b.add(title_plate(), 'ink', (0.030, 0.035, 0.045), 0.72,
                  smooth=False, name='title')
    b.boolean_union(book, [title])
