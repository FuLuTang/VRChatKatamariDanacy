# 剧情模式切片：输入、球视图、骑球、吸附宿主与剧情回合

> **2026-11-16 门已改**：吸收尺寸门按原作数字：物件体积 `V = k·a·b·c`（`k` 缺省 0.70），门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`。「全局球径上限与第一关星级带」一节的星级档位件数按旧体积比门（`a·b·c ≤ (π/6)·D³/10`）算出，只作评分模块的参考输入，不是现行口径；现行口径与实现清单见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。


> 状态：五个模块的源码与 U# 程序资产都在仓库里；Unity 侧的 Play 证据只有 2026-11-16 的 Hub 闭环 ClientSim 实跑（见文末「主房间闭环」）和 StationPlatform 实验的 PC 实测（[Evidence/StationPlatform](<../../Evidence/StationPlatform/README.md>)），其余运行时行为见文末「已知限制」。
> 计划与判据见 [第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §1–§4、§7 S1–S4。

| 模块 | 文件 | 状态 |
|---|---|---|
| 输入 | `Runtime/Input/DesktopMoveInput.cs` | 两把闸门（骑乘 / 回合） |
| 球视图 | `Runtime/BallPresentation/BallView.cs` | 按语义直径缩放 CoreMesh |
| 骑球 | `Runtime/Riding/BallRideStation.cs`、`BallSeatInteract.cs`、`BallRideExitButton.cs`、`RideViewMath.cs`、`RideExitMath.cs`；装配 `Editor/Story/RideUxAssembler.cs`（菜单 `VRC Katamari/Ride/Assemble Ride UX`，可重复执行） | 纯函数有 EditMode 测试 |
| 吸附宿主 | `Runtime/Pickup/PickupFieldHost.cs` | 一个字段区的吸附与复位宿主 |
| 剧情回合 | `Runtime/Story/StoryRoundController.cs` | 回合状态机与 HUD |

下文按模块分节；未经 Play/真机验证的项集中在文末「已知限制」。

## 输入与球视图（S1：计划 §1、§2）

### 两个脚本

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Input/DesktopMoveInput.cs` | 本地球唯一的设备读取点：把 `InputMoveVertical` 存成 forward、`InputMoveHorizontal` 存成 turn，任一变化就 `motor.SetMoveAxes(forward, turn, 0f)` | 不读速度/成长/吸附/回合状态；不写 Transform；不做摇杆死区或曲线（轴的原始值直接交给 motor，motor 自己 clamp 到 [-1,1]） |
| `Runtime/BallPresentation/BallView.cs` | 每帧读 `motor.GetSemanticDiameterWorldM()`，变化超过 1e-4 m 时写 `coreMesh.localScale = Vector3.one * diameter` | 不写 rotation（可视滚动在 motor 内部）、不写球根、不缩放附着根、不碰碰撞半径 |

两个都是 `UdonSharpBehaviour`，命名空间分别是 `VRCKatamari.Input` 与 `VRCKatamari.BallPresentation`，同步模式 `BehaviourSyncMode.None`，字段全部 `[SerializeField]`，没有 static、属性、struct、接口或每帧分配。

### 轴语义（代码证据）

- `SetMoveAxes(forward, turn, lateral)` 只写 motor 的三个 debug 轴（`KatamariBallMotor.cs:965-970`），`StepInput` 用 `(lateral, forward)` 组 `Vector2`，turn 单独进运动层（`:663-682`）。
- turn 的符号：`heading += turn * turnCap * dt`，前进方向取 `(+sin(heading), +cos(heading))`（`Runtime/Motion/BallMotionMath.cs:162-170`）。朝 +Z 时 heading 增大把前进方向转向 +X，**turn +1 = 右转**。
- 键盘映射沿用计划：W/S = ±forward，A/D = ±turn（计划 §1 写“A 负、D 正”），lateral 桌上暂无绑定，恒为 0。
- 尺寸读者是 `GetSemanticDiameterWorldM()`（`KatamariBallMotor.cs:1373`）；球心读者是 `GetCenterWorld()`（`:1348`），本切片没有脚本用它，日志由 lab 宿主自己打。

### 生命周期

生效输入从“开”变“关”的那一刻（任一闸门关上）、`OnStationExited`、`OnPlayerRespawn`、`OnDisable` 都会把两个轴清零并**再推一次零**，所以球会刹车而不是保持最后一次方向；重新打开闸门不推零（此时轴本来就是零）。副作用：桌面只在数值变化时发轴，若玩家在闸门关着时一直按住 W，重新打开后要先松开再按才会动。非有限值（NaN/Inf）在适配器就被丢掉：motor 对不合法帧的策略是“保留上一帧轴”，放进去会让那颗球永久冻结。

### 输入闸门（两个所有者，各写各的）

`DesktopMoveInput` 有两个互相独立的闸门，**每个只有一个所有者**：

| 闸门 | 所有者 | 关 | 开 | 默认 |
|---|---|---|---|---|
| `SetRideGate(bool)` | `BallRideStation` | `Start()`（站着的人不能遥控球）、`OnStationExited`、`OnPlayerRespawn` | `OnStationEntered`（本地球员） | 开 |
| `SetRoundGate(bool)` | `StoryRoundController` | 到点结算 `SettleRound()` | `BeginRound()` | 开 |

两者默认都是开，所以既没有 Station、也没有回合控制器的场景照常能开——无 Station、无回合控制器的无头键盘实验就是这种场景。

**为什么是两把而不是一把**：以前只有一个共用开关，两个所有者都写它，互相覆盖（最后一笔写赢）。实际后果：到点关掉输入后，离座再入座会被骑球宿主重新打开，回合结束后又能开球；反过来 `BeginRound` 也会把骑球宿主关上的门打开。拆成两把后，谁都不能改别人的门，生效输入 = 两者都开。任一关上时适配器把两轴清零并再推一次零（球刹车），重新打开不推零。

### 场景怎么接（Unity 操作者的活）

1. `MotorHost` 上只挂 `KatamariBallMotor`（独占 GameObject，项目惯例见 [BallMotor README](../BallMotor/README.md) 的「装配约束」；同物体本身合法）。
2. 层级：`BallPhysicsRoot`（motor 只写 position）→ `BallVisualRollRoot`（motor 只写 rotation，**localScale 恒 1**）→ `CoreMesh`。把 `ballPhysicsRoot`、`ballVisualRollRoot` 拖到 motor 上。
3. 新建一个空对象（例如 `DesktopInput`），**独占**挂 `DesktopMoveInput`，把 `motor` 拖进去。不要和 motor 同挂一个对象。
4. 新建一个空对象（例如 `BallViewHost`），**独占**挂 `BallView`，把 `motor` 与 `CoreMesh` 拖进去。
5. 相机架：`motor.followRigRoot` 接到相机跟随根；不接的话 motor 算出的跟随帧没有落点（计划 §1 已记录这一点）。
6. 地面：`AddPlane` 注册；起始位置在 `startCenterWorld`。

`CoreMesh` 的网格约定：**直径 1 m、pivot 在球心的球体**（例如 Unity 内置 Sphere），因为代码直接把它缩放到语义直径。换成直径不是 1 m 的网格时显示尺寸会差一个固定倍数，需要在美术侧改，而不是在这里加一个魔数。

## 骑球

> 本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §1、§7 S2 与 §8 的风险 1。

