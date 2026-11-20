# 球的惯性模型（起步、松手滑行、转向、刹车、尺度不变、静止后下坡）

**状态：源码已采用 WLK A2，场景写入与 Unity/VR 验收待办。** 所有者于 2026-11-20 决定以《We Love Katamari REROLL+ Royal Reverie》v20230608 为惯性基准：松杆匀减速，杆量控制速度上限，不照搬绝对速度与滚阻。尺度规则、爬升门的尺度和静止后才施加坡重力的规则保留。早期原作资料见 [ORIGINAL_BALL_INERTIA](../../Research/KatamariBallRules/ORIGINAL_BALL_INERTIA.md)。

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
2. 纵向驱动：`基准上限 = maxSpeed·s`，冲刺时再乘冲刺倍率。杆量曲线给出纵向目标，倒车目标另乘 0.5。反推以 `intent·braking·dt` 先减到零，再受反向目标限制；同向未超目标时以 `intent·acceleration·dt` 加速。
3. 阻力：`vf *= exp(-friction·dt)`；`vl *= exp(-(friction + lateralGrip)·dt)`。转弯时旧方向的速度留在 `vl` 里衰减，形成弧线漂移。
4. 侧移输入给 `vl` 加最多 `maxLateral·dt`，且不把 `vl` 推过 `±maxLateral`（已超过的漂移速度只受抓地衰减）。
5. 松手时纵向匀减到零；同向推杆但已有速度高于目标时，不再加推力，按同样的 `rolling·dt` 减到目标，不跳变也不减过头。加速新产生的速度受目标限制；横向不受这条杆量曲线直接钳制。
6. 纵向硬钳仅使用基准前进／倒车上限，平面总速度也只在超过基准上限时按比例缩小。
7. 位移 `v·dt`，不分步、不裁剪（扫掠归接触层）。

`BallMotionMath.DriveSpeedFraction` 同时给纵向驱动与 `ComputeDrive` 的爬升目标速度使用。取归一化前后意图绝对值作为杆量：低于 0.29 线性到 40%；0.29 至小于 0.99 恒为 40%；本作达到 0.99 即 100%。WLK v20230608 的 `sGetMaxSpeedRate` 使用两杆幅值平均；反汇编显示三段曲线，最高段还在 0.99–1.00 插值，本作保留 A2 约定的直接切换。`sSpeedLimit` 0x992d22 比较上一帧速度，既有超速与本帧越限的分支不同，且检查刹车状态（0x992d48）。本作同向以普通滚阻接近目标，是据此避免瞬时削速的工程近似，不是照搬各分支；原作的常规推进也会扣滚阻（`sResistance` 0x991193–0x99143b），本作同向未超目标时不扣。桌面前后键仍是二值。

### 现行参数（D0 下的工程值）

| 参数 | 地图 0 / 电机默认 | StoryMode / TerrainContactLab / AllMapsHub | 单位 / 缩放 |
|---|---:|---:|---|
| `maxSpeedMps` | 4 | 4 | m/s，× s |
| `accelerationMps2` | 7.5 | 5 | m/s²，× s |
| `brakingMps2` | 8 | 8 | m/s²，× s；只给反向推力 |
| `frictionPerSecond` | 0 | 0 | 1/s，不缩放 |
| `rollingResistanceMps2` | 1.9 | 1.9 | m/s²，× s；松杆纵向减速 |
| `lateralGripPerSecond` | 2.9 | 2.9 | 1/s，不缩放 |
| `maxLateralMps` | 1.2 | 1.2 | m/s，× s；只限制侧移输入的贡献 |
| `turnRateDegreesPerSecond` | 110 | 110 | °/s，不缩放 |
| `rampTangentialGravityScale` | 1 | 1 | 无量纲 |
| `rampRestSpeedMps` / `rampRestSeconds` | 0.05 / 0.3 | 0.05 / 0.3 | m/s（× s）/ s |
| `rampSlideMinSlopeDegrees` | 2 | 2 | °，不缩放；坡角必须大于此值 |

