# 剧情模式（单人）与前两关设计 v0.1

**状态：`PROVISIONAL_NOT_TESTED`。** 本文只有设计与数据：没有 Unity 场景、没有 Udon 实现、没有真人试玩、没有实测帧率或舒适度。**除标注「原作确认」的行外，本文每一个数字都是暂定值**，不得当作平衡、公平、性能或舒适度 PASS 的证据。

本文定义两件事：① 剧情模式与 KatamariBall 的边界；② 野餐草坪（PicnicLawn）上的前两关，细到实现者不需要自己发明目标、数值或结算规则。

---

## 0. 证据等级与数据快照

### 0.1 标记约定

本文每一行结论都带下列标记之一。**没有标记的结论一律视为 `PROVISIONAL_NOT_TESTED`。**

| 标记 | 含义 | 现在能引用的地方 |
|---|---|---|
| `[原作确认 Cn]` | 有 Tier 1/2 公开来源直接说，且已有本仓库核查 | [KatamariBall 规则公开来源核查](../../Research/KatamariBallRules/README.md) §2 的 C1–C22 |
| `[原作强暗示 Cn]` | 多条来源一致但未直说 | 同上 |
| `[项目前提·待核查 G#]` | 本任务给定、与系列常识一致，**但本仓库没有任何剧情模式的公开来源核查**；不得写成「原作确认」 | 见 §5 的 G1–G4 |
| `[项目自选]` | 我们自己的选择，不是原作值 | 01/02/00 号文档或本文 |

**重要边界：** 本仓库的来源核查（`Docs/Research/KatamariBallRules/`）**只覆盖对战模式（KatamariBall）**。剧情模式（单人、国王委托）目前**没有一条可引用的公开来源**。因此本文所有「剧情模式怎么做」都是 `[项目前提]` + `[项目自选]`，不是考据结论。这一点必须写进任何对外说明。

### 0.2 地图数据快照（本文全部算术的依据）

| 文件 | sha256（**现算，不手抄**） | 采样时间（UTC） |
|---|---|---|
| `Docs/Design/Maps/PicnicLawn/layout.json` | `75340524…6765` | 2026-11-16 15:15 UTC |
| `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` | `6e9b0811…9b37` | 2026-11-14 06:15 UTC |

**规则：hash 现算，永不手抄。** 上表只是给人看的缩写副本；被机器校验的是 `Levels/*.json` 的 `map_snapshot`（`validate_levels.py` 与 `Docs/tests/test_levels.py` 逐字节比对磁盘文件）。手抄一定会过期——本文初稿抄过两版，两版都旧了：地图负责人把树根土丘从 0.18 m 降到 0.14 m 之后 `layout.json` 的 sha256 就变了，正文里的缩写还停在前一版。要当前值就跑：

```bash
sha256sum Docs/Design/Maps/PicnicLawn/layout.json Docs/Design/Maps/PicnicLawn/prop_catalog.csv
```

地图一改，重算这两个值、写进两个关卡文件，再跑 `python3 Docs/Design/GameModes/validate_levels.py`：它替你比对，不一致就报「地图已变更，请复核」，且**从不自动改数**。

该快照的合计值（本文所有引用都以此为准）：

| 量 | 值 | 来源 |
|---|---|---|
| 可卷实例 | **452** | `prop_scatter` 20 区合计 448 + `core_lawn` 4 张倒塌长凳 |
| 固定实例 | **60** | `prop_catalog.csv` 中 `role=fixed` 的 17 个定义 |
| 占位价值单位 | **1284**（s=1 / m=4 / l=16,20） | 逐件 `absorb_value_placeholder × count_in_map` |
| 档位分布 | **s 276 / m 154 / l 22** | 同上，按 `size_tier` |
| 区域 | 21（其中 20 个有散布） | `layout.json.zones` / `prop_scatter` |
| 球径包线 | 起始 0.30 m / 设计梯子顶 2.20 m（离线分析用，不是运行时上限） | `layout.json.scale`；运行时上限是全局 400 m（`OPEN_DECISIONS` (n)） |

**地图正在被另一位负责人编辑。** 表里的「采样时间」就是两文件当时的 mtime；写本文期间 `layout.json` 至少又改过两次（最近一次 2026-11-14 05:45，树根土丘 0.18 → 0.14 m），上一版快照就是这么过期的。本文的所有数字因此绑定 `Levels/*.json` 的 `map_snapshot` 两个 hash（上表是它们的缩写副本）：地图一改，§3 的算术与 §4 的 `map_snapshot` 校验会立刻失配——这是设计意图，不是缺陷。

### 0.3 本文的时间/体积算术怎么来的（可复现）

离线、标准库、无 Unity：

1. 用 `layout.json.prop_scatter` 的 `count_min` 与 `prop_catalog.csv` 的 `absorb_value_placeholder`、`size_tier`、`game_longest_dim_m` 构造一个 21 区的地图快照（`collapsed_bench` 按 PicnicLawn README §9 取 1.25 m 门槛，其余按 s `0.08–0.30` / m `0.32–0.95` / l `1.00–1.95` 夹紧）。
2. 把它喂给 [`round_model.simulate_round`](../Gameplay/round_model.py)，**单玩家、永不交分**的策略（`wants_bank=False`，其余按 `EatEverything.choose` 的「单位价值/到达时间」贪心选下一个区），`seed=7`，`dt=0.25 s`。
3. 成长公式沿用模型：`d = d0·(V/V0)^(1/3)`，`V = V0 + units·c`，`c` 由 `units_to_max_diameter=1356` 标定（吃满 1356 单位正好到 2.20 m）。

由此得到的单人成长曲线（本文 §3 的「模型时刻」全部出自这条曲线）：

| 时刻 | 累计件数 | 累计单位 | 球径 |
|---|---|---|---|
| 15 s | 28 | 79 | 0.86 m |
| 20 s | 44 | 143 | 1.05 m |
| 30 s | 74 | 260 | 1.27 m |
| 60 s | 138 | 465 | 1.54 m |
| 120 s | 229 | 933 | 1.94 m |
| 180 s | 317 | 1068 | **2.03 m（单人上限）** |

**这条曲线是乐观上界，不是玩家数据**：模型按「到区即清空该区」「直线距离 ×1.35 当走路」「拾取 3 件/s」近似（[Gameplay README](../Gameplay/README.md) §3 已自陈偏乐观）。真人会明显更慢，所以本文用它算出的「余量」是**乐观余量**，写进关卡时按 2× 打折看。

---

## 1. 模式划分：剧情模式 vs KatamariBall

