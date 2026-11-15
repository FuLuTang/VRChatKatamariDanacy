"""Picnic blanket - 5.00 x 4.50 m flat cloth, 12 mm thick, six woven stripes.

PROP_SET_PLAN.md section 3.6 (PicnicLawn) and the map's greybox: the blanket is
fixed terrain, not a pickup.  The design row only gives one length (12 m, the
large 6.5 x 12 variant) and the greybox small variant is 5.0 x 4.5 m, so the
small variant is what this models - the folder is the corner map, whose north
blanket is the big one and whose two picnic spots use the 5 x 4.5 one.

Thickness is the interesting number.  The greybox pads the blanket at 0.06 m
(``plane_raised``) and adds a 0.8 m apron at 7.5% around it; that pad is terrain.
The cloth itself has to be thin enough for the ball to roll onto it - the route
step limit is 0.05 m - so the cloth is 12 mm: a real thick wool picnic blanket
with a bound edge, whose top is one dead-flat plane at z = 0.012 so plates,
baskets and the ball sit on it without a dome.

The stripes are material slots on the one flat top, not a texture: six 0.833 m
bands alternating cream and brick red, matching the catalog's "平面 + 条纹材质".
The side rim carries the same band split, so the edge does not read as a single
cream slab.
"""

from prop_lib import sheet

ID = 'picnic_blanket'
NAME_ZH = '野餐毯'
CATEGORY = 'terrain'
CSV_REAL_LONGEST_M = 12.0
CSV_GAME_LONGEST_M = 12.0
SOURCING = ('model ourselves：平面 + 条纹材质；2 张 6.5×12 m + 2 张 5×4.5 m'
            '（本件为 5.0×4.5 m 小毯）')
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 5.0
TRI_BUDGET = 600
MATERIALS = [
    {'key': 'cream', 'name_zh': '毯面米白', 'linear_rgb': [0.620, 0.520, 0.355],
     'roughness': 0.92},
    {'key': 'stripe', 'name_zh': '毯面条纹砖红', 'linear_rgb': [0.355, 0.075, 0.055],
     'roughness': 0.92},
]
NOTES = ('Flat slab 5.00 x 4.50 x 0.012 m: a 12 x 6 top grid at z = 0.012, the '
         'same grid at z = 0 and a rim, 360 triangles.  Six 0.8333 m stripes by '
         'face-centre x, alternating cream and brick red, so the weave is colour '
         'only - no texture, UV or vertex colour.  The top plane is flat to the '
         'last vertex because the ball has to rest on it; the 0.06 m greybox pad '
         'and its 0.8 m / 7.5% apron stay terrain and are deliberately not part '
         'of the cloth.  Catalog real length is 12 m (the big variant), so the '
         'build warns once against that column - this is the 5.0 m map variant.')

NX, NY = 12, 6
HALF_X, HALF_Y = 2.5, 2.25
THICKNESS = 0.012
STRIPE_W = TARGET_LONGEST_M / 6.0


def build(b):
    bm = sheet(NX, NY, -HALF_X, HALF_X, -HALF_Y, HALF_Y,
               lambda x, y: THICKNESS, bottom_fn=lambda x, y: 0.0)

    def striped(centre):
        band = int((centre.x + HALF_X) / STRIPE_W)
        return band % 2 == 1

    b.add(bm, 'cream', MATERIALS[0]['linear_rgb'], MATERIALS[0]['roughness'],
          smooth=False,
          extras=[('stripe', MATERIALS[1]['linear_rgb'],
                   MATERIALS[1]['roughness'], striped)],
          name='cloth')
    return b.finish(TARGET_LONGEST_M)
