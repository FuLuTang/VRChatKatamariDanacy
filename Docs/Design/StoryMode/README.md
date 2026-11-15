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

## 再试一次（S6 剩余的一半：结果卡重试）

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-14。本节作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test。对应[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §4「没有重试接缝」与 §7 S6 的后半段；motor 的重置接缝（`TryResetForNewRound`）已在 [BallMotor README](../BallMotor/README.md) 的「重试（reset）」一节，本节只接调用方与道具复位。

S4 表格里「不重试（S6）」一句已被本节取代：控制器现在只在结果卡上接受一次重试。

### 三个文件

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Story/StoryRoundController.cs`（改） | `RequestRetry()`：状态闸门 → motor 复位 → 道具复位 → `BeginRound()`；重试次数写进 HUD 目标行 | 不交分/不奖励（不调 `TryBank`/`TryAwardScore`）、不写账本规则、不改球径与位姿（全交给 motor 的接缝）、不自己搬道具（交给宿主） |
| `Runtime/Pickup/PickupFieldHost.cs`（改） | `Start` 记录每件道具的父物体/局部位姿/激活态；`ResetField()` 把它们放回去、重新可收集 | 不重置 motor、不碰回合闸门、不回退事件游标、不恢复道具 `localScale` |
| `Runtime/Story/StoryRetryButton.cs`（新） | 世界内按钮的 `Interact()` 与 uGUI 入口 `OnRetryPressed()`，两者都只调 `controller.RequestRetry()` | 不持有回合状态、不判断能不能重试、不隐藏/显示自己 |

三个都是 `UdonSharpBehaviour`，同步模式 `BehaviourSyncMode.None`，没有 static 字段、属性、struct、泛型、List/LINQ、try/catch、`??`、字符串插值或方法重载。

### 状态闸门：只在结果卡上允许

`RequestRetry()` 只在 `StateResult` 接受，其他状态一律拒绝并打一行 warning（`retry ignored: no result card is showing (state=...)`），结果卡与回合都不动。

- `Presenting` **刻意拒绝**：那时结果卡还不存在，按钮也无从谈起；若允许，重置会静默跳过“呈给国王”的停留，玩家看到的还是呈递文本。
- `Idle` / `Running` 拒绝：没有结果可重试。
- 拒绝不改状态、不写 HUD：`state` 保持 `StateResult`，`resultText` 保持原样，所以“按钮没反应”只会出现在控制台日志里，不会出现半张卡。

按钮 GameObject 是否只在结果时显示，是场景自己的选择；本脚本不做显隐，`RequestRetry()` 的状态闸门已经能挡住误按。

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

- 先把本宿主提交过、还在队列里的 id 撤回（`WithdrawSubmitted()`）；调用约定是 motor 重置已接受、motor 自己已清空候选，所以这里多为无害的 no-op。
- 只动状态为 `1`（已附着）或 `2`（已隐藏）的道具：`SetParent(original)` → 写 `localPosition`/`localRotation`（与 attach 同一顺序）→ `SetActive(原始激活态)`。隐藏的道具因此重新出现；原本激活的道具保持激活。
- 接了 collider 的行无条件 `enabled = true`。attach/hide 只关不开，所以复位必须开；代价是“authoring 时就关着的 collider”会被复位打开，没有为它再存一个标志。
- 状态回 `0`（自由），所有 `lastSubmitTimes` 清零（motor 已清队列，提交记录没有意义）。
- `attachedCount`、`hiddenCount`、`lastHiddenReason`、`lastHiddenItemId`、`kindTallyCount` 归零；`nextScanTime = 0` 让下一帧立刻重新扫描。
- **不回退** `seenEvents`：motor 的绝对吸收计数跨重置单调（`absorbedEventCount` 只增不减，[BallMotor README](../BallMotor/README.md) 的「吸收事件环的绝对计数保持单调」），保留游标就不会重放旧事件，新回合的事件从同一绝对号继续。重置后 `absorbedEventRingCount = 0`，旧绝对下标一律返回 `""`，宿主跳过即可。
- 保留 `loggedLostEvents`、`hiddenLogCount` 这类整会话诊断闩锁，理由与 motor 保留日志上限相同：无限重试不能把控制台刷爆。
- 返回 `bool`：`propStates == null`（`Start` 未跑）时 false 且什么都没碰；空场地也算成功（true）。
- 不记录、不恢复 `localScale`：attach 从不写 scale，`SetParent` 以“保持世界缩放”方式改 `localScale`，回到原父物体即恢复原外观。
- 不碰 `SetCollecting` 闸门：`BeginRound()` 负责重新打开。

### StoryRetryButton：两种按法，一个入口

**世界内按钮**：独立 GameObject + 一个 Collider + 本行为，`controller` 指向场景里的 `StoryRoundController`。准星对着它按鼠标左键（桌面交互是左键），`Interact()` → `RequestRetry()`。

**uGUI 按钮**：同一个行为也能从 Canvas `Button` 触发。官方 UI 事件白名单把 `UdonBehaviour` 的 `SendCustomEvent` 与 `Interact` 都列为可调用目标（<https://creators.vrchat.com/worlds/udon/ui-events/>，2026-11-14 抓取，页面最后更新 2026-10-07），所以接线不需要额外组件：

1. `Button` 的 `OnClick()` 加一项。
2. 对象槽拖入挂 `StoryRetryButton` 的那个 GameObject（Inspector 里选 `UdonBehaviour` 组件）。
3. 函数选 `UdonBehaviour.SendCustomEvent`。
4. 参数字符串填 `OnRetryPressed`（大小写一致，没有别的参数）。

`SendCustomEvent` 是按字符串分发到这个行为上的公开方法，所以入口是 `public void OnRetryPressed()` 而不是属性或带参方法。**在本仓未验证**：`Docs/` 里没有 UI 事件白名单的本地副本，本节只引官方文档；这个仓库也还没在 Unity 里连过 Canvas 按钮、没按过。世界内 `Interact` 的路径与 `BallRideExitButton` 同形，但那个脚本本身也还没编译。

### 场景怎么接（Unity 操作者）

1. 结果卡所在 Canvas（或世界空间面板）加一个 `Button`；世界内按钮则新建独立 GameObject + Collider + `StoryRetryButton`，放在可点到的地方。
2. `controller` 拖场景里的 `StoryRoundController`。
3. uGUI 按上一节的四步接 `SendCustomEvent("OnRetryPressed")`；世界内按钮不用再接线。
4. 不需要改 `StoryRoundController` 的既有字段：`motor`、`input`、`pickupHost`、四个 Text、关卡数据与评语全部保持原名（本次只加私有字段与方法，没有加序列化字段）。
5. 道具行照旧：`propTransforms` 等并行数组就是复位用的原始来源，宿主自己抓，不需要额外数组。

### 未验证（不要当成已验证）

1. **没有编译**：U# 全量编译、Console 无错都没发生；`Transform.parent`、`Transform.SetParent(null)`、`new Transform[n]` / `new Quaternion[n]` 的运行时分配、`GameObject.activeSelf` 在 Udon 的可用性只是按暴露表推断（`Transform.parent`/`SetParent` 在暴露表内，`SetParent(null)` 与这两种数组的运行时 `new` 没有先例）。
2. **`SetParent(null)`**：道具如果 authoring 在世界根（`parent == null`），复位会 `SetParent(null)`。Unity 语义是“变成根物体”，但 Udon 的 extern 是否接受 null 没有验证。
3. **uGUI 接线**：`Button OnClick → UdonBehaviour.SendCustomEvent("OnRetryPressed")` 的依据是官方文档白名单；本仓没有本地副本，也没有实机按过。`SendCustomEvent` 是否出现在 Button 函数下拉里、参数是否必须非空，都要第一次接线时确认。
4. **Start 顺序与捕获时机**：`GetSemanticDiameterWorldM() > 0` 作为“motor 已 `Start`”的判据来自 BallMotor README 的观察，不是 Unity 的保证；延迟到第一帧捕获的路径没有运行时记录。
5. **motor 拒绝时的日志**：`reason`/`ledgerFailure` 只在代码里组合，没有实际触发过（正常单机路径 `reason=0`）。
6. **道具复位后的视觉**：collider 无条件开启、`localScale` 不写、隐藏道具重新激活，这三条都没有 Play Mode 观察；`SetParent` 回原父物体后局部位姿是否精确回到原位也没有数据。
7. **计数从 0 开始**：`GetAttachedCountForKind` 归零后 HUD 应显示 0；但宿主与控制器 `Update` 顺序、以及重置当帧是否还会消费一条迟到事件，没有实测（按环形契约，旧事件已清成 `""`，应被跳过）。
8. **多人/同步**：`BehaviourSyncMode.None`，整个切片是单机；重试不广播。
9. **Build & Test**：计划 §7 S6 的验收（失败→重试→球回 0.30 m、计时归零、道具全回来、计数从 0）一条都还没有数据。

### 验证命令与本次结果

```bash
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests
```

2026-11-14 本次：第一条 `PASS: 61 tasks; both release DAGs acyclic; source hashes verified.`；第二条 `Ran 270 tests ... OK`。Unity 侧编译、Play Mode、Build & Test 均 **NOT_RUN**（本 agent 不是 Unity 操作者）。

### 没有改动的东西

只改 `Runtime/Pickup/PickupFieldHost.cs`、`Runtime/Story/StoryRoundController.cs`，新增 `Runtime/Story/StoryRetryButton.cs` 与同名 `.meta`（GUID `cbba12c7e27849be891c64aadf04ba3b`，新生成、与仓内既有 307 个 GUID 无冲突）。没有改 motor、账本、`DesktopMoveInput`、Riding、Labs、asmdef、场景、`tasks.json` 或任何其他文件；`PickupFieldHost`/`StoryRoundController` 的既有序列化字段名与公开签名一个没动，只做了追加。

## 开始流程：Idle → 入座 → Running

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-15。本节对应 ClientSim 实玩暴露的问题：场景一加载就开始计时，世界里没有“开始”，站着的玩家按 WASD 就在驱球。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test。

### 问题与改法

旧版 `StoryRoundController.Start()` 直接 `BeginRound()`：没有门厅、没有开始动作，进世界即计时；场景里没有 Station 时 `DesktopMoveInput` 的两把闸门都是默认开，所以站着的人也能开球。改法是把“开始”交给坐上球这一个动作：

1. `Start()` 只在 `startOnLoad = true`（无 Station 的无头实验）时立刻开局；默认 `false` 时进入 **Idle**：HUD 写目标行与提示行，回合闸门关、收集关，计时不动。
2. 玩家站到球边，准星对着球按**鼠标左键**（桌面交互是左键）→ `BallRideStation.Interact()` → `station.UseStation(localPlayer)`。
3. `OnStationEntered`（本地球员）→ 先开骑乘闸门，再调 `roundController.NotifyRiderSeated()`（站上 `roundController` 字段有接线才调）。
4. `NotifyRiderSeated()` 只在 `Idle` 接受：`BeginRound()` 开回合闸门与收集、记 `Time.time` 起点 → **Running**，这时两把闸门都开，WASD 才驱球。
5. 到点 `SettleRound()`：先关回合闸门、再关收集、再清候选，读冻结值 → **Presenting**（“呈给国王”）→ **Result**（星/测量值/评语）。
6. 结果卡上“再试一次”走 `RequestRetry()`（见上一节），世界内退出按钮是 `BallRideExitButton`（离座 → 骑乘闸门关）。

### 每状态的闸门（生效输入 = 两把都开）

| 回合状态 | 回合闸门 `SetRoundGate` | 骑乘闸门 `SetRideGate` | 收集 `SetCollecting` | 剩余行 / 结果行 |
|---|---|---|---|---|
| Idle | 关（`Start()`，`startOnLoad=false`） | 关（Station 的 `Start()`；本地球员入座时开） | 关（`Start()`） | 剩余行清空；结果行显示 `startPrompt`（默认「点击球坐上去开始」） |
| Running | 开（`BeginRound()`） | 入座开、离座关 | 开（`BeginRound()`） | 「剩余 N s」；结果行空 |
| Presenting | 关（`SettleRound()`） | 同上 | 关（`SettleRound()`） | 「剩余 0 s」；结果行 `presenterLine` |
| Result | 关 | 同上 | 关 | 结果卡（星/测量值/评语） |

所以：Idle 里谁都开不动球（回合闸门关，而且站着时骑乘闸门也关）；Running 里站着的人开不动球（只剩骑乘闸门把关）；到点后无论坐着还是站着都不会再吸收。

上表补上了本文前面「输入闸门」小节里 `SetRoundGate` 的“关”只写了 `SettleRound()` 的遗漏：`Start()` 在 `startOnLoad=false` 时也会关它。那个小节的“每个闸门只有一个所有者、默认开、互不覆盖”不受影响。

### 已决定的边界

- **中途离座：回合照常跑。** 离开是玩家的选择，暂停会把计时变成玩家可随意停的资源。输入本来就被骑乘闸门关掉（适配器清零两轴、球刹车），所以离座后计时继续、球不走，到点照样出结果卡。再坐回去只是重新开骑乘闸门（`NotifyRiderSeated()` 在 Running 被忽略），不会重开回合。
- **`NotifyRiderSeated()` 在 Result：什么也不做。** 重试只能按结果卡上的按钮：`BeginRound()` 单独跑不会复位球、账本行、回合 id 和道具，坐下就重开等于“新回合配旧球”；`RequestRetry()` 才是先 motor 复位、再道具复位、最后 `BeginRound()` 的那条路。Running/Presenting 同样忽略，否则坐下会静默重开计时。
- **`startOnLoad = true`** 保留旧行为，给没有 Station、没有人可坐的无头/键盘实验；有 Station 的场景必须保持 `false`。
- **站不接 `roundController` 时**照样能坐、能开关骑乘闸门、能显示提示（StationPlatform 实验就是这样）；但场景里有 `StoryRoundController` 就必须把站上的 `roundController` 指过去，否则控制器永远停在 Idle、回合闸门一直关，表现为“坐上去也开不动”。
- 提示行落在**结果行**（结果卡将来出现的那一行）；结果行没接时退回**剩余行**，并把剩余行清空以免显示一条没开始的倒计时。

### 场景怎么接（Unity 操作者）

1. `BallRideStation.roundController` 指向场景里的 `StoryRoundController`；不接 = 见上一条。
2. 有 Station 的可玩场景：`StoryRoundController.startOnLoad` 保持默认 `false`。无头实验勾上。
3. HUD 照旧四行（目标/当前/剩余/结果）；`startPrompt` 想换文案就在场景实例上改这个字符串，不用改代码。
4. 既有序列化字段名与公开签名一个没变（只追加 `startOnLoad`、`startPrompt` 与 `NotifyRiderSeated()`，站上只追加 `roundController`），旧场景实例里新字段取默认值（`false` / 默认文案）。

### 未验证（不要当成已验证）

1. **没有编译**：U# 全量编译、Console 无错都没发生；跨命名空间类型引用（`Riding` 里的站引用 `Story` 里的控制器）与新增的站序列化字段在 U# 下的编译没有记录。
2. **ClientSim / Play Mode / Build & Test 全部 NOT_RUN**：本节描述的“加载后停在 Idle、左键上球才开始计时、坐着 WASD 才驱球、到点出结果卡、重试/退出可用”一条都还没有运行时数据。
3. **`Interact` → `UseStation` → `OnStationEntered` 的同帧顺序**：代码按“先开骑乘闸门再通知”写，但“通知当帧球就能动”没有观察（桌面按住 W 时 VRChat 只在值变化发轴，可能要先松再按）。
4. **Idle 的提示行实际显示**：四行 Text 的排版、清空剩余行的效果、`startPrompt` 在场景实例上的序列化，都没有 Play Mode 观察。
5. **中途离座后的结果卡**：站着到点时结果卡是否仍可读、重试后是否必须先坐回去才动，未实测。
6. **已经坐在站里进世界**（例如复活回座位）时没有人再按一次左键：站只在 `OnStationEntered` 通知，没有“加载时已在座”的路径，所以会停在 Idle 且骑乘闸门关着，要离座再坐一次才会开始。这是已知缺口，不是已实现的行为。

## 结果卡显隐：按钮只在 Result，卡片从 Presenting 开始

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-15。本节对应实玩暴露的两个问题：场景一加载就看得见“再试一次”（那时还在 Idle，没有回合、没有结果卡），以及 Presenting 那一拍只有一行“呈给国王”、背后没有任何牌面。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test。

### 两个新链接

| 字段 | 拖什么 | 显示于 | 隐藏于 |
|---|---|---|---|
| `retryButtonRoot` | “再试一次”按钮所在对象：uGUI 是挂 `Button` 的那个 GameObject（不是 Canvas，也不是 Text 子物体）；世界内按钮是挂 `StoryRetryButton` + Collider 的那个 GameObject | Result | Idle / Running / Presenting |
| `resultCardRoot` | 结果卡的底板/面板对象（卡面背景） | Presenting / Result | Idle / Running |

两个都是 `[SerializeField] private GameObject`，默认 null；空链接一律跳过，所以没接的场景（含无头实验）行为与改动前完全一样。

### 一个方法写 active 状态

`ApplyStateVisibility()`（`StoryRoundController.cs:738`）是唯一写这两个对象 `SetActive` 的地方，调用点覆盖全部状态变化：

| 调用点 | 进入的状态 | 卡面 | 按钮 |
|---|---|---|---|
| `EnterIdle()`（`:305`） | Idle | 关 | 关 |
| `BeginRound()`（`:388`，含重试成功后的重开） | Running | 关 | 关 |
| `SettleRound()`（`:641`） | Presenting | 开 | 关 |
| `Update()` 的 Presenting→Result 转移（`:409`） | Result | 开 | 开 |
| `RequestRetry()` 开头（`:492`） | 仍为当前状态 | 按当前状态重写 | 按当前状态重写 |

`Start()` 的两个分支分别走 `EnterIdle()` 或 `BeginRound()`，所以加载后的第一次 Start 就会把按钮关掉（按 Unity 生命周期，Start 在第一帧渲染前跑完），正常不会先闪一帧；场景里把按钮对象在 authoring 时就设成 inactive 是双保险。`RequestRetry()` 开头那一次是“重写”：按错状态进来的调用会把按钮重新藏掉，而不是让按钮与状态各说各话。

### 按钮显隐不是规则，`RequestRetry()` 的状态闸门才是

显隐只决定“看得见/按得到”，不决定“能不能重开”。`RequestRetry()` 仍然只在 `StateResult` 接受（其余状态一行 warning 后返回 false，卡面与回合都不动），所以就算某个场景把按钮拖错、或者在 Result 之前用别的方式把它显示出来，也开不了新回合，更不会出现“新回合配旧球”。这也是本节的显隐逻辑可以放心做的原因：它只改表现，规则仍在一处。

### 场景怎么接（Unity 操作者）

1. `retryButtonRoot` 拖 uGUI `Button` 所在的那个 GameObject（`OnClick` 仍按上一节四步接 `UdonBehaviour.SendCustomEvent("OnRetryPressed")`）；世界内按钮则拖挂 `StoryRetryButton` + Collider 的对象。
2. `resultCardRoot` 拖结果卡底板。它从 Presenting 起就显示，所以“呈给国王”那一拍背后有牌面，Result 沿用同一块底板；控制器在这两个状态里只改 `resultText` 的文字，牌面本身由场景决定。
3. **不要把 `resultText` 放进 `resultCardRoot` 的子树**：Idle 会把开始提示（`startPrompt`，默认「点击球坐上去开始」）写进同一条结果行（`resultText` 接了时），而 Idle 时卡面是关的，提示会跟着消失。要“文字压在牌面上”的观感，就让 Text 与底板同级、排在底板之后渲染（同一 Canvas 下的 sibling 顺序），而不是成为底板的子物体。
4. **不要把这两个字段拖成本控制器自己的 GameObject**：Idle 会对它调 `SetActive(false)`，控制器随后也不再运行，回合永远停在 Idle。它们必须是独立对象（通常和卡面一起放在同一个 Canvas 下）。
5. 既有字段与签名一个没变：`motor` / `input` / `pickupHost` / 四个 Text / 关卡数据 / 五句评语 / `startOnLoad` / `startPrompt` 全部原名；`RequestRetry()` 仍是 `public bool` 且行为不变。本次只追加两个序列化字段、一个私有方法，以及类注释里的一段说明。
6. U# 语言约束照旧：没有属性、重载、泛型、`??`、字符串插值、static 字段；`ApplyStateVisibility()` 是无参私有方法，两个 `SetActive` 都是 `GameObject.SetActive(bool)`。

### 与上文的关系（取代一句）

上文「再试一次」一节写过「按钮 GameObject 是否只在结果时显示，是场景自己的选择；本脚本不做显隐」。这句已被本节取代：控制器现在按状态显隐 `retryButtonRoot`。它的后半句仍然成立——真正的规则是 `RequestRetry()` 的状态闸门，显隐只是表现。

### 未验证（不要当成已验证）

1. **没有编译**：U# 全量编译、Console 无错都没发生。`GameObject.SetActive(bool)` 有 `Labs/BallPlayground/PlaygroundProp.cs:167`（BallPlayground 实跑过）的先例，序列化 `GameObject` 字段有同文件 `:38` 的 `visualRoot` 先例，但本文件这个组合没有编译记录。
2. **ClientSim / Play Mode / Build & Test 全部 NOT_RUN**：加载后按钮不可见、Presenting 时底板可见、Result 时底板+按钮都可见、重试后按钮消失，一条都没有运行时观察。
3. **Idle 提示行与卡面的渲染顺序**：见接入第 3 条，纯场景侧排布，没有观察。
4. **`SetActive(false)` 期间按下按钮**：隐藏的对象上 uGUI 不会再派发 `OnClick`，这是本节依赖的机制，但“Result 之前按不到”没有实机记录。

### 验证命令与本次结果

```bash
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests
```

2026-11-15 本次：第一条 `PASS: 61 tasks; both release DAGs acyclic; source hashes verified.`；第二条 `Ran 270 tests ... OK`。Unity 侧编译、Play Mode、Build & Test 均 **NOT_RUN**（本 agent 不是 Unity 操作者）。

### 没有改动的东西

只改 `Runtime/Story/StoryRoundController.cs`（追加两个序列化字段、`ApplyStateVisibility()`、五个调用点与类注释一段）与本节（追加在本文件末尾）。没有改 `StoryRetryButton.cs`、`PickupFieldHost.cs`、motor、Riding、Labs、asmdef、场景、`tasks.json` 或任何其他文件。

## 道具字段的编辑器同步（PickupFieldSync）

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-15。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test、没有 `run_tests`。本节只描述源码与约定。

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

### 未验证（不要当成已验证）

没有 Unity 编译、没有 Play Mode、没有 Build & Test、没有 `run_tests`。`CompileAllCsPrograms` 与 `CopyProxyToUdon` 的时序只是按 `VRC_Katamari/Labs/README.md` 的“先编译再拷贝”排列：若拷贝仍报 `outdated behaviour version`，说明 U# 编译还没跑完，等它结束再运行一次同步（报告里就是这么写的）。`propBounds` 取 Renderer AABB、`propColliders` 只接一个，这两条都还没有运行时观察。
