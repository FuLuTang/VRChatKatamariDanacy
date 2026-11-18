# T55 球层：跟随参考帧 FollowRig

## Unity 实现

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/FollowRig/FollowRigMath.cs`：`public static class FollowRigMath.TryUpdate`，纯静态、无静态字段，成功返回 `true`；输入非有限、`min>max`、负半径/负系数、滤波指数或 alpha 越界时返回 `false`（对应 Python 的 `ValueError`/`OverflowError`），失败不写输出，调用方保留旧状态。

- 单位/坐标：位置为米，yaw 为绕 +Y 的度，输出规范到 `[0,360)`；帧只有位置 + yaw，没有 roll/pitch，也没有球四元数、自转、HMD、控制器或 Camera 输入。
- 取景几何（2026-11-15 定稿）：`eye = ballCentre − forward(滤波后的 yaw) · back + up · height`，`height = eyeBaseUpMeters + radius · eyeUpPerRadius`，`back = eyeBaseBackMeters + radius · eyeBackPerRadius`。三条护栏里 `eyeUpMinMeters` 是离球心高度的下限；`eyeUpMaxMeters` / `eyeMaxBackM` 只钳**加性底座**（`eyeBase*`），不钳半径项——球径上限 400 m 时站姿眼点在球心上 330.9 m、后 235.4 m，固定米数上限会把眼点钉进球里，半径项由球径上限本身约束。两项都从**球心**量起、都随半径线性放大：直径 0.10 / 0.30 / 0.72 / 1.43 m 四档的高度是 0.67 / 0.91 / 1.414 / 2.266 m、后退是 0.86 / 1.08 / 1.542 / 2.323 m（`BallMotor/README.md` 的「跟随架」有整张表）。后退方向沿**滤波后的 yaw**，所以偏移方向与写进 transform 的朝向不会打架。
- 状态在调用方：`hasPreviousFrame` 与上一帧 yaw/**离球心高度**作为参数传入并回写。`resetToTeleport`（或首帧）立即贴合新参考，不跨地图插值。x/z 每步按当前球心、后退距离与当步滤波 yaw 重算，不混入上一帧世界坐标；y 按「当前球 Y + 本次 height」重算。
- 滤波：`alpha = 1 − exp(−2π·hz·dt)`（C# 是 `1f - Mathf.Exp(-exponent)`；Python 用同一式子而不是 `-expm1`，因为要以 C# 的 float 行为为准）。高度用 height alpha，yaw 用 yaw alpha 并走最短弧；滤波后的高度只保底到 `eyeUpMinMeters`，上限就是当步目标（半径项不设固定天花板）。**唯一有意的精度差异**：C# 是 float32，`2π·hz·dt` 超过约 `3.4e38` 就溢出并拒绝；Python 是 double，要到约 `1.8e308` 才溢出。拒绝路径同一条，测试用真正 double 溢出的输入覆盖（见 `follow_rig.py` 模块 docstring）。
- 测试：`Tests/EditMode/BallLayers/FollowRigTests.cs` 与 `Docs/tests/ball_follow_rig/test_follow_rig.py`共同覆盖（10 例）短弧双向、滤波有界不超调、瞬移吸附并重置基线、护栏按加性底座解释、球后上方形（四档直径的手算期望值）、**400 m 球径上限下眼点在球外**、非法输入原子拒绝、巨大滤波积拒绝、接口形状只有位置+yaw、1000 步有限序列；C# 另有「平视时球在画面下缘的角度」「原地旋转球不改变帧」「瞬移后保持有界」；站姿 `RideViewTests` 另测多种 yaw 下眼点和补偿后的模拟头位均无侧偏。其中纯几何检查为：`back` 与 `height` 都随半径增长、且球面净空（height − radius）单调增。未在 Unity、UdonSharp 或 VR 中运行，5Hz/8Hz 仍只是起始值。

**骑球站姿与骑手尺度**：[RIDE_GEOMETRY.md](<RIDE_GEOMETRY.md>) + [ride_geometry.py](<ride_geometry.py>)。玩家站在球后、脚在地上、手从肩向下 45° 按在球背上推球：眼高 E = 2.6546·r，眼点在球心上 1.6546·r、后 1.1768·r（`RideViewMath`，装配器写进 motor 的四个字段，加性底座为 0）；骑手眼高按实测 η 反解（`RideScaleMath`、`RideStanceMath`），座位让脚落地；骑乘面板挂在随 E/1.64 缩放的 `RidePanels` 下。上面列出的 motor 默认系数（0.55 + 2.4r 等）是跟随架本身的字段默认值，故事场景里由骑乘装配器覆盖。
