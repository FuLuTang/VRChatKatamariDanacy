# 大厅：三块墙面板、选关与结果返回

现行决定：owner 2026-11-18 要求「大厅别站到哪儿就加入哪儿，你做成墙上的面板」「游戏结束怎么没回到大厅」。单人、本地、同一场景；代码已修改，Unity/Udon/真机验证待操作员执行，不把本地编译视为真机通过。

## 行为

- 大厅有三块墙面板（见下文「面板内容」）：正对出生点的选关板、左侧「怎么玩/操作」板、右侧「设置」板。三块都是 VRChat 标准的世界空间 UI 面板（Canvas + VRC_UiShape，施工图 [HUB_PANEL_PREFAB](<HUB_PANEL_PREFAB.md>)）。选关板每关一张卡片，整张卡片是一个 uGUI Button。走动、站住、走过旧选关垫位置都不开始；只有明确点击才开始。桌面对准面板出现 UI 指针后鼠标左键；VR 用激光指向后扳机。没有全局 InputUse 监听，不增加驾驶输入闸门。
- 回合仍只有 `StoryRoundController` 的 Idle / Running / Presenting / Result。到点冻结 → Presenting → 完整 Result 卡显示 **8 秒** → 自动下车并回大厅。8 秒从 launcher 观察到 Result 起算，不把 Presenting 算进阅读时间；字段 `HubLevelLauncher.resultReturnSeconds` 可配，非有限值或负值回落 8 秒，0 表示 Result 后立即返回。
- Result 卡保留「回大厅」「再来一次」。回大厅不必等满 8 秒。重试取消自动返回；若自动返回已请求退座，取消后等待座位报空与传送冷却，再由同一个 launcher 开始，不与迟到的退座回调抢座。
- 返回先走 `BallRideStation.RequestExit()`（缺 seat 才用 VRCStation 后备），等 `IsLocalPlayerSeated()==false`，再空一帧传送。10帧仍在座则在结果卡显示「请先下车，正在重试回大厅…」，每1秒复查一次；骑乘组件的IsExitPending已接管每秒ExitStation重发，launcher不重复发。直到确认退座或点「再来一次」取消返回链，不假装到达、不关岛。
- 到达大厅经测距确认后 `ParkActiveIsland()`；清自动返回、重试与传送标记，解除本次传送冷却。面板下一次明确点击即可重新选择任意关。回合保留 Result 和冻结值，不偷偷把结果清零；同关走现有 `RequestRetry()`，换关走 `TryActivate` / `SelectLevel`。
- 自由练习没有计时结算与结果卡，仍通过关内返回按钮离开。允许放弃的计时关在 Running 时点回大厅先 `AbortRound()`；被放弃的局不自动排结果返回计时。

## 代码与职责

[HubRoomHost](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubRoomHost.cs>) 仅显示房间导航相位和提示，绝不调开局入口。面板按钮列表字段为 `levelButtons`，装配器显式重写并回读新Udon变量；不依赖旧字段迁移。旧站区/停留/武装算法与测试已移除。

[HubStartButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubStartButton.cs>) 的 `Press`（卡片 Button.onClick → `UdonBehaviour.SendCustomEvent("Press")`）→ launcher.PressStart(index) 是选关入口。[HubLevelLauncher](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubLevelLauncher.cs>) 拥有开始、返回、取消与计时；[StoryRetryButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/StoryRetryButton.cs>) 在有 launcher 时调 PressRetryCurrentLevel，无大厅的 lab 才直调控制器重试。

[HubSettingsHost](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingsHost.cs>) 存玩家设置（每项一个整数档位，存 VRChat PlayerData，`OnPlayerRestored` 之后才读写），[HubSettingsMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingsMath.cs>) 定范围、默认、步进、存储键与档位→数值换算；[HubSettingButton](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingButton.cs>) 是一行设置的一个箭头（同样由 Button.onClick → `SendCustomEvent("Press")` 调用）。[HubRecordBook](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubRecordBook.cs>) 只读观察回合，每次未放弃的计时关进入 Result 时记一次「最好/上次球径与件数、已玩次数」，存 PlayerData；比较规则在 [HubRecordMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubRecordMath.cs>)。[HubPanelDisplay](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubPanelDisplay.cs>) 写三块板上的全部文字（TextMeshProUGUI）；所有字词（简中/英/日）在 [HubPanelTextMath](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Story/Hub/HubPanelTextMath.cs>)。HubRoomHost 的提示行也跟随语言设置。

