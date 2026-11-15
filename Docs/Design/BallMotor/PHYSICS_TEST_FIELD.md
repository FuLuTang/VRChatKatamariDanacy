# 地图 0：球体物理测试场（Physics Test Field）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15（Asia/Shanghai）。**设计与数据 + 第一批（C14/C09/C08/C10）的实现**：判据数据、四份 `placement.json`、Labs 运行时组件与编辑器导入器都已落盘；**没有编译、没有 Play Mode、没有 Build & Test、没有跑 Unity/NUnit**（本 agent 不是 Unity 操作者），**没有改 `KatamariBallMotor` / `LevelArenaHost` / `PickupFieldHost`、没有跑 git 写命令**。文中所有米数、角度、容量都是本项目暂定值（逐条标 `PROVISIONAL`），不是原作测量值。
> 定位：**给我们自己用的球体物理回归场，不是给玩家玩的关卡**。它只有两个用途：① 在 P1/P2/P3 每阶段落地后，5–10 分钟内把「地形支撑 / 台阶 / 斜面 / 台面与边缘 / 吸附物凹凸 / 挡路 / 贴墙」全部跑一遍并自动判 PASS/FAIL；② 给出可对比的数字（球心、支撑面高、法线、接触数、每步成本），让「手感变了」能落到「哪个读数变了」。
> 上游：地形支撑模型与 P1/P2/P3 分期见 [TERRAIN_SUPPORT.md](TERRAIN_SUPPORT.md)；挡路见 [BLOCKING_PROPS_PLAN.md](../PickupAbsorb/BLOCKING_PROPS_PLAN.md)；原型格位的最初设想见 [BALL_PHYSICS_IMPLEMENTATIONS.md](../../Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md) §3.4；小棍三上限见 [02_TECHNICAL_CONTRACTS.md](../../02_TECHNICAL_CONTRACTS.md) §3。
> 数据分两份，各管一件事：**几何**在 `Docs/Design/Levels/physics_test_field/<cell>/placement.json`（每格一份，`levelbuild check` 是门）；**判据 / 驱动时间线 / 球径 / 机位 / 起点 / 激活矩形**在 [physics_test_field.json](physics_test_field.json)（v2，schema_version=2）。两者不一致时以各自文件为准并修表；跨文件的一致性由 `Docs/tests/levelbuild/test_physics_test_field_cells.py` 守（几何必须在该格的激活矩形内、`nominal_m` 必须等于台面高、每个 metric 必须在驱动里能读到）。
> 图：`/tmp/ptf/shots/physics_test_field_preview_plan_workbench.png`（俯视：四格在 88 m 场里的位置与尺寸，一次性产物，**不入库**；同目录另有 `..._plan_field_workbench.png` 全场视图与 `..._overview_workbench.png` 3/4 视图）。合成预览用的是一份 `/tmp/ptf/preview/placement.json`（四格合并、不入库），真源仍是四份格的 placement。
> 引用约定：`Runtime/…`、`Labs/…`、`Editor/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。

---

## 0. 结论速览（先看这七条）

1. **21 个格位、84 次运行（21 格 × 4 个球径）、128 条可自动判定判据。** 四带一中心：北=斜板（6）、东=台阶（6）、南=薄件/贴墙（4）、西=挡路+长杆+台面+组合（4），中央=起点与球径台（1）。
2. **容量是这张图的硬约束，不是美学。** 电机上限「静态盒 8 / 平面 8 / 斜面 4」装不下 21 格，因此本场采用 **一格一注册**：切格 = `motor.ClearObstacles()` → 目标格 `LevelArenaHost.Reapply()`。这条不是权宜，而是把 `LevelArenaHost.Reapply()` 变成**必须可用**的接口要求（TERRAIN_SUPPORT §7.3 已列，但没写消费方；本场是第一个）。
3. **判据全部落在「已有的 getter」或「每固定步一行的 `[ProxyTrace]` 日志」上**。唯一现在拿不到的是每步成本计数器（`activeSurfaceCount` / 单步耗时），本场把它列成**待加 getter**（§6.4），拿到之前 `readout=counter` 的判据报 `BLOCKED_UNTIL_P4`，而不是假装能测。
4. **球径设定不改运行时代码**：用现有接缝做闭环——`SubmitCandidate(体积单位)` 喂体积 → `GetSemanticDiameterWorldM()` 读回 → 迭代收敛；回 0.30 用 `TryResetForNewRound`（它会把球重置回 baseline 直径）。三条路线的权衡在 §8.3。
5. **三处判据落在等号上**，必须写成「含等号」才自洽：0.075 m 台阶 vs `stepLimit(0.30) = 0.075`；45.0° vs `maxSupportSlopeDegrees = 45`；`collapsed_bench` 的 0.55 m 高 vs `stepLimit(2.20) = 0.55`。这三处要么全按 `≤` 读，要么全按 `<`；**混用会产出互相矛盾的 PASS**（§6.5）。
6. **P1 已合入，所以「现在就该 PASS 的第一批」是 C14**：它的 0.028 m 板在 `stepLimit(0.30) = 0.075` 之内，P1 之前必 FAIL（隐形墙），P1 之后必 PASS；FAIL 那次也是有效证据。剩下只需「地面 + 挡路 + 扫掠 + 径向代理」的 C00/C15/C16/C17/C18/C19 还没有各自的 placement（它们在 `planned_cells`）。
7. **`napkin` 的厚度有数据冲突**：文档写 2.8 cm，`napkin.manifest.json` 实际是 **1.4 cm**。本场用一块**静态 0.028 m 薄板**钉住文档口径（C14），同时把真 napkin 放进挡路行（C18 #1），两条路径分开走（§12 U3）。

> **树状态（2026-11-15 08:00 复核，替换此前「getter 不存在、未接进 motor」的过期描述）**：P1 的支撑链**已经进树**——`KatamariBallMotor` 里 `GetSupportSurfaceHeightWorldM` / `GetSupportNormalWorld` / `GetSupportSurfaceId` / `GetSupportSurfaceCount` / `GetStepHeightLimitWorldM` / `IsFalling()` / `GetSupportKind()` / `GetStepMaskRebuildCount` 都在（`Runtime/BallMotor/KatamariBallMotor.cs`），`Runtime/Story/LevelArenaHost.cs` 也在（`groundNormal/groundOffset`、`boundaryWalls`、`staticBoxes`、`allowedKinds`、`Reapply()`），`SetTerrainSupportEnabled` 让旧 lab 能退回 P1 之前的行为。**仍然没有**的是：`AddRamp`/`rampBoxes`（P3）、道具支撑（P2）、每步成本计数器（P4）。所以本文的 gate 规则是：`gate=P1` 的判据**现在就该算**（P1 getter 已在），`gate=P2/P3` 报 `BLOCKED_UNTIL_P2/P3`；`readout=trace` 的判据由人读 `[ProxyTrace]`，驱动报 `BLOCKED_UNTIL_manual`；`readout=counter` 报 `BLOCKED_UNTIL_P4`。这三类都**不算失败**（§5.3）。
> **本轮新增**（§9.5）：`Labs/PhysicsTestField/` 三个组件（`PhysicsTestCell` / `PhysicsTestFieldDriver` / `LabBallSizeSetter`）与 `Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs` 已写，**未编译**。

---

## 1. 这个场要证明什么：八个物理问题 → 八类格位

| # | 物理问题 | 一句话判据 | 格位 | 阶段 |
|---|---|---|---|---|
| Q1 | 球站得住吗（地形支撑） | 球心 − 支撑面 = r，10 s 波动 < 1 mm | C13（台面静置）、C07–C09 | P1 |
| Q2 | 台阶能上吗（step-up） | `rise ≤ 0.25D` 的可上、其余是墙，且**不穿模** | C07–C12 | P1 |
| Q3 | 斜坡能上吗（ramp） | 爬到坡顶 `center.y = 1.5 + r ± 1 cm`，坡底没有隐形墙 | C01–C05 | P3 |
| Q4 | 斜面护栏生效吗 | 50° 被 `AddRamp` 拒绝并写原因，球被兜底墙挡住 | C06 | P3 |
| Q5 | 台面边缘会掉下去吗 | 从 0.6 m 直角边开出后逐帧下落、落地 `= r`、无瞬移 | C13、C20 | P1 |
| Q6 | 吸附物凹凸有上限吗 | 每步抬升 ≤ 0.04、总突出 ≤ `min(0.35r, 0.30)`、速度不受扰动 | C19 | 现在 |
| Q7 | 吸不动的挡得住吗 | 挡在 `propBounds` 盒外 r 处；长大后同一件变食物/变台阶 | C18 | 现在 / P2 |
| Q8 | 贴墙/夹缝会卡死或穿墙吗 | 4 m/s 不穿薄墙；内角不抖不弹能出来；0.32 m 缝卡住后能恢复 | C15、C16、C17 | 现在 |

> 为什么把「吸附物凹凸」放在**现在**：小棍（径向代理）已实现，`[ProxyTrace]` 每固定步一行、`GetGroundProxyLiftWorldM()` / `GetProxyProtrusionCapWorldM()` 已在。这一格是**今天就能给结论**的，不该等地形。

---

## 2. 场地总图

### 2.1 坐标与朝向约定

| 项 | 值 |
|---|---|
| 引擎/单位 | Unity 2022.3.22f1；米；左手系；`+Y` 上、`+Z` 北、`+X` 东 |
| 地面 | 一张解析平面 `n = (0,1,0)`、`offset = 0`（`motor.SetGroundPlane` 后的等价物） |
| 边界 | 4 张平面 `x = ∓44`、`z = ∓44`（`AddPlane`，只当墙、永不当支撑） |
| 场界 | 88 m × 88 m 方框；实际内容占 `x ∈ [-41, 24]`、`z ∈ [-34, 40.14]` |
| 朝向 | `heading 0 = +Z`，`90 = +X`，`180 = -Z`，`-90 = -X`。**依据代码**：`BallMotionMath.TryStep` 里 `forwardX = sin(h)`、`forwardZ = cos(h)`，`lateral = (cos h, 0, -sin h)` |
| 起点 | `(0, 0.15, 0)`、`heading 0`、`D = 0.30`（0.15 = 起始球半径） |

### 2.2 布局（俯视图）

> 俯视图（本机临时产物，**不入库**）：`/tmp/ptf/shots/physics_test_field_preview_plan_workbench.png`。
> 它由 `Tools/levelbuild/blender/render_views.py` 从**四份格的 placement 合并出的预览文档**渲染（`/tmp/ptf/preview/placement.json`，一次性的合并脚本，不入库）；本机 Workbench 一张 3–4 s。图上能直接量出来的是：C14 的 6×6 m 板在西南、C09/C08/C10 的 6×4 m 台阶沿 z 排成一条（z = −4 / −9 / +1），四格都在 88 m 场框内，其余 17 格还没有几何（`planned_cells`）。图丢了不要紧，placement 才是权威，按它重画即可。

| 带 | 位置 | 内容 | 为什么放这儿 |
|---|---|---|---|
| 中央 | `x,z ∈ [-6, 6]` | C00 起点 + 球径设定台 | 到四个带都在 20–45 m 内；所有格都可以从这里滚过去（§3.3） |
| 北带（`z ≥ 19`） | `x ∈ [-15, 14]` | C01–C06 六条斜板车道（宽 4、间隔 1） | 坡长最长 17.14 m，需要最深的一条空间；六条平行便于一帧侧视对比 |
| 东带（`x ∈ [6, 18]`） | `z ∈ [-16, 13]` | C07–C12 六条台阶车道（沿 +X） | 台阶只需要 6 m 进深，用东侧；与北带以 `z = 13 < 19` 分开 |
| 南带（`z ≤ -22`） | `x ∈ [-24, 24]` | C14 薄片、C15 薄墙、C16 内角、C17 夹缝 | 这四格都需要 8–10 m 的加速跑道；南带空间最整 |
| 西带 | `x ≤ -10` | C18 挡路行（z=0）、C19 长杆（z=14）、C13 台面（z=-12）、C20 组合（z=-12） | 挡路与长杆不需要跑道几何，只要空地 |

> **格与格之间是真的分开的**：每格有自己的足迹矩形（图上灰底），相邻足迹至少留 1 m（同带内）或 4 m（跨带）。因为「一格一注册」（§3），**别的格的几何在物理上不存在**，所以「隔开」是物理上的，不只是画出来的。

### 2.3 场景对象

| 对象 | 作用 | 复用自 |
|---|---|---|
| `MotorHost` | 挂 `KatamariBallMotor`，球心/朝向的唯一所有者 | StoryLevel |
| `PlayerBall/BallPhysicsRoot → BallVisualRollRoot → CoreMesh` | 位置宿主 + 旋转宿主 + 视觉球（三层固定层级） | StoryLevel |
| `RideRoot/RideSeat` | 人玩时的骑乘 Station（可选，测试场不强依赖） | StoryLevel |
| `Input` | `DesktopMoveInput`（人玩）；脚本运行时由驱动器直接 `SetMoveAxes` | StoryLevel |
| `PickupField` | `PickupFieldHost`：**唯一**的挡路写入者，也是道具行宿主 | StoryLevel（`StoryLevel.unity` 里确实挂了实例） |
| `LevelContent/<cellId>` | 受管根（由 `PlacementImporter` 建）：该格的 `Props/`、`Structures/`，**并挂该格唯一的 `LevelArenaHost`** | 新（工具建） |
| `Cells/C14` … | 每格一个空物体，挂 `PhysicsTestCell`（引用上面那个 `LevelArenaHost`）。**它不放进 `LevelContent`**：受管子树每次导入都可能被删改，而组件不能跟着消失 | 新（手工加组件） |
| `Driver` | `PhysicsTestFieldDriver`（§7） | 新（手工加组件） |
| `SizeConsole` | `LabBallSizeSetter`（§8）+ 4 个 uGUI 按钮（可选） | 新 |
| `Cameras/cam_*` | 8 个机位（§6.5） | 新 |
| 根对象 | 必须挂 `VRC.Core.PipelineManager`；`VRCSceneDescriptor.ReferenceCamera` 指向主相机 | `Labs/README.md` 的硬要求 |

---

## 3. 容量预算：为什么必须「一格一注册」

### 3.1 电机的上限（现状 + 计划）

| 容量 | 值 | 来源 |
|---|---|---|
| 平面 | 8 | `MaxPlaneCount`（现状） |
| 静态盒 | 8 | `MaxBoxCount`（现状；地图数据契约，`validate_map.py` 也抄成 8） |
| 斜面 | 4 | `MaxRampCount`（P3 计划） |
| 挡路盒 | 6 | `MaxBlockingBoxCount`（现状） |
| 道具支撑 | 6 | `MaxSupportPropCount`（P2 计划） |
| 每步接触 | 4 | `ContactCapacity`（现状） |

### 3.2 一张图装不下的算术

| 资源 | 全场合计（同时注册） | 上限 | 结论 |
|---|---|---|---|
| 斜面 | **8 次登记**（C01–C06 六条 + C13 + C20；其中 C06 应被拒 → 实际登记 7 条） | 4 | **超 100%**（7 > 4） |
| 静态盒 | **24**（6 坡顶台 + C06 兜底 1 + 6 台阶 + C13 1 + C14 1 + C15 1 + C16 2 + C17 4 + C20 2） | 8 | **超 200%**（24 > 8） |
| 平面 | 4 | 8 | 够 |

**超容量不是「小一点」而是「坏掉」**：`AddBox` / `AddPlane` 超容量会**丢弃并 latch** `overflowCount`，而这个 latch 只有 `Start` 能清（`SignalOverflow`）。所以「先全注册、超了再说」会得到一个**永久带病的会话**，后面所有格的判据都不可信。

### 3.3 结论：一格一注册

```
切格（driver）：
  1. motor.ClearObstacles()            // 清平面/盒/斜面 + 连带清挡路集（现状语义）
  2. targetCell.ApplyRegistration()    // = levelArenaHost.Reapply()：地面 + 目标格的盒/斜面
  3. pickupHost.ResetField()           // 道具回原位；清候选、挡路、径向代理
  4. 记基线：overflow / saturated / absorbed / blockingCount
