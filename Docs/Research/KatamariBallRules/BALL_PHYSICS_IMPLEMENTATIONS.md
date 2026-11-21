# 块魂类滚球的球体物理：怎么做、别人怎么做过

**调研日期：2026-11-15 UTC（项目时区 2026-11-15 Asia/Shanghai）。** 本文回答一个问题：在 **VRChat + UdonSharp + Unity 2022.3** 里，一颗"会滚、会吸、会被吸附物顶得颠簸、要能上坡上台面"的球，物理该怎么实现。
**边界：** 没有实机、没有拆包、没有在 Unity 里跑任何测试。所有结论来自公开文字 + 本仓库已有资料；凡属推断标 `[推断]`。本文不改代码、不改场景，只喂给 [`Docs/Design/BallMotor/TERRAIN_SUPPORT.md`](../../Design/BallMotor/TERRAIN_SUPPORT.md)（撰写中）与后续实现。

---

## 0. 结论速览

1. **我们的球不是"物理没做好"，而是"地形表达力只有一张平面"。** 现有链路（`BallMotionMath` → `SweepMath` → `GroundContactMath`）在数学上是自洽的运动学球：它支持"平地 + 8 平面墙 + 8 轴对齐盒 + 6 挡路盒"，但**支撑面只有一个全局平面**（`groundPlaneNormal`/`groundPlaneOffset`），所以上台阶、上台面、站斜面上在结构上就不可能——不是参数没调好。详见 §1.7。
2. **专利证实了"三模型分离"就是原作的方案**：显示模型 / 碰撞判定模型（等体积球的**外接立方体**，故意保守）/ 滚动模型（16 根固定"针" + 4 根可选"针"），三者分开算。滚动的接地高度由针尖决定，所以吸附物会把球顶起来——这正是评测里"人被卷进去后让球撑杆跳"的来源。`[专利]` §2.1
3. **专利同时给出了两条我们文档里还没有的公式/参数**：① 吸附时固定针的伸长量 `Δr ∝ (d(Ob,Oe) − r)·cos θ`（按夹角余弦衰减，不是硬置 max）；② 吸附门槛是**体积比 < 10%**（把 `ABSORB_SIZE_RATIO.md` 的 10× 从"数据挖掘"升级为"专利明写"）。§2.1
4. **专利没有说的**：台阶、重力/坠落、贴墙爬升、跳跃，全文一句没有。所以"原作怎么上台阶"目前**没有公开依据**，只能当设计选择。§2.1 / §3.1
5. **Udon 下不要用刚体做球。** 官方性能页说 Udon 比 C# 慢 200–1000×；VRChat 官方 bug tracker 上有一条长期问题：`FixedUpdate` 会在一次物理模拟前连跑几十到几百次（实测 28–300 次），靠 `AddForce` 推的球会被累积力打飞。`[官方]` §2.4
6. **推荐路线：继续扩展自写几何（A 案），不换 PhysX 查询。** 决定性的不是性能，而是「我们用写 Transform 移动球」+「球不是球（16 方向壳）」这两条结构性事实——它们让查询路线要么引入未验的 `SyncTransforms`，要么把凹凸永远挡在碰撞之外。**本文初稿推荐 B，读过并行的 `TERRAIN_SUPPORT.md` 后改判 A**，理由与残余分歧见 §3.2–3.3。
7. **外部最值得抄的一条工程做法**：吸附物不做碰撞体，而是**重建球的碰撞外形**（远距离向球心打射线，取最远命中点拼成凸壳）。一个已发布的 Unity 仿作作者说这比"拖着几十个碰撞体跑"快得多。§2.3
8. **最小原型**：一块 5/15/25/35/45° 斜板 + 四级台阶（0.05/0.10/0.20/0.40 m）+ 一个 0.6 m 台面 + 一根 4 m 长杆吸附 + 一堵 0.1 m 薄墙，共 6 个格位；每格有明确的通过判据（§3.4）。
9. **与并行文档只剩一处真分歧**：`TERRAIN_SUPPORT.md` §5.4 选了「没有重力、球永远贴地」，本文倾向「只有下坠与落地、不做跳」。两者各自自洽，但**不能既要永远贴地又要边缘坠落**，必须由 Lead 拍一个。其余决定（A 案、局部支撑面、`k = 0.25·D`、台阶掩码）本文与它一致。

---

## 1. 本仓库已有的（覆盖了什么、结论是什么、缺什么）

### 1.1 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md) §2–3

**一句话：** 定了"一个球体根 + 独立视觉滚动根 + 独立跟随架"的分层，和"16 方向径向代理 + 支持高度 `h=max(r, max_i dot(−n, Q·p_i))`"这条工程近似，并明确写了"扫掠、坡道、台阶、多接触、滚动角速度还需实现和测试"。

- 覆盖：坐标/尺寸命名（§1）、球的分层与"动态 MeshCollider 不当所有附着物的真实刚体外壳"（§2）、成长/吸附/凹凸滚动三件事分开（§3.1）、径向代理的增删与重建（§3.2）、首版推进顺序 A→B→C（§3.3）、视觉吸附与埋藏（§3.4）。
- 结论：**代理只准先影响"表现 + 接地高度"，PVP 一律用球形碰撞半径**，直到一致性测试通过。
- 缺：§3.2 自己承认"该公式只给出支持距离，不是完整动力学解法"。**它没有回答"多个不同高度的支撑面共存时怎么办"**（一张平面以外的地形），也没定义坡度上限、台阶高度、平台边缘与下坠。这就是要补的口子。

### 1.2 [`Docs/Research/KatamariBallRules/`](README.md)

**一句话：** 把 KatamariBall 的计分/兑换/KO/道具规则查清到 `confirmed` 级，但**没有一条讲球体物理**。

- [`README.md`](README.md)：交分必须把球推进"変換エリア"（每 60 秒换位）、交分会缩小球、被大球撞会被吞并异地重生；球内容只通过"体积换算"计分。
- [`ABSORB_SIZE_RATIO.md`](ABSORB_SIZE_RATIO.md)：**拾取判定是体积比 10×，不是最长边**；落到我们头上是 `a·b·c ≤ 0.0524·D³`（包围盒体积形式），并给了"球径 → 能吸什么"的对照表。本文 §2.1 用专利把这条从"数据挖掘"升级到"专利明写"。
- 缺：两份都没有物理/接触/地形内容；`ABSORB_SIZE_RATIO` 的 §9 也明说"没有官方说明或 SDK 级拆包文档"。

### 1.3 [`Docs/Design/BallLayers/`](../../Design/BallLayers/) 各层

