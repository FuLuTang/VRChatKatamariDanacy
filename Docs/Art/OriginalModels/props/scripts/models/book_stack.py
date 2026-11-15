"""Book stack - three hardbacks stacked flat, 26 x 19 x 9 cm, dead flat top.

PICNIC_CORNER_LAYOUT.md 4.1 lists struct_book_stack_01 as book_stack,
0.26 x 0.09 x 0.19 m (x x height x z), D_req 0.440 - the cheapest structural
piece in the level and the first "I ate the thing that blocked me"
(0.36 m can stand on it, 0.44 m eats it).  LEVEL_CONSTRUCTION.md 4 P2 carries
the same piece as 0.26 x 0.19 x 0.09 with a 300 tris budget.  prop_catalog.csv
has no row for the id, so the module declares DOC_ROW and build_all reads the
size from here.

Three hardbacks.  The bottom one is axis aligned and defines the 0.26 x 0.19
footprint; the middle one is turned +8 degrees and the top one -7 degrees, which
is what makes it read as a stack instead of a stepped box.  Their rotated
footprints stay inside the bottom book's, so the AABB is exactly the design
three-axis and finish()'s uniform normalisation does not rescale anything.

Every book is two prisms: a page block inset 4 mm on three sides and flush at
the spine, plus a full-footprint cover slab on top.  The cover overhangs the
pages by 4 mm and the page block's flush +x face takes the cover colour, so from
the 3/4 angle each book reads as "coloured board on top, pale pages under it,
coloured spine on the right" - the spine band is what separates a book from a
cream block with a coloured lid (the first pass had no spine at all and read as
cake slices; giving it its own prism instead of a colour band left three
boundary edges on the middle book).  The top cover's
upper plane is z = 0.090 across the full footprint, so the stack is a usable
0.09 m step and nothing rises above it.
"""

import math

from prop_lib import prism, rounded_rect

ID = 'book_stack'
NAME_ZH = '书堆'
CATEGORY = 'furniture'
SOURCING = 'model ourselves：PICNIC_CORNER_LAYOUT.md 4.1 / LEVEL_CONSTRUCTION.md 4 P2（真实三轴，无 CSV 行）'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.26
TRI_BUDGET = 300
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.26', 'game_longest_dim_m': '0.26'}
MATERIALS = [
    {'key': 'pages', 'name_zh': '书页米白', 'linear_rgb': [0.620, 0.575, 0.465],
     'roughness': 0.88},
    {'key': 'cover_a', 'name_zh': '封面砖红', 'linear_rgb': [0.175, 0.045, 0.035],
     'roughness': 0.72},
    {'key': 'cover_b', 'name_zh': '封面深蓝', 'linear_rgb': [0.040, 0.075, 0.130],
     'roughness': 0.72},
    {'key': 'cover_c', 'name_zh': '封面橄榄', 'linear_rgb': [0.105, 0.105, 0.045],
     'roughness': 0.72},
]
NOTES = ('Three hardbacks.  Bottom 0.260 x 0.190 x 0.032, axis aligned (it owns '
         'the AABB).  Middle 0.220 x 0.150 turned +8 deg at (0.004, 0.000), '
         'z 0.031-0.062 - 0.150 deep, not 0.160: at +8 deg a 0.160 deep book '
         'reaches y -0.0965 and pushes the AABB 0.001 past the design depth.  Top 0.240 x 0.150 turned -7 deg at (0.000, 0.003), '
         'z 0.061-0.090.  Each book: page block inset 4 mm from three sides and '
         'flush at the +x spine, plus a 5 mm full-footprint cover slab on top.  '
         'AABB 0.260 x 0.190 x 0.090, flat top at z = 0.090.  Materials by face '
         'centre: cover slabs (z >= book top - 5 mm), the +x spine face and '
         'everything outside the page footprint are the book colour; the other '
         'three page-block sides are pages.')

# (cover key, width, depth, rotation deg, cx, cy, z0, z1)
BOOKS = (
    ('cover_a', 0.260, 0.190, 0.0, 0.000, 0.000, 0.000, 0.032),
    ('cover_b', 0.220, 0.150, 8.0, 0.004, 0.000, 0.031, 0.062),
    ('cover_c', 0.240, 0.150, -7.0, 0.000, 0.003, 0.061, 0.090),
)
COVER_T = 0.005
PAGE_INSET = 0.004
CORNER_R = 0.004


