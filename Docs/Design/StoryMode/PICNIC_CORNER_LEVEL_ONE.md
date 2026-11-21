# 第一关在野餐一角（PicnicLawn 西南角）：场景、范围与验收

> 状态：`PROVISIONAL`。所有米数、件数、单位都是**本项目暂定值**，不是原作数值。Unity 编译、`run_tests` 与 Play Mode 验收以 `Docs/Evidence/` 与 `tasks.json` 的记录为准。
> 范围：第一关（`story_01_bench_size`，滚到 1.25 m / 120 s）放在 PicnicLawn 西南角；回答场景归属、生成器、角内范围、墙与边界、导入与验收、未决项。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。**符号名才是契约**，文中不写行号。
> §4 的支撑能力：静态盒顶支撑 + 台阶 0.25·D + 重力下落、不可吸道具顶面当支撑（≤6 件）、斜面注册成支撑面；场地注册归 `Runtime/Story/LevelArenaHost.cs`；搭地图真源是 `Docs/Design/Levels/story_01_bench_size/placement.json`，导入器 `Editor/LevelBuild/PlacementImporter.cs`。


## 0. 六问一句话结论

| # | 问题 | 结论 |
|---|---|---|
| 1 | 场景归属 | **正本 = `Worlds/StoryMode/StoryMode.unity`**，角内几何按地图真坐标长在它里面；不翻新、也不转正 `PicnicLawnGreybox`，不新建第二张场景（§1）。 |
| 2 | 生成器还能不能用 | **不能用它建这一关**：`PicnicLawnGreyboxBuilder` 是「整图 · 从零重建 · 只产几何 · 会 `SaveScene`」，重跑必然覆盖手工接线；`--check` 只证明生成物与作者数据一致，与场景接线无关。数据侧只作 `--check` 回归，不重跑（§2）。 |
| 3 | 一角的范围 | **西南角** `x∈[−19.40,−6.00]`、`z∈[−19.40,−6.00]`：毛 179.56 m²、扣花圃 19.60 m² 后**净 160.0 m²**；角内只有平地 + 花圃台（0.26 m）+ 树根丘/角树 + 指示牌 + 毯缘一条线；角内内容 **127 件 / 1500 u / 12 区**（真源 `placement.json`：110 件小道具 412 u + 17 件可吸家具 1088 u），其中 4 件是**结构门件**（§3.5.3）。 |
| 4 | 地形与场地注册 | 边界是 **4 个竖直平面**（球）+ 4 面既有墙碰撞体（玩家）；地形现在**能上**：静态盒顶与「吸不动的道具顶」都能当支撑；道具分角色的台阶判据 `rise ≤ 0.25·D`（含等号）——花圃台 0.26 需 `D ≥ 1.04`，链「台顶 0.26 → 躺椅 0.35 → 折叠桌 0.70」需 `D ≥ 1.40`；斜板是实体 slab OBB（`rampBoxes` → `RegisterRampObb`），坡脚没有隐形墙。场地注册归 `LevelArenaHost`（4 平面 + 4 盒 + 2 斜面 + 11 kind），不是 Lab 脚本（§4）。 |
| 5 | 导入与验收 | 导入（两次幂等 `+0 ~0 -0`）与 Play 验收 E1–E11（§5）。 |
| 6 | 未决 | 场地 160 m²、127 件 / 1500 u、星级暂不重定已由 `OPEN_DECISIONS` (q) 与 [PICNIC_CORNER_LAYOUT](PICNIC_CORNER_LAYOUT.md) §9 拍板；**仍然开着的**是 120 s 与星级要不要按新内容重定（§6 U4）。 |


## 1. Q1 场景归属：哪个 `.unity` 是正本

### 1.1 三条硬约束（先看这个，它把选项砍掉一半）

1. **一个世界 = 一个 Unity 场景。** 运行时不能换场景、脚本不能把玩家送去别的世界，跨世界只有 Portal（[HUB_LOOP_PLAN](HUB_LOOP_PLAN.md) §1 已逐条查证并列出官方来源）。所以「主房间 + 各关卡区域」只能同场景共存。
2. **一个场景里可以有 N 个「岛」，但每个球各有一套 motor**，motor 的障碍表（8 平面 + 8 盒）是**每实例**的；平面是无限半空间（§4），所以岛与岛之间必须靠距离隔离，不能靠「这块归这个 motor」的几何分块。
3. **接线只存在于场景文件里**：宿主平行数组、`propColliders`、motor/Station/输入/HUD/回合控制器与 `LevelArenaHost`（墙平面 Transform）都在 `Worlds/StoryMode/StoryMode.unity` 里；数组内容由导入器从 `placement.json` 重写，组件与引用只有这一份。

### 1.2 正本与其它场景

| 场景 | 角色 |
|---|---|
| **`Worlds/StoryMode/StoryMode.unity`** | 正本：角内几何与第一关内容（127 件）长在它里面，主房间岛 `(1200,0,0)` 也在同一场景（[HUB_LOOP_PLAN](HUB_LOOP_PLAN.md) §7）。 |
| `Labs/PicnicLawnGreybox/PicnicLawnGreybox.unity` | 全图数据预览：`Python 数据 → unity_greybox.json → 编辑器生成器`的产物，`GreyboxReport.json` 与俯视图是它的验收物。生成器 `SaveScene` 从零重建，必然抹掉手工接线，且不产任何 Udon，所以不转正、不接线。 |

不新建第二张关卡场景：两张各改一半的关卡场景会让 GUID 分叉、所有引用要重指。

