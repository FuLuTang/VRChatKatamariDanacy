# 球体接触 v2（Ball Contact v2）：一个接触求解器

**状态：`DESIGN_ONLY`。** 本文只写设计，不改任何 `Runtime/`、`Editor/` C#，不改测试场数据，不碰 Unity/Windows。
所有运行期数字都是 **PROVISIONAL（我方设计/暂定）**，只有明确标 `[官方]`/`[专利]` 的地方来自原作证据。
> **S2 状态（2026-11-15）**：§7.1 的 S2 纯数学库已写入 `Assets/VRC_Katamari/Runtime/BallContact/`（10 个文件）与 `Assets/VRC_Katamari/Tests/EditMode/BallContact/`（8 个文件，含 Python 金值）。**这些文件尚未在 Unity 里编译，EditMode 测试也尚未运行**；电机仍未接入（S3），运行期行为一行未变。
本文的算术有一份可执行参考模型：`Docs/Design/BallMotor/contact_v2/contact_v2.py` + `Docs/tests/ball_contact_v2/test_contact_v2.py`
（`python3 -m unittest discover -s Docs/tests`，本轮 32 例全过：24 接触 + 8 形状）。

写范围：本文件；可选 `Docs/Design/BallMotor/contact_v2/*.py`、`Docs/tests/ball_contact_v2/*.py`。
**不碰**：`Labs/PhysicsTestField`、测试场文档、径向代理（另有 agent 在改）。本文提到它们的改动都是“将来由谁改”。

---

## 0. 结论速览

### 0.1 一句话

把“扫掠 + 台阶掩码 + 支撑表 + 爬墙状态机”四条互不相干的规则，换成**一条规则**：
**球对一组定向盒（OBB）做精确最近点接触；接触法线与竖直的夹角 θ 连续地决定“滚、爬、挡”三件事。**

- **滚**（θ ≤ 45°，`ride`）：球在这张面上滚，位置被推出到相切，水平速度**一分不减**（等于今天坡的手感）。
- **爬**（45° < θ，`climb`）：接触法线接管速度，球沿**上坡切线**以受限速度前进；有速度门、有高度上限、到顶回滑。
- **挡**：不满足“爬”的门（推入太浅、速度不够、已在回滑冷却）时，只做非穿透 + 去法向速度，就是墙。

台阶、坡顶接缝、低台面、斜面从侧面/下面穿过、爬墙，**全部是这一条规则的几何后果**，没有第二种几何表示、没有台阶级别阈值。

### 0.2 七个失败 → 一条规则

| 今天的失败（审计证据） | 根因（`/tmp/ball_audit/report.md`） | v2 的机制 | 状态 |
|---|---|---|---|
| C05 45° 接缝死锁，所有球径都过不去 | 盒子按半径膨胀成 AABB，球心被挡在台面南面 `r` 处，接缝差 `0.5858r` > `stepLimit 0.5r` ⇒ **θ_max = asin(0.6) = 36.87°** | OBB 最近点：接缝处球与坡面、台面棱**同时相切**，差 = 0；不需要任何 step 预算 | **已推导**（§2.6，参考模型 `test_45_degree_seam_*`） |
| 2.2 m 球把 0.6 m 台面当墙 | 同一条膨胀 AABB | 棱在球心下方 ⇒ 推挤法线朝上 ⇒ 滚过去；`h < r` 是几何阈值 | **已推导**（§2.5） |
| 斜面从侧面/下面/球心偏出坡宽时**直接穿过去** | 斜面只当高度场，从不进扫掠 | 坡 = 实体 OBB 板；顶面、侧面、底面都是同一个最近点函数 | **已推导**（§1.5，`test_ramp_side_*`、`test_ramp_underside_*`） |
| C13 2.20 单帧 0.95 m 垂直跳变 + 卡死传送回 `startCenterWorld` | 台阶掩码的一次性大抬升 + 传送到序列化的旧起点 | 没有台阶掩码；传送目标是“最后稳定点”，不是旧起点 | **设计**（§2.8） |
| 完全没有爬墙 | 设计边界（>45° 拒绝注册） | `climb` 通道：`H_cap = k_h·(2r+λL)`，`k_h=1.0` 锚在官方手册 | **锚点 + 我方模型**（§3） |
| 0.35 m 窄坡 + 2.2 m 球：球心在坡宽外 | 足迹不按半径扩张 | 接触是球心到板的最近点，**球可以骑在坡的棱/宽外**，只要球面真的碰到 | **已推导**（§2.5） |
| 埋进球里的吸附物无策略 | 无代码 | 显示层 cull（整个 AABB 在核心−边距内），数据保留 | **设计**（§4.3） |

### 0.3 已推导 / 假设 / 必须试玩

| 类别 | 内容 |
|---|---|
| **已推导（几何/数值，参考模型覆盖）** | 球–OBB 最近点与法线（面/棱/角/内部）；隧穿下界（采样间距 ≤ r 时接触区 ≥ 2r）；45° 接缝差 = 0；`h < r` 的棱滚越条件；0.30 球 4 m/s 需 2 子步；弯坡弦高与法线跳变 |
| **有锚点但数值是我方的** | `H_cap` 的比例：官方手册“能翻过的墙约等于块魂的大小”⇒ 球心可升 **≈ 一个直径**（`k_h=1.0`）。角度、速度、时间全部无公开数值 |
| **纯我方设计（必须试玩）** | `θ_ride=45°`、15° 爬升余量、`v_min=1.5 m/s`、`k_intent=0.5`、`T_climb=4.5 s`、`m0=0.15`、`v_slide_max=1.0`、冷却/锁定、dash 1.6×/0.45 s、窗口边距、全部容量；**形状层**：`r_visual=0.95r_phys`、pickup 盒支撑函数、针尖 16 根与 `r_tip=0.01`、`k_grip=0.5`、针尖参与横向阻挡（§4A） |
| **完全没测** | 任何 Unity/Udon 编译、EditMode、Play Mode、帧时间、手感；本文的所有“每步成本”都是 flop 与调用次数的**估算**，不是测量 |

### 0.4 与现有文档的关系（哪些结论作废）

| 文档 | 结论 | v2 之后 |
|---|---|---|
| `TERRAIN_SUPPORT.md` §4（台阶规则、`stepLimit=0.25D`、掩码） | 台阶是一条独立判据 | **作废**：被 §2.5 的接触规则取代。`stepHeightOverBallDiameter` 只保留为**诊断口径**，不再参与物理 |
| 同 §4.4（跨级大抬升 `maxStepUpLiftWorldM`） | 跨上台阶那一步给大抬升 | **删除**：不再有跨级事件；抬升是推挤的自然结果 |
| 同 §5.5（斜面只支撑、绝不进扫掠） | 斜面不是障碍 | **作废**：坡是实体板，侧面/底面都挡（§1.5） |
| 同 §5.2（45° 坡度护栏） | >45° 拒绝注册 | **作废**：任何坡都能注册；“多陡可滚/可爬”由 θ 决定。作者门（`placement_core` 的 45° error）在 S4 之前**暂时保留**，S4 改成 warning |
| `PHYSICS_TEST_FIELD.md` §4.1.3（36.87° 极限） | 记为电机极限、C05 红灯 | **关闭**：C05 `climbed` 应通过（§6.1） |
| 同 §4.1.5（0.95 m 跳变缺陷单） | 记为缺陷 | **关闭**：结构上不再存在（§2.8） |
| 同 C06（50° 拒绝） | `ramp_refused` / `no_climb` | **重定义**：v2 没有拒绝；C06 变成“50° 是滚/爬分界的证据”（§6.1） |
| `README.md`（马达 9 阶段顺序） | 1→9 固定 | **阶段 2.5/3/3.5/4 换掉**：掩码删除，扫掠换成接触求解，支撑表降级为诊断与代理锚点（§7.3 给新顺序） |
| `Docs/Design/BallLayers/Sweep/*` | 扫掠是位置权威 | 保留文件；v2 不再调用 `TrySweepSphere`（flag 关掉时仍用） |

### 0.5 三条必须记住的口径

1. **`r` 默认指 `r_phys`（物理等效球半径，`cbrt(3V/4π)`）**。视觉核心是 `0.95·r_phys`，滚动是 `r_phys` + 16 根针尖，拾取是更大的方向相关触发体——**三个形状，各有名字与消费者**（§4A）。本文没写形状名的 `r`/`D=2r` 都是物理口径。
2. **θ 是接触法线与 +Y 的夹角**：0° 地板、45° 坡、90° 竖墙。分类只用 θ，不看 surface 类型、不看 `role`。
3. **所有容量都是固定数组**：UdonSharp 没有 `List`/struct/泛型/属性，运行期数据一律并行数组（§1.4、§5.2）。

---

## 1. 几何：球 vs 定向盒（OBB）

### 1.1 为什么必须换掉膨胀 AABB

三个数就能说清（`r` = 球半径）：

```
膨胀 AABB 的球心停点 = 盒面 − r
真实相切（棱/角）的球心     = 到最近点的距离 = r
45° 坡顶接缝的高度差        = r·(tanθ + 1 − 1/cosθ) = r·(2 − √2) = 0.5858·r
台阶预算（v1）              = 0.25·D = 0.5·r
⇒ 能上坡的条件 0.5858·r ≤ 0.5·r  ⇔  0.5858 ≤ 0.5  ⇔  永不成立
⇒ v1 的极限角 = asin(0.6) = 36.87°（与球径无关）
```

四档球的实测 gap（审计）：0.0887 / 0.1763 / 0.3234 / 0.6000 m，与 `0.5858r` 逐位吻合。
v2 用 OBB 最近点后，接缝处球心到“坡面”和“台面棱”的距离**同时**是 r，差 = 0（§2.6 有完整算例）。

### 1.2 最近点与法线（面 / 棱 / 角 / 内部）

OBB 用**中心 + 三个正交单位轴 + 三个半长**表示：`(O, u0,u1,u2, h0,h1,h2)`。

```
d      = (C − O) 投影到局部轴：d_i = dot(C − O, u_i)
q_i    = clamp(d_i, −h_i, +h_i)                    # 局部最近点
e      = d − q                                      # 从最近点到球心
dist   = |e|
outside = #{ i : |d_i| > h_i }                      # 0 内部 / 1 面 / 2 棱 / 3 角
if dist > r + skin:  无接触
else:
    n_local = e / dist                              # dist > 0
    n       = n_local.x·u0 + n_local.y·u1 + n_local.z·u2
    depth   = r − dist                              # 可为负（skin 内但未碰）
    point   = O + q.x·u0 + q.y·u1 + q.z·u2
内部（dist ≤ ε）：
    取 gap_i = h_i − |d_i| 最小的一轴 i，n = sign(d_i)·u_i
    depth = r + gap_i                               # 球心在盒内，穿透 = r + 到最近面的距离
```

要点：

- **棱和角是真的圆角**：法线是 `e/|e|`，不是面法线。这是“球滚过棱”能发生的原因，也是 45° 死锁消失的原因。
- **`feature`（0/1/2/3）只用于诊断与测试**，求解器不按它分支。
- **内部点**不产生“卡在盒里”的死状态：沿最近面法线推出，穿透量是 `r + gap`。这是“初始嵌进几何”的唯一恢复路径（§2.8）。
- **props / 挡路盒**用同一个函数，轴取世界轴（AABB 是 OBB 的特例）。不新增第二种接触代码。

### 1.3 数据布局（UdonSharp：并行数组，无 struct）

```csharp
// 静态几何（场景烘焙一次，永不改）
const int MaxStaticObbCount = 256;      // 地图 0 估 118，留一倍余量
Vector3[] obbCenter      = new Vector3[256];
Vector3[] obbAxisX       = new Vector3[256];   // 正交单位轴
Vector3[] obbAxisY       = new Vector3[256];   // 坡的行走面法线 = +Y（作者约定）
Vector3[] obbAxisZ       = new Vector3[256];
Vector3[] obbHalfExtents = new Vector3[256];
int[]     obbSurfaceId   = new int[256];       // 1..N，与 v1 的 id 空间兼容
int[]     obbRole        = new int[256];       // 0=box 1=slab 2=prop 3=blocking（诊断/作者门）
int[]     obbFlags       = new int[256];       // bit0 = 已登记，bit1 = 有剪切已回退 AABB
float[]   obbBoundingRadius = new float[256];  // |halfExtents|，粗筛用

// 针尖形状（§4A.3）：needleDirLocal[16] / needleSupport[16] / needleCompact[16] /
// needleTipWorld[16] / needleTipRadius / needleProtrusionCap。与上表同构的并行数组。

// 每步近集（只装窗口内的条目，见 §5）
const int MaxNearObbCount = 24;
Vector3[] nearCenter, nearAxisX, nearAxisY, nearAxisZ, nearHalfExtents; // 同构
int[]     nearSurfaceId, nearRole;
int       nearCount;

// 接触（每步最多 12 条：核心 ≤8 + 针尖 ≤4；并行数组）
const int ContactCapacity = 12;
Vector3[] contactNormal, contactPoint;
float[]   contactDepth, contactAngleDeg;
int[]     contactSurfaceId, contactRole, contactFeature, contactClass;
int[]     contactShape;                // 0 = 核心球，1+i = 第 i 根针尖（§4A.3）
int       contactCount;
int       needleContactCount;          // 其中针尖接触的条数（≤4）
float     contactResidualMax;          // 迭代后仍剩的最大穿透
int       contactSaturated;            // 写满 8 条的锁存
```

字节数（粗算，`Vector3` 12 B / `int`/`float` 4 B）：静态表 `5×12 + 4×4 + 1×4 = 80 B/条` × 256 = **20.5 KB**；
近集 24 × 80 B = **1.9 KB**；接触 8 ×（2×12 + 4×4 + 4×4）= **0.5 KB**。合计 < 25 KB，Udon 可以接受（现有马达的数组总量同量级）。

**容量变化（相对今天）**：静态盒 8 → **256**；斜面 4 → 并入同一个 256 表（`role=1`）；平面 8 不变；挡路 6、支撑道具 6 不变；**接触 4 → 8**。
静态表变大是“地图 0 一个大平台”的硬需求（§5）；每步真正被查询的是近集（≤24），不是 256。

### 1.4 烘焙：从 `BoxCollider` 到 OBB（8 角点法）

`LevelArenaHost.Setup()` 今天读 `box.bounds`（世界 AABB，旋转丢失）。v2 改成**用 8 个世界角点拟合 OBB**，一次、只在 `Setup`：

```
corners[8] = t.TransformPoint(box.center + 0.5·(±size.x, ±size.y, ±size.z))   # 8 次
u0 = normalize(corners[1] − corners[0])                                       # 局部 X 边
u1 = normalize(corners[2] − corners[0])                                       # 局部 Y 边
u2 = normalize(corners[4] − corners[0])                                       # 局部 Z 边
若 |dot(u_i,u_j)| > 1e-3（父级非均匀缩放造成剪切）→ 回退：用 bounds 的 AABB + 一条 warning `[arena] shear`
half = 0.5·(|corners[1]−corners[0]|, |corners[2]−corners[0]|, |corners[4]−corners[0]|)
center = 8 角点的平均
```

- **为什么用角点而不是 `rotation`/`lossyScale`**：`TransformPoint` 把父级缩放、旋转、位移全都算进去了；轴与半长直接从结果里取，**不需要相信任何单一属性**。剪切（非均匀缩放 + 旋转）会表现为轴不正交，这时**宁可用 AABB 并报警**，也不要用一个会错的 OBB。
- **`role=box`**：`obbRole=0`。**`role=ramp`**：`obbRole=1`（slab），`obbAxisY` 就是行走面法线；顶面 = `center + h1·u1`，半宽/半长 = `h0/h2`，与 `SupportSurfaceMath.TryBakeRamp` 今天烘出的五个数**同源**。`SupportSurfaceMath` 的“行走面/坡度”只留作**作者校验与诊断**，不再是物理表示。
- **兼容**：`AddBox(min,max)` 保留，登记成轴对齐 OBB（`u=(1,0,0),(0,1,0),(0,0,1)`），所以 `CpuSkirmishBootstrap`、旧 lab、EditMode 都不破。
- **`Bounds` 的旋转丢失**：现状是“已知限制”（审计 G#10）。v2 用 8 角点法**直接消掉**这条限制；对轴对齐盒，两种烘法的数值完全相同，所以旧场景读数不应变化（这是迁移的一个回归检查点，见 §7.4）。
- **容量溢出**：`AddBox`/`AddObb` 超过 256 → `SignalOverflow("obb capacity 256 exceeded")`，**拒绝登记并计数**，不静默丢（§5.4 同一套语义）。

### 1.5 斜面 = 实体板（slab）

**决定（需拍板，推荐“是”）**：坡是一块**实体板**，不是高度场。它有三类面，全部由同一个最近点函数给出：

| 面 | 什么时候最近 | 法线 | v2 的行为 |
|---|---|---|---|
| **顶面**（+local Y） | 球在板的上方 | `u1`（n.y > 0） | ride 或 climb，按 θ |
| **侧面**（±local X、±local Z 的边） | 球在板宽外/板端外，且高度在板厚范围 | 水平或倾斜 | 挡（climb 类，通常 θ≈90°） |
| **底面**（−local Y） | 球在板下方 | `−u1`（n.y < 0） | 挡（ceiling，`CLASS_NONE`） |

**薄板的诚实说明**：C05 的板只有 0.04 m 厚，顶面在坡脚处 y≈0，所以一颗 `r=0.15` 的球在地面滚过去时，它的**顶部（0.30）比板的底面（≈0.27）还高**——球是用“底面棱”撞上板的，而不是“侧面”。这仍然是**挡住**（参考模型 `test_ramp_under…` / `test_ramp_side…` 分别覆盖了厚板的侧面与薄板的下面），但它说明：
- 想测“从侧面撞板”，得用**有厚度的坡体**（沙盒的坡都是厚板/实心坡，§5.9）；
- 薄板在关卡里如果必须挡侧面，就应该把板加厚（≥ 2r 不现实，但 ≥ 0.3 m 就能挡住小球的侧面），或者接受“球从板下面钻过去”——这是**关卡数据**的决定，不是求解器的。

**45° 作者门**：`Tools/levelbuild` 与 `SupportSurfaceMath.TryBakeRamp` 今天都以 45° **拒绝**。v2 不再需要这条门（θ 连续处理），但 `placement_core._check_structure` 是**数据校验器**，改它属于 S4；在那之前保留 45° error，S4 改成 warning + “>50° 的坡只能爬，见 §3.10”。`AddRamp` 保留为**兼容入口**：它把 `(faceCenter, normal, right, halfWidth, halfRun)` 加一个 `thickness` 参数（默认 0.04）构成 slab OBB；`PhysicsTestFieldDriver` 的 C06 probe 走同一个入口，所以拒绝读数在 S3 之前仍能工作。

### 1.6 平面（不变）

`AddPlane(n, offset)` 仍是**单侧半空间**，只用于边界墙；`dot(n,C) − offset ≥ r` 为自由空间。与 v1 唯一的差别：接触记录里它也走同一张 `contact*` 表（`surfaceId` 用 1000+i），所以速度响应与盒一致。**平面不做支撑**（无限平面会变成“天上的地板”），这条 v1 的结论继续有效。

### 1.7 隧穿与子步：一个可证的界

**为什么今天没有 CCD 也不该穿**：原作没有 CCD（30 Hz + 粗碰撞 + 掉出世界救回，`/tmp/orig_physics/report.md` §3.1），我们也不想抄“掉出去再救”。v2 用**子步 + 离散查询**，因为离散查询有一个严格的下界：

> **命题**：球心沿直线运动、障碍是凸体时，`dist(C, solid)` 是 1-Lipschitz 且凸体内为 0。若路径与凸体相交，则路径上 `dist ≤ r` 的区间长度 **≥ 2r**（从进入前 r 到穿出后 r）。
> **推论**：只要每个子步的位移 ≤ r，采样点间距 < 2r，**至少有一个采样点落在接触区里**，不可能“整步跨过”一个凸体。
> κ = 0.5 时采样间距 = 0.5r，**余量 4×**。

```
N = clamp( ceil( (|Δ| + (g + a_climb)·dt²) / (κ·r) ), 1, N_max )      κ = 0.5, N_max = 8
```

`(g+a_climb)·dt²` 是“本步内接触可能新增的速度”对应的位移余量（≈5 mm），把“子步数按步首速度算、步内速度变大”这个漏洞补上。

**D=0.30 的显式界（题目要求）**：`maxSpeed=4 m/s`、`dt=0.02` ⇒ 每步 0.08 m = **0.534·r**（r=0.1499）；`N=2` ⇒ 每子步 **0.267·r**，距“不隧穿只需 ≤ r”有 **3.75×** 余量，距 κ=0.5 有 **1.87×**。dash 1.6×（0.128 m = 0.854r）仍是 N=2、每子步 0.427r。

**数值（四个球径，`maxSpeed = 4 m/s`，`dt = 0.02`）**：

| 情形 | 每步位移 | /r | N | 每子步 |
|---|---:|---:|---:|---:|
| 平地全速，D=0.30（r=0.1499） | 0.0800 m | 0.534 | **2** | 0.0400 = 0.267r |
| 平地全速，D=2.20（r=1.10） | 0.0800 m | 0.073 | 1 | 0.0800 |
| dash 1.6×，D=0.30 | 0.1280 m | 0.854 | **2** | 0.0640 = 0.427r |
| 从 0.6 m 落下落地前（3.43 m/s），D=0.30 | 0.0686 m | 0.458 | 1 | 0.0686 |
| 从 5 m 落下（9.9 m/s），D=0.30 | 0.198 m | 1.32 | **3** | 0.066 |
| 终端速度护栏（40 m/s） | 0.80 m | 5.33 | 8 + 钳位 | — |

**下落钳位**：`N_max·κ·r = 4r = 0.60 m/步`（D=0.30）⇒ 有效终端速度 **30 m/s**（不是 `maxFallSpeedMps = 40`）。这一步把“护栏值”变成“求解器能证明安全的位移”，代价只是 30 m/s 以上的下落被截断——本世界最高落差 < 10 m（落到 14 m/s），碰不到。**这是设计选择，写进参数表**。

