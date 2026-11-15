"""Matchbox - 55 x 36 x 14 mm outer sleeve with the drawer pulled out.

The sleeve is a closed rectangular tube over the drawer; the drawer is slightly
larger than the tube cavity, so the Exact union welds the two into a single shell
and the only visible step is where the drawer sticks out at +X.  The striking
strip is the whole +Y side of the sleeve, switched to the ink slot by the
outward face normal - a separate 0.5 mm plate would add triangles without
reading better at 512 px.

Material pass (2026-11-15 rework): the old sleeve and drawer were both warm
browns (0.30/0.16/0.07 and 0.70/0.57/0.30) and there was no dark slot, so the
box read as one tan lump.  Four slots now: a dark cardboard sleeve (paper_dark),
a dark stained wood drawer (wood_dark), the pale paper slot on the 3 mm of drawer
that protrudes at +X (the drawer front you actually see) and ink for the striking
strip.  Sleeve 0.36 vs drawer front 0.80 is a 0.44 step; wood_dark 0.135 and ink
0.045 read as separate slots through their 30 deg vs 210 deg hue split.
"""

from prop_lib import prism, rounded_rect

ID = 'matchbox'
NAME_ZH = '火柴盒'
CATEGORY = 'stationery'
SOURCING = 'model ourselves：55 x 36 x 14 mm 外盒 + 拉出内抽屉'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.055
TRI_BUDGET = 160
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.055', 'game_longest_dim_m': '0.055'}
MATERIALS = [
    {'key': 'paper_dark', 'name_zh': '盒套深纸板', 'linear_rgb': [0.360, 0.320, 0.250],
     'roughness': 0.92},
    {'key': 'wood_dark', 'name_zh': '内抽屉深木色', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
    {'key': 'paper', 'name_zh': '抽屉正面浅纸', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '擦火面', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.85},
]
NOTES = ('Sleeve outer 36 x 14 mm (R 3 mm) and 49 mm long, cavity 30 x 8 mm '
         'cut through; drawer 55 x 34 x 12 mm inserted so it protrudes at +X.  '
         'Drawer cross-section exceeds the cavity, so the union is one shell.  '
         'Slots: ink on every +Y sleeve face (striking strip), paper on the '
         'drawer front beyond x 24.8 mm, wood_dark on the drawer body, '
         'paper_dark on the sleeve.')

SLEEVE_L, SLEEVE_W, SLEEVE_H = 0.049, 0.036, 0.014
CAVITY_W, CAVITY_H = 0.030, 0.008
DRAWER_L, DRAWER_W, DRAWER_H = 0.055, 0.034, 0.012
DRAWER_X0 = -0.0255
FRONT_X = 0.0248

PAPER_DARK = (0.360, 0.320, 0.250)
WOOD_DARK = (0.135, 0.078, 0.038)
PAPER = (0.800, 0.775, 0.700)
INK = (0.030, 0.035, 0.045)


def build(b):
    sleeve = b.add(
        prism(rounded_rect(SLEEVE_W, SLEEVE_H, 0.003, corner_segments=1),
              SLEEVE_L, axis='x', start=-SLEEVE_L * 0.5),
        'paper_dark', PAPER_DARK, 0.92, smooth=False,
        name='sleeve')
    cavity = b.add(
        prism(rounded_rect(CAVITY_W, CAVITY_H, 0.002, corner_segments=1),
              SLEEVE_L + 0.010, axis='x', start=-SLEEVE_L * 0.5 - 0.005),
        'paper_dark', PAPER_DARK, 0.92, smooth=False,
        name='cavity')
    b.boolean_difference(sleeve, [cavity])
    drawer = b.add(
        prism(rounded_rect(DRAWER_W, DRAWER_H, 0.003, corner_segments=1),
              DRAWER_L, axis='x', start=DRAWER_X0),
        'wood_dark', WOOD_DARK, 0.75, smooth=False,
        name='drawer')
    b.boolean_union(sleeve, [drawer])
    info = b.finish(TARGET_LONGEST_M)

    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        c = poly.center
        if poly.normal.y > 0.9:
            poly.material_index = 3
        elif c.x > FRONT_X:
            poly.material_index = 2
        elif abs(c.y) < DRAWER_W * 0.5 - 0.0005 and abs(c.z) < DRAWER_H * 0.5:
            poly.material_index = 1
        else:
            poly.material_index = 0
    return info
