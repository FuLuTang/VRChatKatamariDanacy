"""Orange - smooth oblate 8 cm orange with a small green stem.

The previous orange had eight cosine grooves carved into the radius and was
flat-shaded to keep them visible.  The result read as a pumpkin: the horizontal
latitude rings of the lathe caught the light as bands, and the eight meridians
made it look segmented.  This version removes the grooves entirely, uses smooth
normals and lets the silhouette do the work: a 0.080 m wide, 0.0774 m tall
oblate spheroid with a 4.6 mm stem rising out of a small navel.

The stem is not a second object.  The lathe profile climbs the body, pulls in to
a 3.6 mm neck at z=0.0728 and continues up the stem to a 2.6 mm top at
z=0.077417, so the whole orange is one closed shell and the height is exactly
the old 0.077417 m.  A face predicate paints the stem green above z=0.0728;
the body keeps the rind colour.

The old final size came from a 0.072 m raw body scaled up by a 1.075 finish
factor (the grooves had cut the raw X extent).  The new raw body is 0.080 m wide
and the stem brings the height to 0.077417 m, so finish sees a 0.080 m longest
edge and leaves the dimensions unchanged: same 0.080000 x 0.080000 x 0.077417 m,
same AABB-centre pivot, same filenames.
"""

from prop_lib import lathe

ID = 'orange'
NAME_ZH = '橙子'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.08
CSV_GAME_LONGEST_M = 0.30
SOURCING = 'model ourselves：Ø8 cm 微扁球 + 小绿梗'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.08
TRI_BUDGET = 360
MATERIALS = [
    {'key': 'rind', 'name_zh': '橙皮', 'linear_rgb': [0.520, 0.160, 0.020],
     'roughness': 0.70},
    {'key': 'stem', 'name_zh': '绿梗', 'linear_rgb': [0.110, 0.230, 0.060],
     'roughness': 0.60},
]
NOTES = ('20 segments, 11 profile points = 360 triangles, smooth-shaded.  Raw '
         'diameter 0.080 m, raw height 0.077417 m, so finish() scales by 1.0 and '
         'the exported size stays exactly 0.080000 x 0.080000 x 0.077417 m.  The '
         'body is an oblate spheroid with its widest ring at z=0.036; the navel '
         'pulls in to r=0.0036 at z=0.0728, then the stem rises to a 0.0026 m '
         'flat top at 0.077417.  Green material above z=0.0728 and inside '
         'r=0.0050.  One closed shell, no grooves, no separate stem.')

SEGMENTS = 20
STEM_Z = 0.0728
STEM_R = 0.0050

PROFILE = [
    (0.0000, 0.0000),   # bottom pole
    (0.0220, 0.0025),
    (0.0340, 0.0100),
    (0.0400, 0.0360),   # widest ring
    (0.0375, 0.0500),
    (0.0300, 0.0620),
    (0.0180, 0.0695),
    (0.0060, 0.0725),   # navel shoulder
    (0.0036, 0.0728),   # stem base
    (0.0026, 0.077417),  # stem top rim
    (0.0000, 0.077417),  # stem top centre
]


def on_stem(c):
    return c.z > STEM_Z and (c.x * c.x + c.y * c.y) < STEM_R * STEM_R


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'rind', (0.520, 0.160, 0.020), 0.70, smooth=True,
          extras=[('stem', (0.110, 0.230, 0.060), 0.60, on_stem)])
