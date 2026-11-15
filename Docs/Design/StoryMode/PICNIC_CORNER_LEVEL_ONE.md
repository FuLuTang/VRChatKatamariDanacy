# 第一关迁入野餐一角（PicnicLawn 西南角）：迁移方案

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。**只读调查 + 方案；作者不是 Unity 操作者**：没有编译、没有 Play Mode、没有 Build & Test，没有跑任何 Unity 测试。所有米数、件数、单位都是**本项目暂定值**，不是原作数值。
> 触发：用户 2026-11-15 拍板「第一关不再在 20×20 m 的平地测试场上做，挪进野餐草坪（PicnicLawn）的一角，在真实布局里调手感」。
> 范围：第一关（`story_01_bench_size`，滚到 1.25 m / 120 s）从实验室平地切片搬进 PicnicLawn 西南角；回答场景归属、生成器、角内范围、墙与边界、迁移步骤、未决六问。
> 非目标：不改 `.unity` / `.asset` / prefab / C# / `Tools/` 代码 / `tasks.json` / 其它文档；不设计新物理；不调数值（决定 (p)）；不做多人、交分、第二关。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。**符号名才是契约**：本轮读代码时 `KatamariBallMotor.cs` / `PickupFieldHost.cs` / `RadialProxy*` 正被并行修改（工作区 mtime 2026-11-15 06:23），文中行号是当时的读数，会漂；引用时请按符号名复核。
> **§4 / §5 于 2026-11-15 按现状重写**：写稿之后，地形支撑 P1（静态盒顶支撑 + 台阶 0.25·D + 重力下落）与 P2（吸不动的道具顶面当支撑，≤6 件）已实现并编译通过、P3（斜面）未做；场地注册已交给通用组件 `Runtime/Story/LevelArenaHost.cs`（`StoryLevel` 已接上）；搭地图工具已落地（真源 `Docs/Design/Levels/story_01_bench_size/placement.json`，导入器 `Editor/LevelBuild/PlacementImporter.cs`）。旧 §4 的「花圃台只能是墙 / 没有台阶 / 没有竖向通路」与旧 §5 的 C1/C2（新造「关卡边界」组件、把 `StoryLevelLabBootstrap` 改名 `StoryArenaHost`）**全部作废**。§1–§3 只改了与之矛盾的句子，均就地标注。

## 0. 六问一句话结论

| # | 问题 | 结论 |
|---|---|---|
| 1 | 场景归属 | **正本 = 把 `Labs/StoryLevel/StoryLevel.unity` 同一文件搬成正式场景（同一 GUID）**，角内几何按地图真坐标长在它里面；不翻新、也不转正 `PicnicLawnGreybox`，不新建第二张场景（§1）。 |
| 2 | 生成器还能不能用 | **不能用它建这一关**：`PicnicLawnGreyboxBuilder` 是「整图 · 从零重建 · 只产几何 · 会 `SaveScene`」，重跑必然覆盖手工接线；`--check` 只证明生成物与作者数据一致，与场景接线无关。数据侧只作 `--check` 回归，不重跑（§2）。 |
| 3 | 一角的范围 | **西南角** `x∈[−19.40,−6.00]`、`z∈[−19.40,−6.00]`：毛 179.56 m²、扣花圃 19.60 m² 后**净 160.0 m²**；角内只有平地 + 花圃台（0.26 m）+ 树根丘/角树 + 指示牌 + 毯缘一条线；角内内容已改为 **127 件 / 1500 u / 12 区**（真源 `placement.json`：110 件小道具 412 u + 17 件可吸家具 1088 u），其中 4 件是**结构门件**（§3.4 的指针、§3.5.3）。 |
| 4 | 地形与场地注册 | 边界是 **4 个竖直平面**（球）+ 4 面既有墙碰撞体（玩家）；地形现在**能上**：静态盒顶与「吸不动的道具顶」都是支撑面，台阶判据 `rise ≤ 0.25·D`（含等号）——花圃台 0.26 需 `D ≥ 1.04`，P2 链「台顶 0.26 → 躺椅 0.35 → 折叠桌 0.70」需 `D ≥ 1.40`；**斜面（P3）还不存在**，球会直接穿过斜板。场地注册归 `LevelArenaHost`（4 平面 + 4 盒 + 11 kind），不是 Lab 脚本（§4）。 |
| 5 | 迁移步骤 | 操作员清单 **A → E（轮 1，含 F1）+ F2/G（轮 2）**：场景转正、Lab 脚本去留、清理旧内容与补视觉、导入（两次幂等 `+0 ~0 -0`）、Play 验收；实现者改动 7 项 `K1–K7`（核心是「把 Lab 的每秒读数拆成只读诊断组件」）（§5）。 |
| 6 | 未决 | 原六组已由 `OPEN_DECISIONS` (q) 与 [PICNIC_CORNER_LAYOUT](PICNIC_CORNER_LAYOUT.md) §9 拍板（场地 160 m²、127 件 / 1500 u、星级暂不重定）；**仍然开着的**是 120 s 与星级要不要按新内容重定（§6 U4）、`.git` 侧转正由谁先做（§5.1 A2），以及本轮的 Play 验收（§5.5）——全部 `NOT_RUN`（§6 头部说明）。 |

**本次一个数据文件都没改**（`layout.json` / `prop_catalog.csv` / `unity_greybox.json` 逐字节未动），原因见 §7.1：前两个被两份关卡 JSON 的 `map_snapshot` 钉住，改一个字节就会让 `validate_levels.py` 与 `Docs/tests/test_levels.py` 变红，而重钉要改 `Docs/Design/GameModes/Levels/*.json`，不在本次写范围。

## 1. Q1 场景归属：哪个 `.unity` 是正本

### 1.1 三条硬约束（先看这个，它把选项砍掉一半）

1. **一个世界 = 一个 Unity 场景。** 运行时不能换场景、脚本不能把玩家送去别的世界，跨世界只有 Portal（[HUB_LOOP_PLAN](HUB_LOOP_PLAN.md) §1 已逐条查证并列出官方来源）。所以「主房间 + 各关卡区域」只能同场景共存。
2. **一个场景里可以有 N 个「岛」，但每个球各有一套 motor**，motor 的障碍表（8 平面 + 8 盒）是**每实例**的；平面是无限半空间（§4），所以岛与岛之间必须靠距离隔离，不能靠「这块归这个 motor」的几何分块。
3. **已有接线的迁移成本**：`StoryLevel.unity` 里已经接好的东西是「宿主平行数组（旧 110 行；2026-11-15 起由导入器从 `placement.json` 重写成 127 行）+ `propColliders` + motor/Station/输入/HUD/回合控制器 + `LevelArenaHost`（4 个墙平面 Transform）」。这些接线**只存在于这一个文件里**，任何「从数据重建」的路径都要重做一遍；导入器只写 `LevelContent` 子树，其余接线它不碰。

### 1.2 三个候选与代价

| 选项 | 做什么 | 代价 | 判定 |
|---|---|---|---|
| **A. 翻新 `PicnicLawnGreybox` 当正本** | 改数据 → 重跑 `build_greybox.py` → 重跑 Unity 生成器 → 再接线 | 生成器 `SaveScene` 从零重建场景（`Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs` 头注释与 `SaveScene` 调用），**必然抹掉**之后手工加的 motor/宿主/回合/Station；且它不产任何 Udon（全文只有 `AddComponent<BoxCollider>` 等几何组件，无 `UdonBehaviour`）；它是 **40×40 全图**，不是一角；还要先解决「全图 452 件 + 4 交分台 + 4 出生点」和单人第一关的取舍 | **否** |
| **B. 把一角的地形搬进 `StoryLevel`（同一文件转正）** | 同一 `.unity` + 同一 `.meta` 搬到 `Assets/VRC_Katamari/Worlds/StoryMode/`，角内几何按地图真坐标长在里面 | 需要重排内容 + 一次性导入（§5.4；导入器按 `placement.json` 建 127 件，不是手摆）；场景会同时承载「房间岛 + 关卡岛」，内存/渲染都在一起（HUB_LOOP_PLAN §2 的已知代价） | **推荐** |
| **C. 新建一张正式场景** | 新 `.unity`，把 lab 内容复制过去 | 立刻两张各改一半的关卡场景（`FIRST_PLAYABLE_PLAN.md` §5 与 `AGENTS.md` 反复在防的账）；GUID 分叉，所有引用要重指 | **否** |

### 1.3 推荐 B（不推荐 A / C），理由四条

1. **HUB_LOOP_PLAN §7 已经拍过同一件事**：「把 `Labs/StoryLevel/StoryLevel.unity` 整体搬成正式场景（同一文件、同一 GUID、`.meta` 一起搬），房间直接加进它。不新建第二张、不复制。」角内改的是**内容与坐标**，不是场景归属，所以这条不变。
2. **接线是唯一不可再生的资产**：宿主平行数组、`propColliders`、`station`、`roundController`、`HUD`、`LevelArenaHost` 全在这个文件里；搬场景是 `mv`，重建场景是重做。（数组的内容现在可再生——真源是 `placement.json`、由导入器重写——但**组件与引用**仍然只有这一份。）
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
| `scale.item_tier_pickup_diameter_m`（s 0.08–0.30 / m 0.32–0.95 / l 1.00–1.95） | `layout.json:49` | **长度**档位：`clamp(game_longest_dim_m, 档窗口)` 当"拾取门槛" | 体积门 `a·b·c ≤ (π/6)·D³/10`；等价边长 `a_eq = (abc)^(1/3)`，`D_req = 2.673·a_eq` | **不动** | 它是内容侧分档、也是校验器第 11 条唯一的判据；改它必须同时改校验器与声明值（值表已删），而校验器不在写范围 |
| `game_longest_dim_m`（目录列） | `prop_catalog.csv` | 「游戏尺寸」（比真实尺寸放大，例：`apple` 0.08→0.30） | 真实尺寸建模、`localScale = 1`、`propBounds` = 模型 AABB | **不动** | 同一列同时喂校验器与生成器的脚印/间距模型；改它会让 `build_greybox --check` 与 README §3.3 的可行性表一起失效（那张表被 `ReadmeFeasibilityTableTests` 逐字比对） |
| `real_longest_dim_m`（目录列） | `prop_catalog.csv` | 真实最长边，只有一列、没有三轴 | 需要三轴或 `volume_m3` | **不动** | 缺第三轴，任何 `a_eq` 都是估的；按「不手抄推导数字」的规矩不写 |
| `@size=<米>` 立方体写法 | 场景道具命名（`Editor/Pickup/PickupFieldAuthoringRules.TryParseDeclaredSize`） | 「挡路 3.6 m、拾取 1.25 m」→ 把 `propBounds` 记成 1.25³ = 1.95 m³ | 体积门下 1.95 m³ 需要 **2.60 m** 球，比 2.20 上限还大 —— 这个通道**已经把门件写成死件**（(l) 原文点名） | **不动（旧编号 C6；现行改动清单见 §5.8 的 K1–K7）** | 正确做法是「`propBounds` 说真话（模型 AABB）+ 挡路盒另立」（挡路接线那条线在做）；改 `PickupFieldAuthoringRules` 属代码，不在写范围 |
| `maxItemSizeWorldM` / `absorbSizeRatio` | `Labs/CpuSkirmish/CpuSkirmish.unity`（**`StoryLevel.unity` 已 0 命中**，本轮 grep 实测；`Labs/BallPlayground` 的 `.unity`/`.prefab` 已于 2026-11-15 随该 lab 退役删除） | 死键，Unity 忽略、回落 512 / 10 | 已删字段 | **不动** | 在场景/prefab 里，属编辑器产物；删死键是 Unity 侧顺手活（(l) 已列为"仍待做：7 处场景序列化覆盖"），且不属于本关场地 |
| `maxItemVolumeWorldM3` / `absorbVolumeRatio` | 同上（场景 `512` / `10`） | 已是新口径 | 同左 | **不改** | 值就是契约默认值，不需要覆盖 |
| `pickup_dim_m`（可选列） | 目录 schema | 故事图专用「声明拾取尺寸 ≠ 外形」通道 | 体积门不需要它；需要的是真实 AABB + 挡路盒 | **不动** | PicnicLawn 目录里没有这一列；它有在故事图 fixture 里，与本次无关 |
| 障碍/平面的 `collision` 声明 | `layout.json` 结构 | 无（0/8、0/8） | 第 14 条运行时预算 | **不动** | PicnicLawn 是全图 PvP 图，不是这一关的关卡数据；这一关的边界账在本文 §4 |

