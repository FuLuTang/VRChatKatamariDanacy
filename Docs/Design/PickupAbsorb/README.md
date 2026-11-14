# T47 拾取、吸附与成长离线参考模型

> **DRAFT 草案 / UNVERIFIED**：标准库 Python 离线模型，用于讨论和测试性质；不是 Unity/Udon 实现，不冻结公共接口，不代表已验证的运行时协议或调校结果。

## 状态分栏

### 【已定】
- 共享地图坐标系用于碰撞、拾取和吞噬；成长账本与渲染网格分离。参见 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L6-L21) 与第3节。
- 同物实例以 `(roundId, spawnId, spawnGeneration)` 辨识；请求幂等、唯一裁决结果；客户端可预测但失败要回滚。远端装饰不能决定玩法碰撞。
- 远端仅依据确认的状态重建外观摘要；装饰模型、代理凹凸、径向样本都不得决定游戏碰撞或计分。
- 接触判定与候选生命周期已是运行时契约（下一节「接触判定与候选生命周期」），**本节 Python 模型不是该契约的权威**：reach 的实现、默认值和队列行为以 C# 为准。

### 【DRAFT 草案】
- `SpatialRegistry` 用均匀网格做包围盒候选索引；`nearby(position, radius)` 返回近邻候选，不等于最终接触判断。
- `Item` 记录位置、包围尺寸、类型、价值、可吸附标记及世代。`eligible` 再应用接触、尺寸、类型白名单与成长上限占位参数。
- `AuthorityLedger` 示意单一权威对有效请求去重并首个提交者获胜；相同请求重放返回同一结果。同一物件只在首次成功claim后贡献一次。实际权威 epoch、玩家会话世代、排序/时间边界还须接入正式协议。
- `Ball` 分开保存本地预测与确认收益。裁决拒绝/状态到达时用 `reconcile` 清空预测并以权威集合重建；快照也可直接恢复。成长按确认价值单调累加并由上限截断。
- 凹凸外形和径向代理目前只作视觉表示、不参与判定，是**设计主张**；是否可让其影响物理，须由 T29 真人门禁确认，未确认前采用球形判定。
- 代码仅依赖 Python 标准库；接口仅为此离线样例，**不冻结**。

