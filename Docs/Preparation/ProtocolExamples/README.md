# P12 宿主事务协议反例与参考模型

状态：离线准备实验；不是Udon实现、网络协议、正式契约或Unity验收。当前没有冻结字段/API、赢家排序、公平语义、截止边界或真实身份认证能力。

## 交付物

- `transaction_cases.json`：可审查的拾取、重复交存、KO、部分磁铁、超时/截止、同帧争用、epoch与迁移案例。每个不确定规则保留候选或OPEN。
- `Docs/tools/prep_protocol/model.py`：只依赖Python标准库的纯宿主Fixture reducer。
- `Docs/tests/prep_protocol/test_model.py`：34个离线单测，含恶意/无效输入和并发请求排列案例。
- 本目录 `handoff.md`：范围、复现与交接信息。

## 运行

从仓库根目录：

```sh
python3 -m unittest discover -s Docs/tests/prep_protocol -v
python3 -m unittest discover -s Docs/tests -v
```

最后复跑：P12目标测试34项通过；全仓测试132项全部通过。P22检查器最初期待不存在的P13数学案例ID `V01-radius-diameter`；实际夹具ID为 `V03-radius-diameter-distinct`。offline-quality已修正自身检查器的预期ID（未改P13夹具），随后全仓通过。上述Python结果均不代表Udon编译、VRChat网络、抗作弊、Unity或真人玩法PASS。JSON文件是人工编写的案例/预期，不是可自动执行的场景脚本。

## 参考模型当前约定（仅测试假设）

- 身份由Host harness提供 `observed_sender_id`、epoch、`received_tick`；模型仅验证这些Fixture值相互匹配。真实调用方认证并未实现，不能把客户端自报ID当可信，也不能宣称抗作弊。
- 请求ID为 `(round_id, actor_id, session_generation, sequence)`，并只在当前authority epoch的响应缓存中生效；迁移成功时清除旧epoch响应缓存，防止相同sequence在新epoch携带不同operation/payload时误返旧结果。持久bank/KO业务ID与spawn代仍从快照恢复，以各自防止重复事务。所有ID有类型/长度限制，数值使用有限的非负整数范围，布尔值不伪装整数；payload只允许JSON值且浮点数须finite。spawnId与spawnGeneration是分开的键事实；旧代号拒绝。
- `enqueue()` 只给 `pending`，不改账本，不表示成功；提交后才对确认拾取/交存/KO改状态。磁铁逐候选结算允许部分成功；同一spawn至多奖励一次。
- `bank_id` 与 `ko_id` 是另外的业务幂等键，不等同 request sequence；安全积分与风险成长分开，KO不删除已交存分，受害方未交存收益清零，攻击者收益不得超过被清除的未交存值。具体份额仍应由游戏规则决定，本模型仅以输入reward示例。
- 每次状态变更增加 `commit_sequence`。当前fixture只支持单分片快照（`total=1,index=0`）；所有玩家/物件及bank/KO业务幂等账本必须在唯一header payload中。任何multipart快照明确拒绝并取消本局，不会只读header而忽略后续分片。单分片快照还需匹配snapshotId/revision/epoch、回合且commit_sequence不倒退；迁移后epoch递增。无效/不完整快照取消Fixture回合，不猜比分/奖项。
- `cutoff_tick`采用严格 `received_tick < cutoff_tick`，只是当前可替换的测试选择。`ordering`可选 `arrival`、`bank_before_ko`、`ko_before_bank`，用于展示同tick会产生不同结果。排序中的actor/sequence稳定tie-break只使离线实验可重现，不是公平性主张，也未获批准。

## 明确未完成

没有接入网络或VRChat SDK；没有真实Authority Owner/Owner迁移；不模拟延迟、时钟漂移、可信发送者、传输丢包/重排或诚实服务器；没有恢复事件之外的完整游戏MatchState、玩家物理、磁铁可见性/距离、复活时间/保护规则。截止是否包含相等tick、bank/KO同一tick先后、公平窗口、业务ID保存期限等需独立决策，不能由本模型暗中定案。
