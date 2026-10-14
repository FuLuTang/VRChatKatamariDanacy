# T49 GroundContact 边界（DRAFT）

状态：仅本机 Python 数学参考与 C# RuntimeDraft；未冻结接口。T06 世界尺度/统一接口仍 OPEN。

## 输入与语义

`solve_ground_contact(core_radius, visual_rotation, proxy_points, ground_normal, plane_height, current_center, caps...)`

- 所有长度/位置同单位；米只是工作约定，当前并未通过 T06 世界尺度验收。
- 原始平面为 `dot(n_raw, x)=d_raw`，法线指向允许球心一侧。入口同时归一化 `n` 与 `d`，即 `n=n_raw/|n_raw|, d=d_raw/|n_raw|`。
- `visual_rotation` 是有限 proper 3×3 旋转矩阵，按行存储并作用于列向量。拒绝错形、非正交、镜像（det≠+1）与非有限矩阵。T50提供的四元数适配仅是 host Hamilton 算例；Unity语义须经方向探针。
- 代理点是球局部坐标的稀疏点；支持值重算为 `h=max(r,max_i dot(-n,Q p_i))`。空点集退化为 `h=r`。

## 输出与回落

- 输出 `support_height` 是当前几何值，不取历史 `max`。
- 目标法线坐标为 `d+h`。当前中心法线坐标 `c=dot(n,current_center)`，请求修正 `δ=d+h-c`；按 `[-max_drop_per_step,+max_lift_per_step]` 截断后返回 `center_position=current_center+n*δ_applied`。这保留中心切向位置，支持代理移除/姿态改变后的受限下降。
- `center_normal_coordinate` 是沿 n 的坐标，**不是 world-Y**；`plane_anchor=d*n` 是平面最近原点锚点，非唯一实际碰撞点。
- 可选速度扰动只做对称限幅建议，不积分速度、不解摩擦、滚动或多面动力学。
- 输入或中间值非法时 Python 抛 `ValueError`，无部分状态可提交。旋转矩阵容差与接触参数均为实现草案。

## 坐标移植门

Unity 2022.3 官方资料写明左手坐标系，并说明 `Vector3.Cross` 用 left-hand rule：<https://docs.unity3d.com/2022.3/Documentation/Manual/QuaternionAndEulerRotationsInUnity.html>、<https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Vector3.Cross.html>。本层不调用叉积；这不等于其输入矩阵/宿主四元数已与 Unity API 对拍。Unity对象需显示 +X/+Y/+Z 轴和 `AngleAxis(+90°, +Y) * +Z` 实际输出，独立比对局部/世界矩阵与非对称测试点。现在 `NOT_RUN`。

## 本层非目标

不负责生成/维护径向代理（T50）、扫掠碰撞（T54）、成长账本、速度积分、真实斜面/台阶/边缘、network、Prefab、Unity组件或 Udon运行时。只是当前平面支持近似。
