# MotionHost：道具运动与骨骼动画的运行时宿主

现行口径。状态：**代码已写、本机编译与纯函数测试通过，Unity 里还没跑过**（U# 编译、场景接线、每帧成本待操作员）。上游设计：[LEVEL_FORMAT_V2](../Levels/LEVEL_FORMAT_V2.md) §6.7、[PROP_BEHAVIOURS](PROP_BEHAVIOURS.md) §7、[OPEN_DECISIONS](../../OPEN_DECISIONS.md)（D-10 13 类全做、D-11 153 个普通 Animator、D-12 相位、D-14 坠落挂起、触发门与剔除半径两个概念）。

## 1. 盘点（参考关卡 `_ref_cancer`，`placement.v2.json`）

| 项 | 数量 |
|---|---|
| 带 `motion` 的道具 | 113（`behaviour:"motion"` 108 + `ambient` 5） |
| 跟着在动的父件走的道具（`parent`+`inherit`，静态父件上的不算） | 42（含 `behaviour:"static"` 的风铃、窗帘一类，它们挂在转动/摆动的父件上） |
| `motions[]` / `paths[]` | 14 / 4（`p00` 2 点、`p15` 59 点、`p22` 2 点、`p23` 50 点） |
| 宿主行数 = 113 + 42 | **155**（再加"只有 Animator"的行，见 §6） |
| 各类 | sporadic_walk 31、hop 18、sway 15、wait_for_player 15、whack_a_mole 8、path_collision 7、spin 7、clouds 5、sloping_path 2、bob 2、flip 2、back_and_forth 1 |
| 触发门 | 本数据**没有** `triggerRadiusM`；`wait_for_player` 15 件：7 件 `motionID 0x00` 按 `PanicRadiusM(sizeGroup 0)=2 m`，8 件 `0x04`（条件 `inZone && parentRolled`，原作未知）保持静止 |
| 相位 | 本数据**没有** `props[].phase`；全部走 `hash32(meta.id/itemId)` |
| 骨骼 | `placement.v2.json` **没有** `defs[].clips` / `props[].clipStart`（0 条）。骨骼数据在仓库外：`~/KatamariRef/props/skinned/type_{480,481,482}.glb`（3 种螃蟹，120/153 实例，5 骨 5 剪辑），其余 16 型（33 实例）**未导出** |
| 编辑器播放走哪套 | 同一份 `placement.v2.json` → `Tools/motion_sim`（把 `MotionMath.cs`/`PhaseSeed.cs` 当源码编译）→ `Tools/leveleditor`。与游戏**同一份数学**，不同的只是宿主（组合/调度）；本轮之后组合层的类/空间/速度解算在 `MotionHostMath.cs`，`motion_sim` 仍保留自己的 `Pose` 组合（父链用同一个公式，已对拍口径一致，未合并，列为后续） |

## 2. 形状

放在 `Runtime/Pickup/`（不放 `Runtime/Motion/`：那里是球的运动 `BallMotionMath`；`MotionMath`/`PhaseSeed` 与 `motion_sim` 的链接路径都在 `Pickup/`）。

| 文件 | 职责 |
|---|---|
| `MotionMath.cs` | 13 类的纯数学（原有）；新增 `SeedForPhase01(phase)`：`Phase01` 的精确逆，作者 `phase` 覆盖走同一个整数种子 API |
| `MotionHostMath.cs` | 纯函数：类编号、按类分派到 `MotionMath`、三种输出空间的位姿组合、父链携带、速度解算、步长/调度、剔除滞回、"被别人动过"判定 |
| `MotionHost.cs` | UdonSharp，**一个 `Update`**，并行数组每件一行，只负责写 transform / Animator |
| `Editor/LevelBuild/PlacementMotionRows.cs` | 纯 C#：文档 → 行表（哪些件有行、父在前排序、速度/相位/触发门/路径展平） |
| `Editor/LevelBuild/PlacementImporter.Motion.cs` | 把行表写进场景里的 `MotionHost`（幂等） |

