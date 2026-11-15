"""Popsicle stick - 11 x 1 x 0.2 cm rounded flat stick with a stamped panel.

Contract: 0.110 x 0.010 x 0.002 m (real size), wood, <= 120 triangles.

A rounded-rectangle outline (16-gon, corner radius 4.8 mm - just under the 5 mm
half-width, so both ends are near-semicircular like a real ice-lolly stick)
extruded 2 mm in Z with `prism`.  The caps are n-gons that finish() triangulates,
and the slab is closed by construction, so there is no boolean seam on the
silhouette.

Lying flat on z = 0 .. 0.002 m as the row asks; the 0.110 m length runs along X.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): the single pale
slot (luma 0.56) becomes three palette tones plus a real stamped panel.
  * `straw`      - the birch stick (linear 0.470 / 0.360 / 0.140, rough 0.90)
  * `wood_dark`  - the stamped panel's side walls (0.135 / 0.078 / 0.038), which
                   give the recess a shadowed edge
  * `ink`        - the 0.8 mm deep panel floor, i.e. the printed/stamped brand
                   block (0.030 / 0.035 / 0.045)
A 0.030 x 0.0055 m rounded tool is subtracted from the top face; the recess is
inside the outline, so the AABB is unchanged.
"""

from prop_lib import prism, rounded_rect

ID = 'popsicle_stick'
NAME_ZH = '冰棍棒'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：0.110 m 圆角扁木棒，中段压印凹块'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.110
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.110', 'game_longest_dim_m': '0.110'}
MATERIALS = [
    {'key': 'straw', 'name_zh': '冰棍木', 'linear_rgb': [0.470, 0.360, 0.140],
     'roughness': 0.90},
    {'key': 'wood_dark', 'name_zh': '压印侧壁深木',
     'linear_rgb': [0.135, 0.078, 0.038], 'roughness': 0.75},
    {'key': 'ink', 'name_zh': '压印底墨', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('rounded_rect(0.110, 0.010, r=0.0048, 3 segments per corner) = 16-point '
         'outline, extruded 0.002 m in Z with prism.  Detail: a 0.030 x 0.0055 m '
         'rounded tool is subtracted 0.8 mm into the top face; the recess floor '
         'is ink, its walls wood_dark, the stick straw.  AABB 0.110 x 0.010 x '
         '0.002 m, underside on z = 0.')

LENGTH = 0.110
WIDTH = 0.010
THICK = 0.002
CORNER_R = 0.0048
CORNER_SEGMENTS = 3
PANEL_W, PANEL_D, PANEL_R = 0.030, 0.0055, 0.0015
PANEL_FLOOR = 0.0012        # 0.8 mm below the top face
STRAW_RGB = (0.470, 0.360, 0.140)
WOOD_DARK_RGB = (0.135, 0.078, 0.038)
INK_RGB = (0.030, 0.035, 0.045)


def _slot(centre):
    """Ink on the recess floor, wood_dark on its walls, straw elsewhere.

    ``c.z > 0.0005`` keeps the stick's single underside n-gon - whose centre is
    inside the panel rectangle - out of the panel slots.
    """
    c = centre
    if (c.z > 0.0005 and abs(c.x) < PANEL_W * 0.5 + 1e-4
            and abs(c.y) < PANEL_D * 0.5 + 1e-4):
        return 2 if c.z < PANEL_FLOOR + 5e-5 else 1
    return 0


def build(b):
    outline = rounded_rect(LENGTH, WIDTH, CORNER_R,
                           corner_segments=CORNER_SEGMENTS)
    stick = b.add(prism(outline, THICK, axis='z', start=0.0),
                  'straw', STRAW_RGB, 0.90, smooth=False, name='stick')
    panel = b.add(prism(rounded_rect(PANEL_W, PANEL_D, PANEL_R, 1),
                        THICK - PANEL_FLOOR, axis='z', start=PANEL_FLOOR),
                  'ink', INK_RGB, 0.75, smooth=False, name='panel_cut')
    b.boolean_difference(stick, [panel])

    me = stick.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