| 层 | 一句话（覆盖什么 / 结论 / 缺什么） |
|---|---|
| `GroundContact/`（该层已随 v1 位置权威在 1db240f 移除） | 支持高度求解器：吃核心半径 + 旋转 + 代理点，输出贴地后的球心；有单步升降夹紧（`max_lift_per_step` / `max_drop_per_step`）。**结论：能表达"球芯被凸起顶高"，但输入永远是一张平面 **。缺：多面体/台阶/边缘/下坠。 |
| [`CONTACT_SOURCE_EVIDENCE.md`](CONTACT_SOURCE_EVIDENCE.md) | **本仓库最诚实的证据台账**：明确记着"没有取得原作源码"、"US7402104B2 只是历史专利，不证明新版沿用"，并逐条记下当年 HTTP 结果（Google Patents 200、USPTO PDF 403、GitHub 搜索 0）。**它自己承认只读了聚合页书目，没有读专利正文。** 本文 §2.1 补上了这一课。 |
| [`RadialProxy/`](../../Design/BallLayers/RadialProxy/README.md) | 16 个 Fibonacci 方向 + 每方向一个"端点支持长度" + 最多 4 个长物件（各 8 个 AABB 角点）；已有"吸附只重算外半球、移除按幸存端点重算（不是历史 max）"的正确设计。**结论：B 阶段（驱动接地高度）已接线，C 阶段（进碰撞/PVP）刻意没做。** 方向数已由 32 改为 16（原作滚动模型 16+4，专利证据；32 曾是 provisional），且方向表只是摘要、物理用端点云，所以物理行为不变。缺：不支持"中段凸起"（稀疏采样漏峰值，文件自己也写了）。 |
| `Sweep/`（该层已随 v1 位置权威在 1db240f 移除） | 球 vs 平面 + 轴对齐盒的连续扫掠（TOI），带接触缓冲（容量 4）、去法向速度、`stuck`/`blocked` 信号。**结论：只去法向分量、不做摩擦/反弹**（明标 PROVISIONAL）。缺：任意网格、角点/边接触、多步迭代。 |
| [`Attachment/`](../../Design/BallLayers/Attachment/README.md) / [`SizeModel/`](../../Design/BallLayers/SizeModel/README.md) / [`VisualRoll/`](../../Design/BallLayers/VisualRoll/README.md) / [`FollowRig/`](../../Design/BallLayers/FollowRig/README.md) / [`RemoteProxy/`](../../Design/BallLayers/RemoteProxy/README.md) | 吸附位姿、体积→三尺寸、无滑滚动、跟随架眼点、远端重建。这五层与"地形"无关，不展开；只提醒 `VisualRoll` 吃的是**实际位移 + 支撑法线**，一旦改成多支撑面，法线来源要跟着换（见 §3.5）。 |

### 1.4 [`Docs/Design/BallMotor/README.md`](../../Design/BallMotor/README.md)（装配层）

**一句话：** 唯一知道"每步按什么顺序调谁"的地方；9 步固定顺序（输入→平面运动→扫掠→接地→拾取→账本→尺寸→滚动→跟随），状态全在 `KatamariBallMotor`。

- 结论：**"装配层只搬运、不算数学"**；失败即不应用；尺寸每步重算不累加；远端球永不进主链。径向代理的接线（第 4 步喂代理点、第二次调用喂空数组给镜头）已经落地为源码。
- 缺（对本文最要紧的）：第 4 步的输入是**一张平面**（`groundPlaneNormal` / `groundPlaneOffset`），第 3 步的障碍是**有限容量数组**（`MaxPlaneCount = 8` / `MaxBoxCount = 8` / `MaxBlockingBoxCount = 6`）。**"站在台面上"需要"支撑面不再是一张平面"，这要改的是第 4 步的接口，不是调参。**
- 另一处已记录的坑：`maxProxyLiftPerStep = 0.04 m`、`maxProxyProtrusionWorldM = 0.30 m`、`maxProxyProtrusionPerCoreRadius = 0.35` ——"长物件把球弹上天"这个风险**已经被三道上限挡住了**，本文不再重复发明，只在原型里验证这三个值够不够。

### 1.5 [`Docs/Design/BallMotion/README.md`](../../Design/BallMotion/README.md)

**一句话：** 平面运动参考模型（加速度/制动/指数摩擦/转向率/成长降额），**垂直方向完全没有**：`velocity.y` 不参与、`displacement.y` 恒 0，重力/坡面/墙法向去速都在别人那里。

- 结论：已移植成 `BallMotionMath.TryStep` 并写了 NUnit。
- 缺：文件里自己列的"未移植"包括 `test_slopes_5_to_15_degrees_stay_supported_and_stable`——**坡面稳定性从来没有被这层实现过**。这与本文 §3.3 的建议直接相关。

### 1.6 [`Docs/Archive/Planning/BALL_WORKFLOW_REPLAN.md`](../../Archive/Planning/BALL_WORKFLOW_REPLAN.md)

**一句话：** 把球拆成 A（接触与外壳）/ B（成长与附着）/ C（表现与跟随）三套并行流水线的历史计划；里面留了一份"当时已知缺陷"清单，今天仍然有效。

- 仍有效的三条：T49"中心高度只能升不能降，移除长杆可能悬空"（已修）；T50"半球邻域过粗"；T54"初始嵌入、stuck 误判、保守盒角"。
- 也定了**三个 Unity 测试对象**的原始设想："球与斜面支持台""薄墙/墙角轨迹台""小棍方向/端点可视台"——本文 §3.4 的测试场就是它们的扩展版，可以直接复用名字。

### 1.7 代码的真实能力边界（一句话表）

| 能力 | 现状 | 卡在哪 |
|---|---|---|
| 平地滚、转向、摩擦、成长降额 | ✅ 有 | — |
| 撞墙不穿、去法向速度 | ✅ 有（平面 + AABB） | 只有 8+8+6 个手注册的障碍 |
| 被吸附物顶高（凹凸接地） | ✅ 有（B 阶段） | 只有"正下方一张平面"这一种支撑 |
| **上坡** | ❌ 无 | `GroundContactMath` 的平面是全局常量；`BallMotionMath` 没有 `vy` 通道 |
| **上台阶** | ❌ 无 | 扫掠把台阶当墙（竖直法线 → 去速），没有 step probe |
| **站在台面上** | ❌ 无 | 支撑面不能是"局部有限面"，也没有边缘/下坠 |
| 与真实地形网格（MeshCollider）交互 | ❌ 无 | 没有任何引擎几何查询 |
| 吸附物把球弹飞（"撑杆跳"） | ⚠️ 被三道上限压住 | 上限是拍的，没有实测 |

### 1.8 仓库里"已有但没人读"的证据缺口（本文补的）

`CONTACT_SOURCE_EVIDENCE.md` 明确写着：Google Patents 页 HTTP 200，但**只读了书目**；USPTO PDF 403。也就是说**专利正文在本仓库从未被读过**。本文 §2.1 读了 US7402104B2 的说明书与权利要求（经 Google Patents 与同族 JP4316334B2 英文机翻），下面的数字都是从那里面抠出来的。

---

## 2. 外部资料

> 可信度图例：**[官方]** 厂商/引擎/平台自己的文档；**[专利]** 专利说明书或权利要求；**[数据挖掘]** 社区从游戏数据反推；**[社区]** 玩家/开发者帖；**[推断]** 本文自己的推理。

### 2.1 原作相关：专利 US7402104B2（Namco，2003 申请 / 2008 授权）

