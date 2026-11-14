# 玩法常量的唯一真源与烘焙流水线（Parameters）

**状态：`PROVISIONAL_NOT_TESTED`。** 本文只定义**规则与流水线**：一个玩法常量允许写在哪、怎么进运行时、怎么被机器抓住漂移、以及从今天的状态怎么一步步迁过去。它不含任何新数值，不改任何现有文件的行为，也不是对原作行为的声明。世界尺度与公共接口仍未冻结（T06），所以这里定义的一切都必须能在 T06 之后重算，而不是被当成接口冻结。

> **一句话**：同一个常量只允许有一处**可编辑的真源**；运行时不读 JSON、不解析文件，只读**烘焙进序列化字段**的值；真源与序列化字段之间的差由一个**便宜且会大喊的测试**抓住，而不是靠人复读文档。

---

## 0. 为什么需要这一层

### 0.1 同一个数现在写在三个（其实是五个）地方

以 `bank_residual_fraction = 0.25` 为例——审阅时点名的那一个：

| # | 位置 | 形态 |
|---|---|---|
| 1 | [`layout.json`](../Maps/PicnicLawn/layout.json) `bank.rules.bank_residual_fraction`（L1636） | 作者数据 |
| 2 | [`KatamariBallMotor.cs`](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs) L116 `bankResidualFraction = 0.25f` | 序列化字段默认值 |
| 3 | [`round_model.py`](../Gameplay/round_model.py) L128 `bank_residual_fraction: float = 0.25` | 离线模型默认值 |
| 4 | [`BallMotor/README.md`](../BallMotor/README.md) L181 的“临时数值”表 | 文档复述 |
| 5 | [`Gameplay/README.md`](../Gameplay/README.md) §2.4 / §7 的正文与参数表 | 文档复述 |

改一处，另外四处**不会报错**，只会安静地变得不对。这不是假设：§0.2 有两个已经发生的实例。

### 0.2 漂移已经发生了两次，而且第二次是结构性的

**实例一（文档哈希已过期，机器钉住的没过期）。** 两份文档手工钉了地图版本：

- [`Gameplay/README.md`](../Gameplay/README.md) §0.2 / §4 与 [`GameModes/README.md`](../GameModes/README.md) §0.2 都写着 `layout.json` = `8218881d…`、`prop_catalog.csv` = `901d0141…`；
- 磁盘上现在是 `layout.json` = `5b08daf9…`、`prop_catalog.csv` = `01d01418…`；
- 而关卡文件里的 `map_snapshot.layout_sha256` 是新的，[`validate_levels.py`](../GameModes/validate_levels.py) 第 435-446 行会比对它，所以**门禁是绿的**。

结论：手工钉的哈希会烂，机器验的哈希不会。文档里的复述值必须改成“引用真源”，而不是“再抄一遍”。

**实例二（运行时与模型对“长到 2.20 m 要多少单位”差 3.44 倍）。** 两边都声明自己把 `0.30 m → 2.20 m` 标定好了，但标定方式不同：

| 侧 | 公式 | 每确认单位的体积 | 到 2.20 m 需要的确认单位 |
|---|---|---|---|
| 离线模型 | `V = V(0.30) + units × ((V(2.20) − V(0.30)) / 1356)`（[`round_model.py`](../Gameplay/round_model.py) L444-458，`units_to_max_diameter` 见 L117） | 0.0041011 m³ | **1356** |
| 运行时 | `V = baseline + units × cubicMetersPerVolumeUnit`，`baselineVolumeUnits = 1`、`cubicMetersPerVolumeUnit = 0.0141`（motor L95-96）、显示上限 `maxCoreRadiusWorldM = 1.1`（L97） | 0.0141 m³ | **≈394.4** |

于是：

- 模型说“把整张图（1284 单位）吃光只到 2.16 m，2.20 m 结构性不可达”（[Gameplay README](../Gameplay/README.md) §4.3 / §6）——这句话只对**模型的标定**成立；
- 运行时在 **394 确认单位**（全图 1284 单位的 30.7%）就已经顶到 2.20 m 显示上限。对当前实现来说，“2.20 m 不可达”是**假的**。

**这条比 0.25 严重得多**：它不是某个比例偏了一点点，而是两套标定各自成立、谁也没读对方。任何用模型数字做的调参决定（Gameplay README §5 的敏感性、§6 的结论）都不能直接搬到游戏里。它也是本文 §8 风险 3 那条“迁移不是纯重构”的来源。

### 0.3 约束：运行时读不到 JSON

