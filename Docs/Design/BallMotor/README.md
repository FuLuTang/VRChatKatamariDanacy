# 球体每帧装配（BallMotor）接口契约

> **吸收尺寸门（现行）**：物件体积 `V = k·a·b·c`（`k` 缺省 0.70），门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`。口径与实现清单见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。


**状态：`PROVISIONAL_NOT_TESTED`。** 本文定义各层之间的**数据边界与调用顺序**，并记录装配层唯一自带的交分换算规则与固定容量信号；不定义调参值，也不是对原作行为的声明。世界尺度与公共接口仍未冻结（T06）。

## 为什么需要这一层

球层八个模块各自是纯静态数学，谁都不该知道别人存在。装配层是**唯一**知道顺序的地方，它负责：

- 每帧按固定顺序调用各层，把上一层的输出喂给下一层；
- 持有全部可变状态（位置、旋转、速度、账本数组、代理点数组、远端槽位数组）；
- 作为 UdonSharpBehaviour 存在于场景里，负责与 Unity 的 Transform、时间步和网络回调对接。

规矩：**装配层只做搬运和顺序，不再实现任何数学。** 出现计算逻辑就说明它放错层了。唯一例外是交分缩小：它是本层自己的策略，没有任何球层拥有它，所以以独立的纯静态类 `BankShrinkMath` 存在（见下文），而不是写进 Behaviour 的方法体。

## 每帧顺序

固定步长（`fixedDeltaTime`），顺序不可交换：

| 步 | 层 | 输入 | 输出 | 顺序为什么不能换 |
|---|---|---|---|---|
| 1 | `ControlIntentMath` | 原始输入 + 上帧边沿状态 | 意图序列 | 一切的下游来源；去抖/菜单抑制只在这里发生 |
| 2 | `BallMotionMath` | 意图里的 `Move2D` + 当前速度 | 新速度、请求位移 | 位移决定后面所有几何 |
| 3 | `ContactSolveMath`（经 `StepContactV2`） | 请求位移 + 近集 OBB/平面/地形三角形 + 16 根针的针尖 + 上一步的爬升/恢复状态 | 最终球心、支撑法线/高度、跟随锚点、接触与爬升读数 | **唯一的位置权威**：子步、窗口、最近点推挤、三分类、速度响应、爬升、卡死恢复都在这一步里完成，没有独立的“先扫掠再贴地”。只有最终位置可以用于视觉滚动；吸收判定必须在它之后。设计与规则见 [CONTACT_MODEL_V2.md](CONTACT_MODEL_V2.md) |
| 5 | `PickupAbsorbMath` | 碰撞半径 + 候选物件尺寸/位置 | 吸收/拒绝 + 每件体积 | 必须在位置上定稿之后判，否则会隔空吸到 |
| 6 | `GrowthLedgerMath` | 已确认的吸收事件 | 已确认体积、交分事务 | 只有确认值能进分数；拾取结算在位置定稿后 |
| 7 | `SizeModelMath` | 已确认体积 | 三个尺寸（核心半径/碰撞半径/语义直径） | 尺寸是账本的纯函数，不能累积增长 |
| 8 | `VisualRollMath` | 实际位移 + 地面法线 + 半径 | 新旋转 | 无滑滚动必须用实际位移；传送帧必须跳过 |
| 9 | `FollowRigMath` | 球位置/yaw + 上帧滤波状态 | 跟随架位姿 | 最后算，消费本帧最终位姿 |

远端球（`RemoteProxyMath`）不进主链：它由网络回调驱动，只在渲染阶段消费自己的槽位数组，且**永不**向 1–8 步提供任何输入。

## 边界数据

全部为值类型或无状态调用；数组一律由装配层预分配并复用，任何层都不分配。

| 边界 | 传什么 | 禁止传什么 |
|---|---|---|
| 1→2 | `Vector2 move`（已钳位）、帧时间戳 | 原始按键、菜单状态 |
| 2→3 | `Vector3 requestedPlanarDisplacement`（米） | 速度、意图 |
| 3→4 | 扫掠后的 `Vector3 center`、是否卡住 | 请求位移 |
| 4→5 | 最终 `center`、`collisionRadiusWorldM` | 语义直径、核心半径 |
| 5→6 | 每件 `absorbedVolumeUnits` + 事件标识 | 位置、模型尺寸 |
| 6→7 | `confirmedVolumeUnits`（累计、已确认） | 未确认/待处理值 |
| 6→分数 | 只经交分事务 | 任何未经确认的增长 |
| 7→渲染 | 三个尺寸**分别**使用 | 一个字段兼表半径与直径 |
| 8 | **实际**位移、单位法线、核心半径 | 请求位移、速度 |

## 不变量（装配层必须保证）

1. **一份状态一份所有者**：位置、旋转、速度、账本只由装配层持有；各层拿不到写权限。
2. **尺寸不累积**：`coreRadiusWorldM` 每帧由账本重算，不允许 `+=`。
3. **只有确认值进游戏**：交分、分数、PvP 判定读账本的确认值；待处理事件不参与。
4. **远端只影响外观**：远端球的半径、装饰、位置不得参与本地碰撞、吸收或分数。
5. **失败即不应用**：任何 `Try*` 返回 false 时，本帧**保持上一帧状态**，不得用零值或部分结果。
6. **一帧一次**：每帧对每个模块只调用一次，避免同一状态被算两遍。
7. **跳变跳过视觉**：传送/重生帧把 `discontinuous = true` 传进 `VisualRollMath`，只改位姿不加旋转。

## 组合层实现说明

实现是一个 UdonSharp Behaviour：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs`（命名空间 `VRCKatamari.BallMotor`）。它只持有状态、按上表顺序调用、在失败时保持上一帧。唯一的规则算术是交分缩小：没有任何球层拥有“交分换算”这一策略，所以它被放在同目录的纯静态类 `BankShrinkMath.cs`（同一命名空间、无状态、可单测），行为类只调用它并据结果决定是否动账本；除此之外本层不实现玩法公式。

### 它持有的状态

单球标量（不是并行数组）。本版**只支持一个本地球**：多球需要把下列标量改成同长度并行数组，并在 `StepBall` 外面套一层下标，本版未做。