**结论**：数据文件里真正「过期」的是**概念**（线性档位、`@size` 立方体、game/real 双列），而它们的消费方（校验器 `validate_map.py` 第 11 条 + `PickupFieldAuthoringRules` + `build_greybox`）**都还按旧口径判**。按「不改校验器就只改不受影响的字段」的规矩，本轮**一个机器可读字段都不改**。

### 2.3 取舍：「翻新数据 → 重跑 → 再接线」还是「不重跑、手工迁移」

| 路线 | 适用 | 代价 |
|---|---|---|
| **翻新数据 → 重跑生成器 → 再接线** | 整图 PicnicLawn 的下一版灰盒（T21） | 必须先补校验器（§2.5）、补生成器的「保留手工接线」能力（今天没有），再重做全部 Udon 接线；对角内第一关是纯亏 |
| **不重跑生成器、手工迁移一角**（推荐） | **本关** | 旧稿是「110 件要重排」；2026-11-15 起改为**导入器按 `placement.json` 建 127 件**（§5.4），不再是手工摆；接线、宿主数组、交付路径全部保住 |
| 折中：数据照旧，只跑 `--check` 回归 | 日常 | `python3 Tools/mapbuild/build_greybox.py --check` 必须保持 OK（本轮实测 OK） |

**生成器仍然有用的地方**：① `--check` 守住「生成物 = 作者数据」；② `--feasibility` 是唯一被测试保护的容量模型，重排 110 件时**照它的公式算**（`RSA_SITE_AREA = 1.436·S²`，用量 ≤85%）；③ 俯视图 `PicnicLawnGreybox_top.png` 是角内布置的参考底图。

### 2.4 角内布置该由谁负责（**缺口已关闭，2026-11-15**）

> **本节原文已作废。** 「今天没有任何数据文件描述第一关的角内布置」不再成立：真源是 `Docs/Design/Levels/story_01_bench_size/placement.json`（127 件 / 1500 u / 44 def / 1 ground + 4 boundary + 4 box + 2 ramp，坐标为 PicnicLawn 真坐标），由 `Editor/LevelBuild/PlacementImporter.cs` 单向导入到受管根 `LevelContent`，设计见 [LEVEL_AUTHORING](../Tools/LEVEL_AUTHORING.md)。`layout.json` 仍然只描述整图 PvP 语义，两者不冲突（一个「地图长什么样」，一个「这一关摆哪」）。
> 仍然保留的判断：**布置的真源不能留在场景里**（旧稿这条是对的），只是兜底手段从「手写校验」换成了「导入器 + 幂等门 `+0 ~0 -0` + `valueUnits` 回读」——见 §5.4。旧稿提到的 C8（造一份布置数据）**已由 `placement.json` 关闭**。

### 2.5 校验器需要怎么改（本轮不改，只列）

`Tools/mapbuild/validate_map.py`（**不在写范围，以下只是清单**）：

| 位置 | 现在 | 应改为 |
|---|---|---|
| 第 11 条「档位与球包线」 | `threshold = clamp(game_longest_dim_m, 档窗口)`，要求 `threshold ≤ max_ball` | 用 `a_eq`（或目录新增 `a_eq_m` / `volume_m3` 列）判 `D_req = 2.673·a_eq ≤ D_cap`；长度档位退化为「价值分档」，与可吸性脱钩 |
| 第 14 条「运行时预算」 | 只查 `pickup_dim_m ≤ max_pickup_diameter_m` | 改成体积上界 `a·b·c ≤ (π/6)·D_cap³/10`；`pickup_dim_m` 通道退役 |
| 目录 schema | 必填 14 列，无体积列 | 建议加可选 `aabb_m`（"x;y;z"）或 `volume_m3`，由建模清单生成，禁止手抄 |
| 第 13 条立面分类 | 允许 `climb_step`（台阶可按球径翻越） | **这一条已由运行时兑现（2026-11-15，P1）**：motor 有 step-up，`climb_step` 按 `rise ≤ 0.25·D` 真实生效。仍然没有运行时的是 `drivable_grade`（12% 可走坡 = 玩家侧的 `validate_map` 分类；球没有牵引力模型，斜面等 P3） |

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
- **本关真正的"上限件"是 4 支暖壶**（**已解决，2026-11-15**）：现状 0.9³ 立方体在 2.20 下**永久吸不到**（`D_req 2.406`）。现在 `placement.json` 里**已经没有 `thermos`**：`container` 改由 3 篮 + 1 箱 + 箱柜类（`beer_crate / cooler_box / cooler_box_large / crate / crate_large / picnic_basket / storage_bin`）承担，按真实尺寸建模。旧稿担心的「412 u 里有 64 u 是空账」不再成立（见 §3.4 的指针）。

### 3.4 现有 110 件 / 412 单位 / 9 kind 怎么映射进去

> **本节已被 [PICNIC_CORNER_LAYOUT](PICNIC_CORNER_LAYOUT.md) §7 与 `placement.json` 取代（2026-11-15）**，保留作历史：现在的账是 **127 件 / 1500 u / 11 kind / 12 区**（110 件小道具仍是 412 u，另 17 件可吸家具 1088 u）；区名沿用但 `fruit_b` 从西北角挪到花圃台面，并新增 `bed_top` / `table_top`。下面的件数 / 单位 / 区表**只用于读旧决策**，**不要照它摆**——摆什么由 `placement.json` 决定（§5.4）。

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
| **通路 / 地形**（滚上去、搭坡、叠放） | 斜板、叠箱、桌面 | **能了，但只有三种面**（2026-11-15：P1 静态盒顶 + P2 道具顶；**斜面仍是 P3**） | 旧依据（「支撑只对一张全局平面 / 没有 step-up」）**已作废**：`SupportSurfaceMath` 每步在球心正下方选一个局部面（地面 / 盒顶 / 道具顶），`rise ≤ 0.25·D` 的盒从扫掠里剔除并成为支撑（`IsStepWithinLimit` / `RefreshStepMask`），失去支撑时按 9.81 m/s² 下落。**仍然做不到**：斜面（P3）、悬空、叠放、曲面。详见本文 §4 与 `TERRAIN_SUPPORT` §3–§5 |
| **挡路**（吸不动时挡球） | 大件挡住小球的去路 | **能，而且是现成的** | `PickupFieldHost.UpdateBlockingProps` 用 `propBounds` 建轴对齐盒、经 `motor.SetBlockingBoxes` 进扫掠；谓词就是吸收门自己：`PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, limit)`（`PickupFieldHost.UpdateBlockingProps`，头注释与 `IsBeyondAbsorbVolumeLimit` 调用处） |
| **食物**（长大后吸走） | 同一件东西后来被吃 | **能，和上一条是同一个函数** | 球长大 → `limit` = `(π/6)·D³/10` 超过该件体积 → 同一谓词不再成立 → 下一帧扫描撤掉挡路盒、同时吸收门放行。**"挡路"与"可吸"是同一个判据的两面，不会漂移** |

一句话（**2026-11-15 起**）：**我们有竖向通路了，但它只由「地面 / 静态盒顶 / 吸不动的道具顶」这三种平面构成**，靠台阶规则（`rise ≤ 0.25·D`）走上去；「挡路 → 食物」的翻转与吸收门同源、不会漂移。所以本关的结构设计同时是两件事：**搭出可以走上去的台面层级**，和**用可吸物搭出一扇会自己消失的门**。旧稿「我们没有竖向通路」只对斜面/悬空/叠放成立。

#### 3.5.2 结构件与阈值（`D_req = (60·a·b·c/π)^(1/3)`）

尺寸取真实尺寸（(o) 建模口径）；`V` 是 AABB 三轴积，`D_req` 既是"可吸"的球径、也是挡路盒消失的球径（同一个数）。

