# 球体每帧装配（BallMotor）接口契约

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
| 3 | `SweepMath` | 请求位移 + 障碍数组（P1：先由 `SupportSurfaceMath.IsSteppable` 决定哪些静态盒是台阶并从数组里剔除） | 实际位移、是否卡住、墙面接触法线（去速用） | **先扫掠再落地面**：被挡住时不能把球放进障碍里；只有实际位移可以用于视觉滚动。去速必须在扫掠之后，否则位移用的还是撞墙的请求。台阶掩码必须在扫掠**之前**刷新，因为「能上」的含义就是「允许球心进入扩张 AABB」 |
| 4 | `GroundContactMath` | 扫掠后中心 + **本步选中的支撑面**（P1；关掉地形支撑时仍是原始地面平面）+ 径向代理外壳（长物件 AABB 角点，按突出上限夹紧） | 贴地后的中心、支持高度、法线修正 | 支持高度依赖最终位置，先移动再贴地。选面必须在扫掠之后（用扫掠后的 XZ），下落预算必须在调用之前算好（见「地形支撑接线」） |
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
| 3→4 | 扫掠后的 `Vector3 center`、是否卡住 | 请求位移（已作废） |
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

**状态：源码已写，未编译、未在 Unity 运行、未做 U# 全量编译、未测。** 实现是一个 UdonSharp Behaviour：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs`（命名空间 `VRCKatamari.BallMotor`）。它只持有状态、按上表顺序调用、在失败时保持上一帧。唯一的规则算术是交分缩小：没有任何球层拥有“交分换算”这一策略，所以它被放在同目录的纯静态类 `BankShrinkMath.cs`（同一命名空间、无状态、可单测），行为类只调用它并据结果决定是否动账本；除此之外本层不实现玩法公式。

### 它持有的状态

单球标量（不是并行数组）。本版**只支持一个本地球**：多球需要把下列标量改成同长度并行数组，并在 `StepBall` 外面套一层下标，本次没做。

| 字段 | 用途 | 只由谁写 |
|---|---|---|
| `headingDegrees` | 平面朝向，喂 `BallMotionMath` 与 `FollowRigMath` | 第 2 步成功时 |
| `planarVelocity` | XZ 速度（y 恒 0） | 第 2 步成功时 |
| `ballCenterWorld` | 球心 | 每步末尾提交（各步只读） |
| `coreRadiusWorldM` / `collisionRadiusWorldM` / `semanticDiameterWorldM` | 三个尺寸，分开存 | 第 7 步成功时 |
| `growthFraction` | `(r-base)/(max-base)` 再夹到 [0,1]，由 `SizeModelMath` 的新半径算出；`BallMotionMath` 明说由调用方算 | 第 7 步成功时 |
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
| `proxyDirections` / `proxySupports` / `proxyEndpointPool` / `proxyItem*` / `proxyCandidateKeys` | 径向代理外壳：32 个固定方向、32 个端点支持长度、最多 4 个长物件 × 8 个 AABB 角点、极值候选键 | `Start` 一次分配；`AttachRadialProxy` / `DetachRadialProxy` / `ClearRadialProxies` / `RebuildRadialProxies`；第 4 步只读 |

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
  2.5 RefreshStepMask()         (P1，仅地形支撑开启) 台阶掩码 + 必要时重建扫掠数组
  3 SweepMath.TrySweepSphere    → sweptCenter（成功才赋值；stuck 交给卡住计时）
    SweepMath.TryCollectWallContactNormals + TryRemoveWallNormalVelocity
                                → planarVelocity（只去墙面法向分量，切向保留）
  3.5 ResolveTerrainSupport()   (P1) 选支撑面 + 判定「支撑面掉走」→ isFalling
  4 重力（仅下落）              fallSpeed += g·dt；本步上限 = fallSpeed·dt
    GroundContactMath.TrySolve  → groundedCenter（代理外壳，夹紧突出量；台阶跨级/下落预算换上限）
    GroundContactMath.TrySolve  → followCenter（空代理数组，核心球心）
    落地判定                    → isFalling = false，fallSpeed = 0
  ★ 卡住计时/恢复（本层策略，扫掠层只报 `stuck`）
  5 PickupAbsorbMath.EvaluateAbsorb（对队列里每个候选，用最终位置）
  6 GrowthLedgerMath.TrySubmitClaim + TryResolveClaims（成功才读回体积）
  7 RecomputeConfirmedAndSize   → 体积恒等式（未交存 + 残余）→ SizeModelMath.TryDerive
                                → 三个尺寸 + growthFraction（成功才赋值）
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

数值（本层默认 1 基线单位 / 0.0141 m³ 每单位 / 2.20 m 上限）：1.49 m 约 121.84 确认单位，交分后 **0.5973 m**、约 6.9 单位；若按决定文档的 0.30 起始写则正好 0.5975 m。旧的体积空间写法在同一输入下留下 0.9537 m。**两个空间是立方关系，不是比例**，所以这个差别是结构性的，调比例救不回来。

还会出错的地方：

- `pre` 取当前语义直径，显示上限生效时它就是 2.20 m：从上限交分会按上限缩到约 0.78 m，而账本仍按未交存全额入分。这是“上限只作用于显示”的已知后果，等 2.20 m 真正可达时要复核。
- `bankResidualFraction` 配成 NaN 时现在直接拒绝交分（旧实现会当成 0）。这是有意的“不应用非有限值”，但配置错误会表现为“交分没反应”，诊断时先看这里。
- 缩小比例与最小门槛仍是项目暂定值，不是原作数据；改它们只需要改字段，不需要改本节的数学。

