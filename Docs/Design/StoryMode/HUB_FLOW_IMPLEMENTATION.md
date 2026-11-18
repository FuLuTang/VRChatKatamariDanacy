# 大厅交互与返回修正：源码交付

日期：2026-11-18。实现者hub-flow，不是Unity操作员。修改基于dc13e7a与共享工作区的并行改动；未改场景、Runtime/Riding、Runtime/Input、电机或拾取代码，未写.meta，未做Git写操作。

## 根因

1. **站区自动加入是原本的代码路径，不是碰撞体误触。** 修改前[HubRoomHost.Update](https://github.com/FuLuTang/VRChatKatamariDanacy/blob/dc13e7a/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubRoomHost.cs#L227-L279)先PickZone，再NextArmed累加dwell，CanAutoStart为真就直接TryStartLevel。原[HubAssembler.AssemblePads](https://github.com/FuLuTang/VRChatKatamariDanacy/blob/dc13e7a/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.cs#L458-L533)生成无Collider地垫，host按距离判圈，所以站进去就开局。
2. **自然结束没有任何返回请求。** [StoryRoundController.SettleRound](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryRoundController.cs#L793-L802>)只设置Presenting、冻结并更新显隐；修改前[HubLevelLauncher.OnStateEntered](https://github.com/FuLuTang/VRChatKatamariDanacy/blob/dc13e7a/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubLevelLauncher.cs#L698-L706)只写状态/日志。HubReturnButton是手动入口，LevelSwitchHost.ParkActiveIsland只有返回测距成功才执行。因此自然结算后会留在关卡/座位，岛也不会自动停放。
3. **Respawn没有被导航层当作放弃。** 修改前launcher没有OnPlayerRespawn，Running越界纠偏会把已经重生到大厅的玩家送回关卡。旧[BallRideStation.OnPlayerRespawn](https://github.com/FuLuTang/VRChatKatamariDanacy/blob/dc13e7a/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Riding/BallRideStation.cs#L316-L338)又先清seated缓存，再单次ExitStation，没有等待真实退座确认。Riding代理本轮独立把确认改为OnStationExited清除，并提供IsExitPending；本代理只调用接口。

## 改动范围

- [HubRoomHost](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubRoomHost.cs>)：纯显示，不再有站区/dwell/开局调用；字段levelButtons。
- [HubRoomMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubRoomMath.cs>)、[HubRoomMathTests](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/Story/HubRoomMathTests.cs>)：删除站区选择/武装/停留算法与旧测试，替换为面板显示相位规则。
- [HubLevelLauncher](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubLevelLauncher.cs#L365-L379>)：本地Respawn先AbortRound、取消旧导航/入座、隐藏结果，再统一退座返回；Result默认8秒自动返回；重试可取消自动返回/失败恢复；返回成功清传送与冷却并停岛。
- [HubLoopMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubLoopMath.cs>)、新增[HubResultFlowTests](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/Hub/HubResultFlowTests.cs>)：8秒边界、手动提前回、重试取消、1秒恢复检查、Respawn放弃/关纠偏、返回后四索引立即可选、无效时间。
- [StoryRetryButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryRetryButton.cs>)：有launcher时走PressRetryCurrentLevel，保留无大厅lab直接重试后备。
- [StoryResultPresenter](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryResultPresenter.cs#L139-L160>)：结果卡恢复提示与Respawn隐藏，不改评分事实；回合运行后清提示/隐藏标记。
- [HubAssembler](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.cs#L412-L553>)：生成墙面2×2按钮；显式VRCWorld唯一spawn=HubSpawn、spawnRadius=0、朝向对齐；出生点(1200,.1,.8)；结果两按钮接同一launcher，retry行为在retryRoot子物体也正确接线；levelButtons/resultPresenter/resultReturnSeconds等Udon表回读。
- [AllMapsHubAssembler](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/AllMapsHubAssembler.cs>)：复用同一套按钮/新字段验证；[HubRoomWiringCheck](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Story/HubRoomWiringCheck.cs>)：按按钮检查，不再按圈半径检查。HubSelectMath只有字段名说明同步，旧目录room宽度数据公式未改。
- 当前说明同步到[HUB_ROOM](<HUB_ROOM.md>)、[HUB_LOOP_PLAN](<HUB_LOOP_PLAN.md>)、[ALL_MAPS_HUB](<ALL_MAPS_HUB.md>)、[StoryMode README](<README.md>)。

## 默认值与接口约定

完整Result卡8秒（resultReturnSeconds可配，不含Presenting）。使用实体Collider+Udon Interact；不是uGUI Button，不要求VRC_UiShape。面板文字44unit、0.0025m/unit，em约11cm；出生点距面板约2.9m，2–3m真实可读仍要头显判。

退座只调用RequestExit/IsLocalPlayerSeated/IsExitPending。IsExitPending为真时Riding自己每秒重发，launcher不重复发；10帧仍在座时显示请先下车，并每1秒检查。拿不到有效RequestExit时用已有VRCStation.ExitStation后备。确认seated=false再空帧移动，测距成功才停岛。再来一次取消返回链，若退出尚未确认则等退出再开，不和迟到回调抢座。

关于FormerlySerializedAs：目标安装包在Linux不可见，不能确认其对backing变量迁移有效；上游固定commit的UdonVariableStorageInterface按当前字段名读写，没有在该层做旧名回退。采用明确重装配，不声称整个U#都不支持属性。装配器直接写levelButtons并验证，必须重建，不能仅刷新脚本。详细来源与操作见[HUB_ROOM](<HUB_ROOM.md>)末节。

## 本地闸门

本代理最后一次check.py工具返回：`Compile check: clean (4 assembly/assemblies, 21.2 s)`。范围是Runtime、Editor、Assembly-CSharp-Editor、Tests.EditMode四程序集。

本代理执行run_tests.py --filter Hub时，被workspace-write沙箱拒绝写入`~/.cache/vrc-katamari-refasm/test-build/partial-*`，PermissionError退出1；没有绕过或提权。Lead随后在根会话以可写缓存路径执行测试，转报：

`run_tests.py --filter Hub --filter Story --filter Pickup --filter CandidateQueue --filter Riding --filter Ride`

编译clean（4程序集）；发现343，passed312、failed0、not runnable31：26需GameObject/场景，3需引擎原生调用，2其它。本地可执行部分全部通过；此处为Lead转报，不冒充本代理直接取得该工具输出。

规划/文档Python门本代理直接返回：`PASS: 61 tasks; both release DAGs acyclic; source hashes verified.`；`Ran 1269 tests in 97.692s — OK`。未改计划数据；它们不能代替Unity验证。

## Unity操作员与owner交接

按[HUB_ROOM](<HUB_ROOM.md>)逐步执行：导入/编译 → 如需Ride UX先跑Ride UX → Assemble All-Maps Hub → Check Room Wiring → 回读新Udon字段 → EditMode/Udon编译 → Play/真机。预期0problem，4按钮/4入口，初始与回房active island0/4，Running1/4，返回distance<=1、seated=false、IsExitPending=false，回房首个点击可开下一关。

截图必须含大厅俯视、玩家出生视角看面板（另2m/3m）、完整Result双按钮、返回大厅后视角；附state、pending、parked、active岛计数。结果第7–8秒重试、自动退座中重试、手动提前回、各Running关菜单Respawn都要单独验。菜单Respawn不出放弃结果卡，也不再hold-in-level拉回。owner VR复测看面板可读、扳机只选所指、返回恢复身高/移动、同/异关立即再选与瞬移舒适度。

本代理没有Unity/Play/Build&Test/VR证据；没有读取Windows输出合集的实时spawn引用。底板源码已回读spawn指向HubSpawn，合集由装配器重写，仍须操作员实读。多人本地岛开关隔离、面板美术和字体实际渲染待后续验证；不声称功能已在VR通过。
