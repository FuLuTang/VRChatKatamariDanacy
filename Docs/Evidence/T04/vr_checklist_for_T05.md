# T05 真人 VR 门禁与 PC 已验证项

**PC 桌面门禁已 PASS；真人 VR 项仍 NOT_RUN。** 下表保留 PC 证据与未完成的 D1 视觉确认；不能将 PC 结果外推为 VR 结论。

## PC 已验证结果

| 项目 | 状态 | 依据 |
|---|---|---|
| 两轮 Build & Test 可进入 Local Test | PASS | 第一轮 `output_log_2026-10-14_00-21-44.txt` 行 561/1018；第二轮 `output_log_2026-10-14_00-44-36.txt` 行 614/1021。 |
| 最终修复版本编译 | PASS | Editor.log 行 1378：`Compile of 12 scripts finished in 00:01.583`；无 `error CS`，程序资产时间戳更新。 |
| Station 可进入，坐姿 Jump 不退座 | PASS（PC） | 第二轮客户端行 1586–1654；7 次计入均 `seated=True`，每次有 JumpCue。 |
| 第一轮长按不重复 | PASS（PC） | 第一轮客户端行 1658–1659、1672–1675。第二轮未将长按单独复测结论重复标记。 |
| 坐姿退出按钮恢复 JumpImpulse | PASS（两轮） | 第一轮行 1677–1678；第二轮行 1600、1610、1630、1645、1659 均 `restored=3 reread=3 ... baseline=default`。 |
| 修复后 D1 代码状态路径 | PASS（日志） | 第二轮行 1586、1603、1614、1635、1649 记录 disabled；行 1599、1609、1629、1644、1658 记录退出按钮路径 enabled。 |
| 修复后提示是否视觉消失 | NOT_RUN（待用户确认） | 日志证明状态切换代码路径执行，不是视觉证据。 |
| 修复后 Respawn / 自然 Station-exit 后 Interact 恢复 | NOT_RUN | 第二轮未触发这两条路径。第一轮行 1716/1718 的 JumpImpulse 恢复发生在修复前。 |
| 桌面交互 | PASS（用户实测） | 对准载具后鼠标左键；E 键无效。VR 控制器方式未测。 |

## 场景视觉识别

用户描述的三个物体是：

1. **大长方体：载具 / Station**，入座交互目标。
2. **一个圆：VisualRoll**，坐姿 JumpCue 变色对象。
3. **小长方体：退出按钮**，坐姿时退出 Station。

PC 桌面已实测对准载具后用**鼠标左键**交互；E 键无效。VR 控制器交互尚未验证。

## T05 真人 VR 待测清单

| 项目 | 操作与证据要求 | 状态 |
|---|---|---|
| VR-A 输入候选 | 头显中入座/离座、Jump 单击/长按，确认只触发一次且不退座 | NOT_RUN |
| VR-B 输入/追尾候选 | 测另一输入与追尾方案并记录差异 | NOT_RUN |
| D1 视觉确认 | 坐下后准星持续对着大长方体载具，确认“坐下”提示是否消失；离座后确认可重新交互。记录用户目视结论 | NOT_RUN（等用户确认） |
| D1 退出路径 Interact 恢复 | 分别测 Respawn 与自然 `OnStationExited`；检查提示恢复，不引用第二轮按钮路径替代 | NOT_RUN |
| 舒适转向/头部自由转动 | 记录头部是否独立转动、视觉滚动与舒适性；不适即停 | NOT_RUN |
| 视野与安全 | 能识别载具、VisualRoll 与退出按钮并随时退出 | NOT_RUN |
| 大/小 Avatar | 自愿选择不同体型，记录入座、视野、追踪及安全退出 | NOT_RUN |
| 多人同步 | 多客户端比较 Station 状态、输入和提示 | NOT_RUN |

## 注意事项

- D1 修复改动说明见只读 [fix_seated_prompt.md](fix_seated_prompt.md)。当前准确状态为：**日志已验证，视觉待用户确认**。
- JumpCue 是无道具阶段的 VisualRoll 变色替代，不是道具系统。正式世界需显式配置跳跃/移动参数；initial=0 不等于玩家原设置。
- PC 和 VRChat 用户自用客户端日志必须区分；路径使用 `%USERPROFILE%`，避免记录个人身份信息。
- Station 是否为正式主方案必须由 T05 真人 VR 与用户决定。