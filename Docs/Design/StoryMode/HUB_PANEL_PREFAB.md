# 大厅墙面板 Prefab 施工图

现行决定：大厅三块墙面板（选关、怎么玩、设置）和骑手面前的回合结算卡各是一个 Prefab，按 VRChat 官方的「可交互世界空间 Canvas」做法搭（owner 2026-11-18：「用『面板』啊」）。Prefab 由 Unity 操作员按本页用 MCP 搭建并保存；装配器只实例化、接场景引用、回读，不生成面板结构。行为与设置项见 [HUB_ROOM](<HUB_ROOM.md>)。

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
| 圆角块 | Image，Source Image = `Art/Hub/Sprites/HubRounded`（白色，128 px，9-slice 边框 48 px，导入设置自动），Image Type = Sliced，Fill Center 开，颜色 = 下表的色，Pixels Per Unit Multiplier = 48 / 圆角半径(mm)。下表「圆角」列直接给出 multiplier |
| 装饰 Image | Raycast Target **关**（只有按钮的 Image 开） |
| 文字 | 全部 `TextMeshProUGUI`：Font Asset = `HubPanelSans SDF`（见「字体」），Material 用该资产默认材质，Auto Size 关，Wrapping 开，Overflow = Overflow，Rich Text 关，Raycast Target **关**，Line Spacing 0，垂直居中（Middle）；水平对齐见各表 |
| 按钮 | `Button`：Interactable 开，**Transition = Sprite Swap**（四态精灵见「方向一外观」；Selected 用 Normal 那张），Target Graphic = 本物体的 Image（Image Type = Sliced、颜色白色，精灵自带颜色），**Navigation = None**；同一物体挂对应 U# 脚本；`On Click ()` 一条：Runtime Only，对象 = 本物体（选本物体上的 **UdonBehaviour** 组件），函数 `UdonBehaviour.SendCustomEvent (string)`，参数 `Press` |
| 字号 | Font Size（画布单位）= 字形 em 高度（毫米）。例：选关板标题 220 = 0.22 m |

## 方向一外观（配色、精灵、两种面板）

owner 2026-11-21 定方向一「暖木纸屋」：低饱和、有主次；面板分两种。**能点的板**（选关、设置）用浅纸面，按钮有厚度和落影；**只看的屏**（怎么玩、以后的对战屏）用深板岩色平面，没有厚度和落影，上面不放可点的东西。设计稿：`.work/hub_design_v3/board_levels.png`、`board_states.png`、`screen_view.png`（不进仓库）。

颜色（sRGB）：

| 名 | Hex | 用在 |
|---|---|---|
| 纸米 Paper | #ECE3CF | 能点的板的板面、卡片底、主按钮文字 |
| 灰泥白 Plaster | #E4DED0 | 板面底色（卡片之间露出的地方） |
| 燕麦 Oat | #E2D8C2 | 标签签、设置行条纹、次按钮 |
| 墨灰 Ink | #3D3F3B | 正文、名称、编号圆章 |
| 次要字 | #6A645A | 记录行、说明行 |
| 胡桃木 Walnut | #6A4E36 | 标题牌底 |
| 鼠尾草 Sage | #88A27E | 剧情卡左侧条 |
| 砖红 Brick | #B4614A | 对战卡左侧条与「对战」章 |
| 番茄 Tomato | #D9503C | 主按钮（开始） |
| 金黄 Gold | #E8C14C | 悬停外圈、「上次玩的」签 |
| 板岩 Slate | #34362F | 只看的屏的底；行底 #2A2C26 |
| 粉笔白 Chalk | #E9E4D6 | 只看的屏上的字；次要 #A9A496 |

精灵（`Tools/hubart/build_hub_sprites.py` 生成到 `Art/Hub/Sprites/`，同时把每张的尺寸和 9-slice 边框写进 hub_layout.json `sprites`；导入设置由 `HubTexturePostprocessor` 自动设：Sprite、Full Rect、Clamp、无 mipmap。颜色画在精灵里，Image 颜色保持白色）：

