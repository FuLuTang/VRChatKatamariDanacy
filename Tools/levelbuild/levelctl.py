#!/usr/bin/env python3
"""Command-line client for the resident level editor (``blender/editor_server.py``).

    python3 Tools/levelbuild/levelctl.py status
    python3 Tools/levelbuild/levelctl.py open story_01_bench_size
    python3 Tools/levelbuild/levelctl.py list --zone table_top --limit 5
    python3 Tools/levelbuild/levelctl.py move apple_01 --to=-7.9,0.05,-17.3
    python3 Tools/levelbuild/levelctl.py check --report
    python3 Tools/levelbuild/levelctl.py render --preset plan
    python3 Tools/levelbuild/levelctl.py undo
    python3 Tools/levelbuild/levelctl.py export --to /tmp/out.json

The client is deliberately thin: it turns flags into the editor's JSON op and prints
what comes back.  Everything that decides what an op *means* lives in
``editor_core.py`` (and, for geometry, ``placement_core.py``), so a Bash-invoked agent
and a Python caller take the same path.

Exit codes: ``0`` the op succeeded, ``1`` the editor refused it, ``2`` no editor
answered (stale/missing endpoint, bad JSON on the wire).

``--json`` prints the whole response object (including ``state``) instead of a short
human summary; ``raw`` sends a request body as-is (for ``batch`` and for anything the
flag surface does not cover yet).
"""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import editor_core, editor_http  # noqa: E402


# --------------------------------------------------------------------------- #
# small argument helpers
# --------------------------------------------------------------------------- #
def vector(text):
    """``1,2.5,-3`` or ``[1, 2.5, -3]`` -> list of three floats."""
    raw = text.strip()
    if raw.startswith("["):
        try:
            value = json.loads(raw)
        except json.JSONDecodeError as error:
            raise argparse.ArgumentTypeError("%s is not JSON: %s" % (text, error))
    else:
        value = [part for part in raw.replace(" ", "").split(",") if part != ""]
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise argparse.ArgumentTypeError("%r is not three numbers" % text)
    try:
        return [float(part) for part in value]
    except (TypeError, ValueError):
        raise argparse.ArgumentTypeError("%r is not three numbers" % text)


def pair(text):
    raw = text.replace(" ", "").split("x") if "x" in text else text.replace(" ", "").split(",")
    if len(raw) != 2:
        raise argparse.ArgumentTypeError("%r is not <width>x<height>" % text)
    try:
        return [int(raw[0]), int(raw[1])]
    except ValueError:
        raise argparse.ArgumentTypeError("%r is not <width>x<height>" % text)


def point_xz(text):
    raw = text.replace(" ", "").split(",")
    if len(raw) != 2:
        raise argparse.ArgumentTypeError("%r is not <x>,<z>" % text)
    try:
        return [float(raw[0]), float(raw[1])]
    except ValueError:
        raise argparse.ArgumentTypeError("%r is not <x>,<z>" % text)


def _drop(args, *names):
    for name in names:
        args.pop(name, None)


