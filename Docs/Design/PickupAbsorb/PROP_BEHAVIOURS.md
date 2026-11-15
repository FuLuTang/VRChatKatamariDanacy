# 道具行为与堆叠：五种 kind、状态机、支撑图

**本文回答四件事（B / C / D 三份交付合并在这里）：**

1. 原作道具的**五种类型**分别是什么，我们怎么把它们变成可序列化的字段、状态机和更新预算；
2. **堆叠问题**：原作在「一次吃掉下面好几件」时，怎么知道上面的东西该开始落？我们要怎么做；
3. 行为库的**起步原型清单**与三个主题的数量预算；
4. 全部结论的**证据级**，以及上一份交付（地图作者规则摘要）的交叉引用。

写成日期 2026-11-16（项目时区）。**没有在 Unity 里运行过任何东西**，也没有对原作做任何测量或拆包。凡涉及我们运行时的都是**代码事实**（`文件:行`）；凡涉及原作的都是公开文字，按 §1 的标签分级；凡是我们自己的设计都标「本项目设计」。

---

## 0. 结论摘要

0.1 **`kind` 不是行为**。`placement.json` 里的 `kind` 是**内容分类**（`fruit` / `tableware` / `food` / `container` / `drink` / `nature` / `furniture` / `garden` / `structure` / `toy` / `debris`，第一关 127 件的实际分布），它只喂给吸收白名单。行为必须是**另一列**，否则「石头」既是 `nature` 又要会掉，语义会打架。

0.2 **原作不用通用物理来做堆叠**。能查到的公开材料里没有任何一处描述堆叠机制；专利 US7402104B2 只讲球的三种模型（显示 / 碰撞 / 滚动），堆叠一个字没有。而官方图鉴文本里有一条**反证**：不倒翁积木的「头」被写死成「下面的积木被敲掉也不动」——如果堆叠是刚体解算，这种部件不可能存在。→ 最省的解释是**每件各自带行为参数 + 一个通用的支撑检查**，这也是本文的设计。

0.3 **「它怎么知道是链在一起的」——它不知道，也不需要知道。** 每件道具只记**自己还有几个活着的支撑者**；吃掉一件，就把它从「支撑者」名单里划掉一次；计数归零的那件才开始落。一次吃掉底下三件，就是划三次名、判一次零。「整摞塌下来」是这个规则的**几何后果**，不是任何一处代码在识别链。

0.4 **我们要做的是「烘焙期推导 + 运行时计数」**，不是物理。运行时只有三样东西：每件一个 `int` 活计数、一个定长工作队列、一次固定步长的坠落积分。没有 `List`、没有字典、没有 `Rigidbody`、没有逐件 `Update`。

0.5 **`support` / `stackedOn` 今天只是校验器的字段**，引擎不读（`Tools/levelbuild/placement_core.py:109` 的 `PROP_KEYS`、`Docs/Design/Tools/LEVEL_AUTHORING.md` §4.2）。所以「上面的东西会掉」这件事**在本仓库里今天完全不存在**，不是配置没填。

0.6 **一个必须同时改的数据陷阱**：`placement_core` 的规范化只保留 `PROP_KEYS` 里的键（`placement_core.py:626`），**新加的 `behaviour*` 字段会被校验器静默忽略、被任何一次 rewrite 静默丢弃**。所以字段提案必须和校验器改动同一批落地，否则作者会以为填了。

0.7 **预算不是瓶颈，Draw Call 才是**。按 0.02 ms/次 Udon 更新、90 Hz 帧里给 25% 算，一帧能做 **138 次**变换更新；路径件按 15 Hz 分摊后，**约 800 个**在跑才吃满这点预算。真正的上限是骨骼/Animator/Draw Call 和不透明物件的可见量，不是 Update 算术。

---

## 1. 边界与证据级

| 标签 | 含义 |
|---|---|
| `[官方]` | 开发商/发行商自述、游戏内文本、官方手册 |
| `[专利]` | 专利原文（只作为机制存在性的证据，不引用原文长句） |
| `[社区]` | wiki / 攻略 / 论坛 / 速通笔记的人工观察 |
| `[推断]` | 本文的推论，**不是**原作事实 |
| `[本项目设计]` | 我们自己的设计，没有原作依据 |

