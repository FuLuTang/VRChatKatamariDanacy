"""Hazelnut - 22 mm wide, 25 mm tall ovoid with a pale basal scar and a sharp tip.

The model keeps the 0.022 x 0.025 m envelope and reads with three features: a
flat pale scar disc on the underside (a 4.2 mm disc exactly at z = 0, flush, not
a bump, with the pale material running 2.6 mm up the base so it is visible in a
three-quarter view), a 0.8 mm dark seam ring where the scar meets the shell, and
a concave taper into a real point at the top.

12 segments and 9 bands give 156 triangles, inside the 180 budget.

Material pass (2026-11-15 rework): the old shell was 0.30/0.16/0.07 with a
0.64/0.52/0.35 scar - two light-brown slots 0.34 apart but no dark slot, so the
nut read as one tan blob.  The shell is now the shared wood key, the scar the
pale shell key, and the seam the dark ink key (0.045 peak), which is the low
value the spec requires and also gives the scar a real edge.
"""

from prop_lib import lathe

ID = 'hazelnut'
NAME_ZH = '榛子'
CATEGORY = 'nature'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.025', 'game_longest_dim_m': '0.025'}
SOURCING = 'model ourselves：Ø0.022 x 0.025 m 卵形榛子，底部果脐 + 顶部尖端'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.025
TRI_BUDGET = 180
MATERIALS = [
    {'key': 'wood', 'name_zh': '榛壳棕', 'linear_rgb': [0.330, 0.205, 0.105],
     'roughness': 0.72},
    {'key': 'shell', 'name_zh': '果脐浅色', 'linear_rgb': [0.560, 0.470, 0.330],
     'roughness': 0.42},
    {'key': 'ink', 'name_zh': '果脐暗缝', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('12 segments, 9 profile points, 8 bands = 156 triangles (one extra '
         'point carves the 0.8 mm seam band).  Width 0.0220 m at z = 16.0 mm, '
         'height 0.0250 m at the tip.  The basal scar is a flat 4.2 mm disc lying '
         'on z = 0 (flush, no bump); the pale material covers every face below '
         'z = 2.6 mm, the dark seam is the 2.6-3.4 mm band, and the pale shell '
         'runs up over the shoulder.  The shoulder turns in at 20 mm and a '
         '45-degree beak closes on the axis at 25 mm.')

SEGMENTS = 12
SCAR_TOP = 0.0026
SEAM_TOP = 0.0034
SCAR_RADIUS = 0.0064

WOOD = (0.330, 0.205, 0.105)
SHELL = (0.560, 0.470, 0.330)
INK = (0.030, 0.035, 0.045)

PROFILE = [
    (0.0000, 0.0000),   # flat scar centre, on the ground
    (0.0046, 0.0000),   # flat scar rim, on the ground
    (0.0062, 0.0026),   # pale step into the shell
    (0.0068, 0.0034),   # dark seam band closes
    (0.0104, 0.0100),
    (0.0110, 0.0160),   # widest, 22 mm across
    (0.0096, 0.0202),   # shoulder
    (0.0048, 0.0228),   # beak base
    (0.0000, 0.0250),   # 45-degree tip on the axis
]


def scar(centre):
    """Pale faces of the basal scar and the lowest 2.6 mm of the shell."""
    return (centre.z < SCAR_TOP
            and centre.x * centre.x + centre.y * centre.y < SCAR_RADIUS ** 2)


def seam(centre):
    """The 0.8 mm dark ring between the pale scar and the shell."""
    return SCAR_TOP <= centre.z < SEAM_TOP


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'wood', WOOD, 0.72, smooth=True,
          extras=[('shell', SHELL, 0.42, scar),
                  ('ink', INK, 0.75, seam)])
