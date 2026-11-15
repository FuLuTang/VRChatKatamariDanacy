# 主房间与关卡的闭环：选关 → 开始 → 关卡 → 回房看结果

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。本文只做查证与设计，不改任何代码、场景、`tasks.json` 或既有文档；作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 Build & Test。
> 范围：把现在“进场景就在关卡里、结算后结果卡还钉在关卡里”的形态，改成“大王房间选关 → 开始 → 关卡 → 回房在面板上看结果 → 再选关”。
> 非目标：第二关之后的内容、联机、国王美术/动画、存档与跨世界数据。
> 本文取代的是 [第一个可玩集成计划](<FIRST_PLAYABLE_PLAN.md>) §7「重试」与 StoryMode [README](<README.md>)「开始流程」「结果卡显隐」两节里**关于起点与卡面位置的部分**；回合规则、冻结顺序、评语、闸门分工全部沿用，不重写。

## 0. 先给结论

1. **运行时不能换场景，也不能用脚本把玩家送到另一个世界**（§1）。所以“主房间 → 关卡”只能是同一场景内的位移/启用，方案必须按这个约束写。
2. **选 A**：一个世界场景里同时存在“房间岛”和“关卡岛”，玩家在房间面板前按开始，被传送到关卡岛；结算后传送回房间，房间面板显示结果（§2、§3）。
3. **只有一套回合状态机**：`StoryRoundController` 的 `Idle / Running / Presenting / Result` 一个都不加。房间不是状态，只是 `Idle` 与 `Presenting/Result` 时玩家恰好所在的地方；“选关”是房间里的界面，不是第五个状态（§4）。
4. **一局的结果仍然归 `StoryRoundController`**，在 `SettleRound()` 里冻结成字段。房间面板是这份数据的**显示目标**，不是第二份数据；面板绝不能去读 motor/宿主“现在”的值（§6）。
5. **场景只有一张**：把 `Labs/StoryLevel/StoryLevel.unity` 整体提升为正式场景（同一文件、同一 GUID、`.meta` 一起搬），房间直接加进它。不新建第二张、不复制关卡（§7）。
6. **第一步不是房间美术，是传送探针**：两个空 Transform + 一个按钮，先回答“ClientSim/真机里 `TeleportTo` 到底动不动”（§9）。

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

大王房间与关卡区域在同一个场景里，中间隔着足够远、互相看不见的距离。房间面板上的“开始”做三件事：把本地玩家传送到关卡入口、开回合（`BeginRound()`）、把 HUD 打开。结算时把玩家传送回房间，房间里的同一块面板显示结果。

代价（照实写）：

- 一个场景同时装两块内容：内存、渲染、音频都在同一个 bundle 里；“离开关卡”不等于卸载关卡。
- 关卡在玩家回房后仍然活着（Udon `Update` 照跑），所以**必须靠状态机保证 `Running` 时玩家一定在关卡里**，不能靠“看不见”。
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
| 出生点（`VRCSceneDescriptor.spawns`） | 房间中央，面朝面板，距面板 4 m | 现在 `StoryLevel.unity` 的 `spawns` 指向关卡里的 `Spawn`，要改到房间 |
| 关卡入口 `LevelEntry` | 球的起点旁 2 m，面朝球 | 玩家一落地就看得到球、一步就能点到；比“传送到球上”稳（不依赖 Station 自动承载） |
| 面板位置 | 房间内墙，中心高 1.5 m，宽 2.4 m | 站立/坐姿都能读；PROVISIONAL |
| 面板朝向 | 面向房间中央 | 出生点正对 |
| 关卡 HUD | 沿用现有三行 Text（目标/当前/剩余） | 场景里唯一的 Canvas 是 `m_RenderMode: 0`（Screen Space - Overlay）；它在 VR 里是否合规未验证，见 §10 |
| 结果区 | 房间面板上的卡面（底板 + 结果文本 + “再来一次”按钮） | 复用现有 `resultCardRoot` / `resultText` / `retryButtonRoot` 三个字段，对象搬到房间（§8） |
| 音效 | 关卡 BGM 只放在关卡岛、用空间化 + 有限半径；房间自己的环境声独立 | `VRCSceneDescriptor` 的 `autoSpatializeAudioSources` 现在是 0；不处理会出现“在房间里听关卡的声音” |

