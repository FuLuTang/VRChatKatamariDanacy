"""Snail shell - 60 mm globose shell: body whorl, spire, suture, dark mouth.

First attempt and why it failed: the shell was swept as one tapered tube along a
conical logarithmic spiral, and it previewed as a stack of plates.  A tube that
is 25 mm across only climbs 17 mm per turn, so consecutive whorls were separate
fat donuts lying on top of each other - real snail shells do not look like that
because each whorl is *glued* to the one below along its whole length, so the
outside is one continuous cone with a suture line pressed into it.  Getting that
from a tube sweep needs a twisting, swelling section far beyond 240 triangles.

Second attempt (this one) is a lathe again, and it also failed at first: one
smooth cone profile with a helical ridge previewed as a beehive, because a snail
shell's silhouette is not a cone - it is a *big round body whorl* with a short
spire stacked on top, and the whorls are visible as bulges with waists between
them.  The profile below therefore stacks three lobes (body whorl at r=30.5 mm,
second whorl at 25.5 mm, spire at 16.5 mm) separated by two waists, which is what
the outside of a real shell's section looks like.  The radius function then adds
the spiral suture (one bulge per ring, advancing 1.6 turns from the underside to
the apex) and the mouth on the +X side of the bottom rings - the asymmetry that
says "shell with an opening" rather than "spinning top".

Third rework (2026-11-15, materials): the shell was one flat tan with a brown
patch, and at 60 mm it read as a plastic blob.  It now carries the palette's
three slots - the tan ``shell`` body, a pale ``wool`` growth band that follows the
suture, and the deep ``wood_dark`` used twice: as the dark spiral line on the
suture itself and as the recessed mouth, which is the shell's interior read from
outside.  The mouth is a real notch: at rings 2-4 the column at 0 degrees is
pulled 34% in while its two neighbours are pushed 26% out, so the opening sits
inside a raised lip instead of on an even wall.  All of it is column
displacement on the existing 216 triangles - no boolean, no extra rings, so the
budget and the one-closed-shell contract are untouched.
"""

import math

from mathutils import Vector

from prop_lib import fit_bounds, lathe

ID = 'snail_shell'
NAME_ZH = '蜗牛壳'
CATEGORY = 'nature'
SOURCING = 'model ourselves：60 mm 蜗牛壳（螺塔 + 螺旋缝合线 + 壳口凹腔 + 浅色生长带）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.060
TRI_BUDGET = 260
# Not in Docs/Design/Maps/PicnicLawn/prop_catalog.csv - this id belongs to the
# DungBeetleMeadow theme, not the PicnicLawn map - so the module carries its
# own catalog row and build_all.py falls back to it.  Real size == built size:
# the dung-meadow props are modelled 1:1 and are never game-scaled.
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.060', 'game_longest_dim_m': '0.060'}
# Shared palette (MATERIAL_AND_DETAIL_SPEC.md section 2), copied verbatim.
PALETTE = {
    'shell': ((0.560, 0.470, 0.330), 0.42),
    'wool': ((0.700, 0.680, 0.630), 0.95),
    'wood_dark': ((0.135, 0.078, 0.038), 0.75),
}
SLOTS = ('shell', 'wool', 'wood_dark')
MATERIALS = [
    {'key': 'shell', 'name_zh': '壳体土黄', 'linear_rgb': [0.560, 0.470, 0.330],
     'roughness': 0.42},
    {'key': 'wool', 'name_zh': '壳面浅色生长带', 'linear_rgb': [0.700, 0.680, 0.630],
     'roughness': 0.95},
    {'key': 'wood_dark', 'name_zh': '缝合线暗带与壳口内腔', 'linear_rgb': [0.135, 0.078, 0.038],
     'roughness': 0.75},
]
NOTES = ('12-segment, 11-point lathe (216 triangles), one closed shell, no '
         'boolean and no self-intersection.  Body whorl is the widest ring at 60 '
         'mm; the top is cut into two terraces that step up to a 46 mm apex.  A '
         'shear above the body whorl (0.55 x height) makes the spire lean 12 mm '
         'off centre so the terraces read as a coil.  Suture = one angular bulge '
         'per ring advancing 1.6 turns bottom to top (amplitude 7.5%).  Mouth = at '
         'rings 2-4 the 0-degree column is pulled 34% in while its +-1 column '
         'neighbours are pushed 26% out, giving a recessed mouth inside a raised '
         'lip.  Materials by face centre before the shear: the mouth pocket '
         '(+15 degrees, z 10-30 mm) and the suture line (|delta| < 0.32 rad) are '
         'wood_dark, the band beside the suture (0.36-1.00 rad) is wool, the '
         'rest shell.  fit_bounds pins 60 x 55 x 50 mm.')

