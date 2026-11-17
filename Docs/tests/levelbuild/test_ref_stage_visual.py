"""The reference level's visual stage: ``stage.glb`` -> ``.katvisual`` + PNG + ``terrain.visual``.

``Tools/levelbuild/ref_stage_visual.py`` converts the original's graphic sectors.  What would
fail silently, and is pinned here:

* the frame map ``(-0.01x, 0.01y, 0.01z)`` **and** the winding flip + normal negation that go
  with it (x is mirrored): a reversed mesh culls to "no ground", which is what the owner saw;
* material parts merge per texture, the PNG alpha channel decides ``opaque/cutout/alpha``;
* the output is deterministic and the validator (``terrain.visual``'s tag set) accepts it and
  rejects each kind of damage with its own tag;
* the guards: nothing is read outside ``~/KatamariRef`` and nothing is written outside a
  ``_ref_*`` level directory;
* against the real extraction (skipped when it is not on this machine): the counts, the
  agreement of winding and normals, a sky shell visible from inside, and the contact terrain
  sidecar left byte-identical.
"""

import copy
import hashlib
import json
import math
import shutil
import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_v2  # noqa: E402
from Tools.levelbuild import ref_stage  # noqa: E402
from Tools.levelbuild import ref_stage_visual as visual  # noqa: E402
from Tools.levelbuild import ref_v2_from_extract as build  # noqa: E402
from Docs.tests.levelbuild.test_placement_v2 import tiny_document  # noqa: E402

REAL_GLB = visual.DEFAULT_SOURCE
COLLISION_SIDECAR_SHA256 = "3fb174579c3ce0bb1ec3036bd50dce02158e5ab792025b9f8a0471b046acc752"


# --------------------------------------------------------------------------- #
# a tiny exporter-shaped GLB
# --------------------------------------------------------------------------- #
def png(width, height, alphas):
    """An 8-bit RGBA PNG whose pixels cycle through ``alphas`` (filter type 0 rows)."""
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))
    raw = bytearray()
    cursor = 0
    for _row in range(height):
        raw.append(0)
        for _column in range(width):
            raw += bytes((200, 100, 50, alphas[cursor % len(alphas)]))
            cursor += 1
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


class Glb:
    def __init__(self):
        self.document = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": []}],
                         "nodes": [], "meshes": [], "materials": [], "textures": [],
                         "images": [], "samplers": [{}], "accessors": [], "bufferViews": [],
                         "buffers": []}
        self.binary = bytearray()

    def view(self, data):
        while len(self.binary) % 4:
            self.binary.append(0)
        self.document["bufferViews"].append({"buffer": 0, "byteOffset": len(self.binary),
                                             "byteLength": len(data)})
        self.binary += data
        return len(self.document["bufferViews"]) - 1

    def accessor(self, data, component, count, kind):
        self.document["accessors"].append({"bufferView": self.view(data),
                                           "componentType": component, "count": count,
                                           "type": kind})
        return len(self.document["accessors"]) - 1

    def texture(self, name, data):
        self.document["images"].append({"bufferView": self.view(data), "mimeType": "image/png",
                                        "name": name})
        self.document["textures"].append({"source": len(self.document["images"]) - 1,
                                          "sampler": 0})
        return len(self.document["textures"]) - 1

    def model(self, name, positions, normals, uvs, parts):
        """``parts``: ``[(texture index, [index triples...])...]`` - one primitive each."""
        primitives = []
        flat_p = b"".join(struct.pack("<3f", *p) for p in positions)
        flat_n = b"".join(struct.pack("<3f", *n) for n in normals)
        flat_u = b"".join(struct.pack("<2f", *u) for u in uvs)
        for texture, triangles in parts:
            self.document["materials"].append({
                "name": "m", "doubleSided": True,
                "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1],
                                         "baseColorTexture": {"index": texture}}})
            indices = [value for triple in triangles for value in triple]
            primitives.append({
                "attributes": {
                    "POSITION": self.accessor(flat_p, 5126, len(positions), "VEC3"),
                    "NORMAL": self.accessor(flat_n, 5126, len(normals), "VEC3"),
                    "TEXCOORD_0": self.accessor(flat_u, 5126, len(uvs), "VEC2")},
                "indices": self.accessor(struct.pack("<%dH" % len(indices), *indices), 5123,
                                         len(indices), "SCALAR"),
                "material": len(self.document["materials"]) - 1, "mode": 4})
        self.document["meshes"].append({"name": name, "primitives": primitives})
        self.document["nodes"].append({"mesh": len(self.document["meshes"]) - 1, "name": name})
        self.document["scenes"][0]["nodes"].append(len(self.document["nodes"]) - 1)

    def bytes(self):
        return ref_stage.write_glb(self.document, self.binary)


