# 剧情模式关卡内容计划（Labs/StoryLevel 切片）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。只做离线内容与算术，不改 Unity/运行时/美术；不声称 Play Mode 或 Build & Test 已验证。
> 范围：`Labs/StoryLevel` 的 20×20 m 平地切片，供 `story_01_bench_size`（1.25 m / 120 s）与 `story_02_fruit_ten`（fruit ×10）共用。共用是本文的 PROVISIONAL 选择（§0.2），所有数值与摆放都是占位数据。
> 引用约定：`路径:行` 相对仓库根；`StoryLevel.unity` = `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/StoryLevel/StoryLevel.unity`；`[csv:N]` = `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 第 N 行；`PickupFieldSync.cs`/`PickupFieldAuthoringRules.cs` = `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/` 下同名文件。

## 0. 结论

0.1 **现有 30 个占位方块不是「30×1」。** 场地同时有两条提交路径：`PickupFieldHost` 的 30 行数组（`itemIds` = `Item_00..Item_29` 唯一、`kinds` = small×10/medium×10/large×10、`propBounds` = 0.12/0.18/0.25、`valueUnits` 30 个 1，合计 30 单位；`StoryLevel.unity:7505`、`:7536`、`:7567`、`:7598`、`:7629`），以及每件上还挂着的 `PlaygroundProp`（`itemId` 全是 `prop-00`、`kind: small`、`semanticDiameterM: 0.2`、`valueUnits: 3`，共 30 个；`:283-288`）。后者重复的 `prop-00` 会让电机把后来者当成「已排队」直接返回（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs:1209-1215`）。**重排前必须只留一条路径**：保留宿主、删掉 30 个 `PlaygroundProp`（推荐）；否则「吃到多少单位」在 30 与 90 之间不确定，S5 的达标记录不可信。

0.2 **本计划把场地重排成 110 件 / 412 单位，两关共用。** 关卡 1 验 1.25 m；关卡 2 只数其中 40 件 `kind=fruit`。这与两关共用 `picnic_lawn` 同构（`Docs/Design/GameModes/README.md:169`）。

0.3 **1.25 m 可达且有余量。** 1.25 m = **245.91 单位**（`Docs/Design/GameModes/README.md:212`；公式 `d=d0·(V/V0)^(1/3)`、1356 单位 = 0.30→2.20 m，`Docs/Design/Gameplay/README.md:56`、`Docs/Design/Gameplay/round_model.py:110-115`）。412/245.91 = **1.68×**（计划要求 ≥1.6×，`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:94`）；达标只吃掉 245.91/412 = **59.7%**（S5 要求 ≤65%，`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:132`）。

0.4 **切片只能验 ★ 和勉强 ★★，验不了 ★★★。** 1.45 m = 385.78 u = 全部内容的 **93.6%**；1.60 m = 519.5 u **> 412**，结构性不可达（星级与 519.5 见 `Docs/Design/GameModes/Levels/story_01_bench_size.json:34-56`、`:55`；385.78 见 `Docs/Design/GameModes/README.md:213`）。S4 的「0/1/2/3 星各触发一次」不能全靠本切片；3 星另找图或加内容，**不要改目标去凑**。