| 维度 | 剧情模式（单人） | KatamariBall（4 人，支持离线 CPU 补位） | 证据 |
|---|---|---|---|
| **计分通道** | **无中途计分**。限时结束时把球呈给国王，一次性评估 | **必须把球送进场景内的「変換エリア」**，中局手动、可反复 | 对战＝`[原作确认 C1]`；剧情＝`[项目前提 G1]` |
| 分数形态 | 星级＋国王评语（0–3 星），不产生积分账 | 交存积分＋回合末奖励分 | 对战＝`C1/C10/C11/C12`；剧情＝`[项目自选]` |
| **兑换区 / 交分（bank）** | **没有**。不建锚点、不轮换、不产生 bank 事务 | **有**。4 个锚点、单台激活、60 s 轮换、提前 8 s 预告 | 对战＝`[原作确认 C1]`＋`[原作强暗示 C3]`（60 s 是强暗示，精确秒数未确认）；剧情＝`[项目前提 G1]` |
| 球会不会缩小 | **不会**。剧情模式球只增不减（没有交分，且无对手） | 交分＝用体积换分，球**变小**（不是清零） | 对战＝`[原作确认 C2/C5]`、`[C20 与证据不符]`；剧情＝`[项目前提 G1]` |
| 交分门槛 | 不适用 | 球太小拒绝兑换 | `[原作确认 C4]` |
| **对手** | **无**。场上只有本人 | 最多 4 人，可离线 CPU 补位 | 对战＝`S-KB05`（官方：4 人 / 联机 / 离线 CPU）；剧情＝`[项目前提 G1]` |
| 玩家间交互 | 无吞噬、无碰撞对手、无抢物 | 撞到更大的球被吞、短暂无法行动、异地重生；同尺寸互弹 | `[原作确认 C6/C8/C9]`、`[原作强暗示 C7]`、`[C22 淘汰制被否定]` |
| 回合结构 | 单关：出生 → 限时滚动 → 呈球评估 → 结果 / 重试 | 大厅 → 倒计时 → 准备 → Playing(180 s) → Settling → Results | 对战＝01 §6；剧情＝`[项目自选]` |
| 结束条件 | 计时归零（达成不提前结束，见 §2.4） | 计时归零＋结算，或只剩 1 人提前结束 | 对战＝01 §2/§6；剧情＝`[项目自选]` |
| 胜负判定 | **达成委托目标＝成功**；未达＝失败可重试 | 结算时**已交存积分最高者胜**（同分并列） | 对战＝`[原作强暗示 C10]`；剧情＝`[项目前提 G1]` |
| 失败处理 | 无限重试，无惩罚、不扣分 | 无「失败」，只有名次；被吞不掉已交存分 | 对战＝`[原作强暗示 C7]`；剧情＝`[项目自选]` |
| 结算奖励层 | 不启用 | 3 项类别各给严格第一名 +50 pt（结构已确认，类别清单是项目选择） | `[原作确认 C11/C12]`；本项目默认开（01 §2） |
| 道具（磁铁/火箭） | 首版**不投放**（暂定，见 §5-6） | 单槽，磁铁 4 s、火箭 2.5 s×1.5 | 对战＝`[原作强暗示 C15]`；数值＝项目原型 |
| **分数是否进 PvP 账本** | **不进**。剧情模式只读同一套「确认球径」，不写积分/交存字段 | 进：交存分与奖励分是本局唯一名次依据 | 剧情＝`[项目自选]`，见 T58 步骤 5 |
| 时长 | 120 s（本文两关，暂定） | 180 s（项目原型） | 对战＝`[C18 原作单局时长 unknown]`，180 s 是项目值；剧情时长＝`[项目自选]` |
| 使用的数据 | 同一张 `picnic_lawn`，但**关掉交分**、只开一个出生点 | 同一张图，4 出生点＋4 交分锚点轮换 | 02 §4 的 `mapId`／`rulesetId` 区分 |

> **必须写死的一句话：** 交分（兑换区 / bank）**只属于 KatamariBall**。剧情模式没有兑换区、没有交分事务、没有「交分后缩小」，也没有「未交存 vs 已交存」两本账。任何把 bank 逻辑挂进剧情模式的实现都是错的；实现上应当由 `bank.enabled=false` 显式关掉，而不是「反正没人走进去」。

三条推论，实现者必须知道：

1. **剧情模式的球只增不减**（无交分、无对手），所以 452 件是一次性预算：120 s 内模型已吃掉 229 件（51%）。限时超过 ~150 s 就必须依赖补货（T22，未实现）。
2. **剧情模式的分数不从体积换算**（`[C16]` 是**对战**的换算口径）。剧情模式用「是否达成委托 + 球径档位」评估，与单位价值无关——否则两条通道会互相污染。
3. **「兑换只看体积、不看内容」是对战规则**，不能推广成「剧情模式也不看内容」：剧情模式的收集类委托恰恰只看内容（§3.2）。

---

## 2. 剧情模式规则

### 2.1 委托类型（照原作三类）

| id | 委托类型 | 判定 | 本文是否实现 |
|---|---|---|---|
| `reach_target_diameter` | 滚到指定大小 | 球碰撞直径 ≥ `target_diameter_m` | ✅ 关卡 1 |
| `collect_category_count` | 收集指定类别 N 件 | 类别定义集合内的物件被**确认吸附**的累计件数 ≥ `count` | ✅ 关卡 2 |
| `absorb_specific_target` | 吞掉指定目标 | 指定 `definition_id`（或唯一实例）被确认吸附 | ⬜ 暂不做（地图目前没有唯一实例，见 §6-B6） |

`[项目前提·待核查 G1]` 三类委托是原作的委托家族，但**本仓库没有剧情模式来源核查**；G1 未补之前，不得在任何对外文案里写「原作就是这么分类的」。

**一关只用一种委托类型**（T58 验收条件）。不得在同一关里既要求大小又要求件数。

### 2.2 限时

- 两关都用 **120 s**（`PROVISIONAL_NOT_TESTED`，`[项目自选]`）。
- 原作的单关时长**未知**（`C18` 只否定「180 s 是对战原作值」这件事，剧情模式更是没有来源）→ G2。
- 限时的下限约束（可用性，不是原作值）：必须 ≥ 3× 模型达成时刻。本文两关分别是 28.8 s 与 ≤32 s 的最坏界，故 ≥ 96 s；取 120 s。

### 2.3 达成、结束、呈球

1. 计时从出生点落地开始（不设倒计时，剧情模式是单人准备，不存在「等其他人」）。
2. 计时归零 → 立即冻结球、冻结吸附 → 播放「呈给国王」→ 评估 → 结果卡。
3. **达成目标不提前结束**（`[项目自选]`，`PROVISIONAL`）：达成瞬间 HUD 翻成「已达成」，剩下的时间用来继续变大、冲更高星。理由是让「差一点」「超额」都有意义，也避免玩家达成后无事可做。原作是否在达成时提前结束是未知的 → G3。
4. 呈球是一次纯表现事件：不写账本、不给道具、不改变球径。

### 2.4 评估

星级只由**结算瞬间的那一个数**决定（球径或件数），不看过程、不看路线、不给「探索分」。

```
成就 = 目标达成（球径 ≥ target，或件数 ≥ count），记 achieved_at_s
星级 = 满足的最高的那一档（§3 各关的 stars 表）
0 星 = 未达成 → 失败，可重试
```

「差一点」的部分评价（`[项目自选]`，`PROVISIONAL`）：未达成但落在目标值的 `partial_band = 90%` 以内时，国王不说「完全不行」，改说「就差一点点」。这只是一个评语文案分支，**不改变星级**（0 星仍是 0 星）。

**实现边界（2026-11-15 拆分，评分是可选模块）**：星级属于「最后评分标准」，不是回合核心的一部分，因此代码按「核心只报事实、评分单独成件」分工：

| 归属 | 位置 | 负责 |
|---|---|---|
| 回合核心 | `Runtime/Story/StoryRoundController.cs` | 坐下/计时/到点冻结（输入、收集、候选）/状态机 `Idle→Running→Presenting→Result`/重试；结算瞬间把**事实**冻结成只读值：碰撞直径 `GetResultDiameterM()`、确认件数 `GetResultCount()`、状态 `GetState()`、剩余时间 `GetRemainingSeconds()`、目标 `GetGoalType()` / `GetTargetDiameterM()` / `GetTargetCount()`。它**不认识星级**：没有星级阈值、没有评语、没有结果卡 Text/面板。 |
| 星级算术 | `Runtime/Story/StoryStarMath.cs` | 纯静态、无状态：`TryEvaluateSizeStars` / `TryEvaluateCountStars`（tier1 = 委托目标，tier2/3 由调用方传入；partial band 判据）。 |
| 评分呈现（可选） | `Runtime/Story/StoryResultPresenter.cs` | 持有控制器的引用、自己的 tier2/3 与 `partial_band`、评语、结果卡 Text/面板；**轮询** `GetState()`，刚进入 `Presenting` 时读事实、算星级、写卡。 |
| 时钟 | `Runtime/Story/StoryRoundMath.cs` | `TryRemainingSeconds` 与共用的两位小数截断文本（HUD 与结果卡同一约定）。 |

