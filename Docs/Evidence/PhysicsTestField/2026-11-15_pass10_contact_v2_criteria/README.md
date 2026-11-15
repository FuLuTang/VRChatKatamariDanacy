# pass10 — v2 接触模型 + 新判据（flag 开，commit b528b39）

commit `b528b39`，PTF 指纹 `build=2026-11-15e cellRev=2026-11-15e`，`contactModelV2Enabled=true`。

- 判据重导：`[ptf] criteria: cells=9 checks=59 v2Rows=59 missingCells=0 missingArenas=0 driverFields=6 errors=0`。
  **注意：`v2Rows=59`，不是 Lead 预期的 11**（仓库里的 `Docs/Design/BallMotor/physics_test_field.json` 现在 59 条判据全部带 v2 override；EditMode 的 `CriteriaModelOverrideTests` 里硬编码的表还是 11 条，因此那条测试失败，见报告）。
- 36 跑全部 `verdict=EXPECTED`，0 unexpected：0.30 = 58/1/0、0.60 = 58/1/0、1.10 = 57/2/0、2.20 = 54/5/0，与期望逐档相同。
- C05 `climbed` v2 四档全过；C10 `blocked_gap` 四档全过无 skip；C14 门内 lift 四档全过 —— 没有需要上报的红灯。
- 跑法：判据菜单 → `autoBeginAllOnStart=true` + `contactModelV2Enabled=true`（不存盘）→ Play → 退出，场景 `dirty=False`。
