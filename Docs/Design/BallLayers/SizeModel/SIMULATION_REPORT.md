# B套 T52 尺寸模型：离线模拟与验证报告

日期：2026-10-14。仅Linux离线源码准备；未SSH、未操作Unity/Blender/账号、未写Git或Unity工程。Unity由用户在Windows Editor拥有；项目 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，MCP未配置。

## 原版公开依据与项目对照

- 原版游戏源码未取得；本地来源材料将其明确标为未知。历史专利US7402104B2不等同真实游戏源码，也不证明新版沿用。本轮没有联网补查。
- 项目自定义依据是 `Docs/02_TECHNICAL_CONTRACTS.md` §§1、3.2、3.4：确认体积映射为等效球半径，核心/碰撞/语义尺寸分开，外部确认账本不由展示封顶改写。这些是本项目设计，不是原作算法或数值。

## 模型与数值安全复核

- 保留球体等效体积立方关系、显式 `CoreRadiusWorldM` / `CollisionRadiusWorldM` / `SemanticDiameter` 类型、显式基线/单位换算/cap参数与按新账本重建。
- 使用log域对数求和/半径推导，避免有限输入下直接计算 `volume*scale`、半径立方容量或 `baseline+confirmed` 造成中间溢出。最终碰撞半径、语义直径及半径转直径的乘法逐项检查；超出可表示范围明确拒绝。cap小于baseline明确拒绝。
- `effective_volume_units`仅是有限展示诊断值，不代表权威余额；尺寸映射不持有或修改账本。上限饱和只限制呈现尺寸。
- 附着位姿审查与尺寸相互独立：T51验证旋转/平移刚体局部变换、四元数及向量中间量有限性和独立物件scale，未将物件scale乘入球芯成长。

## 本轮实际测试与跨层模拟

- `python3 -m unittest discover -s Docs/tests/ball_size_model -v`：**7项通过**。包括独立几何oracle（`V=4πr³/3`）、8倍体积/2倍半径、半径与直径类型隔离、碰撞/语义类型分离、cap与外部账本哑对象、reset使用新余额、有限极值中间量及参数/输入拒绝。
- `python3 -m unittest discover -s Docs/tests/ball_attachment -v`：**9项通过**，其中3项跨层合成用例。
- 另运行 `python3 -m unittest Docs.tests.ball_attachment.test_growth_attachment_simulation -v`：**3项通过**。第一条确认→成长→附着→球平移→按实例身份撤销→剩余附着身份与权威余额相等→交分重建；实例gain明确映射为 mug=3、toy=5，移除mug后toy与剩余量5相符。另两条覆盖撤销/快照原子回滚，以及展示封顶不截账本。
- 本轮未运行全库测试；先前全库 `200项/197通过/3项ball_sweep失败` 是此前记录，不是本轮复测。

## Hash边界与待决语义

本轮同步修正T51编码边界：§3.4远端代理键的窄函数只哈希调用方显式传入的 `ball_seed + instance_id + generation + attach_sequence`，而交易事件幂等/撤销身份将context与event ID等字段独立长度前缀编码。字段边界测试覆盖朴素拼接同串但分段不同的输入。`ballSeed`的来源/值语义仍OPEN，必须由Lead/上游决定；当前草稿函数要求规范非空文本，若上游决定用非文本seed，须先批准canonical编码。本地实现没有修改 `Docs/02_TECHNICAL_CONTRACTS.md`。SHA-256不是认证，也不保证绝对无碰撞。

## 源码、测试与未验证项

- 本轮复核源码：[`size_model.py`](size_model.py)、[`attachment.py`](../Attachment/attachment.py)；接口说明：[`INTERFACES.md`](SuiteFoundation/INTERFACES.md)。测试：[`test_size_model.py`](../../../tests/ball_size_model/test_size_model.py)、[`test_attachment.py`](../../../tests/ball_attachment/test_attachment.py)、[`test_growth_attachment_simulation.py`](../../../tests/ball_attachment/test_growth_attachment_simulation.py)。
- `RuntimeDraft/SizeModelDraft.cs` 与 `../Attachment/RuntimeDraft/AttachmentRecordsDraft.cs` 均为普通C#草稿，**UNVERIFIED**；没有Unity/Udon编译、Prefab/ProgramAsset或场景行为证据。VR、网络、性能也未测（**NOT_RUN**）。
- T52状态由Lead管理；当前仍为 **REVIEW**，待Lead复审整改，不标记DONE，不声称Unity/Udon通过。
