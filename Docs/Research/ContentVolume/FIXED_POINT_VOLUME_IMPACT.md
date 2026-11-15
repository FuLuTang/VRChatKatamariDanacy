# 定点体积单位（fixed-point volume）影响审计

**审计日期：2026-11-15（项目时区 Asia/Shanghai）。** 只读审计：本轮**没有**改任何代码、场景、数据或测试，只写本文件；没有开 Unity，没有跑 Git，Unity 侧测试状态见 §9 末尾。
**问题：** 游戏现在用**整数 value unit** 记体积（`V = (3.4390244 + Σunits) × 0.0041 m³`）。要迁到细粒度定点（int64 / int32 的 micro / milli 单位）。本文件列出**所有**读写 / 同步 / 序列化 / 比较体积单位的位置，给出每种改法的溢出与精度后果、一个推荐表示、迁移顺序与测试影响。
**口径：** 遵循 AGENTS.md 的 **Original-first principle** 与 `Docs/Design/Parity/PARITY_AUDIT.md` 的 divergence 流程；凡是「我们和原作不一样」的地方，一律指到表里那一行，**表里没有行的单独标出**（§10）。

数值都是按仓库里的常数实算的，不引用旧文档的二手数字：`c = 0.0041 m³/unit`、`baseline = 3.4390244 units`、`cap core radius = 1.1 m`（`KatamariBallMotor.cs:194-195`、`Worlds/StoryMode/StoryMode.unity:12619-12621`）。

---

## 0. 结论摘要

1. **现在全项目没有一处网络同步携带体积**：所有 UdonSharpBehaviour 都是 `BehaviourSyncMode.None`（`KatamariBallMotor.cs:68` 及 `Runtime/` 全部 **20** 处，`grep -c '^    \[UdonBehaviourSyncMode'`）。所以「continuous 200 B / manual 280,496 B」今天是**零成本**，它只在将来把账本联网时才变成约束；届时 int64 账本快照在 8 人局会**同时**超过 200 B 的连续同步预算与 8 字节 per-player 的甜点区（§5）。
2. **int32 + 1 mm³ 不可行，不是「紧」而是「已经溢出」**：int32 上限 2,147,483,647 mm³ = **2.147 m³**，而当前 2.20 m 上限球的体积就是 **5.5737 m³**；**球自己都放不下**。
3. **int32 + 1 cm³ 也太小**：上限 2,147.5 m³，`_ref_cancer` 的实测内容体积是 **3,022.3 m³**（`Docs/Design/Levels/_ref_cancer/stats.json` 的 `volumeSupplyM3`）。单这一关的供给就能吃掉 int32-cm³ 的 141%。
4. **int32 + 「milli-unit」（0.0041 m³ / 1000 = 4.1e-6 m³）能装下全部现有内容**（上限 8,804.7 m³，Cancer 用掉 34%），但它的量子是由**我们自己的 0.0041 常数**定义的，原作没有这个常数，且余量只有 2.9×。
5. **推荐：`long volumeMm3`（1 LSB = 1 mm³ = 1e-9 m³，范围 ±9.22e9 m³）**，与原作数据的分辨率（`Req Pickup Size` 整 mm）同源，也与 `PARITY_AUDIT.md:25` 已记录的 owner 决定 D5（`restore`，1 mm³ `long`）一致；Udon 侧 `long` 已被本项目 Runtime 使用（`AttachmentMath.cs:73-75`、`:170`），不是未知类型。
6. **float32 的精确答案：以量子 q 计时，float32 只在累计量 < 2^24·q 时能分辨 +1 个量子。** 换成绝对值：+1 旧 unit 撑到 **68,786.6 m³**（D≈50.8 m）；+0.001 旧 unit 只撑到 **16,384 units = 67.17 m³**（D≈5.04 m）；+1 mm³ 只撑到 **0.016777 m³**（D≈0.32 m）——也就是说**一旦单位变细到 mm³，float32 在起始球附近就死了**。
7. **真正的 killer 不是精度而是幂等**：float 求和的最后几位依赖加法顺序，两台客户端重放同一批事件会得到不同的总分；int64 加法精确、可交换，账本重放天然一致。这也是 account/model 里唯一被 `OPEN_DECISIONS.md:53-57` 和 Ledger README 明确写下的「必须整数」理由。
8. **改动的半径比想象大**：C# runtime 19 处、Editor 6 个文件、Python 9 个模块 + 2 个 web 前端、4 门 NUnit 测试套 + 9 门 Python 测试、6 个场景/asset 的序列化数组、约 30 份文档。**只加宽类型（int→long、不改量子）可以保留全部现有数值**，这是唯一能做到「测试数字一个不动」的切法（§8 Phase A）。

---

## 1. 现在的数据形状（一条链读到底）

体积单位在仓库里是一条**单向链**，两端类型不同，中间靠 `float` 和 `(int)` 转换缝合：

```text
placement.json defs[].valueUnits / props[].valueUnits          int        (JSON number, whole >= 1)
  -> PlacementImporter.BuildDeclaredValueUnits                 int         (C# int, Editor/LevelBuild/PlacementImporter.cs:996-1013)
  -> PickupFieldSync.SyncFromChildren(declaredValueUnits)      int         (写 SerializedProperty.intValue)
  -> PickupFieldHost.valueUnits                                int[]       (Runtime, 序列化)
  -> SubmitProp: (float)valueUnits[index]                      float       (Runtime/Pickup/PickupFieldHost.cs:1306)
  -> PickupAbsorbMath.TryAbsorbValueToLedgerGain(itemValue)    int         (只接受整数值，:568-580)
  -> GrowthLedgerMath commit gain / confirmedVolumes[]         int         (Runtime/Economy/GrowthLedgerMath.cs:741-938)
  -> KatamariBallMotor.unbankedVolumeUnits                     int
  -> confirmedVolumeUnits = (float)unbanked + carried          float       (KatamariBallMotor.cs:3376)
  -> SizeModelMath.TryDerive(confirmedVolumeUnits: float)      float       (对数域，:105-187)
  -> semanticDiameterWorldM                                    float       (被 HUD / 星级 / 吸收门读)
```

**两个关键观察：**

- **账本里体积从来不是 float**。`confirmedVolumes` / `bankedScores` / `commitGains` / `predictedGains` 都是 `int`（`GrowthLedgerMath.cs` 全部签名）。`float confirmedVolumeUnits` 只是从 `int` 派生出来的**显示量**，不是累加器。所以「float32 累加误差污染总分」今天不成立；它只在**把累加器改成 float** 的方案里才成立（这正是 `PARITY_AUDIT.md:249` 反对的那条路）。
- **整数 unit 和物理体积之间没有恒等式**。`_ref_cancer` 声明 737,377 units（`placement.json`，与 `level.expected.valueUnits` 一致），×0.0041 = **3,023.25 m³**；而同目录 `stats.json` 实测 Σ a·b·c = **3,022.263 m³**（相差 240 units / 0.98 m³ / 0.03%）。说明 `valueUnits` 是**按体积定价后再取整到整数格**的独立标量，不是体积本身——这就是「整数单位」和原作「连续体积」分歧的物理来源。

---

## 2. 原作是怎么做的（What the original does）

证据全部来自本机的数据挖掘目录（`~/KatamariRef/meta/`，不进仓库）与 `Docs/Design/Parity/PARITY_AUDIT.md` 已记录的条目。

| # | 原作行为 | 数值 / 形式 | 证据 |
|---|---|---|---|
| O1 | 球有一个**体积**；显示尺寸 = 等效球直径，**额外平加 0.02 cm** | MAS1 的「精确」起始尺寸是 5.02 cm 而不是 5 cm | `meta/raw_growth_mechanism.txt`（Note to 1）；`PARITY_AUDIT.md` `G05`/`G09` |
| O2 | 拾取门 = 球体积 ≥ **10 ×** 物件体积；需要的球径**向下取整到整 mm** | 蚂蚁实际只要 ~8.8× | `raw_growth_mechanism.txt` §2；`DATAMODEL_NOTES.md` §3（1,449/1,450 行在 1 mm 内自洽） |
| O3 | 物件体积 **V = k · bbox**，k 是作者手调系数，**默认 0.70** | 1,425 型里 1,008 型（70.7%）精确 0.70；薄片 8–16.8、蓬松 0.05–0.07、镂空家具 0.3–0.5 | `meta/solid_volume/README.md`、`worst20.csv`；`PARITY_AUDIT.md` `G13` |
| O4 | 体积是**连续量**，按类型存 `Volume (m^3)`（浮点），按实例存 `f32Volume` | 「all objects」1,496×6；「object dump」28,698×17 含 `f32Volume` | `meta/DATAMODEL_NOTES.md` §2/§3 |
| O5 | 唯一的量化发生在**直径**上，不在体积上：`Req Pickup Size (mm)` 是整数 mm | 社区表 1,409 对里 97.7% 在 ±2% 内 | `DATAMODEL_NOTES.md` §3 |
| O6 | 吸收后球的增量 **ΔV = penalty · V_item**，penalty 逐件默认 **0.8**（1198/1436），另有 0.01–1.0 的逐件值 | `pickupVolumePenalty` | `meta/collectible objects`；`PARITY_AUDIT.md` `G06` |
| O7 | penalty 还随球径**按关分段线性**下降 | MAS1：5 cm .8 → 10 cm .8 → 15 cm .4 → 20 cm .16；mas1–mas9 + mtm 全表 | `raw_growth_mechanism.txt` §3 |
| O8 | 群组物件拾取父件时**连带子件体积一起入账** | 解释高侧离群点 | `raw_growth_mechanism.txt` §4；`PARITY_AUDIT.md` 移动件行提到 `parentProp`（38 件群组） |
| O9 | 增长曲线**逐关不同**，不是全局常数 | 每关一套 (size, ratio) 折点 | `raw_growth_mechanism.txt` §3；`PARITY_AUDIT.md` 每关分段回收曲线行 |

**这九条对「表示」的直接含义：**