| 精灵 | 用途 | 边框 (左,下,右,上) px |
|---|---|---|
| `HubCard_{Normal,Highlighted,Pressed,Disabled}` | 关卡卡片 Button 的四态：常态纸面加落影；悬停多一圈金黄；按下落影几乎消失、下沉；禁用变灰 | 40,40,40,40 |
| `HubButtonGo_*`、`HubButtonVersus_*`、`HubButtonPlain_*`（各四态） | 有厚度的胶囊按钮：番茄（开始）、砖红（对战）、燕麦（设置箭头、翻页） | 56,40,56,40 |
| `HubRounded` | 白色圆角块，用 Image 颜色染色：板面、标题牌、标签签、设置行条纹 | 48,48,48,48 |
| `HubStrip_Story` / `HubStrip_Versus` | 卡片左侧条：鼠尾草实色 / 砖红斜纹（不靠颜色也能区分） | 0 |
| `HubBadge_Versus` | 对战卡的盾形章，上面叠 TMP「对战」 | 0 |

文字颜色：能点的板上「白」改用纸米，其余用墨灰；只看的屏上用粉笔白。

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
| Image（板面） | 圆角块，颜色：Levels / Settings 灰泥白 #E4DED0，Help 板岩 #34362F；圆角 24 mm → multiplier 2，Raycast Target 关 |

板尺寸与房间位置（装配器按 hub_layout.json 放置；HubCenter = 世界 (1200, 0, 0)；Canvas 平面在模型板框平面前 0.06 m，紧贴彩色边框前面）：

| 板 | 尺寸（画布单位） | 实例世界位置 | Y 旋转 | 板框（模型） |
|---|---|---|---|---|
| Levels | 6400 × 2700 | (1200, 1.55, 3.76) | 0° | 蜂蜜木 `Hub_FrameWood` |
| Help | 3200 × 2300 | (1195.146, 1.55, 2.411) | -50° | 板岩细框 `Hub_Slate`（只看的屏） |
| Settings | 3200 × 2590 | (1204.854, 1.55, 2.411) | +50° | 蜂蜜木 `Hub_FrameWood` |

缩放 0.001。读者站在 Canvas 的 -Z 一侧（出生点 HubSpawn (1200, 0.1, 0.8) 面向 +Z），看到的是正面、文字不镜像。

每块板第一个子物体是标题牌 `TitleBand`：圆角块，pos (0, 板高/2 − 200)，size (板宽 × 0.62, 360)，圆角 60 → multiplier 0.8，颜色 = 胡桃木 #6A4E36（Help 板用板岩行底 #2A2C26）；标题字用纸米（Help 用粉笔白）。Levels（高 2700）：pos (0, 1150) size (3968, 360)；Help（高 2300）：pos (0, 950) size (1984, 360)；Settings（高 2590）：pos (0, 1125) size (1984, 300)。标题字与标题牌同一个 y（Levels 1150、Help 950、Settings 1125）。

## HubBoard_Levels

```
HubBoard_Levels            (根，见上)
├─ TitleBand               Image
├─ Text_LevelsTitle        TMP
├─ Text_Prompt             TMP
├─ Text_Credits            TMP
├─ LevelCard_0             Image + Button + HubStartButton（+ UdonBehaviour）
│  ├─ StoryStrip           Image（剧情卡左侧条）
│  ├─ VersusStrip          Image（对战卡左侧条）
│  ├─ PreviewFrame         Image
│  ├─ Preview              Image（缩略图；现为占位色块）
│  ├─ NumberDisc           Image（剧情卡编号圆章）
│  ├─ Number               TMP
│  ├─ VersusBadge          Image + 子 TMP「对战」
│  ├─ StartPill            Image（剧情卡，番茄）
│  ├─ VersusPill           Image（对战卡，砖红）
│  ├─ Name                 TMP
│  ├─ Goal                 TMP
│  ├─ Record               TMP
│  └─ Start                TMP
├─ LevelCard_1 … LevelCard_5（同 LevelCard_0）
```

