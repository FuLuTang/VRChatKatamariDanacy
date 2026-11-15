"""Shared material for the level-authoring Blender scripts *and* the resident editor.

Imported by ``build_scene.py``, ``export_placement.py``, ``render_views.py`` and
``editor_server.py`` (never run on its own).  It owns exactly the bpy-facing glue; every
geometric rule - axes, quaternions, AABB, seating, the absorb gate - comes from
``Tools/levelbuild/placement_core.py`` so the render, the validator and the Unity import
cannot disagree.

Two layers, on purpose:

* the **primitives** (:func:`new_prop_object`, :func:`update_prop_object`,
  :func:`new_structure_object`, :func:`update_structure_object`, :func:`remove_object`,
  :func:`ensure_library`) build or change **one object**;
* :class:`LevelScene` drives them from a document, **diffing against the previous
  document** - so the one-shot scripts and the resident editor share one implementation
  of "make the scene match the JSON", and an edit touches only the objects it changed.

One script must run on both Blender builds the project uses: 4.0.2 (Ubuntu arm64,
headless, no GPU) and 5.3.0 (Windows, the user's GUI copy).  Version differences are
concentrated in :func:`select_engine` and :func:`configure_shading`; the EEVEE engine id
itself is resolved by the prop pipeline's own probe
(``Docs/Art/OriginalModels/props/scripts/prop_lib.py``, ``select_eevee_engine``) instead
of being re-implemented here.
"""

import importlib.util
import math
import sys
import time
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
# one object: create / update / remove
# --------------------------------------------------------------------------- #
def _material(name, rgba):
    # Workbench uses material.diffuse_color, EEVEE/Cycles use the Principled node;
    # a material that only sets the node renders grey in the plan view (measured).
    material = bpy.data.materials.new("MAT_" + name)
    material.use_nodes = True
    _set_material_color(material, rgba)
    return material


def _set_material_color(material, rgba):
    material.diffuse_color = rgba
    if not material.use_nodes or material.node_tree is None:
        return
    principled = next((node for node in material.node_tree.nodes
                       if node.type == "BSDF_PRINCIPLED"), None)
    if principled is not None:
        principled.inputs["Base Color"].default_value = rgba
        if "Roughness" in principled.inputs:
            principled.inputs["Roughness"].default_value = 0.85


def axis_matrix():
    """``M``: Blender (x, y, z) -> Unity (x, z, y) as a 4x4."""
    return Matrix(((1.0, 0.0, 0.0, 0.0),
                   (0.0, 0.0, 1.0, 0.0),
                   (0.0, 1.0, 0.0, 0.0),
                   (0.0, 0.0, 0.0, 1.0)))


#: (0, 3, 2, 1) is the bottom face of the vertex list below - the winding is what
#: makes the box render from outside instead of inside.
BOX_FACES = ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7))


def _box_verts(size_u):
    sx, sy, sz = size_u[0] * 0.5, size_u[2] * 0.5, size_u[1] * 0.5
    return [(-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz),
            (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz)]


def _box_mesh(name, size_u):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(_box_verts(size_u), [], list(BOX_FACES))
    mesh.update()
    return mesh


def _box_matrix(center_u, quat_u):
    # The quaternion goes in with its JSON component order (w, x, y, z) and *not*
    # through unity_to_blender_quat: the mesh above is already built in Unity axis
    # order, so the M^-1 ... M conjugation performs the axis change itself.  Adding
    # the sign flip here too would apply it twice (measured: the crate ended up
    # rotated about the wrong axis, `LEVEL_AUTHORING` 1.5).
    raw = Quaternion((quat_u[3], quat_u[0], quat_u[1], quat_u[2]))
    m = axis_matrix()
    return (m.inverted()
            @ Matrix.Translation(Vector(center_u))
            @ raw.to_matrix().to_4x4()
            @ m)


def place_box(obj, center_u, quat_u):
    obj.matrix_world = _box_matrix(center_u, quat_u)


def new_box(name, center_u, size_u, quat_u, rgba):
    """A unit-cube-style box built in Unity axis order and posed in Blender space."""
    obj = bpy.data.objects.new(name, _box_mesh(name, size_u))
    bpy.context.scene.collection.objects.link(obj)
    place_box(obj, center_u, quat_u)
    obj["box_size"] = list(size_u)
    obj.data.materials.append(_material(name, rgba))
    return obj


