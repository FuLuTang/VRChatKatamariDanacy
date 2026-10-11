# P20 asset preview guardrails handoff

## Scope and changes

This handoff records P20 work in the authorized scopes: `Docs/tools/asset_preview/`, `Docs/tests/test_asset_preview.py`, `Docs/tests/test_preparation_content.py`, plus the specifically approved in-memory ZIP inventory API addition under `Docs/tools/offline_quality/`.

- `stage_glb.py` repairs the public `embed_approved_palette` wrapper around the renamed private implementation and normalizes malformed GLB/schema exceptions to `ValueError`.
- Only exact package IDs, fixed repository-relative Download paths and the lead-approved archive SHA-256s are accepted: Food `cdad90853682499b94c9fda2f87678b24bfd8f3264e0ed323f6b6a27fd7c6f6f`; Nature `fa7974a0d342bfe63c38664ba9f8ec1a4aab8ea25f099bdc56870e33588c4d9d`. Package mapping is by ID, not list position. Manifest/archive paths are constrained below the repository download root; symlinked path components are rejected.
- Archives are read once into bounded immutable byte snapshots, hash-checked against those fixed digests, metadata-checked through `inspect_zip_bytes`, then selected members/palette are streamed from `ZipFile(BytesIO(snapshot))`. The archive tree is never extracted. Only selected entries are decompressed in memory, with declared and actual read sizes bounded.
- Palette embedding accepts only exact `Textures/colormap.png`, bounded bytes and PNG signature/header/dimensions; all remaining URIs are rejected. Source-member SHA-256 (pre-rewrite) and staged preview SHA-256 (post-rewrite) are separate fields.
- All intended staging/evidence outputs are preflighted for existing files before archive reads/writes; the run uses a new `selection_batch2.json` rather than overwriting existing selection evidence. Writes are still not atomic: failure during file output can leave partial files; inspect/remove only under separate authorization, no automatic cleanup is done.
- `render_windows.py` checks `bpy.app.background` and requires startup `--factory-startup --disable-autoexec` before factory reset. Before any source stat/read or output creation, a path-component guard rejects any source/output containing a case-insensitive exact `VRChatKatamariDanacyUnity` component; this is independent of the script's deployment location. The repo-derived Unity guard remains an additional check. Script verifies fixed 12-item filename allowlist/hashes, separates source/output, rechecks files before import, and gates finite dimensions/scale plus object, mesh, geometry, material and texture budgets. Display normalization remains preview-only.
- `test_preparation_content.py` adds one host-side structure test for the 24-case, 27-column InputExperiments CSV: unique IDs, empty `observed`/`evidence`, all `NOT_RUN`.

## Test evidence

Commands:

```sh
python3 -m unittest Docs.tests.test_asset_preview Docs.tests.test_preparation_content -v
python3 -m unittest discover -s Docs/tests -v
```

Latest focused run before the final path addition: 22 tests OK. After adding the deployment-independent path guard, the focused static guard test passed and the full suite passed: 57 tests OK. Asset-artifact consistency check skips if generated preview evidence is absent; when present, it checks metadata consistency but does not prove Blender process isolation.

## Remaining limits / not claims

- No Blender script or Windows process was run in this task. Source guard assertions were host-side/static tests only. The prior lead-reported 12 renders are separate evidence and do not prove these changed guardrails ran in Blender.
- PNG validation checks signatures/IHDR dimensions, not complete PNG decoding/CRC; glTF validation is a bounded structural subset, not a full standards validator or importer sandbox. Blender's importer remains a complex parser.
- Renderer hash/reopen still has a small filesystem TOCTOU window; isolated staging is assumed. Staging writes are refuse-overwrite but not transactional/atomic and may leave partial output after I/O failures.
- Blender-imported dimensions are Blender import units, not asserted meters or pickup/collision/gameplay sizes. Per-object preview normalization is only for visual comparison; axis/pivot/Unity import semantics remain open.
- No Unity, VRChat, gameplay, licensing, or final asset acceptance occurred. T20/game tasks are not completed by this utility hardening.

## Environment restrictions observed

Unity is on Windows at `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`; the user owns the Editor. Unity was not started; MCP remains unconfirmed. No SSH, Blender, Unity MCP, downloads, account/payment action, commit or push was performed.
