"""Ceramic mug - 0.12 x 0.09 x 0.10 m mug: white body, red glaze rim, dark inside.

MessyRoom catalogue row ceramic_mug: 0.120 x 0.090 x 0.100 m, round,
tableware_food.  The X extent is the mug plus its handle on one side, so the
body is O 0.090 and the handle reaches x = 0.0752; the body is 0.100 tall.

The body is one lathe profile that climbs the outside from the flat base through
the wall, crosses the 4.4 mm rim and comes back down the inside to a 9 mm thick
floor, so the cavity is real geometry and the mesh is closed - the brief forbids
a zero-thickness open cup.  The rim is a flat annulus, which keeps the lip
readable at preview size and keeps the shell watertight.  Two extra profile
points give the glaze band an edge: the outer wall is split at z=0.0940, so the
top 6 mm can carry a different slot without painting the whole side.

Four material slots (hero piece, spec 1.1 asks for >= 3):

  ceramic        the body and the handle, white porcelain
  plastic_red    the 6 mm glaze band up the outside and across the 4.4 mm rim
                 annulus (worn as glaze, so roughness 0.40 not 0.48)
  ceramic_dark   the inside wall, the floor and the unglazed foot ring, so the
                 cavity reads dark from any angle instead of flushing white
  ink            the weld seam at the two handle roots

The seam is real geometry as well as a slot: the handle's first and last
stations are 4.5 mm-necked (against the 5.2 mm section), so the 7 mm of tube
outside the wall is a moulded collar, and those neck bands plus the wall faces
within 7.5 mm of the roots take the ink slot - that is the dark line where handle
meets wall.

The handle is a tube swept through eleven stations in the XZ plane, and both of
its end caps are buried *inside the 4.4-4.8 mm wall* at x = 0.0415: the cap disc
is perpendicular to a radial first segment, so its full 4.5 mm radius sits
between the inner (r=0.0402) and outer (r=0.0450) surfaces.  That is what keeps
the handle from poking into the cup cavity, and the Exact union welds it without
a seam.  The apex ring still reaches x = 0.0752, so the 0.1202 m pre-scale X
extent (and therefore the finished 0.120 m size) is unchanged.
"""

from math import hypot

from prop_lib import lathe, tube

ID = 'ceramic_mug'
NAME_ZH = '马克杯'
CATEGORY = 'tableware_food'
SOURCING = 'model ourselves：Ø0.09 x 0.10 m 陶瓷杯 + 焊入把手，空心带壁厚'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
# 300 in Docs/Design/Maps/MessyRoom/catalogue.csv x1.6 = 480, capped by the t1
# hard limit of 400 (MATERIAL_AND_DETAIL_SPEC.md 4).
TRI_BUDGET = 400

# MATERIAL_AND_DETAIL_SPEC.md 2, verbatim; plastic_red is worn as glaze, so its
# roughness is the spec's 陶瓷/釉 band (0.30-0.45) rather than the plastic 0.48.
PALETTE = {
    'ceramic': ((0.760, 0.745, 0.700), 0.35),
    'ceramic_dark': ((0.180, 0.230, 0.300), 0.38),
    'plastic_red': ((0.480, 0.075, 0.060), 0.40),
    'ink': ((0.030, 0.035, 0.045), 0.75),
}
MATERIALS = [
    {'key': 'ceramic', 'name_zh': '白瓷胎体',
     'linear_rgb': list(PALETTE['ceramic'][0]),
     'roughness': PALETTE['ceramic'][1]},
    {'key': 'ceramic_dark', 'name_zh': '深釉内壁与圈足',
     'linear_rgb': list(PALETTE['ceramic_dark'][0]),
     'roughness': PALETTE['ceramic_dark'][1]},
    {'key': 'plastic_red', 'name_zh': '红釉口沿带',
     'linear_rgb': list(PALETTE['plastic_red'][0]),
     'roughness': PALETTE['plastic_red'][1]},
    {'key': 'ink', 'name_zh': '把手暗缝',
     'linear_rgb': list(PALETTE['ink'][0]), 'roughness': PALETTE['ink'][1]},
]
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
NOTES = ('Body: 12 segments, 9 profile points = 168 triangles.  O 0.0900 outer, '
         '0.1000 tall; wall 4.4 mm at the rim, floor 9 mm, flat rim annulus, outer '
         'wall split at z=0.0940 for the glaze band.  Handle: 6-sided tube, 11 '
         'stations, 4.5 mm necked roots against a 5.2 mm section, 132 triangles, '
         'both caps buried in the wall at x=0.0415 between the inner (0.0402) and '
         'outer (0.0450) surfaces, so nothing enters the cavity; apex ring still '
         'reaches x=0.0752, keeping the pre-scale AABB at 0.1202 x 0.0900 x '
         '0.1000.  About 330 triangles after the Exact union.  Four slots by face '
         'position: red glaze above z=0.0935 on the outside plus the rim annulus, '
         'ceramic_dark inside (r<0.0435) and on the foot (z<0.0045), ink within '
         '7.5 mm of the two handle roots, ceramic elsewhere.  One closed shell.')

