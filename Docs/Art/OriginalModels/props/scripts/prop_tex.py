"""UV unwrap + procedural PBR texture baking for the original placeholder props.

This module is the headless, reproducible half of TEXTURE_PBR_SPEC.md.  It turns
a prop that was built with *material slots* (flat Principled colours) into a prop
that ships **one material and one texture set**:

    albedo  RGB  base colour x fine noise x baked ambient occlusion
    ORM     R = occlusion, G = roughness, B = metallic   (linear, no sRGB)
    normal  RGB  tangent-space normal from a procedural height field

Pipeline, all deterministic for a given (id, seed, resolution):

    unwrap(ob)              Smart UV Project, island margin 1.5 % of the atlas
    bake_colour(ob, res)    Cycles DIFFUSE / COLOR pass -> flat per-slot colour
    bake_ao(ob, res)        Cycles AO pass              -> cavity map
    build_maps(...)         numpy synthesis of albedo / ORM / normal
    apply_textured(ob)      one material, image nodes wired
    save_maps(...)          PNG files next to the FBX

Why the colour bake instead of reading `MATERIALS` in Python: the bake already
resolves which face owns which texel, so slot boundaries, UV island bleed and
boolean seams all come out right for free, and the synthesis only has to
*recognise* the slot colour (nearest neighbour in RGB) to look up its roughness
and metallic value.

The 0.8/1.2 style numbers below are deliberately mild: these are 1-3 cm
placeholders seen at 5-20 cm on screen in VRChat, so the texture has to survive
BC7 compression and mip reduction without turning into noise mush.
"""

import math
import os

import bpy
import numpy as np
from mathutils import Vector

import prop_lib as lib

# ---------------------------------------------------------------------------
# Unwrapping
# ---------------------------------------------------------------------------


def unwrap(ob, margin=0.015, angle_deg=66.0):
    """Smart UV Project the whole mesh and pack the islands into 0..1.

    Smart Project is used rather than a projected unwrap because the props are
    boolean-heavy (holes, slots, recesses): the angle-based island split keeps
    each flat face patch contiguous, so a 1.5 % margin is enough and the
    texel density stays within roughly 2:1 across the object.
    """
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(
        angle_limit=math.radians(angle_deg),
        island_margin=margin,
        area_weight=0.0,
        correct_aspect=True,
        scale_to_bounds=False)
    # Smart Project can leave two islands overlapping (measured on
    # beetle_figurine: 314 texels at 512, 0.24 % of the used area).  Packing
    # afterwards with the same margin guarantees separation for disconnected
    # islands; rotate=False keeps the layout reproducible.
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.pack_islands(rotate=False, scale=True, margin=margin)
    bpy.ops.object.mode_set(mode='OBJECT')
    return _uv_stats(ob)


def _uv_stats(ob):
    """Island count, UV area and the longest-island share, for the manifest."""
    me = ob.data
    uvs = [loop.uv[:] for loop in me.uv_layers.active.data]
    if not uvs:
        return {'islands': 0, 'uv_area': 0.0, 'max_island_share': 0.0}
    xs = [u for u, _ in uvs]
    ys = [v for _, v in uvs]
    # Cheap area proxy: the bounding box of the whole UV set.  A true island
    # count needs a connectivity flood fill, which is not worth the seconds per
    # prop; the manifest records what is measurable and cheap instead of a
    # number that would be a guess.
    return {
        'uv_min': [round(min(xs), 4), round(min(ys), 4)],
        'uv_max': [round(max(xs), 4), round(max(ys), 4)],
        'uv_layers': len(me.uv_layers),
    }


# ---------------------------------------------------------------------------
# Baking
# ---------------------------------------------------------------------------


def _bake_image(ob, res, name, fill):
    img = bpy.data.images.new(name, res, res, alpha=False, float_buffer=False)
    img.generated_color = fill
    return img


def _attach_bake_node(material, img):
    nodes = material.node_tree.nodes
    node = nodes.get('BAKE_TARGET')
    if node is None:
        node = nodes.new('ShaderNodeTexImage')
        node.name = 'BAKE_TARGET'
        node.label = 'BAKE_TARGET'
        node.location = (-800, 600)
    node.image = img
    nodes.active = node
    for n in nodes:
        n.select = (n is node)
    return node


