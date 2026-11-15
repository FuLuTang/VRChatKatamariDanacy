# PhysicsTestField 判据矩阵 pass2（2026-11-15c，commit 5ceeff6）

## 结论：修复生效，矩阵可用

36 次运行（4 档 × 9 格）全部按预期长到目标直径；`size_accuracy` 每格每档都落在对应列。

| 档 | `size_accuracy` 实测 | 期望 | cap（正比直径） |
|---|---|---|---|
| 0.30 | **+0.0091** | +0.0091 | 0.0525 |
| 0.60 | **+0.1454** | +0.1454 | 0.1048 |
| 1.10 | **+0.4614** | +0.4614 | 0.1927 |
| 2.20 | **+0.3854** | +0.3854 | 0.3000 |

指纹：`[ptf] build=2026-11-15c cells=9 presets=4`、`[ptf] setter build=2026-11-15c baselineVolumeUnits=3.439024`。
坡道格的 run 行都带 `ramps=1/rampCell=1`；`[ptf] rampProbe cell=C06 refused=1 slope=50.00` 仍在。

## 与期望的偏离（只报实测，未改任何判据/参数）

1. **C05 在 0.60 / 1.10 / 2.20 各多一条 `ramp_entered` 失败**（期望只有 `climbed` 一条）：
   - 0.60：`ramp_entry_progress_m=+0.2996`（阈值 ≥0.30）＋ `climbed deck_gap=+0.1763`
   - 1.10：`ramp_entry_progress_m=+0.0485` ＋ `climbed deck_gap=+0.3234`
   - 2.20：`ramp_support_seen=+0.0000`、`ramp_entry_progress_m=-0.5009`、`climbed deck_gap=+0.6000`（三条）
   - 0.30 仍是单条：`climbed deck_gap=+0.0887`（与预期 0.0887 完全一致）。
2. **C01 在 2.20 的 `climbed`**：`deck_gap=+0.0134`，阈值 abs≤0.01，差 3.4 mm → UNEXPECTED_FAIL（其余三档均 PASS）。
3. **C13 在 2.20 有 4 条失败**（其余三档全 PASS）：
   - `ramp_entry_progress_m=+0.1005`
   - **`seam_no_step grounded_frame_dy_m=+0.9499`**（阈值 ≤0.05，着陆帧一帧跳了 0.95 m）
   - `landing_gap_m=+0.0419`
   - `landed_on_ground support_kind=+2.0000`（不是地面支撑）

## 关于「球穿过斜坡」的实测线索

- 2.20 时球半径 1.10 m，而 C13/C01/C05 的台面高度只有 0.60 m：球心在地面时 y=1.10、球底 0.00，台面整块都在球体内部。
- C13 2.20 的 `seam_no_step = 0.9499 m`（一帧垂直位移）与 `support_kind=2` 同时出现，正符合「球体把台面吞进去、支撑判定丢失、随后瞬移/落回」的形态。
- 逐点轨迹见 `traj_ramps.md`：C01/C03/C05/C06/C13 × 四档，含球心 y、球底 y、sup（支撑高度）、lift、stuck。
- 0.60 档对照：C01/C03/C13 的球心最高 0.90（球底 0.60 = 台面），C05 只到 0.72（球底 0.42，卡在 45° 接缝），C06 全程 0.30（50° 被拒）。

## 每档合计（期望：0.30 档 58 pass + 1 unexpected；其余 57 pass + 1 skip + 1 unexpected）

| D | pass | unexpected | skipped | 合计 |
|---|---|---|---|---|
| 0.30 | 58 | 1 | 0 | 59 |
| 0.60 | 56 | 2 | 1 | 59 |
| 1.10 | 56 | 2 | 1 | 59 |
| 2.20 | 50 | 8 | 1 | 59 |

## 状态

- 已退出 Play，场景从磁盘重开 `dirty=False`；未在 Play 中存盘。
- Windows HEAD `5ceeff6`；58 个 U# program `.asset` 仍为 modified（按指示保留，未 `checkout --`）。
- `ProjectSettings/ProjectSettings.asset`、`Labs/CpuSkirmish/CpuSkirmish.unity` 保持原状。
