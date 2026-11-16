# 剧情模式关卡内容计划（Labs/StoryLevel 切片）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。只做离线内容与算术，不改 Unity/运行时/美术；不声称 Play Mode 或 Build & Test 已验证。
> **2026-11-15 增补 §7**：用场景里已落地的 110 件 / 412 单位数据复算 ★/★★/★★★，并用一次 Play Mode 实测（120 s → 1.43 m）校准。结论：★ 1.25 m（59.7%）单看门槛不松，但 ★→★★ 隔了 34 个百分点；★★ 1.45 m 要 93.7%，实测差 4 件没拿到——是「紧」不是「打不过」；★★★ 1.60 m 要 126.1%，上限只有 1.4817 m，**结构不可达**。建议把切片自己的 ★★/★★★ 改成 **1.40 / 1.45 m**（§7.6），关卡 JSON 不动。§0 与 §2.2–§2.4 的旧结论已就地标注/更正（保留原文并写明哪句过时）。
> 范围：`Labs/StoryLevel` 的 20×20 m 平地切片，供 `story_01_bench_size`（1.25 m / 120 s）与 `story_02_fruit_ten`（fruit ×10）共用。共用是本文的 PROVISIONAL 选择（§0.2），所有数值与摆放都是占位数据。
> 引用约定：`路径:行` 相对仓库根；`StoryLevel.unity` = `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/StoryLevel/StoryLevel.unity`；`[csv:N]` = `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 第 N 行；`PickupFieldSync.cs`/`PickupFieldAuthoringRules.cs` = `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/` 下同名文件。

## 0. 结论

0.1 **宿主是 110 行 / 412 单位（2026-11-15 场景实测）。** `StoryLevel.unity` 里 `PlaygroundProp` **0 命中**，宿主是 110 行、`itemIds` 110 个唯一、`valueUnits` 合计 **412**，另有一组 110 个 `propColliders` 指向新道具（名字如 `thermos_01`、`bottle_cap`）。旧 `Item_00..Item_29` 只剩 30 个**不再被宿主引用**的方块。§7 的全部复算都基于这份场景数据，不是基于 §2.1 的计划表。

0.2 **本计划把场地重排成 110 件 / 412 单位，两关共用。** 关卡 1 验 1.25 m；关卡 2 只数其中 40 件 `kind=fruit`。这与两关共用 `picnic_lawn` 同构（`Docs/Design/GameModes/README.md:169`）。

0.3 **1.25 m 可达且有余量。** 1.25 m = **245.91 单位**（`Docs/Design/GameModes/README.md:212`；公式 `d=d0·(V/V0)^(1/3)`、1356 单位 = 0.30→2.20 m，`Docs/Design/Gameplay/README.md:56`、`Docs/Design/Gameplay/round_model.py:110-115`）。412/245.91 = **1.68×**（计划要求 ≥1.6×，`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:94`）；达标只吃掉 245.91/412 = **59.7%**（S5 要求 ≤65%，`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:132`）。**复算（§7.2）：** 用运行时常量（`baselineVolumeUnits = 3.4390244`、`c = 0.0041 m³/u`）精确值是 **245.99 u**，与 245.91 差 0.08 u（0.03%），原因是 `round_model` 的每单位体积取 `(V_max−V_0)/1356 = 0.0040992` 而运行时用 0.0041；比例不变（59.7%、1.68×）。

0.4 **切片只能验 ★ 和勉强 ★★，验不了 ★★★（2026-11-15 复核，数值见 §7）。** 「勉强 ★★」这个说法太轻，但也别读成「打不过」。用 §7.2 的精确值，★★ = **385.89 u = 93.7%**，门槛模拟的最省件路径是 **84 件 / 386 u**——4 单位以上的 82 件一件都不能漏，只剩 26.11 u 余量（刚好等于 26 件 1 单位件）；漏 1 件 4 单位件就掉到 1.4452 m。**但 Play Mode 实测（n=1）120 s 就跑到 1.43 m = 370.00 u（89.8%），只差 15.89 u ≈ 4 件四单位件**——余量极低但一次真人运行就摸到了，所以 ★★ 是「紧」而不是「结构上打不过」。★★★ 1.60 = **519.65 u = 126.1%** 才是硬不可能：把 412 u 全吃光也只到 **1.4817 m**。**但这是切片的问题、不是关卡 JSON 的问题**：同一组 1.25/1.45/1.60 在 1284 单位的出货地图上是 19.2%/30.1%/40.5%（§7.7）。§7.6 建议改切片自己的两个序列化阈值，**不改关卡 JSON**——那才是「改目标去凑」。

0.5 **尺寸门已按决定 (h) 落地（2026-11-15）。** 场景实测 `maxItemSizeWorldM: 8`、`absorbSizeRatio: 1`（`StoryLevel.unity:12448-12449`），判定是 `min(8, 语义直径 × 1.0)`；所以 §2.3 的阶梯就是现在的运行时行为，0.30 m 球的上限是 0.29974 m。本切片所有件 ≤0.90 m，**不要用本切片证明安全上界有效**。

## 1. 现在有什么、许可状态

### 1.1 场地与现有占位

- 地面 20×20 m、顶面 y=0（`StoryLevel.unity:5243-5244`）；四墙内表面约 ±9.8 m（南墙 `:2768-2769`）；出生点 (0, 0.5, −3)（`:5942`）。
- 宿主 **110 行 / 412 单位**；`allowedKinds` 已含 nature/debris/food/fruit/tableware/drink/toy/garden/container（`:2981-2989`）；道具实际中心 x ∈ [−8.66, 7.22]、z ∈ [−8.74, 7.47]、y ∈ [0.02, 0.45]（逐件 Transform + Collider 回读，§5 命令），没有越界件。

### 1.2 可用美术与许可

| 来源 | 内容 | 许可状态 | 现在能否进场景 |
|---|---|---|---|
| Unity 内置 primitive | Cube/Sphere/Cylinder | 引擎内置，无第三方素材许可 | **可**（立即） |
| 自建 `Docs/Art/OriginalModels/` | ball_core、carrier、exit、cloth、item_apple/cup/plate/sugar（`generate.py` 可复现；尺寸 `Docs/Art/OriginalModels/manifest.json:11-14`） | 自有原创、无第三方内容；仓库明确「未指定授权许可」（`Docs/Art/OriginalModels/README.md:3`） | **可**（自用）；对外分发/再许可见 `AGENTS.md:40`，未定 |
| 自建 `Docs/Art/OriginalModels/props/` | seed/acorn/pebble/bottle_cap/paper_scrap/toy_ball/napkin/watering_can | 自有；「no licence assigned」（`Docs/Art/OriginalModels/props/README.md:3`） | **可，且已导入 Unity** `Assets/VRC_Katamari/Art/Props/`（`Docs/Evidence/AssetImport/README.md:5`、`:60`），尺寸已回读 |
| Kenney Food Kit 2.0 | 水果、饼干、面包、餐具、罐/盒 | **CC0-1.0**，包级证据（`Docs/AssetIntake/DirectDownload/manifest.json:26`；`Docs/AssetIntake/DirectDownload/kenney_food-kit_License.txt:10`）；署名非必需 | **可导入**（尚未导入 Unity，`Docs/AssetIntake/Preview/README.md:12`） |
| Kenney Nature Kit 2.1 | flower_redA、mushroom_red、pot_small、rock_smallA | **CC0-1.0**，包级证据（`Docs/AssetIntake/DirectDownload/manifest.json:62`） | **部分可**，形状不符项见 §1.3 |
| Quaternius 食物包 | 仅网页声称 CC0 | 无包级证据、未下载（`Docs/AssetIntake/license_gaps.md:10`） | **不可用** |
| 其他下载/未记录许可 | — | 无 licenseId / licenseTextPath | **不可用**（`Docs/03_MAPS_ASSETS.md:74-79`） |

原则：只有「有记录且兼容的许可」才进场景，缺失的先隔离（`Docs/03_MAPS_ASSETS.md:79`）。Kenney 的授权页与包内 License.txt 已归档，属可记录的一种；`Docs/AssetIntake/DirectDownload/manifest.json:4` 仍写明「不是最终 asset/license acceptance」，所以逐项引用包级证据，不写「已清权」。

### 1.3 明确「暂不可用」

- `folding_stool` 1.40 m、`picnic_basket` 1.20 m、`cooler_box` 1.60 m、`collapsed_bench` 3.60 m：全部 >1 m，当前绝对门必拒（`[csv:33-38]`；`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:64`）；`flower_pot_large` 1.00 m 卡在 `>` 的等号边界，本计划不用它，避免把「等于 1.0 算不算过大」变成隐式规则。
  **2026-11-15 更正：** 上面这条理由已随 (h) 落地失效——1 m 不再是门，这四件现在只在球长大到 ≥它们的最长边时才可吃（1.40 m 的凳要 1.40 m 球 = 346.99 u，§7.2）。它们仍然不进本切片，理由换成「切片只需 20×20 平地上的 ≤0.90 m 件，加进来会把上限从 1.4817 m 抬高、打乱 §7 的带宽」；`flower_pot_large` 的等号边界问题也不再存在（比较含等号，`PickupAbsorbMath.cs:163-164`）。
- Kenney `bread` 是**面包片**不是整条（`Docs/AssetIntake/Preview/README.md:27`）、`cheese-cut` 是**带孔薄片**不是楔块（`:72`）、`pot_small` 是**空花盆**（`:32`）、`rock_smallA` 是**橙色多面体**不符合鹅卵石（`:33`、`:72`）。前三个换概念或组装后才能用；石头用我们自己的 `pebble.fbx`，不依赖它。
- Kenney Furniture Kit 只有网页声称、没有下载包（`Docs/AssetIntake/license_gaps.md:8`）：不可用。

## 2. 关卡 1：20×20 场地与道具表

**视觉源图例**：`O` = 已导入 Unity 的自建 FBX（`Assets/VRC_Katamari/Art/Props/`）；`K` = Kenney CC0 GLB/FBX（需导入）；`U` = Unity primitive；`P` = 自建 primitive/程序化占位。所有 `K` 行都可先用 `U` 占位，只换视觉、不改 `bounds/value/kind`。

### 2.1 道具表（110 件 / 412 单位）

| id | 源 | 档 | bounds m | 值 | 件 | kind | 小计 |
|---|---|---|---|---:|---:|---|---:|
| seed `[csv:2]` | O | s | 0.04 | 1 | 3 | nature | 3 |
| acorn `[csv:3]` | O | s | 0.05 | 1 | 2 | nature | 2 |
| pebble `[csv:4]` | O | s | 0.06 | 1 | 1 | nature | 1 |
| bottle_cap `[csv:6]` | O | s | 0.05 | 1 | 1 | debris | 1 |
| paper_scrap `[csv:7]` | O | s | 0.10 | 1 | 1 | debris | 1 |
| fallen_leaf `[csv:5]` | K | s | 0.10 | 1 | 1 | nature | 1 |
| cookie `[csv:12]` | K | s | 0.09 | 1 | 2 | food | 2 |
| cheese_slice `[csv:13]` | K | s | 0.08 | 1 | 1 | food | 1 |
| strawberry `[csv:10]` | K | s | 0.06 | 1 | 8 | fruit | 8 |
| grapes `[csv:11]` | K | s | 0.08 | 1 | 8 | fruit | 8 |
| apple `[csv:14]` | K | m | 0.30 | 4 | 6 | fruit | 24 |
| pear `[csv:15]` | K | m | 0.32 | 4 | 5 | fruit | 20 |
| orange `[csv:16]` | K | m | 0.30 | 4 | 6 | fruit | 24 |
| banana `[csv:17]` | K | m | 0.45 | 4 | 4 | fruit | 16 |
| croissant `[csv:18]` | K | m | 0.34 | 4 | 5 | food | 20 |
| bread_slice `[csv:19]` | K | m | 0.30 | 4 | 5 | food | 20 |
| sandwich `[csv:20]` | K | m | 0.38 | 4 | 4 | food | 16 |
| cup `[csv:21]` | K | m | 0.26 | 4 | 5 | tableware | 20 |
| bowl `[csv:23]` | K | m | 0.42 | 4 | 4 | tableware | 16 |
| plate `[csv:22]` | K | m | 0.50 | 4 | 4 | tableware | 16 |
| spoon `[csv:24]` | K | m | 0.34 | 4 | 4 | tableware | 16 |
| soda_can `[csv:25]` | K | m | 0.24 | 4 | 5 | drink | 20 |
| juice_carton `[csv:26]` | K | m | 0.34 | 4 | 4 | drink | 16 |
| toy_ball `[csv:31]` | O | m | 0.40 | 4 | 4 | toy | 16 |
| napkin `[csv:32]` | O | m | 0.30 | 4 | 5 | tableware | 20 |
| small_potted_plant `[csv:29]` | K 组装 | m | 0.55 | 6 | 2 | garden | 12 |
| watering_can `[csv:30]` | O | m | 0.58 | 6 | 2 | garden | 12 |
| glass_bottle `[csv:27]` | K | m | 0.52 | 4 | 1 | drink | 4 |
| watermelon `[csv:28]` | K | m | 0.72 | 4 | 3 | fruit | 12 |
| thermos `[csv:34]` | P | l | 0.90 | 16 | 4 | container | 64 |
| **合计** | | | | | **110** | | **412** |

模型门槛：`round_model` 把 s 夹到 ≥0.08、m 夹到 ≥0.32，所以 strawberry 的门是 0.08、apple/orange 是 0.32；运行时 (h) 比的是 `bounds`，表里 `bounds` 保持视觉最长边（0.06/0.30/0.30）（`Docs/Design/GameModes/README.md:278-282`、`Docs/Design/Gameplay/round_model.py:310-315`、`Docs/Design/Maps/PicnicLawn/layout.json:49-56`）。**不要为了让模型门槛好看去改 bounds**；两套口径见 §2.4。

数值口径：s=1、m=4 是计划基线（`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:94`）；工具表对 garden m 给 6、container l 给 16，所以 `small_potted_plant`/`watering_can` 6、`thermos` 16；`glass_bottle` 与 `watermelon` 按工具表取 4（目录写 5/6，工具表取低值，见 §4.2），全是 ≥1 的整数（决定 (j)，`Docs/OPEN_DECISIONS.md:57`）。`thermos` 是唯一 ≤1 m 的 l 档（0.90 m，`[csv:34]`），作为后期大件。110 件略多于计划的「~100」：s 起步环 + m 主体 + 4 个 l 收益件；用 100 件凑不出 412（s=1/m=4 时 100 件上限约 400，且几乎没有起步物）。

### 2.2 算术（1.25 m 可达、余量、上限）

1. **需要** 245.91 u；**有** 412 u；412/245.91 = **1.675×** ≥1.6×；245.91/412 = **59.7%** ≤65%。
2. **件数与上限**：按 412/110 ≈ 3.7 u/件，245.91 u 摊到平均值上约 **66 件**；按「先吃 16/6 单位大件、再吃 4 单位」的价值优先顺序只要 **48 件**（§7.5）。S5 的「≤80 s、≤65%」要在 Play Mode 实测，本文不预支（`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:132`）。
3. **★★/★★★ 复核（§7）**：旧写「★★ 1.45 = 385.78 u = 93.6%，要吃光几乎全场；★★★ 1.60 = 519.5 u > 412，不可达」。精确值是 385.89 u = **93.7%** 与 519.65 u = **126.1%**；方向对，但★★ 的真实形态是「余量 26.11 u 的极紧门」——**实测 120 s 到 1.43 m（370.00 u，89.8%）就差 4 件**。所以 ★★ 不是打不过，是星级带宽错位（1 星 59.7% / 2 星 93.7% / 3 星 126.1%），建议按 §7.6 重定。本切片的上限是 **1.4817 m**，1.60 在任何情况下都不可达。

### 2.3 成长路径（(h) 已于 2026-11-15 生效，按 bounds；单位见 §7.2）

| 球径 m | 需要单位 | 新解锁 |
|---:|---:|---|
| 0.30 | 0 | s 档 0.04–0.10；m 档 0.24–0.30：soda_can/cup/apple/orange/bread_slice/napkin |
| 0.32 | 0.74 | pear；模型口径的 apple/orange |
| 0.34–0.38 | 1.57–3.56 | croissant/spoon/juice_carton；sandwich |
| 0.45 | 8.19 | banana（约 3 件 m） |
| 0.50–0.52 | 12.51–14.50 | plate；glass_bottle |
| 0.55–0.58 | 17.79–21.46 | small_potted_plant；watering_can |
| 0.72 | 44.21 | watermelon |
| 0.90 | 89.63 | thermos（约 22 件 m） |
| 1.25 | 245.91 | 关卡目标 |

起手语义直径 0.29974 m（`Docs/Design/BallMotor/README.md:130`）确实能吃最小档 0.04 m（0.04 ≤ 0.30×1.0）；球心/碰撞半径 0.14987 m（同处）。

表里的单位按 `round_model`（每单位体积 0.0040992）手算；§7.2 用运行时常量（0.0041）精确复算为 **0.75 / 1.58–3.57 / 8.20 / 12.52–14.52 / 17.81–21.48 / 44.23 / 89.66 / 245.99**，逐项差 ≤0.08 u，不改变任何结论。**注意 0.30 档**：起手直径 0.29974 比 0.30 小 0.00026 m，所以 0.30 m 的 22 件在开局是**被拒**的，吃下 1 件 1 单位件（+1 u）后即解锁——这是 (h) 生效后唯一一处「开局门」细节，实测时容易看成 bug。

### 2.4 尺寸门（决定 (h)，2026-11-15 已落地）

- **现在的行为**：`absorb ⇔ max(bounds) ≤ min(maxItemSizeWorldM, 语义直径 × absorbSizeRatio)`，`absorbSizeRatio = 1`（PROVISIONAL）、`maxItemSizeWorldM = 8`（只是安全上界）。合成在 `PickupAbsorbMath.TryComputeAbsorbSizeLimit`（`:221-238`），比较在 `:163-164`；场景实测 `StoryLevel.unity:12448-12449`。0.30 m 球的上限是 0.29974 m，0.90 m 暖壶要球径 ≥0.90 m（= 89.66 u）。
- **对本表与关卡 2**：§2.3 的阶梯现在是真实运行时约束，不是「落地后」。0.72 m 西瓜不再开局可吃（要 44.23 u），0.30 m 的 22 件要 0.01 u。复算见 §7.3——门槛**只强制顺序，不制造不可达件**（110/110 可达）。

## 3. 关卡 2：水果计数（同一块场地）

### 3.1 水果表（7 个 definition，40 件 / 112 单位）

`fruit` 的权威 id 集是 `Docs/Design/GameModes/categories.json:9-17` 的 7 个；全图 50 件的逐 id 件数是 8/8/10/6/8/6/4（`:19`）。本切片取 40 件（均不超过全图件数），留 3 星余量。

| id | 源 | 档 | bounds / 模型门 m | 值 | 件 | 小计 | kind |
|---|---|---|---:|---:|---:|---:|---|
| strawberry `[csv:10]` | K | s | 0.06 / 0.08 | 1 | 8 | 8 | **fruit** |
| grapes `[csv:11]` | K | s | 0.08 / 0.08 | 1 | 8 | 8 | **fruit** |
| apple `[csv:14]` | K | m | 0.30 / 0.32 | 4 | 6 | 24 | **fruit** |
| pear `[csv:15]` | K | m | 0.32 / 0.32 | 4 | 5 | 20 | **fruit** |
| orange `[csv:16]` | K | m | 0.30 / 0.32 | 4 | 6 | 24 | **fruit** |
| banana `[csv:17]` | K | m | 0.45 / 0.45 | 4 | 4 | 16 | **fruit** |
| watermelon `[csv:28]` | K | m | 0.72 / 0.72 | 4 | 3 | 12 | **fruit** |
| **合计** | | | | | **40** | **112** | |

★10/★★20/★★★30（`Docs/Design/GameModes/Levels/story_02_fruit_ten.json:35-59`）；40 件对 30 件是 **1.33×**、对 10 件是 4×。

### 3.2 非水果填充（70 件 / 300 单位）

| 组 | 内容（件数） | 件 | 单位 |
|---|---|---:|---:|
| nature/debris | seed 3、acorn 2、pebble 1、fallen_leaf 1、bottle_cap 1、paper_scrap 1 | 9 | 9 |
| food（非水果） | cookie 2、cheese_slice 1、croissant 5、bread_slice 5、sandwich 4 | 17 | 59 |
| tableware | cup 5、bowl 4、plate 4、spoon 4、napkin 5 | 22 | 88 |
| drink | soda_can 5、juice_carton 4、glass_bottle 1 | 10 | 40 |
| toy / garden / container | toy_ball 4；small_potted_plant 2、watering_can 2；thermos 4 | 12 | 104 |
| **合计** | | **70** | **300** |

300 单位约为西瓜门槛 44.21 u 的 **6.8×**（44.21 见 `Docs/Design/GameModes/README.md:304`），即使玩家不先吃水果也足够长大；同时把关卡 1 的总量补到 412 u。`cookie/cheese_slice` 是「食物但不是水果」的反例（同 `Docs/Design/GameModes/README.md:288` 的思路），kind 记为 `food`，绝不可写成 `fruit`。

### 3.3 kind 契约（决定计数是否成立）

- 控制器读 `quest.objective.category_id`，默认 `countGoalKind = "fruit"`，交给 `PickupFieldHost.GetAttachedCountForKind` 做**逐字大小写敏感**匹配（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryRoundController.cs:163-169`、`:689-700`；`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupFieldHost.cs:331-339`），所以 7 个水果定义的每一件 `kind` 必须**恰好是 `fruit`**；宿主按 kind 累计（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupFieldHost.cs:758-773`），不是按 definitionId。
- 若按 definitionId 写 kind（`apple`/`pear`…），`GetAttachedCountForKind("fruit")` 返回 **0**，3 星永远拿不到；此时要么统一改成 `fruit`（推荐，零代码改动），要么改控制器把 7 个 id 加起来（要改代码，不在本文范围）。
- 其余 kind（nature/debris/food/tableware/drink/toy/garden/container）都不得等于 `fruit`，且必须全部进 `motor.allowedKinds`，否则被 `RejectKindNotAllowed` 拒（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs:137-138`）。

