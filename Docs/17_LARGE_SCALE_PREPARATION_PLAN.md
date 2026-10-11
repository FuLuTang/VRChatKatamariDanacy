# 大规模离线前置：九个执行角色与分阶段推送

日期：2026-10-11。用户要求“更大规模，别忘push”，延续“不运行Unity”和“细活队员做，Lead只把关/项目管理/响应”。

## 规模不是虚报成员数

DSH常驻队员上限8；其中两个旧Sol握手槽不能用现有工具删除，不再派工作。实际采用**6个已核验Luna常驻队员 + 3个明确指定Luna的独立执行worker**，不是9个常驻成员，也不修改DSH上限/凭据/profile。独立任务通过受管workflow编排（job `workflow-1`）；各worker只有不重叠的Docs目录。编排明确指定`openai-codex/gpt-6-luna`，实际请求投影另核验，未核验完成不能写成全员实证。

6个常驻队员本轮lastUsed/next已重新核验为上述provider/model；旧握手队员不因列表标签改成Luna。Lead根任务板对独立worker的owner只表示协调，不表示Lead实施内容。吞吐受运行时并发调度约束，角色总数不等于承诺九个请求每一秒同时运行。

## 分工与单写者

| 执行人 | 包/任务 | 独占交付目录 | 验收重点 |
|---|---|---|---|
| requirements-audit | P09/P17，task-16 | RuleDecisions、ReleaseRFC | 项目参数与官方事实/unknown分开；一图/两图RFC仅提案，不暗改DAG |
| asset-acquisition | P10/P11，task-17 | AssetCoverage、AssetAuthoring | 36概念真实覆盖、12图形状修订；definition/实例/世代、支撑/代理边界；不扩大下载 |
| reference-rules | P12，task-18 | ProtocolExamples、prep_protocol工具/测试 | 可运行宿主事务反例；幂等、epoch、恢复不足取消；不是网络/Udon验收 |
| asset-scout | P13，task-19 | MathCases、prep_math工具/测试 | 球体体积/支持、贡献移除、坐标向量及有限值；不是物理/源单位认证 |
| marketing-draft | P14，task-20 | BrandResearch | 原创候选查重、概念/技术图/实机分层；不定名/法律清权/发布 |
| 独立Luna worker P15 | P15，task-21 | CosmeticStates | 默认免费、失败/撤销/恢复；官方权益权威，收费关闭 |
| 独立Luna worker P16 | P16，task-22 | Baseline | 只读工程元数据与官方恢复流程；不安装/编译/写工程 |
| 独立Luna worker P18 | P18，task-23 | RuntimeEvidence | 2–4客户端/20回合/故障/分位数模板，真实观察全部NOT_RUN |
| offline-quality | P22，task-24 | ReleaseAudit、third_batch工具/测试 | 先公开Git边界审查，再只读互审和机器检查；原作者修，不跨写 |
| Lead | P23，task-25 | 根公共排程、报告、导航、Git | 读审、把关、阶段验收、commit/push结果确认；不实施队员内容 |

完整路径、状态、验收与证据见[机器工作包](<config/offline_preparation.json>)及共享任务板。宿主工具只标准库，不修改Unity目录；没有冻结T06接口。

## 阶段推送

1. **前两批**：冻结已验收P01–P08/P19–P21的代码/工件。原作者仅获task-26窄范围权限匿名化预览说明，不改PNG、GLB、selection、报告或重跑Windows；Lead匿名化自己公共总结。QA先审候选清单及index，而不是等待第三批完工。
2. **白名单暂存**：只加已验收的具体前批目录/文件及公共排程，不用`git add .`。当前第三批并行文件不进入首批commit。
3. **索引快照验证**：对暂存树独立运行宿主测试，避免并行未完成源码影响首批验收；检查实际staged列表/字节/二进制类型/凭据模式、ignore和DAG。
4. **首push**：既有origin的main，非force；确认远端提交OID，无世界上传/宣传发布。
5. **第三批**：作者交付→只读互审→作者修订→Lead验收，再完整工具/测试、公开边界审查及commit/push；保持未测平台门禁。

隐私检查区分“ignored untracked”与“cached tracked”。准确`git ls-files --cached --ignored --exclude-standard`/敏感路径index查询初次均空，不能把`--others`输出误报索引污染。确需去掉公开说明中与验收无关的实际私网IP、用户名、主机名、进程ID等，但保留匿名运行事实、hash与版本。私有聊天、管理RPC、下载ZIP/GLB/blend、cache、许可SDK源码始终不进入提交；截图只有纯素材检查图，没有用户GUI/Avatar/账户截图。

## 不扩大授权

Unity在用户Windows工程，Editor用户所有、本轮不开；MCP未确认。只有asset-acquisition持有Windows Blender预览操作资格，本批没有安排新渲染/下载。禁止账户、付费、改变收费店、SDK分发、世界/宣传发布。Git push授权不等于其他公开操作授权。单图降级RFC仍待用户决定，不能悄悄修改发布依赖。
