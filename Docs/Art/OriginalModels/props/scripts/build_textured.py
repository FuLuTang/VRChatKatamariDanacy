"""Build textured props: UV + procedural PBR + FBX/GLB/blend + preview.

    blender --background --python build_textured.py -- \
        --out /tmp/tex_pilot --props button,seed,crate

This is the pilot driver for TEXTURE_PBR_SPEC.md: it reuses the model modules
unchanged (they still build material slots), then bakes those slots into one
texture set and collapses the prop to a single material.  Nothing about the
geometry changes, so dimensions, pivot and ground contact stay identical - the
manifest proves it by re-checking them after the material swap.
"""

import json
import os
import sys
import time

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import prop_lib as lib          # noqa: E402
import prop_tex as tex          # noqa: E402
import models as propmodels     # noqa: E402


def resolution_for(longest_m, aspect=1.0):
    """Texture edge by prop size - the tier table from the spec, in one place.

    128 px for anything a player sees as a 3-20 cm blob on screen, 256 for the
    0.14-0.30 m middle, 512 for the 0.30-0.65 m pieces where a seam or a
    handle reads as geometry.  Nothing above 0.65 m is in the pilot.
    """
    # 256 is the floor: at 128 px the 1-2 mm eyelets / zip teeth / pip recesses
    # of the small props shrink to 1-2 texels and get eaten by the bake margin,
    # which reads as "no detail" even though the model has it.
    base = 256 if longest_m < 0.30 else 512
    # A sliver (grass blade 0.30 m x 4 mm, aspect 75:1) puts its whole surface in
    # a few UV texels at 256; the slot colour then bleeds into its neighbours and
    # the blade lost its green in the second pilot run.  Long thin pieces get one
    # step up.
    if aspect >= 6.0:
        base = min(1024, base * 2)
    return base


