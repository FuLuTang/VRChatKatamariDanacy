# Cosmetic-state preparation (P15 / task-21)

This directory is offline preparation for the free-default appearance, preview, entitlement uncertainty, and persistence-recovery state behavior. It is a proposal for later review, not runtime code, a frozen public interface, or proof of a commerce integration.

## Scope and authority

- Free/default cosmetics remain available without a store, entitlement response, or persistent-data recovery.
- Official platform entitlements are the sole authority for a paid cosmetic. PlayerData, selected cosmetic IDs, remote cosmetic summaries, local caches, and UI state are not proof of ownership.
- A pending/unknown result is not ownership and is not a denial: preserve the free default while awaiting a definitive official result. Do not invent official API signatures or product IDs; defer platform binding to T34–T36.
- Paid store is disabled (`paidStoreEnabled=false`). Previews and selection can be exercised with local/mock fixtures without real products or purchases.
- Cosmetics and remote summaries are presentation only. They must never change collision/core radius, abilities, pickup/score values, movement, queue priority, threat/protection state, or match authority.
- Any entitlement loss, timeout, failed recovery, or revoked access falls back to a free default without changing collision, adjudication, or score. Do not destroy user preference data merely because authority is temporarily unavailable.

## Files and machine-data conventions

- [`state_transitions.json`](state_transitions.json) is a versioned, explicitly non-frozen proposal.
- [`cases.csv`](cases.csv) contains expected assertions and separate actual/evidence columns. All actual/evidence fields are blank and every case is `NOT_RUN` until supported by evidence.
- CSV uses UTF-8, comma delimiter, one header row, LF line endings; `schemaVersion` is included as a column (value `1`). Empty strings represent unmeasured actual/evidence, never pass.

## Terminology

“Entitlement” means official platform-confirmed access, not a local flag. “Selected” is a preference, not permission. “Preview” is a local presentation sample, not acquisition. “Default fallback” is the usable free cosmetic. “Unknown/pending” means no trustworthy definitive entitlement decision is available yet. “Revoked” denotes an authoritative loss result; a timeout is not revocation.
