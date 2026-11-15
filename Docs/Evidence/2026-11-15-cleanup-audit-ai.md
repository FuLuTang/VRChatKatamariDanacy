# 清理审计：`Runtime/Ai/` 与 `Editor/`

- 日期：2026-11-15
- 审计者：cleanup-audit-ai（**只审计，不改任何代码**）
- 状态：**已完成主体**（末尾「未覆盖」列出未查的部分）
- 唯一写入文件：本文件

## 审计范围（严格限定，未扩）

1. `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Ai/`
2. `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/`

未碰：Unity 编辑器、场景/prefab/`.asset`（只读 grep）、git 写命令、`tasks.json`、任何 `.cs`。

## 方法

- 调用图用 CodeGraph（`codegraph_explore`，`projectPath` = 仓库根），不只看 grep。
- grep 作为**交叉验证**，并逐项标注它是否可信：`SendCustomEvent("名字")`、`SetProgramVariable` 字符串、序列化字段、反射都超出 grep 视野。
- 本仓库现状（已核实，供所有条目共用）：`Runtime/Ai/` 里**没有**任何 `SendCustomEvent` / `SetProgramVariable` / `GetProgramVariable` 调用；全 `Assets/` 内只有 `Runtime/Story/StoryRetryButton.cs` 与 `Labs/HubLoop/PlayerTeleportProbe.cs` 用字符串事件，且都不指向 `Ai/`。因此**对 `Ai/` 内符号，grep 的"0 引用"是可判死刑的**（唯一例外见 A2/A3 的序列化说明）。
- git 只读：用 `git log -S` / `git show` 追溯每个符号的引入点。

## 结论速览

| 编号 | 符号 | 判定 | 批次 |
|---|---|---|---|
| A1 | `CpuSkirmishBootstrap.residualPercent` | 可删（编译器死代码，行为不变） | B1 |
| A2 | `CpuBallPilot.chargeForward` | 可删，但删了会动 `.asset`/场景 YAML | B1 |
| A3 | `CpuBallPilot.turnaroundDegrees` | 可删，同上 | B1 |
| S2-1 | `CpuSkirmishBootstrap.Start()`（空方法体） | 保留（注释是唯一价值） | — |
| T1 | `PickupFieldAuthoringRules.ClassifySizeTier` 的 0.21/0.76 长度分档 | **改口径**：它已不再是吸入门槛，但仍决定价值档 | B2 |
| T2 | `PickupFieldAuthoringRules.SortHierarchyPaths` | 生产零调用者（仅测试反射调用）→ 删或真正接入 | B3 |
| T3 | `MinValueUnits`（Editor 与 Runtime 各一份） | 合并/说明 | B3 |
| D1 | `CpuSkirmishBootstrap` 的 `[round]` 日志 + `ProbeBank()` 三处重复 | **缺陷**，不只是冗余 | B4 |
| D2 | `ProbeBank` 里硬编码的 sizing/residual 副本 | 合并（改为读取电机的值或删掉 `expected`） | B4 |
| D3 | `QueueFacts` 自算包围球半径/reach | 合并/改口径（与 `PickupAbsorbMath.TryComputeReach` 冲突） | B4 |

可删项合计：**3 个确定可删**（A1/A2/A3）+ **1 个候选删**（T2，需连测试一起改）+ **1 个缺陷必须修**（D1）。分 **4 批**。

---

## 一、编译器认证的死代码（三项）

### A1. `CpuSkirmishBootstrap.residualPercent`

- **目标符号**：`CpuSkirmishBootstrap.residualPercent`（局部变量，`ProbeBank` 内）
- **原本是干什么的**：`ProbeBank` 每球每秒做一次 `TryBankWithReason`，并在日志里打出"用同一套公开数学算出的预期缩球后直径"。这个局部变量是**残余比例的百分数形式**（`int residualPercent = 25;`），紧邻的 `float residual = 0.25f;` 才是真正喂给规则的那个。规则原文见 `Docs/OPEN_DECISIONS.md`（a）：`post = max(start, start + (pre - start) * residual)`，`residual = 0.25`（PROVISIONAL，本项目自选）。`BankShrinkMath.TryPostBankDiameter(startDiameterM, preBankDiameterM, residualFraction, out postBankDiameterM)` 收的就是**分数**（并在 [0,1] 内 clamp），不是百分数。
- **现在的值从哪来**：字面量 `25`，硬编码在 `ProbeBank` 里；与电机自己的 `KatamariBallMotor.bankResidualFraction = 0.25f`（`KatamariBallMotor.cs:158`，场景里三个球也都是 `0.25`）同值但**两份独立副本**。`residualPercent` 自己不参与任何计算：`TryPostBankDiameter` 收到的是 `residual`（0.25f），日志里打的也是 `residual`。
- **证据**：
  - 唯一赋值点：`CpuSkirmishBootstrap.cs:253`（`int residualPercent = 25;`）。
  - 读取点：**0**。同一函数内 `residual` 被读 2 次（喂 `TryPostBankDiameter`、拼日志），`residualPercent` 一次都没有 → 这正是 CS0219。
  - CodeGraph 把 `ProbeBank` 列为唯一持有者，`SizeModelMath.TryDerive` / `BankShrinkMath.TryPostBankDiameter` 的调用者里都不含它。
  - 全 `Assets/` grep（`--include=*.cs` 与不带 filter 都跑过）该名字只出现在这一个 `.cs`；`.asset`/场景/prefab 里**没有**（局部变量不会被序列化，符合预期）。
  - git：`git log -S"residualPercent"` → 只有 `453fd91`，即它从引入那天起就没被读过；`git show 453fd91` 可见它与整个 `ProbeBank` 一起新增。
