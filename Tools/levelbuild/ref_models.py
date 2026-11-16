"""Local viewer assets for the git-ignored UNLICENSED reference levels (``_ref_*``).

Reference levels (``Docs/Design/Levels/_ref_cancer``, ``_ref_mas1``) carry positions
and sizes from a community data mine and meshes ripped from a commercial game.  They
are **not** part of the project's asset set: the level directories are git-ignored,
nothing in them may be committed, uploaded or copied into ``Docs/Art`` / the managed
props, and the meshes may only be shown by the local level editor.

This module is the one place that answers "where is the local mesh for this def?".

* The def's Unity ``model`` path (``Assets/_RefUnlicensed/Models/<def>/<def>.fbx``)
  is **not** used to load a file from the Unity project.  It names the asset, not a
  location the web editor can serve.
* The level's own conversion table (``original_defs.json`` -> ``modelFile``) maps a
  def to a source GLB in ``<models-dir>/glb-pack/``.  Those GLBs are the unconverted
  PS2 assets with embedded textures, already in glTF's Y-up metres - three r186 loads
  them without Blender.
* ``<models-dir>/viewer_glb/<def>.glb`` is the optional cache for a converted FBX
  (the git-ignored Blender output), preferred only when the pack has no source GLB.
* ``converted_local/<def>/<def>.fbx`` and ``converted/<modelFile stem>.fbx`` are
  returned as ``kind="fbx"`` sources so a caller can report "convertible, not yet
  converted" instead of pretending the model is missing.

Nothing here writes to the repository.  ``normalized_models_dir`` refuses a models
directory inside the repo so an ignored asset tree cannot accidentally become
committable.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

from Tools.levelbuild import placement_core as core

#: Level ids with this prefix are the unlicensed reference levels.
REF_LEVEL_PREFIX = "_ref_"

#: Directory names inside ``<models-dir>``.
PACK_DIR = "glb-pack"
VIEWER_CACHE_DIR = "viewer_glb"
CONVERTED_LOCAL_DIR = "converted_local"
CONVERTED_DIR = "converted"

#: Environment override for the models root (the CLI flag wins over it).
MODELS_DIR_ENV = "VRC_KATAMARI_REF_MODELS"

DEFAULT_MODELS_DIR = Path.home() / "KatamariRef/models"


class RefAssetError(RuntimeError):
    """A reference asset request the editor refuses (wrong location / missing tree)."""


def is_ref_level(level_id) -> bool:
    """True for the reference levels: the only levels allowed to see ref meshes."""
    return isinstance(level_id, str) and level_id.startswith(REF_LEVEL_PREFIX)


def default_models_dir() -> Path:
    """``$VRC_KATAMARI_REF_MODELS`` or ``~/KatamariRef/models``."""
    value = os.environ.get(MODELS_DIR_ENV)
    return Path(value).expanduser() if value else DEFAULT_MODELS_DIR


def normalized_models_dir(models_dir=None) -> Path:
    """Resolve and validate the local models root.

    It must exist and be **outside the repository**: unlicensed reference assets are
    git-ignored by policy, so a root inside the checkout is refused outright.
    """
    root = Path(models_dir if models_dir is not None else default_models_dir()).expanduser()
    try:
        root = root.resolve(strict=True)
    except OSError:
        raise RefAssetError("reference models directory does not exist: %s" % root)
    if not root.is_dir():
        raise RefAssetError("reference models path is not a directory: %s" % root)
    try:
        root.relative_to(core.ROOT.resolve())
    except ValueError:
        return root
    raise RefAssetError(
        "reference models directory %s is inside the repository; unlicensed reference "
        "assets must stay outside it (e.g. ~/KatamariRef/models)" % root)


def load_model_table(level_dir) -> dict:
    """``original_defs.json`` -> ``{def: modelFile}`` for one reference level.

    ``original_defs.json`` is the level's own conversion table (git-ignored with the
    level).  A missing or malformed file is not fatal: the cache/name fallbacks still
    work, and the caller can report which defs have no source.
    """
    path = Path(level_dir) / "original_defs.json"
    if not path.is_file():
        return {}
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    table = {}
    for entry in document.get("defs") or []:
        if not isinstance(entry, dict):
            continue
        def_id = entry.get("id")
        model_file = entry.get("modelFile")
        if isinstance(def_id, str) and def_id and isinstance(model_file, str) and model_file:
            table[def_id] = model_file
    return table


def _inside(root, path) -> bool:
    try:
        Path(path).resolve().relative_to(root)
        return True
    except (OSError, ValueError):
        return False


def resolve_model(def_id, model, table, models_dir) -> dict | None:
    """The local viewer asset for ``def_id``, or ``None``.

    ``table`` is :func:`load_model_table`'s result; ``models_dir`` must already be
    normalized.  The returned dict has ``def``, ``path``, ``kind`` (``glb`` or
    ``fbx``) and ``source`` (``glb-pack`` / ``viewer_glb`` / ``converted_local`` /
    ``converted``).  A returned path is always inside ``models_dir``.
    """
    if not isinstance(def_id, str) or not def_id:
        return None
    root = Path(models_dir)
    model_file = table.get(def_id) if isinstance(table, dict) else None
    candidates = []
    if isinstance(model_file, str) and model_file:
        candidates.append((root / PACK_DIR / model_file, "glb-pack", "glb"))
    candidates.append((root / VIEWER_CACHE_DIR / (def_id + ".glb"), "viewer_glb", "glb"))
    for path, source, kind in candidates:
        if path.is_file() and path.suffix.lower() == ".glb" and _inside(root, path):
            return {"def": def_id, "path": str(path.resolve()), "kind": kind,
                    "source": source}
    # Convertible FBX sources (no Blender here; the caller can report them).
    fbx_candidates = [(root / CONVERTED_LOCAL_DIR / def_id / (def_id + ".fbx"),
                       "converted_local")]
    if isinstance(model_file, str) and model_file:
        fbx_candidates.append((root / CONVERTED_DIR /
                               (Path(model_file).stem + ".fbx"), "converted"))
    if isinstance(model, str) and model:
        stem = Path(model).stem
        fbx_candidates.append((root / CONVERTED_LOCAL_DIR / stem / (stem + ".fbx"),
                               "converted_local"))
    for path, source in fbx_candidates:
        if path.is_file() and _inside(root, path):
            return {"def": def_id, "path": str(path.resolve()), "kind": "fbx",
                    "source": source}
    return None


def parent_links(doc) -> list:
    """``group``/``groupId`` child -> parent links, resolved against the props.

    The reference placement carries the original's parent control index: 83 children
    in Make Cancer.  Only links whose parent prop exists are returned; the rest are
    the level's own dangling references.
    """
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    by_id = {prop.get("itemId"): prop for prop in props}
    links = []
    seen = set()
    for prop in props:
        parent = prop.get("group") or prop.get("groupId")
        child = prop.get("itemId")
        if not isinstance(parent, str) or not parent or parent == child:
            continue
        if child not in by_id or parent not in by_id:
            continue
        key = (child, parent)
        if key in seen:
            continue
        seen.add(key)
        links.append({"child": child, "parent": parent, "def": prop.get("def")})
    return links


def display_path(path) -> str:
    """``~/KatamariRef/models`` rather than an absolute home path."""
    try:
        home = str(Path.home())
        text = str(path)
        return "~" + text[len(home):] if text.startswith(home + os.sep) else text
    except (OSError, TypeError):
        return str(path)
