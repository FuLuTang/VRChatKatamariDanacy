# 第一关迁入野餐一角（PicnicLawn 西南角）：迁移方案

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。**只读调查 + 方案；作者不是 Unity 操作者**：没有编译、没有 Play Mode、没有 Build & Test，没有跑任何 Unity 测试。所有米数、件数、单位都是**本项目暂定值**，不是原作数值。
> 触发：用户 2026-11-15 拍板「第一关不再在 20×20 m 的平地测试场上做，挪进野餐草坪（PicnicLawn）的一角，在真实布局里调手感」。
> 范围：第一关（`story_01_bench_size`，滚到 1.25 m / 120 s）从实验室平地切片搬进 PicnicLawn 西南角；回答场景归属、生成器、角内范围、墙与边界、迁移步骤、未决六问。
> 非目标：不改 `.unity` / `.asset` / prefab / C# / `Tools/` 代码 / `tasks.json` / 其它文档；不设计新物理；不调数值（决定 (p)）；不做多人、交分、第二关。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。**符号名才是契约**：本轮读代码时 `KatamariBallMotor.cs` / `PickupFieldHost.cs` / `RadialProxy*` 正被并行修改（工作区 mtime 2026-11-15 06:23），文中行号是当时的读数，会漂；引用时请按符号名复核。

## 0. 六问一句话结论

| # | 问题 | 结论 |
|---|---|---|
| 1 | 场景归属 | **正本 = 把 `Labs/StoryLevel/StoryLevel.unity` 同一文件搬成正式场景（同一 GUID）**，角内几何按地图真坐标长在它里面；不翻新、也不转正 `PicnicLawnGreybox`，不新建第二张场景（§1）。 |
| 2 | 生成器还能不能用 | **不能用它建这一关**：`PicnicLawnGreyboxBuilder` 是「整图 · 从零重建 · 只产几何 · 会 `SaveScene`」，重跑必然覆盖手工接线；`--check` 只证明生成物与作者数据一致，与场景接线无关。数据侧只作 `--check` 回归，不重跑（§2）。 |
| 3 | 一角的范围 | **西南角** `x∈[−19.40,−6.00]`、`z∈[−19.40,−6.00]`：毛 179.56 m²、扣花圃 19.60 m² 后**净 160.0 m²**；角内只有平地 + 花圃台（0.26 m）+ 树根丘/角树 + 指示牌 + 毯缘一条线；110 件 / 412 u / 9 kind 按 7 个区重排，其中 4 件是**结构门件**（§3.5）。 |
| 4 | 墙与边界 | motor **只能**：一张解析地面 + ≤8 平面 + ≤8 轴对齐盒 + ≤6 动态挡路盒；**没有台阶/坡/分区地形**。所以边界用 **4 个竖直平面**（球）+ 4 面既有墙碰撞体（玩家），地形高差（花圃 0.26 / 树根 0.14 / 毯缘 0.06）**一律只能当墙，不能当尺寸门**（§4）。 |
| 5 | 迁移步骤 | 操作员 16 步（每步带验收点与回读项）+ 实现者 8 项改动（核心是把 Lab 专用墙登记换成通用的「关卡边界」组件）（§5）。 |
| 6 | 未决 | 场地大小（160 / ~187 / 384 m²，U1）、暖壶换成结构门件（U3+U8）、120 s 是否先测再议（U4）、PicnicLawn 全图与 3.20 m 上限（U5）、毯缘算墙还是纯视觉（U6）、整图 452 件散布要不要（U7）—— 6 组待拍板，每条带代价（§6）。 |

**本次一个数据文件都没改**（`layout.json` / `prop_catalog.csv` / `unity_greybox.json` 逐字节未动），原因见 §7.1：前两个被两份关卡 JSON 的 `map_snapshot` 钉住，改一个字节就会让 `validate_levels.py` 与 `Docs/tests/test_levels.py` 变红，而重钉要改 `Docs/Design/GameModes/Levels/*.json`，不在本次写范围。

## 1. Q1 场景归属：哪个 `.unity` 是正本

### 1.1 三条硬约束（先看这个，它把选项砍掉一半）

1. **一个世界 = 一个 Unity 场景。** 运行时不能换场景、脚本不能把玩家送去别的世界，跨世界只有 Portal（[HUB_LOOP_PLAN](HUB_LOOP_PLAN.md) §1 已逐条查证并列出官方来源）。所以「主房间 + 各关卡区域」只能同场景共存。
2. **一个场景里可以有 N 个「岛」，但每个球各有一套 motor**，motor 的障碍表（8 平面 + 8 盒）是**每实例**的；平面是无限半空间（§4），所以岛与岛之间必须靠距离隔离，不能靠「这块归这个 motor」的几何分块。
3. **已有接线的迁移成本**：`StoryLevel.unity` 里已经接好的东西是「110 行宿主数组 + `propColliders` + motor/Station/输入/HUD/回合控制器 + 4 面墙的 `obstacleBoxes`」。这些接线**只存在于这一个文件里**，任何「从数据重建」的路径都要重做一遍。

### 1.2 三个候选与代价

| 选项 | 做什么 | 代价 | 判定 |
|---|---|---|---|
| **A. 翻新 `PicnicLawnGreybox` 当正本** | 改数据 → 重跑 `build_greybox.py` → 重跑 Unity 生成器 → 再接线 | 生成器 `SaveScene` 从零重建场景（`Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs` 头注释与 `SaveScene` 调用），**必然抹掉**之后手工加的 motor/宿主/回合/Station；且它不产任何 Udon（全文只有 `AddComponent<BoxCollider>` 等几何组件，无 `UdonBehaviour`）；它是 **40×40 全图**，不是一角；还要先解决「全图 452 件 + 4 交分台 + 4 出生点」和单人第一关的取舍 | **否** |
| **B. 把一角的地形搬进 `StoryLevel`（同一文件转正）** | 同一 `.unity` + 同一 `.meta` 搬到 `Assets/VRC_Katamari/Worlds/StoryMode/`，角内几何按地图真坐标长在里面 | 需要重排 110 件道具（§5 步骤 S4）；场景会同时承载「房间岛 + 关卡岛」，内存/渲染都在一起（HUB_LOOP_PLAN §2 的已知代价） | **推荐** |
| **C. 新建一张正式场景** | 新 `.unity`，把 lab 内容复制过去 | 立刻两张各改一半的关卡场景（`FIRST_PLAYABLE_PLAN.md` §5 与 `AGENTS.md` 反复在防的账）；GUID 分叉，所有引用要重指 | **否** |

### 1.3 推荐 B（不推荐 A / C），理由四条

1. **HUB_LOOP_PLAN §7 已经拍过同一件事**：「把 `Labs/StoryLevel/StoryLevel.unity` 整体搬成正式场景（同一文件、同一 GUID、`.meta` 一起搬），房间直接加进它。不新建第二张、不复制。」角内改的是**内容与坐标**，不是场景归属，所以这条不变。
2. **接线是唯一不可再生的资产**：110 行平行数组、`propColliders`、`station`、`roundController`、`HUD` 全在这个文件里；搬场景是 `mv`，重建场景是重做。
3. **`PicnicLawnGreybox.unity` 的定位本来就只是「数据预览」**：它是 `Python 数据 → unity_greybox.json → 编辑器生成器`的产物，`GreyboxReport.json` 与俯视图是它的验收物，`Labs/README.md` 也写明 Labs 不是产品场景。它的价值是**看图对数据**，不是承载玩法。
4. **坐标不需要换算**：角内几何直接取 `layout.json` 的真坐标（花圃 `(−10, 0.13, −8.55)`、角树 `(−18.75, −18.75)`、出生地贴 `(−14.5, −14.5)`…）。以后整张 PicnicLawn 若进同一场景，这一角**已经在正确位置**，不需要再搬一次。

**正本一句话**：`Labs/StoryLevel/StoryLevel.unity` → 同一文件搬到正式目录并转正（`HUB_LOOP_PLAN` §7 的三件事），角内几何与第一关内容长在它里面；`PicnicLawnGreybox.unity` 保留为全图数据预览，不转正、不接线。

## 2. Q2 生成器还能不能用

### 2.1 生成链现在长什么样

```
Docs/Design/Maps/PicnicLawn/layout.json ─┐
Docs/Design/Maps/PicnicLawn/prop_catalog.csv ─┴─► Tools/mapbuild/build_greybox.py
                                                     ├─► --check（生成物 vs 作者数据，逐字节）
                                                     └─► unity_greybox.json
                                                              └─► Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs
                                                                     └─► Labs/PicnicLawnGreybox/PicnicLawnGreybox.unity（从零重建 + SaveScene）
```

三个事实决定「能不能重跑」：

1. **生成器只产几何，不产 Udon**：`PicnicLawnGreyboxBuilder.cs` 全文没有 `UdonBehaviour` / `KatamariBallMotor` / `PickupFieldHost` / `StoryRoundController`，只 `AddComponent<BoxCollider>`、`MeshRenderer`、`TextMesh`、`Light`、`Camera`。
2. **它是幂等重建，不是增量更新**：场景在临时场景里从零搭好再调用 `EditorSceneManager.SaveScene(scene, ScenePath)`。任何手工接线（宿主数组、motor 引用、Station、HUD）都会在重跑时消失。
3. **它硬校验记录数**：`ExpectedZones = 21`、`ExpectedStructures = 60`、`ExpectedSizeGates = 8`、`ExpectedScatterRegions = 20`、`ExpectedRoutes = 21`、`ExpectedChokepoints = 16`、`ExpectedProps = 54`；数据一变（比如为一角改件数/区数）它会**直接拒绝构建**，不会静默出一张不同的图。

### 2.2 数据里哪些是旧口径（逐项判定）

