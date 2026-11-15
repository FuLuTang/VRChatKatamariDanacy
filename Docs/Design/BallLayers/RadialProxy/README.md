# T50 球层：径向凹凸代理（小棍采样外壳）

> **状态：B 阶段已接线，`PROVISIONAL_NOT_TESTED`。** C# 组合层 `RadialProxyShellMath` 已接进 `KatamariBallMotor` 的第 4 步（接地高度）与 `PickupFieldHost` 的吸附确认；纯逻辑 EditMode 测试已写，但**没有在 Unity 编译、没有跑 `run_tests`、没有 Play Mode**。N=32、4 个长物件候选、三个上限都是未测原型值，不是实测预算或原作数值。

## 研究与项目规则边界

- 未取得原版源码；US7402104B2 仅为历史专利公开，不能证明新版采用。官方产品资料不含径向支持算法；GitHub Ruby/Gosu 资源是独立 2D 实验而非原版源码。详见 A 套 [`SOURCE_EVIDENCE.md`](../GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md)。
- 项目 `Docs/02_TECHNICAL_CONTRACTS.md` §3.2 写有方向采样、每对象端点、max 支持、移除重算/账本重建。本层实现这些是项目自定义近似，不代表反编译或还原原作。
- Unity 左手坐标与 Cross 规则已对照官方资料；Python host Hamilton quaternion 不是 Unity 相等性主张。C# 测试里的方向箭头对拍（`AngleAxis(+90°, +Y)` 下 +Z 落轴、world/local 往返）是显式 adapter 的方向事实。

## 模块行为

- 32 个确定性 Fibonacci 方向；原型值，尚未做设备性能验证。每个方向存一个**端点支持长度**（不是历史 max）：表里只存端点最大值，读取时再与当前核心取 max。
- 端点在球局部系里是静态的（随 `BallVisualRollRoot` 旋转），世界端点只在吸附那一刻转换一次。
- **32 方向表与端点池的关系**：方向表是合同要求的稀疏外壳视图（`supports[d] = max dot(p_i, d)`），用于重建、候选记账、诊断和以后 C 阶段的外形摘要；**接地高度按合同公式直接取端点 `p_i`**（`h(-n)=max(r, max_i dot(-n, Q·p_i))`），不先投影到 32 方向再插值——`GroundContactMath` 的接口本来就是点数组，端点云是同一外壳的精确来源，先采样再插值只会多一次近似。两者不会漂移：每次吸附/删除只用同一批角点更新受影响方向，重建时两者同源。
- 长物件候选最多 4 个；输入是稳定实例键（`itemId`，重试/世代由宿主保证不重复）和它的 8 个 AABB 角点。吸附时只重算端点外半球方向；移除时按**幸存端点**重算同一批方向，所以两根棍共享方向时去掉一根不会误删另一根。
- 极值候选最多 4 个（原型值）；满额**明确拒绝**，不驱逐。移除候选后槽位恢复；对外 ID 按字符串稳定排序。
- `TryRebuild` 先在临时逻辑里校验整个账本，全部成功后才替换支持表与候选表；失败保留旧壳。
- 本层不实现接触高度（`GroundContactMath`）、Sweep、价值/吸附裁决、Renderer、掉落轨迹、真实 mesh 碰撞或 PVP 外壳。稀疏方向/半球近邻可能漏掉中段或相邻采样峰值；性能及跨平台逐位一致性未测。

## Unity 实现

