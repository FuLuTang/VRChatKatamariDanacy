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

## 用编辑器脚本搭 UdonSharp 场景（MCP / execute_code）

用 `execute_code` 直接搭场景的要点（Unity 2022.3.22f1、VRChat Worlds SDK 3.10.5 自带 UdonSharp）。核心结论：**编辑器脚本给对象加 UdonSharpBehaviour 不是 `AddComponent<T>()` 一句话的事**——朴素 `AddComponent<T>()` 只建出 proxy 组件，背后没有 `UdonBehaviour`、也没有 program asset。它在 Inspector 里看得见，游戏里永远不跑。

### 必做序列

1. **program asset 先就位**：`.cs` 旁边同名 `UdonSharpProgramAsset`（`.asset`），并和它的 `.meta` 一起提交。脚本写了、asset 没生成或没提交，后面全部白搭。
2. **先编译，再拷贝**：`UdonSharpEditorUtility.CompileAllCsPrograms(true, false)` 必须在任何拷贝之前跑。否则报 `Cannot run serialization on U# behaviour … with outdated behaviour version`。
3. **建 backing 组件**：同一个 GameObject 上 `AddComponent<UdonBehaviour>()`，再用 `SerializedObject` 写两个**字段**（不是属性）：`programSource` = 该 program asset；`serializedProgramAsset` = `programAsset.SerializedProgramAsset`。
4. **把 proxy 接到 backing 上**：手工 `AddComponent<UdonBehaviour>()` 并设好 `programSource` / `serializedProgramAsset` 之后，U# 代理组件上的 `_udonSharpBackingUdonBehaviour` 仍是 0。U# 下次打开场景时会把这种代理当成未挂接，**再生成一套代理 + UdonBehaviour**，并用 `CopyUdonToProxy` 把代理上的引用清空（现象：同一物体上出现 2+2 甚至 5 个组件，引用全空）。修法：用 `SerializedObject` 把代理的 `_udonSharpBackingUdonBehaviour` 指向同物体的那个 `UdonBehaviour`，再调 `UdonSharpEditorUtility.SetBehaviourVersion(ub, UdonSharpBehaviourVersion.V1)`（否则加载时按 V0 升级又会清空变量表）；这之后再 `CopyProxyToUdon`，它会用 `TryAddVariable` 补上缺失的符号。
5. **先填引用，再回写**：proxy 上的 `[SerializeField]` 引用全部设好，再 `UdonSharpEditorUtility.CopyProxyToUdon(proxy)`。
6. **改过字段就重开场景**：源脚本新增或改名序列化字段之后，`CopyProxyToUdon` **不会**给已经存在的实例补符号（实例变量表是建实例时的快照）。必须存盘、`EditorSceneManager.OpenScene(path)` 重载场景 —— U# 在场景加载时按当前 program 重建实例变量表 —— 再 `CopyProxyToUdon` 一次。完整顺序：**编译 → 存盘 → 重开场景 → `CopyProxyToUdon` → 读回验证**。
7. **从 Udon 侧读回验证**：读 backing `UdonBehaviour` 的 `publicVariables`，用 `TryGetVariableValue(string, out object)`。**按参数类型选重载**——只按名字反射会 `AmbiguousMatchException`。

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
| 引用在 Inspector 里接好了，运行时却是 `null`；`CopyProxyToUdon` 有时只丢一句 `outdated behaviour version` | 该字段不在**实例变量表**里（建实例时的快照），`CopyProxyToUdon` 静默跳过它，见下面「两张表」 |
| 坐下不开始回合（`BallRideStation.roundController` 缺失） | 同上：字段没进实例变量表；而且座位比回合控制器先建，这个引用从最初就没接过 |
| 同一帧同一输入事件打两次日志，两次状态还不一样 | 同一对象上叠了两份 proxy + backing（给本来就有 backing 的对象又补了一个）；逐对象扫「同一个 program 上 >1 个 `UdonBehaviour`」 |
| 重新导入/重编后字段还是缺 | 换哪个 API 都补不进已有实例，只能用「存盘 → 重开场景」重建实例表 |
| 用脚本改 prefab 的 U# 字段时进了 Prefab Mode | 舞台脏着切场景会弹「Prefab Has Been Modified」模态框并卡死主线程（`state`/`read_console`/`refresh_unity` 全部超时）。不要进 Prefab Mode：直接写 proxy 的序列化字段，再 `PrefabUtility.SaveAsPrefabAsset(root, path)`；必须退出舞台时，磁盘资产已验证就选 Discard 而不是 Save |
| 一个物体上冒出两套 proxy + `UdonBehaviour`（甚至 2+2、5 个组件），代理上的引用全空 | 手工建的 `UdonBehaviour` 没被代理认领（`_udonSharpBackingUdonBehaviour` = 0），U# 打开场景时按「未挂接」又生成一套，并用 `CopyUdonToProxy` 清了引用。修法见必做序列第 4 步：`SerializedObject` 写 `_udonSharpBackingUdonBehaviour` → `SetBehaviourVersion(ub, UdonSharpBehaviourVersion.V1)` → `CopyProxyToUdon` |
| 编辑器里或 Play Mode 里把 U# 代理组件 `enabled = false`，脚本照样运行 | 代理只是编辑态的影子，真正跑的是 `UdonBehaviour` 上的程序。要停一个 U# 行为，关它所在的 GameObject（`SetActive(false)`）或关那个 `UdonBehaviour` 组件 |
| 用备份恢复场景文件后，已删除的组件又回来了（如 30 个 `PlaygroundProp` 复活，与 `PickupFieldHost` 同时给球提交同一件） | 备份是更早的场景版本；恢复前先确认备份来源。恢复后读回关键不变量：每物体每 program 最多一个 backing、旧宿主组件数为 0、`motors=1` |

