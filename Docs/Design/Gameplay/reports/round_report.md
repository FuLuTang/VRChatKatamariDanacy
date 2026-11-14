# 回合经济模拟报告（自动生成）

**状态：`PROVISIONAL_NOT_TESTED`。** 本报告由 `simulate.py` 生成，是离线设计模型输出，不是 Unity、网络、物理或真人技术测量。

- 命令：`simulate.py --out Docs/Design/Gameplay/reports`
- seed：`7`
- 模型：`Docs/Design/Gameplay/round_model.py`（地图快照 21 区 / 452 件 / 1356 价值单位）
- 交分规则：缩小 `bank_residual_fraction=0.25`、最小兑换 `min_bank_diameter_m=0.55` m、轮换 `bank_cycle_s=60.0` s、奖励 开启 `50×3`

## 场景总表

| 场景 | 玩家 | 策略 | 总分 | 交存分 | 奖励分 | 峰值球径 m | 拾取件数 | 交分次数 | 被吞次数 | 复活停机 s | ≥0.95m 时刻 s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| duel_equal | player_a | eat_everything | 638 | 588 | 50 | 1.48 | 188 | 2 | 0 | 0.0 | 17.0 |
| duel_equal | player_b | eat_everything | 652 | 552 | 100 | 1.48 | 189 | 2 | 1 | 2.8 | 17.0 |
| triple_mixed | fast_eater | eat_everything | 763 | 613 | 150 | 1.47 | 179 | 2 | 2 | 3.5 | 16.2 |
| triple_mixed | careful | avoid_threats | 395 | 395 | 0 | 1.28 | 155 | 2 | 1 | 2.8 | 17.0 |
| triple_mixed | slow_banker | rush_bank | 100 | 100 | 0 | 0.93 | 110 | 1 | 6 | 13.8 | - |
| four_mixed | fast_eater | eat_everything | 452 | 402 | 50 | 1.47 | 148 | 1 | 1 | 2.8 | 16.2 |
| four_mixed | careful | avoid_threats | 278 | 278 | 0 | 1.26 | 125 | 2 | 2 | 5.5 | 17.0 |
| four_mixed | slow_banker | rush_bank | 100 | 100 | 0 | 0.93 | 64 | 1 | 9 | 22.0 | - |
| four_mixed | average_eater | eat_everything | 515 | 415 | 100 | 1.49 | 112 | 1 | 0 | 0.0 | 19.8 |
| four_restock | fast_eater | eat_everything | 524 | 424 | 100 | 1.47 | 166 | 2 | 2 | 5.5 | 16.2 |
| four_restock | careful | avoid_threats | 295 | 295 | 0 | 1.26 | 142 | 2 | 1 | 2.8 | 17.0 |
| four_restock | slow_banker | rush_bank | 100 | 100 | 0 | 0.93 | 81 | 1 | 10 | 19.2 | - |
| four_restock | average_eater | eat_everything | 473 | 423 | 50 | 1.47 | 142 | 2 | 0 | 0.0 | 19.8 |

## 15 s 成长曲线（球径 m）

### duel_equal

| bucket | player_a | player_b |
|---|---|---|
| 15s | 0.86 | 0.86 |
| 30s | 1.27 | 1.27 |
| 45s | 1.38 | 1.38 |
| 60s | 1.48 | 1.48 |
| 75s | 0.72 | 0.70 |
| 90s | 0.89 | 0.87 |
| 105s | 0.96 | 0.91 |
| 120s | 1.03 | 0.98 |
| 135s | 1.11 | 1.03 |
| 150s | 1.17 | 1.09 |
| 165s | 1.17 | 1.09 |
| 180s | 0.52 | 0.51 |

### triple_mixed

