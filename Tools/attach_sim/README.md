# 离线吸附观感测试

```bash
python3 Tools/attach_sim/run.py                                  # 5 个种子、全部变体
python3 Tools/attach_sim/run.py --variants baseline --seeds 1,2,3,4,5
python3 Tools/attach_sim/run.py --ns 10,20,30,40,60,80,100 --variants baseline,core-grows,touch-mesh,embed-45
python3 Tools/attach_sim/run.py --simulate-only                  # 只跑模拟，不渲染
python3 Tools/attach_sim/render.py /绝对路径/simulation.json --out /绝对路径/输出目录
python3 -m unittest discover -s Docs/tests/attach_sim
```

需要本地 .NET 8、Chromium、csharp_check 缓存的 Unity 托管引用，以及 `~/KatamariRef/models` 的参考资产。工具不连接 Unity 或 SSH。

默认读取 `_ref_cancer/placement.v2.json`，从当前运行时可吸的剩余实例中等概率、不放回抽样。球位于原点、不滚动；飞入方向均匀分布于球面，朝向为均匀 SO(3)；沿直线每步推进 1 mm，并用运行时接触谓词细化首次交界。起始直径取关卡；尺寸比均为 1，直径上限 30 m，针最小突出 0.01 m。

工程链接运行时源码，调用 `PickupAbsorbStepMath`、`AttachmentStepMath`、`AttachmentMath`、`BallShapeMath`、尺寸与账本函数；没有第二份吸附、冻结、成长或针接纳算法。真实模型的加载、贴图、枢轴补偿与渲染复用 leveleditor 的 `EditorView`，无头浏览器复用 `shot_cdp.Chrome`。核心优先用原作 `core_01.obj/png`；缺失时用球体。

## 变体

`baseline` 是运行时现行规则（碰撞球接触、核显示固定为起始直径）。其余是假设性对照，只存在于本工具，运行时不变；对照与基线共用同一串抽样（同样的道具、方向、朝向、成长），只改变停下的位置或核的显示尺寸。

| 变体 | 接触规则 | 核显示直径 |
|---|---|---|
| `baseline` | 碰撞球 | 起始直径（固定） |
| `core-grows` | 碰撞球 | 随碰撞球直径长大 |
| `touch-mesh` | 碰撞球，或已粘道具的真实三角网格 | 固定 |
| `touch-mesh+core-grows` | 同上 | 随碰撞球长大 |
| `touch-boxes` | 碰撞球，或已粘道具的有向盒（网格的保守上界） | 固定 |
| `embed-30/45/60` | 碰撞球接触点沿径向向内拉 30/45/60% 道具径向盒长 | 固定 |

对照的停止点经同一份运行时事件、账本、成长与针步骤重放（强制接受，因为停在碰撞球之外或之内时运行时门会拒绝）。`touch-mesh` 的停止点在浏览器里对真实三角网格求解（比球面接触多 0.1 mm 间隙，避免首次接触被计为穿插），由 `--stops=<json>` 交给 C# 重放。

## 输出

默认位于 `~/KatamariRef/viewer_evidence/attach_sim/<UTC时间戳>/`，禁止写入仓库：

- `simulation.json`（基线）、`simulation_touch_mesh.json`、`simulation_touch_boxes.json`、`simulation_embed-NN.json`：种子、冻结局部位姿、逐件门与挂载间隙、成长、针状态及源码指纹。`touch_mesh_stops*.json`、`embed-NN_stops.json` 是对照的停止距离。
- `<变体>/seed_<种子>/n<N>_<视角>.png`：N 件、±X/±Y/±Z 六视角；`_mask` 是核专色遮挡图，`_core_only` 是同相机下仅核基准图；`n<N>_six_views.png` 六向拼图；`<变体>/overview.png` 全部种子总览；`<变体>/render_report.json`。
- `summary.json`：`variants` 下每个变体的核可见比例、间隙、穿插诊断；`controls` 是各 N 的可见度及相对基线的差值（`gainCoreGrows`、`gainTouchAttached`、`gainBoth`、`interaction`、`gainTouchBoxes`、`gainEmbedNN`）。

