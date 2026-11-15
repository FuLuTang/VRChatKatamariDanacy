"""Slipper - 0.26 m felt slipper: blue upper, red lined insole, ribbed rubber sole.

MessyRoom catalogue row slipper: 0.260 x 0.100 x 0.050 m, long, clothes_fabric.
The length runs on X (toe at +X) because the catalogue's longest edge is X, the
same convention as wood_plank; the sole is a stadium outline (260 x 100, R 32
mm) 15 mm thick and sits on z=0.

The sole is an 8-ring loft of that outline instead of one prism, so its side wall
carries two 1.2 mm inset grooves at z=0.0066-0.0078 - spec 3's "2-4 shallow
grooves" for soft goods, and the reason the sole now reads as a moulded foam
unit rather than a slice.  The top rings fell a 2.5 mm footbed well, so the red
lining sits inside a rubber rim and the dark sole is a visible frame from above
instead of a hairline under the toe.

The upper is one arc band, not a tube: an 8 mm-thick crescent between two
ellipses (outer 49 x 42 mm, inner 41 x 34 mm, both centred at z=6 mm) swept 55 mm
along X from x=-0.020 to x=0.035.  Both ends of the crescent lie at z=6 mm, i.e.
9 mm inside the sole, so the Exact union welds them and the tunnel under the arch
opens out the front and the back - that is what keeps "脚背处是空腔" from
becoming an enclosed void and a second shell.  The arch tops out at z=0.048, just
under the catalogue's 50 mm, and the band is 8 mm thick, so the instep opening
reads as a real slipper gap at preview size.

Three material slots by face height and normal:

  fabric_blue  the upper's outer felt
  cotton_red   the insole (the floor of the footbed well, which is what you see
               from above through the opening) and the tunnel roof - the "内里暗一档" lining,
               one step darker than the blue and a different hue, so the slipper
               has a lit outer and a warm dark inside
  rubber       the whole sole body: side wall, both grooves, underside

Materials by face height: everything above the sole top is fabric, the sole and
the part of the band buried in it are foam.
"""

import math

import bmesh

from prop_lib import prism, rounded_rect

