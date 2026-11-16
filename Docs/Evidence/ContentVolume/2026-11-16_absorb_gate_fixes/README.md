# 吸收门修复验证（a15f2fa）— Unity 2026-11-16

Windows 工作副本 `a392e46 → a15f2fa`（`merge --ff-only`）。同步前用 **空场景法** 消除模态框：先用 MCP `NewScene(EmptyScene, Single)` 丢掉 StoryMode（`wasDirty=False`），再 sha256（LF 归一化）逐路径比较 → 42 个与 origin 相同、显式 checkout 复原；18 个保留（9 个 `SerializedUdonPrograms`、`CpuSkirmish.unity`、`PhysicsTestCell.asset`/`PhysicsTestField.unity`/`PhysicsTestFieldDriver.asset`、`KatamariBallMotor.asset`、`BallGrowthDiagnostics.asset`/`BallView.asset`、`PickupFieldHost.asset`、`ProjectSettings.asset`）。**这次没有弹 "modified externally"，无需 Reload。**

## 1. 编译

`refresh_unity force` + `CompileAllCsPrograms(true,false)` → `[UdonSharp] Compile of 34 scripts finished in 00:04.060`，**0 error**。

## 2. EditMode 全量：801 / 800 / 1 / 0（9.68 s）

四条上轮红全部转绿；只剩既有环境红：

```text
Tests.GraphNodeTests.CheckHelpURLsForSystemNodes
  URL does not resolve for Const_SystemBooleanArray: …/system.array.?view=netframework-4.8
  Expected: True / But was: False
```

## 3. StoryMode 的 test_prop 泄漏：仍会出现，但有 DontSave 保护

`testRootsBefore=0`（开跑前）→ 全量跑完后 `testRootsAfter=1`（`test_prop`，`children=0`，`dirty=False`），
并且**该根物体的 `hideFlags=DontSave`**。设置 `HideFlags.DontSave` 的只有
`Tests/EditMode/BallLayers/RadialProxyLocalBoundsTests.CreateProp`（`RadialProxyLocalBoundsTests.cs:261/267`），
全仓仅此一处 `new GameObject("test_prop")`。结论：

- 9a32592 的修复**有效的那一半**：残留物无法被序列化，所以 O3a 里"保存把 test_prop 写进场景"的事故不会再发生（本轮 `dirty=False` 且未保存）；
- **没修的那一半**：该类的 `TearDown` 没有把它清掉（或某个测试在 `created` 之外留下一个），层级里会看到它。定位到类，具体方法未逐个二分（MCP `run_tests` 不支持 `filter`，单类运行需 6 次全量代价）。

已手动 `DestroyImmediate` 清掉。

## 4. PTF：判据重导入 + 36×2 同会话（不存盘）

菜单路径是 `VRC Katamari/Level/Import Physics Test Field Criteria`（**不是**派单写的 `VRC Katamari/Physics Test Field/Import Criteria`，那个返回 false）。

```text
[ptf] criteria: cells=9 checks=59 v2Rows=11 missingCells=0 missingArenas=0 driverFields=6 errors=0
```

场景保持 dirty、**未保存**（`PhysicsTestField.unity` 受保护）。随后同会话跑 v1、再开 v2 跑第二遍（`contactModelV2Enabled` 只改内存）。

首行（两遍都）`[ptf] build=2026-11-15e cells=9 presets=4 model=v1|v2 cellRev=2026-11-15e v2Rows=11` — **不再是 missing**，全场无 `abort reason=staleCriteriaV2`。

| | D=0.300 | 0.600 | 1.100 | 2.200 | field 行 |
|---|---|---|---|---|---|
| v1 pass/预期红/跳过 | 58/1/0 | 57/1/1 | 56/2/1 | 52/5/2 | `unexpected=5 skipped=8` ✅ 同 pass11 |
| v2 pass/预期红/跳过 | 58/0/1 | 58/0/1 | 57/2/0 | 54/5/0 | `unexpected=0 skipped=9 verdict=EXPECTED` ✅ 同派单 |

派单点名的三行（v2）逐字：

```text
[ptf] run=17 cell=C01 D=0.300 model=v2 … arena=planes=4/boxes=1/supports=3/ramps=0/obbs=2/rampCell=1
[ptf] check=ramp_registered metric=static_obb_count value=+2.0000 op=>=1.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=lift_bounded metric=lift_over_nominal_in_deck_m value=+0.0000 op=<=0.0100 expect=pass raw=PASS verdict=EXPECTED
```

v2 全场 `ALERT` 数 = **0**（O3a 是 31）。ANALYSIS.md §1.1 的归因得到确认：O3a 的 31 条是过期烘焙，不是 v2 物理回归。

跑完把 PTF 从磁盘重新打开丢弃脏数据：`PhysicsTestField` 场景 `dirty=False`；工作树里它仍是同步前就有的 `M`（`53/11`，与本轮无关，本轮没有保存它）。

## 5. 场景/脚本状态

StoryMode：本轮**没有重导入、没有保存**（吸收门未改，且 ANALYSIS §5 明确不需要），因此工作树里 StoryMode 的 diff 仍是 O3a 的 `132/3`。`CpuSkirmish.unity` 依旧观察项，未打开未保存。