**证明的边界（要诚实）**：
- 它保证“不会整步跨过凸体”，**不保证**“不会在一步内先碰到再离开”（那由子步内的多次接触迭代处理，且 20 ms 内不可能有两次独立碰撞事件）。
- 它要求障碍是**凸的**；OBB 是凸的，平面是凸的（半空间），弯坡的每一段都是凸的。**凹形轮廓（半管）由多个凸段拼成**，段与段之间的缝由重叠解决（§5.7）。
- 它不覆盖“球心已经在几何内部”的初始状态；那种情况走 §1.2 的内部推出 + §2.8 的恢复。

### 1.8 每步成本估算（不是测量）

| 阶段 | 最坏 | 典型 | 说明 |
|---|---:|---:|---|
| 窗口扫描（200 条静态） | 200 × 8 flops = 1.6k | 同 | 每条一次包围球距离 |
| 近集查询（24 条 × 2 子步） | 48 × 30 = 1.4k（无接触早退） | 12 条 → 0.7k | 有接触的条目 ≈ 60 flops |
| 接触迭代（≤12 条 × 3 次） | 36 × 40 = 1.4k | 2 条 → 80 | 每次重查该条目 |
| 针尖段 vs OBB（§4A.3） | 8×12×2 × 40 = 7.7k | 4×3×2×40 = 1.0k | 只遍历 compact 针；典型 2–6 根 |
| 分类/支撑/爬升 | ≈ 0.2k（+ 爬升时 32 端点 ≈ 0.1k） | 0.1k | |
| **合计** | **≈ 13k flops/步**（含针尖最坏） | **≈ 2.5–3.5k** | v1 的扫掠同量级（≤14 障碍 × 4 投影 × ≤4 接触 ≈ 1.6k）；针尖是 v2 的主要增量 |

**Udon 的真成本是调用，不是 flop**：v2 每步的新增调用是
`ContactSolve.TryStep(...)`（1 次/子步，**所有障碍在函数内部循环**，不许每条障碍一次 Udon 调用）、
`ContactSolve.BuildContacts(...)`（1 次/子步）、`ContactSolve.Classify(...)`（1 次/步）、
爬升 1 次、接触写回 1 次 ⇒ **≤ 8 次/步**。这个数量级与今天的 `TrySweepSphere + TryCollectWallContactNormals + TryRemoveWallNormalVelocity + GroundContactMath×2 + TryResolveSupport` 相同。
**必须做一次 profiler 实测**（`GetLastStepMicros()`，P4 已有的请求）；在拿到数之前，本文只说“同量级”，不说“更快”。

---

## 2. 接触分类与响应

### 2.1 每步收集什么

每个子步结束时（推出之后）对近集做一次查询，写进 `contact*` 并行数组：

| 字段 | 含义 |
|---|---|
| `normal` | 从实体指向球心的单位法线 |
| `point` | 实体表面上的最近点 |
| `depth` | `r − dist`（>0 才推；skin 内可为负） |
| `angleDeg` | `acos(normal.y)` |
| `surfaceId` / `role` | 诊断与作者语义；**不参与行为分支** |
| `feature` | 0 内部 / 1 面 / 2 棱 / 3 角（诊断） |
| `class` | 下面 §2.2 的三分类 |
| `shape` | 0 = 核心球；1+i = 第 i 根针尖（§4A.3） |

`solverResidual = max(depth)`（迭代后仍 > 0 的部分）。**它必须被读到**：> 0.02 m 时电机拒绝把球放进几何（保持上一步位置 + 计数），而不是照常前进（§2.8）。

### 2.2 分类：一个法线的连续函数

```
classify(n):
    if n.y < −0.05:            return NONE     # 天花板/底面：只挡，不滚不爬
    if n.y ≥ cos(θ_ride):      return RIDE     # θ ≤ 50°：可滚
    return CLIMB                              # 50° < θ < 90°+：可爬/挡
```

**θ_ride = 45°（OUR）+ 0.5° 等号容差**：45° 的坡必须能滚（C05，且 v1 的作者门就是 45°），50° 的板（C06）**必须离开“滚”**、进入“慢慢爬”，60° 以上更不用说（否则 60° 坡会以 4 m/s 冲上去，等于没有重力感）。45° 同时是 v1 的 `maxSupportSlopeDegrees`，也是“硬上限 51.3°”（由单步钳位反推）之下的保守值。**这条让 C06 的语义从“拒绝”变成“爬”**（Lead 的补充要求）。
等号按 `θ ≤ 45.5°` 判 ride（0.5° 容差）：45° 的板正好在线上，浮点噪声不能让它在“滚/爬”之间翻。同一条纪律也适用于 §2.5 的 `h<r`（用 `h ≤ r − 0.5 mm` 判滚越），三处等号一次改完，不要只改一处。

**15° 爬升余量（OUR，防“坡顶把爬升通道骗开”）**：
即使一个接触是 CLIMB 类，只要它**不比球当前滚着的面陡 15° 以上**，就**既不进爬升通道、也不参与去速**（视为“面在转弯”，由推挤滚过去）：

```
if support != null and wall.angleDeg < support.angleDeg + 15°:
    wall = null                       # 不爬
    # 同一个接触在速度响应里也被跳过（否则“不爬”仍然会在这里刹车）
```

**为什么必须有这条**：45° 坡顶接缝处，球同时切到坡面（45°）和台面棱（因为离散化，棱的法线可能是 52–56°）。
参考模型在**没有**这条时会误判成“墙”，爬升通道接管速度，球在接缝处从 4 m/s 掉到 0.2 m/s（`test_45_degree_seam_keeps_the_planar_speed` 会红）。
加上 15° 后，52–56° 的棱被视为“面在转弯”，推挤滚过去；真正的墙（≥ 支撑 + 15°）才爬。
这不是角度白名单：它比较的是**接触之间的相对角**，随支撑面连续变化。

### 2.3 位置响应：推挤（push-out）

```
position = C
contacts = Query(position)
for iter in 0..2:                      # SolverIterations = 3
    deepest = argmax(depth) among contacts with depth > slop   # slop = 0.5 mm
    if none: break
    position += deepest.normal · deepest.depth
    contacts = Query(position)          # 只重查（实现上重查全部近集，代价见 §1.8）
residual = max(depth)
```

- **顺序推挤 + 重查**（不是把所有法线求平均）：凹角里两条法线依次作用，与 v1 的 `ProjectionPasses=4` 同思路；3 次对“盒角 + 地面 + 墙”足够（参考模型 C16 类场景 `residual ≤ 2 mm`）。
- **slop = 0.5 mm**：避免浮点噪声每步推一下（抖动）。
- **不产生“弹开”**：位置推出后不加速度；速度响应在 §2.4 单独做。
- **推挤量就是自然抬升**：球滚上台阶时，每步的推挤量 ≤ 本步穿透（≤ 子步位移），所以“上台阶”不再需要 `maxStepUpLift` 这种一次性大抬升。C13 2.20 的 0.95 m/帧在结构上不可能发生（0.95 m 需要一次 0.95 m 的穿透，而子步位移 ≤ 0.08 m）。

### 2.4 速度响应：三种接触三种处理

| 接触类 | 速度处理 | 为什么 |
|---|---|---|
| **RIDE**（θ ≤ 45°） | **不剥任何分量**（只做位置推挤） | 滚动接触不是碰撞。坡上速度不减（v1 的“坡上不减速”手感），坡顶出口不会掉 30% 速度 |
| **CLIMB**（θ > 45°，且满足 §2.2 的余量） | 见 §2.3 的爬升通道：若在爬，**速度由通道接管**（沿上坡切线）；若不在爬，**只剥水平法向分量** | 陡坡上“滚”不成立；水平推入必须被吸收，但竖直分量归爬升/重力管 |
| **NONE**（天花板/底面） | 剥**三维**法向分量（含 `v_y`） | 撞天花板要掉速，这是唯一需要动 `v_y` 的“硬碰撞” |
| 任意类但 `depth ≤ 0`（skin 内、未碰） | **不处理** | “靠近”不是“接触”，否则球离开墙面时会被粘一下 |

**“RIDE 不剥速度”是一个必须写死的决定**（参考模型里它修掉了 30% 的接缝掉速）：
它在物理上等价于“球在面上无滑动地滚”，在数值上让坡顶、台面棱的过渡不损失动能。代价是**球不会被 45° 坡“弹回”**——原作也没有回弹证据（`/tmp/orig_physics` §3.1）。

### 2.5 支撑规则：取代 `stepLimit` band

**旧规则**：扫掠前算掩码 `rise ≤ 0.25D` → 从扫掠剔除 → 支撑表读掩码。这是“一个高度阈值 + 两个读者”。
**新规则**：

```
support = 本步接触里 n.y 最大的 RIDE 接触（并列取 depth 大的）
if support:
    supportNormal = support.normal
    supportPoint  = support.point
    supportHeight = 球心 XZ 处的切平面高度 = C.y − dot(n, C − p) / n.y   # 与 v1 的 getter 同口径
    v_y = 0                       # 被支撑，没有弹道
    VisualRoll 的法线 = support.normal
else:
    无支撑：重力/爬升通道接管
```

- **没有“候选面列表”，没有“激活带”，没有“最高者胜”**。球被谁托着，就是它正在碰的那个面；没碰就是没支撑。
- **“支撑面掉走”不再是一条判据**：接触消失 ⇒ 没支撑 ⇒ 重力。v1 的 `supportDropTriggerM`、`sameSurface` 抑制、`maxDropPerStep` 全部删除（它们存在是为了修补“高度场 + 单一选中面”的模型）。
- **`GetSupportKind()` 兼容**：0 地面 / 1 盒 / 2 坡（slab）/ 3 道具 / **4 爬升接触** / **5 针尖支撑** / −1 无。测试场读旧值的地方不受影响。
- **“只有针尖撑着”是合法状态**（§4A.3）：核心球离开地面、针尖还压着坡/台阶时，`supportKind=5`，`supportHeight` 取**针尖处的切平面**：`height = p_tip.y − [n.x(C.x−p_tip.x)+n.z(C.z−p_tip.z)]/n.y`，`support_gap` = 突出量（>0，表示球被撑起来，不是陷进去）；**不要**套用核心接触的 `C.y − dot(n,C−p)/n.y`（对针尖它会把支撑面算到球心高度）。这取代了 v1 的 `GroundContactMath` 代理抬升路径。
- **`stepHeightOverBallDiameter` 只留作诊断**：`GetStepHeightLimitWorldM()` 继续返回 `0.25D`（给旧判据读），但它**不参与任何物理**。

**台阶从同一条规则涌现（推导）**：

球在支撑面 `y=0` 上，半径 r，滚向一个高 h 的直角台：

- 台面棱在 `(0,h)`。球心到棱的距离是 `√(d² + (r−h)²)`（d = 球心到台面的水平距离）。
- 接触条件 `√(d² + (r−h)²) ≤ r` ⇔ `d² ≤ 2rh − h²`。
- 该接触的法线 y 分量 `n.y = (r−h)/r`：
  - `h < r` ⇒ `n.y > 0` ⇒ 推挤把球**向上**推 ⇒ 球骑上棱、滚过台面。**台阶的门 = h < r = D/2**。
  - `h = r` ⇒ `n.y = 0` ⇒ 法线水平 ⇒ 球停在棱前（纯几何上不去）。
  - `h > r` ⇒ `n.y < 0` ⇒ 推挤向下 ⇒ 球被压住（但接触仍存在，直到 `h ≥ 2r`）。
- **这就是为什么“0.25D 的台阶”不再需要特判**：0.25D < 0.5D = r，天然在“滚过去”区间。C07–C12 的 0.02/0.05/0.075/0.10 在 D=0.30 时全是 `h < r`（0.10 < 0.1499），v1 里它们是墙（`0.10 > 0.075`），v2 里是**滚过去**（期望翻转，见 §6.1）。
- **0.5D ≤ h < 1.0D**：几何上“压住”，但 §3 的爬升通道能把球**慢慢**送上去（`H_cap = D`）。所以台阶的完整三段是：
  **h < 0.5D 滚过去（快） / 0.5D ≤ h < D 爬上去（慢） / h ≥ D 墙**。
  这三段全部来自 `k_h=1.0` 与 `h<r` 两个数，没有第二套阈值。
- **2.2 m 球 + 0.6 m 台面**：`h=0.6 < r=1.1` ⇒ 滚过去。v1 的“当墙”是膨胀 AABB 的假象，不是尺度问题。
- **0.35 m 窄坡 + 2.2 m 球**：球可以骑在坡的**棱**上（棱在球心下方），不需要球心落在坡宽内。`GetSupportKind()` 报 2（slab），球心可能远在坡宽外——这是正确的“球压在板上”，不是“球在坡上”。

### 2.6 45° 接缝：为什么现在过得去（完整算例）

C05：坡 45°、台面顶 0.6、台面南面 z=8.2、球 r=0.1499。

| 量 | v1（膨胀 AABB） | v2（OBB 最近点） |
|---|---|---|
| 球心 z 停点 | `8.2 − r = 8.050` | 到棱的距离 = r 的相切点：`8.2 − r·sin45 = 8.094` |
| 球心 y | 0.661（球底 0.511） | `0.6 + r·cos45 = 0.706`（球底 0.556，仍在台面**前面**） |
| 滚上台面后 | —（上不去） | 球心 y=0.750，球底 = 0.600 = 台面（`deck_gap = 0`） |
| 台面是否可踏 | `rise=0.088 > 0.075` ⇒ **墙** | 接触是**棱**，`n.y=0.707` ⇒ RIDE ⇒ 推挤 ⇒ 上 |
| 结论 | 死锁 | **通过**（参考模型 `test_45_degree_seam_ball_reaches_the_deck`） |

精确算例：坡的顶面方程 `y = z − 7.6`（45°），台面棱在 `(8.2, 0.6)`。球心 `C=(8.094, 0.706)`：
到坡面距离 `= 0.706 − (8.094 − 7.6) = 0.212 = r/cos45` 的法向距离 `0.1499` ✓；
到棱距离 `= √(0.106² + 0.106²) = 0.1499` ✓。**两个约束同时满足**，球可以继续向 +z 滚，接触点从坡面平滑换到台面顶面。
参考模型 20 步（0.4 s）后：`y = 0.750`（= 0.6 + r），`z = 8.675`，`v_planar` 仍为 4.0 m/s。

### 2.7 “大球矮台”与 C13 2.20 的 0.95 m

- **C13 2.20 掉下北缘**：v1 的 `seam_no_step = 0.9499 = min(stepLimit, maxStepUpLift) + 4×maxLiftPerStep`，是“掩码大抬升 + 卡死传送回 `startCenterWorld`（y=0.15 的旧起点）”两段叠加。v2 里：
  - 没有掩码 ⇒ 没有一次性大抬升；抬升只来自“穿透的推出”，而穿透 ≤ 本子步位移（4 m/s、D=2.20 时 0.08 m/步；下落时那是下降，不是抬升），所以**每步抬升 ≤ 0.08 m**；
  - 卡死恢复的目标是**最后稳定点**（§2.8），且会先做软恢复（清速度），不会把球塞进地面；
  - 于是“0.95 m/帧”在结构上不可能。判据 `seam_no_step` 保留，**预期从 fail 变 pass**（§6.1）。

### 2.8 卡死 / 传送：不允许静默循环

```
每步：
  driveIntent = |inputForward| + |inputLateral|
  progress    = |C_new − C_old|                       # 三维，爬升的竖直位移也算
  if driveIntent > 0.05 and progress < 0.0005: stuckSeconds += dt
  else:                                        stuckSeconds = 0
  if progress > 0.001 and residual < 0.001:   lastStableCenter = C_new   # 每条 0.1 s 最多写一次
```

| 触发 | 动作 | 视觉 |
|---|---|---|
| `stuckSeconds > 1.5`（软） | 清 `planarVelocity`、清爬升/下落状态、清 `solverResidual` 相关缓存；**不动位置**；`recoveryCount++`，`recoveryReason=1` | 无跳变 |
| 仍卡到 3.0 s（硬） | 传送到 `lastStableCenter`；清速度；`discontinuous=true`；`recoveryReason=2` | 一次跳变 |
| 没有 `lastStableCenter`（本回合从未移动） | 传送到**本回合起点**（`TryResetForNewRound`/`TeleportTo` 写入的 `roundStartCenter`） | 一次跳变 |
| 10 s 内 `recoveryCount ≥ 3` | **不再传送**：停在原地，`recoveryState=3`，打一条 `[BallMotor] recovery hold cell=… surface=… residual=…`；测试场据此判 `recovery_hold == 0` 失败 | 无（但会被判据抓住） |
| 初始就深嵌（`residual > r`） | 沿最深法线一次推出全部穿透；若仍 > 0.02 m，传送 `lastStableCenter` 并 `recoveryReason=3` | 一次跳变 |

**三条不许做的事**（都是 v1 的教训）：
1. **不许**用序列化的 `startCenterWorld` 当恢复点（它是场景烘死的 y=0.15，不随球径、不随本回合；C13 的 0.95 m 就是它）。
2. **不许**“传送 + 保留速度”（会立刻再撞）。
3. **不许**无限次恢复而不留痕：`GetRecoveryCount()` / `GetRecoveryState()` / `GetLastRecoveryReason()` 三个 getter 必须进树，测试场对除 W6 以外的所有格要求 `recovery_delta == 0`。

### 2.9 v1 / v2 逐项对照

| 项 | v1 | v2 |
|---|---|---|
| 位置权威 | 扫掠（平面 + 膨胀 AABB）+ 支撑平面高度 | 接触推挤（OBB + 平面） |
| 台阶 | `stepLimit = 0.25D` + 掩码 | 几何 `h<r` 滚越 + 爬升 `h<D`；无阈值参数 |
| 坡 | 高度场，只支撑 | 实体 slab，顶/侧/底都挡 |
| 坡顶接缝 | 36.87° 死锁 | 同时相切，差 0 |
| 墙 | 停 + 切向滑 | 停 + 切向滑；可爬（§3） |
| 爬升 | 无 | 参数化模型，`H_cap=D` |
| 卡死 | 1.5 s 后传送到旧起点 | 软/硬两段，最后稳定点，3 次后停手并报错 |
| 接触容量 | 4 | 8 |
| 静态几何容量 | 8 盒 + 4 坡 | 256 OBB（每步近集 ≤24） |
| 每步几何成本 | ≈1.6k flops | ≈1.5–4.2k flops（§1.8） |
| 尺寸耦合 | 接触用 core、扫掠用 collision、reach 用 collision | 三形状：`r_visual` / `r_roll`(+针尖) / `pickupReach(û)`（§4A） |

---

## 3. 爬升（climb）：一个参数化模型

### 3.1 证据：能锚什么、不能锚什么

| 结论 | 级别 | 来源 | 我们怎么用 |
|---|---|---|---|
| 面朝墙、双摇杆前推 ⇒ 向上翻越 | `[官方]` | We Love Katamari 英文手册 “CLIMB OVER OBSTACLES!!” | **输入语义**：不需要按钮，推入即爬（§8 拍板项） |
| 可翻越的墙“about the same size as your katamari” | `[官方]` | 同上 | **唯一的数值锚**：球心可升 ≈ 一个直径 ⇒ `H_cap = k_h·2r`，`k_h = 1.0` |
| 上坡需要“predetermined velocity”；Vmax 随体积增大 | `[专利]` | US7402104B2 | **速度门** `v_req(θ)`；但本工程大球更慢（`growthSpeedFactor=0.25` ⇒ 2.20 档 3.0 m/s），所以“大球能上更陡的坡”**不由速度复现**，由 `H_cap ∝ D` 与 `h<r` 复现 |
| 70°/90° 的具体角度、爬升速度、时间上限 | **无任何公开数值** | — | 全部标 **OUR design**，见 §3.5 的试玩清单 |
| 原作碰撞是**单个等效体积盒**，凸出物只改接地点 | `[专利]` | US7402104B2 | 碰撞仍是**一个球**；凸出物只进 `H_cap` 的 `λL` 项（§3.7），不做逐物件碰撞 |

### 3.2 三个带，不是白名单

| 带 | 条件 | 谁驱动速度 | 有高度上限吗 |
|---|---|---|---|
| **滚** | θ ≤ 45° | `BallMotionMath` 的水平速度（v1 手感，坡上不减速） | 无（坡多长都能滚） |
| **爬** | θ > 45° 且比支撑陡 ≥ 15° | 爬升通道（沿上坡切线，受限） | **有**：`H_cap = 1.0·(2r + λL)` |
| **挡** | 不满足爬的门（推入 < 0.2 / 速度 < `v_req` / 冷却 / 已锁） | 只做非穿透 + 去法向分量 | — |

**为什么不是角度白名单**：白名单会写 `θ ≤ 70 才可爬`，在 70.1° 产生硬边。这里“能不能爬”是**连续门**：

```
v_req(θ) = v_min · sin^α(θ)            # v_min = 1.5 m/s, α = 1.0
v_gate   = max(|v_planar|, k_intent · v_drive)   # k_intent = 0.5, v_drive = 输入目标速度
可爬 ⇔ p ≥ 0.2  且  v_gate ≥ v_req(θ)  且  h < H_cap  且  冷却 = 0  且  未锁
```

- `v_drive` 是 `BallMotionMath` 的**目标速度**（`|forward|·maxSpeed`），不是接触削剩下的实际速度。**为什么**：球顶住墙时实际水平速度 ≈ 0，若用实际速度做门，永远起不了步（“推墙不动”）；用目标速度表达“你有多用力推”。
- 全推（4 m/s）：`v_gate = 2.0` ⇒ 90° 也过（`v_req(90)=1.5`）；半推（2 m/s）：`v_gate = 1.0`，连 50° 都不过（`v_req(50)=1.15`）。所以“轻推不爬、重推才爬”，且这个门随 θ 连续收紧。
- `p = max(0, −dot(û, n_h))`：`û` = 朝向，`n_h` = 法线水平投影的单位向量。斜面侧擦（p 小）不爬。

### 3.3 速度与高度公式（全部 OUR design，除 `k_h`）

**高度上限**