**明确不声称**：本文没有原作实机、没有拆包、没有拿到任何道具行为表。§5.1 的三条公开证据只能支撑「摞着的东西会倒」「不是所有都会倒」「移动件要先撞飞才吸得进」这三句话，**不能**支撑任何数值或实现方式。

---

## 2. 现状事实（能依赖的与不能依赖的）

| 事实 | 证据 | 对本设计的意义 |
|---|---|---|
| 三层分离：视觉网格 / 碰撞体 / 玩法体积 `propBounds`；本项目**不缩放、真实尺寸、轴心在几何中心** | `Docs/02_TECHNICAL_CONTRACTS.md` §2；`PickupFieldHost.cs` 的 `propBounds` 注释 | 坠落只动 `position.y`，不碰 scale；`propBounds` 必须跟着重算 |
| 道具只有三态：`StateFree=0` / `StateAttached=1` / `StateHidden=2` | `Runtime/Pickup/PickupFieldHost.cs:96-98` | **没有** `falling` / `settled`；这两个状态属于新的行为宿主，不塞进 host |
| 吸附时 `AttachProp` 保持**当前**世界位姿、改成球局部、禁用道具 `Collider` | `PickupFieldHost.cs:976`（`TryWorldPoseToLocal`）、`:1028`（写 `StateAttached`） | 会动的道具必须在 attach **之前**停住，否则会带着动画进到球上 |
| 吸收门只认体积：`a·b·c ≤ (π/6)·D³/10` | `Runtime/Pickup/PickupAbsorbMath.cs:350`（`TryComputeAbsorbVolumeLimit`）；`absorbVolumeRatio = 10f`（`KatamariBallMotor.cs:218`） | 等价边长 `a_eq=(abc)^{1/3}`，`D_req = 2.673·a_eq`。坠落后的 AABB 变了，`D_req` 也要变 |
| 一件道具要么是**阻挡盒**、要么是**支撑面**，由一个分类器二选一 | `PickupFieldHost.cs:1305`（`UpdatePropObstacles`）、`:198`（`blockingDisabled`） | 坠落中的件既不该挡路也不该当台阶 → 从两个集合里都摘掉 |
| 阻挡和支撑各只有 6 个槽（电机侧），host 侧缓冲 8 | `PickupFieldHost.cs:129-134`（"six boxes … six tops"、`BlockingSelectionCapacity = 8`）、`KatamariBallMotor.cs:365` | 球附近**同时**能当阻挡或支撑的件 ≤6；堆叠不能靠"全都注册成支撑" |
| 扫描是定时的，0.05 s 一次，激活半径 1.6 m / 释放 2.5 m | `PickupFieldHost.cs:125-127` | 行为宿主沿用同一套节流哲学：定时、定半径、滞回 |
| 球本身有重力坠落，`gravityMps2 = 9.81f` 是电机上的序列化字段 | `KatamariBallMotor.cs:127`、`:470`（`isFalling`） | 道具坠落**复用同一个重力数**，不新开第二个"重力"旋钮 |
| 全仓 `Runtime/` 没有任何 `Animator` 引用 | `grep -rln Animator Runtime/` 无结果 | 自循环动画是全新的东西，要单独定约定（§3.2） |
| `support`（28 行）/ `stackedOn`（6 行）已写进第一关数据，但只在 `placement_core` 里被读 | `placement_core.py:1268`（`_support_surface`）、`:1287`（`_check_support_and_overlap`） | 校验与几何已经有一份实现，**不要写第二份**：运行时只消费烘焙产物 |
| UdonSharp 无静态字段/结构体/泛型/`List`/属性重载 | `AGENTS.md`「Code and tests」；`Docs/02_TECHNICAL_CONTRACTS.md` §9 | 数据结构必须是**并列数组 + CSR**，见 §5.3 |

---

## 3. 五种行为 kind 与字段提案

### 3.1 五种 kind