| 数据项 | 位置 | 现状 | 新口径 | 这次动不动 | 为什么 |
|---|---|---|---|---|---|
| `scale.item_tier_pickup_diameter_m`（s 0.08–0.30 / m 0.32–0.95 / l 1.00–1.95） | `layout.json:49` | **长度**档位：`clamp(game_longest_dim_m, 档窗口)` 当"拾取门槛" | 体积门 `a·b·c ≤ (π/6)·D³/10`；等价边长 `a_eq = (abc)^(1/3)`，`D_req = 2.673·a_eq` | **不动** | 它是内容侧分档、也是校验器第 11 条唯一的判据；改它必须同时改校验器与值表，而校验器不在写范围 |
| `game_longest_dim_m`（目录列） | `prop_catalog.csv` | 「游戏尺寸」（比真实尺寸放大，例：`apple` 0.08→0.30） | 真实尺寸建模、`localScale = 1`、`propBounds` = 模型 AABB | **不动** | 同一列同时喂校验器与生成器的脚印/间距模型；改它会让 `build_greybox --check` 与 README §3.3 的可行性表一起失效（那张表被 `ReadmeFeasibilityTableTests` 逐字比对） |
| `real_longest_dim_m`（目录列） | `prop_catalog.csv` | 真实最长边，只有一列、没有三轴 | 需要三轴或 `volume_m3` | **不动** | 缺第三轴，任何 `a_eq` 都是估的；按「不手抄推导数字」的规矩不写 |
| `@size=<米>` 立方体写法 | 场景道具命名（`Editor/Pickup/PickupFieldAuthoringRules.TryParseDeclaredSize`） | 「挡路 3.6 m、拾取 1.25 m」→ 把 `propBounds` 记成 1.25³ = 1.95 m³ | 体积门下 1.95 m³ 需要 **2.60 m** 球，比 2.20 上限还大 —— 这个通道**已经把门件写成死件**（(l) 原文点名） | **不动（列为实现者改动 C6）** | 正确做法是「`propBounds` 说真话（模型 AABB）+ 挡路盒另立」（挡路接线那条线在做）；改 `PickupFieldAuthoringRules` 属代码，不在写范围 |
| `maxItemSizeWorldM` / `absorbSizeRatio` | `Labs/CpuSkirmish/CpuSkirmish.unity`（**`StoryLevel.unity` 已 0 命中**，本轮 grep 实测；`Labs/BallPlayground` 的 `.unity`/`.prefab` 已于 2026-11-15 随该 lab 退役删除） | 死键，Unity 忽略、回落 512 / 10 | 已删字段 | **不动** | 在场景/prefab 里，属编辑器产物；删死键是 Unity 侧顺手活（(l) 已列为"仍待做：7 处场景序列化覆盖"），且不属于本关场地 |
| `maxItemVolumeWorldM3` / `absorbVolumeRatio` | 同上（场景 `512` / `10`） | 已是新口径 | 同左 | **不改** | 值就是契约默认值，不需要覆盖 |
| `pickup_dim_m`（可选列） | 目录 schema | 故事图专用「声明拾取尺寸 ≠ 外形」通道 | 体积门不需要它；需要的是真实 AABB + 挡路盒 | **不动** | PicnicLawn 目录里没有这一列；它有在故事图 fixture 里，与本次无关 |
| 障碍/平面的 `collision` 声明 | `layout.json` 结构 | 无（0/8、0/8） | 第 14 条运行时预算 | **不动** | PicnicLawn 是全图 PvP 图，不是这一关的关卡数据；这一关的边界账在本文 §4 |

**结论**：数据文件里真正「过期」的是**概念**（线性档位、`@size` 立方体、game/real 双列），而它们的消费方（校验器 `validate_map.py` 第 11 条 + `PickupFieldAuthoringRules` + `build_greybox`）**都还按旧口径判**。按「不改校验器就只改不受影响的字段」的规矩，本轮**一个机器可读字段都不改**。

### 2.3 取舍：「翻新数据 → 重跑 → 再接线」还是「不重跑、手工迁移」

| 路线 | 适用 | 代价 |
|---|---|---|
| **翻新数据 → 重跑生成器 → 再接线** | 整图 PicnicLawn 的下一版灰盒（T21） | 必须先补校验器（§2.5）、补生成器的「保留手工接线」能力（今天没有），再重做全部 Udon 接线；对角内第一关是纯亏 |
| **不重跑生成器、手工迁移一角**（推荐） | **本关** | 110 件要重排（§5 S4）；但接线、宿主数组、交付路径全部保住 |
| 折中：数据照旧，只跑 `--check` 回归 | 日常 | `python3 Tools/mapbuild/build_greybox.py --check` 必须保持 OK（本轮实测 OK） |

**生成器仍然有用的地方**：① `--check` 守住「生成物 = 作者数据」；② `--feasibility` 是唯一被测试保护的容量模型，重排 110 件时**照它的公式算**（`RSA_SITE_AREA = 1.436·S²`，用量 ≤85%）；③ 俯视图 `PicnicLawnGreybox_top.png` 是角内布置的参考底图。

### 2.4 角内布置该由谁负责（现在的缺口）

**今天没有任何数据文件描述「第一关的角内布置」**：110 件的坐标只活在场里（宿主六个平行数组 + Transform），`layout.json` 描述的是整图 PvP 语义。所以重排是**手工 + 约定**，没有 `--check` 兜底。这是本方案最大的数据缺口，已列在 §5.2 C8 与 §6 U2。

### 2.5 校验器需要怎么改（本轮不改，只列）

`Tools/mapbuild/validate_map.py`（**不在写范围，以下只是清单**）：

| 位置 | 现在 | 应改为 |
|---|---|---|
| 第 11 条「档位与球包线」 | `threshold = clamp(game_longest_dim_m, 档窗口)`，要求 `threshold ≤ max_ball` | 用 `a_eq`（或目录新增 `a_eq_m` / `volume_m3` 列）判 `D_req = 2.673·a_eq ≤ D_cap`；长度档位退化为「价值分档」，与可吸性脱钩 |
| 第 14 条「运行时预算」 | 只查 `pickup_dim_m ≤ max_pickup_diameter_m` | 改成体积上界 `a·b·c ≤ (π/6)·D_cap³/10`；`pickup_dim_m` 通道退役 |
| 目录 schema | 必填 14 列，无体积列 | 建议加可选 `aabb_m`（"x;y;z"）或 `volume_m3`，由建模清单生成，禁止手抄 |
| 第 13 条立面分类 | 允许 `climb_step`（台阶可按球径翻越） | 与运行时对齐：motor **没有 step-up**（§4 G1），`climb_step` 目前只能是「设计意图」，要么标注为"未实现"，要么换成 `obstacle` |

## 3. Q3 一角的范围与道具映射

### 3.1 坐标与面积（取地图真坐标，不新建坐标系）

| 项 | 值 | 依据 |
|---|---|---|
| 场地矩形 | `x ∈ [−19.40, −6.00]`，`z ∈ [−19.40, −6.00]` | 西/南 = 地图边界（树篱内面 19.6、不可见墙 19.8，取 19.4 留 0.2 m 避开穿插）；东 = `bed_gap_sw` 外侧（花圃缝 `x∈[−8,−6]` 正好 2.0 m，与 `layout.json.chokepoints.bed_gap_sw` 一致）；北 = 野餐毯南缘 `z = −6.0` |
| 毛面积 | 13.40 × 13.40 = **179.56 m²** | 矩形 |
| 扣花圃 | −19.60 m²（`flower_bed_frame` 足迹 4.0 × 4.9） | `layout.json.structures` |
| **净可玩面积** | **160.0 m²** = 179.56 − 19.60（花圃足迹） | 算术；0.02 m 网格积分复核 160.0 |
| 地面 | `y = 0` 平地（地图 `ground_terrain` 就是 `y=0` 的 40×40 合并面） | `layout.json.structures[0]` |
| 出生（玩家） | `(−14.50, 0.50, −14.50)`，yaw 45°（面朝东北＝朝场内） | 地图 `spawn_sw` + `spawn_marker` |
| 球起点 / `startCenterWorld` | `(−12.50, 0.15, −12.50)` | 出生点前 2.83 m（现场景是 3 m：`(0,0.5,−3)` → `(0,0.15,0)`） |
| 出生净空 | 球起点 1.0 m 内不放任何道具；玩家出生点 1.0 m 内不放任何碰撞体 | `validate_map` story 检查的口径（1.0 m） |
| 场内高差 | 只有花圃台 0.26 m（当墙）；毯缘 0.06 m 在边界线外 | §4 G1 |

**为什么是这个矩形而不是「20×20 整象限」**：整象限（`x,z ∈ [−20,0]`，400 m²）虽然能零重排地平移现有 110 件，但它的两条内边界 `x=0`/`z=0` 会把西野餐毯（`x −13.5..−7, z −6..6`）、南篮子区（`x −6..6, z −13..−8`）和中央草坪各**切一半**，毯缘 0.06 m 立面还会变成场内一道墙 —— 那不是「真实布局里调手感」，是「把平地框搬到地图上」。见 §6 U1 的 B/C 备选与代价。

### 3.2 角内的地形与布景（只有这些，全部来自 `layout.json`）

| 东西 | 真坐标 / 尺寸 | 在本关的角色 | 碰撞 |
|---|---|---|---|
| 地面 `ground_terrain` | 40×40、`y=0` | 球与玩法的地面 | motor 解析平面（§4） |
| 花圃台 `flower_bed_frame` | 中心 `(−10.00, 0.13, −8.55)`，`4.0 × 0.26 × 4.9` | **中央地标 + 不可翻越的障碍**；把东北方向切成两条路（西侧大带、东侧 2 m 缝） | 1 个盒（`0..0.26`） |
| 花圃缝 `bed_gap_sw` | `(−7.00, 0, −8.55)`，净宽 2.0 m | 在整图里是**小球侧缝**；在角内这一段北端被边界封住，变成一条 2.0 × 5.0 m 的死胡同 → 本方案把它改造成**壁龛**（§3.5.3），缝内只放门堆 4 件 + 壁龛 6 件 | 不碰撞；门堆用动态挡路盒 |
| 树根丘 `tree_root_mound` | 中心 `(−18.75, 0.07, −18.75)`，`Ø2.5 × 0.14` | 墙角地标（0.14 m 立面＝**墙**，不是坡） | 1 个盒 |
| 角树 `corner_tree` | `(−18.75, 0, −18.75)`，`Ø0.5 × 9` | 识别「这是西南角」的方位锚 | 1 个盒 |
| 出生地贴 `spawn_marker` | `(−14.50, 0.01, −14.50)`，`Ø1.8 × 0.01` | 出生标记（贴地，< 0.05 m，算地贴） | 无 |
| 区域指示牌 `zone_signpost` | `(−13.20, 0, −6.60)`，`0.45 × 1.2 × 0.12` | 小地标；**它的 `position.y = 0` 与「1.2 m 高」在数据里不自洽**（按 `rise = pos_y + size_y/2` 只有 0.6 m）。本关按「柱脚 0、总高 1.2 m」建模，数据不自洽留给地图 owner | 1 个盒 |
| 毯缘 `picnic_blanket` 南缘 | `z = −6.0`，`0.06 m` 立面 | **正好是北边界线**：球被北边界平面挡住，不会去撞那条 6 cm 立面 | 不需要 |
| 边界树篱 + 不可见墙 | 西/南 ±19.6 / ±19.8 | 视觉边界 + 玩家碰撞 | 玩家用（球用平面） |
| 门外布景 | `collapsed_bench` 在 `(−5.72, −5.72)`（东北角外 0.28 m）、`bank_c` 交分台 `(−4.2, −4.2)` | 「看得见吃不到的大件」——3.6 m 长凳要 3.12 m 球，2.20 上限下永远吸不到（(n) 已判） | 不在场内，不登记 |