def synthetic_glb():
    """A 100 cm floor (two triangles, one degenerate third), a 'wall', and a sky shell.

    Extraction frame: PS2-order normals; a floor that faces *up* in ours has the extraction
    normal ``(0, -1, 0)`` and the extraction index order that faces down after the x mirror.
    """
    glb = Glb()
    opaque = glb.texture("floor", png(4, 4, [255]))
    cutout = glb.texture("fence", png(4, 4, [0, 255]))
    soft = glb.texture("glass", png(4, 4, [0, 120, 255]))
    # extraction cm: x in 0..100, z in 0..100, y 0; (-0.01x) puts x in -1..0
    floor_p = [(0, 0, 0), (100, 0, 0), (100, 0, 100), (0, 0, 100), (50, 0, 50)]
    floor_n = [(0, -1, 0)] * 5
    floor_uv = [(0, 0), (1, 0), (1, 1), (0, 1), (0.5, 0.5)]
    #   (0,2,1) and (0,3,2) are the order the extractor wrote; ours flips them
    glb.model("a2_s1_m0_model0", floor_p, floor_n, floor_uv,
              [(opaque, [(0, 2, 1)]), (cutout, [(0, 3, 2)]), (opaque, [(0, 1, 1)]),
               (opaque, [(1, 2, 4)])])
    glb.model("a2_s1_m1_model0", floor_p, floor_n, floor_uv, [(soft, [(0, 2, 1)])])
    # a "sky": one shell face; the extractor order (0,2,1) faces the origin once flipped
    sky_p = [(1000, 0, 0), (0, 1000, 0), (0, 0, 1000), (-1000, -1000, -1000)]
    sky_n = [(0, 0, 0)] * 4
    sky_uv = [(0, 0)] * 4
    glb.model("a2_s2_m0_model0", sky_p, sky_n, sky_uv, [(opaque, [(0, 2, 1)])])
    return glb.bytes()


class ConvertTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.built = visual.convert(synthetic_glb())
        cls.payload = placement_v2.read_visual_sidecar(cls.built["sidecar"])

    def test_positions_are_mirrored_in_x_and_scaled_to_metres(self):
        self.assertEqual(self.payload["counts"]["vertices"], 5 + 5 + 4)
        x, y, z = self.payload["vertices"][2]
        self.assertAlmostEqual(x, -1.0, places=6)      # -0.01 * 100
        self.assertAlmostEqual(y, 0.0, places=6)
        self.assertAlmostEqual(z, 1.0, places=6)       # +0.01 * 100

    def test_normals_are_negated(self):
        self.assertEqual(self.payload["normals"][0], (0.0, 1.0, 0.0))

    def test_the_winding_is_flipped_once_so_the_floor_faces_up(self):
        part = self.built["block"]["parts"][0]
        first = part["indexRange"][0]
        a, b, c = self.payload["indices"][first:first + 3]
        self.assertEqual((a, b, c), (0, 1, 2))          # (0,2,1) -> (a, c, b)
        vertices = self.payload["vertices"]
        cross = visual._cross(vertices[a], vertices[b], vertices[c])
        self.assertGreater(cross[1], 0.0)               # Unity front = numeric cross, up

    def test_degenerate_triangles_are_dropped_and_counted(self):
        self.assertEqual(self.built["report"]["degenerateDropped"], 1)
        part = self.built["block"]["parts"][0]
        self.assertEqual(part["indexRange"][1], 3 * 3)  # 4 prims - 1 degenerate = 3 triangles

    def test_primitives_with_one_texture_become_one_submesh(self):
        part = self.built["block"]["parts"][0]
        self.assertEqual([(s["material"], s["firstIndex"], s["indexCount"])
                          for s in part["submeshes"]],
                         [("t000", 0, 6), ("t001", 6, 3)])

    def test_roles_follow_the_sector(self):
        self.assertEqual([part["role"] for part in self.built["block"]["parts"]],
                         ["stage", "stage", "sky"])
        self.assertEqual(self.built["block"]["parts"][2]["id"], "area2_sector1_model0")

    def test_blend_comes_from_the_png_alpha(self):
        blends = {m["id"]: m["blend"] for m in self.built["block"]["materials"]}
        self.assertEqual(blends, {"t000": "opaque", "t001": "cutout", "t002": "alpha"})

    def test_the_sky_faces_the_origin(self):
        part = self.built["block"]["parts"][2]
        v0, count = part["vertexRange"]
        first = part["indexRange"][0]
        vertices = self.payload["vertices"][v0:v0 + count]
        a, b, c = self.payload["indices"][first:first + 3]
        cross = visual._cross(vertices[a], vertices[b], vertices[c])
        centre = [sum(vertices[i][k] for i in (a, b, c)) / 3 for k in range(3)]
        self.assertLess(sum(cross[k] * centre[k] for k in range(3)), 0.0)

    def test_conversion_is_deterministic(self):
        again = visual.convert(synthetic_glb())
        self.assertEqual(again["sidecar"], self.built["sidecar"])
        self.assertEqual(json.dumps(again["block"], sort_keys=False),
                         json.dumps(self.built["block"], sort_keys=False))
        self.assertEqual(again["textures"], self.built["textures"])

    def test_a_stripped_export_is_refused(self):
        glb = Glb()
        glb.document["textures"] = []
        glb.model("a2_s1_m0_model0", [(0, 0, 0), (1, 0, 0), (0, 0, 1)],
                  [(0, -1, 0)] * 3, [(0, 0)] * 3, [])
        with self.assertRaises(visual.RefVisualError):
            visual.convert(glb.bytes())

    def test_an_unknown_node_name_is_refused(self):
        glb = Glb()
        glb.texture("t", png(2, 2, [255]))
        glb.model("something_else", [(0, 0, 0), (1, 0, 0), (0, 0, 1)], [(0, -1, 0)] * 3,
                  [(0, 0)] * 3, [(0, [(0, 1, 2)])])
        with self.assertRaises(visual.RefVisualError):
            visual.convert(glb.bytes())

    def test_a_tinted_material_is_refused_instead_of_dropped(self):
        glb = Glb()
        glb.texture("t", png(2, 2, [255]))
        glb.model("a2_s1_m0_model0", [(0, 0, 0), (1, 0, 0), (0, 0, 1)], [(0, -1, 0)] * 3,
                  [(0, 0)] * 3, [(0, [(0, 1, 2)])])
        glb.document["materials"][0]["pbrMetallicRoughness"]["baseColorFactor"] = [1, 0, 0, 1]
        with self.assertRaises(visual.RefVisualError):
            visual.convert(glb.bytes())


