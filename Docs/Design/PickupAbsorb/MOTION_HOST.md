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
| 骨骼 | `placement.v2.json` 不携带 `defs[].clips` / `props[].clipStart`（0 条）。蟹座可摆放的原作骨骼道具共 18 型、152 件；骨骼资产和剪辑清单留在本地参考目录，见 §6。 |
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
- **平滑**：<30 Hz 的行在两步之间每渲染帧 `Lerp/Slerp`（视觉比目标滞后一个间隔，最多 0.2 s 的 spin）；30 Hz 的 hop/whack 只在步进帧写，不抹平 约 7 帧的上跳。
- **三种空间**：出生局部（spin/flip/sway）；世界轴偏移（bob/whack/hop 沿世界 Y，云沿水平轴；四类保留出生旋转，只有游走写世界偏航）；绝对世界（三种路径，`back_and_forth` 保留出生旋转）。`MotionMath` 负责统一运动帧换算，已转世界的路径点只加从 PS2 转来的半高偏移 `point + (0, 半高, 0)`；宿主与预览都直接用这个结果。
- **父链**：行按父在前排序；子 = `world(P) · inverse(spawn(P)) · own(child)`（`inherit:"position"` 只带平移）。子件自己的运动是叠在父变换上的局部运动。绝对世界类不吃父链。
  - **场景里的父子是文件夹，不是 Transform 父子**：导入器把子件放进 `Props/<kind>/<zone>/Hierarchy/<父id>_children/`（单位位姿的空文件夹，没有 Renderer），子件仍是 `Props` 下带 Renderer 的顶层对象，宿主行用 `IndexProps`（只收带 Renderer 的对象）解析，所以 `rowTransforms` 永远指向真道具。文件夹不叫 `<父id>`：曾经同名，按名递归查找（探针 `Find(root, name)`）会先命中那个恒为 (0,0,0) 的空文件夹，把"带子件的父蟹"读成"卡在原点、propY=0、没有 SkinnedMeshRenderer（只看到子件的网格）"——R37 的 coconut_crab_009/011、fiddler_crab_034 就是这样被误报的。现在场景里不再有与道具同名的容器。
  - **原作里蟹真的"带"东西**：`parentProp.monoControlIndex`（REROLL dump）与抽取器的父表逐条一致（83/83，其余 7 条父是不在 511 件里的 Dummy Hit）；483 号电视、426 号纸拉门、428/425 号小蟹挂在 482/424/427 号蟹下，电视底面与地面同高、蟹在电视体积内（蟹"穿"着电视）。不是抽取器推错。
- **相位（D-12）**：`props[].phase` → `SeedForPhase01`；否则 `PhaseSeed.From(meta.id, itemId)`。种子之后的所有随机抽取都是它的确定函数，无 `Random`、无时钟。
- **触发门 ≠ 剔除半径**：`rowTriggerRadiusM`（`params.triggerRadiusM`，缺省 `PanicRadiusM(sizeGroup)` = 2 m）每 5 Hz 对球心距离判一次，触发后闩锁并设 Animator `Panic`；`wakeRadiusM=12`、`cullHysteresisM=3` 只决定是否**冻结**（时钟与状态保留，回来不重置、不跳变，Animator 组件关/开）。`sizeGroup` 本数据没有，全按 0（`motion_sim` 同口径）。**触发后的动作原作未知**，目前不位移，8 件 `0x04` 保持静止（M1/M2）。
- **只驱动"自由"的件**（与吸收/坠落的约定，全部是对 transform 的**只读**检查，不需要 `PickupFieldHost`/电机配合）：
  1. `transform.parent != Start 时的父` → **Away**（被吸收挂到了球的附着根）：不再写（写之前必查，另每 6 帧批查一次所有活动行）。`ResetField` 把它放回原父后自动 **Revive**（重置状态、回出生位姿）。
  2. 上次写入位置与当前位置相差 >2 cm → **Yielded**（别人动了它：坠落、搬运）：不再写，直到 `ResumeRowAfterSettle`。
  3. 父行不在 Active（父被吃/被挪走）→ 子行 **Detached**，不再被携带；父回来则恢复。
  4. `SuspendRow(row)` → **Suspended**。
  给 stack-fall 的最小接口（可选，不调也安全，因为 2 已能识别）：`FindRow(Transform)`、`SuspendRow(int)`、`ResumeRowAfterSettle(int)`（D-14：落定后把出生位姿重定为当前位姿、断开父链、时钟继续）。给回合控制器：`ResetAll()`（重开回合）。**不要**去改 `PickupFieldHost`/电机。
