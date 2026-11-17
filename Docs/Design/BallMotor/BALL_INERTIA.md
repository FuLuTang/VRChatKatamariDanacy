# 球的惯性模型（起步、松手滑行、转向、刹车、尺度不变、静止后下坡）

**状态：已实现，待 Unity 验收。** 离线（Python 参考模型 + 本地 .NET 跑 EditMode）已过；Unity 里的速度曲线读数、Udon 编译、VR 舒适都还没有。**所有者 2026-11-17 拍板**：起步更灵敏（≥95% 上限约 0.7 s）；手感与大小无关（长度型量按 D/D0 缩放）；爬升门按同一尺度；静止过一下之后才下坡滑。原作证据见 [ORIGINAL_BALL_INERTIA](../../Research/KatamariBallRules/ORIGINAL_BALL_INERTIA.md)：**原作没有公开惯性数值**，下面所有数都是我们的 PROVISIONAL，定的是手感，不是复现。

代码：`Runtime/Motion/BallMotionMath.cs`（`TryStep`）、`Runtime/Motion/RampSlideMath.cs`（静止锁存）、`Runtime/BallMotor/KatamariBallMotor.cs`（字段、`CurrentSizeScale`、`UpdateRampSlide`）、`Runtime/BallContact/ClimbMath.cs`（`GateSpeed`）；参考模型 `Docs/Design/BallMotion/ball_motion.py`（速度部分两份必须一致；静止下坡只在 C#）。

## 1. 尺度规则

`s = D / D0`：D = 当前语义直径（`semanticDiameterWorldM`），D0 = 起始球径（`startSemanticDiameterWorldM`，每关可不同：Cancer 0.222 m、校准球 0.2997 m）。

| 类型 | 量 | 规则 |
|---|---|---|
| 长度型（× s） | `maxSpeedMps`、`accelerationMps2`、`brakingMps2`、`rollingResistanceMps2`、`maxLateralMps`（侧移上限）、倒车上限（= 0.5·前进上限）、下坡重力 `g·sinθ`、静止判定速度 | 表里的值都是 D0 下的值 |
| 时间型（不缩放） | `frictionPerSecond`、`lateralGripPerSecond`、`turnRateDegreesPerSecond`、静止保持 0.3 s、`T_climb` | 起停时间、转向速率与球大小无关 |

效果：以「球径/秒」计的速度、以球径计的滑行距离、50%/95% 起步时间、反推过零时间在任何球径都相同；起始球径下的 m/s 数值与以前同量级（最高速 4 m/s @ D0）。`growthSpeedFactor` / `growthTurnFactor`（大球降速降转向）**已删除**，大球不再被降速，而是按 s 变快。下坡口径：沿坡加速度 = `g · sinθ · rampTangentialGravityScale · s`（`g` 取 9.81，s 已是相对 D0 的比值，所以以球径/秒² 计与大小无关）。

## 2. 模型（现行）

每个固定步（Unity 0.02 s），以下长度型量都已乘 s。沿**转向后**的朝向把平面速度分解为纵向 `vf` 与横向 `vl`：

1. 转向：`heading += clamp(turn)·turnRate·dt`（不降额）。
2. 纵向推力：前进意图与 `vf` 反号（反推）用 `braking`，否则用 `acceleration`：`vf += intent·rate·dt`。
3. 阻力：`vf *= exp(-friction·dt)`；`vl *= exp(-(friction + lateralGrip)·dt)`。抓地取代旧的横向硬钳，转弯时旧方向的速度留在 `vl` 里衰减，形成弧线漂移。
4. 侧移输入给 `vl` 加最多 `maxLateral·dt`，且不把 `vl` 推过 `±maxLateral`（已超过的漂移速度只受抓地衰减）。
5. 滚动阻力：只在**没有前进输入**时，`|vf|` 每步减 `rolling·dt`，下限 0、不反向。
6. 纵向钳到 `[-0.5·cap, cap]`（`cap = maxSpeed·s`）；再把平面总速度按比例压到 `cap`。
7. 位移 `v·dt`，不分步、不裁剪（扫掠归接触层）。

不变：`ComputeDrive`（目标速度 = `cap × 意图`）、`DesktopMoveInput`（二值）、接触 v2、爬升其它逻辑（`T_climb`、高度上限 ∝ D）、恢复判据。

### 现行参数（D0 下的值，全部 PROVISIONAL）

