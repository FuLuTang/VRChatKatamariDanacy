# 定点体积步骤 C+D：换量子并恢复原作成长形式

**日期：2026-11-16（项目时区 Asia/Shanghai）。** Owner 已批准（[OPEN_DECISIONS.md (r)](../../OPEN_DECISIONS.md)）。本文是**先写后做**的设计说明：先定表示、键位、字段与验收口径，再照它改代码。吸收门已由按原作数字落地（[ABSORB_GATE_ORIGINAL.md](ABSORB_GATE_ORIGINAL.md)）；本文接手的是**成长侧**，并把成长与门统一到同一个体积定义（`k·a·b·c`）与同一套回收规则（`penalty` 默认 0.8）。

---

## 0. 一句话结论

体积的唯一真源是 `long` 毫米³（1 LSB = 1 mm³ = 1e-9 m³）。球：`V_ball` 累加、`D = (6V/π)^(1/3)`、显示与门都向下取整到整 mm（门侧已有）。物件：`V_item = k·a·b·c`（k 默认 0.70），**一件的成长 = `round(penalty · k · a·b·c · 1e9)` mm³**（penalty 默认 0.8）。这个成长值在**导入/同步时算一次**，写进宿主的 `long[] propGrowthMm3` 列；运行时不再定价、不再乘 0.8、不再有「整数 unit」。`c = 0.0041 m³/unit`、`baselineVolumeUnits`、`valueUnits` 三者退役。

---

## 1. 表示与公式（唯一实现）

```text
1 m³ = 1_000_000_000 mm³
V_item_mm3(gate)   = k · a·b·c                        # 浮点，仅用于门；Req_mm = floor(1000 · cbrt(60·V_item/π))
growth_mm3(item)   = round(penalty · k · a·b·c · 1e9) # 整数，写进宿主，直接作为账本 gain
V_ball_mm3         = startVolumeMm3 + Σ gain - banked（账本守恒，见 §4）
D_m(V)             = (6·V/π)^(1/3)（m）；D_mm = floor(1000·D + ε)
显示直径            = (6·V/π)^(1/3) + 0.0002 m（+0.02 cm，原作 G09；只用于 HUD/读数，不进门）
球径上限           = 全局 maxBallDiameterM（默认 30 m）→ V_cap = π/6·D_cap³
```

- **一个量子**：mm³。`1 旧 unit = 0.0041 m³ = 4,100,000 mm³` 只是历史换算，不再参与任何运行时计算（只在迁移对照里出现）。
- **k 与 penalty 的关系**：门用 `k·bbox`；成长用 `penalty·k·bbox`。**同一件在门与成长里是同一个 `k·bbox` 体积**，差别只是回收率 0.8（原作 O2/O6）。验收项：门与成长对同一件读出同一个 `V_item_mm3`（差一个 0.8 因子，且在 1 mm³ 内可复算）。
- **不再有「最小 1 unit」这回事**：最小一格是 1 mm³，是起始球体积的 `1/14,094,798`，单件量化误差不可见。

## 2. placement.json：键位的去留

| 键 | 去留 | 说明 |
|---|---|---|
| `defs[].valueUnits` / `props[].valueUnits` | **弃用（只读兼容）** | 旧文件仍然**合法输入**：读到就忽略它的数值（它可以继续和旧总量对账），**不再参与成长**。`canonical_doc` **不再写出**该键。理由：它是「按尺寸档定价再取整」的产物（[WHY_OUR_GROWTH_IS_BIG.md](WHY_OUR_GROWTH_IS_BIG.md) §2），留着它等于留着第二套成长货币。 |
| `defs[].volumeK` / `props[].volumeK` | **保留**（已存在） | 缺省 0.70，不写默认值。门的唯一系数。 |
| `defs[].volumePenalty` / `props[].volumePenalty` | **新增，可选** | 缺省 0.8，不写默认值；范围 `0 < p ≤ 1`。原作逐件 0.01–1.0 的值以后可以填进来，只用缺省。 |
| `props[].growthMm3` | **新增，派生，作者永不手填** | 与 `absorbDiameterReq` / `absorbable` 同一个模式：`canonical_doc` 每次从 `aabb`+`k`+`penalty` 重算并写出。写它是因为它是**账本货币**，要能被 diff 审阅；不写它，成长就只能靠运行时隐式推导，审不出来。 |
| `props[].absorbDiameterReq` / `absorbable` | 保留（已有） | 门侧派生列，不变。 |
| `level.expected.valueUnits` | **弃用（只读兼容）** | 旧关卡文件仍可读；校验器不再要求它，也不再拿它当硬门。 |
| `level.expected.growthMm3` | **新增，派生** | 该关全部道具 `growthMm3` 之和，`canonical_doc` 重算写出。校验器按它做硬门（有就必须相等）。 |