依赖是**单向**的：`StoryRoundController` 里没有任何对 presenter 的引用、调用或字段，presenter 只通过控制器的公开 getter 读事实。因此**场景里不挂 presenter 是受支持的配置**——回合照常开始、计时、结算、冻结球、可重试，只是没有结果卡（此时场景里的结果卡面板应保持关闭，否则会露出场景自带的占位文字）。关卡数据里 tier 1 仍是委托目标（§4.5 规则 9），所以目标值留在控制器，tier 2/3、`partial_band` 与评语在 presenter 上；本文 §3 的星级数值不变。

### 2.5 重试

- **无限重试，无惩罚**（`[项目自选]`）。不记录失败次数、不扣星、不进 PvP 账本。
- 重试＝完整重置：球回 `start_diameter_m`、计时归零、**地图物件回到本关的初始世代**（`spawnGeneration` 递增，02 §6）。
- 因为剧情模式在共享实例里跑，重置必须**只影响本关的物件账本**，不能把大厅/对战区的东西一起刷回来。实现上剧情模式应使用独立的关卡区域或独立场景（§5-4）。

### 2.6 不做的部分（明确排除）

- 不做对手、不做 CPU 对手、不做观战。
- 不做交分、不做锚点轮换、不做最小兑换体积。
- 不做「未交存资源损失」「被吞」「重生保护」——这些是对战规则，剧情模式没有触发条件。
- 不做排行榜、不做连胜、不写 `bankedScore`。

### 2.7 与对战共享的部分（不能各写一套）

- 输入与摄像机（01 §4）、防退座与退出（01 §4 末）、舒适性（01 §8）**完全一致**。
- 吸收门槛：`可吸收 ⇔ 物件 pickupDiameterWorldM ≤ 球 collisionDiameter × absorb_size_ratio`（Gameplay README §2.1；`absorb_size_ratio` 原型 1.0）。
- 成长公式与体积→半径换算（02 §3.2，`r=cbrt(3V/4π)`）。
- 球径读取只认**确认值**（`confirmedVolumeUnits` → 球径），不认本地预测——否则呈球评估会出现「我明明到了」的争议（单人场景争议小，但规则要一致）。

---

## 3. 两关设计

两关共用同一张 `picnic_lawn` 灰盒，只换委托、限时、出生点、结算条件（T58 步骤 3）。**两关都不建任何新几何、不新增物件定义。**

### 3.1 关卡 1：`story_01_bench_size`「长凳那么大」

#### 关卡卡

| 字段 | 值 |
|---|---|
| `level_id` | `story_01_bench_size` |
| 委托类型 | `reach_target_diameter` |
| 目标 | **球碰撞直径 ≥ 1.25 m** |
| 限时 | 120 s |
| 出生点 | `spawn_nw`（−14.5, 0, 14.5），yaw 135°，朝地图中心 |
| 成功 | 任一时刻球径 ≥ 1.25 m（结算时记 `achieved_at_s`；结算球径必然也 ≥ 1.25） |
| 失败 | 120 s 归零时球径 < 1.25 m |
| 星级 | ★ 1.25 m / ★★ 1.45 m / ★★★ 1.60 m（`PROVISIONAL_NOT_TESTED`） |
| ★★★ 说明 | 1.60 m 需要 519.5 单位，超过本关 6 区合计 416 单位（只走这些区最多 1.487 m）：★★★ 是**刻意的「离开路线」**目标，关卡数据里带 `design_note_zh` 并由校验器强制声明；★★ 及以内仍按 6 区教学路线可达成 |
| 道具 | 无 |
| 交分 | 关闭（`bank.enabled=false`） |

**国王委托（中文，可直接上 HUD / 语音）**

> 「唔……朕的野餐会，好像少了点热闹。
> 你瞧那张长凳——横在草坪的对角门上，谁也别想从那儿过。
> 去，把那个土团滚起来，滚到**一米二五**那么肥！肥到能把那张长凳一口吞下去。
> 办到了，朕就赏你星光；办不到……哼，朕就当没看见你。」

**评语（结算）**：0 星「……就这样？朕的野餐会还没开始呢。再来一次。」／1 星「嗯，勉强能看。」／2 星「不错不错，有点样子了。」／3 星「好！这才是朕要的球！」

#### 这一关依赖的地图数据（全部对 `layout.json` 快照核过）

| 依据 | 值 | 出处 |
|---|---|---|
| 目标 1.25 m 不是拍脑袋 | `size_gates[*]`（4 个 `gate_bench_*`）的 `absorbable_at_ball_diameter_m = 1.25`、`blocks_ball_diameter_below_m = 1.05` | `layout.json.size_gates` |
| 出生角内容 | `spawn_nw` 12 件，**全部 s 档**（草籽 4 / 瓶盖 2 / 纸屑 2 / 小石子 2 / 落叶 2）＝12 单位 | `prop_scatter` |
| 出生角 + 西侧外环一共多少 | 12（spawn_nw）+ 33（fringe_west）+ 14（tree_roots_nw）+ 27（flower_bed_nw）＝**86 单位** | 逐区求和 |
| 所以必须进内场 | 1.25 m 需要 **245.9 单位**（见下），86 单位不够 → 玩家必须走到 `blanket_west`（80 件 / 266 单位） | 算术 |
| 相关区域 | `spawn_nw` → `fringe_west` → `tree_roots_nw` → `flower_bed_nw` → `blanket_west` →（可选）`basket_north` → `core_lawn`（4 张长凳） | — |
| 长凳 | `collapsed_bench` 4 件，l 档，`absorbable_at_ball_diameter_m = 1.25`，值 16 单位/件 | `prop_catalog.csv`、`size_gates` |

#### 算术（证明「不是只有完美操作才达得到」）

1. **需要多少体积。** 用 `d = d0·(V/V0)^(1/3)` 反解：
   - 1.25 m → **245.9 单位**（占 `units_to_max_diameter=1356` 的 18.1%；占全图 1284 单位的 19.1%）
   - 1.45 m → **385.8 单位**
   - 1.60 m → **519.5 单位**
2. **需要多少件。** 出生角 12 件（12 u）+ 外环 33 u + 树根 14 u + 花圃 27 u = 86 u 仍不够；再吃 `blanket_west` 的 266 u，累计 **352 u** ≥ 245.9 u ✅。按「s 档 1 u / m 档 4 u」的混合，约 **60–80 件**即可跨过目标；模型实测到点时为 **70 件 / 247 单位**。
3. **要多久。** 同一模型单人（永不交分）在 **28.8 s** 到达 1.25 m（70 件 / 247 u）；**54.3 s** 到 1.45 m（121 件 / 397 u）；**65.0 s** 到 1.60 m（153 件 / 523 u）。
4. **余量。** 28.8 / 120 = **24%**；54.3 / 120 = 45%；65.0 / 120 = 54%。即「达标」只需用掉限时的四分之一，**4.2× 余量**；即使真人比模型慢一倍，也还有 2× 余量。
5. **上限参照。** 单人 120 s 模型上限 1.94 m / 229 件 / 933 u；180 s 上限 2.03 m。所以 ★★★ 1.60 m（占 120 s 上限的 **82%**）是「有效率地滚」而不是「必须完美」——但仍属挑战档，`PROVISIONAL_NOT_TESTED`。
6. **1.25 m 一定吃得到长凳。** 门槛是「≥1.25 m 可吸走」，达成目标时球径恰好等于门槛，长凳可吞（`layout.json` 的 `blocks_ball_diameter_below_m = 1.05` / `absorbable_at_ball_diameter_m = 1.25`，含等号）。这一关的教学收尾就是「吃掉刚才挡路的东西」。

#### 教学意图（为什么是这一关）