def _srgb_to_linear(x):
    """Byte images hand back sRGB-encoded values, not linear ones.

    Cycles writes the bake into an 8-bit image, so ``image.pixels`` reads the
    sRGB-encoded byte value.  Treating that as linear and re-encoding it on the
    way out (which is what the first pilot did) brightens and desaturates every
    slot - the grass blade's green (0.115, 0.24, 0.075) came back as
    (0.37, 0.52, 0.30) and every prop baked out beige.
    """
    c = np.clip(x, 0.0, 1.0)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def _detach_bake_nodes(ob):
    for slot in ob.material_slots:
        mat = slot.material
        if mat and mat.use_nodes:
            node = mat.node_tree.nodes.get('BAKE_TARGET')
            if node:
                mat.node_tree.nodes.remove(node)


def _setup_cycles(samples):
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.cycles.samples = samples
    sc.cycles.use_denoising = False
    sc.render.bake.margin = max(2, int(round(sc.render.resolution_x * 0.004)))
    sc.render.bake.use_clear = True


def bake_colour(ob, res):
    """Bake the flat per-slot base colour (DIFFUSE, COLOR only) to a numpy map."""
    img = _bake_image(ob, res, ob.name + '_bake_colour', (0, 0, 0, 1))
    for slot in ob.material_slots:
        if slot.material:
            _attach_bake_node(slot.material, img)
    _setup_cycles(1)
    sc = bpy.context.scene
    sc.render.resolution_x = res
    sc.render.resolution_y = res
    sc.render.bake.use_pass_direct = False
    sc.render.bake.use_pass_indirect = False
    sc.render.bake.use_pass_color = True
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.bake(type='DIFFUSE')
    arr = np.array(img.pixels[:], dtype=np.float32).reshape(res, res, 4)
    arr[..., :3] = _srgb_to_linear(arr[..., :3])
    _detach_bake_nodes(ob)
    bpy.data.images.remove(img)
    return arr


def bake_ao(ob, res, samples=8, distance_scale=0.35):
    """Bake ambient occlusion (Cycles AO pass) to a numpy map."""
    img = _bake_image(ob, res, ob.name + '_bake_ao', (1, 1, 1, 1))
    for slot in ob.material_slots:
        if slot.material:
            _attach_bake_node(slot.material, img)
    _setup_cycles(samples)
    sc = bpy.context.scene
    sc.render.resolution_x = res
    sc.render.resolution_y = res
    # Ray distance follows the prop, so a 12 mm button and a 600 mm crate get
    # the same visual cavity depth instead of the crate getting none.
    dims = lib.bounds(ob.data)
    span = max(dims[1][i] - dims[0][i] for i in range(3))
    sc.world = sc.world or bpy.data.worlds.new('BakeWorld')
    sc.world.light_settings.distance = max(0.001, span * distance_scale)
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.bake(type='AO')
    arr = np.array(img.pixels[:], dtype=np.float32).reshape(res, res, 4)
    arr[..., :3] = _srgb_to_linear(arr[..., :3])
    _detach_bake_nodes(ob)
    bpy.data.images.remove(img)
    return arr


# ---------------------------------------------------------------------------
# Noise
# ---------------------------------------------------------------------------


def _value_noise(res, cells, rng):
    """Bilinear-upsampled value noise in 0..1 with `cells` lattice cells."""
    grid = rng.random((cells + 1, cells + 1)).astype(np.float32)
    xs = np.linspace(0, cells, res, endpoint=False, dtype=np.float32)
    i0 = np.floor(xs).astype(np.int32)
    f = xs - i0
    f = f * f * (3.0 - 2.0 * f)
    i1 = np.minimum(i0 + 1, cells)
    a = grid[i0][:, i0]
    b = grid[i0][:, i1]
    c = grid[i1][:, i0]
    d = grid[i1][:, i1]
    fx = f[None, :]
    fy = f[:, None]
    top = a * (1 - fx) + b * fx
    bot = c * (1 - fx) + d * fx
    return top * (1 - fy) + bot * fy


