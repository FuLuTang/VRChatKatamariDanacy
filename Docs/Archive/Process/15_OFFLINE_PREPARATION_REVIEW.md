# 离线前置首批交付、查漏与主管审查

日期：2026-10-11（Asia/Shanghai）。Unity未启动；没有Windows SSH、Unity MCP、Blender、账户操作、付款、公开发布、Git提交或推送。本文是这批前置工作的验收记录，不是游戏功能验收。

## 1. 总体排程

保留原45项产品里程碑；新增[前置分工与细化目录](<14_OFFLINE_PREPARATION_PLAN.md>)及[18个机器可读工作包](<config/offline_preparation.json>)。P01–P04为本轮交付/维护，P05–P18为后续TODO候选，不谎称已执行。每包含负责人/关联T/写范围/依赖/验收。原T00/T01/T02/T03未变DONE，T04/T05/T06尚未通过。

三名新队员requirements-audit、asset-acquisition、offline-quality的spawn和实际请求投影lastUsed/next均为`openai-codex/gpt-6-luna`；旧队员未新增派活。列表Lead模型标签与当前界面模型不一致，本文不拿列表回退值断言主管实际模型，也不修改会话/Host配置。

## 2. 已交付与修订

| 工作包 | 最终产物 | 审查结论 |
|---|---|---|
| P01 原始聊天查漏 | [追溯表](<Planning/RequirementsAudit/traceability.md>)、[缺口表](<Planning/RequirementsAudit/gaps.md>)、[handoff](<Planning/RequirementsAudit/handoff.md>) | 作者完整分段读取2979行；区分用户/AI与后续取代关系。主管对照关键用户原文核查，修正免费模型广搜来源误归AI、Quest措辞及“离线是否要等T00”的歧义。新文档无私有会话URL/身份/长段原文。 |
| P02 直接素材获取试点 | [包清单与36概念映射](<AssetIntake/DirectDownload/manifest.json>)、[路线](<AssetIntake/DirectDownload/routes.md>)、[库存](<AssetIntake/DirectDownload/inventory.csv>)、[handoff](<AssetIntake/DirectDownload/handoff.md>) | 两个官方免费小包共15,143,791字节，包内CC0声明与网页claim分列；不是最终逐资产可用性/分发验收。修正文案明确元数据库存与另一次CRC载荷读取不相同。 |
| P03 离线质量准备 | [矩阵](<Preparation/Quality/offline_test_matrix.md>)、[库存CLI](<tools/offline_quality/archive_inventory.py>)、[handoff](<Preparation/Quality/handoff.md>) | 首版7个测试扩为12个；主管要求增加Windows ADS/设备名/大小写重复/文件目录冲突、归档体积预检、无效限值与畸形ZIP测试；不是解压器/硬隔离安全保证。 |
| P04 主管维护 | 本报告、[前置清单](<config/offline_preparation.json>)、[集成单测](<tests/test_offline_preparation.py>)、根任务备注与README导航 | 已增加5个证据/计划一致性单测；保留原任务门禁和范围，不提交推送，不改Unity。 |

作者修改原scope，交叉审查只读：requirements-audit审P02/P03；offline-quality审P01；asset-acquisition审P01。主管读取关键产物、最终工具并运行集成测试。工具仍有未覆盖边界，不把文本交叉审查当游戏测试。

## 3. 查漏后的重要结论

没有发现v0.3完全遗漏的一大块用户核心系统，但现有标题不等于可执行完整交付：

