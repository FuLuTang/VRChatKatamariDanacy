# 吸到东西时的物品提示（跟随视角 HUD）

现行设计。代码：`Runtime/BallLayers/CatchName/`（`CatchNameMath` 纯规则、`CatchNameDisplay` Udon 宿主），测试 `Tests/EditMode/BallLayers/CatchNameTests.cs`，接线 `Editor/Story/RideUxAssembler.cs` 的 `AssembleCatchName`，名字导入 `Editor/LevelBuild/PlacementImporter.CatchNames.cs` + `ReferenceCatchNames.cs`。

## 原作行为

依据：截帧（REROLL NS 版、WLK REROLL+ 中文版、PS2 版）与 WLK REROLL+ v20230608 的 IL2CPP 字段与 `UIMonoNameDisplay.Start` 常量。

- 屏幕左下角半透明深色圆底，圆里是刚吸到的物件 3D 模型在慢转（`UILastCatchMonoDisplay`，单独相机渲染到 RawImage，`ROT_PER_SEC = -60`）。
- 圆下方物件名，逐字打出：2 帧/字（`_NEXT_CHR_WAIT`），打完停留 105 帧（`_DISP_WAIT`）；≤5 字与更长名字两套文本（`_CHANGE_CHR_ = 5`，`Under5Text/Over5Text`）。
- 每次吸附都触发；同帧多件按 `u8WndDispPrio`（`mYm_MonoInfo_WndDispPrio`）择一；不排队，新的替换旧的。小模型一直保留到下一件。
- 吸到瞬间从物件屏幕位置拉一条渐隐线到圆框（`MonoGetLine`），约 0.1–0.3 s 消失。
- 名字来自 `TextCommonTable` 的 `name`（9 种语言，含简中）。

## 我们的做法

| 项 | 做法 | 理由 |
|---|---|---|
| 位置 | 跟随视角 HUD：视线下方 15°、偏左 12°，距眼 1.4 m（随骑手眼高缩放），圆底名义视角 7° | VR 没有屏幕角落；按 Meta 规范「要读的面板放视线下方 10–20°、1.2–1.5 m」，偏左呼应原作左下角 |
| 跟随 | 复用 `PushCueMath.HeadYaw / StepAnchorYaw / TryEvaluate`：只跟头部 yaw，死区 27°，0.4 s 追上 | 与推进箭头同一套数学，不另写；`TryEvaluate` 的仰角守卫放宽到 (−90°, 90°) |
| 朝向 | `CatchNameMath.FacingRotation(yaw, elevation)`：面板正对眼点（含俯仰） | 在视线下方时面板上缘朝人倾，读起来不斜 |
| 小模型 | 被吸物件网格的缩小副本：4 个预建槽位借用原物件的 `sharedMesh/sharedMaterials`，按包围盒居中、最长边缩到圆底直径的 0.75 倍，前倾 20°，−60°/s 自转 | 见下节 |
| 名字计时 | 2/30 s 一个字，打完停 3.5 s 后隐去；模型留到下一件 | 原作帧数按 30 fps 换算（基准帧率是推断） |
| 替换 | 不同帧一律换最新；同帧取优先级高的，相同取后到的 | 原作规则；蟹座 496 件对上的物件 `u8WndDispPrio` 全为 0，所以实际等于「同帧取最后一件」，接口保留优先级参数 |
| 线 | `catchLine`（LineRenderer）可选，起点 0.3 s 内从物件收拢到圆底；装配器默认不建；低特效时不画 | 见下节 |
| 语言 | 跟设置里的语言项（中/英/日）；缺失时退回英文，再退回中、日 | 设置机制已有 |
| 开关 | 设置项 `CatchName`（id 12，`vrck.set.catchName`，默认开） | 跟随类 HUD 都应可关 |
| 显示条件 | 本地骑乘且回合进行中；离开骑乘或回合结束时清空 | 与推进箭头相同 |
| 桌面模式 | 同一套世界空间 HUD：桌面的头部追踪就是摄像机，位置在画面左下 | 不另做屏幕空间版本 |

### 小模型：为什么不用单独相机

- (a) 单独相机 + RenderTexture（原作做法）：VRChat 里每多一台启用的相机，就要多一遍剔除和渲染（VR 下还有立体问题：RenderTexture 是单眼图，贴在面板上没有深度）。相机的剔除遮罩还得为小模型单开一个层，而 VRChat 世界可用的用户层有限；RenderTexture 本身也是新资源。我们 R58–R60 还在查卡顿，不加这笔开销。
- (b) HUD 锚点处直接放缩小的网格副本（选用）：不新建任何资源，只多 1–3 个 draw call（蟹座 145 种物件里 141 种是单网格、最多 3 个网格节点、最多 356 三角形）。VR 里是真正的立体小模型。代价：小模型受场景光照，暗处会偏暗；槽位数固定（4），超过的部件不画并记一行警告。
- 骨骼动画物件（螃蟹等）的静态网格仍在原物件下（`SkeletalAnimationAssembler` 只关掉其渲染器），所以小模型显示的是静态姿态。`Skin` 子树用的是 SkinnedMeshRenderer，没有 MeshFilter，所以 `GetComponentsInChildren<MeshFilter>` 只取到静态网格（未在 Unity 里核对）。

### 白色虚线：为什么简化

原作是屏幕空间里从物件到角标的一条虚线。VR 里物件就在手边，HUD 在视线下方，两者之间的世界空间直线会穿过球和手臂，读起来更像一根杆子。所以只保留可选的一条实线（`LineRenderer`，起点在 0.3 s 内收拢到圆底），由操作员决定是否在场景里加；默认不建。低特效设置开启时不画。

## 名字数据

- 来源：WLK REROLL+ 名称表对到初代蟹座物件的本机参考表 `~/KatamariRef/wlkrr/tables/cancer_to_wlk.tsv`（511 件中 496 件有简中名；15 件、13 种 def 没对上，多为初代特有的建筑和招牌，只有英文/日文名）。
- 路径：参考关导入时，`PlacementImporter.SyncCatchNames` 在 `PickupFieldSync` 之后按 itemId 把三语名字写进 `PickupFieldHost` 的三列 `propCatchNamesZh/En/Ja`，并做 Udon 回读。缺表时只写 `original_defs.json` 的英文名，打一行汇总日志。
- 名字文本属于原作参考素材：只在参考关（`_RefUnlicensed` 下的场景）里写入序列化数组，不进 git、不进上传包。自制关卡这三列为空串，HUD 只显示小模型。
- 字体：固定文案（设置行「显示吸到的东西」）只用 `Tools/hubart/hub_panel_charset.txt` 已有的字。物件名是任意文字，用 TMP 默认字体；字体外的字由 VRChat 内置后备字体渲染（TMP Settings 的 fallback 为空，见 [VRChat TMP 页](https://creators.vrchat.com/worlds/components/textmeshpro/)）。Editor 里中文名显示方块属正常。

## 未定

- 原作「Something's Coming!」红环警告（同一控件的另一状态）没有做。
- 所有位置、尺寸、倾角都是待 VR 实测的起点。
- 原作计时的基准帧率（30 还是 60）没有核实。
