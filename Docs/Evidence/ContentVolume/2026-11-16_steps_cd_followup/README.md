# 步骤 C+D 跟进验收（O8）— 2026-11-16

基线 `dd0711d`（Linux origin/main）；Windows `7fa1e37 → dd0711d` ff-only。

## 同步 / 编译
sha256 逐路径：66 个与 origin 一致已复原，6 个保留（PTF 三件 + `CpuSkirmish.unity` + `ProjectSettings.asset` + O7 我改的 `AbsorbSizeLimitTests.cs`）。合并被两处挡：我 O7 的测试补丁（origin 已正确修复）→ `git checkout -- <该路径>` 放弃；`PlacementImporterColliderTests.cs.meta` 未跟踪但 `git hash-object` 与 `origin/main:` 相同 → 删除后 ff-only 成功。

**编译错误 1 处（机械，已修）**：
`Assets\VRC_Katamari\Editor\Pickup\PickupFieldSync.cs(974,33): error CS0165: Use of unassigned local variable 'declaredGrowth'`
→ `long declaredGrowth;` 改 `long declaredGrowth = 0L;`（`declaredGrowthMm3 == null` 时短路，`out` 从未赋值；只在 `hasDeclaredGrowth` 分支里读，语义不变）。之后无编译错误，EditMode 正常执行。

## EditMode：total 820 / passed 818 / failed 2 / skipped 0 / 15.12 s
- 已知 `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`。
- **`VRCKatamari.Tests.StartSizeTests.TheStartVolumeIsRecomputedOnEveryStartNotAccumulated`**：`Expected: 14137167 / But was: 14137169`（`pi/6 * 0.30^3 * 1e9`）。**测试遗留**：同文件第 38 行已有 `AssertVolume`（`FloatVolumeBudget = 2e-6`），第 80/116 行都用了它，只有第 123 行仍是精确 `Assert.AreEqual(14137167L, second, …)`；14137169 与黄金值差 1.4e-7，正好在预算内。按规则未改测试。`test_prop`=0（场景根 2），sweep 警告 1 次。

## 重导入 Level 1（新权威：文件值）
`growthMm3: declared=1417085102, fromFile=130, fromBounds=0, read-back: verified`、`pickup sync: ran, rows=130, rejected=0`、`propColliders: created=0`。
宿主回读：`propGrowthMm3` 130 行、**sum = 1,417,085,102**（与 placement.json 完全相等），`acorn_01=9193`、`apple_01=275708`、`banana_01=218696`、`watermelon_01=8870400`——与期望逐字相同。**没有** bounds-vs-file 警告（fromBounds=0，130 行全部来自文件）。
缺口：同步端没有打印 `growthMm3: written=…` 行（只出现在导入报告里）；同步自己的汇报只有「wrote 130 row(s) into 8 columns」。

## 场景 diff（保存后）
只有 2 行：`propGrowthMm3`（2,080 hex，解码 130 个 int64，sum 1,417,085,102）与 `serializedPublicVariablesBytesString`。与期望一致。
hub：`cell=hub +0 ~0 -0 props=5`、`growthMm3: declared=186113343, fromFile=5, fromBounds=0, read-back: verified`；diff 追加 hub 的 80 hex `propGrowthMm3` + 其 public variables 串。`[hub-check] done: 0 problem(s), 1 circle(s), 1 level entry, zone radius 1.2, room radius 12`。

## PTF（判据只在内存，`PhysicsTestField.unity` 未保存）
`[ptf] criteria: cells=9 checks=59 v2Rows=11 missingCells=0 missingArenas=0 driverFields=6 errors=0`
- v1：`[ptf] field cells=9 runs=36 model=v1 verdict=UNEXPECTED unexpected=5 skipped=8 elapsed=230s`
- v2：`[ptf] field cells=9 runs=36 model=v2 verdict=EXPECTED unexpected=0 skipped=9 elapsed=230s`

skipped 回到 8/9（O7 是 44/45）。`size_accuracy` 现在全部实测通过，样例：
`check=size_accuracy metric=diameter_error_m value=-0.0003 op=abs<=0.0010 expect=pass raw=PASS verdict=EXPECTED`、
`… value=+0.0000 op=abs<=0.0010 …`、（多格同形）。

## 完整回合（调试驾驶，22 次吸收）
过程同 O7：`SendCustomEvent("TrySeatLocalPlayer")` → 两个门 True → 在 `DesktopMoveInput` 写 `debugDriveEnabled/AxisZ`。74 个自动驾驶周期，约 74 s 有效驾驶，吸收 22 件。

| t (s) | absorbed | D (m) | d_vis (m) |
| --- | --- | --- | --- |
| 10 | 0 | 0.2997 | 0.2847 |
| 20 | 2 | 0.3035 | 0.2883 |
| 30 | 10 | 0.3173 | 0.3014 |
| 40 | 13 | 0.3242 | 0.308 |
| 60 | 18 | 0.329 | 0.3125 |
| 70 | 20 | 0.3305 | 0.314 |
| 回合末 | 22 | 0.3306 | 0.3141 |

逐件成长（新键位，units=mm³，摘自 `grow_lines.log`）：`units=289422 / 52920 / 176400 / 29030 / 275708 / 218696 / 293611 / 806400 / …`；**22 条 units 求和 = 4,838,981**，与重试复位行 `[grow] … units=-4838981 D=0.3306 -> 0.2997 (-9.4%)` 完全相等 → 账本整数守恒 ✓。
结束：`剩余 0 s`、`当前 0.33 m`、`目标 1.25 m`；结果板 `呈给国王 / ☆☆☆  直径 0.33 m / ……就这样？朕的野餐会还没开始呢。再来一次。`（0 星）。重试：`field reset restored=22 props=130`、`retry started round=story_01_bench_size:1 attempt=1`。
截图：关掉 ClientSim `PauseMenu` 后 Game 视图可见（`gameview_result_board.png`）——但回合结束玩家已被送回 hub，`[eye] dist=1207.089 m`，所以画面是 hub 房间与结果板，不是球体；球在视野外。

**卡球**：本轮没有真卡住（相邻周期位移 < 0.06 m 只出现 1 次，末周期对 `gate_cooler_01` d=0.95）。O7 的 `apple_01` 陷阱未复现——它在 t=20.4 被吃掉（units=275708）。19/74 周期触发「unstick 转向」（最近道具 < 0.55 m），最常点名 `gate_basket_01`×4（min d 0.46）、`toy_ball_01`×2（0.30）、`struct_stool_01`×2（0.37）、`pear_04`×2（0.38）、`watermelon_02`（0.35）、`struct_bench_01`（0.41）、`struct_beer_crate_01`（0.38）、`serving_tray_01`（0.41）、`seed_01`（0.40）、`pebble_01`（0.30）、`paper_scrap_01`（0.47）——都是「球 0.33 m 时推得动但还吃不动」的大件，不是死锁。