| 字段 | 用途 | 只由谁写 |
|---|---|---|
| `headingDegrees` | 平面朝向，喂 `BallMotionMath` 与 `FollowRigMath` | 第 2 步成功时 |
| `planarVelocity` | XZ 速度（y 恒 0） | 第 2 步成功时 |
| `ballCenterWorld` | 球心 | 每步末尾提交（各步只读） |
| `coreRadiusWorldM` / `collisionRadiusWorldM` / `semanticDiameterWorldM` | 三个尺寸，分开存 | 第 7 步成功时 |
| `sizeScale`（`CurrentSizeScale()`，不存字段） | 当前语义直径 / 起始语义直径（D/D0），喂运动层、爬升门与下坡重力 | 第 7 步成功时 |
| `unbankedVolumeUnits` | 账本确认体积（可交分） | 账本提交/交分成功后的**读回值** |
| `carriedVolumeUnits` | 交分后留下的、已计分的残余体积（只喂尺寸，永不再交分） | 只由交分成功时 |
| `confirmedVolumeUnits` | 尺寸驱动量 = 未交存 + 残余；只由确认来源相加，不累积 | 只由 `RecomputeConfirmedAndSize()` 写（第 7 步与交分成功时） |
| `overflowCount` / `firstOverflowReason` / `contactBufferSaturated` | 固定容量溢出信号（见“固定容量与溢出信号”），不是玩法状态 | 注册被拒/接触缓冲满时写一次 |
| `bankedScoreUnits` | 已交存分数 | 账本提交/交分成功后的读回值 |
| `fixedStepAccumulator` / `localClockSeconds` | 定步长累积器 / 本地时钟 | `Update` |
| `stuckSeconds` | 卡住计时 | 扫掠成功时按 `stuck` 累加或清零 |
| `discontinuous` | 传送/重生标记，喂 `VisualRollMath` 与 `FollowRigMath` | `TeleportTo`、卡住恢复；每步末尾消费后清零 |
| `visualRollRotation` | 视觉滚动位姿 | 第 8 步成功时 |
| `followRigWorldPosition` / `followRigYawDegrees` / `followRigHeightMeters` / `hasFollowRigFrame` | 跟随架上一帧滤波状态（`followRigHeightMeters` 是相对球心的竖向偏移，不是眼高常量） | 第 9 步成功时 |
| `proxyDirections` / `proxySupports` / `proxyEndpointPool` / `proxyItem*` / `proxyCandidateKeys` | 径向代理外壳（16 根针）：16 个固定方向、16 个端点支持长度、每件已吸附物 8 个 AABB 角点的账本（容量 256 件）；不再有长物件候选（`proxyCandidateKeys` 长度 0） | `Start` 一次分配；`AttachRadialProxy` / `DetachRadialProxy` / `ClearRadialProxies` / `RebuildRadialProxies`；第 4 步只读 |

另有账本列（单行、单回合）、输入意图缓冲、扫掠缓冲区、待处理吸收候选队列；都是预分配、复用、不 per-step 分配的字段，不是玩法状态。

### 固定步长策略

`Update` 把 `Time.deltaTime` 累积进 `fixedStepAccumulator`，每次消费 `fixedStepSeconds` 就调一次 `StepBall`；积压先夹到 `fixedStepSeconds * maxStepsPerFrame`，超出的时间**丢弃**，不让一次卡顿变成一串追赶步。所有玩法状态按步推进，Transform 每渲染帧写一次。

- `fixedStepSeconds = 0.02f`（50 Hz）、`maxStepsPerFrame = 5`：**临时值，不是实测固定频率**，与上面“未定项”里固定步长具体值仍未决一致。Unity 侧最终应改用 `FixedUpdate` 或可配置频率。
- 时钟由本地累积的 `Time.deltaTime` 提供，不读 `Time.time`，也不读网络时间；因此同一起始状态与同一 `deltaTime` 序列可复现，跨平台逐位一致不承诺。

### 调试输入的决定

**用的是 `[SerializeField]` 调试轴（`debugForward` / `debugTurn` / `debugLateral` / `debugMenuOpen`），不是 `VRCInput` / `InputManager`。** 理由：

1. Linux 检出里 `Packages/` 只有 manifest 三件套，VPM 包源不在仓库（按仓库规则也不提交），所以**无法在本检出确认** Worlds SDK 3.10.5 是否提供 `VRCInput`；整个运行时代码也没有一处用过它。
2. 输入层自己的契约就是“不读设备”：`ControlIntentMath` 只吃规范帧，不读 Unity 输入、不读 `VRCPlayerApi`。所以设备读取本来就该在适配层，不该在组合层。
3. 因此本层保留 `debugForward/Turn/Lateral` 与 `SetMoveAxes(forward, turn, lateral)`、`SetMenuOpen(bool)` 作为适配层接缝；适配层写这三个值，本层仍照常跑第 1 步（`TryProcess` 负责钳位、菜单抑制、边沿去抖）。

轴约定是**临时的适配约定**：设备 y → forward、x → lateral、单独的 turn 轴 → turn；菜单打开时全零（等于制动）。没有任何一层把设备轴映射成世界方向，所以“相机/rig yaw 基准 + 轴对 → forward/turn/lateral”这一步目前**没有归属层**，见下面的缺口。

### 每步代码路径

```
Update: 累积 deltaTime → 夹积压 → while(够一步且未到 maxSteps) StepBall(fixedStepSeconds) → 写 Transform
StepBall:
  1 StepInput()                 ControlIntentMath.TryProcess → Move2D 的 y/x + turn 轴
  2 BallMotionMath.TryStep      → heading、planarVelocity、requestedDisplacement（成功才赋值）
  2.5–4 StepContactV2()         → groundedCenter、followCenter、supportNormal（近集窗口 → ContactSolveMath.TryStep
                                  → 针尖 pass → 支撑/爬升写回 → 卡死恢复状态机；失败即保持上一位置并去速）
  5 PickupAbsorbMath.EvaluateAbsorb（对队列里每个候选，用最终位置）
  6 GrowthLedgerMath.TrySubmitClaim + TryResolveClaims（成功才读回体积）
  6.5 ApplyGrowthPushOut        （长大后嵌入墙里的球推出去，≤2 次 × ≤5 cm，失败整段不应用）
  7 RecomputeConfirmedAndSize   → 体积恒等式（未交存 + 残余）→ SizeModelMath.TryDerive
                                → 三个尺寸（成功才赋值；运动层的尺度由 `CurrentSizeScale()` 现算）
  8 VisualRollMath.TryAdvanceRotation（实际位移 + 本步 discontinuous）
  9 FollowRigMath.TryUpdate（本步最终位姿，只回表现层）
  提交 ballCenterWorld，清 discontinuous
```

失败即不应用：每个字段的赋值都写在对应 `Try*` 返回 true 的分支里；尺寸每步**重算**而不是累加，所以“失败不涨体积”是结构保证；吸收值只有在账本提交（claim → resolve）成功后才会进 `unbankedVolumeUnits`；传送/卡住恢复只置 `discontinuous`，不产生滚动。体积恒等式 `confirmed = unbanked + carried` 只在 `RecomputeConfirmedAndSize()` 里写一次，第 7 步与交分成功都走它，所以两条路径不可能对“确认体积”各有一套加法。

### 交分（已定规则 (a)/(b)，在直径空间实现）

`TryBank(now, requestId)` 的顺序是**先算计划、再动账本**：一次失败的缩小不会留下“已加分但没缩小”的半状态。

1. 前置拒绝：无本地行、`requestId` 为空、`now` / 当前语义直径非有限、球径低于 `minConversionDiameterWorldM`（默认 `0.55f`，是 (b) 的项目暂定门槛）、未交存确认体积为 0。
2. `startDiameter` 取 `SizeModelMath.TryDerive(0f, …)` 的语义直径，也就是**同一张前向映射在零确认体积时的输出**（默认参数下 **0.29974 m**），不写死 0.30。规则里的 `start` 必须和体积映射同源，否则改一次尺寸参数，缩小公式和球的真实起始尺寸就会对不上。
3. 残差在**直径**上算：`post = max(start, start + (pre − start) × residual)`，`residual` 夹到 [0,1]；`pre` 是本球当前语义直径。残差 0 等价于旧的“缩回起始”，1 等价于不缩。
4. `post` 经 `SizeModelMath.TryVolumeUnitsForSemanticDiameter` 换回确认体积单位；**反函数失败就拒绝交分**（此时账本还没动），绝不应用 NaN 或负数。
5. 全额过 `GrowthLedgerMath.TryBank`（只有 Playing、Active、`amount <= confirmed`、`(player, requestId)` 未重放才成功）。
6. 读回 `unbanked` / `banked`，`carried = max(0, postUnits − 读回未交存)`（通常读回未交存为 0），这部分体积已经计过分、永不再交分；最后 `RecomputeConfirmedAndSize()` 重算**一次**确认总量与尺寸。