角内几何直接取 `layout.json` 的真坐标（花圃 `(−10, 0.13, −8.55)`、角树 `(−18.75, −18.75)`、出生地贴 `(−14.5, −14.5)`…），以后整张 PicnicLawn 进同一场景时这一角已在正确位置。

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

### 2.2 数据里哪些概念过期（逐项判定）

| 数据项 | 位置 | 现状 | 新口径 | 这次动不动 | 为什么 |
|---|---|---|---|---|---|
| `scale.item_tier_pickup_diameter_m`（s 0.08–0.30 / m 0.32–0.95 / l 1.00–1.95） | `layout.json:49` | **长度**档位：`clamp(game_longest_dim_m, 档窗口)` 当"拾取门槛" | `Req_mm = floor(1000·cbrt(60·k·abc/π))`，`k` 缺省 0.70 | **不动** | 它是内容侧分档、也是校验器第 11 条唯一的判据；改它必须同时改校验器与声明值（值表已删），而校验器不在写范围 |
| `game_longest_dim_m`（目录列） | `prop_catalog.csv` | 「游戏尺寸」（比真实尺寸放大，例：`apple` 0.08→0.30） | 真实尺寸建模、`localScale = 1`、`propBounds` = 模型 AABB | **不动** | 同一列同时喂校验器与生成器的脚印/间距模型；改它会让 `build_greybox --check` 与 README §3.3 的可行性表一起失效（那张表被 `ReadmeFeasibilityTableTests` 逐字比对） |
| `real_longest_dim_m`（目录列） | `prop_catalog.csv` | 真实最长边，只有一列、没有三轴 | 需要三轴或 `volume_m3` | **不动** | 缺第三轴，任何 `a_eq` 都是估的；按「不手抄推导数字」的规矩不写 |
| `@size=<米>` 立方体写法 | 场景道具命名（`Editor/Pickup/PickupFieldAuthoringRules.TryParseDeclaredSize`） | 「挡路 3.6 m、拾取 1.25 m」→ 把 `propBounds` 记成 1.25³ = 1.95 m³ | 体积门下 1.95 m³ 需要 **2.60 m** 球，比 2.20 上限还大 —— 这个通道**已经把门件写成死件**（(l) 原文点名） | **不动（旧编号 C6；现行改动清单见 §5.8 的 K1–K7）** | 正确做法是「`propBounds` 说真话（模型 AABB）+ 挡路盒另立」（挡路接线那条线在做）；改 `PickupFieldAuthoringRules` 属代码，不在写范围 |
| `maxItemSizeWorldM` / `absorbSizeRatio` | `Labs/CpuSkirmish/CpuSkirmish.unity` | 死键，Unity 忽略、回落 512 / 10 | 已删字段 | **不动** | 在场景/prefab 里，属编辑器产物；删死键是 Unity 侧顺手活（(l) 已列为"仍待做：7 处场景序列化覆盖"），且不属于本关场地 |
| `maxItemVolumeWorldM3` / `absorbVolumeRatio` | 同上（场景 `512` / `10`） | **两个字段已删除**：门用 `k·a·b·c` + 整 mm，比率/上限是 `PickupAbsorbMath` 的常量；场景里的残留键是 Unity 忽略的死键 | — | **不改场景** | 见 [`ABSORB_GATE_ORIGINAL.md`](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)；本关 `defs[]` 不写 `volumeK`，全部按缺省 0.70 |
| `pickup_dim_m`（可选列） | 目录 schema | 故事图专用「声明拾取尺寸 ≠ 外形」通道 | 体积门不需要它；需要的是真实 AABB + 挡路盒 | **不动** | PicnicLawn 目录里没有这一列；它有在故事图 fixture 里，与本次无关 |
| 障碍/平面的 `collision` 声明 | `layout.json` 结构 | 无（0/8、0/8） | 第 14 条运行时预算 | **不动** | PicnicLawn 是全图 PvP 图，不是这一关的关卡数据；这一关的边界账在本文 §4 |

**结论**：数据文件里真正「过期」的是**概念**（线性档位、`@size` 立方体、game/real 双列），而它们的消费方（校验器 `validate_map.py` 第 11 条 + `PickupFieldAuthoringRules` + `build_greybox`）**都还按这些旧概念判**。按「不改校验器就只改不受影响的字段」的规矩，本轮**一个机器可读字段都不改**。

### 2.3 取舍：「翻新数据 → 重跑 → 再接线」还是「不重跑、手工迁移」

| 路线 | 适用 | 代价 |
|---|---|---|
| **翻新数据 → 重跑生成器 → 再接线** | 整图 PicnicLawn 的下一版灰盒（T21） | 必须先补校验器（§2.5）、补生成器的「保留手工接线」能力（今天没有），再重做全部 Udon 接线；对角内第一关是纯亏 |
| **不重跑生成器、手工迁移一角**（推荐） | **本关** | 现在的做法是**导入器按 `placement.json` 建 127 件**（§5.1），不是手工摆；接线、宿主数组、交付路径全部保住 |
| 折中：数据照旧，只跑 `--check` 回归 | 日常 | `python3 Tools/mapbuild/build_greybox.py --check` 必须保持 OK |

**生成器仍然有用的地方**：① `--check` 守住「生成物 = 作者数据」；② `--feasibility` 是唯一被测试保护的容量模型，重排 110 件时**照它的公式算**（`RSA_SITE_AREA = 1.436·S²`，用量 ≤85%）；③ 俯视图 `PicnicLawnGreybox_top.png` 是角内布置的参考底图。

### 2.4 角内布置由谁负责

