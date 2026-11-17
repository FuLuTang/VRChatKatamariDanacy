"""The reference level's *visual* stage: ``stage.glb`` -> ``.katvisual`` + PNG textures.

A reference level's terrain contact data is the original's **collision** mesh (the
``.katerrain`` sidecar).  That mesh is not what the original draws: it carries a ring of
15 m invisible walls and has no UVs or materials.  What the original draws is the extractor's
graphic sectors, ``stage.glb`` (``Docs/Design/Tools/REF_STAGE_VIEW.md`` §10 has the research):
7 models, 29,846 vertices, 14,780 triangles, 232 material parts over 158 textures.  This
module converts that file - the *textured* export, ``noclip_export/out/stage.glb``, not the
stripped ``kse_out`` one - into what ``terrain.visual`` (LEVEL_FORMAT_V2 §3.8) describes:

* ``terrain/cancer.katvisual``: positions, normals, UVs and part-local indices;
* ``terrain/textures/<image>.png``: the GLB's PNGs byte for byte;
* the ``terrain.visual`` JSON block (parts, submeshes, materials), returned for the document.

Conversion rules (measured, not assumed - see the research doc):

* The export is in the noclip frame, centimetres.  Our frame is ``(-0.01 x, 0.01 y, 0.01 z)``
  (the same numbers ``kse_out/stage.glb`` carries and the collision mesh uses; checked
  against the collision mesh: 93.5% of its triangle centroids lie within 5 cm of this mesh).
  That map mirrors x, so the **winding is flipped once** (swap corners 2 and 3); the vertex
  normals, which the extractor wrote in the PS2 frame, become ``-n``.  After both, 99.6% of
  the triangles agree with their normals and the ground faces up.
* Material parts that share one texture are merged per part into one submesh: the 232 parts
  differ only in their texture (every part has the same GS state, so blend/cull/wrap carry no
  extra information; the GS alpha blend + alpha-test-on-zero is represented by
  ``blend: opaque|cutout|alpha`` derived from each PNG's alpha channel).
* Triangles with an area under 1e-9 m^2 are dropped (the validator forbids them).
* Textures stay in the PNGs' own space: the UV origin is the image's top-left, the glTF
  convention the export was written in; a Unity importer flips ``v``.

Reads only under ``~/KatamariRef`` and writes only into a git-ignored ``_ref_*`` level
directory; everything is deterministic (no timestamps, input order preserved).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild import placement_v2  # noqa: E402
from Tools.levelbuild import ref_stage  # noqa: E402

#: Extraction frame -> ours: noclip centimetres to Unity metres (x mirrored).
SCALE = 0.01
#: A triangle smaller than this (m^2) is degenerate (``[terrain_mesh]`` forbids it).
DEGENERATE_AREA = 1e-9
#: Directory (under the level folder) of the PNGs, and the sidecar's folder.
TERRAIN_DIR = "terrain"
TEXTURE_DIR = "terrain/textures"

#: The textured export (the stripped ``kse_out/stage.glb`` carries the same geometry).
DEFAULT_SOURCE = Path.home() / "KatamariRef/noclip_export/out/stage.glb"

#: ``a<area>_s<sector+1>_m<model>_model<n>`` - the exporter's node names.
_NODE_NAME = re.compile(r"^a(\d+)_s(\d+)_m(\d+)_model(\d+)$")
#: Sector 1 of a stage model bin is the sky shell; every other sector is stage geometry.
SKY_SECTOR = 1

_PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


class RefVisualError(RuntimeError):
    """An extraction this converter does not recognise (it never guesses)."""


# --------------------------------------------------------------------------- #
# PNG: size + alpha class
# --------------------------------------------------------------------------- #
def png_size(data):
    if data[:8] != _PNG_MAGIC:
        raise RefVisualError("an image in stage.glb is not a PNG")
    width, height, depth, colour, _comp, _filt, interlace = struct.unpack(">IIBBBBB", data[16:29])
    return width, height, depth, colour, interlace


def png_alpha_values(data):
    """The set of distinct alpha values of an 8-bit RGBA, non-interlaced PNG."""
    width, height, depth, colour, interlace = png_size(data)
    if depth != 8 or colour != 6 or interlace != 0:
        raise RefVisualError("expected 8-bit RGBA non-interlaced PNGs, got depth %d colour %d "
                             "interlace %d" % (depth, colour, interlace))
    position = 8
    idat = bytearray()
    while position + 8 <= len(data):
        length, kind = struct.unpack(">I4s", data[position:position + 8])
        if kind == b"IDAT":
            idat += data[position + 8:position + 8 + length]
        position += 12 + length
    raw = zlib.decompress(bytes(idat))
    stride = width * 4
    if len(raw) != (stride + 1) * height:
        raise RefVisualError("PNG pixel data has the wrong length")
    previous = bytearray(stride)
    alphas = set()
    for row in range(height):
        base = row * (stride + 1)
        kind = raw[base]
        line = bytearray(raw[base + 1:base + 1 + stride])
        if kind == 1:
            for i in range(4, stride):
                line[i] = (line[i] + line[i - 4]) & 255
        elif kind == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 255
        elif kind == 3:
            for i in range(stride):
                left = line[i - 4] if i >= 4 else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 255
        elif kind == 4:
            for i in range(stride):
                a = line[i - 4] if i >= 4 else 0
                b = previous[i]
                c = previous[i - 4] if i >= 4 else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 255
        elif kind != 0:
            raise RefVisualError("unknown PNG filter %d" % kind)
        alphas.update(line[3::4])
        previous = line
    return alphas


def blend_of(alphas):
    """``opaque`` / ``cutout`` / ``alpha`` from a texture's alpha values.

    The original blends every part with ``src_alpha, 1 - src_alpha`` and discards alpha 0
    (GS ``ATST=NOTEQUAL, AREF=0``): a texture whose alpha is only 0/255 is a cutout.
    """
    if alphas <= {255}:
        return "opaque"
    if alphas <= {0, 255}:
        return "cutout"
    return "alpha"


# --------------------------------------------------------------------------- #
# GLB reading
# --------------------------------------------------------------------------- #
def _view_bytes(document, binary, index):
    view = document["bufferViews"][index]
    start = int(view.get("byteOffset") or 0)
    return bytes(binary[start:start + int(view["byteLength"])])


def _accessor_raw(document, binary, index):
    """The raw bytes behind an accessor (for comparing primitives that share vertices)."""
    accessor = document["accessors"][index]
    view = document["bufferViews"][accessor["bufferView"]]
    start = int(view.get("byteOffset") or 0) + int(accessor.get("byteOffset") or 0)
    width = ref_stage.TYPE_WIDTH[accessor["type"]]
    size = ref_stage.COMPONENT_FORMAT[accessor["componentType"]][1]
    return bytes(binary[start:start + int(accessor["count"]) * width * size])


def _safe_name(name):
    cleaned = re.sub(r"[^A-Za-z0-9_.-]", "_", str(name))
    return cleaned or "texture"


def _read_textures(document, binary):
    """``[{index, name, bytes, width, height, blend, sha256}]`` in glTF texture order."""
    textures = []
    seen = set()
    for index, texture in enumerate(document.get("textures") or []):
        image = document["images"][int(texture["source"])]
        if image.get("mimeType") != "image/png" or "bufferView" not in image:
            raise RefVisualError("texture %d is not an embedded PNG" % index)
        data = _view_bytes(document, binary, image["bufferView"])
        width, height, _depth, _colour, _interlace = png_size(data)
        name = _safe_name(image.get("name") or "texture%03d" % index)
        if name in seen:
            raise RefVisualError("two textures are both called %r" % name)
        seen.add(name)
        textures.append({"index": index, "name": name, "bytes": data, "width": width,
                         "height": height, "blend": blend_of(png_alpha_values(data)),
                         "sha256": hashlib.sha256(data).hexdigest()})
    return textures


def _material_texture(document, material_index):
    material = document["materials"][material_index]
    pbr = material.get("pbrMetallicRoughness") or {}
    colour = pbr.get("baseColorFactor") or [1, 1, 1, 1]
    if [float(value) for value in colour] != [1.0, 1.0, 1.0, 1.0]:
        raise RefVisualError("material %d has a base colour %r; the visual format has no "
                             "tint" % (material_index, colour))
    texture = (pbr.get("baseColorTexture") or {}).get("index")
    if texture is None:
        raise RefVisualError("material %d has no texture" % material_index)
    return int(texture)


def _f32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def _cross(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx)


def _round4(value):
    value = round(float(value), 4)
    return 0.0 if value == 0 else value


# --------------------------------------------------------------------------- #
# the conversion
# --------------------------------------------------------------------------- #
def convert(glb_bytes):
    """Convert one textured ``stage.glb``; returns a dict:

    ``sidecar`` (bytes), ``block`` (the ``terrain.visual`` JSON), ``textures``
    (``{relative path: PNG bytes}``) and ``report`` (counts for humans).
    """
    document, binary = ref_stage.read_glb(glb_bytes)
    textures = _read_textures(document, binary)
    if not textures:
        raise RefVisualError("stage.glb has no textures: this is the stripped export; the "
                             "visual stage needs noclip_export/out/stage.glb")
    nodes = document.get("nodes") or []
    if not nodes:
        raise RefVisualError("stage.glb has no nodes")

    all_vertices, all_normals, all_uvs, all_indices = [], [], [], []
    parts = []
    used = set()
    totals = {"degenerate": 0, "triangles": 0}
    for node in nodes:
        match = _NODE_NAME.match(str(node.get("name") or ""))
        if not match or "mesh" not in node:
            raise RefVisualError("node %r is not an exporter model (a<area>_s<n>_m<k>_model<j>)"
                                 % (node.get("name"),))
        area, sector_one, model = int(match.group(1)), int(match.group(2)), int(match.group(3))
        sector = sector_one - 1
        mesh = document["meshes"][int(node["mesh"])]
        primitives = mesh["primitives"]
        first = primitives[0]
        attributes = first["attributes"]
        raw = {key: _accessor_raw(document, binary, attributes[key])
               for key in ("POSITION", "NORMAL", "TEXCOORD_0")}
        for primitive in primitives[1:]:
            if primitive.get("mode", 4) != 4:
                raise RefVisualError("%s: only triangle lists are handled" % node["name"])
            for key, blob in raw.items():
                if _accessor_raw(document, binary, primitive["attributes"][key]) != blob:
                    raise RefVisualError("%s: primitives do not share one vertex buffer"
                                         % node["name"])
        positions = ref_stage.accessor_values(document, binary, attributes["POSITION"])
        normals = ref_stage.accessor_values(document, binary, attributes["NORMAL"])
        uvs = ref_stage.accessor_values(document, binary, attributes["TEXCOORD_0"])
        out_positions = [(_f32(-SCALE * p[0]), _f32(SCALE * p[1]), _f32(SCALE * p[2]))
                         for p in positions]
        out_normals = [(_f32(-n[0]), _f32(-n[1]), _f32(-n[2])) for n in normals]
        out_uvs = [(_f32(float(u[0])), _f32(float(u[1]))) for u in uvs]

        by_texture = {}
        order = []
        part_degenerate = 0
        for primitive in primitives:
            texture = _material_texture(document, int(primitive["material"]))
            flat = [value[0] for value in
                    ref_stage.accessor_values(document, binary, primitive["indices"])]
            bucket = by_texture.get(texture)
            if bucket is None:
                bucket = by_texture[texture] = []
                order.append(texture)
            for offset in range(0, len(flat) - 2, 3):
                a, b, c = flat[offset:offset + 3]
                cross = _cross(out_positions[a], out_positions[b], out_positions[c])
                if 0.5 * math.sqrt(sum(value * value for value in cross)) < DEGENERATE_AREA:
                    part_degenerate += 1
                    continue
                bucket.extend((a, c, b))          # the winding flip (x is mirrored)

        local = []
        submeshes = []
        for texture in order:
            bucket = by_texture[texture]
            if not bucket:
                continue
            used.add(texture)
            submeshes.append({"material": "t%03d" % texture, "firstIndex": len(local),
                              "indexCount": len(bucket)})
            local.extend(bucket)
        if not local:
            raise RefVisualError("%s has no triangles" % node["name"])
        low = [min(point[axis] for point in out_positions) for axis in range(3)]
        high = [max(point[axis] for point in out_positions) for axis in range(3)]
        parts.append({
            "id": "area%d_sector%d_model%d" % (area, sector, model),
            "role": "sky" if sector == SKY_SECTOR else "stage",
            "vertexRange": [len(all_vertices), len(out_positions)],
            "indexRange": [len(all_indices), len(local)],
            "bounds": {"min": [_round4(v) for v in low], "max": [_round4(v) for v in high]},
            "submeshes": submeshes,
        })
        all_vertices.extend(out_positions)
        all_normals.extend(out_normals)
        all_uvs.extend(out_uvs)
        all_indices.extend(local)
        totals["degenerate"] += part_degenerate
        totals["triangles"] += len(local) // 3

    ids = [part["id"] for part in parts]
    if len(set(ids)) != len(ids):
        raise RefVisualError("two nodes map to the same part id: %s" % ids)
    max_part_vertices = max(part["vertexRange"][1] for part in parts)
    index_width = 2 if max_part_vertices < placement_v2.U16_VERTEX_LIMIT else 4
    sidecar = placement_v2.visual_sidecar_bytes(all_vertices, all_normals, all_uvs, all_indices,
                                                len(parts), index_width)
    digest = hashlib.sha256(sidecar).hexdigest()

    materials = []
    files = {}
    for texture in textures:
        if texture["index"] not in used:
            continue
        relative = "%s/%s.png" % (TEXTURE_DIR, texture["name"])
        files[relative] = texture["bytes"]
        materials.append({"id": "t%03d" % texture["index"], "texture": relative,
                          "sha256": texture["sha256"], "width": texture["width"],
                          "height": texture["height"], "blend": texture["blend"]})

    block = {
        "meshRef": {"path": "%s/%s" % (TERRAIN_DIR, placement_v2.VISUAL_SIDECAR_NAME),
                    "sha256": digest, "vertexCount": len(all_vertices),
                    "indexCount": len(all_indices), "triangleCount": len(all_indices) // 3,
                    "indexWidth": index_width, "normals": True, "uvs": True},
        "parts": parts,
        "materials": materials,
        "filter": "point",
        "source": {"kind": "reference", "windingFix": "flip",
                   "provenance": "PS2 stage graphics sectors -> noclip_export/out/stage.glb"},
    }
    blends = {}
    for material in materials:
        blends[material["blend"]] = blends.get(material["blend"], 0) + 1
    report = {
        "parts": len(parts), "stageParts": sum(1 for p in parts if p["role"] == "stage"),
        "vertices": len(all_vertices), "triangles": totals["triangles"],
        "degenerateDropped": totals["degenerate"],
        "submeshes": sum(len(p["submeshes"]) for p in parts),
        "materials": len(materials), "blends": dict(sorted(blends.items())),
        "textureBytes": sum(len(blob) for blob in files.values()),
        "sidecarBytes": len(sidecar), "sidecarSha256": digest, "indexWidth": index_width,
    }
    return {"sidecar": sidecar, "block": block, "textures": files, "report": report}


# --------------------------------------------------------------------------- #
# guarded I/O
# --------------------------------------------------------------------------- #
def _guards():
    # The reference builder owns the path guards (one place decides what may be read/written).
    from Tools.levelbuild import ref_v2_from_extract as builder
    return builder


def load(source=None):
    """:func:`convert` the GLB at ``source`` (default :data:`DEFAULT_SOURCE`)."""
    builder = _guards()
    path = builder.guard_read(Path(source) if source else DEFAULT_SOURCE)
    if not path.is_file():
        raise RefVisualError("no stage.glb at %s (the textured export is "
                             "noclip_export/out/stage.glb)" % path)
    return convert(path.read_bytes())


def write(folder, built, *, dry_run=False):
    """Write the sidecar + PNGs under the reference level ``folder`` (guarded)."""
    builder = _guards()
    folder = Path(folder)
    sidecar_path = builder.guard_write(folder / built["block"]["meshRef"]["path"])
    targets = {sidecar_path: built["sidecar"]}
    for relative, blob in built["textures"].items():
        targets[builder.guard_write(folder / relative)] = blob
    if not dry_run:
        for path, blob in targets.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            if not path.exists() or path.read_bytes() != blob:
                path.write_bytes(blob)
    return {"sidecar": sidecar_path, "files": len(targets), "written": not dry_run}


def build_parser():
    parser = argparse.ArgumentParser(
        prog="ref_stage_visual.py",
        description="Convert the original's graphic stage (stage.glb) into the .katvisual "
                    "sidecar + PNG textures of a reference level (UNLICENSED reference data). "
                    "Run ref_v2_from_extract.py afterwards to put the terrain.visual block in "
                    "the document.")
    parser.add_argument("--level", default="_ref_cancer")
    parser.add_argument("--levels-dir", default=None, help="override Docs/Design/Levels")
    parser.add_argument("--source", default=None,
                        help="the textured stage.glb (default %s)" % DEFAULT_SOURCE)
    parser.add_argument("--write", action="store_true", help="write the files (default dry run)")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    builder = _guards()
    levels = Path(args.levels_dir) if args.levels_dir else builder.LEVELS_ROOT
    folder = levels / args.level
    try:
        built = load(args.source)
        result = write(folder, built, dry_run=not args.write)
    except (RefVisualError, ref_stage.RefStageError, builder.RefV2Error, OSError,
            KeyError, ValueError) as error:
        print("ref_stage_visual: %s" % error, file=sys.stderr)
        return 2
    report = dict(built["report"], written=bool(args.write),
                  sidecar=core.display_path(result["sidecar"]), files=result["files"])
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print("ref_stage_visual: %s %s (%d files)" % ("wrote" if args.write else "dry run:",
                                                      report["sidecar"], report["files"]))
        print("  %d parts (%d stage), %d vertices, %d triangles (%d degenerate dropped), "
              "%d submeshes" % (report["parts"], report["stageParts"], report["vertices"],
                                report["triangles"], report["degenerateDropped"],
                                report["submeshes"]))
        print("  %d materials %s, %.1f KB of PNG" % (report["materials"], report["blends"],
                                                     report["textureBytes"] / 1024.0))
        print("  sidecar sha256: %s" % report["sidecarSha256"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
