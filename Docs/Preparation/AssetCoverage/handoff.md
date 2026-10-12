# P10 asset coverage handoff

## Deliverables

- [`coverage.csv`](coverage.csv): 36 unique `PIC-01`–`PIC-36` rows cross-checked against the original concept list, the frozen two-package ZIP inventory, and the 12 P21 rendered selections. File-name presence, visual review, and concept fit are separate columns.
- [`next_download_needs.csv`](next_download_needs.csv): 17 concept/shape/authoring needs. Every row is gated, names no selected package or URL, and sets new-package download authorization to `NO`.
- [`README.md`](README.md) and both row schemas describe evidence states, known mapping corrections, and restrictions.

## Review findings

- Reconciled stale `unknown` mappings in the P02 manifest for PIC-16 and PIC-19–PIC-22: the frozen Food Kit inventory contains carton, cup, plate, bowl, and spoon filename candidates. They remain metadata-only until the relevant model is visually reviewed.
- Visual mismatch findings remain explicit: PIC-11 bread is a slice not a loaf; PIC-15 cheese-cut is a thin holed slice not a wedge; PIC-28 pot_small is empty and flower_redA is a separate file; PIC-30 rock_smallA is an orange polyhedron with a green top, not visually confirmed as a natural pebble.
- PIC-03 and PIC-04 both point to apple.glb but have distinct proposed semantic IDs pending an alias/variety decision. `pot_large.glb` is only metadata; it was not rendered and is not inferred to match the small empty pot.
- Package-level CC0 evidence is not per-file/third-party/world-distribution approval. No asset was downloaded, extracted, imported, or modified for P10.

## Validation and limits

- `python3 Docs/Preparation/AssetAuthoring/validate_authoring.py` uses only Python's standard library to parse/validate both CSVs, confirm all 36 IDs match the original concept table, keep all 17 needs unauthorized, and resolve listed member paths against the frozen inventory.
- The script checks the explicitly used JSON Schema subset for coverage/needs/P11 examples and cross-checks preview source/derivative hashes; all JSON files parsed. It is not a full general-purpose JSON Schema engine.
- No Unity, Blender, SSH, MCP, gameplay, collision, scale, or rights approval was performed. Source units, axis/pivot, meters, pickup diameters, gain values, and collider dimensions remain open.
