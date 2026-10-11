"""Render the prepared review set only in isolated Blender:
--background --factory-startup --disable-autoexec.
Review renders and imported-mesh measurements are not gameplay/Unity acceptance.
"""
import hashlib
import json
import math
import sys
from pathlib import Path, PureWindowsPath

import bpy
from mathutils import Vector

# Refuse to reset or operate inside a user GUI session. These process flags must be
# supplied before Blender starts; seeing them only after '--' is not sufficient.
try:
    separator = sys.argv.index('--')
except ValueError as exc:
    raise RuntimeError('Expected -- source output arguments') from exc
startup_args = sys.argv[:separator]
if not bpy.app.background:
    raise RuntimeError('Refuse to render outside a background Blender process')
if '--factory-startup' not in startup_args or '--disable-autoexec' not in startup_args:
    raise RuntimeError('Required Blender flags: --background --factory-startup --disable-autoexec')
args = sys.argv[separator + 1:]
if len(args) != 2:
    raise RuntimeError('Expected staging input and new output directories')
source, output = [Path(p).resolve() for p in args]


def contains_unity_project_component(path):
    """Detect the protected Unity project by any path component, independent of script location."""
    components = set(Path(path).parts)
    components.update(PureWindowsPath(str(path)).parts)
    return any(part.casefold() == 'vrchatkatamaridanacyunity' for part in components)


# This direct component check works for Windows drive paths even when this script is
# deployed outside the repository. Run it before is_dir/exists, file reads or mkdir.
if any(contains_unity_project_component(path) for path in (source, output)):
    raise RuntimeError('Preview source/output must not touch the Unity project path')
if not source.is_dir() or output.exists():
    raise RuntimeError('Input missing or output exists; refuse overwrite')
repo_root = Path(__file__).resolve().parents[3]
unity_root = (repo_root / 'VRChatKatamariDanacyUnity').resolve()
for path in (source, output):
    try:
        path.relative_to(unity_root)
    except ValueError:
        continue
    raise RuntimeError('Preview source/output must remain outside the Unity project')
try:
    output.relative_to(source)
    raise RuntimeError('Render output must not be nested under staging input')
except ValueError:
    pass
try:
    source.relative_to(output)
    raise RuntimeError('Render output must not contain staging input')
except ValueError:
    pass

selection_path = source / 'selection.json'
selection = json.loads(selection_path.read_text(encoding='utf-8'))
if not isinstance(selection, dict) or not isinstance(selection.get('assets'), list) or len(selection['assets']) != 12:
    raise RuntimeError('Expected reviewed 12-asset selection')
expected = {
    ('food', n): 'food_' + n + '.glb'
    for n in ('apple', 'banana', 'strawberry', 'bread', 'croissant', 'cookie', 'cheese-cut', 'soda-can')
}
expected.update({('nature', n): 'nature_' + n + '.glb'
                  for n in ('pot_small', 'rock_smallA', 'mushroom_red', 'flower_redA')})
seen = set()
preflight = []
for asset in selection['assets']:
    if not isinstance(asset, dict):
        raise RuntimeError('Invalid selection record')
    key = (asset.get('group'), asset.get('name'))
    if key not in expected or key in seen or asset.get('filename') != expected[key]:
        raise RuntimeError('Selection does not match the fixed preview allowlist')
    seen.add(key)
    size, digest = asset.get('bytes'), asset.get('sha256')
    if isinstance(size, bool) or not isinstance(size, int) or not 20 <= size <= 1_000_000:
        raise RuntimeError('Invalid/oversized staged GLB length')
    path = (source / asset['filename']).resolve()
    if path.parent != source or path.suffix != '.glb' or path.stat().st_size != size:
        raise RuntimeError('Selection escaped staging or staged length mismatch')
    staged_bytes = path.read_bytes()
    if hashlib.sha256(staged_bytes).hexdigest() != digest:
        raise RuntimeError('Staged asset hash mismatch')
    preflight.append((asset, path, staged_bytes))
if seen != set(expected):
    raise RuntimeError('Selection is missing allowlisted preview members')
# Only create the fresh output directory after all inputs/hash checks have passed.
output.mkdir(parents=True)

