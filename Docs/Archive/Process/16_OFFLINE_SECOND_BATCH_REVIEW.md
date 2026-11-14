# 第二批离线准备与Windows素材预览审查

日期：2026-10-11（Asia/Shanghai）。状态：**本轮离线准备验收完成**。P05–P08文本、P20源码修复、P21队员Windows预览复测及P19主管集成均有交付证据。根Unity/游戏任务不因此完成。

## 1. 当前分工（最新用户指令）

用户明确细活应由队员做，Lead只负责项目管理、把关和响应。

| 人员 | 当前责任 | 写入/执行边界 |
|---|---|---|
| requirements-audit / gpt-6-luna | P05术语、教学、状态文本，P06只读交叉审查 | [Terminology](<Preparation/Terminology/handoff.md>)；无Windows操作 |
| offline-quality / gpt-6-luna | P06测量模板，P07交叉审查；P20预览脚本护栏/单测修复 | [地图模板](<Preparation/MapRubrics/README.md>)、[预览源码](<tools/asset_preview/stage_glb.py>)和对应测试；无SSH/Blender |
| asset-acquisition / gpt-6-luna | P07反馈目录，P05交叉审查；P21唯一Windows Blender操作人 | [反馈目录](<Preparation/Feedback/README.md>)、[Preview证据](<AssetIntake/Preview/README.md>)、ignored staging；只独立后台预览目录 |
| Lead | 排程、审查、验收、公共状态/文档维护、响应用户 | 根公共文档；不再实施源码、渲染、队员scope修改 |

第二批派发前再次通过实际请求投影核验三名队员lastUsed/next均为`openai-codex/gpt-6-luna`。共享任务板task-9/10/11承接文本包，task-13修源码，task-14严格依赖task-13，task-12等三文本包及两执行包后集成。不会把角色名或列表标签当模型证据。

## 2. 文本交付已审查

- **P05**：[37条三语术语](<Preparation/Terminology/glossary.csv>)、[状态文本](<Preparation/Terminology/states.md>)、[三模式教学](<Preparation/Terminology/onboarding.md>)。已区分未交存成长/安全分、预测/确认、KO/退出/平台Respawn；Jump/Station支持待平台测试。主管要求通用对外Rolling Ball而非Katamari，保留参考名称为internal_only；项目已选磁铁/火箭不误写成尚未选择；KO中英日统一等待重生，加速惯性不暗示已实现。母语审校与HUD布局未做。
- **P06**：[出生公平](<Preparation/MapRubrics/spawn_fairness.csv>)、[通路检查](<Preparation/MapRubrics/route_checks.csv>)、[资源/补货](<Preparation/MapRubrics/resources.csv>)。双主题各四出生区、双出口、最大球通路、交分/脱困检查；碰撞直径与外观/拾取口径分开，测量空白/unknown且NOT_RUN，不预定公平阈值。今后须复制growth_escape行覆盖多个球径与窄道，不只做单例。
- **P08**：[24个输入实验案例](<Preparation/InputExperiments/cases.csv>)及[真人安全条件](<Preparation/InputExperiments/human_safety.md>)由requirements-audit后续交付；主管审阅后接受为未来T04/T05准备。列明A水平投影/有限行程、B/PC统一意图、Jump/Station、各类退出恢复责任； observed/evidence全空、NOT_RUN，自愿参加、轻微不适即停、不收无关个人信息。不是真人舒适PASS。
- **P07**：[30条反馈示意事件](<Preparation/Feedback/events.csv>)、[低FX公平规则](<Preparation/Feedback/low_fx_rules.md>)。仅确认收益可庆祝；低FX不得隐藏关键世界物件、对手状态或交分/截止事实。池限额与合并/触觉时间窗只是未测提案。原作者已补有界HUD回退、五类复合事务重放和退出幂等清理，不把文档断言当运行测试。

只读互审环：requirements-audit→P06，offline-quality→P07，asset-acquisition→P05。修订由原作者执行，未交叉写入。Lead审阅重要内容与状态维护；三份文本完成不使T19/T23/T31或T00/T06 DONE。

## 3. Blender第一批已成功，但需区分历史与接手

历史事实：在用户纠正分工之前，第一批细活由Lead完成。**没有在Linux服务器安装Blender，队员也未偷偷安装。** 实际调用Windows原有Steam Blender5.2.2 LTS，用独立后台工厂场景，不修改用户GUI。服务器做预检查、文件传输和回收PNG。

