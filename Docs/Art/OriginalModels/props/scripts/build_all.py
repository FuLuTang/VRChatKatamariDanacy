"""Build the whole first batch of original tiny props.

Run inside headless Blender (``--python-exit-code`` is what turns a raised error
into a non-zero process exit):

  blender --background --factory-startup --disable-autoexec --python-exit-code 2 \\
      --python build_all.py

For every prop in models.ORDER this builds the mesh from scratch, exports
FBX + GLB + .blend into <output root>/exports, writes a per-prop manifest,
renders a transparent 512x512 preview into <output root>/previews, then builds a
contact sheet and a top-level manifest.json / SHA256SUMS.

The output root defaults to this props directory - the committed deliverables.
``--out DIR`` (after Blender's ``--`` separator) or the OUT_DIR environment
variable, the same knob generate.py uses, redirects every deliverable, so a
rebuild can run into a fresh directory outside the repository without
overwriting the committed exports/, previews/ or SHA256SUMS:

  blender ... --python build_all.py -- --out <fresh dir outside the repo>
  OUT_DIR=<fresh dir outside the repo> blender ... --python build_all.py

A missing Docs/Design/Maps/PicnicLawn/prop_catalog.csv is a hard error, not a
warning: the catalog carries each prop's CSV columns, and building without it
would emit manifests that look complete while recording nothing.
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

# Repo-relative on purpose: this is an input, not a deliverable, so it stays
# anchored at lib.ROOT even when the output root is redirected.
CSV_PATH = os.path.normpath(os.path.join(
    lib.ROOT, '..', '..', '..', 'Design', 'Maps', 'PicnicLawn',
    'prop_catalog.csv'))


class BuildInputError(RuntimeError):
    """A required input is missing or inconsistent; the build must stop."""


def catalog_rows():
    """Load the map prop catalog keyed by prop id.  Missing input is fatal."""
    if not os.path.isfile(CSV_PATH):
        raise BuildInputError(
            'prop catalog not found: %s\n'
            'build_all.py cross-checks every prop against the map catalog, so it '
            'refuses to build without it. Run it from a full repository checkout, '
            'or restore the file.' % CSV_PATH)
    with open(CSV_PATH, 'r', encoding='utf-8-sig', newline='') as fh:
        reader = csv.DictReader(fh)
        if not reader.fieldnames or 'id' not in reader.fieldnames:
            raise BuildInputError(
                'prop catalog %s has no "id" column' % CSV_PATH)
        rows = {}
        for row in reader:
            pid = (row.get('id') or '').strip()
            if pid:
                rows[pid] = row
    if not rows:
        raise BuildInputError('prop catalog %s lists no props' % CSV_PATH)
    return rows


def requested_output(argv=None):
    """Deliverable root asked for by ``--out DIR`` or ``OUT_DIR``; else None.

    Blender passes its own arguments, so only what follows ``--`` is read.
    ``--out DIR`` / ``--out=DIR`` wins over the OUT_DIR environment variable
    (the name generate.py already uses); None keeps the historical default.
    """
    return (lib.cli_option('--out', None, argv)
            or os.environ.get('OUT_DIR') or None)


def selected_props(argv=None):
    """Prop ids to build, in registry order: ``--props a,b`` filters ORDER.

    Without the flag the whole registry is built, which is the historical
    behaviour.  A requested id that is not registered is a hard error, so a
    typo cannot silently produce an empty batch.
    """
    wanted = lib.cli_option('--props', None, argv)
    if not wanted:
        return list(propmodels.ORDER)
    ids = [p.strip() for p in wanted.split(',') if p.strip()]
    if not ids:
        raise BuildInputError('--props lists no prop ids')
    unknown = [p for p in ids if p not in propmodels.ORDER]
    if unknown:
        raise BuildInputError(
            'unknown prop(s) in --props: %s (known: %s)'
            % (', '.join(unknown), ', '.join(propmodels.ORDER)))
    return [p for p in propmodels.ORDER if p in ids]


def configure_output(argv=None):
    """Apply the requested output root and return it; no request means the repo.

    Always writes the resolved root back through prop_lib, so the default is a
    real reset rather than "whatever the last call left behind".
    """
    lib.set_output_root(requested_output(argv) or lib.ROOT)
    return lib.OUTPUT_ROOT


def csv_size(row, column, pid):
    """Numeric value of one catalog size column; missing data is fatal."""
    text = (row.get(column) or '').strip()
    if not text:
        raise BuildInputError(
            'prop %r has no %s in %s' % (pid, column, CSV_PATH))
    try:
        return float(text)
    except ValueError:
        raise BuildInputError(
            'prop %r has a non-numeric %s %r in %s'
            % (pid, column, text, CSV_PATH))


def build_one(pid, rows=None):
    if rows is None:
        rows = catalog_rows()
    row = rows.get(pid)
    if row is None:
        raise BuildInputError(
            'prop %r is listed in models.ORDER but not in %s (catalog ids: %s); '
            'add the row or drop the prop from models.ORDER'
            % (pid, CSV_PATH, ', '.join(sorted(rows))))
    csv_real = csv_size(row, 'real_longest_dim_m', pid)
    csv_game = csv_size(row, 'game_longest_dim_m', pid)
    mod = propmodels.load(pid)
    # Which catalog column is the size contract for this prop: the 2026-11-15
    # plan moved the prop set to real-world sizes, so a model may declare
    # SIZE_REFERENCE = 'real'.  Models that keep the older game-scale contract
    # (the first batch) leave it unset and are still checked against
    # game_longest_dim_m, so their manifests do not change.
    size_reference = getattr(mod, 'SIZE_REFERENCE', 'game')
    csv_column = ('real_longest_dim_m' if size_reference == 'real'
                  else 'game_longest_dim_m')
    csv_target = csv_real if size_reference == 'real' else csv_game
    lib.reset_scene()
    b = lib.Builder(pid)
    mod.build(b)
    info = b.finish(mod.TARGET_LONGEST_M)
    ob = b.object

    info.update({
        'name_zh': mod.NAME_ZH,
        'category': mod.CATEGORY,
        'catalog': {
            'source_csv': 'Docs/Design/Maps/PicnicLawn/prop_catalog.csv',
            'real_longest_dim_m': csv_real,
            'game_longest_dim_m': csv_game,
            'sourcing_hint': mod.SOURCING,
            'row_found': bool(row),
        },
        'dimension_check': {
            'size_reference': size_reference,
            'checked_against': csv_column,
            'target_m': mod.TARGET_LONGEST_M,
            'built_longest_m': info['dimensions_m']['longest'],
            'abs_error_m': abs(info['dimensions_m']['longest']
                               - mod.TARGET_LONGEST_M),
            'csv_target_matches': (
                abs(csv_target - mod.TARGET_LONGEST_M) < 1e-9),
            'csv_game_longest_matches': (
                abs(csv_game - mod.TARGET_LONGEST_M) < 1e-9),
            'scale_vs_real_m': round(mod.TARGET_LONGEST_M / csv_real, 3),
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
    if not info['dimension_check']['csv_target_matches']:
        # A mismatch is not fatal, but it must not vanish into the JSON either:
        # the mesh was scaled to models/<id>.py TARGET_LONGEST_M, so one of the
        # two numbers is wrong.
        print('WARN %-13s built longest %.4f m != CSV %s %r'
              % (pid, info['dimensions_m']['longest'], csv_column,
                 (row.get(csv_column) or '').strip()), flush=True)
    return info


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def write_sums(root=None):
    """Fingerprint every deliverable under ``root`` into SHA256SUMS.

    ``root`` defaults to the output root this invocation asks for (``--out`` /
    ``OUT_DIR``), which is what sign.py relies on.  scripts/ and README.md are
    only included when they live under that root, so a redirected build
    fingerprints its own tree and never lists repo files it did not write.
    """
    root = root or configure_output()
    entries = []
    for base in ('exports', 'previews', 'scripts'):
        tree = os.path.join(root, base)
        for dirpath, dirnames, filenames in os.walk(tree):
            dirnames.sort()
            for name in sorted(filenames):
                if (name.endswith('.pyc') or name.endswith('.blend1')
                        or name == '_labels.png'):
                    continue
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, root).replace(os.sep, '/')
                entries.append((sha256_file(full), rel))
    for extra in ('manifest.json', 'verification.json', 'README.md'):
        full = os.path.join(root, extra)
        if os.path.exists(full):
            entries.append((sha256_file(full), extra))
    if not entries:
        raise BuildInputError(
            'nothing to fingerprint under %s (no exports/, previews/ or '
            'manifest.json); build first, then regenerate SHA256SUMS' % root)
    entries.sort(key=lambda e: e[1])
    with open(os.path.join(root, 'SHA256SUMS'), 'w',
              encoding='utf-8', newline='\n') as fh:
        for digest, rel in entries:
            fh.write('%s  %s\n' % (digest, rel))
    return entries


def _fail(exc):
    """Report an input problem and exit non-zero without a bare traceback."""
    sys.stderr.write('BUILD_FAILED: %s\n' % exc)
    sys.exit(2)


def main():
    root = configure_output()
    print('OUTPUT_ROOT %s' % root, flush=True)
    t0 = time.time()
    rows = catalog_rows()
    pids = selected_props()
    full_batch = pids == list(propmodels.ORDER)
    if not full_batch:
        print('SUBSET %s' % ','.join(pids), flush=True)
    entries = []
    for pid in pids:
        print('=== %s' % pid, flush=True)
        entries.append(build_one(pid, rows))

    contact = os.path.join(lib.PREVIEW_DIR, 'contact_sheet.png')
    lib.contact_sheet(entries, contact)
    print('CONTACT_SHEET %s %d bytes' % (contact,
                                         os.path.getsize(contact)), flush=True)

    doc = {
        'title': ('原创占位小道具（第一批） / original placeholder tiny props'
                  if full_batch else
                  '原创占位小道具（子集 %d 件） / original placeholder props '
                  'subset' % len(entries)),
        'status': '原创占位，非最终美术；未指定许可；尺度按目标最长边（真实尺寸）'
                  '与 prop_catalog.csv 逐件核对',
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
    with open(os.path.join(root, 'manifest.json'), 'w',
              encoding='utf-8') as fh:
        json.dump(doc, fh, ensure_ascii=False, indent=2)
    sums = write_sums(root)

    print('', flush=True)
    print('%-13s %6s %6s %6s %8s %8s %s' % (
        'id', 'tris', 'verts', 'budget', 'longest', 'csv_ref', 'ok'), flush=True)
    for e in entries:
        # Show the catalog column this prop is actually checked against: the
        # first batch uses game_longest_dim_m, the real-size set uses
        # real_longest_dim_m.  Printing the game column for both hid which
        # contract a row belonged to.
        checked = e['dimension_check']['checked_against']
        ref = e['catalog']['real_longest_dim_m' if checked == 'real_longest_dim_m'
                          else 'game_longest_dim_m']
        print('%-13s %6d %6d %6d %8.4f %8s %s' % (
            e['id'], e['triangles'], e['vertices'], e['tri_budget'],
            e['dimensions_m']['longest'], ref,
            'yes' if e['within_budget'] else 'NO'), flush=True)
    print('TOTAL tris=%d props=%d files=%d elapsed=%.1fs' % (
        sum(e['triangles'] for e in entries), len(entries), len(sums),
        time.time() - t0), flush=True)
    print('BUILD_ALL_OK', flush=True)


def run_single(pid):
    configure_output()
    try:
        info = build_one(pid)
    except BuildInputError as exc:
        _fail(exc)
        return
    print(json.dumps(info, ensure_ascii=False, indent=2))
    print('BUILD_PROP_OK', flush=True)


if __name__ == '__main__':
    try:
        main()
    except BuildInputError as exc:
        _fail(exc)
