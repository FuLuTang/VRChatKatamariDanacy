"""Paper doily - 120 mm lace mat: lobed disc, recessed inner ring, centre dimple.

Design three-axis 0.120 x 0.120 x 0.001 m, built at 1.6 mm thickness, the floor
for a flat paper prop.  A real doily has a hole in the middle; a through hole in a
1.6 mm sheet would need either a second shell or a boole (both fight the
triangle budget), so the centre is a blind dimple with a 0.5 mm floor left under
it - the contract explicitly allows the dent instead of the hole, and the shell
stays single.

The wavy edge is the lathe's `radius_fn`: four lobes on sixteen segments, so each
lobe is sampled at peak, mid, valley, mid and reads as a scallop instead of a
sawtooth.  The radius is pre-divided by the lobe amplitude so the peaks land on
exactly 0.060 m and finish() does not rescale the model.

Material upgrade (2026-11-15, MATERIAL_AND_DETAIL_SPEC.md 2 + 3, flat paper
class): three palette slots instead of one near-white one, and the print rings are
real 0.6 mm steps in the lathe profile rather than painted bands.
  * `paper`      - the outer lace field (linear 0.800 / 0.775 / 0.700, rough 0.90)
  * `paper_dark` - the 0.6 mm lower inner field, which reads as a printed ring
                   (0.360 / 0.320 / 0.250)
  * `ink`        - the centre dimple floor, the dark accent (0.030 / 0.035 /
                   0.045)
192 triangles: two 16-triangle pole fans plus five 32-triangle quad bands,
i.e. 1.5x the previous 128 and inside the 1.6x detail allowance.
"""

import math

from prop_lib import lathe

ID = 'paper_doily'
NAME_ZH = '纸垫'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：Ø120 mm 波浪边纸垫，凹印内圈 + 中心凹坑'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.120
TRI_BUDGET = 192
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.120', 'game_longest_dim_m': '0.120'}
MATERIALS = [
    {'key': 'paper', 'name_zh': '纸垫乳白', 'linear_rgb': [0.800, 0.775, 0.700],
     'roughness': 0.90},
    {'key': 'paper_dark', 'name_zh': '凹印内圈深纸色',
     'linear_rgb': [0.360, 0.320, 0.250], 'roughness': 0.92},
    {'key': 'ink', 'name_zh': '中心墨点', 'linear_rgb': [0.030, 0.035, 0.045],
     'roughness': 0.75},
]
NOTES = ('Thickness 2.0 mm instead of the catalogue 1.0 mm (flat-slab floor).  '
         '16 segments x 7 bands = 192 triangles.  Four-lobe rim wave, amplitude '
         '7% of the radius, base radius 0.05607 m so the peaks are exactly '
         '0.060 m and the doily is 0.120 x 0.120 m.  Detail: a 0.6 mm step at '
         'r = 0.0385 m separates the outer field (paper) from the inner field '
         '(paper_dark), and the centre dimple floor takes ink; the dimple is a '
         'blind dent, not a hole, so the shell stays closed.')

SEGMENTS = 16
LOBES = 4
AMP = 0.070
R_OUTER = 0.060 / (1.0 + AMP)       # lobe peaks land exactly on 0.060 m
FIELD_Z = 0.0020
INNER_R = 0.0385
INNER_Z = 0.0014
R_DIMPLE = 0.0105
R_DIMPLE_FLOOR = 0.0088
DIMPLE_Z = 0.0006
PAPER_RGB = (0.800, 0.775, 0.700)
PAPER_DARK_RGB = (0.360, 0.320, 0.250)
INK_RGB = (0.030, 0.035, 0.045)

PROFILE = [
    (0.0000, 0.0000),           # underside centre
    (R_OUTER, 0.0000),          # flat underside out to the rim
    (R_OUTER, FIELD_Z),         # rim wall
    (INNER_R, FIELD_Z),         # outer lace field
    (INNER_R, INNER_Z),         # 0.6 mm step down to the printed inner ring
    (R_DIMPLE, INNER_Z),        # inner field in to the dimple lip
    (R_DIMPLE_FLOOR, 0.0009),   # dimple wall down toward the floor
    (0.0000, DIMPLE_Z),         # dimple floor, centre pole
]


def _wave(point_index, segment_index, radius):
    """Lobe the rim (profile points 1-2) only; the rest stays round."""
    if point_index in (1, 2):
        a = math.tau * segment_index / SEGMENTS
        return radius * (1.0 + AMP * math.cos(LOBES * a))
    return radius


def _slot(centre):
    r = math.hypot(centre.x, centre.y)
    if centre.z > 1e-5 and r < R_DIMPLE + 1e-5:
        return 2
    if centre.z > 1e-5 and r < INNER_R:
        return 1
    return 0


def build(b):
    doily = b.add(lathe(PROFILE, segments=SEGMENTS, radius_fn=_wave), 'paper',
                  PAPER_RGB, 0.90, smooth=False, name='doily')
    me = doily.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    for poly in me.polygons:
        poly.material_index = _slot(poly.center)
