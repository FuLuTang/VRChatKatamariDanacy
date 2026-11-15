"""Cup - tapered 7.5 cm paper cup, 9 cm tall, hollow in one closed shell.

The plan's three axes are 0.075 x 0.075 x 0.09 m.  A lathe profile runs up the
outside of the cup, across the 2.2 mm rim and back down the inside to the floor,
so the drink cavity is real geometry rather than a painted disc and the mesh is
still a single watertight shell.  The 4 mm floor is thick enough for the inside
pole to sit clear of the outside bottom.

The first draft of the batch had to choose between a solid tumbler and a hollow
cup; hollow is what makes the silhouette read as a cup from any angle, and 20
segments keep the cone round at 280 triangles.
"""

from prop_lib import lathe

ID = 'cup'
NAME_ZH = '杯子'
CATEGORY = 'tableware'
CSV_REAL_LONGEST_M = 0.09
CSV_GAME_LONGEST_M = 0.26
SOURCING = ('model ourselves：Ø0.075 x 0.09 m 锥形纸杯，空心单壳'
            '（CSV 提示 Kenney cup.glb，本批自行建模）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.09
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'paper', 'name_zh': '纸杯米白', 'linear_rgb': [0.640, 0.600, 0.500],
     'roughness': 0.55},
]
NOTES = ('20 segments, 9 profile points: 280 triangles.  Outside 0.0750 m at the '
         'rim and 0.0536 m at the base (a 1:14 taper), 0.0900 m tall.  Wall '
         '2.2-2.4 mm, floor 4 mm; the rim is a 2.2 mm flat ring, so the inside '
         'is closed without a second object.  One shell, no caps buried inside.')

SEGMENTS = 20

PROFILE = [
    (0.0000, 0.0000),   # outside bottom centre
    (0.0250, 0.0000),   # flat bottom disc
    (0.0268, 0.0030),   # bottom fillet
    (0.0368, 0.0860),   # outside wall, just below the rim
    (0.0375, 0.0900),   # rim outside
    (0.0353, 0.0900),   # rim inside (2.2 mm wall)
    (0.0345, 0.0860),   # inside wall top
    (0.0248, 0.0060),   # inside wall meets the floor
    (0.0000, 0.0040),   # inside floor centre (4 mm base)
]


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'paper', (0.640, 0.600, 0.500), 0.55, smooth=False)
