# 视觉成长：尺寸合同与验证边界

## 当前规则

**owner 2026-11-17：球芯显示等体积直径 = 当前碰撞球直径。** 由体积反推的碰撞半径既供吸附接触门使用，也决定显示核半径；每次直径变化直接更新，不设死区、不使用追赶弹簧。开局蟹座为 0.222 m，吸收后随碰撞球长大，交分或重置缩小后也跟随。

目的是消除“吸附物贴碰撞球面、可见核却留在里面”产生的核外空腔。吸附物仍冻结在接触瞬间的球局部位姿，尺寸不变；不向心嵌入、不向外推，不缩放附件根。

## 原作依据

证据强度：**玩家观察 + 开发者镜头说法**。[REROLL Steam 讨论](https://steamcommunity.com/app/848350/discussions/0/3561682880003061934/) 的玩家原话是 “the 'core' grows and absorbs all the objects”。Takahashi 2004 postmortem 确认镜头随球变大自动拉远；镜头证据并不直接量出核尺寸。核与碰撞球严格同径是项目当前规则，不宣称是原作代码复原。

[专利 US7402104B2](https://patents.google.com/patent/US7402104B2/en) 的 Mp（显示）、Mh（碰撞）、Mr（滚动）是不同模型；分离模型不证明零售版显示核尺寸恒定。`core_XX` 按关卡命名，是皮肤而非尺寸档。网格等体积径归一化、参考资源许可边界与装配步骤见 [CORE_SKIN](CORE_SKIN.md)。

## 尺寸链与所有权

| 环节 | 当前口径 |
|---|---|
| 账本 → 尺寸 | `SizeModelMath.TryDerive` 从起始体积 + 已确认体积求等体积球；motor `DeriveSize` 发布当前尺寸 |
| 吸附几何 | 读取 `collisionRadiusWorldM`，与道具有向盒到球心最短距离比较 |
| 显示核 | `BallView.Update` 读 `2 * GetCollisionRadiusWorldM()`，有效且变化时写 `CoreMesh.localScale` |
| 皮肤 | `OriginalCoreSkin` 子节点固定缩放 `1 / D_mesh`；父节点 CoreMesh 按当前碰撞直径缩放 |
| 姿态 | motor 写位置与滚转；BallView 只写 CoreMesh 尺度，不写滚动根或附件根 |
| HUD | 语义直径加 0.0002 m 显示偏置；不作为 0.1 mm 精度的核直径标尺 |
| 针与 cap | 仍由 motor 的当前物理尺寸与吸附件盒角点决定，不由皮肤网格或显示缩放反推 |

当前碰撞与语义尺寸比值均为 1；显示核显式读碰撞半径，避免比值改变或 HUD 偏置进入显示尺度。未就绪、非正或非有限尺寸不写入。去重仅比较相等，不积累误差、不延迟微小成长。

## 诊断读数

`BallGrowthDiagnostics` 默认关闭。开启后 `[ballview] mesh` 是 CoreMesh 的 `lossyScale.x`；归一化子节点保证它代表显示等体积直径，**不是凹凸核 AABB 最长边**。`d_vis`、`d_motor` 均为当前碰撞直径；两者与 `mesh` 在 BallView 更新后应相等，误差 ≤0.0001 m。Unity 不保证不同组件的 Update 顺序，操作员应在两者更新完成后取样，不把单帧先后顺序当成尺寸追赶。

`[grow]` 记录吸收批次的账本增量与语义直径变化；`[shell] r_core` 为当前碰撞半径，`r_shell` 仅是最远附件中心距离，`swallowed` 仅统计中心进入核内的件数，不是完整网格埋没数。旧吸附件可以被成长核逐渐包住，不要求 swallowed 接近 0。`[eye]` 的 app 是核直径 / 实际眼点距离的小角近似。

## 成长观感与离线模型

单件涨幅由体积定价决定，不用表现层弹簧遮掩。2026-11-16 owner Play 的 29 次 `[grow]` 记录：单次直径增幅 min 0.2%、中位 2.5%、max 23.8%；D<0.4 档中位 16.3%，D>1.0 档连续 3 次 ≤0.9%。数值问题见 [WHY_OUR_GROWTH_IS_BIG](../../Research/ContentVolume/WHY_OUR_GROWTH_IS_BIG.md)，记录见 [owner Play 摘要](../../Evidence/BallPresentation/2026-11-16_owner_play_log/summary.md)。这些是该次运行的结果，不替代当前分支的 Unity 验证。

[visual_growth_model.py](visual_growth/visual_growth_model.py) 是整数单位账本的离线敏感度模型，其 0.95 视觉比例、0.30 m 起径、2.20 m 上限及贪心吸收序列不代表当前运行配置；不要据其 D_visual、吞没率或可见度列验收当前球芯。当前实现的测试应从真实尺寸重算入口读取碰撞半径。

## 验收

- EditMode：`BallPresentationTests` 驱动真实 BallView，核显示径等于碰撞径；从 0.222 / 0.3 m 起径重算吸收体积后增长，缩小后回落；微小变化也更新；未就绪与无效半径不隐藏核；滚动根与附件根保持单位缩放。
- Unity：UdonSharp 编译、BallPresentation / CoreSkin / ContactModelV2 EditMode；开局核显示等体积直径 0.222 m，吸收 0/10/20/30 件后与当前碰撞直径差 ≤0.1 mm。
- Owner：花纹核随成长变大且刚性滚动，无弹簧拖尾；粘附件贴在核表面附近，不空出整圈；检查凹凸表面的局部缝隙、凸起穿插、旧附件被包入后的轮廓，以及插地和挡路观感。

本次只做离线源码与测试检查；未操作 Unity、未取得新的 Play 截图或原作核尺寸像素测量。精确的变换、Udon 编译与观感结论由 Windows Unity 操作员验证。