| bucket | fast_eater | careful | slow_banker |
|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 |
| 30s | 1.28 | 1.15 | 0.46 |
| 45s | 1.47 | 1.28 | 0.42 |
| 60s | 0.93 | 0.52 | 0.30 |
| 75s | 1.00 | 0.69 | 0.30 |
| 90s | 1.10 | 0.89 | 0.61 |
| 105s | 1.16 | 0.98 | 0.77 |
| 120s | 1.20 | 0.98 | 0.30 |
| 135s | 1.22 | 0.99 | 0.30 |
| 150s | 1.22 | 0.99 | 0.30 |
| 165s | 0.30 | 0.48 | 0.30 |
| 180s | 0.30 | 0.48 | 0.49 |

### four_mixed

| bucket | fast_eater | careful | slow_banker | average_eater |
|---|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 | 0.74 |
| 30s | 1.10 | 1.15 | 0.46 | 0.99 |
| 45s | 1.35 | 1.15 | 0.52 | 1.12 |
| 60s | 0.72 | 1.21 | 0.52 | 1.26 |
| 75s | 0.87 | 1.26 | 0.30 | 1.39 |
| 90s | 0.94 | 0.30 | 0.39 | 1.41 |
| 105s | 0.30 | 0.52 | 0.30 | 1.49 |
| 120s | 0.52 | 0.63 | 0.30 | 0.60 |
| 135s | 0.52 | 0.63 | 0.30 | 0.60 |
| 150s | 0.52 | 0.63 | 0.30 | 0.60 |
| 165s | 0.52 | 0.38 | 0.30 | 0.60 |
| 180s | 0.52 | 0.30 | 0.30 | 0.60 |

### four_restock

| bucket | fast_eater | careful | slow_banker | average_eater |
|---|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 | 0.74 |
| 30s | 1.10 | 1.15 | 0.46 | 0.99 |
| 45s | 1.35 | 1.15 | 0.52 | 1.12 |
| 60s | 0.72 | 1.21 | 0.52 | 1.26 |
| 75s | 0.87 | 1.26 | 0.33 | 1.39 |
| 90s | 0.95 | 0.35 | 0.39 | 1.42 |
| 105s | 0.30 | 0.53 | 0.33 | 1.47 |
| 120s | 0.54 | 0.62 | 0.39 | 0.67 |
| 135s | 0.58 | 0.67 | 0.30 | 0.72 |
| 150s | 0.58 | 0.68 | 0.37 | 0.72 |
| 165s | 0.35 | 0.72 | 0.37 | 0.41 |
| 180s | 0.46 | 0.41 | 0.43 | 0.50 |


## 多种子均值

种子：1, 2, 3, 4, 5；总分是全场合计，范围给出种子之间的波动。

| 场景 | 总分均值 | 总分范围 | 拾取件数均值 | 被吞均值 | 交分均值 |
|---|---|---|---|---|---|
| duel_equal | 1250 | 1190-1290 | 373 | 0.6 | 4.0 |
| triple_mixed | 1257 | 1257-1258 | 444 | 9.8 | 5.0 |
| four_mixed | 1335 | 1293-1345 | 449 | 12.4 | 5.0 |
| four_restock | 1407 | 1391-1457 | 533 | 12.2 | 6.6 |
## 时间去向（各场景合计，秒）

| 场景 | 第 1 | 第 2 | 第 3 |
|---|---|---|---|
| duel_equal | travel 184.8 | absorb 121.2 | bank_travel 42.2 |
| triple_mixed | travel 330.5 | absorb 147.0 | bank_travel 32.8 |
| four_mixed | travel 499.2 | absorb 147.0 | respawn 30.2 |
| four_restock | travel 455.0 | absorb 174.0 | bank_travel 46.0 |

## 参数 ±20% 敏感性（baseline `four_mixed`）

