"""Build the whole first batch of original tiny props.

Run inside headless Blender:
  blender --background --factory-startup --disable-autoexec --python build_all.py

For every prop in models.ORDER this builds the mesh from scratch, exports
FBX + GLB + .blend into ../exports, writes a per-prop manifest, renders a
transparent 512x512 preview into ../previews, then builds a contact sheet and
a top-level manifest.json / SHA256SUMS.
"""

import bpy
import csv
import hashlib
import json
import os
import sys
import time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import prop_lib as lib          # noqa: E402
import models as propmodels     # noqa: E402

CSV_PATH = os.path.normpath(os.path.join(
    lib.ROOT, '..', '..', '..', 'Design', 'Maps', 'PicnicLawn',
    'prop_catalog.csv'))


def catalog_rows():
    rows = {}
    if not os.path.exists(CSV_PATH):
        return rows
    with open(CSV_PATH, 'r', encoding='utf-8-sig', newline='') as fh:
        for row in csv.DictReader(fh):
            rows[row['id']] = row
    return rows


def build_one(pid, rows=None):
    if rows is None:
        rows = catalog_rows()
    lib.reset_scene()
    mod = propmodels.load(pid)
    b = lib.Builder(pid)
    mod.build(b)
    info = b.finish(mod.TARGET_LONGEST_M, mod.PIVOT)
    ob = b.object

    row = rows.get(pid, {})
    csv_game = None
    if row.get('game_longest_dim_m'):
        csv_game = float(row['game_longest_dim_m'])
    info.update({
        'name_zh': mod.NAME_ZH,
        'category': mod.CATEGORY,
        'catalog': {
            'source_csv': 'Docs/Design/Maps/PicnicLawn/prop_catalog.csv',
            'real_longest_dim_m': mod.CSV_REAL_LONGEST_M,
            'game_longest_dim_m': csv_game,
            'sourcing_hint': mod.SOURCING,
            'row_found': bool(row),
        },
        'dimension_check': {
            'target_m': mod.TARGET_LONGEST_M,
            'built_longest_m': info['dimensions_m']['longest'],
            'abs_error_m': abs(info['dimensions_m']['longest']
                               - mod.TARGET_LONGEST_M),
            'csv_game_longest_matches': (
                csv_game is not None
                and abs(csv_game - mod.TARGET_LONGEST_M) < 1e-9),
            'scale_vs_real_m': round(
                mod.TARGET_LONGEST_M / mod.CSV_REAL_LONGEST_M, 3),
        },
        'tri_budget': mod.TRI_BUDGET,
        'within_budget': info['triangles'] <= mod.TRI_BUDGET,
        'materials_declared': mod.MATERIALS,
        'notes': mod.NOTES,
        'build_script': 'models/%s.py' % pid,
        'preview': 'previews/%s_preview.png' % pid,
    })

    lib.export_asset(ob, pid, info)
    preview_path = os.path.join(lib.PREVIEW_DIR, pid + '_preview.png')
    lib.render_preview(ob, preview_path)
    info['preview_bytes'] = os.path.getsize(preview_path)

    with open(os.path.join(lib.EXPORT_DIR, pid + '.manifest.json'), 'w',
              encoding='utf-8') as fh:
        json.dump(info, fh, ensure_ascii=False, indent=2)
    print('BUILT %-13s tris=%-4d verts=%-4d longest=%.4f m shells=%d' % (
        pid, info['triangles'], info['vertices'],
        info['dimensions_m']['longest'],
        info['mesh_checks']['closed_shells']), flush=True)
    return info


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def write_sums():
    entries = []
    for base in ('exports', 'previews', 'scripts'):
        root = os.path.join(lib.ROOT, base)
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames.sort()
            for name in sorted(filenames):
                if (name.endswith('.pyc') or name.endswith('.blend1')
                        or name == '_labels.png'):
                    continue
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, lib.ROOT).replace(os.sep, '/')
                entries.append((sha256_file(full), rel))
    for extra in ('manifest.json', 'verification.json', 'README.md'):
        full = os.path.join(lib.ROOT, extra)
        if os.path.exists(full):
            entries.append((sha256_file(full), extra))
    entries.sort(key=lambda e: e[1])
    with open(os.path.join(lib.ROOT, 'SHA256SUMS'), 'w',
              encoding='utf-8', newline='\n') as fh:
        for digest, rel in entries:
            fh.write('%s  %s\n' % (digest, rel))
    return entries


def main():
    t0 = time.time()
    rows = catalog_rows()
    entries = []
    for pid in propmodels.ORDER:
        print('=== %s' % pid, flush=True)
        entries.append(build_one(pid, rows))

    contact = os.path.join(lib.PREVIEW_DIR, 'contact_sheet.png')
    lib.contact_sheet(entries, contact)
    print('CONTACT_SHEET %s %d bytes' % (contact,
                                         os.path.getsize(contact)), flush=True)

    doc = {
        'title': '原创占位小道具（第一批） / original placeholder tiny props',
        'status': '原创占位，非最终美术；未指定许可；尺度按 prop_catalog.csv 的 '
                  'game_longest_dim_m 暂定',
        'units': '1 Blender unit = 1 m',
        'axes': {
            'blender': '+Z up, model front faces +Y',
            'fbx_export': {'axis_forward': '-Z', 'axis_up': 'Y',
                           'bake_space_transform': True},
            'unity_intended': {'up': '+Y', 'front': '+Z',
                               'rotation': [0, 0, 0], 'scale': [1, 1, 1],
                               'import_verified': False},
            'glb_export': {'up': '+Y (export_yup=True)'},
        },
        'conventions': [
            'geometry baked in metres, object transforms identity',
            'closed shells only, triangulated, no n-gons',
            'flat Principled base colours only: no textures, UVs or vertex '
            'colours',
            'deterministic scripts: fixed formulas and fixed seeds',
        ],
        'catalog_source': 'Docs/Design/Maps/PicnicLawn/prop_catalog.csv',
        'build_script': 'scripts/build_all.py',
        'blender_version': bpy.app.version_string,
        'props': entries,
        'contact_sheet': 'previews/contact_sheet.png',
        'limitations': [
            '占位美术，非最终风格',
            '未在 Unity 中导入验证（本批次只做 Blender 端 FBX 回读验证）',
            'FBX 不携带材质贴图；Unity 端需按材质名重建 base colour',
            '无 LOD、无 UV、无碰撞体',
        ],
    }
    with open(os.path.join(lib.ROOT, 'manifest.json'), 'w',
              encoding='utf-8') as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=2)
    sums = write_sums()

    print('', flush=True)
    print('%-13s %6s %6s %6s %8s %8s %s' % (
        'id', 'tris', 'verts', 'budget', 'longest', 'csv', 'ok'), flush=True)
    for e in entries:
        print('%-13s %6d %6d %6d %8.4f %8s %s' % (
            e['id'], e['triangles'], e['vertices'], e['tri_budget'],
            e['dimensions_m']['longest'],
            e['catalog']['game_longest_dim_m'],
            'yes' if e['within_budget'] else 'NO'), flush=True)
    print('TOTAL tris=%d props=%d files=%d elapsed=%.1fs' % (
        sum(e['triangles'] for e in entries), len(entries), len(sums),
        time.time() - t0), flush=True)
    print('BUILD_ALL_OK', flush=True)


def run_single(pid):
    info = build_one(pid)
    print(json.dumps(info, ensure_ascii=False, indent=2))
    print('BUILD_PROP_OK', flush=True)


if __name__ == '__main__':
    main()