```

- **base 只留「地面平面 + 4 面边界墙」**（1 平面表项 + 4 平面 = 5/8；盒 0/8）。切到任何格时预算都够：最坏的 C17 用 4 盒、C06 用 2 盒 + 1 斜面。
- **切换触发**：`active = argmin 到各格足迹的距离`，外扩 `activate_margin_m = r + 2.0`（球在几何出现前 2 m 就已经看见它），并要求连续 0.25 s 稳定（去抖）。球离开所有足迹 1.0 s 后回落到 base。
- **这就是「球可以从中央起点滚到任一格」的实现方式**：中央起点在 C00 足迹内，滚出 6 m 后进入 base，接近某格 2 m 时该格激活。**代价**：任意时刻只有正在接近的那一格是实心的——这一条要写进「怎么用这张图」的说明里，不然第一次用的人会以为别的格坏了。
- **已知成本**：每次切格 `ClearObstacles` + `Reapply` 各重建一次扫掠数组（`new Vector3[]` 一对，TERRAIN_SUPPORT §3.5 已列这个 GC 风险）。本场每个完整 pass = 21 次切格 + 84 次重置，正好是**测这个成本的最小场景**：把它变成 P4 的 profiler 项，而不是猜。
- **已知风险**：切格瞬间新格的盒出现在球脚下 → 走 `SweepMath` 的初始穿透分支，可能被推一下。两个缓解：① 预激活带 2 m；② 传送到格起点时**先切格、再传送**（顺序不能反）。

### 3.4 一格一注册已经落地：每个 arena 必须自带地面 + 边界

`LevelArenaHost.Reapply()` 的实现是 `motor.ClearObstacles()` → `Setup()`，而 `ClearObstacles` 会把**全局**的平面表清零。所以切格那一刻，上一个 arena 的地面与 4 面边界墙一起消失，新的 arena 必须把它们重新登记上。这不是「多花 5 个槽位」，而是**每个格必须是一份完整 arena**：

| 每个 cell 的 placement 里必须有 | 数量 | 原因 |
|---|---|---|
| `role=ground` | 1 | `SetGroundPlane` 是支撑兜底；不登记就没有地面 |
| `role=boundary` | 4 | 切格清空平面表；少一面球就能滚出 88 m 场 |
| `role=box` | 该格 0–8 | 本批 4 格各 1 |

代价写在明处：4 面墙的**标记对象**在场景里按格重复（4 格 16 个空物体），它们是 `Transform` 不是碰撞体，所以只为「每个 arena 自洽」付一点层级噪音。**不要**试图让某格引用 `base` 的地面/墙——那只在「最后注册的那个 host 拥有所有平面」的规则下看起来能用，一旦切格就自相矛盾。

（历史：P1 之前没有 `LevelArenaHost`，只能一场景一注册，`C14+C15+C16+C17` 恰好用满 8 盒。那个 Stage A 已经被本节的做法取代；`placement.json` 的 `budget` 现在每格各自数一遍。）

---

## 4. 格位清单

> 表里的「各球径的主判据」括号内是**预期结果**（`✓` = 该判据在该球径下应通过，`×` = 应失败——`×` 往往是**正确行为**，例如台阶在 0.30 m 球面前就该是墙）。判据 id 与 JSON 一一对应。
>
> **「几何」这一列是设计值**，不是场景数据的副本：已迁移的 4 格的真源是 `Docs/Design/Levels/physics_test_field/<id>/placement.json`（尺寸/位姿/坡度全在那里，`levelbuild check` 复核容量与地面），未迁移的 17 格只有本表这一处记录，等它们各自的批次按 §9.2 的流程建成 placement 后再删掉本表里的重复描述。判据侧只保留「本格的自变量」`nominal_m`（板厚 / 台阶高），因为每条 deck/step 判据都是相对它写的。

<!-- table:cells -->

| 格 | 名称 | 几何（关键尺寸，m） | 各球径的主判据 | 对应阶段 |
|---|---|---|---|---|
| C00 | 中央起点 · 球径设定台 | 地面 + 4 边界平面 | rest_gap_m abs<= 0.005（0.30✓ 0.60✓ 1.10✓ 2.20✓） | 现在（P1 前） |
| C01 | 5° 斜板 | 斜板 5° 升 1.50 水平 17.14 宽 4；盒 4.000×1.500×4.000 @ (-13.0, 38.1) | deck_gap_m abs<= 0.01（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P3 |
| C02 | 15° 斜板 | 斜板 15° 升 1.50 水平 5.60 宽 4；盒 4.000×1.500×4.000 @ (-8.0, 26.6) | deck_gap_m abs<= 0.01（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P3 |
| C03 | 25° 斜板 | 斜板 25° 升 1.50 水平 3.22 宽 4；盒 4.000×1.500×4.000 @ (-3.0, 24.2) | deck_gap_m abs<= 0.01（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P3 |
| C04 | 35° 斜板 | 斜板 35° 升 1.50 水平 2.14 宽 4；盒 4.000×1.500×4.000 @ (2.0, 23.1) | deck_gap_m abs<= 0.01（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P3 |
| C05 | 45° 斜板 | 斜板 45° 升 1.50 水平 1.50 宽 4；盒 4.000×1.500×4.000 @ (7.0, 22.5) | deck_gap_m abs<= 0.01（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P3 |
| C06 | 50° 斜板（应爬不上：拒绝注册） | 斜板 50° 升 1.50 水平 1.26 宽 4；盒 4.000×1.628×1.412 @ (12.0, 19.7)；盒 4.000×1.500×4.000 @ (12.0, 22.3) | ramp_refusal_reason_len >= 1（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P3 |
| C07 | 台阶 0.020 m | 盒 6.000×0.020×4.000 @ (15.0, -14.0) | deck_gap_m abs<= 0.005（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P1 |
| C08 | 台阶 0.050 m | 盒 6.000×0.050×4.000 @ (15.0, -9.0) | deck_gap_m abs<= 0.005（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P1 |
| C09 | 台阶 0.075 m | 盒 6.000×0.075×4.000 @ (15.0, -4.0) | deck_gap_m abs<= 0.005（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P1 |
| C10 | 台阶 0.100 m | 盒 6.000×0.100×4.000 @ (15.0, 1.0) | deck_gap_m abs<= 0.005（0.30× 0.60✓ 1.10✓ 2.20✓） | P1 |
| C11 | 台阶 0.200 m | 盒 6.000×0.200×4.000 @ (15.0, 6.0) | deck_gap_m abs<= 0.005（0.30× 0.60× 1.10✓ 2.20✓） | P1 |
| C12 | 台阶 0.400 m | 盒 6.000×0.400×4.000 @ (15.0, 11.0) | deck_gap_m abs<= 0.005（0.30× 0.60× 1.10× 2.20✓） | P1 |
| C13 | 0.6 m 台面：一侧斜坡上、一侧直角边下 | 斜板 11.31° 升 0.60 水平 3.00 宽 6；盒 6.000×0.600×6.000 @ (-16.0, -12.0) | deck_gap_m abs<= 0.005（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P1 |
| C14 | 0.028 m 薄片（餐巾级） | 盒 6.000×0.028×6.000 @ (-21.0, -28.0) | progress_m >= 8.0（0.30✓ 0.60✓ 1.10✓ 2.20✓） | P1 |
| C15 | 0.1 m 薄墙（高速不穿 + 贴墙滑） | 盒 6.000×2.500×0.100 @ (-7.0, -30.0) | blocked_gap_m >= -0.01（0.30✓ 0.60✓ 1.10✓ 2.20✓） | 现在（P1 前） |
| C16 | 90° 内角（不抖不弹、能出来） | 盒 6.000×2.500×0.100 @ (7.0, -30.0)；盒 0.100×2.500×4.000 @ (10.0, -32.0) | xz_std_m <= 0.002（0.30✓ 0.60✓ 1.10✓ 2.20✓） | 现在（P1 前） |
| C17 | 0.4 m 夹缝（高速不穿 / 卡住能恢复） | 盒 2.800×2.500×0.600 @ (19.4, -28.0)；盒 2.800×2.500×0.600 @ (22.6, -28.0)；盒 2.840×2.500×0.600 @ (19.4, -31.0)；盒 2.840×2.500×0.600 @ (22.6, -31.0) | passed_plane_z_m <= -28.6（0.30✓ 0.60× 1.10× 2.20×） | 现在（P1 前） |
| C18 | 挡路 → 长大可吸 递进（9 件） | 无静态几何 | absorbed_delta >= 1（0.30✓ 0.60✓ 1.10✓ 2.20✓） | 现在（P1 前） |
| C19 | 长杆/板吸附（单长棍测试） | 无静态几何 | attached_delta >= 1（0.30× 0.60✓ 1.10✓ 2.20✓） | 现在（P1 前） |
| C20 | 组合路线：斜坡 → 台阶 → 台面 → 边缘落下 | 斜板 15° 升 0.60 水平 2.24 宽 6；盒 3.760×0.600×6.000 @ (-34.1, -12.0)；盒 5.000×0.700×6.000 @ (-38.5, -12.0) | deck_gap_m abs<= 0.01（0.30× 0.60✓ 1.10✓ 2.20✓） | P3 |

### 4.1 球径档位与派生上限（四个 preset 的全部数值）

<!-- table:diameters -->

| 球径 D | r | stepLimit = 0.25D | 突出 cap = min(0.35r, 0.30) | 可吸体积上限 (π/6)D³/10 |
|---|---|---|---|---|
| 0.30 m | 0.150 | **0.0750** | 0.0525 | 0.001414 m³ |
| 0.60 m | 0.300 | **0.1500** | 0.1050 | 0.011310 m³ |
| 1.10 m | 0.550 | **0.2750** | 0.1925 | 0.069691 m³ |
| 2.20 m | 1.100 | **0.5500** | 0.3000 | 0.557528 m³ |

> 三列的关系要记住：**台阶看第一列、长杆看第二列、挡路看第三列**。同一颗球，`stepLimit` 与「突出上限」是**两个独立的比例**（0.25×D vs 0.35×r），不要互相代用。

### 4.1.1 C01–C06 斜板组：同一升程、不同坡度

设计取 **固定升程 1.50 m**（不是固定水平长），这样六条车道的坡顶高度一致、落差一致、判据一致，只有坡度不同。水平长 = `1.5 / tan θ`：

| 格 | 坡度 | 水平长 | 沿坡长 | 坡顶 z | 预期 | 说明 |
|---|---|---|---|---|---|---|
| C01 | 5° | 17.14 | 17.21 | 36.15 | 可上 | 最缓；17 m 是全场最长的一条，也是「坡上不减速」最容易被看出来的地方 |
| C02 | 15° | 5.60 | 5.80 | 24.60 | 可上 | 和 C20 的坡同角度（0.60 升程），便于横向对照 |
| C03 | 25° | 3.22 | 3.55 | 22.22 | 可上 | 研究文档 §3.4 的「松手会不会下滑」争议点（TERRAIN_SUPPORT U3：现状不会滑） |
| C04 | 35° | 2.14 | 2.62 | 21.14 | 可上 | 单步法向位移 0.08·sin35 = 0.046 ≤ 0.1（余量 2.2×） |
| C05 | 45° | 1.50 | 2.12 | 20.50 | 可上（等号） | 0.08·sin45 = 0.0566 ≤ 0.1（余量 1.8×）；`maxSupportSlopeDegrees = 45` 是否**包含** 45 就是这一格要回答的 |
| C06 | 50° | 1.26 | 1.96 | 20.26 | **拒绝** | `AddRamp` 应拒绝并写原因；同一个 Collider 再烘焙成 `staticBoxes` 兜底墙，球停在坡脚 AABB 面外 r 处 |

**C06 为什么要有兜底墙**：拒绝一条斜面之后，如果什么都不登记，球会直接穿过视觉上的坡（没有支撑、没有障碍）。TERRAIN_SUPPORT S16 的原话是「球被坡旁的墙挡住（作者必须补墙）」。本场把这条做成**同物件两用**：`rampBoxes` 里放它（触发拒绝）+ `staticBoxes` 里放它的**世界 AABB**（4.000 × 1.628 × 1.412，底在 y = −0.128，顶正好 1.50）。顶面 1.50 > 任何 `stepLimit`（最大 0.55），所以这个 AABB 盒**永远不会变成支撑候选**，只当墙。

**坡顶接缝**：坡顶与坡顶台面的高度差 δ = 0（都 1.50），所以支撑面在接缝处高度连续；台面盒是否从扫掠里剔除由 step 规则自动决定（接缝处 `rise ≈ 0 ≤ stepLimit`）。

### 4.2 台阶组 C07–C12：六个 rise × 四个球径的完整矩阵

<!-- table:steps -->

| 台阶 rise | D=0.30 (0.075) | D=0.60 (0.150) | D=1.10 (0.275) | D=2.20 (0.550) |
|---|---|---|---|---|
| 0.020 | **可上** | **可上** | **可上** | **可上** |
| 0.050 | **可上** | **可上** | **可上** | **可上** |
| 0.075 | **可上** | **可上** | **可上** | **可上** |
| 0.100 | 墙 | **可上** | **可上** | **可上** |
| 0.200 | 墙 | 墙 | **可上** | **可上** |
| 0.400 | 墙 | 墙 | 墙 | **可上** |

- 这是**一张 6×4 的预测表**，不是 6 个孤立测试：任何一个格的结果都能被另外 23 个格交叉检验。实现若把 `k` 从 0.25 改成 0.30，这张表会有 4 个格子翻转（0.100@0.30、0.200@0.60、0.400@1.10…）——**表本身就是回归基线**。
- **边界格 C09（0.075 @ D=0.30）**：`rise == stepLimit`。按 TERRAIN_SUPPORT §4.1 的公式（`rise ≤ stepLimit` 判台阶）应**可上**。如果实测是墙，那说明实现用了严格大于 —— 那是**要修的契约偏差**，不是「浮点抖动」。同理 C12 在 2.20（0.40 ≤ 0.55，明确可上）与 `collapsed_bench` 的 0.55 == 0.55（边界）。
- **每格几何只有 1 个盒**（6 m 进深 × 4 m 宽），跑道 4.5 m（`x 7.5 → 12`）。0.30 m 球 4.5 m 足够加速到 4 m/s；2.20 m 球在同样跑道里起步，读数更干净（球大、探测点少）。
- **为什么不做「台阶上下各一层」**：下来不需要台阶（走出足迹 → 支撑回落 → `maxDropPerStep` 钳住，TERRAIN_SUPPORT §4.7 明确不做特例）。加一层下台阶只会多一个盒、多一个判据，不多测任何东西。

### 4.3 台面与边缘 C13

| 入口 | 起点 | 需要 | 测什么 |
|---|---|---|---|
| `entry=teleport` | 台面中心上方 `(-16, 0.6+r, -12)` | **P1**（盒顶支撑） | 站上去稳（10 s，σy < 1 mm）、从西侧直角边掉下去（落地 `= r ± 5 mm`、≥ 3 帧、单帧 ≤ 0.5 m） |
| `entry=ramp` | `(-8, r, -12)`，`heading -90` | **P3**（斜面） | 沿 11.31° 坡上台面、接缝不跳（≤ 0.02 m/帧） |

- 台面 6×6 m、厚 0.6 m；坡宽 6、水平 3.0 m（11.31°）。**为什么是 3 m 水平**：2.20 m 球（r = 1.1）在 1.5 m 的短坡上会同时压住坡与台面，读数会变成「骑棱」而不是「上坡」；3 m 让大球的球心完整走过坡面。
- 直角边**四面都是直角边**（没有扶手，TERRAIN_SUPPORT §5.3 的「侧边不加挡板」）。四条边都能掉下去，判据只用西侧那条（机位固定、可复现）。
- 两侧的落差读数用 `support_gap`：**它天然是「球心到当前支撑面的法向距离」**。球在台面上它 = r；球刚越过边缘、脚下换成地面时它 = r + 0.6，然后**按重力积分**下落（`SupportSurfaceMath.ResolveFallState` + `TryIntegrateFall`：半隐式欧拉，每步的 fall budget 同时当抬升与下降钳位，0.6 m 约 18 步 / 3.5 帧）。所以「掉下去」不需要新 getter，只要看 `support_gap` 的时序——但**不要把「每步 ≤ 0.1 m」写进判据**：那是旧的 `maxDropPerStep`，现在只用于地形过渡，不用于自由落体。
- **P1 就能验「站上去稳」是这一格存在的理由**：斜面（P3）与台面稳定性（P1）绑在一格里，会让人误以为「等坡做完再测台面」。拆成两个入口就没有这个借口。

### 4.4 薄件与贴墙 C14–C17

| 格 | 几何 | 关键判据 | 为什么这么设计 |
|---|---|---|---|
| **C14** | 静态薄板 6×0.028×6 | 真的碾过去（`progress ≥ 8 m`）、速度比 ≥ 0.98、抬升 ≤ 0.028 + 5 mm、不进挡路集、不打满接触缓冲 | 0.028 < 0.075 = `stepLimit(0.30)`：这是 P1 的**旗舰反例**，P1 之前必 FAIL（PICNIC G9 的隐形墙），P1 之后必 PASS。**FAIL 的那次也是有效证据** |
| **C15** | 薄墙 0.1 厚 × 2.5 高 × 6 宽，9 m 跑道 | 4 m/s 撞：任何一帧 `球心到墙面 ≥ r − 1 cm`；停住时 `= r ± 1 cm`；顶住 2 s 的 `stuckSeconds ≤ 0.5`；倒车 1.5 s 内有速度；**另跑一次 30° 入射**：切向保留 ≥ 95%、法向 ≤ 0.05 m/s | 「高速不穿」只有一个真实威胁：扫掠的 TOI 在眼球帧/长步长下被跳过。0.1 m 是项目里最薄的墙（`COLLIDER_LAYER` 的薄墙档），用它当最坏情况 |
| **C16** | 两面墙交成 90° 内角（横向 6×0.1 + 侧向 0.1×4，高 2.5） | 顶住最后 1 s 位置 σ < 2 mm；最后 0.5 s 速度 ≤ 0.05 m/s；倒车 1.5 s 位移 ≥ 0.4 m；**记录**接触饱和 | 两条法线同时作用 + `ProjectionPasses = 4` 的迭代投影，是「抖动/弹开/永久卡死」三种症状的唯一最小复现。饱和信号只记录：`ContactCapacity = 4` 在这里最可能满，但满≠错 |
| **C17** | 两道门：A 净宽 **0.40**、B 净宽 **0.32**（各 2.5 m 高、0.6 m 厚，间隔 2.7 m） | D=0.30：进 A、在 B 前卡住（`stuck ≥ 0.2 s`）、倒车 + 再前进累计位移 ≥ 0.5 m（不是死锁）；D ≥ 0.60：在 A 前被挡（`= r ± 3 cm`）；**任何球径任何帧都不能越过 B** | 0.40 是用户点名的口径；0.32 = 0.30 + 2 cm 净空，是「几乎贴死」的卡住场景。两个门在同一格，一次跑完「不穿 / 卡住 / 恢复」 |

> **C17 的 0.40 对 D=0.30 是「能过」，对 D=0.60 是「过不去」**——这不是矛盾，是同一几何对不同球径的正确答案。判据表里那一行写的是 `passed_plane_z_m ≤ -28.6`，球径更大会**按预期失败**（`×`），驱动器的 verdict 是 `EXPECTED`。

### 4.5 挡路 → 长大可吸 C18（9 件，按体积门排序）

<!-- table:props -->

| # | 道具（真实模型） | 尺寸 m | AABB m³ | E | D_abs | 0.30 | 0.60 | 1.10 | 2.20 | 角色 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | bread_slice 面包片 | 0.100×0.100×0.014 | 0.0001 | 1.00 | **0.140** | 吸 | 吸 | 吸 | 吸 | blocking_progression |
| 2 | thermos 保温壶 | 0.109×0.105×0.300 | 0.0034 | 2.76 | **0.403** | 挡 | 吸 | 吸 | 吸 | blocking_progression |
| 3 | serving_tray 托盘 | 0.550×0.350×0.040 | 0.0077 | 1.57 | **0.528** | 挡 | 吸 | 吸 | 吸 | blocking_progression |
| 4 | wood_plank 木板 | 1.400×0.350×0.025 | 0.0123 | 4.00 | **0.616** | 挡 | 挡 | 吸 | 吸 | blocking_progression |
| 5 | picnic_basket 野餐篮 | 0.450×0.300×0.221 | 0.0299 | 1.50 | **0.829** | 挡 | 挡 | 吸 | 吸 | blocking_progression |
| 6 | cooler_box 保温箱 | 0.550×0.350×0.300 | 0.0578 | 1.57 | **1.033** | 挡 | 挡 | 吸 | 吸 | blocking_progression |
| 7 | folding_table 折叠桌 | 1.000×0.600×0.700 | 0.4200 | 1.43 | **2.002** | 挡 | 挡 | 挡 | 吸 | blocking_progression |
| 8 | collapsed_bench 倒塌长凳 | 1.600×0.550×0.810 | 0.7128 | 1.98 | **2.388** | 挡 | 挡 | 挡 | 爬 | blocking_progression |
| 9 | ptf_support_crate 测试矮台桩（提议新增，测试专用） | 1.200×0.500×1.000 | 0.6000 | 2.40 | **2.255** | 挡 | 挡 | 挡 | 爬 | support_prop |

- 读法：**吸** = 已达 `D_abs`（`a·b·c ≤ (π/6)D³/10`，只有这一个门）；**挡** = 吸不动 **且** `rise > stepLimit`；**爬** = 吸不动 **但** `rise ≤ stepLimit`（P2 之后它变成支撑，不是墙）。
- `D_abs` 的公式是 `(60·V/π)^(1/3)`，由 `IsBeyondAbsorbVolumeLimit` 的同一个门反解 —— 表里的数**不是**拍的，是算的。
- **2.20 档有两个「吸不动但矮」的件**：bench 高 0.55 **正好等于** `stepLimit(2.20) = 0.55`（等号，见 §5.5，只记录不判）；crate 高 0.50 < 0.55（9% 余量，且 `D_abs = 2.255 > 2.20` 永远吸不动）——**P2 的「同一件道具从挡路变通路」用 crate 判**，bench 只打 `value`。
- **每件单独子运行**（JSON 的 `sub_runs`，10 条：9 件 + 1 条整排走一遍）：球复位到该件正东 `2.2 + r` 处再撞 3 s，只让这一件进挡路/吸收判定。不是容量问题，而是**判据归属问题**：一件一件测，`absorbed_<prop>` 与 `blocked_<prop>` 才能各自归因。
- **容量 6 在本场只能做负向验证**：挡路件都是大件（0.45–1.6 m），2.5 m 间距下球周围最多 3–4 件同时进 `SelectNearest` 的范围——**构造不出「7 个候选」的拥挤场景**（要挤就得让大件的 AABB 互相重叠，那又是另一个错误）。所以 `crowd_capacity` 判据是「`blocking_max ≤ 6` 且不 latch」，正向的夹取行为交给 NUnit `OnlyTheNearestCapacityPropsBlock`。这条限制写在这里，是为了让下一个人不要为了「凑 7 件」去改间距。
- **为什么第一件是能吃的 bread_slice**：它给「食物」一个基线读数（`absorbed_delta ≥ 1` 在四个球径都成立），用来区分「没吃到，因为挡路」与「没吃到，因为吸收环坏了」。这一条在很多 lab 里被漏掉，导致「球不动」时无法判断是几何还是宿主。

### 4.6 长杆/板吸附 C19（单长棍测试）

<!-- table:long -->

| 长物件 | 尺寸 m | E | D_abs | 关键读数 |
|---|---|---|---|---|
| ptf_rod_1500 测试长杆（提议新增，测试专用） | 1.500×0.060×0.060 | 25.00 | 0.469 | 截面 6 cm、长 1.5 m：D=0.60 原始突出 0.45 → 被 cap 0.105 夹住；D=1.10 原始突出 0.20 → 被 cap 0.193 夹住。两个球径都正好打到上限。 |
| wood_plank 木板 | 1.400×0.350×0.025 | 4.00 | 0.616 | 真实模型里最细长的板（E=4.0）；替代 1.5 m 测试杆的现有素材 |
| banana 香蕉 | 0.180×0.068×0.032 | 2.65 | 0.195 | E=2.66 且 0.18 ≥ 0.5×0.30=0.15 → 会被判成长物件，但半长 0.09 < r 0.15 → 突出 0，抬升必须是 0（负对照：不能把「已判长物件」当成「必有凹凸」） |

**「最细长的现有模型」的判定**：`RadialProxyShellMath.TryClassifyLongObject` 要两条同时成立——最长边 ≥ `0.5 × 核心直径`、最长边 ≥ `2.5 × 次长边`。现有 39 件里过线的是 `spoon`（E = 5.15，但只有 0.18 m 长）、`wood_plank`（E = 4.00，1.40 m）、`glass_bottle`（E = 4.00，0.28 m）、`banana`（E = 2.65）、`thermos`（E = 2.76）。

**为什么还要提议一根新杆**：在我们跑的四个球径上，现有 5 件里**只有 `wood_plank` 能突出到核心之外**（0.60 球：半长 0.70 − r 0.30 = 0.40）。其余四件太短：`spoon` 半长 0.09 < r 0.15、`glass_bottle` 半长 0.14 < 0.15、`thermos` 半长 0.15 ≤ r 0.30（0.60 球）、`banana` 半长 0.09 < 0.15 —— **它们全在核心里，抬升恒为 0**，只能当负对照。而 `wood_plank` 是 0.35 m 宽的板：它一次同时提供「沿长轴的极值」与「宽边的 4 个角点」，把「杆」和「板」两种情形混在一起。所以要测**纯杆**（一个方向主导、截面 6 cm）就需要一根**测试专用**的杆。

#### 4.6.1 为什么杆是 1.50 m × 0.06 m × 0.06 m（算术）

| 约束 | 数值 | 检查 |
|---|---|---|
| 可吸（D=0.60） | `V = 1.5×0.06×0.06 = 0.0054 m³` → `D_abs = 0.469` | 0.469 ≤ 0.60 ✓（D=0.30 时不可吸，正好也当挡路样例） |
| 可吸（D=1.10 更大球） | 同上 | ✓ |
| 长物件判定 | `E = 25`，`1.5 ≥ 0.5×1.10 = 0.55` | ✓ |
| 打到突出上限（D=0.60） | 半长 0.75 − r 0.30 = 0.45 ≫ cap 0.105 | **被夹**（这是目的） |
| 打到突出上限（D=1.10） | 半长 0.75 − r 0.55 = 0.20 ≫ cap 0.1925 | **被夹**（同一根杆在两个球径都触上限） |
| 静置不滚 | 方截面 0.06 × 0.06，接触面平 | 不会因为「圆杆」在视觉上看起来在滚（物理上它只是一个 `propBounds`） |

- **材质/视觉**：一根拉长的立方体即可（测试专用，不占美术预算）。它**没有** `manifest.json`，在 JSON 里 `source = "proposed_test_only"`，`manifest = null`。若以后要入美术库，按 `Docs/Art/OriginalModels/props/` 的流程补 manifest。
- **判据为什么是「每步」而不是「每次」**：小棍的上限是**每固定步** 0.04 m（`maxProxyLiftPerStep`）。按帧采样会漏掉一帧内的多步，必须用 `[ProxyTrace]` 的逐行日志（每个固定步一行，上限 400 行 = 8 s @ 50 Hz，够跑一次吸附 + 4 m 行程）。

### 4.7 组合路线 C20

`15° 坡（升 0.60）→ 台面 A (0.60) → 0.10 m 台阶 → 台面 B (0.70) → 从 B 的东边缘掉 0.70`

| 球径 | 预期 | 理由 |
|---|---|---|
| 0.30 | **在台阶处被挡**（负对照） | `0.10 > 0.075` |
| 0.60 / 1.10 / 2.20 | 全程通过 | `0.10 ≤ 0.15 / 0.275 / 0.55` |

- 只用 **1 个斜面 + 2 个盒**，是容量最省的一格，也是**最值得先搭**的一组：它一次覆盖「坡 → 台阶 → 台面 → 边缘」四种支撑转换。
- 判据里 `combo_no_catapult`（单帧竖向 ≤ 0.30 m = 3 × 0.1）与 `combo_no_penetration`（`|support_gap − r| ≤ 5 mm`）是**跨阶段**的：前者抓「弹飞」，后者抓「陷进去」。
- `combo_cost` 用 `active_surface_max ≤ 3`：这条**现在拿不到**（没有 getter），标 `readout = motor-counter`、`gate = null`，等 §6.4 的计数器。

### 4.8 中央起点与球径台 C00

- 起点 `(0, 0.15, 0)`、`heading 0`。0.15 是起始球半径（`startCenterWorld` 的默认值就是这个数）。
- 判据只有四条，但每条都在**验证测量链本身**：`rest_gap == r`、球径误差 ≤ 2 mm、静置 1 s 直径漂移 ≤ 1 mm、原地转不产生竖向位移。
- **为什么把它当一格**：如果 `rest_gap` 在起点就不对，后面 20 格的读数全部不可信。它是**仪器的零点校准**，不是「测试项」。

---

## 5. 判据字典（metric → 怎么读 → 怎么判）

### 5.1 读数来源的三条通道

| 通道 | 来源 | 能拿到什么 | 局限 |
|---|---|---|---|
| **A. 每帧 getter** | `KatamariBallMotor` 现有公开方法 + `PickupFieldHost` 计数 | 球心、直径、`support_gap`、`lift`、`cap`、速度、朝向、`stuckSeconds`、代理点数/候选数、吸收/拒绝计数、挡路计数 | **每帧**一次，不是每步；一帧最多 5 个固定步，所以「每步」量只能算**下界/上界** |
| **B. 每固定步日志** | `debugTraceProxySupport = true` 时的 `[ProxyTrace]` 行：`t support core lift cap points candidates centerY` | 真正的**每步**支撑高度、抬升、cap、代理点数 | 只在**地板解算成功**时打（`groundAccepted`），上限 400 行；打开就有日志开销，**只在需要的格里开** |
| **C. 待加计数器** | 尚无（`GetStepMaskRebuildCount()` 已有，但它的语义是「台阶分类翻转次数」，不是每步成本） | `activeSurfaceCount`、`stepMicros`、`stepCount` | 现在**测不到**；列为 §6.4 的接口请求，不假装能测（`readout=counter` 报 `BLOCKED_UNTIL_P4`） |

### 5.2 度量表

| metric | 定义 | 读法 | 通道 | 典型 limit |
|---|---|---|---|---|
| `support_gap_m` | `(center.y − 碰撞半径) − GetSupportSurfaceHeightWorldM()` | 球底到**当前支撑面**的法向距离：站得住 = 0（挡在台阶前时它落在「地面」上，同样是 0，所以它判「陷/浮」而不是「上没上去」） | A | `abs ≤ 0.005` |
| `deck_gap_m` | 整段里 `min abs((center.y − 碰撞半径) − nominal_m)` | **上没上去**：真站上过台面 = 0；从没上去 = `nominal`。取整段最小值，所以「爬上去又从东边掉下来」仍然算上去过 | A | `abs ≤ 0.005` |
| `lift_over_nominal_m` | 整段里 `max((center.y − 碰撞半径) − nominal_m)` | 抬升只能来自本格那个 `nominal` 高的板/台阶；爬到别的东西（隐形台阶、错盒）就超 | A | `≤ 0.010` |
| `blocked_gap_m` | `approach_m − progress_m − 碰撞半径` | 被挡时球心到台阶立面的距离。`approach_m` 由导入器**读 arena 的 `staticBoxes`** 算出（起点沿 heading 第一个进入的盒），所以 0 = 停在立面上、大数 = 提前停住（隐形墙） | A | `abs ≤ 0.010` |
| `deck_y_std_m` | 静置窗口内 `center.y` 的标准差 | 抖动 | A | `≤ 0.001`（S3 口径）；台阶格放宽到 0.002 |
| `frame_dy_m` | 相邻两帧 `abs(Δcenter.y)` | 跳变/弹飞 | A | `≤ 0.02`（接缝）/ `≤ 0.30`（组合） |
| `frame_drop_m` | 相邻两帧的**下落**量 | 反 teleport 上界（重力积分下 0.6 m 落差单帧 ≈ 0.35 m） | A | `≤ 0.5` |
| `fall_frames` | 从离开台面到落地的帧数 | 排除 teleport 式瞬移 | A | `≥ 3` |
| `landing_gap_m` | 落地后 `abs(center.y − r)` | 落地是否贴地 | A | `≤ 0.005` |
| `step_dy_m` | 相邻两条 `[ProxyTrace]` 的 `abs(ΔcenterY)` | **每步**抬升 | B | 长杆 `≤ 0.045` |
| `step_dy_over_step_limit` | `step_dy / stepLimit` | 跨级那一步的抬升是否超限 | B | `≤ 1.05` |
| `blocked_gap_m` | 沿运动轴：`球心到障碍面距离 − r` | 挡没挡住、穿没穿 | A | 挡 `abs ≤ 0.01…0.03`；不穿 `≥ -0.01` |
| `penetration_depth_m` | `max(0, r − support_gap)` | 陷进支撑面的深度 | A | `≤ 0.005` |
| `progress_m` | 沿格轴线的累计位移 | 有没有真的走过去 | A | C14 `≥ 8` |
| `passed_plane_z_m` | 球心越过某平面的最远 z | 夹缝过没过 | A | `≤ -28.6` |
| `min_center_z_m` | 整段的最小 `center.z` | 有没有穿过后门 | A | `≥ -31.3` |
| `speed_mps` / `speed_ratio` | `abs(GetPlanarVelocity())` 及其前后比 | 减速/扰动 | A | `≥ 0.98`（薄片）、`≥ 0.95`（贴墙滑）、`≥ 0.99`（长杆） |
| `normal_speed_mps` | 速度在墙面法线上的分量 | 法向去速是否干净 | A | `≤ 0.05` |
| `impact_speed_jump_mps` | 吸附事件前后一帧的速度差 | 「弹飞」 | A | `≤ 0.02` |
| `xz_std_m` | 顶住时位置的横向标准差 | 内角抖动 | A | `≤ 0.002` |
| `escape_displacement_m` | 倒车后相对卡点的位移 | 能出来 | A | `≥ 0.40` / `≥ 0.50` |
| `stuck_max_s` | 窗口内 `GetStuckSeconds()` 的最大值 | 卡住时长 | A | `≤ 0.50`（薄墙）；`≥ 0.20`（夹缝：证明真的卡过） |
| `protrusion_ratio` | `GetGroundProxyLiftWorldM() / GetProxyProtrusionCapWorldM()` | 突出是否被夹住 | A | `≤ 1.001`；负对照 `≤ 0.02` |
| `proxy_points` / `proxy_candidates` | `GetProxyPointCount()` / `GetProxyCandidateCount()` | 长物件进没进壳 | A | `≥ 8` |
| `roll_distance_ratio` | `abs(滚动角 × r − 位移) / 位移` | 无滑滚 | A | `≤ 0.10` |
| `diameter_error_quanta` | `abs(D − preset) / quantum`，`quantum` = 一个体积单位折算的直径变化（设定器实测斜率 `d(D³)/dV` 除以 `3D²`） | 球径设定是否落在网格最近点。**一个体积单位 = 0.0041 m³**，在 D=0.60 时约 7 mm、2.20 时约 0.5 mm，所以「±2 mm」在 1.10 以下做不到（§8.3） | A | `≤ 1.0` |
| `max_center_y_m` | 整段窗口内 `center.y` 的最大值 | 「最高只到 r + 一点」= 没爬上去（C06） | A | `≤ 0.60` |
| `ramp_entry_progress_m` | 球心进入坡足迹后沿坡向上的位移 | 坡底没有隐形墙（§5.5 陷阱） | A | `≥ 0.30` |
| `slip_back_m` | 停坡 5 s 内沿坡向下的位移 | 「无切向重力」是否成立（§5.4 U3） | A | `abs ≤ 0.020` |
| `recover_speed_mps` | 倒车 1.5 s 内的最大速度 | 离开墙后能动 | A | `≥ 0.20` |
| `elapsed_s` | 一次 run 的墙钟时长 | 没有隐性卡死 | A | `≤ 20` |
| `absorbed_delta` / `attached_delta` | 相对基线的增量 | 吃没吃到 | A | `≥ 1` |
| `blocking_max` | 窗口内 `GetBlockingPropCount()` 的最大值 | 挡路集规模 | A | `≤ 6` |
| `saturated_delta` | `WasContactBufferSaturated()` 的**基线差** | 接触缓冲是否满过 | A | 多数格 `== 0`；内角/夹缝只记录 |
| `overflow_delta` | `GetOverflowCount()` 的**基线差** | 有没有超容量 | A | `== 0`（除 C16 的容量测试） |
| `support_kind` | 支撑面种类（`ground/box/ramp/prop`） | 站的是什么 | **P1/P2/P3 getter** | `== prop` / `== ground` |
| `ramp_count` / `ramp_refusal_reason_len` | `GetRampCount()` / 拒绝原因长度 | 斜面护栏 | **P3 getter** | `== 0` / `≥ 1` |
| `sets_disjoint` | 挡路集与支撑集无交集 | 原子翻转 | **P2 getter** | `== 1` |
| `active_surface_max` | 每步激活支撑面数 | 成本 | **待加计数器** | `≤ 3` |

### 5.3 三态判据与四态结论

每条判据有两个字段：`expect`（按球径给 `pass/fail/n/a`）与 `gate`（哪个阶段之后才有意义）。运行器据此产出**四态**结论：

| 结论 | 条件 | 含义 |
|---|---|---|
| `EXPECTED` | 实测 == `expect` | 行为符合设计（**包括预期失败**） |
| `UNEXPECTED_PASS` | `expect=fail` 但实测通过 | 要么实现提前好了，要么判据写松了 —— **必须人看** |
| `UNEXPECTED_FAIL` | `expect=pass` 但实测失败 | 回归/缺陷 |
| `BLOCKED_UNTIL_<gate>` | 该阶段不存在 / 该读数这个版本拿不到，跳过 | 不是失败，是「还测不了」 |

`BLOCKED_UNTIL` 的五种来源，驱动各打一种 token（都计入 `skipped`，不计入 `unexpected`）：

| token | 什么时候 | 谁能解开 |
|---|---|---|
| `BLOCKED_UNTIL_P2` / `_P3` | 判据声明 `gate=P2/P3`，而这两个阶段的接口（道具支撑 / `AddRamp`）还没有 | 对应阶段落地后改驱动的 gate 映射 |
| `BLOCKED_UNTIL_manual` | `readout=trace`：读数只存在于 `[ProxyTrace]` 的逐行日志里，驱动读不到自己的 Console | 人（grep console） |
| `BLOCKED_UNTIL_P4` | `readout=counter`：每步成本计数器还不存在 | §6.4 的接口请求 |
| `BLOCKED_UNTIL_reset` / `_size` / `_setter` / `_setup` | 这一 run 根本没跑起来（复位失败 / 球径没收敛 / 没挂设定器 / 场景没接好） | 操作员；这是**场景问题**不是物理结论 |

> **为什么要有 `UNEXPECTED_PASS`**：P1 之前 C14 应 FAIL（隐形墙）。如果它「通过」了，最可能的解释不是「P1 提前实现」，而是**薄板根本没注册上**（球从空气里滚过去了）。把这种情况单独命名，就不会被 `pass=6/6` 的绿灯掩盖。

### 5.4 latch 语义：所有「是否曾经」信号都要读基线

`WasContactBufferSaturated()`、`GetOverflowCount()`、`GetFirstOverflowReason()` 都是**永久 latch**（只有 `Start` 清）。所以：

```
格开始时：baseSaturated = motor.WasContactBufferSaturated();  baseOverflow = motor.GetOverflowCount();
格结束时：delta = (now != base) —— 不要读绝对值
```

`stuckSeconds` 相反：它是**瞬时**量（每步 `+dt` 或清零），所以判据要取**窗口内最大值**，不是结束时的值（结束往往已经清零）。

### 5.5 等号纪律（三处，必须统一）

| 位置 | 比较 | 影响 |
|---|---|---|
| `rise ≤ stepLimit` | 0.075 @ D=0.30；0.55 @ 2.20 | 台阶 C09 / bench 边界 |
| `slope ≤ maxSupportSlopeDegrees` | 45.0° | C05 应可上；45.1° 应拒绝 |
| `absorbable ⟺ V ≤ limit` | 表格里的 `D_abs` 都是等号解 | 4 号 plank（0.616）在 0.60 球前是挡 |

本设计**全部按含等号**写（`≤`）。TERRAIN_SUPPORT §4.1 与 `PickupAbsorbMath` 的现状也都是 `≤`（吸收门是「严格大于才拒绝」）。**若实现改成严格小于，这三处会同时翻**，请一次改完三处并更新 JSON 的 `expect`，不要只改一处。

---

## 6. 观测与记录

### 6.1 一次运行（run）的标准协议

**实现方式**（`PhysicsTestFieldDriver`）：每个阶段占一帧段，在 `Update` 里推进，不阻塞。

```
1. 切格：前一个格 arena.ReleaseArena()（SetActive(false)）→ 本格 ApplyRegistration()
        （= SetActive(true) + LevelArenaHost.Reapply()：ClearObstacles → 地面 + 4 墙 + 本格盒）
