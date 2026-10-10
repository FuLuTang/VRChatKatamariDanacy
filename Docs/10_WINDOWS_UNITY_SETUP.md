# Windows Unity 初始化与交接

更新：2026-10-11。Unity 已由用户在 Windows PC 初始化；用户仍是 Unity Editor 唯一操作者。本页记录已知工程路径和版本元数据，以及尚未通过的基线门禁。

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