SIZE = (0.060, 0.055, 0.050)
SEGMENTS = 12
PROFILE = [
    (0.0000, 0.0000),   # underside centre
    (0.0200, 0.0000),   # flat underside
    (0.0295, 0.0070),   # body whorl flank
    (0.0300, 0.0190),   # widest ring: the body whorl
    (0.0250, 0.0300),   # body whorl shoulder
    (0.0215, 0.0335),   # terrace 1: flat annulus on top of the body whorl
    (0.0190, 0.0345),   # step wall up to the second whorl
    (0.0165, 0.0395),   # second whorl flank
    (0.0135, 0.0405),   # terrace 2: the second annulus
    (0.0080, 0.0445),   # third whorl
    (0.0000, 0.0460),   # apex pole
]
SUTURE_TURNS = 1.6
SUTURE_AMP = 0.075
SUTURE_WIDTH = 0.55
MOUTH_RINGS = (2, 3, 4)
MOUTH_DEPTH = 0.34
MOUTH_WIDTH = 0.36
LIP_AMP = 0.26
LIP_WIDTH = 0.40
LIP_OFFSET = 0.52          # the raised lip sits at +-30 degrees, either side
MOUTH_POCKET_ANGLE = 0.26  # dark faces within this angle of 0 degrees
MOUTH_POCKET_Z = (0.010, 0.030)
SUTURE_LINE_WIDTH = 0.32
BAND_INNER = 0.36
BAND_OUTER = 1.00
SPIRE_LEAN = 0.55
WHORL_Z = 0.0240
TAU = math.pi * 2.0
APEX_Z = 0.0460


def _wrap(angle):
    """Signed angular difference in (-pi, pi]."""
    while angle > math.pi:
        angle -= TAU
    while angle <= -math.pi:
        angle += TAU
    return angle


def _bulge(angle, centre, width, amplitude):
    delta = _wrap(angle - centre) / width
    return amplitude * math.exp(-delta * delta)


def _warp(point):
    """Slide the spire sideways so the whorls are eccentric, not concentric.

    A rotation would have been the obvious warp, but twisting a low-poly lathe
    makes every quad non-planar and the triangulation turns into slivers (tried:
    1.9 and 0.45 turns, both previewed as a crumpled pinecone).  A shear is a
    linear map, so planes stay planes and the quads stay clean.  The shear starts
    above the body whorl, so the wide lower whorl keeps its footprint and only
    the spire leans - which is what puts the apex off the body whorl's centre and
    makes the two terraces read as a coil instead of as stacked rings.
    """
    above = max(0.0, point.z - WHORL_Z)
    return Vector((point.x + SPIRE_LEAN * above, point.y, point.z))


def _ring_fraction(z):
    """Height -> fraction along the profile, so the suture phase follows rings.

    The suture bulge is keyed on the ring index, not on height, and the profile
    z values are not evenly spaced; interpolating them keeps the dark spiral line
    on the ridge the geometry actually built.
    """
    last = len(PROFILE) - 1
    for k in range(last):
        z0, z1 = PROFILE[k][1], PROFILE[k + 1][1]
        if z <= z1 or k == last - 1:
            if z1 - z0 <= 1e-9:
                return k / float(last - 1)
            # A face centre sits between two rings, i.e. half a ring along.
            return (k + 0.5) / float(last - 1)
    return 1.0


def build(b):
    last_ring = len(PROFILE) - 2

    def radius_fn(point, column, radius):
        if point in (0, len(PROFILE) - 1):
            return radius
        angle = TAU * column / SEGMENTS
        factor = 1.0 + _bulge(angle, TAU * SUTURE_TURNS * point / last_ring,
                              SUTURE_WIDTH, SUTURE_AMP)
        if point in MOUTH_RINGS:
            factor += max(_bulge(angle, LIP_OFFSET, LIP_WIDTH, LIP_AMP),
                          _bulge(angle, -LIP_OFFSET, LIP_WIDTH, LIP_AMP))
            factor -= _bulge(angle, 0.0, MOUTH_WIDTH, MOUTH_DEPTH)
        return radius * factor

    bm = lathe(PROFILE, segments=SEGMENTS, radius_fn=radius_fn)

    # Material split before the shear, so the section angle still matches the
    # bulge the radius function built.
    labels = []
    for face in bm.faces:
        c = face.calc_center_median()
        angle = math.atan2(c.y, c.x)
        fraction = _ring_fraction(c.z)
        phase = TAU * SUTURE_TURNS * fraction
        delta = abs(_wrap(angle - phase))
        in_mouth = (abs(_wrap(angle)) <= MOUTH_POCKET_ANGLE
                    and MOUTH_POCKET_Z[0] <= c.z <= MOUTH_POCKET_Z[1])
        if in_mouth or delta < SUTURE_LINE_WIDTH:
            labels.append(2)                     # dark mouth / dark suture line
        elif BAND_INNER <= delta < BAND_OUTER:
            labels.append(1)                     # pale growth band
        else:
            labels.append(0)

    for v in bm.verts:
        v.co = _warp(v.co)

    fit_bounds([bm], SIZE)
    rgb, rough = PALETTE['shell']
    me = b.add(bm, 'shell', rgb, rough, smooth=False, name='shell').data
    me.materials.clear()
    for key in SLOTS:
        me.materials.append(b.material(key, *PALETTE[key]))
    for index, poly in enumerate(me.polygons):
        poly.material_index = labels[index]
