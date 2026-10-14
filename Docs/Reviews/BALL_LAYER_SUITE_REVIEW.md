# 球层套件 Lead 评审记录 / 2026-10-14

状态：增量评审，B 套离线 Python 测试已独立复跑；C#、Unity/Udon 与场景均未验收。workflow-5 其他两套仍在运行。

## B 套：成长与附着（T51/T52）

### 独立复测

- `python3 -m unittest discover -s Docs/tests/ball_attachment -v`：9 项通过。
- `python3 -m unittest discover -s Docs/tests/ball_size_model -v`：7 项通过。
- SizeModel 的 log-domain 溢出保护、presented cap 与外部账本分离、尺寸语义类型分离有测试依据；Attachment 的稳定确认键、冲突、撤销墓碑、失败快照原子性、world/local 位姿与独立对象scale有测试依据。
- 以上是宿主 Python 模拟；C# RuntimeDraft 尚未在项目Unity/Udon编译，Unity `Quaternion` 语义也未由场景探针验证。

### 阻止本套整体验收的具体测试错配

[T51/T52跨层模拟](../tests/ball_attachment/test_growth_attachment_simulation.py#L22-L57)：

- helper定义 `mug` 的 value/gain 为 3，`toy` 为 5（第27–28行）。`AttachmentLedger.records` 按完整身份排序，`"mug" < "toy"`；测试第46行取 `records[0]`，所以实际撤销的是 mug，但第48行却从确认量减去 toy 的 5。随后 toy 仍作为唯一附着记录，确认量却是3；银行断言3把这个不一致延续下去。
- 这是测试序列与业务身份映射不一致，不表示生产账本正确/错误。修复应明确按 `instance_id` 选择掉落项并使用它绑定的 gain，断言留下的物件及确认余额相符；或者明确撤销 mug 并减3、银行余额与剩余 toy 身份独立核对。不能改生产算法来迁就错误样例。
- 当前三个跨层用例通过，只证明给定断言成功，不证明该叙事对应的账本交易正确。本项修正并复跑前，B套状态最多 REVIEW，不得DONE。

### 远端Hash与事件幂等身份尚未对齐

- [技术契约§3.4](../02_TECHNICAL_CONTRACTS.md#L63-L69)规定远端Hash输入为 `ballSeed + 实例ID + 世代 + attachSequence`。当前 [attachment_hash](../Design/BallLayers/Attachment/attachment.py#L128-L148) 编码的是 `context_id + ball_id + instance_id + generation + attach_sequence + event_id`；`ConfirmedAttachmentEvent` 没有 `ballSeed` 字段。这可能仍是稳定摘要，但不是按当前契约输入生成的同一远端键，T56消费者可能不一致。
- 应明确分离：event_id/context用于幂等、作用域与撤销；远端代理键按契约四字段计算。若要改契约，需Lead做项目接口决策，而不是由单层worker默默扩字段。此项加跨模块Hash对拍测试后再审核。

### C#草稿风险

- [AttachmentRecordsDraft.cs](../Design/BallLayers/Attachment/RuntimeDraft/AttachmentRecordsDraft.cs#L40-L45) 是普通辅助类而非 `UdonSharpBehaviour`，并在 `ReplaceSnapshot` 中运行期 `new AttachmentRecordsDraft()`（第139行）。这可作为算法移植草稿，但不是已证实可由项目UdonSharp编译/序列化的组件。它使用固定数组这一点符合通用文档提示，但精确目标版本与该类/委托/场景序列化链尚未核验。
- C#侧将序列号限定为非负 `long`，Python接受完整 `u64`；上游协议未冻结前需声明可表示范围差异，不能视为等价实现。
- [SizeModelDraft.cs](../Design/BallLayers/SizeModel/RuntimeDraft/SizeModelDraft.cs)同样必须核对 float/log范围、实现代码与Python结果的差异；实际Udon compile由获准Windows集成者执行。

### 交付判断

代码/测试与独立测试对象规格已有实质进展，可复用；原版源码没有取得，公开专利仅为历史技术披露。先修正跨层测试值绑定，再由Lead独立复核完整测试和diff，之后才考虑创建Windows单层实验对象。当前未写Unity工程、未搭Prefab、未运行Build & Test。