| 结构件 | 真实 AABB m | `V` m³ | `a_eq` | **`D_req`** | 0.30 起手时 | 1.48（本关天花板）时 |
|---|---|---:|---:|---:|---|---|
| 野餐篮 `picnic_basket` | 0.45×0.30×0.22 | 0.02970 | 0.310 | **0.828** | 挡路 | 食物 |
| 保温箱 `cooler_box` | 0.55×0.35×0.30 | 0.05775 | 0.387 | **1.033** | 挡路 | 食物 |
| 折叠凳 `folding_stool`（本关**不用**，留作 B 案） | 0.40×0.30×0.45 | 0.05400 | 0.378 | 1.010 | 挡路 | 食物 |
| 卷起的野餐垫（B 案） | 0.80×0.20×0.20 | 0.03200 | 0.317 | 0.849 | 挡路 | 食物 |
| 托盘（B 案） | 0.55×0.35×0.04 | 0.00770 | 0.197 | 0.528 | **"薄墙"**：0.04 m 低于 0.05 m 台阶阈值，但旧版仍会建挡路盒（§4.4 G9）→ 起手是一堵 4 cm 墙；**P2 的台阶规则落地后它已经是台阶（碾过去）**，旧的 C9 作废 | 食物 |
| 保温壶（真实 0.12×0.12×0.30） | 0.12×0.12×0.30 | 0.00432 | 0.163 | 0.435 | 挡路 | 食物（**本关被篮子/箱子顶替**） |
| 平铺野餐垫（视觉毯） | 1.80×1.40×0.02 | 0.05040 | 0.369 | 0.987 | **"通路"的唯一候选**：0.02 m 平，只要不给它建挡路盒，球就碾过去（§4.4 G9；P2 落地后由台阶规则自动成立，旧的 C9 作废） | 食物 |
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
| 软锁检查 | ★ 要 245.99 u，门外有 384 u → **门后内容不是必经**，0.55–0.82 m 的窗口期进不去也不会卡死（**这两个数是旧的 412 u 账**；现行 127 件 / 1500 u 账见 §3.4 的指针——结论方向不变，门后仍是小头） |
| 观感 | 一堵"野餐篮 + 保温箱"堆起来的墙，正是原作那种"用可吸物搭场景"的读法 |

**为什么不是"一条只有小球能钻的缝"**：起手球就是 0.30 m，任何 ≥0.30 m 的缝开局就能过，缝就不成门。**在 0.30 m 起手的关卡里，"门"只能是"完全封死 + 吃掉才开"**；这条与整图 PicnicLawn 的 `bed_gap`（2.0 m 小球侧缝）语义不同，不要混用（那张图起手也是 0.30，它的缝是"大球过不去"，属于**永久窄口**，不是门）。

**竖向层次的三种可实现形态**（本关就用这三层，没有第四层）：

| 层 | 高度 | 本关内容 | 球的身份 |
|---|---|---|---|
| L0 地面 | `y = 0` | 草地（解析平面） | 唯一的"地形" |
| L1 贴地层 | ≤ 0.05 m（`scale.max_route_step_m`） | 平铺野餐毯、小路、出生地贴 | **通路**（碾过去；旧的 C9「厚度过滤」**已作废**，由 P2 的台阶规则取代——尺寸相关，更忠实）→ 长大后可吸 |
| L2 剪影层 | 0.20–0.45 m | 躺倒的篮子 0.22、保温箱 0.30、凳子 0.45（B 案） | **挡路 → 食物**（就是上面那扇门） |
| L3 台面层 | 0.26 m | 花圃台顶（4.0×4.9 m）：`bed_top` 区的 28 件（**在宿主里**） | **能上去**（`D ≥ 1.04`，step-up）；上去之后就是台面上的食物 |
| L4 桌面/叠放 | ≥ 0.5 m 且悬空 | 折叠桌 0.70 由「台顶 → 躺椅 → 桌面」链上去；悬空/叠放**没有** | 台面可达（P2，`D ≥ 1.40`）；悬空/叠放仍不可实现 |

**L3 的读法（2026-11-15 改正）**：旧稿「花圃台顶只放纯布景、永远上不去」**已作废**——P1 之后台顶是地形，`bed_top` 的 28 件在宿主里、`D ≥ 1.04` 上得去，也就是 `PICNIC_CORNER_LAYOUT` §11 明确作废的那一条。台顶**是**内容，不是布景。

**如果以后要做真正的"沿斜板上桌"**（P3 的活，本方案不设计物理）：下面三条里**第 1、3 条已落地**（场景物体参与支撑 = 盒顶/道具顶；step-up = 台阶规则），**只剩第 2 条**：

1. **场景物体参与支撑** —— **已完成（P1/P2）**：静态盒顶与支撑道具顶都进支撑表；桌子的顶面今天已经能站（只要 `rise ≤ 0.25·D`）。
2. **地面不再是"一张全局平面"** —— **仍未做**：今天是「地面平面 + 盒顶 + 道具顶 + （P3 的）斜面」四种平面，**没有分区高度场、没有曲面**。这一条只在需要坡/曲面/凹结构时才重要。
3. **step-up** —— **已完成（P1）**：`rise ≤ 0.25·D` 的盒从扫掠里剔除、抬升上限 `maxStepUpLiftWorldM = 0.6`、三条防抖。

#### 3.5.4 操作员要点（叠在 §5.1 上）

1. **门堆的四件贴地摆**（`y = 高/2`），横向首尾相接、**不留缝**；总长 1.90 m、净宽 0.10 m，误差 ±0.02 m。
2. **门堆 1.5 m 内不要再放任何"吸不动"的件**：宿主的动态挡路盒只有 **6 格**（`MaxBlockingBoxCount = 6`），门堆已占 4 格，附近再挤 3 件未解锁的大件就会把门件挤出选择集、门出现"漏缝"（§4.4 G9）。
3. 壁龛内 6 件靠里摆（离门 ≥1.0 m），否则球在门外就能把它们吸出来——`activationRadiusM = 1.6` 是按球心算的，门件一被吃掉，壁龛口就会进入 1.6 m 半径。
4. **花圃台顶的 `bed_top` 28 件是正式内容**（**不是布景**，2026-11-15 改正）：它们在 `PickupField` 里、`D ≥ 1.04` 能上去吃。旧稿「L3 只放纯布景」已随 `PICNIC_CORNER_LAYOUT` §11 作废。
5. 验收补一条：**起手直冲门洞必须被挡住**（截图 + `rej=` 行 `TOO_LARGE`），吃到 0.83 m 后必须能进（截图）。

## 4. Q4 场地与支撑：motor 现在能做到什么（2026-11-15 按现状重写）

> 本节按当日**代码现状**重写。旧版写于地形支撑之前，结论「花圃台只能是墙、没有台阶、没有竖向通路」**已作废**。物理设计真源是 [TERRAIN_SUPPORT](../BallMotor/TERRAIN_SUPPORT.md)（§3 支撑面模型、§4 台阶、§5 斜面、§7 场地接口、§8 分阶段与 Play 验收、§8.1 P1 的三处偏离、§10.2 暂定值）；本节只回答「现在的代码能做什么、本关的登记账长什么样、哪些缺口还在」。
> 判据只有两条，**别用眼睛估高度**：吸收门 `a·b·c ≤ (π/6)·D³/10`（`PickupAbsorbMath.IsBeyondAbsorbVolumeLimit`）；台阶 `rise = 顶面 − 当前支撑面 ≤ 0.25·D`（`SupportSurfaceMath.IsStepWithinLimit`，**含等号**）。
> 状态：P1（静态盒顶支撑 + 台阶 + 重力下落）与 P2（道具顶面当支撑）的源码、NUnit 用例与 Python 参考模型已进树并编译通过；**Unity 编译、`run_tests`、Play Mode 全部 `NOT_RUN`**（作者不是 Unity 操作者）。所以本节写的是「代码这么写」，不是「跑起来是这样」。

### 4.1 能力事实表（按符号名引用，不写行号）

| 能力 | 现状 | 符号 |
|---|---|---|
| 地面 | 一张解析平面，但**运行期可改**：`SetGroundPlane(normal, offset)`（P1 新增；`n.y ≤ 1e-3` 拒绝，因为墙一样的「地面」答不出「球下面多高」）——旧版 §4.3 的 G2 缺口**已关闭** | `KatamariBallMotor.SetGroundPlane` |
| 支撑面 | 每步在**球心正下方**选**一个**局部平面（地面 / 静态盒顶 / 道具顶），把 `(n, dot(n,P))` 喂 `GroundContactMath.TrySolve`；`GroundContactMath` 本身一行没改 | `SupportSurfaceMath.TryResolveSupport` |
| 台阶 | `rise ≤ stepLimit = 0.25·D` → 该盒**从扫掠里剔除**并进支撑表；否则仍是实心墙。三条防抖：迟滞 `+0.02 m`、已在顶上（`+0.01 m` 余量）无条件算台阶、跨级需 `> 0.005 m` | `IsStepWithinLimit` / `RefreshStepMask` / `IsStepCrossing` |
| 重力 | 失去支撑 → `fallSpeed += 9.81·dt`，当步的 `maxLift/maxDrop` 换成重力预算，落地清零；**没有跳跃、没有向上速度** | `ResolveFallState` / `TryIntegrateFall` |
| 道具的两个角色 | 每 `0.05 s` 一遍扫描同时出「挡路」「支撑」两个掩码：吸不动 + `rise ≤ stepLimit` → 支撑；吸不动 + `rise > stepLimit` → 挡路；吸得动 → 食物（两边都不进） | `SupportSurfaceMath.TryClassifyProp` / `PickupFieldHost.UpdatePropObstacles` |
| 容量 | 平面 **8**、静态盒 **8**、动态挡路盒 **6**、道具支撑 **6**；**斜面 4 是 P3 的事，今天没有槽位** | `MaxPlaneCount` / `MaxBoxCount` / `MaxBlockingBoxCount` / `MaxSupportPropCount` |
| 斜面 | **不存在**：motor 没有 `AddRamp`、`LevelArenaHost` 没有 `rampBoxes`。导入器给斜板一个 `BoxCollider`（只给玩家），**既不进 `staticBoxes` 也不登记成坡** | `PlacementImporter.ApplyStructures` 的 ramp 分支（只打一条 note） |
| 旋转盒 | 没有。`BoxCollider.bounds` 烘焙成世界 AABB，45° 摆会胖（本关角内没有 45° 件） | `LevelArenaHost.Setup` |
| 卡住恢复 | 1.5 s 无进展 → 球心瞬移回 `startCenterWorld` | `stuckTimeoutSeconds` |
| 场地注册 | **`LevelArenaHost` 独占**：地面 1 次 `SetGroundPlane` + 墙平面 `Transform[]`（读**局部 +Z** 当法线、`dot(n, position)` 当 offset）+ 静态盒 `BoxCollider[]`（烘焙 min/max）+ `allowedKinds`；`Update` 等 `GetSemanticDiameterWorldM() > 0` 后注册一次并打一行 `[arena]`；`Reapply()` 是全清后的重注册路径 | `Runtime/Story/LevelArenaHost.cs` |
| 道具注册 | `PickupFieldHost` 独占；arena **不许**引用道具（`TERRAIN_SUPPORT` §7.4 的一条硬规则） | `PickupFieldHost` |

**旧 §4.1 的三条硬结论，现在读作**：

