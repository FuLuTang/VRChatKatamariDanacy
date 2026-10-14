# B套 T51 附着记录：离线模拟与验证报告

日期：2026-10-14。仅Linux离线源码准备；未SSH、未操作Unity/Blender/账号、未写Git或Unity工程。Unity仍由用户在Windows Editor拥有；工程 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，MCP未配置。

## 原版公开依据与项目对照

- 原版游戏源码未取得；仓库没有经核实的原版源码/反编译实现。本轮仅复核本地来源记录，没有联网补查。
- 本地来源记录记载此前对US7402104B2的有限公开访问（HTTP 200、读取4096字节），并非完整文本审计或法律分析。它是历史专利，不是实际游戏源码，也不证明《Once Upon A KATAMARI》沿用其方法。
- 项目自定义依据是 `Docs/02_TECHNICAL_CONTRACTS.md` §3.4：确认世界位姿转球刚体局部位姿，附着物scale独立，远端键使用 `ballSeed + 实例ID + 世代 + attachSequence`。这不是原版实现证据；`ballSeed`的来源和值语义目前未定义。

## 本轮实现整改

- 保留 `world_to_local` / `local_to_world` 刚体变换、独立物件scale和完整校验后再写状态。失败的身份、位姿、四元数、scale或快照输入不部分更新记录/墓碑。
- 交易事件身份与远端代理键分离：`event_identity_hash` 对 context/ball/instance/event 文本字段分别作UTF-8 u32长度前缀编码，并将generation/attachSequence作固定u32/u64编码；用于交易事件身份，不作为远端代理键。
- 新增窄函数 `remote_proxy_key_hash(ball_seed, instance_id, generation, attach_sequence)`，只接受§3.4规定的四字段；文本字段用UTF-8长度前缀，整数用固定宽度大端编码后计算SHA-256。`ball_seed`必须显式由调用方传入；当前函数草稿要求规范非空文本。来源/值语义仍OPEN；若上游决定采用非文本seed，须先批准canonical编码。不得从context、ball ID或event ID推导。此编码不修改项目技术契约，须由Lead/上游批准后才能定为契约实现。
- 两种SHA-256仅为确定性摘要/键，不是认证机制，也不保证绝对无碰撞。跨字段边界测试覆盖了朴素拼接相同的文本组合。
- 跨层错配已修复：显式 `instance_id → gain` 映射；本例明确撤销 mug（gain 3），剩余附着身份为 toy，权威未交存量为5；独立几何期望值按基线+5计算，银行分值也与该剩余量一致，不再依赖排序后 `records[0]` 推断收益。

## 本轮实际测试

- `python3 -m unittest discover -s Docs/tests/ball_attachment -v`：**9项通过**，含6项T51附着测试和3项跨层测试。
- `python3 -m unittest discover -s Docs/tests/ball_size_model -v`：**7项通过**，含球体几何oracle、8倍体积/2倍半径、半径/直径类型隔离、展示封顶不改账本、按新账本重建、有限极值及非法输入。
- 另运行 `python3 -m unittest Docs.tests.ball_attachment.test_growth_attachment_simulation -v`：**3项通过**。成长→附着→球平移→按身份掉落→剩余量核对→交分重建；撤销/重放与快照失败回滚；封顶与账本分离均覆盖。
- 本轮未运行全库测试；先前的全库 `200项/197通过/3项ball_sweep失败` 是此前记录，不能视作本轮复测结果。

## 源码、测试与未验证项

- 本轮源码：[`attachment.py`](attachment.py)、[`README.md`](README.md)、[`Research/SOURCE_EVIDENCE.md`](Research/SOURCE_EVIDENCE.md)、[`../SizeModel/SuiteFoundation/INTERFACES.md`](../SizeModel/SuiteFoundation/INTERFACES.md)。测试：[`test_attachment.py`](../../../tests/ball_attachment/test_attachment.py)、[`test_growth_attachment_simulation.py`](../../../tests/ball_attachment/test_growth_attachment_simulation.py)、[`test_size_model.py`](../../../tests/ball_size_model/test_size_model.py)。
- `RuntimeDraft/AttachmentRecordsDraft.cs` 与 `../SizeModel/RuntimeDraft/SizeModelDraft.cs` 是普通C#草稿，**UNVERIFIED**；未在Unity/Udon编译。Unity Quaternion/场景方向、Prefab/ProgramAsset链、网络及VR行为均 **NOT_RUN**。
- T51/T52状态仍由Lead管理；当前仍为 **REVIEW**，待Lead复审本次整改。没有标记DONE或声称Unity/Udon通过。
