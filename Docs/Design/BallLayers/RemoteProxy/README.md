# 远端代理（RemoteProxy）

远端玩家球上已确认装饰的**纯外观缓存**：按上游确认的标识去重、按 generation/revision 排序、保留墓碑与已关闭上下文，并在渲染器预算内挑选要画哪些。它永远不向本地碰撞、吞噬、账本或分数提供任何输入。模型与测试见同目录 `remote_proxy.py`、`INTERFACES.md`、`TEST_OBJECT_SPEC.md`。

## Unity 实现

- 纯逻辑：`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/RemoteProxy/RemoteProxyMath.cs`，入口 `VRCKatamari.BallLayers.RemoteProxyMath` 的 `Apply / TrySelectRendered / ClearContext / RebuildContext / TryComputeDigest`，结果类型 `RemoteProxyDisposition`。静态方法 + 调用方自有数组，无 struct、无 Behaviour、无静态字段；除 `TryComputeDigest` 外不分配。
- 状态布局（Python 的 cache 在 U# 里不存在实例类，改为调用方持有的并行数组；`slotState.Length` 即固定容量，也就是 Python 的 `max_records`）：

  | 数组 | 类型 | 字段 |
  | --- | --- | --- |
  | `slotState` | `int[]` | 0 空 / 1 占用 |
  | `slotContextId` / `slotBallId` / `slotInstanceId` | `string[]` | `context_id` / `ball_id` / `instance_id` |
  | `slotGeneration` / `slotAttachSequence` | `int[]` | 世代 / 附着序列，均 ≥ 0 |
  | `slotEventId` | `string[]` | `event_id` |
  | `slotRevision` | `int[]` | `revision`，≥ 0 |
  | `slotDigestLow` / `slotDigestHigh` | `int[]` | 摘要是两个 int（自有实现，见下） |
  | `slotVariantId` / `slotVariantSeed` | `string[]` / `int[]` | 变体与种子 |
  | `slotPosition` / `slotRotation` | `Vector3[]` / `Quaternion[]` | 世界坐标米 / 单位四元数 |
  | `slotVisible` / `slotBuried` / `slotRemoved` | `bool[]` | 可见 / 埋藏 / 墓碑（槽位保留） |
  | `slotPriority` | `int[]` | `presentation_priority`，≥ 0 |
  | `closedContextId` | `string[]` | 闭合 context 表，容量由调用方定 |

  拥有数组的 Behaviour 与 Renderer 池留待后续，本层不建组件、不碰场景。
- disposition 与 Python 一一对应：`Accepted / Duplicate / Updated / StaleGeneration / StaleRevision / RemovedTombstone / ClearedContext`；Python 的三个异常改为返回值 `Invalid / Conflict / Capacity`，另加 `Cleared / AlreadyCleared / Rebuilt`。墓碑只能由新的上游 attach sequence 替换；`clear_context` 永久关闭该 context，只能换新 id 开新回合。快照重建先校验输入/冲突、再按重放峰值做容量检查，之后才动槽位，失败不留半套状态。
- 摘要改为自有实现：`TryComputeDigest` 用两条 32-bit FNV-1a lane 覆盖与 Python fixture 相同的字段序列，输出 low/high 两个 int。它**不是** Python fixture 的 sha256，字节不兼容，只保证确定性与“字段变则变”；对缓存而言仍是不透明上游值，不参与身份、不用于鉴权。
- 隔离：本层只决定装饰画什么、画在哪。API 没有任何碰撞、拾取、账本、分数或 PvP 输出；只有 disposition、调用方的视觉槽位表、渲染排序和池计数。远端数据永远不进入球的碰撞/计分路径。
- 预算：`TrySelectRendered` 按 `presentation_priority` 降序、稳定身份升序、event id 升序取 `min(可渲染数, rendererBudget, 缓冲区长度)` 个槽位索引，严格不超预算；埋藏件只计入 `mergedBuriedCount`，不占 Renderer。
- 与 Python 的差异：`generation / revision / attach_sequence / variant_seed` 收窄为非负 signed 32-bit（与移植本身声明的收窄一致，T51 字段未冻结）；Python 单独维护的 generation high-water 表改为由槽位现算（两者不变式等价，省掉一张只会在槽位已满时才触发的容量表）；`TryComputeDigest` 只用于 host/fixture 侧；身份排序用 `string.CompareOrdinal`（按 UTF-16 code unit，只有含 astral 字符的 id 才会与 Python 的 code point 顺序不同）。
- 测试：`Tests/EditMode/BallLayers/RemoteProxyTests.cs` 移植 `Docs/tests/ball_remote_proxy/test_remote_proxy.py` 全部 14 个用例，另加预算严格性、回收不改容量、摘要确定性/不透明与“无碰撞计分面”检查（反射）；程序集 `VRCKatamari.Tests.EditMode`。
- 状态：Unity 编译、EditMode 测试与 U# 全量编译均 `NOT_RUN`；上面只是移植结果，不是验证证据。Udon 侧字符串/`BitConverter` API 仅按 Udon class exposure tree 核对过（`CompareOrdinal`、`ToCharArray`、`SingleToInt32Bits`），未在 Editor 内编译验证。
