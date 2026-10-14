# T56 RemoteProxy 测试对象规格（未搭建）

## 独立对象与Prefab层级

建议名 `T56_RemoteProxy_SyntheticLab`。离线/本地测试对象只消费脚本内预置的合成摘要；不连接T51、网络、比赛账本或真实远端玩家。

```text
T56_RemoteProxy_SyntheticLab (root; no network sync / gameplay collider)
├── TestPanel (synthetic context/identity/revision/digest、预算与结果)
├── SyntheticEventFeed (fixture按钮或未来测试驱动，不是T51接线)
├── RemoteProxyRoot (装饰输出父节点；未来子项均禁用玩法Collider)
│   ├── ProxySlot_0 (纯MeshRenderer)
│   ├── ProxySlot_1 (纯MeshRenderer)
│   └── ... (固定池，仅由renderer budget允许显隐)
├── BuriedSummaryCounter (文本数值，不另增Renderer)
├── BallCoreReference (只显示位置与几何，不读账本)
└── ResetSnapshotArea (清理/重建入口)
```

## 操作与预期

1. 将同一合成event投递两次：只显示一个代理、`trackedCount`不增加；同ID同revision不同digest：报冲突且旧输出保留。
2. 先后顺序交换多个合成确认摘要并重建：最终稳定键集合、可见列表、Renderer数相等。
3. generation 3后发generation 2：报告旧世代，不能复活；更高revision更新位姿；remove生成墓碑，同键不能重现；新attachSequence作为fixture输入时才成为独立对象。
4. 输入可见、遮挡/埋藏与高低priority候选，预算设0/1/2：每次Renderer数严格不超过预算，优先级相同时按稳定身份打破平局。埋藏计数增长不改变尺寸/账本哑对象。
5. 清理context后重放旧event应被拒绝；用新context id可接受新一轮记录。用完整合成快照重建时若包含非法输入/冲突，旧cache不部分更新。

## 读数、失败、重置

面板展示合成输入ID/摘要、apply disposition、active tracked count、renderer count、selected stable keys、buried merge count、generation/revision和预算。任何重复可见代理、旧世代复活、超预算、同revision冲突覆盖、清理后复活、非法输入部分提交或账本/Collider字段被改写都是失败。重置先关闭旧context并清空展示，再以新context fixture重建；快照顺序需至少正序/逆序各跑一次。

## 编译边界

`RuntimeDraft/RemoteProxyRuntimeDraft.cs`是固定长度数组/摘要结构候选，不是UdonSharp运行时或真实Renderer池；C#草稿没有原子快照重建接口，需在目标支持矩阵确认后用预分配双缓冲补齐或明确保留Python宿主边界。**没有Unity/Udon编译、网络同步或画面验证**。该对象未来由Windows单一操作者创建；所有代理子物禁用玩法Collider，不加账本/分数写者。