### 3.4 关卡 2 的可达性与「成长」真实性

- 30 件 > 单簇 20 件：玩家必须换簇（与 `Docs/Design/GameModes/README.md:303` 的「30 > 单毯 23」同构）；3 星 30/40 = 75% 的水果，全图口径 50 件，本切片 40 件仍高于 30（`Docs/Design/GameModes/categories.json:19`）。
- **尺寸门现在是真约束（2026-11-15，(h) 已落地）**：0.72 m 西瓜要 44.23 u 才解锁，0.90 m 暖壶要 89.66 u（§7.3）。旧写法「0.30 m 球就能吃 0.72 m 西瓜」在 (h) 落地前成立，现在不成立。西瓜仍应放在第二簇（远离出生点）作为路线目标——不是为了尺寸门，而是为了让「换簇」这件事发生；不要用关卡 2 的成立来证明成长门有效（那是 §7.3 的事）。

## 4. 布局与落地

### 4.1 布局规则（20×20，出生 (0,−3)）

- **不越界**：所有中心 |x|,|z| ≤ 9.0（地面 ±10，墙内面约 ±9.8；`StoryLevel.unity:5243-5244`、`:2768-2769`）。
- **出生净空**：距出生点中心 ≥1.0 m 内不放任何道具/碰撞体；起步环从 1.5 m 起。
- **两簇水果**：A (−5.0, +4.0)、B (+5.0, +4.0)，各 r≈2.8 m、各 20 件（A：草莓4/葡萄4/苹果3/梨2/橙3/香蕉2/西瓜2；B：草莓4/葡萄4/苹果3/梨3/橙3/香蕉2/西瓜1）。30 件必须两簇都去。
- **三圈**：起步环绕出生点 r 1.5–3.0 放 12 件 s 非水果；中圈 r 3.5–6.5 放 24 件 m 非水果；外圈 r 6.5–9.0 放 30 件 m 非水果 + 4 件 thermos；西瓜在簇 A/B 的外侧。
- **间距**：s 件中心 ≥0.4 m、m ≥0.8 m、l ≥1.2 m，避免碰撞体互穿和「一点全吸」；外圈留一条 ≥2 m 宽的环道，球长大后仍能绕场。
- 不放大件、不设台阶：本切片运动层只支持平地 + ≤8 盒（`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:88`），台阶/坡道另案。