def update_box(obj, center_u, size_u, quat_u, rgba):
    """Re-pose, rebuild the mesh only when the size changed (keeps the material)."""
    previous = tuple(obj.get("box_size") or ())
    if len(previous) != 3 or max(abs(previous[i] - size_u[i]) for i in range(3)) > 1e-6:
        old = obj.data
        obj.data = _box_mesh(old.name, size_u)
        if old.users == 0:
            bpy.data.meshes.remove(old)
        obj["box_size"] = list(size_u)
    place_box(obj, center_u, quat_u)
    if len(obj.data.materials) == 0:
        obj.data.materials.append(_material(obj.name, rgba))
    elif max(abs(float(obj.data.materials[0].diffuse_color[i]) - rgba[i])
             for i in range(4)) > 1e-6:
        _set_material_color(obj.data.materials[0], rgba)


def remove_object(obj):
    """Delete one object (and its mesh/camera data when nothing else uses it)."""
    data = obj.data
    identity = obj.name
    bpy.data.objects.remove(obj, do_unlink=True)
    if data is None or data.users != 0:
        return identity
    if isinstance(data, bpy.types.Mesh):
        bpy.data.meshes.remove(data)
    elif isinstance(data, bpy.types.Camera):
        bpy.data.cameras.remove(data)
    return identity


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


#: prop JSON field -> Blender custom property (the exporter reads them back).
PROP_TAGS = (("zone", "zone"), ("kind", "kind"), ("support", "support"),
             ("stackedOn", "stacked_on"), ("valueUnits", "value_units"),
             ("group", "group"), ("notes", "notes"))

#: structure JSON field -> Blender custom property.
STRUCTURE_TAGS = (("kind", "struct_kind"), ("collision", "struct_collision"),
                  ("meets", "struct_meets"), ("material", "struct_material"),
                  ("notes", "struct_notes"))


def tag_prop(obj, prop):
    """Mirror the JSON's non-geometric fields onto the object (export reads them)."""
    obj["item_def"] = prop["def"]
    for key, target in PROP_TAGS:
        if key in prop:
            obj[target] = prop[key]
        elif target in obj.keys():
            del obj[target]


def tag_structure(obj, structure):
    obj["role"] = structure.get("role")
    obj["shape"] = structure.get("shape", "box")
    obj["structure_id"] = structure["id"]
    for key, target in STRUCTURE_TAGS:
        if key in structure:
            obj[target] = structure[key]
        elif target in obj.keys():
            del obj[target]
    obj["absorbable"] = bool(structure.get("absorbable", False))
    obj["support"] = bool(structure.get("support", False))


def new_prop_object(prop, library):
    """One prop object (library instance + tags) from its JSON entry."""
    collection = library.get(prop["def"])
    if collection is None:
        raise RuntimeError("prop %s uses def %r which is not in the library"
                           % (prop.get("itemId"), prop.get("def")))
    obj = new_prop_instance(prop["itemId"], collection, tuple(prop["position"]),
                            tuple(prop["rotationQuat"]))
    tag_prop(obj, prop)
    return obj


def update_prop_object(obj, prop, library):
    """Re-pose an existing prop and re-tag it (the mesh is the library's)."""
    collection = library.get(prop["def"])
    if collection is not None:
        obj.instance_collection = collection
    obj.location = Vector(core.unity_to_blender_position(tuple(prop["position"])))
    q = core.unity_to_blender_quat(tuple(prop["rotationQuat"]))
    obj.rotation_quaternion = Quaternion((q[0], q[1], q[2], q[3]))
    tag_prop(obj, prop)


def _structure_color(structure):
    return (KIND_COLORS.get(structure.get("kind"))
            or ROLE_COLORS.get(structure.get("role"), (0.5, 0.5, 0.5, 1.0)))


def new_structure_object(structure):
    obj = new_box(structure["id"], tuple(structure["center"]), tuple(structure["size"]),
                  tuple(structure["rotationQuat"]), _structure_color(structure))
    tag_structure(obj, structure)
    return obj


def update_structure_object(obj, structure):
    update_box(obj, tuple(structure["center"]), tuple(structure["size"]),
               tuple(structure["rotationQuat"]), _structure_color(structure))
    tag_structure(obj, structure)


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


def new_camera(camera_doc, name=None):
    """A camera posed from ``cameras[]``; ``ortho_top`` is derived from target + 12 m."""
    name = name or ("CAM_" + camera_doc["id"])
    data = bpy.data.cameras.new(name)
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    update_camera_object(obj, camera_doc)
    return obj


