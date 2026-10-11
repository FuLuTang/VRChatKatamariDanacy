# 状态、错误与玩家可见文案（草案）

用途：T19的文案/教学输入，不是已实现UI或冻结的状态接口。中文为主文案，英文和日文为待校审草案；没有做母语审校。状态字段名称仅描述玩家可见概念，不规定Udon枚举、网络字段或接口。

## 核心显示规则

- 首版项目设计已选单槽磁铁与火箭；这是项目功能方向，不是原作PVP道具事实。实际实现、参数与平台输入兼容仍未验证。
- **未交存成长**表示本回合已确认收集、仍承担风险的成长量；不等于分数。**已交存积分**是完成交分事务后确认的安全分。
- 本地**预测拾取**可先给轻量吸附反馈，但要标成“确认中”，不能提前增加正式成长/交存/吞噬结果。裁决确认后才显示为已确认拾取；被拒绝时回滚预测展示。
- “当前比分”只显示已确认的已交存积分。未交存成长可以在旁边单列并标注风险；回合结束后按已确认积分排序，不把未交存成长默认为得分。
- **KO/被吞**是当前回合内暂时失控并等待重生的玩法状态；KO本身不会自动退出参赛名单。**退出比赛**是玩家主动选择离开本回合并返回大厅/安全位置。**VRChat Respawn**是平台事件，触发清理/恢复流程；不要把它写成常规游戏退出按钮。
- **观战**与KO临时状态不同：观战者没有当前回合参赛资格；KO玩家仍是该局参赛者，按实现规则重生或在此期间可看稳定观战画面。
- 所有英文/日文句子和显示长度均须母语审查及实际UI布局检查。时间、保护、按钮支持/映射均不能由文案擅自承诺。

## 比赛阶段（概念，不冻结枚举）

| 玩家可见状态 | 中文建议 | English draft | 日本語 draft | 说明 |
|---|---|---|---|---|
| Lobby | 大厅 | Lobby | ロビー | 未加入正式比赛 |
| Countdown | 即将开始：{time} | Starts in {time} | {time}後に開始 | 人数/名单条件成立才倒计时；变量格式待工程定义 |
| Preparing | 正在准备本回合… | Preparing this round… | ラウンドを準備中… | 检查地图与名单；不能显示成比赛已开始 |
| Playing | 比赛进行中 | Round in progress | ラウンド進行中 | 显示剩余时间和双方账本状态 |
| Settling | 正在结算已确认结果… | Settling confirmed results… | 確定済みの結果を集計中… | 停止新收益；只按裁决规则处理截止前已接收有效请求 |
| Results | 本回合结果 | Round results | ラウンド結果 | 展示已确认安全积分及名次/并列 |
| Resetting | 正在重置并返回大厅… | Resetting and returning to the lobby… | リセットしてロビーに戻っています… | 清理回合临时状态并恢复玩家设置 |
| Recovery | 正在恢复比赛状态… | Recovering match state… | 対戦状態を復旧中… | 不承诺必定恢复；快照失败可取消本回合 |
| RecoveryCancelled | 本回合无法安全恢复，已取消 | Match could not be safely recovered and was cancelled | 安全に復旧できないため、このラウンドは中止されました | 需确保账本/物件状态不猜测、不重复发放 |

## 玩家/资源状态与短提示