数值（当前标定 3.4390244 基线单位 / 0.0041 m³ 每单位 / 2.20 m 标定参考直径——设计梯子顶，不是运行时上限，运行时上限是全局 400 m）：1.49 m 约 419 确认单位，交分后 **0.5973 m**、约 23.8 单位；若按决定文档的 0.30 起始写则正好 0.5975 m。旧的体积空间写法在同一输入下留下 0.9537 m。**两个空间是立方关系，不是比例**，所以这个差别是结构性的，调比例救不回来。

还会出错的地方：

- `pre` 取当前语义直径，显示上限生效时它就是 2.20 m：从上限交分会按上限缩到约 0.78 m，而账本仍按未交存全额入分。这是“上限只作用于显示”的已知后果，等 2.20 m 真正可达时要复核。
- `bankResidualFraction` 配成 NaN 时现在直接拒绝交分（旧实现会当成 0）。这是有意的“不应用非有限值”，但配置错误会表现为“交分没反应”，诊断时先看这里。
- 缩小比例与最小门槛仍是项目暂定值，不是原作数据；改它们只需要改字段，不需要改本节的数学。

### 固定容量与溢出信号

注册类缓冲区是固定容量：`MaxPlaneCount = 8`、`MaxBoxCount = 64`、`PendingCandidateCapacity = 4`，加上扫掠每步最多 `ContactCapacity = 4` 个接触点。超出容量的条目**被拒绝并返回 false + latch 原因**（不是静默丢弃），不排队、不扩容、不降级，所以：

- 障碍超出会改变球实际能撞到什么；
- 候选队列超出会静默丢掉一个本可吸收的物件。

这两者都是**数据错误**（场景接错，或宿主每步塞了超过 4 个候选），不是玩法取舍。因此这里不设计“满了怎么办”的玩法规则，只留信号：

- `AddPlane` / `AddBox` / `SubmitCandidate` 因容量被拒时调用 `SignalOverflow()`：`overflowCount` 自增、`firstOverflowReason` 只记第一次；`HasOverflowSignal()` / `GetOverflowCount()` / `GetFirstOverflowReason()` 供诊断读取。不写日志、不每帧输出。
- 接触求解没有会静默截断的固定缓冲：近集窗口溢出由 `contactIncomplete` 锁存并打一条日志（CONTACT_MODEL_V2 §5.4）。

读取时机是检查点（场景装载后、调参后），不是每帧轮询。

### 径向凹凸代理（16 根针）

每件吸附物的 AABB 角点在 16 个固定方向上取 max，得到 16 根针的支持长度（原作滚动模型 `Mr` 的 16 根针）。**针尖是接触求解的一等输入**：`StepContactV2` 里 `PrepareNeedleShape` 把每根针夹到 `[r, r + cap]`（`cap = min(0.35·r, 0.30 m)`），按段扫掠与近集求交，推挤、去速、支撑（`supportKind = 5`）、爬升高度的突出项都读它；镜头锚点是球心本身，不被针尖抬高。水平碰撞的主体仍是球形碰撞半径，PVP 外形摘要同样只用它。

**归属。** 外壳状态在 motor 里——它持有球位姿；16 方向、16 支持长度、256 件 × 8 角点的端点账本都在 `Start` 一次分配、复用；每次吸附只把新件的 8 个角点对 16 个方向点积并取 max，只重算受影响方向；移除/重建走 `RadialProxyShellMath`。

**数据流。** 吸收确认（账本 commit）→ 宿主 `AttachProp` 写球局部位姿 → `AttachRadialProxy` → 8 个角点进端点池、只重算受影响方向、登记候选键 → 每步 `StepContactV2` 读 16 个支持长度。

**准入（序列化字段，PROVISIONAL）**：`minProxyProtrusionM = 0.01 m`：吸附时最远角点连 `core + 1 cm` 都不到就不登记（对支撑没有贡献）。突出上限不再是序列化字段，来自 `ContactTuning`。

**验证。** 纯逻辑测试在 `Tests/EditMode/BallLayers/RadialProxyShellTests.cs`：单根长棍的支持长度与上限、移除后回落、两件共享方向去掉一件不误删另一件、核心长大后原始突出收回、增量与整账本重建等价、容量满时拒绝且不改旧壳。

### 支撑与台阶

球的惯性（松手滑行、起步、刹车、转向漂移）的现状读数与重做设计见 [BALL_INERTIA.md](BALL_INERTIA.md)；原作证据见 [ORIGINAL_BALL_INERTIA](../../Research/KatamariBallRules/ORIGINAL_BALL_INERTIA.md)。

支撑由接触求解选出（`WriteContactSupport`：最高的 RIDE 接触，针尖支撑 = 5，爬升接触 = 4，无支撑 = 下落）。台阶没有阈值字段：台阶高 `h < r` 天然滚过，`h < D` 由爬升通道处理（CONTACT_MODEL_V2 §2.7、§3）。`SupportSurfaceMath` 只留共享的 surface id、`stepHeightOverBallDiameter` 的步高上限（0.25 × 语义直径，宿主据此给道具分挡路/可站两种角色）、斜面校验/烘焙（`AddRamp` 的 45° 作者门）。设计与取舍见 [TERRAIN_SUPPORT.md](TERRAIN_SUPPORT.md)。

| 字段 | 默认 | 作用 |
|---|---:|---|
| `stepHeightOverBallDiameter` | **0.25** | 比例，不是高度：`stepLimit = 0.25 × 语义直径`（数据/校验器/关卡门同一口径；诊断读数与道具角色分类读它） |
| `maxSupportSlopeDegrees` | **45°** | `AddRamp` 的坡度护栏；超过则带原因码拒绝（arena 走 `AddObb`，不经过它） |
| `terrainMeshContactEnabled` | false | 地形网格接触；`TerrainFieldHost` 装配地形后打开 |

**公开方法**：`SetGroundPlane(n, o)`、`GetStepHeightLimitWorldM()`、`GetSupportSurfaceHeightWorldM()`（**支撑面**高度）、`GetSupportNormalWorld()`、`GetSupportSurfaceCount()`（当步接触条数）、`GetSupportSurfaceId()`、`GetSupportKind()`、`IsFalling()`、`AddRamp` / `GetRampCount` 及其拒绝读数。

**关卡登记**：`Runtime/Story/LevelArenaHost.cs` 引用场景里的 `BoxCollider`（台面/台阶/斜板）与墙的 `Transform`（局部 +Z 指向场内），`Setup` 时**烘焙一次**：墙为平面 `(n, offset)`，盒与斜板为 8 角点真 OBB（`AddObb`），之后不再读 Collider。


### 跟随架（PROVISIONAL 数值）

跟随架是**表现层**，也是玩家坐姿的落点：`VRCStation` 挂在 `followRigRoot` 下面，它指向哪里，玩家就在哪里。它只读球的位置、朝向与半径，不写回任何玩法状态。

