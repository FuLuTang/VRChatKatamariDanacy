"""Shared material for the three level-authoring Blender scripts.

Imported by ``build_scene.py``, ``export_placement.py`` and ``render_views.py``
(never run on its own).  It owns exactly the bpy-facing glue; every geometric
rule - axes, quaternions, AABB, seating, the absorb gate - comes from
``Tools/levelbuild/placement_core.py`` so the render, the validator and the
Unity import cannot disagree.

One script must run on both Blender builds the project uses: 4.0.2 (Ubuntu
arm64, headless, no GPU) and 5.3.0 (Windows, the user's GUI copy).  Version
differences are concentrated in :func:`select_engine` and
:func:`configure_eevee`; the EEVEE engine id itself is resolved by the prop
pipeline's own probe (``Docs/Art/OriginalModels/props/scripts/prop_lib.py``,
``select_eevee_engine``) instead of being re-implemented here.
"""

import importlib.util
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector

HERE = Path(__file__).resolve().parent          # Tools/levelbuild/blender
LEVELBUILD = HERE.parent                        # Tools/levelbuild
REPO = LEVELBUILD.parents[1]                    # repository root
if str(REPO) not in sys.path:
    sys.path.insert(0, str(REPO))

from Tools.levelbuild import placement_core as core  # noqa: E402

PROP_LIB_PATH = REPO / "Docs/Art/OriginalModels/props/scripts/prop_lib.py"

#: Blender colour for each structure role (Workbench reads ``diffuse_color``).
ROLE_COLORS = {
    "ground": (0.24, 0.33, 0.17, 1.0),
    "boundary": (0.55, 0.55, 0.60, 1.0),
    "box": (0.42, 0.29, 0.17, 1.0),
    "ramp": (0.52, 0.38, 0.22, 1.0),
}
KIND_COLORS = {
    "table": (0.58, 0.40, 0.22, 1.0),
    "crate": (0.45, 0.36, 0.20, 1.0),
    "lawn": (0.24, 0.33, 0.17, 1.0),
}