def update_camera_object(obj, camera_doc):
    """Apply one ``cameras[]`` entry to an existing camera object (type may change)."""
    data = obj.data
    if camera_doc.get("type") == "ortho_top":
        data.type = "ORTHO"
        data.ortho_scale = float(camera_doc.get("coverage_m", 10.0))
        target = camera_doc["target"]
        # The authored ``location`` is the pose; ``height_m`` is only the fallback for a
        # camera dict that has none.  The old code always used height (default 12 m),
        # which made a JSON -> scene -> JSON round trip rewrite story_01's top camera
        # from y=14 to y=12 (measured; the picture is identical because an ortho camera
        # frames by ``ortho_scale``, but the data was silently lost).
        location = camera_doc.get("location")
        height = float(location[1]) if location else float(camera_doc.get("height_m", 12.0))
        x = float(location[0]) if location else float(target[0])
        z = float(location[2]) if location else float(target[2])
        obj.location = Vector(core.unity_to_blender_position((x, height, z)))
        obj.rotation_euler = (0.0, 0.0, 0.0)
        obj["cam_type"] = "ortho_top"
        obj["cam_coverage_m"] = float(camera_doc.get("coverage_m", 10.0))
        obj["cam_height_m"] = height
        if "cam_target" in obj.keys():
            del obj["cam_target"]
        if "cam_lens" in obj.keys():
            del obj["cam_lens"]
        return
    data.type = "PERSP"
    location = Vector(core.unity_to_blender_position(tuple(camera_doc["location"])))
    target = Vector(core.unity_to_blender_position(tuple(camera_doc["target"])))
    data.lens = float(camera_doc.get("lens", 45.0))
    obj.location = location
    obj.rotation_euler = (target - location).to_track_quat("-Z", "Y").to_euler()
    obj["cam_type"] = "perspective"
    obj["cam_lens"] = float(camera_doc.get("lens", 45.0))
    obj["cam_target"] = list(camera_doc["target"])
    if "cam_coverage_m" in obj.keys():
        del obj["cam_coverage_m"]