def _fbm(res, seed, base_cells=6, octaves=4, gain=0.5):
    rng = np.random.default_rng(seed)
    out = np.zeros((res, res), dtype=np.float32)
    amp, total, cells = 1.0, 0.0, base_cells
    for _ in range(octaves):
        out += amp * _value_noise(res, cells, rng)
        total += amp
        amp *= gain
        cells *= 2
    return out / total


# ---------------------------------------------------------------------------
# Synthesis
# ---------------------------------------------------------------------------


# ---------------------------------------------------------------------------
# Normal maps: which materials get relief, and where it comes from
# ---------------------------------------------------------------------------

# Hard / glaze / metal / plastic surfaces are smooth.  A normal map on them is
# a lie: at 10-50 cm the eye expects the highlight to break only at an edge or a
# seam, which is *geometry*, not noise.  These get no normal map at all.
SMOOTH_CLASS = ('ceramic', 'porcelain', 'glass', 'glaze', 'enamel', 'metal',
                'brass', 'steel', 'chrome', 'silver', 'copper', 'tin',
                'bronze', 'aluminium', 'aluminum', 'plastic', 'resin', 'ink',
                'paint', 'chitin', 'shell', 'eye', 'lens')

# Rough materials that genuinely carry micro-relief, and the physical height
# model used for each.  The gradient of that height is the normal map - no
# other normal source is allowed (spec section 12).
RELIEF_CLASS = {
    'wood': 'grain', 'bark': 'grain', 'plank': 'grain', 'bamboo': 'grain',
    'cardboard': 'fibre', 'paper': 'fibre', 'fabric': 'weave',
    'cloth': 'weave', 'wool': 'weave', 'cotton': 'weave', 'rope': 'weave',
    'jute': 'weave', 'straw': 'fibre', 'bread': 'crumb', 'cracker': 'crumb',
    'biscuit': 'crumb', 'rice': 'crumb', 'grain': 'crumb', 'seed': 'crumb',
    'leaf': 'fibre', 'grass': 'fibre', 'soil': 'granular',
    'stone': 'granular', 'pebble': 'granular', 'rock': 'granular',
    'concrete': 'granular', 'rust': 'granular',
}

RELIEF_STRENGTH = {
    'grain': 0.50,      # wood: long directional grooves
    'weave': 0.40,      # fabric: crossed threads
    'fibre': 0.30,      # paper / leaf / straw: fine fibres
    'crumb': 0.35,      # bread / seed: small lumps
    'granular': 0.45,   # stone / soil: grains and pits
}


def relief_kind(slot_key):
    """'grain' / 'weave' / 'fibre' / 'crumb' / 'granular' / None (smooth)."""
    key = (slot_key or '').lower()
    for token, kind in RELIEF_CLASS.items():
        if token in key:
            return kind
    for token in SMOOTH_CLASS:
        if token in key:
            return None
    return None


def normal_strength(slot_key):
    kind = relief_kind(slot_key)
    return 0.0 if kind is None else RELIEF_STRENGTH[kind]


