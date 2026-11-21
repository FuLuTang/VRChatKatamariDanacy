# 地图 0：球体物理测试场（Physics Test Field）

> 状态：`PROVISIONAL_NOT_TESTED`（文中所有米数、角度、容量都是本项目暂定值，不是原作测量值）。
> **地图 0 就是手感测试场**：`Labs/PhysicsTestField/PhysicsTestField.unity`。以后坡滑、撞墙、爬坡卡死这类手感问题都在这里测，owner 也在这里自己试（§0.2）。接触模型只有一条（StepContactV2），本场只有这一套判据。
> 地图 0 只有一种用法的场地：**手动沙盒**。一大块平地上摆着所有测试板，球出生在南排前方，自由滚到任意一块去试（§0.2、§6.7）；板之间没有墙、没有台垫，只有最外面一圈边界。驱动器还能按时间线对某一格跑**脚本 run**：设球径、开球、自动判 PASS/FAIL 或只记录诊断读数（§6.1），脚本 run 不改变注册，只把球传到那一格的起点。
> 数据分两份，各管一件事：**几何**在 `Docs/Design/Levels/physics_test_field/<cell>/placement.json`（每格一份，`levelbuild check` 是门）；**判据 / 驱动时间线 / 球径 / 起点 / 激活矩形**在 [physics_test_field.json](physics_test_field.json)（schema_version=2）。跨文件的一致性由 `Docs/tests/levelbuild/test_physics_test_field_cells.py` 守。
> 已入库：13 个已接线格（C01 C03 C05 C06 C08 C09 C10 C13 C14 + 2026-11-17 的 C25 C26 C27 C28，共 90 条判据）、沙盒外框 `SANDBOX`（一块大地面 + 4 张外围边界平面）、4 个地形设计格（C21–C24，未接线）、`Labs/PhysicsTestField/` 三个运行时组件、编辑器判据导入器。
> 引用约定：`Runtime/…`、`Labs/…`、`Editor/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。

---

## 0. 现状

1. **13 个已接线格、4 个地形设计格、12 个待迁格、10 条仅设计行。** 已接线格摆成 4 排（§2.2）：北排（z≈11）C01 5° / C03 25° / C05 45° / C13 台面；中排（z≈0）C08 / C09 / C10 台阶 + C06 50° 板；C14 薄板（z=-10）；南排（z=-33）C25 墙 + 顶棚 / C26 / C27 / C28 缓坡。C21–C24 地形格的位置（z=-22）留空，尚未接线。
2. **一种注册、一份代码。** 电机静态 OBB 容量 256、平面 8。13 格的板合计 **21 个 OBB**，沙盒另加 1 个地面平面 + 4 张外围平面，一次放得下（§3）。格的 placement 里只有测试板（没有台垫、没有围墙，`level.arena.frame = "shared"`）；地面和外围墙只在 `SANDBOX` 那份文档里。注册入口是 `LevelArenaHost.Register(clearFirst, boxIdOffset, rampIdOffset)`：外框清表并登记，每格追加。
3. **判据落在已有 getter 上。** 读数是电机的公开 getter 加驱动自己每帧的采样；每步成本计数器没有，`readout=counter` 的判据报 `BLOCKED_UNTIL_P4`（§6.4）。
4. **球径设定不改运行时代码**：喂体积 → 读直径 → 收敛（§8）。
5. **三种判据**：判 PASS/FAIL 的、按球径给期望的、**只记录**的诊断（`op=record`，§5.3）。爬坡卡死这类还没定论的现象用诊断行，不在没有结论时判红绿。
6. **吸收门是原作口径**：`V = k·a·b·c`（`k` 缺省 0.70），`Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`，按道具自身尺寸算（实现 `PickupAbsorbMath`，说明见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)）。§4.1、§4.5、§4.6 的档位数都是按它算的。
7. **`napkin` 的厚度有数据冲突**：文档写 2.8 cm，`napkin.manifest.json` 是 1.4 cm。本场用静态 0.028 m 薄板钉住文档口径（C14），真 napkin 放进挡路行（C18 #1，待迁）。

### 0.1 编辑器与运行时组件

`Labs/PhysicsTestField/` 的 `PhysicsTestCell` / `PhysicsTestFieldDriver` / `LabBallSizeSetter` 与 `Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs`；`LevelArenaHost` 有注册挂起接缝（`SuspendRegistration` / `ResumeRegistration`）和参数化的注册入口 `Register`（`Reapply()` 就是 `Register(true, 0, 0)`，其他关卡照旧用它）。电机读数 getter 见 §6.4。

### 0.2 怎么在地图 0 里试（给 owner）

**前提**（Unity 操作员已装配，见 §9.5）：电机的 `rampTangentialGravityScale = 1`。Play 之后球出生在**南排前方** (0, -39.5)，朝北（+Z）。脚下是一整块 32 × 61 m 的大平地（x -16…16，z -42…19），所有测试板都立在上面，**平地上除了测试板什么都没有**；外围一圈边界（看不见，物理上拦得住球）。**所有板同时是实心的**，球在板与板之间可以连续滚。每块板的起跑标记处立着一块文字牌（`C09 / step 0.075 m`，ASCII，1.4 m 高），从南面读，牌在板前 1–3 m。起始球径约 0.30 m；要看 0.60 / 1.10 / 2.20 的球，让操作员用脚本 run（`BeginCellAtDiameter("C14", 0.60f)`；球会被传到那一格的起点，run 结束后停在原地）。

出生视角（面朝北）：左前方是 C25 的墙和顶棚，前方左右各一条坡 C26 / C27（你站在它们之间 3 m 宽的通道里），右前方是 C28，再往北隔着一大片空地，一排排板依次排开：C14 薄板、C08–C10 台阶 + C06 斜板、最远一排 C01–C13 四个坡 / 台面。

**走法**：每块板的朝向都是 +Z（北），所以从南面的牌走近、朝北满推。坐标是牌的位置（板在牌北面）；列 x = -10.5 / -3.5 / 3.5 / 10.5。

| 排（z） | 格 | 位置（x, z） | 做什么 | 看什么 |
|---|---|---|---|---|
| 南排 z=-37（板 z -35…-28.5） | C25 墙 + 顶棚 | (-10.5, -35.2) | 朝 +Z 满推，让球进顶棚下、顶住墙；顶几秒后松手；再拉后退 | 主人 2026-11-17 亲测确认正常：向前推不动是受阻，不是卡住，玩家后退脱出；不做按进度释放（TERRAIN_CLIMB §6.7）。`climbActive=True` + `actualSpeed≈0` 仅表示通道在爬但球没动 |
| | C26 缓坡 2° | (-3.5, -37) | 先在平地停住，再推上坡，松手停下 | 主人 2026-11-17 认可顺坡朝向松手不滑，保持现状；2° 在坡滑下限边界，平地不滑 |
| | C27 缓坡 5° | (3.5, -37) | 同上，上坡后松手，再把球转成**横着对坡**（原地转 90°） | 主人 2026-11-17 认可顺坡朝向松手不滑：滚动阻力照常作用（方案 A）；横向测试才观察静止约 0.3 s 后下滑，不要求顺坡朝向也滑 |
| | C28 缓坡 10° | (10.5, -37) | 同 C27 | 顺坡朝向不持续滑行（g·sin10° ≈ 1.70 < 1.9）；若横向停住约 0.3 s，开始下滑，终速约 0.6 m/s |
| 薄板 z=-13 | C14 薄板 | (-10.5, -13) | 满推 | 0.028 m 薄板直接碾过，不减速 |
| 中排 z=-3.5（板 z≈0） | C08 / C09 / C10 台阶 | (-10.5 / -3.5 / 3.5, -3.5) | 沿 +Z 满推 | 主人 2026-11-17 亲测：5 / 7.5 / 10 cm 都能靠爬墙通道上去，符合预期，不因走爬升通道而判错 |
| | C06 50° 板 | (10.5, -3.5) | 满推 3.6 s（板在 z=-2…-1.3） | 体力慢坡爬（竖直约 0.25 m/s），无固定高度cap；0.30m球约 3.1 s 到板顶，体力还剩约一半 |
| 北排 z≈7（坡脚 z=7…7.6） | C01 5° / C03 25° / C05 45° | (-10.5 / -3.5 / 3.5, 6.8–7.1) | 沿 +Z 满推上坡；C03 在坡中间松手 | 坡脚没有隐形墙；45° 接缝能不能上台面；C03 静止 0.3 s 后顺坡滑下 |
| | C13 台面 | (10.5, 6.5) | 满推上 11.31° 坡、过台面、从北缘（z=14）掉下去 | 从北缘掉下去是逐帧落地不是瞬移 |

沙盒里不同格的板之间的通道至少 2.5 m（最宽的预设球径 2.2 m 也过得去），最近的一对是 C05 与 C13（2.5 m）；板与外围墙至少 3.0 m；出生点离最近的板 4.5 m。这三个数由 Python 守卫（`SandboxLayout`）按 placement 重算。

**注意**：大球（0.60 以上）进不了 C25 的顶棚下（净高 0.5 m），那一格只对 0.30 球有意义。

---

## 1. 这个场要证明什么：十个物理问题 → 各类格位

| # | 物理问题 | 一句话判据 | 格位 | 阶段 |
|---|---|---|---|---|
| Q1 | 球站得住吗（地形支撑） | 球心 − 支撑面 = r，10 s 波动 < 1 mm | C13（台面静置）、C07–C09 | 已接线 |
| Q2 | 台阶能上吗 | `h < r` 滚上、`r ≤ h < 1.5·D` 爬升、其余是墙，且**不穿模** | C07–C12 | 已接线（C07 / C11 / C12 待迁） |
| Q3 | 斜坡能上吗 | 爬到坡顶 `center.y = 0.6 + r ± 1 cm`，坡底没有隐形墙 | C01–C05 | 已接线（C02 / C04 待迁） |
| Q4 | 斜面护栏生效吗 | 50° 被 `AddRamp` 拒绝并写原因；板作为 box 慢爬不卡死 | C06 | 已接线 |
| Q5 | 台面边缘会掉下去吗 | 从 0.6 m 直角边开出后逐帧下落、落地 `= r`、无瞬移 | C13、C20 | 已接线（C20 待迁） |
| Q6 | 吸附物凹凸有上限吗 | 每步抬升 ≤ 0.04、总突出 ≤ `min(0.35r, 0.30)`、速度不受扰动 | C19 | 待迁 |
| Q7 | 吸不动的挡得住吗 | 挡在 `propBounds` 盒外 r 处；长大后同一件变食物/变台阶 | C18 | 待迁 |
| Q9 | 墙与顶棚夹角能否后退脱出 | 向前受阻不算卡住，不做按进度释放；脚本保留爬升 / 松手 / 反推诊断 | C25 | 已接线（诊断） |
| Q10 | 坡上静止后会下滑吗 | 静止门后仍受滚动阻力限制；C26–C28 顺坡朝向不滑；平地不滑、推着不额外下滑 | C26–C28 | 已接线 |
| Q8 | 贴墙/夹缝会卡死或穿墙吗 | 4 m/s 不穿薄墙；内角不抖不弹能出来；0.32 m 缝卡住后能恢复 | C15、C16、C17 | 现在 |

> 为什么把「吸附物凹凸」放在**现在**：小棍（径向代理）已实现，`[ProxyTrace]` 每固定步一行、`GetGroundProxyLiftWorldM()` / `GetProxyProtrusionCapWorldM()` 已在。这一格是**今天就能给结论**的，不该等地形。

---

## 2. 场地总图

### 2.1 坐标与朝向约定

| 项 | 值 |
|---|---|
| 引擎/单位 | Unity 2022.3.22f1；米；左手系；`+Y` 上、`+Z` 北、`+X` 东 |
| 地面 | 一张解析平面 `n = (0,1,0)`、`offset = 0`（`motor.SetGroundPlane` 后的等价物） |
| 边界 | 沙盒：4 张外围平面 `x = ∓16`、`z = -42 / 19`（`SANDBOX` 的 `AddPlane`，只当墙、永不当支撑）；格自己没有墙 |
| 场界 | 格的足迹占 x -13.5…13.5、z -37.5…16；沙盒外框 x -16…16、z -42…19（32 × 61 m）；单格 `level.bounds = [[-14,-15],[14,16.5]]`（C25–C28 为 [[-14,-40],[14,16.5]]） |
| 朝向 | `heading 0 = +Z`，`90 = +X`，`180 = -Z`，`-90 = -X`。**依据代码**：`BallMotionMath.TryStep` 里 `forwardX = sin(h)`、`forwardZ = cos(h)`，`lateral = (cos h, 0, -sin h)` |
| 起点 | 沙盒出生点 `(0, 0.15, -39.5)`、`heading 0`（朝北）、`D = 0.30`（0.15 = 起始球半径）；每格另有自己的起点（脚本 run 用） |


### 2.2 布局（俯视图）

> 俯视图（不入库）由 `Tools/levelbuild/blender/render_views.py` 从各格 placement 渲染。**placement 才是权威**；下表是排布。每格的足迹（`footprint`，6×9 或 6×10 m）是它在沙盒里的区域，相邻足迹留 1–1.5 m；足迹不画出来，场上只有测试板。

| 排（z） | 列（x） | 内容 | 进深 |
|---|---|---|---|
| 北 `z ∈ [6, 16]` | -10.5 / -3.5 / 3.5 / 10.5 | C01 5°、C03 25°、C05 45°、C13 台面 + 11.31° 坡 | 10 m（C01 的 6.86 m 水平坡长最深） |
| 中 `z ∈ [-4.5, 4.5]` | -10.5 / -3.5 / 3.5 / 10.5 | C08 0.05、C09 0.075、C10 0.10、C06 50° 板 | 9 m |
| 南 `z ∈ [-14.5, -5.5]` | -10.5（其余预留） | C14 0.028 薄板 | 9 m |
| 地形 `z ∈ [-26.5, -17.5]` | -10.5 / -3.5 / 3.5 / 10.5 | C21 台阶 / C22 沟 / C23 隧道 / C24 路缘（未接线；沙盒里这一带是空地） | 9 m |
| 最南 `z ∈ [-37.5, -28.5]` | -10.5 / -3.5 / 3.5 / 10.5 | C25 墙 + 顶棚、C26 2°、C27 5°、C28 10° | 9 m |

沙盒（`SANDBOX`）：大地面 x -16…16、z -42…19，外围 4 张平面在它的边上；出生点 (0, -39.5) 朝北。各板的位置：

| 板 | x 范围 | z 范围 | 顶高 |
|---|---|---|---|
| C25 顶棚 / 墙 | -12.5…-8.5 | -34…-32 / -32…-31.6 | 0.70（底 0.50）/ 2.00 |
| C26 / C27 / C28 坡 + 平台 | -5.5…-1.5 / 1.5…5.5 / 8.5…12.5 | -35…-28.5 | 0.19 / 0.48 / 0.97 |
| C14 薄板 | -12…-9 | -11.5…-8.5 | 0.028 |
| C08 / C09 / C10 台阶 | -12.5…-8.5 / -5.5…-1.5 / 1.5…5.5 | -0.75…0.75 | 0.05 / 0.075 / 0.10 |
| C06 50° 板 | 8.5…12.5 | -2.0…-1.34 | 0.60 |
| C01 / C03 / C05 坡 + 台面 | -12.5…-8.5 / -5.5…-1.5 / 1.5…5.5 | 7.6…15.96 / 7.6…10.39 / 7.6…9.70 | 0.60 |
| C13 坡 + 台面 | 8…13 | 7…14 | 0.60 |

全场已接线部分 27 × 53.5 m。**所有板同时是实心的**，没有任何只看得见、摸不到的东西：每格 placement 没有台垫和围墙，`PlacementImporter` 就不会为它们建可见物体。

### 2.3 场景对象

| 对象 | 作用 | 复用自 |
|---|---|---|
| `MotorHost` | 挂 `KatamariBallMotor`，球心/朝向的唯一所有者 | `PhysicsTestField.unity` |
| `PlayerBall/BallPhysicsRoot → BallVisualRollRoot → CoreMesh` | 位置宿主 + 旋转宿主 + 视觉球（三层固定层级） | `PhysicsTestField.unity` |
| `RideRoot/RideSeat` | 人玩时的骑乘 Station（可选，测试场不强依赖） | `PhysicsTestField.unity` |
| `Input` | `DesktopMoveInput`（人玩）；脚本运行时由驱动器直接 `SetMoveAxes` | `PhysicsTestField.unity` |
| `PickupField` | `PickupFieldHost`：**唯一**的挡路写入者，也是道具行宿主（C18/C19 才需要；`PhysicsTestField.unity` 当前没有挂） | 待加 |
| `LevelContent/<cellId>` | 受管根（由 `PlacementImporter` 建）：该格的 `Props/`、`Structures/`，**并挂该格唯一的 `LevelArenaHost`** | 新（工具建） |
| `LevelContent/SANDBOX` | 沙盒外框（`SANDBOX/placement.json`）：大地面的可见网格由 `PlacementImporter` 自动生成（`ground` 的 `Visual` 子物体，尺寸撑到外围平面），4 张外围平面是 `wall_*` 标记；**挂一个 `LevelArenaHost`**，驱动器的 `sandboxFrame` 指向它 | 新（工具建 + 操作员加组件） |
| `Cells/<cellId>/SandboxLabel` | 每格的世界空间文字牌（`TextMesh`，判据导入器建） | 新（工具建） |
| `Cells/C14` … | 每格一个空物体，挂 `PhysicsTestCell`（引用上面那个 `LevelArenaHost`）。**它不放进 `LevelContent`**：受管子树每次导入都可能被删改，而组件不能跟着消失 | 新（手工加组件） |
| `Driver` | `PhysicsTestFieldDriver`（§7） | 新（手工加组件） |
| `SizeConsole` | `LabBallSizeSetter`（§8）+ 4 个 uGUI 按钮（可选） | 新 |
| `Cameras/cam_*` | 8 个机位（§6.5） | 新 |
| 根对象 | 必须挂 `VRC.Core.PipelineManager`；`VRCSceneDescriptor.ReferenceCamera` 指向主相机 | [Labs/README.md](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/README.md) 的硬要求 |

---

## 3. 注册与容量：一次放下

### 3.1 电机的上限

| 容量 | 值 | 来源 |
|---|---|---|
| 静态 OBB（盒、坡板、弯坡段） | 256 | `ContactTuning.MaxStaticObbCount`；`LevelArenaHost` 全部用 `AddObb` 登记，超出显式拒绝并计数（`obbOverflow`） |
| 平面（边界墙） | 8 | `MaxPlaneCount`；超出 latch `overflowCount`，只有 `Start` 能清 |
| 地面 | 1 张解析平面 | `SetGroundPlane`，重复设置同一张是幂等的 |
| 挡路盒 / 道具支撑 / 每步接触 | 6 / 6 / 4 | 现状 |

### 3.2 全场的算术

| 资源 | 沙盒（全部同时注册） | 上限 |
|---|---|---|
| 静态 OBB | **21**（C01 2、C03 2、C05 2、C13 2、C06 1、C08 1、C09 1、C10 1、C14 1、C25 2、C26 2、C27 2、C28 2；每个有坡的格 = 坡板 + 平台，C25 = 墙 + 顶棚） | 256 |
| 外围平面 | 4（`SANDBOX` 的 4 张） | 8 |
| 格自己的地面 / 围墙 | 0（格的 placement 里没有） | — |

就算把外围墙和地面都当成 OBB 登记，也只有 21 + 4 + 1 = 26。Python 守卫 `SandboxLayout.test_the_whole_sandbox_fits_the_motor_tables` 按 placement 重算，EditMode `SandboxRegistrationTests.TheWholeSandboxFitsTheObbTableAndThePlaneTable` 在 Unity 里再数一遍。

### 3.3 一份注册

```
LevelArenaHost.Register(clearFirst, boxIdOffset, rampIdOffset)
  Reapply()  ==  Register(true, 0, 0)        （其他关卡照旧用它）