- UdonSharp 运行时**没有**磁盘 JSON、没有 `JsonUtility`（那是编辑器/构建期类）、没有 `List`/`Dictionary`/`LINQ`/`try`；[`CODE_CONVENTIONS.md`](../CODE_CONVENTIONS.md) 第 25-31 行把可用子集写死了，[`AGENTS.md`](../../../AGENTS.md) 也写着“No editor APIs or disk JSON in Udon runtime”。
- [`02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md) 第 76 行已经给了方向：**“编辑器可使用 JSON/ScriptableObject，构建时烘焙为 Udon 可用数组/引用”**。
- 所以可选形态只有两种：把数**烘焙成序列化字段**（组件/prefab），或**生成 C# 常量**。两者的失效方式完全不同，见 §2。

### 0.4 本层在球层分工里的位置

[`BallMotor/README.md`](../BallMotor/README.md) 规定了“装配层只搬运、不算数学”。本文不改变那条规矩，只回答一个它没回答的问题：**那些搬运用的标量从哪来**。答案必须是“从一份真源烘焙下来”，而不是“从 Inspector 上随手填的默认值”。

---

## 1. 规则：一个参数一个真源

### 1.1 什么算“真源”

一个位置可以被称为某参数的**真源**，当且仅当同时满足：

1. **可编辑**：人能直接改（JSON / 数据文件），不需要改代码、不需要重建二进制；
2. **被机器读**：至少有一个自动化消费方（烘焙器、校验器、离线模型）直接读它，而不是人眼抄；
3. **有归属**：登记表里写了它的证据等级和唯一 owner。

只满足 1 和 3 的叫**文档复述**（§0.1 的第 4、5 行），必须在正文里标成“见 `<真源>.<路径>`”，不得自带数字。

### 1.2 五个域，各管一段，互不重叠

| 域 | 文件 | 管什么 | 不管什么 |
|---|---|---|---|
| **地图** | `Docs/Design/Maps/<map_id>/layout.json` | 几何、球径包线 `scale.ball_*`、档位门槛 `item_tier_pickup_diameter_m`、`size_gates`、交分锚点与轮换 `bank.anchors` / `bank.rotation`、`bank.rules.*`（残差/最小兑换门槛——它们已经是该文件里的既有字段，见 §5 步骤 1 的取舍） | 对局时长、PvP 比例、道具时长、运动/跟随调参 |
| **规则集** | `Docs/config/rulesets/<ruleset_id>.json`（新，见 §3.1） | 按 `ruleset_id` 共享的规则：交分周期、对局时长、PvP 吞噬/重生、道具时长与倍率、吸收比、成长标定、运动/跟随调参、形状常量 | 任何地图几何；任何每关开关 |
| **关卡** | `Docs/Design/GameModes/Levels/*.json` | **开关与限时**：`time_limit_s`、`quest.objective`、`spawn_id`、`bank.enabled`、`powerups.enabled` | 规则数值——[`GameModes/README.md`](../GameModes/README.md) §4.3 字段表（L444）已经写明 `ruleset_id` 是“引用共享规则集，**关卡不复制这些数字**” |
| **运行时派生** | 代码里算出来的量 | 起始球径（由体积基线算出 = **0.29974 m**，见 [`BallMotor/README.md`](../BallMotor/README.md) L130）、三尺寸、`growthFraction`、交分后直径、`carried`、确认体积 | 任何作者可调的数 |
| **运行时独有** | 代码里的 `const` / 缓冲容量 / 容差 | 结构容量、ε 容差、`fixedStepSeconds`、`stuckTimeoutSeconds` 一类**没有作者数据来源**的实现值 | 设计旋钮 |

规则：**每个参数只属于一个域。** 跨域引用必须走“读那个域的文件”，不得复制值。

### 1.3 证据等级：复用项目既有分级，且只挂在真源上

沿用 [`Research/KatamariBallRules/README.md`](../../Research/KatamariBallRules/README.md) §2 的 C 编号与 [`GameModes/README.md`](../GameModes/README.md) §0.1 的标记约定，本文用六个记号：

| 记号 | 含义 | 现在能引用的地方 |
|---|---|---|
| `[原作确认 Cn]` | Tier 1/2 公开来源直说，已有本仓库核查 | Research README §2 的 C1–C22 |
| `[原作强暗示 Cn]` | 多条来源一致但未直说 | 同上 |
| `[项目自选]` | 我们自己的选择（含用户 2026-11-14 拍板的 (a)–(d)） | [`OPEN_DECISIONS.md`](../../OPEN_DECISIONS.md) §“已由用户决定” |
| `[PROVISIONAL]` | 数值是暂定占位，等某个 T 任务的证据 | `OPEN_DECISIONS.md` 末尾“不因本摘要改变的门禁” |
| `[模型假设]` | 只存在于离线模型，**永不上线** | [`Gameplay/README.md`](../Gameplay/README.md) §7 里标 “(model)” 的行 |
| `[实现常量]` | 运行时结构/容差，不是设计值 | `CODE_CONVENTIONS.md` + 各模块 `const` |

**硬规矩两条：**

1. 等级的**唯一携带者是真源那一行**（JSON 里的 `evidence` 字段）。文档复述不带等级，只引用；
2. `[原作确认]`/`[原作强暗示]` 只能来自 Research README 的 C 编号。**没有 Cn 编号的一律降为 `[项目自选]` 或 `[PROVISIONAL]`。** 这一条防的是“文档写着原作确认、JSON 写着 PROVISIONAL”这种自相矛盾——它今天已经存在于 `bank_cycle_s = 60`（`OPEN_DECISIONS` 标“强暗示 C3”，而 `layout.json` L1627 标 `PROVISIONAL_NOT_TESTED`，两者都对，但没人说清哪个是等级）。

### 1.4 参数登记表：一个参数一行

真源文件里的每条规则必须自带这五样，缺一不可：

```json
"bank": {
  "residual_fraction": {
    "value": 0.25,
    "unit": "ratio",
    "evidence": "PROVISIONAL",
    "decided_by": "OPEN_DECISIONS (a), user 2026-11-14",
    "runtime_field": "bankResidualFraction",
    "consumer": "KatamariBallMotor.TryBank"
  }
}
```

- `runtime_field` 是**约定名**：烘焙器按它写入序列化字段，EditMode 测试按它逐字段比对（§4.2）。没有 `runtime_field` 的参数就永远进不了运行时——这是“加了个数但没人读”的自动拒绝；
- `consumer` 是**谁读它**。写不出 consumer 的参数说明它是 `[模型假设]`，那就不要进规则集（§6.5）。

---

## 2. 候选方案

四个候选，前两个是真选项，后两个是被约束排除的：

### A. 生成 C# 常量（codegen）

`Tools/rulesbuild/build_rules.py` 读真源 → 写 `Runtime/Rules/KatamariRulesBaked.cs`，里面是 `public const float BankResidualFraction = 0.25f;`，运行时直接读常量。

- **好处**：没有编辑器步骤；没有“字段被手改”这条路径；`const` 在 U# 里合法（`CODE_CONVENTIONS.md` L26 明确允许 `const`）。
- **坏处（都是结构性的）**：
  1. **每关覆写无法表达**。`const` 是编译期的，一个场景覆盖不了。剧情关要 `bank.enabled=false`、对战关要 180 s，只能靠第二套机制，于是又回到多源；
  2. **改一个数的代价是“重编译 U#”**：Windows 上 `refresh_unity` + U# 全量编译 + `Assets/SerializedUdonPrograms/*.asset` 重写（仓库规则里这些是 tracked，签名噪声要成批提交）。设计者改一个 0.25 → 0.30，要等几分钟并且产生一堆与语义无关的 diff；
  3. **生成物可以被手改**，改完与真源分叉，只有 `--check` 能发现——而 `--check` 是 C# 侧根本不跑的东西；
  4. 布尔/字符串枚举（`bonus_categories`、`powerup` 名单）在 `const` 里很难表达。

### B. 烘焙载荷 → 序列化字段（**选定**）

```
真源 JSON ──(Python bake)──> 载荷 katamari_rules.json ──(Editor 烘焙器)──> KatamariRules.prefab 的序列化字段 ──> 运行时读字段
```

- **好处**：
  1. **改数值不重编译**：序列化字段是 prefab/场景数据，改值只改 YAML。U# 只在**字段增删**时重编译（那时 `SerializedUdonPrograms` 的变化本来就有意义）；
  2. **每关覆写天然可表达**：同一个 prefab 多个实例，或场景里直接改字段（§6.6）；
  3. **Inspector 能看到**：设计者能看见“我正在动哪个参数”，而不是 `0.25f` 散在代码里；
  4. **和仓库里已经跑通的流水线同构**：[`Tools/mapbuild/README.md`](../../../Tools/mapbuild/README.md) 的 `layout.json → unity_greybox.json → Unity Builder` 已经是“作者数据 → 生成物 → 消费方 + `--check` + 回归测试”的成熟形状。本文不发明新机制，只把同一形状用到规则上。
- **坏处**：prefab 是**可手改**的，所以真源和字段之间会分叉 → 必须配 §4.2 的逐字段等价测试。这正是它的失效模式，而且是可测的。

### C. 手写一个组件当唯一真源，反向生成 JSON

把手写 `KatamariRules` 组件当权威，Python 从 prefab YAML 里读。**否决**：prefab/scene YAML 有 GUID、fileID、嵌套引用，Python 解析它比解析 JSON 贵得多且脆；而且地图域的数（球径包线、档位门槛）已经在 `layout.json` 里被其它工具读，权威一旦翻到 Unity 侧，`validate_map.py` / `validate_levels.py` / `round_model.py` 全都要改成读 YAML。**把权威从纯文本挪到二进制化资产，是纯负收益。**

### D. 运行时读 JSON / 自己写个 U# 解析器

**按约束直接排除**，不是偏好问题：U# 没有 `JsonUtility`（编辑器类）、没有 `Dictionary`、没有 `try`，手写解析器要在每帧路径上分配字符串；`AGENTS.md` 与 `02 §4` 都禁止运行时读磁盘 JSON。列在这里是为了防止下一个人重新提出它。

### 对比

| 维度 | A 生成常量 | **B 载荷→字段（选）** | C 组件当真源 | D 运行时读 JSON |
|---|---|---|---|---|
| 改一个数要重编译 U# | 是 | **否**（只改字段） | 否 | 否 |
| 每关覆写 | ❌ 无法表达 | ✅ | ✅ | ✅ |
| 手改导致分叉 | 改 .cs | 改 prefab | 不适用（它是真源） | — |
| 分叉能否被便宜地测出 | 只能靠 Python `--check` | **Python `--check` + EditMode 逐字段** | 需要解析 YAML | — |
| 离线模型读同一份数 | ✅ | ✅（同读 JSON） | ❌ 要读 YAML | ✅ |
| U# 语言子集 | ✅ | ✅（serialized 字段） | ✅ | ❌ |
| 与现有 mapbuild 流水线同构 | 部分 | **完全** | 否 | 否 |

**选 B。** 理由收敛成一句：**它是唯一一个“改数值不重编译、能表达每关覆写、并且分叉可以被一个不依赖 Unity 的测试抓住”的形态**；代价（prefab 可手改）恰好是 §4.2 能测的东西，而不是隐藏风险。

---

## 3. 推荐流水线

### 3.1 文件与目录

```
真源（人写）
  Docs/Design/Maps/PicnicLawn/layout.json          ← 地图域（已有）
  Docs/config/rulesets/prototype_defaults_v0_3.json ← 规则域（新；ruleset_id 即文件名）

烘焙（Python，离线，仅标准库）
  Tools/rulesbuild/build_rules.py                  ← 新工具，与 Tools/mapbuild 并列
  Tools/rulesbuild/tests/test_build_rules.py       ← 生成器自身的回归测试

产物（生成物，提交进仓库，禁止手改）
  Assets/VRC_Katamari/Rules/katamari_rules.json    ← 载荷（Unity 当 TextAsset；.meta 由首次导入生成后提交一次）

应用（Unity 编辑器，一次性动作）
  Assets/VRC_Katamari/Editor/Rules/KatamariRulesPayload.cs   ← [Serializable] DTO（Editor 程序集，仿 GreyboxPayload）
  Assets/VRC_Katamari/Editor/Rules/KatamariRulesBaker.cs     ← 菜单项：读载荷 → 写 prefab 字段

运行时（U#）
  Assets/VRC_Katamari/Runtime/Rules/KatamariRules.cs         ← UdonSharpBehaviour，一个参数一个 [SerializeField]
  Assets/VRC_Katamari/Rules/KatamariRules.prefab             ← 场景/球 prefab 引用它
```

**为什么载荷放在 `Assets/` 而不是 `Docs/`**：`Docs/` 里的文件 Unity 不当资产，消费方只能 `File.ReadAllText` + 猜仓根（[`PicnicLawnGreyboxBuilder.cs`](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs) L1782-1804 就是这么干的）。烘焙器和 EditMode 测试都只要 `AssetDatabase.LoadAssetAtPath<TextAsset>` 就能拿到同一份字节，不需要任何路径算术，换检出目录也不会坏。**生成物进 `Assets/` 不是新事**：`Assets/SerializedUdonPrograms/*.asset` 一直是生成且提交的。

**为什么 DTO 放 Editor 程序集**：`GreyboxPayload.cs` 就是 Editor 程序集里的 `[Serializable]` 镜像，运行时看不见它。运行时只认 `KatamariRules` 的序列化字段——**载荷的形状永远不会泄漏进 U#**。

### 3.2 载荷形状（示意，字段名待实现时定稿）

```json
{
  "schema_version": 1,
  "generated_by": "Tools/rulesbuild/build_rules.py",
  "map_id": "picnic_lawn",
  "ruleset_id": "prototype_defaults_v0_3",
  "source_sha256": {
    "layout": "5b08daf9…",
    "ruleset": "…"
  },
  "baked": {
    "bank_residual_fraction": 0.25,
    "bank_min_conversion_diameter_m": 0.55,
    "ball_max_collision_diameter_m": 2.2,
    "baseline_volume_units": 1.0,
    "cubic_meters_per_volume_unit": 0.0141,
    "max_core_radius_world_m": 1.1,
    "max_speed_mps": 4.0,
    "…": "每个 runtime_field 一项"
  },
  "derived": {
    "start_diameter_m": 0.29974,
    "units_to_max_diameter_effective": 394.41
  },
  "known_divergences": [
    {
      "id": "size_model_calibration",
      "model_value": 1356,
      "runtime_value": 394.41,
      "owner": "T21 / T39",
      "note": "见 README §0.2 实例二"
    }
  ]
}
```

- `baked` 只放运行时真的读的字段；
- `derived` 是算出来的，**不手填**（`start_diameter_m` 必须与 `SizeModelMath` 的输出一致，否则 [`BallMotor/README.md`](../BallMotor/README.md) L130 那条“start 必须和体积映射同源”就破了）；
- `known_divergences` 是**显式登记的分歧**，不是错误。它必须存在，因为把一个已知分歧藏起来比登记它危险得多（§6.5 与 §10）。

### 3.3 谁在什么时候跑什么

| 谁 | 命令 | 何时 |
|---|---|---|
| 改规则的人 | `python3 Tools/rulesbuild/build_rules.py` | 改完 `rulesets/*.json` 或 `layout.json` 的球径包线 |
| 离线模型 | `round_model.RoundParams.from_ruleset()`（§3.4） | 每次跑 `simulate.py`；直接读 JSON，不读载荷 |
| 关卡校验 | `python3 Docs/Design/GameModes/validate_levels.py` | 关卡或地图变更（已有） |
| 门禁 | `python3 Tools/rulesbuild/build_rules.py --check` | 与 `validate_plan.py` + `unittest discover -s Docs/tests` 一起，见 `AGENTS.md` |
| Unity 操作者 | 菜单 `VRC_Katamari/Bake Rules Into Prefab` | 只在载荷变化后；跑完提交 prefab 的 diff |
| 运行时 | 不跑任何东西 | 只读 `KatamariRules` 的字段 |

### 3.4 Python 模型读同一份数

`round_model.py` **已经**直接读 `layout.json`（L386-404 `load_map()`，而且 L198-204 明确说“没有内置快照”）。需要新增的只有规则域：

```python
RoundParams.from_ruleset(ruleset_id=None)   # 读 Docs/config/rulesets/<id>.json + layout.json 的 scale/bank
```

- 现状的 dataclass 默认值（L110-157）**保留**，但降级为“测试/场景用”；
- `from_ruleset()` 是唯一“与运行时同源”的构造路径；
- 测试断言 `from_ruleset()` 与 `RoundParams()` 逐字段相等（除 `profiles`、`dt_s`），这样默认值不会偷偷变成第二真源。

---

## 4. 漂移守卫

三层，每层只做它能做的那一段，**每层都必须能在不看人眼的情况下失败并指名参数**。失败信息统一格式：

```
<参数名>: 真源 <file>:<path> = <值>，<消费方> = <值>。修：改真源后跑 <命令> / 跑 baker。
```

### 4.1 第 1 层：真源 → 载荷（Python，`Docs/tests/`）

新增 `Docs/tests/test_rules.py`，跟着 AGENTS.md 那条 `python3 -m unittest discover -s Docs/tests` 一起跑。四个断言（外加第 5 条源码扫描，共五条）：

1. **`test_payload_is_current`** —— 调 `build_rules.check()`，返回空才算过。等价于 `--check` 退出码 0，但让它在 unittest 里也红一次（CI/门禁只要跑一条命令）。
2. **`test_every_payload_field_has_exactly_one_source`** —— 模块顶部的 `FIELD_TABLE`（`载荷键 → (域, JSON 路径)`）与载荷的 `baked` 键集合**双向相等**：
   - 载荷有、表里没有 → “生成器写了一个没人认领的字段”；
   - 表里有、载荷没有 → “登记了但没烘进去”。
3. **`test_no_source_value_is_unbaked`** —— 遍历 `rulesets/*.json` 的**所有数值叶子**，凡是不在 `FIELD_TABLE` 也不在 `MODEL_ONLY` / `STRUCTURAL` 白名单里的，失败。**这条是真正的漂移守卫**：任何人往规则集里加一个数，必须当场决定它属于“烘焙 / 仅模型 / 结构常量”，不能含糊地放着。
4. **`test_payload_pins_live_hashes`** —— `source_sha256` 必须等于磁盘上 `layout.json` 与规则集文件的实际 sha256。地图一改，这里立刻红（和 `validate_levels.py` 的 `map_snapshot` 同一招，见 [`validate_levels.py`](../GameModes/validate_levels.py) L435-446）。

再加一个**不需要 Unity 就能抓住 C# 常量**的断言（同文件）：

5. **`test_structural_constants_match_runtime_source`** —— 用正则从源码里读出 `RadialProxyMath.cs:57` 的 `DirectionCount = 32` 与 `:60` 的 `MaxExtremeCandidates = 4`，与规则集 `shape.directionSamples` / `shape.maximumExtraExtremePoints` 比。这条专门钉住今天真实存在的一处“数据 ↔ 运行时”重复（§7 表 D 组），成本是两次 `re.search`。

### 4.2 第 2 层：载荷 → prefab 字段（C#，`Assets/VRC_Katamari/Tests/EditMode/`）

新增 `Tests/EditMode/Rules/KatamariRulesTests.cs`。它**不读 `Docs/`**、不做路径算术，只用 `AssetDatabase`：

```csharp
// 1) 字段集合相同：载荷有而组件没有 = 新参数没接线；组件有而载荷没有 = 手写字段
[Test] public void PayloadFieldsAndComponentFieldsAreTheSameSet()

// 2) 逐字段值相等：反射 [SerializeField] 字段，按字段名分组
[Test] public void EveryPayloadValueEqualsThePrefabField()
//    Assert.AreEqual(expected, actual, fieldName);   // 失败消息里带参数名

// 3) 反退化：迁移后 motor 不得再声明这些调参字段
[Test] public void MotorDeclaresNoBakedTuningField()
```

- 测试 1 是**防静默丢弃**的机制：往载荷加一个参数而不加组件字段，立刻红；
- 测试 3 是**防重复回来**的机制：`bankResidualFraction` 之类重新出现在 motor 上就红。没有它，§7 的 67 份副本会在半年内重新长出来。
- 跑法：MCP `run_tests`（程序集 `VRCKatamari.Tests.EditMode`）。

### 4.3 第 3 层：版本钉（跨文件，可选但推荐）

- 载荷里 `source_sha256` 已覆盖“真源 → 载荷”；
- 关卡侧建议在 `map_snapshot` 旁加 `ruleset_sha256`，让“这一关是按哪版规则设计的”也可验。**这需要改 `Docs/Design/GameModes/` 的 schema 与 `validate_levels.py`，属于 GameModes owner**，本文只登记需求，不代做。今天 `ruleset_id` 只过一条正则（[`validate_levels.py`](../GameModes/validate_levels.py) L357-358），**不解析到任何文件**——这是现存缺口。

### 4.4 为什么不用“跑一遍看数字对不对”

因为“数字对不对”需要一个基准，而基准正是我们要消灭的第二真源。三层守卫全部比对**同一份真源**或**同一份载荷**，没有“预期值”需要人写第二遍。这是它们便宜的原因。

---

## 5. 从今天的状态迁过去

每一步都写清：**改哪些文件**、**这一步之后什么仍然成立**、**怎么验证**。步骤 1–4 不改变任何游戏行为；**只有步骤 5 有行为风险**。

### 步骤 0 —— 本文（当前）

只新增本文件。代码、数据、Unity 资产零改动。

### 步骤 1 —— 建规则集镜像（零消费方）

- 新增 `Docs/config/rulesets/prototype_defaults_v0_3.json`：把下列**现有值逐字搬入**（不改值，只搬家 + 补 `evidence` / `unit` / `runtime_field` / `consumer` 元数据）：
  - [`prototype_defaults.json`](../../config/prototype_defaults.json) 的 `match.durationSeconds`(L9)、`pvp.*`(L15-17)、`powerups.*`(L20-24)、`shape.*`(L27-28)；
  - `bank.residual_fraction` / `bank.min_conversion_diameter_m`：**只写引用条目**（`"source": "layout.bank.rules.bank_residual_fraction"`），不写值——它们留在 `layout.json`（L1636-1637），规则集不复制，见下方取舍；
  - `growth.units_to_max_diameter` 1356、`absorb.size_ratio` 1.0（值取自 `round_model.py` L117-119）；
  - `motion.*` 8 个（值取自 motor L76-83）、`follow_rig.*` 3 个（L106-108）。
- 新增 `Docs/tests/test_rules.py`，**只**跑一条断言：镜像值 == 现存副本值（正则读 motor/`ball_motion.py`，`json.load` 读另两个）。这条断言的作用是把“27 个参数现在写在哪”变成可执行的事实，而不是本文的一段回忆。
- **仍然成立**：运行时、模型、地图、关卡一个字都没变；游戏行为 0 变化；Unity 不需要打开。
- **验证**：`python3 -m unittest discover -s Docs/tests`。

> **取舍（必须记下来）**：`bank.rules.bank_residual_fraction` / `bank_min_conversion_diameter_m` 现在是 `layout.json` 的字段，且被 `GreyboxPayload` 的 `GreyboxBankRotation` 消费（`layout.json` L1633 的 note 就是为此保留一个兼容字段）。**本次不把它们搬去规则集**——它们不是地图几何，但搬它们要改地图 schema、`validate_map.py`、`GreyboxPayload.cs`，而地图此刻正被另一位负责人编辑。规则集里对它们只**引用**（`"source": "layout.bank.rules"`），不复制值。等地图 owner 完成当前轮次后再评估是否合并（§10 未决 2）。

### 步骤 2 —— 模型改成从规则集构造

- 改 `Docs/Design/Gameplay/round_model.py`：加 `RoundParams.from_ruleset()`；dataclass 默认值保留并标注“测试/场景用”。
- 新增 `Docs/Design/Gameplay/tests/test_round_model.py::RulesetDefaultsTests`：`from_ruleset()` 与 `RoundParams()` 逐字段相等（除 `profiles`、`dt_s`）。
- **仍然成立**：24 个既有模型测试不变；`simulate.py` 的 `reports/` 数字不变。**若不等，停下报告 Lead**——那意味着现有默认值之间本来就不自洽，不是迁移的锅。
- **验证**：`python3 -m unittest discover -s Docs/Design/Gameplay/tests` + 重跑 `simulate.py` 与 `reports/` 对比。

### 步骤 3 —— 烘焙器与载荷（仍无消费方）

- 新增 `Tools/rulesbuild/build_rules.py`（确定性输出：键排序、固定键序、2 空格、末尾换行、UTF-8 无 BOM，同 `build_greybox.py`）；`--check` 过期退出 1。
- 新增 `Tools/rulesbuild/tests/test_build_rules.py`：确定性（同输入两次逐字节相同）、`--check` 在篡改后失败、`derived.start_diameter_m` 与 `SizeModelMath` 的输出一致。
- 生成 `Assets/VRC_Katamari/Rules/katamari_rules.json`；`.meta` 由 Unity 首次导入生成后提交一次。
- 补全 `Docs/tests/test_rules.py` 的 §4.1 五条断言。
- **仍然成立**：没有人读载荷。**这一步就能看见 §0.2 实例二**：`derived.units_to_max_diameter_effective = 394.41` 与 `growth.units_to_max_diameter = 1356` 并列出现在同一份载荷里。
- **验证**：`python3 Tools/rulesbuild/build_rules.py --check` + `python3 -m unittest discover -s Tools/rulesbuild/tests` + `… -s Docs/tests`。

### 步骤 4 —— 运行时组件 + 烘焙器（双写期）

- 新增 `Runtime/Rules/KatamariRules.cs`（`UdonSharpBehaviour`，一个参数一个 `[SerializeField]`，字段名 == 载荷 `runtime_field`）+ `.meta`；不新增 asmdef（仍属 `VRCKatamari.Runtime`）。
- 新增 `Editor/Rules/KatamariRulesPayload.cs`（DTO）+ `Editor/Rules/KatamariRulesBaker.cs`（菜单项，读 TextAsset → 写 prefab 字段）。**Editor 侧写的是"确定性赋值"**：同一载荷跑两次，prefab 的 diff 必须为空。
- 新增 `Assets/VRC_Katamari/Rules/KatamariRules.prefab`（挂组件，由 baker 写入）。
- 新增 `Tests/EditMode/Rules/KatamariRulesTests.cs`（§4.2 的测试 1、2）。
- **仍然成立**：motor 仍用自己的字段，**游戏行为完全不变**；这一步只证明“载荷能一比一还原当前值”。
- **验证**：Unity 编译通过 + U# 全量编译 0 错误 + `run_tests` 全绿。**本文件作者不是 Unity 操作者**，这一步与步骤 5 的验收必须由 Unity 操作者执行。

### 步骤 5 —— 切换消费方（**唯一的风险步**）

- 改 `KatamariBallMotor.cs`：
  - 删调参字段：L76-83（8 个运动）、L106-110（5 个跟随）、L95-99（2 个尺寸输入 + 2 个比值）、L103（`maxItemSizeWorldM`）、L116-117（2 个交分）；
  - 加 `[SerializeField] private KatamariRules rules;`；
  - 在 `Start()` 里把这些值**拷进带前缀的私有字段**（`localMaxSpeedMps`、`localBankResidualFraction` …），之后每帧照旧用私有字段（跨 Behaviour 字段访问在 U# 里有分发开销，不进每帧路径）；**前缀是必须的**，否则 §4.2 测试 3 无法区分“从规则组件拷来的副本”和“重新长出来的第二真源”。
- 同一次提交里加 §4.2 的测试 3。
- **关键不变式**：载荷的 `baked` 值必须与旧字段默认值**逐位相同**，所以数值行为必须**完全一致**；任何 Play Mode 差异都是接线错误，不是调参。
- 补一个等价证据：同一输入序列（`debugForward/Turn/Lateral` 脚本 + 固定 `deltaTime`）在改动前后各跑一次，逐帧记 `semanticDiameterWorldM` / `bankedScoreUnits` / `ballCenterWorld`，要求逐帧相等。
- **仍然成立**：T06 之前公共接口没有冻结——`KatamariRules` 是场景内接线，**不是**公共 API，字段名不承诺稳定。
- **验证**：上述等价证据 + `run_tests` + U# 全量编译；`Assets/SerializedUdonPrograms/*.asset` 的签名 diff 作为一批提交或丢弃。

### 步骤 6 —— 删除重复与搬迁原型配置（跨 owner）

- 删除 `Docs/config/prototype_defaults.json`（规则集里用 `supersedes` 指回它），更新 6 处引用：
  `Docs/00_PACKAGE_ENTRY.md:32`、`Docs/tests/test_validate_plan.py:104`、`Docs/Design/Maps/PicnicLawn/README.md:20`、`Docs/Design/Maps/ToyRoom/README.md:21`、`Docs/Design/Gameplay/round_model.py:104`、`Docs/Design/Gameplay/README.md:3`。
- `Docs/Design/GameModes/`（**另一个 owner**）：`validate_levels.py` 让 `ruleset_id` 解析到 `Docs/config/rulesets/<id>.json`；可选加 `ruleset_sha256`（§4.3）。
- 删除 `ball_motion.py` / `follow_rig.py` 里与规则集重复的默认值，改为从 `from_ruleset()` 拿。
- **仍然成立**：`Docs/tests/test_levels.py` 的 hash 期望要同步更新；`test_validate_plan.py` 的 config 断言改读规则集。
- **验证**：两条门禁命令 + `Docs/Design/Gameplay/tests` + `Tools/mapbuild/tests`。

### 步骤 7 —— 文档表改成引用

把 §0.1 里那类“文档复述”改成引用（**只改文字，不改数**）：

- [`BallMotor/README.md`](../BallMotor/README.md) L176-190 的“临时数值”表；
- [`BallMotion/README.md`](../BallMotion/README.md) L34-39；
- [`Gameplay/README.md`](../Gameplay/README.md) §7 的参数表；
- [`PicnicLawn/README.md`](../Maps/PicnicLawn/README.md) L79-81 的球径包线/档位表。

写法固定为：`值见 <真源>.<路径>（本文不再复述）`。**这一步会让 §0.2 的过期哈希一起消失**，因为不再有第二个地方钉版本。

### 步骤 8 —— 收尾

把本文件登记进 Docs 索引，跑完整门禁，并把步骤 3 发现的标定分歧（1356 vs 394.41）登记成一个**独立的平衡任务**（§8 风险 3）。

---

## 6. 不得烘焙的东西

判据只有一条：**它有没有“作者数据来源”。** 没有的就不进规则集，进了就是制造第四份副本。

### 6.1 每关覆写（进关卡文件，不进载荷）

`time_limit_s`、`quest.objective`、`spawn_id`、`bank.enabled`、`powerups.enabled`、`evaluation.*` 全是**开关与限时**，语义是“这一关开不开这条规则”，不是“这条规则是多少”。混进规则载荷会导致：同一 `ruleset_id` 的两个关必须有两份载荷，于是规则集退化成每关一份，`ruleset_id` 也就没意义了。

关卡数值本身（`time_limit_s` 等）由**回合控制器**在读关卡时消费；U# 运行时同样不能直接读磁盘，所以它也需要同一条烘焙路径——但那是**关卡载荷**（`level_id` + 时长 + 开关），本文只规定它不得重新声明规则数值。

### 6.2 每玩家（运行时状态，永不烘焙）

`PlayerProfile.speed_factor` / `absorb_factor` / `scenario_start_units`（`round_model.py` L85-97）、玩家数量、出生点分配、槽位、`protectedUntil` 一类计时。模型自己的注释（L88-91）已经写明 `scenario_start_units` “never a game parameter”。烘焙它们等于把测试夹具写进产品。

### 6.3 运行时派生（算出来，不烘焙）

- **起始球径**：由 `baselineVolumeUnits` + `cubicMetersPerVolumeUnit` 算出 **0.29974 m**（`BallMotor/README.md` L130）。把 0.30 烘成一个字段会立刻产生第二个“起始尺寸”，而 L130 那条“`start` 必须和体积映射同源”就是防这个的；
- 三个尺寸（`SizeModelMath.TryDerive` 输出）、`growthFraction`（motor L781）、`post` 交分直径与单位（`BankShrinkMath`）、`carried`（`TryCarriedVolumeUnits`）、`confirmed = unbanked + carried`（motor L757 `RecomputeConfirmedAndSize`）；
- 当前激活的交分锚点下标：由 `(roundStartClock + t) / period` 算，`activate_at_s`（`layout.json` L1620）是它的**人类可读展开**，不是第二个真源；
- `BallMotionMath` 的 `growthFraction` 降额结果。

### 6.4 结构容量、编译期常量与只属于运行时的标量（不进载荷）

`MaxPlaneCount = 8` / `MaxBoxCount = 8` / `ContactCapacity = 4` / `PendingCandidateCapacity = 4`（motor L225-247）、`PlayerCapacity = 1` / `CommitCapacity = 256` / `ClaimCapacity = 8` / `BankRequestCapacity = 8`（L173-177）、`AbsorbedEventCapacity = 16`（L267）、`SweepMath.ProjectionPasses = 4`（L52）、`RadialProxyMath.DirectionCount = 32` / `MaxExtremeCandidates = 4`（L57/L60）。

理由：这些是**缓冲形状**，改一次要重编译并重建所有预分配数组，不是“调参”。把 `directionSamples` 做成运行时可改的字段，会给出“改了但没生效”的假象。

**但其中两个与数据文件重复**：`prototype_defaults.json` L27-28 的 `shape.directionSamples / maximumExtraExtremePoints` 就是 `DirectionCount / MaxExtremeCandidates`。它们的正确处理是**等价测试**（§4.1 第 5 条），不是烘焙；数据文件里那两行保留为“结构常量的可读声明”。

**只属于运行时的标量**（没有作者数据来源，`[实现常量]`；改它们只需要改字段，不需要动规则集）：

| 字段 | 当前值 | 位置 | 为什么不是设计值 |
|---|---:|---|---|
| `fixedStepSeconds` / `maxStepsPerFrame` | 0.02 s / 5 | motor L71-72 | 走 [`BallMotor/README.md`](../BallMotor/README.md) L91 的“临时值，不是实测固定频率”；最终应改 `FixedUpdate` 或可配置频率 |
| `stuckTimeoutSeconds` | 1.5 s | motor L73 | 本层临时恢复策略，不是规则 |
| `maxLiftPerStep` / `maxDropPerStep` | 0.1 / 0.1 m | motor L88-89 | 贴地容差；无实测依据 |
| `separationEpsilon` | 0.001 | motor L92 | 数值容差 |
| `maxRollDisplacementM` | 0.5 m | motor L113 | 视觉滚动钳位，不是玩法 |
| `contactRadiusWorldM` | 0 | motor L102 | 占位；见 [`BallMotor/README.md`](../BallMotor/README.md) 缺口 4 |
| `collisionToCoreRadiusRatio` / `semanticDiameterPerCoreDiameter` | 1 / 1 | motor L98-99 | 今天让三个尺寸重合，只为让现有场景跑起来；[`02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md) §1（L15-21）要求它们分开。**这是场景接线缺口，不是参数值问题**，不要在这里“调参”解决 |
| `eyeHeightMinMeters` / `eyeHeightMaxMeters` | 0.25 / 3 m | motor L109-110 | 防越界夹取，不是舒适阈值（[`BallMotor/README.md`](../BallMotor/README.md) L187） |
| `ControlIntentMath.DefaultDebounceSeconds` | 0.2 s | `ControlIntentMath.cs:130` | 输入去抖的实现常量；若以后变成体验旋钮，必须整条搬进规则集 |

这些值**不进载荷**，理由是它们没有“作者改一个数、游戏应该跟着变”的语义。（顺带核对过：本代码库**没有** lookahead / 前瞻类常量——`grep -rn lookahead` 在 `Runtime/` 与 `Docs/Design/` 下为零；语义上最接近的是 `maxRollDisplacementM` 与 `stuckTimeoutSeconds`，两者都已在表中。）反过来说：**只要某个运行时量开始有人想按关卡调，它就必须整条搬进规则集**，而不是在 Inspector 上就地改——那会重新打开第二个真源。

### 6.5 模型独有假设（`[模型假设]`，永不烘焙）

`round_model.py` 里 16 个只有模型认的数：`dt_s`、`units_to_max_diameter`（**注意**：它是模型侧的标定，见风险 3）、`radius_exponent`、`absorb_size_ratio`、`absorb_items_per_s`、`base_speed_mps`、`size_speed_penalty`、`path_detour_factor`、`contact_dwell_s`、`steal_fraction`、`bank_service_s`、`powerup_respawn_s`、`powerup_pickup_s`、`restock_period_s`、`restock_fraction`、`bonus_categories`。

**其中三对是“看起来同名、其实不同物”，统一它们会改变玩法**：

| 模型的 | 运行时的 | 为什么不能合并 |
|---|---|---|
| `absorb_size_ratio = 1.0`（球径 × 比例） | `maxItemSizeWorldM = 1f`（绝对米） | 这是 [`BallMotor/README.md`](../BallMotor/README.md) 缺口 3：相对门槛没移植。今天两者数值巧合，语义不同；合并会让“小球吸大物”规则悄悄改变 |
| `base_speed_mps = 3.0`（区域间走路速度） | `maxSpeedMps = 4f`（物理速度上限） | 模型用它算路程时间，运行时用它算速度上限。把它们当成同一个数，会同时改坏模型结论和手感 |
| `units_to_max_diameter = 1356` | 体积标定 `0.0141 m³/单位` | §0.2 实例二：差 3.44 倍。**两边都标 PROVISIONAL，但没有任何一处说明它们必须一致** |

`radius_exponent = 1/3` 是球等效的**物理律**，不是旋钮（[`Gameplay/README.md`](../Gameplay/README.md) §7 已写“不是可调设计值”）。把它烘成可调字段，等于允许有人破坏体积守恒。

### 6.6 覆写怎么合成

优先级从低到高，**只有前两层是数值，后两层是开关与派生**：

```
① 地图默认      layout.json（球径包线、档位门槛、尺寸门、交分锚点/轮换、bank.rules）
        ↓  由 build_rules.py 汇入同一份载荷（规则集不复制地图值，载荷是二者的快照）
② 规则集        rulesets/<ruleset_id>.json（交分周期、对局时长、PvP、道具、运动/跟随调参、成长标定）
        ↓  烘焙
   载荷 katamari_rules.json → KatamariRules.prefab 字段
        ↓  回合控制器在开局时应用
③ 关卡覆写      Levels/*.json 的开关与限时（bank.enabled、powerups.enabled、time_limit_s、quest）
        ↓
④ 运行时派生    SizeModelMath / BankShrinkMath / 锚点轮换 / 每玩家状态
```

规则：**③ 只允许开关和限时，永远不允许覆盖 ② 的数值。** 如果将来真的需要“这一关交分缩得更狠”，正确做法是**加一个 `ruleset_id`**（每关引用不同规则集），不是给关卡加一个数值字段——后者会把规则集重新拆成每关一份。

---

## 7. 现在的重复清单（27 个参数 / 67 处机器可读副本）

只统计**机器读取**的位置（`json` 数据、运行时字段、Python 默认值、可被正则扫到的 C# 常量）。文档复述（§0.1 第 4、5 行那类）不计入，但它们必须按步骤 7 改成引用。

**A 组 地图与交分**

| # | 参数 | 机器可读副本（file:line） | 份数 | 应交真源 |
|---|---|---|---|---|
| 1 | 起始球径 0.30 m | `layout.json:21`、`round_model.py:113`（运行时由 `motor:95-96` 派生为 0.29974） | 2 | layout |
| 2 | 最大球径 2.20 m | `layout.json:22`、`round_model.py:114`、`motor:97`（`maxCoreRadiusWorldM=1.1`，半径制） | 3 | layout |
| 3 | 交分残差 `bank_residual_fraction` | `layout.json:1636`、`motor:116`、`round_model.py:128` | 3 | layout |
| 4 | 交分最小球径 0.55 m | `layout.json:1637`、`motor:117`、`round_model.py:131` | 3 | layout |
| 5 | 交分轮换周期 60 s | `layout.json:76`、`layout.json:1618`、`round_model.py:121` | 3 | layout |
| 6 | 交分预告 8 s | `layout.json:77`、`layout.json:1619` | 2 | layout |
| 7 | 交分锚点数 4 | `layout.json:75`、`layout.json` `anchors` 长度（L1537 起，`activate_at_s` L1620 是其展开） | 2 | layout |

**B 组 运动与跟随**（表内 `README.md` = `Docs/Design/BallMotion/README.md`）

| # | 参数 | 机器可读副本 | 份数 | 应交真源 |
|---|---|---|---|---|
| 8 | `max_speed_mps` 4.0 | `BallMotion/README.md:34`、`ball_motion.py:35`、`motor:76` | 3 | 规则集 |
| 9 | `acceleration_mps2` 5.0 | `README.md:35`、`ball_motion.py:36`、`motor:77` | 3 | 规则集 |
| 10 | `braking_mps2` 8.0 | `README.md:35`、`ball_motion.py:37`、`motor:78` | 3 | 规则集 |
| 11 | `friction_per_s` 1.2 | `README.md:36`、`ball_motion.py:38`、`motor:79` | 3 | 规则集 |
| 12 | `turn_rate_degps` 110 | `README.md:37`、`ball_motion.py:39`、`motor:80` | 3 | 规则集 |
| 13 | `max_lateral_mps` 1.2 | `README.md:38`、`ball_motion.py:40`、`motor:81` | 3 | 规则集 |
| 14 | `growth_speed_factor` 0.25 | `ball_motion.py:41`、`motor:82` | 2 | 规则集 |
| 15 | `growth_turn_factor` 0.35 | `ball_motion.py:42`、`motor:83` | 2 | 规则集 |
| 16 | `eye_height_m` 1.6 | `README.md:39`、`ball_motion.py:179`、`motor:108` | 3 | 规则集 |
| 17 | `height_filter_hz` 5.0 | `ball_motion.py:180`、`follow_rig.py:66`、`motor:106` | 3 | 规则集 |
| 18 | `yaw_filter_hz` 8.0 | `ball_motion.py:181`、`follow_rig.py:66`、`motor:107` | 3 | 规则集 |

**C 组 对局规则**（全部 `prototype_defaults.json` ↔ `round_model.py`，且运行时尚未实现，所以今天只有两份）

| # | 参数 | 机器可读副本 | 份数 | 应交真源 |
|---|---|---|---|---|
| 19 | `durationSeconds` 180 | `prototype_defaults.json:9`、`round_model.py:110` | 2 | 规则集 |
| 20 | `swallowDiameterRatio` 1.35 | `prototype_defaults.json:15`、`round_model.py:141` | 2 | 规则集 |
| 21 | `respawnDelaySeconds` 3 | `prototype_defaults.json:16`、`round_model.py:139` | 2 | 规则集 |
| 22 | `respawnProtectionSeconds` 2 | `prototype_defaults.json:17`、`round_model.py:140` | 2 | 规则集 |
| 23 | `magnetDurationSeconds` 4 | `prototype_defaults.json:22`、`round_model.py:145` | 2 | 规则集 |
| 24 | `rocketDurationSeconds` 2.5 | `prototype_defaults.json:23`、`round_model.py:147` | 2 | 规则集 |
| 25 | `rocketSpeedMultiplier` 1.5 | `prototype_defaults.json:24`、`round_model.py:148` | 2 | 规则集 |

**D 组 形状常量**（数据 ↔ 运行时 `const`）

| # | 参数 | 机器可读副本 | 份数 | 处理 |
|---|---|---|---|---|
| 26 | `directionSamples` 32 | `prototype_defaults.json:27`、`RadialProxyMath.cs:57` | 2 | 不烘焙，用 §4.1-5 等价测试钉住 |
| 27 | `maximumExtraExtremePoints` 4 | `prototype_defaults.json:28`、`RadialProxyMath.cs:60` | 2 | 同上 |

合计：**13 个参数写满三处（39 份）+ 14 个写两处（28 份）= 67 份机器可读副本，27 个参数。** 另有 1 处派生（起始球径，`motor:95-96` → 0.29974 m）不计入：它不是字面量，而是 §6.3 那条“运行时派生不得烘焙”的实例。

---

## 8. 执行这份计划最危险的三个地方

### 风险 1 —— 步骤 5 是唯一的行为风险步，而且“一致性”看起来会比实际更强

切换消费方要改 20 个字段的读取路径。U# 有个恶性组合：**编辑器失焦时不重编译**，`refresh_unity` 之后 Console 也可能只报一部分错；而 `SerializedUdonPrograms/*.asset` 的签名 diff 本来就被仓库规则当成“编译噪声”丢弃。于是“编译通过”很容易变成“我看到的最后一个成功状态”。

更糟的是“数值没变”这件事**无法靠读 diff 证明**：字段默认值被删掉了，`prefab` 里写的是同一批数，但 `Start()` 拷贝的顺序、`rules` 引用是否为空、`null` 时是否回落到旧默认值——任何一个错都会表现为“球滚起来有点不一样”，而人眼对 4 → 3.8 m/s 不敏感。

**缓解**：步骤 5 必须带一段可比较的 Play Mode 轨迹（同输入序列、固定 `deltaTime`、逐帧记三个量），在改动前后各跑一次并要求**逐帧相等**。这是本文唯一要求“跑两次并比对”的地方，因为它是唯一会动行为的地方。

### 风险 2 —— 载荷是两个文件的 join，而两个文件各有 owner

`katamari_rules.json` 合并了 `layout.json`（地图 owner）与 `rulesets/*.json`（规则 owner）。这带来一个 `--check` 结构上无法解决的问题：**它只能告诉你“载荷过期了”，不能告诉你“该烘哪一版”**。地图此刻正在被另一位负责人编辑（`layout.json` 与 `prop_catalog.csv` 的 mtime 一直在动），任何人在他编辑中途跑一次 `build_rules.py`，就会把他的在途改动烘进载荷并提交，看起来像一次正常的规则更新。

**缓解**：`source_sha256` 让“这批字节来自哪里”可查；流程上规定“只在自己的真源变更后、且输入 owner 说完成时”重新生成；`--check` 红的时候，**先问是哪一侧变了**，不要顺手重烘。这条比任何代码都重要，因为它是流程问题被工具放大了。

### 风险 3 —— “先把重复统一”其实是一次平衡改动，不是重构

这是最容易被忽略的一条。步骤 5 的**承诺**是“行为不变”，它成立的前提是载荷烘的是**当前运行时的标定**（`0.0141 m³/单位`、cap 1.1 → 394.41 单位满径）。但同一份载荷里还写着模型侧的 `units_to_max_diameter = 1356`（§0.2 实例二）。一旦有人“顺手把它统一了”：

- 运行时满径从 **394.41 单位**变成 **1356 单位**，即难 **3.44 倍**；
- 模型 §6 的结论（“2.20 m 结构性不可达，要改 `units_to_max_diameter` 或上限”）才第一次**适用于游戏**；
- 而今天所有的到档时刻（12.8 s 到 0.55 m 等，`Gameplay/README.md` §4.3）全部作废。

**缓解**：迁移**只做搬家**（保留 394.41 的运行时标定），把“统一到 1356”登记成独立的平衡任务，由 T21/T39 的证据决定，并且必须重跑 `simulate.py` + 重新采样 `reports/`。`known_divergences`（§3.2）存在的意义就是把这件事写进产品数据里，让下一个人无法“顺手统一”。

---

## 9. 本文不是什么

1. **不是实现。** 没有代码、没有 prefab、没有工具；`Tools/rulesbuild/`、`Assets/VRC_Katamari/Rules/`、`Runtime/Rules/`、`Editor/Rules/` 全部是**目标位置**，现在不存在。
2. **不是数值批准。** 本文引用的每个数都是现状，不是建议值。改任何数都需要对应的证据任务（§1.3 的 `[PROVISIONAL]` 行）。
3. **不是接口冻结。** T06 之前世界尺度未定；`KatamariRules` 的字段名是场景内接线，不是公共 API。
4. **不是验收。** 它不证明可玩、公平、舒适或性能；这些门禁仍是 T21/T22/T27/T38/T39。
5. **不替代现有校验器。** `validate_map.py` 管地图几何，`validate_levels.py` 管关卡自洽，本文的 `build_rules.py --check` 只管“真源 → 载荷”。
6. **不是原作声明。** 等级记号只引用 Research README 的 C 编号；没有 Cn 的都是项目自选。
7. **不授权改名。** 步骤 6 里的文件移动要各自的 owner 执行（地图 / GameModes / 规则）。

---

## 10. 未决项

| # | 事项 | 需要谁 |
|---|---|---|
| 1 | 规则集文件由谁 own（Lead / 平衡 / 地图） | Lead |
| 2 | `bank.rules.*` 是否从 `layout.json` 搬进规则集（§5 步骤 1 的取舍） | 地图 owner + Lead |
| 3 | 关卡 schema 是否加 `ruleset_sha256`（§4.3、§5 步骤 6） | GameModes owner |
| 4 | `KatamariRules` 放在球 prefab 上还是独立 prefab（今天 motor 还没进任何场景，所以两者都可行） | Unity 操作者 |
| 5 | 体积标定统一到哪一侧（§8 风险 3）：保留 394.41 还是改到 1356 | 用户 + T21/T39 证据 |
| 6 | 本文件的 Docs 索引登记位置 | Lead |

---

## 11. 验收

落地后必须同时给出：

1. `python3 Tools/rulesbuild/build_rules.py --check` 退出 0；
2. `python3 -m unittest discover -s Docs/tests` 全绿（含 `test_rules.py` 五条断言）；
3. `python3 -m unittest discover -s Tools/rulesbuild/tests` 全绿；
4. `python3 -m unittest discover -s Docs/Design/Gameplay/tests` 全绿，且 `reports/` 数字未变（步骤 2 的证明）；
5. Unity 编译通过、U# 全量编译 0 错误、`run_tests`（`VRCKatamari.Tests.EditMode`）全绿，含 `KatamariRulesTests` 三条；
6. 步骤 5 的逐帧等价证据（改前/改后各一次）；
7. `grep` 证据：`bankResidualFraction`、`baselineVolumeUnits`、`maxSpeedMps` 等字段名不再出现在 `KatamariBallMotor.cs`（§4.2 测试 3 的机器版）。

**`PROVISIONAL_NOT_TESTED`。** 本文没有 Unity、Udon、VR、网络或真人证据；所有数值都是现状引用，不是验证结果。

---

**参考**：[BallMotor 接口契约](../BallMotor/README.md) · [回合经济离线模型](../Gameplay/README.md) · [剧情关卡设计](../GameModes/README.md) · [野餐草坪](../Maps/PicnicLawn/README.md) · [代码约定](../CODE_CONVENTIONS.md) · [待决事项](../../OPEN_DECISIONS.md) · [原作规则核查](../../Research/KatamariBallRules/README.md) · [技术契约](../../02_TECHNICAL_CONTRACTS.md) · [mapbuild 流水线](../../../Tools/mapbuild/README.md)