### 纯数学（无状态）

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/RadialProxy/RadialProxyMath.cs`（`namespace VRCKatamari.BallLayers`，`public static class RadialProxyMath`）把模型搬成 UdonSharp 可用的无状态静态数学：不分配、无静态字段、无重载/属性/struct，索引与结果都写进调用方预分配的数组。

- 调用方持有壳表：32 个方向（`TryFillDirections` 一次生成）、32 个支持长度、扁平端点池、候选键槽位；本类不持有状态，也不读 Transform。
- 端点：`TryDirectionLengthPoint`（p=d·l）、`TryWorldPointToLocal`、`TryTransformEndpointsToLocal`。
- 支持：`TryCollectAffectedDirections`（dot>0 半球，升序）、`TrySolveSupports`（只重算给定方向）、`TryProtrusion`。
- 候选：`TryInsertCandidateKey` / `TryRemoveCandidateKey`，序数有序、重复/空键/超额拒绝，满额绝不驱逐。
- 整账本：`TryRebuild` 校验完整个调用方账本（键唯一、每项至少一个端点、候选不超容量）后才写支持与候选。
- `IsUnitRotation` 现为 public：组合层构建角点时要用与本层相同的“拒绝缩放四元数、不归一化”规则。

### 组合协议（无状态，唯一实现）

`Runtime/BallLayers/RadialProxy/RadialProxyShellMath.cs`（`public static class RadialProxyShellMath`）是“吸附物 → 端点池 → 受影响方向 → 接地接触点”的**唯一**协议实现；它不复制 `RadialProxyMath` 的数学，只按顺序调用：

| 方法 | 作用 |
|---|---|
| `TryClassifyLongObject(bounds, core, minElongation, minLongestEdgePerCoreDiameter, out bool)` | 长物件准入：最长边 ≥ 0.5 × 球直径 **且** ≥ 2.5 × 第二长边（一个轴压过另外两个轴） |
| `TryBuildAabbCorners(localCenter, localRotation, fullExtents, corners, out farthest)` | 8 个球局部 AABB 角点 + 最远角点距离（相对球心） |
| `TryAttachItem(...)` | 追加 8 端点、只重算受影响方向、登记候选键；容量/键/端点全部先校验，失败不动旧表 |
| `TryDetachItem(...)` | 复制被删角点 → 压缩池和 item 行 → 按幸存端点重算受影响方向 → 释放候选键 |
| `TryRebuildShell(...)` | 整账本原子重建（调用 `RadialProxyMath.TryRebuild`） |
| `TryClearShell(...)` | 重试/整场复位：清空方向支持、端点池、item 行、候选 |
| `TryBuildContactPoints(core, maxProtrusion, endpoints, count, contactPoints)` | 每步交给 `GroundContactMath` 的数组：长度夹到 `core + maxProtrusion`，未用槽清零 |

### 归属

外壳状态在 `KatamariBallMotor`：它已经持有球位姿，并且是唯一调用 `GroundContactMath` 的地方。数组在 `Start` 一次分配，运行期不分配：

- `proxyDirections[32]`、`proxySupports[32]`
- `proxyEndpointPool[32]`（= 4 个候选 × 8 角点）、`proxyEndpointCount`
- `proxyItemStarts/Lengths/Keys/IsCandidate[4]`、`proxyItemCount`
- `proxyCandidateKeys[4]`、`proxyCandidateCount`
- 每步复用的 `localProxyPoints[32]`、`proxyAffectedIndices[32]`、`proxySolveScratch[32]`、`proxyCorners[8]`、`proxyRemovedCorners[8]`、`proxyEmptyPoints[0]`

**没有另起一个 BallLayer 组件**：`RadialProxyMath` 是纯静态的，状态必须由某个 Behaviour 持有；motor 已经持有 `localProxyPoints` 并每步喂 `GroundContactMath`，再把壳状态放到第二个组件会多一次跨组件查找、多一份生命周期同步，而没有任何所有权收益。吸附事件从 `PickupFieldHost` 进入 motor 的公开 seam，宿主不持有代理状态。

### 数据流（接通后）

```
吸收确认（GrowthLedgerMath commit）
  → PickupFieldHost.AttachProp：算局部位姿、SetParent(attachmentRoot)
  → motor.AttachRadialProxy(itemId, propBounds, localPosition, localRotation)
      → 长物件准入 + 8 角点 + 只重算受影响方向 + 候选键
  → 每步 StepBall 第 4 步：
      TryBuildContactPoints(core, cap)         // 长度夹到 core+cap
      GroundContactMath.TrySolve(代理点)        → groundedCenter（球芯高度随凹凸）
      GroundContactMath.TrySolve(空数组)        → followCenter（核心球心，只给镜头）
  → ApplyPresentation: ballPhysicsRoot.position = groundedCenter
