# T50 球层：径向凹凸代理（小棍采样外壳）

> **状态：已接进接触求解，`PROVISIONAL_NOT_TESTED`。** 组合层 `RadialProxyShellMath` 由 `KatamariBallMotor`（吸附/移除/重建）与 `AttachmentStepMath`（吸附登记）调用；16 根针的针尖在 `StepContactV2` 里作为扫掠段参与接触（见 [BallMotor README](../../BallMotor/README.md)「径向凹凸代理」与 [CONTACT_MODEL_V2](../../BallMotor/CONTACT_MODEL_V2.md) §4A）。纯逻辑 EditMode 测试已写；**没有 Play Mode、没有 VR 实测**。方向数已定为 16（理由见“为什么是 16”）；4 个长物件候选槽、准入规则仍是未测原型值，不是实测预算或原作数值。

## 研究与项目规则边界

- 未取得原版源码；US7402104B2 仅为历史专利公开，不能证明新版采用。官方产品资料不含径向支持算法；GitHub Ruby/Gosu 资源是独立 2D 实验而非原版源码。详见 [`CONTACT_SOURCE_EVIDENCE.md`](../../../Research/KatamariBallRules/CONTACT_SOURCE_EVIDENCE.md)。
- 项目 `Docs/02_TECHNICAL_CONTRACTS.md` §3.2 写有方向采样、每对象端点、max 支持、移除重算/账本重建。本层实现这些是项目自定义近似，不代表反编译或还原原作。
- Unity 左手坐标与 Cross 规则已对照官方资料；Python host Hamilton quaternion 不是 Unity 相等性主张。C# 测试里的方向箭头对拍（`AngleAxis(+90°, +Y)` 下 +Z 落轴、world/local 往返）是显式 adapter 的方向事实。

## 模块行为

- 16 个确定性 Fibonacci 方向（已定；此前 32 是 provisional 选择）。每个方向存一个**端点支持长度**（不是历史 max）：表里只存端点最大值，读取时再与当前核心取 max。帧时间仍未做设备验证。
- 端点在球局部系里是静态的（随 `BallVisualRollRoot` 旋转），世界端点只在吸附那一刻转换一次。
- **16 方向表与端点池的关系**：方向表就是球的 16 根针（`supports[d] = max dot(p_i, d)`，p_i 是**所有**已吸附物的盒角点）；每步 `PrepareNeedleShape` 把每根针夹到 `[r, r + cap]`（`cap = min(0.35·r, 0.30 m)`），突出的针尖作为扫掠段进接触求解。端点池只是 detach/重建时重新推导针表的账本。
- 每件吸附物都是一个 item（不再只收长物件）：输入是稳定实例键（`itemId`，重试/世代由宿主保证不重复）和它的 8 个 AABB 角点。吸附时是**增量 max 合并**（只点积新件的 8 个角点）；移除时按**幸存端点**重算受影响方向，所以两件共享方向时去掉一件不会误删另一件。
- 数学层仍保留“极值候选键”（最多 4 个、满额拒绝不驱逐），马达传长度 0 的候选数组，不使用。
- `TryRebuild` 先在临时逻辑里校验整个账本，全部成功后才替换支持表与候选表；失败保留旧壳。
- 本层不实现接触求解（`ContactSolveMath`/`BallShapeMath`）、价值/吸附裁决、Renderer、掉落轨迹、真实 mesh 碰撞或 PVP 外壳。稀疏方向/半球近邻可能漏掉中段或相邻采样峰值；性能及跨平台逐位一致性未测。

### 为什么是 16（决定与证据）

**方向表只是摘要，不是物理参数。** 16 个方向的支持表服务重建、候选记账、诊断和接触求解里的针尖；改方向数改变的是这张摘要表的分辨率，以及每次吸附/删除/重建的点积次数。

**证据等级 `patent`（研究证据，不引用原文）：** 原作滚动模型被记载为 16 根固定针 + 4 根可选针，针尖即接地点，吸附物把附近的针伸长（Namco 专利 US7402104B2，2003-09-25 优先权；见 `Docs/Research/KatamariBallRules/`）。这支持“16 是原作使用的量级”，但**不证明**新版实现或本项目必须逐位相同；本项目仍用自己的 Fibonacci 采样与端点云。

**分辨率代价（可复算）：** 把 16/32 两套 Fibonacci 方向分别与球面网格比，2° 经纬网格采样测得最坏角隙 **27.38°（32 向）→ 38.88°（16 向）**（加密到 20 万点复核为 27.63°/39.04°，量级与结论不变）。平均最近邻从 34.12° 增到 47.45°。若细长尖刺正好落在两个方向中间，摘要表会低估 `cos(38.9°) ≈ 0.78` 倍；这只影响表值，不影响端点云的接地高度。