| 参数 | 原型值 | -20% 总分 | base 总分 | +20% 总分 | 最大|Δ| | 分差 -/base/+ | 含义 |
|---|---|---|---|---|---|---|---|
| radius_exponent | 0.3333 | 1113 | 1335 | 1453 | 17% | 405/404/448 | volume->radius shape; 1/3 is the documented sphere mapping |
| absorb_size_ratio | 1 | 1186 | 1335 | 1414 | 11% | 402/404/304 | item pickup gate vs ball diameter |
| base_speed_mps | 3 | 1214 | 1335 | 1305 | 9% | 493/404/385 | rolling speed |
| magnet_absorb_multiplier | 2 | 1405 | 1335 | 1220 | 9% | 333/404/477 | magnet throughput |
| swallow_diameter_ratio | 1.35 | 1223 | 1335 | 1330 | 8% | 553/404/427 | PVP swallow threshold |
| units_to_max_diameter | 1356 | 1324 | 1335 | 1252 | 6% | 399/404/405 | how much placeholder value the 0.30->2.20 m span costs |
| absorb_items_per_s | 3 | 1328 | 1335 | 1355 | 2% | 379/404/364 | pickup throughput |
| bank_residual_fraction | 0.25 | 1366 | 1335 | 1325 | 2% | 366/404/423 | share of growth kept after converting (project formula) |
| min_bank_diameter_m | 0.55 | 1359 | 1335 | 1316 | 2% | 414/404/412 | smallest ball that can convert at all |
| bonus_points | 50 | 1307 | 1335 | 1363 | 2% | 386/404/422 | end-of-match category bonus (original: +50 pt) |
| bank_cycle_s | 60 | 1323 | 1335 | 1335 | 1% | 392/404/404 | conversion-area rotation period (original: 60 s) |
| respawn_delay_s | 3 | 1343 | 1335 | 1334 | 1% | 413/404/405 | KO downtime |
| steal_fraction | 0.25 | 1328 | 1335 | 1323 | 1% | 397/404/393 | attacker share of victim unbanked volume |
| magnet_duration_s | 4 | 1333 | 1335 | 1316 | 1% | 333/404/364 | magnet uptime |
| bank_service_s | 0.5 | 1335 | 1335 | 1339 | 0% | 404/404/409 | deposit dwell |
| respawn_protection_s | 2 | 1335 | 1335 | 1335 | 0% | 404/404/404 | post-respawn immunity |
| rocket_duration_s | 2.5 | 1335 | 1335 | 1335 | 0% | 404/404/404 | rocket uptime |
| rocket_speed_multiplier | 1.5 | 1335 | 1335 | 1335 | 0% | 404/404/404 | rocket speed |

## 交分规则对比（旧重置 vs 新缩小 vs +奖励）

| 变体 | 总分 | 交存分 | 奖励分 | 奖励占比 | 峰值 m | 末球径均值 m | 末球径≥0.55 人数 | 分差 |
|---|---|---|---|---|---|---|---|---|
| reset_0.00_no_bonus | 1171 | 1171 | 0 | 0.0% | 1.49 | 0.31 | 0 | 317 |
| shrink_0.25_no_bonus | 1195 | 1195 | 0 | 0.0% | 1.48 | 0.43 | 1 | 314 |
| shrink_0.25_plus_bonus | 1335 | 1195 | 140 | 10.5% | 1.48 | 0.43 | 1 | 404 |

`reset_0.00_no_bonus` 是 2026-11-14 之前的旧规则（交分回 0.30 m、无奖励）；`shrink_*.25_no_bonus` 只换缩小规则；最后一行再加回合末奖励。末球径≥0.55 是回合结束时仍达到最小交分门槛的人数（每局均值）。

## 读法

- 总分 = 交存分 + 回合末奖励分；不是单人成绩。
- 被吞次数 = 被吞事件数（每名受害者计一次）。
- 交分不再重置：曲线掉到约 0.36–0.60 m 是一次交分后的缩小，掉回 0.30 m 只可能是被吞后的重生。
- 球径 < `min_bank_diameter_m` 时交分被拒绝（本次运行不产生分数，玩家必须先去长大）；子阈值访问不会改变球或分数。
- 模型假设：玩家在区域内以固定吞吐拾取、按球径门槛过滤；区域库存共享；被吞后回到起始球径。详见 `README.md`。

**PROVISIONAL_NOT_TESTED**：任何数字都不得作为平衡 PASS 或实测证据。