```
H_cap = k_h · (2r + λ·L_eff)          k_h = 1.0（手册锚），λ = 1.0（OUR）
L_eff = clamp( max_i dot(R·p_i, −n) − r , 0, maxProxyProtrusion )   # 见 §3.7
h     = C.y − climbAnchorY            # climbAnchorY = 进入爬升时的球心 y
```

**爬升速度（按“到顶时间”参数化，而不是按速度）**

```
v_up0    = 1.718 · H_cap / T_climb            # T_climb = 4.5 s
                                              # 1.718 = 1/(1−√0.02)：「爬满 98% H_cap」需要的时间
                                              # 由 t(h)=2H/v0·(1−√(1−h/H)) 反解
rate     = clamp(v_gate / 2.0, 0.75, 1.5)     # 推得越狠越快，dash 到 1.5 上限
m(θ)     = cosθ + 0.15·sinθ                    # m(90°) = 0.15（creep 下限），m(50°) = 0.758
fade     = sqrt(clamp(1 − h/H_cap, 0, 1))      # 到顶才归零，接近顶部仍有 20–30% 速度
v_up     = p · rate · (m(θ)/m(90°)) · v_up0 · fade        # 竖直速率 m/s
v_along  = v_up / sinθ                                     # 沿坡切线的速率
t_up     = normalize(up − (up·n)·n)                        # 上坡切线；90° 时 = +Y
v        = v_along · t_up                                  # 爬升通道“拥有”速度向量
```

- **为什么按时间参数化**：`H_cap ∝ r`，若用固定速度，2.20 球爬 2.2 m 要 15 s；按 `T_climb` 后**所有球径都是 ~4.5 s 到顶**（`v_up0 ∝ H_cap`）。大球在 m/s 上更快、在“几个身位”上一致——这是手感决定，需试玩。
- **为什么 `m(θ)/m(90°)`**：70° 比 90° 快 3.2×（0.738 vs 0.229 m/s @D=0.60），满足“70° 能爬但慢、90° 极慢地蹭”。
- **`fade` 用 sqrt**：线性 fade 会让球在 0.9H 时只剩 10% 速度、永远到不了顶；sqrt 在 0.9H 还有 32%。
- **`v_along` 与 `t_up`**：这是让 70° 坡“能爬”的关键。若只给竖直速度，球会**离开**坡面（坡在前进方向升高）；沿切线同时给水平与竖直分量，球才贴着坡走。90° 时 `t_up = +Y`、水平分量为 0，退化成“贴墙直上”。
- **速率限制**：`a_climb = 3 m/s²`，`v_along` 每子步最多变 `a_climb·dt/N`；避免接触瞬间速度阶跃。

**回滑与锁定**

```
到顶（h ≥ H_cap − 0.02）或松开（p < 0.2）：
    climbActive = false; along = 0; cooldown = 0.35 s
    若因到顶：climbLocked = true
    然后：v_y ← max(v_y − g·dt, −v_slide_max)      # v_slide_max = 1.0 m/s，墙面接触时
climbLocked 只在 p < 0.2（松开）时清除
```

- **为什么锁定**：不锁定的话，球到顶→滑下→再次满足门→再爬，会在 `H_cap` 附近形成**看不见的无限循环**（参考模型在加锁前就是每 4 s 一次）。锁定 = “一次推入只给一次机会”，要再爬必须松手再推。
- **为什么回滑限速 1.0 m/s**：`g` 自由落体从 0.6 m 只要 0.35 s，观感是“掉下来”；1.0 m/s 是“慢慢滑回来”，且不会触发卡死计时（有位移）。

### 3.4 状态与每步更新顺序（精确到公式）

**每球状态（UdonSharp 标量；单球版 7 个字段，其中 `climbHeight` 是派生量）**

| 字段 | 类型 | 含义 |
|---|---|---|
| `climbActive` | bool | 本步是否在爬 |
| `climbAnchorY` | float | 进入爬升时的球心 y（`h` 的原点） |
| `climbSurfaceId` | int | 诊断用（**不参与退出判断**，见下） |
| `climbAlong` | float | 沿切线的速率（速率限制的状态） |
| `climbCooldown` | float | 回滑/退出的冷却 |
| `climbLocked` | bool | 到顶锁：松开才清 |
| `climbHeight` | float | 只读派生量 `h`（诊断/getter） |

**每步顺序**（嵌进 v2 的新阶段 3–4，完整 9 阶段见 §7.3）：

```
1. 运动层给 v_planar（水平，y=0）与 v_drive（目标速度）
2. N = clamp(ceil((|v|·dt + (g+a_climb)·dt²)/(0.5r)), 1, 8)
3. for sub in 1..N:
   a. C += v·dt/N
   b. contacts = Query(C)                      # 近集：OBB + 平面
   c. (C, contacts, residual) = PushOut(C, contacts, 3 次)
   d. support = argmax n.y among RIDE; wall = argmax push among CLIMB
      if wall and support and wall.θ < support.θ + 15°: wall = None
   e. if wall: 计算 p、v_gate、进入/退出/锁定（§3.3）
   f. if climbActive and wall:
          up = (0,1,0); t_up = normalize(up − (up·n)n)
          h = C.y − climbAnchorY
          target = climbSpeed(p, v_gate, θ, h, H_cap)
          along += clamp(target/sinθ − along, ±a_climb·dt/N)
          v_planar = (t_up.x, 0, t_up.z)·along
          v_y      =  t_up.y·along
      else:
          for each contact (not RIDE, depth>0):
              CLIMB: 去掉水平法向分量；NONE: 去掉三维法向分量
          if support: v_y = 0
          elif wall:  v_y = max(v_y − g·dt/N, −v_slide_max)
          else:       v_y = max(v_y − g·dt/N, −40)
          # 不在爬时 v_planar 保持接触后的值；下一步由 BallMotionMath（阶段 2）重新加速
4. 支撑高度/法线写回（诊断 + 代理锚点），v_y=0 表示 grounded
5. 吸收 → 账本 → 尺寸（§4）
6. 尺寸变化后的“长大推挤”（§4.2）
7. 视觉滚转（法线 = 支撑法线；爬升时 = 接触法线）
8. 跟随架（核心球心，不吃代理起伏）
```

**两条顺序纪律**：
1. **支撑与爬升独立选**：球在地面上推墙时，support = 地面（RIDE）、wall = 墙（CLIMB），两者同时存在。若只选“一个主导接触”，爬永远起不了步（参考模型在改对之前就是这样）。
2. **`climbSurfaceId` 不参与退出**：45° 坡顶把球交给台面棱时，surface id 变了、法线没变；用 id 判断会在每个接缝处打断爬升。改用“接触类 + 相对角”。

### 3.5 参数表（全部 PROVISIONAL，除 `k_h`）

| 参数 | 默认 | 作用 | 谁需要试玩 |
|---|---:|---|---|
| `k_h` | **1.0** | `H_cap = k_h·2r`，**唯一有官方锚的数** | 是（0.75 / 1.0 / 1.25） |
| `λ` | 1.0 | 凸出物对 `H_cap` 的贡献 | 是 |
| `θ_ride` | 45° | 滚/爬分界 | **是（45/50/55）** |
| `Δθ_climb` | 15° | 爬升必须比支撑陡多少 | 是（10/15/20） |
| `v_min` | 1.5 m/s | 90° 的速度门 | 是（1.0/1.5/2.0） |
| `α` | 1.0 | `sin^α` | 否（1.0 已够） |
| `k_intent` | 0.5 | 目标速度折算成“推入速度” | 是（0.3/0.5/0.8） |
| `T_climb` | 4.5 s | 到 `H_cap` 的时间（决定所有球径的爬升速度） | **是（3/4.5/6）** |
| `m0` | 0.15 | 90° 的 creep 下限 | 是（0.05/0.15/0.25） |
| `p_min` | 0.2 | 最小推入 | 是 |
| `a_climb` | 3 m/s² | 爬升速度的速率限制 | 是 |
| `cooldown` | 0.35 s | 退出/回滑冷却 | 是 |
| `cap_margin` | 0.02 m | 到顶判定的带宽 | 否 |
| `v_slide_max` | 1.0 m/s | 回滑限速 | 是（0.5/1.0/2.0） |
| `dashSpeedScale` | 1.6 | dash 速度倍率 | **是** |
| `dashSeconds` | 0.45 s | dash 时长 | 是 |
| `dashCooldown` | 1.2 s | dash 冷却 | 是 |
| `dash` 对爬升 | —（无独立常量） | dash 抬高 `v_gate` ⇒ `rate` 升到封顶 1.5（爬升快 50%）；**不新增常量** | 是 |
| `κ` / `N_max` | 0.5 / 8 | 子步 | 否（有证明） |
| `contactSkin` / `slop` / `iterations` | 5 mm / 0.5 mm / 3 | 接触注册/推挤 | 是（抖不抖） |
| `maxFallDisplacement` | `N_max·κ·r` | 下落每步上限（30 m/s @D=0.30） | 否 |
| `stuckSoft` / `stuckHard` / `recoveryHold` | 1.5 s / 3.0 s / 10 s 内 3 次 | 卡死恢复 | 是 |

### 3.6 数字：四档球径 × 三个角度

竖直爬升速率 `v_up`（m/s，满推、h=0；`rate=1.0`）：

| θ | m(θ) | D=0.30 | D=0.60 | D=1.10 | D=2.20 |
|---:|---:|---:|---:|---:|---:|
| 50° | 0.758 | 0.579 | 1.157 | 2.121 | 4.243 |
| 55° | 0.696 | 0.532 | 1.064 | 1.950 | 3.900 |
| 60° | 0.630 | 0.481 | 0.962 | 1.764 | 3.527 |
| 70° | 0.483 | 0.369 | 0.738 | 1.352 | 2.704 |
| 80° | 0.321 | 0.245 | 0.491 | 0.900 | 1.799 |
| 90° | 0.150 | **0.115** | **0.229** | **0.420** | **0.840** |

- 到顶时间（90°，到 0.98H）：**四档都是 ≈4.5 s**（这就是 `T_climb` 的意义）。
- 同一个 90° 墙，半推（`v_gate=1.0 < 1.5`）⇒ **不爬**；全推 ⇒ 爬。`rate` 在 `v_gate=2.0` 时为 1.0，dash（`v_gate=3.2`）时封顶 1.5。
- 70° 比 90° 快 3.2×，但仍只有平地速度的 18%（0.738 vs 4.0）。

**三个验收场景的走法**：

| 场景 | 几何 | 预期过程 | 判据 |
|---|---|---|---|
| **W1** 90° 低墙 | 墙高 0.15 m（= 0.5D，D=0.30） | 贴墙 → 爬升 0.15 m（约 1.3 s）→ 球心越过墙顶 → 棱变 RIDE → 滚上墙顶 → 从另一侧落下 | `max_bottom_rise ≥ 0.14`，`climbed_over == 1`，`support_kind` 出现 4 |
| **W2** 90° 高墙 | 墙高 3.0 m（≫ H_cap=0.60，D=0.60） | 爬到 `h≈0.58` → 锁定 → 以 ≤1.0 m/s 滑回地面 → 停在地面（不再爬） | `max_bottom_rise ≤ 1.02·H_cap`，`slide_back_m ≥ 0.3`，`recovery == 0`，`reclimb_count == 0` |
| **W3** 70° 坡 | 坡升 0.24 m（< H_cap=0.30，D=0.30），坡顶接 0.24 m 台面 | 贴坡 → 沿切线爬（0.369 m/s）→ 球心越过台面棱 → RIDE → 站上台面 | `deck_gap abs ≤ 0.01`，`climb_time ≤ 2.0 s`，`support_kind==4` 至少一帧 |

**一个必须写进作者指南的后果**：`H_cap` 是**高度**上限，不是长度上限。一条 70° 的坡如果升程 > `H_cap`（例如 D=0.60 的球遇到 1.0 m 高的 70° 坡），球只能爬到 0.6 m 然后滑回——**这是设计行为**，不是 bug。作者要么把陡坡的升程控制在 `H_cap` 以内，要么把坡度降到 ≤50°（可滚、无上限）。`levelbuild` 应在 S4 加一条 warning：`role=ramp` 且 `slope>50` 且 `rise > 2·(ball diameter)` ⇒ 提示“只能爬到约一个直径”。

### 3.7 凸出长物件（径向代理）的贡献

**今天**：径向代理只喂 `GroundContactMath`，只抬**接地高度**，不参与墙/爬升（审计 D 节；README “C 阶段没做”）。
**v2**：针尖是**一等接触**（§4A.3），会挡墙/台阶；这里只列它对**爬升高度**的贡献（默认开，`λ=1.0`）。
注意这是**有意的偏离专利**：专利的 `Mr`（16+4 针）只用于滚动/贴地，`Mh`（球盒）才做碰撞；我们按用户体感把针尖扩展到了所有接触：

```
L_eff = clamp( max_i dot(R·p_i, −n) − r , 0, capProxy )
capProxy = min(maxProxyProtrusionPerCoreRadius·r, maxProxyProtrusionWorldM)   # 今天 = min(0.35r, 0.30)
```

- `p_i` 是 ≤32 个端点（4 个长物件 × 8 角点），`R` 是 `visualRollRotation`；`−n` 是“朝墙里”的世界方向。
- 成本：32 次点积（≈0.1k flops），**只在有 CLIMB 接触且 `H_cap` 被读时算**（每步最多一次）。
- 效果上限很小：D=0.60 时 `capProxy=0.105` ⇒ `H_cap` 从 0.60 变 0.705（+17.5%）。**这是专利口径的必然后果**（碰撞不随凸出物长大），不是 bug。如果试玩觉得“长杆应该明显更好爬”，要么抬 `maxProxyProtrusionWorldM`（会同时抬接地，风险大），要么给爬升一个独立的 `L_climb` 上限（**需拍板**，推荐先不动）。
- **它不改变 `h<r` 的滚越阈值**：凸出物只延长“能爬多高”，不改变“棱在球心下方就能滚过去”。

### 3.8 dash（新机制，全部 OUR design）

| 项 | 值 | 说明 |
|---|---:|---|
| 触发 | 输入层的一个动作（`ControlIntentMath` 的 action），桌面 = 按住左键 + 前进？ | **需拍板**：键盘/手柄映射由输入适配层定；本设计只要求“一个离散动作 + 一个持续时间” |
| 速度倍率 | 1.6×（4.0 → 6.4 m/s；2.20 档 3.0 → 4.8） | 子步仍 N=2（0.128 m/步 = 0.854r） |
| 时长 / 冷却 | 0.45 s / 1.2 s | |
| 爬升 | `rate` 从 1.0 抬到 1.5（封顶）⇒ 爬升快 50% | 不改变 `H_cap` |
| 用途 | ① 冲过 0.5D–1.0D 的台阶（更快到顶）；② 短距离加速；③ 勉强过速度门 | |

**dash 不改变几何**：它不能让球爬上 > `H_cap` 的墙，也不能让 `h>r` 的棱变成可滚。W5 因此测“更快”，不是“能/不能”（§6.2）。

### 3.9 坡面切向重力：默认 0，保持兼容

- 今天 `rampTangentialGravityScale = 0`：球停在 30° 坡上不动（`TERRAIN_SUPPORT` §5.4）。v2 的 **RIDE 带完全保留这个行为**：位置推挤只做相切，不给切向加速度。
- v2 **新增**：CLIMB 带在“不爬”时用 `v_y` 的重力 + `v_slide_max` 回滑，**但不动 `v_planar`**。所以：
  - 45° 坡（RIDE）松手 = 停住（与 v1 逐位一致）；
  - 70° 坡（CLIMB）松手 = 慢慢滑回（新行为，符合“陡坡站不住”的直觉）。
- 若以后把 `rampTangentialGravityScale` 开到 1：只在 RIDE 带沿下坡方向加 `g·sinθ·cosθ`，CLIMB 带不受影响（它已有自己的回滑）。**默认 0，v2 不需要改这个字段的语义。**

### 3.10 需要试玩才能定的五件事

1. `k_h = 1.0` 是否让**普通台阶**太容易上（0.5D–1.0D 的台阶现在“慢慢也能上”）——若太宽松，降到 0.6–0.75。
2. `T_climb = 4.5 s` 是不是“爬墙”的正确节奏（4.5 s 爬一个身位）。
3. 45° 的滚/爬分界：45° 坡必须能滚（C05）、50° 必须慢爬（C06）；如果 45/46 的手感跳变太大，考虑把 `T_climb` 在 45–55° 之间做一段混合，而不是移动分界。
4. 90° 的 creep 下限 `m0=0.15`（0.229 m/s @D=0.60）是否“极慢但可感知”。
5. dash 的 1.6×/0.45 s 是否够“冲一下”，又不至于让所有墙都变容易。

---

## 4. 尺寸耦合

### 4.1 三个形状：见 §4A

今天 `collisionRadius`、`coreRadius`、`semanticDiameter` 是同一个球的三个名字（两个 ratio 都是 1），而 `semanticDiameter` 同时被视觉、相机、`stepLimit`、吸收门四个消费者读。v2 **不再保持 1:1**，而是按用户体感拆成 **Visual / Rolling / Pickup** 三个对象（§4A）：

```
r_visual = 0.95·r_phys      # 核心网格/相机/cull
r_roll   = 1.00·r_phys      # 接触核心（+ 16 根针尖）
pickupReach(û) = max(r_phys·(|ûx|+|ûy|+|ûz|), rollReach(û))   # 触发体
```

本节以下只保留**长大/缩小/埋没**的规则（§4.2/§4.3）；形状定义、数据、消费者、以及 `collisionToCoreRadiusRatio` / `semanticDiameterPerCoreDiameter` 的重新定义全部在 **§4A**。任何在 §4.2/§4.3 里出现 `r` 的地方，**默认指 `r_phys`**；涉及“看得见/看不见”的地方用 `r_visual`。

### 4.2 长大与正在进行的接触

吸收会让 `r` 在一步内变大（一件大道具可能让 D 变几厘米）。球长大了会**嵌进**正在接触的墙/地面。规则：

```
每步尺寸更新之后（§3.4 的第 5 步）：
  Δr = r_new − r_old
  if Δr ≤ 0: 不处理（缩小只会离开接触）
  else:
    for iter in 0..1:
        contacts = Query(C, r_new)
        deepest = argmax depth
        if deepest == null or deepest.depth ≤ 0.5 mm: break
        push = min(deepest.depth, maxGrowthPushPerStep)      # 0.05 m
        C += deepest.normal · push
    若 residual > 0.02 m：本步回退到 r_old（体积/分数**不回退**），sizeApplyDeferred = true
                        下一步再试（球已经在往外推了）
```

- **为什么沿法线推、不沿“径向”**：球长大是各向同性的，最近的自由方向就是接触法线；沿法线推 = 把球“抬/挤”出几何，符合直觉。
- **为什么有 0.05 m/步的上限**：一次吸收让 D 从 0.30 跳到 1.10 是数据错误，不该让球瞬移 0.4 m；分几步推完，观感是“球把墙顶开一点”。
- **`sizeApplyDeferred` 的语义**：**数据（体积/分数/直径）已经提交，只有“碰撞半径的生效”延后一步**。这样不会出现“吃了东西但没长大”的账本不一致；测试场读 `GetSemanticDiameterWorldM()` 仍然立刻变大（视觉先长），接触半径下一步跟上。**这是一个刻意的 1 步不一致，必须写进 evidence。**
- **推挤后要重算支撑**：中心动了，`supportHeight/normal` 必须用新位置重查一次（否则视觉滚转的法线是旧的）。这一步复用 §3.4 第 4 步的同一个接触查询。
- **视觉滚转**：长大推挤的位移**不进** `VisualRollMath`（它不是滚动）；若推挤 > 0.02 m 置 `discontinuous` 一帧，避免球“自己转一下”。

### 4.3 埋没的吸附物：显示层 cull，数据保留

**原作口径**（`[专利]`）：吸附时记录位姿、父子挂接、**不缩放**；球长大后比球小得多的物件**不显示**，但体积仍算。社区侧（日文 wiki / 5ch）也记“小物件很快消失”。

**v2 设计（只写设计，不改代码）**：

```
吸附时（每次一件）：
    localCenter = 球局部坐标下的道具中心
    localHalf   = 道具局部 AABB 半长（已有 propLocalBounds）
    farRadius   = |localCenter| + |localHalf|          # 最远角点的保守上界
    buried[i]   = false

尺寸变化 > 2 mm 时（吸收/交分后，最多几次/秒）：
    for each attached prop i:
        buried[i] = (farRadius[i] ≤ r_core − buriedMargin)     # buriedMargin = 0.02 m
        if buried[i] != lastBuried[i]:
            prop.SetActive(!buried[i])                          # 只切显示
            buriedCount += ±1
交分缩小后：用同一判据反向恢复显示
```

- **判据用 `r_visual` 而不是 `r_roll`**：cull 是“看不见”，可见性由视觉核心（0.95 r_phys）决定；用滚动半径会把“已经戳出视觉球、其实看得见”的物件藏掉。
- **数据全保留**：体积、分数、吸附列表、径向代理端点都不动；埋没的物件 `L_eff ≤ 0`（它在核内），所以对 `H_cap` 无贡献——**数据与物理自洽**。
- **成本**：只在尺寸变化时 O(已吸附件数)（≤110 × 4 flops）；不是每步。
- **不做**：不做淡出动画、不做 LOD 分级、不删除（原作也不删）。视觉细节交给表现层。
- **反向的坑**（审计 E 节 #9）：`CheckAttachmentRootOnce` 只查距离，若 `AttachmentRoot` 被误挂到 `CoreMesh` 下，`localScale` 会跟着球长大。v2 要求吸附宿主同时断言“父节点是 AttachmentRoot、其 lossyScale=1”，这条与 cull 无关但必须一起修。

### 4.4 边界

| 事 | v2 的归属 |
|---|---|
| 吸收门（体积/最长边） | **不动**：`PickupAbsorbMath` 继续用 `semanticDiameter`；接触求解不参与“能不能吸” |
| 账本/交分 | **不动**：尺寸仍是账本的纯函数；长大推挤不写账本 |
| 视觉滚转 | 用 `supportNormal`；爬升时用接触法线；长大推挤不算滚动 |
| 跟随架 | 读**核心球心**（空代理），不读接触抬升；爬墙时眼点跟着球心上升（这是想要的） |
| 远端球 | 不参与本地接触；`RemoteProxyMath` 不变 |