- **grep 是否可信**：**可信**。局部变量不参与 Unity 序列化、不进 Udon 程序变量表、不可能被 `SendCustomEvent`/反射按名字访问。这里没有任何"grep 看不见"的通道。
- **风险**：零。删掉它不改变任何取值、任何日志、任何场景数据（局部变量本就无法在 Inspector 里看见）。唯一的"风险"是如果有人本来打算把日志改成百分数口径 —— 那也该直接删掉百分数版本，而不是留个没人读的 int。
- **是不是"以后要接"的接口**：**不是**。局部变量跨调用不存在，无法"以后接"；要接只能用 `residual`。
- **建议**：**删**。顺手把 `residual` 的来源写清楚（见 D2：它其实是电机序列化值的副本，应该消掉或标注）。
- **批次**：B1（编译警告清理）。

### A2. `CpuBallPilot.chargeForward`

- **目标符号**：`CpuBallPilot.chargeForward`（`[SerializeField] private float`，`Header("Steering")` 组内）
- **原本是干什么的**：旧"停车—对准—直冲"转向设计的**冲刺速度旋钮**。类的 remarks（`Steer` 的 summary，`CpuBallPilot.cs:178-191`）记录了那套设计被实测否掉的原因：reach 只有 ~0.28 m，"保持 forward=0、转到 8° 死区内再走"会让球一走就丢航向、|v| 在 0.11 / 0.76 / 0 之间摆动而距离卡在 0.38–0.49 m。现行 `Steer` 换成"锥内锁航向 + 全速穿过"，冲刺速度写成字面量 `1f`（`motor.SetMoveAxes(1f, 0f, 0f)`），转向量由 `turnFullScaleDegrees` / `turnDeadbandDegrees` 决定，唯一的调速旋钮是 `adjustForward`。所以 `chargeForward` 是**被替换掉的那套设计的残留旋钮**，当前代码读到它 0 次。
- **现在的值从哪来**：类的 field initializer `0.6f`；场景 `Labs/CpuSkirmish/CpuSkirmish.unity` 里三个 pilot 组件各存一份 `chargeForward: 0.6`，**等于默认值**，没有调过的值会丢。
- **证据**：
  - `CpuBallPilot.cs` 内读取点：**0**（CS0414 的直接来源）。CodeGraph 的 `CpuBallPilot` 成员里，`Steer` 只调 `GetHeadingDegrees` / `SetMoveAxes`，不碰这两个字段。
  - 全 `Assets/` grep 命中 3 类文件：`CpuBallPilot.cs`（声明）、`CpuBallPilot.asset`（UdonSharp 程序资产的字段表，`Data: chargeForward` 条目）、`CpuSkirmish.unity`（3 个组件的序列化值）。**没有任何 `.cs` 读取或写入它**。
  - git：`git log -S"turnaroundDegrees"` 与文件历史都只有 `e13c84e`（"Let three CPU balls play the same rules as the player"），且 `git diff e13c84e HEAD -- .../Ai/` 对 `CpuBallPilot.cs` **无改动** —— 即该字段**从落地那天起就是死的**，编译器自那以后一直在报 CS0414。
