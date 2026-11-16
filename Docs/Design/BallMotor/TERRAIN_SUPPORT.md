# 地形支撑（Terrain Support）：局部支撑面、台阶、斜面，与关卡场地接口

> **2026-11-16 门已改**：吸收尺寸门已按原作数字改写——物件体积 `V = k·a·b·c`（`k` 缺省 0.70），门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`。本文里若出现 `a·b·c ≤ (π/6)·D³/10`、`absorbVolumeRatio`、`maxItemVolumeWorldM3`、「浮点比较、不取整」或按旧门算出的档位件数，那是 2026-11-15 的口径（历史）；现行口径、实现清单与前后实测见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。


> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15（Asia/Shanghai）。**只读调查 + 设计；作者不是 Unity 操作者**：没有编译、没有 Play Mode、没有 Build & Test、没有跑任何 Unity/NUnit 测试。文中所有米数、角度、倍数都是**本项目暂定值**（逐条标 `PROVISIONAL`），不是原作测量值；凡「原作」结论都带出处或标 `unconfirmed`。
> 触发：用户 2026-11-15 的地图原则原话——「一个场景，除了墙壁楼梯地板，整个全是靠可吸附物品搭出来的，比如台球桌上有杂物，有个长条木板搭在地板到桌上。那前期就是用户从木板滚上去去粘杂物，中期木板都能粘起来了，后期台球桌都能粘起来了。」迁移方案 [PICNIC_CORNER_LEVEL_ONE](../../StoryMode/PICNIC_CORNER_LEVEL_ONE.md) §3.5/§4.3（G1/G8/G9）查实：motor 只有「一张解析地面 + ≤8 平面 + ≤8 轴对齐盒 + ≤6 动态挡路盒」，**没有 step-up、没有坡、盒是实心墙**，所以花圃 0.26 m、树根丘 0.14 m、毯缘 0.06 m 全都只能是墙，「沿木板上桌」做不到。
> 范围：设计「球正下方最高的有效支撑」模型、台阶规则、斜面表示、可吸结构的三种身份、`LevelArenaHost` 接口、分阶段实施与验收。**本轮只写这一个文件**：不改代码、不改其它文档、不碰 Unity、不跑 git 写命令。
> 并行代理的接缝：`Runtime/BallMotor/KatamariBallMotor.cs`、`Runtime/BallLayers/RadialProxy/RadialProxyMath.cs`、`Runtime/Pickup/PickupFieldHost.cs` 正被另一个代理改「径向小棍代理驱动接地高度」（工作区 mtime 2026-11-15 06:23 之后仍在动）。本文**按符号名**引用，不写行号；分工写在第 7 节：**小棍决定「球的哪个点最低」，本文决定「那个点下面的支撑面在哪」**。
> 引用约定：`Runtime/…`、`Labs/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。
>
> **P1 已落地（2026-11-15，Linux 侧实现；Unity/U# 编译与 Play Mode 未跑）**：`SupportSurfaceMath`、motor 接线（支撑面/台阶/重力/诊断）、`LevelArenaHost`、NUnit 18 例 + Python 18 例。实现与本设计有**三处偏离**，逐条理由写在新加的 **§8.1**：①球有竖直重力、会从台面边缘掉下去（用户 2026-11-15 定，§5.4 的「没有重力」前提作废）；②支撑带（band）的参考高度改成 `max(上一步支撑面, 球心下方最低核心点)`，否则下落中的球会穿过脚下的盒顶；③`TryBakeRamp` 推迟到 P3（P1 没有斜面，不写未用代码）。§10.2 的默认值、§8 的 P1 表、§12 的校验状态都已按实现更新。
> **P2 已落地（2026-11-15，Linux 侧实现；Unity/U# 编译与 Play Mode 未跑）**：`SupportSurfaceMath` 新增道具角色分类/容量/道具面 id 与支撑表道具段；`PickupFieldHost` 一遍扫描出「挡路 / 支撑」两个掩码（`UpdatePropObstacles`），经 `SetSupportProps` 单一接缝进 motor；NUnit 15 例 + Python 15 例。规则在 **§8 P2 的「定型规则」**，与原文的偏离（分类函数放 `SupportSurfaceMath` 而不是新 `PickupObstacleMath`、不新增 `supportMarginM`、`blockingDisabled` 同时管两个角色、道具查询段 `alreadyAbove`）逐条写在那里。
> **P1 回归修复（2026-11-15，地图 0 的 C09 实测）**：0.30 m 球（量化后 D = 0.2997369、`stepLimit = 0.074934`）能**穿过** 0.075 m 静态盒——盒既不在扫掠里（被当台阶剔除）也没成为支撑（不在 band 内），正是本文禁止的「剔除扫掠却不能支撑」。根因是**两条判据**：`RefreshStepMask` 用 `IsSteppable(rise, limit, alreadyAbove, wasSteppable, +0.02 迟滞)`，支撑表用 `top <= reference + stepLimit`，且支撑表**从未**读过 `boxIsSteppable`；球在出生点离地 0.02 m 时掩码把盒锁成台阶（rise 0.055 ≤ limit），落地后单向迟滞不让它翻回墙。**修法**：`SupportSurfaceMath` 只留一条 `IsStepWithinLimit(top, reference, limit)`（一次减法、含等号）与一个 `SupportReferenceHeight(support, ballBottom)`；删除 `stepHysteresisM` / `stepStickMarginM` 与单向闩锁（去噪只由 §3.5 的刷新门控承担）。「已在顶上」由参考取球底自然成立，不再是第二条比较。
> **P1 第二次回归修复（2026-11-15，Unity 复测：a9aedf6 之后 C09@0.30 仍穿模）**：只共用判据还不够——**同一判据被算了两遍**。掩码在扫掠**前**用出生点参考（球底 0.020）算一次，支撑表在扫掠**后用落地参考（0）**再算一次；两次之间球落地、支撑高度一直是 0，旧门控（看支撑高度）不触发，掩码停在「台阶」，支撑表拒收 → 又回到「剔除扫掠却不能支撑」。**修法（按构造消除分歧，不再调门控）**：①静态盒的台阶结论**只算一次**（掩码）；`TryResolveSupport` 接收 `boxIsSteppable` 并对静态盒**直接沿用掩码结论**，不再重算 band——「掩码剔除 ⇔ 可当支撑」是同一个 bit；②门控下沉为 `StepMaskNeedsRefresh(maskValid, boxesDirty, D, maskedD, reference, maskedReference)`，比较的是**判据自己的输入**（参考高度与直径），不是选中支撑高度；③参考仍取 `SupportReferenceHeight(上一步支撑, 球底)`。斜面与道具没有「扫掠剔除」这一侧（它们从不进扫掠），所以继续用同一条 band 判据，没有同类分歧。回归：C09 出生/落地/0.075 盒/量化 D 的用例 + 门控用例 + 「掩码与支撑表对静态盒永远一致」的性质用例（NUnit `TheSweepMaskAndTheSupportTableNeverDisagree` 等，见 §8.1 D4）。
> **P3 已落地（2026-11-15，Linux 侧实现；Unity/U# 编译与 Play Mode 未跑）**：`SupportSurfaceMath` 新增斜面烘焙/校验/斜面段（`TryBakeRamp`/`TryValidateRamp`/`IsRampSurfaceId` 等）与可选切向重力；motor 新增 `AddRamp`/`ClearRamps`/容量/拒绝码/`GetSupportKind()==2`/`rampTangentialGravityScale`；`LevelArenaHost` 新增 `rampBoxes`（Setup 烘一次中心/尺寸/朝向，重复自检跳过 box）；导入器把 `role=ramp` 接进 `rampBoxes`。NUnit 15 例 + Python 15 例（另加 4 例掩码/下落回归）。规则与偏离写在 **§8 P3 的「定型规则」与实现记录**。

---

## 0. 架构先决：继续扩展自写扫掠（A），还是改用 Unity 物理查询（B）

> 这一节放在最前面，因为后面每一节的写法都取决于它。**结论：A。** 但先把证据摆清，包括「我查不到」的部分。

### 0.1 两案的定义

| 案 | 做法 | 影响面 |
|---|---|---|
| **A. 扩展自写扫掠** | `SweepMath` 已有的「平面 + 轴对齐盒」列表继续扩：加局部支撑面、台阶过滤、斜面；球的形状仍是**径向壳**（受 `RadialProxyShellMath` 驱动） | `SupportSurfaceMath` 新增；`KatamariBallMotor` 接线；`PickupFieldHost` 分类；`LevelArenaHost` 新增 |
| **B. 交给 PhysX 查询** | 地面/墙/斜板/台面用普通 `Collider`，球用 `Physics.SphereCast(All)` / `OverlapSphere` / `Raycast` + Layer 分层做几何查询；运动学仍自写（不用 `Rigidbody`），或干脆 `Rigidbody + SphereCollider` | 换掉 `SweepMath` 的角色、`GroundContactMath` 的来源、挡路盒系统、三层分离、以及全部离线测试 |

### 0.2 API 暴露：查到的、查不到的、必须 Unity 验的

