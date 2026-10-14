# C套 RemoteProxy 合成模拟记录

日期：2026-10-14 UTC。所有输入都是本地生成的synthetic confirmation fixtures；不连接T51、网络或真实远端状态。读取`tasks.json`时T51=RUNNING、T56=TODO并依赖T51。

## 本模块实际测试

命令：`PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests/ball_remote_proxy -v`

结果：**14 tests，14通过，0失败**。覆盖上游稳定身份原样消费/不自行递增、相同摘要幂等、同ID同revision冲突、revision更新与旧revision忽略、新generation替换和旧代拒绝、撤销墓碑与新序列重新附着、match context清理、renderer预算/确定性排序/可见性与埋藏、预算为0、非法输入原子性、容量失败、快照乱序重建与失败原子性、不同context保持隔离。

Digest由单测fixture以标准库SHA-256产生并作为不透明字段传入；实现不自行生成身份序号/摘要，也不把摘要当鉴权。Renderer只选择合成视觉摘要；没有账本、计分或Collider接口。

## 全库与计划校验

- `PYTHONDONTWRITEBYTECODE=1 python3 Docs/tools/validate_plan.py`：PASS，57 tasks、两种release DAG无环、source hashes通过；Unity/Udon/VR/多人/性能/commerce均NOT_RUN。
- `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Docs/tests -v`：**206 tests，203通过，3失败**，三项失败均为C套外Sweep。未修改A/Sweep代码或失败测试。较早的重排记录为194项/191通过/3失败；当前仅报告本次实际发现数，不推断计数差异来源。

## 未测与草稿限制

原版游戏源码未取得；无公开资料证明新版使用此方案，历史专利不等于游戏源码。正式T51确认摘要接口尚未完成，故本模拟不构成正式接线、T56完成或网络验证。普通C#草稿`RuntimeDraft/RemoteProxyRuntimeDraft.cs`使用固定数组但没有原子快照重建（需要目标Udon能力确认后用预分配双缓冲补齐/裁定host侧保留）；没有实例化Unity Renderer、Prefab未创建。未做Unity/Udon编译、同步、四客户端、容量性能或视觉验证；独立对象规格见`TEST_OBJECT_SPEC.md`。C#草稿身份为opaque string，计数/seed为signed 32-bit，不能映射所有Python 64-bit值；闭合context tombstone容量最多64，满时拒绝而不遗忘。