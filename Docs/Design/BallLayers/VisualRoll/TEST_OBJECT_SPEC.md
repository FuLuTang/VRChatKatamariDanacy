# T53 VisualRoll 测试对象规格（未搭建）

## 独立测试对象

**建议Prefab名：** `T53_VisualRoll_NoSlipLab`。本规格是Windows单一Unity操作者后续创建的可进入对象说明；此轮Linux没有写Unity工程、生成Prefab或场景。

### Prefab层级

```text
T53_VisualRoll_NoSlipLab (root; no Udon/network/collider)
├── TestPanel (方向/角度/状态读数；之后可用文字Mesh或Inspector)
├── BallPhysicsReference (只作几何位置标记，不挂玩法代码)
│   ├── CoreVisual (MeshRenderer；无Collider)
│   ├── BallVisualRollRoot (唯一被测试旋转节点；localScale=1)
│   │   └── AsymmetricMark (偏心彩色箭头/贴花，不能用对称球体代替)
│   └── ContactPointMarker (球底/指定法线处的可视点)
├── SupportPlane (可替换为+Y地面或斜面；仅几何)
├── WorldAxisArrows (+X红、+Y绿、+Z蓝，带标签)
└── SamplePath (可视球心起终点与切向/法向分量标线)
```

本对象只显示几何/旋转。验证阶段禁止玩法Collider、记分/拾取脚本和真实FollowRig；不把装饰标记变成球形玩法判定。

## 操作与预期

1. 选择平地 `n=+Y`，单步将球心沿 `+Z` 移动 `s`。预期自转幅度 `s/r` 弧度；对称外观不够，观察偏心标记。依照当前Host右手候选，轴应为`+X`，且独立计算 `v + ω×(-r n)` 残差接近零。
2. 沿`-Z`返回同样距离，滚动反向并恢复初始姿态（允许四元数`q`与`-q`等价）。
3. 斜面位移同时添加法向分量；只有切向部分改变角度。纯法向移动不改变姿态。
4. 触发显式重生/瞬移标志，移动到远处；该步不狂转。下一段新位置的小位移恢复正常滚动。
5. 输入零/NaN法线、非正半径、零/NaN四元数；拒绝且不改旧姿态。长时小步循环后读取四元数范数与Scale。

## 读数与失败指示

后续测试适配器至少输出：位移分解`t/n`、`|t|/r`、四元数xyzw、范数、VisualRoot scale、`v+ω×ρ`残差、是否reset/跳过。若`+Z`测试出现`-X`轴、接触速度与球心同向、纯法向产生角度、重生步角度随跨图距离增长、scale不为1、数值污染或旧态被坏输入部分改写，即失败。

## 重置与编译边界

按钮/调试入口应恢复单位四元数、Scale `(1,1,1)`、初始球心与无传送状态；重新从方向箭头探针开始。`RuntimeDraft/VisualRollRuntimeDraft.cs`仅普通C#源码草稿，没导入Unity、没有UdonSharpBehaviour/ProgramAsset/场景引用，**未编译**。后续必须在Unity 2022.3.22f1目标工程由单一Windows操作者执行坐标方向对拍、基础C#与UdonSharp编译、ProgramAsset与Prefab调用链检查；目前均NOT_RUN。