# T04 PC 桌面验证结果

两轮均为用户手动操作的 PC 桌面测试，不是自动化。第一轮唯一客户端日志：`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_2026-10-14_00-21-44.txt`；第二轮：`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_2026-10-14_00-44-36.txt`。同机用户自己的其他 VRChat 日志不属于测试。Editor 日志路径写作 `%LOCALAPPDATA%\Unity\Editor\Editor.log`；不记录用户名、私网 IP、主机名或账号信息。

## 第一轮：D1 修复前

| 项目 | 状态 | 日志文件、行号 | 原文 / 观察与边界 |
|---|---|---|---|
| Build & Test 进入本地世界 | **PASS** | `output_log_2026-10-14_00-21-44.txt` 行 561、1018 | `Entering Room: Local Test`；`Finished entering world`。用户确认正常进入。 |
| 初始 JumpImpulse 读值 | **PASS（观测值为 0）** | 同上，行 966–967 | `[T04] JumpImpulse initial=0 canJump=False`；`Driver ready`。这是连续第三次独立会话读到 initial=0；此前两份日志为 `output_log_2026-10-12_17-02-41.txt` 与 `output_log_2026-10-12_17-12-57.txt`，其行号未提供。 |
| 入座与 API 读值 | **PASS（观测）** | 同上，行 1623、1692 | 行 1623：`saved=0 set=0`；行 1692 再次入座：`saved=3 set=0`。这与显式 `SetJumpImpulse(3)` 后 getter 读回 3 相符，证明 API 可用，不代表 3 是玩家自定义原值。 |
| 坐姿 Jump 计数、不退座、提示 | **PASS** | 同上，行 1626–1675 | 共 7 次计入，全部 `seated=True`；每次对应 `[T04] JumpCue shown (no item system in prototype)`。用户确认圆变色；Jump 未退座。 |
| 长按不重复 | **PASS** | 同上，行 1658–1659、1672–1675 | `value=True frame=13293` 到 `False frame=13428`，约 4 秒无重复 True；第二次约 4 秒同样无重复。 |
| 坐姿退出按钮恢复 | **PASS** | 同上，行 1677–1678 | `ExitButton pressed seated=true exiting`；`JumpImpulse restored=3 reread=3 reason=world-exit-button`。 |
| Respawn 经 OnStationExited 恢复 JumpImpulse | **PASS（修复前，仅 JumpImpulse）** | 同上，行 1716、1718 | `restored=3 reread=3 reason=station-exit`；`Respawn requested station exit`。用户确认重生正常。该轮在 D1 修复前；不证明修复后的 Interact 在这条路径恢复。 |
| 非坐姿退出按钮分支 | **PASS（安全无操作分支）** | 同上，行 1682 | `ExitButton pressed seated=false nothing-to-exit`。但没有用户可见反馈，仍是待办。 |
| 离座后跳跃 | **PASS（用户观察）** | 无日志文件/行号；用户确认 | 用户确认“跳跃正常”；没有日志佐证。 |
| D1：入座后提示仍出现 | **FAIL（修复前历史观察）** | 无专门日志行；用户观察 | 坐下后准星仍对着载具时显示“坐下”。来源为 `T04PlatformDriver.Interact` 调用 `UseStation`，不是 VRCStation 自带提示。后续代码修复见第二轮；此行仅记录修复前状态。 |
| 真人 VR / 舒适性 / Avatar / 多人 | **NOT_RUN** | 无本轮相关日志 | 留给 T05。 |

## 第二轮：D1 修复后

第二轮客户端日志：`output_log_2026-10-14_00-44-36.txt`。日志所示第四次独立会话仍读到 initial=0。每项状态和引用如下：

