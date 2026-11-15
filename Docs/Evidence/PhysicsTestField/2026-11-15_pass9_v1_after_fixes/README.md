# pass9 — v1 回归（flag 关，commit b528b39）

commit `b528b39`，PTF 指纹 `build=2026-11-15e cellRev=2026-11-15e`，`contactModelV2Enabled=false`，`startDiameterWorldM=0`。

- 36 跑与 pass7 **逐格逐列完全一致**（0 差异，见 `matrix_compare.md`）：0.30 = 58/0/1、0.60 = 57/1/1、1.10 = 56/2/1、2.20 = 52/5/2。
- 0 次读取超时，`matrix.tsv` 无补跑行、无缺行。
- `[arena]` 前缀未变；5 条已知红灯数值与 pass3/4/5/7 相同（C05 climbed +0.0887/+0.1763/+0.3234/+0.6000，C13 2.20 seam_no_step +0.9499）。
- 结论：本轮"写者自分配缓冲 / startDiameterWorldM / 拾取场 / 导入器 / 大厅"的改动**没有动 v1 行为**。
- 跑法：`autoBeginAllOnStart=true`（SerializedObject 改，不存盘）→ Play → 36 条 `[ptf] summary` → 退出，场景 `dirty=False`。
- `console.log` 是退出 Play 前抓的 600 行，含 36 条 summary。