- **挡路/接触**：见 §5。

## 4. 每帧成本与降频规则

**实测基线（R51 消融，蟹座关、球静止、Editor 后台、31.6 fps）**：关掉 MotionHost 一项，`ScriptUpdate` 14.0 → 5.0 ms，即 **≈ 9 ms**。用 Udon 外部调用（每个 float 运算、数组读、Transform 读写都是一次 extern）数出来的热点，优化前（估算，未测）：

| 来源 | 外部调用/次 | 次数/秒（本关） | 外部调用/秒 | 占比 |
| --- | --- | --- | --- | --- |
| **路径步进**（p23 50 点 × 7 行、p15 59 点 × 2 行，15 Hz）：每步 `IsPathOk` 两次（3N 次有限性检查 + N−1 次开方）、总长一次、线性段扫描 | ≈ 8,500 | 135 | **≈ 1.15 M** | ≈ 55% |
| 每帧每行的所有权检查与写入（`t.parent`、`t.position` + `HasDrifted` 里 6 次有限性检查、Lerp/Slerp、`SetPositionAndRotation`） | ≈ 140 | 155 × 31.6 | ≈ 0.69 M | ≈ 33% |
| 其余步进（hop/whack 30 Hz 共 780 步/秒占一半、sway/walk/bob/spin…） | ≈ 170 | ≈ 1,665 | ≈ 0.28 M | ≈ 13% |

合计 ≈ 2.1 M 外部调用/秒 ÷ 实测 0.284 s/s ⇒ 约 0.135 µs/次，与 Udon 常见量级吻合（这个标定把外部调用数换成毫秒；绝对值不可靠，**占比**才是结论：路径步进是第一热点）。

### 做了什么（运动结果不变）

1. **路径表预计算**（`MotionMath.BuildPathTable` / `TryStepPathPrepared`）：每条路径启动时把累计长度、段长、每段起始朝向与朝向差、俯仰一次算好；每步只剩一次段命中（带段号提示：先试上次的段、下一段，失败才线性扫描）、一次插值。表里存的就是原函数每次重算的那些 float（同顺序累加），所以位置/朝向与原步进逐位一致（测试 `Prepared…MatchesOriginal…` 取 1e-6）。输入校验（速度、半高、出生位置有限）每行只在启动时做一次；路径坏了（有 <1 cm 的段/非有限点）的行与原来一样不动（现在启动时警告一次）。BackAndForth 永远用开路径表（原函数忽略 closed）。≈ 8,500 → ≈ 100 次/步。
2. **远处行只在自己步进的帧写 Transform**：球半径 + `nearMarginM`（默认 6 m）之外的行（`rowFar`，每 `classifyStrideFrames=6` 帧用球心距离重判，1/6 的行每帧）不再每帧插值写入，只在这一帧它自己步进了、或它的父行这一帧写了的时候写。**步进时刻表不动**（每个到期步照常走，状态与优化前逐步相同——hop 的"批量步不等于两个单步"见 `MotionMath` TryStepHop，所以**不做**合并步进），写入时的位姿用的是**当前时刻**的插值（`SmoothFraction(now, stepTime, interval)`），所以"被更新那一帧的姿态 = 全频版同一帧的姿态"（测试 `FarSmoothRowsWriteTheCurrentTimePose…`，60/30/144 fps）。远处行的可见更新率从每帧降到其步进率（15/10/5 Hz，与原作自己的 15 Hz 离散一致）。
3. **步进帧才写，且位姿没变就不写**：30 Hz 的 hop/whack 的步输出与上一步逐位相同（休息的 hop、停在顶端的 whack）时不再重算 `StandalonePose`、不写 Transform（`SamePoseRequest` 是精确比较，不是 Unity `==` 的近似比较）。hop 18 行 ≈ 95% 的时间在休息。携带行（`rowRiding`）只在父行本帧写过、或自己有步进时才写（父没动，子的位姿是同一个函数值）。
4. **每行常量预计算**：`rowSpace`、`rowKeepsSpawnRotation`、`rowHopSpeedCmPerFrame`（原来每步都当实参调 `HopSpeedCmPerFrame` 一串比较）、`rowRiding`（父链在 Start 校验之后才定）。`HasDrifted` → `HasDriftedSquared`（容差平方启动时算一次，`!(d² ≤ tol²)` 所以 NaN/∞ 仍算漂移；约 45 → 16 次外部调用）。统计计数只在 `logStatsEachSecond` 时累加。
5. **所有权检查改到"写之前"**：`t.parent`/漂移只在真要写 Transform 之前查（保护的就是这次写，所以不会覆盖被吸附/被摔的道具）；每 6 帧的分类批里再对所有活动行查一次 `t.parent`（被吸收 → Away，回合重置后能 Revive、状态重置）；不写 Transform 的行（WaitForPlayer、只有 Animator 的行）靠这一查发现被吸收。检查次数从"每行每帧"降到"每行每 6 帧 + 每次写"。
6. **剔除/分类**：剔除用同一次距离计算，不再每行每帧做 `(i+frame)%stride`；`classifyStrideFrames` 12 → 6（grep 在 .unity/.prefab 里没有找到序列化的 `cullStrideFrames`，字段改名后默认值生效）。

