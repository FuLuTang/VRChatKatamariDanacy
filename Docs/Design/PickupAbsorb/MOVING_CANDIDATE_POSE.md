# 运动道具的候选位姿

## 问题与修复

`PickupSpatialGrid` 只负责缩小搜索范围；`propMobile` 的道具走线性列表，每次从当前 Transform 取位置。找到运动道具不等于电机里已有的候选也跟着运动。

电机的 `SubmitCandidateWithVolumeKMm3` 按 itemId 幂等：已有候选直接返回，不改原来提交的位置和旋转。宿主持续选中同一件时也不会撤回它。因此，即使空间索引和视觉位置都正确，候选接触盒仍可能停在首次入队的位置；队满时，原来的提交循环甚至不会再走到这件道具。

现行处理是：宿主每次扫描重新排名后，在队满判断之前，对仍被选中的已排队道具比较位姿。相对上一次**实际写入电机**的位姿，中心或任一自身尺寸盒角点位移超过 1 mm 时，调用电机的 `RefreshCandidatePoseUnlessAccepted`。只更新位置和旋转，不撤回再提交。

1 mm 沿用空间缓存的 `DriftToleranceM`，用来抑制亚毫米抖动，不是额外接触余量。旋转误差换成盒角点的米制位移，不用任意角度阈值；细长件的小转角也能触发刷新。小于阈值的运动不更新基准，所以连续小步最终超过阈值仍会刷新。

电机入口只写 `pendingPositions` 和 `pendingRotations`；不改接受状态、提交时间、接触窗口、收益或账本。已接受未提交的候选拒绝刷新，保持作出接受判定时的快照。静止件不产生刷新跨调用。无论道具是否预先声明 mobile，都比较选中候选，覆盖漏声明与只转不移的件。

## 成本与限制

当前电机队列 4 槽，每次扫描最多增加 4 次跨行为调用。默认扫描间隔 0.05 s，持续运动且队满时最多约 80 次/秒；一个渲染帧最多扫描一次，不补跑遗漏扫描。阈值比较最多检查 4 个候选、各 8 个盒角点，不扫描整张道具表。新增快照存储为每件一个 Vector3 和 Quaternion，不在扫描时分配。

这是扫描频率的同步，不是逐物理步追踪或连续碰撞：扫描后的运动仍存在最多一个扫描间隔的时间差，未超过阈值的位姿误差保留在 1 mm 内。快速穿过球的运动件仍需动态实测，不能由本修复宣称全部可达。

## 回归

现有 `PickupCandidateQueueTests` 增加：米制平移/角点阈值、满队列移动后在新位置接触并提交、已接受件快照不动、小位移累计越阈值。纯阈值与接触判定测试可由本地无引擎 runner 执行；宿主/电机组合测试需要 Unity。电机入口自己的幂等与状态保留测试由电机模块维护。

宿主/电机组合测试必须像关卡宿主（`LevelArenaHost` / `LevelSwitchHost`）一样先调 `motor.SetAllowedKinds`：电机 `Start` 把种类白名单设为空，空名单是「拒绝所有种类」，且这个判定发生在接触距离之前（`PickupAbsorbMath.EvaluateAbsorb` 先查 kind，再算 `TryComputeContact`）。2026-11-18 的 `AFullQueueRefreshesAMoverAndAbsorbsAtItsNewPosition` 实测失败正是此原因，不是位姿没刷新：电机 reject 诊断里的 `d` 按 `pendingPositions` 计算（`KatamariBallMotor.LogRejectDiagnosticsOnce`），日志 `d=0` 说明排在第一的候选已经是球心处的新位姿；四个候选在接触判定前全被拒成 kind-not-allowed，`rejectDiagnosticsLimit=3` 只打印前三槽的 d=0/0.8/1.0。修复是给测试夹具接上 `SetAllowedKinds(new[]{"stone"},1)` 并读回 `GetAllowedKindCount`，不新增第二条位姿路径。

候选接受判定只有一份位姿输入：`pendingPositions` / `pendingRotations`。刷新写的就是这两列（`RefreshCandidatePoseUnlessAccepted`），判定读的也是这两列（`EvaluatePendingCandidates` → `PickupAbsorbStepMath` → `PickupAbsorbMath.TryComputeContact`）；OBB、径向代理、接触窗口 `windowPreviousCount` 和空间网格都不参与候选接受，不存在第二份位姿快照。

Unity 重跑：`AFullQueueRefreshesAMoverAndAbsorbsAtItsNewPosition`（预期 `positions[0]` 为球心、`accepted[0]=true`、提交后该 id 离开队列且吸收事件计数 >0）、`AnAcceptedMoverKeepsItsFrozenPoseAndSubmillimetreMovesAccumulate`（预期已接受后 0.2 m 移动不刷新）、`TheContactGateJudgesTheSnapshotPoseButStillNeedsAnAllowedKind`（本地可执行）。

2026-11-18 的蟹座现场导出复核范围：177 个 MotionHost 行与 120 个 Animator 的并集仍为 177（120 个全部在运动表中），不是 297。177 行全部有 PickupField 行；按当前运动类和继承链应标 mobile 的 138 行，现场恰为 138，0 行声明失配。离线尺寸门检查只证明达到门槛且当前盒接触时的可提交性，不证明玩家实际到得了、骨骼每个姿态都贴合玩法盒，或原作上限内能达到所有门槛。原始逐件导出与复核 CSV 是本地未许可参考数据，不进入仓库。
