# T47 拾取、吸附与成长离线参考模型

> **DRAFT 草案 / UNVERIFIED**：标准库 Python 离线模型，用于讨论和测试性质；不是 Unity/Udon 实现，不冻结公共接口，不代表已验证的运行时协议或调校结果。

## 状态分栏

### 【已定】
- 共享地图坐标系用于碰撞、拾取和吞噬；成长账本与渲染网格分离。参见 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L6-L21) 与第3节。
- 同物实例以 `(roundId, spawnId, spawnGeneration)` 辨识；请求幂等、唯一裁决结果；客户端可预测但失败要回滚。远端装饰不能决定玩法碰撞。
- 远端仅依据确认的状态重建外观摘要；装饰模型、代理凹凸、径向样本都不得决定游戏碰撞或计分。
- 接触判定与候选生命周期已是运行时契约（下一节「接触判定与候选生命周期」），**本节 Python 模型不是该契约的权威**：reach 的实现、默认值和队列行为以 C# 为准。
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

从仓库根运行 `python3 -m unittest discover -s Docs/tests`。吸附判定采用球半径 + 显式接触余量 + AABB 包围球半径的宽松近似；这只是离线候选模型。它不含遮挡、速度合理性、物件状态机、回合结算、网络传输或反作弊。权威状态应只计确认后的吸附集合；客户端装饰可丢弃并从权威快照重建。**不声称 VR、舒适性、Udon runtime、Unity 或网络实测通过。**