| 要教的 | 载体 |
|---|---|
| 滚动与吸附：小物先吃，球变大后能吃更大的 | 出生角 12 件全是 s 档（0.04–0.10 m），0.30 m 球一次全吃 |
| 成长是唯一目标，不是「打人」 | 无对手、无交分、HUD 只有一个球径数字 |
| 地图有内场：好东西在里面 | 角落 + 外环只有 86 u，目标要 246 u |
| 成长会解锁东西 | 1.25 m 正好是 4 张长凳的门槛；吃掉长凳 → 对角门打开（`shortcut_door_*`） |

#### 路线与节奏（实现者按此做引导，不做强制）

```
spawn_nw(12u) ──6.5s──> fringe_west(33u) ──> tree_roots_nw(14u) ──> flower_bed_nw(27u)
                                                                        │
                                              blanket_west(80件/266u) <──┘   ≈ 20–30 s 到这里
                                                        │
                                              core_lawn 长凳(4件/64u)  ← 1.25 m 后可吞
```

- 出生 → `fringe_west`：直线 14.53 m，模型步长 19.62 m ≈ **6.5 s**。
- 出生 → `blanket_west`：直线 15.11 m，模型步长 20.40 m ≈ **6.8 s**。
- 插牌/地标已经够用（01 §8 双编码），本关**不新增**指引物；HUD 只显示目标直径与当前直径。

---

### 3.2 关卡 2：`story_02_fruit_ten`「给朕卷十件水果」

#### 关卡卡

| 字段 | 值 |
|---|---|
| `level_id` | `story_02_fruit_ten` |
| 委托类型 | `collect_category_count` |
| 类别 | `fruit`（**显式 7 个 `definition_id`**，不是 CSV 的 `category` 字段，理由见 §6-B1） |
| 目标件数 | **10 件** |
| 限时 | 120 s |
| 出生点 | `spawn_ne`（14.5, 0, 14.5），yaw 225°，朝地图中心 |
| 成功 | 被确认吸附的 `fruit` 累计 ≥ 10 件 |
| 失败 | 120 s 归零时 < 10 件 |
| 星级 | ★ 10 件 / ★★ 20 件 / ★★★ 30 件（`PROVISIONAL_NOT_TESTED`） |
| 道具 | 无 |
| 交分 | 关闭 |

**国王委托（中文）**

> 「朕今天要吃水果！
> 听清楚了——是**水果**。不是草籽，不是饼干渣，更不是盘子。
> 给朕卷**十件**回来：苹果、梨、橙子、香蕉、草莓、葡萄……两块野餐毯上多的是；
> 要是你能摸到篮子区的大西瓜，朕就更高兴了。
> 啊，对了。朕的胃口会越吃越大。二十件？三十件？你自己看着办。」

**评语**：0 星「朕数了数……一件水果都没有？再去。」／差一点（≥9 件未达 10）「九件！就差一件，你让朕怎么吃得开心？」／1–3 星按档升级。

#### `fruit` 类别的权威定义（`PROVISIONAL_NOT_TESTED`）

| `definition_id` | 中文 | 档 | 拾取门槛 m | 全图件数 | 在哪 |
|---|---|---|---|---|---|
| `strawberry` | 草莓 | s | 0.08（表内 0.06，按 s 档夹紧） | 8 | 两块野餐毯 |
| `grapes` | 葡萄 | s | 0.08 | 8 | 两块野餐毯 |
| `apple` | 苹果 | m | 0.32（表内 0.30，夹紧） | 10 | 两块毯 + 2 个篮子区 |
| `pear` | 梨 | m | 0.32 | 6 | 两块毯 |
| `orange` | 橙子 | m | 0.32（表内 0.30，夹紧） | 8 | 两块毯 |
| `banana` | 香蕉 | m | 0.45 | 6 | 两块毯 + 2 个篮子区 |
| `watermelon` | 西瓜 | m | 0.72 | 4 | **只在** 2 个篮子区（每区 2） |
| **合计** | | | | **50** | |

每块野餐毯 23 件水果（草莓 4 / 葡萄 4 / 苹果 5 / 橙子 4 / 香蕉 3 / 梨 3），两张毯合计 46；篮子区各 2 件（西瓜 2）→ **50**。
草籽（`seed`，64 件）虽然 CSV 里 `category=food`，但**不属于 `fruit`**，也不该被「水果」委托计数。

#### 这一关依赖的地图数据

| 依据 | 值 | 出处 |
|---|---|---|
| 出生角有没有水果 | **0 件**（`spawn_ne` 12 件全是草籽/瓶盖/纸屑/石子/落叶） | `prop_scatter` |
| 外环有没有水果 | **0 件**（4 个边带 27 件里只有 2 件汽水罐，是 `drink`） | `prop_scatter` |
| 最近的水果区 | `blanket_east`：80 件里 43 件 `food`、其中 **23 件水果** | `prop_scatter` |
| 第三个水果区 | `basket_north`：14 件里 2 件水果（西瓜 2）+ 1 件果汁（drink） | `prop_scatter` |
| 所以路线是必然的 | 出生角与外环零水果 → 玩家必须进内场，且**第一站只能是野餐毯** | 算术 |
| 相关区域 | `spawn_ne` → `fringe_east`（路过，0 水果）→ `blanket_east`（23 水果）→ `basket_north`（2 西瓜，6.6 s）→ `blanket_west`（23 水果，9.2 s） | — |

#### 算术（证明可达、且 ★★★ 真的需要跑两条路线）

1. **池子够不够。** 全图水果 **50 件**。★ 10 件 = 全图的 20% = 单块毯的 **43%**；★★ 20 件 = 单块毯的 87%；★★★ 30 件 = **单块毯（23）不够**，必须去第二块毯或篮子区 → 这就是「教路线」的机制来源，不是文案要求。
2. **等级门槛够不够。** 草莓/葡萄门槛 0.08 m，0.30 m 球开局就能吃；苹果/梨/橙 0.32 m 需 **0.7 单位**；香蕉 0.45 m 需 **8.2 单位**；西瓜 0.72 m 需 **44.2 单位**。而模型在第 15 s 时已累计 79 单位 → 进毯子后 5–10 s 内全部水果都吃得到，**没有「看得到吃不到」的卡点**。
3. **要多久（用模型实测的件数曲线，含走路）。** 同一模型（单玩家、永不交分）的累计件数：15 s→28 件、20 s→44 件、30 s→74 件、35 s→89 件、60 s→138 件、120 s→229 件。
   - **★ 10 件的上界**：假设最坏顺序（10 件水果全排在毯子最后），必须清掉 80 件 → 模型 **≈32 s**（80 件落在 30 s 与 35 s 之间）。32 / 120 = **27%**，3.8× 余量。
   - **★★ 20 件的上界**：单块毯清掉 77 件（80 − 23 + 20）→ **≈31 s**（27%）。
   - **★★★ 30 件的上界**：清空第一块毯（80 件 ≈ 32 s）+ 转场 9.2 s + 第二块毯吃到 7 件水果（最坏要清 64 件，按曲线里 ~2.5 件/s 的在区速率 ≈ 25 s）→ 保守上界 **≈66 s**；模型直接算「累计 144 件」是 **62.0 s（52%）**。
   - 路线腿长（模型口径：直线 ×1.35 ÷ 3 m/s）：`spawn_ne → blanket_east` 15.11 m → 20.40 m → **6.8 s**；`blanket_east → basket_north` **6.6 s**；`blanket_east → blanket_west` **9.2 s**。
4. **余量结论。** 达标（★）最坏 32 s 用掉 27% 限时；★★★ 最坏 66 s 用掉 55%。**这一关的达标不是「只有完美操作」**：即使真人比模型慢一倍，10 件仍在 ~64 s 内完成。
5. **上限参照。** 120 s 内模型可吸 229 件；★★★ 只需累计吸 ~144 件（80 + 64）。全图 50 件水果，吃到 30 件时还剩 20 件 → 不会「吃无可吃」。

#### 教学意图（为什么是这一关）

