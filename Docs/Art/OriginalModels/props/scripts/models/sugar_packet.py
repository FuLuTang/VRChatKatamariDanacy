"""Sugar packet - 75 x 45 mm sachet: puffed body, two crimped end seals.

Design three-axis 0.075 x 0.045 x 0.008 m.  A real sachet is a paper tube with
both ends pressed flat, so the prop is built as one closed height-field slab: the
underside is flat at z=0 (a packet lying on the table is flattened by its own
weight) and the top puffs to 8.0 mm in the middle, falls to 5.6 mm at the crimp
shoulder and then ramps down to a 1.8 mm seal at each end, where a three-period
ripple in Y gives the pressed seam its ridges.

`sheet` gives the closed slab for free, but its grid is uniform, so the six X
columns are moved to non-uniform positions afterwards: without a column exactly
at the crimp shoulder the 7.5 mm seal band would be smeared over a 15 mm ramp and
the packet would read as a plain pillow.  Top and bottom vertices of a column
share (x, y), so the rim stays vertical and the shell stays closed.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, flat paper
class): three palette slots instead of one near-white one.
  * `paper`      - the sachet paper (linear 0.800 / 0.775 / 0.700, rough 0.90)
  * `ink`        - a raised printed label panel on the middle cell (0.030 /
                   0.035 / 0.045); it stands 0.25 mm proud, which is a real
                   0.25 mm step in the height field, not a painted stripe
  * `paper_dark` - the two pressed end seals (0.360 / 0.320 / 0.250), so the
                   crimp reads darker than the body
The raise is inside the body's puffed height, so the AABB only grows by 0.25 mm
on Z (0.0080 -> 0.00825 m) and stays inside the 1 mm size tolerance.
"""

import math

from prop_lib import sheet

ID = 'sugar_packet'
NAME_ZH = '糖包'
CATEGORY = 'food'
SOURCING = 'model ourselves：75 x 45 mm 两端压边糖包，中段鼓起 + 凸印色块'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.075
TRI_BUDGET = 100
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.075', 'game_longest_dim_m': '0.075'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '糖包纸米白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'ink', 'name_zh': '糖包印刷色块', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
    {'key': 'paper_dark', 'name_zh': '压边深纸色', 'linear_rgb': [0.360, 0.320, 0.250],
     'roughness': 0.92},
]
NOTES = ('One closed slab, flat underside at z=0, top puffed to 8.0 mm at the '
         'centre; crimp shoulder at 5.6 mm and a 1.8 mm (+/-0.7 mm ripple) seal '
         'in the last 7.5 mm at each end.  X columns are moved off the uniform '
         'grid so the crimp has its own column; 92 triangles, single shell.  '
         'Details: the middle cell (x -0.008..0.008 m) is raised 0.25 mm and gets '
         'the ink slot (printed label panel), the sealed ends get paper_dark.')

NX, NY = 5, 3                       # 6 x 4 vertex grid -> 92 triangles
HALF_L = 0.0375                     # 0.075 m, the target axis
HALF_W = 0.0225                     # 0.045 m
BODY_TOP = 0.0080
CRIMP_TOP = 0.0056
SEAL_TOP = 0.0014
SEAL_RIPPLE = 0.0009
CRIMP = 0.0265                      # where the body stops and the seal starts
COLUMNS = [-HALF_L, -CRIMP, -0.008, 0.008, CRIMP, HALF_L]
LABEL_X = 0.0075                    # uniform-grid half-width of the label cell
LABEL_RAISE = 0.00025               # printed panel stands 0.25 mm proud
PAPER_RGB = (0.800, 0.775, 0.700)
INK_RGB = (0.030, 0.035, 0.045)
PAPER_DARK_RGB = (0.360, 0.320, 0.250)


def top_height(x):
    """Top of the sachet at length position ``x`` (the ripple needs y)."""
    ax = abs(x)
    if ax <= CRIMP:
        t = ax / CRIMP
        return BODY_TOP - (BODY_TOP - CRIMP_TOP) * t ** 4
    u = (ax - CRIMP) / (HALF_L - CRIMP)
    return CRIMP_TOP + (SEAL_TOP - CRIMP_TOP) * u


def _slot(centre):
    """Ink on the raised label panel, paper_dark on the pressed seals."""
    if centre.z > 0.0013 and abs(centre.x) < 0.0085:
        return 1
    if centre.z > 0.0004 and abs(centre.x) > CRIMP:
        return 2
    return 0


def build(b):
    bm = sheet(NX, NY, -HALF_L, HALF_L, -HALF_W, HALF_W,
               lambda x, y: 0.0001, bottom_fn=lambda x, y: -0.0001)
    for v in bm.verts:
        col = int(round((v.co.x + HALF_L) / (2.0 * HALF_L) * NX))
        x = COLUMNS[col]
        is_top = v.co.z > 0.0
        h = top_height(x)
        if abs(x) > CRIMP:
            h += SEAL_RIPPLE * math.sin(math.tau * 3.0 * v.co.y / (2.0 * HALF_W))
        if is_top and abs(v.co.x) <= LABEL_X + 1e-9:
            h += LABEL_RAISE
        v.co.x = x
        v.co.z = h if is_top else 0.0
    body = b.add(bm, 'paper', PAPER_RGB, 0.90, smooth=False, name='sachet')

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
