# T52 球层：确认体积到尺寸映射（DRAFT）

状态：离线源码准备；接口未冻结。保留等效球体映射、显式半径/直径/碰撞/语义类型和caller-supplied参数。

## 职责与数值安全

`size_model.py`只读外部确认体积并推导 `CoreRadiusWorldM`、`CollisionRadiusWorldM`、`SemanticDiameter`。不记账、不决定收益、不改写确认余额、不反推Mesh或附着物。封顶只限制呈现尺寸。`effective_volume_units`是有限呈现诊断量，不代表权威余额；大于浮点范围的呈现输入会饱和为最大有限诊断值，半径仍从log域精确比较/映射。

对数域避免有限输入的直接乘积、立方和求和溢出；碰撞半径、语义直径、半径/直径转换的最终乘法仍逐项检查，超出可表示范围明确拒绝。cap小于baseline明确拒绝。所有尺寸参数均显式输入、无产品默认值。

## C#草稿与Unity准备

`RuntimeDraft/SizeModelDraft.cs`仅使用Unity `Mathf`的对数/指数、float和结构体算法，目标为常见Udon可移植子集；不是UdonSharp组件，未编译。`TEST_OBJECT_SPEC.md`定义独立测试对象和对应的几何、操作、读数、失败、重置和层级。

## 证据与测试

原版源码未取得；历史专利不是原版代码，也不证明新版沿用。来源与访问边界见 [`Research/SOURCE_EVIDENCE.md`](Research/SOURCE_EVIDENCE.md)。本模型依据当前项目技术契约而非原作算法。

运行：`python3 -m unittest discover -s Docs/tests/ball_size_model -v`。覆盖球体几何关系独立oracle、8倍体积半径比例、半径/直径类型隔离、封顶不改外部账本、reset新值重算、有限极端中间量及非法参数。Unity/Udon/VR/网络均未验证。

## Unity 实现

- 纯逻辑：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/SizeModel/SizeModelMath.cs`，入口 `VRCKatamari.BallLayers.SizeModelMath.TryDerive`（6 个标量入参、4 个 `out float`）与 `TryCoreDiameter`。静态方法、无 struct、无分配、无静态字段；失败返回 false，四个 out 全为 0，不写部分结果。
- 三个量仍是互不相同的 out，且各自只有一个用途：`coreRadiusWorldM` 是视觉球芯半径（`V=4πr³/3` 等效几何，驱动 CoreMesh 缩放）；`collisionRadiusWorldM` 是唯一碰撞量（PVP 与环境简化判定半径）；`semanticDiameter` 是 UI 叙事直径与尺寸档位比较（可与物理米不同，绝不进入碰撞或玩法门槛——拾取门槛跟确认体积走）。Python 与 `RuntimeDraft` 的 `CoreRadiusWorldM`/`CollisionRadiusWorldM`/`SemanticDiameter` 包装结构体在 Udon 下不能存在，类型隔离改成函数形状隔离：映射只吃体积，`TryCoreDiameter` 只吃核心半径，四个结果各占一个 out。
- 数值：`Mathf` 对数域避免有限输入的乘积、立方与求和溢出；封顶到达后直接取 `maxCoreRadiusWorldM`，饱和精确，两次不同的大额确认给出同一显示尺寸；碰撞半径与语义直径的最终乘法逐项检查，超出 float 可表示范围明确拒绝。float 取代参考模型的 double，接受域相应改为“全部有限 float”。
- 封顶只作用于呈现：确认体积按值传入，没有可被截断或归零的账本；`effectiveVolumeUnits` 是有限呈现诊断量（体积单位，不是 m³）。本类无状态，reset 或交分重建=用新确认值再调一次。
- 测试：`Tests/EditMode/BallLayers/SizeModelTests.cs` 对应 `test_size_model.py` 全部 7 个用例（球体几何独立 oracle、8 倍体积 2 倍半径、半径/直径/碰撞/语义四值不等、封顶精确且与确认额无关、reset 只读新值、float 极值饱和与溢出拒绝、非法输入与 cap<baseline）；程序集 `VRCKatamari.Tests.EditMode`。
- 状态：Unity 编译、EditMode 测试与 U# 全量编译均 `NOT_RUN`；Python 仍是行为参考。