**字节稳定性**：现有文件作为**输入**全部仍然合法（`valueUnits`/`expected.valueUnits` 读得进、校验不报错）。但 `canonical_doc` 的输出变了（去掉 `valueUnits`、加 `growthMm3`、`expected` 换键），所以**受版本管理的 placement.json 必须用写出器整体重生成**；重生成是确定性的（排序、舍入、派生列都由同一函数给出），同一份输入永远得到同一份字节。`_ref_*` 是本地只读参考，不进 git，不重生成（它们的派生列从起就已经是旧门的，读时会被校验器告 stale，属已知状态）。

**total/expected 的后果**：`level.expected.valueUnits`（旧）被 `growthMm3`（新）取代；`propCount` 不变。**星级/目标不变**：关卡目标仍是**直径米**（`Docs/Design/GameModes/Levels/*.json` 的 target 是 m），不换算成 mm³。内容量对目标的兑换率变了，所以第一关 130 件全吃光只到 **1.398 m**（§7），不到原来的 2.20 m 上限——这是原作的成长速度，不是数据错误；星级带要等 T39 试玩重标（(p) 现阶段不调数值）。

## 3. 宿主 / 导入 / 同步的列名与责任

- 宿主（`PickupFieldHost`）列名：**`propGrowthMm3`（`long[]`）**，替换 `valueUnits`。语义：这一件被吸收时账本应记的 gain，单位 mm³，**已经乘过 k 与 penalty**。
- 同步/导入**算一次**：`growth = round(penalty(ID) · k(ID) · bounds.x·bounds.y·bounds.z · 1e9)`，其中 `bounds` 是同步已经写进 `propBounds` 的那个体积（含 `@size=` 声明立方体）；`k`/`penalty` 来自 placement.json（缺省 0.70/0.8），逐件覆盖优先。运行时**不再**乘任何系数，也不再做「整数单位」换算。
- **penalty 不单独立宿主列**：它只在导入/同步定价时用一次；运行时要的是已经算好的 `propGrowthMm3`。这与 `k` 不同——`k` 运行时还要用于门，所以留在 `propVolumeK`。
- **旧场景**（没有 `propGrowthMm3` 列）：宿主按 `propVolumeK`（缺列→0.70）与 `propBounds` 在 `Start` 里**自算一次**成长，规则与同步同一公式，保证未重导入的实验室场景不是 0 收益。老 `valueUnits` 列读到就忽略。
- 回读校验：同步写完后按 itemId 比对 `propGrowthMm3`，与现有 `propVolumeK` 回读同一个机制；不符即 run 失败。
- **导入时控制台应有的两行**（导入器路径也调用 `PickupFieldSyncReport.Log()`，2026-11-16 O8 修：此前只有菜单路径会打）：
  ```text
  [PickupFieldSync] <host>: 130 prop(s), N kind(s), 1417085102 growth mm^3, 0 rejected
    growthMm3: written=1417085102, fromFile=130, fromBounds=0, read-back: verified
  [level] +0 ~0 -0 props=130 other=0 errors=0
    growthMm3: declared=1417085102, fromFile=130, fromBounds=0, read-back: verified
    pickup sync wrote 130 row(s) into 8 base columns (+ the optional proxy columns), including propGrowthMm3 ... and propVolumeK ...
  ```
  `written` 与 `declared` 都必须等于 `placement.json` 的 `level.expected.growthMm3`；`fromBounds` 非 0 表示有行缺 `growthMm3`（旧文件）走了实测 bounds 兜底，`read-back: NOT verified` 表示宿主没写进去。

## 4. 账本 / 尺寸模型 / 交分

