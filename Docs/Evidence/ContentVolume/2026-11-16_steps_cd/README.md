# 定点体积步骤 C+D 验收（O7）— 2026-11-16

基线 `7fa1e37`（Linux origin/main），Windows `2331457 → 7fa1e37` ff-only。

## 编译（两处机械错误，已修）
1. `PickupFieldHost.cs(1063,17): error CS0103: The name 'badValues' does not exist in the current context` → `badValues` 改 `badGrowth`（值列改名后的漏改）。
2. `AbsorbSizeLimitTests.cs(117,34): error CS0030: Cannot convert type 'System.Reflection.FieldInfo' to 'float'` → `(float)FieldOrNull("maxBallDiameterM")` 改 `(float)GetField("maxBallDiameterM")`（FieldOrNull 返回 FieldInfo）。
之后 `[UdonSharp] Compile of 39 scripts finished in 00:11.925` / `00:03.413`，0 error。

## EditMode：total 820 / passed 806 / failed 14 / skipped 0 / 9.74 s
- **float32 黄金值（8 条，代码在文档预算内，测试用精确等值）**：`SizeCalibrationTests` ×4（523598776↔523598784、14137167↔14137169、14137166941154↔14137166921728、14094798↔14094797）、`SizeModelTests` ×2（…846356↔…826930、…598776↔…598784）、`StartSizeTests`（87113746↔87113752）、`BankShrinkMathTests`（97594703↔97594714）。相对误差 1e-9…1.5e-8，正是 §4 写的 float 预算。
- **陈旧黄金值（1 条）**：`AbsorbSizeGateTests.TheSanityCeilingIsNotAGameplayGate` 期望 1000.0，实得 14137.0（§5 新护栏）。
- **真代码问题（4 条）**：`SizeModelTests.RatiosScaleTheCollisionRadiusAndTheSemanticDiameter` 期望 0.14985 实得 0.074925（= 一半 → 碰撞半径按 0.25·D 算）；`AbsorbSizeLimitTests.TheStartBallIsAWholeMillimetreDiameter` 的 `GetConfirmedVolumeMm3` 返回 0 而非 startVolumeMm3；`AbsorbSizeLimitTests.AFullyGrownBallCanTake…` 5 m 立方判定翻转；`PlacementImporterColliderTests.AnAbsentBlockMaterialisesButIsNotDeclared` 缺席 collider 的 type 返回 null 而非 `""`。
- 已知 `GraphNodeTests.CheckHelpURLsForSystemNodes` 照旧。`test_prop`=0（场景根 2），sweep 警告 1 次。

## 重导入 Level 1
`[level] +0 ~0 -0 props=130 other=0 errors=0`；`propColliders: created=0`；`growthMm3: declared=1417085127, preservedRows=0, unpricedRows=0, read-back: verified`；`pickup sync: ran, rows=130, rejected=0`。
宿主：`propGrowthMm3` 130 行 / 2080 hex、`propVolumeK` 130×0.7、`propColliders` 130（首行 0）、12 个 proxy 数组各 130 行（hullPoints/hullFaces=0）、`valueUnits` 已消失。

**对不上账**：宿主 130 个 long 求和 **1,417,080,788**，`placement.json`/设计 **1,417,085,102**（差 4,314 mm³），导入报告自己又写 `declared=1,417,085,127`（比 json 多 25）。逐件抽样：`acorn_01` 9203(json 9193)、`apple_01` 275668(275708)、`banana_01` 218779(218696)、`watermelon_01` 8870394(8870400)。原因应是导入按同步写进 `propBounds` 的实测 bounds 定价，而 `canonical_doc` 按 json `aabb` 定价——两个来源永不可能逐 mm³ 相等。

## 场景 diff（578 增 / 10 删）
电机 −`baselineVolumeUnits`/`cubicMetersPerVolumeUnit`/`maxCoreRadiusWorldM`，+`startVolumeMm3: 14094798`、+`maxBallDiameterM: 30`、+`propProxyContactEnabled: 0`、+4 个调试驾驶字段 0；宿主 `valueUnits`→`propGrowthMm3`。
**额外一处（已按设计改）**：`eyeUpMaxMeters`/`eyeMaxBackM` 在旧实例里是 **8**（不是文档的 64），已用 SerializedObject + `CopyProxyToUdon` 置 64 并保存。

## hub
`[level] cell=hub +0 ~0 -0 props=5 errors=0`、`growthMm3: declared=186113352`；`[hub-check] done: 0 problem(s), 1 circle(s), 1 level entry, zone radius 1.2, room radius 12`。

## PTF（判据只在内存，未保存）
`[ptf] criteria: cells=9 checks=59 v2Rows=11 missingCells=0 missingArenas=0 driverFields=6 errors=0`，`SizeConsole.sizeModelStartVolumeMm3=14094798`。
- v1：`[ptf] field cells=9 runs=36 model=v1 verdict=UNEXPECTED unexpected=5 skipped=44 elapsed=230s`
- v2：`[ptf] field cells=9 runs=36 model=v2 verdict=EXPECTED unexpected=0 skipped=45 elapsed=230s`

unexpected 与 pass11 的 5/0 完全一致（C05 climbed ×4 + C13 seam）；**skipped 从 8 涨到 44/45**：每个 cell 的 `size_accuracy` 都是 `reason=quantumUnavailable` 跳过（9 cell × 4 直径 = 36 条），判据还在问「整 mm 量子」，而成长已换 mm³。

## 第一个完整回合（调试驾驶，新计价）
坐下仍靠 `SendCustomEvent("TrySeatLocalPlayer")`（`seated=True`，两个门 True）。调试变量在 **DesktopMoveInput** 上（不是电机）：`debugDriveEnabled/debugAxisX/debugAxisZ/debugDriveSeconds`。
50 个自动驾驶周期、约 50 s 有效驾驶：
```text
[grow] t=27.8 absorbed=1 units=289345 D=0.2997 -> 0.3017 (+0.6%)
[grow] t=27.9 absorbed=2 units=52862 D=0.3017 -> 0.3021 (+0.1%)
[grow] t=29.8 absorbed=3 units=283500 D=0.3021 -> 0.304 (+0.6%)
[grow] t=33.9 absorbed=4 units=277463 D=0.304 -> 0.3059 (+0.6%)
[grow] t=46.6 absorbed=5 units=28997 D=0.3059 -> 0.3061 (+0%)
[grow] t=148.9 absorbed=0 units=-4058769 D=0.326 -> 0.2997 (-8.1%)   ← retry 复位
```
回合结束：`剩余 0 s`、`当前 0.32 m`、`目标 1.25 m`；结果板 **`呈给国王 / ☆☆☆  直径 0.32 m / ……就这样？朕的野餐会还没开始呢。再来一次。`**（0 星；O6 旧计价是 1.31 m / ★☆☆）。`[eye] t=102.1 absorbed=18`，`d_motor=0.3261`、`d_vis=0.2908`。
重试：`field reset restored=19 props=130`、`retry started round=story_01_bench_size:1 attempt=1`。
**陷阱**：c41–c50 连续 10 个周期球冻在 `(-8.27, 1.10, -17.79)`，贴着 `apple_01`（d=0.59）——球 0.326 m 吃不动苹果，又推不过去，占掉 20% 的驾驶周期。
日志格式：`[grow] t= absorbed= units= D=` ✓（units 已是 mm³）、`[ballview] absorbed/mesh/d_vis/d_motor`、`[shell] r_core/r_shell/swallowed/attached` ✓。
