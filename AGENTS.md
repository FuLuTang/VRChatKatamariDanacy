# Repository operating rules

Current, effective rules only. The full authorization history up to 2026-11-14 is archived in [AGENTS_until_2026-11-14](<Docs/Archive/Process/AGENTS_until_2026-11-14.md>); [Docs/Archive/](<Docs/Archive/README.md>) is process history, not current status.

## Original-first principle

Some data structures and behaviours take priority for restoration to the original game. When our version lacks an object kind or behaviour that the extracted original clearly handles a certain way, read that as a signal our design may be wrong — consider restoring the original's structure and handling instead of patching around it. Every known divergence gets a row (original evidence, ours, reason, decision, owner) in the [parity audit's Divergences table](<Docs/Design/Parity/PARITY_AUDIT.md>); the default decision is restore unless a reason is recorded. Owner decisions — dated, with the one-line why and a pointer to the detailed entry — live in [OPEN_DECISIONS](<Docs/OPEN_DECISIONS.md>); this file carries only rules that bind every agent. Active docs and OPEN_DECISIONS state only the current decision: when a decision is overturned, delete the old text instead of marking it superseded or keeping it for comparison — git history is the record (owner 2026-11-16).

## Read first

[Scope](<Docs/00_SCOPE_DECISIONS.md>), [game design](<Docs/01_GAME_DESIGN.md>), [technical design](<Docs/02_TECHNICAL_CONTRACTS.md>), [open decisions](<Docs/OPEN_DECISIONS.md>) and [tasks.json](<tasks.json>). The v0.3 explicit decisions supersede historical chat. Design parameters are provisional, not measured original-game values. Public interfaces are not frozen until T06.

## Layout and naming

- `VRChatKatamariDanacyUnity/` — the Unity project (Unity 2022.3.22f1, VRChat Worlds SDK 3.10.5 via VPM). Our content lives under `Assets/VRC_Katamari/`.
- `Docs/` — design, research, evidence, art sources. `Docs/Source/` is byte-exact provenance: never modify. `Docs/Archive/` is history: don't extend it except when archiving.
- Reference-game assets and ripped meshes — `Docs/Design/Levels/_ref_*`, `VRChatKatamariDanacyUnity/Assets/_RefUnlicensed/`, `~/KatamariRef` — never enter git, the committed Unity project, or any upload; they are read-only local references.
- `Tools/` — developer tooling (e.g. [mcp](<Tools/mcp/README.md>)). `Marketing/` — promotion material only.
- **Name things by what they are, never by task ID or work batch.** No `T04Foo.cs`, `T57_*`, `third_batch/`, `P18_*`. Task IDs belong only in `tasks.json`, commit messages and prose references.
- Keep one implementation of each piece of logic. Don't add parallel "draft"/"reference" copies, one-shot builders, or self-made test harnesses when Unity or NUnit already provides the mechanism.

## Unity environment