| 触发场景 | 中文 | English draft | 日本語 draft | 说明 |
|---|---|---|---|---|
| 当前安全分 | 已交存：{score} | Banked: {score} | 預け入れ済み：{score} | 仅确认账本 |
| 当前风险成长 | 未交存成长：{amount} | Unbanked growth: {amount} | 未預け入れの成長量：{amount} | 不是比分；具体单位可本地化 |
| 预测拾取 | 正在确认拾取… | Confirming pickup… | 取得を確認中… | 不增加已确认数值 |
| 拾取确认 | 已确认拾取 | Pickup confirmed | 取得が確定しました | 仅在收到有效确认后显示 |
| 拾取拒绝/竞争失败 | 未能确认这件物品 | This item was not confirmed | このアイテムは確定しませんでした | 撤销预测奖励，不责怪玩家 |
| 交分请求处理中 | 正在确认交存… | Confirming deposit… | 預け入れを確認中… | 尚未代表交易完成 |
| 交存成功 | 已交存 {score} 分，球已重置 | Banked {score} points. Your ball has reset. | {score}点を預け入れました。ボールがリセットされました。 | 成功消息只能在事务确认后出现 |
| 无可交存成长 | 暂无可交存成长 | No growth to bank yet | まだ預け入れできる成長量がありません | 不说“积分丢失” |
| 交存未成功 | 交存未完成，请稍后查看状态 | Deposit not completed. Check your status before retrying. | 預け入れは完了していません。状態を確認してから再試行してください。 | 幂等保护；避免鼓励重复请求 |
| KO | 你被吞了，等待重生… | Swallowed. Waiting to respawn… | 飲み込まれました。リスポーン待ち… | 等待重生状态；项目原型方向，数值/时长不写死；不是退赛 |
| KO期间 | 本回合仍在进行 | You are still in this round | このラウンドは継続中です | 与退出/观战状态分别呈现 |
| 重生 | 已返回比赛 | You are back in the round | ラウンドに復帰しました | 保护提示只在确有保护时出现 |
| 保护 | 重生保护中 | Respawn protection active | リスポーン保護中 | 不能暗示精确秒数，除非运行时可确认 |
| 自愿退出 | 你已退出本回合，正在返回大厅 | You left this round. Returning to the lobby. | ラウンドを退出しました。ロビーに戻ります。 | 主动退出动作确认后显示 |
| 观战 | 观战中：{player} | Watching: {player} | 観戦中：{player} | 不显示为参赛者或计分者 |
| 下局排队 | 已加入下回合候选 | Queued for the next round | 次のラウンドに参加希望を出しました | 如果还没被接纳，应明确“候选/等待” |

## 错误、输入状态与可恢复提示

| 条件 | 中文 | English draft | 日本語 draft | 安全/行为要求 |
|---|---|---|---|---|
| 道具槽为空 | 还没有道具 | No power-up held | アイテムを持っていません | 轻提示；不要伪装成输入失败 |
| 道具已在使用/请求中 | 道具处理中，请稍候 | Power-up is processing. Please wait. | アイテムを処理中です。お待ちください。 | 不重复触发、不重复消费 |
| 道具激活被拒 | 道具未能使用 | Power-up could not be used | アイテムを使用できませんでした | 可提供确知原因，否则不臆测 |
| 输入暂不可用 | 当前输入暂不可用，请检查追踪或等待恢复 | Input is temporarily unavailable. Check tracking or wait for it to recover. | 入力を一時的に利用できません。トラッキングを確認するか、復旧をお待ちください。 | 追踪失效/菜单/KO时清除残留驱动 |
| Jump/道具输入 | 比赛中按你的“跳跃 / Jump”键使用道具 | During a round, use your configured Jump button to use a power-up. | ラウンド中は、設定した「ジャンプ」操作でアイテムを使用します。 | 仅为建议映射。须T04验证Station不退座、事件可用；不要保证所有设备均支持 |
| 交分点不可用 | 现在无法交存 | Banking is unavailable right now | 現在は預け入れできません | 提示以当前实际状态为准 |
| 名额已满 | 本回合已满员，可观战或等待下回合 | This round is full. Spectate or queue for the next round. | このラウンドは満員です。観戦するか次のラウンドをお待ちください。 | 不宣称排队已成功，除非收到确认 |
| 名单已锁定 | 本回合名单已锁定 | The player list is locked for this round | このラウンドの参加者は確定しました | 提供观战/下局选择，不偷偷加入 |
| 连接恢复 | 连接恢复中，请勿重复提交 | Reconnecting. Please don't submit again. | 接続を復旧中です。再送信しないでください。 | 只有在系统确有幂等/状态恢复提示时使用；不鼓励重复交存或交易 |
| 本局取消 | 本回合已取消，正在返回大厅 | This round was cancelled. Returning to the lobby. | このラウンドは中止されました。ロビーに戻ります。 | 失败恢复时不虚构名次/积分 |
| 退出确认 | 确认退出本回合？你的未交存成长可能无法保留。 | Leave this round? Your unbanked growth may not be retained. | このラウンドを退出しますか？未預け入れの成長量は保持されない場合があります。 | 只有实际退出规则确认后保留风险提醒；取消/确认按钮要清晰 |

## 母语与实现审校待办

- 英语自然度、日语敬体/术语（尤其預け入れ、成長量、KO/退出）由母语审校；中英日术语统一后再建立运行时文案资源。
- 所有占位符格式、数字分组、字符宽度、屏幕阅读和截断在实际HUD里检查；此处未定产品名。
- 只有经过平台实验的输入能力才能出现在确定语气中；Jump映射和Station组合目前是建议，不是保证。
- 本文没有冻结状态名、事件、字段、按键绑定、计分算法或Udon接口；待T06及相关平台/多人门禁。