2. 白名单：motor.SetAllowedKinds(kindWhitelist)（arena 自己那份只有本格道具的 kind，见 §9.5）
3. 记基线：baseOverflow / baseSaturated / baseAbsorbed / baseAttached（latch 量只能读差）
4. 复位球：motor.TryResetForNewRound("ptf-<cell>-<run>", (x, 碰撞半径+0.02, z), heading, out reason)
        —— 它把球重置为 baseline 直径（0.30）并落到格起点；reason 必须为 0，否则 BLOCKED
5. 复位道具：pickupHost.ResetField()（恢复父/位姿/激活，清候选/挡路/径向代理）
6. 设球径：ballSize.SetDiameterM(D)，等 IsConverged()（≤ 6 次吸收；网格下限见 §8.3）
7. 静置 settleSeconds = 0.5 s
8. 跑脚本：按 cell.drive 的 (t, forward, turn, lateral) 调 motor.SetMoveAxes(...)，同时每帧采样
9. 到 driveEnd + stopSeconds 停；停后再采一帧（读数描述「停住的球」而不是停之前那一帧）
10. 算判据 → 每条一行日志 → 一格一行 summary；traj 每 0.5 s 一行
11. 换下一个球径；四个球径跑完换下一格
```

**顺序不能换的三处**：`TryResetForNewRound` **必须晚于**切格（它按新 arena 的地面落地）；**必须早于** `SetDiameter`（它把球按 baseline 体积重新定价，先设径就白做）；`ResetField` **必须晚于** `TryResetForNewRound`（前者要清掉后者可能留下的候选/代理）。

**为什么球径是「喂上去」而不是直接设**：直径是账本体积的纯函数，没有 setter（§8）。`TryResetForNewRound` 负责「回 0.30」，`SubmitCandidate` 负责「长大」。

**球心高度**：`start_pose.center.y` 只是作者标注（0.15 = 起始半径）。运行时驱动写 `y = r(D) + 0.02` 再让 StepBall 的落地解把它压到 `y = 支撑面 + r`，这样任何球径都不会一开始就嵌进地面。

### 6.2 日志格式（固定、可被脚本 grep）

```
[ptf] run=3 cell=C10 D=0.300 start=(7.500,0.170,1.000) hdg=90.0 arena=planes=4/boxes=1/supports=2 gates=on_step->P1
[ptf] check=on_step metric=deck_gap_m value=-0.1199 op=abs<=0.0050 expect=fail raw=FAIL verdict=EXPECTED
[ptf] check=rest_gap metric=support_gap_m value=+0.0000 op=abs<=0.0050 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=blocked_gap metric=blocked_gap_m value=-0.0007 op=abs<=0.0100 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=size_accuracy metric=diameter_error_quanta value=-0.0031 op=<=1.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] summary cell=C10 D=0.300 checks=6 pass=6 unexpected=0 skipped=0 verdict=EXPECTED elapsed=5.50s
[ptf] traj t=2.50 p=(11.85,0.17,1.00) sup=0.000 lift=0.000 cap=0.0525 stuck=1.20
[ptf] field cells=4 runs=16 verdict=EXPECTED unexpected=0 skipped=0 elapsed=94s
```

规则：
- **一条判据一行**，字段顺序固定；`value` 恒为 `%+.4f`（带符号、四位小数、不开科学计数），由驱动自己的整数格式化打印（UdonSharp 的 `ToString("F4")` 不在本项目已验证的 extern 集合里，见 `PhysicsTestFieldDriver.Fixed`）。
- 一格一行 `summary`，一次 pass 一行 `field`；`run=` 行在切格成功后立刻打，带 `arena=planes/boxes/supports`（这是「arena 真的注册上了」的唯一证据）与 `gates=`（哪些判据被 gate 挡住了）。
- gate/readout 挡住的判据仍然各占一行：`value=n/a` + `raw=SKIPPED` + `verdict=BLOCKED_UNTIL_*`，所以「少了一行」永远不代表「通过了」，也不会出现一个假的 `0.0000`。
- **`pass=` 是「符合预期的判据数」，不是「物理通过数」**：C10 在 0.30 上的 `on_step` 物理上 `raw=FAIL`，但它 `expect=fail`，所以计入 `pass=`；物理事实只在每条判据行的 `raw=` 里。`unexpected=` 才是红灯计数。
- **`traj` 行每 0.5 s 一条**（单次运行 ≤ 40 行），字段是重建轨迹 CSV 的最小集合：`t, p, sup, lift, cap, stuck`。这样证据不需要额外文件格式。
- 任何 `UNEXPECTED_*` 额外一条 `[ptf] ALERT ...`，**并在 Summary 里计数**（不要只在 Console 里红一下）。
- 所有数字**打印事实**，不打印解释（`verdict=` 是结论，`value=` 是事实；不要在 value 里写 "ok"）。

### 6.3 每格要读的数值（最小集）

| 类别 | 读数 | 用途 |
|---|---|---|
| 位置 | `center`（逐帧）、`traj`（每 0.5 s）、`heading` | 轨迹、位移、绕行 |
| 支撑 | `support_gap`、`lift`、`cap`、`support_kind`（P1+）、`support_normal`（P1+） | 贴地、凹凸、法线是否朝上、滚动法线是否正确 |
| 接触 | `saturated_delta`、`stuck_max_s`、`proxy_points`、`proxy_candidates` | 卡住、饱和、长物件进壳 |
| 吸收/挡路 | `absorbed_delta`、`attached_delta`、`blocking_max`、`blocking_candidates`、`absorbed_event` 的 id | 门与分类是否原子翻转 |
| 成本 | `active_surface_max`、`mask_rebuild`、`step_micros`（**待加**） | P4 的帧时间与分配 |
| 几何 | 该格的 `expected_ramp_count` / `GetRampCount()`（P3）、`GetLastRampRefusalReason()`（P3） | 斜面注册是否被正确拒绝 |

### 6.4 现在拿不到、需要加的读数（给实现者的接口请求）

**已经在树里**（2026-11-15，P1 合入后）：`GetSupportSurfaceHeightWorldM()`、`GetSupportNormalWorld()`、`GetSupportSurfaceId()`、`GetSupportSurfaceCount()`、`GetSupportKind()`（0 地面 / 1 盒 / -1 无面）、`GetStepHeightLimitWorldM()`、`IsFalling()`、`GetStepMaskRebuildCount()`。驱动的 `support_gap_m` / `deck_gap_m` / `blocked_gap_m` 全部建立在它们之上，**没有**为测试场新增任何 getter。

仍然缺的（只有这些会报 `BLOCKED_UNTIL_*`）：

| 请求 | 签名草案 | 为什么需要 | 优先级 |
|---|---|---|---|
| 每步成本 | `public int GetLastActiveSurfaceCount();` `public int GetMaskRebuildCount();`（已有，但语义是「分类翻转次数」） `public float GetLastStepMicros();` `public int GetFixedStepCount();` | `combo_cost` 与 §3.3 的切格 GC 必须实测；`Time.realtimeSinceStartup` 夹两端只能测整帧 | P4 |
| 斜面拒绝原因 | `public int GetRampCount();` `public string GetLastRampRefusalReason();` | C06 | P3 |

> **不要**为了测试场去加「测试专用」的 getter 到运行时代码里；上表两项都是**产品也需要**的诊断（HUD 读同样的值）。这是本设计的取舍：宁可等 P3/P4，也不在 Lab 里复制一份状态。

### 6.5 固定机位清单（截图可复现）

| id | 类型 | 位置 / 目标 | 拍什么 | 用在哪几格 |
|---|---|---|---|---|
| `cam_top` | 固定 | `(-8.5, 78, 3)` → `(-8.5, 0, 3)`，FOV 45 | 全场总览（先拍这张证明摆位） | 所有格（每 run 必拍） |
| `cam_plaza` | 固定 | `(-9, 3.2, -7)` → `(0, 0.4, 0)` | 起点 + 球径台近景 | C00 |
| `cam_ramps` | 固定 | `(26, 9, 24)` → `(-1, 2.2, 22)` | 斜板立面：坡长、坡顶接缝、落差一帧读完 | C01–C06 |
| `cam_steps` | 固定 | `(12, 6.5, -22)` → `(13, 0.8, -2)` | 台阶立面：挡与上同画面 | C07–C12 |
| `cam_thin` | 固定 | `(2, 9, -19)` → `(4, 1, -30)` | 薄墙 / 内角 / 夹缝共用 | C14–C17 |
| `cam_deck` | 固定 | `(-8, 4, -19)` → `(-16, 0.7, -12)` | 台面稳定 + 边缘落差 | C13 |
| `cam_west` | 固定 | `(-4, 8, 8)` → `(-22, 0.8, -2)` | 挡路行 + 长杆 | C18、C19、C20 |
| `cam_chase` | 跟球 | 相对球 `(0, 3.2, -4.6)`，看 `(0, 0.35, 0)` | 锁定球的视角：滚动起伏、贴墙、跌落 | C19（起伏）、C13（跌落） |

- 机位全部是**固定 Transform**（除 chase），所以两次 run 的截图可以直接叠图比对（这是「手感变了」最便宜的判据）。
- **轨迹可视化**：不做运行期 Gizmo。原因见 `Labs/README.md`——编辑器 C# 读不到 Udon 的运行期状态，Scene view 的 `OnDrawGizmos` 拿不到球心。
  替代方案两条：① `traj` 日志行（默认，零成本）；② 可选的 `LabTrajectoryTrail`（`LineRenderer`，120 点，Lab 专用、默认关）只在要拍「起伏曲线」时打开，并且**在 Evidence 里注明打开了它**（因为它有每帧 `SetPosition` 的成本）。

### 6.6 Evidence 存放

```
Docs/Evidence/PhysicsTestField/
  README.md                         谁跑的、Unity/SDK/客户端、结论一句话
  environment.txt                   Unity 2022.3.22f1 / VRChat SDK 3.10.5 / PC 或 Quest / 是否 ClientSim
  console.log                       原文：[ptf] + [ProxyTrace] + [BallMotor] + [PickupFieldHost]
  summary.json                      {cells:[{id,D,checks:[{id,value,verdict}],verdict}], totals}
  cells/<cell>_D<0.300>.log         每 run 一个片段（从 console.log 切出来，便于 diff）
  shots/<cell>_<camera>.png         每 run 至少 2 张（cam_top + 该格主相机）
  traj/<cell>_D<0.300>.csv          可选：由 traj 行转出（t,x,y,z,sup,lift,cap,stuck）
