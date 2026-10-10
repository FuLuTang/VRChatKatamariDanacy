# Repository operating rules

## Read first

Read [scope](<Docs/00_SCOPE_DECISIONS.md>), [technical design](<Docs/02_TECHNICAL_CONTRACTS.md>), [operating rules](<Docs/07_AGENT_OPERATING_RULES.md>), [baseline](<Docs/09_REPOSITORY_BASELINE.md>) and [tasks](<tasks.json>).

The v0.3 explicit decisions supersede historical chat. Design parameters are provisional, not measured original-game values. Public interfaces are not frozen until T06 passes its dependencies.

## Layout and ownership

- Unity project root: `VRChatKatamariDanacyUnity/`. Unity commands must use this project path, not repository root.
- Use repository-relative write_scope in tasks.json. Historical paths in imported design chapters are project/package-relative; the normalized task table is the path authority.
- Research, evidence, contracts, art sources, integration and test artifacts live under `Docs/`; marketing stays under `Marketing/`.
- Do not modify archival files in `Docs/Source/`. They are byte-exact provenance, not current execution status.
- Shared Scenes, ProjectSettings, Packages manifests/resolver, core Prefabs and collision layers have one integration writer. Commit Unity .meta files with assets; never regenerate existing GUIDs. Do not commit VPM package source directories (`Packages/com.vrchat.*`); VPM restores them.
- No teammate creation unless the user explicitly requests Agent Teams/teammates. Model labels in the design are planned roles, not proof those models are provisioned.
- Shared-workspace execution requires disjoint write scopes and ordered dependencies. Do not let two agents operate one Unity/Blender session.
- User decision (2026-10-11): Unity runs on the user's Windows PC and the user owns the Editor. Confirmed project root: `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity` (repo root `D:\VRChatKatamariDanacy`). SSH access is available for scoped Git/filesystem operations; Unity MCP remains unconfirmed. Read [Windows setup](<Docs/10_WINDOWS_UNITY_SETUP.md>) and [team kickoff](<Docs/11_TEAM_KICKOFF.md>).
- All implementation/research teammates must actually run `gpt-6-luna`; verify the spawn result before dispatch. The shipped list_agents incorrectly falls back to the Lead model for disposed inactive children; when ambiguous, verify `session/projections` -> `modelSelection.lastUsed` and `next` instead. A name/prompt containing Luna does not switch the model. If actual request evidence reports another model, pause that teammate and correct configuration; no silent fallback. See [routing evidence](<Docs/12_TEAM_MODEL_ROUTING.md>).
- Every delegated brief must repeat that Unity is on Windows, who owns the editor, the confirmed absolute project path (or UNKNOWN), whether MCP is configured, and the allowed write scope. Until explicitly assigned, teammates must not SSH to Windows, call Unity MCP, or modify `VRChatKatamariDanacyUnity/`.
- For this first wave, teammates only prepare T01/T02/T03 documents in disjoint scopes. The Lead owns root tasks.json and shared documentation; do not claim full task acceptance from partial preproduction work.

## Task and evidence discipline

TODO -> READY -> RUNNING -> REVIEW -> DONE; BLOCKED requires a concrete missing condition.
Dependencies must be DONE and external requirements satisfied before READY. An empty directory or documentation does not complete T00. Never mark compile, VR, network, commerce or comfort PASS without the corresponding evidence.

Maintain one shared coordinate system. Avatar and remote decorations never decide gameplay collision. Only confirmed ledger values affect scores, bank transactions or PvP. Item generations and requests must be idempotent. No editor APIs or disk JSON dependencies in Udon runtime.

Run `python3 Docs/tools/validate_plan.py` and `python3 -m unittest discover -s Docs/tests -v` after task/config changes. These checks validate planning only; state any unavailable Unity tests explicitly.

Do not publish/upload, spend money, change live listings, message third parties or handle accounts without explicit authorization. Official entitlements—not PlayerData flags—control paid cosmetics. Paid store remains disabled until required gates pass. Licensing is undecided; do not assign a license on the user's behalf.