> 数据瑕疵记录（不改）：`zone_signpost` 的 `position.y = 0` 与 `size_m[1] = 1.2` 不自洽；`boundary_hedge` 是 40 m 环形合并体、`invisible_wall` 是 4 段 40 m 盒，角内只有它们的一段。
> 几何细节：花圃北面在 `z = −6.10`，北边界在 `z = −6.00`，两者之间是一条 4.0 × 0.10 m 的地缝 —— 球进不去（宽 0.1 m），可忽略；想让画面干净就把北墙贴到 `z = −6.10`。

### 3.3 角内 PicnicLawn 目录道具的 2.20 m 判定表

判据：`D_req = 2.673 · a_eq`，`a_eq = (a·b·c)^(1/3)`；`D_cap = 2.20 m` ⇒ 能吸的上界是 `V ≤ 0.5575 m³` / `a_eq ≤ 0.823 m`（(l)(n)）。**AABB 来源**：`M` = 模型 `exports/<id>.manifest.json` 实测（`Docs/Art/OriginalModels/props/exports/`）；`D` = `PROP_CATALOG.md` §2.1 的设计三轴；`估` = 只有最长边、按立方体上界（最保守）算。

| 目录 id | AABB m | 来源 | `a_eq` | `D_req` m | 2.20 判定 | 角内角色 |
|---|---|---:|---:|---:|---|---|
| seed | 0.0224×0.0400×0.0160 | M | 0.024 | 0.065 | 可吸（起手即可） | 起步环 |
| bottle_cap | 0.0500×0.0476×0.0145 | M | 0.033 | 0.087 | 可吸 | 起步环 |
| pebble | 0.0600×0.0440×0.0319 | M | 0.044 | 0.117 | 可吸 | 起步环 / 树根角 |
| acorn | 0.0400×0.0380×0.0500 | M | 0.042 | 0.113 | 可吸 | 起步环 / 树根角 |
| paper_scrap | 0.1000×0.0741×0.0118 | M | 0.044 | 0.119 | 可吸 | 起步环 |
| fallen_leaf | 0.09×0.06×0.001 | D | 0.018 | 0.047 | 可吸（薄片，体积门几乎不设防） | 起步环 |
| cookie | 0.0700×0.0689×0.0094 | M | 0.036 | 0.095 | 可吸 | 起步环 |
| cheese_slice | 0.06×0.05×0.008 | D | 0.029 | 0.077 | 可吸 | 起步环 |
| strawberry | 0.0362×0.0358×0.0400 | M | 0.037 | 0.100 | 可吸 | 水果簇 |
| grapes | 0.0600×0.0220×0.0597 | M | 0.043 | 0.115 | 可吸 | 水果簇 |
| mushroom | ≤0.12（最长边） | 估（立方体上界） | ≤0.120 | ≤0.321 | 可吸 | 花圃台周边（布景上的不吸） |
| flower | ≤0.18（最长边） | 估（立方体上界） | ≤0.180 | ≤0.481 | 可吸 | 花圃台内（本关不取） |
| soda_can | 0.066×0.066×0.12 | D | 0.081 | 0.215 | 可吸 | 中圈 |
| cup | 0.075×0.075×0.09 | D | 0.080 | 0.213 | 可吸 | 中圈 |
| apple / orange | 0.080×0.078×0.079 | M | 0.079 | 0.211 | 可吸 | 水果簇 |
| pear | 0.0550×0.0536×0.0900 | M | 0.064 | 0.172 | 可吸 | 水果簇 |
| banana | 0.1800×0.0678×0.0320 | M | 0.073 | 0.195 | 可吸 | 水果簇 |
| croissant | 0.12×0.07×0.06 | D | 0.080 | 0.213 | 可吸 | 中圈 |
| bread_slice | 0.10×0.10×0.015 | D | 0.053 | 0.142 | 可吸 | 中圈 |
| sandwich | 0.12×0.09×0.04 | D | 0.076 | 0.202 | 可吸 | 中圈 |
| napkin | 0.3000×0.3000×0.0280 | M（游戏尺寸） | 0.136 | 0.364 | 可吸 | 中/外圈 |
| juice_carton | 0.06×0.06×0.10 | D | 0.071 | 0.190 | 可吸 | 中圈 |
| glass_bottle | 0.07×0.07×0.28 | D | 0.111 | 0.297 | 可吸（**起手门边界**：0.2997 刚好够） | 外圈 |
| bowl | 0.16×0.16×0.06 | D | 0.115 | 0.308 | 可吸 | 中/外圈 |
| plate | 0.24×0.24×0.025 | D | 0.113 | 0.302 | 可吸 | 中/外圈 |
| spoon | 0.18×0.05×0.012（§3.6 描述） | D | 0.048 | 0.127 | 可吸（细长件体积门白送） | 中/外圈 |
| toy_ball | 0.4000³ | M（游戏尺寸） | 0.400 | 1.069 | 可吸（要 1.07 m 球） | 外圈 |
| small_potted_plant | 0.14×0.14×0.22 | D | 0.163 | 0.435 | 可吸 | 外圈 / 花圃旁 |
| watering_can | 0.2603×0.5800×0.4154 | M（游戏尺寸） | 0.397 | 1.062 | 可吸（要 1.06 m 球；真实尺寸后会降到 ~0.46） | 外圈 |
| watermelon | 0.3000×0.2400×0.2200 | M | 0.251 | 0.671 | 可吸（本关最大的水果） | 水果簇 |
| thermos（**设计真实尺寸** 0.12×0.12×0.30） | 0.12×0.12×0.30 | D | 0.163 | **0.435** | **可吸（如果按真实尺寸建模）** | 大件 |
| thermos（**场景现状 0.9 m 立方体**） | 0.9³ | 场景 | 0.900 | **2.406** | **吸不到（> 2.20）** | —— 见 §6 U3 |
| collapsed_bench | 3.6×0.55×0.80 | D | 1.166 | **3.116** | **吸不到** | 角外（东北角外 0.28 m） |
| picnic_table | 1.50×0.75×0.72 | D | 0.932 | **2.492** | **吸不到** | 不在角内 |
| park_bench | 1.80×0.55×0.80 | D | 0.925 | **2.473** | **吸不到** | 不在角内 |
| 固定结构（地面/毯/树篱/墙/树/花圃框/地贴/指示牌/交分台/道具台） | —— | —— | —— | —— | **不可吸（fixed）** | 布景/障碍，见 §3.2 与 §4 |

**读法三条**：

- **角内「可吸」与「布景」的分界不是难度，是角色**：目录里 `role = fixed` 的 17 行（地面、毯、树篱、墙、树、花圃框、地贴、指示牌、交分台、道具台、远景…）**永远不可吸**；`absorbable` 行在 2.20 下只有三件越线（`collapsed_bench` 3.12、`picnic_table` 2.49、`park_bench` 2.47），它们全部在角外或整图区。
- **薄片类（`fallen_leaf` / `cheese_slice` / `cookie` / `paper_scrap`）在体积门下等于沙粒**：0.30 m 起手球就能吃。这是 (l) 已记录的忠实行为，不是本关的 bug；要不要给薄片设厚度下限是设计判断（`PROP_CATALOG §2.4` 已列出），本方案不替用户决定。
- **「按什么尺寸算」比「哪件」更决定结论**：同样是 `absorbable` 的大件，按目录的 `game_longest_dim_m`（当立方体）算，2.20 下**全死** —— `cooler_box` 1.6³→**4.28 m**、`folding_stool` 1.4³→**3.74 m**、`picnic_basket` 1.2³→**3.21 m**、`flower_pot_large` 1.0³→**2.67 m**、`thermos` 0.9³→**2.41 m**，再加 `collapsed_bench` 3.12 / `picnic_table` 2.49 / `park_bench` 2.47。按真实尺寸（(o) 的建模口径）算，前五件全部复活（`D_req` 1.07–1.47 m；暖壶按 0.12×0.12×0.30 是 0.435），只剩三件桌/长凳级越线。这就是 (l) 说的"先让 `bounds` 说真话"在本图的具体形态 —— **不是比值 10 太大，是方块 `bounds` 把体积放大了几十倍**。
- **本关真正的"上限件"是 4 支暖壶**：现状 0.9³ 立方体在 2.20 下**永久吸不到**（`D_req 2.406`），这正是 `PROP_CATALOG §2.2` 说的"关卡设计上的死物"。搬进角内必须先解决它（§6 U3），否则 412 u 里有 64 u 是空账。

### 3.4 现有 110 件 / 412 单位 / 9 kind 怎么映射进去

**总量不变**：110 件、412 u、9 个 kind（`fruit 40 / tableware 22 / food 17 / drink 10 / nature 7 / container 4 / garden 4 / toy 4 / debris 2`）原样保留，只换布置 —— 4 支暖壶换成 3 篮 1 箱之后，**每个 kind 的件数一点没变**（container 仍是 4），所以关卡 2 的 `fruit 40` 不受影响。分 7 个区（其中 `heavy` 的 4 件是**结构型可吸物**，见 §3.5）：

