# 地图 0：球体物理测试场（Physics Test Field）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15（Asia/Shanghai）。**只出设计与数据**：没有编译、没有 Play Mode、没有 Build & Test、没有跑 Unity/NUnit；**没有改运行时代码、没有碰 Unity、没有跑 git 写命令**。文中所有米数、角度、容量都是本项目暂定值（逐条标 `PROVISIONAL`），不是原作测量值。
> 定位：**给我们自己用的球体物理回归场，不是给玩家玩的关卡**。它只有两个用途：① 在 P1/P2/P3 每阶段落地后，5–10 分钟内把「地形支撑 / 台阶 / 斜面 / 台面与边缘 / 吸附物凹凸 / 挡路 / 贴墙」全部跑一遍并自动判 PASS/FAIL；② 给出可对比的数字（球心、支撑面高、法线、接触数、每步成本），让「手感变了」能落到「哪个读数变了」。
> 上游：地形支撑模型与 P1/P2/P3 分期见 [TERRAIN_SUPPORT.md](TERRAIN_SUPPORT.md)；挡路见 [BLOCKING_PROPS_PLAN.md](../PickupAbsorb/BLOCKING_PROPS_PLAN.md)；原型格位的最初设想见 [BALL_PHYSICS_IMPLEMENTATIONS.md](../../Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md) §3.4；小棍三上限见 [02_TECHNICAL_CONTRACTS.md](../../02_TECHNICAL_CONTRACTS.md) §3。
> 数据：[physics_test_field.json](physics_test_field.json)（**唯一权威**：格位、几何位姿、道具摆放、起点、机位、判据）。本文的表格由同一份数据生成，两者不一致时以 JSON 为准并修表。
> 图：`/tmp/physics_test_field/layout.png`（一次性产物，**不入库**；侧栏含球径档位、台阶矩阵、斜板组、挡路递进、长物件、容量审计）。
> 引用约定：`Runtime/…`、`Labs/…`、`Editor/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。

---

## 0. 结论速览（先看这七条）

1. **21 个格位、84 次运行（21 格 × 4 个球径）、128 条可自动判定判据。** 四带一中心：北=斜板（6）、东=台阶（6）、南=薄件/贴墙（4）、西=挡路+长杆+台面+组合（4），中央=起点与球径台（1）。
2. **容量是这张图的硬约束，不是美学。** 电机上限「静态盒 8 / 平面 8 / 斜面 4」装不下 21 格，因此本场采用 **一格一注册**：切格 = `motor.ClearObstacles()` → 目标格 `LevelArenaHost.Reapply()`。这条不是权宜，而是把 `LevelArenaHost.Reapply()` 变成**必须可用**的接口要求（TERRAIN_SUPPORT §7.3 已列，但没写消费方；本场是第一个）。
3. **判据全部落在「已有的 getter」或「每固定步一行的 `[ProxyTrace]` 日志」上**。唯一现在拿不到的是每步成本计数器（`activeSurfaceCount` / `maskRebuildCount` / 单步耗时），本场把它列成**待加 getter**（§6.4），而不是假装能测。
4. **球径设定不改运行时代码**：用现有接缝做闭环——`SubmitCandidate(体积单位)` 喂体积 → `GetSemanticDiameterWorldM()` 读回 → 迭代收敛；回 0.30 用 `TryResetForNewRound`（它会把球重置回 baseline 直径）。三条路线的权衡在 §8.3。
5. **三处判据落在等号上**，必须写成「含等号」才自洽：0.075 m 台阶 vs `stepLimit(0.30) = 0.075`；45.0° vs `maxSupportSlopeDegrees = 45`；`collapsed_bench` 的 0.55 m 高 vs `stepLimit(2.20) = 0.55`。这三处要么全按 `≤` 读，要么全按 `<`；**混用会产出互相矛盾的 PASS**（§6.5）。
6. **现在（P1 之前）就能跑 6 格**：C00、C15、C16、C17、C18、C19。它们的判据只需「地面 + 挡路 + 扫掠 + 径向代理」，即今天的代码。C14 是 P1 的旗舰反例（今天就该 FAIL，且 FAIL 才是对的）。
7. **`napkin` 的厚度有数据冲突**：文档写 2.8 cm，`napkin.manifest.json` 实际是 **1.4 cm**。本场用一块**静态 0.028 m 薄板**钉住文档口径（C14），同时把真 napkin 放进挡路行（C18 #1），两条路径分开走（§12 U3）。

> **树状态（2026-11-15 07:20 复核）**：`Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs` **已进树**（P1 的纯数学：选面、`stepLimit`、迟滞、`IsStepCrossing`、掉落状态机与重力积分），但**还没接进 `KatamariBallMotor`、也还没有 NUnit**；`LevelArenaHost`、`AddRamp`、`GetSupportSurfaceHeightWorldM` 等 getter 都还不存在。所以本文所有 `gate = P1/P2/P3` 的判据按**电机 getter 是否存在**来 gate（不是按数学是否写了），上面「现在能跑 6 格」的结论不受影响。

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

> 俯视图（本机临时产物，**不入库**）：[layout.png](/tmp/physics_test_field/layout.png)。
> 它由 `/tmp/physics_test_field/build.py` 从**同一个 JSON** 生成（一次性的生成脚本，不入库）；图丢了不要紧，JSON 才是权威，按它重画即可。

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
| `Cells/C01…C20` | 每格一个空物体，挂 `PhysicsTestCell` + 该格自己的 `LevelArenaHost` | 新 |
| `Driver` | `PhysicsTestFieldDriver`（§7） | 新 |
| `SizeConsole` | `LabBallSizeSetter`（§8）+ 4 个 uGUI 按钮 | 新 |
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

### 3.4 P1 之前的静态 arena（Stage A）

P1 之前没有 `LevelArenaHost`，只能一场景一注册。**Stage A 恰好用满 8 盒**：

| 格 | 盒数 |
|---|---|
| C14 薄片 | 1 |
| C15 薄墙 | 1 |
| C16 内角 | 2 |
| C17 夹缝 | 4 |
| **合计** | **8 / 8** |

所以 Stage A 的场地是「地面 + 4 平面 + 上面 8 盒」，其余格子的几何**存在但是空的**（视觉有、物理没有）。这不是缺陷：Stage A 只跑那 6 格，其余格在 Stage B 才有意义。

---

## 4. 格位清单

> 表里的「各球径的主判据」括号内是**预期结果**（`✓` = 该判据在该球径下应通过，`×` = 应失败——`×` 往往是**正确行为**，例如台阶在 0.30 m 球面前就该是墙）。判据 id 与 JSON 一一对应。

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
| **C. 待加计数器** | 尚无 | `activeSurfaceCount`、`maskRebuildCount`、`stepMicros`、`stepCount` | 现在**测不到**；列为 §6.4 的接口请求，不假装能测 |

### 5.2 度量表

| metric | 定义 | 读法 | 通道 | 典型 limit |
|---|---|---|---|---|
| `rest_gap_m` | `GetGroundSupportHeightWorldM() − r` | 球心到支撑面的法向距离减半径；水平面上就是「离地」 | A | `abs ≤ 0.005` |
| `deck_gap_m` | `center.y − (台面高 + r)` | 站上台面/台阶顶 | A | `abs ≤ 0.005`（坡顶 `0.010`） |
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
| `diameter_error_m` / `diameter_drift_m` | 与 preset 的差 / 窗口内漂移 | 球径设定是否可靠 | A | `≤ 0.002` / `≤ 0.001` |
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
| `BLOCKED_UNTIL_<gate>` | 该阶段不存在，跳过 | 不是失败，是「还测不了」 |

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

```
1. 记基线：baseOverflow / baseSaturated / baseAbsorbed / baseBlocking / baseAttached
2. 切格：motor.ClearObstacles() → cell.ApplyRegistration()（LevelArenaHost.Reapply）
3. 复位球：motor.TryResetForNewRound("ptf-<cell>-<D>-<n>", startCenter, startHeading, out reason)
   —— 它把球重置为 baseline 直径（0.30）并落到格起点；reason 必须为 0
