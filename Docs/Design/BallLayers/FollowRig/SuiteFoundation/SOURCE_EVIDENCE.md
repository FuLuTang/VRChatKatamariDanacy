# T55 FollowRig 来源核查与证据边界

核查时间：2026-10-14 UTC。只做Linux离线源码/公开网页读取；未接触Unity、Windows主机、账号或游戏客户端。

## 原版源码可用性

- 本仓库可读到的是项目自己的T46宿主草稿 `Docs/Design/BallMotion/ball_motion.py` 中内嵌`FollowRig`及 `Docs/tests/ball_motion/test_ball_motion.py`；不是《塊魂》或《Once Upon A KATAMARI》的真实源码。
- 原版游戏源码**未取得**。本地GitHub REST仓库元数据检索在 `2026-10-14T00:37:59Z` 对查询 `Katamari Damacy original source code` 和 `Once Upon A KATAMARI source code` 均HTTP 200、`total_count=0`：[GitHub repo search](https://api.github.com/search/repositories)。这是有限查询，不证明互联网任何位置绝无源码，也不构成原版内部方法证据。
- 历史专利US7402104B2只是一份技术披露，不等于游戏真实源码，亦不证明2025新版沿用。已保存的本套共同来源记录见 [VisualRoll SuiteFoundation 来源证据](../../VisualRoll/SuiteFoundation/SOURCE_EVIDENCE.md)。

## 本项目可读依据

- `Docs/Design/BallMotion/ball_motion.py`旧T46草稿用位置+yaw指数滤波，采用`wrap_degrees`短差；旧接口仍以 `(roll,pitch,yaw)` 元组输入，FollowRig被嵌在T46中。
- `Docs/tests/ball_motion/test_ball_motion.py`旧测试检查roll/pitch清零、有限lag与不超目标；没有验证0/360最短弧、重生/瞬移重置、眼高区间或坏输入原子性。
- 项目契约 `Docs/02_TECHNICAL_CONTRACTS.md` §2规定FollowRig与BallVisualRollRoot分离；`Docs/Planning/BALL_WORKFLOW_REPLAN.md` §C要求只读水平朝向、不读取HMD或球自转、重生重置与眼高限制。
- 本模块是项目自定义工程近似，继承旧滤波频率只作为可调起始值，不是原作摄像机方案、舒适性结果或产品参数。

## 公开来源

没有找到可支持原作FollowRig/镜头滤波方式的官方公开技术说明或可复现行为观察。上文仅引本项目文件与有限代码托管仓库元数据查询；不补造网页引文。