# 大规模离线前置：第三批验收与推送记录

日期：2026-10-11 至 2026-10-12（Asia/Shanghai）。状态：**P09–P18、P22、P23全部交付并推送**。所有内容都是离线准备；不代表Unity、Udon、VRChat联网、真人VR、性能、收费或发布门禁通过。

## 执行方式

- 6个既有Luna常驻队员加3个明确指定Luna的独立workflow worker，共9个执行角色；常驻上限8，两个旧Sol握手槽未使用。每个实际请求的`lastUsed/next`都经会话投影核验为`openai-codex/gpt-6-luna`，详见[路由记录](<12_TEAM_MODEL_ROUTING.md>)。
- 中途ChatGPT团队套餐额度耗尽（HTTP 429），4名队员中断。按规则没有改用其他模型代做，等额度恢复后由原作者继续。3个独立worker结束后，其后续修订由已记录单写者接手：P15→marketing-draft，P16→requirements-audit，P18→asset-scout。
- Lead只做分工、审查、公共状态和Git交付。所有内容修订由原作者完成，offline-quality做只读交叉审查。

## 交付与审查结论

| 包 | 内容 | 交叉审查发现与修订 |
|---|---|---|
| P09 | [规则决策表](<Preparation/RuleDecisions/decisions.json>)：9项，官方事实/unknown/项目方向/候选分开 | README与JSON推荐项不一致；作者把推荐项置空，等待用户决定 |
| P17 | [一图/两图RFC](<Preparation/ReleaseRFC/comparison.md>)、[候选DAG](<Preparation/ReleaseRFC/proposed_dag.json>) | 仅提案，根任务表未改；T32硬依赖矛盾仍待用户决定 |
| P13 | [球体数学用例](<Preparation/MathCases/README.md>)：25向量、36测试 | 极大值溢出、归一化、矩阵有限值、fixture未执行；V25收窄为明确的建模假设 |
| P14 | [候选名称查重](<Preparation/BrandResearch/names.csv>)、[宣传证据规则](<Preparation/BrandResearch/promotion_rules.md>) | 5个候选，查无同名不等于可商用；未选定名称、未法律清权 |
| P12 | [事务协议宿主模型](<Preparation/ProtocolExamples/README.md>)：34测试 | 快照分片被静默忽略→只支持单分片并明确拒绝multipart；跨epoch同序号返回旧结果→恢复时清请求缓存，业务幂等仍保留 |
| P15 | [外观状态](<Preparation/CosmeticStates/README.md>)：14状态、18转换、18用例 | 交接说明转换数写错（19→18）；收费保持关闭 |
| P16 | [工程基线](<Preparation/Baseline/README.md>)、[元数据](<Preparation/Baseline/metadata.json>) | 原交付缺机器可读元数据；补齐7个来源文件hash，VCC/独立UdonSharp仍unknown，Windows安装状态未检查 |
| P18 | [多人运行证据模板](<Preparation/RuntimeEvidence/README.md>)：25用例、8指标 | 校验器拦不住预填观测值、接受NaN；收紧并加反例自检，预热政策说明文字保留 |
| P10 | [素材覆盖表](<Preparation/AssetCoverage/coverage.csv>)：36概念、17项后续需求 | 后续下载全部标为未授权；逐文件许可仍待审 |
| P11 | [加工规格](<Preparation/AssetAuthoring/README.md>)：5定义、4实例 | schema要求许可状态字段而示例缺失；补齐后标准库与Draft 2020-12校验均通过 |
| P22 | [公开推送审计](<Preparation/ReleaseAudit/first_two_batches.md>)、[第三批检查器](<tools/third_batch/validate.py>) | 曾把ignored未跟踪文件误报为已入索引，按精确Git查询更正；检查器曾期待不存在的数学用例ID，已对齐 |

## 推送记录

| 提交 | 内容 | 推送前检查 |
|---|---|---|
| `78c49e6` | 前两批：78个文件 | 预览说明去除私网IP/用户名/主机名/进程号；暂存快照57测试 |
| `414c177` | P09/P13/P14/P17 | 暂存快照93测试 |
| `c9f5871` | P12/P15/P16/P18 | 暂存快照127测试、P18模板校验 |
| 本批 | P10/P11/P22、P12说明更正、本报告 | 暂存快照完整测试与第三批检查器 |

每次只暂存列出的具体路径，检查暂存列表中没有私人聊天、本地管理RPC、原始ZIP/GLB/blend、`.pyc`、SDK源码或Unity缓存，推送后核对远端提交号一致。没有force push。

## 仍未完成、需要用户的事项

- T00：用户在Windows打开工程、看Console、跑Build & Test；之后才进T04/T05平台实验和T06接口冻结。
- 一图还是两图首发（P17 RFC），以及P09各项规则决策。
- 公开名称、逐文件素材许可、正式美术方向。
- 所有运行时、真人VR、多人、性能、收费测试仍为NOT_RUN。
