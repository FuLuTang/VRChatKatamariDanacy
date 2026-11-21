# 方案 A（按物理体积定价）— 具体重定价方案与可玩性

> 只读分析（2026-11-16 项目时区）。数据：`Docs/Design/Levels/story_01_bench_size/placement.json`（127 件 / 44 def / 1500 u）、`Docs/Design/GameModes/Levels/story_01_bench_size.json`、`StoryMode.unity` 序列化值、`Docs/Art/OriginalModels/props/exports/*.glb`（网格实体体积）。脚本：`repricing2.py` / `final.py` / `build_proposal.py`。没有改任何 tracked 文件。

## 0. 一句话答案：能玩，但必须补件；2.20 m 在本关物理上不可能

* **1.25 m（1 星）当前内容下不可达**：球到 1.25 需要增加 **1.009 m³**；而全关能在这个尺寸之前解锁的物件合计只有 **0.689 m³**（占需要的 68%）。贪心模拟在 **D=0.616 m 卡死**（吃掉 106/127 件后无物可吃）。
* 补 **14 件 / 0.474 m³** 后可玩：1.25 在 **134/135 次吸收**达成，终值 1.317 m（几乎没有余量）。
* 补 **45 件 / 1.147 m³**（推荐）后：1.25 在 156/169 次，1.45 在 164 次，1.60 在 168 次，终值 **1.660 m**（3 星恰好可达）。
* **2.20 m 不可能**：满径需要 5.575 m³，全关物理体积只有 2.517 m³（22%）；即使补到推荐清单也只有 3.66 m³。2.20 只是 `maxCoreRadiusWorldM=1.1` 的包线，本关应把上限降到 **~1.70 m**。
* 重定价后单次冲击：**中位 +0.81% 直径、最大 +3.20% 直径**（今天：中位 +11.6%、最大 +29.3%）。3.20% 就是体积门「门槛处物件 = 球体积 10%」的固有上限。

## 1. 先把目标和结算钉死（已核实）

| 量 | 值 | 出处 |
|---|---|---|
| 通关目标（=1 星） | **1.25 m 直径** | `Docs/Design/GameModes/Levels/story_01_bench_size.json` `quest.objective.target_diameter_m`；`StoryMode.unity` `targetDiameterM: 1.25` |
| 2 星 / 3 星 | **1.45 m / 1.60 m** | 同上 `stars[1..2]`；`StoryMode.unity` `star2DiameterM: 1.45` / `star3DiameterM: 1.6` |
| 时限 | **120 s** | `time_limit_s: 120`；`StoryMode.unity` `timeLimitSeconds: 120` |
| 星级判定 | 达到即得；未达时 `partial_band=0.9` 只影响评语，不影响星 | `StoryStarMath.TryEvaluateSizeStars`（`>=` 含等号） |
| 球径上限 | **2.20 m**（`maxCoreRadiusWorldM=1.1`） | `KatamariBallMotor.cs`；决定 (n) |
| HUD | 显示语义直径（米）；`WriteCurrentHud` 不读 units | `StoryRoundController` |
| 吸收吞吐 | 3 件/s（离线模型值） | `Docs/Design/Gameplay/round_model.py:152`（模型，非实测） |

**目标与上限是两回事**：1.25 是通关线，2.20 只是包线。全文以 1.25 为准，2.20 单独给结论。
顺带发现两处既有不一致（不在本次范围，仅登记）：
1. 关卡 JSON 的 `map_expectations` 仍写 `absorbable_instances_total: 452` / `gain_units_total: 1284`，与 placement 的 127 件 / 1500 u 是两套数（旧全图计划，未随切片更新）。
2. 任务文案说「1.25 m 就能一口吞下长凳」，但 `picnic_bench`（1.2×0.45×0.4）在体积门下 `D_req=1.604`，1.25 m 吃不到；`layout.json` 的 `gate_bench_*` 还写着 `absorbable_at_ball_diameter_m: 1.25`（运行期不读，`SIZE_GATE_AUDIT` §5.3 已记）。

## 2. 定价公式与 f