### 三个脚本

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Riding/BallRideStation.cs` | 与 VRCStation 同物体的骑乘宿主：`TrySeatLocalPlayer()` → `station.UseStation(localPlayer)`；本地球员入座/离座时开关 ride 闸门、球上点击球的提示、骑乘者面板；按住跳跃键 `exitHoldSeconds`（1.5 s）或面板按钮 → `RequestExit()`：先把 exit location 放到球旁可站立地面，再 `ExitStation`；`IsLocalPlayerSeated()` 给探针读 | 不移动 rig/球、不写 motor（只读球心、半径、朝向）、不抢账本/回合状态、不同步任何变量 |
| `Runtime/Riding/BallSeatInteract.cs` | 球上的点击球：挂 `ballPhysicsRoot` 下的单位球 trigger，按 `RideViewMath.SeatTriggerRadius` 随球缩放；`Interact` → `BallRideStation.TrySeatLocalPlayer()` | 不持有 seated 状态 |
| `Runtime/Riding/BallRideExitButton.cs` | 骑乘者面板上的下车按钮：点两次（3 s 内）→ `BallRideStation.RequestExit()` | 不持有 seated 状态、不碰 motor；状态复位由 `BallRideStation.OnStationExited` 一处负责 |
| `Labs/StationPlatform/SeatedAxisProbe.cs` | 只为回答计划风险 1 的只读探针：每个 `InputMoveVertical`/`InputMoveHorizontal`/`InputJump` 打一行 `[seated-axis]`，带 `seated=` | 不是产品代码，没有任何别的文件引用它；答案写进本文后连文件、`.meta`、组件一起删 |

三个都 `BehaviourSyncMode.None`，没有 static、属性、struct、接口、泛型、LINQ 或每帧分配。

### 必需的物体布局（少一条就不成立）

1. **FollowRig** 必须是 motor 的 `followRigRoot`（`KatamariBallMotor.cs:160`；每帧只写它的 `position` 与 yaw，`:1026-1032`）。不接的话 rig 帧没有落点，载人整段不生效。
2. **rig 根就是取景眼点**：`followRigRoot` 收到的是 `eye = 球心 − forward·back + up·height`，`height = 1.900108·r`、`back = 1.041795·r`（无加性底座）（`RideViewMath` 推出，`RideUxAssembler` 写进 motor 的四个眼点字段；站姿推导见 [RIDE_GEOMETRY](../BallLayers/FollowRig/RIDE_GEOMETRY.md)）：玩家站在球后、手按在球背上。**不要再把 rig 根调成贴球心。**
3. **VRCStation + `BallRideStation` 同物体**：`OnStationEntered`/`OnStationExited` 送到 VRCStation 所在物体的 Behaviour。座位上**没有**点击 Collider（座位在眼点上，不在球上，点它等于点空中）；点击由球上的 `BallSeatInteract` 承担（第 6 条）。
4. **Station 是 rig 根的子物体**，`localPosition` 起始 `(0, 0, 0)`。「球后上方」已经由 rig 根本身给出（0.5 m 球时后方 1.85 m、上方 1.75 m），**不要再叠进 Station 的局部偏移**；坐着的人由 Station 承载，跟随靠 motor 写 rig 根，不靠任何脚本搬人。坐姿视点补偿（VRCStation 把人的眼睛放在座位上方的某个高度，本仓未实测）在首次 Build & Test 里量，本文不编数。
5. **VRCStation 保持 Disable Station Exit 开**：移动轴驱球，跳跃是物品/退出输入，若允许移动或一次跳跃就离座，一次误触就结束骑乘（T04 实测：关掉退出后 Jump 不退座，且 `InputJump` 仍送达）。下车改为有意识的两条路：按住跳跃键 1.5 s，或面板上的按钮点两次。场景里 Player Mobility 是 `1`（Immobilize）；实验场景是 `0`，**Immobilize 下 `InputJump` 是否仍送达没有实测**，是真实客户端验收项。
6. **点击目标在球上**：`BallPhysicsRoot/BallSeatTrigger`（单位球 `SphereCollider`、trigger、`BallSeatInteract`），半径 = `1.25 ×` 碰撞半径、下限 0.25 m，由 `BallSeatInteract.Update` 写 scale，所以随球径变；交互提示「骑上去」，距离 4 m。
7. **`DesktopInput` 物体**：独占 `DesktopMoveInput`，把 `BallRideStation.input` 指过去。项目惯例是分开挂：motor、Station、输入各占一个对象，职责和自检都清楚（同物体本身合法，见 [BallMotor README](../BallMotor/README.md) 的「装配约束」）。
8. **骑乘者面板**（`FollowRigRoot/RidePanels` 下的世界空间 Canvas，`RidePanels` 随骑手眼高按 E/1.64 缩放；不用屏幕叠加：头显里不画 Screen Space Overlay）：`StoryHud`（左下，球的左边）、`RideResultCard`（正前，结算卡 Prefab 实例，结构见 [HUB_PANEL_PREFAB](<HUB_PANEL_PREFAB.md>)「结算卡」：结果文字是 TMP，两颗 uGUI Button「再来一次」「回大厅」）、`RideExitPanel`（右下，含 `BallRideExitButton` 与按住跳跃提示，仅入座时显示）。位置规则见 RIDE_GEOMETRY §6；骑乘中鼠标左键仍可交互。下车时 `RideExitMath` 现算落点（球旁 0.9 m 的可站立地面）。

### 骑乘 UX 装配（可重复执行）

菜单 `VRC Katamari/Ride/Assemble Ride UX`（`Editor/Story/RideUxAssembler.cs`）对打开的场景做：写 motor 的四个眼点系数与两个上限；建 `PlayerBall/BallPhysicsRoot/BallSeatTrigger`；删座位上的点击 Collider；把 VRCStation 设为站立式；建 `FollowRigRoot/RidePanels`，把 `StoryHud` 改成世界空间并挂到它下面；在 `RidePanels` 下放一个结算卡 Prefab 实例 `RideResultCard.prefab`（正前、上 6°、缩放 0.001，初始隐藏），写 `StoryResultPresenter.resultCardRoot`/`resultText`、`StoryRoundController.retryButtonRoot`（卡的 `Buttons` 行）与两颗按钮的场景引用，并删掉旧的 `HubResultBoard`、`HubReturnCardButton` 和卡外的 `StoryRetryButton` 物体；建 `RidePanels/RideExitPanel`（`BallRideExitButton` 移进来）；写 `BallRideStation` 的 `motor`/`exitPoint`/`ballInteract`/`exitPanelRoot`/`exitHintText`/`panelRoot`/`vrInput` 与骑手缩放参数。`Assemble All-Maps Hub` 在大厅装配之后自动跑这一步。结果行以 `[ride-ux] done: +N ~M problems=K` 开头；第二次运行应为 `+0 ~0`。

前置与已知坑：`BallSeatInteract` 是新行为，先让 Unity 为它生成 U# program asset（选中 `BallSeatInteract.cs`，Inspector 的 Create 按钮，或先用 `Assets/Create/U# Script` 同名流程；资产连 `.meta` 一起提交）；给**已存在**的行为（`BallRideStation`、`BallRideExitButton`）新增了字段，旧实例的变量表里没有，装配器读回会报「Udon variable table lacks …」，按 `Labs/README.md`：编译 → 存盘 → 重开场景 → 再跑一次装配。

### 驱动门：谁能用 WASD

`DesktopMoveInput` 只认设备，谁按都转成球轴，所以“只有坐着才能开”必须由骑乘宿主把关：`Start()` 先把自己那把闸门关上（站着的人不能遥控球），`OnStationEntered`（本地球员）打开，`OnStationExited` 再关上。闸门是 `DesktopMoveInput.SetRideGate(bool)`（`Runtime/Input/DesktopMoveInput.cs:77`），关的时候适配器会把两轴清零并再推一次零，球会刹车。

`OnPlayerRespawn` 也清一次：复活可能把人留在座位里而不发 `OnStationExited`，那样交互会永久禁用、再也坐不上去。实验的 driver 同样这么做（`StationLabDriver.OnPlayerRespawn`），这里只是照抄这条保险。

**与回合控制器的分工**：`StoryRoundController` 不再共用这把开关，它只关自己的 `SetRoundGate(bool)`（`Runtime/Story/StoryRoundController.cs` 到点时调 `false`）。两把闸门互不影响，所以到点后再入座也不会把输入重新打开；细节见 S1 的“输入闸门”。

### 5 分钟 Build & Test 探针（计划风险 1）

要回答的只有一句：**坐着的时候 VRChat 还把 `InputMove*` 发给 Udon 吗？**

1. 在骑球场景加一个空物体（例如 `SeatAxisProbe`），**独占**挂 `SeatedAxisProbe`，把 `ride` 拖成那个 `BallRideStation`。别的都不用接线。
2. 用户在 Editor 里点 Build & Test，桌面客户端进世界。
3. 准星对着球按**鼠标左键**入座（桌面交互是左键，E 无效）；日志应出现 `[ride] seated entered`。
4. 坐着别动退出：按住 W 约 2 秒，松开；按 S 约 2 秒；再按 A、D 各约 2 秒。
5. 读日志：客户端日志 `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt`（T04 两轮读的就是这个路径），搜 `[seated-axis]`；若你的客户端把 Udon 的 `Debug.Log` 显示在界面里的 Log 面板，也可以直接看。判定：
   - 出现 `seated=True` 且 `value` 非 0 的行 → 轴在坐姿下仍到达，风险 1 不存在，“坐着开”成立。顺手点退出按钮，确认 `[ride] seated exited` 且下球后能走。
   - 只有 `seated=False` 的行 → 坐姿下轴不来。先做一步排查：把 Player Mobility 改成 Mobile 再测一遍，用来区分“坐姿不送轴”和“移动被禁所以不送轴”；仍然没有就照风险 1 记失败并考虑回退方案。
   - 连 `[seated-axis] probe ready` 都没有 → 探针没挂上/脚本没进构建，不是轴的问题。

探针的日志量有上限：值变化各打一行，另外每个轴在非零期间最多每 0.25 s 一行（桌面键盘只发 −1/0/1，实际等于每次按键一行），所以五分钟的测试不会把日志刷爆。

## 吸附宿主

> 本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §3、§7 S3 与 §8 的风险 4。

