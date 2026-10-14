# T51 球层：附着身份与位姿记录（DRAFT）

状态：离线源码准备；接口未冻结。原来的刚体世界/局部转换、独立物件scale、字段定长身份编码方向保留；错误的本地递增序号与renderer预算从本层移除。

## 职责与数据

`attachment.py` 消费已确认事件 `ConfirmedAttachmentEvent`，其中包括 context/round、ball、item instance、generation、上游权威 `attach_sequence`、确认 `event_id` 与ball/object世界位姿及独立物件scale。序号由本层绝不生成。相同身份相同payload幂等；同身份不同payload拒绝；精确撤销留下墓碑阻止旧事件复活；`clear_context`隔离对局；`rebuild_snapshot`在临时状态完整验证后原子替换单一作用域及墓碑。

模块仅保存完整身份与球刚体根局部位姿。物件自身scale不随球芯成长缩放。它不做资格、比赛裁决、记账、renderer排序/预算/可见性/回收或T56装饰重建。位姿运算先校验所有输入、四元数和中间向量，失败不写记录/墓碑。

事务事件身份（context/ball/instance/generation/attachSequence/eventId）与远端代理键分开编码；`event_identity_hash`仅用于交易事件身份，`remote_proxy_key_hash(ball_seed, instance_id, generation, attach_sequence)`严格只接收技术契约§3.4的四字段。文本字段为UTF-8并带u32长度前缀，世代/序号分别为固定u32/u64；`ball_seed`必须由调用方显式传入；当前草稿签名要求规范的非空文本。其来源和值语义仍OPEN，若上游决定采用数值等非文本类型，须先批准canonical编码；不得从context、ball或event身份推导。两个SHA-256摘要均只是确定性键，不是认证或绝对无碰撞证明。快照方法消费一份完整已组装快照，不提供网络分块协议。刚体变换不处理非均匀父缩放或剪切；调用适配器需先规范化或拒绝。

## C#草稿与Unity准备

`RuntimeDraft/AttachmentRecordsDraft.cs` 是固定数组/Vector3/Quaternion的算法草稿，不是UdonSharp组件，未编译。`TEST_OBJECT_SPEC.md`定义独立测试对象与几何、操作、读数、失败及重置。Renderer预算归T56，不在本层复刻旧兼容实现。

## 证据与测试

原版源码未取得；历史专利不等同游戏实际源码，也不证明新版沿用。来源/访问边界见 [`Research/SOURCE_EVIDENCE.md`](Research/SOURCE_EVIDENCE.md)。

运行：`python3 -m unittest discover -s Docs/tests/ball_attachment -v`。测试保留位姿往返、独立scale、稳定身份键等旧基线意图，并新增独立矩阵oracle、确认序号乱序/重放、冲突/撤销/局代清理/快照原子替换/非法输入，以及跨T52模拟。此层没有Unity、Udon、网络或VR运行证据。
