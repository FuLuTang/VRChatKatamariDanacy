# 回合经济模拟报告（自动生成）

**状态：`PROVISIONAL_NOT_TESTED`。** 本报告由 `simulate.py` 生成，是离线设计模型输出，不是 Unity、网络、物理或真人技术测量。

- 命令：`simulate.py --out Docs/Design/Gameplay/reports`
- seed：`7`
- 模型：`Docs/Design/Gameplay/round_model.py`（默认读活地图，无内置快照）
- 地图：`picnic_lawn` — 21 区 / 452 件 / 1284 价值单位；布局 sha256 `5b08daf9…`、目录 sha256 `01d01418…`，出生按 `player_count_plans`
- 交分规则：缩小 `bank_residual_fraction=0.25`、最小兑换 `min_bank_diameter_m=0.55` m、轮换 `bank_cycle_s=60.0` s、奖励 开启 `50×3`

## 场景总表

| 场景 | 玩家 | 策略 | 总分 | 交存分 | 奖励分 | 峰值球径 m | 拾取件数 | 交分次数 | 被吞次数 | 复活停机 s | ≥0.95m 时刻 s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| duel_equal | player_a | eat_everything | 521 | 521 | 0 | 1.47 | 182 | 2 | 0 | 0.0 | 17.0 |
| duel_equal | player_b | eat_everything | 647 | 547 | 100 | 1.47 | 194 | 2 | 0 | 0.0 | 17.0 |
| triple_mixed | fast_eater | eat_everything | 661 | 561 | 100 | 1.48 | 194 | 2 | 2 | 5.5 | 16.2 |
| triple_mixed | careful | avoid_threats | 401 | 351 | 50 | 1.26 | 129 | 2 | 0 | 0.0 | 17.0 |
| triple_mixed | slow_banker | rush_bank | 200 | 200 | 0 | 0.93 | 120 | 2 | 6 | 13.8 | - |
| four_mixed | fast_eater | eat_everything | 462 | 412 | 50 | 1.48 | 114 | 1 | 11 | 27.2 | 16.2 |
| four_mixed | careful | avoid_threats | 300 | 250 | 50 | 1.26 | 128 | 1 | 8 | 19.5 | 17.2 |
| four_mixed | slow_banker | rush_bank | 0 | 0 | 0 | 0.79 | 79 | 0 | 3 | 8.2 | - |
| four_mixed | average_eater | eat_everything | 438 | 388 | 50 | 1.45 | 126 | 1 | 0 | 0.0 | 19.8 |
| four_restock | fast_eater | eat_everything | 463 | 413 | 50 | 1.48 | 167 | 1 | 3 | 8.2 | 16.2 |
| four_restock | careful | avoid_threats | 250 | 250 | 0 | 1.26 | 149 | 1 | 2 | 5.5 | 17.2 |
| four_restock | slow_banker | rush_bank | 0 | 0 | 0 | 0.80 | 91 | 0 | 7 | 16.5 | - |
| four_restock | average_eater | eat_everything | 524 | 424 | 100 | 1.50 | 153 | 1 | 0 | 0.0 | 19.8 |

## 15 s 成长曲线（球径 m）

### duel_equal

| bucket | player_a | player_b |
|---|---|---|
| 15s | 0.86 | 0.86 |
| 30s | 1.27 | 1.27 |
| 45s | 1.38 | 1.38 |
| 60s | 1.47 | 1.47 |
| 75s | 0.59 | 0.65 |
| 90s | 0.75 | 0.77 |
| 105s | 0.86 | 0.89 |
| 120s | 0.90 | 0.96 |
| 135s | 0.99 | 1.04 |
| 150s | 1.05 | 1.10 |
| 165s | 1.05 | 1.10 |
| 180s | 0.55 | 0.56 |

### triple_mixed

