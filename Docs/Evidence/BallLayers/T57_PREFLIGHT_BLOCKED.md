# T57 Windows preflight history

Status: **The Git/authorization gate is satisfied; the one-run route waiver was used and the subagent failed. Lead fixed the shadowed local, and the user confirmed import/compile with CS1061 gone. The user's single guarded-menu attempt failed after creating partial assets and leaving an empty Untitled scene loaded beside T04_PlatformLab. T57 is BLOCKED pending safe recovery; no Prefab or lab Scene was saved or accepted.**

## Read-only checks

- Linux repo: `/home/fulutang/Documents/Github/VRChatKatamariDanacy`, HEAD `71ec62bc8fc7c59adc0e8c2fa7a88f49251b5de2`, clean.
- Windows repo path exists: `D:\VRChatKatamariDanacy`; HEAD `b5a298cf6c68a1e29c854eff3bdb6a6d1d81e20d`.
- The required authorization ancestor check for `b4b6cf0ee3cecd95aa43a14b5e7bef5f528f0644` failed: Git reported `fatal: Not a valid commit name ...`. Thus the required authorization commit is absent/unavailable in the Windows repository. Per T57 gate, stopped here; did not fetch, pull, sync, or retry with another Git path.
- Read-only Git status query excluding the specifically preserved `ProjectSettings/ProjectSettings.asset` returned no other dirty paths. The excluded file was not opened, read, diffed, hashed, staged, or modified.
- `ProjectVersion.txt`: Unity `2022.3.22f1` (revision `887be4894c44`). `vpm-manifest.json`: VRChat Worlds/Base `3.10.5`. Corresponding package directories exist.
- A Unity process was reported present; its window was not inspected because the authorization-commit gate failed. Current scene, Play/compile state, dirty scene state, and unsaved changes remain **NOT_CHECKED**.

## No-action boundary

At this worker stop, no Unity window was focused or inspected; no project asset, `.meta`, scene, package, or setting was written; no compile, Play, Build & Test, VRChat launch, upload, or account operation was performed. The known preserved settings file was not read or touched. Later Lead-only Git actions are recorded below; they did not operate Unity.

## Lead follow-up: one authorized Git-metadata fetch

- On 2026-10-14 the user explicitly authorized one `git fetch origin main` in the Windows repository, limited to `.git` metadata and followed by a read-only ancestry check; no checkout, merge, pull, worktree changes, or ProjectSettings access was authorized.
- The fetch succeeded and updated only `origin/main`/`FETCH_HEAD` to `3cb5def209c2c96a1f1afbde8faaafb3a517b5ea`. The local Windows `HEAD` remains `b5a298cf6c68a1e29c854eff3bdb6a6d1d81e20d`.
- `b4b6cf0ee3cecd95aa43a14b5e7bef5f528f0644` now existed in the fetched object database, but `git merge-base --is-ancestor ... HEAD` returned false (`AUTH_ANCESTOR=NO`). At that point T57 remained blocked; the Windows working tree and preserved ProjectSettings file were not read or changed by this fetch.

## Lead follow-up: guarded fast-forward (user authorized)

- User then authorized a guarded fast-forward after a read-only path-name review.
- The path-only diff from Windows HEAD `b5a298cf6c68a1e29c854eff3bdb6a6d1d81e20d` to `origin/main` `3cb5def209c2c96a1f1afbde8faaafb3a517b5ea` contained Docs/tests/tasks paths only; no Unity project, ProjectSettings, Packages, or formal scene paths.
- The status query excluded the preserved ProjectSettings file and found no other dirty paths. Lead ran `git merge --ff-only origin/main`; Windows HEAD became `3cb5def209c2c96a1f1afbde8faaafb3a517b5ea`, and the authorization commit is now an ancestor.
- No Editor/window was inspected or operated, and the preserved ProjectSettings file was never opened/read/touched. T57 may resume with a new single operator and repeat Editor/scene safety checks.

## Latest worker stop: model-route evidence

- A subsequent fork loaded all four required skills and read the current task, then requested independently verifiable `openai-codex/gpt-6-luna` route evidence (`session/projections` / `modelSelection.lastUsed`). That tool is not exposed in this runtime, so the worker stopped before any Windows or Unity access.
- No files, project assets, settings, Editor state, or Git state were changed by that worker. The user subsequently chose a new subagent for T57 and explicitly waived independent route-projection verification for this one run, accepting current `gpt-6-luna` parent-session inheritance. T57 was set READY for a fresh single-operator safety preflight.

## Subsequent operator failure and manual handoff (2026-10-14)

- The replacement subagent `436d99b5-827b-45a9-b654-e856e0f60f43` passed read-only Git/editor preflight, then reported first Unity import/compile and a CS0170 at `BallLayerLabTestRunner.cs(631,55)`. It planned one targeted repair/reimport but subsequently failed before completion without a closing report.
- Lead's later read-only snapshot of Unity PID 42244 / HWND 1971566 showed `T04_PlatformLab`, no dirty marker, Play not active, the old SDK `Build Succeeded!` modal, and a visible Console error `CS1061` at `BallLayerLabTestRunner.cs(555,25)` (`float` has no `rollDecoy` member). Lead's read-only Windows source check found `RunFollow(BallLayerLabDescriptor d,...)` declares a local `float d` earlier in the same method, shadowing the descriptor parameter. This is not evidence that the planned CS0170 repair succeeded; the two observations may reflect separate/current compiler findings.
- Lead used no input, did not save or alter the scene, and did not read or touch `ProjectSettings.asset`. The old build modal is not a T57 result; no T57 compile PASS, accepted prefab, or accepted lab scene exists.
- The user asked to operate Unity manually and reported closing the old SDK result popup. A fresh Lead read-only screenshot verified the modal is gone, the active scene is still `T04_PlatformLab`, no dirty marker is visible, and Play is not active; CS1061 remained visible.
- At the user's request, Lead made one bounded fast-source fix in the authorized T57 source: renamed local `float d` to `deltaYaw` and updated its references in `RunFollow`. Before copying, the Windows target file matched the evidence payload by SHA-256; after copying, the Windows target matched the updated payload SHA-256. No `.meta`, scene, or settings file was written.
- Immediately after copying, the Editor still showed the old Console error. The user then focused Unity and reported normal import/compile complete with no new errors. A fresh Lead screenshot no longer shows CS1061; it shows prior `Unknown`/UdonBehaviour `--enable-udon-debug-logging` runtime entries, not a current compiler error. The old T04 scene remains clean and Play inactive.
- The user's one menu invocation failed with `InvalidOperationException: Could not create isolated prefab-build scene; original scene remains loaded.` The live snapshot shows T04_PlatformLab and an empty Untitled scene loaded; the window title is Untitled. Read-only Windows status confirms seven `.mat` assets plus `.meta` files exist under Materials; Prefabs/Scenes directories exist but contain no prefab or `.unity` scene file.
- The user selected `Remove` for the empty Untitled scene. A fresh screenshot verifies only `T04_PlatformLab` remains loaded, no dirty marker is visible, and Play is inactive. The seven material assets and empty output folders remain untouched.
- A subsequent read-only Windows Git status (excluding protected `ProjectSettings.asset`) revealed multiple modified `Assets/SerializedUdonPrograms/*.asset` paths outside T57's authorized write scope. Their contents were not read, diffed, hashed, staged, restored, or cleaned. Stop all Unity operations and preserve these paths; T57 remains BLOCKED until the user explicitly authorizes any broader inspection.
