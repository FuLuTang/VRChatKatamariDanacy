# 吸附物件覆盖球芯：我们的截图 vs 原版两帧（2026-11-15）

本目录只收三条参考截图与这份说明。它们回答的是一个**观感差异**：原版球在吸附了二三十件东西之后，看起来是「一堆东西」在滚；我们的球看起来仍是「一颗红球上粘了几件东西」。

> **边界。** 本批没有在 Unity 里跑任何东西（不是 Unity 操作者），也没有对原版做任何测量。下面凡涉及我们运行时的，都是**代码事实**（`文件:行`）；凡涉及原版画面的，是**目视估计**；凡把两边接起来的解释，都标 `[推断]`。原版两帧是第三方视频截图，**只作内部对照证据，不再分发**。

## 1. 三条图是什么

| 文件 | 尺寸 | sha256（本目录逐字节核对过） | 是什么 |
|---|---:|---|---|
| `ours_unity_ball_with_props.png` | 411×326 | `e4bb7030e648b2f6eb121beca34db761af27d7a6857645269da1eab67b3a9d23` | **我们自己的构建**（Unity 截图）。红色球芯，表面粘着几件道具：左侧一块灰色方箱（床/柜一类）、右上方一个木柜、右侧一部木梯、右下一条长凳、下方一条毯子/木板，另有小件若干。**大片红色核心直接可见**；好几件道具是**半埋**的——下半截陷进红色球面里，只露出上半截和棱角。 |
| `original_katamari_14cm0mm.png` | 1502×845 | `8efba66074088931b13c3fec14009db05ca47161215f0db1c7162af3917a7520` | **原作《块魂》（Katamari Damacy）游戏画面**，来自一段**三方视频录制**：左上角 HUD 显示球径 `14cm 0mm`，画面上叠着 bilibili 的界面元素（上传者水印、logo、点赞/分享按钮）以及原画面自带的 `1m` 尺寸参照和道具名标签 `Caramel`。球体由二三十件吸附物组成：成摞的蓝黄色小盒（火柴盒/电池盒一类）、绿色叶簇、瓦楞纸箱、焦糖色块等。 |
| `original_katamari_14cm9mm.png` | 1502×845 | `e9970fe911e8af7b6b55495cd9836b94bf19bcf2df949cbb66341a043b86b8cf` | 同一段录制的另一帧，HUD 为 `14cm 9mm`。球体由绿色棱角叶簇、黄色柑橘、蓝色罐头、紫色盒、白红包装、`おてもと`（筷套）等组成；画面里还有 `1.5m` 参照标注和道具名 `Octopus Sushi`。 |

**关于原版两帧的授权与来源：** 这两张是**第三方视频的截图**，仅作本项目**内部对照证据**保存，**不用于再分发**（仓库目前公开并不改变这一点：正式发布前应把它们移出公开发布物或替换为自制对照图）。本批**没有**记录、也没有核实该视频的 URL、标题与上传者，因此**本文不写任何来源链接**，后来的人也不要凭画面上的水印去补一个「大概是谁的视频」的出处。

## 2. 项目所有者的观察（转述，不是测量）

原话的要点，按他的意思转述（**不是逐字引用**）：

- **原版**：吸到 **20–30 件不算小的物件**之后，**球芯的颜色基本看不见了**；物件把整个球面盖住，**轮廓是这堆物件的外形**，大件带着棱和边往外支棱；滚起来的感觉是「**物件在托着球走**」，不是一颗光滑的球在滚。
- **我们**：**红色核心一直露在外面**，道具是「**陷进红色里**」的；看上去像球把道具吃进去了一半，而不是道具把球包起来。
- **他的怀疑**：可能是**视觉上的球比滚动用的碰撞球更大（或至少一样大）**；所以他直接问：**「粘东西/吸附环」那套碰撞，和滚动用的碰撞，是同一个吗？**

## 3. 审计到的代码事实（可复核）

来源：本轮只读审计 `/tmp/ball_audit/report.md` 的 C 节（三个体积）与 E 节（吸附物缩放/埋没）。下面是其中与这三张图直接相关的部分。

**（1）今天只有一个球：视觉球 = 碰撞球 = 核心球。**

