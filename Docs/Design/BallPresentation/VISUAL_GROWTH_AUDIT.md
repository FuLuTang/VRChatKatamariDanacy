# VISUAL_GROWTH_AUDIT — 吸附之后，视觉球到底有没有马上变大

> **2026-11-16 owner 实机记录（此后以此为准）：** 一轮 StoryMode Play，29 次 `[grow]` 的每次吸收直径增幅 min **0.2%** / 中位 **2.5%** / max **23.8%**；D<0.4 档中位 **16.3%**；D>1.0 档连续 3 次 ≤ **0.9%**；`[shell]` 的 swallowed/attached 收敛到 **22/30**。owner 判定：**开了 80 ms 弹簧仍读作「跳一下」**，所以 **核心追赶弹簧已删除**。真因是**价值定价**（整数 unit 的最小增益过大），修复走定点体积与 level-1 重定价，见 `Docs/Research/ContentVolume/WHY_OUR_GROWTH_IS_BIG.md`。测量原文：`Docs/Evidence/BallPresentation/2026-11-16_owner_play_log/summary.md`。
>
> **现行口径（2026-11-17，原作优先）：** 球芯（核心网格）**显示直径固定为本关起始球径**（蟹座 0.222 m），整局不随吸收长大；`BallView` 在马达就绪后写一次，读 `GetStartDiameterWorldM()`。随体积长大的只有物理等效球（碰撞、接地、吸收门、HUD），吸附物冻结在它的接触面上，所以球大了是核心外一层壳。`0.95·r_phys`、`useVisualCoreRadius`、核心弹簧、`TryCoreTargetDiameter`/`TryStepCoreDiameter`/`TryVisualCoreRadius`/`VisualCoreToPhysical` 全部删除。设计见 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) §3.2，原作证据见 [`ORIGINAL_ATTACH_LOOK.md`](../../Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md) b1。下文 §1–§5 是 2026-11-15 的诊断与离线模型（成长曲线、增量表、吞没率仍是定价问题的证据，其中的“视觉球 = 0.95·r_phys / D_visual”列是当时的假设，不再是目标），§6 的优先级与补丁已按现行口径改写。

- 日期：2026-11-15
- 范围：只读审计 + 离线模型 + 提案。**没有跑 Unity**（本 agent 不是 Unity 操作者），没有任何 Play Mode 观感结论标 PASS。
- 证据分级：`[代码事实]` 可在本仓库复核（`文件:行`）。**行号核对于 2026-11-15T22:55Z；`KatamariBallMotor.cs`/`PickupFieldHost.cs` 同时被其他 agent 修改，若行号漂移请以函数名为准。**
- 证据分级（续）：`[数据挖掘]` 来自 `/home/fulutang/KatamariRef/`、`Docs/Research/`、`/tmp/orig_physics/`；`[专利]` 来自 US7402104B2 的阅读记录（`/tmp/orig_physics/report.md` §7/§10）；`[推断]` 是把两边接起来的解释。
- 所有者报告（转述）：*"吸了东西之后视觉球没有立刻变大；一开始简直像完全没长……但不可能，因为后期明明看得到球在变大。"* 他还问：解包数据里有没有相关逻辑。
- 本文不改 `Runtime/`、`Editor/`、`Labs/`。需要改代码的地方只给**提案补丁**（函数名 + 行号 + 期望效果）。

## 0. 结论（先看这 7 条）

1. **尺寸是"提交即生效"的，没有任何视觉平滑。** 同一固定步内：第 6 阶段提交账本 → 第 7 阶段重算尺寸（`Runtime/BallMotor/KatamariBallMotor.cs:1339`、`:1345`），`BallView` 当帧就把 `coreMesh.localScale` 写成新直径（`Runtime/BallPresentation/BallView.cs:56-61`）。所以"延迟"不来自成长管线。
2. **（当时）`BallView` 用的是语义直径（= 物理直径）**，核心随体积一起长，先吸的件被包进红球（`BallView.cs:56`）。现行口径已改为核心固定为起始球径（见文首）。
3. **每次吸附的直径增量差 40 倍**：1 单位（橡果）在 0.30 m 是 **+8.86 %（+26.6 mm）**，在 1.10 m 只剩 **+0.196 %（+2.15 mm）**，在 2.20 m（上限）是 **0**。按 1 % 可读线算：**1 单位在 0.637 m 以上、4 单位在 1.011 m 以上就看不见**（§2）。
4. **原作的单件增量比我们小 1–2 个数量级**（门槛件最大 2.6 %，典型件 0.11 %；§3a），它靠"一档 20–180 件、中位 ~90 件"的密度堆出连续感。所以我们的问题**不是成长曲线太小，而是"每件都把轮廓改一点点"这件事没有发生**。
5. **吸附物被冻在吸附瞬间的球半径上，之后永不外移、永不缩放**（`Runtime/Pickup/PickupFieldHost.cs:1064-1074`，`AttachmentMath` 明确不施加 objectScale）。球长大后它们被核心吞进内部：等级 1 实测（模型）**中位 81 % 已吸附件完全落在核心球内部**。
6. **最可能的解释（排序第 1）**：视觉轮廓只剩"裸核心球 + 一层被吞掉的冻结外壳"——每件吸附只把半径推 2–27 mm，形状不变、堆积感不增长；后期之所以"看得到变大"，是因为 90–212 单位的大件落在半径之外，外壳相对核心从 0.87 突到 0.56（§4）。
7. **原作有 21 档 `core_01..core_21` 低模"杂物团"核心网格**（`[数据挖掘]`），视觉球是**独立的显示对象**（专利 `Mp`，与碰撞 `Mh`、滚动 `Mr` 分开），并按 display level 把小件**直接不画**。这正是"看起来是一堆东西在滚"的来源；我们是一颗光滑 Unity 球（§3b/§3c）。