### 3.1 按钮形态：先世界内 `Interact`，不要先上 uGUI

- 现在这张场景**没有 `EventSystem`**，HUD Canvas 上也没有 Button（场景里那个 `RetryButton` 对象只是个 Text 标签）；世界内的 `StoryRetryButton` 对象目前只有 Transform + proxy + UdonBehaviour，**没有 Collider**。也就是说两条按钮路径今天都按不动。uGUI 要能按得补 EventSystem + 世界空间 Canvas 的射线（本仓没有先例）；世界内按钮要能按得给它一个 Collider。
- 世界内实体按钮的链路是有先例的：`BallRideExitButton` 与 T04 的退出按钮同形（物体 + Collider + `Interact()`），桌面是准星对着它按**鼠标左键**。
- 建议：第一步的“开始”和“再来一次”都用**世界内实体按钮 + `Interact`**（一块板 + 一个 Collider + 一个行为），面板只负责显示文字。等要做多关卡滚动列表时再评估 uGUI（PROVISIONAL）。

## 4. 状态机：一套，位置随状态走

`StoryRoundController.state` 是唯一的状态机（`Runtime/Story/StoryRoundController.cs`，行号按 `a6058ca` 读、该文件到 `83a5e75` 未再改动）。**不新增状态、不新增并行状态机**；“选关”不是状态，是 `Idle` 与 `Result` 下面板显示的内容。

| 状态 | 玩家在哪 | 谁推进它 | 回合闸门 `SetRoundGate` | 骑乘闸门 `SetRideGate` | 收集 `SetCollecting` | 房间面板 | 关卡 HUD | 结果数据 |
|---|---|---|---|---|---|---|---|---|
| `Idle`（`EnterIdle()`，`:278`） | **房间** | 只有 `Start()`（`startOnLoad=false`）；第一局之后不再回到这里 | 关 | 关（站的人不能开球；`BallRideStation.Start` 关自己的门） | 关 | 关卡信息 + “开始” | 不显示（或显示目标） | 无 |
| `Running`（`BeginRound()`，`:364`） | **关卡**（开始当帧传送） | 房间“开始”按钮 → `RequestStart()` | 开 | 入座开、离座关 | 开 | 隐藏 | 显示目标/当前/剩余 | 还在累计（不读） |
| `Presenting`（`SettleRound()`，`:621`） | **房间**（冻结后立刻传送） | 倒计时归零 | 关 | 关（已退座） | 关 | 卡面 + “呈给国王”一行 | 隐藏 | **已冻结** |
| `Result`（`ShowResult()`，`:703`） | **房间** | `presentingSeconds` 到点 | 关 | 关 | 关 | 卡面 + 星/直径或件数/台词 + “再来一次” | 隐藏 | 只读冻结值 |

三处要写死：

1. **`Idle` 只发生在房间。** 这是与现在最大的差别：现在 `Idle` 是“玩家站在关卡里、还没坐上球”；新形态下这一刻不存在——按“开始”的同一次调用里就 `BeginRound()` 并传送，所以 `Running` 一开始玩家就在关卡入口。
2. **`Presenting` 与 `Result` 只发生在房间。** “呈给国王”本来就是回房那一拍；`resultCardRoot` 因此搬到房间（§8），而不是在关卡里显示完再消失。
3. **选关不是状态。** 一个关卡时面板只有一行字 + 一个开始按钮；多关卡时是同一个面板上的多行，行选谁由按钮自己带 `levelIndex`，`state` 不变。

与 `Docs/01_GAME_DESIGN.md` §6 的 `Lobby → Countdown → … → Lobby` 的关系：那是**多人竞速局**的大厅状态机，今天没有实现；剧情模式不接它、也不复制它。将来房间若同时承载竞速大厅，房间本身仍然不是状态机——两套玩法各自拥有自己的回合状态，“一份逻辑一个实现”说的是同一个玩法里不能有两份。