- 主文献：[US7402104B2 · Google Patents](https://patents.google.com/patent/US7402104B2/en)（申请 US10/944,200，2004-09-20 申请，优先权 2003-09-25，发明人 Ogita Masatoshi / Yamabe Kazumi / **Takahashi Keita**，2008-07-22 授权，状态 Expired）。
- 同族日文版（英文机翻可读，正文更全）：[JP4316334B2 · Google Patents](https://patents.google.com/patent/JP4316334B2/en)。公开前的美国申请版：[US20050101365A1](https://patents.google.com/patent/US20050101365A1/en)。
- 二手综述（可读性最好、不替代原文）：[Patent Arcade 条目](https://www.patentarcade.com/video-game-patent/US7402104/game-performing-method-game-apparatus-storage-medium-data-signal-and-program)。
- 可信度：**[专利]**。**它证明 2003 年 Namco 申请过这套做法，不证明零售版或 2025 新版沿用**；也不构成法律意见。

**它到底说了什么（按对我们的用处排序）：**

1. **三个模型分开算，理由是省算力。** 原文把 `Mp`（显示模型）、`Mh`（碰撞判定模型）、`Mr`（滚动模型）并列，说"碰撞由 Mh 判、不规则颠簸滚动由 Mr 算、画面由 Mp 显示"，因此"形状错综复杂不规则的球的移动控制处理负荷下降"。`[专利]`
   → **这就是我们 §2 分层的原始出处**，我们不是自创。
2. **碰撞判定模型是一个故意保守的盒子，不是球也不是凹凸壳。** 原文：`Mh` 是"**一个边界盒（bounding volume），即一个立方体，等体积的球内切于它**"；并补一句"本实施方式中 Mh 是所谓 boundary box，也可任意设定"。`[专利]`
   → 含义：**碰撞是廉价保守的，颠簸只进滚动/接地**。这给我们"PVP 与墙面继续用球形半径"提供了强依据，也说明"球用外接盒"在原作里是**故意更保守**（角上比球大 √3 倍），不是没做。
3. **滚动模型的针数是 16 + 4，不是我们此前的 32。** 原文：固定针 `Af` 数量为 **16**，可选针 `Am` 数量为 **4**；"球滚动模型 Mr 在固定针端点 `Tf` 与可选针端点 `Tm` 作为接地点（ground points）的状态下滚动"。`[专利]`
   → 我们的方向数已按这个量级定为 16；**物理不因此改变**，因为方向表只是摘要、每步接地高度用端点云（见 `RadialProxy/README.md`“为什么是 16”）。此前多一倍只是保险，不是测量结果。
4. **吸附时针的伸长量是一个平滑衰减公式，不是硬置 max。** 原文：以球心 `Ob` 为原点、被吸附物中心 `Oe` 的极角 `θe` 为基准，**只有落在预定角度范围 `θj` 内的固定针**才伸长；伸长量 `Δr` 对应"到 `Oe` 的距离"，**越靠近吸附位置越大、越远越小**，等价于 `Δr ∝ (d(Ob,Oe) − r)·cos θ`（θ 为该针与 `θe` 的夹角差；具体算式在专利里是公式图）。`[专利]`
   → 我们现在是"端点支持长度取 max"，等价于 `cos θ ≈ 1` 的极端化版本；**平滑衰减会让球在被吸物的边缘有一个过渡坡**，比硬台阶更像原作，也顺便减少"一个点把球顶起来"的抖动。这是本文**最建议抄的一条**。
5. **可选针（长物件）有三个条件，和我们现在的长物件准入几乎一一对应：** ① 每个物件预先作者标定"外缘代表点 `Q`"（我们：8 个 AABB 角点）；② 按到球心距离**降序取预定个数**（我们：≤4 个候选）；③ **只当该点的极半径大于相邻固定针的半径时才设可选针**（我们：`minProxyProtrusionM = 0.01` + 突出量夹紧）。`[专利]`
   → 说明"长物件单独一条通道"是对的；差别只在"预标定代表点 vs 运行时 AABB 角点"——后者更省作者工时，前者更准。
6. **吸附门槛是体积比 10%，明写在说明书里。** 原文：当被撞物件体积设定值 `De` 与球当前体积 `Db` 之比**小于吸附基准值 10%** 时执行吸附。`[专利]`
   → 直接把 [`ABSORB_SIZE_RATIO.md`](ABSORB_SIZE_RATIO.md) 的 10× 从"数据挖掘/社区推导"升级为 **专利明写**。这条对物理没影响，但说明"球的大小决定能吸什么"是同一个模型里的量。
7. **"能不能上坡"在原作里是靠速度门槛，不是靠坡度上限。** 原文：关卡 `ST1..ST3` 之间"由具有预定坡度的斜坡 `SL` 分隔"；**"球要爬上斜坡需要预定的速度；把球吸大变大使最大速度参数变大，就能爬上斜坡"**；行进参数包括**最大速度、加速度、（还可能包括）惯性**，且这些参数**随体积 `Db` 增大而增大**。`[专利]`
   → **这是本文对"上坡"最重要的结论：原作没有 `slopeLimit` 这种硬上限，而是"速度够就上、不够就上不去"**。我们的 `BallMotionMath` 已经按 `growthFraction` 降额速度上限，方向和原作**相反**（原作是球越大越快）。这一点需要在设计上明确选边（见 §3.5）。
8. **移动是参数化运动学，不是刚体。** 原文：滚动控制单元"计算球心 `Ob` 的移动量和以 `Ob` 为原点的滚动量（旋转量）"，并按最大速度/加速度/最小转弯半径等行进参数控制。`[专利]`
   → 位置与旋转分开积分 = 我们的 `BallMotionMath` + `VisualRollMath`，架构一致。
9. **性能优化：按显示层级整批剔除。** 原文：每个物件有"显示层级"，相机切换时比较当前显示层级；**不显示的物件不仅不画，连碰撞判定和吸附判定都不做**。`[专利]`
   → 对我们的启示：支撑面候选与可吸物件分类也应该先按"层级/距离"整批剔除（A 案里就是"别把全场景盒都塞进每步的比较"），而不是"每步全量筛一遍"。

**专利没有说的（重要，别外推）：**

- **没有台阶（step/stairs/ledge）**：全文检索不到台阶、上下缘、落差。唯一的"高差"就是斜坡 `SL`。
- **没有重力/坠落/腾空**：没有 gravity、fall、jump、airborne。专利里的球是**贴地滚动**的（这也是它不需要垂直动力学的自洽之处）。
- **没有贴墙爬升**：没有 wall climb / vertical surface 相关描述。
- **没有"球与地形网格"的算法**：碰撞判定是"与放置物件 `E` 的碰撞"，地形只是分层的斜坡。
- **数值几乎全缺**：16/4 根针、10%、`θj` 的"预定范围"是仅有的几个数；坡度角、台阶高度、速度量级都没有。

> 结论：**专利能证明"凹凸滚动 = 针模型 + 独立碰撞盒"这套方案是原作的**，但**不能**证明"原作怎么上台阶"。谁要说"原作台阶是 X"，必须有别的证据。

> 观感对照（吸附物把球芯盖住 vs 我们的红球露芯；含原版两帧与我们的截图）：[`Docs/Evidence/BallPresentation/2026-11-15_attached_props_vs_original/`](../../Evidence/BallPresentation/2026-11-15_attached_props_vs_original/README.md)。

### 2.2 原作行为的社区观察（能对上的现象）

| 来源 | 说了什么（自己的话） | 可信度 | 对我们的启示 |
|---|---|---|---|
| [Honest Gamers 评测](http://www.honestgamers.com/12930/playstation-2/katamari-damacy/review.html) | 球在**斜面上会打滑、会失控**；吸到的东西真的粘在球上会变成麻烦：**"人被脚朝里卷进去后，会从球上凸出来、随着球滚动戳进地面，让球撑杆跳（pole-vault）"**；只在一侧吸一堆小东西会让球偏。 | [社区]（评测，2004） | **这是"凹凸滚动"最生动的外部证据**，而且它描述的是**位置被顶起来**（我们 B 阶段做的事），不是速度被弹飞。也印证"左右不对称会让球跑偏"是一件**真实存在**的手感，不是 bug。 |
| [Steam 社区：Difficulties rolling up walls](https://steamcommunity.com/app/848350/discussions/0/1639790664936274066) | 玩家抱怨**贴墙爬上去很难**：好不容易开始爬，中途会"撞到什么东西"被弹开；回帖说"这游戏一直这样，是游戏的问题不是你"。 | [社区]（低权重，REROLL） | 原作**有**贴墙爬升这种涌现行为，但**不稳定、不可控**。我们**不应该**为了"还原"去实现贴墙爬升——它是原作碰撞近似的副产物。 |
| [GamesRadar 评测](https://www.gamesradar.com/katamari-damacy-review/) | 双摇杆"像坦克履带那样"分别控制左右，"这套系统提供的**平衡感与惯性**让游戏有了触感"。 | [社区]（媒体） | 惯性是**设计目标**，不是物理副产品 → 运动学里显式的 `acceleration`/`braking`/`inertia` 参数是对的（专利也把 inertia 列进行进参数）。 |

**没找到的：** 没有任何公开来源给出"原作能翻过多高的台阶""最大爬坡角度""吸附物让球升高多少厘米"这类数值。社区里关于"被台阶卡住"的具体讨论也没有找到可靠的一手记录。`[未找到]`

### 2.3 开源 / 教程实现（别人怎么绕开这些坑）

1. **[Matthew Consterdine, "Bring the Light" 附带的一个 Katamari 克隆](https://consto.uk/2018/10/07/bring-the-light)**（源码：[mattconsto/rolling-rolling-rolling](https://github.com/mattconsto/rolling-rolling-rolling)）
   - 做法：**用扭矩滚球（刚体）**；吸附时把物件设为球的子物体、**禁用物件原碰撞体**，然后**重新生成球自己的碰撞网格**：从"大约无限远（他说 10000 单位）"朝球心打射线，记录与球及其所有子物体的命中，**取离球心最远的那个点**拼成碰撞网格。
   - 他自评："更复杂，但**比拖着一堆碰撞体跑的性能好得多**"；射线的方向必须是"**从外向内**"，因为从球内出发会先命中球自己（距离 0）。
   - 可信度：[社区]（个人项目 + 自述，未给帧率数据）。
   - **启示：这是"径向代理"的碰撞体版本。** 我们不需要生成 MeshCollider（Udon 里重建 Mesh 不现实），但"**从外向内、取最远命中**"和"**吸附物自己不要碰撞体**"这两条直接可用：前者就是我们 `TryDirectionLengthPoint` 的几何，后者我们已经在做（吸附 Collider 退出地图判定）。
2. **[Unity Discussions, "Katamari Experiment"（2007）](https://discussions.unity.com/t/katamari-experiment/377199)**
   - 做法/坑：直接 `SetParent` 会让物件"**飘在球外面绕着转**"，因为**球在物件真正被挂上之前已经滚过去了**；结论是"不需要任何 joint，去掉物件的刚体、直接挂"。
   - 可信度：[社区]（很老，但坑是永恒的）。
   - **启示：** 吸附位姿必须用**接触时刻的世界位姿**换算成球的局部位姿——我们合同 §3.4 已经这么写了；这条 2007 年的帖子说明"用吸附瞬间的位姿"不是洁癖，是唯一能让物件贴在球面上的做法。
3. **[Shumpei Morimoto, "Sticky Ball Game"（Tartu 大学课程项目，2021）](https://courses.cs.ut.ee/2021/cg-pro/spring/Main/Project-StickyBallGame)**
   - 做法：`AddForce` 滚球 → 觉得"惯性太大不好控"→ 改成**匀速移动**；吸附时"**移除物件的刚体和碰撞体，把 parent 设成球**"；拾取门槛用**球碰撞半径**；"爬墙"一直列在可选目标里，**最终没做**。
   - 可信度：[社区]（学生作业，工程细节少）。
   - **启示：** 一个典型 hobby 实现的终点就是"我们现在的状态"——能吸、能滚、**没有台阶没有坡**。这不是我们特别落后，是这条路的自然尽头；要继续往上必须换地形表达方式。
4. **[HugheZ/Communisuto-Damacy](https://github.com/HugheZ/Communisuto-Damacy)**
   - 做法/特点：README 写"拿特殊的'不规则'物件会让球的**滚动方式发生变化**"（grows and rolls differently）。
   - 可信度：[社区]（README 自述，未读源码）。
   - **启示：** "不规则物件改变滚动"被独立实现者当成一个**值得做的特性**，说明它不是我们独有的洁癖；但没人给出实现细节。
5. **Godot / Unreal 侧**
   - Godot 论坛的 [ShapeCast3D collide-and-slide 求助帖](https://forum.godotengine.org/t/help-with-shapecast3d-collision-and-sliding/110863) 和 [Katamari 式移动的讨论](https://forum.godotengine.org/u/bantarpar/activity/replies) 显示社区同样是"刚体滚球"起步、卡住后转向"形状投射 + collide and slide"。
   - **Unreal 侧没有找到成型的 Katamari 克隆教程。** `[未找到]`
   - 可信度：[社区]。

**横向结论（三条路线的实际分布）：** 能找到的 hobby 实现**清一色是刚体球 + 吸附时去碰撞体 + 父级挂载**；一旦要做"上台阶/上坡/不穿墙"，**要么放弃（多数）、要么换成形状投射的 collide-and-slide**。没有找到任何公开实现抄了专利的针模型。

### 2.4 VRChat / Udon 侧

**能用的物理 API（有冲突，必须实测确认）**

| API | 本地创作者知识库的说法 | 交叉核对 | 结论 |
|---|---|---|---|
| `Physics.Raycast` | "⚠️ 部分可用" | VRChat 官方 Canny 上有一个 2020 年的 bug 报告，直接引用 Udon 的 extern 名 `UnityEnginePhysics.__Raycast__...`，说明该 extern 存在 | **可用**，但历史上有过重载/输出参数编译 bug（该报告写"下个 SDK 版本修复"） |
| `Physics.SphereCast` | "❌ NOT EXPOSED" | 同一份 Canny 报告说"用 raycast 和 **spherecast**"测过，并且有 `SphereCast` 节点 | **很可能可用**；知识库那行**大概率是错的**（它是社区快照，且把"替代方案"写成 `VRCContactReceiver`，明显不是同类 API） |
| `Physics.SphereCastAll` / `OverlapSphere` | 未列 | 无直接证据 | **待确认** |
| `Physics.ComputePenetration` | 未列 | Unity 文档：一个碰撞体必须是 Box/Sphere/Capsule/凸 MeshCollider，另一个任意；**不要求先更新空间结构，可在 `FixedUpdate` 之外调用** | 语义上非常适合我们（"把球从地形里推出来"），但**Udon 是否暴露未知** |
| `Collider.ClosestPoint` / `ClosestPointOnBounds` | 已列出（`Collider` 的方法表里有 `Vector3 ClosestPoint(Vector3)`） | Unity 文档一致 | **可用**（若 Udon 侧确实按该表暴露） |
| `Rigidbody` | 白名单组件；**VRC Object Sync 会把远端刚体强制 kinematic**；Udon 直改 `isKinematic`/`useGravity` 与同步"打架"不可靠，要走 `SetKinematic`/`SetGravity` | 官方白名单 + ObjectSync 页 | 可用，但**网络下不能当权威模拟** |

> **怎么一次问清：** 在 Unity 里打开 **`VRChat SDK` → `Udon Sharp` → `Class Exposure Tree`**（[官方说明](https://creators.vrchat.com/worlds/udon/udonsharp/class-exposure-tree)，绿=暴露、红=不暴露），搜 `Physics`、`Collider`、`RaycastHit`，把 5 个方法的绿/红截图记进 `Docs/Evidence/`。这是唯一权威答案，**本文不敢替它下结论**。在没确认之前，`SweepMath` 的自写几何是**唯一确定可用**的路径——这也是 §3.3 建议保留它的原因。

**Udon 性能**

- 官方 [Performance Tips](https://creators.vrchat.com/worlds/udon/udonsharp/performance-tips)：**Udon 跑同样代码比 C# 慢约 200×–1000×**；"避免每帧遍历很长的数组"（>建议分帧）；`GetComponent<T>` 慢，只在 `Start` 里拿；跨 Behaviour 调用比本地调用慢。可信度：**[官方]**。
- 官方 Canny：[Udon 的 `FixedUpdate()` 在一次 Unity 物理模拟之前会跑多次](https://vrchat.canny.io/udon/p/udons-fixedupdate-runs-multiple-times-before-any-unity-physics-simulation-is-don)（单客户端实测 28–32 次，双客户端从机 180–300 次），导致 `AddForce` 累积、物体被弹飞。可信度：**[官方 bug tracker]**（社区报告 + 官方未关闭）。
  → **这条直接判了"刚体球 + AddForce"在 Udon 里的死刑**：你没法保证"一次物理步对应一次你的推力"。
- **"每帧能承受多少次物理查询"没有公开数字。** 官方只给了 200–1000× 的定性倍率。`[未找到]`
  → 只能自己量：建议在原型里做「每步成本计数器 + 100 物件压力场景」，把 P95 单步耗时与帧时间记进 `Docs/Evidence/`（与 `02_TECHNICAL_CONTRACTS` §9 的要求一致）。

**VRChat 里的滚球先例**

- [鉄球転がしゲーム【Udonギミック】(Booth, ¥700)](https://booth.pm/ja/items/4150960)：把球从棒上滚进筒里的 Udon 小游戏，**球的位置、棒的位置、分数都同步**；更新日志修过"球卡在舞台左后方"的 bug，并加了重置功能。可信度：[社区]（商品页）。**启示：** ①"滚球 + 同步位置"在 Udon 里是有人做成商品的，可行；②作者踩的坑正是"**卡住**"和"**需要重置**"——我们的 `stuckSeconds` + `TeleportTo` 恢复路径不是过度设计。
- [VRCreators 社区资源列表](https://vrcreators.net/resources/vrchat-community) 里列了一个 "VRChat 8ball — Udon-powered 8-ball and pool physics project"，说明"Udon 里做球类物理"有先例，但**没有找到**任何公开的"VRChat 里的块魂"或"带吸附物的滚球"实现。`[未找到]`

### 2.5 通用技术：球形角色控制器 / step-up / slope limit

| 来源 | 说了什么 | 可信度 | 对我们的启示 |
|---|---|---|---|
| [Unity 手册：Character Controller 组件](https://docs.unity3d.com/2022.3/Documentation/Manual/class-CharacterController.html) | `Slope Limit`：角色**不会爬**比它更陡的坡；`Step Offset`：**只有当台阶比这个值更矮时才迈上去**，且不应大于角色身高（否则报错）；`Skin Width` 建议取半径的 10%；`Min Move Distance` 建议 0。 | [官方] | 这是**业界对"台阶/坡度"的默认答案**：不是一个物理现象，而是两个**显式阈值**。我们的球应该照抄这个结构（`stepHeight` + `slopeLimit`），而不是指望扫掠自己涌现出来。`Skin Width = 0.1r` 也给了我们 `separationEpsilon` 的量级参考。 |
| [gamedev.stackexchange：collide-and-slide 怎么做](https://gamedev.stackexchange.com/questions/214442/how-do-i-implement-the-collide-and-slide-algorithm-in-unity) | `CharacterController.Move()` 就是"沿整个位移做 collide-and-slide，直到走完或**被太陡的坡、太高的台阶、或最大滑动迭代次数**挡住"；胶囊/球可以直接用它；自己写的话要迭代"投射→沿接触面投影剩余位移"。 | [社区]（高赞，作者是长期答主） | **最省事的路线其实是 `CharacterController`**，但它在 Udon 里能不能用、能不能被我们接管位置（我们要自己算滚动与接地）都是未知；**建议把它列为"待测捷径"**，而不是主路线。 |
| [Unity 文档：`Physics.ComputePenetration`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Physics.ComputePenetration.html) | 计算把两个碰撞体分开的最小平移；**不要求先更新空间结构，因此可在 `FixedUpdate` 之外调用**；忽略背面三角形。 | [官方] | 如果 Udon 暴露它，这就是"**球被地形卡进去时的一键推出**"，比我们手写盒推出稳。 |
| [Unity 文档：Rigidbody Collision Detection](https://docs.unity3d.com/2022.3/Documentation/Manual/class-Rigidbody.html) | `Continuous` / `Continuous Dynamic` / `Continuous Speculative` 可防高速穿透，**但只支持 Sphere/Capsule/Box**。 | [官方] | 用刚体路线时，薄墙必须靠 CCD；而 CCD **对 MeshCollider 无效**——又是一个"自写扫掠更可控"的理由。 |

---

## 3. 结论

### 3.1 原作球体物理：可确认 vs 推测

**可确认（专利明写，`[专利]`）：**

1. 显示 / 碰撞判定 / 滚动是**三个不同模型**，分开算以省算力。
2. 碰撞判定用**保守盒**（等体积球的外接立方体），不是球、也不是凹凸壳。
3. 滚动模型 = **16 根固定针 + 4 根可选针**；接地高度由针尖决定，因此吸附物会**顶起**球。
4. 固定针的伸长量按"到被吸物中心的距离 + 夹角余弦"**平滑衰减**，且有预定角度范围 `θj`。
5. 可选针（长物件）有"预标定外缘点 / 取最远 N 个 / 只在超出相邻针时才设"三条约束。
6. 吸附门槛 = **体积比 < 10%**。
7. 行进参数（最大速度、加速度、惯性、最小转弯半径）**随体积增大而增大**；**上坡靠"速度够不够"**，不是靠坡度上限。
8. 移动量（球心平移）与滚动量（绕球心旋转）**分开计算**。
9. 不显示的物件**连碰撞与吸附判定都不做**（按显示层级整批剔除）。

**强推断（有间接证据、但专利没直说，`[推断]`）：**

1. 原作**没有**刚体动力学意义上的重力/坠落——专利全文无 gravity/fall；这与"球永远贴地滚动"的自洽。若真是这样，原作里的"跳/掉"是脚本化的，不是物理。
2. 原作**没有**"台阶"这个概念。地图的高差用斜坡表达；玩家感知到的"能上去"，多半是**斜坡 + 速度门槛**，不是 step-up。
3. 评测里"撑杆跳"是**位置通道**的现象（球心被顶高 + 滚动轴突变），不是速度被弹飞。这与我们只给 B 阶段（位置）不给速度扰动是吻合的。

**明确不知道（`[未找到]`）：**

- 坡度角的数值、台阶高度的数值、球速量级、`θj` 的具体角度、针的初始长度与球半径的关系。
- 零售版/REROLL/2025 新版是否**真的**用了专利这套（专利 ≠ 产品实现）。
- 任何"原作能过多高的台阶"的实测。

### 3.2 三条路线对比（在 Udon 下）

> **本文初稿推荐 B；读过并行的 [`TERRAIN_SUPPORT.md`](../../Design/BallMotor/TERRAIN_SUPPORT.md) §0.3–0.6 后改判为 A。** 改判理由写在表下面，不是"听谁的"，而是它补上了本文漏掉的一个结构性事实（见「改判理由」第 1 条）。

| 维度 | **A. 自写运动学 + 自写几何**（现状，扩展后） | **B. 自写运动学 + 引擎几何查询** | **C. 刚体物理（Rigidbody + AddForce/扭矩）** |
|---|---|---|---|
| 地形表达力 | 平面 + AABB +（扩展后）局部支撑面/斜面/道具顶面。**曲面/凹结构表达不了** | **任意 MeshCollider**（滑梯、碗、隧道） | 任意（PhysX 全量） |
| 上台阶 | 可做，但必须是**显式规则**：从扫掠里剔除"可上盒"（掩码）+ 给一次独立大抬升上限 | 可做（命中法线朝天 + 高度差 ≤ limit 则允许进入） | 会"爬"但不稳（棱上卡、抖、弹） |
| 上坡 | 可做（一个局部平面 + 坡度上限）；**球不会自己滑下坡**（没有切向重力） | 可做（Collider 天生带朝向） | 会滚下去，需要持续扭矩 |
| 球的形状（凹凸进碰撞） | **能**：碰撞外形就是 16 方向径向壳 + 长物件角点，未来 C 阶段直接可用 | **不能**：`SphereCast` 只投一个半径；凹凸永远进不了水平碰撞 | 需要真 Collider ⇒ 几十个 Collider 拖在球上 |
| 一次查询拿到的信息 | TOI、最终位置、剩余位移、滑向、`stuck`、每条接触的法线/时间/穿透深度 —— **是一个"解"** | 命中点 + 法线 + 距离 —— **是一个"命中"**；墙角多接触要自己再写求解器 | 物理引擎内部解，但拿不出来、也改不了 |
| 确定性 / 权威 | **最好**：纯函数 + 定步长 + float32，可离线复现 | 结果是 **PhysX 场景状态**的函数，不是我们的状态 | **最差**：ObjectSync 把远端强制 kinematic；Udon `FixedUpdate` 一次物理前跑 28–300 次 |
| 与"我们用写 Transform 移动球" | 无冲突（几何是我们的数组） | **结构冲突**：查询看物理场景不看 Transform，要正确就得 `Physics.SyncTransforms()`（暴露未验）或接受一步滞后 | 冲突小（刚体自己走物理步），但那是另一个权威 |
| Udon 成本 | 低：纯算术，无 extern；每步 ≤8 平面 + ≤8 盒 + ≤6 挡路盒，**典型支撑面只有 1–3 个** | 中：每步数次查询（引擎级 µs，但每次跨 Udon extern） | 高：每帧施力 + 同步 |
| 可离线测试 | **全套**（NUnit + Python 参考模型 + `Docs/tests` 9 个套件） | 核心几何只能在 Unity+PhysX 里验 | 几乎不可测 |
| 实现工作量 | 增量（`SweepMath`/`GroundContactMath` 一行不改） | 重写扫掠 + 接地 + 挡路 + 三层分离 + 测试 | 看似最小，实则三座山 |
| 适合谁 | **我们要的**（有坡、有台面、有凹凸球） | 关卡必须曲面/凹结构，且愿意放弃"凹凸进碰撞" | 单人、非网络、可接受抖动 |

**改判理由（为什么不是 B）：**

1. **B 与我们"用写 Transform 移动球"这件事结构冲突。** 引擎查询读的是物理场景，不是 Transform；要么每步 `Physics.SyncTransforms()`（Udon 是否暴露未验，且它自己有开销），要么接受"查询看到的是上一步的球"。这一条本文初稿完全没有考虑，是并行的 `TERRAIN_SUPPORT.md` §0.4 第 3 条提出来的。
2. **B 会把球锁死在"球"上。** `SphereCast` 的入参是一个半径；我们的球是 16 方向壳，`maxProxyProtrusionWorldM = 0.30` 的凸起是合同 §3.3-B 已实现、C 阶段要接进碰撞的东西。选 B = 放弃用户说的"球体物理本身就是核心技术"里的那一半。
3. **B 真正的两个好处，A 已经用更低代价拿到了**：①"作者用真 Collider 摆几何"——`LevelArenaHost` 在 `Setup` 时把 `BoxCollider.bounds` 烘焙成 min/max（Lab 已在这么做），**不需要运行期查询**；②"任意地形"——只有**曲面/凹结构**才真的需要 PhysX，而那不是首版关卡的需求。
4. **B 的隐含前提没验**：`Physics.SphereCast` / `OverlapSphere` / `ComputePenetration` 在 Udon 里是否暴露，三份二手资料互相矛盾（§2.4）；而 A 的路径**完全不依赖**这个未验事实。

**一句话取舍：** C 死在**权威性**（Udon 里没有可靠的"物理步"边界）；B 死在**形状与场景同步**（球不是球 + 查询看的是物理场景）；**A 的"表达力不足"是可以靠扩展几何补的，B 的两条是结构性补不了的。** 所以：**A 为默认，B 只在"关卡必须有曲面/凹结构"时重评。**

### 3.3 推荐路线（一段话）

**走 A：把自写扫掠的"地形来源"从"一张全局平面 + 手注册盒"扩成"每步在球心正下方选出的一个局部支撑面"，支撑面来源四类（地面/静态盒顶/斜面/可吸结构顶面），台阶靠"升幅 ≤ `k·D` 的盒从扫掠里剔除并转成支撑候选"这一条规则同时解决"上台阶"和"矮件隐形墙"；斜坡是一个带朝向的局部平面，坡度硬上限由单步升降钳位反推（不是拍的），球不会自己滑下坡因为 `BallMotionMath` 本来就没有切向重力。** 这套展开在 [`TERRAIN_SUPPORT.md`](../../Design/BallMotor/TERRAIN_SUPPORT.md) §3–§7 已经写全（含 `k = 0.25 × 语义直径`、`maxStepUpLiftPerStep` 与 `maxProxyLiftPerStep` 的冲突处理、三条防抖），本文不重复、只做两件事：给它补**专利侧的旁证**（§2.1 的"碰撞盒/滚动壳分离"与"速度门槛上坡"），以及给它补**外部实现与 Udon 约束**（§2.3–2.5）。**唯一还要提醒的一条**：A 的天花板是"曲面/凹结构"，若关卡设计出现滑梯、碗、隧道，先按 `TERRAIN_SUPPORT.md` §0.5 的"高度场逃生舱"评估，再决定要不要重开 B 的账。

**A 案可以立刻吸收的两条专利细节（成本很低）：**

- **吸附造成的支撑隆起，用夹角余弦平滑，而不是"取最大值"。** 专利的固定针伸长量随「该针与吸附方向的夹角」衰减（§2.1 第 4 条），我们现在是 16 个方向各自独立取支持长度——同一颗被吸物会在相邻几个方向上留下**同高的硬边**。把每个方向的 `support_i` 与"该方向到吸附点的夹角"做一次平滑（或只对夹角 < 30° 的方向做加权），能让球爬过被吸物边缘时**没有台阶感**，代价是每方向一次 `dot` + 一次插值，A 案完全承受得起。
- **方向数已定为 16，不是待测的 32/16 取舍。** 依据有三条：① 这张方向表只是"球+长物件包络"的摘要，每步接地高度用端点云，**物理行为与方向数无关**；② 原作滚动模型用 16 根固定针 + 4 根可选针就做出了手感（`[专利]`，US7402104B2）；③ 16 是合法的 Fibonacci 单位集，最坏角隙从 32 的 27.38° 增到 38.88°（2° 经纬网格采样；加密复核 27.63°/39.04°，结论不变），只让摘要表在尖刺落在两方向正中时低估 `cos 38.9° ≈ 0.78`。此前 32 是 provisional 选择，不是原作值。C#/Python 各只有一处方向数常量，端点池的 32（4×8）是另一个 32、不动。

### 3.4 最小可验证原型：一个 6 格位的测试场

> 目标不是"好玩"，是**用最少的格子把"我们到底会不会做地形"这件事证伪**。建议放在 `Assets/VRC_Katamari/Labs/BallTerrain/`（沿用 `BALL_WORKFLOW_REPLAN` 里"独立实验场景、不进正式场景"的规矩），球用同一个 `KatamariBallMotor`，只改 `k`（台阶系数）与球径两档；不换架构。
> 统一约定：球径 **0.30 m（起始）/ 1.00 m（大球）** 两档各跑一遍；每格长 8 m 宽 4 m；地面材质同一张；每格起点有箭头与刻度。

| # | 格位 | 规格 | 通过判据（可量化） |
|---|---|---|---|
| 1 | **斜板组** | 5° / 15° / 25° / 35° / 45° 五块，各 8 m 长，顶端接平段 | 5°/15°：静止**不下滑**，能匀速上行；25°：能上，但松手会缓慢下滑（若接受"无重力"模型则不下滑——**两种都算通过，但要记录选了哪种**）；45°：**不得穿模**，要么上不去、要么明显减速 |
| 2 | **台阶组** | 单级台阶 0.05 / 0.10 / 0.20 / 0.40 m，各一格 | 0.05 / 0.10（≤ `stepHeight`）：**不减速通过**，球心单步抬升 ≤ `maxStepLiftPerStep`，滚动不停顿；0.20 / 0.40：**被挡住**（`blocked=true`）且**不隧穿**、不抖动、不卡死超过 1 s |
| 3 | **台面 / 边缘** | 0.6 m 高、4×4 m 台面，一侧有 30° 斜坡上，另一侧是直角边缘 | 能从斜坡上台面并在台面上**稳定停留**（`grounded` 持续为真、高度不抖）；从边缘走出去要**开始下坠并落地**（若走"无重力"模型则贴边滑下——记录选择） |
| 4 | **长物件吸附** | 4 m 长杆 + 2 m 平板 + 一个 1 m 立方，先后吸附到 0.3 m 球上 | 球心被顶高但**单步 ≤ `maxProxyLiftPerStep`（0.04 m）**、总突出 ≤ `maxProxyProtrusionWorldM`（0.30 m）；**不出现"球被弹飞"**（速度扰动为 0）；杆扫过地面时滚动角速度有可见起伏（撑杆跳的"位置版"）；移除杆后球心**回落**且不悬空 |
| 5 | **薄墙 / 墙角 / 卡住** | 0.1 m 厚墙；90° 内角；两个盒子夹出的 0.4 m 缝隙 | 高速（4 m/s）撞薄墙**不穿**；内角**不抖不弹**；夹缝里 `stuckSeconds` 累计后**能恢复**，不永久卡死 |
| 6 | **组合** | 斜坡（25°）→ 0.10 m 台阶 → 台面 → 边缘，一条 20 m 的路 | 全程不穿模、不弹飞、`ballCenterWorld` 与视觉球心偏差 ≤ 1 cm；跑完记录**每步查询次数**与 P95 帧时间 |

**必须同时做的事（否则测了也白测）：**

1. **每步成本计数器**：记 `activeSurfaceCount`（本步参与比较的支撑面数）、`maskRebuildCount`（扫掠数组重建次数）、`contactCount`，以及 `StepBall` 的单步耗时（`Time.realtimeSinceStartup` 夹两端，取 P95）。这是回答「Udon 每步能承受多少几何」的唯一办法（公开资料没有这个数，只有 200–1000× 的定性倍率）。
2. **Gizmo 轨迹**：球心轨迹 + 支撑法线 + `supportHeight` + 是否 `blocked/stuck`，跑完把截图存 `Docs/Evidence/`。
3. **纯数学固化**：每一格先写成 EditMode 测试（不需要 Unity），例如「0.10 m 台阶 + D=0.30 + `k=0.25` ⇒ `stepLimit=0.075 < 0.10` ⇒ 判墙」。**同一组用例必须在 Python 参考模型与 C# 里得出同一结论**；两边不一致就是移植 bug，不是「浮点不精确」。A 案最大的优势就是这件事办得到，别浪费它。
4. **方向数（已定 16，不再做 16/32 对照）**：方向表只是摘要、接地高度用端点云，所以 16/32 的物理输出按构造相同；若仍想在 lab 里看差异，只会在 `GetProxySupportLength` 的读数上看到分辨率变化（最坏角隙 27.38° vs 38.88°），不会在球心高度上看到 1 cm 级差别。`[推导]`

### 3.5 给 `TERRAIN_SUPPORT.md` 的直接输入（哪些决定归谁）

**已经能定的（建议它直接采纳，不要重新讨论）：**

1. **支撑面必须是「每步选出的一个局部平面」，不是一张全局平面。** 语法上仍是 `(normal, offset)`（`GroundContactMath` 对 `n` 不假设朝上，因此一行不用改），变的是「谁被选中」：地面 / 盒顶 / 斜面 / 道具顶面里取最高的那个。这一条 `TERRAIN_SUPPORT.md` §3.1 已给出，本文只确认它与专利的「滚动面基准点」是同一个思想（§2.1 第 3–4 条）。
2. **碰撞继续用球形半径，凹凸只进支撑与滚动**（专利的 Mh/Mr 分离 + 合同 §3.3 的 C 阶段禁令，两边独立指向同一结论）。
3. **台阶是显式阈值，不是涌现行为**：`rise ≤ k·D` 判台阶、否则判墙；`Unity CharacterController` 的 `Step Offset` 就是这个语义（官方手册明写「只有比它矮的台阶才迈上去」）。`k` 由 `TERRAIN_SUPPORT.md` §4.1 取 `layout.json` 已有的 `0.25`（直径口径），本文不另提数值；但**跨级那一步必须有一个独立的大抬升上限**，否则 `maxProxyLiftPerStep = 0.04` 会让大球「陷进台阶再浮出来」（该文 §4.4 已推导）。
4. **`SweepMath` 与 `GroundContactMath` 一行不改。** 前者继续做「平面 + 盒」的连续扫掠，后者继续做「给定平面 + 代理点求支撑高度」；新增的只有**选择**（哪个面当支撑、哪些盒从扫掠里剔除）与**分类**（台阶 / 墙 / 食物）。这是 A 案能「增量落地」的全部原因，别在这条上开新口子。

**需要它拍板的（本文给倾向，但不替它决定）：**

| 问题 | 选项 | 本文倾向 | 理由 |
|---|---|---|---|
| 上坡要不要硬 `slopeLimit`？ | ①硬上限（Unity 风格）②速度门槛（专利风格）③不设上限 | **两者并存：硬上限当"数值护栏"，速度门槛当"玩法杠杆"** | 专利明写"速度够就能上坡"（§2.1 第 7 条），所以**不该**用硬上限去决定"能不能上"；但单步升降钳位本身会给出一个物理上限（`TERRAIN_SUPPORT.md` §5.2 从 `0.04/步 @50 Hz` 反推出约 45°），那是防穿模的护栏，不是设计旋钮。两者不矛盾，别拿护栏当玩法。 |
| 球越大是越快还是越慢？ | ①现有 `BallMotionMath` 的 `1 − factor·growth`（越大约慢）②专利的"越大约快" | **①先保持现状**，把②做成可切换参数 | 这是**玩法**决定不是物理决定；但必须知道**我们和原作相反**，别把"大球爬不上坡"当成物理 bug 去修几何。 |
| 要不要重力/腾空？ | ①有（只做"下坠 + 落地"，不做跳）②完全无重力（永远贴地，专利风格） | **本文倾向①，但 `TERRAIN_SUPPORT.md` §5.4 已选②——这是全文唯一一处真正的分歧，必须由 Lead 拍** | 选②的代价：**"从台面边缘走出去"不会掉下去**，只能沿支撑面滑出、球心瞬间落到地面高度（观感是"瞬移下去"）；选①的代价：`BallMotionMath` 要新增 `vy` 通道与落地判定，且"停在斜坡上不下滑"要额外写死（否则球会自己溜下去，专利里没有这个现象）。**两者都能自洽，但不能既要"永远贴地"又要"边缘坠落"。** |
| 地形/支撑面数据从哪来 | ①运行期手注册（现状）②`Setup` 时从场景烘焙成数组 ③运行期引擎查询 | **②** | ②与 A 案完全一致（`LevelArenaHost` 烘焙 `BoxCollider.bounds`，Lab 已在这么做），且**零运行期 extern 成本**；③已被 §3.2 的结构性理由否掉；①撑不住真实关卡。 |
| 支撑面/台阶掩码的刷新频率 | ①每步全量重算 ②按变化事件重算 | **②**（`|ΔD| > 0.002` 或 `|ΔsupportY| > 0.005` 或盒集合变化才重建） | 50 Hz 全量重算 14 个盒在 Udon 下是纯浪费；`TERRAIN_SUPPORT.md` §4.3 已给同一条件，本文确认它够用（**前提**：掩码与支撑激活必须由同一个函数算，否则会出现"扫掠里是墙、支撑里是台阶"的死状态）。 |


### 3.6 没查到 / 查不动的

1. **Udon 到底暴露哪些 `Physics` 静态方法**——三份二手资料互相矛盾，只有 Unity 里的 `Class Exposure Tree` 能给答案。这是本文**唯一一个"必须由 Unity 操作者补一刀"**的缺口。
2. **"Udon 每帧能承受多少次物理查询"** 没有公开数字，只能自测。
3. **原作台阶/坡度/球速的数值**：专利只给"预定坡度""预定速度"这种占位词。
4. **2025 新版是否沿用专利模型**：没有拆包、没有开发者说明。`CONTACT_SOURCE_EVIDENCE.md` 的警告继续有效。
5. **没有找到任何"VRChat 里的块魂"或"带吸附物的 Udon 滚球"公开实现**；最接近的是 Booth 上的铁球转小游戏（只同步位置）。
6. **Unreal 侧的 Katamari 实现**没找到。
7. **"吸附物让球颠簸"的定量数据**（升高多少、频率多少）没有任何来源；只能靠 §3.4 格位 4 自己测。

---

## 附录 A：来源登记（按可信度）

| # | 来源 | 类型 | 用于 | 可信度 |
|---|---|---|---|---|
| P1 | [US7402104B2](https://patents.google.com/patent/US7402104B2/en) | 专利 | 三模型/16+4 针/10%/盒/坡度/显示层级 | [专利] |
| P2 | [JP4316334B2](https://patents.google.com/patent/JP4316334B2/en)（同族，英文机翻） | 专利 | 滚动面基准点、可选针三条约束、`Δr` 衰减 | [专利] |
| P3 | [US20050101365A1](https://patents.google.com/patent/US20050101365A1/en) | 专利（公开申请） | 与 P1 互校 | [专利] |
| P4 | [Patent Arcade 条目](https://www.patentarcade.com/video-game-patent/US7402104/game-performing-method-game-apparatus-storage-medium-data-signal-and-program) | 二手综述 | 可读性辅助，不作独立证据 | [社区] |
| U1 | [Unity 手册：Character Controller](https://docs.unity3d.com/2022.3/Documentation/Manual/class-CharacterController.html) | 引擎官方 | `stepOffset` / `slopeLimit` / `skinWidth` 语义 | [官方] |
| U2 | [Unity 文档：Physics.ComputePenetration](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Physics.ComputePenetration.html) | 引擎官方 | 推出穿透、可在 FixedUpdate 外调用 | [官方] |
| U3 | [Unity 文档：Rigidbody](https://docs.unity3d.com/2022.3/Documentation/Manual/class-Rigidbody.html) | 引擎官方 | CCD 只支持 Sphere/Capsule/Box | [官方] |
| V1 | [VRChat：UdonSharp Performance Tips](https://creators.vrchat.com/worlds/udon/udonsharp/performance-tips) | 平台官方 | Udon 200–1000× 慢、避免每帧长数组 | [官方] |
| V2 | [VRChat Canny：FixedUpdate 在物理模拟前跑多次](https://vrchat.canny.io/udon/p/udons-fixedupdate-runs-multiple-times-before-any-unity-physics-simulation-is-don) | 平台 bug tracker | 刚体路线不可靠的硬证据 | [官方] |
| V3 | [VRChat Canny #585：Broken Raycast Output](https://vrchat.canny.io/udon/p/585-broken-raycast-output) | 平台 bug tracker | `Physics.Raycast`/`SphereCast` 的 Udon extern 存在过 | [官方]（2020，可能已过时） |
| V4 | [VRChat：Class Exposure Tree](https://creators.vrchat.com/worlds/udon/udonsharp/class-exposure-tree) | 平台官方 | 唯一权威的 API 暴露检查方式 | [官方] |
| V5 | [Booth：鉄球転がしゲーム【Udonギミック】](https://booth.pm/ja/items/4150960) | 社区商品 | VRChat 滚球先例、卡住/重置是真实需求 | [社区] |
| V6 | 本地创作者知识库 `entities/api/exposed-types.md`、`not-exposed.md`、`scene-components/rigidbody.md` | 社区快照 | `Collider.ClosestPoint` 暴露、Rigidbody 与 ObjectSync 冲突 | [社区]（与 V3 冲突，已标注） |
| O1 | [Consterdine：Katamari 克隆](https://consto.uk/2018/10/07/bring-the-light) | 开发者博客 | 程序化碰撞网格重建、从外向内取最远命中 | [社区] |
| O2 | [Unity Discussions：Katamari Experiment（2007）](https://discussions.unity.com/t/katamari-experiment/377199) | 社区 | 吸附位姿必须在接触瞬间取 | [社区] |
| O3 | [Tartu：Sticky Ball Game](https://courses.cs.ut.ee/2021/cg-pro/spring/Main/Project-StickyBallGame) | 学生项目 | hobby 实现的典型终点（无坡无台阶） | [社区] |
| O4 | [Communisuto-Damacy](https://github.com/HugheZ/Communisuto-Damacy) | 社区 | "不规则物件改变滚动"被独立实现者认可 | [社区] |
| O5 | [gamedev.SE：collide-and-slide](https://gamedev.stackexchange.com/questions/214442/how-do-i-implement-the-collide-and-slide-algorithm-in-unity) | 社区（高赞） | collide-and-slide 的标准结构 | [社区] |
| O6 | [Honest Gamers 评测](http://www.honestgamers.com/12930/playstation-2/katamari-damacy/review.html) | 媒体评测 | 斜面打滑、吸附物导致撑杆跳 | [社区] |
| O7 | [Steam：Difficulties rolling up walls](https://steamcommunity.com/app/848350/discussions/0/1639790664936274066) | 玩家帖 | 贴墙爬升存在但不可控 | [社区] |
| O8 | [Godot 论坛：ShapeCast3D collide and slide](https://forum.godotengine.org/t/help-with-shapecast3d-collision-and-sliding/110863) | 社区 | 同一条"刚体→形状投射"的迁移路径 | [社区] |

## 附录 B：与仓库内文档的继承关系

- **不重复**：[`ABSORB_SIZE_RATIO.md`](ABSORB_SIZE_RATIO.md) 负责"能吸多大"；本文只补一句"10% 已获专利证实"，不重写它的表格。
- **升级**：[`CONTACT_SOURCE_EVIDENCE.md`](CONTACT_SOURCE_EVIDENCE.md) 里"未读专利正文"的缺口由本文 §2.1 补上；该文件本身不应被改写（它记录的是**当时**的检索结果）。
- **喂给**：[`Docs/Design/BallMotor/TERRAIN_SUPPORT.md`](../../Design/BallMotor/TERRAIN_SUPPORT.md)（撰写中）——§3.5 是给它的问题清单与倾向，不是替它做决定。
- **约束**：[`02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md) §3.3 的"直到 C 阶段通过，PVP 用球形半径"继续有效；本文没有推翻任何一条合同。

引用 URL 仅用于核查，不代表版权、品牌授权或对原作实现的背书。未运行游戏、未在 Unity 中执行任何操作、未测量任何性能数值。
