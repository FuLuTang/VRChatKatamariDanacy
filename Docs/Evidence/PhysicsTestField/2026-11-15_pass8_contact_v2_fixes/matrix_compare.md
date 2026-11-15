# 接触模型 v2 修复后重跑：pass8 vs pass6（首跑）

commit `b672f3b`，PTF 指纹 `build=2026-11-15d`，同一套 36 跑（9 格 × 4 球径），**判据一行未改**。
pass6 = v2 首跑（`18f9f3e`）→ 本目录上一级 `2026-11-15_pass6_contact_v2_first_run/`；pass8 = 本轮 v2（`b672f3b`）。
v2 开关在 Play 内写入 Udon 堆并回读 `IsContactModelV2=True`；退出后从磁盘重开场景，`dirty=False`、开关回 `false`，场景未保存。

## 合计

| D | pass6 pass/skip/unexp | pass8 pass/skip/unexp |
|---|---|---|
| 0.300 | 47 / 0 / 12 | **49 / 0 / 10** |
| 0.600 | 49 / 1 / 9 | **51 / 1 / 7** |
| 1.100 | 47 / 2 / 10 | **50 / 2 / 7** |
| 2.200 | 44 / 5 / 10 | **47 / 5 / 7** |

## 你点名的五项

| 项 | 期望 | 实测（pass8） | 结论 |
|---|---|---|---|
| C08 `rest_gap`（四档） | ≈ +0.000x | 全部 **+0.0000 / +0.0021 / +0.0045**（`abs<=0.0050` 全 PASS），四档 4/1 → **5/0** | ✅ 修好 |
| C09 `rest_gap`（四档） | ≈ +0.000x | +0.0021（0.30）/ +0.0045（2.20）/ 另两档 PASS；3/2 → **4/1**、其余 4/1 → **5/0** | ✅ 修好（0.30 仍剩 1 红是 `on_step`，见下） |
| C10 `rest_gap`（1.10、2.20） | ≈ +0.000x | 4/1 → **5/0**；0.30 剩 2 红（`blocked_gap` `−4.3338`、`on_step` UNEXPECTED_PASS），0.60 仍是 5/0/1 | ✅ 修好（剩的是判据语义） |
| C14 `lift_over_nominal`（四档） | ≤ 0.01 | **+0.0189 / +0.0721 / +0.1346 / +0.2133**（比 pass6 的 +0.0198/+0.0729/+0.1554/+0.2547 均变小） | ❌ **未达标**，四档仍红 |
| C05 四档 `climbed` | 仍绿 | 四档仍全部 PASS（5/1、5/1、4/1/1、3/1/2，与 pass6 一致；唯一红是 `ramp_registered`） | ✅ 保持 |
| C13 2.20 `seam_no_step` | 仍 +0.02 级 | **+0.0184**（pass6 +0.0231）PASS | ✅ 保持 |

## 仍红的（判据是 v1 语义，如实记录）

| 检查 | 次数 | 位置 | 值 |
|---|---|---|---|
| `ramp_registered` | 16 | C01/C03/C05/C13 全档 | `ramp_count=+0.0000` |
| `no_climb` | 4 | C06 全档 | +0.3068 / +0.5999 / +0.6000 |
| `stopped_by_wall` | 4 | C06 全档 | 见 matrix.tsv |
| `lift_bounded` | 4 | C14 全档 | +0.0189 / +0.0721 / +0.1346 / +0.2133 |
| `on_step` | 2 | C09 0.30、C10 0.30 | `deck_gap=+0.0000`（`expect=fail` → UNEXPECTED_PASS） |
| `blocked_gap` | 1 | C10 0.30 | −4.3338 |

合计 31 条（pass6 是 41 条）；`rest_gap` 10 条已全部消失。

## 运行期诊断（Play 内，读前先 `UdonSharpEditorUtility.CopyUdonToProxy`）

| 时刻 | `[arena]` | `GetStaticObbCount` | `GetNearObbCount` | Contact / Substep | Recovery | SolveFail | Incomplete |
|---|---|---|---|---|---|---|---|
| C01 0.30 跑动中 | `obbs=2/2`、`obbRefused=0`、`doubleBake=1/0` | **2** | 1 | 1 / 1 | 0 / 0 | 0 | False |
| C13 2.20 跑动中 | `obbs=2/2` | **2** | 1 | 0 / 1 | 0 / 0 | 0 | False |

`GetStaticObbCount` 与 `[arena] obbs=` **一致**（2 对 2）。上一轮"读 0"是我的测量问题：不先 `CopyUdonToProxy` 就读 C# 代理，拿到的是初始值而非 Udon 堆里的真实状态。