| kind | 原作对应（证据级） | 我们的做法 | Udon 成本 |
|---|---|---|---|
| `static` | 绝大多数道具 `[社区/推断]` | 什么都不做，只占 `propBounds` | 0 |
| `ambient` | 会自己循环动的小件：人挥手、风车转 `[社区]`（本系列到处是"会动的东西"，移动件是独立一类） | `Animator` 循环播；Udon **不参与** | 0（Udon），占骨骼/DC |
| `path` | 沿固定路线走的动物、小车 `[官方]`（新闻稿：猫、车"以前是障碍，变大就能卷进去"） | 烘焙折线 + 定速巡线，写 `transform` | 每件每次 1 次更新 |
| `orbit` | 绕某个点转的东西（篝火旁一圈、旋转托盘）`[推断]` | 圆心 + 半径 + 角速度，写 `transform` | 每件每次 1 次更新 |
| `stacked` | 摞着的石头/箱子 `[官方图鉴文本]`+`[社区]` | 支撑图 + 坠落（§5） | 只在事件发生后有成本 |

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
| `support_policy` | string | stacked | `fall`（默认）/ `carry`（跟支撑者一起被吸）/ `fixed`（永远不动） |
| `rests_on` | string[] ≤4 | stacked | **作者显式**支撑者；空则由烘焙派生 |
| `wake_radius_m` | float | 全部 | 活跃半径，缺省 12 m；超出即冻结（§7.3） |
| `floating_ok` | bool | 全部 | 悬空是设计（气球、挂饰）。缺省 `false` |

三个 `support_policy` 直接对齐 `Docs/02_TECHNICAL_CONTRACTS.md` §6 已经写下的三条出路：「支撑物被卷走时按策略**一起收取、坠落或固定展示**」。这是一次命名对齐，不是新发明。

**为什么 `rests_on` 是数组而 `stackedOn` 是单值**：`stackedOn` 是作者对「这件放在那件上」的**声明**，单值够用（`placement_core` 也按单支撑者解释，`:1268`）；`rests_on` 是烘焙产物，允许多支撑（桌面搭两条腿、金字塔顶横跨两块），上限 4 是 Udon 定长数组的容量。两者不冲突：作者只写 `stackedOn`，工具把推导结果写成 `rests_on`。

### 3.3 作者规则（会被校验器拦住的）

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
  RESTING ──(所有活支撑归零)──► FALLING ──(落到面)──► SETTLED
     │                            │                    │
     │                            │                    └─(支撑者被吸走)─► FALLING
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
- 一个件在 `FALLING` 中途被吸走 → `ABSORBED`，**不再**传播坠落（它的承重者已经在它开始落的时候失去了它）。

---

## 5. 支撑图：原作怎么知道上面的要落（C）

### 5.1 原作侧查到了什么

| 结论 | 证据级 | 出处 |
|---|---|---|
| 摞着的同款物件，吃掉最下面那件，剩下的会倒进球里 | `[社区]` | 论坛帖里的一句玩家描述（"a giant stack of the same object … you pick up the bottom one … the rest all fall into you"）。原页被 Cloudflare 挡住（HTTP 403），只有搜索索引里的片段，**没有逐字核对全文** |
| 官方图鉴里确实有「叠起来的积木」和「用锤子敲倒积木塔」这种物件，图鉴文本自己写积木会「倒」 | `[官方]`（游戏内文本，经 wiki 转录） | Katamaripedia 的 We ♥ Katamari Playtime 分类条目（玩具积木、锤子、不倒翁） |
| 但同一条文本里，不倒翁积木的「头」被写死成**下面的积木被敲掉也不动** | `[官方]`（同上） | 同上 |
| 移动的人 / 动物 / 车要「先撞飞」才吸得进去 | `[社区]`（2003 年 TGS 试玩报道） | game.watch 的试玩报道 |
| PS2 版把「视野外被撞倒的移动件」**暂停**，Reroll 才改成继续跑 | `[社区]`（速通社区整理的版本差异） | speedrun.com 的 Reroll 变更帖（原页 403，片段来自搜索索引） |
| 专利里关于堆叠/重力/坠落：**没有**。只有「背景技术」提到别的游戏（打碎建筑、碎块掉落），不是本作 | `[专利]` | US7402104B2（我们自己的研究笔记 `Docs/Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md` §3.1 已记「专利对台阶/重力/坠落什么都没说」） |

| 同系列 2D 外传《Korogashi Puzzle Katamari》明确写了「被托着的方块也会落」 | `[官方]`（介绍文字） | 该外传的报道/介绍页；它是 2D 益智，**不是** 3D 正作 |