### 4.2 作者怎么把表变成场景（同步工具）

另一位 agent 正在写的编辑器同步工具（工作区里已有一版未提交的 `Assets/VRC_Katamari/Editor/Pickup/PickupFieldSync.cs`，下称同步工具）把场景 GameObject 同步成 `PickupFieldHost` 的并行数组。下面按它**当前**的约定写，免得表格与工具各说一套：

1. **命名 → itemId**：每件命名 `prop_<definitionId>_<NN>`（如 `prop_apple_03`）；工具把 GameObject 名直接当 itemId（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/PickupFieldSync.cs:30-31`），所以名字必须唯一。现有 `Item_00..Item_29` 和重复的 `prop-00` 都要在重排时消失（§0.1）。
2. **kind = 父文件夹名**：工具把场根下每个直接子文件夹名当 kind（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/PickupFieldSync.cs:32-33`），所以本表的 `kind` 列就是文件夹名（`fruit/`、`nature/`、`debris/`、`food/`、`tableware/`、`drink/`、`toy/`、`garden/`、`container/`）。工具当前的值表已有 nature/debris/food/tableware/drink/toy/garden/container 行（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/PickupFieldAuthoringRules.cs:85-98`），**但没有 `fruit` 行**；不加 `fruit s 1` / `fruit m 4` 两行，所有水果行会以 rejected row 丢掉，关卡 2 的水果数就是 0。这是本计划要求的唯一工具侧改动，不另做第二套数据；工具定型后以它为准。
3. **bounds → 档 → 值**：工具取子树所有 Renderer 的世界 AABB 全尺寸（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/PickupFieldSync.cs:34-35`），按最长边分档 s ≤0.21 / m ≤0.76 / l >0.76（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/PickupFieldAuthoringRules.cs:57-61`），再用 (kind,tier) 表给值（m 档 4、garden m 6、container l 16）。所以视觉要缩到 §2.1 的 `bounds` 列；`propBounds` 不写 lossyScale，也不写 `pickupDiameter`。
4. **整数**：`valueUnits` 必须 ≥1 的整数；0 或小数会被 `RejectNoGain` 拒，外部只看到 `noGain`（`Docs/OPEN_DECISIONS.md:57`；`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupFieldHost.cs:99`）。
5. **一条路径 + allowedKinds + 自检**：删掉 30 个 `PlaygroundProp`（或删宿主行），保留宿主；`allowedKinds` 改成 `fruit/nature/debris/food/tableware/drink/toy/garden/container` 全量（现只有 small/medium/large，`:1232-1235`）。同步后控制台应出现 `[PickupFieldHost] ready props=110 submittable=110`（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupFieldHost.cs:182-186`），且无 value 警告；再抽查 3 件的 bounds 与表一致。