### 一个脚本管全部道具

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Pickup/PickupFieldHost.cs` | 一个字段区的唯一吸附宿主：按并行数组持有一组道具，把激活半径内的道具交给 motor 的候选队列，再把 motor 报告已吸收的道具 `SetParent` 到球的附着根，让它留在球上 | 不改 motor、不写球的位姿、不缩放附着根或道具、不算分/不算回合、不读账本、不持有视觉 |

为什么不是每件道具一个 `Update`：计划 §3 写明灰盒规模是 448 件（`Labs/PicnicLawnGreybox/GreyboxReport.json` 的 `props_placed`），而 motor 的候选队列只有 4 格（`KatamariBallMotor.cs:1015-1019`），且**永远不会移除没被吸收的候选**——所以既不能每件道具每帧扫一遍事件环，也不能让道具各自排队。

### 序列化数据（一行一件道具，索引对齐）

| 字段 | 含义 | 注意 |
|---|---|---|
| `propTransforms[]` | 道具自身 Transform | 吸收位姿取它**当前**的世界位姿 |
| `itemIds[]` | 账本/候选用的 item id | 空 id 的行永远不提交（motor 直接拒，`KatamariBallMotor.cs:1004`） |
| `kinds[]` | 定义类别（s/m 档等） | 必须包含在 motor 的 `allowedKinds` 里，否则永远 `kindNotAllowed`（见“场景怎么接”第 4 条） |
| `propBounds[]` | 全尺寸 AABB（米，非半尺寸） | 负值/非有限值不提交 |
| `valueUnits[]` | 账本整数单位 | **必须是 ≥1 的整数**（`Docs/OPEN_DECISIONS.md` (j)）；<1 的行宿主不提交，并在启动时用一行日志统计 |
| `propColliders[]` | 可选，吸收时关掉 | 可以留空 |
| `activationRadiusM` / `scanIntervalSeconds` | 1.6 / 0.05 起 | 激活半径是球心到物件中心的距离，候选选择只看它之内的物件 |

每件道具的运行期状态只有三种：`0` 自由、`1` 已附着、`2` 已隐藏（附着失败的后备）。`1`/`2` 都是终态，永不再提交——和 `PlaygroundProp` 的 `consumed` 同一语义，只是从“每件一个 Bool”变成“宿主一个 int 数组”。

### 每帧做什么

1. **先消费事件环**（无论是否在收集）：用自己的绝对游标从上次读数读到 `GetAbsorbedEventCount()`。环容量 16、按绝对下标读（`KatamariBallMotor.cs:1523-1531`、`:1542-1545`、`:1567-1574`）；游标落后超过容量时**明确记一行**丢了多少事件（本次读数与上次读数的差 > 容量即溢出），而不是假装没丢。
2. **再每 0.05 s 扫一次邻近**：一次遍历里，`activationRadiusM` 内的自由道具先过尺寸门（`PickupAbsorbMath.IsBeyondAbsorbLimit`，用电机当前语义直径；吸不了的大件不进队列，仍由挡路路径当墙），过了的按**距离从近到远**插入一个 8 格缓冲，取前 `GetPendingCandidateCapacity()`（4）件；随后先撤回本宿主已入队但不在选择里的（`WithdrawCandidateUnlessAccepted`），再在 `GetPendingCandidateCount() < GetPendingCandidateCapacity()` 时提交选中的。最近优先不是微优化：先到先得会把 4 格给数组下标最小的 4 件，球正碰上的那件反而排不进去；而往满队列里提交会被拒并**latch motor 的数据丢失信号**（`KatamariBallMotor.cs:1015-1019`），那是“宿主丢了一个候选”的告警，宿主不该自己造成它。选择逻辑见 [PickupAbsorb README](../PickupAbsorb/README.md) 第 6 节第 9 条。
3. 宿主只记“我提交过哪些行”（`queuedIndices[]`，容量 16）这一份撤回清单：它只决定“撤回谁”，不参与“要不要提交”（提交幂等，直接调）；电机已不持有的行（已吸收、被电机清空）下次扫描自行脱落。队列的真相由 motor 持有。

### 吸收之后怎么贴上去

1. 事件只用来拿 **item id**，位姿取道具**当前**的世界位姿（`position` / `rotation` / `lossyScale`）。不用 `GetAbsorbedEventPosition`：那一格是**提交时**的快照（`pendingPositions` 在 `KatamariBallMotor.cs:1024` 存、`:773` 原样记进事件），球快滚时能差几十厘米（计划 §3、§8 风险 4）。
2. `AttachmentMath.TryWorldPoseToLocal(球心, 视觉滚动四元数, 道具世界位置, 道具世界旋转, 道具 scale, out 局部位置, out 局部旋转)`（`Runtime/BallLayers/Attachment/AttachmentMath.cs:90-113`）算出球刚体坐标系下的局部位姿。球心用 `GetCenterWorld()`（`KatamariBallMotor.cs:1348-1351`），旋转用 `GetVisualRollRotation()`（`:1418-1421`）——真正把道具带着滚的就是它。
3. `SetParent(attachmentRoot)`，然后写 `localPosition` / `localRotation`；**不写 `localScale`**：附着物保留自己的 scale，不随球芯放大（`AttachmentMath.cs:18-33` 的约定；`Docs/02_TECHNICAL_CONTRACTS.md` §2 要求滚动根 scale 恒 1）。
4. 关掉这件道具的 collider（如果接了），标 `attached`，计入按 kind 的计数，并打一行 `[PickupFieldHost] attached ...`。

`TryWorldPoseToLocal` 拒绝时（非有限值、零/非法四元数、scale 有非正分量）：**隐藏**这件道具（`SetActive(false)` + 关 collider）并记一行带原因的警告。这是后备路径，不是常规路径：被吸收却贴不上的道具如果留在世界里，就是“吃掉了还在那站着”。原因码与数量由 `GetHiddenCount()` / `GetLastHiddenReason()` 读，控制台行数上限 8 行。

### 对 S4 / 第二关的公开接口

| 方法 | 用途 |
|---|---|
| `SetCollecting(bool)` | 回合控制器到点时调 `false`：停止提交，并把自己提交过的候选全部撤回（只撤自己的 id；`ClearCandidates` 会连别的宿主在这个 motor 上的候选一起清掉）。已附着的道具保持附着 |
| `GetAttachedCount()` | 附着总数 |
| `GetAttachedCountForKind(string)` | 按 kind 的附着数，第二关“收集 N 个类别”直接读它，不再写第二套计数 |
| `GetHiddenCount()` / `GetLastHiddenReason()` / `GetLastHiddenItemId()` | 后备路径的证据 |
| `GetPropCount()` / `IsCollecting()` | HUD 与测试读数 |

S4 的 `Runtime/Story/StoryRoundController.cs` 已经在按这些方法接线（`:204` 开、`:297`/`:298` 关，`:365` 总数、`:367` 按 kind），签名与本表一致；它 import 的 `VRCKatamari.Pickup` 也就是本脚本的命名空间。

### 场景怎么接（Unity 操作者）

1. **`AttachmentRoot`**：`BallVisualRollRoot` 的子物体，`localPosition = 0`、`localScale = 1`、旋转不写。这样它的世界位姿就是球的刚体位姿（球心 + 视觉滚动），也就是 `TryWorldPoseToLocal` 用的那个坐标系。放错位置时宿主会在第一次附着时警告“离球心 X 米”。
2. **宿主独立物体**：项目惯例是一个 GameObject 只挂一个 UdonSharpBehaviour（`Docs/Design/BallMotor/README.md` 的「装配约束」；同物体本身合法），所以 `PickupFieldHost` 不要和 motor 同物体。
3. **数组按索引对齐**：一串 `Transform`、一串 id、一串 kind……长度不一致时宿主取最短的那段，并在启动日志里报“忽略了尾部 N 条”。
4. **`allowedKinds` 必须包含所有 kind**：不包含的 kind 会被 gate 拒（`Runtime/Pickup/PickupAbsorbMath.cs:141`），而拒掉的候选**留在队列里**，4 格会被同 kind 的道具占死。这是接线要求，宿主兜不住。
5. **道具不要带动态 Rigidbody**：motor 不做物理，球只是运动学扫掠；被 `SetParent` 到球上的动态刚体会自己掉下去。切片里道具是静态物体。
6. `valueUnits` 按整数填（`Docs/OPEN_DECISIONS.md` (j)、计划 §3 的“两个数据坑”）；`bounds` 是**全尺寸**，不是半尺寸。

### 代码证据（宿主调用的全部 API）

| 调用 | 位置 |
|---|---|
| `motor.SubmitCandidate(absorbable, kind, bounds, worldPosition, valueUnits, itemInstanceId, itemGeneration)`，按 id 幂等、满队列拒绝 | `Runtime/BallMotor/KatamariBallMotor.cs:1001-1032` |
| `motor.WithdrawCandidate(itemId)` | `:1050-1080` |
| `motor.GetPendingCandidateCount()` / `GetPendingCandidateCapacity()` | `:1438-1441` / `:1463-1466` |
| `motor.GetCenterWorld()` | `:1348-1351` |
| `motor.GetVisualRollRotation()` | `:1418-1421` |
| `motor.GetAbsorbedEventCount()` / `GetAbsorbedEventCapacity()` | `:1537-1540` / `:1542-1545` |
| `motor.GetAbsorbedEventItemId(absoluteIndex)`（越界/已被挤出返回 `""`） | `:1567-1574`、`:1894-1904` |
| 事件只在提交成功分支写入；diameter = bounds 最大边 | `:766-776`、`:1523-1531` |
| 环容量 16 | `:280` |
| `AttachmentMath.TryWorldPoseToLocal(ballPosition, ballRotation, objectPosition, objectRotation, objectScale, out localPosition, out localRotation)` | `Runtime/BallLayers/Attachment/AttachmentMath.cs:90-113` |
| 对照：lab 的每件道具宿主“查到自己的 id 就隐藏” | `Labs/CpuSkirmish/PlaygroundProp.cs:144-169` |
| 对照：lab 的激活/释放半径（1.6 / 1.0，释放更小） | `PlaygroundProp.cs:50`、`:57` |

lab 那对半径是**反的**（激活 1.6、释放 1.0，等于排队当帧就撤回）。本宿主没有释放半径：候选队列跟着每次扫描的选择走，离开激活半径、被更近的挤掉或过不了尺寸门的候选都在下一次扫描被撤回。

## 剧情回合（S4）

> 本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §4、§7 S4。

### 一个脚本

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Story/StoryRoundController.cs` | 一局剧情的状态机与 HUD：`Idle → Running → Presenting → Result`；到点冻结输入/收集/候选，并冻结结算瞬间的碰撞直径或确认件数（星与评语由 `StoryResultPresenter` 写进结算卡） | 不交分、不写账本（`bank` / `score_channel` / `ledger_write` 都不碰）、不改球径、不评过程、不重试（重试走 `RequestRetry()`，见「再试一次」） |