> 真源是 `Docs/Design/Levels/story_01_bench_size/placement.json`（127 件 / 1500 u / 44 def / 1 ground + 4 boundary + 4 box + 2 ramp，坐标为 PicnicLawn 真坐标），由 `Editor/LevelBuild/PlacementImporter.cs` 单向导入到受管根 `LevelContent`，设计见 [LEVEL_AUTHORING](../Tools/LEVEL_AUTHORING.md)。`layout.json` 只描述整图 PvP 语义，两者不冲突（一个「地图长什么样」，一个「这一关摆哪」）。
>
> **布置的真源不能留在场景里**：兜底是「导入器 + 幂等门 `+0 ~0 -0` + `valueUnits` 回读」，见 §5.1。

### 2.5 校验器需要怎么改（清单）

`Tools/mapbuild/validate_map.py`（**不在写范围，以下只是清单**）：

| 位置 | 现在 | 应改为 |
|---|---|---|
| 第 11 条「档位与球包线」 | `threshold = clamp(game_longest_dim_m, 档窗口)`，要求 `threshold ≤ max_ball` | 用 `a_eq`（或目录新增 `a_eq_m` / `volume_m3` 列）判 `D_req = 2.673·a_eq ≤ D_cap`；长度档位退化为「价值分档」，与可吸性脱钩 |
| 第 14 条「运行时预算」 | 只查 `pickup_dim_m ≤ max_pickup_diameter_m` | 改成体积上界 `a·b·c ≤ (π/6)·D_cap³/10`；`pickup_dim_m` 通道退役 |
| 目录 schema | 必填 14 列，无体积列 | 建议加可选 `aabb_m`（"x;y;z"）或 `volume_m3`，由建模清单生成，禁止手抄 |
| 第 13 条立面分类 | 允许 `climb_step`（台阶可按球径翻越） | **已由运行时兑现**：台阶高 `h < r` 天然滚过，`h < D` 由爬升通道处理（CONTACT_MODEL_V2 §2.7、§3）。`drivable_grade`（12% 可走坡）是玩家侧的 `validate_map` 分类，球没有牵引力模型 |

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

