# PickupFieldHost 每帧开销：热点、改法、估算

范围：`Runtime/Pickup/PickupFieldHost.cs` 及其纯数学（`PickupSpatialGrid`、`PickupCandidateSelection`、`BlockingPropMath`、`PickupAbsorbMath`）。
起因：Unity 消融剖析（蟹座关，球静止）里关掉蟹座 `PickupFieldHost`（511 行）降 ScriptUpdate 约 3.9 ms，吸收进行中的开销没测到。

数字的性质：下面的 Udon 操作数是读代码数出来的，毫秒是用那次剖析的 3.9 ms 反推出的"每个 Udon 外部调用约 70 ns"换算，**不是实测**。要 Unity 验证的项见文末。

## 1. 优化前每个扫描间隔做什么（间隔 `scanIntervalSeconds` = 0.05 s）

Udon 里每个数组读写、`Transform.position`、`Vector3.x`、每次 float 加减乘比较都是一次外部调用。511 行、一次扫描：

| 段 | 每行做什么 | 约外部调用/行 | 约次数/扫描 |
|---|---|---|---|
| `ScanProps` 循环 | `propStates[i]`、`submittable[i]`、`propTransforms[i]`、`.position`、`OfferProp`（6 次 `.x/.y/.z`、8 次算术、`IsFiniteFloat`） | ~35 | 511 × 35 ≈ 18k |
| `UpdatePropObstacles` 循环 | 两个候选数组各写一次 false、状态、可用、`propTransforms[i].position`、3 次分量、距离平方、`propHalfDiagonal[i]`、剪枝比较 | ~35 | ≈ 18k |
| `RefreshPropObbs` 里的候选合并 | 读两个候选数组、`||`、写第三个数组、计数 | ~10 | ≈ 5k |
| `BlockingPropMath.SelectNearest` | 校验遍历 1 次 + 补足 `while` 每取一个候选再整表遍历 1 次（k 个候选 = k+1 次 511 行遍历，每行 `candidate[i]`；候选行还重算盒距离并 `ContainsIndex`） | ~5/行/遍 | (k+2) × 511 × 5，k=3..12 时 13k–36k |
| 其它 | `IsBeyondAbsorbLimit` 对范围内每行做一次 `Mathf.Pow`（立方根） | | 少数行 |

合计约 5.5 万次外部调用/扫描，按 70 ns 约 3.9 ms，与剖析吻合。剖析时帧率 31 fps（>0.05 s），所以每帧都扫；90 fps 时每 4–5 帧扫一次，均摊小但是**尖峰**一样大。

固定开销（与行数无关）：约 10 次对电机的跨行为调用（`GetCenterWorld`、`GetSemanticDiameterWorldM` ×3、`GetCollisionRadiusWorldM`、`GetSupportSurfaceHeightWorldM`、`GetStepHeightLimitWorldM`、`GetPendingCandidateCapacity`、`GetPropObbsCapacity`、`IsPropProxyContactEnabled`）、最多 12 件选中盒的 `TryBakeProp`、`SetPropObbs`/`SetPropProxies` 一次整块重发。估计 0.2–0.3 ms/扫描。

吸收时：`ConsumeAbsorbedEvents` 每个事件 `FindPropIndex` 线性比 511 个字符串（≈1500 次外部调用，0.1 ms），再加 `AttachProp`（`SetParent`、`AttachRadialProxy` 跨行为调用、一条长 `Debug.Log`）。每次吸收还把 `nextBlockingScanTime` 置 0，当帧多做一次障碍扫描，所以"吸收进行中"等于"每帧一次全表扫描 + 吸收本身"。

## 2. 改法

核心：**只看球附近的行，且对每行只读一次、不重复算**。判定逻辑一条不改，只缩小"看哪些行"。

1. **静止行用缓存、运动行读 Transform**。Start 里读一次静止行的世界位置并建盒（`propWorldPositions`、`propMinima/Maxima`）；运动行（`propMobile[i]` 为真）每次扫描读 `Transform.position`，与原行为一致。
2. **XZ 均匀网格**（`PickupSpatialGrid`，格边长 `gridCellSizeM`=2 m，格数上限 4096）装静止小件；每格带成员中心的紧包围盒，查询按球心到该盒的距离整格剔除。半对角线 > 1.5 m 的静止大件不进网格（会撑大每次查询），放进**线性表**（仍用缓存位置）；运动行也在线性表。
3. **一次收集、两段共用**。`GatherNearRows` 每个 Update 至多一次：网格查询 + 线性表，按和两段完全相同的表达式算距离平方，留下"候选扫描范围内或障碍剪枝范围内"的行（`nearRows/nearDistSq/nearPositions`）。候选扫描和障碍分类只遍历这个短表。
4. **需求预计算**：每行的吸收要求 `Req`（`floor(1000·cbrt(60·k·abc/π))`）在 Start 算好（`propReqMm`），判定变成整数比较（`PickupAbsorbMath.IsBeyondCachedRequirement`，与 `IsBeyondAbsorbLimit` 同判定）。
5. **最近挡路件的选择只看候选列表**（`BlockingPropMath.SelectNearestAmong`）：一遍算距离存进按行索引的暂存，补足阶段只在候选列表里取最小，不再整表扫；距离相等取下标小的（原来靠升序遍历隐含，现在显式写进比较）。`SelectNearest` 保留为全表入口（测试/离线对拍用），内部转调列表版。
6. **`FindPropIndex` 先看本宿主排队的候选**（≤16 行），再回退线性。被吸收的件一定来自候选队列；item id 在一块场里唯一（创作规则拒绝重复 id），所以结果与整表找到的行相同。
7. **漂移巡检**（兜底）：每帧重读 8 个静止行，位置偏离 Start 超过 1 mm 就转成运动行并警告一次（含行号和修复提示）。