[HubAssembler](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.cs>) 负责启动/返回接线与两个结果按钮；房间外观与三块面板 Prefab 的放置/接线由 [HubAssembler.Room](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubAssembler.Room.cs>) 负责，房间布局数据由 [HubRoomLayout](<../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Hub/HubRoomLayout.cs>) 读取。[ALL_MAPS_HUB](<ALL_MAPS_HUB.md>) 仍定义合集、各岛、数据来源与生成场景约束。

## 面板内容

标准是「成品游戏的大厅该有什么」（owner 2026-11-18）。参照原作：初代主菜单在王子星上有存档、收藏（Collection）、星座、礼物、声音（Sound）、振动（Vibration）几项（[StrategyWiki 菜单页](https://strategywiki.org/wiki/Katamari_Damacy/Menu_system)）；REROLL 有两套操作方案可选，菜单里能查操作表（[ScreenRant](https://screenrant.com/change-control-settings-katamari-damacy-reroll/)、[iMore](https://www.imore.com/katamari-damacy-reroll-beginners-guide)）。本项目把这些落成三块墙面板。状态：**本轮** = 本次代码已实现；**待接** = 控件和存储已在，消费方另派；**后续** = 本轮不做。

| 板 / 区 | 内容 | 状态 |
|---|---|---|
| 选关板 | 四张关卡卡片（2×2，清单顺序）：编号色块、关名、目标行（限时 + 目标球径/件数，自由练习写「不计时」）、记录行、「开始 →」 | 本轮 |
| 选关板 · 记录 | 计时关：最好（球径 · 件数）、上次、已玩次数；不评星（蟹座先不评星，评分是可拆卸模块）；自由练习不记 | 本轮（HubRecordBook，PlayerData） |
| 选关板 · 预览 | 卡片左侧的预览位（现为编号色块） | 后续：各关缩略图需要截图资产；位置与节点已留 |
| 选关板 · 提示行 / 制作信息 | 怎么开始（桌面左键 / VR 扳机）；底部一行「粉丝制作、与官方无关」 | 本轮 |
| 怎么玩板 | 五条规则：滚动吸附、比球小才能吸、越滚越大、限时与自动结算、8 秒后回大厅、菜单 Respawn = 放弃回大厅 | 本轮 |
| 怎么玩板 · 操作 | VR 与桌面两栏：移动（随所选 VR 操作方式换文案）、冲刺（VR 长按左扳机、桌面长按 Shift，蓄满自动冲出）、道具 = Jump、下车 = 按住 Jump 1.5 秒、选择方式 | 文案与运行时键位一致；动作规则见 [冲刺](../ControlIntent/README.md#冲刺wlk-规则与长按适配) |
| 设置板 | 12 行，每行 ◀ 值 ▶，改了自动保存 | 推进方向箭头的第 12 行待更新 Prefab；见下表 |
| 后续 | 收藏/图鉴、玩家外观、成就、排行榜、教程关、多人房间 | 后续 |

设置项（`HubSettingsMath` id 顺序；档位与默认值即代码常量）：

| id | 设置 | 档位 / 默认 | 消费方 | 状态 |
|---|---|---|---|---|
| 0 | VR 操作方式：原作双摇杆 / 简单 | 0/1，默认 0 | `VrStickMoveInput.SetControlMode`，由 HubSettingsHost 直接调用 | 本轮 |
| 1 | 左右手互换 | 关/开，默认关 | `VrStickMoveInput` 读 `IsSwapHands()`，映射前交换两根杆的 X/Y；不换扳机 | 本轮 |
| 2 | 摇杆死区 | 0.05–0.50，步 0.05，默认 0.20（= `TankStickMath.DefaultDeadzone`） | `VrStickMoveInput` 读 `GetDeadzone()`，替换世界内的摇杆死区 | 本轮 |
| 3 | 转向灵敏度 | ×0.5–×1.5，步 0.1，默认 ×1.0 | `VrStickMoveInput` 读 `GetTurnSensitivity()`，只缩放 `turn`，夹到 [-1,1] | 本轮 |
| 4 | 音乐音量 | 0–100%，步 10%，默认 80% | 音频系统读 `GetMusicVolume01()` | 待接（尚无音频系统） |
| 5 | 音效音量 | 同上 | `AbsorbFeedbackHost` 读 `GetEffectsVolume01()` | 已接本地吸附音；待试听 |
| 6 | 提示音音量 | 同上 | `GetVoiceVolume01()` | 待接 |
| 7 | 振动 | 关 / 弱 / 标准，默认标准 | `AbsorbFeedbackHost` 读 `GetHaptics01()`（0 / 0.5 / 1） | 已接 VR 吸附；待实机 |
| 8 | 低特效 | 关/开 | 视效读 `IsLowEffects()` | 待接 |
| 9 | 减弱自动转向 | 关/开 | 跟随镜头/骑乘视角读 `IsReduceAutoRotate()` | 待接 |
| 10 | 语言 简体中文 / English / 日本語 | 无存档时按客户端语言（`VRCPlayerApi.GetCurrentLanguage`：ja→日、zh→中、其他→英） | 三块板与提示行 | 本轮 |
| 11 | 推进方向箭头 | 关 / 开，默认开 | `PushDirectionCue` 读 `IsPushCueEnabled()`；`vrck.set.pushCue` | 代码已接，面板 Prefab 待新增一行 |

板上每一行都能调、能保存，不对玩家显示「待接」；哪些还没有消费方只记在本表和 `HubSettingsMath.HasConsumer`（接上一个就把对应 id 改成 true，两处同步）。消费方接法二选一：在 Inspector 里加进 `HubSettingsHost.listeners`（收到 `OnHubSettingsChanged` 自定义事件后读 getter），或在需要用到设置时比较 `GetRevision()`。VR 摇杆用后者：`VrStickMoveInput.settings` 引用本大厅的设置宿主，第一帧有效 VR 输入与修订号变化时读三项，不注册 listener；因此在输入门关闭时调设置或恢复存档，也不会漏掉更新。PlayerData 键 `vrck.set.*`、`vrck.rec.<levelId>.{bestD,bestN,lastD,lastN,plays}` 一经发布不再改名（平台不能删键）。

## 外观与布局

房间在 Blender 里建模：[build_hub_room.py](<../../../Tools/hubart/build_hub_room.py>) 读 [hub_layout.json](<../../../Tools/hubart/hub_layout.json>)，生成 `Art/Hub/HubRoomArt.fbx`（无头 Blender 4.0.2 或 Windows 3.6，用法见脚本头注释）。只用基本形体和纯色。风格是原作式明快色块：奶油色地面 + 粉/天蓝/柠檬/薄荷大方格，墙面下粉上蓝、木色踢脚和白色腰线，三块板的圆角彩框（粉/天蓝/薄荷）+ 木腿 + 角上黄色圆钉，彩旗、四盆植物、一颗装饰块魂、三盏吊灯、出生点黄星。约 1.9 万三角，按材质合并成 13 个网格。**模型里不再有板面、卡片、设置行和文字锚点**：这些全在面板 Prefab 里。

面板是三个 Prefab（`Assets/VRC_Katamari/Prefabs/Hub/HubBoard_{Levels,Help,Settings}.prefab`），由 Unity 操作员按 [HUB_PANEL_PREFAB](<HUB_PANEL_PREFAB.md>) 搭建：World Space Canvas，同物体 VRC_UiShape + BoxCollider + GraphicRaycaster，Default 层，缩放 0.001（1 画布单位 = 1 mm），板面/色带/卡片是圆角 Image，全部文字是 TextMeshProUGUI（Raycast Target 关），卡片与 ◀ ▶ 是 uGUI Button（Navigation None），onClick 在编辑器里绑定到本物体 UdonBehaviour 的 `SendCustomEvent("Press")`。中日英文字用一个静态 TMP 字体资产 `HubPanelSans SDF`（Noto Sans CJK SC Bold 的子集，字符集 [hub_panel_charset.txt](<../../../Tools/hubart/hub_panel_charset.txt>) 由 [build_hub_font.py](<../../../Tools/hubart/build_hub_font.py>) 生成）。

坐标：hub_layout.json 用大厅局部 Unity 米（原点 HubCenter，+Y 上，+Z 北）。板的可读面朝本地 -Z。三块板：选关板中心 (0,1.55,3.82)、6.4×2.7 m、朝南；怎么玩板 (-4.9,1.55,2.45) 偏航 -50°、3.2×2.3 m；设置板 (4.9,1.55,2.45) 偏航 +50°、3.2×2.3 m。面板 Canvas 平面在板框平面前 `canvasDepthM` = 0.06 m（板框厚 0.10 m，前表面在 0.05 m，面板贴在它前面 1 cm）。HubSpawn (1200,0.1,0.8)、朝 +Z（装配器每次放到这里），到选关板约 3.0 m，到两侧板约 4.5 m（走近 1–2 步即 2–3 m）。

字号（em，米 = 画布 Font Size / 1000）：板标题 0.17–0.22，关名 0.15，目标行 0.09，记录行 0.082，提示行 0.085，规则 0.078，制作信息 0.075，操作表 0.066，设置行 0.066–0.07。3 m 处 0.066 m 约 1.26°，大于常用的 1° 头显可读下限；最终可读性仍由 owner 戴头显判断。

模型导出后用 [verify_hub_fbx.py](<../../../Tools/hubart/verify_hub_fbx.py>) 独立核对（直接解析 FBX，按 Unity 导入规则换算，与 hub_layout.json 比对），预期 `[hub-fbx] anchors 3 of 3 within 2 cm`、`RESULT PASS`。锚点 `Anchor_Board_<id>` 在每块板的 Canvas 中心，必须是模型根的直接子节点（4.0 导出器开 bake_space_transform 时只转换根的直接子节点）。网格名 `Art_Hub_<颜色>`，材质名 `Hub_<颜色>`。

Unity 侧（HubAssembler.Room）：

- 实例化模型为场景根 `HubRoomArt`，放在 HubCenter，删掉模型上的 Collider（墙和地面的碰撞来自 placement 导入），渲染器标 BatchingStatic + OccludeeStatic；按材质名换成 `Art/Hub/Materials/Hub_<颜色>.mat`（Standard，颜色取自 hub_layout.json）。三个 `Anchor_Board_*` 与布局比对，偏差超过 2 cm 记 PROBLEM。报告行 `hub art anchors: 3 of 3 within 2 cm`。
- 场景里确保正好一个 EventSystem（StoryMode 底板已有；没有就加 EventSystem + StandaloneInputModule）。
- 每块板放一个面板 Prefab 实例（场景根 `HubBoard_<id>`，位置 = HubCenter + 板 Canvas 中心，旋转 = 板偏航，缩放 0.001）。同名但不是该 Prefab 实例的旧物体（旧版生成的 Canvas）删掉换成实例。Prefab 缺失记 PROBLEM 并指向施工图，不在代码里重建面板。
- 写实例覆盖：`LevelCard_i` 的 HubStartButton.launcher / levelIndex = i、`Number` 文字 = i+1；多余卡片设为不激活；每个箭头的 HubSettingButton.settings；HubPanelDisplay 的全部文字引用（按施工图物体名）；HubRoomHost.levelButtons（卡片根）与 promptText（`Text_Prompt`）。
- 回读：每个面板根 World Space Canvas、VRCUiShape、GraphicRaycaster、BoxCollider、非 UI/UiMenu 层、缩放 0.001–0.01、TMP 不挡射线且有字体、无旧 Text、Selectable 的 Navigation 全为 None；每个按钮 Button + 射线 Image、自身无 Collider、onClick 恰好一条 `SendCustomEvent("Press")` 指向本物体的 backing UdonBehaviour。
- `HubRoomLights`：三盏暖色点光（范围 9 m、无阴影）挂在吊灯下。
- 删除旧的 `HubPads`、`HubLevelPanel`、`HubPrompt`。
- 合集里把回合的 `startPrompt` 写成空（开局靠墙上卡片）；StoryRoundController 在提示为空时不显示 `startPromptText` 这一行。独立 StoryMode 场景不经过这段装配，保留原提示。回读：代理和 Udon 表里都必须为空，否则 PROBLEM。
- 新增根：`HubSettings`（HubSettingsHost，`vrInput` 接场景唯一 VrStickMoveInput，`listeners` = 面板显示）、`HubRecords`（HubRecordBook）、`HubPanelDisplay`。

## Unity 操作员：装配与验证

1. 同步源码（含 `Art/Hub/HubRoomArt.fbx`、`Art/Hub/Fonts/HubPanelSans-Bold.otf` 与 `OFL.txt`、`Runtime/VRCKatamari.Runtime.asmdef` 新增的 `Unity.TextMeshPro` 引用）；退出 Play，确认没有未保存的源场景修改。不要重启 Editor，不碰 ProjectSettings.asset。让 Unity 导入、编译 C# 与 UdonSharp，检查 Console。.meta 由 Unity 生成。编译后 Linux 侧的 `csharp_check` 需要 `--refresh` 取新 csproj。
2. FBX 导入设置：Scale Factor 1、Convert Units 开、Bake Axis Conversion 关（默认值）。
3. 按 [HUB_PANEL_PREFAB](<HUB_PANEL_PREFAB.md>)「字体」生成 `HubPanelSans SDF.asset`（记 Characters missing，应为 0），再按「搭建步骤」建三个 Prefab，并按其第 4 步回读。
4. 打开 AllMapsHub 场景，执行 `VRC Katamari/Hub/Assemble All-Maps Hub`（大厅装配后自动跑骑乘装配）。预期 `problems=0`；报告里应有 `hub art anchors: 3 of 3 within 2 cm`；缺 Udon variables 时按已有流程：编译 → 保存 → 重开 → 重建。
5. 执行 `VRC Katamari/Hub/Check Room Wiring`。预期 `0 problem(s), 4 level card(s), 4 level entry/entries, 24 setting arrow(s)`。层级：`HubRoomArt`、`HubBoard_Levels/Help/Settings`（蓝色 Prefab 实例图标）、`EventSystem`（一个）、`HubRoomLights`、`HubSettings`、`HubRecords`、`HubPanelDisplay`；没有 `HubPads`/`HubLevelPanel`/`HubPrompt`。
6. Inspector 回读：`LevelCard_0..3` 的 HubStartButton.levelIndex = 0/1/2/3（蟹座、野餐、地图0、地形实验室），Button On Click 一条：目标 = 同物体 UdonBehaviour、函数 SendCustomEvent、参数 `Press`；`SettingRow_NN/Less` delta=-1、`/More` delta=+1、settingId=NN；HubSettings.vrInput = 场景唯一 VrStickMoveInput，VrStickMoveInput.settings = HubSettings；HubRecords.levelIds 与 launcher 同序；HubRoom.settings = HubSettings。
7. EditMode：HubSettingsMathTests、HubRecordMathTests、HubPanelTextMathTests、HubPanelCharsetTests、HubRoomMathTests 及现有 Hub/Story 测试；报告发现/通过/失败数。
8. Play（ClientSim）：桌面视角对准面板应出现 UI 指针；鼠标左键点各按钮，日志：点蟹座卡片 `[hub-panel] level card 0 pressed`，随后是 launcher 的开始日志；点设置板语言行 ▶ 两次，三块板与提示行依次变英文、日文，日志 `[hub-settings] vrck.set.language=1/2`；点 VR 操作方式 ▶，日志 `[input] stick mode=simple`，操作表 VR 栏的移动行变成简单模式文案。音乐/提示音等待接项值能改、能保存（行上不显示待接标记）。面板上中日文无方块字。
9. 跑一局蟹座到 Result：日志 `[hub-record] level=_ref_cancer d=… n=… best=True plays=1`，回大厅后蟹座卡片显示「最好 / 上次 / 已玩 1」；在 Running 中 Respawn 放弃的一局不记录。重进世界（Build & Test 或上传后）记录与设置仍在。
10. 原有返回/重试/Respawn 验收（下文与 HUB_LOOP_PLAN）照旧；结果卡按钮不在本次改动范围内。

### 截图与读数

- 大厅俯视全景（整间房、三块面板、灯光），附 `HubRoomArt` Transform 与锚点报告行。
- 出生点玩家视角（HubSpawn，眼高约 1.6 m）看选关板：四张卡片完整、不镜像、不截字；另在离选关板 2 m、3 m 各一张；怎么玩板与设置板正面各在 2 m、3 m 各一张。附字体资产名、几个 TMP 的 Font Size（关名 150、设置行 66）、面板根缩放 0.001。
- ClientSim 里鼠标悬停卡片（高亮）与点击后的 Console 日志截图。
- 切换语言后三块板各一张（英、日）。
- 结果卡与回大厅后视角同前；回大厅后蟹座卡片的记录行。

Blender 预览（本机渲染，面板位置用奶油色平板代替，不代表 Unity 实际效果）：`/tmp/hub_art_overview.png`、`/tmp/hub_art_spawn_view.png`、`/tmp/hub_art_levels_{2,3}m.png`、`/tmp/hub_art_help_{2,3}m.png`、`/tmp/hub_art_settings_{2,3}m.png`。

## owner VR 复测

在真实客户端戴头显：出生时能读四关名称和目标行，走近两侧板 2–3 m 能读规则、操作表和设置行；激光指向后扳机能点中卡片和设置箭头，改语言/操作方式立即生效，退出重进仍保留；在大厅走动不会误入；扳机每次只启动所指一关。蟹座和野餐各跑一局：完整结果可读8秒、回大厅按钮可提前回、再来一次不会被旧计时拉走；回房后恢复正常身高/移动并可立即选另一关。地图0和地形实验室各按一次回大厅。记录是否看到两次瞬移、是否晕、不缩放/不锁座；有问题附设备、Avatar、日志与截图。

## 未决与验证边界

当前改动未在 Unity/Udon/Play/Build & Test/VR 中验证。退座/返回未到时每1秒复查/恢复，不会逐帧狂发传送；无本地玩家/无大厅落点则报错，不假装成功。多人本地岛开关不互通，仍不在本轮范围。面板已改为 VRChat 世界空间 UI Prefab（VRC_UiShape + uGUI Button + TMP），Prefab 尚未搭建，VRChat 客户端里的激光指针与点击未验证；FBX 轴向只在本机按导出设置推算，导入后的锚点比对是验证点。PlayerData 读写、客户端语言码、OnPlayerRestored 时序都未在真实客户端验证。没有新增 Riding 接口要求，调用现有 RequestExit/IsLocalPlayerSeated/TrySeatLocalPlayer。

## Respawn：放弃并回大厅

本地 OnPlayerRespawn先AbortRound（Running才需冻结，忽略allowAbort界面许可），取消待开始/自动入座/旧返回/重试，再按同一个BeginReturn退座并返回。结果呈现器DismissForReturn隐藏卡，不出放弃结果。AbortRound立刻使state离开Running，所以UpdateHoldInLevel不再把人拉回关卡。重复Respawn仍只保留最新一次返回。

证据：官方[OnPlayerRespawn](https://creators.vrchat.com/worlds/udon/graph/event-nodes/#onplayerrespawn)说明菜单Respawn会触发本地事件；[Station](https://creators.vrchat.com/worlds/components/vrc_station/)提供ExitStation并说明Station可阻止常规离座，但没有承诺菜单Respawn必然先退出站。所以不能把收到Respawn或自己清掉seated缓存当成退座证据。当前Riding的seated只由OnStationExited清，IsExitPending持续到回调；launcher等seated=false才移动。RequestExit取不到有效请求时用已有VRCStation.ExitStation后备；不修改Riding文件。

本地底板序列化回读：StoryMode场景spawns的fileID1425292521就是HubSpawn，旧位置(1200,.1,-2.8)、spawnRadius=0。本次装配器每次显式把场景描述符唯一spawns指向HubSpawn、半径0、朝向AlignPlayerWithSpawnPoint，再将HubSpawn设为(1200,.1,.8)。未读取Windows生成AllMapsHub的当前场景，因此操作员必须在重建后回读VRCWorld.spawns[0]的对象引用/坐标，不以底板证据冒充合集实测。

新增验收：在蟹座/野餐Running、自由练习Running、结果等待、回大厅退座中各从VRChat菜单Respawn。预期Running先round aborted，seated确认清除后回HubSpawn，active island0/4，卡不可见，后续没有hold-in-level传送。再点同关应重新唤醒并复位，道具与计时重新开始。模拟延迟OnStationExited：结果卡出现恢复提示且每秒检查、座位组件负责重发；卡上再来一次取消旧返回，确认退座后才重新开始。截图附Respawn前后视角、state、seated、IsExitPending、isParked与pending读数。

## 字段重建的依据

Linux工作区没有安装的Packages UdonSharp源码，不能确认目标1.x的FormerlySerializedAs对backing Udon变量也有效。读取上游固定commit的[UdonVariableStorageInterface](https://github.com/vrchat-community/UdonSharp/blob/aecbdbb7fc52c6f53fec2f4d314f0c11ffddb40c/Packages/com.vrchat.UdonSharp/Editor/Serialization/StorageInterfaces/UdonVariableStorageInterface.cs)：GetVariable/GetElementType按当前变量名查publicVariables/fieldDefinitions；该读写层没有旧名回退。这不是「所有版本绝不支持」的证明，所以本实现不冒充已验证迁移支持：直接删除旧字段，由Assemble All-Maps Hub写入levelButtons、CopyProxyToUdon并验证新变量存在。仅刷新脚本而不重新装配不能验收。
