# 主房间与关卡闭环：平台边界与传送时序

现行操作规范和复测步骤见 [HUB_ROOM](<HUB_ROOM.md>)；合集与岛装配见 [ALL_MAPS_HUB](<ALL_MAPS_HUB.md>)。owner 2026-11-18 决定使用墙面选关按钮，完整结果显示8秒后自动回大厅，也可提前回大厅或再来一次。以下描述源码意图，不表示 Unity/VR 已验收。

## 平台边界

世界构建单位是一个 Unity 场景；本项目没有可用的运行时场景流式加载接口。大厅与关卡都在同一张场景中，靠本地玩家传送、关卡登记与岛启停切换，不做跨世界 Portal 传递回合结果。

平台查证来源：[创建世界](https://creators.vrchat.com/worlds/creating-your-first-world/)、[Build & Test](https://creators.vrchat.com/worlds/udon/using-build-test/)、[传送 API](https://creators.vrchat.com/worlds/udon/players/player-positions/)、[Station](https://creators.vrchat.com/worlds/components/vrc_station/)、[Interact](https://creators.vrchat.com/worlds/udon/graph/event-nodes/#interact)。早期查证日期2026-11-15，Interact页于本次2026-11-18再次读取：实体交互要求同物体 Collider + UdonBehaviour；2D uGUI Button才需要VRC Ui Shape。本实现只用实体交互与不可射线文字，不混两套输入。

Station 可能阻止传送，因此必须先退座；Disable Station Exit 不禁止脚本 ExitStation。ExitStation 到回调的真实延迟、Avatar身高恢复、头显观感都仍需当前客户端验证。

## 归属

`StoryRoundController` 是唯一的 Idle / Running / Presenting / Result 状态机。回合控制器拥有计时、冻结、重试复位、放弃；`StoryResultPresenter` 消费冻结事实显示结果，星级与评语是可选模块，不反向决定开局或传送。

`HubLevelLauncher` 拥有所有导航传送、到达测距、自动返回计时与重试取消。`HubRoomHost` 只写提示，不读站区决定开局。墙面按钮的 levelIndex、levelEntries、levelNames、LevelSwitchHost数组按同一清单顺序。

## 开始

1. Interact → HubStartButton.Press → launcher.PressStart(index)。Idle/Result允许开，Presenting/Running拒绝；在飞传送与冷却防止重复按键。
2. 换关先 TryActivate → SelectLevel；同关从大厅开始先 WakeActiveIsland。
3. 入口离球座不超过entryTrustMeters（12m）优先入口，否则回退球座；新切关时用切关宿主给的球顶坐标，不读仍未跟到新岛的座位旧坐标。
4. TeleportTo → 默认2帧后测GetPosition，达到默认1m容差才BeginRound / RequestRetry；30帧未到则拒绝，不空跑计时。
5. 开局请求TrySeatLocalPlayer，最多3次、10帧间隔，OnStationEntered才是入座证据。Running时，入座直接算在关卡内；未入座时有2.5秒grace，随后最多3次越界纠偏。

## 结束与取消

1. SettleRound冻结事实，Presenting只做呈现；控制器推进到Result后，launcher记录Time.time。评分模块缺失不阻止返回。
2. Result完整卡保持resultReturnSeconds（默认8秒）。ShouldAutoReturn仅接受Result、已武装、无重试且无在飞传送；Presenting不计入阅读时间。结果计时仅触发一次；后续退座恢复由返回路径拥有。
3. 手动回大厅允许Presenting/Result，不等8秒；Running中仅CanAbortRunning为真时AbortRound后回。自由练习没有结果卡，走手动出口。
4. PressRetryCurrentLevel取消尚未触发的计时；自动返回已在飞时，取消returnPending，并等待已请求的退座回调和本次传送冷却，再走同一个PressStart。手动返回不被重试抢占；已重新Running时旧返回不得触发。
5. BeginReturn → rideSeat.RequestExit（没有seat时才直接VRCStation.ExitStation）→ 等seated=false → 空一帧 → TeleportTo(HubSpawn+lift)。10帧仍seated则结果卡提示请先下车，每1秒复查；骑乘组件IsExitPending期间自己每秒重发ExitStation，launcher只观察不重复发。
6. 到达测距成功（含已经在落点的分支）→ FinishReturn清pending/计时/重试与冷却 → ParkActiveIsland。面板立即可以选择；回合仍保留Result及冻结事实，同关下次RequestRetry，换关SelectLevel。

## 失败处理

到达入口超时不开回合；退座超时不传送；返回未达大厅不关闭岛，提示并每1秒复查/恢复。再来一次可取消自动返回或返回恢复链，不把unknown当成功。场景缺roomSpawn、controller、station或关键程序变量必须在装配检查里处理，不靠VR碰运气。

## 数据与场景

合集用 [hub_levels.json](<hub_levels.json>)，每关单独Island_<id>。各岛首次激活让宿主Start跑过，再停用空闲岛；控制器、座位、输入、launcher、结果卡不放岛内。大厅是placement的arena-cell子树，导入器只更新自己的受管子树。

同关重试仍复用控制器已有的球/道具复位，不另写一份重置。结果从冻结getter读取，不回头测电机或已复位道具。没有跨对象复制结果数据。

## 测试范围

[HubResultFlowTests](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/Hub/HubResultFlowTests.cs>) 覆盖8秒边界、手动提前回、重试取消、在飞互斥、非法时间与回到大厅后四索引立即可选；既有HubLoopMathTests覆盖开始/返回合法性、容差、座位纠偏等。纯规则测试不能证明TeleportTo、ExitStation回调、实际Collider射线与可读性，Unity操作员和owner的验收以[HUB_ROOM](<HUB_ROOM.md>)为准。

菜单Respawn是显式放弃：本地事件先AbortRound并取消旧开始/入座请求，再走同一个退座返回路径；不经过Running越界纠偏。呈现器隐藏结果卡，回大厅测距成功后停岛并重置面板可选状态。详见[HUB_ROOM](<HUB_ROOM.md>)的Respawn验收与平台证据。
