# BallPlayground（首个可玩切片）

用来看「球滚过去 → 吃掉道具 → 长大」这条链路是否成立的最小场景。它验证 **组合与数值**，不验证手感；转向、档位、交分都不在这里评价。

## 场景里有什么

| 对象 | 作用 |
| --- | --- |
| `BallPlayground`（根） | 挂 `BallPlaygroundBootstrap`、`VRC.Core.PipelineManager`、HUD、跟随相机 |
| `BallPlayground/MotorHost` | **只挂** `KatamariBallMotor`，独占一个 GameObject（见 `Docs/Design/BallMotor/README.md` 的装配约束） |
| `BallPhysicsRoot` → `BallVisualRollRoot` → `BallVisual` | motor 分别写位置、旋转；bootstrap 按语义直径缩放视觉球 |
| `Prop_00..23` | 可卷方块，各自挂 `PlaygroundProp`，向 motor 提交候选 |
| 地面 / 4 面墙 / 3 个障碍 | 墙与障碍通过 `BoxCollider` 注册给 motor 的 sweep |
| `SceneDescriptor` + `Spawn` | ClientSim 启动所需 |

## 跑一次看什么

进入 Play Mode（ClientSim）后跑约 20 s，控制台每秒一行：

```
[BallPlayground] t=5.1 d=0.966 absorbed=10 ... events=10 center=(0.00, 0.48, 16.93)
```

判据：`absorbed` 增长、`d` 上升、`events` 与 `absorbed` 同步。脚本化时间线全程直行（`turn=0`），保证每次运行轨迹一致。

## 这个场景教过的两件事

1. **`RejectNoGain`**：价值 < 1 单位的道具以前会「闸门接受 → claim → `commitRow=-1` → 静默消失」。现在它会以 `noGain` 出现在每秒的 `reject` 里。看到 `noGain` 就是**场景数据错了**（`valueUnits` 没写进去或 <1），不是判定错。
2. **装配约束（项目惯例，不是引擎限制）**：motor 独占一个 GameObject，理由是职责边界清楚、自检可读；同挂是**合法**的（官方 FAQ 答 "Yes."），真正同物体的硬约束是同步模式 —— 见 `Docs/Design/BallMotor/README.md` 的装配约束。这里以前写成「同挂会让其中一个跑不起来」，2026-11-15 撤回。`BallPlaygroundBootstrap.Start()` 的自检是用来抓「组件在、却什么都不跑」，跟同挂无关。

## 前置条件

场景是脚本搭的，**必须**有 `PipelineManager` 与 `SceneDescriptor.ReferenceCamera`，否则 Play Mode 正常但构建必挂 —— 见 [../README.md](../README.md)。
