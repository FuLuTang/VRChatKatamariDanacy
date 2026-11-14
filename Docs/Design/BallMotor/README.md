# 球体每帧装配（BallMotor）接口契约

**状态：`PROVISIONAL_NOT_TESTED`。** 本文只定义各层之间的**数据边界与调用顺序**，不定义任何调参值，也不是对原作行为的声明。世界尺度与公共接口仍未冻结（T06）。

## 为什么需要这一层

球层八个模块各自是纯静态数学，谁都不该知道别人存在。装配层是**唯一**知道顺序的地方，它负责：

- 每帧按固定顺序调用各层，把上一层的输出喂给下一层；
- 持有全部可变状态（位置、旋转、速度、账本数组、代理点数组、远端槽位数组）；
- 作为 UdonSharpBehaviour 存在于场景里，负责与 Unity 的 Transform、时间步和网络回调对接。

规矩：**装配层只做搬运和顺序，不再实现任何数学。** 出现计算逻辑就说明它放错层了。

## 每帧顺序

固定步长（`fixedDeltaTime`），顺序不可交换：

| 步 | 层 | 输入 | 输出 | 顺序为什么不能换 |
|---|---|---|---|---|
| 1 | `ControlIntentMath` | 原始输入 + 上帧边沿状态 | 意图序列 | 一切的下游来源；去抖/菜单抑制只在这里发生 |
| 2 | `BallMotionMath` | 意图里的 `Move2D` + 当前速度 | 新速度、请求位移 | 位移决定后面所有几何 |
| 3 | `SweepMath` | 请求位移 + 障碍数组 | 实际位移、是否卡住 | **先扫掠再落地面**：被挡住时不能把球放进障碍里；只有实际位移可以用于视觉滚动 |
| 4 | `GroundContactMath` | 扫掠后中心 + 地面平面 + 代理点 | 贴地后的中心、支持高度、法线修正 | 支持高度依赖最终位置，先移动再贴地 |
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
| `followRigWorldPosition` / `followRigYawDegrees` / `followRigEyeOffsetMeters` / `hasFollowRigFrame` | 跟随架上一帧滤波状态 | 第 9 步成功时 |

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
  3 SweepMath.TrySweepSphere    → sweptCenter（成功才赋值；记录 blocked/stuck）
  4 GroundContactMath.TrySolve  → groundedCenter（成功才赋值）
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

### 刻意不在这里

- 网络、远端球代理、多客户端权威/抢单仲裁：本层只有**一个本地玩家行**的账本；远端提交应走 `GrowthLedgerMath.TryApplyCommit` 的快照/提交路径，由回合控制器拥有。
- 回合状态机本体：`TryBeginPlaying` 只是把 Lobby→Ready→Preparing→Playing 三条合法边转发给账本，没有计时、轮换或结算。
- HUD、尺寸读数、音效、粒子、道具生命周期、物品注册表、空间索引（候选由宿主/适配层收集后 `SubmitCandidate` 送入）。
- 场景资产、prefab、`.meta`、新 asmdef：都没加；U# 程序集仍是 `VRCKatamari.Runtime`。

### Unity 操作者还要接线