| bucket | fast_eater | careful | slow_banker |
|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 |
| 30s | 1.28 | 1.15 | 0.46 |
| 45s | 1.38 | 1.26 | 0.52 |
| 60s | 0.60 | 0.69 | 0.65 |
| 75s | 0.76 | 0.86 | 0.84 |
| 90s | 0.88 | 0.86 | 0.93 |
| 105s | 0.98 | 0.94 | 0.46 |
| 120s | 1.00 | 0.95 | 0.57 |
| 135s | 1.08 | 0.97 | 0.30 |
| 150s | 1.11 | 0.97 | 0.30 |
| 165s | 0.30 | 0.97 | 0.30 |
| 180s | 0.30 | 0.47 | 0.30 |

### four_mixed

| bucket | fast_eater | careful | slow_banker | average_eater |
|---|---|---|---|---|
| 15s | 0.91 | 0.86 | 0.30 | 0.75 |
| 30s | 1.28 | 1.09 | 0.52 | 1.01 |
| 45s | 1.48 | 1.26 | 0.30 | 1.15 |
| 60s | 0.42 | 0.54 | 0.66 | 1.20 |
| 75s | 0.30 | 0.52 | 0.79 | 1.26 |
| 90s | 0.30 | 0.73 | 0.30 | 1.33 |
| 105s | 0.30 | 0.30 | 0.30 | 1.39 |
| 120s | 0.30 | 0.30 | 0.52 | 1.45 |
| 135s | 0.30 | 0.30 | 0.52 | 1.45 |
| 150s | 0.30 | 0.30 | 0.52 | 1.45 |
| 165s | 0.30 | 0.30 | 0.52 | 0.59 |
| 180s | 0.30 | 0.30 | 0.52 | 0.59 |

### four_restock

| bucket | fast_eater | careful | slow_banker | average_eater |
|---|---|---|---|---|
| 15s | 0.91 | 0.86 | 0.30 | 0.75 |
| 30s | 1.28 | 1.09 | 0.52 | 1.01 |
| 45s | 1.48 | 1.26 | 0.33 | 1.15 |
| 60s | 0.33 | 0.54 | 0.66 | 1.20 |
| 75s | 0.52 | 0.53 | 0.80 | 1.26 |
| 90s | 0.52 | 0.80 | 0.40 | 1.32 |
| 105s | 0.64 | 0.80 | 0.40 | 1.38 |
| 120s | 0.69 | 0.83 | 0.30 | 1.40 |
| 135s | 0.74 | 0.84 | 0.30 | 1.46 |
| 150s | 0.30 | 0.30 | 0.39 | 1.50 |
| 165s | 0.46 | 0.48 | 0.55 | 0.60 |
| 180s | 0.52 | 0.55 | 0.55 | 0.61 |


## 多种子均值

种子：1, 2, 3, 4, 5；总分是全场合计，范围给出种子之间的波动。

| 场景 | 总分均值 | 总分范围 | 拾取件数均值 | 被吞均值 | 交分均值 |
|---|---|---|---|---|---|
| duel_equal | 1178 | 1168-1218 | 378 | 0.2 | 4.0 |
| triple_mixed | 1262 | 1262-1262 | 443 | 8.0 | 6.0 |
| four_mixed | 1193 | 1174-1203 | 447 | 19.4 | 3.0 |
| four_restock | 1236 | 1235-1238 | 549 | 13.2 | 3.8 |
## 时间去向（各场景合计，秒）

| 场景 | 第 1 | 第 2 | 第 3 |
|---|---|---|---|
| duel_equal | travel 185.2 | absorb 120.5 | bank_travel 45.2 |
| triple_mixed | travel 320.5 | absorb 145.8 | bank_travel 44.0 |
| four_mixed | travel 488.0 | absorb 146.8 | respawn 55.0 |
| four_restock | travel 457.2 | absorb 181.5 | bank_travel 32.8 |

## 参数 ±20% 敏感性（baseline `four_mixed`）

