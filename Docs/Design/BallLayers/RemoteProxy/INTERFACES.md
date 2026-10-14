# T56 RemoteProxy 离线合成接口（DRAFT；未接T51）

状态：仅host参考模型与合成确认摘要回放，不是T56正式接线、网络层或运行时完成。

## 输入/输出窄边界

宿主测试输入为`SyntheticConfirmedSummary`：显式提供`context_id, ball_id, instance_id, generation, attach_sequence, event_id, revision, summary_digest, variant_id, variant_seed, position, rotation, visible, buried, removed, presentation_priority`。摘要/确认ID/世代/序列都由测试fixture作为**上游既有值**提供；模块不分配序号，不以本地索引、到达顺序、Renderer ID、随机数或自身哈希形成稳定身份。`summary_digest`仅用于相同身份/修订内容冲突诊断，不是认证。

- 稳定附着键：`(context_id, ball_id, instance_id, generation, attach_sequence)`；同键不同`event_id`冲突。旧世代、旧revision返回stale，完全相同输入幂等；同身份同revision内容不同拒绝；更高revision更新；remove为不可复活墓碑，同一对象如重新附着必须由上游给新序列/身份。
- `rebuild_context`把完整快照放入临时候选缓存，按稳定身份/世代/revision排序重建，再容量检查后原子交换；不同context不混拼。`clear_context`永久关闭该context，旧包不复活；新回合使用不同context id。
- `rendered_summaries`按摘要显著性降序、稳定身份升序选取不超过调用方传入renderer预算的可见/未埋藏摘要；埋藏件只计作合并提示，不增加Renderer。储存/Renderer容量耗尽显式报错，不静默丢失确认身份。
- 本模块没有ledger、分数、Collider、拾取、物件权威状态或玩法碰撞接口。预算只改变选中的装饰输出。

## 正式接线门槛

根`tasks.json`读取时T51为RUNNING，T56为TODO且依赖T51；当前T51源码中的本地`_next_sequence`是草稿实现，不可当跨端稳定身份。本测试数据均自行合成并标明`synthetic`。正式摘要字段、确认事件/撤销语义和修订规则必须由T51产出并经接口评审后再适配；在此之前不能称T56完成、不能消费真实T51事件，也不能声称网络行为已测。

RuntimeDraft仅为固定数组候选；它将稳定附着身份当opaque string，并把generation/revision/seed/priority限制在非负signed 32-bit值。Python host fixture容许更宽整数（包括64-bit序列/seed）；在T51字段冻结前不得把C#窄字段宣称与最终接口一致。C#草稿也未实现原子快照重建，需要目标Udon支持确认后以预分配双缓冲补齐或把该操作保留在host/上游恢复层。
