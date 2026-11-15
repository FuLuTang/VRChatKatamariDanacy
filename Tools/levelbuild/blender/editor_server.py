"""The resident level editor, inside headless Blender.

    source ~/tools/blender-env.sh
    "$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/editor_server.py -- \
        --level story_01_bench_size [--endpoint /tmp/…/editor.json] [--port 0] \
        [--out-dir /tmp/levelbuild-render] [--undo-limit 50] [--no-auto-export]

It builds the scene **once** from ``placement.json`` and then answers JSON ops on a
loopback HTTP socket (``Tools/levelbuild/editor_http.py``) until it is told to stop.
Nothing here owns a rule: the document ops are ``editor_core.Editor``, the geometry is
``placement_core``, the object-level scene work is ``blender_common.LevelScene``.

Prefer ``levelserve.py``, which finds Blender, starts this process detached, waits for
the endpoint file and knows how to stop it:

    python3 Tools/levelbuild/levelserve.py start --level story_01_bench_size
    python3 Tools/levelbuild/levelctl.py move apple_01 --to=-7.9,0.5,-17.3
    python3 Tools/levelbuild/levelctl.py render --preset plan
    python3 Tools/levelbuild/levelserve.py stop

One process, one open level, requests handled one at a time (bpy is not thread-safe, so
the serve loop is single-threaded on purpose).  ``shutdown`` or SIGTERM exports any
unexported change and removes the endpoint file.
"""

import signal
import sys
import traceback
from pathlib import Path

import bpy

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import blender_common as common  # noqa: E402
from Tools.levelbuild import editor_core, editor_http  # noqa: E402


def _stop(signum, _frame):
    print("[editor] signal %d: stopping after the current request" % signum, flush=True)
    editor.stop_requested = True


def main():
    level = common.option("--level")
    placement = common.option("--placement")
    if not level and not placement:
        raise SystemExit("editor_server: --level <id> or --placement <file> is required")
    exports = common.option("--exports")
    out_dir = common.option("--out-dir")
    endpoint = common.option("--endpoint")
    log = common.option("--log")
    port = int(common.option("--port") or 0)
    undo_limit = int(common.option("--undo-limit") or 50)

    common.reset_scene()
    scene = common.LevelScene(common.resolve(exports) if exports else None, with_lights=True)
    global editor
    editor = editor_core.Editor(
        scene,
        manifests=scene.manifests,
        auto_export=not common.flag("--no-auto-export"),
        undo_limit=undo_limit,
        out_dir=common.resolve(out_dir) if out_dir else None,
    )
    signal.signal(signal.SIGTERM, _stop)
    signal.signal(signal.SIGINT, _stop)

    opened = editor.open(level, common.resolve(placement) if placement else None)
    print("[editor] opened %s: props=%d structures=%d cameras=%d gate=%s"
          % (opened["path"], opened["counts"]["props"], opened["counts"]["structures"],
             opened["counts"]["cameras"],
             "green" if opened["gate"]["ok"] else "%d error(s)" % opened["gate"]["errorCount"]),
          flush=True)
    if not opened["canonical"]:
        print("[editor] NOTE the file is not in canonical form; the first export rewrites it",
              flush=True)

    server = editor_http.Server(
        editor, endpoint, port=port,
        extra={"blender": bpy.app.version_string, "log": log,
               "cmd": " ".join(sys.argv)},
    )
    server.start()
    try:
        server.serve()
    finally:
        # The serve loop can end without a `shutdown` op (Ctrl-C, SIGTERM, a crash in
        # the request handler): never leave committed work only in memory.
        try:
            if editor.doc is not None and editor._dirty():
                exported = editor._export(editor.doc, force=True)
                print("[editor] final export %s (%s bytes)"
                      % (exported.get("path"), exported.get("bytes")), flush=True)
        except Exception:                                     # noqa: BLE001 - best effort
            print("[editor] final export failed:", flush=True)
            traceback.print_exc()
        print("[editor] exit", flush=True)


if __name__ == "__main__":
    main()