## 5. 一局的完整时序

1. 进世界 → 出生在房间 → `StoryRoundController.Start()` → `EnterIdle()`：两把闸门关、收集关、面板显示“故事 1 / 目标 1.25 m / 开始”。
2. 玩家对准“开始”按鼠标左键 → `StoryRoomButton.Interact()` → `controller.RequestStart()`。
3. `RequestStart()`：`state` 只能是 `Idle`（首次）或 `Result`（再来一次）；`Running`/`Presenting` 一律拒绝并打一行日志（与 `RequestRetry()` 的状态闸门同形）。
   - `Result` 时先复位：`motor.TryResetForNewRound(...)` → `pickupHost.ResetField()`（与 `RequestRetry()` 同一份复位实现，§8）。
   - `localPlayer.TeleportTo(levelEntry.position, levelEntry.rotation)`。
   - `BeginRound()`：`Time.time` 记起点、`SetRoundGate(true)`、`SetCollecting(true)`、HUD 打开。
4. 玩家出现在关卡入口，球在 2 m 外。**计时已经开始**（这是“点击开始”的直接后果，见 §10 的风险 4）。
5. 玩家点球 → `BallRideStation.Interact()` → `station.UseStation(localPlayer)` → `OnStationEntered` 开骑乘闸门。`NotifyRiderSeated()` 在 `Running` 被忽略（`:340`），所以不会重开计时。
6. 滚到时间归零 → `SettleRound()`（`:621`）：
   1. 关回合闸门 → 关收集 → `motor.ClearCandidates()`（顺序不变）；
   2. `EvaluateSettledRound()` 冻结星/直径/件数；
   3. `station.ExitStation(localPlayer)`（**先退座**）；
   4. `localPlayer.TeleportTo(roomSpawn.position, roomSpawn.rotation)`；
   5. `state = Presenting`，`presentingStartTimeSeconds = Time.time`，卡面显示 `presenterLine`。
7. `presentingSeconds`（默认 3 s）后 → `ShowResult()` → `Result` → 卡面显示星/直径或件数/评语 + “再来一次”。
8. 玩家按“再来一次” → 回到第 2 步的 `RequestStart()`（`state=Result` 分支先复位再传送）。

**顺序上的硬约束**（写错就出怪现象）：

- **先 `ExitStation` 再 `TeleportTo`**：坐着被传送，Station 可能每帧把人拉回座位，表现为“传送没生效”或“人在房间里坐着”。
- **先冻结再传送**：传送后球还在关卡里、还能被别的途径碰；结算值必须是铃响那一刻的快照，不能等回房再读。
- **先关闸门再读值**：沿用现有顺序，否则最后一帧的候选会算进去。

**失败时的退化**（“传送探针”与“回房与结果面板”两步里各试一次）：

- `TeleportTo` 不生效（ClientSim 或 VR 的某个组合）：玩家还在房间，但 `Running` 已经开始 → 计时白跑一局，到点仍能回房出结果。可接受，但要在 Console 里有一条自己的日志（`[story] enter-level teleport did not move the player`），不能静默。
- 回房传送不生效：玩家留在关卡，卡面在房间 → 看不见结果。兜底不是“再摆一份卡面”，而是 **VRChat 的 Respawn**：世界出生点就是房间，玩家从菜单 respawn 一次就回房，冻结值仍在控制器里，`Result` 照常显示。第一次验收时要试这一条。
- 玩家在关卡里 respawn（掉出世界/主动 respawn）：回到房间，回合**继续跑**（`Running`），到点 `SettleRound()` 的传送是 no-op（人已在房间），结果正常。这条不需要额外代码，但要知道它会这样。
- 玩家中途离座：沿用现有决定（计时继续、球不走），`Running` 里人仍在关卡，不涉及传送。

## 6. 结果数据的归属与“交接”

### 6.1 归谁