def prop_lib():
    """The prop pipeline module, loaded by path (it is not an installed package)."""
    spec = importlib.util.spec_from_file_location("levelbuild_prop_lib", PROP_LIB_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def script_args():
    """Arguments after Blender's ``--`` separator (empty when none)."""
    argv = sys.argv
    return argv[argv.index("--") + 1:] if "--" in argv else []


def option(name, default=None):
    """``--name VALUE`` or ``--name=VALUE`` after the separator."""
    args = script_args()
    for index, arg in enumerate(args):
        if arg == name:
            if index + 1 >= len(args):
                raise SystemExit("%s needs a value" % name)
            return args[index + 1]
        if arg.startswith(name + "="):
            return arg.split("=", 1)[1]
    return default


def flag(name):
    return name in script_args()


def resolve(path_text):
    """Relative paths come from the repository root, not from Blender's cwd."""
    path = Path(path_text).expanduser()
    return path if path.is_absolute() else (REPO / path)


def reset_scene():
    """Factory-empty scene; no default cube, camera or light, no .blend1 backup."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0
    return bpy.context.scene


# --------------------------------------------------------------------------- #
# prop library: one unlinked collection per def
# --------------------------------------------------------------------------- #
def load_library(defs, exports_dir=None):
    """Import each def's GLB into its own collection that is *not* linked to the scene.

    Linking a library collection and hiding it also hides every instance of it
    (measured, ``LEVEL_AUTHORING`` section 1.5), so the library stays out of the
    scene entirely: the scene holds instances only.
    """
    exports_dir = Path(exports_dir or core.PROPS_EXPORTS_DIR)
    library = {}
    for def_id in sorted(set(defs)):
        glb = exports_dir / (def_id + ".glb")
        if not glb.is_file():
            raise SystemExit("missing library asset for def %r: %s" % (def_id, glb))
        before = set(bpy.data.objects.keys())
        bpy.ops.import_scene.gltf(filepath=str(glb))
        imported = [bpy.data.objects[name] for name in bpy.data.objects.keys()
                    if name not in before]
        meshes = [obj for obj in imported if obj.type == "MESH"]
        if not meshes:
            raise SystemExit("def %r imported no mesh from %s" % (def_id, glb))
        collection = bpy.data.collections.new("LIB_" + def_id)
        for obj in meshes:
            for owner in list(obj.users_collection):
                owner.objects.unlink(obj)
            collection.objects.link(obj)
        for obj in imported:
            if obj.type != "MESH" and obj.users == 0:
                bpy.data.objects.remove(obj, do_unlink=True)
        library[def_id] = collection
    return library


def library_is_linked(collection):
    """True when ``collection`` (or a child of it) is linked into the scene."""
    def walk(node):
        if node == collection:
            return True
        return any(walk(child) for child in node.children)

    return walk(bpy.context.scene.collection)


# --------------------------------------------------------------------------- #
# scene content
# --------------------------------------------------------------------------- #
def new_prop_instance(item_id, collection, position, quat_xyzw):
    """An empty instancing the def's library collection, at ``position`` (Unity coords)."""
    obj = bpy.data.objects.new(item_id, None)
    obj.instance_type = "COLLECTION"
    obj.instance_collection = collection
    obj.empty_display_size = 0.06
    obj.rotation_mode = "QUATERNION"
    obj.location = Vector(core.unity_to_blender_position(position))
    q = core.unity_to_blender_quat(quat_xyzw)
    obj.rotation_quaternion = Quaternion((q[0], q[1], q[2], q[3]))
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _material(name, rgba):
    # Workbench uses material.diffuse_color, EEVEE/Cycles use the Principled node;
    # a material that only sets the node renders grey in the plan view (measured).
    material = bpy.data.materials.new("MAT_" + name)
    material.use_nodes = True
    principled = next((node for node in material.node_tree.nodes
                       if node.type == "BSDF_PRINCIPLED"), None)
    if principled is not None:
        principled.inputs["Base Color"].default_value = rgba
        if "Roughness" in principled.inputs:
            principled.inputs["Roughness"].default_value = 0.85
    material.diffuse_color = rgba
    return material


def new_box(name, center_u, size_u, quat_u, rgba):
    """A unit-cube-style box built in Unity axis order and posed in Blender space."""
    sx, sy, sz = size_u[0] * 0.5, size_u[2] * 0.5, size_u[1] * 0.5
    verts = [(-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz),
             (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz)]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    m = axis_matrix()
    # The quaternion goes in with its JSON component order (w, x, y, z) and *not*
    # through unity_to_blender_quat: the mesh above is already built in Unity axis
    # order, so the M^-1 ... M conjugation performs the axis change itself.  Adding
    # the sign flip here too would apply it twice (measured: the crate ended up
    # rotated about the wrong axis, `LEVEL_AUTHORING` 1.5).
    raw = Quaternion((quat_u[3], quat_u[0], quat_u[1], quat_u[2]))
    obj.matrix_world = (m.inverted()
                        @ Matrix.Translation(Vector(center_u))
                        @ raw.to_matrix().to_4x4()
                        @ m)
    mesh.materials.append(_material(name, rgba))
    return obj


def axis_matrix():
    """``M``: Blender (x, y, z) -> Unity (x, z, y) as a 4x4."""
    return Matrix(((1.0, 0.0, 0.0, 0.0),
                   (0.0, 0.0, 1.0, 0.0),
                   (0.0, 1.0, 0.0, 0.0),
                   (0.0, 0.0, 0.0, 1.0)))


def build_structures(doc):
    """Boxes for every structure; returns ``{id: object}``."""
    objects = {}
    for structure in doc.get("structures") or []:
        role = structure.get("role")
        rgba = KIND_COLORS.get(structure.get("kind")) or ROLE_COLORS.get(role, (0.5, 0.5, 0.5, 1.0))
        obj = new_box(structure["id"], tuple(structure["center"]), tuple(structure["size"]),
                      tuple(structure["rotationQuat"]), rgba)
        obj["role"] = role
        obj["shape"] = structure.get("shape", "box")
        obj["structure_id"] = structure["id"]
        for key in ("kind", "collision", "meets", "material", "notes"):
            if key in structure:
                obj["struct_" + key] = structure[key]
        obj["absorbable"] = bool(structure.get("absorbable", False))
        obj["support"] = bool(structure.get("support", False))
        objects[structure["id"]] = obj
    return objects


def build_props(doc, library, exports_dir=None):
    """Instance every prop; returns ``{itemId: object}``.

    ``aabb`` is checked against the def's manifest: a hand-edited size that no
    longer matches the model would make the validator and the render disagree.
    """
    manifests = core.load_manifests(exports_dir)
    objects = {}
    for prop in doc.get("props") or []:
        item_id = prop["itemId"]
        def_id = prop["def"]
        if def_id not in library:
            raise SystemExit("prop %s uses def %r which is not in the library" % (item_id, def_id))
        obj = new_prop_instance(item_id, library[def_id], tuple(prop["position"]),
                                tuple(prop["rotationQuat"]))
        obj["item_def"] = def_id
        for key, target in (("zone", "zone"), ("kind", "kind"), ("support", "support"),
                            ("stackedOn", "stacked_on"), ("valueUnits", "value_units")):
            if key in prop:
                obj[target] = prop[key]
        if "notes" in prop:
            obj["notes"] = prop["notes"]
        if def_id in manifests:
            expected = manifests[def_id]["aabb"]
            declared = tuple(float(v) for v in prop["aabb"])
            if max(abs(declared[i] - expected[i]) for i in range(3)) > 0.001:
                print("WARN aabb %s: json %s vs manifest %s"
                      % (item_id, declared, expected))
        objects[item_id] = obj
    return objects


def add_lights():
    """Two lights and a sky; the same rig for every preset so images compare."""
    bpy.ops.object.light_add(type="AREA", location=(3.0, -4.0, 4.0))
    key = bpy.context.active_object
    key.data.energy = 900.0
    key.data.size = 3.0
    key.rotation_euler = (math.radians(42), 0.0, math.radians(36))
    bpy.ops.object.light_add(type="SUN", location=(-3.0, 2.0, 4.0))
    sun = bpy.context.active_object
    sun.data.energy = 3.0
    sun.data.angle = math.radians(4)
    sun.rotation_euler = (math.radians(46), 0.0, math.radians(-52))
    world = bpy.data.worlds.new("LevelWorld")
    world.use_nodes = True
    background = world.node_tree.nodes.get("Background")
    if background is not None:
        background.inputs[0].default_value = (0.52, 0.64, 0.82, 1.0)
        background.inputs[1].default_value = 0.65
    bpy.context.scene.world = world


def new_camera(camera_doc):
    """A camera posed from ``cameras[]``; ``ortho_top`` is derived from target + 12 m."""
    data = bpy.data.cameras.new("CAM_" + camera_doc["id"])
    obj = bpy.data.objects.new("CAM_" + camera_doc["id"], data)
    bpy.context.scene.collection.objects.link(obj)
    if camera_doc.get("type") == "ortho_top":
        data.type = "ORTHO"
        data.ortho_scale = float(camera_doc.get("coverage_m", 10.0))
        target = camera_doc["target"]
        obj.location = Vector(core.unity_to_blender_position(
            (target[0], float(camera_doc.get("height_m", 12.0)), target[2])))
        obj.rotation_euler = (0.0, 0.0, 0.0)
        obj["cam_type"] = "ortho_top"
        obj["cam_coverage_m"] = float(camera_doc.get("coverage_m", 10.0))
    else:
        location = Vector(core.unity_to_blender_position(tuple(camera_doc["location"])))
        target = Vector(core.unity_to_blender_position(tuple(camera_doc["target"])))
        data.lens = float(camera_doc.get("lens", 45.0))
        obj.location = location
        obj.rotation_euler = (target - location).to_track_quat("-Z", "Y").to_euler()
        obj["cam_type"] = "perspective"
        obj["cam_lens"] = float(camera_doc.get("lens", 45.0))
        obj["cam_target"] = list(camera_doc["target"])
    return obj


def build_from_doc(doc, exports_dir=None, with_lights=True):
    """Structures + props (+ cameras, lights) for a document; the whole scene build."""
    defs = [prop["def"] for prop in doc.get("props") or []]
    library = load_library(defs, exports_dir) if defs else {}
    structures = build_structures(doc)
    props = build_props(doc, library, exports_dir)
    cameras = {camera["id"]: new_camera(camera) for camera in doc.get("cameras") or []}
    if with_lights:
        add_lights()
    bpy.context.view_layer.update()
    return {"library": library, "structures": structures, "props": props, "cameras": cameras}


# --------------------------------------------------------------------------- #
# scene -> JSON
# --------------------------------------------------------------------------- #
def pose_of(obj):
    """``(position, quat)`` in Unity coordinates, read from the evaluated matrix."""
    matrix = obj.matrix_world
    position = core.blender_to_unity_position(tuple(matrix.translation))
    quaternion = matrix.to_quaternion()
    quat = core.blender_to_unity_quat((quaternion.w, quaternion.x, quaternion.y, quaternion.z))
    return tuple(position), core.canonical_quat(tuple(quat))


def scene_props(library_collections=None):
    """Prop objects (collection instances) in itemId order.

    ``library_collections`` narrows the set to the library the caller loaded;
    without it every ``item_def``-tagged or library-instancing empty counts, which
    is what a scene authored in the Blender GUI looks like.
    """
    found = []
    for obj in bpy.data.objects:
        if obj.type != "EMPTY" or obj.instance_collection is None:
            continue
        if library_collections is not None and len(library_collections) > 0:
            if obj.instance_collection in library_collections:
                found.append(obj)
            continue
        if "item_def" in obj.keys() or obj.instance_collection.name.startswith("LIB_"):
            found.append(obj)
    return sorted(found, key=lambda obj: obj.name)


def scene_structures():
    """Objects tagged with a structure role (``build_structures`` sets ``role``)."""
    found = [obj for obj in bpy.data.objects
             if obj.type == "MESH" and obj.get("role") in core.PROP_ROLES]
    return sorted(found, key=lambda obj: obj.name)


def structure_size_u(obj):
    """The box's Unity size from its own local bounds (scale 1 by construction)."""
    dims = obj.dimensions
    return (dims[0], dims[2], dims[1])


def library_def(obj):
    if "item_def" in obj.keys():
        return str(obj["item_def"])
    collection = obj.instance_collection
    if collection is not None and collection.name.startswith("LIB_"):
        return collection.name[len("LIB_"):]
    return None


def seat_scene_props(structure_objects):
    """Turn every prop onto its declared support surface (the fix for half-buried props).

    Reads ``support`` / ``stacked_on`` custom properties, poses the *structure*
    from the scene, and uses ``placement_core`` for the actual maths.  Stacked
    props are seated in a second pass, on the top of the prop they name.
    Returns the list of itemIds it moved.
    """
    poses = {}
    for structure_id, obj in structure_objects.items():
        position, quat = pose_of(obj)
        poses[structure_id] = {"id": structure_id, "center": position,
                               "size": structure_size_u(obj), "rotationQuat": quat}

    def apply(obj, position, quat):
        obj.location = Vector(core.unity_to_blender_position(position))
        q = core.unity_to_blender_quat(quat)
        obj.rotation_quaternion = Quaternion((q[0], q[1], q[2], q[3]))

    moved = []
    props = scene_props()
    boxes = {}
    for obj in props:
        position, quat = pose_of(obj)
        size = prop_size_u(obj)
        if size is not None:
            boxes[obj.name] = core.world_aabb(position, size, quat)
    for obj in props:
        if "stacked_on" in obj.keys():
            continue
        support = obj.get("support")
        if not support or support not in poses:
            continue
        position, quat = pose_of(obj)
        size = prop_size_u(obj)
        if size is None:
            continue
        normal, point = core.structure_top_plane(poses[support])
        new_position, new_quat = core.reseat(position, size, quat, normal, point)
        if _same_pose(position, quat, new_position, new_quat):
            continue
        apply(obj, new_position, new_quat)
        moved.append(obj.name)
    bpy.context.view_layer.update()

    for obj in props:
        other = obj.get("stacked_on")
        if not other or other not in boxes:
            continue
        position, quat = pose_of(obj)
        size = prop_size_u(obj)
        if size is None:
            continue
        top = boxes[other][1][1]
        normal, point = (0.0, 1.0, 0.0), (0.0, top, 0.0)
        new_position, new_quat = core.reseat(position, size, quat, normal, point)
        if _same_pose(position, quat, new_position, new_quat):
            continue
        apply(obj, new_position, new_quat)
        moved.append(obj.name)
    bpy.context.view_layer.update()
    return sorted(set(moved))


def _same_pose(position, quat, new_position, new_quat):
    """Compare at the JSON's precision, so float32 storage noise is not a "move"."""
    return (max(abs(round(a, core.POSITION_DECIMALS) - round(b, core.POSITION_DECIMALS))
                for a, b in zip(position, new_position)) == 0.0
            and max(abs(round(a, core.QUAT_DECIMALS) - round(b, core.QUAT_DECIMALS))
                    for a, b in zip(quat, new_quat)) == 0.0)


def prop_size_u(obj):
    """World-free model size in Unity axes, measured from the instanced collection."""
    collection = obj.instance_collection
    if collection is None:
        return None
    minimum = Vector((1e18, 1e18, 1e18))
    maximum = Vector((-1e18, -1e18, -1e18))
    found = False
    for member in collection.objects:
        if member.type != "MESH":
            continue
        found = True
        for corner in member.bound_box:
            point = member.matrix_world @ Vector(corner)
            for axis in range(3):
                minimum[axis] = min(minimum[axis], point[axis])
                maximum[axis] = max(maximum[axis], point[axis])
    if not found:
        return None
    extents = maximum - minimum
    # Blender local axes -> Unity (x, y=height, z)
    return (extents.x, extents.z, extents.y)


# --------------------------------------------------------------------------- #
# render
# --------------------------------------------------------------------------- #
def select_engine(scene, logical):
    """Map the JSON's logical engine name onto this Blender's real identifier."""
    logical = (logical or "WORKBENCH").upper()
    if logical == "EEVEE":
        return prop_lib().select_eevee_engine(scene)
    if logical == "WORKBENCH":
        scene.render.engine = "BLENDER_WORKBENCH"
        return "BLENDER_WORKBENCH"
    if logical == "CYCLES":
        scene.render.engine = "CYCLES"
        try:
            scene.cycles.device = "CPU"
            # The 4.0.2 distro build has no OpenImageDenoise; asking for it aborts
            # the render ("Build without OpenImageDenoiser").
            scene.cycles.use_denoising = False
        except AttributeError:
            pass
        return "CYCLES"
    raise SystemExit("unknown render engine %r (use WORKBENCH, EEVEE or CYCLES)" % logical)


def configure_shading(scene, logical, samples=0):
    """Per-engine settings, each guarded because 4.0 and 5.3 do not agree."""
    logical = (logical or "WORKBENCH").upper()
    if logical == "WORKBENCH":
        shading = scene.display.shading
        shading.light = "STUDIO"
        shading.color_type = "MATERIAL"
        shading.show_shadows = True
        shading.show_cavity = True
        return
    if logical == "CYCLES":
        if samples:
            scene.cycles.samples = samples
        return
    eevee = getattr(scene, "eevee", None)
    if eevee is None:
        return
    if samples:
        for attribute in ("taa_render_samples", "samples"):
            if hasattr(eevee, attribute):
                setattr(eevee, attribute, samples)
                break
    if hasattr(eevee, "use_gtao"):
        eevee.use_gtao = True
    scene.render.film_transparent = False


def configure_output(scene, res):
    scene.render.resolution_x = int(res[0])
    scene.render.resolution_y = int(res[1])
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.render.image_settings.color_depth = "8"
    scene.render.image_settings.compression = 15
    try:
        scene.view_settings.view_transform = "Standard"
    except TypeError:
        pass


def camera_for(doc, camera_id):
    """Find or create the camera object for ``camera_id``."""
    name = "CAM_" + camera_id
    obj = bpy.data.objects.get(name)
    if obj is not None:
        return obj
    for camera in doc.get("cameras") or []:
        if camera["id"] == camera_id:
            return new_camera(camera)
    raise SystemExit("camera %r is not in the document" % camera_id)


def select_presets(doc, requested):
    presets = doc.get("renderPresets") or []
    if not presets:
        raise SystemExit("the document has no renderPresets")
    if requested in (None, "", "all"):
        return presets
    wanted = [name.strip() for name in requested.split(",") if name.strip()]
    known = {preset["id"] for preset in presets}
    missing = [name for name in wanted if name not in known]
    if missing:
        raise SystemExit("unknown preset(s) %s; known: %s"
                         % (", ".join(missing), ", ".join(sorted(known))))
    return [preset for preset in presets if preset["id"] in wanted]
