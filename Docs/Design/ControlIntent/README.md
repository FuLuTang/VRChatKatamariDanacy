# T45 统一输入意图层（DRAFT）

> **DRAFT 草案，不冻结公共接口。** 本文和参考实现是离线主机侧设计实验，T06 及平台门禁通过前不得视为公共契约或 Udon 实现。没有验证 VR 真机、舒适性、Udon runtime、Unity 或网络；对应结果均为 **UNVERIFIED / NOT_RUN**。Unity 运行时由后续任务实现与验收。

## 状态分层

### 【已定】（仅限现有文档明确范围）

- 移动层只接收规范化意图，不读取具体设备；VR A、VR B、Desktop 是输入适配器来源，而不是运动算法分支。[玩法设计第4节](../../01_GAME_DESIGN.md#L33-L65) [技术契约](../../02_TECHNICAL_CONTRACTS.md)
- Jump 上升沿语义是请求使用当前道具；长按不重复，菜单/退出/KO应清理输入。Desktop Interact 使用鼠标左键，已由 T04 PC 手测；E 无效。[T04 VR/PC核对](../../Evidence/StationPlatform/vr_checklist.md#L17-L27)
- T04 PC 实测：坐姿 Jump 不退座，只计一次，长按不重复；坐姿时本地交互提示应关闭。此事实仅限记载的 PC 测试。[T04结果](../../Evidence/StationPlatform/pc_results.md#L12-L16) [提示记录](../../Evidence/StationPlatform/pc_results.md#L30-L37)

### 【DRAFT 草案】

参考实现把物理读数映射成 `InputFrame`，再产生有序 `Intent`。统一意图集合如下；数值取值是实现边界，不是已冻结 API。

| 意图 | 字段 | 范围/表示 | 语义 |
|---|---|---|---|
| `Move2D` | `timestamp,x,y` | 时间≥0；x/y ∈ [-1,1] | 非零时每帧持续采样；零向量不发事件。运动层按意图消费。 |
| `Look2D` | `timestamp,x,y` | 同上 | 非零时持续采样；零向量不发事件。不得假设视线等于移动方向。 |
| `UseItemOrJump` | `timestamp` | 单调非负时间 | Jump 上升沿一次；长按不重复，松开再按可再次触发；坐姿仍仅是道具/提示请求，不产生实际跳跃。 |
| `InteractOrSeat` | `timestamp` | 边沿事件 | Interact 上升沿一次；由上下文决定交互目标或入座，不在意图层硬编码目标。 |
| `ExitStation` | `timestamp` | 边沿事件 | 主动退出 Station 的请求；与平台 Respawn 分离。 |
| `Respawn` | `timestamp` | 边沿事件 | 平台重生通知/请求，不等价于退出比赛。 |
| `ComfortTurn` | `timestamp,value` | `value` 为 bool | 设置变更时发一次；不是持续转向轴。与Look2D/yaw来源优先级仍待决定。 |

规范轴值在适配时限制到 [-1,1]。动作边沿采用 `debounce_seconds=0.2` 草案参数（与 T04 测试量级相近，非测得的最佳阈值）。时钟应由调用方提供非负单调时间；异常倒退需上层处理。事件顺序按每帧 Move2D、Look2D、ComfortTurn、动作项输出，动作项顺序为 UseItemOrJump、InteractOrSeat、ExitStation、Respawn。

### 三种来源映射表

| 来源 | Move2D | Look2D | UseItemOrJump | InteractOrSeat | ComfortTurn |
|---|---|---|---|---|---|
| A：双控制器 / 手部追踪 | 两个虚拟推杆活动件读数，经本地水平投影/限幅适配为二维意图；手部追踪可用性未验证 | 水平操作参考/观察轴；精确动作映射未定 | 用户当前绑定的 Jump 动作，上升沿；不写死型号按键 | VRChat Interact；控制器方式待验证 | 原生舒适转向状态/设置；事件兼容未验证 |
| B：原生VR | 左摇杆二维轴 | 右摇杆或水平观察/参考轴；不得假定原生 Comfort Turning 与轴可叠加 | 用户当前绑定的 Jump 动作，上升沿 | VRChat Interact；具体绑定待验证 | 独立设置状态；开/关差异及优先级未验证 |
| Desktop | 键盘二维移动（WASD 为设计例） | 鼠标观察/水平参考输入 | Space / Jump 设计映射，上升沿 | **鼠标左键（T04 PC 已测）** | 不适用 |

A 双控制器和 B 原生 VR 的映射不代表设备实测；A 的 Grip 术语冲突也不在本草案裁定。各输入来源都必须先产出相同的规范字段，运动层不分辨设备类型。

### 【未决】

- VR 真机 A/B 映射和 Station 行为：T07/T08（当前未验证）；手部追踪可用性：未测。
- 原生舒适转向开/关的事件差异、与 Look2D/right-stick 的冲突优先级：T04 步骤4未测。
- T04 输入实验总览另有9个 OPEN 项（Grip、观察参考、退出/Respawn恢复等）；本任务不代替逐项裁决。[9项 OPEN 总览](../../Preparation/InputExperiments/controls_overview.md#L47-L61)
- 去抖时间窗、时钟来源、输入边界、回合时间、结算细则、公平阈值和世界尺度仍是占位/待定，不可把本参考实现里的 0.2 秒当最终验收参数。详细未决项见[来源与未决问题](../../08_SOURCES_AND_OPEN_QUESTIONS.md)。

## 状态行为与安全边界

- 菜单打开：本地不输出任何游戏意图；仍跟踪物理按键按下态，菜单关闭后需先松开再按才可触发，避免长按被误认为新上升沿。
- 坐姿：Jump 的唯一动作意图仍为 `UseItemOrJump`；代码模型没有“物理跳跃”意图输出，因此不会把 Jump 转成跳起或退座。该行为参考 T04 PC 事实，不宣称其他平台已通过。
- Station 入座：本地交互提示应隐藏；`prompt_visible(seated, menu_open)` 是 host 侧可测策略函数，不等同 Unity UI 接入或验证。
- 退出、Respawn 是不同意图，清理玩家移动/身高/跳跃设置的责任留给具体生命周期模块。

## 参考实现和测试

- `intent.py`：Python 标准库，仅作为可运行的意图转换参考模型；输入已经由来源适配器映射成 `InputFrame`。
- `python3 -m unittest discover -s Docs/tests`：运行仓库全量测试（本交付新增测试在 `Docs/tests/control_intent/`）。
- 同一脚本化规范输入分别标记 A、B、Desktop，断言产物完整相同。此测试验证转换器的一致性，不验证真实设备适配器。

**明确不声称：** VR 舒适性、VRChat 真机输入兼容、Udon runtime、Unity 编译、网络一致性或任何性能/公平性实测通过。

## Unity 实现

- 纯逻辑：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Input/ControlIntentMath.cs`，入口 `VRCKatamari.Input.ControlIntentMath.TryProcess`——静态方法 + 调用方缓冲区 + `out` 计数，无 struct、无分配、无静态状态、无重载、不抛异常；边沿/去抖/舒适转向状态由调用方用 `ActionCount` 个 bool、`ActionCount` 个 float 和 2 个 bool 的数组持有，`TryReset` 对应 `reset()`。失败返回 false 且 `intentCount=0`，不写部分结果。
- 行为严格取自 `intent.py`：轴限幅 [-1,1]、零向量不发事件；每帧顺序 Move2D→Look2D→ComfortTurn→UseItemOrJump→InteractOrSeat→ExitStation→Respawn；四个动作各自独立上升沿与独立去抖（默认 0.2 s 草案值）；菜单打开时不发意图，但锁存四个动作的按下态与舒适转向设置，关闭后必须先松开再按；`seated` 不入参（模型显式丢弃），只由 `IsPromptVisible(seated, menu_open)` 影响本地提示。时间戳为非负调用方时钟，每个意图沿用该帧时间戳。
- 来源 A/B/Desktop 只做路由校验：`ControlIntentSource` 枚举（`VirtualHandles`/`NativeVr`/`Desktop`）+ `IsSupportedSource`，对应 `SOURCE_BINDINGS` 的三个键；设备绑定描述仍只留在本文档表格，不写进运行时。
- 本层没有、也故意不补（模型未定义）：死区/滞回（本模型只有 [-1,1] 限幅；玩法设计 §4 的 A 模式“死区/灵敏度”是设备适配器设置，数值与语义未定）；世界坐标、前向、yaw 及头/手/摇杆的 yaw 授权（本层不产出 yaw；FollowRig 只接收调用方给的单一水平 yaw，Look2D 与原生舒适转向的优先级仍是未决项）；物理跳跃/下降请求（只有 `UseItemOrJump` 按键请求，没有 descend 意图）；磁铁/火箭选择、道具槽、单槽幂等（单槽由 `Docs/Design/Gameplay/round_model.py` 的 `_pickup_powerup` “已持有道具时拒绝拾取”与道具生命周期/账本负责，本层只产出按键请求）。这些不能从本实现推出契约。
- 唯一有意偏离：Python 对 NaN 轴会原样输出，本移植按项目约定拒绝所有非有限输入（返回 false）；Python 轴元组长度错误在本移植中不可表达（`Vector2`）。
- 测试：`Tests/EditMode/Input/ControlIntentTests.cs` 移植 `Docs/tests/control_intent/test_intent.py` 全部 6 个用例，并补充无输入、轴限幅/符号对称、逐动作去抖、去抖边界、重置、菜单锁存舒适转向、无效参数/缓冲区等不变量，共 20 个 NUnit 用例。注意 Python 测试在 t=0.5 的注释写“去抖窗口内”，实际 0.5−0.2=0.3 s 已超出 0.2 s，模型会发出第三次 `UseItemOrJump`；NUnit 按实际行为断言。
- 状态：Unity 编译、EditMode 测试与 U# 全量编译均 `NOT_RUN`；`.meta` 留给 Unity 操作者生成，本仓库未手写。Python 仍是行为参考。
