# T49 球层：地面接触与支持高度

> **DRAFT / host-only reference.** Python 为标准库离线数学参考，C# 位于 `RuntimeDraft/` 且不是组件；未在 Unity/UdonSharp 编译、未搭场景或网络/VR测试。接口与世界尺度均未冻结。

## 项目要求对照

- 项目技术契约 §3.2 给出 `h=max(r,max_i dot(-n,Q*p_i))`，T49只估计当前表面支持，不解完整动力学。
- 原版源码未取得。US7402104B2是历史专利，不是游戏源码，不证明新版沿用；本套有限HTTP证据与第三方独立2D实验见 [`SuiteFoundation/SOURCE_EVIDENCE.md`](SuiteFoundation/SOURCE_EVIDENCE.md)。
- Unity 2022.3 文档说明左手坐标与 Cross 左手规则；已移除旧 A 套“右手坐标”标签，方向/Quaternion API对拍仍待Unity箭头探针，详见 [`SuiteFoundation/INTERFACES.md`](SuiteFoundation/INTERFACES.md)。

## 当前实现

- `ground_contact.py` 每次按核心半径、旋转矩阵与当前代理点重算支持值；空代理退化为半径。
- 法线平面是 `dot(n_raw,x)=d_raw`；非单位法线与偏置同时归一化。
- 目标中心沿平面法线放置；位置修正保留当前切向分量。上抬由 `max_lift_per_step` 限制，回落由 `max_drop_per_step` 限制，所以移除突出物允许受限下降。`d+h` 是法线坐标，不是 world-Y。
- 旋转矩阵验证形状、有限值、行正交与行列式 +1；T50主机Quaternion转换器的 `+Y 90°: +Z→+X` 是**host方向算例**，不是Unity parity证据。
- 平面锚点叫 `plane_anchor`，不冒称代理/地形真实接触点；速度扰动只输出截断建议。

## 运行

从仓库根执行：`python3 -m unittest discover -s Docs/tests/ball_ground_contact -v`。本目录测试包含地面层与单独A套合成桥接，不是Unity测试。

## 未验证

Unity Quaternion / Transform方向适配、UdonSharp编译/ProgramAsset/组件链、Unity物理/斜面/边缘、性能、网络和VR均 `NOT_RUN`。生成的C#草稿不可称为 Udon可运行功能。
