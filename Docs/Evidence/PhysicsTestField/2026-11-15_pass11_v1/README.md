# pass11 — v1 回归（flag 关，commit d3cb411）

commit `d3cb411`，判据重导 `[ptf] criteria: cells=9 checks=59 v2Rows=11 missingCells=0 missingArenas=0 driverFields=6 errors=0`，`contactModelV2Enabled=false`，`startDiameterWorldM=0`。

- 36 跑；四档 **58/0/1、57/1/1、56/2/1、52/5/2**，与 pass9 **逐格逐列 0 差异**（矩阵 36 行字段 `checks/pass/unexpected/skipped/verdict` 全同）。
- 5 条已知红灯照旧：C05 `climbed` 四档 + C13 @2.20 `seam_no_step`。
- `[ptf] field cells=9 runs=36 model=v1 …` 头行在 `console.log` 末尾。
- 跑法：`autoBeginAllOnStart=true`（SerializedObject，不存盘）→ Play → 抓 summary → 退出；场景未保存。