没有新增 JSON 键：用的都是已在白名单里的 `motion/phase/parent/inherit/params/updatesPerSecond/loop/speed`；`params.triggerRadiusM` 是 §6.7.3 已写的键，校验器对 `params` 只查"有限数字"，键与校验早已同批。`PlacementV2Motion.ParamValues`、`PlacementV2Path.LoopMode` 是读取侧补的字段。

## 3. 语义

- **时间**：每行按自己的档（`updatesPerSecond`：hop/whack 30、sway/路径/游走 15、bob/clouds 10、spin/flip/触发检测 5）走**固定日程**；每个到期步推进正好 `30/rate` 游戏帧（拆成 ≤2 的块，`MotionMath` §12.0），结果与渲染帧率无关。每帧最多补 3 步，落后太多重排日程。
- **平滑**：<30 Hz 的行在两步之间每渲染帧 `Lerp/Slerp`（视觉比目标滞后一个间隔，最多 0.2 s 的 spin）；30 Hz 的 hop/whack 只在步进帧写，不抹平 7 帧的下陷。
- **三种空间**：spawn 局部（spin/flip/bob/sway/whack/hop）、世界轴偏移 + 世界 yaw（游走、云；云保持出生朝向）、绝对世界（三种路径）。路径行把 `spawnPos` 只用于首次找最近点。路径点在地面上，路径类的位置是 `point + (0, 半高, 0)`（`MotionHostMath.PathStep` 把 `MotionMath` 的 `point − (0, b, 0)` 抬 `2b`，依据见 ORIGINAL_SKELETAL_ANIMATION §11）；`motion_sim`（编辑器预览）仍用 `MotionMath` 原式，路径件在预览里会低一个全高。
- **父链**：行按父在前排序；子 = `world(P) · inverse(spawn(P)) · own(child)`（`inherit:"position"` 只带平移）。子件自己的运动是叠在父变换上的局部运动。绝对世界类不吃父链。
- **相位（D-12）**：`props[].phase` → `SeedForPhase01`；否则 `PhaseSeed.From(meta.id, itemId)`。种子之后的所有随机抽取都是它的确定函数，无 `Random`、无时钟。
- **触发门 ≠ 剔除半径**：`rowTriggerRadiusM`（`params.triggerRadiusM`，缺省 `PanicRadiusM(sizeGroup)` = 2 m）每 5 Hz 对球心距离判一次，触发后闩锁并设 Animator `Panic`；`wakeRadiusM=12`、`cullHysteresisM=3` 只决定是否**冻结**（时钟与状态保留，回来不重置、不跳变，Animator 组件关/开）。`sizeGroup` 本数据没有，全按 0（`motion_sim` 同口径）。**触发后的动作原作未知**，目前不位移，8 件 `0x04` 保持静止（M1/M2）。
- **只驱动"自由"的件**（与吸收/坠落的约定，全部是对 transform 的**只读**检查，不需要 `PickupFieldHost`/电机配合）：
  1. `transform.parent != Start 时的父` → **Away**（被吸收挂到了球的附着根）：不再写。`ResetField` 把它放回原父后自动 **Revive**（重置状态、回出生位姿）。
  2. 上次写入位置与当前位置相差 >2 cm → **Yielded**（别人动了它：坠落、搬运）：不再写，直到 `ResumeRowAfterSettle`。
  3. 父行不在 Active（父被吃/被挪走）→ 子行 **Detached**，不再被携带；父回来则恢复。
  4. `SuspendRow(row)` → **Suspended**。
  给 stack-fall 的最小接口（可选，不调也安全，因为 2 已能识别）：`FindRow(Transform)`、`SuspendRow(int)`、`ResumeRowAfterSettle(int)`（D-14：落定后把出生位姿重定为当前位姿、断开父链、时钟继续）。给回合控制器：`ResetAll()`（重开回合）。**不要**去改 `PickupFieldHost`/电机。
