# PhysicsTestField pass3（2026-11-15d，commit 7f5cd7b）

## 结论：全部命中期望，无偏离

36 跑（4 档 × 9 格）逐格逐档与 Lead 给的期望表**完全一致**：没有一条多余的 unexpected，也没有少一条 skip。

| 档 | pass | skipped | unexpected | 期望（Lead） |
|---|---|---|---|---|
| 0.30 | 58 | 0 | 1 | 58 / 0 / 1 ✓ |
| 0.60 | 57 | 1 | 1 | 57 / 1 / 1 ✓ |
| 1.10 | 56 | 2 | 1 | 56 / 2 / 1 ✓ |
| 2.20 | 52 | 5 | 2 | 52 / 5 / 2 ✓ |

`size_accuracy`：0.30 **+0.0091**、0.60 **+0.1454**、1.10 **+0.4614**、2.20 **+0.3854**。

## 编译与测试

- C# / U# 编译 **0 错误**（重编前 refresh + `CompileAllCsPrograms(true,false)`）。
- EditMode 全量：603 total / **602 passed / 1 failed** / 0 skipped（13.46 s）。唯一失败是 SDK 链接检查 `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`（`Const_SystemBooleanArray` 的 docs URL 不解析），与预期一致。详见 `tests.md`。
- 判据重导：`cells=9 checks=59 missingCells=0 missingArenas=0 driverFields=6 errors=0`，无缺 `checkAppliesMasks` → U# 确为新版；非 Play 存盘 `dirty=False`。

## 指纹

```
[ptf] build=2026-11-15d cells=9 presets=4
[ptf] setter build=2026-11-15d baselineVolumeUnits=3.439024
```

## 新规则的实际输出

`derived`（带 `step_limit` / `min_progress`，随直径缩放）：

```
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.074934 expect=fail      (0.30)
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.149735 expect=pass      (0.60)
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.275248 expect=pass      (1.10)
[ptf] derived cell=C09 check=on_step deck_rise=0.0750 step_limit=0.549948 expect=pass      (2.20)
[ptf] derived cell=C01 check=ramp_entered step_limit=0.074934 min_progress=0.074934
```

`notApplicable`（新规则，3 条，全部符合期望）：

```
[ptf] notApplicable cell=C05 D=1.100 checks=ramp_entered reason=scene_has_no_scenario_at_this_diameter
[ptf] notApplicable cell=C05 D=2.200 checks=ramp_support,ramp_entered reason=scene_has_no_scenario_at_this_diameter
[ptf] notApplicable cell=C13 D=2.200 checks=landing_gap,landed_on_ground reason=scene_has_no_scenario_at_this_diameter
```

## 仅剩的 5 条非预期失败（全部已知）

| cell | D | check | metric | value | 说明 |
|---|---|---|---|---|---|
| C05 | 0.30 | climbed | deck_gap_m | +0.0887 | 45° 接缝，电机 θ_max=36.87° |
| C05 | 0.60 | climbed | deck_gap_m | +0.1763 | 同上 |
| C05 | 1.10 | climbed | deck_gap_m | +0.3234 | 同上 |
| C05 | 2.20 | climbed | deck_gap_m | +0.6000 | 同上（球径 > 台面高） |
| C13 | 2.20 | seam_no_step | grounded_frame_dy_m | +0.9499 | 已立缺陷单的大球穿台面 |

C01 2.20 的 `climbed +0.0134` 已消失（窗口改 8.75 生效）；C05 的 `ramp_entered` 三档已由不适用规则转为 skip，C13 2.20 的 `landing_gap`/`landed_on_ground` 同样转 skip。

## 其他

- 无 `BLOCKED_UNTIL`、无 `error=unknownMetric`、无 `sizeNotApplied`、无 `rampCellMismatch`；`rampProbe cell=C06 refused=1 slope=50.00` 仍在；坡道跑都带 `ramps=1/rampCell=1`。
- Console 里没有任何本项目的 warning/error；矩阵前清过 console，其中唯一的非 PTF 行是 MCP 插件自身的 WebSocket 提示（已随清屏移除）。
- 已退出 Play，场景从磁盘重开 `dirty=False`，Play 中未存盘。

## 编译产物（本次带回给 Lead 整批提交）

`assets/` 下 54 个 `.asset`（33 个 `SerializedUdonPrograms/*.asset` + 21 个 U# program `.asset`），清单 `assets_files.txt`，哈希 `assets_sha256.txt`。这些就是 7f5cd7b 源码对应的编译产物，Windows 工作树里同名文件仍为 modified 状态。
