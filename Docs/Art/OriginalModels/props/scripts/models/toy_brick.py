"""Toy brick - 32 x 16 x 10 mm plastic brick, bright topplate over a dark skirt.

MessyRoom catalogue row toy_brick: 0.032 x 0.016 x 0.010 m, blocky, toys_games.
A 15 mm-stud-pitch toddler brick: four O 6 mm studs sit on the body at
(+/-0.0075, +/-0.0040), the 2x2 Duplo lattice for this footprint.

Two material slots, and the split is geometry, not shading: the top 1.4 mm of the
brick is a full-footprint plate in bright red plastic, and the 6.2 mm skirt below
it is inset 0.8 mm and moulded in a much darker red of the same hue.  The
reference brief asked for "a top face one step brighter than the sides", and a
single slot cannot deliver that (one base colour, one roughness for the whole
object), so the step is a real ledge: seen from the 3/4 preview camera the eye
gets the bright plate, a 0.8 mm shadow gap and then the dark skirt.  Both reds
are the spec palette's plastic_red and a red pressed to 24% of it - same hue,
4.2x darker, so the moulding still reads as one red brick instead of two toys.

The studs are separate lathes unioned into the plate with one Exact boolean: a
stud that only touched the top face (or was added without a union) would export
as a second shell, and the shell count is part of the deliverable contract.  Each
stud is buried 1.0 mm into the 1.4 mm plate, so the union leaves no cap inside
the skirt and no seam ring.

Six-sided studs (the brief allows 6-8) keep the cost at 24 triangles each: four
studs is 96 triangles, plate and skirt 12 each, and the boolean trims the buried
caps and re-triangulates the pierced faces.  Flat shading everywhere: a 6-gon
stud under smooth shading would read as a cone.
"""

from prop_lib import lathe, prism

ID = 'toy_brick'
NAME_ZH = '玩具积木'
CATEGORY = 'toys_games'
SOURCING = 'model ourselves：32 x 16 x 10 mm 塑料积木，顶面 4 个 Ø6 凸点'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.032
# 160 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (material/detail rework
# allowance, MATERIAL_AND_DETAIL_SPEC.md 4); the t0 hard cap is 300.
TRI_BUDGET = 256

# Shared linear-RGB palette, copied verbatim from
# MATERIAL_AND_DETAIL_SPEC.md 2 so this module cannot drift from the batch.
PALETTE = {
    'plastic_red': ((0.480, 0.075, 0.060), 0.48),
    # Same hue as plastic_red, pressed to 24%: the brief's "plastic_red_dark"
    # (spec 2 has no such key; section 9 of the batch brief names it).
    'plastic_red_dark': ((0.115, 0.018, 0.014), 0.48),
}
MATERIALS = [
    {'key': 'plastic_red', 'name_zh': '亮红塑料（顶板与凸点）',
     'linear_rgb': list(PALETTE['plastic_red'][0]),
     'roughness': PALETTE['plastic_red'][1]},
    {'key': 'plastic_red_dark', 'name_zh': '深红塑料（侧裙与底面）',
     'linear_rgb': list(PALETTE['plastic_red_dark'][0]),
     'roughness': PALETTE['plastic_red_dark'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.032', 'game_longest_dim_m': '0.032'}
NOTES = ('0.0320 x 0.0160 x 0.0100 brick: a full-footprint 1.4 mm bright topplate '
         '(z 0.0062-0.0076) over a 0.8 mm-inset skirt (0.0304 x 0.0144, z 0-0.0068, '
         'welded 0.6 mm into the plate), plus four 6-sided O 6 mm studs at '
         '(+/-0.0075, +/-0.0040) buried 1.0 mm into the plate and rising to '
         'z=0.0100.  Stud pitch 15 mm across the length and 8 mm across the width.  '
         'Two material slots assigned by face height (c.z > 0.0058 -> bright '
         'plate/studs, else dark skirt), so the moulding step is visible from the '
         'preview camera.  One closed shell, no bevels, flat shading.')

BODY_W, BODY_D = 0.032, 0.016
BODY_H = 0.0076
PLATE_H = 0.0014
PLATE_Z0 = BODY_H - PLATE_H
SKIRT_INSET = 0.0008
SKIRT_TOP = PLATE_Z0 + 0.0006      # 0.6 mm welded into the plate
STUD_R = 0.0030
STUD_TOP = 0.0100
STUD_SEGS = 6
STUD_BASE_Z = BODY_H - 0.0010      # buried 1.0 mm into the 1.4 mm plate
STUD_POS = [(sx * 0.0075, sy * 0.0040) for sx in (-1.0, 1.0)
            for sy in (-1.0, 1.0)]
# Faces above this height are the bright plate; below it the dark skirt.
SPLIT_Z = 0.0058


def _rect(w, d):
    return [(-w * 0.5, -d * 0.5), (w * 0.5, -d * 0.5),
            (w * 0.5, d * 0.5), (-w * 0.5, d * 0.5)]


def build(b):
    plate_rgb, plate_rough = PALETTE['plastic_red']
    skirt_rgb, skirt_rough = PALETTE['plastic_red_dark']

    plate = b.add(prism(_rect(BODY_W, BODY_D), PLATE_H, axis='z',
                        start=PLATE_Z0),
                  'plastic_red', plate_rgb, plate_rough,
                  smooth=False, name='plate')
    skirt = b.add(prism(_rect(BODY_W - 2.0 * SKIRT_INSET,
                              BODY_D - 2.0 * SKIRT_INSET),
                        SKIRT_TOP, axis='z', start=0.0),
                  'plastic_red_dark', skirt_rgb, skirt_rough,
                  smooth=False, name='skirt')
    studs = []
    for i, (cx, cy) in enumerate(STUD_POS):
        bm = lathe([(0.0, 0.0), (STUD_R, 0.0), (STUD_R, STUD_TOP - STUD_BASE_Z)],
                   segments=STUD_SEGS)
        for v in bm.verts:
            v.co.x += cx
            v.co.y += cy
            v.co.z += STUD_BASE_Z
        studs.append(b.add(bm, 'plastic_red', plate_rgb, plate_rough,
                           smooth=False, name='stud%d' % i))
    b.boolean_union(plate, [skirt] + studs)

    me = plate.data
    me.materials.clear()
    me.materials.append(b.material('plastic_red', plate_rgb, plate_rough))
    me.materials.append(b.material('plastic_red_dark', skirt_rgb, skirt_rough))
    for poly in me.polygons:
        poly.material_index = 0 if poly.center.z > SPLIT_Z else 1
