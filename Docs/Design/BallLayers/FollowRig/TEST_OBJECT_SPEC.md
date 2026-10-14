# T55 FollowRig 测试对象规格（未搭建）

## 独立对象与Prefab层级

建议名 `T55_FollowRig_HeadingLab`，未来作为单独可进入对象/Prefab；不成为滚动Mesh子节点，不使用真实HMD或相机输入。

```text
T55_FollowRig_HeadingLab (root; no gameplay/network collider)
├── TestPanel (标量yaw、目标/输出位置、滤波频率、限高与重置状态)
├── BallReference (只画位置球心/朝向水平箭头；无自转读入)
│   └── HorizontalHeadingArrow (+Z前向起始方向)
├── FollowRigReference (独立于BallReference的空Transform/坐标架)
│   └── ForwardArrow (只随FollowRig yaw)
├── EyeHeightBand (球心上方min/max两根线)
├── TeleportStartMarker
├── TeleportEndMarker
└── RollDecoy (原地可旋转的非交互视觉球；不得作为Rig输入)
```

## 操作与预期几何

- 初始将水平heading设为359°，连续目标设为1°：输出应沿+2°短弧变化，不经过180°长路；反向1°→359°同理为-2°短弧。
- 把球参考架沿水平移到远点：普通步逐渐追随，不超目标；按“重生/瞬移重置”后立即到新位置+yaw，无跨地图插值/抖动。
- 依次输入低于min、处于区间、高于max的eye height，观察Rig相对球心Y恰在边界/区间内。
- 旋转`RollDecoy`而保持位置与水平yaw不变：FollowRig箭头不得改变。
- 传入非有限位置/yaw/dt/眼高，或`min>max`：拒绝且输出帧保持上一值。

## 可读值、失败指示、重置

面板读数：输入球位置、单一heading、Rig位置/yaw、短角差、实际eye offset、min/max、reset标志与错误状态。出现朝向跨越大半圆、overshoot、重生长插值、眼高越界、RollDecoy带动Rig、坏输入改写旧态即失败。重置时清空滤波帧并回到起点；先设第一帧，再重复边界/短弧案例。

## 编译边界

`RuntimeDraft/FollowRigRuntimeDraft.cs`只是含`Vector3`/标量yaw的普通C#算法草稿，不是`UdonSharpBehaviour`，没有Unity引用/场景调用、不含HMD API，**未在Unity编译或实机验证**。后续单一Windows操作者需测C#与UdonSharp、ProgramAsset/Prefab层级、`Mathf.Exp`和序列化/状态生命周期；VR舒适性与眼高范围另由用户实际头显测试，本规格不宣称通过。