# 剧情模式切片：驱动与尺寸（计划 S1 的两个运行时脚本）

> 状态：`SOURCE_WRITTEN_NOT_COMPILED`，2026-11-14。本文只说明这两个脚本做什么、场景怎么接、哪些没验证。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test、没有真机。
> 计划与判据见 [第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §1、§2、§7 S1。

## 两个脚本

| 文件 | 职责 | 明确不做 |
|---|---|---|
| `Runtime/Input/DesktopMoveInput.cs` | 本地球唯一的设备读取点：把 `InputMoveVertical` 存成 forward、`InputMoveHorizontal` 存成 turn，任一变化就 `motor.SetMoveAxes(forward, turn, 0f)` | 不读速度/成长/吸附/回合状态；不写 Transform；不做摇杆死区或曲线（轴的原始值直接交给 motor，motor 自己 clamp 到 [-1,1]） |
| `Runtime/BallPresentation/BallView.cs` | 每帧读 `motor.GetSemanticDiameterWorldM()`，变化超过 1e-4 m 时写 `coreMesh.localScale = Vector3.one * diameter` | 不写 rotation（可视滚动在 motor 内部）、不写球根、不缩放附着根、不碰碰撞半径 |

两者都是 `UdonSharpBehaviour`，命名空间分别是 `VRCKatamari.Input` 与 `VRCKatamari.BallPresentation`，同步模式 `BehaviourSyncMode.None`，字段全部 `[SerializeField]`，没有 static、属性、struct、接口或每帧分配。

### 轴语义（代码证据）

- `SetMoveAxes(forward, turn, lateral)` 只写 motor 的三个 debug 轴（`KatamariBallMotor.cs:965-970`），`StepInput` 用 `(lateral, forward)` 组 `Vector2`，turn 单独进运动层（`:663-682`）。
- turn 的符号：`heading += turn * turnCap * dt`，前进方向取 `(+sin(heading), +cos(heading))`（`Runtime/Motion/BallMotionMath.cs:162-170`）。朝 +Z 时 heading 增大把前进方向转向 +X，**turn +1 = 右转**。
- 键盘映射沿用计划：W/S = ±forward，A/D = ±turn（计划 §1 写“A 负、D 正”），lateral 桌上暂无绑定，恒为 0。
- 尺寸读者是 `GetSemanticDiameterWorldM()`（`KatamariBallMotor.cs:1373`）；球心读者是 `GetCenterWorld()`（`:1348`），本切片没有脚本用它，日志由 lab 宿主自己打。

### 生命周期

`SetInputEnabled(false)`、`OnStationExited`、`OnPlayerRespawn`、`OnDisable` 都会把两个轴清零并**再推一次零**，所以球会刹车而不是保持最后一次方向；重新启用不推零（此时轴本来就是零）。副作用：桌面只在数值变化时发轴，若玩家在禁用期间一直按住 W，重新启用后要先松开再按才会动。非有限值（NaN/Inf）在适配器就被丢掉：motor 对不合法帧的策略是“保留上一帧轴”，放进去会让那颗球永久冻结。

## 场景怎么接（Unity 操作者的活）

1. `MotorHost` 上只挂 `KatamariBallMotor`（独占 GameObject，规则见 [BallMotor README](../BallMotor/README.md) 的装配约束）。
2. 层级：`BallPhysicsRoot`（motor 只写 position）→ `BallVisualRollRoot`（motor 只写 rotation，**localScale 恒 1**）→ `CoreMesh`。把 `ballPhysicsRoot`、`ballVisualRollRoot` 拖到 motor 上。
3. 新建一个空对象（例如 `DesktopInput`），**独占**挂 `DesktopMoveInput`，把 `motor` 拖进去。不要和 motor 同挂一个对象。
4. 新建一个空对象（例如 `BallViewHost`），**独占**挂 `BallView`，把 `motor` 与 `CoreMesh` 拖进去。
5. 相机架：`motor.followRigRoot` 接到相机跟随根；不接的话 motor 算出的跟随帧没有落点（计划 §1 已记录这一点）。
6. 地面：`AddPlane` 注册；起始位置在 `startCenterWorld`。

`CoreMesh` 的网格约定：**直径 1 m、pivot 在球心的球体**（例如 Unity 内置 Sphere），因为代码直接把它缩放到语义直径。换成直径不是 1 m 的网格时显示尺寸会差一个固定倍数，需要在美术侧改，而不是在这里加一个魔数。

## 未验证（不要把这些当已验证）

1. **没有编译。** U# 全量编译、Console 无错都还没发生。
2. `InputMoveVertical` / `InputMoveHorizontal` 的 override 签名来自已安装 SDK 的 `UdonSharpBehaviour.cs`（转引 [T04 轴日志证据](../../Evidence/StationPlatform/axis_logging.md)），但**那次改动本身从未编译**。`OnStationExited` / `OnPlayerRespawn` 的签名在 `Labs/StationPlatform/StationLabDriver.cs` 里编译并通过 PC 实测（T04）。`OnDisable` 是 Unity 生命周期消息（官方 [Event Execution Order](https://creators.vrchat.com/worlds/udon/event-execution-order) 把 `OnEnable`/`OnDisable`/`OnDestroy` 列在 Unity 事件里），所以写的是普通私有方法而不是 `override`；本仓没有已编译的先例。
3. 桌面 WASD 的**符号**未实测：官方文档只写 `InputMoveVertical` = W/S、`InputMoveHorizontal` = A/D、桌面上输出 −1/0/1 整数（[Input Events](https://creators.vrchat.com/worlds/udon/input-events)），没有写 A 是 −1 还是 +1。代码按计划“A 负、D 正”映射，第一次 Play Mode 要先确认 A 是左转。
4. 坐姿时 VRChat 是否继续把轴发给 Udon：未验证，也就是计划 §8 的风险 1（S2 才有结论）。
5. 菜单：官方文档说打开 VRChat 菜单时 Udon 会“释放所有按住的输入”，但只对按钮事件明确写了会补发 `InputJump(false)`；轴是否也补发 0 未说明。所以“按住 W 时开菜单”球会不会继续跑，未验证。motor 有 `SetMenuOpen` 接缝，本适配器没有用它（计划 S1 没要求）。
6. `BallView` 的第一个写帧依赖 motor 的 `Start()` 已跑过（未跑时读数是 0，代码跳过而不是写 0），但“Start 顺序”只做了代码层面的防御，没有运行时观察。
7. 1e-4 阈值、`Vector3.one * diameter` 的视觉结果、球底贴地判据（`center.y − d/2`）都还没有 Play Mode 数据。

## 与计划的差异（留给 Lead 决定，本目录没有改计划）

- 计划 §4 的示例写 `input.SetEnabled(false)`，本次实现的公开方法是 `SetInputEnabled(bool)`。S4 写 `StoryRoundController` 时以脚本为准，或先改计划的措辞。
- 计划 §7 S1 还列了 `Labs/StoryLevel/StoryLevel.unity` 与 `StoryLevelLabBootstrap.cs`；它们不在本次写范围内，仍未创建。

## 没有改动的东西

没有改 `KatamariBallMotor`、asmdef、场景或任何既有文件；两个脚本各带 `.meta`，新目录 `BallPresentation` 也带 `.meta`，GUID 均为新生成且全仓唯一。
