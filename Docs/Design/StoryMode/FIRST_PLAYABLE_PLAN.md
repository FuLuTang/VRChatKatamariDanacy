# 剧情模式第一关「第一个可玩」集成计划

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-14。本文只做只读调查与计划，不声称任何 Unity/Play Mode/Build & Test 已验证。
> 范围：让 `story_01_bench_size`（滚到 1.25 m / 120 s / 无交分 / 无对手）在 Unity 里真正可玩。
> 非目标：多人、CPU 对战（T60）、第二关、交分、道具、国王美术。依据：`Docs/Design/GameModes/README.md:79-96`。

## 0. 先给结论

1. **玩家球不动、点球没反应。** CpuSkirmish 的 4 个球里 0 号是玩家位，README 原话“未接输入，静止”（`Labs/CpuSkirmish/README.md:3`）；host 的注释也写明它只开回合、从不驱动（`Runtime/Ai/CpuSkirmishBootstrap.cs:16`）。全项目唯一的驱动接缝是 `SetMoveAxes`（`Runtime/BallMotor/KatamariBallMotor.cs:965-970`），除 CPU pilot（`Runtime/Ai/CpuBallPilot.cs:211-218`）、BallPlayground 的脚本时间线（`Labs/BallPlayground/BallPlaygroundBootstrap.cs:270`）和桌面适配器 `DesktopMoveInput`（S1，已在 `Labs/StoryLevel` 编译实跑，commit `e74ff8c`）外没有别的驱动来源。CpuSkirmish 里没有 `VRCStation`（全场景检索该 GUID 为 0 命中；作为对照 `Labs/StationPlatform/StationPlatformLab.unity` 有 1 个），也没有 `Interact` 处理，所以点球不可能入座。
2. **半埋不是物理问题，是视觉没缩。** CpuSkirmish 的 `BallVisual` 用内置 1 m 球（mesh 10207，`Labs/CpuSkirmish/CpuSkirmish.unity:832`），`localScale` 恒为 1（`:778`）；motor 把球心放在 y≈0.15（`:449`），地面顶面在 y=0（Ground y=−0.2、scale y=0.4，`:579-580`）。于是 1 m 球有 0.35 m（半径的 70%）在地面以下。motor 从不写 scale（`KatamariBallMotor.cs:931-940` 只写 position/rotation），host 也“从不 resize 球”（`CpuSkirmishBootstrap.cs:16`）；BallPlayground 每帧把视觉球缩到语义直径（`Labs/BallPlayground/BallPlaygroundBootstrap.cs:148-152`），所以那边看着是对的。
3. **方块不粘是没接，不是坏了。** 吸附后方块被 `SetActive(false)` 直接藏掉（`Labs/CpuSkirmish/PlaygroundProp.cs:164-169`）；motor 只提供 absorbed-event ring（`KatamariBallMotor.cs:280`、`:1523-1531`）。宿主代码已经写了（`Runtime/Pickup/PickupFieldHost.cs`，用 `AttachmentMath` 把被吸物件挂到球的附着根），缺的只是场景接线：`AttachmentRoot` 与道具行还没有接（§3）。

## 1. 玩家控制与“骑球”

