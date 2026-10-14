# T55 FollowRig 独立边界（DRAFT）

状态：由T46宿主草稿提取出的独立参考模块；不改T46、不冻结接口、不代表场景功能。

## 窄数据接口

`FollowRig.update(ball_position, horizontal_yaw_deg, dt_s, eye_height_m, eye_height_min_m, eye_height_max_m, reset=False) -> FollowFrame(position, yaw_deg)`。

- 朝向输入仅是调用方提供的水平标量偏航角；接口没有球四元数、自转、roll/pitch、HMD姿态/位置、头部方向、控制器观察轴或Camera字段。
- `x/z`位置与水平偏航做指数滤波；偏航差按最短圆周弧计算，输出规范到`[0,360)`，不超调。
- `y = ball_y + filtered_eye_offset`。眼高目标先限制在每次调用显式提供的有限`[min,max]`，滤波后的偏移也重新限幅，保证相对当前球心Y的偏移落在输入区间。范围是模块调用方参数，不是舒适性建议或产品默认值。
- 首样本及`reset=True`（重生/瞬移）立即贴合新参考位置和yaw，旧位置不参与长途插值；后续普通调用恢复平滑。
- 所有参数先验证再提交状态；非法值/中间数值溢出拒绝并保留旧帧。

## 参数继承与限制

滤波频率默认5Hz/8Hz只是从旧T46 `FollowRig` 取出的宿主起始值，非实测产品参数。只抽取了指数滤波和yaw圆周差语义，没有沿用旧 `(roll,pitch,yaw)` 输入类型、`roll_deg/pitch_deg` 字段或自由未限眼高值。此层不创建Camera、不修改球状态、不以HMD身高计算球/世界高度；VR舒适性必须由真人门禁验收。