## 1. 我们的代码今天到底做什么

### 1.1 尺寸链（一条链，四个量，两个比值都是 1）

| 环节 | 代码 | 事实 |
|---|---|---|
| 账本 → 体积 | `SizeModelMath.TryDerive`（`Runtime/BallLayers/SizeModel/SizeModelMath.cs:108-190`） | `V = (baselineVolumeUnits + confirmed) × cubicMetersPerVolumeUnit`，`r = cbrt(3V/4π)`；`core = min(r, maxCoreRadiusWorldM)`（`:164-174`） |
| 三个派生量 | 同上 `:179-180` | `collision = core × collisionToCoreRadiusRatio`、`semantic = 2·core × semanticDiameterPerCoreDiameter` |
| 参数值 | `KatamariBallMotor.cs:194-196`；场景 `Worlds/StoryMode/StoryMode.unity` 组件 `1524211376` | `baselineVolumeUnits=3.4390244`、`cubicMetersPerVolumeUnit=0.0041`、`maxCoreRadiusWorldM=1.1`、两个比值都是 `1` |
| 写入 | `KatamariBallMotor.DeriveSize`（`:3334-3351`） | 三个字段 `coreRadiusWorldM / collisionRadiusWorldM / semanticDiameterWorldM`（`:3348-3350`），随后 `DeriveGrowthFraction`（`:3386`，只喂运动层） |
| 视觉 | `BallView.Update`（`BallView.cs:53-63`） | `coreMesh.localScale = Vector3.one * motor.GetSemanticDiameterWorldM()`（`:61`）；变化小于 `1e-4 m` 不写（`:44`、`:59-60`） |

`coreMesh` 是 `BallVisualRollRoot` 的子物体 `CoreMesh`，MeshFilter 用内置 Sphere（`StoryMode.unity` 对象 `2109846802/2109846803`，`m_Mesh: 10207`），Unity 内置球直径 1，所以 `localScale = D` 就是直径 D。**马达从不写任何 transform 的 scale**（`ApplyPresentation`，`KatamariBallMotor.cs:3421`，只写位置与旋转）。

结论（当时）：`D_visual = D_physical = 2r`，核心随体积长大。现行：`D_visual` = 起始球径，常量。

### 1.2 更新频率与时机：立即，不延迟，不插值

| 问题 | 答案 | 证据 |
|---|---|---|
| 多久算一次尺寸 | 每个固定步一次（场景 `fixedStepSeconds = 0.02`，即 50 Hz；`maxStepsPerFrame = 5`） | `KatamariBallMotor.cs:1255-1277`（`Update` 累加器 + `StepBall`） |
| 吸附与重算的顺序 | 同一步内：阶段 5 判定（`:1336`）→ 阶段 6 提交（`:1339`）→ 阶段 7 重算（`:1345`） | `StepBall`（`:1290-1366`） |
| 有没有 lerp/平滑/插值 | **没有**。`BallView` 只有"死区 + 直写"；`lastDiameterM` 只做去重 | `BallView.cs:44-63` |
| 有没有按档位/定时器节流 | **没有**。视觉层不认 tier，也没有计时器 | 同上；`GetSemanticDiameterWorldM` 是纯 getter（`:5807`） |
| 唯一的"平滑" | 跟随架相机：高度 5 Hz、yaw 8 Hz 低通（`heightFilterHz=5`、`yawFilterHz=8`，`KatamariBallMotor.cs:237-238`、`UpdateFollowRig` `:3401`）。它只影响**观感**，不影响尺寸 | `UpdateFollowRig` / `FollowRigMath.TryUpdate` |
| 单件吸附的账本增益 | 物件的 `valueUnits` **1:1** 进账，不做缩放/取整（非整数被拒） | `Runtime/Pickup/PickupAbsorbMath.cs:568`、`KatamariBallMotor.cs:3199-3204` |

即：**"吸附 → 变大"在代码里是零延迟的**；如果观感上不是，问题只能在"变大这件事看不看得出来"。

### 1.3 吸附物的位姿：吸附瞬间的世界位姿，之后永不外移、永不缩放（确认）