# --------------------------------------------------------------------------- #
# per-op request builders
# --------------------------------------------------------------------------- #
def request_for(args):
    op = args.command
    if op == "status":
        return {"op": "status"}
    if op == "open":
        body = {"op": "open"}
        if args.level:
            body["args"] = {"level": args.level}
        elif args.path:
            body["args"] = {"path": args.path}
        else:
            raise SystemExit("open needs a level id or --path")
        return body
    if op == "reload":
        return {"op": "reload"}
    if op == "list":
        body = {}
        for key in ("zone", "def", "kind", "support", "stacked_on", "text"):
            value = getattr(args, key)
            if value is not None:
                body["stackedOn" if key == "stacked_on" else key] = value
        if args.near is not None:
            body["near"] = args.near
            body["radius"] = args.radius
        if args.limit is not None:
            body["limit"] = args.limit
        return {"op": "list", "args": body}
    if op == "get":
        return {"op": "get", "args": {"id": args.id}}
    if op == "add-prop":
        body = {"def": args.def_id}
        if args.id:
            body["itemId"] = args.id
        if args.pos is not None:
            body["position"] = args.pos
        if args.yaw is not None:
            body["yaw"] = args.yaw
        for key in ("zone", "kind", "support", "stacked_on", "value", "group", "notes"):
            value = getattr(args, key)
            if value is not None:
                body["stackedOn" if key == "stacked_on" else
                     "valueUnits" if key == "value" else key] = value
        if args.seat:
            body["seat"] = True
        if args.update_expected:
            body["updateExpected"] = True
        return {"op": "add_prop", "args": body}
    if op == "add-structure":
        body = {"role": args.role, "center": args.center, "size": args.size}
        if args.id:
            body["id"] = args.id
        if args.yaw is not None:
            body["yaw"] = args.yaw
        for key in ("kind", "shape", "collision", "material", "notes"):
            value = getattr(args, key)
            if value is not None:
                body[key] = value
        for key in ("support", "absorbable"):
            if getattr(args, key) is not None:
                body[key] = getattr(args, key)
        return {"op": "add_structure", "args": body}
    if op == "set":
        body = {"id": args.id}
        if args.pos is not None:
            body["position"] = args.pos
        if args.center is not None:
            body["center"] = args.center
        if args.size is not None:
            body["size"] = args.size
        if args.quat is not None:
            body["rotationQuat"] = args.quat
        elif args.yaw is not None:
            body["yaw"] = args.yaw
        for key in ("zone", "kind", "group", "support", "stacked_on", "value", "notes",
                    "role", "shape", "collision", "meets"):
            value = getattr(args, key)
            if value is not None:
                body["stackedOn" if key == "stacked_on" else
                     "valueUnits" if key == "value" else key] = value
        return {"op": "set", "args": body}
    if op == "move":
        if (args.to is None) == (args.delta is None):
            raise SystemExit("move needs exactly one of --to or --delta")
        return {"op": "move",
                "args": {"id": args.id, "to" if args.to is not None else "delta":
                         args.to if args.to is not None else args.delta}}
    if op == "rotate":
        if args.yaw is not None:
            return {"op": "rotate", "args": {"id": args.id, "yaw": args.yaw}}
        if args.quat is not None:
            return {"op": "rotate", "args": {"id": args.id, "quat": args.quat}}
        raise SystemExit("rotate needs --yaw or --quat")
    if op == "remove":
        body = {"ids": args.ids} if len(args.ids) > 1 else {"id": args.ids[0]}
        if args.update_expected:
            body["updateExpected"] = True
        return {"op": "remove", "args": body}
    if op == "seat":
        body = {"ids": args.ids} if args.ids else {}
        return {"op": "seat", "args": body}
    if op == "derive-supports":
        body = {"ids": args.ids} if args.ids else {}
        if args.apply:
            body["apply"] = True
        return {"op": "derive_supports", "args": body}
    if op == "add-def":
        body = {"id": args.id}
        for key in ("kind", "notes"):
            value = getattr(args, key)
            if value is not None:
                body[key] = value
        if args.value is not None:
            body["valueUnits"] = args.value
        return {"op": "add_def", "args": body}
    if op == "check":
        body = {}
        if args.report:
            body["report"] = True
        if args.strict:
            body["strict"] = True
        return {"op": "check", "args": body}
    if op == "export":
        body = {}
        if args.to:
            body["to"] = args.to
        if args.force:
            body["force"] = True
        return {"op": "export", "args": body}
    if op == "render":
        body = {}
        if args.preset:
            body["preset"] = args.preset
        elif args.camera:
            body["camera"] = json.loads(args.camera)
        else:
            raise SystemExit("render needs --preset <id> or --camera '<json>'")
        for key in ("engine", "samples", "out", "name"):
            value = getattr(args, key)
            if value is not None:
                body[key] = value
        if args.res is not None:
            body["res"] = args.res
        return {"op": "render", "args": body}
    if op == "camera":
        if args.list:
            return {"op": "camera", "args": {"action": "list"}}
        if args.remove:
            return {"op": "camera", "args": {"action": "remove", "id": args.remove}}
        body = {"action": "set", "id": args.id}
        if args.type:
            body["type"] = args.type
        if args.location is not None:
            body["location"] = args.location
        if args.target is not None:
            body["target"] = args.target
        if args.lens is not None:
            body["lens"] = args.lens
        if args.coverage is not None:
            body["coverage_m"] = args.coverage
        return {"op": "camera", "args": body}
    if op == "preset":
        if args.list:
            return {"op": "preset", "args": {"action": "list"}}
        if args.remove:
            return {"op": "preset", "args": {"action": "remove", "id": args.remove}}
        body = {"action": "set", "id": args.id}
        if args.camera_id:
            body["camera"] = args.camera_id
        if args.engine:
            body["engine"] = args.engine
        if args.res is not None:
            body["res"] = args.res
        if args.samples is not None:
            body["samples"] = args.samples
        return {"op": "preset", "args": body}
    if op == "undo":
        return {"op": "undo", "args": {"steps": args.steps}}
    if op == "shutdown":
        return {"op": "shutdown"}
    if op == "raw":
        try:
            return json.loads(args.body)
        except json.JSONDecodeError as error:
            raise SystemExit("raw: %s" % error)
    if op == "batch":
        if args.file and args.file != "-":
            try:
                text = Path(args.file).read_text(encoding="utf-8")
            except OSError as error:
                raise SystemExit("batch: cannot read %s: %s" % (args.file, error))
        else:
            text = sys.stdin.read()
        try:
            payload = json.loads(text)
        except json.JSONDecodeError as error:
            raise SystemExit("batch: %s" % error)
        if isinstance(payload, list):
            payload = {"ops": payload}
        return payload
    raise SystemExit("unknown command %r" % op)


