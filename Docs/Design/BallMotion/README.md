# T46 球形运动离线参考模型

> **DRAFT 草案 / UNVERIFIED**：这是可运行、可重复的主机侧标准库参考模型，用于讨论和测试数学性质；不是 Unity、Udon 或 VRChat 实现。公共接口未冻结。没有声称 VR、舒适性、Udon runtime、Unity、网络或设备实测通过。

## 状态分列

### 【已定】
- 共享地图坐标中的碰撞半径与 Avatar 眼高、表现尺寸分离；球碰撞使用统一语义半径，见 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md) 第1–3节。
- 本地对象分为 `BallPhysicsRoot`、`BallVisualRollRoot`、`CoreMesh`、`AttachmentRoot`、独立 `FollowRig`；视觉滚动不得作为玩法判定，FollowRig 不挂在滚动树下。
- Station + 独立 FollowRig 是目前 PC 实验下的候选方案，不代表 VR 最终通过；姿态与球体自转分离，见 [`Docs/Evidence/StationPlatform/README.md`](../../Evidence/StationPlatform/README.md) 与 [`Docs/Evidence/StationPlatform/station_decision.md`](../../Evidence/StationPlatform/station_decision.md)。T04 记录真人 VR 与舒适性仍待 T05。

### 【DRAFT 草案】
- 固定步长积分：`advance(elapsed_s, ...)` 累积经过时间，仅按 `fixed_dt_s` 更新。相同初始状态、输入序列及步长序列产生相同结果；Python 浮点实现不承诺跨平台逐位一致。
- 位置 `p=(x,y,z)` 米，速度 `v=(vx,vy,vz)` 米/秒；球心由速度积分。`heading_deg` 是水平行进控制朝向，`turn_rate_degps` 单位度/秒。可视自转 `spin_x_deg` 根据路程/半径计算并独立维护；既不反向驱动位置，也不参与碰撞、拾取或 PvP 判定。侧移为受限横向速度意图。
- 加速度 `acceleration_mps2`、制动 `braking_mps2`、摩擦 `friction_per_s`、最大速度 `max_speed_mps` 均显式带单位；摩擦采用指数衰减，空输入时制动为非反向速度限额。
- 半径用暂定线性映射 `r=base+(max-base)*clamp(growth,0,1)`（米）。成长越大，速度上限与转向率按 `1-factor*growth_fraction` 下调，确保因子保持非负。公式与参数全属可替换实验假设，不是定案数值。
- 地面模型为解析平面/无限缓坡 `y=tan(slope)*x`。球心受支持高度约束 `y>=floor(x)+r`，不施加弹跳或坡面动力学。墙面作为自由侧约定的解析平面；每固定步对球心线段做连续球-平面扫掠，解首次距离等于半径的时刻，再去除指向固体的法向速度，避免单步跨过薄墙。墙面角点、双面碰撞、复杂地形尚非完整碰撞解。
- 跟随取景不在本模块：唯一模型是 [`BallLayers/FollowRig/follow_rig.py`](../BallLayers/FollowRig/follow_rig.py)，本模块不含眼高。旧的 `FollowRig` 类与 `eye_height_m = 1.6` 已删除（2026-11-15）；眼点公式为 `eye = 球心 − forward·(0.75 + 2.2r) + up·(0.55 + 2.4r)`，见 `Docs/OPEN_DECISIONS.md` (k)。
- 卡住超过 `stuck_timeout_s` 后清零速度并投影到解析地面；超出 `bounds_half_extent_m` 则置 `out_of_bounds` 并恢复原点。复位行为是离线占位策略，正式世界的恢复点/状态须另定。
- Python 标准库参考代码：[`ball_motion.py`](ball_motion.py)。目前接口仅用于本草案与测试，不冻结。