判据：~~`D_req = 2.673 · a_eq`，`a_eq = (a·b·c)^(1/3)`~~ `Req_mm = floor(1000·cbrt(60·k·(a·b·c)/π))`（`k` 缺省 0.70），可吸 ⇔ `floor(1000·D) ≥ Req_mm`；`D_cap = 2.20 m` ⇒ `k=0.70` 时能吸的 bbox 上界是 `V_box ≤ 0.5575/0.70 = 0.7965 m³`（等价边长 0.927 m）。旧式见 [`ABSORB_GATE_ORIGINAL.md`](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。**AABB 来源**：`M` = 模型 `exports/<id>.manifest.json` 实测（`Docs/Art/OriginalModels/props/exports/`）；`D` = `PROP_CATALOG.md` §2.1 的设计三轴；`估` = 只有最长边、按立方体上界（最保守）算。

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
- **本关没有暖壶**：0.9³ 立方体在 2.20 下**永久吸不到**（`D_req 2.406`）。`placement.json` 里没有 `thermos`，`container` 改由 3 篮 + 1 箱 + 箱柜类（`beer_crate / cooler_box / cooler_box_large / crate / crate_large / picnic_basket / storage_bin`）承担，按真实尺寸建模。

### 3.5 结构型可吸物：同一件东西的三种身份，我们能兑现两种

> 设计依据（原作观察，见 `Docs/Research/KatamariMaps/LEVEL_CONSTRUCTION.md`）：**原作的场景除墙/楼梯/地板外，整体是用可吸附物搭出来的**——台球桌上摆杂物、一块长木板从地板斜搭到桌面；小球沿木板滚上桌吃杂物，中期木板本身能吸，后期桌子也能吸。同一件东西**先当通路/地形，吸不动时挡路，长大后变成食物**。

#### 3.5.1 三种身份，我们只能兑现两种

| 身份 | 原作怎么用 | 我们能不能 | 代码依据 |
|---|---|---|---|
| **通路 / 地形**（滚上去、搭坡、叠放） | 斜板、叠箱、桌面 | **能了，但只有四种面**（地面 / 静态盒顶 / 支撑道具顶 / 斜板） | 支撑由接触求解选出：球与静态 OBB / 道具 OBB / 斜板 slab 的接触里最高的 RIDE 接触即支撑，没有支撑就下落（`WriteContactSupport`）；台阶、斜面都是同一套接触，不需要单独的台阶开关。**仍然做不到**：悬空、曲面。详见本文 §4 与 [BallMotor README](../BallMotor/README.md)「支撑与台阶」 |
| **挡路**（吸不动时挡球） | 大件挡住小球的去路 | **能，而且是现成的** | `PickupFieldHost.UpdatePropObstacles` 用吸收门自己的谓词（`PickupAbsorbMath.IsBeyondCachedRequirement` / `IsBeyondAbsorbLimit`）选出吸不动的近处道具，烘成 OBB 经 `motor.SetPropObbs` 进接触求解（`RefreshPropObbs`） |
| **食物**（长大后吸走） | 同一件东西后来被吃 | **能，和上一条是同一个函数** | 球长大 → `limit` = `(π/6)·D³/10` 超过该件体积 → 同一谓词不再成立 → 下一帧扫描撤掉挡路盒、同时吸收门放行。**"挡路"与"可吸"是同一个判据的两面，不会漂移** |

一句话：**我们有竖向通路了，但它只由「地面 / 静态盒顶 / 吸不动的道具顶」这三种平面构成**，靠台阶规则（`rise ≤ 0.25·D`）走上去；「挡路 → 食物」的翻转与吸收门同源、不会漂移。所以本关的结构设计同时是两件事：**搭出可以走上去的台面层级**，和**用可吸物搭出一扇会自己消失的门**。斜面、悬空与叠放仍然做不到。

#### 3.5.2 结构件与阈值（`D_req = (60·a·b·c/π)^(1/3)`）

尺寸取真实尺寸（(o) 建模口径）；`V` 是 AABB 三轴积，`D_req` 既是"可吸"的球径、也是挡路盒消失的球径（同一个数）。

| 结构件 | 真实 AABB m | `V` m³ | `a_eq` | **`D_req`** | 0.30 起手时 | 1.48（本关天花板）时 |
|---|---|---:|---:|---:|---|---|
| 野餐篮 `picnic_basket` | 0.45×0.30×0.22 | 0.02970 | 0.310 | **0.828** | 挡路 | 食物 |
| 保温箱 `cooler_box` | 0.55×0.35×0.30 | 0.05775 | 0.387 | **1.033** | 挡路 | 食物 |
| 折叠凳 `folding_stool`（本关**不用**，留作 B 案） | 0.40×0.30×0.45 | 0.05400 | 0.378 | 1.010 | 挡路 | 食物 |
| 卷起的野餐垫（B 案） | 0.80×0.20×0.20 | 0.03200 | 0.317 | 0.849 | 挡路 | 食物 |
| 托盘（B 案） | 0.55×0.35×0.04 | 0.00770 | 0.197 | 0.528 | **"薄墙"→台阶**：0.04 m 低于 0.05 m 台阶阈值时起手是一堵 4 cm 墙（§4.4 G9）；P2 的台阶规则落地后它已经是台阶（碾过去） | 食物 |
| 保温壶（真实 0.12×0.12×0.30） | 0.12×0.12×0.30 | 0.00432 | 0.163 | 0.435 | 挡路 | 食物（**本关被篮子/箱子顶替**） |
| 平铺野餐垫（视觉毯） | 1.80×1.40×0.02 | 0.05040 | 0.369 | 0.987 | **"通路"的唯一候选**：0.02 m 平，只要不给它建挡路盒，球就碾过去（§4.4 G9；由台阶规则自动成立） | 食物 |
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
| 软锁检查 | ★ 要 245.99 u，门外有 384 u → **门后内容不是必经**，0.55–0.82 m 的窗口期进不去也不会卡死（这两个数是 412 u 小道具账；现行全关 127 件 / 1500 u，门后仍是小头，结论方向不变） |
| 观感 | 一堵"野餐篮 + 保温箱"堆起来的墙，正是原作那种"用可吸物搭场景"的读法 |

**为什么不是"一条只有小球能钻的缝"**：起手球就是 0.30 m，任何 ≥0.30 m 的缝开局就能过，缝就不成门。**在 0.30 m 起手的关卡里，"门"只能是"完全封死 + 吃掉才开"**；这条与整图 PicnicLawn 的 `bed_gap`（2.0 m 小球侧缝）语义不同，不要混用（那张图起手也是 0.30，它的缝是"大球过不去"，属于**永久窄口**，不是门）。

**竖向层次的三种可实现形态**（本关就用这三层，没有第四层）：

| 层 | 高度 | 本关内容 | 球的身份 |
|---|---|---|---|
| L0 地面 | `y = 0` | 草地（解析平面） | 唯一的"地形" |
| L1 贴地层 | ≤ 0.05 m（`scale.max_route_step_m`） | 平铺野餐毯、小路、出生地贴 | **通路**（碾过去；由 P2 的台阶规则判定——尺寸相关）→ 长大后可吸 |
| L2 剪影层 | 0.20–0.45 m | 躺倒的篮子 0.22、保温箱 0.30、凳子 0.45（B 案） | **挡路 → 食物**（就是上面那扇门） |
| L3 台面层 | 0.26 m | 花圃台顶（4.0×4.9 m）：`bed_top` 区的 28 件（**在宿主里**） | **能上去**（`D ≥ 1.04`，step-up）；上去之后就是台面上的食物 |
| L4 桌面/叠放 | ≥ 0.5 m 且悬空 | 折叠桌 0.70 由「台顶 → 躺椅 → 桌面」链上去；悬空/叠放**没有** | 台面可达（P2，`D ≥ 1.40`）；悬空/叠放仍不可实现 |

**L3 的读法**：台顶是地形，`bed_top` 的 28 件在宿主里、`D ≥ 1.04` 上得去。台顶**是**内容，不是布景。

**沿斜板上桌**：斜板、盒顶、支撑道具顶都是接触求解里的实体，没有额外开关；仍然没有的是分区高度场和曲面（只在需要坡/曲面/凹结构时才重要）。

#### 3.5.4 操作员要点

1. **门堆的四件贴地摆**（`y = 高/2`），横向首尾相接、**不留缝**；总长 1.90 m、净宽 0.10 m，误差 ±0.02 m。
2. **门堆 1.5 m 内不要再放任何"吸不动"的件**：宿主的动态挡路盒只有 **6 格**（`MaxBlockingBoxCount = 6`），门堆已占 4 格，附近再挤 3 件未解锁的大件就会把门件挤出选择集、门出现"漏缝"（§4.4 G9）。
3. 壁龛内 6 件靠里摆（离门 ≥1.0 m），否则球在门外就能把它们吸出来——`activationRadiusM = 1.6` 是按球心算的，门件一被吃掉，壁龛口就会进入 1.6 m 半径。
4. **花圃台顶的 `bed_top` 28 件是正式内容**（**不是布景**）：它们在 `PickupField` 里、`D ≥ 1.04` 能上去吃。
5. 验收补一条：**起手直冲门洞必须被挡住**（截图 + `rej=` 行 `TOO_LARGE`），吃到 0.83 m 后必须能进（截图）。

## 4. Q4 场地与支撑：motor 能做到什么

> 本节按代码现状写。物理真源是 [CONTACT_MODEL_V2](../BallMotor/CONTACT_MODEL_V2.md)，场地登记见 [BallMotor README](../BallMotor/README.md) 的「关卡几何宿主 `LevelArenaHost`」；本节只回答「现在的代码能做什么、本关的登记账长什么样、哪些缺口还在」。
> 判据只有两条，**别用眼睛估高度**：吸收门（`PickupAbsorbMath.IsBeyondAbsorbLimit`，门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，细节见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)）；道具分角色用的台阶判据 `rise = 顶面 − 当前支撑面 ≤ 0.25·D`（`SupportSurfaceMath.IsStepWithinLimit`，**含等号**）。
> 本节写的是「代码这么写」，不是「跑起来是这样」；Play Mode 读数以 Evidence 目录里的记录为准。