1. **发布DAG矛盾**：T39可降一图，T37/T38/T40仍硬依赖第二图T32。记录为RFC待决策，不擅自改为单图发布。
2. **输入风险**：胸前归中件、水平限位、Grip释放、跟踪失效、菜单切换、Jump/Station冲突、退出/Respawn恢复都需具体测试；舒适性还无真人证据。
3. **球体不是“只差实现”**：长杆贡献移除、核心遮盖、最大球脱困、高速扫掠、采样物理上限仍需实现与检验；原型值不等于测得原作值。
4. **物件颗粒度**：模型Mesh数不是拾取实体数；独立杯/盘与桌子、支撑移除、价值、掉落和ID必须联动设计。
5. **网络状态**：Owner迁移不等于事务恢复；预测与确认、世代、epoch、分块快照和交分/KO/截止排序缺一不可。
6. **表现细节**：多语言教学、低特效关键提示、音效分层、免费外观回退、观战/领奖台流程可先做文本与测试准备，但不能声称已实现。
7. **商用与宣传边界**：公开名称、母语校准、商家资格、官方权益和发布批准是不同门禁；不会为了前期资源下载而提前收费/上传。

## 4. 素材获取实证与限制

已正规直接HTTP取得[Kenney Food Kit](https://kenney.nl/assets/food-kit)与[Kenney Nature Kit](https://kenney.nl/assets/nature-kit)，不需要浏览器Computer Use、登录、捐款或付费。原始ZIP只在ignored intake目录；本轮不展开模型、不做渲染、不导入。

主管使用修订后的库存CLI亲自再次扫描两包：Food 1018成员、4,606,270字节；Nature 3626成员、10,537,521字节；SHA256与manifest一致。36概念都被列出，约20项有文件名级候选（包含不精确/部分命中），16项unknown；这不是20个模型已通过审美/尺寸/碰撞验收。

[Food包内许可](<AssetIntake/DirectDownload/kenney_food-kit_License.txt>)和[Nature包内许可](<AssetIntake/DirectDownload/kenney_nature-kit_License.txt>)明确CC0、商业用途可用与署名非强制。商标/第三方权利、具体世界分发边界及每资产内容适宜性仍待审，不替项目设置开源LICENSE。

作者额外执行了`zipfile.testzip()`CRC流式载荷检查，不写模型到磁盘；其限额基于中央目录声明，不是欺诈元数据下的独立实际解压硬上限。新工具只读目录和整ZIP哈希，不校验成员内容CRC、不解压，不证明可安全恢复；中央目录仍在条数限制之前被标准库解析，TOCTOU和解析器资源风险仍在。

库存路径与许可复制件不含账户凭据，属于作者公开免费包的元数据/许可证据，并非模型源码；目前仅留本地新文件，尚未授权推送。下一次公开同步前仍审查此范围；ZIP保持忽略。

## 5. 校验记录

- `python3 Docs/tools/validate_plan.py`：45任务、双DAG和来源哈希通过。
- `python3 -m unittest discover -s Docs/tests -v`：**35项通过**（18计划基线 + 12ZIP元数据策略 + 5前置/素材证据一致性）。作者较早handoff内30是主管集成前的快照。
- 真实两包元数据扫描、体积、成员数量、SHA256复核通过，`member_payloads_read=false`。
- `git check-ignore`：两个下载ZIP均命中忽略；无模型进入Unity工程。
- `git diff --check`通过（跟踪文件）；新增第三方License复制件保持原样，包括原有空白，不为通过格式检查篡改许可证据。
- 主管新增集成测试首次遇到作者manifest字段重构导致KeyError；读取最终schema后调整为`archivePackageLicenseEvidence.evidenceCopy`并全量复测通过。
- Unity/Udon、ClientSim、VR舒服度、真实多人、性能、交易全部**NOT_RUN**。

## 6. 后续前置顺序

优先P05术语/状态文案、P06地图公平/可达性模板、P07反馈事件与低特效规则；可以继续不启动Unity。随后P08输入实验清单、P09原作unknown项目决策材料、P10/P11实际文件覆盖和加工规格。主管并行整理P12/P13协议反例/数学向量、P16版本支持与恢复记录、P17图数RFC。P14名称研究、P15免费定制状态、P18后续测试环境模板各自按依赖准备。

这些是已排程TODO，不假装所有人已在做。真正平台实验/模块实现、正式视觉/宣传与收费仍按原任务依赖和人工门禁开启。本轮不会把“准备工作可做”误成“所有正式模块可并行开写”。