class SidecarCodecTests(unittest.TestCase):
    #: The cross-language golden: one triangle, normals + uvs, index width 2.  The C# reader's
    #: test (TerrainVisualDataTests) decodes the same hex, so the two codecs cannot drift.
    GOLDEN_HEX = (
        "4b56495301000203030000000300000001000000000000000000000000000000"
        "0000803f000000000000000000000000000000000000803f000000000000803f"
        "00000000000000000000803f00000000000000000000803f0000000000000000"
        "000000000000803f00000000000000000000803f000001000200"
    )

    def test_round_trip(self):
        vertices = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0)]
        normals = [(0.0, 1.0, 0.0)] * 3
        uvs = [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0)]
        data = placement_v2.visual_sidecar_bytes(vertices, normals, uvs, [0, 1, 2], 1, 2)
        parsed = placement_v2.read_visual_sidecar(data)
        self.assertEqual(parsed["vertices"], vertices)
        self.assertEqual(parsed["normals"], normals)
        self.assertEqual(parsed["uvs"], uvs)
        self.assertEqual(parsed["indices"], [0, 1, 2])
        self.assertEqual(parsed["counts"], {"vertices": 3, "indices": 3, "triangles": 1,
                                            "parts": 1})
        self.assertEqual(parsed["trailing"], 0)

    def test_the_golden_bytes_are_what_the_writer_produces(self):
        data = placement_v2.visual_sidecar_bytes(
            [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0)], [(0.0, 1.0, 0.0)] * 3,
            [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0)], [0, 1, 2], 1, 2)
        self.assertEqual(data.hex(), self.GOLDEN_HEX)

    def test_header_layout_is_pinned(self):
        data = placement_v2.visual_sidecar_bytes([(0.0, 0.0, 0.0)] * 3, [(0.0, 1.0, 0.0)] * 3,
                                                 [(0.0, 0.0)] * 3, [0, 1, 2], 1, 2)
        self.assertEqual(data[:4], b"KVIS")
        self.assertEqual(struct.unpack_from("<HBB", data, 4), (1, 2, 3))
        self.assertEqual(struct.unpack_from("<III", data, 8), (3, 3, 1))
        self.assertEqual(len(data), 20 + 3 * 12 + 3 * 12 + 3 * 8 + 3 * 2)

    def test_garbage_is_a_sidecar_error(self):
        for blob in (b"", b"KTRN" + b"\0" * 20, b"KVIS" + b"\0" * 4):
            with self.assertRaises(placement_v2.SidecarError):
                placement_v2.read_visual_sidecar(blob)


# --------------------------------------------------------------------------- #
# the validator
# --------------------------------------------------------------------------- #
def with_visual(built):
    """A valid tiny document carrying ``built``'s block, plus the files for the validator."""
    document = tiny_document()
    terrain = {}
    for key, value in document["terrain"].items():
        if key == "source":
            terrain["visual"] = copy.deepcopy(built["block"])
        terrain[key] = value
    document["terrain"] = terrain
    files = dict(built["textures"])
    files[built["block"]["meshRef"]["path"]] = built["sidecar"]
    return document, files


def tags(report):
    return sorted({message.split("]")[0] + "]" for message in report.errors})


class ValidatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.built = visual.convert(synthetic_glb())

    def run_checks(self, mutate=None, files_mutate=None):
        document, files = with_visual(self.built)
        if mutate:
            mutate(document["terrain"]["visual"])
        if files_mutate:
            files_mutate(files)
        report = placement_v2.validate(document, visual_files=files)
        return [m for m in report.errors if m.startswith("[terrain_visual")]

    def test_a_converted_block_passes(self):
        self.assertEqual(self.run_checks(), [])

    def test_unknown_keys_are_errors_at_every_level(self):
        for mutate in (lambda v: v.update(extra=1),
                       lambda v: v["meshRef"].update(extra=1),
                       lambda v: v["parts"][0].update(extra=1),
                       lambda v: v["parts"][0]["bounds"].update(extra=1),
                       lambda v: v["parts"][0]["submeshes"][0].update(extra=1),
                       lambda v: v["materials"][0].update(extra=1)):
            self.assertTrue(any(m.startswith("[terrain_visual_key]")
                                for m in self.run_checks(mutate)), mutate)

    def test_sidecar_damage_is_reported(self):
        problems = self.run_checks(lambda v: v["meshRef"].update(sha256="0" * 64))
        self.assertTrue(any(m.startswith("[terrain_visual_sidecar]") and "sha256" in m
                            for m in problems))
        problems = self.run_checks(lambda v: v["meshRef"].update(vertexCount=1))
        self.assertTrue(any(m.startswith("[terrain_visual_sidecar]") for m in problems))
        problems = self.run_checks(lambda v: v["meshRef"].update(path="../x.katvisual"))
        self.assertTrue(any(m.startswith("[terrain_visual_sidecar]") for m in problems))

        def drop(files):
            files.pop(self.built["block"]["meshRef"]["path"])
        problems = self.run_checks(files_mutate=drop)
        self.assertTrue(any(m.startswith("[terrain_visual_sidecar]") for m in problems))

    def test_texture_damage_is_reported(self):
        first = self.built["block"]["materials"][0]["texture"]

        def corrupt(files):
            files[first] = files[first] + b"x"
        self.assertTrue(any(m.startswith("[terrain_visual_texture]")
                            for m in self.run_checks(files_mutate=corrupt)))

        def missing(files):
            files.pop(first)
        self.assertTrue(any(m.startswith("[terrain_visual_texture]")
                            for m in self.run_checks(files_mutate=missing)))
        problems = self.run_checks(lambda v: v["materials"][0].update(width=99))
        self.assertTrue(any(m.startswith("[terrain_visual_texture]") and "width/height" in m
                            for m in problems))

    def test_material_rules(self):
        problems = self.run_checks(lambda v: v["materials"][0].update(blend="additive"))
        self.assertTrue(any(m.startswith("[terrain_visual_material]") for m in problems))
        problems = self.run_checks(
            lambda v: v["parts"][0]["submeshes"][0].update(material="missing"))
        self.assertTrue(any(m.startswith("[terrain_visual_material]") for m in problems))
        problems = self.run_checks(lambda v: v.update(filter="trilinear"))
        self.assertTrue(any(m.startswith("[terrain_visual]") for m in problems))

    def test_part_rules(self):
        problems = self.run_checks(lambda v: v["parts"][0]["submeshes"][1].update(firstIndex=3))
        self.assertTrue(any(m.startswith("[terrain_visual_part]") for m in problems))
        problems = self.run_checks(lambda v: v["parts"][1].update(vertexRange=[99, 5]))
        self.assertTrue(any(m.startswith("[terrain_visual_part]") for m in problems))
        problems = self.run_checks(lambda v: v["parts"][0].update(role="ground"))
        self.assertTrue(any(m.startswith("[terrain_visual_part]") for m in problems))
        problems = self.run_checks(
            lambda v: v["parts"][0]["bounds"].update(max=[9.0, 9.0, 9.0]))
        self.assertTrue(any(m.startswith("[terrain_visual_part]") and "bounds" in m
                            for m in problems))
        problems = self.run_checks(lambda v: v.update(parts=[]))
        self.assertTrue(any(m.startswith("[terrain_visual_part]") for m in problems))

    def test_a_reversed_mesh_is_a_winding_error(self):
        parsed = placement_v2.read_visual_sidecar(self.built["sidecar"])
        indices = list(parsed["indices"])
        for offset in range(0, len(indices), 3):
            indices[offset + 1], indices[offset + 2] = indices[offset + 2], indices[offset + 1]
        flipped = placement_v2.visual_sidecar_bytes(
            parsed["vertices"], parsed["normals"], parsed["uvs"], indices,
            parsed["counts"]["parts"], parsed["indexWidth"])
        digest = hashlib.sha256(flipped).hexdigest()

        def flip(files):
            files[self.built["block"]["meshRef"]["path"]] = flipped
        problems = self.run_checks(lambda v: v["meshRef"].update(sha256=digest),
                                   files_mutate=flip)
        self.assertTrue(any(m.startswith("[terrain_visual_winding]") for m in problems),
                        problems)

    def test_a_document_without_visual_is_unchanged(self):
        report = placement_v2.validate(tiny_document())
        self.assertEqual([m for m in report.errors if "visual" in m], [])