### 4.1 能力事实表（按符号名引用，不写行号）

| 能力 | 现状 | 符号 |
|---|---|---|
| 地面 | 一张解析平面，但**运行期可改**：`SetGroundPlane(normal, offset)`（P1 新增；`n.y ≤ 1e-3` 拒绝，因为墙一样的「地面」答不出「球下面多高」）| `KatamariBallMotor.SetGroundPlane` |
| 支撑面 | 接触求解选出：最高的 RIDE 接触（针尖支撑 = 5，爬升接触 = 4，无支撑 = 下落）；诊断读 `GetSupportSurfaceHeightWorldM` / `GetSupportKind` | `KatamariBallMotor.WriteContactSupport` |
| 台阶 | 没有阈值字段：台阶高 `h < r` 天然滚过，`h < D` 由爬升通道处理（CONTACT_MODEL_V2 §2.7、§3）；`rise ≤ 0.25·D` 只用于给「吸不动的道具」分角色 | `StepContactV2` / `SupportSurfaceMath.IsStepWithinLimit` |
| 重力 | 无支撑时按 `ContactTuning.Gravity`（9.81 m/s²）下落 | `StepContactV2` |
| 道具的两个角色 | 每 `0.05 s` 一遍扫描同时出「挡路」「支撑」两个掩码：吸不动 + `rise ≤ stepLimit` → 支撑；吸不动 + `rise > stepLimit` → 挡路；吸得动 → 食物（两边都不进） | `SupportSurfaceMath.TryClassifyProp` / `PickupFieldHost.UpdatePropObstacles` |
| 容量 | 边界平面 **8**、静态 OBB **256**（盒与斜板共用）、道具块 **12 槽**（挡路与支撑共用，每次扫描最近优先）；斜板 `MaxRampCount` = 4 | `MaxPlaneCount` / `ContactTuning.MaxStaticObbCount` / `PropObbBakeMath.PropObbCapacity` / `SupportSurfaceMath.MaxRampCount` |
| 斜面 | 斜板是实体 slab OBB：导入器把 `role=ramp` 的 `BoxCollider` 填进 `LevelArenaHost.rampBoxes`，`Setup` 烘成 OBB 经 `AddObb` 登记（`RegisterRampObb`）；它**不进 `staticBoxes`**，所以坡脚没有隐形墙 | `PlacementImporter.BindArenaHost` 的 ramp 分支；`LevelArenaHost.RegisterRampObb` |
| 旋转盒 | 有：盒与斜板按 8 个世界角拟合成真 OBB，剪切变形的盒拒绝并回退到 `Collider.bounds` AABB | `LevelArenaHost.Setup` |
| 卡住恢复 | 分软、硬两级（`ContactStuckSoftSeconds` 1.5 s、`ContactStuckHardSeconds` 3 s） | `KatamariBallMotor.UpdateContactRecovery` |
| 场地注册 | **`LevelArenaHost` 独占**：地面 1 次 `SetGroundPlane` + 墙平面 `Transform[]`（读**局部 +Z** 当法线、`dot(n, position)` 当 offset）+ 静态盒 / 斜板 `BoxCollider[]`（烘成 OBB）+ `allowedKinds`；`Update` 等 `GetSemanticDiameterWorldM() > 0` 后注册一次并打一行 `[arena]`；`Reapply()` 是全清后的重注册路径 | `Runtime/Story/LevelArenaHost.cs` |
| 道具注册 | `PickupFieldHost` 独占；arena **不许**引用道具（BallMotor README「关卡几何宿主」的一条硬规则） | `PickupFieldHost` |

**三条硬结论**：

1. 「角内地面必须全平」→ **不再要求**：盒顶与道具顶都能当支撑，花圃台 0.26 与 P2 链都在代码里成立。但**悬空、叠放、曲面仍然不存在**——能被踩的是「地面 / 静态盒顶 / 支撑道具顶 / 静态斜面」这四种面。
2. 「登记容量够用」→ 仍然成立，而且**现在是导入器在填**：4 平面（上限 8）+ 4 盒 + 2 斜板（共用 256 个静态 OBB）；玩家用的 `Wall_*` 不进盒数组（球用平面）。
3. 「平面是无限半空间」→ 仍然成立：一岛一 motor 的隔离规则不变，房间岛在 `(1200,0,0)`。

### 4.2 本关的登记账（真源是 `placement.json`，由导入器填）

