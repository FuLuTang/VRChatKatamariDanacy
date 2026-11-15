"""Tissue box - 0.24 x 0.12 x 0.08 m carton: grey board, dark pull opening, white sheet.

MessyRoom catalogue row tissue_box: 0.240 x 0.120 x 0.080 m, blocky,
hygiene_bathroom.  The carton is two rounded-rect prisms (R 8 mm, one point per
corner - a carton has sharp, slightly broken edges): the 62 mm body, and a lid
inset 1 mm in plan from z=0.0615 to 0.0800.  The 1 mm step is the lid seam, and
the exposed ring of the body top takes the ink slot, so the carton reads as a
lidded box instead of a single extrusion - spec 3's "parting line + lid 1 mm
smaller" for blocky packaging.

The opening is a blind elliptical dish, not a hole: a 12-sided lathe tool with a
rounded bottom, squashed to 33% on Y to become a 90 x 30 mm oval, is subtracted
from the combined carton with one Exact boolean, cutting 14 mm down from the top
face.  A through-slot would put the carton's interior on the outside.  The whole
dish - wall and floor - is the ink slot, so the opening is a dark mouth under the
white sheet.

The paper pull is a 26 mm wide, 3 mm thick sheet leaning 30 degrees on X, whose
lower 12 mm is buried in the carton below the dish floor and welded by a second
union, so the sheet is part of the same closed shell rather than a floating flap.
The lean is what makes it visible: a sheet standing straight up would be seen
edge-on by the 3/4 preview camera.  Its top reaches z = 0.0904, i.e. 10 mm above
the carton - the one place where this prop exceeds its catalogue height, and the
cue that says "tissues" instead of "box".  The lean is deliberately limited so
the sheet is still inside the 90 x 30 mm dish footprint where it crosses the top
face: past that it would cut the carton top outside the dish instead of rising
out of it.

Three material slots: paper_dark for the carton, ink for the lid seam and the
dish, paper for the sheet.
"""

from prop_lib import lathe, prism, rounded_rect