- **挡路/接触**：见 §5。

## 4. 每帧成本（估算，未测）

数据：全量步进 = 31×15 + 18×30 + 15×15 + 8×30 + 10×15 + 15×5 + 7×5 + 5×10 + 2×10 + 2×5 = **1,810 步/秒**（与 `LEVEL_FORMAT_V2` §6.7.5 一致；spin/flip 每步拆 3 块 dt=2，多约 60 次数学调用/秒）。

- **步进**：≈1,870 次数学调用/秒 ≈ 每帧（90 Hz）约 21 次。
- **写入**：smooth 的 129 行 + 42 个携带行每帧各写一次（hop/whack 26 行与 WaitForPlayer 15 行只在步进帧/携带时写）≈ **每帧最多 ~155 次 `SetPositionAndRotation` + `Lerp` + `Slerp`**（部分行重叠）。
- **检查**：每行每帧 `t.parent` 比较 + 一次 `t.position` 读（漂移检测）+ 数组读写，约 60–70 次 Udon 外部调用/行。
- **口径**：按仓库 [PROP_BEHAVIOURS](PROP_BEHAVIOURS.md) §7.1 的 0.02 ms/次单件更新（`budget.py`）：155 行 × 0.02 ms ≈ **3.1 ms/帧上界**（PC，每行都满更新时）；按实际每行 ~65 外部调用 × 0.2–0.5 µs ≈ 0.01–0.03 ms 的区间，**155 行 ≈ 1.6–5 ms**。上界超过 25% 预算（2.78 ms @90 Hz）。**这是估算，不是测量。**
- **12 m 剔除对本关几乎不省**：球半径 12 m 覆盖整个 452 m² 的院子，所以本关全员活跃；剔除对大关才有意义。
- **不预先优化**。若 Unity Profiler 里 `MotionHost.Update` 超标，按收益排序的候选（现在**不做**）：漂移检测降到每 2–3 帧、smooth 改成 30 Hz 步进不插值、把携带行并入父行步进。
- 诊断：勾 `logStatsEachSecond` 每秒打一行 `steps/writes/active/away/yielded/culled`；期望 `steps≈1,800–1,900`。

## 5. 运动道具的接触/挡路：现状核对

`PickupFieldHost.UpdatePropObstacles`（`PickupFieldHost.cs`）：
- **位置读实时 transform**（`propTransforms[i].position`），所以走动的件位置是对的。
- **v1 盒**：`propBounds[i]`（出生时的**世界 AABB 半边长**）以实时位置为心重建——**不随 yaw 转**：转了向的游走蟹，盒子仍是出生朝向的盒子（对窄长件有误差，上界为 AABB 对角）。
- **v2 OBB**（`RefreshPropObbs`）：`propLocalBounds`/代理 + `prop.rotation` **实时位姿**，朝向正确。
- 只对"球吃不下的件"（`IsBeyondAbsorbLimit`）建挡路/支撑；扫描间隔 `scanIntervalSeconds=0.05 s`，所以运动件的盒/OBB **最多滞后 50 ms**（游走 0.3 m/s ⇒ ≤1.5 cm）。
- **与 LEVEL_FORMAT_V2 §6.7.7 的旧表述不一致**：该节写的是"移动件不注册为挡路盒/支撑面"，现实是**会注册**（宿主按球径而不是按 `behaviour` 判）。影响：球会被走动的大件挡住、能站在转动的大件上（支撑面随 transform 动，滞后 ≤50 ms）。这符合"移动件不豁免"，且数字小；**不需要修**。若以后要求"移动件只可吃不挡路"，最小修正是 `PickupFieldHost` 加一列 `propMoves` 并在 `UpdatePropObstacles` 里 `continue`——不在本轮范围（该文件别人在改）。
- 吸收判定读的是实时位置（`AttachProp` 用 `prop.position`/`rotation`），与 `MotionHost` 同一份 transform，兼容。

