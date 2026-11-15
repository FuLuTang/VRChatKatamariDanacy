# 可吸尺寸门槛现状审计（供 Lead 决定是否改比例）

> **只读审计 / READ-ONLY AUDIT**，2026-11-15。作者不是 Unity 操作者：没有编译、没有 Play Mode、没有 `run_tests`；本文的 C# 结论全部来自代码阅读与手算，**未做运行时验证**。文中每个数字都标了来源类别：
>
> - **【代码事实】** 直接读到/从代码常量算出的；
> - **【文档数据】** 来自 `Docs/` 里的表与 JSON，没有回读 Unity 场景（除第 1 节的序列化值，那几处是 grep 实测）；
> - **【推断】** 我的判断，可能有错，别当结论引用。
>
> 触发问题：`absorbSizeRatio`（吸收比例）现在是 `1.0`，是否要调成 1.25 / 1.5 / 2.0。本文只盘现状与后果，**不给推荐值**。

## 0. 结论摘要

1. **判定量是 `bounds` 的最大全长**（AABB 最长边），不是三轴、不是对角线、不是包围球。【代码事实】
2. **门槛 = `min(8, 语义直径 × absorbSizeRatio)`**；序列化默认 1.0，三个 lab 场景与一个 prefab 的**实际序列化值全是 1**，没有例外。【代码事实】
3. 六个状态下当前允许的最大边长就等于球径本身（0.10/0.20/0.50/0.72/1.00/1.45 m）；比例 `r` 不会撞上 8 m 天花板，直到球径 > `8/r`（6.4 / 5.33 / 4.0 m），远超 2.20 m 满径。【代码事实】
4. **最大的副作用不是"能吃更大"，是节奏**：把 `r` 提到 1.5 后，**"开局球能吸的物件"合计 276 单位 > 关卡 1 目标 1.25 m 所需 245.99 单位**——尺寸门对目标达成的排序作用归零。`r = 1.25` 时是 228 < 245.99，**还差一件 0.55/0.58 的物件**，排序作用勉强保住。【代码事实 + 文档数据】
5. **挡路门确实与吸收门同源，但挡路功能今天没有接线**：`IsBeyondAbsorbLimit` 与 `BlockingPropMath` 在运行期**只有测试调用者**。所以调高比例**不会让今天更穿模**（今天本来就穿），但会缩短 BLOCKING_PROPS_PLAN 所设计的挡路窗口。【代码事实】
6. **还有一个比比例更上游的坑**：`collapsed_bench` 目录尺寸 3.6 m、门数据按 1.25 m 写，调和机制是名字后缀 `@size=1.25`，而**目前没有任何场景或构建脚本产出这个后缀**。忘了它，4 道对角门在 1.0/1.25/1.5 三档下都永远不开（需要 r ≥ 1.64）。【代码事实】

## 1. 现值与出处

| 量 | 现值 | 出处 | 性质 |
|---|---:|---|---|
| `absorbSizeRatio` | `1f` | `KatamariBallMotor.cs:121`（序列化默认） | PROVISIONAL 玩法阈值，决定 (h) |
| `maxItemSizeWorldM` | `8f` | `KatamariBallMotor.cs:120` | **只是安全上界**，不是玩法阈值 |
| `contactRadiusWorldM` | `0f` | `KatamariBallMotor.cs:112` | 决定 (i)：必须实质接触 |
| 每步合成 | `limit = min(8, D语义 × ratio)` | `PickupAbsorbMath.cs:270-287`，`min` 在 `:285` | 唯一合成点 |
| 比较 | `max(x,y,z) of bounds > limit ⇒ 拒` | `PickupAbsorbMath.cs:169-170` | 唯一比较点（严格大于） |
| 装配层调用 | 每固定步对每条在队候选 | `KatamariBallMotor.cs:762-766`（`EvaluateAbsorbWithValueAndBallSize`） | 每步重算，用当步语义直径 |

**场景/prefab 里序列化的实际值（grep 实测，全部为 8 / 1，没有旧值 1 m 残留）：**