MAX_IMPORT_OBJECTS = 100
MAX_MESHES = 64
MAX_VERTICES = 250_000
MAX_TRIANGLES = 250_000
MAX_MATERIAL_SLOTS = 64
MAX_TEXTURE_IMAGES = 4
MAX_TEXTURE_PIXELS = 16_777_216


def face(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()


def label(body, camera, center, up_offset):
    curve = bpy.data.curves.new('review_label', 'FONT')
    curve.body, curve.size, curve.align_x = body, 0.14, 'CENTER'
    curve.extrude = 0
    ob = bpy.data.objects.new('review_label', curve)
    bpy.context.collection.objects.link(ob)
    ob.rotation_euler = camera.rotation_euler.copy()
    up = camera.rotation_euler.to_matrix() @ Vector((0, 1, 0))
    forward = (camera.location - Vector(center)).normalized()
    ob.location = Vector(center) + up * up_offset + forward * 1.5
    material = bpy.data.materials.new('label_emission')
    material.use_nodes = True
    material.node_tree.nodes.clear()
    emit = material.node_tree.nodes.new('ShaderNodeEmission')
    emit.inputs['Color'].default_value = (0.07, 0.09, 0.12, 1)
    out = material.node_tree.nodes.new('ShaderNodeOutputMaterial')
    material.node_tree.links.new(emit.outputs[0], out.inputs['Surface'])
    curve.materials.append(material)


rows, rendered = [], []
for asset, path, staged_bytes in preflight:
    # Re-check immediately before importer open; a concurrent local file replacement
    # between this check and Blender's importer remains an OS-level TOCTOU limitation.
    if path.stat().st_size != len(staged_bytes) or hashlib.sha256(path.read_bytes()).digest() != hashlib.sha256(staged_bytes).digest():
        raise RuntimeError('Staged asset changed after preflight')
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(path))
    imported = list(bpy.context.scene.objects)
    meshes = [obj for obj in imported if obj.type == 'MESH']
    if not meshes or len(imported) > MAX_IMPORT_OBJECTS or len(meshes) > MAX_MESHES:
        raise RuntimeError('Missing mesh or imported object/mesh count limit')
    bpy.context.view_layer.update()
    coords = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    if not coords:
        raise RuntimeError('No imported bound-box vertices')
    lo = Vector(tuple(min(v[a] for v in coords) for a in range(3)))
    hi = Vector(tuple(max(v[a] for v in coords) for a in range(3)))
    dims = hi - lo
    if (any(not math.isfinite(v) for v in (*lo, *hi, *dims)) or
            any(v <= 0 for v in dims)):
        raise RuntimeError('Invalid/non-finite imported bounds')
    scale = 1.65 / max(dims)
    if not math.isfinite(scale) or scale <= 0:
        raise RuntimeError('Invalid display-only normalization scale')
    details, total_vertices, total_triangles, total_materials = [], 0, 0, 0
    for mesh in meshes:
        mesh.data.calc_loop_triangles()
        vertices, triangles, materials = len(mesh.data.vertices), len(mesh.data.loop_triangles), len(mesh.material_slots)
        total_vertices += vertices
        total_triangles += triangles
        total_materials += materials
        details.append({'name': mesh.name, 'vertices': vertices,
                        'triangles': triangles, 'materials': materials,
                        'matrix_world_before_preview': [list(row) for row in mesh.matrix_world],
                        'matrix_determinant': mesh.matrix_world.to_3x3().determinant(),
                        'uv_layers': len(mesh.data.uv_layers)})
    if (total_vertices > MAX_VERTICES or total_triangles > MAX_TRIANGLES or
            total_materials > MAX_MATERIAL_SLOTS):
        raise RuntimeError('Imported geometry/material budget exceeded')
    texture_images = [im for im in bpy.data.images if im.source != 'VIEWER']
    if len(texture_images) > MAX_TEXTURE_IMAGES:
        raise RuntimeError('Imported texture image count exceeded')
    total_pixels = 0
    for image in texture_images:
        width, height = image.size
        if width <= 0 or height <= 0 or width > 4096 or height > 4096:
            raise RuntimeError('Imported image dimensions exceeded')
        total_pixels += width * height
    if total_pixels > MAX_TEXTURE_PIXELS:
        raise RuntimeError('Imported total texture pixel budget exceeded')
    row = {'name': asset['name'], 'filename': asset['filename'], 'group': asset['group'],
           'imported_meshes': details, 'import_bounds_min': list(lo), 'import_bounds_max': list(hi),
           'import_extent_blender_units': list(dims),
           'texture_images': [{'name': im.name, 'dimensions': list(im.size)} for im in texture_images],
           'coordinate_status': 'OPEN: source pivot/axis semantics and Unity chain not verified',
           'units_note': 'Blender import units; not asserted real-world meters or pickup diameter'}
    # Keep imported matrices intact; the display root is review-only normalization.
    anchor = bpy.data.objects.new('DISPLAY_ONLY_normalization', None)
    bpy.context.collection.objects.link(anchor)
    for imported_root in [obj for obj in imported if obj.parent is None]:
        world = imported_root.matrix_world.copy()
        imported_root.parent = anchor
        imported_root.matrix_world = world
    center = (lo + hi) * 0.5
    anchor.scale = (scale,) * 3
    anchor.location = (-center.x * scale, -center.y * scale, -lo.z * scale)
    row['preview_uniform_scale'] = scale
    row['preview_root_matrix_formula'] = 'p_display = s*p_import + (-s*centerX,-s*centerY,-s*minZ)'
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.device, scene.cycles.samples = 'CPU', 16
    scene.cycles.use_denoising = False
    scene.render.threads_mode, scene.render.threads = 'FIXED', 4
    scene.render.resolution_x = scene.render.resolution_y = 384
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.view_settings.view_transform = 'Standard'
    scene.world = bpy.data.worlds.new('review_world')
    scene.world.use_nodes = True
    scene.world.node_tree.nodes.get('Background').inputs[0].default_value = (0.83, 0.86, 0.9, 1)
    scene.world.node_tree.nodes.get('Background').inputs[1].default_value = 0.7
    bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -0.012))
    floor = bpy.context.object
    material = bpy.data.materials.new('neutral_floor')
    material.diffuse_color = (0.84, 0.87, 0.9, 1)
    floor.data.materials.append(material)
    bpy.ops.object.light_add(type='AREA', location=(3, -4, 6))
    light = bpy.context.object
    light.data.energy, light.data.shape, light.data.size = 350, 'DISK', 5
    face(light, (0, 0, 0.5))
    bpy.ops.object.camera_add(location=(3, -5, 3.6))
    camera = bpy.context.object
    aim = (0, 0, 0.65)
    face(camera, aim)
    camera.data.type, camera.data.ortho_scale = 'ORTHO', 3.1
    scene.camera = camera
    label(asset['name'], camera, aim, -1.2)
    label(asset['group'].upper() + ' / ASSET REVIEW', camera, aim, 1.25)
    scene.render.filepath = str(output / (asset['group'] + '_' + asset['name'] + '.png'))
    bpy.ops.render.render(write_still=True)
    rendered.append(scene.render.filepath)
    rows.append(row)
    print('PREVIEW_DONE', asset['name'], total_triangles, flush=True)

