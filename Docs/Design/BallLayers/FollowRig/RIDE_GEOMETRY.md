# 骑球取景、眼点与座位几何（RIDE_GEOMETRY）

> 状态：几何推导与补丁提案，**未编译、未在 Unity/Play/VR 运行**，本文件不修改任何 C#。依据：
> `KatamariBallMotor.cs:250-257,3401-3419,3421-3430`、`FollowRigMath.cs:81-141`、
> `BallRideStation.cs:97-111,184-189`、`SeatEyeCompensation.cs:53-95,434`、`BallMotor/README.md`
> 「跟随架」、`StoryMode/README.md:98-102`、`VRC_WORLD_SIZE_LIMITS.md` §6.2/§6.3。数字唯一来源：
> `ride_geometry.py`（运行即打印 §2.2 表），测试 `Docs/tests/follow_rig/test_ride_geometry.py`（14 例）。

## 0. 结论

1. **今天的 bug 是常数选错，不是「随半径变化」。** α(r)=atan((0.55+2.4r)/(0.75+2.2r)) 是 r 的
   Möbius 函数，被夹在 **36.25°..47.49°**，恒大于 30°（60° 垂直 FOV 的下缘），所以球在任何尺寸都在画外。
   r=0.15 的 **40.12°** 经针孔投影正好是 431 px 画面下缘**下方 99.0 px**，与业主实测逐像素吻合。
2. 新模型：**α 恒为 18°、β 上限 10°**，`h = max(1.779558·r, 1.254159 − r)`、`d = h/tan18°`；球心
   像素行在 0.15..15 m 全是 **336.8 px**（431 行画面），即"位置不变、只变大小"。
3. **座位永远不在球上，也不在眼点上**：座位 = 眼点 − 实测坐姿头高；要让人"有东西可坐"就把座位画
   出来、把触发器罩在球上，不是把座位搬回球心。
4. 玩家眼高只能进**眼点下限**（`平台 + 实测头高`）；现行绝对护栏（1.35/1.3 m）会整条拒绝高个子
   玩家，从而**复现业主的 bug**（α 变 46.7°）。
5. 向下看偏置只能写进 h/d 比（α），**不能给 rig 加 pitch**：rig 只写 yaw（`:3428`），加 pitch 只会
   掀歪坐姿 avatar，转不动 VR 的视线。

## 1. 被混为一谈的三件事

| # | 量 | 今天由什么控制（file:line） | 随 r 变？ |
|---|---|---|---|
| 1 | **玩家的身体在哪**（`VRCStation` 的 transform） | Station 是 rig 子物体、`localPosition (0,0,0)`（`StoryMode/README.md:100`）；`SeatEyeCompensation.WriteSeat` 写成 `rigRoot.position + rigRoot.rotation * seatOffset`（`SeatEyeCompensation.cs:434`），`BallRideStation.seatEye`（`:97`）是唯一调用方 | 间接（父节点是 rig） |
| 2 | **相机在哪**（眼点） | `FollowRigMath.cs:134-141` 的公式 + motor 的 4 个系数（`KatamariBallMotor.cs:250-257`），由 `UpdateFollowRig`（`:3401`）喂入 | 是，两项都线性 |
| 3 | **玩家自己的眼高** | `GetTrackingData(Head)`（`SeatEyeCompensation.cs:449+`）、`OnAvatarEyeHeightChanged`（`BallRideStation.cs:184`）；世界侧**从不设置** | 否 |

**今天三者被压进一个 rig 根**：它同时是（a）眼点、（b）座位的父坐标系与布点原点、（c）触发盒的
落点。已经分开的只有 (1) 与 (2)：2026-11-15 的 `SeatEyeCompensation` 按实测头高把座位反推回去。
业主实测的"0.45 m 盒子悬在胸口、球 +0.91..1.06 上、1.08 后"正是 `BallRideStation.cs:106-111` 那行
`[ride] no seat-eye compensation wired` 的形态——**补偿没接**，所以座位 = 眼点 = 球后上方的空中。

**为什么三者不可能重合**：场景里没有 Camera 对象，相机就是 avatar 的头；Udon 能写的只有 Station 的
transform，于是 `眼点 = 座位 + 坐姿头偏移` ⇒ 座位必须 = 眼点 − 头高，**两者数学上不可能相等**。

