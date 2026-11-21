# 道具行为与堆叠：五种 kind、状态机、父指针

> **吸收门的口径**：物件体积 `V = k·a·b·c`（`k` 缺省 0.70），门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`（实现：`PickupAbsorbMath.TryComputePickupReqDiameterMm` / `IsBeyondAbsorbLimit`）；实现清单与前后实测见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。


**本文回答四件事（B / C / D 三份交付合并在这里）：**

1. 原作道具的**五种类型**分别是什么，我们怎么把它们变成可序列化的字段、状态机和更新预算；
2. **堆叠问题**：原作在「一次吃掉下面好几件」时，怎么知道上面的东西该开始落？我们要怎么做；
3. 行为库的**起步原型清单**与三个主题的数量预算；
4. 全部结论的**证据级**，以及与地图作者规则摘要的交叉引用。

**没有在 Unity 里运行过任何东西**，也没有对原作做任何测量或拆包。凡涉及我们运行时的都是**代码事实**（`文件:行`）；凡涉及原作的都是公开文字，按 §1 的标签分级；凡是我们自己的设计都标「本项目设计」。v1 的堆叠形态是父件被吃 ⇒ 子件坠落、不带，设计在 §10.9。

---

## 0. 结论摘要

0.1 **`kind` 不是行为**。`placement.json` 里的 `kind` 是**内容分类**（`fruit` / `tableware` / `food` / `container` / `drink` / `nature` / `furniture` / `garden` / `structure` / `toy` / `debris`，第一关 127 件的实际分布），它只喂给吸收白名单。行为必须是**另一列**，否则「石头」既是 `nature` 又要会掉，语义会打架。**2026-11-16 用原版数据核对过：原版也是分列存的，而且分得比我们细（§10.1）。**

0.2 **原作不用通用物理来做堆叠**。能查到的公开材料里没有任何一处描述堆叠机制；专利 US7402104B2 只讲球的三种模型（显示 / 碰撞 / 滚动），堆叠一个字没有。而官方图鉴文本里有一条**反证**：不倒翁积木的「头」被写死成「下面的积木被敲掉也不动」——如果堆叠是刚体解算，这种部件不可能存在。→ 最省的解释是**每件各自带行为参数 + 一个通用的支撑检查**，这也是本文的设计。

0.3 **「它怎么知道是链在一起的」——它不知道，也不需要知道。** 每件道具只记**一个父件**；某件被吃掉或开始落时，把 `propParent == 它` 的子件放进待复检队列，每个定步 tick 问一次「脚下有没有东西」——没有就落，有就等。「整摞塌下来」是这个规则的**几何后果**，不是任何一处代码在识别链。
0.4 **v1 是「单父指针 + 每 tick 浮空复检」，不是物理。** 运行时只有：每件一个父索引、一个定长待复检队列、一次固定步长的坠落积分。没有 `List`、没有字典、没有 `Rigidbody`、没有逐件 `Update`。
0.5 **`support` / `stackedOn` / `parent` / `parentDerived` / `pinned` 今天都只是数据与校验器的字段**，引擎不读（`Tools/levelbuild/placement_core.py:210` 的 `PROP_KEYS`、`Docs/Design/Tools/LEVEL_AUTHORING.md` §4.2）。`parent` 另有一份运行时副本：`PickupFieldSync` 把它写成 `PickupFieldHost.propParentIndex`（行索引，-1 = 无），`PropFallHost` 读它——「上面的东西会掉」的实现见 §10.10；`support` / `stackedOn`（旧写法）/ `pinned` 运行时仍不读。

0.6 **一个必须同时改的数据陷阱**：`placement_core` 的规范化只保留 `PROP_KEYS` 里的键（`placement_core.py:626`），**新加的 `behaviour*` 字段会被校验器静默忽略、被任何一次 rewrite 静默丢弃**。所以字段提案必须和校验器改动同一批落地，否则作者会以为填了。

0.7 **预算不是瓶颈，Draw Call 才是**。按 0.02 ms/次 Udon 更新、90 Hz 帧里给 25% 算，一帧能做 **138 次**变换更新；路径件按 15 Hz 分摊后，**约 800 个**在跑才吃满这点预算。真正的上限是骨骼/Animator/Draw Call 和不透明物件的可见量，不是 Update 算术；其中 **Animator 数量按 owner 2026-11-16 的口径不预先优化**（见 §7.1）。

---

## 1. 边界与证据级

| 标签 | 含义 |
|---|---|
| `[官方]` | 开发商/发行商自述、游戏内文本、官方手册 |
| `[专利]` | 专利原文（只作为机制存在性的证据，不引用原文长句） |
| `[社区]` | wiki / 攻略 / 论坛 / 速通笔记的人工观察 |
| `[推断]` | 本文的推论，**不是**原作事实 |
| `[本项目设计]` | 我们自己的设计，没有原作依据 |

**明确不声称**：本文没有原作实机、没有拆包、没有拿到任何道具行为表。§5.1 的三条公开证据只能支撑「摞着的东西会倒」「不是所有都会倒」「移动件要先撞飞才吸得进」这三句话，**不能**支撑任何数值或实现方式；其中第三条我们**已经决定不复制**（§6 第 5 条，用户 2026-11-15 拍板）。

---

## 2. 现状事实（能依赖的与不能依赖的）

| 事实 | 证据 | 对本设计的意义 |
|---|---|---|
| 三层分离：视觉网格 / 碰撞体 / 玩法体积 `propBounds`；本项目**不缩放、真实尺寸、轴心在几何中心** | `Docs/02_TECHNICAL_CONTRACTS.md` §2；`PickupFieldHost.cs` 的 `propBounds` 注释 | 坠落只动 `position.y`，不碰 scale；`propBounds` 必须跟着重算 |
| 道具只有三态：`StateFree=0` / `StateAttached=1` / `StateHidden=2` | `Runtime/Pickup/PickupFieldHost.cs:96-98` | **没有** `falling` / `settled`；这两个状态属于新的行为宿主，不塞进 host |
| 吸附时 `AttachProp` 保持**当前**世界位姿、改成球局部、禁用道具 `Collider` | `PickupFieldHost.cs:976`（`TryWorldPoseToLocal`）、`:1028`（写 `StateAttached`） | 会动的道具必须在 attach **之前**停住，否则会带着动画进到球上 |
| 吸收门只认体积：`V = k·a·b·c`，整 mm 比较 `floor(1000·D) ≥ Req_mm` | `PickupAbsorbMath.TryComputePickupReqDiameterMm`、`IsBeyondAbsorbLimit`（口径见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)） | 坠落只改 y、不改尺寸 ⇒ `Req_mm` 不变；能不能吸只看体积，不看位置 |
| 一件道具要么是**阻挡盒**、要么是**支撑面**，由一个分类器二选一 | `PickupFieldHost.cs:1305`（`UpdatePropObstacles`）、`:198`（`blockingDisabled`） | 坠落中的件既不该挡路也不该当台阶 → 从两个集合里都摘掉 |
| 阻挡和支撑各只有 6 个槽（电机侧），host 侧缓冲 8 | `PickupFieldHost.cs:129-134`（"six boxes … six tops"、`BlockingSelectionCapacity = 8`）、`KatamariBallMotor.cs:365` | 球附近**同时**能当阻挡或支撑的件 ≤6；堆叠不能靠"全都注册成支撑" |
| 扫描是定时的，0.05 s 一次，激活半径 1.6 m | `PickupFieldHost.cs` 的 `DefaultScanIntervalSeconds`、`DefaultActivationRadiusM` | 行为宿主沿用同一套节流哲学：定时、定半径 |
| 球本身有重力坠落，`gravityMps2 = 9.81f` 是电机上的序列化字段 | `KatamariBallMotor.cs:127`、`:470`（`isFalling`） | 道具坠落**复用同一个重力数**，不新开第二个"重力"旋钮 |
| 全仓 `Runtime/` 没有任何 `Animator` 引用 | `grep -rln Animator Runtime/` 无结果 | 自循环动画是全新的东西，要单独定约定（§3.2） |
| `support` / `stackedOn` / `parent` / `parentDerived` / `pinned` 已进 `PROP_KEYS`，但只在 `placement_core` 里被读 | `placement_core.py:210`（`PROP_KEYS`）、`:1543`（`prop_parent`）、`:2758`（`_check_parents`） | 校验与几何已经有一份实现，**不要写第二份**：运行时只消费烘焙产物 |
| UdonSharp 无静态字段/结构体/泛型/`List`/属性重载 | `AGENTS.md`「Code and tests」；`Docs/02_TECHNICAL_CONTRACTS.md` §9 | 数据结构必须是**并列数组**，见 §10.9 |

---

## 3. 五种行为 kind 与字段提案

### 3.1 五种 kind

| kind | 原作对应（证据级） | 我们的做法 | Udon 成本 |
|---|---|---|---|
| `static` | 绝大多数道具 `[社区/推断]` | 什么都不做，只占 `propBounds` | 0 |
| `ambient` | 会自己循环动的小件：人挥手、风车转 `[社区]`（本系列到处是"会动的东西"，移动件是独立一类） | `Animator` 循环播；Udon **不参与** | 0（Udon），占骨骼/DC |
| `path` | 沿固定路线走的动物、小车 `[官方]`（新闻稿：猫、车"以前是障碍，变大就能卷进去"） | 烘焙折线 + 定速巡线，写 `transform` | 每件每次 1 次更新 |
| `orbit` | 绕某个点转的东西（篝火旁一圈、旋转托盘）`[推断]` | 圆心 + 半径 + 角速度，写 `transform` | 每件每次 1 次更新 |
| `stacked` | 摞着的石头/箱子 `[官方图鉴文本]`+`[社区]` | 父指针 + 每 tick 复检 + 坠落（**§10.9**） | 只在事件发生后有成本 |

名字比任务书短一档（任务书里的 `ambient_anim` / `path_mover` / `orbiter` 就是这里的 `ambient` / `path` / `orbit`）：这些字符串要写进每一行 JSON，短名更好抄。

**`ring`（一圈摆着的物件）不是 kind**，是作者模式：用 `static` 摆成一圈即可（篝火旁的一圈石头）。只有当这一圈**真的在转**时才是 `orbit`。

### 3.2 字段提案（扁平键，与现有 `placement.json` 风格一致）

现有 prop 行是扁平的（`position` / `aabb` / `support` / `stackedOn`），所以行为也走扁平前缀，不嵌套对象：

| 键 | 类型 | 用于 | 说明 |
|---|---|---|---|
| `behaviour` | string | 全部 | `static` / `ambient` / `path` / `orbit` / `stacked`；缺省 = `static` |
| `behaviour_phase` | float 0..1 | ambient / path / orbit | 共享时钟上的相位。**必填**，否则同款物件会齐步走 |
| `behaviour_period_s` | float | ambient / orbit | 一个循环的秒数 |
| `behaviour_speed_mps` | float | path | 巡线速度 |
| `behaviour_path` | string | path | 折线 id（烘焙成点表） |
| `behaviour_radius_m` | float | orbit | 半径 |
| `behaviour_axis` | string | ambient | `up` / `x` / `z`，缺省 `up` |
| `parent` | string（prop itemId） | stacked | 运行时指针，单值：这件挂在谁下面（一件最多一个父件）。作者可写；工具推导时也写它。`stackedOn` 是这个关系的旧写法，两者同义，同时出现必须一致 |
| `parentDerived` | bool | stacked | 工具标记：`true` = 这条 `parent` 是 `Tools/levelbuild/supports.py` 按几何写的；`--strict-parents` 只对这一类链的几何失配判 error |
| `support_policy` | string | stacked | **提案，未实现**：`fall`（默认）/ `fixed`（永远不动）；`carry`（跟支撑者一起被吸）**不在 v1**，见 §10.3 |
| `wake_radius_m` | float | 全部 | 活跃半径（距离剔除），缺省 12 m；超出即冻结（§7.3）。**不是触发门**：`WaitForPlayer` 的 `triggerRadiusM` 是 `motions[]` 的参数（2.0/3.0），两者互不改 |
| `floating_ok` | bool | 全部 | **提案，未实现**：悬空是设计（气球、挂饰）。缺省 `false`；已实现的同类豁免是 `pinned: true` |

`support_policy` 的取值（`fall` / `fixed` / `carry`）对齐 `Docs/02_TECHNICAL_CONTRACTS.md` §6 的三条出路：「支撑物被卷走时按策略**一起收取、坠落或固定展示**」。它仍是提案，v1 只做坠落。

**为什么只有单父指针**：`parent`（以及旧写法 `stackedOn`）是作者/工具对「这件挂在谁下面」的唯一指针，`placement_core.prop_parent` 是唯一的解析点（`placement_core.py:1543`）。原作也是单父指针（§10.4），运行时只需要这一个数组。多点接触（桌面搭两条腿、金字塔顶横跨两块）由**每 tick 的几何浮空测试**解决，不靠数据里存多支撑者列表。

**已实现 vs 提案**：`parent` / `parentDerived` / `pinned` / `stackedOn` / `support` 已在 `PROP_KEYS`（`placement_core.py:210`）；`behaviour*` 与 `support_policy` 仍是提案，校验器与运行时都还不读。

### 3.3 作者规则（提案；落地时进校验器）

1. `behaviour_phase` 在同一组同款件里必须**各不相同**（否则"人群"会像军阵）。校验：同 def 同 zone 内 `phase` 两两差 ≥ 0.15。
2. `path` 件必须有闭合折线，且折线**不与任何阻挡结构相交**（球会被卡）。校验：折线采样点 vs 阻挡盒。
3. `path` 件的 `propBounds` 按**运动的包络**算，不是按初始姿态算。校验：包络 AABB。
4. `orbit` 件的圆心到半径的范围必须落在同一 zone 内（不允许扫过两个区）。
5. `stacked` 件禁止写 `floating_ok`；写了就是矛盾。
6. 一个 zone 里 `path` + `orbit` 同时活跃的件数 ≤ 12（§7.2 的预算）。

---

## 4. 状态机

```
                 ┌──────── 吸收门通过 & 接触 ────────┐
                 │                                  ▼
  RESTING ──(脚下空了)──► FALLING ──(落到面)──► SETTLED
     │                            │                    │
     │                            │                    └─(脚下又空了)─► FALLING
     └────────(被吸收)────────────┴────(被吸收)──► ABSORBED（终态）