4. 复位道具：pickupHost.ResetField()（恢复父/位姿/激活，清候选/挡路/径向代理）
5. 设球径：ballSize.SetDiameterM(D)，等 IsConverged()（≤ 6 次吸收，误差 ≤ 2 mm）
6. 静置 settleSeconds = 0.5 s，采起点读数 → 判 start_rest
7. 跑脚本：按 cell.drive 的 (t, forward, turn, lateral) 调 motor.SetMoveAxes(...)
8. 到 stopSeconds 停；再采 stopSeconds 的残留读数（stuck / speed / 饱和 delta）
9. 算判据 → 每条一行日志 → 一格一行 summary
10. 截图（等一帧后）：cam_top + 该格主相机
11. 换下一个球径；四个球径跑完换下一格
```

**顺序不能换的两处**：`TryResetForNewRound` **必须早于** `SetDiameter`（它会重置回 baseline）；`ResetField` **必须晚于** `TryResetForNewRound`（前者要清掉后者可能留下的候选/代理）。

**球心高度**：`start_pose.center.y` 只是作者标注（0.15 = 起始半径）。运行时驱动写 `y = r(D) + 0.02` 再让 StepBall 的落地解把它压到 `y = 支撑面 + r`，这样任何球径都不会一开始就嵌进地面。

### 6.2 日志格式（固定、可被脚本 grep）

```
[ptf] run=37 cell=C10 D=0.300 attempt=1 start=(-6.000,0.150,1.000) hdg=90.0 gates=step_outcome->P1
[ptf] check=step_outcome metric=deck_gap_m value=0.0012 op=abs<=0.0050 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=no_jitter metric=deck_y_std_m value=0.00031 op=<=0.0020 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=per_step_lift metric=step_dy_over_step_limit value=0.31 op=<=1.05 expect=pass raw=PASS verdict=EXPECTED
[ptf] summary cell=C10 D=0.300 checks=5 pass=5 unexpected=0 skipped=0 verdict=EXPECTED elapsed=4.83s
[ptf] traj t=4.83 p=(2.10,0.25,-4.00) sup=0.100 lift=0.000 cap=0.0525 stuck=0.00
[ptf] field cells=21 runs=84 verdict=EXPECTED unexpected=0 skipped=12 elapsed=612s
```

规则：
- **一条判据一行**，字段顺序固定；`value` 用 `%+.4f` 或 `%.5f`（不要科学计数）。
- 一格一行 `summary`，一次 pass 一行 `field`。
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

| 请求 | 签名草案 | 为什么需要 | 优先级 |
|---|---|---|---|
| 每步成本 | `public int GetLastActiveSurfaceCount();` `public int GetMaskRebuildCount();` `public float GetLastStepMicros();` `public int GetFixedStepCount();` | `combo_cost` 与 §3.3 的切格 GC 必须实测；`Time.realtimeSinceStartup` 夹两端只能测整帧 | P4 |
| 支撑种类 | `public int GetSupportKind();`（0 地面 / 1 盒 / 2 斜面 / 3 道具） | C18 的 `crate_support_at_220`、C13 的 `ramp_entry_climb` 需要知道「脚下是什么」，不能从高度反推 | P1 |
| 支撑面高/法线 | `public float GetSupportSurfaceHeightWorldM();` `public Vector3 GetSupportNormalWorld();` | TERRAIN_SUPPORT §7.5 已列；`support_normal` 是「坡上滚动法线对不对」的唯一直接证据 | P1 |
| 斜面拒绝原因 | `public int GetRampCount();` `public string GetLastRampRefusalReason();` | C06 | P3 |

> **不要**为了测试场去加「测试专用」的 getter 到运行时代码里；上表四个都是**产品也需要**的诊断（HUD 读同样的值）。这是本设计的取舍：宁可等 P1/P3，也不在 Lab 里复制一份状态。

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

### 7.2 结构

```csharp
namespace VRCKatamari.Labs.PhysicsTestField
{
    /// 一场物理回归：切格 → 设球径 → 跑脚本 → 判 PASS/FAIL → 打日志 → 截图。
    /// 只活在 Labs 下；不进正式场景，不被 Runtime 引用。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestFieldDriver : UdonSharpBehaviour
    {
        // ---- 链接 ----
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private PickupFieldHost pickupHost;
        [SerializeField] private LabBallSizeSetter ballSize;
        [SerializeField] private PhysicsTestCell[] cells;        // 21 个，index 稳定
        [SerializeField] private float[] presetDiametersM;       // 0.30/0.60/1.10/2.20
        [SerializeField] private Transform[] fixedCameras;       // 与 cameras[] 的索引对应
        [SerializeField] private Transform chaseCamera;
        [SerializeField] private UnityEngine.UI.Text hudText;

        // ---- 运行参数（都是 Lab 旋钮）----
        [SerializeField] private bool autoRunAllOnStart = false;
        [SerializeField] private float settleSeconds = 0.5f;
        [SerializeField] private float stopSeconds = 2.0f;
        [SerializeField] private bool logEveryCheck = true;
        [SerializeField] private bool tracePerStep = false;      // 开 = motor.debugTraceProxySupport
        [SerializeField] private int captureEveryRuns = 1;

        // ---- 操作者 API（也被 HUD 按钮 / 外部探针调用）----
        public void BeginAll();                                  // 21 格 × 4 径
        public void BeginCell(string cellId);                    // "C10"
        public void BeginCellAtDiameter(string cellId, float d);
        public void BeginFromIndex(int cellIndex, int diameterIndex);
        public void Abort();
        public bool IsRunning();
        public string GetCurrentCellId();
        public float GetCurrentDiameterM();
        public int GetRunIndex();
        public int GetRunCount();
        public int GetExpectedCount();
        public int GetUnexpectedCount();
        public int GetSkippedCount();
        public string GetLastSummaryLine();
        public void SelectNextCell();                            // HUD
        public void SelectPreviousCell();
        public void SelectNextDiameter();
    }

