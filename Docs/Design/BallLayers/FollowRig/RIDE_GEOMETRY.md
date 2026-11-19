# 骑球站姿、眼点、座位、骑手身高与面板（RIDE_GEOMETRY）

> 状态：已落成源码（`Runtime/Riding/RideViewMath.cs`、`RideScaleMath.cs`、`RideStanceMath.cs`、`SeatEyeMath.cs`、`SeatEyeCompensation.cs`、
> `BallRideStation.cs`，场景装配 `Editor/Story/RideUxAssembler.cs`），**未在 Unity/Play/VR 运行**。数字唯一来源：C# 的 `*Math` 纯函数类与
> [ride_geometry.py](<ride_geometry.py>)（推导与表，本文所有数字都是 `python3 Docs/Design/BallLayers/FollowRig/ride_geometry.py` 的输出），测试
> `Tests/EditMode/Riding/RideViewTests.cs`、`RideScaleTests.cs`、`RideStanceTests.cs`、`SeatEyeTests.cs`、
> [test_ride_geometry.py](<../../../tests/follow_rig/test_ride_geometry.py>)。侧视图：[ride_stance_side.svg](<ride_stance_side.svg>)（`ride_geometry.py --svg` 生成）。

## 0. 结论

1. **玩家站在球后、脚在地上、手从肩向前下方按在球背上推球。** 所有者 2026-11-18 的要求："手推着球"，触地点—球顶—视点（座位）构成相似三角形。
2. **整套安排是球径的相似变换。** 触地点 G、球顶 T、视点 V、肩 S、手 H、脚 F 的位置都是球半径 r 的固定倍数，没有加性底座：任何球径下是同一个三角形、同一幅画面、同样够得着。
3. **公式**（推的角度 ψ = 45°，唯一的调节量）：骑手眼高 **E = 2.6546·r**，眼点在球心上方 **1.6546·r**、后方 **1.1768·r**。
4. **手是玩家自己的。** VR 里手就是控制器，Udon 摆不了；VRChat 把玩家真实身体按眼高映射到 avatar，所以站姿用**人体**比例（肩、臂长按眼高计），不用 avatar 的比例。
   avatar 只通过 η（站立时头高于 Station 锚点 ÷ 眼高，≈ 1）进入，入座后实测，眼高请求除以 η。
5. **面板随 E 缩放。** 面板根 `RidePanels` 挂在眼点上，缩放 = E / 1.64，面板的角大小、相对眼睛的方位不随骑手大小变；HUD 放在左下、球的左边，低头看手时也在视野里。
6. **眼高连续跟随球径**：5 Hz 低通、变化 ≥ 0.5 % 才发、两次请求 ≥ 0.1 s；下车恢复原眼高；世界关缩放时放弃缩放并按现状重测（§4.6）。

## 1. 站姿推导（`RideViewMath`、`ride_geometry.py`）

侧视，x 向前、y 向上，球与地面的触地点 G 为原点，球心 (0, r)，球顶 T (0, 2r)。人体量（每米眼高，Drillis & Contini 1966，Winter 转载）：

| 量 | 值 | 说明 |
|---|---|---|
| 肩在眼下 | 0.125·E | (0.936 − 0.818)/0.936 |
| 肩在眼后 | 0.055·E | 估计值（1.62 m 眼高时约 9 cm） |
| 直臂长（肩到掌心） | 0.41·E | (0.186 + 0.146 + 0.054)/0.936 |
| 推时用到的比例 | 0.8 | 肘微弯，推距 0.328·E |
| ψ（臂在水平下） | 45° | 唯一调节量 |

约束链：

```
肩 = 眼 + (−0.055·E, −0.125·E)
球心 = 肩 + (0.328·E + r)·(cos ψ, −sin ψ)      手臂沿球半径指向球心，手 H 在球面上
眼.y = E                                         脚在地上（VRChat 的眼高 = 站立时视点离脚的高度）
⇒ e = E/r = (1 + sin ψ) / (1 − 0.125 − 0.328·sin ψ) = 2.654625
   up = (e − 1)·r = 1.654625·r        back = (−0.055·e + (1 + 0.328·e)·cos ψ)·r = 1.176792·r
```

