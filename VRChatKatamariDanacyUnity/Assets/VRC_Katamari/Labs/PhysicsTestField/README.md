# Labs/PhysicsTestField · 球体物理回归场（场景宿主）

> 设计与数据：[`Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md`](../../../../../Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md)、
> 几何：[`Docs/Design/Levels/physics_test_field/`](../../../../../Docs/Design/Levels/physics_test_field/README.md)。
> 场景：`PhysicsTestField.unity`（本目录）。

## 这个目录里的东西

| 文件 | 角色 |
|---|---|
| `PhysicsTestCell.cs` | 一格：本格的 `LevelArenaHost`、起点、激活矩形、`nominal_m`、`approach_m`、驱动时间线、判据并行数组。字段**全部**由判据导入器烘进去，不手填 |
| `PhysicsTestFieldDriver.cs` | 唯一的驱动器：切格 → 设球径 → 静置 → 按时间线驱动 → 判据 → 一行一判据的日志。`BeginCell` / `BeginCellAtDiameter` / `BeginFromIndex` / `BeginAll` / `Abort` |
| `LabBallSizeSetter.cs` | 用现有 `SubmitCandidate` 喂体积（mm³），把球的直径闭环到目标径；斜率/量子是**解析的**（`D³/total` 与 `D/(3·total)`，`total` = 起始体积 + 已确认体积） |

编辑器侧（不在本目录）：`Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs`（菜单
`VRC Katamari/Level/Import Physics Test Field Criteria`）读
`Docs/Design/BallMotor/physics_test_field.json` 写上面三个组件的序列化数组。

## 五条必须知道的机制

1. **所有格常显，只有当前格向电机注册。** 电机的障碍表是**全局**的，几个 `LevelArenaHost` 同时注册会在第一帧
   塞满平面槽并**永久 latch** overflow。所以 `PhysicsTestCell.ReleaseArena()` = `arena.SuspendRegistration()`
   （不 `SetActive(false)`）；`ApplyRegistration()` = `ResumeRegistration()` + `Reapply()`。Play Mode 里 pad、矮墙、台阶、坡都看得见，
   但只有首格（C14）与正在跑的格是实心的，**这是设计**，不是别的格没导入。
2. **切格 = 目标格 `Reapply()`。** `ClearObstacles` 会把上一个 arena 的地面与 4 面墙一起清掉，因此每格
   的 placement 都自带它们（设计 §3.4），而不是共享一份 base。
3. **球径只能长大。** 回 0.30 是 `TryResetForNewRound`（驱动负责，它知道格起点）；设定器只负责喂体积。
   成长量子是 1 mm³（0.30 m 球上约 7 nm 直径当量），判据 `size_accuracy` 是 `abs(diameter_error_m) ≤ 1 mm`（设计 §8.3）。
   电机的起始体积没有 getter，由判据导入器从场景 motor 烘进设定器的 `sizeModelStartVolumeMm3`，所以**改了电机的起始尺寸要重新导入判据**，
   否则设定器未标定（`IsCalibrated()` 为 false），对超过当前尺寸的请求报 `noSizeModel`，该 run 标 `BLOCKED_UNTIL_size`。
4. **球径请求每 run 只发一次：`Switch` 调一次 `SetDiameterM(D)`，`Size` 只读。** 复位把球重定价回 0.30，
   所以每 run 都要重发；但**不能每帧重发**——`SetDiameterM` 会清 `converged`，`TickSize` 每帧调它就会
   永远读不到收敛（`reason=timeout`）。收敛后驱动还要量一次
   `abs(实测 D − 目标) ≤ 20 mm`，不够就 `reason=sizeNotApplied` + `BLOCKED_UNTIL_size`（状态机与字段写者表见设计 §8.3.4）。
5. **先确认跑的是哪份程序。** 进 Play 的头两行是 `[ptf] build=…` 与 `[ptf] setter build=… startVolumeMm3=…`。
   没有它们、或日志里出现 `error=unknownMetric` / `BLOCKED_UNTIL_P3`，说明 U# 程序是旧的（数据可能已经是新的），
   **那份矩阵不能读**（设计 §8.3.3）。

## 怎么跑（操作员）

完整接线清单在设计文档 §9.5（场景路径、加组件、跑两个导入、期望日志）。最小顺序：

1. 打开 `PhysicsTestField.unity`（`PipelineManager` + `ReferenceCamera` + `MotorHost` / `PlayerBall` 已在场景里）；
2. 菜单 `VRC Katamari/Level/Import Placement (LevelContent)`（九份 cell 文档会被逐个导入）；
3. 九个 `LevelContent/<cell>` 各挂一个 `LevelArenaHost`，`motor` 指向 `MotorHost`，再导入一次；
4. `Cells/<cell>` 挂 `PhysicsTestCell`（只填 `cellId`）、`Driver` 挂 `PhysicsTestFieldDriver`（填 `motor` / `ballSize`，C18/C19 才需要 `pickupHost`）、
   `SizeConsole` 挂 `LabBallSizeSetter`（填 `motor`）；
5. 菜单 `VRC Katamari/Level/Import Physics Test Field Criteria`；
6. Play Mode → `driver.BeginCellAtDiameter("C14", 0.30f)`。

期望：每格每径一行 `[ptf] summary ... verdict=EXPECTED`；C09 / C10 在 0.30 上 `on_step` 在 v1 接触模型下应当 **FAIL 且
`verdict=EXPECTED`**（球被台阶挡住是对的）；各格各档的期望计数见设计 §9.5 与 §5.6.2。

## 不做的事

- 不碰 `Runtime/`（这三个组件只调公开接口：`SetMoveAxes`、`SubmitCandidate`、`TryResetForNewRound`、
  `SetAllowedKinds`、`ClearObstacles` 与各 getter）；
- 不建 HUD、不切相机（`GetLastSummaryLine()` 已经给出 HUD 要的那一行）；
- 不在运行时读磁盘 JSON（只有编辑器工具读，见 `02_TECHNICAL_CONTRACTS.md` §4）。