```

命名：`<cell>_D<3 位小数>_<camera>.png`，例如 `C10_D0.300_cam_steps.png`。日期目录：`Docs/Evidence/PhysicsTestField/2026-11-15_pass1/`（一次完整 pass 一个目录，不覆盖上一次）。

---

## 7. 测试场驱动器接口草案（`PhysicsTestFieldDriver`）

### 7.1 定位

`StoryLevelLabBootstrap` 是仓库里剩下的**唯一**脚本化驾驶 lab（`BallPlayground` 已退役），已经是「脚本化驾驶 + 每秒一行日志」的成熟形态：它用 `motor.SetMoveAxes(forward, turn, lateral)` 跑三段脚本，用 `Report()` 每秒打一行。**驱动器沿用同一条缝**，但把「一段硬编码时间线」升级成「按格 + 按球径 + 按判据表」的数据驱动。**不引入任何新的输入层、不引入第二份物理**。

### 7.2 结构（已实现，未编译）

```csharp
namespace VRCKatamari.Labs.PhysicsTestField
{
    /// 一场物理回归：切格 → 设球径 → 跑脚本 → 判 PASS/FAIL → 打日志。
    /// 只活在 Labs 下；不进正式场景，不被 Runtime 引用。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestFieldDriver : UdonSharpBehaviour
    {
        // ---- 链接 ----
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private PickupFieldHost pickupHost;
        [SerializeField] private LabBallSizeSetter ballSize;
        [SerializeField] private PhysicsTestCell[] cells;        // 按 cells[] 顺序，index 稳定
        [SerializeField] private float[] presetDiametersM;      // 0.30/0.60/1.10/2.20
        [SerializeField] private string[] kindWhitelist;        // 每次 Reapply 之后重发
        [SerializeField] private Transform chaseCamera;         // 可选；null = 不动相机
        [SerializeField] private Vector3 chaseOffset;

        // ---- 运行参数（都是 Lab 旋钮）----
        [SerializeField] private bool autoBeginAllOnStart = false;
        [SerializeField] private float settleSeconds = 0.5f;
        [SerializeField] private float sizeTimeoutSeconds = 3f;
        [SerializeField] private bool logTrajectory = true;
        [SerializeField] private float trajectoryIntervalSeconds = 0.5f;

        // ---- 操作者 API（也被 HUD 按钮 / 外部探针调用）----
        public void BeginAll();                                  // 全部格 × 4 径
        public void BeginCell(string cellId);                    // "C10"，四径
        public void BeginCellAtDiameter(string cellId, float d);
        public void BeginFromIndex(int cellIndex, int diameterIndex);
        public void Abort();
        public bool IsRunning();
        public string GetCurrentCellId();
        public float GetCurrentDiameterM();
        public int GetRunIndex();
        public int GetRunCount();
        public int GetExpectedCount();                           // 上一 run 命中预期的判据数
        public int GetUnexpectedCount();
        public int GetSkippedCount();
        public string GetLastSummaryLine();
        public int GetCellCount();
        public string GetCellIdAt(int index);
    }

