# pass9/10 与基线的比较

## C) v1 回归（pass9）↔ pass7（commit b672f3b）

Key = (cell, D)。36 个 key 全部存在，且 `checks/pass/unexpected/skipped/verdict` 五列**逐格相同**：0 处差异。

| 档 | pass | skipped | unexpected | pass7 |
|---|---|---|---|---|
| 0.300 | 58 | 0 | 1 | 58 / 0 / 1 ✓ |
| 0.600 | 57 | 1 | 1 | 57 / 1 / 1 ✓ |
| 1.100 | 56 | 2 | 1 | 56 / 2 / 1 ✓ |
| 2.200 | 52 | 5 | 2 | 52 / 5 / 2 ✓ |

5 条已知红灯数值也相同：C05 `climbed`（deck_gap_m）+0.0887 / +0.1763 / +0.3234 / +0.6000，C13 @2.200 `seam_no_step`（grounded_frame_dy_m）+0.9499。
`[arena]` 前缀不变：`planes/boxes/ramps/supports/refused/kinds/obbs/obbRefused/obbOverflow/obbBake/doubleBake`。
新增字段：summary 行多了 `model=v1`（本批新加的读回口径），指纹 `build=2026-11-15e cellRev=2026-11-15e`（pass7 是 `2026-11-15d`）。

## D) v2 + 新判据（pass10）↔ 判据代理给的期望

36 跑全部 `verdict=EXPECTED`，**0 条 unexpected**。

| 档 | 实测 pass/skip/unexp | 期望 |
|---|---|---|
| 0.300 | 58 / 1 / 0 | 58 / 1 / 0 ✓ |
| 0.600 | 58 / 1 / 0 | 58 / 1 / 0 ✓ |
| 1.100 | 57 / 2 / 0 | 57 / 2 / 0 ✓ |
| 2.200 | 54 / 5 / 0 | 54 / 5 / 0 ✓ |

- C05 `climbed` 在 v2 四档全过（v1 是四档全红）——v2 的圆角接触按期望修好了 45° 接缝。
- C10 `blocked_gap` 四档全过，0 skip（v1 在 0.60/1.10/2.20 各 1 skip）。
- C14 门内 lift 四档 7/7 全过，**没有红灯**。
- C06 四档各 1 skip（50° 坡按几何规则 `notApplicable`，与 v1 一致）。
