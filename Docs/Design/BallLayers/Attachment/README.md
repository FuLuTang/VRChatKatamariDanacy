# T51 球层：附着身份与位姿记录（DRAFT）

状态：离线源码准备；接口未冻结。原来的刚体世界/局部转换、独立物件scale、字段定长身份编码方向保留；错误的本地递增序号与renderer预算从本层移除。

## 职责与数据

`attachment.py` 消费已确认事件 `ConfirmedAttachmentEvent`，其中包括 context/round、ball、item instance、generation、上游权威 `attach_sequence`、确认 `event_id` 与ball/object世界位姿及独立物件scale。序号由本层绝不生成。相同身份相同payload幂等；同身份不同payload拒绝；精确撤销留下墓碑阻止旧事件复活；`clear_context`隔离对局；`rebuild_snapshot`在临时状态完整验证后原子替换单一作用域及墓碑。

模块仅保存完整身份与球刚体根局部位姿。物件自身scale不随球芯成长缩放。它不做资格、比赛裁决、记账、renderer排序/预算/可见性/回收或T56装饰重建。位姿运算先校验所有输入、四元数和中间向量，失败不写记录/墓碑。

事务事件身份（context/ball/instance/generation/attachSequence/eventId）与远端代理键分开编码；`event_identity_hash`仅用于交易事件身份，`remote_proxy_key_hash(ball_seed, instance_id, generation, attach_sequence)`严格只接收技术契约§3.4的四字段。文本字段为UTF-8并带u32长度前缀，世代/序号分别为固定u32/u64；`ball_seed`必须由调用方显式传入；当前草稿签名要求规范的非空文本。其来源和值语义仍OPEN，若上游决定采用数值等非文本类型，须先批准canonical编码；不得从context、ball或event身份推导。两个SHA-256摘要均只是确定性键，不是认证或绝对无碰撞证明。快照方法消费一份完整已组装快照，不提供网络分块协议。刚体变换不处理非均匀父缩放或剪切；调用适配器需先规范化或拒绝。

## C# 移植与测试对象

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/Attachment/AttachmentMath.cs` 是固定数组、字符串、`Vector3`、`Quaternion` 与基础数值上的纯静态数学移植，不是UdonSharp组件，尚未在Unity/Udon编译。`TEST_OBJECT_SPEC.md`定义独立测试对象与几何、操作、读数、失败及重置。Renderer预算归T56，不在本层复刻旧兼容实现。

## 证据与测试

原版源码未取得；历史专利不等同游戏实际源码，也不证明新版沿用。来源/访问边界见 [`Research/SOURCE_EVIDENCE.md`](Research/SOURCE_EVIDENCE.md)。

运行：`python3 -m unittest discover -s Docs/tests/ball_attachment -v`。测试保留位姿往返、独立scale、稳定身份键等旧基线意图，并新增独立矩阵oracle、确认序号乱序/重放、冲突/撤销/局代清理/快照原子替换/非法输入，以及跨T52模拟。此层没有Unity、Udon、网络或VR运行证据。

## Unity 实现

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/Attachment/AttachmentMath.cs`（`namespace VRCKatamari.BallLayers`，`public static class AttachmentMath`）是本模型的 UdonSharp 安全移植：纯静态、不分配、无静态字段、无 struct/属性/重载，账本与快照全部是调用方预分配的并行数组。

- 位姿：`TryWorldPoseToLocal` / `TryLocalPoseToWorld`，位置为世界米、+Y 向上；局部位姿相对球刚体根（worldPos = ballPos + ballRot·localPos，worldRot = ballRot·localRot）。四元数先做防溢出归一化（缩放/零/非有限拒绝）；物件 scale 只校验不施加，球芯长大不得放大已附着物件。
- 账本：十列等长并行数组（context/ball/instance/generation/attachSequence/eventId/localPosition/localRotation/objectScale/isRevoked），一列一个记录；`isRevoked` 行是墓碑。Python 的 records dict + revoked set 在这里是同一行集加标志位，行为不变：同身份同 payload 幂等、同身份异 payload 拒绝、墓碑阻止重放、`TryClearContext` 按局隔离、容量满拒绝而不驱逐。
- 身份：`TryApplyConfirmedEvent`、`TryRevokeEvent`（缺席身份也落墓碑）、`TryClearContext`、`TryBuildActiveOrder`（records 按身份排序，到达顺序不可见）、`TryRebuildContextSnapshot`（整个快照校验通过才替换该局的行；Python 的「同身份不能同时 active 与 revoked」即同身份两次出现而标志不同）。
- 两个哈希：`TryEventIdentityHash`（context/ball/instance/event 四个文本 + generation/attachSequence）与 `TryRemoteProxyKeyHash`（严格 ballSeed/实例ID/世代/attachSequence 四字段）。字段顺序与长度前缀分帧沿用模型，但 **摘要算法不是 SHA-256**：Udon 没有 hashlib、没有 UTF-8 编码器，这里用 32 位 FNV-1a，文本按 UTF-16 码元（低字节、高字节）加码元长度前缀。因此摘要值**与 Python 的 hexdigest 不同，也不可与离线结果互比**；两者都只是确定性键，不是认证、不是无碰撞证明。

`Tests/EditMode/BallLayers/AttachmentTests.cs` 覆盖 `test_attachment.py` 的 6 项与 `test_growth_attachment_simulation.py` 中属于本层的 2 项；增长模拟里的 `derive_size`/体积累加/交分属于 SizeModel 与确认账本，`test_presentation_cap_does_not_truncate_authority_value` 完全不触及本层，这三处断言未在此移植。尚未在 Unity 编译、未跑 `run_tests`；`ballSeed` 的来源与文本规范、Udon 下的哈希与字符串行为均未验证。

## 放置与观感（现行口径）

身份与位姿**记录**（上文）与吸附时**放在哪**是两件事。记录层：吸附位姿冻结在球局部系，不缩放、不重投影、不外推（原作确认）。放置层现行决定（Lead 2026-11-17，原作优先）：

- **触发即接触，原地冻结**：触发改为“道具盒碰到球的碰撞体”（`CANCER_COURTYARD_DIFFERENCES` D08），触发时实体已贴着球，`AttachProp` 继续冻结那一刻的世界位姿；保持朝向，不加嵌入偏移，不拉入，允许互相穿插。现状触发按外接球中心距，细长/扁平件会悬在球面外，随 D08 消失。
- **球芯显示直径固定为本关起始球径，不随吸收长大**（原作录像）；碰撞、接地、吸收门所用的等效体积球照旧随体积长大，吸附在它的接触面上，大球时物件在固定核心之外成外壳。
- **不做埋没隐藏**（核心固定后没有“被核心包住”的件）；原作按相对大小的消隐是开放问题。
- 吸附瞬间的星爆特效（约 0.1 s）是以后的美术需求，本轮不做。

公式/数据、离线估算（核心可见度、插地深度、针代理能否托住，D=0.30/0.50/0.80 m）、`BallView` 改动点、验收判据与开放问题全部在 [`ATTACH_PLACEMENT.md`](ATTACH_PLACEMENT.md)；原作证据、级别与录像时间码在 [`ORIGINAL_ATTACH_LOOK.md`](../../../Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md)。

> 观感对照（附着物保持吸附瞬间位姿、不随球芯长大缩放或外推，与原作两帧的实拍差异）：[`Docs/Evidence/BallPresentation/2026-11-15_attached_props_vs_original/`](../../../Evidence/BallPresentation/2026-11-15_attached_props_vs_original/README.md)。