- 原作**没有「单位」这个概念**。它没有任何 `c`、没有 `baseline`、没有「Σunits」；`0.0041 m³` 和 `3.4390244` 都是我们的标定产物（`Docs/Design/Parameters/README.md:35-42`）。
- 原作的**量化点在直径上（1 mm）**，因此一个忠实表示应当：**体积侧连续且精确**（细到 mm³ 就足够，因为 1 mm³ 在 0.3 m 球上只改直径 7 nm），**直径侧显式取整到 mm**。
- 原作是**单机**、float32 累加，所以它自己不需要跨端确定性。我们要（多客户端账本 + 幂等重放），所以**不能照抄它的 float32 存储类型，只能照抄它的物理形式（m³ + mm 直径）**——这条偏离必须写成理由（§10 第 R1 行的「理由」栏）。

---

## 3. float32 精度：+1 unit / +0.001 unit 在哪失效

float32 有 24 位有效位。归一化数的间距是 `ulp(x) = 2^(floor(log2 x) − 23)`。
**分辨 +q 的条件是 `ulp(x) ≤ q`，等价于累计量 `x < 2^24 · q`。** 换成「累计体积」就是 `V < 2^24 · q_m3`。

### 3.1 两种问法（旧 unit 口径）

| 问法 | 上限（units） | 上限（m³） | 等效球直径 | 说明 |
|---|---|---|---|---|
| float32 能表示 **+1 unit** | 16,777,216 | **68,786.6 m³** | 50.8 m | 游戏内 1359.4 units 的满径球只用了 0.008% 的窗口，**今天完全够** |
| float32 能表示 **+0.001 unit** | 16,384 | **67.17 m³** | 5.04 m | 满径球（5.5737 m³ / 1359.4 units）仍在窗口内，但只剩 12× 余量 |
| float32 在满径球处的间距 | — | — | — | `ulp(1359.4) = 1.22e-4 unit`，即 0.001 unit 还有 8 个格 |
| float32 在 Cancer 全局总量处的间距 | — | — | — | `ulp(737,377) = 0.0625 unit`：**0.001 unit 的增量在这里直接消失**，1 unit 仍可表示 |

### 3.2 换成各种量子之后（这才是迁移真正会遇到的）

`q` 是量子体积。float32 的「+1 量子」窗口 = `2^24 · q`。

| 量子 | q (m³) | float32 分辨 +1 量子到 | 等效球直径 | 备注 |
|---|---|---|---|---|
| 1 旧 unit | 4.1e-3 | 68,786.6 m³ | 50.8 m | 现状；够用 |
| milli-unit（1/1000 unit） | 4.1e-6 | **68.79 m³** | 5.08 m | 单球上限 5.57 m³ 仍在窗口内；**Cancer 全量 3023 m³ 已在窗口外 44×** |
| micro-unit（1/10⁶ unit） | 4.1e-9 | **0.0688 m³** | 0.51 m | 起始球 0.0141 m³ 尚可，0.51 m 以上全丢 |
| 1 cm³ | 1e-6 | 16.78 m³ | 3.18 m | 满径球以内可用 |
| **1 mm³** | 1e-9 | **0.016777 m³** | **0.32 m** | 起始球 0.0141 m³ 只比它小 19%；**再吃一件就掉精度** |

**一句话：精度问题不是「大数才出现」，而是「量子越细，窗口按 2^24·q 一起塌」。** 1 mm³ 的 float32 表示在 0.32 m 球上就没了——而我们的起始球是 0.30 m，满径是 2.20 m。

### 3.3 还有一个比精度更硬的 float32 问题：差值

`BallGrowthDiagnostics.cs:156-157` 用**两个 float 累加器相减**求「这一帧涨了多少」：

```csharp
float confirmedUnits = motor.GetConfirmedVolumeUnits();
float gainedUnits = confirmedUnits - lastConfirmedUnits;
```

在旧 unit 口径下这只是 `ulp` 级噪声；一旦 `confirmedUnits` 变成 mm³ 量级（Cancer 3.0e12），`ulp(3.0e12) = 262,144 mm³`，**差值会把一次吸收读成 0 或 262,144**。凡「大数相减求增量」的地方都必须改成整数减法（`long gained = now - last`），不能用 float。

同理，`CpuSkirmishBootstrap.cs:205-207` 用 `int` 读 banked 前后差；`PickupFieldAuthoringRules.TryValidateValueUnits`（`:428-453`）用 `2147483648f` 做上界比较——这些都是一次性的 int32 假设。

---

## 4. int32 / int64 容量：各量子能装多少

| 量子 | q (m³) | int32 上限（m³） | 等效球直径 | int64 上限（m³） | 现有内容占用（Cancer 3023 m³ 为最坏） |
|---|---|---|---|---|---|
| 1 旧 unit | 4.1e-3 | 8.80e6 | 256 m | 3.78e16 | 0.003% |
| milli-unit | 4.1e-6 | **8,804.7** | 25.6 m | 3.78e13 | **34%**（余量 2.9×） |
| micro-unit | 4.1e-9 | **8.80** | 2.56 m | 3.78e10 | **34,400%**（溢出 344×） |
| 1 cm³ | 1e-6 | **2,147.5** | 16.0 m | 9.22e12 | **141%**（溢出） |
| 1 mm³ | 1e-9 | **2.147** | **1.60 m** | 9.22e9 | **140,800%**（溢出 1,408×） |

游戏内锚点（用它们对照上表）：

- 起始球：`3.4390244 × 0.0041 = 0.0141 m³`（0.29974 m）
- 满径球：`1359.4390244 × 0.0041 = 5.5737 m³`（2.20 m）
- `story_01_bench_size` 全关 1,522 units = 6.24 m³
- `_ref_mas1` 全关 1,448 units = 5.94 m³
- `_ref_cancer` 全关 737,377 units = 3,023.2 m³
- 假想的 30 m 球：`π/6·30³ = 14,137 m³`（`VRC_WORLD_SIZE_LIMITS.md` §5 明确讨论过这个尺寸）

**结论：** int32 只有在「milli-unit 或更粗」时能装下现有内容；**任何 mm³ / micro 级方案都必须 int64**。账本里 `confirmed` 与 `banked` 是**守恒转移**（bank 把量从前者搬到后者，不新增），所以单个玩家的 absorbed 总量 ≤ 关卡供给 + 补货；Cancer 的 3023 m³ 因此是当前最坏的现实下界。

---

## 5. Udon 同步载荷

**现状：0 字节。** `Runtime/` 全部 **20** 个 UdonSharpBehaviour 都声明 `BehaviourSyncMode.None`（`KatamariBallMotor.cs:68`、`PickupFieldHost.cs:92`、`StoryRoundController.cs:119`、`CpuSkirmishBootstrap.cs:20`、`BallView.cs:58`、`BallGrowthDiagnostics.cs:68` …）。体积今天**完全没有进网络**，所以 200 B / 280,496 B 两个预算现在是空的。

**这会变，而且是体积定点方案的主要外部约束。** 官方预算（`Docs/Research/ReferenceRules/VRC_WORLD_SIZE_LIMITS.md:191`、`:328`，来源 S8 = creators.vrchat.com network-details）：continuous **~200 B / serialization**（超了直接序列化失败）、manual **280,496 B / serialization**（超了缓存/延迟）、每脚本 **~11 KB/s**。

按「账本快照 = 每玩家 {confirmed, banked, lifeState, generation}」估算：

| 每玩家字段 | int32 版 | int64 版 |
|---|---|---|
| confirmed 体积 | 4 B | 8 B |
| banked 分数 | 4 B | 8 B |
| lifeState + generation | 8 B | 8 B |
| **小计 / 玩家** | **16 B** | **24 B** |
| 1 人 | 16 B | 24 B |
| 4 人 | 64 B | 96 B |
| **8 人** | **128 B** | **192 B** |
| 8 人 + 每个 commit 事件的 gain（若同包） | 128 + 8×4 = 160 B | 192 + 8×8 = 256 B |

- **int32 版在 200 B 连续预算内（8 人 128 B）**；int64 版的**纯账本**（192 B）也还挤得进去，但**一旦和 commit 事件 / 回合状态同包就必然超 200 B**，只能转 manual 同步（280 KB 对这点数据毫无压力，代价是延迟/缓存语义）。
- 结论：**体积字段从 4 B 涨到 8 B 不是「能不能同步」的问题，而是「够不够塞进 continuous 200 B」的问题。** 推荐把账本分成「高频小包（当前球径 + 本地玩家 confirmed 的粗粒度）」与「低频 manual 快照（完整 long 账本）」两层，而不是把所有体积字段塞进 continuous。
- 另一个方向（不推荐但必须写出来）：用 int32 的**缩放语义**（例如同步「units」而不是 mm³）把载荷压回 4 B，代价是跨端必须约定同一个缩放常数，一旦某个关卡改了 `c` 就跨端不一致——这正是把 `c` 从表示里拿掉（§7）的理由。

---

## 6. 全量清单（file:line / 现类型 / 会坏什么）

类型列写**现在**的类型。行号按当前 HEAD 文件实读。

### 6.1 C# Runtime（`Assets/VRC_Katamari/Runtime`）

