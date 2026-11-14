# 回合经济模拟报告（自动生成）

**状态：`PROVISIONAL_NOT_TESTED`。** 本报告由 `simulate.py` 生成，是离线设计模型输出，不是 Unity、网络、物理或真人技术测量。

- 命令：`simulate.py --out Docs/Design/Gameplay/reports`
- seed：`7`
- 模型：`Docs/Design/Gameplay/round_model.py`（地图快照 21 区 / 452 件 / 1356 价值单位）

## 场景总表

| 场景 | 玩家 | 策略 | 分数 | 峰值球径 m | 拾取件数 | 交分次数 | 被吞次数 | 复活停机 s | ≥0.95m 时刻 s |
|---|---|---|---|---|---|---|---|---|---|
| duel_equal | player_a | eat_everything | 409 | 1.48 | 134 | 2 | 5 | 13.8 | 17.0 |
| duel_equal | player_b | eat_everything | 661 | 1.48 | 205 | 3 | 0 | 0.0 | 17.0 |
| triple_mixed | fast_eater | eat_everything | 601 | 1.47 | 198 | 2 | 1 | 2.8 | 16.2 |
| triple_mixed | careful | avoid_threats | 376 | 1.26 | 163 | 2 | 1 | 2.8 | 17.0 |
| triple_mixed | slow_banker | rush_bank | 200 | 0.93 | 81 | 2 | 6 | 13.8 | - |
| four_mixed | fast_eater | eat_everything | 402 | 1.47 | 121 | 1 | 3 | 8.2 | 16.2 |
| four_mixed | careful | avoid_threats | 282 | 1.27 | 117 | 2 | 1 | 2.8 | 17.0 |
| four_mixed | slow_banker | rush_bank | 100 | 0.93 | 106 | 1 | 4 | 11.0 | - |
| four_mixed | average_eater | eat_everything | 410 | 1.48 | 106 | 1 | 1 | 2.8 | 19.8 |
| four_restock | fast_eater | eat_everything | 422 | 1.47 | 150 | 3 | 4 | 11.0 | 16.2 |
| four_restock | careful | avoid_threats | 282 | 1.27 | 134 | 3 | 3 | 8.2 | 17.0 |
| four_restock | slow_banker | rush_bank | 110 | 0.93 | 124 | 2 | 2 | 5.5 | - |
| four_restock | average_eater | eat_everything | 410 | 1.48 | 111 | 2 | 4 | 11.0 | 19.8 |

## 15 s 成长曲线（球径 m）

### duel_equal

| bucket | player_a | player_b |
|---|---|---|
| 15s | 0.86 | 0.86 |
| 30s | 1.27 | 1.27 |
| 45s | 1.38 | 1.38 |
| 60s | 1.48 | 1.48 |
| 75s | 0.30 | 0.30 |
| 90s | 0.52 | 0.61 |
| 105s | 0.30 | 0.99 |
| 120s | 0.30 | 1.13 |
| 135s | 0.30 | 1.18 |
| 150s | 0.30 | 1.25 |
| 165s | 0.33 | 0.30 |
| 180s | 0.33 | 0.30 |

### triple_mixed

| bucket | fast_eater | careful | slow_banker |
|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 |
| 30s | 1.28 | 1.15 | 0.30 |
| 45s | 1.40 | 1.26 | 0.52 |
| 60s | 0.40 | 0.46 | 0.82 |
| 75s | 0.77 | 0.64 | 0.93 |
| 90s | 0.87 | 0.82 | 0.30 |
| 105s | 0.96 | 0.88 | 0.30 |
| 120s | 1.00 | 0.96 | 0.30 |
| 135s | 1.17 | 0.99 | 0.30 |
| 150s | 1.17 | 0.99 | 0.30 |
| 165s | 0.30 | 0.30 | 0.30 |
| 180s | 0.30 | 0.30 | 0.30 |

### four_mixed

| bucket | fast_eater | careful | slow_banker | average_eater |
|---|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 | 0.74 |
| 30s | 1.10 | 1.15 | 0.30 | 0.99 |
| 45s | 1.35 | 1.15 | 0.62 | 1.12 |
| 60s | 0.30 | 1.21 | 0.62 | 1.26 |
| 75s | 0.62 | 1.26 | 0.52 | 1.35 |
| 90s | 0.52 | 0.30 | 0.65 | 1.38 |
| 105s | 0.52 | 0.52 | 0.82 | 1.45 |
| 120s | 0.55 | 0.57 | 0.30 | 1.47 |
| 135s | 0.57 | 0.58 | 0.30 | 1.47 |
| 150s | 0.30 | 0.58 | 0.30 | 0.30 |
| 165s | 0.30 | 0.30 | 0.30 | 0.30 |
| 180s | 0.30 | 0.30 | 0.30 | 0.30 |

### four_restock

