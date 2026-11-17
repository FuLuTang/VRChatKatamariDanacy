# csharp_check — 不经过 Unity Editor 的 C# 编译检查

在 Linux 上把 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/` 的 C# 编译一遍，输出和 Unity
Console 同形的 `文件(行,列): error CSxxxx: 消息`，有错退出码非 0。用于实现者交付前自测，
省掉 Unity 操作员那一轮。

- `check.py` 只查 **C# 编译**。UdonSharp 的语言子集、场景/prefab/Udon 程序编译都不在范围内
  （见[盲区](#盲区)）。EditMode 测试里不依赖引擎的那一批可以用同目录的
  [`run_tests.py`](#run_testspy--在本机直接跑不依赖引擎的-nunit-测试) 直接执行。
- 不启动、不连接 Unity Editor，不改工程目录里的任何文件；不装任何东西到系统（本地 .NET SDK
  装在 `~/.dotnet`，引用 DLL 和 Roslyn 放在 `~/.cache/vrc-katamari-refasm/`，都在仓库外）。

## 一条命令

```bash
python3 Tools/csharp_check/check.py
```

首次运行会自动：探测 Windows 上的 Unity → 把引用 DLL 和 Unity 版 Roslyn 取到本地缓存 →
（若本机没有）用官方 `dotnet-install.sh` 把 .NET SDK 8 装到 `~/.dotnet`。需要能
`ssh pc114514`；下载约 210 MB（SDK）+ 21 MB（Roslyn）+ 65 MB（引用 DLL，从 Windows 拷回）。
之后每次检查不再联网。实测冷启动约 25 秒（SDK 已装时；装 SDK 另需几分钟，取决于网速）。

常用参数：

| 参数 | 作用 |
| --- | --- |
| `--project DIR` | 检查别的工程目录（默认本仓库的 `VRChatKatamariDanacyUnity`；历史快照就用它） |
| `--unit NAME` | 只检查一个 assembly 及其项目依赖（可重复），如 `--unit VRCKatamari.Tests.EditMode` |
| `--warnings` | 同时打印 warning（默认只打印 error） |
| `--json` | 机器可读结果（含每个 assembly 的错误、耗时、退出码） |
| `--timings` | 打印每个 assembly 的耗时 |
| `--backend local\|windows` | 选择后端，见[两条路线](#两条路线) |
| `--refresh` | 重新探测 Windows、重新取引用 DLL（Unity/包版本变了以后用） |
| `--print-defines` | 打印每个 assembly 实际用的 `#define` 集合并退出 |
| `--csc PATH` | 换一个 csc.dll（默认用缓存里的 Unity 版 Roslyn） |
| `--output-dir DIR` | 仅 local 后端：把编出的 DLL 和便携 PDB 留在 DIR（`run_tests.py` 用；默认不留，编译本身不变） |

退出码：`0` 无错误；`1` 有编译错误；`2` 工具自身失败（连不上 Windows、缓存缺文件等）。

## 编译哪些 assembly

Unity 的编译单元不只三个 asmdef：`Assets/VRC_Katamari/Editor/` 下**大部分代码其实没有
asmdef**（asmdef 只在 `Editor/MapGreybox/`），它们属于 Unity 的默认 Editor assembly
`Assembly-CSharp-Editor`。`LevelPaths.cs`、`PlacementImporter*.cs` 这些编辑器代码就在那里，
所以必须一起编译，否则复现不了已知的 18 条错误。

| 编译单元 | 源码范围 | 说明 |
| --- | --- | --- |
| `VRCKatamari.Runtime` | `Assets/VRC_Katamari/Runtime/` | 其 asmdef |
| `VRCKatamari.Editor` | `Assets/VRC_Katamari/Editor/MapGreybox/` | 其 asmdef（Editor 下唯一的 asmdef） |
| `Assembly-CSharp-Editor` | `Assets/VRC_Katamari/Editor/` 减去 asmdef 子树 | Unity 默认 Editor assembly |
| `VRCKatamari.Tests.EditMode` | `Assets/VRC_Katamari/Tests/EditMode/` | 其 asmdef + `nunit.framework` |

编译顺序按 asmdef 引用：Runtime → VRCKatamari.Editor → Assembly-CSharp-Editor →
Tests；互不依赖的单元并行编译。Runtime 编译失败时，依赖它的单元会被标成 `skipped`，
不产生级联噪声（和 Unity 的行为一致）。

## 两条路线

两条都实现、都验证过；默认走 `local`，因为不依赖 Windows 在线，结果也完全一致。

### local（默认）