| 要教的 | 载体 |
|---|---|
| 读类别：HUD 上是「水果」图标组，不是「食物」 | 委托卡必须给 7 个剪影图标（01 §8 双编码：图形＋文字＋颜色） |
| 别把「食物」当「水果」：草籽、饼干、奶酪都不算 | 出生角与外环铺满草籽/纸屑，正是「不算数」的诱导物 |
| 路线：好东西在内场，且两块毯子才够 3 星 | ★★★ 30 > 单毯 23 |
| 西瓜在篮子区（唯一一个「不在毯子上」的水果） | 3 星之外的收集欲钩子；也教「篮子区 = 大件区」 |
| 只统计**确认吸附** | 撞掉/被别人拿走的不算（单人场景里只会被自己撞掉） |

#### 计数规则（实现者必须照做）

- 只统计 `fruit` 定义集合内、状态从 `Available/(ClaimPending)` 变成 `Attached`/`Removed` 且**由本关本人确认**的件数。
- 同一 `spawnId` 只计一次；补货/重生出来的**新实例**算新件（本关无补货，所以等价于只计一次）。
- 撞掉（`Falling` → `RespawnPending`）不计、也不倒扣。
- 计数与球径一样只认确认值，不用本地预测，避免「HUD 显示 10 件、国王数出 9 件」。

---

### 3.3 两关对照（速查）

| 项 | 关卡 1 | 关卡 2 |
|---|---|---|
| `level_id` | `story_01_bench_size` | `story_02_fruit_ten` |
| 委托类型 | `reach_target_diameter` | `collect_category_count` |
| 目标 | 1.25 m | 10 件 `fruit` |
| ★ / ★★ / ★★★ | 1.25 / 1.45 / 1.60 m | 10 / 20 / 30 件 |
| 限时 | 120 s | 120 s |
| 出生 | `spawn_nw`（yaw 135°） | `spawn_ne`（yaw 225°） |
| 必须走到的区 | `blanket_west`（+ 可选 `basket_north`） | `blanket_east`，3 星再加 `blanket_west` |
| 模型达标时刻 | 28.8 s（24% 限时） | ≤32 s（27% 限时） |
| 模型 ★★★ 时刻 | 65.0 s（54%） | 62.0 s（52%；保守上界 ≤66 s） |
| 交分 / 对手 / 道具 | 全部关闭 / 无 / 无 | 全部关闭 / 无 / 无 |

---

## 4. 关卡数据格式（决定）

### 4.1 结论

- **每关一个 JSON**，放在 `Docs/Design/GameModes/Levels/<level_id>.json`。
- 风格对齐现有作者数据（`layout.json`、`unity_greybox.json`）：snake_case、`schema_version`、`status`、id 引用而不是坐标。
- **关卡数据里不写任何坐标、半径、净宽，也不写逐 definition 的件数明细**——几何与数量只有一个真源 `layout.json` / `prop_catalog.csv`，关卡只写 `zone_id` / `definition_id` / 阈值。**唯一例外**是 `map_expectations` 里的两个总量（452 件 / 1284 单位）：它们不是玩法数值，而是「作者当时依据的地图版本」的指纹，用来发现地图漂移。
- 运行时**不读磁盘 JSON**（AGENTS「No disk JSON in Udon runtime」、02 §4「构建时烘焙为 Udon 可用数组/引用」）：编辑器导入器把 JSON 烘焙成 `StoryLevelDefinition` 资产，Udon 只读烘焙结果。
- 校验器：`Docs/Design/GameModes/validate_levels.py`（标准库、`--json`）。地图侧由通用校验器 [`Tools/mapbuild/validate_map.py`](../../../Tools/mapbuild/validate_map.py) 承担（`python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/PicnicLawn`）；两者同一形态：**只读作者数据**、纯标准库、结尾打印 `OK`/`FAIL`、`--json` 出机器可读摘要。

目录：

```
Docs/Design/GameModes/
  README.md                        ← 本文
  categories.json                  ← 类别注册表（§4.4）
  validate_levels.py               ← 本地校验器（§4.5，已实现）
  Levels/
    story_01_bench_size.json       ← 关卡 1
    story_02_fruit_ten.json        ← 关卡 2
```

烘焙后的运行时资产（实现阶段）：

- `Assets/VRC_Katamari/Runtime/GameModes/StoryLevelDefinition.asset`（每关一个）
- 导入器：`Assets/VRC_Katamari/Editor/GameModes/StoryLevelImporter.cs`
- 命名按「是什么」而不是任务号（AGENTS 命名规则）。

### 4.2 关卡 JSON 结构

```json
{
  "schema_version": 1,
  "level_id": "story_01_bench_size",
  "status": "PROVISIONAL_NOT_TESTED",
  "mode": "story",
  "order": 1,
  "map_id": "picnic_lawn",
  "ruleset_id": "prototype_defaults_v0_3",
  "map_snapshot": {
    "layout_sha256": "7534052468ed5a8f702e47568262f07fbcd899dbe5b36414196a44e25ed96765",
    "catalog_sha256": "6e9b0811c8fa0aab0e1412ebb83c4e44394bf96e93efd97e96ccad24503d9b37"
  },
  "quest": {
    "type": "reach_target_diameter",
    "text_zh": "……",
    "text_ja": null,
    "text_en": null,
    "objective": { "target_diameter_m": 1.25 }
  },
  "time_limit_s": 120,
  "spawn_id": "spawn_nw",
  "player": { "min": 1, "max": 1 },
  "bank": { "enabled": false },
  "powerups": { "enabled": false },
  "stars": [
    { "stars": 1, "requires": { "target_diameter_m": 1.25 }, "comment_id": "s1_ok" },
    { "stars": 2, "requires": { "target_diameter_m": 1.45 }, "comment_id": "s2_ok" },
    { "stars": 3, "requires": { "target_diameter_m": 1.60 }, "comment_id": "s3_ok",
      "design_note_zh": "1.60 m 超过所列 6 区可达范围，是刻意的离路线目标。" }
  ],
  "evaluation": {
    "presenter_id": "quest_giver",
    "partial_band": 0.90,
    "retry": "unlimited",
    "score_channel": "none",
    "ledger_write": false
  },
  "map_expectations": {
    "zones": ["spawn_nw", "fringe_west", "tree_roots_nw", "flower_bed_nw", "blanket_west", "core_lawn"],
    "absorbable_instances_total": 452,
    "gain_units_total": 1284,
    "size_gate_ids": ["gate_bench_nw"],
    "notes_zh": "目标 1.25 m 取自 gate_bench_* 的 absorbable_at_ball_diameter_m。"
  },
  "notes_zh": "…"
}
```

**块里两个 hash 是 2026-11-14 06:24 UTC 的现算值，只是示例**：写新关卡时照 §0.2 现算再填，别把整段 JSON 连同 hash 直接抄过去（关卡 1 的真实值见 `Levels/story_01_bench_size.json`）。`Docs/tests/test_levels.py` 的 `test_readme_declared_hashes_match_the_live_map` 会解析本文这两处（§0.2 缩写表与本节示例块）并与磁盘文件现算值比对，手抄改了 hash 就会变红。

`collect_category_count` 关只换 `quest.objective` 与 `stars[].requires`：

```json
"quest": {
  "type": "collect_category_count",
  "objective": { "category_id": "fruit", "count": 10 }
},
"stars": [
  { "stars": 1, "requires": { "category_id": "fruit", "count": 10 } },
  { "stars": 2, "requires": { "category_id": "fruit", "count": 20 } },
  { "stars": 3, "requires": { "category_id": "fruit", "count": 30 } }
]
```

### 4.3 字段表

