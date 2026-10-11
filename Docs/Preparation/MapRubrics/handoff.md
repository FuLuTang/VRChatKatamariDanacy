# P06 地图测量模板交接

- 任务：`task-10` / P06；当前提交的是离线测量准备稿，不是地图实施或玩法验收。
- 独占写入：`Docs/Preparation/MapRubrics/`。文件：`README.md`、`spawn_fairness.csv`、`route_checks.csv`、`resources.csv`、`handoff.md`。
- 依据：`Docs/03_MAPS_ASSETS.md`、`Docs/05_ACCEPTANCE_TESTS.md`、任务T20/T21/T22/T30/T31/T32/T38/T39、v0.3 provisional config。四区标签和野餐/玩具房主题仅用于模板分组，不表示地图已存在。
- 所有实际收益、路程、碰撞直径、Mesh尺寸、通道净宽、补货时间及统计值均留空或 `unknown`；样例状态均为 `NOT_RUN`。未选择公平容差、最低资源量、路径余量或补货上限。
- README列出统一世界坐标与单位、碰撞直径/可视Mesh/拾取门槛区别、可达确认收益和路程/耗尽/补货公式、待用户/主管决定的阈值与证据要求。
- CSV结构检查：只读标准库CSV解析，三个文件所有数据行与表头字段数一致（spawn 22列、route 24列、resources 25列）。这只是模板格式检查，不是地图测试。
- Unity 位于 Windows `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，Editor归用户；未启动Unity，MCP未确认。本任务未使用SSH/MCP/Unity/Blender，未下载/操作账号/付款/发布/Git。
- 未生成地图，未作路线公平结论；G4/T39真人实测仍NOT_RUN，T21/T31/T39未因此完成。