1. 「角内地面必须全平」→ **不再要求**：盒顶与道具顶都能当支撑，花圃台 0.26 与 P2 链都在代码里成立。但**悬空、叠放、曲面、斜面仍然不存在**——能被踩的只有「地面 / 静态盒顶 / 支撑道具顶」这三种平面。
2. 「`AddBox` / `AddPlane` 够用」→ 仍然成立，而且**现在是导入器在填**：4 平面 + 4 盒，离 8/8 各余 4 格；玩家用的 `Wall_*` 不进盒数组（球用平面）。
3. 「平面是无限半空间」→ 仍然成立：一岛一 motor 的隔离规则不变，房间岛在 `(1200,0,0)`。

### 4.2 本关的登记账（真源是 `placement.json`，由导入器填）

| 类别 | 数量 | 来源（`Docs/Design/Levels/story_01_bench_size/placement.json`） | 上限 | 谁读它 |
|---|---:|---|---:|---|
| 地面 | 1 | `structures[role=ground]`：`center (−12.7, 0, −12.7)`、法线 `+Y` | 1 | `SetGroundPlane` |
| 墙平面 | 4 | `boundary_x0 / x1 / z0 / z1`，在 `LevelContent/Structures/` 下是**空物体**（无 Renderer、无 Collider），局部 +Z 指向场内 | 8 | `AddPlane` |
| 静态盒 | 4 | `flower_bed`（`(−10, 0.13, −8.55)`、`4.0×0.26×4.9`）、`shell_signpost`（`0.45×1.2×0.12`）、`shell_tree_mound`（`2.5×0.14×2.5`）、`shell_tree_trunk`（`0.5×9×0.5`） | 8 | `AddBox`（墙 + 顶面支撑两用） |
| 斜面 | 2 | `ramp_to_bed`（10.7°）、`ramp_to_table`（30.0°） | 4（P3） | **今天没人读**：只有 `BoxCollider` 给玩家 |
| 道具支撑 / 挡路 | 各 ≤6（动态） | 127 件里「吸不动」的那些，每 `0.05 s` 重扫 | 6 / 6 | `SetSupportProps` / `SetBlockingBoxes` |

导入后进 Play Mode 应看到**逐字**这一行（`LevelArenaHost.GetSetupReport()` 的格式）：

```text
[arena] planes=4/4 boxes=4/4 supports=5 refused=0 kinds=11
```

`supports=5` = 地面 1 + 盒顶 4；`refused=0` 是关键（非 0 说明重复登记或容量溢出）；`kinds=11` 是 placement 里出现过的 kind 数（`fruit / tableware / food / container / drink / nature / furniture / garden / structure / toy / debris`）。**空的 `allowedKinds` 等于全部拒绝吸**，这是最容易犯的「什么都吸不到」接法错误。

### 4.3 现在的四条硬结论（取代旧版三条）

1. **竖向通路存在，但只有三种面**：花圃台 `0.26` 需 `D ≥ 1.04`；P2 链「台顶 `0.26` → 躺椅 `0.35` → 折叠桌 `0.70`」在 `D ≥ 1.40` 上成立（两级 rise 分别 0.09 / 0.35）。这是 §3.5.1「没有竖向通路」的**作废点**；但斜面、悬空、叠放、曲面**仍然做不到**（P3/P4）。
2. **毯缘 6 cm 必须继续当纯视觉**：它一旦进 `staticBoxes`，`rise = 0.06 ≤ 0.25·D` 在 `D ≥ 0.24` 时就是台阶，球会踩着毯子走。北边界平面先把球挡在 `z ≤ −6.0 − r`，毯子本来就不在球的可行域里 → 维持 §6 U6-A（不加 Collider、不进盒数组）。
3. **斜板不是墙，是「不存在」**：`ramp_to_bed` / `ramp_to_table` 只对玩家有碰撞；对球既不挡也不抬。旧稿「斜板现阶段只是墙」应读作「**球会穿板**」——这不是 bug 报告，是 P3 的缺口（`TERRAIN_SUPPORT` §8 P3）。它同时意味着：**别为了让球「至少被挡一下」把斜板塞进 `staticBoxes`**，那会在坡脚造一堵隐形墙（`TERRAIN_SUPPORT` §5.5、§7.7 第 1 条）。
4. **静态盒注册一次永不删**：只有 `ClearObstacles()`（全清）+ `LevelArenaHost.Reapply()`。所以「会消失的门」只能用动态挡路盒（门堆 3 篮 + 1 箱正是这么做的）；把门件登记成静态盒就会变成永久墙。

### 4.4 缺口表（G1–G9 逐条更新，新增 G10–G12）

| # | 旧写法 | 现状 | 处置 |
|---|---|---|---|
| G1 | 没有台阶 / 坡 / 分区地面 | **台阶已关闭**（P1）；坡仍未做（P3） | 台阶用现成的；坡等 P3 |
| G2 | 地面只能靠 Inspector 两个字段设 | **已关闭**：`SetGroundPlane` 运行期可改（`n.y > 1e-3`） | 无 |
| G3 | 8+8 上限 + 只有 `ClearObstacles()` | **仍在**：静态盒没有单删接口；`Reapply()` 只能全清后重注册 | 门件继续走动态挡路盒 |
| G4 | 平面是无限半空间 | **仍在** | 一岛一 motor；岛距写进场景约定 |
| G5 | 没有通用的「关卡边界」组件 | **已关闭**：`Runtime/Story/LevelArenaHost.cs` 落地，场景已接（`LevelArena` 根对象上挂着它，`staticBoxes` 目前为空，导入后由导入器填） | 无 |
| G6 | 盒只有 AABB、没有旋转盒 | **仍在**：45° 件退化；本关角内无 45° 件（躺椅 yaw 90° 的 AABB 由数据侧算好） | 整图另案 |
| G7 | 静态布景的「多盒」没有契约 | **仍在**（一件多盒会破 `validate_map` 第 14 条的账） | 与本关无关 |
| G8 | 没有「可爬的结构物」 | **部分关闭**：盒顶（P1）与道具顶（P2）可上；**斜面 / 悬空 / 叠放 / 曲面仍不能** | 本关竖向只到 0.70（折叠桌）；「沿板上桌」= P3 |
| G9 | 挡路盒 6 格 + 没有厚度过滤 | 第 ② 条**已关闭**：台阶规则取代了「固定 5 cm 过滤」（尺寸相关，更忠实），旧 C9 **作废**；第 ① 条**仍在**：门堆 1.5 m 内不要放其它未解锁大件 | 作者侧保持门堆附近干净 |
| **G10** | —— | **验收读数只有一个来源**：`gap` / `support` / `supportId` / `stepLimit` / `falling` 这些数只在 `StoryLevelLabBootstrap.Report()` 的 `[story-lab]` 行里。正式场景一旦不挂 Lab 脚本，P1/P2 的 Play 验收就失去读数 | §5.2 B 与 K1（拆一个只读诊断组件） |
| **G11** | —— | **导入器不产视觉**：`ground` / `boundary` 是空物体标记，`box` / `ramp` 是默认材质的 primitive cube | §5.3 C6：地面与矮墙复用现有对象与 `Art/Materials/Mat_Playground*`；K3 可选 |
| **G12** | —— | **数据与旧文档两套账**：`placement.json` 是 127 件 / 1500 u / 12 区，本文 §3.4 与 `LEVEL_AUTHORING` §11 P1 表仍写 110 件 / 412 u | §3.4 的指针；文档修正交 Lead（K6） |

## 5. Q5 迁移执行（Unity 操作员清单，2026-11-15 按现状重写）

> **这一节是给 Unity 操作员的**：每一步写「做什么 → 验收 / 回读 → 失败长什么样」。作者不是 Unity 操作者，**本节没有任何一步被执行过**。
> 每轮通用铁律：只有一名 agent 动 Unity；每批改动后 `state` + `read_console` 各看一次；导入器**不 `SaveScene`**，保存由人决定；改序列化数据时不要停在 Play Mode；`.meta` 随资产一起提交，**不重新生成已有 GUID**；`ProjectSettings/ProjectSettings.asset` 有既有未提交改动，**不要碰**。
> 轮次划分：**轮 1 = A → E（含 F1）**，**轮 2 = F2 + G**（世界内重试按钮 + 主房间）。理由在 F2 / G 小节末尾。
> 步骤编号与实现者改动编号不混：步骤用 `A/B/C/D/E`，代码改动用 **`K1…K7`**（§5.8）。

### 5.1 A 场景转正

| # | 做什么（精确到 API / 路径 / 数值） | 验收 / 回读 | 失败长什么样 |
|---|---|---|---|
| **A1** | Unity 侧搬家：先 `AssetDatabase.CreateFolder("Assets/VRC_Katamari", "Worlds")`，再建 `Worlds/StoryMode`（仓库里**没有** `Worlds/`，`MoveAsset` 不会替你建父目录）；然后 `AssetDatabase.MoveAsset("Assets/VRC_Katamari/Labs/StoryLevel/StoryLevel.unity", "Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity")`。`.meta` 由 Unity 一起搬，**GUID 不变** | ① `AssetDatabase.LoadAssetAtPath<SceneAsset>(新路径)` 非空、旧路径为空；② `AssetDatabase.AssetPathToGUID(新路径)` == 搬家前记下的 GUID（逐字比对）；③ 打开场景 Console 无 `Missing Script`、无 `outdated behaviour version`；④ `Labs/StoryLevel/` 下只剩 `Mat_Prop*.mat`（A4 处理） | 用「新建场景 + 复制对象」代替 `MoveAsset` → GUID 分叉，所有 `fileID` 引用要重指。**不要**这么做 |
| **A2** | git 侧（**Lead 做，在 Linux 检出**）：`git add -A` 让 git 自己识别 rename（`git status` 显示 `R`，不是 `D` + `A`）；**不要**再 `git mv` 一次 | `git status --short` 里是 `R  Labs/StoryLevel/StoryLevel.unity -> Worlds/StoryMode/StoryMode.unity`（`.meta` 同理）；`git diff --cached -M --stat` 显示相似度 100% | 两边各动一次 → 一半引用指向旧路径。**谁先做谁做**：建议 Unity 先做（`.meta` 由 Unity 管），Linux 侧只 `git add` |
| **A3** | 目录 `.meta`：`Worlds/`、`Worlds/StoryMode/` 的 `.meta` 与场景一起提交（Unity 建目录时自动生成） | `git status` 里两个新 `.meta` 在 staged 列表 | 漏提交目录 `.meta` → 另一台机器上 GUID 重排 |
| **A4** | `Labs/StoryLevel/Mat_PropLarge / Mat_PropMedium / Mat_PropSmall.mat`：旧 110 件删除后它们**没有消费者**。先 `AssetDatabase.GetDependencies` 复核，再决定「搬到 `Art/Materials/`」还是「删」 | 复核返回 0 个引用者；仓里不再有孤立 `.mat` | 留着 → 孤立资产，下一个人不敢删 |