- **grep 是否可信**：**这里不能只靠 grep，必须人工确认三件事**（已确认）：
  1. **序列化字段可被 Inspector 改** → 值可能非默认。已核对场景 YAML：三个组件都是 `0.6`，与默认相同，删字段不会有"悄悄丢掉某个调过的值"的问题。
  2. **Udon 可按名字读写私有字段**（`SetProgramVariable("chargeForward", …)`）→ grep 字符串能覆盖**字面量**形式；本仓库 `Runtime/Ai/` 内 `SetProgramVariable` / `GetProgramVariable` / `SendCustomEvent` 调用数 = 0（全 `Assets/` 也只有 Story/HubLoop 两处，与 `Ai/` 无关），且不存在字符串拼接构造变量名。判定：**需人工确认项已排除，可判死刑**。
  3. **`CpuBallPilot.asset` 是生成物**：删字段会改变该 `.asset` 的字段表 → 该文件会重编译/重序列化（见风险）。
- **风险**：**中等，而且是流程性的，不是行为性的**。行为零变化（无人读）。但删 `.cs` 里两个字段会连带：
  - `Runtime/Ai/CpuBallPilot.asset`（UdonSharp 程序资产）重新生成 → `Docs/Evidence/` 与仓库里会多出 `.asset` 签名/字段表 diff；
  - `Labs/CpuSkirmish/CpuSkirmish.unity` 里 3 个组件的两条 YAML 键变成"未知字段"，Unity 下次保存场景时静默丢弃 → 场景 diff；
  - 都必须由 Unity 侧重编译 + 重存场景才能收敛，无法纯文本完成。
- **是不是"以后要接"的接口**：**不是**。没有任何注释、TODO 或设计文档说"以后要接"；相反，类的 remarks 明确写了那套设计**已被实测否决并替换**。保留它不会"留着以后接"，只会让下一个读代码的人以为转向还有两个没生效的旋钮。
- **建议**：**删**（与 A3 一起，一次改完，一次重编译）。删除前把 remarks 里那段"为什么停车转身失败"保留 —— 那段才是有价值的证据。
- **批次**：B1。

### A3. `CpuBallPilot.turnaroundDegrees`

- **目标符号**：`CpuBallPilot.turnaroundDegrees`（`[SerializeField] private float`，与 A2 相邻）
- **原本是干什么的**：旧设计里"判定需要掉头"的角度阈值（90°）—— 航向误差超过它就先原地转向再冲。现行 `Steer` 用的是 `aimConeDegrees`（12°，判定"锥内锁航向"）、`turnFullScaleDegrees`（45°，把角度误差映射成转向量）和 `turnDeadbandDegrees`（8°，转向死区），三者都在读；`turnaroundDegrees` 不在其中。
- **现在的值从哪来**：类的 field initializer `90f`；场景三个 pilot 组件各 `turnaroundDegrees: 90`，**等于默认值**。
- **证据**：与 A2 同构 —— `CpuBallPilot.cs` 内读取点 0；全 `Assets/` 只命中声明 + `.asset` 字段表 + 场景序列化值；引入提交 `e13c84e` 后该 `.cs` 无任何改动。
- **grep 是否可信**：同 A2，三项人工确认已做（场景值 = 默认值 90；无字符串/反射访问；`.asset` 是生成物）。
- **风险**：同 A2（零行为风险；`.asset` + 场景重序列化的流程成本）。
- **是不是"以后要接"的接口**：**不是**，理由同 A2。注意它与 `turnFullScaleDegrees = 45` 极易被误读成"一对"（都是转向角度），实际只有后者在生效 —— 这正是保留死字段的真实代价：制造了两个看起来成对的旋钮。
- **建议**：**删**（与 A2 同批）。
- **批次**：B1。

> B1 备注：A1 只动一个 `.cs`，不涉及 `.asset`；A2/A3 必须与 Unity 重编译同批完成，且要接受一个 `.asset` + 一个 `.unity` 的重新序列化 diff。若本次不想动场景，可**只做 A1**，把 A2/A3 留到任何一次必须重存 `CpuSkirmish.unity` 的改动里顺手做。

---

## 二、`Runtime/Ai/` 零调用者的公开成员

**结论：`Runtime/Ai/` 没有零调用者的公开 reader。** 清单与计数如下（CodeGraph + grep 双向核对，`grep -rn "\.<名字>("` 计数）：

| 公开成员 | 定义位置 | 调用者数 | 位置 |
|---|---|---|---|
| `CpuBallPilot.GetPilotName` | `CpuBallPilot.cs` | 1 | `CpuSkirmishBootstrap.Update` 的日志拼装 |
| `CpuBallPilot.GetTargetName` | 同上 | 3 | 同一日志行、`QueueFacts`、`TargetReject` |
| `CpuBallPilot.GetHorizontalErrorM` | 同上 | 1 | 同一日志行 |
| `CpuBallPilot.GetHeadingDeltaDegrees` | 同上 | 1 | 同上 |
| `CpuBallPilot.GetForwardInput` | 同上 | 1 | 同上 |
| `CpuBallPilot.GetTargetSwitchCount` | 同上 | 1 | 同上 |
| `CpuBallPilot.GetTargetTransform` | 同上 | 1 | `QueueFacts`（取 target 算 reach） |
| `CpuSkirmishBootstrap.Update` | `CpuSkirmishBootstrap.cs` | Unity 消息 | — |
| `CpuSkirmishBootstrap.Start` | 同上 | Unity 消息，**方法体只有注释** | — |