| 位置 | 现类型 | 角色 | 会坏什么 / 必须改什么 |
|---|---|---|---|
| `BallMotor/KatamariBallMotor.cs:194` | `float` | 序列化 `baselineVolumeUnits = 3.4390244f` | 基线的**语义**是「0.0141 m³ 体积」，不是 3.439 个单位；定点化后应直接存 `startVolumeMm3`（14,100,000），否则 `c` 一改基线就漂 |
| `:195` | `float` | 序列化 `cubicMetersPerVolumeUnit = 0.0041f` | 定点后这个常数应当**消失**（原作没有 `c`）；保留它就是把「单位」永久焊进表示 |
| `:298` | `int` | `unbankedVolumeUnits` | int32 溢出（§4）；mm³ 下 2.147 m³ 就满，**必须先换 `long`** |
| `:299` | `float` | `carriedVolumeUnits` | 已计分残量用 float 存 → 银行后 `post - stillUnbanked` 有舍入；定点下应为 `long` |
| `:300` | `float` | `confirmedVolumeUnits`（派生的显示量） | 值域放大 4.1e6 倍后 float32 立刻丢精度（§3.2）；必须 `long` 或从 `long` 现算 |
| `:301` | `int` | `bankedScoreUnits` | 同 `:298`；且 `TryBank` 的溢出守卫写死 `int.MaxValue` |
| `:1037-1074` | `float` | 启动时用 `TryDerive(0f, baseline…)` 反推起始球 | 传参全部是 float units；定点后 `TryDerive` 签名要改，这里跟着改 |
| `:1105-1157` | `float` | `ApplyStartDiameterWorldM`：用起始直径反写 `baselineVolumeUnits` | 这是**唯一**把「直径」写回「体积基线」的地方；定点后应写 `startVolumeMm3`，且反写必须 round 到 mm³（不能截断，否则 0.05 m 球的 0.015963 unit 会变 0） |
| `:3283` | `int gain` | `TryCommitAbsorb(itemInstanceId, generation, gain)` | 事件 gain 从 int → long；所有调用点跟着改 |
| `:3322` | `int` | `unbankedVolumeUnits = confirmed`（读回账本） | 同 `:298` |
| `:3339-3344` | `float` | `DeriveSize()` 把 `confirmedVolumeUnits` 喂 `TryDerive` | mm³ 直接传 float 会在 3e12 处丢 262,144 mm³；必须整数进入、内部再转 |
| `:3374-3377` | `float` | `RecomputeConfirmedAndSize`: `(float)unbanked + carried` | 唯一写「confirmed = unbanked + carried」恒等式的地方；定点后是 `long` 加法（精确），float 版本会破坏恒等式 |
| `:3519-3524` | `float` | 回合重置时重新 `TryDerive(0f, …)` | 同 `:1037` |
| `:3561-3563` | `int`/`float` | 重置三个量（`0` / `0f` / `0f`） | 常量化，类型跟随 |
| `:5213-5269` | `int`/`float` | `TryBankWithReason`：`unbankedVolumeUnits <= 0`、`TryBank(amount)`、`ApplyBankShrink` | `amount` 是 int；`confirmed < 0 || banked < 0` 的负数检查在 long 下仍需要，但 int32 的 `int.MaxValue - amount` 守卫在 `GrowthLedgerMath` 里必须换成 `long.MaxValue` |
| `:5279-5301` | `float` | `ApplyBankShrink(postBankVolumeUnits)` → `TryCarriedVolumeUnits(post, (float)unbanked)` | 把「已计分残量」用 float 相减；定点后整数相减，且「post 低于 stillUnbanked 就归零」的规则要保留 |
| `:5807` / `:5812-5815` | `float` | `GetSemanticDiameterWorldM` / `GetConfirmedVolumeUnits` | 公开读口；`GetConfirmedVolumeUnits` 的返回类型一变，`BallGrowthDiagnostics`、`CpuSkirmishBootstrap`、`LabBallSizeSetter` 三个消费者一起变 |
| `:5817` | `int` | `GetBankedScoreUnits` | 同上 |
| `:68` | — | `[UdonBehaviourSyncMode(BehaviourSyncMode.None)]` | 体积今天不同步；任何同步方案都要先把它改掉（`§5`） |

| 位置 | 现类型 | 角色 | 会坏什么 |
|---|---|---|---|
| `BallLayers/SizeModel/SizeModelMath.cs:105-118` | `float` × 6 | `TryDerive(confirmedUnits, baseline, c, capRadius, 两个 ratio, out ×4)` | 4 个 out 里 `effectiveVolumeUnits` 是诊断量；定点后签名要拆成「整数体积 + 浮点长度」，不能继续拿一个 float 既当体积又当单位 |
| `:125-187` | `float` | 对数域求和 + 饱和到 cap | 对数域是对「大数不溢出」的解法；**有了 int64 就不需要 log-sum-exp**，但饱和语义（超过 cap 就返回 cap）必须保留，且 `effectiveVolumeUnits` 的 float 输出会在大值处失真 |
| `:267-320` | `float` | `TryVolumeUnitsForSemanticDiameter`（唯一反函数） | 银行缩径靠它把直径折回体积；定点后必须返回 `long`，且**四舍五入策略要写死**（现在 `confirmed = totalUnits - baseline` 是纯浮点减） |
| `:333-335` | `float` | `LogCoreRadiusFromLogVolume` | 同上 |
| `BallMotor/BankShrinkMath.cs:53-70` | `float` | `TryPostBankDiameter`（直径空间缩径） | 直径仍是 float，**这一层不用改**；但它是「体积→直径→体积」往返的一半，往返的舍入会累积（见 `:84`） |
| `:84-92` | `float` | `TryBankVolumeUnits` 转发到反函数 | 返回 `float postBankVolumeUnits`；定点化后这颗舍入直接进账本，必须 round 而不是 trunc |
| `:106-118` | `float` | `TryCarriedVolumeUnits(post, stillUnbanked)` | 见 `KatamariBallMotor.cs:5279` |
| `Pickup/PickupAbsorbMath.cs:449-480` | `float` | `TryComputeGrowthRadius(base, growthPerValue, max, confirmedValueUnits)` | `growthPerValueWorldM × confirmedValueUnits` 是「每单位成长」的线性模型；**一旦 confirmed 变 mm³，这个乘法会放大 4.1e6 倍**，`growthPerValue` 的语义必须一起改（或这个函数废弃，改由 `TryDerive` 承担） |
| `:544-551` | `float` | `ApplyValueGate`：`TryAbsorbValueToLedgerGain(itemValue)` 后 `gain <= 0` 拒绝 | 整数价值契约的守门人 |
| `:568-580` | `float→int` | `TryAbsorbValueToLedgerGain`：`(float)(int)v != v` 即拒绝，`v >= 2147483648f` 拒绝 | **这是「小数道具价值 = 作者错误」的唯一实现**；定点化后「小数」概念消失（mm³ 本来就是整数），这条规则要么删除、要么改成「< 最小量子即拒绝」。`2147483648f` 这个字面量是写死的 int32 假设 |
| `Pickup/PickupFieldHost.cs:124` | `int` | `MinValueUnits = 1` | 最小值从 1 unit 变成 1 mm³（或 1 个新量子），语义完全不同 |
| `:179` | `int[]` | 序列化 `valueUnits` | 场景里存的是 int32 数组（`StoryMode.unity:17437` 的 130 行 hex），**换 long 会让每个场景的数组体积翻倍并重写 YAML** |
| `:783-786` | `int` | `IsSubmittable`: `valueUnits[index] < MinValueUnits` | 同 `:124` |
| `:852` | `int` | 启动自检统计 `valueUnitsBelowMin` | 报告文案里的「whole number >= 1」要重写 |
| `:1300-1307` | `int→float` | `SubmitCandidate(..., (float)valueUnits[index], ...)` | **int 在进电机时被提升成 float**；若 valueUnits → long 且量级到 1e12，这一句就是精度事故点 |
| `Economy/GrowthLedgerMath.cs:551-556` | `int gain` | `TryPredict(..., int gain)` | 预测增益；int32 在 mm³ 下装不下一次大件吸收（一件 0.5 m³ × 0.8 = 4e8 mm³，勉强；一件 3 m³ 就 2.4e9 溢出） |
| `:741-741` | `int gain` | `TryResolveClaims(..., int gain, ...)` | 同上 |
| `:833-841` | `int gain` | `TryApplyCommit(..., int gain, ...)` | 同上 |
| `:899-938` | `int amount` | `TryBank(..., int amount, ...)`；`amount > confirmedVolumes[row]`；`bankedScores[row] > int.MaxValue - amount`（`:925`） | **两处 int32 溢出守卫**（`:925` 与 `TryResolveClaims` 的 `:792`）；long 化后必须改成 `long.MaxValue`，否则守卫生效点错位 |
| `:953-1013` | `int attackerShare` | `TryKo(..., int attackerShare)`；`lost`/`share` 都是 int；`:985` `confirmedVolumes[attackerRow] > int.MaxValue - share` | KO 的「不创造价值」性质靠 `share = min(attackerShare, lost)` 保证；long 化后守卫同 `:924` |
| `:699-718` | `int[] awardedPoints` | `TryAwardOnce` 的奖励点 | 若奖励也用体积单位，同样溢出 |
| `Ai/CpuSkirmishBootstrap.cs:134-135` | `float`/`int` | 日志打印 `confirmed=` / `banked=` | 打印格式（`ToString`）要跟类型 |
| `:188` | `int` | `GetBankedScoreUnits()` 排序比较 | long 化后比较仍成立，但 `int` 局部变量会截断 |
| `:205-207` | `int` | 银行前后差值 | 同 §3.3：差值必须整数相减 |
| `:267-272` | `float` | `ProbeBank`: `float volume = GetConfirmedVolumeUnits()`，且**手抄**了 `3.4390244f, 0.0041f, 1.1f, 1f, 1f` 五个常数 | 定点化后这五个手抄值是**第二批要改的硬编码**（`:270-276` 已承认是抄的）；并且 `volume` 变 long 后 `TryDerive` 调用要改 |
| `BallPresentation/BallGrowthDiagnostics.cs:112` | `float` | `lastConfirmedUnits` 缓存 | 同 §3.3 |
| `:156-165` | `float` | `gainedUnits = confirmed - lastConfirmed`，打印三位小数 | **差值精度事故点**（§3.3）；定点后应 `long gained = confirmed - last;` 并除以量子打印 |
| `Story/*`（`StoryRoundController.cs:79-83` 等） | `float` | 星级 / 目标按**直径**比较 | 好消息：星级不读 units，读 `semanticDiameterWorldM`——**定点迁移不碰星级** |
| `Labs/CpuSkirmish/PlaygroundProp.cs:45` | `float` | 序列化 `valueUnits = 3f` | 这是「实验室没有任何整数阻拦」的根（`OPEN_DECISIONS.md:57` 点名）；小数价值在编辑器里畅通无阻，只在运行时以 `noGain` 暴露 |
| `:120` | `float` | `SubmitCandidate(..., valueUnits, ...)` | — |
| `Labs/PhysicsTestField/LabBallSizeSetter.cs:94-98` | `float` | 序列化 `sizeModelBaselineVolumeUnits` | 手工把 motor 的基线抄进 lab；定点后要抄 mm³ |
| `:281` / `:298-302` | `float` | `totalUnits = baseline + GetConfirmedVolumeUnits()` 后开三次方 | 同 §3.3 的大数相加；这是物理测试场的**直径网格量子**来源，改表示会改它的网格（`:50` 记录 0.30 m 处 ~29 mm/格） |

**Unity 内已存在的 `long` 先例（支持 int64 可行性）：** `BallLayers/Attachment/AttachmentMath.cs:73-75`（`HashOffsetBasis/HashPrime/HashMask`）、`:170`（`out long identityHash`）。本项目在 UdonSharp 里已经编译并通过 `long` 标量；**`long[]` 数组没有先例**，这是迁移前必须在 Editor 里验证的一条（本审计为只读，未验证）。