`valueUnits = max(1, round(f × V_box ÷ c))`，`V_box = a·b·c`（manifest 换轴后的 AABB）。
全关 `ΣV_box=2.517 m³`；44 个 def 的 GLB 都是闭合壳，用散度定理算出的**实体体积** `ΣV_solid=0.826 m³`，加权填充率 **0.328**，单 def 从 **0.03**（`folding_stool`，细框）到 **0.999**（`wood_plank`/板材）。

**f 该不该因形状而异？** 物理上该（差 30 倍），但我不建议把它做进价值表：
1. 体积门用的是 **AABB**（含空腔，决定 (l) / S-AR07）；成长若改用实体体积，门槛与成长就变成两把尺子，「门槛处 10%」这条恒等式立刻失效。
2. 实体体积会让细框件几乎不值钱（折叠凳 0.0014 m³ → 3 u），而它在关卡里是「挡路 → 可吃」的节奏件；这会把中后期阶梯打得更碎。
3. 数据挖掘出来的原作形状是「**物件体积 × 比例**」，那个 volume 就是判定用的同一个量（S-AR01）；即 f 是**全局压实系数**，不是每件形状系数。
所以：**推荐 f = 1.0**（成长体积 = AABB 体积，与门槛同量），`f=0.8`（原作小球的压实比例，S-AR01，未核验）作为更慢的备选。实体填充率只用于「这件道具是不是空壳」的合理性检查，不进 value。

## 3. c（每单位体积）与整数量化

起始体积固定 0.0141 m³（起始直径 0.2997 m 不动），所以 `baselineVolumeUnits = 0.0141 / c`。下表「全关总 u」是**按 f=1 重定价后**的值，不是今天的 1500 u。

| c (m³/u) | 1 u 体积 | baseline u | 起始 1 u 的体积涨幅 | 起始 1 u 的直径量子 | <1 u 的件数 | 全关总 u (f=1) | 1.25 m 所需 u |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 0.0041 | 4.1 L | 3.4 | 29.1% | 26.6 mm | 99/127 | 699 | 246 |
| 0.002 | 2.0 L | 7.0 | 14.2% | 13.6 mm | 95/127 | 1337 | 504 |
| 0.001 | 1.0 L | 14.1 | 7.1% | 6.9 mm | 86/127 | 2579 | 1009 |
| 0.0005 | 0.5 L | 28.2 | 3.5% | 3.5 mm | 71/127 | 5073 | 2017 |

* `c=0.0041`（今天）：**99/127 件不足 1 u，被 `max(1,·)` 抬到 1 u**；开局 87 件各值 1 u，合计 0.357 m³，把球从 0.30 推到 **0.89 m**——这就是「一次吃一件也涨很多」的量化来源。
* `c=0.001`：1 u = 6.9 mm；86 件被抬到 1 u；总 2579 u。
* `c=0.0005`：1 u = 3.5 mm；71 件被抬到 1 u（都是 0.5 L 以下的小件；被抬高的合计 **+39 u = 0.020 m³**，占全关 0.8%）；总 5073 u。**推荐**。
* `c=0.00025` 还能更细（1.8 mm），但对 71 件小件已无意义（它们本来就 <1 u），只会把账本数字再翻倍。

**改 c 要动的常量**：`KatamariBallMotor.cubicMetersPerVolumeUnit`（0.0041 → 0.0005）、`baselineVolumeUnits`（3.4390244 → **28.2**，= 0.0141/0.0005）；`maxCoreRadiusWorldM` 建议 0.85（= 1.70 m 本关上限，见 §5）。`absorbVolumeRatio=10`、`maxItemVolumeWorldM3=512` **不动**（门槛与 c 无关）。`SizeModelMath` 不需要改（它只吃 baseline/confirmed/c 三个数），但它的单测期望值要跟着改。

## 4. 第一关重算（f=1.0, c=0.0005）

