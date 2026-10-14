# Windows Unity 初始化与交接

更新：2026-10-14。Unity 已由用户在 Windows PC 初始化；用户仍拥有 Editor，通常由用户本人操作。用户另行批准T57仅由一名Luna操作者执行，scope记录见本页末尾及AGENTS.md。本页记录已知工程路径和版本元数据，以及尚未通过的基线门禁。

## 你现在做什么

1. 在 Windows 确认/准备同一个仓库副本，记录它的**绝对路径**。
2. 使用 VRChat Creator Companion（VCC）创建 **Worlds** 工程，不是 Avatars。先按 [VRChat 当前 Unity 版本说明](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) 和 VCC提示选择兼容版本，不仅凭旧设计包的2022.3.22f1作决定。
3. 实际工程根为 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，仓库根为 `D:\VRChatKatamariDanacy`。执行路径映射已改为仓库内 `VRChatKatamariDanacyUnity/`；不移动、不覆盖现有Assets或.meta。
4. 通过VCC管理Worlds SDK及所需UdonSharp依赖。记录实际精确版本，不手写猜测的版本锁，不导入候选第三方资源或MCP插件。
5. 打开工程，等导入与编译完成。建立最小空测试场景，配置SDK要求的世界组件，保存场景与.meta。
6. 检查Console，执行SDK的本地Build & Test。账号登录由你自己操作；不公开上传世界。若VRChat客户端/登录导致不能测试，记录缺少条件，不算PASS。
7. Git中保留`Assets/`、`ProjectSettings/`、`Packages/manifest.json`、`packages-lock.json`、`vpm-manifest.json`和`.meta`；忽略`Library/`、`Temp/`等缓存，以及`Packages/`下除VPM resolver外的VRChat SDK包目录。新克隆工程由VPM恢复缺失包，遵照[VRChat官方VPM源控说明](https://github.com/vrchat-community/creator-companion/blob/debe9345e160bd692d0a3fb2ca19116e40e753be/Docs/docs/vpm/source-control.md)。

## 同步原则

Linux工作区与Windows仓库通过Git按提交顺序交换；不要让两个编辑器同时写同一份共享工程。先同步文档/忽略规则，再提交工程文件；不覆盖Windows已创建的文件。此仓库是公开仓库，只提交必要工程数据，排除缓存、个人设置、私有凭据及未获准的第三方包源码。

## Windows 工程回执（本轮检查）

```text
Windows仓库绝对路径：`D:\VRChatKatamariDanacy`
Unity工程绝对路径：`D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`
Unity版本：2022.3.22f1（ProjectVersion.txt）
VCC版本：未知
Worlds SDK版本：3.10.5（从vpm-manifest.json读取）
UdonSharp版本/随SDK管理情况：独立版本未核实
工程根是否为仓库内`VRChatKatamariDanacyUnity/`：是
Console编译错误：未检查（本轮未操作Unity Editor）
Build & Test：NOT_RUN（本轮未操作Unity Editor）
场景路径：`Assets/Scenes/VRCDefaultWorldScene.unity`
构建日志/截图位置：未提供
与Linux仓库的同步方式及提交：Git；工程已推送至公开`origin/main`，提交`f211f9e`
是否已有Unity MCP：未确认（本轮未尝试连接）
Win DSH/Codex可用调用方式：未知 / 说明
```

初始化完成不代表T04/T05通过，更不代表T06接口已冻结。主管审查编译和本地测试证据后才评审T00。

## 之后如何使用Win Agent / MCP

当前暂未选定或连接远程执行通道。默认建议：Win本地Code Agent是唯一Unity编辑器操作者；主管派发任务并审查结果，本地Agent按需要使用MCP。

如果改成主管通过Tailscale远控Unity MCP：同样只允许一个持有`unity-editor`操作权的Agent调用；其他队员只交付独立文件，不能抢编辑器。每次任务说明都重复Windows主机、工程绝对路径、写入范围、MCP工具与连接方式；未配置就报告，不虚构工具。

不要为了MCP把能执行任意代码的服务暴露到公网或整个局域网；优先本地绑定、SSH隧道或有访问控制的Tailscale链路。主管不擅自安装插件、开启监听、登录或操作桌面。

## T57 球层实验台单写者授权（2026-10-14）

用户授权一名当前模型为gpt-6-luna的Unity操作代理，仅为球层各层建立相互隔离的测试Prefab与独立实验场景，用户之后自行进入查看。Windows唯一可写目录：`D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity\Assets\VRC_Katamari\Prototypes\BallLayers\`；Linux仓库唯一证据目录：`Docs/Evidence/BallLayers/`。Unity MCP未确认；代理必须核验实际工程/Editor版本与Packages，不关闭或重启用户Editor。

2026-10-14 01:46 UTC只读预检确认Windows HEAD为`b5a298cf6c68a1e29c854eff3bdb6a6d1d81e20d`，且`VRChatKatamariDanacyUnity/ProjectSettings/ProjectSettings.asset`有一项既存未提交改动。用户于2026-10-14再次明确授权在**不打开/读取文件内容、不diff/hash/暂存/修改/还原/清理**该文件的前提下继续。操作员可只读验证分支包含T57授权提交`b4b6cf0ee3cecd95aa43a14b5e7bef5f528f0644`，并排除此文件检查其他dirty paths；若有其他dirty文件则停止。不得让T57导入/编译写ProjectSettings；如需写入则停，不要保存。用户随后授权Lead一次`git fetch origin main`及条件式快进。只读路径列表确认待合并提交只涉及Docs/tests/tasks（无Unity项目、ProjectSettings、Packages或场景路径）；Lead执行`git merge --ff-only origin/main`后，Windows HEAD为`3cb5def209c2c96a1f1afbde8faaafb3a517b5ea`，授权commit现为其祖先。快进前后仅用排除ProjectSettings的状态检查确认没有其他dirty路径；该已知文件未打开/读取/触碰，且不在快进路径列表中。未操作Unity/Editor。用户于2026-10-14明确选择重新派发T57单一subagent，并免除该任务独立的`session/projections`路由投影核验，接受当前`gpt-6-luna`父会话继承路由。新操作者已通过只读安全预检，但子代理`436d99b5-827b-45a9-b654-e856e0f60f43`随后在完成前失败。Lead只读快照显示T04_PlatformLab、旧SDK Build Succeeded弹窗、Play未激活，并可见T57脚本CS1061错误；没有保存或触碰ProjectSettings。用户已手动关闭旧SDK Build Succeeded弹窗；Lead新截图确认场景仍为T04_PlatformLab、无dirty星、Play未激活，但CS1061仍在；只读源码发现`RunFollow`中`float d`遮蔽descriptor参数。T57现BLOCKED，等待用户选择手动修复或经路由核验的新操作者。

允许的操作止于新对象/脚本写入、保存与Editor脚本/本地UdonSharp编译；不进入Play、不执行Build & Test、不启动VRChat客户端、不上传、不改正式世界场景、ProjectSettings、Packages或scope外文件。T51/T56只用显式synthetic seed和事件，不定义生产ballSeed、不连接正式网络/计分/碰撞。每项资源连同新`.meta`留在此scope并记录编译证据；遇到路径/版本/dirty-state冲突即停下报告，不拓展范围。此授权不影响其他项目或用户窗口，也不授权另一代理同时进入该Editor。