### 6.2 Editor（`Assets/VRC_Katamari/Editor`）

| 位置 | 现类型 | 角色 | 会坏什么 |
|---|---|---|---|
| `LevelBuild/PlacementImporter.cs:188` | `DefEntry[]` | `placement.json` 的 `defs` | 传输模型 |
| `:249` / `:265` | `int` | `DefEntry.valueUnits` / `PropEntry.valueUnits` | JSON 反序列化目标；long 化后 `JsonUtility` 对 `long` 的支持要实测（Unity 的 JsonUtility 支持 long，但 **UdonSharp 的 `Utf8Json`/手写解析不一定**） |
| `:553-554` | `int` | `report.ValueUnitsDeclared = SumDeclaredValueUnits(doc)` | 全关合计，Cancer 1,522/737,377；int32 在 mm³ 下装不下（3e12） |
| `:641-644` | `int` | `DeclaredValueUnits(doc, prop) < 1` 即报错 | 数据校验门 |
| `:963-986` | `int` | `DeclaredValueUnits`：prop 值优先，否则 def 值 | 有效值解析（与 `placement_core.effective_value_units` 必须同规则） |
| `:996-1013` | `int` | `BuildDeclaredValueUnits` → `Dictionary<string,int>` | long 化后字典类型变 |
| `:1017-1030` | `int` | `SumDeclaredValueUnits`（全关合计） | 同 `:553` |
| `LevelBuild/PlacementImportReport.cs:69-72` | `int` × 4 | `ValueUnitsDeclared/Preserved/Undeclared`、`bool Verified` | 报告计数；mm³ 下 int 溢出 |
| `:165-168` | `int` | `valueUnits: declared=… preserved=… read-back: verified` | 导入验收的输出契约，改类型后文案要跟 |
| `:178` | `string[]` | `ValueUnitProblems` | — |
| `Pickup/PickupFieldSync.cs:111` | `string` | `ValueUnitsField = "valueUnits"` | 字段名字符串；**如果改名成 `volumeMm3`，所有场景要重同步**（`PickupFieldHost` 的数组长度/顺序契约也一起变） |
| `:975` / `:1108` | — | 写/回读的字段清单（7 个数组之一） | 数组元数据（长度、顺序）不变，但元素类型从 `intValue` 变 `longValue` |
| `:1205-1230` | `int[] readValues` | 回读：`values.GetArrayElementAtIndex(i).intValue` | **必须变 `longValue`**；`FindValueUnitMismatches(string[], int[], …)` 签名跟着变 |
| `Pickup/PickupFieldAuthoringRules.cs:72` | `int` | `MinValueUnits = 1` | 与 runtime 的 `MinValueUnits` 是**两处写成两份的同一个规则**（`:72` 与 `PickupFieldHost.cs:124`），迁移时要一起改，否则导入通过、运行时拒绝 |
| `:157-186` | `int` × 3 | `TryResolveValueUnits(declared, hasDeclared, existing, hasExisting, out valueUnits, out source)` | 声明优先 / 保留既有 / 写 0 三条路 |
| `:199-230` | `int[]` | `FindValueUnitMismatches(itemIds, hostValueUnits, declared)`，逐件 `host != declared` | **比较点**；long 化后是 `long != long` |
| `:428-453` | `float→int` | `TryValidateValueUnits(raw, out int)`：拒 NaN/Inf/<1/≥2^31/非整 | 这里同时是「小数拒绝」与「int32 上界」两条规则；定点化要重写（见 §6.1 `PickupAbsorbMath.cs:568`） |
| `Pickup/PickupFieldSyncReport.cs:26,32,35,38,41,44` | `int`/`bool` | `TotalValueUnits`、`ValueUnitsRequested/Declared/Preserved/Undeclared/Verified` | 报告对象；`CountRow(string, int)` 与 `Kinds[i].ValueUnits +=` 都是 int 累加 |
| `PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs:643-646` | `float` | 从 motor 读 `baselineVolumeUnits`（`SerializedProperty.floatValue`） | 定点后 motor 字段变成 `long`，这里 `floatValue` 读到 0 → 直接走「cannot read a positive baseline」的失败分支 |
| `:653-661` | `float` | 写进 lab 的 `sizeModelBaselineVolumeUnits` + 一条 Note | 同 `:643`；Note 文案里的 `baselineVolumeUnits=…cubicMetersPerVolumeUnit` 两个字段名要改 |

### 6.3 Python 工具（`Tools/levelbuild`、`Tools/leveleditor`、`Tools/mapbuild`、`Docs/tools`、`parity_harness`）

| 位置 | 现类型 | 角色 | 会坏什么 |
|---|---|---|---|
| `Tools/levelbuild/placement_core.py:149` | tuple | `PROP_KEYS` 含 `"valueUnits"` | 允许键集合；改名/加 `volumeMm3` 要同时改 `editor_core.py:41` 与 `placement_core` 的 canonical 排序 |
| `:154` | tuple | `DEF_KEYS` 含 `"valueUnits"` | 同上 |
| `:1039-1041` | `int` | canonical 归一化：`elif key == "valueUnits": value = int(value)` | **把 JSON 数字强转 int**；一旦允许小数/大整数，`int()` 会静默截断 float 或保精度（Python int 无上限，问题是**下游 C# 收不下**） |
| `:1112` | `int` | structures 的 `valueUnits` 分支 | 同上 |
| `:1149-1185` | `int` | `def_catalog()` / `effective_value_units(prop, catalog)` | 有效值的**唯一 Python 实现**；C# `PlacementImporter.DeclaredValueUnits` 是它的镜像，两边必须同规则 |
| `:1343-1347` | `int` | defs 校验：`valueUnits is required` / `must be a whole number >= 1` | 硬门；mm³ 下「whole number >= 1」变成「>= 1 mm³」 |
| `:1436-1441` | `int` | props 校验：逐件值 whole >= 1，否则回落 def，否则报错 | 同 `:1343` |
| `:2085-2112` | `int` | `level.expected` 校验：`valueUnits %d does not match level.expected.valueUnits %d (%+d)` | **全关总量闸**；Cancer 737,377 / story_01 1,522；mm³ 下这些数变成 3.0e12 / 6.2e9，int 语义全变 |
| `:2237-2249` | `int` | `zone_stats` / `def_stats` 的 `valueUnits` 累加 | 统计表；大数下 Python 没问题，但输出给 Unity/前端的字段要跟 |
| `:2443-2452` | `int` | `value_units_total(doc)`：逐件 `effective_value_units` 求和 | **全仓库最重要的一个求和**（被 CLI、编辑器、校验器、web 前端共用） |
| `Tools/levelbuild/levelbuild.py:50-52` | `int` | `summary()` 里 `units=%d` | 打印格式 |
| `:117` | `int` | 导出 `"valueUnits": core.value_units_total(doc)` | — |
| `:406` | `int` | `int(entry.get("units", 1)) if > 0 else 1` | **从 manifest 的 `units` 复制成 valueUnits**；这是「值从哪来」的旧路径，U2 之后应该不再走 |
| `:548-552` | `int` | 逐件 `valueUnits` 一致时折成 def 值；不一致就保留逐件 | def 折叠逻辑；总量必须不变 |
| `:575` | `int` | `"expected": {"propCount": …, "valueUnits": total_units}` | 写回 expected |
| `:605-611` | `int` | `--update-expected` 与打印 | — |
| `Tools/levelbuild/levelctl.py:124-128`、`:160-165`、`:193-194` | `int` | CLI 的 `--value` → `valueUnits` | 命令行入参是 `int`（argparse `type=int`），大数/小数会被拒在门外 |
| `:299-301`、`:379-381` | `int` | `status` 打印 `valueUnits=%s` | — |
| `Tools/levelbuild/editor_core.py:41` | tuple | `_known` 允许键含 `"valueUnits"` | 见 `placement_core.py:149` |
| `:242` | `int` | `expected["valueUnits"] = totals["valueUnits"]` | 改了 prop 值就自动改 expected（`add_prop`/`set` 的副作用） |
| `:524` | `int` | 编辑器 status 的 `valueUnits` | — |
| `:899-905` | `int` | `add_prop`：def 无值时要求显式 `valueUnits` | 建件门 |
| `:1012-1013` | `int` | `set` 改 prop 值 | — |
| `:1157-1178` | `int` | `add_def`：`valueUnits` 必填、`_integer(..., minimum=1)` | `_integer()` 是唯一的强转点 |
| `:1183-1199` | `int` | `expected` op：`_integer({"valueUnits":…}, minimum=0)` | 允许 0（用于「先清空再填」） |
| `Tools/levelbuild/blender/export_placement.py:87-92` | `int` | `entry["valueUnits"] = int(obj["value_units"])`，缺失即 problem | **场景→JSON 的导出路径**；`int()` 会截断 `0.5` |
| `:270-272` | — | 注释记录「defs 全丢导致价值声明消失」的历史坑 | 迁移时同一类风险会重演 |
| `Tools/levelbuild/blender/blender_common.py:278` | dict | `("valueUnits", "value_units")` 字段映射 | Blender 自定义属性名 ↔ JSON 名 |
| `Tools/leveleditor/server.py:368-369` | `int` | `result["valueUnits"] = total(after) - total(before)` | **增量差值**；大数下仍正确（Python int），但前端显示要跟 |
| `:586` / `:622` | `int` | 打开关卡时每件的 `valueUnits` / manifest 的 `valueUnits` | web 编辑器的数据源 |
| `Tools/leveleditor/web/js/main.js:218,234-235,293,330-334,354-356,379,462,474,654,1057,1242-1243` | JS `number` | 显示 `${counts.valueUnits} u`、`if (!manifest.valueUnits)` 才可拖、`valueUnits` 行、直方图 tooltip | **JS number 是 float64**：mm³ 下 3e12 仍精确（< 2^53），但 `valueUnits` 如果变 long 且超过 2^53 就会丢；当前量级安全 |
| `Tools/leveleditor/web/js/view.js:813,825-833` | JS `number` | 按 zone/def 聚合 `valueUnits` | 同 `main.js` |
| `Tools/mapbuild/validate_map.py:156` | `str` | `absorb_value_placeholder` 列名 | 只读列名；值来自 `round_model` |
| `Docs/tools/prep_math/math_model.py:35-40` | `float` | `sphere_radius_from_volume(volume)`（注释写「game-volume units」） | 这是**另一条体积→半径的独立实现**（`Docs/Preparation/MathCases`），与 `size_model.py`、`SizeModelMath.cs` 三份并存；迁移时它是第三份要跟的 |
| `:56-60` | `float` | `add_contribution(ledger: Mapping[str,float], item_id, amount)` | 浮点账本参考模型；与 runtime 的整数账本口径不同，迁移后差距更大 |
| `Docs/Design/Parity/parity_harness/run_parity.py:119` | `Constant(3.4390244, "KatamariBallMotor.cs:193-197")` | 把运行时基线**抄成常数**并标注来源行 | 迁移后行号与数值都要改，否则 parity 报告会用旧标定算 |
| `:122` | `Constant(10.0, …)` | `absorb_volume_ratio` | 不用改（门一致） |
| `:182-185` | `int` | `self.declared_units = int(expected["valueUnits"])` | 只用于「声明总量」对账 |
| `:192-201` | `int` | 每件 `value_units = int(prop.get("valueUnits", 0))`、`volume_m3` | 门/供给比的分母 |
| `:219-220` | `int` | `value_volume()` = Σ units × `c_per_unit` | **把整数 units 乘回 m³** 的桥；这是「单位」概念在 parity 侧的落点 |
| `:237-260` | `float` | `_ball_volume` / `need` / 供给比 | 体积口径；若 unit 语义变，这些分母全部要复核 |
| `:604-606` | `str` | `volume_curve = "D=(6V/π)^(1/3)"` + 0.02 cm 的对比行 | 记录 G05/G09 的口径 |
| `:714-723` | `float` | 供给比计算（`volume_absorbable_at / ball_volume`） | — |
| `Docs/Design/Gameplay/round_model.py:115-117` | `float` | `units_to_max_diameter: float = 1356.0` | **成长成本标定**（与 `c` 是同一件事的两个写法）；定点后要么保留成「到满径需要多少 mm³」，要么删掉 |
| `:175` | `float` | `RoundItem.value_units: float` | 模型允许小数！与 runtime 的整数契约不一致（`PARITY_AUDIT.md:249` 已记） |
| `:218` | `int` | `MapItem.value: int` | mapbuild 的占位值 |
| `:234` | `int` | `int(row["absorb_value_placeholder"])` | 同上 |
| `:282` | `int` | `units_total = Σ count × value` | 总量 |
| `:317-318` | `int` | `zone_units(zone_ids)` | 按区求和 |
| `:440-471` | `float` | `_sphere_volume_m3` / `diameter_for_units` / `units_for_diameter` / `post_bank_diameter` | 模型的「units ↔ 直径」对；**这是离线模型与运行时唯一必须保持同形的四个函数** |
| `Docs/Design/BallLayers/SizeModel/size_model.py:18-21` | `float` × 5 | `SizeParameters`（baseline/cap/ratio） | 运行时 `TryDerive` 的 Python 孪生；`:27-36` 只校验正有限，不校验「单位是否够细」 |
| `:46-51` | `float` | `SizeState.effective_volume_units` | 诊断量 |
| `:83-120` | `float` | `derive_size` 的 log-sum-exp + 饱和 | 同 `SizeModelMath.cs:125-187` |
| `Docs/Design/BallPresentation/visual_growth/visual_growth_model.py:53-54` | `float` | `BASELINE_VOLUME_UNITS = 3.4390244` / `CUBIC_METERS_PER_UNIT = 0.0041` | 视觉成长报告的标定 |
| `:83-91` | `float` | `volume_units_for_diameter` / `confirmed_units_for_diameter` | 「从直径反推 units」的浮点版本 |
| `:94-101` | `float` | `core_radius_for_units` / `diameter_for_units` | 与运行时同形 |
| `:144-156` | `float` | `apparent_relative_growth` 里连续换算 | 报告数字会随表示变化（小数点后几位） |