- 这些 getter 被 `docs` 注释称为 "Read-only seams; the scene host formats the one line per ball"，属于**有意的公开缝**：宿主（`CpuSkirmishBootstrap`）打一行日志、pilot 自己不打日志。它们都只有 1 个调用者，但那个调用者是活的、且是设计意图，**不是死代码**。判"零调用者"在这里会误杀，故不列入删除。
- **S2-1 `CpuSkirmishBootstrap.Start()`**：`public void Start()` 方法体只有一段 "Ordering note: the motor's Start may run after this one…" 注释。它是 Unity 消息，**保留**：删掉它等于删掉这条"为什么不能在 Start 里碰电机"的现场说明（同类说明在 `Update` 的 `initialised` 分支里也有一份更长的）。若想减噪，可把注释搬到 `initialised` 字段上再删方法 —— 但收益极小。
- 反向确认（避免漏判）：`Runtime/Ai/` 里**没有**任何 `SendCustomEvent` 字符串、`SetProgramVariable`/`GetProgramVariable` 按名访问，也没有被反射调用的成员（`Assets/` 中唯一的反射式 `Invoke("…")` 在 Editor 测试里，指向 `PickupFieldAuthoringRules`，见 T2）。所以第二节的"0 调用者"判定对 `Ai/` 是可靠的。

---

## 三、`Editor/` 与运行期口径不一致的规则

### T1. `PickupFieldAuthoringRules.ClassifySizeTier` 的长度分档（`SmallTierMaxExtentM` / `MediumTierMaxExtentM`）

- **目标符号**：`PickupFieldAuthoringRules.SmallTierMaxExtentM`、`PickupFieldAuthoringRules.MediumTierMaxExtentM`（被 `ClassifySizeTier` 消费）
- **证据（谁在用这两个常量）**：**只有一处生产代码** —— `PickupFieldAuthoringRules.ClassifySizeTier` 本身（`longest <= SmallTierMaxExtentM` → `"s"`；`<= MediumTierMaxExtentM` → `"m"`；否则 `"l"`）。`ClassifySizeTier` 的生产调用者有 **2 个，都在 `Editor/Pickup/PickupFieldSync.cs` 的 `ValidateCandidates` 里**：
  1. 按 renderer AABB 分档（未声明 `@size=` 时）；
  2. 声明了 `@size=<米>` 时，把尺寸换成该边长立方体后**重新分档**，并在报告的 note 里写 `size tier aabbTier -> tier`。
  然后档位进入 `LookupValueUnits(kind, tier)`（`ValueTable`：s=1、m=4/6、l=16）→ `HasValueRow` 不通过就**拒绝该 prop**（"no value-table row for kind … + size tier …"）→ 通过则写进 `PropRow.ValueUnits`。
  第三个消费者：`Tests/EditMode/Pickup/PickupFieldAuthoringRulesTests.cs`（以及 `PickupFieldSyncReport` 只打印计数，不参与判定）。
- **口径冲突事实**：
  - **运行期已经是体积门**：`PickupAbsorbMath.TryComputeAbsorbVolumeLimit(ballSemanticDiameterWorldM, absorbVolumeRatio, maxItemVolumeWorldM3, out limit)` 用 `π/6·d³ / absorbVolumeRatio`（再受 `maxItemVolumeM3` 封顶）与物品 **AABB 体积** 比；`EvaluateAbsorb` 走的是 `IsVolumeBeyondLimit(itemVolumeWorldM3, …)`。物品的 `propBounds` 也是编辑器写的 `pickSize`（AABB 或 `@size=` 立方体）。
  - **编辑器侧仍是长度门**：档位只看**最长边**，与体积无关。所以一个 1.2 × 0.1 × 0.1 m 的细杆按长度是 `l`，但体积 0.012 m³，小球的体积门会放行；反过来一个 0.7 × 0.7 × 0.7 m 的方块按长度是 `m`，体积 0.343 m³，小球会被体积门挡住。两套口径**对同一件 prop 给出相反结论**，且**不会报错**。
  - 这一点**项目已知且已记录**：`Docs/OPEN_DECISIONS.md` 明确写"`Editor/Pickup/PickupFieldAuthoringRules.cs` 的 `SmallTierMaxExtentM = 0.21` / `MediumTierMaxExtentM = 0.76` 分档与 `@size=` 声明尺寸 —— 全是**长度**口径（档位是内容侧的分档，不是运行时门槛）… **这是本条落地后第一个要复核的地方**"；`Docs/Design/PickupAbsorb/SIZE_GATE_AUDIT.md` 亦记"档位分档不受影响…与 ratio 无关"。
