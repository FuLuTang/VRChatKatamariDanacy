"""Soap bar - 90 x 55 x 25 mm rounded bar with a stamped ring and paper tray.

Two stacked rounded-rect prisms give the softened bar silhouette, and a
shallow 1.5 mm ring groove (a 10-gon ring tool built by subtracting an inner
cylinder from an outer one, then cut into the top) reads as the stamped oval.
The whole bar sits in a 3.5 mm paper tray that is 2 mm larger in plan, so the
90 x 55 x 25 mm AABB is the tray and the top of the soap.

Material pass (2026-11-15 rework): the old bar was a 0.86 cream and the tray a
0.76 kraft - within 0.10 of each other, both near white, so the whole piece was
one pale block.  The bar is now the green plastic key, the tray the pale paper
key (0.54 apart), and the existing stamped ring groove takes the ink slot: the
groove is already a real 1.5 mm recess, so the dark print panel is geometry the
model had and simply never coloured.  No triangle change: 186.
"""

from prop_lib import lathe, prism, rounded_rect

ID = 'soap_bar'
NAME_ZH = '肥皂'
CATEGORY = 'household'
SOURCING = 'model ourselves：90 x 55 x 25 mm 圆角肥皂 + 纸托'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.090
TRI_BUDGET = 200
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.090', 'game_longest_dim_m': '0.090'}
MATERIALS = [
    {'key': 'plastic_green', 'name_zh': '皂体绿', 'linear_rgb': [0.090, 0.260, 0.130],
     'roughness': 0.45},
    {'key': 'paper', 'name_zh': '纸托米黄', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '压印环暗色', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Tray 90 x 55 x 2 mm (R 8 mm); lower soap 86 x 51 x 18 mm (R 10 '
         'mm) at z 1-19 mm; upper soap 80 x 46 x 8 mm (R 10 mm) at z 17-25 mm.  '
         'Ring groove outer r 20 mm / inner r 13 mm, 1.2 mm deep, cut 1.2 mm '
         'into the top.  Only the 2 mm tray is paper; the groove floor and walls '
         '(radius 12.5-20.5 mm above z 23.2 mm) take the ink slot as the stamped '
         'print.')

TRAY_W, TRAY_D, TRAY_H = 0.090, 0.055, 0.002
LOWER_W, LOWER_D, LOWER_Z0, LOWER_Z1 = 0.086, 0.051, 0.001, 0.019
UPPER_W, UPPER_D, UPPER_Z0, UPPER_Z1 = 0.080, 0.046, 0.017, 0.025
RING_OUTER, RING_INNER = 0.020, 0.013
RING_BOTTOM, RING_TOP = 0.0238, 0.0255
STAMP_RADIUS0, STAMP_RADIUS1 = 0.0125, 0.0205
STAMP_Z = 0.0232

GREEN = (0.090, 0.260, 0.130)
PAPER = (0.800, 0.775, 0.700)
INK = (0.030, 0.035, 0.045)


def build(b):
    tray = b.add(prism(rounded_rect(TRAY_W, TRAY_D, 0.008, corner_segments=1),
                       TRAY_H),
                 'paper', PAPER, 0.90,
                 smooth=False, name='tray')
    lower = b.add(prism(rounded_rect(LOWER_W, LOWER_D, 0.010, corner_segments=1),
                        LOWER_Z1 - LOWER_Z0, start=LOWER_Z0),
                  'plastic_green', GREEN, 0.45,
                  smooth=False, name='lower')
    upper = b.add(prism(rounded_rect(UPPER_W, UPPER_D, 0.010, corner_segments=1),
                        UPPER_Z1 - UPPER_Z0, start=UPPER_Z0),
                  'plastic_green', GREEN, 0.45,
                  smooth=False, name='upper')
    b.boolean_union(tray, [lower, upper])

    ring_outer = b.add(lathe([(RING_OUTER, RING_BOTTOM),
                              (RING_OUTER, RING_TOP)], segments=10),
                       'plastic_green', GREEN, 0.45,
                       smooth=False, name='ring_outer')
    ring_inner = b.add(lathe([(RING_INNER, RING_BOTTOM - 0.0005),
                              (RING_INNER, RING_TOP + 0.0005)], segments=8),
                       'plastic_green', GREEN, 0.45,
                       smooth=False, name='ring_inner')
    b.boolean_difference(ring_outer, [ring_inner])
    b.boolean_difference(tray, [ring_outer])
    info = b.finish(TARGET_LONGEST_M)

    me = b.object.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    zmin = min(v.co.z for v in me.vertices)
    for poly in me.polygons:
        c = poly.center
        z = c.z - zmin
        r = (c.x * c.x + c.y * c.y) ** 0.5
        if z < 0.0021:
            poly.material_index = 1
        elif z > STAMP_Z and STAMP_RADIUS0 < r < STAMP_RADIUS1:
            poly.material_index = 2
        else:
            poly.material_index = 0
    return info
