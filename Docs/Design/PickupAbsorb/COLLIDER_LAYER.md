# 道具碰撞体层（COLLIDER LAYER）

> 状态：`PROVISIONAL_NOT_TESTED`。
> 依据：物体建模与碰撞体建模是两层（[OPEN_DECISIONS](../../OPEN_DECISIONS.md) (o)）。
> 引用约定：`Runtime/…`、`Tests/…`、`Labs/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。**本文不给行号**，一律按符号名引用。

## 0. 六条结论（一句话版）

| # | 问题 | 结论 |
|---|---|---|
| 1 | 碰撞体给谁用 | 只给 **(b) 玩家走路**；球挡路（a）走 `propBounds` 的体积门、不读碰撞体；吸附物（c）今天完全不参与碰撞，`RadialProxyMath` **没有接线**（§2） |
| 2 | AABB 什么时候够 | 判据不是「体积填充率」，是**空腔净宽 vs 该件释放球径**：`0.30 m ≤ D_gap < D_release` 才必须拆盒（§3） |
| 3 | 形状表达 | 每件 **0..K 个局部盒**，`K ≤ 6`，**有序 = 优先级**；球挡路的道具块共 12 槽（挡路与支撑共用），装不下时**退到 0 号盒**而不是退到「不挡」（§4） |
| 4 | 建在哪、怎么进 Unity | 玩家侧：`PickupFieldSync.EnsurePropCollider` 按视觉 AABB 建 1 个 `BoxCollider`；球侧形状走 `placement.json` 的 `defs[].collider`（COLLIDER_PROXY），不走模型 manifest（§5） |
| 5 | 静态布景 | **同一套表达、另一套预算**：布景走 `LevelArenaHost` 的静态 OBB，地图校验器按「1 structure = 1 collision 盒」计数；**已定：布景只挑主盒，本轮不动地图契约**（§6、§10-4） |
| 6 | 与挡路接线怎么衔接 | **不返工**：电机的 `SetPropObbs` 本来就吃 N 个 OBB，电机零改动；只改宿主「建盒」那一段，且先以「每件 0 盒 = 今天行为」上线（§7） |

**全文最关键的一句**：玩法体积（自身尺寸 `propLocalBounds`）管「**挡不挡**」（与吸收门同一个函数），碰撞盒管「**挡成什么形状**」。两者分开，所以加碰撞盒**永远不会**改变某个球径下能不能吸——这正是 (o) 三层分离要保的性质。

## 1. 三层与本文的位置

| 层 | 数据在哪 | 谁读 | 决定什么 | 精度要求 |
|---|---|---|---|---|
| 视觉网格 | FBX / MeshFilter | 渲染 | 长什么样 | 亚厘米 |
| **碰撞体层（本文）** | 场景 `BoxCollider`（`EnsurePropCollider` 建） | **VRChat 玩家胶囊（PhysX）** | 玩家能不能走过去 | 20–30 cm（绊脚比穿模更糟） |
| 玩法体积 | 场景 `propLocalBounds`（物件自身尺寸）与 `propBounds`（世界包围盒），宿主平行数组 | 自身尺寸 → 吸收门、reach、挡路门；世界包围盒 → 挡路盒形状 | 能不能吸、够不够近、挡不挡 | 1–2 cm（错一点就变成「空气墙」或「白送」） |

三层不混用的依据是 (o)；「挡不挡」只由 `propBounds`（吸收门同一谓词）决定，挡路 OBB 的形状来自 `propLocalBounds`，都不读 `Collider`，本文**不改它**（现行实现见 [COLLIDER_PROXY_RUNTIME](COLLIDER_PROXY_RUNTIME.md) §4.1）。

一件道具「多大」只在 `propBounds` 里声明，**不要**用碰撞盒去反推玩法体积：原作本身就把空腔算进包围盒（[ABSORB_SIZE_RATIO](../../Research/KatamariBallRules/ABSORB_SIZE_RATIO.md) S-AR07 的「包围盒含空腔」），而碰撞盒恰恰要把空腔挖掉。**同一个物件，玩法体积含空腔、碰撞体不含，这不是矛盾，是原作同构。**

```
视觉网格 AABB ──► EnsurePropCollider ──┬──► Unity 场景 BoxCollider ──► 玩家胶囊
        │                                        └──► (将来) 宿主多盒数组 ──► SetPropObbs ──► StepContactV2 ──► 球
        └──► 场景 propBounds / propLocalBounds ──► 吸收门 / reach / 挡路门（挡不挡）