0.5 **运行时仍是绝对 1 m 门。** 决定 (h) 要求按「物件尺寸 ≤ 球径×1.0」相对判定（`Docs/OPEN_DECISIONS.md:39`），代码却仍用 `largestExtent > maxItemSizeWorldM`，默认 1 m（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs:113`、`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs:140-141`）。后果：**0.30 m 球现在就能吃 0.90 m 暖壶**，§2.3 的阶梯在 (h) 落地前不生效；>1 m 的长凳仍被拒（`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:64`）。本切片所有件 ≤0.90 m，正是为了不被 1 m 门误挡；但**不要用本切片证明尺寸门有效**。

## 1. 现在有什么、许可状态

### 1.1 场地与现有占位

- 地面 20×20 m、顶面 y=0（`StoryLevel.unity:5243-5244`）；四墙内表面约 ±9.8 m（南墙 `:2768-2769`）；出生点 (0, 0.5, −3)（`:5942`）。
- 宿主 30 行值合计 **30 单位**；逐件 `PlaygroundProp` 3 单位是另一条路径（§0.1）。现有摆放有 z=13 的件（`:1460`），已在 20×20 地面之外，重排时必须收回 |x|,|z| ≤ 9.0。`allowedKinds` 只有 small/medium/large（`:1232-1235`），改成 §2.1 的 kind 后必须同步，否则 `RejectKindNotAllowed`。

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
2. **件数与上限**：按 412/110 ≈ 3.7 u/件，245.91 u 约需 **66 件**；S5 的「≤80 s、≤65%」要在 Play Mode 实测，本文不预支（`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md:132`）。★★ 1.45 = 385.78 u = 93.6%，要吃光几乎全场；★★★ 1.60 = 519.5 u > 412，不可达（§0.4）。本切片只承诺 ★；★★ 是「几乎清场」的附加挑战。

### 2.3 成长路径（(h) 生效时的「新可吃」，按 bounds；单位由 §0.3 公式算得）

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

### 2.4 绝对 1 m 门 vs 决定 (h)

- **门在哪**：相对规则在 `Docs/Design/Gameplay/round_model.py:119` / `:493-502`；运行时在 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs:140-141`，`maxItemSizeWorldM` 默认 1 m（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs:113`）。今天两者不一致（`Docs/OPEN_DECISIONS.md:39`「实现状态：代码未改」）。
- **对本表与关卡 2**：0.30 m 球现在能一路吃到 0.90 m 暖壶（所有件 ≤1 m 都过绝对门），§2.3 的阶梯只是数据设计，不是运行时保证；0.72 m 西瓜同样开局就能吃（§3.4）。S5 若在 (h) 落地前跑，达标会偏快，记录时必须写明代码版本。**不要**为了「让门生效」把某件调到 >1 m（会被 `RejectItemTooLarge` 直接拒，`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs:141`），也不要用本切片代替 (h) 的验收。

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
- **但在 (h) 落地前，0.30 m 球就能吃 0.72 m 西瓜**，「长大才能吃大果」不成立；西瓜应放在第二簇（远离出生点）作为路线目标，而不是尺寸门。不要用关卡 2 的成立来证明成长门有效。

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
- 1 m 绝对门未改（§2.4）；(h) 落地后本表「新可吃」列才成为运行时行为，届时要复跑 S5 并重记时间。两关共用一块场地是 PROVISIONAL；若 S4 需要 ★★★，或两关内容互相干扰（例如水果被关卡 1 吃光后重试），就拆成两份布置/两个场景，而不是改数值硬凑。

## 5. 验证

```bash
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests
python3 Docs/Design/GameModes/validate_levels.py
```

2026-11-15 基线：前两条 PASS（61 tasks；270 tests OK），第三条 `OK`（错误 0、警告 0）。本文不改 `tasks.json`/关卡 JSON/地图，三项应保持同样结果；Unity 编译、Play Mode、Build & Test 均 **NOT_RUN**。

## 6. 假设与待定（全部 PROVISIONAL）

- 「两关共用一块 20×20 场地」与 110 件/412 单位的具体分配是本文选择，不是既有决定；已用 1.6×/65% 两条约束校验，但未试玩。`thermos` 用 primitive 占位（没有成品模型）；`small_potted_plant` 需把 Kenney `pot_small` 与 `flower_redA` 组装（`Docs/AssetIntake/Preview/README.md:32`）；水果视觉依赖 Kenney Food Kit 导入，未导入前用 primitive 占位，`bounds/value/kind` 不变，避免二次改数据。
- 绝对 1 m 门与 (h) 的差异是当前代码事实（`Docs/OPEN_DECISIONS.md:39`），不是本文能解决的；本文只保证内容不被 1 m 门误拒，并把成长阶梯写成 (h) 生效后的目标。