- `PickupFieldHost.AttachProp`（`PickupFieldHost.cs:1035`）：取 `motor.GetCenterWorld()` 与 `GetVisualRollRotation()`，用 `AttachmentMath.TryWorldPoseToLocal`（`:1064`）算出**球局部**位姿，然后 `prop.SetParent(attachmentRoot)`（`:1072`）+ 写 `localPosition/localRotation`（`:1073-1074`），并禁掉该件的 Collider。
- **没有任何代码再写这些吸附物的局部位姿或缩放**：`Runtime/` 里写 `localScale` 的只有 `BallView.cs:61`（核心网格）、`PushDirectionCue.cs:137`（推球浮标）；`LevelArenaHost.cs:752`、`CpuSkirmishBootstrap.cs:384` 只读；`AttachmentMath` 把 objectScale 当**数据**存，从不施加（`AttachmentMath.cs:130-137` 与其类注释）。
- `AttachmentRoot` 的 scale 是 1，且与 `CoreMesh` 是**兄弟**（`StoryMode.unity` 对象 `1979592395/1979592396`，父 `1011988491`）。所以：**核心长大时，吸附物一动不动，核心球面自己涨上去把它们吞掉。**
- 后果（可测）：等级 1 的 130 次吸附里，**50 % 的已吸附件在头 10 件之后就已经完全在核心内部**，中位吞没率 **81 %**（§5 模型）。
- 径向代理外壳（`maxProxyProtrusionPerCoreRadius=0.35`、`maxProxyProtrusionWorldM=0.3`）只影响**接地高度**，不会把道具推出去（`KatamariBallMotor.cs` 的 `maxProxyProtrusionPerCoreRadius`/`maxProxyProtrusionWorldM` 与 `RadialProxyShellMath.TryBuildContactPoints`）。**注意**：它同时意味着"视觉上凸出的外壳"和"物理上能撑住的外壳"最多差 `min(0.35r, 0.30 m)`——任何把道具外移的表现层改动都必须守住这条（§6）。

### 1.4 相机：跟随架把"变大"按比例抵消掉一大半

`FollowRigMath`：`back = eyeBaseBackMeters + r × eyeBackPerRadius`、`height = eyeBaseUpMeters + r × eyeUpPerRadius`（`Runtime/BallLayers/FollowRig/FollowRigMath.cs:19-20`、`:134`、`:138`；参数 `0.75 / 2.2`、`0.55 / 2.4`，`KatamariBallMotor.cs:250-255`）。

于是眼点距离 ≈ `sqrt((0.75+2.2r)² + (0.55+2.4r)²)`，球在屏幕上的角径 ∝ `D / distance`：

| 球径 | 相对增量（1 单位） | 屏幕上的相对增量 | 保留比例 |
|---|---|---|---|
| 0.30 m | +8.86 % | +5.65 % | 0.64 |
| 0.60 m | +1.19 % | +0.58 % | 0.48 |
| 1.10 m | +0.196 % | +0.067 % | 0.34 |

**相机不是主因，但它是把 1.19 % 压到 0.58 % 的那一刀**：0.6 m 之后单件本来就已经在可读线附近，再打对折就彻底看不见了。

### 1.5 诊断今天能读到什么

`StoryBallDiagnostics.Report`（`Runtime/Story/StoryBallDiagnostics.cs:100-142`）每秒一行 `[story] t=… center=… heading=… diameter=… bottom=… gap=… support=… supportId=… supportY=… supports=… stepLimit=… falling=… axes=… absorbed=… rejects=…`。

**它给了 `diameter`（4 位小数）和 `absorbed`（累计件数），但缺少三个关键量**：① 核心网格的**实际世界缩放**；② 已确认**单位数**；③ 两次日志之间的**每件增量**。所以"账本在长、视觉没长"这类分叉，今天的日志**看不出来**（§4.3 给 3 行补丁提案）。

## 2. 每次吸附，直径到底变多少（当前参数，整数单位账本）

公式：`D = min(2·cbrt(3(baseline+n)c/4π), 2.20)`，`baseline = 3.4390244`、`c = 0.0041`。下表由 `Docs/Design/BallPresentation/visual_growth/visual_growth_model.py` 生成，测试钉在 `Docs/tests/visual_growth/test_visual_growth_model.py`。

| 吸附前 D | 增量 | 吸附后 D | ΔD | ΔD/D | 相机后 | 看得见（≥1 %） |
|---|---:|---:|---:|---:|---:|---|
| 0.30 m | 1 单位（橡果） | 0.3266 | +26.58 mm | **+8.86 %** | +5.65 % | 是 |
| 0.30 m | 4 单位（典型小件） | 0.3878 | +87.80 mm | **+29.27 %** | +17.47 % | 是 |
| 0.30 m | 100 单位（大件） | 0.9322 | +632.19 mm | +210.73 % | +80.11 % | 是 |
| 0.60 m | 1 单位 | 0.6072 | +7.16 mm | **+1.19 %** | +0.58 % | 勉强/否 |
| 0.60 m | 4 单位 | 0.6277 | +27.70 mm | +4.62 % | +2.20 % | 是 |
| 0.60 m | 100 单位 | 0.9997 | +399.68 mm | +66.61 % | +24.18 % | 是 |
| 1.10 m | 1 单位 | 1.1022 | +2.15 mm | **+0.196 %** | +0.067 % | **否** |
| 1.10 m | 4 单位 | 1.1086 | +8.56 mm | **+0.78 %** | +0.26 % | **否** |
| 1.10 m | 100 单位 | 1.2834 | +183.43 mm | +16.68 % | +5.11 % | 是 |
| 2.20 m（上限） | 1 / 4 / 100 单位 | 2.2000 | **0** | **0** | 0 | **否（封顶）** |

