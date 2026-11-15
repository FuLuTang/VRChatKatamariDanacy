# Labs/PhysicsTestField · 球体物理回归场（场景宿主）

> 状态：**代码已写，未编译**（2026-11-15）。Unity/U# 编译、场景、Play Mode、Build & Test 全部 `NOT_RUN`。
> 场景**尚未创建**：本目录只有 4 个 `.cs`，没有 `.meta`（Unity 首次聚焦会用当前程序集补上）。
> 设计与数据：[`Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md`](../../../../Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md)、
> 几何：[`Docs/Design/Levels/physics_test_field/`](../../../../Docs/Design/Levels/physics_test_field/README.md)。

## 这个目录里的东西

| 文件 | 角色 |
|---|---|
| `PhysicsTestCell.cs` | 一格：本格的 `LevelArenaHost`、起点、激活矩形、`nominal_m`、`approach_m`、驱动时间线、判据并行数组。字段**全部**由判据导入器烘进去，不手填 |
| `PhysicsTestFieldDriver.cs` | 唯一的驱动器：切格 → 设球径 → 静置 → 按时间线驱动 → 判据 → 一行一判据的日志。`BeginCell` / `BeginCellAtDiameter` / `BeginFromIndex` / `BeginAll` / `Abort` |
| `LabBallSizeSetter.cs` | 用现有 `SubmitCandidate` 喂整数体积单位，把球的直径闭环到目标径；斜率是**测出来的**，不是 0.0043、也不是硬编码 0.0041 |

编辑器侧（不在本目录）：`Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs`（菜单
`VRC Katamari/Level/Import Physics Test Field Criteria`）读
`Docs/Design/BallMotor/physics_test_field.json` 写上面三个组件的序列化数组。

## 三条必须知道的机制

1. **arena 默认是关的。** `PhysicsTestCell.Start()` 会把自己那格的 `LevelContent/<cell>` `SetActive(false)`；
   驱动只开当前格。`LevelArenaHost` 在自己的 `Update` 里注册，而电机的障碍表是**全局**的——四格同时活着
   会在第一帧把 4 个地面 + 16 面墙塞进 8 个平面槽并**永久 latch** overflow。所以 Play Mode 刚开始场上
   是空的：**这是对的**，不是没导入。
2. **切格 = 开本格 → `Reapply()`。** `ClearObstacles` 会把上一个 arena 的地面与 4 面墙一起清掉，因此每格
   的 placement 都自带它们（设计 §3.4），而不是共享一份 base。
3. **球径只能长大。** 回 0.30 是 `TryResetForNewRound`（驱动负责，它知道格起点）；设定器只负责喂体积。
   一个体积单位 = 0.0041 m³，所以直径的分辨率在 D=0.60 时约 7 mm、0.30 时约 58 mm——「±2 mm」在 1.10 以下
   做不到，判据用 `diameter_error_quanta ≤ 1` 表达这件事（设计 §8.3）。

## 怎么跑（操作员）

完整接线清单在设计文档 §9.5（场景路径、加组件、跑两个导入、期望日志）。最小顺序：

1. 建 `Labs/PhysicsTestField/PhysicsTestField.unity`（`PipelineManager` + `ReferenceCamera` + 从
   `StoryLevel` 拷 `MotorHost`/`PlayerBall`/`PickupField`）；
2. 菜单 `VRC Katamari/Level/Import Placement (LevelContent)`（四份 cell 文档会被逐个导入）；
3. 给 `LevelContent/C14`、`/C09`、`/C08`、`/C10` 各加一个 `LevelArenaHost`，`motor` 指向 `MotorHost`，再导入一次；
4. 加 `Cells/<cell>`（`PhysicsTestCell`，只填 `cellId`）、`Driver`（`PhysicsTestFieldDriver`，填 `motor`/
   `pickupHost`/`ballSize`）、`SizeConsole`（`LabBallSizeSetter`，填 `motor`）；
5. 菜单 `VRC Katamari/Level/Import Physics Test Field Criteria`；
6. Play Mode → `driver.BeginCellAtDiameter("C14", 0.30f)`。

期望：每格每径一行 `[ptf] summary ... verdict=EXPECTED`；C10 在 0.30 上 `on_step` 应当 **FAIL 且
`verdict=EXPECTED`**（球被台阶挡住是对的）。

## 不做的事

- 不碰 `Runtime/`（这三个组件只调公开接口：`SetMoveAxes`、`SubmitCandidate`、`TryResetForNewRound`、
  `SetAllowedKinds`、`ClearObstacles` 与各 getter）；
- 不建 HUD、不切相机（`GetLastSummaryLine()` 已经给出 HUD 要的那一行）；
- 不在运行时读磁盘 JSON（只有编辑器工具读，见 `02_TECHNICAL_CONTRACTS.md` §4）。
