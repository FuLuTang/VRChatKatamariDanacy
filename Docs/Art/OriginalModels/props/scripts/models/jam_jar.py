"""Jam jar - 170 mm jar, 120 mm tall, open mouth with jam, metal collar.

The catalog asks for a body, a metal lid and a jam top that is visible through
the jar mouth.  All three are one lathe: the profile runs from the recessed
underside up the glass wall, over the shoulder into the metal collar, across a
flange that narrows the mouth to 94 mm, down the mouth well and across the jam
surface to the axis.  That single profile is how a bowl is built, so the jar is
one closed shell without a second object to intersect, and it is also why the
lid is modelled as a metal collar with an open flange: a sealing lid would hide
the jam completely, and the brief wants the jam visible from the mouth.

16 segments and 10 bands give 240 triangles, inside the 280 budget.

Material pass (2026-11-15 rework): the old three slots were a pale sage glass
(0.52/0.58/0.52), a mid-brown collar and a dark red jam - the first two were
within 0.1 of each other and nothing was below 0.12, so the jar read as a white
tub.  Four slots now: a green glass body, bright aluminium collar, a dark oxidised
mouth well (the inside of the mouth, which is where the shadow really falls) and
the jam, so the contents are unmistakably different from the body.  The glass is
green rather than blue-green on purpose: glass_blue 0.36 versus metal 0.47 differs
by only 0.11 in linear peak and 20 deg in hue, which the spec's contrast rule
scores as one slot; green 0.26 clears metal by 0.21 and the red jam by 0.22.
The collar also gained a 0.8 mm inward thread groove, which costs 24 triangles
and adds no width.
"""

from prop_lib import lathe

ID = 'jam_jar'
NAME_ZH = '果酱罐'
CATEGORY = 'food'
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.170', 'game_longest_dim_m': '0.170'}
SOURCING = 'model ourselves：Ø0.170 x 0.120 m 果酱罐，玻璃身 + 金属圈盖 + 罐口果酱面'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.170
TRI_BUDGET = 300
MATERIALS = [
    {'key': 'plastic_green', 'name_zh': '罐身绿玻璃', 'linear_rgb': [0.090, 0.260, 0.130],
     'roughness': 0.28},
    {'key': 'metal', 'name_zh': '金属圈盖', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'metal_dark', 'name_zh': '罐口内壁暗部', 'linear_rgb': [0.090, 0.095, 0.105],
     'roughness': 0.55},
    {'key': 'plastic_red', 'name_zh': '深红果酱', 'linear_rgb': [0.480, 0.075, 0.060],
     'roughness': 0.42},
]
NOTES = ('16 segments, 12 profile points, 11 bands = 352 triangles.  Diameter '
         '0.170 m at the body, height 0.120 m, mouth opening 0.094 m across.  The '
         'collar is the metal band at 105-119 mm with a 0.8 mm thread groove at '
         '110-115 mm, flaring 1 mm proud of the glass neck; the flange turns '
         'inward to r = 47 mm so the jam surface 20 mm below the rim stays '
         'visible.  Slots by face centre: metal above z 103.5 mm, the mouth well '
         '(43-56 mm radius, 101-118.5 mm) metal_dark, the jam surface (z < 100.5 mm '
         'inside r 48 mm) red, glass elsewhere.')

SEGMENTS = 16
JAM_TOP = 0.1005
JAM_RADIUS = 0.0480
METAL_FROM = 0.1035
WELL_RADIUS = 0.0430
WELL_OUTER = 0.0560
WELL_Z0 = 0.1005
WELL_Z1 = 0.1185

GLASS = (0.090, 0.260, 0.130)
METAL = (0.430, 0.450, 0.470)
METAL_DARK = (0.090, 0.095, 0.105)
JAM = (0.480, 0.075, 0.060)

PROFILE = [
    (0.0000, 0.0090),   # recessed underside centre
    (0.0850, 0.0000),   # bottom rim, ground contact
    (0.0850, 0.0850),   # glass body, widest (170 mm)
    (0.0660, 0.1020),   # shoulder into the neck
    (0.0700, 0.1050),   # metal collar, flared over the neck
    (0.0692, 0.1100),   # thread groove in
    (0.0700, 0.1150),   # thread groove out
    (0.0700, 0.1190),   # collar top
    (0.0470, 0.1200),   # flange turns inward: the 94 mm mouth
    (0.0470, 0.1000),   # mouth well down to the jam
    (0.0000, 0.0990),   # jam surface, slight dome on the axis
]


def jam(centre):
    """Strawberry surface inside the mouth."""
    return (centre.z < JAM_TOP
            and centre.x * centre.x + centre.y * centre.y < JAM_RADIUS ** 2)


def metal(centre):
    """Collar, flange and mouth well."""
    return centre.z > METAL_FROM


def well(centre):
    """The dark inside of the mouth, between the flange and the jam."""
    r2 = centre.x * centre.x + centre.y * centre.y
    return (WELL_Z0 < centre.z < WELL_Z1
            and WELL_RADIUS ** 2 < r2 < WELL_OUTER ** 2)


def build(b):
    bm = lathe(PROFILE, segments=SEGMENTS)
    b.add(bm, 'plastic_green', GLASS, 0.28, smooth=True,
          extras=[('metal', METAL, 0.32, metal),
                  ('metal_dark', METAL_DARK, 0.55, well),
                  ('plastic_red', JAM, 0.42, jam)])
