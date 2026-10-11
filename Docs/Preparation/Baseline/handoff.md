# P16 handoff

**schemaVersion: 1** · Date: 2026-10-11 (UTC session date). Work package: P16 / board card `task-22`. Write scope used: `Docs/Preparation/Baseline/` only.

## Deliverables and counts

- `README.md`: baseline observations, official-support distinction, metadata hashes and future workflow (1 file).
- `recovery.md`: future user-run clone/VCC/VPM restoration checklist (1 file).
- `licensing_boundary.md`: SDK-source/template distribution boundary (1 file).
- `handoff.md`: this report (1 file).
- `metadata.json`: machine-readable `schemaVersion: 1` snapshot of source hashes/byte counts, observed project declarations, official support evidence, resolver/default-scene observations, and runtime checks (all `NOT_RUN`).
- Total: **4 Markdown documents plus 1 JSON metadata artifact**.

## Inspected inputs

Read current four Baseline Markdown files and exact repository checkout `ProjectVersion.txt`, `Packages/manifest.json`, `Packages/vpm-manifest.json`, `Packages/packages-lock.json`, `Packages/.gitignore`, and default scene plus `.meta`. Repository glob found no SDK package `metadata.json` or `com.vrchat.*` source/resolver package directory in this checkout. This is not a check of the separate Windows installation; no claim that Windows SDK files are missing/broken is made. `metadata.json` records seven source paths with relative path, byte count, and SHA-256. VCC and independent UdonSharp version/state remain unknown.

## Official evidence

- VRChat current Unity recommendation page states `2022.3.22f1`: https://creators.vrchat.com/sdk/upgrade/current-unity-version/ (fetched 200).
- VRChat VCC/VPM source-control policy for included manifests/resolver, excluded SDK source, restore and resolver exception: https://vcc.docs.vrchat.com/vpm/source-control/ (fetched 200).
- A GitHub resolver page fetch returned HTTP 503; no claims rely on it. Current VCC version, separate UdonSharp version, and official “current” status for Worlds SDK `3.10.5` remain **unknown**.

## Commands and results

- `pwd` — `/home/fulutang/Documents/Github/VRChatKatamariDanacy`.
- Python 3 standard-library read-only `hashlib`/`pathlib` command computed seven source-file byte counts and SHA-256 values; these are recorded in `metadata.json`.
- Python 3 `json` parser validated `metadata.json`; schemaVersion 1 fields and all runtimeChecks as `NOT_RUN` were asserted. Repository `glob` checked the Unity project checkout for SDK metadata and package-source paths.
- No project runtime/tool command was executed. No Unity, VCC restore, SSH, Unity MCP, Blender, install, asset download, account/payment, Git write, commit/push or publication.
- No planning task/config files changed, so `validate_plan.py` and test suite were not run.

## Status and limitations

Documentation prep only; **no project compile claim**. Unity Editor, Udon compilation, ClientSim, VR, network, build, package restore and runtime checks are all **NOT_RUN**. T00 remains gated for user-owned Editor evidence. `3.10.5` is observed manifest metadata, not guessed as official-current; VCC/UdonSharp standalone version unknown. Template/default-scene specific redistribution terms were not located and remain unverified. Restore steps are future instructions, not performed actions.