def _height_field(kind, res, seed):
    """Physical height model per material class, 0..1 (0.5 = neutral)."""
    if kind == 'grain':
        # Long streaks along U: the noise lattice is stretched, not random.
        rng = np.random.default_rng(seed)
        grid = rng.random((6, 64)).astype(np.float32)
        gy = np.linspace(0, 5, res, endpoint=False)
        gx = np.linspace(0, 63, res, endpoint=False)
        iy, fx = np.floor(gy).astype(int), gx - np.floor(gx)
        ix = np.floor(gx).astype(int)
        fy = gy - np.floor(gy)
        iy1 = np.minimum(iy + 1, 5)
        ix1 = np.minimum(ix + 1, 63)
        a = grid[iy][:, ix]
        b = grid[iy][:, ix1]
        c = grid[iy1][:, ix]
        d = grid[iy1][:, ix1]
        fx = fx[None, :]
        fy = fy[:, None]
        top = a * (1 - fx) + b * fx
        bot = c * (1 - fx) + d * fx
        grain = top * (1 - fy) + bot * fy
        return 0.5 + (grain - 0.5) * 0.9
    if kind == 'weave':
        u = np.linspace(0, np.pi * 2 * 90, res, dtype=np.float32)
        w = (np.sin(u)[None, :] * 0.5 + np.sin(u)[:, None] * 0.5) * 0.5
        fine = _value_noise(res, 96, np.random.default_rng(seed)) - 0.5
        return 0.5 + w * 0.55 + fine * 0.25
    if kind == 'fibre':
        return 0.5 + (_value_noise(res, 110, np.random.default_rng(seed)) - 0.5) * 0.7
    if kind == 'crumb':
        return 0.5 + (_fbm(res, seed, base_cells=18, octaves=3) - 0.5) * 0.9
    if kind == 'granular':
        return 0.5 + (_fbm(res, seed, base_cells=26, octaves=4) - 0.5) * 1.0
    return np.full((res, res), 0.5, dtype=np.float32)


def _slot_table(slots):
    """[(rgb, roughness, metallic)] from the prop's MATERIALS declaration."""
    table = []
    for s in slots:
        key = (s.get('key') or '').lower()
        # Metallic stays 0 unless a module says otherwise.  VRChat worlds have
        # no reflection probes by default, and a metallic=1 surface with no
        # environment renders as a dark blob - the brass coin came out olive
        # and the beetle's brass head went from yellow to olive in the pilot.
        # Spec section 3: metals are faked by albedo + low roughness for now.
        metallic = float(s.get('metallic', 0.0))
        table.append((np.array(s['linear_rgb'], dtype=np.float32),
                      float(s.get('roughness', 0.6)), float(metallic)))
    return table


def build_maps(colour, ao, slots, res, seed, strength=1.0):
    """Synthesise albedo / ORM / normal from the baked colour + AO maps.

    `colour` is the flat slot colour per texel (sRGB-decoded to linear by the
    bake?  No: Cycles hands back linear values, which is what we want), `ao` is
    the cavity pass in 0..1.
    """
    table = _slot_table(slots)
    srgb = colour[..., :3]
    # Nearest slot in linear RGB -> index map.  Texels on an island margin are
    # black; they get slot 0 and are never sampled by the mesh anyway.
    dists = np.stack([np.sum((srgb - rgb) ** 2, axis=-1) for rgb, _, _ in table],
                     axis=0)
    idx = np.argmin(dists, axis=0).astype(np.int32)
    base = np.stack([table[i][0] for i in range(len(table))])[idx]
    rough = np.float32([table[i][1] for i in range(len(table))])[idx]
    metal = np.float32([table[i][2] for i in range(len(table))])[idx]

    a = np.clip(ao[..., 0], 0.0, 1.0)
    a = a ** 0.85                     # keep cavity readable after mip blur
    grain = _fbm(res, seed, base_cells=8, octaves=4)
    fine = _fbm(res, seed + 1, base_cells=24, octaves=3)
    # Distance from the nearest slot colour: a cheap "edge" signal that darkens
    # slot boundaries slightly, which reads as a seam at 512 px.
    edge = np.clip(np.sqrt(np.min(dists, axis=0)) * 3.0, 0.0, 1.0)

    # Conservative on purpose.  The first pilot run used +-20 % noise and a
    # 0.45 + 0.55*AO term and every prop came out blotchy and "wet": at 5-20 cm
    # on screen the eye reads that as dirt, not as material.  +-6 % noise and a
    # 0.78 + 0.22*AO term keep the slot colour recognisable.
    albedo_l = base * (0.94 + 0.12 * grain[..., None]) * (0.78 + 0.22 * a[..., None])
    albedo_l *= (1.0 - 0.05 * edge[..., None])
    if strength != 1.0:
        albedo_l = base + (albedo_l - base) * strength

    # MRA packing, not glTF ORM: Unity Standard reads metallic from R and
    # smoothness from A of _MetallicGlossMap, and occlusion from G of
    # _OcclusionMap.  One RGBA texture therefore feeds both slots.
    mra = np.empty((res, res, 4), dtype=np.float32)
    mra[..., 0] = metal
    mra[..., 1] = a * (0.94 + 0.06 * grain)
    mra[..., 2] = 0.0
    mra[..., 3] = 1.0 - np.clip(rough * (0.96 + 0.08 * fine), 0.04, 1.0)

    # Normal map = gradient of a *per-material height model* (grain / weave /
    # fibre / crumb / granular), never raw noise, and flat for smooth classes.
    kinds = [relief_kind(s.get('key', '')) for s in slots]
    strengths = np.float32([RELIEF_STRENGTH.get(k, 0.0) if k else 0.0
                            for k in kinds])[idx]
    fields = {}
    for s_i, kind in enumerate(kinds):
        if kind and kind not in fields:
            fields[kind] = _height_field(kind, res, seed + 17 * s_i)
    height = np.full((res, res), 0.5, dtype=np.float32)
    for s_i, kind in enumerate(kinds):
        if kind:
            height = np.where(idx == s_i, fields[kind], height)
    if strengths.max() > 0:
        # Scale the world-space slope by the material's strength so a wood
        # grain is visible and a paper fibre is barely there.
        scale = res * 0.010 * float(strengths.max())
        gy, gx = np.gradient(height)
        nx, ny, nz = -gx * scale, -gy * scale, np.ones_like(gx)
        ln = np.sqrt(nx * nx + ny * ny + nz * nz)
        normal = np.stack([nx / ln, ny / ln, nz / ln], axis=-1) * 0.5 + 0.5
        flat = np.array([0.5, 0.5, 1.0], dtype=np.float32)
        normal = np.where((strengths > 0)[..., None], normal, flat)
    else:
        normal = np.ones((res, res, 3), dtype=np.float32) * 0.5
        normal[..., 2] = 1.0

    return {
        'albedo': _linear_to_srgb_rgba(albedo_l),
        'mra': mra,
        'normal': np.dstack([normal, np.ones((res, res), np.float32)]),
        'normal_kinds': kinds,
        'slot_index': idx,
    }