同一目录的 `StoryRoundMath.cs` 是纯规则（星级查表、倒计时、partial band 判据与 `StoryGoalType`），不读时钟/场景/motor，便于 EditMode 测试。

### 状态与冻结顺序

`Time.time` 记起点；Running 每帧写 HUD（目标/当前/剩余）；到点先关 `input.SetRoundGate(false)`，再 `pickupHost.SetCollecting(false)`，再 `motor.ClearCandidates()`，然后才读结算数值。这个顺序是计划 §4 的：球先刹车、宿主停止排队、在飞的候选全部丢掉，之后读到的数就是铃响那一刻的数。

### 关卡数据与评语

第一关的数字是场景实例上的序列化字段（运行时**不读磁盘 JSON**）：`levelId`、目标直径/件数、两档星、限时、`partialBand`、`countGoalKind`，每个字段的注释写着它对应 `Docs/Design/GameModes/Levels/story_01_bench_size.json` 的哪个键。五句评语（3★ / 2★ / 1★ /“就差一点点”/ 失败）也是序列化字符串字段，默认值是第一关的文案；出处是 GameModes README §3.1「评语（结算）」与 §2.4 的 partial band 分支。换关只换场景实例上的值，不改代码。

### 场景怎么接（Unity 操作者）

1. 独立 GameObject 挂 `StoryRoundController`；项目惯例一物体一个行为（同物体本身合法，见 BallMotor README 的「装配约束」）。
2. 拖入 `motor`、`input`、`pickupHost`；任一为空时那一步跳过（球继续滚，不会抛异常）。
3. 四个 uGUI `Text`：目标、当前、剩余、结果；不接的少一行 HUD。
4. `presentingSeconds` 是“呈给国王”的停留时长，≤0 直接出结果卡。

## 再试一次（S6 剩余的一半：结果卡重试）

> 本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §4「没有重试接缝」与 §7 S6 的后半段；motor 的重置接缝（`TryResetForNewRound`）见 [BallMotor README](<../BallMotor/README.md>) 的「重试（reset）」一节，本节只接调用方与道具复位。