    /// 一格：自己的注册数据 + 自己的判据行 + 自己的起点。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestCell : UdonSharpBehaviour
    {
        [SerializeField] private string cellId;
        [SerializeField] private LevelArenaHost arena;           // 本格唯一的注册者（P1+）
        [SerializeField] private Vector3 startCenter;            // y 被驱动忽略，见 §6.1
        [SerializeField] private float startHeadingDegrees;
        [SerializeField] private Vector2 footprintCenter;
        [SerializeField] private Vector2 footprintSize;
        [SerializeField] private string[] checkIds;
        [SerializeField] private string[] checkMetrics;
        [SerializeField] private int[]    checkOps;              // 0 abs<=, 1 <=, 2 >=, 3 ==, 4 abs>=
        [SerializeField] private float[]  checkLimits;
        [SerializeField] private int[]    checkExpectMasks;      // 每球径 2 bit：0 n/a,1 pass,2 fail
        [SerializeField] private int[]    checkGateStages;       // 0 none,1 P1,2 P2,3 P3

        public string GetCellId();
        public Vector3 GetStartCenter();
        public float GetStartHeadingDegrees();
        public bool ContainsPoint(float x, float z);             // 足迹 + activate margin
        public float DistanceToFootprint(float x, float z);
        public void ApplyRegistration();                         // motor.ClearObstacles 之后调用
        public int GetCheckCount();
    }
}
```

### 7.3 为什么判据是**并行数组**而不是「一个检查对象」

UdonSharp 没有用户结构体、没有 `List`/`Dictionary`、不能用反射。所以「一条判据」只能是并行数组的一行（`checkIds[i] / checkMetrics[i] / ...`）。**JSON 是作者数据**，编辑器侧导入器（`Editor/` 下，可读磁盘 JSON）把它烘焙成这些数组并写进场景；运行时**只读数组**，不读磁盘。这与 `02_TECHNICAL_CONTRACTS.md` §4「不要让 Udon 运行时依赖编辑器 API 或随意读取磁盘 JSON」一致。

### 7.4 状态机

```
Idle → Prepare(cell, D) → Settle → Drive → Measure → Report → (next D | next cell | Done)
```

| 状态 | 做什么 | 失败时 |
|---|---|---|
| `Prepare` | 切格、复位球、复位道具、设球径 | `TryResetForNewRound` 的 reason ≠ 0 → 记 `BLOCKED(reset)` 并跳过本 run |
| `Settle` | 等 `settleSeconds`，采起点读数 | 球心不在 `±0.05` 的期望高度 → `UNEXPECTED_FAIL(start)` |
| `Drive` | 按 `drive[]` 的阶段推 axes | —— |
| `Measure` | 采样 + 收 `[ProxyTrace]`（若开） | —— |
| `Report` | 逐条判据 → 行日志 → summary → 截图 | —— |

**采样频率**：`Update()` 每帧一次（不是每步）。所有「每步」判据必须来自 `[ProxyTrace]`（通道 B）；没有开 trace 的格，逐帧采样的 `frame_dy_m` 只能当**上界**用（一帧最多 5 步，所以 `frame_dy ≤ 5 × 单步上限` 恒成立，判据要按这个写，见 §5.2 的 `frame_drop_m ≤ 0.5`）。

### 7.5 HUD（一行，人眼可读）

```
PTF C10 D0.30 [4/84] checks 5/5  exp 0  skip 0   v=EXPECTED
```

上一行是**结论**，下面一行是**当步事实**：`gap=+0.0012 sup=0.100 lift=0.000 stuck=0.00 blk=0/6`。

---

## 8. 球径设定接口草案（`LabBallSizeSetter`）

### 8.1 需求

同一格要在 0.30 / 0.60 / 1.10 / 2.20 四个球径下各跑一遍。现状：直径**只是账本的纯函数**（`SizeModelMath.TryDerive(confirmedVolume)`），没有 setter；`startCenterWorld` 与 `baselineVolumeUnits` 都是序列化字段（改了要重开场景）。

### 8.2 接口

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
        [SerializeField] private string feederKind = "lab-feeder";
        [SerializeField] private Vector3 feederBounds = new Vector3(0.02f, 0.02f, 0.02f);
        [SerializeField] private float toleranceM = 0.002f;
        [SerializeField] private int maxAbsorbsPerRequest = 6;

        public void SelectPreset(int index);                 // 0..3；HUD 按钮绑这个
        public void SetDiameterM(float targetM);             // 驱动器用
        public void SetDiameterFromLabel(string label);      // "1.10"
        public bool IsConverged();
        public float GetTargetDiameterM();
        public int GetAbsorbCount();
        public bool ResetToBaseline();                       // 回 0.30（走 TryResetForNewRound）
        public string GetLastFailure();                      // "" / "belowBaseline" / "capExceeded" / "noFeedback"
    }
}
```