- Unity runs on the user's Windows PC (SSH alias `pc114514`); the user owns the Editor. Project root `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`, repo root `D:\VRChatKatamariDanacy`.
- Drive the Editor through MCP for Unity v10.3.0 with `python3 Tools/mcp/mcp_cli.py` (SSH-forwards to Windows `127.0.0.1:8080`). Web work in the user's real Chrome goes through Playwright MCP (`--server browser`; Windows scheduled task `DshPlaywrightMcp`, token stays only in its Windows start script — never copy it into the repo). The CUA driver may be used for things MCP cannot do. Only one agent operates Unity at a time, and only one agent drives the browser at a time.
- Build scenes, prefabs and components directly through MCP; check `state` and `read_console` after each batch. Never close, restart or relaunch the Editor (a GUI relaunch runs it as administrator). The Editor may not recompile while unfocused: use `refresh_unity`, or briefly focus only the Unity window with the CUA driver (user approved a one-click CUA foreground poke for this on 2026-11-14; don't drive the UI further).
- `ProjectSettings/ProjectSettings.asset` has a pre-existing uncommitted change on Windows that must stay untouched (don't read, diff, stage, restore). Other ProjectSettings/Packages changes need Lead approval. `Packages/manifest.json` now includes the user-installed `com.coplaydev.unity-mcp`.
- Commit `.meta` files with assets and never regenerate existing GUIDs (move/rename files together with their `.meta`). Never commit VPM package sources (`Packages/com.vrchat.*`). `Assets/SerializedUdonPrograms/*.asset` stay tracked; signature-only diffs there are compile noise and are committed or discarded as a batch.
- Play Mode, Build & Test and private uploads of this project's world are allowed; record each upload (world ID, scene, commit) under `Docs/Evidence/`. No publication, and no other account, profile, avatar, friend, group, payment or settings actions.

## Code and tests

- UdonSharp runtime code under `Assets/VRC_Katamari/Runtime/<Module>/`, editor-only code under `Assets/VRC_Katamari/Editor/`, NUnit EditMode tests under `Assets/VRC_Katamari/Tests/`, hand-inspectable lab scenes under `Assets/VRC_Katamari/Labs/`. Run tests with MCP `run_tests`.
- No editor APIs or disk JSON in Udon runtime. Respect UdonSharp's language limits (no user structs or non-behaviour classes).
- Game-file parsers derived from third-party code (e.g. noclip.website, whose LICENSE is MIT plus a decompilation note) stay outside the repository until the owner chooses the repo licence; do not vendor their code or commit a line-by-line port. A port or derivative must keep the original copyright and licence notice (MIT allows this). The preferred route is a reimplementation from the format spec, which is what we did: that parser lives outside the repo.
- Level editor (`Tools/leveleditor`): the browser GUI (human mode) and headless CLI/HTTP (AI mode, e.g. the planned render CLI `Tools/leveleditor/shot.py`) are thin clients of the same implementation — every GUI operation must be reachable by an agent through a command, and no editing or analysis logic may live only in browser JS; `_ref_*` levels are read-only but fully queryable and renderable. Details go in the planned `Docs/Design/Tools/EDITOR_HUMAN_AI_PARITY.md`.
- Gameplay invariants: one shared world coordinate system; avatars and remote decorations never decide gameplay collision; only confirmed ledger values affect scores, banking or PvP; item generations and requests are idempotent.
- **C# compile gate:** before delivering any C# change, the implementer runs `python3 Tools/csharp_check/check.py` — local Roslyn 4.3.1 mirroring Unity 2022.3, covering `VRCKatamari.Runtime`, `VRCKatamari.Editor`, `Assembly-CSharp-Editor` and `VRCKatamari.Tests.EditMode` in about 15 s ([usage](<Tools/csharp_check/README.md>)); 0 errors is the bar, and the report pastes the last output.
- It checks C# compilation only: the UdonSharp language subset, U# program compilation, EditMode test runs and scene/serialization behaviour still need a Unity round.
- **Local test run:** `python3 Tools/csharp_check/run_tests.py [--filter TEXT]` executes the EditMode tests that need no engine on .NET 8 and reports passed / failed / known difference from Unity / not runnable here against the discovered total ([usage](<Tools/csharp_check/README.md>)). A failure it reports is real; tests that need `GameObject`s or other engine natives stay "not runnable", and local green is not Unity green — the Unity round remains the verdict.
- Every "checked / 0 issues" claim names the scope scanned and its denominator; a claim without a denominator counts as not checked.

## Delegation and Git

- The Lead (main conversation) dispatches, reviews and integrates, and re-checks the routing against the active goal before EVERY subagent dispatch (owner 2026-11-16). The default and only subagent route is `pi-anthropic` + `claude-sonnet-5-5` (Claude Sonnet 5.5) at `reasoning_effort: "high"`, including work that needs network access, search, a headless browser or downloads (owner 2026-11-16). Operating Unity/the Windows Editor keeps the current practice (a long-lived operator agent); the operator's model is recorded in the dispatch brief. Always pass `provider`, `model` and `reasoning_effort` together: omitting the provider or model errors out, and omitting the effort silently falls back to the model default. Every brief says: use the todo tool; exact write scope; the Unity facts above; whether this subagent is the Unity operator; use CodeGraph first for code questions. No teammates unless the user asks.
- Code navigation: this repo has a local CodeGraph index (`.codegraph/`, git-ignored; covers the C# under `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/` and the Python under `Docs/`/`Tools/`). Before reading or grepping code, call the `codegraph_explore` MCP tool with `projectPath` = the repo root (or run `codegraph explore "<symbols or question>"` in a shell); it returns current source plus callers and blast radius in one call. If it looks stale after large edits, run `codegraph sync .` at the repo root. It does not index Unity scenes, prefabs or `.asset` files — use Grep/Read for those.
- Reuse loaded context: when new work extends what a running or recently finished subagent already knows, `send_message` that subagent instead of spawning a new one; spawn fresh only for independent work.
- Subagents never run Git writes. The Lead reviews every output for quality and abstraction before committing, and may commit/push reviewed work to `origin/main` on either checkout (Linux or Windows), keeping history fast-forward and never leaving both checkouts dirty in the same paths. Exclude credentials, raw downloads, Unity caches and SDK sources. Windows can fetch but cannot push over SSH (no credential helper there): bring Windows commits to Linux with `git bundle` + `scp`, fast-forward, push from Linux, then `git pull --ff-only` on Windows.

## Task discipline

TODO -> READY -> RUNNING -> REVIEW -> DONE; BLOCKED needs a concrete missing condition. Never mark compile, VR, network, commerce or comfort PASS without matching evidence. Desktop interaction in VRChat is the left mouse button. Research the original and our own data before designing or upgrading a data format: while the study that decides a schema is pending, record requirements and open questions only — do not propose final schemas or field names. Licensing is undecided; don't assign one. Don't spend money, message third parties or change live listings.

After task/config changes run `python3 Docs/tools/validate_plan.py` and `python3 -m unittest discover -s Docs/tests`. These check planning data only; state which Unity tests did or did not run.
