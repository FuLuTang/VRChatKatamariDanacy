# 滚球性能分项诊断

VR 里球主控约 5 ms 是待拆分的实测反馈，不是本模块测量结论。先量球主控、拾取和物件运动，再决定是否收窄碰撞候选、调整移动调度或减少球上装饰。此探针只测本客户端的几个 Udon 更新段，不测整帧、GPU、渲染线程或 Animator，也不是发布性能门禁。

## 接入 Build & Test

1. Unity 操作员先导入并编译 `BallPerformanceDiagnostics` 的 UdonSharp ProgramAsset。场景加一个常驻、默认启用的 `BallPerformanceDiagnostics` 行为；采集本身默认关。把受测本地 `KatamariBallMotor`、关卡 `PickupFieldHost`、`MotionHost` 的 `performanceDiagnostics` 指向同一个实例；不要接机器人、第二颗球、装饰场或常驻的另一关宿主。多关共用实例时，每轮只开一关，从关内重新开采。默认窗口 5 秒，可把 `intervalSeconds` 调为 1–60 秒（越界仍用 5 秒）。
2. 面板用世界空间 Canvas + TextMeshProUGUI，绑定 `panelRoot` 和 `panelText`（可均留空，仅打日志）。`panelRoot` 初始不激活，只包含读数，不能包住采集器或开关。按调试观察点/现有 HUD 锚点放置，实看不挡球且文本不裁断；不要把它挂到会滚转的球壳。中文字符要由 TMP 字体及回退覆盖。按钮应常可访问，可用既有 VRChat Canvas 按钮接本地 `SendCustomEvent("ToggleSampling")`，或在行为所在物体加 Collider，直接用 VRChat 的 `Interact`。无需 Hub 设置项，没有持久化、`UdonSynced` 或网络事件。
3. 进 **Build & Test** 后自行开启，看到「正在收集首个窗口」，约 5 秒后有数据。先取基线：小球、空旷处滚；再取靠墙/地形、密集道具、吸了较多物件的窗口。每个工况保持 2–3 个窗口；同路线同条件再关探针复走一次，用头显/运行时的帧时间量仪比较开销。关时面板立即隐藏、停止打印；重开不继承旧窗口。改变关卡、重置球或休眠后丢弃跨越该事件的窗口。
4. Windows PCVR 查 `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt` 最新文件，筛 `[BallPerf]`，发卡顿前后几个完整窗口；日志无个人标识，无需给整份日志。或者拍完整面板，包含窗口号、球径和候选数。一起附构建/关卡、哪段路线/是否正在贴墙或吸附、头显刷新率/渲染分辨率、实例人数和卡顿时点。[VRChat 调试日志说明](https://creators.vrchat.com/worlds/udon/debugging-udon-projects/)也提供客户端内调试日志叠层。

## 怎样读数

每个时段一条 `[BallPerf]`。毫秒记为 `每次均值/单次最大值(样本数)@每帧均值`，`@` 是本窗口该项总耗时除以采集器看到的帧数（包含没有固定步或没有扫描的帧）。计数记为 `末值/均值/最大值`；`—` 是没有样本，`0` 才是有样本且读数为零。主控的「每帧步数」取它自己的 `Update` 里累计出来的固定步，含零步帧，不是 Unity `FixedUpdate`。峰值属于各自的样本，不保证各列在同一帧出现；窗口末球径也不是耗时峰值那一刻的球径。

| 列 | 计量范围 |
| --- | --- |
| 三路合计、球主控、拾取帧、物件运动 | 合计以 `LateUpdate` 汇合三个宿主同帧的计时；球主控含固定步循环和姿态写入；拾取帧包住 `UpdateFrame` 与近邻缓存失效；物件运动计已就绪的 `MotionHost.Update`，含观察点、分类与行驱动。 |
| 输入、球运动、接触、吸附、步末 | 每固定步分别计 `StepInput`、运动/冲刺请求、`StepContactV2`、候选判决/账本提交/尺寸重算/增径推出，以及滚动与跟随架/冲刺收尾。 |
| 附着形状、场地近窗、地形格、地形支撑 | 接触的子段：`PrepareNeedleShape`，`FillNearWindow`（Arena 注册的静态 OBB 及道具碰撞代理），两次 `CollectTerrainCandidates` 调用，以及已有的支撑探针耗时。地形格是**每次查询**而非每步一笔。 |
| 球姿态写入、附着事件 | 一帧只写一次球的父级姿态（球上物件随父级滚动，不逐件写）；消费已吸附事件、更新物件归属、挂接姿态计在拾取宿主。仅为 CPU 脚本侧耗时，不测渲染/蒙皮/Transform 的后续引擎工作。 |
| 道具障碍、近邻扫描、择候选、提交候选 | 道具障碍含静态行复核和障碍刷新，可能包含近邻扫描；近邻扫描只计真实 `GatherNearRows`，不把复用缓存算成第二次扫描；择候选与提交只计实际执行（含撤回落选/刷新已排队候选），正式吸附账本提交仍在球主控。 |
| 步内待审、近邻候选、选中候选 | 判决前 motor 队列占用；拾取筛选遍历的近场集合长度（可能共用障碍的较宽半径）；该轮筛选选中数。三者含义不同，不能互换。 |
| 球径米、静态 OBB、已附着 | 分别为语义直径、登记在 motor 静态表里的 OBB 数（不是本步相交数）、该拾取场已附着物件数（不是账本总数或 Renderer 数）。 |
| 支撑遍历三角、支撑精测三角 | 复用 motor 的 `terrainTrianglesTested` / `terrainFullEvals`：前者含粗筛访问，后者数通过 XZ AABB 且进入几何精测的次数。多探针可以重复测同一三角。 |
| 求解输入候选、求解退回全扫 | 前者是送进 `ContactSolveMath.TryStep` 的有效网格候选列表长度，不是求解器内迭代/子步的三角测试数；无列表不写候选值，另在后者记 1（有网格列表时记 0）。没有运行地形查询时四个地形计数都无样本。 |

先比较 「三路合计」以及三条宿主的 `@`，再沿最贵的宿主往下看。**子段已包含在父段里，不再加一次**；帧均可比较经常做的小工作与偶尔做的重工作，单次峰值则能看到偶发尖峰。字串与 `Debug.Log`/面板重排在报告帧，不计进三路时间，仍可能影响真实帧率。采样开启时内部探针成本会计入外层，连续边界的记账成本也会落入下一段；结果用于缩小排查范围，不是无探针的绝对性能基准。

未覆盖：`TerrainFieldHost.SampleSupportHeight` 是落下道具走 `PropFallHost` 的查询；`LevelArenaHost` 自身只做注册，查询走 motor 的近窗。求解器里的重复地形粗筛/精测目前仍合在「接触」内，未单独打点或计数。若它成为热点，下一步在实际候选/全扫的三角遍历汇合处计数，进精确几何前计第二次数，按求解调用用 `ref int` 回收，仍不要在三角循环里读时钟或打日志。

## 时钟和开销

新探针使用无实例的 `Stopwatch.GetTimestamp()` 和首次开启读取的 `Stopwatch.Frequency`，先以 64 位整数做差，再换算毫秒。旧地形支撑使用已有的 `Time.realtimeSinceStartupAsDouble` 差值；照常用、不为面板再读两次。两种时钟的时间戳从不混减。`Stopwatch` 暴露证据来自本项目离线 SDK 引用缓存的 Graph/Wrapper 符号，仍须在实际 SDK 的 [Class Exposure Tree](https://creators.vrchat.com/worlds/udon/udonsharp/class-exposure-tree/) 和 Udon 编译核对。真实精度取决于系统计时器及 extern 开销；三位小数只是显示单位，不承诺 1 微秒分辨率。若现场构建环境不暴露所需方法，统一把新探针时间戳和换算改为 [`Time.realtimeSinceStartupAsDouble`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Time-realtimeSinceStartupAsDouble.html) 差值；如只暴露 `float` 版，运行一小时后的单精度步距约 0.244 ms，等时戳会使短段读零，不能当作没有成本。

关闭时，新增路径只检查可选引用/本地开关（采集器 `LateUpdate` 立即返回），不新增钟读、样本数组写入、字符串或 UI 更新；不关闭历史上已有的地形与 v2 诊断。开启后一次分配 4 个长度为 30 的数组（合计 600 字节数值元素，不含数组/VM 开销），每帧复用；无逐物件/三角探针。新增成本未测，需用开关对照确认。