ID = 'slipper'
NAME_ZH = '拖鞋'
CATEGORY = 'clothes_fabric'
SOURCING = 'model ourselves：260 x 100 x 50 mm 布面拖鞋，鞋面弧带焊进鞋底'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.260
# 240 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t3 cap 700.
TRI_BUDGET = 384

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'fabric_blue': ((0.120, 0.170, 0.290), 0.92),
    'cotton_red': ((0.420, 0.090, 0.080), 0.92),
    'rubber': ((0.045, 0.045, 0.048), 0.88),
}
MATERIALS = [
    {'key': 'fabric_blue', 'name_zh': '蓝布鞋面',
     'linear_rgb': list(PALETTE['fabric_blue'][0]),
     'roughness': PALETTE['fabric_blue'][1]},
    {'key': 'cotton_red', 'name_zh': '红色内里与鞋垫',
     'linear_rgb': list(PALETTE['cotton_red'][0]),
     'roughness': PALETTE['cotton_red'][1]},
    {'key': 'rubber', 'name_zh': '橡胶鞋底',
     'linear_rgb': list(PALETTE['rubber'][0]),
     'roughness': PALETTE['rubber'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.260', 'game_longest_dim_m': '0.260'}
NOTES = ('Sole: 12-point rounded rectangle 0.260 x 0.100 (R 32 mm) lofted through '
         '8 rings - (z, inset) 0.0000/0.0018 bottom chamfer, 0.0018/0, '
         '0.0060/0, 0.0066/0.0012, 0.0078/0.0012, 0.0084/0, 0.0150/0, '
         '0.0130/0.0025 - so the side wall has two 1.2 mm grooves and the top a '
         '2.5 mm footbed well; 188 triangles.  Upper: crescent '
         'between an outer 49 x 42 mm and an inner 41 x 34 mm ellipse centred at '
         'z=6 mm, 9 points per arc = 18-point polygon, swept 55 mm on X from '
         'x=-0.020 to 0.035.  Both crescent ends sit at z=6 mm, 9 mm inside the '
         'sole, so the union welds them; the arch top is z=0.048.  AABB 0.260 x '
         '0.0998 x 0.0480.  One closed shell.  Three slots: above the sole top '
         'fabric_blue, except downward-facing tunnel roof below z=0.042 which is '
         'cotton_red; inside the footbed well (horizontal faces below the sole top) '
         'cotton_red insole; the rest of the sole is rubber.')

SOLE_L, SOLE_W, SOLE_R, SOLE_H = 0.260, 0.100, 0.032, 0.015
SOLE_RINGS = [
    (0.0000, 0.0018),   # bottom chamfer edge
    (0.0018, 0.0000),
    (0.0060, 0.0000),
    (0.0066, 0.0012),   # groove in
    (0.0078, 0.0012),   # groove out
    (0.0084, 0.0000),
    (0.0150, 0.0000),   # top edge: the sole's felled rim
    (0.0130, 0.0025),   # 2.5 mm footbed well, so a rubber rim shows round the
]                       # lining instead of the red running to the outline
ARCH_SEGMENTS = 8
OUTER_A, OUTER_B = 0.049, 0.042
INNER_A, INNER_B = 0.041, 0.034
ARCH_CZ = 0.006
STRAP_X0, STRAP_W = -0.020, 0.055
LINING_TOP = 0.042


def _lofted_sole(rings):
    """Closed loft of the rounded-rect outline; n-gon caps top and bottom."""
    bm = bmesh.new()
    ring_verts = []
    for z, inset in rings:
        outline = rounded_rect(SOLE_L - 2.0 * inset, SOLE_W - 2.0 * inset,
                               SOLE_R - inset, corner_segments=2)
        ring_verts.append([bm.verts.new((x, y, z)) for (x, y) in outline])
    count = len(ring_verts[0])
    for k in range(len(ring_verts) - 1):
        lower, upper = ring_verts[k], ring_verts[k + 1]
        for i in range(count):
            j = (i + 1) % count
            bm.faces.new((lower[i], lower[j], upper[j], upper[i]))
    bm.faces.new(list(reversed(ring_verts[0])))
    bm.faces.new(ring_verts[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _arch():
    """Closed crescent polygon in (y, z): outer arc out, inner arc back."""
    points = []
    for i in range(ARCH_SEGMENTS + 1):
        a = math.pi - math.pi * i / ARCH_SEGMENTS
        points.append((OUTER_A * math.cos(a),
                       ARCH_CZ + OUTER_B * math.sin(a)))
    for i in range(ARCH_SEGMENTS + 1):
        a = math.pi * i / ARCH_SEGMENTS
        points.append((INNER_A * math.cos(a),
                       ARCH_CZ + INNER_B * math.sin(a)))
    return points


def build(b):
    rubber, rubber_rough = PALETTE['rubber']
    blue, blue_rough = PALETTE['fabric_blue']
    sole = b.add(_lofted_sole(SOLE_RINGS), 'rubber', rubber, rubber_rough,
                 smooth=False, name='sole')
    strap = b.add(prism(_arch(), STRAP_W, axis='x', start=STRAP_X0),
                  'fabric_blue', blue, blue_rough, smooth=True, name='upper')
    b.boolean_union(sole, [strap])

    me = sole.data
    me.materials.clear()
    for key in ('fabric_blue', 'cotton_red', 'rubber'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        c = poly.center
        if c.z > SOLE_H + 0.0002:
            lining = poly.normal.z < -0.25 and c.z < LINING_TOP
            poly.material_index = 1 if lining else 0
        elif poly.normal.z > 0.90:
            poly.material_index = 1      # insole: the floor of the footbed well
        else:
            poly.material_index = 2      # rubber: sole wall, rim, grooves, bottom
