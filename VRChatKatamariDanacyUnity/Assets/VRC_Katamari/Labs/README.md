# 实验场景（Labs）

本目录是被手动或脚本搭出来的实验场景，不是产品场景。每个场景有自己的说明；下面这一节是所有**脚本搭场景**共用的前置条件。

## 脚本搭场景的两件必备 SDK 组件

用 VRChat SDK 向导（`VRChat SDK → Show Build Control Panel` 里新建世界）建场景时，SDK 会自动补上下面两件。**用脚本（或手工摆）搭场景时必须自己补**，否则会出现一个很坑的不对称：**Play Mode 一切正常，构建必挂**。

### 1. `VRC.Core.PipelineManager`（挂在场景根对象上）

- **作用**：承载这个世界的身份与打包元数据 —— `blueprintId`、SDK 版本、内容校验。它是「出版元数据」，不是「运行元数据」。
- **缺了会怎样**：构建时报
  `BuildFailedException: The built scene has no pipeline manager component. There must be exactly one.`
- **为什么 Play Mode 不报**：Play Mode 走的是 **ClientSim 模拟器**，它只查询 `VRCSceneDescriptor` 与 spawn 点，然后直接驱动 Udon 虚拟机；`PipelineManager` 从头到尾没有被查询过。

### 2. `VRCSceneDescriptor.ReferenceCamera`（指向场景里的主相机）

- **作用**：模拟器/客户端用来取初始视角的参考相机。
- **缺了会怎样**：`[ClientSimSceneManager] Reference camera is not set in the SceneDescriptor!`（每次进入 Play Mode 一条警告）；对构建无影响。
- **为什么只在 Play 时出现**：它只影响 ClientSim 的视角初始化，构建管线不需要它。

### 不对称对照

| | Play Mode（ClientSim） | 构建（SDK 构建管线） |
| --- | --- | --- |
| 谁在驱动 | ClientSim 模拟器 → Udon 虚拟机 | SDK 打包成 `.vrcw` |
| 需要 `PipelineManager` | **否**，从不查询 | **是**，缺了直接 `BuildFailedException` |
| 需要 `ReferenceCamera` | 是（否则警告，视角可能不对） | 否 |
| 缺组件的表现 | 一切正常，最多一条警告 | 构建失败，Play Mode 里毫无征兆 |

### 给下一个搭场景的人

> 用 SDK 向导建世界会自动补 `PipelineManager` 和 `ReferenceCamera`；**用脚本搭场景必须手动补**，否则 Play Mode 一切正常、构建必挂。

在 `BallPlayground` 里这两件是补过的（`PipelineManager` 挂在根 `BallPlayground` 上，`ReferenceCamera` 指向 `PlaygroundCamera`）。

## 用编辑器脚本搭 UdonSharp 场景（MCP / execute_code）

2026-11-15 用 `execute_code` 直接搭场景时逐条撞出来的（Unity 2022.3.22f1、VRChat Worlds SDK 3.10.5 自带 UdonSharp）。核心结论：**编辑器脚本给对象加 UdonSharpBehaviour 不是 `AddComponent<T>()` 一句话的事**——朴素 `AddComponent<T>()` 只建出 proxy 组件，背后没有 `UdonBehaviour`、也没有 program asset。它在 Inspector 里看得见，游戏里永远不跑。当时 `Labs/StoryLevel` 的 10 个行为里有 5 个是这么来的，这也是前几次「它就是不动」事故的真因。

### 必做序列

1. **program asset 先就位**：`.cs` 旁边同名 `UdonSharpProgramAsset`（`.asset`），并和它的 `.meta` 一起提交。脚本写了、asset 没生成或没提交，后面全部白搭。
2. **先编译，再拷贝**：`UdonSharpEditorUtility.CompileAllCsPrograms(true, false)` 必须在任何拷贝之前跑。否则报 `Cannot run serialization on U# behaviour … with outdated behaviour version`。
3. **建 backing 组件**：同一个 GameObject 上 `AddComponent<UdonBehaviour>()`，再用 `SerializedObject` 写两个**字段**（不是属性）：`programSource` = 该 program asset；`serializedProgramAsset` = `programAsset.SerializedProgramAsset`。
4. **先填引用，再回写**：proxy 上的 `[SerializeField]` 引用全部设好，再 `UdonSharpEditorUtility.CopyProxyToUdon(proxy)`。
5. **从 Udon 侧读回验证**：读 backing `UdonBehaviour` 的 `publicVariables`，用 `TryGetVariableValue(string, out object)`。**按参数类型选重载**——只按名字反射会 `AmbiguousMatchException`。

> `UdonSharpEditorUtility.ConvertToUdonBehaviours(...)` 在本版本直接抛 `NotImplementedException`，不要用。

### 症状 → 原因

| 症状 | 原因 |
| --- | --- |
| 组件在 Inspector 里，进游戏什么都不做 | 只有 proxy，没有 backing `UdonBehaviour`（朴素 `AddComponent<T>()` 的直接结果） |
| `ConvertToUdonBehaviours(...)` 抛 `NotImplementedException` | 该 API 在本版本没实现，改走上面的手工序列 |
| `… with outdated behaviour version` | 拷贝之前没跑 `CompileAllCsPrograms(true, false)` |
| 跨脚本引用读出来是 `ride=UdonBehaviour (RideSeat)` 而不是组件 | **正常**。Udon 侧存的就是对方的 backing `UdonBehaviour`，不是 proxy |
| 编辑器 C# 调 proxy 的 public 方法（如 Play Mode 里 `motor.SetMoveAxes(...)`）对运行中的程序毫无影响 | Udon 状态不在 C# 侧。要驱动就走 Udon 事件，别从编辑器调 proxy 方法 |
| `programSource = null`，行为静默不跑 | program asset 或 `Assets/SerializedUdonPrograms/` 里的产物没带回仓库 |
| 视觉球停在作者高度、半个埋进地里 | motor 把**位置**写 `ballPhysicsRoot`、**旋转**写 `ballVisualRollRoot`；只接了 roll root 就没人写位置 |

### 其余几条硬事实

- **层级固定**：`BallPhysicsRoot` → `BallVisualRollRoot` → `CoreMesh` / `AttachmentRoot`。位置与旋转是两个不同的宿主，缺一个就是上面表格最后一行。
- **uGUI Button → Udon**：`Button.onClick` 的 persistent listener 指向 `UdonBehaviour.SendCustomEvent`，参数是方法名字符串；编辑器里用 `UnityEventTools.AddStringPersistentListener`（或 `Delegate.CreateDelegate`）绑定，lambda 会被拒（`does not derive from UnityEngine.Object`）。
- **回仓库清单**：每个 program asset（`.asset` + `.meta`），以及 `Assets/SerializedUdonPrograms/` 下每个新文件（+ `.meta`）。曾经 5 个 program asset 没提交，后来一次 merge 把它们静默抹掉，留下一批 `programSource = null` 的行为。
- **验证规则**：看渲染结果（`CoreMesh` 的 scale 与 renderer bounds），不要只看 motor 报的数；真实按键输入必须由人试。

## BallPlayground

见 [BallPlayground/README.md](BallPlayground/README.md)。