## 2. 正确模型（推导，不是挑选）

| 符号 | 值 | 来源 |
|---|---|---|
| v（垂直半 FOV） | 30° | **量出来的**：业主的 99 px 只在 v=30°、球心 40.12° 时成立 |
| β（球的角半径上限） | v/3 = 10° | 设计意图"球占画面下三分之一" = 球的**直径** = 画面高度的 1/3 |
| α（球心在地平线下） | v − β − 2° = 18° | 2° 是下缘余量：轮廓不许与下缘相切（滤波过冲与 float 舍入远小于 2°） |
| E（眼点离地下限） | 0.05 + 1.204159 = **1.254159 m** | 座位平台 0.05 + 本仓实测最高坐姿头高；人身体逼出来的，不是审美 |

### 2.1 闭式

```
h_frame(r) = r·sinα/sinβ = 1.779558·r        d_frame(r) = r·cosα/sinβ = 5.476916·r
h(r)       = max(h_frame(r), E − r)          d(r)       = h/tanα = 3.077684·h
L(r)       = √(h²+d²) = h/sinα ≥ r/sinβ = 5.758770·r    ⇒ 相机永远在球外
β(r)       = asin(r·sinα/h) ≤ β             （r ≥ E/(1+k) = 0.451208 m 时取等）
```

`d` **由 h 导出**，不再有独立的后退系数——这是修 40° 的关键（§2.3）。副作用是护栏只能改"球多大"、
改不了"球在哪"：钳 h 后 d 跟着变；钳后退时 h 同比缩放；α 都不变。

### 2.2 表（`ride_geometry.table()`，951x431、v=30°）

| r (m) | D (m) | 上方（离球心） | 后退 | 到球心 | 眼离地 | α | 球直径角 | 球心像素行 | 球占行 | 座位离地 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| 0.15 | 0.30 | 1.1042 | 3.3983 | 3.5731 | 1.2542 | 18° | 4.81° | 336.8 | 320..354 | 0.050 |
| 0.30 | 0.60 | 0.9542 | 2.9366 | 3.0877 | 1.2542 | 18° | 11.15° | 336.8 | 298..378 | 0.050 |
| 0.55 | 1.10 | 0.9788 | 3.0123 | 3.1673 | 1.5288 | 18° | 20.00° | 336.8 | 268..414 | 0.325 |
| 1.10 | 2.20 | 1.9575 | 6.0246 | 6.3346 | 3.0575 | 18° | 20.00° | 336.8 | 268..414 | 1.853 |
| 5.00 | 10.0 | 8.8978 | 27.385 | 28.794 | 13.898 | 18° | 20.00° | 336.8 | 268..414 | 12.694 |
| 15.0 | 30.0 | 26.693 | 82.154 | 86.382 | 41.693 | 18° | 20.00° | 336.8 | 268..414 | 40.489 |

- 0.55 m 起球占 268..414 行 = 画面高度 33.9%，下缘留 17 px（2°）——"下三分之一"；球上沿在视轴下
  8°，桌面准星始终指向球前方（`α > β` 就是"球不挡准星"）。
- 0.15/0.30 m 落在地板分支：球**更小但仍在同一行**，这是人形骑手骑 0.30 m 球的几何必然（§3）。
- **v 是唯一会被 parity 改掉的输入**：`Parity/PARITY_AUDIT.md` 的 `L03` 记录原作垂直 FOV = 37°，此表按当前
  60° 打印。采纳 37° 只需 `Frame(half_fov_deg=18.5)`：β=6.17°、α=10.33°、k_up=1.6698、k_back=9.1582
  （同一相对行 77%，相机更低更远）——α/β/地板之外的数一个都不用改。

### 2.3 为什么今天的常数给出"恒定 40°"（代数）

`h = a + b·r`、`d = c + d·r`（a=0.55,b=2.4,c=0.75,d=2.2），于是

```
α(r) = atan((a + b·r)/(c + d·r))                 # r 的 Möbius 函数
α(0)=atan(a/c)=atan(0.55/0.75)=36.25°   α(∞)=atan(b/d)=atan(2.4/2.2)=47.49°
b·c − a·d = 2.4·0.75 − 0.55·2.2 = 0.59 > 0   # 单调递增 ⇒ 36.25° < α < 47.49°
```