**已有（文档 + 实验证据）**
- 约定关系：Station + 独立 FollowRig；FollowRig“绝不成为滚动 Mesh 的孩子”（`Docs/02_TECHNICAL_CONTRACTS.md:28`）；“FollowRig 挂到 Station/玩家参考系；本层只给位置 + yaw”（`Docs/Design/BallMotor/README.md:169`）；坐姿用 Station 的 Disable Station Exit 处理退出（`Docs/01_GAME_DESIGN.md:63`）；摄像机不随球滚转（`:137-141`）。
- StationPlatform 实验（PC 桌面）证明了：可入座；坐姿 Jump 不退座、长按只计一次；退出按钮恢复 JumpImpulse；入座后交互提示关闭（D1 PASS，用户目视）。见 `Docs/Evidence/StationPlatform/README.md:5`、`:23-28`。桌面交互是准星对准后**鼠标左键**，E 无效（`:28`）。
- 载人机制本身很简单：实验里 Station 是 `StationCarrier` 的子物体（`Labs/StationPlatform/StationPlatformLab.unity:187`），driver 每帧移动 carrier（`Labs/StationPlatform/StationLabDriver.cs:54-59`），玩家就跟着走；`Interact` 里调 `station.UseStation(player)`（`:70-75`）。
- 输入接缝：`SetMoveAxes(forward, turn, lateral)` 落到 `debugForward/Turn/Lateral`（`KatamariBallMotor.cs:965-970`、`:140-144`），`StepInput` 用 `(lateral, forward)` 组 `Vector2` 走 ControlIntent（`:663-682`），turn 直接进 BallMotion，±1 = ±110°/s（`Runtime/Motion/BallMotionMath.cs:155-167`）。ControlIntent 层不读设备（`Docs/Design/ControlIntent/README.md:9`），设备读取属于适配器。

**没接**
- 桌面适配器 `DesktopMoveInput` 已写，并在 `Labs/StoryLevel` 里编译实跑（commit `e74ff8c`），但那是**站着**的键盘实验。坐姿下轴回调是否还到达 Udon 仍未验证——StationPlatform 那次只加过日志，而且那版源码**改了但从未编译/运行**（`Docs/Evidence/StationPlatform/axis_logging.md:3`、`:110-112`）。
- FollowRig 在场景里全是空引用：CpuSkirmish 四个 motor 的 `followRigRoot` 都是 0（`CpuSkirmish.unity:448`、`:2455`、`:4156`、`:5332`），BallPlayground 也是 0（`Labs/BallPlayground/BallPlayground.unity:4329`）。motor 算出的平滑跟随帧没有落点。
- CpuSkirmish 没有 Station / Interact（见 §0 第 1 条），所以“点球跟上去”完全不存在。

**最小正确接线**
- 新 `Runtime/Input/DesktopMoveInput.cs`（UdonSharpBehaviour，独占 GameObject，`BehaviourSyncMode.None`）：只做设备→三轴。`InputMoveVertical` 存 forward，`InputMoveHorizontal` 存 turn（A 负、D 正），`OnStationExited`/`OnPlayerRespawn`/`OnDisable` 清零后 `SetMoveAxes(0,0,0)`。**不要**把设备读取写进 motor。
- 新 `Runtime/Riding/BallRideStation.cs`：`Interact` 未入座则 `station.UseStation(localPlayer)`；`OnStationEntered` 标记 seated 并可驱动；`OnStationExited` 复位。退出另用一个世界内按钮的 `Interact` → `station.ExitStation(localPlayer)`（与 `StationLabExitButton.cs` 同形，但作为产品行为重写，不引用 Labs 代码）。
- 场景：`RideRoot` 接到 motor 的 `followRigRoot`（motor 每帧写它的 position/yaw，`KatamariBallMotor.cs:1026-1032`），Station 作为 `RideRoot` 子物体，朝向球的前进方向。**别把两套偏移叠起来**：FollowRig 的帧本身就是「球心后方 0.75 + 2.2·r m、上方 0.55 + 2.4·r m」（见 `Docs/Design/BallMotor/README.md` 的「跟随架」），所以 rig 根就是取景眼点，Station 的 `localPosition` 从 `(0, 0, 0)` 起，只做坐姿视点补偿；再叠一层位移会叠成约 4 m 后退。`followRigRoot` 必须真的接上，否则这段完全不生效。VRCStation 打开 Disable Station Exit（`Docs/01_GAME_DESIGN.md:63`），退出只走世界内按钮。
- 为什么不用 FollowRig 直接带人：它只输出 position + yaw（`Runtime/BallLayers/FollowRig/FollowRigMath.cs:74`），没有能力移动玩家；能把玩家带走的只有 Station。文档也是这个分工（`Docs/Design/BallMotor/README.md:164`）。
- 装配约束按**项目惯例**：`KatamariBallMotor` 独占 `MotorHost`，新增的输入/骑球/宿主/剧情 Behaviour 也各自独占对象。理由是工程性的（职责边界清楚、自检一眼可验、重建层级不会留下旧副本），**不是**引擎限制——同物体挂多个 UdonSharpBehaviour 是官方支持的，旧说法（“同物体第二个脚本会被引擎关掉”）已撤回（`Docs/Design/BallMotor/README.md` 的「装配约束」）。真正的同物体硬约束是同步模式：多个 UdonBehaviour 会回落到其中最严格的那个，本项目行为都是 `BehaviourSyncMode.None`，所以别把需要 Manual/Continuous 的行为与它们同挂。例外：`BallRideStation` 必须与 VRCStation + 交互 Collider 同物体，因为 `Interact` 与 `OnStationEntered/Exited` 只送到那个物体（§1“骑球”段）。