---

## 4A. 球的三层形状（Visual / Rolling / Pickup）

> 本章按用户 2026-11-15 的实机体感补充，并**修正 §4.1**：不再说“只有一个物理球”。
> 结论：**三个明确分开的对象**，各有名字、数据、使用者与禁令；它们之间是**有序**的（§4A.2 的不变量），不是三个可以互相代用的半径。

### 4A.1 证据与体感

| 证据 | 级别 | 内容 | 我们怎么用 |
|---|---|---|---|
| US7402104B2 图 4A/4C、S4/S54 | `[专利]` | 三个模型：显示 `Mp`、碰撞 `Mh`（等体积球的**外接盒**）、滚动 `Mr`（16 固定针 + 4 可选针，针尖是接地点） | 本章三个对象的**原型**；但专利的 `Mr` 只用于滚动/贴地（见 §4A.3 的偏离声明） |
| 原作手册 “current diameter of the katamari” | `[官方]` | HUD 报的是**直径** | HUD 口径 = 物理等效球直径 `2·r_phys` |
| REROLL 资产 `core_01 … core_21` + KF 的 CORE 文件夹 | `[数据挖掘]` | 视觉核心是**低模“杂物团”**，按尺寸换 21 档模型，不是纯色球 | 视觉层：核心网格可非球形、按档换模型 |
| `/tmp/orig_physics` §10.3 | `[推断]`（置信度中） | `D_vis ≈ D_HUD`，核心半径 ≈ `0.9–1.1 r_phys`，最佳猜测 **0.95** | `r_visual = 0.95·r_phys`（OUR 初值） |
| 同 §10.6 | `[推断]` | “粘附圈不要单独设半径；用碰撞代理 + 10% 体积门；若必须留，取 `1.0 r`（盒角最多 `1.7 r`）” | 拾取层用**外接盒支撑函数**（§4A.4），不另拍一个常数 |
| 用户实机体感 | 用户 | ① 粘贴/拾取范围 > 视觉球、也 > 滚动碰撞球；② 大多数时候是小棍撑出来的滚动体在“撑着滚”，不是核心球；③ 视觉球本体甚至不是球 | 三个对象 + 针尖进入接触求解 + 视觉层独立 |
| 现有审计 | 代码事实 | 针尖只抬高接地高度；扫掠只用 `collisionRadius`，不含突出；C 阶段（针进墙/挡路）没做 | **本设计要修的差异**（§4A.3） |

### 4A.2 三个对象与不变量

| 对象 | 定义 | 数据（UdonSharp，并行数组/标量） | 使用者 | **禁止** |
|---|---|---|---|---|
| **VisualShape** | 核心网格（低模杂物团，21 档）+ 粘附物挂载点（`AttachmentRoot`） | `visualCoreRadius`、`visualTier`(1..21)、`coreMesh` 引用、挂载 `Transform` | `BallView`（缩放/换档）、跟随架相机半径、埋没 cull（§4.3） | 不进接触、不进拾取、不进 `stepLimit`、不进吸收门 |
| **RollingShape** | 核心球 `r_roll` + 16（可选 +4）根**球局部**针的支撑长度；针尖随球滚动旋转 | `rollCoreRadius`、`needleDirLocal[16]`、`needleSupport[16]`、`needleCompact[]`+count、`needleTipRadius`、`needleProtrusionCap` | `ContactSolveMath`（核心接触 + 针尖接触合并）、支撑选择、`VisualRollMath` 法线、爬升的 `L_eff`/grip | 不进拾取 reach、不进吸收门、不写账本 |
| **PickupShape** | 触发体：等体积球的**外接盒支撑函数** ∪ 滚动形状（针尖也算“能粘”） | 无状态；由 `r_phys` + `RollingShape` 现算 | **只有** `PickupAbsorbMath` 的候选 reach（能不能粘） | **绝不**做阻挡、推挤、去速、支撑；不进 `ContactSolve` |

**不变量（必须写进代码注释并测）**：

```
r_visual = 0.95·r_phys
r_roll   = 1.00·r_phys
rollReach(û) = max(r_roll, max_i needleSupport[i]·dot(dir_i, û))     ≤ r_roll + cap
pickupReach(û) = max( r_phys·(|ûx|+|ûy|+|ûz|), rollReach(û) )       # 盒 ∪ 针
⇒ r_visual < r_roll ≤ rollReach(û) ≤ pickupReach(û)                   ∀û
```

- **为什么 pickup 要 ∪ 针尖**：一根 1.5 m 的杆伸出去 0.3 m 时，`rollReach` 可以超过盒的**轴向上限** `r_phys`。如果拾取只取盒，就会出现“杆能撑住墙、却粘不到杆尖前面的东西”——违反用户的“拾取 > 滚动”。取并集后，不变量由构造保证。
- **为什么 visual 可以比 roll 小**：视觉核心只是“被吸附物盖住的那颗杂物团”。它小 5%，物理球仍按 `r_phys` 接触，视觉上不会出现“球悬空 5%”，因为吸附物本来就在外表；这是原作的估计值（§10.3），不是硬证据。
- **三个半径都从同一个体积来**：`r_phys = cbrt(3V/4π)`，V = 初始 + Σ 已确认吸附体积。视觉与拾取都是它的**确定函数**，不是第二套账本。

### 4A.3 Rolling：16 根针尖怎么进入接触求解

**数据**（球局部系，固定方向；长度是“支撑长度”，≥ `r_roll`）：

```csharp
const int NeedleCount = 16;                 // 专利：16 固定；可选 +4
const int OptionalNeedleCount = 4;
const int MaxNeedleContacts = 4;            // 合并进接触集的上限
Vector3[] needleDirLocal   = new Vector3[16];   // 单位，Fibonacci 球面，球局部
float[]   needleSupport    = new float[16];     // 由径向代理的端点支持函数给出
int[]     needleCompact    = new int[16];       // 只有 support > r_roll+1mm 的下标
int       needleCompactCount;
Vector3[] needleTipWorld   = new Vector3[16];   // 每步由 visualRollRotation 旋转
float     needleTipRadius  = 0.01f;
float     needleProtrusionCap;                  // = min(0.35·r_roll, 0.30)
```

**支撑长度从哪来**：沿用径向代理的**精确端点云**（≤4 个长物件 × 8 个 AABB 角点 = 32 点），对每根针方向取 `max_i dot(p_i, dir_i)`，再 `clamp(·, r_roll, r_roll + cap)`。
- 今天 `proxySupports[32]` 就是这张表，但**没有物理消费者**（只被 attach/detach/诊断读）；v2 让它第一次进入接触。
- **32 → 16 是另一个 agent 的范围**：本设计按 16 写；在它落地前，物理侧取 32 张表里“16 个方向”的子集（或直接 32 也成立，容量翻倍而已）。**不要**为了这件事改径向代理文件。
- 视觉杆可以比 `cap` 伸得更长（美术自由），**物理针尖到 cap 为止**；这个差异必须显示在调试读数里（`GetNeedleProtrusionCapWorldM()`），否则又会出现“看起来撑着、物理没撑”。

**每步算法**（在核心接触的同一次子步里）：

```
1. 只遍历 needleCompact（support > r_roll + 1 mm；典型 2–6 根，上限 8）
2. 每根：worldDir = R · dirLocal；tipNow = C + worldDir · support
       tipPrev = C_prev + R_prev · dirLocal · support        # 上一步/上一子步
3. 对每个 near OBB（窗口半径 = r_roll + cap，见 §5.2）：
   a. 段包围球早退：|mid − center| > boundingRadius + |seg|/2 + tipRadius → skip
   b. **针尖扫掠段 vs OBB**：把 tipPrev/tipNow 变到局部，做“膨胀 tipRadius 的 slab test”，
      取进入参数 t 与进入面法线；命中即记录 needle 接触（point=进入点，depth=0+tipRadius 的余量）
4. 对每个平面：tip 的 signed distance ≤ tipRadius → needle 接触
5. 合并：core 接触（≤8）+ needle 接触（按深度取前 4）→ 按 depth 排序取 ContactCapacity=12
   同一 surfaceId 上 core 与 needle 都有时，保留 core（避免同一面推两次）
6. 推挤（§2.3）照旧：needle 接触的推出移动**整个球**（刚体）
```

**为什么针尖必须用“扫掠段”而不是“点测试”**（这条是硬要求）：
球在滚，针尖的世界速度 = `v_center + ω × r_tip`。D=0.30、4 m/s 时 `ω = v/r = 26.7 rad/s`，一根伸出 0.45 m 的针尖速度可达 **16 m/s**，一个子步扫过 **0.32 m**，比 0.1 m 的薄墙还长——点测试会**穿过薄墙**。段 vs OBB 是**精确**的（不是采样），所以针尖也有和核心一样的“不穿透”保证。参考模型 `shapes.py` 只实现点测试（静态几何 + 数值），**C# 必须实现段测试**；两者的一致性由 S2 的 fixture 覆盖（§7.2）。

**合并后的容量与成本**：

| 项 | 最坏 | 典型 |
|---|---:|---:|
| 16 方向旋转（四元数×向量） | 16×18 = 0.3k | 只算 compact：2–6 根 → 0.1k |
| 针尖段 vs OBB（≤8 compact × ≤12 near × 2 子步） | 8×12×2 × 40 = 7.7k | 4×3×2×40 = 1.0k |
| 段包围球早退 | 8×12×2×6 = 1.2k | 0.3k |
| 平面（16 dot） | 0.1k | 0.1k |
| **合计（相对 §1.8 的核心接触）** | **≈ +9k flops/步** | **≈ +1.5k** |

> 最坏情形（4 个长物件把 16 根针都撑出来 + 12 个 near OBB + 2 子步）是 **≈1.3 万 flops/步**（§1.8 的 4.2k 核心 + 这里 9k），比只有核心时高 3–4×。**这是“针尖参与墙/台阶”的代价**；如果 profiler 说不行，降级路径是“针尖只对支撑面与最近 4 个盒”，代价是丢“杆挡墙”。先按上限实现，用 `GetNeedleContactCount()` / `GetLastStepMicros()` 实测再决定。

**针尖进墙是“偏离专利”的**：专利的 `Mr` 只用于滚动/贴地，`Mh`（球盒）才做碰撞。用户要的体感是“杆撑出来的东西也挡墙”。所以 v2 **有意**让针尖参与所有接触（地面/坡/墙/台阶/道具）。后果要写进关卡规则：
- 门洞/夹缝的有效净宽从 `2r` 变成 `2(r + cap)`（D=0.60 时 +0.21 m）；
- 一根横向伸出的 1.5 m 杆会让球“进不了 1.5 m 的门”——这是**想要**的（原作里长杆确实碍事），但关卡必须按 `2(r+cap)` 校验门宽。
- **需拍板**：是否只让针尖参与**支撑 + 爬升**、不做横向阻挡（更接近专利、门洞不变）。推荐“全参与”，因为用户的差异点正是“撑出来的东西在碰”。

**针尖对爬升的贡献**（两个通道，都用同一个 `L_eff`）：

```
L_eff = clamp( max_i dot(worldDir_i, −n)·support_i − r_roll , 0, cap )   # 沿墙法线朝里的突出
H_cap += λ·L_eff                                                          # §3.3
grip   = clamp(L_eff / (0.5·r_roll), 0, 1) · k_grip                       # k_grip = 0.5
v_req(θ) ← v_req(θ) · (1 − grip)                                          # 抓地：门槛最多降一半
```

- 针尖撞到墙时，它本身就是一个 `CLIMB` 接触（θ 用针尖接触的法线），所以“针尖勾住墙”在接触集里是**真实接触**，不只是参数加成。
- `L_eff=0.15`（半个 r_roll=0.30）⇒ grip=0.5 ⇒ 90° 的 `v_req` 从 1.5 降到 0.75：轻推也能爬。这是“抓手”的量化。
- `H_cap` 的加成受 `cap` 限制：D=0.60 时最多 +0.105（+17.5%），和 §3.7 一致。

### 4A.4 Pickup：触发体，永远不是阻挡体

**表示**（方向相关，专利 `Mh` 的盒支撑函数）：

```
pickupReach(û) = max( r_phys · (|ûx| + |ûy| + |ûz|) , rollReach(û) )
                   轴向 1.00 r_phys
                   面对角 1.41 r_phys
                   体对角 1.73 r_phys
```

**消费者只有一处**：`PickupAbsorbMath` 的候选 reach：

```
reach = pickupReach(û_ball→prop) + contactRadiusWorldM + propBoundingRadius   # contactR 仍默认 0
可吸 ⇔ 距离 ≤ reach 且 propVolume ≤ min(512, (π/6)·(2·r_phys)³/10)
```

- **与今天的差别**：今天 `reach = collisionRadius + contactR + propBoundingRadius`，轴向等价；v2 在斜方向上把 reach 放大到 1.41/1.73 倍。**这正是“拾取范围 > 滚动范围”**，且不需要新常数。
- **与挡路的关系**：挡路是**另一条**判定（`propBounds` 与滚动形状的接触/支撑分类），不是 pickup。pickup 只回答“能不能粘”，不回答“挡不挡”。**代码上必须分开**：`ContactSolve` 拿不到 `pickupReach`，`PickupAbsorbMath` 拿不到接触法线。
- **“隔墙吸”的危险**：reach 变大后，墙后的道具可能进候选。今天的候选收集本来就不做视锥/遮挡（`SubmitCandidate` 由宿主收集），所以这不是新问题，但会放大。三个选项：
  - ① 接受（原作也基本如此：吸附判定用球盒 + 体积门，没有遮挡）；
  - ② 加一次 `Physics.Linecast`——**Udon 里没有**，不能用；
  - ③ 用接触集做一次“中间是否有面”的判定（用 `supportSurfaceId` 与距离的粗略检查）——不可靠。
  **推荐 ①**，并在 evidence 里记录“隔墙可吸”的已知行为；如果试玩觉得穿墙吸太假，再单独设计（不在 v2 范围内）。
- **候选容量 4 不变**：pickup 变大只改变“谁进候选”，不改变容量；满了仍然丢并计数。

### 4A.5 两个旧比例怎么重新定义

| 旧量 | 今天 | v2 | 消费者怎么改 |
|---|---|---|---|
| `collisionToCoreRadiusRatio` | 1，`collisionRadius = coreRadius` | **删除**；换成两个显式常数：`rollingCoreToPhysicalRadius = 1.0`、`visualCoreToPhysicalRadius = 0.95` | 接触用 `r_phys`；视觉用 `r_visual`；**不要**再有一个“collisionRadius”同时喂扫掠和 reach |
| `semanticDiameterPerCoreDiameter` | 1，`semanticDiameter = 2·coreRadius` | **删除**；拆成三个量（见下表） | 每个消费者只读自己那个量 |
| `collisionRadiusWorldM` | 扫掠 + reach + 重生高度 | **降级为 `r_phys` 的别名**（兼容 getter），**禁止**用于 reach | 重生高度继续用它（= r_phys，对的） |
| `semanticDiameterWorldM` | 视觉、相机、`stepLimit`、吸收门 | **拆**：`physicalDiameter = 2·r_phys`、`visualDiameter = 2·r_visual`、`pickupReach(û)` | 见下表 |

| 消费者 | 应该读 | 理由 |
|---|---|---|
| HUD “直径” | `physicalDiameter` | 官方手册口径；与吸收门同一个标量（§10.6） |
| `CoreMesh` 缩放 | `visualDiameter` | 视觉核心 0.95 r；只在表现层 |
| 跟随架相机半径 | `r_visual` | 眼点公式按“玩家看到的球”取；差 5%，可接受 |
| `stepLimit`（仅诊断） | `0.25·physicalDiameter` | 物理已不用它；测试场旧判据还要读 |
| 吸收体积门 | `physicalDiameter` | 体积 = 物理等效球；与账本同源 |
| 拾取 reach | `pickupReach(û)` | §4A.4 |
| 接触/推挤/爬升 | `r_roll = r_phys` + 针尖 | §4A.3 |
| 视觉滚转的半径 | `r_roll` | 无滑动滚动按物理半径，否则转速看起来会差 5% |
| 埋没 cull | `r_visual` | 可见性由视觉核心决定（§4.3） |
| PVP/远端摘要 | `physicalDiameter`（主）+ `visualDiameter`（只显示） | 玩法判定只认物理 |

**迁移时的硬检查**（S4）：
1. 全仓 `grep semanticDiameter`：每一个消费者必须落到上表的一行；出现第四个新用途就停下来问。
2. `collisionToCoreRadiusRatio` / `semanticDiameterPerCoreDiameter` 的序列化字段**保留但忽略**（读出来只打 warning），避免旧场景反序列化丢字段；代码里不再有这两个乘法。
3. 调试面板同时打 `r_phys / r_visual / rollReachMax / pickupReachMax`，四个数一起看。

### 4A.6 与前面章节的冲突清单（本章落地时要一起改）

| 位置 | 原说法 | 改成 |
|---|---|---|
| §0.5 口径 1 | “r = collision = core，一个物理球” | 三个形状，按 §4A.2 的不变量 |
| §1.3 接触数组 | 无 `shape` 字段，容量 8 | 加 `contactShape`（0 核心 / 1 针尖 i），容量 **12**；加针尖数组 |
| §1.8 成本 | 只有核心接触 | 加 §4A.3 的针尖段测试（典型 +1.5k，最坏 +9k flops/步） |
| §2.1 接触记录 | 无 `shape` | 加 `shape` 字段 |
| §2.5 支撑 | 只有核心接触 | 允许“只有针尖支撑”：`supportKind=4`，`supportHeight` 用针尖处切平面，`support_gap` = 突出量 |
| §2.9 尺寸耦合 | “只用一个 r” | 改为三形状（§4A.5） |
| §3.7 凸出物 | “可选，只加 H_cap” | 针尖是**一等接触**；`L_eff` 仍加 H_cap，另加 grip（§4A.3） |
| §4.1 | 三个半径 1:1，保持 | 改为“三层对象 + 0.95 视觉”，指向 §4A |
| §4.3 埋没 cull | 用 `r_core` | 用 `r_visual` |
| §5.2 窗口半径 | `r + travel` | `r_phys + cap + travel`（针尖能比核心先碰到） |
| §6 测试 | 无形状格 | 加 W7/W8/P1（§6.2） |
| §8 拍板 | 无形状项 | 加视觉 0.95、pickup 盒、针尖挡墙、隔墙吸 |

### 4A.7 新增测试格（与 §6.2 同一张表）

| 格 | 几何 | 起点 / drive | 判据 |
|---|---|---|---|
| **W7** 带长杆的球贴墙 | D=0.60 球 + `ptf_rod_1500`（1.5×0.06×0.06），杆水平指向墙；墙 3 m 高 | 起点墙前 1.5 m，杆朝 −z；t=0..3 s 全推 | 有杆时核心到墙面的距离比无杆时**大** `L_eff ± 0.02`（D=0.60 时 L_eff=0.105，被 cap 夹住）；针尖到墙面 ≥ −1 mm（不穿）；至少一帧 `contact_shape == needle`；`climb_height` 出现（针尖勾墙） |
| **W8** 长杆撑起球过台阶 | 台阶高 0.65 m（D=0.60：`H_cap` 无杆 0.60 < 0.65 < 有杆 0.705），宽 4 | 两次子运行：无杆 / 有杆（杆朝上坡方向，突出 0.105）；t=0..8 s 全推 | 无杆：`max_bottom_rise ≤ 1.02·H_cap(no rod)` 且滑回；有杆：`deck_gap abs ≤ 0.01`（上去了）；有杆 `max_bottom_rise ≤ 1.02·H_cap(with rod)` |
| **P1** 拾取 > 滚动但不阻挡 | 一件可吸小件放在**面对角**方向 1.2·r_phys 处（旋转 45°）；另一件吸不动的挡路件放在同样距离 | 起点在 2.0 m 外，朝道具直线滚过；t=0..2 s 全推 | 可吸件：`absorbed_delta == 1`，且该件**不产生任何接触**（`contact_shape` 不指向它、速度变化 ≤ 0.02 m/s）；挡路件：核心停在 `r_phys + 该件包围球` 处（滚动口径），**不是** 1.2/1.73·r_phys（拾取口径）；`pickup_reach_flag == 1` |

**容差推导**：W7 的 0.02 = 针尖 cap 的 19%（0.105 的 19%），覆盖端点云的 AABB 角点量化；W8 的 0.65 取在 `H_cap` 两侧各 0.05 的中间，避免边界抖动；P1 的 1.2 r 取在盒的轴向（1.0）与面对角（1.41）之间，确保“只有拾取能到、滚动到不了”。

---

## 5. 大地图：静态容量、空间粗筛、弯坡与手动沙盒

> 本节回答 Lead 补充的两条硬需求：**地图 0 是“一块 60×60 的大平台 + 几十个坡/弯坡/道具”**，今天 8 盒 / 4 坡的容量装不下；
> 以及**弯坡怎么表示**。结论先说：静态表 256、每步近集 24，用“按球位置扫一遍静态表 + 滞回窗口”，**不引入网格也能跑**；
> 弯坡用**一串首尾重叠的薄片 OBB**，在导入器展开，运行期只看见 OBB。

### 5.1 问题

| 今天 | 地图 0 需要 | 差多少 |
|---|---:|---:|
| 静态盒 8 + 斜面 4 = 12 个实体 | 估 **118** 个 OBB（坡/矮墙 16 + 弯坡 72 + 台阶 8 + 矮墙 6 + 跑道牙 16） | **~10×** |
| 每步查询 = 全部登记（12） | 不能每步查 118（成本 ×10） | 需要粗筛 |
| 平面 8 / 挡路 6 / 支撑道具 6 | 5 平面 / 12 道具（同时近的 ≤6） | 够 |
| 单坡 4 段、直坡一条 | 螺旋 32 段、半管 12 段 | 一条弯坡就超容量 |

