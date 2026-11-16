# Unity 代码约定（UdonSharp）

适用于 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/` 下的全部代码。依据：已安装的 Worlds SDK 3.10.5 自带 UdonSharp 源码与官方文档（调研结论见本文末“依据”）。

## 目录与程序集

```
Assets/VRC_Katamari/
  Runtime/                     asmdef VRCKatamari.Runtime（+ 同名 U# Assembly Definition 资产）
    Core/                      跨模块的纯数学小工具（KatamariMath）
    BallLayers/<Module>/       每个球层一个文件夹：<Module>Math.cs（纯静态）+ 需要时的 <Module>Behaviour.cs
    <OtherModule>/             以后的玩法模块同理
  Editor/                      asmdef VRCKatamari.Editor（仅编辑器；引用 Runtime）
  Tests/EditMode/              asmdef VRCKatamari.Tests.EditMode（NUnit；引用 Runtime；仅编辑器）
  Labs/<LabName>/              可手动检查的实验场景与其专用脚本
```

- 运行时代码全部在 `VRCKatamari.Runtime` 一个程序集里。UdonSharp 只能绑定同一个 U# 程序集里的静态类源码，所以不要再拆独立的“数学 asmdef”。
- 命名空间：`VRCKatamari.<区域>`，如 `VRCKatamari.BallLayers`、`VRCKatamari.Core`。
- 命名按“它是什么”，不带任务编号或批次号。

## 纯逻辑怎么写

- 公式写成 `public static class <Module>Math` 里的 `public static` 方法，只吃标量、`Vector3`、`Quaternion` 和调用方预分配的数组。
- 禁止：自定义 struct、可实例化的非 Behaviour 类、接口、泛型类型、属性（用字段或 Get/Set 方法）、方法重载（改用不同名字）、静态字段、List/Dictionary/LINQ/lambda、try/catch、对象初始化器、局部函数、多维数组 `T[,]`。
- 可以用：enum、const、`out`/`ref`、默认参数、一维和交错数组、继承。
- 多个返回值：用 `out` 参数；数组型结果写进调用方给的缓冲区，返回写入的个数。每帧路径上不分配内存。
- 递归方法必须加 `[RecursiveMethod]`。
- 失败用 `bool Try...(..., out ...)` 表示，不抛异常。输入先做有限性检查（`KatamariMath.IsFiniteFloat`/`IsFiniteVector`），不在每个模块里重写这些小工具。
- 有状态的东西（缓存、槽位表）放在 `UdonSharpBehaviour` 的字段/并行数组里，计算仍调静态方法。跨 Behaviour 调用有字符串分发开销，不放进每帧、每采样点的循环。
- 坐标：Unity 世界坐标（左手系，+Y 向上，单位米），全项目只有一套坐标系。

## 测试

- 每个模块一个 `Tests/EditMode/<Module>Tests.cs`，NUnit `[Test]` 直接调静态方法；固定、可重复的用例，写明期望值的来源（手算、几何关系或不变量），不要把被测公式原样抄进断言。
- 需要 Behaviour 的集成测试可在临时 GameObject 上 `AddComponent`，但 Networking/VRCPlayerApi 在 EditMode 下为空。
- **EditMode 测试不得在当前打开的场景里留下对象**（"EditMode tests must not leave objects in the open scene"）。测试没有自己的场景，`new GameObject`/`CreatePrimitive` 都建在操作者打开的那个场景里，失败时（尤其在建好对象、`finally` 还没生效之前就抛异常）残留物会被下一次保存场景写进仓库——`StoryMode.unity` 里出现过的 `test_prop` 就是这么来的。做法：在工厂方法里创建即登记（`created.Add`），`[TearDown]` 里用 `DestroyImmediate` 全部销毁，并给对象加 `HideFlags.DontSave`，这样即使测试运行被中止，残留也无法被序列化进场景。**登记列表不够**：实测有 `children=0` 的 `test_prop` 在 `[TearDown]` 之后仍留在场景里（最可能是被测编辑期代码 `Undo.AddComponent` 留在 Undo 系统/延迟 flush 里的句柄），所以还要有一层按签名（名称 + `DontSave`）扫描**所有已打开场景**的兜底清理，在 `[SetUp]`/`[TearDown]`/`[OneTimeTearDown]` 以及 `EditorApplication.delayCall` 的下一帧各扫一次；销毁前先 `Undo.ClearUndo` 掉对象及其组件，兜底一旦被迫救到残留就让该测试失败（不允许静默通过）。参考 `Tests/EditMode/BallLayers/RadialProxyLocalBoundsTests.cs`。临时场景隔离（`EditorSceneManager.NewPreviewScene` 之类）也可考虑，但预览场景的 Undo 与渲染行为另有约束，而被测的编辑期代码会用 `Undo.AddComponent`，所以本仓库目前用“登记 + `[TearDown]` + `DontSave` + 签名兜底”。
- NUnit 跑的是普通 C#，看不到 UdonSharp 的编译限制，所以每次改动都要同时满足：`run_tests`（EditMode，程序集 `VRCKatamari.Tests.EditMode`）全过，并且 UdonSharp 全量编译后 Console 没有新错误。

## 依据

- 结构体/非 Behaviour 实例类/接口不支持，静态类与静态方法可用，静态字段不支持：UdonSharp 源码 `Editor/Compiler/Binder/BoundNodes/BoundInvocationExpression.cs`、`Symbols/TypeSymbol.cs`、`Symbols/FieldSymbol.cs`，官方测试 `Tests~/TestScripts/Core/GenericsTest.cs`。
- U# 程序集需配 `UdonSharpAssemblyDefinition` 资产：`Editor/UdonSharpAssemblyDefinition.cs`、`Editor/Compiler/UdonSharpCompilerV1.cs`。
- 官方特性列表：<https://creators.vrchat.com/worlds/udon/udonsharp/>、<https://udonsharp.docs.vrchat.com/>。
- MCP `run_tests` 通过 Unity TestRunnerApi 执行 EditMode 测试（MCP for Unity 10.3.0 `Editor/Tools/RunTests.cs`）。