```

| 状态 | 含义 | 允许被吸收 | 当阻挡 | 当支撑面 | 更新频率 |
|---|---|---|---|---|---|
| `RESTING` | 静止在原处（初值） | 是 | 是 | 是 | 0 |
| `FALLING` | 自由落体中 | 是（按**当时**位置） | 否 | 否 | 30 Hz 固定步长 |
| `SETTLED` | 已经落定，位置与 AABB 已更新 | 是 | 是（按新高度） | 是（按新顶面） | 0 |
| `ABSORBED` | 已吸附/已隐藏，终态 | 否 | 否 | 否 | 0 |

要点：

- **`FALLING` 不是障碍**。否则落下来的箱子会挡住球，玩家会觉得「明明能吃的箱子把我卡住了」。同理也不当台阶。
- **`ABSORBED` 是终态**。`PickupFieldHost` 的语义已经是「吸过的不再提供」（`propStates` 只有 Free/Attached/Hidden，没有回退路径），行为宿主不要发明"复活"。
- **`SETTLED` 之后要重算 `absorbDiameterReq`**。一件箱子从 1.2 m 高落到地面，它的 AABB 大小不变（只动 Y），所以 `D_req` 不变；但如果落点让它和另一件**视觉上叠在一起**，`propBounds` 仍是自己的盒子，不受影响。真正要更新的是**支撑面顶面**：电机侧登记的支撑顶面必须换成落定后的高度，否则球会浮在旧高度上。
- 一个件在 `FALLING` 中途被吸走 → `ABSORBED`；它的子件在它开始落时已进复检队列，坠落照常进行。
- **移动件没有额外状态**：`path` / `orbit` 件就是上表这四个状态；被球碰到时只在「被吸收」与「继续挡路」之间二选一（§6 第 5 条）。**不要**为它们新增 `KNOCKED` / `STUNNED` 之类的状态——「先撞飞」已于 2026-11-15 拍板不做。

---

## 5. 堆叠的推导规则（烘焙期与编辑器）

> 本节只保留 v1 仍在用的东西：原作侧能查到的证据（§5.1）与几何推导规则（§5.2）——`Tools/levelbuild/supports.py` 的 `derive_supports`（编辑器 op 与烘焙诊断实际调用的那一份）和运行时浮空测试都按它写。运行期不建图、不计数，v1 的形态见 §10.9。

### 5.1 原作侧查到了什么

| 结论 | 证据级 | 出处 |
|---|---|---|
| 摞着的同款物件，吃掉最下面那件，剩下的会倒进球里 | `[社区]` | 论坛帖里的一句玩家描述（"a giant stack of the same object … you pick up the bottom one … the rest all fall into you"）。原页被 Cloudflare 挡住（HTTP 403），只有搜索索引里的片段，**没有逐字核对全文** |
| 官方图鉴里确实有「叠起来的积木」和「用锤子敲倒积木塔」这种物件，图鉴文本自己写积木会「倒」 | `[官方]`（游戏内文本，经 wiki 转录） | Katamaripedia 的 We ♥ Katamari Playtime 分类条目（玩具积木、锤子、不倒翁） |
| 但同一条文本里，不倒翁积木的「头」被写死成**下面的积木被敲掉也不动** | `[官方]`（同上） | 同上 |
| 移动的人 / 动物 / 车要「先撞飞」才吸得进去 | `[社区]`（2003 年 TGS 试玩报道） | game.watch 的试玩报道。**这条我们不做**（§6 第 5 条） |
| PS2 版把「视野外被撞倒的移动件」**暂停**，Reroll 才改成继续跑 | `[社区]`（速通社区整理的版本差异） | speedrun.com 的 Reroll 变更帖（原页 403，片段来自搜索索引） |
| 专利里关于堆叠/重力/坠落：**没有**。只有「背景技术」提到别的游戏（打碎建筑、碎块掉落），不是本作 | `[专利]` | US7402104B2（我们自己的研究笔记 `Docs/Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md` §3.1 已记「专利对台阶/重力/坠落什么都没说」） |

| 同系列 2D 外传《Korogashi Puzzle Katamari》明确写了「被托着的方块也会落」 | `[官方]`（介绍文字） | 该外传的报道/介绍页；它是 2D 益智，**不是** 3D 正作 |

**检索词**（英/日，都没有权威结果）：`stack` / `pile` / `topple` / `gravity` / `domino`；「積み重なった」「積み上げ」「崩れる」「重力」「物理演算」。手册 OCR 与 wiki 镜像两份本地语料里，这些词在堆叠语义上一次都没命中。

**读法（`[推断]`）**：几条合起来指向「**通用规则 + 每件参数**」而不是「通用刚体物理」。理由：刚体堆不可能让某一层"被敲掉也不动"；而"视野外的移动件暂停"说明移动件本来就是一罐子**被手动 tick 的对象**，不是物理世界的一部分。

**诚实结论**：原作机制**没有公开文档**。我们只能确定"摞着的会倒"和"不是所有都会倒"。下面的设计是**我们的**，不是复原。

### 5.2 推导规则（烘焙期 / 编辑器）

现行实现是 `Tools/levelbuild/supports.py` 的 `derive_supports(doc, ids=None)`（编辑器 op `derive_supports` 与导入期共用的那一份）。它给每件**至多一个**父件，判据只有三条：

1. **间隙**：令 `gap = bottom − top`，要求 `gap ∈ [−0.30·h, +0.50·h]`（容许陷进 0.30·h、浮起 0.50·h；`parent_gap_window`，`h` = 两件世界高度的较小者），且候选顶面**低于**这件自己的顶面。
2. **足迹**：两件的世界 AABB 在 XZ 上有**任意重叠**（`_footprint_overlap`），不设面积或宽度百分比阈值。
3. **取最高**：满足前两条的候选里取 `(top, overlap, itemId)` 最大者；同顶面比重叠面积，再按 id 稳定排序。

最后 `_break_cycles` 把可能出现的环断开（丢最低那一件的边），保证 `parent` 链是森林。没有 prop 父件的件就是根，运行期按地面/结构兜底。

**20% 与「靠」不在现行推导里**：那两条来自计数模型（`support_graph.py`，生产代码无调用），现行 `supports.py` 不读它们；运行时浮空测试要不要面积/宽度阈值，仍是 §10.9 未决第 1 条。

**旋转**：`prop_box` 用旋转感知的世界 AABB（与 `placement_core` 同一份），不需要枚举 8 个角点；本项目的道具没有万向节姿态。

**环**：作者写出的 `parent` 链成环是 **error**（`parent_cycle`，导入拒收）；工具推导自己保证无环。

### 5.3 坠落（简单模拟，不是物理）

- 固定步长 `dt = 1/30 s`，`v -= 9.81·dt`，`y += v·dt`。**不**用 `Time.deltaTime`：抖动的 dt 会让不同客户端落点差几厘米，而固定步长也更好复现。
- 每步重算落点：在**当前**高度以下、水平接触合格、且状态是 `RESTING` / `SETTLED` 的物件里取最高顶面；都没有就是地面。`FALLING` 的物件**不算**落点（它自己在动）。
- 落定条件 `bottom <= 落点 + 2 mm` ⇒ 贴上去、`v=0`、`SETTLED`。
- **落定顺序**：低的那件先落，高的那件在下一步把它的新顶面当作落点。所以"整摞往下坐一层"是自然结果，不需要特意把整摞当整体。
- 单步最大下落量 ≈ 0.21 m（2 m 落差的末速 6.3 m/s ÷ 30 Hz）。所以**薄于 20 cm 的支撑面可能被穿过**——这条要等实测定判据；「这件别掉」用作者字段 `pinned: true` 表达。
- **不做倾斜**：落定时姿态保持不变（只做竖直坠落，落定不倾斜，见 §10.2）。

### 5.4 边界情况（逐条给结论）

| 情况 | 结论 | 理由 |
|---|---|---|
| 一次吃掉底下 2 件（5 块石头） | 上面 3 块陆续落，整摞坐低 2 层 | 每个 tick 复检，一层一层往下坐 |
| 金字塔顶横跨两块底座，吃掉一块 | **不落**（另一块底座仍在脚下） | 复检是几何浮空测试，不是数支撑者 |
| 同上，再吃掉第二块 | 落，落到地面（若右底座不重叠） | 落点按水平接触算 |
| 桌子搭两条腿，吃掉一条 | 不落 | 复检看到另一条腿 |
| 一件同时贴地又压着箱子，吃掉箱子 | 不落 | 复检看到地面 |
| 板子靠着墙（结构） | 永远不落 | 结构不会被吸走 |
| 作者写出的 `parent` 链成环 | error（`parent_cycle`） | 数据坏，导入拒收；工具推导会断环 |
| 父件自己也在落 | 子件已入队，按 tick 复检决定 | 不重复传播 |
| 落定后又压住了别的件 | 不重建、不补边：每 tick 自己算 | v1 没有图，也没有新边 |
| 落定件被再次吸收 | 允许，`D_req` 按它自己的 AABB（不变） | 位置变了、尺寸没变 |
| 整摞被一口气吸掉 | host 的 `SubmitProp` 逐个索引走，行为宿主只收到 N 次 `OnAbsorbed` | 不需要"整摞"的概念 |

### 5.5 校验与编辑器

- **现行校验码**：`parent_missing` / `parent_self` / `parent_cycle` / `parent_conflict` / `parent_derived_orphan` / `parent_not_seated`（另有通用的穿地/越界/互穿）。`FLOATING` / `THIN_LANDING` / `PHASE_CLASH` / `PATH_BLOCKED` 属提案，校验器还不报。
- **校验器与运行期共用同一份几何判据**：校验器报 `parent_not_seated`（warning），运行期每 tick 复检；不要另造一套「浮空」判断，否则会出现「校验器说没事、行为宿主说浮空」。
- **编辑器「推断支撑」**：一个按钮，对选中集合跑一遍 `Tools/levelbuild/supports.py` 的 `derive_supports`，把结果写成每件的 `parent` + `parentDerived`，并把与作者手写 `stackedOn` **不一致**的条目打印出来（不静默改）。这与 `PickupFieldSync` 的"被拒原因都进报告，不静默修"是同一条纪律。
- **不做的**：物理烘焙、落点预计算表（首版用运行时解析式落点就够；契约 §6 说的"预计算合法落点"是**联网**方案，本地单人不需要）。

### 5.6 参考模型与测试

- **v1 的可执行规格**是 `Docs/Design/PickupAbsorb/prop_behaviours/floating_requeue.py`（14 例，`Docs/tests/prop_behaviours/test_floating_requeue.py`）：单父指针 + 每 tick 复检 + 子件坠落，与 §10.9 同构。
- `support_graph.py` 是计数模型（活计数 + CSR + 上限 4）的参考实现：**生产代码无任何 import**（只有 `Docs/tests/prop_behaviours/test_support_graph.py` 的 24 个用例用它），当前保留、是否退役待 Lead 定；这 24 例只钉住这套模型，**不作为运行时实现依据**。
- Udon 侧照抄的是 §10.9 的数组布局与 §5.3 的坠落积分，不是这两个 Python 文件。

---

## 6. 与吸收门的交互

1. **门槛只用体积**（`V = k·a·b·c`，整 mm 比较；实现见 `PickupAbsorbMath.TryComputePickupReqDiameterMm` / `IsBeyondAbsorbLimit`）。与 §5.4 的"落定后尺寸不变"配合：**坠落不改变一件东西能不能被吸**，只改变它在哪。
2. **吸附前必须冻结**。顺序是：`冻结行为 → 停 Animator → AttachProp`。反过来的话，attach 记下的是动画中间帧，之后 Animator 还在写 `transform`，道具会在球上继续挥手/继续转。冻结用"关 Animator 组件、不重置 pose"即可（Unity 关掉 Animator 后 transform 停在最后一次采样）。
3. **路径/绕行件要先摘出驱动列表**。行为宿主的更新循环按索引走，摘件的正确做法是**把该索引置一个"已停"标志**，不是从数组里删（删了索引就和 host 的行号错位）。
4. **移动件在体积门不够时是障碍**。这是既有分工：件被分类成挡路 OBB 或支撑 OBB 二选一（`UpdatePropObstacles`）。行为宿主只负责让**位置**动，不重新分配这个分类；一旦某件开始 `FALLING`，由 host 的扫描把它从两个集合里摘掉。
5. **移动件被球碰到时只有两种结果**（用户 2026-11-15 拍板：原作的「先撞飞」**不做**）：球进入吸收判定 ⇒ 按普通体积门吃掉；过不了门 ⇒ 当阻挡。没有「被撞飞 / 被撞倒 / 眩晕 / 冷却」这些中间状态——它们要接触力、状态切换、冷却三条新机制，换来的只是一次动画，代价是整整一个状态轴。原作这条做法（`[社区]`，§5.1）我们**不复制**。
6. **`carry` 策略**（**不在 v1**，所有者 2026-11-16，见 §10.3）：支撑者被吸时，承重者**跟着被吸**（契约 §6 的三条出路之一）。前提是所有子件都过体积门，且**不能重复计收益**——这是契约里已经写死的红线，实现时要按索引一次性提交，不能让每件各算一次价值。

---

## 7. 更新预算与节流

### 7.1 三类成本

| 类 | 每件每次成本 | 谁付 |
|---|---|---|
| `Animator` 自循环 | **Udon 0** | 引擎（骨骼 + Draw Call + 蒙皮） |
| 写 `transform`（path / orbit） | 1 次 Udon 更新 | Udon |
| 坠落积分 | 1 次/步，落定即停 | Udon |

按 `Docs/Design/PickupAbsorb/prop_behaviours/budget.py`（0.02 ms/次、90 Hz、25% 预算 = 2.78 ms/帧）：

- 一帧可做 **138** 次单次更新；
- 路径件 15 Hz ⇒ `stride = 6` ⇒ 每帧平均 1/6 的件被更新 ⇒ **约 830 件**才吃满 2.78 ms；
- 绕行件 30 Hz、坠落 30 Hz ⇒ 每帧 1/3 ⇒ **约 415 件**。

**所以 Udon 侧的数量上限远高于"内容需要"**。真正的上限是：

- `Animator`：**不再设数量上限**（owner 2026-11-16：参考关卡是 2018 年的 REROLL 复刻、PS2 时代原作，性能要求本身极低，现阶段不预先优化；见 `LEVEL_FORMAT_V2` D-11）。参考关卡按原作就有 **153 个被动画实例**；距离剔除（§7.3）照做，因为它同时省 Draw Call 与蒙皮，不只是省组件。
- Draw Call：每件独立渲染器；不合并网格（`Docs/03_MAPS_ASSETS.md` §8 明确"不要把吸收物全合并"）。
- 阻挡/支撑容量：一个区域同时 ≤ 6（电机侧）+ 8（host 侧），这才是**离球近的地方**真正的稀缺资源。

### 7.2 每区域默认预算

| 参数 | 值 | 依据 |
|---|---|---|
| 同区域 `path` + `orbit` 活跃件 | ≤ 12 | 12 件 15 Hz = 每帧 2 次更新，远离 138 的预算；真正的理由是可读性（球边上超过 12 个动的，玩家看不出哪件能吃） |
| 同区域 `ambient` | ≤ 24 | 可读性 + 大致 DC 预算（作者指南，不是实测上限；Animator 数量已不设上限，见 §7.1） |
| `wake_radius_m` | 12 m（缺省） | 比 host 的激活半径 1.6 m 大得多：动画要在球到达之前就动着，否则玩家会看到"走近才启动"。**这是距离剔除半径，不是 `WaitForPlayer` 的触发门**——触发门是 `motions[].params.triggerRadiusM`（2.0/3.0，按 `sizeGroup`），2026-11-16 lead 拍板两概念分离 |
| 扫描间隔 | 0.2 s | 比 host 的 0.05 s 粗 4 倍，行为不需要那么细 |
| 坠落步长 | 1/30 s | §5.3 |

### 7.3 距离剔除与滞后

- 进：`dist ≤ wake_radius`；出：`dist > wake_radius + 3`（滞回 3 m，避免边界抖动）。
- 出了范围：`path`/`orbit` **冻结**（记相位，不重置），`Animator` **关组件**（不是 `SetActive(false)`——关整个对象会把阴影和碰撞一起处理掉，语义太重）。
- 视觉上看不出差别，因为球在 12 m 外时这些件本来就只是几个像素。12 m 的依据是我们自己的观感需求，不假装是还原（参考实现里的同类暂停是渲染优化，不是游戏逻辑，见 `ORIGINAL_MOTION_PROGRAM` §14.2）。

### 7.4 分帧（stagger）

- path 件按 `index % 6` 分 6 组，每组在不同帧更新，所以逻辑上 15 Hz、不需要每帧遍历。
- orbit 件 `index % 3` 分 3 组。
- 坠落件**不分帧**（同时落的一摞要在同一帧开始，否则看起来像"一个一个掉"）。

### 7.5 不要做的事

- 不要给每件挂一个 `UdonBehaviour` 自己 `Update()`（`Docs/02_TECHNICAL_CONTRACTS.md` §9 明确禁止"每小物件独立 Udon Update"）。
- 不要用 `Rigidbody` 做堆叠：Udon 侧 `FixedUpdate` 可能一帧跑 28–300 次（`Docs/Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md`），物理步和渲染帧完全脱钩，落点也不可复现。
- 不要在运行时读 JSON / 用编辑器 API（Udon 硬限制）。

---

## 8. 网络与本地

首版是**单人剧情**，所以：

- 行为状态**不同步**（`BehaviourSyncMode.None`），坠落本地算。
- 落点是**确定性**的（固定步长 + 解析式落点），所以以后要做多人，同一份初始数据在不同客户端能算出同一个落点——这正是契约 §6 要的性质，只是现在不需要联网去保证它。
- `[推断]` 单人模式下"客户端算落点"没有公平性问题：没有第二个人会跟你在同一个点上抢同一件东西。
- 如果以后做 PvP：契约 §6 已经给了答案（裁决方定落点，或同步少量重要动态物），我们不需要重新设计，只要保证**落点函数是同一个**。

---

## 9. 行为原型清单（D）

三个主题各给一批起步原型。参数栏的 `phase/period` 是**建议起点**，不是测量值。数量是该主题**首版**的投放目标。
移动件（`path` / `orbit`）被球碰到时只有两种结果：过门吃掉，或挡路——没有中间态（§6 第 5 条）。所以下表的"数量"是**投放量**，不是同屏活动量。

### 9.1 野餐一角（PicnicLawn 西南角，24×24 m）

| 原型 | kind | 参数 | 数量 | 备注 |
|---|---|---|---|---|
| 挥手的人（野餐者） | ambient | period 2.8 s, phase 0.0/0.37/0.71 | 3 | 同一个 def 三份，相位错开 |
| 打盹的人（呼吸起伏） | ambient | period 4.5 s | 2 | 幅度极小，靠骨骼 |
| 转着的风向标 | ambient | axis up, period 2.2 s | 2 | 立在篱笆桩上 |
| 转着的野餐桌遮阳伞 | ambient | axis up, period 18 s | 1 | 慢，几乎看不出 |
| 追蝴蝶的狗 | path | 0.55 m/s，闭合 18 m | 1 | 包络 AABB |
| 蚂蚁队列 | path | 0.12 m/s，闭合 6 m | 2 | 小件，玩家在看球时才发现 |
| 蜜蜂绕花丛 | orbit | r 0.8 m, period 6 s | 3 | 圆心在花丛格心 |
| 篝火旁一圈石头 | static | — | 8 | **环是作者模式**，不是 kind |
| 叠起来的野餐盘 | stacked | 3 层 | 2 摞 | 吃掉底下那只会落 |
| 叠起来的木箱 | stacked | 4 层 | 1 摞 | 玩家第一次见到"落" |
| 挂着的气球 | static + `floating_ok` | — | 4 | 悬空是设计 |

小计：ambient 8、path 3、orbit 3、stacked 13 件、floating 4。

### 9.2 玩具房（ToyRoom，40×40 m）

| 原型 | kind | 参数 | 数量 |
|---|---|---|---|
| 玩具士兵（原地摆臂） | ambient | period 1.6 s | 6 |
| 发条机器人（原地转） | ambient | axis up, period 3.0 s | 4 |
| 旋转木马 | orbit | r 1.2 m, period 12 s | 1（6 匹共享**一个**圆心） |
| 遥控车 | path | 0.9 m/s，闭合 12 m | 2 |
| 弹跳球 | ambient（竖直位移） | period 0.9 s | 4 |
| 积木塔 | stacked | 6 层 | 3 摞 |
| 多米诺骨牌 | stacked | 12 片 | 2 排 |

小计：ambient 14、path 2、orbit 1（6 件）、stacked 42 件。

### 9.3 粪金龟草甸（DungBeetleMeadow，24×24 m）

| 原型 | kind | 参数 | 数量 |
|---|---|---|---|
| 吃草的羊 | ambient | period 5 s（低头/抬头） | 4 |
| 转着的风车（小） | ambient | axis up, period 1.4 s | 5 |
| 推粪球的金龟 | path | 0.3 m/s | 2 |
| 绕灯塔飞的海鸥 | orbit | r 3 m, period 10 s | 4 |
| 石堆 | stacked | 4 层 | 4 摞 |
| 木箱堆 | stacked | 3 层 | 2 摞 |

小计：ambient 9、path 2、orbit 4、stacked 26 件。

### 9.4 三主题合计与预算对照

| 主题 | ambient | path | orbit | stacked | 件数合计 |
|---|---:|---:|---:|---:|---:|
| 野餐一角 | 8 | 3 | 3 | 13 | 27 |
| 玩具房 | 14 | 2 | 6 | 42 | 64 |
| 草甸 | 9 | 2 | 4 | 26 | 41 |

对照 §7.2：单区 `path + orbit ≤ 12` ✓；`ambient ≤ 24` ✓。**三张图都远在预算内**——这说明"行为"这件事的风险不在数量，在**正确性**（冻结、摘驱动、落点）。

---

## 10. 所有者答复（2026-11-16）

本节记录所有者 2026-11-16 答复的现行结论：`kind`/`behaviour` 分列（§10.1）、纯重力坠落与落定不倾斜（§10.2）、`carry` 不在 v1（§10.3）、原作的组只有单父指针（§10.4），以及 v1 的堆叠形态——父件被吃 ⇒ 子件坠落，运行时每 tick 浮空复检（§10.9）。

本节引用的原版数据表都在本机的数据挖掘目录（`~/KatamariRef/meta/`、`/tmp/orig_physics/`），**不入库**（同 `Docs/Design/Parity/PARITY_AUDIT.md` 卷首的约定）。表里的字段名是逐字抄的，计数是实测的；凡是我方解读而非原版明写的，按 §1 标 `[推断]`。

### 10.0 术语表（先对齐词）

| 词 | 一句话 | 例子 |
|---|---|---|
| **坠落** | 托着它的东西没了，它开始往下掉 | 吃掉最下面那个木箱，上面那箱开始掉 |
| **落定** | 掉到某个面上停住的那一刻 | 那箱落到桌面上停住，之后不再动 |
| **支撑 / 支撑者** | 烘焙期推导出的「谁托着谁」关系；一件最多记一个父件（单父指针） | 盘子放在桌上 ⇒ 桌子是盘子的父件 |
| **组 / 组表** | v1 的「组」= 同属一个父件的道具集合；没有现成组表，组由单父指针派生（§10.4） | 一堆蛋里 13 个蛋各指向上一个，就是一条链；一个货架上 30 个弹球都指同一个父件，就是一组的兄弟 |
| **携带** | 支撑者被吃掉时，承重者**跟着一起进账**，而不是掉下来 | 托盘上放着一摞杯子，把托盘吃掉 ⇒ 杯子跟着托盘一起被吞 |

**「支撑者」的严格定义**（与 §5.2 的现行推导一致）：一件道具 A 的父件，是满足下面两条的道具 B ——① B 的顶面落在 `parent_gap_window` 内且低于 A 的顶面；② A、B 的 XZ 足迹有重叠；多条候选取顶面最高者。地面/结构不写指针：没有父件的件就是根。运行时只用几何复检「脚下还有没有东西」，不读这个列表。

### 10.1 原待定 1：`kind` 与 `behaviour` —— **按原版办：分两列**（已定 2026-11-16）

设计原则是「照原版」。下面是把原版表拆开逐列核对的结果：**原版把「是什么」和「怎么动」存在不同的列里，从来没有合成一个枚举；而且「是什么」本身还有两根轴**（内容分类 + 形态类）。

#### 表 1 — 逐实例表 `object dump`（28,698 行、1,438 个类型 id、19 个任务）

路径：`~/KatamariRef/meta/tables/raw_reroll_dump__object_dump.tsv`（逐字表头 17 列，见 `~/KatamariRef/meta/DATAMODEL_NOTES.md:60`、`/tmp/orig_physics/reroll_datamodel.md:66`）。

| 列名（逐字） | 它是什么 | 实测取值与计数 |
|---|---|---|
| `u8monoCategory` | **内容分类**（语义族，吸收白名单那种） | 80 个取值：`0` + `2`–`74` + `76`–`81`；`1` 与 `75` 在这 19 个任务里没出现。`0` = 未填，**15,332 行（53.4%）**。**同一类型 id 的非零分类永远只有一个值（0 个例外）** ⇒ 它是逐类型属性，只是印在实例行上 |
| `u8UnitType` | **形态类**（日文量词类），与内容分类正交 | 16 个取值 `0`–`15`；1,426/1,438 个类型与逐类型表 `name_props_0x30` 的 `0x11` 列相同（12 个类型不一致） |
| `playAnimNo` | 逐实例动画号 | 4 个取值：`-1` 无动画 25,913（90.3%）、`0` 2,257、`1` 525、`5` 3 ⇒ **2,785 条实例（9.7%）带动画** |
| `u8MonoShakeOffFlag` | 能否被「抖」下来 | `1` 467 行（1.63%） |
| `parentProp.monoControlIndex` | **群组父件指针**（原版没有别的父子存法） | 1,970 条子件（6.86%）挂在 1,082 个父件上；单个父件最多 30 个子件；433 条子件与父件坐标完全相同；涉及 308 个不同父件名 / 380 个不同子件名 |
| `u8HitLod` | 命中体 LOD | `0` 8,176 行 / `1` 20,522 行 |

#### 表 2 — 逐类型表 `name_props_0x30`（1,717 行）：**「移动」在这里，不在实例表里**

路径：`~/KatamariRef/meta/tables/raw_reroll_dump__name_props_0x30.tsv`。

| 列名 | 它是什么 | 实测取值与计数 |
|---|---|---|
| `pathSpeedIndex` | 移动件的路径速度档（`[推断]`：档位，不是 m/s；原版没有给单位） | 11 个取值：`2` 是缺省（**1,378 个类型 = 80.2%**），其余 `0`/`1`/`3`–`10` 共 338 个类型，另有 1 个坏值 `t`（`White Butterfly`，解析残留）。**非缺省值几乎全落在会动的形态类上**：`12` 隻（船）8/8、`13` 機（飞机）5/5、`1` 人 131/149（88%）、`2` 匹（动物）61/91（67%）、`3` 台（车）36/121（30%）；而 `5` 本（树）2/163（1.2%）、`9` 島 / `10` 棟 / `15` 箱 都是 0–4%。典型名：`7` = Garbage Truck / Monster Truck / Crane / Bulldozer，`8` = Rocket / Cruise Ship / Airplane / Sperm Whale，`10` = Tornado / Jumboman，`0` = Ant / Snail |
| `num vault pts` | 翻越点个数 | `0` 805 / `2` 261 / `3` 650 / `8` 1 |
| `hasFlatCollisionLeeway` | 平地碰撞余量 | `1` 26 个类型 |
| `isUnhatchedEgg` | 未孵化蛋 | `1` 7 个类型 |
| `crownPickupVisualFX` | 皇冠拾取特效 | `1` 16 个类型 |

#### 两根「是什么」的轴各自有什么标签

- **内容分类 `u8monoCategory` 有 81 个带名字的标签**：`~/KatamariRef/meta/tables/raw_reroll_dump__strings.tsv` 的 `OT_CTG_001`–`OT_CTG_081`（英文列逐字）：`001 Dummy`、`002 Vegetables`、`003 Fruit`、`004 Food`、`005 Snacks`、`006 Japanese Food`、`007 Drinks`、`008 Cooking`、`009 Trash`、`010 Cleaning`、`011 Reading`、`012 Stationary`、`013 Fashion`、`014 Sound`、`015 Sports`、`016 Powerful`、`017 Playtime`、`018 Games`、`019 Containers`、`020 Decorations`、`021 Necessities`、`022 Rain`、`023 Electronics`、`024 Furniture`、`025 Post`、`026 Luggage`、`027 Gardening`、`028 Flowers`、`029 Communication`、`030 Tools`、`031 Summer`、`032 Police`、`033 Science`、`034 Rich`、`035 Lighting`、`036 Hot`、`037 Cold`、`038 Seating`、`039 Weapons`、`040 Danger`、`041 Measuring`、`042 Art`、`043 Control`、`044 Japan`、`045 Festival`、`046 Celebration`、`047 Evil`、`048 Heroes`、`049 School`、`050 Playground`、`051 Energy`、`052 Farming`、`053 Industry`、`054 Symbols`、`055 Houses`、`056 Stores`、`057 Facilities`、`058 Partitions`、`059 Entrances & Exits`、`060 Roadways`、`061 Vending Machines`、`062 Advertising`、`063 Guidance`、`064 Plants`、`065 Nature`、`066 Wheels`、`067 Transport`、`068 Work Vehicles`、`069 Romance`、`070 Children`、`071 Teenagers`、`072 Adults`、`073 Workers`、`074 Professionals`、`075 Cousins`、`076 Cute`、`077 Livestock`、`078 Fierce`、`079 Aquarium`、`080 Wings`、`081 Animals`。
  编号与 `u8monoCategory` 数值**一一对应**，语义抽查全对：`2` Vegetables = CARROT01 / DAIKON01 / TOMATO01，`3` Fruit = BANANA02 / ICHIGO01 / PINEAPPLE，`6` Japanese Food = TAKOBALL / SUSHI，`65` Nature = CLOUD01 / CLOUD04 / RAINBOW，`70` Children = MANGIRL / MANBOY，`79` Aquarium = CRAB / FISH / PENGUIN，`80` Wings = EGG / BIRD，`81` Animals = RAT / BUTTERFLY / SPIDER。
  同一个分类也在逐类型表里以字符串 id 出现：`object_db.categoryStrId`（`~/KatamariRef/meta/tables/raw_reroll_dump__object_db.tsv`，1,718 行里 280 行是空）。
  **注意**：`collectible objects` 那张表也有一列 `Category`，但它和实例表**对不上**（865 条可比的里只有 15 条数值相同，且不是固定位移）——那是导出时的行错位，**不要拿它当分类来源**。
- **形态类 `u8UnitType` 的 16 个值就是日文量词**：`object_db.unitStrId` 指向 `strings` 的 `UI_SYS_057`–`UI_SYS_072`（英文列是空的，日文列才是量词）。逐个对上（类型数）：`0` 個 560、`1` 人 149、`2` 匹 91、`3` 台 121、`4` 羽 18、`5` 本 163、`6` 足 14、`7` 冊 11、`8` 枚 111、`9` 島 37、`10` 棟 94、`11` 町 1、`12` 隻 8、`13` 機 5、`14` 基 21、`15` 箱 22。`[推断]`：原版用它来选量词、选碰撞/摆位的形态模板；**它不含运动语义**（船和飞机也在里面，但它们动不动由 `pathSpeedIndex` 决定）。

#### 原版还有一层我们**没导出**的行为字段（重要缺口）

同一个数据挖掘工具解析原始二进制时，逐实例记录里还有：`s16MoveTypeNo`（+0x04）、`u8LinkActNo`（+0x07）、`u8ExActTypeNo`（+0x08）、`u8LocPosType`（+0x02，固定位 vs 随机位）、`s8TwinsNo`（+0x0E）；逐类型定义里还有 `animated`（+0x1A）、`stayLevel`（+0x0C）、`speedIndex`（+0x13）、`altUpdate`（+0x14）、`dummyParent`（+0x21）、以及碰撞行里的 `category` / `size` / `sortKey`（`/tmp/orig_physics/noclip_bin.ts:1371-1383` 与 `:1057-1068`）。**这些列没有进那 20 张导出表** ⇒ 逐实例的「移动方式」在抽取表里查不到，我们只有逐类型的 `pathSpeedIndex`。这条是缺口，不是「原版没有」。

#### 所以我们的设计怎么落地

1. **保持两列**：`placement.json` 的 `kind`（喂吸收白名单的内容分类）与 `behaviour`（`static`/`ambient`/`path`/`orbit`/`stacked`）**不合并**——原版就是分开的，合并会让 `fruit` 那个过滤轴失去意义（§0.1）。
2. **不新增原版第三轴**：`u8UnitType` 那种形态类我们**不加字段**（`[本项目设计]`）。它只影响量词与碰撞代理口径，对玩法没有影响；真需要时它属于 `collider_proxy` 的属性，不该塞进 `kind`。
3. 原版缺 `playAnimNo` 太可惜（9.7% 的实例带动画，正好喂 `ambient`），但**现在不搬**：搬它属于行为字段落地的事（`PARITY_AUDIT.md` 的「移动件/行为字段」行）。

### 10.2 原待定 2：坠落 = 纯重力落到正下方最高的面，**落定不倾斜**（已定 2026-11-16）

- **是什么**：每步按 `v -= 9.81·dt`、`y += v·dt` 竖直下落；落点取「当前高度以下、水平接触合格、状态为 RESTING/SETTLED 的件里最高的顶面」，都没有就是地面（§5.3 的前 5 条，一字不改）。**落定就是把姿态照原样留下**，不改 yaw、不加倾斜。
- **代价**：几乎为零。不倾斜只影响观感，不改变落点、吸收门或任何数据形状；v1 参考模型 `floating_requeue.py` 也不改姿态 ⇒ **参考模型不需要改**。
- **以后想要再加**：按 `itemId` 哈希 ±8° 即可，纯表现层，随时可加，不需要重新推导。

### 10.3 原待定 3：`carry` **不在 v1**（已定 2026-11-16）+ 它到底是什么

**大白话**：`carry`（携带）说的是「托着别人的那件被吃掉时，被托着的那几件**跟着一起被吃掉**，而不是掉下来」。例：托盘上放着一摞杯子，球把托盘吸进去——有 `carry` 的话，杯子算作「托盘的一部分」一起进账；没有 `carry`（也就是 `fall`）的话，托盘一消失，杯子就开始往下掉，球得再滚过去把它们一件件吃掉。另一种更极端的例子是挂着灯的灯架：吃掉灯架，灯跟着消失，还是灯掉在地上。这是**玩法选择**，不是物理正确性问题。

**决定**：v1 **不做** `carry`；`support_policy` 只是提案字段，v1 的行为固定为「父件被吃 ⇒ 子件坠落」。
- 理由：它牵扯「所有子件都要过体积门 + 不能重复计收益」，是独立一轮的工作量（§6 第 6 条、`Docs/02_TECHNICAL_CONTRACTS.md` §6 的三条出路之一）。
- **不要静默降级**（`[本项目设计]`）：作者在数据里写 `carry` 时，校验器要报一条 `UNSUPPORTED_POLICY`，而不是悄悄当成 `fall`——静默降级会让作者以为「我开了 carry」而实际没有，这是本仓库反复踩过的坑（§0.6 的同一类问题）。
- 键位与词义**保留**，以后要做就是在 v1 行为宿主的吸收路径里加一条传播分支，不需要改数据结构。

### 10.4 原作数据里的「组」：只有单父指针；设计假设「组表 + 每 tick 复检」

**设计假设（不是已证实的原作事实）：组表 + 每 tick 复检，没有支撑者计数、没有上限。** 每件东西属于一个组；组里某件被吃掉时，同组其它件每 tick 自己复检「我下面还有东西吗？有就待着，没有就落」。吃掉 1 → 2 落 → 3 看见 2 还在下面先等着，几帧后 2 落开，3 才落，然后 4、5……能不能证实见本节末「逻辑拿得到吗」。v1 的落地方案是「单父指针 + 每 tick 浮空复检 + 子件坠落」，见 §10.9。

#### 数据侧核对：原作表里到底有什么「组」字段

我把全部 35 张导出表（REROLL Dump 20 张 + 1438 对象表 15 张）逐列扫了一遍（表头里 `group` / `parent` / `stack` / `chain` / `order` / `top` 全打出来看），结论是**只有一条关系，就是「父指针」**：

| 存在的东西 | 形态与计数 | 证据 |
|---|---|---|
| `object dump.parentProp.monoControlIndex` | **每件最多一个父件**（单指针，不是组 id）。1,970 条子件 / 1,082 个父件 / 单个父件最多 30 个子件；涉及 308 个不同父件名、380 个不同子件名 | `~/KatamariRef/meta/tables/raw_reroll_dump__object_dump.tsv`；`~/KatamariRef/meta/DATAMODEL_NOTES.md:60` |
| **链（塔）确实存在** | 305 条子件自己也是父件；最深 **13 层**：Make Cygnus 有一摞 13 个蛋（`EGG07_B`×10 + `EGG06_B` + `EGG03_B` + `EGG05_B`），x/z 几乎相同、每层高 ~7.4 cm、总高 0.89 m —— 就是「一堆蛋」 | 同上，用 `parentProp` 反查链 + `position` |
| 几何方向 | 子件在父件**上方**的占 75.1%（有坐标的 1,259 对里 946 对），中位高出 5.6 cm、水平偏 12 cm；433 条与父件坐标完全相同（附件式） | 同上，`position` |
| 二进制里另有一张**独立的父列表** | 按 (关卡, 区域) 存的一串 int16 对，导出表没有它，是解析器直接读的 | `/tmp/orig_physics/noclip_bin.ts:1073-1090`（`getParentList`） |
| 原作**游戏内证据**：子件跟着父件走、并且一起进账 | 「group objects… picking up the **parent** object automatically gives you the size from the **child** objects that are **moving with the parent**」 | `/tmp/orig_physics/doc.txt` 第 4 条（离群点解释，即 `raw_growth_mechanism.txt`） |
| **不存在**的字段（逐条确认） | 没有组 id；没有「组内序号」；没有 stack / tower / pile 这种记录类型（名字里只有 `TOWER01_G` / `DENTOWER_G` / `DENTOWER02_G` 三个塔类建筑，与堆叠无关）；没有逐件的「我在谁上面」以外的引用；没有逐件状态字段 | 35 张表表头 + 全表名检索 |
| 同名但**无关**的「group」 | `random_groups`（随机刷新组）、`cygnus.random group`（同上）、`s8MonoCommentGroupNo`（图鉴批注分组）、`missions.MONO_GROUP` / `PROP_CATCH_A/B`（`GAME_Mxx_CATCH_A/B_nn`，是单人与双人场景名，不是道具组） | 各表表头；`raw_reroll_dump__missions.tsv` 实测 |
| 社区表里的「stack / on top of」 | 只有 **40 行自由文本**（地点描述，如 "in a stack in the corner"、"on top of the marmalade"），散在 `location` 列里，不是字段 | `raw_reroll_dump__objects_1438__items_by_location.tsv` 的 `location` 列 |

**数据对该假设的支持与不支持（分开说）**：
- **支持**：原作数据里确实有「一组东西挂在一个父件下」的关系（1,082 组），而且能表达多层的塔（13 层蛋）；`doc.txt` 第 4 条还说子件**跟着父件动、父件被吃时子件一起进账**。用「组 + 每 tick 复检」来跑这件事，和数据的形状是吻合的，也不需要我们在烘焙期猜一个上限。
- **不支持的部分**：`parentProp` 是**单父指针**，不是「组 id + 组内成员表」；如果实现成「同组表」，这张表本身得我们从父指针**派生**（按 parent 分组），原版导出表里并没有现成的一张「组表」。
- **数据回答不了的部分**：「每 tick 复检」是运行时逻辑，导出表里没有任何逐帧/状态字段能证实或否证它（表里连状态字段都没有）。

#### 原作的**逻辑**拿得到吗：拿不到，只有数据

- 物理与吸附在**原生 DLL** `PS2KatamariSimulation.dll` 里，托管侧只有外壳；公开 mod（KatamariMods / RerollPatcher）只能挂钩 `PS2KatamariSimulation.GetPropMatrixFromSimulation` 和 `AttachableProp` **读矩阵**，不重写逻辑（`/tmp/orig_physics/prop.cs`；`/tmp/orig_physics/reroll_datamodel.md` §1.2）。
- 速通用的 ASL 只读 `levelID / frames / size / stage` 四个值，没有任何物件状态（`/tmp/orig_physics/reroll.asl:1-12`）。
- 调研没有反编译游戏 DLL（`/tmp/orig_physics/reroll_datamodel.md` 卷首与 §6「仍然不可见」）。
- `noclip_bin.ts` 是**渲染用**的解析器（自带 "until we know better"、"I don't understand yet" 这类注释），它证明的是数据布局，不是玩法逻辑。
- **结论**：**能拿到的是数据（父指针、链、坐标、体积、速度档），拿不到的是逻辑（谁在什么时候判「我下面还有东西」）**。所以该假设只能作为**设计依据**成立，不能写成「原版就是这样」。

### 10.7 参考模型与实现的关系

- `support_graph.py`：计数模型的参考实现，**生产代码无 import**（仅自身测试），保留待定；编辑器 op 与烘焙诊断用的是 `Tools/levelbuild/supports.py` 的 `derive_supports`（独立实现，不依赖本文件）。
- `floating_requeue.py`：v1（父指针 + 每 tick 复检 + 子件坠落）的可执行规格，14 例在 `Docs/tests/prop_behaviours/test_floating_requeue.py`。
- C# 运行时照 §10.9 的数组形状落地；两个 Python 文件都不进运行时。

### 10.8 仍然开着的口子

- **行为字段（`playAnimNo` / `pathSpeedIndex` 搬进 `behaviour`）** — 属行为字段落地那一轮（`PARITY_AUDIT.md`「移动件/行为字段」行）。
- 原作的**逻辑**仍未公开（P18），所以 v1 的堆叠规则是我们自己的设计，不写成「原版就是这样」；数据侧只能证明「有父指针、有链」。

### 10.10 现行实现（已写，未在 Unity 验证）

- **文件**：`Runtime/Pickup/PropFallMath.cs`（纯静态：父列清洗与断环、CSR 子表、入队与复检一步、坠落积分、支撑几何、XZ 网格）；`PropFallHost.cs`（行为宿主：状态数组、读 Transform、竖直移动）；`TerrainFieldHost.SampleSupportHeight`（地形高度查询）；`PickupFieldHost` 只加了父索引列、四个只读访问器和三个调用点（吸收事件后 `OnPropAbsorbed`、障碍扫描里跳过坠落件、`ResetField` 里 `ResetFalls`）。
- **数据**：`placement` 的 `parent` →（导入器 `BuildDeclaredParents`）→ `PickupFieldSync` 按 itemId 解析成行索引写入 `propParentIndex`；找不到父件的边与环记 note 并置 -1；不传声明时按 id 保留原值（幂等）。运行时再用 `PropFallMath.SanitizeParents` 清洗一次。没有新 JSON 键。
- **行为**：父件被吃 ⇒ 直接子件入队，**下一个 30 Hz tick** 复检（脚下没有 `RESTING/SETTLED` 的道具、也没有地形/兜底地面落在 §5.2 窗口内 ⇒ `FALLING`），孙件在它开始落的同一 tick 入下一轮队列，所以整摞逐层塌下来。坠落是 §5.3 的纯重力，落点用**步前**底面，落地 2 mm 内贴面、`SETTLED`、不倾斜；落定后就是普通道具（障碍扫描读新位置；吸收判定读 `transform.position`）。落地面 = 下方有重叠足迹的 `RESTING/SETTLED` 道具的最高顶面与地形的较高者；地形取足迹中心 + 四角五点的最高值，入队时采一次并缓存；没有地形数据则落到 `floorY`（默认 −100）。
- **坠落期间**：可以被吸（§0 默认口径，未拍板）、不当障碍也不当台阶；被吸时从坠落列表摘掉，它的子件照常复检。
- **两个实现选择（owner 可改）**：① 仍被支撑的子件只复检 `maxRecheckSeconds`（默认 3 s）后退队，而不是永远复检——换来「没有坠落件时宿主完全空闲」，代价是之后才失去的**非父**支撑不再触发它落；② 判落用世界 AABB，不看网格。`pinned` 运行时未实现（数据里也没有）。
- **只读状态查询（给运动宿主）**：`PropFallHost.IsPropFalling(index)`、`GetPropFallState(index)`（`PropFallMath.StateResting/Falling/Settled/Absorbed`）、`GetFallingCount()`。约定：`FALLING` 或 `ABSORBED` 的道具不被运动宿主驱动；`SETTLED` 后可恢复（D-14）。
- **接线由导入器负责**（`PlacementImporter.PropFall.cs`，在地形导入之后运行）：文档里至少有一条父边（`parent` 指向同文档的另一件）时，在 `LevelContent/<level>/PropFallHost` 创建组件，`pickupField` 接该关的 `PickupFieldHost`，`terrain` 接 `TerrainRoot` 上的 `TerrainFieldHost`（没有地形则留空），`PickupFieldHost.fallHost` 接回；已有的引用不覆盖。没有父边的关卡不创建宿主，已存在的宿主原样保留。第二次导入不写任何东西（`scene wiring writes` 不增加）。`propParentIndex` 仍由拾取同步写入。`PropFallHost` 的 U# 程序资产由 Unity 生成。

### 10.9 v1 设计：单父指针 + 每 tick 复检 + 子件坠落

> **所有者决定（2026-11-16）：** 父件被吃掉时，它的子件**坠落**（fall），**不**随父件一起被吃——v1 不做 `carry`，子件不算父件的一部分。这是我们对原作的**有记录的有意偏离**（原作游戏内证据 P17 说子件随父件一起进账），理由：`carry` 要「所有子件都过体积门 + 不能重复计收益」两条一起做，是独立一轮的量（§10.3）。

#### 数据：一件一个父指针，没有别的

```
int[] propParent;   // 索引 = host 行索引；-1 = 没有父件。形状照抄原版 object dump.parentProp.monoControlIndex
```

- 一件**最多一个**父件（§10.4 实测：1,970 条子件 / 1,082 个父件 / 单个父件最多 30 个子件 / 链最深 13 层）。
- **「组」不是另一张表**：组 = `propParent` 相同的兄弟 + 它们上面那段链，遍历一次数组就能派生（O(n)，无哈希、无 `List`）。所以实现里的「组表」就是 `propParent` 这一个数组本身（这也回答了 §10.8 第 2 条：**直接复用原版形状**）。
- 因此**不存**：活支撑计数、CSR 反向边、多支撑者列表、落定后的补边。
- 字段分工：`parent`（指针，作者或工具写）/ `stackedOn`（旧写法，与 `parent` 同义）/ `parentDerived`（工具标记）/ `support`（作者声明的结构面）/ `pinned`（作者豁免）都是**数据与校验器**的字段，运行时只读 `parent` 解析出的 `propParent`。这些键已在 `PROP_KEYS`，`placement_core.prop_parent` 是唯一解析点。

#### 每 tick 复检：谁需要看，看什么

触发：某件 i 被吸收（或开始落）⇒ 把 `propParent == i` 的子件放进**待复检队列**。每个定步 tick 对队列里的子件跑一次**浮空测试**：

```
脚下没有东西  ⇒ 落；脚下还有东西 ⇒ 继续等
```

- 队列是定长数组 + `bool[] pendingFlag` 去重，容量 = 件数 n；同一时刻每件最多入队一次。
- 落的条件成立 ⇒ `state = FALLING`、`vy = 0`、出队，并把**它的**子件入队（往上爬一层）。
- 不成立 ⇒ 留着，下个 tick 再看。**不重排、不计数、不建图。**
- 这就是上面描述的那条链：**1 被吃 → 2 脚下空了，落 → 3 看见 2 还在下面，等 → 几 tick 后 2 落开，3 再落**。13 层的塔因此是「一层一层坐下去」，不是「整摞一起落」。
- **幂等**：重复复检不改变结论（该等就继续等），所以不需要任何去重计数。
- **无计数、无上限、不重建**：没有支撑者上限，也没有「落定后记一个新支撑者」这回事。

#### 状态：三个新状态不进 `PickupFieldHost`

| 状态 | 在哪 | 含义 |
|---|---|---|
| `RESTING` | 行为宿主 `propState[]` | 静止在原处（初值） |
| `FALLING` | 同上 | 自由落体中：可被吃，但**不当阻挡、不当支撑面**（§4） |
| `SETTLED` | 同上 | 已落定，位置与顶面已更新：可被吃、可当阻挡/支撑面 |
| `ABSORBED` | 同上（终态） | 已吸附/已隐藏 |

host 的 `StateFree` / `StateAttached` / `StateHidden` **一个都不加**（§2）：两套状态只在「吸收」这一个事件上同步（host 的 `OnAbsorbed` → `propState = ABSORBED`）。`path` / `orbit` 件也不新增状态（§4）。

#### 坠落：照 §5.3 / §10.2，一字不改

固定步长 `v -= 9.81·dt`、`y += v·dt`；落点 = 当前高度以下、水平重叠合格、状态为 `RESTING` / `SETTLED` 的**最高顶面**，都没有就是地面；`bottom ≤ 落点 + 2 mm` ⇒ 贴上去、`v = 0`、`SETTLED`；**不倾斜**。坠落中的件不算落点、不挡路、不当台阶。

#### UdonSharp 形状：全定长数组

```
int[]   propParent;     // -1 = 无父件
int[]   propState;      // 0 RESTING / 1 FALLING / 2 SETTLED / 3 ABSORBED
float[] fallVelocityY;
int[]   pending;        // 待复检队列（定长，容量 n）
bool[]  pendingFlag;    // 去重
int     pendingHead, pendingCount;
int[]   propBehaviour;  // static/ambient/path/orbit/stacked（不变，§3.2）
float[] propPhase;      // ambient/path/orbit 的相位
```

没有 `List` / 字典 / 结构体 / 自定义类（`AGENTS.md`「Code and tests」、`02_TECHNICAL_CONTRACTS.md` §9）。容量论证：每件最多入队一次 ⇒ `pending` 取 n 一定够；`propParent` 与 host 行索引同长。

#### Udon 运行期列：CSR 子表 + 逐件几何列

上面的形状够跑，但有两处会在真实的件数上变慢，把**列**定下来（Udon 实现见 §10.10，这里只定数据）：

1. 「我的子件是谁」在 `propParent` 上只能扫全表（O(n)）。每次吃掉一件都扫一遍 n，30 件的兄弟组连续被吃就是 30n。
2. 每 tick 的浮空测试要读**每个件当前的**底/顶面和水平范围；原版坐标只来自烘焙期，落过、被吃过之后必须有一份运行期的世界 AABB。

```
// 每件一行，行索引 = host 行索引（烘焙期一次定好，运行期不重排）
int[]   propParent;      // n    父件行索引；-1 = 无父件（地面 / 结构 / 顶部件）
int[]   childStart;      // n+1  CSR 前缀和：第 p 件的子件 = childList[childStart[p] .. childStart[p+1])
int[]   childList;       // m    m = 有父件的件数 <= n-1
int[]   propState;       // n    0 RESTING / 1 FALLING / 2 SETTLED / 3 ABSORBED
float[] fallVelocityY;   // n    只在 FALLING 时非 0
float[] propMinX, propMinY, propMinZ, propMaxX, propMaxY, propMaxZ;  // n each，世界 AABB
int[]   pending;         // n    待复检队列（定长环形）
bool[]  pendingFlag;     // n    去重
int     pendingHead, pendingCount;
```

- **CSR 在烘焙期或 `Start` 现算，O(n) 两趟、只用 int 数组**：第一趟数 `childStart[propParent[i] + 1]++`，前缀和，第二趟按行序填 `childList`（子件顺序 = 行序，**稳定**，所以同一份数据每次构建的 `childList` 逐元素相同 —— 幂等的要求）。「吃掉父件 p ⇒ 入队它的子件」于是是一次下标切片，不扫表。
- **`propParent` 的来源是唯一的**：导入器用 `placement_core.prop_parent(prop)` 取（`parent`，没有就退回旧的 `stackedOn`），不读第二份。烘焙期要断环（`supports._break_cycles`）、要保证 `propParent[i] < n` 且 `!= i`；运行期不再校验。
- **六个 float 列就是浮空测试的输入**，也是坠落落点的来源：脚在 `propMinY`，顶面在 `propMaxY`，水平重叠用 `propMinX/MaxX/MinZ/MaxZ`（§5.2 的「任意重叠」口径；阈值问题见下面第 1 条）。`FALLING` 件仍要更新自己六列（它要落给下面看），但**不进别人的落点候选**（§4）。
- **容量**：约 14 个 `n` 长数组（int/float 各半）+ `childList` ≤ n。3,000 件的图 ≈ 4.2 万个数，Udon 堆上放得下；`pending` 取 n 一定够（每件同一时刻最多入队一次）。
- **不进数组的**：`stackedOn` / `support` / `parentDerived` / `pinned` 是数据/工具/豁免字段；组 id 与组内序号也没有。组 = 沿 `propParent` 往上走 + `childStart` 往下取，都能现算，所以「组表」不是第四份数据。
- **导出侧**：`placement.json` 里的 `parent` / `parentDerived` 是数据；CSR 与状态数组都是烘焙产物、不入库。列的顺序和索引即契约，Unity 侧按行号绑定，改名/重排等价于换数据格式。
- **落点要用「步前」的底面比**：`top <= 步前底面` 才是「这一步踩到了它」。一步 1/30 s、初速 0 时只走 ~1 mm，但已经有速度的件一步能跨 10 cm 以上；拿**步后**底面去比 `top <= bottom` 会直接穿过薄桌面（1.8 m 下坠穿过 24 mm 薄板只需 2 步）。参考模型 `floating_requeue._landing_top(i, from_bottom)` 的 `from_bottom` 就是步前底面，与 §5.3 的坠落积分同参数——C# 侧照抄这个参数形状。

#### 校验与 `pinned`：几何是报告，不是门

设计原则：引擎不禁止作者的摆法——`serving_tray_01` 就摆在长凳左边 7.5 cm、`small_potted_plant_03` 离冰柜 88 cm，作者要的就是这个摆法，**不要动它们，也不要让作者改**（owner 2026-11-16）。所以校验器把作者的指针当**报告**而不是几何断言，拆成三条：

1. **作者的指针不是几何断言**。`parent` / `stackedOn` 说「这东西属于那件、被那件带着」，可以完全不接触（挂在杆上的招牌、摆在旁边的托盘）。校验器只把它当**报告**：`levelbuild.py check` 每次都打 `declared parent not seated: N`，`--report` 打逐件表格（哪件、指着谁、差多少、为什么不接触）。代码 `[parent_not_seated]`，默认 **warning**，**任何模式下都不会因它变红**。
2. **只有机器推导的链接受几何约束**。`parentDerived: true` 的边是 `supports.derive_supports` 按几何写进去的；它不再成立 ⇒ 文件过期 ⇒ `--strict-parents` 下是 **error**（并提示重跑 `levelctl.py derive-supports --apply`）。默认模式仍是 warning，因为运行期每 tick 复检，一条错指针的代价只多一次检查。
3. **`pinned: true`**：作者声明「这件自己站着，别让它掉」——装饰、挂着的招牌、云。校验器不再报它「悬空」（陷地、越界、穿结构照旧报）。`pinned` 在 `PROP_KEYS` 里紧跟 `parentDerived`。

**`pinned` 在 v1 运行期里的语义**（参考实现 `prop_behaviours/floating_requeue.py`，14 个用例在 `Docs/tests/prop_behaviours/test_floating_requeue.py`；同目录的 `support_graph.py` 是生产代码无调用的计数模型参考实现，保留待定）：

| 规则 | 后果 |
|---|---|
| 永不入队 | 父件被吃、父件开始落时 `enqueue` 直接跳过它 ⇒ 它**永远进不了 FALLING** |
| 永远是支撑面 | 状态一直是 RESTING / SETTLED，别人照样能放在它上面，它的顶面参与别人的浮空测试 |
| 是传播的终点 | A 上钉着 P、P 上放着 C：吃掉 A，P 不动 ⇒ C 复检时仍看见 P 的顶面 ⇒ C 也不动 |
| 可以同时有 `parent` | 「挂在横梁上的招牌」：指针说明它属于谁（记账、被吃时一起处理），`pinned` 说明梁没了也别掉；两者同时存在时 `pinned` 只管「别掉」 |
| 被吃掉时孩子照落 | `pinned` 只保护自己；`absorb(p)` 仍然把 p 的子件入队 |

「pinned 挡住传播」与「pinned 被吃时孩子照落」不矛盾：前者是**几何事件**（脚下没了），后者是**显式事件**（东西被拿走了）。

三种「不掉」的来源各管一段，互不重叠：`support: <结构 id>` = 站在不会被吸走的东西上；`parent`（作者的）=「我属于它」，不保证接触、运行期自己复检；`pinned` =「我自己站着」，运行期**根本不问**。`pinned` 不会让坏指针变成好数据：存在性、自环、环、两个字段互相矛盾仍然是 error。

**现状**：`story_01_bench_size`（130 件 / 8 条作者指针）在默认模式 `PASS (0 error(s), 2 warning(s))`，两条 warning 就是上面那两件（`serving_tray_01` 足迹不重叠、`small_potted_plant_03` 足迹不重叠）；`--strict-parents` 下同样 0 错（它们是作者写的，不是机推导的）。`--strict`（把**所有** warning 当错）会红成 2 错——这是通用开关，不是父指针的规则。


**支撑指针从哪来（Python 侧）**：`Tools/levelbuild/supports.py` 在导入时按几何推导（旋转感知的世界 AABB、足迹相交、`gap ∈ [-0.30, +0.50] × 较小高度`、取最高顶面、断环），`parentDerived: true` 标记它是机器给的；作者手写的 `parent` 不标。依据 `Docs/Research/KatamariBallRules/STACKS_IN_ORIGINAL.md`：原版 987 个接触里只有 28% 有指针、322 摞里 72% 完全没有指针 —— 所以「谁压着谁」在原版是**几何算出来的**，我们的指针只是把它缓存下来，运行期仍按本节每 tick 复检。

**不要照抄原版的指针**（2026-11-16 实测）：`_ref_cancer` 的 90 条父记录里，只有 5 条子件高出父件 5 cm 以上，50 条子件**低于**父件（螃蟹举着水枪、螃蟹站在旗杆旁边），中位水平距 19.9 cm ≈ 一个身位。原版 `parentProp` 的意思是「跟着它动 / 一起进账」，不是「压在它上面」；把它直接当几何支撑指针会让大多数边指错方向。

**回归样本**：`story_01_bench_size` 的 8 条 `stackedOn` 里，`serving_tray_01`（比 `struct_bench_01` 左 7.5 cm）与 `small_potted_plant_03`（离 `struct_cooler_large_01` 88 cm）水平上完全不压、是悬空的；校验器只查竖直间距所以一直是绿的，推导把它们报成 `orphaned`（dry-run，不改这两个件）。`_ref_*` 关卡被导入器**摊平**了（511 / 648 件的底面都在 y=0.000 m），不能当堆叠的回归样本。

#### 还不定的实现问题（要 owner 或实测拍板，按优先级）

1. **浮空测试的精确形式**：脚下「有没有东西」是 AABB 投影**任意重叠**就够，还是要重叠面积 / 轴向宽度阈值（旧的计数模型用过「20% 较小者」，现行推导不用）？参照用**当前**位置还是上一 tick 的位置（坠落中的支撑者下方一帧后就空了）？哪些状态算「有东西」（`FALLING` 肯定不算，`ABSORBED` 当然不算）？
2. **复检的 tick 频率**：30 Hz（与坠落同频、与 §5.3 一致，13 层链 ≈ 0.43 s 落完）还是 10–15 Hz（省，但链明显变慢、看起来像「一件一件掉」）？
3. **每 tick 预算**：一次复检多少件（`kPerTick`）、超了怎么排（FIFO / 按高度 / 按下落速度）？一个 30 件的兄弟组被一次吃光时会不会在同一帧全排上。
4. **坠落件被球碰到算哪种**：按 §6 第 5 条的既有门（过体积门 ⇒ 吃掉；不过 ⇒ 当阻挡）？但 §4 说 `FALLING` 不当阻挡，两者要合成一条——当前默认口径是**坠落中可被吃、但不挡路**，owner 未拍板。
5. **已实现**：`parent` / `parentDerived` 已进 `PROP_KEYS`；校验器码 `parent_missing` / `parent_self` / `parent_cycle` / `parent_conflict` / `parent_derived_orphan`（warning）/ `parent_not_seated`（几何，warning；`--strict-parents` 下只有 `parentDerived` 的链升 error）。导入期由 `Tools/levelbuild/supports.py` 推导，编辑器 op `derive_supports`（默认 dry-run）。没有多支撑者列表需要校验（v1 一件只有一个 `parent`）。
6. **`floating_ok`（提案键，v1 未实现）与 `pinned`**：前者声明「悬空是设计」，后者声明「永不落」；已实现的只有 `pinned`，两者是否合并、要不要报矛盾待定。

v1 的参考模型是 `floating_requeue.py`（父指针派生组、每 tick 浮空复检）；`support_graph.py` 的计数模型生产代码无调用，不再作为实现依据（是否退役待 Lead 定）。

---

## 11. 证据台账

| 编号 | 陈述 | 级 | 位置 |
|---|---|---|---|
| P1 | 摞着的物件吃掉底下那件会倒 | `[社区]` | 论坛帖片段（页面 403，未核对全文） |
| P2 | 官方图鉴里有「叠积木 / 锤子敲倒积木塔」的物件 | `[官方]` | Katamaripedia, We ♥ Katamari Playtime 分类 |
| P3 | 不倒翁的「头」被写死为"下面被敲掉也不动" | `[官方]` | 同上 |
| P4 | 移动的人/动物/车要先撞飞才能吸 | `[社区]` | game.watch 2003-09-05 试玩报道（**本条不复制**，§6 第 5 条） |
| P5 | PS2 版视野外的被撞倒移动件暂停 | `[社区]` | speedrun.com Reroll 变更帖（403，片段） |
| P6 | 专利不含堆叠/重力/坠落 | `[专利]` | US7402104B2；我们自己的笔记 §3.1 |
| P7 | 堆叠机制**无公开文档** | — | 检索（英/日）没有找到任何开发者说明或拆包数据 |
| P8 | 同系列的另一款游戏**明确实现了支撑规则**：被杂物托着的东西会跟着落 | `[官方]`（游戏介绍/评测） | 《Korogashi Puzzle Katamari》（2009，DSiWare 2D 益智外传）的介绍文字里有「被支撑的方块也会落」这类描述 |
| P9 | 检索词（英/日，均无权威结果） | — | `stack` / `pile` / `topple` / `gravity` / `domino` / 「積み重なった」「積み上げ」「崩れる」「重力」「物理演算」 |

**原版数据表条目（`[数据挖掘]`，本机不入库）**：

| 编号 | 陈述 | 级 | 位置（逐字核对） |
|---|---|---|---|
| P10 | 原版**分列**存「是什么」和「怎么动」：实例表 17 列里 `u8monoCategory` / `u8UnitType` / `playAnimNo` / `u8MonoShakeOffFlag` / `parentProp.monoControlIndex` / `u8HitLod` 是**不同的列**，没有合成枚举 | `[数据挖掘]` | `~/KatamariRef/meta/tables/raw_reroll_dump__object_dump.tsv`（28,698 行）；`~/KatamariRef/meta/DATAMODEL_NOTES.md:60` |
| P11 | 内容分类 = 81 个带名字的标签 `OT_CTG_001..081`（Vegetables / Fruit / … / Animals），编号与 `u8monoCategory` 数值一一对应；`0` = 未填（53.4% 的实例） | `[数据挖掘]` | `raw_reroll_dump__strings.tsv` 的 `OT_CTG_*`；分类计数见 §10.1 |
| P12 | 形态类 `u8UnitType` 的 16 个值就是日文量词（個/人/匹/台/羽/本/足/冊/枚/島/棟/町/隻/機/基/箱），与内容分类正交；1,426/1,438 个类型与 `name_props_0x30` 的 `0x11` 一致 | `[数据挖掘]` + `[推断]` | `raw_reroll_dump__object_db.tsv` 的 `unitStrId` → `strings` 的 `UI_SYS_057..072` |
| P13 | 「移动」在**逐类型**表里：`pathSpeedIndex` 11 个取值、`2` 为缺省（80.2% 的类型）、非缺省值集中在船/飞机/人/动物/车（8/8、5/5、88%、67%、30%），树/岛/楼/箱 0–4% | `[数据挖掘]`；「是速度档」为 `[推断]` | `raw_reroll_dump__name_props_0x30.tsv`；交叉统计见 §10.1 表 2 |
| P14 | 逐实例的 `moveType` / `linkAct` / `exActType` / `locPosType` / `twinsNo` 与逐类型的 `animated` / `stayLevel` / `speedIndex` / `dummyParent` **存在但没进导出表** | `[数据挖掘]`（原始二进制解析） | `/tmp/orig_physics/noclip_bin.ts:1371-1383`（实例记录）、`:1057-1068`（类型定义） |
| P15 | 原版有「父指针」这种组关系：1,970 条子件 / 1,082 个父件 / 最多 30 个兄弟；**链最深 13 层**（Make Cygnus 一摞 13 个蛋，每层 ~7.4 cm）；75.1% 的子件在父件上方 | `[数据挖掘]` | `raw_reroll_dump__object_dump.tsv` 的 `parentProp.monoControlIndex` + `position` |
| P16 | 二进制里另有一张按 (关卡, 区域) 存的**父列表**（int16 对），导出表没有它 | `[数据挖掘]`（解析器源码） | `/tmp/orig_physics/noclip_bin.ts:1073-1090`（`getParentList`） |
| P17 | 原作**游戏内**：父件被吃时，**跟着父件动的子件一起进账**（`group` 物件解释体积高侧离群点） | `[社区]`（速通/数据挖掘者的机制笔记，非官方文档） | `/tmp/orig_physics/doc.txt` 第 4 条（= `raw_growth_mechanism.txt`） |
| P18 | 原作的**逻辑拿不到**：物理/吸附在原生 `PS2KatamariSimulation.dll`；公开 mod 只挂钩读矩阵；ASL 只读 `levelID/frames/size/stage`；未反编译 DLL | — | `/tmp/orig_physics/reroll_datamodel.md` 卷首与 §1.2/§6；`/tmp/orig_physics/prop.cs`；`/tmp/orig_physics/reroll.asl:1-12` |
| P19 | 导出表里**没有**：组 id、组内序号、stack/tower 记录类型、逐件状态字段；同名的 `MONO_GROUP` / `random_groups` / `s8MonoCommentGroupNo` 都不是道具组 | `[数据挖掘]`（逐列表头 + 全表名检索） | 35 张表表头；`raw_reroll_dump__missions.tsv` 的 `MONO_GROUP` = `GAME_Mxx_CATCH_B_01`（单人/双人场景名） |

P15–P17 合起来的意义：**「组」这件事在原版数据里是存在的（父指针 + 链），但「组表」要从父指针派生，而且没有任何字段记录逐件状态**。所以「组表 + 每 tick 复检」假设（§10.4）与数据形状**相容**，只是「每 tick 复检」这一层**没有公开证据**（P18）。

P8 的意义：**「支撑规则」在这个系列里是存在的**，只是做在一款 2D 益智外传里才被写清楚。这支持「原作是规则而不是刚体物理」的推断（`[推断]`），但不能证明 3D 正作用的是同一条规则。

**没做的事**：没有实机、没有拆包、没有拿到任何道具行为表；没有在 Unity 里跑过；没有 profile 过 Udon 成本（0.02 ms 是量级假设）。没有拿到原作的**逻辑**（只有数据表），所以「每 tick 复检」在本文里的身份是**设计假设**，不是原作事实。