### 固定容量与溢出信号

注册类缓冲区是固定容量：`MaxPlaneCount = 8`、`MaxBoxCount = 8`、`PendingCandidateCapacity = 4`，加上扫掠每步最多 `ContactCapacity = 4` 个接触点。超出容量的条目**直接丢弃**，不排队、不扩容、不降级，所以：

- 障碍超出会改变球实际能撞到什么；
- 候选队列超出会静默丢掉一个本可吸收的物件。

这两者都是**数据错误**（场景接错，或宿主每步塞了超过 4 个候选），不是玩法取舍。因此这里不设计“满了怎么办”的玩法规则，只留信号：

- `AddPlane` / `AddBox` / `SubmitCandidate` 因容量被拒时调用 `SignalOverflow()`：`overflowCount` 自增、`firstOverflowReason` 只记第一次；`HasOverflowSignal()` / `GetOverflowCount()` / `GetFirstOverflowReason()` 供诊断读取。不写日志、不每帧输出。
- `ContactCapacity` 的截断发生在 `SweepMath` 内部（它写到缓冲长度就停，不返回截断标志），本层只能记录“缓冲曾经满过”的 `WasContactBufferSaturated()`。**这是残留缺口**：真正的接触截断计数要在 `SweepMath` 补，本层修不了。

读取时机是检查点（场景装载后、调参后），不是每帧轮询。

### 径向凹凸代理接线（B 阶段；PROVISIONAL）

合同 §3.3 的 B：径向代理驱动**接地高度**，球芯随长物件起伏，镜头不跟着颠。C（代理进入水平碰撞/PVP）**没做**：墙面、挡路盒和 PVP 仍只用球形碰撞半径。

**归属。** 外壳状态在 motor 里——它本来就持有球位姿，并且是唯一调用 `GroundContactMath` 的地方；32 方向、32 支持长度、32 端点池（4 个长物件 × 8 个 AABB 角点）、4 个候选键都在 `Start` 一次分配、复用。吸附/删除/重建/角点/夹紧的**组合协议**放在新文件 `Runtime/BallLayers/RadialProxy/RadialProxyShellMath.cs`；方向生成、世界点转局部、受影响方向、支持求解、候选键和整账本重建仍然只调用 `RadialProxyMath`，没有第二份数学。`PickupFieldHost` 在视觉吸附成功后调 `motor.AttachRadialProxy(itemId, propBounds, localPosition, localRotation)`（作为参数的 `localPosition/localRotation` 就是刚写进 prop 的球局部位姿），整场 `ResetField` 调 `motor.ClearRadialProxies()`；宿主不持有任何代理状态。

**数据流。** 吸收确认（账本 commit）→ 宿主 `AttachProp` 写球局部位姿 → `AttachRadialProxy` → 8 个角点进端点池、只重算受影响方向、登记候选键 → 每步 `RadialProxyShellMath.TryBuildContactPoints` 按突出上限夹紧 → `GroundContactMath.TrySolve` → `ballCenterWorld` 的高度随凹凸变化。**移除**：`DetachRadialProxy` 按幸存端点重算受影响方向（不是历史 max）；`ResetField` / `TryResetForNewRound` 清空外壳；`TryBank` 缩小时与 `TeleportTo` 走整账本 `RebuildRadialProxies`。本版**没有掉落路径**（吸附物是终态），所以没有"单件掉落"的调用点；接掉落时必须调 `DetachRadialProxy`，不能只从视觉上拿走。

**上限与准入（序列化字段，PROVISIONAL）**：

| 字段 | 默认 | 作用 |
|---|---:|---|
| `maxProxyProtrusionPerCoreRadius` | 0.35 | 允许的最大突出量 = 0.35 × 当前核心半径；相对量，球长大时绝对值跟着长，但 `support − core` 的原始突出仍会先自然收回 |
| `maxProxyProtrusionWorldM` | 0.30 m | 绝对护栏，防止大球的 0.35·r 变成夸张的抬升 |
| `maxProxyLiftPerStep` | 0.04 m | 代理起伏引起的单步抬升上限（只在外壳非空时传给 `GroundContactMath`）；非代理的普通贴地仍用 `maxLiftPerStep = 0.1` |
| `maxProxyNormalSpeedPerturbation` | 0.15 m/s | 法向速度扰动上限。B 只有位置通道，requested 扰动传 0；这个上限留给以后真有法向速度时也不能超过 |
| `longObjectMinLongestEdgePerCoreDiameter` | 0.5 | 长物件准入：最长边 ≥ 0.5 × 球直径 |
| `longObjectMinElongation` | 2.5 | 长物件准入：最长边 ≥ 2.5 × **第二长**边（一个轴压过另外两个轴；方片不算） |
| `minProxyProtrusionM` | 0.01 m | 吸附时最远角点连 `core + 1 cm` 都不到就不登记（对支撑没有贡献） |

**镜头不跟着颠。** 第 4 步对同一个平面调用两次 `GroundContactMath.TrySolve`：一次喂夹紧后的代理点，结果决定球的位置；一次喂空数组（核心球心），只把中心交给 `FollowRigMath`。第二次调用的单步升降夹紧故意放到 10 m——它不是玩法移动，锚点必须正好是核心球心，而不是每步最多 0.1 m 追赶出来的近似，所以眼点不会随凹凸上下抖；表现层自己的 5 Hz 低通与传送吸附仍在，但低通不再独自承担防颠。跟随架的 `ballRadiusM` 仍是语义直径的一半（核心），突出量不会被当成球变大。