| 项目 | 状态 | 日志文件、行号 | 原文 / 观察与边界 |
|---|---|---|---|
| 最终代码编译 | **PASS** | `Editor.log` 行 1378 | `Compile of 12 scripts finished in 00:01.583`；无 `error CS`。这是最终 Build & Test 同步编译，不是中途文件状态的孤立编译记录。 |
| Build & Test 进入本地世界 | **PASS** | `output_log_2026-10-14_00-44-36.txt` 行 614、1021 | `Entering Room: Local Test`；`Finished entering world`。 |
| 初始 JumpImpulse 与 Driver ready | **PASS（观测）** | 同上，行 965–966 | `[T04] JumpImpulse initial=0 canJump=False`；`Driver ready`。这是第四次独立会话读到 0。 |
| D1：入座时禁用 Interact | **PASS（代码路径日志已验证）** | 同上，行 1586、1603、1614、1635、1649 | 每次记录 `[T04] Interact disabled (seated)`。日志证明该状态路径执行，不证明提示在画面中已消失。 |
| D1：坐姿退出按钮后恢复 Interact | **PASS（代码路径日志已验证）** | 同上，行 1599、1609、1629、1644、1658 | 每次记录 `[T04] Interact enabled (left station) reason=world-exit-button`。与下行 JumpImpulse 恢复成对。 |
| 坐姿退出按钮恢复 JumpImpulse | **PASS** | 同上，行 1600、1610、1630、1645、1659 | 每次记录 `JumpImpulse restored=3 reread=3 reason=world-exit-button baseline=default`。 |
| 重新入座时读回 3 并再次置零 | **PASS（观测）** | 同上，行 1604、1615、1636、1650 | `saved=3 set=0`；与显式 `SetJumpImpulse(3)` 后 API 读回 3 相符。 |
| 坐姿 Jump 计数与不退座 | **PASS** | 同上，行 1591–1654 | 共 7 次计入，全部 `seated=True`，每次对应一条 JumpCue。 |
| D1 提示视觉消失 | **NOT_RUN（等待用户确认）** | 无视觉日志；用户确认尚未提供 | 日志证明 `DisableInteractive` 代码路径执行；不能替代用户目视确认准星对着载具时“坐下”提示确实消失。当前结论：**日志已验证，视觉待用户确认**。 |
| D1：Respawn 后恢复 Interact | **NOT_RUN** | 第二轮未触发 Respawn | 第一轮行 1716/1718 的 Respawn 是修复前版本，只证明 JumpImpulse 恢复，不证明 D1 的 Interact 恢复。 |
| D1：自然 OnStationExited 退座后恢复 Interact | **NOT_RUN** | 第二轮未自然退座 | 第二轮只验证退出按钮路径；不得把按钮日志外推为自然退座回调通过。 |
| SDK player-build 最终消息 | **FAIL（仅 player-build 子阶段；整体进入门禁仍 PASS）** | `Editor.log`；行号未提供 | 原文仍为 `Build Finished, Result: Failure`，原因待解释。同一轮 bundle 仍生成且客户端成功进入。 |
| 真人 VR / 舒适性 / Avatar / 多人 | **NOT_RUN** | 无本轮相关日志 | 留给 T05。 |

## 最终版本编译取证说明

Editor.log 行 1204–1207 的 `CS1012` 来自队员写文件过程中的中间版本；此前出现的若干 `Compile finished` 发生在文件仍被修改的期间，不能证明最终版本已编译。当前最终文件无单引号及非 ASCII 字符。以本轮 Build & Test 强制同步编译为准：行 1378 的 12-script Compile finished，且无 `error CS`；Udon 程序资产时间戳也已更新。教训：最终文件状态应由完成构建/同步编译及程序资产更新时间确认，不能单凭中途的一条 `Compile finished`。

## JumpImpulse initial=0 的解释边界

三次修复前会话及第二轮共四次独立会话读到 initial=0。测试场景没有显式配置玩家移动参数与该值相符；Lead 核对的官方说明为默认 VRCWorld prefab 设置 3、API 默认值 0。这是相符解释，不是已证实的原因。正式世界必须显式设置跳跃/移动参数；初始 getter 读值不可当作“玩家原设置”。

## 三种离开/恢复场景

- 坐姿退出按钮：第二轮行 1600/1610/1630/1645/1659 均 `restored=3 reread=3 ... reason=world-exit-button baseline=default`，且有 Interact enabled 日志。
- Respawn：修复前第一轮 JumpImpulse 恢复 PASS（行 1716/1718）；修复后第二轮 **D1 Interact 恢复 NOT_RUN**。
- 自然 `OnStationExited` 退座：修复后第二轮 **D1 Interact 恢复 NOT_RUN**。

第一轮的 `reason=station-exit` 是修复前的 JumpImpulse 恢复记录；不能用于声称 D1 修复后的 Respawn/自然退座 Interact 路径已验证。