| 区 id | 位置（真坐标） | 面积 m² | 道具 | 件 | 单位 | 间距 S | RSA 上限 | 用量 |
|---|---|---:|---|---:|---:|---:|---:|---:|
| `pocket` 起步口袋 | 球起点 `(−12.5,−12.5)` 的环带 `r ∈ [1.0, 3.2]`（扣与水果簇 A 的重叠） | 26.5 | seed 3、acorn 2、pebble 1、bottle_cap 1、paper_scrap 1、fallen_leaf 1、cookie 2、cheese_slice 1 | 12 | 12 | 0.40 | 115 | 10% |
| `fruit_a` 水果簇 A | `x ∈ [−11.8,−6.2]`，`z ∈ [−19.2,−13.6]`（东南，靠 2 m 缝入口；扣与口袋环的重叠） | 28.2 | strawberry 4、grapes 4、apple 3、pear 2、orange 3、banana 2、watermelon 2 | 20 | 56 | 0.60 | 55 | 37% |
| `fruit_b` 水果簇 B | `x ∈ [−19.2,−13.6]`，`z ∈ [−12.8,−6.2]`（西北，花圃西侧后方） | 31.7 | strawberry 4、grapes 4、apple 3、pear 3、orange 3、banana 2、watermelon 1 | 20 | 56 | 0.60 | 61 | 33% |
| `mid` 中圈 | 其余空地（扣口袋环、水果簇 A/B、花圃、壁龛、门堆） | 60.4（与 `outer`/`heavy` 共用） | cup 4、bowl 3、plate 3、spoon 3、croissant 3、bread_slice 3、sandwich 2、soda_can 3、juice_carton 2、toy_ball 2、napkin 2 | 30 | 120 | 0.70 | 86（mid+outer+heavy 合计） | 60% |
| `outer` 外圈 | 同上，靠墙角与花圃两侧 | —— | cup 1、spoon 1、croissant 2、bread_slice 2、sandwich 2、soda_can 2、juice_carton 2、toy_ball 2、napkin 2、small_potted_plant 1、watering_can 1 | 18 | 76 | 0.70 | 同上 | 同上 |
| `alcove` 花圃壁龛 | `x ∈ [−8,−6]`，`z ∈ [−11,−6]`（花圃东面与东墙之间的死胡同，**只有南口一个入口**） | 10.0 | glass_bottle 1、plate 1、bowl 1、napkin 1、small_potted_plant 1、watering_can 1 | 6 | 28 | 0.60 | 19 | 32% |
| `heavy` 结构件 | 壁龛南口（`z ≈ −10.6`，横跨 `x −8.0..−6.0`） | —— | picnic_basket 3、cooler_box 1（**顶替 4 支暖壶**，见 §3.5） | 4 | 64 | 0.70 | 并入上式 | —— |
| **合计** | | **160.0** | 31 个定义 | **110** | **412** | | | |

**校验算式**（可手算复核）：

- 件数：12 + 20 + 20 + 30 + 18 + 6 + 4 = **110** ✓
- 单位：12 + 56 + 56 + 120 + 76 + 28 + 64 = **412** ✓
- 逐定义合计与 [LEVEL_CONTENT_PLAN](LEVEL_CONTENT_PLAN.md) §2.1 完全一致，扣掉 4 支暖壶、换成 3 篮 1 箱之后仍对得上：apple 3+3=6、pear 2+3=5、plate 3+1=4、bowl 3+1=4、cup 4+1=5、napkin 2+2+1=5、small_potted_plant 1+1=2、watering_can 1+1=2、glass_bottle 0+1=1、picnic_basket 0+3=3、cooler_box 0+1=1、thermos 4−4=0。
- 容量：`cap = area / (1.436·S²)`（与 `Tools/mapbuild/build_greybox.py` 同一个 RSA 模型），`mid+outer+heavy` 52 件共用 60.4 m²，`S = 0.70` → `cap = 60.4/(1.436×0.49) = 86`，用量 52/86 = **60%** ≤ 85% ✓；壁龛 6 件 / 10.0 m²、`S = 0.60` → `cap = 19`，用量 32% ✓。

**摆放规则（操作员照做）**：

1. 球起点 1.0 m 内净空；玩家出生点 1.0 m 内无碰撞体。
2. 花圃台足迹内**不放任何道具**（那是布景台，球上不去）；花圃四周留 0.5 m 净距。
3. 2 m 缝（`x ∈ [−8,−6]`，`z ∈ [−11,−6]`）**只放 §3.5 的门堆 4 件与壁龛 6 件**，其余留空；离门中线 1.5 m 内不要有其它未解锁件（挡路盒只有 6 格）。
4. 每件按真实尺寸、`localScale = 1`、轴心在几何中心、贴地 `y = 高/2`（(o) 三层分离）；**已换真实网格的道具不带 `BoxCollider`**。
5. 命名 `prop_<definitionId>_<NN>`、父级 `PickupField/<kind>/`（同步工具按这两条取 itemId 与 kind）。
6. 本关**没有尺寸门**（没有 `absorb` 门；`climb` 门在运行时也不成立，见 §4 G1），所以**不要**给角内写 `blocks_ball_diameter_below_m` / `opens_path` 之类的字段。

### 3.5 结构型可吸物：同一件东西的三种身份，我们能兑现两种

> 原则来自用户 2026-11-15 的补充（转述原作观察，研究正在进行：`Docs/Research/KatamariMaps/LEVEL_CONSTRUCTION.md`，本方案不引用未完成的结论）：**原作的场景除墙/楼梯/地板外，整体是用可吸附物搭出来的**——台球桌上摆杂物、一块长木板从地板斜搭到桌面；小球沿木板滚上桌吃杂物，中期木板本身能吸，后期桌子也能吸。同一件东西**先当通路/地形，吸不动时挡路，长大后变成食物**。

#### 3.5.1 三种身份，我们只能兑现两种

| 身份 | 原作怎么用 | 我们能不能 | 代码依据 |
|---|---|---|---|
| **通路 / 地形**（滚上去、搭坡、叠放） | 斜板、叠箱、桌面 | **不能** | ① 地面支撑永远是对**同一张全局平面**（`groundPlaneNormal/Offset`）求投影：空壳时退化成核心球（`proxyEmptyPoints`），有吸附物时喂的是**已吸附件 AABB 的极值点**（`RadialProxyShellMath.TryBuildContactPoints`，上限 `maxProxyProtrusionWorldM = 0.3`、`maxProxyLiftPerStep = 0.04`）——所以"粘在球上的东西"能把球垫高，**场景里的桌子/斜板不能**（它们只是扫掠盒，不参与支撑）；② 所有静态/动态障碍都是**实心 AABB**（`AddBox`/`SetBlockingBoxes`），扫掠只把球停在盒外，**没有任何 step-up**；③ `maxLiftPerStep = 0.1` 只沿地面法线生效，不是"抬到盒顶"（支撑与盒顶之间没有通道） |
| **挡路**（吸不动时挡球） | 大件挡住小球的去路 | **能，而且是现成的** | `PickupFieldHost.UpdateBlockingProps` 用 `propBounds` 建轴对齐盒、经 `motor.SetBlockingBoxes` 进扫掠；谓词就是吸收门自己：`PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, limit)`（`PickupFieldHost.UpdateBlockingProps`，头注释与 `IsBeyondAbsorbVolumeLimit` 调用处） |
| **食物**（长大后吸走） | 同一件东西后来被吃 | **能，和上一条是同一个函数** | 球长大 → `limit` = `(π/6)·D³/10` 超过该件体积 → 同一谓词不再成立 → 下一帧扫描撤掉挡路盒、同时吸收门放行。**"挡路"与"可吸"是同一个判据的两面，不会漂移** |

一句话：**我们没有"竖向通路"，但有完整、且与吸收门同源的"挡路 → 食物"翻转**。所以本关的结构设计不是"搭一个台子"，而是**用可吸物搭出一扇会自己消失的门**。

#### 3.5.2 结构件与阈值（`D_req = (60·a·b·c/π)^(1/3)`）

尺寸取真实尺寸（(o) 建模口径）；`V` 是 AABB 三轴积，`D_req` 既是"可吸"的球径、也是挡路盒消失的球径（同一个数）。

| 结构件 | 真实 AABB m | `V` m³ | `a_eq` | **`D_req`** | 0.30 起手时 | 1.48（本关天花板）时 |
|---|---|---:|---:|---:|---|---|
| 野餐篮 `picnic_basket` | 0.45×0.30×0.22 | 0.02970 | 0.310 | **0.828** | 挡路 | 食物 |
| 保温箱 `cooler_box` | 0.55×0.35×0.30 | 0.05775 | 0.387 | **1.033** | 挡路 | 食物 |
| 折叠凳 `folding_stool`（本关**不用**，留作 B 案） | 0.40×0.30×0.45 | 0.05400 | 0.378 | 1.010 | 挡路 | 食物 |
| 卷起的野餐垫（B 案） | 0.80×0.20×0.20 | 0.03200 | 0.317 | 0.849 | 挡路 | 食物 |
| 托盘（B 案） | 0.55×0.35×0.04 | 0.00770 | 0.197 | 0.528 | **"薄墙"**：0.04 m 低于 0.05 m 台阶阈值，但今天仍会建挡路盒（§4.3 G9）→ 起手是一堵 4 cm 墙；C9 之后才是"通路" | 食物 |
| 保温壶（真实 0.12×0.12×0.30） | 0.12×0.12×0.30 | 0.00432 | 0.163 | 0.435 | 挡路 | 食物（**本关被篮子/箱子顶替**） |
| 平铺野餐垫（视觉毯） | 1.80×1.40×0.02 | 0.05040 | 0.369 | 0.987 | **"通路"的唯一候选**：0.02 m 平，只要不给它建挡路盒，球就碾过去（§4.3 G9 + C9） | 食物 |
| 野餐桌 `picnic_table` | 1.50×0.75×0.72 | 0.81000 | 0.932 | **2.492** | 永远挡路/布景 | **仍不可吸**（> 2.20） |
| 公园长椅 `park_bench` | 1.80×0.55×0.80 | 0.79200 | 0.925 | **2.473** | 永远 | **仍不可吸** |
| 倒塌长凳 `collapsed_bench` | 3.60×0.55×0.80 | 1.58400 | 1.166 | **3.116** | 永远 | **仍不可吸**（角外东北） |
| 花圃台 `flower_bed_frame` | 4.00×0.26×4.90 | 5.09600 | 1.721 | **4.600** | 永远（`fixed`） | 永远 |

