# T00 本地 Build & Test 证据

## 结论：可进入空场景 — PASS

采样日期为 2026-10-12，Windows 本地时间约 15:47（具体时区未记录）。用户在 Unity Editor 中执行 `VRC Katamari → T00 → Create Bootstrap Scene`，生成项目相对路径 `Assets/VRC_Katamari/Bootstrap/T00_Bootstrap.unity`，GUID `022cd2218fedebb4a84f5d91f0b4022a`。生成器为 `Assets/VRC_Katamari/Bootstrap/Editor/T00BootstrapBuilder.cs`，GUID `4fc37ce5061d8f746babefbc61a112a1`。

场景含 `VRCWorld`、`VRCSceneDescriptor`、Ground 平面、Spawn 与 Reference Camera。它是空的 T00 引导/验收场景，不是玩法场景。

## Editor.log 构建观察

- 第 585/587 行：打开并加载 `T00_Bootstrap` 场景。
- 第 847 行：`Level 0` 大小 `115.4 KB compressed`。
- 第 856 行：出现 `Build Finished, Result: Failure.`。
- 第 879 行：生成 `scene-standalonewindows64-t00_bootstrap-<SDK-generated suffix>.vrcw`，大小 `115.5 KB`。

第 856 行失败消息位于 SDK player-build 阶段，必须作为未解释观察项保留。它没有阻止本轮 bundle 生成，也没有阻止客户端加载进入。此处的 PASS 针对任务原文验收标准“Build & Test 可进入空场景”，不声称 SDK 日志每个子阶段均为成功，也不把该消息改写成成功。

## VRChat 本地客户端观察

客户端日志文件名为 `output_log_2026-10-12_15-47-41.txt`；日志中的启动参数引用了本地 `t00_bootstrap` bundle。绝对文件 URL 与本机目录不在此复制。

- 第 9 行：以 `BuildAndRun` 启动本地测试。
- 第 601–604 行：记录 `Entering Room: Local Test`、`Joining local:…` 与 `Successfully joined room`。
- 第 873 行：`Skipping best region check during Build & Test`。
- 第 1050 行：`Finished entering world.`
- 用户口头确认正常进入世界。

以上构成“本地 Build & Test 可进入空场景”的证据；未上传或发布世界。没有真人 VR、Station、多人/网络、性能测试，这些仍属 T04/T05 及后续验收范围。