    /// 一格：本格 arena + 起点 + 激活矩形 + 判据行 + 驱动时间线。
    /// 所有字段由 Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs 烘焙，不手填。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestCell : UdonSharpBehaviour
    {
        [SerializeField] private string cellId;
        [SerializeField] private LevelArenaHost arena;           // 本格唯一的注册者
        [SerializeField] private Vector3 startCenter;            // y 被驱动忽略，见 §6.1
        [SerializeField] private float startHeadingDegrees;
        [SerializeField] private Vector2 footprintCenter;        // 激活矩形（含跑道）
        [SerializeField] private Vector2 footprintSize;
        [SerializeField] private float nominalM;                 // 本格的自变量：板厚 / 台阶高
        [SerializeField] private float approachM;                // 起点沿 heading 到第一个 staticBox 的距离
        [SerializeField] private float[] driveTimes / driveForward / driveTurn / driveLateral;
        [SerializeField] private float stopSeconds;
        [SerializeField] private string[] checkIds;
        [SerializeField] private string[] checkMetrics;
        [SerializeField] private int[]    checkOps;              // 0 abs<=, 1 <=, 2 >=, 3 ==, 4 abs>=
        [SerializeField] private float[]  checkLimits;
        [SerializeField] private int[]    checkReadouts;         // 0 frame, 1 trace(人读), 2 counter(P4)
        [SerializeField] private int[]    checkGateStages;       // 0 none,1 P1,2 P2,3 P3
        [SerializeField] private int[]    checkExpectMasks;      // 每球径 2 bit：0 n/a,1 pass,2 fail

        public string GetCellId();
        public LevelArenaHost GetArena();
        public Vector3 GetStartCenter();
        public float GetStartHeadingDegrees();
        public float GetNominalM();
        public float GetApproachM();
        public bool ContainsPoint(float x, float z, float marginM);
        public float DistanceToFootprint(float x, float z);
        public void ApplyRegistration();                         // SetActive(true) + arena.Reapply()
        public void ReleaseArena();                              // SetActive(false)
        public bool IsArenaActive();
        public int GetDriveCount();                              // + 每行 getter
        public float GetDriveEndSeconds();
        public int GetCheckCount();                              // + 每行 getter
        public int GetCheckExpect(int index, int diameterIndex);
    }
}
```

**和草案不一样的三处，都是实现逼出来的**：

1. **`checkReadouts[]` 是新增的数组**。草案只有 `readout` 这个字符串字段在 JSON 里；运行时每次判据都做一次字符串比较是没必要的，所以导入器把它烘成整数，驱动只按整数分支。
2. **`cells[i].ReleaseArena()` 在 `Start()` 里无条件调用**。`LevelArenaHost` 在自己 `Update` 里注册，而电机的平面表是全局的——四个 arena 同时活着会在第一帧登记 4 个地面 + 16 面墙（8 个平面槽）并且 latch overflow。所以「一格一注册」不是靠操作员纪律，而是靠 `PhysicsTestCell` 把自己的 arena 关掉、驱动只开目标格。
3. **没有 `fixedCameras[]` / `hudText` / `SelectNextCell()`**。本批不做相机切换与 HUD；证据靠日志和操作员截图（§9.5 的接线清单）。加它们是纯增量，不影响判据。

### 7.3 为什么判据是**并行数组**而不是「一个检查对象」

UdonSharp 没有用户结构体、没有 `List`/`Dictionary`、不能用反射。所以「一条判据」只能是并行数组的一行（`checkIds[i] / checkMetrics[i] / ...`）。**JSON 是作者数据**，编辑器侧导入器（`Editor/` 下，可读磁盘 JSON）把它烘焙成这些数组并写进场景；运行时**只读数组**，不读磁盘。这与 `02_TECHNICAL_CONTRACTS.md` §4「不要让 Udon 运行时依赖编辑器 API 或随意读取磁盘 JSON」一致。

### 7.4 状态机（已实现）

```
Idle → Switch(cell) → Size(D) → Settle → Drive → Report → (next D | next cell | Done)
```

| 状态 | 做什么 | 失败时 |
|---|---|---|
| `Switch` | 关掉上一个 arena、开本格 arena 并 `Reapply()`、重发 kind 白名单、记基线、`TryResetForNewRound`、`ResetField`、打 `run=` 行 | `TryResetForNewRound` 的 reason ≠ 0 → `BLOCKED_UNTIL_reset` 并跳过本 run（换下一径/下一格） |
| `Size` | `ballSize.SetDiameterM(D)`，每帧问 `IsConverged()` | `HasFailed()` 或超 `sizeTimeoutSeconds` → `BLOCKED_UNTIL_size` |
| `Settle` | 等 `settleSeconds` | —— |
| `Drive` | 按 `drive[]` 的阶段推 axes，每帧采样，每 0.5 s 一条 `traj` | —— |
| `Report` | 逐条判据 → 行日志 → `summary` → 决定下一 run | —— |

**采样频率**：`Update()` 每帧一次（不是每步）。所以「每步」量（`step_dy_over_step_limit`、`active_surface_max`）必须来自 `[ProxyTrace]`（通道 B）或 P4 计数器——它们现在都报 `BLOCKED_UNTIL_manual` / `_P4`，**不会被逐帧采样冒充**。逐帧量只有 `frame_dy_m`（本轮数据里没有判据用它）。

**阶段之间的顺序不是自由组合**：见 §6.1 的三个「必须」。

### 7.5 HUD（本批不做）

```
PTF C10 D0.30 [4/84] checks 5/5  exp 0  skip 0   v=EXPECTED
```

上一行是**结论**，下面一行是**当步事实**：`gap=+0.0012 sup=0.100 lift=0.000 stuck=0.00 blk=0/6`。

本批只打 Console（`GetLastSummaryLine()` 已经给出同一行的文本，HUD 接它即可）。4 个 uGUI 按钮的绑定方式见 §8.4。

---

## 8. 球径设定接口草案（`LabBallSizeSetter`）

### 8.1 需求

同一格要在 0.30 / 0.60 / 1.10 / 2.20 四个球径下各跑一遍。现状：直径**只是账本的纯函数**（`SizeModelMath.TryDerive(confirmedVolume)`），没有 setter；`startCenterWorld` 与 `baselineVolumeUnits` 都是序列化字段（改了要重开场景）。

### 8.2 接口（已实现，未编译）

```csharp
namespace VRCKatamari.Labs.PhysicsTestField
{
    /// 只在 Labs 下：把一个运行中的球设到指定球径。
    /// 不改运行时代码：用 SubmitCandidate 喂体积 + GetSemanticDiameterWorldM 闭环收敛。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LabBallSizeSetter : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private float[] presetDiametersM;   // 0.30/0.60/1.10/2.20
        [SerializeField] private string[] presetLabels;      // "0.30"…（按钮按字符串绑，不解析数字）
        [SerializeField] private string feederKind = "lab-feeder";
        [SerializeField] private Vector3 feederBounds = new Vector3(0.02f, 0.02f, 0.02f);
        [SerializeField] private float toleranceM = 0.002f;
        [SerializeField] private int maxAbsorbsPerRequest = 6;
        [SerializeField] private float feedIntervalSeconds = 0.05f;

