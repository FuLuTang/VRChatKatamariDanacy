# Station 决策：PC 首选候选，主方案待 T05

## 当前结论

**Station 在 PC 桌面层面可行，继续作为首选候选；不提前批准为正式主方案。** 两轮用户手动 Build & Test 均可进入 Local Test。坐姿 Jump 不退座；第一轮验证长按不重复及 JumpImpulse 离开恢复；第二轮验证最终代码编译与 D1 的入座/坐姿退出按钮 Interact 状态日志。正式主方案仍须由 T05 真人 VR 的舒适性、视野、控制器、Avatar 与安全退出门禁决定。

D1 的代码路径日志已验证，用户视觉确认未完成：第二轮记录入座 `Interact disabled (seated)` 与坐姿退出按钮 `Interact enabled`，但没有用户确认准星对着载具时“坐下”提示确实消失。第二轮没有触发 Respawn 或自然 `OnStationExited` 退座，修复后这两条路径的 Interact 恢复为 NOT_RUN。

## 两轮证据概述

### 第一轮（修复前）

客户端日志 `output_log_2026-10-14_00-21-44.txt`：
- 行 561、1018：进入并完成进入 Local Test。
- 行 1626–1675：7 次 Jump 计入、均 `seated=True`、每次有 JumpCue；行 1658–1659、1672–1675 长按无重复。
- 行 1677–1678：坐姿退出 `restored=3 reread=3 reason=world-exit-button`。
- 行 1716/1718：Respawn 相关 Station-exit 回调记录 `restored=3 reread=3 reason=station-exit`，之后记录 `Respawn requested station exit`；这是修复前 JumpImpulse 证据，不验证修复后的 Interact 状态。
- 修复前用户观察 D1 提示仍显示；后来才进行源码修复。

### 第二轮（D1 修复后）

客户端日志 `output_log_2026-10-14_00-44-36.txt`：
- 行 614、1021：进入并完成进入 Local Test。
- 行 965、966：第四次独立会话读到 `[T04] JumpImpulse initial=0 canJump=False`、随后 `Driver ready`。
- 行 1586、1603、1614、1635、1649：入座后记录 `Interact disabled (seated)`。
- 行 1599、1609、1629、1644、1658：坐姿退出按钮后记录 `Interact enabled (left station) reason=world-exit-button`。
- 行 1600、1610、1630、1645、1659：逐次记录 `JumpImpulse restored=3 reread=3 reason=world-exit-button baseline=default`。
- 行 1604、1615、1636、1650：重新入座读到 `saved=3 set=0`，与显式设置 3 后 getter 可读回 3 相符。
- 行 1591–1654：7 次坐姿 Jump 计入均 `seated=True` 且有 JumpCue。

Editor.log 行 1378 记录最终 Build & Test 同步编译 12 个脚本完成，1.583 秒，无 `error CS`；Udon 程序资产时间戳更新。行 1204–1207 的 `CS1012` 属写文件过程中的中间版本，不能用来判定最终文件；中间的 Compile finished 也不是最终构建证明。两轮仍有 `Build Finished, Result: Failure` player-build 观察项（第二轮行号未提供），bundle 仍生成、客户端仍进入；原因待解释。

## JumpImpulse 解释边界

第一轮初始读值为 0；第二轮行 965 第四次独立会话再次读到 0。测试场景没有显式配置玩家移动参数与该值相符，但这只是合理解释，不是已证实原因。Lead 核对的官方说明为默认 VRCWorld prefab 将 JumpImpulse 设为 3、API 默认值为 0。正式世界必须显式设置跳跃/移动参数；initial getter 值不是玩家原设置。默认值 3 也不证明恢复了玩家自定义原值。

## D1 与其他待办

- D1 修复后日志验证：入座禁用 Interact、坐姿退出按钮重新启用。用户视觉确认待完成；自然 `OnStationExited` 和 Respawn 两个修复后恢复路径尚未执行，均 NOT_RUN。代码修复说明见 [fix_seated_prompt.md](fix_seated_prompt.md)，此文件只读。
- 修复前缺陷源自 `T04PlatformDriver.Interact` 调用 `UseStation`，不是 VRCStation 自带提示。建议的生命周期为入座 `DisableInteractive=true`，离开时恢复；当前视觉是否修复仍须用户确认。
- 无道具轻提示仍为 VisualRoll 变色替代；非坐姿退出按钮仅有日志，无可见反馈。
- 桌面交互是用户实测的准星对准后鼠标左键；E 键无效。VR 控制器交互未测。

T05 真人 VR 必须体验 VR-A/VR-B、舒适性、头部独立转动、Avatar 适配、多人同步与退出安全，并由真人/用户决定是否批准 Station 为主方案。详细表见 [PC 结果](pc_results.md) 和 [T05 清单](vr_checklist_for_T05.md)。