### 【未决】
- 世界尺度仍为 **OPEN，等待 T06**；不得把这里的米制参数当作最终地图尺度。
- 公平阈值、回合时长、结算细则不得由本模型决定；速度/转向随成长的函数、坡度上限、球体与墙角的实际手感、追尾延迟/舒适阈值都需讨论或实测。参考 [`Docs/08_SOURCES_AND_OPEN_QUESTIONS.md`](../../08_SOURCES_AND_OPEN_QUESTIONS.md)，特别是 Q2/Q3/Q4/Q8；其中没有替本任务解决世界尺度。
- `growth_fraction`、`slope_deg`、`bounds_half_extent_m` 与全部速度/加速度参数均为显式占位参数，不构成已批准数值；跟随取景的滤波频率与眼点参数在 `BallLayers/FollowRig/`，不在本模块。
- Udon 可实现性、Station 的 VR 行为、真实坡道与物理碰撞、网络追尾/同步均未验证（UNVERIFIED）。

## 参数单位表（默认值仅供参考实现运行）

| 参数 | 默认值 | 单位 | 状态 |
|---|---:|---|---|
| `fixed_dt_s` | 1/120 | 秒 | DRAFT 占位 |
| `base_radius_m` / `max_radius_m` | 0.5 / 2.0 | 米 | DRAFT 占位，世界尺度 OPEN |
| `max_speed_mps` | 4.0 | 米/秒 | DRAFT 占位 |
| `acceleration_mps2` / `braking_mps2` | 5.0 / 8.0 | 米/秒² | DRAFT 占位 |
| `friction_per_s` | 1.2 | 每秒⁻¹ | DRAFT 占位 |
| `turn_rate_degps` | 110 | 度/秒 | DRAFT 占位 |
| `max_lateral_mps` | 1.2 | 米/秒 | DRAFT 占位 |
| `slope_deg` | 调用方输入 | 度 | 坡度上限未决 |

## 运行与测试

从仓库根运行：`python3 -m unittest discover -s Docs/tests`。该命令只验证主机侧参考代码性质，不是 Unity/Udon/VR 或网络验收。

## Unity 实现

平面运动这一层已移植为 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Motion/BallMotionMath.cs`（`VRCKatamari.Motion`，纯静态类），EditMode 测试在同项目 `Tests/EditMode/Motion/BallMotionTests.cs`。只覆盖 `_step` 的速度/位移部分，仍是 DRAFT：参数全部由调用方传入，模型默认值没有写成项目常量。

接口只有一个入口、无重载：

```
bool BallMotionMath.TryStep(forward, turn, lateral, headingDegrees, velocity, deltaTime,
    growthFraction, maxSpeedMps, accelerationMps2, brakingMps2, frictionPerSecond,
    turnRateDegreesPerSecond, maxLateralMps, growthSpeedFactor, growthTurnFactor,
    out nextHeadingDegrees, out nextVelocity, out displacement)