**相似三角形**：G (0,0)、T (0, 2r)、V (−1.1768r, 2.6546r) 三点只随 r 缩放，三角形 GTV 在任何球径下相似；手 H = 球心 + r·(−cos ψ, sin ψ) = (−0.7071r, 1.7071r)，
肩 S = (−1.3228r, 2.3228r)，S→H→球心共线。motor 的四个眼点字段写成 `eyeBaseUpMeters = 0`、`eyeUpPerRadius = 1.654625`、`eyeBaseBackMeters = 0`、
`eyeBackPerRadius = 1.176792`。`FollowRigMath` 低通高度增量和 yaw；水平位置由当步球心与滤波 yaw 直接计算，不二次混合上一帧世界 X/Z。
motor 的 `eyeUpMaxMeters` / `eyeMaxBackM` 是**加性底座**的上限，不是离球心距离的上限：球径上限 400 m（半径 200 m）时站姿眼点在球心上 330.9 m、后 235.4 m，任何固定米数天花板都会把眼点钉进球里；半径项本身由球径上限约束，不需要也不允许再钳一次。

### 1.1 ψ 的候选（每单位 r；pitch 0 时的视角）

| ψ | E/r | D/E | up | back | 球心俯角 | 半角 | 球上沿俯角 | 下沿 | 球后地面重现 | 球顶在眼下 (E) | 手离眼 前/下 (E) |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 35° | 2.291 | 0.873 | 1.291 | 1.309 | 44.6° | 33.0° | 11.7° | 77.6° | 9.80r | 0.127 | 0.214 / 0.313 |
| 40° | 2.473 | 0.809 | 1.473 | 1.251 | 49.7° | 31.1° | 18.5° | 80.8° | 6.14r | 0.191 | 0.196 / 0.336 |
| **45°** | **2.655** | **0.753** | **1.655** | **1.177** | **54.6°** | **29.5°** | **25.1°** | **84.1°** | **4.50r** | **0.247** | **0.177 / 0.357** |
| 50° | 2.831 | 0.706 | 1.831 | 1.084 | 59.4° | 28.0° | 31.4° | 87.4° | 3.56r | 0.294 | 0.156 / 0.376 |
| 55° | 3.000 | 0.667 | 2.000 | 0.973 | 64.1° | 26.7° | 37.3° | 90.8° | 2.96r | 0.333 | 0.133 / 0.394 |

读法：ψ 越大人越高、站得越近、越要低头才看见球；ψ = 45° 时球顶在眼下 0.247·E（胸口高度），直视前方时球的上沿在视轴下 25°，
球后 4.5r 处地面重新露出。推距只用掉直臂长的 80 %，直臂长 0.38–0.45·E 的人都够得着（用掉 ≤ 86 %）。

### 1.2 关键数字（η = 1；`reach_table()`）

| 球径 D (m) | 眼高 E (m) | 眼离地 (m) | 眼到球心水平距离 (m) | 手接触点：眼前 / 眼下 (m) | 眼到接触点 (m) | 肩到接触点 (m) |
|---:|---:|---:|---:|---|---:|---:|
| 0.3 | 0.398 | 0.398 | 0.177 | 0.070 / 0.142 | 0.159 | 0.131 |
| 1 | 1.327 | 1.327 | 0.588 | 0.235 / 0.474 | 0.529 | 0.435 |
| 2 | 2.655 | 2.655 | 1.177 | 0.470 / 0.948 | 1.058 | 0.871 |
| 5 | 6.637 | 6.637 | 2.942 | 1.174 / 2.369 | 2.644 | 2.177 |

产品球径全表（`size_table()`；面板距离 = 1.6 m × E/1.64）：