- **grep 是否可信**：**可信**。这两个 `const float` 只可能被代码直接引用；Const 不会被序列化，也不参与 Udon（editor-only asmdef，`includePlatforms: ["Editor"]`，且引用 `VRCKatamari.Runtime` 而不被运行期程序集引用），不存在被字符串/反射读取的路径。
- **风险**：改口径本身低风险（纯编辑器工具，不碰运行期）；**不改**的风险是内容侧持续按长度档定价值，与体积门脱钩后"看起来该吃得下却吃不下 / 该吃不下却吃下"两种困惑都会出现，而且价值（分数与球增长）与可吃性不再是同一个变量。
- **建议**：**改口径，不要删**。
  - 分档的**用途**已经变了：它不再回答"能不能吸"，只回答"这件 prop 值多少单位"（`ValueTable` 的 s/m/l = 1/4-6/16）。所以最诚实的做法是把它**重命名为价值档**（例如 `ValueTierFor`）并在注释里明确"这不是吸入门槛，门槛是运行期体积门（`PickupAbsorbMath.TryComputeAbsorbVolumeLimit`）"，避免下一个人再把它当门槛读。
  - 若要让价值与体积门同口径，则应改成分档依据 **AABB 体积**（或直接对 `propBounds` 用同一 `TryComputeAbsorbVolumeLimit` 的表达式做一次"最小可吃球径"列），并把 `ValueTable` 的边界重标定 —— 这会改所有已同步 prop 的 `valueUnits`，属于内容变更，需单独立项、不可与清理混做。
  - **本轮只做"改注释/改名，不动数值"**，把体积口径列为后续任务。
- **批次**：B2（编辑器注释/改名，不动数值、不动场景）。

### T2. `PickupFieldAuthoringRules.SortHierarchyPaths` —— 规则存在，但没人执行

- **目标符号**：`PickupFieldAuthoringRules.SortHierarchyPaths`
- **证据**：生产调用者 **0**。唯一的调用者是 `Tests/EditMode/Pickup/PickupFieldAuthoringRulesTests.cs`，而且是**反射调用**：`(string[])Invoke("SortHierarchyPaths", new object[] { paths })` —— 这正是"grep 看不见的调用者"的教科书例子：按 `.SortHierarchyPaths(` 搜是 0 命中，但方法确实被测试用字符串名调着。
- **真实矛盾**：该方法的 remarks 写得很重："Ordinal, not culture-aware: … **because the row order is the row identity of the host's index-aligned arrays**"。可 `PickupFieldSync.cs` 并在三处**各写了一份** `string.CompareOrdinal` 排序（顺序：候选收集 `candidates.Sort(...)`、多 collider 收集、host 写入前的 `hosts.Sort(...)`），**没有一处调用这个方法**。也就是说：规则的中心化只存在于注释里，实际有三份独立实现（见 D4）。
- **grep 是否可信**：**不可信，已人工确认**。反射调用者只有测试；`PickupFieldSync` 的三处排序是 `List<T>.Sort(comparer)`，签名上也不可能隐形调用 `string[] → string[]` 的方法。
- **风险**：删方法 → 必须同批删/改那条测试，否则测试运行时 `Invoke` 抛异常（编译能过，运行失败 —— 这类"测试靠反射活着"的代码最容易在清理时被漏掉）。保留 → 继续制造"规则已被集中执行"的错觉。
- **建议**：**删** `SortHierarchyPaths` + 对应测试用例，把那句 ordinal/行序即身份的注释搬到 `PickupFieldSync` 真正排序的三处之一（或提到类注释）。**不要**为了"用上它"而把 `List.Sort` 改造成数组排序 —— 那是为清理而扩大改动面。
- **批次**：B3（与测试同批）。

### T3. `MinValueUnits` 在 Editor 与 Runtime 各有一份

