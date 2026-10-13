# T05 真人 VR 与 PC 复测清单

**本轮最终改码后未能完成 Unity 编译确认或 Build & Test；PC运行与全部VR真人体验均 NOT_RUN。** SDK窗口停在数值验证码提示，未输入验证码/操作账号。已观测到的前置客户端日志见 [PC 结果](pc_results.md)。

## 已知修复/安全约束

- 当前恢复策略不再把 `GetJumpImpulse()` 的0作为玩家原设置恢复值，而是显式恢复至 `defaultJumpImpulse=3`（VRChat 官方说明 `VRCWorld` prefab 设置3；API本身默认0）。代码记录 restored/reread/reason/baseline=default。必须实测 reread；否则仅为设置调用，不算恢复成功。
- `Disable Station Exit` 是防止 Jump 退座的**主机制**；`SetJumpImpulse(0)` 是附加措施，不能替代 Station 设置。
- 新客户端观察：`output_log_2026-10-12_17-12-57.txt` 行1002 原文 `[T04] JumpImpulse initial=0 canJump=False`；行1131 原文 `[T04] StationEntered seated=true; JumpImpulse saved=0 set=0`。两次都为0，已证明不可作保存基准；并非有多时刻持续采样证明其永远为0。

## 启动前

1. 在用户现有 Unity Editor 前台等待导入/编译结束，查看最新 `Editor.log`，确认本轮 `T04PlatformDriver.cs` 修改确实进入最新 C# / UdonSharp 编译，且无 `error CS`。旧编译日志不能代替本轮。
2. SDK Builder 可用后启动本地 **Build & Test**，不点 Upload/Publish；保持该次客户端开启。
3. 记录启动前已有的 VRChat 客户端，仅测试新建的客户端；禁止关闭/重启 Unity 或其他程序。
4. 确认 Station 的 **Disable Station Exit** 已启用，作为防退座主机制；不以 JumpImpulse 归零代替。
5. 自愿参与、低速短测、有观察员与明确停测方式；任何不适即停。

## PC 与 Station 用例

| 用例 | 步骤与通过证据 | 状态 |
|---|---|---|
| P-00 本轮编译 / Build & Test | 最新源码编译完成，无 error CS；Build & Test 启动新客户端 | NOT_RUN；SDK Authentication 数值验证码提示阻塞，未输入验证码 |
| P-01 初始 JumpImpulse | 核对 `[T04] JumpImpulse initial=<v> canJump=<bool>` | PASS（初值观测为0；`output_log_2026-10-12_17-12-57.txt` 行1002）；当前代码不得据此宣称用户原设置为0 |
| P-02 入座保存/归零 | 核对 `StationEntered seated=true; JumpImpulse saved=<v> set=0` | PASS（观测 saved=0；同文件行1131；不可作为恢复基准） |
| J-01 单击Jump | Station内按一次，InputJump count只增一次且仍 seated | NOT_RUN |
| J-02 长按Jump | 长按不重复计数、不退座 | NOT_RUN |
| J-03 释放再按 | 释放后再次按，只新增一次count；检查BUTTON/value/frame | NOT_RUN |
| L-01 未坐下按退出 | 核对 `seated=false nothing-to-exit` | NOT_RUN |
| L-02 坐姿按退出按钮 | 核对实际离座及 `restored=3 reread=3 reason=world-exit-button baseline=default` | NOT_RUN |
| L-03 Station自然退出回调 | 核对 `restored=3 reread=3 reason=station-exit baseline=default` | NOT_RUN |
| L-04 Respawn | Respawn后离座，核对 `restored=3 reread=3 reason=respawn baseline=default` | NOT_RUN |
| M-01 桌面移动/镜头 | 离座后移动恢复、镜头不随VisualRoll滚转 | NOT_RUN |
| VR-A | 控制器手部输入、平面投影、限位、握持、菜单/丢追踪恢复 | NOT_RUN（T05真人） |
| VR-B | 摇杆及舒适转向对照、独立转头 | NOT_RUN（T05真人） |
| VR-X | A/B切换、菜单/focus loss后回中 | NOT_RUN（T05真人） |
| VR-C | 自愿短时体验，记录具体反馈 | NOT_RUN（T05真人） |
| VR-H | 安全调整眼高，观察球与前路可见性 | NOT_RUN（T05真人） |
| VR-Avatar | 不同自愿Avatar进出Station，观察头部/球位置 | NOT_RUN（T05真人） |

每条退出路径须分别看 `restored` 和 `reread`；其中 restored=3 且 reread=3 才能记该路径默认值恢复PASS。若 reread不是3，记录不匹配；不得用初值为0或写入 Set 调用声称恢复成功。Jump不退座主要由 `Disable Station Exit` 保证。

出现退座、无法退出、恢复失败、镜头/跟踪异常、晕动或疼痛即停止。证据使用匿名run_id，记录日期时区、版本、硬件/控制器、舒适设置、用例、日志文件名/行号与结果；不收集账号或个人身份信息。