### 5.2 B `StoryLevelLabBootstrap` 的去留（**建议：正式场景不挂，脚本删除**）

现状（`Labs/StoryLevel/StoryLevelLabBootstrap.cs`，头注释已自认）：它**不再向 motor 注册任何东西**，只剩三件：① `scriptedDrive` 时间线（前进 2 s + 转向 2 s + 松手 2 s，只走 `SetMoveAxes`）；② `cameraFollow`（把某个 Transform 摆到球后上方，当前场景里还是 `{fileID: 0}`，根本没接）；③ 每秒一行 `[story-lab]` 读数。

| 功能 | 正式场景还需不需要 | 处置 |
|---|---|---|
| 注册几何 | 已经不需要（归 `LevelArenaHost`） | 已删除，**不要再搬回来** |
| `scriptedDrive` | **不需要**。正式场景的输入是真玩家；而且它的时间轴只能证明「球会动」，证明不了「撞墙 / 上台阶 / P2 链」 | 随脚本删掉；验收期需要驱动时看 B3 备选 |
| `cameraFollow` | **不需要**。玩家自己就是相机；`FollowRigRoot / RideRoot / RideSeat` 已经在跟球，`cameraFollow` 是给**没有玩家**的 lab 视角用的 | 随脚本删掉 |
| `[story-lab]` 每秒读数 | **需要，但应该换个归属**：`gap=`（球是否真的坐在 0.26 台面上）、`support` / `supportId`、`stepLimit=`、`falling=` 是 P1/P2 的**唯一**验收证据（`TERRAIN_SUPPORT` §3.6 第 3 条与 §9 R11 也点名了这行） | **K1**：搬成 `Runtime/Story/StoryBallDiagnostics.cs` 只读诊断，正式常驻 |

**建议的动作顺序**：先落 K1（新诊断组件，常驻正式场景）→ 再删 `StoryLevelLab` 对象与脚本资产（B1/B2）→ 最后跑 E 轮验收（读数来自新组件的 `[story]` 行）。**不要**先删脚本再验收：那样 §5.5 的判据全部没有读数，只能靠肉眼。

- **B1** 场景：删 `StoryLevelLab` 根对象（含 proxy + backing `UdonBehaviour`）。验收：`grep -rn "StoryLevelLabBootstrap" Assets/` 里除了待删的 `.cs` 本身 **0 命中**；场景 0 Missing Script。
- **B2** 资产：删 `Labs/StoryLevel/StoryLevelLabBootstrap.cs` + `.cs.meta` + `.asset` + `.asset.meta` + `Assets/SerializedUdonPrograms/<hash>.asset`(+`.meta`)。验收：`Labs/StoryLevel/` 目录**整个消失**（A4 的材质搬走之后）；Console 无 `programSource = null`。
- **B3（备选，只在 K1 本轮做不了时用）**：E 轮验收期间**临时**保留 `StoryLevelLabBootstrap`（`scriptedDrive = true`），验收完再删。代价：正式场景里挂着一个叫 Lab 的脚本（`HUB_LOOP_PLAN` §7 配套 1 明确反对），而且它的时间轴（4 s 直行 + 转向）对 E6 / E7 没用。**不推荐**。
- **B4 不要做的事**：把 `StoryLevelLabBootstrap` 改名成 `StoryArenaHost`（旧 C2 的写法**已作废**——注册职责已经归 `LevelArenaHost`，改名只会留下一个空壳）。

### 5.3 C 清理旧内容与补视觉

先给一张「哪些删、哪些改、哪些留」的表（对象名取自当前场景的 20 个根对象）。**编号提示**：本节的 `C1–C8` 与旧版 §5.2 的实现者清单 `C1–C9` 同名但**不是一回事**——旧的清单已整体换成 §5.8 的 `K1–K7`，正文里凡是提到旧编号都写成「旧 C…」。

| 对象 | 现在 | 处置 | 理由 |
|---|---|---|---|
| `Ground` | `scale (20, 0.4, 20)`、`pos (0, −0.2, 0)`，顶面 `y = 0` | **改**：`localPosition (−12.7, −0.2, −12.7)`、`localScale (13.4, 0.4, 13.4)` | 正好覆盖 `x, z ∈ [−19.4, −6.0]`；材质 `Mat_PlaygroundGround` 保留 = 草地视觉（导入器不产地面视觉） |
| `Wall_South / North / East / West` | `±10`、`scale (20, 2, 0.4)`、各带 `BoxCollider` | **改**：见 C2 的四个坐标 | 它们是**玩家**的碰撞体；球的墙来自导入的 4 个 boundary 平面。导入器不会给 boundary 生成视觉 |
| `LevelArena/ArenaWall_North/South/East/West` | 4 个空物体，被 `LevelArenaHost.boundaryWalls` 引用 | **删**（导入后 `boundaryWalls` 会被改成 `LevelContent/Structures/boundary_*`） | 留着就是 8 个平面（上限 8，一格余量都没有） |
| `LevelArena`（根对象） | 挂 proxy + `UdonBehaviour` + `LevelArenaHost`（`staticBoxes` 目前为空） | **留**（只清空子物体） | 组件与 U# backing 已接好，重建等于重做一遍接线 |
| `PickupField`（根对象，挂 `PickupFieldHost`）+ 9 个 kind 文件夹 / 110 件 | 旧道具树与 110 行宿主数组 | **改名**为 `LevelContent`（受管根），**再删** 9 个 kind 文件夹（顺序见 C5：**先导入、后删**）；宿主组件留在根上 | 导入器的受管根必须叫 `LevelContent` 且是**场景根**；宿主是 `LevelContent/Props` 的祖先，导入报告才不会打「host is outside」（C4） |
| `Spawn` | `(0, 0.5, −3)` | **改**：`(−14.5, 0.5, −14.5)`、`Quaternion.Euler(0, 45, 0)` | = `placement.level.playerSpawn`；yaw 45° 面朝场内（东北） |
| `StoryLevelLab` | Lab 脚本 | **删** | §5.2 |
| `StoryRetryButton` | Transform + proxy + `UdonBehaviour`，**没有 Collider** | **留，本轮不动** | 它按不动（F2）；正式形态是房间里的 `HubStartButton` |
| `StoryHud`（4 行 Text + `ResultCard` + `RetryButton`） | 无 `CanvasScaler` | **改**（F1） | 裁切 |
| `SceneDescriptor` | `spawns` → `Spawn`（已对）；`ReferenceCamera = {fileID: 0}` | **改**：补 `ReferenceCamera` | 场景里**根本没有 Camera**（唯一的灯是 `Directional Light`），所以要么新建一个 Camera 指进去，要么接受 Play Mode 那条警告（`Labs/README.md`：对构建无影响） |

| # | 做什么 | 验收 / 回读 | 失败长什么样 |
|---|---|---|---|
| **C1** | 地面：`Ground` 的 transform 按上表改（顶面仍在 `y = 0`） | 顶视截图：地面正好覆盖 `[−19.4, −6.0]²`；Play 里 `gap = 0.000`（±0.002） | 只改尺寸不改中心 → 覆盖 `[−6.7, 6.7]`，球出生在半空 |
| **C2** | 四面玩家墙：南 `(−12.7, 1, −19.6) scale (13.8, 2, 0.4)`、北 `(−12.7, 1, −5.8) scale (13.8, 2, 0.4)`、西 `(−19.6, 1, −12.7) scale (0.4, 2, 13.8)`、东 `(−5.8, 1, −12.7) scale (0.4, 2, 13.8)`。**内表面正好落在边界线上**（厚度 0.4 → 中心外移 0.2）；长度 13.8 让四角叠合 0.2 m 不留缝 | 顶视截图：四面首尾相接、无缝隙；玩家走不到墙外；`[arena] planes=4/4` | 用 20 m 长的旧墙只挪中心 → 墙伸出场地 3 m，顶视图立刻看出来 |
| **C3** | 删 `ArenaWall_*` 四个子物体 | `LevelArenaHost.boundaryWalls` 4 项全指向 `LevelContent/Structures/boundary_*`；`[arena] planes=4/4`（**不是** 8/8） | 不删 → `planes=8/8`，一格余量都没有，两面墙的法线还可能互相打架 |
| **C4** | 把根对象 `PickupField` **改名**为 `LevelContent`（组件不动、引用不动） | 根对象名 = `LevelContent`；`PickupFieldHost` 仍在该对象上；导入报告的 notes 里**没有**「the host … is outside …」 | 忘了改名 → 导入器自己再建一个 `LevelContent`，场景里两个根、宿主在另一个里 → 报告打 note，归属不清 |
| **C5** | 删 `LevelContent` 下 9 个旧 kind 文件夹（`fruit / toy / garden / debris / food / drink / container / tableware / nature`，共 110 件）。**顺序：先做 §5.4 的导入，再删**——导入会重写宿主数组指向新件，此时删旧件不会留下悬空引用 | `LevelContent` 下只剩 `Props/`、`Structures/` 与宿主组件；`propTransforms` 数组里没有 `{fileID: 0}` 槽位 | 先删后导入 → 有一轮宿主数组指向被删对象（不会崩，但中间态很难看，且一旦导入失败场景就脏了） |
| **C6** | **视觉补丁（导入器不产）**：① 地面 = C1 的 `Ground`（`Mat_PlaygroundGround`）；② 矮墙 = C2 的四面 `Wall_*`（`Mat_PlaygroundWall`）；③ 6 个 primitive cube（花圃台 / 指示牌 / 树根丘 / 角树 / 两块斜板）= 导入后手赋 `Art/Materials/Mat_PlaygroundProp.mat` | 场景视图里 0 个灰白 / 洋红 cube；Play 截图能认出花圃台与墙 | 想「让导入器顺便产视觉」→ 那是 K3（可选），本轮不要手改导入器 |
| **C7** | 球与玩家：motor `startCenterWorld = (−12.5, 0.15, −12.5)`；`maxCoreRadiusWorldM` 保持 `1.1`（D 上限 2.20）；`terrainSupportEnabled = 1`、`stepHeightOverBallDiameter = 0.25`（都已是现状值，复核即可）；`Spawn` 按上表 | Play 首帧球心 ≈ `(−12.5, 0.15, −12.5)`；出生点到 `Spawn` < 0.5 m 且面朝东北（截图） | 只改 `Spawn` 不改 `startCenterWorld` → 玩家在角里、球在原点，走两秒才发现 |
| **C8** | `SceneDescriptor.ReferenceCamera`：新建一个 Camera（摆在球起点后上方，例如 `(−12.5, 4, −17)` 朝北）并指进去 | Play Mode 无 `Reference camera is not set` 警告 | 指到 `BallView`（它没有 Camera 组件）→ 字段非 0 但仍然是错的 |