        public void SelectPreset(int index);                 // HUD 按钮绑这个
        public void SetDiameterM(float targetM);             // 驱动器用；重复同目标 = no-op
        public void SetDiameterFromLabel(string label);      // "1.10"
        public void Cancel();
        public bool IsConverged();
        public bool IsRequestActive();
        public bool HasFailed();
        public string GetLastFailure();                      // "" / cannotShrink / capExceeded / submitRefused
        public float GetTargetDiameterM();
        public int GetAbsorbCount();
        public float GetLastErrorM();                        // 有符号：实测 − 目标
        public float GetQuantumM();                          // 一个体积单位在上一测量点的直径当量
        public float GetCubedSlope();                        // 实测 d(D³)/dV
        public bool IsCalibrated();
    }
}
```

**没有 `ResetToBaseline()`**（草案里有）：回 0.30 需要 `TryResetForNewRound(id, 格起点, heading, out reason)`，而「格起点」属于 `PhysicsTestCell`。让设定器去复位球，等于让它同时拥有「球多大」和「球在哪」两件无关的事。复位由驱动在切格那一帧做（§6.1 第 4 步），设定器只负责长大。

### 8.3 三条实现路线（推荐第一条）

| 路线 | 做法 | 成本 | 风险 | 判定 |
|---|---|---|---|---|
| **A. 闭环喂体积（采用并已实现）** | 每次 `SubmitCandidate(true, "lab-feeder", 0.02³, 球心, units, "ptf-feeder-<n>", gen)`；下一帧读 `GetSemanticDiameterWorldM()`，用**实测斜率**算下一次的整数 `units`，≤ 6 次收敛到「网格最近点」 | **零运行时代码改动** | ① 吸收计数/事件被污染（挡路行与长杆格的 `absorbed_delta` 要先记基线，已按 §5.4 处理）；② 需要 `allowedKinds` 含 `lab-feeder`；③ 不能「变小」——只能 `TryResetForNewRound` 回 0.30 再长大 | **采用** |
| B. 加测试专用接缝 | `motor.SetConfirmedVolumeUnitsForTest(float)`（带 `testModeEnabled` 开关，默认 false） | 一处运行时代码 | 多一个「生产里不该存在」的写入口；U# 没有编译期隔离，只能靠开关纪律 | 备选，需 Lead 批 |
| C. 一个球径一个场景 | 改 `baselineVolumeUnits`，做 4 个场景 | 4 份场景 + 4 份维护 | 「同一格不同球径」变成跨场景比对，切格/机位/几何都可能漂 | 否决 |

**A 的实际闭环**（已实现，和草案的差别是「斜率」的来源）：

```
每帧（一次只允许一个候选在飞）：
  V = motor.GetConfirmedVolumeUnits()        // 只算「基线之外」的体积单位
  D = motor.GetSemanticDiameterWorldM()
  if 上一帧有样本 → slope = (D³ − D³_prev) / (V − V_prev)      // 实测 d(D³)/dV
  quantum = slope / (3D²)                    // 一个单位折算的直径当量
  收敛: |D − target| ≤ max(toleranceM, quantum)
  否则: units = round((target³ − D³) / slope)   // 立方域里比，不需要开方
        若 units 会「越过目标更远」→ 这就是分辨率下限，收敛
        motor.SubmitCandidate(true, feederKind, feederBounds, center, units, "ptf-feeder-<n>", gen)
