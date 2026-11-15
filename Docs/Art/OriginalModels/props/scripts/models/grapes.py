"""Grapes - seven berries fused into one closed bunch shell.

The plan asks for a 6 cm wide bunch: 7-9 berries of about 1.8 cm joined by short
stems into one closed shell.  Eight separate spheres welded by a boolean union
cost far more than the 300-triangle budget, so the berries are evaluated as a
metaball field.  The berry centres are spaced about 0.34 design units apart
(2.3 visible radii) and staggered in depth, which leaves a narrow neck between
neighbours instead of the single lump a denser row produces.  Positions are
fixed constants - no randomness - so the geometry digest is stable.

The bunch is laid out 2-3-2: the widest row is the three berries in the middle,
which is what gives the silhouette its bunch shape rather than a vertical
sausage.  Width and height are both close to 0.98 design units, so the 6 cm
target lands on the width with the berries at about 1.8 cm.  Four small field
balls at the midpoints of the row gaps act as short stems: they bridge the three
rows into one closed shell without fattening every berry into a single lump.
"""

from prop_lib import metaball_cluster

ID = 'grapes'
NAME_ZH = '葡萄'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.06
CSV_GAME_LONGEST_M = 0.08
SOURCING = 'model ourselves：6 cm 宽一串，7–9 颗 Ø1.8 cm 球用短梗连成一个闭合壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.06
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'skin', 'name_zh': '葡萄紫', 'linear_rgb': [0.130, 0.045, 0.220],
     'roughness': 0.35},
]
NOTES = ('Berries are listed as visible radii and converted to metaball field '
         'radii for threshold 0.6 / stiffness 2.0 (surface sits at ~0.575 of the '
         'field radius).  Seven berries in a 2-3-2 bunch, every neighbour about '
         '0.34 design units away; the bunch is almost square (0.98 x 0.96) so the '
         '6 cm target lands on the width.  Four small field balls at the row '
         'gaps act as short stems and keep the bunch one closed shell; they are '
         'field elements, not separate stem meshes.')

# visible berry radius and how much larger the metaball field radius must be
BERRY_R = 0.150
FIELD_SCALE = 1.0 / 0.575
RESOLUTION = 0.030
TRI_CAP = 288
# small field balls at the row gaps act as short stems; they add a neck without
# fattening every berry the way raising BERRY_R did
STEM_FIELD_R = 0.060

# (x, y, z) of the berry centres in design units; the cluster is 0.98 wide.
# 2 bottom, 3 middle, 2 top; the y offsets stagger the front and back berries.
CENTRES = [
    (-0.170, 0.000, 0.160),
    (0.170, 0.020, 0.160),
    (-0.340, 0.020, 0.500),
    (0.000, 0.000, 0.505),
    (0.340, -0.020, 0.500),
    (-0.170, 0.000, 0.820),
    (0.170, 0.020, 0.820),
]

# midpoints of the four cross-row gaps (left chain and right chain); the middle
# row already touches both columns, so these four bridges make one closed shell
STEMS = [
    (-0.255, 0.010, 0.330),
    (0.255, 0.000, 0.330),
    (-0.255, 0.010, 0.660),
    (0.255, 0.000, 0.660),
]


def build(b):
    field_r = BERRY_R * FIELD_SCALE
    spheres = [(c, field_r) for c in CENTRES]
    spheres += [(c, STEM_FIELD_R) for c in STEMS]
    bm = metaball_cluster(spheres, resolution=RESOLUTION, target_tris=TRI_CAP)
    b.add(bm, 'skin', (0.130, 0.045, 0.220), 0.35, smooth=True)