1. 从 Windows 的 Unity 工程读 Unity 生成的 `*.csproj`：每个 assembly 的 `DefineConstants`、
   `<Reference>` 的 DLL 绝对路径、`<ProjectReference>`（换成 `Library/ScriptAssemblies/*.dll`）。
2. 把这些 DLL 拷到 `~/.cache/vrc-katamari-refasm/refs/`，把 Unity 2022.3.22f1 自带的
   Roslyn（4.3.1，`Microsoft.Net.Compilers.Toolset` 同版本 NuGet 包）放到 `.../roslyn/`。
   **编译器版本必须对齐**：4.11（.NET 8 SDK 自带）会少报 f1b5d81 里
   `PlacementImporter.V2.cs` 的 2 条 CS0029 级联错误。
3. 用 `csc -nostdlib+ -noconfig -langversion:9.0 -define:... -r:...` 直接编译，临时
   目录在 `/tmp`，离开即删。

### windows（`--backend windows`）

把源码 tar 到 Windows 的 `%TEMP%\vrck_csharp_check_win\`，用 Unity 自己的
`Editor\Data\DotNetSdkRoslyn\csc.dll` + `Editor\Data\NetCoreRuntime\dotnet.exe` 和当前工程
的引用 DLL 编译，结果 JSON 传回。**8 秒左右**，是"Unity 实际会怎么报"的基准；本地缓存
过期或怀疑编译器差异时用它对照。同样不写工程目录、不碰 Editor。

## 验收：四个已知快照

用 `git archive <sha> | tar -x -C /tmp/snap-<sha>` 取出（只读，不建 worktree、不切分支），
`--project /tmp/snap-<sha>/VRChatKatamariDanacyUnity` 检查。两个后端结果相同。

| 快照 | Unity 实测 | 本检查 | 逐条 |
| --- | --- | --- | --- |
| `f1b5d81` | 18 | **18** | 全部对上（见下） |
| `446c0bc` | 5 | **5** | 全部对上 |
| `81400ac` | 1 | **1** | 全部对上 |
| `d7e2a10` | 0 | **0** | 一致 |

`f1b5d81` 的 18 条：

- `PlacementV2RulesTests.cs`：7 × CS7036，行 51、71、129、143、232、242、242（列 38/21/42 等）。
- `LevelPaths.cs`：177 列 63 CS1620；185 列 17 CS0269；196/206/212/215 CS0177。
- `PlacementImporter.V2.cs`：677/706/779 CS0246（`PlacementV2WallSegment` 是嵌套类型），
  677 列 49 与 779 列 49 的 CS0029 级联。

`446c0bc` 的 5 条即 `LevelPaths.cs` 185 CS0269 + 196/206/212/215 CS0177；`81400ac` 的 1 条即
`PlacementImporter.cs(3610,74)` CS1620。

耗时（本机 2 核，缓存已就绪）：local 约 15 s（4 个 assembly，并行）；windows 约 8 s。
首次 bootstrap 约 25 秒（SDK 已装好；首次还要装 SDK，另需几分钟，取决于网速）。

> 2026-11-16 实测：`git archive 7bb7bd3`（当时 HEAD）有 **7 条错误**，全在
> `Tests/EditMode/Pickup/MotionMathTests.cs`，两个后端逐条一致：64/65 CS1503（Unity 自带
> NUnit 1.0.6 没有 `AreNotEqual(double,double,double)` 重载）、321/947/964 CS0170、
> 561/955 CS0165（`out` 参数在 `for` 循环里赋值、循环外读取，编译器不认为确定赋值）。
> 也就是说这是提交代码里的真实错误，不是检查器的误报（检查时工作树里的未提交修复会让
> 结果变 clean）。检查的是工作树；检查时若有别的实现者正在改代码，结果会包含他们未提交的
> 状态（用 `git archive` 取出提交再查可避免）。

## run_tests.py — 在本机直接跑不依赖引擎的 NUnit 测试

```bash
python3 Tools/csharp_check/run_tests.py                       # 全部能跑的
python3 Tools/csharp_check/run_tests.py --filter Ramp         # 全名含 "Ramp" 的（可重复，取并集）
python3 Tools/csharp_check/run_tests.py --list                # 只发现、不执行
python3 Tools/csharp_check/run_tests.py --verbose             # 列出每一条“不可执行”
python3 Tools/csharp_check/run_tests.py --continue-after-failure   # 断言失败后继续（见下）
python3 Tools/csharp_check/run_tests.py --project /tmp/snap/VRChatKatamariDanacyUnity   # 钉住某个提交的快照
```

实测（本机 2 核 ARM64，缓存就绪）：源码有变化时 **约 30 s**（`check.py` 编 4 个 assembly，约
23–29 s，加运行约 2 s）；源码没变（按 `.cs` 内容哈希）直接复用上次编译，**约 2 s**；`--filter` 单个
夹具不到 1 s。退出码：`0` 没有本地失败；`1` 至少一条失败；`2` 工具自己失败（编译错误、缺前置、
runner 崩溃或超时）。

### 它做了什么

1. **编译用 `check.py` 的同一份实现**（`--output-dir` 保留 DLL 与便携 PDB，其余完全不变）：
   Roslyn 4.3.1 + 缓存里 Unity 的引用 DLL + 工程原文件，Runtime、测试一行不复制、不改。
2. `test_runner/`（.NET 8，`~/.dotnet`）在进程里用 **Unity 真正的托管 DLL**（`UnityEngine.*`、
   `UnityEditor.*`、`VRC.*` 等，按需从缓存 `refs/` 解析，不是桩）加载刚编出的四个 assembly，
   再用 NUnit 3.5 的 `NUnitTestAssemblyRunner` 执行。解析时跳过 `mscorlib`、`netstandard`、
   `System*`、`Microsoft.*`，让 .NET 自带的门面接管。
3. **NUnit 版本**：Unity 自带的 `nunit.framework.dll` 是 3.5.0.0 的 net35 构建，在 .NET 8 上
   `TypeLoadException`。测试照旧针对 Unity 的 DLL 编译，运行时换成 NuGet `NUnit 3.5.0` 的
   `lib/dotnet/nunit.framework.dll`（程序集身份同为 3.5.0.0）。首次运行自动下载并校验 SHA-256，
   放在 `~/.cache/vrc-katamari-refasm/nunit/`。
4. `test_results.py` 解析 NUnit 的结果 XML，逐条分类（下表）。

### 分类（分母 = 本地发现的用例数，每条只归一类）

| 类别 | 判定依据 |
| --- | --- |
| 通过 | NUnit 判 Passed，且这条用例运行期间运行时**没有**拒绝过引擎原生调用 |
| 失败 | 断言失败，或抛出了下面几类以外的异常。输出里给出 NUnit 原文、失败断言的 `文件:行`、异常链 |
| 与 Unity 不一致（已知） | 本地红、Unity 绿，且同时命中 `known_differences.json` 的条目（见[已知差异](#已知差异本地红而-unity-绿)）。不算失败，不影响退出码 |
| 本地不可执行 | 实际抛出的异常类型决定：`SecurityException: ECall methods must be packaged into a system module`（运行时拒绝了 Unity 引擎的原生 extern 方法；被 `TargetInvocationException` 包着也看最内层），或 `DllNotFoundException` / `EntryPointNotFoundException`，或 `TypeLoadException`、`MissingMethodException`、`FileNotFoundException` 等“这个环境加载不了”的类型。**不算通过。** 另外：用例通过了、但运行期间有被拒绝的原生调用被用例吞掉（`catch`），也归这一类，因为这条“通过”什么都没证明 |
| 跳过/忽略 | NUnit 的 Ignored / Explicit |

“不可执行”按原因分组，分组不靠测试名，靠 runner 在**第一次机会异常**处记录的数据：被拒绝的那个方法本身，
以及它（和三层内的被调方法）里含有的 `InternalCall` extern 方法，再按这些 extern 的声明类型归类：

- **需要 GameObject 或场景**：extern 在 `GameObject` / `Transform` / `Component` / `UnityEngine.Object` /
  `MonoBehaviour` / `SceneManager` 等上（`new GameObject`、`hideFlags`、`get_transform`……）；
- **引擎原生调用**：其它 `UnityEngine.*` 的 extern（`Quaternion.Inverse/LookRotation/AngleAxis`、
  `Application.dataPath`、`Time.time`、`JsonUtility.FromJson`）——有些是纯数学，但在 Unity 里它们是
  原生实现，这里**不伪造**；
- **编辑器 API**：`UnityEditor.*` 的 extern（目前 0 条）；
- **其它**：运行时拒绝了方法，但静态扫描没找到可识别的 extern（目前 2 条，是经反射调用 helper 的用例）。

用例需要引擎对象时我们既不造对象也不写桩：对应用例一律停在“本地不可执行”，由 Unity 那一轮验证。

### 实测结果（HEAD 35f4246 的快照；工作区当时有别人对 ContactModelV2 的未提交改动，所以用 `git archive` 快照）

```
分母 = 本地发现 785
  通过 613 | 失败 0 | 与 Unity 不一致（已知）3 | 本地不可执行 169（GameObject/场景 91，引擎原生 76，编辑器 API 0，其它 2）