1. 建 prefab：`BallPhysicsRoot`（本层只写 position）、其下的 `BallVisualRollRoot`（只写 rotation）、独立的 `FollowRig`，把三个 Transform 挂到 `ballPhysicsRoot` / `ballVisualRollRoot` / `followRigRoot`。
2. 场景：`startCenterWorld`、地面平面法线/偏移、`AddPlane`/`AddBox` 喂障碍、`startCenterWorld` 同时是卡住恢复点。
3. 玩家 rig：`FollowRig` 挂到 Station/玩家参考系；本层只给位置 + yaw，不碰 HMD 姿态。
4. 输入：要么在 Inspector 拖 `debugForward/Turn/Lateral` 做桌面预览，要么由适配层每帧调 `SetMoveAxes` / `SetMenuOpen`；若要接 `VRCInput`，必须先确认 SDK 里确实有该 API（本检出无法确认）。
5. 吸收：候选收集器（触发器/空间结构）填 `SubmitCandidate(...)`，并用 `SetAllowedKinds` 提供允许的种类；**默认空集合 = 全部拒绝**，这是有意的：种类是世界数据，不在本层编。
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
| `baselineVolumeUnits` / `cubicMetersPerVolumeUnit` / `maxCoreRadiusWorldM` | 1 / 0.0141 / 1.1 | 只为让 0.30–2.20 m 档在 1 单位基线下成立，未测量 |
| `eyeHeightMeters` / 滤波 Hz / 眼高上下限 | 1.6 / 5 / 8 / 0.25–3 | `BallMotion` DRAFT 占位 + 本层防越界的临时上下限 |
| `maxItemSizeWorldM` / `contactRadiusWorldM` | 1 / 0 | 占位；见缺口 4 |
| `maxLiftPerStep` / `maxDropPerStep` / `separationEpsilon` / `maxRollDisplacementM` | 0.1 / 0.1 / 0.001 / 0.5 | 本层临时，非实测容差 |
| `stuckTimeoutSeconds` | 1.5 | 本层临时恢复策略 |

### 缺口（数学层没提供、本层没有自己编）

1. **直径 → 体积的反向映射不存在（本层最实质的缺口）。** `SizeModelMath` 只做体积→三尺寸；参考模型 `units_for_diameter`（`Docs/Design/Gameplay/round_model.py`）在 C# 侧没有对应物。所以交分缩小按 (a) 的比例应用在**体积**（尺寸映射的输入）上，而不是需求写的直径上，缩得明显更轻：以 0.30 m 起始、在 1.49 m 交分为例，本实现留下约 **0.95 m**，直径空间公式要求约 **0.60 m**。要精确复刻 (a)，`SizeModelMath` 需要加反向映射（或在契约里加“残余体积”入参）；装配层不应自己写这个反函数的数学。
2. **设备轴 → forward/turn/lateral 的映射没有归属层。** `ControlIntentMath` 只给设备轴对，`BallMotionMath` 只吃三个标量，中间这一步（含相机/rig yaw 基准）目前是适配层约定，本层用 y→forward、x→lateral 顶着。
3. **墙面法向去速没有公开函数。** `SweepMath` 内部投影剩余位移（私有 `TryProject`）但只返回 `slideDirection`；速度的法向分量没人去掉。结果：球会顶住墙停住，不沿墙滑；卡住计时不会被触发（`blocked` 而非 `stuck`），但滑墙手感缺失。
4. **吸收的“相对球径门槛”没有 C# 层。** `PickupAbsorbMath` 只比固定 `maxItemSizeWorldM`；Python 回合模型里的 `pickup_diameter <= ball_diameter * ratio` 没移植。本层因此传固定阈值，不拿球径乘系数（那是公式）。
5. **`growthFraction` 没有 helper。** 设计文档把它指派给组合层（`BallMotion` README 的“唯一回灌量”），`BallMotionMath` 也明说不做半径映射，所以本层只有这一处 `(r-base)/(max-base)` 夹取；若要复用应下沉到 `SizeModelMath`。
6. **账本提交存储是有容量的**（本地 256 条/回合）。满了以后 `TryResolveClaims` 拒绝、候选留在队列里不再增长；正式回合应由拥有账本的控制器扩容或换权威提交。
7. **单球、单本地玩家行**：多球/多人要并行数组 + 多行账本，本版没有。

## 未定项（等 T06 与试玩）

- 固定步长具体值、每帧最多多少个吸收事件、代理点数组容量、远端槽位容量。
- 是否允许在一次固定步内多次扫掠（现在规定一次）。
- 交分事务的触发方式（靠近/输入/自动）——由回合状态机决定，不在本层。
- 舒适转向与移动权威（头/手/摇杆）如何合成 yaw——`FollowRigMath` 只吃一个标量 yaw，谁产它未定。

## 验收

装配层落地后必须给出：

- Unity 编译通过、U# 全量编译 0 错误；
- 全部 EditMode 测试通过（各层测试已存在，装配层本身只测顺序与失败保持语义）；
- 一个可进 Play Mode 的实验场景，能滚、能吸、能交分、能看尺寸变化。