**检索词**（英/日，都没有权威结果）：`stack` / `pile` / `topple` / `gravity` / `domino`；「積み重なった」「積み上げ」「崩れる」「重力」「物理演算」。手册 OCR 与 wiki 镜像两份本地语料里，这些词在堆叠语义上一次都没命中。

**读法（`[推断]`）**：几条合起来指向「**通用规则 + 每件参数**」而不是「通用刚体物理」。理由：刚体堆不可能让某一层"被敲掉也不动"；而"视野外的移动件暂停"说明移动件本来就是一罐子**被手动 tick 的对象**，不是物理世界的一部分。

**诚实结论**：原作机制**没有公开文档**。我们只能确定"摞着的会倒"和"不是所有都会倒"。下面的设计是**我们的**，不是复原。

### 5.2 答案：它不识别链，只记计数

这是本文最重要的一段，因为提问的方式（"它怎么知道它们是链在一起的"）预设了一个不存在的步骤。

**不需要链的原因：支撑关系是每件道具自己的局部事实。**

- 每件道具有一个整数 `live`：**还有几个"活着"的东西托着它**。地面/结构/墙这类永远不会被吸走的东西，记成 `permanent` 标记，不进这个数。
- 吃掉一件道具 = 从它的每个承重者那里**划掉一次**（`live--`）。
- 某件的 `live` 归零 ⇒ 它开始落。
- 一件开始落 ⇒ 它**自己**也不再是支撑者，于是再对它的承重者划名。

一次吃掉底下三件，就是**划三次名**；中间任何一件归零就立刻入队。整个过程没有任何"哪些东西是一摞"的数据，也没有任何图遍历去找连通分量——**「一摞」是运行结果，不是输入**。

这也解释了为什么原作不需要为每种摞法写脚本：摞法只是**作者摆位置的结果**，运行时只认几何接触。

**「一次吃多件」为什么不需要特殊处理**：吸收是**批**，但传播是**逐条**。批里第 1 件归零的物件立刻落，第 2 件归零的再落，顺序只影响同一帧内的入队次序，不影响终局（`Docs/tests/prop_behaviours/test_support_graph.py` 的 `test_batch_order_does_not_change_the_result` 就是钉这一条）。

### 5.3 数据形状：让 Udon 看见的就是 CSR

Udon 没有 `List`、没有字典、没有 `class`（只有 `UdonSharpBehaviour`）。所以烘焙产物必须是**并列数组**，运行期只读：

```
// 每件道具一个槽（索引 = host 的行索引）
int[]   propBehaviour;        // 0 static / 1 ambient / 2 path / 3 orbit / 4 stacked
int[]   propState;            // RESTING / FALLING / SETTLED / ABSORBED
int[]   liveSupporters;       // 活支撑计数
bool[]  pinnedSupport;        // 有永久支撑（地面/结构）
float[] propPhase;            // ambient/path/orbit 的相位
// 坠落
float[] fallVelocityY;
float[] fallTargetTop;        // 落点顶面（每步重算）

// 支撑图（CSR：行 = 支撑者，列 = 它的承重者）
int[]   dependentStart;       // 长度 n+1，dependentStart[i]..[i+1] 是 i 的承重者
int[]   dependentCount;       // 长度 n，= dependentStart[i+1] - dependentStart[i]
int[]   dependentList;        // 长度 = 边数（= 所有 rests_on 的条数）

// 工作队列（定长，环形也可，容量 2n+8）
int[]   eventKind;            // 0 REMOVE / 1 FALL
int[]   eventIndex;
int     queueHead;
int     queueTail;
```

容量论证：每件在一次传播里最多入队**一次 REMOVE + 一次 FALL**，所以 `2n + 8` 一定够；`Docs/Design/PickupAbsorb/prop_behaviours/support_graph.py` 里就是这么开数组的，超过就抛（宁可炸一次，也不要静默丢事件）。

`support_graph.py` 的 `SupportGraph` 类刻意只暴露 `dependentStart` / `dependentCount` / `dependentList` / `live` 四个字段，C# 侧可以**逐字段照抄**。

### 5.4 推导规则（烘焙期 / 编辑器）

三遍，顺序固定（先按底面高度、再按 `itemId`），所以结果可复现：