# --------------------------------------------------------------------------- #
# the scene as a projection of the document
# --------------------------------------------------------------------------- #
class LevelScene:
    """The level's objects in the current Blender scene, keyed like the JSON.

    ``sync(doc, previous)`` is the whole of "make the scene match the document": it
    diffs the two documents and creates, updates or deletes the objects that differ.
    A first build passes ``previous=None`` (everything is new); the resident editor
    passes the document before the edit, so moving one prop touches one object.
    """

    def __init__(self, exports_dir=None, with_lights=True):
        self.exports_dir = Path(exports_dir) if exports_dir else Path(core.PROPS_EXPORTS_DIR)
        self.manifests = core.load_manifests(self.exports_dir)
        self.library = {}
        self.props = {}
        self.structures = {}
        self.cameras = {}
        self.with_lights = with_lights
        self.lights_added = False

    # -- library -------------------------------------------------------- #
    def ensure_library(self, def_ids):
        """Import only the defs that are not loaded yet; returns the library map."""
        missing = sorted(set(def_ids) - set(self.library))
        if missing:
            try:
                self.library.update(load_library(missing, self.exports_dir))
            except SystemExit as error:                  # load_library's own refusal
                raise RuntimeError(str(error))
        return self.library

    # -- lifecycle ------------------------------------------------------ #
    def reset(self):
        """Remove every object this scene owns; the library and lights stay."""
        for table in (self.props, self.structures, self.cameras):
            for obj in list(table.values()):
                if obj is not None:
                    try:
                        remove_object(obj)
                    except ReferenceError:
                        pass
            table.clear()

    def _object_count(self):
        return {"props": len(self.props), "structures": len(self.structures),
                "cameras": len(self.cameras), "library": len(self.library)}

    # -- the diff ------------------------------------------------------- #
    def sync(self, doc, previous=None):
        previous = previous or {}
        counts = {kind: {"added": 0, "updated": 0, "removed": 0}
                  for kind in ("props", "structures", "cameras")}
        desired_props = {prop["itemId"]: prop for prop in doc.get("props") or []
                         if isinstance(prop, dict) and prop.get("itemId")}
        before_props = {prop.get("itemId"): prop for prop in previous.get("props") or []
                        if isinstance(prop, dict)}
        self.ensure_library(prop["def"] for prop in desired_props.values()
                            if prop.get("def"))

        for item_id in sorted(set(self.props) - set(desired_props)):
            remove_object(self.props.pop(item_id))
            counts["props"]["removed"] += 1
        for item_id, prop in desired_props.items():
            obj = self.props.get(item_id)
            old = before_props.get(item_id)
            if obj is not None and old is not None and old.get("def") != prop.get("def"):
                remove_object(obj)
                self.props.pop(item_id)
                obj = None
            if obj is None:
                self.props[item_id] = new_prop_object(prop, self.library)
                self._warn_aabb(prop)
                counts["props"]["added"] += 1
            elif old != prop:
                update_prop_object(obj, prop, self.library)
                counts["props"]["updated"] += 1

        desired_structures = {structure["id"]: structure
                              for structure in doc.get("structures") or []
                              if isinstance(structure, dict) and structure.get("id")}
        before_structures = {structure.get("id"): structure
                             for structure in previous.get("structures") or []
                             if isinstance(structure, dict)}
        for structure_id in sorted(set(self.structures) - set(desired_structures)):
            remove_object(self.structures.pop(structure_id))
            counts["structures"]["removed"] += 1
        for structure_id, structure in desired_structures.items():
            obj = self.structures.get(structure_id)
            if obj is None:
                self.structures[structure_id] = new_structure_object(structure)
                counts["structures"]["added"] += 1
            elif before_structures.get(structure_id) != structure:
                update_structure_object(obj, structure)
                counts["structures"]["updated"] += 1

        desired_cameras = {camera["id"]: camera for camera in doc.get("cameras") or []
                           if isinstance(camera, dict) and camera.get("id")}
        before_cameras = {camera.get("id"): camera
                          for camera in previous.get("cameras") or []
                          if isinstance(camera, dict)}
        for camera_id in sorted(set(self.cameras) - set(desired_cameras)):
            remove_object(self.cameras.pop(camera_id))
            counts["cameras"]["removed"] += 1
        for camera_id, camera in desired_cameras.items():
            obj = self.cameras.get(camera_id)
            wanted_type = camera.get("type", "perspective")
            if obj is not None and obj.get("cam_type") != wanted_type:
                remove_object(obj)
                self.cameras.pop(camera_id)
                obj = None
            if obj is None:
                self.cameras[camera_id] = new_camera(camera)
                counts["cameras"]["added"] += 1
            elif before_cameras.get(camera_id) != camera:
                update_camera_object(obj, camera)
                counts["cameras"]["updated"] += 1

        if self.with_lights and not self.lights_added:
            add_lights()
            self.lights_added = True
        bpy.context.view_layer.update()
        return counts

    def _warn_aabb(self, prop):
        """The old one-shot builder warned here; keep that second pair of eyes."""
        manifest = self.manifests.get(prop.get("def"))
        if not manifest:
            return
        try:
            declared = tuple(float(v) for v in prop.get("aabb") or ())
        except (TypeError, ValueError):
            return
        if len(declared) != 3:
            return
        if max(abs(declared[i] - manifest["aabb"][i]) for i in range(3)) > 0.001:
            print("WARN aabb %s: json %s vs manifest %s"
                  % (prop.get("itemId"), declared, manifest["aabb"]), flush=True)

    # -- render --------------------------------------------------------- #
    def render(self, spec, doc):
        """Render one frame; an inline (non-preset) camera is temporary."""
        res = [int(v) for v in (spec.get("res") or [1280, 720])]
        camera_doc = spec["camera"]
        temporary = None
        if spec.get("preset") is None:
            temporary = new_camera(camera_doc, name="CAM_adhoc")
            camera = temporary
        else:
            camera = self.cameras.get(camera_doc.get("id"))
            if camera is None:
                camera = new_camera(camera_doc)
                temporary = camera
        try:
            engine_id, seconds = render_frame(
                bpy.context.scene, camera, spec["path"],
                spec.get("engine") or "WORKBENCH", res, int(spec.get("samples") or 0))
        finally:
            if temporary is not None:
                remove_object(temporary)
        return {"path": str(spec["path"]), "engine_id": engine_id,
                "seconds": seconds, "res": res}

    # -- facts for `status` --------------------------------------------- #
    def status(self):
        engines = []
        for logical in core.ENGINES:
            engines.append(logical)
        return {
            "blender": bpy.app.version_string,
            "scene": bpy.context.scene.name,
            "objects": self._object_count(),
            "renderEngine": bpy.context.scene.render.engine,
            "engines": engines,
            "manifests": len(self.manifests),
        }


