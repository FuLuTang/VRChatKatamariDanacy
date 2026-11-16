# 吸收门按原作口径（a392e46）— Unity 验证 2026-11-16

Windows 工作副本 `8ba7c8e → a392e46`（`merge --ff-only`）。同步前 sha256（LF 归一化）逐路径比较：43 个与 origin 相同 → 显式路径 `git checkout --` 复原；17 个保留（8 个 `SerializedUdonPrograms`、`CpuSkirmish.unity`、`PhysicsTestCell.asset`/`PhysicsTestField.unity`/`PhysicsTestFieldDriver.asset`、`KatamariBallMotor.asset`、`BallGrowthDiagnostics.asset`/`BallView.asset`、`PickupFieldHost.asset`、`ProjectSettings.asset`）。**checkout 恢复 StoryMode.unity 又触发了 "modified externally" 模态框 → 选 Reload，已报告。**

## 1. 编译

`refresh_unity force` + `CompileAllCsPrograms(true,false)` → `[UdonSharp] Compile of 34 scripts finished in 00:03.680`，Console 计数 **0 error / 6 warning**（6 条全是 `MCP-FOR-UNITY [WebSocket] … close handshake` 噪声）。U# 侧对 `Mathf.Pow(…,1/3f)`、`out float`、`long` 都接受。

## 2. EditMode 全量（801 / 796 / 5 / 0，12.5 s）

派单预期"只有 GraphNodeTests 红"，实际 **5 红**。逐条原文：

```text
Tests.GraphNodeTests.CheckHelpURLsForSystemNodes
  URL does not resolve for Const_SystemBooleanArray: https://docs.microsoft.com/en-us/dotnet/api/system.array.?view=netframework-4.8
  Expected: True / But was: False

VRCKatamari.Tests.AbsorbSizeGateTests.TheBlockingPredicateAndTheGateAgreeOnEveryCase
  Expected: Absorb / But was: RejectBallAtGrowthCap

VRCKatamari.Tests.AbsorbSizeLimitTests.AFullyGrownBallCanTakeAOnePointTwoMetrePlank
  Expected: 2200.0d +/- 9.9999999747524271E-07d / But was: 2199.0d

VRCKatamari.Tests.AbsorbSizeLimitTests.TheStartBallIsAWholeMillimetreDiameter
  Expected: 0.014100000262260437d +/- 9.9999997473787516E-05d / But was: 0.0d

VRCKatamari.Tests.PickupFieldAuthoringRulesTests.DeclaredVolumeKMismatchesAreReportedById
  Expected: 2 / But was: 1
```

测试文件未改。总数 801 = O2 的 793 + 8 条新门金标。

**注意**：MCP 没有给出 stack trace，只有上面的 message；四条门失败里两条（`2200 → 2199`、`0.0141 → 0`）看起来像"手算/float32 模拟与 U# 实际 float 运算差一个单位/未初始化"，另外两条（`Absorb → RejectBallAtGrowthCap`、`2 → 1`）是逻辑分歧，不是精度。

## 3. 测试泄漏（9a32592 未覆盖这一条）

全量跑完后当前 StoryMode 场景：`dirty=False`，但仍有 **1 个 `test_prop` 根物体**（`children=0`）——来自 `RadialProxyLocalBoundsTests.CreateProp`；9a32592 的修复没管到它。已 `DestroyImmediate` 删除，场景仍未 dirty。

## 4. StoryMode 重导入

干净（未保存）打开 → 菜单 `VRC Katamari/Level/Import StoryMode Level 1 (story_01_bench_size)`：

```text
[level] +0 ~0 -0 props=130 other=0 errors=0  level=story_01_bench_size  scene=StoryMode
  pickup sync: ran, rows=130, rejected=0; udon copy: issued
  valueUnits: declared=1522, preserved=0, undeclared=0, read-back: verified
  note: pickup sync wrote 130 row(s) into 8 columns, including propVolumeK (per-item volume factor): 0 row(s) declared a k, the rest play at the original's 0.70 default
  note: level.ballDiameterStart=0.2997 m: set KatamariBallMotor.startDiameterWorldM to that value …
  the scene is left dirty on purpose: this tool never saves
```

第二次同样 `+0 ~0 -0 props=130 errors=0`。**没有任何 `absorbDiameterReq` 相关 mismatch 报告**。