**两个斜率几乎相等（2.4/2.2 = 1.09），比值几乎不随 r 变，角度被自动钉死在 40° 附近。** "角度恒定"
本身是**对的**（设计意图就是"球始终在画面同一处"），错的是那个常数：40.12° > 30°，球在任何尺寸都在
下缘之外；四档球心行 530.0 / 551.7 / 571.6 / 591.1 px（画面只有 431 行）。

历史：2026-11-15 把常量眼高 1.6 换成"后上方、两项线性"时，系数是为**净空**选的（`h − r` 要随球变大：
0.62/0.76/1.05/1.55 m），从没人为取景解过。`BallMotor/README.md` 那句"球本身也始终在画内"算的是
**吸附壳**上沿（20.4°..32.7°），不是可见球体（34.0°..31.0°，四档都超出下缘）。修法不是调四个数，而是
把 α 当唯一真源、让 h 与 d 从它导出（§7 H1）。

## 3. 玩家身高耦合

机制：Station 承载 avatar 的**根**，头 = 座位 + 坐姿头偏移（本仓只确认偏移量：ClientSim **1.204159 m**、
真机约 **0.85 m**）。玩家自缩放眼高 **0.2–5.0 m**；Udon 设置眼高**接受 0.01–10000 m**、官方支持
**0.1–100 m**；**碰撞体不缩放**（`VRC_WORLD_SIZE_LIMITS.md` §S2/S3/S4）。玩家眼高是唯一不受控量，规则三条：

1. **球的几何只用世界米**，不读玩家眼高：0.15 m 的球在 1.7 m 与 5 m 玩家眼里是同一个球。
2. **座位 = 眼点 − 实测头高**（`SeatEyeCompensation` 已在做）。
3. **眼点下限 = 平台 + 实测头高**：唯一允许随玩家变的量（§7 H4 接进 motor）。

| 玩家眼高 | 坐姿头高 | 眼点下限 | crossover | r=0.15 的 β | 结果 |
|---:|---:|---:|---:|---:|---|
| 0.2 m | 0.142 | 0.192 | 0.069 | 10° | 全程 framing 分支，座位离地 0.275 m：**小人浮在台子上**（第一人称正确，第三人称像玩偶） |
| 1.7 m | 1.204 | 1.254 | 0.451 | 2.41° | §2.2 的表 |
| 5 m | 2.50 | 2.55 | 0.917 | 1.11° | 球在同一行、仍在画内；r≤0.9 时球只占 2.2°/60°，座位正好在平台高度 |

**会坏的地方（必须改）**：`SeatEyeMath.MaxPlausibleHeadAboveSeatMeters = 1.35` 与
`verticalLimitMeters = 1.3` 是**绝对米**；坐姿头高 ≈ 0.5–0.71 × 眼高，所以眼高 > 约 **1.9–2.6 m** 的玩家
会被整条拒绝（reason=6），座位留在眼点上、相机高出 2.5 m → α 从 18° 变 **46.7°**、球再次出画（旧模型
72.4°）。**高个子玩家能一键复现业主的 bug。** 护栏须改相对式（`头高 ≤ 0.8 × GetAvatarEyeHeightAsMeters()`），
绝对天花板抬到 3.6 m；0.2 m 极端只坏第三人称观感。

## 4. 座位与触发器几何

1. **座位永远不在球上。** 座位 = 眼点 − 头高：r=0.15 离地 0.05 m、球后 3.40 m；r=1.10 离地 1.85 m、
   球后 6.02 m。要让人"有东西可坐"就把座位**画出来**（`SeatPlate` 挂 Station 下）；座位一贴到球顶，
   相机就在球正上方，球落在视轴下 74°..90°（"坐在球上"与"追尾取景"互斥）。
2. **触发器必须在球看起来在的地方，并随球缩放。** 规则：**交互半径 = 1.10·r + 0.10 m**，球心为心：
   `coreMesh` 的子物体 + `SphereCollider radius 0.60`（`BallView` 目前把 `coreMesh.localScale` 写成直径
   ⇒ 世界半径 = 0.60 × 2r = 1.2r；若该缩放迁到 `BallPresentationMath`，触发器按同一比例跟），
   **r=0.15 → 0.18 m**（球面 0.15，10% 余量，准星可点）、**r=1.10 → 1.32 m**。固定 0.45 m 盒子只在
   某一档球径上罩住球（`StoryMode/README.md:102` 已承认）。