**不能做的事**：为了装下而“静默丢掉第 13 个坡”（v1 的 `AddBox` 满了就是 `return false` + 计数）。大地图上丢几何 = 球从坡上穿过去，且没人看得见。

### 5.2 静态表 + 每步近集（窗）

**两级数据**：

| 级 | 容量 | 生命周期 | 谁写 |
|---|---:|---|---|
| **静态表** `staticObb*` | `MaxStaticObbCount = 256` | 关卡注册一次（`LevelArenaHost.Setup`），换格 `Reapply` 重建 | 宿主 |
| **近集** `nearObb*` | `MaxNearObbCount = 24` | 每步重算（不分配） | 接触求解器 |
| 平面 | 8 | 同静态表 | 宿主 |
| 挡路/支撑道具 | 6 + 6 | 宿主 20 Hz 扫描（**已有**的 `SelectNearest` 就是道具的窗） | `PickupFieldHost` |

**窗口半径（由几何决定，不是拍的）**：

```
R_in  = r_phys + needleProtrusionCap + maxStepTravel + 0.05   # 针尖能比核心先碰到（§4A.3）
R_out = R_in + 0.5                        # 滞回带
maxStepTravel = |v|·dt + (g + a_climb)·dt²          # 4 m/s、dt 0.02 ⇒ 0.085
```

| 球径 | cap | `R_in` | `R_out` | 窗直径 `2·R_out` | 与 8 m 网格的关系 |
|---:|---:|---:|---:|---:|---|
| 0.30 | 0.0525 | 0.34 m | 0.84 m | 1.68 m | ≪ 8 m，3×3 查询足够 |
| 2.20 | 0.30 | 1.54 m | 2.04 m | 4.08 m | < 8 m，3×3 查询足够 |

**每步扫描**（`ContactSolve.FillWindow`）：

```
nearCount = 0; dropped = 0; candidateCount = 0
for i in 0..staticCount−1:
    d_approx = |C − center_i| − boundingRadius_i        # 6 flops，保守下界
    if d_approx > R_out: continue
    d = exactDistanceToObb(i, C)                          # 30 flops
    if d ≤ R_in 或 (已在上一步窗内 且 d ≤ R_out):
        candidate[candidateCount] = (i, d); candidateCount++
# 候选按距离插入排序（候选 ≤ 256，容量 24 ⇒ 最坏 24×256 次比较，只在溢出时）
sort candidate by d
for k in 0..min(candidateCount,24)−1: CopyToNear(candidate[k])
dropped = max(0, candidateCount − 24)                      # 见 §5.4
```

### 5.3 刷新规则与滞回

| 规则 | 值 | 为什么 |
|---|---|---|
| 扫描频率 | **每步**（每 20 ms） | 位置每步都变；扫描是 200 次保守距离（≈1.2k flops）+ 幸存者的精确距离（§1.8） |
| 近集重建 | **只在 id 集合变化时**拷贝数组 | 稳态下 id 集合不变，拷贝 0 次；`nearRebuilt` 计数 |
| 进入半径 | `R_in` | 只有“可能碰到”的才进 |
| 退出半径 | `R_out = R_in + 0.5` | 已在窗内的条目要离开 0.5 m 才被踢出；**边界抖动不会让集合每步翻** |
| 抖动上界 | 一个条目在 `R_in` 附近来回 1 mm，不会进出（因为退出要 0.5 m） | 这是滞回的唯一目的：**省重建，不改行为** |
| 确定性 | 窗口只由球心与静态表决定，**没有跨步的隐藏状态**（除 `previousIds` 用于滞回） | 传送/回滚后可复现 |

**为什么不是“注册进窗口”**：注册式窗口（进格才登记、出格才注销）需要维护“谁在谁不在”，一旦漏注销就是幽灵碰撞。**扫描式**没有这个状态：每步从位置重算，错也只错一步。

### 5.4 容量满：显式失败，不静默丢

```
若候选 > 24：
    dropped = 候选数 − 24
    nearestDropped = 第 25 近的精确距离
    nearComplete = (nearestDropped > r + 0.02)      # 丢掉的都够不着 ⇒ 物理上仍完整
    if !nearComplete:
        contactIncomplete = true                     # fail-closed
        broadphaseOverflowCount++
        每 1 s 最多打一条: [ContactV2] window overflow cell=… dropped=… nearest=…
    else:
        broadphaseSaturatedCount++                   # 只计数，不报警
```

- **`nearComplete == false` 时，求解器仍然只用 24 条**，但会把 `contactIncomplete` 置位；测试场判据 `contact_complete == 1` 失败，**这一格的结果作废**（而不是“看起来通过”）。
- **为什么保留最近 24 而不是拒绝新条目**：拒绝新条目会让球在“第 25 个坡”处穿过去；保留最近 24 至少保证“离球最近的都在”，并把不确定性显式化。
- **日志节流**：每 1 s 最多一条，带 cell、dropped、nearest；不每帧刷。
- **容量本身是数据校验**：`levelbuild` 在 S4 增加“每格静态 OBB ≤ 256、道具 ≤ 6 近邻”的检查；沙盒的 118 条远低于上限。

### 5.5 成本（估算）

| 项 | 每步 | 说明 |
|---|---:|---|
| 静态扫描（200） | 200 × 6 = 1.2k flops | 保守下界 |
| 精确距离（幸存者 ~10–30） | 30 × 30 = 0.9k | 只对通过保守测试的 |
| 近集拷贝（仅集合变化） | 24 × 20 float = 480 次写 | 低频 |
| 近集接触查询（2 子步 × ≤24） | 48 × 30 = 1.4k | 与 §1.8 同 |
| **合计** | **≈3.5k flops/步**（最坏 ≈4.2k） | 50 Hz ⇒ ≈0.2 Mflop/s |

**对比**：v1 的扫掠是“≤14 障碍 × 8 次 pass”，≈1.6k；v2 的静态扫描把它换成“200 × 6 + 近集查询”，约 2.2×。**这是“200 个几何”的代价，不是“每个几何都参与求解”的代价。**
如果实测超预算，升级路径见 §5.6（网格把 1.2k 降到 ~0.1k）。

### 5.6 升级路径：均匀网格（同一份窗口契约）

只在 **静态数 > 512** 或 **profiler 实测扫描 > 0.2 ms/步** 时启用；行为不变（同样的 `R_in/R_out/容量/失败语义`）。

```
cellSize      = 8 m                       # ≥ 2·R_out(最大球) = 3.5，且 ≥ 最大 OBB 边长的一半
gridOrigin    = 关卡作者给的矩形左下角
cellCountX/Z  = ceil(size / 8)             # 60 m ⇒ 8×8 = 64；512 m ⇒ 64×64 = 4096
int[] cellHead = new int[4096];            # −1 = 空；否则是第一条的下标
int[] next     = new int[256];             # 链表
建表：每条 OBB 用它的 XZ AABB（中心 ± boundingRadius）插入所有覆盖的格
查询：球所在格 + 相邻 1 圈（3×3），因为 R_out ≤ 8/2
```

- 内存：`4096 × 4 B = 16 KB` + `256 × 4 B = 1 KB`；比扫描式多 17 KB，换 10× 的扫描成本下降。
- **网格不改变任何判据**：它只是“更快的 `FillWindow`”。测试场对同一位置应得到**完全相同的近集**（S2 的 EditMode 用同一组 fixture 对两种实现做逐元素相等）。
- **不做**：层级网格、BVH、动态树——Udon 上没有 `List`/指针，做不动，也不需要（200–500 条）。

### 5.7 弯坡：表示比较与选择

#### (a) 一串薄片 OBB（**选它**）

一条弧按段角 `Δ` 切成 `K = ceil(Φ/Δ)` 段，每段是一个薄 OBB（宽 × 厚 × 弦长），**相邻段沿切向重叠一个矢高**：

```
弦长      L = 2R·sin(Δ/2)
矢高      s = R·(1 − cos(Δ/2))          # 弦与弧的最大偏差
重叠系数  overlap = 1 + 2s/L             # 凸侧不留缝；凹侧的凸起 ≤ s
法线跳变  Δ/2（相邻两段面法线之差）
```

**为什么重叠**：不重叠会在**凸侧**留一条缝，球会卡进缝里（正是我们要消灭的“接缝”）；重叠的代价是凸侧有 ≤ s 的小凸起，而球半径 r ≫ s 时会**同时压住两段**，推挤把两个法线平均掉，观感是连续曲面。

**段数与精度**（误差预算 = `5%·r_min` = 7.5 mm，`r_min = 0.15`）：

| 曲率半径 R | 90° 所需段数 K | Δ | 矢高 s | s / r_min |
|---:|---:|---:|---:|---:|
| 2 m | 10 | 9.0° | 6.2 mm | 4.1% |
| 3 m | 12 | 7.5° | 6.4 mm | 4.3% |
| 5 m | 15 | 6.0° | 6.8 mm | 4.5% |
| 10 m | 21 | 4.29° | 7.0 mm | 4.7% |
| 20 m | 29 | 3.10° | 7.3 mm | 4.9% |

（`K` 由 `s ≤ 0.05·r_min` 反解；R 越大，同样的矢高允许更小的 Δ、更多的段。`K` 是段数，弧对称时奇偶都不影响首尾对齐，取整即可。）

**接触法线连续性**：球同时压住两段时，法线在两个面法线之间；单段时是面法线。所以**球看到的法线跳变 ≤ Δ/2**；球心高度跳变 ≤ s。测试容差见 §6.3。

#### (b) 解析圆柱 / 圆环面段（**不选，留作逃生舱**）

**球对圆柱 patch 的最近点**（有闭式，代价 ≈ OBB）：

```
局部：轴 = local y，半径 R，半长 h，角度范围 [φ0, φ1]
ρ = sqrt(x² + z²)
q_inf = (R·x/ρ, clamp(y, −h, h), R·z/ρ)            # 无限圆柱
若 atan2(z,x) 在 [φ0,φ1] 内：q = q_inf，n = (x/ρ, 0, z/ρ)（ρ>R）
否则：最近点在端部圆弧/端盖上 —— **又变成一条棱**（法线是角点法线）
球心在 ρ<R 时：n = (−x/ρ, 0, −z/ρ)，depth = r + (R−ρ)
```

**圆环（双曲率）没有闭式解**：球–圆环最近点要解四次方程，Udon 上只能迭代（每步每段 ~5–10 次），成本是 OBB 的 10 倍以上，且迭代次数影响确定性——**直接排除**。

| | (a) 薄片链 | (b) 解析圆柱/圆环 |
|---|---|---|
| 新接触原语 | **0**（复用 OBB） | **1–2**（圆柱 patch、圆环）|
| 法线连续性 | 段内精确，段间 ≤ Δ/2 | patch 内 C¹ 精确；**patch 边界仍是硬棱** |
| 退化情形 | 无 | 球心在轴线上（ρ≈0）、patch 角度边界、端盖 |
| 圆环（水平+竖直双曲率） | 支持（螺旋） | **无闭式解**（四次方程），要迭代 ⇒ Udon 不可行 |
| 成本 | 每段一次 OBB（≈30 flops） | 每次 ≈50 flops + 边界分支 |
| 数据/导入 | 段由导入器展开，作者写一条 | 新 JSON 类型 + 新烘焙 + 新 EditMode |
| 结论 | **v2 用它** | 只在“半径 ≥ 20 m 的半管、段数 > 40”时考虑；届时它是**第二个接触原语**，与“一个求解器”的原则冲突，需单独立项 |

#### JSON 与导入器

```json
{
  "id": "curve_up_a",
  "role": "curve_ramp",
  "collision": "box",
  "center": [8.0, 0.0, 12.0],
  "rotationQuat": [0, 0, 0, 1],
  "size": [4.0, 0.25, 0.0],
  "curve": {
    "radius_m": 4.0,
    "arc_deg": 30.0,
    "segments": 8,
    "bend": "up",
    "axis": "local_x",
    "width_m": 4.0,
    "thickness_m": 0.25,
    "pitch_m": 0.0,
    "bank_deg": 0.0
  }
}
```

- `bend`: `up` / `down`（竖直面内的弧）/ `left` / `right`（水平面内的弧）/ `helix`（水平弧 + `pitch_m`）。
- **导入器在 edit time 展开**成 `segments` 条 `role=box` 的 structure（各自 `center` + `rotationQuat` + `size`），名字 `curve_up_a_03`，并写 `generated_from`/`generated_index`；**原 curve 条目保留**（`expanded: true` + `segment_ids[]`），删掉/改参数时整组重建。
- **为什么导入期展开**：运行期只看见 OBB（烘焙/容量/窗口/求解器全不变）；场景里能选中每一段排查；撤销就是删条目；不会出现“运行期生成的几何没进静态表”。
- **`levelbuild check`（S4）**：段数 ≥ 表 §5.7 的下限；`s ≤ 0.05·r_min`；相邻段重叠 > 0 且 < 段长的 25%；总静态 OBB ≤ 256；螺旋首尾不相交；`arc_deg ≤ 360`。
- **弯坡的判据**（§6.2 的 X1）：法线跳变 ≤ `Δ/2 + 3°`；球心高度抖动 ≤ `s + 2 mm`；全程 `fall_frames == 0`；升程 = `R(1−cosΦ)` ± 5 mm；接触段号单调（不来回跳）。

### 5.8 地图 0 手动沙盒：布局建议（只写文档，不改数据）

**定位**：与“9 个小格 + 自动化回归”**并存**，不是替换。小格证明“每个机制对”，沙盒给人**手动滚**、试手感、试尺寸耦合。
沙盒是一个独立场景（`Labs/PhysicsSandbox`），用同一套 `LevelArenaHost`/`motor`；一格一注册不再需要（大平台一次注册 118 条）。

**平台**：60 m × 60 m，x,z ∈ [−30, 30]，地面 `y=0`；四面边界平面（±30，高 6 m 的视觉墙 + 4 条平面）。
**分区**（矩形不重叠；坐标是区域中心/范围，单位 m）：

| 区 | 范围（x × z） | 内容 | 数量 |
|---|---|---|---:|
| **A 角度阶梯** | x −30…2，z 11…25 | 8 条坡，同升程 1.2 m，宽 3.2，x 中心 = **−28,−24,−20,−16,−12,−8,−4,0**（间距 4），坡脚 z=11：5°/15°/25°/35°/45°/60°/70°/90°；前 7 条的坡顶各一个 3.2×1.5 平台，90° 那条是 1.2 m 高 × 0.4 厚的矮墙（墙顶就是它的“平台”） | 8 坡 + 7 平台 = **15**（+1 墙 = 16 条 OBB） |
| **B 弯坡** | x 8…30，z 8…28 | B1 上弯 R4/30°/宽4 @ (8,0,12)；B2 下弯 R4/30° @ (10.1,0.54,12)；B3 水平 S 弯 R5/2×45°/宽3 @ (14,0,12)；B4 螺旋 R6/360°/螺距2.4/宽3 @ (24,0,22)；B5 半管 R3/120°/长8/宽3 @ (10,0,24) | 8+8+12+32+12 = **72** |
| **C 台阶阶梯** | x 4…14，z −28…−12 | 8 个独立台阶岛（3×2），高 0.02/0.05/0.075/0.10/0.20/0.40/0.60/0.80，z 间距 2.2 | **8** |
| **D 矮墙** | x −28…−8，z −28…−12 | 6 面墙，5 长 × 0.2 厚，高 0.15/0.30/0.60/1.20/2.40/4.00，z 间距 2.6 | **6** |
| **E 道具尺寸阶梯** | x 16…28，z −24…−12 | 从现有 44 种模型里由小到大挑 12 种（只列代表，尺寸取 `PROP_CATALOG.md` 的 `propBounds`，**不是**设计三轴）：`bottle_cap` 0.05 → `paper_scrap` 0.10 → `soda_can` 0.24 → `bread_slice` 0.30 → `watermelon` ≈0.25 → `thermos` 0.90（占位方块）→ `folding_table` 1.0×0.6×0.7 → `collapsed_bench` 1.6×0.55×0.81 → `stroller` 1.0×0.55×0.9 → `hand_cart` 1.1×0.5×0.55，另两件按同一梯度补；2 排 × 6，间距 2.4 | 12 道具 |
| **F 环形跑道** | 中心 (0,0)，中线半径 9，宽 3 | 外侧 16 段路牙（0.2×0.15×弦长），内侧开放；圈内是起点/球径台 6×6 | **16** |
| **起点** | 中心 (0,0) | `startCenterWorld = (0, r, 0)`；球径台在圈心 | 0 |

**几何数量**：静态 OBB **118**（A 16 + B 72 + C 8 + D 6 + F 16；按 200 条做预算，其余场景留余量），平面 **5**（地面 + 4 边界），动态道具 12，视觉墙 4。
**峰值近集估计**：球在螺旋里时窗内 ≈ 6–10 段 + 2–3 个其它 → **≤ 16 < 24**，有 1.5× 余量。
**峰值道具近邻**：道具行 2.4 m 间距、球 r≤1.1 → 同时近的 ≤ 3 < 6。

**v1 运动器下根本做不出来的**（所以必须等 v2）：

| 沙盒项 | v1 的结果 |
|---|---|
| 60°/70°/90° 坡 | `AddRamp` 按 45° 拒绝；写成 box 则是膨胀 AABB 墙，`2.2` 球连坡脚都到不了 |
| 任何爬墙 | 完全没有 |
| 弯坡（B1–B5） | 无原语；直坡链每段 ≤45° 能注册，但**从侧面/下面穿过**；半管需要 12 条 > 4 容量；螺旋 32 条 |
| 0.5D–1.0D 台阶（C 的 0.20/0.40/0.60/0.80） | `stepLimit=0.25D` ⇒ 墙（0.20@0.60、0.40@1.10 都是墙） |
| 2.20 球 + 0.6 台面 | 台面 AABB 膨胀 1.1 ⇒ 墙 + 卡死传送（C05 2.20 的循环） |
| 118 个静态实体 | 容量 8+4 = 12，静默丢 106 个 |
| 12 件道具同时近 | 挡路容量 6；超过的静默丢（`SelectNearest` 只取最近 6，但会改变“谁挡路”） |
| 环形跑道牙 16 段 | 容量 8 盒 |

**手动测试脚本（写在沙盒里的提示牌上，不是自动化）**：每档球径（0.30/0.60/1.10/2.20）走一遍 A 的八条坡 → 记录“能滚/能爬/上不去”；B 的每条弯坡走一遍；C 的八个台阶各撞一次；D 的六面墙各推 5 s；E 的 12 件各吸一次；F 跑一圈。**这是“手感”的验证，不是判据**；判据仍在 §6 的小格。

---

## 6. 测试计划

### 6.1 现有格 C00–C20 在 v2 下的期望

记号：**滚** = `h < r` 直接过去；**爬** = `r ≤ h < H_cap` 慢速上去；**墙** = `h ≥ H_cap`。
“变化”列只标**与今天 pass2 不同**的格。

| 格 | 今天（v1） | v2 期望 | 变化 | 判据怎么改 |
|---|---|---|---|---|
| C00 起点 | 全 pass | 全 pass | — | `rest_gap` 口径不变 |
| C01 5° | pass（2.20 差 13 mm，时间线问题） | pass | — | 2.20 时间线保留 8.75 s |
| C02 15° | pass | pass | — | — |
| C03 25° | pass + `no_slip_back` | pass | — | 切向重力仍 0，`slip_back ≤ 0.02` 不变 |
| C04 35° | pass | pass | — | — |
| **C05 45°** | **`climbed` 全档 fail（死锁）** | **`climbed` 全档 pass** | **翻转** | `climbed deck_gap abs ≤ 0.01`（不变，变的是结果）；`ramp_support == 1` 全档（2.20 仍 n/a，见下） |
| **C05 @2.20** | `ramp_support/ramp_entered` 不适用 | 仍不适用（r=1.1 > 坡长 0.6，球不“站在坡面上”） | — | 保留 R1/R2 的 n/a，但**球会滚过去**：新增 `passed_over == 1` |
| **C06 50°** | `ramp_refused` / `no_climb` / 墙 | **没有拒绝**；50° 在爬升带（θ_ride=45），但 0.6 的升程 vs `H_cap = D`：**D=0.30 只爬到 cap 0.30 后滑回**（到不了 0.6 的板顶）；D=0.60 刚好到 cap 0.60（参考模型里勉强上台面）；D=1.10 爬到顶；D=2.20 直接滚过 | **重定义（每档不同）** | 删 `ramp_refusal_*` / `no_climb`；`support_slope_deg ∈ [49,51]`；D=0.30/0.60 判 `max_bottom_rise ≤ 1.02·H_cap` + `slide_back ≥ 0.2`；D=1.10 判 `climbed` pass（窗口 ≥ 3 s）；D=2.20 判 `passed_over` |
| C07 0.02 | pass 全档 | pass 全档 | — | — |
| C08 0.05 | pass 全档 | pass 全档 | — | — |
| **C09 0.075** | 0.30 **fail**（墙），其余 pass | 0.30 **pass**（0.075 < r=0.15） | **翻转 0.30** | `expect` 0.30 改 pass |
| **C10 0.10** | 0.30 **fail**，其余 pass | 0.30 **pass**（0.10 < 0.15） | **翻转 0.30** | 改 pass |
| **C11 0.20** | 0.30/0.60 fail，1.10/2.20 pass | 0.30 **爬**（0.15≤0.2<0.3）、0.60 滚、1.10/2.20 滚 | **翻转 0.30（爬）、0.60（滚）** | 0.30 的 `deck_gap` 需要更长窗口（爬 ~2 s） |
| **C12 0.40** | 0.30/0.60/1.10 fail，2.20 pass | 0.30 墙、0.60 **爬**、1.10/2.20 滚 | **翻转 0.60（爬）、1.10（滚）** | 0.60 窗口加长 |
| **C13 台面** | pass（除 2.20 的 0.95 m 跳变/落地） | pass；2.20 **滚过 0.6 台面**，`seam_no_step` pass | **2.20 修复** | `seam_no_step ≤ 0.05` 保留；`landing_gap`/`landed_on_ground` 在 2.20 由 n/a 恢复为 applicable（球会真正落回地面） |
| C14 0.028 薄片 | pass | pass | — | 不变 |
| C15 薄墙 | pass | pass | — | `blocked_gap = r ± 1 cm` 不变 |
| C16 内角 | pass | pass | — | `xz_std ≤ 2 mm`；推挤 3 次应满足 |
| C17 夹缝 | 0.30 过、其余被挡 | 同 | — | 不变（0.40 净宽 = 2r+0.1 @0.30） |
| C18 | pass | pass | — | 尺寸/吸附不变 |
| **C19 长杆** | pass（`lift` 只抬接地） | pass，但**行为变了**：杆是针尖一等接触，会撑、会碰 | **预期变化** | `attached_delta ≥ 1` 不变；`roll_distance_ratio` 从 ≥0.99 放宽到 ≥0.95（杆朝下时本来就该颠）；`protrusion_ratio` 仍 ≤1.001 |
| **C20 组合** | 0.30 在 0.10 台阶被挡；其余 pass | 0.30 **过**（0.10 < r=0.15） | **翻转 0.30** | `deck_gap` 改 pass |