| 字段 | 类型 | 必填 | 含义 / 约束 |
|---|---|---|---|
| `schema_version` | int | ✅ | 必须是 `1` |
| `level_id` | string | ✅ | `^story_[a-z0-9_]+$`，且 = 文件名去 `.json`；全局唯一 |
| `status` | enum | ✅ | `PROVISIONAL_NOT_TESTED` / `PROVISIONAL_TESTED` / `CONFIRMED` |
| `mode` | enum | ✅ | 必须 `story`（本目录只放剧情关；对战关不进这个 schema） |
| `order` | int | ✅ | ≥1，同 `map_id` 内唯一，用于关卡顺序 |
| `map_id` | string | ✅ | 必须等于某个 `layout.json` 的 `map_id` |
| `ruleset_id` | string | ✅ | 引用共享规则集（球径包线、吸收比、成长标定），**关卡不复制这些数字** |
| `map_snapshot.layout_sha256` | hex | ✅ | 作者依据的地图文件 hash；校验器比对，不匹配则报「地图已变，请复核」 |
| `map_snapshot.catalog_sha256` | hex | ✅ | 同上（道具目录） |
| `quest.type` | enum | ✅ | `reach_target_diameter` / `collect_category_count` / `absorb_specific_target` |
| `quest.text_zh` | string | ✅ | 国王语气正文；1–200 字；HUD 直接显示 |
| `quest.text_ja` / `text_en` | string 或 null | ⬜ | 01 §8 要求三语；缺省 `null` 表示回落到 `text_zh` |
| `quest.objective` | object | ✅ | 按 `type` 取不同键，见下 |
| `time_limit_s` | int | ✅ | 30–300，且 ≥ 3× 校验器算出的可行性下限（§4.5 规则 8） |
| `spawn_id` | string | ✅ | 必须 ∈ `layout.spawns[].id` |
| `player.min/max` | int | ✅ | 剧情模式必须 `1/1` |
| `bank.enabled` | bool | ✅ | **必须 `false`**（剧情模式无交分） |
| `powerups.enabled` | bool | ✅ | 本版两关都 `false` |
| `stars[]` | array | ✅ | 3 条，`stars` 递增 1→3；`requires` 严格递增 |
| `evaluation.presenter_id` | string | ✅ | 现只用 `quest_giver`（对外角色名见 §5-1） |
| `evaluation.partial_band` | float | ✅ | 0.5–1.0；未达成但达到「目标×band」时换评语，不改星 |
| `evaluation.retry` | enum | ✅ | `unlimited` / `limited:N` |
| `evaluation.score_channel` | enum | ✅ | 剧情模式必须 `none` |
| `evaluation.ledger_write` | bool | ✅ | **必须 `false`**（不写 PvP 账本） |
| `map_expectations.zones` | string[] | ✅ | 必须 ⊆ `layout.zones[].id`；用于校验器算「相关区总量」 |
| `map_expectations.absorbable_instances_total` | int | ✅ | 必须 = 快照实际值（452） |
| `map_expectations.gain_units_total` | int | ✅ | 必须 = 快照实际值（1284） |
| `map_expectations.size_gate_ids` | string[] | ⬜ | 若目标依赖尺寸门，必须列出并核对阈值 |
| `notes_zh` | string | ⬜ | 作者备注，不进运行时 |

`quest.objective` 各类型：

| type | objective 键 | 约束 |
|---|---|---|
| `reach_target_diameter` | `target_diameter_m` | 必须 ∈ [`ball_start_collision_diameter_m`, `ball_max_collision_diameter_m`] |
| `collect_category_count` | `category_id`, `count` | `category_id` 必须 ∈ `categories.json`；`count` ≥ 1 |
| `absorb_specific_target` | `definition_id`, `count`（默认 1） | 本版不实现（见 §6-B6） |

### 4.4 为什么需要 `categories.json`（类别注册表）

`prop_catalog.csv` 的 `category` 列**太粗**：`food` 里混了草籽（`seed`，64 件）、面包片、奶酪片；`drink` 与 `food` 的边界也和玩家直觉不同。所以「水果」这种委托类别**不能直接用 CSV 列**，必须另有一层注册表：

```json
{
  "schema_version": 1,
  "categories": {
    "fruit": {
      "name_zh": "水果",
      "definition_ids": ["strawberry", "grapes", "apple", "pear", "orange", "banana", "watermelon"],
      "exclude_definition_ids": [],
      "icon_ids": ["fruit_strawberry", "fruit_grapes", "fruit_apple", "fruit_pear",
                   "fruit_orange", "fruit_banana", "fruit_watermelon"],
      "notes_zh": "不含草籽；西瓜只在篮子区，用来勾玩家去篮子区。"
    }
  }
}
```

规则：一个类别 = 显式 `definition_ids` 列表；**不要**用 `category` 字段做减法（`food` 减 `seed` 这种写法在地图改分类时会静默失效）。

### 4.5 校验器规则（`validate_levels.py`，已实现）

形态与通用地图校验器 [`Tools/mapbuild/validate_map.py`](../../../Tools/mapbuild/validate_map.py) 一致：**只读作者数据**——本目录 `Levels/*.json` + `categories.json`，加上作为参照的 `Docs/Design/Maps/PicnicLawn/layout.json` 与 `prop_catalog.csv`；不碰 Unity、不写任何文件、不生成载荷。纯标准库。最后一行打印结论 `OK`/`FAIL`（退出码 0 = 通过、1 = 有 ERROR），加 `--json` 额外输出机器可读摘要（`status` + `errors`/`warnings`/`notes`），`--no-model` 跳过时间估算。运行：`python3 Docs/Design/GameModes/validate_levels.py`。

1. `schema_version == 1`；`level_id` 合法且 = 文件名；同目录内 `level_id` 唯一。
2. `mode == "story"`；`player.min == player.max == 1`；`bank.enabled == false`；`evaluation.score_channel == "none"`；`evaluation.ledger_write == false`。
3. `map_id` / `spawn_id` / `map_expectations.zones` / `size_gate_ids` 全部能在对应 `layout.json` 里解析到。
4. `map_snapshot` 两个 hash 与实际文件一致；不一致时报「地图已变更，请复核 target/count」，**不自动改数**。
5. `map_expectations.absorbable_instances_total` / `gain_units_total` 与快照实际值**逐字相等**。
6. `reach_target_diameter`：`target_diameter_m` 在包线内；且 `units_for(target_diameter_m) ≤ 该关 map_expectations.zones 合计单位 × 0.7`（**可达性**：光走这些区就够，还留 30% 余量；关卡 1 是 245.9 ≤ 416×0.7=291 ✅）；且 `units_for(target_diameter_m) ≤ 全图可卷单位`（否则「整张图都吃光也不够」→ FAIL）。
7. `collect_category_count`：类别所有 `definition_ids` 都是 `role=absorbable` 且属于 `map_id`；**全图该类件数 ≥ `2 × count`**（可达性下限；关卡 2 是 50 ≥ 20 ✅）；**第 3 星 `count` 必须 > 单区最大供给**（保证 3 星必须跑第二个区；关卡 2 是 30 > 23 ✅，否则 3 星可以一圈刷完，教不会路线）；**第 1 星 `count` 必须 > 出生角该类件数**（否则站着不动就完成；关卡 2 是 10 > 0 ✅）。
8. 可行性：用 `round_model` 的成长曲线（或校验器内联同一公式）估算达标所需件数，要求 `time_limit_s ≥ 3 × 估算时刻`。
9. `stars[]` 恰好 3 条、`stars` 递增、`requires` 严格递增且第 1 档 = `quest.objective`。`reach_target_diameter` 的第 3 星若高于 `map_expectations.zones` 的可达球径，必须带 `design_note_zh` 明确写成「刻意离开路线」的扩展目标（否则 WARN）；说明过时（目标已在所列区可达）则 FAIL。
10. `partial_band ∈ [0.5, 1.0]`；`time_limit_s ∈ [30, 300]`。
11. 所有 `*_id` 引用**只允许**来自 `layout.json` / `prop_catalog.csv` / `categories.json`，禁止自由文本 id。
12. 文件里出现坐标、半径、净宽或逐 definition 的件数明细 → 直接 FAIL（防止绕过单一真源）；`map_expectations` 的两个总量是唯一豁免。

