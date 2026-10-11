# P13 — offline sphere contribution and coordinate math cases

Status: **host-side reference experiment only**. This is not production physics, collision resolution, Udon, Unity import validation, a performance result, or interface approval. Numeric fixture units are abstract game-volume units unless a test explicitly says world metres. `earnedVolumeUnits` remains a confirmed game ledger quantity; neither rendered mesh volume nor AABB volume is its authority.

## Files and reproducibility

- [`vectors.json`](vectors.json): 25 named deterministic examples/boundaries. Source asset axes, baked transforms and physical units are intentionally not claimed by these fixtures.
- [`math_model.py`](../../tools/prep_math/math_model.py): standard-library host reference helpers.
- [`test_math_model.py`](../../tests/prep_math/test_math_model.py): unittest coverage.
- Run from repository root: `python3 -m unittest discover -s Docs/tests/prep_math -v`.

## Model assumptions tested

1. Radius and diameter are distinct quantities: `diameter = 2r`; an equivalent sphere radius may be derived from assigned game volume units by `r = cbrt(3V/(4π))`. This does not convert mesh bounds into game value.
2. Each accepted item contributes once, keyed by item instance. Repeating the same item/value is idempotent; a conflicting duplicate rejects. Removing a contribution removes that item's ledger value.
3. Directional support is recomputed from the current candidate points plus core radius, rather than retaining a historical maximum. Thus removing a long rod can reduce support; a growing core can cover a short candidate. This is only a sampled support-distance illustration, not dynamic contact/physics.
4. Bank/reset visual appearance is independent from the authoritative ledger. A smaller visual core or item hiding does not decrement earned volume unless a separate confirmed transaction changes the ledger.
5. Transform functions use finite 4×4 matrices with column vectors: `p_world = M_world_from_local * p_local`; chain composition `T_parent * T_child`. Direction vectors use `w=0` (no translation); points use `w=1`. Attachments are stored in the visual root local pose and converted through the full parent chain. The attachment-invariance fixture explicitly models CoreMesh and AttachmentRoot as sibling children: CoreMesh scale changes while AttachmentRoot's own transform path stays fixed; it verifies this host-side hierarchy assumption, not a runtime scene.
6. Positive finite component scales only in this proposed helper; zero/negative/mirrored transforms are rejected rather than guessed. Matrix inverse rejects singular transforms. Direction normalization rejects vectors whose largest absolute component is at or below `1e-15`; this is a deliberate near-zero cutoff, covered immediately below and above the boundary. Directions are normalized where a support direction is required.

## OPEN / not established

- A fixture's known frame is not evidence that any Kenney/Quaternius file uses that axis order, handedness, forward/up, unit, origin, or pivot. Source asset version and import chain remain OPEN. Do not add a guessed 90-degree correction. A real conversion requires source geometry basis probes and full export/import/parent transform evidence.
- Long-rod/flat-slice sampled proxies do **not** certify dynamics, collisions, support stability, pickup behavior, network agreement, Unity behavior, or performance.
- `N=32` directions plus up to four extreme candidates is a provisional proposal from the design contract only; it is not frozen, benchmarked, or endorsed here.
- Finite matrix and transform round trips establish algebraic invertibility for the provided fixtures only; a successful roundtrip cannot prove a source-to-target axis mapping is correct.
