"""Thermos - 30 cm vacuum flask: steel body, colour belt, cup lid, strap handle.

Reworked 2026-11-15 (fourth batch): the second-batch version read as a ketchup
bottle - red body, beige cap - so the three cues that make a flask legible were
strengthened while the size, the file name and the pivot stay identical.

  1. Cup lid, not screw cap.  The top is an inverted cup 70 mm tall: a flange at
     z=0.230 that steps out over the 0.040 m neck, then a straight wall from
     0.054 m radius to a flat top at 0.300.  The cup is 0.108 m across against
     the 0.096 m body, so the step is visible all the way round, and the flat top
     is what says "a cup sits on top" rather than "a domed cap".
  2. Vertical strap handle, clearly detached and a different colour.  The handle
     is a 19 mm wide, 7 mm thick strap (a flat elliptical sweep, not a wire)
     whose straight run is 72 mm long at 0.066 m from the axis; the finger gap between the body
     (r=0.048) and the strap's inner face is 14.5 mm, and the strap is dark
     charcoal, so it reads against the grey body instead of disappearing into it.
     The first draft's round 5.5 mm tube merged into the body in the preview, and
     a same-colour strap read as a moulding crease.

     The handle sits 40 degrees round from +X.  On +X the camera looks straight
     into the loop, the opening is filled by the body behind it, and the handle
     reads as a dark seam pasted on the shell (both first and second revisions
     did exactly that).  At -40 degrees the loop's plane is nearly parallel to the
     picture plane, so the 14.5 mm finger gap shows as background between the
     strap and the body's silhouette - the one cue that survives a flat render.
     The cost is 10 mm of footprint: the AABB goes from 0.121 x 0.106 m (handle on
     +X, second batch) to 0.111 x 0.108 m.  The body and cup diameters and the
     0.30 m height are unchanged, and the prop still fits the 0.12 m box.
  3. Steel body with a colour belt.  Grey stainless body, teal belt bulged 3 mm
     proud over z=0.130-0.180, so the two colours read from any angle without a
     texture.

The belt material is keyed on the z band plus a radius ceiling, not on "radius >
bulge": a 14-segment quad's centroid sits at cos(12.8 deg) = 0.975 of its own
radius, so a threshold just under the bulge radius misses every belt face and
paints the handle instead.  That was the first draft's bug: the belt came out
body-grey with a teal sliver on the handle.

The handle union adds no cap inside the body; the whole prop is one closed shell.
Measured X is 0.111 m (body plus handle on one side) and Y 0.108 m (cup
diameter), so the prop still fits the catalog's 0.12 x 0.12 m footprint, and the
height stays the 0.30 m longest edge.
"""

import math

from mathutils import Vector

from prop_lib import lathe, tube

ID = 'thermos'
NAME_ZH = '保温壶'
CATEGORY = 'container'
CSV_REAL_LONGEST_M = 0.30
CSV_GAME_LONGEST_M = 0.90
SOURCING = 'model ourselves：Ø0.10 x 0.30 m 旋转体 + 倒扣杯盖 + 竖向提手'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.30
TRI_BUDGET = 400
MATERIALS = [
    {'key': 'body', 'name_zh': '不锈钢灰', 'linear_rgb': [0.340, 0.360, 0.385],
     'roughness': 0.30},
    {'key': 'handle', 'name_zh': '提手炭黑', 'linear_rgb': [0.070, 0.075, 0.085],
     'roughness': 0.55},
    {'key': 'belt', 'name_zh': '腰带深青', 'linear_rgb': [0.020, 0.115, 0.105],
     'roughness': 0.45},
    {'key': 'cap', 'name_zh': '杯盖米白', 'linear_rgb': [0.640, 0.580, 0.440],
     'roughness': 0.50},
]
NOTES = ('14-segment body, 12 profile points, plus a 6-station, 5-segment strap '
         'handle; about 340 triangles before the exact union, one shell after '
         'it.  Body r=0.048 from the 0.010 base chamfer to 0.210, short shoulder '
         'to a 0.040 neck at 0.230; belt bulges to r=0.051 over z=0.130-0.180; '
         'cup flange at z=0.230 (r 0.040 -> 0.054), wall tapers to r=0.0525 at '
         '0.292, flat top closes at 0.300.  Handle centreline x=0.066, strap '
         '19 x 7 mm, entering at z=0.052 and 0.215, swung 40 degrees round from '
         '+X so the finger gap reads as background: 14.5 mm.  Four materials: '
         'steel body, charcoal handle, teal belt, cream cup.  Measured X 0.111 m '
         '(handle side), Y 0.108 m (cup diameter), Z 0.300 m - the footprint is '
         '10 mm narrower than the second-batch version because the handle no '
         'longer points at +X; body, cup and height are unchanged.')