| 参数 | 原型值 | -20% 总分 | base 总分 | +20% 总分 | 最大|Δ| | 分差 -/base/+ | 含义 |
|---|---|---|---|---|---|---|---|
| swallow_diameter_ratio | 1.35 | 1007 | 1193 | 1206 | 16% | 528/482/395 | PVP swallow threshold |
| radius_exponent | 0.3333 | 1102 | 1193 | 1209 | 8% | 480/482/507 | volume->radius shape; 1/3 is the documented sphere mapping |
| absorb_size_ratio | 1 | 1101 | 1193 | 1196 | 8% | 492/482/503 | item pickup gate vs ball diameter |
| magnet_absorb_multiplier | 2 | 1175 | 1193 | 1097 | 8% | 507/482/618 | magnet throughput |
| units_to_max_diameter | 1356 | 1225 | 1193 | 1150 | 4% | 616/482/500 | how much placeholder value the 0.30->2.20 m span costs |
| magnet_duration_s | 4 | 1177 | 1193 | 1139 | 4% | 486/482/601 | magnet uptime |
| bonus_points | 50 | 1163 | 1193 | 1223 | 3% | 468/482/496 | end-of-match category bonus (original: +50 pt) |
| absorb_items_per_s | 3 | 1183 | 1193 | 1219 | 2% | 472/482/498 | pickup throughput |
| respawn_delay_s | 3 | 1180 | 1193 | 1170 | 2% | 492/482/462 | KO downtime |
| steal_fraction | 0.25 | 1163 | 1193 | 1201 | 2% | 480/482/483 | attacker share of victim unbanked volume |
| base_speed_mps | 3 | 1184 | 1193 | 1205 | 1% | 462/482/492 | rolling speed |
| bank_cycle_s | 60 | 1183 | 1193 | 1193 | 1% | 482/482/482 | conversion-area rotation period (original: 60 s) |
| bank_residual_fraction | 0.25 | 1193 | 1193 | 1176 | 1% | 482/482/482 | share of growth kept after converting (project formula) |
| bank_service_s | 0.5 | 1193 | 1193 | 1201 | 1% | 482/482/492 | deposit dwell |
| min_bank_diameter_m | 0.55 | 1194 | 1193 | 1193 | 0% | 482/482/482 | smallest ball that can convert at all |
| respawn_protection_s | 2 | 1193 | 1193 | 1193 | 0% | 482/482/482 | post-respawn immunity |
| rocket_duration_s | 2.5 | 1193 | 1193 | 1193 | 0% | 482/482/482 | rocket uptime |
| rocket_speed_multiplier | 1.5 | 1193 | 1193 | 1193 | 0% | 482/482/482 | rocket speed |

## 交分规则对比（旧重置 vs 新缩小 vs +奖励）

| 变体 | 总分 | 交存分 | 奖励分 | 奖励占比 | 峰值 m | 末球径均值 m | 末球径≥0.55 人数 | 分差 |
|---|---|---|---|---|---|---|---|---|
| reset_0.00_no_bonus | 1046 | 1046 | 0 | 0.0% | 1.48 | 0.31 | 0 | 412 |
| shrink_0.25_no_bonus | 1043 | 1043 | 0 | 0.0% | 1.48 | 0.39 | 1 | 412 |
| shrink_0.25_plus_bonus | 1193 | 1043 | 150 | 12.6% | 1.48 | 0.39 | 1 | 482 |

`reset_0.00_no_bonus` 是 2026-11-14 之前的旧规则（交分回 0.30 m、无奖励）；`shrink_*.25_no_bonus` 只换缩小规则；最后一行再加回合末奖励。末球径≥0.55 是回合结束时仍达到最小交分门槛的人数（每局均值）。

## 读法

- 总分 = 交存分 + 回合末奖励分；不是单人成绩。
- 被吞次数 = 被吞事件数（每名受害者计一次）。
- 交分不再重置：曲线掉到约 0.36–0.60 m 是一次交分后的缩小，掉回 0.30 m 只可能是被吞后的重生。
- 球径 < `min_bank_diameter_m` 时交分被拒绝（本次运行不产生分数，玩家必须先去长大）；子阈值访问不会改变球或分数。
- 模型假设：玩家在区域内以固定吞吐拾取、按球径门槛过滤；区域库存共享；被吞后回到起始球径。详见 `README.md`。

**PROVISIONAL_NOT_TESTED**：任何数字都不得作为平衡 PASS 或实测证据。