## 6. 骨骼动画：普通 Animator

- **数据**（仓库外，`_RefUnlicensed`，不入 git）：`~/KatamariRef/props/skinned/type_{480,481,482}.glb` + `report.json`（剪辑名/fps/时长）。
- **转换**：`Tools/levelbuild/blender/skinned_glb_to_fbx.py`（Blender 4.0.2 无头，**本机已跑通**）→ `~/KatamariRef/props/skinned_fbx/type_N/type_N.fbx`（armature + 蒙皮 + 5 个 take：`walk walk_fast slot2 idle slot4`，按 glTF 动作名去掉 `_root` 后缀查剪辑表命名，并断言每个 take 时长等于 `report.json` 的秒数）+ `skinned_defs.json`（def→type）。
- **装配**（编辑器，菜单 `VRC Katamari/Level/Assemble Skeletal Animation (reference)`，`SkeletalAnimationAssembler.cs`）：FBX 按 Generic 导入、take 循环；每型一个 `AnimatorController`（参数 `Moving`、`Panic`；状态 `idle`（默认）/`walk`/`walk_fast`）；每个骨骼实例在道具下建子物体 `Skin`（FBX 实例，按 mesh 包围盒中心对齐静态模型，静态 Renderer 关掉但保留），挂 Animator + 共享控制器；输出 `[anim_fit]` 行校验蒙皮/静态尺寸比（>±25% 就标红，提示 FBX 轴有问题）。两侧都取 `sharedMesh.bounds` 经各自 transform 的 8 角（蒙皮侧是 bind 姿态）；不用 `renderer.bounds`，因为 `SkinnedMeshRenderer.bounds` 覆盖全部剪辑姿态且在倾斜的根骨坐标系里，天然比模型大。拾取同步（`PickupFieldSync`）量尺寸时跳过 `Skin` 子树，只量静态 Renderer，所以 `propBounds`/`propLocalBounds`/回退 BoxCollider 不含蒙皮包围盒。源文件跟踪：每型的戳（FBX 字节与 `skinned_defs.json` 的 SHA-256）存在 FBX 导入器的 `userData`；戳与磁盘不符即视为源已变：剪辑设置从文件重读（take 范围随之更新）、控制器三个状态改接到重导入后的剪辑、该型现存的 `Skin` 全部重建。`Skin` 缺网格、实例的不是该 FBX、或 bounds 中心偏离静态模型超过 0.5 mm 也重建。重建前先查 `Skin` 子树：有 `MonoBehaviour`（含 `UdonBehaviour`）则什么都不销毁，报 problem 并清掉戳以便下次重试。重导入后每条剪辑的长度与 `skinned_defs.json` 的秒数对照（`[anim_clip]`，差 >2 ms 算 problem）。每次运行对每个现存 `Skin` 都输出 `[anim_fit]`。`Skin` 以外挂在道具下的骨骼 FBX 实例（名为 `type_N` 或来自动画根目录的资产）报 `[anim_stray]` problem，不自动删除：它的 renderer 会撑大道具的量测包围盒。源不变时幂等（`+0 ~0 -0`）；换了 FBX 或 `skinned_defs.json` 不需要手工删 `Skin`，也不需要清空 `clipAnimations`。
- **帧率**：FBX 的 `TimeMode` 是 Unity 剪辑的采样率，Unity 把 take 取整到整帧。转换脚本把场景帧率设为使全部剪辑为整帧的最低值（这三型 60 fps：walk/idle 58 帧 = 0.9667 s，walk_fast 29 帧 = 0.4833 s，slot2 6 帧 = 0.1 s）；`skinned_defs.json` 记 `fps`，装配器核对导入后的剪辑长度。
- **与运动宿主的关系**：路径运动（宿主写道具根 transform）+ 原地动画（Animator 在子物体 `Skin` 上），互不干涉。宿主：启动时 `Play("idle", 0, phase01)` 打散同相；游走蟹在 Walk 相位时 `Moving=true`；WaitForPlayer 触发时 `Panic=true`；被剔除时 `animator.enabled=false`（PROP_BEHAVIOURS §7.3，不是 `SetActive`）。纯骨骼、无运动的件（如静止的蟹）是"只有 Animator 的行"（类 0、无父），同样吃剔除/相位。
- **导入顺序**：先装配（加 Animator）→ 再跑关卡导入（`rowAnimators` 列由导入器从场景里读）。导入器每次都读，所以装配后重新导入一次即接线。
- **局限（写明，不假装）**：只有 3 型 120/153 实例；"哪条是 walk"是导出器按"腿摆 ±15°、0.967 s"的命名（研究里最强的读法，非证实）；`playAnimNo` 读法未定，所以开局一律 idle 再按 Moving 切；FBX 轴/尺度未在 Unity 验证（`[anim_fit]` 就是为此）。

