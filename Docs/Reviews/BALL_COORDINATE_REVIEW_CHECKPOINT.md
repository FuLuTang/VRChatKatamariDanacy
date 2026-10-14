# 球模块坐标移植：Lead检查点

## 官方依据核查

实际读取Unity2022.3官方手册（HTTP200）：[Rotation and orientation in Unity](https://docs.unity3d.com/2022.3/Documentation/Manual/QuaternionAndEulerRotationsInUnity.html)。该页面明确：Unity uses a left-handed coordinate system；+X右、+Y上、+Z前。欧拉外禀旋转顺序为Z、X、Y。

## 当前冲突与处理边界

A套接口草案写“右手系建议约定 +Y上、+Z前、+X右”。轴名并不能证明旋转乘法和Unity兼容；这与官方对Unity的手性描述不一致。该句必须在源码移植审查时澄清，不可直接当已验证合同。

不要只改一句“左手”或盲目给四元数取反。必须核对Python的叉积、四元数xyzw、乘法、局部到世界作用，和Unity的Quaternion.AngleAxis / quaternion-vector操作是否在独立探针下等效。确认之前不进入真实场景集成。

## 必需独立探针（离线算例→Unity实测）

- 身份旋转：+X/+Y/+Z三支有颜色和标签的箭头。
- 指定+Y轴90度的姿态：记录前向探针实际落在哪根轴，与Unity API输出对拍，而非只检验往返可逆。
- 平面上沿+Z前进：用球底部有颜色的标记，以及接触点的旋转速度+平移速度是否相消，确定正滚动方向。
- 逆向行进与斜坡：切向距离决定滚动角度，不把法向抬升算作滚动路程。
- 增量乘法顺序：连续绕不同轴旋转，用非对称点/箭头验证累计结果，单位球对称外观不算证据。
- 世界→局部→世界往返仅证明自洽，不足以证明与Unity约定一致。

## 状态

官方手性描述：已核实。Python→Unity旋转行为对拍、Udon编译与实际场景探针：NOT_RUN。此检查点不改正在并行写入的父级接口文件，避免单写者冲突；Lead最终评审时必须逐项核验和要求修正。