首轮12项[检查图](<AssetIntake/Preview/renders/contact_sheet.png>)与[测量报告](<AssetIntake/Preview/renders/render_report.json>)已存在；background job `bash-109`退出0，结果已收集，用户既有GUI进程前后保留（公开记录已匿名化）。只读GLB预检发现Food文件引用包内调色板，改为精确白名单嵌入到新副本后渲染。原包与原GLB没改，没导入Unity。

已经明确的候选偏差：bread是面包片不是整条面包；cheese-cut是带孔薄片不是楔块；pot_small为空盆，尚非盆栽组合。12项Blender导入三角面数16–164；不代表项目性能预算通过，也不按Mesh或材质数重复算收益。独立缩放检查图不是实物尺寸图；源单位、轴/pivot语义与Unity/拾取链OPEN。

用户给出Windows本机Blender MCP启动截图（公开说明不保留实例连接细节）。**本轮未配置/使用MCP**，没有把Linux loopback误当Windows。Unity MCP未确认，Unity不运行。

## 4. 源码审查与待闭环事项

offline-quality只读审查发现真实护栏不足：来源manifest路径和包顺序信任、反复打开归档的竞态、非事务写入、GUI中误执行会FactoryReset、GLB窄检查的畸形类型异常、非有限尺寸和工件依赖测试等。Lead不继续抢修，把源码/测试全交P20队员，源工件/Win复测交P21单写者。

P20由队员修复完成：固定批准包ID/路径/哈希、以单次不可变ZIP快照同时做元数据与精选读取、输入/输出覆盖预检、统一GLB拒绝类型、Blender背景/禁止autoexec护栏、有限尺寸和资源预算检查。主管复跑57测试通过，另外识别Windows部署下`parents[3]`不能证明真实Unity目录，原作者补部署无关的路径组件护栏并再次测试通过；主管读取最终guard后明确放行P21。P21使用新的Windows隔离目录与新的回收目录，不覆盖第一批。PNG头部/GLB窄策略不证明所有导入器安全，包哈希不是签名清权；保留真实未测边界。脚本输出仍非事务性，已记录失败留下部分文件的风险，不自动删除或覆盖证据。具体实现见[队员源码交接](<tools/asset_preview/handoff.md>)。

## 5. 验证状态

- 前修复版本完整宿主测试：47项通过（35原基线 + 7GLB/预览 + 5文本结构）；这是修复前的时间点，不冒充最新。
- 最后派发时存在主管半途函数重命名未闭环，已明确交接P20修复，未以坏版本继续执行或验收。
- 根45项双发布DAG和来源哈希原检查通过；单图降级与T32硬依赖的矛盾仍保留，未偷改。
- Unity/Udon、ClientSim、真人VR、多人、运行性能、收费与发布全部NOT_RUN。
- 本轮未commit/push；下载二进制仍ignored，Windows预览不是Unity项目变更。

## 6. 最终闭环

P21由asset-acquisition实际执行：托管job `bash-208`已收集退出0/`PREVIEW_BATCH_COMPLETE 12`；Windows既有GUI进程前后保留，独立新目录，无安装/MCP/Unity操作。上传的修订renderer与本地hash一致，14个输出已逐一核远端/本地hash。主管再次读取[新接触图](<AssetIntake/Preview/verified_renders/contact_sheet.png>)与[队员handoff](<AssetIntake/Preview/handoff.md>)，检查12项完整、14个工件、PNG签名、renderer hash及单位OPEN状态，通过本轮技术预览验收。

部署中hostname显示名与技能旧登记不同；主管以原SSH别名、实际地址/用户、正常known_hosts验证和原GUI/程序路径确认既有设备，未修改主机名、hostkey或凭据。两次只读PowerShell查询引号错误已由执行者调查修正，无写入副作用；两条Blender use_nodes弃用warning不影响退出0。

最终全仓宿主测试**57项通过**，根45任务双DAG/来源哈希通过；source/渲染输入始终不进入Unity。原始第一批工件保留，最新复测另存，工件成功不当成完整导入器安全/商业清权/Unity/真人/性能PASS。

后续按[机器排程](<config/offline_preparation.json>)继续P09规则决策、P10真实资产覆盖、P11加工颗粒度等准备；实施/草案由队员，Lead分工与审查。已完成11个离线工作包，另10个仍TODO，不假装全部已开工，也不跳过平台门禁。
