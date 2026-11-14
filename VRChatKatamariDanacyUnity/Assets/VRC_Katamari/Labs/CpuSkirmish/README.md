# CpuSkirmish（1 玩家 + 3 CPU）

单机对战补位的实验场景：四个球各有自己的 `KatamariBallMotor`（各自独占一个 `MotorHost`），四条车道各有 6 个道具，球 1–3 由 `CpuBallPilot` 驱动，球 0 是玩家位（未接输入，静止）。

## 命名规则（务必遵守）

**道具的 `itemId` 必须等于它的 GameObject 名**（例如 `P1_3` ↔ `itemId = "P1_3"`）。

原因：`itemId` 是稳定身份，GameObject 名是可读标签。两者不一致时，任何"这个名字在不在队列里"的判断都会永远为假——本项目为此白跑了两轮（`itemId = "p1-0"` vs 名字 `P1_0`，探针恒报 `queued=0`）。新建场景时按这条来。

## 日志

每球每秒一行，**打印事实而不是比较结果**：

```
[skirmish] t=2.1 ball=1 name=cpu-1 target=P1_0 err=0.38 delta=-7.9 fwd=0.6 switches=0 q=[P1_0@0.84] |v|=0.05 d=0.299 absorbed=0 ...
```

- `q=[id@age,...]`：motor 队列里**真实的** id 与它们被提交了多久；
- `target`/`err`：pilot 追的件与它的实时水平距离；
- `delta`/`fwd`/`|v|`：航向误差、发出的前进量、实际速度。

判读：`q` 里有 `target` 且 `err < reach` 仍未吸收 → 判定/几何问题；`q` 里没有 → 入队问题；`q` 里有但 `err > reach` → 控制没到位。

## 前置条件

脚本搭的场景，必须有 `VRC.Core.PipelineManager` 与 `VRCSceneDescriptor.ReferenceCamera`，否则 Play Mode 正常但构建必挂 —— 见 [../README.md](../README.md)。
