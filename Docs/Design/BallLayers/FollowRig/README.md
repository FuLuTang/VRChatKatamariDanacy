# T55 球层：跟随参考帧 FollowRig

## Unity 实现

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/FollowRig/FollowRigMath.cs`：`public static class FollowRigMath.TryUpdate`，纯静态、无静态字段，成功返回 `true`；输入非有限、`min>max` 或滤波指数溢出时返回 `false`（对应 Python 的 `ValueError`/`OverflowError`），失败不写输出，调用方保留旧状态。

- 单位/坐标：位置为米，yaw 为绕 +Y 的度，输出规范到 `[0,360)`；帧只有位置 + yaw，没有 roll/pitch，也没有球四元数、自转、HMD、控制器或 Camera 输入。
- 状态在调用方：`hasPreviousFrame` 与上一帧位置/yaw/偏移作为参数传入并回写。`resetToTeleport`（或首帧）立即贴合新参考，不跨地图插值。`previousRigWorldPosition` 只读 x/z，y 永远按“当前球 Y + 眼高偏移”重算。
- 滤波：`alpha = 1 - exp(-2*pi*hz*dt)`（与 Python 的 `-expm1(-exponent)` 等价；float 下指数小于约 `6e-8` 时 alpha 会舍入为 0），x/z、偏移共用高度 alpha，yaw 用 yaw alpha 并走最短弧。眼高先夹到调用方给的 `[min,max]`，滤波后再夹一次。
- 测试：`Tests/EditMode/BallLayers/FollowRigTests.cs` 覆盖 `Docs/tests/ball_follow_rig/test_follow_rig.py` 的 8 个用例，另加“原地旋转球不改变帧”“瞬移后保持有界”两个不变量。未在 Unity、UdonSharp 或 VR 中运行，5Hz/8Hz 仍只是旧 T46 起始值。