手动沙盒（驱动器 EnterSandbox，唯一的注册）：
  1. 外框 SANDBOX.Register(true, 0, 0)：ClearObstacles → 地面 + 4 张外围平面
  2. 每格 ApplySandboxRegistration(盒偏移, 坡偏移) == Register(false, 已登记的盒数, 已登记的坡数)：
     不清、surface id 向后错开（格里没有地面、没有墙）
  3. 重发 kind 白名单；球复位到出生点
```

- **格里没有台垫和围墙**：每格的 placement 只有测试板（`level.arena.frame = "shared"`，校验器要求这种文档**没有** `role=ground` / `role=boundary`）。导入器只给 placement 里有的结构建可见物体，所以场上不会有「看得见、摸不到」的墙或台垫；地面是 `SANDBOX` 的那一块。
- **为什么 surface id 要错开**：接触求解按 surface id 去重，两个格各有一个 `1 + 0` 的盒，会被当成同一个实体。偏移后盒是 1…n、坡是 500…（`GetSupportKind()` 只认 500…503 为坡，沙盒里第 5 个以后的坡读数会是 1：纯诊断读数，不影响物理）。
- **脚本 run**：不改注册，只把球传到那一格的起点（别格的板在 2.5 m 以外，仍然是实心的）。判据最初是在每格带围墙的隔离 pad 里写的，沙盒里它们的读数可能变，见 §9.5.3。
- **容量检查**：`LevelArenaHost` 的上限检查按「电机里已有的 + 这个宿主要登记的」算，追加的宿主共用 256 个位置。
- **已知成本**：进沙盒一次 `ClearObstacles` + 登记，重新登记静态 OBB 表。

---

## 4. 格位清单

> 「各球径的主判据」括号内是**预期结果**（`✓` = 该判据在该球径下应通过）。判据 id 与 JSON 一一对应。「几何」列是设计值；已接线格的真源是 `Docs/Design/Levels/physics_test_field/<id>/placement.json`，未迁移的格只有本表一处记录。判据侧只保留本格的自变量 `nominal_m`。

<!-- table:cells -->

| 格 | 名称 | 几何（关键尺寸，m） | 各球径的主判据 | 状态 |
|---|---|---|---|---|
| C00 | 中央起点 · 球径设定台 | 地面 + 4 边界平面 | rest_gap_m abs<= 0.005 | 待迁 |
| C01 | 5° 斜板 | 坡 5° 升 0.60、水平 6.858、宽 4；台面 4×0.6×1.5；x=-10.5，北排 | `ramp_support`=1 / `ramp_entry_progress`>=推导值 / `climbed` deck_gap abs<=0.01 / `ramp_registered`（0.30–2.20 ✓） | 已接线 |
| C02 | 15° 斜板 | 按紧凑口径 15° 升 0.60、水平 2.24 | deck_gap_m abs<= 0.01 | 待迁 |
| C03 | 25° 斜板 | 坡 25° 升 0.60、水平 1.287、宽 4；x=-3.5 | 同 C01 + `slip_back`（记录：坡上停 5 s 后下滑的距离） | 已接线 |
| C04 | 35° 斜板 | 35° 升 0.60、水平 0.86 | deck_gap_m abs<= 0.01 | 待迁 |
| C05 | 45° 斜板 | 坡 45.0°（等号）、升 0.60、水平 0.60；x=3.5 | 同 C01 | 已接线 |
| C06 | 50° 板（拒绝注册 + 慢爬） | 50° 板 4.0 宽 × 0.20 厚 × 0.783 沿坡，以 box 落库（§4.1.2）；x=10.5，中排 | `ramp_refused`>=1 / `refusal_slope`>=49 / `stopped_by_wall` blocked_gap<=0 / `slope_top_reached`（`max_bottom_rise_m>=0.58`，四档均到顶） | 已接线 |
| C07 | 台阶 0.020 m | 盒 6.000×0.020×4.000 @ (15.0, -14.0) | deck_gap_m abs<= 0.005 | 待迁 |
| C08 | 台阶 0.050 m | 盒 4.0×0.05×1.5 @ (x=-10.5, z=0) | 同 C01 的 deck_gap | 已接线 |
| C09 | 台阶 0.075 m | 盒 4.0×0.075×1.5 @ (x=-3.5, z=0) | `on_step`：四档都滚上去（0.075 < r） | 已接线 |
| C10 | 台阶 0.100 m | 盒 4.0×0.10×1.5 @ (x=3.5, z=0) | `on_step`：四档都滚上去；`blocked_gap`<=0 | 已接线 |
| C11 | 台阶 0.200 m | 盒 6.000×0.200×4.000 | deck_gap_m abs<= 0.005（0.20 > r(0.30) 走爬升通道，< H_cap 0.45） | 待迁 |
| C12 | 台阶 0.400 m | 盒 6.000×0.400×4.000 | deck_gap_m abs<= 0.005（0.30 球 H_cap=0.45，贴边） | 待迁 |
| C13 | 0.6 m 台面：一侧斜坡上、一侧直角边下 | 台面 5×0.6×4 @ (x=10.5, z=12)；南侧坡 11.31° 升 0.60、水平 3.00；北缘 z=14 直角边 | `climbed` / `seam_no_step` / `landing_gap` / `fall_frames`>=3 / `ramp_support`=1 | 已接线 |
| C14 | 0.028 m 薄片（餐巾级） | 3×0.028×3 板 @ (x=-10.5, z=-10)；1.5 m 助跑 | progress_m >= 4.5；`lift_bounded`（板足印内） | 已接线 |
| C15 | 0.1 m 薄墙（高速不穿 + 贴墙滑） | 盒 6.000×2.500×0.100 | blocked_gap_m >= -0.01 | 待迁 |
| C16 | 90° 内角 | 两道墙交成内角 | xz_std_m <= 0.002 | 待迁 |
| C17 | 0.4 m 夹缝 | 两道门 0.40 / 0.32 | passed_plane_z_m <= -28.6（0.30 ✓，其余球径预期不通过） | 待迁 |
| C18 | 挡路 → 长大可吸 递进（9 件） | 无静态几何 | absorbed_delta >= 1 | 待迁 |
| C19 | 长杆/板吸附 | 无静态几何 | attached_delta >= 1 | 待迁 |
| C20 | 组合路线：斜坡 → 台阶 → 台面 → 边缘落下 | 斜板 15° 升 0.60；盒两块 | deck_gap_m abs<= 0.01 | 待迁 |
| **C25** | **墙 + 顶棚（受阻与后退脱出，只诊断）** | 墙 4.0×2.0×0.4（前面 z=cz+1.0）；顶棚 4.0×0.2×2.0，**底面 0.50 m**，贴在墙前；x=-10.5，z=-33；起点 z=cz-2.2 | 10 条只记录 + `size_accuracy` / `no_overflow`；只跑 D=0.30 | 已接线（§4.9） |
| **C26 / C27 / C28** | **缓坡 2° / 5° / 10°（坡滑规则）** | 坡升 0.192 / 0.481 / 0.970 m，水平 5.5 m、宽 4，平地助跑 2 m；x=-3.5 / 3.5 / 10.5，z=-33 | `flat_no_slide` / `slides_after_rest` / `slides_promptly` / `slide_speed` / `push_not_slowed`（2° 只记录）；只跑 D=0.30 | 已接线（§4.9） |

C21–C24 地形格（台阶 / 沟 / 隧道 / 路缘，三角网格）在 `Tools/levelbuild/physics_test_terrain.py` 生成，判据在 JSON 的 `planned_cells[]`（`status: terrain_pending`），未接线。

### 4.1 球径档位与派生上限（四个 preset 的全部数值）

<!-- table:diameters -->

| 球径 D | r | 爬升上限 H_cap = 1.5·D | 突出 cap = min(0.35r, 0.30) | 吸收门：整 mm 球径 | 可吸的 a·b·c 上限（k=0.70，严格小于） |
|---|---|---|---|---|---|
| 0.30 m | 0.150 | 0.450 | 0.0525 | 300（量化后实际球 0.2997 m = 299 mm） | 0.002040 m³（299 mm 时 0.002020） |
| 0.60 m | 0.300 | 0.900 | 0.1050 | 600 | 0.016238 m³ |
| 1.10 m | 0.550 | 1.650 | 0.1925 | 1100 | 0.099830 m³ |
| 2.20 m | 1.100 | 3.300 | 0.3000 | 2200 | 0.797555 m³ |

> 门是 `Req_mm = floor(1000·cbrt(60·k·a·b·c/π)) ≤ 球的整 mm 直径`，上表最后一列是它对 `k=0.70` 的 a·b·c 反解：`a·b·c < π/42·((整mm+1)/1000)³`。**按道具自身尺寸算**，不是球体积的固定比例；k≠0.70 的道具另算（`absorb_diameter_req_mm`）。台阶看 r 与 H_cap，长杆看突出 cap，挡路看吸收门：三个独立的量，不要互相代用。


**2026-11-17坡爬验收更新**：C06仍是50°/0.60m的同一块旋转box，AddRamp作者探针拒绝读数保留，
运行时OBB不变；`slope_top_reached`用`max_bottom_rise_m>=0.58`，不再以`1.5D`封顶。C03/C05仍是RIDE，
C25是竖墙+顶棚（>60°墙通道），三格几何/判据均不改；新增耗尽回滑与recoveries=0由EditMode真motor补测。
判据行数不增加，导入器已支持现有metric/op，**无需新阈值码/新字段**；Unity须重新导入判据后才生效。

**2026-11-17坡爬速度修正**：坡爬竖直速度改为 `v_up0 = 0.917·D` 慢爬量级（TERRAIN_CLIMB §0.1），D=0.30 到板顶约 3.1 s
（旧公式 0.77 s）。C06 驱动时间线因此从「推 2 s 后松手」改为「推 3.6 s 后松手」，`stop_seconds` 仍 2.0；判据
行、阈值不变。大球更早到顶，之后继续往北滚（与 D=2.20 本来就会滚过板的行为一致）。Unity须重新导入判据。

### 4.1.1 C01–C06 斜板组：同一升程、不同坡度

**紧凑化后取固定升程 0.60 m**（不是固定水平长）：同时 5° 坡的水平长降到 6.86 m，能塞进 10 m pad。水平长 = `0.6 / tan θ`：

| 格 | 坡度 | 水平长 | 沿坡长 | 坡顶 z（脚 z=7.6） | 预期 | 说明 |
|---|---|---|---|---|---|---|
| C01 | 5° | 6.858 | 6.884 | 14.46 | 可上 | 已入库；全场最长的一条，也是「坡上不减速」最容易被看出来的地方 |
| C02 | 15° | 2.239 | 2.318 | 9.84 | 可上（待迁） | 和 C20 的坡同角度（0.60 升程），便于横向对照 |
| C03 | 25° | 1.287 | 1.422 | 8.89 | 可上 | 已入库；研究文档 §3.4 的「松手会不会下滑」争议点（现状不会滑：松手后只受接触求解的下坡规则影响） |
| C04 | 35° | 0.857 | 1.046 | 8.46 | 可上（待迁） | 单步法向位移 0.08·sin35 = 0.046 ≤ 0.1（余量 2.2×） |
| C05 | 45° | 0.600 | 0.849 | 8.20 | 可上（等号） | 已入库；`maxSupportSlopeDegrees = 45` 是否**包含** 45 就是这一格要回答的 |
| C06 | 50° | 0.503 | 0.783 | — | **拒绝** | 已入库；`AddRamp` 应拒绝并写原因；同一块板以box登记真实旋转OBB，运行时按50°体力坡爬；probe拒绝不影响实体接触 |

**C06 为什么要有兜底墙**：拒绝一条斜面之后，如果什么都不登记，球会直接穿过视觉上的坡（没有支撑、没有障碍）。作者必须在坡旁补墙，球才会被挡住。本场的做法见 §4.1.2：50° 板以 `role=box` 落库，**同一个 Collider 的两种用途**变成「`staticBoxes`保留这块板的Collider，接触v2登记真实旋转OBB」+「判据导入器把这同一块板的 +Y 面烘成 probe，驱动拿去问 `motor.AddRamp`」。紧凑版 AABB 是 4.000 × 0.729 × 0.656（底在 y = −0.129，顶正好 0.60）；板高 0.60 超过 2.20 档以外所有球的 r，球要慢爬才过得去（体力足够时四档均应到板顶，§5.6）。

**坡顶接缝**：坡顶与坡顶台面的高度差 δ = 0（都 0.60），**坡面**在接缝处高度连续。45° 的坡配齐平台面是接缝最难的一档：45° 是 RIDE、没有爬升上限，所以球一路骑上台面；C05 的 `climbed` 四档都预期通过。

### 4.1.2 C06 的落库形态：作者门 > 45° 要求 box

设计最初写的是「同一个 Collider 进 `rampBoxes` 触发拒绝，同时把它的世界 AABB 放进 `staticBoxes`」。落到工具上撞到一条**更早的门**：

1. `Tools/levelbuild/placement_core.py` 的 `_check_structure` 对 `role=ramp` 有硬门 `slope <= 45 + 0.01`，超过直接 **error**，并在 `LEVEL_AUTHORING` §4.3 写明「坡度 > 45° 时拒绝并要求改成 `box`」。所以 50° 不可能以 `role=ramp` 进库而不把 `levelbuild check` 弄红。
2. 即使硬塞进 `rampBoxes`，`LevelArenaHost.RegisterRamp` 调的是 `SupportSurfaceMath.TryBakeRamp`，它在 **`AddRamp` 之前**就按同一个 45° 护栏拒了。motor 的 `GetLastRampRefusalReason()` / `GetLastRampRefusalSlopeDegrees()` / `GetRampCount()` 根本看不到这块板（拒绝发生在 arena 自己那层），所以「读 motor 的拒绝 getter」也拿不到东西。

于是用**同一块板、两条读数**解决，不改工具、不改 motor / `LevelArenaHost`：

| 用途 | 数据/代码 | 读数 |
|---|---|---|
| 几何 + 兜底墙 | placement：`role=box`、`kind=ramp_probe`、50°、4.0 宽 × 0.20 厚 × 0.783 沿坡 | 0.20 m 厚让世界 AABB = 4.000×0.729×0.656、底 −0.129、顶 0.60。AABB 进 `staticBoxes`，球要慢爬才过得去 |
| 护栏取证 | 判据导入器读 `LevelArenaHost.staticBoxes` 里同名 Collider 的变换，用 `SupportSurfaceMath.TryBakeRamp(..., 89.5°)` **放行**烘出同一个 +Y 面，写进 `PhysicsTestCell.rampProbe*` | 驱动在切格后调 `motor.AddRamp(probe...)`；motor 用真实的 45° 护栏拒绝，`GetLastRampRefusalReason()` 非空、`GetLastRampRefusalSlopeDegrees()` ≈ 50、`GetRampCount()` 仍为 0。这就是 `ramp_refused` / `refusal_slope` / `no_ramp_registered` 三条判据的来源 |

**偏差要读清楚**：arena 自己的 `[arena] ... ramps=0/0 ...` 行里没有 1（因为这块板不在 `rampBoxes`），所以设计原文的「ramps=0/1 + 原因」在本工具链下拆成了「`ramps=0/0`（arena 计数）+ `rampProbe refused=1 slope=50.00 reason=ramp slope exceeds the support limit`（驱动行）」。两者合起来证明同一件事：**50° 被拒绝，且拒绝携带原因**。想让 `ramps=0/1` 字面成立，只能改 `Tools/levelbuild` 的 45° 门（放行对抗性 ramp）并让 arena 的 refuse 路径把原因转发给 motor 的 getter——那需要同时改工具与 `LevelArenaHost` 两处，目前不做。



### 4.1.4 坡道格的分档适用性与阈值推导

某些判据在某个球径下**场景里根本不存在那个情形**（球比坡还大），数据用 `applies_d[4]` 显式声明，驱动报 `SKIPPED_NOT_APPLICABLE`（计入 skipped，另打 `[ptf] notApplicable cell=… checks=…`），不是静默通过。**只有几何规则能触发它，不能用来退休一条真实红灯**；规则由 Python 守卫用 placement 几何重算：

| 规则 | 条件 | 含义 |
|---|---|---|
| R1 `ramp_support` | `ramp_horizontal_run − r > 0` | 台面盒膨胀后的南面必须在坡脚**之后**，球才可能站上坡面 |
| R2 `ramp_entered` | 可达前进 ≥ `0.25·D` | 可达前进 = 台面在球被挡处仍不可踏时的 `run − r`，否则整条 `run` |
| R3 `landing_gap` / `landed_on_ground` | `nominal_m ≥ r` | 台面至少要有球半径那么高，球掉下去才可能干净落地 |

`ramp_entry_progress_m` 取整段最大值（`maxRampEntryM` 锁存），阈值 `min_progress` 每次 run 由驱动按 `0.25·D` 的一步行程推导（日志 `[ptf] derived … min_progress=…`），数据里的 0.30 只是名义值。`applies_d` 缺省 = 四档全适用。

### 4.2 台阶组 C07–C12：高度与球半径的关系

接触模型的台阶规则（CONTACT_MODEL_V2 §2.5、TERRAIN_CLIMB §6）：台阶高 `h < r` 直接滚上去；`r ≤ h < H_cap = 1.5·D` 走爬升通道（贴着立面慢慢爬上去）；`h > H_cap` 是墙。

| 台阶 rise | D=0.30（r 0.15，H_cap 0.45） | D=0.60（0.30 / 0.90） | D=1.10（0.55 / 1.65） | D=2.20（1.10 / 3.30） |
|---|---|---|---|---|
| 0.020 | 滚上 | 滚上 | 滚上 | 滚上 |
| 0.050 | 滚上 | 滚上 | 滚上 | 滚上 |
| 0.075 | 滚上 | 滚上 | 滚上 | 滚上 |
| 0.100 | 滚上 | 滚上 | 滚上 | 滚上 |
| 0.200 | 爬升 | 滚上 | 滚上 | 滚上 |
| 0.400 | 爬升（贴近上限） | 滚上 | 滚上 | 滚上 |

- 已接线的 C08 / C09 / C10 全部四档预期通过。主人 2026-11-17 在地图 0 亲测确认 5/7.5/10 cm 都能上，靠爬墙通道上去符合预期；不要求必须表现为一步几何滚越。上表是几何规则分类，不是对亲测爬升路径的否定。
- **紧凑版每格 1 个盒**（4 m 宽 × 1.5 m 进深），跑道 2.75 m。读数 `deck_gap_m`：真站上过台面 = 0，没上去 = `nominal`。
- **不做「台阶上下各一层」**：下来不需要台阶，支撑回落由重力积分处理。


### 4.3 台面与边缘 C13

| 入口 | 起点 | 需要 | 测什么 |
|---|---|---|---|
| `entry=teleport` | 台面中心上方 `(10.5, 0.6+r, 12)` | 盒顶支撑 | 站上去稳、从北侧直角边掉下去（落地 `= r ± 5 mm`、≥ 3 帧） |
| `entry=ramp` | `(10.5, r, 6.5)`，`heading 0` | 斜面 | 沿南侧 11.31° 坡上台面、接缝不跳 |

- **紧凑版**台面 5×4 m、厚 0.6 m；坡宽 5、水平 3.0 m（11.31°），坡在台面南侧，球沿 +Z 上坡。**为什么仍是 3 m 水平**：2.20 m 球（r = 1.1）在更短的坡上会同时压住坡与台面，读数会变成「骑棱」；3 m 让大球的球心完整走过坡面。
- 直角边**四面都是直角边**（没有扶手）。四条边都能掉下去，判据只用北侧 z=14 那条（机位固定、可复现）。
- 两侧的落差读数用 `support_gap`：**它天然是「球心到当前支撑面的法向距离」**。球在台面上它 = r；球刚越过边缘、脚下换成地面时它 = r + 0.6，然后**按重力积分**下落（`SupportSurfaceMath.ResolveFallState` + `TryIntegrateFall`：半隐式欧拉，每步的 fall budget 同时当抬升与下降钳位，0.6 m 约 18 步 / 3.5 帧）。所以「掉下去」不需要新 getter，只要看 `support_gap` 的时序——但**不要把「每步 ≤ 0.1 m」写进判据**：那是旧的 `maxDropPerStep`，现在只用于地形过渡，不用于自由落体。
- **「站上去稳」是这一格存在的理由**：斜面与台面稳定性绑在一格里，会让人误以为「等坡做完再测台面」。拆成两个入口就没有这个借口。

> **C13 的落库形态**：上面两个入口合成**一次 run**（`entry=ramp` 路线）：球从 z=6.5 起跑（0.5 m 助跑），上南侧 11.31° 坡（水平 3.0、升 0.60）→ 5×4 台面（z 10…14）→ 从北缘掉 0.60 m 落地。`ramp_support` / `seam_no_step` / `climbed` / `landing_gap` / `fall_frames` 在一段轨迹里同时可测；`entry=teleport` 可由操作员改 `start_pose` 临时跑。

### 4.4 薄件与贴墙 C14–C17

| 格 | 几何 | 关键判据 | 为什么这么设计 |
|---|---|---|---|
| **C14** | 紧凑：静态薄板 3×0.028×3（1.5 m 助跑） | 真的碾过去（`progress ≥ 4.5 m`）、速度比 ≥ 0.98、抬升 ≤ 0.028 + 5 mm、不进挡路集、不打满接触缓冲 | 0.028 远低于 r(0.30)=0.15：薄片不是墙，必须被碾过去而不是被隐形墙挡住（PICNIC G9 的原症状）。**FAIL 的那次也是有效证据** |
| **C15** | 薄墙 0.1 厚 × 2.5 高 × 6 宽，9 m 跑道 | 4 m/s 撞：任何一帧 `球心到墙面 ≥ r − 1 cm`；停住时 `= r ± 1 cm`；顶住 2 s 的 `stuckSeconds ≤ 0.5`；倒车 1.5 s 内有速度；**另跑一次 30° 入射**：切向保留 ≥ 95%、法向 ≤ 0.05 m/s | 「高速不穿」只有一个真实威胁：扫掠的 TOI 在眼球帧/长步长下被跳过。0.1 m 是项目里最薄的墙（`COLLIDER_LAYER` 的薄墙档），用它当最坏情况 |
| **C16** | 两面墙交成 90° 内角（横向 6×0.1 + 侧向 0.1×4，高 2.5） | 顶住最后 1 s 位置 σ < 2 mm；最后 0.5 s 速度 ≤ 0.05 m/s；倒车 1.5 s 位移 ≥ 0.4 m；**记录**接触饱和 | 两条法线同时作用 + `ProjectionPasses = 4` 的迭代投影，是「抖动/弹开/永久卡死」三种症状的唯一最小复现。饱和信号只记录：`ContactCapacity = 4` 在这里最可能满，但满≠错 |
| **C17** | 两道门：A 净宽 **0.40**、B 净宽 **0.32**（各 2.5 m 高、0.6 m 厚，间隔 2.7 m） | D=0.30：进 A、在 B 前卡住（`stuck ≥ 0.2 s`）、倒车 + 再前进累计位移 ≥ 0.5 m（不是死锁）；D ≥ 0.60：在 A 前被挡（`= r ± 3 cm`）；**任何球径任何帧都不能越过 B** | 0.40 是用户点名的口径；0.32 = 0.30 + 2 cm 净空，是「几乎贴死」的卡住场景。两个门在同一格，一次跑完「不穿 / 卡住 / 恢复」 |

> **C17 的 0.40 对 D=0.30 是「能过」，对 D=0.60 是「过不去」**——这不是矛盾，是同一几何对不同球径的正确答案。判据表里那一行写的是 `passed_plane_z_m ≤ -28.6`，球径更大会**按预期失败**（`×`），驱动器的 verdict 是 `EXPECTED`。

### 4.5 挡路 → 长大可吸 C18（9 件，按体积门排序）

<!-- table:props -->

| # | 道具（真实模型） | 尺寸 m | AABB m³ | E | D_abs | 0.30 | 0.60 | 1.10 | 2.20 | 角色 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | bread_slice 面包片 | 0.100×0.100×0.014 | 0.0001 | 1.00 | **0.123** | 吸 | 吸 | 吸 | 吸 | blocking_progression |
| 2 | thermos 保温壶 | 0.109×0.105×0.300 | 0.0034 | 2.76 | **0.358** | 挡 | 吸 | 吸 | 吸 | blocking_progression |
| 3 | serving_tray 托盘 | 0.550×0.350×0.040 | 0.0077 | 1.57 | **0.468** | 挡 | 吸 | 吸 | 吸 | blocking_progression |
| 4 | wood_plank 木板 | 1.400×0.350×0.025 | 0.0123 | 4.00 | **0.547** | 挡 | 吸 | 吸 | 吸 | blocking_progression |
| 5 | picnic_basket 野餐篮 | 0.450×0.300×0.221 | 0.0299 | 1.50 | **0.736** | 挡 | 挡 | 吸 | 吸 | blocking_progression |
| 6 | cooler_box 保温箱 | 0.550×0.350×0.300 | 0.0578 | 1.57 | **0.917** | 挡 | 挡 | 吸 | 吸 | blocking_progression |
| 7 | folding_table 折叠桌 | 1.000×0.600×0.700 | 0.4200 | 1.43 | **1.777** | 挡 | 挡 | 挡 | 吸 | blocking_progression |
| 8 | collapsed_bench 倒塌长凳 | 1.600×0.550×0.810 | 0.7128 | 1.98 | **2.120** | 挡 | 挡 | 挡 | 吸 | blocking_progression |
| 9 | ptf_support_crate 测试矮台桩（提议新增，测试专用） | **1.600×1.050×0.500** | 0.8400 | 3.20 | **2.239** | 挡 | 挡 | 挡 | 爬 | support_prop |

- 读法：**吸** = 整 mm 球径 ≥ `Req_mm`（`Req_mm = floor(1000·cbrt(60·0.70·a·b·c/π))`，`D_abs` = `Req_mm/1000`，只有这一个门）；**挡** = 吸不动 **且** 高度 > 球半径（滚不上去）；**爬** = 吸不动 **但** 低于 H_cap（走爬升通道 / 作为支撑）。AABB 列是 a·b·c。0.30 档按量化后的 299 mm 算，`D_abs = 0.299…0.300` 的件（本表没有）才会受影响。
- 表里的数**不是**拍的，是算的：`Req_mm` 用 `PickupAbsorbMath` 同一个公式（`TryComputePickupReqDiameterMm`）。
- **2.20 档在现行门下只剩一个「吸不动但矮」的件**：旧的 crate（1.2×0.5×1.0，Req 2001 mm）和 bench（Req 2120 mm）现在 2.20 球都吸得动；所以 crate 提议改成 **1.600×1.050×0.500**（abc 0.84，Req 2239 mm > 2200，永远吸不动，高 0.5 < 爬升上限 3.3），「同一件道具从挡路变通路」用它判；bench 在 2.20 档变成食物，只打 `value`。C18 迁入时按这个尺寸（§12 U20）。
- **每件单独子运行**（JSON 的 `sub_runs`，10 条：9 件 + 1 条整排走一遍）：球复位到该件正东 `2.2 + r` 处再撞 3 s，只让这一件进挡路/吸收判定。不是容量问题，而是**判据归属问题**：一件一件测，`absorbed_<prop>` 与 `blocked_<prop>` 才能各自归因。
- **容量 6 在本场只能做负向验证**：挡路件都是大件（0.45–1.6 m），2.5 m 间距下球周围最多 3–4 件同时进 `SelectNearest` 的范围——**构造不出「7 个候选」的拥挤场景**（要挤就得让大件的 AABB 互相重叠，那又是另一个错误）。所以 `crowd_capacity` 判据是「`blocking_max ≤ 6` 且不 latch」，正向的夹取行为交给 NUnit `OnlyTheNearestCapacityPropsBlock`。这条限制写在这里，是为了让下一个人不要为了「凑 7 件」去改间距。
- **为什么第一件是能吃的 bread_slice**：它给「食物」一个基线读数（`absorbed_delta ≥ 1` 在四个球径都成立），用来区分「没吃到，因为挡路」与「没吃到，因为吸收环坏了」。这一条在很多 lab 里被漏掉，导致「球不动」时无法判断是几何还是宿主。

### 4.6 长杆/板吸附 C19（单长棍测试）

<!-- table:long -->

| 长物件 | 尺寸 m | E | D_abs | 关键读数 |
|---|---|---|---|---|
| ptf_rod_1500 测试长杆（提议新增，测试专用） | 1.500×0.060×0.060 | 25.00 | 0.416 | 截面 6 cm、长 1.5 m：D=0.60 原始突出 0.45 → 被 cap 0.105 夹住；D=1.10 原始突出 0.20 → 被 cap 0.193 夹住。两个球径都正好打到上限。 |
| wood_plank 木板 | 1.400×0.350×0.025 | 4.00 | 0.547 | 真实模型里最细长的板（E=4.0）；替代 1.5 m 测试杆的现有素材 |
| banana 香蕉 | 0.180×0.068×0.032 | 2.65 | 0.173 | E=2.66 且 0.18 ≥ 0.5×0.30=0.15 → 会被判成长物件，但半长 0.09 < r 0.15 → 突出 0，抬升必须是 0（负对照：不能把「已判长物件」当成「必有凹凸」） |

**「最细长的现有模型」的判定**：`RadialProxyShellMath.TryClassifyLongObject` 要两条同时成立——最长边 ≥ `0.5 × 核心直径`、最长边 ≥ `2.5 × 次长边`。现有 39 件里过线的是 `spoon`（E = 5.15，但只有 0.18 m 长）、`wood_plank`（E = 4.00，1.40 m）、`glass_bottle`（E = 4.00，0.28 m）、`banana`（E = 2.65）、`thermos`（E = 2.76）。

**为什么还要提议一根新杆**：在我们跑的四个球径上，现有 5 件里**只有 `wood_plank` 能突出到核心之外**（0.60 球：半长 0.70 − r 0.30 = 0.40）。其余四件太短：`spoon` 半长 0.09 < r 0.15、`glass_bottle` 半长 0.14 < 0.15、`thermos` 半长 0.15 ≤ r 0.30（0.60 球）、`banana` 半长 0.09 < 0.15 —— **它们全在核心里，抬升恒为 0**，只能当负对照。而 `wood_plank` 是 0.35 m 宽的板：它一次同时提供「沿长轴的极值」与「宽边的 4 个角点」，把「杆」和「板」两种情形混在一起。所以要测**纯杆**（一个方向主导、截面 6 cm）就需要一根**测试专用**的杆。

#### 4.6.1 为什么杆是 1.50 m × 0.06 m × 0.06 m（算术）

| 约束 | 数值 | 检查 |
|---|---|---|
| 可吸（D=0.60） | `V = 1.5×0.06×0.06 = 0.0054 m³` → `D_abs = 0.416` | 0.416 ≤ 0.60 ✓（D=0.30 时不可吸，正好也当挡路样例） |
| 可吸（D=1.10 更大球） | 同上 | ✓ |
| 长物件判定 | `E = 25`，`1.5 ≥ 0.5×1.10 = 0.55` | ✓ |
| 打到突出上限（D=0.60） | 半长 0.75 − r 0.30 = 0.45 ≫ cap 0.105 | **被夹**（这是目的） |
| 打到突出上限（D=1.10） | 半长 0.75 − r 0.55 = 0.20 ≫ cap 0.1925 | **被夹**（同一根杆在两个球径都触上限） |
| 静置不滚 | 方截面 0.06 × 0.06，接触面平 | 不会因为「圆杆」在视觉上看起来在滚（物理上它只是一个 `propBounds`） |

- **材质/视觉**：一根拉长的立方体即可（测试专用，不占美术预算）。它**没有** `manifest.json`，在 JSON 里 `source = "proposed_test_only"`，`manifest = null`。若以后要入美术库，按 `Docs/Art/OriginalModels/props/` 的流程补 manifest。
- **判据为什么是「每步」而不是「每次」**：小棍的上限是**每固定步** 0.04 m（`maxProxyLiftPerStep`）。按帧采样会漏掉一帧内的多步，必须用 `[ProxyTrace]` 的逐行日志（每个固定步一行，上限 400 行 = 8 s @ 50 Hz，够跑一次吸附 + 4 m 行程）。

### 4.7 组合路线 C20

`15° 坡（升 0.60）→ 台面 A (0.60) → 0.10 m 台阶 → 台面 B (0.70) → 从 B 的东边缘掉 0.70`

| 球径 | 预期 | 理由 |
|---|---|---|
| 0.30 / 0.60 / 1.10 / 2.20 | 全程通过 | 0.10 低于每档的球半径（0.15 起），台阶直接滚上 |

- 只用 **1 个斜面 + 2 个盒**，是容量最省的一格，也是**最值得先搭**的一组：它一次覆盖「坡 → 台阶 → 台面 → 边缘」四种支撑转换。
- 判据里 `combo_no_catapult`（单帧竖向 ≤ 0.30 m = 3 × 0.1）与 `combo_no_penetration`（`|support_gap − r| ≤ 5 mm`）是**跨阶段**的：前者抓「弹飞」，后者抓「陷进去」。
- `combo_cost` 用 `active_surface_max ≤ 3`：这条**现在拿不到**（没有 getter），标 `readout = motor-counter`、`gate = null`，等 §6.4 的计数器。

### 4.8 中央起点与球径台 C00

- 起点 `(0, 0.15, 0)`、`heading 0`。0.15 是起始球半径（`startCenterWorld` 的默认值就是这个数）。
- 判据只有四条，但每条都在**验证测量链本身**：`rest_gap == r`、球径误差 ≤ 2 mm、静置 1 s 直径漂移 ≤ 1 mm、原地转不产生竖向位移。
- **为什么把它当一格**：如果 `rest_gap` 在起点就不对，后面 20 格的读数全部不可信。它是**仪器的零点校准**，不是「测试项」。


### 4.9 2026-11-17 的新格：C25 墙 + 顶棚、C26 / C27 / C28 缓坡

四格排在最南一行（z=-33），每格 6×9 m pad，**只跑 D=0.30**（`run_at_d = [true,false,false,false]`，其余三档驱动记 `SKIPPED_NOT_RUN`）：C25 的顶棚净高 0.50 m 小于 0.60 以上球的直径；缓坡 pad 只有 9 m，装不下 s 倍速度的大球（规则本身与球径无关，BALL_INERTIA §1）。

#### C25 墙 + 顶棚（诊断，不判 PASS/FAIL）

对应 TERRAIN_CLIMB §6.7：球顶住竖墙往上爬，顶棚把它压回去，`climbActive` 可一直为真而球没有进展。主人 2026-11-17 亲测确认这是正常受阻，玩家后退脱出；不做按进度释放，也不将无进展判成卡死。几何参照离线夹具（r=0.15，墙前 z=0.25，顶棚底 0.50，爬升高度 h=0.20）：

| 件 | 尺寸 / 位置（世界 m，格心 cx=-10.5, cz=-33） | 说明 |
|---|---|---|
| 墙 `C25_wall` | 4.0 × 2.0 × 0.4，中心 (cx, 1.0, cz+1.2)；前面 z=cz+1.0 | 2.0 m ≫ H_cap（0.45）：翻不上墙顶 |
| 顶棚 `C25_ceiling` | 4.0 × 0.2 × 2.0，中心 (cx, 0.60, cz)；**底面 y=0.50**，z∈[cz-1, cz+1]，贴在墙前 | 0.50 m 在球顶（0.30）之上、休息高度 + H_cap（0.75）之内：球爬到 h=0.20（球心 0.35）时球顶刚好碰到顶棚 |
| 起点 | (cx, 0.15, cz-2.2)，朝 +Z | 1.2 m 助跑进顶棚下 |

驱动时间线：t=0 满推；t=8 s 松手（看能否自己落下）；t=11 s 反推 -1 共 2.5 s（看能否退出）；t=13.5 s 松手，再测 1 s。读数（全部 `op=record`，`verdict=DIAGNOSTIC`）：

| 判据 id | metric | 读法 |
|---|---|---|
| `climb_active_at_release` | `climb_active_at_release` | 松手那一帧 `IsClimbing()`（1 = 仍在爬） |
| `cap_lock_at_release` | `cap_lock_at_release` | 松手那一帧 `IsClimbLocked()`（高度上限锁） |
| `climb_active_s` / `climb_lock_s` | 同名 | 整段这两个状态为真的累计秒数 |
| `climb_height_max_m` | 同名 | 整段最大 `GetClimbHeightWorldM()` |
| `push_actual_speed_mps` | 同名 | 松手前 2 s 最终接受球心的实际速度：平面速度对竖墙爬升恒为 0，不能用它判进展；≈0 而 `climbActive` 为真 = 通道无进展 |
| `released_descent_m` | 同名 | 松手到反推开始球心下降多少（>0 自己下来了；≈0 挂在墙上） |
| `reverse_retreat_m` | 同名 | 反推后沿朝向向后退出的距离（>0 能退出） |
| `final_bottom_rise_m` | 同名 | 结束时球底离地高度 |
| `recovery_delta` | 同名 | 本 run 的卡死恢复次数 |

另有 `size_accuracy` / `no_overflow` 两条正常判据（D=0.30 pass）。要同时看电机每秒一行的 `[BallMotor] climb`（含 `climbActive` / `capLock` / `height` / `actualSpeed` / `resid`），打开电机的 `climbDiagnosticsEnabled`（§9.5.2），读完关回。

#### C26 / C27 / C28 缓坡 2° / 5° / 10°（坡滑规则，BALL_INERTIA §4）

| 格 | 坡 | 升 / 水平 | x |
|---|---|---|---|
| C26 | 2° | 0.192 / 5.5 | -3.5 |
| C27 | 5° | 0.481 / 5.5 | 3.5 |
| C28 | 10° | 0.970 / 5.5 | 10.5 |

平地助跑 2 m（z=cz-4.0 → 坡脚 cz-2.0），坡沿 +Z 上升，顶端接 1 m 深的平台。三格同一份时间线：

| t (s) | 输入 | 目的 |
|---|---|---|
| 0–1.5 | 全部为 0 | 平地静止，读 `flat_drift_m`：平地不滑 |
| 1.5 | forward 0.25 | 四分之一推力推 3 s：平地 2 m 后上坡。推着时下坡重力不应加进来（`push_speed_ratio`） |
| 4.5 | forward 0，turn +1 | 松手并原地右转 0.82 s（朝向 90°，下坡成了横向，不被前向滚动阻力 1.9 m/s² 吞掉） |
| 5.32 | turn 0 | 停转；球滑停后静止 0.3 s 开始下滑，再看到 11.0 s |

判据（`flat_drift_m` 等是驱动的新读数，§5.2）：

| 判据 | 读数 | C27 5° / C28 10° | C26 2° |
|---|---|---|---|
| `flat_no_slide` | `flat_drift_m` | ≤ 0.01 m | 只记录 |
| `slides_after_rest` | `slide_delay_s`（速度 < 0.05 起到下坡速度 > 0.1 m/s 的时间） | ≥ 0.25 s | 只记录 |
| `slides_promptly` | 同上 | ≤ 0.6 s（99 = 根本没滑） | — |
| `slide_speed` | `slide_peak_speed_mps` | ≥ 0.15 / ≥ 0.30 m/s（终速 g·sinθ/2.9 的一半以上：0.30 / 0.59 m/s） | 只记录 |
| `push_not_slowed` | `push_speed_ratio`（推着时坡上最高速 / 平地最高速） | ≥ 0.9 | 只记录 |

**按现行 WLK A2，C26–C28 顺坡松手不滑。** 上表是脚本主动转成横向后的诊断，验收窗口不变；还要单独手测 C28 顺坡静止不滑。2° 正好在 `rampSlideMinSlopeDegrees` 的边界上，只记录。5° / 10° 的沿坡重力约 0.855 / 1.70 m/s²，低于 1.9 m/s² 的纵向滚阻。**已有的坡没有复用**：C01（5°）和 C03（25°）是爬坡格，时间线是上台面，没有「静止后观察下滑」的窗口，加窗口会动 `climbed` 的时间线；C03 的 `slip_back`（25° 坡上停 5 s 后下滑的距离）因此改成只记录，不再要求「不往回滑」。


## 5. 判据字典（metric → 怎么读 → 怎么判）

### 5.1 读数来源的三条通道

| 通道 | 来源 | 能拿到什么 | 局限 |
|---|---|---|---|
| **A. 每帧 getter** | `KatamariBallMotor` 现有公开方法 + `PickupFieldHost` 计数 | 球心、直径、`support_gap`、`lift`、`cap`、速度、朝向、`stuckSeconds`、代理点数/候选数、吸收/拒绝计数、挡路计数 | **每帧**一次，不是每步；一帧最多 5 个固定步，所以「每步」量只能算**下界/上界** |
| **B. 每固定步日志** | `debugTraceProxySupport = true` 时的 `[ProxyTrace]` 行：`t support core lift cap points candidates centerY` | 真正的**每步**支撑高度、抬升、cap、代理点数 | 只在**地板解算成功**时打（`groundAccepted`），上限 400 行；打开就有日志开销，**只在需要的格里开** |
| **C. 待加计数器** | 尚无（`GetStepMaskRebuildCount()` 已有，但它的语义是「台阶分类翻转次数」，不是每步成本） | `activeSurfaceCount`、`stepMicros`、`stepCount` | 现在**测不到**；列为 §6.4 的接口请求，不假装能测（`readout=counter` 报 `BLOCKED_UNTIL_P4`） |

### 5.2 度量表

| metric | 定义 | 读法 | 通道 | 典型 limit |
|---|---|---|---|---|
| `support_gap_m` | `(center.y − 碰撞半径) − GetSupportSurfaceHeightWorldM()`（测试场驱动；**Story 的 `gap` 已改口径**：沿支撑法线 `(center.y − supportY)·n.y − r`，见 §5.2.1） | 球底到**当前支撑面**的竖直差：站得住 = 0（挡在台阶前时它落在「地面」上，同样是 0，所以它判「陷/浮」而不是「上没上去」）。**斜面上竖直差 = r(1/n.y − 1) 非零**，所以斜面格不用它，用 `support_kind` / `deck_gap` / `landing_gap` | A | `abs ≤ 0.005` |
| `deck_gap_m` | 整段里 `min abs((center.y − 碰撞半径) − nominal_m)` | **上没上去**：真站上过台面 = 0；从没上去 = `nominal`。取整段最小值，所以「爬上去又从东边掉下来」仍然算上去过 | A | `abs ≤ 0.005` |
| `lift_over_nominal_m` | 整段里 `max((center.y − 碰撞半径) − nominal_m)` | 抬升只能来自本格那个 `nominal` 高的板/台阶；爬到别的东西（隐形台阶、错盒）就超 | A | `≤ 0.010` |
| `blocked_gap_m` | `approach_m − progress_m − 碰撞半径` | 被挡时球心到台阶立面的距离。`approach_m` 由导入器**读 arena 的 `staticBoxes`** 算出（起点沿 heading 第一个进入的盒），所以 0 = 停在立面上、大数 = 提前停住（隐形墙） | A | `abs ≤ 0.010` |
| `deck_y_std_m` | 静置窗口内 `center.y` 的标准差 | 抖动 | A | `≤ 0.001`（S3 口径）；台阶格放宽到 0.002 |
| `frame_dy_m` | 相邻两帧 `abs(Δcenter.y)` | 跳变/弹飞 | A | `≤ 0.02`（接缝）/ `≤ 0.30`（组合） |
| `frame_drop_m` | 相邻两帧的**下落**量 | 反 teleport 上界（重力积分下 0.6 m 落差单帧 ≈ 0.35 m） | A | `≤ 0.5` |
| `fall_frames` | 从离开台面到落地的帧数 | 排除 teleport 式瞬移 | A | `≥ 3` |
| `landing_gap_m` | 落地后 `abs(center.y − r)` | 落地是否贴地 | A | `≤ 0.005` |
| `step_dy_m` | 相邻两条 `[ProxyTrace]` 的 `abs(ΔcenterY)` | **每步**抬升 | B | 长杆 `≤ 0.045` |
| `blocked_gap_m` | 沿运动轴：`球心到障碍面距离 − r` | 挡没挡住、穿没穿 | A | 挡 `abs ≤ 0.01…0.03`；不穿 `≥ -0.01` |
| `penetration_depth_m` | `max(0, r − support_gap)` | 陷进支撑面的深度 | A | `≤ 0.005` |
| `progress_m` | 沿格轴线的累计位移 | 有没有真的走过去 | A | C14 `≥ 8` |
| `passed_plane_z_m` | 球心越过某平面的最远 z | 夹缝过没过 | A | `≤ -28.6` |
| `min_center_z_m` | 整段的最小 `center.z` | 有没有穿过后门 | A | `≥ -31.3` |
| `speed_mps` / `speed_ratio` | `abs(GetPlanarVelocity())` 及其前后比 | 减速/扰动 | A | `≥ 0.98`（薄片）、`≥ 0.95`（贴墙滑）、`≥ 0.99`（长杆） |
| `normal_speed_mps` | 速度在墙面法线上的分量 | 法向去速是否干净 | A | `≤ 0.05` |
| `impact_speed_jump_mps` | 吸附事件前后一帧的速度差 | 「弹飞」 | A | `≤ 0.02` |
| `xz_std_m` | 顶住时位置的横向标准差 | 内角抖动 | A | `≤ 0.002` |
| `escape_displacement_m` | 倒车后相对卡点的位移 | 能出来 | A | `≥ 0.40` / `≥ 0.50` |
| `stuck_max_s` | 窗口内 `GetStuckSeconds()` 的最大值 | 卡住时长 | A | `≤ 0.50`（薄墙）；`≥ 0.20`（夹缝：证明真的卡过） |
| `protrusion_ratio` | `GetGroundProxyLiftWorldM() / GetProxyProtrusionCapWorldM()` | 突出是否被夹住 | A | `≤ 1.001`；负对照 `≤ 0.02` |
| `proxy_points` / `proxy_items` | `GetProxyPointCount()` / `GetProxyItemCount()` | 吸附物进没进针（每件 8 点；不再只收长物件） | A | `≥ 8` |
| `roll_distance_ratio` | `abs(滚动角 × r − 位移) / 位移` | 无滑滚 | A | `≤ 0.10` |
| `diameter_error_m` | `motor.GetSemanticDiameterWorldM() − presetDiametersM[diameterIndex]`（米，带符号；判据用 `abs<=`） | 设定器是否把球放到了预设直径。步骤 C+D 后成长量子是 1 mm³（约 7 nm 直径当量），所以 1 mm 是判据限，不是物理下限（§8.3） | A | `abs<= 0.001` |
| `max_center_y_m` | 整段窗口内 `center.y` 的最大值 | 「最高只到 r + 一点」= 没爬上去 | A | 按格 |
| `max_bottom_rise_m` | `max(center.y) − 碰撞半径` | C06：球底峰值应到0.60m板顶，体力足够不受墙cap约束 | A | `≥0.58` |
| `ramp_support_seen` | 整段里是否至少一帧 `GetSupportKind() == 2`（0/1） | 球真的被斜面撑着；终点在台面时 `support_kind` 已经是 box，所以需要这个逐帧锁存 | A | `== 1` |
| `grounded_frame_dy_m` | 非下落帧里相邻两帧 `abs(Δcenter.y)` 的最大值 | 坡顶接缝不顿、不弹（S14）；自由落体帧被排除，否则 0.6 m 掉落会冒充「接缝跳变」 | A | `≤ 0.05` |
| `ramp_refusal_slope_deg` | `GetLastRampRefusalSlopeDegrees()`（切格后 probe 的读数） | 拒绝的是不是那块 50° 板 | A | `≥ 49` |
| `ramp_entry_progress_m` | 整段里 `max(progress − rampFootM)`（**锁存最大值**，不是结束时刻的位置） | 坡底没有隐形墙（§5.5 陷阱）。结束在坡后方的 run 会误读结束位置，C13@2.20 就因此读到 0.10 | A | `≥ stepLimit`（=0.25·D，驱动每次 run 推导；数据里的 0.30 是名义值） |
| `slip_back_m` | 停坡 5 s 内沿坡向下的位移 | 「无切向重力」是否成立（§5.4 U3） | A | `abs ≤ 0.020` |
| `recover_speed_mps` | 倒车 1.5 s 内的最大速度 | 离开墙后能动 | A | `≥ 0.20` |
| `elapsed_s` | 一次 run 的墙钟时长 | 没有隐性卡死 | A | `≤ 20` |
| `absorbed_delta` / `attached_delta` | 相对基线的增量 | 吃没吃到 | A | `≥ 1` |
| `blocking_max` | 窗口内 `GetBlockingPropCount()` 的最大值 | 挡路集规模 | A | `≤ 6` |
| `saturated_delta` | `WasContactBufferSaturated()` 的**基线差** | 接触缓冲是否满过 | A | 多数格 `== 0`；内角/夹缝只记录 |
| `overflow_delta` | `GetOverflowCount()` 的**基线差** | 有没有超容量 | A | `== 0`（除 C16 的容量测试） |
| `support_kind` | 支撑面种类（`ground/box/ramp/prop`） | 站的是什么 | **P1/P2/P3 getter** | `== prop` / `== ground` |
| `ramp_count` / `ramp_refusal_reason_len` | `motor.GetRampCount()` / `motor.GetLastRampRefusalReason().Length`（切格后） | 斜面护栏：接受了几条、拒绝原因有没有 | **P3 getter** | 可上格 `== 1`；C06 `== 0` / `≥ 1` |
| `sets_disjoint` | 挡路集与支撑集无交集 | 原子翻转 | **P2 getter** | `== 1` |
| `active_surface_max` | 每步激活支撑面数 | 成本 | **待加计数器** | `≤ 3` |
| `climb_active_at_release` / `cap_lock_at_release` | 松手那一帧 `IsClimbing()` / `IsClimbLocked()`（0/1） | 爬升通道与高度上限锁的状态 | A | 记录 |
| `climb_active_s` / `climb_lock_s` | 整段这两个状态为真的累计秒数 | 爬了多久、锁了多久 | A | 记录 |
| `climb_height_max_m` | 整段最大 `GetClimbHeightWorldM()` | 爬到多高 | A | 记录 |
| `push_actual_speed_mps` | 松手前 2 s 最终接受球心位移 / 时间 | 竖墙爬升平面速度恒 0，这个才是「球实际动没动」 | A | 记录 |
| `released_descent_m` | 松手到反推开始的球心下降 | 松手后能否自己下来 | A | 记录 |
| `reverse_retreat_m` | 反推后沿朝向向后退出的距离 | 倒着推能否退出 | A | 记录 |
| `final_bottom_rise_m` | 结束时 `center.y − 碰撞半径` | 落没落地 | A | 记录 |
| `recovery_delta` | `GetRecoveryCount()` 的基线差 | 卡死恢复触发过没有 | A | 记录 / `== 0` |
| `flat_drift_m` | 第一次推动之前离起点的最大水平位移 | 平地上不滑 | A | `<= 0.01` |
| `slide_delay_s` | 松手后速度 < 0.05 起，到沿坡下坡速度 > 0.1 m/s 的时间；99 = 没滑 | 静止 0.3 s 后开始下滑 | A | `0.25…0.6` |
| `slide_peak_speed_mps` | 静止后沿坡向下的最大速度 | 下滑够不够快 | A | `>= 0.15…0.3` |
| `push_speed_ratio` | 推着时坡上最高速 / 平地最高速 | 推着不额外下滑 | A | `>= 0.9` |

#### 5.2.1 读数口径：`gap` 沿支撑法线（R11）

`StoryBallDiagnostics` 的 `gap` 取沿支撑法线的间隙，而不是 `bottom − supportY`：在斜面上后者不是「球心到支撑面」的距离：平面方程给出 `center.y = supportY + r / n.y`，所以竖直差恒为 `r(1/n.y − 1)`（45°、r=1.10 时 0.455 m），看起来像球一直浮着。所以把间隙投到支撑法线上（R11）：

```
gap = (center.y − GetSupportSurfaceHeightWorldM()) * GetSupportNormalWorld().y − 碰撞半径
```

平面上的球心到平面距离 = `(center.y − supportY)*n.y`（探针与球心同一 XZ），减半径即「沿法线的间隙」。平地 `n.y = 1`，数值与旧口径相同（浮点结合序可能差 1 ulp）；`.y` 不可信（无支撑/墙）时回退到 1，与旧行为一致。两个文件同口径；Udon 侧只多读一个已有 getter（`GetSupportNormalWorld()`，`Runtime/BallMotor/KatamariBallMotor.cs:3191`），没有新增接口。**测试场驱动的 `support_gap_m` 仍是竖直差**（它只在台阶/薄片格用），斜面格的判据改用 `support_kind` / `deck_gap` / `landing_gap` 等不依赖法线的读数。

### 5.3 三态判据与四态结论

每条判据有两个字段：`expect`（按球径给 `pass/fail/n/a`）与 `gate`（哪个阶段之后才有意义）。运行器据此产出**四态**结论：

| 结论 | 条件 | 含义 |
|---|---|---|
| `EXPECTED` | 实测 == `expect` | 行为符合设计（**包括预期失败**） |
| `UNEXPECTED_PASS` | `expect=fail` 但实测通过 | 要么实现提前好了，要么判据写松了 —— **必须人看** |
| `UNEXPECTED_FAIL` | `expect=pass` 但实测失败 | 回归/缺陷 |
| `BLOCKED_UNTIL_<gate>` | 该阶段不存在 / 该读数这个版本拿不到，跳过 | 不是失败，是「还测不了」 |
| `SKIPPED_N_A` | `expect = n/a`（该档没有名义答案） | 跳过，不是失败 |
| `DIAGNOSTIC` | `op = record`：读数只记录，不判 | 日志 `[ptf] check=… value=… op=record raw=RECORDED verdict=DIAGNOSTIC`；**不计 pass、不计 unexpected、不计 skipped**，summary 里单独一个 `diag=` 计数 |
| `SKIPPED_NOT_RUN` | 这格 `run_at_d[档] = false` | 这一档整个 run 不开（球放进去也没意义），日志 `[ptf] skipped run=… reason=cell_does_not_run_at_this_diameter` |
| `SKIPPED_NOT_APPLICABLE` | `applies_d[档] = false`：该档的**场景在物理上不存在**（§4.1.4 的 R1/R2/R3） | 跳过，不是失败；报告里另有一行 `[ptf] notApplicable cell=… D=… checks=…` 点名。**它不能用来退休一条真实红灯**：只有几何规则能触发，规则由 Python 守卫重算校验 |

`BLOCKED_UNTIL` 的五种来源，驱动各打一种 token（都计入 `skipped`，不计入 `unexpected`）：

| token | 什么时候 | 谁能解开 |
|---|---|---|
| `BLOCKED_UNTIL_P2` | 判据声明 `gate=P2`，但场景里没有接 `PickupFieldHost`，或宿主关掉了道具障碍（`SetSupportProps` 的唯一调用方） | P2 的 getter 已进树；接上宿主且 `IsBlockingEnabled()` 为 true 后判据自动可测 |
| `BLOCKED_UNTIL_P3` | **不产生**：P3 的斜面接口（`AddRamp`）已进树（d31dc9b） | 驱动把 `gate=P3` 当普通判据测；token 只留在数据的 gate 词汇里 |
| `BLOCKED_UNTIL_manual` | `readout=trace`：读数只存在于 `[ProxyTrace]` 的逐行日志里，驱动读不到自己的 Console | 人（grep console） |
| `BLOCKED_UNTIL_P4` | `readout=counter`：每步成本计数器还不存在 | §6.4 的接口请求 |
| `BLOCKED_UNTIL_reset` / `_size` / `_setter` / `_setup` | 这一 run 根本没跑起来（复位失败 / 球径没收敛 / 没挂设定器 / 场景没接好） | 操作员；这是**场景问题**不是物理结论 |

> **为什么要有 `UNEXPECTED_PASS`**：判据期望失败而实测通过，最可能的解释不是「实现提前好了」，而是**几何根本没注册上**（球从空气里滚过去了）。把这种情况单独命名，就不会被全绿掩盖。

### 5.4 latch 语义：所有「是否曾经」信号都要读基线

`WasContactBufferSaturated()`、`GetOverflowCount()`、`GetFirstOverflowReason()` 都是**永久 latch**（只有 `Start` 清）。所以：

```
格开始时：baseSaturated = motor.WasContactBufferSaturated();  baseOverflow = motor.GetOverflowCount();
格结束时：delta = (now != base) —— 不要读绝对值
```

`stuckSeconds` 相反：它是**瞬时**量（每步 `+dt` 或清零），所以判据要取**窗口内最大值**，不是结束时的值（结束往往已经清零）。

### 5.5 等号纪律

| 位置 | 比较 | 影响 |
|---|---|---|
| `slope ≤ maxSupportSlopeDegrees` | 45.0° | C05 应被接受；45.1° 应拒绝 |
| `absorbable ⟺ floor(1000·D) ≥ Req_mm` | 整 mm 相等算可吸 | 挡路行里 `D_abs` 刚好等于球径的道具算「吸」 |
| `rampSlideMinSlopeDegrees = 2°` | 坡角 **大于** 2° 才滑 | C26 的 2.0° 在边界上，只记录 |

全部按含等号 / 严格大于写；**改成别的会同时翻几处**，请一次改完并更新 JSON 的 `expect`。

### 5.6 覆盖行与只记录行

- **一条判据就是一行**，没有按接触模型分两套：驱动按metric/op/limit/expect判。C06改为`slope_top_reached`（球底峰值≥0.58m），不再拿墙cap当坡判据。`climb_cap_bound`符号阈值仅为W2等墙测试保留；覆盖块闭集与导入器不变。
- **`op = record`**：诊断行，见 §5.3。写在数据里的 `expect` 必须全为 `n/a`；Python 守卫会检查。
- **`run_at_d`**：每格可选，4 个布尔，缺省全跑。
- **`ramp_registered`** 读 `motor.GetStaticObbCount() >= 1`（坡烘成静态 OBB，`GetRampCount()` 恒为 0）；Play 内用诊断脚本读它之前要先 `UdonSharpEditorUtility.CopyUdonToProxy`，驱动本身在 Udon 里直接读 getter。
- **`lift_over_nominal_in_deck_m`**（C14）：只在球 XZ 落在板足印 ±1 个碰撞半径内累计；足印由导入器从 arena 的 `staticBoxes` 烘进 `deckCenterXZ / deckHalfSizeM`，判据文件里一个数都没有。
- **新格位的判据先落数据**：`planned_cells[]` 里的 W1–W6、R1/R2、S1、X1 十格是设计行（`status: pending_scene`，`wired: false`，`design_checks[]`），导入器不读，要做某格时：写 placement → `levelbuild check` → 把判据搬成 `checks[]` → 删 `status`。其中 `metric_available: false` 的读数现在没有驱动 reader，先写判据、再写 reader，不拿近似值假装测过。

---

## 6. 观测与记录

### 6.1 一次运行（run）的标准协议

**实现方式**（`PhysicsTestFieldDriver`）：每个阶段占一帧段，在 `Update` 里推进，不阻塞。

```
1. 注册：沙盒已经注册（所有格的板同时在电机里）；脚本 run 不切格、不清表，只把球传到本格起点（`TeleportTo`）。run 开始时若沙盒还没注册（`autoBeginAllOnStart`），先 `EnterSandbox()`


