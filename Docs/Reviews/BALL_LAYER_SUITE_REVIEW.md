# 球层套件 Lead 评审记录 / 2026-10-14

状态：增量Lead评审。A/B/C全套离线交付已返回；Lead独立复跑全库 **247/247** 通过、计划校验通过。T49–T55离线草案/规格已DONE；T56正式远端接线仍TODO（生产ballSeed/快照接口未定）。T57三次均在Unity访问前安全停止；用户允许保留Windows既存ProjectSettings改动，并另授权一次Git-only fetch。fetch已完成，但Windows HEAD不以T57授权commit为祖先；没有checkout/merge/pull授权，任务仍BLOCKED。所有C# RuntimeDraft、Unity/Udon编译、Prefab与VR场景仍未验收；实际原版游戏源码未取得。workflow-5/6/7均已结束。

## B 套：成长与附着（T51/T52）

### 独立复测

- `python3 -m unittest discover -s Docs/tests/ball_attachment -v`：9 项通过。
- `python3 -m unittest discover -s Docs/tests/ball_size_model -v`：7 项通过。
- SizeModel 的 log-domain 溢出保护、presented cap 与外部账本分离、尺寸语义类型分离有测试依据；Attachment 的稳定确认键、冲突、撤销墓碑、失败快照原子性、world/local 位姿与独立对象scale有测试依据。
- 以上是宿主 Python 模拟；C# RuntimeDraft 尚未在项目Unity/Udon编译，Unity `Quaternion` 语义也未由场景探针验证。

### 已修复的跨层测试身份错配