**这是眼点，不是座位**：上面的式子给的是**眼睛**该在哪，不是**座位**该在哪。玩家由 `VRCStation` 承载，station 落的是 avatar 的**根**而不是眼睛；把座位直接摆在眼点上，真实相机（avatar 自己的头部相机，场景里没有 Camera 对象）就会再高出一个坐姿眼高（因 avatar 而异，通常 0.4–0.9 m）——试玩反馈的"坐下后头的位置偏高"就是这一条。所以座位由**骑乘层**按实测眼高反推：`Runtime/Riding/SeatEyeCompensation` 在本地玩家坐下后的稳定窗口里实测 `GetTrackingData(Head)` 相对入座点的偏移，再把入座点按 `−inverse(R)·d` 摆到"头正好落在眼点"的位置（R 是跟随架 yaw，d 是实测偏移）；motor 与 `FollowRigMath` 都不知道这件事，公式仍然只负责眼点。偏移测一次就冻结（VR 下头部会随真实头动，不能每帧拉回），离座、重生、换 avatar 时复位重测。补偿量落在两条护栏之内：① 合理性上限 `SeatEyeMath.MaxPlausibleHeadAboveSeatMeters = 1.35 m`，实测头距超过它就整条拒绝（`reason=6`）而不是减半补偿——它是防「读到明显错误的数据（站姿眼高而相机并不在那里）」的护栏，不是产品值：ClientSim 实测 **1.204159 m**（2026-11-15，那里的相机确实就在那个高度）、真机坐姿约 **0.85 m**，1.35 给最大值留了余量、又仍在站姿眼高 1.5–1.8 m 之下；② 竖向钳位 `verticalLimitMeters = 1.3 m`（2026-11-15 定），与 1.35 的合理性上限配套：它必须高于本仓实测到的最高的头（ClientSim **1.204159 m**），否则真实测出来的一米二会被削掉、相机照样偏高——1.3 让这条测量**完整补偿**（`uncompensatedMeters = 0`）；超出的部分不补偿并计入 `uncompensatedMeters`（例：1.40 m 的头剩 0.10 m）。代价只落在第三人称：avatar 下半身更深地没入地面，本地玩家自己的视角不受影响。

**为什么是「球后方的 station」而不是摄像机**（这条必须留着，别让后来的人以为方案选错了）：VRChat **没有任何 API 能连续移动玩家 avatar**，唯一手段是 `VRCStation`（或传送）。所以「人推球」在这台引擎上只能实现成**摆在球后方的一个 station**：第一人称下玩家看到的正是「球在身前、视角在球后上方」，与推球一致；但第三人称会看到 avatar 是坐姿——这是引擎限制，不是设计取舍。球的**位置**仍然只由 motor 决定，座位只是它的观察点。

取景模型（v0.3 用户拍板，还原《块魂》：球一开始就很小，视角在球**后面**，球越大视角越高）：

```
eye = ballCenter − forward(yawFiltered) * (eyeBaseBackMeters + radius * eyeBackPerRadius)
                + up                    * (eyeBaseUpMeters   + radius * eyeUpPerRadius)
```

即**后方 + 上方，两项都随半径线性放大**，不再有「球心正上方 + 常量眼高」，也不再有「离球顶」的中间量。旧语义 `y = 球心 + 1.6` 的净空是 `1.6 − r`（线性下滑：0.30 m 球 1.45 m、1.43 m 球 0.885 m），而吸收物站在球面上、约一个物件深（门槛是「物件最大边 ≤ 球半径」），球越大越往里挤——这就是 1.43 m 时画面被糊满的几何原因。

| 直径 | 半径 | 上方（离**球心**） | 后退 | 到球心 | 到**球面** | 到地面（球落地时） | 球角半径 | 平视时「球+壳」上沿 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 0.10 m | 0.050 | 0.670 m | 0.860 m | 1.09 m | 1.04 m | 0.72 m | 2.6° | 视轴下 32.7° |
| 0.30 m | 0.150 | 0.910 m | 1.080 m | 1.41 m | 1.26 m | 1.06 m | 6.1° | 27.9° |
| 0.72 m | 0.360 | 1.414 m | 1.542 m | 2.09 m | 1.73 m | 1.77 m | 9.9° | 22.4° |
| 1.43 m | 0.715 | 2.266 m | 2.323 m | 3.25 m | 2.53 m | 2.98 m | 12.7° | 20.4° |

上表是 motor 字段的**默认值**（0.55/2.4/0.75/2.2）的几何。故事场景里这四个字段由 `RideUxAssembler` 按 `RideViewMath` 写成 0 / 1.654625 / 0 / 1.176792（上/后底座为 0，两项都正比于半径：站姿手推球的眼点，推导见 [RIDE_GEOMETRY](../BallLayers/FollowRig/RIDE_GEOMETRY.md)），任何直径下平视时球心在视轴下 54.6°、球半角 29.5°、球上沿在视轴下 25.1°。

净空（上方 − 半径）随球**变大**：0.62 / 0.76 / 1.05 / 1.55 m。

**为什么水平视角（pitch 由玩家鼠标控制）能看见前方地面**：上表最后一列是「球+吸附壳」上沿在视轴下方的角度。壳 = 半径 + min(0.6 m, 半径)：吸收门槛允许吃掉和球一样大的物件（最深可到半径），0.6 m 是场景最大道具尺度的上界估计。四个尺寸的上沿都在 **20.4°–32.7°**，所以平视时球和吸附物只占画面下缘（60° 垂直 FOV 的下半屏），上半屏和地平线以上是空的，前方地面从球的两侧一直可见。球本身也始终在画内：球心在视轴下 37.9° / 40.1° / 42.5° / 44.3°，比上沿更深。球径小的时候视角也低（0.10 m 球时眼点离地 0.72 m），**没有把下限写死在人性尺度上**；同时眼点始终在球顶之上（0.62 m 起），不会贴地/穿地。

护栏（不是调参）：`eyeUpMinMeters = 0.1` / `eyeUpMaxMeters = 8` 钳的是**离球心**的高度，`eyeMaxBackM = 8` 钳后退。默认值永远碰不到它们——2.2 m 的球（半径 1.1）也只有 3.19 m，旧方案里那个绝对值 3 m 的天花板会把它削平，所以上下限必须跟着半径走。

滤波不变：x/z 与高度用 `heightFilterHz`（5 Hz）、yaw 用 `yawFilterHz`（8 Hz），alpha = `1 − exp(−2π·hz·dt)`；传送/卡住恢复靠 `discontinuous` 一次吸附。**喂进来的中心是第 4 步空代理数组解出的核心球心**（见上一节），代理凹凸不进入这条高度通道，所以低通只需要处理正常地面/转向，不再需要独自压住长棍的起伏。后退偏移沿**滤波后的 yaw**取，所以偏移方向和写进 transform 的朝向不会打架。代价照旧要说清：后退偏移也是转向时座位横移的半径——110°/s 满转在 2.3 m 后退下约 4.4 m/s；觉得像「侧滑」就降 `eyeBackPerRadius`。

新增的序列化字段（改名/新增都要 Unity 侧重接，见 [Labs/README.md](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/README.md>) 的实例变量表说明）：

| 字段 | 类型 | 默认 | 语义 |
|---|---|---:|---|
| `eyeBaseUpMeters` | float | 0.55 m | 眼点高于球心的常量项 |
| `eyeUpPerRadius` | float | 2.4 | 每米半径再抬高多少米 |
| `eyeUpMinMeters` | float | 0.1 m | 护栏：离球心高度的下限 |
| `eyeUpMaxMeters` | float | 8 m | 护栏：离球心高度的上限 |
| `eyeBaseBackMeters` | float | 0.75 m | 球心后方多少米（常量） |
| `eyeBackPerRadius` | float | 2.2 | 每米半径再后退多少米 |
| `eyeMaxBackM` | float | 8 m | 护栏：后退上限 |