```

对照基线是提交 `6ad6def` 之后的 HEAD（`35f4246`，之后只改了 `AGENTS.md`）。工作区里别人正在改的
`KatamariBallMotor.cs` / `ContactModelV2Tests.cs` 的中间状态会让发现数变化（当时 787）。要和 Unity 的基线对齐，
就像上面那样 `git archive HEAD VRChatKatamariDanacyUnity/Assets/VRC_Katamari | tar -x -C /tmp/snap`，
然后 `--project /tmp/snap/VRChatKatamariDanacyUnity`。

**与 Unity 983 对账**：Unity 983 = 本地 785 + SDK 自带 191 + **未解释 7**。

- 本地 785 与源码里的 `[Test]` 783 + `[TestCase]` 2 完全一致。
- 缺的那一大块是 VRChat SDK 自带的 `UnityEditorTests` assembly
  （`Packages/com.vrchat.worlds/Editor/Udon/UnityEditorTests/` 的 9 个文件，`Tests.GraphNodeTests`
  就在其中）：它们不在 `check.py` 的 4 个 assembly 里，本工具不编译、不运行。一次性核实
  （把那 9 个文件读下来在 `/tmp` 里编译、发现，之后已删）：**191 条**（VRCJsonTests 74、DataListTests 29、
  DataTokenTests 28、JsonListTests 23、UdonGraphSettingsTests 15、DataDictionaryTests 10、
  JsonDictionaryTests 10、GraphNodeTests 1、UICompilerTests 1）。
- 还差 **7 条没有解释**：Windows 上 `UnityEditorTests.csproj` 只列了这 9 个文件，本地没有任何别的发现路径能
  凑出这 7 条。可能是 Unity Test Runner 的计数口径（例如 `[Repeat(32)]` 的 `GenerateGarbage`、参数化用例）
  或工程里别的测试 assembly；要查清需要 Unity 的结果 XML，本工具读不到。

**Unity 最近一次红的两条在本地的状态**：`Tests.GraphNodeTests.CheckHelpURLsForSystemNodes` 是 SDK 的，本地没有；
`ContactModelV2Tests.AShallowWedgeWithNoOpposingContactIsStillRecovered` 本地是“不可执行”
（需要 `GameObject`），所以本地对这条红**没有任何发言权**。

### 已知差异（本地红而 Unity 绿）

登记在 [`known_differences.json`](known_differences.json)，每条是“测试全名 + 那一处失败的消息片段”；
同一条测试以别的方式失败不会被它盖住，会照常报成失败。输出里它们单独成一类，不混进失败，也不改测试或 Runtime。
现在只剩一条，已查到原因：

1. **`CriteriaModelOverrideTests.TheCellSerializesTheV2RowAndTheDeckFootprint`：程序集没编译。**
   测试在已加载的 assembly 里按名字找实验场的 `PhysicsTestCell`，它在 `Assets/VRC_Katamari/Labs/`
   （Unity 的 `Assembly-CSharp`），`check.py` 不编译这个 assembly，所以类型不存在、用例在 `Assert.Fail`。
   这不是行为差异；这条用例即便补上类型也需要引擎（`JsonUtility`、`MonoBehaviour`）。

**曾经登记、现已消除的一类：浮点表达式的中间精度。** Unity 的 Mono 把 `float` 表达式的中间结果留在 double 精度、
只在存回变量时舍入一次，.NET 每步都舍入；两种结果在“恰好踩在边界上”的断言里会不一样。
`SizeModelTests.TheCapSaturatesExactlyAndReportsTheCapVolume` 与 `RampSupportTests.TheRampFootMeetsTheFloorWithoutAStep`
就是这样，已经分别改掉，不再登记：

- 30 m 上限饱和：`SizeModelMath.TryDerive` 原先用 `total >= capVolume` 判饱和，而 `capVolume` 本身是 float 表达式
  `PI/6f*d*d*d` 的结果（30 m 处 Mono 与 .NET 相差约 1e6 mm³）。运行时改为整数域、带 1 ppm 舍入预算的比较
  （`total >= capVolume - capVolume/CapVolumeToleranceDivisor`），所以“恰好等于数学上限”在任何舍入下都是上限。
  新增 `SaturationDoesNotDependOnHowTheCapVolumeWasRounded` 钉住预算边界。
- 斜坡脚与地面并列：脚线上平面高度是 `0` 还是 `~1e-9` 取决于舍入，选择规则“严格更高者胜”下谁拿到 id 随之变化；
  但高度处处连续（两者差 1e-9 m），所以这只是个标签。运行时不动，测试改为：脚线上只钉“高度≈地面”（id 可为地面或斜坡），
  上移 1 mm（高于舍入噪声约 6 个数量级）处钉斜坡胜出。

写以后依赖“float 中间值落在边界上”的断言时，同样要么用容差，要么把被比较的量放到整数域。

另外两个环境决定会影响结果，已固定：runner 把当前文化设为 `InvariantCulture`（主机的 `LANG` 不能决定测试怎么格式化/解析；
测试自己切文化的地方照常生效）；不开 `InvariantGlobalization`（`de-DE` 之类的文化要能创建）。前任的 `CultureNotFoundException`
就是开着 `InvariantGlobalization` 的产物。

### 断言失败后继续（`--continue-after-failure`）

Unity 里 NUnit 在第一条失败断言处停下，后面的断言没跑过。NUnit 3.5 没有 `Assert.Multiple`，所以**用 3.5 做不到**。
这个开关换用 NUnit **3.14.0**（`lib/netstandard2.0`，同样首次自动下载并校验 SHA-256）：测试代码不动，
runner 在运行时给每个已加载的测试方法挂一个 `IApplyToContext`，把测试线程上下文的 `MultipleAssertLevel` 置 1，
等价于整个测试体都在 `Assert.Multiple` 里；失败的每条断言带各自的 `文件:行` 和原文一起列出。注意：

- 这是**诊断视图**，不是 Unity 的行为：框架版本不同（3.14，不是 3.5），并且第一条之后的失败可能只是被第一条连带出来的。
- 已知差异条目只覆盖它登记的断言：继续执行后多出来的失败断言要在条目的 `assertions`
  里逐条登记，否则这条用例回到“失败”。
- 异常（含引擎原生调用）依然是抛出，不是记录，“不可执行”的判定不变。
- 实测（本次改动后）：失败 0、已知差异 1、不可执行 183，通过 640，与默认模式相同，也就是说那 640 条在“继续执行”下也没有被藏起来的后续失败。

### 不覆盖什么（本地绿 ≠ Unity 绿）

- **UdonSharp**：语言子集、U# 程序编译、Udon 虚拟机里的行为，都只能在 Unity 验证。这里跑的是 Roslyn 编出来的普通 C#。
- **需要引擎对象的测试**（`GameObject`、场景、`MonoBehaviour` 生命周期、物理、`Quaternion` 的原生实现、`JsonUtility`、
  `Application.dataPath`……）：当前 183 条（共发现 824），全部归“本地不可执行”，没有被算作通过。
- **Mono 与 .NET 的运行时差异**：浮点中间精度（上节：测试别踩在它的刀口上）、`Math.Pow/Tan` 等 libm 实现（glibc 与 Windows 的 CRT 末位可能不同）、
  集合/哈希顺序、`Random` 序列；这些只能靠 Unity 暴露。
- **运行平台**：本地是 Linux/ARM64 上的 .NET 8，不是 Windows 上的 Mono；IL2CPP / Player 不涉及。
- **SDK 自带测试**（191 条）和 `Assembly-CSharp`（Labs、UdonSharp 工具脚本）不编译、不运行。
- **只读 `Assets/VRC_Katamari/Tests/EditMode/` 里的一个测试 assembly**：Editor 程序集里的测试目前没有；若以后出现需扩充。
- 读不到 Unity 的结果 XML，所以“Unity 绿”以 `known_differences.json` 里记的基线（提交 `6ad6def`：983 条 981 绿）为准。
  这份基线随新的 Unity 全量结果更新。

### 缓存与文件

仓库外（和 `check.py` 共用 `~/.cache/vrc-katamari-refasm/`，**都不进仓库**）：

- `nunit/NUnit.3.5.0.nupkg`、`nunit/nunit.framework.dll`；`nunit/NUnit.3.14.0.nupkg`、`nunit/3.14.0/nunit.framework.dll`
- `test-runner/`、`test-runner-continue/` — 构建好的 runner（源码有变才重编）
- `test-build/<源码哈希>/` — 四个 assembly 的 DLL 与 PDB，保留最近 3 份
- `/tmp/vrck-run-tests-*` — 每次运行的临时结果，结束即删

仓库内：

| 文件 | 作用 |
| --- | --- |
| `run_tests.py` | 入口：复用 `check.py` 编译、调 runner、渲染报告 |
| `test_results.py` | 纯 Python：结果 XML 解析与分类（有单测，不需要 .NET） |
| `known_differences.json` | 已知差异与 Unity 基线 |
| `test_runner/` | .NET 8 的 NUnit 宿主：`test_runner.csproj`、`Program.cs`（没有测试、没有引擎桩） |

Python 单测在 `Docs/tests/csharp_check/`（`python3 -m unittest discover -s Docs/tests`）。

## 条件编译符号

从 Windows 工程里 Unity 生成的 csproj 的 `DefineConstants` 原样读取（不是脚本里写死的），
用 `--print-defines` 可以随时打印当前值。Editor 平台编译的关键符号：

- `UNITY_EDITOR`、`UNITY_EDITOR_64`、`UNITY_EDITOR_WIN`、`UNITY_EDITOR_ONLY_COMPILATION`
  （只加在 Editor-only assembly：VRCKatamari.Editor / Assembly-CSharp-Editor / Tests）
- `UNITY_2022_3_22`、`UNITY_2022_3`、`UNITY_2022`、`UNITY_2022_3_OR_NEWER`、
  `UNITY_2021_3_OR_NEWER` … `UNITY_5_3_OR_NEWER`（逐级）
- `UNITY_64`、`PLATFORM_ARCH_64`、`UNITY_STANDALONE_WIN`、`UNITY_STANDALONE`
- `NET_4_6`、`NET_UNITY_4_8`、`ENABLE_MONO`、`DEBUG`、`TRACE`、`UNITY_ASSERTIONS`
- VRChat/UdonSharp：`VRC_SDK_VRCSDK3`、`UDON`、`UDONSHARP`、`VRC_ENABLE_PLAYER_PERSISTENCE`、
  `VRC_ENABLE_COMPANIONS`、`UNITY_POST_PROCESSING_STACK_V2`
- 测试：`UNITY_INCLUDE_TESTS`（四个 assembly 的定义里都有；Tests 的 asmdef 还有同名 defineConstraint）
- `CSHARP_7_OR_LATER`、`CSHARP_7_3_OR_NEWER` 等兼容符号，以及 Unity 的 `ENABLE_*` 平台特性开关

完整列表（123 个，供核对；实际以 `--print-defines` 为准）：

```text
UNITY_2022_3_22;UNITY_2022_3;UNITY_2022;UNITY_5_3_OR_NEWER;UNITY_5_4_OR_NEWER;UNITY_5_5_OR_NEWER;UNITY_5_6_OR_NEWER;UNITY_2017_1_OR_NEWER;UNITY_2017_2_OR_NEWER;UNITY_2017_3_OR_NEWER;UNITY_2017_4_OR_NEWER;UNITY_2018_1_OR_NEWER;UNITY_2018_2_OR_NEWER;UNITY_2018_3_OR_NEWER;UNITY_2018_4_OR_NEWER;UNITY_2019_1_OR_NEWER;UNITY_2019_2_OR_NEWER;UNITY_2019_3_OR_NEWER;UNITY_2019_4_OR_NEWER;UNITY_2020_1_OR_NEWER;UNITY_2020_2_OR_NEWER;UNITY_2020_3_OR_NEWER;UNITY_2021_1_OR_NEWER;UNITY_2021_2_OR_NEWER;UNITY_2021_3_OR_NEWER;UNITY_2022_1_OR_NEWER;UNITY_2022_2_OR_NEWER;UNITY_2022_3_OR_NEWER;PLATFORM_ARCH_64;UNITY_64;UNITY_INCLUDE_TESTS;ENABLE_AR;ENABLE_AUDIO;ENABLE_CACHING;ENABLE_CLOTH;ENABLE_EVENT_QUEUE;ENABLE_MICROPHONE;ENABLE_MULTIPLE_DISPLAYS;ENABLE_PHYSICS;ENABLE_TEXTURE_STREAMING;ENABLE_VIRTUALTEXTURING;ENABLE_LZMA;ENABLE_UNITYEVENTS;ENABLE_VR;ENABLE_WEBCAM;ENABLE_UNITYWEBREQUEST;ENABLE_WWW;ENABLE_CLOUD_SERVICES;ENABLE_CLOUD_SERVICES_ADS;ENABLE_CLOUD_SERVICES_USE_WEBREQUEST;ENABLE_CLOUD_SERVICES_CRASH_REPORTING;ENABLE_CLOUD_SERVICES_PURCHASING;ENABLE_CLOUD_SERVICES_ANALYTICS;ENABLE_CLOUD_SERVICES_BUILD;ENABLE_EDITOR_GAME_SERVICES;ENABLE_UNITY_GAME_SERVICES_ANALYTICS_SUPPORT;ENABLE_CLOUD_LICENSE;ENABLE_EDITOR_HUB_LICENSE;ENABLE_WEBSOCKET_CLIENT;ENABLE_GENERATE_NATIVE_PLUGINS_FOR_ASSEMBLIES_API;ENABLE_DIRECTOR_AUDIO;ENABLE_DIRECTOR_TEXTURE;ENABLE_MANAGED_JOBS;ENABLE_MANAGED_TRANSFORM_JOBS;ENABLE_MANAGED_ANIMATION_JOBS;ENABLE_MANAGED_AUDIO_JOBS;ENABLE_MANAGED_UNITYTLS;INCLUDE_DYNAMIC_GI;ENABLE_SCRIPTING_GC_WBARRIERS;PLATFORM_SUPPORTS_MONO;RENDER_SOFTWARE_CURSOR;ENABLE_VIDEO;ENABLE_ACCELERATOR_CLIENT_DEBUGGING;ENABLE_NAVIGATION_PACKAGE_DEBUG_VISUALIZATION;ENABLE_NAVIGATION_HEIGHTMESH_RUNTIME_SUPPORT;ENABLE_NAVIGATION_UI_REQUIRES_PACKAGE;PLATFORM_STANDALONE;TEXTCORE_1_0_OR_NEWER;PLATFORM_STANDALONE_WIN;UNITY_STANDALONE_WIN;UNITY_STANDALONE;ENABLE_RUNTIME_GI;ENABLE_MOVIES;ENABLE_NETWORK;ENABLE_NVIDIA;ENABLE_CRUNCH_TEXTURE_COMPRESSION;ENABLE_OUT_OF_PROCESS_CRASH_HANDLER;ENABLE_CLUSTER_SYNC;ENABLE_CLUSTERINPUT;PLATFORM_UPDATES_TIME_OUTSIDE_OF_PLAYER_LOOP;GFXDEVICE_WAITFOREVENT_MESSAGEPUMP;PLATFORM_INITIALIZES_MEMORY_MANAGER_EXPLICITLY;ENABLE_MONO;NET_4_6;NET_UNITY_4_8;ENABLE_PROFILER;DEBUG;TRACE;UNITY_ASSERTIONS;UNITY_EDITOR;UNITY_EDITOR_64;UNITY_EDITOR_WIN;ENABLE_UNITY_COLLECTIONS_CHECKS;ENABLE_BURST_AOT;UNITY_TEAM_LICENSE;ENABLE_CUSTOM_RENDER_TEXTURE;ENABLE_DIRECTOR;ENABLE_LOCALIZATION;ENABLE_SPRITES;ENABLE_TERRAIN;ENABLE_TILEMAP;ENABLE_TIMELINE;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER;TEXTCORE_FONT_ENGINE_1_5_OR_NEWER;VRC_SDK_VRCSDK3;UDON;UDONSHARP;UNITY_POST_PROCESSING_STACK_V2;VRC_ENABLE_PLAYER_PERSISTENCE;VRC_ENABLE_COMPANIONS;CSHARP_7_OR_LATER;CSHARP_7_3_OR_NEWER
```

注意：这是 Unity 的 **Editor 平台**编译（能覆盖 EditMode 测试）；Player/IL2CPP 平台的符号
差异没有建模。`COMPILER_UDONSHARP` 不在 csproj 里——UdonSharp 编译 U# 程序时另有一套参数，
本工具不模拟。

## 盲区

- **不是 UdonSharp 检查**。U# 的语言子集限制（无 struct/泛型/`List<T>`/属性、不能用某
  些 API 等）由 U# 编译器/UdonSharp 自己检查，Roslyn 不报，本工具也不会报。
- **`check.py` 不跑测试**。只证明 C# 编译通过；测试用 `run_tests.py` 跑，其中本地跑不了的部分
  （见上一节）和本地绿的结果是否等于 Unity 绿，仍需 Unity 那一轮。
- **不碰场景/资产**。场景、prefab、序列化、`Assets/SerializedUdonPrograms/`、Udon 程序
  编译（UdonSharp→Udon VM 字节码）都不在范围内。
- **不跑 Unity 的 analyzer/source generator**。csproj 里引用了
  `Unity.SourceGenerators`、`Microsoft.Unity.Analyzers` 等；本工具只调用 csc，不加载
  analyzer，生成代码或 analyzer 产生的错误看不到。
- **只覆盖上面 4 个 assembly**。`Assets/VRC_Katamari/Labs/*.cs` 和
  `Assets/UdonSharp/UtilityScripts/*.cs` 属于默认的 `Assembly-CSharp`（运行时），不在检查范围。
  `Assembly-CSharp-Editor` 也只编译 `Assets/VRC_Katamari/Editor/` 下的文件，不含工程里其他
  没有 asmdef 的 Editor 脚本（目前没有这类文件）。
- **引用/宏是 bootstrap 时刻的快照**。Unity 版本、VRChat SDK 包、`Packages/manifest.json`
  变了以后要 `--refresh`；否则可能出现缺引用/多余引用。
- 输出用的是英文诊断（`-preferreduilang:en`）；中文版 Unity Console 只是消息文本不同。
- 诊断的**行/列/代码**与 Unity 一致（同编译器、同引用、同宏），但 Unity Console 里多条
  错误的**排列顺序**可能不同。

## 缓存与 Windows 上的读写

仓库外：

- `~/.dotnet/` — .NET SDK（仅用来跑 Roslyn；不装到系统）
- `~/.cache/vrc-katamari-refasm/manifest.json` — 本地用的引用清单（assembly→DLL 映射、宏、语言版本）
- `~/.cache/vrc-katamari-refasm/probe.json` — Windows 原始探测结果（含 DLL 的 Windows 绝对路径）
- `~/.cache/vrc-katamari-refasm/refs/` — 从 Windows 拷来的 Unity/VRChat 引用 DLL（65 MB，
  **专有文件，绝不进 git、不放进仓库**）
- `~/.cache/vrc-katamari-refasm/roslyn/` — Unity 同版本 Roslyn（NuGet 包，MIT）
- `/tmp/vrck-*` — 临时编译目录，结束即删

Windows（`pc114514`）上：

- 读：`D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity\` 下
  `VRCKatamari.Runtime.csproj`、`VRCKatamari.Editor.csproj`、`Assembly-CSharp-Editor.csproj`、
  `VRCKatamari.Tests.EditMode.csproj`；这些 csproj 里 `<Reference>` / `<ProjectReference>`
  指向的 DLL（Unity 安装目录、`Packages\com.vrchat.*`、`Library\PackageCache\*`、
  `Library\ScriptAssemblies\*`）；Unity 安装目录下的 `Editor\Data\DotNetSdkRoslyn\csc.dll`
  和 `Editor\Data\NetCoreRuntime\dotnet.exe`（后者执行 `-version` 和 windows 后端的编译）。
- 写：只在 `%TEMP%\vrck_csharp_check\`（bootstrap 暂存）和
  `%TEMP%\vrck_csharp_check_win\`（windows 后端）下写 probe/stage 脚本、引用 DLL、
  tar、rsp、输出 DLL 和 results.json，结束删除（`--keep-remote` 可保留用于排查）。
- **不写**工程目录：`Assets/`、`Library/`、`Packages/`、`ProjectSettings/` 都不写；
  `ProjectSettings/ProjectSettings.asset` 根本不打开。不安装任何软件，不启动/连接 Editor。

## 维护

- Unity 换版本：改 `~/.cache/.../manifest.json` 的来源即可，`--refresh` 重新探测；若
  Unity 的 Roslyn 不再是 4.3.1，bootstrap 会提示，用
  `VRCK_ROSLYN_VERSION=<NuGet 版本> python3 Tools/csharp_check/bootstrap.py --refresh` 对齐。
- 只换 SDK：`python3 Tools/csharp_check/bootstrap.py --dotnet-only`。
- 环境变量：`VRCK_SSH_HOST`、`VRCK_WINDOWS_PROJECT`、`VRCK_CSHARP_CHECK_CACHE`、
  `VRCK_DOTNET_ROOT`、`VRCK_DOTNET_VERSION`、`VRCK_ROSLYN_VERSION`。

## 文件

| 文件 | 作用 |
| --- | --- |
| `check.py` | 入口；找源码、生成响应文件、调 csc、格式化诊断（`--output-dir` 保留 DLL 供 `run_tests.py` 用） |
| `run_tests.py`、`test_results.py`、`known_differences.json`、`test_runner/` | 本地跑 NUnit，见上一节 |
| `bootstrap.py` | 探测 Windows、取引用 DLL/Roslyn、装 `~/.dotnet` |
| `probe_windows.ps1` | 只读探测 csproj/Unity 编译器，输出 JSON |
| `stage_refs.ps1` | 把探测到的 DLL 平铺到 `%TEMP%` 暂存目录 |
| `compile_windows.ps1` | windows 后端：用 Unity 的 csc 跑响应文件，输出 JSON |
| `windows_backend.py` | windows 后端的编排与诊断归一化 |