## 读数定义

- 核可见比例 = 完整场景的纯品红像素 / 仅核的纯品红像素（同相机、同核尺寸）；遮挡材质保留贴图透明裁切。范围指各种子六向平均值的最小/最大值。
- 运行时间隙 `attachmentGapMm`：道具有向盒到吸收前碰撞球的有符号间隙。`meshContactGapMm`：真实三角网格最近点到同一吸收前球面的间隙；盒内空腔、圆形轮廓与枢轴使它不同。`meshToFixedCoreSurfaceGapMm`：真实网格最近点到固定显示核表面的距离。`coreSurfaceToCollisionSphereMm`：碰撞球半径减固定核半径。
- 穿插：`obbPairsUpperBound` 是有向盒 SAT 重叠对数（上界，含盒内空角）；`meshSurfaceIntersectPairs` 是三角表面相交对数（忽略贴图透明孔，不统计完全包含而无表面相交的情形）；`meshAffectedItems` 是至少与一件网格相交的道具数。分母为 N(N−1)/2。
- 没有模拟地面、附着物互碰（基线）或原作行走路线。

## 体积回收比：仅离线对照

```bash
python3 Tools/attach_sim/recovery.py --out ~/KatamariRef/viewer_evidence/attach_sim/<UTC时间戳>/
python3 Tools/attach_sim/recovery.py --resume --out <同一目录> # 仅复用同一 C#、曲线、N 的已完成结果
```

四组各五种子（1–5），N=10/20/30/40/60/80：`baseline`、`mas1-normalized`、`mas1-half-normalized`、`mas1-absolute`。原文折点是 MAS1 的 (5 cm,.8)/(10 cm,.8)/(15 cm,.4)/(20 cm,.16)，来源为本机 `~/KatamariRef/meta/raw_growth_mechanism.txt:14–38`（社区文献，**没有 Cancer 曲线**）。归一化组把直径折点乘 `Cancer 起始径 / 5 cm`；激进组再把 ratio 减半；绝对组不缩放直径折点。三者都是**移植假设**，端点外夹持也是本工具假设，绝非 Cancer 原作数值。

C# 参数 `--growth-curve=<json>` 接受 `diameterM` / `ratio` 两数组，在吸收**前**球径上线性插值；`growthMm3 × ratio / 0.8` 在提交前算一次、四舍五入到 mm³，保留逐件 penalty 的相对覆盖，不把原来已乘的 0.8 再乘一遍。`RecomputeConfirmedAndSize` 仍只读取确认账本，不回溯改变已有收益。参数 `--draws=<simulation.json>` 重放同种子的实例序列，每件仍须通过当前运行时可吸门，否则报错；不强行接受。方向和朝向保持相同。

回收比会改变可吸集合，直接用相同 PRNG 不代表相同物件。因此本实验先在最慢的绝对组抽样，再将同样物件/方向/朝向交给其余组，隔离成长与放置效应；所有组仍然用各自半径重新求接触点。它回答**同样件数与内容**的差异，不是玩家路线或自然解锁后的节奏预测。六向正交相机对同种子各组使用相同视野。

输出 `simulation_<组>.json`、`<组>_curve.json`、`recovery_summary.json`，以及各组 `overview_n30.png` / `overview_n60.png`（五种子六向总览）。浮感代理：`isolatedFraction` = 没有与任何其他道具三角表面相交、且真实网格到固定**参考核球面**最近距离 >10 mm 的件数/N；另报该参考核正间隙的 p50/p90（分母为五种子全部件）。参考核为半径 `startDiameterM/2` 的理想球，不是凹凸原作核网格。此指标不等于欧氏道具间隙，也不证明知觉悬浮；表面交叉忽略贴图透明孔、完全包含而无表面交叉的情况不计。原有核可见度与真实网格互穿对数同时保留，避免以单指标代替观感。
