# 道具碰撞体层（COLLIDER LAYER）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15（同日第二轮：写入 Lead 决定）。
> 写入范围：本文件新建，另在 [PROP_CATALOG](../StoryMode/PROP_CATALOG.md) 填「碰撞体方案」列并改掉「另一条线在做」的旁注。**没有写运行时代码、没有改 Blender 脚本、没有开 Unity、没有 Play Mode。**
> 2026-11-15 第二轮只做一件事：把 §10 的 7 个未决问题里 **Lead 已拍板的 1–4、6、7 逐条写成「已定」**（正文相应位置同步改措辞），**第 5 条保持未决、标注「待用户决定」**。已定方案本身**没有实现**——`PickupFieldSync` 的扩展、manifest 的 `collision` 块、任何 `BoxCollider` 都还没写；本轮只落文字。
> 触发：用户 2026-11-15 的硬约束「不仅仅『物体建模』，还有『碰撞体建模』呢，别搞混了」（[OPEN_DECISIONS](../../OPEN_DECISIONS.md) (o)），以及 `PROP_CATALOG` 的「碰撞体方案」整列 `待定` 被注明「另一条线在做」而实际无人认领。
> 引用约定：`Runtime/…`、`Tests/…`、`Labs/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。**本文不给行号**（这几个文件本轮都在动），一律按符号名引用。

## 0. 六条结论（一句话版）

| # | 问题 | 结论 |
|---|---|---|
| 1 | 碰撞体给谁用 | 只给 **(b) 玩家走路**；球挡路（a）走 `propBounds` 的体积门、不读碰撞体；吸附物（c）今天完全不参与碰撞，`RadialProxyMath` **没有接线**（§2） |
| 2 | AABB 什么时候够 | 判据不是「体积填充率」，是**空腔净宽 vs 该件释放球径**：`0.30 m ≤ D_gap < D_release` 才必须拆盒（§3） |
| 3 | 形状表达 | 每件 **0..K 个局部轴对齐盒**，`K ≤ 6`，**有序 = 优先级**；球挡路预算仍是 6 格，装不下时**退到 0 号盒**而不是退到「不挡」（§4） |
| 4 | 建在哪、怎么进 Unity | Blender 里 `<id>_COL_<n>` 命名的盒对象（**不进渲染网格**）→ 写入 manifest 的 `collision.boxes`（**Unity Y-up**）→ **扩展现有编辑器同步工具 `PickupFieldSync` 读清单并同步 `BoxCollider`**，接进 `propColliders`；**无盒时回退视觉 AABB**（§5，已定 §10-6） |
| 5 | 静态布景 | **同一套表达、另一套预算**：`AddBox` 只有 8 格且地图校验器按「1 structure = 1 collision 盒」计数，所以布景多盒要动地图契约；**已定：布景只挑主盒，本轮不动地图契约**（§6、§10-4） |
| 6 | 与挡路接线怎么衔接 | **不返工**：`SetBlockingBoxes` 本来就吃 N 个盒，电机零改动；只改宿主「建盒」那一段，且先以「每件 0 盒 = 今天行为」上线（§7） |

**全文最关键的一句**：`propBounds` 管「**挡不挡**」（与吸收门同一个函数），碰撞盒管「**挡成什么形状**」。两者分开，所以加碰撞盒**永远不会**改变某个球径下能不能吸——这正是 (o) 三层分离要保的性质。

## 1. 三层与本文的位置

| 层 | 数据在哪 | 谁读 | 决定什么 | 精度要求 |
|---|---|---|---|---|
| 视觉网格 | FBX / MeshFilter | 渲染 | 长什么样 | 亚厘米 |
| **碰撞体层（本文）** | Blender 盒对象 → manifest `collision.boxes` → 场景 `BoxCollider` | **VRChat 玩家胶囊（PhysX）** | 玩家能不能走过去 | 20–30 cm（绊脚比穿模更糟） |
| 玩法体积 | 场景 `propBounds`（宿主平行数组） | 吸收门、reach、挡路门 | 能不能吸、够不够近、挡不挡 | 1–2 cm（错一点就变成「空气墙」或「白送」） |

三层不混用的依据是 (o)；挡路盒**只**来自 `propBounds` 是 [BLOCKING_PROPS_PLAN](BLOCKING_PROPS_PLAN.md) §0.6 已经拍过的实现现状，本文**不改它**。

一件道具「多大」只在 `propBounds` 里声明，**不要**用碰撞盒去反推玩法体积：原作本身就把空腔算进包围盒（[ABSORB_SIZE_RATIO](../../Research/KatamariBallRules/ABSORB_SIZE_RATIO.md) S-AR07 的「包围盒含空腔」），而碰撞盒恰恰要把空腔挖掉。**同一个物件，玩法体积含空腔、碰撞体不含，这不是矛盾，是原作同构。**

```
Blender 模型脚本 ──► manifest.collision.boxes ──┬──► Unity 场景 BoxCollider ──► 玩家胶囊
        │                                        └──► (将来) 宿主多盒数组 ──► SetBlockingBoxes ──► SweepMath ──► 球
        └──► manifest.bounds_m ──► 场景 propBounds ──► 吸收门 / reach / 挡路门（挡不挡）