### 8.3 三条实现路线（推荐第一条）

| 路线 | 做法 | 成本 | 风险 | 判定 |
|---|---|---|---|---|
| **A. 闭环喂体积（推荐，本轮设计采用）** | 每帧 `SubmitCandidate(true, "lab-feeder", 0.02³, 球心, units, "feeder-<n>", gen)`；下一帧读 `GetSemanticDiameterWorldM()`，按 `D³ ∝ V` 修正 `units`，≤ 6 次收敛到 ±2 mm | **零运行时代码改动** | ① 吸收计数/事件被污染（挡路行与长杆格的 `absorbed_delta` 要先记基线，已按 §5.4 处理）；② 需要 `allowedKinds` 含 `lab-feeder`；③ 不能「变小」——只能 `TryResetForNewRound` 回 0.30 再长大 | **采用** |
| B. 加测试专用接缝 | `motor.SetConfirmedVolumeUnitsForTest(float)`（带 `testModeEnabled` 开关，默认 false） | 一处运行时代码 | 多一个「生产里不该存在」的写入口；U# 没有编译期隔离，只能靠开关纪律 | 备选，需 Lead 批 |
| C. 一个球径一个场景 | 改 `baselineVolumeUnits`，做 4 个场景 | 4 份场景 + 4 份维护 | 「同一格不同球径」变成跨场景比对，切格/机位/几何都可能漂 | 否决 |