**与 `validate_plan.py` 的关系（重要）**：`Docs/tools/validate_plan.py` **只校验 `tasks.json`**，不读任何关卡文件。所以 T58 验收里「可被 `validate_plan.py` 检查」只能落实为两件事：① T58 自己的任务条目通过 `validate_plan.py`；② 关卡数据由 `validate_levels.py` 检查。

### 4.6 两关的数据（按 §4 schema 的取值表）

| 字段 | 关卡 1 | 关卡 2 |
|---|---|---|
| `schema_version` | 1 | 1 |
| `level_id` | `story_01_bench_size` | `story_02_fruit_ten` |
| `status` | `PROVISIONAL_NOT_TESTED` | `PROVISIONAL_NOT_TESTED` |
| `mode` | `story` | `story` |
| `order` | 1 | 2 |
| `map_id` | `picnic_lawn` | `picnic_lawn` |
| `ruleset_id` | `prototype_defaults_v0_3` | `prototype_defaults_v0_3` |
| `map_snapshot` | §0.2 两 hash | §0.2 两 hash |
| `quest.type` | `reach_target_diameter` | `collect_category_count` |
| `quest.objective` | `{"target_diameter_m": 1.25}` | `{"category_id": "fruit", "count": 10}` |
| `quest.text_zh` | §3.1 国王委托全文 | §3.2 国王委托全文 |
| `time_limit_s` | 120 | 120 |
| `spawn_id` | `spawn_nw` | `spawn_ne` |
| `player` | `{"min":1,"max":1}` | `{"min":1,"max":1}` |
| `bank.enabled` | `false` | `false` |
| `powerups.enabled` | `false` | `false` |
| `stars[0].requires` | `{"target_diameter_m":1.25}` | `{"category_id":"fruit","count":10}` |
| `stars[1].requires` | `{"target_diameter_m":1.45}` | `{"category_id":"fruit","count":20}` |
| `stars[2].requires` | `{"target_diameter_m":1.60}` | `{"category_id":"fruit","count":30}` |
| `stars[2].design_note_zh` | §3.1 的离路线说明（超出 6 区 416 单位） | —（3 星在相关区可达） |
| `evaluation.presenter_id` | `quest_giver` | `quest_giver` |
| `evaluation.partial_band` | 0.90 | 0.90 |
| `evaluation.retry` | `unlimited` | `unlimited` |
| `evaluation.score_channel` | `none` | `none` |
| `evaluation.ledger_write` | `false` | `false` |
| `map_expectations.zones` | `spawn_nw, fringe_west, tree_roots_nw, flower_bed_nw, blanket_west, core_lawn` | `spawn_ne, fringe_east, blanket_east, basket_north, blanket_west` |
| `map_expectations.absorbable_instances_total` | 452 | 452 |
| `map_expectations.gain_units_total` | 1284 | 1284 |
| `map_expectations.size_gate_ids` | `gate_bench_nw`（+ 其余 3 个同阈值） | `[]` |

---

## 5. 未决事项（每条都给了暂定选择）

| # | 未决 | 现在的暂定 | 谁来定 | 需要什么才能定 |
|---|---|---|---|---|
| 1 | **国王/委托者怎么呈现**（谁说话、什么形象、UI 形态） | 内部沿用「国王」语气；**对外形象用本项目自有吉祥物**（球/屎壳郎一族），不复用原作角色。`evaluation.presenter_id` 就叫 `quest_giver`，不叫 king | 用户 + 美术 | 00 号文档「不复用王子、国王、原作角色音乐」的边界确认；一句话文案复核 |
| 2 | **评估公式**（星级阈值怎么来） | 纯阈值查表，阈值 = 成功目标 × 递进（本文 4 档）；不做加权分 | 设计 + 真人试玩 | T39 前用 3–5 名真人测「达标时刻分布」，把阈值钉在 P50/P80/P95 |
| 3 | **重试规则** | 无限重试、无惩罚、完整重置物件世代 | 设计 | 是否需要「失败次数」做成就；共享实例里重置的隔离方案（§5-4） |
| 4 | **剧情模式用整张图还是裁剪出生集** | **整张图**（452 件全开），只换 `spawn_id`、目标、限时 | 设计 | T21 实测「单人 120 s 能吃多少」；若吃空太快再裁剪 |
| 5 | **剧情模式与对战是否同实例** | 独立场景/独立关卡区域，独立物件世代 | 技术 + 设计 | 02 §6 世代方案；VRChat 单实例内的状态隔离实测 |
| 6 | **剧情模式给不给道具** | **不给**（磁铁/火箭是对战抢夺设计的一部分） | 设计 | 是否需要「成长加速」教学关；若给，先解决与对战数值共用的问题 |
| 7 | **达成后是否提前结束**（G3） | **不结束**，继续滚到计时归零 | 设计（原作未知） | 剧情模式来源核查；真人试玩反馈 |
| 8 | **语言** | `text_zh` 必填；`text_ja/en` 预留字段 | 本地化 | 01 §8 三语要求落地时补 |
| 9 | **「差一点」的 band** | 目标 × 0.90 | 设计 | 真人试玩里「差一点」与「差很多」的体感分界 |
| 10 | **原作剧情模式来源核查缺失（G1–G4）** | 全部标 `[项目前提]`，不写「原作确认」 | 研究 | 一次与 `KatamariBallRules` 同规格的剧情模式来源核查（委托类型、限时、呈球结算、失败重试） |

**G1–G4 待补核查清单**：G1 委托类型家族与分类方式；G2 单关时长（区间与是否固定）；G3 达成时是否立即结束；G4 失败是否有惩罚/是否可无限重试。

---

## 6. 与地图数据的一致性核查（report-only，不改地图）

### 6.1 数字对账：`PicnicLawn/README.md`（2026-11-14 03:40 版）vs 当前 `layout.json`+`prop_catalog.csv`

| 项 | README 写的 | 快照实际 | 影响 |
|---|---|---|---|
| 可卷实例 | 452 | **452** ✅ | — |
| 固定实例 | 60 | **60** ✅ | — |
| 占位价值单位 | 1356 | **1284** ❌ 差 72 | `units_to_max_diameter=1356` 是模型标定值，不是地图总量；**吃满全图只能到 2.16 m，2.20 m 不可达** |
| 档位分布 | s 252 / m 178 / l 22 | **s 276 / m 154 / l 22** ❌ s/m 互换了 24 件 | 只影响「按档统计」的委托；本文两关都不按档计数 |
| 边带 | 21 件 / 27 单位 ×4 | **27 件 / 33 单位 ×4** | 外环比文档更肥；对本文关卡无影响（§3.2 靠「零水果」不靠件数） |
| 篮子区 | 26 件 / 220 单位 ×2 | **14 件 / 172 单位 ×2** | 篮子区明显缩水；§3.2 的西瓜依赖仍在（每区西瓜 2 件） |
| 野餐毯 | 80 件 / 266 单位 ×2 | **80 件 / 266 单位 ×2** ✅ | — |
| 出生角 | 12 件 / 12 单位 ×4 | **12 / 12 ×4** ✅ | — |
| 树根 / 花圃 | 14 / 12 件 | **14 / 12** ✅ | — |

**对账结果（2026-11-14）：** 地图负责人在 04:41 的 `18d7ae24` 与随后的工作树修改里已把价值总量改为 1284、边带 27/33、篮子区 14/172，并把 `bank_resets_ball_to_start_size` 改为 `false`、补上 `bank_size_rule=proportional_shrink`。上表保留为审计记录；当前 `PicnicLawn/README.md` 与地图数据一致，本文关卡的两个 sha256 也已同步。

### 6.2 会让「好委托」做不成的地图数据问题

