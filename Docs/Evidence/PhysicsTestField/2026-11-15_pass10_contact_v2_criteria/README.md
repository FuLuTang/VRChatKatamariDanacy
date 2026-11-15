# pass10 — v2 接触模型 + 新判据（flag 开，commit b528b39）

commit `b528b39`，PTF 指纹 `build=2026-11-15e cellRev=2026-11-15e`，`contactModelV2Enabled=true`。

- 判据重导日志：`[ptf] criteria: cells=9 checks=59 v2Rows=59 missingCells=0 missingArenas=0 driverFields=6 errors=0`。
  **`v2Rows=59` 是导入器缺陷的读数，不是数据**：`Docs/Design/BallMotor/physics_test_field.json` 只有 **11** 条判据带 `"v2"` 块，59 是判据总数。缺陷在 `PhysicsTestFieldCriteriaImporter.WriteChecks` 的 `bool hasOverride = check.v2 != null;`——Unity JsonUtility 会给每个 check 物化一个默认 `V2Entry`，所以 null 检查恒真。已改用内容判据 `HasV2Override`（`expect` / `metric` / `op` / `threshold` / `not_applicable` 任一存在）；修复后重导应打印 **`v2Rows=11`**。
- ⚠️ **本页的 v2 矩阵（0.30 = 58/1/0、0.60 = 58/1/0、1.10 = 57/2/0、2.20 = 54/5/0）是在"59 行全部被当成 v2 行"的缺陷状态下判出的，修复后必须重跑重判，不能当作结论引用。**
  被错标的是 9 行：C08 `on_step`；C01/C03/C05/C13 的 `climbed` 与 `ramp_entered`。修复后它们回到 v1 判据，实际改变判定输入的是 4 条 `ramp_entered`（limit 从烘死的 0.3 m 改为电机 step limit = 0.25·D）和 C08 `on_step`（`deck_gap_m` 重新推导，0.05 m 低于四档 limit，期望仍是 pass）；4 条 `climbed` 因为驱动对坡道格本来就不做 `deck_gap_m` 推导（`!cell.IsRampClimb()` 门槛，设计 §8.3.1），只是标签错了。重跑前先重导判据（当前入库场景还没有 `checkHasV2[]` 等数组），日志必须是 `v2Rows=11`。
- 该次运行 36 跑全部 `verdict=EXPECTED`，0 unexpected：0.30 = 58/1/0、0.60 = 58/1/0、1.10 = 57/2/0、2.20 = 54/5/0，与当时的期望逐档相同（缺陷状态下的结果，须按上一条重跑）。
- C05 `climbed` v2 四档全过；C10 `blocked_gap` 四档全过无 skip；C14 门内 lift 四档全过 —— 没有需要上报的红灯。
- 跑法：判据菜单 → `autoBeginAllOnStart=true` + `contactModelV2Enabled=true`（不存盘）→ Play → 退出，场景 `dirty=False`。