* 127 件总价值 **5073 u**（旧 1500 u）；`valueUnits` 见 `new_value_table.csv`。
* 1.25 m 需要 **2017 u**（占总 u 的 40%）；1.45 m 需要 3164 u；1.60 m 需要 4261 u；2.20 m 需要 11122 u（> 全关总 u 的 2 倍）。
* 物理供给：`D_req ≤ 1.25` 的物件合计 **0.689 m³**，而到 1.25 需要 **1.009 m³** → **差 0.32 m³**；`D_req ≤ 1.60` 只有 0.995 m³，到 1.60 需要 2.131 m³ → **差 1.15 m³**。
* 开局（D=0.2997）：**87 件**可吸，合计 89 u（0.0445 m³），全吃完只到 **D=0.482 m**。
* **卡死检查**：不加件时贪心在 **D=0.616 m** 停住（106 件后，下一件 `picnic_basket` 0.0299 m³ 需要球 0.299 m³，实际只有 0.095）。

贪心曲线（推荐清单，169 次吸收，每 8 步取一点）：

| 吸收数 | D (m) | 代表物件 |
|---:|---:|---|
| 1 | 0.303 | seed |
| 9 | 0.329 | pebble |
| 17 | 0.351 | strawberry |
| 25 | 0.370 | grapes |
| 33 | 0.388 | bread_slice |
| 41 | 0.404 | pear |
| 49 | 0.419 | juice_carton |
| 57 | 0.433 | sandwich |
| 65 | 0.447 | apple |
| 73 | 0.459 | cup |
| 81 | 0.471 | croissant |
| 89 | 0.490 | plate |
| 97 | 0.520 | plate |
| 105 | 0.563 | toy_ball |
| 113 | 0.616 | small_potted_plant |
| 121 | 0.679 | serving_tray |
| 129 | 0.794 | watermelon |
| 137 | 0.907 | watermelon |
| 145 | 1.048 | picnic_basket |
| 153 | 1.200 | folding_stool |
| 161 | 1.392 | storage_bin |
| 169 | 1.660 | picnic_bench |

## 5. 缺口分析（按直径档，f=1.0）

每档「需要」= `V(hi)−V(lo)`；「供给」= `D_req` 落在该档的物件 `V_box` 之和。注意这是**乐观**口径：它假设档内每件都吃得到，实际还受「先长大才能吃下一件」的约束（见模拟）。

| 档 (m) | 件数 | 供给 V_box (m³) | 需要 (m³) | 缺口 (m³) | 现有 def 可复用 |
|---|---:|---:|---:|---:|---|
| 0.30–0.40 | 8 | 0.0119 | 0.0194 | +0.0075 | plate(0.00144) / bowl(0.00154) |
| 0.40–0.55 | 11 | 0.0518 | 0.0536 | +0.0018 | toy_ball(0.00338) / book_stack(0.00445) / watering_can(0.00502) / serving_tray(0.0077) |
| 0.55–0.75 | 5 | 0.0720 | 0.1338 | +0.0618 | wood_plank(0.01225) / watermelon(0.01584) |
| 0.75–1.00 | 6 | 0.2016 | 0.3027 | +0.1011 | picnic_basket(0.0299) / beer_crate(0.0336) / folding_stool(0.0448) / cooler_box(0.0578) |
| 1.00–1.25 | 4 | 0.3267 | 0.4991 | +0.1723 | cooler_box(0.0578) / storage_bin(0.077) / crate(0.096) |
| 1.25–1.45 | 1 | 0.1260 | 0.5736 | +0.4476 | cooler_box_large(0.126) / crate_large(0.18) |
| 1.45–1.60 | 1 | 0.1800 | 0.5484 | +0.3684 | picnic_bench(0.216) |
| 1.60–2.20 | 4 | 1.5220 | 3.4306 | +1.9086 | lounge_chair(0.336) / folding_table(0.42) / stroller(0.55) |

到 1.25 m 的档内缺口合计 **+0.344 m³**（但模拟证明实际要补 0.47 m³：因为档内顺序还会再卡一次）。到 1.60 m 还要再加 0.82 m³。**1.60–2.20 档缺 1.91 m³，本关不可能补齐**——那一段只能靠换图或降上限。

### 补件清单（只复用现有 44 个 def，不新建模）

