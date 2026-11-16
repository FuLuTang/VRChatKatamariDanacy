# Step B（`valueUnits` int[] → long[]）Unity 验证 — 2026-11-16

Windows 工作副本快进到 `8ba7c8e`（`merge --ff-only`，同步前用 sha256 逐路径比较，LF 归一化；25 个文件证明与 origin 相同后用显式路径 `git checkout --` 复原；其余保留）。

## 1. 编译

- `refresh_unity force` + `CompileAllCsPrograms(true,false)` → `[UdonSharp] Compile of 34 scripts finished in 00:03.957`，**0 error / 0 warning**。
- Udon 程序里确实带上了新类型：`PickupFieldHost.asset` 的 `serializedUdonProgramAsset` = `Assets/SerializedUdonPrograms/deccdf0eb9a059d4f8cb093682516e57.asset`，其解压程序体含 `valueUnits` 且 `SystemInt64` = **3**。
  全仓 67 份程序 `SystemInt64` 合计 **45**（`eb192fc9…`=40 是 `KatamariBallMotor`，Step A 的账本 long）。

## 2. EditMode 全量

`total 793 / passed 792 / failed 1 / skipped 0`（10.07 s）。唯一失败：

```text
Tests.GraphNodeTests.CheckHelpURLsForSystemNodes
URL does not resolve for Const_SystemBooleanArray: https://docs.microsoft.com/en-us/dotnet/api/system.array.?view=netframework-4.8
  Expected: True
  But was:  False
```

（已知红，pass11 同款。总数 793 比派单预期的 792 多 1：`PickupFieldValueUnitsTests` + `PickupFieldAuthoringRulesTests` 一共加了 3 条，不是 2 条。）

## 3. StoryMode 重导入

顺序：打开 `StoryMode.unity`（非 Play）→ 菜单 `VRC Katamari/Level/Import StoryMode Level 1 (story_01_bench_size)`（不是 `Sync Prop Arrays From Children`）→ 再跑一次 → 保存。

第一次（关键行原文）：

```text
Storage for variable 'valueUnits' of type 'System.Int64[]' did not match, updated storage type
[level] +0 ~0 -0 props=130 other=0 errors=0  level=story_01_bench_size  scene=StoryMode
  pickup sync: ran, rows=130, rejected=0; udon copy: issued
  valueUnits: declared=1522, preserved=0, undeclared=0, read-back: verified
  arena: wired (boxes
```

第二次（幂等）：`[level] +0 ~0 -0 props=130 other=0 errors=0` + `valueUnits: declared=1522, preserved=0, undeclared=0, read-back: verified`。

保存后 `git diff --numstat` = **2 / 2**，两行都是同一个 `PickupFieldHost` 块：

```text
-  valueUnits: 2400000024000000400000001000000010000000...
+  valueUnits: 240000000000000024000000000000004000000000...
-  serializedPublicVariablesBytesString: Ai8AAAAAATIAAABWAFIAQwAuAFUAZABvAG4ALgBDAG8AbQBtAG4...
+  serializedPublicVariablesBytesString: Ai8AAAAAATIAAABWAFIAQwAuAFUAZABvAG4ALgBDAG8AbQBtAG4...
```

- `valueUnits` 新串：**2,080** 个十六进制字符 = 130 × 16（int64），sha256（十六进制文本按 ASCII）**`ed41c3a42c11b9a259f6ba41b78cdbf4bad57cb4307d42675245d0f7ec8ac853`**，与派单给的期望值一致。
- **额外一行**：`serializedPublicVariablesBytesString`（Udon 公共变量 blob）也必须变——同一个 `valueUnits` 在组件字段之外还存了一份到 Udon 公共变量，类型从 `System.Int32[]` 变 `System.Int64[]` 时这份 blob 必然重写。这不是意外改动，但它确实不是"只有 valueUnits 一行"。
- 另有一次**必须报告的意外**：第一次保存后 diff 是 34/2，多出一个挂在 `SceneRoots` 的根物体 `test_prop`（`m_Name: test_prop`，Y 轴 90°）。它是 EditMode 测试 `RadialProxyLocalBoundsTests.CreateProp` 里 `new GameObject("test_prop")` 的残留——测试在编辑器当前打开的 StoryMode 场景里建物体，我的保存把它写进了场景。已删除该物体并重新保存，上述 2/2 是删除后的结果。

## 4. Play 验证（20 s，owner 不在 Play）

```text
[PickupFieldHost] ready props=130 submittable=130 propRows=130 propObstacles=on proxyBounds=local activationR=1.6 releaseR=2.5 scanInterval=0.05
[BallMotor] Start ready diameter=0.2997369 collisionRadius=0.1498684 baselineUnits=3.439024 startSize=calibration hasLocalRow=True terrainSupport=True
[ballview] mesh=0.2847 d_motor=0.2997  [shell] core=0.1423 shell=0.1423
[story-lab] ... absorbed=0 rejects=0
```

Play 中读 `PickupFieldHost` 的 `valueUnits` 序列化数组：`arraySize = 130`（不是 65 行垃圾，说明 blob 形状与字段一致）。无 error。**没有发生 absorb**：本次仍是静止空跑，且此前已确认无法从编辑器驱动吸收（motor 每步重写世界位置、`PickupFieldHost` 在 Start 缓存 `propRows=130`、Udon 输入事件不可伪造），要吸收必须由 owner 亲自操作。

## 5. PTF v1 36 runs

`[ptf] field cells=9 runs=36 model=v1 verdict=UNEXPECTED unexpected=5 skipped=8 elapsed=230s`；
四个直径总计与 pass11 逐项相同：**58/0/1、57/1/1、56/2/1、52/5/2**。明细 `ptf_v1_matrix.tsv`。

## 6. 观察项（未改）

`Labs/CpuSkirmish/CpuSkirmish.unity`、`Labs/PhysicsTestField/PhysicsTestField.unity` 未打开、未保存、未改动（工作树里它们的既有 diff 是本轮之前就有的，本次没有任何操作触及）。