来源依据：[`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L32-L110)、[`Docs/01_GAME_DESIGN.md`](../../01_GAME_DESIGN.md#L13-L20)、[`Docs/Archive/Handoffs/Research_ReferenceRules.md`](../../Archive/Handoffs/Research_ReferenceRules.md)。

## Unity 实现

- 运行时：`Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs`（命名空间 `VRCKatamari.Pickup`）。EditMode 测试：`Assets/VRC_Katamari/Tests/EditMode/Pickup/PickupAbsorbTests.cs`、`RejectNoGainTests.cs`、`AbsorbSizeGateTests.cs`（体积门：0.05/0.30 的 216 倍、棒球棒、上限与非法值）与 `Tests/EditMode/BallMotor/AbsorbSizeLimitTests.cs`（装配层的默认值与上限读数）。
- 只做纯值判定：`EvaluateAbsorb` 按模型的门控顺序返回“可吸附”或首个拒绝原因（不可吸附、类型不在白名单、AABB 体积超上限、球已达成长上限、超出接触距离）。**体积上限的合成**在 `TryComputeAbsorbVolumeLimit`（球体积 `(π/6)·D³` ÷ `absorbVolumeRatio`，再由 `maxItemVolumeWorldM3` 兜底），物件的体积在 `TryComputeItemVolume`，比较只有 `IsVolumeBeyondLimit` 一处；带值的相对入口是 `EvaluateAbsorbWithValueAndBallSize`，装配层每步都走它（决定 (l)，见 1.1 节）。接触判定的公式、各项含义、门序与排查方法见下一节，本节不再复述。
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

**行号状态（2026-11-15，决定 (l) 落地时）：** 本节 `PickupAbsorbMath.cs:…` 的行号是在 (h) 落地时逐行核对过的，**体积口径改造后又漂了一次**（文件头与尺寸门相关段落重写过）：引用前请按**符号名**检索确认（`TryComputeItemVolume`、`IsVolumeBeyondLimit`、`TryComputeAbsorbVolumeLimit`、`IsBeyondAbsorbVolumeLimit`、`TryComputeReach`），不要照抄本节任何 `:行号`。`KatamariBallMotor.cs:…` 同样只核对了字段与判定调用两处。这不是新问题：行号在本仓已经漂过至少三次。

### 1. reach 公式

代码按如下顺序计算（`PickupAbsorbMath.cs:186`），比较在 `PickupAbsorbMath.cs:188`：

```
reach = collisionRadiusWorldM + contactRadiusWorldM + itemRadius
absorb ⇔ distance ≤ reach          // 距离取平方和再开方，中心到中心
```

`distance` 是球心到物件中心的三维欧氏距离（`PickupAbsorbMath.cs:168-176`），`itemRadius` 是物件的**包围球半径**（见第 2 节），三项全是米、全部相加、没有乘除、没有衰减。

| 项 | 代码 | 物理含义 | 谁喂它 |
|---|---|---|---|
| `collisionRadiusWorldM` | 参数 `:139`，加在 `:186` | 球当前**碰撞半径**（米），不是语义直径、不是核心半径 | 装配层在判定调用处传 `collisionRadiusWorldM`（`KatamariBallMotor.cs:749-753`），该字段每步由 `SizeModelMath.TryDerive` 从确认体积重算（`KatamariBallMotor.cs:905` 的 `DeriveSize`，公式在 `SizeModelMath.cs:179`：`collision = core × collisionToCoreRadiusRatio`） |
| `contactRadiusWorldM` | 参数 `:140`，加在 `:186` | 纯附加接触余量（米）。不是物件半径、不是容差、不随球长大 | 序列化字段，默认 `0f`（`KatamariBallMotor.cs:112`） |
| `itemRadius` | 局部量 `:184` | 物件包围球半径 √((x/2)²+(y/2)²+(z/2)²) | 由宿主 `SubmitCandidate` 传进来的 `bounds` 现算（`KatamariBallMotor.cs:1238` 存、`:752` 传），本层从不读 Collider |
| 尺寸门的物件侧（参数 `itemVolumeK`） | `EvaluateAbsorb` 的参数，用在尺寸门 | 原作的每件体积系数 `k`（`V = k·a·b·c`），**不参与 reach** | 序列化列 `PickupFieldHost.propVolumeK`（导入器从 `defs[].volumeK` 填，缺省 0.70），随候选 `SubmitCandidateWithVolumeK` 进电机；比较在 `IsBeyondAbsorbLimit`（见 1.1 节） |
| `maxGrowthRadiusWorldM` | 参数 `:142` | 成长上限门：碰撞半径到这个值就整体拒收，**不参与 reach** | 装配层传 `maxCollisionRadiusWorldM`（`KatamariBallMotor.cs:752`），该值 = `maxCoreRadiusWorldM × collisionToCoreRadiusRatio`（`KatamariBallMotor.cs:485`），默认为 `1.1 × 1 = 1.1` |

尺寸门与成长上限门**不是** reach 的项，它们只在 reach 之前把整个候选拒掉；把它们加进 reach 是“球够不够远”那类算错，把它们当同一件事（尺寸门 = reach 门）是另一类算错：尺寸门问的是“球够不够大”，与中心距无关。

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

`a·b·c` 是物件 `bounds` 的三轴乘积（AABB 体积，m³），`D` 是球的语义直径（m），`absorbVolumeRatio = 10` 表示**球体积必须是物件体积的 10 倍**。`(π/6)·D³` 就是球自己的体积，所以整条规则读作 `V_item ≤ V_ball / 10`，也就是 `V_ball ≥ 10·V_item`——原作的口径。

**证据等级：中。** [ABSORB_SIZE_RATIO.md](../../Research/KatamariBallRules/ABSORB_SIZE_RATIO.md) 有 14 条来源、逐条分级：一份社区数据挖掘直接陈述了 10× 体积判定与「Size to roll up = 能滚起它的最小球径」（S-AR01/S-AR02，**唯一**直接陈述常数 10 的来源）；9 个物件按门槛反算体积、8 个落在现实体积 ±25% 内（S-AR05 等）；三处独立旁证同向（Fandom 的「包围盒含空腔」与「铅笔比橡皮好吸」S-AR07、游戏内铅笔 4.3 cm < 橡皮 8.1 cm 的反例、玩家观测帖 S-AR12）。**常数 10 没有被第二份独立来源确认**：若真值是 8 或 12，所有门槛按 `(c/10)^(1/3)` 缩放，即 −7%～+6% 的球径，不改变口径结论。

展开（2026-11-16 起）：物件体积在 `PickupAbsorbMath.TryComputeOriginalItemVolume`（`k × a·b·c`），整 mm 在 `TryFloorMillimetres`，门槛在 `TryComputePickupReqDiameterMm`，比较在 `IsBeyondAbsorbLimit`（**唯一一处**，吸收门与挡路/支撑分类都调它）。「太大」在运行时只有这一份定义。

| 量 | 谁提供 | 语义 |
|---|---|---|
| `semanticDiameterWorldM` | `KatamariBallMotor.GetSemanticDiameterWorldM()`，与 `SizeModelMath.TryDerive` 的第三个输出同源 | 球**报告给玩家的**语义直径（起始 0.29974 m、满径 2.20 m）。**不是**碰撞半径；reach 仍用碰撞半径 |
| `volumeK` | 宿主 `PickupFieldHost.propVolumeK`（导入器从 `defs[].volumeK` 填，缺省 0.70），随候选经 `SubmitCandidateWithVolumeK` 进电机 | 原作的每件体积系数 `k`；合法范围 `0 < k ≤ 32` |
| `BallSemanticDiameter → ballMm` | `PickupAbsorbMath.TryFloorMillimetres`（`GetAbsorbGateDiameterMm()` 报告同一个数） | 球的整 mm 直径，门的一侧 |
| `k·a·b·c` | `PickupAbsorbMath.TryComputeOriginalItemVolume` | 原作的物件体积 `V`；门槛 `Req_mm = floor(1000·cbrt(60V/π))` |

**判定量是 AABB 体积，不是最长边、不是对角线、也不是包围球。** 与之相邻但**不同**的量是 reach 用的包围球半径 `√((x/2)²+(y/2)²+(z/2)²)`（第 2 节）：那个只决定「够不够近」，与「够不够大」无关，同一个 `bounds` 同时喂两边。细长物件是两者分离的典型——棒球棒 `0.02×0.02×0.85 m` 的 AABB 体积是 `0.00034 m³`，而它的包围球半径是 `0.425 m`。把最长边、对角线或包围球半径中的任何一个当体积代入，都是这里点名的算错。

**为什么必须换量而不是调系数（用户给的例子）。** 测试场景里黄色小方块边长 **0.05 m**、红色方块 **0.30 m**：**边长只差 6 倍，体积差 `(0.30/0.05)³ = 216` 倍。** 球能吃多少是**体积**账（球体积 ÷ 10），而线性口径只拿「最长边 vs 球直径」比长短——它嗓门里只有那个「6」，说不出 216 倍，也就在结构上无法回答「这个球够不够大」。换成系数语言更清楚：等效线性系数 `k = 可吸物件最长边 / 所需球径` **不是常数**（研究文档 §4：立方体 0.37、3:1 的砖 0.78、5:1 的扫帚级 1.09、10:1 的铅笔级 1.74），所以任何单一 `absorbSizeRatio` 都只能在一个形状比上对：口径错，调系数就只是在两个方向的误差之间挪。真实道具建模后差别更大：方糖 1.6 cm vs 野餐桌 150 cm，边长差约 94 倍，体积差约 `8×10⁵` 倍。这就是决定 (l) 把「判定量」换掉、而不是把 1.0 调成 1.25 或 1.5 的原因（后者的后果清单见 [SIZE_GATE_AUDIT.md](SIZE_GATE_AUDIT.md) §4–§5）。

**细长物件因此自动能吸，不需要按形状特判。** 棒球棒 `0.02×0.02×0.85` 的体积 `0.00034 m³`，0.50 m 球的限是 `0.006545 m³`，放行；旧线性口径要 `0.85 ≤ D × 1.0`，即 0.85 m 的球才吸得动——这正是「原作里最像『吃了比自己大的东西』」的那批道具（棒球棒、铅笔、扫帚）被关掉的原因。反方向的代价是**薄片几乎白送**：`0.1×0.07×0.002` 的落叶体积只有 14 cm³。这是原作的宽容方向（研究文档 §7.4），要收就收在作者侧（`bounds` 或 `@size=` 声明），不要为它加回一个最长边门。

**球类道具会被 AABB 算虚大 `6/π ≈ 1.91` 倍，这是忠实还原，不是 bug。** 球/椭球（种子、珠子、毛絮团、水果）的 AABB 体积是 `d×d×d`，比真实体积大 1.91 倍，于是这些道具需要约 `1.91^(1/3) = 1.24` 倍大的球才吸得动。原作本身就用包围盒、并把空腔算进去（S-AR07 原文「including any empty space」），所以这是同构而不是缺陷；要按「看起来的体积」校准，正确的口子是给每件道具存一个 `volume_m3`（研究文档 §6 推荐项 1 的后半句），不是把这一层改回长度。

**边界与非法值。** 比较是 `≤`（含等号）：体积恰好等于上限放行。`absorbVolumeRatio ≤ 0`、ratio / 语义直径 / 上界非有限、语义直径 `< 0` 一律 `RejectInvalidInput`。**ratio 配成 0 不是「谁都吸不到」的正常状态，是配置错误**：它大声拒绝（拒绝环里是 6），而不是悄悄把上限钳成 0。语义直径恰好为 0 会被接受、上限也是 0（什么都吸不到），那是尺寸读数没就绪，不是配置错误。**新增一条比 (h) 更严的守卫**：三轴乘积溢出成 infinity 的物件按 `RejectInvalidInput` 拒（以前比最长边不会溢出）；「装不下的体积」是坏数据，不是「特别大的物件」。

**比较严格性**（2026-11-16 起）：接触是 `≤`，距离恰好等于 reach 算吸附（测试 `PickupAbsorbTests.cs`：0.6 m 对 0.6 m reach 通过，0.6001 m 拒绝）；尺寸门是「**整 mm 相等就放行**」（`Req_mm == ball_mm` 可吸，少 1 mm 就拒）；成长上限门是 `≥`（恰等于上限就拒）。尺寸门的边界是**整数 mm**，所以它可以被精确地测试（`AbsorbSizeGateTests.AnItemExactlyAtReqIsAbsorbableAndOneMillimetreLessIsNot`）；唯一的 epsilon 是整 mm floor 前的 1e-3 mm 守卫，它只把浮点误差压到边界上的值抬回整数，不改变「少 1 mm 就拒」。

**序列化默认值（2026-11-16 起：门不再有任何序列化旋钮）。** 体积比率是常量 `PickupAbsorbMath.AbsorbVolumeRatio = 10`，物件上限是常量 `MaxItemVolumeSanityWorldM3 = 1000`（数值卫生，非玩法），逐件 `k` 来自关卡数据（缺省 0.70）。下面 2026-11-15 的两个字段已**删除**，场景里的残留键是 Unity 忽略的死键。

- `absorbVolumeRatio = 10f`。**这是玩法阈值**。原作判定的就是这个 10（证据等级中），不是本项目调的味；改成 5 等于「球只要比物件大 5 倍」，与研究的结论不再同构。
- `maxItemVolumeWorldM3 = 512f`。**这是安全上界**，不是玩法阈值：512 m³ 是「8 m 立方体」的读法，与 (h) 时代的 8 m 长度上界同量级、但换成了体积口径。长度上界配体积门会在 8 m 处硬截断，语义错——一条 0.02 m 见方、8 m 长的杆体积只有 0.0032 m³，却会被「8 m」钉死。
- **删掉的字段**：`absorbSizeRatio`（物件最长边 / 球直径 = 1.0）与 `maxItemSizeWorldM`（8 m 长度上界）。删而不是留兼容，理由有两条：长度上界与体积门拼在一起语义错（上一条），以及留一条旧字段等于留一条「能悄悄把闸门压回线性」的配置路径——(h) 已经因为场景里的残留覆盖值踩过一次。**迁移后果**：现存 lab 场景/prefab 里的 `maxItemSizeWorldM: 8` / `absorbSizeRatio: 1` 变成 Unity 忽略的死键，组件回落到新字段的 C# 默认值（512 / 10），所以**不改场景行为也已经是新口径**；清理死键是 Unity 侧的顺手活。
- `contactRadiusWorldM = 0f`。见下面第 1 节末与决定 (i)：0 是「必须实质接触」，不是「还没接」。

### 2. `itemRadius` 是包围球半径，不是半边长、也不是整边长

`PickupAbsorbMath.cs:178-184`：

```
itemRadius = √( (bounds.x/2)² + (bounds.y/2)² + (bounds.z/2)² )
```

立方体代入即 `√3·s/2 ≈ 0.8660·s`。三个都不一样的量，别混：

| 说法 | 立方体值 | 是不是代码用的 |
|---|---:|---|
| 半边长 `s/2` | 0.5·s | 否 |
| 整边长 `s` | 1.0·s | 否 |
| 包围球半径 `√3·s/2` | 0.866·s | **是** |

原因：`bounds` 传的是物件的**全长**（AABB 三个方向的总尺寸），不是半长，`PickupAbsorbMath.cs:46-54` 明确写了“full axis-aligned extents, not half extents”，尺寸门也是拿全长比（`:163-164`）。对全长取包围球，就是把三个半长当分量求模——这正是 Python 模型 `pickup_absorb.py:91` 的 `sqrt(sum((s/2)**2 for s in item.bounds))`，以及 `:92` 的“bounds proxy permits contact when bounding radius overlaps the ball reach”。包围球是**能装下整个盒子的球**，所以除了角点方向，它都比真实接触更早放行；面心方向最松，误差正好 `(√3−1)/2·s ≈ 0.366·s`（表中“多放行的量”）。

装配层实际传什么：`SubmitCandidate` 把宿主给的 `bounds` 原样存下（`KatamariBallMotor.cs:1238`）再原样喂给判定（`KatamariBallMotor.cs:752`）。实验室的宿主把它填成 `(semanticDiameterM, semanticDiameterM, semanticDiameterM)`（`PlaygroundProp.cs:110`），即立方体近似，**不是** Collider 尺寸。结论：**把道具 Collider 改小不会缩小 reach**，除非同时把 `bounds` 改小；反过来，`bounds` 填成大立方体，reach 与尺寸门同时变松/变紧，而画面上看不出任何变化。

### 3. 门序：首个失败者胜（枚举值不是门序）

`EvaluateAbsorb` 的检查顺序（`PickupAbsorbMath.cs:144-188`）。**注意：`AbsorbOutcome` 的编号（`:18-32`，`Absorb=0` 到 `RejectInvalidInput=6`、`RejectNoGain=7`）只是枚举值，不是执行顺序**——`RejectInvalidInput` 编号最大却最先执行。

| 序 | 判定 | 输入/触发 | 返回 |
|---:|---|---|---|
| 0 | 尺寸门的输入守卫（`EvaluateAbsorb`） | `volumeK` 非有限或不在 `0 < k ≤ 32`、语义直径/碰撞半径/接触半径非有限、碰撞半径或直径 `< 0`、bounds 负/非有限/乘积溢出 | `RejectInvalidInput` (6) |
| 1 | 输入守卫（非模型门，端口自加） | `itemKind == null` 或 `allowedKinds == null`（`:144`）；`allowedKindCount < 0` 或 `> allowedKinds.Length`（`:145-146`）；`itemBounds`/`itemPosition`/`ballPosition` 含非有限分量（`:147-149`）；四个标量参数非有限（`:150-154`）；`itemBounds` 任一分量为负（`:155-156`）；`collisionRadiusWorldM < 0`（`:157`）；以及计算中途 dx/dy/dz、distanceSquared、itemRadiusSquared、reach 非有限（`:171-175`、`:182-183`、`:187`） | `RejectInvalidInput` (6) |
| 2 | 可吸附标记 | `itemAbsorbable == false` | `RejectNotAbsorbable` (1) |
| 3 | 种类白名单 | `IsKindAllowed` 走完前 `allowedKindCount` 项都没按序数匹配上（`:160-161`，实现 `:425-433`） | `RejectKindNotAllowed` (2) |
| 4 | 尺寸门 | `floor(1000·cbrt(60·k·a·b·c/π)) > floor(1000·D)`（整 mm 比较，唯一实现在 `IsBeyondAbsorbLimit`；体积超过 `MaxItemVolumeSanityWorldM3` 也算「太大」） | `RejectItemTooLarge` (3) |
| 5 | 成长上限门 | `collisionRadiusWorldM ≥ maxGrowthRadiusWorldM`（含等于，`:165-166`） | `RejectBallAtGrowthCap` (4) |
| 6 | 距离 | 计算中心距（`:168-176`）与包围球半径（`:178-184`） | 仅用于下一步 |
| 7 | 接触 | `distance > reach` 则拒，否则吸（`:186-188`） | `RejectOutOfReach` (5) / `Absorb` (0) |

**早门遮蔽后门**——这是排查时最容易白跑的地方：

- 白名单为空或拼错时，每个候选都停在第 3 门，第 6/7 门的距离与 reach **根本没有算**（虽然装配层为了记拒绝事件另算了一次距离，见第 7 节），所以“距离看着在 reach 内”不能作为门序正常的证据。
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
5. **`valueUnits` 必须是整数**。`TryAbsorbValueToLedgerGain` 对小数、负数、非有限值、超出 int32 的值一律返回 `false`（`PickupAbsorbMath.cs:411-422`），两个模型都没定义取整规则，所以它不四舍五入。非整数价值的后果不是“少加一点”，而是这件道具永远提交不了、永久占槽。
6. **障碍注册与吸收判定无关**。`AddPlane` / `AddBox` 喂的是 `SweepMath`（`KatamariBallMotor.cs:989-1028`），本层的接触判定没有遮挡项（`PickupAbsorbMath.cs:80-84` 明说“models no occlusion”）。所以隔着一面已注册的墙，只要中心距够近，照样能吸——想让墙挡住吸收，本层没有这个能力，只能在宿主侧不提交。
7. **账本必须已在 `Playing`**：`TrySubmitClaim` 的阶段门在 `GrowthLedgerMath.cs:435`。否则每个候选都判定为“可吸”却提交失败，表现为“零拒绝、零吸收、队列满”（第 7 节用它区分账本问题）。
8. **候选收集、空间索引、道具生命周期不在本层**：本层不扫场景，只判定宿主递进来的一条。

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
| 3 | `RejectItemTooLarge` | **数据**：这件物件的 `Req_mm = floor(1000·cbrt(60·k·a·b·c/π))` 大于本步球的 `GetAbsorbGateDiameterMm()`，或它的体积超过 `MaxItemVolumeSanityWorldM3`，或宿主传的 `bounds` 比以为的大（它是全长；把 Collider 尺寸当半长传就会放大 8 倍体积） | 打印 `GetAbsorbGateDiameterMm()` 与 `PickupAbsorbMath.TryComputePickupReqDiameterMm(bounds, k)` 两个整数比；球长大时两者都涨。k 配错（0/70）会先被拒为 6。**不要比最长边**：0.85 m 的细杆 Req 只有 165 mm |
| 4 | `RejectBallAtGrowthCap` | **配置**：球已到 `maxCoreRadiusWorldM × collisionToCoreRadiusRatio`（默认 1.1 m），此后一律不收 | 看 `GetCollisionRadiusWorldM()`；这是尺寸上限，不是几何 |
| 5 | `RejectOutOfReach` | **几何**：中心距 > reach | 用 `GetRejectEventDistance()` 与手算的 `碰撞半径 + contactR + itemRadius` 比；`GetCollisionRadiusWorldM()` 给第一项，`bounds` 给第三项。差得多是位置/尺寸错，差一点点是包围球算错或 `contactRadiusWorldM` 与预期不符 |
| 6 | `RejectInvalidInput` | **配置或数据**：`kind` 为 null、`bounds` 有负分量、位置含 NaN/Inf（未接线的 Transform 会直接给 NaN）、位置或尺寸溢出、count 大于数组长度 | 打印原始输入，先找 NaN |

5. 距离**单调增大**且队列停在 4 → 就是第 5 节的冻结：宿主没有撤回。不是 reach 变紧。
6. 只看控制台不够：拒绝诊断日志全程只有 3 行（`KatamariBallMotor.cs:294`、`:1464-1485`，每次打印 `d`、`reach`、`itemR`、`ballR`、`contactR`、`allowedKindCount`），用完之后就不再输出。持续通道是拒绝环（8 槽，`:292`）加 `GetRejectEventDroppedCount()`（`:1519-1523`）。

### 与 `pickup_absorb.py` 的已知分歧

不是笔误，是两边的定位不同；记录在这里以免下次拿模型当依据。

| # | 项 | 代码 | 模型 | 影响 |
|---|---|---|---|---|
| 1 | 参数默认值 | `contactRadiusWorldM = 0f`（序列化）；门侧无序列化旋钮：比率/上限是常量，逐件 `k` 缺省 0.70（`PickupAbsorbMath.DefaultVolumeK`） | 无默认，构造 `PickupRules` 必须显式给（`pickup_absorb.py:74-78`，注释写明“deliberately no accepted/default gameplay tuning”） | 模型强迫调用方表态；运行时的门已把「比率」和「整 mm」钉成常量，剩下的逐件表态是 `defs[].volumeK` |
| 2 | 门粒度 | 不可吸附（`:130`）与种类（`:131-132`）是两个 outcome | 合并成一个 `if`（`pickup_absorb.py:83`） | 顺序一致，但模型无法区分这两个原因；“首个失败门即报告原因”对这一条在模型里不成立 |
| 3 | 非法输入 | 负 `bounds`、非有限值、null kind 一律先拒（`PickupAbsorbMath.cs:144-157`） | `eligible` 不检查；只有 `register` 检查 value/bounds（`pickup_absorb.py:42-43`） | 端口自加的守卫，代码注释 `PickupAbsorbMath.cs:13-16` 已声明；同一份输入两侧结论可能不同（模型放行、代码拒） |
| 4 | 候选生命周期 | 4 槽队列、每步重判、submit/withdraw/clear、无 TTL（第 5 节） | 完全没有对应物：`nearby()` 只返回候选（`pickup_absorb.py:59-69`） | 第 5 节的规则**没有参考模型**，属实现侧契约，不能拿模型反驳 |
| 5 | 半径口径 | reach 用 `collisionRadius = core × collisionToCoreRadiusRatio`（`SizeModelMath.cs:179`） | 只有一个 `radius = min(max, base + growth × confirmed)`（`pickup_absorb.py:141-142`），没有碰撞/核心比 | 默认 ratio = 1 时两侧数值一致；ratio ≠ 1 时模型没有对应项 |
| 6 | 尺寸门（跨层） | **原作整 mm 口径（2026-11-16）**：`Req_mm = floor(1000·cbrt(60·k·a·b·c/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`（1.1 节） | **仍是旧口径**：`pickup_diameter ≤ ball_diameter × absorb_size_ratio`（`round_model.py:493-502`，默认 ratio 1.0，`:119`），`pickup_absorb.py:85` 仍然是 `max(item.bounds) > max_item_size_world_m`（最长边） | **已不是同构**：模型是线性、运行时是整 mm 体积。两个模型都是 DRAFT/离线参考，本轮**没有**追平。拿模型反驳运行时结论之前先读这一行 |

### 仍然会坏的地方（读代码看到、未修）

1. **`contactRadiusWorldM = 0` 是承重的决定值**：它是“能不能吸到”的全部额外余量，改它直接等于改手感，而没有任何一处要求关卡必须给值。0 是决定 (i) 的口径（必须实质接触），不是占位；`maxItemVolumeWorldM3` 是安全上界，两者性质不同：前者是手感契约，后者是防误配的上界。
2. ~~**`maxItemSizeWorldM` 绝对值 + 不随球长大**~~ **已解决两次**：先是 (h) 让上限随球长大，再是 (l) 把量换成体积。**残余风险换了个形状**：(l) 删掉了旧字段，所以场景里残留的 `maxItemSizeWorldM: 8` / `absorbSizeRatio: 1` 是死键、会被忽略，组件取新默认值 512 / 10——**这正好是想要的**，但也意味着「某关卡曾经用场景覆盖把门槛调松/调紧」这件事再也不会生效，谁想按关卡调整现在只能改组件（或未来改成体积字段的覆盖）。`absorbVolumeRatio = 10` 本身仍是研究结论（证据等级中，常数 10 只有一份独立来源），不是实测。
3. **`bounds` 由宿主手填、与 Collider 无关联**：改 Collider 不改 reach；实验室把 `semanticDiameterM` 当三边填（`PlaygroundProp.cs:110`），那是立方体近似。
4. **4 槽 + 无 TTL**：任何系统性拒绝都会永久冻结队列，而信号只有“拒绝环”（几何类）或**什么都没有**（账本类）。
5. ~~**重复 id 无去重**~~ **已修（2026-11-14）**：`SubmitCandidate` 现在按 item id 幂等，重复提交返回 `true` 不占新槽；宿主因此不再需要本地闩锁。
6. **`SetAllowedKinds` 存引用不拷贝**（`KatamariBallMotor.cs:894`），且被 `Start()` 无条件重置（`:399-400`）——两个静默失效源。
7. **成长上限门的边界**：门用 `≥`（`PickupAbsorbMath.cs:165`），而 `SizeModelMath` 在“确认体积恰好等于上限体积”时走 `exp` 路径（`SizeModelMath.cs:165` 的 `logTotal > logCappedVolume` 是严格大于），理论上可能返回比上限小 1 ulp 的半径，那样满级球还能再吸一件。**这是读代码的推断，未实测。**
8. **`WithdrawCandidate` 用 `==` 比较字符串**（`KatamariBallMotor.cs:962`）。C# 语义是按值比较，UdonSharp 下是否同样按值**未验证**；若不按值，撤回会静默失效，退化成第 5 节的冻结。

**整数单位契约（有意为之，不是缺陷）**：`TryAbsorbValueToLedgerGain` 只接受整数单位（`(float)(int)v != v` 即返回 `false`）。小数值视为作者数据错误，由 `RejectNoGain` 大声拒绝并进入拒绝环/日志，**不要去"修"成四舍五入**——取舍是"数据必须整数"，让错误可见。

> 观感对照（吸附之后球长什么样：我们的红色球芯 vs 原版两帧的物件覆盖）：[`Docs/Evidence/BallPresentation/2026-11-15_attached_props_vs_original/`](../../Evidence/BallPresentation/2026-11-15_attached_props_vs_original/README.md)。
