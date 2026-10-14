# T52 独立测试对象规格（待Windows操作者实施）

状态：源代码准备完成；没有写入Unity工程、没有编译/运行或Prefab验证。Unity为用户Windows端Editor，项目 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，MCP未配置；后续由单一Windows集成操作者在评审后建立对象。

## 对象：SizeVolumeMappingLab

- **目的**：独立检查确认体积输入至核心半径、碰撞半径、语义直径的纯映射；证明显示封顶不写入/截断账本。
- **预期几何**：用明确测试参数 `baseline=1 volume unit, cubicScale=0.25 m³/unit, maxCoreRadius=3m, collisionRatio=0.8, semanticDiameterRatio=1.5`。这些仅为测试输入，不是产品默认值。球芯半径应满足等效几何 `V=4πr³/3`；碰撞球半径为核心半径的0.8，语义直径为核心直径的1.5。球芯Mesh可缩放；碰撞代理和UI语义读数分别显示，不能以直径冒充半径。
- **操作**：输入确认量0、增长至未封顶值、跨越上限的大数；分别调用reset的新账本值0；输入NaN/Inf/负数/bool仅在宿主测试驱动，不需场景UI模拟非数。
- **读数**：完整显示原始ledger confirmed与banked（只读测试桩）、有效呈现体积、coreRadiusWorldM、collisionRadiusWorldM、semanticDiameter及状态。越过显示上限后半径饱和，但ledger原值仍逐笔保留；reset应从新账本值重算。
- **失败指示**：半径与直径混字段、碰撞半径输出到核心半径、极端有限数出现NaN/Inf、半径封顶同步改账本、reset从上次显示尺寸缩放，均为FAIL。
- **重置**：测试驱动覆盖输入ledger快照为baseline-only并重新推导；不在模型中存储或清理账本。
- **Prefab层级**：`SizeVolumeMappingLab` → `BallPhysicsRoot` → `CoreMesh`；`CollisionRadiusGizmo`、`SemanticDiameterReadout`、`ConfirmedLedgerReadout`为根下独立子节点。BallPhysicsRoot与展示读数不继承CoreMesh的scale；不挂附着物或Renderer预算组件。
- **编译边界**：`RuntimeDraft/SizeModelDraft.cs` 是静态数学草稿，不是UdonSharp组件，未在Unity/Udon编译。Udon运行时白名单对 `System.Math`、双精度函数的实际支持尚未核实；Unity场景改用 `Mathf`/float前需保留有限中间量保护和oracle覆盖，并由操作者验证编译。所有参数仍是实验数据，不冻结项目尺度/单位契约。