| D (m) | E (m) | up (m) | back (m) | 面板距眼 (m) |
|---:|---:|---:|---:|---:|
| 0.222 | 0.2947 | 0.1837 | 0.1306 | 0.287 |
| 0.3 | 0.3982 | 0.2482 | 0.1765 | 0.388 |
| 0.5 | 0.6637 | 0.4137 | 0.2942 | 0.647 |
| 0.8 | 1.0618 | 0.6618 | 0.4707 | 1.036 |
| 2 | 2.6546 | 1.6546 | 1.1768 | 2.590 |
| 20 | 26.5462 | 16.5462 | 11.7679 | 25.899 |

眼高夹在 0.1–100 m：D < 0.0753 m 时 E 被钳在 0.1 m（骑手比规则大，`clamped` 为真并记日志）；D = 20 m 时 E = 26.5 m，不碰上限。原作 5 cm 起始球（MAS1）会被钳制，该关另议。

## 2. 三件事与谁控制

| # | 量 | 由什么控制 | 随 r 变？ |
|---|---|---|---|
| 1 | **玩家的身体在哪**（`VRCStation` 所在 Transform，站立 Station 的锚点 = 脚） | `SeatEyeCompensation` 把该 Transform 写为 `rigRoot.position + rigRoot.rotation * seatOffset`（`seatAnchor` 就是它）；`stationEnterPlayerLocation` 只决定入座瞬间把人放在哪，装配器把两者都归一到 rig 位姿（enter 相对 station 零偏移），朝向继承 rig | 是 |
| 2 | **眼点在哪** | `FollowRigMath` + motor 的 4 个系数（来自 `RideViewMath`，装配器写入） | 是，线性、无底座 |
| 3 | **玩家眼高** | `BallRideStation` 经 `SetAvatarEyeHeightByMeters` 按 `RideScaleMath` 设置，`GetAvatarEyeHeightAsMeters` 读回 | 是，正比于球径 |

场景里没有 Camera 对象，相机就是 avatar 的头；Udon 能写的只有 Station 的 transform：`座位 = 眼点 − 头偏移`。

## 3. η 与座位（`RideStanceMath`、`SeatEyeCompensation`）

- Station 由装配器设为**站立**（VRCStation `seated` 关）且是**移动站**（`PlayerMobility = ImmobilizeForVehicle`）：Station 是 rig 根的子物体，眼点每帧动、补偿又写它的位置，所以它是"会动的 station"；VRChat 对这类的文档配置就是 Immobilize For Vehicle。装配器把 VRCStation 所在 Transform 与 `stationEnterPlayerLocation` 都归一到 `FollowRigRoot` 的位姿（enter 相对 station 因而零偏移），并把 `SeatEyeCompensation.followRigRoot` / `seatAnchor` 分别写成 rig 根与 **VRCStation 本体**。装配完成后回读代理字段、Udon 变量表与 VRCStation 的 `seated`/`PlayerMobility`。
- 为什么锚点是 Station 本体而不是子物体 enter：VRChat 用 `VRCStation` 所在 Transform 搬运坐着的玩家（车辆 Station 就是这么动的），`stationEnterPlayerLocation` 只在入座瞬间决定落点。把补偿写到 enter 子物体会在 ClientSim 里生效（它每帧 `SitPosition(EnterLocation())`），却不一定被真客户端搬运；写 Station 本体在两种实现下都搬运玩家，前提是 enter 相对 Station 零偏移（本装配保证）。
- StoryMode 基底的实际 enter 是 `FollowRigRoot/RideRoot/RideSeat/StandPoint`，原值是 (+0.45, −0.15, 0) m；跳过装配会保留这个偏移。它会被当成头偏移的一部分，而水平护栏是 0.35 m × E/1.64（站姿 E = 2.6546·r，即 0.566·r）：0.3 m 球（r = 0.15）只补偿 0.085 m，残余 0.365 m 横向偏移——这就是"坐在球的右侧"；同时 −0.15 m 让有 η 时头比眼点低 0.15 m。装配后 enter 局部为 (0, 0, 0)、姿态与 Station 相同。HUD 直接跟随眼点和 yaw，所以 HUD 朝向正常不能证明头在正确位置。
- 入座并稳定后，在 `PostLateUpdate` 里读本地玩家 `GetTrackingData(Head)`，换算到 rig 坐标系、相对锚点，取窗口平均，冻结。η = 头.y ÷ 测量时的眼高
  （`RideStanceMath.TryMeasureEta`），护栏 [0.75, 1.25]（不是产品值：坐姿头约 0.5 个眼高）。