**A 的实际收闭环**（伪代码）：

```
void SetDiameterM(float target) {
    targetM = target;
    if (target + 0.002 < motor.GetSemanticDiameterWorldM()) { pendingReset = true; return; }  // 只能长大
    if (!converged) { FeedOnce(); }
}
void FeedOnce() {
    float d = motor.GetSemanticDiameterWorldM();
    float need = Mathf.Pow(targetM / d, 3f) - 1f;         // V ∝ D³ → 体积系数
    float units = need * lastKnownVolumeUnits;            // 上次一单位带来的体积（measured）
    if (units <= 0f) { MarkConverged(); return; }
    motor.SubmitCandidate(true, feederKind, feederBounds, motor.GetCenterWorld(), units,
                          "feeder-" + timeStamp, absorbSeq++);
    measured = true;                                       // 下一帧读回、更新 lastKnownVolumeUnits
}
```

- **为什么不用 `cubicMetersPerVolumeUnit` 直接算**：那是 `[SerializeField] private`，没有 getter。硬编码 0.0041 会在标定一改就静默错。闭环用**测出来的**斜率，改标定不影响。
- **`ResetToBaseline()` 的实现**：`motor.TryResetForNewRound(newRoundId, 当前格起点, heading, out reason)`；它按 `baselineVolumeUnits` 重新定价，所以回的是**场景配置的起始直径**（当前 0.30），不是硬编码。
- **副作用清单**（必须写进 Evidence 的 README）：吸收计数 +N、`GetAbsorbedCount()` 变大、账本体积变大、`GetAbsorbVolumeLimitWorldM3()` 变大 → **挡路集与支撑集的分界会跟着变**。这正是我们要测的东西之一（C18 的「长大可吸」），但会污染 C14 的 `not_blocking` 判据——所以那条判据读的是「基线差」，不是绝对值。

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