### 4.3 验收前必须知道的限制

- 本文所有数量/单位是离线算术；Play Mode 的达标时刻、吸附手感、FPS 都 **NOT_RUN**（本 agent 不是 Unity 操作者）。
- 1 m 绝对门已不存在（(h) 2026-11-15 落地，§2.4）；§2.3 的「新可吃」列现在就是运行时行为，S5 要按它复跑并重记时间。两关共用一块场地仍是 PROVISIONAL；若 S4 需要 ★★★，按 §7.6 的 P1 重定切片阈值（而不是加内容到 800 u）；若两关内容互相干扰（例如水果被关卡 1 吃光后重试），就拆成两份布置/两个场景，而不是改数值硬凑。

## 5. 验证

```bash
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests
python3 Docs/Design/GameModes/validate_levels.py
```

**§7 的场景数据怎么复现（只读，不改场景）：**

```bash
S=VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/StoryLevel/StoryLevel.unity
grep -c 'PlaygroundProp' "$S"                       # 0：旧的第二条提交路径已不存在
grep -n 'targetDiameterM\|star2DiameterM\|star3DiameterM\|timeLimitSeconds' "$S"   # 1.25 / 1.45 / 1.6 / 120
grep -n 'maxItemSizeWorldM\|absorbSizeRatio\|baselineVolumeUnits\|cubicMetersPerVolumeUnit' "$S"  # 8 / 1 / 3.4390244 / 0.0041
```