### 【未决】
- 吸附接触/尺寸阈值、价值表及成长曲线；本模块规则对象及构造参数均由调用方显式传入，无权威默认调校。
- 世界尺度与坐标单位的实测；`world_m` 命名是占位约定，不代表标定完成。
- 两人同时够到时的公平阈值、是否接受先到裁决偏差；当前样例仅演示权威唯一裁决，不宣称公平。
- 凹凸是否参与物理（T29），须真人门禁；回合时长、结算/截止细则也未由本任务决定。
- 对应问题索引：[`Docs/08_SOURCES_AND_OPEN_QUESTIONS.md`](../../08_SOURCES_AND_OPEN_QUESTIONS.md)，尤其 Q1、Q3、Q5、Q8。参考规则矩阵亦将相关原作细节标为 unknown：[`Docs/Research/ReferenceRules/rules_matrix.md`](../../Research/ReferenceRules/rules_matrix.md#L13-L25)。

## 使用与边界

从仓库根运行 `python3 -m unittest discover -s Docs/tests`。吸附判定采用球半径 + 显式接触余量 + AABB 包围球半径的宽松近似；这只是离线候选模型。它不含遮挡、速度合理性、物件状态机、回合结算、网络传输或反作弊。权威状态应只计确认后的吸附集合；客户端装饰可丢弃并从权威快照重建。**不声称 VR、舒适性、Udon runtime、Unity 或网络实测通过。**

来源依据：[`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L32-L110)、[`Docs/01_GAME_DESIGN.md`](../../01_GAME_DESIGN.md#L13-L20)、[`Docs/Archive/Handoffs/Research_ReferenceRules.md`](../../Archive/Handoffs/Research_ReferenceRules.md)。

## Unity 实现

- 运行时：`Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs`（命名空间 `VRCKatamari.Pickup`）。EditMode 测试：`Assets/VRC_Katamari/Tests/EditMode/Pickup/PickupAbsorbTests.cs`。
- 只做纯值判定：`EvaluateAbsorb` 按模型的门控顺序返回“可吸附”或首个拒绝原因（不可吸附、类型不在白名单、最大全长超尺寸上限、球已达成长上限、超出接触距离）。接触判定的公式、各项含义、门序与排查方法见下一节，本节不再复述。
- 另外两个纯函数：`TryComputeGrowthRadius` 按 `min(上限, 基础半径 + 每单位成长 × 确认值)` 求半径（只接受确认值）；`TryAbsorbValueToLedgerGain` 把物件的 float 价值转成账本的整数 gain，两个草案都没定义取整规则，因此非整数/越界值直接拒绝而不是四舍五入。
- 未移植：`SpatialRegistry` 均匀网格广播结构（宿主侧容器，Udon 无 List/Dictionary）与 `Ball` 自己的 confirmed/predicted 字典（那会成为第二份账本；确认账本归 `GrowthLedgerMath`）。Python 的 5 个测试中 4 个已移植，网格那个未移植。
- 未验证：本环境没有 Unity/Udon 编译与 EditMode 运行，因此**不声称** Udon 编译、EditMode 测试、VR 或网络实测通过。

## 接触判定与候选生命周期

这一节存在的原因：`reach` 怎么算、`contactRadiusWorldM` 是占位值、空白名单意味着全拒、候选队列没有过期——这些此前只写在代码里。结果两次独立的现场调试（其中一次是 Lead）各自把 reach 算错，然后按“几何上应该吸到了”去改场景，而实际规则是另一条。以下每个结论都标了 `文件:行`。

**证据等级：** 全部来自代码阅读与手算，本检出没有 Unity/Udon 编译或运行，**未做运行时验证**。

引用对象（下文用 `文件:行` 指到具体行）：

- [PickupAbsorbMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs) — 纯值判定层，无状态、不分配。
- [KatamariBallMotor.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs) — 装配层，持有队列、默认值与拒绝环形缓冲。
- [SizeModelMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/SizeModel/SizeModelMath.cs) — 球的三个尺寸，其中只有碰撞半径进本层。

### 1. reach 公式

代码按如下顺序计算（`PickupAbsorbMath.cs:157`），比较在 `PickupAbsorbMath.cs:159`：

```
reach = collisionRadiusWorldM + contactRadiusWorldM + itemRadius
absorb ⇔ distance ≤ reach          // 距离取平方和再开方，中心到中心
```

`distance` 是球心到物件中心的三维欧氏距离（`PickupAbsorbMath.cs:139-147`），`itemRadius` 是物件的**包围球半径**（见第 2 节），三项全是米、全部相加、没有乘除、没有衰减。

| 项 | 代码 | 物理含义 | 谁喂它 |
|---|---|---|---|
| `collisionRadiusWorldM` | 参数 `:110`，加在 `:157` | 球当前**碰撞半径**（米），不是语义直径、不是核心半径 | 装配层传 `collisionRadiusWorldM` 字段（`KatamariBallMotor.cs:650`），该字段每步由 `SizeModelMath.TryDerive` 从确认体积重算（`KatamariBallMotor.cs:774`，公式在 `SizeModelMath.cs:179`：`collision = core × collisionToCoreRadiusRatio`） |
| `contactRadiusWorldM` | 参数 `:111`，加在 `:157` | 纯附加接触余量（米）。不是物件半径、不是容差、不随球长大 | 序列化字段，默认 `0f`（`KatamariBallMotor.cs:112`） |
| `itemRadius` | 局部量 `:155` | 物件包围球半径 √((x/2)²+(y/2)²+(z/2)²) | 由宿主 `SubmitCandidate` 传进来的 `bounds` 现算（`KatamariBallMotor.cs:927` 存、`:650` 传），本层从不读 Collider |
| `maxItemSizeWorldM` | 参数 `:112` | 尺寸门：物件**最大全长**的上限（米），**不参与 reach** | 序列化字段，默认 `1f`（`KatamariBallMotor.cs:113`） |
| `maxGrowthRadiusWorldM` | 参数 `:113` | 成长上限门：碰撞半径到这个值就整体拒收，**不参与 reach** | 装配层传 `maxCollisionRadiusWorldM`（`KatamariBallMotor.cs:651`），该值 = `maxCoreRadiusWorldM × collisionToCoreRadiusRatio`（`KatamariBallMotor.cs:411`），默认为 `1.1 × 1 = 1.1` |

后两项**不是** reach 的项，它们只在 reach 之前把整个候选拒掉；把它们加进 reach 是本轮被算错的那类错误。

**比较严格性**（照模型的严格性移植，`PickupAbsorbMath.cs:64-67`）：接触是 `≤`，距离恰好等于 reach 算吸附（测试 `PickupAbsorbTests.cs:88-93`：0.6 m 对 0.6 m reach 通过，0.6001 m 拒绝）；尺寸门是严格 `>`（恰等于上限放行，`PickupAbsorbMath.cs:135`）；成长上限门是 `≥`（恰等于上限就拒，`PickupAbsorbMath.cs:136`）。**没有加 epsilon**：和 `round_model.py:502` 的 `+1e-9` 不同，正好落在边界上的浮点值可能因末位误差翻面，写测试时不要卡在 1 ulp 上。

**序列化占位值。** 今天有两个值不是测量结果，而是占位：

- `contactRadiusWorldM = 0f`（`KatamariBallMotor.cs:112`）。0 不是“关掉这个功能”，它就是“球必须物理接触”：此时
  ```
  absorb ⇔ 中心距离 ≤ 球半径 + 物件包围球半径
  ```
  也就是说，没有“擦到一点就吸”的余量，也没有“靠近就吸”的手感窗口。要放宽手感，改的是 `contactRadiusWorldM`（它均匀地把 reach 放大这么多米），**不是**改球径、也不是改道具尺寸；反过来，把它留成 0 却抱怨“看着碰到了却不吸”，那是 reach 算错，不是场景错。
- `maxItemSizeWorldM = 1f`（`KatamariBallMotor.cs:113`）。这是**绝对**上限，不随球长大（`PickupAbsorbMath.cs:134-135` 只比常量），所以 1 m 以上的道具在任何球径下都永远吸不到。它与 `round_model.py:493-502` 的相对规则不一致，见「已知分歧」第 6 条。

**reach vs 物件尺寸（工作算例）。** 假设：球处于起始直径 0.30 m（确认体积 0 → 核心半径 = 碰撞半径 = 0.14987 m，由 `baselineVolumeUnits = 3.4390244`、`cubicMetersPerVolumeUnit = 0.0041` 经 `SizeModelMath.cs:137/171-173` 的 `V = 4πr³/3` 反解，`KatamariBallMotor.cs:105-106`；语义直径 0.2997 m）；`contactRadiusWorldM = 0`；物件是边长 `s` 的立方体，`bounds = (s,s,s)`。

| 边长 s (m) | itemRadius = √3·s/2 | **reach（contact=0，今天的行为）** | 面心方向真正接触的距离 = 球半径 + s/2 | 包围球多放行的量 | 参考：contact=0.10 时的 reach |
|---:|---:|---:|---:|---:|---:|
| 0.10 | 0.0866 | **0.236** | 0.200 | 0.037 | 0.336 |
| 0.25 | 0.2165 | **0.366** | 0.275 | 0.092 | 0.466 |
| 0.35 | 0.3031 | **0.453** | 0.325 | 0.128 | 0.553 |
| 0.50 | 0.4330 | **0.583** | 0.400 | 0.183 | 0.683 |

读法：`reach` 是**中心距阈值**。0.50 m 的方块在球心距 0.583 m 时就算吸附，而它的表面最近点在 0.400 m 处——包围球代理比真实接触早 0.183 m 放行（边长越大误差越大，见第 2 节）。所以“目测还差一点”时它已经能吸，这不是 bug；反方向的“看着贴上了却不吸”只会在宿主传的 `bounds` 比画面上的道具小时出现（见第 2 节末）。按视觉判断 reach 一定错。

### 2. `itemRadius` 是包围球半径，不是半边长、也不是整边长

`PickupAbsorbMath.cs:149-155`：

```
itemRadius = √( (bounds.x/2)² + (bounds.y/2)² + (bounds.z/2)² )
```

立方体代入即 `√3·s/2 ≈ 0.8660·s`。三个都不一样的量，别混：

| 说法 | 立方体值 | 是不是代码用的 |
|---|---:|---|
| 半边长 `s/2` | 0.5·s | 否 |
| 整边长 `s` | 1.0·s | 否 |
| 包围球半径 `√3·s/2` | 0.866·s | **是** |

原因：`bounds` 传的是物件的**全长**（AABB 三个方向的总尺寸），不是半长，`PickupAbsorbMath.cs:42-46` 明确写了“full axis-aligned extents, not half extents”，尺寸门也是拿全长比（`:134-135`）。对全长取包围球，就是把三个半长当分量求模——这正是 Python 模型 `pickup_absorb.py:91` 的 `sqrt(sum((s/2)**2 for s in item.bounds))`，以及 `:92` 的“bounds proxy permits contact when bounding radius overlaps the ball reach”。包围球是**能装下整个盒子的球**，所以除了角点方向，它都比真实接触更早放行；面心方向最松，误差正好 `(√3−1)/2·s ≈ 0.366·s`（表中“多放行的量”）。

装配层实际传什么：`SubmitCandidate` 把宿主给的 `bounds` 原样存下（`KatamariBallMotor.cs:927`）再原样喂给判定（`KatamariBallMotor.cs:650`）。实验室的宿主把它填成 `(semanticDiameterM, semanticDiameterM, semanticDiameterM)`（`PlaygroundProp.cs:110`），即立方体近似，**不是** Collider 尺寸。结论：**把道具 Collider 改小不会缩小 reach**，除非同时把 `bounds` 改小；反过来，`bounds` 填成大立方体，reach 与尺寸门同时变松/变紧，而画面上看不出任何变化。

### 3. 门序：首个失败者胜（枚举值不是门序）

`EvaluateAbsorb` 的检查顺序（`PickupAbsorbMath.cs:115-159`）。**注意：`AbsorbOutcome` 的编号（`:18-27`，`Absorb=0` 到 `RejectInvalidInput=6`）只是枚举值，不是执行顺序**——`RejectInvalidInput` 编号最大却最先执行。

| 序 | 判定 | 输入/触发 | 返回 |
|---:|---|---|---|
| 1 | 输入守卫（非模型门，端口自加） | `itemKind == null` 或 `allowedKinds == null`（`:115`）；`allowedKindCount < 0` 或 `> allowedKinds.Length`（`:116-117`）；`itemBounds`/`itemPosition`/`ballPosition` 含非有限分量（`:118-120`）；四个标量参数非有限（`:121-125`）；`itemBounds` 任一分量为负（`:126-127`）；`collisionRadiusWorldM < 0`（`:128`）；以及计算中途 dx/dy/dz、distanceSquared、itemRadiusSquared、reach 非有限（`:142-146`、`:153-154`、`:158`） | `RejectInvalidInput` (6) |
| 2 | 可吸附标记 | `itemAbsorbable == false` | `RejectNotAbsorbable` (1) |
| 3 | 种类白名单 | `IsKindAllowed` 走完前 `allowedKindCount` 项都没按序数匹配上（`:131-132`，实现 `:232-240`） | `RejectKindNotAllowed` (2) |
| 4 | 尺寸门 | `max(x,y,z) of itemBounds > maxItemSizeWorldM`（严格大于，`:134-135`） | `RejectItemTooLarge` (3) |
| 5 | 成长上限门 | `collisionRadiusWorldM ≥ maxGrowthRadiusWorldM`（含等于，`:136-137`） | `RejectBallAtGrowthCap` (4) |
| 6 | 距离 | 计算中心距（`:139-147`）与包围球半径（`:149-155`） | 仅用于下一步 |
| 7 | 接触 | `distance > reach` 则拒，否则吸（`:157-159`） | `RejectOutOfReach` (5) / `Absorb` (0) |

**早门遮蔽后门**——这是排查时最容易白跑的地方：

- 白名单为空或拼错时，每个候选都停在第 3 门，第 6/7 门的距离与 reach **根本没有算**（虽然装配层为了记拒绝事件另算了一次距离，见第 7 节），所以“距离看着在 reach 内”不能作为门序正常的证据。
- `absorbable=false` 的物件在第 2 门就出局，它的种类错误、尺寸超限都看不到。
- 大于 `maxItemSizeWorldM` 的物件停在第 4 门，不会告诉你它同时也够不着（对称地：球到成长上限后，所有候选都停在成长上限门，与几何无关）。
- 一个 `bounds` 为负或位置含 NaN 的候选停在第 1 门，会把同类里其他候选的问题一起盖住。

所以排查必须**一次只修一门、改完重读拒绝原因**，不能一次改三个参数。

### 4. 空白名单 = 全部拒绝（fail-closed）

- `IsKindAllowed` 从 0 遍历到 `allowedKindCount`，一个都没匹配就返回 `false`（`PickupAbsorbMath.cs:232-240`）——**0 项的意思是“没有任何种类被允许”，不是“不过滤”**。
- `Start()` 把它初始化成 `new string[0]` + `allowedKindCount = 0`（`KatamariBallMotor.cs:399-400`）。装配层内部从不调用 `SetAllowedKinds`；全仓唯一的调用点是宿主（实验室里是 `BallPlaygroundBootstrap.cs:175`）。
- **陷阱：`Start()` 会无条件把它重置成空**（`:399-400`）。所以在 `Awake`/`OnEnable` 或另一个脚本的 `Start` 里先设好，随后本层 `Start` 一跑就被抹掉——静默回到全拒。正确时机是“本层 `Start` 之后、第一个固定步之前”（实验室宿主在第一次 `Update` 里做，`BallPlaygroundBootstrap.cs:167-175`），并且**读回** `GetAllowedKindCount()` 确认（`:1577-1580`；实验室在 `:179-184` 读回并在 0 时 `LogError`）。
- `SetAllowedKinds` 只挡 `null` 和越界 count（`KatamariBallMotor.cs:890-896`）；它保存的是调用方数组的**引用**，不是拷贝。宿主之后复用或改这个数组，白名单会跟着静默改变。实验室为此特意传序列化数组而不是运行时新建的 `string[]`（`BallPlaygroundBootstrap.cs:169-172`，注释说运行时新建数组在这个边界上会触发 `__SetProgramVariable__` 空引用）。
- 匹配是**序数比较**（`string.CompareOrdinal`，`PickupAbsorbMath.cs:237`）：`"Stone"` 不等于 `"stone"`，首尾空格也算不同。数组里的 `null` 项被跳过（`:236`），不报错。
- 忘设时的运行表现：**吸收数为 0，没有异常、没有报错**；控制台上最多只有 3 行拒绝诊断（`KatamariBallMotor.cs:294`、`:1464`），队列照样被候选填满，看上去就像“这一关没有可吸的东西”。唯一的客观线索是拒绝环形缓冲里每条都是 reason = 2（第 7 节）。

### 5. 候选生命周期：进队即每步重判，只有三条出口

装配层持有一个固定 4 槽的候选队列（`PendingCandidateCapacity = 4`，`KatamariBallMotor.cs:257`）。状态转移只有这些：

| 时机 | 发生什么 | 代码 |
|---|---|---|
| 宿主 `SubmitCandidate(...)` | 入队；存下**当时**的 `bounds`、`position`、`valueUnits`、`id`、`generation` | `:914-935`（写入 `:924-933`） |
| 队列已满（4 条） | 该次提交**被丢弃**并 `false`，同时点亮溢出信号 | `:917-921`（`SignalOverflow("pending candidate capacity 4 exceeded")`） |
| 每个固定步、阶段 5 | 对**在队的每一条**重新跑一次 `EvaluateAbsorb`，判定用的球心是贴地后的最终位置 | `:580`、`:643-663` |
| 判定为拒 | 记一条拒绝事件（原因 + 中心距），候选**留在队里** | `:656-661` |
| 判定为吸 | 走账本提交；只有提交成功才真正移除 | `:673-716` |
| 账本提交失败 | 候选**留在队里**，下一帧重新判定（包括再次通过判定再失败） | `:669-670`、`:702-713` |
| 宿主 `WithdrawCandidate(id)` | 按 id 删除（就地压缩），可再提交 | `:953-982` |
| 宿主 `ClearCandidates()` | 全清，不触碰账本 | `:984-987` |

关键点，逐条：

1. **位置是入队快照，不会刷新**。`SubmitCandidate` 存的是传入的 `worldPosition`（`:928`），判定每步都用这个旧值（`:650`）。所以球一旦离开，候选的距离只会越算越大——宿主必须在 reach 内提交，并在离开时撤回，这不是优化建议而是契约（第 6 节）。
2. **每步都重判**。`pendingAccepted[i]` 每步先清零（`:647`），上一帧的结论不复用。好处是物件被推开又推回来能自动恢复；坏处是每条在队候选每步都产生一条拒绝事件：4 条在队 + 8 槽环（`:292`），**两次固定步就把环写满，从第三步起开始淘汰**，`GetRejectEventDroppedCount()`（`:1519-1523`）随之持续增长。看拒绝原因要看最新的几条，不是最早的。
3. **没有 TTL、没有过期、没有老化**。代码里没有任何时间或帧数上限与候选相关；唯一的移除条件是“提交成功”、“宿主撤回”、“宿主全清”。因此**系统性拒绝会把队列永久冻在 4 槽**：白名单错（第 3 门）、尺寸上限太小（第 4 门）、球到成长上限（第 5 门）、`valueUnits` 非整数或账本不在 Playing（提交失败）——任何一种都会让队列再也进不来更近的物件。
4. **这条冻结是实测过的症状**：候选按“离球近”就入队、球随后滚走，于是 4 个远处的道具把队列占满，每步都以“超出 reach”被拒，中心距随球离开单调增大，吸收数始终为 0，任何更近的道具都进不来。记录在 `KatamariBallMotor.cs:941-946` 与 `PlaygroundProp.cs:96-102`（那里的修法是：只在激活半径内提交 `:104-113`，离开释放半径就撤回 `:124-129`）。**逐帧数值没有入库的运行日志**，我找不到可引用的数字记录，所以这里只记机制与症状定性；引用这段时不要编造具体距离。
5. **重复提交同一个 id 没有去重**。第二次提交照样入队（`:914-935` 不查重），但该事件一旦提交过，账本会以“键已提交”拒绝（`GrowthLedgerMath.cs:440-444`），于是这条候选永远提交失败、永远占着槽——比“白占槽”更隐蔽，因为拒绝环里一条记录都不会有（判定是通过的）。
6. **队满时丢弃会点亮溢出信号**（`:917-921`），这是数据错误信号而不是玩法规则：宿主每次提交前应读 `GetPendingCandidateCount()` / `GetPendingCandidateCapacity()`（`:1313`、`:1326`）。

### 6. 宿主欠本层什么

按上面的机制整理成契约清单，任何一条不满足都不会报错，只会表现为“吸不到”：

1. **在本层 `Start()` 之后、第一个固定步之前**调 `SetAllowedKinds(...)`，并读回 `GetAllowedKindCount() > 0`。传序列化数组，不要传运行时新建的数组；不要让本层持有后又被改写的数组。
2. **只在 reach 内提交**。判定用入队时的位置快照，不刷新；先算 `碰撞半径 + contactRadius + itemRadius`（可用 `GetCollisionRadiusWorldM()`，`KatamariBallMotor.cs:1243`），再决定提交。
3. **物件离开就 `WithdrawCandidate(id)`**；关卡换段落或重开就 `ClearCandidates()`。没有 TTL 替你兜底。
4. **同一个 `itemInstanceId` 只提交一次**。提交成功后它进入账本；再提一次会永久卡住一个槽（`GrowthLedgerMath.cs:440-444`）。
5. **`valueUnits` 必须是整数**。`TryAbsorbValueToLedgerGain` 对小数、负数、非有限值、超出 int32 的值一律返回 `false`（`PickupAbsorbMath.cs:218-230`），两个模型都没定义取整规则，所以它不四舍五入。非整数价值的后果不是“少加一点”，而是这件道具永远提交不了、永久占槽。
6. **障碍注册与吸收判定无关**。`AddPlane` / `AddBox` 喂的是 `SweepMath`（`KatamariBallMotor.cs:989-1028`），本层的接触判定没有遮挡项（`PickupAbsorbMath.cs:62-64` 明说“models no occlusion”）。所以隔着一面已注册的墙，只要中心距够近，照样能吸——想让墙挡住吸收，本层没有这个能力，只能在宿主侧不提交。
7. **账本必须已在 `Playing`**：`TrySubmitClaim` 的阶段门在 `GrowthLedgerMath.cs:435`。否则每个候选都判定为“可吸”却提交失败，表现为“零拒绝、零吸收、队列满”（第 7 节用它区分账本问题）。
8. **候选收集、空间索引、道具生命周期不在本层**：本层不扫场景，只判定宿主递进来的一条。

### 7. 静默零吸收的排查顺序

按代价从低到高，每一步都能给出“该改配置还是该改几何”的结论：

1. `GetAbsorbedCount()`（`KatamariBallMotor.cs:1273`）与 `GetRejectEventCount()`（`:1508`）。两者都是 0 且队列空 → 宿主根本没提交（收集器没接上），别调几何。
2. `GetAllowedKindCount()`（`:1577`）。为 0 → 配置错误：白名单没设、被 `Start()` 重置、或计数没传对。这是最常见的“零吸收”。
3. **拒绝数为 0 但队列停在 4、吸收数不涨** → 不是几何问题：判定通过了，但提交没成功。三个无拒绝记录的成因：账本阶段不是 `Playing`（`GrowthLedgerMath.cs:435`）、键已提交过（`:440-444`，重复 id）、或 `valueUnits` 非整数（`PickupAbsorbMath.cs:218-230`）；另有 `hasLocalRow == false` 时整段提交直接跳过（`KatamariBallMotor.cs:675`）。先看 `TryBeginPlaying` 的返回值（实验室在 `BallPlaygroundBootstrap.cs:200-207`，注释里记着 0 时间戳会被拒、症状正是“四个槽永不排空”）。
4. 拒绝数不为 0 时，用最新几条读原因与距离：`GetRejectEventItemId(i)`（`:1526`）、`GetRejectEventReason(i)`（`:1539`）、`GetRejectEventDistance(i)`（`:1547`）；索引是**绝对索引**，已淘汰的返回 `""`/`-1`（`:1554-1564`）。原因码对照 `AbsorbOutcome`（`PickupAbsorbMath.cs:18-27`）：

| 原因 | 枚举 | 指向 | 下一步 |
|---:|---|---|---|
| 1 | `RejectNotAbsorbable` | 数据：宿主把 `absorbable` 传成 false | 查道具数据/标记 |
| 2 | `RejectKindNotAllowed` | **配置**：白名单为空、拼错、大小写/空格（序数比较） | 读回 `GetAllowedKindCount()`，核对 `kind` 字符串 |
| 3 | `RejectItemTooLarge` | **配置或数据**：`maxItemSizeWorldM`（默认 1 m，绝对值，不随球长大）或宿主传的 `bounds` 比以为的大（它是全长；把 Collider 尺寸或半径当全长传就会误判） | 打印 `bounds`，与 `maxItemSizeWorldM` 比最长边 |
| 4 | `RejectBallAtGrowthCap` | **配置**：球已到 `maxCoreRadiusWorldM × collisionToCoreRadiusRatio`（默认 1.1 m），此后一律不收 | 看 `GetCollisionRadiusWorldM()`；这是尺寸上限，不是几何 |
| 5 | `RejectOutOfReach` | **几何**：中心距 > reach | 用 `GetRejectEventDistance()` 与手算的 `碰撞半径 + contactR + itemRadius` 比；`GetCollisionRadiusWorldM()` 给第一项，`bounds` 给第三项。差得多是位置/尺寸错，差一点点是包围球算错或 `contactRadiusWorldM` 与预期不符 |
| 6 | `RejectInvalidInput` | **配置或数据**：`kind` 为 null、`bounds` 有负分量、位置含 NaN/Inf（未接线的 Transform 会直接给 NaN）、位置或尺寸溢出、count 大于数组长度 | 打印原始输入，先找 NaN |

5. 距离**单调增大**且队列停在 4 → 就是第 5 节的冻结：宿主没有撤回。不是 reach 变紧。
6. 只看控制台不够：拒绝诊断日志全程只有 3 行（`KatamariBallMotor.cs:294`、`:1464-1485`，每次打印 `d`、`reach`、`itemR`、`ballR`、`contactR`、`allowedKindCount`），用完之后就不再输出。持续通道是拒绝环（8 槽，`:292`）加 `GetRejectEventDroppedCount()`（`:1519-1523`）。

### 与 `pickup_absorb.py` 的已知分歧

不是笔误，是两边的定位不同；记录在这里以免下次拿模型当依据。

| # | 项 | 代码 | 模型 | 影响 |
|---|---|---|---|---|
| 1 | 参数默认值 | `contactRadiusWorldM = 0f`、`maxItemSizeWorldM = 1f` 序列化默认（`KatamariBallMotor.cs:112-113`） | 无默认，构造 `PickupRules` 必须显式给（`pickup_absorb.py:74-78`，注释写明“deliberately no accepted/default gameplay tuning”） | 模型强迫调用方表态，运行时替它表态了。0 不是无操作，它把玩法固定成“必须接触” |
| 2 | 门粒度 | 不可吸附（`:130`）与种类（`:131-132`）是两个 outcome | 合并成一个 `if`（`pickup_absorb.py:83`） | 顺序一致，但模型无法区分这两个原因；“首个失败门即报告原因”对这一条在模型里不成立 |
| 3 | 非法输入 | 负 `bounds`、非有限值、null kind 一律先拒（`PickupAbsorbMath.cs:115-128`） | `eligible` 不检查；只有 `register` 检查 value/bounds（`pickup_absorb.py:42-43`） | 端口自加的守卫，代码注释 `PickupAbsorbMath.cs:13-16` 已声明；同一份输入两侧结论可能不同（模型放行、代码拒） |
| 4 | 候选生命周期 | 4 槽队列、每步重判、submit/withdraw/clear、无 TTL（第 5 节） | 完全没有对应物：`nearby()` 只返回候选（`pickup_absorb.py:59-69`） | 第 5 节的规则**没有参考模型**，属实现侧契约，不能拿模型反驳 |
| 5 | 半径口径 | reach 用 `collisionRadius = core × collisionToCoreRadiusRatio`（`SizeModelMath.cs:179`） | 只有一个 `radius = min(max, base + growth × confirmed)`（`pickup_absorb.py:141-142`），没有碰撞/核心比 | 默认 ratio = 1 时两侧数值一致；ratio ≠ 1 时模型没有对应项 |
| 6 | 尺寸门（跨层） | 绝对最大全长：`max(bounds) > maxItemSizeWorldM`（`PickupAbsorbMath.cs:134-135`） | 回合模型是相对规则：`pickup_diameter ≤ ball_diameter × absorb_size_ratio`（`round_model.py:493-502`，默认 ratio 1.0，`:119`） | **不是同一条规则**：球长大后回合模型放行更多道具，本层永远卡在 1 m。`BallMotor/README.md` 缺口 3 也记着这条 |

### 仍然会坏的地方（读代码看到、未修）

1. **`contactRadiusWorldM = 0` 是承重的占位值**：它是“能不能吸到”的全部额外余量，改它直接等于改手感，而没有任何一处要求关卡必须给值。它和 `maxItemSizeWorldM = 1` 一起被 `BallMotor/README.md` 列为占位，但这里才是它们真正起作用的位置。
2. **`maxItemSizeWorldM` 绝对值 + 不随球长大**，与 `round_model.py:493-502` 的相对规则冲突（上表第 6 条）。所有 > 1 m 的道具在球满级（碰撞半径 1.1 m）时仍然永远吸不到。
3. **`bounds` 由宿主手填、与 Collider 无关联**：改 Collider 不改 reach；实验室把 `semanticDiameterM` 当三边填（`PlaygroundProp.cs:110`），那是立方体近似。
4. **4 槽 + 无 TTL**：任何系统性拒绝都会永久冻结队列，而信号只有“拒绝环”（几何类）或**什么都没有**（账本类）。
5. ~~**重复 id 无去重**~~ **已修（2026-11-14）**：`SubmitCandidate` 现在按 item id 幂等，重复提交返回 `true` 不占新槽；宿主因此不再需要本地闩锁。
6. **`SetAllowedKinds` 存引用不拷贝**（`KatamariBallMotor.cs:894`），且被 `Start()` 无条件重置（`:399-400`）——两个静默失效源。
7. **成长上限门的边界**：门用 `≥`（`PickupAbsorbMath.cs:136`），而 `SizeModelMath` 在“确认体积恰好等于上限体积”时走 `exp` 路径（`SizeModelMath.cs:165` 的 `logTotal > logCappedVolume` 是严格大于），理论上可能返回比上限小 1 ulp 的半径，那样满级球还能再吸一件。**这是读代码的推断，未实测。**
8. **`WithdrawCandidate` 用 `==` 比较字符串**（`KatamariBallMotor.cs:962`）。C# 语义是按值比较，UdonSharp 下是否同样按值**未验证**；若不按值，撤回会静默失效，退化成第 5 节的冻结。

**整数单位契约（有意为之，不是缺陷）**：`TryAbsorbValueToLedgerGain` 只接受整数单位（`(float)(int)v != v` 即返回 `false`）。小数值视为作者数据错误，由 `RejectNoGain` 大声拒绝并进入拒绝环/日志，**不要去"修"成四舍五入**——取舍是"数据必须整数"，让错误可见。