**新台阶矩阵（v2）**：

| rise | D=0.30（r .15, Hcap .30） | D=0.60（.30/.60） | D=1.10（.55/1.10） | D=2.20（1.10/2.20） |
|---:|---|---|---|---|
| 0.020 | 滚 | 滚 | 滚 | 滚 |
| 0.050 | 滚 | 滚 | 滚 | 滚 |
| 0.075 | 滚 | 滚 | 滚 | 滚 |
| 0.100 | 滚 | 滚 | 滚 | 滚 |
| 0.200 | **爬**（2 s） | 滚 | 滚 | 滚 |
| 0.400 | **墙** | **爬**（3 s） | 滚 | 滚 |
| 0.600 | 墙 | 墙（h = Hcap） | **爬**（4.5 s） | 滚 |
| 0.800 | 墙 | 墙 | **爬**（4.5 s） | 滚 |

> 这张表是**设计值**，需要 Unity 实跑确认；它说明“台阶阈值从 0.25D 变成约 1.0D”，**是本设计最大的玩法变化**（§8 拍板项 5）。

### 6.2 新增格（W1–W6 + R1/R2 + S1 + X1）

所有格沿用测试场协议（§6.1 of `PHYSICS_TEST_FIELD.md`）：切格 → 复位 → 设球径 → settle → 按 `drive` 段推进 → 采样 → 判据。
`drive` 写法：`t=0` 全推 + 向；松手时刻按场景给。**新增格建议只跑 0.30 / 0.60 两档**，1.10/2.20 只在 S1 与 W2 跑（省时间）。

| 格 | 几何 | 起点 / drive | 判据（容差从几何来） |
|---|---|---|---|
| **W1** 90° 低墙 | 墙 0.15 高（0.5D @0.30）、4 长、0.4 厚；墙后是地面 | 起点墙前 1.0 m，`(0,0.15+,…)`，朝向 +z；t=0..3 s 全推，3..4 s 松手 | `max_bottom_rise ≥ 0.14`（= 0.5D − 1 cm）；`passed_over == 1`（球心 z > 墙背 + r）；`support_kind==4` 至少一帧；`fall_frames` 不判 |
| **W2** 90° 高墙 | 墙 3.0 高（≫ Hcap）、4 长、0.4 厚 | 起点墙前 1.0 m；t=0..8 s 全推（**必须覆盖 4.5 s 爬升 + 回滑**） | `max_bottom_rise ≤ 1.02·H_cap + 0.02`（D=0.60：≤ 0.632）；`max_bottom_rise ≥ 0.5·H_cap`（确实爬了）；`final_bottom_rise ≤ 0.1`（滑回）；`reclimb_count == 0`；`recovery == 0` |
| **W3** 70° 坡 | 坡升 0.24（< Hcap 0.30 @0.30），坡顶接 0.24 台面，宽 3 | 起点坡脚前 0.8 m；t=0..4 s 全推 | `deck_gap abs ≤ 0.01`；`climb_time ≤ 2.0 s`（0.24 m / 0.369 m/s + 余量）；`support_kind==4` ≥ 1 帧；`no_slip_back` 不适用（爬升带允许回滑） |
| **W4** 墙 + 凸出长杆 | W2 的墙 + 球先吸一根 1.5×0.06×0.06 杆（`ptf_rod_1500`），杆朝墙 | 同 W2 | `h_max(W4) − h_max(W2) = λ·L_eff ± 0.02`（D=0.60：0.105 ± 0.02）；`h_max(W4) ≤ 1.2·H_cap` |
| **W5** dash 上台阶 | 台阶高 = 0.8·H_cap（D=0.30：0.24；D=0.60：0.48），宽 4 | 两次子运行：① 不 dash；② 起跑同时 dash | `t_to_deck(dash) ≤ 0.7·t_to_deck(nodash)`；两次都 `deck_gap abs ≤ 0.01` |
| **W6** 掉出世界 | 平台边缘外无地面（把地面平面移到 −20，或平台外 3 m 无支撑） | 起点平台边内 1 m；t=0..2 s 全推冲出去 | 先 `is_falling == 1`，再 `recovery_delta == 1`（硬恢复）且位置回到 `lastStableCenter`（不是旧起点）；`recovery_state == 2`；**不允许** `recovery_state == 3` |
| **R1** 坡从侧面 | C05 的 45° 板，但球从板宽外 +x 撞；板用 **0.3 m 厚**的坡体（≥ r，保证撞的是侧面而不是底面） | 起点 (x = 2 + r + 0.5, y = r, z = 7.9)，朝向 −x；t=0..2 s 全推 | 全程 `min_distance_to_slab ≥ r − 2 mm`；球心 x ≥ 2 − 2 mm（没进板）；`penetration_max ≤ 2 mm` |
| **R2** 坡从下面 | C05 的板抬高 1.0 m（底面 y≈1.27），球在下面 | 起点 (0, 0.5, 7.9)，给一次向上速度（`TeleportTo` + 载具？或用 `drive` 冲上斜坡底——**需要驱动接口支持竖直**；若拿不到，就用“从高处落到板底面”的等价场景） | 全程 `min_distance ≥ r − 2 mm`；球心**不出现**在板顶面之上；`passed_through == 0` |
| **S1** 2.20 球 + 0.6 台面 | C13 的台面（0.6 高）+ 北缘落差 | C13 的 `entry=ramp` 时间线，D=2.20 | `climbed` pass（滚过 0.6）；`seam_no_step ≤ 0.05`；`landing_gap abs ≤ 0.01`；`frame_drop ≤ 0.5`（真下落） |
| **X1** 弯坡 | B1 上弯 R4/30°/8 段/宽4，末端接平台 | 起点弧前 1 m，t=0..3 s 全推 | `normal_jump_max ≤ Δ/2 + 3° = 6.75°`；`center_y_jitter ≤ s + 2 mm = 8.4 mm`；`fall_frames == 0`；`rise = R(1−cos30) = 0.536 ± 0.005`；`segment_index` 单调 |

**形状层的三格（W7/W8/P1）定义在 §4A.7**，与本表同一协议：W7「带长杆贴墙」、W8「长杆撑起过台阶」、P1「拾取 > 滚动但不阻挡」。

**时间线纪律**：W2 的 8 s、C11/C12 的爬升档、W5 的两次子运行会让一次完整 pass 变长；建议**把 W1–W6 + R1/R2 + S1 + X1 放在单独的一张“v2 验收表”里**，只跑 0.30/0.60 两档，全表 ≈ 8 分钟。

### 6.3 容差怎么从几何算（不是拍的）

| 判据 | 容差 | 推导 |
|---|---|---|
| `deck_gap` | 0.01 m | 台面高 0.6 的 1.7%；v1 已用 0.01 |
| `max_bottom_rise ≤ 1.02 H_cap + 0.02` | 2% + 2 cm | `cap_margin=0.02` 是到顶判定带宽；1.02 给 fade 的浮点 |
| `h_max(W4) − h_max(W2) = λL_eff ± 0.02` | 0.02 m | `L_eff` 由端点云算出，端点精度 = AABB 角点，无拟合 |
| `t_to_deck(dash) ≤ 0.7·t(nodash)` | 0.7 = 1/1.4 | dash 的 `rate` 上限 1.5、速度 1.6；取 1.4 的保守比 |
| 弯坡 `normal_jump ≤ Δ/2 + 3°` | 3° | `asin(s/r) = asin(0.0064/0.15) = 2.4°`，取 3 |
| 弯坡 `y_jitter ≤ s + 2 mm` | 2 mm | 接触 skin 5 mm、slop 0.5 mm，取 2 mm |
| `min_distance ≥ r − 2 mm` | 2 mm | 推挤收敛到 `slop=0.5 mm`；子步离散再给 1.5 mm |
| `climb_time ≤ 2.0 s`（W3） | 2.0 | 0.24 m / 0.369 m/s = 0.65 s；给 3× 余量（起速/进接触） |
| `blocked_gap = r ± 0.01` | 0.01 | v1 已有 |
| W7 有杆/无杆的核心间距差 | ±0.02 | 端点云是 AABB 角点（无拟合）；0.02 = cap 0.105 的 19% |
| W8 台阶高 0.65 | 边界 ±0.05 | `H_cap` 无杆 0.60 / 有杆 0.705，取中间 ±0.05 避开边界抖动 |
| P1 可吸件距离 1.2·r_phys | — | 盒轴 1.0 / 面对角 1.41 之间：只有拾取能到、滚动到不了 |

### 6.4 需要的新 getter / 日志（给测试场）

| 读数 | 签名 | 用途 |
|---|---|---|
| `GetContactCount()` | int | 本步接触数（≤8） |
| `GetContactNormal(int)` / `GetContactDepth(int)` / `GetContactClass(int)` / `GetContactSurfaceId(int)` | | 逐接触诊断（W1/W2 的 `support_kind==4`） |
| `GetSolverResidualMax()` | float | 穿透残留；> 0.02 m 判失败 |
| `WasContactIncomplete()` | bool | 窗口溢出导致“可能漏接触”（§5.4） |
| `GetNearObbCount()` / `GetNearObbDropped()` / `GetNearRebuildCount()` | int | 窗口成本与溢出 |
| `GetClimbHeightWorldM()` / `IsClimbing()` / `GetClimbState()` | float/bool/int | 爬升诊断（0 无 / 1 爬 / 2 回滑 / 3 锁定） |
| `GetSupportSlopeDegrees()` | float | 当前支撑/爬升接触的 θ（C06 的 `support_slope_deg`） |
| `GetNeedleContactCount()` / `GetContactShape(int)` | int | 针尖接触条数 / 0 核心、1+i 针尖（W7/W8） |
| `GetNeedleProtrusionCapWorldM()` / `GetNeedleProtrusionMaxWorldM()` | float | 物理 cap 与当前实际最大突出（诊断“看起来撑、物理没撑”） |
| `GetRollReachWorldM()` / `GetPickupReachWorldM()` | float | 沿当前朝向的滚动/拾取 reach（P1、调试面板四件套） |
| `GetVisualCoreRadiusWorldM()` / `GetPhysicalRadiusWorldM()` | float | 形状层四件套 |
| `GetRecoveryCount()` / `GetRecoveryState()` / `GetLastRecoveryReason()` | int | §2.8 |
| `GetBroadphaseOverflowCount()` | int | 与 `GetOverflowCount()` 分开 |
| `GetLastStepMicros()` / `GetLastActiveSurfaceCount()` / `GetFixedStepCount()` | | P4 已有的请求，v2 继续要 |

日志（节流）：`[ContactV2] step=… contacts=… residual=… near=… dropped=… climb=… h=…`（每 0.5 s 或状态变化时一条）；`[ContactV2] window overflow …`（每 1 s 最多一条）。

### 6.5 小格与沙盒的分工

| | 9 个小格（现有测试场） | 地图 0 沙盒（新场景） |
|---|---|---|
| 目的 | **判据**：每个机制的数值对错、回归 | **手感**：手动滚、尺寸耦合、弯坡、爬墙的观感 |
| 驱动 | 脚本时间线 + 四档球径 | 人 |
| 判据 | 每条带 `expect` | 无判据；只有“能不能过/手感如何”的记录表 |
| 容量 | 一格一注册（8+4 够） | 118 条一次注册（靠 §5 的窗） |
| 何时建 | 现在 | S5（v2 flag 打开、矩阵通过之后） |

---

## 7. 分阶段迁移（每步独立可发、可回滚）

### 7.1 阶段表

| 阶段 | 内容 | 文件 | 风险 | 验收 | 回滚 |
|---|---|---|---|---|---|
| **S1（已完成）** | Python 参考模型 + 单测 | `Docs/Design/BallMotor/contact_v2/contact_v2.py`、`.../shapes.py`、`Docs/tests/ball_contact_v2/test_contact_v2.py`、`.../test_shapes.py` | 低（不改运行期） | `python3 -m unittest discover -s Docs/tests` 全过（本轮 534 例，含新增 32 例） | 删文件 |
| **S2（代码已写，未编译）** | C# 纯数学库 + EditMode | 实际落点（2026-11-15）：`Runtime/BallContact/` 的 `ContactTuning.cs`、`ContactCodes.cs`、`ObbContactMath.cs`、`ObbBakeMath.cs`、`ContactClassificationMath.cs`、`ClimbMath.cs`、`BallShapeMath.cs`、`ContactWindowMath.cs`、`ContactSolveMath.cs`、`CurveRampMath.cs`；`Tests/EditMode/BallContact/` 的 8 个测试类 | 中：数值与模型不一致 | 同一组 fixture 与 Python **逐位/1e-6 一致**；面/棱/角/内部、窗口、弯坡段几何。**代码已写但未编译、EditMode 未跑**（写它的机器没有 Unity） | 删文件（还没人调用） |
| **S3（代码已写，未编译）** | 电机集成，**flag 默认 false** | `KatamariBallMotor.cs`：`contactModelV2Enabled`、`StepContactV2`/`FillNearWindow`/`WriteContactSupport`/`ApplyGrowthPushOut`/恢复状态机、`AddObb`/`ClearObbs`、新 getter；`StepPositionV1` 保留 v1 原语句；`RefreshStepMask`/`TrySweepSphere` 在 v2 分支跳过；`BallShapeMath.TryNeedleSegmentContact`（针尖段扫掠）+ `NeedleSegmentTests` + `ContactModelV2Tests`；接口与偏差见 §7.5 | **高**（最大的改动） | flag=false 时与今天**逐位相同**（同一场景同一 run 的 `[ptf]` 行对比；`StepPositionV1` 是原中段整体搬移，diff 已核对）；flag=true 时 C 矩阵 + W 格通过；`recovery == 0`。**本轮没有任何 Unity 编译/测试** | **flag 置 false**（v1 路径原样保留）；必要时 revert 一个 commit |
| **S4** | 数据/导入器 | `LevelArenaHost.cs`（8 角点 OBB 烘、`AddObb`、ramp→slab、`ramps` 数组语义）、`PlacementImporter.cs`（`curve_ramp` 展开）、`placement_core.py`（45° error→warning + 弯坡校验）、`SupportSurfaceMath.cs`（降级为诊断） | 中：旧场景重烘 | 9 个小格的烘焙 OBB 表与旧 AABB **数值相同**（轴对齐）；`levelbuild check` 全绿；弯坡段数/矢高合规 | `LevelArenaHost.useObbBake=false`（回 AABB，v1/v2 都能跑）；弯坡条目 `expanded:false` 不展开 |
| **S4a（已完成 b662b64）** | 关卡数据：`curve_ramp` 角色、45° 改为 warning、静态 OBB 总数预算 | `Tools/levelbuild/placement_core.py`、`Editor/LevelBuild/PlacementImporter.cs`（弧展开成 `<id>_00…` slab 子物体并逐段进 `rampBoxes`） | 中 | `python3 -m unittest discover -s Docs/tests` 全绿；`levelbuild check` 的 256 条与弦高预算；**Unity 未编译** | 弯坡条目 `expanded:false` 不展开 |
| **S4b（代码已写，未编译）** | arena 与道具场接 v2：8 角点真 OBB 登记、§7.4 双烘自检、容量溢出原因码、道具 OBB 接通电机 `SetPropObbs` | `Runtime/Story/LevelArenaHost.cs`、`Runtime/Pickup/PickupFieldHost.cs`、新增 `Runtime/BallContact/PropObbBakeMath.cs`、`Tests/EditMode/Story/LevelArenaHostObbTests.cs`、`Tests/EditMode/BallLayers/PropObbBakeTests.cs`、`Tests/EditMode/BallLayers/PickupFieldPropObbTests.cs`、本文 §7.6 | 中 | 三种模式的静态 OBB 条数 = box + ramp/弯坡段；轴对齐双烘一致；剪切回退带原因码；容量满可复现；道具块 `GetPropObbsCount()` 随扫描变化；**没有 Unity 编译/EditMode** | `LevelArenaHost.useObbBake=false`（回 AABB，AABB 与 v1/v2 都能跑）；道具侧 flag 关即回 v1（宿主会清空道具块） |
| **S5** | 新测试格 + 沙盒 + 证据 | `Labs/PhysicsTestField`（W/R/S/X 格）、`Labs/PhysicsSandbox`（地图 0）、`physics_test_field.json`、`Docs/Evidence/...` | 中：数据/场景工作量大 | v2 验收表全绿；沙盒能手动滚 | 删格；沙盒场景独立，不影响主场景 |

**依赖**：S1 → S2 → S3；S4 可与 S3 并行（S3 的 flag=true 需要 S4 的 OBB 烘，或先用 `AddBox` 的轴对齐 OBB 做 Unit 测试）；S5 依赖 S3+S4。
**“v1 仍然可用”的硬约束**：S3 的 flag=false 分支必须**一行不改**地保留 `RefreshStepMask → TrySweepSphere → TryResolveSupport → GroundContactMath` 的原顺序；任何“顺手重构”都会让回滚失效。

### 7.2 每步的验收命令

```bash
python3 -m unittest discover -s Docs/tests          # S1/S2 的模型与全部既有 Python 测试
python3 Docs/tools/validate_plan.py                  # 计划数据
# Unity 侧（由 Unity 操作者执行，本文作者不是操作者）
#   refresh_unity → run_tests（EditMode）→ 测试场 Play Mode 跑 v2 验收表
```

**S2 的数值对齐**：把 Python 模型的一组固定输入（面/棱/角/内部/45° 接缝/90° 墙/70° 坡/200 条窗口/12 段弯坡）写成 JSON fixture，C# EditMode 与 Python 各自跑，断言 `|Δ| ≤ 1e-6`。**这是“两份实现不许漂”的唯一机制**（与仓库既有 `sweep.py` ↔ `SweepMath.cs` 的做法一致）。

### 7.3 v2 的 9 阶段顺序（S3 落地时的目标）

| 阶段 | 层 | v2 变化 |
|---|---|---|
| 1 | `ControlIntentMath` | 不变；新增 dash 动作 |
| 2 | `BallMotionMath` | 不变（`v_drive` 目标速度要额外输出） |
| ~~2.5~~ | ~~`RefreshStepMask`~~ | **删除**（v2 分支） |
| 3 | **`ContactSolveMath.StepContacts`** | 新：子步、窗口、接触、推挤、分类、速度响应 |
| 3.5 | `ClimbMath`（同文件） | 新：爬升状态与速度 |
| 4 | 支撑写回 + 代理 | 由接触结果给 `supportNormal/Height`；代理只抬高度 |
| 4.5 | **长大推挤** | 新（§4.2） |
| 5–7 | 吸收/账本/尺寸 | 不变 |
| 8 | `VisualRollMath` | 法线 = 支撑/接触法线 |
| 9 | `FollowRigMath` | 不变 |

### 7.4 S4 的回归检查点（必须做）

轴对齐的旧 9 格，`8 角点 OBB` 与 `bounds AABB` 必须给出**完全相同**的 min/max（浮点差 ≤ 1e-6）。做法：`LevelArenaHost` 在 `Setup` 时把两种烘法都算一遍，不一致就 `Debug.LogError` + 用 AABB；这个“双烘自检”在 S4 验收后可以删。

### 7.5 S3 实现与接口（2026-11-15）

**状态：`代码已写，未编译`。** 本轮改的是 `Runtime/BallMotor/KatamariBallMotor.cs`（v2 区域）、`Runtime/BallContact/BallShapeMath.cs`（补 §4A.3 的针尖**段**扫掠）、`Tests/EditMode/BallContact/NeedleSegmentTests.cs`、`Tests/EditMode/BallMotor/ContactModelV2Tests.cs`、`Docs/Design/BallMotor/contact_v2/shapes.py` + `Docs/tests/ball_contact_v2/test_segment_sweep.py`（参考模型与 Python 金值）。**没有**碰 `LevelArenaHost`、`PlacementImporter`、`placement_core`、测试场场景/数据。
（S2 的 10 个文件已由操作者在 Unity 里编译，EditMode 658 例全绿，仅 SDK URL 检查因离线红色；这是本节的运行前提，不是本轮跑出来的。）

#### 7.5.1 开关、回滚与 v1 路径的改动清单

`[SerializeField] private bool contactModelV2Enabled = false;`（另有 `SetContactModelV2Enabled(bool)` / `IsContactModelV2()` 供实验与 EditMode 用）。

`StepBall(dt)` 现在只做一件事：阶段 1–2 与 5–9 共享，**位置权威**在两者之间二选一：

```
1 StepInput -> 2 BallMotionMath -> [ flag ? StepContactV2 : StepPositionV1 ] -> 5..9 (含 7.5 长大推挤)
```

`StepPositionV1` 是**原 StepBall 的中段整体搬移**（语句、注释、常量逐字不变，已用 diff 核对），因此 flag=false 时执行顺序仍是 `RefreshStepMask → TrySweepSphere → TryResolveSupport → GroundContactMath`。

进入 v1 路径的改动**只有这 5 处**（其余全部是新增方法/字段）：

