# T04 PC 侧验证结果（2026-10-13收口轮）

本轮代码只改 JumpImpulse 恢复基准及对应注释/日志。用户提供的客户端日志文件名和行号按原样记录，不含账号、用户名、主机名或私网地址。

| 验证项 | 结果 | 文件 / 行 / 原文或理由 |
|---|---|---|
| 世界加载时 JumpImpulse 初值 | **PASS（观测到0）** | `output_log_2026-10-12_17-12-57.txt` 行1002：`[T04] JumpImpulse initial=0 canJump=False`。Lead 提供的新客户端日志；世界刚加载，尚未调用过任何 Set。 |
| 入座时保存值 | **PASS（观测到0；不能当基准）** | 同文件行1131：`[T04] StationEntered seated=true; JumpImpulse saved=0 set=0`。证明此次 API 读取0，说明读值不可信；不表示玩家默认跳跃设置本来为0。 |
| `GetJumpImpulse()` 在本环境恒为0 | **FAIL（“恒为”这一绝对命题尚未证实）** | 同一日志仅提供启动行1002和入座行1131两次读值。两处均为0，足以判定不可用作这次保存基准；未连续采样、未观察恢复后的 reread，不能声称所有时刻必为0。 |
| 恢复策略源码修改 | **PASS（静态检查）** | `T04PlatformDriver.cs` 当前源码：定义 `defaultJumpImpulse=3f`；`RestoreJumpImpulse` 调用 `SetJumpImpulse(defaultJumpImpulse)`，日志附 `baseline=default`。Respawn/按钮先恢复并记各自原因，再请求Station退出，避免退出回调抢先记成自然退座。静态读源码，无对应运行日志。 |
| 官方默认值依据 | **PASS（官方文档核实）** | [VRChat Player Forces](https://creators.vrchat.com/worlds/udon/players/player-forces/)：“Default is 0, so set this if you want to enable jump in your world. The default VRCWorld prefab sets this to 3.” 代码取3是恢复至默认VRCWorld prefab配置，不是 API getter 的默认值。 |
| Disable Station Exit 主防退座机制 | **PASS（静态代码注释/配置保留）** | `T04PlatformDriver.cs` 入座处注释明确“Disable Station Exit is primary protection… zero jump is secondary”；本轮没有移除该 Station 设置。运行时防退座需 T05 复测。 |
| 最新 Editor.log 段无 error CS | **PASS（日志观测，不代表本轮编译成功）** | `%LOCALAPPDATA%\Unity\Editor\Editor.log` 行4603：`Scripting: domain reloads=0, domain reload time=0 ms, compile time=0 ms, other=0 ms`；最新段没有 `error CS`。 |
| 本轮恢复策略改动后的 Unity C# 编译 | **NOT_RUN / 未确认** | 同一 `Editor.log` 行4603 是零 compile time；没有本轮源码修改之后明确的 C# 编译完成记录。未将更早旧编译算作本轮 PASS。 |
| 最近可见 UdonSharp 编译 | **PASS（记录存在，但非本轮恢复源码的确认）** | `Editor.log` 行4516：`[<color=#0c824c>UdonSharp</color>] Compile of 124 scripts finished in 00:27.753`；行4531：`[<color=#0c824c>UdonSharp</color>] Compile of 124 scripts finished in 00:07.552`。未确认它们发生在本轮源码编辑之后，因此不作为本轮通过证据。 |
| SDK Builder 可用性 | **FAIL（本轮无法进入 Builder）** | CUA 截图 `/tmp/cua/shot.png` 显示 `VRChat SDK` 面板停在 `Authentication`，提示 `Check your email for a numeric code.`。未提供/输入验证码；遵守不操作账号限制。 |
| 本轮 Build & Test | **NOT_RUN** | 因 SDK 窗口仍处于数值验证码提示，未点任何账号验证控件，也未点 Build & Test；没有新 Build 日志或客户端。未点 Upload/Publish。 |
| 恢复日志三条路径 | **NOT_RUN** | 无本轮客户端日志证明 `reason=station-exit`、`reason=respawn`、`reason=world-exit-button` 各自触发或 reread 值。 |
| Jump 单击/长按去抖及不退座 | **NOT_RUN** | 本轮未运行新测试客户端或发送按键，无新的 `InputJump` 日志。 |
| PC移动/FollowRig/镜头 | **NOT_RUN** | 本轮未运行测试客户端；无运行日志/画面证据。 |
| VR头显舒适性、A/B控制与眼高 | **NOT_RUN** | 需T05真人头显参与，本轮未做。 |

## 关键解释

官方文档区分 API 默认0 与 `VRCWorld` prefab 默认设置3。当前恢复常量3的目标是让测试世界恢复到默认 prefab 设置；恢复后如果 reread 仍非3，日志会明确显示不匹配，不能把设置调用本身当成功。`Disable Station Exit` 才是阻止 InputJump 退座的主机制，归零Jump只作附加防护。
