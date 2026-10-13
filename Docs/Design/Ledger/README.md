# T48 争抢账本、生命周期事务与回合状态机（DRAFT）

**离线参考模型草案；不冻结公共接口。** 本文和 Python 模块仅供设计讨论与主机侧逻辑实验。UNVERIFIED：没有声称 VR、舒适性、Udon runtime、Unity 或网络实测通过。

## 【已定】依据

- 共享坐标、已确认值与本地预测分离，账本只接受确定性权威提交；身份、物件实例世代、请求去重和单调提交序列见 [技术契约](../../02_TECHNICAL_CONTRACTS.md#L71-L112)。
- 银行是一次事务，重复 bankRequestId 不得双计；仅已确认账本能影响成绩及 PVP。[技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L130)
- PVP KO 后已交存分保留、未交存资源损失，攻击方收益受限；退出/Respawn 必须清理座位和比赛状态。[玩法设计](../../01_GAME_DESIGN.md#L22-L31) [玩法设计](../../01_GAME_DESIGN.md#L59-L64)
- 迟到者在准备/进行阶段进入下局队列，比赛终点基于同步网络时间而非逐秒广播。[玩法设计](../../01_GAME_DESIGN.md#L88-L100)

## 【DRAFT 草案】参考语义

模块 `ledger.py` 可独立运行。`EventKey(item_instance_id, round_id, generation)` 是得分事件幂等键；claim 请求批量暂存，不改变确认体积。裁决按最早帧先到先得；同帧按稳定 `player_id` 字典序破同分（占位策略，非既定公平结论）。提交具有严格递增序号；重复相同提交幂等，冲突内容拒绝，缺序号/重排拒绝。失败者预测条目删除，确认值不应用预测值，而取权威提交值。银行只能读写确认账本。

KO 在一个模型事务中清空受害者未交存确认量、预测、抢占请求并进入 RespawnPending；已银行分保留。攻击者份额由显式占位参数提供，默认0。到期重生恢复Active并清理预测与计时；离开则置Disconnected并清理临时请求。重生保护时长等具体玩法需外部策略层提供，当前仅保留占位参数。

`Snapshot` 是单个 revision 的原子权威视图：round、phase、终点、commit sequence、完整玩家确认账本/生命状态及提交事件。迟到加入安装完整快照，不合并本地预测。断线者由 disconnect 迁移，不因暂时离线直接删除已银行分；重连身份代数/保留期限仍需决定。

状态机草案：Lobby→Ready→Preparing→Playing→Settling→Results→Lobby；Ready、Preparing 可取消回 Lobby。非法迁移抛出 `InvalidTransition`。超时由权威时钟/单一裁决者检查，不依赖任何玩家本地状态。Playing 结束必须进入 Settling，再到 Results。超时推进可重复调用，推进后不再继续触发同一旧截止时间。该简化模型不处理权威迁移/选举。

## 【未决】不得推断定案

- 公平阈值与回溯/延迟窗口：**UNDECIDED**；当前同帧稳定ID裁决是可替换草案。见 [来源与未决问题 Q5](../../08_SOURCES_AND_OPEN_QUESTIONS.md#L153-L161)。
- 回合时长：`ROUND_DURATION_PLACEHOLDER`；180秒只是在玩法文档的原型提议，不是冻结值。[玩法设计](../../01_GAME_DESIGN.md#L12-L19)
- 结算细则：`SETTLEMENT_POLICY_PLACEHOLDER`（未交存资源处置、并列、奖励等均未在本模型裁决）。
- 首发地图数量：`LAUNCH_MAP_COUNT_PLACEHOLDER`；设计建议中的两图尚未定案。[范围决策](../../00_SCOPE_DECISIONS.md#L15-L20)
- KO 重生等待：`RESPAWN_DELAY_PLACEHOLDER`；保护时长 `RESPAWN_PROTECTION_PLACEHOLDER`；攻方份额 `ATTACKER_SHARE_PLACEHOLDER`。
- 世界尺度、网络 tick/帧分组、请求窗口、名单锁定、断线重连与 authority epoch 恢复策略均 **UNDECIDED**，见 [来源与未决问题](../../08_SOURCES_AND_OPEN_QUESTIONS.md)。

## 运行测试

从仓库根执行：`python3 -m unittest discover -s Docs/tests`。本目录测试只验证这个纯 Python 草案模型；不代表 Udon、Unity、VR 舒适性或真实网络行为验证。