3. **退出按钮必须跟着 rig 走**：座位在球后 3.4..6.0 m，场景里放死的按钮点不到；挂 rig 根下
   `localPosition (0,−0.30,1.00)`。
4. **球侧的交互必须自成一个行为**：`Interact` 只送到被点到 Collider 所在物体的 UdonBehaviour，Station
   回调只送到 VRCStation 所在物体（`StoryMode/README.md` 必需布局 3）；座位与球不在一处，一个物体服务
   不了两边——需要 15 行 `BallSeatInteract`（§7 H6），与已验证的 `BallRideExitButton` 同形。

## 5. 桌面 vs VR

- **实测**：桌面/ClientSim 出生时 **pitch = 0**，只应用 spawn yaw；**VR** 的 head pitch 是玩家的。
- **规则：不给 rig 加 pitch，把偏置写进 α（h/d 比）。** (a) `ApplyPresentation` 只写
  `Quaternion.Euler(0, yaw, 0)`（`:3428`），pitch 会把 Station 连同坐姿 avatar 掀歪；(b) VR 的视线由
  头决定，rig 的 pitch 转不动相机、只转身体；(c) `FollowRigMath` 的契约是"只有位置 + yaw"。
  所以"向下看的 bias" = 选一个 **α**，不是旋转。桌面 pitch=0 时球正好落在 §2.2 那一行。

## 6. 迁移

### 6.1 常数 before / after

| 字段（`KatamariBallMotor`） | 今天 | 新 | 影响 |
|---|---:|---:|---|
| `eyeBaseUpMeters` / `eyeUpPerRadius` | 0.55 / 2.4 | **删** | 由 α/β 推出（h/r = sinα/sinβ = 1.779558） |
| `eyeBaseBackMeters` / `eyeBackPerRadius` | 0.75 / 2.2 | **删** | d 不再有常量项（d/r = cosα/sinβ = 5.476916） |
| `eyeFrameAngleDegrees` | — | **18** | α：相机俯视角的唯一真源 |
| `eyeBallHalfAngleDegrees` | — | **10** | β：球直径 = 画面 1/3 |
| `eyeFloorAboveGroundMeters` | — | **1.254159** | 人身体下限（座位 0.05 + 实测头高），骑乘层可改写 |
| `eyeUpMinMeters` | 0.1 | 0.1 | 护栏，永不触发（r=0.5 时 h=0.75） |
| `eyeUpMaxMeters` | 8 | **40** | r=15 需 26.69；8 m 会把 22.6 m 以上的球削平 |
| `eyeMaxBackM` | 8 | **210** | r=15 需 82.15 |

`Parameters/README.md` 第 16 行与 §6.4 护栏表同步（4 个眼点系数 → α / β / 眼点下限；两条 8 m 护栏 →
40/210）。顺带作废 `VRC_WORLD_SIZE_LIMITS.md` §6.2 的"22 m 上限"：新模型 `L ≥ 5.76·r`，相机永远在球外。

### 6.2 顺序

1. `FollowRigMath.TryUpdate`（H1：签名 + 公式 + 拒绝条件）——**与另一位 agent 无关**。
2. `follow_rig.py` 同名同序改（H3）+ `Docs/tests/ball_follow_rig` 9 例改期：四档 0.67/0.91/1.414/2.266
   与 0.86/1.08/1.542/2.323 **全部作废**，换成 §2.2 的 up/back。
3. `KatamariBallMotor.cs` 字段区（H2）+ `UpdateFollowRig` 调用点（H5）+ `.asset` 的序列化键（旧键失配
   后回落到新默认值，属清理，不是前提）。
4. `Tests/EditMode/BallLayers/FollowRigTests.cs` 重写（§2.2 表 + α>β 拒绝）；场景 7 步（§7 末尾）。
5. 回归测量（§6.3）→ `SeatEyeMath` 相对护栏（H7，不改取景）。

### 6.3 回归测量（Play，owner）

