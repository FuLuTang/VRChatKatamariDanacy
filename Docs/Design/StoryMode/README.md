# 剧情模式切片：输入、球视图、骑球、吸附宿主与剧情回合

> 状态：`PARTIALLY_COMPILED`，2026-11-14。五个模块的源码都已写；只有 `DesktopMoveInput` 与 `BallView` 的**旧版本**在 `Labs/StoryLevel` 里编译并实跑过（commit `e74ff8c`，对应 U# 编译产物 `Runtime/Input/DesktopMoveInput.asset`、`Runtime/BallPresentation/BallView.asset`，见 `Docs/Evidence/StoryLevel/windows_assets_manifest.txt`）。`DesktopMoveInput` 后来改成两把闸门（见“输入闸门”），**这一版未编译**；骑球/吸附/回合也从没有编译、没有 Play Mode、没有 Build & Test、没有真机。
> 计划与判据见 [第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §1–§4、§7 S1–S4。

| 模块 | 文件 | 状态 |
|---|---|---|
| 输入 | `Runtime/Input/DesktopMoveInput.cs` | 源码已写；单个开关的旧版已在 `Labs/StoryLevel` 编译实跑（commit `e74ff8c`），两把闸门这版未编译 |
| 球视图 | `Runtime/BallPresentation/BallView.cs` | 源码已写；已在 `Labs/StoryLevel` 编译并实跑（commit `e74ff8c`） |
| 骑球 | `Runtime/Riding/BallRideStation.cs`、`Runtime/Riding/BallRideExitButton.cs` | 源码已写，未编译 |
| 吸附宿主 | `Runtime/Pickup/PickupFieldHost.cs` | 源码已写，未编译 |
| 剧情回合 | `Runtime/Story/StoryRoundController.cs` | 源码已写，未编译 |

下文按模块分节；每节末尾列该节自己的未验证项，不要跨节当成已验证。

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

两者默认都是开，所以既没有 Station、也没有回合控制器的场景照常能开——`Labs/StoryLevel` 的键盘实验就是这种场景。

**为什么是两把而不是一把**：以前只有一个共用开关，两个所有者都写它，互相覆盖（最后一笔写赢）。实际后果：到点关掉输入后，离座再入座会被骑球宿主重新打开，回合结束后又能开球；反过来 `BeginRound` 也会把骑球宿主关上的门打开。拆成两把后，谁都不能改别人的门，生效输入 = 两者都开。任一关上时适配器把两轴清零并再推一次零（球刹车），重新打开不推零。

### 场景怎么接（Unity 操作者的活）

1. `MotorHost` 上只挂 `KatamariBallMotor`（独占 GameObject，项目惯例见 [BallMotor README](../BallMotor/README.md) 的「装配约束」；同物体本身合法）。
2. 层级：`BallPhysicsRoot`（motor 只写 position）→ `BallVisualRollRoot`（motor 只写 rotation，**localScale 恒 1**）→ `CoreMesh`。把 `ballPhysicsRoot`、`ballVisualRollRoot` 拖到 motor 上。
3. 新建一个空对象（例如 `DesktopInput`），**独占**挂 `DesktopMoveInput`，把 `motor` 拖进去。不要和 motor 同挂一个对象。
4. 新建一个空对象（例如 `BallViewHost`），**独占**挂 `BallView`，把 `motor` 与 `CoreMesh` 拖进去。
5. 相机架：`motor.followRigRoot` 接到相机跟随根；不接的话 motor 算出的跟随帧没有落点（计划 §1 已记录这一点）。
6. 地面：`AddPlane` 注册；起始位置在 `startCenterWorld`。

`CoreMesh` 的网格约定：**直径 1 m、pivot 在球心的球体**（例如 Unity 内置 Sphere），因为代码直接把它缩放到语义直径。换成直径不是 1 m 的网格时显示尺寸会差一个固定倍数，需要在美术侧改，而不是在这里加一个魔数。

### 未验证（S1，不要把这些当已验证）

1. **`BallView` 与 `DesktopMoveInput` 的旧版已编译并实跑**（`Labs/StoryLevel`，commit `e74ff8c`）。`DesktopMoveInput` 现在改成两把闸门，**这一版没有编译记录**；下面这些具体判据也没有逐条留下的记录。
2. `InputMoveVertical` / `InputMoveHorizontal` / `OnDisable` 的签名已随 `Labs/StoryLevel` 编译通过（commit `e74ff8c`）。签名来源是已安装 SDK 的 `UdonSharpBehaviour.cs`；`OnDisable` 是 Unity 生命周期消息（官方 [Event Execution Order](https://creators.vrchat.com/worlds/udon/event-execution-order) 把 `OnEnable`/`OnDisable`/`OnDestroy` 列在 Unity 事件里），所以写的是普通私有方法而不是 `override`。`OnStationExited` / `OnPlayerRespawn` 的签名另在 `Labs/StationPlatform/StationLabDriver.cs` 里编译并通过 PC 实测（T04）。T04 那次轴日志改动本身从未编译，本文不拿它当证据。
3. 桌面 WASD 的**符号**没有留下实测记录：官方文档只写 `InputMoveVertical` = W/S、`InputMoveHorizontal` = A/D、桌面上输出 −1/0/1 整数（[Input Events](https://creators.vrchat.com/worlds/udon/input-events)），没有写 A 是 −1 还是 +1。代码按计划“A 负、D 正”映射，下一位操作者要先确认 A 是左转。
4. 坐姿时 VRChat 是否继续把轴发给 Udon：未验证，也就是计划 §8 的风险 1（S2 才有结论）。
5. 菜单：官方文档说打开 VRChat 菜单时 Udon 会“释放所有按住的输入”，但只对按钮事件明确写了会补发 `InputJump(false)`；轴是否也补发 0 未说明。所以“按住 W 时开菜单”球会不会继续跑，未验证。motor 有 `SetMenuOpen` 接缝，本适配器没有用它（计划 S1 没要求）。
6. `BallView` 的第一个写帧依赖 motor 的 `Start()` 已跑过（未跑时读数是 0，代码跳过而不是写 0），但“Start 顺序”只做了代码层面的防御，没有运行时观察。
7. 1e-4 阈值、`Vector3.one * diameter` 的视觉结果、球底贴地判据（`center.y − d/2`）没有单独的量化记录；`Labs/StoryLevel` 的实跑只到“能开、能看”这一层。

### 与计划的差异（S1，留给 Lead 决定）

- 计划 §4 的输入关闭示例已改写成 `input.SetRoundGate(false)`，与实现的两把闸门（见“输入闸门”）一致。
- 计划 §7 S1 列出的 `Labs/StoryLevel/StoryLevel.unity` 与 `StoryLevelLabBootstrap.cs` 后来已经建好（commit `e74ff8c`），不在本文的脚本范围内。

### 没有改动的东西（S1）

没有改 `KatamariBallMotor`、asmdef、场景或任何既有文件；两个脚本各带 `.meta`，新目录 `BallPresentation` 也带 `.meta`，GUID 均为新生成且全仓唯一。

## 骑球

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-14。本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §1、§7 S2 与 §8 的风险 1。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test、没有真机。

### 三个脚本

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Riding/BallRideStation.cs` | 与 VRCStation 同物体的骑乘宿主：未入座时 `Interact` → `station.UseStation(localPlayer)`；本地球员入座/离座时开关 ride 闸门、开关交互提示；`IsLocalPlayerSeated()` 给探针读 | 不移动任何 Transform（带人的是 Station，位置由 motor 的 rig 帧给）、不写 motor、不抢账本/回合状态、不同步任何变量 |
| `Runtime/Riding/BallRideExitButton.cs` | 世界内退出的唯一出口：`Interact` → `station.ExitStation(localPlayer)` | 不持有 seated 状态、不碰 motor；状态复位由 `BallRideStation.OnStationExited` 一处负责（实验里的退出按钮要自己清 driver 的标记，所以需要防重复退出；这里不需要） |
| `Labs/StationPlatform/SeatedAxisProbe.cs` | 只为回答计划风险 1 的只读探针：每个 `InputMoveVertical`/`InputMoveHorizontal`/`InputJump` 打一行 `[seated-axis]`，带 `seated=` | 不是产品代码，没有任何别的文件引用它；答案写进本文后连文件、`.meta`、组件一起删 |

三个都 `BehaviourSyncMode.None`，没有 static、属性、struct、接口、泛型、LINQ 或每帧分配。

### 必需的物体布局（少一条就不成立）

1. **FollowRig** 必须是 motor 的 `followRigRoot`（`KatamariBallMotor.cs:137`；每帧只写它的 `position` 与 yaw，`:935-939`）。不接的话 rig 帧没有落点，载人整段不生效。
2. **rig 根贴球心**：把 `eyeHeightMeters` 从默认 1.6 起调到 0.25–0.5（默认值是给人形 rig 的，`KatamariBallMotor.cs:118`），“球后上方”交给 Station 自己的局部偏移。
3. **一个 GameObject 同时挂 VRCStation + 交互 Collider + `BallRideStation`**。三样必须同物体，不是风格：`Interact` 送到被点到的 Collider 所在物体的 UdonBehaviour；`OnStationEntered`/`OnStationExited` 送到 VRCStation 所在物体的 Behaviour。实验场景就是这个布局（`Labs/StationPlatform/StationPlatformLab.unity:157` driver、`:198` VRCStation、`:277` BoxCollider 全在 `Station` 物体上），并在 T04 的 PC Build & Test 里实测过。
4. **Station 是 rig 根的子物体**，`localPosition` 起始 `(0, 0.8, −2.2)`（球的后上方，计划 §1 给的起点）。坐着的人由 Station 承载，跟随靠 motor 写 rig 根，不靠任何脚本搬人。
5. **VRCStation 打开 Disable Station Exit**（实验场景 `disableStationExit: 1`，`StationPlatformLab.unity:204`）。Player Mobility 先保持实验文件里的序列化值 `0`，含义未在本仓核实（VRChat 组件文档的枚举是 Mobile / Immobilize / ImmobilizeForVehicle，文档建议会动的载具用 Immobilize For Vehicle）。
6. **Collider 要覆盖球的可见位置**：座位在球后上方 2.2 m，所以把 BoxCollider 的 `center` 前移下移（起始试 `center (0, −0.8, +2.2)`、`size 1.2³`），让准星对着球就能点到；否则点的是空中的座位，用户会说“点球没反应”。
7. **`DesktopInput` 物体**：独占 `DesktopMoveInput`，把 `BallRideStation.input` 指过去。项目惯例是分开挂：motor、Station、输入各占一个对象，职责和自检都清楚（同物体本身合法，见 [BallMotor README](../BallMotor/README.md) 的「装配约束」）。
8. **退出按钮**：独立 GameObject + Collider + `BallRideExitButton`，`station` 指同一个 Station；放在坐姿视野前方、不被球的 Collider 挡住。坐姿下鼠标左键仍可交互（T04 PC 实测的退出按钮路径）。

### 驱动门：谁能用 WASD

`DesktopMoveInput` 只认设备，谁按都转成球轴，所以“只有坐着才能开”必须由骑乘宿主把关：`Start()` 先把自己那把闸门关上（站着的人不能遥控球），`OnStationEntered`（本地球员）打开，`OnStationExited` 再关上。闸门是 `DesktopMoveInput.SetRideGate(bool)`（`Runtime/Input/DesktopMoveInput.cs:77`），关的时候适配器会把两轴清零并再推一次零，球会刹车。

`OnPlayerRespawn` 也清一次：复活可能把人留在座位里而不发 `OnStationExited`，那样交互会永久禁用、再也坐不上去。实验的 driver 同样这么做（`StationLabDriver.OnPlayerRespawn`），这里只是照抄这条保险。

**与 S4 的分工（已解决）**：`StoryRoundController` 不再共用这把开关，它只关自己的 `SetRoundGate(bool)`（`Runtime/Story/StoryRoundController.cs` 到点时调 `false`）。两把闸门互不影响，所以到点后再入座也不会把输入重新打开；细节见 S1 的“输入闸门”。

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

### 未验证（不要当成已验证）

1. **没有编译**：U# 全量编译、Console 无错都还没发生。
2. **坐姿桌面轴是否到达 Udon**：这正是上面探针要回答的，现在未知（`Docs/Evidence/StationPlatform/axis_logging.md:3`、`:110-112` 也只到“改了源码没编译”）。
3. **Station 回调必须与 VRCStation 同物体**：本仓的依据是实验场景布局 + T04 PC 实测，不是 VRChat 文档的原文；文档只写“一个 GameObject 挂 VRCStation + Collider + UdonBehaviour”。若实际允许跨物体，可以再把 Collider 拆到球上。
4. **点击距离**：座位在球后上方，准星到球的 Collider 约 2.2 m 起。超出桌面交互距离就点不到——首次测试第一件事就是确认能坐下；点不到就减小座位偏移或加大 Collider。
5. **坐姿下是否还有第二个提示**：T04 的 D1 只证明 `DisableInteractive = true` 让 Behaviour 的“坐下”提示消失；VRCStation 组件自带的提示是否也被压掉，没有分开观察过。
6. **退出按钮在未入座时**：`ExitStation` 无对象可退出，预期是 no-op，未验证。
7. **坐姿镜头**（计划风险 2）：偏移 `(0, 0.8, −2.2)`、`eyeHeightMeters` 只是起点，会不会看天/被球挡只能 Build & Test 里调。
8. **Player Mobility = 0 的含义**：实验文件里是 0，本仓没有核实它对应 Mobile 还是 Immobilize；探针的第 5 步排查依赖这一点。
9. **多人/远端**：`BehaviourSyncMode.None`，`DisableInteractive` 是本地状态，远端客户端会看到自己的提示。切片是单人，不做。
10. **`IsLocalPlayerSeated()` 的跨 Behaviour 调用**：签名与调用方式（类型化引用上直接调公开方法）与 `DesktopMoveInput` 调 `motor.SetMoveAxes` 同形（`DesktopMoveInput.cs:149`）。`DesktopMoveInput` 本身已编译（commit `e74ff8c`），但探针这个调用方自己没有编译过。

### 与计划的差异

- 计划 §1 只写“Station 作为 RideRoot 子物体”。本节把 **Collider 的 center/size** 和**退出按钮的可达性**写成硬要求：不写的话“点球入座”在几何上根本点不到。
- 计划 §7 S2 的文件清单里没有探针；本节按 S2 的风险 1 加了一个可删的 Labs 探针，让这条风险一次 Build & Test 就有答案。
- `BallRideStation.Start()` 先把驱动门关上；计划没写这一条，但不写就挡不住站着的玩家用 WASD 遥控球。
- 输入拆成 `SetRideGate` / `SetRoundGate` 两把闸门后，计划 §4 的输入关闭示例已同步改写为 `SetRoundGate(false)`（与 S1 那节的记录一致）。

### 没有改动的东西

本节只新增三个文件，各带 `.meta`，新目录 `Runtime/Riding/` 也带 `.meta`，GUID 全仓唯一（与既有 296 个 GUID 无冲突）。`DesktopMoveInput` 是后来拆两把闸门时才改的，不在本节，见 S1 的“输入闸门”。

## 吸附宿主

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-14。本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §3、§7 S3 与 §8 的风险 4。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test、没有真机。

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
| `activationRadiusM` / `releaseRadiusM` / `scanIntervalSeconds` | 1.6 / 2.5 / 0.05 起 | 释放半径必须大于激活半径，`Start` 会强制 |

每件道具的运行期状态只有三种：`0` 自由、`1` 已附着、`2` 已隐藏（附着失败的后备）。`1`/`2` 都是终态，永不再提交——和 `PlaygroundProp` 的 `consumed` 同一语义，只是从“每件一个 Bool”变成“宿主一个 int 数组”。

### 每帧做什么

1. **先消费事件环**（无论是否在收集）：用自己的绝对游标从上次读数读到 `GetAbsorbedEventCount()`。环容量 16、按绝对下标读（`KatamariBallMotor.cs:1523-1531`、`:1542-1545`、`:1567-1574`）；游标落后超过容量时**明确记一行**丢了多少事件（本次读数与上次读数的差 > 容量即溢出），而不是假装没丢。
2. **再每 0.05 s 扫一次邻近**：一次遍历同时做两件事——超出 `releaseRadiusM` 的道具 `WithdrawCandidate`；`activationRadiusM` 内的自由道具按**距离从近到远**插入一个 8 格缓冲，只在 `GetPendingCandidateCount() < GetPendingCandidateCapacity()` 时提交前几个。最近优先不是微优化：先到先得会把 4 格给数组下标最小的 4 件，球正碰上的那件反而排不进去；而往满队列里提交会被拒并**latch motor 的数据丢失信号**（`KatamariBallMotor.cs:1015-1019`），那是“宿主丢了一个候选”的告警，宿主不该自己造成它。
3. 道具离开释放半径时，只有“这个宿主提交过它”（`lastSubmitTimes[] > 0`）才值得再调一次 `WithdrawCandidate`：队列的真相由 motor 持有，宿主不另存一份“有没有排队”。

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

### 代码证据（本次调用的全部 API）

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
| 对照：lab 的每件道具宿主“查到自己的 id 就隐藏” | `Labs/BallPlayground/PlaygroundProp.cs:144-169` |
| 对照：lab 的激活/释放半径（1.6 / 1.0，释放更小） | `PlaygroundProp.cs:50`、`:57` |

lab 那对半径是**反的**（激活 1.6、释放 1.0，等于排队当帧就撤回）。本宿主没有照抄：`releaseRadiusM` 必须大于 `activationRadiusM`，`Start` 里不满足就按 1.25 倍往上调，留出迟滞。

### 未验证（不要当成已验证）

1. **没有编译。** U# 全量编译、Console 无错都还没发生。
2. **`Transform.SetParent(Transform)` 在 Udon 的可用性**：全仓此前没有任何 `SetParent` 调用（计划 §8 风险 4 已把“`SetParent`/局部写入未编译验证”记在案）。这是本脚本最需要第一轮编译确认的一行。
3. **`Collider[]` 序列化**：单个 `Collider` 字段有先例（`PlaygroundProp.cs:39`，BallPlayground 实跑过），数组形式的抽象类型引用没有先例。
4. **`Transform.lossyScale`**：Udon 是否暴露未验证；不暴露会在编译期报错，替代是给每件道具再序列化一个 scale。
5. **`Vector3.Distance`**：本仓只在 motor 的日志里出现过（`KatamariBallMotor.cs:1615`），本脚本用它在第一次附着时检查附着根位置。
6. **附着根的位置约定**：`BallVisualRollRoot` 的世界位置由场景层级决定（motor 只写 `ballPhysicsRoot.position` 与 `ballVisualRollRoot.rotation`，`:931-940`），所以“根 = 球刚体位姿”依赖场景把滚动根放在球心、且 `AttachmentRoot` 局部为 0。宿主只做一次 5 cm 容差的位置告警，旋转没有检查。
7. **每帧成本**：一次扫描 = 每件道具一次平方距离（切片约 100 件、灰盒 448 件），每 0.05 s 一次；每次扫描最多再读 4 次队列计数。真实 Udon 开销没有量过。
8. **两条后备路径都没有被执行过**：环溢出只打日志，隐藏只在数据/接线错误时发生；正常一局只走“附着成功”。
9. **触发顺序**：宿主与 motor 的 `Update` 顺序没有保证，事件可能晚一帧被消费；附着写的是局部位姿，晚一帧不会造成漂移，但快滚时的视觉差没有观察过。
10. **ClientSim / Play Mode / Build & Test 全部 NOT_RUN。** 计划 §7 S3 的验收（吃 5 件、5 件仍可见、随球滚动、不随球芯变大、没有隔空粘上）一条都还没有数据。

### 与计划的差异（留给 Lead）

- 计划 §7 S3 写“`absorbed` 与按 definition 的计数 +5”。本实现按 **kind** 计数（`GetAttachedCountForKind`），没有单独的 definition 计数列：`kinds[]` 现在就是定义类别的文本，接第二关时把过滤集合换成 `categories.json` 的 definition 集合即可（计划 §4 尾段同义）。要按 definition 精确计数就得再加一列 `definitionIds[]` 和一个按 definition 的计数——那属于第二关的数据，本次没加。
- 计划 §3 写“每件道具 state 0/1”，本实现多了 `2 = 隐藏`（附着失败），并把这个数量单独暴露，不混进 `attached`。
- 计划没写“释放半径必须大于激活半径”；这是 lab 设置反了之后总结的约束，写进了 `Start` 的 clamp。
- 计划 §7 S3 的文件清单只有这一个脚本；`Labs/StoryLevel/StoryLevel.unity` 的道具数组接线仍是 Unity 操作者的活（那批道具现在还没有 `PickupFieldHost` 组件）。

### 已处理的既有文件

1. **本文件的 H1 与首行状态**以前只写“S1 的两个运行时脚本”；现在标题与状态表覆盖输入、球视图、骑球、吸附宿主、剧情回合五个模块。
2. **`Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md` §0 第 3 条与 §3「没接」**：吸附宿主源码落地后只剩“场景没接（`AttachmentRoot`、道具行）”成立，计划那两段已按此更新。
3. **motor 不需要改**：`GetCenterWorld`、`GetVisualRollRotation`、事件环的四个读口、`SubmitCandidate` / `WithdrawCandidate`、两个队列计数口都已存在；附着根也不进 motor（符合 `Docs/Design/BallMotor/README.md:162` 的分工）。没有需要补的 getter。

### 验证命令与本次结果

```bash
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests
```

2026-11-14 本次：第一条 `PASS: 61 tasks; both release DAGs acyclic; source hashes verified.`；第二条 `Ran 270 tests ... OK`。Unity 侧编译、Play Mode、Build & Test 均 **NOT_RUN**（本 agent 不是 Unity 操作者）。

### 没有改动的东西

S3 只新增 `Runtime/Pickup/PickupFieldHost.cs` 与同名 `.meta`（GUID 新生成，全仓唯一），外加本节（追加在本文件末尾）。没有改 `KatamariBallMotor`、`AttachmentMath`、`PlaygroundProp`、asmdef、场景或任何既有代码文件。

## 剧情回合（S4）

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-14。本节对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §4、§7 S4。没有编译、没有 Play Mode、没有 Build & Test。

### 一个脚本

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Story/StoryRoundController.cs` | 一局剧情的状态机与 HUD：`Idle → Running → Presenting → Result`；到点冻结输入/收集/候选，按结算瞬间的碰撞直径或确认件数查星，显示星数与一句评语 | 不交分、不写账本（`bank` / `score_channel` / `ledger_write` 都不碰）、不改球径、不评过程、不重试（S6） |

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

### 未验证（不要当成已验证）

1. **没有编译。** U# 全量编译、Console 无错都还没发生；`Time.time`、`Text.text`、`Mathf.Floor` 这些调用在这个组合里没有编译记录（`Text` 有 BallPlayground 的 HUD 先例）。
2. **五句评语是序列化字符串**：默认值是否随场景实例正确序列化、改值后是否只出现在结果卡，未观察。
3. **冻结顺序的运行时效果**：宿主与控制器 `Update` 顺序没有保证，事件可能晚一帧；到点后“不再吸收”只在代码顺序上成立，没有实测。
4. 计划 §7 S4 的验收（0/1/2/3 星各触发一次、到点后不再吸收、`GetBankedScoreUnits()==0`）一条都还没有数据。