```

- 坐标系与单位：世界坐标，+Y 向上，米与秒；运动只在 XZ 平面。`headingDegrees` 是绕 +Y 的偏航、输出恒在 [0,360)，0 面向 +Z，forward=(sin h, 0, cos h)、横向（右）=(cos h, 0, -sin h)，与 Unity yaw 同向。速度 m/s，`displacement` 是本步的米数。
- 每步顺序照抄模型：`growth` 夹到 [0,1] → 速度/转向上限按 `1-factor*growth` 降额 → 按 `turn` 转向并取模 → 沿**转向后**的朝向加速度 → 指数摩擦 `exp(-friction*dt)` → 横向夹到 `±maxLateral` → 平面速度按比例压到上限 → `|clamp(forward)|<1e-9` 时按 `braking*dt` 减速且不反向 → `p += v*dt`。
- 垂直运动不在本层：`velocity.y` 不参与计算，`nextVelocity.y` 与 `displacement.y` 恒为 0。重力、坡面 `y>=floor(x)+r`、墙法向去速、卡住计时与越界复位都不在这里；持有垂直速度的调用方自行合并。
- 不做子步进、不裁剪位移：快球穿薄墙由扫掠层负责（Python `test_fast_sweep...` 的语义），本层必须把整段 `v*dt` 交给它。
- `growthFraction` 就是模型的 `size` 项，由调用方从自己持有的半径算出；本层不做体积↔半径映射（见 `SizeModelMath`）。
- 与 Python 的差异：非法输入返回 `false` 且不写任何 out（模型抛异常或让 NaN 穿过夹取）；`dt<=0` 拒绝（`dt=0` 会让速度上限在没有时间流逝时夹一次）；`%` 用正模实现；算术由 double 变 float；有限输入但中间量溢出（加速度、阻尼指数、位移长度）同样拒绝而不夹取。

Python 测试 → NUnit 对照：

| Python | NUnit | 说明 |
|---|---|---|
| `test_fast_sweep_does_not_tunnel_thin_wall` | `StepTravelIsUnclampedAndReproducible`（部分） | 只验本层给出完整 `v*dt` 且不裁剪；扫掠本身归 `SweepMath`（已有 `SweepTests.cs`） |
| `test_slopes_5_to_15_degrees_stay_supported_and_stable` | 未移植 | 坡面支持是垂直方向的事（`y>=floor(x)+r`、`vy` 恒 0），本层没有 `vy` 通道，当前也未实现 |
| `test_delay_and_fixed_step_are_bounded_and_reproducible` | `StepTravelIsUnclampedAndReproducible` | 定步长累积器归调用方时钟；C# 侧只承诺同一 `dt` 序列可复现 |
| `test_growth_clamps_and_parameters_remain_legal_at_max` | `InputsAndGrowthAreClampedToTheModelBounds` | 半径夹取归 `SizeModelMath`；本层验 `growth` 夹取与降额后上限仍非负 |
| `test_out_of_bounds_recovers_to_origin` | 未移植 | 复位/边界属 `BallMotor` 与世界设置 |

另外补了六条语义测试：终端速度等于降额上限、零输入单调减速到静止且不反向、加速在 `(velocity, forward)` 同时取反时严格反号、转向速率与「先转后加速」、`dt` 分块不可互换（模型只承诺定步长复现）、夹取与 NaN/溢出拒绝。

本次只写源码：**没有**在 Unity 里运行 `run_tests`，也没有做 UdonSharp 全量编译（写作者不是 Unity 操作者）。这两项仍需 Unity 侧确认。

### 未来 `BallMotor` 组合层（仅描述，不实现）

`BallMotor` 自己持有每帧状态（heading、速度、球心、半径、growth、卡住计时、越界标记）与定步长累积器，各层保持无状态、只返回 `bool`。按下面的顺序串联，每个边界只传这些数据：

- **ControlIntentMath → BallMotionMath**：本步 `dt`、Move2D 轴对（设备量，不是世界方向）、舒适转向边沿（化成 ±1）、当前 heading 与平面速度；朝向基准（相机/rig yaw）由适配层给。
- **BallMotionMath → GroundContactMath**：新平面速度、本步平面位移、调用方累加后的球心、heading、`dt`、碰撞半径、地面法线与偏移。
- **GroundContactMath → SweepMath**：修正后的球心、本步剩余位移、碰撞半径、平面/盒障碍数组、接触预算与分离 epsilon。
- **SweepMath → PickupAbsorbMath**：实际落点、被消耗的位移、接触（点、法线、时刻、障碍索引、穿透深度）、`blocked`/`stuck`、去掉法向分量后的速度。
- **PickupAbsorbMath → SizeModelMath**：本步确认的体积增量与当前确认体积（只传账本已确认值，不传远程装饰）。
- **SizeModelMath → VisualRollMath**：滚动半径、支撑法线、本步确认位移、是否瞬移/不连续。
- **VisualRollMath → FollowRigMath**：球心世界位置、球半径、水平 yaw 与 7 个眼点系数（4 个几何 + 3 条护栏）；FollowRig 只回位置 + yaw，不回写玩法状态。
- **SizeModelMath → BallMotionMath（下一帧）**：由新半径算出的 `growthFraction=(r-base)/(max-base)`（再夹到 [0,1]），是唯一回灌量，避免半径映射分叉。