**每步成本（常数，与已吸附物件总数无关）**：夹紧 32 个端点（≤32 次 sqrt + 缩放）、代理接地 ≤32 次点积、核心球心一次空数组调用（约 10 次运算）。吸附一次：8 角点 + 受影响方向扫描（最坏 32×8 点积）+ 受影响支持重算（最坏 32 方向 × 32 端点），约 1k 次点积；删除同理。整账本重建（32×32 点积）只在交分、传送、重试时发生。

**验证。** 纯逻辑测试在 `Tests/EditMode/BallLayers/RadialProxyShellTests.cs`：单根长棍抬升且不超过突出/单步上限、移除后回落、两件共享方向去掉一件不误删另一件、核心长大后原始突出收回、增量与整账本重建等价、容量满时拒绝且不改旧壳、角点夹紧与未用槽清零。**Unity 编译与 `run_tests` 没有在本检出运行**（Linux 侧不碰 Unity）。

### 地形支撑接线（P1，2026-11-15；PROVISIONAL）

支撑、台阶、重力下落的**数学**在 `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs`；装配层只搬运和排序。设计与取舍见 [TERRAIN_SUPPORT.md](TERRAIN_SUPPORT.md)（§8.1 是实现记录，含与设计的偏离）。

**每步顺序（P1 新增的两处）**：`RefreshStepMask` 在扫掠**前**（台阶盒必须从扫掠里剔除，球才进得去）；`ResolveTerrainSupport` 在扫掠**后**（探针用扫掠后的 XZ）；重力预算在 `GroundContactMath` **前**算好。

**序列化字段（默认值，全部 PROVISIONAL）**：

| 字段 | 默认 | 作用 |
|---|---:|---|
| `terrainSupportEnabled` | **true** | 总开关。关掉时与 P1 之前逐位一致（四条见下） |
| `stepHeightOverBallDiameter` | **0.25** | 比例，不是高度：`stepLimit = 0.25 × 语义直径`（数据/校验器/关卡门同一口径） |
| `maxStepUpLiftWorldM` | 0.6 m | 单次跨级抬升护栏（> 0.25 × 2.2 = 0.55，正常碰不到） |
| `stepHysteresisM` | 0.02 m | 台阶单向迟滞（判成台阶后要超 `limit + 0.02` 才回到墙） |
| `stepStickMarginM` | 0.01 m | 「球已在盒顶之上」余量，避免扫掠把站在台阶上的球推出去 |
| `minStepHeightM` | 0.005 m | 小于它的落差只是数值噪声，不给大抬升（**不是**地图的 0.05 m 分类门） |
| `gravityMps2` | **9.81 m/s²** | 竖直重力（只有下落时作用；没有跳跃） |
| `maxFallSpeedMps` | 40 m/s | 下落速度护栏（单步 ≤0.8 m），不是产品值 |
| `supportDropTriggerM` | **0.01 m** | 「支撑面掉走」的容差；更小的落差由 `maxDropPerStep` 一步吃掉 |

**公开方法（P1）**：`SetGroundPlane(n, o)`、`SetTerrainSupportEnabled(bool)` / `IsTerrainSupportEnabled()`、`GetStepHeightLimitWorldM()`、`GetSupportSurfaceHeightWorldM()`（**支撑面**高度，不是球心减支撑长度）、`GetSupportNormalWorld()`、`GetSupportSurfaceCount()`（本步 band 内面数；地图 0 的 `active_surface_max` 就是它）、`GetSupportSurfaceId()`（0 = 地面，1+i = 第 i 个静态盒）、`GetSupportKind()`（0 地面 / 1 盒 / -1 未解出；2 斜面、3 道具留给 P3/P2）、`GetStepMaskRebuildCount()`（掩码翻转引起的扫掠数组重建次数）、`IsFalling()`。`LevelArenaHost` 侧另有 `Reapply()`：清表后重注册，地图 0 的「一格一注册」靠它。P2 的 `SetSupportProps`、P3 的 `AddRamp` 都**没有**实现，不要在这里找。

**关掉开关 = 逐位一致**：①掩码不刷新、`RebuildObstacles` 按注册顺序复制全部静态盒；②`GroundContactMath` 拿原始 `(groundPlaneNormal, groundPlaneOffset)`；③升降上限还是 `maxLiftPerStep` / `maxProxyLiftPerStep` / `maxDropPerStep`，`isFalling` 恒 false；④`VisualRollMath` 拿原始地面法线。运行期切换会清掩码并重建扫掠数组，`TeleportTo` 也会重置支撑参考与下落状态（传送进空气 → 真下落，不是 0.1/步 缓降）。

**每步成本（常数，与已吸物件数无关）**：掩码门控 ≤8 次比较（尺寸变化 >2 mm / 支撑面变化 >5 mm / 盒集合变化才重算）；选面 ≤8 盒 × 约 15 flops + 1 次归一化，无分配；重力约 10 flops。掩码变化才重建扫掠数组（现有实现本来就会在条数变化时 `new Vector3[]`，P4 实测 GC）。