## 7. 给 Unity 操作员

**需要 Unity 生成（不手写）**：`Runtime/Pickup/MotionHost.cs.meta`、`MotionHostMath.cs.meta`、`Editor/LevelBuild/PlacementMotionRows.cs.meta`、`PlacementImporter.Motion.cs.meta`、`SkeletalAnimationAssembler.cs.meta`、`Tests/EditMode/Pickup/MotionHostMathTests.cs.meta`、`Tests/EditMode/LevelBuild/PlacementMotionRowsTests.cs.meta`；**U# 程序资产** `Runtime/Pickup/MotionHost.asset(+.meta)`，以及编译产生的 `Assets/SerializedUdonPrograms/*.asset`（整批提交或整批丢弃）。`MotionHostMath` 是静态类，不需要程序资产。

步骤与判据：
1. 刷新/编译：Console 0 error；`MotionHost` 出现 U# 程序资产。**特别看**：U# 是否接受 `float[][]` 锯齿数组与 `Transform.SetPositionAndRotation`、`Animator.Play(string,int,float)`；不接受时的退路是把 `rowState` 拍平成带偏移的一维数组并给 `MotionMath` 加偏移重载。
2. EditMode 测试：`MotionHostMathTests`（29）、`PlacementMotionRowsTests`（8，其中"参考关卡行数"那条本机跑不了，需 Unity：期望 `Own=113 Rider=42 paths=4 errors=0`）。
3. （可选，要骨骼）把 `~/KatamariRef/props/skinned_fbx/*` 拷到 Windows 工程 `Assets/_RefUnlicensed/Animation/`，跑菜单 `Assemble Skeletal Animation`：期望 `[anim] +N ~0`、每行 `[anim_fit]` 比值接近 1；再跑一次 `+0 ~0`。
4. 重新导入 RefCancer（`Import Placement`）：期望首次 `motion host: rows=155`（有骨骼则 +已装配的纯动画行）、`writes>0`；**第二次 `writes=0`、headline `other=0`**。
5. Play：院子里螃蟹游走、风铃/云/旗杆摆动、hop 的蟹短促下陷；勾 `logStatsEachSecond`，每秒 `steps≈1,800–1,900`，`MotionHost.Update` 的 Profiler 耗时记录下来（PC 与 Quest 分别）。
6. 吸收一只在动的蟹：它立刻停在球上（`away` +1），不抖、不回弹；`ResetField`/重开回合后它回到出生位姿重新走（`away` 回落）。
7. 球走到离某蟹 2 m 内：`GetTriggeredCount` +1、该蟹 Animator `Panic`；球在 3–12 m：蟹照常游走，不触发。
8. 与 stack-fall 联调：吃掉一件有子件的动父件，子件坠落期间 `yielded` +N（宿主不抢位姿），落定后若调用了 `ResumeRowAfterSettle` 继续动。
