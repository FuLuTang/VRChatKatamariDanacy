# PhysicsTestField v1 回归矩阵（第 J 轮，commit 44e0cfe，指纹 2026-11-15d）

`contactModelV2Enabled` 保持 **false**。36 跑与 pass3（7f5cd7b）逐行逐列完全一致。

| run | D | cell | checks | pass | skipped | unexpected | verdict | ramp |
|---|---|---|---|---|---|---|---|---|
| 1 | 0.300 | C14 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 2 | 0.300 | C09 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 3 | 0.300 | C08 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 4 | 0.300 | C10 | 6 | 6 | 0 | 0 | EXPECTED | 0 |
| 5 | 0.300 | C01 | 6 | 6 | 0 | 0 | EXPECTED | 1 |
| 6 | 0.300 | C03 | 7 | 7 | 0 | 0 | EXPECTED | 1 |
| 7 | 0.300 | C05 | 6 | 5 | 0 | 1 | UNEXPECTED | 1 |
| 8 | 0.300 | C06 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 9 | 0.300 | C13 | 10 | 10 | 0 | 0 | EXPECTED | 1 |
| 10 | 0.600 | C14 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 11 | 0.600 | C09 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 12 | 0.600 | C08 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 13 | 0.600 | C10 | 6 | 5 | 1 | 0 | EXPECTED | 0 |
| 14 | 0.600 | C01 | 6 | 6 | 0 | 0 | EXPECTED | 1 |
| 15 | 0.600 | C03 | 7 | 7 | 0 | 0 | EXPECTED | 1 |
| 16 | 0.600 | C05 | 6 | 5 | 0 | 1 | UNEXPECTED | 1 |
| 17 | 0.600 | C06 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 18 | 0.600 | C13 | 10 | 10 | 0 | 0 | EXPECTED | 1 |
| 19 | 1.100 | C14 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 20 | 1.100 | C09 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 21 | 1.100 | C08 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 22 | 1.100 | C10 | 6 | 5 | 1 | 0 | EXPECTED | 0 |
| 23 | 1.100 | C01 | 6 | 6 | 0 | 0 | EXPECTED | 1 |
| 24 | 1.100 | C03 | 7 | 7 | 0 | 0 | EXPECTED | 1 |
| 25 | 1.100 | C05 | 6 | 4 | 1 | 1 | UNEXPECTED | 1 |
| 26 | 1.100 | C06 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 27 | 1.100 | C13 | 10 | 10 | 0 | 0 | EXPECTED | 1 |
| 28 | 2.200 | C14 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 29 | 2.200 | C09 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 30 | 2.200 | C08 | 5 | 5 | 0 | 0 | EXPECTED | 0 |
| 31 | 2.200 | C10 | 6 | 5 | 1 | 0 | EXPECTED | 0 |
| 32 | 2.200 | C01 | 6 | 6 | 0 | 0 | EXPECTED | 1 |
| 33 | 2.200 | C03 | 7 | 7 | 0 | 0 | EXPECTED | 1 |
| 34 | 2.200 | C05 | 6 | 3 | 2 | 1 | UNEXPECTED | 1 |
| 35 | 2.200 | C06 | 7 | 7 | 0 | 0 | EXPECTED | 0 |
| 36 | 2.200 | C13 | 10 | 7 | 2 | 1 | UNEXPECTED | 1 |

## 每档合计（pass / skipped / unexpected）

| D | pass | skipped | unexpected | pass3 期望 | 一致 |
|---|---|---|---|---|---|
| 0.300 | 58 | 0 | 1 | 58/0/1 | 是 |
| 0.600 | 57 | 1 | 1 | 57/1/1 | 是 |
| 1.100 | 56 | 2 | 1 | 56/2/1 | 是 |
| 2.200 | 52 | 5 | 2 | 52/5/2 | 是 |

## 非预期失败（5 条已知红灯，值与 pass3 相同）

| cell | D | check | metric | value |
|---|---|---|---|---|
| C05 | 0.300 | climbed | deck_gap_m | +0.0887 |
| C05 | 0.600 | climbed | deck_gap_m | +0.1763 |
| C05 | 1.100 | climbed | deck_gap_m | +0.3234 |
| C05 | 2.200 | climbed | deck_gap_m | +0.6000 |
| C13 | 2.200 | seam_no_step | grounded_frame_dy_m | +0.9499 |

## notApplicable（3 条，与 pass3 相同）

| cell | D | checks | reason |
|---|---|---|---|
| C05 | 1.100 | ramp_entered | scene_has_no_scenario_at_this_diameter |
| C05 | 2.200 | ramp_support,ramp_entered | scene_has_no_scenario_at_this_diameter |
| C13 | 2.200 | landing_gap,landed_on_ground | scene_has_no_scenario_at_this_diameter |

## derived（step_limit 与 pass3 相同）

```
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.074934 expect=fail
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.149735 expect=pass
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.275248 expect=pass
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.549948 expect=pass
```