| # | 位置 | 改动 |
|---|---|---|
| 1 | `StepBall` 尾部（阶段 7.5） | 新增 `if (contactModelV2Enabled) ApplyGrowthPushOut(...)`，flag 关时不执行 |
| 2 | `StepPositionV1` 开头/中段 | 三个值从局部变量变成 `out` 参数：`groundedCenter`(`= sweptCenter`)、`followCenter`(`= sweptCenter`)、`supportNormal`(`= groundPlaneNormal`)；方法开头先初始化三者（CS0165） |
| 3 | `AddBox` / `AddRamp` 末尾 | 各加一次 `RegisterBoxObb` / `RegisterRampSlab` 调用；v1 没有任何读者读这两张表 |
| 4 | `ClearObstacles` / `TeleportTo` | `ClearObstacles` 加 `ClearObbs()`；`TeleportTo` 加 v2 状态复位（垂直速度、爬升、稳定点、针尖上一帧、roundStartCenter）；v1 不读这些字段 |
| 5 | `GetSupportKind` 开头 | 加 `if (contactModelV2Enabled) { 针尖支撑→5; 爬升接触→4 }`；flag 关时不进 |
| 6 | `Start` 末尾 | `AllocateContactV2Buffers()`（约 25 KB，**flag 关了也分配**，这样运行期翻转 flag 仍能看到场景已登记的几何） |

回滚 = flag 置 false；不需要 revert commit。

#### 7.5.2 新增公开 API（S4 按这些签名写）

几何登记（`AddBox` / `AddRamp` / `AddPlane` / `SetGroundPlane` 签名与语义不变）：

```csharp
bool AddObb(Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
            Vector3 halfExtents, int surfaceId, int role);   // 容量 256；坏盒/超容拒绝并计数
void ClearObbs();
int  GetStaticObbCount();          int  GetStaticObbCapacity();      // 256
int  GetStaticObbOverflowCount();  int  GetStaticObbRefusalCount();
string GetLastStaticObbRefusalReason();

// 道具块（S4b 的宿主每次扫描重登记；容量 12，固定数组，超容拒绝+计数+一条日志）
bool SetPropObbs(Vector3[] centers, Vector3[] axisX, Vector3[] axisY, Vector3[] axisZ,
                 Vector3[] halfExtents, int[] surfaceIds, int[] roles, int count);
void ClearPropObbs();
int  GetPropObbsCount();          int  GetPropObbsCapacity();        // 12
int  GetPropObbsOverflowCount();  int  GetPropObbsRefusalCount();
string GetPropObbsRefusalReason();
```

接触 / 窗口 / 求解（§6.4）：

```csharp
int     GetContactCount();                 Vector3 GetContactNormal(int i);
Vector3 GetContactPoint(int i);            float   GetContactDepth(int i);
int     GetContactClass(int i);            int     GetContactSurfaceId(int i);
int     GetContactRole(int i);
int     GetContactShape(int i);            int     GetNeedleContactCount();
int     GetDominantContactClass();         int     GetDominantContactSurfaceId();
float   GetSolverResidualMax();            bool    WasContactIncomplete();
int     GetNearObbCount();                 int     GetNearObbDropped();
int     GetNearObbDroppedTotal();          int     GetNearRebuildCount();
int     GetWindowOverflowCount();          int     GetWindowSaturatedCount();
int     GetSubstepCount();                 float   GetLastStepMicros();
```

爬升 / 恢复 / 长大 / 形状：

```csharp
bool  IsClimbing();          int   GetClimbState();        // 0 无 / 1 爬 / 2 回滑冷却 / 3 锁定
float GetClimbHeightWorldM(); float GetClimbSpeedMps();   bool IsClimbLocked();
int   GetRecoveryCount();    int   GetRecoveryState();     // 0/1 软/2 硬/3 保持
int   GetLastRecoveryReason();                            // 1 软 2 硬 3 初始深嵌
bool  IsSizeApplyDeferred(); int   GetSizeApplyDeferredCount();
int   GetContactSolveFailureCount();   int GetContactRejectedStepCount();
float GetContactRadiusUsedWorldM();    Vector3 GetContactDriveDirection();
float GetSupportSlopeDegrees();
float GetNeedleProtrusionCapWorldM();  float GetNeedleProtrusionMaxWorldM();
float GetPhysicalRadiusWorldM();       float GetVisualCoreRadiusWorldM();
float GetRollReachWorldM();            float GetPickupReachWorldM();
bool  IsContactModelV2();              void  SetContactModelV2Enabled(bool enabled);
```

#### 7.5.3 每个 v2 阶段落在哪个方法

| 设计阶段 | 代码 | 说明 |
|---|---|---|
| 2.5 台阶掩码 | **无**（v2 分支不调用 `RefreshStepMask`） | 掩码字段/方法仍在，v1 用 |
| 3 子步 + 窗口 + 接触 + 推挤 + 分类 + 速度响应 | `StepContactV2` → `FillNearWindow` + `ContactSolveMath.TryStep`（S2，内含 `MaxSubSteps`/`TryCollectContacts`/`TryPushOut`/`SelectSupportContact`/`SelectClimbContact`/`ClimbMath`） | 一次 Udon 调用做完全部障碍循环。**窗口把静态表与道具块混排**：两块放在同一组数组里（静态前缀 `[0, static)` + 道具块 `[static, static+prop)`），`FillNearWindow` 用 `static + prop` 作为条目数调**一次** `TrySelectWindow`，所以 R_in/R_out、24 上限、距离排序、滞回、溢出判据全部是同一份；求解器看到的是同一张 near 表，没有任何“道具分支” |
| 3.5 爬升 | `ContactSolveMath.TryStep` 内的 TryStep 状态机；状态字段在电机（`climbActive/climbAnchorY/climbSurfaceId/climbCooldown/climbLocked/climbAlongSpeed`），`WriteContactSupport` 写回口径 | `GetClimbState` 的 2 = 冷却窗口 |
| 4 支撑写回 | `WriteContactSupport`（v1 的 `supportSurface*` 字段全部继续写） | 支撑高度用 v1 同一公式 `C.y − dot(n,C−p)/n.y`，针尖支撑自然落到 §2.5 的针尖公式 |
| 4.5 长大推挤 | `ApplyGrowthPushOut`（在共享尾部的 7.5，flag 关时不跑） | 独立接触 scratch，不覆盖当步 `GetContact*` 读数；≤2 次 × ≤5 cm；失败则**整段不应用** + `sizeApplyDeferred` |
| 针尖（§4A.3） | `PrepareNeedleShape` / `EstimateNeedleProtrusion` / `CollectNeedleContacts` / `ResolveNeedleContacts` / `MergeNeedleContacts` / `ApplyNeedleVelocityResponse` + `BallShapeMath.TryNeedleSegmentContact` | 段扫掠在 S2 库里（纯数学 + EditMode + Python 金值） |
| 2.8 卡死/恢复 | `UpdateContactRecovery` / `TryRecoverToStablePoint` / `EnterRecoveryHold` | 软 1.5 s 清速度；硬 3 s 回**最后稳定点**（没有则本回合起点）；10 s 内 3 次 → `recoveryState=3` + 一条 `[BallMotor] recovery hold …`，不再传送 |
| §5.4 窗口溢出 | `FillNearWindow` + `LogWindowOverflowOnce` | 保留最近 24 条，`contactIncomplete` 锁存 + 每 1 s 最多一条 `[ContactV2] window overflow …` |

**AddBox → OBB**：`center=(min+max)/2`、`halfExtents=(max−min)/2`、轴 = 世界轴、`surfaceId = 1 + boxIndex`（v1 支撑表 id）、`role = 0`。**AddRamp → slab OBB**：`axisY = rampNormal`、`axisX = right 在坡面内的投影归一化`、`axisZ = cross(axisY, axisX)`、`halfExtents = (halfWidth, thickness/2, halfRun / normal.y)`、`center = walkingCenter − normal·thickness/2`、`surfaceId = 10 + rampIndex`、`role = 1`；`thickness = rampSlabThicknessM`（序列化，默认 0.04）。坡的**走向面**就是原来的烘焙平面，所以旧场景开 flag 后球不会陷半个板厚。

#### 7.5.4 getter 在 v2 的口径（旧读者不受影响）

| getter | v1 | v2 |
|---|---|---|
| `GetSupportSurfaceId` | 0 / 1+i / 500+i / 100+i / 2000+ | 同上（地面 0、盒 1+i（1..64）、坡 500+i、道具 100+i、地形 2000+；`AddObb` 用调用方给的 id） |
| `GetSupportKind` | 0/1/2/3/−1 | 同上 **+ 4 = 爬升接触**、**5 = 只有针尖撑着** |
| `GetSupportSurfaceHeightWorldM` | 选中面在球心 XZ 的高度 | 同一公式；针尖支撑时给出针尖处的切平面高度（§2.5）；**爬升时没有地板可报**，返回球的底部（`center.y − r`），使 `support_gap` 读 0（首跑把墙接触点的 y 当高度，读成 `−r`，见 §7.7） |
| `GetSupportNormalWorld` | 选中面法线 | 支撑法线；爬升时为爬升接触法线；无支撑时保留上一次 |
| `GetStepHeightLimitWorldM` | 0.25·D，参与物理 | 仍返回 0.25·D，**只作诊断**（每步刷新） |
| `IsFalling` | 丢失支撑后进入重力 | = 无支撑接触且不在爬升 |
| `GetSupportSurfaceCount` | 带内面数 | 当步接触条数 |
| `GetContactSurfaceId` / `GetContactRole` | — | 同一条接触的两种标签，用来区分静态与道具（**没有**道具专用分支）：地面 0 / 静态盒 1..64 / 坡 500..503 / 道具 100..111 / 边界平面 1000+i / 地形 2000+；role 0 盒、1 坡、2 支撑道具、3 挡路。`GetDominantContactSurfaceId` 返回原样 id，读者按区间判类 |
| `GetNearObbCount` | — | 静态 + 道具合并后的近集条数（≤24）；`GetNearObbDropped` 是**两块共享预算**下丢掉的条数 |
| `GetCollisionRadiusWorldM` | 扫掠半径 | = `r_phys`，接触半径 |
| `GetSemanticDiameterWorldM` | 视觉/相机/stepLimit/吸收门 | 不变（§4A.5 的三形状拆分不在 S3） |
| `GetGroundProxyLiftWorldM` / `GetProxyProtrusionCapWorldM` | 径向代理抬升 | v2 不再抬升：前者仍返回最后值（诊断），后者仍按 `min(0.35r,0.30)` 返回（= 针尖 cap） |
| `GetStepMaskRebuildCount` | 掩码重建次数 | v2 不再重建（值冻结） |
| `IsTerrainSupportEnabled` / `SetTerrainSupportEnabled` | P1 支撑/台阶/下落开关 | v2 **不读**这个开关（没有掩码、没有高度场、没有 `fallSpeedMps` 下落状态）；flag 开时它无效 |

#### 7.5.5 与设计的偏差（已实现 vs 推迟）

**已实现**：子步上限/窗口/滞回/溢出判据、面/棱/角/内部最近点、三分类、RIDE 不去速、CLIMB 去水平法向、NONE 去三维法向、15° 余量（在 S2 的 `SelectClimbContact`/TryStep 内）、支撑=最高 RIDE、爬升通道（H_cap/速度/锁定/回滑/冷却）、软/硬/保持三段恢复、长大推挤、针尖段扫掠 + 合并 + 推挤 + 去速 + 针尖支撑(5)、针尖对 `H_cap` 的 `L_eff`。

**推迟（有意，写在这里避免误读为已做）**：

1. **针尖接触不在子步循环里**：S2 的 `TryStep` 自己跑子步并在内部收集接触，电机无法把针尖塞进去，所以针尖是**每步一次后置 pass**（2 次推出迭代 + 一次重新收集）。不穿透由**段扫掠**保证，不由子步数保证；代价是顺序确定性差一点。
2. **针尖不启动爬升通道、不应用 grip 折扣**：`TryStep` 没有 gate 缩放参数，`RequiredSpeedWithGrip` 需要改 S2 的 TryStep 签名（会破坏“两份实现不漂”）。W7/W8 归 S5；本轮的针尖只做推挤、去速、支撑、`L_eff`。
3. **`L_eff` 用推入方向代替墙法线**：求解前不知道墙法线，用运动层的推力方向做投影（`dot(worldDir, drive)`）。
4. **长大推挤是“原子”的**：失败时整段不应用并把 `sizeApplyDeferred` 置位（下一步重试）。设计写的“本步回退到 r_old”被实现成“这一步的推挤不生效”，因为进入本步时接触半径本来就是 r_old（尺寸更新发生在求解之后）。
5. **`GetLastStepMicros` 是粗测**：Udon 没有高精度时钟（无 `Stopwatch`），`Time.time` 每帧才变；一帧跑多个固定步时只有第一个非零，读数只能当“整帧上界”。要真测请用 profiler。
6. **`contactIncomplete` 是锁存 + 一条日志，不冻结球**：§5.4 的原文就是“仍用 24 条，标记不完整，判据失败”。
7. **没有 dash**（§3.8）、**没有 `GetFixedStepCount`**（§6.4 列的是 P4 请求，S3 未加）。
8. **AddBox 容量 64**（2026-11-16 由 8 提到 64）：现有场景（≤64 盒 + ≤4 坡）可以直接开 flag；地图 0 的 118 条要等 S4 把宿主改成 `AddObb`。`AddRamp` 的 45° 作者门仍在（S4 改 warning），更陡的坡本轮只能用 `AddObb` 直接登记。
9. **挡路道具 / 支撑道具：缺口已填（S4b 宿主 + 本轮 `SetPropObbs`）**。宿主在它的 20 Hz 扫描里把近邻道具作为真 OBB 交给 `SetPropObbs`（12 槽，surface id 100+i，role 2/3），它们与静态盒走**同一份**接触/分类/推挤，窗口里按距离与静态混排。**v1 的 `SetBlockingBoxes` / `SetSupportProps` 没有自动转换**（它们仍只喂 v1 的 sweep/支撑表，flag 关时行为逐字不变）；`TeleportTo`/`ClearObstacles` 清道具块，下一次宿主扫描重登记，与 v1 清挡路盒的策略一致。§7.6.5 记的宿主侧实现已按这个签名接通（S4b 后续提交）。
10. **三形状拆分（§4A.5）未落到尺寸层**：`r_phys = collisionRadiusWorldM`，`collisionToCoreRadiusRatio` / `semanticDiameterPerCoreDiameter` 语义未变；只提供 `GetPhysicalRadiusWorldM` / `GetVisualCoreRadiusWorldM` / `GetRollReachWorldM` / `GetPickupReachWorldM` 四个读数。
    **pickup 已按 §4A.4 接成 trigger**：`EvaluatePendingCandidates` 在 flag 开时把“球心→该候选”方向上的 `pickupReach(û)` 作为 `PickupAbsorbMath` 的球半径参数（reach = pickupReach + contactR + propBoundingRadius），**只影响“能不能粘”**，不进接触、不挡、不支撑；flag 关时该参数逐字仍是 `collisionRadiusWorldM`。`PickupAbsorbMath` 本身一行未改。隔墙吸（§4A.4 的选项①）行为不变。
11. **针尖可能漏掉亚毫米的擦角**（`BallShapeMath.TryNeedleSegmentContact` 的 5 采样 + 12 次二分）：面/棱命中精确，角上极浅的擦过可能漏。
12. **爬升门的“持续条件”在电机里，而不是在 `TryStep`/参考模型里**（首跑修复）：§3.2 的 `v_gate ≥ v_req(θ)` 被当作**持续**条件，`RefreshClimbGate` 在每步求解前检查；`p < p_min`、到达上限、以及“门已关闭”三者任一成立就离开爬升通道并给 0.35 s 冷却。`ContactSolveMath.TryStep` 与 `contact_v2.py` 只实现“到达上限 / `p < p_min`”两种退出，参考模型的夹具全是全推时间线，所以这一条留在电机层，`step()` 的两份实现仍然一致。
13. **v2 表在需要时惰性分配**：`AddObb`/`SetPropObbs` 会先 `EnsureContactV2Buffers()`，`Start` 只在表不存在时分配。Unity 跨 GameObject 的 `Start` 顺序是任意的，宿主可能先登记；首跑读到 `GetStaticObbCount == 0` 而 `[arena]` 报 `obbs=1/1` 就是这一类时序窗口（登记被拒/被 Start 覆盖），现在两条路径都不会丢几何。

#### 7.5.6 本轮跑了什么 / 没跑什么

| 跑了 | 结果 |
|---|---|
| `python3 -m unittest discover -s Docs/tests` | 全绿（新增 `ball_contact_v2/test_segment_sweep.py` 12 例） |
| `python3 Docs/tools/validate_plan.py` | PASS |
| **没跑** | Unity 编译、U# 全量编译、EditMode、Play Mode、profiler、任何手感/帧时间；**S3 的 C# 一行都没编译过** |

操作者要跑的两组 EditMode：`VRCKatamari.Tests.NeedleSegmentTests`（纯数学，12 例）与 `VRCKatamari.Tests.ContactModelV2Tests`（电机级，15 例，含道具 OBB 的挡球/混排/超容/清理/flag 关无效五例）；后者是设计期望值，若红，先看 `GetContactCount/GetSupportKind/GetRecoveryState` 三组读数再判是代码还是期望。

### 7.6 S4b 实现与接口（2026-11-15）

**状态：`代码已写，未编译`。** 本轮只碰了 S4b 的五处：`LevelArenaHost.cs`、`PickupFieldHost.cs`（只到「挡路/支撑道具」这一段）、新增的 `Runtime/BallContact/PropObbBakeMath.cs`、两个新 EditMode 测试类，以及 `LEVEL_AUTHORING.md` §7.6 的 arena 接线段。**没有**碰 `KatamariBallMotor.cs`、`Editor/LevelBuild/*`、`Tools/levelbuild/*`、测试场场景/数据。

#### 7.6.1 一条几何只登记一次：三种模式

`LevelArenaHost` 新增 `[SerializeField] private bool useObbBake = true;`，但**走哪条路**由电机的 flag 决定，按模式二选一（不是两条都跑）：

| 模式 | arena 的登记 | 电机 v2 静态表里是谁 | 静态 OBB 条数 |
|---|---|---|---|
| v2（`IsContactModelV2()`）且 `useObbBake` | 每个 box/ramp/弯坡段：8 世界角点 → `ObbBakeMath.TryBakeFromCorners` → `AddObb`（box `1+i`、slab `500+i`，role 0/1） | arena 登记的真 OBB | box + ramp/段 |
| v2 且 `!useObbBake` | 与 v1 相同：`AddBox`/`AddRamp`（§7.1 的回滚路径） | 电机镜像的轴对齐 OBB（S3 §7.5.3） | box + ramp/段 |
| v1 | 与 v1 相同：`AddBox`/`AddRamp` | 电机镜像（v1 不读） | box + ramp/段 |

**为什么不两种都调**：`AddBox`/`AddRamp` 自己就会 `RegisterBoxObb`/`RegisterRampSlab`（§7.5.3 第 3 条），所以在 v2 里再调一次 `AddObb` 会把同一条几何登记两遍（球会看到两层壳）。上面的三条路每一条都只写一次，EditMode 直接断言 `GetStaticObbCount() == 盒子数 + 坡/段数`。`useObbBake` 单独为 true 但电机仍是 v1 时**不**走 `AddObb`（那会让 v1 的扫掠表空掉、球掉下去），报告行写 `obbBake=mirror`。

#### 7.6.2 8 角点、§7.4 双烘自检、剪切

- **角点怎么算**：`position + M·(localCorner)`，`M` 由**逐层 `localRotation × localScale` 累乘**得到（`AccumulateWorldBasis`），不是 `rotation`/`lossyScale`。原因就是 §1.4 的那条：`lossyScale` **表达不了剪切**，用它等于把「父级旋转 + 非均匀缩放」悄悄吞掉，然后用一个作者没画过的盒子登记。累乘之后，剪切会自己冒出来——三个边向量不再正交，`TryBakeFromCorners` 按 §1.4 拒绝。也**没有**用 `Transform.TransformPoint`：本项目 Runtime 里没有任何一处用它，Udon 的绑定没验证过；累乘只用 `localRotation`/`localScale`/`parent`。
- **§7.4 双烘自检**：只对**轴对齐**的盒子做（旋转盒的 bounds AABB 本来就比 OBB 大，比了会把每条斜板都判成错）。判据是「OBB 的世界 AABB」对 `Collider.bounds`，容差 `max(1e-6, 1e-6·(中心+半长之和))`（30 m 外的盒子 float ulp ≈ 2e-6，1e-6 的绝对容差会误报）。不一致：`Debug.LogError` **一次** + 用 bounds AABB，计 `doubleBake=<检查数>/<不一致数>`，原因码 8。这个自检按 §7.4 的原话「S4 验收后可以删」，现在留着，且成本只在 Setup 一次。
- **剪切**：回退 bounds AABB + **一条** `Debug.LogWarning`（`[arena] shear …`），原因码 1；退化（零边/非有限）原因码 2。两者都不是静默：`refusal=<码>:<文字>` 在报告行末尾，`GetLastRefusalCode()` 给测试场读。

#### 7.6.3 弯坡：段就是坡

导入器（S4a）把一条弧展开成 `<id>_00…` 的 slab 子物体，**逐段进 `rampBoxes`**；arena 对 `rampBoxes` 的每一段走**同一条** ramp→slab 路径，所以：

- 表面 id 是 `SupportSurfaceMath.FirstRampSurfaceId + 已登记段序号`（2026-11-16 起 500 起；一条 8 段弧 = 500..507）。`SupportSurfaceMath.IsRampSurfaceId` 的上界仍是 `MaxRampCount=4` 的 `500..503`，v2 的弯坡会超出——那是**诊断读者**的旧假设，不是登记约束，留给 S5 一起改。
- v2 **不套 45° 门**：θ 是连续处理的，弧末段接近垂直也能登记（这也是 `AddRamp` 在 v2 不被调用的原因之一：`AddRamp` 的坡度门和 4 条上限都不适用）。v1 照旧拒绝 >45° 的段。
- `[arena] … ramps=<成功>/<数组长度>` 统计的是**段数**（`rampBoxes` 的条目数），所以一条弧在报告里就是它的段数；`obbs=` 再给一次总数。

#### 7.6.4 容量与溢出

