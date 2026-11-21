# 对战模式（第一版）设计

## 0. 本文范围与依据

本文写的是多人对战（KatamariBall）第一版怎么做：一局的流程、谁说了算、同步什么、观战，以及分几批做。还没有代码，也没有在 Unity 里验证过。

已定的规则（不在本文重新讨论）：
- 玩法是 2025 作「スポーツ塊」式交分：最多 4 人；场景里有交分区，球必须滚进去才能把体积换成分；交分后球缩小，规则为 `post = max(start, start + (pre − start) × 0.25)`；球径小于 0.55 m 拒绝交分；交分区 60 s 轮换一次；被更大的球吞掉后在别处重生；结算时另给 3 项第一名各 +50 pt。见 [OPEN_DECISIONS](<../../OPEN_DECISIONS.md>)「交分与回合规则（2026-11-14 拍板）」(a)–(d) 和「Owner decisions 2026-11-21」。
- 走进准备圈就算准备，用 Player Join Zones 的做法；倒计时剩 3 s 时锁定名单。一局结束后所有人回中庭。
- 观战可开关，有鸟瞰（正交俯视加玩家标记）和跟随某个玩家两种视角，第一版两种都做。
- 第一版地图是 WLK REROLL+ 的 vs_2：场地 `03_C_VS`，整局只用这一个场地。初始球径 1.5 m（原作 `ssCoreInit_vs_2` 的 8 个槽都是 1500 mm）。原作时限 400 s（`s32Time = 12000` 帧，按 30 Hz 换算）。以后两作的 8 张对战图都要做进来，由玩家选图。

数值口径：(a)–(c) 的 0.25、0.55 m、60 s 是按野餐草坪尺度（起始球径 0.30 m）定的 PROVISIONAL 值。vs_2 的起始球径是 1.5 m，这三个数怎么换算到 vs_2 放在 §6 开放问题 Q1。

原作依据：[KatamariBallRules](<../../Research/KatamariBallRules/README.md>) C1–C22。两作原版的对战（本地 2 人分屏）与这里的规则不同，取证在仓库外的工作笔记里，只作对照。

## 1. 现状盘点：仓库里能复用什么

