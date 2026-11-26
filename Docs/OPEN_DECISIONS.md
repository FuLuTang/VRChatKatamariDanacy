# 当前项目待决事项摘要

**用途：**把已审阅的规则决策表与发布RFC压缩成用户可回答的选项。本文记录现状、已由用户做出的决定，以及仍待决定的阶段与影响；**不替用户推荐或选择**，也不改根任务、DAG、产品范围、原作规则或接口。

## 当前状态

- 已选项目方向与用户已决规则分开记录；`recommended_option`为空。第二轮公开来源已确认原作交分为场景内兑换区（王子飞船正下方、约每分钟换位）、站在区里逐跳结算、每跳球会变小、不足一跳就停；被吞＝短暂冻结＋异地重生、不淘汰；对战有宝箱道具与加分Brother，结算另有3项第一名+50pt。**原作**仍未知的是吞人门槛与吞人收益、ブラザー加成、道具完整清单与出生点；交分规则（逐跳、每跳 0.5 s、缩径＝起始直径的 10%、局时 3 分钟）已按原作实测改写，见下节 (a)–(d)，每跳单价 15 仍标 PROVISIONAL。([研究核查](Research/KatamariBallRules/README.md)、[规则矩阵](Research/ReferenceRules/rules_matrix.md)、[P09 决策表](Preparation/RuleDecisions/decisions.json)的`rules.match_format`与`rules.score_and_exchange`条)
- 当前项目基线仍为首发两图目标、第一纵切片一图两人、夜市后置；T39允许在完整证据后批准两图或降为一图。([范围决策:L15-L22](00_SCOPE_DECISIONS.md#L15-L22)、[发布RFC:L7-L10](Archive/Preparation/ReleaseRFC/comparison.md#L7-L10))
- P09 决策表（`decisions.json`）仍是 2026-10-11 的决策支持稿，尚未回填本文 (a)–(d) 四条；P17 审查未记录新的用户决定；根任务/DAG 未因本摘要改变。([P09/P17审查:L15-L16](Archive/Process/18_LARGE_SCALE_PREPARATION_REVIEW.md#L15-L16)、[决策表`non_decisions`段](Preparation/RuleDecisions/decisions.json))

## 已由用户决定（2026-11-14）

- **素材许可**：项目现阶段不商用、也不再分发原始素材，免费素材不按许可类型设限（Fab 标准许可、CC-BY、Quaternius QAL 等均可用）。仍在素材清单里逐项记录来源/作者/许可，需署名的放进世界内署名板。若以后重新启用付费外观，需按清单复查 NC 限制。
- **仓库**：开发阶段所有素材照常提交进 GitHub 仓库（目前公开），正式版前由用户自行改为私有。
- **Fab 自动化**：除用户 Chrome 里的 Playwright 外，可试用非官方 `@threenative/fab-mcp` 辅助搜索/下载；许可仍以 listing 页面为准。
- 说明：“完全不商用”与下方“付费外观 SKU”方向冲突；商店维持关闭，是否彻底移除内购方向待用户以后确认。

### 交分与回合规则（2026-11-14 拍板）

这四条已决定，并于 2026-11-26 按原作实测（对战视频逐帧读数）改写为现行口径。**标 PROVISIONAL 的数值是本项目暂定值**，须在 T39 试玩后复核；决定本身不改变任何任务依赖。

- **（a）交分逐跳结算（owner 2026-11-26，按原作实测）。** 球站在激活的交分区里，每 0.5 s 结算一跳：每跳直径减少「本图开局直径 × 10%」（`D ← D − 0.1·D0`），积分加固定单价；减完会小于开局直径时就停（`D − 0.1·D0 < D0`）。每跳缩径随图自动取开局直径的 10%，不逐图配置。账本请求按跳计，每跳一个 requestId。依据：对战视频实测（本地分析，未入库），缩径与图尺度成比例、与当前球径无关。
- **（b）没有单独的最小兑换门槛。** 下限就是开局直径：`D − 0.1·D0 ≥ D0` 才能再跳一次。分数按跳计、单价先用 15（PROVISIONAL，可调），不再按体积换分，分数与直径下降量成线性。另有两条实测但原因不明的现象，不写进规则：最后一分钟那处每跳 +10，本局首跳 +45。
- **（c）交分区：同时 1 处，每 60 s 换一处，开局即有，一局 3 处（局时 180 s）。** 区上的 UFO 图标显示该处剩余秒数；到 0 立刻在别处刷新，没有空档。依据：对战视频实测（对账 3 处与整分钟对齐，置信度中高）。具体布局见[野餐草坪 §6 交分区与轮换](Design/Maps/PicnicLawn/README.md)。
- **（d）结算奖励：3 档，每档每局从奖项池随机抽一项（三档不重复），每档第一名 +50 pt，并列都发。** 交分积分仍是主计分通道。第一版池：卷入物件数、卷入别人塊的次数，第三项待 owner 定（不写死）；ブラザー和道具第一版不做，也不进池。依据：对战视频实测（3 档名称固定、项目每局不同、并列都发）。另：最后 60 s 进入隐藏阶段（计时变红显示秒数；HUD 只显示自己的分和名次；结算前他人分显示「?????」）；被吞后已交的分保留、球径不重置，约 6 s 后在别处重生，重生后约 3 s 保护（PROVISIONAL）；吞人门槛先保留 1.35 倍（未测出），吞人得分仍是开放项；大球移速随直径下降（定性，曲线待调参）。细则见 [VERSUS_MODE](Design/Versus/VERSUS_MODE.md)。

### 地形门、成长包线与地图指纹（2026-11-14 拍板）

同 (a)–(d)：这三条是“按这个口径往下做”的决定，不是验证通过，也不改任务依赖。前两条须在 T06 尺度冻结与 T21 灰盒实测后复核；第三条只约束关卡数据与校验器的写法。

- **（e）12% 坡度上限只管可驶坡道，不管尺寸门台阶。** `layout.json` 的 `scale.max_traversable_slope_percent = 12.0`（PROVISIONAL）约束的是**球要滚上去的可驶坡面**；竖直台沿／平台边只要用途是**尺寸门**，就不算违规。例：花圃木框 0.26 m 台高 / 0.9 m 斜面 = **28.9%**，它的判据是“球够不够大”（`size_gates` 的 `climb` 门，门槛 1.10 m，0.95 m 的球上不去），不是“坡够不够缓”——与原作同构：能不能越过去看球的大小（[规则矩阵](Research/ReferenceRules/rules_matrix.md) 的尺寸分档 C6/C9 是尺寸判据；原作未公布坡度与台阶数值，所以 12% 与 0.05 m 都是 `[项目自选]` 暂定值，见 02 §3.2“坡道、台阶…还需实现和测试”）。**不要为了消掉这个百分比去降台面**：压到 ≤0.11 m（或把斜面拉到 ≥2.2 m）会让 0.95 m 的球直接滚进花圃，门就废了。口径后果：地图数据要逐面声明 `traversability.class`——**可驶坡 `drivable_grade`（`run_m` 必填、坡度现算，查 ≤12%）／爬升台阶 `climb_step`（`step_height_m` 必填，查“台阶高 ≤ 1.5 × 球径”——`k_h = 1.5`，PROVISIONAL，见 [TERRAIN_CLIMB](Design/BallMotor/TERRAIN_CLIMB.md) §7；矮于半球的沿子 `h<r` 不必声明，棱一推就滚过）／障碍 `obstacle`**——通用校验器（`Tools/mapbuild/validate_map.py`）按类别分别判，不再用一个全局坡度阈值扫所有立面。现状：`layout.json` 为 schema 2，花圃木框声明 `climb_step`（step 0.26 m / run 0.9 m）。**注意（2026-11-16）**：1.5 倍口径下最小可翻球径 = 0.26 ÷ 1.5 ≈ 0.17 m，与该 `climb` 门的 1.10 m 门槛**不再相容**——0.26 m 的框对任何 ≥0.17 m 的球都可爬，尺寸门失去物理支撑；第一关要么接受早开（与原决定 U5 的「斜板早开」同向），要么改这道立面。处置留给第一关 owner（PicnicLawn README §16 风险 12），树根丘声明 `drivable_grade`（run 1.25 m、11.2%，由 `64e0de3` 压到上限内）。逐面分类本身是 `[项目自选]` 的工程写法，不是原作规则；原作只给“球够大才翻得过去”这个形状。
- **（f）2.20 m 是包线天花板，不是一张图里能滚到的直径。** 成长标定 `units_to_max_diameter = 1356` 价值单位 = 0.30→2.20 m 的一次满径成本（PROVISIONAL，见 [Gameplay 报告](Design/Gameplay/README.md)）；但野餐草坪活地图只有 **1284 单位**，把整张图吃光也只到 **2.16 m**。决定：**保留 1356 标定**（它是成长节奏参数，不是可达性承诺），把 2.20 m 写成**包线上限**；真要出现 2.20 m 的球，只能靠补货（T22）或加地图内容，且**现在没有任何一关需要它**——剧情两关的三星目标一关是 1.60 m、一关是 30 件（`Docs/Design/StoryMode/ROUND_RULES.md`），都不碰 2.20 m。设计文档里的包线一行统一写成“**0.30–2.20 m 包线；1284 单位的地图上实际 2.16 m**”，不要再出现“最大球 2.20 m”这种会被读成单图可达值的写法。若以后要 2.20 m 变成常规体验，动的是补货预算或地图内容，不是把 1356 改小去凑（那会同时把 t3 提前，等于换成长节奏）。
- **（g）文档里不手抄地图哈希。** 需要 `layout.json` / `prop_catalog.csv` 的指纹时一律对文件现算（`sha256sum`），不从别的文档复制；野餐草坪 README 已写明“禁止手抄”。这是 `[项目自选]` 的工程约束，不是原作规则。

### 拾取层：吸收门槛与接触余量（2026-11-14 拍板）

同 (a)–(g)：这两条是**运行时契约**层面的决定（decided），不是测量结果，也不改任务依赖。它们钉住的是“现场调试时两个 agent 各自把 reach／门槛算错”那类问题的口径；代码改没改另说。

- **（h）可吸收性按球的尺寸相对判定，不按绝对 1 m。** 门槛随球长大；判定量与比例的现行形态见 (l)（体积口径）。三个量不要互换：pickup 尺寸（地图档位/道具声明）、`bounds`、接触 reach 半径各是各的量，把 AABB 全长当 `pickup_diameter` 等值代入是同一类算错。运行时每步用当前语义直径（与碰撞半径同源）算上限，绝对上限只作护栏、不得悄悄变成第二个玩法阈值。PvP 的 `swallow_diameter_ratio = 1.35`（`Docs/Design/Gameplay/round_model.py`）不是道具吸收比例，别混用；PvP 吞噬阈值本身仍未验证。
- **（i）`contactRadiusWorldM` 保持 0，且这 0 是项目决定，不是占位。** 吸收的接触判定是原作的结构（2026-11-17，lead，原作优先，差异表 D08）：球心到道具**有向盒**（自身尺寸 + 当前世界旋转）的最短距离 ≤ 球碰撞半径 + `contactRadiusWorldM`（`PickupAbsorbMath.TryComputeContact`），不再用道具外接球。所以 0 的语义就是“盒面必须实质碰到球、没有额外手感窗口”，不是“这个功能还没接”。工作面算例（球在起始直径 0.30 m、碰撞半径 0.14987 m、`contactRadiusWorldM = 0`、物件为边长 s 的立方体）：

| s (m) | 正对面心触发的中心距 = 球半径 + s/2 | 正对角点触发的中心距 = 球半径 + √3·s/2 |
|---:|---:|---:|
| 0.10 | 0.200 | 0.236 |
| 0.25 | 0.275 | 0.366 |
| 0.35 | 0.325 | 0.453 |
| 0.50 | 0.400 | 0.583 |

读法：触发取决于盒面到球面的距离，细长件从侧面逼近比沿长轴逼近要靠得更近才触发；反过来“视觉上贴住了却不吸”才是接触算错（位置/旋转快照过期，或宿主把 `size` 填得比画面小），不是场景要调。完整推导见 [PickupAbsorb README 第 1、2 节](Design/PickupAbsorb/README.md)。

### 价值单位：账本只收整数，小数当场拒绝（2026-11-14 拍板）

同 (a)–(i)：这条也是**运行时契约**层面的决定（decided），不是测量结果，也不改任务依赖。与前九条不同的是它的代码早已是这个行为——本条把“实现细节”升成决定，好让下一次“吸收不动”的排查不再先怀疑几何。**实现状态：代码已是该行为，无需改动**（`PickupAbsorbMath.cs:266-268`）。

- **（j）账本 gain 是整数单位，这是有意的；小数道具价值是作者数据错误，以 `RejectNoGain` 大声拒绝，不静默截断、不四舍五入。** 代码路径只有一条：宿主提交候选时把 `valueUnits` 原样存进候选槽（`KatamariBallMotor.cs:1013`），判定时 `EvaluateAbsorbWithValue` 先过几何门、再用 `TryAbsorbValueToLedgerGain` 换 gain，换不出来就返回 `RejectNoGain`（`PickupAbsorbMath.cs:254`），换出来但 gain ≤ 0（即价值为 0）同样拒绝（`:255`），拒绝随即进电机拒绝环（`KatamariBallMotor.cs:711`）；换算本体先截断成 `int truncated = (int)itemValue`（`PickupAbsorbMath.cs:266`），再要求 `(float)truncated == itemValue` 逐位相等，不相等即 `false`（`:268`），负数、非有限值与 ≥2³¹ 的值同样先拒（`:262`、`:265`）。理由不是“整数好实现”：确认体积在账本里与 `banked_score`、每个事件 gain 共用同一个整数单位（`GrowthLedgerMath.cs:52-54`），账本只接受确定性权威提交（[Ledger README L7](Design/Ledger/README.md#L7)），而整套成长标定本身就是按整数单位写的——0 单位对应 0.29974 m 的起始语义直径（[BallMotor README L130](Design/BallMotor/README.md#L130)），1356 单位对应 2.20 m 满径（[Gameplay README L56](Design/Gameplay/README.md#L56)）。小数价值落在两个整数之间，在成长曲线上没有对应分数，也就没有一个可以被两台客户端都算成同一个数的总分。**对内容的后果：** 每件道具的 `valueUnits` 必须是 ≥ 1 的整数——填 0 或 0.5 让这件道具**永远不可吸收**，跟球贴得多准、reach 算得多对无关；实验室宿主把 `valueUnits` 暴露成一个可序列化 float（`PlaygroundProp.cs:45`），所以这种值在编辑器里没有任何阻拦，只会在运行时以 `noGain` 暴露。**调试教训：** 拒绝在外部只表现为 `noGain`，与“球根本没碰到道具”在读数上无法区分；必须打印**那件道具**被电机收到的价值（`pv`，`CpuSkirmishBootstrap.cs:208`，读的是候选槽里的原值 `KatamariBallMotor.cs:1645-1649`），而不是电机级“最后一次拒绝”的聚合（`CpuSkirmishBootstrap.cs:168-175` 的 `rej=`）——聚合会把上一件道具的拒绝盖到当前目标上，于是“道具自己写的 1.0125 旁边挂着 noGain”被读成了几何或接线问题（那批实验室道具的价值是按 `s³ × 300` 手算填的：边长 0.15 m → 1.0125、0.2 m → 2.4、0.25 m → 4.6875，逐个都过不了整数门）。**这不是以后要“修”的 bug：** 改成静默截断或四舍五入会把作者错误藏起来（道具看起来被吃了、分数却没动），并让两台客户端对同一份提交得出不同的总分；该放松的是数据（把价值表改成整数），不是这段判定。

### 跟随架眼点：球后上方、随半径线性（2026-11-15 拍板）

同 (a)–(j)：这条也是“按这个口径往下做”的决定，不是测量结果，也不改任务依赖。它定的是**玩家视点**（跟随架参考帧）的几何；Unity 侧已是该实现，Python 孪生与两侧测试同日追平。

- **（k）眼点 = 球心后方 + 上方，两项都随球半径线性放大，不再有「离球心常量眼高」。** 公式 `eye = ballCentre − forward(滤波后的 yaw) · (eyeBaseBackMeters + radius · eyeBackPerRadius) + up · (eyeBaseUpMeters + radius · eyeUpPerRadius)`，默认 `0.75 + 2.2r` 后退、`0.55 + 2.4r` 抬高，护栏 `eyeUpMinMeters = 0.1` / `eyeUpMaxMeters = 8`（钳**离球心**高度）、`eyeMaxBackM = 8`（钳后退）。四档直径（0.10 / 0.30 / 0.72 / 1.43 m）算出来是：上方 0.670 / 0.910 / 1.414 / 2.266 m，后退 0.860 / 1.080 / 1.542 / 2.323 m，球面净空（上方 − 半径）0.62 / 0.76 / 1.05 / 1.55 m。依据：旧语义 `y = 球心 + 1.6` 的净空是 `1.6 − r`，0.30 m 球 1.45 m、1.43 m 球 0.885 m；而吸附物站在球面上、约一个物件深（吸收门槛是「物件最大边 ≤ 球半径」），球越大画面越被糊满——1.43 m 正是设计判定的最坏档。改成随半径增长后净空**变大**，平视时「球 + 吸附壳」上沿仍在视轴下 20.4°–32.7°，球留在画面下缘而不是糊住中间。已否决：A. 保留常量眼高（净空线性下滑，1.43 m 时只剩 0.885 m）；B. 只抬高不后退（球顶在视点正下方，吸收物直接糊镜头，也失去“推球”的纵深）；C. 改成按「离球顶」定义高度（等于把半径从式子里再减一次，净空还是常量，换名字不换问题）。数值全部 PROVISIONAL，须在 T05/T06 的舒适性与尺度门禁里由真人头显复核；公式本身是几何，不是舒适参数。
- **（k）的硬约束：`VRCStation` 是这台引擎上唯一能连续移动玩家的手段。** VRChat **没有**任何 API 能连续移动玩家 avatar，可用的只有 `VRCStation` 与传送。所以「人推球」只能实现成**摆在球后方的一个 station**：第一人称下玩家看到的正是「球在身前、视角在球后上方」，与推球一致；第三人称会看到 avatar 是坐姿——这是引擎限制，不是设计取舍。这条同时解释了为什么方案不是「让玩家自己走、摄像机跟球」：玩家自己走既不能保证与球同速同向，VRChat 里也没有“每帧把玩家搬到某点”的接口。口径后果：motor 只写 `followRigRoot` 的 position + yaw（不碰 HMD、不写玩家位置），Station 挂在这个根下，且它的局部偏移**不能**再叠一层「球后上方」——那样会叠成约 4 m 后退。代码位置：`FollowRigMath.TryUpdate`（`Runtime/BallLayers/FollowRig/FollowRigMath.cs`）、7 个字段 `KatamariBallMotor.cs:137-143`、`UpdateFollowRig` 与 `ApplyPresentation`（`:1004-1032`）。
- **实现状态（2026-11-15）**：C# 已是该语义；Python 孪生 `Docs/Design/BallLayers/FollowRig/follow_rig.py` 的 `try_update(...)` 与 C# `TryUpdate` 同名同序（含调用方持有的上一帧位置/yaw/离球心高度），`Docs/tests/ball_follow_rig/` 由 8 例改为 9 例（新增四档直径的球后上方检查，期望值手算），`Docs/tests/ball_visual_roll/test_presentation_cross_layer_simulation.py` 的 4 处旧调用一并改完；`Docs/Design/BallMotor/README.md` 的「跟随架」表、`Docs/Design/BallLayers/FollowRig/` 的三份文档与 `Docs/Design/Parameters/README.md` 的重复清单已同步。**未在 Unity、UdonSharp 或 VR 中运行**，第三人称坐姿与坐姿视点补偿仍待 Build & Test。

### 吸收尺寸门改为体积口径（2026-11-15 拍板）

同 (a)–(k)：这是**运行时契约**层面的决定（decided），不是测量结果，也不改任务依赖。与 (h) 的区别不是参数，而是**判定量换了**：从「物件最长边 ≤ 球直径 × 比例」换成「物件体积 ≤ 球体积 ÷ 比例」。

- **（l）吸收判定用体积，不用最长边：`a·b·c ≤ (π/6)·D³/10 ≈ 0.05236·D³`。** 其中 `D` 是球的**语义直径**（不是碰撞半径），`a·b·c` 是物件 `bounds` 的三轴乘积（AABB 体积），`10` 是 `absorbVolumeRatio`——球体积必须是物件体积的 **10 倍**。等价写法是 `D ≥ 2.154 × d_eq`，`d_eq = (6·a·b·c/π)^(1/3)` 是物件的体积等效直径；对**立方体**它化简成 `D ≥ 2.673·边长`（因为立方体的 `d_eq = 1.2407·边长`）。依据：[ABSORB_SIZE_RATIO.md](Research/KatamariBallRules/ABSORB_SIZE_RATIO.md)（14 条来源、逐条分级）。**证据等级：中**——一份社区数据挖掘直接陈述 10× 体积判定与「Size to roll up = 能滚起它的最小球径」（S-AR01/S-AR02）；9 个物件按门槛反算体积，8 个落在 ±25% 内（S-AR05 等）；三处旁证同向（Fandom「包围盒含空腔」与「铅笔比橡皮好吸」S-AR07、游戏内铅笔 4.3 cm < 橡皮 8.1 cm、玩家观测帖 S-AR12）。**常数 10 本身只有一份独立来源**：若真值是 8 或 12，所有门槛按 `(c/10)^(1/3)` 缩放，即 −7%～+6% 的球径，量级上不改变口径结论。
- **为什么必须换量而不是调系数（用户给的具体例子，留档）：** 测试场景里黄色小方块边长 **0.05 m**、红色方块 **0.30 m**。**边长只差 6 倍，体积差 `(0.30/0.05)³ = 216` 倍。** 球能吃多少是**体积**账（球体积 ÷ 10），线性口径只拿「最长边 vs 球直径」比长短，它只能说出那个「6」，说不出 216 倍，也就无法回答「这个球够不够大」。换成系数语言：等效线性系数 `k = 可吸物件最长边 / 所需球径` **不是常数**（研究文档 §4：立方体 0.37、3:1 砖 0.78、5:1 扫帚级 1.09、10:1 铅笔级 1.74），所以任何一个 `absorbSizeRatio` 都只能在一个形状比上对——口径错，调系数只是在两个方向的误差之间挪。真实道具建模后这种差别只会更大：方糖 1.6 cm vs 野餐桌 150 cm，边长差约 94 倍，体积差约 `8×10⁵` 倍。口径的直接收益是**细长物件自动能吸**（棒球棒 0.02×0.02×0.85 m 体积 0.00034 m³，0.50 m 的球就吸得动；线性口径要 0.85 m 的球），代价是**薄片几乎白送**——两者都是原作的形状（S-AR07），不是我们新引入的偏差。
- **旧的两个线性字段已删除，不是保留兼容。** `absorbSizeRatio`（物件最长边 / 球直径 = 1.0）与 `maxItemSizeWorldM`（8 m **长度**上界）都消失，替换为 `absorbVolumeRatio = 10` 与 `maxItemVolumeWorldM3 = 512`（= 8³ m³，即「8 m 立方体」的读法：保留旧上界的量级、换成体积口径）。理由有两条：(1) 长度上界配体积门会在 8 m 处**硬截断**，语义错——一条 0.02 m 见方、8 m 长的杆体积只有 0.0032 m³，却被「8 m」这一条钉死；(2) 保留旧字段当兼容等于留一条**能悄悄把闸门压回线性**的配置路径，而这正是 (h) 已经踩过一次的坑（场景里残留覆盖值让决定不生效）。**迁移代价**：现存 lab 场景/prefab 里的 `maxItemSizeWorldM: 8` / `absorbSizeRatio: 1` 变成被 Unity 忽略的死键，组件回落到新字段的 C# 默认值（512 / 10），所以**行为不需要改场景就已正确**；清理死键属于 Unity 侧的顺手活，见下。
- **绝对上限也是体积口径，且它是护栏不是玩法。** 满径 2.20 m 球的体积 `(π/6)·2.20³ = 5.575 m³`，距 512 m³ 有 92 倍余量；`maxItemVolumeWorldM3` 只在比例被配错到很低时才会咬人。**不要**拿它当「一关里最大能吸的东西」用——那是体积门的活。
- **两件事不要混：判定量是自身尺寸三轴乘积，接触量的是有向盒到球心的距离。** `TryComputeContact` 只决定「够不够近」（盒面碰到球才算），与体积门没有关系；同一个自身尺寸 `itemSize` 喂给两者。细长物件正是两者分离的典型：棒球棒的体积 0.00034 m³，而它沿长轴的端面离中心 0.425 m。把最长边、对角线、包围球半径中的任何一个当体积用，都是本条点名的算错。
- **球类道具会被 AABB 算虚大 `6/π ≈ 1.91` 倍，这是忠实还原，不是 bug。** 球/椭球（种子、珠子、毛絮团、水果）的 AABB 体积比真实体积大 1.91 倍，于是需要约 `1.91^(1/3) = 1.24` 倍大的球才吸得动。原作本身也用包围盒、并把空腔算进去（S-AR07），所以这是同构而不是缺陷；要按「看起来的体积」校准，正确的口子是给每件道具存 `volume_m3`（研究文档 §6 推荐项 1 的后半句），不是把这一层改回长度。
- **实现状态（2026-11-15，C# 已落地，已编译进 Udon 程序资产，EditMode 455 条 454 过）。** 判定链只有一条：`PickupAbsorbMath.TryComputeItemVolume(bounds)`（**唯一**的体积推导，三轴乘积，溢出为非法输入）→ `IsVolumeBeyondLimit(itemVolume, limitVolume)`（**唯一**的比较，含等号在可吸一侧）→ 吸收门用它、挡路谓词 `IsBeyondAbsorbVolumeLimit(bounds, limitVolume)` 也用它，所以「太大」在运行时只有一份定义。上限合成 `TryComputeAbsorbVolumeLimit(semanticDiameter, absorbVolumeRatio, maxItemVolumeWorldM3)` = `min(ceiling, (π/6)·D³ / ratio)`；相对入口仍是 `EvaluateAbsorbWithBallSize` / `EvaluateAbsorbWithValueAndBallSize`；装配层每步重算，诊断读数改名 `GetAbsorbVolumeLimitWorldM3()`。序列化字段在 `KatamariBallMotor.cs`（`maxItemVolumeWorldM3 = 512f`、`absorbVolumeRatio = 10f`），人工仍以 `[SerializeField]` 暴露、默认值即契约。测试：`Tests/EditMode/Pickup/AbsorbSizeGateTests.cs`（含 0.05/0.30 的 216 倍与棒球棒两条新用例）、`PickupAbsorbTests.cs`、`BlockingPropMathTests.cs`、`Tests/EditMode/BallMotor/AbsorbSizeLimitTests.cs`（装配层默认值与上限读数）、`RejectNoGainTests.cs`。**Unity 侧已完成**：重编译与 Udon 程序重生成、`run_tests`（EditMode 全量 **455 条：454 过，1 红**；红的是 VRChat SDK 包内自带的网络 URL 测试，与本项目无关；`AbsorbSizeGateTests` 8/8 全过）。**仍待做**：7 处场景序列化覆盖的死键清理（编辑器产物）。**未验证：任何运行时表现**（Play Mode/VR 尚未跑）。
- **当前有意没改、但会被本条改变语义的地方（内容侧，等真实道具落地再动）：** `Tools/mapbuild/validate_map.py` 的 `item_tier_pickup_diameter_m` 档位窗口、`Editor/Pickup/PickupFieldAuthoringRules.cs` 的 `SmallTierMaxExtentM = 0.21` / `MediumTierMaxExtentM = 0.76` 分档与 `@size=` 声明尺寸——全是**长度**口径（档位是内容侧的分档，不是运行时门槛）。改了体积门却不改它们，不会报错，只会让「档位」与「某球径下能不能吸」脱钩；**这是本条落地后第一个要复核的地方**，不是本条的缺陷。
- **内容侧后果（2026-11-15，按序列化数据实测，不是推断）**：`propBounds` 若按目录**最长边填成立方体**（`0.9,0.9,0.9`、`0.52³`、`0.38³`、`0.24³`…）。体积口径下 `0.9³ = 0.729 m³` 需要 `D ≥ (6·0.729·10/π)^(1/3) = 2.40 m` 的球——超过 2.20 m 包线，**暖壶从此永远吸不到**；同一张表里 `0.52³` 要 1.39 m、`0.38³` 要 1.02 m。旧线性口径下「0.9 m 的立方体」和「0.9 m 的长条」难度完全相同，所以这个数据问题被口径掩盖了。同理 `@size=1.25` 的长凳记成 1.25³ = 1.95 m³ 也不可达。**结论：体积门落地后的第一件事是让 `bounds` 说真话——用真实渲染器 AABB 或每件道具的 `volume_m3`，不是回头调 ratio。** 这也是「球类道具虚大 6/π」那条的同源问题，只是立方体近似比 6/π 更狠。
- **离线参考模型仍是线性口径，这是已知分歧不是遗漏。** `Docs/Design/Gameplay/round_model.py`（`pickup_diameter_m <= ball_diameter_m * absorb_size_ratio`）与 `Docs/Design/PickupAbsorb/pickup_absorb.py`（`max(item.bounds) > max_item_size_world_m`）都还是旧口径；两者都是 DRAFT/离线模型、不是运行时契约，追平它们要连带改 `Docs/tests/`，已记在 [PickupAbsorb README「与 `pickup_absorb.py` 的已知分歧」](Design/PickupAbsorb/README.md)。

### 评分模块可拆卸、球径上限与道具三层（2026-11-15 拍板）

同 (a)–(l)：以下四条是产品口径、工程约定与当前优先级（用户 2026-11-15 定），不是测量结果，也不改任务依赖。

- **（m）星级与评语属于可拆卸的“评分模块”，回合核心不认识它。** 回合核心只做计时、状态机、到点冻结与重试；星级、评语、结果卡文案是独立组件，场景里不挂它，回合照样能跑完、能冻结、能重试。用户原话：“其实星级不重要，不加都行，这玩意算是‘最后评分标准’的模块里的，不是我们核心的，需要解耦”。理由：评分要等玩法成型才有意义，而“这一局有没有跑完”不该被“拿了几颗星”绑住。口径后果：新写的回合逻辑不得读星级/评语；评分只能消费回合已经确认的量（已确认单位、件数、耗时），不能反过来影响吸收、冻结、重试或成长；[StoryMode README](Design/StoryMode/README.md) 的星级带只是评分模块将来的输入。代码拆分正在进行（`Runtime/Story/`），本条只定边界，不描述其进度。
- **（n）全局球径上限 400 m（owner 2026-11-18），所有地图适用。** 这是语义直径，不是成长目标。电机的 `GlobalMaxBallDiameterM` 是唯一来源；`SizeModelMath.TryDerive` 在上限处饱和。起始直径和目标仍可按关设定。
- **（o）道具三层分离：视觉网格 / 碰撞体 / 玩法体积（`propBounds`）是三件事，不混用。** 用户原话：“不仅仅‘物体建模’，还有‘碰撞体建模’呢，别搞混了”。依据：吸收门读的是道具自身尺寸（`propLocalBounds`）的三轴乘积（(l)），接触读的是同一个尺寸放进世界的有向盒（(i)），碰撞体决定球怎么被挡住——三者任一顶替另一个，都会静默改变门槛或让球穿模。口径后果：一件道具“多大”只在 `propBounds` 里声明；视觉网格可以比碰撞体细，碰撞体可以是简化凸包；场景按 AABB 中心放置道具，所以自建模型的轴心一律在几何包围盒中心，不再用底面中心（[道具阶梯](Design/StoryMode/PROP_SET_PLAN.md) §3.1）。
- **（p）现阶段不调数值。** 用户原话：“现在数值上的问题倒不是主要矛盾点”。口径后果：[StoryMode README](Design/StoryMode/README.md) 末尾「全局球径上限与第一关星级带」里的 1.25 / 1.40 / 1.42 只是**评分模块将来的参考输入**，不是待办；体积门的 10× 比例、`c = 0.0041` 都照现状用，不因为某关暂时拿不到三星就去改 ratio、经济常数或时限。要调数值须由用户另行提出。

### 野餐一角布局：内容量、结构件单位来源与三条通路（2026-11-15 拍板）

同 (a)–(p)：以下是**内容与布局口径**的决定（拍板人 Lead，用户可推翻），不是测量结果，也不改任务依赖。它结掉的是 [`PICNIC_CORNER_LAYOUT.md`](Design/StoryMode/PICNIC_CORNER_LAYOUT.md) §9「需要拍板」的 U1–U6；该节六行已就地标成“已决定”并链回本条。

- **（q）野餐一角按 S1 = 1500 u 往下做，两块静态斜板永久不可吸，壁龛保持 2 m；六条里只有 U3 是新增工作量，U2 的工具改动已完成。** 下面 U1–U6 是这一条的正文；它不改 `tasks.json`、不改场景、不改任何数值表，也不解锁 (p) 的“现阶段不调数值”。
- **U1 内容总量 = S1 1500 u（`PICNIC_CORNER_LAYOUT` §5.5 的推荐方案）。** 其中 **110 件小道具仍是 412 u，一件不动**（kind 计数 40/22/17/10/7/4/4/4/2 原样），**17 件结构件承担 1088 u**，412 + 1088 = 1500；2.20 m 需要 1356 u，落在内容的 **90.4%** 处，按该模型全吃光约 2.275 m。**星级被摊薄是可接受的副作用**：1.25 / 1.45 / 1.60 三个冻结阈值在 1500 u 里等于 16.4% / 25.7% / 34.6%，但评分是可拆卸模块（(m)），用户明确说星级不重要，所以**现阶段不调星级数**，也不为了让百分比好看而回头改 1500——这与 (p)「现阶段不调数值」同一个态度。已否决：S2（折叠桌当最后一口，节奏最好，但 2.20 永远不可达）、S3 = 1356（吃光正好 2.20，容错 0，与 120 s 实测速率打架）。
- **U2 结构件的价值单位按 def 定义，由 placement 数据（搭地图工具的 `placement.json` / def 目录）给出，不再由 `PickupFieldSync` 按最长边定价。** 即 `valueUnits` 从“同步工具用 `PickupFieldAuthoringRules` 的 (kind, tier) 值表算出来并写回”改成**内容声明值**（字段与写法见 [LEVEL_AUTHORING.md](Design/Tools/LEVEL_AUTHORING.md) §4.2 的 `valueUnits` 行与 §7.4 的接线表），同步工具退化为**消费者 + 回读校验**：声明值与宿主值不符就报错并列出，**不静默接受、不自己重算**。**与既有条目的关系（不要另立平行条目）：** 本文没有一条独立的“价值单位口径统一”条目，最近的两处是 **(j)**（上节「价值单位：账本只收整数，小数当场拒绝」——它定的是**账本怎么收**：gain 必须是 ≥1 的整数，小数/0/负数当场拒）与本文件外的《[COLLIDER_LAYER](Design/PickupAbsorb/COLLIDER_LAYER.md)》§5.3 那句待办“**在价值口径统一前仍然拒绝它的 `valueUnits` 变更**（refused: value units pending one rubric）”。U2 结掉的是**后者**：数从哪来定了，(kind, tier) 值表定价这条路不再走；**(j) 的整数契约不变**（声明值仍须是 ≥1 的整数），(m)/(p) 也不变，所以不新增第二条价值单位条目。U2 本身**不给任何具体数值**——每件多少 u 仍是内容数据，属被 (p) 搁置的那一轮。**实现状态：已实现（2026-11-15，Linux 侧代码与文档）**。`Editor/Pickup/PickupFieldSync.cs` 不再读 (kind, tier) 值表，也不再按最长边定价：`SyncFromChildren(host, fieldRoot, declaredValueUnits)` 把调用方传来的声明值（导入器按 `defs[].valueUnits` / `props[].valueUnits` 收集）与另外五个数组一次写入，再从宿主回读校验；声明值与宿主不符、或声明的 itemId 没有成行，都报错并列出，导入器不再自己改写数组（`WriteDeclaredValueUnits` 已删除）。没有声明时同步工具保留宿主已序列化的 `valueUnits`——未声明的宿主（如 `CpuSkirmish`）因此一位不变；既无声明又无既有值的新行写成 0 并在报告里点名，不再因为缺 (kind, tier) 行丢件。`refused: value units pending one rubric` 守卫随本实现解除，[COLLIDER_LAYER](Design/PickupAbsorb/COLLIDER_LAYER.md) §4.2/§5.3 的相应句子已改。**（j）的整数契约不变**（声明值仍须是 ≥1 的整数）。
- **U3 四个新 def 要建模，已派出。** `storage_bin`（0.55×0.40×0.35）、`cooler_box_large`（0.70×0.40×0.45）、`crate_large`（0.80×0.45×0.50）、`picnic_bench`（1.20×0.45×0.40），尺寸按 §9 U3 推荐项，用途是填 1.1–1.6 m 之间的结构件空档（不做的话 1.22 → 1.86 会退化成只剩小件可吃的长草期）。口径提醒：建模 = **视觉网格 / 碰撞体 / 玩法体积（`propBounds`）三件事**，按 (o) 分开做，轴心在包围盒中心；上面给的是 AABB 设计尺寸，导入时不要再放大。
- **U4 两块静态斜板永久不可吸——接受。** `ramp_to_table`（30°）与 `ramp_to_bed`（10.7°）保持 `LevelArenaHost` 声明的静态斜面，不进 `PickupField`、没有 `valueUnits`。静态斜面由 `LevelArenaHost.rampBoxes` 登记为实体 slab OBB（见 [BallMotor README](Design/BallMotor/README.md)「关卡几何宿主」），**不是新例外**。代价照记：“木板中期能粘”只对**松木板**成立，对**钉住的斜板**不成立；补偿是 2 件可吸的同款松木板（§7 已列）。
- **U5 地形第三阶段（P3）之后 0.30 m 的球能沿 10.7° 斜板上花圃台面、提前吃掉水果簇 B——接受，作为原作式的小捷径保留。** 这与原作“前期从木板滚上去”同构，也是 (e)“尺寸门看球够不够大”的同一形状：通路本来就该随地形打开，而不是靠台阶门槛把人挡在外面。**代价照记**：台面作为中期地标会被提前扫掉（水果簇 B 的 56 u 提前被拿走）。**试玩后再看**：若 T21 灰盒或 T39 多人试玩显示台面因此失去地标意义，改的是斜板布置或台面条目，**不是**把 (e) 的台阶规则调回去。
- **U6 壁龛净宽 2 m——确认。** `D > 1.9` 的球进不去，但门后 6 件的 `D_req` 全部 ≤ 0.30，开门的球（0.83）第一时间就能吃，不会变成“后期回不去的孤儿”；既不改几何，也不给壁龛加第二道门。**无待办。**

### 体积表示：定点 `long volumeMm3`（2026-11-16 拍板）

同 (a)–(q)：这条是**表示层**的决定（owner 2026-11-16 拍板，读完 [定点体积影响审计](Research/ContentVolume/FIXED_POINT_VOLUME_IMPACT.md) 后确认），不是测量结果，也不改任务依赖；它把 [parity 审计](Design/Parity/PARITY_AUDIT.md) 的 D5 从「建议」落成「表示已定」。

- **（r）球与物件的体积一律用定点 `long volumeMm3` 表示：1 LSB = 1 mm³ = 1e-9 m³。** 量子取 mm³ 而不是我们自己的 `c = 0.0041 m³/unit`，因为原作的唯一量化点在**直径**上（`Req Pickup Size` 整 mm），体积侧按同一精度取样（D5 原话）；int32 装不下（2.147 m³ < 满径球 5.5737 m³），`long` 上限 ±9.22e9 m³；整数加法精确、可交换，账本重放幂等——这是 (j)「账本只收整数」在更细量子上的延续，不是推翻它。
- **（r）迁移顺序 A → B → C → D，四步各自独立验收，不许合并。** **A** 只加宽类型、量子不变：`GrowthLedgerMath` 的 `int` 参数与数组、电机的 `unbankedVolumeUnits` / `bankedScoreUnits`、`PickupAbsorbMath.TryAbsorbValueToLedgerGain` 的 out 参数改 `long`，**全部黄金值一个不动、场景不重导入**；**B** `valueUnits` 由 `int[]` 加宽到 `long[]`（`PickupFieldHost` / `PickupFieldSync` / `PickupFieldAuthoringRules` / `PlacementImporter`），数据形状变、语义不变，**所有关卡场景与预制体同批重导入**；**C** 换量子：`valueUnits` → `volumeMm3`，删 `c`（`cubicMetersPerVolumeUnit`）与 `baselineVolumeUnits`（改每关 `startVolumeMm3`），黄金值换成 mm³（`1356` 单位 → `≈5.56e9 mm³`），验收是**直径逐帧一致（1e-6 m）**；**D** 恢复原作数值形式（`V = k·a·b·c`、`penalty` 默认 0.8、门槛向下取整 mm、HUD +0.02 cm、群组连带子件），即 parity 表已拍 `restore` 的那几行。**C 与 D 不许合成一步**：C 错的表现是直径漂移，D 错的表现是成长曲线变。
- **（r）未验证项（要 Unity，不因本条变成已验证）：** UdonSharp 的 `long[]` 序列化字段（`long` 标量有先例，`long[]` 没有）、`JsonUtility` 与 `SerializedProperty` 对 `long` 的处理、重导入后的场景 YAML diff 大小。任一条不成立就退回「`int[]` 拆高低位」或「`int[]` + milli-unit」兼容档（[影响审计](Research/ContentVolume/FIXED_POINT_VOLUME_IMPACT.md) §4/§11）。本条只定表示与顺序，不改代码、场景或任务依赖；`c` / `valueUnits` 的退役发生在 C 步，**只做 A/B 不会让「unit 本身」这条分歧消失**。

### 堆叠传播：父件被吃 ⇒ 子件坠落，组用单父指针（2026-11-16 拍板）

同 (a)–(r)：这条是**玩法机制**的决定（owner 2026-11-16 拍板），不是测量结果，也不改任务依赖。它把 [道具行为](Design/PickupAbsorb/PROP_BEHAVIOURS.md) §10.4 的「组表 + 每 tick 复检」假设落成 v1 的确定形态，并结掉该文 §10.8 的第 1、2、5 条。

- **（s）父件被吃掉时，子件「掉」，不「带」。** 一件道具被吸收时，挂在它下面的子件**开始坠落**，**不**随之一起进账——v1 不做 `carry`（携带），子件不算父件的一部分。**组关系沿用原作的单父指针**：每件一个 `parent` 索引（-1 = 无父件），形状照抄原作的 `object dump.parentProp.monoControlIndex`（实测 1,970 条子件 / 1,082 个父件 / 单父最多 30 个子件 / 链最深 13 层）；「组」不另存表，由这个数组派生。运行时是**每 tick 浮空复检**：子件被放进定长待复检队列，每个定步 tick 问一次「脚下有没有东西」——没有就落，有就等，落了再把它的子件入队，所以传播是**跨 tick 一层一层往上爬**（1 被吃 → 2 落 → 3 看见 2 还在下面先等 → 几 tick 后 2 落开，3 才落）。**没有支撑者计数、没有「最多 4 个支撑者」的上限、不重建关系**；坠落仍是纯重力落到正下方最高的面、**落定不倾斜**（§10.2 不变）。状态 `resting` / `falling` / `settled` 属于**行为宿主自己的数组**，不塞进 `PickupFieldHost` 的 `Free`/`Attached`/`Hidden` 三态，两套只在「吸收」这一个事件上同步；数据结构只用定长并行数组（UdonSharp 没有 `List` / 字典 / 结构体 / 自定义类）。设计正文见 [PROP_BEHAVIOURS.md §10.9](Design/PickupAbsorb/PROP_BEHAVIOURS.md)。
- **（s）这是与原作的一处有记录的有意偏离。** 原作游戏内证据（速通/数据挖掘者的机制笔记 `raw_growth_mechanism.txt` 第 4 条，即本文档外的 挖掘者的本机笔记（第三方，不入库），非官方文档）说**跟着父件动的子件在父件被吃时一起进账**——那正是 `carry`。我们选「掉下来」的理由：`carry` 要同时满足「所有子件都过体积门」与「不能重复计收益」（[技术契约](02_TECHNICAL_CONTRACTS.md) §6 的红线），是独立一轮的工作量；机制上先让「吃掉底下那件，上面会掉」成立，再谈携带。口径后果：`parity` 审计的「支撑图（`rests_on`）」行按**原作的父指针形式**记 `restore`（本条即其 owner 确认），「群组物件体积」行（连带子件体积入账）**顺延到 v2**，`carry` 键位与词义保留、以后要做的就是多一条传播分支。
- **（s）实现状态与仍然开着的实现问题。** `parent` / `parentDerived` / `pinned` 已进 `PROP_KEYS`；校验器、编辑器 op `derive_supports`、`levelbuild.py check` 的报告行与 `levelctl.py derive-supports` 同批落地，`story_01_bench_size` 0 错 2 警告通过；C# 侧已实现（`PropFallHost` / `PropFallMath`、`propParentIndex` 列、同步写入，见 PROP_BEHAVIOURS §10.10）。**未在 Unity / UdonSharp 里运行过任何东西**。仍然开着的实现问题（浮空测试的精确形式、复检 tick 频率、每 tick 预算、坠落件被球碰到算吃掉还是挡路）逐条列在 PROP_BEHAVIOURS §10.9 末节，owner 未拍板。
- **（t）作者的指针不是几何断言。** `parent` / `stackedOn` 说的是「这东西属于那件、被那件带着」，可以完全不接触（挂着的招牌、摆在旁边的托盘、离冰柜 88 cm 的盆栽）。校验器只把它当**报告**：`levelbuild.py check` 每次打 `declared parent not seated: N`，`--report` 打逐件表格，代码 `[parent_not_seated]` 默认是 **warning**，**任何模式下都不因它变红**；`stackedOn` 不要求顶底对齐（没有 ±1 cm 硬错误），`parent_seat`（派生链越窗即错）与它同码。为什么敢放：运行期每 tick 复检「脚下有没有东西」，一条落空的指针只多一次检查、不会让不该落的落（参考模型 `Docs/Design/PickupAbsorb/prop_behaviours/floating_requeue.py` 有 `test_a_wrong_pointer_costs_one_check_not_a_fall`）。
- **（t）只有机器推导的链接受几何约束。** `parentDerived: true` 的边是 `supports.derive_supports` 按几何写进去的，它不再成立就意味着文件过期；`levelbuild.py check --strict-parents` 把这一类升成 **error**（提示重跑 `levelctl.py derive-supports --apply`），默认模式仍是 warning。所以「作者写法永远不红、工具写法可以红」——两件事分开。
- **（t）新字段 `pinned: true`：作者声明「这件自己站着，别让它掉」**（装饰、挂着的招牌、云）。校验器不再报它「悬空」（陷地、越界、穿结构照旧报）。运行期语义：**永不入队、永不下落**（入队前就跳过，所以它进不了 `FALLING`），但状态一直是 `resting`、**仍然是支撑面**（别人照常可以放在它上面），因此它是**坠落传播的终点**——A 上钉着 P、P 上放着 C，吃掉 A 之后 P 和 C 都不动；`pinned` 也可以与 `parent` 并存（挂在横梁上的招牌：指针说明它属于谁，`pinned` 说明梁没了也别掉），两者并存时 `pinned` 只管「别掉」，坏指针（不存在/自环/环/两字段矛盾）仍然是 error。显式把钉住的件吃掉时，它的子件**照常落**：「pinned 挡住几何传播」和「被拿走时孩子落」不矛盾，前者是几何事件、后者是显式事件。
- **（t）落点、口径与代价。** 代码：`Tools/levelbuild/placement_core.py` 的 `contact` / `parent_gap_window` / `seat_verdict` / `seating_report` / `_check_parents`，`Tools/levelbuild/supports.py:derive_supports`，CLI `levelbuild.py check --strict-parents --report`、`levelctl.py derive-supports`。实测：`story_01_bench_size`（130 件，8 条作者指针）**0 错 2 警告**，两条都是 `[parent_not_seated]` 且 `--strict-parents` 下也不升级。**未在 Unity / UdonSharp 里运行过**：C# 侧（导入器、`PickupFieldHost` 的行为数组）尚未接线，`pinned` 目前只是数据与规格。

## Owner decisions 2026-11-15/16 (summary list)

> 索引节：一行一条，只写「决定 / 一句理由 / 细节在哪」。带 (a)–(s) 的正文仍在上面各节，这里不重复；本节新增的条目以本节为记录。

- **原作优先** — 我们缺了原作明确有的结构或行为时，先当作「我们的设计可能错了」，默认 restore，除非写下四类理由之一。见 [AGENTS.md 原作优先原则](<../AGENTS.md>)、[PARITY_AUDIT 的 Divergences 表](Design/Parity/PARITY_AUDIT.md)。
- **只重建一关原作关卡来暴露引擎差异，地图定为 Make Cancer（不是 Make a Star 1）** — Cancer 的 22.2→80 cm 落在引擎舒适区，MAS1 的 5→10 cm 低于起始球、只留作下限探针；重建里原作球芯只当占位。见 [_ref_cancer README](Design/Levels/_ref_cancer/README.md)、[_ref_mas1 README](Design/Levels/_ref_mas1/README.md)。
- **不做爬升按钮** — 原作是推墙即爬、没有按键，加按钮等于新造第三种输入。见 [PARITY_AUDIT 的 `M07` 行](Design/Parity/PARITY_AUDIT.md)。
- **定步暂留 50 Hz，计划切 30 Hz** — 现有接触/子步参数按 0.02 s 标定过，切换单独立项；原作是 30 Hz。见 [PARITY_AUDIT 的 `T01` 行](Design/Parity/PARITY_AUDIT.md)。
- **几何校验是报告、`pinned` 是显式豁免** — 作者的 `parent`/`stackedOn` 不接触只报 warning，只有 `parentDerived` 的链在 `--strict-parents` 下红；新增 `pinned: true` 表示「自己站着、永不落」，校验器免检悬空、运行期不入队但仍是支撑面（传播终点）。理由是所有者要的摆法（托盘离长凳 7.5 cm、盆栽离冰柜 88 cm）不该被校验器禁止。见 [PROP_BEHAVIOURS §10.9「校验与 `pinned`」](Design/PickupAbsorb/PROP_BEHAVIOURS.md)、[LEVEL_AUTHORING §8 规则 5b/5c](Design/Tools/LEVEL_AUTHORING.md)、[PARITY_AUDIT 的「叠放公差」行](Design/Parity/PARITY_AUDIT.md)。
- **吸收门按原作算式，不单独调常数** — 物件体积 = 逐型 `k` × AABB 体积（默认 `k = 0.70`，薄片/蓬松/镂空按原作手调），门槛 `Req = floor_mm(∛(10·V·6/π))`；这是把 `k` 与取整接进同一条门，不是改 10×，实现为体积迁移 C 步之前的一个独立小步。见 [PARITY_AUDIT 的「体积定义（`k` 系数）」与「门槛取整」行](Design/Parity/PARITY_AUDIT.md)、[§5.3.2](Design/Parity/PARITY_AUDIT.md)；实现、口径与前后实测（`_ref_cancer` 0.222 m 球 91 → 184 件，与原作 184 件相同）见 [ABSORB_GATE_ORIGINAL.md](Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。
- **体积表示与迁移** — 定点 `long volumeMm3`（1 LSB = 1 mm³），A→B→C→D 四步各自独立验收；细节不在此重复，见上方 (r) 条。
- **Hub 流程 = 选关 → 开始 → 传送并直接入座** — 座位触发器不再是开始条件，省掉「走到球边再点球」的断点（[HUB_ROOM](Design/StoryMode/HUB_ROOM.md) 的入座步骤按本条改）。见 [HUB_LOOP_PLAN](Design/StoryMode/HUB_LOOP_PLAN.md)。
- **移除 80 ms 核心追赶弹簧** — 开着仍被读成「跳一下」，真因是价值定价（单件涨幅过大），不是缺平滑。见 [PARITY_AUDIT 的「球核心视觉」行](Design/Parity/PARITY_AUDIT.md)、[VISUAL_GROWTH_AUDIT](Design/BallPresentation/VISUAL_GROWTH_AUDIT.md)。
- **骑球 = 站姿手推球，触地点—球顶—视点成相似三角形**（owner 2026-11-18，VR 实测反馈「不是手在推着球，球在我脚底下」之后的决定；原作的小王子不缩放，骑手随球缩放是本项目的选择）— 玩家站在球后、脚在地上，手从肩向前下按在球背、手臂沿半径指向球心；眼高和眼点偏移都正比于球半径，平台缩放范围内保持同一站姿。站姿用玩家真实身体的比例（VR 的手是控制器），avatar 只经实测 η（站立头高 ÷ 眼高）修正眼高；Station 为站立式，座位让脚落地。骑乘面板（HUD、结果卡、下车面板）是随 E/1.64 缩放的世界空间面板，HUD 在左下球旁。推导、数字与 Unity 验证见 [RIDE_GEOMETRY](Design/BallLayers/FollowRig/RIDE_GEOMETRY.md)；η 的实际值、手到球面的实测距离、面板在头显里的可读性待 owner 亲测。
- **全局球径上限 400 m（owner 2026-11-18），所有地图适用。** 详见 (n) 与 [PARITY_AUDIT 的「球径上限」行](Design/Parity/PARITY_AUDIT.md)。
- **冲刺以《We Love Katamari REROLL+ Royal Reverie》为准；VR 不做双摇杆交替手势**（owner 2026-11-19）——用左扳机／桌面 Shift 长按替代交替输入：每次等效 `s16DashInputDuration=14` 帧，按 `s32DashWheelSpinData` 分档（3/4/5 次空转，+4 次自动出射）。空转后提前松手以 `f32DashHelpSpeedCoef=0.67` 弱冲，空转前松开不冲。蓄力复用普通滑行；出射倍率按球径来自 `s32SpeedD/s32SpeedF`，方向沿驾驶朝向；冲刺继续走普通推进，放大速度上限和前推加速；反推／撞墙退出，不按低速终止。寿命 `s16DashLife=90` 帧，耗尽喘气 `s16DashForbidTime=240` 帧，非冲刺非喘气每 `s16DashRecoverTime=10` 帧回 1。骑乘面板仍用 `RideDashInteractMath` 避免冲刺途中误触。见 [接触模型 §3.8](Design/BallMotor/CONTACT_MODEL_V2.md#38-冲刺wlk-规则与长按适配)。
- **吸附物冻结、不随球长大外移** — 与原作一致，外移会破坏「吸附瞬间定姿」的契约。见 [PARITY_AUDIT 的「吸附物位姿」行](Design/Parity/PARITY_AUDIT.md)。
- **球芯显示等体积直径跟随当前碰撞直径（owner 2026-11-17）** — 为消除粘附件与核之间的空腔，核随吸收/缩小直接更新，不用平滑弹簧。REROLL 玩家观察核会变大并吞入旧件，开发者确认镜头随球变大拉远；证据强度为“玩家观察 + 开发者镜头说法”，不是原作核/碰撞比例实测。`core_XX` 仍是按关卡命名的皮肤，不是尺寸档；玩家可切换，默认 `core_01`，关卡对应未知。见 [核皮肤与证据](Design/BallPresentation/CORE_SKIN.md)、[视觉核心差异行](Design/Parity/PARITY_AUDIT.md)。
- **道具行为按原作的分列结构：`kind` 与 `behaviour` 两列、纯重力坠落、落定不倾斜、v1 不做 `carry`、单父指针、父件被吃子件坠落** — 原作本来就是「内容分类」和「怎么动」两轴；「掉」比「带」少一轮体积门与重复计收益的工作量。见 [PROP_BEHAVIOURS §10.1–§10.3、§10.9](Design/PickupAbsorb/PROP_BEHAVIOURS.md)；其中坠落传播见上方 (s) 条，不在此重复。
- **碰撞体必须跟视觉形状，按型给代理，不用 AABB** — AABB 的角部比西瓜/玩具球多出几厘米，玩家会看到「没碰到就被挡」。见 [COLLIDER_PROXY](Design/PickupAbsorb/COLLIDER_PROXY.md)、[COLLIDER_LAYER](Design/PickupAbsorb/COLLIDER_LAYER.md)。
- **贴图：法线不得来自噪声，UV 必须有效** — fbm 噪声法线会把釉面/金属打成「湿报纸」；重叠或零面积 UV 不进 Unity。见 [TEXTURE_PBR_SPEC §10、§10.1](Art/OriginalModels/props/TEXTURE_PBR_SPEC.md)。
- **关卡格式与编辑器整体升级：研究已落地，v2 进入最终提案** — 地形/碰撞/旋转/缩放/父表/许可都已查清，字段与分层见 [LEVEL_FORMAT_V2](Design/Levels/LEVEL_FORMAT_V2.md)（`FINAL_PROPOSAL`）；Unity 侧尚未验证。仍然有效的原则：新键与校验器同批落地、未知键硬错误、不静默丢弃。另（2026-11-16）：**出生点与球起点允许是两个 marker**——参考数据 `ball.start` 与 `playerSpawn` 差 0.4 m（Cancer `[-5.0,-0.999,2.5]` vs `[-5.0,-0.599,2.5]`），导入建 `Markers/BallStart` 与 `Markers/PlayerSpawn` 两个；只有两者重合时才合成一个 `Markers/Spawn`。这是数据驱动，不是随手多建。描述符出生点（脚底）按我们的规则另算（球后 2.5 m、落在地形面、面向球），转换器 `AUTHORED_SPAWNS[...]['playerStart']` 把它写成 `meta.playerStart`（`position` 脚底 + `facingDeg`），规则见 [ORIGINAL_TERRAIN §2.4](Research/KatamariBallRules/ORIGINAL_TERRAIN_AND_LAYOUT.md)；导入器据此建 `Markers/PlayerSpawn`（位置 + 朝向）、把场景描述符的 `spawns` 指向它、令 `RespawnHeightY = killPlane.y − 1 m`，把球（`KatamariBallMotor.startCenterWorld` 与 `MotorHost` 位置）对齐到 `ball.start`，并在 `TerrainRoot/TerrainVisual` 上放非 convex 的 `MeshCollider` 供玩家站立（接口见 [LEVEL_FORMAT_V2 §2.2/§11.1](Design/Levels/LEVEL_FORMAT_V2.md)）；没有 `meta.playerStart` 的关卡行为不变。
- **地形必须支持真正的三维表面，纯 heightfield 不够** — 原作有洞、隧道和悬挑，所以碰撞至少要三角网 + 空间加速结构（大片简单区域可以再用 heightfield 当优化）。数据侧形态已定：静态三角网 + 碰撞 AABB broad phase + 烘焙网格；运行时接触分 T0–T4，每步成本与 Udon 传参仍待 Unity 实测。见 [ORIGINAL_TERRAIN_AND_LAYOUT](Research/KatamariBallRules/ORIGINAL_TERRAIN_AND_LAYOUT.md)、[NOCLIP_KATAMARI_FORMAT](Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)、[LEVEL_FORMAT_V2 §3/§13](Design/Levels/LEVEL_FORMAT_V2.md)。
- **noclip.website：不 vendor 它的代码，自己写解析器、出我们自己的格式** — **许可更正（2026-11-16）**：noclip 的 LICENSE 是 **MIT + 一段反编译说明**（GitHub 因此报 `NOASSERTION`），**不是 GPL-3**；原「GPL-3」前提有误。即便如此，逐行翻译仍属衍生作品，MIT 允许再分发但必须保留版权与许可声明。首选照规格重实现（已完成，981 行解析器在 `~/KatamariRef/`，仓库外）；仓库许可仍未定。见 [NOCLIP_KATAMARI_FORMAT §10](Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)、[LEVEL_FORMAT_V2 §19](Design/Levels/LEVEL_FORMAT_V2.md)。
- **关卡编辑器：Unity 式鼠标操作 + 区域统计（件数与总体积）** — 复用玩家/作者已经会的手感；区域统计用来验内容密度与供给。见 [LEVEL_EDITOR_WEB §7.1 与飞行/操作节](Design/Tools/LEVEL_EDITOR_WEB.md)。
- **VR 验收后置，不着急** — 先把桌面闭环与灰盒跑通，VR 放在 Build & Test 有内容之后。

### Owner decisions 2026-11-16（球径、活动区、坠落重生、运动与相位、缩放）

> 与上面 2026-11-15/16 同一天的第二批裁定：一行一条，细节在指针里，不在此重复。凡与旧条目冲突处（如 2.20 m 包线、D-10 的「先做子集」、D-11 的「≤64 个被动画实例」），以本节与随后的「追加」节为准。

- **活动区 = 硬墙 + 地图设计** — 参考关本来就是靠布局限制玩法范围的，不只是靠墙；越界后果 = 硬墙 + 球径门 + 掉出重生，软边距（减速/提示）不做。见 [LEVEL_FORMAT_V2 §5 与 D-4](Design/Levels/LEVEL_FORMAT_V2.md)、[HUB_ROOM](Design/StoryMode/HUB_ROOM.md)。
- **掉出重生 = 逐关 Y 的重启阈值，由回合状态机执行（不是全局地板）** — `activityArea.killPlane.y` 逐关写，运行时由 `StoryRound` 读它当重启阈值；没有回合状态机的世界才退回 `ActivityAreaHost` 兜底。见 [LEVEL_FORMAT_V2 §5.5 与 D-6](Design/Levels/LEVEL_FORMAT_V2.md)。
- **D-10：13 类运动全加** — 数据与运行时都进第一版，不先做子集；没测到参数的类（`wait_for_player`/`whack_a_mole`/`clouds`/`spin`）用 PROVISIONAL 默认并在诊断里标记，不用「先做位移类」拖后。见 [LEVEL_FORMAT_V2 §6.7 与 D-10](Design/Levels/LEVEL_FORMAT_V2.md)、[ORIGINAL_ANIMATION_OBSERVATIONS](Research/KatamariBallRules/ORIGINAL_ANIMATION_OBSERVATIONS.md)。
- **D-12：相位 = 确定性哈希 + 作者覆盖** — 「相位」= 这件从周期的哪一刻开始动；用 `itemId + level.id` 哈希派生，同一件每次进世界一致（账本/双端/回放不飘），作者可用 `phase` 覆盖；不用 `Math.random()`，原作种子来源仍未验证（U5）。见 [LEVEL_FORMAT_V2 §16.2 与 D-12](Design/Levels/LEVEL_FORMAT_V2.md)。
- **D-13：舞台动画现在不做** — noclip 没做舞台动画，不能反证原作没有；等原机证据，或用材质/Shader 另立项。见 [LEVEL_FORMAT_V2 的 D-13](Design/Levels/LEVEL_FORMAT_V2.md)。
- **D-14：下落期间挂起子件自己的运动，落定后恢复** — 与已定「子件坠落、不 carry」一致。见 [LEVEL_FORMAT_V2 的 D-14](Design/Levels/LEVEL_FORMAT_V2.md)。
- **逐件缩放 = 四层，不是放开**（2026-11-16 加第四层：非均匀 + 无代理） — ①运行时永不缩放（不变）；②作者态均匀缩放全允许（含挂接件：原作就有 1 件 0.1 挂在父件下）；③非均匀 + 有碰撞代理只允许无父、static、box/sphere；④非均匀 + **无代理**（def 不写 `collider`）允许无父、static、`absorbable:false`（实现记 `NonUniformNoProxy`；判据是「有没有代理」，不是「有没有碰撞体」）。原作 524 件里 6 件非单位（4×0.9、1×0.1、1 非均匀），忽略会让体积/门槛/渲染全错（0.1 那件门差 10×、非均匀差 28.7×）。「不许改尺寸凑档」仍然有效。唯一的 ④ 实例 `sparrow_001`（`k = 0.122` 的 1.7 cm 薄片）按「贴花」处理——渲染位置对、不碰撞、不可吸，**不再列为待定**（原选项 A）。见 [LEVEL_FORMAT_V2 §6.4、§18 与 D-5](Design/Levels/LEVEL_FORMAT_V2.md)。

### Owner decisions 2026-11-16（追加：性能口径、怀旧关命名）

- **性能口径：现阶段不预先优化（D-11 重写）** — 参考关卡是 2018 年的 REROLL 复刻（PS2 时代原作），性能要求本身极低，现阶段完全不必担心 Animator 数量、顶点数、骨骼数；**153 个骨骼动画实例直接挂普通 Animator/蒙皮，不做顶点动画纹理（VAT）**，「64 Animators 不够」的论证与 VAT 方案降级为「以后真测出瓶颈再看」，`caps.animators` 退成统计字段。**只放松「不预先优化」**：后续真实测量（P5 合成场景、真机帧率、Quest 实机）照旧要做。见 [LEVEL_FORMAT_V2 §6.7.6 / §16.2 / D-11](Design/Levels/LEVEL_FORMAT_V2.md)、[PARITY_AUDIT 的「骨骼动画的呈现（Animator 数量）」行](Design/Parity/PARITY_AUDIT.md)、[ORIGINAL_ANIMATION_OBSERVATIONS §5.4](Research/KatamariBallRules/ORIGINAL_ANIMATION_OBSERVATIONS.md)。
- **怀旧关正式名：怀旧关：蟹座小院 / Cancer Courtyard**（工作名 Nostalgia 可作别名） — 这个定名是给以后换皮发布的版本用的；内部 id 在换皮前仍是 `_ref_cancer`，不在 `_ref_*` 上直接改成正式内容（先换成我们自己的贴图/模型，再用正式名建关）。见 [_ref_cancer README](Design/Levels/_ref_cancer/README.md)、[原作优先原则](../AGENTS.md)。

### Owner decisions 2026-11-16（追加：子代理模型路由）

- **子代理模型：一律 Claude Sonnet 5.5、思考等级 high，搜索类调研同样走这条路由（owner 2026-11-16）** — 默认且唯一的子代理路由是 `pi-anthropic` + `claude-sonnet-5-5`，`reasoning_effort` 为 `high`，包括需要联网、搜索、无头浏览器、下载的工作。`provider`、`model`、`reasoning_effort` 三项同时传。见 [AGENTS.md 委派与 Git](<../AGENTS.md>)。

### Lead decisions 2026-11-16（触发门、判定体、运动更新率）

> 同日由 lead 代主人拍的三条技术裁定；不与上面 owner 的拍板冲突。

- **`WaitForPlayer` 的触发门与 `wake_radius_m = 12` 是两个概念，两者都保留、互不改**（owner = lead，2026-11-16） — 12 m 是**距离剔除半径**（[PROP_BEHAVIOURS](Design/PickupAbsorb/PROP_BEHAVIOURS.md) §7.2/§7.3，滞回 +3 m、出圈冻结但记相位），作用是别让 128 件全时跑动画；原作 200/300 cm（按 `sizeGroup`）是它的**行为触发门**。实现：给这一类单独加 `triggerRadiusM`（2.0 / 3.0；原作五档 200/300/500/700/900 cm，小件只用前两档），**12 不动**。两条后果一并写清：拿 12 当触发门 → 球在 12 m 外就把蟹惊跑（覆盖 452 m² vs 12.6/28 m²）；把更新半径压到 2–3 m → 院子里 128 件全冻、远处蟹全静止，比原作更假。另 8 件 `moveType 10`（`0x04`）的条件是 `inZone && parentRolled`，参考实现写死 false、原作真实条件未知（M2），**测量前保持静止、别猜**。见 [LEVEL_FORMAT_V2 §6.7.3/§6.7.5](Design/Levels/LEVEL_FORMAT_V2.md)、[PARITY_AUDIT](Design/Parity/PARITY_AUDIT.md)、[ORIGINAL_MOTION_PROGRAM §4.5/§14](Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md)。
- **运动更新率按「位移」分档：`hop` / `whack_a_mole` 必须 30 Hz**（owner = lead，2026-11-16） — 10 Hz 很容易漏采 Hop 跳顶，Whack 每 tick 错 35 cm；`sway` 15 Hz、其余 5–15 Hz，全量 1,810 次/秒只占 14.6%，我们的 50 Hz 定步直接够，**不需要为运动加速**（也别再拿「迁到 30 Hz 步长」去重算）。见 [LEVEL_FORMAT_V2 §6.7.3/§6.7.5](Design/Levels/LEVEL_FORMAT_V2.md)、[ORIGINAL_MOTION_PROGRAM §12.0/§6.3](Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md)。
- **原作 12 条 `Dummy Hit` 命中判定体 + 1 条 `Lalala` 不是可收集道具，v1 不建**（owner = lead，2026-11-16） — 524 条 spawn = 511 道具 + 12 判定体 + 1 精灵；判定体的原作用途未测，等实机测量后再定是否需要。见 [ORIGINAL_MOTION_PROGRAM §11.3](Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md)、[PARITY_AUDIT](Design/Parity/PARITY_AUDIT.md)。

### Owner decisions 2026-11-17（挡路与粘附观感）

- **还不能吸的道具挡住球（owner 2026-11-17）** — 可吸收性决定能否穿过道具，球不应穿过当前卷不动的实体；见 [接触与挡路规则](Design/BallLayers/Attachment/ATTACH_PLACEMENT.md#30-可吸收性与挡路)。
- **不做粘附嵌入：接触处冻结、贴碰撞球面（owner 2026-11-17）** — “浮”的主因是件大而少、空腔未填满，靠件小量多和填充密度解决，不靠沿径向往里压；见 [现行吸附位置](Design/BallLayers/Attachment/ATTACH_PLACEMENT.md#31-吸附位置触发即接触原地冻结)、[核与物件密度研究](Design/BallPresentation/CORE_SKIN.md#研究依据与边界)。

- **删除 v1 位置权威（owner 2026-11-17）** — 统一为一套位置逻辑，避免两份实现。

### 所有者决定 2026-11-20（球惯性）

- **惯性采用 WLK A2（owner 2026-11-20）** — 以《We Love Katamari REROLL+ Royal Reverie》v20230608 为基准，采用松杆匀滚阻和杆量限速，保留本作的绝对速度与场景推力；见 [BALL_INERTIA](Design/BallMotor/BALL_INERTIA.md#2-模型现行)。

### Owner decisions 2026-11-17（地图 0 沙盒亲测）

- **缓坡松手不滑保持现状：方案 A，滚动阻力照常作用（owner 2026-11-17）** — C26 2°、C27 5° 顺坡朝向时不滑，主人接受「有点阻力也行」；见 [BALL_INERTIA §4](Design/BallMotor/BALL_INERTIA.md#4-静止后下坡)、[PHYSICS_TEST_FIELD §4.9](Design/BallMotor/PHYSICS_TEST_FIELD.md#49-2026-11-17-的新格c25-墙--顶棚c26--c27--c28-缓坡)。
- **C25 受阻不算卡住，不做按进度释放（owner 2026-11-17）** — 墙与顶棚夹角里往前推不动正常，玩家靠后退脱出；见 [TERRAIN_CLIMB §6.7](Design/BallMotor/TERRAIN_CLIMB.md#67-墙与顶棚夹角受阻不等于卡住2026-11-17-拍板)。
- **C08/C09/C10 的 5/7.5/10 cm 台阶都能上，符合预期（owner 2026-11-17）** — 主人确认靠爬墙通道上去没有问题；见 [PHYSICS_TEST_FIELD §4.2](Design/BallMotor/PHYSICS_TEST_FIELD.md#42-台阶组-c07c12高度与球半径的关系)。
- **陡坡改体力制、与爬墙分开（owner 2026-11-17）** — θ≤45°正常滚不变；45°<θ≤60°慢坡爬、无固定高度上限，越陡/持续越久消耗越大，平地立即满，大球更容易；θ>60°墙仍保持1.5D上限与到顶掉落。60°为工程试验，不是原作值；保留速度门，冲刺通过同一门速度增速减耗。C06 50°/0.60m板的0.30m球在体力足够时应爬完。理由：REROLL逆向显示独立坡体力结构，统一墙cap不适用于坡；见[TERRAIN_CLIMB §0.1](Design/BallMotor/TERRAIN_CLIMB.md#01-体力坡爬2026-11-17-当前决定)。

### Owner decisions 2026-11-19（还原基准与 VR 复测）

- **还原基准改为「越新越好」：首选 We Love Katamari REROLL+ Royal Reverie（2023）（owner 2026-11-19）** — 手感、数值、资源、界面都可能在新版里优化过，所以新版是首选来源，初代 REROLL 数据退为对照。当前关「蟹座小院」也要评估能否换成新版数据：差异太大就先不换、先做其他工作，但最终要换。新版程序只读、提取物只放本机 `~/KatamariRef/wlkrr/`，不进仓库、不上传；每条采用的数值要注明来源版本。原作优先原则里的「原作」从此指这一版。
- **蟹座小院的可吸收判定按原作门槛（owner 2026-11-19，2026-11-22 修订）** — 按原作逐件的吸收门槛判定（原作本关球长不到 1.2 m 以上，所以这些件在原作里够不着）；原作没有「永久不可卷入」标记的大物件（石灯笼、拉门、圆桌等），球够大就能卷走，不按转换器推出的 `absorbable:false` 永久禁止。卷走同 id 孪生件后原地留下隐形固体的隐患要修。
- **推进方向箭头固定在球心前上方（owner 2026-11-19）** — 位置与大小随球的语义半径变化，不跟头部；仍指当前推进方向，不指目标或下一区域。见[推进方向箭头](Design/BallMotor/README.md#推进方向箭头本地球上方提示)。
- **未入座时可正常跳跃，滚球时短按 Jump 不下车（owner 2026-11-19）** — 座位已能禁止默认退座，无需禁用玩家的跳跃；仍可按住 Jump 1.5 秒主动下车，见[防退座与退出](01_GAME_DESIGN.md#防退座与退出)。
- **VR 冲刺只用左扳机，不支持双摇杆交替摇（owner 2026-11-19）** — 原作的 gacha 计数折算成按住时长。
- **体积回收曲线按新版做（owner 2026-11-19）** — 采用 WLK REROLL+ 数据里的回收比曲线；拿到数据前维持全局 0.8。
- **过关按原作类型与参数判定（owner 2026-11-20）** — 过关独立于评分；蟹座按初代 REROLL 的 B 型，420 秒到时以确认捕获数 ≥10 判定，80 cm 不参与过关。B 类计数对象是原生库任务表白名单里的六种蟹（共 133 只），没有配对条件，见[原作放置格式 §1](Research/KatamariBallRules/ORIGINAL_PLACEMENT_FORMATS.md)。见[剧情模式](Design/StoryMode/README.md)与[合集关卡清单](Design/StoryMode/ALL_MAPS_HUB.md#21-当前四关偏移与源数据来自-r47-盘点)。
- **蟹座异区物件不与球碰撞（owner 2026-11-20）** — 照初代 `hitOnAreaNo`，晒衣夹虽有模型却不是本关障碍，不再用静态盒挡球；见[区域碰撞格式](Design/Levels/LEVEL_FORMAT_V2.md#物件的活动区域号)。
- **球皮肤可切换（owner 2026-11-20）** — 原作 27 个球芯全部装上，玩家在设置板「球样式」行切换，默认 core_01，选择存 PlayerData `vrck.set.ballSkin`（存原作编号）；本批只换本地球，远端球的皮肤同步留待后续。见 [核皮肤](Design/BallPresentation/CORE_SKIN.md)。
- **透明叶子照初代 REROLL（owner 2026-11-20）** — 参考道具的 MASK 材质按 REROLL `CustomStandard` 做：硬裁切阈值 0.5、贴图关闭 mipmap（REROLL 只采 mip0），远处叶子不再被 mip 平均掉；远处像素闪烁与 REROLL 相同。见 [参考道具材质的透明模式](Design/StoryMode/ALL_MAPS_HUB.md#614-参考道具材质的透明模式)。
- **骑手随球放大、眼点抬高（2026-11-19）** — 吸附物挡前路，推球角定为 52°，即居中腿中轴离球面至少 0.03r 的最大整数候选；不改球径，见 [骑球几何 §1.1](Design/BallLayers/FollowRig/RIDE_GEOMETRY.md#11-ψ-的候选每单位-rpitch-0-时的视角)。

### Owner decisions 2026-11-21（对战玩法、大厅与观战、骨骼动画、资料与仓库）

- **对战保持 2025 作「スポーツ塊」的交分玩法**（owner 2026-11-21，选 C）— 最多 8 人（2026-11-23 改，见下节）、交分区、交分后球缩小、被吞后异地重生；不照两作原版的本地双人分屏对战改。两作原版对战是本地 2P 分屏、专用小图（初代 VS_00–04 每局 180 s 比直径；WLK 400 s、FAST/MANY 目标物），与 VRChat 联网多人不对口。依据：两作解包取证（2026-11-20）。交分规则以上文「交分与回合规则」(a)–(d)（2026-11-26 按原作实测改写）为准。第一版对战用原作的对战专用小图（不用蟹座小院），同时验证「原版地图转我们格式」的转换工具链是否可用；第一版对战图用 WLK REROLL+ 的 vs_2（场地 03_C_VS，整局单场地，不按球径切换场地）（owner 2026-11-21）；理由：最新作、单场地与交分时缩球不冲突、尺寸适合 4 人。之后两作的全部对战图都要做进来：初代 V_00–V_04 共 5 张，WLK vs_1–vs_3 共 3 张，玩家在多人对战里自己选图。
- **大厅引导板只展示、不传送**（owner 2026-11-21）— 板上只给信息，不承担传送。
- **走进准备区即准备**（owner 2026-11-21；区域形状见 2026-11-26 节：方形）— 采用 VRChat 官方 Player Join Zones 的做法（`OnPlayerTriggerStay/Exit`、`OnPlayerLeft`），不用点按钮报名。倒计时中有人走出方形加入区：剩 3 秒时锁定名单，之前离开即退出本局，之后离开不影响。
- **一局结束后所有人回中庭**（owner 2026-11-21）。
- **大厅按原作取色重做**（owner 2026-11-21）— owner 评价现在的大厅「太丑陋…超高饱和撞色就是丑…也没有什么空间功能区域划分，东西也都没有材质」。要求功能分区明确、所有表面有材质；面板分两种：能点的板、只看的屏。美术方向选定为方向一「暖木纸屋」（owner 2026-11-21）：材质贴图用生图制作（木地板、灰泥墙、水磨石），分区用带 3 m 拱门的隔墙。
- **观战可开关**（owner 2026-11-21）— 等待时在多人面板上显示，两种视角：鸟瞰（正交俯视相机叠加玩家位置标记）、跟随某个玩家的视角；第一版两种都做。
- **骨骼动物动画用原作状态名**（owner 2026-11-21，方案 A）— 状态为 idle/walk/escape/rampage/chase/special；被球吓到播 escape；rampage/chase/special 已装进控制器但不触发，触发条件待原生库逆向。见 [MOTION_HOST §6](Design/PickupAbsorb/MOTION_HOST.md#6-骨骼动画普通-animator)。
- **原作分析资料集中放 `D:\块魂原作分析\`**（owner 2026-11-21）— 分初代REROLL、WLK_REROLL+ 两个子目录，游戏本体原地只读。
- **仓库保持清洁**（owner 2026-11-21）— 被新实现取代的旧文件、旧代码、过时注释直接删，不留误导，与 [AGENTS.md](<../AGENTS.md>) 「只写现行决定」一致。

### Owner decisions 2026-11-22（大厅第一批、远景、蟹座大物件、对战开放问题）

- **选关板上对战卡的按钮只引导「去对战区」**（owner 2026-11-22）— 选图、准备、倒计时都在对战区完成，选关板不承担这些。
- **大厅第一批：四张生图平铺贴图入库，补面板缺字**（owner 2026-11-22）— 木地板、灰泥墙、水磨石、毛毡四张进仓库；面板缺的 29 个字要补，TMP 字体分小步重烘，先保存场景。方向见上文「大厅按原作取色重做」。
- **参考关卡的富士山等远景先按导出原样**（owner 2026-11-22）— 不做对照修改。
- **蟹座大物件按原作门槛处理**（owner 2026-11-22）— 见上文「蟹座小院的可吸收判定按原作门槛」。
- **对战开放问题 Q1–Q8 已定**（owner 2026-11-22；Q1/Q2/Q5 于 2026-11-26 按原作实测关闭）— 详见 [VERSUS_MODE §6](Design/Versus/VERSUS_MODE.md#6-开放问题)：Q4 吞噬得分仍开放；Q3 出生点按原作（调研中）；Q6 观战者也加载对战岛；Q7 2 人即可开局；Q8 中途加入者不补位，只能观战或排下一局。

### Owner decisions 2026-11-23（对战人数、vs_2 出生点、粘附物显示数量）

- **对战人数上限改为 8 人，每张图另配推荐人数**（owner 2026-11-23）— 小图（如 WLK vs_1、初代 V_00–V_04）建议 ≤4 人；超过推荐人数的图在选图时置灰或提示。
- **WLK vs_2 出生点用满原作 8 个槽**（owner 2026-11-23）— 修好组 0（便利店 `CONVENI03_F` 补碰撞）和组 2（`VSGREEN01_E` 原作 `u8NoCrash=1`，按原作改为不碰撞）；人少时照原作做法随机抽组。
- **球上粘附物件限显示数量**（owner 2026-11-23）— 远端球必须限，VRChat 不能同步大量物体。初步规则：自己的球显示「最大 10 件 + 最近 10 件」，其他玩家的球显示「最大 5 件 + 最近 5 件」；两组重复时跳过重复项、往后顺延补足（例：最大的那件同时是第 6 近，最近一组就取第 1–5 和第 7–11 近）。先按原作「深埋即删」隐藏已埋进球里的件（WLK 口径；交分缩小后不恢复），再从剩下的里选；见 [ATTACH_PLACEMENT §3.3](Design/BallLayers/Attachment/ATTACH_PLACEMENT.md)。单人部分已实现，远端球随对战联网批次做。
- **大厅家具素材优先用 tevy.dev 的 CC0 模型，可以入库**（owner 2026-11-23）— tevy.dev 的模型都是 CC0 1.0，每个模型带三档 LOD；大厅取最轻或中档，下载 GLB 后转成 FBX 放进仓库。Fab 的免费素材做备选（owner 已确认可用于其他引擎、可商用）；两边都没有的，用 Blender 加生图贴图自己做。原作参考资产不受本条影响，照旧不入库。入库前先出挑选清单（预览图 + 面数）给 owner 定；Fab 许可的入库口径见 2026-11-26 节。

### Owner decisions 2026-11-26（对战加入区、素材许可、大厅黑板与天花板）

- **对战准备区是一块方形区域**（owner 2026-11-26）— 玩家走进对战区里的方形区域即加入，不设 8 个站位、不做圆形准备圈；判定用 VRChat 官方接口（Player Join Zones / 玩家进出触发区），不自写判定。理由：官方示例就是方形区域，圆形能否实现不确定，且应优先用官方接口。见 [VERSUS_MODE](Design/Versus/VERSUS_MODE.md)。
- **Fab 标准许可的免费素材可以入 git**（owner 2026-11-26；owner 之后会把仓库改为私有）— 与 tevy CC0 一样，入库前先出挑选清单（预览图 + 面数）给 owner 定。
- **大厅中庭小黑板只做宣传页**（owner 2026-11-26）— 世界标题、主视觉、一句话介绍，不放高密度信息；「怎么玩」另找位置，方案待定。
- **大厅天花板保留白灰顶，只加单根木梁，不铺整片木板**（Lead 定，owner 授权「你推荐」，2026-11-26）。
- **对战交分规则全部照原作实测改**（owner 2026-11-26）— 逐跳结算、每跳 0.5 s、缩径＝开局直径 × 10%、单价 15（PROVISIONAL）、同时 1 个交分区每 60 s 换一处、局时 180 s、最后 60 s 隐藏他人分数、被吞保分保径、结算 3 档随机奖励并列都发、开局直径按图配置、大球移速随直径下降。规则正文在上文「交分与回合规则」(a)–(d)，实现细节见 [VERSUS_MODE](Design/Versus/VERSUS_MODE.md)。依据：对战视频逐帧实测（本地分析，未入库）。

### Still open


- **参考关卡：道具嵌进 scenery 固体 —— AABB 代理假阳性，已改用拟合代理（2026-11-16）** — 根因既不是摆放、也不是该不该挡，而是 `structures.solids` 用了**原始 AABB** 当接触形状——一棵树的盒子把整个树冠包进去，螃蟹贴着树根站就必然「嵌进去」。这违反仓库既有口径「碰撞体必须跟视觉形状、按型给代理、不用 AABB」（同文件「拾取层」§(h)/(l) 的同一理由：AABB 角部多出几厘米，玩家会看到「没碰到就被挡」）。改法：`structures.solids[]` 的接触形状改为 **def 的拟合代理**（`collider_proxy.fit_proxy`；schema 加 `shape:"proxy"` + `collider` + `proxySource`，见 [LEVEL_FORMAT_V2 §4](Design/Levels/LEVEL_FORMAT_V2.md)），**`size` 仍写 AABB、只用于支撑面（盒顶面）**——代理是圆/凹/斜的形状，「球能站在上面」仍要水平顶面，两件事分开，理由写在 §4。实测（同一份 `_ref_cancer` 数据、同一判据：8 个 OBB 角点沉进固体 > 2 cm）：**AABB 判据 47 件 / 最深 1.094 m → 拟合代理判据 24 件 / 最深 0.614 m**，21 件是代理粗造成的假阳性、0 件是改用代理后新出现的（树 `medium_tree_002` 从 15 件降到 7 件）。剩下 24 件是真的贴着代理站（`doghouse_001` 9 件、`medium_tree_002` 7 件…），v2 校验器写成 note（§9.2 只把**道具×道具**互穿定为错误），等实机看「球撞上去是否合理」再决定要不要 `meets`/豁免。**影响：C# 侧现在只认盒**，导入器要读 `shape:"proxy"` 的 `collider` 才吃到这个修正；读到之前按 `size` 盒工作。
- **静态固体的非 OBB 接触通道 — lead 2026-11-16 acknowledged，待新通道（不是 v1 阻塞）** — `structures.solids` 的接触形状已经是拟合代理（`shape:"proxy"`），但**电机的静态表只认 OBB**，所以参考关卡那 18 个凸包固体今天被「装箱成 OBB」（35 条 scenery 固体里 18 convex / 17 box）。要做真凸包/球/胶囊，需要一条新的静态非 OBB 接触通道，另需一条「只支撑不阻挡」的静态通道；导入器没有静默降级，逐件打 note + 汇总一行。见 [PARITY_AUDIT 的「静态固体的接触形状」行](Design/Parity/PARITY_AUDIT.md)、[LEVEL_FORMAT_V2 §12.1](Design/Levels/LEVEL_FORMAT_V2.md)。
- **地形接触的运行时成本**：数据侧（三角网 + AABB broad phase + 网格 + 侧车）已定；T0–T4 的每步成本、Udon 行为间数组传参、3,456 三角形实测未做（[LEVEL_FORMAT_V2 §13/§16.1](Design/Levels/LEVEL_FORMAT_V2.md)）。
- **已知可优化项：地形超大三角形（89 个 >3 m）限制 chunk 粗筛**（2026-11-16 记录，**现在不做**） — 参考关卡地形 3,455 个三角形里有 **427 个自身 XZ 跨度 >2 m、89 个 >3 m、22 个 >6 m、6 个 >10 m（最大 12.7 m）**。chunk 已经重切成空间连续段（58 块 ≤60 三角形，见 [REF_STAGE_VIEW §8](Design/Tools/REF_STAGE_VIEW.md)），但**一个 probe 仍会命中约 7.8 个块**：大三角形把所在块的 AABB 撑到下一个格子，XZ 预筛因此变钝。实测 k_t/probe 中位 **467**（改前 1,949，4.2×）。若把这 89 个 >3 m 的三角形细分到 ≤1.5 m，命中会降到 ~1 块/probe、k_t **约 80**（档案数字：细分后 262 → ~80 那一版实测见记录）；代价是三角形从 3,455 涨到约 6–7k（引擎 `MaxTriangles = 8192` 装得下，但要把本关的 `caps.terrainTriangles` 从 4096 提上去），且改动几何。**触发条件**：真机测到 `triTests`/`fullEvals` 超预算（例如 50 Hz 下接触求解 >1 ms）再做，不是现在。
- **LEVEL_FORMAT_V2 的 D-2 / D-3 / D-7（地形侧车、陡面分阶段阻挡、解析体保留）**：设计取舍，现行口径写在 [LEVEL_FORMAT_V2 §16](Design/Levels/LEVEL_FORMAT_V2.md)，等 owner 一句话确认。
- **仓库许可**：未选，且不再由 GPL 顾虑驱动——noclip 是 MIT；将来若移植第三方 MIT 代码，保留原版权/许可声明即可，首选仍是照规格重实现；任何解析器在许可选定前留在仓库外（见本节 noclip 条）。

## 需要在正式发布范围/终验前回答

| 事项 | 当前基线与选项（均未选择） | 影响 | 决定时点 |
|---|---|---|---|
| **首发地图数与地图选择** | A. 保留野餐草坪+玩具房两图目标；B. 明确降为一张完整体验，并由用户指定是哪张。野餐是纵切片起点，不等于已选为唯一首发图。 | 一图仍须完整公平/可玩验证；若降一图，必须经授权修订T37/T38/T40依赖与验收，未选地图应保留为后续、延期或另作决定；不能跳过T32或伪标完成。 | 在T39正式终验前明确范围；T39再依据实际证据作最终体验批准。批准前维持当前两图目标和原DAG。([候选与影响](Archive/Preparation/ReleaseRFC/comparison.md#L14-L27)、[批准纪律](Archive/Preparation/ReleaseRFC/comparison.md#L29-L38)、[任务依赖](../tasks.json#L1290-L1414)) |
| **正式公平门槛** | 可在正式试玩前先设数值阈值；也可先做明确标为“探索”的试跑，再设阈值；或选定定性评估方式。待定项目包括出生资源差、路线/交分时间差、重生最低资源、补货延迟、重复次数/统计方式及软锁标准。 | 若先探索，探索样本不能追认成PASS；若用定性评估，可复现性较低。T38四人性能测试条件与T39公平标准也不能靠该RFC自动确定。 | 用户/主管须在T39正式试验前明确标准；T39仍需真人证据。([公平阈值选项](Preparation/RuleDecisions/decisions.json)的`project.fairness_thresholds`条、[T38/T39门槛](../tasks.json#L1328-L1397)、[RFC未定阈值:L31-L34](Archive/Preparation/ReleaseRFC/comparison.md#L31-L34)) |

## 可暂缓至项目试玩/相应契约门禁

(a)–(s) 十五条已在上文各节决定（a–d 回合与交分，2026-11-26 按原作实测改写、e–g 地形与指纹、h–l 拾取与吸收口径、m–p 产品与工程口径、q 野餐布局、r 体积表示、s 堆叠传播）；上节 2026-11-15/16 汇总只做索引，不新增字母条目；本表只剩下列事项。

| 事项 | 已知与未决边界 | 需要决定的阶段 / 暂缓条件 |
|---|---|---|
| **回合结构与时长** | 原作对战实测：局时 3 分钟；站在同时 1 处的交分区里每 0.5 s 逐跳结算，区每 60 s 换一处、一局 3 处；最后 60 s 隐藏他人分数（见上节 (a)–(d)）。仍unknown：吞人收益、回合数。项目对战按最多 8 人交分玩法做（见上文 2026-11-21、2026-11-23 对战条）。([研究核查](Research/KatamariBallRules/README.md)、[决策表](Preparation/RuleDecisions/decisions.json)的`rules.match_format`与`rules.score_and_exchange`条) | 每跳单价 15 为 PROVISIONAL，随 T39 试玩复核；若要改变项目范围再由用户明确批准。([项目基线与优先级](00_SCOPE_DECISIONS.md#L15-L22)、[交存事务边界](Preparation/RuleDecisions/decisions.json)的`rules.score_and_exchange`条) |
| **KO、重生、PVP阈值与道具平衡** | 原作已确认：同尺寸弹开、可吞更小的对手；被更大的球吞掉＝已交的分保留、球径基本不变，镜头跟吞人方约 6 s 后在别处重生，重生后有短暂保护，**没有淘汰**；对战有宝箱道具与ブラザー（C6–C15，第一版不做）。项目取约 6 s 重生＋约 3 s 保护（PROVISIONAL）；吞人门槛先保留 1.35 倍（未测出）；吞人得分仍开放。([决策表](Preparation/RuleDecisions/decisions.json)的`rules.pvp_swallow_threshold`、`rules.ko_respawn`、`rules.powerups_in_pvp`条) | 相关参数须随T27/T39多人、公平与舒适证据调校；新增计时器等道具属于范围变更。([范围边界](00_SCOPE_DECISIONS.md#L25-L32)、[决策所有者](Preparation/RuleDecisions/decisions.json)的`rules.ko_respawn`条) |
| **生长/碰撞上限与世界尺度** | 语义显示尺寸、碰撞半径、拾取门槛与收益账本必须分开；0.2–5米只是表现工作区间，不是碰撞或技术上限。最终半径、成长段、速度/物理限值仍未定。([技术契约:L15-L21](02_TECHNICAL_CONTRACTS.md#L15-L21)、[决策表](Preparation/RuleDecisions/decisions.json)的`project.item_growth_and_collision`条) | 先保持参数化；测量须由平台、地图和公平/性能证据支持，再由Lead在T01/T05后处理契约。T06未冻结前不得硬编码为公共接口。([T06依赖与范围](../tasks.json#L253-L279)、[契约冻结规则](02_TECHNICAL_CONTRACTS.md#L3-L13)) |
| **付费外观SKU/价格** | 方向是免费完整玩法与可选纯外观；商品目录、价格、资格、授权和交易恢复均未批准。([决策表](Preparation/RuleDecisions/decisions.json)的`project.commercial_sku`条) | 可暂不决定并保持商店关闭；只有进入相应许可、权益和交易门禁后才需用户选SKU/价格。([发布配置](../tasks.json#L2094-L2105)、[范围排除](00_SCOPE_DECISIONS.md#L38-L42)) |

### 2026-11-17 合集场景 AllMapsHub

- **计时关允许中途回大厅 = 放弃本局；自由练习图随时可回** — 否则主人在沙盒里出不来，也没法中途换关；改掉了“`Running` 中回房一律拒绝”的旧规则。见 [ALL_MAPS_HUB](Design/StoryMode/ALL_MAPS_HUB.md)。
- **蟹座先不评星** — 蟹座的星级阈值没有数据来源，结果卡显示过关结论、螃蟹数与球径；大厅仍显示球径和总吸附件数。见 [ALL_MAPS_HUB](Design/StoryMode/ALL_MAPS_HUB.md)。
- **合集里的地形实验室用现有 C23 场景** — 不补 C21/C22/C24 的数据。见 [ALL_MAPS_HUB](Design/StoryMode/ALL_MAPS_HUB.md)。
- **合集场景放 `Assets/_RefUnlicensed/Scenes/AllMapsHub.unity`** — 含原作素材（蟹座），不进 git、不上传。见 [ALL_MAPS_HUB](Design/StoryMode/ALL_MAPS_HUB.md)。

## 不因本摘要改变的门禁

- P17中的单图方案只是提案；修改任务依赖只能在用户/主管批准后进行，并须运行计划校验。([RFC执行纪律](Archive/Preparation/ReleaseRFC/comparison.md#L36-L38)、[候选DAG声明](Archive/Preparation/ReleaseRFC/proposed_dag.json#L5-L7))
- (a)–(r) 的数值仍是 PROVISIONAL：决定记录的是“用这个暂定值往下做”，不是验证通过；T01 规则冻结、T21 地图验证与 T39 多人试玩后须复核，改动只需更新本文与相关配置，不动任务依赖。（(h)(i)(j)(k)(l) 是运行时契约决定：(k) 与 (l) 已于 2026-11-15 落地、(k)(l) 的数值仍 PROVISIONAL，(j) 没有暂定数值、代码已是该行为，(h) 的判定量由 (l) 定义，见各条“实现状态”。(m)–(p) 是产品口径与当前优先级：不含新的待验证数值，也不改任务依赖；(p) 明确现阶段不调数值。(q) 是野餐一角布局的内容量与通路取舍：U1 的 1500 u 与 U3 的四个设计尺寸仍是 PROVISIONAL，U4/U5 是保留代价的取舍（U5 待试玩复核），U2 已实现（同步工具只消费声明值/保留既有值，导入器单次写入并回读，见 U2 条）。(r) 是体积表示与迁移顺序：四步顺序是决定，A/B 不改数值、C 起动黄金值；`long[]`、`JsonUtility`/`SerializedProperty` 的 `long`、重导入 diff 大小三条仍未在 Unity 验证。)
- 本文不认证原作细节、不定正式公平阈值、不冻结公共接口、不改T任务状态；后续批准应另行明确记录。