## 2. 球贴地（不是半埋）与“碰撞”是什么意思

**已有**
- 球心=中心：每步 Sweep → GroundContact；空代理时支撑高度=核心半径，球心被放到 `planeOffset + support`（`Runtime/BallLayers/GroundContact/GroundContactMath.cs:89-111`）。地面是一条解析平面（`KatamariBallMotor.cs:86-87`），host 用 `AddPlane` 注册（`:1087-1101`）。
- 三个尺寸分开：核心半径 / 碰撞半径 / 语义直径（`KatamariBallMotor.cs:1363-1376`）。默认起始语义直径 0.29974 m、碰撞半径 0.14987 m（`Docs/Design/BallMotor/README.md:130`），所以球心 y≈0.15 本身是对的。
- BallPlayground 已证明正确做法：`ballVisual.localScale = diameter`（`BallPlaygroundBootstrap.cs:148-152`），实跑球心 y 随半径 0.15→0.48（`tasks.json:2158`）。

**没接 / 根因**
- CpuSkirmish 没有任何“按语义直径缩放”的代码；它的 host 明说“never drives or resizes a ball”（`CpuSkirmishBootstrap.cs:16`）。内置 Sphere 的 pivot 在球心（`CpuSkirmish.unity:832`），所以不是 pivot 偏移，就是没缩。验证方法：`center.y − diameter/2` 应为 0；当前应是 −0.35。
- 本设计的“碰撞”不是 Rigidbody/PhysX：球没有 Collider，只有运动学球对已注册平面/盒子的扫掠（`KatamariBallMotor.cs:235-236`、`:550-556`），上限 8 平面 + 8 盒子（`:1089-1092`、`:1105-1108`），超出直接丢并 latch 溢出信号。地面接触是支撑高度解算，不是动力学（`Docs/02_TECHNICAL_CONTRACTS.md:54`）。
- 对单人第一关够不够：**平地、无台阶、障碍 ≤8 时够**（能滚、挡墙、吸、长大）。坡道/台阶/多接触/滚动角速度明确“还需实现和测试”（`Docs/02_TECHNICAL_CONTRACTS.md:54`），滑墙的切向去速也没有公开函数（`Docs/Design/BallMotor/README.md:197`），所以 PicnicLawn 的花圃台/树根丘不能靠这套通过。

**最小正确接线**
- 新 `Runtime/BallPresentation/BallView.cs`：直径变化超过 1e-4 时写 `coreMesh.localScale = Vector3.one * motor.GetSemanticDiameterWorldM()`。`coreMesh` 是 `BallVisualRollRoot` 的子物体，scale 由它独占；**不缩放附着根**（滚动根 scale 恒 1：`Docs/02_TECHNICAL_CONTRACTS.md:25`、`Docs/Design/BallLayers/VisualRoll/README.md:9`）。
- 落地判据：`center.y − diameter/2 ∈ [−0.01, 0.01]`；球长大后仍成立（地面平面 offset 0）。

## 3. 吸附物粘在球上

