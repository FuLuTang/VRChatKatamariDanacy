# T47 拾取、吸附与成长离线参考模型

> **DRAFT 草案 / UNVERIFIED**：标准库 Python 离线模型，用于讨论和测试性质；不是 Unity/Udon 实现，不冻结公共接口，不代表已验证的运行时协议或调校结果。

## 状态分栏

### 【已定】
- 共享地图坐标系用于碰撞、拾取和吞噬；成长账本与渲染网格分离。参见 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L6-L21) 与第3节。
- 同物实例以 `(roundId, spawnId, spawnGeneration)` 辨识；请求幂等、唯一裁决结果；客户端可预测但失败要回滚。远端装饰不能决定玩法碰撞。
- 远端仅依据确认的状态重建外观摘要；装饰模型、代理凹凸、径向样本都不得决定游戏碰撞或计分。

### 【DRAFT 草案】
- `SpatialRegistry` 用均匀网格做包围盒候选索引；`nearby(position, radius)` 返回近邻候选，不等于最终接触判断。
- `Item` 记录位置、包围尺寸、类型、价值、可吸附标记及世代。`eligible` 再应用接触、尺寸、类型白名单与成长上限占位参数。
- `AuthorityLedger` 示意单一权威对有效请求去重并首个提交者获胜；相同请求重放返回同一结果。同一物件只在首次成功claim后贡献一次。实际权威 epoch、玩家会话世代、排序/时间边界还须接入正式协议。
- `Ball` 分开保存本地预测与确认收益。裁决拒绝/状态到达时用 `reconcile` 清空预测并以权威集合重建；快照也可直接恢复。成长按确认价值单调累加并由上限截断。
- 凹凸外形和径向代理目前只作视觉表示、不参与判定，是**设计主张**；是否可让其影响物理，须由 T29 真人门禁确认，未确认前采用球形判定。
- 代码仅依赖 Python 标准库；接口仅为此离线样例，**不冻结**。

### 【未决】
- 吸附接触/尺寸阈值、价值表及成长曲线；本模块规则对象及构造参数均由调用方显式传入，无权威默认调校。
- 世界尺度与坐标单位的实测；`world_m` 命名是占位约定，不代表标定完成。
- 两人同时够到时的公平阈值、是否接受先到裁决偏差；当前样例仅演示权威唯一裁决，不宣称公平。
- 凹凸是否参与物理（T29），须真人门禁；回合时长、结算/截止细则也未由本任务决定。
- 对应问题索引：[`Docs/08_SOURCES_AND_OPEN_QUESTIONS.md`](../../08_SOURCES_AND_OPEN_QUESTIONS.md)，尤其 Q1、Q3、Q5、Q8。参考规则矩阵亦将相关原作细节标为 unknown：[`Docs/Research/ReferenceRules/rules_matrix.md`](../../Research/ReferenceRules/rules_matrix.md#L13-L25)。

## 使用与边界

从仓库根运行 `python3 -m unittest discover -s Docs/tests`。吸附判定采用球半径 + 显式接触余量 + AABB 包围球半径的宽松近似；这只是离线候选模型。它不含遮挡、速度合理性、物件状态机、回合结算、网络传输或反作弊。权威状态应只计确认后的吸附集合；客户端装饰可丢弃并从权威快照重建。**不声称 VR、舒适性、Udon runtime、Unity 或网络实测通过。**

来源依据：[`Docs/02_TECHNICAL_CONTRACTS.md`](../../02_TECHNICAL_CONTRACTS.md#L32-L110)、[`Docs/01_GAME_DESIGN.md`](../../01_GAME_DESIGN.md#L13-L20)、[`Docs/Research/ReferenceRules/handoff.md`](../../Research/ReferenceRules/handoff.md)。