板级文字：

| 物体 | pos | size | 字号 | 对齐 | 色 |
|---|---|---|---|---|---|
| Text_LevelsTitle | (0, 1150) | 3800 × 320 | 200 | 居中 | 纸米 |
| Text_Prompt | (0, 830) | 6000 × 200 | 85 | 居中 | 墨灰 |
| Text_Credits | (0, -1235) | 6000 × 110 | 75 | 居中 | 墨灰 |

卡片（整张卡片就是按钮；卡片 Image 是唯一的射线目标）：

| 物体 | pos | size |
|---|---|---|
| LevelCard_0 | (-2150, 260) | 2000 × 900 |
| LevelCard_1 | (0, 260) | 2000 × 900 |
| LevelCard_2 | (2150, 260) | 2000 × 900 |
| LevelCard_3 | (-2150, -680) | 2000 × 900 |
| LevelCard_4 | (0, -680) | 2000 × 900 |
| LevelCard_5 | (2150, -680) | 2000 × 900 |

卡片底不再按卡换色：六张都用 `HubCard_*` 四态精灵（纸米），剧情 / 对战靠左侧条和角章区分。

**临时布局**：6 张卡片排成 3 列 × 2 行，大厅按方向一重做面板时会重新设计，这里只保证 6 关都能点。推算：板 6400 × 2700；三列 3 × 2000 + 2 × 150（缝）= 6300，左右各留 50；两行保持原 2×2 的行心 260 / −680 与卡高 900，所以与 Text_Prompt（y 830，高 200）和 Text_Credits（y −1235，高 110）的相对位置不变（行 1 占 −190…710，行 2 占 −1130…−230，缝 40）。卡片宽从 2950 缩到 2000，卡内子物体按下表重排；板不用加宽。

卡片 Image：Source Image = `HubCard_Normal`，Sliced，multiplier 0.9（卡片圆角约 46 mm），Raycast Target **开**；Button 的 Sprite State：Highlighted `HubCard_Highlighted`、Pressed `HubCard_Pressed`、Selected `HubCard_Normal`、Disabled `HubCard_Disabled`。`HubStartButton` 字段：`launcher` 留空、`levelIndex` = 卡片序号（0–5），两者装配器都会写；Prefab 里先填序号。卡片多于场景提供的关卡时，装配器把多余卡片设为不激活。

卡片子物体（pos 相对卡片中心；卡片 2000 × 900，x −1000…1000、y −450…450。左边是缩略图，右边是文字，右下是开始胶囊；同一父物体下按表的顺序排，后面的画在上面）：

| 物体 | 类型 | pos | size | 内容 |
|---|---|---|---|---|
| StoryStrip | Image | (-978, 0) | 44 × 900 | `HubStrip_Story`，Simple，白色，Raycast Target 关；装配器按关卡 `mode` 开关 |
| VersusStrip | Image | (-978, 0) | 44 × 900 | `HubStrip_Versus`，其余同上 |
| PreviewFrame | Image | (-560, 0) | 780 × 780 | `HubRounded` Sliced，燕麦 #CBC0A6，multiplier 1.2（圆角 40） |
| Preview | Image | (-560, 0) | 740 × 740 | `HubRounded` Sliced，multiplier 1.6（圆角 30）；**缩略图占位**：剧情卡灰绿 #9DAF7C、对战卡石灰 #A6A399。关卡截图做好后换成各关的精灵（新增图像资源，另做） |
| NumberDisc | Image | (-840, 280) | 150 × 150 | `HubRounded` Sliced，墨灰 #3D3F3B，multiplier 0.64（圆角 75 = 正圆）；剧情卡开、对战卡关 |
| Number | TMP | (-840, 280) | 150 × 150 | 字号 100，居中，纸米 #ECE3CF；文字 = 序号+1（装配器写）；剧情卡开、对战卡关 |
| VersusBadge | Image | (-800, 250) | 240 × 180 | `HubBadge_Versus`，Simple，白色；子 TMP `Label`：pos (0, 15)、size 240 × 120、「对战」字号 72、纸米、居中。对战卡开、剧情卡关 |
| Name | TMP | (450, 270) | 1000 × 200 | 字号 105，左对齐，墨灰 #3D3F3B |
| Goal | TMP | (450, 90) | 1000 × 140 | 字号 62，左对齐，墨灰 #3D3F3B |
| Record | TMP | (450, -110) | 1000 × 200 | 字号 56，左对齐，次要字 #6A645A（最多两行） |
| StartPill | Image | (680, -330) | 520 × 150 | `HubButtonGo_Normal`，Sliced，multiplier 1，**白色**（精灵自带颜色），Raycast Target 关；剧情卡开、对战卡关。它只是视觉，点击仍是整张卡 |
| VersusPill | Image | (680, -330) | 520 × 150 | `HubButtonVersus_Normal`，其余同 StartPill；对战卡开、剧情卡关 |
| Start | TMP | (680, -330) | 520 × 150 | 字号 72，居中，纸米 #ECE3CF；必须排在两个胶囊之后（画在上面） |