**与小棍的接法**：小棍决定「球的哪个点最低」，支撑查询决定「那个点下面的面在哪」；本层喂 `GroundContactMath` 的是支撑面的 `(n, dot(n,P))`，代理点数组照旧。跨级那一步用 `min(stepLimit, maxStepUpLiftWorldM)` 覆盖 0.04 的代理上限，下一步立刻回到 0.04，所以长棍仍然弹不动球。**镜头仍读空代理数组解出的核心球心**，不跟台阶/下落起伏（低通照旧）。

**关卡登记**：`Runtime/Story/LevelArenaHost.cs` 引用场景里的 `BoxCollider`（台面/台阶/墙）与墙的 `Transform`（局部 +Z 指向场内），`Setup` 时**烘焙一次** `bounds` / 平面 `(n, offset)` 后就不再读 Collider。`Labs/StoryLevel/StoryLevelLabBootstrap` 不再登记任何几何。

### 跟随架（2026-11-15 定稿语义，PROVISIONAL 数值）

跟随架是**表现层**，也是玩家坐姿的落点：`VRCStation` 挂在 `followRigRoot` 下面，它指向哪里，玩家就在哪里。它只读球的位置、朝向与半径，不写回任何玩法状态。

**这是眼点，不是座位**（2026-11-15 补）：上面的式子给的是**眼睛**该在哪，不是**座位**该在哪。玩家由 `VRCStation` 承载，station 落的是 avatar 的**根**而不是眼睛；把座位直接摆在眼点上，真实相机（avatar 自己的头部相机，场景里没有 Camera 对象）就会再高出一个坐姿眼高（因 avatar 而异，通常 0.4–0.9 m）——试玩反馈的"坐下后头的位置偏高"就是这一条。所以座位由**骑乘层**按实测眼高反推：`Runtime/Riding/SeatEyeCompensation` 在本地玩家坐下后的稳定窗口里实测 `GetTrackingData(Head)` 相对入座点的偏移，再把入座点按 `−inverse(R)·d` 摆到"头正好落在眼点"的位置（R 是跟随架 yaw，d 是实测偏移）；motor 与 `FollowRigMath` 都不知道这件事，公式仍然只负责眼点。偏移测一次就冻结（VR 下头部会随真实头动，不能每帧拉回），离座、重生、换 avatar 时复位重测。补偿量落在两条护栏之内：① 合理性上限 `SeatEyeMath.MaxPlausibleHeadAboveSeatMeters = 1.35 m`，实测头距超过它就整条拒绝（`reason=6`）而不是减半补偿——它是防「读到明显错误的数据（站姿眼高而相机并不在那里）」的护栏，不是产品值：ClientSim 实测 **1.204159 m**（2026-11-15，那里的相机确实就在那个高度）、真机坐姿约 **0.85 m**，1.35 给最大值留了余量、又仍在站姿眼高 1.5–1.8 m 之下；② 竖向钳位 `verticalLimitMeters = 1.3 m`（2026-11-15 定），与 1.35 的合理性上限配套：它必须高于本仓实测到的最高的头（ClientSim **1.204159 m**），否则真实测出来的一米二会被削掉、相机照样偏高——1.3 让这条测量**完整补偿**（`uncompensatedMeters = 0`）；超出的部分不补偿并计入 `uncompensatedMeters`（例：1.40 m 的头剩 0.10 m）。代价只落在第三人称：avatar 下半身更深地没入地面，本地玩家自己的视角不受影响。

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

净空（上方 − 半径）随球**变大**：0.62 / 0.76 / 1.05 / 1.55 m。

**为什么水平视角（pitch 由玩家鼠标控制）能看见前方地面**：上表最后一列是「球+吸附壳」上沿在视轴下方的角度。壳 = 半径 + min(0.6 m, 半径)：吸收门槛允许吃掉和球一样大的物件（最深可到半径），0.6 m 是场景最大道具尺度的上界估计。四个尺寸的上沿都在 **20.4°–32.7°**，所以平视时球和吸附物只占画面下缘（60° 垂直 FOV 的下半屏），上半屏和地平线以上是空的，前方地面从球的两侧一直可见。球本身也始终在画内：球心在视轴下 37.9° / 40.1° / 42.5° / 44.3°，比上沿更深。球径小的时候视角也低（0.10 m 球时眼点离地 0.72 m），**没有把下限写死在人性尺度上**；同时眼点始终在球顶之上（0.62 m 起），不会贴地/穿地。

护栏（不是调参）：`eyeUpMinMeters = 0.1` / `eyeUpMaxMeters = 8` 钳的是**离球心**的高度，`eyeMaxBackM = 8` 钳后退。默认值永远碰不到它们——2.2 m 的球（半径 1.1）也只有 3.19 m，旧方案里那个绝对值 3 m 的天花板会把它削平，所以上下限必须跟着半径走。

滤波不变：x/z 与高度用 `heightFilterHz`（5 Hz）、yaw 用 `yawFilterHz`（8 Hz），alpha = `1 − exp(−2π·hz·dt)`；传送/卡住恢复靠 `discontinuous` 一次吸附。**喂进来的中心是第 4 步空代理数组解出的核心球心**（见上一节），代理凹凸不进入这条高度通道，所以低通只需要处理正常地面/转向，不再需要独自压住长棍的起伏。后退偏移沿**滤波后的 yaw**取，所以偏移方向和写进 transform 的朝向不会打架。代价照旧要说清：后退偏移也是转向时座位横移的半径——110°/s 满转在 2.3 m 后退下约 4.4 m/s；觉得像「侧滑」就降 `eyeBackPerRadius`。

