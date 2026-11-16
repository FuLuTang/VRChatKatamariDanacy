# Collider proxy runtime handoff

> Status: `PROVISIONAL_NOT_TESTED`, 2026-11-16. Runtime implementation of
> [COLLIDER_PROXY.md](COLLIDER_PROXY.md) §6, behind `propProxyContactEnabled` (default
> **false**). No Unity/Udon compile, no EditMode run, no Play Mode/VR in this checkout;
> every claim about compilation/behaviour is unverified here.

## 1. File / symbol map

| File | New symbols |
|---|---|
| `Runtime/BallContact/ProxyContactMath.cs` (new) | `KindBox/Sphere/Capsule/Convex`, `IsValidProxy`, `BoundingRadius`, `TryBakeProxy`, `TryClosestPointOnProxy`, `TrySphereProxyContact`; box branches delegate to `ObbContactMath` |
| `Runtime/BallContact/ContactWindowMath.cs` | `TrySelectProxyWindow` (same scan/hysteresis/overflow as `TrySelectWindow`; exact distance per kind) |
| `Runtime/BallContact/ContactSolveMath.cs` | optional `obbKinds/obbRadii/obbHalfHeights/obbAxes/hullStarts/hullCounts/faceStarts/faceCounts/hullPoints/faceNormals/faceOffsets/faceA/B/C` parameters on `TryCollectContacts`, `TryPushOut`, `TryStep`; null keeps the old box path |
| `Runtime/BallMotor/KatamariBallMotor.cs` | `propProxyContactEnabled` (serialized bool, default false); proxy shape arrays for the 12-slot prop block; `SetPropProxies` / `ClearPropProxies` / `IsPropProxyContactEnabled` / `SetPropProxyContactEnabled`; proxy branch in `FillNearWindow`; proxy args in `TryStep` and the growth push; `SetPropObbs` resets the proxy slots |
| `Runtime/Pickup/PickupFieldHost.cs` | serialized `propProxyKinds/Centers/Sizes/Radii/HalfHeights/Axes/HullStarts/HullCounts/HullPoints/FaceStarts/FaceCounts/HullFaces`; runtime 12-slot proxy buffers; `BakeProxyRows`, `ProxyKindAt`, `ApplyPropProxySet`, `ClearPropProxies`; `RefreshPropObbs` chooses the proxy block when the motor flag is on |
| `Editor/Pickup/PickupFieldAuthoringRules.cs` | `PickupProxyDefinition`, `ProxyKind*`, `MaxProxyHullPoints/Faces`, `ProxyError`, `TryBuildHullFaces` (editor-time triangulation of a ≤24-point hull) |
| `Editor/LevelBuild/PlacementImporter.cs` | `ColliderEntry` (flat `float[] hull`), `DefEntry.collider`, `TryBuildProxy`, `DeclaredProxy`, `BuildDeclaredProxies`; def validation and the sync declaration map |
| `Editor/Pickup/PickupFieldSync.cs` | 6-argument `SyncFromChildren` with `declaredProxies`; `PropRow.Proxy`; preservation by item id; 12 new columns in `WriteRows`; `ReadExistingProxies`; read-back verification |
| `Editor/Pickup/PickupFieldSyncReport.cs` / `Editor/LevelBuild/PlacementImportReport.cs` | `ProxyVerified` |
| `Tests/EditMode/BallContact/ProxyContactTests.cs` (new) | sphere/capsule/box/convex closest point, contact normal/depth, watermelon capsule floor clearance, box window/collect equivalence, bake equivalence |

## 2. Serialization / scene diff

The host's new `[SerializeField]` columns are index-aligned with `propTransforms`
(StoryMode's whole-level host currently has **130** rows; the 5-row host in the same scene
is separate). A first sync/import after this change is expected to add, per host:

| Field | Element type | Size |
|---|---|---:|
| `propProxyKinds` | `int[]` | 130 |
| `propProxyCenters` | `Vector3[]` | 130 |
| `propProxySizes` | `Vector3[]` | 130 |
| `propProxyRadii` | `float[]` | 130 |
| `propProxyHalfHeights` | `float[]` | 130 |
| `propProxyAxes` | `int[]` | 130 |
| `propProxyHullStarts` | `int[]` | 130 |
| `propProxyHullCounts` | `int[]` | 130 |
| `propProxyHullPoints` | `Vector3[]` | sum of convex hull points (0 with no declared collider) |
| `propProxyFaceStarts` | `int[]` | 130 |
| `propProxyFaceCounts` | `int[]` | 130 |
| `propProxyHullFaces` | `int[]` | 3 × sum of convex faces (0 with no declared collider); `propProxyFaceStarts` is an int offset into this array, while the host's runtime face arrays are per-face |

The motor gains `propProxyContactEnabled: 0` (false). No scene was edited here; the diff
appears only when the Unity operator compiles the scripts and runs the importer/sync.

## 3. Data decision (why no collider blocks were written)

`HOW_TO_ADD_A_PROP.md` defines `exports/*.manifest.json` as the **per-model** manifest
(dimensions, materials, topology), not the level's `defs[]`. The collider block lives in
`Docs/Design/Levels/<level>/placement.json` `defs[]`, so writing it into the model
manifests would put it where the importer never reads it. This round therefore **left all
level and model data untouched**; the runtime/import path is ready for a level author to
add `defs[].collider` blocks. No `StoryMode.unity` / `placement.json` / manifest changed.

## 4. Flag and fallback contract

- Flag **false**: `SetPropObbs` is the only prop registration, `TrySelectWindow` and the
  old `TryCollectContacts` signature (null proxy arrays) are used. The new serialized
  columns are inert; a row with no proxy (`kind = -1`) keeps the old local-OBB shape.
- Flag **true**: the host bakes each selected prop into the 12-slot proxy block. A prop with
  no collider block is baked as a box with the same `PropObbBakeMath` path, so enabling the
  flag without data is the old box world. `SetPropProxies` clears `propObbCount`; `SetPropObbs`
  clears the proxy kind columns, so the two blocks never double-register a prop.
- A declared block that fails validation is an import/sync error; at runtime a failed bake
  falls back to the OBB and is counted in `GetPropProxyFallbackRows()`.

## 5. Not verified here

- Unity 2022.3 / UdonSharp compilation of the new signatures (especially the many optional
  array parameters on `TryStep`/`TryCollectContacts` and the 268-slot array allocations).
- EditMode `ProxyContactTests` execution and `run_tests`.
- The `JsonUtility` read of a real `defs[].collider` block (the DTO is flat by design).
- UdonSharp `CopyProxyToUdon` with the 12 new serialized columns; scene YAML sizes above
  are read from the current `StoryMode.unity`, not from a post-sync run.
- Any Play Mode/VR feel change.
