"""Independent export check: re-import every FBX and GLB into a fresh scene.

Run inside headless Blender:
  blender --background --factory-startup --disable-autoexec \
      --python verify_import.py

Writes ../verification.json with the per-file vertex/triangle counts, world
dimensions, object transforms and check flags.
"""

import bpy
import json
import os
import sys

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import prop_lib as lib          # noqa: E402
import build_all                # noqa: E402

TOL = 5e-4


def scene_measure():
    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    others = [o.type for o in bpy.data.objects if o.type not in {'MESH'}]
    verts = sum(len(o.data.vertices) for o in meshes)
    tris = sum(sum(max(0, len(p.vertices) - 2) for p in o.data.polygons)
               for o in meshes)
    points = [o.matrix_world @ v.co for o in meshes for v in o.data.vertices]
    mn = [min(p[i] for p in points) for i in range(3)] if points else [0] * 3
    mx = [max(p[i] for p in points) for i in range(3)] if points else [0] * 3
    mats = sorted({m.name for o in meshes for m in o.data.materials if m})
    return {
        'mesh_objects': len(meshes),
        'other_object_types': others,
        'vertices': verts,
        'triangles': tris,
        'world_min_m': [round(v, 6) for v in mn],
        'world_max_m': [round(v, 6) for v in mx],
        'bbox_center_m': [round((mn[i] + mx[i]) / 2, 6) for i in range(3)],
        'dimensions_m': {
            'x': round(mx[0] - mn[0], 6),
            'y': round(mx[1] - mn[1], 6),
            'z': round(mx[2] - mn[2], 6),
            'longest': round(max(mx[i] - mn[i] for i in range(3)), 6),
        },
        'materials': mats,
        'transforms': [{
            'name': o.name,
            'location': [round(v, 6) for v in o.location],
            'rotation_euler_deg': [round(v * 57.29577951308232, 4)
                                   for v in o.rotation_euler],
            'scale': [round(v, 6) for v in o.scale],
        } for o in meshes],
    }


def check(pid, manifest, measured, kind):
    dims_ok = all(abs(measured['dimensions_m'][k] - manifest['dimensions_m'][k])
                  < TOL for k in ('x', 'y', 'z'))
    # Pivot contract (2026-11-15): every prop is placed at its AABB centre, so
    # the imported geometry's bounds centre must sit at the origin.  Check the
    # measured world bounds rather than the object origin: the FBX importer is
    # free to put the object's origin wherever it likes, but the scene uses the
    # geometry, and a base-pivot export would show up as a centre at half the
    # height.
    pivot = manifest['pivot']
    centre = measured['bbox_center_m']
    pivot_ok = all(abs(c) < TOL for c in centre)
    flags = {
        'triangles_match': measured['triangles'] == manifest['triangles'],
        'vertices_match': measured['vertices'] == manifest['vertices'],
        'dimensions_match': dims_ok,
        'pivot_ok': pivot_ok,
        'mesh_objects': measured['mesh_objects'],
        'no_extra_object_types': measured['other_object_types'] == [],
        'materials_imported': len(measured['materials']),
    }
    return {
        'format': kind,
        'file': '%s.%s' % (pid, kind),
        'file_bytes': os.path.getsize(os.path.join(
            lib.EXPORT_DIR, '%s.%s' % (pid, kind))),
        'expected': {
            'vertices': manifest['vertices'],
            'triangles': manifest['triangles'],
            'dimensions_m': manifest['dimensions_m'],
            'pivot': pivot,
            'bbox_center_m': [0.0, 0.0, 0.0],
        },
        'measured': measured,
        'checks': flags,
        'ok': (flags['triangles_match'] and flags['dimensions_match']
               and flags['pivot_ok'] and flags['mesh_objects'] == 1
               and flags['no_extra_object_types']),
    }


def main():
    # Read the same deliverables the build wrote: --out / OUT_DIR redirects both
    # the exports/previews tree and this report, so an out-of-repo build is
    # verified in place instead of checking the committed repo copies.
    root = build_all.configure_output()
    pids = build_all.selected_props()
    print('VERIFY_ROOT %s' % root, flush=True)
    report = {
        'note': 'FBX/GLB were re-imported into fresh factory-empty Blender '
                'scenes; counts are from the imported datablocks, not from the '
                'export manifest.',
        'tolerance_m': TOL,
        'blender_version': bpy.app.version_string,
        'output_root': root,
        'props': {},
    }
    all_ok = True
    for pid in pids:
        with open(os.path.join(lib.EXPORT_DIR, pid + '.manifest.json'),
                  encoding='utf-8') as fh:
            manifest = json.load(fh)
        entry = {}
        for kind, importer in (('fbx', bpy.ops.import_scene.fbx),
                               ('glb', bpy.ops.import_scene.gltf)):
            bpy.ops.wm.read_factory_settings(use_empty=True)
            importer(filepath=os.path.join(lib.EXPORT_DIR,
                                           '%s.%s' % (pid, kind)))
            measured = scene_measure()
            result = check(pid, manifest, measured, kind)
            entry[kind] = result
            all_ok = all_ok and result['ok']
            print('VERIFY %-13s %-3s tris=%-4d verts=%-4d dims=%s ok=%s' % (
                pid, kind, measured['triangles'], measured['vertices'],
                [round(measured['dimensions_m'][k], 4)
                 for k in ('x', 'y', 'z')], result['ok']), flush=True)
        report['props'][pid] = entry
    report['all_ok'] = all_ok
    with open(os.path.join(root, 'verification.json'), 'w',
              encoding='utf-8') as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print('VERIFY_ALL_OK' if all_ok else 'VERIFY_FAILED', flush=True)


if __name__ == '__main__':
    main()
