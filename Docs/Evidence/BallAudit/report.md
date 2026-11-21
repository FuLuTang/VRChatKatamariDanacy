# VRC塊魂 — 球体物理只读审计（A–F）

仓库 `/home/fulutang/Documents/Github/VRChatKatamariDanacy`，Unity 侧未运行、未编译、未跑 NUnit/Play；
本文所有 Unity 证据来自已有证据目录，不来自本次执行。**只读审计：未改任何 tracked 文件。**

每节按 **(a) 代码事实 → (b) 实测 → (c) 推断/结论** 分开写。引用格式 `path:line`（相对仓库根）。

---

## 0. 证据与边界

| 证据 | 位置 | 有效性 |
|---|---|---|
| pass2 矩阵（commit `5ceeff6`） | `/tmp/unity_round_f/bring_back_d/{console.log,matrix.md,traj_ramps.md,README.md,MANIFEST.md}` | **有效**：36 跑全部按目标直径运行（`size_accuracy` 每档同列） |
| pass1 矩阵 | Windows `Docs/Evidence/PhysicsTestField/2026-11-15_pass1/`（`/tmp` 下已被 pass2 覆盖） | **无效**：同档只有首格真的长到目标直径 |

> 注意：`/tmp/unity_round_f/bring_back_d/` 在本次审计过程中被 pass2 覆盖过（16:55）。
> 本文凡引用 pass1 的数字，都是覆盖前读到的原文件内容，并已注明。

本次审计**没有**做任何 Unity 操作（不是 Unity 操作者），下列结论中凡涉及"运行时行为"的，都是对 pass2 日志的读取或对代码的静态推导。

---

## A. 马达：不是 PhysX，是自研运动学马达（解析扫掠 + 支撑平面）

### (a) 代码事实