读法：**本关能用的"门件"必须 `D_req` 落在 0.30–1.48 之间**（低于 1.48 才会在一局里打开；高于 2.20 的永远打不开，只能当布景）。篮子 0.83、箱子 1.03 正好落在中段；桌子/长椅/长凳全部越线，只能当"看得见吃不到"的地标。

#### 3.5.3 本关的结构：**花圃壁龛**（一扇用野餐装备搭出来的门）

几何是现成的，不用新造墙：花圃台东面（`x = −8`）与东边界（`x = −6`）之间有一条 **2.0 m × 5.0 m** 的死胡同（`z ∈ [−11,−6]`），**只有南口一个入口**（北面被北边界封住、东面被东墙封住、西面是花圃台）。这正好是"一间房 + 一个门洞"。

| 项 | 值 |
|---|---|
| 门洞 | `z ≈ −10.6`、`x ∈ [−8.0, −6.0]`，净宽 **2.0 m** |
| 门件（4 件 `container`，顶替 4 支暖壶） | 野餐篮 ×3（各 0.45 长）+ 保温箱 ×1（0.55 长）= **1.90 m**，横向摆满 |
| 剩余净宽 | **0.10 m** —— 起手 0.30 m 球**钻不过去**（0.10 < 0.30），门是**真封死**的 |
| 第一次打开 | **D ≥ 0.83** 吃掉最外侧的篮子 → 净宽 0.55 → 再吃一只 → 1.00 → 0.83 的球过得去 |
| 完全清空 | **D ≥ 1.03** 吃掉保温箱 → 净宽 1.45 m |
| 门后 | 6 件 / **28 u**（`glass_bottle` 1、`plate` 1、`bowl` 1、`napkin` 1、盆栽 1、洒水壶 1），只占全关 6.8% |
| 软锁检查 | ★ 要 245.99 u，门外有 384 u → **门后内容不是必经**，0.55–0.82 m 的窗口期进不去也不会卡死 |
| 观感 | 一堵"野餐篮 + 保温箱"堆起来的墙，正是原作那种"用可吸物搭场景"的读法 |

**为什么不是"一条只有小球能钻的缝"**：起手球就是 0.30 m，任何 ≥0.30 m 的缝开局就能过，缝就不成门。**在 0.30 m 起手的关卡里，"门"只能是"完全封死 + 吃掉才开"**；这条与整图 PicnicLawn 的 `bed_gap`（2.0 m 小球侧缝）语义不同，不要混用（那张图起手也是 0.30，它的缝是"大球过不去"，属于**永久窄口**，不是门）。

**竖向层次的三种可实现形态**（本关就用这三层，没有第四层）：

| 层 | 高度 | 本关内容 | 球的身份 |
|---|---|---|---|
| L0 地面 | `y = 0` | 草地（解析平面） | 唯一的"地形" |
| L1 贴地层 | ≤ 0.05 m（`scale.max_route_step_m`） | 平铺野餐毯、小路、出生地贴 | **通路**（视觉，不建挡路盒 = C9）→ 长大后可吸（毯 0.99 m） |
| L2 剪影层 | 0.20–0.45 m | 躺倒的篮子 0.22、保温箱 0.30、凳子 0.45（B 案） | **挡路 → 食物**（就是上面那扇门） |
| L3 台面层 | 0.26 m | 花圃台顶（4.0×4.9 m）：摆 5 件小花/蘑菇/盆栽**纯布景**，不进宿主 | **看得见吃不到**（没有 step-up，永远上不去） |
| L4 桌面/叠放 | ≥ 0.5 m 且悬空 | **本关没有** | 不可实现（§3.5.1 第 1 行） |

**L3 为什么只放布景**：花圃台是 `fixed`，它上面的东西要么进宿主（就会变成"永远吸不到的死行"，`PROP_CATALOG §2.2` 已经吃过一次这个亏），要么当纯装饰。选后者，并在关卡说明里写明"那是看得见的，不是这一关的"。

**如果以后要做真正的"沿斜板上桌"**（不在本方案，也不设计物理）：至少需要三件东西，缺一不可 ——

1. **场景物体参与支撑**：今天喂给 `GroundContactMath` 的 proxy 点只来自**已吸附件**（`RadialProxyShellMath`，06:23 仍在被并行接线）；桌子/斜板要能支撑，得让它们也贡献支撑高度，且要决定"球在盒顶时扫掠怎么不停它"；
2. **地面不再是"一张全局平面"**：需要分区高度场或多平面支撑（今天是 `groundPlaneNormal/Offset` 两个标量，见 G2）；
3. **step-up / 斜坡约束**：扫掠遇到低于某个高度的盒时把球"抬上去"或沿斜面走，而不是停住（今天 `SweepMath` 只有"停"与"沿墙滑"）。

#### 3.5.4 操作员要点（叠在 §5.1 上）

1. **门堆的四件贴地摆**（`y = 高/2`），横向首尾相接、**不留缝**；总长 1.90 m、净宽 0.10 m，误差 ±0.02 m。
2. **门堆 1.5 m 内不要再放任何"吸不动"的件**：宿主的动态挡路盒只有 **6 格**（`MaxBlockingBoxCount = 6`），门堆已占 4 格，附近再挤 3 件未解锁的大件就会把门件挤出选择集、门出现"漏缝"（§4.3 G9）。
3. 壁龛内 6 件靠里摆（离门 ≥1.0 m），否则球在门外就能把它们吸出来——`activationRadiusM = 1.6` 是按球心算的，门件一被吃掉，壁龛口就会进入 1.6 m 半径。
4. L3 布景**不要**加进 `PickupField`、不要给 Collider；它们不是道具。
5. 验收补一条：**起手直冲门洞必须被挡住**（截图 + `rej=` 行 `TOO_LARGE`），吃到 0.83 m 后必须能进（截图）。

## 4. Q4 墙与边界：motor 的能力边界（读代码确认，不设计新物理）

### 4.1 事实表（`Runtime/BallMotor/KatamariBallMotor.cs`）

| 能力 | 事实 | 位置 |
|---|---|---|
| 地面 | **一张解析平面**：`groundPlaneNormal` / `groundPlaneOffset` 两个**序列化字段**，`GroundContactMath.TrySolve` 用它算支撑高度 | 字段 `groundPlaneNormal/Offset`、调用点 `StepBall` 的接地段 |
| 地面的高度修正 | 沿**法线**方向，单步夹在 `[−maxDropPerStep, +maxLiftPerStep]` = 0.1 m；吸附物带来的抬升另有 `maxProxyLiftPerStep = 0.04` | 字段 `maxLiftPerStep`/`maxDropPerStep`/`maxProxyLiftPerStep`、`GroundContactMath.TrySolve` |
| 地面不是 `AddPlane` 注册的 | `AddPlane` 把平面塞进**扫掠列表**；Lab bootstrap 的 `AddPlane(groundNormal, groundOffset)` 只是把同一平面再当一道墙登记一次（冗余但无害） | `AddPlane`、`Labs/StoryLevel/StoryLevelLabBootstrap.Setup()` |
| 静态障碍 | `AddPlane(normal, offset)`（**单侧无限半空间**）+ `AddBox(min, max)`（**轴对齐实心盒**） | `AddPlane` / `AddBox` |
| 容量 | 平面 **8**、盒 **8**；超出**静默丢弃并 latch**（`SignalOverflow`） | `MaxPlaneCount` / `MaxBoxCount` / `SignalOverflow` |
| 动态挡路盒 | `SetBlockingBoxes(minima, maxima, count)` **6** 格，由 `PickupFieldHost` 独占写入；与静态 8 盒分开计 | `MaxBlockingBoxCount` / `SetBlockingBoxes` / `PickupFieldHost` 头注释 |
| 清空 | 只有 `ClearObstacles()`（平面+盒+动态一起清），**没有删单个** | `ClearObstacles` |
| 卡住恢复 | 1.5 s 无进展 → 球心瞬移回 `startCenterWorld` | `stuckTimeoutSeconds` / `startCenterWorld` |
| 台阶 / 坡 | **没有**：盒是实心的，扫掠只停球；地面只有一张平面，`maxLift` 只沿法线；没有任何"抬到盒顶"的路径 | 同上一行与 `SweepMath` |
| 旋转盒 | **没有**：`AddBox` 只有 min/max（AABB） | `AddBox` |
| 球半径语义 | 扫掠用 `collisionRadiusWorldM`；接地用 `coreRadiusWorldM`；语义直径 `semanticDiameterWorldM` 三者分开 | 三个字段 + `TrySweepSphere` 调用 |

**由此得到三条硬结论**：

1. **角内地面必须是平的（或整体一个统一坡度）**。花圃 0.26 m、树根丘 0.14 m、毯缘 0.06 m **不可能**成为"可翻的台阶/可走的坡"——它们只能当**墙**。于是 PicnicLawn 的 `climb_step` 尺寸门（(e) 的判据）**在第一关不成立**；这不是本关偷懒，是 motor 没有 step-up。
2. **`AddBox` / `AddPlane` 够用**：角内需要登记的只有 4 道竖直边界 + 3~4 个布景盒（§4.2），远低于 8+8。
3. **`AddPlane` 是无限半空间**：边界平面会约束**这个 motor 的整个坐标系**。一岛一 motor 时没问题；同场景多岛时每个岛必须有自己的 motor，且任何平面都不得跨越别的岛（房间岛在 `(1200,0,0)`，不受影响）。

### 4.2 本关的登记账（8 平面 / 8 盒 的上限内）

**平面 5 / 8**（`AddPlane(normal, offset)`，free space 满足 `dot(n,p) ≥ offset`）：