可读线（1 % 的直径变化）出现在：**1 单位 → 0.637 m；4 单位 → 1.011 m；100 单位 → 2.956 m（超过上限，即封顶前一直可读）**。

三个直接推论：

1. **"开局看不出长大"与成长曲线矛盾**：开局一颗橡果就是 +8.86 %、一件 4 单位小件就是 +29.3 %（屏幕上 +17.5 %）。这条只能由"视觉没跟上"或"看的不是红球芯"解释，不能由"增量太小"解释。
2. **中局单件确实不可见**：1.10 m 处 1 单位 +2.15 mm。**玩家在中局把整个 130 件里 56 件（43 %）的单件增量都低于 1 %**（模型统计；其中 110/130 件连"相机之后"都低于 1 %）。
3. **封顶后永远不长大**：等级 1 的 1522 单位里有 **165.6 单位（10.9 %）** 落在 1356.4 的封顶线以上，完全不产生视觉增长；第 **129** 件是最后一次让球变大的吸附（从 <2.20 直接触顶），第 130 件增量为 0。这解释"后期看不到继续变大"，与报告相反——所以报告里的"后期在长"更可能是大件落在外壳上造成的**轮廓跳变**（§4）。

## 3. 原作到底怎么做（只用我们手上真有的数据）

**先说清楚边界**：REROLL 的物理与成长在原生 `PS2KatamariSimulation.dll`（`SimulationNativeMethods.Tick(float delta)`）里，**没有反编译**（`/tmp/orig_physics/reroll_numbers.md` §4；Practice Mod 是已编译 DLL，没有反编译）。所以下面没有一行"原作代码"，只有**数据表 + 专利 + 社区实测**，并逐条标证据级别。

### (a) 是不是逐件连续长大？每件 ΔD/D 多少？

`[社区实测]`（`/home/fulutang/KatamariRef/meta/raw_growth_mechanism.txt` 第 3 条）：**每吸一件，把该件体积的一个比例加进球体积，再从新体积重算直径**——即逐件、连续、可叠加；比例随关卡与当前球径**分段线性**（同一文档给出 10 张关卡曲线）。另有两条量化细节：拾取门槛直径**向下取整到 mm**（同上第 2 条）、显示直径额外 **+0.02 cm**（第 1 条）。

`[数据挖掘]` 交叉验证：类型表里每型都有 `Volume (m³)` 与 `Req Pickup Size (mm)`，且 `π·D³/6 ≈ 10·V` 自洽（Spatula：V=0.0006121952 → 门槛 226 mm，与 10V 只差 0.13 %，正是向下取整）；逐件惩罚 `Pickup Volume Penalty` 默认 **0.8**（1198/1436 型），尾部 0.5/0.6/0.4/0.08/0.01（`reroll_numbers.md` §3.2）。

于是"每件 ΔD/D"是**可算的**，不需要逻辑代码：

```
ΔD/D = (1 + f(D)·V_item/V_ball)^(1/3) − 1,   V_item ≤ V_ball/10（10× 体积门）
```

| 球径 | f（MAS5 曲线） | 门槛件：ΔD/D 上限 | 典型件（0.35·D）：ΔD/D |
|---|---:|---:|---:|
| 0.30 m | 0.80 | **2.60 %** | **0.114 %** |
| 0.60 m | 0.80 | 2.60 % | 0.114 % |
| 1.10 m | 0.80 | 2.60 % | 0.114 % |
| 2.20 m | 0.304 | 1.00 % | 0.043 % |

（MAS4 作为敏感度：1.10 m 处 f 已经掉到 0.32 → 上限 1.06 %；2.20 m 处 0。）

**结论**：原作**确实是逐件长大**，而且**单件比我们小得多**——门槛件都只有 2.6 %，典型件 0.11 %；我们一颗开局橡果是 8.9 %，是原作上限的 **3.4 倍**、典型件的 **78 倍**。原作之所以"看起来在连续长"，不是因为单件大，而是因为**密度**：MAS1 一关 654 件；全作在 0.30–0.40 m 这一档就有 1131 个实例，单关每 0.1 m 档 20–180 件（中位 ~90）（`reroll_numbers.md` §2.2）。我们的整关只有 130 件覆盖 0.30→2.20 m。

### (b) 视觉球和物理球是同一个对象吗？

`[专利]` US7402104B2 图 4A/4C：**三个模型分开**——显示 `Mp`、碰撞 `Mh`（等体积球的**外接盒**）、滚动 `Mr`（16 固定针 + 4 可选针）；S4/S54 的流程是"按 `Mr` 算完滚动，用结果**移动显示模型 Mp**"（`/tmp/orig_physics/report.md` §7 P3/P5、§10）。`Mh` 在吸附后按体积增量**整体放大**，而 `Mp` 是挂着吸附物的**父子层级**（吸附加体积、记录接触瞬间位姿、按 display level 不画太小的件）。

`[推断]`：`Mp` 就是"能看见的那一坨"，`Mh` 是保守碰撞盒，两者**不是同一个物体**。这也解释了为什么原作可以"视觉上小件消失、碰撞还在"。

### (c) 是 21 档换网格，还是缩放一个网格？

