"""Bottle opener - a flat 13 cm opener: handle, then the open head ring.

Contract: 0.130 x 0.035 x 0.008 m (real size), metal, <= 180 triangles.

The whole opener is ONE extruded polygon, which is why it has no boolean seam and
one shell by construction.  The outline is traced as the union of a tapered
handle and a C: the head is an annulus (outer r 17.5 mm, bore r 10.5 mm) with a
64 deg wedge cut out at the front, and the handle's two straight sides run into
the ring's outer circle at the tangent x.  The 3.2 mm wall between the bore and
the outside is what hooks a crown cap, and because the C is a solid slab the
"open ring" is a closed solid (genus 0), never a single-layer sheet.

The slab is 8 mm thick in Z, the row's thickness: this opener is a chunky
stamped-blank shape rather than a 2 mm bar blade, and the thickness is what makes
the slot a real through-slot instead of a painted line.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3): the single
bright steel slot grew a dark one and a stamped grip recess.
  * `metal`      - the body (linear 0.430 / 0.450 / 0.470, rough 0.32)
  * `metal_dark` - the bore wall (the inner jaw that bites the cap) and the grip
                   recess in the handle (0.090 / 0.095 / 0.105, rough 0.55)
The grip recess is real geometry: a 36 x 8 mm rounded tool is subtracted 5.5 mm
into the 8 mm handle, leaving a 2.5 mm floor.  It stays inside the handle
outline, so the AABB is unchanged.

X_BACK is derived from the head's front extent so the built length is exactly
0.130 m: the C's outermost point is at cos(32 deg) of the ring, not at the full
radius, so it sits at x = 57.34 mm and the butt is placed to make up the 130 mm.
"""

import math

from prop_lib import prism, rounded_rect

ID = 'bottle_opener'
NAME_ZH = '开瓶器'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：0.130 m 扁平开瓶器，开口环 + 压印握位'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.130
TRI_BUDGET = 288
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.130', 'game_longest_dim_m': '0.130'}
MATERIALS = [
    {'key': 'metal', 'name_zh': '拉丝钢', 'linear_rgb': [0.430, 0.450, 0.470],
     'roughness': 0.32},
    {'key': 'metal_dark', 'name_zh': '内孔与握位暗钢',
     'linear_rgb': [0.090, 0.095, 0.105], 'roughness': 0.55},
]
NOTES = ('One 32-point polygon (handle + C-ring with a 64 deg front opening), '
         'extruded 0.008 m in Z with prism.  Ring outer r 0.0175, bore r 0.0105, '
         'wall 7 mm; front gap 18.5 mm at the outer radius.  Handle half-width '
         '6.2 mm, butt at x = -0.07266 so the built X is exactly 0.130 m '
         '(the C front reaches 0.05734, not 0.060).  Detail: a 0.036 x 0.008 m '
         'rounded tool is subtracted 5.5 mm into the handle (the grip recess); '
         'the recess and the bore wall take metal_dark.  AABB 0.130 x 0.035 x '
         '0.008 m, one closed shell.')

RING_CX = 0.0425
RING_CY = 0.0
OUTER_R = 0.0175
BORE_R = 0.0105
GAP_HALF_DEG = 32.0
HANDLE_HALF = 0.0062
THICK = 0.008
# Explicit sampling angles instead of a uniform step: 90 and 270 have to be
# sampled exactly, otherwise the C's widest points fall between samples and the
# built Y comes out 0.5% under the row's 35 mm.
BOTTOM_ARC = (200.745, 225.0, 250.0, 270.0, 290.0, 310.0, 328.0)
TOP_ARC = (32.0, 50.0, 70.0, 90.0, 110.0, 130.0, 145.0, 159.255)
BORE_ARC = (328.0, 300.0, 270.0, 240.0, 210.0, 180.0, 150.0, 120.0, 90.0,
            60.0, 32.0)
GRIP_W, GRIP_D, GRIP_R = 0.036, 0.008, 0.002
GRIP_CX = -0.050
GRIP_FLOOR = 0.0025
METAL_RGB = (0.430, 0.450, 0.470)
METAL_DARK_RGB = (0.090, 0.095, 0.105)


def _arc(radius, angles):
    return [(RING_CX + radius * math.cos(math.radians(a)),
             RING_CY + radius * math.sin(math.radians(a))) for a in angles]


def _outline():
    # The handle sides meet the ring where |y| = HANDLE_HALF, i.e. at
    # asin(0.0062 / 0.0175) = 20.745 deg from the -X axis: 200.745 and 159.255,
    # the first and last angle of the two outer arcs below.
    x_back = RING_CX + OUTER_R * math.cos(math.radians(GAP_HALF_DEG)) - 0.130
    pts = [(x_back, -HANDLE_HALF)]
    # Bottom side of the handle, then around the outside of the C to the lower
    # edge of the front opening.
    pts += _arc(OUTER_R, BOTTOM_ARC)
    # Gap face, bore (traversed back the other way), gap face.
    pts += _arc(BORE_R, BORE_ARC)
    # Outside of the C up to the top tangent point, then the handle's top side.
    pts += _arc(OUTER_R, TOP_ARC)
    pts.append((x_back, HANDLE_HALF))
    return pts


def _slot(centre):
    c = centre
    # Bore wall: the only side faces (centre z = THICK/2) at the bore radius.
    if abs(c.z - THICK * 0.5) < 1e-6:
        d = math.hypot(c.x - RING_CX, c.y - RING_CY)
        if BORE_R * 0.94 < d < BORE_R * 1.02:
            return 1
    if (c.z > GRIP_FLOOR - 1e-4
            and GRIP_CX - GRIP_W * 0.5 - 1e-4 < c.x < GRIP_CX + GRIP_W * 0.5 + 1e-4
            and abs(c.y) < GRIP_D * 0.5 + 1e-4):
        return 1
    return 0


def build(b):
    opener = b.add(prism(_outline(), THICK, axis='z', start=0.0),
                   'metal', METAL_RGB, 0.32, smooth=False, name='opener')
    grip = b.add(prism(rounded_rect(GRIP_W, GRIP_D, GRIP_R, 1),
                       THICK, axis='z', start=GRIP_FLOOR),
                 'metal_dark', METAL_DARK_RGB, 0.55, smooth=False, name='grip')
    for v in grip.data.vertices:
        v.co.x += GRIP_CX
    b.boolean_difference(opener, [grip])

    me = opener.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