**已有**
- `AttachmentMath` 提供世界位姿→球局部位姿、身份幂等、十列记录、撤销/快照（`Runtime/BallLayers/Attachment/AttachmentMath.cs:90-113`、`:222`），并明确物件 scale 不随球芯放大（`:18-33`）。
- motor 的 ring 每次成功 commit 记 id/位置/最大边长（`KatamariBallMotor.cs:773-775`、`:1523-1531`），读者用绝对下标当游标（`:1567-1588`），容量 16、满了覆盖最旧（`:1547-1561`）。
- 场景里目前的唯一消费者是 lab 的 `PlaygroundProp`：查到自己的 id 就 `SetActive(false)` + 关 collider（`PlaygroundProp.cs:144-169`）。这就是用户看到的“方块消失”。运行时的消费者是 `PickupFieldHost`（源码已写、未编译、未接线，见下）。

**没接**
- 吸附宿主源码已写（`Runtime/Pickup/PickupFieldHost.cs`），但**场景没接**：没有 `AttachmentRoot`、没有道具数组行，CpuSkirmish / BallPlayground 里放的仍是 lab 的 `PlaygroundProp`。宿主本身也**未编译**。
- 事件位置是**提交时**快照（`SubmitCandidate` 的 `pendingPositions`，`KatamariBallMotor.cs:1024`），不是吸附瞬间的位置；快滚时可能差几十厘米，不能直接当最终位姿。

**最小正确接线**
- 新 `Runtime/Pickup/PickupFieldHost.cs`（一个宿主管全部道具；灰盒规模是 448 件，不能每件一个 `Update`——`Labs/PicnicLawnGreybox/GreyboxReport.json` 的 `props_placed`）：序列化并行数组（Transform / itemId / definitionId / kind / size / value），按激活半径 `SubmitCandidate`、离开 `WithdrawCandidate`；每帧从自己的游标读 ring，命中自己的 id 就用**该道具当前 Transform** + `motor.GetCenterWorld()` / `GetVisualRollRotation()` 调 `AttachmentMath.TryWorldPoseToLocal`，随后 `SetParent(attachmentRoot)`（先单参、再写 localPosition/localRotation），关 collider、停止再提交、保留视觉。
- 球侧：`AttachmentRoot` 是 `BallVisualRollRoot` 的子物体、localScale 1。吸附物随球滚动，但不随球芯放大。
- 事件消费在宿主，不在 motor；motor 仍不持有、不格式化视觉（`Docs/Design/BallMotor/README.md:162`）。
- 两个数据坑：`valueUnits` 必须是 ≥1 的整数，否则以 `RejectNoGain` 静默拒（`Runtime/Pickup/PickupAbsorbMath.cs:254-255`、`:266-268`；`Docs/OPEN_DECISIONS.md:57`）；`maxItemSizeWorldM` 目前固定 1 m（`KatamariBallMotor.cs:113`，判定在 `PickupAbsorbMath.cs:141`），所以 3.6 m 长凳必定 `itemTooLarge`，第一关不要放长凳，等 (h) 落地（`Docs/OPEN_DECISIONS.md:39`）。

## 4. 剧情关卡流程

**已有（数据与设计）**
- 第一关：目标 1.25 m、120 s、1 人、`bank.enabled=false`、`powerups.enabled=false`、星 1.25/1.45/1.60（`Docs/Design/GameModes/Levels/story_01_bench_size.json:18-57`）；评估 `presenter_id=quest_giver`、`partial_band=0.9`、`retry=unlimited`、`score_channel=none`、`ledger_write=false`（`:58-64`）。
- 流程设计：计时归零→冻结→呈给国王→评估→结果；达成不提前结束（`Docs/Design/GameModes/README.md:128-143`）；剧情无交分/无对手（`:79-96`）；实现清单（`:615-624`）。
- 数据校验：`python3 Docs/Design/GameModes/validate_levels.py` 通过（见 §9）。