**为什么墙建议 2.0 m 而不是 placement 的 1.2 m**：`placement` 的 `boundary` 只是一个平面声明（`size [1.2, 0.04, 13.4]`），导入器把它做成**空物体**（`scale = 1`），这个 `size` **没有任何运行时消费方**——墙的几何完全由操作员决定。VRChat 允许跳跃，1.2 m 的墙玩家一跳就出去了，而球的墙是无限平面、与高度无关。**想兼顾观感与限制**：视觉做 1.2 m、另加一个 2.0 m 的不可见 `BoxCollider`——那要多一个对象，属 P2 美术，本轮先用 2.0 m。

### 5.4 D 导入

| # | 做什么 | 验收 / 回读 | 失败长什么样 |
|---|---|---|---|
| **D0** | **前置（今天还不满足）**：44 个 def 的 `Art/Props/<def>/<def>.fbx` 必须全部就位，且**每个 FBX 的根节点自带 Renderer**（导入器对根无 Renderer 的模型报错并删实例）。实测当前只有 **16/44**，缺 28 个：`beer_crate / book_stack / bowl / bread_slice / cheese_slice / cooler_box / cooler_box_large / crate / crate_large / croissant / cup / fallen_leaf / folding_stool / folding_table / glass_bottle / juice_carton / lounge_chair / picnic_basket / picnic_bench / plate / sandwich / serving_tray / small_potted_plant / soda_can / spoon / storage_bin / stroller / wood_plank` | `ls Art/Props \| wc -l` ≥ 88（44 目录 + 44 `.meta`）；逐个 `AssetDatabase.LoadAssetAtPath<GameObject>` 非空 | 缺模型 → 报告里 28 条 `no model at …`，`props=99`，`level.expected` 对不上 |
| **D1** | 前置状态：不在 Play Mode；场景已转正（A）；`LevelContent` 是场景根（C4）；场景里**恰好 1 个** `PickupFieldHost`、**恰好 1 个** `LevelArenaHost`（多个时导入器只同步层级序第一个并打 note） | `FindObjectsOfType<PickupFieldHost>().Length == 1` 等 | 两个宿主 → 只有第一个被同步，另一个的数组永远是旧的 |
| **D2** | 跑菜单：**`VRC Katamari/Level/Import Placement (LevelContent)`**（`PlacementImporter.MenuPath`）。它按 `placement.json` 的 `level.scene` 解析关卡（`level.id = story_01_bench_size`），**不 `SaveScene`** | Console 的 headline 与 summary | 菜单不存在 → 编辑器脚本没编译（`refresh_unity` 后重看） |
| **D3** | **第一次导入**的期望读数（逐字比对） | ```[level] +127 ~0 -0 props=127 other=+11 ~0 -0 errors=0``` · `pickup sync: ran, rows=127, rejected=0` · `valueUnits: declared=1500, preserved=0, undeclared=0, read-back: verified` · `arena: wired (boxes=4, walls=4, ramps=0 [P1 has no ramp slot], kinds=11)` | `props=99` → D0 缺模型；`-110` → 旧件也在 `Props/` 下（C4/C5 没做对）；`read-back: NOT verified` → 声明值没写进去；`arena: not wired` → 场景里没有 `LevelArenaHost` |
| **D4** | **第二次导入（幂等门）** | ```[level] +0 ~0 -0 props=127 other=+0 ~0 -0 errors=0```；`valueUnits … verified` 不变 | 第二次仍 `+127` → 幂等键坏了（按名字 / 顺序而不是 `itemId`），报 Lead |
| **D5** | 保存场景（人决定时机）：`Ctrl+S`（或 `EditorSceneManager.SaveScene`） | `git status` 里场景 modified；`git diff --stat` 只动该场景 | 和导入混在同一批保存 → 把半成品固化（导入器故意不保存） |
| **D6** | 抽检：5 件坐标与 JSON 误差 < 1 cm；`LevelContent/Props` 下件数 = 127；球起点 1.0 m 内 0 件；花圃台足迹内 0 件 | 用一次 `execute_code` 遍历读 `transform.position` 比对 JSON | 误差 > 1 cm → `PlacementImporter` 的位姿写错了，**报 Lead，不要手调** |

### 5.5 E Play 验收（每步都要截图 + 读数）

> 读数来源：K1 的 `[story]` 行（推荐）或临时保留的 `[story-lab]` 行（B3 备选）。**没有读数就不算通过。**
> 驱动：操作员按不了键（`SetMoveAxes` 从 C# 调不进 Udon heap，`Labs/README.md` 已实测）。所以 E2 / E4 / E5 / E6 / E7 需要**人在 Play Mode 里按键**；操作员负责盯 Console 与截图。若用户不在场，用 CUA 往 Unity 窗口发按键只做一次性验收，不要写进常驻流程。

| # | 步骤 | 通过判据（读数 + 截图） |
|---|---|---|
| **E1** | 进 Play Mode，站桩不动 | ① `[arena] planes=4/4 boxes=4/4 supports=5 refused=0 kinds=11`；② 球心 ≈ `(−12.5, 0.15, −12.5)`、`gap = 0.000`、`support = ground`；③ HUD 三行可读（F1 之后）；④ Console 无 `Reference camera is not set`；⑤ 角外的 3.6 m 长凳等「看得见吃不到」的大件可见 |
| **E2** | 地面滚动 | 球前进、`CoreMesh` 滚动方向与位移一致；`axes` 与输入一致；速度到 `maxSpeedMps = 4`；**没有**抖动或自行漂移 |
| **E3** | 撞墙（四个方向各一次） | 球心到墙面距离 = 碰撞半径（±0.01）；`center=` 读数稳定；从墙外侧截图确认不穿墙 |
| **E4** | 吸收 | `absorbed=` 增长、HUD「当前」增长、球肉眼变大；吃起步环（`D_req 0.03–0.12` 的小件）无延迟；`rej=` 不增长 |
| **E5** | 挡路 | `D = 0.30` 撞保温箱 / 西瓜 / 皮球 → **停住**，`rej=` 出现 `TOO_LARGE`；`D ≥ 0.83` 吃掉最外侧的篮子后能进壁龛（净宽 0.10 → 0.55） |
| **E6** | **P1 台阶**（花圃台 0.26） | `D = 1.00` → 被挡；`D ≥ 1.04` → 上去；静止后 `gap = 0.000 ± 0.005`、`support=box`、`supportY=0.260`；台顶沿边来回 10 s：`center.y` 波动 < 1 mm、`support` 不翻转；从台顶开出去 → `support=ground`、`falling` 至少一帧为真、落回后 `gap ≈ 0` |
| **E7** | **P2 链**（台顶 → 躺椅 0.35 → 折叠桌 0.70） | 站上台顶后能跨上躺椅（rise 0.09，`D ≥ 0.36` 就有资格）；`D ≥ 1.45` 能上桌；`support=prop`、`supportId ≥ 100`；站上桌后 `D ≥ 2.00` 吃掉桌子 → `falling` 至少一帧、落回地面。**两个数要盯**：① 桌子的可用窗口是 `[1.40, 2.00)`（`4×rise` 到 `D_req`），1.40 是**等号**，实测请按 1.45 判（`D = 1.40` 时 `stepLimit` 恰好等于 rise，任何一点浮点误差都会让球掉下去）；② 躺椅 yaw 90° → 世界足迹 `x∈[−12.6,−12.0]`、`z∈[−10.4,−8.8]`，折叠桌 `x∈[−13.6,−12.6]`、`z∈[−9.5,−8.9]`，两者**只在 `x = −12.6` 这条线相接（重叠面积 0）**，不是旧稿写的「0.5×0.2 m」。链能不能过必须实测 |
| **E8** | 斜板（P3 前） | 球从坡面**直接穿过去**：不减速、不上升、`support` 不变。截图一张，标注「P3 缺口，不是 bug」 |
| **E9** | 限时结束与结算 | 120 s 到点 → `SettleRound` 冻结 → 卡面显示「呈给国王」→ 3 s 后星 / 直径 / 评语；`GetBankedScoreUnits() == 0`；HUD 隐藏。**重试按钮本轮不验**（F2）：它没有 Collider，uGUI 那条也没接 `EventSystem` |
| **E10** | 方向指示与入座视点 | 推杆时 `PushDirectionCue` 指向球的前进方向（截图）；点球入座后眼点在球后上方、球在画面下前方不糊镜头；`[ride] seated entered` 一行 |
| **E11** | 截图清单（存 `Docs/Evidence/StoryLevel/<日期>/`） | ① 场景视图顶视（与 `/tmp/corner_layout/corner_absorb.png` 对照）；② 球视角：起步 / 台顶 / 壁龛口；③ 撞墙帧；④ 上台阶帧（含 `gap` 读数）；⑤ P2 链三帧；⑥ HUD 三行；⑦ 结果卡；⑧ 斜板穿模帧 |

**E 轮的已知风险**（不进判据，但要写进报告）：`startOnLoad = 0` 意味着回合要**坐上球**才开始，而 ClientSim 里的坐姿驱动本仓**没有验证过**（`HUB_LOOP_PLAN` §10 第 2 条）。若坐不上，临时把场景实例上的 `startOnLoad` 改成 1 跑一轮，**在报告里写明这是验收临时值**（不要提交）。

### 5.6 F HUD 裁切与「再试一次」：**拆成两半，F1 本轮做，F2 下一轮**

**F1（本轮做，S 大小）—— HUD 裁切。** 证据：`StoryHud` 上只有 `Canvas`（`m_RenderMode: 0` = Screen Space Overlay），**没有 `CanvasScaler`**；四行 Text 是固定像素 rect（锚点左上、`(24,−24) / (24,−68) / (24,−112) / (24,−200)`、`sizeDelta 940×40`、字号 24、`m_Alignment 0` = UpperLeft、`HorizontalOverflow 0` = Wrap、`VerticalOverflow 0` = Truncate）。**没有任何缩放层**，画面一变窄（ClientSim 的 Game 视图常比 940 px 窄）文字就右溢出、换行被 `Truncate` 切掉，左边那 24 px 在窄视口里还会顶到屏幕边缘。

