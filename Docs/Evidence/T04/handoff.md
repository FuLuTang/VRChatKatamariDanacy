# T04 Windows 侧变更交接

## 本轮操作范围与结果

- Unity 在用户 Windows PC；Unity Editor 所有者为用户，工程 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`。授权写入范围仅 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/` 及 Linux `Docs/Evidence/T04/`。
- 本轮只修改 `T04PlatformDriver.cs` 的 JumpImpulse 恢复策略：新增 `defaultJumpImpulse=3f` 与官方依据注释；恢复改用常量3，不回写读到的0；恢复日志增加 `baseline=default` 并继续记录 reread/reason；说明 Disable Station Exit 是 Jump 防退座主机制。Respawn/按钮先恢复并写各自原因，再请求Station退出，避免StationExited同步回调抢先消费恢复记录。`initial` / `saved` 诊断日志保留。载具摆动、去抖、按钮分支日志不变。
- 未做任何 Git 写入；未关闭/重启 Unity、未关客户端；未输入验证码/密码；未上传/发布或操作账号其他功能。
- 编译：`%LOCALAPPDATA%\Unity\Editor\Editor.log` 行4603为 `Scripting: domain reloads=0, domain reload time=0 ms, compile time=0 ms, other=0 ms`；行4516和4531有较早 UdonSharp 124 scripts 完成记录，但无法确认在本轮源码修改后。本轮最新代码编译**未确认**。最新日志段未见 `error CS`，但不可据此声称本轮编译PASS。
- Build & Test：SDK面板截图 `/tmp/cua/shot.png` 正显示 Authentication 数值验证码提示，未触发Build & Test；未输入验证码；没有本轮新客户端。没有点 Upload/Publish。
- T04客户端运行证据由Lead提供：`output_log_2026-10-12_17-12-57.txt` 行1002 `[T04] JumpImpulse initial=0 canJump=False`，行1131 `[T04] StationEntered seated=true; JumpImpulse saved=0 set=0`。此次读值不可作保存基准。完整结论见 [PC结果](pc_results.md)。

## Windows `git status --short --untracked-files=all`

本轮结束读取的原始完整输出（只读 Git 状态；未提交、暂存、清理或还原）：

```text
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/17830d157f00b09408e9c38c2091a851.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/18a8a73823b22934e929c67357a4e2d7.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/1acedb947e4c9dc4d8f749557d611c1e.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/1f7e9fb643472ef4d83f2ad49fe34b18.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/3053cc98f03a13041a10e0650d9b6e24.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/33324f447d661da40b534c9fa93c0ec2.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/3d024fda64377514ab33fa3baefec378.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/4715e20276be3b141a6a216230cab4e9.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/49ba99e73d9ab4349871a10f182ee457.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/4d685674de27be844ad0f9920614fd2c.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/4eb7aa2be7d95324ea25c03bf1cab34f.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/5463afdb73b505649b857341d262468a.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/566cc00e27d5822449529a3785eae366.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/62d157ce28de1064b8095c8cd096e77c.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/6657daa4973ee1249aae293810e8bccd.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/667c46fd2864a124ea921d98d4914fe5.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/68d999abd6627d04999b5bebe2438687.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/699261d683532df468f1ed17ff8c8cf1.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/75f3b7bb830f4324a8b2f49814f3493a.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/8175c20f542fbaf40811f752bfbf8759.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/853a35cf0f51df6498d68490a1f662e3.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/8732b730b248f4344a2839981e1ff9f0.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/953e2e6278cc9314f9f2913d9bc25309.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/96ee11c7074d21e4f84e189ad35d45fc.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/980a7697571ae1540827c8b930f79790.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/9d916a2228b78c646aa46fe3ba85879d.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/a1125ad687f8d9f41b0da3667d153a30.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/a3b924aa5d416d94c86ddd019ccb8441.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/a7250c474046ad245ac64456f76800ca.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/acd8738ca64f5a9448dfb040d1f2e4d5.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/b35f97b1813cb064d852c92d1c5c1751.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/b42eae3a19d2e89448e47dd9c7f5d0e2.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/b5280742086799a4c8c0a14e90cd913d.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/b8e95bf9755820c4380659898968aaf5.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/ba069e3ec8479394a8a01259fbacbfe0.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/c8df303ceb45ae84f85a11591f741734.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/d78d0aa68ba85fb43bbfaa649e8e398c.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/da113172081f2ba40b9cc46674a846d0.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/dfcb9d6121fc4084e97b5303b0054618.asset
 M VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/fa52d8b703fd906459112715b1ef1f3a.asset
 M VRChatKatamariDanacyUnity/ProjectSettings/ProjectSettings.asset
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/5344e79a099715844af2c3872d362a72.asset
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/5344e79a099715844af2c3872d362a72.asset.meta
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/599405add3a9d2b45883b07e0c83ee53.asset
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/599405add3a9d2b45883ee53.asset.meta
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/8f4c09d18e339a245b89f974ad7eaa2e.asset
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/8f4c09d18e339a245b89f974ad7eaa2e.asset.meta
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/e1773a35b06d6894aa5b506fd0b1412d.asset
?? VRChatKatamariDanacyUnity/Assets/SerializedUdonPrograms/e1773a35b06d6894aa5b506fd0b1412d.asset.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/Editor.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/Editor/Editor.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/Editor/T04PlatformLabBuilder.cs
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/Editor/T04PlatformLabBuilder.cs.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04FollowRig.asset
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04FollowRig.asset.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04FollowRig.cs
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04FollowRig.cs.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04PlatformDriver.asset
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04PlatformDriver.asset.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04StationControl.asset
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04StationControl.asset.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04StationControl.cs
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04StationControl.cs.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04VisualRoll.asset
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04VisualRoll.asset.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04VisualRoll.cs
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04VisualRoll.cs.meta
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04_PlatformLab.unity
?? VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Prototypes/Platform/T04_PlatformLab.unity.meta
```

Unity 自动改动照实列出，不清理、不回滚、不归因到本轮 JumpImpulse 补丁。状态中的 `ProjectSettings.asset`、`SerializedUdonPrograms`、原型 `.meta`、场景与 ProgramAsset 均保留原状。

## 后续交接

- 需用户在当前 SDK 登录状态可用后，于现有 Editor 先确认新代码 C# / UdonSharp 编译，再启动本地 Build & Test；不要上传/发布。
- 新客户端重点核对 initial/saved 读值、Jump一次/长按不退座、退出后默认恢复3并 reread=3，以及 station-exit / respawn / world-exit-button 三条独立原因。
- 不关闭现存 VRChat 客户端（本轮没有新开的客户端）；不要重启 Unity。T05真人头显项目按 [清单](vr_checklist_for_T05.md) 执行。
