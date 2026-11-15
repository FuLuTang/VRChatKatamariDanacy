# KatamariBallMotor 拆分提案（分析；第一批改动已落地，见下）

**状态：PROPOSAL。** 对象 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs`（2088 行，下称 motor），行号以本次工作区为准。依据 `Docs/Design/CODE_CONVENTIONS.md` 与 `Docs/Design/BallMotor/README.md`。

## 落地状态（2026-11-15，第一批）

**已落地三项，其余未动。未编译、未进 Unity、未跑 EditMode——与本文其余部分同一状态。** 改动只在私有实现内：44 个 `[SerializeField]` 的名字与顺序未动，被测试与场景引用的 public 方法名未动，§4 末尾列的反射钉子全部仍能解析。当前文件为 **2115 行**（改动前 2148 行；`+80 / −113`）。

| 项 | 结果 |
|---|---|
| S1（部分） | 删 9 个零读者取值器与其只写字段/写入（见下面「删除证据」）；`RecordReject` 的错位注释（原 1868–1877）移回 `RecordReject` 上方（现 2005–2014）。`GetAbsorbedEventPosition` **未删**：`Docs/Design/StoryMode/README.md` 按名字引用它，删掉会留下悬空引用，留给同时改那份文档的人 |
| S3（只做公式合一） | 新增 `PickupAbsorbMath.TryComputeReach`（`PickupAbsorbMath.cs:215`）；`EvaluateAbsorb` 的判定体（`PickupAbsorbMath.cs:184-189`）、motor 的追踪快照（`:752-777`）与拒绝日志（`:1810-1821`）全部改调它。顺带把「每个候选每步都算半径/reach」变成「只在该件被追踪或要打拒绝日志时算」。拒绝码与门序未变；非法 `bounds` 在诊断里报 `-1`（原本会打印 NaN） |
| S5 | `CompactPending(int write, int read)`（motor `:857`），`CommitAcceptedAbsorbs`（`:800`）与 `WithdrawCandidate`（`:1281`）的两处 9 字段复制合一，同下标直接返回 |
| 未动 | S2 `TryGrowthFraction`、S4 `RingBufferMath`（风险最高的一步）、S6 `StuckRecoveryMath`、S7 `TryReadMove2D`、S8 `TryProjectBankedScore`、§3 的「回合所有者」边界（账本/交分/奖励/`TryResetForNewRound` 搬迁）、`proxyRotation`（提案本身给了两个互斥选项，未定）、`PredictionCapacity = 0` 与四个零长预测数组（删字段要同步改 `GrowthLedgerMath` 调用签名） |

**删除证据**（全仓 grep，代码/测试/Docs/`Assets` 下场景与 prefab YAML 均计，2026-11-15 02:00 UTC，排除 `.git`、`.codegraph`）：`GetCoreRadiusWorldM`、`GetGrowthFraction`、`GetUnbankedVolumeUnits`、`LastSweepWasBlocked`、`LastSweepWasStuck`、`GetFollowRigWorldPosition`、`GetFollowRigYawDegrees`、`HasFollowRigFrame`、`GetAbsorbedEventDiameter`、`absorbedEventDiameters`、`lastSweepBlocked`、`lastSweepStuck` 在 motor 之外只命中本文 §5 与 `KatamariBallMotor.asset` 的 Udon 符号表（U# 编译产物，`refresh_unity` 会重生成；私有字段不进场景序列化，故无存档值丢失）。`GetAbsorbedEventPosition` 命中 `Docs/Design/StoryMode/README.md:187`，因此保留。

**改动后的行号锚点**（本文其余部分的行号仍是改动前的 2148 行版本，引用前请按下表或其上方符号名复核）：`StepBall :541`、`StepInput :715`、`EvaluatePendingCandidates :737`、`CommitAcceptedAbsorbs :800`、`CompactPending :857`、`TryCommitAbsorb :871`、`DeriveSize :922`、`RecomputeConfirmedAndSize :962`、`DeriveGrowthFraction :974`、`TryBeginPlaying :1026`、`TryResetForNewRound :1087`、`TryBankWithReason :1458`、`RecordAbsorbedEvent :1729`、`GetAbsorbedEventPosition :1789`、`RecordReject :2015`、`AbsorbedEventSlot :2104`。

另：`Runtime/Ai/CpuSkirmishBootstrap.cs:351` 还有**第四份** reach 写法（诊断读数，用 `localScale.x` 当立方体边长）。它不在本批写入范围，未改；要合一应改调 `PickupAbsorbMath.TryComputeReach` 并传入 `GetPendingCandidate*` 那件道具的 bounds。

## 0. 结论

1. motor 的问题不是「逻辑太多」，是「杂项太多」：2088 行里注释 626、空行 181、代码 1281；代码中字段声明 183 行、25 个只读取值器约 100 行。九步管线本体只有 141 行（529–669），八次 `Try*` 调用，没有公式。
2. 全文件真正内联的算术只剩 5 处（919–929、714–722 与 1788–1792、1698–1710 与 2045–2086、620–637、1892–1896），其余「内联」是事务编排（交分 1413–1471、提交 814–856、复位 1030–1117），不该变成数学。
3. **不建议新增任何 UdonSharpBehaviour。** U# 禁止可实例化的普通类（CODE_CONVENTIONS.md:25），状态只能住 Behaviour 字段；每加一个 Behaviour 就多一份 UdonSharpProgramAsset（现有 `BallMotor/KatamariBallMotor.asset`）与 `Assets/SerializedUdonPrograms` 产物，而跨 Behaviour 调用的字符串分发开销被明确排除在每帧/每采样点循环之外（CODE_CONVENTIONS.md:30）。
4. 该做的是：6 个纯静态抽取（5 处算术 + 1 处 Move2D 选择策略）+ 1 个私有压缩 helper（消掉两段逐字相同的队列复制）+ 删 3 类零读者代码 + 修 1 段错位注释。

## 1. 分区

| 行范围 | 行数 | 责任 | 判定 |
|---|---:|---|---|
| 1–68 | 68 | 类头 remarks，无代码 | 纯文档（54 行注释） |
| 70–144 | 75 | 43 个 `[SerializeField]` 调参字段 | 纯数据；场景/prefab 已按名存值，禁改名 |
| 146–361 | 216 | 全部可变状态：球标量、账本列、输入/扫掠缓冲、候选队列、两个事件环、追踪、奖励、溢出闩锁（141 行代码） | 纯声明 |
| 363–504 | 142 | `Start`：62 次数组分配、一次零体积定尺寸、`TryJoin`、自动进 Playing | 内联 119 行，只跑一次 |
| 506–527 | 22 | `Update`：定步累积、`maxStepsPerFrame` 夹取、每帧写 Transform | 内联 6 行算术 |
| 529–669 | 141 | 九步管线本体 | **薄**：8 次 `Try*`；仅 620–637 卡住恢复、654 实际位移是本层策略 |
| 671–702 | 32 | 第 1 步：`ControlIntentMath.TryProcess`，再挑 Move2D、菜单抑制 turn | 薄 + 内联 695–701「最后一个 Move2D 胜出」 |
| 704–746 | 43 | 第 5 步：逐候选 `EvaluateAbsorbWithValue`，写接受位、拒绝环、追踪 | 内联 714–722 重算物品半径/触达，**每候选每步都算**，追踪关闭时也一样 |
| 748–856 | 109 | 第 6 步：提交账本、写吸收事件、原地压缩队列 | 编排薄；内联 9 字段压缩块 797–808 |
| 858–930 | 73 | 第 7 步：定尺寸 + 体积恒等式 + 增长比 | 薄（`TryDerive`）+ 内联公式 919–929（README:199 缺口 4 已点名） |
| 932–959 | 28 | 第 9 步 + 表现层写 Transform | 薄 |
| 961–1137 | 177 | `TryBeginPlaying`、`TryResetForNewRound`、两个 getter | 前者薄（3 条账本边）；复位是 88 行过程（1030–1117） |
| 1139–1260 | 122 | 输入 seam、`SetAllowedKinds`、候选队列 API | 内联队列语义：1185–1188 幂等扫描、1239–1250 压缩 |
| 1262–1363 | 102 | 平面/盒子注册、`SignalOverflow`、`RebuildObstacles` 精确长度数组 | 内联；只在装载时跑 |
| 1365–1517 | 153 | `TryBank`/`TryBankWithReason`/`ApplyBankShrink`/`TeleportTo` | 编排；数学已在 BankShrinkMath.cs:53/84/106 |
| 1519–1683 | 165 | 25 个只读取值器（球/尺寸/扫掠/跟随架/队列/溢出） | 薄，一行 `return` |
| 1685–1771 | 87 | 吸收事件环：追加 + 绝对下标读 | 内联环算术 1698–1710、2077–2086 |
| 1773–1872 | 100 | 拒绝日志、追踪 5 个 getter、候选值 getter | 只诊断；1808–1817 是错位注释 |
| 1874–1924 | 51 | `TryAwardScore` + 2 个 getter | 内联守卫 1887–1896 + 账本规则 |
| 1926–2055 | 130 | 候选年龄/距离 getter、`LogCommitFailureOnce`、拒绝环 | 内联环算术 1988–1996、2045–2055 |
| 2057–2087 | 31 | `GetAllowedKindCount`、`AbsorbedEventSlot` | 薄 + 环槽映射 |

## 2. 内联逻辑下沉到静态 Math

| # | 位置 | 现在是什么 | 去处 |
|---|---|---|---|
| 1 | 919–929 | `(r−base)/(max−base)` 夹 [0,1] + 有限性 | `SizeModelMath.TryGrowthFraction`（README:199 指定的落点） |
| 2 | 716–722、1788–1792 | `itemRadius=‖bounds‖/2`、`reach=球+接触+itemRadius`；连同 PickupAbsorbMath.cs:155–165 全仓共三份 | `PickupAbsorbMath.TryComputeReach`，`EvaluateAbsorb` 内部改调它 |
| 3 | 620–637 | 卡住累加/清零 + 严格 `>` 超时 | `StuckRecoveryMath.TryAdvanceTimer` / `TryRecover` |
| 4 | 1698–1710、1988–1996、2045–2055、2077–2086 | 两套环：追加、`count % cap`、绝对下标→槽 | `RingBufferMath.TryAppend` / `TrySlotFromAbsolute` / `NextRetained` |
| 5 | 1892–1896 | `banked + points` 溢出守卫 | `GrowthLedgerMath.TryProjectBankedScore` |
| 6 | 695–700 | 「最后一个 Move2D 胜出」 | `ControlIntentMath.TryReadMove2D` |

```csharp
public static bool TryGrowthFraction(float coreRadiusWorldM, float baseCoreRadiusWorldM,
    float maxCoreRadiusWorldM, out float fraction);                        // SizeModelMath