| # | 问题 | 后果 | 建议（report-only） |
|---|---|---|---|
| **B1** | `prop_catalog.csv` 的 `category=food` 包含草籽（64 件，散在出生角/外环） | 「收集 N 件食物」会被草籽刷满，委托瞬间失去意义；「水果」这种子类根本不存在 | 委托类别走 §4.4 的显式 `definition_ids` 注册表；别用 `category` 做减法 |
| **B2** | 全图 1284 单位 < 1356 标定 | **「滚到最大 2.20 m」这类委托在这张图上不可能完成**（理论满清 2.16 m，模型 180 s 实测 2.03 m） | 要么补 ~72 单位物件，要么把 `units_to_max_diameter` 下调（属 T20/T21 决策），要么剧情关不设「满径」目标 |
| **B3** | 出生角只有 s 档（12 件 / 12 单位），且草籽/石子/纸屑在全角落都有 | 任何 `collect_category_count` 若把类别定在角落可拾取物上，玩家不用动就能完成 | 关卡层保证：`count` 必须大于出生角该类件数（已写进校验器规则 7；关卡 2 是 10 > 0） |
| **B4** | 中央草坪只有 4 张长凳（l 档，1.25 m 门槛） | 剧情模式里 `core_lawn` 几乎是空场：4 件 64 单位，且要 1.25 m 才吃得到 | 若以后要做「吞掉中央地标」类委托，需要地图加不可卷地标或别把草坪留空 |
| **B5** | 花圃 12 件里 m 档是 0.55/0.58 m 门槛 | 早期进花圃（1.10 m 翻越门）之前就能吃到台内 m 档（0.55 m 球）→ 花圃的「看得到吃不到」其实只对小 m 档成立 | 若要做「花圃专类收集」委托，先复核花圃门槛与成长节奏 |
| **B6** | 每个 `definition_id` 都有多个实例（苹果 10、长凳 4…），**没有唯一实例/唯一地标可卷** | 「吞掉指定目标」这类委托无法指定「那一个」，只能按 `definition_id + count` 退化 | 需要地图提供唯一 landmark（或 `spawnId` 级引用）才能做第三类委托 |
| **B7** | 交分锚点/轮换写在 `layout.json` 里，且 `activate_at_s` 从 0 开始 | 剧情模式若直接复用这张图，旗杆/自发光环会按 60 s 轮换干扰「无交分」的叙事 | 运行时必须在剧情模式关掉 bank 轮换与旗杆脉冲（`bank.enabled=false` 要真的关表现，不只是关判定） |
| **B8** | 无补货（`restock_period_s=0`，T22 未实现）且单人 120 s 已吃 229/452 件 | 限时 >150 s 的剧情关会「吃空」；延长关卡前必须先有补货 | 剧情关时长先压在 120 s 内 |

---

## 7. 实现清单（给实现者，按依赖顺序）

1. **关卡片 / HUD**：显示委托正文、目标数值、当前进度、达成翻面、剩余时间；类别委托显示 7 个剪影图标（01 §8 双编码）。
2. **`StoryLevelDefinition` 资产 + 导入器**：把 §4 的 JSON 烘焙成 Udon 可读的序列化字段；运行时零磁盘 IO、零编辑器 API。
3. **剧情回合状态**：`Idle → Running → Presenting → Result`；`bank` 与 `powerup` 子系统在本模式不实例化。
4. **计数/测量**：`reach_target_diameter` 读确认球径；`collect_category_count` 读确认吸附事件（`Attached`/`Removed`）并按 `categories.json` 过滤。
5. **呈球与评估**：时间到 → 冻结 → 播报 → 星级查表 → 结果卡；`evaluation.ledger_write=false` 的账本闸门要显式实现（写入路径直接不挂）。
6. **重试**：重置球径、计时、物件世代；只重置本关区域。
7. **校验器**：`validate_levels.py`（§4.5 的 12 条），与关卡 JSON 一起进 CI 式检查。
8. **不做**：对手、CPU、交分、锚点轮换、最小兑换体积、排行榜。

---

## 8. 本文引用的证据

| 编号 | 断言 | 判定 | 用在哪 |
|---|---|---|---|
| `C1` | 计分必须把球送进场景内「変換エリア」，不是自动结算 | confirmed | §1：交分只属于对战；剧情模式没有交分 |
| `C2` | 交分＝用体积换分，球会变小 | confirmed | §1：剧情模式球不缩小的对照项 |
| `C3` | 兑换区在王子飞船正下方、约每 60 s 换位、有导航图标 | strongly implied | §1 轮换周期；§6-B7 |
| `C4` | 体积过小不予兑换（有最小值门槛） | confirmed | §1：剧情模式无门槛概念 |
| `C5` | 缩小后吃不动大物件（缩小有实质代价） | confirmed | §1：剧情模式不缩小，因此没有这个代价 |
| `C6` / `C8` / `C9` | 被更大的球吞、短暂无法行动、异地重生；可吞更小的对手；同尺寸互弹 | confirmed | §1：剧情模式无对手，这三条不触发 |
| `C7` | 被吞不是淘汰、不掉已得分 | strongly implied | §1：剧情模式没有「掉分」概念 |
| `C10` | 单场结束积分最高者胜 | strongly implied | §1：对战胜负；剧情模式胜负=达成委托 |
| `C11` / `C12` | 结算 3 项各第一名 +50 pt；类别取自吞球数/冲刺/道具/Brother/最大尺寸 | confirmed | §1：剧情模式不启用奖励层 |
| `C16` | 换算只看体积，不按物品种类计分 | strongly implied | §1 推论：这条是**对战**口径，剧情收集类委托看的是内容 |
| `C18` | 单局时长 | **unconfirmed** | §1/§2.2：120 s / 180 s 都是项目值 |
| `C20` | 交分后「清空回初始体积」 | **与证据不符** | §6.1：`layout.json` 仍写着 reset-to-start，需修 |
| `C21` | 地图数量、出生点规则 | **unconfirmed** | §1：两关同图是项目选择 |
| `C22` | 有淘汰制/缩圈 | **contradicted** | §1：剧情模式更不存在淘汰 |
| `S-KB05` | 官方确认 4 人 / 联机 / 离线 CPU | 一级来源 | §1 对手与 CPU 补位 |

**再次强调：以上没有一条是剧情模式的来源。** 剧情模式的部分全部是 `[项目前提·待核查 G1–G4]` + `[项目自选]` + `PROVISIONAL_NOT_TESTED`。

---

## 9. 下一步（谁来做什么）

1. **地图负责人 / 主管**：§6.1 的数值与 `bank_resets_ball_to_start_size` 矛盾已修；仍需拍板是否补 ~72 单位（或降 `units_to_max_diameter`）让 2.20 m 可达；以及剧情模式要不要在 `core_lawn` 加不可卷地标（§6.2-B4）。
2. **研究**：补一次剧情模式公开来源核查（G1–G4），规格照 `KatamariBallRules`。
3. **实现**：§7 的 1–8；先做关卡 1（只需球径读取 + 查表），再做关卡 2（需要类别注册表与确认吸附计数）。
4. **测试（T39 前）**：真人试玩钉住星级阈值（§5-2）与「差一点」的 band（§5-9）；把 120 s 的达标时刻分布回填本文。
5. **本文状态**：只有在拿到真人数据后，才能把 §3 的「模型时刻」换成实测值，并去掉 `PROVISIONAL_NOT_TESTED`。

---

**PROVISIONAL_NOT_TESTED。** 本文所有数值（1.25/1.45/1.60 m、10/20/30 件、120 s、星级阈值、28.8 s 等）都是离线推演，没有 Unity、Udon、VR、网络或真人证据；不得引用为平衡、公平、性能或舒适度 PASS。地图相关数字绑定 `Levels/*.json` 的 `map_snapshot` 两个 sha256（现算命令见 §0.2）。