| 资产 | 行 | `maxItemSizeWorldM` | `absorbSizeRatio` |
|---|---|---:|---:|
| `Labs/StoryLevel/StoryLevel.unity` | 12448-12449 | 8 | 1 |
| `Labs/BallPlayground/BallPlayground.unity` | 4315-4316 | 8 | 1 |
| `Labs/BallPlayground/BallPlayground.prefab` | 83-84 | 8 | 1 |
| `Labs/CpuSkirmish/CpuSkirmish.unity` | 434-435 | 8 | 1 |
| 同上（第 2 个电机） | 2442-2443 | 8 | 1 |
| 同上（第 3 个） | 4144-4145 | 8 | 1 |
| 同上（第 4 个） | 5321-5322 | 8 | 1 |

> **要改比例，这 7 处序列化覆盖都要改**——`[SerializeField]` 的默认值只在"没有场景覆盖"时生效，改 `:121` 一行不会改变任何现有场景的行为。这是最容易做成"改了没反应"的地方。

### 1.1 顺手的发现：文档行号已经漂了

`Docs/Design/PickupAbsorb/README.md` §1.1 与 `Docs/OPEN_DECISIONS.md` (h) 里挂的行号已经不是当前行号（文件后来加过函数，不是统一偏移）。本次审计按符号名复核，实际如下：

| 文档写的 | 现在实际 |
|---|---|
| 合成 `PickupAbsorbMath.cs:221-238` | `:270-287`（`min` 在 `:285`） |
| 尺寸门 `:163-164` | `:169-170` |
| reach `:186` / 比较 `:188` | 拼装在 `:233`（`TryComputeReach`），比较在 `:189` |
| `itemRadius` `:178-184` | `:226-231`（`TryComputeReach` 内） |
| 判定调用 `KatamariBallMotor.cs:749-753` | `:762-766` |
| `GetSemanticDiameterWorldM :1588` | `:1605` |
| `GetAbsorbSizeLimitWorldM :1682` | `:1664`（`BLOCKING_PROPS_PLAN` 的 `:1647-1651` 同样过时） |

`contactRadiusWorldM :112`、`maxItemSizeWorldM/:ratio :120-121` 仍然对得上。本文下面一律用**当前**行号。

## 2. 代码行为：判定用的是哪个量

【代码事实】`EvaluateAbsorb`（`PickupAbsorbMath.cs:137`）在尺寸门这一步只有两行：

```csharp
float largestExtent = Mathf.Max(itemBounds.x, Mathf.Max(itemBounds.y, itemBounds.z));   // :169
if (largestExtent > maxItemSizeWorldM) return AbsorbOutcome.RejectItemTooLarge;        // :170
```

`maxItemSizeWorldM` 是**调用方传进来的已合成上限**（相对入口在 `:345-347` 先算好再传）。所以：

| 候选量 | 是不是门用的 | 证据 |
|---|---|---|
| **AABB 三边中的最大者（全长，米）** | **是** | `:169-170`，`IsBeyondAbsorbLimit` 里同一行 `:311-312` |
| 三轴分别比较 | 否 | 只取 `Max`，`y`/`z` 短一点不影响结论 |
| 包围盒对角线 | 否 | 代码里没有对角线 |
| 包围球半径 `√((x/2)²+(y/2)²+(z/2)²)` | 否（那是 **reach** 用的） | `:226-231`、`:233` |
| 碰撞体尺寸 / renderer 尺寸 | 否 | 本层从不读 Collider；`bounds` 由宿主填 |

`bounds` 的语义是**全长**（三个方向的总尺寸），不是半长：`PickupAbsorbMath.cs:46-54` 与 `BlockingPropMath.cs:15-19` 都写明了。宿主把 Collider 的 `size`（全长）当 `bounds` 传是对的，把 `extents`（半长）传进去会**放宽一倍**。实验室宿主填的是 `(semanticDiameter, semanticDiameter, semanticDiameter)`（`PlaygroundProp.cs:116`），即立方体近似。

**用的是哪个"球径"：** 门的输入是 `semanticDiameterWorldM`（`KatamariBallMotor.cs:1605` 的同源读数），**不是**碰撞半径。在当前默认 `collisionToCoreRadiusRatio = 1`、`semanticDiameterPerCoreDiameter = 1`（`:108-109`）下，`SizeModelMath.cs:179-180` 给出 `semantic = 2 × core`、`collision = core`，于是