- **目标符号**：`PickupFieldAuthoringRules.MinValueUnits`（`public const int = 1`）与 `PickupFieldHost.MinValueUnits`（`private const int = 1`）
- **证据**：两处都源自 `Docs/OPEN_DECISIONS.md`（j）"账本只吃整数单位，低于 1 拒绝"。Editor 侧用于 `TryValidateValueUnits` 拒绝 `value < 1` 的登记行；Runtime 侧用于 `PickupFieldHost` 自检（`valueUnits[index] < MinValueUnits` 判行无效）并在日志里报 `valueUnitsBelowMin`。两处注释都指向同一条决策。
- **grep 是否可信**：可信（`const`，直接引用）。
- **风险**：低。两份都是 `1`，且都是"拒绝"方向的守卫；真正的风险是将来决策改成别的下限时只改一处 —— 表现是"编辑器放行、运行期判无效"（或反之），而这**不会编译报错**。
- **建议**：**保留但标注**（编辑器侧已写明来源）。彻底消除需要把 runtime 的 `const` 提升为可被 Editor 引用的公开常量（Editor asmdef 已引用 `VRCKatamari.Runtime`，技术上可行），但这会让一个私有实现细节变成公开 API，收益不抵成本。**合并动作留给"下一次真的改这个下限"的人**。
- **批次**：B3（只加注释，不改值）。

### T4. `PickupFieldAuthoringRules` 的 `0.21 / 0.76` 边界本身是否还站得住

- **证据**：常量注释自述"PROVISIONAL: the s/m gap in the PicnicLawn catalog is 0.18 -> 0.24 m and the m/l gap is 0.72 -> 0.80 m, so the boundaries sit in the middle of those gaps"。也就是说这两个值的存在理由**是目录里最长边的间隙**，与运行期毫无关系。它们没有第二处定义（无副本），场景/`.asset` 里也没有 —— 只在代码里。
- **风险/建议**：只要 `ValueTable` 还用 s/m/l 三档，这两个数字就有意义；**不要**在"清理"名义下改数值。改动必须与 `ValueTable` 重标定一起做（见 T1）。
- **批次**：不做（记录为"有意保留"）。

---

## 四、同一逻辑的重复实现

### D1. `CpuSkirmishBootstrap` 里 `[round]` 日志 + `ProbeBank()` 被复制了三份（**缺陷，不只是冗余**）

- **目标符号**：`CpuSkirmishBootstrap.AllMotorsReady`、`CpuSkirmishBootstrap.Setup` 里各多出一段与 `CpuSkirmishBootstrap.Update` 完全相同的代码：`float remaining = roundSeconds - elapsed; … ProbeBank(); Debug.Log("[round] t=" + … + " phase=" + …);`
- **证据**：三段逐字相同（`Update` 的日志块、`AllMotorsReady` 开头、`Setup` 开头）。`git show 453fd91 -- …/CpuSkirmishBootstrap.cs` 可见这三段是**同一个提交里一次插入三遍**的（该提交的 diff 出现三次 `+            ProbeBank();`），是一次机械插入的复制粘贴产物，不是设计。
- **为什么它是缺陷而不是冗余**：
  1. `AllMotorsReady()` 在 `Update` 里**每帧**被调用，直到 4 个球都报出正直径（`readinessLimit = 240` 帧）。这段代码**不受 `logIntervalSeconds` 约束**，也不受 `initialised` 约束 → 就绪等待期间会**每帧**打一条 `[round] t=… phase=Playing` 和一条 `[bank] window=closed skipped`（`ProbeBank` 在 `windowOpen == false` 时立刻打这行）。既污染日志，也让"round 日志每秒一条"这个可读性约定失效。
  2. `Setup()` 里那段在 `initialised = true` **之前**执行：`ProbeBank()` 在电机还没 `AddPlane` / `TryBeginPlaying`、`elapsed` 即将被清零的时刻被调一次，多打一条 `[round]` 和一条 `[bank]`。日志里的 `t=` 也因此用的是"清零前"的 elapsed。
  3. 两处都发生在 `roundPhase == 0` 且 `windowOpen == false`，所以**不会**真的去 `TryBankWithReason`（`ProbeBank` 的第一道 `if (!windowOpen)` 挡住了）→ 当前**没有**意外的交分。这是它至今没被发现的原因。
- **grep 是否可信**：不涉及——这是纯粹的同文件代码比对 + git diff 事实。
- **风险**：修它是**纯删重复块**，行为变化只有"少打两类噪声行"，这正是想要的。唯一需要确认的是：有没有人依赖就绪期那几行 `[round]`（没有——`Docs/Evidence/` 的记录里 round 行都是"每秒一条"的节奏）。**不修的风险**：一旦有人把 `windowOpen` 的初值改成 `true`（例如为了让 Lab 一开场就能交分），`AllMotorsReady` 就会在电机未就绪时**每帧发起交分尝试**，那是真实的规则破坏。
- **建议**：**删 `AllMotorsReady` 与 `Setup` 里的两份副本**，只保留 `Update` 里的那一份（受 `nextLogTime` 节流）。这是本审计里**唯一建议优先做的改动**，因为它是"复制粘贴已经在制造错误行为"的现行证据。
- **批次**：B4。