1. **接触**：支撑者顶面与承重者底面同高（允许陷进 1 cm、浮起 2 cm——与 `placement_core` 的 `SINK_TOLERANCE_M` / `FLOAT_TOLERANCE_M` 同量级），且接触面**在两个水平轴上都不小于"较小者宽度"的 20%、面积不小于"较小者投影"的 20%**。取顶面最高的那一层；同高的全收（并列支撑）；超过 4 个就按压住面积截断并报 `SUPPORTER_CAP`。
2. **靠**：没有接触、也没贴地时，找侧面贴着（间隙 ≤3 cm）且顶面比自己底面高出 ≥5 cm 的东西。**靠着的支撑者也是普通支撑者**——靠箱子会掉，靠墙不会，差别只在墙是结构、永远不会被吸走。
3. **地面**：底面贴地 ⇒ 永久支撑。

既没接触、也没靠、也没贴地 ⇒ `FLOATING`（浮空）。气球/挂饰用 `floating_ok` 显式声明，不报错（契约 §6 允许"明确悬浮类型可以浮空"）。

**为什么"20% 较小者"而不是"覆盖上位件的 20%"**：桌面搭在两条细腿上时，接触面只占桌面 1%，但那 1% 就是腿的全部顶面；反过来，半个盒子悬在台沿外，接触面占小盒子的 80%，可在轴向只有 5 cm，撑不住。两条都试过，规则取的就是能同时容下这两种极端的形式（`is_support_contact`，两个用例 `TableOnTwoLegsTests` / `RotatedBoxTests.test_a_yawed_box_hanging_off_a_corner_is_reported_as_floating`）。

**旋转**：只支持绕 Y 的 yaw。世界 AABB 有闭式解（`|cos|·hx + |sin|·hz`），不用枚举 8 个角点；`placement_core.world_aabb` 的意图一致但少一次三角函数。本项目的道具没有万向节姿态，这条限制是真实的，不是偷懒。

**环**：第 2 条允许两件一模一样高的东西互相"靠"，所以成环**可能**。环在几何上意味着两件一起飘着或一起倒，运行期无论先处理谁都会自相矛盾 ⇒ 由 `CYCLE` 诊断抓出、**按钉住处理**（不传播坠落），并把作者错误报出来。

### 5.5 传播语义（一次 `absorb` 里发生什么）

```
absorb(ids):
  for id in ids: 入队(REMOVE, id)
  while 队列非空:
    (kind, i) = 出队
    if kind == REMOVE:  REMOVE(i)
    else:               FALL(i)

REMOVE(i):
  if 已 ABSORBED: return
  prev = state[i]; state[i] = ABSORBED
  if prev == FALLING: return        // 它开始落的时候已经划过一次名，不重复
  for d in dependents(i):  LOSE(d)

LOSE(d):
  if state[d] not in (RESTING, SETTLED): return
  if pinned[d]: return              // 还有贴地/结构撑着
  live[d] -= 1
  if live[d] <= 0: 入队(FALL, d)

FALL(i):
  if state[i] not in (RESTING, SETTLED): return
  state[i] = FALLING; fallVelocityY[i] = 0
  for d in dependents(i):  LOSE(d)   // 它自己也不再托人
```

**三个必须写下来的判断**：

1. `if prev == FALLING: return` —— 否则「落一半被吸走」会把承重者划两次名，`live` 变成 -1，之后的计数全错。
2. `LOSE` 里先查 `pinned` 再减 —— 有永久支撑的件，`live` 减到 0 也不落（它本来就靠地面）。这样「地面 + 箱子同时托着一块板」不需要特判。
3. `FALL` 里对承重者划名 —— 这是"整摞一起落"的来源。如果只在 `REMOVE` 里划名，第 2 块开始落时第 3 块会**悬在半空**（它托着的那块已经不在原位，但它没被通知）。这是最容易写错的一处。

### 5.6 坠落（简单模拟，不是物理）

