# 当前项目待决事项摘要

**用途：**把已审阅的规则决策表与发布RFC压缩成用户可回答的选项。本文只提供现状、影响与需要决定的阶段；**不替用户推荐或选择**，也不改根任务、DAG、产品范围、原作规则或接口。

## 当前状态

- 已选项目方向与待确认选项分开记录；`recommended_option`为空。官方资料只确认KatamariBall为限时滚大、交存换分、最高分获胜等宽泛描述，未给出具体PVP流程。([规则矩阵说明](../../Preparation/RuleDecisions/README.md#L3-L18)、[决策表:L5-L6](../../Preparation/RuleDecisions/decisions.json#L5-L6)、[原作规则范围:L29-L43](../../Preparation/RuleDecisions/decisions.json#L29-L43))
- 当前项目基线仍为首发两图目标、第一纵切片一图两人、夜市后置；T39允许在完整证据后批准两图或降为一图。([范围决策:L15-L22](../../00_SCOPE_DECISIONS.md#L15-L22)、[发布RFC:L7-L10](../../Preparation/ReleaseRFC/comparison.md#L7-L10))
- P09/P17没有记录新的用户决定；根任务/DAG未因本摘要改变。([P09/P17审查:L15-L16](../../18_LARGE_SCALE_PREPARATION_REVIEW.md#L15-L16)、[决策表非决定:L178-L182](../../Preparation/RuleDecisions/decisions.json#L178-L182))

## 需要在正式发布范围/终验前回答

| 事项 | 当前基线与选项（均未选择） | 影响 | 决定时点 |
|---|---|---|---|
| **首发地图数与地图选择** | A. 保留野餐草坪+玩具房两图目标；B. 明确降为一张完整体验，并由用户指定是哪张。野餐是纵切片起点，不等于已选为唯一首发图。 | 一图仍须完整公平/可玩验证；若降一图，必须经授权修订T37/T38/T40依赖与验收，未选地图应保留为后续、延期或另作决定；不能跳过T32或伪标完成。 | 在T39正式终验前明确范围；T39再依据实际证据作最终体验批准。批准前维持当前两图目标和原DAG。([候选与影响](../../Preparation/ReleaseRFC/comparison.md#L14-L27)、[批准纪律](../../Preparation/ReleaseRFC/comparison.md#L29-L38)、[任务依赖](../../../tasks.json#L1290-L1414)) |
| **正式公平门槛** | 可在正式试玩前先设数值阈值；也可先做明确标为“探索”的试跑，再设阈值；或选定定性评估方式。待定项目包括出生资源差、路线/交分时间差、重生最低资源、补货延迟、重复次数/统计方式及软锁标准。 | 若先探索，探索样本不能追认成PASS；若用定性评估，可复现性较低。T38四人性能测试条件与T39公平标准也不能靠该RFC自动确定。 | 用户/主管须在T39正式试验前明确标准；T39仍需真人证据。([公平阈值选项:L129-L143](../../Preparation/RuleDecisions/decisions.json#L129-L143)、[T38/T39门槛](../../../tasks.json#L1328-L1397)、[RFC未定阈值:L31-L34](../../Preparation/ReleaseRFC/comparison.md#L31-L34)) |

## 可暂缓至项目试玩/相应契约门禁

| 事项 | 已知与未决边界 | 需要决定的阶段 / 暂缓条件 |
|---|---|---|
| **回合结构、时长与交存细则** | 官方只说明限时、交存换分、最高分胜出；确切时长、回合数、平分、交存地点/触发、计分公式与截止处理均未由官方资料确定。当前2–4人、约180秒单回合只是项目原型，不是原作数据。([决策表:L29-L59](../../Preparation/RuleDecisions/decisions.json#L29-L59)) | 可先保留为可调试配置，待相关节奏/计分试玩后选择；若要改变项目范围再由用户明确批准。不得将原作`unknown`补成事实。([项目基线与优先级](../../00_SCOPE_DECISIONS.md#L15-L22)、[交存事务边界](../../Preparation/RuleDecisions/decisions.json#L46-L59)) |
| **KO、重生、PVP阈值与道具平衡** | 原作资料未确认是否吞噬/KO、尺寸比、保护、重生及道具在PVP中的投放。项目方向保留KO/重生，并选择单槽磁铁/火箭与Jump输入；1.35比例、约3秒等待、约2秒保护是未验证项目参数，不是原作规则。([决策表:L62-L110](../../Preparation/RuleDecisions/decisions.json#L62-L110)) | 相关参数须随T27/T39多人、公平与舒适证据调校；新增计时器等道具属于范围变更，不从官方“道具列表”自动推出PVP支持。([范围边界](../../00_SCOPE_DECISIONS.md#L25-L32)、[决策所有者:L74-L110](../../Preparation/RuleDecisions/decisions.json#L74-L110)) |
| **生长/碰撞上限与世界尺度** | 语义显示尺寸、碰撞半径、拾取门槛与收益账本必须分开；0.2–5米只是表现工作区间，不是碰撞或技术上限。最终半径、成长段、速度/物理限值仍未定。([技术契约:L15-L21](../../02_TECHNICAL_CONTRACTS.md#L15-L21)、[决策表:L146-L159](../../Preparation/RuleDecisions/decisions.json#L146-L159)) | 先保持参数化；测量须由平台、地图和公平/性能证据支持，再由Lead在T01/T05后处理契约。T06未冻结前不得硬编码为公共接口。([T06依赖与范围](../../../tasks.json#L253-L279)、[契约冻结规则](../../02_TECHNICAL_CONTRACTS.md#L3-L13)) |
| **付费外观SKU/价格** | 方向是免费完整玩法与可选纯外观；商品目录、价格、资格、授权和交易恢复均未批准。([决策表:L162-L175](../../Preparation/RuleDecisions/decisions.json#L162-L175)) | 可暂不决定并保持商店关闭；只有进入相应许可、权益和交易门禁后才需用户选SKU/价格。([发布配置](../../../tasks.json#L2094-L2105)、[范围排除](../../00_SCOPE_DECISIONS.md#L38-L42)) |

## 不因本摘要改变的门禁

- P17中的单图方案只是提案；修改任务依赖只能在用户/主管批准后进行，并须运行计划校验。([RFC执行纪律](../../Preparation/ReleaseRFC/comparison.md#L36-L38)、[候选DAG声明](../../Preparation/ReleaseRFC/proposed_dag.json#L5-L7))
- 本文不认证原作细节、不定正式公平阈值、不冻结公共接口、不改T任务状态；后续批准应另行明确记录。
