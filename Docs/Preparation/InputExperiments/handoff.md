# P08 输入平台实验准备交接

## 文件

- [`README.md`](README.md)：范围、实验原则、版本/设备/参数/恢复责任记录要求。
- [`cases.csv`](cases.csv)：PCVR A/B、Desktop、Jump+Station、模式/菜单/追踪状态、KO/退出/Respawn/回合恢复与真人舒适用例。
- [`human_safety.md`](human_safety.md)：自愿测试、停止条件、隐私和舒适表述边界。

## 证据状态

- 所有案例预填 `NOT_RUN`；`observed` 与 `evidence` 保持空白。没有在Unity、ClientSim、VRChat、设备或真人上执行任何测试。
- 记录模式、Unity/VRCSDK/UdonSharp版本、设备/头显/控制器、原生转向设置、输入绑定、校准参数、玩家状态与入场设置快照；恢复责任人/组件使用`restore_owner`列，未知必须明记。
- 覆盖A固定轮廓归中与有限水平行程、Grip接合/释放/回中、追踪丢失/菜单；B与PC共用受限球运动意图、原生转向冲突；Jump上升沿单次、Station不意外跳/退座；KO、切模式、自愿退出、回合结束、平台Respawn与Recovery各自清输入并检查玩家设置恢复。
- 预期是测试目标，不是当前实现事实。Jump监听不等于拦截默认动作；若平台组合无法可靠实现，要记录BLOCKED/不支持，不能偷偷替换成未经批准的键位。
- 任何真人舒适结论需自愿体验和用户批准；轻微不适立即停，不要求强撑。此报告不作医学保证。

## 权限与未做项

Unity运行于Windows用户电脑，确认路径 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`；Editor归用户，未启动。Unity MCP未确认。没有SSH、MCP、Blender、Unity工程访问、下载、源码变更、账号、付费、提交/推送或发布。唯一写入范围为 `Docs/Preparation/InputExperiments/`。

本工作只准备T04/T05实验，不改变其门禁或其他T状态，不冻结公共输入字段/接口/事件；需由主管/用户审阅具体设备参数和实际执行顺序。