public static bool TryComputeReach(Vector3 itemBounds, float collisionRadiusWorldM,
    float contactRadiusWorldM, out float itemRadiusM, out float reachM);  // PickupAbsorbMath
public static bool TryAdvanceTimer(bool sweepAccepted, bool sweepStuck, float dt,
    float currentSeconds, out float nextSeconds);                           // StuckRecoveryMath
public static bool TryRecover(float seconds, float timeoutSeconds, out bool recover); // 同上
public static bool TryAppend(int count, int capacity, out int slot, out int nextCount); // RingBufferMath
public static bool TrySlotFromAbsolute(int absoluteIndex, int count, int retained,
    int capacity, out int slot);                                            // 同上
public static bool TryProjectBankedScore(int banked, int points, out int projected); // GrowthLedgerMath
public static bool TryReadMove2D(ControlIntentKind[] kinds, float[] xs, float[] ys,
    int count, out float forward, out float lateral);                       // ControlIntentMath
```

另加一个**非数学**的私有 helper（纯搬运，不进 Math）：`CompactPending(int write, int read)`——797–808 与 1239–1250 `diff` 无差异，逐字重复 9 个字段的复制。

## 3. 该不该拆成独立 Behaviour

| 候选 | 判定 | 代价与依据 |
|---|---|---|
| 诊断：吸收环/拒绝环/追踪/两条限流日志（272–355、724–734、1685–1872、1988–2055，约 350 行） | **不值得** | 环要每次提交、每个拒绝候选每步写；拆成 Behaviour 就把它们变成每步 ≤4 次跨 Behaviour 调用（CODE_CONVENTIONS.md:30 明令禁止）。真要让诊断离开，正确做法是**删**（追踪只服务一个 Lab），不是搬 |
| 候选队列 | **不值得** | 队列是宿主每 tick 直接调的 4 槽缓冲（PickupFieldHost.cs:720/782/796）；搬运只加一跳，并把「队列是唯一所有者」拆成两个所有者（README:241 记录的 latch bug 就是这么来的） |
| 奖励表 | **不值得** | `TryAwardScore` 直接写 `bankedScores[localRow]`（1908–1909），那是账本状态；搬走就有两个写者，违反「一份状态一份所有者」（README:51 不变量 1）。它该跟账本走 |
| 交分/换算 | **不值得** | 必须是「先算计划→动账本→缩水落地」的原子事务（1401–1412、README:125–134）；跨对象拆开就产生半状态。数学已全在 BankShrinkMath |
| 障碍注册 | **不值得** | 注册只在装载时调用，但数组每步被 `SweepMath.TrySweepSphere` 读（569–575）；搬家等于每步跨对象取数组 |
| 账本 + 交分 + 奖励 + 回合复位（「回合所有者」） | **以后值得，现在不** | 这是唯一真实的边界：账本列（189–223）、交分、奖励、`TryResetForNewRound` 属于回合不属于球。但这条接缝**正在被接线**：工作区未提交的改动已经让 `StoryRoundController` 调 `GetRoundId()`/`TryResetForNewRound`/`GetLastResetLedgerFailure`（StoryRoundController.cs:368/379/384），此刻搬它会和这次改动正面相撞；另外 BankReasonTests 用反射直接摸 `RecomputeConfirmedAndSize`/`confirmedVolumes`/`localRow`（BankReasonTests.cs:99/109/112）。触发条件：重试接缝稳定落地 + T06 接口冻结 + 多行/远端账本落地时，与回合控制器一起搬 |

结论：**现在新增 0 个 Behaviour**；只增 2 个静态类文件（`BallMotor/RingBufferMath.cs`、`BallMotor/StuckRecoveryMath.cs`），其余 4 处进既有类，仍属 `VRCKatamari.Runtime` 一个程序集（CODE_CONVENTIONS.md:18）。

## 4. 风险排序的落地步骤

每步只改私有实现：43 个 `[SerializeField]` 名与 67 个 public 方法名一个都不动（场景/prefab 已按字段名存值，改名即丢值；`SerializedUdonPrograms` 还会产生签名 diff）。

| 步骤 | 动什么 | 必须保持 | 验证 |
|---|---|---|---|
| S1 | 修 1808–1817 错位 XML 注释；删 §5 的零读者代码 | 不动行为 | EditMode 全绿 + U# 编译 |
| S2 | `SizeModelMath.TryGrowthFraction`（纯新增）+ 919–929 改调 | 夹取与有限性语义 | 新用例进 `Tests/EditMode/BallLayers/SizeModelTests.cs`；`SizeCalibrationTests` |
| S3 | `PickupAbsorbMath.TryComputeReach`，删 motor 两份副本，714–722 移进「需要追踪/拒绝时才算」 | `EvaluateAbsorb` 的拒绝码与判定顺序（PickupAbsorbMath.cs:121–165） | `PickupAbsorbTests`；新用例钉三份合一 |
| S4 | `RingBufferMath`，两个环改调 | 绝对计数单调、占用可变（README:329–337） | 新用例覆盖环绕 + 复位后 `dropped` |
| S5 | `CompactPending` helper | 压缩顺序与 `pendingSubmitTimes` 一并搬 | `CandidateQueueIdempotencyTests`（:50–114）+ 新交错用例 |
| S6 | `StuckRecoveryMath` | 严格 `>`；扫掠被拒时不动计时（620–630） | 新单元用例；lab 手感目测 |
| S7 | `ControlIntentMath.TryReadMove2D` | y→forward、x→lateral 的适配约定（README:102） | `ControlIntentTests` |
| S8 | `GrowthLedgerMath.TryProjectBankedScore` | 拒绝顺序：先验行再烧奖励键（AwardOnceTests.cs:114） | `AwardOnceTests`；Economy 全组 |

Unity 侧每步的验收门是 `refresh_unity`（U# 全量编译 0 错误）+ `run_tests`（`VRCKatamari.Tests.EditMode`，21 个测试类）。注意反射钉子：`Start`、`TryBeginPlaying`、`RecomputeConfirmedAndSize`、`TryBankWithReason`、`TryAwardScore`、`SubmitCandidate`/`WithdrawCandidate`/`GetPendingCandidate*`，以及字段 `localRow`/`confirmedVolumes`/`bankedScores`/`unbankedVolumeUnits`/`semanticDiameterWorldM`/`cubicMetersPerVolumeUnit`/`baselineVolumeUnits`（CandidateQueueIdempotencyTests.cs:50–57、BankReasonTests.cs:74–120、AwardOnceTests.cs:124–141）。`SizeCalibrationTests.cs:36–40` 抄的是 motor 的默认常数，改默认值会红，这是有意的。

## 5. 可删 / 重复 / 只诊断

引用计数截止 2026-11-15 00:07 UTC。同一工作区还有别人未提交的改动（`StoryRoundController.cs`、`PickupFieldHost.cs`、新增 `StoryRetryButton.cs`），动删除前请重跑这一节引用的 grep。

| 对象 | 证据（全仓 grep，代码/测试/Docs 均计） | 建议 |
|---|---|---|
| 1808–1817 XML 注释 | 内容是 `RecordReject`（1988–1996）的说明，却贴在 `GetTraceCount`（1819）上方，隔 170 行 | 移回或删 |
| `GetAbsorbedEventPosition` 1759 / `GetAbsorbedEventDiameter` 1766 | 0 引用；`Docs/Design/StoryMode/README.md:187` 明确写「不用 GetAbsorbedEventPosition」（提交时快照太旧） | 删方法 + 数组 283–284 + 写入 1703–1704 |
| `GetCoreRadiusWorldM` 1538、`GetGrowthFraction` 1553、`GetUnbankedVolumeUnits` 1563、`LastSweepWasBlocked` 1583、`LastSweepWasStuck` 1588、`GetFollowRigWorldPosition` 1598、`GetFollowRigYawDegrees` 1603、`HasFollowRigFrame` 1608 | 全部 0 引用；没有测试或 HUD 读它们 | 删；做 HUD 时随功能加回 |
| `proxyRotation` 162 | 只在 `Start` 写一次（467/469），之后没有任何层更新，却作为 `GroundContactMath.TrySolve` 的旋转入参（605）——「看着是活的，其实是常量」 | 要么由 RadialProxy 层真正喂它，要么删字段传 `Quaternion.identity` |
| `PredictionCapacity = 0` 186 + 4 个零长数组（212–216、390–393） | 每次提交/复位都把空预测列交给账本（834、1065），账本还会**校验**它们（GrowthLedgerMath.cs:474）；预测路径在本层永不可达 | 加注释说明本层不预测，或删 4 字段（账本调用签名同步改） |
| `contactCenters…contactPenetrationDepths` 249–254 | 只写不读（571–573），唯一用途是 `contactCount >= ContactCapacity`（586） | 保留：README:197 缺口 2（墙面去速）要读 `contactNormals` |
| 追踪机制 320–333、724–734、1819–1847 | 唯一读者是 `Labs/BallPlayground/BallPlaygroundBootstrap.cs:330–335`；每次评估都白算距离与触达 | 保留到 lab 退役，退役时整块删（约 80 行） |
| `LogRejectDiagnosticsOnce` 1782 / `LogCommitFailureOnce` 1967 | 纯 `Debug.Log` 副作用，各限 3 行 | 保留；不是逻辑，别当死代码 |
| `absorbedCount` 169 vs `absorbedEventCount` 285 | 两个「吸收总数」：前者复位清零（1085），后者单调（README:329） | 保留但别再引入第三个 |

## 6. 未验证

- 本次只读：没编译、没进 Unity、没跑 EditMode。每步的 `refresh_unity` + `run_tests` 由 Unity 操作者执行。
- 分析期间工作区被并行改动（`StoryRoundController.cs` 在 00:06 UTC 接上了重试接缝），本文的行号与引用计数以该时刻为准；落地前需按最新文件复核 §1 的行区间。
- `python3 Docs/tools/validate_plan.py` 与 `python3 -m unittest discover -s Docs/tests` 只校验 tasks.json、Source 哈希与关卡/资产数据，不读本文。
- 「跨 Behaviour 更慢」依据 CODE_CONVENTIONS.md:30 与程序资产结构，未实测数字。