| 时机 | 读数 | 期望 |
|---|---|---|
| spawn（r=0.15） | rig (up, back)、球像素行（951x431、60° FOV） | (1.1042, 3.3983) ±0.01；球心 337、球占 320..354 行，**不得**"整球在下缘之外" |
| 滚动中 / 直径 0.60 / 1.10 / 2.20 | (up, back) 与球心行 | 0.9542/2.9366、0.9788/3.0123、1.9575/6.0246；球心行**全部 337**、波动 ≤ ±2 px |
| 15 m（放开 `maxCoreRadiusWorldM` + 空场景）/ 换 0.2 m、5 m avatar | 球心行、相机在球外、`[seat]` 日志 | 337 行、L=86.4>15；换 avatar 后行不变，补偿量 = 实测头高 |

## 7. 补丁提案（不修改文件，交给应用者）

**独立性**：H1、H3、H6 与另一位 agent 的文件无关；H2/H5 与正在被编辑的 `KatamariBallMotor.cs` 同文件
但**不同区域**（字段区 250-258、调用点 3401-3419），合并预期干净；H4 在 `BallRideStation.cs`，H7 在
`SeatEyeMath`/`SeatEyeCompensation`。**H1/H3 可立刻单独落地**（Python 与 C# 须同一次提交）。

### H1 `Runtime/BallLayers/FollowRig/FollowRigMath.cs`（独立）

```csharp
// BEFORE  (:81-87 参数, :134-141 公式)
float eyeBaseUpMeters, float eyeUpPerRadius, float eyeUpMinMeters, float eyeUpMaxMeters,
float eyeBaseBackMeters, float eyeBackPerRadius, float eyeMaxBackM,
float targetHeight = eyeBaseUpMeters + ballRadiusM * eyeUpPerRadius;
targetHeight = Mathf.Clamp(targetHeight, eyeUpMinMeters, eyeUpMaxMeters);
float back = eyeBaseBackMeters + ballRadiusM * eyeBackPerRadius;
if (back < 0f) back = 0f;
if (back > eyeMaxBackM) back = eyeMaxBackM;

// AFTER - alpha is the ONLY source of the angle; back is DERIVED from height, so no
// coefficient pair can drift it (the 40 deg bug) and a rail resizes, never moves, the ball.
float eyeFrameAngleDegrees, float eyeBallHalfAngleDegrees, float eyeFloorAboveGroundMeters,
float eyeUpMinMeters, float eyeUpMaxMeters, float eyeMaxBackM,
float frameRadians = eyeFrameAngleDegrees * Mathf.Deg2Rad;
float targetHeight = (Mathf.Sin(frameRadians) / Mathf.Sin(eyeBallHalfAngleDegrees * Mathf.Deg2Rad))
    * ballRadiusM;
float floorHeight = eyeFloorAboveGroundMeters - ballRadiusM;
if (floorHeight > targetHeight) targetHeight = floorHeight;
if (targetHeight < 0f) targetHeight = 0f;
targetHeight = Mathf.Clamp(targetHeight, eyeUpMinMeters, eyeUpMaxMeters);
float back = targetHeight / Mathf.Tan(frameRadians);
if (back > eyeMaxBackM)   // keep alpha: scale both, then hold the height rail
{
    targetHeight = targetHeight * (eyeMaxBackM / back);
    back = eyeMaxBackM;
}
```

校验（现有 `IsFiniteFloat` 序列里，全部 `return false`）：α、β ∈ (0,90)、`β < α`（球不得越过地平线）、
`eyeFloorAboveGroundMeters ≥ 0`。

### H2 `KatamariBallMotor.cs` 字段区（:250-257，同文件需合并）

```csharp
// AFTER - derived in ride_geometry.py (Frame): beta = halfFov/3, alpha = halfFov-beta-2deg,
// floor = 0.05 + measured seated head.
[SerializeField] private float eyeFrameAngleDegrees = 18f;
[SerializeField] private float eyeBallHalfAngleDegrees = 10f;
[SerializeField] private float eyeFloorAboveGroundMeters = 1.254159f;
[SerializeField] private float eyeUpMinMeters = 0.1f;
[SerializeField] private float eyeUpMaxMeters = 40f;    // r = 15 m needs 26.7
[SerializeField] private float eyeMaxBackM = 210f;      // r = 15 m needs 82.2
public void SetEyeFloorAboveGroundMeters(float floorM)
{
    // The seated head is the one length the world cannot control (avatar scale 0.2-5 m), so the
    // ride layer owns this input; it never writes ball state, ledger or the frame itself.
    if (KatamariMath.IsFiniteFloat(floorM) && floorM >= 0f) eyeFloorAboveGroundMeters = floorM;
}
```