`[数据挖掘]`：`core_01 … core_21` 是**成套的 core 模型**（低模"杂物团" + 贴图，The Models Resource "Katamari" 分类 81 件；`report.md` §11 M1/M3、`DATAMODEL_NOTES.md`）。配套的 swirl 尺寸阶梯每关一串（如 MAS1 `7/10/20/30/50/75/120` cm），常超出该关上限，像"全局尺寸档阶梯在该关的切片"（`reroll_numbers.md` §1.1）。

**对观感的含义**：21 档意味着每升一档，**轮廓的形状**就换一次（新的杂物堆），而不是同一个球被放大。半径变化被"换模型"这个离散事件盖住；玩家看到的是"这堆东西换了一层"，不是"同一个球大了 2 %"。**我们没有 tier，也没有换网格**，所以成长只能靠半径——而半径在 1 m 以上每件只动 2–9 mm。

### (d) 有没有成长动画 / "吞"的弹跳？

**没有文档。** 我们能查到的只有：逻辑 30 Hz 定步、渲染帧对**物件矩阵**做插值（RerollPatcher 的 `GetPropMatrixFromSimulation` + 插值补丁）；`[数据挖掘]` 的 20 个工作表里只有关卡表、类型表、实例表、惩罚表、swirl 尺寸表、文本表，**没有动画/时序表**；专利只讲 display level 的**显示/不显示**，没有"吞入动画"。所以：**"原作有一个吞入 pop"这件事没有证据**，不要把它当需求来源；反过来，"换网格 + 每件都往轮廓上添东西"是有证据的。

## 4. 最可能的解释：排序、判别测试、日志怎么读

### 4.1 排序表

| # | 假设 | 代码/数据判决 | 判别测量 |
|---|---|---|---|
| **1** | **(iii) 轮廓只剩裸核心球，吸附物被冻住并吞掉，所以"每件都在改变轮廓"这件事没有发生** | **支持（最强）**：`PickupFieldHost.cs:1064-1074` 冻结位姿；模型：中位 81 % 已吸附件在核心内部、`shell/core` 中位 0.87；核心是光滑 Unity 球（`StoryMode.unity` `m_Mesh: 10207`），无 tier | 两条 `[shell]` 行（吸附 10 件 / 40 件时）算 `core/shell` 与 `swallowed`；同一位置两张截图看轮廓方差。若 `core/shell` 长期 <0.9 且轮廓方差不变 → 确认 |
| **2** | **(v) 相机随球长大而后退，把"变大"按比例抵消** | **支持（放大项）**：`FollowRigMath.cs:19-20/134/138`；保留比例 0.64/0.48/0.34（§1.4） | `[eye]` 行：`app = ΔD/D × 0.75/(0.75+2.2r)`。若某次吸附 `ΔD/D≥1 %` 但 `app<0.6 %`，就是它在吃掉 |
| **3** | **(ii) 单件相对增量太小** | **中局成立、开局不成立**：1 单位在 ≥0.637 m、4 单位在 ≥1.011 m 低于 1 %；但开局 1 单位是 8.86 %、4 单位 29.3 % | `[ballview]` 行的 `dD_per_item`：若开局几次 `dD` 就是 20–90 mm 而观感"没长"，(ii) 被否 |
| 4 | (iv) 视觉根本没跟随物理（只在换档/定时器更新、或引用错） | **资产层面被否**：`BallView` 程序资产里确有 `_update` 入口与 `Transform.set_localScale`（`Assets/SerializedUdonPrograms/254e6f6df0e1b7d4b9dbecbdca064aa2.asset` 解压后符号表含 `_update`、`set_localScale`、`lastDiameterM`）；场景把 `coreMesh` 连到 `CoreMesh`（`StoryMode.unity` 对象 `1875452168`），组件 `m_Enabled: 1`。代码里没有 tier/定时器分支 | `[ballview]` 行：`mesh / d_motor` 必须恒为 1.000（±1e-4）。若不是 1 → **这是真 bug**，优先修它 |
| 5 | (i) 平滑/lerp 让低 D 时的变化看不出来 | **被否**：视觉路径零插值（`BallView.cs:44-63`），唯一的低通在相机（`heightFilterHz/yawFilterHz`） | 源码即证据；日志上表现为 `mesh` 与 `d_motor` 同步跳变、没有拖尾 |

**排第 1 的名字：`(iii) 视觉轮廓不再随吸附改变：外壳冻结 + 裸球核心`。** 它同时解释两半报告：中局每件只推 2–9 mm 的半径（看不见），而"吃掉的东西"没有任何一件留在轮廓上（球不再是"一堆东西"）；后期 90–212 单位的大件落在半径之外（`shell/core` 从 0.87 掉到 0.56），轮廓整块往外跳，于是"后期明明看得到在变大"。
**唯一可能推翻它的观测**：如果 `[ballview]` 行显示 `mesh` 跟不上 `d_motor`，则第 4 条升为第 1——那也是唯一能解释"开局 +29 % 也看不见"的机制。**所以第一次 Play 先打这两行日志**（§4.3）。

### 4.2 每条假设"确认/否证"的具体测量

