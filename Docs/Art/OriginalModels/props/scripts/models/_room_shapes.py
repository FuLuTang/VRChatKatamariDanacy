"""Shape helpers for the MessyRoom flat/long prop pilot (room props, batch B).

Not a prop module: it is only imported by the ``<id>.py`` models of this batch.
``prop_lib`` covers surfaces of revolution (lathe), circular sweeps (tube),
polygon prisms and single-sheet height fields; the room props additionally need

  * ``grid_slab``  - a rectangular height-field slab with a *flat fan* underside
                     (a mirrored grid would double the triangles), optionally
                     warped in plan (rounded corners, T-shirt silhouette, a
                     crimped crisp package outline);
  * ``stack``      - a closed solid through a list of (z, outline) rings, which
                     is how a book cover over a page block or a box with a lid
                     groove gets its steps without a single boolean;
  * ``annulus``    - a closed washer (a key ring, a raised collar band).

Every helper returns a watertight bmesh in metres; faces are not triangulated
here (``Builder.finish`` does that once at the end).
"""

import bmesh
import math


TAU = math.pi * 2.0


def clamp(x, lo=0.0, hi=1.0):
    return lo if x < lo else hi if x > hi else x


def smoothstep(t):
    t = clamp(t)
    return t * t * (3.0 - 2.0 * t)


def lerp(a, b, t):
    return a + (b - a) * t


def superellipse_radius(theta, a, b, p):
    """Radius of |x/a|^p + |y/b|^p = 1 in direction ``theta``.

    p = 2 is an ellipse, p ~ 5-6 is a rounded rectangle whose flat sides run
    through +/-a and +/-b, so the outline still touches the requested envelope.
    """
    c = abs(math.cos(theta))
    s = abs(math.sin(theta))
    return 1.0 / (((c / a) ** p + (s / b) ** p) ** (1.0 / p))


def squircle_warp(a, b, p):
    """Square -> superellipse plan warp for ``grid_slab`` / ``stack`` grids.

    ``u, v`` in [0, 1] map onto the square [-1, 1]^2; each point is pushed along
    its own ray to the same *fraction* of the superellipse boundary, so grid
    cells stay quasi-uniform and the outline is exactly the rounded rectangle
    of half-axes a, b.
    """
    def warp(u, v):
        sx = 2.0 * u - 1.0
        sy = 2.0 * v - 1.0
        m = max(abs(sx), abs(sy))
        if m < 1e-9:
            return 0.0, 0.0
        theta = math.atan2(sy, sx)
        return (m * superellipse_radius(theta, a, b, p) * math.cos(theta),
                m * superellipse_radius(theta, a, b, p) * math.sin(theta))
    return warp


def _fan(bm, verts, z, flip):
    """Close a ring of verts with a centroid fan at height z."""
    n = len(verts)
    cx = sum(v.co.x for v in verts) / n
    cy = sum(v.co.y for v in verts) / n
    c = bm.verts.new((cx, cy, z))
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((verts[j], verts[i], c) if flip
                     else (verts[i], verts[j], c))


