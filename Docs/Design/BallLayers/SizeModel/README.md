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