- **座位**：水平位置让视点落在眼点；有 η 时竖直位置让锚点（脚）落在球最低点（`FeetTargetInRig` = 球心.y − r − rig.y，每帧从 motor 读，球的滤波滞后不会让脚离地），
  此时竖直不限幅；水平限幅 0.35 m × E/1.64。
- **眼高改变时**：偏移按"测量值 × 当前眼高 ÷ 测量眼高"现算，不重测（均匀缩放的推断，`GetCurrentEyeErrorMeters` 读实时误差检验）。
- **没有 η**（坐姿 avatar、读数离谱、世界关缩放）：座位只对齐眼睛；眼高仍按 η = 1 跟球（手照样够得着），日志 `[ride] no usable stance (reason N)`，`N` 是 `RideStanceMath.Reason*`。
- **手**：`SeatEyeCompensation.GetHandGapMeters()` 读左右手追踪点到球面的距离（负 = 在球内），只读不动，日志 `handGap` 是"手推着球"的实测量。
- 护栏 `SeatEyeMath.MaxPlausibleHeadAboveSeatMeters = 2.1`、`verticalLimitMeters = 2.1`（装配器写入）在 1.64 m 眼高下制定，乘 E/1.64。

## 4. 平台事实与眼高更新

### 4.1 平台事实

