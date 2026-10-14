# 球模块独立测试对象：Lead 集成检查表

状态：待实现。此表不是场景已创建、编译或运行证据。

## 依据与不能重复的旧问题

已读现有 [T04 场景构建器](../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/Editor/T04PlatformLabBuilder.cs)。它是旧的单场景构建工具，不能为新球模块重跑或改写：
- 使用 NewScene(Single)，没有保护当前未保存场景；新集成工具必须先做零副作用安全检查。
- 仅创建带描述符的空 VRCWorld，没有显式移动参数设置。T04读回初始JumpImpulse=0；原因尚未证实。新测试世界必须核验移动配置，不能直接复制这条不完整初始化路线。
- 每模块独立源码、ProgramAsset与.meta；准备完并验证后才生成场景，不在场景修改一半时编译。

## 单一 Windows 集成槽

- 只允许一名明确指定的集成操作者；三套源码worker不SSH、不写Unity工程。
- 核实实际Unity命令行工程路径：D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity；用户拥有Editor，禁止关闭/重启或操作别的项目。
- 预期新目录 Assets/VRC_Katamari/Prototypes/BallLayerLabs/；与原T04 Platform目录分离。实际写入前在授权brief中明确此scope。
- 先检查正确场景、非Play Mode、非编译/更新、无Prefab Stage、无未保存修改；不满足则零写入退出，不替用户丢弃修改。
- Windows不得提交Git；Lead复核只集成白名单的源码/场景/ProgramAsset/meta与必要生成资产。ProjectSettings、Packages、账号设置保持不动。

## 测试对象清单（全部待搭建）

| 层 | 可视对象与验收行为 |
|---|---|
| GroundContact | 平面/斜面、球心、支持高度读数；抬升有限，杆撤除后不悬空 |
| RadialProxy | 32方向线段、长杆端点与4候选槽；吸杆变长、掉杆缩短、重建一致 |
| Sweep | 薄墙/盒/墙角轨迹；显示接触顺序、法线、剩余位移与卡住原因 |
| Attachment | 杯子与局部位姿坐标轴；球转动后稳定粘贴，增长不放大杯子，重复确认不复制 |
| SizeModel | 确认体积、核心/碰撞半径及语义直径分列；八倍体积两倍半径、上限与重置 |
| VisualRoll | 有表面标记的球；验证前进旋向及接触点无滑动，不以对称球假装验证旋向 |
| FollowRig | 水平参考架与滚动球分别画轴；roll/pitch隔离，重生可重置，眼高范围清楚 |
| RemoteProxy | 重复/乱序/旧世代模拟按钮、renderer计数；表现预算变化不改变账本，无玩法Collider |

## 每层独立证据与用户操作

1. 对应源码与实际新编译日志绑定，C#编译不等于Udon编译。
2. 序列化组件引用、UdonProgram和.meta完整，验证只改变被声明的对象。
3. 场景内每台有操作说明、重置按钮、当前输入/预期/实际读数和失败标志。
4. 先Desktop Build & Test；仅操作获准项目，不上传或发布。游戏内操作交给用户，不自动驾驶客户端。
5. 逐台记录用户观察；没有用户体验证据的层不标运行验收通过。
6. Unity物理与Python解析近似不同的地方明确列出；不把旧PASS或Python通过当Unity运行证据。
7. VR、多人、性能与T06正式接口冻结另行验收，不阻塞这些可独立观看的Desktop实验台。