- **账本列不变**（`GrowthLedgerMath` 已是 `long`）。变的只是量的含义：`unbankedVolume`/`bankedScore`/`commitGains` 现在是 mm³。
- 电机镜像：`unbankedVolumeUnits` → **`unbankedVolumeMm3`（long）**；`carriedVolumeUnits`（float）→ **`carriedVolumeMm3`（long）**；`confirmedVolumeUnits`（float）删除，改成 `confirmedVolumeMm3`（long，= `unbanked + carried`）与一个派生 `float` 读数 `GetConfirmedVolumeCubicMetres()`（仅显示）。**账本加法只做整数加法**，不允许 float 累加。
- `SizeModelMath`：
  - `TryDerive(long volumeMm3, float maxBallDiameterM, float collisionRatio, float semanticPerCore, out coreRadius, out collisionRadius, out semanticDiameter, out long effectiveVolumeMm3)`。直径用 `Mathf.Pow`（float）算 `(6V/π)^(1/3)`，先 clamp 到 `V_cap`（`V` 超过上限时直接给 `D_cap`，不靠浮点比较）。
  - `TryVolumeMm3ForSemanticDiameter(float d, float maxBallDiameterM, float semanticPerCore, out long volumeMm3)`：`V = round(π/6·(d/semanticPerCore)³·1e9)`，用于起始球与交分后体积。
  - **float32 风险（写进注释）**：`(float)long` 在 `V > 2^24 mm³`（0.0168 m³）时丢低位。最坏相对误差 `1/2^24 ≈ 6e-8`，对直径是 `2e-8` 相对量（30 m 球上 ~0.6 µm）；可接受，但①**禁止用 float 累加体积**（只允许瞬时换算），②门/显示在**整 mm** 上比较，毫米以下的浮点噪声不进门，③cbrt 不要用「先开方再平方」之类会放大误差的写法。`double` 在 Udon 不是一等类型，不用。
- `BankShrinkMath`：`TryBankVolumeMm3(postBankDiameterM, maxBallDiameterM, semanticPerCore, out long)` = `TryVolumeMm3ForSemanticDiameter` 的薄委托；`TryCarriedVolumeMm3(long post, long stillUnbanked, out long carried)`：`carried = max(0, post − stillUnbanked)`，整数减法。交分规则（(a) 比例缩小、(b) 门槛）不变，只是量换成 mm³。
- 交分/账本的**幂等/commit 规则不变**：同一 `itemId+generation` 一次 claim，一次 commit；bank 请求 ID 去重。

## 5. 每关起始尺寸与全局球径上限

- **起始**：`startVolumeMm3`（long，序列化，缺省 `14,100,000` ≈ 0.30 m 球）是真正的初值。关卡仍用 `startDiameterWorldM`（人类可读、导入报告会打印）作为每关输入：`> 0` 时 `Start` 用它重算 `startVolumeMm3 = round(π/6·D0³·1e9)`（`0.2997 m → 14,094,798 mm³`），`0` 时用序列化的 `startVolumeMm3`。两者只有这一个换算点。
- **上限**：删掉每关的 `maxCoreRadiusWorldM`（1.1 m，耦合 2.20 m），改成一个**全局** `maxBallDiameterM`（序列化在电机上，缺省 **30 m**）。原作上限 30 cm 是「球」，我们这一轮先按 30 m 的工程上限走（`V_cap = π/6·30³ = 14,137.17 m³ = 1.4137e13 mm³`）；某关要更小就调这一个数，不再有第二个半径门。`maxCoreRadiusWorldM`、`collisionToCoreRadiusRatio`、`semanticDiameterPerCoreDiameter` 三者保留比值语义：核心半径 = `0.5·D / semanticPerCore`，碰撞半径 = 核心 × `collisionRatio`。
- **连带修 8 m / 512**：
  - `[FollowRigMath]` 的 `eyeUpMaxMeters` / `eyeMaxBackM` = 8 m 是为 2.2 m 球设的：`0.55+2.4·15 = 36.55 m`、`0.75+2.2·15 = 33.75 m`，8 m 会把 30 m 球的眼睛钉在球面上。两者缺省提到 **64 m**（仍是「防呆上限」，不是玩法上限），并在注释里写明推导。
  - 吸收门的 `MaxItemVolumeSanityWorldM3`（现 1000 m³）按全局上限重算：`V_cap/10 = 1,413.7 m³` 是 30 m 球能吸的最大件，1000 m³ 已经**小于**它。改为 **`π/6·30³/10` 的 10 倍 ≈ 14,137 m³**（与 `V_cap/10` 同量级，留 10× 余量），注释写清是护栏不是门。
  - 场景里残留的 `maxItemVolumeWorldM3: 512` / `absorbVolumeRatio: 10` 是死键（已删字段），不动场景；列出给操作者顺手清。
  - 其它「按 2.2 m 球写死」的地方（`maxProxyProtrusionWorldM = 0.3`、`maxStepUpLiftWorldM = 0.6`、`minConversionDiameterWorldM = 0.55`、`bankResidualFraction`）本文**不改语义**，只在注释/报告里标为「30 m 球下需要重标」——它们不在 C+D 的验收里，改了会掩盖本文的成长曲线。

