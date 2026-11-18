# 球的操控：双摇杆坦克式与简单模式

owner 2026-11-18 决定：VR 端用控制器的两根实体摇杆还原原作的坦克式操控，另外提供一个简单模式（仿《从前从前有个块魂》的 Simple）。坐上球后摇杆只负责滚球。Desktop 保留 WASD 前后和转向，Space 留给道具。冲刺（dash）照原作 Charge'n'Roll：按住蓄力、松手出射——VR 用左手扳机，PC 用 Shift（见下文「冲刺」）。

## 实现

| 文件 | 作用 |
|---|---|
| [TankStickMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Input/TankStickMath.cs) | 纯算法：两根摇杆换算成 (forward, turn, lateral)，包括死区和两套模式 |
| [VrStickMoveInput.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Input/VrStickMoveInput.cs) | VR 设备读取：每帧读四个摇杆轴，调用 TankStickMath，再调 `KatamariBallMotor.SetMoveAxes`；**左手扳机**（`InputUse`，只取 `handType == LEFT`）发蓄力的按下/松开。只在 `IsUserInVR()` 时工作 |
| [DesktopMoveInput.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Input/DesktopMoveInput.cs) | Desktop 的 WASD 路径（`InputMove*`，映射不受本设计影响）；Shift 长按蓄力、松开出射 |
| [DashMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/DashMath.cs) | 纯算法：蓄力分档时间、原地旋转阈值、出射倍率/时长/冷却、能量与 huff；motor 每固定步推进一次 |
| [TankStickTests.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/Input/TankStickTests.cs) | 摇杆映射的 EditMode 测试（10 条） |
| [DashMathTests.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/BallMotor/DashMathTests.cs) | 冲刺的 EditMode 测试（14 条：分档、旋转阈值、未蓄满不出、出射时长、冷却、能量、回充、huff、重置、坏输入） |

两个输入行为喂的是同一个入口 `SetMoveAxes(forward, turn, lateral)`。motor 的约定是：forward +1 向前，turn +1 右转（俯视顺时针），lateral +1 向右横移。两者共用同样的两道门：骑乘门归 `BallRideStation`，回合门归 `StoryRoundController`；蓄力的按下/松开也走这两道门（站着或回合结束后按扳机不会蓄力，关门前已蓄的会被取消）。

## 坦克式映射（默认，`controlMode = 0`）

每根杆的 y 各当一条履带：forward = (L+R)/2，turn = (L−R)/2，lateral = (Lx+Rx)/2。

| 动作 | 原作输入 | (forward, turn, lateral) |
|---|---|---|
| 前进 / 后退 | 两杆同推前 / 同拉后 | (±1, 0, 0) |
| 原地右转 / 左转 | 左前右后 / 左后右前 | (0, +1, 0) / (0, −1, 0) |
| 单推左杆 | 绕右侧转弯 | (0.5, +0.5, 0) |
| 单推右杆 | 绕左侧转弯 | (0.5, −0.5, 0) |
| 横移 | 两杆同向左 / 右推 | (0, 0, ±1) |
| 斜前推 | 两杆同向斜前 | 前进加横移，例如 (0.71, 0, 0.71) |
| 冲刺 | 原作：左前右后与左后右前快速交替（Charge'n'Roll，交互推拉） | 本项目照原作逻辑、改用长按：VR 左手扳机 / PC Shift，蓄满 2.0–3.2 s 后松手出射；不在本表的三轴里，见「冲刺」节 |
| 快速转身 | 初代 L3+R3：王子跳到球的另一侧，朝向反转 180° | 本项目不做：摇杆按下在 VRChat 里是 Action Menu |
| 爬墙 | 贴墙后两杆持续前推 | 普通前进，走现有爬墙路径 |

这是连续差速的工程近似，没有复刻原作的斜向混合和转弯半径。

**死区**：每根杆按径向处理，默认 0.2，边缘外的部分重新缩放回 [0,1]；InputManager 自己还有一层 0.19 的死区。超过 [-1,1] 的读数先夹到范围内；出现 NaN 或无穷时整帧拒绝，保留上一帧的轴值。