| # | 作用 | normal | offset | 备注 |
|---|---|---|---|---|
| P0 | 地面 | `(0,1,0)` | `0` | 与现状一致；`groundPlaneOffset = 0` 才是真正让球贴地的那个量 |
| P1 | 西 | `(1,0,0)` | `−19.40` | 球心 `x ≥ −19.40 + r` |
| P2 | 东 | `(−1,0,0)` | `6.00` | 球心 `x ≤ −6.00 − r` |
| P3 | 南 | `(0,0,1)` | `−19.40` | 球心 `z ≥ −19.40 + r` |
| P4 | 北 | `(0,0,−1)` | `6.00` | 球心 `z ≤ −6.00 − r` |

**盒 4 / 8**（`AddBox(min, max)`，世界坐标 AABB）：

| # | 东西 | min | max |
|---|---|---|---|
| B1 | 花圃台 | `(−12.00, 0.00, −11.00)` | `(−8.00, 0.26, −6.10)` |
| B2 | 树根丘 | `(−20.00, 0.00, −20.00)` | `(−17.50, 0.14, −17.50)` |
| B3 | 角树干 | `(−19.00, 0.00, −19.00)` | `(−18.50, 9.00, −18.50)` |
| B4 | 区域指示牌 | `(−13.425, 0.00, −6.660)` | `(−12.975, 1.20, −6.540)` |

余量：平面 3 格、盒 4 格；动态挡路盒 6 格由宿主独占（起点时 `watermelon`、`picnic_basket`、`cooler_box`、`toy_ball`、`watering_can`、`small_potted_plant` 等未解锁件会自动进这套——**门堆就是靠这套翻转的**，见 §3.5.1）。

**玩家的碰撞体另算**：4 面 `Wall_*` 的 `BoxCollider` 继续给玩家走路用（球不读它们），所以**不要**再把这 4 面墙塞进 motor 的盒数组——那样盒会变成 8/8，一格余量都没有。这是本方案与现状接线唯一的"减法"。

### 4.3 缺口（如实写，不设计新物理）

| # | 缺口 | 后果 | 建议（只列，不实现） |
|---|---|---|---|
| G1 | **没有台阶/坡/分区地面** | 花圃 0.26 m 只能是墙；整图 `climb_step` 门在第一关不存在；毯缘 0.06 m 要么当墙要么纯视觉 | 接受（本关全平）；要台阶须另立任务，改 `GroundContactMath` + 扫掠，不在本方案 |
| G2 | 地面只能靠 **Inspector 上的两个字段**设，代码里没有 public setter | 「关卡边界组件」无法在运行时决定地面高度；换地面高度要手工改 Inspector | 若以后要每关一套地面参数，给 motor 加 `SetGroundPlane(normal, offset)`（新接口，需评审） |
| G3 | 8+8 上限 + 只有 `ClearObstacles()` | **静态盒**（`AddBox` 登记的布景）一旦注册就永远在，没有删单个的接口；**动态挡路盒**（`propBounds` → `SetBlockingBoxes`）每帧重扫、会自己消失。所以"会消失的门"只能用动态挡路盒做，被登记成静态盒的门件就永远挡路 | 本关的门走动态挡路盒（§3.5），不受影响；整图若要"地基类门件"，仍需 [REGION_MAP_PLAN](REGION_MAP_PLAN.md) §4 G4 的 `MapObstacleHost`（`IsAttached` + 整组重注册） |
| G4 | 平面是无限半空间 | 同场景多岛时平面会"越过"岛界；多 motor 是唯一隔离手段 | 一岛一 motor；把岛间距离写进场景约定 |
| G5 | **没有通用的「关卡边界」组件**，只有 Lab 专用的 `StoryLevelLabBootstrap`（4 个 `BoxCollider` + 自测驱动/日志） | 正式场景不能继续挂 Lab 脚本（HUB_LOOP_PLAN §7 配套 1 已要求转正） | §5.2 C1：新建 `LevelArenaHost`（平面数组 + 盒数组 + `TryBeginPlaying`），见清单 |
| G6 | 盒只有 AABB、没有旋转盒 | 45° 摆的布景只能用外包 AABB，比实际胖 | 本关角内唯一 45° 件（`collapsed_bench`）在角外，不受影响；整图另案 |
| G7 | 静态布景的"多盒"没有契约 | `layout.json` 按"1 structure = 1 碰撞盒"计数（`validate_map` 第 14 条），一件多盒会破账 | 与本关无关（4 个盒各属一件）；整图另案（`COLLIDER_LAYER.md` §10-4 已列为未决） |
| G8 | **没有"可爬的结构物"**：斜板/叠放/桌面层都做不出来 | 支撑永远对**一张全局平面**求投影（proxy 点只来自**已吸附件**，不来自场景物体），盒是实心的、无 step-up；`maxLiftPerStep = 0.1` 只沿法线。所以原作的"沿板上桌"在本关**不存在**（§3.5.1） | 接受：本关的竖向层次只到 L3 布景（§3.5.3）；要真爬，至少补"场景物体参与支撑 + 分区高度场 + step-up"三件，见 §3.5.3 末段 |
| G9 | **挡路盒只有 6 格，且没有厚度过滤** | ① `MaxBlockingBoxCount = 6`：门堆 4 件已占 4 格，附近再有 3 件未解锁大件就会把门件挤出选择集 → 门出现"漏缝"；② `UpdateBlockingProps` 对**任何**超限件都建盒（只判 `IsBeyondAbsorbVolumeLimit`），不看到底多薄 → 一块 2 cm 的平铺野餐毯在起手时也是一堵 2 cm 高的墙（观感荒诞，还把"通路层"变成"墙"） | ① 作者侧：门堆 1.5 m 内不放其它未解锁件（§3.5.4）；② 代码侧：C9 —— 挡路盒高度低于 `max_route_step_m` 量级的件不登记，让 L1 贴地层真正是"通路" |

## 5. Q5 迁移步骤

### 5.1 Unity 操作员步骤（按顺序，每步带验收）

> 前置：只有一名 agent/操作者动 Unity；每次改动后 `state` + `read_console` 各看一次；`ReferenceCamera` 现在是 `{fileID: 0}`（未接），本流程最后一步补。

| # | 做什么 | 验收（截图点 / 回读项） |
|---|---|---|
| **S1** | **场景转正**：`Labs/StoryLevel/StoryLevel.unity` + `.meta` 一起 `mv` 到 `Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity`（GUID 不变）；`StoryLevelLabBootstrap.cs`+`.cs.meta`+`.asset`+`.asset.meta` 一起搬出 `Labs/`（文件名与类名一起改成 `StoryArenaHost`，见 C2） | 场景能打开、无 Missing Script；`Labs/StoryLevel/` 已空；`Assets/` 下只有一张正式场景；Console 无 `outdated behaviour version` |
| **S2** | **地面**：`Ground` 的 `localScale` 从 `(20,0.4,20)` 改成 `(13.6,0.4,13.6)`、`localPosition` 从 `(0,−0.2,0)` 改成 `(−12.7,−0.2,−12.7)`（顶面仍在 `y=0`） | 顶视截图：地面正好覆盖 `[−19.5,−5.9]`；Play Mode 里 `[story-lab] … bottom=` 一行恒为 `0.000`（±0.002） |
| **S3** | **四道边界的玩家墙**：`Wall_South` → `(−12.7,1,−19.4)`、`Wall_North` → `(−12.7,1,−6.0)`、`Wall_West` → `(−19.4,1,−12.7)`、`Wall_East` → `(−6.0,1,−12.7)`；南北墙 `scale (13.6,2,0.4)`、东西墙 `scale (0.4,2,13.6)`；**把它们从 `obstacleBoxes` 数组里移除**（球改用平面，见 C1） | 目视：四面墙首尾相接、无缝隙；`[story-arena] planes=5 boxes=4` 一行；Play Mode 里把球顶到墙边，球心到墙面距离 = 碰撞半径（截图 + `center=` 读数） |
| **S4** | **角内布景**（按 `layout.json` 真坐标）：花圃台 `(−10,0.13,−8.55)` `4.0×0.26×4.9`；树根丘 `(−18.75,0.07,−18.75)` `Ø2.5×0.14`；角树 `(−18.75,0,−18.75)` `Ø0.5×9`（灰盒＝圆柱/胶囊即可）；指示牌 `(−13.2,0.6,−6.6)`；出生地贴 `(−14.5,0.01,−14.5)` `Ø1.8`；毯缘一段 `x∈[−13.5,−7]` 贴 `z=−6.0`（纯视觉、**不加 Collider**，见 §6 U5） | 顶视截图与 `Labs/PicnicLawnGreybox/PicnicLawnGreybox_top.png` 的西南角**逐件对齐**（同一坐标，误差 ≤0.1 m）；`read_console` 无 Collider 警告 |
| **S4b** | **结构门件（§3.5）**：在壁龛南口 `z ≈ −10.6` 横排 4 件——野餐篮 ×3（各 0.45 长）+ 保温箱 ×1（0.55 长），贴地 `y = 高/2`、首尾相接、**净宽做到 0.10 m**；壁龛内摆 6 件（离门 ≥1.0 m）；花圃台顶摆 5 件纯布景（**不进宿主、不给 Collider**） | 顶视截图：门堆总长 1.90 m、净宽 0.10 m（±0.02）；起手直冲门洞被挡住（`rej=` 一行 `TOO_LARGE` + 截图）；壁龛内 6 件中心离门中线 ≥1.0 m |
| **S5** | **道具重排**：`PickupField` 下 31 个定义 / 110 件按 §3.4 的 7 个区重新摆；删掉落在花圃足迹/出生净空里的件；2 m 缝里只留门堆 4 件 + 壁龛 6 件；薄片与水果换真实网格（有模型的 16 个定义），其余保留方块占位但**把 `localScale` 调成该件的真实 AABB**（真实尺寸口径） | 逐件回读：110 件中心 `x ∈ [−19.3,−6.1]`、`z ∈ [−19.3,−6.1]`（四面墙内侧各留 ≥0.1 m）；花圃足迹内 0 件；2 m 缝内**只允许门堆 4 件 + 壁龛 6 件**（逐件名单核对）；球起点 1.0 m 内 0 件（写一条 4 行的 Python/Console 脚本回读） |
| **S6** | **宿主数组重同步**：对 `PickupField` 上的 `PickupFieldHost` 跑 `VRC Katamari/Pickup/Sync Prop Arrays From Children`；**先确认值表**（`fruit s 1 / fruit m 4 / …`）与 §3.4 的逐件单位一致 | Console：`[PickupFieldHost] ready props=110 submittable=110`；`valueUnits` 合计 **412**；无 `no value-table row` 警告；无 `duplicate itemId` |
| **S7** | **motor 参数**：`startCenterWorld = (−12.5, 0.15, −12.5)`；`maxCoreRadiusWorldM = 1.1`（第一关 2.20 上限，(n)）；`groundPlaneNormal/Offset` 保持 `(0,1,0)/0`；`contactRadiusWorldM` 保持 0（(i)） | `read_console`：`[story-lab] setup … startDiameter=0.29974`；Play Mode 首帧球心 `y ≈ 0.15` |
| **S8** | **出生点**：`Spawn` 移到 `(−14.5, 0.5, −14.5)`、yaw 45°；`SceneDescriptor.spawns` 仍指向它 | Play Mode 出生位置到 `Spawn` 距离 <0.5 m；面朝东北（截图） |
| **S9** | **骑乘点**：`RideSeat` 仍在 `RideRoot` 下（followRig 每帧写位置/yaw）；把 `RideRoot`/`RideSeat` 的 `localPosition` 从球后上方偏移**清零**（(k) 的硬约束：不许再叠一层） | 入座后眼点在球后上方，`[ride] seated entered` 一行；截图：球在画面前下方、不糊镜头 |
| **S10** | **边界平面注册**（C1 落地后）：把 P0–P4 的 normal/offset、B1–B4 的 min/max 填进 `LevelArenaHost` 的数组 | `[story-arena] planes=5 boxes=4`；把球分别开到四面墙，读取 `center=` 与墙面距离 = 碰撞半径（±0.01 m） |
| **S11** | **门/挡路**：本关没有 `absorb` 尺寸门，但**有 4 个结构门件**（§3.5）；确认 `PickupFieldHost` 的 `blockingDisabled = false`。起点时 `D_req > 0.29974` 的件（西瓜 3、篮子 3、保温箱 1、皮球 4、洒水壶 2、盆栽 2、餐盘 4、碗 4、餐巾 5…）都会进这套，宿主每帧只登记**最近的 ≤6 件**，所以同一时刻最多 6 个动态挡路盒 | Play Mode：① 起手撞门堆被挡住（截图 + `rej=` 一行 `TOO_LARGE`）；② 长到 0.83 m 后吃掉篮子、能进壁龛（截图 + `absorbed=` 跳数）；③ 西瓜/皮球同理被挡到解锁 |
| **S12** | **首轮 Play Mode（不调数值）**：跑满 120 s，记录到达各直径的时刻、结束直径、件数、`GetBankedScoreUnits()==0` | 截图：HUD 三行；Console：`[story-lab] t=… diameter=… absorbed=…` 每秒一行；结束直径写进本轮报告（`Docs/Evidence/`；预期 ~1.4–1.48 m，见 §6 U4） |
| **S13** | **角度/视线**：从出生点、花圃西侧、2 m 缝入口各截一张图；确认花圃台把东北方向切开、角树在视野内可定向 | 三张截图存档；"门内看得见吃不到的大件"（3.6 m 长凳）在东北角外可见 |
| **S14** | **回读死键**：`grep -n "maxItemSizeWorldM\|absorbSizeRatio" StoryMode.unity` 应为 **0 命中**（现状已 0，转正后必须还是 0）；`maxItemVolumeWorldM3: 512` / `absorbVolumeRatio: 10` 仍在 | 0 命中；两个新字段在位；死键只剩 `CpuSkirmish` 一处（`BallPlayground` 已 2026-11-15 退役删除；别处，不属本关） |
| **S15** | **`ReferenceCamera`**：`SceneDescriptor.ReferenceCamera` 指向场景主相机（`Labs/README.md` 的两件必备组件之一） | Play Mode 不再出现 `Reference camera is not set` |
| **S16** | **Build & Test**（只能用户点） | 出生在西南角、点球入座、滚到 1.25 m、到点冻结出结果卡；结果记 `Docs/Evidence/` |

