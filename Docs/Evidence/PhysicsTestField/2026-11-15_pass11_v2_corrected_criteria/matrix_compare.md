# pass11 与 pass9 / pass10 的比较

Key = (cell, D)，比较字段 `checks/pass/unexpected/skipped/verdict`（忽略 `model=`、`elapsed=`）。

## v1：pass11 vs pass9

36 个 key 全部存在，**0 处差异**。

| 档 | pass | skip | unexpected | pass9 |
|---|---|---|---|---|
| 0.300 | 58 | 0 | 1 | 58/0/1 ✓ |
| 0.600 | 57 | 1 | 1 | 57/1/1 ✓ |
| 1.100 | 56 | 2 | 1 | 56/2/1 ✓ |
| 2.200 | 52 | 5 | 2 | 52/5/2 ✓ |

## v2：pass11 vs pass10

36 个 key 全部存在，**0 处差异**。

| 档 | pass | skip | unexpected | 期望 / pass10 |
|---|---|---|---|---|
| 0.300 | 58 | 1 | 0 | 58/1/0 ✓ |
| 0.600 | 58 | 1 | 0 | 58/1/0 ✓ |
| 1.100 | 57 | 2 | 0 | 57/2/0 ✓ |
| 2.200 | 54 | 5 | 0 | 54/5/0 ✓ |

**没有哪一格发生变化**：`ramp_entered` 的 0.25·D 限与 C08 `on_step` 的 `deck_gap` 派生，与 pass10 的判定结果逐格相同（pass10 的 `stepLimit` 本来就是 0.25·D）。
