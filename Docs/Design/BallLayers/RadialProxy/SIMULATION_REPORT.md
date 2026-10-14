# T50 RadialProxy 离线模拟报告

状态：Python host-only验证；C# RuntimeDraft非组件且未编译；性能、Unity、UdonSharp、Unity对象与网络均未验证。

## 研究 / 项目差异

公开材料没有原版源码或新版内部几何证据。US7402104B2是历史专利；官方产品/Q&A页不讲径向点算法；独立Ruby/Gosu 2D clone不是Namco/Bandai源码。来源与实际HTTP有限响应见 [`GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md`](../GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md)。实现只承接本项目 §3.2 的项目自定原型契约，不作原版复刻主张。

## 命令和结果

命令（仓库根）：

```text
python3 -m unittest discover -s Docs/tests/ball_radial_proxy -v
```

实际：**10 tests run, OK**。

独立集合重算oracle：选固定方向 `d[7]`，以 `pA=2d,pB=4d`附着两个稳定实例，读值4；移除B后2，移除A后核心基线1。该期望直接来自当前仍存端点集合的点积最大值，不依赖实现中缓存；测试也涵盖候选释放再分配、满额拒绝无驱逐、候选ID排序、32方向及候选上限、任意方向*长度端点 `(0,0,1)*2.5=(0,0,2.5)`、不同顺序完整重建逐项一致、非法 attach/rebuild失败原快照完全不变、世界到局部转换与主机+Y 90°探针。

## 留存限制

- 方向格/半球邻域是稀疏近似；物体中段外凸/方向间峰值可被漏掉。支持输出不等于精确外壳。
- 32方向与最多4候选未测设备性能；候选满额策略“拒绝”仅为草案。
- 输入key需要上游提供稳定身份和generation；本模块不确认吸附或账本。
- host quaternion采用标准Hamilton algebra，没在Unity按+X/+Y/+Z箭头对拍；可能需要有证据的坐标适配。
- C#的Unity Quaternion调用只留作RuntimeDraft；不代表Unity API/UdonSharp目标版本支持或行为已验证。
