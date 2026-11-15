"""Hairbrush - 0.22 m paddle brush: grey plate, white nylon tufts, red handle.

MessyRoom catalogue row hairbrush: 0.220 x 0.060 x 0.040 m, long, hygiene_bathroom.
The length runs on X: the head plate is the 0.078 m front half (x 0 to 0.078) and
the handle the 0.146 m rear half (x -0.142 to 0.004), overlapping the plate by
2 mm so one Exact union welds them.

The bristle field is 24 real columns: the head's top face is a 6 x 4 cell grid and
each cell carries a plateau 6 mm above the plate, so the columns stand up with
4 mm channels between them and a side view shows six separate posts.  The columns
are built into the plate's own mesh - not welded on with 24 booleans - which is
what keeps the prop inside the triangle budget: each cell costs four skirt quads
plus its top quad, 10 triangles, so 24 columns cost 240 against 576 for 24
standalone 6-sided pins plus a pierced top face.

Column pitch follows the head (13 x 11 mm, giving 9 x 7 mm columns) rather than
the brief's O 6 mm at 8 mm pitch: a 6 x 4 field of O 6 mm columns with 2 mm gaps
only spans 48 x 32 mm, which would leave 60% of this 78 x 44 mm head as bare
plastic and read as a sticker patch.  The count (24), the height (6 mm), the flat
column tops and the visible channels are what the review asked for.

Four material slots, split where the geometry already splits:

  plastic_grey  the head plate: rim, side wall and underside
  rubber        the sloped skirt of every column - the dark socket the tufts are
                set into, which is the "dark base" the brief asked for and what
                keeps the white tips from floating on a pale plate
  nylon         the 24 flat column tops, bright white (fabric_white's value worn
                at plastic roughness, since spec 2 has no nylon key)
  plastic_red   the handle, all of it, so the brush is one bright colour against
                the grey head

Materials by face height: the column tops are nylon, the skirts below them
rubber, everything under z=0.0205 is plate or handle.  Flat shading keeps the
column rings crisp.
"""

import bmesh

from prop_lib import prism