### D2. `ProbeBank` 里硬编码了电机序列化参数的副本（`3.4390244 / 0.0041 / 1.1 / 1 / 1` + `residual = 0.25`）

- **目标符号**：`CpuSkirmishBootstrap.ProbeBank` 中 `SizeModelMath.TryDerive(0f, 3.4390244f, 0.0041f, 1.1f, 1f, 1f, …)` 与 `float residual = 0.25f;`
- **证据**：这 6 个数字与 `KatamariBallMotor` 的序列化字段默认值**逐一相同**（`baselineVolumeUnits = 3.4390244f`、`cubicMetersPerVolumeUnit = 0.0041f`、`maxCoreRadiusWorldM = 1.1f`、`collisionToCoreRadiusRatio = 1f`、`semanticDiameterPerCoreDiameter = 1f`、`bankResidualFraction = 0.25f`，见 `KatamariBallMotor.cs:105-109,158`），场景里三个电机也确实是这些值。注释写的是 "Expected shrink, computed from the same public math the motor uses" —— **数学是同一套，输入不是同一份**。`TryBankWithReason` 用的是电机的字段；`ProbeBank` 用的是字面量。
- **现在的值从哪来**：手抄的默认值。电机一旦在某个场景里被调过（这些字段是 `[SerializeField]`，本来就是为了每场景调），`expected=` 就不再是"规则的答案"，而是一个**没有编译错误的谎言**。目前两边相等，所以日志是对的 —— 这是"暂时正确"的重复。
- **grep 是否可信**：可信（都是字面量与字段引用）。
- **风险**：不影响任何决策（`expected` 只进日志），因此**低风险、高误导**。这是本项目"同一逻辑只有一份实现"规则（`AGENTS.md`）要防的典型：副本漂移时没有信号。
- **建议**：**合并**。两条路：
  - 首选：给电机加一个公开 seam（例如 `GetExpectedPostBankDiameterM()`，内部就是 `TryBankWithReason` 用的同一串计算），`ProbeBank` 只打这个值 —— 但那会增加一个公开 API，且要动 `KatamariBallMotor`（超出本次范围）。
  - 次选（**推荐本轮做**）：**删掉整个 `expected` 计算**，日志只留真实的 `before` / `after` / `reason`。`before`→`after` 本身就是可验证的事实，`expected` 是它的复查副本；删掉它同时消灭 A1 的 `residualPercent` 与这里的 6 个手抄常量。
- **批次**：B4（与 D1 同一文件，同一次重编译）。

### D3. `QueueFacts` 自算包围球半径 / reach，与 `PickupAbsorbMath.TryComputeReach` 是两套几何

- **目标符号**：`CpuSkirmishBootstrap.QueueFacts` 里的 `float itemRadius = Mathf.Sqrt(3f) * size * 0.5f; float reach = m.GetCollisionRadiusWorldM() + m.GetContactRadiusWorldM() + itemRadius;`
- **权威实现**：`PickupAbsorbMath.TryComputeReach(itemBounds, collisionRadiusWorldM, contactRadiusWorldM, out itemRadiusM, out reachM)` —— `itemRadius` = **AABB 半对角线** `sqrt((x/2)²+(y/2)²+(z/2)²)`，`reach = collision + contact + itemRadius`。电机自己的 `EvaluatePendingCandidates` 就是把 `pendingBounds[i]` 喂给它来判 `RejectOutOfReach` 的（`KatamariBallMotor.cs:771-775`）。
- **差异（不只是"重复"，是会给出错误数字）**：
  1. `QueueFacts` 用 `Mathf.Sqrt(3f) * size * 0.5f` = **立方体**假设；真实物品 AABB 非立方时（长凳、树、桌子）会系统性偏大或偏小。例如 1.2 × 0.4 × 0.5 m 的长凳：立方体式给 `sqrt(3)*1.2/2 ≈ 1.04 m`，权威式给 `sqrt(0.6²+0.2²+0.25²) ≈ 0.68 m` —— 打印出来的 reach 多 0.36 m。
  2. `size` 取的是 `target.localScale.x` —— **`Transform.localScale` 不是 renderer 的 world AABB**：它不含父级缩放，也不是 mesh 的本地 bounds。对任何非 1×1×1 单位的 mesh 或带父级缩放的 prop，它与 `propBounds`（编辑器写入的 AABB）根本不是同一个量。
  3. 物品来源也不同：`propBounds` 由 `PickupFieldSync` 从 renderer AABB 或 `@size=` 立方体写入；`target.localScale` 是场景里那个 Transform 的缩放。
  4. 于是出现注释里描述过的那个伪症状："d3d 在 reach 内但没吸到" —— 有一部分读数其实是**两把尺子量了同一个东西**。