`StoryRoundController` 的四个私有字段：`resultStars`、`resultInsidePartialBand`、`resultDiameterM`、`resultCount`，在 `SettleRound()` 里写一次，之后**只读**（`:621`、`:644`）。房间面板通过控制器已有的 getter（`GetResultStars()`、`GetResultDiameterM()`、`GetResultCount()`、`GetState()`）或直接由控制器写 Text 来显示——**不存在“数据交接”这一步**，因为房间与关卡在同一个场景、同一个控制器实例里。

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

## 7. 场景归属：把 Lab 整体提升，不新建第二张

**推荐：把 `Labs/StoryLevel/StoryLevel.unity` 整体搬成正式场景，房间加进它。** 不推荐“新建一张正式场景再把内容复制过去”。

| 做法 | 结果 |
|---|---|
| 推荐：同一文件搬家（`Labs/StoryLevel/StoryLevel.unity` → 例如 `Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity`，`.meta` 一起搬，GUID 不变） | 一次操作、零分叉；场景内的引用（`PipelineManager`、`SceneDescriptor`、所有 Udon 实例）全部不受影响 |
| 不推荐：新建正式场景 + 把 lab 的东西复制过去 | 立刻有两张各改一半的关卡场景；这正是 `FIRST_PLAYABLE_PLAN.md` §5 与 `AGENTS.md` 反复在防的账 |
| 不推荐：一直留在 `Labs/` | 正式世界长在实验目录里，`Labs/README.md` 的“实验场景不是产品场景”就作废了；而且第一次 Build & Test 之后就不该再叫 lab |

配套的三件事（Unity 操作者做，本文不做）：

1. **`StoryLevelLabBootstrap.cs` 转成生产宿主**：只保留“注册地面平面 + 障碍盒 + `TryBeginPlaying`”，删掉 `scriptedDrive`、`Report()`、`GetStartDiameter()` 这些实验件；文件随场景一起搬出 `Labs/`（例如 `Runtime/Story/StoryArenaHost.cs`），`.meta` 与 `.asset` 一起搬，GUID 不变。**不要**在正式场景里继续挂一个叫 Lab 的脚本。
2. **补 SDK 组件**：`PipelineManager` 已有；`VRCSceneDescriptor.ReferenceCamera` 现在是 `{fileID: 0}`（未接），必须补；`spawns` 从关卡里的 `Spawn` 改到房间。
3. **文档引用**：搬走后 `Labs` 路径会失效，涉及 [README](<README.md>)、[LEVEL_CONTENT_PLAN](<LEVEL_CONTENT_PLAN.md>)、[FIRST_PLAYABLE_PLAN](<FIRST_PLAYABLE_PLAN.md>) 与各 `tasks.json` 条目——这些不在本 agent 的写范围，留给 Lead 一次性更新。

时机：**最晚在第一次 Build & Test 之前**。在转正之前，房间可以先在 lab 场景里用灰盒长出来（同一个文件），转正时连房间一起搬走，不需要重做。

## 8. 与现有「再试一次」「结果卡」的冲突与归宿

现状：`retryButtonRoot`（结果时才显示）、`resultCardRoot`（Presenting 起显示）、`resultText`（结果行，只放“呈给国王”与结算内容）、`startPromptText`（提示行，只在 Idle 显示）四个字段在关卡里；`StoryRetryButton` 的代码提供了世界内 `Interact` 与 uGUI `OnRetryPressed` 两条入口，但场景里两条都还没接通（世界内对象没有 Collider，Canvas 上没有 Button/EventSystem）；`RequestRetry()` 是“复位 + 原地重开”。

