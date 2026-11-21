# 剧情模式回合规则

单人剧情关的回合规则。代码：`Runtime/Story/StoryRoundController.cs`（回合核心）、`StoryStarMath.cs`（星级算术）、`StoryResultPresenter.cs`（结果卡）、`StoryRoundMath.cs`（时钟）。关卡清单与每关的回合参数在 [hub_levels.json](hub_levels.json)（`round` 块），大厅与换关见 [HUB_ROOM](HUB_ROOM.md)。证据等级：规则本身是项目选择（`PROVISIONAL_NOT_TESTED`），原作的过关类型对应 `CLEAR_TYPE`。

## 1. 与对战（KatamariBall）的区别

- 剧情模式**没有交分**：不建锚点、不产生 bank 事务、不缩小球，也没有「未交存 / 已交存」两本账。实现上由关卡数据显式关掉，而不是「没人走进去」。
- 单人、无对手、无吞噬；球只增不减。
- 分数不进 PvP 账本：剧情只读同一套「确认球径」/确认吸附件数，不写积分与交存字段。
- 输入、摄像机、舒适性、吸收门槛、成长公式与体积→半径换算与对战共用同一套，不各写一份。

## 2. 过关类型（`StoryGoalType`，对应原作 `CLEAR_TYPE`）

| 值 | 判定 | 状态 |
|---|---|---|
| `A` | 到时比较球径（碰撞直径 ≥ `targetDiameterM`） | 已实现 |
| `B` | 到时比较指定类别/定义的**确认吸附件数** ≥ `targetCount`（`countGoalKind`，或 `hub_levels.json` 的 `countDefinitions`） | 已实现 |
| `F` | 自由练习，不自动结算（`kind: freeRoam`） | 已实现 |
| `N/C/D/E/X` | — | 未实现 |

一关只用一种过关类型。件数只认确认吸附（同一实例只计一次，撞掉不计、不倒扣）。

## 3. 回合流程

1. 状态机：`StateIdle → StateRunning → StatePresenting → StateResult`；重试在 `Result` 后才可按（`Presenting` 期间重试会跳过结果卡）。
2. 计时从坐上球开始（`startOnLoad=false` 时 Idle 等玩家点击）；限时默认 120 s，每关可在 `round.timeLimitSeconds` 覆盖。
3. 计时归零 → 冻结球与吸附 → 呈球 `presentingSeconds`（表现事件，不写账本、不给道具、不改球径）→ 评估 → 结果卡。
4. **达成目标不提前结束**：达成后 HUD 翻成「已达成」，剩余时间继续变大、冲更高星。
5. 结算瞬间回合核心把事实冻结成只读值：`GetResultDiameterM()`、`GetResultCount()`、`GetState()`、`GetRemainingSeconds()`、`GetGoalType()`、`GetTargetDiameterM()`、`GetTargetCount()`。核心**不认识星级**。

## 4. 评估（可选模块）

星级只由结算瞬间那一个数（球径或件数）决定，不看过程与路线：

```
星级 = 满足的最高一档；0 星 = 未达成（可重试）
```

- 第 1 档 = 委托目标本身，第 2、3 档由 presenter 上的字段给出（球径 `star1/2/3DiameterM` 默认 1.25 / 1.45 / 1.60，件数 `star1/2/3Count` 默认 10 / 20 / 30），各档逐级严格递增，比较含等号。
- 「差一点」评语：未达成但落在目标值的 `partialBand`（默认 0.9）以内时只换评语文案，不改星级。
- 评语（第 1 关）：0 星「……就这样？朕的野餐会还没开始呢。再来一次。」／1 星「嗯，勉强能看。」／2 星「不错不错，有点样子了。」／3 星「好！这才是朕要的球！」
- 依赖单向：`StoryRoundController` 不引用 presenter；场景里不挂 presenter 是受支持的配置（只有回合与计时，没有结果卡）。每关是否有星由 presenter 的 `levelHasStars[]` 与 `hub_levels.json` 的 `stars` 决定。

## 5. 重试

- 无限重试，无惩罚，不记失败次数、不扣星、不进 PvP 账本。
- 重试 = 完整重置：球回起始直径、计时归零、地图物件回到初始世代（`TryResetForNewRound`，见 [BallMotor README](../BallMotor/README.md)「重试（reset）」）。重置只影响本关的物件账本。

## 6. 不做

对手/CPU、观战、交分、锚点轮换、最小兑换体积、「被吞/重生保护」、排行榜、连胜、`bankedScore`。

## 7. 第 1 关与第 2 关的作者参数

| 项 | 第 1 关 `story_01_bench_size`「长凳那么大」 | 第 2 关「给朕卷十件水果」（`countGoalKind = "fruit"`） |
|---|---|---|
| 过关类型 | A | B |
| 目标 | 球径 ≥ 1.25 m（一口吞下对角门上的长凳） | 确认吸附 `fruit` 类 ≥ 10 件 |
| 星级 | ★ 1.25 / ★★ 1.45 / ★★★ 1.60 m | ★ 10 / ★★ 20 / ★★★ 30 件 |
| 限时 | 120 s | 120 s |

国王委托文案（第 1 关）：「唔……朕的野餐会，好像少了点热闹。你瞧那张长凳——横在草坪的对角门上，谁也别想从那儿过。去，把那个土团滚起来，滚到一米二五那么肥！肥到能把那张长凳一口吞下去。办到了，朕就赏你星光；办不到……哼，朕就当没看见你。」

第 2 关文案：「朕今天要吃水果！听清楚了——是水果。不是草籽，不是饼干渣，更不是盘子。给朕卷十件回来：苹果、梨、橙子、香蕉、草莓、葡萄……要是你能摸到大西瓜，朕就更高兴了。朕的胃口会越吃越大。二十件？三十件？你自己看着办。」

`fruit` 的定义集合：apple、banana、grapes、orange、pear、strawberry、watermelon 七个 `definition_id`。

可达性与星级数值（成长算术）见 [LEVEL_CONTENT_PLAN](LEVEL_CONTENT_PLAN.md) 与 [README](README.md)「全局球径上限与第一关星级带」；吸收门口径见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。