`valueUnits` 是 Unity 的 `int[]`，序列化成十六进制串：**每 8 个 hex 字符 = 1 个 little-endian int32**（`01000000` = 1、`04000000` = 4、`10000000` = 16），110 个拼一行。`itemIds`/`kinds`/`propBounds` 是普通的 `- ` 列表；`propColliders` 的 fileID 经 Collider → `m_GameObject` → Transform `m_LocalPosition` 可回读每件的坐标（§7.3 的路线就是用这 110 个坐标算的）。

2026-11-15 基线：前两条 PASS（61 tasks；270 tests OK），第三条 `OK`（错误 0、警告 0）。本文不改 `tasks.json`/关卡 JSON/场景，三项应保持同样结果；本次新增 §7 后重跑结果见文末。**Unity 编译、EditMode 测试、Play Mode 复跑、Build & Test 均 NOT_RUN（本文作者不是 Unity 操作者）**；§7 引用的 1.43 m 是用户自己在 Play Mode 里的单次实测，不是本文跑的。

## 6. 假设与待定（全部 PROVISIONAL）

- 「两关共用一块 20×20 场地」与 110 件/412 单位的具体分配是本文选择，不是既有决定；已用 1.6×/65% 两条约束校验，但未试玩。`thermos` 用 primitive 占位（没有成品模型）；`small_potted_plant` 需把 Kenney `pot_small` 与 `flower_redA` 组装（`Docs/AssetIntake/Preview/README.md:32`）；水果视觉依赖 Kenney Food Kit 导入，未导入前用 primitive 占位，`bounds/value/kind` 不变，避免二次改数据。
- 绝对 1 m 门已不存在（(h) 2026-11-15 落地，§2.4）；当前口径是 `min(8, 语义直径 × 1.0)`，§2.3 的阶梯就是运行时行为。本文只保证内容不被安全上界误拒，并用 §7.3 证明顺序约束不制造死件。
- §7 的数值建议（★★ 1.40 / ★★★ 1.45）是**建议**，不是决定；它们需要 S4 试玩复核，且改的是 `StoryRoundController` 的序列化字段，不是关卡 JSON（§7.7）。

