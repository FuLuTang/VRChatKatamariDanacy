# T04 Platform Lab — 本轮收口记录

日期：2026-10-13。范围仅为 `T04PlatformDriver.cs` 的 JumpImpulse 恢复策略及 `Docs/Evidence/T04/` 证据；没有 Git 写入。

## 修复与依据

- `[T04] JumpImpulse initial=...`、入座 `saved=...` 诊断日志保留。Lead 提供的新客户端日志 `output_log_2026-10-12_17-12-57.txt` 行1002 原文 `[T04] JumpImpulse initial=0 canJump=False`，行1131 原文 `[T04] StationEntered seated=true; JumpImpulse saved=0 set=0`。这与世界刚加载时尚未调用任何 `SetJumpImpulse` 的事实相符：本环境的 `GetJumpImpulse()` 读值为0，不能作为玩家原跳跃设置的可信保存/恢复基准。不能据 `saved=0` 声称已保存玩家设置。
- 官方 [VRChat Player Forces 文档](https://creators.vrchat.com/worlds/udon/players/player-forces/) 对 `GetJumpImpulse / SetJumpImpulse` 明确写明 API 默认值为0，而默认 `VRCWorld` prefab 会将其设为3。故代码注释引用此行为，恢复常量 `defaultJumpImpulse=3f`。这表示恢复到 VRCWorld prefab 的默认配置，不表示读回值必然为3；每次恢复仍 reread 并记录。
- 出站日志格式为 `[T04] JumpImpulse restored=<v> reread=<v> reason=station-exit|respawn|world-exit-button baseline=default`；`initial`/`saved` 诊断仍保留。读到的 `saved` 仅用于证据诊断，不再回写。Respawn 与退出按钮分支先记录并恢复各自原因、再调用 Station.ExitStation，避免同步的 StationExited 回调抢先消费恢复记录。
- **`Disable Station Exit` 是阻止 Jump 退座的主机制**；代码将其作为注释说明。`SetJumpImpulse(0)` 仅是辅助防护，不能替代该 Station 设置。
- 其他行为保持：载具摆动、Jump 去抖/边沿计数、退出按钮 seated 分支日志没有改动。

## 编译与运行结果

- Windows Editor 已置前。最新可读 `Editor.log` 行4603 是 `Scripting: domain reloads=0, domain reload time=0 ms, compile time=0 ms, other=0 ms`；行4516/4531分别有 `[UdonSharp] Compile of 124 scripts finished in 00:27.753`、`[UdonSharp] Compile of 124 scripts finished in 00:07.552`。但日志没有本轮这次恢复策略修改后明确的 C# 编译完成记录；因此**本轮最终源码编译结果 NOT_RUN/未能确认**，不能据旧编译记录报 PASS。检索到的最新日志段未出现 `error CS`，但“没看到错误”不等价于确认本轮编译通过。
- SDK 窗口当前为账号数值验证码提示（截图 `/tmp/cua/shot.png`），不是 Builder；没有输入验证码/操作账号。为避免误点，未触发 Build & Test。**本轮 Build & Test NOT_RUN**，无新客户端或新运行日志。
- 可确认的本轮运行证据只有 Lead 提供的 `output_log_2026-10-12_17-12-57.txt` 行1002和1131：读值为0。Jump 入座后仅一次空格、保持坐姿、按钮退出恢复、自然退座、Respawn 均未在本轮重新验证。详见 [PC 结果](pc_results.md) 和 [T05 清单](vr_checklist_for_T05.md)。

## 结论

(a) `GetJumpImpulse()` 在这次新客户端世界加载初始读取与入座读取均为0；环境实测表明它不能作为保存基准。严格地说，已提供日志只有启动和入座两次观测，并非本轮多次采样证明每一时刻恒为0；后续回读亦须实测。 (b) Jump 不退座的主机制是 `Disable Station Exit`。恢复采用文档注明的 VRCWorld prefab 默认值3，但由于本轮没有 build/runtime，新策略尚未运行验收。

Station 仍是隔离实验候选，不批准为正式主方案；真人头显、A/B舒适性、不同Avatar等必须由T05真人确定。详见 [Station 初步结论](station_decision.md)。

## 边界与变更

未关闭/重启 Unity，未输入验证码，未操作账号其他内容，未上传/发布，未关闭任何 VRChat 客户端，未运行 Git 写入。Windows 状态清单与 Unity 自动生成项见 [交接](handoff.md)。