def downscale_maps(maps, res):
    """Box-filter the baked maps down to the shipping resolution.

    Slots are baked at 2x the target and quantised there, so a 1 mm eyelet keeps
    its own texels for the nearest-slot decision and only *then* gets averaged;
    baking straight at 128/256 let the bake margin swallow it.
    """
    out = {}
    for key in ('albedo', 'mra', 'normal'):
        arr = maps[key]
        h = arr.shape[0]
        f = h // res
        if f < 2:
            out[key] = arr
            continue
        red = arr.reshape(res, f, res, f, arr.shape[2]).mean(axis=(1, 3))
        if key == 'normal':
            n = red[..., :3] * 2.0 - 1.0
            ln = np.sqrt((n * n).sum(-1, keepdims=True)) + 1e-6
            n = n / ln
            red = np.concatenate([n * 0.5 + 0.5, red[..., 3:4]], axis=-1)
        out[key] = red.astype(np.float32)
    # slot_index is a label map, not an image: take one label per block.
    si = maps['slot_index']
    f2 = si.shape[0] // res
    out['slot_index'] = si[::f2, ::f2][:res, :res] if f2 >= 2 else si
    out['normal_kinds'] = maps.get('normal_kinds', [])
    return out


def _linear_to_srgb_rgba(rgb):
    x = np.clip(rgb, 0.0, 1.0)
    srgb = np.where(x <= 0.0031308, x * 12.92,
                    1.055 * np.power(x, 1.0 / 2.4) - 0.055)
    return np.dstack([srgb, np.ones(x.shape[:2], np.float32)])