# Composite existing renders into a deliverable contact sheet; no resampling.
bpy.ops.wm.read_factory_settings(use_empty=True)
width, height, size = 1536, 1152, 384
pixels = [1.0] * (width * height * 4)
for index, filename in enumerate(rendered):
    image = bpy.data.images.load(filename, check_existing=False)
    data = list(image.pixels[:])
    column, row = index % 4, 2 - index // 4
    for y in range(size):
        dest = ((row * size + y) * width + column * size) * 4
        offset = y * size * 4
        pixels[dest:dest + size * 4] = data[offset:offset + size * 4]
    bpy.data.images.remove(image)
sheet = bpy.data.images.new('asset_review_contact_sheet', width=width, height=height, alpha=True)
sheet.pixels.foreach_set(pixels)
sheet.filepath_raw = str(output / 'contact_sheet.png')
sheet.file_format = 'PNG'
sheet.save()
report = {'blender_version': bpy.app.version_string, 'render_engine': 'Cycles CPU 4 threads 16 samples',
          'purpose': 'Asset review; independently normalized. NOT GAMEPLAY. No Unity import.',
          'assets': rows, 'contact_sheet': 'contact_sheet.png'}
(output / 'render_report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print('PREVIEW_BATCH_COMPLETE', len(rows), flush=True)
