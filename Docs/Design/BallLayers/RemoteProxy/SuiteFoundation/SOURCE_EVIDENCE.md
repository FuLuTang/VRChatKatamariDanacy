# T56 RemoteProxy 来源核查与证据边界

核查时间：2026-10-14 UTC。只做Linux离线源码/公开网页读取；未接触Unity、Windows主机、账号或游戏客户端。

## 原版源码与公开实现

- 本地检查可读的是项目自身 `Docs/02_TECHNICAL_CONTRACTS.md`、本套工作流文件与B套项目草稿；没有取得《塊魂》或《Once Upon A KATAMARI》的游戏源码。原版源码**未取得**。
- GitHub REST仓库元数据查询（2026-10-14T00:37:59Z）对 `Katamari Damacy original source code` 和 `Once Upon A KATAMARI source code` 均返回HTTP 200、`total_count=0`：[GitHub repo search](https://api.github.com/search/repositories)。查询不是穷尽式证明，不能据此断言所有公共平台都没有源码。
- US7402104B2为历史专利技术文本，不是游戏真实源码，也不证明新版继承；共同来源记录与实际专利页有限摘录见 [VisualRoll SuiteFoundation 来源证据](../../VisualRoll/SuiteFoundation/SOURCE_EVIDENCE.md)。该专利没有给出本项目远端代理去重/renderer池方案。
- 官方作品页、Q&A及制作人访谈已在上述共同来源记录中核对；没有说明T56网络摘要、世代/修订去重或Renderer预算算法。没有实机观察可补足内部实现。

## 本地依赖状态

- 本次读取`tasks.json`：T51仍为RUNNING，T56为TODO并依赖T51；故正式接线未完成。
- B套 `Docs/Design/BallLayers/Attachment/attachment.py` 是项目本地宿主草稿，字段含确认记录的候选身份，但其`_next_sequence`按本地到达自增，不用作跨端权威ID。T56只用测试fixture合成的确认摘要，不导入/调用T51。
- 本模块cache、摘要摘要值与事件均为synthetic/mock；digest只用于本地同ID冲突诊断，不是加密认证。此证据不支持真实网络一致性或原作实现结论。