### 三个文件

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Story/StoryRoundController.cs`（改） | `RequestRetry()`：状态闸门 → motor 复位 → 道具复位 → `BeginRound()`；重试次数写进 HUD 目标行 | 不交分/不奖励（不调 `TryBank`/`TryAwardScore`）、不写账本规则、不改球径与位姿（全交给 motor 的接缝）、不自己搬道具（交给宿主） |
| `Runtime/Pickup/PickupFieldHost.cs`（改） | `Start` 记录每件道具的父物体/局部位姿/激活态；`ResetField()` 把它们放回去、重新可收集 | 不重置 motor、不碰回合闸门、不回退事件游标、不恢复道具 `localScale` |
| `Runtime/Story/StoryRetryButton.cs` | 结算卡「再来一次」的 uGUI 入口 `Press()`（`Button.onClick` → `SendCustomEvent("Press")`）：有 `HubLevelLauncher` 时调 `launcher.PressRetryCurrentLevel()`，单关场景直接调 `controller.RequestRetry()` | 不持有回合状态、不判断能不能重试、不隐藏/显示自己 |

三个都是 `UdonSharpBehaviour`，同步模式 `BehaviourSyncMode.None`，没有 static 字段、属性、struct、泛型、List/LINQ、try/catch、`??`、字符串插值或方法重载。

### 状态闸门：只在结果卡上允许

`RequestRetry()` 只在 `StateResult` 接受，其他状态一律拒绝并打一行 warning（`retry ignored: no result card is showing (state=...)`），结果卡与回合都不动。

- `Presenting` **刻意拒绝**：那时结算卡只显示「呈给国王」，按钮行还没打开；若允许，重置会静默跳过这段停留，玩家看到的还是呈递文本。
- `Idle` / `Running` 拒绝：没有结果可重试。
- 拒绝不改状态、不写 HUD：`state` 保持 `StateResult`，`resultText` 保持原样，所以“按钮没反应”只会出现在控制台日志里，不会出现半张卡。

按钮的显隐由控制器按状态写（见「结果卡显隐」），规则本身仍是 `RequestRetry()` 的状态闸门，它挡住误按。

### 顺序与失败处理

1. `motor.TryResetForNewRound(newRoundId, startPosition, startHeadingDegrees, out reason)`。这是唯一有失败码的一步：账本行、`roundId`、候选、事件环、速度、朝向、三档尺寸、位姿全由它复位（[BallMotor README](../BallMotor/README.md) 的「重试（reset）」）。
2. 若 motor 拒绝（1..7，例如 `noLocalRow`）：`Debug.LogWarning` 打出 `reason` 与 `GetLastResetLedgerFailure()`，**保留结果卡**并返回 false。此时道具与计时都没动。
3. `pickupHost.ResetField()`。返回 false 只表示“宿主还没 `Start` 或没有道具”，不阻止重开；打一行普通日志后继续。
4. `BeginRound()`：`Time.time` 重新记起点、状态回 Running、结果卡清空、`SetRoundGate(true)`、`SetCollecting(true)`、目标/当前/剩余三行重写。

“先 motor、再道具、最后计时”是有意的：任何一步失败都不该让玩家看到一个“新回合”的 HUD 配上一颗旧球。

### 回合 id 与重试次数

`newRoundId = levelId + ":" + 次数`，次数从 1 开始，每次成功重试加一（`retryAttempt` 字段；首局为 0）。构造前先读 `motor.GetRoundId()`（`Runtime/BallMotor/KatamariBallMotor.cs:1134`），如果碰巧相同就继续加，最多 8 次（`RetryRoundIdGuard`）；motor 对相同 id 返回 `roundIdUnchanged`（4），这一步是防呆而不是常规路径。

次数只在实际重开后写入；被 motor 拒绝的那一次不消耗编号。第 N 次重试的 HUD 目标行是 `目标 1.25 m（第 N 次重试）`（件数关卡同样加在末尾），`GetAttemptNumber()` 也可读。选择写在目标行而不是结果卡：`BeginRound()` 会清空结果卡，而目标行每局都会重写，是唯一在重开后仍然显示的地方。

### 起点姿态：用 motor 的 getter，不在场景里另存

- 球心：`KatamariBallMotor.GetCenterWorld()`（`Runtime/BallMotor/KatamariBallMotor.cs:1523`）。
- 朝向：`KatamariBallMotor.GetHeadingDegrees()`（`:1528`）——**存在**，所以没有退回“序列化开始朝向”。

捕获**一次**：`Start` 里读一次，存进 `retryStartPosition` / `retryStartHeadingDegrees`，之后重试都复用，不随球移动更新。

**Start 顺序的坑**：两个不同物体上的 `Start` 顺序 Unity 不保证。如果控制器的 `Start` 先跑、motor 的 `Start` 还没跑，`GetCenterWorld()` 读到的就是 `(0,0,0)`（有限值，看起来像合法姿态），重试会把球送回世界原点。所以捕获用 motor 的基线直径做就绪判据：直径 ≤ 0 时（BallMotor README 用它判断“`Start` 未跑”）不在 `Start` 里落盘，而是在第一个 `Update` 里、motor 已有基线直径时再捕获。第一帧玩家还没推轴，球实际上没动；最坏误差是“控制器 `Start` 晚于球被移动”的异常装配，不在本切片。

### PickupFieldHost：记录与复位

`Start` 在 `AllocatePropStore()` 之后、任何附着之前调 `RecordOriginalPoses()`，按道具索引记录四个并行数组：`parent`、`localPosition`、`localRotation`、`gameObject.activeSelf`。空 Transform 的行跳过，数组保持默认值。

`ResetField()` 的语义：

- 先把本宿主提交过、还在队列里的 id 撤回（`WithdrawSubmitted()`，无条件 `WithdrawCandidate`）；调用约定是 motor 重置已接受、motor 自己已清空候选，所以这里多为无害的 no-op。
- 只动状态为 `1`（已附着）或 `2`（已隐藏）的道具：`SetParent(original)` → 写 `localPosition`/`localRotation`（与 attach 同一顺序）→ `SetActive(原始激活态)`。隐藏的道具因此重新出现；原本激活的道具保持激活。
- 接了 collider 的行无条件 `enabled = true`。attach/hide 只关不开，所以复位必须开；代价是“authoring 时就关着的 collider”会被复位打开，没有为它再存一个标志。
- 状态回 `0`（自由），撤回清单清空（motor 已清队列，提交记录没有意义）。
- `attachedCount`、`hiddenCount`、`lastHiddenReason`、`lastHiddenItemId`、`kindTallyCount` 归零；`nextScanTime = 0` 让下一帧立刻重新扫描。
- **不回退** `seenEvents`：motor 的绝对吸收计数跨重置单调（`absorbedEventCount` 只增不减，[BallMotor README](../BallMotor/README.md) 的「吸收事件环的绝对计数保持单调」），保留游标就不会重放旧事件，新回合的事件从同一绝对号继续。重置后 `absorbedEventRingCount = 0`，旧绝对下标一律返回 `""`，宿主跳过即可。
- 保留 `loggedLostEvents`、`hiddenLogCount` 这类整会话诊断闩锁，理由与 motor 保留日志上限相同：无限重试不能把控制台刷爆。
- 返回 `bool`：`propStates == null`（`Start` 未跑）时 false 且什么都没碰；空场地也算成功（true）。
- 不记录、不恢复 `localScale`：attach 从不写 scale，`SetParent` 以“保持世界缩放”方式改 `localScale`，回到原父物体即恢复原外观。
- 不碰 `SetCollecting` 闸门：`BeginRound()` 负责重新打开。

### StoryRetryButton：一个入口，卡上的 uGUI Button

结算卡 Prefab 的 `Buttons/RetryButton` 是 uGUI `Button` + `StoryRetryButton`。`Button` 的 `On Click ()` 只有一条：Runtime Only，目标 = 本物体的 backing `UdonBehaviour` 组件，函数 `UdonBehaviour.SendCustomEvent (string)`，参数 `Press`；按钮没有自己的 Collider，不走 `Interact`。官方 UI 事件白名单把 `UdonBehaviour` 的 `SendCustomEvent` 列为可调用目标（<https://creators.vrchat.com/worlds/udon/ui-events/>，2026-11-14 抓取，页面最后更新 2026-10-07）。

`Press()` 是 `public void`（`SendCustomEvent` 按字符串分发，不能是属性或带参方法）。它不判断状态：有 `HubLevelLauncher` 时走 `launcher.PressRetryCurrentLevel()`（与自动返回、退座抢座共用同一条链），没有大厅的 lab 才直调 `controller.RequestRetry()`；能不能重开仍由 `RequestRetry()` 的状态闸门决定（下一节）。

接线由装配器回读：`RideUxAssembler.ReadResultCard` 检查两颗卡按钮的 onClick 恰好一条 `SendCustomEvent("Press")` 指向自己的 `UdonBehaviour`、没有 Collider、Navigation 为 None、Image 是射线目标、按钮字等于 `StoryRetryButton.Label`。**本仓未在 Unity/真机里按过这张卡**，依据是官方白名单与装配器回读。

### 场景怎么接（Unity 操作者）

1. 结算卡由 `RideUxAssembler` 在 `FollowRigRoot/RidePanels` 下实例化 `RideResultCard.prefab`；结构、尺寸与按钮 onClick 都属于 Prefab，场景里不再手工建 Canvas 或触发盒。
2. 装配器写 `StoryRetryButton.controller`（场景唯一的 `StoryRoundController`）与 `launcher`（场景有 `HubLevelLauncher` 时）；没有大厅的场景 `launcher` 留空，按钮直调 `RequestRetry()`。
3. `Buttons` 行是回合控制器的 `retryButtonRoot`，只在 Result 打开；装配器按名字 `Buttons` 找到它。
4. 不需要改 `StoryRoundController` 的既有字段：`motor`、`input`、`pickupHost`、四个 Text、关卡数据与评语全部保持原名（只有私有字段与方法，没有序列化字段）。
5. 道具行照旧：`propTransforms` 等并行数组就是复位用的原始来源，宿主自己抓，不需要额外数组。

## 开始流程：Idle → 入座 → Running

> 本节说明回合如何由“坐上球”开始：加载后停在 Idle，世界里的开始动作是左键点球入座，站着的玩家按 WASD 不驱球。

### 问题与改法

旧版 `StoryRoundController.Start()` 直接 `BeginRound()`：没有门厅、没有开始动作，进世界即计时；场景里没有 Station 时 `DesktopMoveInput` 的两把闸门都是默认开，所以站着的人也能开球。改法是把“开始”交给坐上球这一个动作：

1. `Start()` 只在 `startOnLoad = true`（无 Station 的无头实验）时立刻开局；默认 `false` 时进入 **Idle**：HUD 写目标行与提示行，回合闸门关、收集关，计时不动。
2. 玩家准星对着球按**鼠标左键**（桌面交互是左键；VR 扳机）→ `BallSeatInteract.Interact()` → `BallRideStation.TrySeatLocalPlayer()` → `station.UseStation(localPlayer)`。
3. `OnStationEntered`（本地球员）→ 先开骑乘闸门，再调 `roundController.NotifyRiderSeated()`（站上 `roundController` 字段有接线才调）。
4. `NotifyRiderSeated()` 只在 `Idle` 接受：`BeginRound()` 开回合闸门与收集、记 `Time.time` 起点 → **Running**，这时两把闸门都开，WASD 才驱球。
5. 到点 `SettleRound()`：先关回合闸门、再关收集、再清候选，读冻结值 → **Presenting**（“呈给国王”）→ **Result**（星/测量值/评语）。
6. 结果卡上「再来一次」走 `StoryRetryButton.Press`：有大厅时经 `launcher.PressRetryCurrentLevel()`，单关直调 `RequestRetry()`（见上一节；重试后仍在座位上，球回起点）。下车是 `BallRideStation.RequestExit()`（按住跳跃键，或面板按钮点两次）→ 离座 → 骑乘闸门关。

### 每状态的闸门（生效输入 = 两把都开）

| 回合状态 | 回合闸门 `SetRoundGate` | 骑乘闸门 `SetRideGate` | 收集 `SetCollecting` | 剩余行 / 提示行 / 结算卡 |
|---|---|---|---|---|
| Idle | 关（`Start()`，`startOnLoad=false`） | 关（Station 的 `Start()`；本地球员入座时开） | 关（`Start()`） | 剩余行清空；提示行（`startPromptText`）显示 `startPrompt`（默认「点击球坐上去开始」）；结算卡隐藏 |
| Running | 开（`BeginRound()`） | 入座开、离座关 | 开（`BeginRound()`） | 「剩余 N s」；提示行关；结算卡隐藏 |
| Presenting | 关（`SettleRound()`） | 同上 | 关（`SettleRound()`） | 「剩余 0 s」；提示行关；结算卡显示「呈给国王」 |
| Result | 关 | 同上 | 关 | 提示行关；结算卡显示星/测量值/评语 |

所以：Idle 里谁都开不动球（回合闸门关，而且站着时骑乘闸门也关）；Running 里站着的人开不动球（只剩骑乘闸门把关）；到点后无论坐着还是站着都不会再吸收。

`SetRoundGate` 的“关”有两处：`SettleRound()` 与 `startOnLoad=false` 时的 `Start()`；“每个闸门只有一个所有者、默认开、互不覆盖”（见「输入闸门」）不受影响。

### 已决定的边界

- **中途离座：回合照常跑。** 离开是玩家的选择，暂停会把计时变成玩家可随意停的资源。输入本来就被骑乘闸门关掉（适配器清零两轴、球刹车），所以离座后计时继续、球不走，到点照样出结果卡。再坐回去只是重新开骑乘闸门（`NotifyRiderSeated()` 在 Running 被忽略），不会重开回合。
- **`NotifyRiderSeated()` 在 Result：什么也不做。** 重试只能按结果卡上的按钮：`BeginRound()` 单独跑不会复位球、账本行、回合 id 和道具，坐下就重开等于“新回合配旧球”；`RequestRetry()` 才是先 motor 复位、再道具复位、最后 `BeginRound()` 的那条路。Running/Presenting 同样忽略，否则坐下会静默重开计时。
- **`startOnLoad = true`** 保留旧行为，给没有 Station、没有人可坐的无头/键盘实验；有 Station 的场景必须保持 `false`。
- **站不接 `roundController` 时**照样能坐、能开关骑乘闸门、能显示提示（StationPlatform 实验就是这样）；但场景里有 `StoryRoundController` 就必须把站上的 `roundController` 指过去，否则控制器永远停在 Idle、回合闸门一直关，表现为“坐上去也开不动”。
- 提示行有**自己的一条 Text**（`startPromptText`），只在 Idle 显示；没接这条提示行时退回**剩余行**，并把剩余行清空以免显示一条没开始的倒计时。结果文字（`StoryResultPresenter.resultText`，卡上的 TMP）不再承载提示，只放「呈给国王」与结算内容——它随结果卡一起在 Idle 隐藏。

### 场景怎么接（Unity 操作者）

1. `BallRideStation.roundController` 指向场景里的 `StoryRoundController`；不接 = 见上一条。
2. 有 Station 的可玩场景：`StoryRoundController.startOnLoad` 保持默认 `false`。无头实验勾上。
3. HUD 四行（目标/当前/剩余/结果）之外多一条**提示行** `startPromptText`，只在 Idle 显示（见下节「结果卡显隐」）；`startPrompt` 想换文案就在场景实例上改这个字符串，不用改代码。
4. 相关序列化字段与默认值：`StoryRoundController.startOnLoad`（默认 `false`）、`startPrompt`（默认文案）、`BallRideStation.roundController`，以及「结果卡显隐」一节的 `StoryRoundController.retryButtonRoot` / `startPromptText` 与 `StoryResultPresenter.resultCardRoot` / `resultText`（默认 `null`，`null` 一律跳过）。

## 结果卡显隐：按钮只在 Result，卡片从 Presenting 开始

> 本节说明结算卡与按钮的显隐规则：Idle 不显示按钮，Presenting 起显示卡面，Result 起显示按钮。卡根与结果文字归 `StoryResultPresenter`，`Buttons` 行与提示行归 `StoryRoundController`；两个行为各写各的对象。

### 四条场景引用（卡三条 + 提示行一条）

| 字段（所在行为） | 指向什么 | 打开 / 写入于 | 关闭 / 清空于 |
|---|---|---|---|
| `StoryRoundController.retryButtonRoot` | 结算卡的 `Buttons` 空行（`RideResultCard/Buttons`，两颗按钮的父物体；不是按钮本身，也不是卡根） | Result | Idle / Running / Presenting |
| `StoryResultPresenter.resultCardRoot` | 结算卡根物体（`RidePanels/RideResultCard` 的 Prefab 实例） | Presenting / Result | Idle / Running；Respawn 放弃时也不显示 |
| `StoryResultPresenter.resultText` | 卡上的 `RideResultCard/ResultText`（TMP） | Presenting 写「呈给国王」，Result 写完整结果 | 离开 Presenting/Result 时清空 |
| `StoryRoundController.startPromptText` | 提示行的 `Text` 本身：一个只有这个 `Text` 的独立对象，**不要**放进 `resultCardRoot` 子树（Idle 会把提示和卡面一起关掉） | Idle | Running / Presenting / Result |

这些字段默认都是 null，空链接一律跳过，所以没接的场景（含无头实验）行为与改动前完全一样——没有提示行时，Idle 的提示退回剩余行。

### 两个写者，各写各的对象

`StoryResultPresenter.Update()` 是卡根 active 状态的唯一写者：Idle / Running 隐藏卡根并清空 `resultText`；Presenting 写「呈给国王」并打开卡根；Result 写星/测量值/评语并保持打开；自由练习关（`FreeRoam`）与 `AbortRound()` 放弃的局不显示卡。

`StoryRoundController.ApplyStateVisibility()`（`StoryRoundController.cs:1010`）只写 `retryButtonRoot`（`Buttons` 行，两颗按钮一起）与 `startPromptText`：

| 调用点 | 进入的状态 | 提示行 | `Buttons` 行 |
|---|---|---|---|
| `EnterIdle()` | Idle | 开 | 关 |
| `BeginRound()`（含重试成功后的重开） | Running | 关 | 关 |
| `SettleRound()` | Presenting | 关 | 关 |
| `Update()` 的 Presenting→Result 转移 | Result | 关 | 开 |
| `AbortRound()` | 放弃后的 Result | 关 | 开（卡根由 presenter 关着，看不到） |
| `RequestRetry()` 开头 | 仍为当前状态 | 按当前状态重写 | 按当前状态重写 |

`Start()` 的两个分支分别走 `EnterIdle()` 或 `BeginRound()`，所以加载后的第一次 Start 就会把 `Buttons` 行关掉（按 Unity 生命周期，Start 在第一帧渲染前跑完），正常不会先闪一帧；Idle 分支同时把提示行打开。场景里把 `Buttons` 行在 authoring 时就设成 inactive 是双保险。`RequestRetry()` 开头那一次是“重写”：按错状态进来的调用会把 `Buttons` 行重新藏掉，而不是让按钮与状态各说各话。

### 按钮显隐不是规则，`RequestRetry()` 的状态闸门才是

显隐只决定“看得见/按得到”，不决定“能不能重开”。`RequestRetry()` 仍然只在 `StateResult` 接受（其余状态一行 warning 后返回 false，卡面与回合都不动），所以就算某个场景把 `Buttons` 行拖错、或者在 Result 之前用别的方式把它显示出来，也开不了新回合，更不会出现“新回合配旧球”。这也是本节的显隐逻辑可以放心做的原因：它只改表现，规则仍在一处。

### 场景怎么接（Unity 操作者）

1. `retryButtonRoot` 指向结算卡的 `Buttons` 空行（两颗按钮的父物体；不是按钮本身，也不是卡根），装配器写这个字段，手工接线的场景照此指向。两颗按钮的 `Button.onClick` 一条 `SendCustomEvent("Press")` 属于 Prefab，装配器与 `ReadResultCard` 回读。
2. `resultCardRoot` 与 `resultText` 在 `StoryResultPresenter` 上（装配器写）：卡根从 Presenting 起显示，Result 沿用；presenter 在 Presenting 写「呈给国王」、Result 写星/测量值/评语，牌面本身由 Prefab 决定。
3. **结果文字 `resultText` 就在卡根子树里**（`RideResultCard/ResultText`，TMP）：它从 Presenting 起才有内容，卡面也正好在这两个状态开着，两者同显同隐最省事。开始提示不占结果文字：它有自己的一条 Text（`startPromptText`），由回合控制器在 Idle 打开、其余状态关掉。所以 **`startPromptText` 必须放在卡片外面**：放进 `resultCardRoot` 的提示会在 Idle 跟着卡面一起消失。
4. **不要把这些对象拖成对应行为自己的 GameObject**：卡根与 `Buttons` 行在 Idle 会被 `SetActive(false)`，提示行在非 Idle 被关；只要行为自己的对象被关掉，它随后就不再运行，回合永远停在 Idle。卡根、`Buttons` 行与提示行都必须是独立对象（通常和卡面一起放在同一个 Canvas 下）；提示行还要是只有这一个 `Text` 的专属对象，别拖一个共用的父物体，否则关它会连带关掉别的 HUD 行。
5. `RequestRetry()` 是 `public bool`；上面的显隐字段默认 null，不接时行为与无显隐逻辑相同。
6. U# 语言约束照旧：没有属性、重载、泛型、`??`、字符串插值、static 字段；`ApplyStateVisibility()` 是无参私有方法，写 `GameObject.SetActive(bool)` 与 `startPromptText.gameObject.SetActive(bool)`（`PickupFieldHost.cs` 有 `prop.gameObject.SetActive(...)` 先例）。

## 道具字段的编辑器同步（PickupFieldSync）

> 本节描述编辑器同步工具的约定与入口。

### 为什么从道具本身推导（方案 a）

`PickupFieldHost` 的六个并行数组是运行时唯一的数据所有者，作者数据必须落到它们身上。**编辑器程序集里的 MonoBehaviour 没有运行时类型，Unity 无法把它序列化进要构建的场景**，所以“挂一个 EditorOnly 的作者组件”不成立；方案 (b) 要新增一个运行时组件再加构建期剔除，只为了作者数据。做法改成：场景里的道具已经是数据（名字、层级、Renderer AABB、Collider），编辑器工具把它们算成六个数组、写进 proxy、再 `CopyProxyToUdon`。运行时一行代码没加。

### 作者要遵守的约定

| 数据 | 来源 | 规则 |
|---|---|---|
| itemId | 道具 GameObject 的名字 | 全字段唯一；同名行**全部**拒（id 是候选/账本键）；灰盒的 `id#NN` 天然满足 |
| kind | 道具的父文件夹名，即 field root 的直接子物体 | 小写类别名（`food`、`nature`…）；必须在价值表里有一行 |
| propBounds | 道具子树里所有 Renderer 的世界 AABB 的 `size` | **全尺寸**（米，不是半尺寸）；最长边 < 0.1 mm 判为零尺寸 |
| 尺寸档 | AABB 最长边 | `s` ≤ 0.21 m、`m` ≤ 0.76 m、其余 `l`（取自 PicnicLawn 目录 0.18/0.24 与 0.72/0.80 两处空档，PROVISIONAL） |
| valueUnits | 价值表的 (kind, tier) 行 | 整数 ≥1（`Docs/OPEN_DECISIONS.md` (j)）；未声明的组合**直接拒，不给默认值**；加 kind = 在表里加一行 |
| propColliders | 道具自己的 Collider，否则子树里按路径序第一个 | 一行最多一个（宿主只关一个）；多 Collider 会在报告里提示 |