```
limit = 2 × R碰撞 × absorbSizeRatio
```

即 ratio 1.0 时"球能吸的东西最长边 = 碰撞直径 = 语义直径"。改成 1.25 就是"最长边 2.5 × 碰撞半径"。这条换算方便用 `GetCollisionRadiusWorldM()`（`:1600`）在日志里交叉核对。

**门序**：尺寸门是第 4 门，排在"可吸附标记"与"种类白名单"之后、"成长上限"与 reach 之前；比较是严格 `>`，**恰等于上限放行**，且本层不加 epsilon。【代码事实】

**reach 不会掩盖尺寸门。【推断】** reach = `R + contactR + 0.866·s`（立方体），而"真的贴住"时中心距 ≤ `R + s/2 < reach`，所以**凡是能碰到的大件都在 reach 内**，大件被拒的原因只可能是尺寸门。反过来说，调高比例对"吸不到"的改善不会被 reach 吃掉；但 reach 也不会因为你调了比例而变松（它仍用 `collisionRadiusWorldM`，与 ratio 无关）。

## 3. 六状态：当前门槛允许的最大物件边长

【代码事实】`limit = min(8, D × 1.0)`，8 远未触顶，所以六状态就是 `limit = D`：

| 球语义直径 D (m) | 0.10 | 0.20 | 0.50 | 0.72 | 1.00 | 1.45 |
|---|---:|---:|---:|---:|---:|---:|
| 当前上限（最大可吸边长，m） | **0.100** | **0.200** | **0.500** | **0.720** | **1.000** | **1.450** |
| 对应碰撞半径 R = D/2 | 0.05 | 0.10 | 0.25 | 0.36 | 0.50 | 0.725 |
| 上限 ÷ R | 2.0 | 2.0 | 2.0 | 2.0 | 2.0 | 2.0 |

算式：`limit(D, r) = min(8, D × r)`，`r = 1`。

> **可达性提醒【代码事实 + 推断】**：`D = 0.10 / 0.20` 低于起始球 0.29974 m（`D(0)` 见 §3.1），正常玩法里出现不了（交分缩球的地板也是起始值，`post = max(start, …)`）。保留这两行是因为测试里会构造小球；**不要拿它们当"开局可吃"的证据**。

### 3.1 成长→直径的换算（后面所有"要多少单位"都用它）

【代码事实】`baselineVolumeUnits = 3.4390244`、`cubicMetersPerVolumeUnit = 0.0041`（`KatamariBallMotor.cs:105-106`），`V = (3.4390244 + U) × 0.0041`，`D = 2·(3V/4π)^(1/3)`：

```
D(0)    = 0.29974 m        D(1356) = 2.1998 m（=2.20）
U(D)    = (π D³ / 6) / 0.0041 − 3.4390244
```

自检：`U(0.72) = 44.23`、`U(0.90) = 89.66`、`U(1.25) = 245.99`，与 `LEVEL_CONTENT_PLAN` §7.2 的 44.23 / 89.66 / 245.99 逐位一致，说明这套换算是文档在用的那一套。

## 4. 三档比例对照表

【代码事实，按 §3 公式手算】上限单位米，`limit = min(8, D × r)`：

| D (m) | r = 1.0（今天） | r = 1.25 | r = 1.5 | r = 2.0 |
|---:|---:|---:|---:|---:|
| 0.10 | 0.100 | 0.125 | 0.150 | 0.200 |
| 0.20 | 0.200 | 0.250 | 0.300 | 0.400 |
| 0.50 | 0.500 | 0.625 | 0.750 | 1.000 |
| 0.72 | 0.720 | 0.900 | 1.080 | 1.440 |
| 1.00 | 1.000 | 1.250 | 1.500 | 2.000 |
| 1.45 | 1.450 | 1.812 | 2.175 | 2.900 |
| **开局球 0.29974** | 0.2997 | **0.3747** | **0.4496** | **0.5995** |
| 天花板生效的球径 | > 8.00 | > 6.40 | > 5.33 | > 4.00 |
| 满径 2.20 球的上限 | 2.200 | 2.750 | 3.300 | 4.400 |