def slot_colour_check(maps, slots):
    """Per-slot: does the baked albedo still match the declared slot colour?

    This is the acceptance test for "texture化 does not break slot-colour
    compatibility": the texels belonging to slot k are compared against the
    palette entry the module declared for k.
    """
    idx = maps['slot_index']
    alb = maps['albedo'][..., :3]
    linear = np.where(alb <= 0.04045, alb / 12.92, ((alb + 0.055) / 1.055) ** 2.4)
    out = []
    for k, s in enumerate(slots):
        mask = idx == k
        n = int(mask.sum())
        if n == 0:
            out.append({'slot': s['key'], 'texels': 0})
            continue
        mean = linear[mask].mean(axis=0)
        want = np.array(s['linear_rgb'], dtype=np.float32)
        out.append({
            'slot': s['key'],
            'texels': n,
            'albedo_mean_linear': [round(float(v), 3) for v in mean],
            'declared_linear': [round(float(v), 3) for v in want],
            'max_channel_delta': round(float(np.abs(mean - want).max()), 3),
        })
    return out


# ---------------------------------------------------------------------------
# Saving + material
# ---------------------------------------------------------------------------


def save_png(arr, path, name):
    res = arr.shape[0]
    img = bpy.data.images.new(name, res, res, alpha=False, float_buffer=False)
    img.pixels = arr.reshape(-1).tolist()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)
    return path


def apply_textured(ob, albedo_path, orm_path, normal_path, slot_name):
    """Replace the prop's slots with ONE textured material.

    The texture already carries every slot colour, so keeping three materials
    would tripple the Unity material count and the draw calls for nothing.
    """
    me = ob.data
    mat = bpy.data.materials.new(slot_name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes['Principled BSDF']
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.location = (-700, 300)
    tex.image = bpy.data.images.load(albedo_path, check_existing=True)
    nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])

    mra = nt.nodes.new('ShaderNodeTexImage')
    mra.location = (-700, 0)
    mra.image = bpy.data.images.load(orm_path, check_existing=True)
    mra.image.colorspace_settings.name = 'Non-Color'
    sep = nt.nodes.new('ShaderNodeSeparateColor')
    sep.location = (-450, 0)
    nt.links.new(mra.outputs['Color'], sep.inputs['Color'])
    nt.links.new(sep.outputs['Red'], bsdf.inputs['Metallic'])
    # MRA stores smoothness in alpha; Principled wants roughness = 1 - A.
    inv = nt.nodes.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.location = (-450, -140)
    inv.inputs[0].default_value = 1.0
    nt.links.new(mra.outputs['Alpha'], inv.inputs[1])
    nt.links.new(inv.outputs['Value'], bsdf.inputs['Roughness'])

    if normal_path is None:
        me.materials.clear()
        me.materials.append(mat)
        for poly in me.polygons:
            poly.material_index = 0
        return mat

    nrm = nt.nodes.new('ShaderNodeTexImage')
    nrm.location = (-700, -300)
    nrm.image = bpy.data.images.load(normal_path, check_existing=True)
    nrm.image.colorspace_settings.name = 'Non-Color'
    nmap = nt.nodes.new('ShaderNodeNormalMap')
    nmap.location = (-450, -300)
    nt.links.new(nrm.outputs['Color'], nmap.inputs['Color'])
    nt.links.new(nmap.outputs['Normal'], bsdf.inputs['Normal'])

    me.materials.clear()
    me.materials.append(mat)
    for poly in me.polygons:
        poly.material_index = 0
    return mat




# ---------------------------------------------------------------------------
# UV verification
# ---------------------------------------------------------------------------


def uv_check(ob, res=512):
    """Rasterise every UV triangle and report coverage, overlap and degeneracy.

    This is the hard evidence the spec asks for: a UV set is "correct" only if
    (a) no texel is claimed by two different faces, (b) the islands together
    cover a sane share of 0..1, and (c) no face has a zero-area UV triangle.
    """
    me = ob.data
    uv = me.uv_layers.active.data if me.uv_layers else None
    if uv is None:
        return {'error': 'no uv layer'}
    cover = np.zeros((res, res), dtype=np.int32)
    zero_area = 0
    total_area = 0.0
    area_sum = 0.0
    for poly in me.polygons:
        loops = list(poly.loop_indices)
        pts = [uv[i].uv for i in loops]
        if len(pts) < 3:
            continue
        for k in range(1, len(pts) - 1):
            tri = (pts[0], pts[k], pts[k + 1])
            a = _tri_area(tri)
            area_sum += a
            total_area += a
            if a <= 1e-12:
                zero_area += 1
                continue
            _raster_tri(cover, tri, res)
    used = int((cover > 0).sum())
    over = int((cover > 1).sum())
    return {
        'uv_area_fraction': round(float(area_sum), 4),
        'coverage_pct': round(100.0 * used / (res * res), 2),
        'overlap_texels': over,
        'overlap_pct_of_used': round(100.0 * over / max(1, used), 3),
        'zero_area_uv_triangles': zero_area,
        'max_texel_stack': int(cover.max()),
        'raster_res': res,
    }