PROFILE = [
    (0.0000, 0.0000),   # base centre
    (0.0400, 0.0000),   # base disc
    (0.0480, 0.0100),   # base chamfer
    (0.0480, 0.1300),   # steel body up to the belt
    (0.0510, 0.1360),   # belt bulges out
    (0.0510, 0.1740),   # belt
    (0.0480, 0.1800),   # belt back to the body
    (0.0480, 0.2100),   # shoulder starts
    (0.0400, 0.2300),   # neck
    (0.0540, 0.2300),   # cup flange: the cup mouth sits on the neck
    (0.0525, 0.2920),   # cup wall
    (0.0460, 0.3000),   # cup top edge
]
HANDLE_AZIMUTH = math.radians(-40.0)
HANDLE_RZ = [(0.0430, 0.0520), (0.0570, 0.0680), (0.0660, 0.1000),
             (0.0660, 0.1720), (0.0570, 0.2020), (0.0430, 0.2150)]
HANDLE_POINTS = [Vector((r * math.cos(HANDLE_AZIMUTH),
                         r * math.sin(HANDLE_AZIMUTH), z))
                 for r, z in HANDLE_RZ]
HANDLE_MATERIAL = 'handle'
HANDLE_HALF_THICK = 0.0035     # radial: how far the handle stands off
HANDLE_HALF_WIDTH = 0.0095     # tangential: the strap's 19 mm face
BELT_LOW = 0.1305
BELT_HIGH = 0.1795
BELT_MAX_RADIUS = 0.0560       # body faces only; the handle sits at x=0.066
HANDLE_MIN_RADIUS = 0.0560     # outside the belt bulge (0.0497) and the cup (0.0527)
CAP_ABOVE = 0.2295


def _strap_section(index, angle, radius):
    a = HANDLE_HALF_THICK
    b = HANDLE_HALF_WIDTH
    c, s = math.cos(angle), math.sin(angle)
    return a * b / math.sqrt((b * c) ** 2 + (a * s) ** 2)


def build(b):
    body = b.add(lathe(PROFILE, segments=14), 'body',
                 (0.340, 0.360, 0.385), 0.30, smooth=True, name='body')
    handle = b.add(tube(HANDLE_POINTS, [1.0] * len(HANDLE_POINTS),
                        segments=5, section_fn=_strap_section),
                   HANDLE_MATERIAL, (0.070, 0.075, 0.085), 0.55, smooth=True,
                   name='handle')
    b.boolean_union(body, [handle])

    # Re-assign materials after the union rather than trusting the modifier to
    # transfer them: the handle must stay body-coloured, the belt is a z band on
    # the bulged ring, and everything above the flange is the cup lid.
    me = body.data
    me.materials.clear()
    me.materials.append(b.material('body', (0.340, 0.360, 0.385), 0.30))
    me.materials.append(b.material('handle', (0.070, 0.075, 0.085), 0.55))
    me.materials.append(b.material('belt', (0.020, 0.115, 0.105), 0.45))
    me.materials.append(b.material('cap', (0.640, 0.580, 0.440), 0.50))
    for poly in me.polygons:
        c = poly.center
        r = math.hypot(c.x, c.y)
        if c.z > CAP_ABOVE:
            poly.material_index = 3
        elif r > HANDLE_MIN_RADIUS:
            poly.material_index = 1
        elif BELT_LOW < c.z < BELT_HIGH and r < BELT_MAX_RADIUS:
            poly.material_index = 2
        else:
            poly.material_index = 0
