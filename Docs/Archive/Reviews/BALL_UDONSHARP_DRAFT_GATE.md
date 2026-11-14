# 球模块 C# 草稿与 UdonSharp 门槛

状态：Lead 离线核查；**未编译**。C# 文件如果以 RuntimeDraft 交付，仅是待移植候选，不能称为 UdonSharp 可运行功能。

## 外部依据

已读取官方 [UdonSharp 文档首页](https://udonsharp.docs.vrchat.com/)（2026-10-14 HTTP 200）。文档自述是将 C# 编译成 Udon assembly 的编译器，且不完全符合任何 C# 语言规范；公开页列出有限的特性支持、只支持数组集合、`Vector3.Normalize()` 等结构体变异方法可能不改变值，并建议 UdonSharpBehaviour。该文档页面自述内容尚不完整，首页当前也没有锁定到本项目安装的精确 UdonSharp 版本，因此它是通用风险提示，不代替目标工程编译证据。

项目现有 `Packages/manifest.json` 没有列出 UdonSharp 独立包；`packages-lock.json` 将VRChat基础/世界包标为embedded `file:` 依赖。仅凭这里不能确认现行编译器的精确版本或支持矩阵；不能从过时的官方页面版本需求反推本工程。

## 草稿审查规则

- 每个层的普通 C# 草稿需注明与 Python 宿主模型之间的行为映射、Udon不支持/需替换特性；不接受只建空壳类。
- UdonSharp 实际组件必须走 `UdonSharpBehaviour → UdonSharp ProgramAsset → 序列化 Udon Program → Scene/Prefab backing behaviour → runtime consumer` 一整条链。
- Unity数学结构体不要依赖链式修改 `Normalize()`；应显式赋值并在实际编译/运行检查。
- UdonSharp的单元语义需分别检查：基础C#编译、UdonSharp编译、ProgramAsset生成、序列化引用、场景内调用行为。任一通过都不能替代其他层。
- Python模拟oracle通过 ≠ C#行为一致 ≠ UdonSharp编译通过 ≠ Unity场景运行通过。验收报告逐项标记证据。
- 不在当前Linux工作区假装生成可用的Unity ProgramAsset/Scene；这些属于用户Windows Unity工程且已有单一操作员规则。此轮仅准备草稿与对象规格。

## 来源边界

- UdonSharp官方文档：[https://udonsharp.docs.vrchat.com/](https://udonsharp.docs.vrchat.com/)
- Unity编辑器/项目为Unity 2022.3.22f1；本文件没有核实Unity里实际UdonSharp版本，也没有操作Unity或运行编译。
- 当workflow返回C#草稿后，Lead审查数组/状态生命周期、公共方法及字段可序列化性、浮点边界、分配与每帧成本，再批准Windows集成测试。不得将此检查点描述成Compile PASS。