眼点由 7 个系数决定，没有离球心常量眼高的字段。模型 `Docs/Design/BallLayers/FollowRig/follow_rig.py` 的 `try_update(...)` 与 C# `TryUpdate` 同名同序（含调用方持有的上一帧状态），7 个眼点系数是每次调用的参数；测试在 `Docs/tests/ball_follow_rig/` 与 `Docs/tests/ball_visual_roll/test_presentation_cross_layer_simulation.py`。改这里的式子，C# 与 Python 必须同一提交。

### 推球方向浮标（纯视觉，PROVISIONAL）

`Runtime/BallLayers/PushCue/` 的 `PushDirectionCue` 在球前方浮一个方向小箭头，只读本层与 `BallRideStation` 的公开接口：位置 = 球心 + `forward(跟随架 yaw) × (半径 + 间距)`，高度 = 球心 + `半径 + 悬空量`（即贴着球顶上方浮着），朝向 = 跟随架 yaw，大小 = `基准长度 + 直径 × 系数`（夹在护栏内），再叠一个 0.25 Hz / 2.5 cm 的上下浮动；只在本地玩家坐在球上、且（接了控制器时）回合状态为 `StateRunning` 时显示。**方向取跟随架 yaw 而不是球速**：静止时球速为零，而 yaw 就是座位朝向，指示物与座位不会打架。间距/高度/缩放都随**语义直径**线性放大（直径 0.30 m → 球心前方 0.635 m、离地 0.38 m、长 0.22 m；2.00 m → 2.25 m、2.08 m、0.90 m），因此各尺寸下浮标都落在骑士视轴下方约 20°、球轮廓之外；贴地版本在球长大后会被球自己挡住，所以高度从球顶量起。纯表现：不进候选、不登记碰撞/遮挡数组、不参与任何玩法。公式在 `PushCueMath`，测试在 `Tests/EditMode/BallLayers/PushCueTests.cs`。

### 刻意不在这里

- 网络、远端球代理、多客户端权威/抢单仲裁：本层只有**一个本地玩家行**的账本；远端提交应走 `GrowthLedgerMath.TryApplyCommit` 的快照/提交路径，由回合控制器拥有。
- 回合状态机本体：`TryBeginPlaying` 只是把 Lobby→Ready→Preparing→Playing 三条合法边转发给账本，没有计时、轮换或结算。
- HUD、尺寸读数、音效、粒子、道具生命周期、物品注册表、空间索引（候选由宿主/适配层收集后 `SubmitCandidate` 送入）。
- 径向代理：针尖进入接触求解（CONTACT_MODEL_V2 §4A.3）；挡路盒、PVP 外形摘要仍然只用球形碰撞半径。代理不写 `RemoteProxyMath`，也不进任何分数/账本。
- 场景资产、prefab、新 asmdef：都没加；U# 程序集仍是 `VRCKatamari.Runtime`（`BankShrinkMath.cs` 与其 `.meta` 是运行时代码文件，不新增程序集）。

### Unity 操作者还要接线

1. 建 prefab：`BallPhysicsRoot`（本层只写 position）、其下的 `BallVisualRollRoot`（只写 rotation）、独立的 `FollowRig`，把三个 Transform 挂到 `ballPhysicsRoot` / `ballVisualRollRoot` / `followRigRoot`。
2. 场景：`startCenterWorld`（同时是卡住恢复点）由本层给；**地面/墙/静态盒改由 `LevelArenaHost` 登记**（`SetGroundPlane` + `AddPlane` + `AddBox`，box 的 bounds 在 Setup 烘焙）。旧 lab 若保留 `AddBox` 行为，显式 `SetTerrainSupportEnabled(false)`。
3. 玩家 rig：`FollowRig` 挂到 Station/玩家参考系；本层只给位置 + yaw，不碰 HMD 姿态。
4. 输入：要么在 Inspector 拖 `debugForward/Turn/Lateral` 做桌面预览，要么由适配层每帧调 `SetMoveAxes` / `SetMenuOpen`；若要接 `VRCInput`，必须先确认 SDK 里确实有该 API（本检出无法确认）。
5. 吸收：候选收集器（触发器/空间结构）填 `SubmitCandidate(...)`，并用 `SetAllowedKinds` 提供允许的种类；**默认空集合 = 全部拒绝**，这是有意的：种类是世界数据，不在本层编。`PickupFieldHost` 在 `AttachProp` 里已经接了 `AttachRadialProxy`，`ResetField` 里已经接了 `ClearRadialProxies`；换宿主时这两个调用是必须的（否则球永远只有核心球壳）。
6. 交分：兑换区触发器在停留满足后调 `TryBank(now, requestId)`，`requestId` 必须每次交分唯一（重放的 id 会被账本拒绝）。
7. 关卡开始：`autoStartPlaying` 为真时 Start 已自动进 Playing；正式回合应由回合控制器调 `TryBeginPlaying`。
8. Unity 侧验收：先 `refresh_unity` 让 U# 全量编译，再 `run_tests`；本条与“验收”一节同义，本文件写作者不是 Unity 操作者。

### 临时数值（都需试玩/Unity 侧确认）

| 字段 | 默认 | 来源 |
|---|---:|---|
| `fixedStepSeconds` / `maxStepsPerFrame` | 0.02 s / 5 | 临时值；上面“未定项”未决 |
| `bankResidualFraction` | 0.25 | `Docs/Design/Gameplay/README.md` 的反推暂定值；`OPEN_DECISIONS.md` (a) |
| `minConversionDiameterWorldM` | 0.55 | `Docs/Design/Gameplay/README.md` 的 t1 档下限；`OPEN_DECISIONS.md` (b) |
| `maxSpeedMps` / `accelerationMps2` / `brakingMps2`（D0 下，× D/D0） | 4 / 7.5 / 6 | `BALL_INERTIA.md`，PROVISIONAL |
| `rollingResistanceMps2`（D0 下，× D/D0） / `maxLateralMps`（× D/D0） | 1.3 / 1.2 | 同上 |
| `frictionPerSecond` / `lateralGripPerSecond` / `turnRateDegreesPerSecond`（时间型，不缩放） | 0.9 / 2.0 / 110 | 同上 |
| `baselineVolumeUnits` / `cubicMetersPerVolumeUnit` / `maxCoreRadiusWorldM` | 3.4390244 / 0.0041 / 1.1 | 与 `round_model.py` 对齐：起始体积 `3.4390244 × 0.0041 = 0.0141 m³`（0.29974 m），满径 2.20 m 需约 1356.4 确认单位；未测量 |
| `eyeBaseUpMeters` / `eyeUpPerRadius` / `eyeUpMinMeters` / `eyeUpMaxMeters` | 0.55 m / 2.4 / 0.1 m / 8 m | **PROVISIONAL**：眼点 = 球心 +（常量 + 半径×系数），上下限是离球心的护栏；见下「跟随架」 |
| `eyeBaseBackMeters` / `eyeBackPerRadius` / `eyeMaxBackM` | 0.75 m / 2.2 / 8 m | 同上：球心后方（常量 + 半径×系数），上限是护栏 |
| `contactRadiusWorldM` | 0 | 决定 (i)：接触半径为 0（体积门见文首） |
| `maxRollDisplacementM` | 0.5 | 本层临时，非实测容差 |
| `rampTangentialGravityScale` / `rampRestSpeedMps` / `rampRestSeconds` / `rampSlideMinSlopeDegrees` / `gravityMps2` | 1 / 0.05 / 0.3 / 2 / 9.81 | 静止后下坡（`StepContactV2` 里的 `UpdateRampSlide`），见 `BALL_INERTIA.md` §4 |
| `minProxyProtrusionM` | 0.01 m | 最小可贡献突出：最远角点没越过核心这么多的件不登记；PROVISIONAL。每件吸附物都登记 |
| `stuckTimeoutSeconds` | 1.5 | 本层临时恢复策略 |
| `stepHeightOverBallDiameter` | 0.25 | 台阶比例（与 `layout.json` 同源）；PROVISIONAL |
| `maxSupportSlopeDegrees` | 45° | P3 斜面坡度护栏（PROVISIONAL；硬上限 51.3°） |