### 6.4 placement.json schema

**仓库里没有 JSON Schema 文件**：`placement.json` 的契约是 `Tools/levelbuild/placement_core.py` 的键集合 + 校验函数 + `Docs/Design/Tools/LEVEL_AUTHORING.md` §4.2 的表。搜索 `*schema*` 只命中 `Docs/Preparation/AssetAuthoring/asset_authoring.schema.json` 等无关文件。

| 位置 | 现类型 | 契约 | 会坏什么 |
|---|---|---|---|
| `placement_core.py:148-156` | tuple | `PROP_KEYS` / `DEF_KEYS` / `STRUCTURE_KEYS` / `LEVEL_KEYS` | 字段名与 canonical 键序；新增 `volumeMm3` 要同时改这里、`editor_core.py:41`、`LEVEL_AUTHORING.md` §4.2 |
| `Docs/Design/Tools/LEVEL_AUTHORING.md:218` | `int` | `valueUnits` 行：「分数单位…按 def 由关卡数据声明」 | 字段的**唯一文字契约**；迁移后这行必须重写，且 `defs[].valueUnits` 与 `props[].valueUnits` 的优先级不变 |
| `LEVEL_AUTHORING.md:203` | `int` | 示例 `"valueUnits": 8` | 示例值 |
| `LEVEL_AUTHORING.md:326` / `:1070` | `int` | `expected: {propCount, valueUnits}` 示例 | 全关闸 |
| `LEVEL_AUTHORING.md:604` | — | 验收第 14 条：`valueUnits` 与 `level.expected` 相等 | 闸的文字版 |
| 全部 `Docs/Design/Levels/*/placement.json` | `int` | 130–648 行/关的 `valueUnits` | **数据要迁移**：`_ref_cancer` 657 处、`_ref_mas1` 754 处、`story_01` 175 处、`hub` 11 处、`physics_test_field/C01-C14` 各 1 处、`Tools/levelbuild/examples` 17 处 |
| 派生文件 | `int` | `*.model.json`、`*.gate_exact.model.json`、`original_defs.json`、`stats.json` | 都是生成物；`stats.json` 的 `valueUnitsUnscaled: 737377`、`volumeSupplyM3: 3022.263041` 是**量纲对照的锚**，迁移后必须重新生成而不是手改 |

### 6.5 测试

C# NUnit（`Assets/VRC_Katamari/Tests/EditMode`）：

| 文件 | 现断言 / 常数 | 迁移后 |
|---|---|---|
| `BallLayers/SizeModelTests.cs:35-36` | `CalibratedBaseline = 3.4390244f`、`CalibratedCubicMetersPerUnit = 0.0041f` | 若只换类型，这些常数保留；若换量子，全部重算 |
| `:320` | 「/0.0041 m³ 每单位，减 3.4390244 基线」 | 注释与断言一起改 |
| `:458-463` | `InverseWith(...)` 转发 `TryVolumeUnitsForSemanticDiameter` | **签名改变即编译失败**（4 个 float 入参 → long/float 混合） |
| `BallMotor/SizeCalibrationTests.cs:35-36` | 同两个常数 | 同上 |
| `:73` / `:98` | `DiameterForUnits(1356f)`、`Assert.AreEqual(1356.4f, UnitsForDiameter(2.20f), 1.0f)` | **黄金值**：1356.4 units；换量子后这个数变成 5.56e9 mm³ |
| `BallMotor/AbsorbSizeLimitTests.cs:39` | `UnitsAtCap = 1356f` | 黄金值 |
| `:47` | `CapBallVolumeWorldM3 = 5.5737f` | 体积锚，可保留 |
| `:121-123` | 「2.20 m 是 1356 units 的上限」「5.5737/10 = 0.5574 m³」 | 注释与除数 |
| `BallMotor/BankShrinkMathTests.cs:31-32` | 两个标定常数 | — |
| `:122` | 「0.1117/0.0041 = 27.249 total units，减 3.439 基线」 | 黄金值 |
| `BallMotor/BankReasonTests.cs:97-98` | `SetField("cubicMetersPerVolumeUnit", cubicPerUnit)` / `baselineVolumeUnits = volume/cubicPerUnit` | **反射写字段**；字段改名/改类型直接失效 |
| `:127-161` | `SetStartDiameter(0.53f, 0.0041f)` ×5 | 同上 |
| `BallLayers/StartSizeTests.cs:34` / `:70-71` | `CubicMetersPerUnit = 0.0041f`、`baseline = (float)GetField("baselineVolumeUnits")` | 反射读 float；long 后 `(float)GetField` 抛 `InvalidCastException` |
| `:88` / `:101` / `:115-122` | `Assert.AreEqual(3.4390244f, (float)GetField(...), 1e-6f)` ×2、`smallBaseline/largeBaseline` | **黄金值 + 反射类型** |
| `:23-24` | 「0.05 m 球 = 0.015963 units，连续体积未批准」 | 注释：这是「起始球径可小于 1 unit」的证据 |
| `Pickup/PickupFieldValueUnitsTests.cs`（全文件） | 4 个 U2 验收场景：declared/preserved/zero/mismatch | 若字段/类型改名，这四条要重写（它们锁的是**规则**，不是数字） |
| `Pickup/PickupAbsorbTests.cs:262` | 「baselineVolumeUnits/cubicMetersPerVolumeUnit through SizeModelMath」 | 注释 |
| `Pickup/AbsorbSizeGateTests.cs:158` | 「ball's volume (3.4390244 * 0.0041)」 | 注释 |
| `Economy/GrowthLedgerTests.cs`（约 20 条断言） | 小整数 3/4/5/6/7/11 | **只换 int→long 则数值全不动**；换成 mm³ 则每条都要乘 4.1e6 |
| `Economy/GrowthLedgerResetTests.cs:105,117,146,155,193,296` | 同上 | 同上 |
| `Economy/LedgerTestHost.cs` | `int[] ConfirmedVolumes/BankedScores/CommitGains/PredictionGains` | **数组类型**；换 long 后整个 host 跟着换 |
| `BallLayers/SupportSurfaceTests.cs:598` | 「one volume unit = 0.0041 m³」 | 注释 |
| `Story/*Tests` | 按直径/时间断言，不读 units | **不受影响**（这是好消息：星级与回合逻辑正交） |

Python（`Docs/tests`、`Tools/mapbuild/tests`）：