## 7. 星级数值复算与实测校准（2026-11-15）

### 7.1 数据来源与口径

| 量 | 值 | 来源 | 等级 |
|---|---|---|---|
| 场上件数 / 合计价值 | **110 件 / 412 u** | 宿主 `itemIds`/`valueUnits` 逐件解析（§5 命令） | **场景实测** |
| 逐件 `bounds` | 0.04–0.90 m 立方体 | 同上 `propBounds` | 场景实测 |
| 切片星级序列化值 | 目标 **1.25** m、★★ **1.45** m、★★★ **1.60** m、限时 **120 s** | `StoryLevel.unity:8901-8907`（`StoryRoundController`） | **场景实测（以场景为准）** |
| 一次 Play Mode 结果 | 结束 1.43 m、剩余 0 s | 用户 HUD（n=1） | **实测（单次）** |
| `baselineVolumeUnits` / `c` | 3.4390244 u / 0.0041 m³/u | `StoryLevel.unity:12442-12443` | 代码常量 |
| 起始语义直径 | 0.29974 m | 由上面两个常量复算 | 复算 |
| 吸收比例 `absorbSizeRatio` | 1.0 | 决定 (h)，PROVISIONAL | **暂定** |
| 球速 / 加速度 / 转向 | 4 m/s / 5 m/s² / 110°/s | `KatamariBallMotor.cs:76-80` | 代码默认，未实测手感 |

**文档与场景没有矛盾，别把它们读成两个目标。** 关卡的**目标**（★）就是 `targetDiameterM = 1.25`；1.45 是**第二档**、1.60 是第三档。HUD 打出的「目标 1.25 m」与本文 §0.4/§2.2、与 `story_01_bench_size.json` 的 ★ 档完全一致；前一轮分析里说的「★★ 目标 1.45 m」指的是第二档，不是关卡目标。**唯一过时的是本文 §0.5/§2.4 曾写「运行时仍是绝对 1 m 门」**——那条已在 2026-11-15 被 (h) 落地推翻，已就地更正。

### 7.2 精确复算：到各直径要多少「新增」单位