装配器按关卡 `mode` 开关 `StoryStrip`、`VersusStrip`、`NumberDisc`、`Number`、`VersusBadge`、`StartPill`、`VersusPill`（缺任何一个记 PROBLEM）。字宽核算：「怀旧关：蟹座小院」8 字 × 105 ≈ 840 < 1000；记录行最长约 18 字 × 56 ≈ 1010，略超时 TMP 换行（Wrapping 开，高 200 放得下两行）。

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
| Text_HelpTitle | (0, 950) | 1900 × 260 | 170 | 居中 | 粉笔白 |
| Text_HowTo | (0, 470) | 3000 × 780 | 78 | 左 | 粉笔白 |
| Text_ControlsTitle | (0, 0) | 2800 × 180 | 110 | 居中 | 粉笔白 |
| Text_ControlsVr | (-770, -500) | 1480 × 800 | 66 | 左 | 粉笔白 |
| Text_ControlsDesktop | (770, -500) | 1480 × 800 | 66 | 左 | 粉笔白 |

无按钮（但根上仍要 VRC Ui Shape：手机端 Focus View 与一致的检查）。

## HubBoard_Settings

```
HubBoard_Settings
├─ TitleBand
├─ Text_SettingsTitle
├─ Text_SettingsHint
├─ SettingRow_00           空 RectTransform
│  ├─ Stripe               Image（每行都有，奇偶两种颜色）
│  ├─ Label                TMP
│  ├─ Value                TMP
│  ├─ Less                 Image + Button + HubSettingButton（+ UdonBehaviour）
│  │  └─ Arrow             TMP「◀」
│  └─ More                 Image + Button + HubSettingButton（+ UdonBehaviour）
│     └─ Arrow             TMP「▶」
├─ SettingRow_01 … SettingRow_14
```

| 物体 | pos | size | 字号 | 对齐 | 色 |
|---|---|---|---|---|---|
| Text_SettingsTitle | (0, 1125) | 1900 × 260 | 150 | 居中 | 纸米 |
| Text_SettingsHint | (0, 900) | 2800 × 80 | 56 | 居中 | 次要字 #6A645A |

行：`SettingRow_NN` 是空 RectTransform，pos (0, y)，size 3000 × 130；NN = 设置 id（00–14，顺序见 HUB_ROOM「设置项」）。`y = 760 − 135 × NN`（第一行 760，最后一行 −1130）。竖向占位（板高 2590，上下沿 ±1295）：标题牌 975…1275，提示行 860…940，第一行 695…825，最后一行 −1195…−1065；相邻两块之间至少留 35 mm。

每一行都有 `Stripe`，从行首一直铺到箭头后面，读的人横着扫过去不会串行：偶数行燕麦 #E2D8C2，奇数行浅一档 #EEE9DE。斜着看板时，左边的标签和右边的值离得远（标签从 x −1480 起，值在 x 860），没有底条的行容易看错行。