| 参数 | 值 | 单位 | 缩放 | 说明 |
|---|---:|---|---|---|
| `maxSpeedMps` | 4 | m/s | × s | 满推终端速度 |
| `accelerationMps2` | 7.5 | m/s² | × s | 前进推力；到 50/95% 上限 0.32 / 0.70 s |
| `frictionPerSecond` | 0.9 | 1/s | — | 纵、横向指数阻力 |
| `rollingResistanceMps2` | 1.3 | m/s² | × s | 无前进输入时线性滑行减速 |
| `lateralGripPerSecond` | 2.0 | 1/s | — | 横向额外衰减；极大 = 无漂移 |
| `brakingMps2` | 6 | m/s² | × s | 只给反向推力（注意：小于前进推力 7.5） |
| `maxLateralMps` | 1.2 | m/s | × s | 只限制侧移输入的贡献 |
| `turnRateDegreesPerSecond` | 110 | °/s | — | 不降额 |
| `rampTangentialGravityScale` | 1 | — | — | 下坡重力占 `g·sinθ` 的比例，只在「静止后」生效 |
| `rampRestSpeedMps` / `rampRestSeconds` | 0.05 / 0.3 | m/s（× s）/ s | 速度 × s | 判定「静止」 |
| `rampSlideMinSlopeDegrees` | 2 | ° | — | 小于它的平地不滑；上限用现有 `maxSupportSlopeDegrees` |

离线读数（Python 参考模型 0.02 s；任何球径相同）：到 50 / 90 / 95% 上限 0.32 / 0.64 / 0.70 s；满速松手到 <0.1·s m/s 约 1.4 s，滑行 2.26 m @ 4 m/s（≈ 10.2 个 0.222 m 球径；0.2997 m 起始球则 ≈ 7.5 个球径）；满速反推到速度过零 0.54 s；满推 + 满转到朝向 90° 时速度方向落后朝向约 29°（速度保持 4 m/s，因推力把速度补回上限）。

旧行为对照值（只用于旧 Lab 对照，不是第二套实现）：`s = 1`、`acceleration 5`、`braking 8`、`friction 1.2`、`rollingResistance 8`、`lateralGrip 1000`、`maxLateral 1.2`。近似旧手感（满速松手 ≈ 0.45 s 停；转向速度方向基本跟朝向），不逐位等价。EditMode `LegacyComparisonValuesStopQuicklyAndFollowTheHeading` 与 Python `test_legacy_equivalent_values_stop_in_about_half_a_second` 固定了这组值。

## 3. 爬升门

`vGate = max(实际平面速度, KIntent·目标速度) / s`，与 D0 下的 `v_req(θ) = 1.5·sinθ`（90° 墙 1.5 m/s）比较，也喂给 `ClimbSpeed` 的速率因子（参考 2 m/s）。因为速度本身 ∝ s，满推始终 `vGate = 2 ≥ 1.5`、半推 `vGate = 1 < 1.5`，大球小球都一样（旧：目标速度 ≥ 3 m/s）。只改了阈值的尺度，`ClimbMath.GateSpeed` 在 `ContactSolveMath.TryStep`（新增末尾默认参数 `gateSizeScale = 1`，既有调用不受影响）与 `KatamariBallMotor.RefreshClimbGate` 共用；`T_climb` 与高度上限未动。

## 4. 静止后下坡

`RampSlideMath.TryStepRestLatch`：平面速度 < `rampRestSpeedMps·s` 且无推动（`|forward|+|lateral| ≤ 0.05`）时累计静止时间，否则清零；站在可走坡（`2° < 坡角 ≤ maxSupportSlopeDegrees`，非墙、非爬升、非坠落）上且静止满 0.3 s 时锁存「滑动」，之后只要仍在坡上且没有推动就保持（滑动本身是运动，不能靠静止计时维持），任何推动或离开坡面即关闭。滑动期间每步经 `SupportSurfaceMath.TryApplyRampTangentialGravity(重力 g·s，速度上限 maxSpeed·s)` 加沿水平下坡方向的加速度；**运动中（未锁存）不额外加下坡加速**，平地不滑。滑动速度由滚动阻力/阻力/抓地限制：10° 坡朝向垂直坡向时漂移终速约 0.59 m/s·s；坡太缓（`g·sinθ` 小于滚动阻力与阻力平衡）时会慢到几乎不滑。覆盖范围：任何给出支撑法线的表面（解析坡与地形网格）。

## 5. 契约更正（相对惯性改动前）

1. `BallMotionTests`/`TerrainClimbTests` 里的 `TryStep` 调用同步：签名为 `(forward, turn, lateral, heading, velocity, dt, sizeScale, maxSpeed, acceleration, braking, friction, rollingResistance, lateralGrip, turnRate, maxLateral, out …)`——**删除** `growthFraction`、`growthSpeedFactor`、`growthTurnFactor`，**新增** `sizeScale`（有限且 > 0）、`rollingResistanceMps2`、`lateralGripPerSecond`。
2. `brakingMps2`：旧 = 无前进输入时对总速度的减速；新 = 只在前进输入与前向速度反号时作为推力率。无输入不再用它。
3. 无前进输入的停车：旧 = 对总速度减 `braking·dt`；新 = 纵向线性滚动阻力 + 指数阻力，横向只有指数衰减。
4. 横向：旧 = 硬钳 `±maxLateralMps`；新 = 抓地衰减，`maxLateralMps` 只限制侧移输入的增量。
5. 倒车：旧 = 同前进上限与加速度；新 = 上限 0.5·cap，从前进速度反推时走 `brakingMps2`。
6. 大球不再降速（旧 `growthSpeedFactor` 把 2.20 档压到 3.0 m/s、转向压到 71.5°/s）；现在速度、加速度随 s 变大，转向不变。
7. 爬升门阈值读 `vGate / s`；`ContactSolveMath.TryStep` 增加末尾默认参数 `gateSizeScale`。
8. `rampTangentialGravityScale` 默认 0 → 1，并只在「静止后」生效；新增三个序列化字段（`rampRestSpeedMps`、`rampRestSeconds`、`rampSlideMinSlopeDegrees`）。
9. 总平面速度仍压到 `cap`（保留，使侧移输入叠加漂移时不超上限）。
10. `ComputeDrive`、`DesktopMoveInput`、接触 v2、爬升其它逻辑、恢复判据：无改动。

