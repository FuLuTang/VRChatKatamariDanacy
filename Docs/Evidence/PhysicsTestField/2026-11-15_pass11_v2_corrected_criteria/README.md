# pass11 — v2 + 修正后的判据推导（flag 开，commit d3cb411）

commit `d3cb411`，判据重导 `v2Rows=11`（pass10 是 59，本轮为覆盖检测修正后的 11 条 v2 override），`contactModelV2Enabled=true`（设在**电机**上，驱动读 `motor.IsContactModelV2()`）。

- 36 跑；四档 **58/1/0、58/1/0、57/2/0、54/5/0**，`unexpected=0`，`[ptf] field … skipped=9 elapsed=230s`。
- 与 pass10 逐格逐列 **0 差异**：修正后的推导（`ramp_entered` 用 0.25·D 限、C08 `on_step` 由 `deck_gap` 派生）**没有改变任何一格的通过/跳过判定**，只是把阈值来源与 pass2 的 A/B/C 结论对齐。
- v1 的 5 条红灯在 v2 下仍全绿：C05 `climbed` 四档通过、C10 `blocked_gap` 四档通过无 skip、C14 门内 lift 7/7。
- 跑法同 pass11_v1，仅把 `contactModelV2Enabled` 置 true。