没改的：吸收门、候选入队/撤回、`SetPropObbs`/`SetPropProxies` 的每扫描整块重发（见"后续项"）、`AttachProp`。

### 谁会动道具的 Transform（运动行契约）

- `MotionHost`：写 `rowTransforms[i]` 的 `SetPositionAndRotation`。`propMobile` 列由导入器按下面的规则写。**约束：MotionHost 无论怎么降频/裁剪，运动行的世界位姿必须仍写在该道具自己的 Transform 上**（本宿主读 Transform，降频只是读到更旧的、MotionHost 上次写入的值，与改前一致）。不需要 MotionHost 暴露任何接口。
- `PropFallHost`（栈塌）：某行第一次下落写 Transform 前调用 `PickupFieldHost.MarkPropMobile(row)`（幂等；行从此按运动行读，终身不回静止）。
- 漂移巡检兜住"列没写全"的情况，但那只是安全网：巡检发现之前，宿主看到的是旧位置。

`propMobile` 的规则（`PickupFieldAuthoringRules.FindMobileMotionItemIds`，与 MotionHost 的 `rowWritesPose` 同口径）：有自己程序且写位姿的行（类别 ≠ none / dragged-along / wait-for-player）为真；骑行（parent 行存在、inherit ≠ none）的行当且仅当它的父行为真时为真。注意：骑在**不会动**的父件上的 rider 判为静止——MotionHost 对它写的是经单位变换合成的位姿，与 Start 位置至多差浮点舍入（~1e-7 m），不是逐位相同。
导入器在写完 MotionHost 后写这一列（`PlacementImporter.PropMobility.cs` → `PickupFieldSync.ApplyMobileDeclaration`），菜单重新同步则按 item id 保留，没有值的 id 记为运动（安全默认）。**列缺失或短于行数 = 所有行都是运动行**，即旧行为（仍受益于合并遍历和缓存 Req，不受益于网格）。

## 3. 判定与改前一致的依据

- 候选扫描：`OfferProp` 拆成"量距离"与 `OfferMeasured`（同一个判定核心）；对每行用与原来相同的表达式、相同的位置（静止行的缓存位置就是 Transform 会读到的值），`inRange` 只数 `submittable` 的行、`withinLimit` 只数通过尺寸门的行，与原一致。
- 最近 4 个的选择：`InsertNearest` 按 (距离平方, 行下标) 排序，等距取小下标。原来的升序遍历 + 严格小于恰好给出同一结果；现在与遍历顺序无关，所以网格的格序不影响结果。
- 障碍分类：剪枝上界、`IsBeyond` 判定、盒、`TryClassifyProp`、`alreadyAbove` 全部是原表达式；静止行的盒 Start 时用同一个函数、同样的位置和 `propBounds` 算好。
- 网格覆盖保证：查询返回的行是超集，包含所有中心到球心 3D 距离平方 ≤ R² 的成员。格范围向外各多取一格；整格剔除用的盒距离平方由浮点逐轴相减的单调性保证不大于盒内任意点的距离平方。R = max(激活半径, 剪枝半径 + 网格内最大半对角线)，所以每个"原来会被某段保留"的行都在候选表里，两段各自再用原来的精确判定筛一遍。
- 最近挡路件：`SelectNearestAmong` 与全表版同一规则（先保留上一批、后按距离补足、等距取小下标、`distance == radius` 算半径内），候选列表顺序不影响结果。
- 测试：`PickupSpatialGridTests`（10 条，含与暴力解逐点对拍的覆盖性质、分区、退化/非法输入）、`PickupRowIndexEquivalenceTests`（7 条：缓存 Req 与门判定在 7000+ 个输入组合上一致；排序与顺序无关；`SelectNearestAmong` 与改前算法的内联副本在 400 组随机输入上逐位相等；网格+线性表遍历与全表遍历在 8×60 个随机查询上选择、计数、剪枝行集合完全一致）、`PickupMobileColumnTests`（6 条：运动行规则、保留规则）。原有 `PickupCandidateQueueTests`、`BlockingPropMathTests` 等一条没改。

