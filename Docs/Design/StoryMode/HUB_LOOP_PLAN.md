# 主房间与关卡的闭环：选关 → 开始 → 关卡 → 回房看结果

> 状态：`PROVISIONAL_NOT_TESTED`。§1–§3、§9 是平台查证与设计；§12 记录 `Runtime/Hub/` 的实际代码，§4、§5、§6、§8 按该代码和“评分已拆出”的现状写。Unity 侧 2026-11-16 ClientSim 走通开局与自动入座（见 [HUB_ROOM](<HUB_ROOM.md>) §8）；回房、结果卡、再来一局尚无 Play 证据。
> 现行口径（选关、装配、进关落点）见 [HUB_ROOM](<HUB_ROOM.md>) §12；本文保留平台约束与传送/回房时序的查证。
> 范围：把现在“进场景就在关卡里、结算后结果卡还钉在关卡里”的形态，改成“大王房间选关 → 开始 → 关卡 → 回房在面板上看结果 → 再选关”。
> 非目标：第二关之后的内容、联机、国王美术/动画、存档与跨世界数据。
> 本文只定义起点与卡面位置（[第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §7「重试」与 StoryMode [README](<README.md>)「开始流程」「结果卡显隐」两节在这两点上以本文为准）；回合规则、冻结顺序、评语、闸门分工见 README，本文不重写。

## 0. 先给结论

1. **运行时不能换场景，也不能用脚本把玩家送到另一个世界**（§1）。所以“主房间 → 关卡”只能是同一场景内的位移/启用，方案必须按这个约束写。
2. **选 A**：一个世界场景里同时存在“房间岛”和“关卡岛”，玩家在房间面板前按开始，被传送到关卡岛；结算后传送回房间，房间面板显示结果（§2、§3）。
3. **只有一套回合状态机**：`StoryRoundController` 的 `Idle / Running / Presenting / Result` 一个都不加。房间不是状态，只是 `Idle` 与 `Presenting/Result` 时玩家恰好所在的地方；“选关”是房间里的界面，不是第五个状态（§4）。
4. **一局的结果仍然归 `StoryRoundController`**，在 `SettleRound()` 里冻结成字段（`GetResultDiameterM()` / `GetResultCount()` 等只读 getter）。房间面板是这份数据的**显示目标**，不是第二份数据；面板绝不能去读 motor/宿主“现在”的值（§6）。**评分已拆出**：星级与评语在可选组件 `StoryResultPresenter`，控制器没有 `GetResultStars()`。
5. **场景只有一张**：正式场景 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity`，房间直接加在它里面。不另建第二张、不复制关卡（§7）。
6. **第一步不是房间美术，是传送探针**：两个空 Transform + 一个按钮，先回答“ClientSim/真机里 `TeleportTo` 到底动不动”（§9）。探针脚本是 `Labs/HubLoop/PlayerTeleportProbe.cs`；`Runtime/Hub/` 的协调代码见 §12。

## 1. 技术前提：运行时能不能换场景、能不能脚本送玩家去别的世界

### 1.1 查了什么

| 来源 | 条目 | 查到什么 |
|---|---|---|
| 本机知识库 `vrchat-agent-creator-library` | `_always-load.md`、`entities/world/scene-components/vrc-scenedescriptor.md`、`vrc-portalmarker.md`、`entities/api/player-api.md`、`entities/api/exposed-types.md`、`entities/api/not-exposed.md` | 场景描述组件把“一个 Unity 场景”当作一个世界的单位；跨世界只有 Portal；玩家侧只有 `TeleportTo`（同世界位移）；暴露清单里没有 SceneManagement |
| VRChat 官方 Creator Docs（2026-11-15 抓取） | [`creating-your-first-world`](https://creators.vrchat.com/worlds/creating-your-first-world/)、[`vrc_scenedescriptor`](https://creators.vrchat.com/worlds/components/vrc_scenedescriptor/)、[`using-build-test`](https://creators.vrchat.com/worlds/udon/using-build-test/)、[`vrc_portalmarker`](https://creators.vrchat.com/worlds/components/vrc_portalmarker/) | 建世界的第 1 步就是“一个场景 + 一个 VRC Scene Descriptor”；Build & Test 的原文是 “make a build of the scene”；Portal 是“走入 → 由客户端跳转到目标 World” |
| UdonSharp 官方 wiki | `Frequently-Asked-Questions.md`、`Class-Exposure-Tree.md`（`vrchat-community/UdonSharp.wiki`） | “If Udon supports it, then so does UdonSharp. Check Class Exposure Tree”；暴露树是编辑器窗口 `Window > Udon Sharp > Class Exposure Tree` |
| VRChat Canny | [`World child instances (multiple scenes)`](https://vrchat.canny.io/udon/p/world-child-instances-multiple-scenes) | 2024-09-26 提出，2026-01/02 合并了同名“sub-instance”请求，**至今仍是功能请求**；请求正文就是“大厅与关卡分成两个 scene / 两个实例” |

### 1.2 结论：不可行（按不可行处理）

**没有查到一句官方正面表述“世界不能在运行时换场景”。** 按任务要求，下面三条是“不可行”的依据，结论仍按不可行处理：

1. **世界 = 一个 Unity 场景。** 官方对 `VRC Scene Descriptor` 的描述是 “Required for every Unity scene you'd like to use as a VRChat world”，官方入门把“新建/打开一个场景 → 加 descriptor → Build”当作建世界的全部流程，Build & Test 页写的是 “make a build of the scene”。SDK 打包的单位就是当前打开的那一个场景，**构建产物里没有第二个场景可加载**。
2. **Udon 的暴露面里没有场景管理 API。** UdonSharp README/FAQ 把“能不能用某个 API”一律指向 Class Exposure Tree；本机知识库对它的解析（`entities/api/exposed-types.md`，来源标注 SDK 3.10.3 的 class-exposure-tree）里，`UnityEngine.SceneManagement`、`SceneManager`、`Application.LoadLevel` 一个都没有；`entities/api/not-exposed.md` 也没把它们列进“被否掉”的清单——它们根本不在树上。这是**“没有”的弱证据**，不是官方声明，所以只作为旁证。
3. **跨世界只有 Portal，而且不是脚本能按的按钮。** 官方 `VRC_PortalMarker` 页面：传送门指向另一个 World，**玩家走入**才触发，跳转由 VRChat 客户端处理；World ID 是 Inspector 里的字段，知识库注“运行时改 World ID 可能不被支持”（社区注释，中置信）；官方不支持用 REST API 自动化跳转（知识库标 FACT）。Canny 上“把大厅和关卡拆成两个 scene/实例”的请求仍未被实现，正好说明平台今天没有这个能力。

### 1.3 后果（直接决定方案形态）

- 房间与关卡必须在**同一个场景**里同时存在；切换只能是位移（传送）或整块启用/禁用。
- 一局的结束**不能靠“回到主世界重新加载”来清空**：清空、复位、冻结都必须在同一台 Udon VM 里显式做。
- 想用 Portal 做 B，就得把这一局的结果带过世界边界：VRChat 没有跨世界的“这一局的数据”通道（Persistence 是每世界每玩家的存储，不是世界间消息），回来又是另一个 instance/世界，房间状态全丢。

## 2. A 与 B

### A（选它）：一个场景，两块地方

大王房间与关卡区域在同一个场景里，中间隔着足够远、互相看不见的距离。房间面板上的“开始”做三件事：把本地玩家传送到关卡入口、**量过确认人真的到了**、再开回合（Idle → `BeginRound()`；Result → `RequestRetry()`，它是“复位 + 开局”那一份现成实现）。HUD 显隐仍归控制器。结算后由协调组件先把人从座位上弄下来、再传送回房间，房间里的同一块面板与结果卡显示结果。这三件事的实际入口是 `Runtime/Hub/HubLevelLauncher`，不是控制器（§12）。

代价（照实写）：

- 一个场景同时装两块内容：内存、渲染、音频都在同一个场景里；“离开关卡”不等于卸载关卡。
- 关卡在玩家回房后仍然活着（Udon `Update` 照跑），所以**必须保证 `Running` 时玩家一定在关卡里**，不能靠“看不见”。落地方式（§12）：开局前测距确认到达才开回合；`Running` 中玩家被量到在关卡岛外（例如菜单 respawn 回房）就拉回入口，最多 3 次；`Running` 中的“回房”一律拒绝，因为控制器没有公开的“结束本局”接口。
- 每加一关，场景更大（一张场景装 N 个岛），没有场景流式加载可用。
- 传送是瞬移：VR 舒适度风险，且没有淡入淡出（见 §10）。

### B（否决）：房间与关卡是两个世界，用 Portal 衔接

否决理由都是可查的：

| 问题 | 依据 |
|---|---|
| Portal 只能由玩家**走入**触发，按钮按不了 | 官方 `VRC_PortalMarker`：走入 Collider → 客户端跳转；“走入 vs 飞行”适用于所有移动方式，但没有“脚本触发跳转”的入口 |
| 目标 World ID 必须在 Inspector 里预填 | 同一页；知识库注“运行时修改可能不被支持”。等于一关一个 Portal、且每关都要先上传 |
| 官方不支持自动化跳转 | 知识库 FACT（REST API 建 instance + 跳转不官方支持，合规建议是用 `VRC_PortalMarker`） |
| 结果带不过去 | 没有跨世界数据通道；Persistence 是每世界每玩家。回房后 `StoryRoundController` 是新场景的新实例，`Idle` |
| 上传/审核/Quest 成本翻倍 | 两个世界 = 两套构建产物、两次上传、两套内容审核；Canny 请求里 Quest 内存正是需求来源之一 |
| 回房不是“回到原来的房间” | Portal 进入的是目标世界的（可能是新的）instance，房间里的人/状态与你离开时不同 |

结论：B 在这个项目里不是“以后再优化”，是**平台没有这个能力**。

## 3. 空间布局（全部 PROVISIONAL）

| 项 | 暂定值 | 理由 / 备注 |
|---|---|---|
| 房间岛位置 | `(1200, 0, 0)` 附近 | 放在默认远裁剪之外（Unity 默认约 1000 m），房间与关卡互相看不见、听不见；传送距离不影响精度 |
| 房间尺寸 | 12 × 12 m，层高 4 m | 一块面板 + 出生点 + 走两步就够；灰盒 |
| 关卡岛位置 | **不动**，仍在原点 | 球、110 件道具、`startCenterWorld (0, 0.15, 0)` 都在原点；动房间比动关卡便宜 |
| 出生点（`VRCSceneDescriptor.spawns`） | 房间中央，面朝面板，距面板 4 m | `spawns` 指向房间出生点，不指向关卡里的出生点 |
| 关卡入口 `LevelEntry` | 球的起点旁 2 m，面朝球 | 玩家一落地就看得到球、一步就能点到；比“传送到球上”稳（不依赖 Station 自动承载） |
| 面板位置 | 房间内墙，中心高 1.5 m，宽 2.4 m | 站立/坐姿都能读；PROVISIONAL |
| 面板朝向 | 面向房间中央 | 出生点正对 |
| 关卡 HUD | 沿用现有三行 Text（目标/当前/剩余） | 场景里唯一的 Canvas 是 `m_RenderMode: 0`（Screen Space - Overlay）；它在 VR 里是否合规未验证，见 §10 |
| 结果区 | 房间面板上的卡面（底板 + 结果文本 + “再来一次”按钮） | 复用现有三个对象：`resultCardRoot` / `resultText` 现在是 `StoryResultPresenter` 的字段，`retryButtonRoot` 仍在控制器上；把这三个对象搬到房间即可（§8） |
| 音效 | 关卡 BGM 只放在关卡岛、用空间化 + 有限半径；房间自己的环境声独立 | `VRCSceneDescriptor` 的 `autoSpatializeAudioSources` 现在是 0；不处理会出现“在房间里听关卡的声音” |

### 3.1 按钮形态：先世界内 `Interact`，不要先上 uGUI

- 现在这张场景**没有 `EventSystem`**，HUD Canvas 上也没有 Button（场景里那个 `RetryButton` 对象只是个 Text 标签）；世界内的 `StoryRetryButton` 对象目前只有 Transform + proxy + UdonBehaviour，**没有 Collider**。也就是说两条按钮路径今天都按不动。uGUI 要能按得补 EventSystem + 世界空间 Canvas 的射线（本仓没有先例）；世界内按钮要能按得给它一个 Collider。
- 世界内实体按钮的链路是有先例的：`BallRideExitButton` 与 T04 的退出按钮同形（物体 + Collider + `Interact()`），桌面是准星对着它按**鼠标左键**。
- 建议：第一步的“开始”和“再来一次”都用**世界内实体按钮 + `Interact`**（一块板 + 一个 Collider + 一个行为），面板只负责显示文字。等要做多关卡滚动列表时再评估 uGUI（PROVISIONAL）。

## 4. 状态机：一套，位置随状态走

`StoryRoundController.state` 是唯一的状态机（`Runtime/Story/StoryRoundController.cs`）。**不新增状态、不新增并行状态机**；“选关”不是状态，是 `Idle` 与 `Result` 下面板显示的内容。`SettleRound()` **不退座、不传送**——那两步归 hub。（下表行号取自提交 `ac2e880`「评分拆出」之后的 `StoryRoundController.cs`，之后的编辑会让行号漂移，以函数名为准。）

| 状态 | 玩家在哪 | 谁推进它 | 回合闸门 `SetRoundGate` | 骑乘闸门 `SetRideGate` | 收集 `SetCollecting` | 房间面板 | 关卡 HUD | 结果数据 |
|---|---|---|---|---|---|---|---|---|
| `Idle`（`EnterIdle()`，`:278`） | **房间** | 只有 `Start()`（`startOnLoad=false`）；第一局之后不再回到这里 | 关 | 关（站的人不能开球；`BallRideStation.Start` 关自己的门） | 关 | 关卡信息 + “开始” | 不显示（或显示目标） | 无 |
| `Running`（`BeginRound()`，`:364`） | **关卡**（到达确认之后才开回合） | 房间“开始” → `HubLevelLauncher.PressStart()` → 到达校验 → `BeginRound()` | 开 | 入座开、离座关 | 开 | 隐藏 | 显示目标/当前/剩余 | 还在累计（不读） |
| `Presenting`（`SettleRound()`，`:671`） | **房间**（hub 在 0–1 帧后退座 + 传送） | 倒计时归零，传送由 hub 做 | 关 | 关（hub 先 `ExitStation`，等座位报空再移动） | 关 | 卡面 + “呈给国王”一行 | 隐藏 | **已冻结** |
| `Result`（`Update()`，`:400`） | **房间** | `presentingSeconds` 到点 | 关 | 关 | 关 | 卡面（`StoryResultPresenter`：星/直径或件数/台词）+ “再来一次” | 隐藏 | 只读冻结值 |

三处要写死：

1. **`Idle` 只发生在房间。** 有房间时“玩家站在关卡里、还没坐上球”这一刻不存在——按“开始”会先传送、量到人真的在入口，然后才 `BeginRound()`。开始与传送**不是同一次调用**，因为“传送成功没”必须先量过才敢开时钟（§12）。
2. **`Presenting` 与 `Result` 只发生在房间。** “呈给国王”本来就是回房那一拍；`resultCardRoot` 因此搬到房间（§8），而不是在关卡里显示完再消失。控制器只负责进入 `Presenting`，**回房那一步是 hub 的**：它轮询到状态变化后先退座、再传送。
3. **选关不是状态。** 一个关卡时面板只有一行字 + 一个开始按钮；多关卡时是同一个面板上的多行，行选谁由按钮自己带 `levelIndex`，`state` 不变。

与 `Docs/01_GAME_DESIGN.md` §6 的 `Lobby → Countdown → … → Lobby` 的关系：那是**多人竞速局**的大厅状态机，今天没有实现；剧情模式不接它、也不复制它。将来房间若同时承载竞速大厅，房间本身仍然不是状态机——两套玩法各自拥有自己的回合状态，“一份逻辑一个实现”说的是同一个玩法里不能有两份。

## 5. 一局的完整时序

下面按 `Runtime/Hub/` 的实现写（本节即 §12 那几个文件的行为）。

1. 进世界 → 出生在房间 → `StoryRoundController.Start()` → `EnterIdle()`：两把闸门关、收集关；`HubLevelLauncher.Start()` 打一行 `[hub] ready levels=… state=…`，房间状态行（`statusText`）显示“按开始进入：<关卡名>”。
2. 玩家对准“开始”按鼠标左键 → `HubStartButton.Interact()` → `HubLevelLauncher.PressStart(levelIndex)`。
3. `PressStart` → `TryStartLevel`：`HubLoopMath.TryDecideStart` 判定——`state` 必须是 `Idle`/`Result`、没有传送在飞、过了冷却、`levelIndex` 有入口数组里的一项；然后算 `entry.position + arrivalLiftMeters`（默认 0.1 m，与探针同约定）。
   - 玩家已经在入口的容差内（默认 1 m）→ 跳过传送，当帧开回合。
   - 否则 `TeleportTo(lifted, entry.rotation, AlignPlayerWithSpawnPoint, false)`——**全项目唯一的玩家传送点**，一行 `[hub] teleport cause=enter-level levelIndex=… name=… mode=… state=…`，记 `startPending`。
4. 之后每帧（`arrivalCheckFrames` 默认 2 帧起）读 `GetPosition()` 与入口比对：
   - **到了** → 按 `HubLoopMath.TryStartMode` 调**控制器已有的公开方法**开回合：`Idle` → `BeginRound()`；`Result` → `RequestRetry()`（复位球到捕获位姿 + `ResetField()` + 开局，一份现成实现，hub 不复制）。打 `[hub] round started cause=arrived … distanceFromEntry=… framesWaited=…`。
   - `arrivalTimeoutFrames`（默认 30 帧 ≈ 0.5 s）还没到 → `[hub] start refused reason=10 (the player did not reach the level entry) …`，**不开回合**，`state` 留在 `Idle`/`Result`，玩家可以再按。
   - 期间 `state` 自己变了（例如有人坐下触发 `NotifyRiderSeated`）→ `[hub] start refused reason=11`。
5. 玩家出现在关卡入口，球在 2 m 外。**计时从第 4 步开回合那一刻起**（比点按钮晚 1–2 帧，见 §10）。
6. 玩家点球 → `BallRideStation.Interact()` → `station.UseStation(localPlayer)` → `OnStationEntered` 开骑乘闸门 + `NotifyRiderSeated()`（`Running` 里被忽略，`:339`），不会重开计时。这一段没有改动。
7. 滚到时间归零 → 控制器 `SettleRound()`（`:671`）：关回合闸门 → 关收集 → `motor.ClearCandidates()` → `MeasureSettledRound()` 冻结事实 → `state = Presenting` → `ApplyStateVisibility()`。**控制器不退座、不传送**，它只把状态推到 `Presenting`。
8. hub 的 `Update` 在 0–1 帧后看到 `Presenting`，打 `[hub] round state=2 …` 并 `BeginReturn("round-settled")`：
   1. 若在座位上（`BallRideStation.IsLocalPlayerSeated()`）→ `rideStation.ExitStation(player)`，打 `[hub] return exit-station … seated=true seatKnown=true`；
   2. 等座位报空（最多 `exitStationTimeoutFrames` 默认 10 帧），**确认后再空出一帧**才移动——三个分支各有日志：`left the station after N frames` / `still seated N frames after ExitStation; moving anyway (unverified…)` / `station exit requested, seat state unknown; moving after N frames`；
   3. `TeleportTo(roomSpawn.position + lift, roomSpawn.rotation, …)`，`[hub] teleport cause=return-to-room …`；
   4. 2 帧后测距：`[hub] return arrived frames=… distance=…`；没到则 `[hub] return teleport did not reach the room point distance=… arrived=false; …`（**不自动重试**，见下面的退化表）。
9. `presentingSeconds`（默认 3 s）后控制器把自己推到 `Result`（`:400`–`:405`），卡面由 `StoryResultPresenter` 写（星/测量值/评语）；hub 的房间状态行只写“上一局已结算：<关卡>，按开始再来一局”，**不重复测量值与星级**（§6.1）。（兜底：若 `presentingSeconds` 被设成 0，hub 两帧之间会错过 `Presenting`；这时进入 `Result` 若人还不在房间，hub 补一次回房，`cause=result-outside-room`，人已在房间则什么都不做、也不打日志。）
10. 玩家按“开始”/“再来一次” → 回到第 2 步，`Result` 分支走 `RequestRetry()`。

**顺序上的硬约束**：

- **先 `ExitStation` 再 `TeleportTo`**：坐着被传送，Station 可能每帧把人拉回座位，表现为“传送没生效”或“人在房间里坐着”。现在由 hub 做，并且等 exit 回调落地、再空一帧才移动——因为“离座后同一帧能不能传送”仍未验证（§10）。
- **先冻结再传送**：传送后球还在关卡里、还能被别的途径碰；结算值必须是铃响那一刻的快照。冻结在 `SettleRound()` 里完成，hub 之后才移动，这条自然成立。
- **先关闸门再读值**：控制器内部顺序没变。

**失败时的退化**（每条都有 `[hub]` 日志，不静默）：

| 情况 | 现在的行为 |
|---|---|
| 入场传送不生效 | 到达校验超时 → `reason=10` + 一行 warning，**回合不开**（不让计时白跑一局），人还在房间，可以再按。 |
| 回房传送不生效 | `[hub] return teleport did not reach the room point …`；玩家按“回房”按钮再试，或从菜单 respawn——世界出生点就是房间，冻结值仍在控制器里，`Result` 照常显示。 |
| `Running` 中 respawn / 掉出世界 | hub 每帧量“是否还在关卡岛内”（半径默认 200 m），不在就拉回关卡入口，最多 3 次，之后放弃并 warning（理由见 §2 的那条不变式）。 |
| `Running` 中按“回房” | **拒绝**，`reason=5 (a round is still running)`。控制器没有公开的“结束本局”接口，硬传送会把运行中的回合留在没有玩家的关卡里；时钟总会结束回合，`Presenting`/`Result` 里同一颗按钮就能用。 |
| 玩家中途离座 | 沿用现有决定（计时继续、球不走），`Running` 里人仍在关卡，不涉及传送。 |
| `Result` 下开局被控制器拒绝（复位失败） | `reason=12`，人已经被送到关卡入口，hub 顺手把人送回房间（`cause=start-rolled-back`），避免“回合在 Result、人却在关卡”。 |

## 6. 结果数据的归属与“交接”

### 6.1 归谁

`StoryRoundController` 现在只冻结**测量事实**：`resultDiameterM`、`resultCount`，在 `SettleRound()` 里写一次（`:671`；计算在 `MeasureSettledRound()`，`:701`），之后**只读**，通过 `GetResultDiameterM()` / `GetResultCount()` 读出。星级、`partial_band` 与评语在可选组件 `StoryResultPresenter` 的私有字段里（它读控制器的 getter，控制器不认识它）。**控制器没有 `GetResultStars()`**。

房间一侧的读法因此分两种：

- **只读控制器的事实**（状态、目标、冻结的直径/件数、关卡 id、次数）：`HubLevelLauncher` 与 `StoryResultPresenter` 都这么做。**不存在“数据交接”这一步**，因为房间与关卡在同一个场景、同一个控制器实例里。
- **星级与评语**：只有 `StoryResultPresenter` 有，而且它**没有公开 getter**（字段私有，只写自己的卡面 Text）。所以 hub 的房间状态行不显示星级、也不重复测量数值——同一事实印两遍就是本节禁止的第二份数据。要显示星级，就让 presenter 的卡面显示（卡面对象搬到房间即可）；要把它显示在别处，需要 presenter 增加只读 getter，那是 `Runtime/Story/` 的改动（§12.4 的缺失接口）。

### 6.2 为什么不能“回房再读”

| 会被误用的源 | 回房后读到什么 | 后果 |
|---|---|---|
| `motor.GetCollisionRadiusWorldM() * 2` | 复位后的 0.30 m（或复位前球被继续推动的值） | 面板显示“直径 0.30 m”，星级却是 2 星 |
| `pickupHost.GetAttachedCountForKind()` | `ResetField()` 之后的 0 | 件数关显示 0 件 |
| 关卡 HUD 的 Text | 已被下一次 `BeginRound()` 覆盖 | 面板显示上一局之前的内容 |
| 事件环 / 账本 | 回合已复位 | 无意义 |

规则一句话：**结算是快照，房间只读快照**。这也是“不要设计两套并行状态机”的实际含义——第二套状态机会自带第二份“当前结果”。

### 6.3 控制器的存活条件

- 控制器必须挂在**房间与关卡都不包含的根对象**上，且任何代码都不能对它 `SetActive(false)`。既有 README 已经警告过“不要把 `resultCardRoot` 拖成控制器自己的 GameObject”；现在多一条：房间/关卡可以整体隐藏，**控制器不行**——它一停，`Presenting → Result` 的那次 `Update` 就永远不会发生。
- 不要销毁/重建它（`Instantiate`/`Destroy`），否则 `state` 与冻结值一起没了。

### 6.4 单人与多人

| 项 | 今天（单人） | 多人时会发生什么 |
|---|---|---|
| 控制器实例 | 每个客户端一个，`BehaviourSyncMode.None` | 每人各自一套 `state`；A 在房间里，B 可能在关卡里 |
| 传送 | `localPlayer.TeleportTo`，只动本地 | 只动自己：A 按开始只把 A 送进关卡；B 仍在房间 |
| 结果 | 本地冻结、本地显示 | 各显示各的，不是“这一局”的共同结果 |
| 场景几何 | 房间/关卡共享 | 两个球在同一块关卡里跑：看得见彼此，但球与球之间没有碰撞（球没有 Collider），会直接叠在一起 |
| 后加入者 | 出生在房间 → `Idle` | 后加入者也能按开始，等于**开了第二局**，与正在跑的那局无关 |

结论：**现阶段明确只支持单人**。要多人，至少要有：master 拥有的同步回合状态与同步结果字段、按玩家分配的关卡实例（同一关卡的多个岛或按玩家分层的碰撞）、以及“有人正在跑时不接受第二个人开始”的门。这些都不在本文范围。

## 7. 场景归属：一张正式场景

正式场景是 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity`，房间岛与关卡岛都在它里面。不并行维护第二张关卡场景：复制关卡会立刻出现两张各改一半的场景（这正是 `FIRST_PLAYABLE_PLAN.md` §5 与 `AGENTS.md` 反复在防的账）；正式世界也不放在 `Labs/` 里（`Labs/README.md`：实验场景不是产品场景）。

场景的约束：

1. 场景里的关卡注册宿主是 `Runtime/Story/LevelArenaHost.cs`（注册地面平面 + 障碍），正式场景不挂叫 Lab 的脚本。
2. `VRCSceneDescriptor.ReferenceCamera` 必须接上；`spawns` 指向房间出生点（`HubSpawn`）。
3. 文档引用关卡场景时写 `Worlds/StoryMode/StoryMode.unity`。

## 8. 与现有「再试一次」「结果卡」的冲突与归宿

现状：`retryButtonRoot`（结果时才显示）、`resultCardRoot`（Presenting 起显示）、`resultText`（结果行，只放“呈给国王”与结算内容）、`startPromptText`（提示行，只在 Idle 显示）四个字段在关卡里；`StoryRetryButton` 的代码提供了世界内 `Interact` 与 uGUI `OnRetryPressed` 两条入口，但场景里两条都还没接通（世界内对象没有 Collider，Canvas 上没有 Button/EventSystem）；`RequestRetry()` 是“复位 + 原地重开”。

| 现有物 | 新形态里的归宿 | 建议 |
|---|---|---|
| `resultCardRoot` | 房间结果卡的底板（Presenting/Result 可见，语义不变） | **保留字段，对象搬到房间**；不改代码 |
| `resultText` | 房间卡面上的结果文本 | **保留**；它从 Presenting 起才有内容，和卡面同显同隐，放进 `resultCardRoot` 子树正好（开始提示有自己的 `startPromptText`，不占结果行） |
| `retryButtonRoot` | 房间卡面上的“再来一次”（Result 可见，语义不变） | **保留字段，对象搬到房间**；`Interact` 改挂 `HubStartButton`（`levelIndex` 指向本关）→ `HubLevelLauncher.PressStart()`，由 hub 决定要不要 `RequestRetry()`。**不要同时挂 `StoryRetryButton`**：它直连 `RequestRetry()`、不传送，两颗按钮会变成两条不同的开局路径 |
| `StoryRetryButton` | 正式形态里没有位置 | **保留为调试入口**（无房间的 lab、快速回归）；正式场景不摆这颗按钮（它不经过 hub、也不传送） |
| `RequestRetry()` | 原地重开（复位 + `BeginRound()`，不传送） | **保留，并升为 hub 在 `Result` 下的正式开局**：hub 先把人送到入口并确认到达，再调它。不需要抽 `TryResetForNextAttempt()`，也就不存在第二份复位实现——控制器不改 |
| `Start()` / `EnterIdle()` 的提示行（`startPromptText`，独立 Text，只在 Idle 显示） | 房间的“开始”提示 | 现在 `startPrompt` 默认“点击球坐上去开始”；房间场景该改成“选好关卡，按开始进入”（场景实例上改字符串即可）。提示行对象随面板搬到房间，但**必须留在卡片外面**：控制器在非 Idle 会把它整个关掉 |
| `NotifyRiderSeated()` | 无房间场景的旧起点 / 有房间场景里 `Running` 中忽略 | 保留；有房间时它是无害的 no-op |

**要不要删“再试一次”？建议不删，只降级。** 理由：无房间的 lab 与 Build & Test 的快循环需要“不传送、原地重开”这条路径；删掉会把“重开是否可用”的验证绑在传送上（传送一坏，重开一起坏）。但正式场景不要摆它——两颗“重开”按钮会让玩家分不清“回房间再开”与“原地再开”。

## 9. 实施顺序（每步独立可验收，按功能命名）

代码与场景是两条腿：`Runtime/Hub/` 的代码见 §12，场景内容由 Unity 操作者建。每步都写成“场景里建什么 → 连什么 → 看哪几行日志 → 量什么”。

### 第一步：传送探针（先回答“传送动不动”）

- 场景：在 StoryMode 里加两个空 Transform（`RoomSpawn`、`LevelEntry`，相距 1200 m）和两个世界内按钮（各带 Collider + `PlayerTeleportProbe`，`interactTargetIndex` 分别指向两个点）。
- 代码：`Labs/HubLoop/PlayerTeleportProbe.cs`。
- 验收（Play Mode）：进场景后 `localPlayer.GetPosition()` 与 `RoomSpawn` 距离 < 0.5 m；按“去关卡”后 `[hub-teleport] immediate` 与 `[hub-teleport] settled` 两行的 `distanceToArrival` 都 < 0.5、`arrived=True`；按“去房间”回来；Console 无异常。**两行读数的差**才是“传送成功但被人推走”的诊断。
- 不碰：回合、计时、球、面板。

### 第二步：房间、开始与回房（接线，不写新逻辑）

- 场景：灰盒房间（12×12×4）、面板底板 + 一行 Text（连 `statusText`）、`LevelEntry`、`RoomSpawn`、一个“开始”按钮（Collider + `HubStartButton`）、一个“回房”按钮（Collider + `HubReturnButton`，摆在关卡里、球旁边）、`VRCSceneDescriptor.spawns` 改到房间。
- 组件：`HubLevelLauncher` 挂在**房间与关卡都不包含的根对象**上并永不被 `SetActive(false)`；连 `controller`、`levelEntries[0] = LevelEntry`、`levelNames[0]`、`roomSpawn`、`rideStation`、`rideSeat`、`statusText`。
- 验收：进 Play Mode 看到 `[hub] ready levels=1 …`；按开始 → `[hub] teleport cause=enter-level …` → 1–2 帧后 `[hub] round started cause=arrived …`；人在入口、HUD 开始倒计时。**这一条只验“开始 → 在关卡里跑起来”**；坐姿驱动在 ClientSim 里本仓没有验证过，坐不上去就留给 Build & Test，不要在这一步纠结。
- 预期：`statusText` 在 Idle 显示“按开始进入：<关卡名>”。

### 第三步：回房与结果卡

- 场景：`resultCardRoot` / `resultText` / `retryButtonRoot` 三个对象搬到房间；`StoryResultPresenter` 挂自己的对象、连 `controller` / `resultCardRoot` / `resultText`；`retryButtonRoot` 上的 `StoryRetryButton` 换成 `HubStartButton`（`levelIndex = 0`）。
- 验收：把 `timeLimitSeconds` 临时改成 15 s、三档直径临时改成 0.35 / 0.40 / 0.45 m（只改场景实例上的序列化值，不改代码、不改关卡 JSON），跑出 0/1/2/3 星各一次：到点先 `[hub] round state=2 …`，坐着时依次是 `[hub] return exit-station …`、`[hub] return: left the station after N frames`、`[hub] teleport cause=return-to-room …`、`[hub] return arrived frames=… distance=…`；人必须在房间，卡面先“呈给国王”、3 s 后星 + 直径 + 评语。**坐着的那条链要单独看**：要出现 `left the station after N frames`，不是 `still seated … moving anyway`。再把值改回真实档位跑一次完整长度。
- 大小：M。

### 第四步：再来一次与“Running 时在关卡里”

- 验收（再来一次）：Result 里按“再来一次” → `[hub] teleport cause=enter-level … mode=2` → `[hub] round started cause=arrived … mode=2`（走的是 `RequestRetry()`）；球回 0.30 m、道具全回来、卡面消失、倒计时重开、目标行出现“第 N 次重试”；连按 3 次不炸（回合 id 不重复）。
- 验收（不变式）：`Running` 中从菜单 respawn → 回房 → 应看到最多 3 次 `[hub] teleport cause=hold-in-level …`，人被送回关卡入口，第 3 次后有 `hold-in-level gave up …`。再把 `holdPlayerInLevel` 关掉对照一次，确认是它拉的。
- 验收（拒绝）：`Running` 中按“回房” → `[hub] return refused reason=5 (a round is still running)`，人不动。
- 大小：S。

### 第五步：Build & Test 验收

- 验收：用户点 Build & Test 进世界；出生在房间、走进选关垫、入座、计时、到点看结果卡并回房；“Reference camera is not set”警告消失。**Build & Test 只能由用户点**（`tasks.json` 记录的构建入口问题），本步骤的验收一定是用户动作。
- 大小：M。

## 10. 未验证与风险（按影响排序）

1. **`TeleportTo` 的实际行为**：ClientSim 是否真的位移、VR 里是否生效、坐着时是否被 Station 拉回——全部未验证，传送探针就是为它准备的。知识库写了“签名 `TeleportTo(Vector3, Quaternion, …)`、只能对本地玩家调用、**Stations 可以阻止 Udon 传送玩家**”（本地库 `entities/world/udon/players/player-positions.md` §传送 API），`TeleportTo` / `UseStation` / `ExitStation` 在 SDK 3.10.3 的暴露清单里（`entities/api/exposed-types.md`）；官方页（2026-08-31 更新，2026-11-15 抓取）原文是 “Send the local player to a new position and specified rotation, **unless a Station does not allow it**” 与 “Stations can prevent Udon from teleporting players”。所以“先离座”不是猜测，是必需步骤；标为未验证的只是“离座要等多久”。
2. **`ExitStation` → `TeleportTo` 的时序**：顺序是从“坐着不能传”推出来的，没有实测。**代码按“未验证”写**：请求 `ExitStation` 后等 `IsLocalPlayerSeated()` 变 false（最多 `exitStationTimeoutFrames` = 10 帧），确认后再空出一帧才 `TeleportTo`；超时也照传，但日志改成 `still seated … moving anyway (unverified …)`。所以第一次测回房时，看日志里走的是哪条分支，就知道这条假设成不成立。
3. **`ExitStation` 与 `Disable Station Exit`**：这一条已由官方页说清，但仍未在本仓实测。官方 `vrc_station` 页（2026-10-30 更新，2026-11-15 抓取）对 Disable Station Exit 的原文是 “Prevents the player from exiting the station by moving or jumping. **Use the `ExitStation` node to remove the player from the station.**” 也就是说 `ExitStation` 就是关掉常规出口后的正规出路（知识库 `entities/world/scene-components/vrc-station.md` 也这么整理）。仍未验证的是 SDK 3.10.5 在**这个**球站上的实际时序：`ExitStation` 之后 `OnStationExited` 是当帧还是下一帧、`IsLocalPlayerSeated()` 什么时候变 false。hub 的 `exitStationTimeoutFrames` 分支与三条日志就是为回答这个准备的；兜底是球上的 `BallRideExitButton`。
4. **退座会先移动玩家一次**：官方参数表写明 “Station Exit Player Location: The player is moved to the position of this Transform when they exit the station.”，所以“退座 → 回房传送”之间人已经先被挪到出口点一次；hub 的回房测距在这之后做，不影响结果，但第一帧的观感是两次位移，验收时要知道。
5. **世界空间 uGUI 交互**：本仓没有先例（场景无 `EventSystem`）；本文建议先用世界内 `Interact` 按钮绕开它。
6. **点“开始”到能开球之间的时间算进 120 s**：传送 + 到达校验（1–2 帧）+ 找球 + 坐下大概 2–5 s。若实测超过 5 s，再考虑“开始即自动入座”（`station.UseStation(localPlayer)`）或把 `LevelEntry` 贴到座位旁；本文不预先实现自动入座（它把传送和 Station 两个未验证项绑在一起）。
7. **两块内容同场景的开销**：110 件道具 + 房间，渲染/内存/音频串扰都没有量过；距离 1200 m 只是“看不见”的暂定值，不是性能解。
8. **VR 舒适度**：瞬移没有淡入淡出；进/出各一次，频率低，但已知敏感玩家会不适。
9. **关卡 HUD 的 Canvas 形态**：现在是 Screen Space - Overlay（`m_RenderMode: 0`），VR 下是否可见/合规未验证；若不可用，关卡 HUD 要改成跟随球的世界空间 Canvas，这是独立的一条工作。
10. **后加入/联机**：见 §6.4，今天明确不支持。
11. **轮询带来的一帧延迟**：hub 和 presenter 一样靠 `GetState()` 轮询，两个 `Update` 的先后未定义，所以“回房”可能比 `SettleRound()` 晚一帧。冻结在 `SettleRound()` 内部完成，所以这一帧不会读到半份结果。
12. **所有布局、坐标、尺寸、文案都是 PROVISIONAL**，只在 Build & Test 里按玩家实际视点调。

## 11. 验证入口

- 规则类 `HubLoopMath` 的 EditMode 测试在 `Tests/EditMode/Hub/HubLoopMathTests.cs`（纯逻辑，不碰场景）。
- Unity 侧的 Play 证据与验收清单见 [HUB_ROOM](<HUB_ROOM.md>) §8、§10、§11；本文 §9 的各步验收是更早的分步版本。
- 计划数据与文档的 Python 门：`python3 Docs/tools/validate_plan.py`、`python3 -m unittest discover -s Docs/tests`。

## 12. 运行时代码现状

### 12.1 文件

| 文件 | 职责 |
|---|---|
| `Runtime/Hub/HubLevelLauncher.cs` | 唯一的协调组件：开始按钮 → 传送 + 到达校验 + 开回合；`Presenting` → 退座 + 传送回房；`Running` 中把人留在关卡；房间状态行。 |
| `Runtime/Hub/HubStartButton.cs` | 房间“开始”按钮：`Interact` → `launcher.PressStart(levelIndex)`。按钮自带 `levelIndex`，多关卡就是多一颗按钮 + 多一项 `levelEntries`。 |
| `Runtime/Hub/HubReturnButton.cs` | “回房”按钮：`Interact` → `launcher.PressReturnToRoom()`。 |
| `Runtime/Hub/HubLoopMath.cs` | 纯规则：能不能开 / 能不能回、开回合该调哪个现成方法、去重与冷却、到达容差、`Running` 期间该不该拉回。 |
| `Tests/EditMode/Hub/HubLoopMathTests.cs` | `HubLoopMath` 的 NUnit EditMode 测试（规则类不碰场景，所以能纯逻辑测）。 |

### 12.2 数据流

```
按钮 Interact
   └─ HubLevelLauncher.PressStart(index) / PressReturnToRoom()
        └─ HubLoopMath.TryDecideStart / TryDecideReturn    （规则 + reason code）
             └─ TeleportTo(dest + lift, dest.rotation, AlignPlayerWithSpawnPoint, false)
                  └─ Update 轮询：量 GetPosition() → 到了才 BeginRound() / RequestRetry()
        ↑
StoryRoundController.GetState()  （唯一状态机；hub 只读，不复制）
```

- **只读控制器的公开事实**：`GetState()` / `GetLevelId()` / `GetAttemptNumber()`；结果数值与星级一律不碰（星级在 `StoryResultPresenter`，且它没有公开 getter）。
- **只调用控制器已有的公开方法**：`BeginRound()`（Idle）与 `RequestRetry()`（Result）。hub 不改 `Runtime/Story/`。
- **“Running 时玩家必在关卡”** 由三件事一起保证：① 开局前量到达，没到就不开回合；② `Running` 中每帧量是否还在关卡岛内（默认半径 200 m），不在就拉回入口，最多 3 次；③ `Running` 中“回房”一律拒绝（`reason=5`），因为控制器没有公开的“结束本局”接口。

### 12.3 公开方法（给场景与其他行为用）

| 类型 | 成员 | 用途 |
|---|---|---|
| `HubLevelLauncher` | `PressStart(int levelIndex)` | 世界内按钮 / uGUI `SendCustomEvent` 的入口，拒绝时打一行 reason。 |
| `HubLevelLauncher` | `bool TryStartLevel(int levelIndex, out int reason)` | 同上的可判定版本（项目惯例 `Try*` + `out reason`）。 |
| `HubLevelLauncher` | `PressReturnToRoom()` / `bool TryReturnToRoom(out int reason)` | 回房。 |
| `HubStartButton` | `Interact()` / `Press()` | 世界内按下 / uGUI 字符串事件。 |
| `HubReturnButton` | `Interact()` / `Press()` | 同上。 |
| `HubLoopMath` | `TryStartMode`、`TryDecideStart`、`TryDecideReturn`、`IsCoolingDown`、`TryLiftedPoint`、`IsWithin`、`ShouldHoldInLevel`、`IsRoundState`、`DescribeReason` | 纯规则，全部有 EditMode 测试。 |

reason code（0 = 成功）：1 传送在飞、2 冷却中、3 关卡索引越界、4 状态不是 Idle/Result、5 回合仍在跑、6 状态值不可用、7 无本地玩家、8 落点非有限值、9 关卡入口槽为空、10 没到达入口、11 传送途中状态自己变了、12 回合拒绝复位重开、13 没连控制器、14 没连房间落点。

### 12.4 缺的接口（由 Lead 协调）

1. **`StoryRoundController` 没有公开的“结束/立即结算本局”**（例如 `AbortRound()` 或 `SettleNow()`）。缺它，“`Running` 中途回房”就只能拒绝（§12.2 ③）；要做成“放弃本局回房”，需要那个方法，且它必须自己保证冻结与闸门顺序。
2. **`StoryResultPresenter` 没有公开只读 getter**（星级 / 评语 / 是否在 partial band）。所以房间状态行不显示星级；要在别处显示，需要 presenter 加 getter（`Runtime/Story/` 的改动）。
3. **`BallRideStation` 没有“请求本地玩家离座”的公开方法**，只有 `IsLocalPlayerSeated()` 这个读口；hub 因此自己持一个 `VRCStation` 引用调 `ExitStation`。若 `Runtime/Riding/` 之后要收口“离座”这件事，hub 应该改调它，而不是直接摸 `VRCStation`。
4. **场景侧还没有**：房间几何、按钮 Collider、`LevelEntry` / `RoomSpawn`、`spawns`、卡面搬家、`StoryResultPresenter` 的接线。这些是 Unity 操作者的活（§9）。