| 现有物 | 新形态里的归宿 | 建议 |
|---|---|---|
| `resultCardRoot` | 房间结果卡的底板（Presenting/Result 可见，语义不变） | **保留字段，对象搬到房间**；不改代码 |
| `resultText` | 房间卡面上的结果文本 | **保留**；它从 Presenting 起才有内容，和卡面同显同隐，放进 `resultCardRoot` 子树正好（旧版“别放进去”的警告来自提示复用结果行，那个耦合已经拆开） |
| `retryButtonRoot` | 房间卡面上的“再来一次”（Result 可见，语义不变） | **保留字段，对象搬到房间**；它的 `Interact`/`OnClick` 改成调 `RequestStart()`（或 `StoryRetryButton` 加一个 `StartMode`），不再调 `RequestRetry()` |
| `StoryRetryButton` | 正式形态里没有位置 | **保留为调试入口**（无房间的 lab、快速回归）；正式场景不摆这颗按钮 |
| `RequestRetry()` | 原地重开（复位 + `BeginRound()`，不传送） | **保留，降级为调试路径**；把“复位”抽成私有 `TryResetForNextAttempt()`，`RequestRetry()` 与新的 `RequestStart()` 都调它——**一份复位实现，两个入口** |
| `Start()` / `EnterIdle()` 的提示行（`startPromptText`，独立 Text，只在 Idle 显示） | 房间的“开始”提示 | 现在 `startPrompt` 默认“点击球坐上去开始”；房间场景该改成“选好关卡，按开始进入”（场景实例上改字符串即可）。提示行对象随面板搬到房间，但**必须留在卡片外面**：控制器在非 Idle 会把它整个关掉 |
| `NotifyRiderSeated()` | 无房间场景的旧起点 / 有房间场景里 `Running` 中忽略 | 保留；有房间时它是无害的 no-op |

**要不要删“再试一次”？建议不删，只降级。** 理由：无房间的 lab 与 Build & Test 的快循环需要“不传送、原地重开”这条路径；删掉会把“重开是否可用”的验证绑在传送上（传送一坏，重开一起坏）。但正式场景不要摆它——两颗“重开”按钮会让玩家分不清“回房间再开”与“原地再开”。

## 9. 实施顺序（每步独立可验收，按功能命名）

### 传送探针（先回答“传送动不动”）

- 内容：在现有 `Labs/StoryLevel` 里加两个空 Transform（`RoomSpawn`、`LevelEntry`，相距 1200 m）和一个临时行为（或直接给控制器加两个 public 方法）做 `localPlayer.TeleportTo(...)`；再摆两个世界内按钮：一个“去房间”、一个“去关卡”。
- 不碰：回合、计时、球、面板。
- 验收（Play Mode）：进场景后 `localPlayer.GetPosition()` 与 `RoomSpawn` 距离 < 0.5 m；按“去关卡”后与 `LevelEntry` 的距离 < 0.5 m；按“去房间”回来；Console 无异常。若 ClientSim 里不动，先解决这一个再往下走——后面的每一步都建在它上面。
- 大小：S。

### 房间与开始

- 内容：灰盒房间（12×12×4，灰盒地面 + 一面墙 + 面板底板）、面板三行 Text（关卡名 / 目标 / 提示，全部 PROVISIONAL 文案）、“开始”按钮；`StoryRoomButton`（`Interact` → `controller.RequestStart()`）；`RequestStart()` 做“（Result 时）复位 → 传送 → `BeginRound()`”；`spawns` 改到房间。
- 验收：进 Play Mode 在房间；面板读到“目标 1.25 m”；按开始 → 人在 `LevelEntry`、HUD 开始倒计时、Console 有 `[story] enter-level teleport` 一行。**这一条只验“开始 → 在关卡里跑起来”**；坐姿驱动（Station）在 ClientSim 里本仓没有验证过，若 Play Mode 里坐不上去，把它留给 Build & Test，不要在这一步纠结。
- 大小：M。

### 回房与结果面板

- 内容：`SettleRound()` 里按 §5 的顺序加“退座 → 传送回房”；卡面三个对象搬到房间；`ShowResult()` 改成先拼字符串再写非空目标（否则卡面 Text 一换对象就断）；`ApplyStateVisibility()`（`:738`）加上房间开始区/结果区的显隐。
- 验收：把 `timeLimitSeconds` 临时改成 15 s、三档直径临时改成 0.35 / 0.40 / 0.45 m（只改场景实例上的序列化值，不改代码、不改关卡 JSON），跑出 0/1/2/3 星各一次：到点后 `localPlayer.GetPosition()` 在房间；卡面先“呈给国王”、3 s 后显示星 + 直径 + 评语；面板数值与 `GetResultStars()`/`GetResultDiameterM()` 完全一致；`GetBankedScoreUnits() == 0`。再把值改回真实档位跑一次完整长度。**“回房后不在关卡”要单独确认**：把玩家位置与 `LevelEntry` 的距离打出来。
- 大小：M。

