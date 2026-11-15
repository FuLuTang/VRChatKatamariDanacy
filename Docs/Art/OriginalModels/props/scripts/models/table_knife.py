"""Table knife - 230 mm knife: flat wedge blade with a bevelled edge, wood handle.

Design three-axis 0.230 x 0.020 x 0.008 m.  The knife lies flat, so the underside
is a flat plane at z=0 - the blade's whole flat side rests on the cloth - and the
height field carries everything else: the blade climbs from 0.5 mm at the cutting
edge to 2.2 mm at the spine (the grind), and the handle swells to 8 mm on a sine
profile so it reads as a rounded grip rather than a second blade.

A `prism` would close the silhouette with two n-gon caps, and the cap is the face
you actually look at on a flat knife, so the blade/handle split has to be per
quad.  An 8 x 4 `sheet` gives that split; the outline is shaped by scaling every
vertex's Y with the silhouette half-width at its X station (tapered tip, 14 mm
handle, rounded butt).  The X columns are then moved off the uniform grid so one
cell is exactly 1.2 mm wide at the bolster: that cell drops 1 mm and becomes the
collar between blade and handle, which is the 1 mm step the spec asks for at a
metal/handle junction.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, tool class):
four palette slots, all metal/handle colours, no invented tones.
  * `metal`   - the blade (linear 0.430 / 0.450 / 0.470, rough 0.32)
  * `ceramic` - the polished tip, the brighter metal slot (0.760 / 0.745 / 0.700,
                rough 0.35)
  * `brass`   - the collar at the bolster (0.520 / 0.380 / 0.120, rough 0.30)
  * `wood_dark` - the handle (0.135 / 0.078 / 0.038, rough 0.75), the dark slot:
                the palette's `wood` (0.224) would leave the prop with no dark
                tone at all, and a dark walnut grip separates hardest from the
                steel blade at 512 px
"""

import math

from prop_lib import sheet

ID = 'table_knife'
NAME_ZH = '餐刀'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：230 x 20 x 8 mm 餐刀，抛光刀尖 + 黄铜箍 + 木柄'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.230
TRI_BUDGET = 200
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.230', 'game_longest_dim_m': '0.230'}
MATERIALS = [
    {'key': 'metal', 'name_zh': '刀身不锈钢', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'ceramic', 'name_zh': '抛光刀尖', 'linear_rgb': [0.760, 0.745, 0.700],
     'roughness': 0.35},
    {'key': 'brass', 'name_zh': '刀箍黄铜', 'linear_rgb': [0.520, 0.380, 0.120],
     'roughness': 0.30},
    {'key': 'wood_dark', 'name_zh': '深木柄', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('Sheet grid 8 x 4 reshaped into the silhouette: blunt 3 mm tip from '
         'x=-0.115 opening to the 20 mm blade at x=-0.086, blade to x=0.0288, '
         'bolster taper to a 15 mm handle at x=0.0575, rounded butt at x=0.115.  '
         'Underside flat at z=0; top 0.0005 m at the cutting edge rising to '
         '0.0022 m at the spine, handle 0.0080 m on a sin^0.75 section.  Detail: '
         'the X columns are non-uniform and the 1.2 mm cell at x 0.0300-0.0312 '
         'drops 1 mm, forming the brass collar (the metal/handle step).  '
         'Materials: ceramic tip (x < -0.0620), brass collar, wood_dark handle from '
         'x = 0.0575, metal elsewhere.  176 triangles, one closed shell.')

NX, NY = 8, 4
HALF_L = 0.115                  # 0.230 m, the target axis
HALF_W = 0.010                  # 0.020 m at the widest
TIP_START = -0.098              # where the tip taper reaches full blade width
BLADE_END = 0.0288              # blade/bolster junction
HANDLE_START = 0.0575           # end of the bolster taper
BUTT_START = 0.1035
TIP_HALF = 0.0030
BLADE_HALF = 0.010
HANDLE_HALF = 0.0075
BUTT_HALF = 0.0035
EDGE_TOP = 0.0005
BLADE_TOP = 0.0022
HANDLE_TOP = 0.0080
# Non-uniform X columns (9 values -> 8 cells); cell 5 is the 1.2 mm collar.
XCOL = (-0.115, -0.086, -0.0575, -0.028, 0.0, 0.0300, 0.0312, 0.0575, 0.115)
COLLAR_CELL = 5
COLLAR_DROP = 0.0010
POLISHED_TIP = -0.0620          # the bright metal stops here
METAL_RGB = (0.430, 0.450, 0.470)
CERAMIC_RGB = (0.760, 0.745, 0.700)
BRASS_RGB = (0.520, 0.380, 0.120)
WOOD_RGB = (0.135, 0.078, 0.038)


def half_width(x):
    """Silhouette half-width at length position ``x``."""
    if x <= TIP_START:
        u = (x + HALF_L) / (TIP_START + HALF_L)
        u = max(0.0, min(1.0, u))
        return TIP_HALF + (BLADE_HALF - TIP_HALF) * (u ** 0.65)
    if x <= BLADE_END:
        return BLADE_HALF
    if x <= HANDLE_START:
        u = (x - BLADE_END) / (HANDLE_START - BLADE_END)
        return BLADE_HALF + (HANDLE_HALF - BLADE_HALF) * u
    if x <= BUTT_START:
        return HANDLE_HALF
    u = min(1.0, (x - BUTT_START) / (HALF_L - BUTT_START))
    return HANDLE_HALF + (BUTT_HALF - HANDLE_HALF) * u


def top_height(x, span):
    """Top edge height; ``span`` is 0 at the cutting edge, 1 at the spine."""
    blade = EDGE_TOP + (BLADE_TOP - EDGE_TOP) * span
    handle = BLADE_TOP + (HANDLE_TOP - BLADE_TOP) * math.sin(math.pi * span) ** 0.75
    if x <= BLADE_END:
        return blade
    if x >= HANDLE_START:
        return handle
    u = (x - BLADE_END) / (HANDLE_START - BLADE_END)
    return blade + (handle - blade) * u


def _column(x):
    return int(round((x + HALF_L) / (2.0 * HALF_L) * NX))


def _slot(centre):
    x = centre.x
    if XCOL[COLLAR_CELL] - 1e-5 <= x <= XCOL[COLLAR_CELL + 1] + 1e-5:
        return 2
    if x > HANDLE_START:
        return 3
    if x < POLISHED_TIP:
        return 1
    return 0


def build(b):
    bm = sheet(NX, NY, -HALF_L, HALF_L, -HALF_W, HALF_W,
               lambda x, y: 0.0001, bottom_fn=lambda x, y: -0.0001)
    for v in bm.verts:
        col = _column(v.co.x)
        x = XCOL[col]
        span = min(1.0, abs(v.co.y) / HALF_W)
        is_top = v.co.z > 0.0
        width = half_width(x) * span
        v.co.x = x
        v.co.y = width if v.co.y > 0.0 else (-width if v.co.y < 0.0 else 0.0)
        z = top_height(x, span)
        if is_top:
            if col in (COLLAR_CELL, COLLAR_CELL + 1) and span > 0.05:
                z = max(EDGE_TOP, z - COLLAR_DROP)
            v.co.z = z
        else:
            v.co.z = 0.0
    knife = b.add(bm, 'metal', METAL_RGB, 0.32, smooth=False, name='knife')

    me = knife.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
