"""Pizza box - flat square box with a moulding seam and one corner lifting.

MessyRoom catalogue t4 / flat / 0.350 x 0.350 x 0.045 m; no catalog row, so the
module carries ``DOC_ROW``.

The whole box is a single ``stack`` of six rings, so it is one closed shell by
construction - no base/lid boolean, and no pair of plates with a shadow line
between them.  The lid seam is a real 1.5 mm groove cut into the wall at 24.5 mm,
which is 54% of the 45 mm height: two rings at the same height but 1.5 mm apart
make the recess, and the two rings above it close it again.  The groove band
carries the dark ``ink`` material, so the seam reads as a printed moulding line
rather than relying on ambient occlusion alone.

The lifted corner is the reason the rings are not enough: every vertex above the
groove is raised by up to 6 mm towards +X+Y, which *opens the groove* into a
6 mm gap at that corner while the base below it is untouched.  The lid therefore
stands clear of the base at one corner - a warped lid, not a floating plate - and
the box still sits flat on the other three.

Four material slots (2026-11-15 material pass), one shell:

* ``paper`` - the lid's top face: the one large light surface you look at;
* ``paper_dark`` - the box wall, top and bottom (so the dark board frames the
  white lid face), plus the seam faces *inside the lifted corner* (the raw board
  you see when the lid lifts);
* ``ink`` - the moulding seam itself, all the way round;
* ``cotton_red`` - a 90 x 12 x 0.4 mm label plate unioned onto the front wall,
  i.e. a sticker, not a printed texture.

12-point outline with a 28 mm corner radius (rounded corners), 5 ring
transitions: 120 + 24 cap triangles = 144, inside the 260 budget.
"""

import bmesh

from models._room_shapes import clamp, smoothstep, stack
from prop_lib import prism, rounded_rect

ID = 'pizza_box'
NAME_ZH = '披萨盒'
CATEGORY = 'packaging'
SOURCING = 'model ourselves：扁盒 + 盒盖分模槽 + 一角掀起，单壳阶梯实体'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.350
TRI_BUDGET = 260
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.350', 'game_longest_dim_m': '0.350'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '盒盖纸面', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'paper_dark', 'name_zh': '盒身纸板芯', 'linear_rgb': [0.360, 0.320,
                                                             0.250],
     'roughness': 0.92},
    {'key': 'ink', 'name_zh': '分模槽墨线', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.78},
    {'key': 'cotton_red', 'name_zh': '红色标签片', 'linear_rgb': [0.420, 0.090,
                                                             0.080],
     'roughness': 0.90},
]
NOTES = ('One 6-ring stack: 12-point outline (0.350 m square, R 28 mm '
         'corners), 1.5 mm-deep moulding groove at z 0.0245-0.0260 painted ink, '
         'lid top at 0.039 m.  Vertices above the groove rise up to 6 mm towards '
         '+X+Y, so the groove opens into a 6 mm gap at one corner and the lid '
         'corner stands clear of the base.  Slots: paper lid, paper_dark base + '
         'raw seam inside the lifted corner, ink seam, cotton_red label plate '
         'on the +Y wall (0.090 x 0.013 m, 0.5 mm proud, unioned in).  Total '
         '0.350 x 0.350 x 0.045 m.')

HALF = 0.175
GROOVE_Z0 = 0.0245
GROOVE_Z1 = 0.0260
LID_TOP = 0.0390
CORNER_LIFT = 0.0060
GROOVE_INSET = 0.9914


def lift(x, y):
    u = clamp((x / HALF + 1.0) * 0.5)
    v = clamp((y / HALF + 1.0) * 0.5)
    return CORNER_LIFT * smoothstep((u - 0.45) / 0.55) * smoothstep(
        (v - 0.45) / 0.55)


def is_lid(centre):
    """The lid's top face only: the box wall stays the darker board colour."""
    return centre.z > 0.0345


def is_seam(centre):
    """The moulding seam band, including the part the corner lift opens up."""
    return GROOVE_Z0 + 0.0002 < centre.z < 0.0300


def is_open_seam(centre):
    """Seam faces inside the lifted corner: raw board, not the printed line."""
    return is_seam(centre) and centre.x > 0.045 and centre.y > 0.045


def is_label(centre):
    return centre.y > 0.1746 and abs(centre.x) < 0.047 and \
        -0.0010 < centre.z < 0.0190


def build(b):
    outer = rounded_rect(0.350, 0.350, 0.028, corner_segments=2)
    groove = [(x * GROOVE_INSET, y * GROOVE_INSET) for (x, y) in outer]
    bm = stack([
        (0.0, outer),
        (GROOVE_Z0, outer),
        (GROOVE_Z0, groove),
        (GROOVE_Z1, groove),
        (GROOVE_Z1, outer),
        (LID_TOP, outer),
    ])
    for v in bm.verts:
        if v.co.z > GROOVE_Z1 - 0.0002:
            v.co.z += lift(v.co.x, v.co.y)

    box = b.add(bm, 'paper_dark', (0.360, 0.320, 0.250), 0.92, smooth=False,
                name='box',
                extras=[('paper', (0.800, 0.775, 0.700), 0.90, is_lid),
                        ('ink', (0.030, 0.035, 0.045), 0.78, is_seam),
                        ('paper_dark', (0.360, 0.320, 0.250), 0.92,
                         is_open_seam)])

    label = prism([(-0.045, 0.1748), (0.045, 0.1748), (0.045, 0.1755),
                   (-0.045, 0.1755)], 0.013, axis='z', start=0.0055)
    label_ob = b.add(label, 'cotton_red', (0.420, 0.090, 0.080), 0.90,
                     smooth=False, name='label')
    b.boolean_union(box, [label_ob])
