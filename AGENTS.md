# Repository operating rules

Current, effective rules only. The full authorization history up to 2026-11-14 is archived in [AGENTS_until_2026-11-14](<Docs/Archive/Process/AGENTS_until_2026-11-14.md>); [Docs/Archive/](<Docs/Archive/README.md>) is process history, not current status.

## Read first

[Scope](<Docs/00_SCOPE_DECISIONS.md>), [game design](<Docs/01_GAME_DESIGN.md>), [technical design](<Docs/02_TECHNICAL_CONTRACTS.md>), [open decisions](<Docs/OPEN_DECISIONS.md>) and [tasks.json](<tasks.json>). The v0.3 explicit decisions supersede historical chat. Design parameters are provisional, not measured original-game values. Public interfaces are not frozen until T06.

## Layout and naming

- `VRChatKatamariDanacyUnity/` — the Unity project (Unity 2022.3.22f1, VRChat Worlds SDK 3.10.5 via VPM). Our content lives under `Assets/VRC_Katamari/`.
- `Docs/` — design, research, evidence, art sources. `Docs/Source/` is byte-exact provenance: never modify. `Docs/Archive/` is history: don't extend it except when archiving.
- `Tools/` — developer tooling (e.g. [unity-mcp](<Tools/unity-mcp/README.md>)). `Marketing/` — promotion material only.
- **Name things by what they are, never by task ID or work batch.** No `T04Foo.cs`, `T57_*`, `third_batch/`, `P18_*`. Task IDs belong only in `tasks.json`, commit messages and prose references.
- Keep one implementation of each piece of logic. Don't add parallel "draft"/"reference" copies, one-shot builders, or self-made test harnesses when Unity or NUnit already provides the mechanism.

## Unity environment

- Unity runs on the user's Windows PC (SSH alias `pc114514`); the user owns the Editor. Project root `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`, repo root `D:\VRChatKatamariDanacy`.
- Drive the Editor through MCP for Unity v10.3.0 with `python3 Tools/unity-mcp/unity_mcp.py` (SSH-forwards to Windows `127.0.0.1:8080`). The CUA driver may be used for things MCP cannot do. Only one agent operates Unity/MCP/CUA at a time.
- Build scenes, prefabs and components directly through MCP; check `state` and `read_console` after each batch. Never close, restart or relaunch the Editor (a GUI relaunch runs it as administrator). The Editor may not recompile while unfocused: use `refresh_unity` or briefly focus only the Unity window.
- `ProjectSettings/ProjectSettings.asset` has a pre-existing uncommitted change on Windows that must stay untouched (don't read, diff, stage, restore). Other ProjectSettings/Packages changes need Lead approval. `Packages/manifest.json` now includes the user-installed `com.coplaydev.unity-mcp`.
- Commit `.meta` files with assets and never regenerate existing GUIDs (move/rename files together with their `.meta`). Never commit VPM package sources (`Packages/com.vrchat.*`). `Assets/SerializedUdonPrograms/*.asset` stay tracked; signature-only diffs there are compile noise and are committed or discarded as a batch.
- Play Mode, Build & Test and private uploads of this project's world are allowed; record each upload (world ID, scene, commit) under `Docs/Evidence/`. No publication, and no other account, profile, avatar, friend, group, payment or settings actions.

## Code and tests

- UdonSharp runtime code under `Assets/VRC_Katamari/Runtime/<Module>/`, editor-only code under `Assets/VRC_Katamari/Editor/`, NUnit EditMode tests under `Assets/VRC_Katamari/Tests/`, hand-inspectable lab scenes under `Assets/VRC_Katamari/Labs/`. Run tests with MCP `run_tests`.
- No editor APIs or disk JSON in Udon runtime. Respect UdonSharp's language limits (no user structs or non-behaviour classes).
- Gameplay invariants: one shared world coordinate system; avatars and remote decorations never decide gameplay collision; only confirmed ledger values affect scores, banking or PvP; item generations and requests are idempotent.

## Delegation and Git

- The Lead (main conversation) dispatches, reviews and integrates. Small tasks go to background subagents routed explicitly to `dsh-opencode-go/deepseek-v4.1-flash`. Every brief says: use the todo tool; exact write scope; the Unity facts above; whether this subagent is the Unity operator. No teammates unless the user asks.
- Subagents never run Git writes. The Lead reviews every output for quality and abstraction before committing, and may commit/push reviewed work to `origin/main` on either checkout (Linux or Windows), keeping history fast-forward and never leaving both checkouts dirty in the same paths. Exclude credentials, raw downloads, Unity caches and SDK sources.

## Task discipline

TODO -> READY -> RUNNING -> REVIEW -> DONE; BLOCKED needs a concrete missing condition. Never mark compile, VR, network, commerce or comfort PASS without matching evidence. Desktop interaction in VRChat is the left mouse button. Licensing is undecided; don't assign one. Don't spend money, message third parties or change live listings.

After task/config changes run `python3 Docs/tools/validate_plan.py` and `python3 -m unittest discover -s Docs/tests`. These check planning data only; state which Unity tests did or did not run.
