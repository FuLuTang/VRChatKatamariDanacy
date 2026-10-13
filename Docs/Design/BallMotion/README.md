# T46 球形运动、追尾与眼高离线参考模型

> **DRAFT 草案 / UNVERIFIED**：这是可运行、可重复的主机侧标准库参考模型，用于讨论和测试数学性质；不是 Unity、Udon 或 VRChat 实现。公共接口未冻结。没有声称 VR、舒适性、Udon runtime、Unity、网络或设备实测通过。

## 状态分列

### 【已定】
- 共享地图坐标中的碰撞半径与 Avatar 眼高、表现尺寸分离；球碰撞使用统一语义半径，见 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md) 第1–3节。
- 本地对象分为 `BallPhysicsRoot`、`BallVisualRollRoot`、`CoreMesh`、`AttachmentRoot`、独立 `FollowRig`；视觉滚动不得作为玩法判定，FollowRig 不挂在滚动树下。
- Station + 独立 FollowRig 是目前 PC 实验下的候选方案，不代表 VR 最终通过；姿态与球体自转分离，见 [`Docs/Evidence/T04/README.md`](../../Evidence/T04/README.md) 与 [`Docs/Evidence/T04/station_decision.md`](../../Evidence/T04/station_decision.md)。T04 记录真人 VR 与舒适性仍待 T05。

### 【DRAFT 草案】
- 固定步长积分：`advance(elapsed_s, ...)` 累积经过时间，仅按 `fixed_dt_s` 更新。相同初始状态、输入序列及步长序列产生相同结果；Python 浮点实现不承诺跨平台逐位一致。
- 位置 `p=(x,y,z)` 米，速度 `v=(vx,vy,vz)` 米/秒；球心由速度积分。`heading_deg` 是水平行进控制朝向，`turn_rate_degps` 单位度/秒。可视自转 `spin_x_deg` 根据路程/半径计算并独立维护；既不反向驱动位置，也不参与碰撞、拾取或 PvP 判定。侧移为受限横向速度意图。
- 加速度 `acceleration_mps2`、制动 `braking_mps2`、摩擦 `friction_per_s`、最大速度 `max_speed_mps` 均显式带单位；摩擦采用指数衰减，空输入时制动为非反向速度限额。
- 半径用暂定线性映射 `r=base+(max-base)*clamp(growth,0,1)`（米）。成长越大，速度上限与转向率按 `1-factor*growth_fraction` 下调，确保因子保持非负。公式与参数全属可替换实验假设，不是定案数值。
- 地面模型为解析平面/无限缓坡 `y=tan(slope)*x`。球心受支持高度约束 `y>=floor(x)+r`，不施加弹跳或坡面动力学。墙面作为自由侧约定的解析平面；每固定步对球心线段做连续球-平面扫掠，解首次距离等于半径的时刻，再去除指向固体的法向速度，避免单步跨过薄墙。墙面角点、双面碰撞、复杂地形尚非完整碰撞解。
- FollowRig 的位置与眼高使用指数平滑；高度目标为球心/输入位置高度加 `eye_height_m`。只读输入姿态的 yaw（偏航），独立过滤角差；每次输出将 roll（翻滚）和 pitch（俯仰）硬置零。眼高与过滤频率均为草案参数，不代表舒适阈值。
- 卡住超过 `stuck_timeout_s` 后清零速度并投影到解析地面；超出 `bounds_half_extent_m` 则置 `out_of_bounds` 并恢复原点。复位行为是离线占位策略，正式世界的恢复点/状态须另定。
- Python 标准库参考代码：[`ball_motion.py`](ball_motion.py)。目前接口仅用于本草案与测试，不冻结。

### 【未决】
- 世界尺度仍为 **OPEN，等待 T06**；不得把这里的米制参数当作最终地图尺度。
- 公平阈值、回合时长、结算细则不得由本模型决定；速度/转向随成长的函数、坡度上限、球体与墙角的实际手感、追尾延迟/舒适阈值都需讨论或实测。参考 [`Docs/08_SOURCES_AND_OPEN_QUESTIONS.md`](../../08_SOURCES_AND_OPEN_QUESTIONS.md)，特别是 Q2/Q3/Q4/Q8；其中没有替本任务解决世界尺度。
- `growth_fraction`、`slope_deg`、`bounds_half_extent_m`、眼高与滤波频率、全部速度/加速度参数均为显式占位参数，不构成已批准数值。
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
| `eye_height_m` | 1.6 | 米 | DRAFT 占位，非舒适性结论 |
| `height_filter_hz` / `yaw_filter_hz` | 5 / 8 | Hz | DRAFT 占位 |
| `slope_deg` | 调用方输入 | 度 | 坡度上限未决 |

## 运行与测试

从仓库根运行：`python3 -m unittest discover -s Docs/tests`。该命令只验证主机侧参考代码性质，不是 Unity/Udon/VR 或网络验收。
