# T04 平台实验：PC 桌面门禁结果

## 当前结论

PC 桌面运行门禁 **PASS**。Station 可进入；Jump 不退座，第一轮长按不重复，第二轮再次观察 7 次坐姿计入；退出按钮恢复 `JumpImpulse=3` 并回读 3。D1 的 Interact 禁用/恢复代码路径有日志验证；用户于 2026-10-14 在第二轮修复后 PC 手动测试中目视确认：“坐下后，准星继续对着载具，‘坐下’提示消失了”。D1 现为 **PASS（用户目视，PC 桌面，修复后版本）**。PC 桌面层面的 T04 步骤 3（InputJump、Disable Station Exit、JumpImpulse 保存/恢复、Respawn/退出）已有证据；修复后 Respawn/自然 `OnStationExited` 的 Interact 恢复仍 NOT_RUN。Station 仍是 PC 层面首选候选，不是已批准的正式主方案；批准必须等待 T05 真人 VR。

## 两轮实测

### 第一轮（D1 修复前）

用户手动完成 PC 桌面 Build & Test，客户端日志为 `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_2026-10-14_00-21-44.txt`。行 561 进入 Local Test，行 1018 完成进入世界。该轮坐姿 Jump、长按、退出按钮恢复及 Respawn/`OnStationExited` 的 JumpImpulse 恢复通过；修复前观察到 D1 提示未关闭。逐项记录见 [PC 结果](pc_results.md)。

### 第二轮（修复后）

用户手动完成第二轮 Build & Test，客户端日志为 `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_2026-10-14_00-44-36.txt`；文件名时间戳 2026-10-14 00:44:36，时区未提供。行 614 进入 Local Test，行 1021 完成进入世界。日志记录 12 个脚本在最终构建中编译完成（Editor.log 行 1378），且无 `error CS`。第二轮入座/坐姿退出按钮路径多次记录 Interact disabled/enabled 与恢复 JumpImpulse；之后用户目视确认提示消失（用户原话见 [PC 结果](pc_results.md)）。

Editor.log 行 1204–1207 的 `CS1012` 来自文件写入过程中的一个中间版本。此前有 `Compile finished` 发生在最终文件状态确定之前，不能证明最终版本已编译。本轮 Build & Test 强制同步编译，最终 Compile 记录为行 1378 `Compile of 12 scripts finished in 00:01.583`；Udon 程序资产时间戳更新。教训：以最终构建/同步编译及程序资产更新时间为准，不用写入中间态的一条 Compile 完成记录作为最终证明。细节见 [PC 结果](pc_results.md)。

两轮 Editor.log 均有 SDK player-build 阶段 `Build Finished, Result: Failure` 观察项；行号未提供，原因待解释。两轮均生成 `scene-standalonewindows64-t04_platformlab-…vrcw` 且客户端成功进入。这条 SDK 子阶段消息保留，但不改变 PC 本地进入门禁 PASS。

## D1 状态与解释边界

- **D1：PASS（用户目视，PC 桌面，修复后版本；2026-10-14）。** 用户确认坐下后准星继续对着载具时“坐下”提示消失。第二轮入座/坐姿退出按钮日志分别证明 Interact disabled/enabled；修复后 Respawn 或自然 `OnStationExited` 路径上的 Interact 恢复仍为 **NOT_RUN**。第一轮 Respawn 的 JumpImpulse 恢复发生在 D1 修复前，不能替代这两条修复后检查。
- 修复前的缺陷是：坐下后若准星仍对准载具，仍显示“坐下”提示。来源是 `T04PlatformDriver.Interact` 调用 `UseStation`，不是 VRCStation 自带提示。修复变更说明见只读文件 [fix_seated_prompt.md](fix_seated_prompt.md)，本文件未改动该说明。
- 测试场景没有显式配置玩家移动参数，和 `initial=0` 相符但不是已证实原因。官方默认 VRCWorld prefab 值为 3、API 默认值为 0；正式世界必须显式配置跳跃/移动参数，初始 getter 读值不能当作玩家原设置。
- 无道具轻提示仍以 VisualRoll 变色替代；非坐姿退出按钮只记录日志、没有可见反馈。

桌面交互已由用户实测：对准交互目标后**鼠标左键**，E 键无效。真人 VR（A/B）、舒适性、大/小 Avatar、多人同步均为 NOT_RUN，留给 T05。Station 保留 PC 层面首选候选，须由 T05 真人 VR 决定是否批准为主方案。

详见 [PC 结果](pc_results.md)、[T05 清单](vr_checklist_for_T05.md)、[Station 决策](station_decision.md) 与 [handoff.md](handoff.md)。