# O3a 归因与修复（2026-11-16，a392e46）

两部分互不相关：A 是**判据烘焙过期**（场景数据），不是物理回归；B 是四条**新金标测试**里三条测试算错、一条编辑器代码漏报。
本轮只改代码（Runtime/Editor/Tests/Labs C#），没有编辑任何 `.unity`/`.asset`；PTF 场景必须由操作员重导入并**保存**（见 §3）。

## 1. A：PTF v2 的 31 条 unexpected —— 场景里没有 v2 override 行

### 1.1 结论

`Labs/PhysicsTestField/PhysicsTestField.unity` 里的判据烘焙**早于 v2 override 行**。U# 的代理组件把字段写在
YAML 里，所以可以直接核对（九个格逐字段）：

| 字段 | 场景里出现次数 | 说明 |
|---|---|---|
| `checkIds` / `checkMetrics` / `checkOps` / `checkLimits` / `checkReadouts` / `checkGateStages` / `checkExpectMasks` | 各 9 | 基础判据已烘焙 |
| `checkAppliesMasks` | **0** | 2026-11-15 的 `9a71727` 才加 |
| `deckCenterXZ` | **0** | `a4c9f4a` 才加（v2 的 deck 判据要用） |
| `checkHasV2` / `checkV2Metrics` / `checkV2Ops` / `checkV2Limits` / `checkV2ExpectMasks` / `checkV2Thresholds` | **各 0** | `a4c9f4a` 才加的六个 v2 数组 |

场景最后一次提交是 `a5c21e8`（2026-11-15 15:15），而 `9a71727`、`a4c9f4a` 都在它之后（`a4c9f4a` 是 22:03）。
也就是说这份场景从未在"判据知道接触模型"之后保存过；pass11 那次"好"的 v2 是**在内存里导入判据之后跑的**
（导入器 `PhysicsTestFieldCriteriaImporter.cs:715-719` 会 `CopyProxyToUdon`，所以不存盘也能进 Play），
而它自己就打印 `the scene is left dirty on purpose: this tool never saves`。本轮 O3a 跑场前没有重新导入判据
（README §4 只重导了 StoryMode），于是运行时读到的是旧烘焙：

```
modelIsV2 = true                                   (driver, 电机标志)
cell.HasCheckV2(i) = false 对每一行               (六个数组都是 null → GetCheckV2* 全走"继承 v1")
→ 11 条 v2 override 行全部按 v1 行判 → 31 条 UNEXPECTED
```

伴随症状：本轮 v2 `skipped=8`，pass11 是 `skipped=9` —— 少掉的那一条正是 v2 下 C06 `no_ramp_registered`
变成 n/a 的那条，同样属于"v2 行没生效"。

判据 JSON 自 `66a0585` 起没有改动（`git diff --stat 66a0585..HEAD -- Docs/Design/BallMotor` 为空），
`HasV2Override` 也没有退化（`d3cb411` 的修复在 66a0585 之前），所以**导入一次必然写 11 行**；
Python 边的封闭计数测试（`Docs/tests/levelbuild/test_physics_test_field_cells.py:551`）也钉死 11。

### 1.2 为什么"ramps=0"不是回归

README §5 拿 v1 的 `ramps=1` 对比 v2 的 `ramps=0`，但那是两种模型的**正确**差异（v2 把坡烘成静态 OBB）：

| 行 | 日志 | 读数 |
|---|---|---|
| O3a v1 | `ptf_v1_console.log:213`（run=17 C01） | `ramps=1/obbs=2/rampCell=1` |
| O3a v2 | `ptf_v2_console.log:228`（run=17 C01） | `ramps=0/obbs=2/rampCell=1` |
| pass11 v2（好） | `2026-11-15_pass11_v2_corrected_criteria/console.log:212` | `ramps=0/obbs=2/rampCell=1` |

v2 的 arena 行与 pass11 **逐字相同**。`obbs=2` 就是"1 盒 + 1 坡"，且同一 run 的 `ramp_support`、
`ramp_entered` 都是 PASS —— 坡确实进了接触集。真正的差异是下一行**读的指标**：