2. 白名单：motor.SetAllowedKinds(kindWhitelist)（arena 自己那份只有本格道具的 kind，见 §9.5）
3. 记基线：baseOverflow / baseSaturated / baseAbsorbed / baseAttached（latch 量只能读差）
4. 复位球：motor.TryResetForNewRound("ptf-<cell>-<run>", (x, 碰撞半径+0.02, z), heading, out reason)
        —— 它把球重置为 baseline 直径（0.30）并落到格起点；reason 必须为 0，否则 BLOCKED
5. 复位道具：pickupHost.ResetField()（恢复父/位姿/激活，清候选/挡路/径向代理）
6. 设球径：ballSize.SetDiameterM(D)，等 IsConverged()（≤ 6 次吸收；网格下限见 §8.3）
7. 静置 settleSeconds = 0.5 s
8. 跑脚本：按 cell.drive 的 (t, forward, turn, lateral) 调 motor.SetMoveAxes(...)，同时每帧采样
9. 到 driveEnd + stopSeconds 停；停后再采一帧（读数描述「停住的球」而不是停之前那一帧）
10. 算判据 → 每条一行日志 → 一格一行 summary；traj 每 0.5 s 一行
11. 换下一个球径；四个球径跑完换下一格
```

**顺序不能换的三处**：`TryResetForNewRound` **必须晚于**切格（它按新 arena 的地面落地）；**必须早于** `SetDiameter`（它把球按 baseline 体积重新定价，先设径就白做）；`ResetField` **必须晚于** `TryResetForNewRound`（前者要清掉后者可能留下的候选/代理）。

**为什么球径是「喂上去」而不是直接设**：直径是账本体积的纯函数，没有 setter（§8）。`TryResetForNewRound` 负责「回 0.30」，`SubmitCandidate` 负责「长大」。

**球心高度**：`start_pose.center.y` 只是作者标注（0.15 = 起始半径）。运行时驱动写 `y = r(D) + 0.02` 再让接触求解把它压到 `y = 支撑面 + r`，这样任何球径都不会一开始就嵌进地面。

### 6.2 日志格式（固定、可被脚本 grep）

```
[ptf] run=3 cell=C10 D=0.300 start=(7.500,0.170,1.000) hdg=90.0 arena=planes=4/boxes=1/supports=2 gates=on_step->P1
[ptf] check=on_step metric=deck_gap_m value=-0.1199 op=abs<=0.0050 expect=fail raw=FAIL verdict=EXPECTED
[ptf] check=rest_gap metric=support_gap_m value=+0.0000 op=abs<=0.0050 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=blocked_gap metric=blocked_gap_m value=-0.0007 op=abs<=0.0100 expect=pass raw=PASS verdict=EXPECTED
[ptf] check=size_accuracy metric=diameter_error_m value=-0.0000 op=abs<=0.0010 expect=pass raw=PASS verdict=EXPECTED
[ptf] summary cell=C10 D=0.300 checks=6 pass=6 unexpected=0 skipped=0 diag=0 verdict=EXPECTED elapsed=5.50s
[ptf] traj t=2.50 p=(11.85,0.17,1.00) sup=0.000 stuck=1.20
[ptf] field cells=4 runs=16 verdict=EXPECTED unexpected=0 skipped=0 elapsed=94s
```

规则：
- **一条判据一行**，字段顺序固定；`value` 恒为 `%+.4f`（带符号、四位小数、不开科学计数），由驱动自己的整数格式化打印（UdonSharp 的 `ToString("F4")` 不在本项目已验证的 extern 集合里，见 `PhysicsTestFieldDriver.Fixed`）。
- 一格一行 `summary`，一次 pass 一行 `field`；`run=` 行在切格成功后立刻打，带 `arena=planes/boxes/supports`（这是「arena 真的注册上了」的唯一证据）与 `gates=`（哪些判据被 gate 挡住了）。
- gate/readout 挡住的判据仍然各占一行：`value=n/a` + `raw=SKIPPED` + `verdict=BLOCKED_UNTIL_*`，所以「少了一行」永远不代表「通过了」，也不会出现一个假的 `0.0000`。
- **`pass=` 是「符合预期的判据数」，不是「物理通过数」**：C10 在 0.30 上的 `on_step` 物理上 `raw=FAIL`，但它 `expect=fail`，所以计入 `pass=`；物理事实只在每条判据行的 `raw=` 里。`unexpected=` 才是红灯计数。
- **`traj` 行每 0.5 s 一条**（单次运行 ≤ 40 行），字段是重建轨迹 CSV 的最小集合：`t, p, sup, lift, cap, stuck`。这样证据不需要额外文件格式。
- 任何 `UNEXPECTED_*` 额外一条 `[ptf] ALERT ...`，**并在 Summary 里计数**（不要只在 Console 里红一下）。
- 所有数字**打印事实**，不打印解释（`verdict=` 是结论，`value=` 是事实；不要在 value 里写 "ok"）。

### 6.3 每格要读的数值（最小集）

| 类别 | 读数 | 用途 |
|---|---|---|
| 位置 | `center`（逐帧）、`traj`（每 0.5 s）、`heading` | 轨迹、位移、绕行 |
| 支撑 | `support_gap`、`lift`、`cap`、`support_kind`（P1+）、`support_normal`（P1+） | 贴地、凹凸、法线是否朝上、滚动法线是否正确 |
| 接触 | `saturated_delta`、`stuck_max_s`、`proxy_points`、`proxy_items` | 卡住、饱和、吸附物进针 |
| 吸收/挡路 | `absorbed_delta`、`attached_delta`、`blocking_max`、`blocking_candidates`、`absorbed_event` 的 id | 门与分类是否原子翻转 |
| 成本 | `active_surface_max`、`mask_rebuild`、`step_micros`（**待加**） | P4 的帧时间与分配 |
| 几何 | 该格的 `expected_ramp_count` / `GetRampCount()`（P3）、`GetLastRampRefusalReason()`（P3） | 斜面注册是否被正确拒绝 |

### 6.4 现在拿不到、需要加的读数（给实现者的接口请求）

**已经在树里**：P1 的 `GetSupportSurfaceHeightWorldM()`、`GetSupportNormalWorld()`、`GetSupportSurfaceId()`、`GetSupportSurfaceCount()`、`GetSupportKind()`（0 地面 / 1 盒 / 2 斜面 / 3 道具 / -1 无面）、`GetStepHeightLimitWorldM()`、`IsFalling()`、`GetStepMaskRebuildCount()`；P2 的 `GetSupportPropCount()` 等；**P3 的斜面家族**全在同一个文件（行号见下）。驱动的 `support_gap_m` / `deck_gap_m` / `blocked_gap_m` / 斜面判据全部建立在它们之上。

| P3 getter | 签名 | 文件:行 | 用途 |
|---|---|---|---|
| `GetRampCount()` | `public int GetRampCount()` | `Runtime/BallMotor/KatamariBallMotor.cs:2228` | `ramp_count`（C01/C03/C05/C13 `==1`；C06 `==0`） |
| `GetLastRampRefusalReason()` | `public string GetLastRampRefusalReason()` | `…/KatamariBallMotor.cs:2255` | `ramp_refusal_reason_len`（C06 `>=1`） |
| `GetLastRampRefusalSlopeDegrees()` | `public float GetLastRampRefusalSlopeDegrees()` | `…/KatamariBallMotor.cs:2261` | `ramp_refusal_slope_deg`（C06 `>=49`） |
| `GetLastRampRefusalCode()` | `public int GetLastRampRefusalCode()` | `…/KatamariBallMotor.cs:2249` | 供人读（4 = slope too steep） |
| `GetRampCapacity()` / `GetMaxSupportSlopeDegrees()` | `int` / `float` | `…/KatamariBallMotor.cs:2234, 2267` | 45° 等号的证据 |
| `SuspendRegistration()` / `ResumeRegistration()` / `IsRegistrationSuspended()` | `void` / `void` / `bool` | `Runtime/Story/LevelArenaHost.cs` | 沙盒里所有宿主启动时挂起（不自注册），由驱动器统一 `Register`；`Update` 在 suspended 时返回，显式 `Register` / `Reapply` 不受影响。EditMode 用例在 `Tests/EditMode/Story/StoryRoundControllerTests.cs` |

仍然缺的（只有这些会报 `BLOCKED_UNTIL_*`）：

| 请求 | 签名草案 | 为什么需要 | 优先级 |
|---|---|---|---|
| 每步成本 | `public int GetLastActiveSurfaceCount();` `public float GetLastStepMicros();` `public int GetFixedStepCount();`（`GetStepMaskRebuildCount()` 已有，但语义是「分类翻转次数」） | `combo_cost` 与 §3.3 的切格 GC 必须实测；`Time.realtimeSinceStartup` 夹两端只能测整帧 | P4 |

> **不要**为了测试场去加「测试专用」的 getter 到运行时代码里；上表都是**产品也需要**的诊断（HUD 读同样的值）。C06 的 probe 用的是**已有** `AddRamp` 与拒绝 getter，没有为测试场新增任何运行时接口：作者门在 45° 之上要求把板写成 box（§4.1.2），所以探针是唯一能让 motor 自己拒一次的办法。

### 6.5 固定机位清单（截图可复现）

| id | 类型 | 位置 / 目标 | 拍什么 | 用在哪几格 |
|---|---|---|---|---|
| `cam_top` | 固定 | `(0, 60, 0.75)` → `(0, 0, 0.75)`，ortho coverage 34 | 紧凑全场总览（先拍这张证明摆位） | 所有格（每 run 必拍） |
| `cam_plaza` | 固定 | `(-9, 3.2, -7)` → `(0, 0.4, 0)` | 起点 + 球径台近景 | C00 |
| `cam_ramps` | 固定 | `(26, 9, 24)` → `(-1, 2.2, 22)` | 斜板立面：坡长、坡顶接缝、落差一帧读完 | C01–C06 |
| `cam_steps` | 固定 | `(12, 6.5, -22)` → `(13, 0.8, -2)` | 台阶立面：挡与上同画面 | C07–C12 |
| `cam_thin` | 固定 | `(2, 9, -19)` → `(4, 1, -30)` | 薄墙 / 内角 / 夹缝共用 | C14–C17 |
| `cam_deck` | 固定 | `(6, 3.5, 5)` → `(10.5, 0.7, 12)` | 台面稳定 + 北缘落差 | C13 |
| `cam_west` | 固定 | `(-4, 8, 8)` → `(-22, 0.8, -2)` | 挡路行 + 长杆 | C18、C19、C20 |
| `cam_chase` | 跟球 | 相对球 `(0, 3.2, -4.6)`，看 `(0, 0.35, 0)` | 锁定球的视角：滚动起伏、贴墙、跌落 | C19（起伏）、C13（跌落） |

- 机位全部是**固定 Transform**（除 chase），所以两次 run 的截图可以直接叠图比对（这是「手感变了」最便宜的判据）。
- **轨迹可视化**：不做运行期 Gizmo。原因见 `Labs/README.md`——编辑器 C# 读不到 Udon 的运行期状态，Scene view 的 `OnDrawGizmos` 拿不到球心。
  替代方案两条：① `traj` 日志行（默认，零成本）；② 可选的 `LabTrajectoryTrail`（`LineRenderer`，120 点，Lab 专用、默认关）只在要拍「起伏曲线」时打开，并且**在 Evidence 里注明打开了它**（因为它有每帧 `SetPosition` 的成本）。

### 6.6 Evidence 存放

```
Docs/Evidence/PhysicsTestField/
  README.md                         谁跑的、Unity/SDK/客户端、结论一句话
  environment.txt                   Unity 2022.3.22f1 / VRChat SDK 3.10.5 / PC 或 Quest / 是否 ClientSim
  console.log                       原文：[ptf] + [ProxyTrace] + [BallMotor] + [PickupFieldHost]
  summary.json                      {cells:[{id,D,checks:[{id,value,verdict}],verdict}], totals}
  cells/<cell>_D<0.300>.log         每 run 一个片段（从 console.log 切出来，便于 diff）
  shots/<cell>_<camera>.png         每 run 至少 2 张（cam_top + 该格主相机）
  traj/<cell>_D<0.300>.csv          可选：由 traj 行转出（t,x,y,z,sup,lift,cap,stuck）