口径与 `SizeModelMath.TryVolumeUnitsForSemanticDiameter` 同源（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/SizeModel/SizeModelMath.cs:267`）：

```
total = baselineVolumeUnits + confirmedVolumeUnits
r     = (3 · total · c / 4π)^(1/3)          d = 2r
⇒ confirmed(d) = (π/6)·d³ / c − 3.4390244
```

以 **1.25 m** 走一遍（可拿计算器对）：
1. V = π/6 × 1.25³ = 0.5235988 × 1.953125 = **1.022654 m³**
2. total = 1.022654 ÷ 0.0041 = **249.43 u**
3. confirmed = 249.43 − 3.4390244 = **245.99 u**

| 目标直径 | V = πd³/6 (m³) | total = V/0.0041 | 新增 = total − 3.4390 | 占 412 |
|---:|---:|---:|---:|---:|
| 1.20 | 0.904779 | 220.68 | **217.24** | 52.7% |
| 1.25（★，现状） | 1.022654 | 249.43 | **245.99** | 59.7% |
| 1.30 | 1.150347 | 280.57 | **277.13** | 67.3% |
| 1.32 | 1.204260 | 293.72 | **290.28** | 70.5% |
| 1.35 | 1.288249 | 314.21 | **310.77** | 75.4% |
| 1.36 | 1.317090 | 321.24 | **317.80** | 77.1% |
| 1.40 | 1.436755 | 350.43 | **346.99** | 84.2% |
| 1.42 | 1.499215 | 365.66 | **362.22** | 87.9% |
| **1.43（实测终点）** | 1.531112 | 373.44 | **370.00** | **89.8%** |
| 1.45（★★，现状） | 1.596256 | 389.33 | **385.89** | 93.7% |
| 1.4817（全吃光 412 u） | 1.703300 | 415.44 | **412.00** | 100% |
| 1.60（★★★，现状） | 2.144661 | 523.09 | **519.65** | **126.1%** |

**与旧数字的差**：本文 §0.3/§0.4/§2.2 里的 245.91 / 385.78 / 519.5 是用 `round_model` 的 `(V_max−V_0)/1356 = 0.0040992` 算的，上表用运行时 `c = 0.0041`。逐项差 0.08–0.15 u（≤0.04%），不改变任何结论；**以后引用一律以本表为准**。

### 7.3 尺寸门的顺序：先小后大，实际能吃到多少

- **开局面**：起手 0.29974 m，能吃 `bounds ≤ 0.29974` 的件 = **38 件 / 68 u**（0.04–0.26 档）。0.30 档（22 件）差 0.01 u——吃 1 件 1 单位件就解锁。所以 0.30 m 的 22 件在开局是**被拒**的，这不是 bug。
- **顺序约束不制造死件**：按「当前可吃里挑价值最高」模拟（门槛每步重算），110/110 件全部可达，合计 412 u，终止球径 1.4817 m。
- **但顺序决定「吃到多少 u 时球多大」**（累计表；「吃完 ≤该尺寸的全部件」＝乐观上界，真人不会这么整齐）：

| 吃完 ≤ 该尺寸的全部件 | 累计件数 | 累计 u | 球径 |
|---:|---:|---:|---:|
| 0.26 | 38 | 68 | 0.8240 |
| 0.30 | 60 | 156 | 1.0768 |
| 0.34 | 78 | 228 | 1.2192 |
| 0.45 | 94 | 292 | 1.3226 |
| 0.72 | 106 | 348 | **1.4013** |
| 0.90（暖壶） | 110 | 412 | 1.4817 |

**关键一行**：把除 4 支暖壶外的 **106 件全吃光，球只有 1.4013 m**——比 ★★ 1.45 还低 4.9 cm。所以 1.45 以上**必须**动暖壶，而暖壶要 89.66 u 才解锁。这也是为什么 1.45 的门槛是「4 单位以上一件不能漏」。

### 7.4 实测校准：120 s 到 1.43 m 说明了什么

1.43 m ⇒ **370.00 u = 89.8%**（§7.2）。用它校准三件事：

1. **模型没算错，只是不能把下限当预期。** §7.5 的耗时是「不会走错、不会漏件」的下限：全清 110 件 ≈ **93 s**（路线 192.5 m ÷ 4 m/s + 110 件 × 0.41 s 转向）。实测 120 s 吃掉约 100 件（≈1.2 s/件），比下限 0.9 s/件慢 33%——正好是真人转向、找件、漏捡再回头的损耗。**93 s < 120 s**，所以「120 s 能到 1.43 m」与模型自洽，不需要改模型。
2. **★★ 1.45 是「紧」，不是「不可能」——前一轮的措辞要收回。** 实测差 **15.89 u ≈ 4 件四单位件**（或 1 支暖壶）。一次真人运行就摸到 1.43，说明这是「再优化一点就能拿」的门槛，不是结构上打不过。我在 §0.4 初稿写的「不许失误 / 不是关卡」说过头了，正确的说法是「余量只有 26.11 u，容错极低，但可达」。
3. **★★★ 1.60 仍然是硬不可能。** 全吃光 412 u 也只有 **1.4817 m**；1.60 要 519.65 u = 126.1%，任何一次运行都到不了。这条不受实测影响。

**真正的问题因此不是「打不过」，是星级带宽错位：**

| 档 | 直径 | 需要 u | 占 412 | 实测一次 120 s（370 u）的结果 |
|---|---:|---:|---:|---|
| ★ | 1.25 | 245.99 | 59.7% | 拿到 |
| ★★ | 1.45 | 385.89 | 93.7% | **差 4 件，没拿到** |
| ★★★ | 1.60 | 519.65 | 126.1% | 结构不可达 |

玩家吃掉了 89.8% 的场地、只拿 1 星；1 星在六成处、2 星在九成四、3 星在 126%。这不是难度曲线，是「1 星白给、2 星清场、3 星不存在」。**要改的是 ★★ 的位置和 ★★★ 的存在性，不是把 120 s 拉长。**

### 7.5 结论 (a)(b)(c)

**(a) 结构可达性**

| 档 | 现状 | 判定 |
|---|---|---|
| ★ 1.25 | 245.99 u / 59.7% | **可达且宽松**（余量 +166.01 u），实测已达成 |
| ★★ 1.45 | 385.89 u / 93.7% | **可达但极紧**：余量 +26.11 u，最少 84 件；实测差 15.89 u |
| ★★★ 1.60 | 519.65 u / 126.1% | **结构不可达**：上限 412 u / 1.4817 m |

**「1.25 m 是不是太松」：单看门槛不松，松的是带宽。** 1 星要吃掉 59.7% 的场地，这不是白给；问题是 ★ 与 ★★ 之间隔了 **34.0 个百分点**、中间没有任何档位，于是玩家体验成「1 星随便拿、2 星要清场、3 星不存在」。把 ★★ 挪到 1.40（84.2%）就是把这个洞填上——这也是 §7.6 P1 的唯一目的。

**(b) 比例与每件预算**（件数是「价值优先 + 门槛合法」的最少件数，路线是增量最省插入、从出生点 (0, −3) 起）

| 直径 | 需要 u | 占 412 | 最少件数 | 占件数 | 120 s 每件 | 180 s 每件 | 路线 | 估计耗时¹ |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1.20 | 217.24 | 52.7% | 41 | 37% | 2.93 s | 4.39 s | 86.5 m | 38 s |
| 1.25 ★ | 245.99 | 59.7% | 48 | 44% | 2.50 s | 3.75 s | 98.3 m | 44 s |
| 1.30 | 277.13 | 67.3% | 56 | 51% | 2.14 s | 3.21 s | 105.0 m | 49 s |
| 1.35 | 310.77 | 75.4% | 64 | 58% | 1.88 s | 2.81 s | 114.5 m | 55 s |
| 1.40 | 346.99 | 84.2% | 73 | 66% | 1.64 s | 2.47 s | 125.4 m | 61 s |
| 1.45 ★★ | 385.89 | 93.7% | 84 | 76% | 1.43 s | 2.14 s | 151.2 m | 72 s |
| 全吃光 | 412 | 100% | 110 | 100% | 1.09 s | 1.64 s | 192.5 m | 93 s |

¹ 估计耗时 = 路线 ÷ 4 m/s + 件数 × 0.41 s（平均 45° 转向、110°/s）。这是**下限**；实测那次 120 s 到 370 u，比 1.45 的下限 72 s 慢 67%，所以「下限能过」不等于「真能过」。180 s 一列只是对照：它不改变 26.11 u 的余量，只把每件预算从 1.43 s 抬到 2.14 s。

**(c) 建议** 见 §7.6。

### 7.6 建议改动（P1 最小，推荐）

**P1：只改切片自己的两个序列化阈值，不动 JSON、不动道具、不动时限。**

| 档 | 现状 | 建议 | 需要 u | 占 412 | 最少件数 | 余量 | 对照实测 370 u |
|---|---:|---:|---:|---:|---:|---:|---|
| ★ | 1.25 | **1.25（不变）** | 245.99 | 59.7% | 48 | +166.01 u | 已达成，约在 2/3 处 |
| ★★ | 1.45 | **1.40** | 346.99 | 84.2% | 73 | +65.01 u | **+23.01 u 余量 → 那次跑就拿到了** |
| ★★★ | 1.60 | **1.45** | 385.89 | 93.7% | 84 | +26.11 u | 差 15.89 u → 再吃 4 件即到 |

- **改动面**：`StoryRoundController` 的 `star2DiameterM` 1.45→1.40、`star3DiameterM` 1.6→1.45（`StoryLevel.unity:8901-8903`）+ 结果面板文案。**JSON 不动、道具不动、120 s 不动**。等级：**PROVISIONAL**，须 S4 试玩复核。
- **理由**：★★ 落在实测能力的 94%（370×0.94≈347）处，是真正「再努力一点」；★★★ 沿用旧的 ★★ 直径 1.45，保留「几乎清场」的仪式感，而且**第一次真的可达**（旧 1.60 是 −26.1%）。
- **副作用**：★→★★ 从 139.90 u（34.0 pp）缩到 101.00 u（24.5 pp，多吃 25 件），★★→★★★ 38.90 u（9.5 pp，再多吃 11 件）。★→★★ 仍是一次明显的额外投入；★★→★★★ 只差 11 件，适合当「最后冲刺」。

**为什么不把 ★★★ 定在 1.4817（全清上限）**：那是「必须吃光 412 u 且 4 支暖壶全中」，把上限当目标等于不可达；不建议。**也不建议 ★★★ 定 1.50+**：超过 1.4817 的任何数字都和 1.60 一样不可达。

**P2（加内容，保住 1.45/1.60）**：要 1.45 = 65%，总量需 385.89 ÷ 0.65 = **593.7 u**，即 **+181.7 u ≈ +45 件四单位件**（155 件 / 594 u）。届时 1.25/1.45/1.60 = **41.4% / 65.0% / 87.5%**，1.60 变成可达（全清 1.672 m）。代价：件数 +41%（0.39 件/m²，间距 1.6 m）、美术与摆放全部重做，且 ★ 掉到 41% 会变得太容易。**能解决，但不是「最小」。**

**P3（改时限）**：实测速率 370 ÷ 120 = **3.08 u/s**，按此全清 412 u 要 **134 s**。所以 120 s ≈ 90% 场地，与实测吻合；拉到 180 s 会有 **46 s 场地已空**（没东西可吃），不是「更从容」而是「空转」。若要靠时间让 1.45 稳，只需 **+15 s**（135 s ≈ 416 u），但那是在用秒表补 15.89 u 的洞——用户明确不要，本文同意。**时限不改。**

**P4（改分档价值）**：s/m/l 1/4/16 → 2/6/24，总量 596 u，1.25/1.45/1.60 = 41.3%/64.7%/87.2%。一处表 + 一次同步看着便宜，但这是**全局经济**（账本币值、1356 u 标定、其他地图与交分分数一起变）。不建议为一张切片动它。

### 7.7 这不是「改目标去凑」

§0.4 原文写「3 星另找图或加内容，**不要改目标去凑**」。那句针对的是**出货关卡**，本文同意并保留；P1 改的是**切片自己的序列化阈值**，两件事：

| 阈值所在 | 内容总量 | 1.25 | 1.45 | 1.60 |
|---|---:|---:|---:|---:|
| 出货地图 `picnic_lawn` | 1284 u | 19.2% | 30.1% | 40.5% |
| 本切片 | 412 u | 59.7% | 93.7% | 126.1% |

同一组数字在 1284 u 的图上是「随便就吃到」，在 412 u 的切片上是「吃光也不够」。这是**内容总量与阈值不匹配**，不是难度选择：切片的阈值是从 `story_01_bench_size.json` 抄来的（`StoryLevel.unity:8901-8903` 与 JSON 逐字段一致），而那份 JSON 是给 1284 u 的地图定的，切片只有 32% 的量。按切片自己的内容重算阈值属于修正单位错配；把**关卡 JSON** 的 1.45/1.60 改小才是「改目标去凑」，本文不做。

（若产品意图是「切片必须能验 3 星 @1.60」，那需要 800 u 以上 ≈ 200 件铺在 400 m² 上，或改用出货地图；不是调一个数字能解决的。）

### 7.8 哪些是算的、哪些是暂定的

- **算出来的（可复现）**：§7.2 整张表；§7.3 的 38 件/68 u、1.4013 m、1.4817 m；§7.4 的 370.00 u / 89.8% / 3.08 u/s / 134 s；§7.5 的最少件数、路线、耗时下限；§7.6 的 P2 593.7 u、P3 134 s。复现命令见 §5。
- **暂定的（PROVISIONAL，不是已定值）**：吸收比例 1.0（决定 (h)）；球速/转向 4 m/s、110°/s（代码默认，**未实测手感**）；0.41 s/件的转向折算；P1 的 1.40/1.45 是**本文建议值**，不是决定；「★★ 应落在实测能力的 94%」是设计判断，不是测量。
- **实测但只有 n=1**：1.43 m / 0 s 是一次运行，不是统计。它足以否掉「120 s 到不了 1.4 m」这类说法，不足以定「中位玩家能到多少」。
- **没做的**：Play Mode 复跑、吸附手感、FPS、多人公平——**NOT_RUN**（本 agent 不是 Unity 操作者）。

## 8. 本次改动的验证结果（2026-11-15）

```text
python3 Docs/tools/validate_plan.py                  -> PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
python3 -m unittest discover -s Docs/tests           -> Ran 280 tests ... OK
python3 Docs/Design/GameModes/validate_levels.py     -> 结论: OK（错误 0、警告 0）
```

- 三条都是**规划数据**校验，全绿；本文只改了 `Docs/Design/StoryMode/LEVEL_CONTENT_PLAN.md` 一个文件，没动 `tasks.json`、关卡 JSON、场景、`.cs`，所以结果应与基线一致。
- 测试数从 §5 记的 270 变成 280：本文只改 Markdown、不可能新增测试，所以这是仓库里其他改动带来的（不是本文的功劳，也不是回归）。
- **NOT_RUN**：Unity 编译、UdonSharp 编译、EditMode/PlayMode 测试、Build & Test、帧率、多人公平。§7 引用的 1.43 m 是用户自己在 Play Mode 里的单次观测（n=1），本文没有复跑，也不能把它当统计。
