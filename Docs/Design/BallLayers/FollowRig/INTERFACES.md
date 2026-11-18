# T55 FollowRig 独立边界（DRAFT）

状态：由T46宿主草稿提取出的独立参考模块；不改T46、不冻结接口、不代表场景功能。

## 窄数据接口

`FollowRig.update(ball_position, ball_radius_m, horizontal_yaw_deg, dt_s, eye_base_up_m, eye_up_per_radius, eye_up_min_m, eye_up_max_m, eye_base_back_m, eye_back_per_radius, eye_max_back_m, reset=False) -> FollowFrame(position, yaw_deg)`。

模块级 `try_update(...)` 与 C# `FollowRigMath.TryUpdate` 同名同序（含 `has_previous_frame` / 上一帧 yaw、高度这些调用方持有的状态），宿主类只是替调用方保存这份状态并提供 `frame` / `height_m` 读数。

- 朝向输入仅是调用方提供的水平标量偏航角；接口没有球四元数、自转、roll/pitch、HMD姿态/位置、头部方向、控制器观察轴或Camera字段。
- 位置是 `eye = ball_centre − forward·back + up·height`：`height = min(eye_base_up_m, eye_up_max_m) + radius·eye_up_per_radius`（再保底到 `eye_up_min_m`），`back = min(eye_base_back_m, eye_max_back_m) + radius·eye_back_per_radius`。两个 max 只钳**加性底座**，不钳半径项：固定米数上限会在球变大时把眼点钉进球里，半径项由球径上限约束。两项都从**球心**量起、随半径线性放大，所以「球小、视角低、贴得近」和「球大、视角高、退得远」是同一个式子。`forward` 取**滤波后的 yaw**，与写进 `FollowFrame.yaw_deg` 的朝向同源，偏移方向和朝向不会分歧。
- `x/z` 由当前球心和滤波 yaw 直接算，确保眼点处于球正后方。高度做指数滤波，水平偏航单独用 yaw alpha；偏航差按最短圆周弧计算，输出规范到`[0,360)`，不超调。`y = ball_y + 本次 height`，`height` 就是下一次调用的基线。
- 首样本及`reset=True`（重生/瞬移）立即贴合新参考位置、yaw 与高度，旧位置不参与长途插值；后续普通调用恢复平滑。
- 所有参数先验证再提交状态；非法值（含负半径、负系数、`min>max`）/中间数值溢出拒绝并保留旧帧。

## 参数继承与限制

滤波频率默认5Hz/8Hz只是从旧T46 `FollowRig` 取出的宿主起始值，非实测产品参数。只抽取了指数滤波和yaw圆周差语义，没有沿用旧 `(roll,pitch,yaw)` 输入类型、`roll_deg/pitch_deg` 字段，也没有保留旧的「离球心常量眼高」`eye_height_m`：眼点系数与三条护栏（`eyeUpMin/Max`、`eyeMaxBackM`）都是 motor 的序列化字段，模型只作为每次调用的参数接收，不自带默认值（这样模型里没有第二份产品数）；`eyeUpMaxMeters` / `eyeMaxBackM` 是加性底座的上限，半径项不钳。此层不创建Camera、不修改球状态、不以HMD身高计算球/世界高度；VR舒适性必须由真人门禁验收。