## 异常

- 无 `[ContactV2]`、无 `[BallMotor] recovery hold`、无 `LogError`、无异常、无 NaN（36 跑全程）。
- 无掉穿、无卡死；`WasContactIncomplete=False` 全程。

# 接触模型 v2 修复后重跑：pass8 vs pass6（首跑）

commit `b672f3b`，PTF 指纹 `build=2026-11-15d`，同一套 36 跑，判据一行未改。
pass6 = v2 首跑（`18f9f3e`，flag 开），pass8 = 本轮 v2（`b672f3b`，flag 开）。

## 合计

| D | pass6 pass/skip/unexp | pass8 pass/skip/unexp |
|---|---|---|
| 0.3 | 47 / 0 / 12 | **49 / 0 / 10** |
| 0.6 | 49 / 1 / 9 | **51 / 1 / 7** |
| 1.1 | 47 / 2 / 10 | **50 / 2 / 7** |
| 2.2 | 44 / 5 / 10 | **47 / 5 / 7** |

## 逐格（只列有变化的检查格）

### C14 D=0.3  （pass6 6/1/0 → pass8 6/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| lift_bounded | lift_over_nominal_m | +0.0198 (FAIL) | +0.0189 (FAIL) |
| no_overflow | overflow_delta | +0.0000 (PASS) | - |
| size_accuracy | diameter_error_quanta | +0.0091 (PASS) | - |

### C14 D=0.6  （pass6 6/1/0 → pass8 6/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| lift_bounded | lift_over_nominal_m | +0.0729 (FAIL) | +0.0721 (FAIL) |
| no_speed_loss | speed_ratio | +1.6134 (PASS) | +1.5860 (PASS) |

### C14 D=1.1  （pass6 6/1/0 → pass8 6/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| lift_bounded | lift_over_nominal_m | +0.1554 (FAIL) | +0.1346 (FAIL) |
| no_speed_loss | speed_ratio | +1.4769 (PASS) | +1.5024 (PASS) |

### C14 D=2.2  （pass6 6/1/0 → pass8 6/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| lift_bounded | lift_over_nominal_m | +0.2547 (FAIL) | +0.2133 (FAIL) |

### C09 D=0.3  （pass6 3/2/0 → pass8 4/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| rest_gap | support_gap_m | -0.1499 (FAIL) | +0.0021 (PASS) |

### C09 D=0.6  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| rest_gap | support_gap_m | -0.2995 (FAIL) | - |

### C09 D=1.1  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| rest_gap | support_gap_m | -0.5505 (FAIL) | - |

### C09 D=2.2  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| rest_gap | support_gap_m | -1.0999 (FAIL) | +0.0045 (PASS) |

### C08 D=0.3  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|

### C08 D=0.6  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|

### C08 D=1.1  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|

### C08 D=2.2  （pass6 4/1/0 → pass8 5/0/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|

### C10 D=1.1  （pass6 4/1/1 → pass8 5/0/1）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|

### C10 D=2.2  （pass6 4/1/1 → pass8 5/0/1）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|

### C01 D=0.3  （pass6 5/1/0 → pass8 5/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| no_overflow | overflow_delta | - | +0.0000 (PASS) |
| size_accuracy | diameter_error_quanta | - | +0.0091 (PASS) |

### C05 D=0.3  （pass6 5/1/0 → pass8 5/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +6.2726 (PASS) | +6.3523 (PASS) |

### C06 D=0.3  （pass6 5/2/0 → pass8 5/2/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| stopped_by_wall | blocked_gap_m | -0.0809 (FAIL) | -0.0817 (FAIL) |

### C06 D=0.6  （pass6 5/2/0 → pass8 5/2/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| stopped_by_wall | blocked_gap_m | -1.0264 (FAIL) | -0.9843 (FAIL) |

### C06 D=1.1  （pass6 5/2/0 → pass8 5/2/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| stopped_by_wall | blocked_gap_m | -2.1663 (FAIL) | -2.1042 (FAIL) |

### C06 D=2.2  （pass6 5/2/0 → pass8 5/2/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| stopped_by_wall | blocked_gap_m | -2.8897 (FAIL) | -2.9522 (FAIL) |

### C13 D=0.6  （pass6 9/1/0 → pass8 9/1/0）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| fall_frames | fall_frames | +30.0000 (PASS) | - |

### C13 D=2.2  （pass6 7/1/2 → pass8 7/1/2）

| 检查 | 指标 | pass6 | pass8 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +7.9001 (PASS) | +7.9167 (PASS) |
| seam_no_step | grounded_frame_dy_m | +0.0231 (PASS) | +0.0184 (PASS) |
