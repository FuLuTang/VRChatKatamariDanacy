# T00 验收证据摘要

## 证据采样

本次文档重写依据 Lead 已核实的 Editor 与客户端日志事实；没有远程访问 Windows、操作 Unity/MCP 或读取本机日志。

- 采样日期：2026-10-12；Windows 本地时间约 15:47（具体时区未记录）。
- Unity Editor：2022.3.22f1，revision `887be4894c44`。
- VPM：`com.vrchat.base` 与 `com.vrchat.worlds` 均为 3.10.5（以 `Packages/vpm-manifest.json` 为据）。UdonSharp 使用 Worlds 包内置目录 `Packages/com.vrchat.worlds/Integrations/UdonSharp`；未主张独立的 UdonSharp 包版本。
- 验收对象：由用户在普通用户权限的 Editor 中生成的空白 T00 bootstrap 场景；本地 Build & Test。

## T00 验收结论

| 验收项 | 结论 | 边界 |
|---|---|---|
| 目标 SDK 编译成功 | **PASS** | 当前 Editor.log 记录 8 个脚本编译成功；全日志未发现指定编译错误标记。 |
| Build & Test 可进入空场景 | **PASS** | SDK 生成了 115.5 KB 的本地世界 bundle；VRChat 本地客户端成功加入并完成进入，用户确认正常进入世界。 |

Build & Test 期间 Editor.log 仍出现 `Build Finished, Result: Failure.`（第 856 行）。该消息位于 SDK player-build 阶段；同一轮仍记录 Level 0、生成 `.vrcw` bundle，并由客户端成功加载进入。该观察如实保留，尚未解释其 SDK 内部含义；它没有阻止本任务所定义的“Build & Test 可进入空场景”验收。不将其隐去，也不据此宣称整条 SDK 构建日志无异常。

## 场景与变更摘要

- 场景：`Assets/VRC_Katamari/Bootstrap/T00_Bootstrap.unity`，GUID `022cd2218fedebb4a84f5d91f0b4022a`。
- 生成器：`Assets/VRC_Katamari/Bootstrap/Editor/T00BootstrapBuilder.cs`，GUID `4fc37ce5061d8f746babefbc61a112a1`；用户从 `VRC Katamari → T00 → Create Bootstrap Scene` 菜单执行。
- 场景包含 `VRCWorld` 与 `VRCSceneDescriptor`、Ground 平面、Spawn、Reference Camera；只用于 T00 空场景门禁，不代表玩法实现。
- Windows 提交：`135014b`。包括场景、生成器及 `.meta`；SDK 构建填充的 40 个 `SerializedUdonProgram` 资产只修改了 `serializedSignature` 字段。`ProjectSettings.asset` 的 iOS `m_Automatic` 自动变化已还原，未提交。

## 明确未测及边界

没有进行真人 VR 体验、Station、多人/网络、性能测试；这些属于 T04/T05 及后续任务。未上传或发布世界。编译与本地进入证据仅满足 T00 两项验收，不冻结公共接口，不代表游戏玩法、平台行为或性能通过。

详细日志依据见 [compile_evidence.md](compile_evidence.md) 与 [build_and_test.md](build_and_test.md)；交接记录见 [handoff.md](handoff.md)。