## 6. 迁移与验收

1. **数据**：第一关 130 件按 `k=0.70`、`penalty=0.8` 从 `aabb` 重算成长（§7）。不手填成长。
2. **C# 黄金值**：`1356.4 units`（0.30→2.20 m 的旧标定）退役；换成 `V(0.30) = 14,137,167 mm³`、`V(2.20) = 5,573,697,774 mm³`、`V(30 m) = 1.4137e13 mm³`。所有「units at cap」类断言改成 mm³ 版本并写出算式。
3. **直径一致（C 的验收）**：同一 `V` 在旧表示（`(3.4390244+U)·0.0041`）与新表示（`V_mm3`）下算出的直径差 < 1e-6 m。用现行关卡的 `V` 抽查（不是「看起来差不多」）。
4. **成长曲线（D 的验收）**：门与成长对同一件同源（`k·bbox`），差 0.8 因子；第一关曲线见 §7，并与 owner 的 HUD 参考对比。
5. **Unity 未验证项**（照旧全部标注）：`long[]` 列、`float[] propVolumeK` 列、导入/同步回读、重导入场景 diff、Udon 里 `long` 与 `Mathf.Pow` 的组合。

## 7. 第一关（`story_01_bench_size`，130 件）的成长曲线

用仓库内数据实算（`placement.json` 的 `aabb`；关卡没有 `volumeK`/`volumePenalty` 声明，所以全部 0.70 / 0.8）：

- 起始：`ballDiameterStart = 0.2997 m` → `V0 = π/6·0.2997³ = 0.0140948 m³ = 14,094,798 mm³`。
- 单件成长：`Σ growth = 1,417,085,102 mm³ = 1.4171 m³`（`Σ a·b·c = 2.5305 m³`，×0.8×0.70 = 1.4171 ✓）。
- **全吃光：`V = 1.4312 m³` → `D = 1.39819 m`**（旧表示同内容 1522 units ×0.0041 = 6.24 m³ → 2.28 m，被 2.20 截；新曲线只到 1.40 m，**这就是原作成长速度**）。
- 逐件直径增量（按吸收时的球径分桶，单位 %）：

| 桶（吸收时 D） | 件数 | 中位 % | 最小 % | 最大 % |
|---|---:|---:|---:|---:|
| 0–0.5 m | 39 | 0.448 | 0.0047 | 19.68 |
| 0.5–0.75 m | 68 | 0.054 | 0.0003 | 19.90 |
| 0.75–1.0 m | 5 | 4.734 | 0.287 | 7.515 |
| 1.0–1.5 m | 18 | 0.208 | 0.045 | 12.21 |

（单桶最大 19.7% 来自最后那几件「大的结构件/大件」，它们本来就被设计成一口跳；中位数才是「手感」）。
- 与 owner 的 HUD 参考 **1.4–1.5%/件（早期）** 对比：**按直径算我们低 3 倍**（第一桶中位 0.45%）——原因是我们的起始球是原作的 6 倍（0.30 m vs 5 cm，体积 216 倍），而我们的早期道具只有 2.5–7.5 cm。若那个参考是**体积百分比**，则第一桶中位 0.448% × ≈3.1 ≈ 1.4%（体积 % 与直径 % 的比值在 1.5–20% 区间约为 3），与 owner 的数吻合。**结论：曲线本身是原作的形状；是否要「早期每件 1.4–1.5%」取决于 owner 的参考是体积还是直径，以及要不要把起始球/早期内容改成原作尺度——那是内容与关卡参数（(p) 现阶段不调数值），不是本文成长公式的事。** 这一条写进报告，请 owner 确认。

## 8. 不在本文范围（明确写出，避免被当成漏做）

- 原作**按球径分段**的 penalty 曲线（5 cm .8 → 15 cm .4 → 20 cm .16）：只落默认 0.8 + 逐件覆盖；分段函数需要「这一关的球径→penalty」表，owner 未给，属于后续。
- HUD **+0.02 cm** 的最终显示格式（本文给一个 `GetHudDiameterWorldM()` 读数，是否上屏由表现层决定）。
- 群组连带子件（(s) v2）、30 m 球下的代理/台阶/转换门槛重标（§5 末）。
- `_ref_cancer` / `_ref_mas1` 的重生成（本地只读参考，不进 git）。