```

命名：`<cell>_D<3 位小数>_<camera>.png`，例如 `C10_D0.300_cam_steps.png`。日期目录：`Docs/Evidence/PhysicsTestField/<date>_pass<N>/`（一次完整 pass 一个目录，不覆盖上一次）。

### 6.7 手动沙盒：自己滚球（不是脚本 run）

驱动器 `Start` 把所有格和外框挂起（它们的宿主不得自注册），第一个电机就绪的 `Update` 里 `EnterSandbox()`：外框 `Register(true, 0, 0)`，再逐格 `ApplySandboxRegistration`，重发 kind 白名单，`TryResetForNewRound("ptf-sandbox-<n>", spawn, heading)` + `TeleportTo` 把球放到出生点（y = 半径 + 0.02），打一行：

```
[ptf] sandbox cells=13 boxes=14 ramps=7 obbs=21/256 obbOverflow=0 framePlanes=4 reset=ok spawn=(0.00,-39.50) hdg=0.0
```

（`boxes + ramps = 21`：7 个坡 + 14 个盒。）之后没有任何每帧工作：不再按球的位置切格，也没有第二种注册。脚本 run（`BeginAll` / `BeginCell` / `BeginCellAtDiameter`）不改注册，只把球传到那一格的起点再开跑，跑完球停在原地。

出生点、外框链接和每格文字牌由判据导入器从判据文件的 `sandbox` 块和各格的 `sandbox_label` 写进场景（§9.3）。

---

## 7. 测试场驱动器接口草案（`PhysicsTestFieldDriver`）

### 7.1 定位

驱动器沿用 [StoryBallDiagnostics](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryBallDiagnostics.cs) 那条「脚本化驾驶 + 每秒一行日志」的缝：用 `motor.SetMoveAxes(forward, turn, lateral)` 跑脚本，每秒打一行；但把「一段硬编码时间线」做成「按格 + 按球径 + 按判据表」的数据驱动。**不引入任何新的输入层、不引入第二份物理**。

### 7.2 结构（已实现）

```csharp
namespace VRCKatamari.Labs.PhysicsTestField
{
    /// 一场物理回归：切格 → 设球径 → 跑脚本 → 判 PASS/FAIL → 打日志。
    /// 只活在 Labs 下；不进正式场景，不被 Runtime 引用。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestFieldDriver : UdonSharpBehaviour
    {
        // ---- 链接 ----
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private PickupFieldHost pickupHost;
        [SerializeField] private LabBallSizeSetter ballSize;
        [SerializeField] private PhysicsTestCell[] cells;        // 按 cells[] 顺序，index 稳定
        [SerializeField] private float[] presetDiametersM;      // 0.30/0.60/1.10/2.20
        [SerializeField] private string[] kindWhitelist;        // 沙盒注册之后重发
        [SerializeField] private Transform chaseCamera;         // 可选；null = 不动相机
        [SerializeField] private Vector3 chaseOffset;

        // ---- 运行参数（都是 Lab 旋钮）----
        [SerializeField] private bool autoBeginAllOnStart = false;
        [SerializeField] private float settleSeconds = 0.5f;
        [SerializeField] private float sizeTimeoutSeconds = 3f;
        [SerializeField] private bool logTrajectory = true;
        [SerializeField] private float trajectoryIntervalSeconds = 0.5f;

        // ---- 操作者 API（也被 HUD 按钮 / 外部探针调用）----
        public void BeginAll();                                  // 全部格 × 4 径
        public void BeginCell(string cellId);                    // "C10"，四径
        public void BeginCellAtDiameter(string cellId, float d);
        public void BeginFromIndex(int cellIndex, int diameterIndex);
        public void Abort();
        public bool IsRunning();
        public string GetCurrentCellId();
        public float GetCurrentDiameterM();
        public int GetRunIndex();
        public int GetRunCount();
        public int GetExpectedCount();                           // 上一 run 命中预期的判据数
        public int GetUnexpectedCount();
        public int GetSkippedCount();
        public string GetLastSummaryLine();
        public int GetCellCount();
        public string GetCellIdAt(int index);
    }