### 预期（估算，未测；Unity 验证后以实测为准）

同一场景、同一标定（0.135 µs/次、31.6 fps）：

| 项 | 优化后外部调用/秒 | 说明 |
| --- | --- | --- |
| 路径步进 | ≈ 19 k（135 × ≈140，含步进壳） | 1.15 M → 0.02 M |
| 其余步进 | ≈ 0.28 M → ≈ 0.17 M | hop/whack 休息步省 `StandalonePose`/有限性检查 ≈ 80 次/步 |
| 每帧行驱动 | 近处行 ≈ 95/次，远处/冻结行 ≈ 12–30/次；假设 40% 近：≈ 8k/帧 × 31.6 ≈ 0.26 M | 原 0.69 M |
| 合计 | ≈ **0.47 M/秒 ⇒ ≈ 2.0 ms/帧**（近处占 20% 时 ≈ 1.7 ms，占 60% 时 ≈ 2.4 ms） | 原 ≈ 9 ms |

即**降幅约 4.5×（≈ 9 → ≈ 2 ms）**。**没有凭空保证 ≤1.5 ms**：剩下的大头是其余步进（≈ 0.17 M ≈ 0.8 ms，不能再降：步进时刻表和状态必须与优化前一致）和近处行的写入。要再压到 1.5 ms：缩小 `nearMarginM`（4 m 仍盖住吸收/挡路读取，见下）、或把 `wakeRadiusM` 由 12 m 收到 9 m（会改"冻结"的可见范围，是设计参数，需要 owner 决定）。帧率越高，每帧行驱动越贵（每秒的步进成本固定），60 fps 下近处行的每帧写入翻倍，但远处行不涨。

### 远处行的位置滞后：对吸收/挡路的影响

`PickupFieldHost` 每次扫描（0.05 s）读运动行的 `Transform.position` 做吸收候选（球心 1.6 m 内）、挡路/支撑 OBB（球半径 + 0.8 m 余量 + 0.25 m 粘滞 + 道具半对角线内）。`nearMarginM`（6 m）= 这些读取范围（≤ 约 5 m，含 2 m 半对角线）之外再留约 2 m 给"两次分类之间球的位移"（6 帧 × 10 m/s ÷ 30 fps = 2 m）。因此：**所有可能被这些扫描读到的行都在近处，位姿每帧都写，与优化前一样**；远处行读到的位置最多比全频版旧一个步进间隔（15 Hz 的游走/路径行约 2.4–5 cm，10 Hz 的云 ≈ 30 cm），而它们离球 ≥ 6 m，不会被吸、不会挡路。规则：运动行的世界位姿**始终写在它自己的道具 Transform 上**（PickupFieldHost 读的就是它），不新增接口。球变大时近处半径按 `GetCollisionRadiusWorldM()` 同步变大（`NearRadiusM`）。

