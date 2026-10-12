# T00 证据交接

本次仅重写证据文档，依据 Lead 已核实的 Windows Editor.log、VRChat 客户端日志及提交事实；没有 SSH、操作 Unity、调用 MCP、下载、账号操作、Git 写入或发布。Windows Editor 归用户所有，Unity MCP 未配置。

## 验收结论

1. **目标 SDK 编译成功：PASS。** 当前 Editor.log 第 406 行记录 8 个脚本成功编译（1.354 秒）；第 598 行记录 62 个 `SerializedUdonProgramAsset` 的代码签名计算完成。全 919 行中 `error CS`、`Compilation failed`、`Scripts have compiler errors`、`Exception` 均零命中。
2. **Build & Test 可进入空场景：PASS。** 本地 bundle 生成，VRChat 客户端成功加入 Local Test 并完成进入，且用户确认正常进入。Editor.log 第 856 行仍有 SDK player-build 阶段的 `Build Finished, Result: Failure.`；同轮 bundle 与客户端加载成功。该矛盾观察已保留，暂不解释，不隐去，也不视为本任务进入场景验收失败。

## 环境与场景证据

- 采样：2026-10-12，Windows 本地时间约 15:47；具体时区未记录。
- Unity Editor `2022.3.22f1`，revision `887be4894c44`；VPM `com.vrchat.base` / `com.vrchat.worlds` 为 `3.10.5`；UdonSharp 使用 Worlds 包内置目录 `Packages/com.vrchat.worlds/Integrations/UdonSharp`。
- 用户通过 `VRC Katamari → T00 → Create Bootstrap Scene` 生成 `Assets/VRC_Katamari/Bootstrap/T00_Bootstrap.unity`（GUID `022cd2218fedebb4a84f5d91f0b4022a`）。生成器 `Assets/VRC_Katamari/Bootstrap/Editor/T00BootstrapBuilder.cs`（GUID `4fc37ce5061d8f746babefbc61a112a1`）。
- 场景含 `VRCWorld`、`VRCSceneDescriptor`、Ground、Spawn 和 Reference Camera。
- `Editor.log` 关键行：406 编译完成；585/587 场景打开/加载；598 签名计算；847 Level 0；856 player-build Failure 观察项；879 115.5 KB bundle 生成。
- 客户端 `output_log_2026-10-12_15-47-41.txt`：第 9 行 BuildAndRun 本地 bundle 启动；601–604 成功进入 Local Test；873 跳过最佳区域检查；1050 完成进入世界。

## 版本控制与工作区记录

Windows 提交 `135014b` 包含场景、bootstrap builder 及对应 `.meta`，以及 SDK 构建产生的 40 个 `SerializedUdonProgram` 资产变化；这些资产仅 `serializedSignature` 字段由 SDK 填充。Unity 自动修改的 `ProjectSettings.asset` iOS `m_Automatic` 已还原，未提交。此交接不执行 Git 操作，也不包含任何绝对本机用户路径、用户名、私网地址或主机名。

## 未测 / 后续门禁

真人 VR、Station、多人/网络、性能均未测试，按计划属于 T04/T05 及后续任务；本证据不推导这些项通过。世界未上传、未发布。T00 的两个验收点有证据支持，但第 856 行 SDK 观察项仍可由后续集成/构建排查继续解释。