| bucket | fast_eater | careful | slow_banker | average_eater |
|---|---|---|---|---|
| 15s | 0.90 | 0.86 | 0.65 | 0.74 |
| 30s | 1.10 | 1.15 | 0.30 | 0.99 |
| 45s | 1.35 | 1.15 | 0.62 | 1.12 |
| 60s | 0.30 | 1.21 | 0.62 | 1.26 |
| 75s | 0.62 | 1.26 | 0.53 | 1.35 |
| 90s | 0.53 | 0.30 | 0.65 | 1.38 |
| 105s | 0.53 | 0.54 | 0.84 | 1.45 |
| 120s | 0.33 | 0.30 | 0.87 | 1.45 |
| 135s | 0.48 | 0.52 | 0.42 | 0.30 |
| 150s | 0.56 | 0.54 | 0.42 | 0.30 |
| 165s | 0.30 | 0.30 | 0.47 | 0.33 |
| 180s | 0.30 | 0.30 | 0.30 | 0.33 |


## 多种子均值

种子：1, 2, 3, 4, 5；总分是全场合计，范围给出种子之间的波动。

| 场景 | 总分均值 | 总分范围 | 拾取件数均值 | 被吞均值 | 交分均值 |
|---|---|---|---|---|---|
| duel_equal | 1072 | 1070-1074 | 365 | 4.4 | 5.0 |
| triple_mixed | 1177 | 1177-1177 | 442 | 8.0 | 6.0 |
| four_mixed | 1201 | 1184-1238 | 450 | 10.6 | 5.0 |
| four_restock | 1224 | 1222-1224 | 519 | 13.0 | 9.6 |
## 时间去向（各场景合计，秒）

| 场景 | 第 1 | 第 2 | 第 3 |
|---|---|---|---|
| duel_equal | travel 176.5 | absorb 109.2 | bank_travel 50.8 |
| triple_mixed | travel 334.5 | absorb 144.0 | bank_travel 30.8 |
| four_mixed | travel 489.5 | absorb 149.8 | bank_travel 46.2 |
| four_restock | travel 419.0 | absorb 173.5 | bank_travel 73.0 |

## 参数 ±20% 敏感性（baseline `four_mixed`）

| 参数 | 原型值 | -20% 总分 | base 总分 | +20% 总分 | 最大|Δ| | 分差 -/base/+ | 含义 |
|---|---|---|---|---|---|---|---|
| radius_exponent | 0.3333 | 1003 | 1201 | 1302 | 16% | 302/294/511 | volume->radius shape; 1/3 is the documented sphere mapping |
| absorb_size_ratio | 1 | 1034 | 1201 | 1239 | 14% | 309/294/312 | item pickup gate vs ball diameter |
| magnet_absorb_multiplier | 2 | 1236 | 1201 | 1117 | 7% | 356/294/430 | magnet throughput |
| units_to_max_diameter | 1356 | 1195 | 1201 | 1141 | 5% | 284/294/306 | how much placeholder value the 0.30->2.20 m span costs |
| swallow_diameter_ratio | 1.35 | 1149 | 1201 | 1203 | 4% | 789/294/297 | PVP swallow threshold |
| absorb_items_per_s | 3 | 1172 | 1201 | 1208 | 2% | 301/294/310 | pickup throughput |
| base_speed_mps | 3 | 1205 | 1201 | 1179 | 2% | 497/294/312 | rolling speed |
| bank_cycle_s | 45 | 1191 | 1201 | 1171 | 2% | 311/294/304 | anchor rotation period |
| respawn_delay_s | 3 | 1194 | 1201 | 1182 | 2% | 302/294/310 | KO downtime |
| steal_fraction | 0.25 | 1195 | 1201 | 1224 | 2% | 291/294/264 | attacker share of victim unbanked volume |
| magnet_duration_s | 4 | 1218 | 1201 | 1177 | 2% | 289/294/316 | magnet uptime |
| bank_service_s | 0.5 | 1201 | 1201 | 1205 | 0% | 294/294/302 | deposit dwell |
| respawn_protection_s | 2 | 1201 | 1201 | 1201 | 0% | 294/294/294 | post-respawn immunity |
| rocket_duration_s | 2.5 | 1201 | 1201 | 1201 | 0% | 294/294/294 | rocket uptime |
| rocket_speed_multiplier | 1.5 | 1201 | 1201 | 1201 | 0% | 294/294/294 | rocket speed |

## 读法

- 总分 = 全场所有玩家已交存单位之和；不是单人成绩。
- 被吞次数 = 被吞事件数（每名受害者计一次）。
- 曲线在某 15 s 桶突然掉回 0.30 m 表示一次交分或一次被吞后的重生。
- 模型假设：玩家在区域内以固定吞吐拾取、按球径门槛过滤；区域库存共享；被吞/交分后回到起始球径。详见 `README.md`。

**PROVISIONAL_NOT_TESTED**：任何数字都不得作为平衡 PASS 或实测证据。
