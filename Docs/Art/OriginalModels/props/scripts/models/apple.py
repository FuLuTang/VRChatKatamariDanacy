"""Apple - squashed sphere with a real crown dimple and a stem in the crater.

The plan asks for an 8 cm sphere squashed to 0.9, a dent at the top and a 1.5 cm
stem.  The stem is not a second object: the profile runs up the body, down the
inside of the dimple and back up the axis as a narrow column, so the whole apple
including its stem is one closed surface of revolution.  Total height stays
under the 8 cm diameter, so the longest edge - the number the pickup gate
compares against - is the width, exactly as specified.

The first version was almost a sphere with a stub, which reads as a tomato.  The
fix is the crater: a 4.5 mm deep, 3.5 cm wide dimple whose rim is a real ring,
with the stem rising out of its floor.  The widest point also moved to 52 % of
the height so the top shoulder curves in to the dimple; that profile is what
makes the silhouette apple-like rather than spherical.
"""

from prop_lib import lathe

ID = 'apple'
NAME_ZH = '苹果'
CATEGORY = 'food'
CSV_REAL_LONGEST_M = 0.08
CSV_GAME_LONGEST_M = 0.30
SOURCING = 'model ourselves：Ø8 cm 球体压扁 0.9，顶部凹 + 1.5 cm 梗'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.08
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'skin', 'name_zh': '苹果红', 'linear_rgb': [0.400, 0.030, 0.025],
     'roughness': 0.35},
    {'key': 'stem', 'name_zh': '果梗褐', 'linear_rgb': [0.100, 0.050, 0.020],
     'roughness': 0.75},
]
NOTES = ('One lathe: body 0.0796 m wide and 0.0785 m tall, a 4.5 mm deep crown '
         'crater (rim r=17.5 mm, floor r=8.8 mm) and a stem column rising from '
         'the floor to 0.0785 m, capped flat.  The stem must stay below the '
         'diameter or finish() would shrink the body to fit 8 cm.  The brown '
         'material is the narrow column above the crater floor plus everything '
         'above the rim, so the crater wall stays red.')

SEGMENTS = 14
STEM_ABOVE_RIM = 0.0685
STEM_AXIS_RADIUS = 0.0075
DIMPLE_FLOOR = 0.0635

PROFILE = [
    (0.0000, 0.0000),   # bottom pole
    (0.0210, 0.0050),
    (0.0315, 0.0145),
    (0.0380, 0.0270),
    (0.0398, 0.0410),   # widest, 52 % of the height
    (0.0372, 0.0525),
    (0.0305, 0.0615),
    (0.0175, 0.0685),   # dimple rim
    (0.0088, 0.0640),   # dimple floor
    (0.0058, 0.0710),   # stem base
    (0.0044, 0.0785),   # stem tip, capped flat
]


def stem(centre):
    """Brown for the stem column and its cap, red for the crater wall."""
    if centre.z > STEM_ABOVE_RIM:
        return True
    return (centre.z > DIMPLE_FLOOR
            and centre.x * centre.x + centre.y * centre.y
            < STEM_AXIS_RADIUS * STEM_AXIS_RADIUS)


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'skin', (0.400, 0.030, 0.025), 0.35, smooth=True,
          extras=[('stem', (0.100, 0.050, 0.020), 0.75, stem)])
