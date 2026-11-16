# `Tools/motion_sim` — the editor's motion evaluator

A small .NET command-line tool that answers "where is each moving prop at time *t*" by
running **the repository's own motion library**, not a copy of it.

```bash
# Through the Python client (builds on first use; no need to know the dll path):
python3 -m Tools.motion_sim.motion_cli status
python3 -m Tools.motion_sim.motion_cli params <placement.v2.json> --items sunflower_001
python3 -m Tools.motion_sim.motion_cli trajectory <placement.v2.json> \
  --items sunflower_001 --seconds 30 --rate 30 --out /tmp/one.json

# Or drive the tool directly:
./build.sh                       # python3 Tools/csharp_check/check.py installs the SDK
dotnet bin/Release/net8.0/motion_sim.dll --level <placement.v2.json> --mode params
```

The level editor calls it through [`motion_cli.py`](motion_cli.py); nothing else should
shell out to it.

## Why it exists, and what it must never become

`MotionMath.cs` is the authority for the original's 13 motion programs
([`Docs/Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md`](../../Docs/Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md)
§12–§15). The repository keeps **one implementation of each piece of logic**, so the editor
does not re-derive those programs in Python or JavaScript:

* `motion_sim.csproj` compiles these files **as linked source**:
  * `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/MotionMath.cs`
  * `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PhaseSeed.cs` (the D-12 seed)
  * `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Core/KatamariMath.cs`
* [`UnityStubs.cs`](UnityStubs.cs) supplies the six `Mathf` functions and the `Vector3` /
  `Quaternion` structs those files use, with Unity's semantics, so they link on Linux
  without Unity. It is not a Unity replacement and never ships into the project.
* [`Program.cs`](Program.cs) is the only logic here: read a `katamari.placement/2`
  document, call the right `MotionMath` entry point per prop, compose the world pose, write
  JSON. It contains **no motion constant** — not a speed, not a period, not a phase rule.
  Where a class needs a number the document does not carry, that number is obtained by
  calling a `MotionMath` function (`HopSpeedCmPerFrame`, `PanicRadiusM`, `SpeedCmPerFrame`,
  `Phase01`) and the limitation is reported in the response's `assumptions`.

If `MotionMath.cs` changes, the next request rebuilds the tool and the editor follows.

## Modes

### `--mode params` (no time simulated)

One row per prop that has a motion or a parent, with everything the editor displays:

| field | meaning |
|---|---|
| `motionClass`, `motion`, `behaviour`, `inherit`, `parent` | declared by the document |
| `updatesPerSecond`, `loop`, `outputSpace` | the class's tier, loop flag and output space |
| `phaseSeed`, `phase` | `PhaseSeed.From(level.id, itemId)` and `MotionMath.Phase01(seed)` |
| `speedSource`, `speedMps`, `speedCmPerSec`, `speedIndex` | resolved speed (path record > type table > explicit) |
| `halfHeightM` | `aabb[1] / 2`, the `h` the classes move about (§6.7.3) |
| `hopSpeedCmPerFrame` | `MotionMath.HopSpeedCmPerFrame(sizeGroup)`, hop only |
| `triggerRadiusM`, `triggerMotionId` | `MotionMath.PanicRadiusM(sizeGroup)`, WaitForPlayer only |
| `assumptions` | every approximation that row relies on |

### `--mode trajectory --seconds S --rate HZ`

Steps every moving prop at **dt = 1 game frame (30 Hz)** and samples the world pose every
`30 / rate` frames. Per prop: `pos` (flat `x, y, z`) and `rot` (flat `x, y, z, w`), rounded to
0.1 mm and 1e-5, plus `followsParent`. `--items a,b` narrows the output; parents of the
requested props are still simulated.

## Assumptions (stated, not hidden)

Every response carries them, and the editor shows them in its motion panel:

1. **Size group 0.** `katamari.placement/2` carries no original size id, so the size-group
   tables run at group 0. Every Make Cancer motion prop is original size 2 (group 0), and
   the hop dip that results (6 cm) is the measured one.
2. **No host queries.** Ground-following, zone membership, and the "is the next step
   blocked" ray are not modelled. Walkers and clouds move as if every step and every turn
   were allowed; the editor draws the WaitForPlayer ring using a terrain height from the
   `.katerrain` sidecar (a query the *editor* makes, in Python).
3. **No player.** A level preview has no observer, so WaitForPlayer reports its radius and
   never triggers.
4. **Phase.** Always the D-12 hash seed. A `props[].phase` in the document is *not* applied,
   because `MotionMath`'s `Reset*` entry points take only a seed; a document that declares
   one gets an assumption line saying so.
5. **Composition.** `world = spawn * local` (spawn-local classes); a child's placement is
   rigid relative to its parent with its own motion on top; the absolute-world classes
   (paths) do not compose onto a parent. This is the one rule that is this tool's decision
   rather than `MotionMath`'s, and it is echoed in every response.

## Output shape

`params`:

```json
{"ok": true, "mode": "params", "level": "_ref_cancer",
 "evaluator": "Tools/motion_sim (compiles MotionMath.cs)",
 "assumptions": {...}, "counts": {...}, "motions": [...], "paths": [...], "props": [...]}
```

`trajectory`:

```json
{"ok": true, "mode": "trajectory", "rate": 30, "frames": 901, "sampleCount": 901,
 "seconds": 30.0, "counts": {...},
 "props": [{"itemId": "...", "motionClass": "spin", "followsParent": false,
            "pos": [x0,y0,z0, ...], "rot": [x0,y0,z0,w0, ...]}]}
```

## Tests

`Docs/tests/leveleditor/test_editor_motion.py` covers both modes against the reference
level: Spin's 4.19 s turn, Sway's 0.342 / 0.030 ellipse of the full model height, Hop's 6 cm
dip, the p23 path's 24.4 cm/s, and the whole level's 30 s trajectory (155 props × 901
samples, ~1 s). Those tests skip themselves when the .NET SDK or the (unlicensed, local)
reference level is absent.
