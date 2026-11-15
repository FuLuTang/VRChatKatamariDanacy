"""Game controller - 0.15 x 0.10 x 0.06 m pad: grey shell, coloured buttons, sticks.

MessyRoom catalogue row game_controller: 0.150 x 0.100 x 0.060 m, blocky,
electronics_cables.  X is the axis across the grips (0.150), Y the front-back
depth (0.098), Z the height (0.0609); the pad faces +Y.

This version is one lofted slab instead of three welded prisms.  A 24-point plan
outline gives the real controller silhouette - a waist 66 mm deep at the middle,
two 96 mm lobes at the grips, rounded tips at x = +/-0.075 - and the top surface
is a shallow dome, 0.060 m over the middle dropping to 0.038 m over the grips,
so the grips fall away from the crown the way a real pad's do.  Three concentric
outlines plus a pole carry the dome, the outer outline drops to z=0 through a
24-quad rim and one n-gon underside closes it, so there is no boolean in the body.

Everything else is a unioned part, and every part is a slot (six slots, spec 1.1
asks >= 3 for a hero-ish electronic):

  plastic_grey   the shell
  plastic_red    the +X/+Y face button      (A)
  plastic_blue   the +X/-Y face button      (B)
  plastic_green  the -X/-Y face button
  plastic_yellow the -X/+Y face button
  rubber         both thumbstick caps and the recessed dark screen pocket

The four buttons are O 16 mm, 6-sided, each seated on the local dome height (base
2 mm below it) and standing 5.5 mm proud - the brief's 1 mm minimum plus enough
height that the coloured top disc is 30 px of the 512 px tile.  The two thumbstick
caps sit at (+/-0.048, -0.010) on the grip slope: an 9.2 mm collar with a domed
top, base sunk 6.5 mm so the union is welded even where the dome falls away under
the cap, top 5.5 mm proud.  The screen is a 28 x 14 mm pocket pressed 2.5-4 mm into the
crown between the two button rows, its floor and walls on the rubber slot - the
brief's "dark screen strip", and the only flat dark area on the pad.

Everything is flat-shaded: the dome is meant to read as folded plates, and the
button rings stay crisp.  Slots are assigned after the booleans by face position
(nearest button centre and local dome height, stick centres, strip box), so the
material list is exactly the five colours plus the shell no matter how the Exact
solver re-triangulates.
"""

import bmesh
from math import hypot

from prop_lib import lathe

ID = 'game_controller'
NAME_ZH = '游戏手柄'
CATEGORY = 'electronics_cables'
SOURCING = 'model ourselves：150 x 100 x 60 mm 双手柄手柄，按键并入单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.150
# 420 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 = 672, capped by the t2
# hard limit of 500 (MATERIAL_AND_DETAIL_SPEC.md 4).
TRI_BUDGET = 500

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'plastic_grey': ((0.230, 0.235, 0.240), 0.50),
    'plastic_red': ((0.480, 0.075, 0.060), 0.48),
    'plastic_blue': ((0.075, 0.145, 0.400), 0.48),
    'plastic_green': ((0.090, 0.260, 0.130), 0.48),
    'plastic_yellow': ((0.640, 0.450, 0.060), 0.48),
    'rubber': ((0.045, 0.045, 0.048), 0.88),
}
SLOT_ORDER = ('plastic_grey', 'plastic_red', 'plastic_blue', 'plastic_green',
              'plastic_yellow', 'rubber')