    /// 一格：本格 arena + 起点 + 激活矩形 + 判据行 + 驱动时间线。
    /// 所有字段由 Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs 烘焙，不手填。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PhysicsTestCell : UdonSharpBehaviour
    {
        [SerializeField] private string cellId;
        [SerializeField] private LevelArenaHost arena;           // 本格唯一的注册者
        [SerializeField] private Vector3 startCenter;            // y 被驱动忽略，见 §6.1
        [SerializeField] private float startHeadingDegrees;
        [SerializeField] private Vector2 footprintCenter;        // 激活矩形（含跑道）
        [SerializeField] private Vector2 footprintSize;
        [SerializeField] private float nominalM;                 // 本格的自变量：板厚 / 台阶高
        [SerializeField] private float approachM;                // 起点沿 heading 到第一个 staticBox 的距离
        [SerializeField] private float[] driveTimes / driveForward / driveTurn / driveLateral;
        [SerializeField] private float stopSeconds;
        [SerializeField] private string[] checkIds;
        [SerializeField] private string[] checkMetrics;
        [SerializeField] private int[]    checkOps;              // 0 abs<=, 1 <=, 2 >=, 3 ==, 4 abs>=
        [SerializeField] private float[]  checkLimits;
        [SerializeField] private int[]    checkReadouts;         // 0 frame, 1 trace(人读), 2 counter(P4)
        [SerializeField] private int[]    checkGateStages;       // 0 none,1 P1,2 P2,3 P3
        [SerializeField] private int[]    checkExpectMasks;      // 每球径 2 bit：0 n/a,1 pass,2 fail

        public string GetCellId();
        public LevelArenaHost GetArena();
        public Vector3 GetStartCenter();
        public float GetStartHeadingDegrees();
        public float GetNominalM();
        public float GetApproachM();
        public bool ContainsPoint(float x, float z, float marginM);
        public float DistanceToFootprint(float x, float z);
        // （没有 ApplyRegistration：沙盒是唯一的注册，见 ApplySandboxRegistration）
        public void ReleaseArena();                              // SetActive(false)
        public bool IsArenaActive();
        public int GetDriveCount();                              // + 每行 getter
        public float GetDriveEndSeconds();
        public int GetCheckCount();                              // + 每行 getter
        public int GetCheckExpect(int index, int diameterIndex);
    }
}
```

**和草案不一样的三处，都是实现逼出来的**：

1. **`checkReadouts[]` 是新增的数组**。草案只有 `readout` 这个字符串字段在 JSON 里；运行时每次判据都做一次字符串比较是没必要的，所以导入器把它烘成整数，驱动只按整数分支。
2. **注册开关与可见性分开。** `LevelArenaHost` 在自己 `Update` 里注册，而电机的表是全局的——十三个 arena 同时自注册会让 surface id 全从 1 开始、地面重复登记。所有格常显，所以 `ReleaseArena()` 不 `SetActive(false)`，而是 `arena.SuspendRegistration()`；`LevelArenaHost.Update` 在 suspended 时直接返回，**显式 `Register` / `Reapply()` 不受影响**。`Start` 把所有格和外框挂起，由驱动器一次 `EnterSandbox()`。`registrationSuspended` 默认 false，所以所有旧场景行为逐字节不变。
3. **没有 `fixedCameras[]` / `hudText` / `SelectNextCell()`**。不做相机切换与 HUD；证据靠日志和操作员截图（§9.5 的接线清单）。加它们是纯增量，不影响判据。

### 7.3 为什么判据是**并行数组**而不是「一个检查对象」

UdonSharp 没有用户结构体、没有 `List`/`Dictionary`、不能用反射。所以「一条判据」只能是并行数组的一行（`checkIds[i] / checkMetrics[i] / ...`）。**JSON 是作者数据**，编辑器侧导入器（`Editor/` 下，可读磁盘 JSON）把它烘焙成这些数组并写进场景；运行时**只读数组**，不读磁盘。这与 `02_TECHNICAL_CONTRACTS.md` §4「不要让 Udon 运行时依赖编辑器 API 或随意读取磁盘 JSON」一致。

### 7.4 状态机（已实现）

```
Idle → Switch(cell) → Size(D) → Settle → Drive → Report → (next D | next cell | Done)
```

| 状态 | 做什么 | 失败时 |
|---|---|---|
| `Switch` | 沙盒还没注册则先注册；记 `ramp_count`、有 `rampProbe` 的格调 `motor.AddRamp` 取证拒绝、记基线、`TryResetForNewRound`、`TeleportTo` 本格起点、`ResetField`、`ballSize.SetDiameterM(D)` **一次**（本 run **唯一**的球径请求；复位已经把球重定价回基线，所以每 run 必须重新发请求，但只发一次）、打 `run=` 行 | `TryResetForNewRound` 的 reason ≠ 0 → `BLOCKED_UNTIL_reset` 并跳过本 run（换下一径/下一格） |
| `Size` | `ballSize.SetDiameterM(D)`，每帧问 `IsConverged()`；**收敛后还要校验收敛是真的**：`abs(实测 D − 目标) ≤ SizeAppliedToleranceM`（常量 20 mm） | `HasFailed()` → `BLOCKED_UNTIL_size`（reason 来自设定器）；收敛但球不在目标径 → `reason=sizeNotApplied`；超 `sizeTimeoutSeconds` → `reason=timeout` |
| `Settle` | 等 `settleSeconds` | —— |
| `Drive` | 按 `drive[]` 的阶段推 axes，每帧采样，每 0.5 s 一条 `traj` | —— |
| `Report` | 逐条判据 → 行日志 → `summary` → 决定下一 run | —— |

**采样频率**：`Update()` 每帧一次（不是每步）。所以「每步」量（`step_dy_over_step_limit`、`active_surface_max`）必须来自 `[ProxyTrace]`（通道 B）或 P4 计数器——它们现在都报 `BLOCKED_UNTIL_manual` / `_P4`，**不会被逐帧采样冒充**。逐帧量只有 `frame_dy_m`（当前判据里没有用它的）。

**阶段之间的顺序不是自由组合**：见 §6.1 的三个「必须」。

### 7.5 HUD（不做）

```
PTF C10 D0.30 [4/84] checks 5/5  exp 0  skip 0   v=EXPECTED
```

上一行是**结论**，下面一行是**当步事实**：`gap=+0.0012 sup=0.100 lift=0.000 stuck=0.00 blk=0/6`。

只打 Console（`GetLastSummaryLine()` 已经给出同一行的文本，HUD 接它即可）。4 个 uGUI 按钮的绑定方式见 §8.4。

---

## 8. 球径设定接口草案（`LabBallSizeSetter`）

### 8.1 需求

同一格要在 0.30 / 0.60 / 1.10 / 2.20 四个球径下各跑一遍。现状：直径**只是账本的纯函数**（`SizeModelMath.TryDerive(confirmedVolume)`），没有 setter；`startCenterWorld` 与 `startVolumeMm3` 都是序列化字段（改了要重开场景）。

### 8.2 接口（已实现）

```csharp
namespace VRCKatamari.Labs.PhysicsTestField
{
    /// 只在 Labs 下：把一个运行中的球设到指定球径。
    /// 不改运行时代码：用 SubmitCandidate 喂体积 + GetSemanticDiameterWorldM 闭环收敛。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LabBallSizeSetter : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private float[] presetDiametersM;   // 0.30/0.60/1.10/2.20
        [SerializeField] private string[] presetLabels;      // "0.30"…（按钮按字符串绑，不解析数字）
        [SerializeField] private long  sizeModelStartVolumeMm3;  // 判据导入器从场景 motor 镜像（§8.3）
        [SerializeField] private string feederKind = "lab-feeder";
        [SerializeField] private Vector3 feederBounds = new Vector3(0.02f, 0.02f, 0.02f);
        [SerializeField] private float toleranceM = 0.002f;
        [SerializeField] private int maxAbsorbsPerRequest = 6;
        [SerializeField] private float feedIntervalSeconds = 0.05f;