目录形状：`<field root>/<kind>/<prop>`。道具的**根必须自己带 Renderer**（子树里的 mesh 算同一件道具）；没有 Renderer 的叶子是数据错误，会被拒并报出原因。

### 入口与顺序

- 菜单 `VRC Katamari/Pickup/Sync Prop Arrays From Children`：选中 host（或它下面的道具），field root 取 host 自身。
- 组件右键 `Sync Prop Arrays From Children`。
- 代码入口 `PickupFieldSync.SyncFromChildren(host, fieldRoot)`，返回 `PickupFieldSyncReport`：件数、按 kind 的计数、总价值单位、全部被拒行与原因、U# 拷贝是否发出、读回长度是否对。

顺序：按 field-root 相对路径 ordinal 排序 → 校验 → 用 `SerializedObject` 写 proxy 六个字段 → `UdonSharpProgramAsset.CompileAllCsPrograms(false, false)`（只编 dirty 的，避免每次重编全工程）→ `UdonSharpEditorUtility.CopyProxyToUdon(host)` → 标脏 + 标脏场景 → 读回长度。重复运行幂等：同一层级算出同样的数组。全部候选被拒时**不动**既有数组；field root 下一个候选都没有时清空并提示。本工具不建组件、不建 program asset、不保存场景。

### 被拒原因（都进报告，不静默修）

重复 itemId（同 id 全拒）、缺 Renderer、零/非有限 bounds、kind 名带首尾空白、未声明的 (kind, tier)、value <1 或非整数、kind 文件夹自己带 Renderer。

### 程序集与测试

- `Editor/Pickup/` **不在**现有 `VRCKatamari.Editor.asmdef` 的覆盖范围内（该 asmdef 在 `Editor/MapGreybox/`），按任务要求没有新建 asmdef：三个文件编进预定义的 `Assembly-CSharp-Editor`（仅编辑器；U# 的 `UdonSharp.Runtime` 与 `UdonSharp.Editor` 都是 `autoReferenced: true`，所以能直接用 `UdonSharpEditorUtility`）。若 Lead 要归入 `VRCKatamari.Editor`：把 asmdef 上移到 `Editor/`，references 加 `UdonSharp.Runtime`、`UdonSharp.Editor`；代码不用改。
- 纯规则在 `Editor/Pickup/PickupFieldAuthoringRules.cs`（尺寸档、价值表、重复 id、ordinal 排序），测试 `Tests/EditMode/Pickup/PickupFieldAuthoringRulesTests.cs` 用反射调用，因为 `VRCKatamari.Tests.EditMode` 只引用 `VRCKatamari.Runtime`（与 `BankReasonTests` 的既有做法同因）；给测试 asmdef 加一行引用即可改成直接调用。

