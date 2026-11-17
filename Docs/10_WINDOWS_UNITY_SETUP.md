# Windows Unity 初始化与同步

Unity 运行在用户的 Windows PC 上，Editor 由用户拥有。工程路径、版本与操作规则：

- 仓库根 `D:\VRChatKatamariDanacy`，Unity 工程根 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`（仓库内 `VRChatKatamariDanacyUnity/`）。
- Unity 2022.3.22f1，VRChat Worlds SDK 3.10.5（VPM）；以 `ProjectSettings/ProjectVersion.txt` 与 `vpm-manifest.json` 为准。
- Editor 的操作方式（MCP 通道、单一操作者、写入边界）见 [AGENTS.md](<../AGENTS.md>) 与 [Tools/mcp/README.md](<../Tools/mcp/README.md>)。

## 初始化步骤

1. 在 Windows 准备同一个仓库副本，记下它的**绝对路径**。
2. 使用 VRChat Creator Companion（VCC）创建 **Worlds** 工程，不是 Avatars。先按 [VRChat 当前 Unity 版本说明](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) 和 VCC提示选择兼容版本。
3. 工程根为仓库内 `VRChatKatamariDanacyUnity/`；不移动、不覆盖现有Assets或.meta。
4. 通过VCC管理Worlds SDK及所需UdonSharp依赖。记录实际精确版本，不手写猜测的版本锁，不导入候选第三方资源。
5. 打开工程，等导入与编译完成。建立最小空测试场景，配置SDK要求的世界组件，保存场景与.meta。
6. 检查Console，执行SDK的本地Build & Test。账号登录由你自己操作；不公开上传世界。若VRChat客户端/登录导致不能测试，记录缺少条件，不算PASS。
7. Git中保留`Assets/`、`ProjectSettings/`、`Packages/manifest.json`、`packages-lock.json`、`vpm-manifest.json`和`.meta`；忽略`Library/`、`Temp/`等缓存，以及`Packages/`下除VPM resolver外的VRChat SDK包目录。新克隆工程由VPM恢复缺失包，遵照[VRChat官方VPM源控说明](https://github.com/vrchat-community/creator-companion/blob/debe9345e160bd692d0a3fb2ca19116e40e753be/Docs/docs/vpm/source-control.md)。

## 同步原则

Linux工作区与Windows仓库通过Git按提交顺序交换；不要让两个编辑器同时写同一份共享工程。先同步文档/忽略规则，再提交工程文件；不覆盖Windows已创建的文件。此仓库是公开仓库，只提交必要工程数据，排除缓存、个人设置、私有凭据及未获准的第三方包源码。