| 运行 | `ramp_registered` 行 |
|---|---|
| pass11 v2 | `metric=static_obb_count value=+2.0000 op=>=1.0000 raw=PASS` |
| O3a v2 | `metric=ramp_count value=+0.0000 op===1.0000 raw=FAIL` |

即：override 没被应用，不是几何没登记。

### 1.3 31 条 = 11 条 override 行，逐条对得上

`Docs/Design/BallMotor/physics_test_field.json` 的 v2 块恰好 11 个，受影响判据与条数完全覆盖 31：

| 格 | 判据 | v2 行改变什么 | ALERT 条数 |
|---|---|---|---|
| C01/C03/C05/C13 | `ramp_registered` | `ramp_count ==1` → `static_obb_count >=1` | 16 |
| C14 | `lift_bounded` | 指标 `lift_over_nominal_m` → `lift_over_nominal_in_deck_m` | 4 |
| C06 | `no_climb` | `climb_cap_bound`（符号阈值） | 4 |
| C06 | `stopped_by_wall` | `abs<=0.03` → `<=0.0` | 4 |
| C09/C10 | `on_step` | 0.30 档 fail → pass | 2 |
| C10 | `blocked_gap` | `abs<=0.01` → `<=0.0` 且四档 pass | 1 |
| | | **合计** | **31** |

对照证据：C14 的 v2 好行是 `metric=lift_over_nominal_in_deck_m`（pass11 `console.log` C14 段），
O3a 是 `metric=lift_over_nominal_m`（`ptf_v2_console.log:16`）；C06 的 v2 好行用 `climb_cap_bound`，
O3a 用 v1 的固定 0.05/0.03。

### 1.4 排除的其它假设

| 假设 | 证伪 |
|---|---|
| (1) 某条 v2 行的 metric reader 变了 | `static_obb_count` 读取路径没改，pass11 读到 2；`GetStaticObbCount` 在 `KatamariBallMotor` 里原样保留（v2 的 `ramps=0/obbs=2` 就是它） |
| (2) 容量/分配顺序导致坡被静默拒绝 | `obbs=2` 且 `supports=3`、`ramp_support=PASS`：坡与台面都在表里；`RegistrationBuffersAtCapacity` 若拒，`obbs` 会少且 `[arena]` 会带 `obbRefused/obbOverflow`（本轮没有） |
| (3) PTF.unity 还序列化已删除字段 | 死键被 Unity 忽略，且 `AbsorbSizeLimitTests.TheRetiredRatioAndCeilingFieldsAreGone` 钉住 `maxItemVolumeWorldM3`/`absorbVolumeRatio` 不是字段；与 v2 判据行无关 |
| (5) v2-only 路径读旧门字段 | 电机里已无这两个字段；`LiftOverNominalInDeck` 等 v2 指标在 driver 里独立采样，O3a 的 C14 行只是退回了 v1 指标 |
| (4) 判据没重新导入 / 场景里的烘焙过期 | **成立**，见 §1.1 |

补充：`DriverRevision`/`CellRevision` 都是**编译期常量**（`"2026-11-15e"`），所以首行的 `build=… cellRev=…`
证明不了场景数据是新烘焙的 —— 这正是 §2 的代码修复要补的洞。

## 2. A 的代码修复（不碰场景）

`GetCheckV2RowCount()` 把"表不存在"（-1）和"有表但本格没有 override"（0）分开，driver 用它做两件事：

1. **首行可核对**：`PhysicsTestFieldDriver.cs:213` 追加 ` v2Rows=` + `V2RowsText()`（`:1640`）。
   正常场应打印 `[ptf] build=2026-11-15e cells=9 presets=4 model=v2 cellRev=2026-11-15e v2Rows=11`，
   与导入器报告里的 `v2Rows=11` 直接对照；过期烘焙打印 `v2Rows=missing`。
2. **拒绝出假矩阵**：`PhysicsTestFieldDriver.cs:493` 在切格时若 `model=v2` 且该格没有 v2 表
   （`GetCheckV2RowCount() < 0`），打印
   `Debug.LogError("[ptf] abort reason=staleCriteriaV2 …")` 并中止整场（`:495-505`），
   不再把 11 条 override 当 v1 判却报"UNEXPECTED"。