行内子物体（pos 相对行中心）：

| 物体 | 类型 | pos | size | 内容 |
|---|---|---|---|---|
| Stripe | Image | (0, 0) | 2950 × 130 | 圆角块（HubRounded，Sliced，multiplier 0.96），颜色：偶数行 #E2D8C2、奇数行 #EEE9DE；Raycast Target 关；行的第一个子物体 |
| Label | TMP | (-680, 0) | 1600 × 130 | 字号 66，左对齐 |
| Value | TMP | (860, 0) | 620 × 130 | 字号 70，居中 |
| Less | Image + Button | (400, 0) | 220 × 130 | `HubButtonPlain_*` 四态（Sprite Swap），Sliced，multiplier 1.0，Raycast Target **开**；HubSettingButton：`settingId` = NN，`delta` = −1 |
| Less/Arrow | TMP | (0, 0) | 220 × 130 | 「◀」，字号 80，居中，墨灰 |
| More | Image + Button | (1300, 0) | 220 × 130 | 同上；`delta` = +1 |
| More/Arrow | TMP | (0, 0) | 220 × 130 | 「▶」，字号 80，居中，墨灰 |

`HubSettingButton.settings` 留空（场景引用，装配器写）。

## 结算卡 RideResultCard

骑手正前方的结算卡，用法与墙面板相同：Canvas 根 + uGUI Button，按钮 onClick → 本物体 backing UdonBehaviour 的 `SendCustomEvent("Press")`，没有自己的 Collider、不走 Interact。根组件、层、文字、按钮规则全部照「总约定」与「根物体」。

| 项 | 值 |
|---|---|
| Prefab 路径 | `Assets/VRC_Katamari/Prefabs/Story/RideResultCard.prefab`（新建 `Prefabs/Story/` 目录） |
| 根物体名 | `RideResultCard` |
| 尺寸 | 1152 × 576（画布单位 = 毫米，实例缩放 0.001 → 1.152 × 0.576 m，与旧卡物理大小相同） |
| 板面 Image | 圆角块，纸米 #ECE3CF，圆角 60 → multiplier 0.8，Raycast Target 关 |
| BoxCollider | Is Trigger 开，Center (0,0,0)，Size (1152, 576, 20) |
| VRC Ui Shape | Allow Focus View 开 |
| 层 | 根和全部子物体 Default（0） |
| 根激活 | Prefab 里激活；装配器放实例时设为不激活（由 `StoryResultPresenter` 在 Presenting/Result 打开） |

```
RideResultCard              (根：RectTransform、Canvas、CanvasScaler、GraphicRaycaster、VRCUiShape、BoxCollider、Image)
├─ ResultText               TMP
└─ Buttons                  空 RectTransform（回合控制器只在 Result 打开它）
   ├─ RetryButton           Image + Button + StoryRetryButton（+ UdonBehaviour）
   │  └─ Label              TMP「再来一次」
   └─ ReturnButton          Image + Button + HubReturnButton（+ UdonBehaviour）
      └─ Label              TMP「回大厅」
```

| 物体 | 类型 | pos | size | 内容 |
|---|---|---|---|---|
| ResultText | TMP | (0, 70) | 1080 × 380 | 字号 56，居中（水平、垂直），墨灰，Wrapping 开；文字留空（运行时由 presenter 写） |
| Buttons | 空 RectTransform | (0, −200) | 1080 × 140 | — |
| Buttons/RetryButton | Image + Button | (−260, 0) | 440 × 130 | `HubButtonGo_*` 四态（Sprite Swap），Sliced，multiplier 1.0，Raycast Target **开**；onClick 一条 `SendCustomEvent("Press")` 指向本物体的 UdonBehaviour |
| RetryButton/Label | TMP | (0, 0) | 440 × 130 | 「再来一次」，字号 64，居中，纸米 |
| Buttons/ReturnButton | Image + Button | (260, 0) | 440 × 130 | `HubButtonPlain_*` 四态，其余同 RetryButton |
| ReturnButton/Label | TMP | (0, 0) | 440 × 130 | 「回大厅」，字号 64，居中，墨灰 |