### 关闭再开（R50 提到的"绝对 Time.time 相位"）

不是问题，R50 的描述与代码不符：运动状态是**逐步积分**的（每步把 `state` 的时钟/距离累加 `dt` 帧），`Time.time` 只用来决定"这一步何时到期"和写入时的插值分数，相位不是 `Time.time` 的函数；种子只在 Start 决定初值。岛被停放时 `Update` 不跑，状态原样冻结，重开后从冻结的姿态继续（与被剔除的行同口径）。优化前重开的实际副作用只有：到期时间已过期，开头最多补 3 步并在一个间隔内用过期的步时刻插值（一次小抖动）。现在加了 `IsResumeGap`：两次 `Update` 间隔 > `resumeGapSeconds`（0.5 s）就把所有活动行的时刻表重新锚到 now（带原来的 8 档错开），状态不变；0.5 s 以内的卡顿仍走原来的补步逻辑。结论：**重开不会跳相位**；若想要"重开像新开局"，那是 `ResetAll()` 的事，不是这里。

- 诊断：勾 `logStatsEachSecond` 每秒打一行 `steps/writes/active/away/yielded/culled/far`；`steps` 应与优化前同量级（≈1,800–1,900，步进没减），`writes` 应明显下降。`decimateFarWrites=false` 回到全频写入，可作 Profiler A/B。

## 5. 运动道具的接触/挡路：现状核对

`PickupFieldHost.UpdatePropObstacles`（`PickupFieldHost.cs`）：
- **位置读实时 transform**（`propTransforms[i].position`），所以走动的件位置是对的。
- **v1 盒**：`propBounds[i]`（出生时的**世界 AABB 半边长**）以实时位置为心重建——**不随 yaw 转**：转了向的游走蟹，盒子仍是出生朝向的盒子（对窄长件有误差，上界为 AABB 对角）。
- **v2 OBB**（`RefreshPropObbs`）：`propLocalBounds`/代理 + `prop.rotation` **实时位姿**，朝向正确。
- 只对"球吃不下的件"（`IsBeyondAbsorbLimit`）建挡路/支撑；扫描间隔 `scanIntervalSeconds=0.05 s`，所以运动件的盒/OBB **最多滞后 50 ms**（游走 0.3 m/s ⇒ ≤1.5 cm）。
- **与 LEVEL_FORMAT_V2 §6.7.7 的旧表述不一致**：该节写的是"移动件不注册为挡路盒/支撑面"，现实是**会注册**（宿主按球径而不是按 `behaviour` 判）。影响：球会被走动的大件挡住、能站在转动的大件上（支撑面随 transform 动，滞后 ≤50 ms）。这符合"移动件不豁免"，且数字小；**不需要修**。若以后要求"移动件只可吃不挡路"，最小修正是 `PickupFieldHost` 加一列 `propMoves` 并在 `UpdatePropObstacles` 里 `continue`——不在本轮范围（该文件别人在改）。
- 吸收判定读的是实时位置（`AttachProp` 用 `prop.position`/`rotation`），与 `MotionHost` 同一份 transform，兼容。

## 6. 骨骼动画：普通 Animator