新增的序列化字段（改名/新增都要 Unity 侧重接，见 `Labs/README.md` 的实例变量表说明）：

| 字段 | 类型 | 默认 | 语义 |
|---|---|---:|---|
| `eyeBaseUpMeters` | float | 0.55 m | 眼点高于球心的常量项 |
| `eyeUpPerRadius` | float | 2.4 | 每米半径再抬高多少米 |
| `eyeUpMinMeters` | float | 0.1 m | 护栏：离球心高度的下限 |
| `eyeUpMaxMeters` | float | 8 m | 护栏：离球心高度的上限 |
| `eyeBaseBackMeters` | float | 0.75 m | 球心后方多少米（常量） |
| `eyeBackPerRadius` | float | 2.2 | 每米半径再后退多少米 |
| `eyeMaxBackM` | float | 8 m | 护栏：后退上限 |

删除的字段：`eyeHeightMeters`、`eyeHeightMinMeters`、`eyeHeightMaxMeters`（旧「离球心常量眼高」语义）。**模型侧已于 2026-11-15 同步**：`Docs/Design/BallLayers/FollowRig/follow_rig.py` 的 `try_update(...)` 与 C# `TryUpdate` 同名同序（含调用方持有的上一帧状态），7 个眼点系数是每次调用的参数、模型里不再有 `eye_height_m` 默认值；`Docs/tests/ball_follow_rig/` 由 8 例改为 9 例（新增「球后上方、两项随半径增长」的四档直径检查），`Docs/tests/ball_visual_roll/test_presentation_cross_layer_simulation.py` 的 4 处旧调用一并改完。以后改这里的式子，C# 与 Python 必须同一提交。

### 推球方向浮标（2026-11-15，纯视觉，PROVISIONAL）

`Runtime/BallLayers/PushCue/` 的 `PushDirectionCue` 在球前方浮一个方向小箭头，只读本层与 `BallRideStation` 的公开接口：位置 = 球心 + `forward(跟随架 yaw) × (半径 + 间距)`，高度 = 球心 + `半径 + 悬空量`（即贴着球顶上方浮着），朝向 = 跟随架 yaw，大小 = `基准长度 + 直径 × 系数`（夹在护栏内），再叠一个 0.25 Hz / 2.5 cm 的上下浮动；只在本地玩家坐在球上、且（接了控制器时）回合状态为 `StateRunning` 时显示。**方向取跟随架 yaw 而不是球速**：静止时球速为零，而 yaw 就是座位朝向，指示物与座位不会打架。间距/高度/缩放都随**语义直径**线性放大（直径 0.30 m → 球心前方 0.635 m、离地 0.38 m、长 0.22 m；2.00 m → 2.25 m、2.08 m、0.90 m），因此各尺寸下浮标都落在骑士视轴下方约 20°、球轮廓之外；贴地版本在球长大后会被球自己挡住，所以高度从球顶量起。纯表现：不进候选、不登记碰撞/遮挡数组、不参与任何玩法。公式在 `PushCueMath`，测试在 `Tests/EditMode/BallLayers/PushCueTests.cs`。

### 刻意不在这里

