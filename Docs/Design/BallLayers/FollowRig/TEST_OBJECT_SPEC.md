# T55 FollowRig 测试对象规格（未搭建）

## 独立对象与Prefab层级

建议名 `T55_FollowRig_HeadingLab`，未来作为单独可进入对象/Prefab；不成为滚动Mesh子节点，不使用真实HMD或相机输入。

```text
T55_FollowRig_HeadingLab (root; no gameplay/network collider)
├── TestPanel (球半径、标量yaw、目标/输出位置、滤波频率、高度/后退护栏与重置状态)
├── BallReference (只画位置球心/半径与朝向水平箭头；无自转读入)
│   └── HorizontalHeadingArrow (+Z前向起始方向)
├── FollowRigReference (独立于BallReference的空Transform/坐标架)
│   └── ForwardArrow (只随FollowRig yaw)
├── EyeHeightBand (球心上方 eyeUpMinMeters / eyeUpMaxMeters 两根线)
├── BackDistanceBand (球心后方 0 → eyeMaxBackM 的标尺)
├── TeleportStartMarker
├── TeleportEndMarker
└── RollDecoy (原地可旋转的非交互视觉球；不得作为Rig输入)
```

## 操作与预期几何

- 初始将水平heading设为359°，连续目标设为1°：输出应沿+2°短弧变化，不经过180°长路；反向1°→359°同理为-2°短弧。若yaw在滤波中间，**后退方向也要跟着滤波后的yaw转**：把球心固定在原点、yaw 0→90，输出帧的位移方向应是一段圆弧而不是先横移再转向。
- 把球半径依次设为0.05 / 0.15 / 0.36 / 0.715 m（直径0.10 / 0.30 / 0.72 / 1.43 m），每次单步吸附（reset）后读rig：相对**球心**的上方应是0.67 / 0.91 / 1.414 / 2.266 m，后方应是0.86 / 1.08 / 1.542 / 2.323 m（`height = 0.55 + 2.4·r`，`back = 0.75 + 2.2·r`）；两者随半径单调增，球面净空（height − r）也是0.62 / 0.76 / 1.054 / 1.551 m，随球变大而不是变小。这是本对象要证明的核心性质：旧「离球心常量眼高」在1.43 m球上只剩0.885 m净空，被吸附物糊满画面。
- 把球参考架沿水平移到远点：普通步逐渐追随，不超目标；按“重生/瞬移重置”后立即到新位置+yaw，无跨地图插值/抖动。
- 护栏：把`eyeBaseUpMeters`分别设成100 / 0.1 / 1.0（半径0.5 m、护栏0.5–1.5 m），Rig相对球心的高度应落在1.5 / 0.5 / 1.0；把`eyeMaxBackM`设成1.0时，1.85 m的后退应被钳到1.0。
- 旋转`RollDecoy`而保持位置、半径与水平yaw不变：FollowRig箭头不得改变。
- 传入非有限位置/yaw/dt/半径、负半径、负系数、`eyeUpMin>eyeUpMax`或负的`eyeMaxBackM`：拒绝且输出帧保持上一值（含上一帧高度）。

## 可读值、失败指示、重置

面板读数：输入球心位置与半径、单一heading、Rig位置/yaw、短角差、**离球心高度与后退量**（两个都由半径现算，不是常量）、`eyeBaseUpMeters`/`eyeUpPerRadius`、`eyeBaseBackMeters`/`eyeBackPerRadius`、三条护栏、reset标志与错误状态。出现朝向跨越大半圆、overshoot、重生长插值、高度或后退越界、后退方向没跟滤波后的yaw、RollDecoy带动Rig、坏输入改写旧态即失败。重置时清空滤波帧并回到起点；先设第一帧，再重复边界/短弧案例。

## 编译边界

`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallLayers/FollowRig/FollowRigMath.cs`只是含`Vector3`/标量yaw的普通C#算法移植，不是`UdonSharpBehaviour`，没有场景调用/状态读取、不含HMD API，**未在Unity编译或实机验证**。后续单一Windows操作者需测C#与UdonSharp、ProgramAsset/Prefab层级、`Mathf.Exp`和序列化/状态生命周期；VR舒适性与眼高范围另由用户实际头显测试，本规格不宣称通过。