### 缺口（数学层没提供、本层没有自己编）

直径 → 体积的反函数由 `SizeModelMath.TryVolumeUnitsForSemanticDiameter` 提供，交分因此在直径空间做（见上一节）。下面是本层的已知缺口；编号被其它文档按“缺口 N”引用，故保持不变。

1. **设备轴 → forward/turn/lateral 的映射没有归属层。** `ControlIntentMath` 只给设备轴对，`BallMotionMath` 只吃三个标量，中间这一步（含相机/rig yaw 基准）目前是适配层约定，本层用 y→forward、x→lateral 顶着。
2. **墙面速度响应**：由 `ContactSolveMath` 的三分类规则提供（RIDE 不去速、CLIMB 去水平法向、NONE 去三维法向），规则见 CONTACT_MODEL_V2 §2。
3. **吸收的相对球径门槛**：由吸收尺寸体积门判定，配方不在本层；现行口径见 `Docs/Design/PickupAbsorb/README.md` 1.1 节与文首指向的 ABSORB_GATE_ORIGINAL。
4. **`growthFraction` 没有 helper。** 设计文档把它指派给组合层（`BallMotion` README 的“唯一回灌量”），`BallMotionMath` 也明说不做半径映射，所以本层只有这一处 `(r-base)/(max-base)` 夹取；若要复用应下沉到 `SizeModelMath`。
5. **账本提交存储是有容量的**（本地 256 条/回合）。满了以后 `TryResolveClaims` 拒绝、候选留在队列里不再增长；正式回合应由拥有账本的控制器扩容或换权威提交。
6. **单球、单本地玩家行**：多球/多人要并行数组 + 多行账本，本版没有。

### 墙面速度响应

撞墙后的速度只去掉指向墙里的分量、切向保留（滑墙），分类与规则由 `ContactSolveMath` 拥有，装配层只调用 `StepContactV2`，不重算几何。刻意不做摩擦、不做反弹：原作墙面响应没有可靠证据（PROVISIONAL）。

## 未定项（等 T06 与试玩）

- 固定步长具体值、每帧最多多少个吸收事件、代理点数组容量、远端槽位容量。
- 一次固定步内的子步数由接触求解决定（`MaxSubSteps`），装配层不重复扫掠。
- 交分事务的触发方式（靠近/输入/自动）——由回合状态机决定，不在本层。
- 舒适转向与移动权威（头/手/摇杆）如何合成 yaw——`FollowRigMath` 只吃一个标量 yaw，谁产它未定。

## 起始球径是每关参数

| 字段 | 默认 | 含义 |
|---|---:|---|
| `startDiameterWorldM` | 0 | 0 = **未设**：起始尺寸仍由 `baselineVolumeUnits` 决定（既有场景，0.2997 m）。>0 = 该关的**语义**起始直径（米），基线体积由它反推 |

- **为什么有这个字段**：`level.ballDiameterStart` 过去只到导入器的弯坡弦预算，引擎侧没有任何「起始直径」入口，于是 5 cm 关也从 0.30 m 起步（原作 MAS1 是 5 cm、Cancer 22.2 cm；owner 决定 D6：起始/目标/上限都是每关参数）。
- **推导只有一处**：`ApplyStartDiameterWorldM()`，在 `Start` 里把 `baselineVolumeUnits` 改写成 `(4/3)πr³ / cubicMetersPerVolumeUnit`（`r = 起始直径/2/semanticDiameterPerCoreDiameter`），随后立刻用 `SizeModelMath.TryDerive(0, …)` 反查校验；反查失败（超过 `maxCoreRadiusWorldM`）或往返不一致就**保留原基线**并打一条 warning——不会留下「0 m 的球」。
- **只有基线变，单位不变**：连续体积未批准（PARITY_AUDIT §5.3），账本仍是整数单位；成长、重试、兑换、体积门都读同一个基线，所以 0.05 m 关的整条「确认单位 → 直径」曲线整体缩放。`1356 单位到 2.20 m` 那条标定只对 0.2997 m 基线成立，换起始球径的关卡需要自己的目标/上限（同一份 `level{}` 数据）。
- **接线**：关卡场景把该值设成 `placement.json level.ballDiameterStart`；导入器不写球，只在报告里提醒（`Docs/Design/Tools/LEVEL_AUTHORING.md` §7.9）。旧的 `Start ready` 日志行现在多两个读数：`baselineUnits=` 与 `startSize=level|calibration`。

## 跨 Behaviour 写入的集合缓冲：所有权

**问题形态**：`PickupFieldHost` 在自己的 `Update` 里把候选推给 motor，而 motor 的 `Start` 不是先跑的。Unity 不保证跨 GameObject 的 `Start` 顺序，UdonSharp 下更实际的情形是「motor 的自有步进已经算出了尺寸，但它的 `Start` 事件还没来」；那一刻 motor 的缓冲还是 `null`，写入抛 `Object reference not set`，该 UdonBehaviour 被 halt，球再没动过（参考关卡 `_ref_mas1` 首跑即属此列）。

**规则**（与 v2 接触表的 `EnsureContactV2Buffers` 是同一条）：

1. **谁写谁分配**。每个写入入口先调 `EnsureRegistrationBuffers()` / `EnsureCandidateBuffers()`，不依赖 `Start` 是否跑过。
2. **`Start` 只 ensure，不重分配**。`Start` 调同一个函数，因此在它之前登记的集合**被保留**而不是被重新 new 掉（否则等于把球所在世界的墙静默清空）。
3. **`null` / 长度不足 = 带原因码的拒绝**，绝不空引用。ramp 记 refusal code 7（`GetLastRampRefusalReason` 返回 `"set buffers unavailable"`），候选队列打一条一次性错误并返回 false。

**覆盖的缓冲**：planes / boxes / ramps（`EnsureRegistrationBuffers`）与候选队列（`EnsureCandidateBuffers`）。v2 静态/道具 OBB 表早就是这条规则；径向代理壳只由本组件的吸收事件环写入（事件环存在就意味着已经走过一步），因此仍是 `Start` 分配。

**宿主侧的一半**：`PickupFieldHost.Update` 在 `GetSemanticDiameterWorldM() <= 0` 时直接返回——尺寸还没推导出来时，任何集合描述的都是「世界原点上一个 0 直径的球」。这一条只是就绪约定，**不是** F1 的修复：F1 里 motor 已经有尺寸（它自己的步进算出来的），集合仍然必须能写进去。