| 档 | def | MIN 件数 | REC 件数 | 单件 V_box | REC 小计 (m³) |
|---|---|---:|---:|---:|---:|
| 0.30 | plate | 0 | 6 | 0.0014 | 0.0086 |
| 0.40 | toy_ball | 0 | 6 | 0.0034 | 0.0203 |
| 0.44 | book_stack | 0 | 2 | 0.0044 | 0.0089 |
| 0.46 | watering_can | 0 | 2 | 0.0050 | 0.0100 |
| 0.53 | serving_tray | 1 | 1 | 0.0077 | 0.0077 |
| 0.62 | wood_plank | 1 | 1 | 0.0122 | 0.0122 |
| 0.67 | watermelon | 6 | 10 | 0.0158 | 0.1584 |
| 0.83 | picnic_basket | 0 | 3 | 0.0299 | 0.0896 |
| 0.86 | beer_crate | 0 | 3 | 0.0336 | 0.1008 |
| 0.95 | folding_stool | 2 | 3 | 0.0448 | 0.1344 |
| 1.03 | cooler_box | 2 | 3 | 0.0577 | 0.1732 |
| 1.14 | storage_bin | 2 | 3 | 0.0770 | 0.2310 |
| 1.22 | crate | 0 | 2 | 0.0960 | 0.1920 |
| | **合计** | **14** | **45** | | **1.147** |

验证（模拟）：
* MIN：1.25 在 **134/135** 次达成，终值 1.317 m。
* REC：1.25 在 **156/169**，1.45 在 164，1.60 在 168，终值 **1.660 m**。
* 结论：**1 星可玩**（MIN 就够），但「吃掉 99% 物件」；REC 才有 3 星和一点余量，仍然是「吃掉 92%」。若想要原作那种「吃 50–70% 就达标」的松弛感，需要的不是 45 件而是约 **90 件 / 2.2 m³** 的可达内容。

## 6. 17 件结构件重定价（f=1, c=0.0005）

| itemId | def | V_box (m³) | D_req (m) | 旧 u | 新 u | 实体填充 | 曲线位置 |
|---|---|---:|---:|---:|---:|---:|---|
| struct_book_stack_01 | book_stack | 0.0044 | 0.44 | 8 | 9 | 0.76 | 1 星前 |
| struct_tray_01 | serving_tray | 0.0077 | 0.53 | 12 | 15 | 0.45 | 1 星前 |
| struct_tray_02 | serving_tray | 0.0077 | 0.53 | 12 | 15 | 0.45 | 1 星前 |
| struct_plank_01 | wood_plank | 0.0122 | 0.62 | 16 | 24 | 1.00 | 1 星前 |
| struct_plank_02 | wood_plank | 0.0122 | 0.62 | 16 | 24 | 1.00 | 1 星前 |
| struct_beer_crate_01 | beer_crate | 0.0336 | 0.86 | 36 | 67 | 0.28 | 1 星前 |
| struct_beer_crate_02 | beer_crate | 0.0336 | 0.86 | 36 | 67 | 0.28 | 1 星前 |
| struct_stool_01 | folding_stool | 0.0448 | 0.95 | 38 | 90 | 0.03 | 1 星前 |
| struct_storage_01 | storage_bin | 0.0770 | 1.14 | 52 | 154 | 0.96 | 1 星前 |
| struct_crate_01 | crate | 0.0960 | 1.22 | 64 | 192 | 0.84 | 1 星前 |
| struct_crate_02 | crate | 0.0960 | 1.22 | 64 | 192 | 0.84 | 1 星前 |
| struct_cooler_large_01 | cooler_box_large | 0.1260 | 1.34 | 90 | 252 | 0.96 | 3 星前 |
| struct_crate_large_01 | crate_large | 0.1800 | 1.51 | 120 | 360 | 0.71 | 3 星前 |
| struct_bench_01 | picnic_bench | 0.2160 | 1.60 | 130 | 432 | 0.12 | 3 星门槛件（1.60） |
| struct_lounge_chair_01 | lounge_chair | 0.3360 | 1.86 | 150 | 672 | 0.12 | **不可达**（>1.66） |
| struct_folding_table_01 | folding_table | 0.4200 | 2.00 | 212 | 840 | 0.05 | **不可达**（>1.66） |
| struct_stroller_01 | stroller | 0.5500 | 2.19 | 32 | 1100 | 0.15 | **不可达**（>1.66） |