| 类别 | 数量 | 来源（`Docs/Design/Levels/story_01_bench_size/placement.json`） | 上限 | 谁读它 |
|---|---:|---|---:|---|
| 地面 | 1 | `structures[role=ground]`：`center (−12.7, 0, −12.7)`、法线 `+Y` | 1 | `SetGroundPlane` |
| 墙平面 | 4 | `boundary_x0 / x1 / z0 / z1`，在 `LevelContent/Structures/` 下是**空物体标记**（无 Renderer、无 Collider，局部 +Z 指向场内）；可见墙 + 玩家碰撞盒在它的 `Visual` 子物体上 | 8 | `AddPlane`（标记）；`Visual` 只给玩家 |
| 静态盒 | 4 | `flower_bed`（`(−10, 0.13, −8.55)`、`4.0×0.26×4.9`）、`shell_signpost`（`0.45×1.2×0.12`）、`shell_tree_mound`（`2.5×0.14×2.5`）、`shell_tree_trunk`（`0.5×9×0.5`） | 8 | `AddBox`（墙 + 顶面支撑两用） |
| 斜面 | 2 | `ramp_to_bed`（10.7°）、`ramp_to_table`（30.0°） | 4 | `AddRamp`（支撑面；`LevelArenaHost.rampBoxes`） |
| 道具支撑 / 挡路 | 共 ≤12（动态） | 127 件里「吸不动」的那些，每 `0.05 s` 重扫 | 12 | `PickupFieldHost` → `SetPropObbs` |

导入后进 Play Mode 应看到**逐字**这一行（`LevelArenaHost.GetSetupReport()` 的格式）：

```text
[arena] planes=4/4 boxes=4/4 ramps=2/2 supports=7 refused=0 kinds=11
```

`supports=7` = 地面 1 + 盒顶 4 + 斜面 2；`refused=0` 是关键（非 0 说明重复登记或容量溢出）；`kinds=11` 是 placement 里出现过的 kind 数（`fruit / tableware / food / container / drink / nature / furniture / garden / structure / toy / debris`）。**空的 `allowedKinds` 等于全部拒绝吸**，这是最容易犯的「什么都吸不到」接法错误。

### 4.3 现在的四条硬结论

1. **竖向通路存在，但只有三种面**：花圃台 `0.26` 需 `D ≥ 1.04`；P2 链「台顶 `0.26` → 躺椅 `0.35` → 折叠桌 `0.70`」在 `D ≥ 1.40` 上成立（两级 rise 分别 0.09 / 0.35）。斜板已是实体斜面；悬空、叠放、曲面**仍然做不到**。
2. **毯缘 6 cm 必须继续当纯视觉**：它一旦进 `staticBoxes`，`rise = 0.06 ≤ 0.25·D` 在 `D ≥ 0.24` 时就是台阶，球会踩着毯子走。北边界平面先把球挡在 `z ≤ −6.0 − r`，毯子本来就不在球的可行域里 → 维持 §6 U6-A（不加 Collider、不进盒数组）。
3. **斜板只登记一次**：`ramp_to_bed` / `ramp_to_table` 在 `rampBoxes` 里，由 `RegisterRampObb` 烘成 slab OBB；它们**不进 `staticBoxes`**，同一块板登记两次会让坡脚多出一堵隐形墙（BallMotor README「关卡几何宿主」第 1 条；`Setup` 会报错并跳过盒登记）。
4. **静态盒注册一次永不删**：只有 `ClearObstacles()`（全清）+ `LevelArenaHost.Reapply()`。所以「会消失的门」只能用动态挡路盒（门堆 3 篮 + 1 箱正是这么做的）；把门件登记成静态盒就会变成永久墙。

### 4.4 仍在的缺口

| # | 缺口 | 现状 | 处置 |
|---|---|---|---|
| G3 | 静态盒没有单删接口 | `ClearObstacles()` 全清 + `LevelArenaHost.Reapply()` 重注册 | 门件继续走动态挡路 OBB |
| G4 | 平面是无限半空间 | 一岛一 motor | 岛距写进场景约定 |
| G7 | 静态布景的「多盒」没有契约 | 一件多盒会破 `validate_map` 第 14 条的账 | 与本关无关 |
| G8 | 悬空、叠放、曲面没有支撑 | 能被踩的是地面 / 静态盒顶 / 支撑道具顶 / 斜板 | 本关竖向只到 0.70（折叠桌） |
| G9 | 门堆 1.5 m 内放其它未解锁大件会一起挡路 | 挡路块 12 槽最近优先 | 作者侧保持门堆附近干净 |

## 5. Q5 导入与 Play 验收（Unity 操作员清单）

> 每步写「做什么 → 验收 / 回读 → 失败长什么样」。通用铁律：只有一名 agent 动 Unity；每批改动后 `state` + `read_console` 各看一次；导入器**不 `SaveScene`**，保存由人决定；改序列化数据时不要停在 Play Mode；`.meta` 随资产一起提交，**不重新生成已有 GUID**。

### 5.1 导入