### 其余几条硬事实

- **两张表，缺字段必须两边都比**：程序侧用 `UdonSharpProgramAsset.SerializedProgramAsset.RetrieveProgram().SymbolTable.GetExportedSymbols()` 取 **program 符号表**（脚本导出了哪些字段）；实例侧用 `UdonBehaviour.publicVariables`（一个 non-public 字段）取**实例变量表**。只看一边会误判「脚本没问题」。导出字段一侧**必须用 `GetExportedSymbols()`**——`GetSymbols()` 会把私有字段和编译器生成的局部符号一起返回，拿来比对会得到满屏假缺失。
- **实例变量表是快照**：实例建好之后再给脚本加序列化字段，符号不会自己进表。`CompileAllCsPrograms`、`RefreshProgram`、`AssetDatabase.ImportAsset(..., ImportAssetOptions.ForceUpdate)`、删掉重建 program asset（GUID 都不变）、`UpgradeSceneBehaviours` 全都补不进去。
- **不是修法**：把 proxy 和 backing 删掉再重新加回来，表会变成空表并抛 `Value cannot be null. Parameter name: key`。要修只走「存盘 → 重开场景」。
- **加新 link 之后要回头扫旧对象**：`BallRideStation.roundController` 一开始就没接，是因为建座位的时候回合控制器还不存在。新增一个引用之后，把所有「本该指向它」的既有对象重新检查一遍。

健康检查三件事（任一不过，按上面的「存盘 → 重开场景」重来）：

```csharp
// 1) program exported fields
var spo = (AbstractSerializedUdonProgramAsset)progAsset.GetType().GetProperty("SerializedProgramAsset").GetValue(progAsset);
var symbols = spo.RetrieveProgram().SymbolTable.GetExportedSymbols();
// 2) instance variable table
var pv = typeof(UdonBehaviour).GetField("publicVariables", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ub);
// pv.TryGetVariableValue(name, out object v) == false  → instance table lacks the symbol
// 3) every reference field value non-null; CopyProxyToUdon does not report "outdated"
```

第 1 步只能用 `GetExportedSymbols()`：`GetSymbols()` 会混进私有字段和编译器生成的局部符号，拿它和实例变量表比对会得到满屏假缺失。

- **层级固定**：`BallPhysicsRoot` → `BallVisualRollRoot` → `CoreMesh` / `AttachmentRoot`。位置与旋转是两个不同的宿主，缺一个就是表格里「视觉球停在作者高度」那一行。
- **uGUI Button → Udon**：`Button.onClick` 的 persistent listener 指向 `UdonBehaviour.SendCustomEvent`，参数是方法名字符串；编辑器里用 `UnityEventTools.AddStringPersistentListener`（或 `Delegate.CreateDelegate`）绑定，lambda 会被拒（`does not derive from UnityEngine.Object`）。
- **回仓库清单**：每个 program asset（`.asset` + `.meta`），以及 `Assets/SerializedUdonPrograms/` 下每个新文件（+ `.meta`）。漏提交的 program asset 会在 merge 时被静默抹掉，留下 `programSource = null` 的行为。
- **验证规则**：看渲染结果（`CoreMesh` 的 scale 与 renderer bounds），不要只看 motor 报的数；真实按键输入必须由人试。

## 共用资产

- `Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundBall / Ground / Prop / Wall`：`Worlds/StoryMode/StoryMode.unity`（`Ground`、4 面墙、`CoreMesh`、`SceneDescriptor.DynamicMaterials`）与 `Labs/CpuSkirmish/CpuSkirmish.unity` 在用。
- `Labs/CpuSkirmish/PlaygroundProp.cs` + `PlaygroundProp.asset`（+ `Assets/SerializedUdonPrograms/bb940408ec3999040829cf3d16ca2ec1.asset`）：`CpuSkirmish.unity` 的 24 个 `P0_0..P3_5` 道具挂的就是它。
