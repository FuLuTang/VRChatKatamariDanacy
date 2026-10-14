# T49 GroundContact 离线模拟报告

状态：host-only Python参考验证；`RuntimeDraft`未编译；测试对象未搭建；Unity/Udon未验证。

## 来源与项目对照

- 未取得原版源码。历史 US7402104B2 仅是公开专利，不证明新作沿用；有限网页访问与可访问独立Ruby/Gosu实验见 [`SuiteFoundation/SOURCE_EVIDENCE.md`](SuiteFoundation/SOURCE_EVIDENCE.md)。
- 本实现遵守 [`Docs/02_TECHNICAL_CONTRACTS.md`](../../../02_TECHNICAL_CONTRACTS.md) §3.2 的支持距离公式，但把项目自己的几何近似当成项目算法，而非原作规则。
- 当前矩阵输入必须是真正 proper rotation；地面方程归一化法线与偏置；输出分离平面锚点/法线坐标。旧历史max上抬模式被当前几何支持+上下行限幅取代。

## 复现命令与实际结果

命令（仓库根）：

```text
python3 -m unittest discover -s Docs/tests/ball_ground_contact -v
```

实际结果：**15 tests run, OK**（T49单层14项 + suite-A跨层桥接1项）。早期第一次运行有1个失败是超大向量用例未构造溢出（该向量点积仍有限）；测试改成非轴向归一化法线，使解析中间和确实溢出后重跑通过。没有隐藏算法失败。

独立预期/覆盖：空代理 `h=r`；局部端点支持取最大投影；r增长使突出量变小；最大抬升1.25截断；突出端点移除后支持从3.5回到1，drop cap=.4使中心从y=3.5降至3.1；非单位法线 `(0,2,0)` 与原始偏置4归一后为平面 y=2、半径1目标y=3；斜法线 `(1,1,0)` 用 `x=(5,-5,7)`起点校验切向差 `x-y` 与z保持，法线位移为2√2；矩阵错形/缩放/镜像/NaN拒绝；host Hamilton +Y90°方向探针 `+Z→+X`。

跨层桥接输入/输出：T50附着端点 `(0,-2,0)` → 从32方向快照还原的代理点 → T49当前支持 `>0.5`、中心法线坐标升高 → T54球形扫掠触地仍保持球半径0.5 → T50 detach+rebuild空账本 → T49支持0.5并允许中心从高位下降 → T54再次验证地面接触。该用例同时验证凹凸代理未静默改变T54球形判定半径。

## 风险与未测

- Python quaternion转换只验证了明示的主机Hamilton代数，不证明 Unity 2022.3 `Quaternion`/Transform同向；需对象上方+Y 90°箭头探针后才能接入。
- 支持距离不是完整碰撞/动力学；坡缘、台阶、多个平面与时步大穿透另由Sweep/后续运动处理。
- 单位、限幅和矩阵容差是草案；无物理手感/网络/性能证据。
- C#草稿不是Unity组件；Unity C#、UdonSharp、ProgramAsset、场景调用链未编译/未运行。
