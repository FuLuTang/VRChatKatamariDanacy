# T50 球层：径向凹凸代理（小棍采样外壳）

> **DRAFT / host-only reference.** Python标准库参考不是Unity/Udon交付；C# RuntimeDraft是普通源码候选、未编译/未作为组件；N=32和4候选是性能未测原型参数，接口未冻结。

## 研究与项目规则边界

- 未取得原版源码；US7402104B2仅为历史专利公开，不能证明新版采用。官方产品资料不含径向支持算法；GitHub Ruby/Gosu资源是独立2D实验而非原版源码。详见A套 [`SOURCE_EVIDENCE.md`](../GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md)。
- 项目 `Docs/02_TECHNICAL_CONTRACTS.md` §3.2 写有方向采样、每对象端点、max支持、移除重算/账本重建。T50实现这些是项目自定义近似，不代表反编译或还原原作。
- Unity左手坐标与Cross规则已对照官方资料；Python host Hamilton quaternion不是Unity相等性主张。Integration要求方向箭头对拍：`AngleAxis(+90°, +Y)`下+Z实际落轴及world/local往返，用方向事实建立显式adapter。

## 模块行为

- 32个确定性Fibonacci方向；原型值，尚未做设备性能验证。附着输入是稳定实例键（需包含spawn世代）和预烘焙世界端点；将端点转换到球局部后，只增量更新端点外半球方向。
- 每个方向重算遍历仍在场的所有端点，支持是 `max(core_radius, dot(point,direction)...)`。移除最大贡献后保留次大贡献；该层只移除贡献，不计算世界掉落。
- 方向乘长度通过 `direction_length_to_point(d,l)` 明确计算 `p=d*l`；拒绝非单位方向、负或非有限长度。
- 极值候选最多4个（可配置0–4，原型值）；满额**明确拒绝**，不驱逐。移除候选后槽位恢复；对外ID按字符串稳定排序。
- `rebuild(core_radius, confirmed_ledger)`先在临时代理全建，所有输入成功后才替换状态；非法 attach/rebuild失败保留旧快照。

## 运行与范围

仓库根运行 `python3 -m unittest discover -s Docs/tests/ball_radial_proxy -v`。跨层测试在T49目录单独发现运行，见 [`GroundContact/SIMULATION_REPORT.md`](../GroundContact/SIMULATION_REPORT.md)。

本层不实现接触高度(T49)、Sweep(T54)、价值/吸附裁决、Renderer、掉落轨迹、真实mesh碰撞或PVP外壳。稀疏端点/半球近邻可能漏掉中段或相邻采样峰值；性能及跨平台逐位一致性未测。