| 文件 | 现断言 | 迁移后 |
|---|---|---|
| `Docs/tests/levelbuild/test_placement_core.py:63,66-80` | expected `valueUnits: 6`、defs 1/1/4、props 1/1/4 | 小整数 fixture |
| `:195-197` | `valueUnits: 1` / expected 7 | — |
| `:240-244` | `def` 回落的合计 = 6 | 规则断言 |
| `Docs/tests/levelbuild/test_levelbuild_gate.py:74-75` | expected 412 不匹配即红 | **黄金总量** |
| `:173-174` | 4 件 / 7 | — |
| `Docs/tests/levelbuild/test_level_editor.py:179-181` | `set valueUnits=2` 被拒、保持 1 | 编辑器闸 |
| `:191-236` | add_def 9 → prop 9 → expected 34 | — |
| `:395-396,454-460` | 总量守恒（改前=改后） | — |
| `Docs/tests/levelbuild/test_levelbuild_fields.py` | 1 处 | — |
| `Docs/tests/visual_growth/test_visual_growth_model.py`（8 处） | 3.4390244 / 0.0041 及读数 | 报告黄金值 |
| `Docs/Design/Gameplay/tests/test_round_model.py:282` | `total_value = Σ count × value_units` | — |
| `Docs/tests/ball_size_model/test_size_model.py` | `SizeParameters` 常数 | — |
| `Docs/tests/parity/support.py:142-154` | fixture `valueUnits: 1/64` | parity 语料 |
| `Docs/tests/parity/test_run_parity.py` | 47 行指标 | 供给比/曲线数字随标定动 |
| `Docs/tests/levelbuild/test_physics_test_field_cells.py`（5 处） | cell 的价值/尺寸 | — |
| `Tools/mapbuild/tests/test_story_mode.py:106,337` | `absorbable_value_units == 84`（两处） | **黄金值** |
| `Docs/tests/ball_attachment/test_growth_attachment_simulation.py`（1 处） | 成长/附着仿真 | — |
| `Docs/tests/test_preparation_content.py`（2 处） | 准备内容表 | — |
| `Docs/tests/prep_math/test_math_model.py:51` | `add_contribution({}, "fruit", 3)` | 浮点账本模型 |

### 6.6 文档

| 文件:行 | 内容 | 迁移影响 |
|---|---|---|
| `Docs/OPEN_DECISIONS.md:53-59` | 「账本只收整数，小数当场拒绝」（决定 j），引用了 `PickupAbsorbMath.cs:266/268`、`GrowthLedgerMath.cs:52-54` 等**旧行号** | **必须重写**：定点后「小数」概念消失，规则变成「小于一个量子即拒绝」；这条是本迁移的宪法级冲突点 |
| `Docs/OPEN_DECISIONS.md:99` | U2：`valueUnits` 由关卡数据按 def 声明 | 若改成 `volumeMm3`，这条要改口径但保留「内容声明」的性质 |
| `Docs/02_TECHNICAL_CONTRACTS.md:34,45,47` | 「价值/体积累加」「核心半径由确认体积更新」「旧视觉物件可回收但体积留在账本」 | 抽象描述，不用逐字改，但「确认体积」的定义要跟 |
| `Docs/Design/Parity/PARITY_AUDIT.md:24-25` | divergence 行：体积定义（k）/ 体积口径（restore，D5，1 mm³ long） | **本迁移的授权来源**；迁移完成后这两行要更新「我方」列 |
| `:30` | 每关分段回收曲线（restore） | 与定点表示正交，但同批做最省事 |
| `:83-84`（F4/F5） | 原作 10× 门 / 0.8 惩罚的证据行 | 数据依据 |
| `:143-144`（`G06`/`G07`） | 回收比例 / 10× 门的一致性 | 数值锚 |
| `:227-255`（D2/D5 + §5.2 七行） | §5.2 已经写了本迁移的 7 个受影响点（含「1 mm³ long」推荐） | **本文件是它的展开实现版**；`:249` 的「推荐整数定点」与本文 §7 一致 |
| `:252`（D6）、`:31` | 每关起始/目标球径 | 定点化后起始体积要按关存 mm³ |
| `Docs/Design/Parameters/README.md:35-42` | 3.4390244 / 0.0041 的重标定史与重算 | 迁移后这段「标定史」变成历史；新常数是 mm³ |
| `:222-223` | `baked: baseline_volume_units / cubic_meters_per_volume_unit` | 机器可读参数块 |
| `:241` | `units_to_max_diameter_effective: 1356.39` | 黄金值 |
| `:568-572` | 「迁移必须烘当前标定，不能写回 1/0.0141」 | **迁移警告，正好适用** |
| `Docs/Design/PickupAbsorb/README.md:39,181,192,212,231,262` | 「整数单位契约（有意为之）」 | 需重写：mm³ 不是「取消整数」，而是**把量子定得足够细**，整数契约仍成立 |
| `Docs/Design/PickupAbsorb/SIZE_GATE_AUDIT.md:118-122` | `V=(3.4390244+U)×0.0041`、`U(D)=(πD³/6)/0.0041−3.4390244` | 两个公式都要换成 mm³ 版 |
| `Docs/Design/Gameplay/README.md:56,92,173,192,206` | 1356 单位满径 / 1284 全图 / 「交分公式是本项目发明的」 | 黄金值与「本项目发明」的自认 |
| `Docs/Design/BallMotor/README.md:144,301,368` | 标定三数、交分后 0.5973 m / 23.8 units | 黄金读数 |
| `Docs/Design/BallLayers/SizeModel/README.md`（1 处）、`SIMULATION_REPORT.md`、`SuiteFoundation/HANDOFF.md` | 体积单位提及 | 注释级 |
| `Docs/Design/BallPresentation/VISUAL_GROWTH_AUDIT.md:28,79,189` | 参数来源、公式、CSV 列 | 视觉报告要重生成 |
| `Docs/Design/Tools/LEVEL_AUTHORING.md:218,203,326,477,483,487,604,621,695,799,827` | 数据契约 + 验收 | 见 §6.4 |
| `Docs/Design/Tools/LEVEL_EDITOR_WEB.md:196,214,287,321,364` | web 编辑器口径「个数 / 体积 m³ / valueUnits」 | 三口径并存的事实；迁移后 m³ 与新整数可以互证 |
| `Tools/levelbuild/README.md:81-98,131-135,182,282,326,349` | 示例 JSON、`valueUnits` 是内容数据 | 示例要跟 |
| `Docs/Design/StoryMode/{LEVEL_CONTENT_PLAN,PROP_SET_PLAN,PICNIC_CORNER_LAYOUT,PICNIC_CORNER_LEVEL_ONE,README}.md`、`Docs/Design/StoryMode/PROP_CATALOG.md` | 每关的价值表/总量 | 数值表要重算 |
| `Docs/Design/Levels/{_ref_cancer,_ref_mas1}/README.md`、`physics_test_field/README.md` | 建材说明 | — |
| `Docs/Design/Maps/{PicnicLawn,ToyRoom}/README.md` | 体积/价值口径 | — |
| `Docs/Design/PickupAbsorb/COLLIDER_LAYER.md:4 处` | 「value units pending one rubric」已解除 | 历史句 |
| `Docs/Preparation/AssetAuthoring/{README.md,asset_authoring.schema.json,examples.json,validate_authoring.py}` | `valueUnits` 字段 | **第二个 schema**（资产授权），与 placement 不是同一份，迁移要一起看 |
| `Docs/Preparation/MapRubrics/{resources.csv,spawn_fairness.csv}` | 每件价值 | 数据 |
| `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` / `Docs/Design/Gameplay/round_model.py` 的 catalog | `absorb_value_placeholder` | 占位值来源 |
| `Docs/Research/ReferenceRules/unknown_handling.md` | 1 处 | — |
| `Docs/Evidence/**`（12 个 log/README）、`Docs/Archive/Tooling/tools/third_batch/validate.py` | 历史证据 | **不改**（Archive 是历史） |
| `tasks.json:1 处` | 任务描述 | 迁移任务落这里 |

### 6.7 已序列化的场景 / asset 数据

| 文件:行 | 现形状 | 迁移影响 |
|---|---|---|
| `Worlds/StoryMode/StoryMode.unity:17437` | `valueUnits:` 一行 1,040 hex 字符 = **130 个 int32**（little-endian，和 = **1,522**，max 212），与 `story_01_bench_size/placement.json` 的 130 件/1,522 units 完全一致 | 换 `long[]` 后每行变 2,080 hex 字符；**必须重同步并重新提交场景** |
| `StoryMode.unity:12619-12621` | `baselineVolumeUnits: 3.4390244` / `cubicMetersPerVolumeUnit: 0.0041` / `maxCoreRadiusWorldM: 1.1` | 字段改名/改类型 → Unity 丢掉旧键、走新默认值（`.cs` 的 `[SerializeField]` 名是契约） |
| `Labs/CpuSkirmish/CpuSkirmish.unity`（32 处） | 每个 `PlaygroundProp` 一个 `valueUnits:`（3/8/1/2/13/5/…） | 实验室道具，32 行要改 |
| `Labs/CpuSkirmish/PlaygroundProp.asset` | 2 处 | 同上 |
| `Labs/PhysicsTestField/PhysicsTestField.unity`（2 处）、`LabBallSizeSetter.asset`（1 处） | `sizeModelBaselineVolumeUnits` | 浮点序列化值 |
| `Runtime/BallMotor/KatamariBallMotor.asset`（4 处） | 预制体上的四个标定字段 | 同 `StoryMode.unity:12619` |
| `Runtime/Pickup/PickupFieldHost.asset`（2 处） | `valueUnits` 数组 | 同 `StoryMode.unity:17437` |
| `Assets/SerializedUdonPrograms/*.asset` | Udon 编译产物 | **改字段类型会让所有 U# 程序重新编译**；按 AGENTS.md，签名级 diff 要成批处理 |

---

## 7. 推荐表示

### 7.1 推荐（一层表示 + 一层口径）

