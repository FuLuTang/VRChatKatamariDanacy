# P05 术语与教学文案交接

## 交付物

- [`glossary.csv`](glossary.csv)：稳定文案ID及中/英/日候选、使用上下文与限制说明。
- [`states.md`](states.md)：阶段、账本提示、错误/恢复/退出/观战/KO/重生状态的候选文案和行为边界。
- [`onboarding.md`](onboarding.md)：VR A、VR B、Desktop分开的首次游玩教学步骤及共同目标/安全说明。

## 对齐与限制

- 依据v0.3范围、技术契约、玩法文档、T19及离线准备计划；没有重读私有聊天全文，也没有把旧聊天中的备选方案复活为决定。
- 明确区分未交存成长与已交存安全积分，预测拾取与裁决确认拾取；预测反馈不得冒充已确认比分。
- 区分KO/临时等待重生、自愿退出比赛、观战、下局排队与VRChat平台Respawn。KO不等于离开比赛名单。
- 单槽磁铁与火箭是v0.3已选择的项目功能方向；实现细节、参数及平台支持尚未验证，不代表原作PVP规则。Jump作为比赛内当前道具使用的**建议**输入映射：未写死控制器型号或按键。Desktop Space仅是候选默认，不对改键后的实际映射作保证；Station/事件可用性需T04平台验证。
- 对外默认通用称谓为“滚球 / Rolling Ball / 転がすボール”；Katamari仅标为internal_only参考作品称谓，不进入对外默认教学文案。
- 文案不宣称已经实现、舒适、公平或兼容，也不写死临时参数；英文/日文均待母语者校审。名称不是定稿。
- 没有冻结状态枚举、Udon字段、网络事件、计分算法或接口；T06及相应门禁未完成。

## 未做 / 权限边界

- 没有运行Unity、Blender、ClientSim、VRChat、网络或真实输入测试；没有做母语审校。
- Unity在Windows用户PC，确认工程路径 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`；用户拥有Editor，本轮未启动。Unity MCP未确认。
- 没有SSH、Unity MCP、Blender、Unity工程访问；没有下载、账号操作、付费、提交、推送或发布。
- 本轮写入范围仅 `Docs/Preparation/Terminology/`。

## 审核建议

1. 由主管审查账本与MatchFlow措辞，避免把预测、请求中、提交确认混成同一状态。
2. 请日文/英文母语审校者检查自然度、术语一致性与按钮文案长度。
3. T04验证Jump/Station、输入丢失与退出/Respawn恢复后，才更新对应“待验证”提示。
4. T19做真实HUD布局和读屏/截断测试；此文档不是T19完成或任何Unity门禁PASS。
