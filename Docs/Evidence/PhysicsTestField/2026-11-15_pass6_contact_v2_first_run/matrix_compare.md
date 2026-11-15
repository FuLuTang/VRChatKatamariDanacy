# 接触模型 v2 首跑：v1 vs v2 逐格对比

commit `18f9f3e`，PTF 指纹 `build=2026-11-15d`，同一套 36 跑（9 格 × 4 球径），**判据一行未改**。
v1 = pass5（`contactModelV2Enabled=false`，证据 `2026-11-15_pass5_v1_after_obb/`），v2 = pass6（`true`，本目录）。
v2 的开关在 Play 内用 `UdonSharpEditorUtility.CopyProxyToUdon` 写入 Udon 堆并回读 `IsContactModelV2=True`；退出 Play 后从磁盘重开，`dirty=False`、开关回 false，场景未保存。

## 合计

| D | v1 pass/skip/unexpected | v2 pass/skip/unexpected |
|---|---|---|
| 0.300 | 58 / 0 / 1 | **47 / 0 / 12** |
| 0.600 | 57 / 1 / 1 | **49 / 1 / 9** |
| 1.100 | 56 / 2 / 1 | **47 / 2 / 10** |
| 2.200 | 52 / 5 / 2 | **44 / 5 / 10** |

## v2 的 41 条非预期，按检查归类

| 检查 | 次数 | 格 | 数值（原样） | 判断 |
|---|---|---|---|---|
| `ramp_registered` | 16 | C01/C03/C05/C13 全档 | `ramp_count=+0.0000` | **判据语义**：v2 把坡道烘焙成 OBB（`[arena] obbBake=obb`），v1 的坡道计数归零。坡道实际可用：`ramp_support_seen=1`、`ramp_entry_progress` 7.3–8.3 m 全 PASS |
| `rest_gap` | 10 | C08 全档、C09 全档、C10 0.30/1.10/2.20 | `support_gap_m` = −0.1499 / −0.2995 / −0.5505 / −1.0999 | 恰为 **−半径**：球心落在支撑面高度上。平地上静置实测 `center.y=0.15`、`r=0.1499`、`surface=0` → +0.0001，正常；只在"爬上台阶/箱顶之后"出现 → 疑似 v2 落在 OBB 顶面时少补一个半径 |
| `lift_bounded` | 4 | C14 全档 | `lift_over_nominal_m` +0.0198 / +0.0729 / +0.1554 / +0.2547 | v1 为 0；v2 有 1.9–15.5 cm 抬升 |
| `no_climb` | 4 | C06 全档 | `max_bottom_rise_m` +0.3068 / +0.5999 / +0.6000 / (2.20 见下表) | **与预期一致**：v2 对 50° 板变成慢爬 |
| `stopped_by_wall` | 4 | C06 全档 | `blocked_gap_m` −0.0809 / −1.0264 / −2.1663 / −2.8897 | 同上：v2 越过挡板继续走 |
| `on_step` | 2 | C09 0.30、C10 0.30 | `deck_gap_m=+0.0000`（`expect=fail`） | **UNEXPECTED_PASS**：最小球径也能上 0.075 / 0.10 台阶（v1 上不去） |
| `blocked_gap` | 1 | C10 0.30 | `blocked_gap_m=−4.3338` | 球穿过 0.10 挡板 4.3 m |

## 预期内的改善（都发生了）

| 项 | v1 | v2 |
|---|---|---|
| C05 45° 接缝 `climbed`（四档全红） | +0.0887 / +0.1763 / +0.3234 / +0.6000 | **四档全绿** |
| C13 2.20 `seam_no_step`（0.95 m 单帧跳变） | +0.9499（红） | **+0.0231（绿）** |
| C01 / C03 坡道进入 | +8.2491 / +5.9103 m | +8.2501 / +5.9169 m（一致） |
| C14 `size_accuracy` | +0.0091 / +0.1454 / +0.4614 / +0.3854 | **完全相同** |
| C05/C13 的 `skipped` | 0 / 2 | 0 / 2（一致） |

## 异常与非预期稳定性