| 层 | 推荐 | 理由 |
|---|---|---|
| **编码** | `long volumeMm3`，**1 LSB = 1 mm³ = 1e-9 m³** | ① `PARITY_AUDIT.md:25` 已拍 D5；② 与原作唯一的量化点（`Req Pickup Size` 整 mm）同源；③ int64 上限 9.22e9 m³，30 m 球（1.41e10 mm³）也放得下；④ Udon 支持 `long` 标量（`AttachmentMath.cs:73-75`）；⑤ 加法精确、可交换 → 重放幂等 |
| **口径** | 体积以 m³ 为**作者/显示单位**，mm³ 只做**账本编码**；`c` 与「unit」**从表示里删除** | 原作没有 `c`；保留它就是保留一个「我们的发明」当基础设施。删掉后 `V` 是唯一真源，单位只用于显示（`PARITY_AUDIT.md:247` 的推荐做法） |
| **换算** | `1 旧 unit = 0.0041 m³ = 4,100,000 mm³`（**精确整数**，可无损迁移） | 现有全部 `valueUnits` × 4,100,000 就是 mm³，**不需要重新定价** |
| **起步状态** | `startVolumeMm3 = 14,100,000`（= 0.0141 m³）/ 每关覆盖 | 替代 `baselineVolumeUnits` |
| **直径** | `D_m = (6V/π)^(1/3) + 0.0002`（+0.02 cm），寄存器仍 `float`；**门与显示在直径上取整到 mm** | 照抄原作 O1/O2/O5 |
| **道具价值** | `V_item_mm3 = round(k · a·b·c · 1e9)`，`k` 默认 0.70 逐型可覆盖；`props[].volumeMm3` 允许逐件覆盖 | 照抄 O3；把 `valueUnits`（分数）退役成派生的显示量 |
| **增长** | `ΔV = round(penalty(D) · V_item_mm3)`，penalty 按关分段线性 | 照抄 O6/O7 |
| **门** | `V_ball_mm3 ≥ 10 · V_item_mm3`，需要球径向下取整到 mm | 照抄 O2 |

**为什么不是 int32 的某个变体：** 见 §4——mm³ 要 2.147 m³（比 2.20 m 球还小）；cm³ 要 2,147 m³（小于 Cancer 的 3,022 m³）；milli-unit 虽能装下（8,804 m³），但量子由 `0.0041` 定义，等于把「我们的发明」升格为跨端常量，且余量只有 2.9×（一次补货或 NG+ 就可能撞线）。

**为什么不是 float64：** float64 能表示 +1 mm³ 到 2^24 mm³ ≈ 0.0168 m³（和 float32 同一个窗口，只是量子换成 mm³ 后窗口的绝对值不同——`double` 的 53 位尾数把窗口推到 `2^53 mm³ = 9.0e6 m³`，确实够用）。但 double 在 Udon 里不是一等公民、跨端序列化是 8 B 且**不能保证逐位一致**（NaN/-0.0/舍入），而整数加法天然一致。**double 可行但不是最优；不需要为了「连续」牺牲确定性。**

### 7.2 被否掉的方案

| 方案 | 上限 | 否掉的理由 |
|---|---|---|
| `int` + 1 mm³ | 2.147 m³ | **小于满径球 5.5737 m³** |
| `int` + 1 cm³ | 2,147.5 m³ | 小于 Cancer 的 3,022 m³ 供给 |
| `int` + micro-unit | 8.80 m³ | 比满径球大一点点，Cancer 溢出 344× |
| `int` + milli-unit | 8,804.7 m³ | 能装下，但量子绑死 `0.0041`、余量 2.9×；**可作为「必须先上 int32 又不能 int64」时的兼容档** |
| `float` + 任意细量子 | 2^24·q | §3；且重放不幂等 |
| `double` + mm³ | 9.0e6 m³ | 可用，但 Udon 序列化与跨端一致性没有整数强 |
| `int` + 「unit」（现状） | 8.80e6 m³ | **没有溢出问题，问题是量子太粗**：一件 4 cm 的苹果和一件 8 cm 的橙子都只能记 1 unit |

---

## 8. 迁移顺序

原则：**先加宽、再换量子、最后改口径**，每一步都能独立编译/测试/回滚。前两步「测试数字不变」，第三步才开始动黄金值。

### Phase A — 只加宽类型，不改量子（可独立合并）

1. `GrowthLedgerMath` 的 6 个 `int` 参数/数组（`gain`、`amount`、`attackerShare`、`awardedPoints`、`confirmedVolumes`、`bankedScores`、`commitGains`）→ `long`；全部体积相关的 `int.MaxValue - x` 守卫（`:792`、`:868`、`:925`、`:985`）→ `long.MaxValue - x`。
2. `KatamariBallMotor` 的 `unbankedVolumeUnits` / `bankedScoreUnits` → `long`；`carriedVolumeUnits` / `confirmedVolumeUnits` 保持 `float`（只是显示量），但 `RecomputeConfirmedAndSize` 改成 `(float)unbanked + carried` 不变。
3. `PickupAbsorbMath.TryAbsorbValueToLedgerGain(float, out int)` → `out long`；`2147483648f` 上界改成 `9.2e18` 或直接删（因为 Phase C 会重写这个函数）。
4. `LedgerTestHost` 的 int[] → long[]；`GrowthLedgerTests`/`GrowthLedgerResetTests` 的断言数值**不变**（C# 的 `Assert.AreEqual(7, host.Confirmed("a"))` 在 long 下仍然过）。
5. **验收**：EditMode 全绿，场景不需要重导入（序列化数组仍是 int，只是运行时不写回）。
   → 这一步就把「int32 求和溢出」这条风险消掉，且**零数据迁移**。

### Phase B — `valueUnits` 加宽到 `long`（数据形状变，语义不变）

6. `PickupFieldHost.valueUnits: int[]` → `long[]`；`PickupFieldSync` 的写/回读从 `intValue` 改 `longValue`；`PickupFieldAuthoringRules` 的 `TryResolveValueUnits` / `FindValueUnitMismatches` / `TryValidateValueUnits` 跟着改。
7. `PlacementImporter` 的 `DefEntry/PropEntry.valueUnits` → `long`；`PlacementImportReport` 的 4 个计数 → `long`。
8. Python：`placement_core` 的 `int()` 归一化保持（Python int 无上限），但**校验上界**要与 C# 一致（`<= 2^53` 以免 JS 前端丢精度）。
9. **重导入所有关卡**（`StoryMode`、`CpuSkirmish`、`PhysicsTestField`、`CpuSkirmish` prefab），提交场景 diff（130 + 32 + … 行的 hex 变宽）。
10. **验收**：`placement_core.value_units_total` 与原 `level.expected.valueUnits` 逐关相等；导入报告 `read-back: verified`；C# 的 `PickupFieldValueUnitsTests` 四条规则不变。

### Phase C — 换量子：`valueUnits` → `volumeMm3`（口径变，黄金值开始动）

11. 定义唯一的换算：`MM3_PER_LEGACY_UNIT = 4_100_000`；写一个**双向**函数（Python 一处、C# 一处、Udon 一处），并在 `Docs/Design/Parameters/README.md` 记录「旧 unit 已退役，仅用于迁移换算」。
12. `KatamariBallMotor`：`baselineVolumeUnits`/`cubicMetersPerVolumeUnit` → `startVolumeMm3`（+ 每关覆盖）；`TryDerive` 拆成「整数体积进、浮点长度出」；`SizeModelMath.TryVolumeUnitsForSemanticDiameter` → `TryVolumeMm3ForSemanticDiameter`（返回 long，round 而非 trunc）；`BankShrinkMath.TryBankVolumeUnits` 跟着改。
13. `PickupFieldHost.valueUnits` 改名 `volumeMm3`（**这一步会让未重导入的场景丢字段**，必须在同一次提交里重导入全部场景/预制体）。
14. `PickupAbsorbMath.TryAbsorbValueToLedgerGain` 重写为「mm³ 直接就是 gain」——小数拒绝规则消失，`MinValueUnits = 1` 变成「>= 1 mm³」。
15. 重新生成 Python 侧：`round_model`（`units_to_max_diameter` → `mm3_to_max_diameter`）、`size_model`、`visual_growth_model`、`run_parity`、`placement_core` 的 `expected`、`stats.json`、所有 `*.model.json`。
16. **验收**：同一关在旧/新表示下算出的**直径**逐帧一致（公差 1e-6 m）；`_ref_cancer` / `_ref_mas1` / `story_01` 的满径时刻不变；`docs/tools/validate_plan.py` + `python3 -m unittest discover -s Docs/tests` 全绿。

### Phase D — 恢复原作的数值形式（parity，独立立项）

17. `V_item = k · a·b·c`（`k` 默认 0.70，逐型 `pickupVolumeFactor`）；`defs[].valueUnits` 退役成派生显示值。
18. `ΔV = penalty(D) · V_item`，per-mission 分段表 + 逐件 `pickupVolumePenalty`。
19. 门/显示：`D_req` 向下取整 mm；HUD `+0.02 cm`。
20. 群组物件：父件拾取连带子件体积。
21. 每关起始/目标/上限（D6）。

> Phase A/B 可以现在做（无行为变化）；Phase C 是「细粒度定点」这个需求本身；Phase D 是 owner 已拍 `restore` 但尚未实现的部分（`PARITY_AUDIT.md:24-30`）。**不要**把 C 和 D 合成一步：C 只改编码，D 改数值，两者的失败信号完全不同（C 错 = 直径漂移，D 错 = 成长曲线变）。

---

## 9. 测试影响

### 9.1 会变的黄金值（按 Phase）