## 显式拾取尺寸：名字后缀 `@size=`（补 G13）

> 本节是 `@size=` 的唯一最新口径；上面的「作者要遵守的约定」表里 **itemId / propBounds 两行按本节扩展**（其余行不变）。

### 为什么需要它

同步工具按 Renderer 世界 AABB 取 `propBounds`，而门件恰恰是「长而细」：G3 的长凳 3.2 m 长、G2 倒木 3.0 m、G1 擀面杖 1.6 m（`Docs/Design/Maps/REGION_MAP_PLAN.md` §2.4）。不补这条通道，长凳会被记成 3.2 m，1.25 m 的门永远打不开（同文件 §4 的 G13）。这不是「AABB 算错」：外形尺寸和拾取尺寸本来就是两回事（决定 (h) 提醒过，`Docs/OPEN_DECISIONS.md:39`）。

### 作者怎么写

把拾取尺寸写在道具 GameObject 名字的末尾：

```
prop_bench_01@size=1.25
```

- 只认名字**最后**那一段 `@size=<数字>`；`@size=` 必须小写、整名只能出现一次。
- 数字按 invariant culture 解析：小数点 `.`，不认千分位、空白和指数。所以 `1,25` 在德语机器上也一律拒绝，而不是悄悄变成 1.25 或 125。
- 合法范围：有限、>0、≥0.0001 m（与既有的 `MinBoundsExtentM` 同一门槛）。`0`、`-1.25`、`0.00005`、`NaN`、`Infinity`、`1e40` 全部拒。
- `@size=` 必须收尾：`prop_bench_01@size=1.25#02`、`prop_bench_01@size= 1.25` 都拒（后者带空格）。
- 后缀**从 itemId 里剥掉**：`prop_bench_01@size=1.25` 的 itemId 是 `prop_bench_01`。这样 itemId 仍是关卡计划 §4.2 的 `prop_<definitionId>_<NN>`，日志/账本里不会出现工具语法。代价是同一件道具的「带后缀」与「不带后缀」两种写法会撞 id → 重复 itemId 全拒（不会写出两行同键）。
- 写了但读不出来 = **该行被拒 + 原因进报告**，绝不静默回退到 AABB。理由：作者写了尺寸却拿到 3.2 m 的 AABB，是这条约定最该防的失败。

### 工具怎么用它

- 声明尺寸按**立方体**写进 `propBounds`（`1.25` → `(1.25, 1.25, 1.25)`），尺寸档也按这个边长判；`valueUnits` 仍走 (kind, tier) 表。所以门件的 kind 必须有对应档位的行：长凳在 `furniture` 下、l 档 = 16（`Editor/Pickup/PickupFieldAuthoringRules.cs` 的价值表）。
- **AABB 仍然必须先存在且可用**：没有 Renderer、AABB 的**最长边** < 0.0001 m、或任一边为负/非有限的道具，即便写了 `@size=` 也照拒。某一边正好是 0 **不算**退化（纸片、叶子、Unity 的 Plane 本来就是扁的），档位只按最长边判。声明的是拾取尺寸，不是「这个物体不用渲染」。
- 报告：每个采用声明尺寸的行加一条 `note`（路径、原名、声明边长、原 AABB、档位变化，例如 `s -> l`）。被拒行照旧进 `rejected` 列表。
- 纯函数在 `Editor/Pickup/PickupFieldAuthoringRules.cs`：`DeclaredSizeMarker`（`"@size="`）、`TryParseDeclaredSize(name, out itemId, out declaredEdgeM, out error)`、`IsUsableDeclaredSize(edgeM)`。同步侧只在 `Editor/Pickup/PickupFieldSync.cs` 里调用它们，没有第二套解析。

### 为什么用名字后缀，而不是子物体或组件

名字跟对象一起走：改名、拖进 prefab 变体、复制道具，后缀都在；不需要多一个场景物体（子物体标记会被「根必须自己带 Renderer、整个子树算同一件道具」的规则当成外形的一部分），也不需要运行时类型（组件方案正是 §「为什么从道具本身推导（方案 a）」否掉的那条路）。剥掉后缀后 itemId 仍与关卡计划一致。

### 关卡 2 的 `fruit` 价值行

价值表含两行：`fruit s = 1`、`fruit m = 4`（`Docs/Design/StoryMode/LEVEL_CONTENT_PLAN.md` §3.1 的水果表：草莓/葡萄 s、苹果/梨/橙/香蕉/西瓜 m）。不加这两行，所有水果都以「未声明的 (kind, tier)」被拒，关卡 2 的水果数就是 0。**故意不加 `fruit l`**：计划里最大的水果是 0.72 m 西瓜（m 档），>0.76 m 的水果必须自己加一行，而不是拿默认值。这些数字与表内其余行一样是 PROVISIONAL。

### 测试

- 测试在 `Tests/EditMode/Pickup/PickupFieldAuthoringRulesTests.cs`：解析并剥名、非法数字、多个标记、非正/过小、非有限与指数、locale 独立（在 `de-DE` 下 `1.25` 通过、`1,25` 被拒）、档位与值表联动（`1.25` 立方 → l，`furniture l = 16`；`0.72` → m，`fruit m = 4`），并覆盖 `fruit s/m` 有行、`fruit l` 缺失的价值表。`DegenerateBoundsHaveNoTier` 只覆盖三边都不可用的 AABB（全 0、三边 0.00005、负数、NaN、±Infinity）；`FlatPropsKeepTheirLongestSideTier` 钉住「一边为 0、其余边正常 → 仍按最长边分档」。

## 全局球径上限（现行）与第一关星级带（2026-11-15 参考输入）

> **2026-11-15 用户决定：现阶段不调数值**（“现在数值上的问题倒不是主要矛盾点”）。本节的 1.25 / 1.40 / 1.42 只是**评分模块将来的参考输入**，不是待办；星级与评语本身属于可拆卸的结果卡模块，与回合核心解耦（见 `Docs/OPEN_DECISIONS.md` 的 (m)(p)）。数字与算式保持原样，等评分模块接线时再取用。

> 状态：`PROVISIONAL_NOT_TESTED`。本节是静态数值设计；唯一的“实测”是 `LEVEL_CONTENT_PLAN.md` §7.4 记录的 1 次 120 s Play Mode（370 u / 1.43 m）。“场景值”来自 `KatamariBallMotor.cs` 的直接读取。

### 1. 全局球径上限 400 m（owner 2026-11-18）

所有地图共用 `KatamariBallMotor.GlobalMaxBallDiameterM`。球长到 400 m（语义直径）时，由 `SizeModelMath.TryDerive` 饱和；切关只切换起始大小，不改变上限。它不是每张图的可达值，实际能长多大取决于可吸内容和成长量。

**骑手缩放到平台上限就停**：骑乘时 avatar 眼高 E = 2.9001·r ÷ η 由 `RideScaleMath.TryEyeHeightForDiameter` 夹到 `[0.1, 100] m`（`MaxSupportedEyeHeightMeters`；VRChat 不支持 >100 m 的头像缩放）。η ≈ 1 时球径约 69 m 起眼高就停在 100 m，球继续长到 400 m 而骑手不再变大——这是平台限制，不是待修项。

### 2. 第一关星级带：1.25 / 1.40 / 1.42

**星级参考模型的常数**：`B = 3.4390244 u`，`c = 0.0041 m³/u`；`V(d) = πd³/6`；`total = V/c`；`confirmed = total − B`；`d(U) = (6(B+U)c/π)^(1/3)`。该参考模型的内容声明是 **127 件 / 1500 u**（`Docs/Design/Levels/story_01_bench_size/placement.json` 的 `level.expected`），所以 `d_max = (6×(3.4390244+1500)×0.0041/π)^(1/3) = 2.2746 m`（模型估算，不是运行时球径上限）。**下面这张星级表仍按旧 110 件 / 412 u 的账算（决定 (p)：现阶段不调星级数），「占 412」那一列不是现行内容占比。**

| 星 | 目标 `d` | `V = πd³/6` | `total = V/c` | `confirmed = total−B` | 占 412 | 余量 `412−confirmed` |
|---|---:|---:|---:|---:|---:|---:|
| ★ | **1.25** | 1.022653859 | 249.4278 | **245.9887** | **59.71%** | 166.01 |
| ★★ | **1.40** | 1.436755040 | 350.4281 | **346.9890** | **84.22%** | 65.01 |
| ★★★ | **1.42** | 1.499214091 | 365.6620 | **362.2229** | **87.92%** | 49.78 |

逐行算式（可手算复核）：