* 结构件合计 **4505 u**（占 89%），旧表是 1088 u（占 73%）。重定价把 89% 的物理体积装进了结构件，这是对的（它们真的很大）。
* 但 `lounge_chair`(1.86) / `folding_table`(2.00) / `stroller`(2.19) 三件在 1.66 m 的终值下**永远吃不到**，却占 **2612 u（51%）**。按决定 (n) 的先例（野餐桌/公园长椅已移出第一关）：**这三件也应移出或缩小声明体积**，否则半张表是死重量。
* `folding_stool` 实体填充 0.03：AABB 0.045 m³ 但只有 1.4 L 实体。f=1 仍按 0.045 m³ 给 90 u（与门槛同量），这是「门槛含空腔」的忠实结果，不是错误，但设计师要接受「一个空架子很值钱」。

## 7. 推荐配置

**f = 1.0，c = 0.0005 m³/u，baseline = 28.2 u，本关上限 1.70 m，+ REC 45 件。**

* 新价值表：`/tmp/growth_audit/new_value_table.csv`（44 行，含 V_box / V_solid / fill / D_req / 旧 u / 新 u@c=0.0005 / 新 u@c=0.001）。
* 新总量 5073 u；1.25 m = 2017 u（40%），1.60 m = 4262 u（84%）。
* 星级：1.25 / 1.45 / 1.60 全部可达（REC 下 156 / 164 / 168 次），2.20 不可达。
* 手感：单次最大 +3.2% 直径（门槛固有的 10% 体积），中位 +0.8%；开局 87 件只把球推到 0.48 m。
* 时间不是瓶颈：REC 下要吃掉 169 件，按 3 件/s 约 56 s（< 120 s）；瓶颈是内容体积与阶梯，不是时限。

**为什么不用 f=0.8**：同样 c 下要 22 件才勉强到 1.25（终值 1.29），3 星要多补 ~0.3 m³；f=0.8 的出处只是 S-AR01 的附带表（未核验），而 f=1 有「门槛与成长同量」的硬理由。若试玩后仍觉得快，再回到 0.8，代价是补件。
**为什么不用 c=0.001**：结论一样、还能少补 4 件（10 件即可），但 1 u = 6.9 mm，开局量子比 0.0005 粗一倍；c=0.0041 则会让 99 件被抬到 1 u，开局一跳 0.30→0.89 m，等于把用户抱怨的现象保留下来。

## 8. 实施顺序与风险

**顺序（数据 → 代码 → 测试 → 文档）**
1. `Tools/levelbuild/placement_core.py`（含 `canonical_doc` / 校验器）：加 `value_units_for(vbox, f, c)`（唯一实现）与 `expected.valueUnits` 校验；用它重算 `Docs/Design/Levels/story_01_bench_size/placement.json` 的 `defs[].valueUnits`（44 个），并把 `expected.valueUnits` 从 1500 改成 5073；**只改 defs，不逐 prop 改**（props 的 `valueUnits` 是导入器回读的快照，要一起重生成）。
2. `Editor/LevelBuild/PlacementImporter.cs` + `Editor/Pickup/PickupFieldSync.cs`：已具备「只消费声明值 + 回读校验」，确认缺省回退路径不再写 0；重新导入场景。
3. `KatamariBallMotor.cs`：`cubicMetersPerVolumeUnit 0.0041→0.0005`、`baselineVolumeUnits 3.4390244→28.2`、`maxCoreRadiusWorldM 1.1→0.85`（1.70 m）；并同步**所有序列化覆盖**：`StoryMode.unity`、`PhysicsTestField.unity`、`CpuSkirmish.unity`（4 个电机）、`CpuSkirmishBootstrap.ProbeBank` 里硬编码的 `3.4390244f/0.0041f`（`Docs/Evidence/2026-11-15-cleanup-audit-ai.md` D2 已记这处重复）。
4. 测试：`Tests/EditMode/BallMotor/AbsorbSizeLimitTests`（默认值与上限读数）、`Tests/EditMode/Story/StoryStarMathTests`（若用单位构造）、`Tests/EditMode/Pickup/PickupFieldValueUnitsTests`、`Docs/tests/levelbuild/test_placement_core.py`（新的定价函数）、`Docs/tests/` 里 1356/1500 的用例。
5. 文档：**至少 12 份**提到 1500 u / 1356 u / 0.0041——`Docs/Design/Parameters/README.md`（1356 → 11122，0.0041 → 0.0005）、`Docs/OPEN_DECISIONS.md` (q)/U1、`Docs/Design/StoryMode/PICNIC_CORNER_LAYOUT.md`（1500/1088/412）、`Docs/Design/StoryMode/PICNIC_CORNER_LEVEL_ONE.md`、`PROP_SET_PLAN.md`、`LEVEL_CONTENT_PLAN.md`、`StoryMode/README.md`、`Docs/Research/KatamariMaps/LEVEL_CONSTRUCTION.md:198`（1356）、`Docs/Design/Maps/PicnicLawn/README.md`、`Docs/Design/Maps/ToyRoom/README.md`、`Docs/Design/Tools/LEVEL_AUTHORING.md`、`Docs/Design/PickupAbsorb/README.md`（1 u = 4.1 L 的说法）、`Docs/Design/Gameplay/round_model.py` + `config/prototype_defaults.json`（units_to_max_diameter）。
6. 关卡数据：补 REC 45 件（placement 的 props + 对应 Unity 实例），并把 `map_expectations` 的 452/1284 更新或标成 stale。