def _profile(width, depth, rot_deg, cx, cy):
    """Rounded-rect book outline, turned by ``rot_deg`` and moved to (cx, cy)."""
    a = math.radians(rot_deg)
    ca, sa = math.cos(a), math.sin(a)
    return [(cx + x * ca - y * sa, cy + x * sa + y * ca)
            for (x, y) in rounded_rect(width, depth, CORNER_R,
                                       corner_segments=1)]


def _rect(x0, x1, y0, y1, rot_deg, cx, cy):
    """Axis-aligned rectangle in book-local space, turned and moved like above."""
    a = math.radians(rot_deg)
    ca, sa = math.cos(a), math.sin(a)
    out = []
    for (x, y) in ((x0, y0), (x1, y0), (x1, y1), (x0, y1)):
        out.append((cx + x * ca - y * sa, cy + x * sa + y * ca))
    return out


def _local(point, rot_deg, cx, cy):
    """World (x, y) -> book-local (x, y)."""
    a = math.radians(-rot_deg)
    dx, dy = point[0] - cx, point[1] - cy
    return (dx * math.cos(a) - dy * math.sin(a),
            dx * math.sin(a) + dy * math.cos(a))


def build(b):
    pages = MATERIALS[0]
    by_key = {mat['key']: mat for mat in MATERIALS}
    body = None
    books = []
    for index, (cover_key, width, depth, rot, cx, cy, z0, z1) in \
            enumerate(BOOKS):
        hx, hy = width * 0.5, depth * 0.5
        # Two prisms per book: an inset page block that is flush at the spine
        # edge, and the cover slab.  The first pass added a third prism for the
        # spine band; its square corner poked through the cover's rounded
        # corner and the union left three boundary edges on the middle book.
        # The spine is a colour band on the page block's +x face instead.
        page = b.add(
            prism(_rect(-hx + PAGE_INSET, hx, -hy + PAGE_INSET, hy - PAGE_INSET,
                        rot, cx, cy),
                  z1 - z0 - COVER_T, start=z0),
            'pages', pages['linear_rgb'], pages['roughness'], smooth=False,
            name='pages%d' % index)
        cover = b.add(
            prism(_profile(width, depth, rot, cx, cy), COVER_T,
                  start=z1 - COVER_T),
            cover_key, by_key[cover_key]['linear_rgb'],
            by_key[cover_key]['roughness'], smooth=False,
            name='cover%d' % index)
        b.boolean_union(page, [cover])
        if body is None:
            body = page
        else:
            b.boolean_union(body, [page])
        books.append({'key': cover_key, 'rot': rot, 'cx': cx, 'cy': cy,
                      'z0': z0, 'hx': hx, 'hy': hy, 'top': z1})

    me = body.data
    me.materials.clear()
    for mat in MATERIALS:
        me.materials.append(b.material(mat['key'], mat['linear_rgb'],
                                       mat['roughness']))
    slots = {mat['key']: i for i, mat in enumerate(MATERIALS)}
    for poly in me.polygons:
        c = poly.center
        # The books overlap by 1 mm, so pick the lowest book whose z span holds
        # the face; the face is cover-coloured when it sits in the cover slab,
        # outside the inset page block, or on the spine face at +x.
        book = books[-1]
        for candidate in books:
            if candidate['z0'] - 0.0006 <= c.z <= candidate['top'] + 0.0006:
                book = candidate
                break
        lx, ly = _local((c.x, c.y), book['rot'], book['cx'], book['cy'])
        on_cover = c.z >= book['top'] - COVER_T - 0.0006
        # Page sides sit exactly PAGE_INSET inside the footprint and the
        # cover rim exactly on it, so split at the midpoint - testing against
        # the page edge itself painted every page side in the cover colour and
        # the stack read as three solid blocks.
        outside_pages = (lx >= book['hx'] - PAGE_INSET * 0.5
                         or lx <= -(book['hx'] - PAGE_INSET * 0.5)
                         or abs(ly) >= book['hy'] - PAGE_INSET * 0.5)
        poly.material_index = slots[book['key']] if (on_cover or
                                                    outside_pages) else 0
    return b.finish(TARGET_LONGEST_M)