def stack(rings, cap_bottom=True, cap_top=True):
    """Closed solid swept through ``rings`` = [(z, [(x, y), ...]), ...].

    All rings carry the same number of points in the same order (so a smaller
    page block under a bigger cover is the same polygon scaled).  Consecutive
    rings are joined by quads, so two rings at the same height but different
    size make a clean vertical lip; the two ends are closed by centroid fans.
    """
    if len(rings) < 2:
        raise ValueError('stack needs at least two rings')
    n = len(rings[0][1])
    bm = bmesh.new()
    vrings = []
    for z, pts in rings:
        if len(pts) != n:
            raise ValueError('stack rings need the same point count')
        vrings.append([bm.verts.new((x, y, z)) for (x, y) in pts])
    for k in range(len(vrings) - 1):
        lo, up = vrings[k], vrings[k + 1]
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((lo[i], lo[j], up[j], up[i]))
    if cap_bottom:
        _fan(bm, vrings[0], rings[0][0], True)
    if cap_top:
        _fan(bm, vrings[-1], rings[-1][0], False)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def annulus(cx, cy, z0, z1, r_out, r_in, segments=10):
    """Closed washer: a raised collar band or a key ring, with a real hole."""
    if not 0.0 < r_in < r_out:
        raise ValueError('annulus needs 0 < r_in < r_out')
    bm = bmesh.new()
    ob, ot, ib, it = [], [], [], []
    for i in range(segments):
        a = TAU * i / segments
        ca, sa = math.cos(a), math.sin(a)
        ob.append(bm.verts.new((cx + r_out * ca, cy + r_out * sa, z0)))
        ot.append(bm.verts.new((cx + r_out * ca, cy + r_out * sa, z1)))
        ib.append(bm.verts.new((cx + r_in * ca, cy + r_in * sa, z0)))
        it.append(bm.verts.new((cx + r_in * ca, cy + r_in * sa, z1)))
    for i in range(segments):
        j = (i + 1) % segments
        bm.faces.new((ob[i], ob[j], ot[j], ot[i]))    # outer wall
        bm.faces.new((ot[i], ot[j], it[j], it[i]))    # top band
        bm.faces.new((it[i], it[j], ib[j], ib[i]))    # inner wall
        bm.faces.new((ib[i], ib[j], ob[j], ob[i]))    # bottom band
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def grid_slab(nx, ny, x0, x1, y0, y1, top_fn, bottom_fn=None, bottom_z=0.0,
              warp=None):
    """Closed slab: sampled top grid, underside, and the rim between them.

    ``top_fn(x, y)`` is sampled on an (nx + 1) x (ny + 1) grid so bumps, recesses
    and pillow domes come straight out of the height field.  ``warp(u, v)``
    (u, v in [0, 1]) replaces the plain rectangle with any plan shape: a
    superellipse (squircle_warp) or a silhouette such as a T-shirt.

    Without ``bottom_fn`` the underside is one flat fan at ``bottom_z`` plus one
    ring of rim quads - a slab that rests on the table needs no mirrored grid,
    and the fan costs a third of what a second height field would.  With
    ``bottom_fn`` the underside is a real grid, which a lid that warps off the
    table needs.
    """
    if nx < 1 or ny < 1:
        raise ValueError('grid_slab needs at least a 1x1 grid')
    bm = bmesh.new()
    top = []
    bot = []
    for j in range(ny + 1):
        v = j / ny
        row_t, row_b = [], []
        for i in range(nx + 1):
            u = i / nx
            if warp is None:
                x = x0 + (x1 - x0) * u
                y = y0 + (y1 - y0) * v
            else:
                x, y = warp(u, v)
            row_t.append(bm.verts.new((x, y, top_fn(x, y))))
            if bottom_fn is not None:
                row_b.append(bm.verts.new((x, y, bottom_fn(x, y))))
        top.append(row_t)
        if bottom_fn is not None:
            bot.append(row_b)

    for j in range(ny):
        for i in range(nx):
            bm.faces.new((top[j][i], top[j][i + 1], top[j + 1][i + 1],
                          top[j + 1][i]))

    # perimeter in closed loop order, without repeating the corners
    loop = []
    for i in range(nx):
        loop.append((0, i))
    for j in range(ny):
        loop.append((j, nx))
    for i in range(nx, 0, -1):
        loop.append((ny, i))
    for j in range(ny, 0, -1):
        loop.append((j, 0))
    p = len(loop)

    if bottom_fn is None:
        under = []
        for (j, i) in loop:
            co = top[j][i].co
            under.append(bm.verts.new((co.x, co.y, bottom_z)))
        _fan(bm, under, bottom_z, True)
        for k in range(p):
            k2 = (k + 1) % p
            t0, t1 = top[loop[k][0]][loop[k][1]], top[loop[k2][0]][loop[k2][1]]
            bm.faces.new((t0, t1, under[k2], under[k]))
    else:
        for j in range(ny):
            for i in range(nx):
                bm.faces.new((bot[j][i], bot[j + 1][i], bot[j + 1][i + 1],
                              bot[j][i + 1]))
        for k in range(p):
            k2 = (k + 1) % p
            j0, i0 = loop[k]
            j1, i1 = loop[k2]
            bm.faces.new((top[j0][i0], top[j1][i1], bot[j1][i1], bot[j0][i0]))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def ribbon_slab(nx, stations, exponent=0.42, bottom_z=0.0):
    """Closed slab swept along a 2-D path: a flat sock, strap or tapered strap.

    ``stations`` = [(cx, cy, half_width, crown_height), ...] one per row, so the
    shape follows a bent centreline (a sock's leg into its foot) instead of an
    axis-aligned rectangle.  The across direction is the 2-D normal of the
    station tangent; the crown is a flattened power curve ``h(1 - u^2)^p``, which
    meets the flat underside at z = bottom_z along both edges - that is what
    makes a soft object read as lying on the floor rather than resting on a round
    belly.
    """
    n = len(stations)
    if n < 2 or nx < 1:
        raise ValueError('ribbon_slab needs nx >= 1 and two stations')
    bm = bmesh.new()
    rows = []
    for j, (cx, cy, hw, h) in enumerate(stations):
        i0 = max(0, j - 1)
        i1 = min(n - 1, j + 1)
        tx = stations[i1][0] - stations[i0][0]
        ty = stations[i1][1] - stations[i0][1]
        length = math.hypot(tx, ty) or 1.0
        ux, uy = -ty / length, tx / length
        row = []
        for i in range(nx + 1):
            u = 2.0 * i / nx - 1.0
            x = cx + ux * u * hw
            y = cy + uy * u * hw
            z = bottom_z + h * (max(0.0, 1.0 - u * u) ** exponent)
            row.append(bm.verts.new((x, y, z)))
        rows.append(row)

    for j in range(n - 1):
        for i in range(nx):
            bm.faces.new((rows[j][i], rows[j][i + 1], rows[j + 1][i + 1],
                          rows[j + 1][i]))

    loop = []
    for i in range(nx):
        loop.append((0, i))
    for j in range(n - 1):
        loop.append((j, nx))
    for i in range(nx, 0, -1):
        loop.append((n - 1, i))
    for j in range(n - 1, 0, -1):
        loop.append((j, 0))
    p = len(loop)
    under = []
    for (j, i) in loop:
        co = rows[j][i].co
        under.append(bm.verts.new((co.x, co.y, bottom_z)))
    _fan(bm, under, bottom_z, True)
    for k in range(p):
        k2 = (k + 1) % p
        t0, t1 = rows[loop[k][0]][loop[k][1]], rows[loop[k2][0]][loop[k2][1]]
        bm.faces.new((t0, t1, under[k2], under[k]))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def merge_bmeshes(parts):
    """Fuse several closed bmeshes into one (a multi-solid boolean tool).

    A boolean union applies one modifier per tool object, and each pass
    re-triangulates the growing result; feeding several solids in *one* tool
    costs a single pass.  Blender's Exact solver treats every closed shell of the
    tool as a volume, so the tool stays a valid union operand.
    """
    import bpy

    acc = bmesh.new()
    for bm in parts:
        me = bpy.data.meshes.new('merge_tmp')
        bm.to_mesh(me)
        bm.free()
        acc.from_mesh(me)
        bpy.data.meshes.remove(me)
    bmesh.ops.recalc_face_normals(acc, faces=acc.faces[:])
    return acc


def translate(bm, dx=0.0, dy=0.0, dz=0.0):
    bmesh.ops.translate(bm, vec=(dx, dy, dz), verts=bm.verts[:])
    return bm