- **没有** NaN、异常、`LogError`、`[ContactV2]` 行、`[BallMotor] recovery hold` —— 36 跑全干净。
- 球没有掉穿地面：所有 traj 的 `p.y ≥ sup`，最低点仍在地面之上；也没有卡死（`stuck` 全 0）。
- Play 内静止诊断：`GetContactCount=1`、`GetStaticObbCount=0`、`GetNearObbCount=0`、`GetSubstepCount=1`、`WasContactIncomplete=False`、`GetContactSolveFailureCount=0`、`GetPropObbsCapacity=12`、`IsContactModelV2=True`。
- `[arena]`（v2）逐行：`obbs=1/1`（C01/C03 等）～`obbs=2/2`（C13 2.20）、`obbRefused=0`、`obbOverflow=0`、`obbBake=obb`、`doubleBake=1/0` 或 `0/0`。
- ⚠ 一处待你确认：`[arena]` 报 `obbs=1/1`、`2/2` 时 motor 的 `GetStaticObbCount` 仍读 **0**（静止在原点时读取）。要么该 getter 只在步进中填，要么表没递到 motor。

# 接触模型 v2 首跑：逐格对比 v1 vs v2（commit 18f9f3e，指纹 2026-11-15d）

同一套 36 跑（9 格 × 4 球径），判据一行未改。v1 = pass5（flag 关），v2 = pass6（flag 开）。
表中每个指标写成 `v1 值(判定) → v2 值(判定)`；`-` 表示该跑这一项没被记录（控制台缓冲裁剪），不是未执行。

## C14

### C14 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| crossed | progress_m | +7.3491 (PASS) | +7.3501 (PASS) |
| lift_bounded | lift_over_nominal_m | 0.0000 (PASS) | +0.0198 (FAIL) |
| no_overflow | overflow_delta | +0.0000 (PASS) | +0.0000 (PASS) |
| no_saturation | saturated_delta | +0.0000 (PASS) | +0.0000 (PASS) |
| no_speed_loss | speed_ratio | +1.6484 (PASS) | +1.6205 (PASS) |
| not_blocking | blocking_max | +0.0000 (PASS) | +0.0000 (PASS) |
| size_accuracy | diameter_error_quanta | +0.0091 (PASS) | +0.0091 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

### C14 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| crossed | progress_m | +7.1995 (PASS) | +7.2005 (PASS) |
| lift_bounded | lift_over_nominal_m | - | +0.0729 (FAIL) |
| no_speed_loss | speed_ratio | +1.5860 (PASS) | +1.6134 (PASS) |
| size_accuracy | diameter_error_quanta | +0.1454 (PASS) | +0.1454 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

### C14 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| crossed | progress_m | +6.9485 (PASS) | +6.9495 (PASS) |
| lift_bounded | lift_over_nominal_m | - | +0.1554 (FAIL) |
| no_speed_loss | speed_ratio | +1.4769 (PASS) | +1.4769 (PASS) |
| size_accuracy | diameter_error_quanta | +0.4614 (PASS) | +0.4614 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

### C14 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| crossed | progress_m | +6.3991 (PASS) | +6.4001 (PASS) |
| lift_bounded | lift_over_nominal_m | - | +0.2547 (FAIL) |
| no_speed_loss | speed_ratio | +1.2382 (PASS) | +1.2382 (PASS) |
| size_accuracy | diameter_error_quanta | +0.3854 (PASS) | +0.3854 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

## C09

### C09 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| on_step | deck_gap_m | +0.0750 (FAIL) | +0.0000 (PASS) |
| rest_gap | support_gap_m | +0.0000 (PASS) | -0.1499 (FAIL) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 3 过 / 2 红 / 0 跳过

### C09 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| rest_gap | support_gap_m | - | -0.2995 (FAIL) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

### C09 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| rest_gap | support_gap_m | - | -0.5505 (FAIL) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

### C09 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| on_step | deck_gap_m | - | +0.0002 (PASS) |
| rest_gap | support_gap_m | - | -1.0999 (FAIL) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

## C08

### C08 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| on_step | deck_gap_m | +0.0000 (PASS) | +0.0000 (PASS) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

### C08 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

### C08 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| on_step | deck_gap_m | - | +0.0004 (PASS) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

### C08 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| on_step | deck_gap_m | - | +0.0001 (PASS) |

