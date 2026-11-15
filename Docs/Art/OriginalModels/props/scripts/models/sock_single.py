"""Single sock - one collapsed sock: rolled cuff, heel, bent foot, blind cuff.

MessyRoom catalogue t3 / long / 0.250 x 0.100 x 0.030 m; no catalog row, so the
module carries ``DOC_ROW``.

A sock that has been taken off is not a cylinder: it slumps.  The whole sock is a
*ribbon* - a height-field slab swept along the sock's own centreline, which runs
from the rolled cuff down the leg, round the heel and out along the foot to the
toe.  Because the section is a flattened power curve that meets a flat underside,
the sock lies on the floor along its whole length instead of resting on a round
belly; the crown is only 11-13 mm over a 58 mm width, so the section reads as
cloth, not as hose.

The details that carry the shape, all asked for in review:

* the cuff is a *roll* - two stations 9 mm apart, 4 mm wider and 9 mm taller than
  the leg - so the opening stands up like a turned-over hem;
* the heel is a bulge (the widest, tallest station of the foot) rather than a
  bend;
* the toe tapers to a 9 mm stub;
* the cuff opening is a blind 8 mm pocket boolean-subtracted from the cuff face,
  not a hole: a sock has an inside, but an open tube would be a second shell or a
  hole through the garment.  Leaving 1 mm of fabric above the pocket keeps it
  blind while still reading as an opening.

The sock is laid with the cuff towards +X, because the preview camera looks from
+X+Y: the opening is the one detail that has to face the viewer (the first draft
pointed it away and the pocket was invisible).

Three material slots (2026-11-15 material pass): ``fabric_grey`` body,
``cotton_red`` rolled cuff (warm, one step darker than the body - the ribbing
band is what makes the sock read as knitwear), ``ink`` as the inside of the cuff
pocket, i.e. the dark opening.  The pocket cutter carries the ink material and
the difference transfers it, so the inside is dark without a second shell.  Two
stations are also dropped 1.5 mm to add the knee/instep creases of worn cotton.

5 x 9 ribbon: 80 top + 26 underside fan + 52 rim = 184 triangles, plus the
pocket; inside the 220 budget.
"""

import math

import bmesh

from models._room_shapes import ribbon_slab
from prop_lib import prism

ID = 'sock_single'
NAME_ZH = '单只袜子'
CATEGORY = 'clothes_fabric'
SOURCING = 'model ourselves：塌陷袜子沿中心线扫掠 + 翻边袜口内凹腔，单壳'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.250
TRI_BUDGET = 220
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.250', 'game_longest_dim_m': '0.250'}
MATERIALS = [
    {'key': 'fabric_grey', 'name_zh': '袜子灰布', 'linear_rgb': [0.270, 0.255,
                                                              0.230],
     'roughness': 0.92},
    {'key': 'cotton_red', 'name_zh': '袜口红罗纹', 'linear_rgb': [0.420, 0.090,
                                                              0.080],
     'roughness': 0.94},
    {'key': 'ink', 'name_zh': '袜口内里', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.90},
]
NOTES = ('9-station ribbon, 5 across: rolled cuff facing +X (two stations 9 mm '
         'apart, half-width 0.031 and 24.5 mm high) over a 52 mm-wide leg, heel '
         'bulge at (0.024, 0.009, 20 mm high), then the foot widens to 62 mm and '
         'turns towards +Y, ending in a 32 mm toe stub at (-0.074, 0.050).  Cuff '
         'opening is '
         'a 42 x 19 x 8 mm blind pocket cut into the cuff face; its walls are '
         'the ink slot, the rolled cuff is cotton_red and the body fabric_grey.  '
         'Two stations are 1.5 mm lower than the first draft, which is the only '
         'change to the profile: two soft creases along the leg.')

# (centre x, centre y, half width, crown height) - one per grid row
STATIONS = [
    (0.118, 0.028, 0.0310, 0.0245),
    (0.109, 0.027, 0.0315, 0.0245),
    (0.096, 0.025, 0.0265, 0.0180),
    (0.055, 0.016, 0.0260, 0.0162),
    (0.024, 0.009, 0.0275, 0.0200),
    (-0.004, 0.018, 0.0315, 0.0180),
    (-0.034, 0.034, 0.0310, 0.0180),
    (-0.058, 0.045, 0.0285, 0.0150),
    (-0.074, 0.050, 0.0160, 0.0095),
]

# cuff opening: 42 x 18 mm ellipse, 8 mm deep into the cuff face
CUFF_CENTRE = (0.028, 0.0120)
CUFF_HW = 0.021
CUFF_HH = 0.0090


def cuff_cutter():
    pts = []
    for i in range(12):
        a = math.pi * 2.0 * i / 12
        pts.append((CUFF_HW * math.cos(a), CUFF_HH * math.sin(a)))
    bm = prism(pts, 0.016, axis='x', start=0.110)
    bmesh.ops.translate(bm, vec=(0.0, CUFF_CENTRE[0], CUFF_CENTRE[1]),
                        verts=bm.verts[:])
    return bm


def build(b):
    def is_cuff(centre):
        # the turned-over cuff only: a contrast heel patch mid-sock read as a
        # bandage in the preview
        return centre.x > 0.096

    sock = b.add(ribbon_slab(5, STATIONS), 'fabric_grey', (0.270, 0.255, 0.230),
                 0.92, smooth=False, name='sock',
                 extras=[('cotton_red', (0.420, 0.090, 0.080), 0.94, is_cuff)])

    cutter = b.add(cuff_cutter(), 'ink', (0.030, 0.035, 0.045), 0.90,
                   smooth=False, name='cuff_cutter')
    b.boolean_difference(sock, [cutter])
