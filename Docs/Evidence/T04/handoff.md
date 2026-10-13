# T04 证据交接

## 来源与操作边界

本轮只更新证据文档，依据 Lead 已核对的两轮用户手动 PC 桌面测试、Editor.log 与用户观察。未 SSH、操作 Unity/MCP、触碰账号、执行 Git 写入或发布。用户机器上另有自己的 VRChat 客户端；本记录仅使用指定测试客户端文件 `output_log_2026-10-14_00-21-44.txt` 与 `output_log_2026-10-14_00-44-36.txt`，路径统一写 `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\`。两份文件名时间戳的时区未提供。没有记录用户名、私网 IP、主机名或账号信息。

## 结果摘要

- 第一轮 PC 桌面 Build & Test 可进入 Local Test：PASS（客户端日志行 561、1018）。坐姿 Jump、长按及第一轮 JumpImpulse 恢复各有证据，详见 [PC 结果](pc_results.md)。
- 第二轮修复后 PC 桌面 Build & Test 可进入 Local Test：PASS（`output_log_2026-10-14_00-44-36.txt` 行 614、1021）。最终代码编译 PASS：Editor.log 行 1378 `Compile of 12 scripts finished in 00:01.583`，无 `error CS`，Udon 程序资产时间戳更新。
- 第二轮 7 次坐姿 Jump 均 `seated=True` 且有 JumpCue；入座 Interact disabled 与坐姿退出按钮 Interact enabled 均在日志中出现。用户于 2026-10-14 在第二轮修复后 PC 手动测试中确认：“坐下后，准星继续对着载具，‘坐下’提示消失了”。D1 当前为 PASS（用户目视、PC 桌面、修复后版本）。
- 第二轮只测坐姿退出按钮 D1 恢复路径；Respawn 与自然 `OnStationExited` 后 Interact 恢复 **NOT_RUN**。第一轮 Respawn 的 `JumpImpulse` 恢复为 PASS，但该轮在 D1 修复前。
- 两轮均记录 SDK player-build `Build Finished, Result: Failure`，行号未提供；属于待解释子阶段观察。bundle 仍生成，客户端成功进入。
- Station 建议不变：PC 层面可行的首选候选；T05 真人 VR 批准前不能定为正式主方案。

## 编译中间态经验

Editor.log 行 1204–1207 的 `CS1012` 来自队员写文件时的中间版本；此前中途出现的若干 `Compile finished` 不能证明最终源文件已完成编译。最终 Build & Test 的同步编译在 Editor.log 行 1378 记录 12 个脚本完成，且无 `error CS`；程序资产时间戳也更新。以最终构建/同步编译与资产更新时间确认版本，不以写入期间的单行消息作最终证据。修复变更范围按只读记录为单文件 `T04PlatformDriver.cs`。修复说明 [fix_seated_prompt.md](fix_seated_prompt.md) 是 Build 前的只读记录，本次未改它；其中“未编译”状态现由本次行 1378 最终编译记录和第二轮 Build & Test 证据更新。

## 待办与交接

1. T05 中复核 D1 提示在真人 VR 坐姿时是否消失；PC 桌面用户目视已 PASS，VR 尚未确认。
2. 在修复版本分别走 Respawn 与自然 Station-exit，检查 `Interact enabled` 是否恢复；当前均 NOT_RUN。
3. 评估非坐姿退出按钮无用户可见反馈；无道具时 JumpCue 仍只是 VisualRoll 变色替代。
4. T05 验证 VR-A/VR-B、舒适性、大/小 Avatar、多人同步，决定 Station 是否为主方案。
5. Lead 后续负责提交相关 Windows/T04 变更；本交接不声明提交号，也未执行 Git 写入。

逐项状态、原文与行号见 [PC 结果](pc_results.md)，测试辨识与步骤见 [T05 清单](vr_checklist_for_T05.md)，方案结论见 [Station 决策](station_decision.md)。