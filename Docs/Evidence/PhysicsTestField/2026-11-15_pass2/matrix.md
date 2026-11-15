# PhysicsTestField 判据矩阵 pass2（2026-11-15c）

36 次运行：每档按 C14 C09 C08 C10 C01 C03 C05 C06 C13。`ramp=1` 表示该跑的 run 行带 `ramps=1/rampCell=1`。
`cap`（traj 里，正比直径）实测：0.30 档 0.0525、0.60 档 0.1048、1.10 档 0.1927、2.20 档 0.3000，全部符合预期。
`size_accuracy` 实测：0.30 **+0.0091**、0.60 **+0.1454**、1.10 **+0.4614**、2.20 **+0.3854**（Unity 控制台会合并完全相同的行，所以每档只出现一次；每格都落在同一列）。

| run | D | cell | checks | pass | unexpected | skipped | verdict | ramp |
|---|---|---|---|---|---|---|---|---|

## 每档合计

| D | pass | unexpected | skipped | 合计 |
|---|---|---|---|---|

## 非预期失败明细（ALERT）

| cell | D | check | metric | value | verdict |
|---|---|---|---|---|---|
| C05 | 0.300 | climbed | deck_gap_m | +0.0887 | UNEXPECTED_FAIL |
| C05 | 0.600 | ramp_entered | ramp_entry_progress_m | +0.2996 | UNEXPECTED_FAIL |
| C05 | 0.600 | climbed | deck_gap_m | +0.1763 | UNEXPECTED_FAIL |
| C05 | 1.100 | ramp_entered | ramp_entry_progress_m | +0.0485 | UNEXPECTED_FAIL |
| C05 | 1.100 | climbed | deck_gap_m | +0.3234 | UNEXPECTED_FAIL |
| C01 | 2.200 | climbed | deck_gap_m | +0.0134 | UNEXPECTED_FAIL |
| C05 | 2.200 | ramp_support | ramp_support_seen | +0.0000 | UNEXPECTED_FAIL |
| C05 | 2.200 | ramp_entered | ramp_entry_progress_m | -0.5009 | UNEXPECTED_FAIL |
| C05 | 2.200 | climbed | deck_gap_m | +0.6000 | UNEXPECTED_FAIL |
| C13 | 2.200 | ramp_entered | ramp_entry_progress_m | +0.1005 | UNEXPECTED_FAIL |
| C13 | 2.200 | seam_no_step | grounded_frame_dy_m | +0.9499 | UNEXPECTED_FAIL |
| C13 | 2.200 | landing_gap | landing_gap_m | +0.0419 | UNEXPECTED_FAIL |
| C13 | 2.200 | landed_on_ground | support_kind | +2.0000 | UNEXPECTED_FAIL |

## 坡道格轨迹

见 `traj_ramps.md`（C01/C03/C05/C06/C13 × 四档，含球心/球底 y、sup、lift、stuck 逐点采样）。