- **grep 是否可信**：可信（两处都是明文代码，CodeGraph 确认 `TryComputeReach` 的调用者只有 `KatamariBallMotor` 与 `PickupAbsorbMath` 自身，`CpuSkirmishBootstrap` 不在其列）。
- **风险**：只影响诊断日志（不参与任何判定），但它是"诊断误导过两轮"的那条链上的东西（`QueueFacts` 的 remarks 自己就写了"Facts, not verdicts: a boolean … is wrong forever when the comparison is wrong, which cost this project two rounds"）。讽刺的是，它算 reach 的方式仍是一套独立实现。
- **建议**：**改口径**（不是简单删）。两条路：
  - 干净做法：电机已有 `GetPendingCandidateDistance` / `Value` / `Age` / `ItemId`，但**没有 bounds getter** —— 需要新增 `GetPendingCandidateBounds(int)`，然后 `QueueFacts` 直接调 `PickupAbsorbMath.TryComputeReach`。这会动 `KatamariBallMotor`（超范围，需要另立任务）。
  - 本轮可做：把注释改成明确的"立方体近似（`sqrt(3)·localScale.x/2`），只在单位立方体、无父级缩放时等于门用的 AABB 半对角线"，并把日志字段名从 `reach` 改成 `reachApprox`，防止下一个人再把它当门槛读。
- **批次**：B4（注释改名）；真正的合并留给"给电机加 bounds getter"的任务。

### D4. `PickupFieldSync` 三处各写一份 ordinal 排序

- **目标符号**：`PickupFieldSync` 内 `candidates.Sort((l, r) => string.CompareOrdinal(l.Path, r.Path))`、多 collider 收集处的同类排序、以及写 host 前的 `hosts.Sort(... string.CompareOrdinal(HierarchyPath(...)))`；对应规则类是 T2 的 `SortHierarchyPaths`。
- **证据**：3 处独立排序，0 处调用规则类。三处都是 `List<T>.Sort(comparer)`，无法直接复用 `string[] → string[]` 的方法，所以这不是"有人偷懒"，而是"规则放错了地方"。
- **grep 是否可信**：可信。
- **风险**：低（三处都用 `CompareOrdinal`，口径一致）。真正的风险是"以后有人改其中一处（比如改成 culture-aware 以便阅读）而没意识到行序就是 host 的索引身份"。
- **建议**：**合并到一条可复用的 comparer**（例如规则类提供 `StringComparer`/`IComparer<T>`，三处共用），并把 T2 的方法删掉。若不想动三处，至少把那句注释提到 `PickupFieldSync` 类级。
- **批次**：B3（与 T2 同批）。

---

## 未覆盖

1. **`Editor/MapGreybox/`（`GreyboxPayload.cs`、`PicnicLawnGreyboxBuilder.cs`，共 ~2100 行）只做了目录级确认，没逐符号查零调用者/重复实现。** 原因是本轮范围被刻意压到"两个目录 + 三个已知项"，而这一对文件的读者是编辑器菜单（入口由 `[MenuItem]` 字符串触发，grep 看不到），判死风险高于收益，应作为独立一轮。
2. **`Editor/Pickup/PickupFieldSync.cs` / `PickupFieldSyncReport.cs` 只查了与 `PickupFieldAuthoringRules` 交界处（校验链、排序链、写入链），没有系统审计它自身的零调用者成员。**
3. **`Runtime/Ai/README.md` 未审**（文档一致性不属于本次口径）。
4. **未在 Unity 里实测**（本审计不碰 Unity）。D1 的"每帧刷日志"是从代码路径推断的（`AllMotorsReady` 在 `!initialised` 时每帧调用、内部无节流），建议由 Unity 操作者在 `CpuSkirmish` 场景跑一次确认日志行数。
5. **`.asset` / `.unity` 的序列化影响是按 Unity/UdonSharp 常识推断的**（删字段 → 程序资产重生成 + 场景未知键静默丢弃），未实际执行验证。
6. `Docs/Design/PickupAbsorb/SIZE_GATE_AUDIT.md` 只读了与 T1 相关的结论段，未通读。

## 本次审计没有做的事

- 没有修改任何 `.cs`、场景、prefab、`.asset`、`tasks.json`。
- 没有跑 git 写命令，没有跑 Unity，没有跑测试。
