# StoryMode 生长诊断（BallGrowthDiagnostics，commit d3cb411）

目的：用 `[ballview] [shell] [eye] [grow]` 四条线量「物理长大了、画面没长」的假设。
场景改动（**Play 之外**编辑并保存）：`BallView` 上加 `BallGrowthDiagnostics`，`motor=PlayerBall/MotorHost`、`coreMesh=PlayerBall/BallPhysicsRoot/BallVisualRollRoot/CoreMesh`、`attachmentRoot=…/AttachmentRoot`、`loggingEnabled=true`。
`BallView.useVisualCoreRadius=1`、`smoothCoreGrowth=1`（读回确认）。

## 读数（Play 头 12 s，v1，diameter 0.2997）

```
[ballview] mesh=0.2847 d_motor=0.2997      → mesh/d_motor = 0.9500
[shell] core=0.1423 shell=0.1423 swallowed=0 attached=0
[eye] app=0.0807 d_core=0.2847 dist=3.527
```

- **mesh / d_motor = 0.9500**，与 0.95 视觉半径设计值一致（`core radius 0.1423 = 0.95 × 0.1499`）。
- `[eye] app=0.0807`（core 直径 / 眼距 3.527 m）。
- `swallowed=0 attached=0`：球还没吸收任何东西。

## 未完成：>=5 次吸收

本轮**没能驱动吸收**：
1. 电机的世界位置由 U# 侧每步重写，从编辑器 C# 直接改 `PlayerBall/BallPhysicsRoot.position` 下一帧就被覆盖（`[story-lab] center` 一直是 (-12.50, 0.15, -12.50)）。
2. 把 props 移到球心再 `SendCustomEvent("ResetField")` 给 `PickupFieldHost`，`[grow]` 仍是 0 —— 拾取场在 Start 时缓存 prop 行（`propRows=130`），移动 Transform 不改缓存。
3. 编辑器侧无法伪造 Udon 输入事件（`DesktopMoveInput.InputMoveVertical` 走 Udon 输入管线），因此无法用 WASD 让球滚起来。

所以 `[grow]` 的每次吸收百分比这一轮**没有数据**；需要一次真人/输入驱动的 Play（或给实验室加一个脚本驱动的 probe）。