```

## 2. 问题 1：碰撞体给谁用

### 2.1 (a) 球挡路 —— **不用碰撞体，且不能改用碰撞体**

【代码事实】球是纯 kinematic：全仓没有 `Rigidbody` / `CharacterController`，位置只由接触求解写（`KatamariBallMotor.StepContactV2`）。挡路今天由 `PickupFieldHost.UpdatePropObstacles` 用 `propBounds` 判「吸不动」并分挡路/支撑角色，再由 `RefreshPropObbs` 把选中的道具烘成 OBB（`propLocalBounds` 按道具世界旋转，`PropObbBakeMath.TryBakeProp`），经 `motor.SetPropObbs` 进接触求解；选中数据里没有 `BoxCollider`，也不读视觉网格。

所以「球挡路」这个消费者的输入是**玩法体积**，不是碰撞体。为什么不能反过来让它读碰撞体：一旦挡路盒取决于「这件有没有挂 collider」，同一件道具的物理行为就会随场景接线而变，而吸收门永远只看 `propBounds`，两者立刻可以漂移。

**但「挡不挡」与「挡成什么形状」可以分开**：将来若要用多盒细化挡路形状，读的也是**同一份形状数据**（宿主序列化成数组），**不是**运行期去读 Unity 的 `Collider`。见 §4、§7。

### 2.2 (b) 玩家走路 —— **今天唯一的正式消费者**

VRChat 里世界是实心的靠场景 collider，玩家的碰撞胶囊由 VRChat 的玩家控制器管（知识库 `entities/world/scene-components/boxcollider.md`：BoxCollider 是白名单组件、低开销；玩家碰撞「由 VRChat 玩家控制器管理」，社区理解，官方无专页）。玩家大多数时间坐在球后的 `VRCStation` 上（`Runtime/Riding/BallRideStation`），但**站立与走动是存在的**：交互坐下之前、按退出按钮之后都要走路。

现状（解析 `Worlds/StoryMode/StoryMode.unity`）：

| 事实 | 读数 |
|---|---|
| 110 件道具里 `propColliders[i]` 为空 | **61 件**（正好是已换真实网格的 16 个 def） |
| 仍带 `BoxCollider` | **49 件**（Cube 占位的方块，0.24–0.90 m 立方体） |
| 110 件道具的旋转 | **110/110 = identity**（`m_LocalRotation = (0,0,0,1)`），缩放 ≈ 1 |
| 真实网格道具的 `propBounds` | 已经是网格 AABB（例：`napkin_01` = 0.30×0.028×0.30，`watering_can` = 0.26×0.415×0.58） |

**【序列化实测】**30 cm 方块挡在 2.8 cm 餐巾上是空气墙，所以小件不挂碰撞体；但 0.58 m 的洒水壶、0.40 m 的小皮球没有碰撞体就会穿模。本文回答「哪些该留、留多大」。

**宿主的接线**：`propColliders` 只控制 Unity Collider 的启停，吸附/隐藏时禁用，复位时启用；电机的吸附判定和道具接触代理不读它。宿主启动时按行检查：槽位缺失或为 `null`，且道具根或子物体（包括 inactive 子物体）确有 Unity Collider，才汇总警告；没挂 Collider 的道具允许留空，整列都空也合法。此诊断判定缺接，不验证已接引用的归属，也不检查同一行的多 Collider。

**每件一件的局限**：`propColliders` 是 `Collider[propCount]`，一件一个。所以：

- **K = 1 的今天**：直接挂 1 个 `BoxCollider` 到道具物体上（物体 scale=1、rotation=identity、原点就是 AABB 中心，`center`/`size` 可直接用局部盒），接进 `propColliders[i]`，零代码改动。
- **K ≥ 2 的将来**：需要「每件一个碰撞体根 GameObject + 0..K 个 BoxCollider」，宿主改成整根开关（`propCollisionRoots[i].SetActive(false/true)`）。这是 `PickupFieldHost` 里 3 行级别的改动，但**该文件属于挡路接线那位代理**，必须协调，本文只给接口（§7.4）。

### 2.3 (c) 被吸附后粘在球上的物件 —— **不需要碰撞体，而且今天本来就没有**

【代码事实】三条一起看：

1. 球身碰撞半径是 `collisionRadiusWorldM`（球芯的碰撞半径），**不含**吸附物。
2. 吸附物对球的影响走径向代理：`RadialProxyShellMath` 把每件吸附物的 8 个角点合并进 16 根针的支持长度，`StepContactV2` 里 `PrepareNeedleShape` 把针夹到 `[r, r + cap]`，针尖按段扫掠进接触求解（BallMotor README「径向凹凸代理」）。

所以「吸附物改变球的滚动轮廓」已经由球层的径向代理实现，不靠「给道具加碰撞体」：吸附物挂在 `attachmentRoot` 下、跟着球走，它们的 Unity collider 在吸附瞬间被宿主**主动关掉**（`propColliders[i].enabled = false`），所以连玩家都撞不到。这条线属于「吸附物代理」，**不是道具自身的碰撞体**。

### 2.4 一张表收束

| 消费者 | 今天读什么 | 需要多细 | 实现位置 | 碰撞体层参不参与 |
|---|---|---|---|---|
| (a) 球挡路 | `propBounds` 体积门 + `propLocalBounds` 烘成的 OBB | 贴着道具自身的盒 | `PickupFieldHost` → `SetPropObbs` → `StepContactV2` | **不参与**；将来可选「同一份盒数据」喂多盒，但「挡不挡」仍由 `propBounds` 定 |
| (b) 玩家走路 | 场景 `BoxCollider`（PhysX） | 很粗（20–30 cm 足够） | VRChat 玩家胶囊 + 场景 collider | **唯一的正式消费者** |
| (c) 吸附物 | 径向代理（16 针） | 针尖 | `RadialProxyShellMath` + `StepContactV2` | 不参与，也不该参与 |

## 3. 问题 2：AABB 什么时候够、什么时候不够

### 3.1 判据（可算，不用手感）

设：

- `V = a·b·c` = 该件 `propBounds` 的三轴积（玩法体积，**含空腔**）；
- `D_release = (60·k·V/π)^(1/3)`，`k` 缺省 0.70（`PickupAbsorbMath.DefaultVolumeK`）—— 这就是「球长到多大时这件不再挡路」（吸收门 `Req_mm = floor(1000·cbrt(60·V/π))` 的米制反解；毫米取整的差别在 1 mm 内，这里不计）；
- `D_gap` = 该件**可穿越空腔的最小净宽**（桌下净空、腿间距减腿宽、把手内径…），无空腔记 0。

**规则：当且仅当 `0.30 m ≤ D_gap < D_release` 时，必须拆成多盒。**

读法两端：

- `D_gap < 0.30 m`：起手球都钻不进去的空腔，AABB 把它填实没有可观察差别 → **AABB 够**。
- `D_gap ≥ D_release`：能钻过去的球早就把这件吸掉了（不挡路），空腔永远用不上 → **AABB 够**。
- 中间那段：**存在一个球径，它钻得过去、却因为 AABB 被挡住** —— 这就是「桌子底下变成实心墙」，必须拆盒。

`0.30 m` 是当前起手球径（(a) 的 `D`，`OPEN_DECISIONS` (b) 的 0.55 m 是交分门槛，不要混）；起手球径若改，这条界限跟着改。

### 3.2 为什么不按「体积填充率」判

板凳/桌子类的 `网格体积 / AABB 体积` 很低，但**香蕉、盘子、盘子上的饼干更低**（香蕉的 AABB 填充率约 0.37），而它们明明 AABB 够用。原因：AABB 多出来的体积如果是**贴地的角**，球滚不进去（球同时碰地），填不填都一样；只有**能被球整个穿过的通道**才是错的。所以判据必须是「净宽 vs 球径」，不是「填充率 vs 阈值」——**填充率只能当筛查信号，不能当判据**。

### 3.3 16 个已有真实网格 def 的实际读数

`V` / `D_release` 用当前 `propBounds` 算（脚本见 §11），`h` = 最高点（`propBounds.y`）：

| def | 件 | h (m) | V (m³) | D_release (m) | 会挡球吗（D≥0.30） | 可穿越空腔 | 结论 |
|---|---:|---:|---:|---:|---|---|---|
| `seed` | 3 | 0.016 | 0.000014 | 0.057 | 否 | 无 | 不需要 |
| `acorn` | 2 | 0.050 | 0.000076 | 0.101 | 否 | 无 | 不需要 |
| `pebble` | 1 | 0.032 | 0.000084 | 0.104 | 否 | 无 | 不需要 |
| `bottle_cap` | 1 | 0.0145 | 0.000034 | 0.077 | 否 | 无 | 不需要 |
| `paper_scrap` | 1 | 0.012 | 0.000088 | 0.106 | 否 | 无 | 不需要 |
| `strawberry` | 8 | 0.040 | 0.000052 | 0.089 | 否 | 无 | 不需要 |
| `grapes` | 8 | 0.022 | 0.000079 | 0.102 | 否 | 无 | 不需要 |
| `cookie` | 2 | 0.009 | 0.000045 | 0.084 | 否 | 无 | 不需要 |
| `orange` | 6 | 0.077 | 0.000495 | 0.188 | 否 | 无 | 不需要 |
| `apple` | 6 | 0.079 | 0.000492 | 0.187 | 否 | 无 | 不需要 |
| `pear` | 5 | 0.090 | 0.000265 | 0.152 | 否 | 无 | 不需要 |
| `banana` | 4 | 0.032 | 0.000391 | 0.174 | 否 | 无 | 不需要 |
| `napkin` | 5 | 0.028 | 0.002520 | **0.323** | **是**（0.30–0.32 会被挡） | 无（平片） | 不需要（见下） |
| `watering_can` | 2 | 0.415 | 0.062714 | **0.943** | **是** | 无贯通，但壶嘴边有 ~0.3 m 敞开凹口 | **1 盒：壶身（不含壶嘴）** |
| `watermelon` | 3 | 0.220 | 0.015840 | **0.596** | **是** | 无（实心椭球） | **AABB** |
| `toy_ball` | 4 | 0.400 | 0.064000 | **0.949** | **是** | 无（实心球） | **AABB** |

三条必须解释的判决：

1. **16 个 def 里 13 个（52 件）「不需要」不是偷懒，是两条独立理由同时成立**：① 它们 `D_release < 0.30 m`，起手球就能吃掉，**从来不会挡球**；② 它们最高点 < 0.20 m，玩家应当**踩过去**而不是被 4 cm 的草籽绊住——这类空气墙不挂碰撞体。给它们挂碰撞体的唯一效果是把刚修好的 bug 装回去。**已定：碰撞体只给「身体级大件」做（玩家站立时会被绊到的尺寸），本节这张表就是执行结果。**
2. **`napkin` 会挡球，但仍然「不需要」碰撞体**：球挡路不读碰撞体（§2.1），而 2.8 cm 的餐巾对走路的人就是一块布。它在球那侧是**已接受的取舍**：吸不动的低矮件对大球仍是一块实体 OBB。**两个消费者给出不同答案，是三层分离的正常结果，不是不一致。**
3. **`banana` 的 AABB 确实够用（无空腔），但它不需要碰撞体**——「AABB 够不够」和「要不要做」是两个问题。简报拿香蕉当「AABB 够用」的例子是对的；本文的判决是「够用，但不必做」。同类的还有 `plate`（还没模型，计划三轴 0.24×0.24×0.025 = 0.00144 m³，`D_release` 0.268 m——起手球就吃得到，不挡）。

**`watering_can` 为什么不是 `AABB`**：视觉 AABB 是 0.26×0.415×0.58，其中 0.58 那一轴几乎全是壶嘴。整件包一个盒，会在壶嘴**旁边**凭空多出约 0.3 m 的实心区——那正是「空气墙」。壶身单独一个盒（约 0.26×0.30×0.26，具体值等模型脚本量出来）就把这块去掉；壶嘴细（约 5 cm），玩家走过去、球擦过去都不值得为它再花一个盒。

### 3.4 还没建模、但已经知道会撞上这条判据的家具

按 [PROP_SET_PLAN](../StoryMode/PROP_SET_PLAN.md) §3.6/§3.7 的三轴与 `PROP_CATALOG` §2.2 的 `D_release`：

| 件 | 三轴 (m) | V (m³) | D_release (m) | D_gap (m) | 判决 |
|---|---|---:|---:|---:|---|
| `picnic_table` 野餐桌 | 1.50×0.75×0.72 | 0.810 | 2.21 | ≈0.66（桌下净空） | **拆盒**：桌面板 1 + 4 条腿（腿 < 0.10 m，可省；见下） |
| `park_bench` 公园长椅 | 1.80×0.55×0.80 | 0.792 | 2.20 | ≈0.42（座下净空）+ 座板与靠背之间 | **拆盒**：座板 + 靠背 + 2 个腿架 ≈ 4 |
| 椅子（E 档新增） | ~0.50×0.50×0.90 | ~0.225 | ~1.44 | ≈0.42 | **拆盒**：座板 + 靠背 + 4 腿 ≈ 6 |
| 大树（布景） | 树干 Ø0.25，冠底 ~1.8 | — | — | 冠下 1.8 | 2 盒：树干 + 树冠（冠底低于「最大球顶」时才要第二个） |
| 遮阳伞（布景） | 伞面 2.2，杆 Ø0.04 | — | — | 伞下 ~1.9 | 1 盒：杆（伞面在球够不到的高度） |

「腿 < 0.10 m 一律省」是一条**设计取舍**：省掉 4 条腿 → 球能从桌下穿过但会擦到腿（视觉穿模约 6 cm 宽）；不省 → 4 个盒吃掉道具块 12 槽的 1/3，几张桌子同时靠近时必然有几张退化成 1 盒。**第一版选省**，理由见 §4.3 的退化规则。

## 4. 问题 3：形状表达

### 4.1 数据形状

**每件道具 0..K 个轴对齐盒，坐标是道具局部空间（原点 = 网格 AABB 中心 = 场景放置点），单位米，`min <= max`。**

- `K = 0`：没有碰撞盒 → 回退视觉 AABB（今天 = `propBounds`）。
- `K ≥ 1`：盒的**顺序即优先级**，`0` 号是「主盒」：**在大球仍会挡路时它必须挡住**。桌子的 0 号盒是桌面板（不是腿），椅子的 0 号盒是座板。理由：预算不够时退化到 0 号盒，丢的只能是「薄腿/靠背的保真度」，不能丢「这件还挡不挡」。
- 盒必须**包含在视觉 AABB 内**；越界的盒是数据错误（会让挡路盒跑到玩法体积外面，也会让吸收体积低估实体）。

### 4.2 局部盒 → 世界 OBB

接触求解吃的是真 OBB（`motor.SetPropObbs`：中心、三根轴、半长）。宿主现在对每件被选中的道具，把 `propLocalBounds`（道具自身局部轴上的全长）按道具的世界旋转烘成一个 OBB（`PropObbBakeMath.TryBakeProp`），没有局部列的行退回世界 AABB 并计数（`propObbFallbackRows`）。所以道具摆成 45° 时挡路盒也跟着转，不会像世界 AABB 那样胖 41%。

多盒落地时同一条路：每个局部盒（局部空间 min/max）取中心偏移与半长，中心 = 道具位置 + 旋转 × 局部盒中心，三根轴 = 道具世界旋转的三根轴，半长 = 局部盒半长。没有新的碰撞数学，只是烘焙那一段多循环 K 次；0 盒时走现有单盒烘焙。

### 4.3 K 的上限与预算

三个上限，别混：

| 上限 | 值 | 理由 |
|---|---:|---|
| `K_authored`（作者可写几个盒） | **≤ 6** | 一件家具最多「面板 1 + 腿/靠背 5」。超过 6 说明该拆成多个道具（或只做主盒），因为一件就能占去电机道具块 12 槽的一半 |
| `K_blocking`（喂给电机的盒数，每件） | **≤ 6，且全局 12** | 道具块 `PropObbBakeMath.PropObbCapacity = 12`（挡路与支撑共用），本文不动 |
| `K_avatar`（场景里挂几个 `BoxCollider`） | 今天 **≤ 1**（`propColliders` 一件一个）；宿主改成整根开关后 **≤ 6** | §2.2 |

**预算耗尽的退化规则（必须按这个写，否则会退化成「不挡」；已定：全局仍是道具块 12 槽，装不下就退到该件的 0 号主盒）**：

1. 按「球心到盒」的距离升序选盒，**同一件道具的盒按 0..K 顺序**；
2. 某件只要被选中，就**至少收它的 0 号盒**；其余盒能装几个装几个；
3. 全局 12 槽装满即止；**不允许**出现「某件本来该挡、却一个盒都没进」的情况——那等于把这件变成可穿过。

举例：两张野餐桌同时靠近（各 5 盒）→ 近的那张收 5 盒，远的那张收 1 盒（主盒）。远处的桌子仍然挡住大球，只是小球的桌下通行保真度降级。**这比「远的那张整个穿过去」好得多，也是为什么 0 号盒必须是面板而不是腿。**

## 5. 问题 4：在哪里建、怎么进 Unity

**现状（已实现）**：

- **玩家碰撞盒**：`PickupFieldSync.EnsurePropCollider` 给每个没有 `Collider` 的道具根建一个 `BoxCollider`，中心/尺寸取视觉网格的实测局部 AABB（已有 Collider 的件只接不建；第二次导入 `created=0`），接进 `propColliders[i]`。宿主在吸附/隐藏时关掉它，复位时开回。
- **球侧形状**：由 `placement.json` 的 `defs[].collider`（`box/sphere/capsule/convex`，`PlacementImporter` 校验与同步，见 [COLLIDER_PROXY](COLLIDER_PROXY.md)、[COLLIDER_PROXY_RUNTIME](COLLIDER_PROXY_RUNTIME.md)）声明；没有声明的件用 `propLocalBounds` 烘出的 OBB。这条路取代了「模型 manifest 的 `collision.boxes` + Blender `_COL_` 盒」的方案：`exports/*.manifest.json` 里没有 `collision` 键，流水线脚本里也没有 `_COL_` / `omitted_parts` 的实现。
- `valueUnits`/`propVolumeK` 由导入器按声明值一次写入并回读，同步工具不定价。

多盒（凹形家具）目前没有数据入口；要做时的改动清单见 §9。

### 5.1 与 `propBounds` 的关系（三条硬规则）

1. **回退**：没有声明碰撞形状的件，球侧形状 = `propLocalBounds` 烘出的 OBB，玩家侧 = 视觉 AABB 的 `BoxCollider`。这条保证「被判定需要碰撞体、但还没做形状的道具」不会退化成可穿过，**只是不精确**。**注意边界**：§3.3 判为 `不需要` 的 13 个 def / 52 件是按 §10-1 的决定**故意不给玩家碰撞体**（最高点 < 0.20 m 的件玩家踩过去），否则会把空气墙装回去。
2. **不许回写**：碰撞形状**永远不能**改 `propBounds`。一件道具「多大」只在一处声明（(o)）。
3. **不许运行期互读**：宿主不读 `Collider`，Unity 侧不从 `propBounds` 反推碰撞体。

## 6. 问题 5：静态布景

**同一套表达，另一套预算。**

- 表达相同：不可吸的地形/墙/大树/当布景的桌子，同样是「0..K 个盒」，主盒规则一样。
- 预算不同：布景由 `LevelArenaHost` 登记成静态 OBB（容量 `ContactTuning.MaxStaticObbCount` = 256，盒与斜板共用），边界用平面（`MaxPlaneCount` = 8）。地图作者的数据契约是 `Tools/mapbuild/validate_map.py` 的 `MOTOR_MAX_COLLISION_BOXES = 8`，`layout.json` 里每个 `structures[i]` 只能声明**一个** `collision` 值（`"box"`/`"plane"`），校验器按「1 structure = 1 盒」计数；该常量与电机现行容量不一致，属于校验器侧的遗留（见 `LEVEL_AUTHORING.md` 的预算表）。
- 现状读数：`Docs/Design/Maps/PicnicLawn/layout.json` 的 60 个 structure 一个 `collision` 都没声明；真正在用的是关卡场景（StoryMode 4 盒）。

**已定：布景只挑主盒，不拆。** 每件只挂一个盒，靠 `AddPlane` 补大面积面；多盒留给「玩家真的会从底下钻过去」的布景，等真的要拆时再加 `collision_parts` 数组（校验器改成数 parts）。**本轮不动地图契约。**

## 7. 问题 6：和「挡路接线」那位代理怎么衔接而不返工

### 7.1 不变量（衔接的全部前提）

- 「挡不挡」= 吸收门自己的谓词（`PickupAbsorbMath.IsBeyondAbsorbLimit` / `IsBeyondCachedRequirement`，宿主每次扫描用当前球径判），一个字不改。
- 「挡成什么形状」= OBB。今天每件 1 个（`propLocalBounds` 烘出），将来 0..K 个（局部盒，缺省回退）。
- 电机 `SetPropObbs` 本来就接受 N 个 OBB，容量 12（`PropObbBakeMath.PropObbCapacity`）。**电机零改动**。
- `BlockingPropMath` 的纯函数（`TryBoxFromCentreAndExtents` / `TryDistanceToBox` / `SelectNearest` / `SelectNearestAmong`）契约不变，选择按盒行工作。

### 7.2 接口形状（给宿主，不给电机）

宿主新增（序列化，平行数组，与 `propLocalBounds` 同风格）：

| 字段 | 类型 | 语义 |
|---|---|---|
| `propBoxCounts` | `int[propCount]` | 0 = **回退 `propLocalBounds`**（今天的行为）；1..6 = 用下面两个数组里的前 N 个 |
| `propBoxMinima` | `Vector3[propCount * MaxBoxesPerProp]` | 局部空间，固定步长（`row * MaxBoxesPerProp + k`），未用槽位为 0 |
| `propBoxMaxima` | `Vector3[propCount * MaxBoxesPerProp]` | 同上 |

选**固定步长**而不是「计数 + 前缀和」：UdonSharp 里前缀和要一次额外遍历和一处 off-by-one 风险，而场景序列化数据量完全可接受。

选择算法（**只改宿主 `RefreshPropObbs` 里「选择 → 烘焙」那一段**）：

1. 逐件：`propBoxCounts[i] == 0` → 走现有单盒烘焙（`PropObbBakeMath.TryBakeProp`，**现有代码原样**）；否则逐盒把局部 min/max 换成中心偏移与半长，按道具世界位姿烘成 OBB。
2. 候选仍由 `UpdatePropObstacles` 按**件**的中心距剪枝，避免为远处道具做任何盒数学。
3. `SelectNearestAmong` 的 `previousIndices` / 粘性 / 距离按**盒行**工作，容量取电机的 `GetPropObbsCapacity()`；行 → 件 = `row / MaxBoxesPerProp`。
4. 按 §4.3 的退化规则夹到容量，写进 `SetPropObbs`。

**不填数据时行为必须逐位不变**——这是验收判据：`propBoxCounts` 全 0 时，`SetPropObbs` 收到的 OBB 与今天完全相同（`BlockingPropMathTests`、`PropObbBakeMathTests` 全部照过）。所以这条改造可以「先合入、后填数据」，不存在中间态。

### 7.3 分步迁移（每步独立可验、都可回退）

| 步 | 做什么 | 行为变化 | 验收 |
|---|---|---|---|
| **S1（已实现，2026-11-15）** | `PickupFieldSync.EnsurePropCollider`：没有 Collider 的道具根按视觉局部 AABB 建 1 个 `BoxCollider` 并接 `propColliders`，幂等（第二次 `created=0`）；`valueUnits` 由导入器声明、一次写入并回读 | 整关首次导入建盒，球行为不变 | `RadialProxyLocalBoundsTests` 里的两条 Collider 用例（`MissingColliderGetsOneBoxOnThePropRootAndIsReused`、`ExistingChildColliderIsWiredInsteadOfDuplicated`） |
| **S3** | 宿主加 `propBoxCounts` 等三数组（全 0）+ 多盒烘焙 | 无（逐位不变） | `BlockingPropMathTests`、`PropObbBakeMathTests` 全绿；Play Mode 球感不变 |
| **S4** | 家具建模时填 `counts ≥ 1`，先只填 1 盒/件 | 桌下可通行 | 0.3 m 球从桌下过、1.0 m 球被桌面挡 |
| **S5** | 需要时填第 2..6 盒（腿/靠背） | 腿不再穿模 | 12 槽预算下的退化规则可观察（宿主的 OBB 计数读数） |

**S3 之后才允许**谈「球挡路改读多盒」；在那之前，球挡路就是每件一个 OBB，任何人说「加了碰撞体所以球会挡住桌子腿」都是错的。

### 7.4 对宿主的具体要求

1. **保持** `SetPropObbs` 的「整批替换、N 个 OBB、容量 12」语义——多盒方案完全靠它。
2. **K ≥ 2 时**（家具期）把 `propColliders`（`Collider[]`，一件一个）换成 `propCollisionRoots`（`GameObject[]` 或 `Transform[]`，整根开关），否则一件的第二个盒在吸附后仍是实体。
3. **不要**在 `RefreshPropObbs` 里读 `Collider` 或视觉网格——形状数据走宿主自己的平行数组。
4. 道具块 12 槽**先不动**；若实测不足，再按 §10-3 抬。

## 8. 数据结构汇总（一页）

```text
placement.json（每个 def）
  defs[].collider      { type: box|sphere|capsule|convex, ... }   // 可选；缺省 = propLocalBounds 的 OBB

场景（Unity）
  道具物体（scale≈1, rotation=identity, 原点=AABB 中心）
    └─ BoxCollider ×1（视觉 AABB，EnsurePropCollider）  ← 只给玩家
       接进 PickupFieldHost.propColliders[i]

宿主（多盒方案，未实现）
  propBoxCounts[propCount]                 // 0 = 回退 propLocalBounds
  propBoxMinima/Maxima[propCount * 6]      // 局部空间，固定步长

电机
  SetPropObbs(...)                         // 容量 12，整批替换
```

## 9. 若要做多盒（S3–S5）要动的地方

1. 数据入口：`defs[].collider` 目前是单个形状；多盒需要新字段（先走 `Tools/levelbuild/placement_core.py` 的 schema 与校验），或用 COLLIDER_PROXY 的 `convex` 表达。
2. `PickupFieldSync` / `PlacementImporter` 把数据同步成宿主的 `propBox*` 平行数组（§7.2）。
3. `PickupFieldHost.RefreshPropObbs` 里循环多盒烘焙（§7.2 的选择算法）。
4. `propColliders`（一件一个）换成整根开关（§7.4）。

**Unity 侧**：`addCollider: false` 的导入设置保持不变（`Docs/Evidence/AssetImport/README.md` §2），碰撞体是场景里挂的，不是从网格生成的。Cube 占位的件在换网格时必须同一批把它的立方体 `BoxCollider` 换掉，否则会出现「0.9 m 的盒子套 0.3 m 的暖壶」。

## 10. 已定问题

按「什么时候必须定」排序。每一项下面写明了已定结论。

1. **碰撞体到底给哪些道具做？**（影响 13 个 def / 52 件的判决，也决定要不要一次真人走测）

   **已定：选 A ——只给「身体级」大件做（玩家站立时会被绊到的尺寸，本文取最高点 ≥ 0.20 m）。** 理由：52 件小件给碰撞体等于把刚修掉的空气墙装回一批，而球挡路本来就不读碰撞体（§2.1），两个消费者给出不同答案是三层分离的正常结果。

   - A（已定）：只给「身体级」大件做（最高点 ≥ 0.20 m）。成本：玩家能穿过香蕉/餐巾；球仍然会被它们挡住（两个消费者的答案不同）。
   - B（否）：所有道具都做（含 3 cm 餐巾）。成本：空气墙复活、PhysX 与维护成本上升。
   - C（否）：按 kind 白名单（例如只给 `container`/`garden`/`toy` 做）。成本：同一尺寸的物件待遇不同，作者每次要查表。

2. **0.20 m 这个门槛要不要一次真人走测？**（PROVISIONAL，没有 Unity 证据）

   **已定：选 A ——先按 0.20 m 做，主房间/关卡第一次真人走测时记录到 `Docs/Evidence/`。** 理由：门槛只是经验值，没有走测数据就改它只是换一个同样没依据的数；先落地、再用一次走测把它变成有依据的数。

   - A（已定）：按本文 0.20 m 先做，第一次真人走测时顺便走一遍记到 `Docs/Evidence/`。
   - B（否）：改成 0.30 m（更保守，更多空气墙）。
   - C（否）：先不做碰撞体，等骑乘/步行流程定稿再一起做。代价：玩家会穿过 0.4 m 的皮球。

3. **挡路多盒的预算**（家具期才咬人）

   **已定：选 A ——仍是道具块 12 槽，超出时退到该件 0 号主盒。** 理由：退化语义已经写在 §4.3（至少收主盒，绝不让一件变成可穿过），抬常量会改电机与全部 lab 的预算，收益只在两张桌子同时靠近时才看得见。

   - A（已定）：全局仍 12 槽，同一件装不下就退到主盒。
   - B（否）：道具块抬到 24 槽。成本：改电机常量 + 宿主选择缓冲 + 近集窗口变大。
   - C（否）：每件只 1 盒，接受桌腿/靠背穿模。成本：观感最差。

4. **静态布景要不要多盒，以及怎么声明**（只有 `layout.json` + `validate_map.py` 的 owner 能改）

   **已定：选 A ——只挑主盒。** 理由：声明方式（`collision_parts`）还没定，为几个盒去动地图契约与两张图不划算；需要拆的时候再走 B。

   - A（已定）：布景先只挑主盒，不拆。
   - B（否，留作将来）：加 `collision_parts` 数组，校验器按 parts 计数（预算仍 8）。

5. **吸附物改变球的滚动轮廓**：已由球层的径向代理实现（BallMotor README「径向凹凸代理」），不属于道具碰撞体层。

6. **Unity 侧怎么落**

   **已定并已实现：扩展现有 `PickupFieldSync`（`EnsurePropCollider`），不手挂、不另写生成器。** 一条管线同时写 `propBounds`/`propLocalBounds` 与玩家用的 `BoxCollider`，`valueUnits` 由导入器声明值传入。

7. **形状数据的入口**

   **已定：`placement.json` 的 `defs[].collider`**（COLLIDER_PROXY），不走模型 manifest。坐标是道具局部空间。

## 11. 复核方式（今天能跑 / 必须 Unity）

**今天能离线做（本文的数字都是这么来的）**

```python
# D_release：与吸收门同一式子反解（V 取场景 propBounds 的三轴积，k = 0.70）
V = 0.062714            # watering_can
D = (60 * 0.7 * V / 3.141592653589793) ** (1/3)        # → 0.943
```

```python
# 110 件旋转是否全 identity、61 件 propColliders 是否为空
# 解析 StoryMode.unity：propTransforms[i] → Transform.m_LocalRotation；propColliders[i] == {fileID: 0}
# 结果：1 种旋转 (0,0,0,1) ×110；空 collider 61 件、非空 49 件
```

**必须 Unity**：玩家能否走过 0.20 m 的门槛而不被绊住；`BoxCollider` 挂上后吸附/复位有没有留下悬空实体；1 个 vs 多个挡路盒的帧时间；多盒时近集窗口的帧时间。

## 12. 不做什么 / 与既有决定的关系

- **不改** `propBounds`、不改体积门、不改道具块容量、不改 `PickupFieldHost` 的现有行为、不改任何 Blender 脚本、不写运行时代码。
- **不改** `Docs/Design/StoryMode/PROP_CATALOG.md` 的其它任何列。那 61 行的 `视觉模型文件` / `当前 propBounds` / `体积` / 三列 ✅❌ 由目录 owner 维护，见该文 §0.1 / §1。
- 与 [OPEN_DECISIONS](../../OPEN_DECISIONS.md) 的关系：(o) 三层分离是本文的前提；(l) 体积门是「挡不挡」的唯一口径；本文只在 (o) 的「碰撞体」那一层里填内容，不新增决定。

## 13. 与目录表的对齐

- `PROP_CATALOG.md` §4 第 6 条记录了两件**模型尺寸与设计三轴不一致**的件（`toy_ball` 0.40 m vs 设计 0.15；`napkin` 0.30×0.30 vs 设计 0.15×0.15）。`propBounds` 没错，**错的是模型或设计三轴**，需要设计侧拍板。
- `exports/*.manifest.json` 没有 `collision` 键，也不再计划加（§5）。