- 网络、远端球代理、多客户端权威/抢单仲裁：本层只有**一个本地玩家行**的账本；远端提交应走 `GrowthLedgerMath.TryApplyCommit` 的快照/提交路径，由回合控制器拥有。
- 回合状态机本体：`TryBeginPlaying` 只是把 Lobby→Ready→Preparing→Playing 三条合法边转发给账本，没有计时、轮换或结算。
- HUD、尺寸读数、音效、粒子、道具生命周期、物品注册表、空间索引（候选由宿主/适配层收集后 `SubmitCandidate` 送入）。
- C 阶段：径向代理**只**参与第 4 步接地高度；水平扫掠（`SweepMath`）、挡路盒、PVP 外形摘要仍然只用球形碰撞半径。代理不写 `RemoteProxyMath`，也不进任何分数/账本。
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
| `fixedStepSeconds` / `maxStepsPerFrame` | 0.02 s / 5 | 本轮临时；上面“未定项”仍未决 |
| `bankResidualFraction` | 0.25 | `Docs/Design/Gameplay/README.md` 的反推暂定值；`OPEN_DECISIONS.md` (a) |
| `minConversionDiameterWorldM` | 0.55 | `Docs/Design/Gameplay/README.md` 的 t1 档下限；`OPEN_DECISIONS.md` (b) |
| `maxSpeedMps` / `accelerationMps2` / `brakingMps2` | 4 / 5 / 8 | `Docs/Design/BallMotion/README.md` DRAFT 占位 |
| `frictionPerSecond` / `turnRateDegreesPerSecond` / `maxLateralMps` | 1.2 / 110 / 1.2 | 同上 |
| `growthSpeedFactor` / `growthTurnFactor` | 0.25 / 0.35 | 仅 `BallMotionTests` 里出现过，属测试值，不是产品值 |
| `baselineVolumeUnits` / `cubicMetersPerVolumeUnit` / `maxCoreRadiusWorldM` | 3.4390244 / 0.0041 / 1.1 | 2026-11-15 为对齐 `round_model.py` 重新标定过：起始体积 `3.4390244 × 0.0041 = 0.0141 m³`（0.29974 m），满径 2.20 m 需约 1356.4 确认单位；未测量 |
| `eyeBaseUpMeters` / `eyeUpPerRadius` / `eyeUpMinMeters` / `eyeUpMaxMeters` | 0.55 m / 2.4 / 0.1 m / 8 m | **PROVISIONAL，2026-11-15 定稿语义**：眼点 = 球心 +（常量 + 半径×系数），上下限是离球心的护栏；见下「跟随架」 |
| `eyeBaseBackMeters` / `eyeBackPerRadius` / `eyeMaxBackM` | 0.75 m / 2.2 / 8 m | 同上：球心后方（常量 + 半径×系数），上限是护栏 |
| `maxItemVolumeWorldM3` / `absorbVolumeRatio` / `contactRadiusWorldM` | 512 / 10 / 0 | 体积门（决定 (l)）：上限 = `min(512, 球体积/10)`；旧的 `maxItemSizeWorldM` / `absorbSizeRatio` 已删除。`contactRadiusWorldM = 0` 是决定 (i) |
| `maxLiftPerStep` / `maxDropPerStep` / `separationEpsilon` / `maxRollDisplacementM` | 0.1 / 0.1 / 0.001 / 0.5 | 本层临时，非实测容差 |
| `maxProxyProtrusionPerCoreRadius` / `maxProxyProtrusionWorldM` | 0.35 / 0.30 m | 径向代理突出上限（相对核心半径 + 绝对护栏）；PROVISIONAL，需单长棍测试 |
| `maxProxyLiftPerStep` / `maxProxyNormalSpeedPerturbation` | 0.04 m / 0.15 m/s | 代理单步抬升与法向速度扰动上限；PROVISIONAL，保守值 |
| `longObjectMinLongestEdgePerCoreDiameter` / `longObjectMinElongation` / `minProxyProtrusionM` | 0.5 / 2.5 / 0.01 m | 长物件准入（最长边 ≥ 0.5 球直径且 ≥ 2.5 × 第二长边）与最小可贡献突出；PROVISIONAL，未做道具普查 |
| `stuckTimeoutSeconds` | 1.5 | 本层临时恢复策略 |
| `terrainSupportEnabled` | true | P1 地形支撑总开关（2026-11-15 用户改定；旧 lab 显式关） |
| `stepHeightOverBallDiameter` / `maxStepUpLiftWorldM` | 0.25 / 0.6 m | 台阶比例（与 `layout.json` 同源）与单次跨级抬升护栏；PROVISIONAL |
| `stepHysteresisM` / `stepStickMarginM` / `minStepHeightM` | 0.02 / 0.01 / 0.005 m | 三条防抖：单向迟滞、已在顶上、去噪；PROVISIONAL |
| `gravityMps2` / `maxFallSpeedMps` / `supportDropTriggerM` | 9.81 m/s² / 40 m/s / 0.01 m | 下落重力、速度护栏、掉走容差；PROVISIONAL（原作无测量） |

### 缺口（数学层没提供、本层没有自己编）

旧缺口“直径 → 体积的反函数不存在”已由 `SizeModelMath.TryVolumeUnitsForSemanticDiameter` 补上，交分因此改在直径空间（见上一节）。下面是仍然存在的缺口；**第 2、3 条已于 2026-11-15 解决**，编号保留以便其它文档继续按“缺口 2”“缺口 3”引用它们，规则分别见本节之后的「墙面法向去速」与缺口 3 本身的说明。

1. **设备轴 → forward/turn/lateral 的映射没有归属层。** `ControlIntentMath` 只给设备轴对，`BallMotionMath` 只吃三个标量，中间这一步（含相机/rig yaw 基准）目前是适配层约定，本层用 y→forward、x→lateral 顶着。
2. ~~**墙面法向去速没有公开函数。**~~ **已解决（2026-11-15）**：规则与实现见下面「墙面法向去速（已解决，PROVISIONAL）」。编号保留，`SPLIT_PROPOSAL.md` 等文档仍按“缺口 2”引用它。
3. ~~**吸收的“相对球径门槛”没有 C# 层**~~ **已解决（2026-11-15，决定 (h)）**：`PickupAbsorbMath.TryComputeAbsorbSizeLimit` 把上限算成 `min(maxItemSizeWorldM, 语义直径 × absorbSizeRatio)`，本层每步把 `semanticDiameterWorldM` 与 `absorbSizeRatio` 一起传进 `EvaluateAbsorbWithValueAndBallSize`（判定调用在 `KatamariBallMotor.cs:747-751`），配方不在本层，所以这条不再算缺口。`maxItemSizeWorldM` 由 1 改成 `8f` 并降级为安全上界，`absorbSizeRatio = 1f` 是 PROVISIONAL；`GetAbsorbSizeLimitWorldM()`（`:1647`）供诊断读当步上限。口径与两个尺寸量的区别见 `Docs/Design/PickupAbsorb/README.md` 1.1 节。
4. **`growthFraction` 没有 helper。** 设计文档把它指派给组合层（`BallMotion` README 的“唯一回灌量”），`BallMotionMath` 也明说不做半径映射，所以本层只有这一处 `(r-base)/(max-base)` 夹取；若要复用应下沉到 `SizeModelMath`。
5. **账本提交存储是有容量的**（本地 256 条/回合）。满了以后 `TryResolveClaims` 拒绝、候选留在队列里不再增长；正式回合应由拥有账本的控制器扩容或换权威提交。
6. **单球、单本地玩家行**：多球/多人要并行数组 + 多行账本，本版没有。

### 墙面法向去速（已解决，2026-11-15；PROVISIONAL）