**诊断**：ramp 拒绝记在 `GetRampRefusalCount`；它不影响 `SignalOverflow`（容量满的 ramp 例外，是接线错误）。

## 验收

装配层落地后必须给出：

- Unity 编译通过、U# 全量编译 0 错误；
- 全部 EditMode 测试通过（各层测试已存在；装配层本身不能在 EditMode 实例化，新增的只有 `BankShrinkMathTests` 这个纯规则测试，用来替代伪造行为测试）；
- 一个可进 Play Mode 的实验场景，能滚、能吸、能交分、能看尺寸变化。

## 装配约束（assembly constraints）

**一个 GameObject 上挂多个 UdonSharpBehaviour 是官方支持的**：官方 FAQ 对 "Can I have more than one UdonSharp UdonBehavior on a GameObject?" 回答 "Yes."（[UdonSharp FAQ](https://creators.vrchat.com/worlds/udon/udonsharp/#can-i-have-more-than-one-udonsharp-udonbehavior-on-a-gameobject)）。引擎里**没有**"同物体第二个 U# 脚本会被静默禁用"这条规则——本节以前那样断言是错的，2026-11-15 撤回。

**项目惯例，不是引擎限制**：`KatamariBallMotor` 独占一个 GameObject（惯例命名 `MotorHost`），不与其它 UdonSharpBehaviour 同挂。理由都是工程性的：职责边界清楚；自检可读（"这个对象上就是球马达"一眼可验）；重建层级时不会留下旧 motor 副本继续跑。

**真正的同物体硬约束是同步模式**：同物体上多个 UdonBehaviour 会回落到其中最严格的同步设置（Manual + Continuous → Manual，[Network details](https://creators.vrchat.com/worlds/udon/networking/network-details/#multiple-udonbehaviours)）。本项目的行为是 `BehaviourSyncMode.None`，所以**不要把需要 Manual/Continuous 同步的行为与它们同挂**；None 与 Manual 混挂还有 3.10.5 上的串扰报告（[feedback](https://feedback.vrchat.com/udon/p/novariablesync-can-switch-manual-synced-gameobjects-to-continuous)）。

判断与防复发：

- **"组件在、什么都不跑"的真因是实例不对**：调用方持有的 motor 不是跑过 `Start()` 的那一个（场景里有重复根），那份实例的数组没分配，于是 `AddPlane` 抛错、`GetSemanticDiameterWorldM()` 读 0。实验场景驱动的自检检查的就是这个。
- 另一种可能是编辑器脚本用朴素 `AddComponent<T>()` 添加 U# 行为，背后没有 `UdonBehaviour` 和 program asset——**推断，未验证**；应由 UdonSharp 编辑器 API（`UdonSharpEditorUtility.AddUdonSharpComponent<T>()`）添加。
- U# 代理组件显示 disabled 是正常的："Proxies are always disabled and should remain disabled."（[Editor scripting](https://creators.vrchat.com/worlds/udon/editorscripting/#proxies-are-always-disabled-and-should-remain-disabled)），这不是"被禁用"的证据。
- "同物体只有第一个脚本运行"是 ClientSim 的旧 bug，SDK 3.7.6 已修（[3.7.6 release](https://creators.vrchat.com/releases/release-3-7-6/)）；本项目 SDK 3.10.5，不受影响。
- 直径持续为 0 = **该实例的 `Start()` 从未跑过，或你持有的是别的实例**，不是"球太小"。驱动在 `Start()` 自检 `motor == null`；`Update()` 等 120 帧仍读 0 才 `Debug.LogError` 并停驱动（首个 Update 可能早于 motor 的 `Start`，第一帧读 0 不算错）。
- Udon 没有 `FindObjectsOfType`，"场景里 motor 数量必须 = 1"仍由编辑器侧审计保证：跑实验前断言 `FindObjectsOfType<KatamariBallMotor>().Length == 1`。
- 重建球层级时先删旧 motor 对象再新建 `MotorHost`，并同步重建 prefab（prefab 根上残留 motor 会让每次实例化都复活旧结构）。

### 宿主提交契约：幂等 + 无闩锁

`SubmitCandidate` **按 item id 幂等**：同 id 重复提交返回 `true` 且不新增槽位。`WithdrawCandidate` 对不在队列里的 id 返回 `false`，是**无害 no-op**。

队列是"这件是否已入队"的**唯一所有者**，所以宿主**不保存自己的 `queued` 闩锁**，也不据本地记录决定"要不要提交"。两份状态必然分叉：本项目实际出现过闩锁为真而队列里无该件，该件从此永不提交，表现为"球贴到 0.2 m 却什么都没发生"。

宿主正确写法：每次扫描选出"眼下能吸到的最近几件"直接调 `SubmitCandidate`（重复无害）；不在本次选择里的、此前自己提交过的，调 `WithdrawCandidateUnlessAccepted`（不删本步已判定为吸、尚未提交的条目；返回 `false` 时用 `IsCandidateQueued` 区分"已不在队"与"因已接受而保留"）。宿主只记"我提交过哪些行"这一份撤回清单，清单里的行电机不再持有时自行脱落，它从不参与提交决定。回合结束或场地复位的全部释放用 `WithdrawCandidate`（无条件）。

## 兑换（bank）原因码表

`KatamariBallMotor.TryBank(now, requestId)` 是唯一实现的转发入口；原因由 `TryBankWithReason(now, requestId, out int reason)` 给出。

| 编号 | 名字 | 触发条件 |
| --- | --- | --- |
| 0 | `ok` | 兑换成功：已按直径规则缩水、分数已写入 |
| 1 | `noLocalRow` | 台账没有本地行（`hasLocalRow == false`，即 `TryJoin` 未成功） |
| 2 | `invalidRequestId` | `requestId` 为 null 或空串 |
| 3 | `invalidInput` | `now` 或语义直径不是有限数 |
| 4 | **`tooSmall`** | 语义直径 < `minConversionDiameterWorldM`（默认 0.55 m） |
| 5 | `nothingToBank` | `unbankedVolumeUnits <= 0`（没有可兑换的已确认体积） |
| 6 | `sizingRefused` | `SizeModelMath.TryDerive` 或 `BankShrinkMath` 的两步定价返回 false |
| 7 | `ledgerRefused` | `GrowthLedgerMath.TryBank` 拒绝，或读回的 confirmed/banked 为负 |
| 8 | `shrinkFailed` | 台账已提交，但缩水落地失败（`ApplyBankShrink` 返回 false；入参不再是有限数） |

注意 7 与 8 的区别：7 是**没有提交**，8 是**已经提交但缩水没落地**——后者不会回滚台账（`(player, requestId)` 一次性，重试不会重复计分）。

`noLocalRow`（1）在当前架构下不可构造：`Start()` 必定 `TryJoin` 一次且容量为 1，故 EditMode 测试**未覆盖**该分支，测试文件里有注释说明。

## 重试（reset）：`TryResetForNewRound`

来源：`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md` §4「没有重试接缝」与 S6。第一关写 `retry: unlimited`（`story_01_bench_size.json:61`），而在此之前没有任何接缝能把球、账本和计时一起退回开局，只能重载场景。本节定义唯一的重试入口，**不改**场景、不碰道具生命周期。

### 接缝

```csharp
bool TryResetForNewRound(string newRoundId, Vector3 startPosition,
                         float startHeadingDegrees, out int reason);
```

一次调用完成：账本本地球行与全部回合级存储复位（纯规则 `GrowthLedgerMath.TryResetRowForNewRound`，有 EditMode 测试）→ 换 `roundId` → 清候选与两个事件环 → 速度/卡住计时清零 → 尺寸按零体积映射回到基线 → 传送到起点姿态 → 走和 `Start()` 相同的 Lobby→Ready→Preparing→Playing 三条边重进 Playing。`newRoundId` 必须与当前 `roundId` 不同（提交键 `(item, round, generation)` 在新回合必须是新键）；下一个回合 id 从 `GetRoundId()` 读当前值再构造，避免调用方另存一份。

| 编号 | 名字 | 触发条件 |
| --- | --- | --- |
| 0 | `ok` | 复位成功，已在 Playing，球回到基线尺寸 |
| 1 | `noLocalRow` | `hasLocalRow == false`（`TryJoin` 未成功），无行可复位 |
| 2 | `invalidRoundId` | `newRoundId` 为 null 或空串 |
| 3 | `invalidStartPose` | `startPosition` 或 `startHeadingDegrees` 不是有限数 |
| 4 | `roundIdUnchanged` | `newRoundId` 与当前 `roundId` 相同（不是新回合） |
| 5 | `sizeRefused` | 零体积映射 `SizeModelMath.TryDerive(0f, …)` 拒绝，无法保证回到基线尺寸；**此时什么都没动** |
| 6 | `ledgerRefused` | 账本规则拒绝；`GetLastResetLedgerFailure()` 给出该规则的具体码（见下表），**此时什么都没动** |
| 7 | `reenterFailed` | 行已复位到 Lobby，但走回 Playing 被拒（只有损坏的 round store 会这样）；球已复位但不在 Playing，可再调 `TryBeginPlaying(0f)` |

`GetLastResetLedgerFailure()` 与 `GetLastAwardFailure()` 同一形态：`-1` 表示账本根本没被问到（1–5 号原因），`0` 表示接受，`1..11` 是账本规则自己的码。

### 账本规则与失败码

`GrowthLedgerMath.TryResetRowForNewRound(...)` 只动调用方数组，返回 `bool` + `out int failureCode`，先校验后写，拒绝时所有存储保持原样。

| 编号 | 名字 | 触发条件 |
| --- | --- | --- |
| 0 | `ok` | 行已复位、回合级存储已清空 |
| 1 | `invalidRoundStore` | `roundState`/`roundFlags`/`roundClock` 为空、过短、相位越界、序列或 revision 为负；也包括「该相位不允许复位」 |
| 2 | `invalidPlayerColumns` | 玩家并列数组为空、长度不一致、计数越界或存有非法值 |
| 3 | `invalidRoundId` | `newRoundId` 为 null 或空串 |
| 4 | `invalidCommitColumns` | 提交存储形状/值非法 |
| 5 | `invalidClaimColumns` | 认领存储形状/值非法 |
| 6 | `invalidPredictionColumns` | 预测存储形状/值非法 |
| 7 | `invalidBankRequestColumns` | 兑换请求存储形状/值非法 |
| 8 | `invalidAwardColumns` | 奖励键表为空、两个数组长度不等或计数越界 |
| 9 | `invalidPlayerId` | `playerId` 为 null |
| 10 | `unknownPlayer` | `playerId` 不在名单里（含 `playerCount == 0`） |
| 11 | `sharedRoundNotSupported` | `playerCount != 1`；见下面「为什么只允许单行」 |

**清掉什么**：确认体积、已交存分数、玩家代数、生命状态（回 Active）、重生时间；提交存储（旧事件键不再算「已提交」）、认领、预测、兑换请求幂等标记、奖励键表（`settle:first` 这类键是**每回合**一次，新回合必须能重新发）。相位回 Lobby、deadline 清除、提交序列归零、revision +1。**保留什么**：`playerIds[row]`（同一个玩家）。

### 相位规则

| 相位 | 允许复位 | 理由 |
| --- | --- | --- |
| Lobby / Ready / Preparing | 是 | 回合还没真正跑，重新开始没有歧义 |
| Playing | 是（中途重试） | 「再试一次」就是放弃当前这局；拒绝它只会逼调用方先伪造 Settling/Results 两条边。代价是该行未交存体积与已交存分数全部丢弃 |
| Settling / Results | 是 | 结果面板点「再试一次」的正常路径 |
| 未知值 | 否（码 1） | `IsResetAllowedInPhase` 的默认分支是 false，以后加相位必须回来重新决定，不会静默变成可复位 |

判定写成 `public static bool IsResetAllowedInPhase(LedgerPhase)`，可单独测；规则体也调用它，所以决定只有一份。

### 为什么只允许单行（`sharedRoundNotSupported`）

规则清空的是**共享**提交存储。多行时，其它行的确认体积正是由这些提交支撑的；清掉提交却留着别人的体积，就会留下「没有提交支撑的分数」，破坏「只有确认值进分数、分数总额不超过提交增益」的不变量。多人回合重开要整局复位（所有行一起），是另一条规则，本层不定。本层本来就只有一行（`PlayerCapacity = 1`），所以不会遇到。

### 吸收事件环的绝对计数保持单调（刻意决定）

`GetAbsorbedEventCount()` **不因复位回退**。宿主（`PickupFieldHost`）拿绝对下标当自己的游标，只在计数变小的时候重同步；所以：

- 计数回退会让宿主重放一遍已经消失的旧事件；
- 计数跳到容量边界之后会让宿主一直读不到东西直到下一次环绕；
- 保持单调，则重试对任何读者都不需要特例：保留窗口直接变空（`GetAbsorbedEventDroppedCount()` 会报告这些被清掉的事件 = 原计数），新回合的事件从同一个绝对号继续。

实现上把「绝对计数」与「环占用」拆开：`absorbedEventCount` 只增不减，新增的 `absorbedEventRingCount` 才是 0..16 的占用，`GetAbsorbedEventDroppedCount()` = 计数 − 占用。拒绝环（`rejectEventCount`，容量 8）是纯诊断，没有游标契约，复位时直接归零。

### 刻意不复位的东西

- 已注册的平面/盒子：场景几何不随回合变。
- `localClockSeconds`、`authorityFrame`：时钟不倒流；`authorityFrame` 是权威帧号，复位会让外部把新帧当旧帧。
- 诊断日志上限（`rejectDiagnosticsLogged`/`commitDiagnosticsLogged`/`traceLinesLogged`）：它们是为**整个会话**限流，重试若重置，无限重试就等于无限日志。
- 溢出闩锁（`overflowCount`/`overflowLatched`/`contactBufferSaturated`）：接错场景不会因为重试就变好，信号必须留着。
- 场景道具：复位的是球与账本，不是地图；道具「回来」属宿主（S3 的 `PickupFieldHost`）自己按自己的记录恢复，不由 motor 负责。

### 还没做 / 未验证

- 调用方：结果面板按钮 → `TryResetForNewRound(下一个回合 id, 出生点, 出生朝向, out reason)` → 计时/HUD 自己归零（`StoryRoundController`，S4/S6，不在本节）。
- Unity 编译、U# 全量编译、EditMode 的 NUnit 运行都 **NOT_RUN**（本节作者不是 Unity 操作者）。
- host 侧若要从「球上已粘的物件」也恢复，需要它自己的复位接缝；本层只保证事件环不会把旧事件重放给宿主。
