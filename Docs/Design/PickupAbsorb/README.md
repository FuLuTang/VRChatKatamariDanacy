# T47 拾取、吸附与成长离线参考模型

> **DRAFT 草案 / UNVERIFIED**：标准库 Python 离线模型，用于讨论和测试性质；不是 Unity/Udon 实现，不冻结公共接口，不代表已验证的运行时协议或调校结果。

## 状态分栏

### 【已定】
- 共享地图坐标系用于碰撞、拾取和吞噬；成长账本与渲染网格分离。参见 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L6-L21) 与第3节。
- 同物实例以 `(roundId, spawnId, spawnGeneration)` 辨识；请求幂等、唯一裁决结果；客户端可预测但失败要回滚。远端装饰不能决定玩法碰撞。
- 远端仅依据确认的状态重建外观摘要；装饰模型、代理凹凸、径向样本都不得决定游戏碰撞或计分。
- 接触判定与候选生命周期已是运行时契约（下一节「接触判定与候选生命周期」），**本节 Python 模型不是该契约的权威**：接触判定的实现、默认值和队列行为以 C# 为准。
- **吸收尺寸门是原作的整 mm 体积口径**（owner 2026-11-16，取代 2026-11-15 的 (l) 初版）：`V = k·a·b·c`，`Req_mm = floor(1000·cbrt(60V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`。契约在 1.1 节（含 2026-11-16 的更正块），实现只在 `PickupAbsorbMath` 的 `TryComputeOriginalItemVolume` / `TryFloorMillimetres` / `TryComputePickupReqDiameterMm` / `IsBeyondAbsorbLimit` 四个函数里；`absorbVolumeRatio` / `maxItemVolumeWorldM3` 两个序列化字段与更早的 `absorbSizeRatio` / `maxItemSizeWorldM` 都已删除。实测前后对比见 [`ABSORB_GATE_ORIGINAL.md`](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。

### 【DRAFT 草案】
- `SpatialRegistry` 用均匀网格做包围盒候选索引；`nearby(position, radius)` 返回近邻候选，不等于最终接触判断。
- `Item` 记录位置、包围尺寸、类型、价值、可吸附标记及世代。`eligible` 再应用接触、尺寸、类型白名单与成长上限占位参数。
- `AuthorityLedger` 示意单一权威对有效请求去重并首个提交者获胜；相同请求重放返回同一结果。同一物件只在首次成功claim后贡献一次。实际权威 epoch、玩家会话世代、排序/时间边界还须接入正式协议。
- `Ball` 分开保存本地预测与确认收益。裁决拒绝/状态到达时用 `reconcile` 清空预测并以权威集合重建；快照也可直接恢复。成长按确认价值单调累加并由上限截断。
- 凹凸外形和径向代理目前只作视觉表示、不参与判定，是**设计主张**；是否可让其影响物理，须由 T29 真人门禁确认，未确认前采用球形判定。
- 代码仅依赖 Python 标准库；接口仅为此离线样例，**不冻结**。

### 【未决】
- 吸附**接触余量**阈值、价值表及成长曲线；本模块的规则对象及构造参数均由调用方显式传入，无权威默认调校。（**尺寸阈值不再是未决项**：决定 (l) 把它定为体积口径 `a·b·c ≤ (π/6)·D³/10`，见 1.1 节；这里说的是 Python 模型仍要求调用方显式传参。）
- 世界尺度与坐标单位的实测；`world_m` 命名是占位约定，不代表标定完成。
- 两人同时够到时的公平阈值、是否接受先到裁决偏差；当前样例仅演示权威唯一裁决，不宣称公平。
- 凹凸是否参与物理（T29），须真人门禁；回合时长、结算/截止细则也未由本任务决定。
- 对应问题索引：[`Docs/08_SOURCES_AND_OPEN_QUESTIONS.md`](../../08_SOURCES_AND_OPEN_QUESTIONS.md)，尤其 Q1、Q3、Q5、Q8。参考规则矩阵亦将相关原作细节标为 unknown：[`Docs/Research/ReferenceRules/rules_matrix.md`](../../Research/ReferenceRules/rules_matrix.md#L13-L25)。

## 使用与边界

从仓库根运行 `python3 -m unittest discover -s Docs/tests`。离线 Python 模型的吸附判定仍是球半径 + 显式接触余量 + AABB 包围球半径的宽松近似，运行时（C#）已改为道具有向盒到球心的最短距离，见下一节；这只是离线候选模型。它不含遮挡、速度合理性、物件状态机、回合结算、网络传输或反作弊。权威状态应只计确认后的吸附集合；客户端装饰可丢弃并从权威快照重建。**不声称 VR、舒适性、Udon runtime、Unity 或网络实测通过。**

来源依据：[`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L32-L110)、[`Docs/01_GAME_DESIGN.md`](../../01_GAME_DESIGN.md#L13-L20)、[`Docs/Archive/Handoffs/Research_ReferenceRules.md`](../../Archive/Handoffs/Research_ReferenceRules.md)。

## Unity 实现

- 运行时：`Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs`（命名空间 `VRCKatamari.Pickup`）。EditMode 测试：`Assets/VRC_Katamari/Tests/EditMode/Pickup/PickupAbsorbTests.cs`、`RejectNoGainTests.cs`、`AbsorbSizeGateTests.cs`（体积门：0.05/0.30 的 216 倍、棒球棒、上限与非法值）与 `Tests/EditMode/BallMotor/AbsorbSizeLimitTests.cs`（装配层的默认值与上限读数）。
- 只做纯值判定：`EvaluateAbsorb` 按模型的门控顺序返回“可吸附”或首个拒绝原因（不可吸附、类型不在白名单、AABB 体积超上限、球已达成长上限、超出接触距离）。**体积上限的合成**在 `TryComputeAbsorbVolumeLimit`（球体积 `(π/6)·D³` ÷ `absorbVolumeRatio`，再由 `maxItemVolumeWorldM3` 兜底），物件的体积在 `TryComputeItemVolume`，比较只有 `IsVolumeBeyondLimit` 一处；带值的相对入口是 `EvaluateAbsorbWithValueAndBallSize`，装配层每步都走它（决定 (l)，见 1.1 节）。接触判定的公式、各项含义、门序与排查方法见下一节，本节不再复述。
- 另外两个纯函数：`TryComputeGrowthRadius` 按 `min(上限, 基础半径 + 每单位成长 × 确认值)` 求半径（只接受确认值）；`TryAbsorbValueToLedgerGain` 把物件的 float 价值转成账本的整数 gain，两个草案都没定义取整规则，因此非整数/越界值直接拒绝而不是四舍五入。
- 未移植：`SpatialRegistry` 均匀网格广播结构（宿主侧容器，Udon 无 List/Dictionary）与 `Ball` 自己的 confirmed/predicted 字典（那会成为第二份账本；确认账本归 `GrowthLedgerMath`）。Python 的 5 个测试中 4 个已移植，网格那个未移植。
- 未验证：本环境没有 Unity/Udon 编译与 EditMode 运行，因此**不声称** Udon 编译、EditMode 测试、VR 或网络实测通过。

## 接触判定与候选生命周期

这一节存在的原因：接触怎么算、`contactRadiusWorldM` 是占位值、空白名单意味着全拒、候选队列没有过期——这些此前只写在代码里。结果两次独立的现场调试（其中一次是 Lead）各自把 reach 算错，然后按“几何上应该吸到了”去改场景，而实际规则是另一条。以下每个结论都标了 `文件:行`。

**证据等级：** 全部来自代码阅读与手算，本检出没有 Unity/Udon 编译或运行，**未做运行时验证**。

引用对象（下文用 `文件:行` 指到具体行）：

- [PickupAbsorbMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs) — 纯值判定层，无状态、不分配。
- [KatamariBallMotor.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs) — 装配层，持有队列、默认值与拒绝环形缓冲。
- [SizeModelMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/SizeModel/SizeModelMath.cs) — 球的三个尺寸，其中只有碰撞半径进本层。

**行号状态（2026-11-15，决定 (l) 落地时）：** 本节 `PickupAbsorbMath.cs:…` 的行号是在 (h) 落地时逐行核对过的，**体积口径改造后又漂了一次**（文件头与尺寸门相关段落重写过）：引用前请按**符号名**检索确认（`TryComputeItemVolume`、`IsVolumeBeyondLimit`、`TryComputeAbsorbVolumeLimit`、`IsBeyondAbsorbVolumeLimit`、`TryComputeContact`），不要照抄本节任何 `:行号`。`KatamariBallMotor.cs:…` 同样只核对了字段与判定调用两处。这不是新问题：行号在本仓已经漂过至少三次。

### 1. 接触公式：道具的有向盒碰到球

原作的触发是「物件的碰撞模型碰到球的碰撞体」（专利 US7402104B2，见 [`ORIGINAL_ATTACH_LOOK.md`](../../Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md)），所以接触量的是**盒面到球**，不是道具外接球到球。代码（`PickupAbsorbMath.TryComputeContact`，判定在 `EvaluateAbsorb`）：

```
boxDistance  = 球心到「道具有向盒」的最短距离（盒 = 自身尺寸 itemSize，放在 itemPosition，转 itemRotation；球心在盒内为 0）
contactLimit = collisionRadiusWorldM + contactRadiusWorldM
absorb ⇔ boxDistance ≤ contactLimit
```

`boxDistance` 在盒坐标系里算：把球心用逆旋转带进盒的局部系，三个轴各取 `max(|p_i| − size_i/2, 0)`，再求模（面区只有一个轴在外、棱区两个、角区三个，一个公式）。零边是合法的扁盒/线/点；非单位四元数先归一化，零四元数、NaN、非有限位置/尺寸、负尺寸一律 `RejectInvalidInput`。纯 float 运算，不分配，无 `Quaternion` 运算符，Udon 与离线测试跑同一份代码。盒按**以道具位置为中心**处理（预制体根在模型中心；同步只量尺寸，不量盒心偏移）。

接触比较是 `≤`：盒面恰好碰到球面（或接触余量边）算吸附。没有外接球、没有 `itemRadius`：细长/扁平件从侧面逼近时，盒面贴到球面才触发，不再在更远处的外接球壳上触发；沿长轴逼近时它的端面本来就更远，所以也自然更早触发。

| 项 | 代码 | 物理含义 | 谁喂它 |
|---|---|---|---|
| `itemSize` | 参数 | 物件**自身**三边（全长，米），与朝向无关 | 宿主 `propOwnSizes`（`propLocalBounds`），见 1.1 节 |
| `itemRotation`、`itemPosition` | 参数 | 物件入队时的世界旋转与位置（**入队快照，不刷新**） | 宿主 `SubmitProp` 传 `propTransforms[i].rotation/position`，随候选存进电机队列（`pendingRotations`） |
| `collisionRadiusWorldM` | 参数 | 球当前**碰撞半径**（米），不是语义直径、不是核心半径 | 装配层在判定调用处传 `collisionRadiusWorldM`（`KatamariBallMotor.cs:749-753`），该字段每步由 `PickupAbsorbStepMath.RecomputeConfirmedAndSize` → `DeriveSize` → `SizeModelMath.TryDerive` 从确认体积重算（`collision = core × collisionToCoreRadiusRatio`） |
| `contactRadiusWorldM` | 参数 | 纯附加接触余量（米）。不是物件半径、不是容差、不随球长大 | 序列化字段，默认 `0f`（`KatamariBallMotor.cs:112`） |
| 尺寸门的物件侧（参数 `itemSize`、`itemVolumeK`） | `EvaluateAbsorb` 的参数，用在尺寸门 | `itemSize` 同上（原作每类型的 `a, b, c`）；`itemVolumeK` 是原作的每件体积系数 `k`（`V = k·a·b·c`），**不参与接触** | 序列化列 `PickupFieldHost.propVolumeK`（导入器从 `defs[].volumeK` 填，缺省 0.70）与 `propLocalBounds`（物件自身尺寸），随候选 `SubmitCandidateWithVolumeKMm3` 进电机；比较在 `IsBeyondAbsorbLimit`（见 1.1 节） |
| `maxGrowthRadiusWorldM` | 参数 | 成长上限门：碰撞半径到这个值就整体拒收，**不参与接触** | 装配层传 `maxCollisionRadiusWorldM`（`KatamariBallMotor.cs:752`），该值 = `maxCoreRadiusWorldM × collisionToCoreRadiusRatio`（`KatamariBallMotor.cs:485`），默认为 `1.1 × 1 = 1.1` |

尺寸门与成长上限门**不是**接触的项，它们只在接触之前把整个候选拒掉；把它们加进接触极限是“球够不够远”那类算错，把它们当同一件事（尺寸门 = reach 门）是另一类算错：尺寸门问的是“球够不够大”，与距离无关。

### 1.1 尺寸门是体积门（决定 (l) 2026-11-15；**2026-11-16 更正为原作的整 mm 口径**）

> **2026-11-16 更正（owner：门按原作数字）。** 下面 2026-11-15 的式子少了**每件物体的体积系数 k**、也**没有把门槛向下取整到整 mm**，还把 `maxItemVolumeWorldM3 = 512` 当成与球耦合的硬顶。现行规则（唯一实现）是：
>
> ```
> V_item = k · a·b·c                     # k = defs[].volumeK / props[].volumeK，缺省 0.70
> Req_mm = floor(1000 · cbrt(60 · V_item / π))        # 球体积 ≥ 10 × 物件体积，向下取整到整 mm
> absorbable_by_size ⇔ floor(1000 · D) ≥ Req_mm       # D = 球的语义直径
> ```
>
> - `Req_mm`：`PickupAbsorbMath.TryComputePickupReqDiameterMm`（`60 = 6 × 10`，即 `πD³/6 ≥ 10V` 解出 D）。
> - 整 mm：`PickupAbsorbMath.TryFloorMillimetres` 是**唯一**把长度变成整 mm 的地方，两侧都用它；ε = 1e-3 mm（1 µm，约 4× 2.2 m 处 float32 ulp）只修浮点边界，与 Python 的 `placement_core.MM_EPSILON` 同值。
> - 「太大」只有一处定义：`PickupAbsorbMath.IsBeyondAbsorbLimit`（`reqMm > ballMm`），吸收门与挡路/支撑分类都调它。
> - `k` 的合法范围是 `0 < k ≤ 32`（`MaxVolumeK`）：0 会把每件物体定价成 0 体积，70（0.70 的笔误）会让椅子被针尖吸走，所以越界是**拒绝**不是夹紧。
> - 原来的 512 m³ 上限退役，换成一个**不属于玩法**的数值卫生常量 `MaxItemVolumeSanityWorldM3 = 1000`：2.20 m 上限球最多能吸 0.558 m³ 的物件，1000 m³ 是它的约 1,792 倍（整关 511 件合计也才 3,022 m³），只有坏掉的 bounds 能碰到；碰到时结论是「不可吸 = 挡路」。
> - 实测：`_ref_cancer` 在 0.222 m 球上旧门 91 件 → 新门 **184** 件，原作 **184** 件；145 个类型的 Req 比值 145/145 = 1.0000。详见 [`ABSORB_GATE_ORIGINAL.md`](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。
>
> **成长侧没有一起改**：`ΔV = V_box`（100%）仍是步骤 C/D 的事，所以现在「门」用 `k·bbox`、「成长」还是 bbox——这是**已知的未完成项**，不要当成两套门。

`a·b·c` 是物件**自身尺寸** `itemSize` 的三边乘积（m³；宿主用 `propLocalBounds`，模型根节点局部轴下量得的包围盒，不含摆放旋转；`propLocalBounds` 缺失时才退回世界包围盒 `propBounds` 并告警），`D` 是球的语义直径（m），`absorbVolumeRatio = 10` 表示**球体积必须是物件体积的 10 倍**。`(π/6)·D³` 就是球自己的体积，所以整条规则读作 `V_item ≤ V_ball / 10`，也就是 `V_ball ≥ 10·V_item`——原作的口径。

**证据等级：中。** [ABSORB_SIZE_RATIO.md](../../Research/KatamariBallRules/ABSORB_SIZE_RATIO.md) 有 14 条来源、逐条分级：一份社区数据挖掘直接陈述了 10× 体积判定与「Size to roll up = 能滚起它的最小球径」（S-AR01/S-AR02，**唯一**直接陈述常数 10 的来源）；9 个物件按门槛反算体积、8 个落在现实体积 ±25% 内（S-AR05 等）；三处独立旁证同向（Fandom 的「包围盒含空腔」与「铅笔比橡皮好吸」S-AR07、游戏内铅笔 4.3 cm < 橡皮 8.1 cm 的反例、玩家观测帖 S-AR12）。**常数 10 没有被第二份独立来源确认**：若真值是 8 或 12，所有门槛按 `(c/10)^(1/3)` 缩放，即 −7%～+6% 的球径，不改变口径结论。

展开（2026-11-16 起）：物件体积在 `PickupAbsorbMath.TryComputeOriginalItemVolume`（`k × a·b·c`），整 mm 在 `TryFloorMillimetres`，门槛在 `TryComputePickupReqDiameterMm`，比较在 `IsBeyondAbsorbLimit`（**唯一一处**，吸收门与挡路/支撑分类都调它）。「太大」在运行时只有这一份定义。

| 量 | 谁提供 | 语义 |
|---|---|---|
| `semanticDiameterWorldM` | `KatamariBallMotor.GetSemanticDiameterWorldM()`，与 `SizeModelMath.TryDerive` 的第三个输出同源 | 球**报告给玩家的**语义直径（起始 0.29974 m、满径 2.20 m）。**不是**碰撞半径；reach 仍用碰撞半径 |
| `volumeK` | 宿主 `PickupFieldHost.propVolumeK`（导入器从 `defs[].volumeK` 填，缺省 0.70），随候选经 `SubmitCandidateWithVolumeKMm3` 进电机 | 原作的每件体积系数 `k`；合法范围 `0 < k ≤ 32` |
| `itemSize` | 宿主 `PickupFieldHost` 在 Start 解出的 `propOwnSizes`（`propLocalBounds`，缺失才退回 `propBounds`），随候选进电机 | 物件自身三边 `a, b, c`；转动物件不改变它，所以同一件东西转任何角度门槛都一样。挡路/支撑分类调同一个 `IsBeyondAbsorbLimit`，读同一份自身尺寸 |
| `BallSemanticDiameter → ballMm` | `PickupAbsorbMath.TryFloorMillimetres`（`GetAbsorbGateDiameterMm()` 报告同一个数） | 球的整 mm 直径，门的一侧 |
| `k·a·b·c` | `PickupAbsorbMath.TryComputeOriginalItemVolume` | 原作的物件体积 `V`；门槛 `Req_mm = floor(1000·cbrt(60V/π))` |

**判定量是物件自身的包围盒体积，不是最长边、不是对角线、也不是包围球。** 与之相邻但**不同**的量是接触用的有向盒距离（第 1 节）：那个只决定「够不够近」，与「够不够大」无关，尺寸门读的是物件自身尺寸 `itemSize`，与朝向无关。把转过物件的世界 AABB 当 `a, b, c` 代入会把它算大（参考关卡里牙刷门槛 76 mm 会变成 189 mm）；接触同样不读世界 AABB，而是把自身尺寸的盒放进世界（见第 1 节）。细长物件是两者分离的典型——棒球棒 `0.02×0.02×0.85 m` 的 AABB 体积是 `0.00034 m³`，而它的包围球半径是 `0.425 m`。把最长边、对角线或包围球半径中的任何一个当体积代入，都是这里点名的算错。

**为什么必须换量而不是调系数（用户给的例子）。** 测试场景里黄色小方块边长 **0.05 m**、红色方块 **0.30 m**：**边长只差 6 倍，体积差 `(0.30/0.05)³ = 216` 倍。** 球能吃多少是**体积**账（球体积 ÷ 10），而线性口径只拿「最长边 vs 球直径」比长短——它嗓门里只有那个「6」，说不出 216 倍，也就在结构上无法回答「这个球够不够大」。换成系数语言更清楚：等效线性系数 `k = 可吸物件最长边 / 所需球径` **不是常数**（研究文档 §4：立方体 0.37、3:1 的砖 0.78、5:1 的扫帚级 1.09、10:1 的铅笔级 1.74），所以任何单一 `absorbSizeRatio` 都只能在一个形状比上对：口径错，调系数就只是在两个方向的误差之间挪。真实道具建模后差别更大：方糖 1.6 cm vs 野餐桌 150 cm，边长差约 94 倍，体积差约 `8×10⁵` 倍。这就是决定 (l) 把「判定量」换掉、而不是把 1.0 调成 1.25 或 1.5 的原因（后者的后果清单见 [SIZE_GATE_AUDIT.md](../../Archive/Reviews/SIZE_GATE_AUDIT.md) §4–§5）。

**细长物件因此自动能吸，不需要按形状特判。** 棒球棒 `0.02×0.02×0.85` 的体积 `0.00034 m³`，0.50 m 球的限是 `0.006545 m³`，放行；旧线性口径要 `0.85 ≤ D × 1.0`，即 0.85 m 的球才吸得动——这正是「原作里最像『吃了比自己大的东西』」的那批道具（棒球棒、铅笔、扫帚）被关掉的原因。反方向的代价是**薄片几乎白送**：`0.1×0.07×0.002` 的落叶体积只有 14 cm³。这是原作的宽容方向（研究文档 §7.4），要收就收在作者侧（`bounds` 或 `@size=` 声明），不要为它加回一个最长边门。

**球类道具会被 AABB 算虚大 `6/π ≈ 1.91` 倍，这是忠实还原，不是 bug。** 球/椭球（种子、珠子、毛絮团、水果）的 AABB 体积是 `d×d×d`，比真实体积大 1.91 倍，于是这些道具需要约 `1.91^(1/3) = 1.24` 倍大的球才吸得动。原作本身就用包围盒、并把空腔算进去（S-AR07 原文「including any empty space」），所以这是同构而不是缺陷；要按「看起来的体积」校准，正确的口子是给每件道具存一个 `volume_m3`（研究文档 §6 推荐项 1 的后半句），不是把这一层改回长度。

**边界与非法值。** 比较是 `≤`（含等号）：体积恰好等于上限放行。`absorbVolumeRatio ≤ 0`、ratio / 语义直径 / 上界非有限、语义直径 `< 0` 一律 `RejectInvalidInput`。**ratio 配成 0 不是「谁都吸不到」的正常状态，是配置错误**：它大声拒绝（拒绝环里是 6），而不是悄悄把上限钳成 0。语义直径恰好为 0 会被接受、上限也是 0（什么都吸不到），那是尺寸读数没就绪，不是配置错误。**新增一条比 (h) 更严的守卫**：三轴乘积溢出成 infinity 的物件按 `RejectInvalidInput` 拒（以前比最长边不会溢出）；「装不下的体积」是坏数据，不是「特别大的物件」。

**比较严格性**（2026-11-16 起）：接触是 `≤`，盒距恰好等于 `碰撞半径 + 接触余量` 算吸附（测试 `PickupAbsorbTests.cs`：点状物件 0.6 m 对 0.6 m 极限通过，0.6001 m 拒绝）；尺寸门是「**整 mm 相等就放行**」（`Req_mm == ball_mm` 可吸，少 1 mm 就拒）；成长上限门是 `≥`（恰等于上限就拒）。尺寸门的边界是**整数 mm**，所以它可以被精确地测试（`AbsorbSizeGateTests.AnItemExactlyAtReqIsAbsorbableAndOneMillimetreLessIsNot`）；唯一的 epsilon 是整 mm floor 前的 1e-3 mm 守卫，它只把浮点误差压到边界上的值抬回整数，不改变「少 1 mm 就拒」。

**序列化默认值（2026-11-16 起：门不再有任何序列化旋钮）。** 体积比率是常量 `PickupAbsorbMath.AbsorbVolumeRatio = 10`，物件上限是常量 `MaxItemVolumeSanityWorldM3 = 1000`（数值卫生，非玩法），逐件 `k` 来自关卡数据（缺省 0.70）。下面 2026-11-15 的两个字段已**删除**，场景里的残留键是 Unity 忽略的死键。

- `absorbVolumeRatio = 10f`。**这是玩法阈值**。原作判定的就是这个 10（证据等级中），不是本项目调的味；改成 5 等于「球只要比物件大 5 倍」，与研究的结论不再同构。
- `maxItemVolumeWorldM3 = 512f`。**这是安全上界**，不是玩法阈值：512 m³ 是「8 m 立方体」的读法，与 (h) 时代的 8 m 长度上界同量级、但换成了体积口径。长度上界配体积门会在 8 m 处硬截断，语义错——一条 0.02 m 见方、8 m 长的杆体积只有 0.0032 m³，却会被「8 m」钉死。
- **删掉的字段**：`absorbSizeRatio`（物件最长边 / 球直径 = 1.0）与 `maxItemSizeWorldM`（8 m 长度上界）。删而不是留兼容，理由有两条：长度上界与体积门拼在一起语义错（上一条），以及留一条旧字段等于留一条「能悄悄把闸门压回线性」的配置路径——(h) 已经因为场景里的残留覆盖值踩过一次。**迁移后果**：现存 lab 场景/prefab 里的 `maxItemSizeWorldM: 8` / `absorbSizeRatio: 1` 变成 Unity 忽略的死键，组件回落到新字段的 C# 默认值（512 / 10），所以**不改场景行为也已经是新口径**；清理死键是 Unity 侧的顺手活。
- `contactRadiusWorldM = 0f`。见下面第 1 节末与决定 (i)：0 是「必须实质接触」，不是「还没接」。

### 1.2 接触时刻的局部位姿与 gap 诊断（R33 修复）

**球侧一律用 `collisionRadiusWorldM`（等效体积球），v1/v2 相同**；不读视觉核心（核的等体积直径虽等于当前碰撞直径，判定量仍只认碰撞半径），也不把立方体方向支撑函数/滚动针壳 `reach` 代入本层。原作专利 `Mh` 是盒、实际形状/朝向未测，本项目采用其内接等效球是有记录的取舍，见 [`PARITY_AUDIT.md`](../Parity/PARITY_AUDIT.md)“吸收触发的几何”行。

电机不是 Unity `FixedUpdate`：它在自身 `Update` 内用累加器跑若干定步。每步阶段 5 判门 → 6 成功提交 → 7 成长 → 7.5 成长推出 → 8 滚转；宿主另一个 `Update` 消费环，因此**不得到消费时再用当前球心/当前滚转换算**。现行纯值入口为 `PickupAbsorbStepMath.EvaluatePendingCandidates` → `CommitAcceptedAbsorbs` → 成功行的 `AttachmentStepMath.RecordAbsorbedEvent` → `PickupAbsorbStepMath.CompactPendingCandidates` → `RecomputeConfirmedAndSize`；事件 payload：`AttachmentStepMath.RecordAbsorbedEvent` 用门判断的球心和候选位置/旋转，调用 `AttachmentMath.TryCaptureContactPose`（用同一 `VisualRollMath` 算到本步接触位置的滚转、吸收前半径），当步转出 local position/rotation，与碰撞半径、contactRadius、球心、步号一起写固定 16 槽并行数组。只有账本提交成功才记事件。宿主 `TryGetAbsorbedEventAttachment` 经 `AttachmentStepMath.TryReadAbsorbedEventAttachment` 读取保留槽，直接把 local pose 写 `AttachmentRoot`，再经 `AttachmentStepMath.TryAttachRadialProxy` 给径向代理；缺失/过期 payload 不用当前姿态兜底。此后的 N 步运动或同一步成长推出，都携带已冻结局部位姿。原有 `GetAbsorbedEventPosition` 仍是门使用的道具世界位置，供其它事件读者使用。

**唯一 gap 口径**：电机与宿主都调 `AttachmentMath.TryComputeBoxGap` → `PickupAbsorbMath.TryComputeContact`，`boxGapMm = 1000 × [boxDistance − (吸收时碰撞半径 + 吸收时contactRadius)]`。电机量接触世界位姿，宿主量将写入的局部位姿；球面在刚体变换下不变，两个值可按 `event` 配对。门不加 epsilon；验收允许浮点与打印误差 **0.2 mm**（两端 gap 差绝对值 ≤0.2 mm，宿主 gap ≤0.2 mm），不是 5 mm 手感窗口。日志另打 `ballR/contactMm`、`delaySteps`（接触后已走的定步数）、`centerDeltaMm`（含同一步推出），不要把时延位移混进 gap，也不要拿视觉核心网格边缘（非球形皮肤的实际轮廓）当当前物理球面。

**R33 证据（修复前）**：亲读三份 play 日志共 70 对；motor 70/70 非正，host 69/70 正（−9.2～117.5 mm），两端差中位 77.2 mm。原因主要是电机日志减 v2 放大的方向 `reach`，宿主减物理半径；可用紧邻 story 直径直接分解的 36 对中，半径项解释总差的 97%，29 对几何残差 ≤0.2 mm，但两对有 +36.9/+42.8 mm 真几何漂移（旧日志不能区分后续步与同一步成长推出）。不是“触发已正确，全部差都是延迟”。完整例子、截图边界与复验表在 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) §1、§6。

**NUnit**：`PickupAttachmentPoseTests` 是无 GameObject 的纯值回归（0/1/3/8 步、四分之一圈接触滚转、同口径 gap、轴/面对角/体对角球门）；`PickupAttachmentIntegrationTests` **需 Unity**，在一次性 preview scene 测实际事件环与宿主消费、物件已移动/球已成长、游标幂等，以及 v1/v2 均拒球外角向物。部分 Unity Quaternion 纯数学 API 是 native extern，本地 .NET runner 不可执行时不算通过；UdonSharp 编译/Unity 场景复验仍由操作员做。

### 2. 接触量的是盒，不是外接球（对照表）

立方体边长 `s`，球的碰撞半径 `r = 0.14987 m`（起始球），接触余量 0。「球心到物件中心的距离，小于多少算碰到」：

| 立方体边长 `s` | 正对面心（`r + s/2`） | 正对角点（`r + √3·s/2`） | 旧外接球口径（处处等于角点值） |
|---:|---:|---:|---:|
| 0.10 m | 0.200 m | 0.236 m | 0.236 m |
| 0.25 m | 0.275 m | 0.366 m | 0.366 m |
| 0.35 m | 0.325 m | 0.453 m | 0.453 m |
| 0.50 m | 0.400 m | 0.583 m | 0.583 m |

旧口径（`r + 外接球半径`）在角点方向才是对的，在面心方向多放行 `(√3−1)/2·s ≈ 0.366·s`，细长件更多：牙刷 `0.012×0.0155×0.18 m` 从侧面逼近，旧口径在中心距 `0.111 + 0.0905 = 0.2015 m` 时触发，盒面离球面还有约 9 cm；新口径在 `0.111 + 0.006 = 0.117 m` 时触发。测试 `PickupAbsorbTests.ContactTableMatchesTheWorkedExample` 把上表的数字（1 mm 容差）钉在运行时代码上，`ItemContactTests` 钉盒距离的面/棱/角三区、旋转、球心在盒内和细长件侧向逼近。

对吸收节奏的影响见 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) 的 O4：参考关卡路线（模型估算，不是 Unity 实测）里 150 件有 67 件（45%）在旧停靠点盒面碰不到球，需要多靠近的量中位 3.7 cm、p90 8.1 cm、最大 25 cm，总路程约 +4.8%；起始球旋转件的「触发」更晚，这是还原的代价，不是回归。

装配层实际传什么：宿主 `PickupFieldHost.SubmitProp` 调 `SubmitCandidateWithVolumeKMm3`，把 `propOwnSizes`（物件自身尺寸）、`propTransforms[i].rotation`、`position` 存进队列（`pendingSizes`/`pendingRotations`/`pendingPositions`，`CompactPending` 同步搬运），阶段 5 把它们和球心一起喂给判定。实验室的宿主（`PlaygroundProp`）用旧桥 `SubmitCandidate`，把 `(semanticDiameterM, semanticDiameterM, semanticDiameterM)` 当 `size`、单位旋转，即立方体近似，**不是** Collider 尺寸。结论：**把道具 Collider 改小不会缩小接触**，除非同时改 `size`。

### 3. 门序：首个失败者胜（枚举值不是门序）

`EvaluateAbsorb` 的检查顺序（`PickupAbsorbMath.cs:144-188`）。**注意：`AbsorbOutcome` 的编号（`:18-32`，`Absorb=0` 到 `RejectInvalidInput=6`、`RejectNoGain=7`）只是枚举值，不是执行顺序**——`RejectInvalidInput` 编号最大却最先执行。

| 序 | 判定 | 输入/触发 | 返回 |
|---:|---|---|---|
| 0 | 尺寸门的输入守卫（`EvaluateAbsorb`） | `volumeK` 非有限或不在 `0 < k ≤ 32`、语义直径/碰撞半径/接触半径非有限、碰撞半径或直径 `< 0`、`itemSize` 负/非有限/乘积溢出 | `RejectInvalidInput` (6) |
| 1 | 输入守卫（非模型门，端口自加） | `itemKind == null` 或 `allowedKinds == null`（`:144`）；`allowedKindCount < 0` 或 `> allowedKinds.Length`（`:145-146`）；`itemSize`/`itemPosition`/`ballPosition` 含非有限分量、`itemRotation` 含非有限分量或为零四元数；四个标量参数非有限（`:150-154`）；`itemSize` 任一分量为负（`:155-156`）；`collisionRadiusWorldM < 0`（`:157`）；以及计算中途的盒距、接触极限非有限（`TryComputeContact`） | `RejectInvalidInput` (6) |
| 2 | 可吸附标记 | `itemAbsorbable == false` | `RejectNotAbsorbable` (1) |
| 3 | 种类白名单 | `IsKindAllowed` 走完前 `allowedKindCount` 项都没按序数匹配上（`:160-161`，实现 `:425-433`） | `RejectKindNotAllowed` (2) |
| 4 | 尺寸门 | `floor(1000·cbrt(60·k·a·b·c/π)) > floor(1000·D)`（整 mm 比较，唯一实现在 `IsBeyondAbsorbLimit`；体积超过 `MaxItemVolumeSanityWorldM3` 也算「太大」） | `RejectItemTooLarge` (3) |
| 5 | 成长上限门 | `collisionRadiusWorldM ≥ maxGrowthRadiusWorldM`（含等于，`:165-166`） | `RejectBallAtGrowthCap` (4) |
| 6 | 接触 | `boxDistance > collisionRadius + contactRadius` 则拒（球心到道具有向盒的最短距离，`TryComputeContact`），否则吸 | `RejectOutOfReach` (5)「没碰到」/ `Absorb` (0) |

**早门遮蔽后门**——这是排查时最容易白跑的地方：

- 白名单为空或拼错时，每个候选都停在第 3 门，第 6 门的盒距**根本没有判**（虽然装配层为了记拒绝事件另算了一次盒距，见第 7 节），所以“盒距看着在极限内”不能作为门序正常的证据。
- `absorbable=false` 的物件在第 2 门就出局，它的种类错误、尺寸超限都看不到。
- 体积大于**本步上限**（`GetAbsorbVolumeLimitWorldM3()`，m³）的物件停在第 4 门，不会告诉你它同时也够不着（对称地：球到成长上限后，所有候选都停在成长上限门，与几何无关）。第 4 门随球长大而放宽，所以“它以前吸不到”不是证据，要读当前上限——**并且要比的是物件的三轴乘积，不是最长边**。
- 一个 `bounds` 为负或位置含 NaN 的候选停在第 1 门，会把同类里其他候选的问题一起盖住。

所以排查必须**一次只修一门、改完重读拒绝原因**，不能一次改三个参数。

### 4. 空白名单 = 全部拒绝（fail-closed）

- `IsKindAllowed` 从 0 遍历到 `allowedKindCount`，一个都没匹配就返回 `false`（`PickupAbsorbMath.cs:425-433`）——**0 项的意思是“没有任何种类被允许”，不是“不过滤”**。
- `Start()` 把它初始化成 `new string[0]` + `allowedKindCount = 0`（`KatamariBallMotor.cs:399-400`）。装配层内部从不调用 `SetAllowedKinds`；全仓唯一的调用点是宿主（实验室里是 `BallPlaygroundBootstrap.cs:175`）。
- **陷阱：`Start()` 会无条件把它重置成空**（`:399-400`）。所以在 `Awake`/`OnEnable` 或另一个脚本的 `Start` 里先设好，随后本层 `Start` 一跑就被抹掉——静默回到全拒。正确时机是“本层 `Start` 之后、第一个固定步之前”（实验室宿主在第一次 `Update` 里做，`BallPlaygroundBootstrap.cs:167-175`），并且**读回** `GetAllowedKindCount()` 确认（`:1577-1580`；实验室在 `:179-184` 读回并在 0 时 `LogError`）。
- `SetAllowedKinds` 只挡 `null` 和越界 count（`KatamariBallMotor.cs:890-896`）；它保存的是调用方数组的**引用**，不是拷贝。宿主之后复用或改这个数组，白名单会跟着静默改变。实验室为此特意传序列化数组而不是运行时新建的 `string[]`（`BallPlaygroundBootstrap.cs:169-172`，注释说运行时新建数组在这个边界上会触发 `__SetProgramVariable__` 空引用）。
- 匹配是**序数比较**（`string.CompareOrdinal`，`PickupAbsorbMath.cs:430`）：`"Stone"` 不等于 `"stone"`，首尾空格也算不同。数组里的 `null` 项被跳过（`:429`），不报错。
- 忘设时的运行表现：**吸收数为 0，没有异常、没有报错**；控制台上最多只有 3 行拒绝诊断（`KatamariBallMotor.cs:294`、`:1464`），队列照样被候选填满，看上去就像“这一关没有可吸的东西”。唯一的客观线索是拒绝环形缓冲里每条都是 reason = 2（第 7 节）。

### 5. 候选生命周期：进队即每步重判，只有三条出口

装配层持有一个固定 4 槽的候选队列（`PendingCandidateCapacity = 4`，`KatamariBallMotor.cs:257`）。状态转移只有这些：

| 时机 | 发生什么 | 代码 |
|---|---|---|
| 宿主 `SubmitCandidateWithVolumeKMm3(...)` | 入队；存下**当时**的 `size`（物件自身尺寸，门用）、`rotation`、`position`（接触用）、`growthMm3`、`id`、`generation` | `:914-935`（写入 `:924-933`） |
| 队列已满（4 条） | 该次提交**被丢弃**并 `false`，同时点亮溢出信号 | `:917-921`（`SignalOverflow("pending candidate capacity 4 exceeded")`） |
| 每个固定步、阶段 5 | 对**在队的每一条**重新跑一次 `EvaluateAbsorb`，判定用的球心是贴地后的最终位置 | `:580`、`:643-663` |
| 判定为拒 | 记一条拒绝事件（原因 + 中心距），候选**留在队里** | `:656-661` |
| 判定为吸 | 走账本提交；只有提交成功才真正移除 | `:673-716` |
| 账本提交失败 | 候选**留在队里**，下一帧重新判定（包括再次通过判定再失败） | `:669-670`、`:702-713` |
| 宿主 `WithdrawCandidate(id)` | 按 id 删除（就地压缩），可再提交；回合结束/场地复位的“全部释放”用它 | `:953-982` |
| 宿主 `WithdrawCandidateUnlessAccepted(id)` | 同上，但**不删**本步判定为吸、尚未提交的条目（`pendingAccepted`），此时返回 `false`；`IsCandidateQueued(id)` 区分“已不在队”与“因已接受而保留”。宿主按最近重排候选时用它 | `KatamariBallMotor.cs` 的 `RemovePendingById` |
| 宿主 `ClearCandidates()` | 全清，不触碰账本 | `:984-987` |

关键点，逐条：

1. **位置是入队快照，不会刷新**。`SubmitCandidate` 存的是传入的 `worldPosition`（`:928`），判定每步都用这个旧值（`:650`）。所以球一旦离开，候选的距离只会越算越大——宿主必须在 reach 内提交，并在离开时撤回，这不是优化建议而是契约（第 6 节）。
2. **每步都重判**。`pendingAccepted[i]` 每步先清零（`:647`），上一帧的结论不复用。好处是物件被推开又推回来能自动恢复；坏处是每条在队候选每步都产生一条拒绝事件：4 条在队 + 8 槽环（`:292`），**两次固定步就把环写满，从第三步起开始淘汰**，`GetRejectEventDroppedCount()`（`:1519-1523`）随之持续增长。看拒绝原因要看最新的几条，不是最早的。
3. **没有 TTL、没有过期、没有老化**。代码里没有任何时间或帧数上限与候选相关；唯一的移除条件是“提交成功”、“宿主撤回”、“宿主全清”。因此**系统性拒绝会把队列永久冻在 4 槽**（`PickupFieldHost` 靠只提交能过尺寸门的最近 4 件来避免，见第 6 节第 9 条）：白名单错（第 3 门）、尺寸上限太小（第 4 门）、球到成长上限（第 5 门）、`valueUnits` 非整数或账本不在 Playing（提交失败）——任何一种都会让队列再也进不来更近的物件。
4. **这条冻结是实测过的症状，有两种形状**：(a) 候选按“离球近”就入队、球随后滚走，于是远处的道具把队列占满，每步都以“超出 reach”被拒，中心距随球离开单调增大；(b) 候选在 reach 内但**球永远吃不下**（`RejectItemTooLarge`），每步被拒、也不会自己离开，更近的小件进不来（Unity 实测 R28：0.222 m 球，60 s 路线 34 件只吸 12 件，其中 20 件曾到 0.3 m 内；4 个槽被 1.76 m 的饭团、246 mm 门槛的钱包、397 mm 的招潮蟹占住）。两种都表现为吸收数不涨、任何更近的道具进不来。`PickupFieldHost` 对 (a) 只在激活半径内提交并撤回离开的，对 (b) 在选择阶段就用尺寸门预筛（第 6 节第 9 条）；lab 宿主 `PlaygroundProp` 只处理了 (a)。
5. **重复提交同一个 id 是幂等的**：按 item id 查重，已在队列里就直接返回 `true`、不占新槽，所以宿主不需要“已入队”闩锁。但一件**已提交进账本**的事件再次入队时，账本会以“键已提交”拒绝（`GrowthLedgerMath.cs:440-444`），那条候选永远提交失败、永远占槽，而且拒绝环里没有记录（判定是通过的）——所以已吸收的道具不能再提交。
6. **队满时丢弃会点亮溢出信号**（`:917-921`），这是数据错误信号而不是玩法规则：宿主每次提交前应读 `GetPendingCandidateCount()` / `GetPendingCandidateCapacity()`（`:1313`、`:1326`）。

### 6. 宿主欠本层什么

按上面的机制整理成契约清单，任何一条不满足都不会报错，只会表现为“吸不到”：

1. **在本层 `Start()` 之后、第一个固定步之前**调 `SetAllowedKinds(...)`，并读回 `GetAllowedKindCount() > 0`。传序列化数组，不要传运行时新建的数组；不要让本层持有后又被改写的数组。
2. **只在可能碰到时提交**。判定用入队时的位置/旋转快照，不刷新；宿主的候选预筛（激活半径，默认 1.6 m，按球心到道具位置的距离）没有改。新口径能触发的件是旧口径（外接球）能触发的件的子集，所以预筛漏掉的件不比以前多；超大件（半对角线接近激活半径）的预筛盲区仍是旧问题，不是这次引入的。
3. **物件离开就 `WithdrawCandidate(id)`**；关卡换段落或重开就 `ClearCandidates()`。没有 TTL 替你兜底。若宿主会在球滚动中按“最近”重排候选，撤回用 `WithdrawCandidateUnlessAccepted(id)`，别吞掉已判定为吸的那一条。
4. **同一个 `itemInstanceId` 只提交一次**。提交成功后它进入账本；再提一次会永久卡住一个槽（`GrowthLedgerMath.cs:440-444`）。
5. **`valueUnits` 必须是整数**。`TryAbsorbValueToLedgerGain` 对小数、负数、非有限值、超出 int32 的值一律返回 `false`（`PickupAbsorbMath.cs:411-422`），两个模型都没定义取整规则，所以它不四舍五入。非整数价值的后果不是“少加一点”，而是这件道具永远提交不了、永久占槽。
6. **障碍注册与吸收判定无关**。`AddPlane` / `AddBox` 喂的是 `SweepMath`（`KatamariBallMotor.cs:989-1028`），本层的接触判定没有遮挡项（`PickupAbsorbMath.cs:80-84` 明说“models no occlusion”）。所以隔着一面已注册的墙，只要中心距够近，照样能吸——想让墙挡住吸收，本层没有这个能力，只能在宿主侧不提交。
7. **账本必须已在 `Playing`**：`TrySubmitClaim` 的阶段门在 `GrowthLedgerMath.cs:435`。否则每个候选都判定为“可吸”却提交失败，表现为“零拒绝、零吸收、队列满”（第 7 节用它区分账本问题）。
8. **候选收集、空间索引、道具生命周期不在本层**：本层不扫场景，只判定宿主递进来的一条。
9. **队列里只放“眼下可能吸到的”，且是其中最近的**（`PickupFieldHost.ScanProps`，选择逻辑在纯函数 `PickupCandidateSelection.OfferProp`）。每次扫描：激活半径内的自由道具，先过尺寸门——就是 `PickupAbsorbMath.IsBeyondAbsorbLimit(物件自身尺寸, k, 电机当前语义直径)`，与电机判定和挡路分类同一个函数，不复制门的数学——过了的才参与“最近 4 件”（4 = 电机容量）；然后**先撤回**本宿主已入队但不在本次选择里的（被更近的挤掉、球滚远、门已不过），**再提交**选中的。已判定为吸、尚未提交的条目不撤回。球长大后每次扫描都用当前直径，先前吸不了的自然进入。吸不了的大件不进队列，仍由 `UpdatePropObstacles` 当墙/台阶（那条路径只读同一个尺寸门，不读队列），所以它们不再产生 `RejectItemTooLarge` 拒绝事件；队列里剩下的拒绝基本只有 `RejectOutOfReach`（在激活半径内、过了门、还没到 reach）。激活半径用球心到物件中心的距离，不随球径缩放。

### 7. 静默零吸收的排查顺序

按代价从低到高，每一步都能给出“该改配置还是该改几何”的结论：

1. `GetAbsorbedCount()`（`KatamariBallMotor.cs:1273`）与 `GetRejectEventCount()`（`:1508`）。两者都是 0 且队列空 → 宿主根本没提交（收集器没接上），别调几何。
2. `GetAllowedKindCount()`（`:1577`）。为 0 → 配置错误：白名单没设、被 `Start()` 重置、或计数没传对。这是最常见的“零吸收”。
3. **拒绝数为 0 但队列停在 4、吸收数不涨** → 不是几何问题：判定通过了，但提交没成功。三个无拒绝记录的成因：账本阶段不是 `Playing`（`GrowthLedgerMath.cs:435`）、键已提交过（`:440-444`，重复 id）、或 `valueUnits` 非整数（`PickupAbsorbMath.cs:411-422`）；另有 `hasLocalRow == false` 时整段提交直接跳过（`KatamariBallMotor.cs:675`）。先看 `TryBeginPlaying` 的返回值（实验室在 `BallPlaygroundBootstrap.cs:200-207`，注释里记着 0 时间戳会被拒、症状正是“四个槽永不排空”）。
4. 拒绝数不为 0 时，用最新几条读原因与距离：`GetRejectEventItemId(i)`（`:1526`）、`GetRejectEventReason(i)`（`:1539`）、`GetRejectEventDistance(i)`（`:1547`）；索引是**绝对索引**，已淘汰的返回 `""`/`-1`（`:1554-1564`）。原因码对照 `AbsorbOutcome`（`PickupAbsorbMath.cs:18-32`）：

| 原因 | 枚举 | 指向 | 下一步 |
|---:|---|---|---|
| 1 | `RejectNotAbsorbable` | 数据：宿主把 `absorbable` 传成 false | 查道具数据/标记 |
| 2 | `RejectKindNotAllowed` | **配置**：白名单为空、拼错、大小写/空格（序数比较） | 读回 `GetAllowedKindCount()`，核对 `kind` 字符串 |
| 3 | `RejectItemTooLarge` | **数据**：这件物件的 `Req_mm = floor(1000·cbrt(60·k·a·b·c/π))` 大于本步球的 `GetAbsorbGateDiameterMm()`，或它的体积超过 `MaxItemVolumeSanityWorldM3`，或宿主传的 `size` 比以为的大（它是全长；把 Collider 尺寸当半长传就会放大 8 倍体积；把转过物件的世界包围盒当 `size` 传也会放大） | 打印 `GetAbsorbGateDiameterMm()` 与 `PickupAbsorbMath.TryComputePickupReqDiameterMm(size, k)` 两个整数比；球长大时两者都涨。k 配错（0/70）会先被拒为 6。**不要比最长边**：0.85 m 的细杆 Req 只有 165 mm |
| 4 | `RejectBallAtGrowthCap` | **配置**：球已到 `maxCoreRadiusWorldM × collisionToCoreRadiusRatio`（默认 1.1 m），此后一律不收 | 看 `GetCollisionRadiusWorldM()`；这是尺寸上限，不是几何 |
| 5 | `RejectOutOfReach` | **几何**：没碰到（盒距 > 碰撞半径 + 接触余量） | `GetRejectEventDistance()` 现在是**盒距**（球心到道具有向盒，-1 = 非法几何），与 `GetCollisionRadiusWorldM() + contactR` 比；拒绝诊断行打印 `boxDist`、`limit`、`size`。差得多是位置/尺寸错，差一点点是 `contactRadiusWorldM` 与预期不符 |
| 6 | `RejectInvalidInput` | **配置或数据**：`kind` 为 null、`size` 有负分量、位置含 NaN/Inf（未接线的 Transform 会直接给 NaN）、位置或尺寸溢出、count 大于数组长度 | 打印原始输入，先找 NaN |

5. 距离**单调增大**且队列停在 4 → 就是第 5 节的冻结：宿主没有撤回。不是接触变紧。
6. 只看控制台不够：拒绝诊断日志全程只有 3 行（`KatamariBallMotor.cs:294`、`:1464-1485`，每次打印 `d`、`boxDist`、`limit`、`size`、`ballR`、`contactR`、`allowedKindCount`），用完之后就不再输出。持续通道是拒绝环（8 槽，`:292`）加 `GetRejectEventDroppedCount()`（`:1519-1523`）。
7. 看宿主的队列是否健康：控制台 `[PickupFieldHost] scan inRange=N withinLimit=M selected=S queued=K ballMm=B`（计数变化时、至多每 2 s 一行），或读 `GetScanInRangeCount()` / `GetScanWithinLimitCount()` / `GetScanSelectedCount()` / `GetQueuedCount()`。N = 激活半径内的自由道具数，M = 其中过了尺寸门的，S = 选进队列的（≤ 4），K = 扫描后本宿主在电机队列里的条数；`queued` 长期 < `selected` 才是提交被拒。电机侧 `GetPendingCandidateItemId(i)` 读出的槽里，不应再出现 `Req_mm` 大于 `ballMm` 的道具。

### 与 `pickup_absorb.py` 的已知分歧

不是笔误，是两边的定位不同；记录在这里以免下次拿模型当依据。

| # | 项 | 代码 | 模型 | 影响 |
|---|---|---|---|---|
| 1 | 参数默认值 | `contactRadiusWorldM = 0f`（序列化）；门侧无序列化旋钮：比率/上限是常量，逐件 `k` 缺省 0.70（`PickupAbsorbMath.DefaultVolumeK`） | 无默认，构造 `PickupRules` 必须显式给（`pickup_absorb.py:74-78`，注释写明“deliberately no accepted/default gameplay tuning”） | 模型强迫调用方表态；运行时的门已把「比率」和「整 mm」钉成常量，剩下的逐件表态是 `defs[].volumeK` |
| 2 | 门粒度 | 不可吸附（`:130`）与种类（`:131-132`）是两个 outcome | 合并成一个 `if`（`pickup_absorb.py:83`） | 顺序一致，但模型无法区分这两个原因；“首个失败门即报告原因”对这一条在模型里不成立 |
| 3 | 非法输入 | 负 `bounds`、非有限值、null kind 一律先拒（`PickupAbsorbMath.cs:144-157`） | `eligible` 不检查；只有 `register` 检查 value/bounds（`pickup_absorb.py:42-43`） | 端口自加的守卫，代码注释 `PickupAbsorbMath.cs:13-16` 已声明；同一份输入两侧结论可能不同（模型放行、代码拒） |
| 4 | 候选生命周期 | 4 槽队列、每步重判、submit/withdraw/clear、无 TTL（第 5 节） | 完全没有对应物：`nearby()` 只返回候选（`pickup_absorb.py:59-69`） | 第 5 节的规则**没有参考模型**，属实现侧契约，不能拿模型反驳 |
| 5 | 半径口径 | 接触极限用 `collisionRadius = core × collisionToCoreRadiusRatio`（`SizeModelMath.cs:179`） | 只有一个 `radius = min(max, base + growth × confirmed)`（`pickup_absorb.py:141-142`），没有碰撞/核心比 | 默认 ratio = 1 时两侧数值一致；ratio ≠ 1 时模型没有对应项 |
| 6 | 尺寸门（跨层） | **原作整 mm 口径（2026-11-16）**：`Req_mm = floor(1000·cbrt(60·k·a·b·c/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`（1.1 节） | **仍是旧口径**：`pickup_diameter ≤ ball_diameter × absorb_size_ratio`（`round_model.py:493-502`，默认 ratio 1.0，`:119`），`pickup_absorb.py:85` 仍然是 `max(item.bounds) > max_item_size_world_m`（最长边） | **已不是同构**：模型是线性、运行时是整 mm 体积。两个模型都是 DRAFT/离线参考，本轮**没有**追平。拿模型反驳运行时结论之前先读这一行 |

### 仍然会坏的地方（读代码看到、未修）

1. **`contactRadiusWorldM = 0` 是承重的决定值**：它是“能不能吸到”的全部额外余量，改它直接等于改手感，而没有任何一处要求关卡必须给值。0 是决定 (i) 的口径（必须实质接触），不是占位；`maxItemVolumeWorldM3` 是安全上界，两者性质不同：前者是手感契约，后者是防误配的上界。
2. ~~**`maxItemSizeWorldM` 绝对值 + 不随球长大**~~ **已解决两次**：先是 (h) 让上限随球长大，再是 (l) 把量换成体积。**残余风险换了个形状**：(l) 删掉了旧字段，所以场景里残留的 `maxItemSizeWorldM: 8` / `absorbSizeRatio: 1` 是死键、会被忽略，组件取新默认值 512 / 10——**这正好是想要的**，但也意味着「某关卡曾经用场景覆盖把门槛调松/调紧」这件事再也不会生效，谁想按关卡调整现在只能改组件（或未来改成体积字段的覆盖）。`absorbVolumeRatio = 10` 本身仍是研究结论（证据等级中，常数 10 只有一份独立来源），不是实测。
3. **`size` 由宿主给、与 Collider 无关联**：改 Collider 不改接触；实验室把 `semanticDiameterM` 当三边填（`PlaygroundProp.cs:110`），那是立方体近似。
4. **4 槽 + 无 TTL**：任何系统性拒绝都会永久冻结队列，而信号只有“拒绝环”（几何类）或**什么都没有**（账本类）。宿主已排除尺寸门这一类；白名单、成长上限、账本类仍靠接线保证。
5. **`SetAllowedKinds` 存引用不拷贝**（`KatamariBallMotor.cs:894`），且被 `Start()` 无条件重置（`:399-400`）——两个静默失效源。
6. **成长上限门的边界**：门用 `≥`（`PickupAbsorbMath.cs:165`），而 `SizeModelMath` 在“确认体积恰好等于上限体积”时走 `exp` 路径（`SizeModelMath.cs:165` 的 `logTotal > logCappedVolume` 是严格大于），理论上可能返回比上限小 1 ulp 的半径，那样满级球还能再吸一件。**这是读代码的推断，未实测。**
7. **`WithdrawCandidate` 用 `==` 比较字符串**（`KatamariBallMotor.cs:962`）。C# 语义是按值比较，UdonSharp 下是否同样按值**未验证**；若不按值，撤回会静默失效，退化成第 5 节的冻结。

**整数单位契约（有意为之，不是缺陷）**：`TryAbsorbValueToLedgerGain` 只接受整数单位（`(float)(int)v != v` 即返回 `false`）。小数值视为作者数据错误，由 `RejectNoGain` 大声拒绝并进入拒绝环/日志，**不要去"修"成四舍五入**——取舍是"数据必须整数"，让错误可见。

> 观感对照（吸附之后球长什么样：我们的红色球芯 vs 原版两帧的物件覆盖）：[`Docs/Evidence/BallPresentation/2026-11-15_attached_props_vs_original/`](../../Evidence/BallPresentation/2026-11-15_attached_props_vs_original/README.md)。