**症状**：球撞墙时扫掠只把位置停在墙前，`planarVelocity` 仍带着整份撞墙速度（实测日志里 3.95 m/s 持续数秒），于是不沿墙滑、斜撞会“黏”在墙上、转身后还留着过期速度，快速撞墙还有抖动风险。

**规则**：第 3 步成功且本步记录到接触时，对每条墙面法线 `n`（指向障碍外侧）按接触顺序稳定地执行

```
v' = v - min(0, v·n) n
```

只去掉“指向墙里”的分量，切向分量原样保留——滑墙就是切向保留的直接结果。凹角的两条墙依次作用：第二条法线作用在已修正的速度上，所以两个法向分量都会被去掉；重复法线幂等。`planarVelocity` 是 XZ 平面速度，所以法线先归一化再投到水平面，结果的 y 恒 0；水平分量可忽略的法线（盒顶、天花板）不参与，非有限或零长法线整次拒绝。

**实现（都在 `SweepMath`，纯静态、无分配、UdonSharp 安全）**：

- `TryCollectWallContactNormals(contactCenters, contactNormals, contactObstacleIndices, contactCount, planeNormals, planeOffsets, boxMinima, boxMaxima, wallNormals, out int wallNormalCount)`：把本步记录的接触转成指向障碍外侧的法线。平面直接取校验后的单位法线；盒子按接触中心落在未扩张盒的哪一侧重定向——扫掠为对齐 `sweep.py` 保留了 slab 符号规则，负方向进入盒子时记录的是**内侧面**法线（见 [`../BallLayers/Sweep/README.md`](../BallLayers/Sweep/README.md)），拿它去速会把“离开墙”的速度删掉、把“撞进去”的速度留下，所以必须在这里翻正。写入受调用方缓冲区长度限制，返回写入条数。
- `TryRemoveWallNormalVelocity(velocity, wallNormals, wallNormalCount, out Vector3 correctedVelocity)`：按上面的公式逐条去速。

装配层只在第 3 步之后调用这两个函数（`KatamariBallMotor.StepBall`），不自己重算几何，仍符合“装配层只搬运和顺序”。

**为什么只有侧面/盒子接触**：地面平面从来不是扫掠障碍，它由 `GroundContactMath` 的解析平面单独处理；所以地面接触既不会在这里刹车，也不会改方向。只有调用方把地面也注册成扫掠障碍时才会出现近似垂直的接触法线，而那种法线在水平投影里会被忽略。

**刻意不做摩擦、不做反弹**：原作墙面响应没有可靠证据，只去掉法向分量是“不粘墙”的最小规则。**PROVISIONAL**，试玩或拿到原作证据后应重新决定是否加切向摩擦或回弹。

**限制**：只有被记录下来的接触参与（`ContactCapacity = 4`，缓冲饱和时去速不完整，`contactBufferSaturated` 是信号）；一步内只做一次去速，不迭代二次响应；**未在 Unity 编译、未跑 EditMode/NUnit、未做 U# 全量编译**（本文件作者不是 Unity 操作者）。测试见 `Tests/EditMode/BallLayers/SweepTests.cs` 的 `HeadOnWallContactRemovesTheWholeNormalSpeed`、`GlancingHitKeepsTheTangentialSpeed`、`MovingAwayFromAWallLeavesVelocityUnchanged`、`CornerSweepRemovesBothNormalComponents`、`NonFiniteOrDegenerateNormalsAreRefused`、`BoxEnteredAlongItsNegativeFaceIsOrientedOutward`。

## 未定项（等 T06 与试玩）

- 固定步长具体值、每帧最多多少个吸收事件、代理点数组容量、远端槽位容量。
- 是否允许在一次固定步内多次扫掠（现在规定一次）。
- 交分事务的触发方式（靠近/输入/自动）——由回合状态机决定，不在本层。
- 舒适转向与移动权威（头/手/摇杆）如何合成 yaw——`FollowRigMath` 只吃一个标量 yaw，谁产它未定。

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

