"""AA battery - O 14 x 50 mm cell: steel body, yellow label sleeve, coloured ends.

MessyRoom catalogue row battery_aa: 0.050 x 0.014 x 0.014 m, round,
electronics_cables.  One lathe profile builds the whole cell: the recessed
negative end, a 1 mm shoulder out to the full 14 mm body, the body, a 0.15 mm
proud label sleeve, the flat positive shoulder and the small O 5.6 mm nub.  There
is no separate cap object - a snapped-on nub would be a second shell and a seam
line under the collar.

Four material slots, assigned by position along the cell axis after the lathe is
rotated to lie on +X (this is a "printing band" that is really a sleeve, so the
label is a ring of faces between two explicit profile points, and the sleeve is
0.15 mm proud so its edges catch a highlight):

  x < 0.0095   ink           negative end: face, shoulder and collar
  0.0095-0.0290 plastic_yellow  the wrapped label sleeve
  0.0290-0.0480 metal         bare steel body
  x >= 0.0480  brass         positive shoulder + nub

The brief's positive/negative terminals are brass and ink; the label is the one
bright colour on the prop.  No lettering is modelled: the sleeve is the print.

Ten segments: the sleeve's two extra rings cost 40 triangles, so the profile is
160 triangles against the catalogue's 100 x1.6 allowance.
"""

import math

from mathutils import Matrix

from prop_lib import lathe

ID = 'battery_aa'
NAME_ZH = '5号电池'
CATEGORY = 'electronics_cables'
SOURCING = 'model ourselves：Ø0.014 x 0.050 m 5 号电池，两端台阶单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.050
# 100 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 (spec 4); t0 cap 300.
TRI_BUDGET = 160

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim.
PALETTE = {
    'metal': ((0.430, 0.450, 0.470), 0.32),
    'ink': ((0.030, 0.035, 0.045), 0.75),
    'brass': ((0.520, 0.380, 0.120), 0.30),
    'plastic_yellow': ((0.640, 0.450, 0.060), 0.48),
}
MATERIALS = [
    {'key': 'metal', 'name_zh': '电池钢壳', 'linear_rgb': list(PALETTE['metal'][0]),
     'roughness': PALETTE['metal'][1]},
    {'key': 'plastic_yellow', 'name_zh': '黄色标签套',
     'linear_rgb': list(PALETTE['plastic_yellow'][0]),
     'roughness': PALETTE['plastic_yellow'][1]},
    {'key': 'brass', 'name_zh': '黄铜正极', 'linear_rgb': list(PALETTE['brass'][0]),
     'roughness': PALETTE['brass'][1]},
    {'key': 'ink', 'name_zh': '深色负极环', 'linear_rgb': list(PALETTE['ink'][0]),
     'roughness': PALETTE['ink'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.050', 'game_longest_dim_m': '0.050'}
NOTES = ('10 segments, 10 profile points, 8 rings = 160 triangles: 0.0500 m long '
         'and O 0.0140 at the body.  Negative end: flat O 12 mm face 1 mm inside '
         'the body diameter, a 1.2 mm shoulder, then the collar up to z 0.0095.  '
         'Label sleeve: O 0.0143 from z 0.0095 to 0.0290, 0.15 mm proud of the '
         'body, closed by two flat annuli.  Positive end: steel to 0.0480, flat '
         'shoulder into the O 5.6 mm brass nub rising to 0.0500.  One closed '
         'shell, no separate cap.  The lathe is rotated so the cell lies on +X '
         'and lifted 7 mm to rest on z=0.  Materials by face centre x: ink below '
         '0.0095, plastic_yellow to 0.0290, metal to 0.0480, brass above.')

SEGMENTS = 10
BODY_R = 0.0070
SLEEVE_R = 0.00715
NEG_FACE_R = 0.0065
NUB_R = 0.0032
COLLAR_TOP = 0.0095
LABEL_TOP = 0.0290
BODY_TOP = 0.0480

PROFILE = [
    (0.0000, 0.0000),   # negative face centre
    (NEG_FACE_R, 0.0000),   # negative face edge, 1 mm inset
    (BODY_R, 0.0012),   # short shoulder out to the full diameter
    (BODY_R, COLLAR_TOP),   # top of the dark negative collar
    (SLEEVE_R, COLLAR_TOP),   # label sleeve, proud by 0.15 mm
    (SLEEVE_R, LABEL_TOP),
    (BODY_R, LABEL_TOP),
    (BODY_R, BODY_TOP),   # bare steel body
    (NUB_R, BODY_TOP),   # flat shoulder into the positive nub
    (0.0000, 0.0500),   # nub tip
]


def build(b):
    metal, metal_rough = PALETTE['metal']
    bm = lathe(PROFILE, segments=SEGMENTS)
    rot = Matrix.Rotation(math.radians(90.0), 3, 'Y')
    for v in bm.verts:
        v.co = rot @ v.co
        v.co.z += BODY_R

    ob = b.add(bm, 'metal', metal, metal_rough, smooth=True, name='cell')
    me = ob.data
    me.materials.clear()
    for key in ('metal', 'plastic_yellow', 'brass', 'ink'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        x = poly.center.x
        if x < COLLAR_TOP:
            poly.material_index = 3      # ink: negative end
        elif x <= LABEL_TOP:
            poly.material_index = 1      # label sleeve
        elif x < BODY_TOP:
            poly.material_index = 0      # steel
        else:
            poly.material_index = 2      # brass positive terminal
