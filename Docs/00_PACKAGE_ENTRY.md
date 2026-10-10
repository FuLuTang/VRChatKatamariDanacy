# VRC塊魂 /《我是屎壳郎》：整体方案与多Agent执行计划 v0.3

研究日期：2026-10-10。此文档是建议及开发契约，不是已完成的实现或测试报告。

# VRC塊魂：执行设计包 v0.3
研究日期：2026-10-10。内部名 VRC塊魂；公开名未最终确定，《我是屎壳郎》为主题方向。

这是设计、接口、任务和验收包，不是已实现的Unity工程。
没有运行Unity、VRChat真机、多人联机或实际商品购买测试；没有下载候选第三方资源。
所有速度、距离、时间、数量、价格方案都是可调原型值，不是原作数值或性能实测。

## 给总控 Agent 的第一条指令
先读 `docs/00_SCOPE_DECISIONS.md`、`docs/02_TECHNICAL_CONTRACTS.md`、
`docs/07_AGENT_OPERATING_RULES.md` 和 `tasks.json`。
只启动依赖满足的任务。先完成平台验证和接口冻结，再扩展模块。
旧聊天仅作背景；遇到冲突以本包的明确决策为准，未定项不能自行伪装成既定结论。
没有用户提供的Unity项目路径、账号授权或测试设备时，完成可独立的文件任务并标记外部阻塞，
不得编造测试成功、公开上传世界、花钱、修改正式商品或向他人发消息。

## 交付入口
- `docs/00_SCOPE_DECISIONS.md`：边界、纠正、未决项。
- `docs/01_GAME_DESIGN.md`：玩法、输入、大厅、道具、反馈。
- `docs/02_TECHNICAL_CONTRACTS.md`：球体、物件、网络、身份、缩放。
- `docs/03_MAPS_ASSETS.md`：两张首发图及第三张候选、资产加工。
- `docs/04_EXECUTION_PLAN.md`：阶段、依赖和逐任务说明。
- `docs/05_ACCEPTANCE_TESTS.md`：自动测试与真机门禁。
- `docs/06_MONETIZATION_LAUNCH.md`：装扮、内购、宣传。
- `docs/07_AGENT_OPERATING_RULES.md`：Sol/Luna调度和并行写入规则。
- `docs/08_SOURCES_AND_OPEN_QUESTIONS.md`：研究来源及事实边界。
- `tasks.json`：可供调度器读取的任务DAG。
- `diagrams/`：纵向依赖图、阶段甘特图，均为Mermaid源码。
- `config/prototype_defaults.json`：原型配置，不是经过平衡的正式值。
- `templates/`：资源清单、实验记录、Agent交接模板。
- `tools/validate_plan.py`：仅验证设计包的任务依赖/字段；不验证游戏实现。

## 首版范围
PCVR + Desktop，2–4人休闲PVP，单人练习。
两种VR控制；PC采用普通移动与观察输入。只有球参与玩法碰撞。
磁铁和火箭两种道具、单槽、跳跃动作键使用。
先做好一张完整图，首发目标两张；第三主题不阻塞上线。
普通玩家私人Avatar保留；不强制更换王子或昆虫Avatar。
免费完整玩法，少量纯外观永久商品；没有付费战力、抽奖或现金奖励。

## 硬门禁
1. VR Station追尾、InputJump不退座、A/B两种输入和一键退出必须真机通过。
2. 共享世界坐标固定，不允许每位玩家各自缩放公共竞技地图。
3. 一件物品一个世代只能成功归属一次；所有积分来自已确认账本。
4. 断线迁移、迟到加入、回合重置必须通过多人测试。
5. API权益验证和资源商业授权通过后才启用付费商品。
6. 舒适性必须由真人体验；代码通过不能代替佩戴头显验收。



---
