# T51 独立测试对象规格（待Windows操作者实施）

状态：源代码准备完成；没有写入Unity工程、没有编译/运行或Prefab验证。Unity为用户Windows端Editor，项目 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，MCP未配置；后续由单一Windows集成操作者在评审后建立对象。

## 对象：AttachmentPoseAndIdentityLab

- **目的**：单独验证T51确认身份消费、位姿换算、重复事件幂等、撤销墓碑、跨局隔离和原子快照替换。不启用碰撞/玩法账本/renderer预算。
- **预期几何**：测试根 `BallPhysicsRoot` 位于 `(2,-1,4)`，绕世界Y正向旋转60°；`ConfirmedItem` 用世界位置 `(6,2,5)`、单位姿态及独立scale `(0.4,0.7,1.2)`。局部坐标由逆Y旋转矩阵算得；移动/旋转ball root后，物件保持该局部姿态和原始scale，绝不继承核心球放大。
- **操作序列**：调用预置确认事件（固定context、ball、spawn、generation、eventId和权威attachSequence）；重复派发同事件；用相同身份、不同位姿测试冲突；按精确身份撤销后重放；更换generation和context；提交完整有效/无效快照；将球芯表现大小调大但不改AttachmentRoot缩放。
- **读数**：显示成功/错误枚举、记录条数、身份字段、局部位置/Quaternion/scale、重建世界姿态误差；两种消息到达顺序都应得到同一身份排序集合。
- **失败指示**：重复事件增加记录、冲突事件覆盖旧记录、撤销后重放复活、不同generation/context合并、无效快照部分替换、局部尺度随球芯改变，任一即FAIL。测试可视化不得将Renderer数量或回收当T51指标。
- **重置**：显式调用 `ClearContext(testContextId)`；另生成全新测试context作交叉验证。确认快照重建会替换该作用域而非累计。
- **Prefab层级**：`AttachmentPoseAndIdentityLab` → `BallPhysicsRoot` → `AttachmentRoot` → `ConfirmedItem`；`CoreMesh` 与 `TestReadout` 是 `BallPhysicsRoot` 的兄弟节点。CoreMesh单独缩放；AttachmentRoot保持单位局部scale。无Rigidbody、Collider、玩法层或动态MeshCollider。
- **编译边界**：`RuntimeDraft/AttachmentRecordsDraft.cs` 只用固定数组、字符串、`Vector3`、`Quaternion`及基础数值；不是UdonSharp脚本组件、未接SDK事件或序列化快照协议。真实Udon编译、Unity编译、变换API语义、网络多端及非均匀父缩放/shear仍须后续操作者验证。快照草稿只接收已组装完整快照，分块网络协议不在本层。