# --------------------------------------------------------------------------- #
# output
# --------------------------------------------------------------------------- #
def print_summary(response):
    state = response.get("state") or {}
    result = response.get("result")
    op = response.get("op")
    if not response.get("ok"):
        print("REFUSED %s: %s" % (op, response.get("error")))
        for detail in response.get("details") or []:
            print("  - %s" % detail)
        return
    if op == "status":
        print("%s  %s" % (state.get("level"), state.get("path")))
        counts = result.get("counts") or {}
        print("props=%s structures=%s cameras=%s defs=%s valueUnits=%s"
              % (counts.get("props"), counts.get("structures"), counts.get("cameras"),
                 counts.get("defs"), counts.get("valueUnits")))
        gate = result.get("gate") or {}
        print("gate=%s  dirty=%s  undo=%d  autoExport=%s"
              % ("PASS" if gate.get("ok") else "FAIL(%d)" % gate.get("errorCount", 0),
                 result.get("dirty"), len(result.get("undo") or []),
                 result.get("autoExport")))
        file_state = result.get("file") or {}
        print("file=%s bytes canonical=%s  lastExport=%s  uptime=%.1fs"
              % (file_state.get("bytes"), file_state.get("canonical"),
                 (result.get("lastExport") or {}).get("sha256", "-")[:12],
                 result.get("uptimeSeconds") or 0.0))
        scene = result.get("scene") or {}
        if scene:
            print("blender=%s  objects=%s" % (scene.get("blender"), scene.get("objects")))
    elif op == "list":
        print("%d of %d prop(s)" % (result.get("count", 0), result.get("total", 0)))
        for prop in result.get("props") or []:
            print("  %-22s %-16s %-12s %s" % (prop.get("itemId"), prop.get("def"),
                                              prop.get("zone"), prop.get("position")))
    elif op == "get":
        print(json.dumps(result.get("entry"), ensure_ascii=False, indent=2))
    elif op == "check":
        print("%s  %d error(s) %d warning(s)%s"
              % ("PASS" if result.get("ok") else "FAIL", len(result.get("errors") or []),
                 len(result.get("warnings") or []),
                 "  inSync" if result.get("inSync") else "  NOT-in-sync"))
        for line in (result.get("errors") or [])[:40]:
            print("  E %s" % line)
        for line in (result.get("warnings") or [])[:20]:
            print("  W %s" % line)
        if result.get("text"):
            print(result["text"])
    elif op == "export":
        print("%s %s (%s bytes, sha %s) gate=%s"
              % ("wrote" if result.get("wrote") else "did NOT write", result.get("path"),
                 result.get("bytes"), (result.get("sha256") or "")[:16],
                 "PASS" if (result.get("gate") or {}).get("ok") else "FAIL"))
        if not result.get("wrote"):
            print("why: %s" % result.get("why"))
            for line in (result.get("gate") or {}).get("errors") or []:
                print("  E %s" % line)
    elif op == "render":
        print("%s  %s  %dx%d  %.2fs  %s bytes"
              % (result.get("path"), result.get("engineId"), result["res"][0],
                 result["res"][1], result.get("seconds") or 0.0, result.get("bytes")))
    elif op == "undo":
        print("undid %s" % ", ".join(result.get("undone") or []))
        print("props=%s" % (result.get("counts") or {}).get("props"))
    elif op == "seat":
        print("seated %d prop(s)%s" % (len(result.get("moved") or []),
                                       (": " + ", ".join(result["moved"]))
                                       if result.get("moved") else ""))
    elif op == "derive-supports":
        counts = result.get("counts") or {}
        print("%s: proposed %s, would write %s, agreed %s, conflicting %s, orphaned %s"
              % ("applied" if result.get("applied") else "dry run",
                 counts.get("supports"), len(result.get("new") or []),
                 counts.get("agreed"), counts.get("conflict"), counts.get("orphaned")))
        for entry in result.get("new") or []:
            print("  + %s -> %s (gap %s m)" % (entry["itemId"], entry["parent"], entry["gap"]))
        for entry in result.get("conflict") or []:
            print("  ! %s: file says %s, geometry says %s"
                  % (entry["itemId"], entry["declared"], entry["proposed"]))
        for entry in result.get("orphaned") or []:
            print("  ? %s: file says %s, geometry finds nothing"
                  % (entry["itemId"], entry["declared"]))
        if result.get("written"):
            print("wrote %d pointer(s): %s"
                  % (len(result["written"]), ", ".join(result["written"])))
    elif op in ("move", "set", "rotate"):
        print("%s %s" % (op, result.get("id") or result.get("entry", {}).get("itemId")))
        for key in ("position", "center", "rotationQuat"):
            if key in result:
                print("  %s = %s" % (key, result[key]))
    elif op in ("add_prop", "add_structure"):
        print("added %s" % (result.get("itemId") or result.get("id")))
    elif op == "batch":
        for index, item in enumerate(result.get("ops") or []):
            print("  [%d] %s changed=%s" % (index, item.get("id") or
                                            item.get("itemId") or item.get("label") or "",
                                            item.get("changed")))
        commit = result.get("commit") or {}
        print("batch committed=%s %s" % (commit.get("changed"), commit.get("label", "")))
    elif op in ("camera", "preset"):
        print(json.dumps(result, ensure_ascii=False, indent=2))
    elif op == "shutdown":
        print("editor stopped")
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    commit = response.get("gate")
    if commit and op not in ("status", "check", "export", "list", "get"):
        print("gate=%s  %s" % ("PASS" if commit.get("ok") else
                               "FAIL(%d)" % commit.get("errorCount", 0),
                               (response.get("result") or {}).get("export", {}).get("path", "")))
    if state.get("props") is not None and op not in ("status", "list", "get"):
        print("state: props=%s structures=%s valueUnits=%s dirty=%s undo=%s"
              % (state.get("props"), state.get("structures"),
                 state.get("valueUnits"), state.get("dirty"), state.get("undo")))
    if response.get("seconds") is not None:
        print("took %.3fs" % response["seconds"])


