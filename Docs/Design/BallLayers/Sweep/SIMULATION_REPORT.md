# T54 Sweep 离线模拟报告

状态：纯Python几何参考；`SweepRuntimeDraft.cs`不是组件，未Unity/UdonSharp编译；SweepLab未搭建。

## 原作来源 / 项目近似

没有取得原版源码。US7402104B2仅是历史公开专利，不证明任何游戏版本沿用；官方产品资料没有本层扫掠实现细节；公开的Ruby/Gosu 2D项目不是原版代码。详细实际HTTP及限定证据见 [`GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md`](../GroundContact/SuiteFoundation/SOURCE_EVIDENCE.md)。T54依据的是本项目 §3.3 的“先球形扫掠”需求与 §1共享几何边界；平面与扩张AABB只是DRAFT实现。

## 命令与真实输出

命令（仓库根）：

```text
python3 -m unittest discover -s Docs/tests/ball_sweep -v
```

实际结果：**14 tests run, OK**。包含高速、薄墙、角滑动、有效多接触顺序、单面完全阻挡状态、静止初始平面嵌入、静止深盒嵌入及非中心盒嵌入、保守盒角假阳性、接触预算显式剩余量、固定输入确定性、非单位平面法线偏置归一化与坏输入。

多接触旧用例已按解析几何更正：旧 `(2,2,0)+(-3,-1,0)` 的y最小值为1，且第一次x面碰撞后y剩余只走1/3，永远到不了y=0；旧“第二次接触”断言无几何依据。替代输入 `(2,2,0)+(-3,-4,0)`：y平面t=1/2先到；投影剩余x=-1.5，x面整步TOI=1/2+(1/2)*(1/3)=2/3，测试按 `[y索引1,x索引0]` 验证。没有为旧断言改算法。

盒嵌入解析预期：原盒[-1,1]^3、r=.5，扩张AABB最近自由面离原点1.5；恢复距离=1.5+epsilon（稳定tie先选x负面）。不再只推一个epsilon。零位移同样先测；求解后复核仍嵌入或预算耗尽保留剩余则`stuck=true`。完整阻挡=`blocked=true,stuck=false`，正常切向滑动不会误报。

## 留存风险与限制

- AABB半径扩张在角落是方形近似，可能对真实球盒圆角假阳性；不是精确sphere-box。
- 单面平面有自由侧语义；重复双面墙、对称凹角、不可行窄通道、复杂约束与数值尺度需要继续验证。预算耗尽时返回未处理位移，不宣称路径已碰撞验证完成。
- epsilon/tolerance为占位；未测Unity物理、Udon、真实坡面、网络、VR、性能。