静态容量就是 S3 的 256（`GetStaticObbCapacity()`）。`CheckObbCapacity()` 在登记前先数一遍（非空 `rampBoxes` + 非空且不在 `rampBoxes` 里的 `staticBoxes`），超了打**一条** `Debug.LogError`（带请求数与容量）；随后逐条 `AddObb` 的拒绝各自计数（`GetStaticObbOverflowCount()` 涨了就是容量拒绝 → 原因码 3，否则 4）。报告行给 `obbs=<成功>/<尝试> obbRefused=<n> obbOverflow=<n>`：**没有静默丢弃**，也没有「悄悄覆盖前一条」。`SuspendRegistration`/`ResumeRegistration`/`Reapply` 的「一次只登记一格」语义不变（`Reapply` 走的还是 `ClearObstacles → Setup`，`ClearObstacles` 现在连 v2 表一起清）。

#### 7.6.5 道具：已接通电机（2026-11-15 补，电机 6b83413）

`PickupFieldHost` 在每次扫描后（同一批候选标记、同一个「太贵不能吸」判据、同一条 `SelectNearest` 选择规则）再选一份最多 12 条的道具集合（6 挡路 + 6 支撑合成一张表），用 `PropObbBakeMath.TryBakeProp` 把 `propLocalBounds` 按道具世界旋转**转一次**建成真 OBB（缺列时退回 `propBounds` 的轴对齐盒，与 v1 逐位相同，并计 `GetPropObbFallbackRows()`）。这就是「径向代理双重旋转」那个 bug 在道具挡路侧的对应修法。

**交接**：`ApplyPropObbSet` 直接调电机的整块替换接口：

```csharp
motor.SetPropObbs(propObbCenter, propObbAxisX, propObbAxisY, propObbAxisZ,
    propObbHalfExtents, propObbSurfaceId, propObbRole, count);   // 容量 12，整批拒绝
```

- **只整块替换**：吸收、走远、变可吸都只是「下一批里没有它」，宿主不维护增删；集合变空时调一次 `motor.ClearPropObbs()`（换集合由整批写覆盖，不需要先清）。
- **只在 `IsContactModelV2()` 时登记**（`ApplyPropObbSet` 里判断，`RefreshPropObbs` 也在 v1 直接返回）：flag 关时电机本来就忽略道具 OBB，每扫描做一次 12 槽校验+拷贝是纯开销。**清空不判断 flag**——一次集合变化只发一次，而且不这样的话 flag 关掉再打开时会把旧块复活。
- **约定**：surface id `100 + 槽位`（100..111），role 2 = 可站（`ContactCodes.RoleProp`）、3 = 挡路（`ContactCodes.RoleBlocking`）；于是 `GetSupportKind()` 的 3（道具支撑）与 `GetContactRole()` 都能认出道具接触。注意 dot 文档里「role 2=挡路」的说法与 `ContactCodes` 相反，以代码为准（2=支撑道具，3=挡路/边界）。
- **拒绝**：`SetPropObbs` 返回 false 时电机保留上一块（它的契约），宿主按 `GetPropObbsRefusalReason()` 打**一条** warning（`BlockingLogLimit` 封顶），不逐扫描刷。容量溢出由电机计数（`GetPropObbsOverflowCount()`），宿主自己的 `GetPropObbOverflowCount()` 仍记「候选数 > 12 的扫描次数」。
- 因此 §7.5.5 第 9 条（道具不进 v2）**已关闭**：v2 的道具挡路/支撑不再需要「关 flag」这个前提。

#### 7.6.6 本轮跑了什么 / 没跑什么

| 跑了 | 结果 |
|---|---|
| `python3 -m unittest discover -s Docs/tests` | 全绿（634 例；S4b 不改 Python 模型，所以没有新增 Python 例） |
| `python3 Docs/tools/validate_plan.py` | PASS（61 tasks；两棵 DAG 无环；源哈希一致） |
| 纯代码审查 + 括号/`out` 全路径人工核对（写它的机器没有 Unity） | 未编译 |
| **没跑** | Unity 编译、U# 全量编译、EditMode、Play Mode、任何场景导入 |

操作者要跑的三组 EditMode：`VRCKatamari.Tests.LevelArenaHostObbTests`（电机级，6 例）、`VRCKatamari.Tests.PropObbBakeTests`（纯数学，5 例）、`VRCKatamari.Tests.PickupFieldPropObbTests`（宿主级，2 例，现在断言真实交接：`GetPropObbsCount()`、surface id 100+槽位、role 3）。三组都要在有 S4b 的 .meta 之后跑（新文件是 `PropObbBakeMath.cs`、`LevelArenaHostObbTests.cs`、`PropObbBakeTests.cs`、`PickupFieldPropObbTests.cs`）。

---

### 7.7 首跑对账（2026-11-15）

依据 `Docs/Evidence/PhysicsTestField/2026-11-15_pass6_contact_v2_first_run/`（commit `18f9f3e`，36 跑，判据一行未改）。这一节只写**v2 下每个检查应有的期望**与“保持/翻转/替换”，**不改** `physics_test_field.json` 与 criteria importer（另有人做）。

#### 7.7.1 本轮据此修掉的电机侧缺陷

| 现象（pass6） | 根因 | 修复 |
|---|---|---|
| `rest_gap` 在 C08/C09/C10 全档读 `−r`（只在爬上台阶/箱顶后） | 不是支撑高度公式，也不是“棱 vs 顶面”：球在**边界平面**上进入了爬升通道，时间线松手后 `v_gate→0`，`ClimbSpeed` 返回 0，于是**悬在墙上**（chunk 高出地面 2–25 cm）。爬升分支又把墙接触点的 y 当“地板高度”，而竖墙的最近点正好在球心高度 ⇒ `support_height = center.y` ⇒ gap = −r | ①`RefreshClimbGate`：爬升门的**持续**条件（§3.2）——门关了/推入没了就退出爬升 + 冷却，球按 `v_slide_max` 滑回；②爬升分支的高度改为**球的底部**（`center.y − r`），没有地板就不假装有 |
| `lift_bounded` C14 全档 +1.9…25.5 cm | 同一根因：C14 的球滚到北边界平面后开始爬升并悬停，悬停高度进了 `bottom − nominal` | 同上（唯一改动）；修后球滑回地面，`lift = −nominal ≤ 0.01` |
| `GetStaticObbCount` 读 0 而 `[arena]` 报 `obbs=1/1` | 跨 GameObject 的 `Start` 顺序任意：宿主先登记时 motor 表还不存在（被拒），或登记后 `Start` 再分配把表覆盖 | `EnsureContactV2Buffers()` 惰性分配 + `Start` 只在表不存在时分配；`AddObb`/`SetPropObbs` 都走它（Python 侧另有 `test_support_height.py` 与 EditMode 三个新例） |

#### 7.7.2 v2 期望表（给判据维护者）

“保持”= 判据与期望都不动；“翻转”= 同一指标、期望值反了；“替换”= 该检查在 v2 下应换指标或标 n/a。

| 检查 | 指标 | pass6 v2 实测 | v2 应有期望 | 处置 |
|---|---|---|---|---|
| `climbed` | `deck_gap_m` | C05 四档 +0.0000（v1 全红 +0.0887…+0.6000） | **保持 pass** | 保持（§6.1 的 C05 翻转已实现） |
| `seam_no_step` | `grounded_frame_dy_m` | C13 2.20 +0.0231（v1 +0.9499） | 保持 pass（≤0.05） | 保持 |
| `rest_gap` | `support_gap_m` | C08/C09/C10 全档 −r（红） | 保持 pass（|gap| ≤ 0.005），修后应回到 +0.000x | 保持判据，**重跑** |
| `lift_bounded` | `lift_over_nominal_m` | pass8：+0.0189/+0.0721/+0.1346/+0.2133（仍红） | **保持 ≤0.01，但指标要加空间门**：只在球位于板的 XZ 足印 ± 1r 内取样（§7.7.4 已证明板本身抬升 ≈0，剩余全部来自远端边界平面的合法爬升） | 判据加门；`nominal` 不变 |
| `on_step` | `deck_gap_m` | C09 0.30 / C10 0.30 = +0.0000 但 `expect=fail`（UNEXPECTED_PASS） | **翻转成 expect=pass**：0.075/0.10 < r(0.30)=0.15，§2.5 的 `h<r` 就该滚过去 | 改 `expect` |
| `blocked_gap` | `blocked_gap_m` | C10 0.30 −4.3338 | **翻转**：0.10 高的挡板在 v2 下不是墙（同上 `h<r`） | 该格改判 `passed_over` 或 expect=pass |
| `ramp_registered` | `ramp_count` | C01/C03/C05/C13 全档 0（16 次红） | **替换**：v2 把坡烘成 OBB，`ramp_count` 天然为 0 | 改读 `GetStaticObbCount() > 0` 或解析 `[arena] obbs=`；`ramp_count` 只给 v1 |
| `no_ramp_registered` | `ramp_count` | — | **n/a**（与上一行互斥的 v1 语义） | 标 n/a |
| `ramp_support` | `ramp_support_seen` | 全 PASS | 保持 | 保持（坡顶仍是 RIDE 接触） |
| `ramp_entered` | `ramp_entry_progress_m` | 7.3–8.3 m PASS | 保持 | 保持 |
| `ramp_refused` / `refusal_slope` | `ramp_refusal_reason_len` / `ramp_refusal_slope_deg` | C06 四档无拒绝 | **翻转**：v2 不拒绝任何坡（§1.5、§8 拍板 9） | 改成 warning 或 n/a（S4 已在 levelbuild 改成 warning） |
| `no_climb` | `max_bottom_rise_m` | C06 四档 +0.3068/+0.5999/+0.6000/… | **替换**：50° 在 v2 必须**慢爬**，不是“不许爬” | 新判据：`max_bottom_rise ∈ [0.5·H_cap, 1.02·H_cap+0.02]` 且 `slide_back ≥ 0.2`（D=0.30/0.60）；D=1.10 判 `climbed`；D=2.20 判 `passed_over`（§6.1 C06 行） |
| `stopped_by_wall` | `blocked_gap_m` | C06 四档 −0.0809/−1.0264/−2.1663/−2.8897 | **替换**：v2 越过挡板继续走 | 改判 `blocked_gap > 0`（越过了）或换 `passed_over` |
| `crossed` / `no_speed_loss` / `not_blocking` / `no_overflow` / `no_saturation` / `size_accuracy` | 各自 | 全 PASS（C14 的 speed_ratio 1.62/1.61/1.48/1.24） | 保持 | 保持 |
| `fall_frames` / `landed_on_ground` / `landing_gap` / `no_slip_back` | 各自 | C13 2.20 由 n/a 恢复为 applicable（球真落回地面） | 保持；2.20 的 `landed_on_ground`（读 `support_kind`）在取值域扩到 4/5 后仍成立（落地是 0，不是 4/5） | 保持，注意 `support_kind` 现在可能是 4/5 |

#### 7.7.3 这一轮**没有**改的

`physics_test_field.json`、`PhysicsTestFieldCriteriaImporter.cs`、`PhysicsTestFieldDriver.cs`、`LevelArenaHost.cs`、`PickupFieldHost.cs`、`Editor/*`：一行未动。上面“翻转/替换”的落地属于判据维护者；本节的表就是交接单。

#### 7.7.4 C14 `lift_over_nominal` 剩余红的定性与处置（2026-11-15 第二轮）

pass8 把 rest_gap 全清、悬停消失，但 C14 的 `lift_bounded` 仍红：+0.0189/+0.0721/+0.1346/+0.2133（≈0.06–0.12·D）。轨迹（`pass8_console.log`）把来源钉死：

| 时刻 | D=0.30 | D=1.10 | D=2.20 | 说明 |
|---|---|---|---|---|
| t≈1.0–1.5 s（在板上） | y=0.18, `sup=0.028` | y=0.58, `sup=0.028` | y=1.13, `sup=0.028` | 球底 = 板顶（lift ≈ +0.002，即 rest_gap 的 +0.0021） |
| t≈3.0 s（球停在 z≈−6.0…−6.6，`sup`>0） | y=0.19, `sup=0.045` | y=0.70, `sup=0.153` | y=1.32, `sup=0.217` | **在场地远端边界平面上爬升**（驱动到 3.0 s 才松手） |
| t≈3.5 s 后 | y=0.15 | y=0.55 | y=1.10 | 松手 → 门关 → 滑回地面（本轮修复生效） |

- **理论值**：球滚过高度 h 的板，球底最高只能到 h（棱接触的切平面永远把球心放在 `corner + r·n`，`n.y ≤ 1` ⇒ `bottom ≤ h`）。参考模型复现（`test_plate_lift.py`，D=0.30/0.60/1.10/2.20，4 m/s）：**板内抬升 = −0.0002…0.0000**；同一驱动继续撞到 z=+6 的边界平面后，未加门的总抬升变成 +0.253/+0.558/+1.058/+2.159（≈0.84·H_cap，爬升率 `v_up0 ∝ H_cap ∝ D`）。
- **结论：判据语义问题，不是实现缺陷。** `lift_over_nominal` 取全程最大值，而 C14 的驱动一直推到远端边界平面；v2 的“重推就能爬”是设计行为（§3.2），v1 读 0 只是因为它根本不会爬。
- **处置（给判据维护者）**：`nominal` 保持 0.028（板顶，不是 D 的函数）；给采样加**空间门**——只在球的 XZ 位于板的足印 ± 1r 内累计 lift，门内 `lift ≤ 0.01` 在四档都成立（模型与 EditMode 各一条测试）。或者把 C14 的时间线在到墙前收住（改数据，不改判据）。若坚持全程最大值，则应把期望改成 `≤ max(0.01, k·D)` 并把该指标的语义改名为“全程最高抬升”，但那会失去“板不是坡”的判据意义。
- 同批测试：`Docs/tests/ball_contact_v2/test_plate_lift.py`（3 例：板内 ≤0.01、确实滚过去并落回地面、未加门时抬升 ∝ D 的 characterization）；EditMode `ARollingBallIsNotLiftedAboveAThinPlate`（门内 ≤0.01 + 滚过 + 落回 + support kind 0）。


---
## 8. 需要拍板

| # | 决定 | 选项 | 我的推荐 | 影响 |
|---|---|---|---|---|
| 1 | **坡要不要从侧面/下面挡** | ① 实体 slab（挡） ② 只挡顶面（v1） | **①**：用户看到的就是“穿过去”，且实体板让“坡 = 一个 OBB”成立 | 关卡数据：0.35 m 窄坡会变成障碍；薄板要加厚 |
| 2 | **`k_h`（H_cap = k_h·2r）** | 0.75 / 1.0 / 1.25 | **1.0**（官方手册锚） | 直接决定“多高的台阶/墙能上”；0.75 更保守 |
| 3 | **爬墙要不要按钮** | ① 只推入（官方） ② 额外按键 | **①**：官方手册就是“双摇杆前推”；② 会让 VR 输入更复杂 | 输入层 |
| 4 | **dash** | ① 1.6×/0.45 s/冷却 1.2 s ② 不做 ③ 更强 | **①**；dash 只加速/提速爬升，不改 H_cap | 输入层 + 手感 |
| 5 | **台阶阈值变成 ~1.0D 是否接受** | ① 接受（滚/爬/墙三段） ② 把爬升限制在墙（台阶不爬） | **①**，但用 `k_h` 调；若嫌“什么都能上”，降 `k_h` 到 0.6–0.75 | 所有台阶格的重定义（§6.1） |
| 6 | **`θ_ride = 45°`** | 45 / 50 / 55 | **45**（45 坡必须滚、50 坡必须慢爬、60 坡必须不能冲） | C05/C06 的语义 |
| 7 | **弯坡表示** | ① 薄片 OBB 链 ② 解析圆柱 | **①**；② 只在 R≥20 m 半管时单独立项 | 导入器/关卡数据/容量 |
| 8 | **大地图与 9 格的关系** | ① 并存（小格判据、沙盒手感） ② 替换 | **①**（Lead 已明确“9 格留作自动化回归”） | 测试场工作量 |
| 9 | **45° 作者门何时改 warning** | S4 立即 / 更晚 | **S4**（否则 60/70 坡没法入库） | `levelbuild` + 既有 level 数据 |
| 10 | **凸出物是否加 H_cap** | ① 加（λ=1.0，受 0.105–0.30 cap） ② 不加 | **①**，但效果很小；要明显就单独抬 `L_climb` 上限（另立项） | W4 的判据 |
| 11 | **埋没道具的边距** | 0.01 / 0.02 / 0.05 m | **0.02**（≈1.3% 的 1.10 球） | 视觉“什么时候消失” |
| 12 | **窗口容量 24 / 静态 256** | — | **24 / 256**（沙盒峰值 16 / 118） | 内存 ~25 KB；溢出显式失败 |
| 13 | **视觉核心 0.95 r_phys** | 0.90 / 0.95 / 1.00 | **0.95**（原作估计，§10.3） | 只影响外观与相机；物理不动 |
| 14 | **pickup 用外接盒（方向相关）还是球** | ① 盒（1.0–1.73 r） ② 球 k=1.4 ③ 球 k=1.73 | **①**（专利口径、无新常数）；广相用 k=1.5 的球兜底 | 吸附距离在斜方向变大；可能“隔墙吸” |
| 15 | **针尖是否参与横向阻挡** | ① 全参与（支撑+爬升+挡墙） ② 只支撑+爬升 | **①**（用户体感“撑出来的东西在碰”），但门洞净宽按 `2(r+cap)` 校验 | 关卡门宽、长杆手感 |
| 16 | **隔墙吸是否处理** | ① 接受（原作也没有遮挡） ② 另立项 | **①**，写进 evidence；Udon 没有 Linecast，②的代价高 | 吸附手感 |

---

## 9. 附录

### 9.1 符号

| 符号 | 含义 |
|---|---|
| `r`, `D` | 碰撞半径（= 核心半径），直径 `D=2r` |
| `n` | 接触法线，从实体指向球心（单位） |
| `θ` | `acos(n.y)`，度 |
| `h` | `C.y − climbAnchorY` |
| `H_cap` | `k_h·(2r + λL_eff)` |
| `p` | 推入强度 `max(0, −dot(û, n_h))` |
| `v_gate` | `max(|v_planar|, k_intent·v_drive)` |
| `v_up` / `v_along` | 竖直 / 沿切线爬升速率 |
| `N` | 子步数 |
| `κ` | 每子步位移上限 / r |

### 9.2 参考模型 API（与将来 C# 同名同序）

```python
closest_point_on_obb(obb, point) -> (point, distance, normal, feature)
sphere_obb_contact(obb, center, radius, skin) -> Contact | None
classify_contact(normal, theta_ride_deg) -> CLASS_NONE | CLASS_RIDE | CLASS_CLIMB
max_substeps(displacement, radius, kappa, cap) -> int
climb_height_cap(radius, protrusion_m, k_h, lam) -> float
required_speed(theta_deg, v_min, alpha) -> float
climb_speed(push_in, v_gate, theta_deg, height_m, h_cap_m, climb_time_s) -> float
step(world, state, drive_dir, drive_speed, dt, ...) -> StepReport
tessellate_vertical_curve(origin, forward, up, radius, arc, segments, width, thickness, id0)
select_window(obbs, center, radius, max_travel, previous_ids, capacity, ...) -> NearWindow
# shapes.py（§4A）
visual_core_radius(r_phys, scale) -> float
needle_protrusion_cap(r_phys) -> float
RollingShape(core_radius, directions, supports).tip_offsets_world(rotation)
needle_tip_contact(obb..., tip, tip_radius) -> (normal, depth, point) | None
pickup_reach(direction, r_phys, shape) -> float
rolling_reach(direction, shape) -> float
grip_from_protrusion(protrusion, r_core, k_grip) -> float
required_speed_with_grip(theta_deg, protrusion, r_core) -> float
```

### 9.3 本轮实际跑了什么 / 没跑什么

| 跑了 | 结果 |
|---|---|
| `python3 -m unittest discover -s Docs/tests` | **534 例全过**（含 `ball_contact_v2` 的 32 例：24 接触 + 8 形状） |
| `python3 Docs/tools/validate_plan.py` | PASS（61 tasks，DAG 无环，source hashes 通过） |
| 参考模型的验收场景 | 45° 接缝到 0.750 m；90° 墙上限 0.581 m（cap 0.60）后滑回；70° 坡上到 0.39 m 台面；0.5D 低墙翻过；50° 板 D=0.30 爬到 0.299 后滑回、D=1.10 上台面 |
| 形状层（`shapes.py`，8 例） | 视觉核心 0.95 r；pickup 盒 1.0/1.41/1.73 r 且 ≥ 滚动 reach；针尖支撑被 cap 夹住；只有突出的针进接触；针尖随球旋转；针尖 point 接触的深度/法线；grip 在半个 r 的突出时把 90° 门槛减半 |
| **没跑** | Unity 编译、U# 全量编译、EditMode、Play Mode、profiler、任何帧时间/手感；**本文所有 Unity 侧结论都是设计，不是证据** |

### 9.4 与其它文档的交叉引用

- 现状审计：`/tmp/ball_audit/report.md`（A–G 节；本文 §1.1/§2.6/§2.8 引用其行号结论）
- 原作研究：`/tmp/orig_physics/report.md`（§1 爬墙证据、§3 坡是速度门、§6.3 爬升模型草案）
- 测试场：`Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md`（§4.1.3 死锁、§4.1.5 跳变、§6.1 协议、§5.2 判据字典）
- 地形支撑：`Docs/Design/BallMotor/TERRAIN_SUPPORT.md`（§4 台阶、§5 斜面——本文 §2.5/§1.5 取代其物理，作者校验保留）
- 马达契约：`Docs/Design/BallMotor/README.md`（9 阶段顺序；§7.3 给 v2 的目标）
- 尺寸/吸附：`Docs/Design/PickupAbsorb/README.md`、`Docs/Design/StoryMode/PROP_CATALOG.md`（沙盒道具尺寸）
- 参考模型：`Docs/Design/BallMotor/contact_v2/contact_v2.py`、`.../shapes.py`、`Docs/tests/ball_contact_v2/test_contact_v2.py`、`.../test_shapes.py`