# --------------------------------------------------------------------------- #
# parser
# --------------------------------------------------------------------------- #
def build_parser():
    parser = argparse.ArgumentParser(
        prog="levelctl.py",
        description="Talk to the resident level editor (see `levelctl.py ops`).")
    parser.add_argument("--endpoint", default=None,
                        help="endpoint file (default: the shared runtime path)")
    parser.add_argument("--json", action="store_true", help="print the raw response")
    parser.add_argument("--timeout", type=float, default=None,
                        help="request timeout in seconds (default 120; renders need more)")
    sub = parser.add_subparsers(dest="command")

    sub.add_parser("status", help="level, counts, gate, dirty flag, undo depth")
    open_cmd = sub.add_parser("open", help="open a level and build the scene")
    open_cmd.add_argument("level", nargs="?")
    open_cmd.add_argument("--path")
    sub.add_parser("reload", help="re-read placement.json and re-sync the scene")

    list_cmd = sub.add_parser("list", help="filter the props")
    for key in ("zone", "def", "kind", "support"):
        list_cmd.add_argument("--" + key)
    list_cmd.add_argument("--stacked-on")
    list_cmd.add_argument("--text")
    list_cmd.add_argument("--near", type=point_xz, help="x,z - with --radius")
    list_cmd.add_argument("--radius", type=float, default=1.0)
    list_cmd.add_argument("--limit", type=int)

    get_cmd = sub.add_parser("get", help="one prop/structure/camera/preset")
    get_cmd.add_argument("id")

    add_prop = sub.add_parser("add-prop", help="add a prop (aabb comes from the manifest)")
    add_prop.add_argument("--def", dest="def_id", required=True)
    add_prop.add_argument("--id")
    add_prop.add_argument("--pos", type=vector, help="x,y,z")
    add_prop.add_argument("--yaw", type=float, help="degrees around Unity +Y")
    add_prop.add_argument("--zone")
    add_prop.add_argument("--kind")
    add_prop.add_argument("--group")
    add_prop.add_argument("--support")
    add_prop.add_argument("--stacked-on", dest="stacked_on")
    add_prop.add_argument("--value", type=int, help="valueUnits")
    add_prop.add_argument("--notes")
    add_prop.add_argument("--seat", action="store_true",
                          help="seat it on its declared support after adding")
    add_prop.add_argument("--update-expected", dest="update_expected", action="store_true",
                          default=None,
                          help="also recompute level.expected (the design total)")

    add_structure = sub.add_parser("add-structure", help="add a structure box/plane")
    add_structure.add_argument("--role", required=True)
    add_structure.add_argument("--center", type=vector, required=True)
    add_structure.add_argument("--size", type=vector, required=True)
    add_structure.add_argument("--id")
    add_structure.add_argument("--yaw", type=float)
    add_structure.add_argument("--kind")
    add_structure.add_argument("--shape")
    add_structure.add_argument("--collision")
    add_structure.add_argument("--material")
    add_structure.add_argument("--notes")
    add_structure.add_argument("--support", action="store_true", default=None,
                               help="props may sit on its top face")
    add_structure.add_argument("--absorbable", action="store_true", default=None)

    set_cmd = sub.add_parser("set", help="set fields on a prop or structure")
    set_cmd.add_argument("id")
    set_cmd.add_argument("--pos", type=vector, help="prop position")
    set_cmd.add_argument("--center", type=vector, help="structure center")
    set_cmd.add_argument("--size", type=vector, help="structure size")
    set_cmd.add_argument("--quat", type=vector, help="x,y,z,w")
    set_cmd.add_argument("--yaw", type=float)
    set_cmd.add_argument("--zone")
    set_cmd.add_argument("--kind")
    set_cmd.add_argument("--group")
    set_cmd.add_argument("--support")
    set_cmd.add_argument("--stacked-on", dest="stacked_on")
    set_cmd.add_argument("--value", type=int)
    set_cmd.add_argument("--notes")
    set_cmd.add_argument("--role")
    set_cmd.add_argument("--shape")
    set_cmd.add_argument("--collision")
    set_cmd.add_argument("--meets")

    add_def = sub.add_parser("add-def", help="add or update a defs[] entry")
    add_def.add_argument("--id", required=True)
    add_def.add_argument("--value", type=int, help="valueUnits (required for a new def)")
    add_def.add_argument("--kind")
    add_def.add_argument("--notes")

    move_cmd = sub.add_parser("move", help="move by absolute target or delta")
    move_cmd.add_argument("id")
    move_cmd.add_argument("--to", type=vector)
    move_cmd.add_argument("--delta", type=vector)
    rotate_cmd = sub.add_parser("rotate", help="rotate by yaw or quaternion")
    rotate_cmd.add_argument("id")
    rotate_cmd.add_argument("--yaw", type=float)
    rotate_cmd.add_argument("--quat", type=vector)

    remove_cmd = sub.add_parser("remove", help="remove props/structures by id")
    remove_cmd.add_argument("ids", nargs="+")
    remove_cmd.add_argument("--update-expected", dest="update_expected", action="store_true",
                            default=None,
                            help="also recompute level.expected (the design total)")
    seat_cmd = sub.add_parser("seat", help="re-seat props on their support")
    seat_cmd.add_argument("ids", nargs="*")

    derive_cmd = sub.add_parser("derive-supports",
                                help="derive parent pointers from geometry (dry run by default)")
    derive_cmd.add_argument("ids", nargs="*")
    derive_cmd.add_argument("--apply", action="store_true", help="write the missing pointers")

    check_cmd = sub.add_parser("check", help="validate the resident document")
    check_cmd.add_argument("--report", action="store_true")
    check_cmd.add_argument("--strict", action="store_true")

    export_cmd = sub.add_parser("export", help="validate + write canonical placement.json")
    export_cmd.add_argument("--to", help="write a copy here instead of the level file")
    export_cmd.add_argument("--force", action="store_true",
                            help="write even when the gate is red (work in progress)")

    render_cmd = sub.add_parser("render", help="render one frame to PNG")
    render_cmd.add_argument("--preset")
    render_cmd.add_argument("--camera", help="inline camera as JSON")
    render_cmd.add_argument("--engine")
    render_cmd.add_argument("--res", type=pair, help="1280x720")
    render_cmd.add_argument("--samples", type=int)
    render_cmd.add_argument("--out", help="output directory")
    render_cmd.add_argument("--name", help="file name (without .png)")
    render_cmd.add_argument("--timeout", type=float, default=None,
                            help="request timeout (a 1080p EEVEE frame can take minutes)")

    camera_cmd = sub.add_parser("camera", help="add/update/remove a camera")
    camera_cmd.add_argument("--id")
    camera_cmd.add_argument("--type")
    camera_cmd.add_argument("--location", type=vector)
    camera_cmd.add_argument("--target", type=vector)
    camera_cmd.add_argument("--lens", type=float)
    camera_cmd.add_argument("--coverage", type=float, help="ortho coverage in metres")
    camera_cmd.add_argument("--remove")
    camera_cmd.add_argument("--list", action="store_true")

    preset_cmd = sub.add_parser("preset", help="add/update/remove a render preset")
    preset_cmd.add_argument("--id")
    preset_cmd.add_argument("--camera", dest="camera_id")
    preset_cmd.add_argument("--engine")
    preset_cmd.add_argument("--res", type=pair)
    preset_cmd.add_argument("--samples", type=int)
    preset_cmd.add_argument("--remove")
    preset_cmd.add_argument("--list", action="store_true")

    undo_cmd = sub.add_parser("undo", help="undo the last N committed operations")
    undo_cmd.add_argument("--steps", type=int, default=1)

    sub.add_parser("shutdown", help="export if dirty and stop the editor")

    raw_cmd = sub.add_parser("raw", help="send a JSON request body as-is")
    raw_cmd.add_argument("body")

    batch_cmd = sub.add_parser("batch", help="apply a JSON ops list atomically")
    batch_cmd.add_argument("file", nargs="?", help="default: stdin")

    sub.add_parser("ops", help="list the ops the editor accepts")
    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "command", None):
        parser.print_help()
        return 2
    if args.command == "ops":
        for name, text in sorted(editor_core.OP_HELP.items()):
            print("%-16s %s" % (name, text))
        return 0
    try:
        payload = request_for(args)
    except json.JSONDecodeError as error:
        editor_http.fail("bad JSON argument: %s" % error)
        return 2
    timeout = args.timeout if getattr(args, "timeout", None) else 120.0
    if payload.get("op") == "render":
        timeout = max(timeout, 900.0)
    try:
        response = editor_http.call(payload, endpoint=args.endpoint, timeout=timeout)
    except editor_http.EditorUnavailable as error:
        editor_http.fail(str(error))
        return 2
    if args.json:
        print(json.dumps(response, ensure_ascii=False, indent=2))
    else:
        print_summary(response)
    return 0 if response.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
