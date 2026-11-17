# 骑球取景、眼点、座位与骑手身高（RIDE_GEOMETRY）

> 状态：已落成源码（`Runtime/Riding/RideViewMath.cs`、`RideScaleMath.cs`、`RideWaistMath.cs`、`SeatEyeMath.cs`、`SeatEyeCompensation.cs`、
> `BallRideStation.cs`，场景装配 `Editor/Story/RideUxAssembler.cs`），**未在 Unity/Play/VR 运行**。数字唯一来源：C# 的 `*Math` 纯函数类与
> `ride_geometry.py`（推导与表），测试 `Tests/EditMode/Riding/RideViewTests.cs`、`RideScaleTests.cs`、`RideWaistTests.cs`、`SeatEyeTests.cs`、
> `Docs/tests/follow_rig/test_ride_geometry.py`。依据：`KatamariBallMotor.cs`（眼点系数字段）、`FollowRigMath.cs`、
> `BallMotor/README.md`「跟随架」。

## 0. 结论

1. **整套安排是球径的相似变换。** 眼点（球心上方 `up`、后方 `back`）与骑手眼高都是球半径的固定倍数，没有加性底座、没有地板：任何球径下看到的是同一幅画面
   （与球运动的"尺度不变"一致）。
2. **所有者 2026-11-17 的规则**：球小人小、球大人大，坐姿**腰部与球顶同高**，眼睛略高于球顶、能看见前面的路。眼高由 avatar 的视点设置决定，身体比例因 avatar 而异，
   所以**不假定任何人体比例**：入座后实测这个 avatar 的"视点到腰的竖直距离 ÷ 眼高" κ，再反解眼高。
3. **公式**（球半径 r = D/2，眼点 `up = 1.779558·r`）：眼在球顶之上 `up − r = 0.779558·r`，腰（Hips）在眼下 `κ·E`，腰落在球顶 ⇒ **`E = 0.779558·r / κ`**。
   座位的水平位置让视点落在眼点，竖直位置让 Hips 落在球顶。
4. **读不到腰时退回"只对齐眼睛"**：用入座时实测的视点偏移摆座位，不改骑手大小，不用任何固定人体比例（§3.5）。
5. **眼高连续跟随球径，但低通 + 限频**：5 Hz 低通、变化 ≥ 0.5 % 才发、两次请求 ≥ 0.1 s；座位不重测，按"入座时测一次的偏移 × 眼高比"现算。
6. **下车恢复原眼高**；世界关缩放时放弃缩放并按现状重测（§3.7）。
7. 向下看偏置只能写进 h/d 比（α），**不能给 rig 加 pitch**（§5）。

## 1. 被混为一谈的三件事

| # | 量 | 由什么控制 | 随 r 变？ |
|---|---|---|---|
| 1 | **玩家的身体在哪**（`VRCStation` 的 transform） | Station 是 rig 子物体；`SeatEyeCompensation` 写 `rigRoot.position + rigRoot.rotation * seatOffset` | 是（见 §3.4、§4） |
| 2 | **相机在哪**（眼点） | `FollowRigMath` 的公式 + motor 的 4 个系数，系数来自 `RideViewMath`，由装配器写入 | 是，两项都线性、无底座 |
| 3 | **玩家自己的眼高** | `BallRideStation` 经 `SetAvatarEyeHeightByMeters` 按 `RideScaleMath` 设置，`GetAvatarEyeHeightAsMeters` 读回 | 是，正比于球径 |

场景里没有 Camera 对象，相机就是 avatar 的头；Udon 能写的只有 Station 的 transform，于是 `眼点 = 座位 + 坐姿头偏移`，座位 = 眼点 − 头偏移，**两者数学上不可能相等**。

## 2. 取景规则（`RideViewMath`）

| 符号 | 值 | 来源 |
|---|---|---|
| v（垂直半 FOV） | 30° | **量出来的**：业主 2026-11-15 的"球心在画框下缘外 99 px"只在 v = 30°、球心 40.12° 时成立 |
| β（球的角半径） | v/3 = 10° | 设计意图"球占画面下三分之一"：球**直径** = 画面高度的 1/3 |
| α（球心在地平线下） | v − β − 2° = 18° | 2° 是下缘余量，轮廓不与下缘相切 |