| 黄金值 | 现值 | Phase A | Phase B | Phase C | Phase D |
|---|---|---|---|---|---|
| `SizeCalibrationTests` 满径 units | `1356.4` | 不变 | 不变 | `≈5.56e9 mm³`（1356.4 确认 units） | 按新 `c`/`k` 重算 |
| `AbsorbSizeLimitTests.UnitsAtCap` | `1356f` | 不变 | 不变 | `≈5.56e9`（= 1356 × 4.1e6） | 重算 |
| `AbsorbSizeLimitTests.CapBallVolumeWorldM3` | `5.5737f` | 不变 | 不变 | 不变（m³ 锚） | 不变 |
| `SizeModelTests.CalibratedBaseline` | `3.4390244f` | 不变 | 不变 | `14_100_000` mm³ | `startVolumeMm3` 每关不同 |
| `SizeModelTests.CalibratedCubicMetersPerUnit` | `0.0041f` | 不变 | 不变 | **删除** | 删除 |
| `BankShrinkMathTests` 27.249 units | `27.249` | 不变 | 不变 | `111_720_900 mm³` | 重算（penalty） |
| `StartSizeTests` 3.4390244 断言 ×2 | 3.4390244 | 不变 | 不变 | `14_100_000` | — |
| `StartSizeTests` 0.015963 units（5 cm 球） | 0.015963 | 不变 | 不变 | `65_448 mm³` | — |
| `GrowthLedgerTests` 3/4/5/6/7/11 | 小整数 | **不变**（只换 long） | 不变 | ×4.1e6 | ×4.1e6·penalty |
| `test_levelbuild_gate` 412 | `412` | 不变 | 不变 | `1_689_200_000` | 重算 |
| `test_placement_core` 6/7 | 小整数 | 不变 | 不变 | ×4.1e6 | 重算 |
| `test_level_editor` expected 34 | `34` | 不变 | 不变 | `139_400_000` | 重算 |
| `test_story_mode` `absorbable_value_units == 84` | `84` | 不变 | 不变 | ×4.1e6 | 重算 |
| `visual_growth_model` 读数 | 浮点 | 不变 | 不变 | 末位变化 | 曲线变 |
| `stats.json` `valueUnitsUnscaled: 737377` | 737,377 | 不变 | 不变 | `3.023e12`（或改键名 `volumeMm3`） | 重算（×k×penalty） |
| `run_parity` 供给比 / `value_volume` | 0.46× 等 | 不变 | 不变 | 不变（比值） | 变（×0.8×0.7） |

### 9.2 会**编译失败**（签名变化，不是数值）

- `SizeModelTests.InverseWith`（`:458-463`）——`TryVolumeUnitsForSemanticDiameter` 签名。
- `StartSizeTests`（`:71,88,101,115,122`）——`(float)GetField("baselineVolumeUnits")` 反射。
- `BankReasonTests`（`:97-98,127-161`）——`SetField("cubicMetersPerVolumeUnit", …)` 反射。
- `LedgerTestHost` + 8 个 `GrowthLedger*` 调用点——数组类型。
- `PickupFieldValueUnitsTests`——`int valueUnits` 出参。
- `BallGrowthDiagnostics` / `CpuSkirmishBootstrap` / `LabBallSizeSetter`——`GetConfirmedVolumeUnits()` 返回类型。

### 9.3 完全不受影响

- `Story/*Tests`（星级、回合、目标都按直径/时间比较）。
- 坐标/接触/支撑/放射代理/附着（`BallContact/*`、`BallLayers/*` 除 SizeModel）。
- `Docs/tests/parity` 的结构断言（只动比值）。

### 9.4 本轮**没有**跑的验证（只读审计）

Unity EditMode 与 Python 测试**都没有跑**（本轮禁止开 Unity；也不做 Git 操作）。上面所有「会变」是按源码静态推的。落地时按 AGENTS.md 的顺序：`python3 Docs/tools/validate_plan.py` + `python3 -m unittest discover -s Docs/tests`，再到 Editor 里 `refresh_unity` + `run_tests`。

---

## 10. 与原作的分歧清单（哪些有记录、哪些没有）

`PARITY_AUDIT.md:22-37` 的 divergence 表已有 14 行。体积相关的行只有 **2 行**（`体积定义（k 系数）`、`体积口径`），它们都写着 `restore`。下表把「体积这条链上我们和原作不一样」的地方**逐条对齐到那 14 行**；**表里没有行的**单列出来（这是本轮审计要暴露的东西）。

| # | 区域 | 原作 | 我方 | parity 表里有行？ | 有记录的理由？ | 状态 |
|---|---|---|---|---|---|---|
| R1 | 存储类型 | float32 m³（`f32Volume`），单机 | 建议 int64 mm³ | **部分**（`体积口径` 行覆盖「整数 units」，写法是 `restore` + D5） | 有：跨端确定性 / 幂等重放（Ledger README、`OPEN_DECISIONS:57`）；**但这不是 parity 表的四类理由之一**（VRChat 限制/性能/授权/刻意设计）→ 必须补写成「刻意设计」 | 需补理由 |
| R2 | 量子位置 | 只在**直径**上量化（1 mm） | 在**体积**上量化（1 unit = 0.0041 m³） | **是**（`体积口径`） | `none -> restore` | 已记录，待实现 |
| R3 | 体积定义 | `V = k·bbox`，k 默认 0.70 | `a·b·c`（k=1） | **是**（`体积定义`） | `none -> restore` | 已记录，待实现 |
| R4 | 单件回收 | `0.8 · V_item` | `1.0 · V_item` | **是**（`单件回收比例`） | `none -> restore` | 已记录，待实现 |
| R5 | 逐件 penalty | `pickupVolumePenalty` 0.01–1.0（1198/1436 = 0.8） | 无 | **无独立行**（只被 `单件回收比例` 一行笼统覆盖） | 无 | **缺行** |
| R6 | 每关分段曲线 | 每关 (size, ratio) 折点 | 全局常数 | **是**（`每关分段回收曲线`） | `none -> restore` | 已记录，待实现 |
| R7 | 门槛取整 | 向下取整到 mm | 浮点比较 | **是**（`门槛取整`） | `none -> restore` | 已记录，待实现 |
| R8 | HUD 偏置 | +0.02 cm | 0 | **无独立行**（指标表 `G09` 有，divergence 表无） | 无 | **缺行** |
| R9 | 群组体积 | 父件拾取连带子件体积 | `group`/`groupId` 只存不读，无价值聚合 | **无独立行**（`移动件/行为字段` 提到 `parentProp`，但说的是行为字段不是体积规则） | 无 | **缺行** |
| R10 | 交分/缩径 | 原作**没有**「交分」概念（增长只增不减） | 银行后按直径空间缩径（`post = max(start, start + (pre−start)·0.25)`），残量 `carried` 记入且不再计分 | **无行** | Gameplay README `:92` 自认「这个公式是本项目发明的」，但没有任何一类 parity 理由（不是 VRChat 限制/性能/授权/owner 拍板记录） | **缺行 + 缺理由** |
| R11 | 每关球径 | MAS1 5→10 cm、Cancer 22.2→80 cm | 一套 0.30→1.25 m，且运行时不从关卡 JSON 读 | **是**（`每关起始/目标球径`） | `none -> restore` | 已记录，待实现 |
| R12 | 全局上限 | 每关参数 | 全局 2.20 m | **是**（`球径上限`） | 有：设计（先留一个全局上限） | 已记录，keep |
| R13 | 分数/账本 | 单机、无 per-player 账本 | per-player confirmed/banked + 幂等键 | **无行** | 有：02_TECHNICAL_CONTRACTS / Ledger README（多人设计），但**不在 parity 表** | 需补行 |
| R14 | 「unit」本身 | 不存在 | `c = 0.0041 m³/unit` 是基础设施 | **部分**（`体积口径`） | `none -> restore` | 已记录，待实现 |

**结论（对本迁移的直接指示）：** R1/R2/R14 合起来说明「整数 unit」这个结构**本身**就是分歧；`restore` 的正确落法不是「把 int 换成 long 继续用 unit」，而是**把量子从体积上移到直径上**——即 §7 的推荐：体积用 mm³ 编码（不引入任何 `c`），直径用 mm 取整。只做 Phase A/B（加宽）而不做 Phase C（去 unit）会把 R2/R14 永久固化。

---

## 11. 风险与未决

1. **`long[]` 在 UdonSharp 里没有先例**（`long` 标量有，见 `AttachmentMath.cs`）。Phase B 第一步就该在 Editor 里用一个 `long[]` 序列化字段 + Udon 编译验证；失败则退回「`int[]` 拆高低位」或「`int[]` + milli-unit」（§4 的兼容档）。本轮只读，未验证。
2. **场景 YAML 是手写十六进制**：`StoryMode.unity:17437` 的 130 个 int32 是 Unity 序列化的结果，**不能手改**，必须走重导入（`PickupFieldSync`）+ Editor 保存，否则 fileID/顺序会错。
3. **`valueUnits` 改名 = 破坏性 schema 变更**：`PickupFieldHost` 的 `[SerializeField]` 名就是场景键，改名后旧场景会静默丢字段（`PickupFieldHost.cs:904-966` 已有「re-sync it」的 reason 码）。必须与重导入同批。
4. **`u` 文案与 `level.expected.valueUnits` 是外部契约**：web 编辑器、CLI、导入报告、关卡 README 都在打印 `u`；改名后要么全改，要么保留 `u` 作为显示单位（推荐：保留 `u` 仅作显示，账本字段叫 `volumeMm3`）。
5. **`OPEN_DECISIONS` (j) 是宪法级冲突**：它把「小数价值 = 作者错误」写成项目决定。定点方案下「小数」不存在，这条必须重写为「小于一个 mm³ 即拒绝」，否则实现会同时遵守两条互相矛盾的规则。
6. **`c = 0.0041` 的删除会影响 parity 报告**：`run_parity.py:119` 把它抄成常数并标注了 `KatamariBallMotor.cs:193-197` 的行号；删字段后每次 parity 运行都会指向不存在的行。
7. **本轮无法验证的事实**：Udon `long[]`、Unity `JsonUtility` 对 `long` 的处理、`SerializedProperty.longValue`、重导入后的场景 diff 大小、EditMode 是否会因 `SerializedUdonPrograms` 重编译而出现签名 diff。这五项都需要 Editor，应由 Unity 操作者单独验证。

---

## 12. 引用索引（本文件用到的证据源）

- 代码：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/{Runtime,Editor,Tests,Labs,Worlds}/**`（行号见 §6）
- 数据：`Docs/Design/Levels/{_ref_cancer,_ref_mas1,story_01_bench_size,hub,physics_test_field}/*.json`
- 决定：`Docs/OPEN_DECISIONS.md:53-59,99`；`Docs/Design/Parity/PARITY_AUDIT.md:14-38,83-84,110-118,143-151,200-255`；`Docs/Design/Ledger/README.md:1-20,39-40`
- 参数：`Docs/Design/Parameters/README.md:35-42,218-245,568-572`
- 原作数据挖掘（本机，不入库）：`~/KatamariRef/meta/{raw_growth_mechanism.txt,DATAMODEL_NOTES.md,solid_volume/README.md,solid_volume/worst20.csv}`
- 平台限制：`Docs/Research/ReferenceRules/VRC_WORLD_SIZE_LIMITS.md:191,328`（S8 = creators.vrchat.com network-details：200 B 连续 / 280,496 B manual / ~11 KB/s）