**此前的 32 是 provisional 选择**，不是原作值，也没有设备实测支撑；改成 16 后方向数不再作为调参项。真源是 `RadialProxyMath.DirectionCount` 这**一处 C# const**；Python 参考模型是同一文件里的一处 `DIRECTION_COUNT`（默认值与硬校验都引用它），`prototype_defaults.json` 的 `shape.directionSamples` 是可读副本，靠等价测试/人工核对保持一致。**另一个 32 不动**：端点池是 4 个长物件 × 8 个 AABB 角点 = 32 个端点，与方向数无关。

### 尺寸口径：`propBounds`（世界 AABB）与 `propLocalBounds`（道具局部轴）

径向代理壳读的是**道具自身局部轴上的全长**，不是世界 AABB。两个口径各有消费者，不能互换：

| 序列化列 | 口径 | 消费者 |
|---|---|---|
| `propBounds` | 道具的**世界 AABB**（已含道具根旋转；`@size=` 时是声明边长的立方体） | 吸收体积门、挡路盒、支撑分类——都把它当轴对齐**世界**盒读，旋转只发生一次且是正确的 |
| `propLocalBounds` | 道具**自身局部轴**上的全长（不含道具根旋转；子节点的相对旋转/缩放已烘进去；`@size=` 同样写成声明立方体） | 径向代理壳（`TryBuildAabbCorners` / `motor.AttachRadialProxy`） |

为什么必须分两列：`AttachmentMath.TryWorldPoseToLocal` 给出的 `localRotation = inverse(ball) × 道具世界旋转` **已经含道具自身旋转**，壳再算 `localCenter + localRotation * (±尺寸/2)`。如果把世界 AABB 交给它，躺椅（yaw 90°）的长轴会被转第二次，落到错误的球局部轴上。躺椅的模型是 `1.60 × 0.35 × 0.60`（长轴在自身 X），世界 AABB 是 `0.60 × 0.35 × 1.60`；壳要的是前者。吸收门只看体积、挡路/支撑按世界轴建盒，都不受这个问题影响。

躺椅吸附后要看壳的长轴，最直接的是 `RadialProxyLocalBoundsTests` 里同一个 1.6 × 0.35 × 0.6 盒的用例。

`Editor/Pickup/PickupFieldSync` 在一次同步里同时写两列：世界 AABB 仍由 `Renderer.bounds` 并集得到；局部尺寸把每个 mesh 的 `sharedMesh.bounds` 角点变换到道具根局部系再合并，最后乘根三轴的世界长度（`localToWorld` 列范数），所以道具或父级有缩放时结果仍是米。

旧场景没有 `propLocalBounds`（或长度与 `itemIds` 不一致、某行非有限/负数）时，宿主回退到 `propBounds`，并打**一条**带原因码的 warning：`1` 缺列、`2` 长度与 `itemIds` 不一致、`3` 行值非法；`[PickupFieldHost] ready` 行也会写 `proxyBounds=local|fallback`。回退行为与修复前相同（旋转道具的壳仍然错），只影响吸附壳，吸收门/挡路/支撑不受影响。

## Unity 实现