def build_from_doc(doc, exports_dir=None, with_lights=True):
    """Build the whole scene for a document (the one-shot scripts' entry point)."""
    scene = LevelScene(exports_dir, with_lights=with_lights)
    counts = scene.sync(doc)
    bpy.context.view_layer.update()
    return {"library": scene.library, "structures": scene.structures,
            "props": scene.props, "cameras": scene.cameras, "counts": counts}


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
    """Objects tagged with a structure role (``tag_structure`` sets ``role``)."""
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
    # Quantise every input to the precision the JSON stores before doing the maths.
    # The scene holds float32, so a structure top of 0.26 comes back as 0.26000002;
    # a prop whose exact seat lands on a 4-decimal rounding boundary (story_01's
    # bread_slice and grapes on the flower bed: top + h/2 = 0.26715) then rounds the
    # other way and every ``--seat`` run would rewrite the file by 0.1 mm (measured).
    # Feeding the maths the same numbers the file holds makes seating idempotent and
    # agrees with ``editor_core.seat_props``, which reads the JSON.
    def quantise(values, digits):
        return tuple(core.rounded(value, digits) for value in values)

    poses = {}
    for structure_id, obj in structure_objects.items():
        position, quat = pose_of(obj)
        poses[structure_id] = {
            "id": structure_id,
            "center": quantise(position, core.POSITION_DECIMALS),
            "size": quantise(structure_size_u(obj), core.SIZE_DECIMALS),
            "rotationQuat": quantise(quat, core.QUAT_DECIMALS)}

    def apply(obj, position, quat):
        obj.location = Vector(core.unity_to_blender_position(position))
        q = core.unity_to_blender_quat(quat)
        obj.rotation_quaternion = Quaternion((q[0], q[1], q[2], q[3]))

    # The ground plane is a structure like any other (role=ground).  The JSON path
    # (``editor_core.seat_props`` / ``levelbuild.py seat``) seats an unsupported prop on
    # ``level.groundY``; the scene path used to skip those props entirely, so a displaced
    # ground prop was never repaired by ``export_placement.py --seat`` (measured: an
    # acorn 5 cm up came back unchanged).  Use the scene's own ground so both agree.
    ground = None
    for structure_id, obj in structure_objects.items():
        if obj.get("role") == "ground":
            ground = poses.get(structure_id)
            break

    moved = []
    props = scene_props()
    boxes = {}
    for obj in props:
        position, quat = pose_of(obj)
        size = prop_size_u(obj)
        if size is not None:
            boxes[obj.name] = core.world_aabb(
                quantise(position, core.POSITION_DECIMALS),
                quantise(size, core.SIZE_DECIMALS),
                quantise(quat, core.QUAT_DECIMALS))
    for obj in props:
        if "stacked_on" in obj.keys():
            continue
        support = obj.get("support")
        if support and support in poses:
            normal, point = core.structure_top_plane(poses[support])
        elif ground is not None:
            # role=ground is a plane *through the centre* (a solid slab would eat the box
            # budget), exactly what placement_core.ground_plane / ground_level use.
            normal, point = core.boundary_plane(ground)
        else:
            continue
        position, quat = pose_of(obj)
        position = quantise(position, core.POSITION_DECIMALS)
        quat = quantise(quat, core.QUAT_DECIMALS)
        size = prop_size_u(obj)
        if size is None:
            continue
        size = quantise(size, core.SIZE_DECIMALS)
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
        position = quantise(position, core.POSITION_DECIMALS)
        quat = quantise(quat, core.QUAT_DECIMALS)
        size = prop_size_u(obj)
        if size is None:
            continue
        size = quantise(size, core.SIZE_DECIMALS)
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


def render_frame(scene, camera, out_path, engine, res, samples=0):
    """Configure the scene for one engine/resolution and render one frame.

    Shared by ``render_views.py`` (one-shot, whole level) and the resident editor's
    ``render`` op, so "how a preset becomes a PNG" exists once.  Returns
    ``(engine_id, seconds)``.
    """
    engine_id = select_engine(scene, engine)
    configure_shading(scene, engine, samples)
    configure_output(scene, res)
    scene.camera = camera
    fit_ortho_to_frame(camera, res)
    target = Path(out_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    scene.render.filepath = str(target)
    started = time.time()
    bpy.ops.render.render(write_still=True)
    elapsed = time.time() - started
    if not target.is_file() or target.stat().st_size == 0:
        raise RuntimeError("the render wrote no image to %s" % target)
    return engine_id, round(elapsed, 2)


def fit_ortho_to_frame(camera, res):
    """Make ``coverage_m`` the extent guaranteed to be in frame on the *shorter* side.

    Blender's ``ortho_scale`` is the extent along the sensor's longer side, so a 16:9
    plan view of a square arena would silently crop the top and bottom (measured: a
    13.4 m arena showed only 7.9 m of depth).  The camera data keeps the authored
    coverage; the per-preset resolution decides the scale.  Reads the authored value
    from the custom property, so rendering the same camera at two resolutions is
    stable and not cumulative.
    """
    data = camera.data
    if data.type != "ORTHO":
        return
    authored = float(camera.get("cam_coverage_m", data.ortho_scale))
    aspect = float(res[0]) / float(res[1]) if res[1] else 1.0
    data.ortho_scale = authored * max(1.0, aspect)


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
