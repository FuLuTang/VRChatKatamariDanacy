# EditMode 全量（第 K 轮，commit 18f9f3e）

| 指标 | 值 |
|---|---|
| total | **710**（681 + 新增 29） |
| passed | **703** |
| failed | **7** |
| skipped | 0 |
| duration | 14.42 s |

## 7 条失败

### 1. SDK 链接检查（预期内）
`Tests.GraphNodeTests.CheckHelpURLsForSystemNodes` — URL does not resolve for Const_SystemBooleanArray，Expected True / But was False。

### 2–6. `LevelArenaHostObbTests`（5/6 失败）

```
LevelArenaHostObbTests.FullTableRefusesEachSolidWithAnOverflowCode
  Unhandled log message: '[Error] [arena] double bake mismatch on 'deck': the eight-corner OBB
  and the bounds AABB disagree; registering the bounds AABB (design §7.4)'

LevelArenaHostObbTests.ReapplyReregistersEachSolidExactlyOnce
  同上一条 Unhandled log message

LevelArenaHostObbTests.RotatedBoxKeepsItsOwnAxesAndItsAuthoredExtents
  the bounds AABB is the inflated shape
  Expected: greater than 3.9000001f / But was: 1.0f

LevelArenaHostObbTests.ShearedParentIsRefusedWithAReasonCodeAndTheBoundsBoxIsUsed
  the AABB is wider than the authored box (the corners are not a box)
  Expected: greater than 0.600000024f / But was: 0.5f

LevelArenaHostObbTests.V2BakesOneTrueObbPerBoxAndPerSlab
  ramp slabs keep 10+i and boxes keep 1+i
  Expected is <System.Int32[3]>, actual is <System.Int32[268]>
  Values differ at index [3] / Extra: < 0, 0, 0... >
```

### 7. `PlacementImporterCurveRampTests.ImpossibleArcsAreRefusedInsteadOfBuilt`

```
radius=4 arc=361 bend=up must not produce a chain
Expected: False / But was: True
```

## 重点类逐类

| 类 | total | passed | failed |
|---|---|---|---|
| `ContactModelV2Tests` | **16** | 16 | 0 |
| `LevelArenaHostObbTests` | 6 | 1 | **5** |
| `PropObbBakeTests` | 5 | 5 | 0 |
| `PickupFieldPropObbTests` | 2 | 2 | 0 |
| `PlacementImporterCurveRampTests` | 11 | 10 | **1** |
| `NeedleSegmentTests` | 12 | 12 | 0 |

## 编译

- C#：0 错误。
- U#：首次 refresh 时自动编译报了 15 条 `CS0103: The name 'PropObbBakeMath' does not exist in the current context`（`PickupFieldHost.cs` 的 707/708/709/710/711/1705/1745/1747/1748 行），发生在 `PropObbBakeMath.cs` 还没进程序集的时候；程序集重建后再执行 `CompileAllCsPrograms(true,false)` → `Compile of 33 scripts finished`，**0 错误**。
- **没有任何机械修复**，源码一行未改。