- 固定步长 `dt = 1/30 s`，`v -= 9.81·dt`，`y += v·dt`。**不**用 `Time.deltaTime`：抖动的 dt 会让不同客户端落点差几厘米，而固定步长也更好复现。
- 每步重算落点：在**当前**高度以下、水平接触合格、且状态是 `RESTING` / `SETTLED` 的物件里取最高顶面；都没有就是地面。`FALLING` 的物件**不算**落点（它自己在动）。
- 落定条件 `bottom <= 落点 + 2 mm` ⇒ 贴上去、`v=0`、`SETTLED`。
- **落定顺序**：低的那件先落，高的那件在下一步把它的新顶面当作落点。所以"整摞往下坐一层"是自然结果，不需要特意把整摞当整体。
- 单步最大下落量 ≈ 0.21 m（2 m 落差的末速 6.3 m/s ÷ 30 Hz）。所以**薄于 20 cm 的支撑面可能被穿过**——校验器要拦「坠落路径上跨过薄板」的情形，或者允许作者给该件写 `support_policy: fixed`。
- 落定时可以给一个**确定性小倾斜**（由 `itemId` 哈希出 ±8°），只影响视觉，不影响 AABB 判定。

### 5.7 边界情况（逐条给结论）

| 情况 | 结论 | 理由 |
|---|---|---|
| 一次吃掉底下 2 件（5 块石头） | 上面 3 块全落，整摞坐低 2 层 | §5.5 的 `FALL` 划名 |
| 金字塔顶横跨两块底座，吃掉一块 | **不落**（还剩 1 个活支撑） | 计数语义；物理上会倾斜，我们不模拟倾斜 |
| 同上，再吃掉第二块 | 落，落到地面（若右底座不重叠） | 落点按水平接触算 |
| 桌子搭两条腿，吃掉一条 | 不落 | 同上 |
| 一件同时贴地又压着箱子，吃掉箱子 | 不落 | `pinned` 先判 |
| 板子靠着箱子 | 吃掉箱子 ⇒ 板子落 | 靠 = 普通支撑者 |
| 板子靠着墙（结构） | 永远不落 | 结构不会被吸走 |
| 两件互相靠（环） | 钉住，报 `CYCLE` | 自相矛盾，见 §5.4 |
| 支撑者自己也在落 | 承重者在 `FALL` 时已经划名，一起落 | §5.5 第 3 点 |
| 落定后又压住了别的件 | 别的件不会重新"粘"上去（只按初始几何推导） | 首版：不重建图。要重建就得在运行时跑推导（贵、且不确定） |
| 落定件被再次吸收 | 允许，`D_req` 按它自己的 AABB（不变） | 位置变了、尺寸没变 |
| 整摞被一口气吸掉 | host 的 `SubmitProp` 逐个索引走，行为宿主只收到 N 次 `OnAbsorbed` | 不需要"整摞"的概念 |

### 5.8 校验与编辑器

- **校验码**（新增，故意与现有 16 条规则分开报，避免混淆）：`SUPPORTER_CAP` / `FLOATING` / `CYCLE` / `SINK_TOO_DEEP` / `THIN_LANDING` / `PHASE_CLASH` / `PATH_BLOCKED`。
- **`profile: true` 的语义要固定**：目前"浮空"在 `placement_core` 里是硬错（除非声明 `support`），新规则必须复用同一条判断，不能出现"校验器说浮空、行为宿主说没事"。
- **编辑器「推断支撑」**：一个按钮，对选中集合跑一遍 `derive_supports`，把结果写成每件的 `rests_on`，并把与作者手写 `stackedOn` **不一致**的条目打印出来（不静默改）。这与 `PickupFieldSync` 的"被拒原因都进报告，不静默修"是同一条纪律。
- **不做的**：物理烘焙、落点预计算表（首版用运行时解析式落点就够；契约 §6 说的"预计算合法落点"是**联网**方案，本地单人不需要）。

### 5.9 参考模型与测试

`Docs/Design/PickupAbsorb/prop_behaviours/support_graph.py`：

- `Body`（世界 AABB、yaw 闭式解）、`derive_supports`（三遍规则 + 环检测）、`SupportGraph`（CSR + `live`）、`SupportRuntime`（`absorb` / `step` / `settle_all`）。
- 31 个用例（`Docs/tests/prop_behaviours/`），跑法：`python3 -m unittest discover -s Docs/tests`。逐条对应：