### H5 `UpdateFollowRig` 调用点（:3406-3411）

```csharp
// BEFORE: dt, eyeBaseUpMeters, eyeUpPerRadius, eyeUpMinMeters, eyeUpMaxMeters,
//         eyeBaseBackMeters, eyeBackPerRadius, eyeMaxBackM
// AFTER:  dt, eyeFrameAngleDegrees, eyeBallHalfAngleDegrees, eyeFloorAboveGroundMeters,
//         eyeUpMinMeters, eyeUpMaxMeters, eyeMaxBackM
```

### H4 `Runtime/Riding/BallRideStation.cs`（新增 `motor` 引用 + 三处发布）

```csharp
[SerializeField] private KatamariBallMotor motor;   // new: the eye floor's only writer
[SerializeField] private float seatPlatformAboveGroundMeters = 0.05f;
// after seatEye.BeginSeated() in OnStationEntered / OnAvatarChanged / OnAvatarEyeHeightChanged:
PublishEyeFloor();
// after seatEye.EndSeated() in OnStationExited / OnPlayerRespawn: restore the authored floor
motor.SetEyeFloorAboveGroundMeters(seatPlatformAboveGroundMeters + DefaultSeatedHeadMeters);
private void PublishEyeFloor()
{
    if (motor == null || seatEye == null) return;
    float head = seatEye.GetMeasuredEyeAboveSeatMeters();   // 0 until measured
    if (head <= 0f) return;
    motor.SetEyeFloorAboveGroundMeters(seatPlatformAboveGroundMeters + head);
}
```

### H6 新文件 `Runtime/Riding/BallSeatInteract.cs`（~15 行，独立）

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BallSeatInteract : UdonSharpBehaviour
{
    [SerializeField] private VRC.SDK3.Components.VRCStation station;
    public override void Interact()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player != null && station != null) station.UseStation(player);   // like BallRideExitButton
    }
}
```

### H7（高个子玩家必需）`SeatEyeMath` / `SeatEyeCompensation`

`MaxPlausibleHeadAboveSeatMeters`（常量 1.35）改成入参上限 `0.8 × GetAvatarEyeHeightAsMeters()`，
`verticalLimitMeters` 默认 1.3 → **3.6**。不改：眼高 > 约 1.9–2.6 m 的玩家补偿被拒、α 变 46.7°（§3）。

### 场景改动（Unity 操作者，7 步）

1. `FollowRig` 根仍是 motor 的 `followRigRoot`，别动；Station 仍是 rig 子物体、`localPosition (0,0,0)`。
2. `SeatEyeCompensation` 接到 Station：`followRigRoot` = rig 根、`seatAnchor` = Station 的 transform、
   `settleFrames 12`、`sampleFrames 8`、`verticalLimitMeters 3.6`（H7 落地后）。
3. **删掉 Station 上那个 0.45 m 的 BoxCollider**（业主点到的就是它，它在眼点上）。
4. 球侧：`coreMesh` 下建空子物体（local 0、scale 1），加 `SphereCollider radius 0.60`、
   `isTrigger ✓`、加 `BallSeatInteract`（`station` → 那个 VRCStation）。r=0.15 时世界半径应为 0.18 m。
5. `SeatPlate`（0.6×0.04×0.6 cube，无 collider）挂 Station 下、local (0,−0.02,0)。
6. 退出按钮改挂 rig 根下、local (0,−0.30,1.00)、缩放 0.3；`station` 引用不变。
7. 进 Play 看 Console：`[ride] no seat-eye compensation wired` **必须消失**，并出现 `[seat] measuring:`
   / `[seat] seat …`。

## 8. 未运行 / 未验证

Unity 编译、U# 全量编译、EditMode、Play、VR 真机、15 m 实机、`VRCStation` 的 pitch 是否真的会歪 avatar、`UseStation` 跨物体调用、桌面 Interact 射程。`unittest discover -s Docs/tests`（759 例）与 `validate_plan.py` 只验证几何模型与规划数据。
