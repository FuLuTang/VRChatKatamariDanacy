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