修法（都在场景里，不改代码）：① `StoryHud` 加 `CanvasScaler`（`ScaleWithScreenSize`，参考 1920×1080，`matchWidthOrHeight = 0.5`）；② 四行 Text 改成左右拉伸（`anchorMin (0,1)`、`anchorMax (1,1)`、`offsetMin.x = offsetMax.x = 24`），高度 40 → 32；③ `ResultCard` 保持居中。
验收：1920×1080 / 1280×720 / ClientSim 默认三个尺寸各截一张，四行文字**完整**（左右各留 ≥16 px、不换行），`ResultCard` 与 `RetryButton` 不出画。

**F2（下一轮做，跟着 G）—— 世界内的「再试一次」按钮。** 三条证据说明它不能顺手做掉：
1. `RetryButton`（uGUI `Button`）的 `onClick` **唯一一条 persistent call 的 `m_Target` 是 `{fileID: 0}`**（空），而且**场景里没有 `EventSystem`** → 两条都不成立，补一条不够。
2. `StoryRetryButton` 对象只有 `Transform` + proxy + `UdonBehaviour`，**没有 `Collider`** → 世界内 `Interact` 也按不动；要能按就得给它形状（一块板 + Collider），那是房间面板那一轮的美术决定。
3. `HUB_LOOP_PLAN` §8 已定正式形态：房间卡面上的「再来一次」挂 `HubStartButton`（`levelIndex = 0`）→ `HubLevelLauncher.PressStart()`，由 hub 先传送再开局。**现在挂 `StoryRetryButton` 会在 G 轮被换掉**（它已降级为「无房间场景的调试入口」），等于返工。

### 5.7 G 主房间：**不建议同一轮**

| 理由 | 说明 |
|---|---|
| 抢同一个字段 | G 轮必须把 `SceneDescriptor.spawns` 从角里的 `Spawn` 改到房间（`HUB_LOOP_PLAN` §3 / §9 第二步），而 E 轮**全部验收**的前提是「进 Play 就在角里、球在 2.8 m 外」。同轮做 → E 的验收方式直接作废 |
| 风险面不同 | A–E 是「移动资产 + 一次性导入 + 改 4 个 transform」，每步都能回读；G 是「1200 m 外新长一套几何 + 7 个跨组件引用 + 结果卡搬家」，错了只表现为「传送没到」，与关卡问题混在一起无法二分 |
| 基线证据要先采 | E 轮要产出球速 / 成长曲线 / P1–P2 帧读数这些**基线**；G 轮一落地，`startOnLoad = false` + 传送就把「进 Play 立刻能滚」这条路径改掉，基线采不到 |
| 传送本身还没验 | `HUB_LOOP_PLAN` §9 第一步（传送探针）**还没跑过**：`TeleportTo` 在 ClientSim 的行为、离座时序都是未验证项。把未验证的传送塞进搬家轮 = 两件未验证的事叠在一起 |

推荐排期：**轮 1** = A–E（+F1）；**轮 2** = F2 + G（房间 + 世界按钮 + 结果卡搬家 + `spawns` 改房间）；**轮 3** = P3 斜面（另一条线，`TERRAIN_SUPPORT` §8 P3）。

### 5.8 实现者改动清单（交 Lead 派活；本节不改代码）

| # | 文件 | 改什么 | 为什么 / 验收 |
|---|---|---|---|
| **K1** | 新 `Runtime/Story/StoryBallDiagnostics.cs`（U#，`BehaviourSyncMode.None`）+ `.meta` + `.asset` | 只读诊断：每秒一行 `[story]`，字段与今天的 `[story-lab]` **同名同序**（`t / center / heading / diameter / bottom / gap / support / supportId / supportY / supports / stepLimit / falling / axes / absorbed / rejects`）；**只有** `motor` 一个引用；不要 drive、不要相机 | 正式场景不再挂 Lab 脚本（B），但 P1/P2 的 Play 验收**必须有** `gap / support / stepLimit / falling`（`TERRAIN_SUPPORT` §3.6-3、§9 R11）。验收：每秒一行、字段齐全；`StoryLevelLabBootstrap` 删除后 Console 无 `Missing Script` |
| **K2** | 删 `Labs/StoryLevel/StoryLevelLabBootstrap.cs / .cs.meta / .asset / .asset.meta` + `Assets/SerializedUdonPrograms/<hash>.asset(+meta)` | 整删 | K1 落地后它没有消费者（`AGENTS.md`：不要平行草稿 / 一次性 builder）。**顺序：先 K1 后 K2** |
| **K3** | `Editor/LevelBuild/PlacementImporter.cs`（可选） | 给 `box` / `ramp` 的 primitive cube 赋一个**可配置材质**（序列化字段或 `Mat_PlaygroundProp` 常量）；给 `ground` / `boundary` 标记一个「此处需要视觉」的提示 | 现在 C6 全靠人手赋材质：重跑导入不会覆盖（导入器不碰 Renderer），但下一个人不知道。优先级**低**，不做也不阻塞本轮 |
| **K4** | `Runtime/BallMotor/KatamariBallMotor.cs` + `Runtime/Story/LevelArenaHost.cs` | `AddRamp / ClearRamps / GetRampCount / GetLastRampRefusalReason` + `rampBoxes` 烘焙（P3） | 斜板今天对球**不存在**（E8）。列出来是为了防止有人把「球穿过斜板」当 bug 修成「把斜板塞进 `staticBoxes`」（那会造坡脚隐形墙）。**不属本轮** |
| **K5** | `Runtime/Story/StoryRetryButton.cs` | 正式形态并入 hub（`HubStartButton` 那条调用链）；本轮不动 | F2 的三条证据（无 `EventSystem`、`onClick` 目标为 0、世界按钮无 Collider） |
| **K6** | `Docs/Design/Tools/LEVEL_AUTHORING.md` §11 P1 表 + §8 第 14 条 | 「110 件 / 412 u」→「127 件 / 1500 u」，期望值同步 | 文档修正，不在本 agent 写范围：本文 §3.4 与那份 P1 表是**同一处过期** |
| **K7** | `Docs/Design/StoryMode/PICNIC_CORNER_LAYOUT.md` §7 的注 | 那句「`struct_lounge_chair_01` 是 `yaw = 90`，它的 `size` 已经是**旋转后**的世界 AABB」与 `placement.json` 不自洽：JSON 的 `aabb` 是**旋转前**（`[1.6, 0.35, 0.6]`），世界 AABB 由 `aabb + rotationQuat` 算出来是 `[0.60, 0.35, 1.60]`；两处都按「已旋转」读，挡路盒会**再转一次** | 交 Lead 判：改那句话或改 JSON 的 `aabb`。**影响 E7 的足迹判读** |

### 5.9 本轮不做什么（防误伤）

不改 `KatamariBallMotor` / `SupportSurfaceMath` / `PickupFieldHost` / `StoryRoundController` / `StoryStarMath`（数值不动，等 (p)）；不改 `placement.json` 与任何地图数据；不改 `tasks.json`；不建 `rampBoxes`；不动 `Packages/` 与 `ProjectSettings/`；不新建第二张场景；不在 `Labs/` 里留第二份 `StoryLevel.unity`。

## 6. Q6 风险与未决（需要用户拍板，带选项与代价）

> **2026-11-15 状态更新**：U1（160 m²）、U2（内容重排）、U3（暖壶按真实尺寸建模；现在 `thermos` 已不在本关）、U5、U6、U7、U8 已由 [`Docs/OPEN_DECISIONS.md`](../../OPEN_DECISIONS.md) 的 (q) 条与 [PICNIC_CORNER_LAYOUT](PICNIC_CORNER_LAYOUT.md) §9 拍板。**仍然开着的只有 U4**（120 s 与星级 1.25/1.45/1.60 要不要按新内容 1500 u 重定——(q) 明写「现阶段不调星级数」）。下表保留原问题与代价，作为决策历史；旧口径里的「110 件 / 412 u」按 §3.4 的指针读成「127 件 / 1500 u」。

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
| **U4** | **120 s / 1.25 m 三档是否先测再议** | **A（推荐）先测一轮，不改数**（(p) 现行）：§5.5 E9 跑一次（120 s 满局），用实测决定 | 一次 Play Mode 的 2 分钟；结论可能指向"时间太松" |
| | | B 现在就按新密度把时限/星级重算 | 违反 (p)，且新场地的手感还没测过，等于盲调 |
| **U5** | **PicnicLawn 全图与 3.20 m 上限** | **A（推荐）本关只管角，全图另案**；但先确认一个事实：`layout.json` 写 `ball_max_collision_diameter_m = 2.2`，而 (n)/`PROP_SET_PLAN §2.8.5` 说野餐桌/长椅/长凳改由 "PicnicLawn 3.20 m 上限"承接 —— **两处口径不一致**，且校验器按 2.20 判路线余量 | 若真要 3.20，PicnicLawn 的路线净宽（3.40/4.00/5.00）与余量（1.20）要重算，校验器会先报 warning/error；不确认就会在"门件该不该可吸"上反复 |
| | | B 现在就把 PicnicLawn 改成 3.20 | 需要同时改 `scale`/路线/关卡快照/README 手抄值，且两份关卡 JSON 的 `map_snapshot` 要重钉（不在本次写范围） |
| **U6** | **毯缘 6 cm 算墙还是纯视觉** | **A（推荐）纯视觉、不加 Collider**：球滚到边界时被北平面挡住，视觉上"毯子在墙外"，不出现"巨球被一块布挡住" | 需要毯子不带碰撞体（今天它就是 `plane_raised` + 0.06 m，本来也不是玩家必经） |
| | | B 当墙：毯缘进碰撞集 | 吃掉 1 个盒；观感"布墙" |
| **U7** | **第一关是否也用整图的 452 件散布** | **A（推荐）不用**：角内只放本关 110 件；整图的 `spawn_sw`(12) + `tree_roots_sw`(14) + `flower_bed_sw`(12) + `fringe_west/south` 的角内段（各约 6–7 件）合计 **≈50 件 / ≈50 单位**会白送进角，412 立刻失真 | 与"一个角就是一张小图"一致 |
| | | B 用整图散布替代 110 件 | 关卡阈值、件数目标、关卡 2 的水果计数全部要重算 |
| **U8** | **结构门件怎么算进 110/412** | **a（推荐）3 篮 + 1 箱顶替 4 支暖壶**：件数仍是 110、单位仍是 412，顺带把 U3 的 64 u 死账换成"一扇会消失的门"（门后 28 u 是壁龛内容，从 `outer` 挪过去） | 需要 2 个新模型（野餐篮、保温箱，真实尺寸）+ 它们的声明单位（`PickupFieldSync` 已改为只消费声明值并回读，见 §5.4） |
| | | b 结构件**另加**（不顶替）：114 件 / 476 u，★1.25 = 51.7%、上限 1.55 m | 关卡阈值要重定（与 (p)"现在不调数值"冲突），且 ★ 掉到 52% 偏松 |
| | | c 结构件只当布景、不进宿主 | 没有"挡路 → 食物"的翻转，等于放弃用户补充的那条原则 |