两颗按钮的字段在 Prefab 里留空：`StoryRetryButton.controller`/`launcher`、`HubReturnButton.launcher` 是场景引用，装配器写。按钮字就是 `StoryRetryButton.Label`、`HubReturnButton.Label` 两个常量，字都在 `hub_panel_charset.txt` 里（`HubPanelCharsetTests` 覆盖）。`ResultText` 的正文（呈给国王、星、评语、回大厅提示）有不少字不在该字体资产里，由 VRChat 内置后备字体显示，Editor 里可能显示方块（与物品名 HUD 同一做法；是否要把正文字加入字符集见 HUB_ROOM「未决与验证边界」）。

装配（`VRC Katamari/Ride/Assemble Ride UX`，All-Maps 装配会自动跑）：在 `FollowRigRoot/RidePanels` 下放一个实例（名 `RideResultCard`，正前方、上 6°、距眼 1.6 m，缩放 0.001），同名但不是该 Prefab 实例的旧卡删掉换掉；删掉场景里的旧 `HubResultBoard`、`HubReturnCardButton` 和卡外的 `StoryRetryButton` 物体；写 `StoryResultPresenter.resultCardRoot`（卡根）与 `resultText`（`ResultText`）、`StoryRoundController.retryButtonRoot`（`Buttons`）、`RetryButton` 的 `controller`/`launcher`、`ReturnButton` 的 `launcher`；场景里没有 `HubLevelLauncher`（无大厅的 lab）时把 `ReturnButton` 设为不激活、`RetryButton` 直调回合重试。卡和子物体不参与 `RidePanels` 的层 8 统一（它们按本页在 Default）。这些都是实例覆盖，不要 Apply 回 Prefab。

回读：骑乘装配的 note 行与 `Check Room Wiring` 都调用同一个 `RideUxAssembler.ReadResultCard`，汇总行 `result card ok: retry wired, return wired`；检查项为：卡是该 Prefab 的实例且在 `RidePanels` 下、画布规则同墙面板（`BoardCanvasProblem`）、层 0、presenter/round 的三条引用在 proxy 与 Udon 表里一致、两颗按钮符合 `PanelButtonProblem`（onClick 恰好一条 `SendCustomEvent("Press")` 到自己的 UdonBehaviour、无 Collider、Navigation None、射线 Image）、按钮字等于常量、按钮的场景引用正确、全场景只有一个 `StoryRetryButton`、没有旧 `HubResultBoard`/`HubReturnCardButton`。

## 字体

只用一个 TMP 字体资产，三块板全部文字共用：

1. 字体文件：`Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans-Bold.otf`，由 [build_hub_font.py](<../../../Tools/hubart/build_hub_font.py>) 从 Noto Sans CJK SC Bold 按字符集取子集并改名（SIL OFL 1.1 的修改版要求改名；许可证全文在同目录 `OFL.txt`）。字符集文件 [hub_panel_charset.txt](<../../../Tools/hubart/hub_panel_charset.txt>)：可打印 ASCII + HubPanelTextMath 全部字面量 + 结算卡与回大厅提示 + hub_levels.json 关名 + ◀▶ + 指定保留字（`REQUIRED_EXTRA`，含方向一面板设计里还没写进代码的对战/观战用字与「–」），共 508 个字（非 ASCII 414）。改了面板文字就重跑 `python3 Tools/hubart/build_hub_font.py`（需要 fontTools），NUnit `HubPanelCharsetTests` 会在漏字时失败。
2. Unity：`Window > TextMeshPro > Font Asset Creator`：Source Font File = `HubPanelSans-Bold`；Sampling Point Size = Auto Sizing（或 64）；Padding = 6；Packing Method = Optimum；Atlas Resolution = 2048 × 2048；Character Set = **Characters from File**，Character File = `Tools/hubart/hub_panel_charset.txt`（先把它拖进 Project 作为 TextAsset，或临时复制到 `Assets/VRC_Katamari/Art/Hub/Fonts/hub_panel_charset.txt`）；Render Mode = SDFAA。Generate 后读数应为 Characters missing **0**（空格以外），保存为 `Assets/VRC_Katamari/Art/Hub/Fonts/HubPanelSans SDF.asset`。
3. 字体资产 Inspector：Atlas Population Mode = **Static**；Fallback Font Assets 列表留空。项目 TMP Settings 的 Fallback Font Assets 不改（官方建议移除 fallback 让 VRChat 内置字体兜底，这属于 ProjectSettings 以外的资产，但本面板字符已全部在静态图集里，不依赖兜底）。
4. 若 Generate 报缺字：说明字符集与字体不一致，重跑 build_hub_font.py 再生成；不要手改图集。