盘点时间 2026-11-21。范围：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/` 下的运行时、测试、实验场景，以及 `Docs/Design/` 下的模型。查法：用 grep 在 `Assets/VRC_Katamari` 里搜 `UdonSynced|RequestSerialization|SetOwner|OnPlayerTrigger|OnPlayerLeft|OnPlayerJoined|SendCustomNetworkEvent|BehaviourSyncMode`，共 48 处命中；再用 CodeGraph 查账本、交分和远端代理相关的符号。

**总的结论：仓库里还没有任何联网代码。** 命中的 48 处全是 `[UdonBehaviourSyncMode(BehaviourSyncMode.None)]` 或注释，没有一个 `[UdonSynced]` 字段，也没有 `RequestSerialization`、`SetOwner` 或网络事件。唯一的玩家事件是 `BallRideStation.OnPlayerJoined`（`Runtime/Riding/BallRideStation.cs:231`）。tasks.json 里 T17（大厅加入区与回合状态机）、T18（交分、吞噬、KO 及重生事务）、T26（一图两人纵切片）、T33（结果板、领奖台）、T56（远端代理重建）、T60（单机对战 vs CPU）的状态都是 TODO。

| 东西 | 位置 | 现在是什么 | 能否直接用 |
|---|---|---|---|
| 回合账本（纯算法） | `Runtime/Economy/GrowthLedgerMath.cs:159`；阶段枚举 `LedgerPhase` 在 :12（Lobby/Ready/Preparing/Playing/Settling/Results），生命周期 `PlayerLifecycle` 在 :31（Active/KnockedOut/RespawnPending/Disconnected）；`TryJoin` :376、`TrySubmitClaim` :613、`TryResolveClaims` :751、`TryApplyCommit` :843、`TryBank` :909、`TryKo` :963、`TryRespawn` :1025、`TryDisconnect` :1064、`TryInstallSnapshot` :1112 | 移植自 `Docs/Design/Ledger/ledger.py`（DRAFT）：未交存体积与已交存积分分开记账；吸收走「请求 → 裁决 → 提交」，同一 (物件, 世代) 只提交一次；交分按 requestId 去重；KO 时清空受害者未交存部分、已交存保留；支持快照安装。全部是无状态静态方法，数据放在调用方持有的并行数组里 | **算法层可直接用**：多人账本的规则已经写好并有测试（`Tests/EditMode/Economy/`）。缺的是「谁持有这些数组、怎么同步」，即权威行为与网络层。注释 :155–156 写明权威选举、重连 epoch、结算策略尚未决定，没有替它猜 |
| 球电机里的账本 | `Runtime/BallMotor/KatamariBallMotor.cs:307–312`：`PlayerCapacity = 1`、`CommitCapacity = 256`、`ClaimCapacity = 8`、`BankRequestCapacity = 8` | 每个球的电机自带一份只有 1 名玩家的账本，吸收在本地「自己提交、自己裁决」（`Pickup/PickupAbsorbStepMath.cs:125–141` 的 `TryCommitAbsorb`） | **不能直接当多人账本**：多人时裁决方必须是一个共享的权威端，不能每个球自己说了算。电机这一份可以保留为「本地预测 + 显示」，确认值改由权威端下发（§2.4） |
| 交分 + 缩球 | `KatamariBallMotor.TryBank` :5645 / `TryBankWithReason` :5666（返回 0–8 的拒绝原因）、`ApplyBankShrink` :5745；公式在 `Runtime/BallMotor/BankShrinkMath.cs:53`（`TryPostBankDiameter`）、:82、:99；参数 `bankResidualFraction = 0.25`、`minConversionDiameterWorldM = 0.55`（`KatamariBallMotor.cs:226–227`）；Python 模型 `Docs/Design/Gameplay/round_model.py:474` | **已实现**：门槛检查 → 账本事务 → 按 (a) 公式缩球 → 重建径向代理；缩球后残余体积不会再次计分（carried） | **单机可直接用**。多人时交分判定要搬到权威端，缩球要用权威端下发的结果；电机里这一份逻辑可以原样作为「执行确认结果」的那一半。调用方目前只有 CPU 驾驶 `Runtime/Ai/CpuBallPilot.cs:88`，场景里还没有交分区触发器 |
| CPU 四球对练 | `Runtime/Ai/CpuSkirmishBootstrap.cs:21`、`CpuBallPilot.cs`，实验场景 `Labs/CpuSkirmish/` | 一个场景里 4 个球、4 个电机，CPU 会去交分区交分（T60 原型） | 能复用的是「一个场景放 4 个电机」的装配方式和交分区的位置参数；CPU 本身不进第一版对战 |
| 远端代理（纯算法） | `Runtime/BallLayers/RemoteProxy/RemoteProxyMath.cs:112`；文档 `Docs/Design/BallLayers/RemoteProxy/README.md` | 远端球上已确认吸附物的外观缓存：去重、按世代/修订号排序、按渲染预算挑选；明确不进入碰撞、计分和 PvP | **算法可用**。README 写明「拥有数组的 Behaviour 与 Renderer 池留待后续」，组件没有写（T56） |
| 切关 / 岛开关 | `Runtime/Hub/LevelSwitchHost.cs:42`，`TryActivate` :257，`ParkActiveIsland` :169；规则 `IslandActivationMath`；文档 [ALL_MAPS_HUB §3.1](<../StoryMode/ALL_MAPS_HUB.md>) | 一个场景、多座岛，同一时刻最多一座岛开着；切关时给**唯一一个**电机换障碍表、地形、起始球径 | **单机可用，多人要改**：它假设场景里只有一个电机，而且 `SetActive` 只影响本机（§4.3）。对战场地要作为单独一座岛，本局所有参赛者和观战者都打开它 |
| 单人开局 / 回大厅 | `Runtime/Hub/HubLevelLauncher.cs:114`（`TryStartLevel` :549、`TryReturnToRoom` :737、传送 :1287）；`Runtime/Story/StoryRoundController.cs:120` | 单人、只在本地运行（注释 :101–106：`TeleportTo` 只能移动本地玩家，两个客户端各跑各的循环） | 「传送入场 → 入座 → 比赛 → 回大厅」的本地流程和传送写法可以照抄。回合控制器是单人的，不能当对战回合用 |
| 大厅显示 | `Runtime/Story/Hub/HubRoomHost.cs:13`（只读的提示显示）、`HubPanelDisplay.cs`；大厅重做设计 `.work/notes/research/hub_redesign.md`（对战区：报名板、观战屏、领奖台） | 单人大厅的面板与提示 | 面板组件可复用。还没有准备圈、没有对战报名板 |
| 骑球座位 | `Runtime/Riding/BallRideStation.cs:113`（本地座位状态，`OnPlayerJoined` :231） | 每个球一个座位，由本地玩家坐 | 每个参赛者坐自己的球，座位代码可复用；4 个球就要 4 个座位 |
| 观战相关 | `Runtime/Diagnostics/BallFollowCamera.cs:10–19`：第二台 Camera 渲染进 RenderTexture，再用 RawImage 显示（调试用）；文档 `Docs/Design/BallPresentation/DEBUG_CAMERA.md:16` 写「ClientSim 与真实客户端都成立」，:59 写「实际显示未实测」 | 调试用的画中画追球相机 | 是现成的「相机 + RenderTexture + RawImage」写法，观战屏可以照这个做；在真实客户端里的显示效果还没测过 |
| 对战地图 vs_2 | `Docs/Design/Levels/_ref_wlk_vs2/`（被 git 忽略），由 `~/KatamariRef/tools/adapt_wlk_vs2.py` 生成；离线校验 `levelbuild.py check` 0 错误；地形 3,087 面，props 798 件（795 件可吸），33 个障碍固体（见 `.work/notes/research/wlk_vs_placement.md` 第三轮补） | 已转成我们的 v2 格式。出生点是临时选的场地中心附近平整点，**不是原作出生点**；53 个随机槽没有处理；`Loc/03K*` 的 302 面局部地面没有并入 | 离线数据可用，进 Unity 的导入另由 Unity 操作员做；8 个出生点的位置原作数据没查到（§2.3） |
| 离线规则模型 | `Docs/Design/Gameplay/round_model.py`（交分、吞噬、重生、CPU 策略的模拟） | 模拟用 | 做参数推算用，不进运行时 |

小结：
- **可以直接用的**：账本算法（交分、KO、重生、快照）、交分缩球公式和电机里的执行代码、远端代理算法、本地传送与入座写法、调试相机的 RenderTexture 写法、vs_2 的离线数据。
- **必须新写的**：
  - 联网层：同步变量、ownership、网络事件。
  - 对战权威行为：谁持有共享账本、谁裁决吸收/交分/吞噬。
  - 准备圈和锁名单的逻辑。
  - 多电机的场地开关（替代「单电机」的 LevelSwitchHost 用法）。
  - 交分区触发器与 60 s 轮换。
  - 玩家吞玩家的判定。
  - 观战相机与玩家标记。
  - 远端球的显示组件。

## 2. 一局的状态机

## 3. VRChat / Udon 约束

## 4. 观战

## 5. 分批实施计划

## 6. 开放问题
