# 道具碰撞代理（COLLIDER PROXY）

> **2026-11-16 门已改**：吸收尺寸门已按原作数字改写——物件体积 `V = k·a·b·c`（`k` 缺省 0.70），门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`。本文里若出现 `a·b·c ≤ (π/6)·D³/10`、`absorbVolumeRatio`、`maxItemVolumeWorldM3`、「浮点比较、不取整」或按旧门算出的档位件数，那是 2026-11-15 的口径（历史）；现行口径、实现清单与前后实测见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。


> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15（2026-11-16 增补运行期实现）。
> 运行期落点、序列化字段与场景 diff 见 [COLLIDER_PROXY_RUNTIME.md](COLLIDER_PROXY_RUNTIME.md)；C# 编译/EditMode/PlayMode **仍未验证**。
> 写入范围：本文件新建；`Tools/levelbuild/collider_proxy.py`（新）、`Tools/levelbuild/placement_core.py`（只加可选字段）、`Tools/levelbuild/blender/fit_collider_proxy.py`（新）、`Docs/tests/levelbuild/test_collider_proxy.py`（新）、[collider_proxy_report.md](collider_proxy_report.md) 与 [collider_proxy.csv](collider_proxy.csv)。
> **没有**改 `PlacementImporter.cs`、`PickupFieldSync.cs`、`Runtime/**`、`Tools/leveleditor/**`、任何场景/预制体，也没有开 Unity。运行期契约在第 6 节，只有签名和成本估计，不声称已编译或已实测。

## 0. 一句话

今天每件道具的碰撞体 = 视觉网格的局部 AABB；西瓜（0.30×0.22×0.24 的椭球）按 0.30×0.22×0.24 的**盒子**挡球，角上比果子多出约 4 cm。本文给 `defs[]` 加一个**可选**的 `collider` 块，用纯 Python 从网格点云拟合 **sphere → capsule → box → ≤24 点凸包** 中最便宜且形状误差达标的一个；**吸收门、`propBounds`、G13 的 k 系数一个字不改**（第 1 节）。

## 1. 与现有三层的关系（为什么不能顺手改吸收门）

| 层 | 数据 | 谁读 | 本文是否动 |
|---|---|---|---|
| 视觉网格 | FBX / MeshFilter | 渲染 | 只读，用来拟合 |
| **碰撞形状（本文）** | `defs[].collider` → 将来烘焙成世界空间代理 | 球接触 v2（将来）、玩家胶囊（将来） | **新增可选块** |
| 玩法体积 | `propLocalBounds`（自身尺寸）、`propBounds`（世界包围盒），场景平行数组 | 吸收门、reach 与挡路门读自身尺寸，挡路盒形状读世界包围盒 | **不动** |

- 「挡不挡」仍然是 `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(propBounds[i], limit)`，与吸收门同一个函数；`propBounds` 仍是 AABB 体积。**本块不参与吸收判定**，也不许回写 `propBounds`。
- `PARITY_AUDIT` 的 `G13`（原作 `Volume = k·bbox`，默认 k=0.70）是**体积口径**的问题，属于吸收门；本文不碰它，也不把碰撞代理的实体体积当成吸收体积。一个道具可以「玩法体积含空腔、碰撞体不含」，这是 [COLLIDER_LAYER](COLLIDER_LAYER.md) §1 已经写明的同构。
- 一个 def 只有一份 `collider`，该 def 的所有实例共用；它描述的是**模型局部空间**里的形状，Unity 侧按每件实例的世界位置/旋转烘焙一次。

## 2. 数据模型：`defs[].collider`

`Docs/Design/Levels/<level>/placement.json` 的 `defs[]` 里新增**可选**键：

```jsonc
// 西瓜（本轮实际拟合值）：胶囊
{"id": "watermelon", "kind": "food", "valueUnits": 4,
 "collider": {"type": "capsule", "center": [0.0, 0.0, 0.0],
              "radius": 0.1118, "halfHeight": 0.03, "axis": "x",
              "source": "auto", "fit_error": 0.0289}}

// 其他三种形状的字段形状（各一个例子；字段不能跨类型混用）
// box:     {"type": "box", "center": [0,0,0], "size": [1.2, 0.45, 0.4]}
// sphere:  {"type": "sphere", "center": [0,0,0], "radius": 0.074}
// convex:  {"type": "convex", "center": [0,0,0],
//           "hull": [-0.12,0.0125,0.0, ... 4..24 点，扁平 3N 数字 ...]}
```

### 2.1 字段与语义

| 字段 | 类型 | 必填 | 语义 |
|---|---|---|---|
| `type` | string | 是 | `box` / `sphere` / `capsule` / `convex` |
| `center` | 3×number | 是 | 形状中心；**四种类型统一**为「中心 + 局部偏移」 |
| `size` | 3×number | box | 完整边长（不是半长），每轴 > 0 |
| `radius` | number | sphere/capsule | 米，> 0 |
| `halfHeight` | number | capsule | 轴段半长，≥ 0；0 等价于球 |
| `axis` | `"x"`/`"y"`/`"z"` | capsule | 轴段方向（道具局部轴） |
| `hull` | 4..24 × 3×number | convex | **扁平数组** `[x0,y0,z0, x1,y1,z1, ...]`（JsonUtility 不支持数组的数组）；**相对 `center` 的局部偏移**；世界点 = `propPos + propRot * (center + hull[i])`。Python 校验器也接受可读的 `[[x,y,z], ...]` 并规范化成扁平 |
| `source` | `"auto"`/`"manual"` | 否 | 拟合来源；缺省按 `auto` 处理 |
| `fit_error` | number [0,1] | 否 | 拟合误差；`manual` 可不填 |

**空间**：本块是**道具局部空间**，轴与 `placement.json` 的 `aabb` 一致（Unity 左手、Y-up、米）。它**不是** manifest 的 Blender 空间：`bounds_m` 是 Blender（z 上），`aabb` 与 `collider` 是 Unity（y 上）；换算已在流水线里，`collider` 不再做第二次轴变换。Blender 辅助脚本导出的点就是 FBX 的网格局部坐标（导入器只把**物体**转 90°，网格数据本身已经是 Unity 局部帧），见 `Tools/levelbuild/blender/fit_collider_proxy.py` 的注释。

**凸包的 `center` 约定（最容易错的一条）**：`hull` 的点是**相对 `center` 的偏移**，不是绝对坐标。这样四种类型都是 `center + 局部偏移`，消费方永远只加一次 `center`。`center` 取 hull 的 AABB 中心（不是质心）。

### 2.2 校验（`placement_core.validate`，硬错误）

`[collider]` 前缀，全部是 error，不是 warning：

1. 块必须是对象；`type` 必须是四种之一；**未知字段 / 跨类型字段**直接报错（例如 sphere 带 `size`、box 带 `radius`），防止 `raduis` 这类拼写错误静默退化成默认盒子。
2. `center` 必须是 3 个有限数；`size` 每轴 > 0；`radius` > 0；`halfHeight` ≥ 0；`axis` 必须是 x/y/z。
3. `hull` 点数 4..24；每点 3 个有限数；**凸性检查**：每个点都必须在「其余点的凸包」表面上，容差 `max(0.1 mm, 0.1% × 最大跨度)`（1 m 的件是 1 mm；凸包坐标只存到 0.1 mm，容差必须覆盖舍入）；退化（共面/零体积）报错。
4. `source` ∈ {auto, manual}；`fit_error` 若存在必须在 [0, 1]。
5. **盒 ⊆ 视觉 AABB**：把代理的 AABB 与 manifest 的 `aabb` 比，容差 1 mm（与 `MANIFEST_AABB_TOLERANCE_M` 同）。球/胶囊/盒/凸包都按 `center ± 半尺寸` 算；凸包按 `center + hull` 的 AABB 算。越界是 error（COLLIDER_LAYER §4.1：盒必须在视觉 AABB 内）。
6. **没有 `collider` 块 = 今天的行为**：`canonical_doc` / `validate` 不添加、不改写任何东西；`Docs/tests/levelbuild/test_collider_proxy.py` 用 git HEAD 的 `story_01` 与 9 个 `physics_test_field` cell 证明逐字节不变、错误集合不变。

### 2.3 规范化与往返

- `DEF_KEYS` 变成 `("id","kind","model","valueUnits","collider","notes")`；`canonical_doc` 对 `collider` 用固定键序 `type, center, size, radius, halfHeight, axis, hull, source, fit_error`，所有数 4 位小数、`-0.0 → 0.0`；`hull` 保持点顺序（顺序无语义，但保留作者/拟合顺序，保证确定性）。
- 同一份文档 `dump_doc → load → dump_doc` 逐字节相同；没有 `collider` 的旧文档逐字节不变。
- 规范形只对**存在**的块生效；不会把缺省字段补出来（保持可选、最小 diff）。

## 3. 拟合器（`Tools/levelbuild/collider_proxy.py`）

### 3.1 输入与采样

- 输入：网格顶点 + 三角面，或纯点云。FBX 侧由 Blender 辅助脚本导出 `<def>.json`（顶点、三角面、局部 bounds），写在仓库外（例如 `/home/fulutang/KatamariRef/meta/collider_proxy/points/`），仓库里只留 CSV/报告。
- 表面样本 = 顶点 + 每个三角形的质心；超过 1200 个按固定步长抽取（确定性）。质心用来抓「面内最大偏差」，只靠顶点会漏掉盒角/棱。
- **外轮廓（outer envelope）**：先算全部点的凸包；只保留距凸包表面 ≤ `max(0.5 mm, 1% 最大跨度)` 的样本参与拟合和误差。理由：凸碰撞体本来就表达不了凹腔（碗内壁、桌下、篮筐内），而球进不去的凹腔不是碰撞形状需求；用全部表面会把「凹」误判成形状误差。`outer_fraction` 写进 CSV，便于审计。
- 体积参考：凸包体积（`hull_volume`）；另外算网格自身封闭体积，给出 `mesh_volume_ratio`（空心件会很大，只作诊断）。
- **视觉 AABB**：可选 `bounds` 参数（局部空间的 `(low, high)`）。给了就把球/胶囊夹进去（§3.3 的 AABB 夹紧）；没给就不夹，由调用方负责。Blender 辅助脚本从 manifest 的 `dimensions_m` 换算成 Unity 局部 `(x, z, y)` 写进 sidecar 的 `visual_bounds`，所以 128 件真实数据都带约束。

### 3.2 误差定义

`fit_error = max( 网格表面样本到代理表面的无符号距离 ) / 代理最大跨度`。

- **无符号**：表面不是实体。椭球在它的 AABB 里，点到盒面的距离是正的（角部大），所以「盒太大」和「盒太小」同样被罚。如果只用「网格点是否在代理内」，一个平面盘子会被 AABB 判成完美（盘子的上下表面正好贴在盒的上下面上），这正是用户抱怨的反方向；所以盒子另加体积守卫（§3.4）。
- 对凸包/盒子，最大距离通常出现在顶点或三角形质心；对球/胶囊用解析式。
- 归一化用**代理**最大跨度（球=2r，胶囊=2(h+r)，盒/凸包=最大边长），不是网格的。
- 同时报 `volume_ratio = 代理体积 / 凸包体积`（1.0 = 正好填满外轮廓；椭球的 AABB 是 6/π = 1.91；<1 表示代理比可见轮廓小、会穿模）和 `containment`（表面样本落在代理内的比例，穿模诊断）。这两个是**报告/守卫**，不进 JSON 块。

### 3.3 四档与阈值

按成本从低到高，第一个达标的赢：

| 档 | 拟合 | 接受条件 | 为什么是这个数 / 用户看到什么 |
|---|---|---|---|
| **sphere** | Kåsa 最小二乘球（中心 + 半径；对精确球面点云的误差 ~1e-16）。简报写的是「质心 + RMS 半径」，这里改用最小二乘：质心/RMS 在 300 个斐波那契球面点上会有 ~0.03% 的半径偏差，过不了「完美球误差 < 1e-6」的验收；Kåsa 对精确球面是代数精确的，奇异时回退到质心/RMS | `max deviation ≤ max(8% × r, 2 mm)` 且 `体积 ≤ 1.5 × 凸包体积` | 8% 半径：0.15 m 半径的西瓜是 12 mm 鼓包，0.5 m 距离约 1.4° 视角，已经能看出「不圆」；再大就不像水果。40 mm 的玩具球是 3.2 mm，低于 2 mm 绝对底时按 2 mm。 |
| **capsule** | 沿 x/y/z 三个局部轴各拟合一次，取误差最小；固定半长 h 时半径的最优解是「各点到轴/球帽距离的 (min+max)/2」，对 h 做粗到细的确定性一维搜索 | `max gap ≤ max(10% × r, 2 mm)` 且 `体积 ≤ 1.5 × 凸包体积` | 10% 半径：西瓜 11 mm；胶囊的圆柱段与半球段交界在 10% 以上开始被看成「药丸」而不是「椭球」。对细长件 r 很小，10% 很紧，所以细长件通常落到凸包/盒子；这是有意的：胶囊只该给「圆头长条」。 |
| **box** | 局部轴对齐 AABB（schema 没有盒旋转，见 §7） | `max gap ≤ max(5% × 最大边, 2 mm)` 且 `体积 ≤ 1.15 × 凸包体积` | 5% 最大边：1.2 m 长凳是 60 mm；0.3 m 木板是 15 mm。盒子只在「表面本身就接近平面」时成立；超过 5% 的角会变成空气墙。1.15 体积守卫专门抓「盘子/圆片」：它们的上下表面与 AABB 齐平，单向误差看不到角部外凸（盘子 AABB 体积是凸包的 1.96 倍，必须拒）。 |
| **convex ≤24 点** | 全凸包顶点的**最远点采样**（FPS，确定性）到 24 点，取这 24 点的凸包 | `max gap ≤ max(10% × 最大跨度, 2 mm)` | 24 点是「很低的低模」：球面上 24 个支撑点的弦高误差约 10%，这正是用户说的「至多一个很低模的椭球」。超过 10% 说明 24 点已经不够表达这个形状（细长弯钩、多凸起），报告里降级为 best-available 并写明原因。 |

**AABB 夹紧**：球/胶囊在拟合后会夹进视觉 AABB（`r ≤ 最小半轴`、`h ≤ 轴向半长 − r`，中心也夹到可行区间），夹紧后**重新量误差**（只会变大）。这保证块能通过 §2.2 的「盒 ⊆ 视觉 AABB」校验；不夹的话，西瓜的胶囊半径 111.8 mm 会比 110 mm 的 y 半轴多出 1.8 mm，直接是数据错误。夹紧后胶囊的 `h` 会在约束内重新搜索（否则会为了缩短半径而丢掉轴端）。

**绝对底 2 mm**：所有档通用，`max gap ≤ 2 mm` 直接接受，即使相对阈值更紧。依据：球的接触皮肤是 `ContactTuning.ContactSkinM = 5 mm`，2 mm 在皮肤带以内；在 1 m 距离上 2 mm 约 0.11°，接近视觉锐度极限。这样 12 mm 的纽扣不会被逼着上 24 点凸包去省 1 mm，而 300 mm 的西瓜完全不受影响（它的误差是 8–40 mm）。

**体积守卫**：`MAX_ROUND_PROXY_VOLUME_RATIO = 1.5`（球/胶囊）、`MAX_BOX_VOLUME_RATIO = 1.15`（盒）。依据：1.15 低于圆柱的 AABB/凸包 = 4/π = 1.27，所以圆柱永远不会被判成盒；1.5 低于椭球的 1.91，保证「圆代理」不会比它要替代的盒子还大。守卫只对**已通过形状阈值**的候选生效；被守卫拒掉的候选会写进 `reason`。

### 3.4 没有一档达标时

不静默退回盒子。计算盒与 24 点凸包两个候选，取**实际最大间隙更小**的那个，标 `accepted = false`，把每一档的失败比例、体积守卫、以及「如果允许盒旋转本可以过」的诊断写进 `reason`/CSV。理由：24 点凸包 10.1% 的毛球比 20.4% 的盒子好；盲目退回盒子等于把用户刚抱怨的盒子装回去。只有凸包退化/建不出来，或者盒子确实更优时，才落到盒子。

### 3.5 确定性

- 点云不排序（保持输入顺序）；凸包用固定初始四面体 + 稳定 tie-break；FPS 的起点/平局按索引；胶囊搜索是固定网格；所有输出四舍五入到 4 位。
- 同一份 sidecar 两次拟合的 `block()` JSON 逐字节相同；`canonical_collider` 也逐字节相同。测试覆盖。

## 4. 128 件的拟合结果

数据：`Docs/Art/OriginalModels/props/exports/*.fbx` 共 **128** 个 def；sidecar 由 Blender 4.0.2 headless 生成；完整逐件数字见 [collider_proxy.csv](collider_proxy.csv)，分析见 [collider_proxy_report.md](collider_proxy_report.md)。

| 类型 | 件数 | 其中达标 | 说明 |
|---|---:|---:|---|
| sphere | 3 | 3 | `apple`、`marble`、`toy_ball` |
| capsule | 5 | 5 | 西瓜、种子、橡子、榛子、牙签 |
| box | 34 | 32 | 板条箱、书、盒子、长凳、折叠桌、木板等；2 件是不达标的 best-available |
| convex | 86 | 78 | 水果、碗、盘、椅子、婴儿车、手推车等；8 件不达标但仍是最好候选 |
| **合计** | **128** | **118** | 10 件记录 `accepted=false` 与原因 |

12 件点名的道具（`fit_error` 后的 mm 是实际最大间隙；AABB 是今天的行为）：

| def | 选型 | 拟合误差 / 最大间隙 | 体积比 | 今天 AABB 的间隙 / 体积比 |
|---|---|---|---:|---:|
| `watermelon` | capsule（r=110.0 mm, h=30.0 mm, x） | 3.57% / 10.0 mm | 0.97 | 51 mm / 1.97 |
| `toy_ball` | sphere（r=74.0 mm） | 0.73% / 1.1 mm | 1.01 | 31 mm / 2.01 |
| `pebble` | convex（24 点） | 5.09% / 2.0 mm | 0.91 | 8 mm / 2.67 |
| `seed` | capsule（r=3.9 mm, h=4.5 mm, z） | 10.17% / 1.73 mm | 0.99 | 3 mm / 2.52 |
| `banana` | convex（24 点） | 4.74% / 8.5 mm | 0.85 | 16 mm / 2.09 |
| `picnic_bench` | box（1.2×0.45×0.40） | 1.25% / 15.0 mm | 1.08 | 同左 / 1.08 |
| `lounge_chair` | convex（24 点） | 0.96% / 15.3 mm | 0.99 | 26 mm / 1.15 |
| `folding_table` | box（1.0×0.7×0.6） | 0.86% / 8.6 mm | 1.13 | 同左 / 1.13 |
| `stroller` | convex（24 点） | 2.33% / 23.3 mm | 0.98 | 200 mm / 1.75 |
| `wood_plank` | box（1.4×0.025×0.35） | 0.21% / 2.9 mm | 1.00 | 同左 / 1.00 |
| `plate` | convex（24 点） | 3.38% / 8.1 mm | 0.94 | 11 mm / **1.96**（被体积守卫拒） |
| `bowl` | convex（24 点） | 6.62% / 10.6 mm | 0.92 | 26 mm / 1.71 |

**西瓜**：今天 AABB 的角部离果子表面约 40%（短半轴方向最明显）；拟合出的胶囊最大间隙 10.0 mm，体积比 0.97，`containment = 0.77`（约 23% 的表面点在胶囊外，主要在长轴两端，最多 10 mm）。24 点凸包对这颗西瓜反而更差（30 mm / 0.77），所以按成本顺序选胶囊是正确的。**玩具球**：球代理 1.1 mm，AABB 31 mm。**长凳**：它本来就是盒子形，盒子 15 mm 已达标，凸包 11 mm 只便宜 4 mm 却更贵，按顺序保留盒子。

## 5. 与 COLLIDER_LAYER / G13 的边界

- 本块**不**决定「玩家能不能走过去」：COLLIDER_LAYER §3.1 的判据是「空腔净宽 vs 释放球径」，那张表里 13 个 def / 52 件判为「不需要碰撞体」。本文只回答「需要碰撞体时，形状是什么」；要不要给某件挂碰撞体仍是 §10-1 的决定。
- 本块**不**改变挡路门：「挡不挡」仍由 `propBounds` 的体积门决定；将来若把同一份代理喂给 `SetBlockingBoxes`，读的也是**代理数据**，不是运行期 `Collider`（COLLIDER_LAYER §7.1 的不变量）。
- `G13` 的 `k·bbox` 是**吸收体积**；本块的 `volume_ratio` 是**碰撞形状 vs 凸包**。两个体积不是同一个量，不能互相换算，也不能用一个去校准另一个。

## 6. 电机侧契约（只写设计，不改 C#）

前提：接触模型 v2 的静态表是 **256 静态 OBB + 12 prop 槽 = 268 个逻辑槽**，近集 `MaxNearObbCount = 24`，接触容量 `ContactCapacity = 12`（`ContactTuning`）。今天 prop 也走 OBB（`PropObbBakeMath.TryBakeProp`）。下面的契约把「每件一个 OBB」换成「每件一个代理记录」，槽位数、近集大小、接触容量都**不变**。

### 6.1 世界空间烘焙（一次，不是每帧）

`collider` 是局部空间；道具的世界位置/旋转在摆放时已知。**在注册时**把代理烘焙成世界空间，近集和求解器只读世界空间数组，避免每步做四元数旋转：

```csharp
namespace VRCKatamari.BallContact
{
    public static class ProxyContactMath
    {
        public const int KindBox = 0;
        public const int KindSphere = 1;
        public const int KindCapsule = 2;
        public const int KindConvex = 3;
        public const int AxisX = 0;
        public const int AxisY = 1;
        public const int AxisZ = 2;
        public const int MaxHullPoints = 24;
        // Euler: a triangulated convex polyhedron with V <= 24 has F <= 2V - 4 = 44.
        public const int MaxHullFaces = 44;
        public const int MaxPropProxies = 12;

        /// <summary>Bakes one local collider block into world-space arrays.</summary>
        public static bool TryBakeProxy(
            int kind,
            Vector3 propPosition, Quaternion propRotation,
            Vector3 localCenter, Vector3 localSize,
            float localRadius, float localHalfHeight, int localAxis,
            Vector3[] localHull, int localHullStart, int localHullCount,
            int[] localFaces, int localFaceStart, int localFaceCount,
            Vector3[] hullWorld, int hullWorldStart, int hullCapacity,
            Vector3[] faceNormals, float[] faceOffsets,
            int[] faceA, int[] faceB, int[] faceC, int faceOutputStart, int faceCapacity,
            out Vector3 worldCenter, out Vector3 axisX, out Vector3 axisY,
            out Vector3 axisZ, out Vector3 halfExtents,
            out float radius, out float halfHeight, out int axis,
            out int hullCount, out int faceCount, out float boundingRadius)
        {
            // kind == KindBox: worldCenter = propPosition + propRotation * localCenter,
            //                  axisX/Y/Z = propRotation * unit axes, halfExtents = localSize * 0.5
            // kind == KindSphere: worldCenter = ..., radius = localRadius
            // kind == KindCapsule: worldCenter = ..., radius, halfHeight,
            //                      axis = unit axis from propRotation
            // kind == KindConvex:  worldCenter = ..., hullWorld[i] = worldCenter
            //                      + propRotation * localHull[i];
            //                      faceNormals/offsets/A/B/C from the hull triangles,
            //                      all outward, in world space.
            // Bounding radius = max |hullWorld[i] - worldCenter| (convex),
            //                   |halfExtents| (box), r (sphere), h + r (capsule).
            // Refuse a non-finite/negative input, hullCount outside 4..24,
            // faceCount > 44, or a degenerate hull; zero every out value first.
        }
```

`localHull` 是这个 def 的凸包点（相对 `localCenter`）；`hullWorld` 是**已经加上** `worldCenter` 的绝对世界点。**`center` 只加一次**，这是 §2.1 的约定。

### 6.2 逐点查询（求解器只读世界空间）

```csharp
        /// <summary>Exact closest point on one proxy, with distance/normal/feature.</summary>
        public static bool TryClosestPointOnProxy(
            int kind,
            Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
            Vector3 halfExtents, float radius, float halfHeight, int axis,
            Vector3[] hullWorld, int hullStart, int hullCount,
            Vector3[] faceNormals, float[] faceOffsets,
            int[] faceA, int[] faceB, int[] faceC, int faceStart, int faceCount,
            Vector3 point,
            out Vector3 closestPoint, out float distance, out Vector3 normal,
            out int feature)
        {
            // KindBox: return ObbContactMath.TryClosestPointOnObb(
            //     center, axisX, axisY, axisZ, halfExtents, point, ...);
            // KindSphere: normal = normalize(point - center); closest = center + normal*r;
            //             distance = |point - center| - r; feature = FeatureFace;
            //             if |point - center| < 1e-6 use +Y as the normal and distance = -r.
            // KindCapsule: t = clamp(dot(point - center, axisVector), -h, +h);
            //             closestOnSegment = center + axisVector*t;
            //             normal = normalize(point - closestOnSegment);
            //             closest = closestOnSegment + normal*r;
            //             distance = |point - closestOnSegment| - r;
            // KindConvex: best = min over faces of pointTriangleDistance(point, A, B, C);
            //             closest = that triangle's closest point;
            //             normal = normalize(point - closest) if distance > 1e-6,
            //                      else the face normal; feature = Face/Edge/Corner
            //                      from the barycentric coordinates.
            // False on non-finite input or a malformed proxy; out values zeroed.
        }

        /// <summary>Sphere-vs-proxy contact record; box delegates to ObbContactMath.</summary>
        public static bool TrySphereProxyContact(
            int kind,
            Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ,
            Vector3 halfExtents, float radius, float halfHeight, int axis,
            Vector3[] hullWorld, int hullStart, int hullCount,
            Vector3[] faceNormals, float[] faceOffsets,
            int[] faceA, int[] faceB, int[] faceC, int faceStart, int faceCount,
            Vector3 sphereCenter, float sphereRadius,
            out Vector3 normal, out Vector3 point, out float depth, out int feature,
            out float angleDeg, out int contactClass,
            float skin = ContactTuning.ContactSkinM)
        {
            // Closest-point query above; contact iff distance <= sphereRadius + skin;
            // depth = sphereRadius - distance; angleDeg = ContactAngleDeg(normal);
            // contactClass = ContactClassificationMath.ClassifyContact(normal).
            // KindBox MUST call ObbContactMath.TrySphereObbContact so the existing
            // face/edge/corner normal convention stays bit-identical.
        }
```

### 6.3 近集选择

`ContactWindowMath` 现有的 `TrySelectWindow` 只认 OBB 数组和 `TryClosestPointOnObb`。新增一个**不重载**的函数（U# 不支持重载），保留旧函数给「全是盒子」的静态前缀/回滚期：

```csharp
        public static bool TrySelectProxyWindow(
            int proxyCount,
            Vector3[] proxyCenters,
            int[] proxyKinds,               // Kind* per logical slot
            int[] proxyTableIndex,          // index into the 12-slot hull/face arrays; -1 = box
            Vector3[] proxyAxisX, Vector3[] proxyAxisY, Vector3[] proxyAxisZ,
            Vector3[] proxyHalfExtents,
            float[] proxyRadii, float[] proxyHalfHeights, int[] proxyAxes,
            Vector3[] hullWorld, int[] hullStarts, int[] hullCounts,
            Vector3[] faceNormals, float[] faceOffsets,
            int[] faceA, int[] faceB, int[] faceC,
            int[] faceStarts, int[] faceCounts,
            float[] proxyBoundingRadii, int[] proxySurfaceIds,
            Vector3 center, float radius, float maxTravel,
            int[] previousSurfaceIds, int previousCount, int capacity,
            int[] scratchIndices, float[] scratchDistances, int[] selectedIndices,
            out int selectedCount, out int dropped, out bool complete, out bool rebuilt,
            float enterMargin = ContactTuning.WindowEnterMarginM,
            float exitMargin = ContactTuning.WindowExitMarginM,
            float completeMargin = ContactTuning.WindowCompleteMarginM)
        {
            // Identical to TrySelectWindow: bounding-sphere lower bound, then exact
            // TryClosestPointOnProxy for survivors, hysteresis, partial selection sort,
            // overflow verdict and rebuilt flag.  The ONLY new part is the exact query.
        }
```

- 静态前缀的 256 槽 `proxyKinds = KindBox`、`proxyTableIndex = -1`；12 个 prop 槽写各自的 kind 和 0..11 的 shape 索引。这样同一个 268 槽逻辑表和 24 槽近集不变。
- `proxyBoundingRadii` 对 box 仍是 `ObbContactMath.BoundingRadius`；对球 r；胶囊 h+r；凸包 max |hullWorld - center|。宽相位行为不变。

### 6.4 接触收集

`ContactSolveMath.TryCollectContacts` 的镜像（同样不重载）：

```csharp
        public static bool TryCollectProxyContacts(
            bool hasGround, Vector3 groundNormal, float groundOffset,
            Vector3[] planeNormals, float[] planeOffsets, int planeCount,
            int[] proxyKinds, int[] proxyTableIndex,
            Vector3[] proxyCenters, Vector3[] proxyAxisX, Vector3[] proxyAxisY,
            Vector3[] proxyAxisZ, Vector3[] proxyHalfExtents,
            float[] proxyRadii, float[] proxyHalfHeights, int[] proxyAxes,
            Vector3[] hullWorld, int[] hullStarts, int[] hullCounts,
            Vector3[] faceNormals, float[] faceOffsets,
            int[] faceA, int[] faceB, int[] faceC,
            int[] faceStarts, int[] faceCounts,
            int[] proxySurfaceIds, int[] proxyRoles, int proxyCount,
            Vector3 sphereCenter, float radius,
            Vector3[] contactNormals, Vector3[] contactPoints, float[] contactDepths,
            float[] contactAngles, int[] contactSurfaceIds, int[] contactRoles,
            int[] contactFeatures, int[] contactClasses, int capacity,
            out int contactCount, float skin = ContactTuning.ContactSkinM)
        {
            // Ground first (surface id 0), then boundary planes (1000 + i), then the
            // near proxies.  One contact per proxy at most; capacity 12 unchanged.
            // An invalid proxy contributes no contact (fail closed; the window/bake
            // should already have refused it).
        }
```

### 6.5 每步成本估计（24 prop 窗口、每件 ≤12 面）

- **近集**：每步一次 `TrySelectProxyWindow`。宽相位先做包围球（6 flops/槽）；只有 268 槽里靠近的少数进入精确查询。最坏 24 个近集代理 × 12 面 = **288 次面平面测试/步**；按 50 Hz ≈ 14.4k/s。
- **接触收集**：`MaxSubSteps = 8`、`SolverIterations = 3` 时，最坏每步 `288 × 3 × 8 = 6912` 次面测试（≈0.35M/s）；每个面测试是 1 次点积 + 1 次比较；凸包查询还需要点-三角形最近点（约 30 flops）。按 Udon 的解释器开销，**估计 0.1–0.3 ms/步（桌面 CPU）**；没有实测，不能当成验收数字。
- **近集重建**：只在 `rebuilt == true` 时把选中代理的 24 个 hull 点和 ≤44 个面复制进 near 数组（≤24×44 = 1056 个面平面，约 24 KB 拷贝）；滞回（0.5 m 退出带）把重建频率压到「集合变化」时，不是每步。
- **容量**：`MaxStaticObbCount = 256`、`MaxPropObbCount = 12`、`MaxNearObbCount = 24`、`ContactCapacity = 12` **全部不变**。新增内存约 18 KB（12 件 × 24 点 × 12 B = 3.5 KB；12 × 44 面 × 28 B = 14.8 KB；槽位元数据很小），在 v2 现有约 25 KB 的同一量级；如果更在意，可把 hull 面上限压到 32（V=20 时）。
- **风险**：**不要给 256 个静态槽上凸包**。静态全凸包需要 256×24×12 B ≈ 74 KB 点 + 256×44×28 B ≈ 315 KB 面，超出预算且窗口每步复制成本爆炸。静态继续只用 OBB；凸包只给 12 个 prop 槽。静态要拆形状时走 `COLLIDER_LAYER` 的多盒/AddPlane 路线，不是这条路。

### 6.6 要动/不要动的 C# 位置

| 文件 | 改什么 |
|---|---|
| `Runtime/BallContact/ProxyContactMath.cs`（新） | §6.1–6.2 的烘焙、最近点、球-代理接触；`KindBox` 分支必须委托 `ObbContactMath`，保持现有法线/特征码逐位一致 |
| `Runtime/BallContact/ContactWindowMath.cs` | 新增 `TrySelectProxyWindow`；旧 `TrySelectWindow` 保留 |
| `Runtime/BallContact/ContactSolveMath.cs` | 新增 `TryCollectProxyContacts`；旧函数保留给纯 OBB 回滚期 |
| `Runtime/BallContact/PropObbBakeMath.cs` | 新增 `TryBakeProxy`（或把 §6.1 放 `ProxyContactMath`）；`TryBakeProp` 保留为 AABB 回退 |
| `Runtime/BallMotor/KatamariBallMotor.cs` | `SetPropObbs` → `SetPropProxies`（或并行新增），`FillNearWindow` 改调 `TrySelectProxyWindow`，接触收集改调 `TryCollectProxyContacts`；**本任务不改** |
| `ObbContactMath.cs` | **不改**。它是盒子的后端，也是回归基准 |
| `ContactTuning.cs` | 只加 `MaxProxyHullPoints = 24` / `MaxProxyFaces = 44`；不改 24/12/256 常量 |

**验收（C# 侧下一步）**：① 同一件 box 代理与旧 `TryBakeProp` 产出的 `center/axes/halfExtents` 逐位相等；② 球/胶囊/凸包与解析解的单测（球-球、球-胶囊、球-凸包面/棱/角）；③ 12 件全 box 时 `TryCollectProxyContacts` 与 `TryCollectContacts` 的接触记录逐位相等（回滚保证）；④ 24 窗口下 `GetNearObbCount()` 和 `contactIncomplete` 语义不变；⑤ 一件西瓜从 AABB 换成胶囊后，`propBounds`、吸收门、`GetBlockingPropCount()` 全不变。

## 7. 明确的限制（写下来，避免下一个人重新发现）

1. **一个凸代理表达不了凹**：桌下穿行、碗内壁、篮筐内腔、椅子扶手之间。COLLIDER_LAYER 的多盒方案才是这些件的答案；本文的 24 点凸包会把桌下填实（和今天的盒子一样），只是外轮廓更贴。报告里的 `bench` 就是盒子，`bowl` 的 `containment` 只有 0.31（内壁在凸包内），这是已知的、可接受的单代理上限。
2. **盒子没有旋转字段**：schema 只有 `size`。拟合器内部会做一个 PCA 定向盒的**诊断**（CSV 的 `reason` 会写「oriented box would pass」），但不会输出它；如果某个件真的只有定向盒能过，它会被凸包（≤24 点）接住。将来若要加 `rotationQuat`，必须先改 schema + 校验 + C# 契约，不是本文的追加。
3. **凸包点是相对 `center` 的偏移**，不是绝对坐标；消费方只加一次 `center`。这条最容易在 C# 里写错，测试里专门用非零 `center` 的 L 形块覆盖。
4. **`fit_error` 是单向表面距离**（网格→代理），所以「盒子角部外凸」在盘子上可能不可见（盘的上下表面与盒面齐平）。体积守卫 1.15 补这个盲区；`volume_ratio` / `containment` 是两个诊断，不是新的判定量。
5. **点云没有面时没有网格体积**：`mesh_volume_ratio` 为空；`volume_ratio` 用凸包体积。真实 FBX 都有三角面。
6. **细节级别**：24 点的弦高误差在球面上约 10%，这是「低模」的定义，不是 bug；要更贴就加预算（25+ 点），但那会改 schema 上限和 C# 面容量，不能偷偷加。

## 8. 复现

```bash
# 1) 128 个 FBX -> 点 sidecar（仓库外；约 8 s）
source ~/tools/blender-env.sh
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/fit_collider_proxy.py -- \
    --out /home/fulutang/KatamariRef/meta/collider_proxy/points

# 2) 拟合 + CSV（纯 Python，约 13 s）
python3 Tools/levelbuild/collider_proxy.py \
    --points-dir /home/fulutang/KatamariRef/meta/collider_proxy/points \
    --csv Docs/Design/PickupAbsorb/collider_proxy.csv \
    --json /tmp/collider_proxy_blocks.json

# 3) 测试与规划门
python3 -m unittest discover -s Docs/tests
python3 Docs/tools/validate_plan.py
```

## 9. 验证状态

**已做（离线）**：
- 18 个 `test_collider_proxy.py` 用例：schema 往返/字段序/四类必填与跨类型拒绝、凸性拒绝（内点/点数/NaN/共面）、代理越界 AABB、无块文档与 git HEAD 逐字节一致（story_01 + 9 个 physics cell）、合成完美球（误差 4.6e-16）、立方体→盒、胶囊→胶囊、L 形→盒/凸包且记录体积过剩、圆盘不选球/盒、确定性。
- 128 件 FBX 全部拟合；CSV 与报告落盘。
- `python3 -m unittest discover -s Docs/tests`：**除一个与本次无关的既有失败**（`test_physics_test_field_cells.CriteriaModels.test_v1_rows_are_byte_identical_to_git_head`，另一个 agent 正在改 `physics_test_field.json`）外全绿；本次新增 18 个用例全过。
- `python3 Docs/tools/validate_plan.py`：见最终报告。

**没做（明确未验证）**：
- 没有 Unity/Udon 编译、没有 EditMode 运行、没有 Play Mode/VR、没有真机走测；第 6 节的成本和签名是设计，不是实测。
- 没有把任何 `collider` 块写进真实的 `placement.json`（本轮只交付 schema + 拟合数据 + 报告；填数据要等 C# 消费方就位，否则会出现「数据有、没人读」的中间态）。
- 没有改 `.meta`、没有生成/提交任何二进制。