        public void SelectPreset(int index);                 // HUD 按钮绑这个
        public void SetDiameterM(float targetM);             // 唯一的请求入口：每 run 调一次（Switch），每次都清状态并重发
        public void SetDiameterFromLabel(string label);      // "1.10"
        public void Cancel();
        public bool IsConverged();
        public bool IsRequestActive();
        public bool HasFailed();
        public string GetLastFailure();                      // "" / cannotShrink / noSizeModel / capExceeded / submitRefused
        public float GetTargetDiameterM();
        public int GetAbsorbCount();
        public float GetLastErrorM();                        // 有符号：实测 − 目标
        public float GetQuantumM();                          // 一个体积单位的直径当量（解析），未烘尺寸模型时 0
        public float GetCubedSlope();                        // d(D³)/dV（解析 = D³ / (baseline + confirmed)）
        public bool IsCalibrated();                          // sizeModelStartVolumeMm3 > 0
    }
}
```

**没有 `ResetToBaseline()`**（草案里有）：回 0.30 需要 `TryResetForNewRound(id, 格起点, heading, out reason)`，而「格起点」属于 `PhysicsTestCell`。让设定器去复位球，等于让它同时拥有「球多大」和「球在哪」两件无关的事。复位由驱动在切格那一帧做（§6.1 第 4 步），设定器只负责长大。

### 8.3 三条实现路线（推荐第一条）

| 路线 | 做法 | 成本 | 风险 | 判定 |
|---|---|---|---|---|
| **A. 闭环喂体积（采用并已实现）** | 每次 `SubmitCandidate(true, "lab-feeder", 0.02³, 球心, units, "ptf-feeder-<n>", gen)`；下一帧读 `GetSemanticDiameterWorldM()`，用**解析斜率**算下一次的整数 `units`（§8.3.2），通常一次喂足、≤ 6 次收敛到「网格最近点」 | **零运行时代码改动** | ① 吸收计数/事件被污染（挡路行与长杆格的 `absorbed_delta` 要先记基线，已按 §5.4 处理）；② 需要 `allowedKinds` 含 `lab-feeder`；③ 不能「变小」——只能 `TryResetForNewRound` 回 0.30 再长大 | **采用** |
| B. 加测试专用接缝 | `motor.SetConfirmedVolumeUnitsForTest(float)`（带 `testModeEnabled` 开关，默认 false） | 一处运行时代码 | 多一个「生产里不该存在」的写入口；U# 没有编译期隔离，只能靠开关纪律 | 备选，需 Lead 批 |
| C. 一个球径一个场景 | 改 `startVolumeMm3`，做 4 个场景 | 4 份场景 + 4 份维护 | 「同一格不同球径」变成跨场景比对，切格/机位/几何都可能漂 | 否决 |

**A 的实际闭环**（已实现，和草案的差别是「斜率」的来源）：

```
每帧（一次只允许一个候选在飞）：
  V = motor.GetConfirmedVolumeMm3()          // 只算「起始体积之外」的 mm³
  D = motor.GetSemanticDiameterWorldM()
  total = sizeModelStartVolumeMm3 + V        // 起始体积是唯一的私有输入，由判据导入器镜像
  slope = D³ / total                         // 解析 d(D³)/dV；不需要任何样本
  quantum = D / (3 · total)                  // 1 mm³ 折算的直径当量
  收敛: |D − target| ≤ max(toleranceM, quantum)
  否则: units = round((target³ − D³) / slope)   // 要喂的 mm³ 数；立方域里比，不需要开方
        若 units 会「越过目标更远」→ 这就是分辨率下限，收敛
        motor.SubmitCandidate(true, feederKind, feederBounds, center, units, "ptf-feeder-<n>", gen)