| 事实 | 内容 | 来源 |
|---|---|---|
| Udon 眼高夹界 | Target Eye Height 夹在 **0.1–100 m**；Action Menu 0.2–5.0 m | [Avatar Scaling](https://creators.vrchat.com/avatars/avatar-scaling/)（官方） |
| 官方支持范围 | 只支持 0.1–100 m；世界里的 Udon 可强制缩放或限制范围 | [OSC Avatar Scaling](https://docs.vrchat.com/docs/osc-avatar-scaling)（官方） |
| 重新施加 | Udon 必须在 avatar eye height changed 事件里重新施加缩放 | [Avatar Scaling](https://creators.vrchat.com/avatars/avatar-scaling/)（官方） |
| 眼高定义 | Prefab Height 是默认缩放下的眼高；禁用缩放的世界里 Target Eye Height 恒等于 Prefab Height | 同上 |
| 缩放方式 | 改 avatar 根节点的 `localScale` | 同上 |
| 世界关缩放 | `SetAvatarEyeHeightByMeters` 等调用无效 | [Player Avatar Scaling](https://creators.vrchat.com/worlds/udon/players/player-avatar-scaling/)（官方） |
| 事件 | 换 avatar 触发 `OnAvatarChanged`，眼高变化触发 `OnAvatarEyeHeightChanged` | 同上 |
| 读追踪点的时机 | `PostLateUpdate` 在 IK 之后，读数是当帧的 | [UdonSharp 事件](https://udonsharp.docs.vrchat.com/events/)（官方） |
| 重新开启手动缩放 | 会把玩家设回自己的身高，所以骑乘期间不切换 `SetManualAvatarScalingAllowed` | [反馈帖](https://feedback.vrchat.com/open-beta/p/1312-activating-manual-scaling-after-setavatareyeheightbymeters-set-player-back)（社区） |
| Overlay Canvas | 屏幕空间 Overlay Canvas 只画在桌面屏幕上，VR 头显里看不到；VR 可见的 UI 必须是世界空间 Canvas | 本项目 §6.1 的场景证据 + VRChat 世界 UI 惯例（未找到官方单页） |

**未核对**：站立 Station 里头相对锚点的高度是否 ≈ 眼高（η ≈ 1）；缩放是否严格均匀；连续高频 Set 是否限流；近裁剪面/音频/移动速度是否随眼高缩放；
< 0.2 m 时 avatar 表现；`Interact` 的 `proximity` 是否随缩放。

### 4.2 眼高更新

- 首次：入座时读原眼高 E₀ 存下，在 E₀ 上测 η，锁定后才第一次请求；之后 5 Hz 低通、≥ 0.5 % 才发、间隔 ≥ 0.1 s，座位在发送的同一帧按新眼高现算。
- `OnAvatarEyeHeightChanged` 读回与己方发出值差 > 5 %：当作别人改的，重新施加；连续 3 次仍不是就放弃，按现状重测。换 avatar（`OnAvatarChanged`）：η 重测。
- 读回眼高偏离发出值持续约 3 s（世界关缩放）：放弃缩放，按实际眼高重测座位。
- 下车 / 重生：`OnPlayerRespawn` 只发出离站请求（`IsExitPending` 为真，1 s 未回调则重发）；`OnStationExited` 复位座位、恢复存下的眼高、面板缩放回 1。

## 5. 触发器与下车

1. **座位永远不在球上**：座位水平位置让视点落在眼点（球心后 1.1768·r），竖直位置让脚落地。
2. **触发器在球上，随球缩放**：`BallSeatTrigger` 挂在 motor 的 `ballPhysicsRoot` 下，半径 = 1.25 × 碰撞半径，下限 0.25 m。座位自己的 0.45 m 盒子由装配器删掉。
3. **退出**：按住跳跃键 1.5 s，或下车面板按钮点两次，都进 `BallRideStation.RequestExit`，先把 exit location 放到球旁可站立的地面再 `ExitStation`。
4. 下车落点（`RideExitMath`）：球面外 0.9 m，按后/右/左/前各向下射线，法线 y ≥ 0.7、高度在球底 +0.6 m 与 −1.5 m 之间者胜出，抬 5 cm、朝向球；
   都不行保留场景里的 exit location 并报 warning。这些是绝对米数，未随尺度重算。
5. 不给 rig 加 pitch：rig 只有位置 + yaw；VR 的视线由头决定，桌面出生时 pitch = 0。

## 6. 面板

所有骑乘期可见的 UI 都是 `FollowRigRoot/RidePanels` 下的**世界空间** Canvas（`RideUxAssembler` 装配）：HUD 与下车面板在层 8；结算卡是 `RideResultCard.prefab` 实例，根和子物体都在 Default 层 0（VRChat 可交互画布的做法，见 [HUB_PANEL_PREFAB](../../StoryMode/HUB_PANEL_PREFAB.md)「结算卡」）。`RidePanels` 位于眼点、无偏移，
`BallRideStation` 每次发送眼高时把它缩放成 `E / 1.64`（`RideViewMath.PanelScaleForEyeHeight`），下车回 1。下表为 1.64 m 眼高时的布局，
距眼 1.6 m，乘缩放后距离 = 1.6 × E/1.64（§1.2 表最后一列），角度不变：

| 面板 | 方位 / 仰角 | 尺寸 × 每单位米数 | 离球轮廓最近 | 内容 |
|---|---|---|---:|---|
| `StoryHud` | 左 38° / 下 12°（中心锚点） | 1000 × 260 × 0.0014 | 9.6° | 目标、当前、剩余、起始提示 |
| `RideResultCard` | 正前 / 上 6° | 1152 × 576 × 0.001 | 20.9° | 结果文字（TMP）+ 卡面 + uGUI Button「再来一次」「回大厅」（`Prefabs/Story/RideResultCard.prefab` 实例） |
| `RideExitPanel` | 右 24° / 下 8° | 360 × 150 × 0.0014 | 15.0° | 「下车（点两次）」+「按住跳跃键下车」；仅入座时显示 |

"离球轮廓最近"是面板上任一点到球的角半径边界的最小夹角（pitch 0，`panel_clearance_deg`；面板随 E 缩放、站姿正比于 E，所以与球径无关）。
HUD 放在球的左边、视轴下 12°：直视前方和低头看手（接触点在视轴下 63.6°）时都在视野里，不挡球和手。

### 6.1 VR 里看不到 HUD 的根因（2026-11-18，只读核对 Windows 工作区场景文件）

用 `ssh pc114514` 只读读取场景 YAML（PowerShell `Get-Content`/`Select-String`），结果：

| 场景（Windows 工作区） | `StoryHud` Canvas | `RidePanels`/`RideResultCard`/`RideExitPanel`/`BallSeatTrigger` | motor 眼点系数 | VRCStation `seated` | `scaleRiderWithBall` |
|---|---|---|---|---|---|
| `_RefUnlicensed/Scenes/AllMapsHub.unity`（11/18 03:31） | `m_RenderMode: 0`（Screen Space Overlay），根物体（`m_Father: 0`），层 0 | 都没有 | 默认值 0.55 + 2.4r 上、0.75 + 2.2r 后 | 1（坐姿） | 0 |
| `VRC_Katamari/Worlds/StoryMode/StoryMode.unity`（AllMapsHub 的 base） | 同上，`m_RenderMode: 0` | 都没有 | 同上默认值 | 1 | 0 |
| `_RefUnlicensed/Scenes/RefCancer.unity`（11/18 03:08） | `m_RenderMode: 2`（World Space） | 有 `RideExitPanel`、`BallSeatTrigger`、`RideExitPoint`，无 `RidePanels` | 上一版 1.779558 / 5.476916 | 1 | 1 |

结论：

1. **AllMapsHub 的 HUD 是 Screen Space Overlay**：在桌面屏幕上画，VR 头显里不画。不是位置/缩放算错，也不是层或相机剔除（它根本不在世界空间里）。
2. 原因是 **`RideUxAssembler` 从未在 AllMapsHub 上运行过**：`AllMapsHubAssembler` 每次从 base（`StoryMode.unity`）重新复制出场景，再跑岛屿导入和 `HubAssembler.Run`，
   不调用 `RideUxAssembler`；而 base 本身也没装配过骑乘 UX。同一原因让 AllMapsHub 里的眼点系数、Station 姿态、骑手缩放全是旧默认值——
   **如果所有者是在 AllMapsHub 里测的，他测到的是默认机位 + 不缩放的骑手，不是任何一版骑乘几何**（1.6 m 眼高的人骑 0.3 m 的球，球确实在脚下）。
3. 修复：`AllMapsHubAssembler` 在 `HubAssembler.Run` 之后调用 `RideUxAssembler.AssembleOpenScene(problems)`，报告里逐行带 `ride:` 前缀，骑乘装配的 PROBLEM 计入总问题数（有问题不存盘）。
   放在大厅装配**之后**：大厅装配创建 `VrStickMoveInput`，骑乘装配要把它写进 `BallRideStation.vrInput`；骑乘装配不重建、不重挂 `BallRideStation`/VRCStation（大厅的 launcher 按组件引用它们），只把 Station 与它的 enter 位姿归一到 rig（装配后的场景里这已是零位移，重复运行 `~0`），
   结算卡由骑乘装配实例化 `RideResultCard.prefab` 并接线（`StoryResultPresenter.resultCardRoot`/`resultText`、`StoryRoundController.retryButtonRoot`（卡的 `Buttons` 行）、`StoryRetryButton` 的 `controller`/`launcher`、`HubReturnButton` 的 `launcher`）；大厅装配不再生成卡或卡按钮。
   之后 `CheckRideVrInput` 确认 `BallRideStation.vrInput` 就是场景里唯一的 `VrStickMoveInput`，否则报 PROBLEM。

## 7. Unity 验证

**给 Unity 操作员**（未运行；预期读数来自本文数字）：

0. `refresh_unity`，`read_console` 0 错误（`BallRideStation` 新增 `vrInput` 字段，U# 程序要重编）。
1. 执行 `VRC Katamari/Hub/Assemble All-Maps Hub`（会从 `StoryMode.unity` 重新生成 AllMapsHub 并自动跑骑乘装配）。预期：
   - 报告里有 `ride: [ride-ux] done: +N ~M problems=0` 一段，note 里有 `push stance written to the motor (push angle 45 deg)`、`the VRCStation is a standing, moving-station station (seated off, PlayerMobility=ImmobilizeForVehicle)`、
     `panel root created under the follow frame`、`HUD canvas 'StoryHud' is world-space under RidePanels`；总报告 `problems=0` 且 `saved Assets/_RefUnlicensed/Scenes/AllMapsHub.unity`；
   - 若报告 `BallRideStation's Udon variable table lacks: … vrInput`（或 `panelRoot`）：按提示编译 → 存 base 与程序资产 → 重开 → 再生成一次，不可当通过；
   - 然后 `VRC Katamari/Hub/Check Room Wiring` 结尾 `0 problem(s)`。
   在单独的 `StoryMode.unity`（非大厅）上手动执行 `Assemble Ride UX` 时，场景里没有 `VrStickMoveInput` 是正常的，报告记 note `BallRideStation.vrInput left as it is`，不算 PROBLEM。
2. 读场景（Inspector 或 MCP 读组件）：
   - motor：`eyeBaseUpMeters = 0`、`eyeUpPerRadius = 1.654625`、`eyeBaseBackMeters = 0`、`eyeBackPerRadius = 1.176792`、`eyeUpMaxMeters = eyeMaxBackM = 128`（这两个只钳加性底座，本装配的底座为 0，所以它们不限制眼点；半径项由球径上限约束）；
   - VRCStation `seated = false`、`PlayerMobility = Immobilize For Vehicle`（2，不是 Immobilize）；`BallRideStation.scaleRiderWithBall = true`、`panelRoot` 指向 `FollowRigRoot/RidePanels`、`vrInput` 指向根物体 `VrStickMoveInput`（与 `StoryRoundController.vrInput` 同一个）；
   - `SeatEyeCompensation.motor` 已连、`verticalLimitMeters = 2.1`、`followRigRoot = FollowRigRoot`、`seatAnchor = RideSeat`（VRCStation 本体，不是子物体 `StandPoint`）；VRCStation 的 `stationEnterPlayerLocation = RideSeat/StandPoint`，`stationExitPlayerLocation = RideSeat/RideExitPoint`；
   - 场景位姿：`RideSeat`、`StandPoint` 与 `FollowRigRoot` 世界位置/朝向相同（`RideSeat` 经 `RideRoot` 两级 local 均为零；`StandPoint.localPosition = (0, 0, 0)`、`localRotation = identity`、`localScale = 1`）。若仍是 (0.45, −0.15, 0)，说明骑乘装配没跑或失败，按报告 PROBLEM 处理，不能当通过；
   - `StoryHud`：Canvas Render Mode = World Space，父物体 `RidePanels`，层 8，pivot (0.5, 0.5)，sizeDelta (1000, 260)，localScale 0.0014，
     localPosition ≈ (−0.964, −0.333, 1.233)（`HudLocalPosition(1.6, −38, −12)`）；`RideResultCard`（`RideResultCard.prefab` 实例，localScale 0.001）与 `RideExitPanel` 也在 `RidePanels` 下；场景里不再有 Render Mode = Screen Space Overlay 的 `StoryHud`，也没有旧的 `HubResultBoard`。
   - AllMapsHub 文件只读复核（Linux 侧可用 `ssh pc114514` + `Select-String`）：`StoryHud` 的 `m_RenderMode: 2`，`eyeUpPerRadius: 1.654625`、`eyeBackPerRadius: 1.176792`，
     `seated: 0`、`PlayerMobility: 2`，`scaleRiderWithBall: 1`，`StandPoint` 的 `m_LocalPosition: {x: 0, y: 0, z: 0}`，`m_Name: RidePanels` 存在，`vrInput:` 不是 `{fileID: 0}`，`SeatEyeCompensation` 的 `seatAnchor` 指向 `RideSeat`。
3. `run_tests`：`VRCKatamari.Tests.EditMode` 中 `Riding` 全过（RideView / RideScale / RideStance / SeatEye）；U# 全量编译无新错误。其中 `RideAssemblerAlignsTheStationAndEntryToTheRideFrame` 需要真编辑器（预览场景 + UdonSharpUndo），本地 Roslyn 运行器会报"不可执行"，以 Unity 里跑的结果为准；它断言装配后 `StandPoint`/`RideSeat` 都在 rig 位姿、`seatAnchor = RideSeat`、VRCStation 为站立移动站（`seated=false`、`PlayerMobility=2`）。
4. Play（ClientSim），从大厅选关入座，读 Console：
   - `[seat] locked` 行里的 `offset` 水平分量应接近测量到的人头小偏移（不再是固定 −0.34 左右）、`left` ≈ 0；若 `left` 很大说明 enter 仍有固定偏移；`[ride] seat calibrated: eta ≈ 1 …`（或 `no usable stance (reason N)`，记下 N）；
   - 球径约 0.3 / 1 / 2 m 时 `[ride] eye height …`：E ≈ 0.398 / 1.327 / 2.655 m（÷η），`eyeError` < 0.02·E；`handGap` 记下（ClientSim 的手是动画手，只作参考）；
   - `RidePanels.localScale` ≈ E/1.64（0.3 m 球约 0.243）。
5. 同一帧记录球心、FollowRigRoot 位置/yaw、实际 enter 位置/朝向、VRCStation 位置/朝向、HMD 追踪头的位置/朝向以及 `RidePanels` 位置/朝向。用 rig 的四元数逆旋转各位置差（不除缩放）；在静止、横移及左右转向各测一次。`Rig−球心` 坐标的 X 应接近 0，Z ≈ −1.176792·r，静止时 Y ≈ 1.654625·r；`RidePanels−Rig` 应为零；`Station−Rig` 与 `Enter−Rig` 应为零。居中站立、校准锁定并完成缩放后，`Head−Rig` 水平应接近零（侧向 |right| < 0.05 m 视为测量噪声）；入座静止时 `Head−Station` 与 `Head−Enter` 相同。如果不为零，记 `GetState()`、`GetLastRefusal()`、`GetSeatOffsetMeters()`、`GetUncompensatedMeters()`、请求/读回的眼高，再对比 `Head−Enter` 与 `Head−TrackingData(Origin)`；真实 VR 左右换站位后分别重新上球，区分房间追踪偏移与补偿限幅，保留探头自由。
6. 截图（玩家视角，Game 视图 1920×1080；pitch 0 一张、低头看手一张；球径 0.3 m 和 ≥ 1 m 各一组）：要能同时看到 **手的位置、球、HUD**。
   预期：直视时球上沿在视轴下约 25°、HUD 在左下离球约 10°；低头时手在球背上（接触点在视轴下约 64°）。另截一张 Scene 视图侧视：脚在地面、眼在球心后上方。
7. 下车：`[ride] station exit reason=station-exit wasSeated=True inputWired=True vrInputWired=True seatEyeWired=True`、`[ride] eye height restored to <原值>`，`RidePanels.localScale` 回 1；重复入座/下车 3 次结果一致。

**给所有者亲测**（头显，桌面各一次）：
1. 刚上球：低头时手是否刚好按在球背上（伸手就碰到、不用弯腰也不用伸直）？脚是否在地上？`handGap` 日志接近 0 即为对。
2. HUD 是否在左下、不抬头也能读；结果卡、下车按钮能否看清、点到。
3. 坐上球后两根摇杆推球（ControlIntent 复测清单）；离座后摇杆不再推球（`vrInput` 骑乘门）。
4. 球变大时人是否平滑变大，"手推着球"的感觉是否保持；有无眩晕、近裁剪面切物体、声音突变。
5. 觉得人太高（球太矮）或太矮，调的是 ψ（§1.1 表），不是别的数。

## 8. 未运行 / 未验证

Unity 编译、U# 编译、EditMode 实跑；真实客户端：站立 Station 里 η 的实际值、均匀缩放与偏移正比于眼高、眼高设置与恢复、连续 Set 的开销与限流、
近裁剪面与音频、面板在很小与很大骑手下的可读性与可点性、下车落点、VR 手的实际位置（`handGap`）。`unittest discover -s Docs/tests` 只验证几何模型。