ID = 'hairbrush'
NAME_ZH = '发刷'
CATEGORY = 'hygiene_bathroom'
SOURCING = 'model ourselves：220 x 60 x 40 mm 板式发刷，24 个并入刷头板的刷毛凸柱'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.220
# 220 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t3 cap 700.
TRI_BUDGET = 352

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim; nylon has no spec key, so it wears
# fabric_white's value at the plastic roughness band (0.40-0.55).
PALETTE = {
    'plastic_grey': ((0.230, 0.235, 0.240), 0.50),
    'rubber': ((0.045, 0.045, 0.048), 0.88),
    'nylon': ((0.720, 0.700, 0.660), 0.50),
    'plastic_red': ((0.480, 0.075, 0.060), 0.48),
}
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '灰塑料刷头板',
     'linear_rgb': list(PALETTE['plastic_grey'][0]),
     'roughness': PALETTE['plastic_grey'][1]},
    {'key': 'nylon', 'name_zh': '亮白尼龙刷毛',
     'linear_rgb': list(PALETTE['nylon'][0]),
     'roughness': PALETTE['nylon'][1]},
    {'key': 'rubber', 'name_zh': '深色刷毛底座',
     'linear_rgb': list(PALETTE['rubber'][0]),
     'roughness': PALETTE['rubber'][1]},
    {'key': 'plastic_red', 'name_zh': '红塑料手柄',
     'linear_rgb': list(PALETTE['plastic_red'][0]),
     'roughness': PALETTE['plastic_red'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.220', 'game_longest_dim_m': '0.220'}
NOTES = ('Head plate 0.078 x 0.044 m, plate top z=0.020, rim down to z=0, one '
         'n-gon underside.  Bristle field: 6 x 4 cells (13 x 11 mm pitch), each '
         'with a plateau 2 mm inside the cell edges and 6 mm above the plate, so '
         'the columns are 9 x 7 x 6 mm with 4 mm channels: 24 columns, 10 '
         'triangles each, 240.  Head total 240 + 40 rim + 18 underside = 298.  '
         'Handle: 4-sided prism 0.030 x 0.0195 m on z 0-0.0195, x -0.142 to '
         '0.004, 12 triangles.  324 triangles measured after the union.  AABB '
         '0.220 x 0.044 x 0.026.  Four slots by face height/position: column tops '
         '(z > 0.0257) nylon, column skirts (z > 0.0205) rubber, handle '
         '(x < -0.002 below the plate top) plastic_red, the rest plastic_grey.  '
         'One closed shell.')

HEAD_X0, HEAD_X1 = 0.000, 0.078
HEAD_Y = 0.022
CELLS_X, CELLS_Y = 6, 4
PLATE_TOP = 0.020
POST_TOP = PLATE_TOP + 0.006
COLUMN_GAP = 0.002
HANDLE_X0, HANDLE_L = -0.142, 0.146
HANDLE_W, HANDLE_H = 0.030, 0.0195
HANDLE_SPLIT_X = -0.002


def _bristled_plate():
    """Closed plate whose top face is a 6 x 4 grid of flat-topped columns."""
    bm = bmesh.new()
    grid = []
    for i in range(CELLS_X + 1):
        x = HEAD_X0 + (HEAD_X1 - HEAD_X0) * i / CELLS_X
        row = []
        for j in range(CELLS_Y + 1):
            y = -HEAD_Y + 2.0 * HEAD_Y * j / CELLS_Y
            row.append(bm.verts.new((x, y, PLATE_TOP)))
        grid.append(row)

    for i in range(CELLS_X):
        for j in range(CELLS_Y):
            cx0, cx1 = grid[i][j].co.x, grid[i + 1][j + 1].co.x
            cy0, cy1 = grid[i][j].co.y, grid[i + 1][j + 1].co.y
            inner = [(cx0 + COLUMN_GAP, cy0 + COLUMN_GAP),
                     (cx1 - COLUMN_GAP, cy0 + COLUMN_GAP),
                     (cx1 - COLUMN_GAP, cy1 - COLUMN_GAP),
                     (cx0 + COLUMN_GAP, cy1 - COLUMN_GAP)]
            top = [bm.verts.new((x, y, POST_TOP)) for (x, y) in inner]
            corners = (grid[i][j], grid[i + 1][j], grid[i + 1][j + 1],
                       grid[i][j + 1])
            for k in range(4):
                bm.faces.new((corners[k], corners[(k + 1) % 4],
                              top[(k + 1) % 4], top[k]))
            bm.faces.new(top)

    # Boundary ring in walk order: +x edge, +y edge, -x edge, -y edge.
    ring = []
    for i in range(CELLS_X):
        ring.append(grid[i][0])
    for j in range(CELLS_Y):
        ring.append(grid[CELLS_X][j])
    for i in range(CELLS_X, 0, -1):
        ring.append(grid[i][CELLS_Y])
    for j in range(CELLS_Y, 0, -1):
        ring.append(grid[0][j])

    floor = [bm.verts.new((v.co.x, v.co.y, 0.0)) for v in ring]
    for k in range(len(ring)):
        m = (k + 1) % len(ring)
        bm.faces.new((ring[k], ring[m], floor[m], floor[k]))
    bm.faces.new(list(reversed(floor)))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(b):
    grey, grey_rough = PALETTE['plastic_grey']
    head = b.add(_bristled_plate(), 'plastic_grey', grey, grey_rough,
                 smooth=False, name='head')
    red, red_rough = PALETTE['plastic_red']
    handle = b.add(prism([(-HANDLE_W * 0.5, 0.0), (HANDLE_W * 0.5, 0.0),
                          (HANDLE_W * 0.5, HANDLE_H), (-HANDLE_W * 0.5, HANDLE_H)],
                         HANDLE_L, axis='x', start=HANDLE_X0),
                   'plastic_red', red, red_rough, smooth=False, name='handle')
    b.boolean_union(head, [handle])

    me = head.data
    me.materials.clear()
    for key in ('plastic_grey', 'nylon', 'rubber', 'plastic_red'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        c = poly.center
        if c.z > PLATE_TOP + 0.0005:
            poly.material_index = 1 if c.z > POST_TOP - 0.0003 else 2
        elif c.x < HANDLE_SPLIT_X:
            poly.material_index = 3
        else:
            poly.material_index = 0