### 5.2 实现者代码改动清单（只列，不写代码）

| # | 文件 | 改什么 | 为什么 / 验收 |
|---|---|---|---|
| **C1** | 新 `Runtime/Story/LevelArenaHost.cs`（UdonSharp，`BehaviourSyncMode.None`） | 序列化 `KatamariBallMotor motor`、`Vector3[] planeNormals`、`float[] planeOffsets`、`BoxCollider[] boxes`（或 `Vector3[] boxMin/boxMax`）；`Update` 首次 `GetSemanticDiameterWorldM() > 0` 后注册 5 平面 + 4 盒、`TryBeginPlaying`、写一行 `[story-arena] planes=… boxes=…`；超 8 时按电机返回值报错 | 取代 `StoryLevelLabBootstrap` 的注册段：把 Lab 专用墙登记换成通用「关卡边界」。验收：5/4 计数行正确；第 9 个盒被拒并报 `[story-arena] box capacity exceeded` |
| **C2** | `Labs/StoryLevel/StoryLevelLabBootstrap.cs` → `Runtime/Story/StoryArenaHost.cs` | 删 `scriptedDrive`/`DriveScripted`/`Report`/`GetStartDiameter`/`cameraFollow`（Lab 自测件）；保留"注册 + `TryBeginPlaying`"，或整体并入 C1 后删除。**文件名与类名必须一起改**（Unity 要求 MonoBehaviour 类名 = 文件名），`.cs`/`.cs.meta`/`.asset`/`.asset.meta` 同搬，GUID 不变（场景与 program asset 的引用才不会断） | HUB_LOOP_PLAN §7 配套 1 已要求；正式场景不挂叫 Lab 的脚本 |
| **C3** | `Runtime/Pickup/PickupFieldHost.cs` | **本关不需要改**（`activationRadiusM = 1.6` 对最大 0.30 m 的件足够：reach = 球碰撞半径 + 包围球半径 ≤ 0.74 + 0.26）。整图大件才需要"每件激活半径 = 当前半径 + 该件包围半径 + 0.3"（REGION_MAP_PLAN G8） | 明确写出来，防止有人顺手改半径把本关手感搅乱 |
| **C4** | `Editor/Pickup/PickupFieldSync.cs` | **重排后必须重跑一次**，但**先确认值表**；`@size=` 通道应改成"不许再用于门件"（体积门下立方体会把 3.6 m 长凳写成 1.95 m³ = 要 2.60 m 球） | 重跑会按 AABB 重分档 → `valueUnits` 可能整体变化（真实尺寸后大多数落 s/m），**412 会变**；因此重跑必须与关卡阈值重定一起做（§6 U4） |
| **C5** | `Editor/Pickup/PickupFieldAuthoringRules.cs` | ① `SmallTierMaxExtentM = 0.21` / `MediumTierMaxExtentM = 0.76` 是**长度**分档；若要"大件更值钱"，需改成按 `a_eq`/`volume` 分档 + 扩表。② **值表缺 `container m`**：野餐篮 0.45 m、保温箱 0.55 m 都落 `m` 档，而 `ValueTable` 只有 `container l`（16）——不加这一行，4 件门件会被同步工具当 rejected row **直接丢掉**，门就没了 | 验收：同步报告里 4 件门件都成行、`valueUnits` 各 16；无 `no value-table row for kind 'container'` |
| **C6** | `@size=` 约定（同上文件 + 场景命名） | 退役：门件的 `propBounds` 用真实 AABB，挡路用 `SetBlockingBoxes`（挡路接线那条线在做）；过渡期至少**禁止**在体积门下给 `@size=` 写"只比最长边小一点"的值 | 否则门件会变成永久死件（`@size=1.25` 长凳 = 1.95 m³） |
| **C7** | `Runtime/Story/StoryRoundController.cs` | **不改**：星级/评语属可拆卸评分模块（(m)），回合核心不认识它；本关只搬几何 | 明确写出来，防止有人为了"新场地"动状态机 |
| **C8** | 数据侧（建议，非本关硬需求） | 角内布置目前**没有数据文件**；若要 `--check` 兜底，需要给第一关一份自己的布置数据（例：`Docs/Design/StoryMode/StoryLevels/story_01/placement.json`）与一个校验（件数/单位/净空/间距），而不是把布置留在场景里 | 今天只能靠 S5/S6 的逐件回读；这是本方案最大的"不可回归"风险 |
| **C9** | `Runtime/Pickup/PickupFieldHost.cs`（`UpdateBlockingProps`） | 挡路盒加一条**厚度过滤**：`propBounds.y` 低于 `max_route_step_m` 量级（≤0.05 m）的件不建盒，让贴地件（平铺毯、托盘、纸片）真正是"通路"而不是 2 cm 墙 | 与 §3.5 的 L1 层绑定；属挡路接线那条线的文件，须协调。验收：起手碾过平铺毯不被挡、仍能在 0.99 m 后吸走 |

## 6. Q6 风险与未决（需要用户拍板，带选项与代价）