SEGMENTS = 12
BODY_R = 0.0450
BODY_TOP = 0.1000
GLAZE_Z = 0.0940
WALL_R = 0.0406
FLOOR_Z = 0.0090

PROFILE = [
    (0.0000, 0.0000),   # base centre
    (0.0395, 0.0000),   # flat base disc
    (0.0448, 0.0085),   # base into the outer wall
    (BODY_R, GLAZE_Z),  # outer wall, start of the glaze band
    (BODY_R, BODY_TOP),  # rim outside
    (WALL_R, BODY_TOP),  # rim inside (4.4 mm)
    (0.0400, 0.0135),   # inner wall bottom
    (0.0300, 0.0095),   # inner floor edge
    (0.0000, FLOOR_Z),  # inner floor centre
]

NECK_R = 0.0045
HANDLE_R = 0.0052
HANDLE_PATH = [
    (0.0415, 0.0, 0.0305),   # lower root, buried inside the wall
    (0.0520, 0.0, 0.0290),   # lower neck (visible 7 mm collar)
    (0.0575, 0.0, 0.0288),   # flare out to the full section
    (0.0620, 0.0, 0.0315),
    (0.0680, 0.0, 0.0400),
    (0.0700, 0.0, 0.0520),   # apex
    (0.0680, 0.0, 0.0640),
    (0.0620, 0.0, 0.0725),
    (0.0575, 0.0, 0.0752),
    (0.0520, 0.0, 0.0810),   # upper neck
    (0.0415, 0.0, 0.0800),   # upper root, buried inside the wall
]
HANDLE_RADII = [NECK_R, NECK_R, HANDLE_R, HANDLE_R, HANDLE_R, HANDLE_R,
                HANDLE_R, HANDLE_R, HANDLE_R, NECK_R, NECK_R]
ROOTS = ((0.0415, 0.0305), (0.0415, 0.0800))
SEAM_R = 0.0075


def _in_seam(c):
    """True for faces at (or on the wall around) either handle root."""
    for (rx, rz) in ROOTS:
        if hypot(c.x - rx, c.z - rz) < SEAM_R:
            return True
    return False


def build(b):
    ceramic, ceramic_rough = PALETTE['ceramic']
    body = b.add(lathe(PROFILE, segments=SEGMENTS), 'ceramic', ceramic,
                 ceramic_rough, smooth=True, name='body')
    handle = b.add(tube(HANDLE_PATH, HANDLE_RADII, segments=6), 'ceramic',
                   ceramic, ceramic_rough, smooth=False, name='handle')
    b.boolean_union(body, [handle])

    me = body.data
    me.materials.clear()
    for key in ('ceramic', 'ceramic_dark', 'plastic_red', 'ink'):
        rgb, rough = PALETTE[key]
        me.materials.append(b.material(key, rgb, rough))
    for poly in me.polygons:
        c = poly.center
        r = hypot(c.x, c.y)
        # Face centres are chord midpoints, so a lathe face on the 12-gon outer
        # wall sits at 0.0450*cos(15 deg) = 0.0435, not at 0.0450: the radii
        # below are the chord values, not the profile radii.
        if _in_seam(c):
            poly.material_index = 3          # ink: handle weld seam
        elif (c.z > GLAZE_Z - 0.0005 and r > 0.0430) \
                or (c.z > BODY_TOP - 0.0005 and r > 0.0398):
            poly.material_index = 2          # red glaze band + rim annulus
        elif r < 0.0420 or c.z < 0.0045:
            poly.material_index = 1          # dark inside + unglazed foot
        else:
            poly.material_index = 0