两套参数的满速松手曲线相同：`v(t) = max(0, 4 − 1.9t)·s`。按 0.02 s 步进，低于 `0.1·s` m/s 约 2.06 s，约 2.12 s 完全停住，共滑约 `4.17·s` m（0.222 m 起始球约 18.8 个球径）。StoryMode 仍保留较低的起步推力，但不再有较高的纵向黏滞。A2 对齐的是减速形状和杆量限速；不是移植 WLK 按球径插值的速度、加速度和滚阻表。

## 3. 爬升门

`vGate = max(实际平面速度, KIntent·目标速度) / s`，与 D0 下的 `v_req(θ) = 1.5·sinθ`（90° 墙 1.5 m/s）比较，也喂给 `ClimbSpeed` 的速率因子（参考 2 m/s）。因为速度本身 ∝ s，满推始终 `vGate = 2 ≥ 1.5`、半推 `vGate = 0.8 < 1.5`，大球小球都一样（旧：目标速度 ≥ 3 m/s）。`ClimbMath.GateSpeed` 在 `ContactSolveMath.TryStep`（新增末尾默认参数 `gateSizeScale = 1`，既有调用不受影响）与 `KatamariBallMotor.RefreshClimbGate` 共用；`T_climb` 与高度上限未动。

## 4. 静止后下坡

**当前决定（owner 2026-11-17，地图 0 沙盒亲测）：方案 A，滚动阻力照常作用。** C26 2°、C27 5° 顺坡朝向时松手不滑；采用 A2 后 C28 10° 也不持续顺坡滑行。静止闩锁开启不等于必然有可见下滑，缓坡无需绕过滚动阻力，也不为这两格调整参数。脚本把球转成横向后的坡滑读数是另一朝向的诊断，不是顺坡朝向必须滑动的要求。

`RampSlideMath.TryStepRestLatch`：平面速度 < `rampRestSpeedMps·s` 且无推动（`|forward|+|lateral| ≤ 0.05`）时累计静止时间，否则清零；站在可走坡（`2° < 坡角 ≤ maxSupportSlopeDegrees`，非墙、非爬升、非坠落）上且静止满 0.3 s 时锁存「滑动」，之后只要仍在坡上且没有推动就保持（滑动本身是运动，不能靠静止计时维持），任何推动或离开坡面即关闭。滑动期间每步经 `SupportSurfaceMath.TryApplyRampTangentialGravity(重力 g·s，速度上限 maxSpeed·s)` 加沿水平下坡方向的加速度；静止的纵轴若重力分量不超过滚阻，就抵消该分量，横向不受影响（匀减速模型下 `asin(1.9/9.81) ≈ 11.2°`；原作滚阻另随坡度变动，起始球径区间的估算分界约 11.8°）。**运动中（未锁存）不额外加下坡加速**，平地不滑。滑动速度由滚动阻力/阻力/抓地限制：10° 坡朝向垂直坡向时漂移终速约 0.59 m/s·s。实现位置：`StepContactV2` 里 `RefreshClimbGate` 之后、接触求解之前调 `UpdateRampSlide`；坡面取上一步 `WriteContactSupport` 写下的支撑法线，不在坡上的情形还包括针尖支撑、爬升接触和无支撑（下落）。加进 `planarVelocity` 的下坡速度随同一步的接触求解一起积分；有支撑的子步里求解器把下坡速度投影到支撑面的切平面（`ContactSolveMath.SupportTangentVerticalSpeed`），球顺坡走时贴着坡面、不丢支撑，所以闩锁不会因离开坡面而关；上一步被整步拒绝（残差 > 2 cm）时本步不加（拒绝步的速度本来就被清零），`TeleportTo` 清闩锁与静止计时。覆盖范围：任何给出支撑法线的表面（解析坡、OBB 斜板与地形网格）。

### 4.1 45–60°体力坡爬的耗尽滑回