| 假设 | 确认 | 否证 |
|---|---|---|
| (iii) | 同一次 Play 里，吸附 10 件与 40 件各截一张同机位图；用 `[shell]` 的 `core/shell` 对比：轮廓半径跟着 `shell` 走、`shell/core` 长期 <0.9，且轮廓几乎不变 | `core/shell ≥ 0.97` 且截图轮廓明显变大 → 外壳不是主因 |
| (v) | 任一次 `ΔD/D ≥ 1 %` 的吸附，`app < 0.6 × ΔD/D` 且屏幕尺寸实测（两帧截图里球的像素直径）同步只涨了 `app` | 把 `eyeBackPerRadius` 临时设 0 后，单件观感显著变强 → 相机是主因（这条要 Unity 操作者批准，属临时实验） |
| (ii) | `[ballview]` 的 `dD_per_item` 在 1.10 m 处是 2–9 mm（1–4 单位），且玩家在那一档确实一直在吃 1–4 单位件 | 玩家在那一档吃的是 100 单位大件（`dD=183 mm`）却仍看不见 → 否 |
| (iv) | `mesh / d_motor ≠ 1`；或 `absorbed` 增长而 `d_motor` 不变；或 `mesh` 只在某些时刻跳 | `mesh` 与 `d_motor` 每帧一致（±1e-4 m） |
| (i) | 源码里出现插值/弹簧；日志上 `mesh` 落后 `d_motor` 并在 100–300 ms 内追上 | 本仓库当前源码：否 |

### 4.3 需要新增的 2–3 行诊断（提案，不是代码）

都只加 `Debug.Log`，不加状态；字段顺序固定，方便 grep。位置与字段如下：

1. **`BallView.Update`（`Runtime/BallPresentation/BallView.cs:53`，在 `:61` 写完 scale 之后）** —— 一行同时判定 (i) 与 (iv)：
   `[ballview] absorbed=<motor.GetAbsorbedCount()> d_motor=<diameterM> d_visual=<lastDiameterM> mesh=<coreMesh.lossyScale.x> dD=<diameterM - lastReportedDiameterM>`
   建议每 `0.5 s` 或每 5 件打一次（`absorbed` 变化 + 计时器），**不要每帧**。
2. **`PickupFieldHost.AttachProp`（`Runtime/Pickup/PickupFieldHost.cs:1035`，在 `:1074` 写完局部位姿之后，每 10 件一次）** —— 判定 (iii)：
   `[shell] attached=<attachedCount> core=<0.5*motor.GetSemanticDiameterWorldM()> r_attach=<|localPosition|> h=<0.5*max(aabb)> shellMax=<运行最大值> frac=<core/shellMax> swallowed=<|localPos|+h < core 的件数>`
3. **`KatamariBallMotor.UpdateFollowRig`（`KatamariBallMotor.cs:3401`，成功之后）** —— 判定 (v)：
   `[eye] r=<核心半径> back=<eyeBaseBack + r*eyeBackPerRadius> up=<eyeBaseUp + r*eyeUpPerRadius> app=<semanticDiameter / hypot(back,up)>`

读法：一次 Play 里，若 `[ballview]` 的 `mesh/d_motor` 恒 1 且 `dD` 与 `[story] absorbed` 的增量对得上，则 (i)(iv) 出局；再用 `[shell]` 的 `frac` 与 `[eye]` 的 `app` 分摊 (iii) 与 (v)。**建议把这三行写进 `Docs/Design/StoryMode/` 的验收脚本之前先做一次试玩**，因为现在没有任何日志能区分"账本长了"和"轮廓长了"。

## 5. 模型、表与测试（离线，可复现）

- 模型：`Docs/Design/BallPresentation/visual_growth/visual_growth_model.py`（stdlib，确定性）
- 测试：`Docs/tests/visual_growth/test_visual_growth_model.py`（17 条，全绿）
- CSV（不进仓库，按需生成）：`python3 Docs/Design/BallPresentation/visual_growth/visual_growth_model.py --csv /tmp/level1_absorb_growth.csv`
  - 列：`index, item_id, value_units, confirmed_units, diameter_physical_m, diameter_visual_m, delta_diameter_m, relative_growth, apparent_growth, core_radius_m, shell_radius_m, visible_core_fraction, swallowed_props, swallowed_fraction`
  - `diameter_visual_m` 是离线模型当时的 `0.95` 假设，现行目标是常量起始球径（该模型不再据此判断）。
- 贪心顺序（写死在模型里，可复现）：每一步在"当前直径能吸（`absorbDiameterReq ≤ D`）"的件里取 **valueUnits 最小**的一件（同值按 itemId）。这给出**最少件数**下的成长曲线（从不空跑去找吃不了的东西）。
- 三个 ASCII 摘要（模型直接打印）就是下面三块。统计：130 件里 **56 件（43 %）单件相对增量 <1 %**，**110 件连"相机之后"都 <1 %**，74 件 <0.5 %；中位吞没率 81 %、中位 `core/shell` 0.87。

### 5.1 当前参数：每次吸附（§2 的表）

