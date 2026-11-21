# 第一张「区域地图」计划：野餐一角（PicnicCorner）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。只读调查 + 计划；作者不是 Unity 操作者，没有编译、Play Mode 或 Build & Test。所有米数、件数、单位都是**本项目暂定值**，不是原作数值。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；其余路径相对仓库根。设计原则引 [KatamariMaps 调研](<../../Research/KatamariMaps/README.md>) 的节号（§x）与原则号（P1–P10）；没有来源的写「本项目选择」。
> 目标（项目所有者）：用**已有、授权可用**的素材做**一张**连通区域——大多数物件可卷、少量固定布景——按原作的关卡做法布局，但不用原作任何资产（调研开篇的版权条款）。

## 0. 结论

1. **先做一张新的单人区域图「野餐一角」**（24×24 m，平地，SW 角起步，向 NE 逐级外扩），沿用野餐草坪的主题与物件 id，不扩建 PicnicLawn，也不另起「时代」主题（理由 §1）。
2. **最大的阻塞不是素材，是尺寸门**：运行时今天只有绝对 1 m 门（`Runtime/Pickup/PickupAbsorbMath.cs:140-141`、`Runtime/BallMotor/KatamariBallMotor.cs:113`），0.30 m 的球能吃下任何 ≤1 m 的东西、永远吃不下 1.25 m 的长凳。决定 (h) 已拍板但代码没落地（`Docs/OPEN_DECISIONS.md:39`）。不落地，同心尺寸带就不存在（§4 G1）。
3. **布局按运行时上限反推**：地面 1 平面 + 边界 4 平面（共 5/8），场内固定布景与门共 8 个盒子（8/8），没有坡和台阶；约 175 件可卷 / 768 占位单位，固定碰撞物只有 8 个（§2）。
4. **素材够做灰盒与第一版**：8 件自建小道具已在 Unity（`Art/Props/*`），Kenney Food 2.0 / Nature 2.1 已有包级 CC0 证据（`Docs/AssetIntake/DirectDownload/manifest.json`）但**尚未通过最终人工验收**；野餐篮、保温箱、长凳、卷毯必须自建（§3）。
5. **构建走数据流水线**：`layout.json` + `prop_catalog.csv` → 校验器 → 带稳定 id 的生成载荷 → 幂等的编辑器同步工具 → 场景，最后接剧情第一关（§5）。不写一次性 builder。

## 1. 先做哪块区域