ID = 'tissue_box'
NAME_ZH = '纸巾盒'
CATEGORY = 'hygiene_bathroom'
SOURCING = 'model ourselves：240 x 120 x 80 mm 纸盒 + 顶面椭圆凹槽 + 抽出的纸巾'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.240
# 220 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t3 cap 700.
TRI_BUDGET = 352

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'paper_dark': ((0.360, 0.320, 0.250), 0.92),
    'paper': ((0.800, 0.775, 0.700), 0.90),
    'ink': ((0.030, 0.035, 0.045), 0.75),
}
MATERIALS = [
    {'key': 'paper_dark', 'name_zh': '纸板灰盒体',
     'linear_rgb': list(PALETTE['paper_dark'][0]),
     'roughness': PALETTE['paper_dark'][1]},
    {'key': 'ink', 'name_zh': '深色抽出口与开盖缝',
     'linear_rgb': list(PALETTE['ink'][0]), 'roughness': PALETTE['ink'][1]},
    {'key': 'paper', 'name_zh': '纸巾米白',
     'linear_rgb': list(PALETTE['paper'][0]),
     'roughness': PALETTE['paper'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.240', 'game_longest_dim_m': '0.240'}
NOTES = ('Body 0.240 x 0.120 x 0.0620 (R 8 mm) from z=0; lid inset 1 mm in plan '
         'from z=0.0615 to 0.0800, welded 0.5 mm into the body.  Dish: 12-sided '
         'lathe tool, O 0.090 squashed to 33% on Y (90 x 30 mm), rounded bottom, '
         'subtracted so the dish floor sits at z=0.066 (14 mm deep, blind).  Pull: '
         '26 mm wide, 3 mm thick, 42 mm long sheet leaning 30 degrees on X, base '
         'buried at z=0.054 below the dish floor, unioned; top reaches z=0.0904, '
         'so the AABB is 0.2400 x 0.1200 x 0.0904.  Three slots: paper for the '
         'pull, ink for the 1 mm lid seam ring (z 0.0612-0.0628) and the dish '
         '(0.0645 < z < 0.0792 inside the oval), paper_dark for the carton.  One '
         'closed shell.')

BOX_W, BOX_D, BOX_H = 0.240, 0.120, 0.080
BOX_R = 0.008
BODY_H = 0.0620
LID_Z0 = 0.0615
LID_INSET = 0.0010
DISH_R, DISH_Y_SCALE = 0.0450, 0.33
DISH_FLOOR_Z, DISH_DEPTH = 0.0660, 0.0350
DISH_PROFILE = [
    (0.0000, 0.0000),
    (0.0360, 0.0030),
    (DISH_R, 0.0110),
    (DISH_R, DISH_DEPTH),
]
FLAP_HALF_W, FLAP_T = 0.0130, 0.0030
FLAP_Z0, FLAP_Z1 = 0.0540, 0.0960
FLAP_TILT = 0.5235987755982988   # 30 degrees on X
COS_TILT = 0.8660254037844387
SIN_TILT = 0.49999999999999994
SEAM_Z0, SEAM_Z1 = 0.0612, 0.0628
DISH_Z0, DISH_Z1 = 0.0645, 0.0792


def _rect(w, d, inset=0.0):
    return rounded_rect(w - 2.0 * inset, d - 2.0 * inset, BOX_R - inset,
                        corner_segments=1)



def _is_pull(c):
    """True for the six faces of the leaning sheet, found by undoing its tilt.

    A height test cannot be used: since the dish is a blind pocket in the top
    face, the lid's own top-face triangles near the oval also sit above z=0.070
    and would take the paper slot.  Mapping the face centre back through the
    30-degree X rotation identifies the sheet exactly.
    """
    dx, dz = c.x, c.z - FLAP_Z0
    lx = dx * COS_TILT - dz * SIN_TILT
    lz = dx * SIN_TILT + dz * COS_TILT
    return (abs(lx) <= FLAP_HALF_W + 0.0004
            and -0.0004 <= lz <= (FLAP_Z1 - FLAP_Z0) + 0.0004
            and abs(c.y) <= FLAP_T * 0.5 + 0.0004)


def build(b):
    board, board_rough = PALETTE['paper_dark']
    box = b.add(prism(_rect(BOX_W, BOX_D), BODY_H, start=0.0),
                'paper_dark', board, board_rough, smooth=False, name='body')
    lid = b.add(prism(_rect(BOX_W, BOX_D, LID_INSET), BOX_H - LID_Z0,
                      start=LID_Z0),
                'paper_dark', board, board_rough, smooth=False, name='lid')
    b.boolean_union(box, [lid])

    cutter = b.add(lathe(DISH_PROFILE, segments=12), 'paper_dark', board,
                  board_rough, smooth=False, name='dishcut')
    for v in cutter.data.vertices:
        v.co.y *= DISH_Y_SCALE
        v.co.z += DISH_FLOOR_Z
    b.boolean_difference(box, [cutter])

    flap = b.add(prism([(-FLAP_HALF_W, FLAP_Z0), (FLAP_HALF_W, FLAP_Z0),
                        (FLAP_HALF_W, FLAP_Z1), (-FLAP_HALF_W, FLAP_Z1)],
                       FLAP_T, axis='x', start=-FLAP_T * 0.5),
                 'paper', PALETTE['paper'][0], PALETTE['paper'][1],
                 smooth=False, name='pull')
    for v in flap.data.vertices:
        dz = v.co.z - FLAP_Z0
        x = v.co.x
        v.co.x = x * COS_TILT + dz * SIN_TILT
        v.co.z = FLAP_Z0 + dz * COS_TILT - x * SIN_TILT
    b.boolean_union(box, [flap])

    me = box.data
    me.materials.clear()
    for key in ('paper_dark', 'ink', 'paper'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        c = poly.center
        pulled = _is_pull(c)
        if pulled:
            poly.material_index = 2
        elif SEAM_Z0 < c.z < SEAM_Z1:
            poly.material_index = 1
        elif (DISH_Z0 < c.z < DISH_Z1 and abs(c.x) < DISH_R + 0.0010
              and abs(c.y) < DISH_R * DISH_Y_SCALE + 0.0016):
            poly.material_index = 1
        else:
            poly.material_index = 0