读法：

- **天花板在六状态里一次都没生效**，2.20 m 满径也不生效（最紧的 r = 2.0 也只要 4.4 < 8）。所以这三档的差别是纯倍率，没有第二个拐点。
- 天花板真正开始咬人的时候是 `r > 8 / 2.20 = 3.64`（满径球）。想用 `maxItemSizeWorldM` 兜住"配错比例吞房子"，它守的是 8 m 这条绝对线，不是"相对球的几倍"。
- 开局那一行最能说明问题：`r = 2.0` 时开局球（0.30 m）就能吸 **0.5995 m** 的物件——接近它自己直径的两倍。

## 5. 副作用分析

### 5.1 挡路判定与吸收门同源，但挡路**今天没有接线**

【代码事实】

- `PickupAbsorbMath.IsBeyondAbsorbLimit(bounds, limit)`（`:306-313`）与尺寸门是**同一个比较**（`:311-312` vs `:169-170`，都是 `Max(x,y,z) > limit`，都含等号在"可吸"一侧）。
- 但全仓非测试引用为 **0**：`grep IsBeyondAbsorbLimit` 只命中 `PickupAbsorbMath.cs` 自身与 `Tests/EditMode/BlockingPropMathTests.cs`；`BlockingPropMath` 类（`BlockingPropMath.cs:24`）同样只有测试调用者；电机里**没有** `SetBlockingBoxes` 之类接口。
- 挡路方案在 `Docs/Design/PickupAbsorb/BLOCKING_PROPS_PLAN.md` 里还是 `PROVISIONAL_NOT_TESTED` 的**方案**（§0 结论 1、§5.3 代码清单）。方案承诺"只有一条门槛"（§0 结论 3），开关时刻与吸收门重合。

**所以"比例变大 → 挡路窗口缩小 → 穿模变多"这条：今天不成立为"变更"，因为今天所有吸不动的道具本来就穿过去**（`BLOCKING_PROPS_PLAN` 的触发记录就是"球直接穿过去"）。它成立为**对未来方案的削弱**：

【文档数据 + 代码事实】按方案的单门槛，某件 `extent = s` 的物件在 `s > r·D` 时挡路，即**挡到球径 `D = s/r` 为止**。用它扫本切片的 110 件（`LEVEL_CONTENT_PLAN` §2.1 的 bounds 表）：

| 球径 D (m) | r = 1.0 挡路件数 | r = 1.25 | r = 1.5 | r = 2.0 |
|---:|---:|---:|---:|---:|
| 0.30（开局） | **72** | 32 | 20 | 7 |
| 0.50 | 12 | 7 | 4 | 0 |
| 0.72 | 4 | 4 | 0 | 0 |
| 1.00 | 0 | 0 | 0 | 0 |

读法：挡路容量设计是 **6 格**（方案 §0 结论 2）。`r = 1.0` 开局有 72 件竞争 6 格（容量是瓶颈）；`r = 2.0` 开局只剩 7 件（容量几乎不再是瓶颈，但"大件把路堵住"这件事基本消失）。**挡路保护的窗口整体按 `1/r` 缩短**：同一件物件从"能挡路"变成"能吸走"的球径比今天低 20% / 33% / 50%。

**更隐蔽的一条【推断，本次最值得复核】**:挡路谓词只看尺寸，而实际吸收还要过另外四道门（可吸附标记、种类白名单、成长上限、价值整数/`RejectNoGain`）。因此存在这样一批物件：**尺寸上已经不挡路了，但因为白名单漏配 / value 为 0 / 队列满 / 账本不在 Playing 而永远吸不掉**——它们会从"挡路"直接变成"穿模"。`r` 越大，这批"门内但吃不掉"的物件越早出现。方案 §5.3 的宿主筛选里带了 `submittable`，能挡住白名单那一类；**value 为 0 / 非整数**这一类它挡不住（`Docs/OPEN_DECISIONS.md` (j)：0 或小数会以 `RejectNoGain` 拒收）。

### 5.2 成长速度：节奏会被压平，而且能定量