```

## 2. 问题 1：碰撞体给谁用

### 2.1 (a) 球挡路 —— **不用碰撞体，且不能改用碰撞体**

【代码事实】球是纯 kinematic：全仓没有 `Rigidbody` / `CharacterController`，位置只由 `SweepMath` 写（`KatamariBallMotor.StepBall`）。挡路今天由 `PickupFieldHost.UpdateBlockingProps` 用 `propBounds` 建轴对齐盒，经 `motor.SetBlockingBoxes` 追加进扫掠。宿主头注释明写「not from a `BoxCollider` and not from the visual mesh」。

所以「球挡路」这个消费者的输入是**玩法体积**，不是碰撞体。为什么不能反过来让它读碰撞体：一旦挡路盒取决于「这件有没有挂 collider」，同一件道具的物理行为就会随场景接线而变，而吸收门永远只看 `propBounds`，两者立刻可以漂移（`BLOCKING_PROPS_PLAN` §0.6 的原话）。

**但「挡不挡」与「挡成什么形状」可以分开**：将来若要用多盒细化挡路形状，读的也是**同一份 `collision.boxes` 数据**（宿主序列化成数组），**不是**运行期去读 Unity 的 `Collider`。见 §4、§7。

### 2.2 (b) 玩家走路 —— **今天唯一的正式消费者**

VRChat 里世界是实心的靠场景 collider，玩家的碰撞胶囊由 VRChat 的玩家控制器管（知识库 `entities/world/scene-components/boxcollider.md`：BoxCollider 是白名单组件、低开销；玩家碰撞「由 VRChat 玩家控制器管理」，社区理解，官方无专页）。玩家大多数时间坐在球后的 `VRCStation` 上（`Runtime/Riding/BallRideStation`），但**站立与走动是存在的**：交互坐下之前、按退出按钮之后都要走路。

现状（本次逐字节解析 `Labs/StoryLevel/StoryLevel.unity`）：

| 事实 | 读数 |
|---|---|
| 110 件道具里 `propColliders[i]` 为空 | **61 件**（正好是已换真实网格的 16 个 def） |
| 仍带 `BoxCollider` | **49 件**（Cube 占位的方块，0.24–0.90 m 立方体） |
| 110 件道具的旋转 | **110/110 = identity**（`m_LocalRotation = (0,0,0,1)`），缩放 ≈ 1 |
| 真实网格道具的 `propBounds` | 已经是网格 AABB（例：`napkin_01` = 0.30×0.028×0.30，`watering_can` = 0.26×0.415×0.58） |

**【序列化实测】**上一轮把 61 件的 `BoxCollider` 全删了，理由是「30 cm 方块挡在 2.8 cm 餐巾上是空气墙」。这个判断对**小件**成立，但它同时把 0.58 m 的洒水壶、0.40 m 的小皮球也变成了可穿模——**删对了方向，删过头了**。本文要补的就是「哪些该留、留多大」。

**宿主已经有一个现成的接缝**：`[SerializeField] private Collider[] propColliders`，唯一用途是**吸附/隐藏时 `enabled = false`、复位时 `enabled = true`**（`PickupFieldHost.TakeAttachment` / `ResetField`），并在数组**长度不足**时打一条 `"prop colliders not fully wired; attached props stay collidable"` 警告。注意那条警告只看长度（`propColliders.Length < propCount`）：场景里这个数组已经是 110 项，所以**槽位是 null 时它静默通过**——接完碰撞体必须逐件核对槽位，「没报警」不等于接对了。也就是说**碰撞体层必须接进这个数组**，否则吸到球上的物件会留下一个悬空实体（玩家会撞到浮在空中的盒子）。这条是硬约束，不是可选项。

**每件一件的局限**：`propColliders` 是 `Collider[propCount]`，一件一个。所以：

- **K = 1 的今天**：直接挂 1 个 `BoxCollider` 到道具物体上（物体 scale=1、rotation=identity、原点就是 AABB 中心，`center`/`size` 可直接用局部盒），接进 `propColliders[i]`，零代码改动。
- **K ≥ 2 的将来**：需要「每件一个碰撞体根 GameObject + 0..K 个 BoxCollider」，宿主改成整根开关（`propCollisionRoots[i].SetActive(false/true)`）。这是 `PickupFieldHost` 里 3 行级别的改动，但**该文件属于挡路接线那位代理**，必须协调，本文只给接口（§7.4）。

### 2.3 (c) 被吸附后粘在球上的物件 —— **不需要碰撞体，而且今天本来就没有**

【代码事实】三条一起看：

1. 扫掠半径是 `collisionRadiusWorldM`（球芯的碰撞半径），**不含**吸附物（`SweepMath.TrySweepSphere(..., collisionRadiusWorldM, ...)`）。
2. 接地支撑走 `GroundContactMath.TrySolve(coreRadiusWorldM, proxyRotation, localProxyPoints, ...)`，而 `localProxyPoints` 在 `Start` 里被写成 `new Vector3[0]`，注释是「until it feeds the motor the support height degenerates to the core sphere」。
3. `RadialProxyMath`（32 方向支持壳）**没有任何运行期调用者**：全仓只有 `Tests/EditMode/BallLayers/RadialProxyTests.cs` 调它；`Docs/Design/BallLayers/RadialProxy/README.md` 自己也写「未编译/未作为组件」「只作视觉表示、不参与判定」。

所以「吸附物改变球的滚动轮廓」是**原作观感 + 项目尚未实现的目标**，不是现状，更不是靠「给道具加碰撞体」能实现的：吸附物挂在 `attachmentRoot` 下、跟着球走，它们的 Unity collider 在吸附瞬间被宿主**主动关掉**（`propColliders[i].enabled = false`），所以连玩家都撞不到。要让它们参与，改的是**球层**（RadialProxy 接进 `GroundContactMath`，以及/或者把外壳接进扫掠），那是另一条线，且属于「吸附物代理」，**不是道具自身的碰撞体**（§10-5）。

### 2.4 一张表收束

| 消费者 | 今天读什么 | 需要多细 | 实现位置 | 碰撞体层参不参与 |
|---|---|---|---|---|
| (a) 球挡路 | `propBounds` AABB + 体积门 | 粗（扫掠是保守扩张、无上台阶） | `PickupFieldHost` → `SetBlockingBoxes` → `SweepMath` | **不参与**；将来可选「同一份盒数据」喂多盒，但「挡不挡」仍由 `propBounds` 定 |
| (b) 玩家走路 | 场景 `BoxCollider`（PhysX） | 很粗（20–30 cm 足够） | VRChat 玩家胶囊 + 场景 collider | **唯一的正式消费者** |
| (c) 吸附物 | 空代理（球芯球） | — | `GroundContactMath`（未接 RadialProxy） | 不参与，也不该参与 |

## 3. 问题 2：AABB 什么时候够、什么时候不够

### 3.1 判据（可算，不用手感）

设：

- `V = a·b·c` = 该件 `propBounds` 的三轴积（玩法体积，**含空腔**）；
- `D_release = (6·V·ratio/π)^(1/3)`，`ratio = absorbVolumeRatio = 10` —— 这就是「球长到多大时这件不再挡路」（即吸收门放行它的球径，与 `PickupAbsorbMath.TryComputeAbsorbVolumeLimit` 同一式子反解）；
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
| `seed` | 3 | 0.016 | 0.000014 | 0.065 | 否 | 无 | 不需要 |
| `acorn` | 2 | 0.050 | 0.000076 | 0.113 | 否 | 无 | 不需要 |
| `pebble` | 1 | 0.032 | 0.000084 | 0.117 | 否 | 无 | 不需要 |
| `bottle_cap` | 1 | 0.0145 | 0.000034 | 0.087 | 否 | 无 | 不需要 |
| `paper_scrap` | 1 | 0.012 | 0.000088 | 0.119 | 否 | 无 | 不需要 |
| `strawberry` | 8 | 0.040 | 0.000052 | 0.100 | 否 | 无 | 不需要 |
| `grapes` | 8 | 0.022 | 0.000079 | 0.115 | 否 | 无 | 不需要 |
| `cookie` | 2 | 0.009 | 0.000045 | 0.095 | 否 | 无 | 不需要 |
| `orange` | 6 | 0.077 | 0.000495 | 0.212 | 否 | 无 | 不需要 |
| `apple` | 6 | 0.079 | 0.000492 | 0.211 | 否 | 无 | 不需要 |
| `pear` | 5 | 0.090 | 0.000265 | 0.172 | 否 | 无 | 不需要 |
| `banana` | 4 | 0.032 | 0.000391 | 0.195 | 否 | 无 | 不需要 |
| `napkin` | 5 | 0.028 | 0.002520 | **0.364** | **是**（0.30–0.36 会被挡） | 无（平片） | 不需要（见下） |
| `watering_can` | 2 | 0.415 | 0.062714 | **1.062** | **是** | 无贯通，但壶嘴边有 ~0.3 m 敞开凹口 | **1 盒：壶身（不含壶嘴）** |
| `watermelon` | 3 | 0.220 | 0.015840 | **0.671** | **是** | 无（实心椭球） | **AABB** |
| `toy_ball` | 4 | 0.400 | 0.064000 | **1.069** | **是** | 无（实心球） | **AABB** |

三条必须解释的判决：

1. **16 个 def 里 13 个（52 件）「不需要」不是偷懒，是两条独立理由同时成立**：① 它们 `D_release < 0.30 m`，起手球就能吃掉，**从来不会挡球**；② 它们最高点 < 0.20 m，玩家应当**踩过去**而不是被 4 cm 的草籽绊住——上一轮删掉的正是这类空气墙。给它们挂碰撞体的唯一效果是把刚修好的 bug 装回去。**已定（2026-11-15，Lead）：碰撞体只给「身体级大件」做（玩家站立时会被绊到的尺寸），本节这张表就是执行结果。**
2. **`napkin` 会挡球，但仍然「不需要」碰撞体**：球挡路不读碰撞体（§2.1），而 2.8 cm 的餐巾对走路的人就是一块布。它在球那侧是**已接受的取舍 R1**（`BLOCKING_PROPS_PLAN` §8：低矮件对扫掠就是一面墙，第一版接受）。**两个消费者给出不同答案，是三层分离的正常结果，不是不一致。**
3. **`banana` 的 AABB 确实够用（无空腔），但它不需要碰撞体**——「AABB 够不够」和「要不要做」是两个问题。简报拿香蕉当「AABB 够用」的例子是对的；本文的判决是「够用，但不必做」。同类的还有 `plate`（还没模型，计划三轴 0.24×0.24×0.025 = 0.00144 m³，`D_release` 0.302 m——0.30 m 的球差 0.6% 吃不到、0.31 m 就吃，仍属于「几乎不挡」）。

**`watering_can` 为什么不是 `AABB`**：视觉 AABB 是 0.26×0.415×0.58，其中 0.58 那一轴几乎全是壶嘴。整件包一个盒，会在壶嘴**旁边**凭空多出约 0.3 m 的实心区——那正是「空气墙」。壶身单独一个盒（约 0.26×0.30×0.26，具体值等模型脚本量出来）就把这块去掉；壶嘴细（约 5 cm），玩家走过去、球擦过去都不值得为它再花一个盒。

### 3.4 还没建模、但已经知道会撞上这条判据的家具

按 [PROP_SET_PLAN](../StoryMode/PROP_SET_PLAN.md) §3.6/§3.7 的三轴与 `PROP_CATALOG` §2.2 的 `D_release`：

| 件 | 三轴 (m) | V (m³) | D_release (m) | D_gap (m) | 判决 |
|---|---|---:|---:|---:|---|
| `picnic_table` 野餐桌 | 1.50×0.75×0.72 | 0.810 | 2.49 | ≈0.66（桌下净空） | **拆盒**：桌面板 1 + 4 条腿（腿 < 0.10 m，可省；见下） |
| `park_bench` 公园长椅 | 1.80×0.55×0.80 | 0.792 | 2.47 | ≈0.42（座下净空）+ 座板与靠背之间 | **拆盒**：座板 + 靠背 + 2 个腿架 ≈ 4 |
| 椅子（E 档新增） | ~0.50×0.50×0.90 | ~0.225 | ~1.66 | ≈0.42 | **拆盒**：座板 + 靠背 + 4 腿 ≈ 6 |
| 大树（布景） | 树干 Ø0.25，冠底 ~1.8 | — | — | 冠下 1.8 | 2 盒：树干 + 树冠（冠底低于「最大球顶」时才要第二个） |
| 遮阳伞（布景） | 伞面 2.2，杆 Ø0.04 | — | — | 伞下 ~1.9 | 1 盒：杆（伞面在球够不到的高度） |

「腿 < 0.10 m 一律省」是一条**设计取舍**：省掉 4 条腿 → 球能从桌下穿过但会擦到腿（视觉穿模约 6 cm 宽）；不省 → 4 个盒吃掉 6 格预算的 2/3，两张桌子同时靠近时必然有一张退化成 1 盒。**第一版选省**，理由见 §4.3 的退化规则。

## 4. 问题 3：形状表达

### 4.1 数据形状

**每件道具 0..K 个轴对齐盒，坐标是道具局部空间（原点 = 网格 AABB 中心 = 场景放置点），单位米，`min <= max`。**

- `K = 0`：没有碰撞盒 → 回退视觉 AABB（今天 = `propBounds`）。
- `K ≥ 1`：盒的**顺序即优先级**，`0` 号是「主盒」：**在大球仍会挡路时它必须挡住**。桌子的 0 号盒是桌面板（不是腿），椅子的 0 号盒是座板。理由：预算不够时退化到 0 号盒，丢的只能是「薄腿/靠背的保真度」，不能丢「这件还挡不挡」。
- 盒必须**包含在视觉 AABB 内**；越界的盒是数据错误（会让挡路盒跑到玩法体积外面，也会让吸收体积低估实体）。

### 4.2 AABB → 世界盒：为什么今天不痛，什么时候会痛

电机与扫掠**只吃世界轴对齐盒**（`SweepMath` 的 `boxMinima/boxMaxima`）。局部盒要按道具的旋转/位置变成世界盒：

| 道具旋转 | 世界盒 | 代价 |
|---|---|---|
| identity（**今天 110/110**） | 局部盒 + 位置，逐位相等 | 0 |
| 仅 yaw（绕 Y） | 水平两轴变为 `abs(w·cosθ) + abs(d·sinθ)`，最坏 45° 时 **+41%**；竖直轴不变 | 8 个角点变换，每次扫描每盒 1 次；O(K) |
| 任意（含 pitch/roll） | 最坏 **+73%**（√3） | 同上 |

今天**全部道具都是 identity 旋转**【序列化实测】，所以「多盒 + 世界 AABB」落地时**一行 OBB 数学都不用写**：世界盒 = 局部盒 + 位置，逐位相等，可离线断言。这也解释了为什么这条迁移现在做最便宜。

顺带一个发现：现有单盒路径**完全忽略旋转**（`BlockingPropMath.TryBoxFromCentreAndExtents(position, propBounds)`，只读位置），所以道具一旦被摆成 45°，现在的挡路盒是「转回去的轴对齐盒」，会**欠覆盖**角部最多 41%。多盒路径顺手把这个问题修掉（代价是过覆盖等价的那 41%），比现在**更**正确，不是更保守。

**不做 OBB 的理由**（否决记录）：把 `SweepMath` 改成球 vs OBB 要重写 TOI、初始穿透、多接触投影、面法线定向四段，而这几段现在各只有一份实现，是全仓最贵的数学。收益只是「少 41% 的过覆盖」，而这个过覆盖已经由 `propBounds` 的 AABB 承担了（`BLOCKING_PROPS_PLAN` R2 已记录并接受）。

### 4.3 K 的上限与预算

三个上限，别混：

| 上限 | 值 | 理由 |
|---|---:|---|
| `K_authored`（作者可写几个盒） | **≤ 6** | 一件家具最多「面板 1 + 腿/靠背 5」。超过 6 说明该拆成多个道具（或只做主盒），因为一件就能吃光电机的 6 格挡路预算 |
| `K_blocking`（喂给电机的盒数，每件） | **≤ 6，且全局 6** | 电机 `MaxBlockingBoxCount = 6` 是既有常量，本文不动 |
| `K_avatar`（场景里挂几个 `BoxCollider`） | 今天 **≤ 1**（`propColliders` 一件一个）；宿主改成整根开关后 **≤ 6** | §2.2 |

**预算耗尽的退化规则（必须按这个写，否则会退化成「不挡」；已定 2026-11-15，Lead：全局仍 6 格，装不下就退到该件的 0 号主盒）**：

1. 按「球心到盒」的距离升序选盒，**同一件道具的盒按 0..K 顺序**；
2. 某件只要被选中，就**至少收它的 0 号盒**；其余盒能装几个装几个；
3. 全局 6 格装满即止；**不允许**出现「某件本来该挡、却一个盒都没进」的情况——那等于把这件变成可穿过。

举例：两张野餐桌同时靠近（各 5 盒）→ 近的那张收 5 盒，远的那张收 1 盒（主盒）。远处的桌子仍然挡住大球，只是小球的桌下通行保真度降级。**这比「远的那张整个穿过去」好得多，也是为什么 0 号盒必须是面板而不是腿。**

## 5. 问题 4：在哪里建、怎么进 Unity

### 5.1 Blender 侧约定

- 命名：`<id>_COL_<n>`（`n` 从 0 起，0 = 主盒），**网格对象是立方体**，变换应用（identity）、以米为单位，与视觉网格同一 collection。
- **不进渲染网格**：导出 FBX 时只选视觉网格那一个对象（`prop_lib.export_asset` 今天就是 `select_all(DESELECT)` + 选 `ob` + `use_selection=True`），所以把 `_COL_` 对象留在场景里也不会被导出。**验证点**：`verify_import.py` 回读的 `mesh_objects` 必须仍然是 1。
- 盒子由**模型脚本显式声明**，不要在 Blender 里手工摆：`prop_lib.Builder` 已经按 `add()` 攒了每个部件（`self.objs`），所以给 `Builder` 加一个 `collision(part_keys, name=...)` 就够——用已生成部件的 AABB 算盒，尺寸随模型自动同步，不会出现「模型改了、盒忘了改」。
- 必须在部件级声明「**哪些部件被省略**」及理由（腿/壶嘴/把手），写进 manifest 的 `omitted_parts`，否则下一个人无法判断那是遗漏还是决定。

### 5.2 manifest 契约（新增，不动已有字段）

`Docs/Art/OriginalModels/props/exports/<id>.manifest.json` 增加一个顶层键（`build_all.py` 同步写进总 `manifest.json` 的每件条目）。**下面的数字只是形状示意，不是任何一件的真实量测值**：

```json
"collision": {
  "space": "unity_y_up",
  "source": "authored",
  "boxes": [
    { "part": "body", "min": [-0.1301, -0.2077, -0.1301], "max": [0.1301, 0.2077, 0.1301] }
  ],
  "omitted_parts": [
    { "part": "spout", "reason": "thin (0.05 m); a player walks through it and a ball clips it, neither is worth a box" }
  ],
  "check": {
    "boxes_inside_visual_bounds": true,
    "vertex_coverage": 0.94,
    "max_vertex_distance_m": 0.021
  }
}
```

字段口径：

| 字段 | 含义 |
|---|---|
| `space` | **必须**是 `"unity_y_up"`：盒已换算到 Unity 轴（x, y=上, z=前）。**`bounds_m` 保持 Blender 空间不动**，两套空间并存，靠这个字段区分 |
| `source` | `authored`（有 `_COL_` 盒）/ `aabb_fallback`（作者声明「整件一个盒」）/ `none`（明确不要碰撞体，例如 13 件小件） |
| `boxes` | 0..6 项，**有序 = 优先级**；`part` 只给人和校验用，运行期不读 |
| `omitted_parts` | 省略的部件 + 理由；没有就空数组 |
| `check.vertex_coverage` | 网格顶点落在某个盒内（或距盒 ≤ 2 cm）的比例，构建时算。它只是**供人复核**的信号，不是门禁 |

轴换算（**这一条最容易错**）：现有 16 件的 `bounds_m`（Blender）与场景 `propBounds`（Unity）逐件对得上，得到 `unity = (x, z, y)`（例：`banana` 的 Blender 0.18×0.0678×0.032 → Unity 0.18×0.032×0.0678）。**排列被实测确认，符号没有**：`watering_can` 的壶嘴在 Blender 指向 +Y，它在 Unity 是 +Z 还是 −Z，只能等第一件带盒的道具进 Unity 目视核对一次，然后把符号写死进流水线的 `BLENDER_TO_UNITY` 常量。

**已定（2026-11-15，Lead）：选 A ——盒由流水线换算到 Unity Y-up，符号在首次导入时于 Unity 目视核对一次。** 这条已经把「排列」从猜测升成实测：本轮复核了场景里 61 件真实网格的 `propBounds` 与 24 个 `exports/*.manifest.json` 的 `dimensions_m`，按 `unity=(x,z,y)` **61/61 完全吻合**（最大偏差 1.2×10⁻⁷ m）。**但 AABB 尺寸是绝对值，它只能确认排列、确认不了符号**；符号与「盒中心是否落在网格原点上」这两件事，放在第一件带 `collision.boxes` 的道具导入时目视核对一次，然后把常量写死。

### 5.3 Unity 侧：谁读、存在哪

**已定（2026-11-15，Lead）：不手挂，也不新写生成器——扩展现有的编辑器同步工具 `Editor/Pickup/PickupFieldSync.cs`，让它读模型 manifest 的 `collision.boxes` 并同步碰撞盒。** 模型改了重跑一次同步即可，不另起一份实现。

为什么是它：`PickupFieldSync` 本来就是「从场景子物体把宿主六个数组填对」的唯一作者路径（头注释原话是 *the one authoring path*），而 `propColliders` 已经是它写的六个数组之一。碰撞盒只是把同一条管线往下延伸一格：**同一次同步，既写 `propBounds`，也写 `BoxCollider` 与 `propColliders[i]`**，两边永远出自同一次读取，不会出现「网格换了、盒还是旧的」这种漂移。

现状与要补的差：

| 现状（`PickupFieldSync`） | 要补 |
|---|---|
| 只**接**已有 `Collider`（`PropCandidate.PrimaryCollider` 取道具自身或路径序第一个子 collider），**从不创建** | 按 `collision.boxes` 创建/更新道具物体上的 `BoxCollider`（v1 只用 `boxes[0]`：`center` = 盒中心、`size` = 盒尺寸），`source: none` 时明确不建 |
| 只读场景（Renderer bounds、层级路径） | 另读 `<id>.manifest.json` 的 `collision` 块；路径要能在 Editor 里解析（`Docs/` 不在 Unity 资产库里，见下） |
| `valueUnits` 由 `PickupFieldAuthoringRules` 的 (kind, tier) 表算出并**写回** | **在价值口径统一前仍然拒绝它的 `valueUnits` 变更**：读到既有非零值时保留、报告一条「refused: value units pending one rubric」，不覆盖 |

**代价（要写清楚，不是免费的）**：

1. **同步工具要加两段新逻辑**：manifest 读取（含 `collision` 缺失时的默认 `none`）与碰撞盒生成（幂等、可 Undo、不乱改 prefab 实例）。这不是小改，`PickupFieldSync.cs` 已有 700+ 行且承担 U# 程序资产拷贝顺序。
2. **manifest 位置要定**：`Docs/Art/OriginalModels/props/exports/` 不在 `Assets/` 里，Unity 的 `AssetDatabase` 看不到。要么同步工具用项目相对路径直接 `File.ReadAllText`（可跑但绕开资产库，路径随仓库布局变化就会静默失效），要么由流水线把同一份清单写进 `Assets/`（例如 `Assets/VRC_Katamari/Art/Props/prop_collision.json`）——**这一条仍是执行时要定的小项，不再是「选哪个方案」的未决**。
3. **需要测试**：EditMode 用例至少覆盖 ① 全 0/无 `collision` 时 `BoxCollider` 与 `propColliders` **逐位不变**（等价于今天的行为）；② 幂等（连跑两次结果相同）；③ identity 旋转下 `BoxCollider.size` == `boxes[0]` 尺寸、盒 ⊆ `propBounds`；④ `source: none` 时不留 collider。
4. **`valueUnits` 口径未统一前的运行期拒绝**见上表第三行；这条不是「以后再补」，是**加碰撞盒这一次就要带上的守卫**，否则一次同步会顺手把 110 行的分值改掉。

- 挂在哪：道具物体本身（scale≈1、rotation=identity、原点=AABB 中心），`BoxCollider.center` = 盒中心、`size` = 盒尺寸，**不需要子物体**。
- 接进 `propColliders[i]`。**不要**把 `"not fully wired"` 不出现当成验收：那条警告只在数组长度不足时生效，而场景里长度已经是 110。真实验收是**逐件核对槽位非空**，再抽一件吸掉、看原来位置有没有留下悬空实体。
- 被吸附/隐藏时宿主自动关掉它；复位时自动开回来。**不要**自己写脚本管这件事。
- **首次运行的现实预期**：24 个 `exports/*.manifest.json` **今天一个都没有 `collision` 键**，所以扩展后的第一次同步相当于「全 `none`」的空跑——它应当只报告、不建盒、不改 `propColliders`。真正的 9 个盒要等流水线先写出 `collision.boxes`（§9）。

**为什么不做手挂（记下被否的方案）**：手挂只有 9 个盒、看起来更便宜，但模型一改盒就静默过期，而「盒过期」正是上一轮回滚的那个 bug 的同一类；项目已有同步工具、已有「重跑即对齐」的路径，再开一条手挂路径等于让同一份数据有两个来源。**也不新写 `PropColliderBuilder`**：那会与 `PickupFieldSync` 争「谁写 `propColliders`」，两份实现迟早不一致。

### 5.4 与 `propBounds` 的关系（三条硬规则）

1. **回退**：`collision.source == none` 或 `boxes` 为空 → 碰撞体 = 视觉 AABB（今天等于 `propBounds`）。这条保证「被判定需要碰撞体、但还没做盒的道具」不会退化成可穿过，**只是不精确**。**注意边界**：它只对「需要碰撞体的件」生效——§3.3 判为 `不需要` 的 13 个 def / 52 件是按 §10-1 的决定**故意可穿过**，不走这条回退，否则会把空气墙装回去。
2. **不许回写**：碰撞盒**永远不能**改 `propBounds`。一件道具「多大」只在一处声明（(o)）。
3. **不许运行期互读**：宿主不读 `Collider`，Unity 侧不从 `propBounds` 反推碰撞体。两边都从 manifest 来，**同一个源，两条管道**。

## 6. 问题 5：静态布景

**同一套表达，另一套预算。**

- 表达相同：不可吸的地形/墙/大树/当布景的桌子，同样是「0..K 个轴对齐盒」，主盒规则一样。
- 预算不同：布景走 `KatamariBallMotor.AddPlane` / `AddBox`，容量是 **8 平面 + 8 盒**，而且是**地图作者的数据契约**——`Tools/mapbuild/validate_map.py` 的 `MOTOR_MAX_COLLISION_BOXES = 8` 把它抄成了常量，`layout.json` 里每个 `structures[i]` 只能声明**一个** `collision` 值（`"box"`/`"plane"`），校验器按「1 structure = 1 盒」计数。
- 所以布景要拆盒，必须动三处之一：**① 加 `collision_parts` 数组**（校验器改成数 parts，预算仍是 8）；**② 抬 `MaxBoxCount`**（同时改校验器常量、全部 lab 的场景预算、`StoryLevel` 现在 4 盒 / `BallPlayground` 现在 7 盒——后者随该 lab 于 2026-11-15 退役删除）；**③ 不拆**，布景靠人挑「主盒」，靠 `AddPlane` 补大面积面。
- 现状读数：`Docs/Design/Maps/PicnicLawn/layout.json` 的 60 个 structure **一个 collision 都没声明**（0/8），而新增的 `size_gates` 里也没有；真正在用的是 lab 场景（StoryLevel 4 盒；BallPlayground 7 盒已于 2026-11-15 随该 lab 退役）。也就是说 **8 格预算目前基本是空的**，这是好消息：布景拆盒有空间，缺的是**声明方式**，不是容量。

**已定（2026-11-15，Lead）：选 A ——布景只挑主盒，不拆。** 每件只挂一个盒，靠 `AddPlane` 补大面积面；多盒留给「玩家真的会从底下钻过去」的布景，等真的要拆时再走 ①（`collision_parts`），**不要**走 ②，因为抬常量会连带改两张图和每个 lab 的预算，收益却只是几个盒。**本轮不动地图契约**（`layout.json`、`validate_map.py`、8 格预算全部保持原样）。

## 7. 问题 6：和「挡路接线」那位代理怎么衔接而不返工

### 7.1 不变量（衔接的全部前提）

- 「挡不挡」= `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(propBounds[i], limit)` —— 与吸收门**同一个函数**，一个字不改。
- 「挡成什么形状」= 盒。今天 1 个（`propBounds`），将来 0..K 个（`collision.boxes`，缺省回退）。
- 电机 `SetBlockingBoxes(minima, maxima, count)` **本来就接受 N 个盒**，容量 6。**电机零改动**。
- `BlockingPropMath` 的三个纯函数（`TryBoxFromCentreAndExtents` / `TryDistanceToBox` / `SelectNearest`）**契约不变**，只新增一个「局部盒 → 世界 AABB」的纯函数。

### 7.2 接口形状（给宿主，不给电机）

宿主新增（序列化，平行数组，与 `propBounds` 同风格）：

| 字段 | 类型 | 语义 |
|---|---|---|
| `propBoxCounts` | `int[propCount]` | 0 = **回退 `propBounds`**（今天的行为）；1..6 = 用下面两个数组里的前 N 个 |
| `propBoxMinima` | `Vector3[propCount * MaxBoxesPerProp]` | 局部空间，固定步长（`row * MaxBoxesPerProp + k`），未用槽位为 0 |
| `propBoxMaxima` | `Vector3[propCount * MaxBoxesPerProp]` | 同上 |

选**固定步长**而不是「计数 + 前缀和」：UdonSharp 里前缀和要一次额外遍历和一处 off-by-one 风险，而 175 件 × 6 × 2 × 12 B ≈ 25 KB 的场景序列化数据完全可接受（`propBounds` 本身已经 175 × 12 B）。若嫌大，把 `MaxBoxesPerProp` 降到 4（175 × 4 × 2 = 5600 个 float）。

选择算法（**只改宿主 `UpdateBlockingProps` 里「建盒 → 选择」那一段**）：

1. 逐件：`propBoxCounts[i] == 0` → 用 `propBounds` 建 1 个盒（**现有代码原样**）；否则逐盒用 `TryWorldBoxFromLocal(boxMin[k], boxMax[k], position, rotation, out min, out max)` 建世界盒（identity 旋转时结果必须与局部盒逐位相等，可离线断言）。
2. 候选数组从「每件一格」变成「每件 `MaxBoxesPerProp` 格」，剪枝仍按**件**的中心距（用 `propBounds` 半对角线，现有逻辑不变），避免为远处道具做任何盒数学。
3. `SelectNearest` 的 `previousIndices` / 粘性 / 距离全部按**盒行**工作，容量参数仍是 6；行 → 件 = `row / MaxBoxesPerProp`（整数除法），用于「已吸附的件不选」。
4. 按 §4.3 的退化规则夹到 6 格，写进 `SetBlockingBoxes`。

**不填数据时行为必须逐位不变**——这是验收判据：`propBoxCounts` 全 0 时，`SetBlockingBoxes` 收到的盒与今天**完全相同**（`BlockingPropMathTests` 全部照过）。所以这条改造可以「先合入、后填数据」，不存在中间态。

### 7.3 分步迁移（每步独立可验、都可回退）

| 步 | 做什么 | 行为变化 | 验收 |
|---|---|---|---|
| **S0** | 本文 + 目录列 | 无 | `validate_plan.py` + `Docs/tests` |
| **S1** | 扩展 `PickupFieldSync`：加 manifest 读取、`collision` 缺失按 `none`、按 `boxes[0]` 幂等生成 `BoxCollider` 并接 `propColliders`，同时保留 `valueUnits` 拒绝守卫；带 4 条 EditMode 测试（§5.3） | 无（首次全 `none`，空跑） | 测试全绿；连跑两次场景逐位不变；`valueUnits` 一个都没变 |
| **S2** | manifest 增 `collision`：`watering_can` 一条 `authored`，`toy_ball`/`watermelon` 两条 `aabb_fallback`，其余 13 件按「没有 `_COL_` 对象」自动判 `none`；重跑 S1 的同步工具落地 3 个 def / 9 件 = 9 个盒 | 玩家不再穿过 0.4 m 球/西瓜/洒水壶；球行为**不变** | `SHA256SUMS` 更新、`verify_import` 仍 1 个 mesh、场景里每个 `BoxCollider` 与 manifest 对应盒逐个相等、吸掉一件后原地无悬空实体 |
| **S3** | 宿主加 `propBoxCounts` 等三数组（全 0）+ `TryWorldBoxFromLocal` | 无（逐位不变） | `BlockingPropMathTests` 全绿；Play Mode 球感不变 |
| **S4** | 家具建模时填 `counts ≥ 1`，先只填 1 盒/件 | 桌下可通行 | 0.3 m 球从桌下过、1.0 m 球被桌面挡 |
| **S5** | 需要时填第 2..6 盒（腿/靠背） | 腿不再穿模 | 6 格预算下的退化规则可观察（`GetBlockingPropCount()`） |

**S3 之后才允许**谈「球挡路改读多盒」；在那之前，球挡路就是 `propBounds` 单盒，任何人说「加了碰撞体所以球会挡住桌子腿」都是错的。

### 7.4 给挡路接线代理的具体请求（不要我改他的文件）

1. **保持** `SetBlockingBoxes` 的「整批替换、N 个盒、容量 6」语义——多盒方案完全靠它。
2. **K ≥ 2 时**（家具期）把 `propColliders`（`Collider[]`，一件一个）换成 `propCollisionRoots`（`GameObject[]` 或 `Transform[]`，整根开关），否则一件的第二个盒在吸附后仍是实体。
3. **不要**在 `UpdateBlockingProps` 里读 `Collider` 或视觉网格——`collision.boxes` 走宿主自己的平行数组。
4. `MaxBlockingBoxCount = 6` **先不动**；若实测不足，再按 §10-3 抬。

## 8. 数据结构汇总（一页）

```text
manifest（每件，Blender 产出）
  collision.space   "unity_y_up"
  collision.source  authored | aabb_fallback | none
  collision.boxes[] { part, min[3], max[3] }        // 有序 = 优先级，0 = 主盒，≤ 6
  collision.omitted_parts[] { part, reason }
  collision.check   { boxes_inside_visual_bounds, vertex_coverage, max_vertex_distance_m }

场景（Unity）
  道具物体（scale≈1, rotation=identity, 原点=AABB 中心）
    └─ BoxCollider ×1（v1）        ← center/size 从 boxes[0] 抄
       接进 PickupFieldHost.propColliders[i]

宿主（S3 起，序列化）
  propBoxCounts[propCount]                 // 0 = 回退 propBounds
  propBoxMinima/Maxima[propCount * 6]      // 局部空间，固定步长

电机（零改动）
  SetBlockingBoxes(minima, maxima, count)  // 容量 6，整批替换
```

## 9. 迁移步骤（要动流水线脚本的话，只写在这里）

**本文件不改任何脚本**。以下是要改的内容清单，给流水线 owner：

1. `scripts/prop_lib.py`
   - 加 `BLENDER_TO_UNITY` 轴映射常量（`(x, z, y)`，符号待 §5.2 的一次目视核对后写死）；
   - `Builder` 加 `collision(part_keys, name=...)`：用已生成部件的 AABB 算盒（Blender 空间），**立刻**换算成 Unity 空间存进 `self.collision_boxes`；
   - `finish()` 里写 `collision` 块：`space` / `source` / `boxes` / `omitted_parts`，以及 `check`（`boxes_inside_visual_bounds`、`vertex_coverage`、`max_vertex_distance_m`——都只用已算出的顶点与盒，不需要新依赖）；
   - `export_asset` **不改**（现在只导视觉网格，正是我们要的）；但要在导出后断言场景里仍有 `_COL_` 对象且没有进 FBX（`verify_import` 端已有 `mesh_objects == 1` 的检查，把它接到 `collision.boxes` 非空时必跑）。
2. `scripts/models/<id>.py`：只在需要盒的件里调用 `b.collision([...])`，并显式列出 `omitted_parts` 及理由。**本轮 16 件里只有 `watering_can` 需要 authored 盒**（壶身 1 盒，壶嘴进 `omitted_parts`）；`toy_ball` / `watermelon` 写 `source: "aabb_fallback"`（盒 = 视觉 AABB，即 `propBounds`）；其余 13 件写 `source: "none"`（在 `build_all` 里按「没有 `_COL_` 对象」自动判定，不必逐件写）。
3. `scripts/build_all.py`：把 `info['collision']` 原样带进总 `manifest.json` 的每件条目（现在 `props[i]` 是 `info` 的子集，照抄一层即可）；`SHA256SUMS` 已经覆盖 `exports/*.manifest.json`，无需改。
4. `scripts/verify_import.py`：加一条「FBX 回读只有 1 个 mesh 对象」的硬失败（现在只是记录），并把 `collision.check.boxes_inside_visual_bounds` 与回读的 `world_min/max` 对一次（**换算回 Blender 空间再比**，否则轴一变就误报）。
5. `props/README.md`：补 `collision` 键、`_COL_` 命名、`< 0.10 m` 省略规则、以及「盒是 Unity 空间、`bounds_m` 是 Blender 空间」。

**Unity 侧不在流水线里**：`addCollider: false` 的导入设置保持不变（`Docs/Evidence/AssetImport/README.md` §2），碰撞体是场景里挂的，不是从网格生成的。**Cube 占位的 49 件在换网格时必须同一批把它的立方体 `BoxCollider` 换掉**——否则又会出现「0.9 m 的盒子套 0.3 m 的暖壶」。

## 10. 未决问题（1–4、6、7 已由 Lead 拍板；5 待用户决定）

按「什么时候必须定」排序。**2026-11-15 的 Lead 决定已逐条写在每一项下面**，本文正文里相应位置同步改成了「已定」措辞；只有第 5 条仍未决。

1. **碰撞体到底给哪些道具做？**（影响 13 个 def / 52 件的判决，也决定要不要一次真人走测）

   **已定（2026-11-15，Lead）：选 A ——只给「身体级」大件做（玩家站立时会被绊到的尺寸，本文取最高点 ≥ 0.20 m）。** 理由：52 件小件给碰撞体等于把刚修掉的空气墙装回一批，而球挡路本来就不读碰撞体（§2.1），两个消费者给出不同答案是三层分离的正常结果。

   - A（已定）：只给「身体级」大件做（最高点 ≥ 0.20 m）。成本：玩家能穿过香蕉/餐巾；球仍然会被它们挡住（两个消费者的答案不同）。
   - B（否）：所有道具都做（含 3 cm 餐巾）。成本：空气墙复活、PhysX 与维护成本上升。
   - C（否）：按 kind 白名单（例如只给 `container`/`garden`/`toy` 做）。成本：同一尺寸的物件待遇不同，作者每次要查表。

2. **0.20 m 这个门槛要不要一次真人走测？**（PROVISIONAL，没有 Unity 证据）

   **已定（2026-11-15，Lead）：选 A ——先按 0.20 m 做，主房间/关卡第一次真人走测时记录到 `Docs/Evidence/`。** 理由：门槛只是经验值，没有走测数据就改它只是换一个同样没依据的数；先落地、再用一次走测把它变成有依据的数。

   - A（已定）：按本文 0.20 m 先做，第一次真人走测时顺便走一遍记到 `Docs/Evidence/`。
   - B（否）：改成 0.30 m（更保守，更多空气墙）。
   - C（否）：先不做碰撞体，等骑乘/步行流程定稿再一起做。代价：玩家会穿过 0.4 m 的皮球。

3. **挡路多盒的预算**（家具期才咬人）

   **已定（2026-11-15，Lead）：选 A ——仍是 6 格，超出时退到该件 0 号主盒。** 理由：退化语义已经写在 §4.3（至少收主盒，绝不让一件变成可穿过），抬常量会改电机与全部 lab 的预算，收益只在两张桌子同时靠近时才看得见。

   - A（已定）：全局仍 6 格，同一件装不下就退到主盒。
   - B（否）：`MaxBlockingBoxCount` 抬到 12。成本：改电机常量 + 宿主选择缓冲 + 扫掠障碍数最多 +100%。
   - C（否）：每件只 1 盒，接受桌腿/靠背穿模。成本：观感最差。

4. **静态布景要不要多盒，以及怎么声明**（只有 `layout.json` + `validate_map.py` 的 owner 能改）

   **已定（2026-11-15，Lead）：选 A ——只挑主盒。** 理由：8 格预算虽然基本是空的，但声明方式（`collision_parts`）还没定，为几个盒去动地图契约与两张图不划算；需要拆的时候再走 B。

   - A（已定）：布景先只挑主盒，不拆。
   - B（否，留作将来）：加 `collision_parts` 数组，校验器按 parts 计数（预算仍 8）。
   - C（否）：抬 `MaxBoxCount`。

5. **吸附物要不要改变球的滚动轮廓**（原作观感，本项目未实现）——**待用户决定**。

   这一条**仍未决**，本文不给默认、不给推荐；下面的 A 只是「今天的现状」，不是已选方案。

   - A（现状，不是决定）：不做。吸附物只跟位置走，不参与扫掠/接地。
   - B：把 `RadialProxyMath` 接进 `GroundContactMath`（球会「踩」在吸附物上）。成本：球层任务；会改变球能钻多窄、能贴多近的手感，需要重验尺寸门与爬坡。
   - C：把外壳接进扫掠（球会被自己粘的东西卡住）。成本最高，且与「吸收 = 移除」的语义冲突，风险最大。

6. **Unity 侧怎么落：手挂还是生成器**

   **已定（2026-11-15，Lead）：选 C ——不是手挂，也不是新写生成器，而是扩展现有的编辑器同步工具 `PickupFieldSync`（`Editor/Pickup/`），让它读 manifest 的 `collision.boxes` 并同步碰撞盒；模型改了重跑同步即可。**

   代价（完整版见 §5.3）：同步工具要加「读 manifest」与「生成/更新 `BoxCollider`」两段逻辑，并补 4 条 EditMode 测试（无 `collision` 时逐位不变、幂等、盒 ⊆ `propBounds`、`source: none` 不留盒）；manifest 在 `Docs/` 下不在 Unity 资产库里，路径解析方式要定（直读 vs 写一份进 `Assets/`）；**在价值口径统一前，同步工具运行时仍要拒绝它的 `valueUnits` 变更**——今天它是按 (kind, tier) 表写回 `valueUnits` 的，扩展时必须加守卫，否则一次同步会顺手改掉 110 行的分值。

   - A（否）：场景里手挂，数值照 manifest 抄。成本：模型改了会静默过期，需要一条离线校验兜底。
   - B（否）：新写 `Editor/Props/PropColliderBuilder` 读资产侧 JSON 幂等生成。成本：~100 行 + 测试 + 一份 `Assets/` 副本，且与 `PickupFieldSync` 争「谁写 `propColliders`」。
   - C（已定）：扩展现有 `PickupFieldSync`，一条管线同时写 `propBounds` 与 `BoxCollider`。

7. **`collision.boxes` 的坐标空间**

   **已定（2026-11-15，Lead）：选 A ——流水线换算到 Unity Y-up，首次导入时在 Unity 目视核对一次（核对符号与盒相对模型的位置），然后把常量写死。** 理由：`unity = (x, z, y)` 已由 61 件真实网格的 `propBounds` 与 manifest 尺寸逐件对（61/61）确认；让每个手挂/同步的人各转一次轴是最容易错的一条。

   - A（已定）：Unity Y-up，流水线换算，符号在 Unity 目视核对一次后写死。
   - B（否）：Blender Z-up，Unity 侧手转。成本：每个碰它的人都要记一次置换和符号，最容易错。

## 11. 复核方式（今天能跑 / 必须 Unity）

**今天能离线做（本文的数字都是这么来的）**

```python
# D_release：与吸收门同一式子反解（V 取场景 propBounds 的三轴积）
V = 0.062714            # watering_can
D = (6 * V * 10 / 3.141592653589793) ** (1/3)        # → 1.062
```

```python
# 110 件旋转是否全 identity、61 件 propColliders 是否为空
# 解析 StoryLevel.unity：propTransforms[i] → Transform.m_LocalRotation；propColliders[i] == {fileID: 0}
# 结果：1 种旋转 (0,0,0,1) ×110；空 collider 61 件、非空 49 件
```

**必须 Unity（都没做）**：玩家能否走过 0.20 m 的门槛而不被绊住；`BoxCollider` 挂上后吸附/复位有没有留下悬空实体；1 个 vs 6 个挡路盒的帧时间；旋转道具的世界 AABB 过覆盖手感。

**一条建议加的离线门禁（本轮没写）**：一个 `Docs/tests` 用例，读 `StoryLevel.unity` 与 manifest，断言「每件有盒道具的 `BoxCollider.size` == `collision.boxes[0]` 的尺寸」（identity 旋转下）且「盒 ⊆ `propBounds`」。它不需要 Unity，能在模型/场景漂移时报出来——这是 §10-6 已定方案（扩展 `PickupFieldSync`）之外的离线兜底。

## 12. 不做什么 / 与既有决定的关系

- **不改** `propBounds`、不改体积门、不改 `MaxBlockingBoxCount`、不改 `PickupFieldHost` 的现有行为、不改任何 Blender 脚本、不写运行时代码。
- **不动** [BLOCKING_PROPS_PLAN](BLOCKING_PROPS_PLAN.md)（§0.6 的「挡路盒只来自 `propBounds`」本文完全保留，只是把「将来的形状细化」写清楚）。
- **不改** `Docs/Design/StoryMode/PROP_CATALOG.md` 的其它任何列。那 61 行的 `视觉模型文件` / `当前 propBounds` / `体积` / 三列 ✅❌ 当时**还是过时的**（「最长边立方体」时代的数）；**2026-11-15 第 2 版已由目录 owner 按场景重读修正**，见该文 §0.1 / §1，本文不再需要替它记过时项（§13）。
- 与 [OPEN_DECISIONS](../../OPEN_DECISIONS.md) 的关系：(o) 三层分离是本文的前提；(l) 体积门是「挡不挡」的唯一口径；本文只在 (o) 的「碰撞体」那一层里填内容，不新增决定。

## 13. 与目录表的对齐状态（原「两处过时」已关闭）

本文件 §13 原来给目录 owner 留了两处过时，**2026-11-15 都已关闭**：

1. ~~`PROP_CATALOG.md` §4 第 4 条写「`碰撞体方案` 整列 `待定`」~~ → 已改成「61 行（16 个 def）已按 COLLIDER_LAYER 判据填好，49 行 Cube 占位仍 `待定`」。
2. ~~`视觉模型文件` / `当前 propBounds` / `体积 (m³)` / 三列可吸对 61 行全部过时~~ → 目录表第 2 版已逐行从 `StoryLevel.unity` 重读（`napkin_01` 现在是 0.3×0.028×0.3 = 0.00252 m³，不再是 0.027），并顺带把依赖这些数的 §2 / §3 分析段重算。

**留给下一轮的仍然成立的两件事**（都不在本文写入范围）：

- `PROP_CATALOG.md` §4 第 6 条记录了两件**模型尺寸与设计三轴不一致**的件（`toy_ball` 0.40 m vs 设计 0.15；`napkin` 0.30×0.30 vs 设计 0.15×0.15）。它们已进场景、`propBounds` 没错，**错的是模型或设计三轴**，需要设计侧拍板。
- 24 个 `exports/*.manifest.json` **一个都没有 `collision` 键**（本文 §5.2 的契约尚未实现）。§10-6 的同步工具落地前，流水线必须先把 `collision.boxes` 写出来，否则工具只能空跑。