- `SizeModelMath.TryDerive`（`Runtime/BallLayers/SizeModel/SizeModelMath.cs:126`–`192`）一次算出三个量：
  `coreRadius = min(cbrt(3·(baseline+confirmed)·c / 4π), maxCoreRadiusWorldM)`、
  `collisionRadius = coreRadius × collisionToCoreRadiusRatio`、
  `semanticDiameter = 2·coreRadius × semanticDiameterPerCoreDiameter`。
- 序列化比值：`collisionToCoreRadiusRatio = 1`、`semanticDiameterPerCoreDiameter = 1`（`Runtime/BallMotor/KatamariBallMotor.cs:193`–`197`）。
- 视觉网格缩放读的是**语义直径**：`coreMesh.localScale = Vector3.one * motor.GetSemanticDiameterWorldM()`（`Runtime/BallPresentation/BallView.cs:56`–`61`）。
- 水平阻挡/吸附 reach 用的是**碰撞半径**（`KatamariBallMotor.cs:945`、`PickupAbsorbMath` reach），接地高度用的是**核心半径**（`KatamariBallMotor.cs:1114`）。
- 两个比值都是 1，所以 `coreRadius = collisionRadius = semanticDiameter/2`：**「滚动球 / 挡墙球 / 视觉球 / 吸附 reach 球」今天完全重合**。审计的数值表：D = 0.30 / 1.10 / 2.20 m 时，核心半径 0.150 / 0.550 / 1.100 m。
- 地雷：`BallView` 读语义直径、马达内部读核心半径，**只要有人把 `semanticDiameterPerCoreDiameter` 调成 ≠1，视觉球和物理球立刻分家**（视觉变大、碰撞不变）。

**（2）吸附物保持吸附瞬间的球局部位姿，之后不会被往外推。**

- `PickupFieldHost.AttachProp`（`Runtime/Pickup/PickupFieldHost.cs:976`–`1028`）：`AttachmentMath.TryWorldPoseToLocal(ballCenter, visualRollRotation, …)`（`:1005`）把世界位姿换成球局部位姿，然后 `prop.SetParent(attachmentRoot)` + 写 `localPosition` / `localRotation`（`:1013`–`1015`），并禁用该道具的 Collider。
- 这一段**只写位姿**。运行时有 `localScale` 写入的只有两处：`BallView.cs:61`（写 `coreMesh`）与 `PushDirectionCue.cs:137`（推球浮标），**没有一处写吸附物的 scale**，所以道具的世界尺寸原样保留。
- 球长大以后，**没有任何代码重写附着物的局部位姿**：核心半径变大、道具位置不动 → 球面自己「涨」上去，把道具的内侧部分吞掉。径向代理外壳（`maxProxyProtrusionPerCoreRadius = 0.35`、绝对护栏 0.30 m，`KatamariBallMotor.cs:1050`、`:1114`）只影响**接地高度**，不会把道具推出去；角点连「超出核心 1 cm」都不到的长物件还会在代理登记时被跳过（`minProxyProtrusionM`）。

**（3）没有任何「埋没」策略。**

- 本地吸附物只有「吸附失败才 `HideProp`」（`PickupFieldHost.cs:1059`）。
- `RemoteProxyMath` 里确实有 `buried` 槽位（`Runtime/BallLayers/RemoteProxy/RemoteProxyMath.cs:73`、`:345`），但那是**远端球的装饰摘要**，与本地附着物无关。
- 也就是说：**今天没有「埋进去就隐藏 / 淡出 / 缩回 / 沿径向推出」的任何策略**，道具被吞掉的一半就在球体里。

## 4. 原作侧查到了什么（公开来源，二手）

来源：`/tmp/orig_physics/report.md`（只做了公开文字 + 专利原文 + 官方手册 OCR 的检索，**没有实机、没有拆包**）。

- **碰撞外形是「一个」等效体积代理，不是「球 + 每件吸附物各自碰撞」** `[专利]`（US7402104B2）：碰撞模型 `Mh` 是一个包围盒，其内接一个**体积 = 球体积 + 所有已吸附物体积之和**的等效球；吸附后按新体积整体放大。→ 凸出物**不改变碰撞外形**。
- **另有一套独立的滚动模型** `[专利]`：`Mr` = **16 根固定针 + 4 根可选针**，**针尖就是接地点**；吸附物把附近的针往外伸长，于是球「凹凸不平地滚」。这套模型**只影响滚动/贴地，不进碰撞判定**。
- **显示层按 display level 消隐小物件** `[专利]` + `[社区实测]`：比球小得多的物件**不绘制**；社区侧记录「尺寸小的物件，球一变大就马上消失」。**数据层仍算它的体积**（否则球会缩水），所以是「显示层 cull、数据层保留」。
- **吸附物不缩放** `[专利]`：吸附瞬间记录物件当时的位置与姿态，挂成子物体，球长大不重设尺寸。
- 旁证（媒体实测）：吸附物会「戳地」，球被顶得颠、歪着滚，一根长杆能撑杆跳——说明**接地点由凸出物决定**，而不是碰撞外形由凸出物决定。