**风险（按严重度）**
1. **账本与分数尺度**：c 变 8.2×，所有「1 u = 4.1 L」的隐含假设、PvP 交存分、`banked_score` 全部变 8.2×。故事关 `bank.enabled=false`，不影响本关；但 `round_model.py` 的 `swallow` 与 PvP 预算要一起重标。
2. **CPU 预测**：`CpuSkirmishBootstrap.ProbeBank` 硬编码了旧的 `3.4390244/0.0041`，不改会让 CPU 的缩球预测与电机不一致。
3. **文档数字**：1500 u / 1356 u / 0.0041 在至少 12 份文档与 2 份配置里出现；漏改会让「数值冻结」与校验器对不上。
4. **HUD/星级不依赖 u**：`WriteCurrentHud` 只用米，星级阈值也是米，所以 HUD 与星级本身安全；变的是「多久能到」。
5. **3 星太紧**：REC 下 1.60 要吃掉 168/169 件；建议要么把 3 星降到 1.55，要么再加 10 件中件。
6. **`maxItemVolumeWorldM3=512` 与 `absorbVolumeRatio=10` 不用动**，但 `maxCoreRadiusWorldM=0.85` 是每关参数（决定 n），其它关（`PhysicsTestField`/`CpuSkirmish`）要各自填。
7. **`@size=` 仍未接线**：结构件如果要「按声明体积而不是渲染 AABB」计价，必须先让产出端写 `@size=`（`SIZE_GATE_AUDIT` §5.3）；本次清单里的 `picnic_bench`(1.2×0.45×0.4) 是真实 AABB，不依赖它。

## 9. 测得的 vs 假设的

**测得/算得**：44 个 def 的 V_box（placement）、V_solid（GLB 散度定理，闭合壳）、fill、D_req；ΣV_box=2.517、ΣV_solid=0.826、加权 fill=0.328；供给 0.689/0.995；贪心曲线与卡死点 0.616；补件后的里程碑 134/156/164/168；冲击中位 0.81%、最大 3.20%；需求 2017/3166/4262 u。
**假设**：① f=1.0 是「成长 = AABB 体积」的选择，不是唯一正确；f=0.8 是 S-AR01 的未核验片段。② 吞吐 3 件/s 来自离线模型，不是实测；③ 贪心「D_req 从小到大、够得着就吃」忽略 reach/地形/跑图；④ `c=0.0005` 的 71 件 <1 u 被抬到 1 u，合计抬高 0.020 m³（+0.8%），可接受但不是零；⑤ 补件默认复用现有 def 的 AABB 与外观，没有新模型。
**没有验证**：Unity 编译、Play Mode、VR、网络；本文没有运行时证据。