## 简单模式（`controlMode = 1`）

左杆管二维滚球（y 前后，x 横移），右杆的 x 管转向，右杆的 y 不用（VR 里由头显负责看）。这对应 Once Upon a Katamari Simple 的「左杆往任意方向滚、右杆横向转」。冲刺与模式无关：左手扳机/Shift 在两种模式下都能用（简单模式里没有手柄交替手势，长按就是它的入口）。模式在什么地方切换还没有定；公开入口已留好：`SetControlMode(int)` / `GetControlMode()` 和序列化字段 `controlMode`。大厅墙面按钮原计划加在 2×2 关卡按钮旁，因大厅面板要整体重做而搁置。

## 来源

- 初代与 WLK 操作：[psxdatacenter SLPS-25467](https://psxdatacenter.com/psx2/games2/SLPS-25467.html)（Shift = 一推一拉，Dash = 两杆快速上下交替，Quick turn = L3+R3，Roll left/right = 两杆同向横推），[StrategyWiki 初代控制表](https://strategywiki.org/wiki/Katamari_Damacy/Controls)（搜索摘要可读，正文 403），[GameFAQs FAQ](https://gamefaqs.gamespot.com/ps2/918766-katamari-damacy/faqs/32721)（搜索摘要）。
- 原作冲刺机制（gacha 窗口、5/6/7/8 档位、3 次起旋转、能量 240 帧与 huff 8 s）：[REROLL Speedrun Notes · Boosts](https://shookietea.github.io/reroll-notes/mechanics/boosts/boosts/)（全文可读，属社区资料，不是官方）；原作文档汇编见 [ORIGINAL_BALL_INERTIA 第 5 行](../../Research/KatamariBallRules/ORIGINAL_BALL_INERTIA.md)。**触发方式是我们的适配**：原作是两根摇杆交替推拉，VR 手柄上照搬不了，owner 2026-11-18 定为「左扳机 / Shift 长按蓄力、松手出射」；出射倍率/时长/冷却与旋转速率是我们的设计值，见 [CONTACT_MODEL_V2 §3.8](../BallMotor/CONTACT_MODEL_V2.md)。
- 简单模式：[Keymap · Once Upon a Katamari](https://keymap.io/controls/once-upon-a-katamari/)（全文可读，第三方站点）。

## VRChat 平台事实

| 事实 | 依据 |
|---|---|
| `InputMoveHorizontal/Vertical` 对应左杆；`InputLookHorizontal` 对应右杆 x，但**只在没开 Comfort Turning 时**才有；`InputLookVertical` 对应右杆 y | 官方 [Input Events](https://creators.vrchat.com/worlds/udon/input-events/) |
| Udon 可以调用 `Input.GetAxisRaw`；VRChat 工程的 InputManager 定义了 `Oculus_CrossPlatform_{Primary,Secondary}Thumbstick{Horizontal,Vertical}`（本仓 `ProjectSettings/InputManager.asset`：dead 0.19，Vertical 带 invert）；SaccFlight 等载具用的也是这组轴 | 同上官方页；[SaccGroundVehicle.cs](https://github.com/Sacchan-VRC/SaccFlightAndVehicles/blob/master/Scripts/SaccGroundVehicle/SaccGroundVehicle.cs) |
| 这组轴名不跟随玩家在 VRChat 里改的键位；菜单打开期间读不到输入 | Creator Library `input-events.md`（社区整理，置信度 Medium）；官方 Input Events「Input Detection in VRChat Menus」 |
| `InputUse`：Desktop = 鼠标左键，VR = 扳机（trigger）。`UdonInputEventArgs.handType` 给出 LEFT/RIGHT（桌面键盘算 LEFT、鼠标算 RIGHT；手柄两个扳机都报 RIGHT 是已知官方 bug）。它是**玩家级**事件，和 `Interact()`（对象级，要准心对着有 Collider 的对象）是两回事 | 官方 [Input Events](https://creators.vrchat.com/worlds/udon/input-events/)；事件签名见官方 [UdonSharp Events](https://udonsharp.docs.vrchat.com/events/)；手柄 handType 见[官方反馈帖](https://vrchat.canny.io/bug-reports/p/inputuse-inputgrab-events-have-incorrect-handtype-for-gamepad-triggers) |
| **冲突**：同一次扳机按下会**同时**发 `InputUse(true)` 和（准心指着可交互对象时）`Interact()`；社区实测文用日志确认“InputUse 不看你指着什么都会发”。所以蓄力/出射期间准心若对着载具的下车面板等 Interact 按钮，会顺带按下它——已缓解：骑乘时蓄力/出射段的 Interact 被忽略（见「冲刺」节） | [UdonSharp Interact vs Input Events（UhiyamaLab）](https://uhiyama-lab.com/en/notes/vrchat/udonsharp-user-interaction/#basic-interact-event)；官方 Input Events 与 [UdonSharp Events](https://udonsharp.docs.vrchat.com/events/) |
| 菜单（主菜单 / 快捷菜单 / 文本输入）打开期间 Udon 读不到输入；**关闭菜单时 Udon 会把仍按住的 Unity 输入重新“按下”**，所以 PC 端长按 Shift 跨菜单可能被补一次边沿（平台行为，不是我们的 bug） | 官方 Input Events 同页「Input Detection in VRChat Menus」 |
| Udon 可以调用 `Input.GetKey` / `GetKeyDown`（官方列出）；SDK 3.6.1 的 ClientSim 曾让 `Input.GetKey` 恒返回 false（真机不受影响，官方确认是 ClientSim bug 并已修）。本项目 SDK 3.10.5 晚于修复，但 **ClientSim 里 Shift 蓄力未验证** | 官方 Input Events 同页「Unity Input Methods and Properties」；[ClientSim issue #117](https://github.com/vrchat-community/ClientSim/issues/117)、[官方反馈帖](https://feedback.vrchat.com/sdk-bug-reports/p/sdk-361-broke-inputgetkey) |
| 坐在 Station 里是否屏蔽 `InputUse`：官方文档只列「菜单打开时屏蔽」，**没有**座位屏蔽输入事件的条目；Station 只影响移动（PlayerMobility），另有已知缺陷是座位内 `OnPlayerTriggerEnter/Exit/Stay` 不触发（与输入事件不是同一族）。本仓 Station 实验的 **PC 日志**里坐姿下 `InputJump` 正常触发（`Docs/Evidence/StationPlatform/vr_checklist.md`，「坐姿 Jump 不退座 PASS」）；**VR 左扳机在座位内未真机验证** | 官方 Input Events 与官方 [VRC Station](https://creators.vrchat.com/worlds/components/vrc_station/)；[Stations break trigger events](https://vrchat.canny.io/bug-reports/p/stations-break-trigger-events)；本仓证据文件 |
| PC 为什么选 Shift 而不是照用左键（`InputUse`）：左键在 PC 上同时是 UI/交互键，长按蓄力会与点面板、点世界对象纠缠；Shift 是桌面跑步键，坐着（Immobilize）时移动被禁、Shift 不驱动任何移动，且 `Input.GetKey` 读键盘原始状态。代价：手柄 PC 玩家没有 Shift（手柄两个扳机都报 RIGHT），v1 不支持手柄蓄力 | 官方 [VRC Station](https://creators.vrchat.com/worlds/components/vrc_station/)；官方 Input Events；[手柄 handType 反馈帖](https://vrchat.canny.io/bug-reports/p/inputuse-inputgrab-events-have-incorrect-handtype-for-gamepad-triggers) |
| Station 的 Immobilize / Immobilize For Vehicle 让玩家坐着时不能移动（左杆的 locomotion 不起作用）；StoryMode 场景里的值是 `PlayerMobility: 1`、`disableStationExit: 1` | 官方 [VRC Station](https://creators.vrchat.com/worlds/components/vrc_station/)；`StoryMode.unity:913` |
| 右杆 x 默认用来转身（Oculus 键位 Right Thumbstick = Turn）。坐在 Immobilize 座位上时，VRChat 自己的右杆转向（平滑或 snap）是否仍会旋转视角，官方文档没写，**未验证**。座位会固定玩家位置，头部转向不受影响 | [Oculus Touch 键位](https://docs.vrchat.com/docs/touch)；仓内 `Docs/Evidence/StationPlatform/vr_checklist.md` 中该项为 NOT_RUN |
| Desktop 不受影响：`VrStickMoveInput` 只在 `IsUserInVR()` 时读轴、推轴 | 代码 |

**右杆转向冲突的处理办法**：不靠 Udon 事件，直接读轴，所以无论 Comfort Turning 开不开都能拿到右杆的值。如果 VR 复测发现坐下后右杆 x 还会转动视角，可选：(a) 接受这个现象（视角转了，但球的朝向由我们控制）；(b) 让玩家关掉 Comfort Turning、把转向灵敏度调到 0——这需要 owner 决定是否写进教学；(c) 改用简单模式。Udon 没有关闭平台转向的 API（Creator Library 和官方页都没有找到）。

## 装配与接线

已接好：
- `DesktopMoveInput` 的两个 `InputMove*` 在 `IsUserInVR()` 时直接 return，所以 VR 下只有 `VrStickMoveInput` 写 motor 的轴，Desktop 和 ClientSim 不受影响。
- `StoryRoundController` 新增 `vrInput` 字段。原先三处 `input.SetRoundGate(...)`（等待入座时关门、`BeginRound` 开门、结束铃关门）旁边各加了一行 `vrInput.SetRoundGate(...)`。
- `HubAssembler.Run` 在配置回合之后调用 `AssembleVrStickInput`：若场景没有 `VrStickMoveInput`，就新建根物体 `VrStickMoveInput` 并挂上组件；然后把 `motor` 指向场景里唯一的 `KatamariBallMotor`，把 `round.vrInput` 指向这个组件。`VerifyTables` 里的 `VerifyVrStickInput` 会回读 Udon 变量表（`motor/controlMode/deadzone`、`vrInput`）和两个引用。`AllMapsHubAssembler` 的自检要求场景里恰好有 1 个 `VrStickMoveInput`；`HubRoomWiringCheck` 检查数量、`motor` 和 `round.vrInput`。
- 骑乘门：`BallRideStation` 有 `vrInput` 字段，`SetRideGate` 对 `input` 与 `vrInput` 各自判空调用，所以没接 desktop 输入时 VR 的门照样跟着入座/离座开关。`RideUxAssembler` 把 `BallRideStation.vrInput` 写成场景里唯一的 `VrStickMoveInput`，并在 `VerifyUdonTables` 里回读；场景里还没有这个组件时只记 note、不写空值，有两个以上时报 PROBLEM。`AllMapsHubAssembler` 在 `HubAssembler.Run`（它创建 `VrStickMoveInput`）**之后**跑骑乘装配，再检查 `BallRideStation.vrInput` 就是那一个组件，不是就报 PROBLEM。

Unity 操作员步骤：
1. `refresh_unity`，确认 `read_console` 为 0 错误；`VrStickMoveInput.asset`（UdonSharp 程序）和 `.meta` 由 Unity 生成。
2. 在 hub 场景跑 Hub 装配（或 All-Maps 装配），报告应没有 `VrStickMoveInput` / `vrInput` 相关的 PROBLEM；若提示变量缺失，按提示保存、重开场景、再跑一次。
3. 跑 `VRC Katamari/Hub/Check Room Wiring`，结尾应为 `0 problem(s)`。
4. ClientSim 下 `IsUserInVR()` 为 false，`VrStickMoveInput` 什么都不做，Desktop 回合照旧，可按 DEBUG_DRIVE 验收。

## owner VR 复测清单

坐上球，回合进行中，分别在开、关 Comfort Turning 时各测一遍。

| 操作 | 预期 |
|---|---|
| 两杆同推前 / 同拉后 | 球直线向前 / 向后滚 |
| 左前右后 | 原地右转（顺时针） |
| 左后右前 | 原地左转 |
| 只推左杆 | 边前进边向右转弯，速度约一半 |
| 只推右杆 | 边前进边向左转弯 |
| 两杆同时向左 / 向右 | 球向左 / 向右横移，朝向不变 |
| 两杆一起斜前推 | 斜着前进，不转向 |
| 松开两杆（摇杆轻微漂移） | 球不被推动 |
| 按住左扳机不放 | 日志 `[input] dash charge begin`；前 1.2 s 球停住不动，之后原地旋转、不前进 |
| 按住约 1 s 就松开（小球档要 2.0 s） | 日志 `[input] dash released, charge not full`；球不加速 |
| 按住 2.0 s（0.3 m 起步球）再松开 | 日志 `[input] dash fired`；球朝当前朝向加速（约 1.6×）约 0.45 s |
| 出射后 1.2 s 内再按住 | 日志 `[input] dash charge refused (huff, cooldown or already charging)`；不蓄力 |
| 连续蓄满 3 次以上 | 能量耗尽后日志出现 refused；随后 8 s 内按扳机都不能蓄力（huff） |
| 右扳机按住 | 无反应（只取左手 `handType`） |
| 蓄力/出射时准心指着下车面板并按扳机 | 面板不响应本次按下（`RideDashInteractMath` 忽略）；松开、dash 结束后再点两次能正常下车 |
| 蓄力时准心指着结果卡重试（回合结束后不会出现蓄力，作参考） | 同上，重试不会在 dash 中触发 |
| 右杆左右推（Comfort Turning 开） | 球转向；记录视角有没有额外 snap 一下 |
| 右杆左右推（Comfort Turning 关） | 球转向；记录视角有没有跟着平滑转 |
| 切到简单模式（操作员写 `controlMode=1` 或调 `SetControlMode(1)`） | 左杆往哪推球就往哪滚，右杆左右推转向，右杆上下推无反应 |
| 离座或 Respawn | 球立即停止受推 |
| Desktop 玩家（坐着按住 Shift 约 2.5 s 再松开） | 日志先 `dash charge begin` 再 `dash fired`，球朝朝向加速；按住 1 s 松开只出现 `dash released, charge not full` |

## 冲刺（dash）：长按蓄力、松手出射

- **现行决定（owner 2026-11-18，覆盖同日更早的「按键触发」版本）**：照原作的 Charge'n'Roll——按住蓄力、松手出射。VR 用**左手扳机**（`InputUse`，只取 `handType == LEFT`）；PC 用 **Shift 长按**（理由见「VRChat 平台事实」）。输入层只发「按下 / 松开」，蓄力、旋转、能量、huff 的规则全在 motor 的 [DashMath.cs](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/DashMath.cs)。
- **蓄力期间不前进、不转向**：motor 把三根轴按 0 处理并清掉平面速度；蓄满 3 个 gacha 窗口（1.2 s）后球开始原地旋转（原作 count=3 起旋转；转速 180°/s 是 PROVISIONAL，原作转速由玩家交替速度决定、没有公开值）。没蓄满就松手 = 不冲刺，也不进入冷却。
- **蓄满时间按球径分档**：原作需要 5/6/7/8 次交替，我们把一次交替折算成一个 0.4 s 的 gacha 窗口（窗口是原作「两次交替之间最长还计数」的间隔，所以按一个窗口一次交替折算，既还原原作所需输入时长，也不奖励猛按）。分档与换算依据写在 [CONTACT_MODEL_V2 §3.8](../BallMotor/CONTACT_MODEL_V2.md)，数值：

| 球径 | 原作 gacha 数 | 我们的蓄满时间 |
|---|---:|---:|
| <0.5 m | 5 | 2.0 s |
| 0.5–5 m | 6 | 2.4 s |
| 5–10 m | 7 | 2.8 s |
| ≥10 m | 8 | 3.2 s |

- **出射**：蓄满松手朝当前朝向冲出；冲出段沿用 1.6× 速度 / 0.45 s，冷却 1.2 s（从松手起算）。这三项是我们的设计值，原作没有公开出射倍率与时长；1.2 s 冷却也是我们加的（原作靠能量限制）。冲出时的爬升走既有 v_gate 链（`ComputeDrive` 的 `driveSpeed` → `ClimbMath.GateSpeed` → `ClimbMath`/`SlopeClimbMath`），没有第二条 dash-aware 路径。
- **能量与 huff（照原作）**：能量 240 帧 = 8 s；从第三个 gacha 窗口起、以及冲出段和松手后 0.4 s 的 gacha 尾巴里按 1 s/s 消耗；100 帧（3.33 s）没用能量后回充 18 帧（0.6 s）；能量到 0 进入 huff 240 帧（8 s），期间拒绝一切蓄力，结束后能量回满 240 帧。
- **与原作的差异（交 Lead/owner，不擅改）**：REROLL 笔记只给 gacha 档位、gacha 窗口、旋转门槛、能量与 huff，**没有**出射倍率与持续时间——1.6× / 0.45 s 沿用我们早前的设计值；1.2 s 冷却不是原作的；旋转速率是我们的 PROVISIONAL 值。
- **已知冲突与缓解（已实现）**：扳机同时是 `InputUse` 与 `Interact` 的触发源——蓄力/出射时准心若指着载具的下车面板等 Interact 按钮，会顺带按下它（来源见「VRChat 平台事实」）。owner 2026-11-18 定的缓解：骑乘时（本地玩家坐着）骑乘面板/结果卡上的 Interact 在 motor 蓄力或出射段**忽略本次按下**（`RideDashInteractMath`；`BallRideStation.ShouldIgnoreDashInteract()` 给面板按钮、`StoryRoundController.ShouldIgnoreDashInteract()` 给结果卡重试）。不在 dash 中时行为完全不变。**残留**：同一帧里 `InputUse` 与 `Interact` 的送达顺序没有保证，所以「起蓄」的那一次按下仍可能先被面板收到；下车按钮是两次确认，第二次按下若仍在蓄力/出射段会被忽略，要完成下车必须等 dash 结束再点两次（那时是玩家明确要点）。
- **交替推拉手势不再实现**：`TankStickMath`/`VrStickMoveInput` 里的 gacha 计数与 `IsDashEdge` 已删，测试同批删除；gacha 的档位数值改由 `DashMath.ChargeGachasFor` 表达，一个逻辑一个实现。
- 快速转身（L3+R3）不做：摇杆按下在 VRChat 里是 Action Menu。

## 旧代码的去留（按此执行，暂不删）

| 文件 | 现状 | 建议 |
|---|---|---|
| `Runtime/Input/ControlIntentMath.cs` + `Tests/EditMode/Input/ControlIntentTests.cs` | motor 的 `StepInput` 仍然通过它把 debug 轴转成 Move2D；其中的 Jump/Interact/Exit/Respawn/Comfort 边沿逻辑没有任何调用方 | 先在 motor 里把 `StepInput` 改成直接 `inputForward=debugForward; inputLateral=debugLateral; inputTurn=menuOpen?0:debugTurn`（另一个代理正在改 motor，交 Lead 排期），然后删掉 ControlIntentMath 和它的测试。按键边沿和菜单屏蔽各自已有归属（骑乘退出在 `BallRideStation`，菜单由平台释放输入） |
| `Docs/Design/ControlIntent/intent.py` + `Docs/tests/control_intent/` | 是 ControlIntentMath 的 Python 草稿模型 | 和 ControlIntentMath 一起删 |
| `Runtime/Input/DebugDriveMath.cs` + `DebugDriveTests.cs` + `DEBUG_DRIVE.md` | MCP 无法按住按键，操作员验收要靠它 | 保留；它只服务 Desktop 的 DEV-ONLY 覆盖，和摇杆映射不是同一个逻辑 |