```
CURRENT: rel = diameter change / diameter, app = after camera pull-back
readable line (1 % of the diameter) is reached at: 1u -> 0.637 m, 4u -> 1.011 m, 100u -> 2.956 m
  d_before  units   d_after     delta_mm      rel        app     readable
      0.30      1    0.3266     26.5775    0.08859    0.05649   True
      0.30      4    0.3878     87.8020    0.29267    0.17468   True
      0.30    100    0.9322    632.1860    2.10729    0.80110   True
      0.60      1    0.6072      7.1645    0.01194    0.00579   True
      0.60      4    0.6277     27.7028    0.04617    0.02199   True
      0.60    100    0.9997    399.6807    0.66613    0.24181   True
      1.10      1    1.1022      2.1529    0.00196    0.00067   False
      1.10      4    1.1086      8.5618    0.00778    0.00264   False
      1.10    100    1.2834    183.4272    0.16675    0.05112   True
      2.20      1    2.2000      0.0000    0.00000    0.00000   False
      2.20      4    2.2000      0.0000    0.00000    0.00000   False
      2.20    100    2.2000      0.0000    0.00000    0.00000   False
```

### 5.2 原作曲线（MAS5 主、MAS4 敏感度）

```
ORIGINAL: f = volume fraction documented for MAS5 (MAS4 sensitivity)
  diameter  f_MAS5   max_rel(gate)  typical_rel(0.35 D)  f_MAS4  max_rel_MAS4
      0.30    0.80       0.025986             0.001142    0.80       0.025986
      0.60    0.80       0.025986             0.001142    0.74       0.023955
      1.10    0.80       0.025986             0.001142    0.32       0.010555
      2.20    0.30       0.010032             0.000434    0.00       0.000000
```

### 5.3 等级 1 的贪心吸附（每 10 件一桶）

```
LEVEL 1 greedy absorb (bucket = every 10 absorbs)
  absorb  units  D_phys   D_vis   rel/bucket  app/bucket  shell/core  swallowed
       0      0    0.300   0.285      0.0000      0.0000       1.000      0.000
      10     10    0.472   0.449      0.5751      0.3155       0.908      0.500
      20     20    0.568   0.540      0.2037      0.1022       0.910      0.750
      30     36    0.676   0.642      0.1894      0.0867       0.923      0.933
      40     76    0.854   0.811      0.2629      0.1052       0.854      0.850
      50    116    0.978   0.929      0.1456      0.0535       0.900      0.740
      60    156    1.077   1.023      0.1011      0.0349       0.810      0.817
      70    196    1.160   1.102      0.0775      0.0255       0.873      0.829
      80    236    1.233   1.171      0.0628      0.0198       0.841      0.800
      90    276    1.298   1.233      0.0528      0.0161       0.873      0.844
     100    316    1.357   1.290      0.0456      0.0134       0.900      0.780
     110    378    1.440   1.368      0.0609      0.0172       0.729      0.791
     120    570    1.650   1.567      0.1456      0.0372       0.557      0.867
     130   1522    2.200   2.090      0.3335      0.0682       0.600      0.915
```

读法：**前 10 件把球从 0.300 推到 0.472（+57.5 %，屏幕上 +31.6 %）**，之后每 10 件的相对增量掉到 1–6 %，`shell/core` 一直在 0.73–0.92（外壳只比核心多出 8–37 %），`swallowed` 从 50 % 涨到 90 % 以上——**球越大，已吸附的东西越看不见**。最后两桶的 `shell/core` 反而掉到 0.56（大件落在半径外），这就是"后期突然看得到变大"的来源。

## 6. 建议：怎么改才能"吸了就觉得在长"（按优先级）

排序原则：**每一元的改动先买"每件都能被看见"**，再买"同一个球在连续长大"。

| 优先级 | 改法 | 预期效果 | 成本 | 风险/约束 |
|---|---|---|---|---|
| **2** | **换核心网格（低模杂物团）+ 3–5 档 tier**，替掉 Unity 内置球 | 轮廓在档位边界**换形状**，把 2 % 的半径变化藏进一次"换一层"；同时让核心不再是纯红球 | 高（美术 3–21 个 mesh + 一张档位表；原作 21 档，我们 0.30–2.20 m 用 3–5 档够用：0.30/0.55/0.90/1.40/2.20） | 需要 Art 与 Lead 排期；tier 只读 `semanticDiameter`，**不得**进碰撞/拾取/stepLimit（§4A.2 的禁令） |
| **3** | **核心固定为起始球径**（已落地 2026-11-17；弹簧与 0.95 已删） | 核心不长，先吸的件不再被包进红球；大球是核心外的一层物件壳（原作观感机制） | 已完成 | 只改表现层；物理球仍随体积长大，吸附物挂在物理球接触面上 |
| **4** | **吞入 pop**：每次 `GetAbsorbedCount()` 变化时把外壳乘 1.02–1.03，0.1–0.15 s 回弹 | 给"吸附 → 立刻有反应"一个明确的**时间锚点**；原作是接触点的 0.07–0.13 s 星爆特效，而不是缩放 | 极低 | 纯表现，必须与真实尺寸解耦；不能参与 `[story] diameter`；优先做星爆（`ATTACH_PLACEMENT.md` O9） |
| **5** | **相机别按比例后退**：`back = 0.75 + 2.2·r` 改成次线性（如 `r^0.75`），或把 `eyeBackPerRadius` 从 2.2 降到 1.4 | 把 0.34–0.64 的"抵消系数"抬到 0.6–0.8，等于整体放大成长观感 | 低（2 个常量） | 改变整局手感与舒适度（站点眼点），**必须所有者拍板**；不是纯表现层的局部改动 |