### 纯数学（无状态）

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/RadialProxy/RadialProxyMath.cs`（`namespace VRCKatamari.BallLayers`，`public static class RadialProxyMath`）把模型搬成 UdonSharp 可用的无状态静态数学：不分配、无静态字段、无重载/属性/struct，索引与结果都写进调用方预分配的数组。

- 调用方持有壳表：16 个方向（`TryFillDirections` 一次生成）、16 个支持长度、扁平端点池、候选键槽位；本类不持有状态，也不读 Transform。
- 端点：`TryDirectionLengthPoint`（p=d·l）、`TryWorldPointToLocal`、`TryTransformEndpointsToLocal`。
- 支持：`TryCollectAffectedDirections`（dot>0 半球，升序）、`TrySolveSupports`（只重算给定方向）、`TryProtrusion`。
- 候选：`TryInsertCandidateKey` / `TryRemoveCandidateKey`，序数有序、重复/空键/超额拒绝，满额绝不驱逐。
- 整账本：`TryRebuild` 校验完整个调用方账本（键唯一、每项至少一个端点、候选不超容量）后才写支持与候选。
- `IsUnitRotation` 现为 public：组合层构建角点时要用与本层相同的“拒绝缩放四元数、不归一化”规则。

### 组合协议（无状态，唯一实现）

`Runtime/BallLayers/RadialProxy/RadialProxyShellMath.cs`（`public static class RadialProxyShellMath`）是“吸附物 → 端点池 → 受影响方向 → 接地接触点”的**唯一**协议实现；它不复制 `RadialProxyMath` 的数学，只按顺序调用（针夹紧与针尖接触在 `BallShapeMath`）：

| 方法 | 作用 |
|---|---|
| `TryBuildAabbCorners(localCenter, localRotation, fullExtents, corners, out farthest)` | 8 个球局部 AABB 角点 + 最远角点距离（相对球心）；`fullExtents` 是道具局部轴全长 |
| `TryResolvePropExtents(local, world, index, rows, out extents, out source)` | 选列：`propLocalBounds` 可用则用（source 0），否则回退 `propBounds` 并给原因码 1 缺列 / 2 长度不符 / 3 行值非法 |
| `TryAttachItem(...)` | 追加 8 端点，**增量**合并：只把新件的 8 角点对受影响方向点积并对支持表取 max（不重扫整个池）；容量/键/端点全部先校验，失败不动旧表。马达对每件吸附物调用（`isCandidate=false`） |
| `TryDetachItem(...)` | 复制被删角点 → 压缩池和 item 行 → 按幸存端点重算受影响方向 → 释放候选键 |
| `TryRebuildShell(...)` | 整账本原子重建（调用 `RadialProxyMath.TryRebuild`） |
| `TryClearShell(...)` | 重试/整场复位：清空方向支持、端点池、item 行、候选 |

### 归属

外壳状态在 `KatamariBallMotor`：它持有球位姿并驱动接触求解。数组在 `Start` 一次分配，运行期不分配：

- `proxyDirections[16]`、`proxySupports[16]`
- `proxyEndpointPool[2048]`（= 256 件 × 8 角点，**每件吸附物都登记**）、`proxyEndpointCount`
- `proxyItemStarts/Lengths/Keys/IsCandidate[256]`、`proxyItemCount`
- `proxyCandidateKeys[0]`、`proxyCandidateCount`（数学层仍支持候选键，马达不用）

**没有另起一个 BallLayer 组件**：`RadialProxyMath` 是纯静态的，状态必须由某个 Behaviour 持有；motor 已经持有球位姿并驱动接触求解，再把壳状态放到第二个组件会多一次跨组件查找、多一份生命周期同步，而没有任何所有权收益。吸附事件从 `PickupFieldHost` 进入 motor 的公开 seam，宿主不持有代理状态。

### 数据流

```
吸收确认（GrowthLedgerMath commit）
  → PickupFieldHost.AttachProp：算局部位姿、SetParent(attachmentRoot)
  → motor.AttachRadialProxy(itemId, propLocalBounds, localPosition, localRotation)
      → 8 角点 + 只重算受影响方向
  → 每固定步 StepContactV2：
      PrepareNeedleShape        // 16 个支持长度夹到 [r, r+cap]，选出突出的针
      ... 近集 OBB / 平面 / 地形三角形 与核心球求解 ...
      ResolveNeedleContacts     // 针尖按段扫掠，推挤、去速，接触并入同一份接触记录