【代码事实 + 文档数据】用 §2.1 的 110 件（412 单位）逐件算"当前球径下门允许的物件合计"，得到每个比例下**开局就能吸到的单位池**：

| 比例 r | 开局上限 (m) | 开局可吸件数 | 开局可吸单位 | 占全图 412 单位 | 门对全图失效的球径/单位 |
|---|---:|---:|---:|---:|---|
| 1.0 | 0.2997 | 38 | **68** | 17% | D = 0.90 / **89.66 u** |
| 1.25 | 0.3747 | 78 | **228** | 55% | D = 0.72 / 44.23 u |
| 1.5 | 0.4496 | 90 | **276** | 67% | D = 0.60 / 24.15 u |
| 2.0 | 0.5995 | 103 | **336** | 82% | D = 0.45 / 8.20 u |

"门对全图失效"= 全图最大件（`thermos` 0.90 m）也被放行的那一刻。逐件解锁点（`U` 为所需确认单位，负数表示**开局就够**）：

| 物件（bounds m） | r = 1.0 | r = 1.25 | r = 1.5 | r = 2.0 |
|---|---:|---:|---:|---:|
| thermos 0.90 | 89.66 | 44.23 | 24.15 | **8.20** |
| watermelon 0.72 | 44.23 | 20.97 | 10.68 | **2.52** |
| watering_can 0.58 | 21.48 | 9.32 | 3.94 | **≤0** |
| plate 0.50 | 12.52 | 4.73 | 1.29 | **≤0** |
| banana 0.45 | 8.20 | 2.52 | 0.01 | **≤0** |
| sandwich 0.38 | 3.57 | 0.15 | **≤0** | **≤0** |

**这不是"价值跃迁突变"，是"中段消失"。** 三点必须说清：

1. **单位价值不随体积涨**：本切片的 value 是**按档位给的**（s = 1、m = 4、garden m = 6、container l = 16），不是按体积算的（§2.1 表 + `Docs/OPEN_DECISIONS.md` (j)）。所以调高比例**不会**让单件收益爆炸（最大单件仍 16 u），**不会**出现"1 分钟吃到一件 = 一件顶十件"的雪球。真正变的是**可选集合**。
2. **排序作用归零的临界点很清楚**：关卡 1 的 1 星是 `target_diameter_m = 1.25`（`Docs/Design/GameModes/Levels/story_01_bench_size.json`），需 **245.99 u**。`r = 1.5` 的开局池 **276 u > 245.99 u**，`r = 2.0` 是 336 u——**玩家可以只用开局门就允许的物件达标，尺寸门在这一关全程不参与决策**。`r = 1.25` 时开局池 228 u < 245.99 u，还差一件 0.55/0.58 的物件，门仍然"卡"了一次。
3. **中段的 m 档会被跳过**：`r = 2.0` 下 0.30 m 开局球直接吃 0.58 的浇水壶与 0.45 的香蕉，而 0.72 西瓜只要 2.52 u（约 1 件 m 档）。用「按价值贪心」的路径模拟（离线手算，非实测）看，`r = 2.0` 的前 10 件里就有 **4 件 16 u 的 thermos**；`r = 1.0` 时前 10 件最大只到 6 u 的浇水壶/盆栽。也就是说"先吃 4 单位、攒到西瓜、再攒到暖壶"这条设计好的三段式，在 `r ≥ 1.5` 时可以直接跳到终局件。

> 反面提醒：**以上只说明尺寸门的约束力，不代表通关时间**。到 1.25 m 还要跑图、过 reach、过 4 槽队列与 `absorb_items_per_s = 3` 的吞吐（`round_model.py:152`），这些参数与 ratio 无关。已有的唯一实测是 120 s 到 1.43 m（`LEVEL_CONTENT_PLAN` §7.4，n = 1），不能拿来外推比例改动后的秒数。

### 5.3 台沿 / 台阶 / 桌子腿

【代码事实】三件事要分开：