| 断言 | 来源 | 判定 |
|---|---|---|
| `Physics.SphereCast` / `CapsuleCast` / `BoxCast` **未暴露**给 Udon；`Physics.Raycast` 部分可用 | 本地创作者知识库 `entities/api/not-exposed.md`（`source: UdonSharp wiki class-exposure-tree 解析`，标注 SDK 3.10.3、`confidence: High`、`last_review: 2026-06-11`） | **community / 需复核**：它是「解析 Class Exposure Tree」的二手结论，不是官方文档正文 |
| `Physics.SphereCast` / `SphereCastAll` / `OverlapSphere` / `RaycastNonAlloc` 可用 | 同一知识库 `entities/world/layers.md`（其 `sources` 列的是 Unity 官方 Layers/Raycast 文档） | **与上一条冲突**；该页是通用 Unity 物理清单，**不是 VRChat 暴露清单**，按本仓库规矩不能当证据 |
| Udon **图**里有 spherecast 节点（2020 年曾报过它的输出 broken） | [VRChat Canny #585](https://vrchat.canny.io/udon/p/585-broken-raycast-output)（用户报告原文 "tested with raycast and spherecast"） | `strongly implied`：节点存在过；**不证明** `UdonSharp` 的 C# 静态方法 `Physics.SphereCast` 被暴露，也不证明 3.10.5 的行为 |
| 权威判据只有一处：SDK 里的 **Class Exposure Tree**（`VRChat SDK > Udon Sharp > Class Exposure Tree`），绿=暴露、红=未暴露 | [官方文档 Class Exposure Tree](https://creators.vrchat.com/worlds/udon/udonsharp/class-exposure-tree/) | `confirmed`（机制）；**本检出没有 VPM 包源，读不到那棵树** |

**处理**：按本仓库既有做法（`Docs/Design/BallMotor/README.md` 对 `VRCInput` 的处理）——**不假设存在**。这一条标 `NEEDS_UNITY_VERIFICATION`，验证配方三步、约 3 分钟（由 Unity 操作者做，不是本文作者）：

1. 打开 `VRChat SDK > Udon Sharp > Class Exposure Tree`，搜 `Physics`，记录 `Raycast` / `RaycastAll` / `RaycastNonAlloc` / `SphereCast` / `SphereCastAll` / `SphereCastNonAlloc` / `OverlapSphere` / `SyncTransforms` / `autoSyncTransforms` 各自的绿/红；
2. 在 `Tests/EditMode` 加一个**只用来试探**的一行脚本（`Physics.SphereCast(...)`），`refresh_unity` 看是否 U# 编译报错；验完删掉，不留在仓里；
3. 同一棵树里顺带记录 `Rigidbody`、`Collider.bounds`、`Physics.ComputePenetration` 的状态（B 案要用）。

**但下面的推荐不依赖这一步**：即使全部暴露，A 仍然是正确路线，理由在 0.3–0.4。

### 0.3 七个判据

| # | 判据 | A（自写扫掠） | B（PhysX 查询） |
|---|---|---|---|
| 1 | **球的形状能不能表达** | 能。球的碰撞外形是 16 方向径向壳 + 最多 4 个长物件角点（`RadialProxyShellMath`；16 向表只是摘要，端点云才是精确输入），球长大时外形跟着变 | **不能**。`SphereCast` 只能投一个**球**（或胶囊/盒）。合同 §3.3 的 C 阶段（「代理实际影响运动」）在 B 下**永远做不到**，只能退化回球形 —— 这正是用户说的「球体物理本身就是核心技术」要保住的东西 |
| 2 | **一次查询能拿到多少信息** | TOI、最终位置、剩余位移、滑向、卡住、`stuck`、每条接触的**中心/法线/时间/障碍下标/穿透深度**；墙面去速只吃这些 | 命中点、法线、距离。**多接触同时求解**（墙角楔住、`ProjectionPasses = 4` 的迭代投影）没有对应物 —— 要的话得在 PhysX 命中之上**再写一个求解器**，等于两份权威 |
| 3 | **确定性 / 权威** | 自有固定步（`fixedStepSeconds = 0.02`、`maxStepsPerFrame = 5`）+ 纯函数 + float32；同一起始状态与同一 dt 序列可复现（README 已声明不承诺跨平台逐位一致） | 结果是 PhysX 场景状态的函数：物理步长不是我们的、`autoSyncTransforms` 由**工程设置**决定（不是我们能改的），而我们**用写 Transform 的方式移动球**——查询要么看到上一物理步的位置，要么依赖 `Physics.SyncTransforms()`（暴露未验）。跨端（PC/Quest）一致性更无法承诺 |
| 4 | **与「吸附物只是视觉、不做刚体外壳」** | 一致：吸附物只喂径向壳端点，**不产生 Collider**（合同 §2「不要让动态 MeshCollider 成为所有附着物的真实刚体外壳」） | 冲突：要让吸附物影响碰撞就得给它 Collider（合同明令反对），或者继续用径向壳 → 于是 B 只覆盖「地形」，球与地形的接触又变成两套几何 |
| 5 | **与 `COLLIDER_LAYER.md` 的三层分离** | 不冲突：视觉网格 / 玩家碰撞体 / 玩法体积 `propBounds` 三层各管各的，球只读第三层 | 冲突：B 要求玩法几何是**真 Collider**，于是「玩家能不能走过去」与「球能不能过」被绑成一份数据；该文档 §1 明确反对（"同一个物件，玩法体积含空腔、碰撞体不含"） |
| 6 | **可离线测试** | 全套：NUnit 纯数学 + `Docs/Design/BallLayers/*` 的 Python 参考模型 + `Docs/tests/*`。**这是本仓库唯一能证明「球对」的手段**（作者不是 Unity 操作者） | 不可：核心几何只能在 Unity+PhysX 里验，离线只剩「调用顺序」可测。`Docs/tests` 里 9 个 `ball_*` 套件对 B 案全部失效 |
| 7 | **迁移代价** | 增量：`SweepMath`/`GroundContactMath` 一行不改，只加选择与过滤 | 重写：`SweepMath`（约 400 行 + `sweep.py` 参考模型 + `SweepTests`）、`GroundContactMath`（平面 → 射线/多射线）、挡路盒（`BlockingPropMath` + `blocking_props.py` + 12 条测试）、`RadialProxyShellMath` 的接线、三个 lab 的接线；外加 Layer 工程设置 |
| 8 | **性能** | 每步：1–3 平面 + ≤14 盒 × 4 次投影 × ≤4 接触（未实测，Udon 解释器开销未测） | 单次 `SphereCast` 引擎级很快（µs 级），但多接触要 N 次投射；**两边都不是瓶颈**，差别不在性能 |

### 0.4 三条否决点（不是偏好，是结构性的）

1. **球的形状不是球。** `SphereCast` 的入参是一个半径；我们的球是 16 方向径向壳（方向数已定 16，此前 32 为 provisional），`maxProxyProtrusionWorldM = 0.30` 的凸起是合同 §3.3-B 已实现、C 阶段要接进碰撞的东西。选 B 等于把球锁死在球体上，**未来上限被写死**。
2. **扫掠的输出不是「命中」，是「解」。** 墙面去速（`TryCollectWallContactNormals` + `TryRemoveWallNormalVelocity`）要「指向障碍外侧的**已定向**法线」；墙角要两条法线依次作用；`stuck` 是「有接触但位置没推进」。PhysX 命中集没有「这一次扫掠是否卡住」的语义，得自己判 —— 判完就是第二份 `SweepMath`。
3. **我们用写 Transform 移动球。** 查询看物理场景，不看 Transform。要正确就得每步 `Physics.SyncTransforms()`（暴露未验）+ 承担它自己的开销；不调就是隐性滞后。这一条在 B 案里**无法回避**，只能选「滞后」或「依赖未验 API」。

### 0.5 B 真正的两个优势，以及它们在本项目的可用形态

| B 的优势 | 我们已有什么 | 还缺什么 |
|---|---|---|
| **作者用真 Collider 摆几何，不用手填数字** | `LevelArenaHost` 在 `Setup` 时读 `BoxCollider.bounds` 烘焙成 min/max（Lab 现在就是这么做的：`StoryLevelLabBootstrap.Setup` 里 `box.bounds` → `motor.AddBox`） | 斜面的烘焙（第 5 节：从 `Transform` 的 up/right + `BoxCollider.size` 反解）。**不需要 PhysX 查询** |
| **任意凹面/曲面的地形**（滑梯、隧道、弧形坡） | 没有：支撑模型只有「水平面 / 盒顶 / 平面矩形」，**表达不了曲面** | 逃生舱（仍属 A）：离线烘焙**高度场**（每格一个高度 + 法线）或三角汤 + 自写三角形扫掠。代价是新的数学与新的参考模型；触发条件见 0.6 |

### 0.6 结论与「什么会推翻它」

**选 A。** 一句话：B 买到的是「引擎级几何查询」，卖掉的是「球的形状」与「一份可离线证明的几何权威」；而作者便利（B 唯一立刻能兑现的好处）已经由「Setup 时烘焙 Collider」拿到，不需要把运行期交给 PhysX。

三个**会**让我改判的触发条件（写下来是为了以后不用重吵）：

1. 关卡的必需地形出现**曲面或凹结构**（滑梯、碗、隧道），且「高度场逃生舱」的数学量级超过 `SupportSurfaceMath` 的 3 倍 —— 那时是「A + 高度场」还是「B」，重新比。
2. C 阶段（代理影响水平碰撞）被**明确放弃**（用户说不要凹凸参与 PVP/碰撞）—— 那 B 的第 1 条否决消失一半，值得重评。
3. Unity 侧实测证明 `SphereCast` 未暴露，**但** `OverlapSphere`+`ComputePenetration` 暴露且够用 —— 这不会让 A 变差，只是把 B 重新扶上台面；仍受第 2、3 条否决点约束。

### 0.7 若最终选 B：本文哪些节要重写

| 节 | 在 B 下 |
|---|---|
| §3 支撑面模型 | 改成「向下 `Raycast`/`SphereCast` 打地形 Layer，取最近命中」；数据表示从「表」变成 Layer + Collider，容量讨论消失，成本变成每次投射 |
| §4 台阶规则 | 仍需保留（PhysX 没有 step-up）：但实现从「盒掩码」变成「命中法线朝天 + 高度差 ≤ limit 则允许进入」 |
| §5 斜面 | 大幅简化（Collider 天生带朝向）；但「球不会滑下」仍然成立（切向重力默认 0） |
| §6 三种身份 | **最难的一节**：可吸结构的顶面在 B 下必须是真 Collider，而它随球长大要**消失** → 运行期增删 Collider（Udon 能不能 `enabled`/`Destroy` 是另一组未验 API），且与 `COLLIDER_LAYER` 的三层分离正面冲突 |
| §7 `LevelArenaHost` | 接口不变（它本来就是作者层），但字段从 min/max 数组变成 `Collider[]` + Layer 掩码 |
| §8–§9 | 测试从「NUnit 纯数学」改成「Play Mode 场景验收」为主，离线证明能力下降 |

---

## 1. 七个问题一句话结论

| # | 问题 | 结论 |
|---|---|---|
| 1 | **支撑面模型** | 支撑 = 每步在**球心正下方**选出的**一个局部平面**（地面 / 静态盒顶 / 斜面 / 可吸结构顶面），选最高的那个；`GroundContactMath` **一行不改**，只把喂进去的 `(groundPlaneNormal, groundPlaneOffset)` 换成 `(surfaceNormal, dot(surfaceNormal, surfacePoint))`。复用 `AddBox` 的 8 个槽位，新增 4 个斜面槽 + 6 个道具支撑槽，每步 ≤18 个候选面、典型 1–3 个激活（§3）。 |
| 2 | **台阶规则（step-up）** | `stepLimit = max_step_height_over_ball_diameter × 语义直径 = 0.25 × D`（**沿用 `layout.json` 已有的 0.25**，不是我新定的）；`盒顶 − 当前支撑面高 ≤ stepLimit` 的盒**从扫掠里剔除**并同时成为支撑候选，否则仍是墙。同一条规则顺带消灭「2.8 cm 餐巾变隐形墙」（PICNIC §4.3 G9 / C9），而且是**尺寸相关**的，比 C9 提议的固定 5 cm 过滤更忠实（§4）。**静态盒只算一次**：台阶结论由扫掠掩码给出，支撑表直接读它（§4.3/§8.1 D4）。 |
| 3 | **斜面** | 一个**局部平面 + 水平足迹矩形**（中心 = 行走面上一点/法线/水平右轴/半宽/半长），坡度硬上限由单步钳位反推 `tan θ ≤ min(maxLift,maxDrop)/(maxSpeed·dt) = 1.25 → 51.3°`，设计护栏取 **45°**（序列化字段，超过则**带原因码拒绝**）；斜面**只支撑、绝不进扫掠**（否则坡底有隐形墙）。球**不会自己滑下坡**：motor 只有**竖直**重力（P1 起，只在失去支撑时作用，§8.1），切向重力是**默认 0** 的可配置字段（`rampTangentialGravityScale`，PROVISIONAL）。两端衔接要求「顶边与台面齐平（δ = 0）或 δ ≤ stepLimit」（§5）。 |
| 4 | **可吸结构的三种身份** | 吸不动 **且** rise ≤ stepLimit → **地形**（顶面进支撑表、盒从扫掠剔除）；吸不动 **且** rise > stepLimit → **挡路**（`SetBlockingBoxes`）；吸得动 → **食物**。前两条的分界只有一条几何规则，第三、四条的判据**只有** `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit`（同一个函数、同一个 limit，没有第二个阈值）。球站在要被吸的东西上时：它下一扫描离开支撑集，球按**重力**落下（P1 起；不是旧的 `maxDropPerStep` 钳位），不做特例（§6、§8.1）。 |
| 5 | **`LevelArenaHost` 接口** | 序列化「地面平面 + 边界平面数组（normal/offset）+ 静态盒数组（`BoxCollider[]`，Setup 时烘焙 min/max）+ 斜面数组（`BoxCollider[]`，按朝向烘焙）+ 允许的 kind + 是否自动开局」，`Update` 里等 `GetSemanticDiameterWorldM() > 0` 一次性注册并写一行 `[arena] planes=… boxes=… ramps=… supports=…`。**可吸结构的支撑由 `PickupFieldHost` 登记**（它已经拥有道具表和挡路集），arena 组件永远看不到道具（§7）。 |
| 6 | **分阶段** | **P1（已实现，见 §8.1）** 静态盒顶 + step-up + 重力下落 + `LevelArenaHost`（球能上花圃台、`climb_step` 门第一次成立）；**P2** 道具支撑/台阶拆分（消灭隐形墙，兑现「木板先当路」）；**P3** 斜面（兑现「沿木板上桌」）；**P4** 打磨（探针细化、防抖调参、性能实测、HUD 诊断）。每步的文件/测试/验收在 §8。 |
| 7 | **风险** | 五个真风险：台阶边缘抖动（三条防抖规则）、单步抬升穿模（台阶要用**自己的**抬升上限，覆盖 `maxProxyLiftPerStep = 0.04` —— 与小棍接线的最大冲突点）、`ContactCapacity = 4` 饱和（台阶盒被剔除后接触变**少**，反而缓解）、每步成本（≤18 面、典型 1–3）、镜头低通（5 Hz 滤波 + 单步上限，大落差读作「顿一下」）。逐条在 §9。 |

---

## 2. 现状事实（按符号名，不写行号）

| # | 事实 | 证据（符号） |
|---|---|---|
| F1 | 地面是**一张解析平面**：`(groundPlaneNormal, groundPlaneOffset)` 两个序列化标量，喂给 `GroundContactMath.TrySolve` | `KatamariBallMotor.groundPlaneNormal/groundPlaneOffset`、`StepBall` 第 4 段 |
| F2 | **`GroundContactMath` 是平面通用的**：`support = max(coreRadius, max_i −dot(n, Q·p_i))`、`target = offset + support`、位移只沿 `n`。**它对 `n` 不假设朝上** | `GroundContactMath.TrySolve` |
| F3 | 因此「局部支撑面」**不需要改这一层**：传入斜面/盒顶的 `(n, offset)` 即可，`offset = dot(n, P)`（`TrySolve` 内部会除以 `|n|`，传单位法线即可） | 同上；`offset = rawPlaneOffset / rawPlaneLength` |
| F4 | 扫掠障碍 = **≤8 平面 + ≤64 轴对齐盒**（2026-11-16 由 8 提到 64），另加 **≤6 动态挡路盒**；超出**显式返回 false 并 latch 原因**（不是静默丢弃） | `MaxPlaneCount`、`MaxBoxCount`、`MaxBlockingBoxCount`、`SignalOverflow` |
| F5 | 盒是**实心 AABB**，扫掠只把球停在盒外；**没有任何 step-up 通道**；盒没有旋转 | `SweepMath.TrySweepSphere`、`AddBox(min,max)` |
| F6 | 扫掠数组**按总条数重建**，`SweepMath` 用 `array.Length` 当障碍数（数组不能留空槽） | `RebuildObstacles`、`BLOCKING_PROPS_PLAN` §5.2 |
| F7 | 单步抬升 `maxLiftPerStep = 0.1`；**有径向壳时，「代理接地」那一次求解的抬升上限降到 `maxProxyLiftPerStep = 0.04`**（喂核心球心那次仍是 0.1） | `StepBall` 里 `proxyLiftCap`、两次 `GroundContactMath.TrySolve` |
| F8 | 视觉滚动吃**地面法线**：`VisualRollMath.TryAdvanceRotation(rotation, displacement, groundNormal, …)` —— 坡上必须换成支撑面法线，否则滚轴是错的 | `StepBall` 第 8 段 |
| F9 | **P1 之前**没有重力、没有切向加速度：`planarVelocity` 是 XZ，`y` 恒 0；「下落」只是 `GroundContactMath` 的 `maxDropPerStep = 0.1` 钳位。**P1 起竖直重力生效**（9.81 m/s²，只在失去支撑时作用；§8.1）。**P3 起切向重力是可配置字段 `rampTangentialGravityScale`，默认 0（= 不存在）** | `BallMotionMath.TryStep`、`maxDropPerStep`、`SupportSurfaceMath.TryApplyRampTangentialGravity`、§8.1 |
| F10 | 挡路盒由宿主**整批替换**，来源是玩法体积 `propBounds`，谓词是 `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, limit)`；`limit` 由电机 `GetAbsorbVolumeLimitWorldM3()` 给 | `PickupFieldHost.UpdateBlockingProps`、`SetBlockingBoxes` |
| F11 | 球的「最低点」由径向壳决定：`RadialProxyShellMath.TryBuildContactPoints(coreRadius, protrusionCap, endpointPool, endpointCount, localProxyPoints)`，点是从球心出发的**局部偏移**，世界点 = `center + Q·p` | 同上（并行代理在改这一带） |
| F12 | 全仓运行时代码 **0 处** `Physics.` / `Rigidbody`（唯一 `SphereCollider` 在 `Labs/StationPlatform/StationPlatformLab.unity`） | `grep -rn "Physics\.\|Rigidbody" Runtime/ Labs/` = 0 命中 |
| F13 | 台阶数据**已经在数据侧**：`scale.max_step_height_over_ball_diameter = 0.25`、`scale.max_route_step_m = 0.05`、`scale.max_traversable_slope_percent = 12.0`；校验器按「台阶高 ≤ 0.25 × 球径」判 `climb_step` | `Docs/Design/Maps/PicnicLawn/layout.json`、`Tools/mapbuild/validate_map.py` |
| F14 | 但运行时**没有任何一处读 0.25**：`climb_step` 今天只是作者注记（PICNIC §3.5.1、§4.3 G1） | 全文 0 处消费方；本文是第一个 |

---

## 3. 支撑面模型

### 3.1 核心决定：支撑是「一个局部平面」，不是高度场

**不做高度场。** 高度场要一张网格、要插值、要离线烘焙，而本关的地形只有「平面 / 盒顶 / 矩形斜面」三种，用**平面表**就能精确表达（没有任何近似）。代价是曲面表达不了（§0.5 的逃生舱）。

每步只选出**一个**面，把它的 `(n, offset)` 喂给现有的 `GroundContactMath.TrySolve`。这样：

- 「多接触」在支撑侧不存在竞争 —— 球在 20 ms 内不可能同时被两个面支撑（真要过渡，是两个面高度差 ≤ stepLimit 的连续情形，见 4.2）。
- `GroundContactMath` 的 `maxLiftPerStep` / `maxDropPerStep` 语义**原封不动**（钳的是沿 `n` 的修正量）。
- 支撑面的法线同时是**滚动法线**（F8），坡上的视觉滚动自动正确。

### 3.2 四种来源与数据表示

| 种类 | 数据 | 容量 | 谁登记 | 备注 |
|---|---|---|---|---|
| **Ground**（地面平面） | 复用现有 `groundPlaneNormal/Offset`；新增 `SetGroundPlane(n, o)`（补 PICNIC §4.3 G2 的缺口） | 1 | `LevelArenaHost` | 永远是最低的兜底；查询永远不返回「无支撑」 |
| **BoxTop**（静态盒顶面） | **复用 `AddBox(min,max)` 的 8 个槽位**：盒的 `x∈[min.x,max.x]`、`z∈[min.z,max.z]`、支撑面 `y = max.y`、`n = (0,1,0)`、`offset = max.y` | ≤8（与扫掠同槽） | `LevelArenaHost` | 一次 `AddBox` **两用**：扫掠(墙) + 支撑(顶面)。**不需要**第二个注册调用，也不需要新容量 |
| **Ramp**（斜面/斜板） | 5 个标量 + 3 个向量：`center`（**行走面**上的一点，不是盒中心）、`normal`（单位，`n.y > 0.05`）、`right`（水平单位 = 作者右轴的 XZ 投影）、`halfWidth`（沿 `right`）、`halfRun`（沿 `down` = 法线的水平分量；斜面法线向**低侧**倾斜，所以 `down = normalize(n.x, 0, n.z)`，不是 `-n`）。支撑平面 = `dot(n,P) = dot(n,center)`，足迹 = 水平矩形。面 id = `FirstRampSurfaceId + i`（500..503） | ≤4 | `LevelArenaHost` | **不进扫掠**（见 §5.5 的作者陷阱）；坡度 > 上限时**拒绝注册**并带原因码（§5.2） |
| **PropTop**（可吸结构顶面） | `(min,max)` 两个向量，**与挡路盒同一份 `propBounds` + 同一个 Transform 位置** | ≤6 | `PickupFieldHost` | 与挡路集**同源、互斥**：一件道具在同一扫描里只能落进两者之一（§6） |

> **为什么 PropTop 不复用 `AddBox`**：静态盒是「场景几何」，注册一次永不删；道具会消失（被吸走）、会重生，且来源是 `propBounds` 不是场景 Collider。把它们塞进同一组槽位会让「清空静态障碍」(`ClearObstacles`) 与「道具状态刷新」互相踩。分开容量，就没这个问题。

**UdonSharp 限制**：没有用户结构体（`AGENTS.md`），所以 Ramp 用**并行数组**：`rampCenter[4]`、`rampNormal[4]`、`rampRight[4]`、`rampHalfWidth[4]`、`rampHalfRun[4]`；PropTop 用 `supportPropMinima[6]`、`supportPropMaxima[6]`。与 `planeNormalBuffer`/`boxMinimumBuffer` 的写法一致。

### 3.3 每步查询

```
输入：球心 C（世界）、语义直径 D、上一步的 supportSurfaceY、stepLimit = kStep·D、
      静态盒掩码 boxIsSteppable[]（扫掠侧算好的唯一结论）
1. 探针 P = (C.x, C.z)                         ← 球心正下方（见下「为什么是球心」）
2. 静态盒：候选 ⇔ boxIsSteppable[i]            ← 掩码的同一个 bit，这里不重算
   地面/斜面/道具：激活带（bandReference = SupportReferenceHeight(上一步支撑面, 球心下方最低核心点)，
   否则下落中的球会穿过脚下的盒顶；见 §8.1 D2）——用 `IsStepWithinLimit`：
     面 i 激活 ⇔ height_i(P) − bandReference ≤ stepLimit      // 一次减法，含等号
3. 在候选面里取 height_i(P) 最大者 → surfaceId、surfacePoint P_i、surfaceNormal n_i
4. 输出 (n_i, dot(n_i, P_i))，喂 GroundContactMath.TrySolve
```

> **为什么静态盒读掩码而不是自己再算一次**：2026-11-15 的 C09 两次回归都出在这里。第一次是掩码与支撑表用两条不同判据；第二次是**同一条判据算两次**，两次调用之间球落地（参考从 0.020 变 0）而支撑高度没变，旧门控没刷新掩码。现在掩码是**唯一结论**，支撑表只是它的读者；门控 `StepMaskNeedsRefresh` 看的是判据的输入（参考与直径），见 §4.3/§8.1 D4。斜面与道具没有扫掠侧，不存在这个分歧，所以继续用 band。

`height_i(P)` 按种类：

| 种类 | `height(P)` | `inside(P)` |
|---|---|---|
| Ground | 平面方程解出的 y（`n.y > 0` 时唯一） | 恒真 |
| BoxTop | `max.y` | `P.x∈[min.x,max.x]` 且 `P.z∈[min.z,max.z]`（**含等号**：贴边算在面上） |
| Ramp | `(dot(n,center) − n.x·P.x − n.z·P.z) / n.y`（与 GroundContactMath 同一平面，高度无近似） | `|dot(P−center, right)| ≤ halfWidth` 且 `|dot(P−center, down)| ≤ halfRun`（含等号） |
| PropTop | `max.y` | 同 BoxTop |

**为什么探针是「球心正下方」而不是「接触点正下方」**（这条要留着，别让后来的人以为是偷懒）：

- 对**任何平面**，球心高度的闭式解是 `y_center = planeHeight(C.x, C.z) + r / n.y`，而 `GroundContactMath` 算的正好是它（`target = offset + support`，`support = r`）。**所以球心探针在高度上没有任何误差**，斜面也一样。
- 唯一近似发生在**边的判定**：球心越过盒沿 0.3 m 内时，球实际还压在棱上，但查询已经换成下面的面 → 球会**早一点点掉下去**。这个误差被球半径本身盖住（任何高度场游戏都这样），且**偏保守**（宁可早掉，不会悬空）。
- 接触点在斜面上相对球心沿坡向下偏 `r·sinθ`（45°、r=1.1 时 0.78 m）；但**那只影响「哪一个面在球下面」，不影响高度**（上面的闭式解）。只有「球心在 A 面、接触点压在更高的 B 面」这种骑棱情形才需要真接触点——留给 P4（§8）的可选细化，代价是多一次 32 点扫描（见 3.5）。
- 有径向壳时，「球心正下方」与「最低点正下方」的差 ≤ `maxProxyProtrusionWorldM = 0.30 m`，所以骑棱误差有界。**把这条界写进契约**，不要让它随小棍的调参漂。
- **P1 落地时就存在的另一面**（§9 R13）：探针在球心，所以**上台阶**是「球心进入足迹才抬升」，在此之前球的可视外壳会先压进台阶（`D = 1.10`、0.26 m 台阶时最多约 0.47 m 的水平行程）。设计接受这个误差（P4 的探针细化可修），但它是看得见的，不要当成 bug 再查一遍。

### 3.4 与 `AddPlane`/`AddBox` 的关系（能复用就复用）

| 现有 API | 在支撑模型里的角色 |
|---|---|
| `AddPlane(n, o)` | **不参与支撑**。它登记的是「单侧无限半空间」，即墙/边界；支撑需要**有限足迹**，而无限平面会变成「天上的地板」。边界平面（`[−19.40]` 那四面）继续只当墙 |
| `AddBox(min, max)` | **两用**：扫掠盒 + 顶面支撑。这是本设计最省的地方——**存量 lab 一行不改就获得了「可站上去的盒顶」**（但仍受 4.7 的开关保护，见 8.1） |
| `SetBlockingBoxes(minima, maxima, count)` | 保持原义（挡路）；新增平行的 `SetSupportProps(minima, maxima, count)` 给同一件道具的「当台阶/地形」身份 |
| `ClearObstacles()` | 连带清支撑面（语义是「所有障碍」）；静态盒/斜面表一起清 |

### 3.5 容量与每步成本

**容量**：`MaxPlaneCount = 8` / `MaxBoxCount = 64`（2026-11-16 由 8 提到 64）/ `MaxBlockingBoxCount = 6` + **新增** `MaxRampCount = 4`、`MaxSupportPropCount = 6`。最坏候选面数 = 1(地面或地形) + 64(盒) + 4(斜面) + 6(道具) = **75**；激活面数通常 1–3。

**每步成本**（与已吸附物件总数无关）：

| 阶段 | 成本 | 频次 |
|---|---|---|
| 激活掩码重算 | 19 次 `height()` + 比较 ≈ 19 × 12 flops ≈ 230 | **仅当** `|ΔsupportY| > 0.005` 或 `|ΔD| > 0.002`；稳态 0 |
| 选面（argmax） | 激活面数 × (足迹判定 4 比较 + 1 次 `height()`) ≈ 3 × 12 = 36 flops（最坏 19 × 12 = 230） | 每步 |
| 探针细化（P4 可选） | + 32 次点积 + 缩放 ≈ 200 flops | 每步，**只在启用时** |
| 对比：现有扫掠 | 1–3 平面 + ≤14 盒 × `ProjectionPasses = 4` × ≤4 接触 | 每步 |

结论：**支撑查询是扫掠的一个零头**。真正的 Udon 风险不是 flops，是**方法调用**：所以查询必须写成**一个静态函数吃数组**（`SupportSurfaceMath.TryResolveSupport(...)`），不要每个面调一个 helper、不要 `Vector3` 属性链、不要分配。50 Hz 下每步 36 flops 的预算可以放心。

**但有一个真成本要写清**：台阶过滤会让扫掠数组的**条数**变化，而 `SweepMath` 用 `array.Length` 当障碍数（F6）→ 条数变化就要 `new Vector3[]`（`RebuildObstacles` 现有的行为）。触发条件是「某个盒跨过 stepLimit」，属于**低频**（球径变化或爬到新高度时），但**不是零**。P4 必须实测 GC；备选方案（把过滤下沉进 `SweepMath` 加一个 `maxStepHeight` 入参）写在 §8 P4，且要同步改 `sweep.py` 参考模型，不能只改 C#。

### 3.6 与 `GroundContactMath` 的接法（**它一行不改**）

```csharp
// 现在（StepBall 第 4 段）：
GroundContactMath.TrySolve(coreRadiusWorldM, visualRollRotation, localProxyPoints,
    groundPlaneNormal, groundPlaneOffset, sweptCenter, proxyLiftCap, maxDropPerStep, ...)

// 之后：只换第 4、5 个入参，并让抬升上限随「是不是台阶」变（§4.4）
GroundContactMath.TrySolve(coreRadiusWorldM, visualRollRotation, localProxyPoints,
    supportNormal, supportOffset, sweptCenter, liftCap, maxDropPerStep, ...)
```

`supportOffset = dot(supportNormal, supportPoint)`。**这一层不知道地形从哪来**，符合「装配层只搬运和顺序」。

三处必须一起改的**消费方**（否则功能"看起来好了但滚动是错的"）：

1. **视觉滚动**：`VisualRollMath.TryAdvanceRotation(..., groundPlaneNormal, ...)` → `supportNormal`（F8）。
2. **跟随架**：不改（`FollowRigMath` 只吃位置 + yaw）；但它读的是**空代理数组**解出来的核心球心（`followCenter`），所以台阶抬升会让镜头抬一下 —— 见 §9 R5。
3. **诊断**：`[story-lab] … bottom=` 那一行（`StoryLevelLabBootstrap.Report`）在坡上不再是「球心 − 直径/2」，要改成「球心 − 支撑面」，否则验收会误判。这是本设计**会打破的既有验收口径**，必须一起改。

---

## 4. 台阶规则（step-up）

> **2026-11-16 起本条只描述 v1 的静态盒台阶（回滚口径）。** 台阶/爬升的当前决定是
> [TERRAIN_CLIMB.md](TERRAIN_CLIMB.md) §6：连续爬升 `H_cap = 1.5·D`、`h<r` 棱滚越、超限掉落；
> `0.25·D` 在 T2 里只剩诊断。改任何与「上台阶」有关的实现或判据前先读那篇。

### 4.1 公式与来源

```
rise(盒)  = 盒顶 y − 当前支撑面在探针处的高度 y
stepLimit = max_step_height_over_ball_diameter × 语义直径 D
rise ≤ stepLimit           → 台阶（可上）
rise >  stepLimit           → 墙
```

- **k 的值不是我定的**：`layout.json` 的 `scale.max_step_height_over_ball_diameter = 0.25` 已经在数据里，校验器已经按它判 `climb_step`（F13），只是**运行时没有消费方**（F14）。本文让它第一次生效。原作的比例**无公开说明**（`Docs/Research/KatamariMaps/README.md` §6 P8：「原作只确认『只能吸比球小的』，能否翻越高差无公开说明」，标 `unconfirmed`）——所以这是**我们自选的 0.25**，属 `PROVISIONAL`。
- **注意单位**：任务书写的是 `k·r`（r = 半径），数据写的是 `0.25 × 直径`。两者是同一个数（半径口径下 k = 0.5），但**实现只许用直径口径**，因为数据、校验器、关卡门全是直径口径，混合一次就会出现「26 cm 台面 1.10 m 球能上、1.20 m 球不能上」这种反向 bug。
- **数值实例**（本关）：0.26 m 花圃台 → 需要 `D ≥ 1.04`。这与 `gate_bed_*` 的 0.95→1.10 m 门**对得上**（1.04 落在中间），也就是说 **P1 落地后，`climb_step` 第一次真的成立**，PICNIC 里「花圃只能当墙」的结论随之作废。
- 尺寸实例：`D = 0.30 → 0.075`；`D = 0.83 → 0.208`（野餐篮 0.22 差一点）；`D = 1.10 → 0.275`；`D = 2.20 → 0.55`。

### 4.2 三态判定（一个谓词，两处使用）

| 状态 | 条件 | 扫掠 | 支撑 | 玩法读法 |
|---|---|---|---|---|
| **台阶 / 地形** | `rise ≤ stepLimit` **或**「球已在盒顶之上」（4.5 规则 2） | **剔除** | **入表** | 滚上去 / 站上去 |
| **墙** | `rise > stepLimit` 且不在盒顶之上 | 保留 | 不入表 | 挡住（现状行为） |
| **不在场** | 道具被吸走 / 状态非 `Free` | 剔除 | 剔除 | 食物（§6） |

「台阶」与「地形」在运行时**没有区别**：都是「顶面支撑 + 侧面可穿过」。区别只在作者语义（`layout.json` 的 `climb_step` vs `drivable_grade`），运行时不需要读那个字段。

### 4.3 机制：把可上的盒从扫掠里剔除（掩码）

**为什么必须剔除**：扫掠是**保守的扩张 AABB**——球心在 `y = r` 时，0.026 m 的餐巾对 1.2 m 的球也是一堵墙（`BLOCKING_PROPS_PLAN` R1 已记录）。要让球「进入盒的水平足迹再被抬上去」，就必须先允许它进入。

```
每步（在扫掠之前）：
  reference = SupportReferenceHeight(上一步支撑面, 球底)       // 判据自己的输入
  if (StepMaskNeedsRefresh(maskValid, boxesDirty, D, maskedD,
                           reference, maskedReference)) {       // 2 mm 直径 / 5 mm 参考
      重算 boxIsStep[8] = IsStepWithinLimit(盒顶, reference, stepLimit)
      若掩码有变化 → RebuildObstacles()（扫掠只含非台阶盒 + 挡路盒）
  }
  扫掠后：TryResolveSupport(..., boxIsSteppable, ...)           // 直接读掩码，不重算
```

- **一步的滞后是可接受的**：掩码用的是扫掠**前**的球底/上一步支撑。球在平地上接近台阶时 support 稳定，不会滞后；球正在爬的时候 support 在变，但那时盒**已经**是台阶了（掩码只会更保守）。这与挡路盒 20 Hz 扫描的滞后同性质（`BLOCKING_PROPS_PLAN` §3「时间误差」）。
- **一条判据**：`SupportSurfaceMath.IsStepWithinLimit(top, reference, stepLimit)` + `SupportReferenceHeight`。不允许在任何一侧另写一遍 `rise`/`band`：2026-11-15 的 C09 第一次回归就是这样出现「扫掠里是墙、支撑里不是面」的死状态（一侧带 0.02 迟滞、另一侧没有，且支撑表根本没读掩码）。
- **一个结论，两个读者**（第二次 C09 回归的修法）：静态盒的台阶答案**只算一次**（掩码），`TryResolveSupport` 对静态盒只读 `boxIsSteppable[i]`，不再比较当前 `reference`。于是「盒被剔除 ⇒ 盒可支撑」是**同一个 bit**；两次调用之间的参考漂移（出生 0.020 → 落地 0）不可能让两侧分居阈值两边。
- **门控看判据的输入**：`StepMaskNeedsRefresh` 比较的是直径与**参考高度**，不是选中支撑高度。旧门控看 `supportSurfaceY`，球从出生高度落地时支撑高度一直 0，门控不触发，掩码停在「台阶」——这是第二次回归的根因。
- **陈旧的代价有界**：门控允许参考最多漂 5 mm，所以一个「陈旧 true」的盒顶最多比当前 band 高 5 mm；球仍会被选中并抬上去，落在台阶的单步容差内。这个界是「一个结论」换来的，不能再加第二条 band 把它压回去（那正是分歧的来源）。
- **没有单向迟滞**：去噪只由上面的刷新门控（2 mm 直径 / 5 mm 参考）承担。单向闩锁只会让剔除比支撑更宽，永远不安全（§8.1 D4）。
- **斜面没有这一侧**：斜面从不进扫掠，支撑表用同一条 `IsStepWithinLimit` 的 band 判据；不存在「扫掠剔除 vs 支撑」的分歧。

### 4.4 抬升速度：台阶必须有自己的上限（与小棍的**最大冲突点**）

现状：有径向壳时，代理接地那一次求解的 `proxyLiftCap = maxProxyLiftPerStep = 0.04`（F7）。若台阶也沿用，`D = 1.10` 的球爬 0.26 m 台阶要 **7 步（0.14 s）**，这 7 步里球**穿在盒里**（球心还没抬够）——观感是「陷进去再浮出来」。

规则：

```
liftCap = isStep ? maxStepUpLiftPerStep : (shellEmpty ? maxLiftPerStep : maxProxyLiftPerStep)
maxStepUpLiftPerStep = min(stepLimit, maxStepUpLiftWorldM)      // maxStepUpLiftWorldM = 0.6 (PROVISIONAL 护栏)
```

- `isStep` = 本步选中的支撑面**不是上一帧那个面**且 `rise > 0`（即「刚跨上一级」）。**只有跨上那一步**给大上限，之后照常用 0.1/0.04。
- **为什么不 weaken 小棍的护栏**：`maxProxyLiftPerStep` 存在的理由是「一根长棍不能把球弹上天」（README 三条上限）。台阶是**面不连续**，性质不同，所以给**独立的、只作用于一次跨级的**上限是对的；跨级之后立即回到 0.04，长棍依然弹不动球。
- `maxStepUpLiftWorldM = 0.6` 是护栏：`kStep` 被误配成 1.0 时（`D = 2.2` → 2.2 m 瞬移）不至于把球送到天上。`0.6 > 0.55 = 0.25 × 2.2`，所以正常范围碰不到它。

### 4.5 防抖（台阶判定两条 + 抬升去噪一条，缺一就会在棱上抖）

**规则一句话**：一个静态盒对当前球要么是**台阶**（从扫掠剔除且可当支撑），要么是**墙**（留在扫掠里）——掩码是**唯一结论**（`IsStepWithinLimit(top, reference, stepLimit)` + `SupportReferenceHeight`），支撑表对静态盒**直接读掩码**；不存在「剔除扫掠却不能支撑」的中间态。

| # | 规则 | 防的是什么 |
|---|---|---|
| 1 | **刷新门控**：直径变化 > 2 mm、**参考高度**变化 > 5 mm 或盒集合变化才重算掩码（`StepMaskNeedsRefresh`）。比较参考而不是选中支撑高度：球从出生净空落地时支撑高度不变、参考变了，必须重算 | 球在棱上参考微抖 → 盒在墙/台阶之间每步翻转 → 扫掠数组反复重建 + 球被推一下；同时堵住「支撑高度没变但判据输入变了」的陈旧掩码 |
| 2 | **参考取球底**：`reference = max(上一步支撑面, 球心下方最低核心点)`；球站在盒顶或在其上方时 `rise ≤ 0`，自动是台阶 | 球站上盒顶后 `support` 变成盒顶；若因缩球/台阶边缘等原因 `rise` 变大，盒会回到扫掠，而球**正站在扩张盒里** → 初始穿透分支把球推开。用球底作参考后这条是**恒等式**，不是第二条比较 |
| 3 | **最小台阶**：`rise < minStepHeightM (0.005)` 时不算「跨级」（不给大抬升，不记台阶事件） | 0.004 m 的高度差被当成台阶 → 每步一次大抬升 → 抖动。数值与 `layout.json` 的 `max_route_step_m = 0.05` 不同：**0.05 是作者分类「地贴 vs 立面」的门槛，0.005 是数值去噪**，两者不要混用（混用会让 2.8 cm 餐巾变成「地贴」而跳过支撑） |

> **已删除（2026-11-15，C09 第一次回归）**：单向迟滞 `stepHysteresisM` 与「已在顶上」余量 `stepStickMarginM`。前者让「剔除扫掠」的判据比支撑判据宽 0.02 m，且支撑表从未读过掩码——0.30 m 球因此穿过 0.075 m 台阶；后者被 `reference = max(支撑面, 球底)` 完全覆盖。理由见 §8.1 D4。
> **第二次回归（2026-11-15，Unity 复测）**：删迟滞还不够。同一条判据算两遍、参考漂移仍会分居阈值两侧；修法是「掩码是唯一结论 + 支撑表读掩码 + 门控看参考」，见 §4.3 与 §8.1 D4。

### 4.6 与挡路（`SetBlockingBoxes`）的关系

一句话：**台阶规则把挡路集「吃掉」一部分，而且吃对了**。

- **矮扁件不再是隐形墙**（PICNIC §4.3 G9 的第 ② 条、C9）：2.8 cm 餐巾在 `D = 0.30` 时 `stepLimit = 0.075 > 0.028` → 台阶 → 滚过去。C9 提议的是「厚度 ≤ 0.05 m 的件不建挡路盒」——**固定阈值**，会让 0.06 m 的托盘在 0.30 m 球面前仍是墙、在 2.2 m 球面前也仍是墙（其实那时它当然应该是台阶）。本文的规则**尺寸相关**，严格更好，**建议 C9 作废、由本规则取代**（这不需要改 `PickupFieldHost` 的谓词，只改它的**分类**）。
- **高件仍然是墙**：`D = 0.30` 时保温箱 0.30 m > 0.075 → 挡路；`D = 1.20` 时 0.30 ≤ 0.30 → 台阶（**能爬上去**）。这正是原作「变大后挡路的东西就能过」的竖向版本，而且和吸收门**同源**（同一件东西，吃不动时可爬、吃得动时被吃）。
- **门件那一套不受影响**：PICNIC §3.5 的壁龛门靠「净宽 0.10 m < 球径」封死，是**水平**缝隙；台阶规则只影响**竖向**。0.22 m 的篮子对 0.30 m 球是墙（rise 0.22 > 0.075），门照样封死。

### 4.7 从台阶下来：自由落体（2026-11-15 用户决定，替换本节原文）

> **本节已按用户决定重写。** 原文假设「没有重力，球永远贴地」，并用 `maxDropPerStep = 0.1` 把下落限速成 5 m/s（0.26 m 台阶 = 3 步）。用户 2026-11-15 明确：**球有向下的重力，会从台面边缘掉下去；不做跳跃**。所以：

支撑面消失（走出足迹）→ 支撑查询回落到**另一个**面（面 id 变了）→ 支撑面高度差超过 `supportDropTriggerM = 0.01 m` → 进入下落状态：

```
fallSpeed += 9.81 * dt                         // 半隐式欧拉，初速 0
fallBudget = fallSpeed * dt                    // 本步允许的竖向位移
GroundContactMath(maxLift = fallBudget, maxDrop = fallBudget)
落到位（本步修正没被 maxDrop 钳住）→ fallSpeed = 0，恢复贴地
```

- **下落不被旧的下降钳位限速**：`maxDropPerStep = 0.1` 仍是「贴地状态下支撑面变低」的单步上限，但**下落状态不用它**——用本步的重力预算。从 0.26 m 掉下需约 0.23 s（12 步），从 1.0 m 掉下需约 0.45 s；第 1 秒末速度 9.81 m/s、单步 0.196 m，早就超过 0.1。
- **没有向上的速度**：不做跳跃，也不做「弹一下」。`fallSpeed` 只因落地清零。
- **水平运动照常**：第 2、3 步不受影响，所以「从台顶开出去」是抛物线而不是垂直落体。
- **`supportDropTriggerM = 0.01 m`** 是数值容差，不是玩法阈值：更小的落差（1 cm 以下）由 `maxDropPerStep` 在一步内吃掉，读作贴地；它同时挡住「微斜地面上每步都进出下落状态」的抖动（4 m/s、7° 地面每步降 9.8 mm）。
- **同一个连续面不算「掉走」**（P3 补）：判据是「**选中的面 id 变了** 且 高度差 > trigger」。球沿坡往下滚时，探针下方的平面高度每步都会降（30°、4 m/s 时 46 mm/步），但那是同一个面，不是失去支撑；否则每一步都会进下落状态，球会浮起来再落地。倾斜地面同理。真实边缘（坡→地面、盒→地面、道具被吸走）会换面 id，仍然触发。这一条不是第二套下落规则，是「掉走」的定义：**同一个面没有掉走**。
- 不引入 `maxStepDownSnap`：下落本身就是最自然的吸附。
- **道具槽位的例外要说清**：P2 的支撑槽 id 是「扫描内槽位」、不是跨扫描的道具身份（§8 P2 规则 2）。集合变化时同一槽位可能换成更低的道具，这时「面 id 没变」会让 `sameSurface` 抑制一次真实的重力下落，球改由 `maxDropPerStep = 0.1` 平滑下降。这是**保守**的（不会穿模、不会悬空），且不引入第二条判据；若试玩觉得这种下降太软，再说。

判据与参数见 §8.1、§10.2。

---

## 5. 斜面（斜板 / 托盘 / 木板）

### 5.1 表示

一个斜面 = **一个平面 + 一个水平足迹矩形**（§3.2 的 Ramp）。烘焙来源是场景里的 `BoxCollider`：

```
n         = 盒子的 Transform.up（归一化，且要求 n.y > 0.05）
faceCenter= 盒子中心 + n × (size.y / 2)            // 行走面 = +Y 面，不是盒中心
right     = normalize(Transform.right 的水平投影)   // 只取 XZ
down      = normalize(n 的水平分量)                 // 法线向低侧倾斜，所以不是 -n
halfWidth = max |dot(corner − faceCenter, right)|  // 4 个行走面角点
halfRun   = max |dot(corner − faceCenter, down)|   // 4 个行走面角点（水平投影！）
```

> **`center` 必须是行走面中心**：`placement.json` 的 `center` 是**盒中心**，板厚 0.04 沿 `-n` 向下延伸；如果把盒中心当平面点，球会陷进板里半个厚度（0.02 m）。所以 `TryBakeRamp` 输出 `boxCenter + n·thickness/2`，`AddRamp` 只接受这个点（`LevelArenaHost` 负责烘）。`placement_core.structure_top_plane()` 的注释也是这个约定。
>
> `halfRun` 要的是**水平**半长，所以不能直接取 `size.z/2`（斜放时水平投影变短）。实现用 4 个行走面角点在 `right` / `down` 上的投影取最大绝对值（`TryBakeRamp`），**别让作者手填**；有 roll 的板会得到保守的外接矩形。

**一个矩形平面只能表达「平板」。** 弧形板、带翻边的托盘要拆成 2–3 段矩形（容量 4 够本关用）。

### 5.2 坡度上限：由单步钳位反推，不是拍脑袋

球沿坡行走时，每步需要的**法向坐标**变化是：

```
Δn = (v · dt) · sin θ
```

被 `maxLiftPerStep`（上坡）/ `maxDropPerStep`（下坡）钳住，所以必须：

```
tan θ ≤ min(maxLift, maxDrop) / (maxSpeed · fixedStepSeconds)
       = 0.1 / (4 × 0.02) = 1.25      → θ ≤ 51.3°
```

取 **45°** 作设计护栏（30% 余量，且 45° 以上「滚上去」的读法已经不成立）：`maxSupportSlopeDegrees = 45f`（PROVISIONAL，需试玩）。

- **超过上限的斜面，`AddRamp` 直接拒绝**并给**原因码**（`RampRefusalSlopeTooSteep`）+ 实测坡度（`GetLastRampRefusalSlopeDegrees()`）+ 字面原因（`GetLastRampRefusalReason()`），**不静默登记**。因为这个面一旦登记，球能爬 58° 的"墙"；不登记球又会穿过去——两种都不对，所以**拒绝并要求作者改成墙**（`AddBox`）。实现没有用 `"slope 58 deg exceeds 45"` 这种拼字符串的消息（Udon 上少一次 float→string，也让测试能断言码）；坡度数值单独可读。
- **上限是序列化字段 `maxSupportSlopeDegrees`（默认 45）**，不是 `SupportSurfaceMath` 里的硬编码：`LevelArenaHost` 烘焙时读 `motor.GetMaxSupportSlopeDegrees()`，所以宿主与电机不可能对「多陡算陡」有分歧。
- **这个上限依赖三个旋钮**：`maxLiftPerStep`、`maxSpeedMps`、`fixedStepSeconds`。任一变了都要重算（写进代码注释，别只写在文档里）。例如 `maxSpeedMps = 6` 时上限掉到 **39.8°**。
- **与 `layout.json` 的 12% 不是一回事**：12% 是「**玩家**能开上去的可走坡」分类（`drivable_grade`，`validate_map.py` 第 13 条），约束的是地图作者与校验器；球没有牵引力模型，坡对球的约束只有上面那条**单步钳位**。**两个数服务于两个消费者**，不要互相引用（这正是 PICNIC §6 U5 那种「2.20 vs 3.20」口径打架的同型风险）。若某个斜面既想当 `drivable_grade` 又想当球的坡，它必须 ≤ 12%，那是**更严**的那个，且由校验器管。

### 5.3 两端衔接

| 位置 | 规则 | 为什么 |
|---|---|---|
| **底边** | 斜面最低点的高度必须落在「地板面 − 0.01 m」到「地板面 + `min(stepLimit, 0.05)`」之间 | 低一点没关系（球会从地板跨上 0.02 m）；高太多就是一个台阶（也算能用，但要作者知道自己在做台阶）；**穿到地板以下**会在坡底出现一个「隐形坎」（支撑查询在足迹内返回更低的面，球会先下沉再爬）。**坡底没有扫掠墙**：斜面从不登记成盒（§5.5），所以坡底附近不会出现「半径扩张 AABB」那堵墙 |
| **顶边** | 顶边高度 = 台面高（δ = 0）**或** δ ≤ stepLimit | δ = 0 时球从坡面平滑过渡到台面（两个面在探针处高度相同，选谁都不跳变）；δ > 0 时按台阶规则，**仍然能用**，但会多一次跨级 |
| **侧边** | 不加挡板。球从侧面走出足迹就按 §4.7 掉下去 | 加挡板要额外的盒，且与本关「没有扶手」的读法一致 |

> **坡底/坡顶的正常升降（已知、可接受）**：球从水平地面切到斜面时，球心目标高度从 `h + r` 变成 `h + r / n.y`（球心到平面的法向距离仍是 r，但法向不再竖直），所以在坡底那一步球心会抬 `r(1/n.y − 1)`（30°、r = 0.55 时约 7.6 cm）；坡顶切回水平面时是对称的「降同样多」。它们由 `maxLiftPerStep = 0.1` / `maxProxyLiftPerStep = 0.04` 钳住，最多两三步走完，不是传送。这是「球心探针 + 平面法向求解」的固有近似（与 R13 的上台阶观感同族），P4 若要更软可在坡底做一段虚拟倒角，**不是** P3 的第二套判据。

### 5.4 球在坡上会不会滑下去：**不会**，因为只有竖直重力、没有切向重力

> **前提已更新（2026-11-15）**：原文写「根本没有重力」。P1 起球有**竖直**重力（9.81 m/s²），但它**只在失去支撑时**作用（§4.7/§8.1）：球在坡面上时由 `GroundContactMath` 贴着坡面，支撑面不会「掉走」，所以重力不会给球任何沿坡的分量。结论没变，理由变了。

F9：`planarVelocity` 只有 XZ，`BallMotionMath` 不产出任何切向加速度。球停在 30° 坡上**就是不动的**。

这是本设计**最重要的一条"手感事实"**，也是需要用户拍板的问题之一（§11 U3）：

- **不加切向重力**（推荐，P1–P3；P3 实现成**默认 0** 的序列化字段 `rampTangentialGravityScale`，见 §10.2）：坡 = 一条变速通道（上坡慢？也不会慢，速度只由输入决定）。代价：坡没有「重量感」，陡坡和缓坡手感一样；好处：不引入新层、不改 9 步顺序、不改 `ball_motion.py` 参考模型、不影响 PVP 一致性。字段 > 0 时由 `SupportSurfaceMath.TryApplyRampTangentialGravity` 沿水平下坡方向加 `g·sinθ·scale·dt`（受 `maxSpeedMps` 钳位），motor 只接线；默认 0 时逐位等于 P1 行为。
- **加切向重力**：要在第 2 步之后按支撑面法线加一个 `g·sinθ·cosθ` 的切向分量。代价：`BallMotionMath` 的契约（「只吃意图 + 当前速度」）被打破，`Docs/tests/ball_motion/` 全部用例要重定，角色会**自己往下滑**（原作是否如此**无证据**，`unconfirmed`），且球在坡上「不推就滑」会与「推球」的手感冲突。P4 之后再议。

### 5.5 斜面与扫掠：**绝不能同时登记成盒**（作者陷阱）

斜面如果既是 `AddRamp` 又是 `AddBox`，扩张 AABB 会把球挡在坡的**下方**（球的球心在 r 高度时就撞上坡的包围盒）——症状是「坡底有一堵隐形墙」，而且因为坡是斜的，这堵墙的位置**看起来完全不合理**，很难查。

对策两条（都写进 §7.7 接线审计）：

1. `LevelArenaHost` 的**字段分开**：`rampBoxes` 与 `staticBoxes` 是两个数组，`Setup` 时各走各的注册，**不做类型推断**。
2. `Setup` 里做一次自检：若某个 `rampBoxes[i]` 也出现在 `staticBoxes[]` 里（同一 `BoxCollider` 引用），**打 `Debug.LogError` 并跳过它的 box 注册**（保留 ramp）。这条检查是**O(n·m) ≤ 8×4**，一次性的。

---

## 6. 可吸结构的三种身份

### 6.1 一张表说清

| 吸收门（`IsBeyondAbsorbVolumeLimit`） | 几何（`rise ≤ stepLimit`） | 身份 | 注册到 | 玩家看到 |
|---|---|---|---|---|
| **吸不动**（true） | true | **地形 / 通路** | `SetSupportProps`（顶面）+ 从扫掠剔除 | 滚上去、站上去 |
| **吸不动**（true） | false | **挡路** | `SetBlockingBoxes` | 挡住 |
| **吸得动**（false） | —（不参与） | **食物** | 都不注册 | 被吸走 |

**只有两个判据，各只有一处**：吸收门在 `PickupFieldHost.UpdateBlockingProps` 里已经有（`PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(propBounds[i], limit)`，`limit = motor.GetAbsorbVolumeLimitWorldM3()`）；几何判据是本文新增的**一条** `rise ≤ stepLimit`。**不许**再加第三个（例如「薄件不挡」「矮件不挡」「门件特判」）——那正是 `OPEN_DECISIONS` 反复警告的第二阈值。

### 6.2 为什么台阶规则不是「第二个阈值」

吸收门回答的是「**能不能吃**」；台阶规则回答的是「**能不能爬**」。两者是不同问题，而且**必须**不同——否则 2.8 cm 餐巾就只能选「永远挡」或「永远不挡」，而正确答案是「小球挡、大球爬、再大被吃」，三段都由各自的判据决定。写清这一点，避免下一个人把它当成重复阈值删掉。

### 6.3 翻转时机（20 Hz 扫描，原子翻转）

```
PickupFieldHost.UpdatePropObstacles（每 scanIntervalSeconds = 0.05 s，先于收集门）
  ConsumeAbsorbedEvents()
  UpdatePropObstacles():
     limit  = motor.GetAbsorbVolumeLimitWorldM3()
     supportY = motor.GetSupportSurfaceHeightWorldM()     // P1 getter
     stepLimit = motor.GetStepHeightLimitWorldM()          // P1 getter（kStep × D）
     ballBottom = motor.GetCenterWorld().y − motor.GetCollisionRadiusWorldM()
     对每件 Free 且可用的道具：
         建盒一次（propBounds → min/max）
         beyond = IsBeyondAbsorbVolumeLimit(bounds, limit)  // 与吸收门同一个谓词
         alreadyAbove = ballBottom ≥ max.y
         role = SupportSurfaceMath.TryClassifyProp(beyond, max.y, supportY, stepLimit, alreadyAbove)
         role == None      → 两个掩码都 false（食物）
         role == Support   → supportCandidate = true
         role == Blocking  → blockingCandidate = true
     两次 SelectNearest（同一个纯函数，不同 candidate 掩码与粘性）
       → motor.SetSupportProps / motor.SetBlockingBoxes
```

- **同一扫描里原子翻转**：一件道具不会「既不在挡路集、又不在支撑集」（那会有 ≤0.05 s 的「门漏了」窗口）。这是 §4.3「一次刷新两件事」在宿主侧的对应。
- **两个容量各 6**，`SelectNearest` 跑两遍（同一函数、不同掩码）。**不要**用「先选最近 6 件再分类」：一个近处的台阶件会占掉挡路槽位，导致更远的真墙没登记 → 球穿墙（这是把两个角色塞进一个容量池的必然结果）。
- **粘性**：挡路用 `blockingStickinessM = 0.25`（现状）；支撑用**独立**的 `supportStickinessM = 0.10`（更小：支撑留旧件会让球「悬在已经走开的面」上，比挡路多留一会更难受）。
- **失败语义**：选择被拒 → **保留上一批**（与挡路一致，`BLOCKING_PROPS_PLAN` §5.3）。

### 6.4 「球站在要被吸的东西上」

时序（`D` 刚跨过某件的门槛）：

| 步 | 发生什么 |
|---|---|
| N | 球站在桌面（支撑 = 桌顶）。吸收门放开（`limit` 变大）→ 宿主下一次扫描把桌子从**挡路**移到**食物**；同时桌子**还在**支撑集里（扫描是原子的，但 `limit` 读的是电机当步的值） |
| N+1 | 门通过 reach（球站在桌上时 `距离 ≈ r + h/2 ≤ r + 半径`，**够得着**）→ 提交 → 账本确认 → 桌子 `StateFree → Attached` |
| N+1 … N+k | 宿主下一次扫描（≤0.05 s）把桌子从支撑集移除 → 支撑回落到地面 → P1 的竖直重力生效（`fallSpeed += 9.81·dt`；不是旧的 `maxDropPerStep = 0.1` 钳位） |
| 视觉 | `discontinuous` **不置位**（这不是传送）；球「往下掉」但不额外滚（垂直位移对滚动没贡献）。0.72 m 的桌面按自由落体约 0.38 s 落地；镜头再把这条曲线过一遍 5 Hz 低通 —— 读作「顿一下」 |

**不做特例**（不禁止「吸你正站着的东西」）。理由：① 加禁则要一个新的判据（而且是玩法判据，不是几何判据），与「一个阈值」原则冲突；② 「桌子塌了，你掉下来」是**正确**的读法，也是用户原则里「后期台球桌都能粘起来」的必然结果；③ 真要避免，最省的位置是**宿主不提交「正被支撑的那件」**（一行 `if (i == motor.GetSupportPropIndex()) continue;`），代价是「站在桌上永远吃不掉桌子」——这作为 §11 U2 的备选列出。

### 6.5 「同一件东西先当路、后当食物」怎么兑现

| 阶段 | 木板（prop，0.80 × 0.20 × 0.20，`D_req = 0.849`） | 台球桌/野餐桌（prop，`D_req = 2.49`） |
|---|---|---|
| `D < 0.849` | 吸不动 + rise 0.20 > stepLimit（`D < 0.80` 时 `stepLimit < 0.20`）→ **挡路** | 吸不动 + rise 0.72 → 墙 |
| `0.849 ≤ D` | 被吸 → **食物** | 吸不动 + rise 0.72 ≤ stepLimit（需 `D ≥ 2.88`，**超出 2.20 上限**）→ **永远是墙** |
| 结论 | 木板能走完「挡路 → 食物」，但**走不到「地形」**（它 0.20 m 高，`D` 到 0.80 就被吃了） | 2.20 m 上限下**永远上不去**（这正是 PICNIC §3.5.2 算过的 `D_req = 2.492 > 2.20`） |

**所以用户原则里的「斜板」不能靠 prop 的 AABB 顶面实现**，必须靠 §5 的作者声明斜面。这是本设计一个**必须说清的限制**：

- 一件**倾斜**的道具，我们只知道它的 `propBounds`（AABB）和 Transform 的旋转，**AABB 抹掉了朝向** → 它只能被当成一个**水平搁板**（顶面 = AABB 顶）。
- 要让它真的当坡，道具行必须携带**带朝向的盒**（`COLLIDER_LAYER.md` §5 的 manifest `collision.boxes`，K ≤ 6、有序）。**那条线落地后**，`PropTop` 就能升级成 `PropRamp`（同一个 Ramp 结构，来源换成道具行），届时「木板先当坡、后被吃」才真正兑现。**在那之前，斜板只能由 `LevelArenaHost` 声明成静态斜面**——它不可吸，所以只能演「通路」这一种身份。
- 这不是设计缺陷，是**数据缺口**，写在 §11 U4 与 §8 P4 的「依赖」里。

---

## 7. `LevelArenaHost` 接口

### 7.1 为什么不是 Lab 脚本

现状只有 `Labs/StoryLevel/StoryLevelLabBootstrap`：它把「地面 + 4 面墙 + 允许的 kind + 自测驱动 + 每秒日志 + 相机跟随」全塞在一个 Lab 组件里（`Setup` 里 `AddPlane` + 遍历 `obstacleBoxes` 调 `AddBox`）。正式场景不能挂叫 Lab 的脚本（`HUB_LOOP_PLAN` §7 配套 1；PICNIC §5.2 C1/C2 已列为实现者改动）。`LevelArenaHost` 取代它的**注册段**，其余（自测驱动、日志、相机）留在 Lab 或删掉。

### 7.2 序列化字段（草案）

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class LevelArenaHost : UdonSharpBehaviour
{
    [Header("Links")]
    [SerializeField] private KatamariBallMotor motor;

    [Header("Ground")]
    [SerializeField] private Vector3 groundNormal = new Vector3(0f, 1f, 0f);
    [SerializeField] private float groundOffset = 0f;

    [Header("Boundary planes (walls for the ball; never supports)")]
    [SerializeField] private Vector3[] planeNormals;      // free space: dot(n,p) >= offset
    [SerializeField] private float[]   planeOffsets;

    [Header("Static boxes (sweep obstacle AND a support top)")]
    [SerializeField] private BoxCollider[] staticBoxes;

    [Header("Ramps (support only, NEVER a sweep box)")]
    [SerializeField] private BoxCollider[] rampBoxes;

    [Header("Optional")]
    [SerializeField] private string[] allowedKinds;       // null/empty = motor refuses every candidate
    [SerializeField] private bool autoBeginPlaying = true;
    [SerializeField] private bool logSetupLine = true;
}
```

**为什么 `BoxCollider[]` 而不是 `Vector3[] min/max`**：作者用真碰撞体摆几何（对齐视觉、可用于玩家走路），`Setup` 烘焙成 min/max，**与 B 案唯一能立刻兑现的好处相同，但不需要 PhysX 查询**（§0.5）。缺点：45° 摆的盒会退化成外包 AABB（PICNIC §4.3 G6，已知）；本关角内没有 45° 件。

**为什么斜面也用 `BoxCollider`**：同一个对象既能给玩家撞、又能烘焙出朝向（`Transform.up/right`）；**但它的 Collider 只给玩家用**，绝不能再进 `staticBoxes`（§5.5）。P3 的 `LevelArenaHost.RegisterRamp` 读 `box.center`/`box.size`/`transform.lossyScale` 与 `rotation`，把盒中心换算成行走面中心后交给 `TryBakeRamp`；`rampBoxes` 与 `staticBoxes` 是两个独立数组，导入器只把 `role=ramp` 写进前者。

### 7.3 公开方法（草案）

```csharp
// 生命周期
public void Update();                    // 等 GetSemanticDiameterWorldM() > 0 后 Setup() 一次
public bool IsReady();
public void Reapply();                   // 运维/调参用：清表后重注册（不删静态几何语义）

// 诊断（HUD / 验收脚本读这些）
public int    GetRegisteredPlaneCount();
public int    GetRegisteredBoxCount();
public int    GetRegisteredRampCount();
public int    GetRegisteredSupportCount();
public string GetSetupReport();          // "[arena] planes=5 boxes=4 ramps=1 supports=9"
```

`Setup()` 的注册顺序**不可交换**（后注册的容量不够时要能报出是哪一条）：

```
1. motor.SetGroundPlane(groundNormal, groundOffset)   // 新 API（PICNIC §4.3 G2）
2. for planes:  motor.AddPlane(n, o)                  // 墙；不参与支撑
3. for ramps:   motor.AddRamp(center, n, right, halfWidth, halfRun)   // 只支撑
4. for boxes:   motor.AddBox(min, max)                // 墙 + 顶面支撑（两用）
   4.0 自检：同一 BoxCollider 若也在 rampBoxes 里 → LogError + 跳过 box（保留 ramp，§5.5）
5. motor.SetAllowedKinds(allowedKinds, count)
6. if (autoBeginPlaying) motor.TryBeginPlaying(Time.time)
7. log one line: "[arena] planes=.. boxes=.. ramps=.. supports=.. refused=.."
   + （有斜面被拒时）" rampRefusal=<collider>: <reason>"
```

**容量失败必须可见**：`AddPlane`/`AddBox` 已返回 `bool` 并且超容量会 `SignalOverflow`（latch）。`AddRamp` 同样返回 `bool` + 原因码；超容量（第 5 条）也 `SignalOverflow`（场景接线错误），几何/坡度拒绝只计数并带原因（不是场景容量错误）。`Setup` 在最后一行日志里带 `refused=…`，并且**只在 setup 时**打 `Debug.LogError`（不要每帧）。

### 7.4 与 `PickupFieldHost` 的分工（谁登记什么）

| 几何 | 谁拥有数据 | 谁调 motor | 为什么 |
|---|---|---|---|
| 地面平面 | `LevelArenaHost` | `SetGroundPlane` | 关卡属性 |
| 边界平面（墙） | `LevelArenaHost` | `AddPlane` | 关卡属性 |
| 静态盒（布景/台面/台阶） | `LevelArenaHost` | `AddBox`（两用） | 场景几何，注册一次永不删 |
| 斜面（静态斜板） | `LevelArenaHost` | `AddRamp` | 场景几何 |
| **可吸结构（吸不动时）的顶面** | **`PickupFieldHost`** | **`SetSupportProps`** | 它已经拥有全部道具行、`propBounds`、状态机、和挡路集；分类必须与挡路**同一遍扫描**（§6.3），否则两个集合会分叉 |
| 可吸结构的挡路盒 | `PickupFieldHost` | `SetBlockingBoxes` | 现状，不动 |

**一条硬规则**：`LevelArenaHost` **不得**引用任何道具、不得读 `PickupFieldHost`、不得按名字找道具。它只认识场景几何。反过来 `PickupFieldHost` 不引用 `LevelArenaHost`，只调电机的公开 setter。这样「谁登记可吸结构的支撑」只有一个答案。

### 7.5 电机侧新增接口（一次列全，避免下一个人再造第二个）

```csharp
// 地面（补 G2）
public bool SetGroundPlane(Vector3 normal, float offset);

// 斜面（只支撑，不进扫掠）
public bool AddRamp(Vector3 center, Vector3 normal, Vector3 right,
                    float halfWidth, float halfRun);
public void ClearRamps();
public int  GetRampCount();
public string GetLastRampRefusalReason();     // "" = 从未拒绝（"slope 58 deg exceeds 45" 等字面量）

// 可吸结构的两面
public bool SetSupportProps(Vector3[] minima, Vector3[] maxima, int count);   // 整批替换
public void ClearSupportProps();

// 台阶
public float GetStepHeightLimitWorldM();      // kStep × 语义直径（宿主分类要用）
public bool  SetStepHeightOverBallDiameter(float k);   // 校验 k∈[0,1]，否则拒绝

// 支撑诊断（宿主 + HUD + 验收）
public int     GetSupportSurfaceCount();      // 激活面数（诊断）
public float   GetSupportSurfaceHeightWorldM();
public Vector3 GetSupportNormalWorld();
public bool    IsSupportingAProp();           // 站在可吸结构上（§11 U2 的备选要用）
```

> **P1 实际实现（2026-11-15）**：`SetGroundPlane`、`SetTerrainSupportEnabled(bool)` / `IsTerrainSupportEnabled()`、`GetStepHeightLimitWorldM`、`GetSupportSurfaceHeightWorldM`、`GetSupportNormalWorld`、`GetSupportSurfaceCount`、`GetSupportSurfaceId`（诊断：0 = 地面，1+i = 第 i 个静态盒，**P2 起 100+i = 第 i 个支撑槽位（宿主按道具下标排序，见 §8 P2 规则 2）**）、`GetSupportKind`（0 地面 / 1 盒 / **3 道具** / -1 未解出；2 斜面在 P3 已启用）、`GetStepMaskRebuildCount`（掩码翻转导致的扫掠数组重建次数，物理测试场的 P4 成本判据）、`IsFalling`。`LevelArenaHost` 侧有 `Reapply()`（清表后重注册，地图 0 的一格一注册靠它）。
> **P2 实际实现（2026-11-15）**：`SetSupportProps(Vector3[] minima, Vector3[] maxima, int count)` / `ClearSupportProps()`、`GetSupportPropCount()` / `GetSupportPropCapacity()`（= 6）/ `GetSupportPropRefusalCount()` / `GetSupportPropRefusalReason()`、`IsSupportingAProp()`。**未实现**：`SetStepHeightOverBallDiameter`（P1 用序列化字段 `stepHeightOverBallDiameter`；宿主不需要在运行期改它）。`GetSupportSurfaceCount` 报的是**本步 band 内的面数**（足迹未应用，P2 起含道具顶面），不是注册数；静态注册数由 `LevelArenaHost.GetRegisteredSupportCount()` 报，道具注册数由 `GetSupportPropCount()` 报。
>
> **P3 实际实现（2026-11-15）**：`AddRamp(Vector3 center, Vector3 normal, Vector3 right, float halfWidth, float halfRun)` / `ClearRamps()` / `GetRampCount()` / `GetRampCapacity()`（= 4）/ `GetRampRefusalCount()` / `GetLastRampRefusalCode()` / `GetLastRampRefusalReason()` / `GetLastRampRefusalSlopeDegrees()` / `GetMaxSupportSlopeDegrees()`。面 id：地面 0、静态盒 `1+i`、**斜面 `10+i`（10..13）**、道具 `100+i`；`GetSupportKind()` 的 **2 = 斜面** 已生效。`AddRamp` 与 `AddBox` 是**两个**注册入口，motor 内部斜面用独立并行数组（`rampCenter/Normal/Right/HalfWidth/HalfRun[4]`），**不写扫掠**、不触碰台阶掩码。`TryResolveSupport` 多接收斜面 5 数组 + `rampCount`，并多接收 `bool[] boxIsSteppable`（静态盒候选 = 掩码）。`SupportSurfaceMath` 另有 `TryBakeRamp`、`TryValidateRamp`、`TryRampSlopeDegrees`、`IsRampCountAllowed`、`IsRampSurfaceId`、`RampSurfaceIndex`、`StepMaskNeedsRefresh`、`TryApplyRampTangentialGravity`。`ClearObstacles()` 连斜面表一起清（`Reapply` 重新注册）。

`SetSupportProps` 的语义与 `SetBlockingBoxes` **完全对齐**（整批替换、逐盒先校验后写、同一批重复写入不重建、失败保留上一批、超容量计数不 latch）——**直接复用同一套校验代码路径**，不要写第二份（这是本仓库已经付过两次代价的「一份逻辑一个实现」）。

### 7.6 迁移：从 `StoryLevelLabBootstrap` 到 `LevelArenaHost`

| # | 做什么 | 验收 |
|---|---|---|
| M1 | 新建 `Runtime/Story/LevelArenaHost.cs`（UdonSharp，`BehaviourSyncMode.None`）+ `.meta` | U# 全量编译 0 错误 |
| M2 | 场景里把 `StoryLevelLabBootstrap` 的 `groundNormal/groundOffset/obstacleBoxes` 三组值搬进 `LevelArenaHost`；**把四面 `Wall_*` 从 `obstacleBoxes` 移除**（球改用平面，PICNIC §5.1 S3） | 日志 `[arena] planes=5 boxes=4 ramps=0 supports=9`；球顶到墙时球心到墙面 = 碰撞半径 |
| M3 | `StoryLevelLabBootstrap` 只留自测驱动/日志（或整体删除，PICNIC C2 的原话：文件名与类名一起改，`.cs`/`.cs.meta`/`.asset`/`.asset.meta` 同搬，GUID 不变） | 正式场景无 Missing Script；Console 无 `outdated behaviour version` |
| M4 | 花圃台/树根丘/角树/指示牌进 `staticBoxes`；**不要**把花圃台再单独 `AddBox` 一次 | `[arena] boxes=4`；`GetSupportSurfaceHeightWorldM()` 在台顶读 0.26 |
| M5 | 打开 `stepUpEnabled`（P1 的开关，见 8.1），`kStep = 0.25` | 0.30 m 球被花圃挡住；1.10 m 球能上（§8 验收 S2） |

### 7.7 接线审计清单（作者必须遵守的六条）

1. **同一个 `BoxCollider` 不能同时出现在 `staticBoxes` 与 `rampBoxes`**（`Setup` 会报错并跳过 box）。
2. **斜面底边不要穿到地板以下**，顶边齐平台面（§5.3）。
3. **边界墙用平面，不要用盒**（盒会用掉 8 个支撑槽，而且墙的顶面变成可站的"窄条"，观感荒诞）。
4. **`allowedKinds` 为空 = 全部拒绝**（电机契约）；忘了填会表现为「什么都吸不到」。
5. **一个电机只有一个挡路写入者**（`BLOCKING_PROPS_PLAN` R5）；`LevelArenaHost` 不许碰 `SetBlockingBoxes`。
6. **台阶/支撑相关的坑位检查**：`stepUpEnabled` 打开后，旧 lab（`BallPlayground`/`CpuSkirmish`）的行为会变——它们各自的 bootstrap 要显式 `motor.SetTerrainSupportEnabled(false)`，或者接受变化（§8.1）。`Labs/BallPlayground` 已于 2026-11-15 退役（由 `Labs/PhysicsTestField` 取代），所以这条现在只剩 `CpuSkirmish` 还适用。

---

## 8. 分阶段实施

> 顺序与任务书不同的一处：**把「可吸结构顶面」提到「斜面」之前**（任务书是 静态→斜面→可吸结构）。理由是 P2 与 P1 **共用同一张支撑表和同一条 `rise` 判据**，增量只是宿主分类（约 40 行 + 两个 setter），而它立刻消灭「矮扁件隐形墙」；斜面要引入**新的数学**（平面足迹 + 坡度护栏 + 烘焙），风险更高，放在已经能跑的地形上做更安全。如果用户更想先看到「沿木板上桌」，把 P2 与 P3 对调即可，**两者互不依赖**。

### P1 — 静态盒顶支撑 + step-up + `LevelArenaHost`

**范围**：地面平面 + 静态盒顶面 + 台阶规则（含三条防抖）+ **竖直重力下落** + `LevelArenaHost` 取代 Lab 注册段。**不做**：斜面、道具、探针细化、跳跃。

> **实现状态（2026-11-15）**：已实现并入库（源码 + NUnit + Python 模型）。文件：新 `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs`、改 `Runtime/BallMotor/KatamariBallMotor.cs`、新 `Runtime/Story/LevelArenaHost.cs`、改 `Labs/StoryLevel/StoryLevelLabBootstrap.cs`（只留自测驱动/镜头/日志，注册交出去）、新 `Docs/Design/BallLayers/TerrainSupport/terrain_support.py`、新 `Docs/tests/ball_terrain_support/`、新 `Tests/EditMode/BallLayers/SupportSurfaceTests.cs`。**未做**：`TryBakeRamp`（P3，理由见 §8.1）、道具支撑（P2）、新 `.cs` 的 `.meta`（本仓规矩：新 `.cs` 不造 meta）、Unity 编译 / `run_tests` / Play Mode（本侧不是 Unity 操作者）。

| 文件 | 改什么 |
|---|---|
| 新 `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs` | **唯一**的地形数学：`TryResolveSupport(probe, referenceSupportY, stepLimit, 地面, 盒表, 斜面表, 道具表, out surfaceId, out point, out normal, out height)`；`IsStepWithinLimit(top, reference, stepLimit)`（两侧唯一的台阶判据）与 `SupportReferenceHeight(supportHeight, ballBottom)`；`TryBakeRamp(center, normal, right, size, out …)`（P1 时只留签名，P3 已落地，见 §8）。纯静态、无分配、UdonSharp 安全 |
| `Runtime/BallMotor/KatamariBallMotor.cs` | ① `AddBox` 记录顶面（支撑槽复用同一缓冲）；② `RebuildObstacles` 增加「剔除台阶盒」的掩码；③ `StepBall` 在扫掠前刷新掩码、扫掠后选面、把 `(supportNormal, supportOffset)` 喂 `GroundContactMath`、把 `supportNormal` 喂 `VisualRollMath`；④ 新字段 `stepHeightOverBallDiameter = 0.25f`、`terrainSupportEnabled = false`、`maxStepUpLiftWorldM = 0.6f`、`minStepHeightM = 0.005f`（`stepHysteresisM`/`stepStickMarginM` 已于 2026-11-15 删除，见 §8.1 D4）；⑤ 新 getter（§7.5 里 P1 需要的四个） |
| 新 `Runtime/Story/LevelArenaHost.cs`（+ `.meta`） | §7.2/§7.3 |
| `Labs/StoryLevel/StoryLevelLabBootstrap.cs` | 删注册段（或整体并入 M3） |
| 新 `Docs/Design/BallLayers/TerrainSupport/terrain_support.py` | Python 参考模型，与 C# 同名同序（本仓库每层都有的镜像；**没有它，C# 就没有离线对照**） |
| 新 `Docs/tests/ball_terrain_support/test_terrain_support.py` | 与下面 NUnit 同一组用例（目录名按现有 `ball_*` 惯例） |
| 新 `Tests/EditMode/BallLayers/SupportSurfaceTests.cs`（+ `.meta`） | 见下 |

**NUnit（纯数学，`run_tests` 必须全绿）**

1. `GroundOnlyReturnsTheGroundPlane` — 空表时选地面，`height == groundOffset`。
2. `BoxTopWinsInsideItsFootprint` — 探针在足迹内 → 盒顶；高度 = `max.y`。
3. `ProbeOnTheFootprintEdgeCountsAsInside` — 含等号（写死契约，防以后改边界）。
4. `ProbeOutsideTheFootprintFallsBackToGround`。
5. `HighestSurfaceWinsWhenTwoFootprintsOverlap`。
6. `SurfacesAboveTheStepBandAreNotCandidates` — `height > supportY + stepLimit` 的面被排除（这就是「墙的顶面不能被当地板」）。
7. `StepLimitScalesWithDiameter` — 0.30/0.83/1.10/2.20 四个直径的 `stepLimit`。
8. `TheBoxThatIsWithinTheLimitLeavesTheSweep` — 掩码：0.26 m 盒在 `D = 1.10` 时被剔除、在 `D = 0.30` 时保留。
   19/20. `TheQuantizedDiameterPutsTheBoundaryDeckAboveTheLimit` / `TheSweepMaskAndTheSupportTableNeverDisagree` — C09 的 0.075 > 0.074934 必须留作墙，且任何 top/reference 下「剔除」与「可支撑」同答案（2026-11-15 回归）。
9. `HysteresisKeepsASteppableBoxOutUntilItExceedsTheLimitPlusTheMargin`。
10. `ABallAlreadyAboveTheTopIsNeverPushedOut` — 规则 2。
11. `NonFiniteInputIsRefusedAndNothingIsWritten`。
12. `SurfaceSelectionIsStableAcrossTwoIdenticalCalls`（确定性）。
13. `CapacityRefusesTheNinthBoxWithoutTouchingTheFirst` — 现有 `AddBox` 语义不许回退。
14. `NoSurfaceIsEverReportedWithoutTheGround`（永远有兜底）。

> **落地差异**：文件里是 **18 例**。第 13 条在当前架构下只能钉「查询只读 `boxCount` 条、第 9 槽永不被读」——注册容量仍是电机 `AddBox` 的既有语义，而 EditMode 不能实例化装配层（`README.md` 验收一节已说明）。另加 3 条重力用例，名字就是用户要求的两件事加落地时补的一条：`WalkingOffTheLedgeStartsAFall`、`TheFallFollowsGravityAndLandsOnTheLowerSurface`、`TheFallIsNotLimitedByTheDropPerStepClamp`（`SupportSurfaceMath` 的 `SupportDroppedAway`/`ResolveFallState`/`TryIntegrateFall`），再加 1 条 `StepCrossingIgnoresNumericalNoise`（钉 `minStepHeightM` 的去噪语义，与地图的 0.05 m 分类门不是一回事）。C# 与 `Docs/tests/ball_terrain_support/` 同名同序（18/18）。

**Play Mode 验收（`StoryMode` 场景，操作员执行）**

| # | 步骤 | 通过判据 |
|---|---|---|
| S1 | `D = 0.30` 直冲花圃台 | 被挡住；`rej=` 一行仍是 `TOO_LARGE`；日志 `steppable=0 support=ground` |
| S2 | 长到 `D ≥ 1.04` 再冲 | 上去了；静止后 `center.y − 0.26 − 碰撞半径 = 0.000`（±0.005）；`support=box y=0.26` |
| S3 | 在台顶沿边来回开 10 s | `center.y` 波动 < 1 mm（无抖动）；`support` 不来回翻转 |
| S4 | 从台顶开出去 | 落到地面；**没有**被推一下；`support=ground` 在 3 步内 |
| S5 | 站在台顶不推摇杆 5 s | 球不动、不下沉（`bottom` 恒 0.26） |
| S6 | 容量：临时加第 65 个盒 | `AddBox` 返回 false、日志报 `box capacity 64 exceeded`；前 64 个照旧；`overflowCount == 1` |
| S7 | 旧 lab 回归 | `CpuSkirmish` 在 `terrainSupportEnabled = false` 下**帧行为与今天一致**（原并联的 `BallPlayground` 已于 2026-11-15 退役、场景已删，无法再回归） |

### 8.1 P1 实现记录：与本文的偏离、开关、成本（实现后补）

#### 三处偏离（每条都有理由）

| # | 本文原写法 | 实现 | 为什么 |
|---|---|---|---|
| D1 | §5.4「没有重力、球永远贴地」；§4.7 用 `maxDropPerStep = 0.1` 限速下落 | **球有竖直重力**：失去支撑时 `fallSpeed += 9.81·dt`，`maxDrop`/`maxLift` 都换成该步的重力预算 `fallSpeed·dt`；落地即清零；不做跳跃、不做向上速度 | 用户 2026-11-15 拍板：「球有向下的重力，会从台面边缘掉下去；不做跳跃」。旧的 0.1/步钳位会把 1 m 的掉落拉成 0.5 s 的缓降，观感是漂浮 |
| D2 | §3.3 的 band 参考 = `supportSurfaceY`（上一步选中的支撑面高） | band 参考 = `max(上一步支撑面高, 球心下方最低核心点)` | 有重力之后「下落中」的球会经过比「上一步支撑面 + stepLimit」高、但比球自己低的面（例：从高台掉向一张台面）。按原式这些面被排除，而它们在 `alreadyAbove` 下又已从扫掠里剔除 → 球**穿过**台面。取球自己的最低点作参考后，可落的面永远在 band 内；贴地时最低点 = 支撑面，band 与原文完全一致 |
| D3 | §8 文件表要求先写 `TryBakeRamp` 签名 | **不写**，推迟到 P3 | P1 没有斜面；一个没有调用方、没有测试的烘焙函数就是「平行草稿」，本仓 `AGENTS.md` 明令不要 |
| D4 | §4.5 三条防抖里的「单向迟滞」（`stepHysteresisM`）与「已在顶上余量」（`stepStickMarginM`）；§3.3 的 band 由支撑表单独判 | **删除两个字段与闩锁**；两侧共用 `IsStepWithinLimit(top, reference, limit)` + `SupportReferenceHeight(support, ballBottom)`；「已在顶上」由 `reference` 取球底自动成立 | 2026-11-15 地图 0 的 C09 实测：0.30 m 球（D 量化 0.2997369、limit 0.074934）**穿过** 0.075 m 盒。球在出生点离地 0.02 m 时掩码按「球底参考」把盒判成台阶（rise 0.055），落地后支撑表（`top ≤ 0 + 0.074934`）不认它，而单向迟滞让掩码停在台阶——「剔除扫掠却不能支撑」。迟滞只要存在就只能让剔除更宽，无法与支撑表同步；去噪由 §3.5 的 5 mm 门控承担（对称），所以删掉是唯一安全解 |
| D5 | D4 之后仍把同一条判据在两侧各算一次：掩码（扫掠前，球底参考）与支撑表（扫掠后，落地参考）；门控看的是 `supportSurfaceY` | **一个结论，两个读者**：`TryResolveSupport` 接收 `bool[] boxIsSteppable`，静态盒候选 = 掩码，不再重算；门控改为 `StepMaskNeedsRefresh(...)`，输入是**参考高度与直径**（判据自己的输入）。斜面/道具无扫掠侧，继续用同一条 band | 2026-11-15 Unity 复测：a9aedf6 后 C09@0.30 仍穿模。出生时球底 0.020，掩码判 0.075 盒为台阶并剔除扫掠；落地后球底 0、支撑高度一直 0，旧门控不触发，掩码陈旧（`maskValid=True steppable0=True`），支撑表用新参考拒收 → 又分居阈值两侧。按构造消除分歧，比继续调门控可靠 |

#### 开关（`terrainSupportEnabled`，默认 **true**）

用户 2026-11-15 决定：默认 **true**（本文 §10.2 原写 false）。理由：正式关卡的地面/台阶/台面本来就是静态盒，「关着」等于特性不存在；旧的 lab 是少数，让它们显式关。

**关掉时与 P1 之前逐位一致**，靠的是四条「每个下游输入都不变」：

1. `RefreshStepMask` 不调用 → `boxIsSteppable` 全 false → `RebuildObstacles` 按注册顺序复制**全部**静态盒（`boxMinima/boxMaxima` 与旧版同长同序）；
2. 喂 `GroundContactMath` 的还是原始 `(groundPlaneNormal, groundPlaneOffset)`，不是支撑面的 `(n, dot(n,P))`；
3. 升降上限还是 `maxLiftPerStep` / `maxProxyLiftPerStep` / `maxDropPerStep`，`isFalling` 恒 false，`ResolveFallState` 不会被调用；
4. `VisualRollMath` 拿到的是原始 `groundPlaneNormal`（当开关关闭时 `supportNormal = groundPlaneNormal`，原样传下去）。

`SetTerrainSupportEnabled(false)` 还会清掩码并重建扫掠数组、清下落状态；`AddBox`/`ClearObstacles`/`TeleportTo` 都会让掩码失效，所以运行期切换也安全。

#### 每步新增成本（Udon 解释器，常数，与已吸物件数无关）

| 阶段 | 成本 | 频次 |
|---|---|---|
| `RefreshStepMask` | ≤64 盒 ×（1 减法、1 比较、1 次 `IsStepWithinLimit` ≈ 6 flops）；**门控**：尺寸变化 > 2 mm 或支撑面变化 > 5 mm 或盒集合变化才重算（调 `IsStepWithinLimit` 一次） | 每步一次门控（≈64 次比较，未过门时零成本），重算低频 |
| `RebuildObstacles`（掩码变化时） | 现有实现本来就在条数变化时 `new Vector3[]`；掩码变化是低频（球长大/爬到新高度） | 低频，P4 实测 GC |
| `ResolveTerrainSupport` | `TryResolveSupport`：1 次归一化 + 探针/盒校验（≤8×2 向量有限性）+ ≤8 次足迹/高度比较，约 120 flops；无分配 | 每步 |
| 重力 | `TryIntegrateFall` + `ResolveFallState`：约 10 flops | 每步（仅下落时改上限） |
| 诊断 getter | 0（只读字段） | 读时 |

对比现有扫掠（1–3 平面 + ≤14 盒 × 4 次投影 × ≤4 接触），支撑查询仍是一个零头；真正的风险是方法调用而不是 flops，所以选面是**一个静态函数吃数组**。

### P2 — 可吸结构：挡路 / 支撑拆分

**范围**：`PickupFieldHost` 把挡路扫描扩成「一遍分类、两次选择」，新增 `SetSupportProps`；不碰斜面。

**P2 定型规则（2026-11-15，实现前补写；原文只有轮廓，这一节把接口与判据钉死）**

1. **分类只有一个实现**：`SupportSurfaceMath.TryClassifyProp(beyondAbsorbVolumeLimit, topY, supportHeightM, stepLimitM, alreadyAbove, out role)`。顺序就是判据的顺序：
   - 吸收门先答。`beyondAbsorbVolumeLimit == false`（吸得动）→ 角色 `None`（0）：两个集合都不进，它就是食物。
   - 吸不动 → `rise = topY − supportHeightM`；`rise ≤ stepLimitM` → `Support`（1）；`rise > stepLimitM` → `Blocking`（2）。**等号是 Support**（有测试钉住；PICNIC 的「躺椅 0.35 → 折叠桌 0.70，D = 1.40」正好落在等号上）。
   - `alreadyAbove`（球底 ≥ 道具顶）→ Support，无条件。这是静态盒 §4.5 规则 2 的同一条反抖规则：一个正在落到道具顶上的球，不能把着陆面当墙。
   - 非法输入（非有限、负 stepLimit）→ 返回 false 且 role = None；宿主跳过该行。
   - **`beyondAbsorbVolumeLimit` 由宿主传入**，函数自己不读道具：宿主仍然只调 `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit`，所以「挡路/支撑/食物」三个身份共享同一个吸收谓词，没有第二个阈值。
2. **支撑面身份**：`surfaceId = 100 + 支撑槽位`（`SupportSurfaceMath.FirstPropSurfaceId = 100`）；宿主在注册前把选中的行按**道具下标升序**排好，所以一件道具只要还在支撑集里，它的槽位（因而 id）就稳定；集合成员变化时槽位可以移动。静态盒仍是 `1 + i`，地面 0。`GetSupportKind()`：0 地面 / 1 静态盒 / **3 道具** / -1 未解出（P3 起再加 **2 = 斜面**，id 10…13）。`GetSupportSurfaceId()` 因此是 0、1…8、10…13、100…105。**它是诊断 + 「面是否换了」的键，不是跨扫描的道具身份**；需要身份的地方读 `IsSupportingAProp()`/`GetSupportKind()`。
3. **支撑面选择**：`TryResolveSupport` 多接收 `(propMinima, propMaxima, propCount)`。道具顶面与静态盒顶面走**同一条** band（`reference + stepLimit`）+ 同一条足迹判据（含边界）+ 同一个「最高者胜，等高保留先读者」。道具在静态盒之后读，所以道具与盒等高时盒赢（注册顺序稳定）。道具没有第二个几何规则。
4. **容量**：`SupportSurfaceMath.MaxSupportPropCount = 6` 是唯一来源，`IsSupportPropCountAllowed(count)` 是唯一判据；`SetSupportProps` 拒绝第 7 件。拒绝语义与 `SetBlockingBoxes` **完全对齐**：整批不写、保留上一批、独立拒绝计数 `GetSupportPropRefusalCount/Reason`、**不进 `overflowCount` latch**——「附近道具多于 6 件」是每次扫描的正常答案，不是场景接错（`BLOCKING_PROPS_PLAN` §121）。
5. **数据流（单一接缝）**：`PickupFieldHost` 拥有道具表、`propBounds`、状态机与吸收门；motor 只接收盒（`SetSupportProps` / `SetBlockingBoxes`）。每 `scanIntervalSeconds` 一遍扫描：每个候选道具**建盒一次**、**分类一次**，得到两个掩码，然后 `BlockingPropMath.SelectNearest` 跑两遍（同一纯函数、独立粘性 `blockingStickinessM = 0.25` 与 `supportStickinessM = 0.10`），最后 `SetSupportProps` + `SetBlockingBoxes`。一件道具只有一个角色，两个集合天然不相交。
6. **吸走即失去支撑**：`AttachProp`/`HideProp` 在把行标成 `StateAttached`/`StateHidden` 的同时把下一次扫描提前到本帧（`nextBlockingScanTime = 0`），所以吸收事件被消费的那一帧扫描就会把该行排除出两个候选；motor 的支撑面回落到地面或更低的面，`SupportDroppedAway → ResolveFallState` 进入 P1 的重力下落。**没有**「支撑道具被吸走」的专用路径，也没有第二个下落规则。
7. **开关**：motor 的 `terrainSupportEnabled == false` 时 `ResolveTerrainSupport` 传 `propCount = 0`，查询与 P1 **逐位一致**（等价于空道具表）。宿主侧读同一个开关：地形支撑关掉时它**清空支撑集**，并把本该是支撑的吸不动件退回**挡路**（P1 之前的世界里它们全是墙）——这样关掉开关不会让球穿过矮件；`blockingDisabled`（历史字段名，语义定为「道具不参与动态障碍」）则清两个集合，球可以穿过全部道具（这是 lab 的显式选择）。两个开关各管一件事，都有「关掉后不会出现第三个状态」的测试/文档约束。
8. **每步/每次扫描成本**：每步 `TryResolveSupport` 多 ≤6 次（band + 足迹 + 高度比较，约 60 flops，零分配）；每 0.05 s 扫描多一次分类（O(1)）与第二遍 `SelectNearest`（≤候选数 × 2）。所有数组在 `Start` 一次性分配。
9. **P2 的可用窗口（由 1 的判据直接推出，作者必读）**：一件道具能当台阶的球径必须同时满足 `D ≥ 4·rise`（几何）与 `D < D_req`（仍吸不动），即 **`[4·rise, D_req)`**；区间为空的道具**永远不会**当台阶，它会先变成食物。本关数据：
   - 躺椅（从台顶 rise 0.09，`D_req ≈ 1.86`）→ 窗口 **0.36–1.86**；
   - 折叠桌（从躺椅 rise 0.35，`D_req ≈ 2.00`）→ 窗口 **1.40–2.00**（这就是「台顶 → 躺椅 → 折叠桌」链能成立的原因；从地面 rise 0.70 需要 `D ≥ 2.80 > D_req`，所以从地面直接上桌只能靠 P3 的斜板）；
   - 保温箱（rise 0.30，`D_req ≈ 1.03`）→ `4×0.30 = 1.20 > 1.03`，**窗口为空**：它永远是挡路或食物，不会被爬上去；
   - 平铺木板/餐盘（rise 0.025/0.04，`D_req ≈ 0.53`）→ 起手 0.30 就能碾过，0.53 之后变食物。
   验收时按这张窗口表判断「该不该能上」，不要按「看起来多高」猜。

| 文件 | 改什么 |
|---|---|
| `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs` | 分类（`TryClassifyProp`）、容量判据、道具面 id 编码、`TryResolveSupport` 的道具段（**唯一**判定实现；原计划放在 `Runtime/Pickup/PickupObstacleMath.cs`，按任务书「判定进 SupportSurfaceMath 或同模块」改放这里，避免第二份模块） |
| `Runtime/Pickup/PickupFieldHost.cs` | `UpdateBlockingProps` → `UpdatePropObstacles`：一遍扫描出两个掩码（§6.3）；两套粘性；新增序列化 `supportStickinessM = 0.10f`；**不再新增** `supportMarginM`（与挡路同值，第二个旋钮只会漂）；`ResetField` 里 `motor.ClearSupportProps()`；`SetCollecting(false)` **不清**支撑集（世界仍然实心，与挡路一致） |
| `KatamariBallMotor.cs` | `SetSupportProps` / `ClearSupportProps`（复用 `SetBlockingBoxes` 的校验路径）、支撑表接线、`GetSupportKind()==3`、`IsSupportingAProp()`、道具容量/拒绝 getter |
| `Docs/Design/BallLayers/TerrainSupport/terrain_support.py` | 分类 + 道具面选择的参考模型（与 C# 同名同序；原计划的 `blocking_props.py` 不在本轮写范围内，分类与支撑选择同属地形支撑这一层） |
| `Tests/EditMode/BallLayers/PropSupportTests.cs`（新） | 见下；`SupportSurfaceTests.cs` 的调用点补空道具表 |
| `Docs/tests/ball_terrain_support/test_prop_support.py`（新） | 与 NUnit 同一组用例（CamelCase → snake_case） |

**NUnit（`Tests/EditMode/BallLayers/PropSupportTests.cs`）**

1. `AnAbsorbProofLowPropIsASupportNotAWall` — 静态 0.028 m 薄板（C14 口径）、`D = 0.30`、支撑面 0 → Support（真餐巾 0.014 / `D_req ≈ 0.18` 是食物，不是这条用例的对象）。
2. `AnAbsorbProofPropExactlyAtTheStepLimitIsASupport` — `rise == stepLimit` → Support（等号钉）。
3. `OneMillimetreAboveTheLimitIsAWall` — 同一件高 1 mm → Blocking。
4. `TheSamePropFlipsToSupportWhenTheBallGrows` — 0.30 m 高、**仍然吸不动**的件在 `D = 1.20`（`stepLimit = 0.30`）→ Support（同一函数；这是几何分支的契约测试，不代表保温箱真能被爬——它的窗口是空的，见规则 9）。
5. `AnAbsorbablePropIsNeitherAWallNorASupport` — 吸收门放开 → None。
6. `ABallAlreadyAboveTheTopNeverGetsAWall` — 下落中的球底在顶上 → Support。
7. `APropTopEntersTheSupportTableWithAPropSurfaceId` — 面 id = 100、高度 = 顶、法线 +Y。
8. `APropFootprintEdgeIsInclusive`（与静态盒同一条边界契约）。
9. `APropOutsideItsFootprintFallsBackToTheGround`。
10. `APropAboveTheStepBandIsNotACandidate`。
11. `ABoxAndAPropAtTheSameHeightKeepTheBox`（等高时注册顺序稳定）。
12. `SupportSelectionReadsExactlyTheReportedPropCount` — 7 件、count = 6，第 7 件永不读；`IsSupportPropCountAllowed(6)` 真、`(7)` 假。
13. `AnAbsorbedSupportPropStartsTheGravityFall` — 选中道具顶 → 道具被吸走（表清空）→ `SupportDroppedAway` → 进入下落。
14. `AnEmptyPropTableLeavesThePreP2Surface` — 同一查询在道具表为空时与 P1 结果逐字段相同（关开关/空表的逐位一致）。
15. `NonFinitePropIsRefusedAndNothingIsWritten`。

**P2 实现记录（2026-11-15，Linux 侧；Unity/U# 编译与 Play Mode 未跑）**

| 项 | 状态 |
|---|---|
| 源码 | `SupportSurfaceMath`（分类/容量/道具面 id/道具支撑段）、`KatamariBallMotor`（接缝 + 表接线 + 诊断）、`PickupFieldHost`（一遍分类、两次选择、两套粘性、复位清两套） |
| 与原文的偏离 | ①分类函数放 `SupportSurfaceMath`（原文给了 `Runtime/Pickup/PickupObstacleMath.cs` 或 `BlockingPropMath`；任务书写范围只到 `Runtime/Pickup/PickupFieldHost.cs` 与 `Runtime/BallLayers/TerrainSupport/`，放支撑层也符合「判定只有一份」）；②**不新增** `supportMarginM`（原文说「与挡路同值」，所以复用 `blockingMarginM`；第二个旋钮只会漂）；③`blockingDisabled` 一个开关管两个角色（原文只写挡路；两个开关会制造「是台阶但不是墙」这种分类器不产生的状态）；④分类多一个 `alreadyAbove` 参数（原文签名草稿里有，落地时保留，理由见定型规则 1）；⑤探索里的 `Docs/Design/PickupAbsorb/blocking_props.py` 不写（不在本轮写范围），Python 参照并入 `terrain_support.py` |
| NUnit | **NOT_RUN**：新增 `PropSupportTests.cs` 15 例；`SupportSurfaceTests.cs` 的 8 个调用点补空道具表（18 例语义不变） |
| Python | **PASS**：`ball_terrain_support` 33 例（18 P1 + 15 P2） |
| 未做 | P3 斜面；Unity 编译 / `run_tests` / Play Mode S8–S12（本侧不是 Unity 操作者） |

**关开关逐位一致**：motor 在 `terrainSupportEnabled == false` 时向查询传 `propCount = 0`，等价于 P1 的空道具表（`AnEmptyPropTableLeavesThePreP2Surface` 钉住）；宿主读同一个开关，关掉时清支撑集并把本该是支撑的吸不动件退回挡路（P1 之前它们全是墙），所以关掉后既不会站上道具，也不会穿过矮件。`blockingDisabled == true` 则清两个集合（lab 的显式「道具不参与」选择）。没有第三条路径能让一个道具在开关关掉后仍参与支撑。

**Play Mode**

| # | 步骤 | 判据 |
|---|---|---|
| S8 | `D = 0.30` 碾过平铺木板/餐盘（顶 0.025/0.04，`D_req ≈ 0.53`） | 不减速、不被挡；`GetSupportSurfaceHeightWorldM()` 短暂读到 0.025/0.04；`supportId ≥ 100`、`GetSupportKind() == 3`。**不要用真餐巾**：它 `D_req ≈ 0.18`，在 0.30 已经是食物（P2 定型规则 1 的吸收门先答） |
| S9 | `D = 0.30` 撞保温箱 | 停住（挡路）。**它在 `D ≥ 1.03` 就变食物**，`4×0.30 = 1.20`，所以保温箱**永远不是台阶**（P2 规则 9 的空窗口）。「吸不动但能爬」请用第一关躺椅/折叠桌链或地图 0 的 crate@2.20 |
| S10 | 站上折叠桌（0.70，`D_req ≈ 2.00`；经躺椅上去）后长到 `D ≥ 2.00` | 被吸走（`absorbed` 跳数）、球下落（`IsFalling` 至少一帧为真，`supportId` 回到 0/更低面）、**没有** teleport 闪断（`discontinuous` 未置位） |
| S11 | 门堆（PICNIC §3.5.3）回归 | 0.30 m 球仍被 0.10 m 缝挡死（台阶规则不吃水平缝） |
| S12 | 6 件挡路 + 6 件支撑同时存在 | 帧时间与 `P1` 同量级（记录到 `Docs/Evidence/`）；两个计数各自 ≤6 |

### P3 — 斜面（2026-11-15 已实现；Unity/U# 编译与 Play Mode 未跑）

**范围**：Ramp 表 + 烘焙 + 坡度护栏 + 两端衔接 + `VisualRoll` 法线（P1 已把法线换成支撑面法线，这一步只是让「支撑面可以是斜的」）。

**P3 定型规则（实现前补写；原文只有轮廓，这一节把接口与判据钉死）**

1. **表示与烘焙**：斜面 = `center`（行走面上一点）+ `normal`（单位，`n.y > 0.05`）+ `right`（水平单位）+ `halfWidth` + `halfRun`。`LevelArenaHost` 只烘一次：盒中心 + `n·thickness/2` 得行走面中心；`right` 取 `Transform.right` 的 XZ 投影；`halfWidth`/`halfRun` 用行走面 4 个角点在 `right` / `down` 上的投影（`TryBakeRamp`），**不接受作者手填**。`down = normalize(n.x, 0, n.z)`（法线向低侧倾斜）。
2. **坡度**：`maxSupportSlopeDegrees` 是 motor 的序列化字段（默认 45，PROVISIONAL），由 `TryValidateRamp` 判；`slope > limit` → `RampRefusalSlopeTooSteep`，不注册。原因码另有 `NormalNotUpward`（`n.y ≤ 0.05`）、`DegenerateAxis`（平坦/零宽/右轴无水平投影）、`SlopeLimitInvalid`、`InvalidInput`、`Capacity`。`GetLastRampRefusalReason()` 返回字面量，坡度数值单独 `GetLastRampRefusalSlopeDegrees()`。
3. **只支撑，不进扫掠**：`AddRamp` 不写 `boxMinimumBuffer`/`boxMaximumBuffer`，不触发 `RebuildObstacles`，不碰台阶掩码。坡底因此**没有**半径扩张 AABB 的隐形墙。作者若把同一 Collider 同时填进 `staticBoxes` 与 `rampBoxes`，`LevelArenaHost` 打 LogError 并**跳过 box**、保留 ramp。
4. **选面**：斜面在静态盒之后、道具之前读；高度 = 平面方程在探针 XZ 的解，法线 = 斜面法线。候选判据是**同一条** `IsStepWithinLimit(height, bandReference, stepLimit)`（与地面/道具一致，含等号）；**不**为斜面另写 band。等高时静态盒赢、斜面对道具赢。面 id `10+i`，`GetSupportKind() == 2`。
5. **两端衔接**：坡顶与台面齐平（同一探针处高度相等）时选面从 ramp 切到 box 但高度不跳；坡底与地面齐平时高度连续，坡底那一步的球心抬升 `r(1/n.y−1)` 由现有抬升上限钳住（§5.3 注）。**没有**第二套衔接判据。
6. **视觉滚动**：P1 已把 `VisualRollMath` 的法线换成支撑面法线，斜面自动正确（支撑面法线 = 斜面法线）。
7. **切向重力**：`rampTangentialGravityScale`（0..1，默认 **0**，PROVISIONAL）只对 ramp 支撑、非下落状态生效；>0 时在支撑解析后调用 `TryApplyRampTangentialGravity`，把 `g·sinθ·scale·dt` 加到水平下坡速度并受 `maxSpeedMps` 钳位（下一步生效，20 ms 滞后）。默认 0 逐位等于 P1 行为。
8. **开关**：`terrainSupportEnabled == false` 时 `ResolveTerrainSupport` 传 `rampCount = 0`、`boxIsSteppable` 全 false，切向重力跳过——查询与 P1/P2 逐位一致。
9. **容量**：`SupportSurfaceMath.MaxRampCount = 4` + `IsRampCountAllowed` 是唯一来源；第 5 条在 motor 里被拒并 `SignalOverflow`（场景接线错误）。
10. **不可吸**：静态斜面属于场景几何，`LevelArenaHost` 永远看不到道具；「木板中期能粘起来」等 `COLLIDER_LAYER` 的带朝向盒（§6.5 / U4）。

| 文件 | 改什么 |
|---|---|
| `SupportSurfaceMath.cs` | 斜面常量/面 id/容量/原因码、`TryBakeRamp`、`TryValidateRamp`、`TryResolveSupport` 的斜面段与 `boxIsSteppable` 参数、`StepMaskNeedsRefresh`（P1 第二次回归）、`TryApplyRampTangentialGravity` |
| `KatamariBallMotor.cs` | 斜面并行数组、`AddRamp`/`ClearRamps`/容量/拒绝码 getter、支撑表接线、`GetSupportKind()==2`、`maxSupportSlopeDegrees`、`rampTangentialGravityScale`、掩码门控改成参考/直径 |
| `LevelArenaHost.cs` | `rampBoxes` 烘焙（§5.1）+ 与 `staticBoxes` 的重复自检（§5.5）+ 报告 `ramps=`/`rampRefusal=` |
| `Editor/LevelBuild/PlacementImporter.cs` | `role=ramp` 的结构写入 `rampBoxes`（只改 ramp 接线） |
| `terrain_support.py` + 测试 | 斜面与掩码回归用例 |

**NUnit（`Tests/EditMode/BallLayers/RampSupportTests.cs`，15 例；`SupportSurfaceTests.cs` 另加 4 例掩码/下落回归）**

1. `RampHeightMatchesThePlaneEquation` — 三探针，手算 `center.y ± tanθ·Δz`。
2. `RampFootprintRejectsPointsOutsideItsHorizontalRectangle` — 四条边含等号、外 0.1 mm 出界。
3. `ARampSteeperThanTheLimitIsRefusedWithAReason` — 58° 被拒、码与坡度；44.9/45.1 边界；容量谓词。
4. `ARampWithANonUpwardNormalIsRefused` — `n.y ≤ 0.05`、平坦板、垂直 right。
5. `BakeFromATransformedBoxYieldsTheExpectedPlane` — 30°/45°、非单位缩放、行走面中心。
6. `HalfRunUsesTheHorizontalProjection` — 10.7° 板的 `halfRun = 0.687`，不是 `0.6992`。
7. `CapacityRefusesTheFifthRampAndKeepsThePreviousFour` — 5 条只读 4 条。
8. `TheRampFootMeetsTheFloorWithoutAStep` — 坡脚高度连续、低于 `minStepHeightM`。
9. `ARampRegisteredAsABoxWouldBeAWallAtItsFoot` — 同一 AABB 盒顶超出 band（坡底墙的反例）。
10. `AnEmptyRampTableLeavesThePreRampSurface` — `rampCount = 0` 与空表逐字段一致（关开关的纯数学半边）。
11. `TheHighestSurfaceWinsAcrossBoxesRampsAndProps` — 最高者胜 + 等高注册顺序（盒 > 斜 > 道具）。
12. `RampSurfaceIdsNeverCollideWithBoxOrPropIds` — 10..13 与 1..8 / 100+ 不相交。
13. `NonFiniteRampInputIsRefusedAndNothingIsWritten` — 所有 out 归零。
14. `TheRampTopIsFlushWithTheTableEdge` — 接缝处等高、两侧 1 mm 高度差 < 1 mm。
15. `TheTangentialGravityIsOffByDefaultAndPushesDownhill` — 默认 0 不变、下坡方向、速度钳位。
- `SupportSurfaceTests` 新增 4 例（P1/P3 边界）：`ALandedBallKeepsTheC09DeckInTheSweep`、`TheStepMaskGateFollowsThePredicateInputs`、`TheSweepMaskAndTheSupportTableNeverDisagree`（性质：任意 mask 参考/查询参考下，静态盒被选 ⇔ 掩码为真）、`DroppingDownTheSameSurfaceIsNotALostSurface`（沿坡下滚不触发重力下落）。

**Play Mode**

| # | 步骤 | 判据 |
|---|---|---|
| S13 | 1.5 m 长的板从地板搭到 0.50 m 平台（18.4°） | 开上去；`center.y` 逐步单调，单步变化 ≤ 0.1 m（坡脚法向切换那一步约 `r(1/n.y−1)`，30° 时 ~7.6 cm，不是瞬移） |
| S14 | 坡顶与台面接缝 | 不在接缝处停顿；`support` 从 ramp 切到 box 时高度差 < 0.005 |
| S15 | 坡中间横向开出 | 掉到地面，不被坡的包围盒挡住（验证 §5.5 的陷阱没踩） |
| S16 | 45.1° 的板 | `AddRamp` 拒绝；日志一条；球被坡旁的墙挡住（作者必须补墙） |
| S17 | 停在 30° 坡上 5 s 不推 | 球不动（§5.4：切向重力默认 0，这是**预期**，不是 bug） |
| S18 | 第一关两块斜板（`ramp_to_bed` 10.7°、`ramp_to_table` 30°） | `[arena] ramps=2/2`；球分别上到 0.26 花圃台与 0.70 桌面；`supportId` 在 10..11、`GetSupportKind()==2`；坡上不滑 |

**P3 实现记录（2026-11-15，Linux 侧；Unity/U# 编译与 Play Mode 未跑）**

| 项 | 状态 |
|---|---|
| 源码 | `SupportSurfaceMath`（烘焙/校验/斜面段/面 id/原因码/切向重力）、`KatamariBallMotor`（斜面表/API/接线/kind/两个新字段/掩码门控）、`LevelArenaHost`（`rampBoxes`/重复自检/报告）、`PlacementImporter`（`role=ramp` → `rampBoxes`） |
| P1 第二次回归 | 已修：掩码是静态盒唯一结论，`TryResolveSupport` 读 `boxIsSteppable`；门控 `StepMaskNeedsRefresh` 看参考/直径。另修 P3 发现的「沿坡下滚被当成失去支撑」：`SupportDroppedAway` 多一个 `sameSurface`，同一个面 id 的下降不是掉走（§4.7） |
| 导入器接缝 | `PlacementImporter.cs` 基于 94b0518（ground/boundary 可见体 `ApplyStructureVisuals`）重读后只做 ramp 接线：`role=ramp` 的根对象 `BoxCollider` 写入 `rampBoxes`；可见体逻辑只给 marker 角色加子物体，ramp 不受影响 |
| NUnit | **NOT_RUN**：`RampSupportTests.cs` 15 例 + `SupportSurfaceTests.cs` 新增 4 例；`SupportSurfaceTests`/`PropSupportTests` 的调用点补掩码与空斜面表 |
| Python | **PASS**：`ball_terrain_support` 53 例（23 P1/掩码回归 + 15 P2 + 15 P3），全套 479 例 OK（含并行代理新增的套件） |
| 未做 | Unity 编译 / `run_tests` / Play Mode S13–S18；`PlacementImportReport.cs` 的摘要字符串仍写「P1 has no ramp slot」（不在本轮写范围，需随导入器报告一起改） |

### P4 — 打磨与实测（可选，取决于 P1–P3 的手感）

1. **探针细化**：骑棱时用「最低点正下方」而不是球心（多一次 32 点扫描，§3.5）。
2. **GC 实测**：台阶掩码重建的 `new Vector3[]` 是否可见（Profiler）；若可见，评估「把 `maxStepHeight` 下沉进 `SweepMath`」——**代价是同步改 `sweep.py` 参考模型与 `SweepTests`**，不能只改 C#。
3. **参数试玩**：`stepHeightOverBallDiameter`（0.25 是否太松/太紧）、`maxSupportSlopeDegrees`（45）、`supportStickinessM`。
4. **依赖**：等 `COLLIDER_LAYER.md` 的「带朝向盒」进 manifest，把 `PropTop` 升级成 `PropRamp`（§6.5）。
5. **HUD 一行**：`support=<kind> y=… step=<0|1>`，只在 `logSetupLine` 打开时每 0.5 s 打一次（不要每帧）。

---

## 9. 风险

| # | 风险 | 机制 | 处理 |
|---|---|---|---|
| R1 | **台阶边缘抖动**（在棱上反复上下） | 探针在足迹边界内外跳；`supportSurfaceY` 在 0.26 与 0 之间抖 | §4.5 的两条（刷新门控、参考取球底）+ 最小台阶；验收 S3（10 s 波动 < 1 mm）。**残余风险**：球静止在恰好 0.5 步宽的位置仍可能每 3 步抖一次 —— 若出现，加「足迹内缩 0.01 m 的迟滞环带」（两条边界：进入用外沿、退出用内沿） |
| R2 | **抬升穿模** | 台阶比 `proxyLiftCap = 0.04` 高得多 | §4.4 的 `maxStepUpLiftPerStep`；**只跨级那一步**给大上限。验收 S2 看单步 `center.y` 跳变 ≤ 0.275（D=1.1） |
| R3 | **接触饱和**（`ContactCapacity = 4`） | 墙角 + 台阶边缘 | 台阶盒被**剔除**后接触数**下降**（本设计对饱和是减压方向）；仍读 `WasContactBufferSaturated()` 作诊断；墙角卡顿优先怀疑这里（`BLOCKING_PROPS_PLAN` R3） |
| R4 | **每步成本** | 19 个候选面（最坏）；方法调用开销 | 典型 1–3 激活面、~36 flops/步；**一个静态函数吃数组**、不分配；P4 实测帧时间（0/6/12 个激活面） |
| R5 | **镜头与低通打架** | 台阶/掉落是**位置**跳变，`FollowRigMath` 用 5 Hz 高度低通、读的是**空代理**核心球心 | 0.26 m 台阶：滤后 ~46%/步，4 步落定（可接受）；0.72 m 桌面掉落读作「顿一下」。**不要**用 `discontinuous` 去吸附台阶（那会冻结视觉滚动）。若试玩觉得晃：单独给跟随架一个「支撑面高度」通道（**新缺口**，登记在 README 缺口清单，不在本文实现） |
| R6 | **与小棍叠加** | 探针用球心 XZ，而最低点可能偏 0.3 m；`liftCap` 两条上限互斥 | 探针近似有界（≤ `maxProxyProtrusionWorldM`），高度上**无误差**（§3.3）；`liftCap` 的取舍是**只跨级那一步**（§4.4）。**必须**与并行代理确认：`GetSupportSurfaceHeightWorldM()` 报的是**支撑面**高度，不是球心减去 support（后者会被小棍抬高，撑高分类就会漂） |
| R7 | **墙被「台阶化」**（球爬上不该上的东西） | `rise` 用的是**当前支撑**；球站在 0.26 台面上时，旁边 0.5 m 的盒子 rise = 0.24 ≤ stepLimit(1.1)=0.275 → 可爬 | 这是**特性**（叠箱楼梯），但要写进关卡说明；若某面必须不可爬：把它做成平面（`AddPlane`，永远不是支撑）或提高它的高度。**不要**加「只能爬一级」的规则（会与「叠箱」原则冲突） |
| R8 | **斜面被误登记成盒** | 作者把同一个 Collider 填进两个数组 | §5.5 的 `Setup` 自检（LogError + 跳过 box） |
| R9 | **矮扁件从「隐形墙」变成「隐形台阶」** | 台阶规则让 2.8 cm 餐巾可碾过，但**视觉上**球几乎没抬 | 这是正确的观感（就是碾过一张纸）；风险是**可吸结构被吃时**球已经站在它上面 → 0.028 m 的掉落，无感。**不处理** |
| R10 | **旧 lab 行为变化** | `AddBox` 两用 + 台阶规则 + 重力 | **默认已改为 true**（用户 2026-11-15，见 8.1）：`CpuSkirmish` 的 bootstrap 必须显式 `motor.SetTerrainSupportEnabled(false)` 或显式接受变化（`Labs/BallPlayground` 已于 2026-11-15 退役）。`StoryLevelLabBootstrap` 不再登记任何几何，注册归 `LevelArenaHost` |
| R11 | **验收口径被打破** | `[story-lab] bottom = center.y − 直径/2` 在坡上不成立 | 日志改成 `bottom = center.y − 支撑面高`（§3.6 第 3 条）。**这是必须一起改的**，否则 S13 会误判 |
| R12 | **`AddBox` 两用让「墙的顶面」变成可站表面** | 1.0 m 矮篱的顶面理论上可站 | `height > supportY + stepLimit` 时**不是候选**（NUnit 第 6 条），所以只有球真的爬得上去才会用到它；矮篱高度 1.0 m 在 `D < 4.0` 时永远进不了候选 |
| R13 | **上台阶时球壳先压进台阶** | 探针在球心：球心进入足迹才抬升，而球的可视外壳在 `d < sqrt(r² − (r − h)²)` 时已经与台阶相交（`D = 1.10`、`h = 0.26` → 约 0.47 m 行程） | 设计接受（§3.3：宁可早掉、不悬空）。这是**看得见**的观感，不是穿模 bug；P4 的探针细化（最低点正下方）是同一条修法。0.26 m 台阶在 4 m/s 下约 0.12 s |
| R14 | **非台阶盒的「顶面托住」** | 扫掠是半径扩张 AABB：球心在盒顶以上、水平仍在扩张足迹内时，盒子顶面会托住球，直到球心离开足迹 | 只影响「本来就不该站上去的盒」（传送/缩球时在顶上的情形）；正常的台阶盒在台阶掩码里，不走扫掠，所以 S4 的「从台顶开出去」不会被托住 |

---

## 10. 原作行为与出处（以及暂定值）

### 10.1 查到的（带出处）

| # | 断言 | 出处 | 判定 |
|---|---|---|---|
| O1 | 原作关卡是「除了墙/楼梯/地板，几乎全部可吸」；同一件东西先挡路、变大后被吃 | `Docs/Research/KatamariMaps/README.md` §0 #8、§6 P5/P6（S-M16 wiki3、S-M09 Famitsu 先行体验） | `confirmed`（T3 wiki + T2 实机，两源一致） |
| O2 | **「能不能翻越高差」无公开说明**；`max_step_height_over_ball_diameter = 0.25` 是**本项目自定** | `Docs/Research/KatamariMaps/README.md` §6 P8，原文「原作只确认『只能吸比球小的』，**能否翻越高差无公开说明**」［S-M16］ | 原作 `unconfirmed`；数值 `[项目自选]` |
| O3 | 原作球撞到吸不动的东西会**被弹开并「削れ」**（掉体积） | `Docs/Research/KatamariMaps/README.md` §0 #8、§1 引 S-M09（Famitsu 先行体验：「巻き込めないモノに勢いよくぶつかると塊が弾かれ削れてしまう」） | `confirmed`（T2 实机 + T3 wiki） |
| O4 | 开局点常是**极小封闭空间**（海盗船船尾的**桌面**上），再逐级外扩 | `Docs/Research/KatamariMaps/README.md` §0 #5（S-M09） | `confirmed`（T2） |
| O5 | 拾取判定是**体积**：`V_item ≤ V_ball / 10` | `Docs/Research/KatamariBallRules/ABSORB_SIZE_RATIO.md`（`Docs/Design/PickupAbsorb/README.md` 1.1 引用） | 代码已按此实现（`IsBeyondAbsorbVolumeLimit`） |
| O6 | 「撞到吸不动的道具是停还是弹」在**对战规则**那份研究里是 `unconfirmed` | `Docs/Design/PickupAbsorb/BLOCKING_PROPS_PLAN.md` §4 | 与 O3 **两份研究结论不一致**（O3 更晚、有两源）。本文不据此改挡路响应，只记录冲突 |

**关于 O3 与本设计的关系**（必须诚实）：如果原作真的是「撞不动的就弹开并削球」，那么「2.8 cm 餐巾可碾过」就不是还原，而是**我们的选择**。本文的立场：① 我们**没有**任何"削球"规则（`BLOCKING_PROPS_PLAN` §4 已明确不做，属玩法变更）；② 原作的「弹开」针对的是**物件**，而「地面/台阶/桌沿」是地形，本文的台阶规则只作用于「球能从上面滚过去的高度」，与「撞大件」不冲突；③ 这条差异**写进 §11 U1**，让用户拍板。

### 10.2 暂定值表（全部 `PROVISIONAL`）

| 参数 | 默认 | 来源 / 理由 | 谁来调 |
|---|---:|---|---|
| `stepHeightOverBallDiameter` | **0.25** | `layout.json` 已有常量（F13），与 `gate_bed_*` 的 0.95→1.10 m 门互锁；**原作比例未公开**（O2） | 关卡（序列化字段） |
| `maxStepUpLiftWorldM` | 0.6 m | 护栏：`> 0.25 × 2.2 = 0.55`，正常范围碰不到；防 `kStep` 被误配 | 代码常量 |
| `minStepHeightM` | 0.005 m | 数值去噪；**不要**与 `max_route_step_m = 0.05` 混用 | 序列化 |
| 台阶参考高度 | `max(上一步支撑面, 球心下方最低核心点)` | **不是旋钮**：它是「扫掠剔除与支撑激活共用同一个参考」的载体（§4.5、§8.1 D4）；`stepHysteresisM` / `stepStickMarginM` 已删除 | 代码（不序列化） |
| `maxSupportSlopeDegrees` | **45°** | 硬上限 51.3° 由 `min(maxLift,maxDrop)/(maxSpeed·dt) = 1.25` 反推（§5.2），取 30% 余量 | 序列化 |
| `MaxRampCount` | 4 | 本关角内最多 1–2 个斜板；4 留余量 | 代码常量 |
| `MaxSupportPropCount` | 6 | 与 `MaxBlockingBoxCount` 对称；「球周围同时贴几件」的估法同 `BLOCKING_PROPS_PLAN` §9-2 | 代码常量 |
| `supportStickinessM` | 0.10 m | 小于挡路的 0.25：支撑留旧件比挡路留旧件更难受 | 序列化 |
| `terrainSupportEnabled` | **true**（2026-11-15 用户改定；本文原文 false） | 正式关卡的地面/台阶/台面本来就是静态盒；旧 lab 显式关（R10、§8.1） | 序列化 / `SetTerrainSupportEnabled` |
| `gravityMps2` | **9.81 m/s²** | 用户定「球会从台面边缘掉下去」；具体加速度是 PROVISIONAL（原作无测量），可序列化 | 序列化 |
| `maxFallSpeedMps` | 40 m/s | 护栏（单步 ≤0.8 m），不是产品值；正常关卡高度碰不到 | 序列化 |
| `supportDropTriggerM` | **0.01 m** | 判定「支撑面掉走」的容差；小于它的落差由 `maxDropPerStep` 一步吃掉，并挡住微斜地面上的每步抖动 | 序列化 |
| 探针 = 球心 XZ | 开启 | §3.3：高度上精确，仅边缘判定近似 | 代码（P4 可换） |
| 竖直重力 | **有**（P1 起） | 失去支撑时自由落体；没有跳跃、没有向上速度 | 序列化 `gravityMps2` |
| 切向重力 | **无**（`rampTangentialGravityScale = 0`） | F9：坡上不加沿坡分量，属玩法变更（§11 U3）；P1 的竖直重力不改变这条。P3 实现成 0..1 的序列化字段：>0 时 `g·sinθ·scale` 沿水平下坡、受 `maxSpeedMps` 钳位，默认 0 逐位等于 P1 | 序列化（试玩时定） |
| `rampTangentialGravityScale` | **0** | 切向重力的比例（0 = 关闭，1 = g·sinθ）；PROVISIONAL，U3 试玩用。只对 ramp 支撑、非下落生效 | 序列化 |

---

## 11. 需要用户拍板（只列真正影响玩法的）

| # | 问题 | 选项 | 代价 |
|---|---|---|---|
| **U1** | **矮小的可吸物件应当「碾过」还是「挡路」？** 原作 O3 说撞不动会被弹开削球，O6 说这条没确认；而我们的台阶规则会让 2.8 cm 餐巾、0.04 m 托盘在**任何**球径下都可碾过 | **A（推荐）碾过（台阶规则照 §4）**：观感符合「地上有张纸」，且 `climb_step` 与「木板先当路」两个玩法成立 | 与 O3 的「弹开」不一致；失去「小件也能挡你一下」的摩擦感 |
| | | B 只让**固定结构**（花圃台、桌沿）当台阶，可吸物件一律挡路 | 回到「2.8 cm 隐形墙」（PICNIC G9 的原始 bug）；「木板先当路」只能靠静态斜面，道具永远当不了路 |
| | | C 按物体「厚/高比」再分一类（薄片碾过、块状挡） | 第三个阈值，`OPEN_DECISIONS` 明确警告过；且要给每件道具论是非 |
| **U2** | **球站在要被吸的东西上时，允许吸走它吗？** | **A（推荐）允许**：桌子被吃 → 球落下（§6.4），符合「后期台球桌都能粘起来」 | 0.72 m 的掉落读作「顿一下」；镜头低通要试玩确认 |
| | | B 禁止（宿主跳过「正被支撑的那件」）：一行判断 | 「站在桌上永远吃不掉桌子」，玩家会觉得"它明明能吃了"；且这是**玩法判据**，不是几何判据 |
| **U3** | **坡上要不要有切向重力（自己滑下去）？**（注：竖直重力 2026-11-15 已定，有且只在下落时作用，见 §8.1；U3 只问沿坡分量） | **A（推荐）不加**：坡就是通道，速度只由输入决定（现状 F9） | 坡没有重量感，陡坡/缓坡手感一样；「球停在 30° 坡上」可能被玩家读作 bug |
| | | B 加：`g·sinθ` 的切向分量 | 改 `BallMotionMath` 契约 + `Docs/tests/ball_motion/` 全部重定 + 与「推球」手感冲突；原作是否如此 `unconfirmed` |
| **U4** | **斜板是「静态不可吸」还是「可吸」？**（决定「木板中期能粘起来」能不能兑现） | **A（推荐）P3 先做静态斜面**（`LevelArenaHost` 声明），先拿到「沿木板上桌」；可吸斜板等 `COLLIDER_LAYER` 的带朝向盒 | 「木板能粘」推迟到那条线之后；静态斜板永远不可吸 |
| | | B 现在就把道具行扩成「带朝向盒」，让 prop 也能当坡 | 要改 manifest / `PickupFieldSync` / `propBounds` 三层分离的边界（`COLLIDER_LAYER` §5），是另一条线的活，且会和并行代理的文件撞 |
| **U5** | **`stepHeightOverBallDiameter` 用 0.25 还是更松？**（0.25 → 花圃 0.26 m 需要 1.04 m 球；0.30 → 需要 0.87；0.20 → 需要 1.30） | **A（推荐）0.25**：数据、校验器、关卡门已经按它互锁（F13），改了要同时改 `layout.json`、`validate_map.py` 第 13 条、`gate_bed_*` 门、以及两张关卡 JSON 的 `map_snapshot` | 改一个数要动四处（且校验器会先报错） |
| | | B 先按 0.25 上线，试玩后再调 | 调的时候上面四处一起动，等于一次小型重定 |

**不列为拍板项**（已有结论或纯实现）：架构 A vs B（§0，若用户不同意再重开）、`AddBox` 两用、斜面 45° 护栏、容量 4/6、`LevelArenaHost` 字段形状。

---

## 12. 本轮写范围与校验（P1 实现记录，2026-11-15）

| 项 | 状态 |
|---|---|
| 改动文件 | 新 `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs`；改 `Runtime/BallMotor/KatamariBallMotor.cs`；新 `Runtime/Story/LevelArenaHost.cs`；改 `Labs/StoryLevel/StoryLevelLabBootstrap.cs`（只留自测驱动/镜头/日志）；新 `Docs/Design/BallLayers/TerrainSupport/terrain_support.py`；新 `Docs/tests/ball_terrain_support/`（`__init__.py` + 17 例）；新 `Tests/EditMode/BallLayers/SupportSurfaceTests.cs`（18 例）；本文；`Docs/Design/BallMotor/README.md`。 |
| 未改 | 场景 / prefab / `.asset` / `tasks.json` / `Runtime/Pickup/` / `Runtime/Riding/` / `Runtime/Hub/` / `Runtime/BallLayers/PushCue/` / `Labs/` 下除 `StoryLevelLabBootstrap.cs` 与 `Labs/README.md` 外的文件（本轮也没动 `Labs/README.md`）。新 `.cs` **没有**造 `.meta`（本仓规矩）。 |
| Unity / U# 编译 | **NOT_RUN**（本侧不是 Unity 操作者；Linux 检出没有 C# 编译器） |
| NUnit（EditMode） | **NOT_RUN**：已写 17 例，等 Unity 侧 `run_tests` |
| Play Mode S1–S7 | **NOT_RUN**：操作清单见 §8 与交付说明 |
| Python 参考模型 | **PASS**：`python3 -m unittest discover -s Docs/tests` → `Ran 297 tests ... OK`（新增 18 例，全部来自 `ball_terrain_support`） |
| 计划校验 | **PASS**：见下 |
| `Physics.SphereCast` 等 API 暴露 | 未用到（路线 A）；`NEEDS_UNITY_VERIFICATION` 一条仍留在 §0.2，与本实现无关 |

```text
python3 Docs/tools/validate_plan.py
  -> PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
     Dependency roots (external requirements NOT checked): T00, T01, T02, T03
     NOT_RUN: Unity/Udon compilation, VR comfort, multiplayer, performance, commerce.

python3 -m unittest discover -s Docs/tests
  -> Ran 297 tests in 6.363s
     OK
```

**下一轮（给实现者的最小启动包）**：§7.2/§7.5 的接口签名 + §8 P1 的 7 个文件 + 14 条 NUnit 用例名。**P1 落地前不要再动 `SweepMath`**——本设计一行都不需要改它（这正是选 A 但只做「选择与过滤」的收益）。

### 12.1 P2 实现记录（2026-11-15，Linux 侧）

| 项 | 状态 |
|---|---|
| 改动文件 | 改 `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs`；改 `Runtime/BallMotor/KatamariBallMotor.cs`；改 `Runtime/Pickup/PickupFieldHost.cs`；新 `Tests/EditMode/BallLayers/PropSupportTests.cs`（15 例）；改 `Tests/EditMode/BallLayers/SupportSurfaceTests.cs`（调用点补空道具表）；改 `Docs/Design/BallLayers/TerrainSupport/terrain_support.py`；新 `Docs/tests/ball_terrain_support/test_prop_support.py`（15 例）；本文。 |
| 未改 | 场景 / prefab / `.asset` / `tasks.json` / `Labs/` / `Editor/` / `Runtime/Story/` / `Runtime/Pickup/PickupAbsorbMath.cs` / `BlockingPropMath.cs`。新 `.cs` **没有**造 `.meta`（本仓规矩）。 |
| Unity / U# 编译 | **NOT_RUN**（本侧不是 Unity 操作者；Linux 检出没有 C# 编译器） |
| NUnit（EditMode） | **NOT_RUN**：已写 15 例（P2），等 Unity 侧 `run_tests` |
| Play Mode S8–S12 | **NOT_RUN**：操作清单见 §8 P2 |
| Python 参考模型 | **PASS**：`python3 -m unittest discover -s Docs/tests` → `Ran 376 tests ... OK`（`ball_terrain_support` 33 例 = 18 P1 + 15 P2） |
| 计划校验 | **PASS**：`PASS: 61 tasks; both release DAGs acyclic; source hashes verified.` |

```text
python3 Docs/tools/validate_plan.py
  -> PASS: 61 tasks; both release DAGs acyclic; source hashes verified.
     Dependency roots (external requirements NOT checked): T00, T01, T02, T03
     NOT_RUN: Unity/Udon compilation, VR comfort, multiplayer, performance, commerce.

python3 -m unittest discover -s Docs/tests
  -> Ran 376 tests in 3.567s
     OK
```

### 12.2 P3 实现记录（2026-11-15，Linux 侧）

| 项 | 状态 |
|---|---|
| 改动文件 | 改 `Runtime/BallLayers/TerrainSupport/SupportSurfaceMath.cs`；改 `Runtime/BallMotor/KatamariBallMotor.cs`；改 `Runtime/Story/LevelArenaHost.cs`；改 `Editor/LevelBuild/PlacementImporter.cs`（仅 ramp 接线）；新 `Tests/EditMode/BallLayers/RampSupportTests.cs`（15 例）；改 `Tests/EditMode/BallLayers/SupportSurfaceTests.cs`（掩码参数 + 3 例回归）、`PropSupportTests.cs`（掩码参数）；新 `Docs/tests/ball_terrain_support/test_ramp_support.py`（15 例）；改 `test_terrain_support.py`（掩码参数 + 3 例回归）；改 `Docs/Design/BallLayers/TerrainSupport/terrain_support.py`；本文；`Docs/Design/BallMotor/README.md`；`Docs/Design/Levels/physics_test_field/C09/placement.json` 的 note。 |
| 未改 | 场景 / prefab / `.asset` / `tasks.json` / `Runtime/Pickup/` / `Labs/` / `Editor/PropArt/` / `Art/`；`Editor/LevelBuild/PlacementImportReport.cs`（摘要仍写「P1 has no ramp slot」，需另改）。新 `.cs` **没有**造 `.meta`（本仓规矩）。 |
| Unity / U# 编译 | **NOT_RUN**（本侧不是 Unity 操作者；Linux 检出没有 C# 编译器）。风险点：`BoxCollider.center/size` 与 `Transform.lossyScale` 在 U# 的暴露未在本检出验证（见 §交付清单）。 |
| NUnit（EditMode） | **NOT_RUN**：`RampSupportTests.cs` 15 例 + `SupportSurfaceTests.cs` 3 例回归，等 Unity 侧 `run_tests`。 |
| Play Mode S13–S18 | **NOT_RUN**：操作清单见 §8 P3。 |
| Python 参考模型 | **PASS**：`python3 -m unittest discover -s Docs/tests` → `Ran 479 tests ... OK`（`ball_terrain_support` 53 例 = 23 P1/掩码回归 + 15 P2 + 15 P3；全套 479 例含并行代理新加的套件）。 |
| 计划校验 | **PASS**：`PASS: 61 tasks; both release DAGs acyclic; source hashes verified.` |