**没接**
- 运行时零实现。CpuSkirmish 的 Playing→Settling→Ended 是 lab 专用，作者自己写“不属 Runtime，真正的 game mode 是后面的任务”（`Runtime/Ai/CpuSkirmishBootstrap.cs:277-285`），**不能当产品代码复用**。motor 的 `TryBeginPlaying` 只走账本三条合法边（`KatamariBallMotor.cs:950-962`），没有计时、目标、结算。
- 文档提议的 `StoryLevelDefinition.asset` 与导入器（`Docs/Design/GameModes/README.md:373-375`）没有写资产类型；按项目规则 Udon 侧不放用户 struct/非 Behaviour 类（`AGENTS.md:29`），所以第一关的数据应放场景里某个 Behaviour 的序列化字段（或直接放控制器字段）。这条是规则推断，未经 Unity 编译验证。
- 没有“重试”接缝：motor 只有 `TryBeginPlaying`/`TeleportTo`/`TryBank`（`KatamariBallMotor.cs:950-970`、`:1335-1342`），没有重置本局状态/账本的公开 API；关卡却写无限重试（`story_01_bench_size.json:61`）。

**最小正确接线**
- 新 `Runtime/Story/StoryRoundController.cs`：状态 `Idle → Running → Presenting → Result`（int 即可）；`Time.time` 记起点；Running 每帧更新 HUD（目标/当前/剩余）；到点后 `input.SetRoundGate(false)`（输入是两个独立闸门，回合只关自己那个，见 [StoryMode README](<README.md>) 的“输入闸门”）、`propHost.SetCollecting(false)`、`motor.ClearCandidates()`（`KatamariBallMotor.cs:1082-1085`），按结算瞬间的**碰撞直径** `2 * motor.GetCollisionRadiusWorldM()` 查星（今天与语义直径相等，因为两个比例都是 1，`:108-109`），显示“呈给国王”+星数+直径+评语。**不调用** `TryBank`/`TryAwardScore`，不写 `bankedScore`。
- HUD/结果用 uGUI `Text`（BallPlayground 已验证：`BallPlaygroundBootstrap.cs:33`）。King 的形象/动画没有美术，本阶段明确不做，只做结果面板；“呈球是纯表现事件”本身是设计允许的（`GameModes/README.md:131`）。
- 第一关的 6 个数字（level_id、目标、两档星、限时、文本）先写成控制器的序列化字段，注释指向 JSON；**不要**为第一关造一次性导入器。第二关再做 `Editor/Story/` 烘焙器，且只烘规则字段，不烘地图坐标。
- 第二关（类别计数）不在本计划：S3 的宿主已经按 definition 累计吸附件数，接第二关时只把过滤条件换成 `categories.json` 的显式 definition 集合（类别目标见 `Docs/Design/GameModes/Levels/story_02_fruit_ten.json:18-21`），不新增第二套计数实现。
- 重试**不在本计划内**：它需要 motor/账本的重置接缝（清本地球行、commit/claim、尺寸、ring 后重新进 Playing），单独一步做，避免把第一关撑大。

## 5. 用哪张图

**建议：新开一张小的、平的剧情切片场景；不要把 PicnicLawn 灰盒直接当第一关。** 理由都可查：
1. motor 只认 8 平面 + 8 盒子（`KatamariBallMotor.cs:1089-1092`、`:1105-1108`），PicnicLawn 有 38 个障碍面（`Docs/Design/Maps/PicnicLawn/README.md:74`）；超出的被静默丢弃，球会穿树篱。
2. 灰盒是纯几何：996 GameObject / 895 MeshRenderer / 只有 4 个 BoxCollider，没有 Camera、没有 `m_Script`、没有 SDK 组件（`Labs/PicnicLawnGreybox/GreyboxReport.json` 的 counts；对 `PicnicLawnGreybox.unity` 检索 `Camera:`/`m_Script:` 均 0 命中）。448 个道具也没有 item 身份/价值数据。
3. 灰盒有 0.26 m 花圃台、0.14 m 树根丘（`Docs/Design/Maps/PicnicLawn/README.md:339`、`:343`），而运动层没有台阶/坡道（`Docs/02_TECHNICAL_CONTRACTS.md:54`）。
4. `PicnicLawnGreyboxBuilder` 是菜单触发、从零重建场景的一次性生成器（`Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs:8-30`、`:102-110`）；扩展它去搭故事场景违反“不加一次性 builder”（`AGENTS.md:15`）。它应保持为地图作者工具。
5. 切片不影响第一关的规则验证：目标/限时/评估都来自关卡 JSON，与地图无关。