1. **物理高差（台沿、花圃框、台阶）来自静态 `SweepMath` 平面/盒**，由地图注册，**不读 `absorbSizeRatio`**。所以调比例**不会**让球翻过原本翻不过的台沿，也**不会**让它穿过静态墙。
2. **地图里两个"尺寸门"家族是作者数据，运行期不读**：`layout.json` 的 `size_gates` 8 条（4 × `gate_bench_*` 1.05→1.25 m、4 × `gate_bed_*` 0.95→1.10 m），只在编辑器灰盒与校验器里用（`PicnicLawnGreyboxBuilder.cs:1598` 检查 `absorbable ≥ blocks`），运行期 0 引用。`blocks_ball_diameter_below_m` 也是同一状况（`BLOCKING_PROPS_PLAN` R7 已记录）。
3. **道具本身没有碰撞**（球是 kinematic，`BLOCKING_PROPS_PLAN` F9），所以"桌子腿挡路"今天不存在，调比例也不会新增。

**真正会被比例改坏的是数据自洽【代码事实】：** 两个门家族的数字是**按 ratio = 1.0 写的**

- `gate_bench_*`：物件的 `absorbable_at_ball_diameter_m = 1.25`，含义是"球径 1.25 时按 1.0 比例刚好吸掉 1.25 m 的长凳"。比例改成 1.25，同一件在 **D = 1.00** 就被吃掉（124.27 u 而不是 245.99 u）；改成 1.5 是 **D = 0.833**（70.47 u）。
- 这一条直接撞上关卡 1 的叙事：1 星目标 1.25 m 的**理由**就是"达成那一刻正好吃掉挡路的长凳"（`Docs/Design/GameModes/README.md:202-219`）。比例一调，长凳在达标前很久就没了，"吃掉刚才挡路的东西"这个教学收尾落空，而关卡 JSON 的 1.25 不会报错、只会变成没有意义的数字。
- `gate_bed_*` 是 `climb` 型（不是 `absorb`），且 `flower_bed_frame` 的值为 0（`prop_catalog.csv`）——**即使比例大到能吸，也会以 `RejectNoGain` 拒收**。所以"吃掉花圃框抄近路"不成立；但这也说明**作者用 value = 0 在兜底**，而不是靠门槛。

**长凳的 3.6 m 与 1.25 m：机制已经有了，产出端还是空的【代码事实】:** `collapsed_bench` 在 `prop_catalog.csv` 里 `game_longest_dim_m = 3.6`，而门数据说"1.25 m 可吸"。这两件事**本来有一个指定的调和机制**：`PickupFieldAuthoringRules.DeclaredSizeMarker = "@size="`（`:95`），道具 GameObject 名字带 `@size=1.25` 时，编辑器同步把 `bounds` 记成边长 1.25 的立方体、而不是渲染器 AABB（`PickupFieldSync.cs` 的条目说明与 `PickupFieldAuthoringRules.cs:56-75` 的注释原文就拿长凳举例："a gate bench 3.2 m long but 1.25 m thick is recorded as 1.25 m, not 3.2 m"）。规则有单测（`PickupFieldAuthoringRulesTests.cs:156-172`）。

但是：**当前没有任何产出端写这个后缀**——`grep "@size="` 在 `*.unity` 里 0 命中，在 `Editor/` 里只命中规则与同步的读取方，`Tools/mapbuild/build_greybox.py` 也不生成。灰盒里的对象叫 `bench_block` / `gate_bench_*`。所以：

- 如果将来按"渲染器 AABB"同步那 4 张长凳，`bounds = 3.6`，那么 `D ≥ 3.6 m` 才能吸，而满径只有 2.20 m ⇒ **需要 r ≥ 3.6 / 2.20 = 1.636**；`r = 1.0 / 1.25 / 1.5` 全部救不了，对角门永远不开。
- 只有按约定把名字写成 `…@size=1.25`，§4 表里"1.25 刚好"才成立。

**这是本次审计发现的、比选比例更上游的一项**：比例怎么调都要先把长凳的 `bounds` 出口定死（要么命名带 `@size=1.25`，要么别指望门数据）。反过来说，如果谁想用"把比例调到 1.7"去修"长凳吸不到"，那他修的是 `bounds` 配错，代价是 §5.2 的节奏全丢——**别用比例去补数据**。

### 5.4 其他受影响面（逐条，短的）