```

移除/重置路径：

| 事件 | 调用 | 语义 |
|---|---|---|
| 单件移除（目前无运行时掉落） | `motor.DetachRadialProxy(itemId)` | 按幸存端点重算受影响方向，释放候选键；未注册的 id 返回 false，壳不变 |
| 整场复位 / 再试一次 | `PickupFieldHost.ResetField` → `motor.ClearRadialProxies()`，以及 `TryResetForNewRound` 内部再清一次 | 所有 prop 回原父级、原局部位姿；壳清空，旧突出绝不残留 |
| 交分/大幅缩小 | `TryBank` → `ApplyBankShrink` → `RecomputeConfirmedAndSize` → `RebuildRadialProxies()` | 整账本重建（原子）；端点池不缩放 |
| 传送/重生 | `TeleportTo` → `RebuildRadialProxies()` | 端点本来就是球局部量，重建只保证支持表与账本一致 |
| 核心长大 | 不写支持表 | 表存端点最大值；每步按当前半径夹紧，`max(0, l−r)` 自然收回 |

### 端点取法与理由

**每个长物件取 AABB 的 8 个角点**，不是沿最长轴的两个端点：

1. 长杆的尖端本来就是 AABB 的角；8 角点覆盖杆/板/长凳的所有极端方向，而“两个轴向端点”会漏掉斜放扁板的侧角，支撑会系统性偏低。
2. 壳用的 `propLocalBounds` 与吸收体积门/挡路盒用的 `propBounds` 是**同一件道具的两种口径**：前者是局部轴全长（供壳旋转一次），后者是世界 AABB（供轴对齐世界盒）。两列由同一次同步写出，`@size=` 对两者都生效；壳不再直接读 `propBounds`。旧场景未重跑同步时宿主回退到 `propBounds`，那会复现旋转两次的问题（见“尺寸口径”一节）。
3. 两个端点虽然更省，但会让“长杆必须能被表示为突出”依赖于杆恰好沿某轴放。

**候选槽与准入**：数学层保留“极值候选键”（最多 4 个、满额拒绝不驱逐），马达传长度 0 的候选数组，不使用；每件吸附物都登记进端点池。吸附时最远角点连 `core + minProxyProtrusionM`（0.01 m）都不到的件不登记（对支撑没有贡献）。

### 上限

针的突出上限 `cap = min(0.35·r, 0.30 m)`（`BallShapeMath.NeedleProtrusionCap`，常量在 `ContactTuning.NeedleProtrusionPerRadius/WorldM`），只在**每步生成针尖时**施加，不写回端点池：池里保留原始长度，所以球长大后 `l − r` 自然减小，而不会因为吸附时截断过而永远长不回来。夹紧是**径向**的：每根针的长度被缩到 `core + cap`，任何方向的 support 都不会超过上限。唯一的序列化准入字段是 `minProxyProtrusionM`。

镜头锚点就是球心：v2 没有“代理抬升”，视线跟随球心的真实爬升（CONTACT_MODEL_V2 §4.4）。

### 成本

- 吸附一次：8 角点 + 受影响方向扫描（最坏 16×8 点积）+ 受影响方向支持重算（最坏 16 方向 × 端点池）；移除同理。
- 整账本重建（16 方向 × 端点池点积）只在交分、传送、重试时发生。
- 运行期零分配（所有数组 `Start` 一次分配）。每步针尖接触的成本见 CONTACT_MODEL_V2 §5。

## 测试

- Python 模型测试：`python3 -m unittest discover -s Docs/tests/ball_radial_proxy -v`。
- C# EditMode：`Tests/EditMode/BallLayers/RadialProxyTests.cs`（数学层）、`RadialProxyShellTests.cs`（组合层：单根长棍的支持长度、移除后回落、两件共享方向去掉一件不误删另一件、核心长大后原始突出收回、增量与整账本重建等价、满候选拒绝第五件且旧壳不变、角点随球局部旋转、多件增量合并与幸存端点重算）、`RadialProxyLocalBoundsTests.cs`（`propLocalBounds` 口径：躺椅按自身长轴、缺列回退并给原因码、根旋转不烘进局部列、子节点旋转烘进局部列）。
- Python 模型只接收预烘焙的 `endpoints_world`，不读道具 bounds。

**没有跑 Unity 编译、EditMode/NUnit 或 Play Mode**。以上测试是源码级证据，不等于 Unity 证据。

## 未决/风险

- **4 个候选是拒绝制**（合同模型）：第 5 件长物件不驱逐已有候选，只被跳过。若实机发现长物件经常超过 4 件，应先做候选选择策略（按突出量排序、显式驱逐），而不是偷偷扩容量。
- **两列 AABB 都是保守近似**：局部列把子 mesh 的旋转/缩放烘成根局部 AABB，斜放扁板的角点可能比真实网格高；吸收门/挡路盒继续用世界 AABB，径向壳用局部列，两个消费者对“这件道具多大”不再共用同一个数——口径不同是有意的，见“尺寸口径”。`@size=` 声明尺寸对两列都生效，所以勺 `0.34³`、保温杯 `0.9³` 这类声明件仍然是立方体、仍过不了伸长准入；未声明且 yaw 0 的道具，世界 AABB 与局部列本来就相同。**旧场景未重跑同步时壳回退世界 AABB，旋转道具的双转问题仍在**；`banana_01–04` 这类 yaw 0 的现有长件不受回退影响。更长的门挡长凳等整张 PicnicLawn 进场景后才会出现。
- **没有掉落路径**：吸附物是终态，所以“掉落”只有整场复位；接掉落时必须调 `DetachRadialProxy`，不能只移动 Transform。
- **范围**：针尖进接触求解；PVP 外形摘要与远端球不使用它，核心碰撞主体仍是球形碰撞半径。
- **未测**：Unity 编译、U# 全量编译、EditMode/NUnit、Play Mode、帧率、VR 舒适度。16 方向是已定值（理由见“为什么是 16”）；4 候选槽与准入规则仍是原型值。