| 候选 | 判定 | 依据 |
|---|---|---|
| A. 扩建 PicnicLawn（例如剧情第一关切的西侧 6 区） | **不先做** | 全图 38 个障碍面（`Docs/Design/Maps/PicnicLawn/README.md:74`）远超 8 平面 + 8 盒（`Runtime/BallMotor/KatamariBallMotor.cs:235-236`）；西侧就有毯缘坡、0.14 m 树根丘、0.26 m 花圃台（同文件 `:337-339`），运动层只有一张解析地面（`KatamariBallMotor.cs:86-89`，`Docs/02_TECHNICAL_CONTRACTS.md:54`）；它是四人对称 PvP 图，改几何会动 PvP 设计；Unity 侧是从零重建的生成器。 |
| B. 新开「时代」主题区（对应 2025 作的时间旅行轴，§1.1、§1.3） | **不先做** | 仓库里没有时间旅行剧情的任何设计决定（只有调研）；没有任何时代主题素材，全部要新找或新建。主题可以以后只靠国王委托文案带出，不影响几何（本项目选择）。 |
| **C. 新单人区域「野餐一角」，复用野餐物件 id** | **推荐** | 素材面与野餐目录重合（`Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 的 seed/acorn/pebble/bottle_cap/paper_scrap/toy_ball/napkin/watering_can 已建模并导入）；能在现有运行时上限内设计；剧情第一关的规则（1.25 m / 120 s，见 `Docs/Design/StoryMode/ROUND_RULES.md`）与地图无关，可直接改挂到新图；做完的经验（门、密度、扫描成本）原样回流 PicnicLawn（T21）。 |

C 不是 20×20 m 的平地切片，也不替代 [关卡内容计划](<../StoryMode/LEVEL_CONTENT_PLAN.md>) 给切片排的 110 件 / 412 单位：切片是无门、无地标的平场，且 ★★★ 1.60 m（519.5 单位）在切片里结构性不可达（该文 §0.4）。本图是它的下一步：有尺寸带、门、地标，★★★ 可达（§2.2）。切片里的接线（输入、骑球、宿主、回合）与命名/kind 约定（该文 §4.2）全部沿用。

## 2. 区域布局

### 2.1 骨架（P1、P10）

- **同心尺寸带，从角落向外**：起步口袋 → 野餐毯 → 草地 → 外缘与营地，四带对应 PicnicLawn 的 t0–t3（0.30–0.55 / 0.55–0.95 / 0.95–1.45 / 1.45–2.20 m，`Docs/Design/Maps/PicnicLawn/README.md` §2）。依据：制作人「何cm帯の大きさで巻き込めるのはこのルート」（§3，S-M10）；原作按尺寸带切图（§5，S-M28）。
- **起步是小而封闭的口袋，再外扩**：对应海盗船关从船尾桌面起步（§1.5，S-M09）。本图没有高差，口袋用两段矮卷毯墙 + 两条边界围出。
- **一张图、多关各切一刀**（P10，§2.1 Make a Star 1 只开客厅）：剧情第一关只需口袋 + 毯 + 草地，第二关（水果）用全图。地图数据不随关卡复制。
- **坐标**：与 PicnicLawn 同约定（Unity 左手系、+Z 北、原点在中心地面），边界 x、z ∈ [−12, 12]，地面 y = 0；出生点约 (−10, 0, −10)，朝 45°（看向营地）。

### 2.2 区与数量（全部暂定，R2 写进 `layout.json` 后以校验器结果为准）

| 区 id | 带 | 矩形 x0,x1,z0,z1 | m² | 可卷件（s/m/l） | 占位单位 | 件/m² | 间距 S | 说明 |
|---|---|---|---|---|---|---|---|---|
| `pocket` | t0 | −12,−7,−12,−7 | 25 | 30（30/0/0） | 30 | 1.20 | 0.45 | 草籽、橡子、石子、瓶盖、纸屑、饼干；30 件取 Make a Star 1「一关 30 件」的量级作类比（§2.1，S-M15），不是复刻 |
| `blanket_w` + `blanket_s` | t1 | −12,−1,−7,−1 / −7,−1,−12,−7 | 96 | 70（30/40/0） | 190 | 0.73 | 0.72 | 杯盘碗、水果、面包、餐巾、皮球；全图最密，对应 PicnicLawn 毯区 0.72 m 间距的先例 |
| `lawn_n` + `lawn_e` | t2 | −12,5,−1,5 / −1,5,−12,−1 | 168 | 52（20/20/12） | 312 | 0.31 | 1.30 | 1.0–1.4 m 的花盆、水壶、倒木、小凳；S 由最大两件 (1.4+1.2)/2 决定 |
| `edge_n` + `edge_e` | t3 | −12,5,5,12 / 5,12,−12,5 | 238 | 16（10/0/6） | 130 | 0.07 | 1.60 | 1.45–1.6 m 大件 + 10 件补给碎屑；回营地后折返的路线奖励（P9） |
| `camp` | t2–t3 | 5,12,5,12 | 49 | 4（0/0/4） | 80 | 0.08 | 1.50 | 高价值区，在门后（P5） |
| 3 个门（§2.4） | — | — | — | 3 | 26 | — | — | 门本身是可卷物 |
| **合计** | | | **576** | **175** | **768** | 0.30 | | 固定碰撞物 8（§2.3），另有不碰撞的装饰 |

- **单位从哪来**：沿用 PicnicLawn 的占位权重 s=1、m=4/5、l=16/20（同 README §12），全部是 ≥1 的整数，满足决定 (j)（`Docs/OPEN_DECISIONS.md:57`）。场景里真正写进宿主的值来自同步工具的 (kind, 档) 值表（`Editor/Pickup/PickupFieldSync.cs:39-41`），所以目录的占位值必须与那张表一致，由 R1 的校验器对账。
- **尺寸节点**（按电机标定 `KatamariBallMotor.cs:95-106`：0.30 m = 3.439 单位、体积线性，即 `u(D) ≈ 3.439 × ((D/0.30)³ − 1)`）：0.55 m ≈ 18、0.95 m ≈ 106、1.25 m ≈ 246、1.45 m ≈ 385、1.60 m ≈ 518 单位。
  - 口袋 30 单位 → 约 0.64 m：t1 打开时口袋已吃完，余量 1.7×。
  - 口袋 + 毯 220 单位 → 约 1.21 m；再吃草地的 s/m 就过 1.25 m（剧情第一关 ★ = 全图 32%，满足切片计划「达标 ≤ 总单位 65%」）。
  - ★★ 1.45 m = 全图 50%，★★★ 1.60 m = 67%；吃满 768 单位约 1.82 m，到不了 2.20 m 封顶（本项目选择：区域图留成长余地给后面的关）。
- **密度按区核算**（P3）：上表是每区件数 ÷ 每区面积；原作只公开「单区 145 件 / 一关 30 件 / 校舍 63 件」，没有面积（§7 第 7 条），所以这里的每 m² 数是本项目自定，必须在 R8 灰盒实测。
- **可卷比例**（P6）：175 件可卷 vs 8 个碰撞固定物（≈96%）。原作接近全可卷（§2.4，S-M16）；我们只把边界、地标、门框留成固定物，理由是电机的 8 盒上限，不是美术。

### 2.3 固定布景与障碍账本（运行时预算即设计约束）

| 槽 | 东西 | 约尺寸 m | 位置意图 | 作用 |
|---|---|---|---|---|
| 平面 1 | 地面 y=0 | — | 全图 | 剧情 lab 也是这样注册（`Labs/StoryLevel/StoryLevelLabBootstrap.cs:117`） |
| 平面 2–5 | 四条边界 x/z = ±12 | — | 边界 | 用平面不用盒，省下 4 个盒；视觉上配 Kenney 栅栏/灌木，让墙「有理由」（P7） |
| 盒 F1 | 巨型野餐篮 | 2.4×1.6×1.8 | 营地西侧 | 主地标 + 营地墙；出生点望向它（P4） |
| 盒 F2 | 保温箱 | 3.0×1.2×1.1 | 营地南侧 | 营地墙；与 F1 之间留营地南口 |
| 盒 F3 | 大树桩 | 2.4×2.4×1.2 | 草地东南，偏离出生视线 | 草地中段的次地标，把草地切成两条环线 |
| 盒 F4、F5 | 卷起的毯边 | 4.0×0.6×0.6 各一 | 口袋东侧、北侧 | 围出口袋；只 0.6 m 高，口袋里也能越过它看到 F1（P4） |
| 盒 G1–G3 | 三个尺寸门的挡路盒 | 见 §2.4 | 见 §2.4 | 被吸走后从电机里撤掉（§4 G4） |
| 不碰撞 | 毯面贴图（`ground_cloth_tile`）、边界外巨树与帐篷、花丛贴片 | — | 地面 / 边界外 | 只渲染；边界外地标由边界平面挡住，不占盒 |

合计 5 平面（余 3）、8 盒（余 0）。**任何新增碰撞物都要先拿掉一个**，或等 §4 G3 扩容。

### 2.4 咽喉、尺寸门与路线（P5）

| 门 / 咽喉 | 位置意图 | 净宽 | 挡路物（可卷） | 吸走所需球径 | 打开什么 | 绕行 |
|---|---|---|---|---|---|---|
| C1 口袋东口 | F4 与 F5 之间 | 1.6 m | 无（常开） | — | 口袋 → 毯南段 | — |
| G1 口袋北口 | F5 与西边界之间 | 1.6 m | 横倒的擀面杖（1.6 m 长） | 0.55 m | 口袋 → 毯西段近道 | 走 C1，+4 m |
| G2 草地东南缺口 | F3 与南边界之间 | 3.0 m | 倒木（3.0 m 长） | 0.95 m | 草地 → 东外缘近道 | 绕 F3 北侧，+8 m |
| G3 营地西口 | F1 南端与 F2 之间 | 3.0 m | 横倒的长凳（3.2 m 长） | 1.25 m | 草地 → 营地近道 | 经 G2 与东外缘进营地南口，+14 m |

- **门是「长而细」的物件**：挡住整个口子，但拾取尺寸按粗细声明（宿主行的 `propBounds` 写拾取尺寸，挡路盒写真实长度）。这是 PicnicLawn 3.6 m 长凳在 1.25 m 开门的同一做法（`Docs/Design/Maps/PicnicLawn/README.md` §9）。决定 (h) 提醒过「AABB 全长」与「拾取尺寸」别混（`Docs/OPEN_DECISIONS.md:39`），所以这两列在目录里必须分开写（§4 G1）。注意同步工具今天按 Renderer 世界 AABB 取 `propBounds`（`Editor/Pickup/PickupFieldSync.cs:34-35`），长凳会被算成 3.2 m，必须先补 §4 G13。
- **口子宽度**：G2/G3 3.0 m ≥ 最大球 2.20 m + 0.8 m 余量；口袋两口 1.6 m 只给口袋里的球（最大约 0.64 m），按校验器规则标 `narrow_small_ball_lane` 且不得落在散布区内（`Tools/mapbuild/README.md:99-101`）。大球不需要回口袋。
- **每个门都有绕行、每个区至少两条出路**（`Docs/03_MAPS_ASSETS.md:44-47`）；门只挡近道，不挡唯一出路。G3 门就是剧情第一关文案里「吞下长凳」的那一张（`story_01_bench_size.json` 的 `quest.text_zh`）。
- **路线**：外环沿边界一圈（净宽 ≥3.0 m）；内环绕 F3；口袋 → 毯 → 草地是主路，全部 0% 坡（P8 的台阶类本版不做，§4 G5）。
- **视线**（P4）：出生点 → F1 的对角线（约 26 m）上不放任何 >0.6 m 的固定物；F3 放在对角线外。边界外巨树比 F1 高，任何位置都能定向。校验器查不了视线，R8 用截图人工验。
- **回访兑现**（P9；问题本身见 §3 的事后剖析 S-M21）：草地的 1.0–1.4 m 件是 t1 球路过时「看得到吃不到」的；从营地南口出来沿东/北外缘折返时，外缘的 1.45–1.6 m 件只有大球能吃，回程本身就是奖励。

## 3. 素材来源

**可进场景的条件**：有记录在案的兼容授权，且通过人工验收（`Docs/03_MAPS_ASSETS.md:68-79`）。CC-BY 需要世界内致谢牌；本表目前没有 CC-BY 素材。自建素材「未指定授权」（`Docs/Art/OriginalModels/props/README.md:3`），按仓库规则不替它指定（AGENTS「Licensing is undecided」）。

| 物件类 | 用什么 | 授权与状态 | 还缺什么 |
|---|---|---|---|
| 碎屑 s：草籽、橡子、石子、瓶盖、纸屑 | 自建 `seed/acorn/pebble/bottle_cap/paper_scrap` | 原创；**已在 Unity**（`Art/Props/*`） | 价值行、宿主行（R7） |
| 餐巾、小皮球、洒水壶 | 自建 `napkin/toy_ball/watering_can` | 原创；已在 Unity | 同上 |
| 水果：苹果、梨、橙子、香蕉、草莓、葡萄、西瓜 | Kenney Food 2.0（`apple/pear/orange/banana/strawberry/grapes/watermelon`） | 包内 License.txt = CC0-1.0、署名非必需（`Docs/AssetIntake/DirectDownload/manifest.json`）；**未过最终验收**（`Docs/AssetIntake/license_gaps.md:26-28`） | Lead 逐文件验收清单；走 T20 导入（Prefab、简化碰撞、id） |
| 餐具：盘、杯、马克杯、碗、叉勺 | Kenney Food（`plate/cup/mug/bowl/utensil-*`）；备选自建 `item_plate/item_cup`（只在 `Docs/Art/OriginalModels/`） | 同上 / 原创 | 同上 |
| 面包、三明治、牛角包、饼干、奶酪、汽水罐 | Kenney Food（`bread/sandwich/croissant/cookie/cheese-cut/soda-can`） | 同上；面包是片、奶酪是片、汽水罐只验过一个视角无文字（`Docs/AssetIntake/Preview/README.md:22-35`） | 同上；罐身全角度商标检查 |
| 草地 l 件：花盆、蘑菇丛、倒木、小石块 | Kenney Nature 2.1（`pot_large/mushroom_redGroup/log/rock_small*`） | 包级 CC0（同 manifest）；未过最终验收 | 同上 |
| 花丛（s/m 可卷） | Kenney Nature `flower_*` | 同上 | 同上 |
| 门 G1 擀面杖 / G2 倒木 | Kenney Food `rollingPin` / Nature `log` | 同上 | 按 1.6 m / 3.0 m 缩放后的拾取尺寸表 |
| 门 G3 长凳、小凳、保温壶、外缘大件 | **自建**（Furniture Kit 只有页面声明，没下载，`license_gaps.md:8`；PicnicLawn 也判为 NOT_FOUND，`Docs/Design/Maps/PicnicLawn/README.md:428`） | 原创 | Blender 简单道具，按 `Docs/Art/OriginalModels/props/scripts/models/*.py` 同一管线新增 |
| F1 巨型野餐篮、F2 保温箱、F4/F5 卷毯 | **自建** | 原创 | 同上（盒/圆柱基础体即可） |
| F3 大树桩 | Kenney Nature `stump_round` | 包级 CC0 | 验收 |
| 毯面 | 自建 `ground_cloth_tile`（`Docs/Art/OriginalModels/`） | 原创；未导入 Unity | 导入 |
| 边界栅栏/灌木、边界外巨树、帐篷 | Kenney Nature `fence_simple/plant_bushLarge/tree_oak/tent_detailedOpen` | 包级 CC0 | 验收；Kenney 署名非必需，但建议仍放一块致谢牌（本项目选择，为以后的 CC-BY 素材留位置） |

需新建的 Blender 道具：长凳、小凳、保温壶、野餐篮（巨型 + 可卷小号）、保温箱、卷毯，共 6 类，全部基础体拼接、单色材质、≤600 三角面（沿用 props 批的预算，`Docs/Art/OriginalModels/props/README.md:20`）。

## 4. 阻塞本图的工程缺口

| # | 缺口（代码事实） | 最小修法 | 挡第一版？ |
|---|---|---|---|
| G1 | **尺寸门是绝对 1 m**：只比 `largestExtent > maxItemSizeWorldM`（`Runtime/Pickup/PickupAbsorbMath.cs:140-141`），默认 1 m（`KatamariBallMotor.cs:113`）；决定 (h) 未落地（`Docs/OPEN_DECISIONS.md:39`） | 上限改成 `min(maxItemSizeWorldM, 当前直径 × 1.0)`，安全上界提到 2.2 m；EditMode 测试覆盖 0.30/0.64/1.25 m 三档；门物件提交的是声明的拾取尺寸 | **挡**：不修就没有尺寸带，G3 永远开不了 |
| G2 | **太大的候选会占死队列**：队列 4 格，电机里被拒的候选不会被丢 | 已落地：`ScanProps` 每次读当前直径，只让过得了尺寸门（`IsBeyondAbsorbLimit`）的最近 4 件参与候选选择，并撤回不在选择里的（`PickupCandidateSelection`） | 已解决 |
| G3 | 障碍上限 8 平面 + 8 盒，超出静默丢弃并 latch（`KatamariBallMotor.cs:235-236`、`:1289-1321`），地面占 1 个平面 | 第一版按 §2.3 账本设计在上限内；校验器加「碰撞物 ≤ 上限」检查（G10）。扩容或空间分块留到 PicnicLawn | 不挡（已绕开） |
| G4 | **门吃掉后挡路盒不会消失**：电机只有 `ClearObstacles()`（`:1323-1328`），没有删单个；宿主也没有「某 id 是否已附着」的读数（公开方法只有总数/按 kind 计数，`PickupFieldHost.cs:322-331`） | 宿主加 `IsAttached(itemId)` getter；新 `Runtime/Maps/MapObstacleHost.cs`（独占物体）持有平面/盒列表和门 id，门附着后 `ClearObstacles()` 再重注册其余；`ResetField`（`:276`）重试时全部重注册 | **挡**（门是本图的咽喉） |
| G5 | 没有坡与台阶：一张解析地面（`KatamariBallMotor.cs:86-89`），坡道/台阶「还需实现和测试」（`Docs/02_TECHNICAL_CONTRACTS.md:54`） | 第一版全平；毯面等贴地件 ≤0.02 m（低于 `max_route_step_m` 0.05，`Tools/mapbuild/validate_map.py:107`）；不用 `climb` 门 | 不挡（已绕开）；P8 延后 |
| G6 | 吃不动的件是障碍、吃得动的不是：`PickupFieldHost` 每次扫描把当前吃不动的近处道具登记为挡路 OBB（最近优先，一个块 12 槽），吃得动的不挡；没有原作的弹开削球（§1.4，S-M09） | 布局仍按尺寸带摆放：l 件放在主路两侧成簇，不放在小球必经线上 | 不挡；R8 记录观感 |
| G7 | 扫描成本 O(N)：每 0.05 s 遍历全部道具并读 `Transform.position`（`PickupFieldHost.cs:101`、`:818-842`），Udon 下的耗时**没测过** | 道具在被吸前不动，`Start` 时缓存世界坐标数组；本图 175 件一个宿主；R8 在 Build & Test 里记帧时间 | 不挡；>~250 件前必须先测（本项目选择的门槛） |
| G8 | 激活半径固定 1.6 m（`PickupFieldHost.cs:102`），而接触距离 = 碰撞半径 + 物件包围半径；1.6 m 球吃 1.5 m 件需要约 2.1 m | 每件激活半径 = 当前碰撞半径 + 该件包围半径 + 0.3 m | 不挡 t0–t2；挡 t3 外缘的手感 |
| G9 | 附着物大了以后：只保留局部位姿与自身 scale（`Runtime/BallLayers/Attachment/AttachmentMath.cs:24-31`），球芯长大会把早期附着物埋进球里，渲染成本还在；大件附着后伸出球外、会穿墙 | 宿主在长大时把「局部距离 + 自身半径 < 当前核心半径 − 余量」的附着物 `SetActive(false)`；穿墙第一版接受 | 不挡 |
| G10 | 校验器按 PvP 图写：必填 `bank`/`player_count_plans`/`respawn`（`validate_map.py:111-116`、`:1129-1143`）；不查运行时上限 | layout 加 `map_kind: "story_region"`，此时这些键可缺、出生→交分路程不算；**保留**每出生点 2 出路（`:1179`）；新增第 14 组「运行时预算」：平面 ≤8（含地面）、盒 ≤8、可卷拾取尺寸 ≤ 安全上界、价值为 ≥1 的整数、实例数 ≤ 宿主预算 | **挡数据步骤**（R1） |
| G11 | 摆放位置由 Unity 侧决定：生成器只给每区件数，Unity builder 用种子随机找点（`Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs:829-839`），实例没有稳定 id，同步工具无从对账 | 把种子摆放挪到 Python 生成器（复用 `Tools/mapbuild/build_greybox.py` 的 `scatter_feasibility` 模型），载荷逐件给 id + 坐标；Unity 只照抄 | **挡同步步骤**（R3） |
| G12 | 剧情第一关的回合参数在场景序列化字段与 `hub_levels.json`，不绑定具体地图 | 关卡按 `hub_levels.json` 的条目挂接 | 不挡剧情关接入（R9） |
| G13 | 同步工具（未提交，另一 agent 在写）的 bounds 只来自 Renderer AABB、档位按最长边（`Editor/Pickup/PickupFieldSync.cs:34-41`），没有「拾取尺寸 ≠ 外形」的通道 | 给少数门物件一个显式的拾取尺寸来源（例如目录/载荷里的覆盖值，或一个只放尺寸的子物体约定），其余行照旧 | **挡**三个门（只影响 3 行） |

## 5. 构建路线（每步独立可验；不写一次性 builder）

| 步 | 做什么 | 谁 | 验收 |
|---|---|---|---|
| R0 | Lead 拍板：选 C；(h) 的 1.0 比例按「声明的拾取尺寸」实现；Kenney 首批逐文件验收清单（约 30 个文件） | Lead | `Docs/OPEN_DECISIONS.md` / intake 记录更新 |
| R1 | 校验器加 `story_region` 与第 14 组运行时预算（G10），配负例测试 | 非 Unity agent | `python3 -m unittest discover -s Tools/mapbuild/tests`；PicnicLawn、ToyRoom 结果不变 |
| R2 | 写 `Docs/Design/Maps/PicnicCorner/{layout.json, prop_catalog.csv, README.md}`：§2 的区、门、咽喉、路线、障碍账本；目录里拾取尺寸与碰撞盒尺寸分列，固定物单列（调研 §6 的三条硬约束） | 非 Unity agent | `python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/PicnicCorner` 0 错；`--feasibility` 每区 ≤85% |
| R3 | 生成器输出逐件实例（稳定名 `prop_<definitionId>_<NN>`、kind 文件夹、坐标、拾取尺寸、价值；命名与 kind 约定照 [关卡内容计划](<../StoryMode/LEVEL_CONTENT_PLAN.md>) §4.2），种子固定（G11） | 非 Unity agent | 两次生成逐字节相同；`--check` 通过；新图独立测试模块（`Tools/mapbuild/README.md:172-175`） |
| R4 | 运行时 G1 + G2 + G8（`PickupAbsorbMath` / `PickupFieldHost`），NUnit EditMode 测试 | 代码 agent 写，Unity 操作者编译跑测 | MCP `run_tests` 全绿；`Labs/StoryLevel` 回归：吃 5 件仍附着 |
| R5 | G4：宿主 `IsAttached` + `Runtime/Maps/MapObstacleHost.cs` | 同上 | Play Mode 日志：启动 5 平面 / 8 盒、无 overflow；吃掉 G1 后盒数 7 且能穿过口子；重试后回到 8 |
| R6 | 素材：Kenney 验收清单导入（T20 路线）；6 类 Blender 新道具按 props 管线生成 + 清单 + SHA256SUMS | 素材 agent；导入由 Unity 操作者 | 每件 manifest、三角面预算、导入 scale 1 / rotation 0 |
| R7 | 两段同步，都幂等：① 摆放同步（新，放进 `Editor/MapGreybox/`，与现有载荷 DTO 同处）读 R3 载荷，**按名字增 / 改 / 删**本图场根下的实例，不碰别的对象；② 现有的 `Editor/Pickup/PickupFieldSync.cs`（另一 agent 在写，未提交）从场景回填 `PickupFieldHost` 的并行数组（加 G13） | Unity 操作者运行 | 两段各连跑两次，第二次「0 处变更」；宿主启动日志件数 = 载荷件数；UdonSharp 组件按 `Labs/README.md:41-49` 的序列建 |
| R8 | 灰盒试玩 `Maps/PicnicCorner/PicnicCorner.unity`（含 PipelineManager、ReferenceCamera，`Labs/README.md:5-33`） | 用户按键 + Unity 操作者读日志 | 记录：G1 打开时刻、各尺寸节点时刻与单位、扫描帧时间、截图验视线；不达标改件数，不改目标 |
| R9 | 剧情第一关挂到本图：G12；`hub_levels.json` / `placement.json` 改出生点与区域（口袋 + 毯 + 草地） | 非 Unity agent + Unity 操作者接 `StoryRoundController` | `python3 Tools/levelbuild/levelbuild.py check` OK；Play Mode 0/1/2/3 星各一次；**Build & Test 由用户点**，结果记 `Docs/Evidence/` |
| R10 | 美术与致谢牌、边界外地标；第二关（水果）用全图 | — | 不阻塞 R9 |

顺序约束：R1→R2→R3→R7；R4、R5 与 R2/R3 并行；R6 只挡 R8 的观感，灰盒可以先用基础体顶替。

## 6. 不在本计划内 / 不能当事实用

- 原作没有公开每 m² 密度、关卡文件格式或具体门槛米数（调研 §7）；本文所有数字都是本项目暂定。
- 多人、交分、道具、CPU 对手、坡/台阶（P8）、原作式的弹开削球、地图分块宿主都不在第一版。
- 本文没有改任何代码、数据或任务；§4 的每条修法都要各自走任务与评审。

## 7. 校验器已支持（2026-11-15，R1）

> 本节只记录 `Tools/mapbuild/validate_map.py` 的**现状**，不改本文前面的计划；G10 的
> 落地形态以此处为准（字段名、默认值、哪些段会跳过）。作者数据仍是 `PROVISIONAL_NOT_TESTED`。

**模式字段。** `layout.json` 加可选键 `mode`，取值 `"pvp"`（缺省）或 `"story"`；**不写 `mode`
就是 `pvp`**，所以 PicnicLawn / ToyRoom 的结果不变（下面有证据）。G10 里草拟的
`map_kind: "story_region"` **没有实现**，也没有留别名：R2 请直接写 `"mode": "story"`。
`schema_version` 仍是 1/2，新增字段全部可选，旧图不需要升级。

**story 图（`mode: "story"`）跳过什么、换成什么。** 跳过：`bank`（交分台/转盘/台面接近路线）、
`respawn`、`player_count_plans`、出生→交分的等距路程与偏差、多出生点公平性；这四个顶层键
在 story 图里可以整段不写。**没有**另写一套 PvP 校验：交分台检查被抽成 `check_bank()`，
PvP 图必跑，story 图只有在真的写了 `bank` 时才跑（写了就照查，坏数据不放过）。换成三条
单人检查：①**正好 1 个出生点**；②出生点 `scale.spawn_pocket_clear_radius_m`（≥1.0 m，缺省
1.0）内**不许有可卷物或高于 `max_route_step_m` 的固定立面**——0.015 m 的贴地出生标记/毯面
照旧允许，判据与 PicnicLawn 的贴地件一致；③散布区边界压进净空盘时给 warning——散布点由
生成器随机摆放，**逐件坐标校验器查不了**，这条只能提醒生成器别往口袋里撒。**保留**每个出生点
≥2 条独立出路（门只挡近道）。

**第 14 组：运行时预算（两种模式都跑）。** 碰撞上限抄自电机常量
`Runtime/BallMotor/KatamariBallMotor.cs:243-244`（`MaxPlaneCount = 8`、`MaxBoxCount = 8`；
超出的 `AddPlane`/`AddBox` 静默丢弃并 latch overflow）。数据侧用 `structures[].collision` /
absorb 门的 `collision` 声明 `"plane"` / `"box"`，未声明 = 不进碰撞集。story 图超过 8 报
**error**；PvP 图旧数据没有声明，一旦声明且超标只报 warning（迁移期口径，不悄悄改旧图结论）。
同时检查：`scale.max_pickup_diameter_m`（story 必填）与目录可选列 `pickup_dim_m`——可卷物
游戏尺寸超过最大拾取尺寸时必须自己声明拾取尺寸（长凳那类「挡路 3.2 m、拾取 1.6 m」的门件），
这就是 G13 需要的作者侧通道；每件**可卷**物价值必须是 ≥1 的**整数**（决定 (j)，`int()` 不再
截断 2.5）；目录 id 与同一散布区里的定义 id 不得重复。报告里打印碰撞平面/盒用量、道具总数、
可卷/固定件数与可卷比例、可卷占位价值（`--json` 的 `runtime_budget` 里也有同样数字）。

**代码结构。** 一份实现：`validate()` 顶部只读一次 `mode`，顶层必填键按模式取并集
（`PVP_ONLY_LAYOUT_KEYS` 只在 story 放宽），随后每个 PvP 段各有一个 `if mode == "pvp"` 开关，
交分台整体是一个函数。没有 story 专用副本、没有每图一份校验器；fixture 在
`Tools/mapbuild/tests/fixtures/story_pocket/`，只给测试用，不放进 `Docs/Design/Maps/`。

**证据（2026-11-15）。** `python3 -m unittest discover -s Tools/mapbuild/tests`：120 tests OK
（新增 `test_story_mode.py` 29 个）；`python3 -m unittest discover -s Docs/tests`：270 tests OK；
`python3 Docs/tools/validate_plan.py` PASS。用改动前的 `validate_map.py`（`git show HEAD:`）
与改动后逐张对比 PicnicLawn / ToyRoom 的 `--json`：`status`、`errors`、`warnings`、
`surfaces`、`obstacles` **完全相同**，只多一条运行时预算 note。
`python3 Tools/mapbuild/validate_map.py Tools/mapbuild/tests/fixtures/story_pocket` 0 error /
0 warning。

**校验器仍然查不了的（R8 及运行时修复项的证据缺口，不要当 PASS）：** 电机是否真的注册了
这些平面/盒、overflow 是否发生（G3/G4 没落地）；程序生成的逐件坐标、种子稳定性与件数是否
真的摊到（G11）；吃不动大件的挡路手感（G6）、激活半径（G8）、扫描帧时间（G7）；视线（P4）；
可吸收性相对球径的运行时行为（决定 (h) 已落地但场景资产仍有 1 m 覆盖）；地形结构声明与
Unity prefab 实际包围盒是否一致；以及所有可玩性/公平性/性能判断。