**切片规格**：20×20 m 平地，四周 8 个边界盒，出生点朝中心，约 100 个 s/m 档道具（占位价值 s=1 / m=4 单位，`Docs/Design/GameModes/README.md:45`），总单位 ≥400；目标 245.9 单位 ≈ 62 个 m 档（`Docs/Design/GameModes/README.md:211-215`），留 ≥1.6× 余量。不放长凳/篮子区（>1 m 被尺寸门拒）。切片**不是出货地图**：它借用了关卡规则，但 `map_snapshot`/`map_expectations` 不适用，场景 README 必须写清。

## 6. Build & Test 就绪条件

- 场景必须有 `VRC.Core.PipelineManager`（根对象，恰好一个）和 `VRCSceneDescriptor.ReferenceCamera`，以及 Spawn；脚本搭场景时 SDK 不会替你补，缺了 Play Mode 正常、构建必挂（`Labs/README.md:7-33`）。CpuSkirmish 是现成范例（`CpuSkirmish.unity:2013` Spawn、`:2977` PipelineManager、`:3440`/`:3463` SceneDescriptor/ReferenceCamera）。
- Play Mode 走 ClientSim，只查 SceneDescriptor + spawn；Station/坐姿**没有**在 ClientSim 里验证过（T04 两轮都是 Build & Test，`Docs/Evidence/StationPlatform/README.md:11-15`）。所以“骑球”必须在 Build & Test 里验收。
- 已知阻塞：程序化构建不可用——`manage_build` 报 `ArgumentException: The Assembly UnityEditor.CoreModule is referenced by VRCCore-Editor`，SDK 没有公开 Build 入口（`tasks.json:2158`）。构建只能由用户在 Editor 里点；本计划里所有 Build & Test 都是用户动作。
- T04 两轮都有 SDK 子阶段 `Build Finished, Result: Failure` 但仍生成 bundle 并进入（`Docs/Evidence/StationPlatform/README.md:19`）：照实记录，不写成 PASS。

## 7. 有序步骤（每步独立可验收；第 1 步先让用户能控制球）

**S1 能控制一颗球（含正确大小）**
- 目标：Play Mode 里 WASD 让球动、松手刹车、球底贴地、长大时球变大。
- 文件：新 `Runtime/Input/DesktopMoveInput.cs`、新 `Runtime/BallPresentation/BallView.cs`、新 `Labs/StoryLevel/StoryLevel.unity`（1 球、20×20 地面、边墙、约 30 个临时道具、相机跟随），以及一个只做接线/相机/`TryBeginPlaying` 的 `Labs/StoryLevel/StoryLevelLabBootstrap.cs`。
- 验证：Play Mode；W/S 前后、A/D 转向；日志 `center/heading/diameter`；`center.y − d/2 ≈ 0`；撞边墙停住。判据：30 s 手动运行能沿一条边走完再转回来。
- 大小：S–M（两个小运行时文件 + 一张 lab 场景 + 接线）。

**S2 点球坐上去、跟着球走**
- 目标：鼠标左键点球→入座；WASD 驱动；镜头在球后上方不滚；世界内“退出”能下球。
- 文件：新 `Runtime/Riding/BallRideStation.cs`、`Runtime/Riding/BallRideExitButton.cs`；场景加 `RideRoot`（接 `motor.followRigRoot`）、Station + Collider、退出按钮。
- 验证：**Build & Test**（不是 Play Mode）。日志入座/离座与 `localPlayer.GetPosition()`；确认坐姿 WASD 仍到 Udon；确认球滚时画面不 roll；确认退出后能走。若坐姿轴不来，见风险 1。
- 大小：M（平台风险最高）。

**S3 吸附物留在球上**
- 目标：吃掉的方块不消失，粘在球面并随球走。
- 文件：新 `Runtime/Pickup/PickupFieldHost.cs`；场景加 `AttachmentRoot`（`BallVisualRollRoot` 子物体，scale 1），道具数组接线。
- 验证：Play Mode 吃 5 件；5 件仍可见、随球移动/滚动、不随球芯变大；`absorbed` 与按 definition 的计数 +5；没有远处“隔空”粘上。
- 大小：L（本计划最大逻辑块）。