MATERIALS = [
    {'key': 'plastic_grey', 'name_zh': '石墨灰机身',
     'linear_rgb': list(PALETTE['plastic_grey'][0]),
     'roughness': PALETTE['plastic_grey'][1]},
    {'key': 'plastic_red', 'name_zh': '红色按键 A',
     'linear_rgb': list(PALETTE['plastic_red'][0]),
     'roughness': PALETTE['plastic_red'][1]},
    {'key': 'plastic_blue', 'name_zh': '蓝色按键 B',
     'linear_rgb': list(PALETTE['plastic_blue'][0]),
     'roughness': PALETTE['plastic_blue'][1]},
    {'key': 'plastic_green', 'name_zh': '绿色按键',
     'linear_rgb': list(PALETTE['plastic_green'][0]),
     'roughness': PALETTE['plastic_green'][1]},
    {'key': 'plastic_yellow', 'name_zh': '黄色按键',
     'linear_rgb': list(PALETTE['plastic_yellow'][0]),
     'roughness': PALETTE['plastic_yellow'][1]},
    {'key': 'rubber', 'name_zh': '深色摇杆帽与屏幕条',
     'linear_rgb': list(PALETTE['rubber'][0]),
     'roughness': PALETTE['rubber'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.150', 'game_longest_dim_m': '0.150'}
NOTES = ('Plan outline: 24 points, +/-0.075 across the grips and +/-0.049 at the '
         'lobes, 66 mm deep at the waist.  Dome: pole at 0.060 with rings at '
         '0.62 and 0.88 of the outline, height 0.060 - 0.020*(|x|/0.075)^1.7 '
         '- 0.004*(|y|/0.048)^2, so the grips end at 0.038; 24-quad rim down to '
         'z=0 and one n-gon underside.  Slab 190 triangles; four 6-sided O 16 mm '
         'buttons on the local dome height at (+/-0.024, +/-0.020), 5.5 mm proud, '
         '96 triangles; two 6-sided O 18.4 mm thumbstick collars with domed tops at '
         '(+/-0.048, -0.010), 36 triangles; one 28 x 18 mm screen pocket cut '
         '2.5-4 mm into the crown between the button rows.  AABB 0.150 x 0.098 x '
         '0.0609.  '
         'Six slots by face position after the unions.  One closed shell.')

HALF_X, HALF_Y = 0.075, 0.048
TOP_CENTRE, TOP_DROP_X, TOP_DROP_Y = 0.060, 0.026, 0.005
RING_SCALES = (0.62, 0.88, 1.0)
# Underside tuck: one ring in from the outline so the pad has a slanted back
# instead of a 20 mm vertical brick edge.  The old two-ring chamfer cost one
# 24-quad band (48 triangles); the screen pocket and the thumbsticks needed that
# budget more than a second bevel nobody sees from above.
SIDE_RINGS = ((0.955, 0.000),)
BUTTON_R, BUTTON_SEGS = 0.0080, 6
BUTTON_SINK, BUTTON_RISE = 0.0020, 0.0055
# (slot key, x, y) - the four face buttons, one colour each.
BUTTONS = (
    ('plastic_red', -0.024, 0.020),
    ('plastic_blue', 0.024, 0.020),
    ('plastic_green', -0.024, -0.020),
    ('plastic_yellow', 0.024, -0.020),
)
STICK_R, STICK_SEGS = 0.0092, 6
STICK_SINK, STICK_TOP = 0.0065, 0.0120
# The caps sit a little forward of the grip centre line (y=+0.002 hid the far one
# behind the crown at the preview's 21-degree elevation) and stand 5.5 mm proud.
STICK_POS = ((-0.048, -0.010), (0.048, -0.010))
# The dark "screen" is a pocket pressed into the crown, not a part welded on:
# a raised bar at the front lip straddled the waist notch (the plan outline dips
# to y=0.028 at x=0), so its box hung in the air and exported as a second shell.
# A recess can only remove material, so it cannot float, and a display window
# set 2-4 mm below the shell reads better than a bar standing on it anyway.
POCKET = {'hw': 0.0140, 'y0': 0.0040, 'y1': 0.0220, 'z0': 0.0545, 'z1': 0.0650}

# Upper half of the plan, left tip to right tip; the lower half mirrors it.
OUTLINE_UPPER = [
    (-0.075, 0.000),
    (-0.072, 0.018),
    (-0.064, 0.036),
    (-0.050, 0.049),
    (-0.032, 0.047),
    (-0.016, 0.034),
    (0.000, 0.028),
    (0.016, 0.034),
    (0.032, 0.047),
    (0.050, 0.049),
    (0.064, 0.036),
    (0.072, 0.018),
    (0.075, 0.000),
]


def _outline():
    lower = [(x, -y) for (x, y) in reversed(OUTLINE_UPPER[1:-1])]
    return OUTLINE_UPPER + lower


def _top(x, y):
    return (TOP_CENTRE
            - TOP_DROP_X * (abs(x) / HALF_X) ** 1.7
            - TOP_DROP_Y * (abs(y) / HALF_Y) ** 2.0)


def _slab(outline):
    """Domed closed slab: pole, concentric rings, rim to z=0, flat underside."""
    bm = bmesh.new()
    count = len(outline)
    pole = bm.verts.new((0.0, 0.0, _top(0.0, 0.0)))
    rings = []
    for scale in RING_SCALES:
        rings.append([bm.verts.new((x * scale, y * scale, _top(x * scale,
                                                              y * scale)))
                      for (x, y) in outline])
    for i in range(count):
        j = (i + 1) % count
        bm.faces.new((pole, rings[0][i], rings[0][j]))
    for k in range(len(rings) - 1):
        lower, upper = rings[k], rings[k + 1]
        for i in range(count):
            j = (i + 1) % count
            bm.faces.new((lower[i], lower[j], upper[j], upper[i]))
    outer = rings[-1]
    for scale, z in SIDE_RINGS:
        nxt = [bm.verts.new((x * scale, y * scale, z))
               for (x, y) in outline]
        for i in range(count):
            j = (i + 1) % count
            bm.faces.new((outer[i], outer[j], nxt[j], nxt[i]))
        outer = nxt
    bm.faces.new(list(reversed(outer)))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def build(b):
    grey, grey_rough = PALETTE['plastic_grey']
    body = b.add(_slab(_outline()), 'plastic_grey', grey, grey_rough,
                 smooth=False, name='body')

    parts = []
    for key, cx, cy in BUTTONS:
        base = _top(cx, cy) - BUTTON_SINK
        bm = lathe([(0.0, 0.0), (BUTTON_R, 0.0),
                    (BUTTON_R, BUTTON_SINK + BUTTON_RISE)],
                   segments=BUTTON_SEGS)
        for v in bm.verts:
            v.co.x += cx
            v.co.y += cy
            v.co.z += base
        parts.append(b.add(bm, 'plastic_grey', grey, grey_rough,
                           smooth=False, name='button_%+.0f_%+.0f' % (cx, cy)))

    for i, (cx, cy) in enumerate(STICK_POS):
        base = _top(cx, cy) - STICK_SINK
        # rim at +10 mm (3.5 mm above the dome), apex at +12 mm: the first
        # draft put the rim at +6 mm, i.e. below the dome surface, so only the
        # 6 mm cone poked out and the caps read as dark spikes, not caps.
        bm = lathe([(0.0, 0.0), (STICK_R, 0.0), (STICK_R, 0.0100),
                    (0.0, STICK_TOP)], segments=STICK_SEGS)
        for v in bm.verts:
            v.co.x += cx
            v.co.y += cy
            v.co.z += base
        parts.append(b.add(bm, 'plastic_grey', grey, grey_rough, smooth=False,
                           name='stick%d' % i))

    b.boolean_union(body, parts)

    cutter = b.add(_pocket_prism(), 'plastic_grey', grey, grey_rough,
                   smooth=False, name='pocketcut')
    b.boolean_difference(body, [cutter])

    me = body.data
    me.materials.clear()
    for key in SLOT_ORDER:
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    index = {key: i for i, key in enumerate(SLOT_ORDER)}
    for poly in me.polygons:
        poly.material_index = index[_slot_of(poly.center)]


def _pocket_prism():
    """Box cutter for the crown pocket; its top sits above the dome (z=0.060)."""
    bm = bmesh.new()
    hw, y0, y1, z0, z1 = (POCKET['hw'], POCKET['y0'], POCKET['y1'],
                          POCKET['z0'], POCKET['z1'])
    corners = [(-hw, y0), (hw, y0), (hw, y1), (-hw, y1)]
    lower = [bm.verts.new((x, yy, z0)) for (x, yy) in corners]
    upper = [bm.verts.new((x, yy, z1)) for (x, yy) in corners]
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((lower[i], lower[j], upper[j], upper[i]))
    bm.faces.new(list(reversed(lower)))
    bm.faces.new(upper)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _slot_of(c):
    """Slot for one face centre, tested from the smallest part outwards."""
    # Pocket floor and walls: below the surrounding dome (0.058), so the shell
    # faces above the pocket cannot take the rubber slot.
    if (abs(c.x) < POCKET['hw'] + 0.0008
            and POCKET['y0'] - 0.0008 < c.y < POCKET['y1'] + 0.0008
            and POCKET['z0'] - 0.0006 < c.z < 0.0578):
        return 'rubber'
    for (cx, cy) in STICK_POS:
        if hypot(c.x - cx, c.y - cy) < STICK_R + 0.0008 \
                and c.z > _top(cx, cy) - 0.0030:
            return 'rubber'
    for key, cx, cy in BUTTONS:
        if hypot(c.x - cx, c.y - cy) < BUTTON_R + 0.0008 \
                and c.z > _top(cx, cy) + 0.0012:
            return key
    return 'plastic_grey'
