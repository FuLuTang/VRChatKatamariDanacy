# T53 VisualRoll 独立边界（DRAFT）

状态：离线宿主数学草稿；接口未冻结，不是Unity/Udon运行交付。

## 数据接口

- `VisualRollState(local_rotation=(x,y,z,w))`：只拥有VisualRoll子节点局部旋转。构造时校验并规范化四元数；零、非有限、长度错误或范数不高于 `1e-12` 拒绝，不静默回退。输出 `local_scale=(1,1,1)`，没有玩法朝向字段。
- `advance(displacement, ground_normal, radius, discontinuous=False, max_displacement_m=None)`：同一调用方帧的米制球心位移、自由空间支撑法线、正有限滚动半径；可选显式瞬移/重生标志与调用方自定的位移跳变阈值。成功返回新状态；输入错误抛 `ValueError`/`OverflowError`，不修改旧状态。host/C#候选切向近零阈值暂为`1e-7 m`，精度映射仍待目标运行时核验。
- 位移先投影到支撑切平面 `t = d - n*dot(d,n)`；角度 `|t|/r`。法向移动不产生滚动。调用方必须在传送后从新位置重新取下一段位移，不能再传入跨图弦长。

## 旋转轴与适配声明

Host oracle明示使用四元数 `(x,y,z,w)` 与传统右手向量叉积语义。由刚体接触点速度 `v + ω×ρ = 0`、`ρ=-r n` 得轴方向 `n×t`；+Y支撑面、球心沿+Z移动时为+X。单测独立从接触点速度残差验证符号，不以被测 `_cross` 或旧测试公式构造期望值。

此约定不能直接宣布等价Unity。Unity 2022.3官方Cross页面明确写“left hand rule”；Python→Unity的轴/四元数行为尚未对拍。RuntimeDraft用分量表达式而非 `Vector3.Cross`，但Unity `Quaternion` 对象作用、乘法顺序、局部帧仍须由后续独立方向箭头与接触速度对象核验。未核验前不接入工程。

## 非目标

不积分球运动，不写位置/速度/玩法平面朝向/账本/碰撞/Collider，不驱动FollowRig、镜头或HUD，不选择半径/法线来源，不承诺世界尺度、VR舒适性、Unity/Udon编译或性能。