| # | 做什么 | 验收 / 回读 | 失败长什么样 |
|---|---|---|---|
| **D0** | **前置（今天还不满足）**：44 个 def 的 `Art/Props/<def>/<def>.fbx` 必须全部就位，且**每个 FBX 的根节点自带 Renderer**（导入器对根无 Renderer 的模型报错并删实例）。实测当前只有 **16/44**，缺 28 个：`beer_crate / book_stack / bowl / bread_slice / cheese_slice / cooler_box / cooler_box_large / crate / crate_large / croissant / cup / fallen_leaf / folding_stool / folding_table / glass_bottle / juice_carton / lounge_chair / picnic_basket / picnic_bench / plate / sandwich / serving_tray / small_potted_plant / soda_can / spoon / storage_bin / stroller / wood_plank` | `ls Art/Props \| wc -l` ≥ 88（44 目录 + 44 `.meta`）；逐个 `AssetDatabase.LoadAssetAtPath<GameObject>` 非空 | 缺模型 → 报告里 28 条 `no model at …`，`props=99`，`level.expected` 对不上 |
| **D1** | 前置状态：不在 Play Mode；场景是 `Worlds/StoryMode/StoryMode.unity`；`LevelContent` 是场景根；场景里**恰好 1 个** `PickupFieldHost`、**恰好 1 个** `LevelArenaHost`（多个时导入器只同步层级序第一个并打 note） | `FindObjectsOfType<PickupFieldHost>().Length == 1` 等 | 两个宿主 → 只有第一个被同步，另一个的数组永远是旧的 |
| **D2** | 跑菜单：**`VRC Katamari/Level/Import Placement (LevelContent)`**（`PlacementImporter.MenuPath`）。它按 `placement.json` 的 `level.scene` 解析关卡（`level.id = story_01_bench_size`），**不 `SaveScene`** | Console 的 headline 与 summary | 菜单不存在 → 编辑器脚本没编译（`refresh_unity` 后重看） |
| **D3** | **第一次导入**的期望读数（逐字比对） | ```[level] +127 ~0 -0 props=127 other=+11 ~0 -0 errors=0``` · `pickup sync: ran, rows=127, rejected=0` · `valueUnits: declared=1500, preserved=0, undeclared=0, read-back: verified` · `arena: wired (boxes=4, walls=4, ramps=2, kinds=11)` · `propColliders: created=127` | `props=99` → D0 缺模型；`-110` → 旧件也在 `Props/` 下（C4/C5 没做对）；`read-back: NOT verified` → 声明值没写进去；`arena: not wired` → 场景里没有 `LevelArenaHost` |
| **D4** | **第二次导入（幂等门）** | ```[level] +0 ~0 -0 props=127 other=+0 ~0 -0 errors=0```；`valueUnits … verified` 不变；`propColliders: created=0`（第一次创建的 BoxCollider 被复用） | 第二次仍 `+127` → 幂等键坏了（按名字 / 顺序而不是 `itemId`），报 Lead |
| **D5** | 保存场景（人决定时机）：`Ctrl+S`（或 `EditorSceneManager.SaveScene`） | `git status` 里场景 modified；`git diff --stat` 只动该场景 | 和导入混在同一批保存 → 把半成品固化（导入器故意不保存） |
| **D6** | 抽检：5 件坐标与 JSON 误差 < 1 cm；`LevelContent/Props` 下件数 = 127；球起点 1.0 m 内 0 件；花圃台足迹内 0 件 | 用一次 `execute_code` 遍历读 `transform.position` 比对 JSON | 误差 > 1 cm → `PlacementImporter` 的位姿写错了，**报 Lead，不要手调** |

### 5.2 Play 验收（每步都要截图 + 读数）

> 读数来源：`StoryBallDiagnostics` 的 `[story]` 行。**没有读数就不算通过。**
> 驱动：操作员按不了键（`SetMoveAxes` 从 C# 调不进 Udon heap，`Labs/README.md` 已实测）。所以 E2 / E4 / E5 / E6 / E7 需要**人在 Play Mode 里按键**；操作员负责盯 Console 与截图。若用户不在场，用 CUA 往 Unity 窗口发按键只做一次性验收，不要写进常驻流程。