| 用例 | 钉住什么 |
|---|---|
| `StackOfStonesTests`（5 例） | 五块石头叠一摞；一次吃掉底下两块 ⇒ 上面三块一起落，整摞坐低两层；批序无关；吃掉第三块只落两块；坠落中被吸走不炸 |
| `PyramidTests`（4 例） | 并列支撑记成两个支撑者；吃掉一个不落；吃掉第二个落；一批吃两个只落一次 |
| `TableOnTwoLegsTests`（2 例） | 细腿也算支撑（"20% 较小者"规则）；两条腿都没了才落 |
| `RotatedBoxTests`（3 例） | yaw 45° 的闭式 AABB；37° 的盒子坐在箱子上；角落搭边 12.5% 判浮空 |
| `ToleranceTests`（3 例） | 陷 8 mm 算支撑；浮 3 cm 不算；声明为悬浮的装饰不报错 |
| `LeanTests`（3 例） | 靠箱子会掉、靠墙不会、互相靠成环被钉住 |
| `BookkeepingTests`（4 例） | 12 层链的队列不溢出；推导与输入顺序无关；不穿过已落定的件；结构上的道具 |

**这不是运行时**：它是"运行时该长什么样"的可执行规格。C# 侧要照抄的是 §5.3 的数组布局和 §5.5 的分支顺序，不是这个 Python 文件。

---

## 6. 与吸收门的交互

1. **门槛只用体积**（`PickupAbsorbMath.TryComputeAbsorbVolumeLimit`，`PickupAbsorbMath.cs:350`）。`D_req = 2.673·(abc)^{1/3}`，与 §5.7 的"落定后尺寸不变"配合：**坠落不改变一件东西能不能被吸**，只改变它在哪。
2. **吸附前必须冻结**。顺序是：`冻结行为 → 停 Animator → AttachProp`。反过来的话，attach 记下的是动画中间帧，之后 Animator 还在写 `transform`，道具会在球上继续挥手/继续转。冻结用"关 Animator 组件、不重置 pose"即可（Unity 关掉 Animator 后 transform 停在最后一次采样）。
3. **路径/绕行件要先摘出驱动列表**。行为宿主的更新循环按索引走，摘件的正确做法是**把该索引置一个"已停"标志**，不是从数组里删（删了索引就和 host 的行号错位）。
4. **移动件在体积门不够时是障碍**。这是既有的 `BLOCKING_PROPS_PLAN` 分工：件被分类成阻挡盒或支撑面二选一（`UpdatePropObstacles`）。行为宿主只负责让**位置**动，不重新分配这个分类；一旦某件开始 `FALLING`，由 host 的扫描把它从两个集合里摘掉。
5. **原作旁证**：移动件"先撞飞才能吸"（`[社区]`）。**首版不做**撞倒——它需要"接触力 + 状态切换 + 冷却"，是另一套机制。首版只做"移动件按体积门吸，吸不到就挡路"。
6. **`carry` 策略**：支撑者被吸时，承重者**跟着被吸**（契约 §6 的三条出路之一）。前提是所有子件都过体积门，且**不能重复计收益**——这是契约里已经写死的红线，实现时要按索引一次性提交，不能让每件各算一次价值。

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

- `Animator`：每个活着的 Animator 都有骨骼/蒙皮成本。经验（`[推断]`，未实测）一个区域 ≤ 24 个、全图 ≤ 80 个。
- Draw Call：每件独立渲染器；不合并网格（`Docs/03_MAPS_ASSETS.md` §8 明确"不要把吸收物全合并"）。
- 阻挡/支撑容量：一个区域同时 ≤ 6（电机侧）+ 8（host 侧），这才是**离球近的地方**真正的稀缺资源。

### 7.2 每区域默认预算

| 参数 | 值 | 依据 |
|---|---|---|
| 同区域 `path` + `orbit` 活跃件 | ≤ 12 | 12 件 15 Hz = 每帧 2 次更新，远离 138 的预算；真正的理由是可读性（球边上超过 12 个动的，玩家看不出哪件能吃） |
| 同区域 `ambient` | ≤ 24 | 骨骼/DC |
| `wake_radius_m` | 12 m（缺省） | 比 host 的激活半径 1.6 m 大得多：动画要在球到达之前就动着，否则玩家会看到"走近才启动" |
| 扫描间隔 | 0.2 s | 比 host 的 0.05 s 粗 4 倍，行为不需要那么细 |
| 坠落步长 | 1/30 s | §5.6 |

### 7.3 距离剔除与滞后

