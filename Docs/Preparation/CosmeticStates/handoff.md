# P15 cosmetic states handoff

## Delivery

- Task: P15, board task-21 (Lead-managed task card; not changed here).
- Scope written: `Docs/Preparation/CosmeticStates/` only.
- Files: `README.md`, `state_transitions.json`, `cases.csv`, this handoff.
- `cases.csv`: 18 distinct offline review cases. Expected assertions are separate from actual results and evidence; all 18 actual/evidence cells are blank and all statuses are `NOT_RUN`.
- `state_transitions.json`: schemaVersion 1, `paidStoreEnabled: false`, marked `proposal_not_frozen`; contains 14 described state IDs, 18 transition rules, invariants, and out-of-scope items.

## Design boundaries

Free default is always available. Preview is not acquisition. Optional appearance requires official platform entitlement confirmation; preference/PlayerData/local cache never grants paid access. Loading, unknown, timeout, and errors keep the free fallback and are not equivalent to either ownership or revocation. Authoritative not-owned/revoked results use the free fallback. Preference restore failure must not erase or overwrite unrecovered prior data. Rendering/remote summaries cannot affect radius, abilities, collision, adjudication, queue priority, or score. The official adapter/API and product binding remain deferred to T34–T36; this proposal does not freeze enums or interfaces.

## Checks and results

- Read the required scope, technical contract, operating rules, baseline, task table, acceptance tests, monetization guidance, and terminology handoff before authoring.
- `state_transitions.json` was created as ordinary JSON with an explicit schemaVersion. No runtime code was changed.
- No Unity/Blender/MCP/SSH, commerce, live entitlement, integration, or gameplay test was run. No project compilation is claimed.
- No project-scoped automated validator was run for these docs-only files.

## NOT_RUN / limits

- All 18 CSV test cases: NOT_RUN (no actual or evidence recorded).
- English/Japanese native-language review, HUD/layout behavior, runtime implementation, Udon/Unity compilation, platform entitlement/API validation, preview rendering, persistence integration, and real purchase/refund/revocation: NOT_RUN.
- This is preparation only, not task acceptance, interface freeze, commerce enablement, or completion of T24/T25/T35/T36.