```
up   = r·sinα/sinβ = 1.779558·r        back = up/tanα = 5.476916·r        到球心 = r/sinβ = 5.7588·r
```

球心恒在视轴下 18°、半角恒 10°：上沿 8°、下沿 28°；951x431 画面上球心行 336.8、球占 268..414 行（画面高 431）。motor 四个字段写成
`eyeBaseUpMeters = 0`、`eyeUpPerRadius = 1.779558`、`eyeBaseBackMeters = 0`、`eyeBackPerRadius = 5.476916`（`FollowRigMath` 不改）。

- **v 是唯一会被 parity 改掉的输入**：`Parity/PARITY_AUDIT.md` 的 `L03` 记录原作垂直 FOV = 37°；改 37° 只需把 `HALF_FOV_DEG` 换成 18.5（β = 6.17°、α = 10.33°，k_up = 1.6698），
  骑手尺度的公式（§3）跟着自动变（`up − r` 随 k_up 变）。
- 眼点没有加性底座：加性底座会破坏尺度不变；骑手随球变小，座位也随球缩放（§4）。
- 眼点高度护栏 `eyeUpMinMeters = 0.1` 只在球半径 < 0.056 m（D < 0.11 m）时才咬住。

## 3. 骑手身高（所有者 2026-11-17：球小人小、球大人大，坐姿腰部与球顶同高）

### 3.1 平台事实

下表是实现依赖的全部平台事实；表外的东西一律写"未核对"。

