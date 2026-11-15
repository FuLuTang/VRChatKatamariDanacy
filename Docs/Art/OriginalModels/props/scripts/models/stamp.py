"""Stamp - 22 x 26 mm postage stamp: closed slab with a pinked perforation edge.

The design three-axis is 0.022 x 0.026 x 0.0005 m.  A 0.5 mm closed slab is not
buildable here - finish() welds doubles at 1e-6 m and dissolves edges at 1e-5 m,
so two 0.022 x 0.026 faces cannot be joined by a 0.5 mm wall and stay watertight.
The slab is therefore 1.5 mm thick, the documented modelling floor for flat paper
props; X and Y stay exactly 0.022 x 0.026 m, and TARGET_LONGEST_M is still the
catalogue's 0.026 m, so finish() does not rescale anything.

The perforation is part of the outline, not a row of punched holes: the rim
polygon alternates between the true stamp edge and a point bitten 1.4 mm inward,
so the silhouette reads as a stamp border while the shell stays single and
closed.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, flat paper
class): the slab is no longer one near-white slot.  Three slots, all from the
shared palette:
  * `paper`      - the stamp paper (linear 0.800 / 0.775 / 0.700, rough 0.90)
  * `ink`        - the recessed printed panel (linear 0.030 / 0.035 / 0.045)
  * `paper_dark` - the cut edge and the panel's side walls (0.360 / 0.320 /
                   0.250), which is what makes the perforation read at 512 px
The printed panel is real geometry: a 13 x 17 mm rectangular tool is subtracted
0.6 mm into the top face (a blind pocket, so the shell stays single and closed)
and its floor plus walls take the ink slot.  The pocket is inside the outline, so
the AABB is unchanged.
"""

from prop_lib import prism

ID = 'stamp'
NAME_ZH = '邮票'
CATEGORY = 'debris'
SOURCING = 'model ourselves：22 x 26 mm 齿孔边邮票，凹印面板 + 暗色切边，无贴图'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.026
TRI_BUDGET = 96
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.026', 'game_longest_dim_m': '0.026'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '邮票纸米白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '凹印墨块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'paper_dark', 'name_zh': '纸切边与印坑侧壁',
     'linear_rgb': [0.360, 0.320, 0.250], 'roughness': 0.92},
]
NOTES = ('Thickness 1.5 mm instead of the catalogue 0.5 mm: a 0.5 mm wall is '
         'below the slab floor that still welds cleanly, and the X/Y axes are '
         'the ones that carry the stamp read.  16-point prism outline, notches '
         '1.4 mm deep (no through holes).  Detail: a 0.013 x 0.017 m blind '
         'pocket 0.6 mm deep is subtracted from the top face and takes the ink '
         'slot; the cut edge (the prism side walls) takes paper_dark, so the '
         'perforation and the panel both read without a texture.  Pocket and '
         'edge stay inside the outline: AABB 0.022 x 0.026 x 0.0015 m.')

HALF_W = 0.011          # 0.022 m across the short side
HALF_H = 0.013          # 0.026 m along the long side, the target axis
THICK = 0.0015          # modelling floor for a flat paper prop
NOTCH = 0.0014          # how far a perforation notch bites inward
PANEL_W = 0.0065        # print panel half-width (13 mm across)
PANEL_H = 0.0085        # print panel half-height (17 mm along the long side)
PANEL_Z = 0.0009        # pocket floor: 0.6 mm below the top face
PAPER_RGB = (0.800, 0.775, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
PAPER_DARK_RGB = (0.360, 0.320, 0.250)


def outline():
    """Closed CCW 16-gon: four rim points per side, notches bite inward."""
    hw, hh, n = HALF_W, HALF_H, NOTCH
    return [
        # bottom, left to right
        (-hw, -hh), (-hw * 0.5, -hh + n), (0.0, -hh), (hw * 0.5, -hh + n),
        # right, bottom to top
        (hw, -hh), (hw - n, -hh * 0.5), (hw, 0.0), (hw - n, hh * 0.5),
        # top, right to left
        (hw, hh), (hw * 0.5, hh - n), (0.0, hh), (-hw * 0.5, hh - n),
        # left, top to bottom
        (-hw, hh), (-hw + n, hh * 0.5), (-hw, 0.0), (-hw + n, -hh * 0.5),
    ]


def panel():
    return [(-PANEL_W, -PANEL_H), (PANEL_W, -PANEL_H),
            (PANEL_W, PANEL_H), (-PANEL_W, PANEL_H)]


def _slot(centre):
    """Ink inside the printed pocket, paper_dark on the cut edge, else paper.

    The z floor is 0.5 mm on purpose: the slab's underside is a single n-gon at
    z = 0 whose centre also lies inside the panel rectangle, so a plain "z below
    the top face" test painted the whole back of the stamp with ink.
    """
    if 0.0005 < centre.z < PANEL_Z + 5e-4 and abs(centre.x) < PANEL_W + 1e-6 \
            and abs(centre.y) < PANEL_H + 1e-6:
        return 1
    if abs(centre.z - THICK * 0.5) < 1e-6:
        return 2
    return 0


def build(b):
    body = b.add(prism(outline(), THICK, axis='z'), 'paper', PAPER_RGB,
                 0.90, smooth=False, name='stamp')
    pocket = b.add(prism(panel(), THICK * 2.0, axis='z', start=PANEL_Z),
                   'paper', PAPER_RGB, 0.90, smooth=False, name='panel_cut')
    b.boolean_difference(body, [pocket])

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