- 进：`dist ≤ wake_radius`；出：`dist > wake_radius + 3`（滞回 3 m，避免边界抖动）。
- 出了范围：`path`/`orbit` **冻结**（记相位，不重置），`Animator` **关组件**（不是 `SetActive(false)`——关整个对象会把阴影和碰撞一起处理掉，语义太重）。
- 视觉上看不出差别，因为球在 12 m 外时这些件本来就只是几个像素。**原作有同类做法**：PS2 版把视野外被撞倒的移动件暂停（`[社区]`）。

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
- 落点是**确定性**的（固定步长 + 解析式落点 + `itemId` 哈希倾斜），所以以后要做多人，同一份初始数据在不同客户端能算出同一个落点——这正是契约 §6 要的性质，只是现在不需要联网去保证它。
- `[推断]` 单人模式下"客户端算落点"没有公平性问题：没有第二个人会跟你在同一个点上抢同一件东西。
- 如果以后做 PvP：契约 §6 已经给了答案（裁决方定落点，或同步少量重要动态物），我们不需要重新设计，只要保证**落点函数是同一个**。

---

## 9. 行为原型清单（D）

三个主题各给一批起步原型。参数栏的 `phase/period` 是**建议起点**，不是测量值。数量是该主题**首版**的投放目标。

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

## 10. 待拍板（给项目所有者）

1. **`kind` 与 `behaviour` 分两列**（本文建议）还是合成一列枚举？合成会让 `fruit` 失去意义（吸收白名单按它过滤）。
2. **坠落要不要倾斜**？本文建议只做"落地 + 确定性小倾斜"，不做翻滚；翻滚要角速度积分，收益低。
3. **`carry` 策略首版要不要**？本文建议**不要**：它牵扯"全部子件过门 + 不重复计收益"，是独立一轮的工作量。
4. **移动件"先撞飞"要不要**？本文建议首版不做（§6.5）。
5. **`rests_on` 上限 4** 够不够？超过就截断并报 `SUPPORTER_CAP`；如果作者真需要 5 个（比如大桌面搭 4 条腿 + 中线），要改成 6，代价是 host 行数据变宽。
6. **落定后要不要重建图**（让"落下来的箱子正好压住另一件"成立）？本文建议首版不重建，理由见 §5.7 最后几行。

---

## 11. 证据台账

| 编号 | 陈述 | 级 | 位置 |
|---|---|---|---|
| P1 | 摞着的物件吃掉底下那件会倒 | `[社区]` | 论坛帖片段（页面 403，未核对全文） |
| P2 | 官方图鉴里有「叠积木 / 锤子敲倒积木塔」的物件 | `[官方]` | Katamaripedia, We ♥ Katamari Playtime 分类 |
| P3 | 不倒翁的「头」被写死为"下面被敲掉也不动" | `[官方]` | 同上 |
| P4 | 移动的人/动物/车要先撞飞才能吸 | `[社区]` | game.watch 2003-09-05 试玩报道 |
| P5 | PS2 版视野外的被撞倒移动件暂停 | `[社区]` | speedrun.com Reroll 变更帖（403，片段） |
| P6 | 专利不含堆叠/重力/坠落 | `[专利]` | US7402104B2；我们自己的笔记 §3.1 |
| P7 | 堆叠机制**无公开文档** | — | 本轮检索（英/日）没有找到任何开发者说明或拆包数据 |
| P8 | 同系列的另一款游戏**明确实现了支撑规则**：被杂物托着的东西会跟着落 | `[官方]`（游戏介绍/评测） | 《Korogashi Puzzle Katamari》（2009，DSiWare 2D 益智外传）的介绍文字里有「被支撑的方块也会落」这类描述 |
| P9 | 检索词（英/日，均无权威结果） | — | `stack` / `pile` / `topple` / `gravity` / `domino` / 「積み重なった」「積み上げ」「崩れる」「重力」「物理演算」 |

P8 的意义：**「支撑规则」在这个系列里是存在的**，只是做在一款 2D 益智外传里才被写清楚。这支持「原作是规则而不是刚体物理」的推断（`[推断]`），但不能证明 3D 正作用的是同一条规则。

**没做的事**：没有实机、没有拆包、没有拿到任何道具行为表；没有在 Unity 里跑过；没有 profile 过 Udon 成本（0.02 ms 是量级假设）。
