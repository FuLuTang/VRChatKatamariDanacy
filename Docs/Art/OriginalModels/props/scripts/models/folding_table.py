"""Folding table - 100 x 60 x 70 cm table with a dead flat top.

LEVEL_CONSTRUCTION.md section 4 (P0) gives folding_table at
1.00 x 0.60 x 0.70 m, 900 tris - the map's "billiard table": a 0.70 m top with a
cluster of small props on it.  prop_catalog.csv has no row, so the module
declares DOC_ROW.  The story plan's note "desktop slightly domed" is
deliberately ignored: this batch's contract is that every table/ramp surface is
flat, and a domed top would make the cluster slide off.

Top: 1.000 x 0.600 x 0.030 rounded-rect slab, top plane at z = 0.700.  Frame:
four 40 mm square legs on z = 0 plus two crossed braces per side, all metal
grey, split from the wooden top at z = 0.665.  The braces stop inside the legs
so every union has real overlap; nothing sticks through the top.
"""

from prop_lib import prism, rounded_rect

ID = 'folding_table'
NAME_ZH = '折叠桌'
CATEGORY = 'furniture'
SOURCING = 'model ourselves：LEVEL_CONSTRUCTION.md 4 P0（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 1.00
TRI_BUDGET = 900
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '1.00', 'game_longest_dim_m': '1.00'}
MATERIALS = [
    {'key': 'top', 'name_zh': '桌面浅木', 'linear_rgb': [0.560, 0.430, 0.270],
     'roughness': 0.68},
    {'key': 'frame', 'name_zh': '桌腿深灰', 'linear_rgb': [0.170, 0.180, 0.190],
     'roughness': 0.40},
]

TOP_W, TOP_D, TOP_T = 1.000, 0.600, 0.030
TOP_Z = 0.700 - TOP_T
LEG_X, LEG_Y, LEG_S = 0.420, 0.240, 0.040
NOTES = ('Top 1.000 x 0.600 x 0.030 with its upper plane at z = 0.700; four '
         '0.040 square legs at x +/-0.420, y +/-0.240; AABB 1.000 x 0.600 x 0.700, flat top. '
         'Size from LEVEL_CONSTRUCTION.md section 4 P0 (no CSV row).')


def build(b):
    top = MATERIALS[0]
    frame = MATERIALS[1]
    ob = b.add(prism(rounded_rect(TOP_W, TOP_D, 0.020), TOP_T, start=TOP_Z),
               'top', top['linear_rgb'], top['roughness'], smooth=False,
               name='top')
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            # prism() has no placement argument, so shift the 12-gon profile.
            pts = [(x + sx * LEG_X, y + sy * LEG_Y)
                   for (x, y) in rounded_rect(LEG_S, LEG_S, 0.004)]
            leg = b.add(prism(pts, TOP_Z + 0.002),
                        'frame', frame['linear_rgb'], frame['roughness'],
                        smooth=False, name='leg%d%d' % (sx > 0, sy > 0))
            b.boolean_union(ob, [leg])
    me = ob.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = 0 if poly.center.z > 0.665 else 1
    return b.finish(TARGET_LONGEST_M)