## 场景侧（装配器负责，Prefab 里不要放）

- EventSystem：场景里正好一个（StoryMode 底板已有一个 `EventSystem` 根物体）；没有时装配器加 `EventSystem` + `StandaloneInputModule`。
- 实例：根物体名 `HubBoard_<id>`，场景根，位置/旋转/缩放见上表。同名但不是该 Prefab 实例的旧物体会被删掉换成实例。
- 写入的场景引用：卡片 `HubStartButton.launcher`、`levelIndex`；每个箭头 `HubSettingButton.settings`；`Number` 文字；`HubPanelDisplay` 的全部文字引用（按本页物体名查找）；`HubRoomHost.levelButtons`（卡片根）与 `promptText`（Text_Prompt）。这些是实例覆盖（override），不要 Apply 回 Prefab。
- 回读（`problems` 计数）：根上 Canvas World Space、VRCUiShape、GraphicRaycaster、BoxCollider、非 UI/UiMenu 层、缩放 0.001–0.01；所有 TMP Raycast Target 关且有字体；没有旧 `Text`；所有 Selectable 的 Navigation = None；每个按钮：Button + 射线 Image、自身没有 Collider、onClick 恰好一条 `SendCustomEvent("Press")` 指向本物体的 backing UdonBehaviour；`HubSettingButton` 24 个，(id, 方向) 覆盖 12 × 2。

## 搭建步骤（Unity 操作员，MCP）

1. 同步源码后让 Unity 导入 `HubPanelSans-Bold.otf`、重编译 C#/UdonSharp（HubStartButton、HubSettingButton、StoryRetryButton、HubReturnButton 的 program asset 已存在，不需要新建）。
2. 按「字体」生成 `HubPanelSans SDF.asset`，记下 missing 数（应为 0）。
3. 建 `Assets/VRC_Katamari/Prefabs/Hub/`。在一个空场景或当前场景的空处按上面三棵树搭三块板：U# 组件用 UdonSharp 的方式加（`gameObject.AddUdonSharpComponent<T>()` 或 Inspector Add Component 后由 UdonSharp 自动配 backing UdonBehaviour）；按钮 onClick 目标选 **UdonBehaviour** 组件（不是 HubStartButton 代理组件）的 `SendCustomEvent(string)`，参数 `Press`。存为 Prefab 后删掉场景里的临时物体。
4. 每个 Prefab 打开后回读：根组件 7 个（RectTransform、Canvas、CanvasScaler、GraphicRaycaster、VRCUiShape、BoxCollider、Image）；Levels 6 个按钮（卡片）、Settings 每行 2 个按钮，每个按钮 onClick 一条、参数 `Press`；全部 TMP 的 Font Asset = HubPanelSans SDF。
5. 打开 AllMapsHub 场景，执行 `VRC Katamari/Hub/Assemble All-Maps Hub`，再执行 `VRC Katamari/Hub/Check Room Wiring`，预期读数见 [HUB_ROOM](<HUB_ROOM.md>)「Unity 操作员」。