## 5. 三张图能证明什么、不能证明什么

| 判断 | 级别 | 依据 |
|---|---|---|
| 原版这两帧的 HUD 球径是 **14.0 cm** 与 **14.9 cm** | **实测（读图）** | 两帧左上角 HUD 文字 `14cm 0mm` / `14cm 9mm` |
| 我们这一关的**起始**球径是 **D = 0.30 m**（派生值 0.29974 m） | **实测（代码/关卡数据）** | `KatamariBallMotor.cs:193`–`194` 的 `baselineVolumeUnits × cubicMetersPerVolumeUnit = 0.0141 m³`；`Docs/Design/Levels/story_01_bench_size/placement.json` 的 `ballDiameterStart = 0.2997` |
| 原版这两帧里，球芯颜色**基本看不见**，轮廓由物件构成 | **目测** | 两帧画面；`14cm9mm` 帧中部还能看到一小片粉/洋红（底球或粉色物件，未核实） |
| 原版两帧的吸附物数量**二三十件** | **目测估计** | 肉眼点数在 20–30 的量级；遮挡与重叠使确切件数不可数，没有逐件核对 |
| 我们的截图里红色核心**成片可见**；可辨认的吸附物约 **8 件**，多件半埋 | **目测** | 本图；对红色像素做 RGB 阈值粗算约占整幅 411×326 画面的 14%（13.4 万像素里约 1.87 万），只作「大片可见」的脚注，不是标定测量 |
| 我们今天**没有**「视觉球比碰撞球大」这回事：三个比值都是 1，视觉球 = 碰撞球 = 核心球 | **实测（代码）** | §3(1) |
| 他问的「粘东西/吸附环的碰撞」和「滚动碰撞」**是同一个球** | **实测（代码）** | 吸附 reach 用 `collisionRadius`、墙用 `collisionRadius`、接地用 `coreRadius`，三者相等 |
| 「看起来像被吞进去一半」的机制不是半径不一致，而是**道具保持吸附时的位姿、球芯随后长大把它包住**，且**没有任何埋没/cull 策略** | **推断** | §3(2)(3)：位姿写入时机与「之后无人重写」都可读；但观感归因没有人做过 A/B 验证 |
| 原版「物件托着球」与我们缺的那一层（接地点由凸出物决定、凸出物有独立滚动模型）**同构** | **推断** | 原作专利的针模型 vs 我们的端点云代理；我们只对「长物件」登记代理，且只影响接地高度 |
| 原版 14 cm 的球已经能挂住二三十件东西，说明**件数/覆盖度**在原版里是球径的函数；我们 0.30 m 的球挂约 8 件就露出大片球芯 | **推断** | 两边的「件数」与「球径」都可读，但两边的道具尺寸分布、吸附门槛、关卡内容完全不同，不能直接换算 |

**明确不声称：** 本文没有证明我们的实现「错了」，也没有给出原版的任何数值规律（原版这两帧离我们的起始球径还差一半，且关卡、道具表、门槛都不同）。它只固定一条**可复核的观感差距**：同一句「吸附之后球长什么样」，两边差得很远，而差距不在碰撞半径上。

## 6. 相关文档

- 吸附即「保持吸附瞬间位姿、不随球长大缩放」的契约：[`Docs/02_TECHNICAL_CONTRACTS.md` §2 / §3.4](../../../02_TECHNICAL_CONTRACTS.md)。
- 三个尺寸的定义与消费者：[`Docs/Design/BallLayers/SizeModel/README.md`](../../../Design/BallLayers/SizeModel/README.md)。
- 吸附/附着外观：[`Docs/Design/BallLayers/Attachment/README.md`](../../../Design/BallLayers/Attachment/README.md)。
- 原作侧的证据与我们的取舍：[`Docs/Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md`](../../../Research/KatamariBallRules/BALL_PHYSICS_IMPLEMENTATIONS.md)。