- **PvP 的 1.35 不是同一个数**【代码事实】：`swallow_diameter_ratio = 1.35`（`round_model.py:141`）要求**攻击方直径 ≥ 受害方 × 1.35**（`:1054`、`:1255`），与"物件 ≤ 球径 × r"方向相反，不能混用。**但**比例一过 1.35，"道具比对手好吞"，与原作"物件是硬门槛、对手是偶发事件"的形状相反——**这是审美判断，不是证据，标【推断】**。
- **AI 不读尺寸门**【代码事实】：`CpuBallPilot` 只用球径做逃跑/交分判定（`:77`、`:82`、`:166`），不查 `GetAbsorbSizeLimitWorldM`。所以比例变化对 CPU 行为的影响只通过"球长得更快"间接发生，不需要改 AI。
- **档位分档不受影响**【代码事实】：作者规则按最长边分档，`SmallTierMaxExtentM = 0.21f`、`MediumTierMaxExtentM = 0.76f`（`PickupFieldAuthoringRules.cs:85-86`），与 ratio 无关；比例只改变"某档在某球径下能不能吃"。
- **决定 (b) 的 0.38 m 安全下限推导过期**【代码事实 + 推断】：`Docs/OPEN_DECISIONS.md` (b) 用"交分后 0.3625 m > m 档门槛 0.32 m"论证下限。那是**回合模型的 pickup 档位**，不是 `bounds`；比例一调，"0.3625 的球能吃到什么"整体变松，这条论证仍然成立但不再是紧约束。不改也不出错，只是别再用它当"为什么是 0.38"的完整理由。
- **`GetAbsorbSizeLimitWorldM()` 没有运行期消费者**【代码事实】：`:1664-1669` 的注释说"给宿主判断值不值得提交用"，但全仓无调用者（只有测试与 HUD 日志）。比例改大后，若将来有宿主拿它做筛选，它会自动跟着变——这是好事，但今天它只是一条诊断读数。

## 6. 受影响的测试清单

【代码事实】全仓只有两个测试文件直接提 `absorbSizeRatio`，另有一个间接用同一个比较。把**序列化默认值**改成 1.25 的后果：

| 文件 | 用例 | 写死了什么 | 改默认值后 |
|---|---|---|---|
| `Tests/EditMode/BallMotor/AbsorbSizeLimitTests.cs` | `TheCeilingIsAboveTheFullBallSize`（`:71-81`） | `Assert.AreEqual(1f, Field("absorbSizeRatio"), 1e-6f)`（`:80`） | **红**（直接断言默认值） |
| 同上 | `TheStartBallLimitIsItsOwnDiameter`（`:83-91`） | `limit == 语义直径`（`:90`） | **红**（0.2997 → 0.3747） |
| 同上 | `AFullyGrownBallCanTakeAnItemOverOneMetre`（`:93-110`） | `limit == 2.20`（`:108`） | **红**（→ 2.75） |
| 同上 | `TheRatioScalesTheLimitAndANonPositiveRatioIsRefused`（`:112-125`） | 显式 `SetField` 0.5 / 0 / −1 | 绿（不读默认值） |
| `Tests/EditMode/Pickup/AbsorbSizeGateTests.cs` | 全部 6 个用例（`AnExtentExactlyAtTheRelativeLimitIsAbsorbable :62`、`SmallBallRefusesTheItemAHugeBallAccepts :84`、`TheCeilingRefusesAnItemEvenForAHugeBall :112`、`RelativeLimitIsTheMinimumOfTheCeilingAndTheProduct :123`、`NonPositiveOrNonFiniteRatioIsInvalidInput :145`、`TheGainGateStillRunsAfterTheRelativeGate :179`） | 每个都显式传 `ratio`（`1f` 或故意非法的 `0f`，辅助函数 `:51-58`），**不读电机字段** | **绿**，但注释里"ratio 1.0 is the PROVISIONAL gameplay ratio"（`:87`、`:133`）会与现实脱节 |
| `Tests/EditMode/BlockingPropMathTests.cs` | `GrowthCrossingTheLimitFlipsTheSameFunction`（`:325-355`） | 显式 1f + 手算 1.20/1.25 | 绿，但"1.20 的球吃 1.20 的物件"这组数字不再代表现实 |
| 同上 | `ExtentEqualToTheLimitIsAbsorbable`、`ShrinkingRecomputesWithNoResidualState` | 显式 limit 值 | 绿 |

