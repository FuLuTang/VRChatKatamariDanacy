# P09/P17 decision matrix and release RFC handoff

**Prepared: 2026-10-11. Status: draft for read-only review; no scope decision made.**

## Deliverables

- [RuleDecisions/README.md](../RuleDecisions/README.md) — evidence boundaries and usage.
- [RuleDecisions/decisions.json](../RuleDecisions/decisions.json) — official facts vs unknowns vs project choices/prototype values, candidate options, units, risks, and decision owners.
- [comparison.md](comparison.md) — two-map target vs authorized one-map-complete-experience downgrade.
- [proposed_dag.json](proposed_dag.json) — current dependency snapshot and two alternatives, explicitly not applied to `tasks.json`.

## Evidence and decisions

- Official facts are constrained to the already reviewed first-party FAQ/announcement references S02/S03 in the T01 matrix. These establish broad KatamariBall format/objective, not detailed exchange, swallow/KO/respawn, or PVP item deployment rules.
- `unknown` is preserved for unsupported original rules. The project’s KO/respawn and Magnet/Rocket/Jump directions are described separately; prototype settings are not promoted to original values or measured facts.
- Alternatives cover match format, scoring/exchange, swallow/threshold, KO/respawn, PVP power-ups, map count/fairness, growth/collision values, and commercial SKU. No threshold, map, SKU, price, or user choice was assigned.
- RFC identifies the DAG mismatch: T39 permits one map while T37/T38/T40 still depend on T32. Two-map option preserves current task graph; single-map example shows proposed dependency edits and preserves unselected T31/T32 as deferred real work. No task state or root dependency was changed.

## Validation and limits

- JSON syntax and internal proposal consistency checked offline; no task-plan files changed. No original-game session, Unity, VR, multiplayer, commerce, SKU, or fairness experiment was run.
- Unity runs on the user’s Windows PC at `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`; the user owns the Editor; it was not started. Unity MCP is unknown.
- No SSH, Blender, Unity/MCP, asset downloads, account/payment, Git commit/push, publication, or Unity-project access.
- Write scope was limited to `Docs/Preparation/RuleDecisions/` and `Docs/Preparation/ReleaseRFC/`. Awaiting `offline-quality` read-only review; author will handle any requested edits.
