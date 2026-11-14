# P11 asset authoring handoff

## Deliverables

- [`asset_authoring.schema.json`](asset_authoring.schema.json): host-only schema for semantic definitions, placement instances, composition alternatives, and lifecycle cases.
- [`examples.json`](examples.json): Apple preview example with source/derivative hashes, filename-only cup/plate candidates, a side-table placeholder, separate cup/plate instances attached to a support parent, and explicit support-removal/drop/respawn/restock cases.
- [`README.md`](README.md): stable-ID, entity-granularity, render/collider/support proxy, and scale-boundary guidance.

## Specification decisions captured as proposals

- Proposed semantic `definitionId` is independent of file path; `spawnId` identifies one map placement; `spawnGeneration` separates respawn generations. These are host draft labels, not runtime API freeze.
- Render meshes, gameplay collider proxies, and support/extreme-shape proxies have different fields and are never inferred from each other. Material slots and mesh counts do not imply collectible count.
- Cup+saucer may become one approved definition/instance or separate cup/plate definitions and instances. A table can be a fixed support, a collectible, or a landmark. Children need explicit policy if a support parent is removed; chairs are only a textual rule example and are not claimed in the 36-source coverage.
- Units, axes, pivots, normalized display scale, gameplay meters, pickup diameter, collision radius, gain volume, and zone/tier restock budget remain OPEN/NULL/NOT_SET.

## Validation and limits

- `python3 Docs/Preparation/AssetAuthoring/validate_authoring.py` passes using only Python's standard library. It checks the JSON Schema subset used here, required fields/types/enums/patterns, frozen source paths and Apple selection hashes, stable ID uniqueness, support-parent references/acyclicity, and open/unset gameplay measurements. It is intentionally not described as a general JSON Schema implementation.
- Host data examples are not generated Unity assets, are not read by Udon, and have no approved values or serialized runtime guarantees.
- No Unity, Blender, SSH, MCP, download, import, gameplay, or final license-review operation occurred.