- **数据**（仅供本地参考，不入 Git 或上传）：初代 PS2 蟹座 level 11 任务包的骨骼 LOD1、原作 `animationBlock`、`motion_table.json`；以 `inLevelProps && animated` 为范围，按 `nameIdx` 对上 `original_defs.json` 的 `mono`，得到 **18 型、152 件**。`1695 Lalala` 不在这一关的摆放定义里。不要按英文名匹配鸽子或麻雀：两对同名类型各有自己的 `mono`。
- **转换**：仓库外的 `katamari_stage_extract.build_props` 出 `type_N.glb` 和 `report.json`，再由 [蒙皮转换器](../../../Tools/levelbuild/blender/skinned_glb_to_fbx.py) 在 Windows Blender 无头运行，输入 `--src`、`--out`、`--motion-table` 和 `--original-defs`，产出每型 `type_N/type_N.fbx` 和唯一的 `skinned_defs.json`。缺源、重名或实例数不一致会中止；`--only` 试转不发布清单。take 按 PS2 槽号取原作状态名（转换器的 `STATE_OF_SLOT`；`skinned_defs.json` 的 `clips[].name` 同名，`slot` 留槽号）：

  | PS2 槽 | take / 状态 | 原作 `ANIM_NAME` 下标（`playAnimNo`） | 有的型 |
  |---|---|---|---|
  | 0 | `walk` | 1 Walk | 18 |
  | 1 | `escape` | 3 Escape | 18（鸽子 BIRD01_C、麻雀 BIRD08_B、玩具狗、打盹妈妈的是定格） |
  | 2 | `rampage` | 2 Rampage | 18 |
  | 3 | `idle` | 0 Idle | 18（玩具狗定格；打盹妈妈是 `Snoose`） |
  | 4 | `chase` | 4 Chase | 9 |
  | 5 | `special` | 5 Special | 2（两只狗） |

  依据：REROLL（2019）`resources.assets` 的 `mononame` 表 `ANIM_NAME` 列（托管层按原生模拟给的编号播 `ANIM_NAME[编号]`），以及 18 型 REROLL 控制器逐状态的剪辑时长、状态速度、定格位置与我们各槽一致（研究记录 `.work/notes/research/reroll_skeletal_export.md`，仓库外；对照表 `~/KatamariRef/kdrr/reroll_skel/state_slot_map.tsv`）。槽序与原作动画编号序不同：按原作 dump 的 `playAnimNo` 选状态时走 `SkeletalStateMath.StateOfAnimNo`，不能把编号当槽号。