### 再来一次

- 内容：“再来一次”按钮改调 `RequestStart()`（Result 分支复位 + 传送 + 开局）；`RequestRetry()` 抽公共复位。
- 验收：失败 → 按再来一次 → 人在 `LevelEntry`、球回 0.30 m、道具全回来、卡面消失、倒计时重新开始、目标行出现“第 N 次重试”；连按 3 次不炸（回合 id 不重复）。
- 大小：S。

### 场景转正

- 内容：§7 的三件事（搬家 + 宿主转生产 + 补 `ReferenceCamera`/`spawns`）；删 `Labs/StoryLevel` 目录。
- 验收：用户点 Build & Test 进世界；出生在房间、按开始、坐球、计时、到点自动回房看结果；“Reference camera is not set”警告消失。**Build & Test 只能由用户点**（`tasks.json` 记录的构建入口问题），本步骤的验收一定是用户动作。
- 大小：M（场景搬家本身小，风险在“搬完第一次构建”）。

## 10. 未验证与风险（按影响排序）

1. **`TeleportTo` 的实际行为**：ClientSim 是否真的位移、VR 里是否生效、坐着时是否被 Station 拉回——全部未验证，传送探针就是为它准备的。知识库只写了“`player.TeleportTo(Vector3, Quaternion)` 暴露、只能对本地玩家调用”（`entities/api/player-api.md`）。
2. **`ExitStation` → `TeleportTo` 的顺序**：顺序是从“坐着不能传”这个机制推出来的，没有任何实测；第一次测回房时就盯“人是不是还在座位上”。
3. **世界空间 uGUI 交互**：本仓没有先例（场景无 `EventSystem`）；本文建议先用世界内 `Interact` 按钮绕开它。
4. **点“开始”到能开球之间的时间算进 120 s**：传送 + 找球 + 坐下大概 2–5 s。若实测超过 5 s，再考虑“开始即自动入座”（`station.UseStation(localPlayer)`）或把 `LevelEntry` 贴到座位旁；本文不预先实现自动入座（它把传送和 Station 两个未验证项绑在一起）。
5. **两块内容同场景的开销**：110 件道具 + 房间，渲染/内存/音频串扰都没有量过；距离 1200 m 只是“看不见”的暂定值，不是性能解。
6. **VR 舒适度**：瞬移没有淡入淡出；进/出各一次，频率低，但已知敏感玩家会不适。
7. **关卡 HUD 的 Canvas 形态**：现在是 Screen Space - Overlay（`m_RenderMode: 0`），VR 下是否可见/合规未验证；若不可用，关卡 HUD 要改成跟随球的世界空间 Canvas，这是独立的一条工作。
8. **后加入/联机**：见 §6.4，今天明确不支持。
9. **所有布局、坐标、尺寸、文案都是 PROVISIONAL**，只在 Build & Test 里按玩家实际视点调。

## 11. 验证命令与本次结果

本文只新增一份设计文档，没有改代码、场景、`tasks.json` 或配置，所以规划校验与 Unity 侧都没有跑：

- `python3 Docs/tools/validate_plan.py`：**NOT_RUN**（`tasks.json` 未改）。
- `python3 -m unittest discover -s Docs/tests`：**NOT_RUN**（同上）。
- Unity 编译、Play Mode、Build & Test：**NOT_RUN**（本 agent 不是 Unity 操作者）。

工作区状态：HEAD `83a5e75`；本文引用的 `StoryRoundController.cs` 与 `StoryLevel.unity` 自 `a6058ca` 起没有改动（`git diff a6058ca..HEAD -- <这两个文件>` 为空），所以行号可对。