**没有任何测试读场景/prefab 的序列化覆盖**（§1 那 7 处）。也就是说：只改 `KatamariBallMotor.cs:121` 会让 3 个用例变红、但运行时**一点变化都没有**；只改 7 处场景而不改 `:121` 则测试全绿、新场景有效。两件事必须一起做（或明确只做其中一个并说明）。

## 7. 代码事实 vs 我的推断

**我读到的代码事实（可直接引用、可用同样命令复核）**

1. 门的量 = `max(bounds.x, bounds.y, bounds.z)`，严格 `>` 才拒（`:169-170`、`IsBeyondAbsorbLimit :311-312`）。
2. 上限 = `min(maxItemSizeWorldM, 语义直径 × ratio)`，唯一合成点 `:270-287`，非法 ratio（≤0、非有限）→ `RejectInvalidInput`。
3. 默认 `ratio = 1f`、`maxItemSizeWorldM = 8f`；3 个场景 + 1 个 prefab 共 7 处序列化值全是 8 / 1。
4. 每步重算，用当步 `semanticDiameterWorldM`（`KatamariBallMotor.cs:762-766`）。
5. 当前 `semantic = 2 × collision`（`:108-109` + `SizeModelMath.cs:179-180`）。
6. 挡路谓词与门同源，但 `IsBeyondAbsorbLimit` / `BlockingPropMath` 在运行期**无调用者**。
7. `layout.json` 的 `size_gates`（1.05→1.25 / 0.95→1.10）运行期不读。
8. 三个用例把 ratio 默认值 1.0 写死（§6 前三行）。
9. `bounds` 有官方覆盖机制 `@size=<米>`（`PickupFieldAuthoringRules.DeclaredSizeMarker :95`，同步侧 `PickupFieldSync.cs`），但**当前没有任何场景/构建脚本产出这个后缀**。

**我的推断（可错，别当结论）**

1. reach 不会掩盖尺寸门（贴住即可达），所以调比例的收益是"实"的。
2. 挡路门"只看尺寸"会造成"门内但吃不掉 → 穿模"的洞；比例越大洞越大。方案里的 `submittable` 只能堵住白名单那一半。
3. 1.25 m 关卡目标在 r ≥ 1.5 时可以由开局门合法物件凑够，因此尺寸门对该关失去排序作用。
4. 长凳那 4 道门在落地时如果忘了 `@size=1.25`，`bounds` 会是 3.6 m，届时只能靠 r ≥ 1.64 才吸得到——这会被误读成"比例太小"，其实该改的是命名。
5. PvP 1.35 与道具比例过近会改变"道具难/对手易"的观感——纯审美，无数据。
6. 六个状态里 0.10 / 0.20 实际不可达（低于起始球），只对测试有意义。

## 8. 不能声称的东西

- 没有编译、没有 EditMode/PlayMode 运行、没有 `run_tests`；本文没有验证任何行为的**运行时**表现。
- §5.2 的"件数/单位池"来自 `LEVEL_CONTENT_PLAN` §2.1 的表（文档数据），**没有回读 `StoryLevel.unity` 的 110 条 `propBounds`**。表内自洽（30 行、110 件、412 单位、小计列合计 412，与场景实测行文一致），但它终究是二手数据。
- "1 分钟跳过中段"是按门算的**可行性**，不是实测时长；唯一实测数字是 120 s → 1.43 m（n = 1），不能外推。
- 没有评估性能、网络、VR 舒适性；本次改动不涉及这些，但也不代表它们不受影响。

## 9. 一句话

**按我的分析，把 1.0 调高最大的风险是：尺寸门失去"节奏机制"的身份——`r = 1.5` 时开局球就够得着 276 单位（关卡 1 目标只要 245.99 单位），整关的门槛排序在这一刻归零，而关卡数据里所有 1.25/1.45/1.60 的目标与 `gate_bench_*` 的 1.05→1.25 全部是按 1.0 写的，会静默变成没有意义的数字。**