- **材质**：18 型的 LOD0 和 LOD1 使用同一张原作纹理（按该型的 TEX0 和逐像素 RGBA 对照），但几何和 UV 点集不同。装配按 `def` 选 FBX，`Skin` 保留 LOD1 UV，把同件静态模型的唯一有贴图共享材质赋给每个蒙皮槽，不复制材质、不改透明设置。如果静态材质缺失或不唯一、蒙皮槽数不对，报 `[anim_material]`，不猜映射。这 18 型原作纹理的 alpha 均不透明。
- **装配**（编辑器，菜单 `VRC Katamari/Level/Assemble Skeletal Animation (reference)`，`SkeletalAnimationAssembler.cs`）：FBX 按 Generic 导入、take 循环；每型一个 `AnimatorController`（参数 `Moving`、`Panic`；状态 `idle`（默认）↔ `walk` 由 `Moving` 切换，`Panic` 从任意状态进 `escape`；FBX 里有的 `rampage`/`chase`/`special` 也装成状态，但没有过渡，原作的触发条件在原生库里尚未逆出。缺 `idle`/`walk`/`escape` 任一 take、或出现不是原作状态名的 take，都报 `[anim_problem]`）；每个骨骼实例在道具下建子物体 `Skin`（FBX 实例，按 mesh 包围盒中心对齐静态模型，静态 Renderer 关掉但保留），挂 Animator + 共享控制器；输出 `[anim_fit]` 行校验蒙皮/静态尺寸比（>±25% 就标红，提示 FBX 轴有问题）。两侧都取 `sharedMesh.bounds` 经各自 transform 的 8 角（蒙皮侧是 bind 姿态）；不用 `renderer.bounds`，因为 `SkinnedMeshRenderer.bounds` 覆盖全部剪辑姿态且在倾斜的根骨坐标系里，天然比模型大。拾取同步（`PickupFieldSync`）量尺寸时跳过 `Skin` 子树，只量静态 Renderer，所以 `propBounds`/`propLocalBounds`/回退 BoxCollider 不含蒙皮包围盒。源文件跟踪：每型的戳（FBX 字节与 `skinned_defs.json` 的 SHA-256）存在 FBX 导入器的 `userData`；戳与磁盘不符即视为源已变：剪辑设置从文件重读（take 范围随之更新）、控制器各状态改接到重导入后的剪辑（状态集合与 FBX 的 take 不符的旧控制器在原资产里重建，GUID 不变）、该型现存的 `Skin` 全部重建。`Skin` 缺网格、任一蒙皮渲染器缺根骨或骨骼引用（`bones-missing`）、实例的不是该 FBX、或 bounds 中心偏离静态模型超过 0.5 mm 也重建。重建替换整个 FBX 实例，不保留旧实例的骨骼删除覆盖。重建前先查 `Skin` 子树：有 `MonoBehaviour`（含 `UdonBehaviour`）则什么都不销毁，报 problem 并清掉戳以便下次重试。重导入后每条剪辑的长度与 `skinned_defs.json` 的秒数对照（`[anim_clip]`，差 >2 ms 算 problem）。每次运行对每个现存 `Skin` 都输出 `[anim_fit]`。`Skin` 以外挂在道具下的骨骼 FBX 实例（名为 `type_N` 或来自动画根目录的资产）报 `[anim_stray]` problem，不自动删除：它的 renderer 会撑大道具的量测包围盒。源不变时幂等（`+0 ~0 -0`）；换了 FBX 或 `skinned_defs.json` 不需要手工删 `Skin`，也不需要清空 `clipAnimations`。
- **帧率**：转换器优先选能让所有 take 落在整帧的最低采样率；少数原作 Q8 速率不能精确对齐时，端点只允许舍入到 1 ms 以内。源速度为零的槽导出一帧长的定格，不补出动作。`skinned_defs.json` 的 `seconds` 是 FBX 时长，`sourceSeconds` 保留原作时长（定格为 `null`）。Windows Blender 3.6.21 已生成并回读 18 型的 83 个 take，其中 6 个定格；非定格时长与报告源值的最大差为 0.9 ms。Unity 导入后的时长仍须独立核对。
- **与运动宿主的关系**：路径运动（宿主写道具根 transform）+ 原地动画（Animator 在子物体 `Skin` 上），互不干涉。宿主：启动时 `Play(SkeletalStateMath.Idle, 0, phase01)` 打散同相；游走蟹在 Walk 相位时 `Moving=true`；**路径蟹（path_collision / sloping_path / back_and_forth）只要有 Animator 就一直 `Moving=true`**（`MotionHostMath.HoldsMovingAnimation`；原作 dump 里 Cancer 的 8 只路径蟹 `playAnimNo` 全是 1 = 起始 MOVING，路径程序从不切 IDLE，唯一例外是 BackAndForthNoYaw 到端点的一步，未建模），此前它们一直播 idle；WaitForPlayer 触发时 `Panic=true`（进 `escape`）；被剔除时 `animator.enabled=false`（PROP_BEHAVIOURS §7.3，不是 `SetActive`）。纯骨骼、无运动的件（如静止的蟹）是"只有 Animator 的行"（类 0、无父），同样吃剔除/相位。
- **导入顺序**：先装配（加 Animator）→ 再跑关卡导入（`rowAnimators` 列由导入器从场景里读）。导入器每次都读，所以装配后重新导入一次即接线。
- **局限**：`playAnimNo` 是原生模拟的动画编号（0 Idle … 5 Special，见上表），但它何时变成 2/4/5 不知道；宿主开局一律进 `idle`，再按运动和触发切换。Toy Dog 的原作槽 3 是定格，默认待机不动不等于没装 Animator；`doggy_001` 的原作 PC 转储记 `playAnimNo=1`，目前仍走统一的启动策略，不把装配成功当成动作状态已逐件还原。新增 15 型的 Unity 尺寸、材质保存回读和实际播放仍须验收。

## 7. 给 Unity 操作员

**需要 Unity 生成（不手写）**：`Runtime/Pickup/MotionHost.cs.meta`、`MotionHostMath.cs.meta`、`Editor/LevelBuild/PlacementMotionRows.cs.meta`、`PlacementImporter.Motion.cs.meta`、`SkeletalAnimationAssembler.cs.meta`、`Tests/EditMode/Pickup/MotionHostMathTests.cs.meta`、`Tests/EditMode/LevelBuild/PlacementMotionRowsTests.cs.meta`；**U# 程序资产** `Runtime/Pickup/MotionHost.asset(+.meta)`，以及编译产生的 `Assets/SerializedUdonPrograms/*.asset`（整批提交或整批丢弃）。`MotionHostMath` 是静态类，不需要程序资产。