- **"组件在、什么都不跑"的真因是实例不对**：调用方持有的 motor 不是跑过 `Start()` 的那一个（场景里有重复根），那份实例的数组没分配，于是 `AddPlane` 抛错、`GetSemanticDiameterWorldM()` 读 0。lab 的自检注释记的就是这个（`BallPlaygroundBootstrap.cs` 66–93 行）。
- 另一种可能是编辑器脚本用朴素 `AddComponent<T>()` 添加 U# 行为，背后没有 `UdonBehaviour` 和 program asset——**推断，未验证**；应由 UdonSharp 编辑器 API（`UdonSharpEditorUtility.AddUdonSharpComponent<T>()`）添加。
- U# 代理组件显示 disabled 是正常的："Proxies are always disabled and should remain disabled."（[Editor scripting](https://creators.vrchat.com/worlds/udon/editorscripting/#proxies-are-always-disabled-and-should-remain-disabled)），这不是"被禁用"的证据。
- "同物体只有第一个脚本运行"是 ClientSim 的旧 bug，SDK 3.7.6 已修（[3.7.6 release](https://creators.vrchat.com/releases/release-3-7-6/)）；本项目 SDK 3.10.5，不受影响。
- 直径持续为 0 = **该实例的 `Start()` 从未跑过，或你持有的是别的实例**，不是"球太小"。`BallPlaygroundBootstrap.Start()` 自检 `motor == null`；`Update()` 等 120 帧仍读 0 才 `Debug.LogError` 并停驱动（首个 Update 可能早于 motor 的 `Start`，第一帧读 0 不算错）。
- 该 lab 里用 `motor.transform == transform` 反"同物体"的自检**现在比规则更严**：同物体本身合法。应由下次动这个 lab 的人放宽或改写（例如改成"持有的实例是否为本场景唯一的 motor"），本次不改。
- Udon 没有 `FindObjectsOfType`，"场景里 motor 数量必须 = 1"仍由编辑器侧审计保证：跑实验前断言 `FindObjectsOfType<KatamariBallMotor>().Length == 1`。
- 重建球层级时先删旧 motor 对象再新建 `MotorHost`，并同步重建 prefab（prefab 根上残留 motor 会让每次实例化都复活旧结构）。

### 宿主提交契约：幂等 + 无闩锁（2026-11-14 起）

`SubmitCandidate` **按 item id 幂等**：同 id 重复提交返回 `true` 且不新增槽位。`WithdrawCandidate` 对不在队列里的 id 返回 `false`，是**无害 no-op**。

队列是"这件是否已入队"的**唯一所有者**，所以宿主**不保存自己的 `queued` 闩锁**。两份状态必然分叉：本项目实际出现过闩锁为真而队列里无该件，该件从此永不提交，表现为"球贴到 0.2 m 却什么都没发生"。

宿主正确写法：激活半径内每 tick 直接调 `SubmitCandidate`（重复无害），离开释放半径调 `WithdrawCandidate`，不记录任何本地队列状态。

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

**状态：源码已写，未编译、未在 Unity 运行、未做 U# 全量编译、未测。** 与装配层其余部分同一状态。

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

规则清空的是**共享**提交存储。多行时，其它行的确认体积正是由这些提交支撑的；清掉提交却留着别人的体积，就会留下「没有提交支撑的分数」，破坏「只有确认值进分数、分数总额不超过提交增益」的不变量。多人回合重开要整局复位（所有行一起），是另一条规则，本次不猜。本层本来就只有一行（`PlayerCapacity = 1`），所以不会遇到。

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

## 代码整理（2026-11-15，`SPLIT_PROPOSAL.md` 的第一批）

**状态：源码已改，未编译、未在 Unity 运行、未做 U# 全量编译、未跑 EditMode。** 只动了私有实现：44 个 `[SerializeField]` 的名字与顺序一个没变（改动前后都是 44 个），被测试/场景用到的 public 方法名也都没变（反射钉子 `Start`、`TryBeginPlaying`、`RecomputeConfirmedAndSize`、`TryBankWithReason`、`TryAwardScore`、`SubmitCandidate`/`WithdrawCandidate`/`GetPendingCandidate*` 与字段 `localRow`/`confirmedVolumes`/`bankedScores`/`unbankedVolumeUnits`/`semanticDiameterWorldM`/`cubicMetersPerVolumeUnit`/`baselineVolumeUnits` 全部仍在）。

**这一批做了什么**（细节与证据在 `SPLIT_PROPOSAL.md` 的「落地状态」一节）：

1. 删零读者代码：9 个取值器（`GetCoreRadiusWorldM`、`GetGrowthFraction`、`GetUnbankedVolumeUnits`、`LastSweepWasBlocked`、`LastSweepWasStuck`、`GetFollowRigWorldPosition`、`GetFollowRigYawDegrees`、`HasFollowRigFrame`、`GetAbsorbedEventDiameter`），以及它们的唯一读者一消失就变成只写状态的字段：`lastSweepBlocked`/`lastSweepStuck`（连同 `StepBall` 的两次赋值与 `TryResetForNewRound` 的两次清零）与 `absorbedEventDiameters`（连同分配与写入）。`GetAbsorbedEventPosition` **保留**：`Docs/Design/StoryMode/README.md` 按名字引用它（说明“不要用它，取道具当前位姿”），删它会留下悬空引用，故留给同时改那份文档的人。
2. 修错位 XML 注释：`RecordReject` 的说明原本贴在 `GetTraceCount` 上方（隔约 170 行），已移回 `RecordReject`。
3. 队列压缩只写一份：`CommitAcceptedAbsorbs` 与 `WithdrawCandidate` 里逐字相同的 9 字段复制收进私有 `CompactPending(int write, int read)`（同下标直接返回），顺序与内容不变。
4. reach 公式只写一份：新增 `PickupAbsorbMath.TryComputeReach(itemBounds, collisionRadius, contactRadius, out itemRadius, out reach)`，判定本体与装配层的两处诊断（追踪快照、拒绝日志）改调它。装配层不再在**每个**候选的**每一步**都算一次半径与 reach——追踪关闭时（默认）根本不算，只在被追踪件或要打拒绝日志时算。非法 `bounds`（非有限、负分量、负半径）在判定里仍是 `RejectInvalidInput`，诊断里报 `-1`（本文件既有的“无效”记号），不再打印 NaN。

**这一批刻意没做**（都还留在 `SPLIT_PROPOSAL.md`）：`SizeModelMath.TryGrowthFraction`（S2）、`RingBufferMath`（S4，风险最高的一步）、`StuckRecoveryMath`（S6）、`ControlIntentMath.TryReadMove2D`（S7）、`GrowthLedgerMath.TryProjectBankedScore`（S8）、`proxyRotation` 的处置（提案本身给了两个互斥选项，未定）、`PredictionCapacity = 0` 与四个零长预测数组（删它们要同步改账本调用签名，属高风险一步）、「回合所有者」边界（S3 的账本/交分/奖励/复位搬迁）。
