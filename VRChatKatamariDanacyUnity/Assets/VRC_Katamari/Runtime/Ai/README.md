# CPU 补位（Runtime/Ai）

放在 `Runtime/Ai/` 而不是 `Docs/Design/`：`CpuBallPilot` 与 `CpuSkirmishBootstrap` 是随场景运行的 U# 代码，规则和代码放一起最不容易脱节；设计层的取舍仍记在 `Docs/Design/BallMotor/README.md`。

**这是项目自选实现，不是原作 AI 的还原**——没有公开资料描述原作 CPU 如何选目标。

## 1. 一句话结论

**比例转向 + 恒定速度会形成半径 `r = v / ω` 的稳定圆轨道；半径必须小于 `reach` 才可能吸收。**

## 2. 参数关系与当前取值

| 量 | 含义 | 当前值 |
| --- | --- | --- |
| `adjustForward` | 调整段（未对准）的前进量 | **0.1** |
| `turnFullScaleDegrees` | `turn = clamp(delta / 该值, ±1)` | 45° |
| `turnCap` | motor 的最大角速度（`turnRateDegreesPerSecond`） | 110 °/s |
| `ω` | 实际角速度 = `turnCap · |turn|` | 转向时 ≈ 1.8 rad/s |
| `v` | 实测平面速度 | 0.40–0.54 m/s |
| `r` | 轨道半径 ≈ `v / ω` | **≈ 0.15–0.30 m** |
| `reach` | `collisionRadius + contactRadius + itemRadius` | 0.24–0.58 m（小件 ≈ 0.28 m） |

**这是用算术定参数，不是调参凑结果**：`adjustForward` 从 0.2 降到 0.1 之前，实测 `v=0.54`、`ω≈1.82` → `r≈0.30 m`，恰好卡在 `reach=0.279` 之外；减半后 `v≈0.40` → `r≈0.22 m`，稳进 reach，随即开始吸收。

## 3. 三形态判读（实际用过的表）

| 形态 | 观测特征 | 结论 |
| --- | --- | --- |
| **稳定轨道** | `err / delta / \|v\|` 连续多秒一位小数不变；`pos` 沿固定半径画圈；`d3d` 恒略大于 `reach` | 控制律问题：`r = v/ω` 太大，降 `adjustForward` 或提 ω |
| **过冲摆动** | `delta` 在相邻采样间大幅跳变（如 -8° → -102°）；`\|v\|` 在 0 / 0.7 间跳 | 一边跑一边修航向 → 改为对准后 `turn = 0` 直冲 |
| **直冲成功** | `d3d < reach` 出现，`absorbed` 增加 | 收敛 |

## 4. 为什么不用 lateral 横移

`Runtime/Motion/BallMotionMath.cs` 的注释写明：**`lateral input with no forward input does not sustain sideways travel`** —— lateral 是辅助量（有界速度补偿），不是横移。用它"侧滑到目标"在模型里不成立。

## 5. 道具 value 必须是整数（硬契约）

`PickupAbsorbMath.TryAbsorbValueToLedgerGain` 只接受**整数单位**：

```csharp
int truncated = (int)itemValue;
if ((float)truncated != itemValue) return false;   // 小数 → 拒绝
```

小数会被判 `RejectNoGain` 并以每球每秒的 `rejTarget=7(noGain)` 大声报出来。这是**有意的数据契约**，不是缺陷——本项目为此白跑一轮（道具值 `s³×300` 全是小数 1.0125 / 2.4 / 4.6875…，于是每件都被判 noGain）。新建场景时**把 value 取整**（`max(1, round(s³×300))`）。

## 6. 日志与探针原则

每球每秒**一行**，由 `CpuSkirmishBootstrap` 打印，`CpuBallPilot` 只提供 getter。原则：**印事实，不印聚合量/布尔判断**。

- `q=[P1_0@0.37,P1_1@0.51]`：队列里真实的 id 与提交年龄；
- `d3d=` / `reach=`：motor 自己存的三维距离与门限；
- `rejTarget=` / `pv=`：**只针对目标件**的拒绝原因与 motor 实际收到的值。

反面教材：曾用「目标名是否为队列中某个 id」的布尔量，而 id 命名是 `p1-0`、GameObject 名是 `P1_0`，探针恒为假，白跑两轮；也曾用「该 motor 最后一条拒绝」代表目标件的拒绝，得到与数据矛盾的 `noGain`。两次都是聚合量/判定的错。