**S4 剧情流程：计时、目标、呈给国王**
- 目标：120 s 到点冻结，按 1.25 m 判星，显示结果；全程 `banked=0`。
- 文件：新 `Runtime/Story/StoryRoundController.cs`；场景加 HUD/结果 Text。
- 验证：Play Mode 用 15 s 测试限时；0/1/2/3 星各触发一次；到点后不再吸收；日志 `GetBankedScoreUnits()==0`。
- 大小：M。

**S5 切片内容够 1.25 m**
- 目标：切片总单位 ≥400，1.25 m 在 120 s 内可达且有余量。
- 文件：`Labs/StoryLevel/StoryLevel.unity` 布置、`Labs/StoryLevel/README.md`。
- 验证：Play Mode 一次“尽量吃”的运行，记录达标时刻与总单位；要求达标 ≤ 总单位 65%、达标时刻 ≤ 80 s。不达标就加道具，不改目标。
- 大小：L（MCP 批量摆放；不要写一次性 builder）。

**S6 重试（设计写了 unlimited，但可后置）**
- 目标：结果面板“再试一次”完整重置球、计时、道具。
- 文件：`Runtime/BallMotor/KatamariBallMotor.cs` 增加重置接缝（清本地球行/commit/claim/ring、`TeleportTo(start)`、重进 Playing）；能抽成纯规则的放 `GrowthLedgerMath` 并加 EditMode 测试；`StoryRoundController` 调它。
- 验证：失败→重试→球回 0.30 m、计时归零、道具全回来、计数从 0 开始。
- 大小：M（唯一改 motor 的一步）。

**S7 真图（后续，不阻塞第一关）**
- 前置：motor 的 8/8 容量不够（扩容或加空间分块宿主）；灰盒补 Camera/SDK/道具元数据；台阶/坡道有解；`maxItemSizeWorldM` 相对化（(h)）。
- 大小：XL，不在本计划内。

## 8. 风险与未验证（按影响排序）

1. **坐姿桌面轴**：`InputMove*` 在 Station 内是否继续发给 Udon 未验证（`Docs/Evidence/StationPlatform/axis_logging.md:3`、`:110-112`）。若不来，S2“坐着开”不成立。先做一次 5 分钟 Build & Test 探针（入座后打印轴），再决定回退方案。
2. **坐姿镜头高度/遮挡**：Station 把 avatar 根放到站点，玩家视角还要叠 avatar 眼高；球小、站点在后上方时可能看天或看空。只能在 Build & Test 里调 `RideRoot`/Station 偏移，ClientSim 不算数。
3. **内容量**：1.25 m 要 245.9 单位，切片要 ~400 单位才有余量；约 100 件摆放是体力活，且 `valueUnits` 必须整数，填错只以 `noGain` 出现（`Docs/OPEN_DECISIONS.md:57`）。
4. **吸附位姿**：事件位置是提交时快照，`AttachmentMath` 用视觉滚动四元数；快滚时可能看着偏。用道具当前 Transform 可缓解；`SetParent`/局部写入在 Udon 的可用性未编译验证。
5. **运动层边界**：8 平面/8 盒、无台阶/坡道/多接触（`Docs/02_TECHNICAL_CONTRACTS.md:54`）；第一关必须平地且障碍 ≤8。
6. **构建路线**：只能用户手点 Build & Test（`tasks.json:2158`）；任何“已验证 Build”都要等用户那一轮，本文不预支。

## 9. 验证命令与本次结果

```bash
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests
python3 Docs/Design/GameModes/validate_levels.py
```

2026-11-14 基线：前两条 PASS（`61 tasks`；`270 tests OK`），第三条 `OK`（错误 0、警告 0）。Unity 侧编译、Play Mode、Build & Test 均 **NOT_RUN**（本 agent 不是 Unity 操作者）。