1. **整棵 `Runtime/` 里没有任何 `Rigidbody`**（`grep -rn "Rigidbody" Runtime/` 0 命中），也没有 `Physics.*` 调用。球是一个被每固定步直接写 `Transform.position` 的运动学物体：
   - 固定步时钟 + 阶段 1–9：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs:895`（`StepBall`），50 Hz 累加器在 `:640`（`Start`）/ `:869`（`Update`）。
   - 阶段顺序：输入 → 运动 → **台阶掩码** → 扫掠 → 支撑选择 → 接地高度 → 吸附裁决 → 账本 → 尺寸 → 视觉滚转 → 跟随架（`:895`–`:1289`）。
2. **推动球的所有几何都是数字，不是 Collider**：
   - 水平阻挡 = `SweepMath.TrySweepSphere(..., collisionRadiusWorldM, planeNormals, planeOffsets, boxMinima, boxMaxima, ...)`（`KatamariBallMotor.cs:947`）。扫掠只吃"平面 + 半径膨胀 AABB 盒"，**斜面根本不在这个调用里**。
   - 竖直 = `GroundContactMath.TrySolve(coreRadius, visualRollRotation, localProxyPoints, supportNormal, supportOffset, ...)`（`:1108`），只沿法线做位置修正（`GroundContactMath.cs:104`：`targetCoordinate = offset + support`）。
   - 支撑面选择 = `SupportSurfaceMath.TryResolveSupport`（`SupportSurfaceMath.cs:793`）。
3. **Unity Collider 的唯一作用是一次性烘焙**：`LevelArenaHost` 在 `Setup()` 里读 `BoxCollider.bounds`、`box.center/size`、`transform.rotation/lossyScale`，之后**永不再读**（`Runtime/Story/LevelArenaHost.cs:193`，注释 `:26`–`:30`）。所以"改 Collider 会在运行时改变球的行为"是不成立的；但"Collider 填错/没接线"会让马达拿到错数字或 0 个登记。

**马达会碰到的全部几何、登记方式与容量**

| # | 几何 | 登记入口 | 容量 | 用的是 Unity Collider 还是自己的烘焙数 |
|---|---|---|---|---|
| 1 | 地面平面（解析平面 `n·x = d`） | `SetGroundPlane(normal, offset)`（`KatamariBallMotor.cs:2090`） | 1 | 自己的数（`LevelArenaHost.groundNormal/groundOffset`） |
| 2 | 边界墙（半空间平面） | `AddPlane`（`:2112`），`normal = wall.rotation*(0,0,1)`、`offset = dot(normal, wall.position)`（`LevelArenaHost.cs:160`–`174`） | **8**（`MaxPlaneCount`，`:351`） | 只读 Transform，不读 Collider |
| 3 | 静态盒（扫掠障碍 **且** 可站立顶面） | `AddBox(bounds.min, bounds.max)`（`:2128`） | **8**（`MaxBoxCount`，`:352`） | 烘焙 `BoxCollider.bounds` 的**世界 AABB**一次（旋转丢失，`LevelArenaHost.cs:193`–`196`） |
| 4 | 静态斜面 | `AddRamp(center, normal, right, halfWidth, halfRun)`（`:2187`），由 `TryBakeRamp` 从 Collider 的 centre/size/rotation 烘出（`SupportSurfaceMath.cs:531`） | **4**（`MaxRampCount`，`SupportSurfaceMath.cs:113`） | 自己的烘焙数：行走面 = 盒的 **+Y 面** |
| 5 | 动态挡路盒（吸不动的道具） | `SetBlockingBoxes(min[], max[], count)`（`:2317`） | **6**（`MaxBlockingBoxCount`，`:360`） | 宿主传世界 AABB 数字 |
| 6 | 可站立道具（吸不动但矮） | `SetSupportProps(...)`（`:2480`） | **6**（`SupportSurfaceMath.MaxSupportPropCount`，`:363`） | 同上 |
| 7 | 径向代理（吸附的细长物件） | `AttachRadialProxy(...)`（`:2925`） | **4 物件 × 8 角点 = 32 端点**（`:596`–`:597`） | 道具局部轴 AABB（`propLocalBounds`） |
| 8 | 扫掠接触缓存 | `ContactCapacity` | **4**（`:353`） | — |
| 9 | 待吸候选队列 | `SubmitCandidate` | 4（`PendingCandidateCapacity`，`:475`） | — |

所以任务里说的 **8/8/4/6/6 = 平面/静态盒/斜面/挡路盒/道具支撑**，全部对上；另有 4 接触、4 候选、4 代理物件、32 端点。

### (b) 实测

- pass2 每次跑表头：`arena=planes=4/boxes=1/supports=3/ramps=1/rampCell=1`（坡道格）与 `supports=2/ramps=0`（非坡道格）——**扫描/支撑表里就是这些数字，没有任何 Collider 参与运行时**。
- `[arena] planes=4/4 boxes=1/1 ramps=0/0 supports=2 refused=0 kinds=0`（pass2 `console.log:1`）。

### (c) 结论

球**不是** PhysX 刚体：没有 Rigidbody、没有 Collider 驱动的运动、没有 `Physics.Raycast`。它是"输入 → 水平速度积分 → 对平面/AABB 的解析球扫掠 → 高度场支撑 → 法线修正"的自定义运动学马达。
**斜面上的 BoxCollider 只决定烘焙出的那 6 个数字**（行走面中心、法线、右轴、半宽、半长）；它本身不挡球、不抬球。

---

## B. 斜坡：为什么"像穿过去"

### (a) 代码事实：一条坡从 json 到球的完整链路

1. **产生**：`placement.json` 的 `structures[]` 里 `role="ramp"` → `PlacementImporter` 建一个 primitive Cube：`localScale = size`、`rotation = rotationQuat`、`BoxCollider(size=1, center=0)`（`Editor/LevelBuild/PlacementImporter.cs:1039`、`:1135`–`:1139`）。尺寸约定 `size = (宽, 厚, 沿坡长)`。
2. **接线**：`role="ramp"` 只进 `LevelArenaHost.rampBoxes`，**绝不进 `staticBoxes`**（`PlacementImporter.cs:1808`–`:1814`；同一 Collider 两边都填会被 `LevelArenaHost.cs:185`–`:190` 报错并丢掉 box）。
3. **烘焙**：`RegisterRamp`（`LevelArenaHost.cs:333`）→ `SupportSurfaceMath.TryBakeRamp(centre, rotation*up, rotation*right, worldSize, 45°, ...)`：
   - **行走面 = 盒的 +Y 面**：`faceCenter = boxCenter + n·(size.y/2)`（`SupportSurfaceMath.cs:595`）；
   - `halfWidth / halfRun` = 行走面四角在"水平右轴 / 水平下坡方向"上的最大投影（`:598`–`:625`），所以是**水平足迹矩形**、`halfRun = 沿坡长/2 · cosθ`；
   - 坡度护栏 `maxSupportSlopeDegrees = 45`（motor 序列化，`KatamariBallMotor.cs:166`；场景 `PhysicsTestField.unity:8801`），超限**拒绝注册**并带原因码（`SupportSurfaceMath.cs:480`）。
4. **参与方式（关键）**：`TryResolveSupport` 只在**球心的 XZ** 上取坡面高度：
   `h = (n·C − n.x·p.x − n.z·p.z) / n.y`，要求 `IsStepWithinLimit(h, reference, stepLimit)`（`SupportSurfaceMath.cs:947`；判据在 `:260`，**只有上界** `rise ≤ stepLimit`，往下不设限），再要求球心 XZ 落在水平矩形内（`:954`–`:960`），最后"最高者胜"。
   `reference = max(上一步支撑高度, 球心.y − collisionRadius)`（`:220`）；`stepLimit = 0.25 · semanticDiameter`（`:188`）。
5. **球心高度**：被选中后 `GroundContactMath` 把球心放到 `平面 + r·n`（`GroundContactMath.cs:89`–`104`），即球**与行走面相切**，既不沉入也不悬空。
6. **它从不进扫掠**：`StepBall` 的扫掠只传 `planeNormals/planeOffsets/boxMinima/boxMaxima`（`KatamariBallMotor.cs:947`）；设计文档同样写"斜面只支撑、绝不进扫掠"（`Docs/Design/BallMotor/TERRAIN_SUPPORT.md:805`，另见 `:100`、`:282`）。

> **所以"斜面"在马达里是一个高度场，不是一块板**：没有侧面、没有下表面、没有顶面以外的任何面。
> 任何"band 判据不成立"或"球心 XZ 不在矩形内"的接近方式，马达**完全不知道那块板存在**，球会直接穿过板的体积。

**会穿过去的三种情况（代码推导）**
1. **从侧面/高段接近**：球在低处（球底 ≈ 0），球心 XZ 落在坡的高段，`h` 远大于 `0.25D` → `rise > stepLimit` → 坡不是候选 → 不挡、不抬 → 球从板中间穿过去（球的下半部在板里，上半部露出来）。
2. **从板下方**：同上，`h` 在球心之上，永远超出 band。
3. **球心偏出坡宽**：足迹就是板本身的宽度，**不按球半径扩张**（对比：所有挡路盒都按半径扩张，`SweepMath.cs:623`/`:130`）。level 1 的坡只有 **0.35 m 宽**，球心必须落在 ±0.175 m 内才有支撑；2.2 m 的球很容易"看起来在坡上、其实球心在坡外"。

### (b) 实测：pass2（有效）

几何（我从 `placement.json` 复算，脚本 `/tmp/ball_audit/ramps.py`，输出 `ramps_out.txt`）：**每一条坡的行走面都从坡脚的 y≈0 精确升到台面高度，坡度与作者值一致**。

| 格 | 坡度 | 行走面 低端 (Y,Z) | 高端 (Y,Z) | 台面 | 顶面 Y | 板厚 |
|---|---|---|---|---|---|---|
| C01 | 5.0003° | (0.0000, 7.6001) | (0.6000, 14.4581) | C01_top | 0.6000 | 0.04 |
| C03 | 25.0000° | (0.0000, 7.6000) | (0.6000, 8.8867) | C03_top | 0.6000 | 0.04 |
| C05 | 44.9999° | (0.0001, 7.6000) | (0.6000, 8.1999) | C05_top | 0.6000 | 0.04 |
| C13 | 11.3101° | (0.0000, 7.0000) | (0.6000, 10.0000) | C13_deck | 0.6000 | 0.04 |
| story_01 `ramp_to_bed` | 10.7132° | (0.0000, −12.374) | (0.2599, −11.000) | flower_bed | 0.2600 | 0.04 |
| story_01 `ramp_to_table` | 30.0084° | (0.0000, −10.712) | (0.7000, −9.500) | table | 0.7000 | 0.04 |

C06 是**故意**的 50° 反例：`role="box"`、`kind="ramp_probe"`，世界 AABB 顶 0.60 > 最大 `stepLimit` 0.55，永远是墙（`Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md:217`–`231`）。

**轨迹（pass2 `traj_ramps.md`，`sup` = 马达报告的支撑面高度，球底 = 球心 y − D/2）**

| 格 / D | 球是否爬上去 | 关键读数 | 判定 |
|---|---|---|---|
| C01 0.30 / 0.60 / 1.10 | **是** | `sup=0.600`，球底 0.600（= 台面） | 与支撑面 ≤3 mm 吻合 |
| C01 2.20 | 是 | 球底 0.590，`deck_gap=+0.0134` | `climbed` 差 3.4 mm → UNEXPECTED_FAIL |
| C03 0.30 / 0.60 / 1.10 / 2.20 | **是**，然后从台面北缘掉回地面（`sup=0.000`） | 球底 0.600 | 4 档全 PASS |
| C05 0.30 | **只到接缝** | `sup=0.449`，球底 0.510，`deck_gap=+0.0887` | UNEXPECTED_FAIL（climbed） |
| C05 0.60 / 1.10 | 只到接缝 | `gap=+0.1763` / `+0.3234` | 各多 1 条 `ramp_entered` |
| C05 2.20 | **一步都没动** | 球心恒 1.100，`sup=0`，`stuck` 在 0.42–1.50 之间反复（卡死计时器循环） | 3 条 UNEXPECTED |
| C13 0.30 / 0.60 / 1.10 | **是** | 球底 0.600 | 全 PASS |
| C13 2.20 | 是，然后掉下去 | 单帧垂直位移 **0.9499 m**、`support_kind=2`（= 斜面）、`landing_gap=+0.0419`、`landed_on_ground` 得到 2 | 4 条 UNEXPECTED |

球底 vs 支撑面的偏差（C01 0.30：球底 0.080 / `sup` 0.077）说明**球没有陷进行走面**，两者只差 3 mm 的球面几何量。
`lift` 36 跑全为 `0.000`：径向代理在这批测试里完全惰性（场上没有细长道具）。

**pass1（无效）的一个补充观测**：pass1 的每次跑都报 `supports=2`（= 1 盒 + 0 斜面 + 地面），即那一版**坡道根本没登记进马达**（`GetRegisteredSupportCount = boxes + ramps + ground`，`LevelArenaHost.cs:305`–`310`，该函数在 pass1 的 commit 上就是这段）。pass2 同格是 `supports=3/ramps=1`。两次之间场景被"判据重导后存盘"，所以最可能是**接线/重导把 `rampBoxes` 补上了**（推断）。也就是说：设计者如果看的是 pass1 那一版，"球直接穿过坡"是**字面意义上的**——马达里没有坡。

### (c) 结论与最小修复

- **不是 Collider 做错了**：行走面 = 视觉板顶面，位置/角度/厚度全部正确，球也精确贴在面上（≤3 mm）。pass2 里 0.30/0.60/1.10 档 C01/C03/C13 是**真的滚上去**了。
- **是设计限制 + 三个真缺陷**：
  1. **设计限制（不可回避）**：斜面只支撑、不进扫掠。从侧面、从板下、球心偏出坡宽，都会被穿过。0.35 m 宽的 level 1 坡把这个限制放大到"经常发生"。
  2. **45° 接缝死锁（半径无关的几何不变量）**：球被台面的半径膨胀 AABB 挡在离台面 `r` 处，此处坡面还差 `r(tanθ+1−1/cosθ)` 才到台面；`0.25D` 的台阶预算按 `0.5r` 算，于是 `tanθ+1−1/cosθ ≤ 0.5` 才可能上去 → **θ ≤ 36.87°**。45° 恒不成立（我算的 gap：0.30/0.60/1.10/2.20 = 0.0879/0.1757/0.3222/0.6444，实测 0.0887/0.1763/0.3234/0.6000），所以 C05 在**所有**球径都卡住。
  3. **2.2 m 球把台面当成墙**：C05 2.20 球连坡脚都到不了（台面 AABB 按 1.1 m 膨胀，`zmin 8.2 − 1.1 = 7.1`，起点 6.8），卡死计时器每 1.5 s 把球传送回起点 → "球完全不理坡"。
  4. **C13 2.20 单帧 0.95 m 垂直跳变**：球径 2.2 > 台面高 0.6，球心跨过台面边缘时支撑在"台面顶 0.6 / 地面 0"之间跳变，配合下落状态产生一次近 1 m 的帧内位移（推断，0.5 s 采样无法分辨中间过程）。
- **最小修复（不实现）**，按代价排序：
  - **(R1) 把"接缝规则"写进球空间**：登记坡时要求 `台面顶 − 球底(可达点) ≤ stepLimit`，等价于 `θ ≤ 36.87°` **或** 让行走面在台面立面外再延伸 `(gap − stepLimit)/tanθ`（45° 时 = 0.013/0.026/0.047/0.094 m）。改一处 `TryBakeRamp` 的验收 + C05 作者数据（或把 C05 改成带 2 cm 倒角的坡顶）。
  - **(R2) 给"只支撑"的坡加一个可选的侧向/底面挡板**：把坡的**两端薄边**（不是整块板）登记为低矮 box，或给 `AddRamp` 增加"side walls"选项；这是唯一能让"从侧面撞坡"有反馈的办法，成本在 `SupportSurfaceMath` + 关卡数据。
  - **(R3) 窄坡配宽球**：level 1 的 0.35 m 坡应至少 `2r + 板宽` 或用"球心到坡中心线距离 ≤ halfWidth + r×k"的宽容判据（现在完全没有宽容）。
  - **(R4) 大球的小台面**：台面高 < 球半径时（C13 2.20）应禁止这种组合，或让台面在 band 内直接当"可碾过的小台阶"，避免 0.95 m 跳变。

---

## C. 三个体积：名字上是三个，今天是同一个球

### (a) 代码事实

`SizeModelMath.TryDerive`（`Runtime/BallLayers/SizeModel/SizeModelMath.cs:126`–`192`）：

```
coreRadius      = min( cbrt( 3·(baseline + confirmed)·c / (4π) ), maxCoreRadiusWorldM )
collisionRadius = coreRadius × collisionToCoreRadiusRatio
semanticDiameter= (2 × coreRadius) × semanticDiameterPerCoreDiameter
```

序列化常数（`KatamariBallMotor.cs:193`–`197`，场景 `PhysicsTestField.unity:8813`–`8814`）：
`baselineVolumeUnits = 3.4390244`、`cubicMetersPerVolumeUnit = 0.0041`、`maxCoreRadiusWorldM = 1.1`、
**`collisionToCoreRadiusRatio = 1`**、**`semanticDiameterPerCoreDiameter = 1`**。

各量的消费者：

| 量 | 定义 | 消费者 |
|---|---|---|
| `coreRadiusWorldM` | 上式 | 接地（`GroundContactMath`，`:1108`）、视觉滚转角速度（`:1238`）、代理突出上限（`:1050`）；**无公开 getter** |
| `collisionRadiusWorldM` | core × ratio | 水平扫掠（`:947`）、吸附 reach（`PickupAbsorbMath.cs:244`）、重生高度（驱动 `radius+0.02`） |
| `semanticDiameterWorldM` | 2core × ratio | HUD（`GetSemanticDiameterWorldM`，`:3326`；目前无 HUD）、**视觉网格缩放**（`BallView.cs:61`）、跟随架视高（`:1754`）、`stepLimit = 0.25D`、**吸收体积门**（`PickupAbsorbMath.cs:348`） |
| 视觉网格 | `CoreMesh.localScale = (D,D,D)`（`BallView` 契约：CoreMesh 是单位直径球，`BallView.cs:34`–`38`） | `BallPresentation/BallView.cs:44`–`:62` |
| 吸收门体积 | `V_ball = π/6·D³`；`limit = min(V_ball/10, 512 m³)` | `PickupAbsorbMath.cs:348`–`:380`；reach = `collisionR + contactR(0) + 道具包围球` |
| 道具 AABB | `propBounds`（世界 AABB）/ `propLocalBounds`（道具局部轴） | 吸收门/挡路/支撑用前者，径向壳用后者 |
| 径向壳 | `core + min(0.35·core, 0.30 m)`，只影响**接地高度** | `KatamariBallMotor.cs:1050`、`:1108` |

### (b) 实测（数值表）

| 量 | D=0.30 | D=1.10 | D=2.20 | 用于 |
|---|---|---|---|---|
| `coreRadiusWorldM` | 0.150 | 0.550 | 1.100（封顶） | 滚动/接地、视觉半径 |
| `collisionRadiusWorldM` | 0.150 | 0.550 | 1.100 | 墙/道具阻挡、reach |
| `semanticDiameterWorldM` | 0.300 | 1.100 | 2.200 | HUD、视觉、stepLimit、吸收门 |
| 视觉网格 scale | 0.300 | 1.100 | 2.200 | 屏幕上的球 |
| 球体积 π/6·D³ | 0.01414 m³ | 0.6969 m³ | 5.5753 m³ | 吸收门 |
| 吸收上限 V/10 | 0.001414 | 0.06969 | 0.55753 | 能吸多大的道具 |
| `stepLimit = 0.25D` | 0.0750 | 0.2750 | 0.5500 | 台阶/台面判据 |
| 代理突出上限 | 0.0525 | 0.1925 | 0.3000（绝对护栏） | 只有接地 |
| 实测 `cap`（pass2） | 0.0525 | 0.1927 | 0.3000 | 与上式一致 |

### (c) 结论

**今天只有一个物理球**：`collisionRadius == coreRadius`（ratio 1），`semanticDiameter == 2·coreRadius`（ratio 1），而视觉网格正好按 `semanticDiameter` 缩放。所以"滚动体积 = 挡墙体积 = 视觉球 = 吸收 reach 球"**完全同一个球**。
所谓"三个体积"目前是**三个名字 + 一个球**；只有两个真正不同性质的量：
1. **吸收门体积**（`π/6D³/10`）——它是**判定用的公式量**，不是碰撞体；
2. **径向代理壳**（`core + 0.35core`）——**只抬接地高度、不参与水平碰撞**，而且只有在吸附了"细长物件"时才非零；这批测试里 `lift` 全 0，所以等于不存在。
风险：`BallView` 用的是 `semanticDiameter`，而马达内部用 `coreRadius`；一旦有人把 `semanticDiameterPerCoreDiameter` 调成 ≠1，**视觉球和物理球会立刻分家**（视觉变大、碰撞不变），这是隐藏的地雷，值得在 HUD/调试面板上把三个数同时打出来。

---

## D. 小棍（径向代理，32 方向）

### (a) 代码事实

- **方向**：`RadialProxyMath.TryFillDirections`（`Runtime/BallLayers/RadialProxy/RadialProxyMath.cs:78`–`100`）：`y_i = 1 − 2(i+0.5)/32`，方位角按黄金角；`DirectionCount = 32`（`:57`）。
- **表语义**：`supports[d] = max_i dot(p_i, d)`（`TrySolveSupports`，`RadialProxyMath.cs:240`；端点云在方向 d 上的支持长度），**不预存 core 下限**，读的时候再 `max(core, …)`（`GetProxySupportLength`，`KatamariBallMotor.cs:3121`–`3128`）。
- **端点**：每个"长物件"取 **8 个 AABB 角点**（`RadialProxyShellMath.cs:61`、`:207`），最多 **4 个物件**（`MaxExtremeCandidates`）→ 32 个端点。
- **关键事实：接地不用这 32 个方向**。每步真正喂给 `GroundContactMath` 的是端点云本身：
  `RadialProxyShellMath.TryBuildContactPoints(core, cap, proxyEndpointPool, count, localProxyPoints)`（`:592`）→ `GroundContactMath.TrySolve(...)` 里对**每个端点**算 `max(−dot(n, R·p_i))`（`GroundContactMath.cs:89`–`99`）。
  32 方向表只被 `TryAttachItem / TryDetachItem / TryRebuildShell / TryClearShell` 和诊断 getter 使用（`grep proxySupports` 只有这些）。
- **坐标系**：端点与方向都是**球局部系**（`RadialProxyShellMath.cs:34`–`40`：`localCenter + localRotation·(±extent/2)`），每步由 `visualRollRotation` 旋转到世界（`GroundContactMath.cs:92`）。所以它们**随球滚动而转**；`GetProxySupportLength(d)` 返回的是**球局部方向**上的支持，没有世界方向版本。
- **准入门槛**（`RadialProxyShellMath.TryClassifyLongObject`，`:146`–`186`）：最长边 ≥ `0.5 × 球直径` **且** 最长边 ≥ `2.5 × 第二长边`；另有"最远角点至少超出 core 1 cm"（`minProxyProtrusionM`）。所以杯子/方块/方片/勺（声明成 0.34³）都不进。
- **用途**：**只有接地高度**。墙/道具阻挡用 `collisionRadius`（不含突出），吸收门用体积，吸附预测只调 `AttachRadialProxy`。
- **尺寸口径**：壳用 `propLocalBounds`（道具自身局部轴），吸收门/挡路用 `propBounds`（世界 AABB）；缺列时宿主回退并打一条 warning。

### (b) 实测

- 36 跑 `lift = 0.000`、`cap` 随 D 变化（0.0525/0.1048/0.1927/0.3000）→ **代理壳在这批测试里没有任何物理效果**（场上 `propCount=0`）。
- Fibonacci 覆盖（我算的，`/tmp/ball_audit/fib_out.txt`）：N=32 最近方向最大偏差 **27.38°**、平均最近邻 34.12°；N=16 最大偏差 **38.88°**、平均 47.45°。细长尖刺若正好落在两个方向中间，表值会低 `cos(27.4°)=0.89`（32 向）或 `cos(38.9°)=0.78`（16 向）。

### (c) 判断与 32→16 计划

- **采样够不够**：对"每步物理"来说**无所谓**——物理用的是精确角点云，不是 32 个采样；对"稀疏外形摘要/诊断"来说 32 向已经偏粗（最坏 27°），但它是给人看的，不是判据。
- **改成 16 会变什么**：表分辨率降到最坏 38.9°；**物理行为 0 变化**；每次吸附的"受影响方向"重算从 32×8 降到 16×8 点积（约省一半，本身已是微秒级）；数组从 32 缩到 16。
- **所有硬编码 32 的地方**（只读 grep）：

| 文件 | 行 | 内容 |
|---|---|---|
| `Runtime/BallLayers/RadialProxy/RadialProxyMath.cs` | 57 | `DirectionCount = 32`（**唯一真源**） |
| 同上 | 78–100, 192–202, 248–254, 394–431, 540 | 全部用 `DirectionCount`，自动跟随 |
| `Runtime/BallLayers/RadialProxy/RadialProxyShellMath.cs` | 277–287, 397–406, 513–514, 547–554, 592–605 | 全部用 `DirectionCount`（要求 `Length == DirectionCount`） |
| `Runtime/BallMotor/KatamariBallMotor.cs` | 780, 786, 798, 3109 | 数组/`GetProxyDirectionCount()` 用 `DirectionCount` |
| `Tests/EditMode/BallLayers/RadialProxyTests.cs` | 20–21（注释）、31–39、53、261（注释）、270 | 数组用 `DirectionCount`；**262–263 `new Vector3[31]/[33]`、271 `new Vector3[31]` 是字面量** |
| `Tests/EditMode/BallLayers/RadialProxyShellTests.cs` | 24–35, 159, 170, 200, 263, 288, 320 | 全部 `DirectionCount` |
| `Docs/Design/BallLayers/RadialProxy/radial_proxy.py` | 19 | `direction_count: int = 32` |
| 同上 | 147–148 | `!= 32` 直接 `ValueError`（模型硬门） |
| `Docs/tests/ball_radial_proxy/test_radial_proxy.py` | 84 | `assertEqual(len(proxy.directions), 32)` |
| 同上 | 153 | 越界用例 `(-1, 32, 1.0)` |
| `Docs/Design/BallLayers/RadialProxy/README.md` | 多处（"32 个确定性 Fibonacci 方向"、`proxyDirections[32]` 等） | 文档 |
| `Docs/Design/BallLayers/RadialProxy/INTERFACES.md` / `SIMULATION_REPORT.md` / `TEST_OBJECT_SPEC.md` | 各含 32 | 文档 |
| `Docs/Design/BallMotor/*.md`（README、TERRAIN_SUPPORT、SPLIT_PROPOSAL、PHYSICS_TEST_FIELD） | 各含 32 | 文档 |
| `Docs/Design/BallLayers/{Attachment,FollowRig,RemoteProxy,VisualRoll}/README.md` | 各含 32 | 文档 |
| `.asset`（场景/prefab 序列化） | — | **没有**：代理数组都在 `Start()` 里按 `DirectionCount` 新建，不序列化（`KatamariBallMotor.cs:780`–`798`），改常数不需要动任何场景 |

**16 向实施顺序（不执行）**
1. `RadialProxyMath.cs:57` 改 `DirectionCount = 16`（其余 C# 自动跟随；`MaxExtremeCandidates=4`、`AabbCornerCount=8`、端点池 32 **不变**——那是 4×8，与方向数无关）。
2. `RadialProxyTests.cs:262–263` 的 `31/33` → `15/17`，`:271` 的 `31` → `15`，注释 "32" → "16"。
3. Python 模型 `radial_proxy.py:19`、`:147`，测试 `test_radial_proxy.py:84`、`:153`。
4. 文档 10+ 处（RadialProxy 四份 + BallMotor 四份 + 4 个 BallLayers README）与 `SIMULATION_REPORT` 的读数。
5. 风险：(a) 32 向的"半球受影响"集合在 16 向时更粗，**若**以后 C 阶段把表用作水平外形，最坏 38.9° 的漏检要重新评估；(b) `TrySolveSupports` 的"方向数必须等于 `DirectionCount`"会让任何外部预分配的 32 长数组直接报错——现在只有测试和 motor 持有，无外部调用者；(c) 无 `.asset`/场景迁移成本。
6. **建议**：如果目标只是"便宜一半"，收益极小（物理不用它）；真正该做的是**先把 32 向表从"装饰性摘要"变成有消费者的东西，或者直接删掉**，否则 16 只是把没人用的表改小。

---

## E. 吸附物缩放：尺子**不会**跟着球长大（今天是正确的）

### (a) 代码事实

1. **球的视觉缩放只写一个子节点**：
   `BallView.Update()` → `coreMesh.localScale = Vector3.one * motor.GetSemanticDiameterWorldM()`（`Runtime/BallPresentation/BallView.cs:44`–`:62`）。
   类注释明确："never scales the rolling root … and never scales the attachment root, because absorbed props must not grow with the core"（`BallView.cs:19`–`:22`）。
2. **马达只写位置/旋转，从不写 scale**：`ApplyPresentation()` 只写 `ballPhysicsRoot.position` 与 `ballVisualRollRoot.rotation`（`KatamariBallMotor.cs:1774`–`1783`）。
3. **吸附是"换父节点 + 保持球局部位姿"**：
   `PickupFieldHost.AttachProp`（`Runtime/Pickup/PickupFieldHost.cs:976`）：
   `AttachmentMath.TryWorldPoseToLocal(ballCenter, visualRollRotation, worldPos, worldRot, worldScale, out localPosition, out localRotation)`（`:1005`）→ `prop.SetParent(attachmentRoot)`、写 `localPosition/localRotation`（`:1013`–`:1015`），并禁用该道具的 Collider（`:1016`）。
   **localScale 不写**，`AttachmentRoot` 的 lossyScale 为 1，所以世界尺寸原样保留。
4. **场景里的层级**（我在 `Worlds/StoryMode/StoryMode.unity` 里核对过）：
   `PlayerBall → BallPhysicsRoot → BallVisualRollRoot → { CoreMesh, AttachmentRoot }`，
   四个节点 `localScale` 全为 `(1,1,1)`、`localPosition` 全为 0。
   所以 `CoreMesh` 的缩放**不会**传染给 `AttachmentRoot` 下的道具。
5. **"埋进球里"的策略：没有**。吸附道具只保留球局部位姿，球长大后 core 半径变大而道具位置不变 → 道具会逐渐陷进球体。代码里只有：
   - 吸附失败才 `HideProp`（`PickupFieldHost.cs:1059`）；
   - 代理准入的 `minProxyProtrusionM` 只在"角点不超出 core 1 cm"时**跳过代理登记**（不隐藏视觉）；
   - `RemoteProxyMath` 有 `buried` 槽位概念（`Runtime/BallLayers/RemoteProxy/RemoteProxyMath.cs:73`、`:345`），但那是**远端球的代理**，和本地吸附道具无关。
   即：目前**没有任何"埋没后隐藏/淡出/缩回"的策略**。

### (b) 实测

- 场景/预制体层面：`StoryMode.unity` 的层级与 scale 如上（直接读 YAML 核对）。
- 没有任何 Play 证据表明"尺子跟着长大"或"尺子被隐藏"；测试场 `propCount=0`，pass2 无吸附事件。

### (c) 结论

- **今天一根粘在球上的尺子不会随球长大** —— 这是**按设计正确**的，而且是"只缩放 `CoreMesh`、不缩放父节点"实现的，符合设计者要求。
- **最大的风险不是"会不会长大"，而是"会不会被埋"**：球长大后 core 会把吸附物吞进去，目前无任何处理。
- **最小正确修复**（若以后要处理埋没）：不要改父节点缩放（那会同时改变道具世界尺寸）；在 `BallView`/`PickupFieldHost` 里按 `prop 到球心距离 + prop 半径` 与当前 `coreRadius` 比较，超出即隐藏或沿径向推出去（数据已经在 `propLocalBounds`/`attachmentRoot` 手里）。另建议把 `CheckAttachmentRootOnce`（`PickupFieldHost.cs:1092`–`1107`）从"只查距离"扩成"同时查父节点与 scale"，否则把 `AttachmentRoot` 误挂到 `CoreMesh` 下、local 0 时距离检查会通过，然后道具**会**跟着长大。

---

## F. 爬墙：今天完全没有

### (a) 代码事实

1. **坡度上限 45°**，超过**拒绝注册**（`SupportSurfaceMath.cs:480`，motor 字段 `:166`）。设计意图见 `:400`–`:413`：注册了会"爬墙"，不注册会"穿过去"，所以要求作者改成 box。
2. **竖直/陡面 = 半径膨胀 AABB 墙**：`SweepMath` 把每个盒扩成 `min−r, max+r`（`SweepMath.cs:130`、`:623`），球心一进就按最近面推出；**没有圆角**，所以盒角是方的（角落会过度阻挡）。
3. **撞墙后的速度处理**：只去掉"朝墙内的水平分量"，切向保留（`SweepMath.cs:415`–`460`）→ **贴墙滑行，不反弹、不爬升**。
4. **竖直方向只有两种来源**：接地法线修正（`GroundContactMath`）与"跨台阶的一次性抬升"（`stepLimit`，`KatamariBallMotor.cs:1096`–`1107`）。**没有跳跃、没有向上速度、没有墙面攀爬**。
5. **不能用高度作弊**：地面/台面判据是"球心 XZ 下的高度"（`SupportSurfaceMath.cs:947`），不是"球体是否与斜面接触"，所以球不能靠"贴墙"获得支撑。

### (b) 实测

- C06（50° 板，`role=box`）：`[ptf] rampProbe cell=C06 refused=1 slope=50.00 reason=ramp slope exceeds the support limit`；`ramp_count=0`；球**全程不动**（球心恒 0.15/0.30/0.55/1.10），`stopped_by_wall blocked_gap=+0.0010` PASS，`no_climb max_bottom_rise=0.0000` PASS。
- 竖直墙：C09/C10 等台阶格的 `stopped_by_wall` 读数一致（球停在膨胀 AABB 外 `r` 处）。
- C05 2.20：球被台面膨胀 AABB 挡在起点附近，`stuck` 在 1.5 s 阈值上反复 → 卡死计时器把球传送回起点（`KatamariBallMotor.cs:1209`–`1216`）。

### (c) 结论

- **没有任何爬墙/爬陡坡能力**，这是当前设计的明确边界：`stepLimit = 0.25D` 以内是"台阶"，之外是"墙"。竖直墙 = 停住 + 贴墙滑；>45° 斜面要么被拒（作者必须补 box 墙），要么以 AABB 墙的形式挡住。
- 对设计者的"假运动"直觉：**贴墙滑行 + 卡死传送 + 支撑面切换时的帧内跳变**这三件事加在一起，很容易被感知成"不是真球"。数据侧最硬的一条是 C13 2.20 的 `0.9499 m` 单帧垂直位移（`support_kind=2`）。

---

## G. 真正的问题清单（按严重度）

| # | 严重度 | 问题 | 证据（代码 / 实测） | 最小下一步 |
|---|---|---|---|---|
| 1 | **P0** | 45° 接缝死锁：C05 在所有球径都爬不上去（2.2 时连坡都到不了） | 几何不变量 `tanθ+1−1/cosθ ≤ 0.5` → θ ≤ 36.87°（本审计计算）；实测 gap 0.0887/0.1763/0.3234/0.6000 vs stepLimit 0.075/0.15/0.275/0.55 | 把接缝验收改成"球空间"：限 36° 或给坡顶加 ≥(gap−stepLimit)/tanθ 的延伸；先改 `TryBakeRamp` 验收 + C05 数据 |
| 2 | **P0** | 斜面只支撑、从不阻挡：侧面/下方/球心偏出坡宽时球直接穿板 | `KatamariBallMotor.cs:947`（扫掠不含 ramp）、`TERRAIN_SUPPORT.md:805`；level 1 坡宽仅 0.35 m | 二选一：给坡加"两端薄边挡板"选项，或至少把坡宽/宽容判据写进关卡校验 |
| 3 | **P1** | 2.2 m 球把 0.6 m 台面当墙：C05 完全不动的卡死循环 | `C05 2.20` 三条 UNEXPECTED，`stuck` 0.42–1.50 循环 | 大球 + 小台面的组合要么禁止，要么把"台面高 < 球半径"按可碾过处理 |
| 4 | **P1** | C13 2.20 单帧 0.95 m 垂直跳变（"假运动"最硬的证据） | `seam_no_step=+0.9499`、`support_kind=2`、`landing_gap=+0.0419` | 用逐帧 `[ptf] traj`（0.02 s）复现一次，确认是支撑切换还是卡死传送 |
| 5 | **P1** | C01 2.20 `climbed deck_gap=+0.0134`（阈值 0.01，差 3.4 mm） | pass2 matrix | 先确认是"几何必然"（接缝公式给 0.0134 就是它）而不是抖动；若是必然，调阈值或调 C01 台面 |
| 6 | **P1** | 三个"体积"今天其实是同一个球，且 `semanticDiameterPerCoreDiameter` 一旦 ≠1 视觉/物理立刻分家 | `SizeModelMath.cs:126`、`BallView.cs:61`、场景 ratio=1 | 加一个调试/HUD 读数同时打 core/collision/semantic；在文档里把 ratio 的语义写死 |
| 7 | **P2** | 32 向表没有物理消费者，16 向只省一次没人用的摘要 | `grep proxySupports` 只有 attach/detach/rebuild/诊断；`lift=0` 全 36 跑 | 先决定这张表要不要留；要留再谈 16 |
| 8 | **P2** | 吸附物随球长大被"埋"但没有策略 | `AttachProp` 保位姿不改 scale（`PickupFieldHost.cs:1013`）；无 burial 代码 | 加"到球心距离 + 尺寸 vs core"判定，隐藏或沿径向推出 |
| 9 | **P2** | `CheckAttachmentRootOnce` 只查距离，不查父节点/scale | `PickupFieldHost.cs:1092`–`1107` | 补父节点与 lossyScale 检查（防止把 AttachmentRoot 挂到 CoreMesh 下） |
| 10 | **P2** | 静态盒烘焙成世界 AABB，旋转丢失；盒角是方的（过度阻挡） | `LevelArenaHost.cs:193`–`196`、`SweepMath.cs:623` | 已知限制，记录在案；有旋转箱需求时再做 OBB |

**给 Lead 的最小动作序列**：① 先做 #4 的 0.02 s 逐帧轨迹（一次 Play，10 分钟，能把"假运动"定性）；② 用 #1 的公式改 `TryBakeRamp` 的接缝验收并把 C05 标成"不可爬"或给坡顶倒角；③ 决定 #2 的产品口径（坡到底要不要挡侧面），这决定关卡数据和 `AddRamp` 接口；④ #3/#5 是数据/阈值层面，可以和 ② 一起做；⑤ #6–#9 是文档/防御性小改，不影响本轮判定。

---

### 附：本审计用到的复算脚本

- `/tmp/ball_audit/ramps.py` → `ramps_out.txt`：从 `placement.json` 复算每条坡的行走面、坡度、足迹、台面接缝与 4 档球径的 gap/stepLimit。
- `/tmp/ball_audit/fib.py` → `fib_out.txt`：Fibonacci 16/32 向的最近邻与覆盖半径。