| 事实 | 内容 | 来源 |
|---|---|---|
| Udon 眼高夹界 | Udon 设置的 Target Eye Height 夹在 **0.1–100 m**；Action Menu 的可调范围是 0.2–5.0 m | [Avatar Scaling](https://creators.vrchat.com/avatars/avatar-scaling/)（官方） |
| 官方支持范围 | 官方只支持 0.1–100 m，超出时 HUD 显示警告；OSC 可写入 0.01–10000 m；世界里的 Udon 仍可强制缩放或限制范围 | [OSC Avatar Scaling](https://docs.vrchat.com/docs/osc-avatar-scaling)（官方） |
| 重新施加 | Udon 必须在 "avatar eye height changed" 事件里重新施加缩放，才能完全强制 | [Avatar Scaling](https://creators.vrchat.com/avatars/avatar-scaling/)（官方） |
| 眼高定义 | Prefab Height 是默认缩放下的 avatar 眼高；网站禁用缩放的世界里 Target Eye Height 始终等于 Prefab Height | 同上 |
| 缩放方式 | 缩放改的是 avatar 根节点的 `localScale` | 同上 |
| 世界关了缩放 | 网站上禁用缩放时，`SetManualAvatarScalingAllowed`、`SetAvatarEyeHeightMinimumByMeters`、`SetAvatarEyeHeightMaximumByMeters`、`SetAvatarEyeHeightByMeters`、`SetAvatarEyeHeightByMultiplier` 调用无效 | [Player Avatar Scaling](https://creators.vrchat.com/worlds/udon/players/player-avatar-scaling/)（官方） |
| 事件 | 换 avatar 触发 `OnAvatarChanged`，眼高变化触发 `OnAvatarEyeHeightChanged` | 同上 |
| 只对本地 | `SetAvatarEyeHeightByMeters` 只对本地玩家有效 | [SDK 文档镜像](https://www.matthewherber.com/VRCSDK-Documentation-Project/docs/doxygen/html/classVRC_1_1SDKBase_1_1VRCPlayerApi.html)（**非官方**） |
| 读骨骼的时机 | `PostLateUpdate` 在 IK 之后触发，此时读骨骼位置是最新的，不落后一帧 | [UdonSharp 事件](https://udonsharp.docs.vrchat.com/events/)、[VRChat 2021.3.1](https://docs.vrchat.com/docs/vrchat-202131)（官方） |
| 其它时机读到旧值 | 在 `PostLateUpdate` 之前读 `GetBone*` 拿到的是 IK 解算前的值；远端玩家在 `LateUpdate` 里读到错误位置、在 `PostLateUpdate` 里正确；远端玩家不在视野内时骨骼可能因剔除而不更新 | [反馈帖](https://feedback.vrchat.com/udon/p/remote-playerapi-gettrackingdata-and-getbonepositionrotationtransform-return-wro)、[Ask 论坛](https://ask.vrchat.com/t/for-using-trackingdata/27049)、[note.com 笔记](https://note.com/hiro_vrc/n/n599405f17b18)（均为**社区**） |
| 重新开启手动缩放 | 用 Udon 重新开启手动缩放后，玩家会被设回其原本或自行设定的身高，即使超出世界设定的上下限；所以骑乘期间不切换 `SetManualAvatarScalingAllowed` | [反馈帖](https://feedback.vrchat.com/open-beta/p/1312-activating-manual-scaling-after-setavatareyeheightbymeters-set-player-back)（**社区** bug 报告） |
| Animator 参数 | `EyeHeightAsMeters` 是 avatar 眼高（米），`ScaleFactor` 是当前高度与默认高度之比 | [Animator Parameters](https://creators.vrchat.com/avatars/animator-parameters/)（官方） |

由此定下的做法：**只读本地玩家**（远端剔除与远端读数问题与本方案无关）；视点与 Hips 都在 `PostLateUpdate` 读；眼高请求夹在 0.1–100 m；
在 `OnAvatarEyeHeightChanged` 里把非己方的眼高改回来；世界关缩放时放弃缩放、走"只对齐眼睛"。

**未核对，不作为事实使用**：眼高同步的量化精度；Station 的 `Seated` 对 avatar 动画层的影响；骨骼不存在时 `GetBonePosition` 返回什么（本机 Creator Library 的社区快照写返回
`Vector3.zero`，置信度 Medium，未经官方核对——代码对"恰好为原点"与"非有限值"都当作读不到）；ClientSim 里 `GetBonePosition` 的实现与坐姿 IK 是否与真实客户端一致；
缩放是否严格均匀、坐姿头/腰偏移是否严格正比于眼高；坐姿动画对 Hips 位置的影响；连续高频 Set 是否被限流；近裁剪面/音频/移动速度是否随眼高缩放；< 0.2 m 时 avatar 表现；
`Interact` 的 `proximity` 是否随缩放；固定 1.6 m 的面板与固定米数的下车落点在很小/很大的骑手下是否可用。任何人体尺寸数字都不作为依据写入。

### 3.2 "腰与球顶同高"的含义

所有者的话：坐姿下腰部与球顶同高，比例像屎壳郎推球，眼睛略高于球顶、能看见前面的路。取"腰 = Hips 骨"、"球顶 = 球心 + 语义半径"（与跟随架同一个半径）。
取景规则决定眼点在球顶之上 `0.779558·r`（= 0.39·D）：这就是"眼睛略高于球顶"的量；觉得过高或过低，改的是取景的 α/β（§2），不是本节。

### 3.3 κ 与公式

符号：r = D/2（`GetSemanticDiameterWorldM()/2`）；眼点离球心 `up`（§2）；E = 玩家眼高；κ = （视点到 Hips 的竖直距离）÷ E，**入座后按这个 avatar 实测**。

```
Hips 世界高度  = 眼点 − κ·E                          （视点落在眼点时）
Hips 世界高度  = 球心 + r = 球顶                      ← 所有者规则
⇒ κ·E = up − r = 0.779558·r
⇒ E = 0.779558·r / κ = 0.779558·D / (2κ)
```

**实测**（`SeatEyeCompensation`）：入座并稳定后，在 `PostLateUpdate` 里每帧读本地玩家的 `GetTrackingData(Head)`（视点）与 `GetBonePosition(Hips)`，都换算到 rig 坐标系、
相对座位，取一个窗口的平均。κ = (视点.y − Hips.y) ÷ 测量时的眼高（`RideWaistMath.TryMeasureKappa`）。读取与取平均沿用既有的"稳定窗口 → 取平均 → 冻结"。

**座位**：`SeatEyeMath` 的解是"座位偏移 = −头偏移"。有腰时，喂给它的"头偏移"把竖直分量换成 `Hips.y − 腰目标`（`RideWaistMath.EffectiveHeadInRig`），
水平分量仍是头的：座位水平位置让视点落在眼点，竖直位置让 Hips 落在球顶。"腰目标"= 球顶 − rig 根高度，每帧由 `SeatEyeCompensation` 从 motor 读球心与语义半径、
从 rig 读高度得到（`RideWaistMath.WaistTargetInRig`），所以球的任何滤波滞后都不会让腰离开球顶。眼高等于规则给的值时视点同时落在眼点；
眼高滞后或被夹界时腰仍在球顶，视点偏离眼点（§3.6）。

**眼高改变时的座位**：偏移与眼高成正比（均匀缩放的推断），座位 = 测量值 × 当前眼高 ÷ 测量眼高，现算，不重测；冻结的是测量，不是座位：球顶相对 rig 的高度每帧在变。
头部之后的任何动作（VR 头显移动）不会进入座位。

### 3.4 数值表（`ride_geometry.table()`；κ 取 0.2 / 0.35 / 0.5 只是说明 avatar 之间的离散，不是测量值）

| 球径 D (m) | κ | 玩家眼高 E | Hips 在眼下 κ·E | Hips 离球最低点 | 球心俯角 / 半角 / 下沿 |
|---:|---:|---:|---:|---:|---|
| 0.222 | 0.2 | 0.4327 | 0.0865 | 0.222 | 18° / 10° / 28° |
| 0.222 | 0.35 | 0.2472 | 0.0865 | 0.222 | 18° / 10° / 28° |
| 0.222 | 0.5 | 0.1731 | 0.0865 | 0.222 | 18° / 10° / 28° |
| 2 | 0.2 | 3.8978 | 0.7796 | 2.000 | 18° / 10° / 28° |
| 2 | 0.35 | 2.2273 | 0.7796 | 2.000 | 18° / 10° / 28° |
| 2 | 0.5 | 1.5591 | 0.7796 | 2.000 | 18° / 10° / 28° |
| 20 | 0.2 | 38.978 | 7.7956 | 20.000 | 18° / 10° / 28° |
| 20 | 0.35 | 22.273 | 7.7956 | 20.000 | 18° / 10° / 28° |
| 20 | 0.5 | 15.591 | 7.7956 | 20.000 | 18° / 10° / 28° |

读法：E/D = 0.3898/κ 对所有球径相同；Hips 在眼下 0.39·D、球顶在眼下 0.39·D，与 κ 无关；球心恒在视轴下 18°、下沿 28° < 30° 半视场（余量 2°）。
座位高度与小腿是否悬空取决于这个 avatar 的坐姿，**不在表里**，只能实测。

### 3.5 读不到腰：退回"只对齐眼睛"

Hips 读数满足全部条件才使用（`SeatEyeCompensation.ResolveWaist`）：窗口内**每一帧**都有读数、读数有限且不恰为原点（`RideWaistMath.IsUsableBonePosition`）、
离座位不比一个座上的人可能的距离更远（沿用 `SeatEyeMath` 的头距离护栏，随眼高缩放）、有 motor 可读球顶、有测量眼高（世界允许缩放）、κ 落在护栏 [0.05, 1]
（`MinKappa`/`MaxKappa`：护栏，不是产品值——Hips 与视点几乎同高不是腰，Hips 比视点低过整个眼高也不是坐姿腰）。任何一条不满足：

- 座位只对齐眼睛：用入座时实测的视点偏移（`SeatEyeMath` 原有的解与限幅），不用 Hips，不用任何固定人体比例；
- **骑手大小不动**（`BallRideStation` 不请求眼高；入座时存的原眼高在下车时仍会恢复）；
- 日志一行：`[ride] no usable waist (reason N)`，`N` 是 `RideWaistMath.Reason*`。

### 3.6 眼高更新：时机、平滑与限频

| 选项 | 内容 | 结论 |
|---|---|---|
| **连续 + 低通 + 阈值 + 间隔（采用）** | `E_target = 0.779558·r / κ`（夹 0.1–100 m）；5 Hz 低通；与上次发出值相差 ≥ 0.5 % 才发；两次发送 ≥ 0.1 s | 人随球平滑长大；球从 0.222 m 长到 20 m 全程约 900 次请求（ln(90.09)/ln 1.005 = 902） |
| 分档 | 少发、但每档一个大跳 | 否决：座位/眼点同时跳，VR 不舒服 |
| 每帧发 | 无抖动控制 | 否决：每次都触发 `OnAvatarEyeHeightChanged` 与网络同步，限流未核对 |

- **首次**：入座时眼高就是玩家原来的眼高 E₀；测量在 E₀ 上做，锁定后才第一次请求，所以入座后约 1 s 内人从 E₀ 经 5 Hz 低通缩到目标，座位在每次发送的同一帧按新眼高现算。
- **被改动后恢复**：`OnAvatarEyeHeightChanged` 读回值与己方发出值差 > 5 % 时，当作别人改的（玩家自己的缩放盘、换 avatar），重新施加己方值
  （官方要求 Udon 在这个事件里重新施加）；连续 3 次仍不是己方值就放弃，按现状重测座位；己方值回来时清零计数。测量中（尚无目标）的眼高变化只重测。
- **换 avatar**（`OnAvatarChanged`）：κ 重测。
- **抖动控制**：①阈值 0.5 %；②间隔 0.1 s；③低通 5 Hz，与 rig 高度滤波同速；④测量期间不发请求；⑤己方请求回来的事件不重测。
- **一帧错位**：座位在发请求的同一帧移动，而 VRChat 缩放 avatar 可能晚一帧生效，会有一帧 ≤ 0.5 % × 头高的头位错位；由验收清单的 `eyeError` 日志核对。
- **座位写在 `PostLateUpdate`**：与读取同一回调，IK 之后。坐在 Station 里时位置对齐发生在何时、座位在 `PostLateUpdate` 写入是否落后一帧，未核对，列入验收。
- 假设（未核对）：均匀缩放、坐姿头/腰偏移正比于眼高。`GetCurrentEyeErrorMeters` 读视点到眼点的实时误差，是这个假设的检验。

### 3.7 上下限与放弃路径

- 请求夹在 0.1–100 m（`RideScaleMath.TryResolveTarget`）。E = 0.779558·D/(2κ)：κ = 0.35 时 **D = 0.09 m 起**能精确匹配（更小的球 E 被钳在 0.1 m，骑手比规则大，
  腰在球顶之上而不是上面，`clamped` 为真并记日志；腰仍按球顶摆座位，视点偏离眼点）；球径上限 20 m 在 κ = 0.2 时 E = 39 m，不会咬到 100 m。
- 小于 0.1 的原作 5 cm 起始球（MAS1）会被钳制，该关另议。
- 绝对护栏随眼高缩放：`SeatEyeMath.MaxPlausibleHeadAboveSeatMeters = 1.35`、`verticalLimitMeters = 1.3`、`horizontalLimitMeters = 0.35` 在 `ReferenceEyeHeightMeters = 1.64`
  下制定，乘 `E/1.64`（`LimitScaleForEyeHeight`）。**有腰时座位的竖直不限幅**（头数据已在测量时审过，且眼高滞后时腰目标可能远低于眼）；水平仍限幅。
- 世界关缩放：读回眼高与发出值偏离超过 5 % 持续约 3 s → 放弃缩放，按实际眼高重测座位；此时眼高为 0，没有 κ，走 §3.5。
- **设计约束：骑乘期间不切换 `SetManualAvatarScalingAllowed`**（重新开启手动缩放会把玩家设回自己的身高，§3.1），玩家自己的缩放权不动；骑乘中玩家拖缩放会被改回（连续 ≤ 3 次），之后按其所选重测。

### 3.8 时序：入座、校准、跟随、下车

1. **入座** `OnStationEntered`：读原眼高存下（尚不请求）；以这个眼高开始测座位。
2. **测量** `SeatEyeCompensation`：`SetEyeHeight(E₀)` → `BeginSeated()`；稳定窗口、取平均（视点与 Hips）、座位滑到位后锁定，同时判定 κ 是否可用（§3.5）。
3. **校准**：锁定后 `HasWaist()` 为真则取 `GetKappa()` 开始跟随；为假则骑手大小不动，座位只对齐眼睛。
4. **跟随**：每帧低通 + 限频后发请求、`ApplyEyeHeight` 移座位；锁定后座位每帧按球顶重算竖直位置。
5. **异常**：读回眼高与发出值偏离 → 重新施加或放弃（§3.7）；换 avatar → 重测 κ。
6. **下车 / 重生**：`OnStationExited`/`OnPlayerRespawn` 先复位座位再 `SetAvatarEyeHeightByMeters(存值)`（`RideScaleMath.RestoreTarget` 夹到 0.1–100）。

### 3.9 已知后果（需要所有者知道）

- 骑手大小因 avatar 而异：E = 0.779558·D/(2κ)，κ 小的 avatar（视点离腰近）更大。球 20 m 时人 15–39 m（κ 0.5–0.2）。
- 入座与每次成长都改变 avatar 的世界尺度：声音/近裁剪面/手部交互的缩放行为未核对。
- 面板（HUD、结果卡、下车面板）仍是距眼 1.6 m 的世界空间面板（§6），**没有随骑手缩放**：角度大小不变，但小骑手时它们落在球后 1 m 外的世界里（可能被道具遮挡），
  大骑手时它们在巨人面前 1.6 m 处。下车仍可用按住跳跃键（不依赖面板）。是否让面板跟骑手缩放，需要实机看过再定。

## 4. 座位与触发器几何

1. **座位永远不在球上**：座位水平位置让视点落在眼点，在球后约 `back` = 5.4769·r（球面外 4.477·r = 2.24·D）；竖直位置由 Hips 落在球顶决定，所以座位是浮空的，具体高度取决于 avatar 的坐姿（实测）。
   座位贴到球顶会让相机在球正上方，球落在视轴下 74°..90°（"坐在球上"与"追尾取景"互斥）。要让人"有东西可坐"就把座位**画出来**（`SeatPlate`，挂 Station 下，尺寸随骑手缩放），不是把座位搬回球心。
2. **触发器在球上，随球缩放。** `BallSeatTrigger` 挂在 motor 的 `ballPhysicsRoot` 下，单位球 `SphereCollider`（半径 0.5、trigger），`BallSeatInteract.Update` 把 scale 写成
   `2 × RideViewMath.SeatTriggerRadius(collisionRadius)`；半径 = `1.25 × 碰撞半径`，下限 0.25 m。座位自己的 0.45 m 盒子在眼点上、不在球上，装配器把它删掉。
3. **退出路径不依赖座位位置**：按住跳跃键 1.5 s，或骑乘者面板上的按钮点两次。两条都进 `BallRideStation.RequestExit`，先把 VRCStation 的 exit location 放到球旁可站立的地面再 `ExitStation`。
4. **球侧的交互必须自成一个行为**：`Interact` 只送到被点到 Collider 所在物体的 UdonBehaviour，Station 回调只送到 VRCStation 所在物体；座位与球不在一处，所以点球的是 `BallSeatInteract`，转发给 `BallRideStation.TrySeatLocalPlayer`。

## 5. 桌面 vs VR

- **实测**：桌面/ClientSim 出生时 **pitch = 0**，只应用 spawn yaw；**VR** 的 head pitch 是玩家的。
- **规则：不给 rig 加 pitch，把偏置写进 α（h/d 比）。** `ApplyPresentation` 只写 `Quaternion.Euler(0, yaw, 0)`，pitch 会把 Station 连同坐姿 avatar 掀歪；VR 的视线由头决定；`FollowRigMath` 的契约是"只有位置 + yaw"。

## 6. 面板与下车（现行）

所有骑乘期可见的 UI 都是挂在 rig 根（眼点）下的世界空间 Canvas，位置用 `RideViewMath.HudLocalPosition`（距眼 1.6 m、方位角、仰角）给出，朝向用 `HudLocalRotation`：

| 面板 | 方位 / 仰角 | 内容 |
|---|---|---|
| `StoryHud`（原屏幕叠加 Canvas） | 左 30° / 上 26°（左上角锚点） | 目标、当前、剩余、起始提示 |
| `RideResultCard` | 正前 / 上 6° | 结果文字 + 卡面 + 「再试一次」 |
| `RideExitPanel` | 右 24° / 下 8° | 「下车（点两次）」按钮 + 「按住跳跃键下车」；仅入座时显示 |

跟 rig 而不跟球：rig 是已滤波、只带 yaw 的眼点。屏幕空间 Overlay Canvas 不被 VRChat 客户端绘制，所以不用。可点击用 `Interact`（触发盒 + 左键/VR 扳机）。

下车落点（`RideExitMath`）：以球心为心、球面外 0.9 m，按球朝向的 后/右/左/前 顺序各向下射一条射线，第一个法线 y ≥ 0.7、高度在球底面 +0.6 m 与 −1.5 m 之间的点胜出，抬 5 cm，朝向球；四个都不行则保留场景里写的 exit location 并报 warning。
场景里的 exit location 由 `RideUxAssembler` 在 VRCStation 没有时创建（子物体 `RideExitPoint`，位置按同一规则取第一个候选点的平地落点，`RideExitMath.TryStandPose`），重复装配不改动已有的。
（`0.9 m` 与 `0.6/−1.5 m` 是绝对米数，球径 20 m 时落在球面外 0.9 m 仍可站，但是否合适属于下车几何，未随尺度重算。）

## 7. 未运行 / 未验证

Unity 编译、U# 编译（`PostLateUpdate` 覆写、`HumanBodyBones` 读 Hips）、EditMode 实跑；真实客户端：Hips 与视点在 Station 里是否可读且正确（含 ClientSim 与真实客户端是否一致）、
κ 的实际数值、均匀缩放与偏移正比于眼高、眼高设置与恢复、连续高频 Set 的开销与是否限流、近裁剪面与音频、一帧错位的可见度、世界关缩放时的放弃路径、
面板在很小与很大骑手下的可读性与可点性、下车落点、VR 全身追踪下 Hips 的表现。
`unittest discover -s Docs/tests` 与 `validate_plan.py` 只验证几何模型与规划数据。

## 8. 验收清单

**给 Unity 操作员**（按 `Assemble Ride UX` → 编译 → 存盘 → 重开场景 → 再装配一次）：
1. 装配器报告 0 PROBLEM；motor 的 `eyeBaseUpMeters`/`eyeBaseBackMeters` = 0、`eyeUpPerRadius` = 1.779558、`eyeBackPerRadius` = 5.476916；`BallRideStation.scaleRiderWithBall` = true、`seatEye` 已连；
   `SeatEyeCompensation.motor` 已连（装配器写入，U# 变量表要重开场景后才有这个字段）。
2. `run_tests`：`VRCKatamari.Tests.EditMode` 全过（含 `Riding` 的 RideScale / RideWaist / RideView / SeatEye）；U# 全量编译无新错误（尤其 `SeatEyeCompensation.PostLateUpdate`）。
3. ClientSim 入座，读 Console：`[seat] locked`（确认 `PostLateUpdate` 在 ClientSim 里触发）、`[ride] seat calibrated: kappa …` 或 `[ride] no usable waist (reason N)`；记下 κ 与原因码。
4. 把球径增到 ≈ 0.5 / 0.8 / 2 m，读 `[ride] eye height … diameter … eyeError …`：`eyeError` 应保持在 ~几 cm 内（< 0.02·E）。**若 eyeError 随眼高线性增大，说明"偏移 ∝ 眼高"假设不成立**，报告此数据。
5. 退出后 `[ride] eye height restored to <原值>`；重复入座/下车 3 次，眼高回到原值、座位回到装配位置。
6. 世界关缩放（网站设置）时：约 3 s 后出现 `eye height does not follow the requests` 警告，座位按实际眼高测量。

**给所有者亲测**（桌面 + 头显各一次，用多个比例不同的真实 avatar；ClientSim 里的骨骼读数不能代表真实客户端）：
1. 刚坐下：腰线（Hips）是否与球顶齐平？眼睛是否略高于球顶、能看见前面的路面？球是否在画面下方三分之一？
2. 换到比例明显不同的 avatar（腿长、短身、非标准体型）：腰线是否仍与球顶齐平？非 humanoid 或没有 Hips 的 avatar：是否按 §3.5 只对齐眼睛、大小不变？
3. 球变大时：人是否**平滑**变大？吃一件大东西后是否有一瞬视角抖动或"世界在缩放"感？
4. 骑乘中手动改眼高（Action Menu 的缩放）：是否在一两帧内被改回？连续改是否出现卡顿或视点漂移？
5. VR 全身追踪：Hips 追踪器是否把腰拉离球顶？（实测后座位冻结，之后头与臀怎么动都不应拖动世界。）
6. 有无眩晕、近裁剪面切掉物体、声音音调/音量随人变大突变？球径达到 ~2 m 与更大（~10 m 以上）时，球顶是否仍与腰同高、面板能否看清、能否点到下车按钮。
7. 下车：眼高是否回到你原本的大小，落点是否站得住（20 m 球旁尤其注意）。
8. 坐姿 avatar 的腿是否悬空；是否可接受，或需要画出座板。
