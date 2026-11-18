# 大厅：三块墙面板、选关与结果返回

现行决定：owner 2026-11-18 要求「大厅别站到哪儿就加入哪儿，你做成墙上的面板」「游戏结束怎么没回到大厅」。单人、本地、同一场景；代码已修改，Unity/Udon/真机验证待操作员执行，不把本地编译视为真机通过。

## 行为

- 大厅有三块墙面板（见下文「面板内容」）：正对出生点的选关板、左侧「怎么玩/操作」板、右侧「设置」板。选关板每关一张卡片，整张卡片是一个实体 Interact 按钮。走动、站住、走过旧选关垫位置都不开始；只有明确点击才开始。桌面对准按钮后鼠标左键；VR 指向后扳机。没有全局 InputUse 监听，不增加驾驶输入闸门。
- 回合仍只有 `StoryRoundController` 的 Idle / Running / Presenting / Result。到点冻结 → Presenting → 完整 Result 卡显示 **8 秒** → 自动下车并回大厅。8 秒从 launcher 观察到 Result 起算，不把 Presenting 算进阅读时间；字段 `HubLevelLauncher.resultReturnSeconds` 可配，非有限值或负值回落 8 秒，0 表示 Result 后立即返回。
- Result 卡保留「回大厅」「再来一次」。回大厅不必等满 8 秒。重试取消自动返回；若自动返回已请求退座，取消后等待座位报空与传送冷却，再由同一个 launcher 开始，不与迟到的退座回调抢座。
- 返回先走 `BallRideStation.RequestExit()`（缺 seat 才用 VRCStation 后备），等 `IsLocalPlayerSeated()==false`，再空一帧传送。10帧仍在座则在结果卡显示「请先下车，正在重试回大厅…」，每1秒复查一次；骑乘组件的IsExitPending已接管每秒ExitStation重发，launcher不重复发。直到确认退座或点「再来一次」取消返回链，不假装到达、不关岛。
- 到达大厅经测距确认后 `ParkActiveIsland()`；清自动返回、重试与传送标记，解除本次传送冷却。面板下一次明确点击即可重新选择任意关。回合保留 Result 和冻结值，不偷偷把结果清零；同关走现有 `RequestRetry()`，换关走 `TryActivate` / `SelectLevel`。
- 自由练习没有计时结算与结果卡，仍通过关内返回按钮离开。允许放弃的计时关在 Running 时点回大厅先 `AbortRound()`；被放弃的局不自动排结果返回计时。

## 代码与职责

[HubRoomHost](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubRoomHost.cs>) 仅显示房间导航相位和提示，绝不调开局入口。面板按钮列表字段为 `levelButtons`，装配器显式重写并回读新Udon变量；不依赖旧字段迁移。旧站区/停留/武装算法与测试已移除。

[HubStartButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubStartButton.cs>) 的 Interact → launcher.PressStart(index) 是选关入口。[HubLevelLauncher](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubLevelLauncher.cs>) 拥有开始、返回、取消与计时；[StoryRetryButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryRetryButton.cs>) 在有 launcher 时调 PressRetryCurrentLevel，无大厅的 lab 才直调控制器重试。

[HubSettingsHost](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingsHost.cs>) 存玩家设置（每项一个整数档位，存 VRChat PlayerData，`OnPlayerRestored` 之后才读写），[HubSettingsMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingsMath.cs>) 定范围、默认、步进、存储键与档位→数值换算；[HubSettingButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingButton.cs>) 是一行设置的一个箭头。[HubRecordBook](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubRecordBook.cs>) 只读观察回合，每次未放弃的计时关进入 Result 时记一次「最好/上次球径与件数、已玩次数」，存 PlayerData；比较规则在 [HubRecordMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubRecordMath.cs>)。[HubPanelDisplay](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubPanelDisplay.cs>) 写三块板上的全部文字；所有字词（简中/英/日）在 [HubPanelTextMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubPanelTextMath.cs>)。HubRoomHost 的提示行也跟随语言设置。