`PhysicsTestCell.cs:564` 是新的 `GetCheckV2RowCount()`；六个数组任一为 null（或长度短于 `checkIds`，
即部分烘焙）→ -1，否则统计 `checkHasV2[i] != 0` 的行数。这两处都是 Lab C#，不触碰 `.unity`。U# 需要重编译
（`PhysicsTestCell`/`PhysicsTestFieldDriver` 的程序资产会随之变化，属于常规编译噪声）。

## 3. 操作员必须做的（场景侧，唯一落地路径）

1. 重编译 U#（`CompileAllCsPrograms`），确认 0 error。
2. 打开 `Labs/PhysicsTestField/PhysicsTestField.unity`，跑菜单 `VRC Katamari/Physics Test Field/Import Criteria`。
   报告必须含 `v2Rows=11`（当前场景是 `missing`）。
3. **保存场景**并提交（它就是 `windows_status_after.txt:40` 里那个 `M`）。
   不保存 = pass11 的重演：内存里对，落盘没有。
4. 重跑 PTF 36×2。要核对的行：
   - `[ptf] build=2026-11-15e … model=v2 … v2Rows=11`
   - `run=17 cell=C01 D=0.300` 下
     `check=ramp_registered metric=static_obb_count value=+2.0000 op=>=1.0000 expect=pass raw=PASS verdict=EXPECTED`
   - C14 `check=lift_bounded metric=lift_over_nominal_in_deck_m … PASS`
   - 汇总回到 pass11 的 **pass/skipped/unexpected**：`58/1/0、58/1/0、57/2/0、54/5/0`
     （即 field 行 `unexpected=0 skipped=9`）；v1 保持 `58/0/1、57/1/1、56/2/1、52/5/2`
     （`unexpected=5 skipped=8`，5 条是 C05 `climbed` 四档 + C13@2.20 `seam_no_step` 的既有红灯）。
   - 若仍看到 `abort reason=staleCriteriaV2`，说明第 3 步的保存没有生效（或 Unity 又用了旧场景）。

## 4. B：四条新 EditMode 红灯

判据不变：`V = k*a*b*c`；`Req_mm = floor(1000*cbrt(60V/pi) + 1e-3)`；
可吸 iff `floor(1000*D + 1e-3) >= Req_mm`（`PickupAbsorbMath.MmFloorEpsilon = 1e-3`，
Python 孪生 `placement_core.MM_EPSILON = 1e-3`）。四条里 **三条是测试自己算错**，一条是编辑器代码漏报。

### 4.1 `AbsorbSizeGateTests.TheBlockingPredicateAndTheGateAgreeOnEveryCase`（Absorb→RejectBallAtGrowthCap）

- **测试错，不是门错。** 该测试为了"只测尺寸门"，把上限设成
  `MaxGrowthRadiusM = 50f`，但它的 `balls[]` 里有 `100f`，辅助函数又按 `radius = D/2 = 50` 传参；
  成长上限是**含等号**拒绝（`collisionRadiusWorldM >= maxGrowthRadiusWorldM`），于是尺寸门还没说话，
  `RejectBallAtGrowthCap` 先返回。测试自己的注释写的是"上限远高于这里用到的每一个球"，50 并不够。
- 修复：`Tests/EditMode/Pickup/AbsorbSizeGateTests.cs:44` `MaxGrowthRadiusM = 1000f`（对 100 m 球有 20 倍余量），
  并写清为什么。门的顺序和语义未动。

### 4.2 `AbsorbSizeLimitTests.AFullyGrownBallCanTakeAOnePointTwoMetrePlank`（2200→2199）

- **测试手算错，代码对。** 1356 confirmed units 不是"正好 2.20 m"：
  `V = (3.4390244 + 1356) * 0.0041 = 5.573700 m³`，而上限体积是
  `4π/3 * 1.1³ = 5.575280 m³`（差 0.385 个体积单位）。派生直径 = `cbrt(6V/π) = 2.199792 m`，
  于是 `floor(1000*2.199792 + 1e-3) = 2199`。上限是 **core 半径** 1.1 m 的上限，不是一个四舍五入过的
  "2.20 m"标签；门的球侧一直用 `TryFloorMillimetres`（与物件侧同一个 `MmFloorEpsilon`），
  1e-3 mm 也**不应该**去补 0.2 mm 的差（那会变成四舍五入，违背原作的"向下取整"）。
