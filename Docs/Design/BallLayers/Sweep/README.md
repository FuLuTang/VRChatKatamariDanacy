# T54 球层：墙面与环境扫掠

> **DRAFT / host-only reference.** `sweep.py` 是仅标准库数值参考；C# 移植 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/Sweep/SweepMath.cs` 是普通静态数学类，不是MonoBehaviour，未在Unity/UdonSharp编译；接口仍未冻结。

## 项目/公开证据边界

- 原版源码没有取得。US7402104B2只表示历史专利公开，不证明新版沿用；Bandai官方产品页/FAQ没有公开本套TOI/盒碰撞实现；Ruby/Gosu公开代码是独立2D实验。访问结果见 [`GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md`](../GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md)。
- 本项目 `Docs/02_TECHNICAL_CONTRACTS.md` §3.3 先用统一球形扫掠；本层的平面/扩张AABB只是项目离线近似，不是原作源码或完整PhysX。

## 行为和语义

- 平面允许侧为 `dot(n,x)>=d`；原始n/d同时规范化。盒子以球半径扩张AABB后做线段测试；角落扩大为方形，是保守角近似，可能早报接触/假阳性，**不是精确球盒**。
- 每次调用先检查初始穿透，即使位移为零。盒内恢复深度取扩张AABB最近自由面距离，再加分离epsilon；零深度平面/盒边界只对内向位移产生碰撞。
- TOI最终映射为原始步位移段 `[0,1]` 时间；同一TOI按障碍输入索引稳定排序。命中后分离，并对剩余位移做确定性法线投影，多接触顺序复查。
- `blocked=True,stuck=False`表示接触正常消耗了剩余请求运动（例如直撞单墙后停下）；`stuck=True`只用于剩余求解/几何验证未完成、无进展或预算耗尽。接触本身、残余为零不再单独等于stuck。预算耗尽时保留未处理剩余位移供调用方判断。

## 运行 / 非目标

仓库根执行 `python3 -m unittest discover -s Docs/tests/ball_sweep -v`；跨层综合序列在 [`GroundContact/SIMULATION_REPORT.md`](../GroundContact/SIMULATION_REPORT.md)。本层不积分运动、不做T49凹凸支持、不接T50代理、不算摩擦反弹、任意旋转凸体、动态网络碰撞、真实球-盒圆角距离或Unity Physics。

未在Unity/Udon、VR、网络或真实物理场景验证。对称凹角/极薄通道/大量冲突约束、最大接触次数、尺度依赖epsilon都需后续测试。

## Unity 实现

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/Sweep/SweepMath.cs`：`public static class SweepMath`，唯一入口 `TrySweepSphere`，成功返回 `true`；输入或中间量非有限时返回 `false`（对应 Python 的 `ValueError`），此时所有 `out` 为零、不写接触。

- 单位/坐标：长度与位置均为米，Unity 世界坐标（+Y 向上）；本层没有角度。
- 无 struct：平面为调用方并行数组 `planeNormals[]`/`planeOffsets[]`，盒为 `boxMinima[]`/`boxMaxima[]`。障碍顺序固定为“先全部平面，后全部盒”，盒 j 的 `obstacleIndex` 是 `planeNormals.Length + j`；同一 TOI 的接触按该顺序处理。
- 无分配：接触写进调用方 6 个并行数组（time/center/point/normal/obstacleIndex/penetrationDepth），返回写入个数；数组最短者满后只截断记录、不截断求解。
- 语义保持：扩张 AABB 的保守盒角、初始嵌入“真实面距 + separationEpsilon”推出、`blocked`（接触消耗请求运动）与 `stuck`（无进展/预算耗尽/最终复核仍嵌入）都与 `sweep.py` 一致。同一时刻的接触法线不做去重列表；重复法线的投影是幂等的。盒进入面的法线沿用 `sweep.py` 的 slab 符号规则：负方向进入时报告的是内侧面法线（下一步嵌入分支仍会把它分离出去），为逐值一致未在本层单独修正。
- epsilon 为浮点占位：内部阈值 `1e-6`（Python `1e-10`），同时 TOI 窗口 `1e-6`（Python `1e-8`）。
- 测试：`Tests/EditMode/BallLayers/SweepTests.cs` 对应 `Docs/tests/ball_sweep/test_sweep.py` 全部 14 个用例，另加一个“拒绝调用不写任何输出”的用例；因 float 精度，位置容差放宽到 `1e-4` m、测试固定 `separationEpsilon=1e-5` m。未在 Unity 编译，未接 Unity Physics。