[T51/T52跨层模拟](../tests/ball_attachment/test_growth_attachment_simulation.py#L22-L57)：

- helper定义 `mug` 的 value/gain 为 3，`toy` 为 5（第27–28行）。`AttachmentLedger.records` 按完整身份排序，`"mug" < "toy"`；测试第46行取 `records[0]`，所以实际撤销的是 mug，但第48行却从确认量减去 toy 的 5。随后 toy 仍作为唯一附着记录，确认量却是3；银行断言3把这个不一致延续下去。
- 此问题已由 B 整改修复：用明确 `instance_id → gain` 映射，明确撤销 mug（3），剩余记录为 toy（5），并断言权威剩余量、尺寸和银行分值一致。Lead复跑T51目录9项通过，跨层共3项通过。
- 这次修复消除样例叙事错配；仍需避免把跨层示例误当作交易账本本身，因为余额属于T48外部权威记录。T51/T52保持REVIEW，等待Lead接口审核及Udon边界核实。

### 远端Hash四字段已分离；seed语义仍OPEN

- B整改已分离两个键：[event_identity_hash](../Design/BallLayers/Attachment/attachment.py#L140-L156)用于context/ball/instance/generation/sequence/event身份；新增 [remote_proxy_key_hash](../Design/BallLayers/Attachment/attachment.py#L159-L174)严格只编码契约§3.4的四字段，并有固定整数宽度、UTF-8长度前缀及边界测试。Lead独立复跑T51的9项测试包含这些用例。
- **仍OPEN：**契约中的 `ballSeed` 的身份、类型、生成者和值域没有定义。函数目前只接受调用方传入的非空规范文本，不从`ball_id/context_id/event_id`推断。T51/T56只有在上游明确seed语义及接口后才能正式互接；SHA摘要不是认证或绝对无碰撞证明。

### C#草稿风险

- [AttachmentRecordsDraft.cs](../Design/BallLayers/Attachment/RuntimeDraft/AttachmentRecordsDraft.cs#L40-L45) 是普通辅助类而非 `UdonSharpBehaviour`，并在 `ReplaceSnapshot` 中运行期 `new AttachmentRecordsDraft()`（第139行）。这可作为算法移植草稿，但不是已证实可由项目UdonSharp编译/序列化的组件。它使用固定数组这一点符合通用文档提示，但精确目标版本与该类/委托/场景序列化链尚未核验。
- C#侧将序列号限定为非负 `long`，Python接受完整 `u64`；上游协议未冻结前需声明可表示范围差异，不能视为等价实现。
- [SizeModelDraft.cs](../Design/BallLayers/SizeModel/RuntimeDraft/SizeModelDraft.cs)同样必须核对 float/log范围、实现代码与Python结果的差异；实际Udon compile由获准Windows集成者执行。

### 交付判断

代码、测试与单独测试对象规格可保留进入Review；原版源码没有取得，公开专利仅是历史技术披露。跨层gain错配已修复、Hash字段已分离，但ballSeed语义尚未冻结，因此T51/T56不能接线完成。当前未写Unity工程、未搭Prefab、未运行Build & Test。

## C 套：表现与跟随（T53/T55/T56）

### 独立复测

- `python3 -m unittest discover -s Docs/tests/ball_visual_roll -v`：12项通过，含接触点无滑动方向oracle及两项跨层模拟。
- `python3 -m unittest discover -s Docs/tests/ball_follow_rig -v`：8项通过。
- `python3 -m unittest discover -s Docs/tests/ball_remote_proxy -v`：14项通过。
- C的VisualRoll、FollowRig、RemoteProxy Python实现与各自`TEST_OBJECT_SPEC.md`存在。以上只是本地宿主算法/合成摘要验证；原版源码没有取得，且不表示Udon或真实网络验证。

### 独立模块审查与接线边界

- VisualRoll明确将宿主参考代数标为右手，并用标量轴表达避免直接调用Unity `Vector3.Cross`；但Quaternion乘法/向量旋转的Unity适配还未由引擎箭头探针证明。虽Python和其独立无滑动测试一致，T53仍为REVIEW，禁止直接把C#草稿接入运行场景。
- FollowRig输入只有位置/标量水平yaw、dt、显式眼高范围，结构边界避免读取HMD/自转；需Unity坐标/yaw正方向验证，实际VR舒适度不在当前验收内。候选模块无自身数学缺陷足以阻止离线review，任务为REVIEW。
- RemoteProxy的Python只消费synthetic upstream摘要，identity来自输入；其C#RuntimeDraft不含原子快照重建（[测试规格说明](../Design/BallLayers/RemoteProxy/TEST_OBJECT_SPEC.md#L32-L34)）。该缺口已显式披露。T56依赖T51稳定确认身份，不能标READY/DONE，继续TODO，仅合成算法和隔离对象规格可作为草案保留。

### C套边界判断

C套来源记录区分了历史专利与2025作品、没有原版源码或物理/渲染算法证据。T53/T55可在离线review后推进各自独立Desktop实验台；真实Unity/Udon行为、Prefab/ProgramAsset序列化链和VR舒适性都仍NOT_RUN。T56实验台必须维持装饰无玩法Collider和无账本写入。

## A 套：接触与外壳（T49/T50/T54）

### 独立复测

- `python3 -m unittest discover -s Docs/tests/ball_ground_contact -v`：15项通过（含1项A套跨层桥接）。
- `python3 -m unittest discover -s Docs/tests/ball_radial_proxy -v`：10项通过。
- `python3 -m unittest discover -s Docs/tests/ball_sweep -v`：14项通过。
- Sweep已修复旧的零位移嵌入处理、真实扩张AABB最近面深度和`blocked`/`stuck`语义；多接触测试换为解析上确实可碰到两面的输入。盒角AABB只声明保守假阳性，不冒充精确球盒。

### 接口和技术边界

- GroundContact输出支持距离、中心法线坐标与有界纠正；Lead已修改T49任务，把旧“精确接触点”要求澄清为plane anchor（平面参考点），不代表代理接触点。代理/球芯变换矩阵、Hamilton四元数仅是Host数学候选。
- RadialProxy和GroundContact仍假定同一局部到世界矩阵约定。Unity方向探针、`Quaternion`乘法和Transform的实际父子链需在Windows对象上对拍；未过探针不得接正式场景。
- A套C#文件是普通RuntimeDraft，不是Unity/UdonSharp组件；RuntimeDraft、ProgramAsset、场景均未编译/运行。每层测试对象目前仅有SPEC。
- 公开核查未取得原版源码。公开专利/产品页/Ruby实验只支持有限公开背景，不支持内部球物理算法主张。

## 全库回归与当前总判断

- Lead在A/B/C所有写入workflow完成后独立运行 `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests -v`：**247 tests，247通过**，包含A 39项、B 16项、C 34项和既有其他离线测试。T55/RemoteProxy测试目录的`__init__.py`已补齐，递归发现覆盖已复核。
- `python3 Docs/tools/validate_plan.py`：PASS，58 tasks、两个release DAG无环、source hashes通过；计划测试18项通过。
- 当前任务状态：T49–T55已按离线原型范围验收DONE；T56正式接线为TODO，生产ballSeed/快照接口未定。没有任何Unity、UdonSharp、VR、网络、性能或游戏客户端测试通过声明。
- 8个单层测试对象规格均存在；**实际Prefab/Scene仍未创建**。T57三次都在Unity访问前安全停止：先后为Linux任务表dirty、Windows ProjectSettings.asset既存dirty，以及Windows仓库缺少授权commit对象。用户已批准不读取/触碰并保留该ProjectSettings改动；Lead一次Git-only fetch已使授权commit对象可用，但当前Windows HEAD仍非其祖先。未做checkout/merge/pull，需用户决定手动同步或另行授权有限的分支更新；Play/Build & Test/客户端/上传仍禁止。