def _tri_area(tri):
    (x0, y0), (x1, y1), (x2, y2) = tri
    return abs((x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)) * 0.5


def _raster_tri(cover, tri, res):
    xs = [t[0] * res for t in tri]
    ys = [t[1] * res for t in tri]
    x0, x1 = int(np.floor(min(xs))), int(np.ceil(max(xs)))
    y0, y1 = int(np.floor(min(ys))), int(np.ceil(max(ys)))
    x0, y0 = max(0, x0), max(0, y0)
    x1, y1 = min(res - 1, x1), min(res - 1, y1)
    if x1 < x0 or y1 < y0:
        return
    (ax, ay), (bx, by), (cx, cy) = tri
    gy, gx = np.mgrid[y0:y1 + 1, x0:x1 + 1]
    px = (gx + 0.5) / res
    py = (gy + 0.5) / res
    d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
    if abs(d) < 1e-12:
        return
    w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / d
    w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / d
    w2 = 1.0 - w0 - w1
    # Strict interior: a texel centre lying exactly on the shared edge of two
    # triangles belongs to both, which would report a phantom overlap.
    inside = (w0 > 1e-3) & (w1 > 1e-3) & (w2 > 1e-3)
    cover[y0:y1 + 1, x0:x1 + 1] += inside.astype(np.int32)


def checker_image(path, res=512, squares=32):
    """A UV checkerboard with axis marks: the only real proof of a UV unwrap.

    Squares are 16 px at 512, so a stretched island shows up as rectangles, a
    flipped island as a mirrored run, and a seam as a discontinuity.  The red
    band runs along u = 0 and the green band along v = 0, which fixes the
    orientation at a glance.
    """
    step = res // squares
    idx = (np.arange(res) // step) % 2
    board = (idx[None, :] ^ idx[:, None]).astype(np.float32)
    rgb = np.stack([board, board, board], axis=-1) * 0.9 + 0.02
    y, x = np.mgrid[0:res, 0:res]
    rgb[..., 0] = np.where(x < 2, 0.9, rgb[..., 0])
    rgb[..., 1] = np.where(y < 2, 0.9, rgb[..., 1])
    arr = np.dstack([rgb, np.ones((res, res), np.float32)])
    save_png(arr, path, 'uv_checker')
    return path


def apply_checker(ob, checker_path, name):
    """Swap the prop to an unlit-ish checker material, keep the UV layout."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes['Principled BSDF']
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(checker_path, check_existing=True)
    nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = 1.0
    ob.data.materials.clear()
    ob.data.materials.append(mat)
    for poly in ob.data.polygons:
        poly.material_index = 0
    return mat


# ---------------------------------------------------------------------------
# Budget helpers
# ---------------------------------------------------------------------------

# Bytes per pixel for the Unity compression the spec mandates (BC7 / BC5).
BC7_BPP = 1.0
BC5_BPP = 1.0


def texture_budget(res, channels='albedo+normal+orm'):
    """Texture memory for a square map, including the mip chain."""
    px = res * res
    per_channel = px * BC7_BPP if 'normal' not in channels else px * BC5_BPP
    total = 0
    for part in channels.split('+'):
        total += px * (BC5_BPP if part == 'normal' else BC7_BPP)
    mips = total * 4.0 / 3.0
    return {
        'resolution': res,
        'base_bytes': int(total),
        'with_mips_bytes': int(mips),
        'per_channel_bytes': int(per_channel),
    }