**合计** v1: 5 检查 / 5 过 / 0 红 / 0 跳过 → **v2**: 5 检查 / 4 过 / 1 红 / 0 跳过

## C10

### C10 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| blocked_gap | blocked_gap_m | +0.0010 (PASS) | -4.3338 (FAIL) |
| on_step | deck_gap_m | +0.1000 (FAIL) | - |
| rest_gap | support_gap_m | - | +0.0000 (PASS) |

**合计** v1: 6 检查 / 6 过 / 0 红 / 0 跳过 → **v2**: 6 检查 / 4 过 / 2 红 / 0 跳过

### C10 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|

**合计** v1: 6 检查 / 5 过 / 0 红 / 1 跳过 → **v2**: 6 检查 / 5 过 / 0 红 / 1 跳过

### C10 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|

**合计** v1: 6 检查 / 5 过 / 0 红 / 1 跳过 → **v2**: 6 检查 / 4 过 / 1 红 / 1 跳过

### C10 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| on_step | deck_gap_m | - | +0.0005 (PASS) |

**合计** v1: 6 检查 / 5 过 / 0 红 / 1 跳过 → **v2**: 6 检查 / 4 过 / 1 红 / 1 跳过

## C01

### C01 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | +0.0000 (PASS) | +0.0000 (PASS) |
| ramp_entered | ramp_entry_progress_m | +8.2491 (PASS) | +8.2501 (PASS) |
| ramp_registered | ramp_count | +1.0000 (PASS) | +0.0000 (FAIL) |
| ramp_support | ramp_support_seen | +1.0000 (PASS) | +1.0000 (PASS) |

**合计** v1: 6 检查 / 6 过 / 0 红 / 0 跳过 → **v2**: 6 检查 / 5 过 / 1 红 / 0 跳过

### C01 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +8.0995 (PASS) | +8.1005 (PASS) |

**合计** v1: 6 检查 / 6 过 / 0 红 / 0 跳过 → **v2**: 6 检查 / 5 过 / 1 红 / 0 跳过

### C01 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +7.8484 (PASS) | +7.8494 (PASS) |

**合计** v1: 6 检查 / 6 过 / 0 红 / 0 跳过 → **v2**: 6 检查 / 5 过 / 1 红 / 0 跳过

### C01 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +7.2990 (PASS) | +7.3000 (PASS) |

**合计** v1: 6 检查 / 6 过 / 0 红 / 0 跳过 → **v2**: 6 检查 / 5 过 / 1 红 / 0 跳过

## C03

### C03 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| no_slip_back | slip_back_m | +0.0000 (PASS) | +0.0000 (PASS) |
| ramp_entered | ramp_entry_progress_m | +5.9103 (PASS) | +5.9169 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

### C03 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +5.8269 (PASS) | +5.8675 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

### C03 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | - | +0.0002 (PASS) |
| ramp_entered | ramp_entry_progress_m | +5.8193 (PASS) | +5.8473 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

### C03 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| ramp_entered | ramp_entry_progress_m | +5.3698 (PASS) | +5.3244 (PASS) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 6 过 / 1 红 / 0 跳过

## C05

### C05 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | +0.0887 (FAIL) | - |
| ramp_entered | ramp_entry_progress_m | +0.4492 (PASS) | +6.2726 (PASS) |

**合计** v1: 6 检查 / 5 过 / 1 红 / 0 跳过 → **v2**: 6 检查 / 5 过 / 1 红 / 0 跳过

### C05 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | +0.1763 (FAIL) | - |
| ramp_entered | ramp_entry_progress_m | +0.2996 (PASS) | +6.3383 (PASS) |

**合计** v1: 6 检查 / 5 过 / 1 红 / 0 跳过 → **v2**: 6 检查 / 5 过 / 1 红 / 0 跳过

### C05 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | +0.3234 (FAIL) | - |
| ramp_entered | ramp_entry_progress_m | n/a (SKIPPED) | n/a (SKIPPED) |

**合计** v1: 6 检查 / 4 过 / 1 红 / 1 跳过 → **v2**: 6 检查 / 4 过 / 1 红 / 1 跳过