**其它风险（不需要拍板，但必须知道）**：

1. **密度翻倍**：≈0.29 → **0.79 件/m²**（127 ÷ 160.0，比旧稿的 110 件更高），`120 s` 的达标会更容易（这是 §5.5 E9 要测的第一件事）。
2. **球被 6 cm 毯缘挡住**（若选 U6-B）或**自己陷进毯子 6 cm**（若选 U6-A 且毯子在同一平面）——两者都是视觉问题，不是物理问题。
3. **指示牌数据不自洽**（`position.y = 0` vs 1.2 m 高）：本关按柱脚 0 建模；地图数据留给 owner。
4. **`PickupFieldSync` 重跑会改 `valueUnits`**：真实尺寸落地后大多数件落 s/m 档，412 会变；重跑必须与阈值重定同批做。
5. **单人专属**：同场景两块内容、同一台 Udon VM，多人时两个人的球会叠在一起（球无 Collider），HUB_LOOP_PLAN §6.4 已判"现阶段明确只支持单人"。
6. **"门封死"是采样结果，不是几何保证**：吸收门看 `propBounds` 体积，挡路盒只取最近的 ≤6 件（§4.4 G9）。门堆附近必须保持干净，验收要看截图，不能只看数据。
7. **门后的 28 u 在 0.55–0.82 m 窗口期是"看得到进不去"**：这是有意的奖励房，但必须确认它不在 ★ 的必经路径上（245.99 u < 门外 384 u，已算过）。
8. **结构件的"竖向"只有 0.22–0.45 m**：篮子躺倒 0.22 m、箱子 0.30 m，视觉上是"一堆野餐装备"，不是原作那种"台面 + 台面杂物"的高度。台面层（L3）现在**能上**（`D ≥ 1.04`），只有悬空/叠放的 L4 仍做不到（§4.4 G8）。

## 7. 写范围、改动与校验结果

### 7.1 改了哪些文件（只有两个，且都不含机器可读数字）

**第一轮（写稿，2026-11-15 上午）：**

| 文件 | 改动 | 为什么不动机器可读文件 |
|---|---|---|
| `Docs/Design/StoryMode/PICNIC_CORNER_LEVEL_ONE.md` | 新建（本文） | —— |
| `Docs/Design/Maps/PicnicLawn/README.md` | **追加**一节「§18 体积口径下的可吸性审计（2026-11-15）」，指向本文；既有章节（含 §3.3 被测试逐字比对的可行性表）**一字未动** | —— |
| `Docs/Design/Maps/PicnicLawn/layout.json` | **未改** | 被 `Docs/Design/GameModes/Levels/story_01_bench_size.json` / `story_02_fruit_ten.json` 的 `map_snapshot.layout_sha256` 钉住；改一个字节 → `validate_levels.py` 报「地图已变更」、`Docs/tests/test_levels.py` 变红；重钉要改 `Levels/*.json`（不在写范围，且 (g) 明确禁止工具自动刷新哈希） |
| `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` | **未改** | 同上（`catalog_sha256`）；另外它的 `game_longest_dim_m` 同时喂校验器第 11 条与生成器的脚印模型，改了会让 `build_greybox --check` 与 README 可行性表一起失效 |
| `Docs/Design/Maps/PicnicLawn/unity_greybox.json` | **未改** | 生成物：`--check` 要求它与作者数据逐字节一致，手改即失效；重生成要先改上面两个文件 |

**第二轮（§4 / §5 重写，2026-11-15 傍晚）**：只改本文件——§4、§5 整节重写；§0 / §1 / §2 / §3 / §6 / §7 只就地改正与之矛盾的句子，每处都带「2026-11-15 起 / 已作废 / 已关闭」这类标注，不删历史。没有碰任何数据文件（`layout.json` / `prop_catalog.csv` / `unity_greybox.json` / `placement.json`）、没有碰代码、场景、prefab、`tasks.json`。

### 7.2 第一轮实跑的命令与结果（上午，留档）

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

### 7.3 第一轮没跑 / 跑不了（留档）

- **Unity 编译、UdonSharp 编译、EditMode/PlayMode、Build & Test：NOT_RUN**（本 agent 不是 Unity 操作者）。
- **角内布置的件数/净空/间距**：第一轮 `NOT_RUN`（当时没有数据文件）；第二轮已由 `placement.json` + 校验器接管（`levelbuild check` 0 错），但**导入与 Play 验收仍是 `NOT_RUN`**（§5.4 / §5.5）。
- **`D_req` 表**：算术（`D_req = (60·V/π)^(1/3)`），可用 §3.3 的 AABB 手算复核；`估` 行是立方体上界，不是实测。
- **结构门件的"挡路 → 可吸"翻转、0.10 m 门缝是否真封死、6 格挡路盒会不会漏：NOT_RUN**（要 Play Mode；§3.5 的算术只证明阈值，不证明采样）。
- 本文不改 `tasks.json`，所以计划校验与基线一致不是"本文的功劳"。

### 7.4 实施顺序（**已由 §5.7 的轮次划分取代**）

旧写法（C1/C2 + S1–S3 先做…）**已作废**：C1/C2 由 `LevelArenaHost` 取代，S 编号整套换成 §5 的 A→G。现在的排期只有三行：

1. **轮 1 = §5.1 A（转正）→ §5.3 C（清理 + 视觉）→ §5.4 D（导入）→ §5.5 E（Play 验收）**，外加 §5.6 F1（HUD 裁切，S 大小）与 §5.2 B 的前置 K1。
2. **轮 2 = §5.6 F2 + §5.7 G**（主房间 + 世界内重试按钮 + 结果卡搬家 + `spawns` 改房间）。
3. **轮 3 = P3 斜面**（`TERRAIN_SUPPORT` §8 P3，另一条线）。

仍然成立的两条接缝：**挡路/支撑那条线**已经落地（`PickupFieldHost.UpdatePropObstacles`，P2），所以旧稿第 3 条对门件的担心只剩「作者侧保持门堆附近干净」；**`Runtime/Hub/` 与本文不冲突**，但两边都不能改 `StoryRoundController` 的状态机（(m)）。

### 7.5 第二轮（§4 / §5 重写）的实跑结果

作者仍然不是 Unity 操作者：**没有编译、没有 Play Mode、没有 Build & Test、没有跑任何 NUnit**。本轮只读代码/场景/数据并跑仓库的 Python 门：

```text
python3 Docs/tools/validate_plan.py
  -> PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
     Dependency roots (external requirements NOT checked): T00, T01, T02, T03
     NOT_RUN: Unity/Udon compilation, VR comfort, multiplayer, performance, commerce.
python3 -m unittest discover -s Docs/tests
  -> Ran 396 tests in 3.616s
     OK
```

读代码/读数据时实际确认过的事实（§4 / §5 的每一条都落在这里面，不是凭记忆写的）：

| 事 | 怎么确认的 |
|---|---|
| `LevelArenaHost` 已在场景里 | 场景 YAML：`LevelArena` 根对象上挂着 `LevelArenaHost`（script guid `a1f0be70…`）+ `UdonBehaviour`，`boundaryWalls` 4 项、`staticBoxes` **空**、`allowedKinds` 9 项、`autoBeginPlaying 1` |
| `StoryLevelLabBootstrap` 只剩驱动/镜头/日志 | 读 `Labs/StoryLevel/StoryLevelLabBootstrap.cs` 全文：`Register`-类调用 0 处；`cameraFollow` 在场景里是 `{fileID: 0}` |
| motor 的地形字段全在 | 场景 YAML：`terrainSupportEnabled 1`、`stepHeightOverBallDiameter 0.25`、`gravityMps2 9.81`、`maxStepUpLiftWorldM 0.6`、`stepHysteresisM 0.02`、`supportDropTriggerM 0.01`、`maxCoreRadiusWorldM 1.1`、`startCenterWorld (0,0.15,0)` |
| 台阶判定含等号 | `SupportSurfaceMath.IsStepWithinLimit`：`return rise <= stepLimitM;` |
| 斜板没有运行时槽位 | `grep -rn "AddRamp\|rampBoxes" Runtime/ Editor/` 只命中 `PlacementImporter` 的两处注释/note；`LevelArenaHost` 无 `rampBoxes` 字段 |
| 导入器的受管根约束 | `PlacementImporter.FindOrCreateManagedRoot`：按名字找**场景根**，非根则 `Fail`；`propsRoot = LevelContent/Props`；`host` 不在 `propsRoot` 祖先链上时打 note |
| 场景里没有 Camera、没有 EventSystem | `m_Name` 全表 + 组件扫描：唯一灯是 `Directional Light`，0 个 `Camera`、0 个 `EventSystem`；`SceneDescriptor.ReferenceCamera = {fileID: 0}` |
| 「再试一次」按不动 | `RetryButton` 的 `Button.onClick` 唯一一条 persistent call `m_Target = {fileID: 0}`；`StoryRetryButton` 对象只有 `Transform` + proxy + `UdonBehaviour`（无 `Collider`） |
| HUD 没有缩放层 | `StoryHud` 只有 `RectTransform` + `Canvas`（`m_RenderMode 0`），无 `CanvasScaler`；四行 Text 固定 `940×40`、锚点左上、`Wrap` + `Truncate` |
| 127 件 / 1500 u 的构成 | `placement.json`：`level.expected {propCount 127, valueUnits 1500}`，逐件 `valueUnits` 求和 = 1500，11 kind、12 zone、结构 1 ground + 4 boundary + 4 box + 2 ramp |
| 模型还没齐 | `Art/Props/` 只有 16 个 def 目录，`placement.json` 需要 44 个 → **缺 28 个**（§5.4 D0） |
| 躺椅/折叠桌的足迹 | 由 `aabb × rotationQuat` 手算：躺椅 yaw 90° → `x∈[−12.6,−12.0]`、`z∈[−10.4,−8.8]`；折叠桌 → `x∈[−13.6,−12.6]`、`z∈[−9.5,−8.9]`；**只在 `x = −12.6` 相接** |

**仍然 `NOT_RUN` 的**（这些只能由 Unity 操作员做，见 §5）：A（转正）、B（删 Lab 脚本）、C（清理与视觉）、D（导入与两次幂等）、E（Play 验收）、F（HUD 与按钮）、G（主房间）。`K1–K7` 是代码/文档改动，本轮**一行代码都没改**。