# --------------------------------------------------------------------------- #
# guards and the real extraction
# --------------------------------------------------------------------------- #
class GuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="refvisual-guard-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.read_root = self.tmp / "KatamariRef"
        self.levels = self.tmp / "Levels"
        (self.read_root / "noclip_export" / "out").mkdir(parents=True)
        (self.levels / "_ref_demo").mkdir(parents=True)
        (self.levels / "hub").mkdir(parents=True)
        for patch in (mock.patch.object(build, "READ_ROOT", self.read_root),
                      mock.patch.object(build, "LEVELS_ROOT", self.levels)):
            patch.start()
            self.addCleanup(patch.stop)

    def test_reading_outside_the_reference_tree_is_refused(self):
        with self.assertRaises(build.RefV2Error):
            visual.load(ROOT / "Tools/levelbuild/placement_core.py")

    def test_a_missing_source_is_an_error_not_a_guess(self):
        with self.assertRaises(visual.RefVisualError):
            visual.load(self.read_root / "noclip_export" / "out" / "stage.glb")

    def test_writing_goes_to_reference_levels_only(self):
        built = visual.convert(synthetic_glb())
        with self.assertRaises(build.RefV2Error):
            visual.write(self.levels / "hub", built)
        result = visual.write(self.levels / "_ref_demo", built)
        self.assertTrue(result["written"])
        sidecar = self.levels / "_ref_demo" / built["block"]["meshRef"]["path"]
        self.assertEqual(sidecar.read_bytes(), built["sidecar"])
        for relative, blob in built["textures"].items():
            self.assertEqual((self.levels / "_ref_demo" / relative).read_bytes(), blob)

    def test_dry_run_writes_nothing(self):
        built = visual.convert(synthetic_glb())
        visual.write(self.levels / "_ref_demo", built, dry_run=True)
        self.assertFalse((self.levels / "_ref_demo" / "terrain").exists())


@unittest.skipUnless(REAL_GLB.is_file(), "the textured reference stage is not on this machine")
class RealStageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.built = visual.load()
        cls.payload = placement_v2.read_visual_sidecar(cls.built["sidecar"])

    def test_the_counts_match_the_research(self):
        report = self.built["report"]
        self.assertEqual(report["parts"], 7)
        self.assertEqual(report["stageParts"], 6)
        self.assertEqual(report["vertices"], 29846)
        self.assertEqual(report["triangles"] + report["degenerateDropped"], 14780)
        self.assertEqual(report["materials"], 158)
        self.assertEqual(report["blends"], {"alpha": 6, "cutout": 11, "opaque": 141})

    def test_winding_agrees_with_the_normals_and_the_sky_faces_inward(self):
        for part in self.built["block"]["parts"]:
            stats = placement_v2._visual_part_stats(self.payload, part["vertexRange"],
                                                    part["indexRange"])
            self.assertGreater(stats["agree"], 0.99 * stats["measured"], part["id"])
            self.assertEqual(stats["degenerate"], 0)
        sky = next(p for p in self.built["block"]["parts"] if p["role"] == "sky")
        v0, count = sky["vertexRange"]
        vertices = self.payload["vertices"][v0:v0 + count]
        first, total = sky["indexRange"]
        indices = self.payload["indices"][first:first + total]
        inward = 0
        for offset in range(0, total, 3):
            a, b, c = indices[offset:offset + 3]
            cross = visual._cross(vertices[a], vertices[b], vertices[c])
            centre = [sum(vertices[i][k] for i in (a, b, c)) for k in range(3)]
            inward += 1 if sum(cross[k] * centre[k] for k in range(3)) < 0 else 0
        self.assertEqual(inward, total // 3)

    def test_the_stage_stays_in_the_courtyards_frame(self):
        main = next(p for p in self.built["block"]["parts"]
                    if p["id"] == "area2_sector0_model4")
        self.assertLessEqual(main["bounds"]["max"][1], 3.6)
        self.assertGreaterEqual(main["bounds"]["min"][1], -1.6)

    def test_the_document_gets_the_block_and_the_contact_terrain_does_not_change(self):
        folder = ROOT / "Docs/Design/Levels/_ref_cancer"
        document, sidecar, _report = build.build_document("_ref_cancer", folder,
                                                          with_colliders=False,
                                                          visual=self.built)
        self.assertEqual(hashlib.sha256(sidecar).hexdigest(), COLLISION_SIDECAR_SHA256)
        self.assertEqual(document["terrain"]["meshRef"]["sha256"], COLLISION_SIDECAR_SHA256)
        keys = list(document["terrain"])
        self.assertEqual(keys[keys.index("visual") - 1], "materialSegments")
        self.assertEqual(keys[keys.index("visual") + 1], "source")
        files = dict(self.built["textures"])
        files[self.built["block"]["meshRef"]["path"]] = self.built["sidecar"]
        self.assertEqual(build.structural_errors(
            json.loads(build.canonical_text(document)), sidecar, files), [])


if __name__ == "__main__":
    unittest.main()