### C05 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | +0.6000 (FAIL) | - |
| ramp_entered | ramp_entry_progress_m | n/a (SKIPPED) | n/a (SKIPPED) |
| ramp_support | ramp_support_seen | n/a (SKIPPED) | n/a (SKIPPED) |

**合计** v1: 6 检查 / 3 过 / 1 红 / 2 跳过 → **v2**: 6 检查 / 3 过 / 1 红 / 2 跳过

## C06

### C06 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| no_climb | max_bottom_rise_m | +0.0000 (PASS) | +0.3068 (FAIL) |
| no_ramp_registered | ramp_count | +0.0000 (PASS) | +0.0000 (PASS) |
| ramp_refused | ramp_refusal_reason_len | +36.0000 (PASS) | +36.0000 (PASS) |
| refusal_slope | ramp_refusal_slope_deg | +50.0000 (PASS) | +50.0000 (PASS) |
| stopped_by_wall | blocked_gap_m | +0.0010 (PASS) | -0.0809 (FAIL) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 5 过 / 2 红 / 0 跳过

### C06 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| no_climb | max_bottom_rise_m | - | +0.5999 (FAIL) |
| stopped_by_wall | blocked_gap_m | - | -1.0264 (FAIL) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 5 过 / 2 红 / 0 跳过

### C06 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| no_climb | max_bottom_rise_m | - | +0.6000 (FAIL) |
| stopped_by_wall | blocked_gap_m | - | -2.1663 (FAIL) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 5 过 / 2 红 / 0 跳过

### C06 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| stopped_by_wall | blocked_gap_m | - | -2.8897 (FAIL) |

**合计** v1: 7 检查 / 7 过 / 0 红 / 0 跳过 → **v2**: 7 检查 / 5 过 / 2 红 / 0 跳过

## C13

### C13 D=0.3

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | +0.0000 (PASS) | +0.0000 (PASS) |
| fall_frames | fall_frames | +29.0000 (PASS) | +31.0000 (PASS) |
| landed_on_ground | support_kind | +0.0000 (PASS) | +0.0000 (PASS) |
| landing_gap | landing_gap_m | +0.0000 (PASS) | +0.0012 (PASS) |
| ramp_entered | ramp_entry_progress_m | +8.8491 (PASS) | +8.8501 (PASS) |
| seam_no_step | grounded_frame_dy_m | +0.0134 (PASS) | +0.0134 (PASS) |

**合计** v1: 10 检查 / 10 过 / 0 红 / 0 跳过 → **v2**: 10 检查 / 9 过 / 1 红 / 0 跳过

### C13 D=0.6

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| fall_frames | fall_frames | +28.0000 (PASS) | +30.0000 (PASS) |
| landing_gap | landing_gap_m | - | +0.0008 (PASS) |
| ramp_entered | ramp_entry_progress_m | +8.6995 (PASS) | +8.7005 (PASS) |

**合计** v1: 10 检查 / 10 过 / 0 红 / 0 跳过 → **v2**: 10 检查 / 9 过 / 1 红 / 0 跳过

### C13 D=1.1

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| climbed | deck_gap_m | - | +0.0002 (PASS) |
| ramp_entered | ramp_entry_progress_m | +8.4485 (PASS) | +8.4495 (PASS) |
| seam_no_step | grounded_frame_dy_m | - | +0.0133 (PASS) |

**合计** v1: 10 检查 / 10 过 / 0 红 / 0 跳过 → **v2**: 10 检查 / 9 过 / 1 红 / 0 跳过

### C13 D=2.2

| 检查 | 指标 | v1 | v2 |
|---|---|---|---|
| fall_frames | fall_frames | - | +25.0000 (PASS) |
| landed_on_ground | support_kind | n/a (SKIPPED) | n/a (SKIPPED) |
| landing_gap | landing_gap_m | n/a (SKIPPED) | n/a (SKIPPED) |
| ramp_entered | ramp_entry_progress_m | +7.8991 (PASS) | +7.9001 (PASS) |
| seam_no_step | grounded_frame_dy_m | +0.9499 (FAIL) | +0.0231 (PASS) |

**合计** v1: 10 检查 / 7 过 / 1 红 / 2 跳过 → **v2**: 10 检查 / 7 过 / 1 红 / 2 跳过