## 4. 预期降幅（估算，待 Unity 实测）

蟹座：511 行，其中约 100–180 行运动（placement.v2.json：有 motion 113 件，parent+inherit 71 件，按上述规则约 100–150 件为真），约 40 件半对角线 > 1.5 m 的静止大件走线性表，其余约 300 行进网格。球半径 ≤ 1.1 m（成长上限直径 2.2 m），剪枝半径 ≤ 2.2 m。

每扫描（外部调用数，70 ns/次）：

| 项 | 次数 |
|---|---|
| 网格查询：约 12 个非空格 × ~20 + 约 45 行 × ~17 | ≈ 1k |
| 线性表：约 150 运动 + 40 大件，每行 ~20（读 Transform、距离、上界比较） | ≈ 3.8k |
| 候选扫描 + 障碍分类（近处约 30–60 行，每行 ~25） | ≈ 1.2k |
| 最近挡路件选择（候选 k ≤ 40，一遍距离 + ≤12 次列表取最小） | ≈ 2k |
| 固定开销（电机跨行为调用、烘焙、重发） | ≈ 3.5k（0.25 ms） |
| 漂移巡检（每帧） | ≈ 0.2k |

合计约 1.2 万次 ≈ **0.8 ms/扫描**，其中约一半是固定开销和运动行读数。球静止、每帧都扫（剖析时的 31 fps）≈ 0.6–0.8 ms，对比 3.9 ms 降约 80%；90 fps 时每 4–5 帧扫一次，均摊 ≈ 0.15–0.2 ms/帧，扫描帧的尖峰同样降到 ~0.8 ms。**≤0.8 ms 的目标在估算里刚好够，裕度主要取决于运动行数**；若实测超出，下面"后续项"的运动行分级是下一步。

吸收进行中：扫描成本不随球是否在动变化（无状态收集）；每次吸收额外 ≈ `FindPropIndex` 从 0.1 ms 降到 ~20 µs，`AttachProp` 不变（≈0.2–0.3 ms，含一条诊断日志）。吸收触发的额外障碍扫描现在也是上面那 ~0.8 ms 而不是 ~4 ms，所以"连吃多件"的每帧开销从 ~4.3 ms/件降到 ~1.1 ms/件的量级。球长大后剪枝半径变大，近处行数增加，网格查询随之线性增长，但被球半径上限（1.1 m）封顶。

## 5. 后续项（不在本次范围）

- **`SetPropObbs`/`SetPropProxies` 每扫描整块重发，而电机的这两个入口每次把 `windowPreviousCount = 0`（重置迟滞窗）**。所以"集合没变就不重发"会改变电机行为，本次没做。重发本身约 0.1 ms/扫描，且每 0.05 s 重置迟滞窗是否本意，需**电机负责人决定**。
- 运动行分级：运动行每次都读 Transform（~0.2 ms/扫描）。若 MotionHost 能给每行"离出生点最远能走多远"的包络，就能把有界运动（sway/bob/spin/hop）也放进网格（按包络扩格），只让漫游类每次读。需要 MotionHost 侧接口（导入器可离线算包络），暂不提出。
- 静止件的烘焙缓存：选中的 ≤12 件每扫描重算 `TryBakeProp`（~50 µs），静止件的结果可按行缓存。
- `AttachProp` 的诊断日志每次吸收一条（十余次字符串拼接 + `Debug.Log`），连吃时可限频；日志格式有人解析，未动。

## 6. 只能在 Unity 里验证的项

1. U# 编译：新用到的 `float.PositiveInfinity`（`BlockingPropMath.SelectNearestAmong`）、静态类里的 `out int`、`PickupSpatialGrid` 的 8 参数静态调用。
2. `PickupFieldPropObbTests`、`PickupFieldOwnSizeTests`、`PickupCandidateQueueTests` 的宿主部分（GameObject 版）通过——它们走"列缺失 = 全运动"路径。
3. 重新导入蟹座，确认同步报告里 `mobile` 行数、`[PickupFieldHost] ready ... index=grid gridRows=… linearRows=… mobileRows=…` 这一行，且没有 "moved … without being marked mobile" 警告。
4. Profiler 对照：同一场景同一姿势，关/开蟹座 `PickupFieldHost` 的 ScanUpdate 差；再测满推吸收（要真正吃到东西）时的差。
5. 行为对照：同一输入下 `scan inRange/withinLimit/selected/queued` 日志、吸收顺序、挡路件数（`GetBlockingPropCount`/`GetSupportPropCount`）与改前一致；MotionHost 降频后运动行附近的挡路/吸收无异常。