保存后 `git diff --numstat` = **132 / 3**，全部可解释：

```text
-  maxItemVolumeWorldM3: 512          ← 被移除的旧字段，Unity 在保存时丢弃
-  absorbVolumeRatio: 10              ← 同上
+  propVolumeK:                       ← 新 float[]，130 行 `  - 0.7`，紧挨在 propLocalBounds 之后、valueUnits 之前
+  - 0.7   ×130
-/+ serializedPublicVariablesBytesString: …（1 换 1，类型/shape 变化必然重写）
```

`valueUnits` 未再变（Step B 已对齐）。**旧字段还会在下一个保存它的场景里消失**：`Labs/PhysicsTestField/PhysicsTestField.unity` 仍含 `maxItemVolumeWorldM3`（受保护，本轮未打开保存）；`CpuSkirmish.unity` 已不含。`_RefUnlicensed` 里的引用场景不入 git。

## 5. PTF 36×2

v1（`[ptf] field cells=9 runs=36 model=v1 verdict=UNEXPECTED unexpected=5 skipped=8 elapsed=230s`）：
`58/0/1、57/1/1、56/2/1、52/5/2` — **与 pass11 逐项相同**。

v2（`unexpected=31 skipped=8 elapsed=230s`）：**与派单预期（58/1/0、58/1/0、57/2/0、54/5/0）严重不符**：

| D | pass/unexpected/skipped | 派单预期 pass/unexpected/skipped |
|---|---|---|
| 0.300 | 49 / 10 / 0 | 58 / 1 / 0 |
| 0.600 | 51 / 7 / 1 | 58 / 1 / 0 |
| 1.100 | 50 / 7 / 2 | 57 / 2 / 0 |
| 2.200 | 47 / 7 / 5 | 54 / 5 / 0 |

31 条 ALERT 分型：`ramp_registered` 16、`no_climb` 4、`stopped_by_wall` 4、`lift_bounded` 4、`on_step` 2、`blocked_gap` 1。根因在场景/arena 侧，不在球：同一 run（`run=17 cell=C01`）

```text
v1: arena=planes=4/boxes=1/supports=3/ramps=1/obbs=2/rampCell=1  → check=ramp_registered value=+1.0000 PASS
v2: arena=planes=4/boxes=1/supports=3/ramps=0/obbs=2/rampCell=1  → check=ramp_registered value=+0.0000 UNEXPECTED_FAIL
```

即 v2 的球在 C01 没能把斜坡纳入接触集（ramps 计数 0 而不是 1），后面的 no_climb/stopped_by_wall 是它的连锁。**这是本轮唯一的行为回归发现**，需要 Lead 判断是 v2 接触模型本就不达预期，还是派单里的 v2 基线来自别的 PTF cell 版本（本轮 `[ptf] build=2026-11-15e cellRev=2026-11-15e`）。

## 6. StoryMode Play（38 s，owner 不在 Play）

```text
[PickupFieldHost] ready props=130 submittable=130 propRows=130 propObstacles=on proxyBounds=local activationR=1.6 releaseR=2.5 scanInterval=0.05
[arena] planes=4/4 boxes=4/4 ramps=2/2 supports=7 refused=0 kinds=11
[ballview] mesh=0.2847 d_motor=0.2997   [shell] core=0.1423 shell=0.1423
```

无 error。**没有 `ballMm/req mm` 日志行**：门只在有阻挡集或被拒绝/吸收时才打印，静止空跑两个条件都不满足；Play 中 `motor.GetSemanticDiameterWorldM()` 返回 0（round 未开始，`[StoryRoundController] idle: waiting for the local player to sit`），`GetAbsorbGateDiameterMm()` 也是 0。因此改用同一份运行期数据直接算门（读 `propLocalBounds`+`propVolumeK` 130 行调用 `PickupAbsorbMath.TryComputePickupReqDiameterMm`）：

```text
rows=130 kRows=130
first: bounds=(0.400, 0.280, 0.300) k=0.7 reqMm=765
ok=130 refused=0 minMm=28 maxMm=1944 meanMm=313.53
```

即 130 个物件全部能算出需求直径，范围 28–1944 mm，均值 313.5 mm；起点球 300 mm 下能吸的只有最小那批（req ≤ 300），与"原作数字"的直觉一致。无 absorb 发生（静止，且编辑器无法驱动吸收，此前已多次确认）。