```

- **斜率是解析的**：`D³` 对**总体积**是线性的（`D = (6V/π)^(1/3)`，V 以 mm³ 计），所以 `d(D³)/dV = D³/total`，一次 feed 就能精确落在目标直径上。这里不再有「整体积单位」的量子：旧文档里的 29 mm / 7.25 mm / 2.16 mm / 0.54 mm 是 **0.0041 m³ 整数单位**的直径当量，随那个单位一起退役。
- **那一个数字怎么来**：电机的 `startVolumeMm3` 是 `[SerializeField] private`、**没有 getter**，所以判据导入器用 `SerializedObject` 读**场景里那台 motor** 的 `startDiameterWorldM`/`startVolumeMm3`，写进设定器的 `sizeModelStartVolumeMm3`（与 `approach_m` / `rampFootM` 同一套「编辑器读场景 → 烘进组件」的做法）。改电机起始尺寸后**重新导入判据**；没导（字段为 0）时 `IsCalibrated()` 为 false，设定器对超过当前尺寸的请求报 `noSizeModel`，该 run 在 Size 阶段被标 `BLOCKED_UNTIL_size`，**不会**用猜的数字换一个假 PASS。
- **每一个 run 发一次请求，只有这一个地方发**：复位 `TryResetForNewRound` 会把球重定价回 0.30，所以每 run 都必须重新发；但**只发一次**——`Switch` 里复位之后 `ballSize.SetDiameterM(D)`，`TickSize` 只读 `IsConverged()`/`HasFailed()`。两条反例：① 「同目标就短路」会保留上一个 run 的 `converged`，同档只有第一格真的长到目标径；② 「收敛后再调 = 新请求」配合每帧调用的 `TickSize`，`converged` 每帧被清一次、驱动永远读不到 `true` → `reason=timeout`。所以 `SetDiameterM` 是唯一请求入口（每次都清状态并重发），轮询是**只读**的（状态机与四条路径见 §8.3.4）；量子按需实时计算（`ReadSizeModel`），没有缓存字段。
- **收敛不等于到位**：`Size` 状态在 `IsConverged()` 之后还要量一次 `abs(实测 D − 目标) ≤ 20 mm`，否则报 `reason=sizeNotApplied` 并把该 run 标成 `BLOCKED_UNTIL_size`。没有这道门，一个「说收敛了但球还在 0.30」的旧程序会安静地跑完一整张矩阵，把假读数当成物理结果。
- **为什么 feed 必须是整数**：`PickupAbsorbMath.TryAbsorbValueToLedgerGain` 拒绝小数（账本增益是 int），喂 0.35 单位会被判 `RejectNoGain`、候选一直留在队列里。整数单位同时也是账本记的那个数。
- **为什么一次只允许一个候选在飞**：候选队列只有 4 格，第 5 次提交会 `SignalOverflow("pending candidate capacity 4 exceeded")` **永久 latch** overflow 计数——而本场好几条判据断言它 Δ=0。所以 feed 前先看 `GetPendingCandidateCount() == 0`，宁可慢也不要污染。
- **分辨率下限（解析，不是猜）**：`startVolumeMm3 = 14,094,798`（`D₀ = 0.2997`）。一个 mm³ 的直径当量 `= D/(3·total)` ≈ **7.1 nm**（0.30 m 球），所以分辨率**不是**限制：设定器可以落在任意预设直径上。判据因此是 `abs(diameter_error_m) ≤ 1 mm`（`size_accuracy`），收敛容差同样是 1 mm；1 mm 与 float32 的直径预算（~2e-8 相对，30 m 球上 ~1 µm）相比余量极大。
- **副作用清单**（必须写进 Evidence 的 README）：吸收计数 +N、`GetAbsorbedCount()` 变大、账本体积变大、`GetAbsorbVolumeLimitWorldM3()` 变大 → **挡路集与支撑集的分界会跟着变**。这正是我们要测的东西之一（C18 的「长大可吸」），但会污染 C14 的 `not_blocking` 判据——所以那条判据读的是「基线差」，不是绝对值。

### 8.3.1 结论对判据的影响（要写进 Evidence）

- `size_accuracy` 判据 = `abs(diameter_error_m) ≤ 1 mm`。它证明的是「设定器把球放到了预设直径」，四个档都要求 pass；不再有「整单位量子」这回事（那个量随 0.0041 m³ 单位退役）。
- **预设直径会被量化**：0.30 预设落成 D = 0.2997369 m（整 mm 为 299），所以凡是写死「0.30 m 球」的数都要用实际直径；判据一律按运行时读到的直径算，不拿预设标签当球径。
- ±1 mm 现在就是判据本身（`abs(diameter_error_m) ≤ 0.001`），不需要再给电机加测试接缝。

### 8.3.2 读数的失败语义：名字错 ≠ 现在读不到

`TryReadMetric` 返回**状态码**而不是 bool，ALERT 再把状态码映射成原因：

| reason | 含义 | 谁的问题 |
|---|---|---|
| `unknownMetric` | 驱动里**没有这个分支**（`metric` 名字错/拼错） | 判据文件（`physics_test_field.json`） |
| `noDeckSample` / `noLiftSample` | 这一 run 还没采到样本（run 没进 drive 就结束了） | 运行/时间线 |
| （已退役）`noSetter` / `sizeModelMissing` / `quantumUnavailable` | 只属于旧的「整单位量子」判据；mm³ 判据不读设定器也不读烘进去的模型 | 驱动没连设定器时，Size 阶段直接报 `BLOCKED_UNTIL_setter`；模型没烘时 `noSizeModel` 报在设定器里 |

ALERT 行因此变成：
`[ptf] ALERT cell=<id> D=<p> check=<id> metric=<name> reason=<上表> value=n/a verdict=SKIPPED`。
`SKIPPED` 仍然不计失败（§5.3），但**原因写得出来**；`raw=PASS` 的那一行只在真的量到时出现。实现用 **int 状态码**（`MetricOk` / `MetricUnknown` / `MetricNoDeckSample` / …）+ `MetricReason(status)` 映射成上表的字符串，**不用 `out string`**：与电机 `out int reason` 的既有写法一致，也不给 U# 引入一个本仓库还没用过的参数形态。

**另外两种「不是判据」的失败**（同样有名字，不混进 `reason=`）：
- `[ptf] blocked ... stage=size reason=sizeNotApplied diameter=... target=...`：设定器说收敛、球却不在目标径（§7.4 的到位校验）。
- `[ptf] rampCellMismatch cell=... ramps=N rampClimb=0/1`：坡道格的 `rampClimb` 与 motor 里的坡数不一致 = **判据没重导**；前者会让台面被当成台阶、整组坡道判据变形。

### 8.3.3 程序指纹：先证明你跑的是这份代码

跑旧的 U# 程序而数据是新导入器写的，整张矩阵会无效。下面四种日志症状**只可能来自旧驱动**：

| 日志里的证据 | 只可能来自 |
|---|---|
| `[ptf] ALERT ... error=unknownMetric` | e8ef316 之前的驱动（现在打印 `reason=`） |
| `BLOCKED_UNTIL_P3` | 5835517 之前的驱动（该 skip 已删） |
| `landing_gap_m` / `fall_frames` / `max_bottom_rise_m` 报 `unknownMetric` | 5835517 之前的驱动（当时还没有这些 metric） |
| `value=0.0000`（无 `+`） | 旧的格式化器（现在 `Signed()` 输出 `+0.0000`） |

而数据是新的（`sizeModelStartVolumeMm3` 已烘进去）时，这套组合就会出现。为让这件事一眼可见，两个组件在 `Start` 各打一行指纹：

```
[ptf] build=2026-11-15d cells=9 presets=4
[ptf] setter build=2026-11-15d startVolumeMm3=14094798
```

**看到第一行之后才可信**：没有 `build=` 行 = U# 程序没重编；`startVolumeMm3=0` = 判据没重导。两条都不满足时不要读矩阵。

### 8.3.4 两个组件的状态机：谁发请求、谁清状态、谁只读

**一句话规则：`SetDiameterM` 每 run 只被调用一次（`Switch` 里、`TryResetForNewRound` 之后）；`Size` 状态只读。**

`LabBallSizeSetter` 的字段写者（每帧都会跑的是 `Update()` → `Feed()`）：

| 成员 | 谁写 | 何时 |
|---|---|---|
| `targetM` | `SetDiameterM` | 每请求一次 |
| `requestActive` | `SetDiameterM`（true）/ `Cancel`（false）/ `Fail`（false） | 请求开始 / 取消 / 失败 |
| `converged` | `SetDiameterM`（false）/ `Feed`（true） | 请求开始 / 到位或到分辨率下限 |
| `absorbsThisRequest` | `SetDiameterM`（0）/ `Feed`（+1） | |
| `absorbCount` | `SetDiameterM`（0）/ `Feed`（+1） | |
| `nextFeedTime` | `SetDiameterM`（0）/ `Feed`（now + interval） | |
| `lastFailure` | `SetDiameterM`（""）/ `Feed`（收敛时置 ""）/ `Fail`（原因） | |
| `lastErrorM` | `SetDiameterM`（0）/ `Feed`（每帧） | |
| `generationSequence` | `Feed`（+1，**不重置**：itemId 必须全局唯一） | |

`PhysicsTestFieldDriver` 在这条链上的动作：

| 动作 | 状态 | 性质 |
|---|---|---|
| `ballSize.SetDiameterM(D)` | `Switch`（复位之后） | **全驱动唯一一处**；清状态 + 重发 |
| `ballSize.IsConverged()` / `HasFailed()` / `GetQuantumM()` / `GetLastErrorM()` | `Size` / `Report` | **只读** |

**四条路径的逐步转移**（`D₀ = 0.2997369`，`q` = 当前量子 = `D/(3·(baseline+confirmed))`）：

| 路径 | 步骤 | 结果 |
|---|---|---|
| 0.30（球已在目标） | 复位 → `D = 0.2997`；`SetDiameterM(0.30)` → `converged=false`；`Feed`：`q=0.02905`，误差 `0.00026 ≤ max(0.002, q)` → `converged=true`（**0 次喂料**）；`TickSize` 观察到 → 到位校验 `0.00026 ≤ 0.02` → `Settle` | 收敛，absorb = 0 |
| 0.60（要喂料） | `SetDiameterM(0.60)`；`Feed`：`slope = D₀³/3.439 = 0.00783`，`q = 0.02905`，误差 0.30 > q → `units = round(24.15) = 24` → 提交；候选在飞时 `Feed` 直接返回；提交后 `D = 0.598942`、`q = 0.007276`，误差 0.00106 ≤ q → `converged`（**1 次喂料**） | 收敛，absorb = 1 |
| 同档连续格（C14→C09 @0.60） | 新 run：复位 → `D` 回到 0.2997；`SetDiameterM(0.60)` **再次清状态并重发**（不短路）→ 与上一行相同 | 每格都真的长到 0.60 |
| 换档（0.60→1.10→2.20） | 每次 `SetDiameterM(新目标)`；`units = 167 / 1356`，1 次喂料到 `1.100994 / 2.199792`，误差 0.00099 / 0.00021 ≤ q | 收敛，absorb = 1 |

**为什么旧死锁不会再发生**：`converged` 的唯一清除者是 `SetDiameterM`，它每 run 只在 `Switch` 调用一次；此后 `TickSize` 不写任何 setter 字段，所以 `Feed` 置的 `true` 会一直保留到该 run 结束。Python 侧把它写成了一个最小状态机模拟（`test_the_driver_never_reissues_the_size_request`）：旧设计（每帧 `SetDiameterM` + 「收敛后重发」）在模型里复现 3 秒超时，新设计在 0.30/0.60/1.10/2.20、同档连续 9 格与两种 Update 顺序下都收敛。

### 8.4 HUD 控件

4 个 uGUI 按钮（0.30 / 0.60 / 1.10 / 2.20）+ 1 个「回 0.30」。绑定方式按 [Labs/README.md](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/README.md) 的硬事实：`Button.onClick` 的 persistent listener 指向 `UdonBehaviour.SendCustomEvent`，参数字符串是方法名，用 `UnityEventTools.AddStringPersistentListener`（lambda 会被拒）。

---

## 9. 场景与数据

### 9.1 场景

| 项 | 值 |
|---|---|
| 场景 | `Labs/PhysicsTestField/PhysicsTestField.unity` |
| 目录 | `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/PhysicsTestField/` |
| 新建时的必做序列 | [Labs/README.md](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/README.md)「用编辑器脚本搭 UdonSharp 场景」的 7 步（program asset 先就位 → `CompileAllCsPrograms` → backing `UdonBehaviour` → `_udonSharpBackingUdonBehaviour` → `SetBehaviourVersion` → 填引用 → `CopyProxyToUdon` → 存盘重开场景再回写）。**不是** `AddComponent<T>()` 一句话能搞定的 |
| 必备 SDK 组件 | 根对象 `VRC.Core.PipelineManager`；`VRCSceneDescriptor.ReferenceCamera` → 主相机 |
| 材质 | 复用 `Art/Materials/Mat_Playground*`或纯色；**不许**为了好看引入需要烘焙的材质（测试场不用阴影判断任何东西） |
| 光照 | 一盏方向光，固定角度；截图不依赖实时阴影的精确定位 |
| 可玩性 | 可玩（Station + `DesktopMoveInput`），但**默认以驱动器为准**；人玩只用于看手感 |

### 9.2 数据：几何在 placement，判据在 JSON

`physics_test_field.json` 不带几何格式；几何只在 `LEVEL_AUTHORING` 定义的 `placement.json` 里（见 [LEVEL_AUTHORING](../Tools/LEVEL_AUTHORING.md)）。两份数据各管一件事，**没有第三份**：

| 文件 | 管什么 | 判据/门 |
|---|---|---|
| `Docs/Design/Levels/physics_test_field/<cell>/placement.json` | **场景里有什么**：`structures[]`（ground / boundary / box / ramp）、`level.arena.id`、`level.budget`、`level.expected` | `python3 Tools/levelbuild/levelbuild.py check <该文件>`（容量、地面、通配、规范性） |
| `Docs/Design/BallMotor/physics_test_field.json`（v2） | **跑什么**：每格的 `start_pose` / `footprint` / `nominal_m` / `drive[]` / `checks[]` / 机位引用 / 期望；场级的 `diameters[]`、`motor_constants`、`cameras[]`、`registration_model`、`driver`、`ball_size` | `Docs/tests/levelbuild/test_physics_test_field_cells.py`（跨文件一致性） |

**为什么是「每格一份 placement」，不是「一份 placement 按 zone 分格」**：

1. **容量门就是按文件算的**。`levelbuild check` 的硬规则是 `planes ≤ 8 / boxes ≤ 8 / ramps ≤ 4`（`level.budget`）。一份文件装整场的格 = 二十多个盒，**天生红**；要让一份文件通过，就必须把预算改成「按 zone 数」，那等于把这条硬约束在第二个地方重新实现一遍——所以一格一份文档。
2. **导入器是「一份文档 = 一个 arena host」**。每格一个 host 是需求（沙盒逐格追加登记，各自有 `approach_m` / 台面足印 / 坡脚的烘焙）。一份文件 = 一份几何 = 一个 host，导入器不需要「按 zone 分组」这一层逻辑（分组一旦要按 `zone` 字段做，又得规定 zone 与 host 的命名规则、props 的归属……每一条都是新格式）。
3. **schema 早就留好了位置**。`LEVEL_AUTHORING` §15.4 的原话就是「本 schema 是『一格一份 placement』（`level.id` + 目录），没有假设一场景一份」；`physics_test_field` 直接用这个位置。

**为此对工具做的最小改动**（`Tools/levelbuild/` + `Editor/LevelBuild/`，都不是新格式）：

| 改动 | 位置 | 为什么必须 |
|---|---|---|
| `level.arena = {id, notes}` | `placement_core.LEVEL_KEYS` + `_check_arena` | 一份文档声明「我是一个 arena 格」。导入器用它决定受管子树与 arena host；`_check_arena` 要求 `id` 等于所在目录名（复制来的文件不会带着旧身份跑） |
| `level.id` 允许一层 `/` | `_LEVEL_ID_RE` | `level.id` 是「Levels 下的目录」，现在多了一层（`physics_test_field/C14`）；`placement_path("physics_test_field/C14")` 能直接解析到文件 |
| 盒子/斜面必须落在 `level.bounds` 内（仅 arena 文档） | `_check_arena` | 一份**整图**的 bounds 只是报表范围（历史行为不变：树的角树可以越过场地边界），但**一格**的 bounds 是它的场域，几何跑出去就是数据错 |
| 导入器：cell 模式（`LevelContent/<arenaId>/` + 该对象上的 host） | `Editor/LevelBuild/PlacementImporter.cs` | 一场景多格：每格一个受管子树、一个 arena host，互不覆盖 |
| 菜单一次导入该场景的全部 cell | 同上（`CollectLevels` + `ImportActiveSceneLevel`） | 一场景 21 份文档，逐个手点 21 次不现实；混入「整关文档」时仍然拒绝（否则两个 arena 抢同一张电机表） |
| 渲染文件名净化 `level.id` 里的 `/` | `Tools/levelbuild/blender/render_views.py` | 否则 `--preset plan` 会往 `.../physics_test_field/C14_plan_workbench.png` 写（父目录不存在） |

**`physics_test_field.json` v2 的键**（schema_version 2）：

| 键 | 内容 |
|---|---|
| `id` / `title_zh` / `status` / `created` / `updated` / `design_doc` / `purpose` / `generated_by` | 身份与出处 |
| `geometry` | 迁移模型、目录、**哪些字段来自 placement**、`check` / `preview` 命令、以及「footprint 为什么不算几何」 |
| `scene` | 场景路径、状态 `NOT_CREATED`、坐标系、朝向约定（**边界平面不在这里了**：它们是每格 placement 的 4 条 `role=boundary`） |
| `motor_constants` | 全部常量 + 容量表（引用代码，不重定义） |
| `diameters[]` | 4 档：`diameter_m / radius_m / step_limit_m / protrusion_cap_m / absorb_volume_limit_m3` |
| `start` | 全场默认起点与起始球径（每格自己的起点在 `cells[].start_pose`） |
| `registration_model` | `mode = manual_sandbox`、唯一的注册、**`per_cell_arena`**、成本说明 |
| `cells[]` | **已接线**的格（九个：C14/C09/C08/C10/C01/C03/C05/C06/C13），见下 |
| `planned_cells[]` | 未迁移的格（含 C21–C24 地形格与 10 条仅设计行）：只有 `id/slug/name_zh/kind/stage/expectation/note`，**没有任何几何**，`wired=false` |
| `cameras[]` | 8 个机位（数据） |
| `override_rows` | 覆盖块的键、驱动规则与每档期望计数 |
| `driver` / `ball_size` / `criteria_importer` | 三个组件的路径、状态、运行参数、`kind_whitelist`、`blocked_notes` |
| `evidence` | 根目录、文件布局、截图规则 |

单个 `cell` 的键（v2）：

| 键 | 含义 |
|---|---|
| `id` / `slug` / `name_zh` / `kind` / `stage` | 身份与阶段（`now` / `P1` / `P2` / `P3`） |
| `wired` | true = 已接进场景；false 的格不进 `cells[]` |
| `placement` | `physics_test_field/C14/placement.json`（相对 `Docs/Design/Levels/`）；**几何的唯一入口** |
| `start_pose` | `{center:[x,y,z], heading_deg}`：**runner 数据**（驱动把球传送到这里）；`y` 是作者标注，运行时被 `碰撞半径+0.02` 覆盖 |
| `footprint` | `{center:[x,z], size:[sx,sz]}`：**runner 的激活矩形**，含跑道（C14 是 6×12 给 6×6 的板）。它**不是**几何的包围盒，placement 里也没有这个矩形；两者的关系由测试守（几何必须落在自己的 footprint 内） |
| `nominal_m` | 本格的自变量：板厚 0.028 / 台阶 0.075、0.050、0.100。每条 deck/step 判据都相对它写，所以一条 limit 对四个球径都成立 |
| `stop_seconds` | 驱动时间线结束后继续测量的秒数（球已松手） |
| `drive[]` | `{t, forward, turn, lateral, note}`：脚本化驾驶的相位表 |
| `checks[]` | `{id, metric, op, limit, unit, readout, gate, window, expect[4], note}`。**`expect` 是按 `diameters[]` 顺序的 4 元数组**（JsonUtility 读不了「以球径为键的对象」，而且数组顺序就是 `checkExpectMasks` 的位序） |
| `cameras[]` / `acceptance_refs[]` | 机位、验收编号 |
| `expectation` | `{pre_p1, p1, p2, p3}` 的人读结论（`PASS` / `EXPECTED_FAIL` / `BLOCKED_UNTIL_*`） |

### 9.3 两个编辑器工具

| 工具 | 位置 | 做什么 | 不做什么 |
|---|---|---|---|
| `PlacementImporter`（含 cell 模式） | `Editor/LevelBuild/PlacementImporter.cs` | 把 `Levels/<field>/<cell>/placement.json` 变成 `LevelContent/<cell>/{Props,Structures}`，并填该格 arena host 的 `groundNormal/groundOffset`、`boundaryWalls`、`staticBoxes`、`allowedKinds` | 不建 `LevelArenaHost` 组件（U# 组件要 program asset + backing UdonBehaviour + `CopyProxyToUdon` 的 7 步，脚本硬建只会得到一个「看着接好、其实不跑」的组件）；不 `SaveScene` |
| `PhysicsTestFieldCriteriaImporter` | `Editor/PhysicsTestField/PhysicsTestFieldCriteriaImporter.cs`，菜单 `VRC Katamari/Level/Import Physics Test Field Criteria` | 读 `physics_test_field.json`，把 `start_pose / footprint / nominal_m / drive[] / checks[]` 烘进 `PhysicsTestCell` 的并行数组，把 `cells[] / presetDiametersM / kindWhitelist` 填进驱动；`approach_m` 由它**读 arena 的 `staticBoxes`** 算（起点沿 heading 第一个进入的盒），不是抄 JSON；`sandbox` 块写进驱动（`sandboxFrame` ← `LevelContent/<arena_id>` 上的 `LevelArenaHost`、出生点与朝向）；每格 `sandbox_label` 建成 `Cells/<id>/SandboxLabel`（`TextMesh`，位置 = 格的 pad x + 起点 z，高 1.4 m） | 不建 U# 组件、不建场景、不 `SaveScene`（`TextMesh` 是原生组件，文字牌是它唯一建的对象）；找不到组件时打印「该给哪个对象加什么」并跳过 |

> 这一层是**工具**，不是运行时。作用是「改 JSON/placement 一个数字 → 重跑两个导入 → 场景与文档与数据三者一致」，而 Udon 运行时永远只读序列化数组（`02_TECHNICAL_CONTRACTS.md` §4）。

### 9.4 与地图工具的关系

本场的几何单元（静态盒 + 斜面 + 平面）与 `Docs/Design/Maps/*/layout.json` 的 `structures[]`/`collision` 是**同一组概念**（`Tools/mapbuild/validate_map.py` 已经在数 8 盒 / 8 平面）。**没有**把测试场塞进 `layout.json`：测试场不是地图、不参与 `map_snapshot` 哈希、不跑 `validate_map.py`。但**几何格式现在是同一个** `katamari.placement/0`，所以 `levelbuild.py check/format/seat` 与 Blender 出图对测试场直接可用。

### 9.5 给 Unity 操作员的装配清单（2026-11-17b：手动沙盒）

> 每一步都是「操作员在 Windows 的 Unity 里做」，产物是场景与 U# 组件实例；场景 `.unity` 不手改。`PhysicsTestField.unity` 已有九格 + C25–C28 的装配（按上一版清单）；本批新增沙盒外框 `SANDBOX`，驱动器新增 `sandboxFrame` / `sandboxSpawn`，删除 `walkAroundActivation`；13 格的 placement 删掉了各自的台垫和围墙（**重导会自动把场景里旧的台垫和围墙删掉**，见步骤 2）。

#### 9.5.1 装配步骤

1. **拉到最新并等 U# 重编**。驱动与格的指纹是 `2026-11-17b`；Play 后第一行 `[ptf] build=2026-11-17b … cellRev=2026-11-17b` 才可信。
2. **导入沙盒外框几何**：菜单 `VRC Katamari/Level/Import Placement (LevelContent)`（场景是 PhysicsTestField；菜单会认出 14 份 cell 文档：13 格 + `SANDBOX`，全部导入）。认成功：新增一份 `[level] cell=SANDBOX …` 报告，arena 那行说 "not wired"（`LevelContent/SANDBOX` 上还没有 `LevelArenaHost`，第一次属正常）。**大地面的可见网格由这一步自动生成**：`LevelContent/SANDBOX/ground/Visual` 是一块撑到外围平面的 32 × 0.04 × 61 m 立方体（顶面 y=0）；4 张外围平面是 `wall_x_min / wall_x_max / wall_z_min / wall_z_max` 标记，按项目约定其 `Visual` 渲染器关着（边界不画，只拦球），手工不用建任何网格。
   **这一步同时会把旧的台垫和围墙从场景里删掉**：13 格的 placement 已经没有 `ground` 和 `wall_*` 四面墙，`PlacementImporter` 对 `LevelContent/<cell>/Structures` 下「文档里已经没有」的子物体做 `Undo.DestroyObjectImmediate`（`PlacementImporter.cs` 的 stale 清扫，计入 `StructuresDeleted`；其 `Visual` 子物体随父物体一起删）。**预期 13 格合计 `StructuresDeleted=65`（13 × 5：一个 `ground` + 四面 `wall_*`）**；各格 `LevelArenaHost` 的 `boundaryWalls` 数组被改写成空，`groundNormal/groundOffset` 保持默认。操作员不用手删，只需看报告里这个数；第二次导入应回到 `+0 ~0 -0`。沙盒的每格 placement 也不再需要 `ground`，是因为导入器和 `levelbuild check` 都认 `level.arena.frame = "shared"`（见 LEVEL_AUTHORING §4.3 的 `arena` 行）。
3. **给 `LevelContent/SANDBOX` 加 `LevelArenaHost`**（U# 组件，按 [Labs/README.md](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/README.md) 的 7 步建；`motor` 手填 `MotorHost` 上的电机；`autoBeginPlaying` 保持默认），再跑一次第 2 步，报告应变成：SANDBOX `boxes=0, walls=4, ramps=0`。
4. **电机参数**（选 `MotorHost` 的 `KatamariBallMotor`，沿用上一版）：`rampTangentialGravityScale = 1`；`accelerationMps2` 7.5、`brakingMps2` 6、`frictionPerSecond` 0.9。
5. **驱动参数**（选 `Driver`）：没有模式开关要设（`walkAroundActivation` / `walkAroundDebounceSeconds` 已删，场景保存后消失）。`autoBeginAllOnStart` 另行决定。
6. **重导判据**：菜单 `VRC Katamari/Level/Import Physics Test Field Criteria`。认成功：`[ptf] criteria: cells=13 checks=90 v2Rows=1 missingCells=0 missingArenas=0 driverFields=… labels=13 errors=0`，driverFields 比上一版多 3（`sandboxFrame` / `sandboxSpawn` / `sandboxSpawnHeadingDegrees`），`labels=13` 说明 13 块文字牌建好了（`Cells/<id>/SandboxLabel`，`TextMesh`，1.4 m 高，在每格起跑标记处）。若报 "no LevelArenaHost on LevelContent/SANDBOX"，回到第 3 步。
7. **存场景**。保存后的 diff 应只有：`LevelContent/SANDBOX` 子树、它的 `LevelArenaHost`、13 个 `SandboxLabel`、驱动器的 3 个新字段（`sandboxFrame` / `sandboxSpawn` / `sandboxSpawnHeadingDegrees`），被删掉的 `walkAroundActivation` / `walkAroundDebounceSeconds`，以及 13 格 `Structures` 下被删掉的 65 个旧结构（`ground` 和 `wall_*`）。
8. **进 Play 看**：不用动手，Console 应出现：
   `[ptf] build=2026-11-17b mode=sandbox cells=13 presets=4 cellRev=2026-11-17b v2Rows=1`
   `[arena] planes=4/4 boxes=0/0 ramps=0/0 supports=1 refused=0 kinds=0 obbs=0/0 …`（SANDBOX 外框）
   `[arena] planes=0/0 boxes=… ramps=… …` × 13（每格一行，没有 `planes`，`obbRefused=0 obbOverflow=0`）
   `[ptf] sandbox cells=13 boxes=14 ramps=7 obbs=21/256 obbOverflow=0 framePlanes=4 reset=ok spawn=(0.00,-39.50) hdg=0.0`
   然后球在 (0, 0.17, -39.5) 静止；推到任一块板上试（§0.2）。`obbs=` 不是 21 或 `obbOverflow` 非 0，就是有格没登记上，先看那一行 `[arena]` 的 `refusal=`。

#### 9.5.2 打开 `climbDiagnosticsEnabled`

电机的这个字段默认关。在 Inspector 里勾选 `MotorHost` 上 `KatamariBallMotor` 的 `Climb Diagnostics Enabled`（U# 代理会在进 Play 时拷到运行体）；用 MCP 的话，改代理上的 `climbDiagnosticsEnabled` 后调 `UdonSharpEditorUtility.CopyProxyToUdon(motor)`。打开后每个模拟秒一行：

```
[BallMotor] climb t=… climbActive=True capLock=False id=… height=0.2 climbV=… planarV=… actualSpeed=… resid=…
```

复现 C25 时看 `climbActive=True / capLock=False / actualSpeed≈0`（爬升通道在抬球、顶棚把它压回去，没有进展）与 cap 之后的掉落守卫的区别。**读完关回**，不要提交成打开。

#### 9.5.3 跑与预期读数

驱动 `Start` 后第一行应有 `[ptf] build=2026-11-17b cells=13 presets=4 cellRev=2026-11-17b v2Rows=1`。脚本 run 不需要先做任何切换（沙盒已注册，run 只传球）。用 MCP / UnityEvent 调 `driver.BeginCellAtDiameter("C25", 0.30f)` 等；`BeginAll()` 跑全部格 × 4 档（C25–C28 在后三档各记一行 `SKIPPED_NOT_RUN`）。**判据是在每格带 pad 围墙的孤岛里写的，沙盒里没有围墙：脚本 run 的后段球会越过原来的 pad 北缘，往下一排的板滚。** 红的判据先看是不是球越界了，再决定缩短时间线。D=0.30 的 summary 预期（**设计预期，不是实测**）：

| 格 | checks | pass | unexpected | skipped | diag | 说明 |
|---|---|---|---|---|---|---|
| C25 | 12 | 2 | 0 | 0 | 10 | `size_accuracy` / `no_overflow` 判；其余十条只记录，**值要贴进证据**，由人读 |
| C26 | 6 | 2 | 0 | 0 | 4 | 2° 缓坡：`slide_delay` 等只记录 |
| C27 | 7 | 7 | 0 | 0 | 0 | `flat_drift_m` ≈ 0；`slide_delay_s` 约 0.30–0.45；`slide_peak_speed_mps` 约 0.3；`push_speed_ratio` ≥ 0.9 |
| C28 | 7 | 7 | 0 | 0 | 0 | `slide_delay_s` 约 0.30–0.40；`slide_peak_speed_mps` 约 0.6 |
| C03 | 7 | 6 | 0 | 0 | 1 | `slip_back` 现为只记录（25° 坡静止后会顺坡滑下） |

C27 / C28 红了先看三件事：`rampTangentialGravityScale` 是不是 1（0 就是 `slide_delay_s=99`）；电机的惯性参数是不是现行默认；日志里 `[ptf] run=… arena=` 的 `obbs=` 是不是 ≥ 2（坡没烘进去）。

九个老格的 D=0.30…2.20 期望计数见 JSON `override_rows.expected_per_run`。

#### 9.5.4 出证据

`Docs/Evidence/PhysicsTestField/<date>_pass<N>/`：`console.log`（`[ptf]` 与 `[BallMotor] climb` 原文）、`summary.json`、`shots/<cell>_D<0.300>_<camera>.png`。**C25 的诊断读数与 `[BallMotor] climb` 行是这一格存在的目的，必须原文留存。**


### 9.6 实现状态

| 项 | 状态 |
|---|---|
| 几何（13 格 placement） | 已入库；`levelbuild check` 全部 PASS（C06 一条预期的 tilted-box warning，见 §4.1.2） |
| 判据 JSON v2 | 已入库；13 格 90 条判据 + planned 格 |
| Labs 三个组件 | 已入库；2026-11-17a 新增 record 行、run mask、爬升/坡滑读数；2026-11-17b 新增手动沙盒，删除 walk-around 与逐格隔离注册 |
| 沙盒外框 | `SANDBOX/placement.json` 已入库、`levelbuild check` PASS；场景里的 `LevelArenaHost` 待操作员装配（§9.5） |
| 判据导入器 | 已入库；`approach_m` / `rampFootM` / C06 probe 的几何读取只在编辑器跑 |
| 场景 | `Labs/PhysicsTestField/PhysicsTestField.unity`；沙盒外框与文字牌待操作员装配（§9.5） |
| 跨文件一致性 | `Docs/tests/levelbuild/test_physics_test_field_cells.py` |
| 未验证 | 主人 2026-11-17 已在地图 0 手动亲测并认可 C25、C26/C27、C08/C09/C10、C06 慢爬（§0.2）；这不等于 13 格 90 条脚本判据全量通过。C25 精确诊断读数、C26–C28 坡滑时间仍待脚本证据 |

---

## 12. 不确定处与需要拍板

| # | 问题 | 现状/倾向 |
|---|---|---|
| **U3** | **napkin 厚度 2.8 cm vs 1.4 cm** | 本场两个都用：C14 用静态 0.028，C18 #1 用真模型（0.014）。若 2.8 cm 是旧版模型，C14 就是一个「已不存在的尺寸」的测试，需要一次美术确认 |
| **U4** | **球径设定走闭环喂体积还是测试接缝** | 已定：闭环喂体积，已实现。副作用：吸收计数/账本被污染（判据读基线差）、只能长大（复位负责回 0.30） |
| **U5** | **挡路容量 6 的正向夹取在本场构造不出来** | 场内只做负向判据（`blocking_max ≤ 6` 且不 latch），正向夹取交给 NUnit |
| **U6** | **切格重新登记静态 OBB 表的 GC 是否可见** | 未测；一次完整 pass 的切格 / 重置正好是量它的最小场景 |
| **U9** | **`allowedKinds` 的两份名单** | 驱动在沙盒注册之后重发 `kindWhitelist`，arena 那份是它的子集 |
| **U10** | **C06 的「ramps=0/1」字面值与工具链冲突** | 板以 `role=box`+`kind=ramp_probe` 落库，导入器烘 probe 面，驱动调 `motor.AddRamp` 取证（§4.1.2） |
| **U12** | **C13 的 `seam_no_step` 用 `grounded_frame_dy`** | 0.6 m 自由落体单帧位移远大于接缝，所以只在非下落帧累计，阈值 0.05 |
| **U15** | **解析量子要的那一个电机数字没有 getter** | 判据导入器读场景 motor 的 `startVolumeMm3`，烘进设定器；改电机的定价字段后必须重新导入判据 |
| **U16** | **「跑的到底是哪份 U# 程序」是盲区** | 两个组件在 `Start` 打 build 指纹（§8.3.3）；`Size` 状态对「收敛但球没到目标径」报 `sizeNotApplied` |
| **U17** | **「每帧都调的方法」是这条链上最危险的面** | 请求只在 `Switch` 发一次；`TickSize` 只读；`Feed` 是唯一在每个 `Update` 里写 setter 状态的函数（§8.3.4） |
| **U18** | **缓坡格只跑 D=0.30** | 足迹只有 9 m，装不下按 s 缩放的速度；若要看大球的坡滑，要给这三格做加长足迹的版本 |
| **U19** | **C25 的 H_cap 关系** | 顶棚底 0.50 m 略大于 `1.5·D = 0.45`，但在「休息高度 + H_cap = 0.75」之内；球顶在 h=0.20 时顶到顶棚。主人 2026-11-17 确认受阻正常、靠后退脱出；不做按进度释放，保留诊断（TERRAIN_CLIMB §6.7） |
| **U20** | **C18 的 `ptf_support_crate` 已改尺寸** | 旧 1.2×0.5×1.0 在现行吸收门下 2.20 球可吸（Req 2001 mm），不再有「吸不动但矮」的件；提议改成 1.6×1.05×0.5（abc 0.84，Req 2239 mm > 2200），高度仍 0.5 < 爬升上限。C18 迁入时按这个尺寸 |

---

## 14. 一句话总结

**这张图是一台仪器，也是 owner 的手感测试场**：一块大平地、所有板同时是实心的；一份数据只写一次，几何只在 `<cell>/placement.json`（格里只有测试板），判据只认本格的 `nominal_m`；受阻不等于卡住（C25 由主人确认靠后退脱出），诊断读数不代替主人决定或全量验收。