| # | 问题 | 选项 | 代价 |
|---|---|---|---|
| **U1** | **场地多大** | **A（推荐）160.0 m²**：`x,z ∈ [−19.4,−6.0]`，边界全落在真实几何上（西/南墙、北毯缘、东 2 m 缝） | 110 件必须重排（S4b/S5，约 0.5–1 个操作员工作日）；密度从 ≈0.29（当前 19.6×19.6 内空）→ 0.69 件/m²，节奏会变（§6 U4） |
| | | B ≈ 187 m²：东界推到 `x = −4.0`（15.4 × 13.4 − 花圃 = 186.8），含中央草坪西南角与 `collapsed_bench` | +27 m²，但多一个 3.6 m 死件（`collapsed_bench`）挡在东北角，且要新增东墙；重排照样要做 |
| | | C 384 m²：整 20×20 象限（零重排，整体平移 `(−9.8,0,−9.8)`） | 两堵墙（`x=0`/`z=0`）把西毯、南篮子区、中央草坪各切一半；毯缘变成场内一道 6 cm 墙 —— 与"真实布局"目标冲突 |
| **U2** | **110 件是否重排** | 跟 A 走：重排，坐标按 §3.4 | 见 U1-A |
| | | 不重排：只能选 C | 见 U1-C |
| **U3** | **4 支暖壶怎么办**（现状 0.9 m 立方体 → `D_req 2.406` > 2.20，**永久吸不到**，涉及 64 u；**与 U8 一起拍**：若选 U8a，暖壶已不在第一关，本条自动关闭） | **a（推荐）按真实尺寸建模 0.12×0.12×0.30**：`D_req 0.435`，412 u 保住，与"真实尺寸"决定一致 | 需要一个模型（Blender 改造，属美术线，不在本方案） |
| | | b 保留 0.9³ 占位：4 件变死件 → 可达 348 u、上限 1.408 m，★★/★★★ 必须重定 | 相当于把 412 的门槛体系打掉 16% |
| | | c 把暖壶移出第一关：106 件 / 348 u | 同上，且 `container` 这个 kind 在第一关消失（关卡 1 不受影响，关卡 2 也不用 container） |
| **U4** | **120 s / 1.25 m 三档是否先测再议** | **A（推荐）先测一轮，不改数**（(p) 现行）：S12 跑一次，用实测决定 | 一次 Play Mode 的 2 分钟；结论可能指向"时间太松" |
| | | B 现在就按新密度把时限/星级重算 | 违反 (p)，且新场地的手感还没测过，等于盲调 |
| **U5** | **PicnicLawn 全图与 3.20 m 上限** | **A（推荐）本关只管角，全图另案**；但先确认一个事实：`layout.json` 写 `ball_max_collision_diameter_m = 2.2`，而 (n)/`PROP_SET_PLAN §2.8.5` 说野餐桌/长椅/长凳改由 "PicnicLawn 3.20 m 上限"承接 —— **两处口径不一致**，且校验器按 2.20 判路线余量 | 若真要 3.20，PicnicLawn 的路线净宽（3.40/4.00/5.00）与余量（1.20）要重算，校验器会先报 warning/error；不确认就会在"门件该不该可吸"上反复 |
| | | B 现在就把 PicnicLawn 改成 3.20 | 需要同时改 `scale`/路线/关卡快照/README 手抄值，且两份关卡 JSON 的 `map_snapshot` 要重钉（不在本次写范围） |
| **U6** | **毯缘 6 cm 算墙还是纯视觉** | **A（推荐）纯视觉、不加 Collider**：球滚到边界时被北平面挡住，视觉上"毯子在墙外"，不出现"巨球被一块布挡住" | 需要毯子不带碰撞体（今天它就是 `plane_raised` + 0.06 m，本来也不是玩家必经） |
| | | B 当墙：毯缘进碰撞集 | 吃掉 1 个盒；观感"布墙" |
| **U7** | **第一关是否也用整图的 452 件散布** | **A（推荐）不用**：角内只放本关 110 件；整图的 `spawn_sw`(12) + `tree_roots_sw`(14) + `flower_bed_sw`(12) + `fringe_west/south` 的角内段（各约 6–7 件）合计 **≈50 件 / ≈50 单位**会白送进角，412 立刻失真 | 与"一个角就是一张小图"一致 |
| | | B 用整图散布替代 110 件 | 关卡阈值、件数目标、关卡 2 的水果计数全部要重算 |
| **U8** | **结构门件怎么算进 110/412** | **a（推荐）3 篮 + 1 箱顶替 4 支暖壶**：件数仍是 110、单位仍是 412，顺带把 U3 的 64 u 死账换成"一扇会消失的门"（门后 28 u 是壁龛内容，从 `outer` 挪过去） | 需要 2 个新模型（野餐篮、保温箱，真实尺寸）+ 值表 `container m` 一行（C5） |
| | | b 结构件**另加**（不顶替）：114 件 / 476 u，★1.25 = 51.7%、上限 1.55 m | 关卡阈值要重定（与 (p)"现在不调数值"冲突），且 ★ 掉到 52% 偏松 |
| | | c 结构件只当布景、不进宿主 | 没有"挡路 → 食物"的翻转，等于放弃用户补充的那条原则 |

**其它风险（不需要拍板，但必须知道）**：

1. **密度翻倍**：≈0.29 → 0.69 件/m²（110 ÷ 160.0），`120 s` 的达标会更容易（这是 S12 要测的第一件事）。
2. **球被 6 cm 毯缘挡住**（若选 U6-B）或**自己陷进毯子 6 cm**（若选 U6-A 且毯子在同一平面）——两者都是视觉问题，不是物理问题。
3. **指示牌数据不自洽**（`position.y = 0` vs 1.2 m 高）：本关按柱脚 0 建模；地图数据留给 owner。
4. **`PickupFieldSync` 重跑会改 `valueUnits`**：真实尺寸落地后大多数件落 s/m 档，412 会变；重跑必须与阈值重定同批做。
5. **单人专属**：同场景两块内容、同一台 Udon VM，多人时两个人的球会叠在一起（球无 Collider），HUB_LOOP_PLAN §6.4 已判"现阶段明确只支持单人"。
6. **"门封死"是采样结果，不是几何保证**：吸收门看 `propBounds` 体积，挡路盒只取最近的 ≤6 件（§4.3 G9）。门堆附近必须保持干净，验收要看截图，不能只看数据。
7. **门后的 28 u 在 0.55–0.82 m 窗口期是"看得到进不去"**：这是有意的奖励房，但必须确认它不在 ★ 的必经路径上（245.99 u < 门外 384 u，已算过）。
8. **结构件的"竖向"只有 0.22–0.45 m**：篮子躺倒 0.22 m、箱子 0.30 m，视觉上是"一堆野餐装备"，不是原作那种"台面 + 台面杂物"的高度。L3 台面层只能看不能上（§3.5.3）。

## 7. 本次写范围、改动与校验结果

### 7.1 改了哪些文件（只有两个，且都不含机器可读数字）

| 文件 | 改动 | 为什么不动机器可读文件 |
|---|---|---|
| `Docs/Design/StoryMode/PICNIC_CORNER_LEVEL_ONE.md` | 新建（本文） | —— |
| `Docs/Design/Maps/PicnicLawn/README.md` | **追加**一节「§18 体积口径下的可吸性审计（2026-11-15）」，指向本文；既有章节（含 §3.3 被测试逐字比对的可行性表）**一字未动** | —— |
| `Docs/Design/Maps/PicnicLawn/layout.json` | **未改** | 被 `Docs/Design/GameModes/Levels/story_01_bench_size.json` / `story_02_fruit_ten.json` 的 `map_snapshot.layout_sha256` 钉住；改一个字节 → `validate_levels.py` 报「地图已变更」、`Docs/tests/test_levels.py` 变红；重钉要改 `Levels/*.json`（不在写范围，且 (g) 明确禁止工具自动刷新哈希） |
| `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` | **未改** | 同上（`catalog_sha256`）；另外它的 `game_longest_dim_m` 同时喂校验器第 11 条与生成器的脚印模型，改了会让 `build_greybox --check` 与 README 可行性表一起失效 |
| `Docs/Design/Maps/PicnicLawn/unity_greybox.json` | **未改** | 生成物：`--check` 要求它与作者数据逐字节一致，手改即失效；重生成要先改上面两个文件 |

### 7.2 本轮实跑的命令与结果

```text
python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/PicnicLawn
  -> 结论: OK  (errors 0, warnings 0)   立面：可走坡 8（最陡 11.2%）/ 爬升台阶 4 / 障碍 38
python3 Tools/mapbuild/build_greybox.py --check
  -> OK: unity_greybox.json 与作者数据一致
python3 Docs/tools/validate_plan.py
  -> PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
python3 -m unittest discover -s Docs/tests
  -> Ran 279 tests ... OK
python3 -m unittest discover -s Tools/mapbuild/tests
  -> Ran 120 tests ... OK
python3 Docs/Design/GameModes/validate_levels.py
  -> 结论: OK（错误 0、警告 0；地图 452 件 / 1284 单位）
```

### 7.3 没跑 / 跑不了

- **Unity 编译、UdonSharp 编译、EditMode/PlayMode、Build & Test：NOT_RUN**（本 agent 不是 Unity 操作者）。
- **角内布置的件数/净空/间距：NOT_RUN**（没有数据文件，只能 S5/S6 逐件回读）。
- **`D_req` 表**：算术（`D_req = (60·V/π)^(1/3)`），可用 §3.3 的 AABB 手算复核；`估` 行是立方体上界，不是实测。
- **结构门件的"挡路 → 可吸"翻转、0.10 m 门缝是否真封死、6 格挡路盒会不会漏：NOT_RUN**（要 Play Mode；§3.5 的算术只证明阈值，不证明采样）。
- 本文不改 `tasks.json`，所以计划校验与基线一致不是"本文的功劳"。

### 7.4 实施顺序建议（与并行 agent 的接缝）

1. **C1/C2 与 S1–S3 先做**（边界组件 + 场景转正），因为它们决定"球能不能被围住"，且与地形无关。
2. **S4/S4b/S5（布景 + 门堆 + 110 件重排）与 U8（两个结构件模型）并行**；重排完再跑 S6 同步。
3. **挡路接线那条线**落地后，S11 的验收才完整（今天 `PickupFieldHost` 的 `SetBlockingBoxes` 已经在，缺的是 `COLLIDER_LAYER` 的碰撞体接法与 `MapObstacleHost` 的门重注册）。
4. **主房间运行时（`Runtime/Hub/`）**只消费"场景里有一个关卡岛"，与本方案不冲突；但两边都不能改 `StoryRoundController` 的状态机（(m)）。
5. **与 `Docs/Research/KatamariMaps/LEVEL_CONSTRUCTION.md`（研究员在写）对齐**：本文 §3.5 只用了用户转述的原则（三种身份、台球桌/斜板的例子），**没有引用任何未完成的研究结论**。对齐点三处：① 原作里"斜板/叠放"占多大比重（决定 G8 值不值得立项）；② 原作的"可吸物搭场景"是不是每件最终都可吸（决定 L3 布景层是否可接受）；③ 原作的房间/走廊尺度（决定 U1 的 160 m² 是否偏小）。研究若给出不同答案，改的是 §3.5 与 U1，**不改 §4 的代码事实**。