- 修复：断言改成 2199（`:172`），注释给上述推导；并**保留 2200 的用例**——把球灌到真饱和
  （1357 units，`:190`），映射返回上限参数本身，直径恰为 2.2 m → 正好 2200 mm。这样两个数都被钉住：
  未饱和读真实直径、饱和读上限。
- Python 孪生加了一条同样的断言（`Docs/tests/levelbuild/test_placement_core.py`，
  `floor_mm(2.199792) == 2199` 且 `floor_mm(2.2) == 2200`），两侧以后不能各说各话。

### 4.3 `AbsorbSizeLimitTests.TheStartBallIsAWholeMillimetreDiameter`（0.0141→0.0）

- **测试漏项，代码对。** 它算的是
  `GetConfirmedVolumeUnits() * cubicMetersPerVolumeUnit`，但球体积是
  `(baselineVolumeUnits + confirmedVolumeUnits) * cubicMetersPerVolumeUnit`：
  baseline 就是 3.4390244 个同一单位（=0.0141 m³）。起跑时 confirmed=0，所以少乘了 baseline，得 0。
  `LabBallSizeSetter.cs:302` 是另一个读者，它写的就是 `baseline + confirmed`。
- 修复：`Tests/EditMode/BallMotor/AbsorbSizeLimitTests.cs:148` 用
  `(Field("baselineVolumeUnits") + confirmed) * cubicMetersPerVolumeUnit`。

### 4.4 `PickupFieldAuthoringRulesTests.DeclaredVolumeKMismatchesAreReportedById`（2→1）

- **代码错，测试对。** `PickupFieldAuthoringRules.FindVolumeKMismatches` 的"no row"循环遍历的是
  `itemIds`，而落入"声明了但宿主没有这一行"的 id **本来就不在** `itemIds` 里，
  所以那个循环能看到的只有已经 `seen` 的 id —— 永远报不出来（只可能在
  `hostVolumeK` 比 `itemIds` 短时误报尾行）。同族的 `FindDeclaredIdsWithoutRows` 遍历的是**声明字典**，
  `PickupFieldSync` 的注释也要求"declared ids with no row"必须让同步失败。
- 修复：`Editor/Pickup/PickupFieldAuthoringRules.cs:304-336` 改为遍历 `declaredVolumeK` 的键
  （外加 `hostVolumeK` 覆盖不到的尾行），按 ordinal 排序后统一输出 `"<id>: declared <k>, no row"`。
  影响：某个在 placement 里声明了 k、却被同步丢掉的 prop，现在会让导入失败并点出 id —— 这正是
  `FindDeclaredIdsWithoutRows` 对 valueUnits 已有的行为，StoryMode 本关没有声明 k（`declaredVolumeK.Count=0`），
  所以不会给现有场景添新红。

## 5. 已验证 / 待验证

已在 Linux 侧验证：

- `python3 -m unittest discover -s Docs/tests` → **861 tests OK**（含新增的 `floor_mm(2.199792)==2199`）。
- 四个被改的 C# 文件大括号/括号平衡检查通过；**不能在本机编译 C#**（无 Unity/无 dotnet），
  所以 C# 编译与 EditMode 结果必须由 Windows 侧给出。

操作员重跑清单：

1. `CompileAllCsPrograms`（0 error；`PhysicsTestCell`、`PhysicsTestFieldDriver`、`PickupFieldAuthoringRules`
   都在其中）。
2. EditMode 全量：四条门测试转绿（`AbsorbSizeGateTests` 1 条、`AbsorbSizeLimitTests` 2 条、
   `PickupFieldAuthoringRulesTests` 1 条）。已知 `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`
   仍是环境/网络导致的既有红，与本轮无关。
3. PTF 场景重导入 + **保存**，再跑 36×2（见 §3）。
4. StoryMode 不需要重导入（吸收门未改；本关没有声明 volumeK）。`RadialProxyLocalBoundsTests`
   仍会在当前场景留下 `test_prop`（README §3 的泄漏），不在本轮范围。