```

- **为什么不用 `cubicMetersPerVolumeUnit` 直接算**：那是 `[SerializeField] private`，没有 getter；而且**基线体积也是私有的**（`D³ ∝ baselineVolumeUnits + confirmed`，`GetConfirmedVolumeUnits()` 只给后一半）。所以「先探一个单位、从两个样本里解出斜率」既绕开了两个私有字段，又让标定一改就自动跟上。第一次请求花 2 次吸收（一次探针 + 一次主喂），之后斜率跨请求复用。
- **为什么 feed 必须是整数**：`PickupAbsorbMath.TryAbsorbValueToLedgerGain` 拒绝小数（账本增益是 int），喂 0.35 单位会被判 `RejectNoGain`、候选一直留在队列里。整数单位同时也是账本记的那个数。
- **为什么一次只允许一个候选在飞**：候选队列只有 4 格，第 5 次提交会 `SignalOverflow("pending candidate capacity 4 exceeded")` **永久 latch** overflow 计数——而本场好几条判据断言它 Δ=0。所以 feed 前先看 `GetPendingCandidateCount() == 0`，宁可慢也不要污染。
- **分辨率下限（实测推算，不是猜）**：`baselineVolumeUnits = 3.439`、`cubicMetersPerVolumeUnit = 0.0041` → `d(D³)/dV = 0.00783`。所以一个单位的直径当量是 `0.00783 / (3D²)`：**D=0.30 时 58 mm、0.60 时 7.3 mm、1.10 时 2.2 mm、2.20 时 0.54 mm**。**「±2 mm」只有 1.10 与 2.20 档能保证**；0.30 档由复位精确命中（误差 0），0.60 档最坏 3.6 mm。这就是判据用 `diameter_error_quanta`（≤1 个网格单位）而不是 `abs ≤ 0.002 m` 的原因——后者在小球径上是在要求一件物理上做不到的事。
- **副作用清单**（必须写进 Evidence 的 README）：吸收计数 +N、`GetAbsorbedCount()` 变大、账本体积变大、`GetAbsorbVolumeLimitWorldM3()` 变大 → **挡路集与支撑集的分界会跟着变**。这正是我们要测的东西之一（C18 的「长大可吸」），但会污染 C14 的 `not_blocking` 判据——所以那条判据读的是「基线差」，不是绝对值。

### 8.3.1 结论对判据的影响（要写进 Evidence）

- `size_accuracy` 判据 = `diameter_error_quanta ≤ 1.0`。它证明的是「设定器落在网格最近点」，**不是**「±2 mm」。四个球径的实测 `quantum` 要打进 Evidence 的 README（`GetQuantumM()` 每 run 都有）。
- 若将来要真正的 ±2 mm，唯一干净的路是给电机加一个测试接缝（上表路线 B），那需要 Lead 批一处运行时代码；本批不做。

### 8.4 HUD 控件

4 个 uGUI 按钮（0.30 / 0.60 / 1.10 / 2.20）+ 1 个「回 0.30」。绑定方式按 `Labs/README.md` 的硬事实：`Button.onClick` 的 persistent listener 指向 `UdonBehaviour.SendCustomEvent`，参数字符串是方法名，用 `UnityEventTools.AddStringPersistentListener`（lambda 会被拒）。

---

## 9. 场景与数据

### 9.1 场景

| 项 | 值 |
|---|---|
| 场景 | `Labs/PhysicsTestField/PhysicsTestField.unity`（**未创建**） |
| 目录 | `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/PhysicsTestField/` |
| 新建时的必做序列 | `Labs/README.md`「用编辑器脚本搭 UdonSharp 场景」的 7 步（program asset 先就位 → `CompileAllCsPrograms` → backing `UdonBehaviour` → `_udonSharpBackingUdonBehaviour` → `SetBehaviourVersion` → 填引用 → `CopyProxyToUdon` → 存盘重开场景再回写）。**不是** `AddComponent<T>()` 一句话能搞定的 |
| 必备 SDK 组件 | 根对象 `VRC.Core.PipelineManager`；`VRCSceneDescriptor.ReferenceCamera` → 主相机 |
| 材质 | 复用 `Art/Materials/Mat_Playground*`（BallPlayground 退役时搬过去的）或纯色；**不许**为了好看引入需要烘焙的材质（测试场不用阴影判断任何东西） |
| 光照 | 一盏方向光，固定角度；截图不依赖实时阴影的精确定位 |
| 可玩性 | 可玩（Station + `DesktopMoveInput`），但**默认以驱动器为准**；人玩只用于看手感 |

### 9.2 数据：几何在 placement，判据在 JSON（**本轮的核心改动**）

原来 `physics_test_field.json` 自带一套几何格式（`cells[].registration`：盒的 center/size/euler、斜面的 angle/rise/run、`top_y_m`），与 `Docs/Design/Tools/LEVEL_AUTHORING.md` 的 `placement.json` 重复。现在分成两份，各管一件事，**没有第三份**：

| 文件 | 管什么 | 判据/门 |
|---|---|---|
| `Docs/Design/Levels/physics_test_field/<cell>/placement.json` | **场景里有什么**：`structures[]`（ground / boundary / box / ramp）、`level.arena.id`、`level.budget`、`level.expected` | `python3 Tools/levelbuild/levelbuild.py check <该文件>`（容量、地面、通配、规范性） |
| `Docs/Design/BallMotor/physics_test_field.json`（v2） | **跑什么**：每格的 `start_pose` / `footprint` / `nominal_m` / `drive[]` / `checks[]` / 机位引用 / 期望；场级的 `diameters[]`、`motor_constants`、`cameras[]`、`registration_model`、`driver`、`ball_size` | `Docs/tests/levelbuild/test_physics_test_field_cells.py`（跨文件一致性） |

**为什么是「每格一份 placement」，不是「一份 placement 按 zone 分格」**：

1. **容量门就是按文件算的**。`levelbuild check` 的硬规则是 `planes ≤ 8 / boxes ≤ 8 / ramps ≤ 4`（`level.budget`）。一份文件装 21 格 = 24 盒，**天生红**；要让一份文件通过，就必须把预算改成「按 zone 数」，那等于把这条硬约束在第二个地方重新实现一遍——而它正是「一格一注册」存在的理由。
2. **导入器是「一份文档 = 一个 arena host」**。`LevelArenaHost` 一次只能注册一组地面/墙/盒；每格一个 host 是需求（`Reapply()` 单格重注册）。一份文件 = 一份几何 = 一个 host，导入器不需要「按 zone 分组」这一层逻辑（分组一旦要按 `zone` 字段做，又得规定 zone 与 host 的命名规则、props 的归属……每一条都是新格式）。
3. **schema 早就留好了位置**。`LEVEL_AUTHORING` §15.4 的原话就是「本 schema 是『一格一份 placement』（`level.id` + 目录），没有假设一场景一份」；本轮只是把 `physics_test_field` 接上去。

**为此对工具做的最小改动**（`Tools/levelbuild/` + `Editor/LevelBuild/`，都不是新格式）：

| 改动 | 位置 | 为什么必须 |
|---|---|---|
| `level.arena = {id, notes}` | `placement_core.LEVEL_KEYS` + `_check_arena` | 一份文档声明「我是一个 arena 格」。导入器用它决定受管子树与 arena host；`_check_arena` 要求 `id` 等于所在目录名（复制来的文件不会带着旧身份跑） |
| `level.id` 允许一层 `/` | `_LEVEL_ID_RE` | `level.id` 是「Levels 下的目录」，现在多了一层（`physics_test_field/C14`）；`placement_path("physics_test_field/C14")` 能直接解析到文件 |
| 盒子/斜面必须落在 `level.bounds` 内（仅 arena 文档） | `_check_arena` | 一份**整图**的 bounds 只是报表范围（历史行为不变：树的角树可以越过场地边界），但**一格**的 bounds 是它的场域，几何跑出去就是数据错 |
| 导入器：cell 模式（`LevelContent/<arenaId>/` + 该对象上的 host） | `Editor/LevelBuild/PlacementImporter.cs` | 一场景多格：每格一个受管子树、一个 arena host，互不覆盖 |
| 菜单一次导入该场景的全部 cell | 同上（`CollectLevels` + `ImportActiveSceneLevel`） | 一场景 21 份文档，逐个手点 21 次不现实；混入「整关文档」时仍然拒绝（否则两个 arena 抢同一张电机表） |
| 渲染文件名净化 `level.id` 里的 `/` | `Tools/levelbuild/blender/render_views.py` | 否则 `--preset plan` 会往 `.../physics_test_field/C14_plan_workbench.png` 写（父目录不存在） |

**`physics_test_field.json` v2 的键**（schema_version 2）：

| 键 | 内容 |
|---|---|
| `id` / `title_zh` / `status` / `created` / `updated` / `design_doc` / `purpose` / `generated_by` | 身份与出处 |
| `geometry` | 迁移模型、目录、**哪些字段来自 placement**、`check` / `preview` 命令、以及「footprint 为什么不算几何」 |
| `scene` | 场景路径、状态 `NOT_CREATED`、坐标系、朝向约定（**边界平面不在这里了**：它们是每格 placement 的 4 条 `role=boundary`） |
| `motor_constants` | 全部常量 + 容量表（引用代码，不重定义） |
| `diameters[]` | 4 档：`diameter_m / radius_m / step_limit_m / protrusion_cap_m / absorb_volume_limit_m3` |
| `start` | 全场默认起点与起始球径（每格自己的起点在 `cells[].start_pose`） |
| `registration_model` | `mode = one_cell_at_a_time`、切换规则、去抖、**`per_cell_arena`**、成本说明 |
| `cells[]` | **已接线**的格（本批 4 个），见下 |
| `planned_cells[]` | 未迁移的 17 格：只有 `id/slug/name_zh/kind/stage/expectation/note`，**没有任何几何**，`wired=false` |
| `cameras[]` | 8 个机位（数据，不是本轮实现） |
| `driver` / `ball_size` / `criteria_importer` | 三个组件的路径、状态、运行参数、`kind_whitelist`、`blocked_notes` |
| `evidence` | 根目录、文件布局、截图规则 |

单个 `cell` 的键（v2）：

| 键 | 含义 |
|---|---|
| `id` / `slug` / `name_zh` / `kind` / `stage` | 身份与阶段（`now` / `P1` / `P2` / `P3`） |
| `wired` | true = 本批已接进场景；false 的格不进 `cells[]` |
| `placement` | `physics_test_field/C14/placement.json`（相对 `Docs/Design/Levels/`）；**几何的唯一入口** |
| `start_pose` | `{center:[x,y,z], heading_deg}`：**runner 数据**（驱动把球传送到这里）；`y` 是作者标注，运行时被 `碰撞半径+0.02` 覆盖 |
| `footprint` | `{center:[x,z], size:[sx,sz]}`：**runner 的激活矩形**，含跑道（C14 是 6×12 给 6×6 的板）。它**不是**几何的包围盒，placement 里也没有这个矩形；两者的关系由测试守（几何必须落在自己的 footprint 内） |
| `nominal_m` | 本格的自变量：板厚 0.028 / 台阶 0.075、0.050、0.100。每条 deck/step 判据都相对它写，所以一条 limit 对四个球径都成立 |
| `stop_seconds` | 驱动时间线结束后继续测量的秒数（球已松手） |
| `drive[]` | `{t, forward, turn, lateral, note}`：脚本化驾驶的相位表 |
| `checks[]` | `{id, metric, op, limit, unit, readout, gate, window, expect[4], note}`。**`expect` 是按 `diameters[]` 顺序的 4 元数组**（JsonUtility 读不了「以球径为键的对象」，而且数组顺序就是 `checkExpectMasks` 的位序） |
| `cameras[]` / `acceptance_refs[]` | 机位、对应 TERRAIN_SUPPORT 验收编号 |
| `expectation` | `{pre_p1, p1, p2, p3}` 的人读结论（`PASS` / `EXPECTED_FAIL` / `BLOCKED_UNTIL_*`） |

### 9.3 两个编辑器工具（都**未编译**）

| 工具 | 位置 | 做什么 | 不做什么 |
|---|---|---|---|
| `PlacementImporter`（已有，本轮扩了 cell 模式） | `Editor/LevelBuild/PlacementImporter.cs` | 把 `Levels/<field>/<cell>/placement.json` 变成 `LevelContent/<cell>/{Props,Structures}`，并填该格 arena host 的 `groundNormal/groundOffset`、`boundaryWalls`、`staticBoxes`、`allowedKinds` | 不建 `LevelArenaHost` 组件（U# 组件要 program asset + backing UdonBehaviour + `CopyProxyToUdon` 的 7 步，脚本硬建只会得到一个「看着接好、其实不跑」的组件）；不 `SaveScene` |
| `PhysicsTestFieldCriteriaImporter`（新） | `Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs`，菜单 `VRC Katamari/Level/Import Physics Test Field Criteria` | 读 `physics_test_field.json`，把 `start_pose / footprint / nominal_m / drive[] / checks[]` 烘进 `PhysicsTestCell` 的并行数组，把 `cells[] / presetDiametersM / kindWhitelist` 填进驱动；`approach_m` 由它**读 arena 的 `staticBoxes`** 算（起点沿 heading 第一个进入的盒），不是抄 JSON | 不建组件、不建场景、不 `SaveScene`；找不到组件时打印「该给哪个对象加什么」并跳过 |

> 这一层是**工具**，不是运行时。作用是「改 JSON/placement 一个数字 → 重跑两个导入 → 场景与文档与数据三者一致」，而 Udon 运行时永远只读序列化数组（`02_TECHNICAL_CONTRACTS.md` §4）。

### 9.4 与地图工具的关系

本场的几何单元（静态盒 + 斜面 + 平面）与 `Docs/Design/Maps/*/layout.json` 的 `structures[]`/`collision` 是**同一组概念**（`Tools/mapbuild/validate_map.py` 已经在数 8 盒 / 8 平面）。**没有**把测试场塞进 `layout.json`：测试场不是地图、不参与 `map_snapshot` 哈希、不跑 `validate_map.py`。但**几何格式现在是同一个** `katamari.placement/0`，所以 `levelbuild.py check/format/seat` 与 Blender 出图对测试场直接可用。

### 9.5 给 Unity 操作员的接线清单（本轮**不建场景**）

> 下面每一步都是「操作员在 Windows 的 Unity 里做」。代理没有碰 Unity，也没有建 `.meta`；这一步的产物是场景与 U# 组件实例。

**0. 前置**

| 项 | 值 |
|---|---|
| 场景路径（建议） | `Assets/VRC_Katamari/Labs/PhysicsTestField/PhysicsTestField.unity` |
| 目录 | `VRC_Katamari/Labs/PhysicsTestField/`（已有 4 个 `.cs`，**没有** `.meta`，Unity 首次聚焦会用当前程序集补上） |
| 必备 | 根对象 `VRC.Core.PipelineManager`；`VRCSceneDescriptor.ReferenceCamera` → 主相机 |
| 球/输入/道具宿主 | 从 `StoryLevel.unity` 拷 `MotorHost`（`KatamariBallMotor`）、`PlayerBall/*` 三层、可选 `RideRoot/RideSeat`、`Input`（`DesktopMoveInput`）、`PickupField`（`PickupFieldHost`）——**这些组件的引用一个都不由工具写** |

**1. 导入四格几何（per cell）**

```
菜单: VRC Katamari/Level/Import Placement (LevelContent)
  —— 打开的必须是 PhysicsTestField 场景；四份 placement 的 level.scene 都是 "PhysicsTestField"，
     菜单会认出来并把四格全部导入（它是 cell 文档，所以逐个导入而不是拒绝）
或者逐个调用（MCP / -executeMethod）:
  PlacementImporter.Import("physics_test_field/C14")
  PlacementImporter.Import("physics_test_field/C09")
  PlacementImporter.Import("physics_test_field/C08")
  PlacementImporter.Import("physics_test_field/C10")
认成功: 每份报告里 `[level] cell=C14 +0 ~0 -0 ... other=+6` 且 arena 那行说 "not wired"
        （第一次导入时 `LevelContent/C14` 上还没有 LevelArenaHost，属正常）
```

**2. 给每个 `LevelContent/<cell>` 加 `LevelArenaHost`（4 次 Add Component）**

- 对象：`LevelContent/C14`、`/C09`、`/C08`、`/C10`（**不是** `Cells/…`）。
- 加完**先不要**改任何字段：`groundNormal`、`boundaryWalls`、`staticBoxes`、`allowedKinds` 由第 1 步的导入器填；`motor` 需要指向 `MotorHost` 上的那个 `KatamariBallMotor`（**手填**，导入器不碰别人的引用）。
- 再跑一次第 1 步的导入 → 报告应变成 `arena: wired (boxes=1, walls=4, ramps=0, kinds=0)`。
- `autoBeginPlaying` 保持 true（它只在自己 `Setup()` 时开一次 round；切格走 `Reapply()` 不会再开）。

**3. 场景对象（5 个空物体 + 3 个组件）**

| 对象 | 组件 | 手填引用 |
|---|---|---|
| `Cells/C14`（…C09/C08/C10） | `PhysicsTestCell` | 空着即可（判据导入器按「`cellId` 或物体名」认格，然后写 `cellId`）；**arena 引用也由它写** |
| `Driver` | `PhysicsTestFieldDriver` | `motor`、`pickupHost`、`ballSize`、可选 `chaseCamera`；`cells` / `presetDiametersM` / `kindWhitelist` 由判据导入器写 |
| `SizeConsole` | `LabBallSizeSetter` | `motor`；`presetDiametersM` / `presetLabels` 由判据导入器写（与驱动的 preset 同源，都来自 `diameters[]`） |

> `PhysicsTestCell` / `PhysicsTestFieldDriver` / `LabBallSizeSetter` 都是 UdonSharpBehaviour：**必须按 `Labs/README.md` 的 7 步建**（program asset → `CompileAllCsPrograms` → backing `UdonBehaviour` → `_udonSharpBackingUdonBehaviour` → `SetBehaviourVersion` → 填引用 → `CopyProxyToUdon` → 存盘重开再回写）。直接 `AddComponent<T>()` 不会被接受。

**4. 烘判据**

```
菜单: VRC Katamari/Level/Import Physics Test Field Criteria
认成功: [ptf] criteria: cells=4 checks=23 missingCells=0 missingArenas=0 driverFields=5 errors=0
        （23 = C14 7 条 + C09/C08 各 5 条 + C10 6 条；driverFields=5 = 驱动 3 个数组 + 设定器 2 个）
```

**5. 跑第一个 run**

- 进 Play Mode。`PhysicsTestCell.Start()` 会把四个 arena 全部 `SetActive(false)`（**这是设计**，见 §7.2 第 2 条），所以 Play 起来场上是空的——**不是没导入**。
- 用 MCP（`executeMethod`/UnityEvent）或临时 HUD 调 `driver.BeginCellAtDiameter("C14", 0.30f)`；
  期望日志（顺序固定）：

```
[ptf] run=1 cell=C14 D=0.300 start=(-21.000,0.170,-22.000) hdg=180.0 arena=planes=4/boxes=1/supports=2 gates=crossed->P1,no_speed_loss->P1,lift_bounded->P1
[ptf] check=crossed metric=progress_m value=+11.5231 op=>=8.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=no_speed_loss metric=speed_ratio value=+1.0031 op=>=0.9800 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=lift_bounded metric=lift_over_nominal_m value=+0.0000 op=<=0.0100 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=not_blocking metric=blocking_max value=+0.0000 op===0.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=no_saturation metric=saturated_delta value=+0.0000 op===0.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=size_accuracy metric=diameter_error_quanta value=+0.0000 op=<=1.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=no_overflow metric=overflow_delta value=+0.0000 op===0.0000 expect=pass raw=PASS verdict=EXPECTED
[ptf] summary cell=C14 D=0.300 checks=7 pass=7 unexpected=0 skipped=0 verdict=EXPECTED elapsed=6.00s
```

四格 × 0.30 的**期望结论**（在 P1 正确的前提下）：

| 格 | 期望 | 说明 |
|---|---|---|
| C14 | `checks=7 pass=7` | 0.028 的板必须碾过去：`progress ≥ 8 m`、速度比 ≥ 0.98、抬升 ≤ 名义板厚 + 1 cm |
| C09 | `on_step` = PASS（0.075 == stepLimit） | 等号语义的第一个答案；若是 `FAIL` 且 `blocked_gap ≈ 0`，说明实现用了严格大于（契约偏差，不是抖动） |
| C08 | `on_step` = PASS | 0.050 < 0.075，33% 余量：可上的正面基线 |
| C10 | `on_step` = **FAIL 但 verdict=EXPECTED**，`blocked_gap ≈ 0` | 0.100 > 0.075，0.30 球就该是墙；`blocked_gap` 大数 = 提前停住（隐形墙） |

**6. 四个球径**

对同一格再跑 0.60 / 1.10 / 2.20（`BeginCellAtDiameter`，或 `BeginCell("C14")` 一次四径）。切球径会复位球（回 0.30）再喂大，`size_accuracy` 的 `value` 就是这颗球的网格误差（单位是「一个体积单位」）。

**7. 出证据**

`Docs/Evidence/PhysicsTestField/<date>_pass1/` 下：`console.log`（`[ptf]` + `[ProxyTrace]` 原文）、`summary.json`（自己从 summary 行抄/脚本转）、`shots/C14_D0.300_cam_top.png` 等。

### 9.6 本轮实现状态

| 项 | 状态 |
|---|---|
| 几何（4 格 placement） | **已入库**；`levelbuild check` 4/4 PASS（0 error 0 warning） |
| 判据 JSON v2 | **已入库**；4 格 23 条判据 + 17 格 planned |
| Labs 三个组件 | **已写，未编译**（UdonSharp 编译、场景接线、Play Mode 全部 `NOT_RUN`） |
| 判据导入器 | **已写，未编译**；`approach_m` 的射线求交只在编辑器跑 |
| 场景 | **未创建**（§9.5 是给操作员的清单） |
| 俯视图 | 已出（`/tmp/ptf/shots/*.png`，不入库），本人已看图核对位置/尺寸 |
| 跨文件一致性 | `Docs/tests/levelbuild/test_physics_test_field_cells.py`，29 个用例 |

---

## 10. 分阶段落地

### 10.1 四个阶段

| 阶段 | 依赖 | 建哪些格 | 预期结果 | 时长 |
|---|---|---|---|---|
| **A（现在）** | 无（今天的代码） | C00 起点/球径台、C15 薄墙、C16 内角、C17 夹缝、C18 挡路行、C19 长杆 + **C14 作为「预期失败」的反例** | 6 格 PASS、C14 EXPECTED_FAIL（隐形墙）、C18 的 crate 在 2.20 之前一律「挡」 | ~5 min |
| **B（P1 合入后）** | `LevelArenaHost` + 盒顶支撑 + step-up + P1 的 14 条 NUnit 绿 | **第一批**：C14（PASS）、C07、C08、C09；然后 C10、C11、C12；最后 C13 的 `entry=teleport` | 台阶矩阵 6×4 全对；C14 从 FAIL 翻 PASS；C13 站得稳、掉得下去 | ~12 min |
| **C（P2 合入后）** | 道具支撑/挡路拆分（`SetSupportProps`） | C18 的两个子项：crate 在 2.20 变支撑、`sets_disjoint` | 「同一件道具先挡路、后通路」成立；两个集合永不重叠 | ~4 min |
| **D（P3 合入后）** | 斜面（`AddRamp` 烘焙 + 护栏） | C01–C06、C13 的 `entry=ramp`、C20 | 5 档坡度可上、50° 被拒、组合路线全程通 | ~10 min |

### 10.2 第一批（P1 合入后 30 分钟内要能跑起来）的格

**只建这四格，按这个顺序**（本轮已把数据与代码做完，剩下的是 §9.5 的接线）：

| 顺序 | 格 | 为什么是它 | 建完立刻能判什么 |
|---|---|---|---|
| 1 | **C14** 薄片 0.028 | 单盒、单判据、P1 的成败标志（0.028 < 0.075） | `progress ≥ 8 m` + 速度比 ≥ 0.98：P1 的 step-up 是否真的让「薄件不再是墙」 |
| 2 | **C09** 台阶 0.075 | **边界**（0.075 == stepLimit(0.30)） | 等号语义（`≤` 还是 `<`）第一手答案 |
| 3 | **C08** 台阶 0.050 | 明确可上（0.05 < 0.075，留 33% 余量） | 「可上」的正面基线：不抖动、单步抬升 ≤ stepLimit |
| 4 | **C10** 台阶 0.100 | 明确是墙（0.10 > 0.075，0.60 球才可上） | 「墙」的正面基线：停住位置 = r、不穿模、不绕过去 |

本批的判据与几何已经落盘（§9.6）：C14 7 条、C09/C08 各 5 条、C10 6 条，共 23 条；几何 4 份 placement 全过 `levelbuild check`。**没有任何一条跑过**——Unity/U# 未编译（§13）。

**为什么不是先建 C01–C06 斜板**：斜面（P3）要新数学（平面足迹 + 坡度护栏 + 烘焙），而 P1 的判据只需要盒顶 + 一条 `rise` 规则。先把「盒顶支撑 + 台阶」这条链验穿，再上斜面；否则斜面出问题时无法区分是「斜面数学错」还是「支撑表错」。这也与 TERRAIN_SUPPORT §8 的顺序一致（它把 P2 提到 P3 前，但把 P1 放第一）。

**建完之后**（同一天内）：C07、C11、C12（补齐 6×4 矩阵），然后 C13 的 `entry=teleport`。C13 需要 `LevelArenaHost` 的 `Reapply()` 可用（切格）——这就是 P1 接口要求的第一条外部消费方。

### 10.3 一次完整 pass 的顺序建议

```
C00（零点） → C14 → C09 → C08 → C10 → C07 → C11 → C12 → C13(teleport)
→ C18（9 件 + crate） → C19（杆/板/蕉 + detach） → C15 → C16 → C17
→ [P3 之后] C01 → C02 → C03 → C04 → C05 → C06 → C13(ramp) → C20
```

- **先 C00**：仪器零点不对，后面全部白跑。
- **C18/C19 在薄件之后**：它们会吃道具、改账本体积；放在前面会让后续格的 `absorbVolumeLimit` 与挡路集带上前一次的副作用（`ResetField` 会恢复道具，但**账本不回滚**——球径设定器每次都会把球重置回 baseline，所以这一点是可控的；写在这里提醒实现者别把「球越小越准」当成理所当然）。
- **最后 C15/C16/C17**：它们是「永远能跑」的格，放最后保证一次 pass 至少有一组可用的结论。

---

## 11. 与 TERRAIN_SUPPORT 验收 / NUnit 的对应

| TERRAIN_SUPPORT 验收 | 本场格位 | 判据 id | 备注 |
|---|---|---|---|
| S1（0.30 球被花圃挡住） | C10 / C11 / C12 | `step_outcome` @0.30 = fail | 用台阶代替花圃：同一套 `rise > stepLimit` |
| S2（1.10 球上台，`center.y − h − r = 0`） | C11（0.20 @ 1.10） | `deck_gap_m abs ≤ 0.005` | `h = 0.20` 是变量，判据形状相同 |
| S3（台顶来回 10 s，波动 < 1 mm） | **C13** `deck_stable` | `deck_y_std ≤ 0.001` | 直接对应 |
| S4（从台顶开出→落地，不被推） | **C13** `edge_drop` + `drop_is_gradual` | `landing_gap ≤ 0.005`、`fall_frames ≥ 3` | 直接对应 |
| S5（台顶不推 5 s 不动不下沉） | **C13** `deck_rest` | `deck_gap ≤ 0.005`（10 s） | 直接对应 |
| S6（第 9 个盒 → overflow） | **不在常规 pass 里**：一次会话的**最后一步**，由操作者手工给某格多加一个盒 | `overflow_delta == 1` + 前 8 个照旧 | 本场唯一**故意**触发 latch 的地方。`overflowCount` 只有 `Start` 能清，所以跑完必须重开场景/重启 Play Mode；**绝不要放在 pass 中间** |
| S7（旧 lab 回归：`terrainSupportEnabled = false` 行为不变） | 不在本场（本场全程 true） | —— | 归 `CpuSkirmish` / `StoryLevel` 自己（`BallPlayground` 已于 2026-11-15 退役） |
| S8（0.30 碾过餐巾） | **C14**（静态 0.028）+ C18 #1（真 napkin 0.014） | `crossed` / `no_speed_loss` | 两条路径：静态几何 vs 道具 |
| S9（0.30 撞保温箱 → 停住；D ≥ 1.20 时能爬） | **C18 #6** cooler_box（挡的部分）+ **C18 #9** crate（爬的部分） | @0.30：`blocked_cooler_box`；@1.10：cooler_box 的 `D_abs = 1.033` → 被吃，不再是墙。**「吸不动但能爬」由 crate 在 2.20 承担**（`crate_support_at_220`） | 原验收的 1.20 m 档不在四个 preset 里；crate 用 2.20 档等价表达同一件事 |
| S10（站上后长大，被吸走→球下落） | **C18 #9 crate** @2.20 + 直径设定器 | `absorbed` + 球心下落序列（`support_gap` 时序） | 需要「站上去再长大」的子脚本（本场列为可选子项） |
| S11（门堆 0.10 缝，台阶规则不吃水平缝） | **C17**（0.40/0.32，同性质） | `no_tunnel_gapB` | 0.10 缝在 PICNIC 地图里，本场用 0.32 做同类判据 |
| S12（6 挡路 + 6 支撑同时存在，帧时间同量级） | **C18** `crowd_capacity` | `blocking_max ≤ 6` + `mask_rebuild`（待加） | 帧时间要 Unity+Profiler |
| S13（1.5 m 板搭 0.50 m 台，18.4°） | **C01–C05**（固定 1.50 升程）+ **C20** | `climbed` / `combo_climbed_all` | 本场用「固定升程、变坡度」覆盖 5–45° |
| S14（坡顶接缝不顿、δ < 0.005） | **C13** `seam_no_step`（entry=ramp） | `frame_dy ≤ 0.02` | 接缝高度差本身由几何保证（δ = 0） |
| S15（坡中间横开→掉地，不被 AABB 挡） | **C01–C05** 的 `no_phantom_wall` + C13 的 `square_side_wall` | `ramp_entry_progress ≥ 0.30` | 「坡底隐形墙」的直接反例 |
| S16（45.1° 板 → 拒绝 + 墙） | **C06**（50°） | `ramp_refused` / `refused_not_registered` / `stopped_by_wall` | 50° 比 45.1° 更狠，且离 45° 有 5° 余量，不会踩浮点 |
| S17（停 30° 坡 5 s 不动） | **C03**（25°）/ **C04**（35°）的 `no_slip_back` | `slip_back ≤ 0.02` | 25/35 夹住 30，避免只测一个点 |
| NUnit `StepLimitScalesWithDiameter` | C00 + C07–C12 | `size_accuracy` + `step_outcome` | 四个球径的 `stepLimit` 由 C00 实测读数交叉验证 |

---

## 12. 不确定处与需要拍板

| # | 问题 | 现状/倾向 | 代价 |
|---|---|---|---|
| **U1** | **0.075 m 台阶 @ D=0.30 的等号**：`rise == stepLimit` 判「可上」还是「墙」？ | 按 TERRAIN_SUPPORT §4.1 的公式与 `PickupAbsorbMath` 的现状（严格大于才拒绝），**含等号 = 可上** | 若实测是墙 → C09 是**契约偏差**而非抖动；要改就改公式并同步 C09/bench 两处 expect |
| **U2** | **`collapsed_bench` @ D=2.20 的等号**（0.55 == 0.55） | 同 U1，含等号 → 「爬」。因为这条判据只为记录，建议**不设 PASS/FAIL**，只打 `value` | 若做成判据，一旦等号语义翻转就会假红 |
| **U3** | **napkin 厚度 2.8 cm vs 1.4 cm**：`TERRAIN_SUPPORT` §4.6 写 2.8 cm（引自 `layout.json` 时代的口径），`napkin.manifest.json` 是 1.4 cm（`notes` 说折痕是平滑高度场、不是叠层） | 本场**两个都用**：C14 用静态 0.028 钉文档口径，C18 #1 用真模型（0.014）走 P2 路径 | 若 2.8 cm 是旧版模型，C14 就是一个「已不存在的尺寸」的测试。需要一次美术确认（`Docs/Art/OriginalModels/props/README.md`） |
| **U4** | **球径设定走闭环喂体积（A）还是加测试接缝（B）** | **已定：A**（Lead 2026-11-15），并已实现。副作用：吸收计数/账本被污染（用「基线差」判据规避）、只能长大（复位负责回 0.30）、每次请求 ≤ 6 次吸收 | B 仍留着：只有它能让小球的球径精度好过「一个体积单位」 |
| **U5** | **挡路容量 6 的正向夹取在本场构造不出来**：挡路件都是 0.45–1.6 m 的大件，2.5 m 间距下球周围最多 3–4 件进 `SelectNearest` 范围；要挤到 7 个候选就得让大件 AABB 互相重叠（那是另一个错误） | 场内只做**负向**判据（`blocking_max ≤ 6` 且不 latch），正向夹取交给 NUnit `OnlyTheNearestCapacityPropsBlock` | 若以后真要场内验夹取，只能缩小件的尺寸 —— 但那些件在 2.20 球面前就是食物，不再挡路，逻辑上凑不出 |
| **U6** | **切格重建扫掠数组的 GC 是否可见** | 未测；本场 21 次切格 / 84 次重置正好是量它的最小场景 | 若 Profiler 显示明显尖峰，P4 要考虑「把 `maxStepHeight` 下沉进 `SweepMath`」（TERRAIN_SUPPORT §8 P4 已列，代价是同步改 `sweep.py`） |
| **U8** | **球径分辨率 = 一个体积单位（新发现）**：`0.0041 m³` 在 D=0.60 上是 7.3 mm 直径当量，在 0.30 上是 58 mm | 判据改成 `diameter_error_quanta ≤ 1`，并在 Evidence 里记 `quantum`（设定器有 getter）。若某个判据真的需要 ±2 mm，只能走 U4 的路线 B | 不改运行时代码就**无法**在 1.10 以下做到 ±2 mm；把这句写进 Evidence，避免下一个人以为是设定器坏了 |
| **U9** | **`allowedKinds` 的两份名单**：arena 的 `allowedKinds` 是「本格道具的 kind」，而球径设定器的 feeder 也必须过同一道吸收门 | 驱动在每次 `Reapply()` 之后重发一份 `kindWhitelist`（本批只有 `lab-feeder`），arena 那份是它的子集 | 多一处「谁最后写谁生效」的约定；已写进 §6.1 与 §9.5 |
| **U7** | **`PickupFieldHost` 的成熟度**：`BLOCKING_PROPS_PLAN` 自述「已写、未编译」；`StoryLevel.unity` 里确实挂了 `PickupField` 实例 | 本场是它**第一次被系统性测量**（C18 的全部判据都依赖它） | 若它本身有 bug，C18 的 18 条逐件判据会一起红——先看 `[PickupFieldHost]` 的启动行，再看判据 |

---

## 13. 本轮写范围与校验

| 项 | 状态 |
|---|---|
| 新建 | `Docs/Design/Levels/physics_test_field/{C14,C09,C08,C10}/placement.json`（4 份格的几何）、`Labs/PhysicsTestField/{PhysicsTestCell,PhysicsTestFieldDriver,LabBallSizeSetter}.cs`、`Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs`、`Docs/tests/levelbuild/test_physics_test_field_cells.py` |
| 改写 | `Docs/Design/BallMotor/physics_test_field.json`（v1 → v2：删掉 `cells[].registration` / `footprint` 之外的几何、`capacity_audit`、`open_questions`，加 `geometry` / `planned_cells` / `criteria_importer`） |
| 最小改动 | `Tools/levelbuild/placement_core.py`（`level.arena`、`_check_arena`、`level.id` 允许一层 `/`）、`Tools/levelbuild/blender/render_views.py`（文件名净化）、`Editor/LevelBuild/PlacementImporter.cs`（cell 模式 + 菜单一次导多格）、`Editor/LevelBuild/PlacementImportReport.cs`（`ArenaId`） |
| 未改（按写范围） | `Runtime/BallMotor/KatamariBallMotor.cs`、`Runtime/Story/LevelArenaHost.cs`、`Runtime/Pickup/PickupFieldHost.cs`、任何 `.unity` / `.prefab` / `.asset` / `.meta`、`tasks.json`、`Docs/Design/Tools/LEVEL_AUTHORING.md` |
| 没有碰 Unity | 没有打开编辑器、没有 MCP 调用；场景**未创建**（§9.5 是给操作员的清单） |
| Unity 编译 / U# 编译 / Play Mode / Build & Test | **NOT_RUN**（本 agent 不是 Unity 操作者） |
| `levelbuild check` | **4/4 PASS**（每份 0 error 0 warning） |
| `python3 Docs/tools/validate_plan.py` | **PASS**（见下） |
| `python3 -m unittest discover -s Docs/tests` | **OK, 361 tests**（见下；332 基线 + 29 新增） |
| 图表 | 俯视图 `/tmp/ptf/shots/physics_test_field_preview_plan_workbench.png`（+ 全场 / 3/4 两张），一次性产物，**不入库**；合并预览文档 `/tmp/ptf/preview/placement.json` 也不入库 |
| 未做的文档同步 | `Docs/Design/Tools/LEVEL_AUTHORING.md` §4.5 的 `level` 键表没有 `arena`（该文件不在本轮写范围）；它的 §15.4 恰好预见了「一格一份 placement」，`arena` 只是把这件事显式化。建议 Lead 在下次改该文件时补一行 |

```text
$ for c in C14 C09 C08 C10; do     python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/physics_test_field/$c/placement.json; done
PASS Docs/Design/Levels/physics_test_field/C14/placement.json (0 error(s), 0 warning(s))
PASS Docs/Design/Levels/physics_test_field/C09/placement.json (0 error(s), 0 warning(s))
PASS Docs/Design/Levels/physics_test_field/C08/placement.json (0 error(s), 0 warning(s))
PASS Docs/Design/Levels/physics_test_field/C10/placement.json (0 error(s), 0 warning(s))

$ python3 Docs/tools/validate_plan.py
PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
Dependency roots (external requirements NOT checked): T00, T01, T02, T03
NOT_RUN: Unity/Udon compilation, VR comfort, multiplayer, performance, commerce.

$ python3 -m unittest discover -s Docs/tests
Ran 361 tests in 9.310s
OK
```

**下一步（按顺序，都是操作员/下一批的事）**：
1. §9.5 的接线清单：场景 → 导入四格几何 → 加 4 个 `LevelArenaHost` → 加 3 个组件的实例 → 烘判据 → Play Mode 跑 `BeginCellAtDiameter("C14", 0.30f)`；
2. 看 `[ptf] summary` 与 §9.5 的期望表对不对；C14 必须 `pass=7`（它今天若 FAIL 就是 P1 的真实回归）；
3. 补齐 C07 / C11 / C12（同一套 placement 形状，改一个盒高），再 C13 的 `entry=teleport`；
4. 每迁一格：`Docs/Design/Levels/physics_test_field/<id>/placement.json` + `cells[]` 里把 `wired` 打开 + 删掉 `planned_cells` 那一行 + 跑 §13 的两条命令。

---

## 14. 一句话总结

**这张图不是「更漂亮的 greybox」，而是一台仪器**：21 个格位、84 次运行、128 条判据（第一批落地 4 格 / 23 条），全部落在已有的 getter 与 `[ProxyTrace]` 上；它的第一条设计结论不是几何，而是「电机容量装不下一个测试场，所以测试场必须是**一格一注册**」——而这一条恰好把 `LevelArenaHost.Reapply()` 从「计划里的一个方法」变成「有人真的在调用的接口」，也顺手把「一格一份 placement」变成了数据格式。

第二条结论是**一份数据只写一次**：`physics_test_field.json` 里再没有一格盒子的坐标，几何只活在 `<cell>/placement.json` 里；判据只认本格的 `nominal_m`，所以一条 limit 对四个球径同时成立，不需要为每个球径抄一串阈值。