- ★：`π/6 × 1.25³ = 0.523598776 × 1.953125 = 1.022653859`；`/0.0041 = 249.4278`；`−3.4390244 = 245.9887`；`245.9887/412 = 59.71%`。
- ★★：`1.40³ = 2.744`；`×0.523598776 = 1.436755040`；`/0.0041 = 350.4281`；`−3.4390244 = 346.9890`；`/412 = 84.22%`。
- ★★★：`1.42³ = 2.863288`；`×0.523598776 = 1.499214091`；`/0.0041 = 365.6620`；`−3.4390244 = 362.2229`；`/412 = 87.92%`。
- 反过来：`d(412) = 1.481713`；`1.42/1.481713 = 95.84%` 的直径上限，单位余量 49.78（12.08%）。
- 对照：`1.45` → `385.8918 u`（93.66%，余 26.11）；`1.60` → `519.6489 u`（126.13%，**−107.65，数学上不可达**）。旧的 ★★★ 1.60 不是"难"，是错。

> **适用前提**：上表是真实尺寸落地后（110/412/1.4817）的配额；当前混合场景只到 320 u / 1.3631 m，★★/★★★ 还拿不到（见 §5 最大风险）。

**为什么是 1.42 不是 1.43/1.45：**

- 唯一一次 120 s Play Mode：**370.00 u = 1.4300 m**（`LEVEL_CONTENT_PLAN.md` §7.4）。1.42 在它下面 **7.78 u（约 2.5 s）**；1.43 正好等于它；1.45 在它上面 **15.89 u（约 5.2 s）**。**该样本对应真实尺寸状态；当前场景 320 u 上限下不可复现（见 §5）。**
- 1.42 是"**时间余量为正、且与 ★★ 仍有 0.02 m 差**"的值；再低（1.41）与 ★★ 只差 0.01 m，已经不算一档。代价是 ★★→★★★ 只有 0.02 m / 15.2 u，是"最后一口气"而不是新档。这是 412 u 只到 1.4817 的结构性结果：★★ 已经占 84.2%，剩下的 15.8% 只买得到 0.08 m。
- 时间余量有两个锚点，差很大：按 §7.4 的 93 s 下界线性折算，1.42 ≈ 81.8 s（余 **38.2 s**）；按唯一一次 120 s 样本，余 **2.5 s**。中间取决于路线熟练度，所以"1.42 可达"不是理论刚好，但也不是随便打打就有。
- 1.43 可以作为"上限值"：如果接受"n=1 的 370 u 就是目标线"，1.43 = 89.8%，内容余量 42.0 u（10.2%），但它在这条样本上时间余量为 0。
- 1.45 不要在 120 s 下用：385.89 u 比唯一实测高 15.89 u，会重复旧 ★★ 1.45 的"紧"（`LEVEL_CONTENT_PLAN.md` §7.6 已把 1.45 称"可达但极紧"）。

### 4. 如果一定要 1.45：三个补救，别只调门限

| 方案 | 改动 | 1.45 需要 | 120 s 内? | 代价 |
|---|---|---:|---|---|
| 现状 120 s | 无 | 385.89 u / 125.2 s | 否（比唯一实测高 15.89 u） | 第三星只能到 1.42/1.43 |
| 135 s | 时限 +15 s | 385.89 u / 125.2 s | 是，余 ~10 s | 关卡变长，清空率上升 |
| `c` +7% | 0.0041→0.0044；`B`→3.2045 | 359.58 u / 116.6 s | 是，余 ~3 s | 全局经济改，1,356→1,264 u；起手仍 0.2997 m |
| +45 u 内容 | 412→457 | 385.89/457 = 84.4% | 仍是 386 u / 125 s | 不加时间仍是紧；星级要重排（1.29/1.45/1.48） |

### 5. 结论与风险

- **第一关不是"撑不满 120 s"，是"120 s 吃不完 412 u 的最后 10%"。** 412 u 在 3.083 u/s 下要 133.6 s；120 s 只能吃 370 u（89.8%）。所以第三星的问题在时间/成长速度，不在"没道具"；**再加道具而不提高单位密度，只会让 1.42 之后更慢**（如果新道具在同一路线上、单位/秒更高，就能把 1.42 提前，见 +45 u 方案）。
- **最大风险不是星级，是当前场景的混合 `propBounds` 把第一关停在 1.3631 m（100/320 u）**：`glass_bottle` 0.52³ = 0.140608 vs 门 0.132610（6.0%），剩下 4 暖壶/3 西瓜/1 玻璃瓶/2 小盆栽共 92 u 全吃不到。**当前场景只能拿 ★ 1.25；★★ 1.40 要 346.99 u、★★★ 1.42 要 362.22 u，都超过 320**，必须先落真实尺寸（110/412/1.4817）。另外，`LEVEL_CONTENT_PLAN` §7.4 的 370 u 样本与当前 320 u 上限不一致，样本应来自真实尺寸状态（或场景在采样后被改回游戏尺寸），**这条样本不能当作当前场景的校准**，需复核。
- **第二大风险：`@size=` 是按立方体写 `propBounds` 的**（本文件「显式拾取尺寸」节）。在体积门下，`@size=1.25` 的体积是 1.953 m³，比 3.6×0.55×0.80 的 1.584 m³ **更大**——它不能把长凳变"小"，反而更贵；`@size=1.25` 需要 `D=3.341`、`U=4,760`，比真实 AABB 的 `D=3.116`、`U=3,860` 还多，连 3.20 的上限都不够。长度门时代的 `@size` 口径必须按体积重设计，不能直接沿用。
- **第三大风险：PicnicLawn 的 3.6 m 长凳在 1,284 u 下不可吸**；3.20 的上限只解决"直径够不够"，不解决"单位够不够"。要么把 PicnicLawn 的内容/价值提到 ~4,181 u，要么把长凳的拾取 AABB 压到 ≤0.526 m³（横截面 ~0.38×0.38），要么把它改成可绕过的装饰而不是门。

## 已知限制

以下行为没有 Play Mode / 真机记录（Hub 闭环自己的限制见 [HUB_ROOM](<HUB_ROOM.md>) §11）：

1. **坐姿下桌面轴是否仍送达 Udon**：由「5 分钟 Build & Test 探针」回答；Player Mobility = Immobilize 下 `InputJump` 是否送达同样没有实测。桌面 WASD 的符号（A 是 −1 还是 +1）官方文档没写，代码按“A 负、D 正”映射。
2. **Station 回调必须与 VRCStation 同物体**：依据是实验场景布局与 StationPlatform 的 PC 实测，不是 VRChat 文档原文。
3. **点击距离**：座位在 rig 根上，准星到球的 Collider 至少是 `back`（0.5 m 球 1.85 m 起、1.43 m 球 2.32 m）；超出桌面交互距离就点不到，处理是减小 `eyeBaseBackMeters`/`eyeBackPerRadius` 或加大 Collider。
4. **菜单与按住输入**：官方只写打开菜单会释放按住的按钮输入，轴是否补发 0 未说明；motor 有 `SetMenuOpen` 接缝，输入适配器没有用它。
5. **已经坐在站里进世界**（例如复活回座位）时没有“加载时已在座”的路径：站只在 `OnStationEntered` 通知，会停在 Idle 且骑乘闸门关着，要离座再坐一次。
6. **`Update` 顺序**：宿主与 motor、控制器之间没有顺序保证，事件可能晚一帧被消费；附着写的是局部位姿，晚一帧不会漂移，快滚时的视觉差没有观察。
7. **后备路径**：事件环溢出只打日志，附着失败只隐藏；正常一局只走“附着成功”，这两条没有被执行过。
8. **uGUI `Button` → `SendCustomEvent` 接线**：墙面板卡片与结算卡两颗按钮都走这条；代码与装配器回读已写，本仓未在 Unity/真机按过。
9. **单人**：全部行为 `BehaviourSyncMode.None`，`DisableInteractive` 是本地状态；多人不在范围内。
10. **每帧成本**：宿主每 0.05 s 一次全道具平方距离扫描（切片约 100 件、灰盒 448 件），真实 Udon 开销没有量过。

## 主房间闭环（HUB_ROOM）

> 现行决定：owner 2026-11-18 要求墙面选关与游戏结束回大厅。代码与装配器已更新；这轮 Unity/Udon/VR 尚待验收。完整操作步骤、截图与预期读数见 [HUB_ROOM](<HUB_ROOM.md>)，合集规则见 [ALL_MAPS_HUB](<ALL_MAPS_HUB.md>)。

- **选关数据驱动**：[hub_levels.json](<hub_levels.json>) 定义关卡与索引。Assemble All-Maps Hub 通过 [HubAssembler](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.cs>) 重建北墙 2×2 关卡卡片面板（uGUI Button），host.levelButtons与入口数组同序；不依赖手摆。
- **开始**：只有明确点击才开始（墙面卡片是 uGUI Button，onClick → `SendCustomEvent("Press")`）。桌面准星鼠标左键，VR指向扳机；走动/停留不选关。传送到关卡入口，入口离球座>12m才回退座位，量到到达后开回合并请求自动入座。
- **结算**：完整Result卡默认显示8秒后自动回大厅，也可点「回大厅」提前回或「再来一次」取消自动返回。重试经同一个launcher处理传送/唤醒岛，不绕过导航。退座慢时卡上提示「请先下车」，每秒复查，Riding负责退出重发。
- **Respawn**：本地菜单重生先AbortRound，取消旧开始与入座，再确认退座、返回HubSpawn、停用关卡岛；不出放弃结果卡，也不会被Running纠偏送回球上。回大厅首个明确点击即可再选关。
- **单场景、多关**：共享一个球、一个回合控制器；按关登记物理与道具。空闲岛SetActive停用，基础设施不在岛内。规则测试包括 [HubResultFlowTests](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/Hub/HubResultFlowTests.cs>)、HubLoopMathTests、HubRoomMathTests；场景行为仍须Unity与owner头显复测。