def build_one(pid, out_root):
    mod = propmodels.load(pid)
    lib.reset_scene()
    b = lib.Builder(pid)
    mod.build(b)
    # Modules may or may not call Builder.finish() themselves (seed does not,
    # button does), so finish here and take the object afterwards - the same
    # order build_all.py uses.
    info = b.finish(mod.TARGET_LONGEST_M)
    ob = b.object

    before = dict(info['dimensions_m'])
    longest = before['longest']
    others = sorted([before['x'], before['y'], before['z']])[-2:]
    res = resolution_for(longest, longest / max(1e-6, others[0]))

    t0 = time.time()
    # Adaptive unwrap: a higher angle limit means fewer seams and better texel
    # use, but folded surfaces (beetle_figurine's mandibles and leg tubes) can
    # self-overlap inside one island.  Try the permissive angle first and only
    # tighten when the rasteriser reports an actual overlap.
    uv_angle, uv = 66.0, None
    for candidate in (66.0, 48.0, 30.0):
        while ob.data.uv_layers:
            ob.data.uv_layers.remove(ob.data.uv_layers[0])
        uv = tex.unwrap(ob, angle_deg=candidate)
        if tex.uv_check(ob)['overlap_texels'] == 0:
            uv_angle = candidate
            break
        uv_angle = candidate
    bake_res = min(1024, res * 2)
    colour = tex.bake_colour(ob, bake_res)
    ao = tex.bake_ao(ob, bake_res)
    maps = tex.build_maps(colour, ao, mod.MATERIALS, bake_res,
                          seed=sum(ord(c) for c in pid))
    # Slot check runs on the bake-resolution maps, before the box filter.
    check = tex.slot_colour_check(maps, mod.MATERIALS)
    maps = tex.downscale_maps(maps, res)
    bake_s = time.time() - t0

    checker = tex.checker_image(os.path.join(out_root, 'uv_checker.png'))

    tdir = os.path.join(out_root, 'textures')
    albedo = tex.save_png(maps['albedo'], os.path.join(tdir, pid + '_albedo.png'),
                          pid + '_albedo')
    orm = tex.save_png(maps['mra'], os.path.join(tdir, pid + '_mra.png'),
                       pid + '_mra')
    relief = [k for k in maps.get('normal_kinds', []) if k]
    has_normal = bool(relief) and res >= 256
    normal = (tex.save_png(maps['normal'], os.path.join(tdir, pid + '_normal.png'),
                           pid + '_normal') if has_normal else None)
    tex.apply_textured(ob, albedo, orm, normal, 'Prop_%s_tex' % pid)
    # finish() recorded the slot materials; after the swap the mesh really has
    # one.  Keep the palette under textures.slots_baked and report the truth.
    single = 'Prop_%s_tex' % pid
    info['materials'] = [single]
    info['material_base_colors'] = {single: [1.0, 1.0, 1.0]}
    info['mesh_material_slots'] = len(ob.data.materials)

    # Material swap must not move a millimetre: re-measure the mesh and diff.
    lib.bounds(ob.data)
    mn, mx = lib.bounds(ob.data)
    after = {'x': mx.x - mn.x, 'y': mx.y - mn.y, 'z': mx.z - mn.z}
    drift = max(abs(after[k] - before[k]) for k in 'xyz')

    normal_used = bool(normal)
    exp = lib.export_asset(ob, pid, info)
    prev = os.path.join(out_root, 'previews', pid + '_preview.png')
    lib.render_preview(ob, prev)

    # Hard evidence for the UV question: bake the UV layout itself into a
    # checker render.  Same camera, same object, only the material changes.
    uv_metrics = tex.uv_check(ob)
    uv_metrics['angle_limit_deg'] = uv_angle
    tex.apply_checker(ob, checker, 'UVCheck_%s' % pid)
    uvprev = os.path.join(out_root, 'previews', pid + '_uvcheck.png')
    lib.render_preview(ob, uvprev)

    budget = tex.texture_budget(res)
    info['textures'] = {
        'resolution': res,
        'albedo': 'textures/%s_albedo.png' % pid,
        'mra': 'textures/%s_mra.png' % pid,
        'normal': ('textures/%s_normal.png' % pid) if normal_used else None,
        'normal_kinds': relief,
        'uv_check': uv_metrics,
        'uv_check_preview': 'previews/%s_uvcheck.png' % pid,
        'channels': 'albedo(sRGB RGB) + MRA(R=metal,G=AO,A=smoothness) + normal(RGB)',
        'slots_baked': [m['key'] for m in mod.MATERIALS],
        'uv': uv,
        'compression': 'BC7 albedo / BC5 normal / BC7 ORM (Unity)',
        'bytes_with_mips': budget['with_mips_bytes'],
        'single_material': single,
        'slot_colour_check': check,
        'dims_drift_m': round(drift, 12),
        'bake_seconds': round(bake_s, 1),
    }
    info['preview'] = 'previews/%s_preview.png' % pid
    return info


def main():
    out_root = lib.cli_option('--out', None) or os.environ.get('OUT_DIR')
    if not out_root:
        raise SystemExit('pass --out DIR after --')
    out_root = os.path.abspath(out_root)
    lib.set_output_root(out_root)

    props = lib.cli_option('--props', '')
    ids = [p.strip() for p in props.split(',') if p.strip()] or list(
        propmodels.ORDER)

    entries = []
    t0 = time.time()
    for pid in ids:
        info = build_one(pid, out_root)
        entries.append(info)
        print('  %-18s %4d tris  tex %s  %s' % (
            pid, info['triangles'], info['textures']['resolution'],
            info['textures']['single_material']))
    elapsed = time.time() - t0

    doc = {
        'title': 'Original placeholder props - textured pilot',
        'unit': 'metre', 'up': '+Z', 'front': '+Y',
        'generated_by': 'build_textured.py',
        'texture_policy': 'one texture set + one material per prop',
        'props': entries,
    }
    os.makedirs(os.path.join(out_root, 'exports'), exist_ok=True)
    with open(os.path.join(out_root, 'manifest.json'), 'w') as fh:
        json.dump(doc, fh, indent=2, sort_keys=True)
    print('TEXTURED_OK props=%d elapsed=%.0fs' % (len(entries), elapsed))


if __name__ == '__main__':
    main()