[HubAssembler](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.cs>) 负责启动/返回接线与两个结果按钮；房间外观与三块板由 [HubAssembler.Room](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.Room.cs>) 装配，布局数据由 [HubRoomLayout](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubRoomLayout.cs>) 读取。[ALL_MAPS_HUB](<ALL_MAPS_HUB.md>) 仍定义合集、各岛、数据来源与生成场景约束。

## 面板内容

标准是「成品游戏的大厅该有什么」（owner 2026-11-18）。参照原作：初代主菜单在王子星上有存档、收藏（Collection）、星座、礼物、声音（Sound）、振动（Vibration）几项（[StrategyWiki 菜单页](https://strategywiki.org/wiki/Katamari_Damacy/Menu_system)）；REROLL 有两套操作方案可选，菜单里能查操作表（[ScreenRant](https://screenrant.com/change-control-settings-katamari-damacy-reroll/)、[iMore](https://www.imore.com/katamari-damacy-reroll-beginners-guide)）。本项目把这些落成三块墙面板。状态：**本轮** = 本次代码已实现；**待接** = 控件和存储已在，消费方另派；**后续** = 本轮不做。

| 板 / 区 | 内容 | 状态 |
|---|---|---|
| 选关板 | 四张关卡卡片（2×2，清单顺序）：编号色块、关名、目标行（限时 + 目标球径/件数，自由练习写「不计时」）、记录行、「开始 →」 | 本轮 |
| 选关板 · 记录 | 计时关：最好（球径 · 件数）、上次、已玩次数；不评星（蟹座先不评星，评分是可拆卸模块）；自由练习不记 | 本轮（HubRecordBook，PlayerData） |
| 选关板 · 预览 | 卡片左侧的预览位（现为编号色块） | 后续：各关缩略图需要截图资产；位置与节点已留 |
| 选关板 · 提示行 / 制作信息 | 怎么开始（桌面左键 / VR 扳机）；底部一行「粉丝制作、与官方无关」 | 本轮 |
| 怎么玩板 | 五条规则：滚动吸附、比球小才能吸、越滚越大、限时与自动结算、8 秒后回大厅、菜单 Respawn = 放弃回大厅 | 本轮 |
| 怎么玩板 · 操作 | VR 与桌面两栏：移动（随所选 VR 操作方式换文案）、冲刺（VR 长按左扳机蓄力松手冲出；桌面键位未定，显示「冲刺键」）、道具 = Jump、下车 = 按住 Jump 1.5 秒、选择方式 | 本轮（文字）；冲刺本身由另一代理实现 |
| 设置板 | 11 行，每行 ◀ 值 ▶，改了自动保存 | 见下表 |
| 后续 | 收藏/图鉴、玩家外观、成就、排行榜、教程关、多人房间 | 后续 |

设置项（`HubSettingsMath` id 顺序；档位与默认值即代码常量）：

| id | 设置 | 档位 / 默认 | 消费方 | 状态 |
|---|---|---|---|---|
| 0 | VR 操作方式：原作双摇杆 / 简单 | 0/1，默认 0 | `VrStickMoveInput.SetControlMode`，由 HubSettingsHost 直接调用 | 本轮 |
| 1 | 左右手互换 | 关/开 | VR 输入（`Runtime/Input`）读 `IsSwapHands()` | 待接 |
| 2 | 摇杆死区 | 0.05–0.50，步 0.05，默认 0.20（= `TankStickMath.DefaultDeadzone`） | VR 输入读 `GetDeadzone()` | 待接 |
| 3 | 转向灵敏度 | ×0.5–×1.5，步 0.1，默认 ×1.0 | 球驱动/输入读 `GetTurnSensitivity()` | 待接 |
| 4 | 音乐音量 | 0–100%，步 10%，默认 80% | 音频系统读 `GetMusicVolume01()` | 待接（尚无音频系统） |
| 5 | 音效音量 | 同上 | `GetEffectsVolume01()` | 待接 |
| 6 | 提示音音量 | 同上 | `GetVoiceVolume01()` | 待接 |
| 7 | 振动 | 关 / 弱 / 标准，默认标准 | 触觉反馈读 `GetHaptics01()`（0 / 0.5 / 1） | 待接 |
| 8 | 低特效 | 关/开 | 视效读 `IsLowEffects()` | 待接 |
| 9 | 减弱自动转向 | 关/开 | 跟随镜头/骑乘视角读 `IsReduceAutoRotate()` | 待接 |
| 10 | 语言 简体中文 / English / 日本語 | 无存档时按客户端语言（`VRCPlayerApi.GetCurrentLanguage`：ja→日、zh→中、其他→英） | 三块板与提示行 | 本轮 |

板上每一行都能调、能保存，不对玩家显示「待接」；哪些还没有消费方只记在本表和 `HubSettingsMath.HasConsumer`（接上一个就把对应 id 改成 true，两处同步）。消费方接法二选一：在 Inspector 里加进 `HubSettingsHost.listeners`（收到 `OnHubSettingsChanged` 自定义事件后读 getter），或每帧比较 `GetRevision()`。PlayerData 键 `vrck.set.*`、`vrck.rec.<levelId>.{bestD,bestN,lastD,lastN,plays}` 一经发布不再改名（平台不能删键）。

## 外观与布局

房间外观在 Blender 里建模：[build_hub_room.py](<../../../Tools/hubart/build_hub_room.py>) 读 [hub_layout.json](<../../../Tools/hubart/hub_layout.json>)，生成 `Art/Hub/HubRoomArt.fbx`（无头 Blender 4.0.2 或 Windows 3.6 都能跑，用法见脚本头注释）。只用基本形体和纯色，不用任何参考素材或贴图文件。风格是原作式的明快色块：奶油色地面 + 粉/天蓝/柠檬/薄荷大方格，墙面下粉上蓝、木色踢脚和白色腰线，三块板为圆角彩框（粉/天蓝/薄荷）+ 奶油色板面 + 标题色带，卡片四色描边、红色「开始」胶囊、紫色设置箭头，另有彩旗、四盆植物、一颗装饰块魂、三盏吊灯、出生点黄星。约 2.5 万三角，导出前按材质合并成 15 个网格（14 色 + 灯泡）。

坐标：hub_layout.json 用大厅局部 Unity 米（原点 HubCenter，+Y 上，+Z 北）。板的可读面朝本地 -Z，板上 x = 读者右、y = 上、depth = 朝读者。三块板：选关板中心 (0,1.55,3.82)、6.4×2.7 m、朝南；怎么玩板 (-4.9,1.55,2.45) 偏航 -50°；设置板 (4.9,1.55,2.45) 偏航 +50°。HubSpawn (1200,0.1,0.8)、朝 +Z（装配器每次把它放到这里；旧底板里的 z=-2.8 是选关垫时代的位置，会被挪过来，场景描述符的 spawns 仍指向同一个对象）到选关板约 3.0 m，到两侧板约 4.5 m（走近 1–2 步即 2–3 m）。

字号（em，米）：板标题 0.17–0.22，关名 0.15，目标行 0.09，记录行 0.082，提示行 0.085，规则 0.078，制作信息 0.075，操作表 0.066，设置行 0.066–0.07。卡片左侧编号色块用深色（绿/紫/番茄红/藏青），白色编号可读。卡片目标行由关卡数据生成（AllMaps 的每关 plan：目标类型、目标球径/件数、限时），不是写死。俯视图里地面上方的三个黄色圆片是吊在 2.85 m 的吊灯灯罩（下方各有一盏 Unity 点光）。3 m 处 0.066 m 约 1.26°，大于常用的 1° 头显可读下限；最终可读性仍由 owner 戴头显判断。

导出后用 [verify_hub_fbx.py](<../../../Tools/hubart/verify_hub_fbx.py>) 独立核对：直接解析 FBX 节点（不经 Blender 导入），按 FBX 的 T·R·S 复合到世界，再按 Unity 导入规则（单位 1 m，x 取反）换算，与 hub_layout.json 比对；预期 `[hub-fbx] anchors 78 of 78 within 2 cm` 和 `RESULT PASS`。锚点必须是模型根的直接子节点：4.0 导出器开 bake_space_transform 时，只对根的直接子节点做轴向转换，隔一层的节点会保留 Blender 轴向（R53 实测全部锚点绕 X 转了 90°，就是这个原因）。

节点命名（模型内，空节点，模型根的直接子节点，装配器按名字核对）：`Anchor_Text_<id>`（id 见 hub_layout.json `texts`）、`Anchor_Level<i>_{Button,Preview,Name,Goal,Record,Start}`、`Anchor_Setting<NN>_{Label,Value,Less,More}`。网格名 `Art_Hub_<颜色>`，材质名 `Hub_<颜色>`。

Unity 侧（HubAssembler.Room）：

- 实例化模型为场景根 `HubRoomArt`，放在 HubCenter，删掉模型上的 Collider（墙和地面的碰撞仍来自 placement 导入），渲染器标 BatchingStatic + OccludeeStatic；按材质名换成 `Art/Hub/Materials/Hub_<颜色>.mat`（Standard，颜色取自 hub_layout.json，装配器生成）。
- 每个锚点与 hub_layout.json 比对，偏差超过 2 cm 记 PROBLEM（FBX 轴向/缩放错时文字会飘离板面）。报告行 `hub art anchors: N of N within 2 cm`。
- 每块板一个世界空间 Canvas `HubBoard_<Levels|Help|Settings>`，1 mm/单位；文字按布局放，fontSize = em/1 mm。按钮是 Canvas 下的独立对象：`LevelButton_<levelId>`（整张卡片，HubStartButton）、`SettingButton_<NN>_{Less,More}`（HubSettingButton），带 trigger BoxCollider、Default 层，不用 uGUI Button、文字 raycastTarget=false。
- 文字比锚点前移 6 mm；卡片编号前移 15 mm（色块网格比锚点凸出 6.5 mm）。字号超过 200 时改用小字号加放大 rect，世界大小不变（编号 em 0.36 m = fontSize 180 × 缩放 2）。
- 模型未导入时板子用奶油色底，功能照常，报告写 note。
- `HubRoomLights`：三盏暖色点光（范围 9 m、无阴影）挂在吊灯下。
- 删除旧的 `HubPads`、`HubLevelPanel`、`HubPrompt`。
- 合集里把回合的 `startPrompt` 写成空（开局靠墙上卡片，「点击球坐上去开始」已过时）；StoryRoundController 在提示为空时不显示 `startPromptText` 这一行。独立 StoryMode 场景不经过这段装配，保留原提示。回读：代理和 Udon 表里都必须为空，否则 PROBLEM。
- 新增根：`HubSettings`（HubSettingsHost，`vrInput` 接场景唯一 VrStickMoveInput，`listeners` = 面板显示）、`HubRecords`（HubRecordBook）、`HubPanelDisplay`。

## Unity 操作员：装配与验证

1. 同步源码（含 `Art/Hub/HubRoomArt.fbx`）；退出 Play，确认没有未保存的源场景修改。不要重启 Editor，不碰 ProjectSettings.asset。让 Unity 导入 FBX、编译 C# 与 UdonSharp（新 U# 脚本 HubSettingsHost、HubSettingButton、HubRecordBook、HubPanelDisplay 需要各自的 program asset：Project 窗口右键 Create → U# Script 生成的 .asset 放在 .cs 旁，与现有 HubStartButton.asset 相同做法），检查 Console。.meta 由 Unity 生成。
2. 检查 FBX 导入设置：Scale Factor 1、Convert Units 开、Bake Axis Conversion 关（默认值）；Model 选项卡 Read/Write 不需要。
3. 执行 `VRC Katamari/Hub/Assemble All-Maps Hub`（它在大厅装配后自动跑骑乘装配）。预期 `problems=0`；报告里应有 `hub art anchors: 44 of 44 within 2 cm`；缺 Udon variables 时按已有流程：编译 → 保存 → 重开 → 重建。
4. 执行 `VRC Katamari/Hub/Check Room Wiring`。预期 `0 problem(s), 4 panel button(s), 4 level entry/entries, 22 setting arrow(s)`。层级：`HubRoomArt`、`HubBoard_Levels/Help/Settings`、`HubRoomLights`、`HubSettings`、`HubRecords`、`HubPanelDisplay`；没有 `HubPads`/`HubLevelPanel`/`HubPrompt`。
5. Inspector 回读：`LevelButton_*` levelIndex=0/1/2/3 依次为蟹座、野餐、地图0、地形实验室；`SettingButton_NN_Less` delta=-1、`_More` delta=+1，settingId=NN；HubSettings.vrInput = 场景唯一 VrStickMoveInput；HubRecords.levelIds 与 launcher 同序；HubRoom.settings = HubSettings；其余见上一节。
6. EditMode：HubSettingsMathTests、HubRecordMathTests、HubPanelTextMathTests、HubRoomMathTests 及现有 Hub/Story 测试；报告发现/通过/失败数。
7. Play（ClientSim）：面板文字为简中（ClientSim 语言若非 zh 则为英文，记下读数）；点设置板语言行 ▶ 两次，三块板与提示行依次变英文、日文，日志 `[hub-settings] vrck.set.language=1/2`；点 VR 操作方式 ▶，日志 `[input] stick mode=simple`，操作表 VR 栏的移动行变成简单模式文案。音量等待接项值能改、能保存（行上不显示待接标记）。
8. 跑一局蟹座到 Result：日志 `[hub-record] level=_ref_cancer d=… n=… best=True plays=1`，回大厅后蟹座卡片显示「最好 / 上次 / 已玩 1」；在 Running 中 Respawn 放弃的一局不记录。重进世界（Build & Test 或上传后）记录与设置仍在。
9. 原有返回/重试/Respawn 验收（下文与 HUB_LOOP_PLAN）照旧，按钮名改为 `LevelButton_<id>`。

### 截图与读数

- 大厅俯视全景（整间房、三块板、灯光），附 `HubRoomArt` Transform 与锚点报告行。
- 出生点玩家视角（HubSpawn，眼高约 1.6 m）看选关板，四张卡片完整、不镜像、不截字；另在离选关板 2 m、3 m 各一张；怎么玩板与设置板各在 2 m、3 m 各一张。附字体资产名与几个 Text 的 fontSize（关名 150、设置行 66）、Canvas 缩放 0.001。
- 切换语言后三块板各一张（英、日）。
- 结果卡与回大厅后视角同前；回大厅后蟹座卡片的记录行。

Blender 预览（本机渲染，供对照，不代表 Unity 实际光照）：`/tmp/hub_art_overview.png`、`/tmp/hub_art_spawn_view.png`、`/tmp/hub_art_levels_2m.png`、`/tmp/hub_art_levels_3m.png`、`/tmp/hub_art_help_{2,3}m.png`、`/tmp/hub_art_settings_{2,3}m.png`（重跑脚本即重新生成）。

## owner VR 复测

在真实客户端戴头显：出生时能读四关名称和目标行，走近两侧板 2–3 m 能读规则、操作表和设置行；扳机能点中设置箭头，改语言/操作方式立即生效，退出重进仍保留；在大厅走动不会误入；扳机每次只启动所指一关。蟹座和野餐各跑一局：完整结果可读8秒、回大厅按钮可提前回、再来一次不会被旧计时拉走；回房后恢复正常身高/移动并可立即选另一关。地图0和地形实验室各按一次回大厅。记录是否看到两次瞬移、是否晕、不缩放/不锁座；有问题附设备、Avatar、日志与截图。

## 未决与验证边界

当前改动未在 Unity/Udon/Play/Build & Test/VR 中验证。退座/返回未到时每1秒复查/恢复，不会逐帧狂发传送；无本地玩家/无大厅落点则报错，不假装成功。多人本地岛开关不互通，仍不在本轮范围。面板美术已改为 Blender 模型 + 装配器叠字；FBX 轴向只在本机按导出设置推算，导入后的锚点比对是验证点。PlayerData 读写、客户端语言码、OnPlayerRestored 时序都未在真实客户端验证。没有新增 Riding 接口要求，调用现有 RequestExit/IsLocalPlayerSeated/TrySeatLocalPlayer。

## Respawn：放弃并回大厅

本地 OnPlayerRespawn先AbortRound（Running才需冻结，忽略allowAbort界面许可），取消待开始/自动入座/旧返回/重试，再按同一个BeginReturn退座并返回。结果呈现器DismissForReturn隐藏卡，不出放弃结果。AbortRound立刻使state离开Running，所以UpdateHoldInLevel不再把人拉回关卡。重复Respawn仍只保留最新一次返回。

证据：官方[OnPlayerRespawn](https://creators.vrchat.com/worlds/udon/graph/event-nodes/#onplayerrespawn)说明菜单Respawn会触发本地事件；[Station](https://creators.vrchat.com/worlds/components/vrc_station/)提供ExitStation并说明Station可阻止常规离座，但没有承诺菜单Respawn必然先退出站。所以不能把收到Respawn或自己清掉seated缓存当成退座证据。当前Riding的seated只由OnStationExited清，IsExitPending持续到回调；launcher等seated=false才移动。RequestExit取不到有效请求时用已有VRCStation.ExitStation后备；不修改Riding文件。

本地底板序列化回读：StoryMode场景spawns的fileID1425292521就是HubSpawn，旧位置(1200,.1,-2.8)、spawnRadius=0。本次装配器每次显式把场景描述符唯一spawns指向HubSpawn、半径0、朝向AlignPlayerWithSpawnPoint，再将HubSpawn设为(1200,.1,.8)。未读取Windows生成AllMapsHub的当前场景，因此操作员必须在重建后回读VRCWorld.spawns[0]的对象引用/坐标，不以底板证据冒充合集实测。

新增验收：在蟹座/野餐Running、自由练习Running、结果等待、回大厅退座中各从VRChat菜单Respawn。预期Running先round aborted，seated确认清除后回HubSpawn，active island0/4，卡不可见，后续没有hold-in-level传送。再点同关应重新唤醒并复位，道具与计时重新开始。模拟延迟OnStationExited：结果卡出现恢复提示且每秒检查、座位组件负责重发；卡上再来一次取消旧返回，确认退座后才重新开始。截图附Respawn前后视角、state、seated、IsExitPending、isParked与pending读数。

## 字段重建的依据

Linux工作区没有安装的Packages UdonSharp源码，不能确认目标1.x的FormerlySerializedAs对backing Udon变量也有效。读取上游固定commit的[UdonVariableStorageInterface](https://github.com/vrchat-community/UdonSharp/blob/aecbdbb7fc52c6f53fec2f4d314f0c11ffddb40c/Packages/com.vrchat.UdonSharp/Editor/Serialization/StorageInterfaces/UdonVariableStorageInterface.cs)：GetVariable/GetElementType按当前变量名查publicVariables/fieldDefinitions；该读写层没有旧名回退。这不是「所有版本绝不支持」的证明，所以本实现不冒充已验证迁移支持：直接删除旧字段，由Assemble All-Maps Hub写入levelButtons、CopyProxyToUdon并验证新变量存在。仅刷新脚本而不重新装配不能验收。
