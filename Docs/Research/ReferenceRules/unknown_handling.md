# T01：unknown 项目的处理清单

状态：供主管评审批准；不是新增原作事实、冻结规则或接口。依据是本目录的[规则矩阵](rules_matrix.md#L11-L25)和已推送的[项目决策表](../../Preparation/RuleDecisions/decisions.json#L27-L183)。矩阵中的 S02/S03 官方文字只确认 KatamariBall 是限时滚大并兑换积分、最高分获胜；它没有给出下列细节。下表给出的都是**项目处理办法**，绝不把它们反推为原作规则。

## 使用约定

- **项目已定方向**：来自用户明确的项目范围/当前 v0.3 设计；含义仅是本项目当前方向，不代表参数已测或接口已冻结。
- **原型/待验证**：当前可用作可配置的实验起点。相关任务完成并经评审前，不把数值写成已验证规则。
- **待用户决定**：项目方向/正式验收门槛仍需用户批准。原作 unknown 本身不是让项目臆测的空格；若项目选择不还原，继续标注“原作未知”。
- `decisions.json` 中各行的 `recommended_option` 仍为 `null`；本清单是逐项处理提案，不声称该表已经批准这些选项。正式公共接口仍由 T06 门禁处理。

当前项目方向的出处：项目以 PCVR/Desktop、人类玩家 PvP 为首发范围；单人仅练习，不做复杂机器人；保留吞噬/KO/重生方向；选择单槽磁铁与火箭、不选 timer；以已确认 banked score 排名、初始球芯不重复获利；地图先做一图双人纵切片、当前目标两图。[范围决策](../../00_SCOPE_DECISIONS.md#L3-L22) [玩法设计](../../01_GAME_DESIGN.md#L12-L31) [技术事务契约](../../02_TECHNICAL_CONTRACTS.md#L128-L141)

## 全部 T01 unknown 及处理

表内“原作 unknown”依 [规则矩阵](rules_matrix.md#L13-L18) 逐项拆开；交叉适用的 P09 项目验收/尺寸 unknown 也一并列明，避免把它们误当原作数据。

| ID | 原作 unknown（未验证，不补造） | 项目当前处理 / 状态 | 决策与项目出处 |
|---|---|---|---|
| U01 | 原作准确回合时长。 | 原型从 180 秒开始，必须配置化；在两人核心局观察节奏后由用户与主管决定是否调整。不是原作数值。 | `rules.match_format`；[设计](../../01_GAME_DESIGN.md#L12-L16)；[T27](../../../tasks.json#L937-L967) |
| U02 | 原作是否有多轮/分段。 | 当前项目先做单场单回合；多轮不实现，除非之后用户明确改产品范围。 | `rules.match_format`；[决策候选](../../Preparation/RuleDecisions/decisions.json#L29-L43) |
| U03 | 原作平局规则。 | 项目按已确认安全积分排序，同分并列，不按网络到达毫秒拆胜负；不宣称复刻原作。 | `rules.match_format`；[设计](../../01_GAME_DESIGN.md#L13-L16) |
| U04 | 四人模式在线每种情境是否由 CPU 补位。 | 项目方向为 2–4 名真人；单人提供练习，不开发复杂机器人。建议首发不做 CPU 补位；若用户要求机器人，作为范围变更单独批准。 | `rules.match_format`；[范围](../../00_SCOPE_DECISIONS.md#L5-L6)；[设计](../../01_GAME_DESIGN.md#L12-L14) |
| U05 | 截止时正在处理的交存请求如何归属/裁决。 | 项目进入 Settling，只结算截止规则下已收到且有效的请求；迟到请求不追认。`<` 或 `<=` 的边界及具体时钟定义须在 T06/T27 前明文决定并测试，现未冻结。 | `rules.score_and_exchange`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L141)；P12 仅为 host fixture，非运行时批准规则。 |
| U06 | 原作交存地点是固定还是移动。 | 项目方向用可读交分区/预制锚点轮换并提前提示；具体移动周期仍为配置与地图测试项。 | `rules.score_and_exchange`；[设计](../../01_GAME_DESIGN.md#L17-L20) |
| U07 | 原作交存是手动还是自动触发。 | 项目采用明确交互/请求并由裁决账本确认；不做无提示自动计分。交互实现与平台验证待 T06/T27。 | `rules.score_and_exchange`；[事务契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U08 | 原作交存触发范围。 | 项目使用明确的交分区与经配置的有效范围；半径作为地图/交互参数测量，不填原作值。 | `rules.score_and_exchange`；[设计](../../01_GAME_DESIGN.md#L17-L20)；T27 |
| U09 | 原作交存动画与耗时。 | 动画仅作确认后的表现，不能延后或提前伪造账本状态；时长是可调表现参数，试玩评估，不与交存成功语义耦合。 | `rules.score_and_exchange`；[事务契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U10 | 原作每局/每次可交存频率。 | 项目每次已确认交存都是独立幂等事务；回合内可再次采集和交存，重复 bank ID 不重复计分。具体速率限制按地图/试玩确定。 | `rules.score_and_exchange`；[事务契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U10a | 原作每次交存全部清空还是允许分段兑换。 | 项目每次确认结算当时已赚取资源；首版不提供玩家部分交存选择，除非主管批准规则变更。 | `rules.score_and_exchange`；[设计](../../01_GAME_DESIGN.md#L14-L18)；[决策表](../../Preparation/RuleDecisions/decisions.json#L46-L59) |
| U11 | 原作兑换后未交存成长是否保留。 | 项目交存时结算当次实际 earned value 并重置成长；不允许初始免费球芯刷分。 | `rules.score_and_exchange`；[设计](../../01_GAME_DESIGN.md#L14-L18)；[基线](../../09_REPOSITORY_BASELINE.md#L9-L19) |
| U12 | 原作交存后球是否重置。 | 项目方向是回到初始球芯；具体半径/视觉、账本与动画须一致，接口等 T06 冻结。 | `rules.score_and_exchange`；[设计](../../01_GAME_DESIGN.md#L17-L18)；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U13 | 原作交存是否给保护。 | 项目当前不额外承诺银行无敌或保护窗；首版按普通 PvP 状态继续裁决。若试玩显示交存点被锁死，T27/T39 提出受限方案再由主管评审，不能直接假定原作有保护。 | `rules.score_and_exchange`；[设计](../../01_GAME_DESIGN.md#L19-L20)；[T27](../../../tasks.json#L937-L967)；[T39](../../../tasks.json#L1342-L1371) |
| U14 | 原作交存可否被中断。 | 项目账本事务原子提交：请求待确认时不改分；提交后不回滚已确认账本。动画是否可打断只影响表现，具体输入/UI待 T27 测试。 | `rules.score_and_exchange`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U15 | 原作体积/资源换算积分公式。 | `earnedVolumeUnits` 与 `bankedScore` 分离；由 T06 定义项目单位/映射，并用试玩确认数值；不从 Mesh 包围盒推分，也不宣称原作公式。 | `rules.score_and_exchange`、`project.item_growth_and_collision`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L15-L21)；[T06](../../../tasks.json#L224-L258) |
| U16 | 原作 KatamariBall 是否允许吞噬对手。 | 原作继续标 unknown；项目方向已保留吞噬/KO，但启用前受单一裁决、相同共享坐标与双人测试门禁约束。 | `rules.pvp_swallow_threshold`；[范围](../../00_SCOPE_DECISIONS.md#L44-L49)；[设计](../../01_GAME_DESIGN.md#L22-L31) |
| U17 | 原作吞噬尺寸比/条件。 | 项目 `1.35` 仅作可配置原型值，不能硬编码成“原作阈值”；T27/T39 根据证据调参或关闭功能。 | `rules.pvp_swallow_threshold`、`project.item_growth_and_collision`；[设计](../../01_GAME_DESIGN.md#L22-L30)；[决策表](../../Preparation/RuleDecisions/decisions.json#L62-L76) |
| U18 | 原作接触方向是否影响吞噬。 | 项目首版候选按共享球体碰撞/阈值判定，不根据 Avatar 朝向或客户端视觉装饰判定；是否需要方向规则，先通过 T27 明确测试再由主管定契约。 | `rules.pvp_swallow_threshold`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L6-L21)；[T27](../../../tasks.json#L954-L966) |
| U18a | 原作吞噬判定是否受保护状态影响。 | 项目将明确记录保护状态并由单一裁决者检查；具体哪些保护窗口阻止吞噬，纳入项目规则与 T27/T39 试玩门禁，不反推原作。 | `rules.pvp_swallow_threshold`、`rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L22-L30)；[T27](../../../tasks.json#L954-L966) |
| U18b | 原作接触碰撞/命中如何处理。 | 项目以共享碰撞半径和统一碰撞判定为基础；接触/推开/吞噬先后等冲突不得由各客户端独立决定，细节经 T06/T27 评审。 | `rules.pvp_swallow_threshold`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L6-L21)；[T06](../../../tasks.json#L224-L258) |
| U19 | 原作吞噬时收益/损失比例与资源守恒。 | 项目攻击收益受被吞者未交存资源上限约束，已 banked score 保留；份额作为可配置参数，经 T27/T39 调整，不制造净价值。 | `rules.pvp_swallow_threshold`、`rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L23-L28)；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U20 | 原作双方同时命中/连续接触的裁决顺序。 | 项目只能有一份权威结果；使用幂等请求与冷却，禁止双方各自宣布击杀。排序策略须 T06 定义、T27/T28 测试，延迟/公平仍不得宣称通过。 | `rules.pvp_swallow_threshold`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L100-L112)；[T27](../../../tasks.json#L954-L967) |
| U21 | 原作是否存在 KO。 | 原作标 unknown；项目方向保留 KO/重生，作为本项目玩法而非还原结论。 | `rules.pvp_swallow_threshold`、`rules.ko_respawn`；[范围](../../00_SCOPE_DECISIONS.md#L44-L49) |
| U22 | 原作是否重生及 KO 等待时长。 | 项目原型短暂出局，约 3 秒仅作未验证起点，必须配置化；T27/T39 结合可玩性/公平性测后调整。 | `rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L23-L30)；[决策表](../../Preparation/RuleDecisions/decisions.json#L79-L93) |
| U23 | 原作重生点选择规则。 | 项目从多个安全点选择远离敌球且附近有低阶资源的位置；无安全点时延迟释放保护或重选。地图策略在 T27/T39 验证。 | `rules.ko_respawn`、`project.fairness_thresholds`；[设计](../../01_GAME_DESIGN.md#L25-L28)；[T39](../../../tasks.json#L1359-L1368) |
| U24 | 原作重生保护是否存在及其条件。 | 项目约 2 秒保护是未验证可配置原型，不是原作值；保护期间暂禁攻击/交分/抢中心道具，T27/T39 检查套利与安全。 | `rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L25-L28)；[T27](../../../tasks.json#L954-L966) |
| U25 | 原作 KO 期间是否观战。 | 项目设计只采用短暂 KO 提示与稳定观战/淡出转场作为备选表现；具体选择待可读性/舒适性验证，不改变比赛账本。 | `rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L28-L30) |
| U26 | 原作重生后携带道具/状态如何处理。 | 项目 KO 清理道具与临时输入/效果，重生后按项目补给规则重新获得；若改成保留道具，须主管批准并测重复收益。 | `rules.ko_respawn`、`rules.powerups_in_pvp`；[设计](../../01_GAME_DESIGN.md#L25-L30)；[道具生命周期](../../01_GAME_DESIGN.md#L82-L86) |
| U27 | 原作重生后玩家资源如何恢复。 | 项目保留已交存分，清除被 KO 玩家未交存成长；初始球芯与出生资源是项目补给而非原作补偿。 | `rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L23-L28)；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135) |
| U28 | 原作是否给重生追赶补偿。 | 项目不直接赠送追赶分；低阶地图资源作为待验证补给，受公平性预算约束。 | `rules.ko_respawn`、`project.fairness_thresholds`；[设计](../../01_GAME_DESIGN.md#L25-L28)；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143) |
| U29 | 原作 PVP 是否投放道具。 | 即使原作 FAQ 列出道具，也不能推断其出现在 KatamariBall。项目选择磁铁/火箭，但仅在项目事务及平台门禁通过后启用。 | `rules.powerups_in_pvp`；[矩阵](rules_matrix.md#L17)；[设计](../../01_GAME_DESIGN.md#L51-L56) |
| U30 | 原作 KatamariBall 道具种类。 | 项目当前范围为单槽磁铁+火箭，不加入 timer；扩展道具需要用户明确范围变更。 | `rules.powerups_in_pvp`；[范围](../../00_SCOPE_DECISIONS.md#L15-L21)；[决策表](../../Preparation/RuleDecisions/decisions.json#L95-L110) |
| U31 | 原作 PVP 道具出现/刷新地点。 | 项目以权威刷新世代管理道具，避开出生点并给刷新预告；地图位置配置化并由 T39 验证。 | `rules.powerups_in_pvp`、`project.fairness_thresholds`；[设计](../../01_GAME_DESIGN.md#L82-L86)；[T39](../../../tasks.json#L1359-L1368) |
| U31a | 原作 PVP 道具刷新周期/方式。 | 项目周期为未定配置参数；按 zone/tier 和公平性测试后由主管定值，不引用原作数值。 | `rules.powerups_in_pvp`、`project.fairness_thresholds`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L114-L126) |
| U32 | 原作道具拾取/库存规则与槽位。 | 项目单槽；槽满不覆盖；拾取和使用按同一共享状态/幂等裁决。 | `rules.powerups_in_pvp`；[设计](../../01_GAME_DESIGN.md#L51-L56)；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L100-L112) |
| U33 | 原作道具持续时间。 | 项目 Magnet 4 秒、Rocket 2.5 秒仅是可调待测 prototype，不是原作数值。 | `rules.powerups_in_pvp`；[设计](../../01_GAME_DESIGN.md#L67-L86)；[决策表](../../Preparation/RuleDecisions/decisions.json#L96-L110) |
| U33a | 原作道具冷却。 | 项目只在明确定义的生命周期状态中允许激活；具体冷却是否需要及数值尚待 T27/T39 体验数据和主管评审。 | `rules.powerups_in_pvp`；[设计](../../01_GAME_DESIGN.md#L82-L86) |
| U33b | 原作 PVP 道具平衡。 | 不宣称平衡已验证；在 T27/T39 中对照无道具基线并调配置。 | `rules.powerups_in_pvp`；[T27](../../../tasks.json#L954-L966)；[T39](../../../tasks.json#L1359-L1368) |
| U34 | 原作道具作用目标/范围。 | 项目磁铁只处理可采集地图物件；火箭只增强持有者球体运动。具体范围参数以项目配置和测试为准。 | `rules.powerups_in_pvp`；[设计](../../01_GAME_DESIGN.md#L67-L80) |
| U34a | 原作道具是否影响对手。 | 项目当前设计不直接吸引/攻击玩家；任何影响对手的效果属于新增规则，需另行批准。 | `rules.powerups_in_pvp`；[设计](../../01_GAME_DESIGN.md#L67-L80) |
| U35 | 原作道具是否跨 KO/交存保留。 | 项目 KO 取消 active effect；交存取消加速并清理状态。held item 是否保留以项目规则明确后测试，不从原作推断。 | `rules.powerups_in_pvp`、`rules.ko_respawn`；[设计](../../01_GAME_DESIGN.md#L82-L86) |
| U36 | 原作 KatamariBall 地图数量。 | 项目当前纵切片一图双人，上市目标 Picnic Lawn + Toyroom，Night Market 后置；一张或两张首发由用户与主管在 T39 证据后明确选择，未改任务依赖前不得宣称范围已降级。 | `project.map_count`；[范围](../../00_SCOPE_DECISIONS.md#L15-L21)；[决策表](../../Preparation/RuleDecisions/decisions.json#L113-L126)；[T39](../../../tasks.json#L1342-L1371) |
| U36a | 原作地图轮换/选图机制。 | 本清单建议：先只开放已验证地图供选择，不引入自动轮换；等地图数与路线体验证据后由主管定方案。此处理提案待评审批准。 | `project.map_count`；[范围](../../00_SCOPE_DECISIONS.md#L15-L21)；[T39](../../../tasks.json#L1359-L1368) |
| U37 | 原作地图资源种类。 | 项目只采用获准资源清单与尺寸层；清单是项目内容选择，不声称原作完整物件表。 | `project.item_growth_and_collision`；[设计](../../01_GAME_DESIGN.md#L12-L20)；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L71-L85) |
| U37a | 原作资源点初始位置/地图布局。 | 项目先灰盒布置，所有客户端使用共享地图坐标；路线和出生公平由 T39 检验。 | `project.fairness_thresholds`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L6-L21)；[T39](../../../tasks.json#L1359-L1368) |
| U38 | 原作地图资源刷新方式/周期。 | 项目按 zone/tier 预算补充；近球/视野内允许延迟并预告，延迟值配置化并纳入 T39。 | `project.fairness_thresholds`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L114-L126)；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143) |
| U38a | 原作 KO 后的重生资源补给量/时机。 | 项目提供低阶资源作为原型方案；最低资源与补给窗口未定，需用户/主管在 T39 正式试验前批准门槛。 | `rules.ko_respawn`、`project.fairness_thresholds`；[设计](../../01_GAME_DESIGN.md#L25-L28)；[决策表](../../Preparation/RuleDecisions/decisions.json#L79-L93) |
| U39 | 原作交分区移动规则与周期。 | 项目预制锚点轮换且提前提示；具体周期/路线及交分时间差需地图数据+T39实测后定。 | `rules.score_and_exchange`、`project.fairness_thresholds`；[设计](../../01_GAME_DESIGN.md#L17-L20)；[T39](../../../tasks.json#L1359-L1368) |
| U40 | 原作项目地图在发布版本的数量。 | 当前两图是目标，不是批准的发布承诺；T39 根据两图公平/质量证据，由用户和主管决定保留两图或明确降为一图并更新 DAG。 | `project.map_count`；[基线开放问题](../../09_REPOSITORY_BASELINE.md#L44-L50)；[T39](../../../tasks.json#L1361-L1368) |
| U41 | 可接受的出生资源方差。 | 当前无数值 PASS 门槛；用户/主管在 T39 正式多人试验前批准可接受区间。 | `project.fairness_thresholds`；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143)；[T39](../../../tasks.json#L1359-L1368) |
| U41a | 可接受的路线差异。 | 同上；测量项目地图路线/绕行，不借此声称原作公平阈值。 | `project.fairness_thresholds`；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143)；[T39](../../../tasks.json#L1359-L1368) |
| U41b | 可接受的交分耗时差。 | 同上；记录交分路线和银行窗口到达时间后设门槛。 | `project.fairness_thresholds`；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143)；[T39](../../../tasks.json#L1359-L1368) |
| U42 | 重生玩家最低资源量。 | 未定项目验收阈值；用户/主管在 T39 正式试验前设定。 | `project.fairness_thresholds`；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143) |
| U42a | 地图补货最大延迟。 | 未定项目验收阈值；配置并记录刷新延迟后，T39 评估。 | `project.fairness_thresholds`；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143) |
| U42b | 正式试验重复轮数及统计方法。 | 先明确样本/重复次数和统计方法；探索性 pilot 不能追认成正式 PASS。 | `project.fairness_thresholds`；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143) |
| U42c | 软锁判据。 | 在 T39 前定义需检测的软锁（如重生无物可吃、永久领先、交分点拥堵）和阻断级标准。 | `project.fairness_thresholds`；[T39](../../../tasks.json#L1361-L1368)；[决策表](../../Preparation/RuleDecisions/decisions.json#L129-L143) |
| U43 | 最终最大碰撞半径。 | 暂不把 0.2–5 m 表现工作区间当碰撞值；T06 前参数化，结合 T04/T27/T39 证据量测。 | `project.item_growth_and_collision`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L6-L21)；[决策表](../../Preparation/RuleDecisions/decisions.json#L146-L159)；[T06](../../../tasks.json#L224-L258) |
| U43a | 成长层级与拾取阈值。 | 使用独立项目配置/游戏尺寸，不从 Mesh 包围盒推定；T06 定义字段和单位，T27/T39 调测。 | `project.item_growth_and_collision`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L15-L21)；[T06](../../../tasks.json#L224-L258) |
| U43b | 各物件 gain 值/价值换算。 | 以确认账本记值，先列调参表；具体单位/换算在 T06 冻结前评审，不冒称原作数据。 | `project.item_growth_and_collision`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L15-L21)；[T06](../../../tasks.json#L224-L258) |
| U43c | 加速度与物理影响上限。 | 作为可配置动力/碰撞参数，经物理与多人门禁测试；无实测前不作性能或公平性承诺。 | `project.item_growth_and_collision`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L32-L61)；[T27](../../../tasks.json#L954-L966) |
| U43d | 实用 Avatar/Station 尺度包络。 | 0.2–5 m 是用户选择的表现工作区间，并非技术极限；需由 T04 设备/Station 与实际地图测试给出包络。 | `project.item_growth_and_collision`；[技术契约](../../02_TECHNICAL_CONTRACTS.md#L6-L21)；[T04](../../../tasks.json#L156-L190)；[T27](../../../tasks.json#L937-L967) |

## 已定项目方向（仍不能冒称实测）

1. 用真人 PCVR/Desktop PvP；2–4 人为范围方向，Quest 不在首发；单人是练习，不做复杂 CPU。180 秒、1.35 比率、KO 约 3 秒、保护约 2 秒等均属可调、未验证项目原型，不是已批准平衡值。[范围](../../00_SCOPE_DECISIONS.md#L3-L22) [决策表](../../Preparation/RuleDecisions/decisions.json#L29-L43)
2. 以确认的 banked score 排名，同分并列；earned/unbanked 与 banked ledger 分开，交分才转换，项目初始球芯不得反复刷分。bank 事务成功才变更状态。[玩法设计](../../01_GAME_DESIGN.md#L12-L20) [技术契约](../../02_TECHNICAL_CONTRACTS.md#L128-L135)
3. 保留吞噬/KO/重生作为项目玩法方向；按共享裁决判定，KO 清理未交存资源、保留已交存分。阈值、时间和公平性尚待证据。[范围](../../00_SCOPE_DECISIONS.md#L44-L49) [玩法设计](../../01_GAME_DESIGN.md#L22-L31)
4. 道具方向为单槽磁铁+火箭，Jump 上升沿触发；不加入 timer/额外经典冲刺。道具持续时间与数值仍待测试。[范围](../../00_SCOPE_DECISIONS.md#L15-L21) [玩法设计](../../01_GAME_DESIGN.md#L51-L86)
5. 一图双人纵切片；两图为当前发布目标，夜市后置；最终一图/两图发布选择没有批准。[范围](../../00_SCOPE_DECISIONS.md#L15-L22) [T39](../../../tasks.json#L1342-L1371)

## 尚需用户决定（便于主管提问）

1. **首发地图数**：等 T39 实测后，是保留 Picnic Lawn + Toyroom 两图，还是批准降为一张完整地图？如降级，需同步更新任务依赖与验收，不静默改表。
2. **正式公平性门槛**：T39 正式试验前，采用哪些可量化阈值（出生资源方差、路线/交分耗时差、重生最低资源、补货延迟、重复轮数/统计方法及软锁判据）？也可先批准探索性 pilot、之后再定门槛，但 pilot 不算 PASS。
3. **回合结构参数**：180 秒单回合是当前起点。试玩证据出来后，是否继续此时长/结构，或批准改为其他时长/多轮？该选择需用户与主管确认；原作具体时长仍不能由项目测试推知。

主管/评审批准：**PENDING**（需批准本清单对所有 unknown 的项目处理；未批准前继续按临时/待验证标记执行，不冻结公共接口）。

## 范围外

`project.commercial_sku` 中资格、商品、价格、税币显示、交易恢复等 unknown 属商业门禁，不属于 T01 参考规则；仍按决策表的 T34–T37/T41 与人工批准要求处理，本清单不代为定案。[决策表](../../Preparation/RuleDecisions/decisions.json#L162-L175)
