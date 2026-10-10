# Windows Unity 初始化与交接

更新：2026-10-11。用户已决定在 Windows PC 初始化 Unity；主管和文档队员当前在 Linux 共享仓库工作。本页是操作清单，不是初始化成功报告。

## 你现在做什么

1. 在 Windows 确认/准备同一个仓库副本，记录它的**绝对路径**。
2. 使用 VRChat Creator Companion（VCC）创建 **Worlds** 工程，不是 Avatars。先按 [VRChat 当前 Unity 版本说明](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) 和 VCC提示选择兼容版本，不仅凭旧设计包的2022.3.22f1作决定。
3. 目标目录建议是 Windows 仓库下 `UnityProject/`。如果 VCC 要求空目录，可先在仓库外空目录创建，再在编辑器关闭后迁入；不要删除仓库、不覆盖已有Assets或.meta，不套出 `UnityProject/项目名/Assets` 的额外层。实际位置不同先报告，主管再修改映射。
4. 通过VCC管理Worlds SDK及所需UdonSharp依赖。记录实际精确版本，不手写猜测的版本锁，不导入候选第三方资源或MCP插件。
5. 打开工程，等导入与编译完成。建立最小空测试场景，配置SDK要求的世界组件，保存场景与.meta。
6. 检查Console，执行SDK的本地Build & Test。账号登录由你自己操作；不公开上传世界。若VRChat客户端/登录导致不能测试，记录缺少条件，不算PASS。
7. 将真实工程文件通过商定的Git/文件同步流程交回；`Assets/`、`Packages/`、`ProjectSettings/`和`.meta`保留，`Library/`、`Temp/`等缓存不交回。

## 特别注意：当前Linux文件尚未提交/推送

Windows的`git pull`不一定能获得刚整理的设计文档，因为当前变化仍在Linux工作区。不要让Win Agent根据旧仓库自行理解玩法。先约定由主管提交/传输文档，再交接固定版本；此页不意味着已同步到Windows，也不意味着主管已经派出远程命令。

同步必须是显式单向交接或Git提交交换，禁止两个编辑器对同一个网络共享目录同时写入。不要覆盖Windows已经创建的工程。远端仓库公开性未核实，测试账号/私人Avatar截图不要顺手提交到公开仓库。

## 初始化后给主管的回执

```text
Windows仓库绝对路径：
Unity工程绝对路径：
Unity版本：
VCC版本：
Worlds SDK版本：
UdonSharp版本/随SDK管理情况：
是否使用UnityProject作为工程根：
Console编译错误：无 / 有（附日志）
Build & Test：NOT_RUN / PASS / FAIL / BLOCKED
场景路径：
构建日志/截图位置：
与Linux仓库的同步方式及提交：
是否已有Unity MCP：无 / 有（名称、版本、传输方式；不填token）
Win DSH/Codex可用调用方式：未知 / 说明
```

初始化完成不代表T04/T05通过，更不代表T06接口已冻结。主管审查编译和本地测试证据后才评审T00。

## 之后如何使用Win Agent / MCP

当前暂未选定或连接远程执行通道。默认建议：Win本地Code Agent是唯一Unity编辑器操作者；主管派发任务并审查结果，本地Agent按需要使用MCP。

如果改成主管通过Tailscale远控Unity MCP：同样只允许一个持有`unity-editor`操作权的Agent调用；其他队员只交付独立文件，不能抢编辑器。每次任务说明都重复Windows主机、工程绝对路径、写入范围、MCP工具与连接方式；未配置就报告，不虚构工具。

不要为了MCP把能执行任意代码的服务暴露到公网或整个局域网；优先本地绑定、SSH隧道或有访问控制的Tailscale链路。主管不擅自安装插件、开启监听、登录或操作桌面。