坡/墙分流见[TERRAIN_CLIMB §0.1](TERRAIN_CLIMB.md#01-体力坡爬2026-11-17-当前决定)。`UpdateRampSlide`的RIDE支撑筛选保持≤45°，
不把新陡坡伪装成RIDE。坡通道自己给`RampSlideMath.TryStepRestLatch`传`onSlope=true`，共享motor的0.3s/0.05m/s·s参数；
它存活时`UpdateRampSlide`因climbActive排除，不重复加重力。耗尽→有效推进归零/along置零→静止0.3s→沿负切线滑回，
持推也不能把耗尽体力再当新起爬。积分前由坡通道接管速度，防止运动层每步加的新推力把等待变成向上蠕动。
RIDE缓坡仍受滚动阻力，C26–C28 顺坡不滑；耗尽陡坡在接触通道用重力与1m/s滑回上限，不让缓坡的静摩擦阻住它。
这是**同一个静止闩锁、不同角度通道的速度归属**，不是另加一个计时器算法。恢复判据不变；真motor有50°/58°回坡脚且recovery=0测试，待Unity执行。

## 5. 运动层接口

`TryStep` 的签名不变：纯平面固定步，读取调用方给出的 `sizeScale` 与调参值。纵向黏滞仍可配置，A2 取 0；无前后输入只在纵轴扣匀滚阻，横向仍由抓地衰减。倒车上限仍为前进上限的一半。`ComputeDrive` 使用同一杆量曲线，不另起一套限速。墙爬、接触 v2 和恢复的规则不变。

## 6. 与接触 v2、恢复的相互作用

| 项 | 影响 | 处理 |
|---|---|---|
| 贴墙滑行 | 切向速度原样保留；有惯性后擦墙带着残余切向速度滑得更远，横向漂移让撞墙更常发生 | 无需改接触层；Unity 看 C 矩阵与 W 格 `contact_count` 不增加卡顿 |
| 爬升门 | 读 `vGate/s`，大小球一致；推墙时用实际速度和同一杆量曲线的目标速度 | 同一尺度，不复制杆量曲线 |
| 爬升退出 | 松手 `vGate→0` 仍按现行退出并冷却 0.35 s | 无改动 |
| 卡死恢复 | v2 判据读意图（`|forward|+|lateral| > 0.05` 且不受阻）与 1 cm 圆盘；松手滑行或下坡滑动时 `driveIntent = 0`，`stuckStep` 为假、`stuckSeconds` 每步清零，**滑行不会累计卡死**（读代码的论证，`UpdateContactRecovery`）。软恢复清 `planarVelocity`、硬恢复传送都会清掉惯性 | 无改动；Unity 看 `recovery_delta == 0` |
| 坡上松手 | 满足坡角与静止门后施加下坡重力，仍受滚动阻力/阻力/抓地限制；C26–C28 顺坡朝向不滑 | A2 保留横向坡滑 |

### 拒绝位移时不得积存速度（E3 A2，2026-11-17）

E3 驱动脚本直接取 `motor.GetProgramVariable("planarVelocity")` 并打印 `v.magnitude`；`GetPlanarVelocity()` 也返回同一个电机持久字段（m/s），不是输入、目标速度，也不是球心差分速度。接触求解后若 `residual > 0.02 m`，电机拒绝本步位置，**同一笔 motion 的平面与竖直速度也必须清零**；`climbAlongSpeed` 同时归零，避免下一步从未实现的爬升速率恢复。此规则由 `ContactSolveMath.RejectUnresolvedMotion` 实现，电机在速度回写前调用。可接受的步（含恰好 0.02 m）不改速度，因此正常擦墙切向惯性不受影响；它不是把所有速度替换成 `Δ位置/dt`（推出本来可以造成无动量的修正）。

修前只回滚球心，仍把求解后速度写回；下一步 `BallMotionMath` 继续对这份速度加推力。纯数学 EditMode 的缩小夹具：两不可吸 OBB 的内侧间距 0.20 m，球半径 0.135 m，沿缝持续推进，`dt=0.02 s`，设速度上限 5.015 m/s 以对照日志量级（不是重建 E3 的道具尺寸/成长快照）。求解残差恒 0.070 m、球心 x 恒 0；旧策略 t=0.02/0.50/1.00/1.50/2.00 s 的状态速度为 0.147324/2.992670/4.900882/5.015000/5.015000 m/s。移除挡板且松手，首步仍前移 0.097991 m、速度 4.899538 m/s；反推首步仍向旧方向移动 0.096154 m、速度 4.807678 m/s。**这是真实的状态动量缺陷，不只是显示口径**。修后每个拒绝步状态速度为 0，挡板移除且松手无平面位移，反推第一步即可倒退。

回归：`ContactSolveTests.ARejectedObbStepCannotStoreSpeedOrLaunchWhenTheObstacleDisappears`（松手/反推两例）、`TheResidualGateDiscardsVelocityOnlyWhenItRejectsTheMove`（阈值下/等于/阈值上三例），以及真电机 `ContactModelV2Tests.ARejectedPropGapStepClearsTheMotorsStoredVelocity`（需 Unity）。这不解决夹缝本身的几何不可解；残差、拒绝计数与现行恢复策略保持可见。

**风险**：横向漂移会让球在狭窄通道里蹭墙更久；若 W2/W7、Story 关卡出现新的 recovery，先调大 `lateralGripPerSecond`。大球速度 ∝ s（D=5 m 时 90 m/s）：扫掠层与关卡尺度是否承受须 Unity 验证。

## 7. Unity 验收（PhysicsTestField 以 0.02 s 采样 `GetPlanarVelocity` 画速度-时间曲线）

下面是离线期望，**不是**原作值，也不是 Unity 读数；`D0` 下 m/s，其它球径把速度和距离乘 s、时间不变：

| 判据 | 期望 | 容差 |
|---|---|---|
| 满推到 50% / 90% / 95% 上限时间 | 地图 0 约 0.28 / 0.50 / 0.52 s；StoryMode 约 0.40 / 0.72 / 0.76 s | ±15% |
| 松手到速度 < 0.1·s m/s / 完全停住 | 2.06 / 2.12 s；相邻 0.02 s 帧的纵向速度差为 `0.038·s` m/s，最后一步除外 | ±20%（时间） |
| 松手滑行距离（满速） | `4.17·s` m | ±20% |
| 满速反推到零 | 约 0.50 s | ±20% |
| 满推 + 满转到朝向 90° 时，速度方向与朝向差 | 地图 0 约 23°，StoryMode 约 27° | ±10° |
| 爬升门 | 从静止贴墙：满推目标 `4·s` m/s 能爬；半推目标 `1.6·s` m/s 不能。实际速度仍计入门槛 | — |
| 静止后下坡 | C26–C28 顺坡不滑；C28 横向静止约 0.3 s 后滑、终速约 `0.6·s` m/s；平地不滑 | 静止门 ±0.1 s |
| 贴墙满推、松手、W2 回滑、卡死恢复 | 不应新增恢复 | `recovery_delta == 0` |

场景已有显式字段，不能靠改电机默认值更新。先在 Unity 写入地图 0、StoryMode、TerrainContactLab 的现行值；AllMapsHub 以 StoryMode 为底图，重装配后继承加速 5 m/s² 的预设。VR 验收：半推摇杆稳态上限固定约为满推的 **40%**；试轻推、半推、接近满推和完全松手，并观察大球的加速与冲刺退出，本文不声称舒适。

## 8. 所有者已定 / 仍开放

已定：WLK A2 匀滚阻、杆量限速，保留 D0 缩放、两套起步推力、转向漂移及现有坡滑闩锁。仍开放：是否对齐原作按球径变化的绝对速度、是否使用动量爬升；不在本次验收范围。
