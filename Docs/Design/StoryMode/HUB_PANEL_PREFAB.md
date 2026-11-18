# 大厅墙面板 Prefab 施工图

现行决定：大厅三块墙面板（选关、怎么玩、设置）各是一个 Prefab，按 VRChat 官方的「可交互世界空间 Canvas」做法搭（owner 2026-11-18：「用『面板』啊」）。Prefab 由 Unity 操作员按本页用 MCP 搭建并保存；装配器只实例化、接场景引用、回读，不生成面板结构。行为与设置项见 [HUB_ROOM](<HUB_ROOM.md>)。

## 依据

- 官方 [VRC Ui Shape](https://creators.vrchat.com/worlds/components/vrc_uishape/)：Canvas 与 `VRC_UiShape` 在同一物体；物体层不能是 UI（用 Default）；Render Mode = World Space；缩放约 0.01（1 px = 0.01 m），不能是 1；需要 BoxCollider（自己加时要保证尺寸正确）；场景要有 EventSystem；可点元素要有 Raycast Target 开着的 Image、Canvas 要有 GraphicRaycaster，文字盖在按钮上时关掉文字的 Raycast Target；Navigation 设 None；用 TextMeshPro；`SendCustomEvent` 到 UdonSharp 时方法必须 public；Canvas 的 Z 正方向背向玩家。
- 官方 [UI Events](https://creators.vrchat.com/worlds/udon/ui-events/)：UI 事件能调用的 UdonBehaviour 方法只有 `RunProgram`、`SendCustomEvent`、`Interact`。
- 官方 [TextMesh Pro](https://creators.vrchat.com/worlds/components/textmeshpro/)：不用内置 Text；字体资产只导入需要的字符、降低图集分辨率。
- 本地知识库 `entities/world/scene-components/canvas.md`（同一 7 条条件）、`entities/api/ui.md`（Button.onClick 在编辑器里绑定，Udon 不支持运行时 AddListener）、`entities/world/whitelisted-world-components.md`（Canvas、CanvasScaler、GraphicRaycaster、Image、Button、TextMeshProUGUI、EventSystem、StandaloneInputModule 均在白名单）。

## 总约定

| 项 | 值 |
|---|---|
| Prefab 路径 | `Assets/VRC_Katamari/Prefabs/Hub/HubBoard_Levels.prefab`、`HubBoard_Help.prefab`、`HubBoard_Settings.prefab` |
| 单位 | Prefab 内一律用「画布单位」= 毫米。根缩放由装配器写成 0.001（Prefab 资产里根缩放写 0.001 也可，装配器每次覆盖） |
| 坐标 | 所有子物体 Anchor Min/Max = (0.5, 0.5)、Pivot = (0.5, 0.5)；下表 pos = Anchored Position（相对父物体中心，x 向读者右、y 向上），size = Width × Height；Pos Z = 0；旋转 0；缩放 1 |
| 层 | 根和所有子物体都在 **Default** 层（0） |
| 绘制顺序 | 同一父物体下按表中顺序排列（后面的画在上面） |
| 圆角块 | Image，Source Image = 内置 `UISprite`（`UI/Skin/UISprite.psd`，边框 10 px），Image Type = Sliced，Fill Center 开，Pixels Per Unit Multiplier = 10 / 圆角半径(mm)。下表「圆角」列直接给出 multiplier |
| 装饰 Image | Raycast Target **关**（只有按钮的 Image 开） |
| 文字 | 全部 `TextMeshProUGUI`：Font Asset = `HubPanelSans SDF`（见「字体」），Material 用该资产默认材质，Auto Size 关，Wrapping 开，Overflow = Overflow，Rich Text 关，Raycast Target **关**，Line Spacing 0，垂直居中（Middle）；水平对齐见各表 |
| 按钮 | `Button`：Interactable 开，Transition = Color Tint（Normal #EDEDED、Highlighted #FFFFFF、Pressed #BFBFBF、Selected #EDEDED、Disabled #C8C8C8 α0.5、Fade 0.1），Target Graphic = 本物体的 Image，**Navigation = None**；同一物体挂对应 U# 脚本；`On Click ()` 一条：Runtime Only，对象 = 本物体（选本物体上的 **UdonBehaviour** 组件），函数 `UdonBehaviour.SendCustomEvent (string)`，参数 `Press` |
| 字号 | Font Size（画布单位）= 字形 em 高度（毫米）。例：选关板标题 220 = 0.22 m |

颜色（hub_layout.json `palette`，sRGB）：

| 名 | Hex | 名 | Hex |
|---|---|---|---|
| Cream | #FFF5DB | Navy | #212B61 |
| Pink | #FF8CB8 | White | #FAFAF7 |
| Sky | #6BC7FF | Orange | #FF9E38 |
| Lemon | #FFDB40 | Leaf | #5CB84C |
| Mint | #73E0A8 | Peach | #FFD6B8 |
| Grape | #9E73F2 | Tomato | #FF614C |

文字颜色：表里写「白」的用 White，其余用 Navy。

## 根物体（三块板相同）

根物体名 = Prefab 名（`HubBoard_Levels` / `HubBoard_Help` / `HubBoard_Settings`），组件：

| 组件 | 字段 |
|---|---|
| RectTransform | Width × Height = 板尺寸（下表），Pivot (0.5, 0.5) |
| Canvas | Render Mode = World Space；Event Camera 留空；Sorting Order 0 |
| CanvasScaler | Dynamic Pixels Per Unit 1，Reference Pixels Per Unit 100 |
| GraphicRaycaster | Ignore Reversed Graphics 开，Blocking Objects None |
| VRC Ui Shape（`VRC.SDK3.Components.VRCUiShape`） | Allow Focus View 开（手机/平板可放大） |
| BoxCollider | Is Trigger 开，Center (0,0,0)，Size = (板宽, 板高, 20)（画布单位，20 = 2 cm 厚） |
| Image（板面） | 圆角块，颜色 Cream，圆角 160 mm → multiplier 0.0625，Raycast Target 关 |

板尺寸与房间位置（装配器按 hub_layout.json 放置；HubCenter = 世界 (1200, 0, 0)；Canvas 平面在模型板框平面前 0.06 m，紧贴彩色边框前面）：

| 板 | 尺寸（画布单位） | 实例世界位置 | Y 旋转 | 板框颜色（模型） |
|---|---|---|---|---|
| Levels | 6400 × 2700 | (1200, 1.55, 3.76) | 0° | Pink |
| Help | 3200 × 2300 | (1195.146, 1.55, 2.411) | -50° | Sky |
| Settings | 3200 × 2300 | (1204.854, 1.55, 2.411) | +50° | Mint |

缩放 0.001。读者站在 Canvas 的 -Z 一侧（出生点 HubSpawn (1200, 0.1, 0.8) 面向 +Z），看到的是正面、文字不镜像。

每块板第一个子物体是标题色带 `TitleBand`：圆角块，pos (0, 板高/2 − 200)，size (板宽 × 0.62, 360)，圆角 150 → multiplier 0.0667，颜色 = 板框颜色（Levels Pink、Help Sky、Settings Mint）。Levels：pos (0, 1150) size (3968, 360)；Help/Settings：pos (0, 950) size (1984, 360)。

## HubBoard_Levels

```
HubBoard_Levels            (根，见上)
├─ TitleBand               Image
├─ Text_LevelsTitle        TMP
├─ Text_Prompt             TMP
├─ Text_Credits            TMP
├─ LevelCard_0             Image + Button + HubStartButton（+ UdonBehaviour）
│  ├─ Inner                Image
│  ├─ PreviewFrame         Image
│  ├─ Preview              Image
│  ├─ StartPill            Image
│  ├─ Number               TMP
│  ├─ Name                 TMP
│  ├─ Goal                 TMP
│  ├─ Record               TMP
│  └─ Start                TMP
├─ LevelCard_1 … LevelCard_3（同 LevelCard_0）
```

板级文字：

| 物体 | pos | size | 字号 | 对齐 | 色 |
|---|---|---|---|---|---|
| Text_LevelsTitle | (0, 1130) | 4000 × 320 | 220 | 居中 | 白 |
| Text_Prompt | (0, 830) | 6000 × 200 | 85 | 居中 | Navy |
| Text_Credits | (0, -1235) | 6000 × 110 | 75 | 居中 | Navy |

卡片（整张卡片就是按钮；卡片 Image 是唯一的射线目标）：

| 物体 | pos | size | 卡片底色（Image） | Preview 色块 |
|---|---|---|---|---|
| LevelCard_0 | (-1550, 260) | 2950 × 900 | Orange | Leaf |
| LevelCard_1 | (1550, 260) | 2950 × 900 | Lemon | Grape |
| LevelCard_2 | (-1550, -680) | 2950 × 900 | Sky | Tomato |
| LevelCard_3 | (1550, -680) | 2950 × 900 | Leaf | Navy |

卡片 Image：圆角 140 → multiplier 0.0714，Raycast Target **开**。`HubStartButton` 字段：`launcher` 留空、`levelIndex` = 卡片序号（0–3），两者装配器都会写；Prefab 里先填序号。卡片多于场景提供的关卡时，装配器把多余卡片设为不激活。

卡片子物体（pos 相对卡片中心）：

| 物体 | 类型 | pos | size | 内容 |
|---|---|---|---|---|
| Inner | Image | (0, 0) | 2870 × 820 | White，圆角 100 → 0.1 |
| PreviewFrame | Image | (-1050, 0) | 780 × 780 | 与卡片底色相同，圆角 60 → 0.1667 |
| Preview | Image | (-1050, 0) | 720 × 720 | Preview 色块，圆角 40 → 0.25 |
| StartPill | Image | (1120, 280) | 560 × 220 | Tomato，圆角 110 → 0.0909 |
| Number | TMP | (-1050, 0) | 720 × 720 | 字号 360，居中，白；文字 = 序号+1（装配器写） |
| Name | TMP | (100, 260) | 1400 × 260 | 字号 150，左对齐 |
| Goal | TMP | (420, 20) | 2040 × 180 | 字号 90，左对齐 |
| Record | TMP | (420, -260) | 2040 × 320 | 字号 82，左对齐 |
| Start | TMP | (1120, 280) | 560 × 220 | 字号 100，居中，白 |

## HubBoard_Help

```
HubBoard_Help
├─ TitleBand
├─ Text_HelpTitle
├─ Text_HowTo
├─ Text_ControlsTitle
├─ Text_ControlsVr
└─ Text_ControlsDesktop
```

| 物体 | pos | size | 字号 | 对齐 | 色 |
|---|---|---|---|---|---|
| Text_HelpTitle | (0, 980) | 2800 × 260 | 170 | 居中 | 白 |
| Text_HowTo | (0, 470) | 3000 × 780 | 78 | 左 | Navy |
| Text_ControlsTitle | (0, 0) | 2800 × 180 | 110 | 居中 | Navy |
| Text_ControlsVr | (-770, -500) | 1480 × 800 | 66 | 左 | Navy |
| Text_ControlsDesktop | (770, -500) | 1480 × 800 | 66 | 左 | Navy |

无按钮（但根上仍要 VRC Ui Shape：手机端 Focus View 与一致的检查）。

## HubBoard_Settings

```
HubBoard_Settings
├─ TitleBand
├─ Text_SettingsTitle
├─ Text_SettingsHint
├─ SettingRow_00           空 RectTransform
│  ├─ Stripe               Image（仅偶数行）
│  ├─ Label                TMP
│  ├─ Value                TMP
│  ├─ Less                 Image + Button + HubSettingButton（+ UdonBehaviour）
│  │  └─ Arrow             TMP「◀」
│  └─ More                 Image + Button + HubSettingButton（+ UdonBehaviour）
│     └─ Arrow             TMP「▶」
├─ SettingRow_01 … SettingRow_10
```

| 物体 | pos | size | 字号 | 对齐 | 色 |
|---|---|---|---|---|---|
| Text_SettingsTitle | (0, 980) | 2800 × 260 | 170 | 居中 | 白 |
| Text_SettingsHint | (0, 700) | 2800 × 100 | 60 | 居中 | Navy |

行：`SettingRow_NN` 是空 RectTransform，pos (0, y)，size 3000 × 140；NN = 设置 id（00–10，顺序见 HUB_ROOM「设置项」）。y：00 = 580，之后每行 −155（01 = 425，02 = 270，03 = 115，04 = −40，05 = −195，06 = −350，07 = −505，08 = −660，09 = −815，10 = −970）。

行内子物体（pos 相对行中心）：

| 物体 | 类型 | pos | size | 内容 |
|---|---|---|---|---|
| Stripe（id 偶数行才有） | Image | (0, 0) | 2950 × 140 | Peach，圆角 50 → 0.2 |
| Label | TMP | (-680, 0) | 1600 × 140 | 字号 66，左对齐 |
| Value | TMP | (860, 0) | 620 × 140 | 字号 70，居中 |
| Less | Image + Button | (400, 0) | 220 × 130 | Grape，圆角 65 → 0.1538，Raycast Target **开**；HubSettingButton：`settingId` = NN，`delta` = −1 |
| Less/Arrow | TMP | (0, 0) | 220 × 130 | 「◀」，字号 80，居中，白 |
| More | Image + Button | (1300, 0) | 220 × 130 | 同上；`delta` = +1 |
| More/Arrow | TMP | (0, 0) | 220 × 130 | 「▶」，字号 80，居中，白 |

`HubSettingButton.settings` 留空（场景引用，装配器写）。

## 字体

只用一个 TMP 字体资产，三块板全部文字共用：

1. 字体文件：`Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans-Bold.otf`，由 [build_hub_font.py](<../../../Tools/hubart/build_hub_font.py>) 从 Noto Sans CJK SC Bold 按字符集取子集并改名（SIL OFL 1.1 的修改版要求改名；许可证全文在同目录 `OFL.txt`）。字符集文件 [hub_panel_charset.txt](<../../../Tools/hubart/hub_panel_charset.txt>)：可打印 ASCII + HubPanelTextMath 全部字面量 + hub_levels.json 关名 + ◀▶，共 436 字（非 ASCII 342）。改了面板文字就重跑 `python3 Tools/hubart/build_hub_font.py`（需要 fontTools），NUnit `HubPanelCharsetTests` 会在漏字时失败。
2. Unity：`Window > TextMeshPro > Font Asset Creator`：Source Font File = `HubPanelSans-Bold`；Sampling Point Size = Auto Sizing（或 64）；Padding = 6；Packing Method = Optimum；Atlas Resolution = 2048 × 2048；Character Set = **Characters from File**，Character File = `Tools/hubart/hub_panel_charset.txt`（先把它拖进 Project 作为 TextAsset，或临时复制到 `Assets/VRC_Katamari/Art/Hub/Fonts/hub_panel_charset.txt`）；Render Mode = SDFAA。Generate 后读数应为 Characters missing **0**（空格以外），保存为 `Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans SDF.asset`。
3. 字体资产 Inspector：Atlas Population Mode = **Static**；Fallback Font Assets 列表留空。项目 TMP Settings 的 Fallback Font Assets 不改（官方建议移除 fallback 让 VRChat 内置字体兜底，这属于 ProjectSettings 以外的资产，但本面板字符已全部在静态图集里，不依赖兜底）。
4. 若 Generate 报缺字：说明字符集与字体不一致，重跑 build_hub_font.py 再生成；不要手改图集。

## 场景侧（装配器负责，Prefab 里不要放）

- EventSystem：场景里正好一个（StoryMode 底板已有一个 `EventSystem` 根物体）；没有时装配器加 `EventSystem` + `StandaloneInputModule`。
- 实例：根物体名 `HubBoard_<id>`，场景根，位置/旋转/缩放见上表。同名但不是该 Prefab 实例的旧物体会被删掉换成实例。
- 写入的场景引用：卡片 `HubStartButton.launcher`、`levelIndex`；每个箭头 `HubSettingButton.settings`；`Number` 文字；`HubPanelDisplay` 的全部文字引用（按本页物体名查找）；`HubRoomHost.levelButtons`（卡片根）与 `promptText`（Text_Prompt）。这些是实例覆盖（override），不要 Apply 回 Prefab。
- 回读（`problems` 计数）：根上 Canvas World Space、VRCUiShape、GraphicRaycaster、BoxCollider、非 UI/UiMenu 层、缩放 0.001–0.01；所有 TMP Raycast Target 关且有字体；没有旧 `Text`；所有 Selectable 的 Navigation = None；每个按钮：Button + 射线 Image、自身没有 Collider、onClick 恰好一条 `SendCustomEvent("Press")` 指向本物体的 backing UdonBehaviour；`HubSettingButton` 22 个，(id, 方向) 覆盖 11 × 2。

## 搭建步骤（Unity 操作员，MCP）

1. 同步源码后让 Unity 导入 `HubPanelSans-Bold.otf`、重编译 C#/UdonSharp（HubStartButton、HubSettingButton 的 program asset 已存在，不需要新建）。
2. 按「字体」生成 `HubPanelSans SDF.asset`，记下 missing 数（应为 0）。
3. 建 `Assets/VRC_Katamari/Prefabs/Hub/`。在一个空场景或当前场景的空处按上面三棵树搭三块板：U# 组件用 UdonSharp 的方式加（`gameObject.AddUdonSharpComponent<T>()` 或 Inspector Add Component 后由 UdonSharp 自动配 backing UdonBehaviour）；按钮 onClick 目标选 **UdonBehaviour** 组件（不是 HubStartButton 代理组件）的 `SendCustomEvent(string)`，参数 `Press`。存为 Prefab 后删掉场景里的临时物体。
4. 每个 Prefab 打开后回读：根组件 7 个（RectTransform、Canvas、CanvasScaler、GraphicRaycaster、VRCUiShape、BoxCollider、Image）；Levels 4 个按钮、Settings 22 个按钮，每个按钮 onClick 一条、参数 `Press`；全部 TMP 的 Font Asset = HubPanelSans SDF。
5. 打开 AllMapsHub 场景，执行 `VRC Katamari/Hub/Assemble All-Maps Hub`，再执行 `VRC Katamari/Hub/Check Room Wiring`，预期读数见 [HUB_ROOM](<HUB_ROOM.md>)「Unity 操作员」。
