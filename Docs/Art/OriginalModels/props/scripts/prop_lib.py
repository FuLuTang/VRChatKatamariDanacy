"""Shared builder utilities for the original tiny-prop set.

Conventions (provisional until the scale/axis contract is frozen):
  * 1 Blender unit = 1 metre, Blender +Z up, the model front faces +Y.
  * Geometry is baked in metres: every object transform stays identity.
  * Only closed shells are built (no open borders), n-gons are triangulated
    before export, no textures / UVs / vertex colours - flat base colours only.
  * Deterministic: no unseeded randomness, no time- or path-dependent values.

The module is imported by build_all.py / build_prop.py inside headless Blender.
"""

import bpy
import bmesh
import hashlib
import json
import math
import os
from mathutils import Matrix, Vector

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SCRIPT_DIR)
EXPORT_DIR = os.path.join(ROOT, 'exports')
PREVIEW_DIR = os.path.join(ROOT, 'previews')

TAU = math.pi * 2.0


# ---------------------------------------------------------------------------
# scene / files
# ---------------------------------------------------------------------------

def ensure_dirs():
    for d in (EXPORT_DIR, PREVIEW_DIR):
        os.makedirs(d, exist_ok=True)


def reset_scene():
    """Factory-empty scene: no default cube/camera/light, no leftover data."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    # no .blend1 backups: the exports directory only holds deliverables
    bpy.context.preferences.filepaths.save_version = 0
    ensure_dirs()


# ---------------------------------------------------------------------------
# primitives (all return a new bmesh, closed unless stated)
# ---------------------------------------------------------------------------

def lathe(profile, segments=16, cap_start=True, cap_end=True, radius_fn=None):
    """Revolve a (radius, z) profile around +Z.

    ``profile`` runs bottom to top.  A point with radius 0 becomes a pole; any
    other end radius is closed with a flat fan when the matching cap flag is
    set.  ``radius_fn(point_index, segment_index, radius)`` can modulate the
    radius per profile point and per segment (used for bottle-cap flutes).
    """
    if len(profile) < 2:
        raise ValueError('lathe needs at least two profile points')
    bm = bmesh.new()
    rings = []
    for pi, (r, z) in enumerate(profile):
        if r <= 1e-9:
            rings.append([bm.verts.new((0.0, 0.0, z))])
            continue
        ring = []
        for i in range(segments):
            rr = r if radius_fn is None else radius_fn(pi, i, r)
            a = TAU * i / segments
            ring.append(bm.verts.new((rr * math.cos(a), rr * math.sin(a), z)))
        rings.append(ring)

    for k in range(len(rings) - 1):
        lower, upper = rings[k], rings[k + 1]
        if len(lower) == 1:
            for i in range(segments):
                bm.faces.new((lower[0], upper[i], upper[(i + 1) % segments]))
        elif len(upper) == 1:
            for i in range(segments):
                bm.faces.new((lower[i], lower[(i + 1) % segments], upper[0]))
        else:
            for i in range(segments):
                j = (i + 1) % segments
                bm.faces.new((lower[i], lower[j], upper[j], upper[i]))

    if len(rings[0]) > 1 and cap_start:
        c = bm.verts.new((0.0, 0.0, profile[0][1]))
        for i in range(segments):
            bm.faces.new((rings[0][(i + 1) % segments], rings[0][i], c))
    if len(rings[-1]) > 1 and cap_end:
        c = bm.verts.new((0.0, 0.0, profile[-1][1]))
        for i in range(segments):
            bm.faces.new((rings[-1][i], rings[-1][(i + 1) % segments], c))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _sphere_uv(segments, rings, radius):
    bm = bmesh.new()
    try:
        bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=rings,
                                 radius=radius)
    except TypeError:  # pre-3.0 argument name
        bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=rings,
                                 diameter=radius)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def _sphere_ico(subdivisions, radius):
    bm = bmesh.new()
    try:
        bmesh.ops.create_icosphere(bm, subdivisions=subdivisions, radius=radius)
    except TypeError:
        bmesh.ops.create_icosphere(bm, subdivisions=subdivisions,
                                   diameter=radius)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def uv_sphere(segments, rings, radius):
    return _sphere_uv(segments, rings, radius)


def icosphere(subdivisions, radius):
    return _sphere_ico(subdivisions, radius)


def tube(points, radii, segments=8, cap_start=True, cap_end=True):
    """Sweep a circular section along a polyline using parallel transport."""
    pts = [Vector(p) for p in points]
    if len(pts) < 2 or len(radii) != len(pts):
        raise ValueError('tube needs >=2 points and one radius per point')
    tangents = []
    for i in range(len(pts)):
        if i == 0:
            t = pts[1] - pts[0]
        elif i == len(pts) - 1:
            t = pts[-1] - pts[-2]
        else:
            t = pts[i + 1] - pts[i - 1]
        tangents.append(t.normalized())

    ref = Vector((0.0, 0.0, 1.0))
    if abs(tangents[0].dot(ref)) > 0.9:
        ref = Vector((0.0, 1.0, 0.0))
    normal = (ref - tangents[0] * ref.dot(tangents[0])).normalized()

    bm = bmesh.new()
    rings = []
    prev_t = None
    for i, (p, t, r) in enumerate(zip(pts, tangents, radii)):
        if prev_t is not None:
            axis = prev_t.cross(t)
            if axis.length > 1e-8:
                normal = (Matrix.Rotation(prev_t.angle(t), 3, axis.normalized())
                          @ normal)
            normal = (normal - t * normal.dot(t)).normalized()
        prev_t = t
        binormal = t.cross(normal).normalized()
        ring = []
        for j in range(segments):
            a = TAU * j / segments
            ring.append(bm.verts.new(p + normal * (math.cos(a) * r)
                                     + binormal * (math.sin(a) * r)))
        rings.append(ring)

    for k in range(len(rings) - 1):
        a, b = rings[k], rings[k + 1]
        for i in range(segments):
            j = (i + 1) % segments
            bm.faces.new((a[i], a[j], b[j], b[i]))

    if cap_start:
        c = bm.verts.new(pts[0] - tangents[0] * 0.0)
        for i in range(segments):
            bm.faces.new((rings[0][(i + 1) % segments], rings[0][i], c))
    if cap_end:
        c = bm.verts.new(pts[-1])
        for i in range(segments):
            bm.faces.new((rings[-1][i], rings[-1][(i + 1) % segments], c))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


def sheet(nx, ny, x0, x1, y0, y1, top_fn, bottom_fn=None, thickness=None):
    """Closed thin slab: top height field + bottom height field + rim.

    bottom_fn=None and thickness given -> bottom = top - thickness.
    bottom_fn given -> used directly (e.g. a flat z=0 underside).
    """
    if bottom_fn is None:
        if thickness is None:
            raise ValueError('sheet needs bottom_fn or thickness')
        bottom_fn = lambda x, y: top_fn(x, y) - thickness
    bm = bmesh.new()
    top, bottom = [], []
    for j in range(ny + 1):
        y = y0 + (y1 - y0) * j / ny
        row_t, row_b = [], []
        for i in range(nx + 1):
            x = x0 + (x1 - x0) * i / nx
            row_t.append(bm.verts.new((x, y, top_fn(x, y))))
            row_b.append(bm.verts.new((x, y, bottom_fn(x, y))))
        top.append(row_t)
        bottom.append(row_b)
    for j in range(ny):
        for i in range(nx):
            bm.faces.new((top[j][i], top[j][i + 1], top[j + 1][i + 1],
                          top[j + 1][i]))
            bm.faces.new((bottom[j][i], bottom[j + 1][i], bottom[j + 1][i + 1],
                          bottom[j][i + 1]))
    for i in range(nx):
        bm.faces.new((top[0][i], bottom[0][i], bottom[0][i + 1],
                      top[0][i + 1]))
        bm.faces.new((top[ny][i + 1], bottom[ny][i + 1], bottom[ny][i],
                      top[ny][i]))
    for j in range(ny):
        bm.faces.new((top[j][0], top[j + 1][0], bottom[j + 1][0],
                      bottom[j][0]))
        bm.faces.new((top[j][nx], bottom[j][nx], bottom[j + 1][nx],
                      top[j + 1][nx]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    return bm


# ---------------------------------------------------------------------------
# material / builder
# ---------------------------------------------------------------------------

def _make_material(name, rgb, roughness):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['Base Color'].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    bsdf.inputs['Roughness'].default_value = roughness
    bsdf.inputs['Metallic'].default_value = 0.0
    m.diffuse_color = (rgb[0], rgb[1], rgb[2], 1.0)
    return m


class Builder:
    """Accumulates mesh parts, joins them, triangulates and validates one prop."""

    def __init__(self, pid):
        self.pid = pid
        self.objs = []
        self._mats = {}
        self.object = None
        self.shells = 0

    def material(self, key, rgb, roughness=0.65):
        cache = (key, tuple(rgb), roughness)
        if cache not in self._mats:
            self._mats[cache] = _make_material(
                'Prop_%s_%s' % (self.pid, key), rgb, roughness)
        return self._mats[cache]

    def add(self, bm, key, rgb, roughness=0.65, smooth=True, name=None,
            extras=None):
        """Add a bmesh part.  ``extras`` = [(key, rgb, roughness, predicate)]
        assigns further material slots by face centre (local coords)."""
        me = bpy.data.meshes.new('%s_%s' % (self.pid, name or key))
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        bm.to_mesh(me)
        bm.free()
        for p in me.polygons:
            p.use_smooth = smooth
        ob = bpy.data.objects.new(me.name, me)
        bpy.context.collection.objects.link(ob)
        me.materials.append(self.material(key, rgb, roughness))
        if extras:
            for (ekey, ergb, erough, pred) in extras:
                me.materials.append(self.material(ekey, ergb, erough))
                idx = len(me.materials) - 1
                for p in me.polygons:
                    if pred(p.center.copy()):
                        p.material_index = idx
        self.objs.append(ob)
        return ob

    def boolean_union(self, target, tools):
        for tool in tools:
            mod = target.modifiers.new('union_' + tool.name, 'BOOLEAN')
            mod.operation = 'UNION'
            mod.solver = 'EXACT'
            mod.object = tool
            bpy.ops.object.select_all(action='DESELECT')
            target.select_set(True)
            bpy.context.view_layer.objects.active = target
            bpy.ops.object.modifier_apply(modifier=mod.name)
            if tool in self.objs:
                self.objs.remove(tool)
            bpy.data.objects.remove(tool, do_unlink=True)

    def finish(self, target_longest_m, pivot='base'):
        bpy.ops.object.select_all(action='DESELECT')
        for ob in self.objs:
            ob.select_set(True)
        bpy.context.view_layer.objects.active = self.objs[0]
        if len(self.objs) > 1:
            bpy.ops.object.join()
        ob = bpy.context.view_layer.objects.active
        ob.name = self.pid
        ob.data.name = self.pid + '_Mesh'
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        me = ob.data

        bm = bmesh.new()
        bm.from_mesh(me)
        # clean boolean seams: weld coincident verts and drop zero-area slivers
        bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-6)
        bmesh.ops.dissolve_degenerate(bm, dist=1e-6, edges=bm.edges[:])
        bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='FIXED',
                              ngon_method='BEAUTY')
        bmesh.ops.dissolve_degenerate(bm, dist=1e-5, edges=bm.edges[:])
        bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='FIXED',
                              ngon_method='BEAUTY')
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        bm.to_mesh(me)
        bm.free()

        mn, mx = bounds(me)
        longest = max(mx[i] - mn[i] for i in range(3))
        if longest <= 0:
            raise RuntimeError('degenerate mesh for ' + self.pid)
        s = target_longest_m / longest
        for v in me.vertices:
            v.co = v.co * s

        mn, mx = bounds(me)
        if pivot == 'center':
            c = (mn + mx) * 0.5
            for v in me.vertices:
                v.co = v.co - c
        else:
            for v in me.vertices:
                v.co.z -= mn.z
        me.validate(verbose=False)
        me.update()
        self.object = ob
        stats = self._stats(me)
        mn, mx = bounds(me)
        info = {
            'id': self.pid,
            'pivot': 'base' if pivot == 'base' else 'center',
            'target_longest_m': target_longest_m,
            'dimensions_m': {
                'x': mx.x - mn.x, 'y': mx.y - mn.y, 'z': mx.z - mn.z,
                'longest': max(mx.x - mn.x, mx.y - mn.y, mx.z - mn.z),
            },
            'bounds_m': {'min': list(mn), 'max': list(mx)},
            'vertices': stats['vertices'],
            'triangles': stats['triangles'],
            'materials': [m.name for m in me.materials if m],
            'material_base_colors': {
                m.name: [round(c, 4) for c in m.node_tree.nodes['Principled BSDF']
                         .inputs['Base Color'].default_value[:3]]
                for m in me.materials if m},
            'mesh_checks': {
                'non_manifold_edges': stats['non_manifold_edges'],
                'zero_area_faces': stats['zero_area_faces'],
                'loose_vertices': stats['loose_vertices'],
                'ngons': stats['ngons'],
                'duplicate_faces': stats['duplicate_faces'],
                'closed_shells': stats['shells'],
            },
            'geometry_sha256': stats['hash'],
            'transforms': 'identity (applied; geometry in metres)',
        }
        return info

    @staticmethod
    def _stats(me):
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.verts.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        nonman = sum(1 for e in bm.edges if not e.is_manifold)
        zero = sum(1 for f in bm.faces if f.calc_area() < 1e-12)
        loose = sum(1 for v in bm.verts if not v.link_faces)
        ngons = sum(1 for f in bm.faces if len(f.verts) != 3)
        keys = set()
        dup = 0
        for f in bm.faces:
            k = tuple(sorted(v.index for v in f.verts))
            if k in keys:
                dup += 1
            keys.add(k)
        seen = set()
        shells = 0
        for f in bm.faces:
            if f.index in seen:
                continue
            shells += 1
            stack = [f]
            seen.add(f.index)
            while stack:
                cur = stack.pop()
                for e in cur.edges:
                    for nf in e.link_faces:
                        if nf.index not in seen:
                            seen.add(nf.index)
                            stack.append(nf)
        bm.free()

        # Order-independent signature: bmesh op output order depends on
        # internal pointers (ASLR), the resulting shape does not.  Vertices and
        # triangle corner coordinates are hashed as sorted text.
        h = hashlib.sha256()
        vtext = sorted('v %.6f %.6f %.6f' % (v.co.x, v.co.y, v.co.z)
                       for v in me.vertices)
        ftext = []
        for p in me.polygons:
            corners = sorted('%.6f %.6f %.6f' % (me.vertices[i].co.x,
                                                 me.vertices[i].co.y,
                                                 me.vertices[i].co.z)
                             for i in p.vertices)
            ftext.append('f m%d %s' % (p.material_index, ' | '.join(corners)))
        for line in vtext + sorted(ftext):
            h.update((line + '\n').encode())
        return {
            'vertices': len(me.vertices),
            'triangles': sum(max(0, len(p.vertices) - 2) for p in me.polygons),
            'non_manifold_edges': nonman,
            'zero_area_faces': zero,
            'loose_vertices': loose,
            'ngons': ngons,
            'duplicate_faces': dup,
            'shells': shells,
            'hash': h.hexdigest(),
        }


def bounds(me):
    mn = Vector((1e18, 1e18, 1e18))
    mx = Vector((-1e18, -1e18, -1e18))
    for v in me.vertices:
        for i in range(3):
            mn[i] = min(mn[i], v.co[i])
            mx[i] = max(mx[i], v.co[i])
    return mn, mx


# ---------------------------------------------------------------------------
# export
# ---------------------------------------------------------------------------

def export_asset(ob, pid, info):
    ensure_dirs()
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    fbx = os.path.join(EXPORT_DIR, pid + '.fbx')
    glb = os.path.join(EXPORT_DIR, pid + '.glb')
    blend = os.path.join(EXPORT_DIR, pid + '.blend')

    bpy.ops.export_scene.fbx(
        filepath=fbx, use_selection=True,
        apply_scale_options='FBX_SCALE_UNITS', axis_forward='-Z', axis_up='Y',
        bake_space_transform=True, object_types={'MESH'},
        mesh_smooth_type='FACE', add_leaf_bones=False, use_mesh_modifiers=True)
    bpy.ops.export_scene.gltf(
        filepath=glb, export_format='GLB', use_selection=True,
        export_yup=True, export_apply=True, export_normals=True)
    bpy.ops.wm.save_as_mainfile(filepath=blend)

    info['files'] = {}
    for path in (fbx, glb, blend):
        info['files'][os.path.basename(path)] = os.path.getsize(path)
    info['export_settings'] = {
        'fbx': {'axis_forward': '-Z', 'axis_up': 'Y',
                'bake_space_transform': True,
                'apply_scale_options': 'FBX_SCALE_UNITS',
                'mesh_smooth_type': 'FACE'},
        'glb': {'format': 'GLB', 'export_yup': True, 'export_apply': True},
        'blender': bpy.app.version_string,
    }
    return info


# ---------------------------------------------------------------------------
# preview render
# ---------------------------------------------------------------------------

def _look_at(obj, target):
    d = Vector(target) - obj.location
    obj.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()


def render_preview(ob, path, res=512, samples=32):
    sc = bpy.context.scene
    for o in list(bpy.data.objects):
        if o.type in {'CAMERA', 'LIGHT'}:
            bpy.data.objects.remove(o, do_unlink=True)

    sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x = res
    sc.render.resolution_y = res
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'
    sc.render.image_settings.color_depth = '8'
    sc.render.image_settings.compression = 15
    sc.eevee.taa_render_samples = samples
    sc.eevee.use_gtao = True
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'

    world = bpy.data.worlds.new('PreviewWorld')
    world.use_nodes = True
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.34, 0.35, 0.38, 1.0)
    bg.inputs['Strength'].default_value = 0.55
    sc.world = world

    mn, mx = bounds(ob.data)
    center = (mn + mx) * 0.5
    span = max(mx[i] - mn[i] for i in range(3))

    cam_data = bpy.data.cameras.new('PropPreviewCam')
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = span * 1.30
    cam = bpy.data.objects.new('PropPreviewCam', cam_data)
    bpy.context.collection.objects.link(cam)
    direction = Vector((1.70, 1.15, 0.80)).normalized()
    cam.location = center + direction * (span * 3.0)
    _look_at(cam, center)
    sc.camera = cam

    for name, pos, energy in (
            ('Key', (1.6, 2.2, 2.6), 3.2),
            ('Fill', (-2.4, 1.0, 1.0), 1.3),
            ('Rim', (-0.6, -2.6, 1.8), 1.8)):
        ld = bpy.data.lights.new('PropPreview' + name, 'SUN')
        ld.energy = energy
        ld.angle = math.radians(12.0)
        lo = bpy.data.objects.new('PropPreview' + name, ld)
        bpy.context.collection.objects.link(lo)
        lo.location = pos
        _look_at(lo, (0.0, 0.0, 0.0))

    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)

    for o in (cam,):
        bpy.data.objects.remove(o, do_unlink=True)
    for o in list(bpy.data.objects):
        if o.type == 'LIGHT':
            bpy.data.objects.remove(o, do_unlink=True)
    return path


# ---------------------------------------------------------------------------
# contact sheet
# ---------------------------------------------------------------------------

def _text_layer(specs, width, height, path):
    """Render white text on transparent at 1 BU = 1 px (x/y from top-left)."""
    sc = bpy.context.scene
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)

    mat = bpy.data.materials.new('LabelEmission')
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    em = nt.nodes.new('ShaderNodeEmission')
    em.inputs['Color'].default_value = (1.0, 1.0, 1.0, 1.0)
    em.inputs['Strength'].default_value = 1.0
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])

    for (x_px, y_px, text, size) in specs:
        cu = bpy.data.curves.new('Label', type='FONT')
        cu.body = text
        cu.align_x = 'CENTER'
        cu.align_y = 'CENTER'
        cu.size = size
        cu.materials.append(mat)
        ob = bpy.data.objects.new('Label', cu)
        bpy.context.collection.objects.link(ob)
        ob.location = (x_px - width * 0.5, height * 0.5 - y_px, 0.0)

    cam_data = bpy.data.cameras.new('LabelCam')
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = float(width)
    cam = bpy.data.objects.new('LabelCam', cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = (0.0, 0.0, 5.0)
    sc.camera = cam

    world = bpy.data.worlds.new('LabelWorld')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.0
    sc.world = world

    sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x = width
    sc.render.resolution_y = height
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'
    sc.render.image_settings.color_depth = '8'
    sc.eevee.taa_render_samples = 8
    sc.view_settings.view_transform = 'Standard'
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)

    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    return path


def contact_sheet(entries, path, res=512, cols=4, gap=20, label_h=36,
                  margin=24, title_h=52):
    import numpy as np

    rows = (len(entries) + cols - 1) // cols
    width = 2 * margin + cols * res + (cols - 1) * gap
    height = 2 * margin + title_h + rows * (res + label_h) + \
        (rows - 1) * gap
    canvas = np.zeros((height, width, 4), dtype=np.float32)
    canvas[..., 0:3] = 0.021
    canvas[..., 3] = 1.0

    label_specs = []
    for k, entry in enumerate(entries):
        c = k % cols
        r = k // cols
        x0 = margin + c * (res + gap)
        y0 = margin + title_h + r * (res + label_h + gap)

        preview = entry['preview']
        if not os.path.isabs(preview):
            preview = os.path.join(ROOT, preview)
        img = bpy.data.images.load(preview, check_existing=False)
        w, h = img.size
        if (w, h) != (res, res):
            bpy.data.images.remove(img)
            raise RuntimeError('preview size mismatch for ' + entry['id'])
        buf = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(buf)
        tile = buf.reshape((h, w, 4))[::-1]
        bpy.data.images.remove(img)

        a = tile[..., 3:4]
        canvas[y0:y0 + res, x0:x0 + res, 0:3] = (
            tile[..., 0:3] * a + canvas[y0:y0 + res, x0:x0 + res, 0:3] * (1 - a))
        canvas[y0:y0 + res, x0:x0 + res, 3] = 1.0
        # thin tile border
        canvas[y0, x0:x0 + res, 0:3] = 0.06
        canvas[y0 + res - 1, x0:x0 + res, 0:3] = 0.06
        canvas[y0:y0 + res, x0, 0:3] = 0.06
        canvas[y0:y0 + res, x0 + res - 1, 0:3] = 0.06

        label_specs.append((x0 + res * 0.5, y0 + res + label_h * 0.5,
                            '%s   %.3f m   %d tris' % (
                                entry['id'], entry['dimensions_m']['longest'],
                                entry['triangles']), 22))

    label_specs.append((width * 0.5, margin + title_h * 0.5,
                        'Original placeholder props - Blender 1 BU = 1 m, '
                        '+Z up, front +Y - previews, no textures', 24))

    layer_path = os.path.join(PREVIEW_DIR, '_labels.png')
    _text_layer(label_specs, width, height, layer_path)
    img = bpy.data.images.load(layer_path, check_existing=False)
    buf = np.empty(width * height * 4, dtype=np.float32)
    img.pixels.foreach_get(buf)
    layer = buf.reshape((height, width, 4))[::-1]
    bpy.data.images.remove(img)

    a = layer[..., 3:4]
    canvas[..., 0:3] = layer[..., 0:3] * a + canvas[..., 0:3] * (1 - a)

    out = bpy.data.images.new('contact_sheet', width, height, alpha=True)
    out.pixels.foreach_set(np.ascontiguousarray(canvas[::-1]).ravel())
    out.filepath_raw = path
    out.file_format = 'PNG'
    out.save()
    bpy.data.images.remove(out)
    try:
        os.remove(layer_path)
    except OSError:
        pass
    return path