### 9.2 数据（`physics_test_field.json`）

顶层键（schema_version 1）：

| 键 | 内容 |
|---|---|
| `id` / `title_zh` / `status` / `design_doc` / `purpose` / `generated_by` | 身份与出处 |
| `scene` | 路径、状态 `NOT_CREATED`、坐标系、朝向约定、边界平面（4 条，带 min/max 注释） |
| `motor_constants` | 扫掠/运动/小棍/台阶/斜面/吸收的全部常量 + 容量表（**引用代码，不重定义**） |
| `diameters[]` | 4 档：`diameter_m / radius_m / step_limit_m / protrusion_cap_m / absorb_volume_limit_m3` |
| `start` | 起点、朝向、起始球径 |
| `registration_model` | `mode = one_cell_at_a_time`、切换规则、去抖、base 注册、成本说明 |
| `cells[]` | 见下 |
| `cameras[]` | 8 个机位：`kind / position / target / euler / fov / offset_from_ball` |
| `driver` / `ball_size` | 两个 Lab 组件的路径、状态 `NOT_WRITTEN`、运行参数 |
| `evidence` | 根目录、文件布局、截图规则 |
| `capacity_audit` | 每格盒/斜面/平面数 + 全场最大值（生成时断言过） |
| `open_questions` | 6 条（与本文 §12 同源） |

单个 `cell` 的键：

| 键 | 含义 |
|---|---|
| `id` / `slug` / `name_zh` / `kind` / `stage` | 身份与阶段（`now` / `P1` / `P2` / `P3`） |
| `footprint` | `{center:[x,z], size:[sx,sz]}`：切格与「隔开」的依据 |
| `start_pose` | `{center:[x,y,z], heading_deg}`；`y` 是作者标注，运行时被 `r(D)+0.02` 覆盖 |
| `registration` | `{ground, planes, boxes[], ramps[]}`：每项带 `kind/id/center/size/rotation_euler/top_y_m` 或 `angle_deg/rise_m/run_m/slope_length_m/footprint/normal` |
| `props[]` | `{prop_id, name_zh, source, manifest, size_m, aabb_volume_m3, elongation, d_absorbable_m, center, yaw_deg, role, note}` |
| `drive[]` | `{t, forward, turn, lateral, note}`：脚本化驾驶的相位表（整格共用） |
| `sub_runs[]` | 可选：`{id, prop_id, ball_start[3], ball_start_rule, resolved_at_diameter_m, heading_deg, drive_seconds, note}`。道具相关的格（C18/C19）用它把「一件一件测」写成数据，而不是藏在驱动器的代码里 |
| `checks[]` | `{id, metric, op, limit, unit, readout, gate, window, expect{``0.30``…}, note}` |
| `cameras[]` / `unit_refs[]` / `acceptance_refs[]` | 机位、对应 NUnit 用例名、对应 TERRAIN_SUPPORT 验收编号 |
| `expectation` | `{pre_p1, p1, p2, p3}` 的人读结论（`PASS` / `EXPECTED_FAIL` / `BLOCKED_UNTIL_*`） |