步骤与判据：
1. 刷新/编译：Console 0 error；`MotionHost` 出现 U# 程序资产。**特别看**：U# 是否接受 `float[][]` 锯齿数组与 `Transform.SetPositionAndRotation`、`Animator.Play(string,int,float)`；不接受时的退路是把 `rowState` 拍平成带偏移的一维数组并给 `MotionMath` 加偏移重载。
2. EditMode 测试：`MotionHostMathTests`（31）、`MotionHostSchedulingTests`（17：预计算路径与原步进逐位一致、远处行/步进行降频写入的位姿等于全频版、写入门规则）、`PlacementMotionRowsTests`（10，其中"参考关卡行数"与"带子件的路径蟹仍是自有程序行"两条本机跑不了，需 Unity：前者期望 `Own=113 Rider=42 paths=3 errors=0`）；另有 `PlacementImporterHierarchyTests`（子件文件夹名 `<父id>_children`、容器不与道具同名）与 `PlacementV2StructureProxyTests`（有同 id 道具的 proxy 固体关 Renderer 留 BoxCollider、二次导入 `~0`）。
3. 蟹座参考场景：将转换批次的 18 个 FBX 和 `skinned_defs.json` 放进 Windows 工程 `Assets/_RefUnlicensed/Animation/`，覆盖时保留已有 `.meta`。打开 RefCancer，跑 `Assemble Skeletal Animation (reference)`，核 `[anim] problems=0`、18 型 `[anim_clip]`、152 件 `[anim_fit]`；源和材质不变时重跑应 `+0 ~0 -0`。
4. 重新导入 RefCancer（`Import Placement`），预期 `motion host: rows=203, animators=152`；重跑 `column writes=0`。保存后重装 All-Maps Hub，核 `[ref-parity] animators=152/152 motionRowsAll=203/203`，并核 152 件蒙皮均有主贴图。
5. Play：院子里螃蟹游走、风铃摇荡、云平移、旗杆出生驻留后下沉并升回、hop 蟹短促向上跳；勾 `logStatsEachSecond`，每秒 `steps≈1,800–1,900`，`MotionHost.Update` 的 Profiler 耗时记录下来（PC 与 Quest 分别），并与 `decimateFarWrites=false` 对比；蟹座关目标 ≈ 2 ms 量级（基线 ≈ 9 ms）。
6. 吸收一只在动的蟹：它立刻停在球上（`away` +1），不抖、不回弹；`ResetField`/重开回合后它回到出生位姿重新走（`away` 回落）。
7. 球走到离某蟹 2 m 内：`GetTriggeredCount` +1、该蟹 Animator `Panic`；球在 3–12 m：蟹照常游走，不触发。
8. 路径蟹（coconut_crab_009/010/011、fiddler_crab_034/038/041/042、small_fiddler_crab_045）：Animator 参数 `Moving=true`、当前状态 `walk`（不再是 `idle`）；**按物体名取位姿时先确认拿到的是带 Renderer 的那个**（`GetComponent<Renderer>() != null`）：带子件的父蟹下面有 `Hierarchy/<id>_children` 文件夹（单位位姿，名字不再等于蟹 id）。预期 30 s 内这 8 只的世界 xz 都在变、coconut_crab_009/011 与 fiddler_crab_034 不再读到 (0,0,0)，它们的子件（fiddler_crab_037、color_tv_001、fiddler_crab_035、shoji_001）跟着走。
   **贴地读数用静态网格，不用 `SkinnedMeshRenderer.bounds`**：后者覆盖全部剪辑姿态且在倾斜的根骨系里（约静态模型的 1.17×1.27×1.17），会把底部读低 2–9 cm。网格口径（离线，碰撞网格地面）：路径点相对地面 −0.1…+0.3 cm（p22 是 −2.6 cm，数据如此），半高 b = 0.1098（椰子蟹）/ 0.0674（招潮蟹）/ 0.0393（小招潮蟹）；walk 剪辑最低点相对绑定姿态：椰子蟹 −1.6…−2.1 cm、招潮蟹 0、小招潮蟹 +1.1…+1.4 cm，是动画本身的腿摆，不修。
9. 与 stack-fall 联调：吃掉一件有子件的动父件，子件坠落期间 `yielded` +N（宿主不抢位姿），落定后若调用了 `ResumeRowAfterSettle` 继续动。