不建议做的事：**不要为了"看得见"去改 `cubicMetersPerVolumeUnit`/`baselineVolumeUnits`**。它们被星星目标钉住（1356 单位 = 2.20 m 上限，`KatamariBallMotor.cs:184-192` 的注释与 `Docs/Design/Gameplay/round_model.py`），改它等于改关卡难度曲线；而且 §2 已证明开局单件增量**已经过大**（29 %，是原作的 11 倍），问题不在数值大小。

### 6.1 与 `CONTACT_MODEL_V2.md` §4A 的交叉检查

§4A.2 的三形状与不变量（`Docs/Design/BallMotor/CONTACT_MODEL_V2.md:755-777`）：

```
r_visual = r_start          # 起始球径的一半，常量
r_roll   = 1.00·r_phys
rollReach(û) = max(r_roll, max_i needleSupport[i]·dot(dir_i, û))     ≤ r_roll + cap
pickupReach(û) = max( r_phys·(|ûx|+|ûy|+|ûz|), rollReach(û) )
⇒ r_visual ≤ r_roll ≤ rollReach(û) ≤ pickupReach(û)
```

| 项 | 设计 | 今天代码 | 差 |
|---|---|---|---|
| `CoreMesh` 缩放 | `visualDiameter = 2·r_start`（常量，§4A.5 表） | 已落地：`BallView` 写一次 `GetStartDiameterWorldM()` | 无 |
| 视觉层读数 | 只读 `visualDiameter`（起始球径），**不**进接触/拾取/stepLimit | `BallView` 读 `GetStartDiameterWorldM()`；`GetVisualCoreRadiusWorldM()` 是它的一半，诊断读它 | 无 |
| 吸附物挂载 | `AttachmentRoot` 属于 VisualShape，是"粘附物挂载点" | 已是 `BallVisualRollRoot` 的子物体 | 结构对，行为（冻结/吞没）没设计 |
| 埋没 cull | §4A.2 把"埋没 cull"列为 VisualShape 的消费者；专利 display level | **没有**任何 cull（本地）；`RemoteProxyMath` 的 `buried` 只用于远端摘要 | 缺 |
| v2 开关 | 三形状拆分属 S4 | 场景 `contactModelV2Enabled: 0`（`StoryMode.unity` 组件 `1524211376`），**v2 未启用** | 本文所有建议都在表现层，**开关两态都成立**；等 v2 打开时 `BallView` 应改读 `GetVisualCoreRadiusWorldM()` |

**视觉层在 §4A 下应该怎么表现**（一句话）：核心网格固定为起始球径、只做表现；吸附物挂在 `AttachmentRoot` 上、**壳层外径不超过 `pickupReach`**（否则会"看得见却吸不到"，违反 §4A.2 的 `r_visual < … ≤ pickupReach`）；埋没的件按 display level 不画（而不是留在里面）；换 tier 只改网格与 tile，不改任何半径。

### 6.2 补丁提案（不落地；给其他 agent 的精确位置）

**P1 — `BallView`：核心固定为起始球径（已落地，2026-11-17）**

`BallView.Update` 在马达就绪（`GetStartDiameterWorldM() > 0`）后把 `coreMesh.localScale` 写成该直径**一次**，之后只剩一次布尔判断；0.95 开关、`smoothCoreGrowth`、弹簧状态与对应数学全部删除。原提案的 0.95 + 80 ms 弹簧被取代。

**P3 — 吞入反馈**：原作是接触点 0.07–0.13 s 的星爆特效，不是缩放（`ATTACH_PLACEMENT.md` O9）；特效属后续美术项，不在本文提案内。

## 7. 没验证的、以及边界

- **没有 Unity 运行**：本文所有"看不出/看得见"的判据都是几何与可读线（1 %）推断，**没有一次 Play Mode 截图或像素测量**。第 4 节的判别测试必须由 Unity 操作者做一次。
- **没有原作运行**：原作侧全部是 `[社区实测]` + `[数据挖掘]` + `[专利]`，**没有 `PS2KatamariSimulation.dll` 的反编译**；"21 档换网格"是资产命名与尺寸阶梯的证据，"逐件加体积"是社区文档与类型表自洽（0.13 %）的证据，二者都不是代码证据。
- **模型是"最少件数"下界**：贪心顺序假设玩家从不空跑；真实玩家会来回滚，所以真实到达 2.20 m 的**时间**比模型长，但**每件增量**与**吞没率**与顺序无关。
- **`shell_radius_m` 的建模假设**：吸附瞬间球心到道具中心的距离取 `r_attach`（贴面），道具的径向半径取 AABB 最长边的一半。真实值取决于玩家路径，是可比的**下界**（道具可能更靠外）。
- **相机表**用的是小角近似 `D/distance`；真实 VRChat FOV 与站点眼点还会引入常数项，但**相对**结论（保留比例 0.34–0.64）与 FOV 无关。
- **未做的验证**：`python3 -m unittest discover -s Docs/tests` 与 `python3 Docs/tools/validate_plan.py` 只覆盖离线数据；Unity EditMode/U# 编译与 Play Mode 都没跑（本 agent 不是 Unity 操作者）。
