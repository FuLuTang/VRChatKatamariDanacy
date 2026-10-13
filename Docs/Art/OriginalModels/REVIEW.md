# Lead review — original basic models (first batch)

Date: 2026-10-14. Reviewer: Lead (visual check of the three-view previews, manifests and SHA256SUMS; no Unity import was run).

| Model | Verdict | Notes |
|---|---|---|
| `ball_core` | ACCEPT as placeholder | 320 tris, ~1 m diameter, pivot at centre. |
| `carrier_platform` | ACCEPT as placeholder | Reads clearly as a seat platform; anchors `SeatAnchor` / `FollowRigAnchor` are separate empties. Height 0.26 m (platform + seat marker), slightly above the 0.2 m asked for. |
| `exit_button` | ACCEPT as placeholder | Cylinder base with a dome button; unambiguous shape, no text. |
| `ground_cloth_tile` | ACCEPT as placeholder | Two-colour cloth with a ramped hem, no steps. |
| `item_apple`, `item_cup`, `item_plate`, `item_sugar_cube` | ACCEPT as placeholders | Real-world sizes only; world scale remains OPEN. |
| `beetle_simple` | **REWORK** | Previews show a flattened ellipsoid plus a small sphere; the legs are thin slabs hidden under the body, so it does not read as a beetle. Optional item; do not use until redone. |

Scope reminders: not final art, not Unity-import validated, no collider/gameplay meaning, axes/units/pivots provisional until T06. Mesh self-checks (non-manifold / zero-area faces) come from the generating script, not from an independent QA pass.
