# EditMode 全量（第 L 轮，commit b672f3b）

| 指标 | 值 |
|---|---|
| total | **717** |
| passed | **716** |
| failed | **1** |
| skipped | 0 |
| duration | 14.69 s |

## 唯一失败（预期内）

```
Tests.GraphNodeTests.CheckHelpURLsForSystemNodes
  URL does not resolve for Const_SystemBooleanArray:
  https://docs.microsoft.com/en-us/dotnet/api/system.array.?view=netframework-4.8
  Expected: True
  But was:  False
```

## 上轮 6 条红，全部修好

| 测试 | 上轮结果 | 本轮 |
|---|---|---|
| `LevelArenaHostObbTests.FullTableRefusesEachSolidWithAnOverflowCode` | 红（double bake mismatch 未处理日志） | ✅ |
| `LevelArenaHostObbTests.ReapplyReregistersEachSolidExactlyOnce` | 红（同上） | ✅ |
| `LevelArenaHostObbTests.RotatedBoxKeepsItsOwnAxesAndItsAuthoredExtents` | 红（AABB 未膨胀） | ✅ |
| `LevelArenaHostObbTests.ShearedParentIsRefusedWithAReasonCodeAndTheBoundsBoxIsUsed` | 红（剪切未拒） | ✅ |
| `LevelArenaHostObbTests.V2BakesOneTrueObbPerBoxAndPerSlab` | 红（烘焙表长度 3 vs 268） | ✅ |
| `PlacementImporterCurveRampTests.ImpossibleArcsAreRefusedInsteadOfBuilt` | 红（361° 弧仍建链） | ✅ |

## 重点类

| 类 | total | passed | failed |
|---|---|---|---|
| `ContactModelV2Tests` | **20** | 20 | 0 |
| `LevelArenaHostObbTests` | 6 | 6 | 0 |
| `PlacementImporterCurveRampTests` | 11 | 11 | 0 |
| `PlacementImporterModelPathTests` | **3** | 3 | 0 |
| `PropObbBakeTests` | 5 | 5 | 0 |
| `PickupFieldPropObbTests` | 2 | 2 | 0 |

## 编译

- C#：0 错误（refresh 后立即为 `isCompiling=False / compileFailed=False`，控制台 0 条 error）。
- U#：`Compile of 33 scripts finished in 00:03.148`，0 错误，无拒绝。
- **没有做任何机械修复**，源码零改动（`git diff` 中转发的 .cs 数量为 0）。