### 9.3 编辑器侧导入器（P1 的一部分，不在本轮写）

`Editor/PhysicsTestFieldImporter.cs`（`Assets/VRC_Katamari/Editor/`）：

1. 读 `Docs/Design/BallMotor/physics_test_field.json`（**只有编辑器读磁盘**）；
2. 在场景里按 `cells[].registration` 建 `BoxCollider`（盒用 `size + center + euler`；斜面用 `size + euler`，并按 §5.1 的公式复核 `normal/run/rise`）；
3. 把 `checks[]` 烘焙进 `PhysicsTestCell` 的并行数组（§7.2）；
4. 把 `props[]` 烘焙进 `PickupFieldHost` 的平行数组（`propTransforms/itemIds/kinds/propBounds/valueUnits`）；
5. 建 8 个机位；
6. **自检**：每格的盒 ≤ 8、斜面 ≤ 4、平面 ≤ 8；斜面不出现在 `staticBoxes`（TERRAIN_SUPPORT §5.5 的陷阱）；同一格内道具的 AABB 不互相重叠（生成脚本里已经有这一步断言，导入器再断一次）。

> 这一层是**工具**，不是运行时。它存在的作用是：JSON 改一个数字 → 重跑导入 → 场景与文档与地图工具三者一致。

### 9.4 与地图工具的关系

本场的几何单元（静态盒 + 斜面 + 平面）与 `Docs/Design/Maps/*/layout.json` 的 `structures[]`/`collision` 是**同一组概念**（`Tools/mapbuild/validate_map.py` 已经在数 8 盒 / 8 平面）。**没有**把测试场塞进 `layout.json`：测试场不是地图、不参与 `map_snapshot` 哈希、不跑 `validate_map.py`。它的 JSON 是自己的独立 schema（`schema_version: 1`），由本文与 `physics_test_field.json` 自己保持一致。

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

**只建这四格，按这个顺序**：

| 顺序 | 格 | 为什么是它 | 建完立刻能判什么 |
|---|---|---|---|
| 1 | **C14** 薄片 0.028 | 单盒、单判据、P1 的成败标志（0.028 < 0.075） | `progress ≥ 8 m` + 速度比 ≥ 0.98：P1 的 step-up 是否真的让「薄件不再是墙」 |
| 2 | **C09** 台阶 0.075 | **边界**（0.075 == stepLimit(0.30)） | 等号语义（`≤` 还是 `<`）第一手答案 |
| 3 | **C08** 台阶 0.050 | 明确可上（0.05 < 0.075，留 33% 余量） | 「可上」的正面基线：不抖动、单步抬升 ≤ stepLimit |
| 4 | **C10** 台阶 0.100 | 明确是墙（0.10 > 0.075，0.60 球才可上） | 「墙」的正面基线：停住位置 = r、不穿模、不绕过去 |

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
| **U4** | **球径设定走闭环喂体积（A）还是加测试接缝（B）** | A 现在就能用、零运行时代码；B 更干净但要 Lead 批一处运行时代码 | A 的副作用：吸收计数/账本被污染（已用「基线差」判据规避，但 Evidence 要写清）；B 的风险是「生产里多一个写入口」 |
| **U5** | **挡路容量 6 的正向夹取在本场构造不出来**：挡路件都是 0.45–1.6 m 的大件，2.5 m 间距下球周围最多 3–4 件进 `SelectNearest` 范围；要挤到 7 个候选就得让大件 AABB 互相重叠（那是另一个错误） | 场内只做**负向**判据（`blocking_max ≤ 6` 且不 latch），正向夹取交给 NUnit `OnlyTheNearestCapacityPropsBlock` | 若以后真要场内验夹取，只能缩小件的尺寸 —— 但那些件在 2.20 球面前就是食物，不再挡路，逻辑上凑不出 |
| **U6** | **切格重建扫掠数组的 GC 是否可见** | 未测；本场 21 次切格 / 84 次重置正好是量它的最小场景 | 若 Profiler 显示明显尖峰，P4 要考虑「把 `maxStepHeight` 下沉进 `SweepMath`」（TERRAIN_SUPPORT §8 P4 已列，代价是同步改 `sweep.py`） |
| **U7** | **`PickupFieldHost` 的成熟度**：`BLOCKING_PROPS_PLAN` 自述「已写、未编译」；`StoryLevel.unity` 里确实挂了 `PickupField` 实例 | 本场是它**第一次被系统性测量**（C18 的全部判据都依赖它） | 若它本身有 bug，C18 的 18 条逐件判据会一起红——先看 `[PickupFieldHost]` 的启动行，再看判据 |