## 6. 与接触 v2、恢复的相互作用

| 项 | 影响 | 处理 |
|---|---|---|
| 贴墙滑行 | 切向速度原样保留；有惯性后擦墙带着残余切向速度滑得更远，横向漂移让撞墙更常发生 | 无需改接触层；Unity 看 C 矩阵与 W 格 `contact_count` 不增加卡顿 |
| 爬升门 | 读 `vGate/s`，大小球一致；起步变快（0.7 s）后推墙更早到门 | 只改阈值尺度 |
| 爬升退出 | 松手 `vGate→0` 仍按现行退出并冷却 0.35 s | 无改动 |
| 卡死恢复 | v2 判据读意图（`|forward|+|lateral| > 0.05` 且不受阻）与 1 cm 圆盘；松手滑行或下坡滑动时 `driveIntent = 0`，`stuckStep` 为假、`stuckSeconds` 每步清零，**滑行不会累计卡死**（读代码的论证，`UpdateContactRecovery`）。软恢复清 `planarVelocity`、硬恢复传送都会清掉惯性 | 无改动；Unity 看 `recovery_delta == 0` |
| 坡上松手 | 球停下 0.3 s 后开始顺坡滑；站在坡上不动会被带走 | 所有者要求；若影响摆放（坡上道具场景）调大 `rampSlideMinSlopeDegrees` 或把 `rampTangentialGravityScale` 设 0 |

**风险**：横向漂移会让球在狭窄通道里蹭墙更久；若 W2/W7、Story 关卡出现新的 recovery，先调大 `lateralGripPerSecond`。大球速度 ∝ s（D=5 m 时 90 m/s）：扫掠层与关卡尺度是否承受须 Unity 验证；`brakingMps2`（6）小于 `accelerationMps2`（7.5），反推的「刹车」比前进推力弱，试玩觉得刹车不够狠就调大。

## 7. Unity 验收（PhysicsTestField 以 0.02 s 采样 `GetPlanarVelocity` 画速度-时间曲线）

下面是离线期望，**不是**原作值，也不是 Unity 读数；`D0` 下 m/s，其它球径把速度和距离乘 s、时间不变：

| 判据 | 期望 | 容差 |
|---|---|---|
| 满推到 50% / 95% 上限时间 | 0.32 s / 0.70 s（任何球径） | ±15% |
| 松手到速度 < 0.1·s m/s | 约 1.4 s | ±20% |
| 松手滑行距离（满速） | 2.26 m @ D0（≈ 10.2 个 0.222 m 球径，任何球径同比例） | ±20% |
| 反推到速度过零（满速） | 约 0.54 s | ±20% |
| 满推 + 满转到朝向 90° 时，速度方向与朝向差 | 约 29° | ±10° |
| 爬升门 | 满推（目标速度 = 4·s）能爬，半推（2·s）不爬，球径 0.222 / 0.8 / 5 m 都一致 | — |
| 静止后下坡 | 10° 坡上静止 ≥ 0.3 s 后开始滑；运动中不加速；平地不滑 | 开始时刻 ±0.1 s |
| 贴墙满推、松手、W2 回滑、卡死恢复 | 与现行相同 | `recovery_delta == 0` |

场景里 `accelerationMps2` / `brakingMps2` / `frictionPerSecond` / `rampTangentialGravityScale: 0` 有显式旧值覆盖，须先由操作员改成现行值再读数；场景里残留的 `growthSpeedFactor` / `growthTurnFactor` 键是死键（Unity 忽略）。VR 舒适：最大减速度约 6 m/s²·s，大球绝对加速度更大，**本文不声称舒适**。Udon 成本：每步多一个 `exp` 与几次乘加，无分配。

## 8. 所有者已定 / 仍开放

已定：预设 B 的松手/反推/转向手感；起步 95% ≈ 0.7 s；尺度不变（D0 基准）；转向漂移开；爬升门同尺度；静止后下坡（0.3 s、2° 起）；`growthSpeedFactor` 删除（PARITY M01 的「大球更慢」分歧随之消除）。仍开放：试玩后调 `acceleration` / `rolling` / `grip` / `braking`；是否要「动量爬升」（门读实际速度，需重做 v2 验收）。
