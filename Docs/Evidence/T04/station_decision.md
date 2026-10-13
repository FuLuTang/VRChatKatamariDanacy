# Station 是否作为主方案：暂不批准，等待门禁

## 当前初步结论

**Station 仍只作为隔离实验候选，不批准为正式主方案。** 本轮只修复 JumpImpulse 恢复策略：`GetJumpImpulse()` 的初始/入座读值在本环境为0，恢复应明确设为官方 `VRCWorld` prefab 默认3，并 reread 取证。因为修改后编译确认和 Build & Test 均未完成，不能判定恢复、坐姿Jump或离站路径已通过。

## JumpImpulse 与安全机制

Lead 提供的客户端日志 `output_log_2026-10-12_17-12-57.txt`：

- 行1002：`[T04] JumpImpulse initial=0 canJump=False`（世界加载初始阶段，尚未调用任何 Set）。
- 行1131：`[T04] StationEntered seated=true; JumpImpulse saved=0 set=0`。

因此这两次读值为0，`GetJumpImpulse()` **不能作为本环境玩家原跳跃设置的保存/恢复基准**。严格说日志只涵盖两次读取，不能扩大成连续测量证明每个时刻“恒为0”；恢复后的 reread 尚未取到。

官方 [VRChat Player Forces 文档](https://creators.vrchat.com/worlds/udon/players/player-forces/) 区分 API 默认值0 与默认 `VRCWorld` prefab 将其设置为3。代码以 `defaultJumpImpulse=3f` 恢复到该 prefab 配置，日志应包含 `restored=3 reread=<v> reason=<...> baseline=default`。仅看到 Set 调用或 restored=3 不够，需 reread=3 才能将该路径标成恢复PASS。

Jump 不退座的**主机制是 Station 的 `Disable Station Exit`**；`SetJumpImpulse(0)` 只能是附加措施。不能依赖“清零Jump”来防止退出。

## 本轮证据和结果

- `Editor.log` 行4603：`Scripting: domain reloads=0, domain reload time=0 ms, compile time=0 ms, other=0 ms`。当前最新日志片段未见 `error CS`，但无本轮改码后的编译完成标记，最终源码编译未确认。
- `Editor.log` 行4516/4531分别有 `[<color=#0c824c>UdonSharp</color>] Compile of 124 scripts finished in 00:27.753` / `...00:07.552`；不能确认在本轮改码之后，故不作为本轮改动通过证据。
- CUA 的 VRChat SDK 窗口截图 `/tmp/cua/shot.png` 显示 Authentication 数值验证码提示。未输入验证码，不操作账号；因Builder不可用未点击Build & Test。无本轮客户端运行日志。
- 修改后 `restored/reread` 三路径、Jump防退座、Jump计数、Exit按钮行为全部 NOT_RUN。之前的新客户端只验证初始/入座读值0。

详细的逐项判定见 [PC结果](pc_results.md)；真人操作步骤见 [T05清单](vr_checklist_for_T05.md)。

## Station 方案哪些需T05真人决定

1. 自愿真人在真实头显中体验Station与备选B方案，判断实际舒适性、疲劳、可控感；文本/PC观察不能代替。
2. 头部是否保持独立、不随球转；眼高范围下是否能看见球和前路。
3. 不同尺寸/类型Avatar的入座、追踪、离座与碰撞/视野适配；松手、菜单、失去追踪后的恢复。
4. A/B输入、原生舒适转向、转头、Jump只触发一次且不退座、随时可安全退出。
5. 对Station作为主方案、备选或仅桌面原型的最终批准。

PC门槛须先完成本轮编译及Build & Test，再分别实测Station自然退出、Respawn和世界退出按钮三路，各核对 `restored=3 reread=3` 与原因字段；按钮需验证 seated / non-seated 分支。全部未通过前，不扩成正式输入模块。