```

移除/重置路径：

| 事件 | 调用 | 语义 |
|---|---|---|
| 单件移除（目前无运行时掉落） | `motor.DetachRadialProxy(itemId)` | 按幸存端点重算受影响方向，释放候选键；未注册的 id 返回 false，壳不变 |
| 整场复位 / 再试一次 | `PickupFieldHost.ResetField` → `motor.ClearRadialProxies()`，以及 `TryResetForNewRound` 内部再清一次 | 所有 prop 回原父级、原局部位姿；壳清空，旧突出绝不残留 |
| 交分/大幅缩小 | `TryBank` → `ApplyBankShrink` → `RecomputeConfirmedAndSize` → `RebuildRadialProxies()` | 整账本重建（原子）；端点池不缩放 |
| 传送/重生 | `TeleportTo` → `RebuildRadialProxies()` | 端点本来就是球局部量，重建只保证支持表与账本一致 |
| 核心长大 | 不写支持表 | 表存端点最大值、下限由 `GroundContactMath` 每步加当前核心；`max(0, l−r)` 自然收回 |

### 端点取法与理由

**每个长物件取 AABB 的 8 个角点**，不是沿最长轴的两个端点：

1. 长杆的尖端本来就是 AABB 的角；8 角点覆盖杆/板/长凳的所有极端方向，而“两个轴向端点”会漏掉斜放扁板的侧角，支撑会系统性偏低。
2. `propBounds` 是项目里已经统一的简化体积（吸收体积门、挡路盒都用它），8 角点是同一个盒子的保守支撑，三个消费者对“这件道具多大”只有一个答案。
3. 成本有界：4 个候选 × 8 = 32 个端点，正好是每步扫描的常数上限；两个端点虽然更省，但会让“长杆必须能被表示为突出”依赖于杆恰好沿某轴放。

**为什么只登记长物件**：合同给的是“32 个固定方向 + 最多 4 个长物件极值候选”，不是“每件吸附物都进壳”。小道具的角点最多比核心高几厘米，把它们全部塞进端点池会让池随吸附数量增长（PicnicLawn 一场可吸附数百件），每步扫描和吸附重算都会爆；长物件才是能真正顶起球、且 32 方向容易漏掉尖端的形状。准入用两条暂定规则（长 ≥ 0.5 球直径、长 ≥ 2.5 × 第二长边），所以杯子/方块/方片不占候选，勺/香蕉/棍/长凳会占。

### 上限与准入（硬性，序列化，PROVISIONAL）

| 字段 | 默认 | 语义 |
|---|---:|---|
| `maxProxyProtrusionPerCoreRadius` | 0.35 | 最大突出量 = 0.35 × 当前核心半径 |
| `maxProxyProtrusionWorldM` | 0.30 m | 绝对护栏（防大球的相对上限绝对值过大） |
| `maxProxyLiftPerStep` | 0.04 m | 代理引起的单步抬升上限；普通贴地仍用 `maxLiftPerStep = 0.1` |
| `maxProxyNormalSpeedPerturbation` | 0.15 m/s | 法向速度扰动上限；B 只有位置通道，requested 传 0，留给以后有法向速度时也不能超过 |
| `longObjectMinLongestEdgePerCoreDiameter` | 0.5 | 准入：最长边 ≥ 0.5 × 球直径 |
| `longObjectMinElongation` | 2.5 | 准入：最长边 ≥ 2.5 × 第二长边（方片不算长物件） |
| `minProxyProtrusionM` | 0.01 m | 吸附时最远角点不到 core+1 cm 就不登记 |

突出上限只在**每步生成接触点**时施加，不写回端点池：池里保留原始长度，所以球长大后 `l − r` 自然减小（合同第 4 条），而不会因为吸附时截断过而永远长不回来。

夹紧是**径向**的：每个端点的长度被缩到 `core + cap`，等价于把超出上限的杆截在“半径 core+cap 的球面”上，而不是只在正下方把 support 砍到 cap。这样任何方向的 support 都不会超过上限；代价是斜向下的杆抬升比“砍 support”更小（更保守），不会出现杆还斜着、球已经被顶到上限的观感。

### 镜头不跟着颠

跟随架读的是**未受凹凸影响的核心球心**：第 4 步用空代理数组再解一次同一个平面，把那个中心交给 `FollowRigMath`；这次调用的单步升降夹紧故意放到 10 m（它不是玩法移动），所以锚点正好是核心球心，不会出现“每次只追 0.1 m”的滞后抖动。球本身（`ballPhysicsRoot`）仍用带代理的高度。5 Hz 高度低通保留，但不再承担“防颠”的唯一责任。视线高度系数按核心半径算，突出量不会被当成球变大。

### 每步成本

- 每固定步（50 Hz）：夹紧 ≤32 个端点（≤32 次 sqrt + 缩放）、代理接地 ≤32 次点积、核心球心一次空数组调用（约 10 次运算）。与已吸附物件总数无关。
- 吸附一次：8 角点 + 受影响方向扫描（最坏 32×8 点积）+ 受影响方向支持重算（最坏 32 方向 × 32 端点）≈ 1k 次点积；移除同理。
- 整账本重建（32×32 点积）只在交分、传送、重试时发生。
- 运行期零分配（所有数组 `Start` 一次分配）。

## 测试

- Python 模型测试：`python3 -m unittest discover -s Docs/tests/ball_radial_proxy -v`（原有 10 例，未改）。
- C# EditMode：`Tests/EditMode/BallLayers/RadialProxyTests.cs`（原有 10 例，未改）与 `Tests/EditMode/BallLayers/RadialProxyShellTests.cs`（新增 9 例）：
  - `SingleLongRodLiftsSupportAndIsCapped`：单根 2 m 长杆朝下时 support = core+cap，单步修正夹到 0.04，原始支持表仍 > cap，远端方向不动。
  - `RemovingTheRodDropsTheSupportBackToTheCore`：移除后 support 回 core、修正 0、支持表全 0。
  - `SharedDirectionKeepsTheOtherRodsContribution`：两根同向棍，去掉长的后短的 0.13 m 接管，而不是残留 0.30 m。
  - `GrowingTheCoreRetractsTheRelativeProtrusionWithoutARebuild`：core 0.15 → 0.9 → 1.1，表不变、原始突出收回至 0。
  - `WholeLedgerRebuildMatchesIncrementalAndClearEmptiesIt`：增量与整账本重建逐方向一致；清空后全 0。
  - `FullCandidateListRefusesTheFifthItemWithoutChangingTheShell`：第 5 个候选被拒，旧壳逐位不变。
  - `LongObjectAdmissionUsesLengthAndSecondLongestEdge`：勺过、杯不过、短棍不过、扁板过、负尺寸拒绝。
  - `CornersFollowThePropsBallLocalRotation`：杆的角点随球局部旋转转向，零四元数拒绝。
  - `ContactPointsClampLongEndpointsAndZeroUnusedSlots`：夹紧到 core+cap、未用槽清零、长度不一致拒绝。

**没有跑 Unity 编译、EditMode/NUnit 或 Play Mode**。以上测试是源码级证据，不等于 Unity 证据。

## 单长棍 Lab（给 Unity 操作员；合同 Q3）

目的：回答 `Docs/08_SOURCES_AND_OPEN_QUESTIONS.md` Q3——“代理凹凸要不要进入物理，还是仅表现”。本层已经把“进入接地高度”接上，这个测试决定它是保留、调小还是关掉。

### 准备

1. 打开 `Labs/StoryLevel/StoryLevel.unity`，确认 `KatamariBallMotor` 的 `debugTraceProxySupport` 勾上（新增字段，默认关）。它每个固定步打一行 `[ProxyTrace] t=… support=… core=… lift=… cap=… points=… candidates=… centerY=…`，上限 400 行，足够 8 秒。`cap` 就是当步的 `min(0.35 × core, 0.30 m)`；也可以在 Play Mode 里用 `GetProxyProtrusionCapWorldM()` / `GetGroundSupportHeightWorldM()` / `GetGroundProxyLiftWorldM()` 读同一组值。
2. 选一件**细长**的现有道具。本场景的 `propBounds` 实测（`StoryLevel.unity` 的 `propBounds` 行）：
   - **`banana_01`–`banana_04` 是起始球上最合适的现有长物件**：bounds `(0.18, 0.032, 0.068)`，最长边 0.18 m、第二长边 0.068 m（2.65 ≥ 2.5），起始球（直径 0.30 m）的准入线是 0.15 m；体积 0.00039 m³ 也低于 0.00141 m³ 的吸收门。**首选它。**
   - `napkin_01`–`napkin_05`（bounds `(0.3, 0.028, 0.3)`）**不会**登记：最长边/第二长边 = 1，按“一个轴压过另外两个轴”的准入被排除。这正好验证规则不是只看“最薄的那一轴”。
   - `spoon_01` 的 `propBounds` 是 `0.34³` 的立方体（伸长比 1），**过不了伸长准入**；`thermos_01` 是 `0.9³` 立方体；`watering_can_01` 是 `(0.26, 0.42, 0.58)`（最长/第二长 = 1.38）——这三件都不会登记，正好可以拿来验证“非长物件不占候选”。
   - 如果香蕉的抬升太小（0.18 m 的道具在 0.30 m 球上，角点最多突出几厘米）：**临时**在草地上放一根测试杆——空 GameObject + Cylinder，`localScale` 设为 `(0.04, 0.5, 0.04)`（长 1.0 m），加 MeshRenderer；把它加进 `PickupFieldHost` 的平行数组一行：`itemId = prop_proxy_rod_01`、`kind = debris`、`propBounds = (0.04, 1.0, 0.04)`、`valueUnits = 1`，Transform 拖进去。**不要保存/提交这个场景改动**，它是实验件；测完删除该行和对象。
3. 确保球的 `pickupFieldHost` / `attachmentRoot` / `motor` 与现有 StoryLevel 接线一致（`attachmentRoot` 在 `BallVisualRollRoot` 下、局部 0、scale 1；宿主已经在 `AttachProp` 里调 `AttachRadialProxy`）。

### 步骤

1. 进 Play Mode（桌面即可），把球滚到长道具上吸住。Console 里应出现 `attached id=…`，随后 `[ProxyTrace]` 的 `points` 变成 8（香蕉/测试杆）或 0（未准入），`candidates` 变成 1。
2. 让长杆转到**朝下**（继续朝同一方向滚，杆会随球转）。记录：
   - `lift` 是否从 0 升到 >0；峰值是否 ≤ 同行的 `cap`（起始球约 0.0525 m；大球不超过 0.30 m）。注意 `lift` 是几何目标（`support − core`），不是本步实际位移；实际每步最多 0.04 m（`maxProxyLiftPerStep`），所以 `centerY` 会分几步追上 `support`。
   - `support` 与 `core` 的差是否在 2–4 个固定步内到顶（`maxProxyLiftPerStep = 0.04`，所以 5 cm 凸起大约 2 步）。
   - 球心 `centerY` 是否随 support 上升，而**镜头/座位没有同步上下抖**（截图对比：球与地平线的相对高度变化，座位画面应稳定）。
3. 继续滚过长杆方向，让杆转向侧面/上方：`lift` 应回落到 0，球心下降，且没有反向弹跳。
4. 触发“再试一次”（回合控制器的 retry）或手动调 `PickupFieldHost.ResetField()`：`points/candidates` 归 0，`lift` 归 0，下一次 `[ProxyTrace]` 的 `support == core`。
5. 若场景里能吃到更多道具让球长大：再记录一次同一根杆的 `lift`。原始 `support − core` 应随 core 增大而**减小**；`0.35 × core` 的 cap 可能先变大后碰到 0.30 m 绝对护栏。
6. 截图三张：杆在球顶（lift≈0）、杆朝下（lift 峰值）、杆转开后（lift=0）。每张同时截到球和地平线，用于判断镜头是否跟着颠。

### 判定与回填

- **保留物理凹凸**：lift 峰值可见（≥ 1–2 cm）、球不弹跳、镜头不抖、帧率无明显下降。
- **只当表现/调小**：如果球像被弹起、座位画面明显上下抖，先把 `maxProxyProtrusionPerCoreRadius` 降到 0.15–0.2、`maxProxyLiftPerStep` 降到 0.02 再测；仍不适则把 `maxProxyProtrusionPerCoreRadius = 0`（B 的接线保留，但接地退化回核心球）。
- **准入**：若 `points` 一直是 0，先看 `GetLastProxySkipReason()`（1 不是长物件、2 没突出、3 候选满、4 端点池满、5 输入非法、6 数学拒绝）；Console 里 `[BallMotor] radial proxy refused` 只在 3–6 时打，最多 3 行。
- 把 `[ProxyTrace]` 原始行、三张截图、球径/杆尺寸、帧率写进 `Docs/Evidence/`（按现有证据目录规则），并在 `Docs/08_SOURCES_AND_OPEN_QUESTIONS.md` Q3 上回填结论；本次 Linux 侧不改那份文档。

## 未决/风险

- **4 个候选是拒绝制**（合同模型）：第 5 件长物件不驱逐已有候选，只被跳过。若实机发现长物件经常超过 4 件，应先做候选选择策略（按突出量排序、显式驱逐），而不是偷偷扩容量。
- **AABB 是保守近似**：斜放扁板的角点可能比真实网格高；`propBounds` 对声明尺寸（`@size=`）的道具是立方体，长杆的声明尺寸会把它变粗。当前 `StoryLevel.unity` 的多数小道具 `propBounds` 本来就是立方体（勺 `0.34³`、保温杯 `0.9³`），所以它们的**视觉细长形状不在 `propBounds` 里**——本层按项目统一口径只用 `propBounds`，不会去读 Renderer；要让勺/叉产生凹凸，得先让它们的 `propBounds` 说真话（数据/编辑器侧，不在本次范围）。`banana_01–04` 是当前场景里能通过准入的现有行（起始球上）；更长的门挡长凳等整张 PicnicLawn 进场景后才会出现。
- **没有掉落路径**：吸附物是终态，所以“掉落”只有整场复位；接掉落时必须调 `DetachRadialProxy`，不能只移动 Transform。
- **C 阶段未做**：代理不参与 `SweepMath`、挡路盒、PVP 外形摘要，远端球也不广播它；水平碰撞仍用球形半径。
- **未测**：Unity 编译、U# 全量编译、EditMode/NUnit、Play Mode、帧率、VR 舒适度。32 方向/4 候选/三个上限/两条准入规则都是原型值。
