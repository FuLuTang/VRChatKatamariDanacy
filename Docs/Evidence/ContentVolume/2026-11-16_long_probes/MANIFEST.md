# Round O1 bring-back (Windows @ 02e1262)

Windows HEAD: 02e1262 (merged 78ee9c1 then 02e1262 ff-only; 5 local files were byte-identical to origin and were restored with explicit `git checkout --` lists).
`ProjectSettings/ProjectSettings.asset` untouched. No stash/reset/commit/push.

## Files to bring back
1. `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity` — 1 line: `BallView.smoothCoreGrowth: 1 -> 0` (see `storymode_smoothcoregrowth.diff`). `useVisualCoreRadius: 1` and the `BallGrowthDiagnostics` component unchanged.
2. Compile-noise batch: 24 U# program `.asset` + 25 `SerializedUdonPrograms/*.asset` (`.meta` of one). Not functionally different except `SerializedUdonPrograms/eb192fc9…asset`, which gained the 40 `SystemInt64` entries of the Step A `long` fields (§ probe 1 evidence).
3. No new `.meta` files (probe temp files created and deleted: `Labs/LongProbe/` + `Editor/LongProbeEditor.cs` + orphan `SerializedUdonPrograms/e41903d7….asset/.meta`).

## Left alone on Windows (pre-existing, not this round)
`Labs/CpuSkirmish/CpuSkirmish.unity`, `Labs/PhysicsTestField/PhysicsTestField.unity` (protected), their `.asset` siblings + 20 more program `.asset` (signature noise), `ProjectSettings/ProjectSettings.asset`, untracked `_RefUnlicensed.meta` and 10 `Docs/Evidence/PhysicsTestField/2026-11-15_pass*` files (older rounds).

## Linux-side evidence (already written in repo)
`Docs/Evidence/ContentVolume/2026-11-16_long_probes/{README.md,ptf_v1_matrix.tsv,ptf_v1_console.log,storymode_smoothcoregrowth.diff,working_tree_numstat.tsv}`;
`Docs/Research/ContentVolume/UNITY_PROBES_FOR_LONG.md` §6 appended with the measured lines.