| # | 步骤 | 通过判据（读数 + 截图） |
|---|---|---|
| **E1** | 进 Play Mode，站桩不动 | ① `[arena] planes=4/4 boxes=4/4 ramps=2/2 supports=7 refused=0 kinds=11`；② 球心 ≈ `(−12.5, 0.15, −12.5)`、`gap = 0.000`、`support = ground`；③ HUD 三行可读（F1 之后）；④ Console 无 `Reference camera is not set`；⑤ 场内 127/127 件都在 `[−19.4,−6.0]` 内，最大件是 1.60 m 躺椅与两根 1.40 m 木板；角外没有登记进宿主的大件（东北角外的 `collapsed_bench` 若还在场景里只是布景，不在本关 `props[]`） |
| **E2** | 地面滚动 | 球前进、`CoreMesh` 滚动方向与位移一致；`axes` 与输入一致；速度到 `maxSpeedMps = 4`；**没有**抖动或自行漂移 |
| **E3** | 撞墙（四个方向各一次） | 球心到墙面距离 = 碰撞半径（±0.01）；`center=` 读数稳定；从墙外侧截图确认不穿墙 |
| **E4** | 吸收 | `absorbed=` 增长、HUD「当前」增长、球肉眼变大；吃起步环（`D_req 0.03–0.12` 的小件）无延迟；`rej=` 不增长 |
| **E5** | 挡路 | `D = 0.30` 撞保温箱 / 西瓜 / 皮球 → **停住**，`rej=` 出现 `TOO_LARGE`；`D ≥ 0.83` 吃掉最外侧的篮子后能进壁龛（净宽 0.10 → 0.55） |
| **E6** | **P1 台阶**（花圃台 0.26） | `D = 1.00` → 被挡；`D ≥ 1.04` → 上去；静止后 `gap = 0.000 ± 0.005`、`support=box`、`supportY=0.260`；台顶沿边来回 10 s：`center.y` 波动 < 1 mm、`support` 不翻转；从台顶开出去 → `support=ground`、`falling` 至少一帧为真、落回后 `gap ≈ 0` |
| **E7** | **P2 链**（台顶 → 躺椅 0.35 → 折叠桌 0.70） | 站上台顶后能跨上躺椅（rise 0.09，`D ≥ 0.36` 就有资格）；`D ≥ 1.45` 能上桌；`support=prop`、`supportId ≥ 100`；站上桌后 `D ≥ 2.00` 吃掉桌子 → `falling` 至少一帧、落回地面。**两个数要盯**：① 桌子的可用窗口是 `[1.40, 2.00)`（`4×rise` 到 `D_req`），1.40 是**等号**，实测请按 1.45 判（`D = 1.40` 时 `stepLimit` 恰好等于 rise，任何一点浮点误差都会让球掉下去）；② 躺椅 yaw 90° → 世界足迹 `x∈[−12.6,−12.0]`、`z∈[−10.4,−8.8]`，折叠桌 `x∈[−13.6,−12.6]`、`z∈[−9.5,−8.9]`，两者**只在 `x = −12.6` 这条线相接（重叠面积 0）**，不是 0.5×0.2 m。链能不能过必须实测 |
| **E8** | 斜板 | 球从坡脚上坡：`support=ramp`、`supportId` 落在斜面槽位、`center.y` 随坡上升；坡脚没有隐形墙（不会在坡前被挡）。两张截图：坡脚进入 / 坡顶接缝 |
| **E9** | 限时结束与结算 | 120 s 到点 → `SettleRound` 冻结 → 卡面显示「呈给国王」→ 3 s 后星 / 直径 / 评语；`GetBankedScoreUnits() == 0`；HUD 隐藏。**重试按钮本轮不验**（F2）：它没有 Collider，uGUI 那条也没接 `EventSystem` |
| **E10** | 方向指示与入座视点 | 推杆时 `PushDirectionCue` 指向球的前进方向（截图）；点球入座后眼点在球后上方、球在画面下前方不糊镜头；`[ride] seated entered` 一行 |
| **E11** | 截图清单（存 `Docs/Evidence/StoryLevel/<日期>/`） | ① 场景视图顶视（与 `/tmp/corner_layout/corner_absorb.png` 对照）；② 球视角：起步 / 台顶 / 壁龛口；③ 撞墙帧；④ 上台阶帧（含 `gap` 读数）；⑤ P2 链三帧；⑥ HUD 三行；⑦ 结果卡；⑧ 斜板上坡帧 |

**E 轮的已知风险**（不进判据，但要写进报告）：`startOnLoad = 0` 意味着回合要**坐上球**才开始，而 ClientSim 里的坐姿驱动本仓**没有验证过**（`HUB_LOOP_PLAN` §10 第 2 条）。若坐不上，临时把场景实例上的 `startOnLoad` 改成 1 跑一轮，**在报告里写明这是验收临时值**（不要提交）。

**E 轮的已知风险**：`startOnLoad = 0` 意味着回合要**坐上球**才开始。若坐不上，临时把场景实例上的 `startOnLoad` 改成 1 跑一轮，**在报告里写明这是验收临时值**（不要提交）。

## 6. Q6 风险与未决（需要用户拍板，带选项与代价）

> U1（160 m²）、U2（内容重排）、U3（暖壶按真实尺寸建模）、U5、U6、U7、U8 已由 [`Docs/OPEN_DECISIONS.md`](../../OPEN_DECISIONS.md) 的 (q) 条与 [PICNIC_CORNER_LAYOUT](PICNIC_CORNER_LAYOUT.md) §9 拍板。
>
> **仍然开着的只有 U4**：120 s 与星级 1.25 / 1.45 / 1.60 要不要按新内容 1500 u 重定——(q) 明写「现阶段不调星级数」。

**其它风险（不需要拍板，但必须知道）**：

1. **密度翻倍**：≈0.29 → **0.79 件/m²**（127 ÷ 160.0），`120 s` 的达标会更容易（这是 §5.2 E9 要测的第一件事）。
2. **球被 6 cm 毯缘挡住**（若选 U6-B）或**自己陷进毯子 6 cm**（若选 U6-A 且毯子在同一平面）——两者都是视觉问题，不是物理问题。
3. **指示牌数据不自洽**（`position.y = 0` vs 1.2 m 高）：本关按柱脚 0 建模；地图数据留给 owner。
4. **`PickupFieldSync` 重跑会改 `valueUnits`**：真实尺寸落地后大多数件落 s/m 档，412 会变；重跑必须与阈值重定同批做。
5. **单人专属**：同场景两块内容、同一台 Udon VM，多人时两个人的球会叠在一起（球无 Collider），HUB_LOOP_PLAN §6.4 已判"现阶段明确只支持单人"。
6. **"门封死"是采样结果，不是几何保证**：吸收门看 `propBounds` 体积，挡路盒只取最近的 ≤6 件（§4.4 G9）。门堆附近必须保持干净，验收要看截图，不能只看数据。
7. **门后的 28 u 在 0.55–0.82 m 窗口期是"看得到进不去"**：这是有意的奖励房，但必须确认它不在 ★ 的必经路径上（245.99 u < 门外 384 u，已算过）。
8. **结构件的"竖向"只有 0.22–0.45 m**：篮子躺倒 0.22 m、箱子 0.30 m，视觉上是"一堆野餐装备"，不是原作那种"台面 + 台面杂物"的高度。台面层（L3）现在**能上**（`D ≥ 1.04`），只有悬空/叠放的 L4 仍做不到（§4.4 G8）。