---

## 13. 本轮写范围与校验

| 项 | 状态 |
|---|---|
| 改动文件 | **只有** `Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md`（新建）与 `Docs/Design/BallMotor/physics_test_field.json`（新建）；另在 `/tmp/physics_test_field/layout.png` 生成俯视图（**不入库**） |
| 代码 / 场景 / prefab / `.asset` / `.meta` | **未改**（没有 `Labs/PhysicsTestField/`，没有 `LevelArenaHost`，没有 `LabBallSizeSetter`） |
| Unity 编译、U# 编译、EditMode/PlayMode、Build & Test | **NOT_RUN**（本 agent 不是 Unity 操作者；按规矩不碰 Unity） |
| `python3 Docs/tools/validate_plan.py` | **PASS**（见下） |
| `python3 -m unittest discover -s Docs/tests` | **OK, 279 tests**（见下） |
| 计划数据 | 本次不改 `tasks.json`、不改任何地图 JSON，所以校验结果应与基线一致 |
| 图表 | 俯视图 + 侧栏六张表；生成脚本 `/tmp/physics_test_field/build.py`（一次性，不入库，JSON 与图同源） |

```text
$ python3 Docs/tools/validate_plan.py
PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
Dependency roots (external requirements NOT checked): T00, T01, T02, T03
NOT_RUN: Unity/Udon compilation, VR comfort, multiplayer, performance, commerce.

$ python3 -m unittest discover -s Docs/tests
Ran 279 tests in 2.725s
OK
```

**下一步（给实现者的最小启动包）**：
1. `Docs/Design/BallMotor/physics_test_field.json` 的 21 个 `cells[]` 直接当作者数据；
2. `Labs/README.md` 的 7 步建场景序列 + `PipelineManager` / `ReferenceCamera`；
3. 先建 §10.2 的四格（C14 / C09 / C08 / C10），只挂 `PhysicsTestCell` + 一个 `LevelArenaHost`；
4. `PhysicsTestFieldDriver` 先只实现 `BeginCellAtDiameter` 一条路径（不要一开始就做 HUD）；
5. 判据先只接 `metric` 里的前 6 个（`rest_gap_m / deck_gap_m / deck_y_std_m / frame_dy_m / blocked_gap_m / speed_ratio`），够跑第一批。

---

## 14. 一句话总结

**这张图不是「更漂亮的 greybox」，而是一台仪器**：21 个格位、84 次运行、128 条判据，全部落在已有的 getter 与 `[ProxyTrace]` 上；它的第一条设计结论不是几何，而是「电机容量装不下一个测试场，所以测试场必须是**一格一注册**」——而这一条恰好把 `LevelArenaHost.Reapply()` 从「计划里的一个方法」变成「有人真的在调用的接口」。
