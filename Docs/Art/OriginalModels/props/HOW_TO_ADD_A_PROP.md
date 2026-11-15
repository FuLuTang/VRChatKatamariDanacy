# 怎么加一件道具（原创程序化道具流水线）

> 面向：给本目录加一个新 `def`（一个可独立被卷走的物件）的人或 agent。
> 状态：2026-11-15 写成，依据现有 48 件道具的真实脚本与构建产物；不是设计愿望清单。
> 本目录的 README 记录每一批做了什么、为什么改；本文只讲**流程与硬规则**，两者不重复。

## 0. 一页流程

1. 在道具目录（`Docs/Design/Maps/<Theme>/catalogue.csv` 或 `prop_catalog.csv`）里选一个 id，确认它的真实三轴和 tier。
2. 写 `scripts/models/<id>.py`：模块常量 + `build(b)`，只用 `prop_lib` 图元。
3. 在 `scripts/models/__init__.py` 的 `ORDER` 末尾登记 id（**只有一个人改这个文件**）。
4. 用 staging 目录构建单件 → 看 `previews/<id>_preview.png` → 改到顺眼。
5. 回读验证 `verify_import.py`，确认 FBX/GLB 尺寸、面数、枢轴。
6. 全量构建一次，更新 `manifest.json` 与 `SHA256SUMS`。
7. 交给 Unity 操作员（**不是模型作者**）导入 `exports/<id>/`；作者不碰 Unity 工程。

## 1. 硬规则（项目所有者的规则，违反即返工）

| 规则 | 具体要求 | 为什么 |
|---|---|---|
| 真实尺寸 | 模型最长边 = 现实尺寸（米），写在模块的 `TARGET_LONGEST_M`，与 catalogue 的 `real_*` 一致；`SIZE_REFERENCE = 'real'` | 玩法按真实尺寸算体积/收益；「游戏内放大」的旧约定已于 2026-11-15 废止 |
| Unity 里不缩放 | 导出时几何已烘焙成米；Unity 侧 Import 的 Scale=1、Use File Scale 关，Prefab 的 Transform 是 identity | 任何 `localScale` 都会让接触判定和 AABB 数据对不上 |
| 枢轴在几何包围盒中心 | `Builder.finish()` 统一处理，不要自己写 origin；导出前 object 的 location 必须归零 | 场景按 AABB 中心摆件，运行期接触测试把 itemPosition 当中心 |
| 静置姿态 | 按物件真实静置方式建模：书平放、瓶子按真实站/躺、长杆平放；地面是建模时的 `z = 0`。`finish()` 之后枢轴移到中心，底面自然落在 `z = -h/2`，这是预期 | 玩法里道具以当前姿态被吸附；「立着浮空」的模型一进场景就穿地 |
| 低多边形 | tiny ≤300 tris、小件 ≤400–500、中件 ≤700–900、大件 ≤1200–1800、支架/地标 ≤3500；参考同尺寸现有件 | 一关几百件实例，面数是 draw/内存的大头 |
| 材质槽少而命名清楚 | 1–3 个纯色 Principled 材质，key 用小写英文（`paper`、`metal`、`fabric`），中文名写 `name_zh`；**无贴图、无 UV、无顶点色** | Unity 侧按 manifest 的 `material_base_colors` 重建 `.mat`；槽名是 prefab 的稳定接口 |
| 单一闭合壳 | `closed_shells: 1`、`non_manifold_edges: 0`、`zero_area_faces: 0`、`ngons: 0`、`loose_vertices: 0`、`duplicate_faces: 0` | 闭合壳才能稳定算法线、算体积、做 LOD/碰撞代理；破壳在 Unity 里会闪面 |
| 法线朝外 | 每个图元内部已 `recalc_face_normals`；布尔后必须再检查 | 反面材质在 VRChat 里是黑的 |
| 确定性 | 只用固定公式；禁止 `random`、时间戳、`uuid`、外部文件；同一脚本两次构建的 `geometry_sha256` 必须相同 | 复现与审查；manifest 是可核验契约 |
| 无第三方内容 | 不复制任何现有游戏/商业包的网格、贴图、Logo；品牌件用中性名（`toy_brick` 不是 LEGO） | 授权未定；未授权素材不得进正式场景 |

### 尺寸与 tier

catalogue 的 `tier` 按**最大边长**分档（第一次能被吞的球径档）：

| tier | 球直径带 (m) | 物件最大边 (m) |
|---|---|---|
| t0 | 0.30–0.40 | 0.02–0.099 |
| t1 | 0.40–0.55 | 0.10–0.139 |
| t2 | 0.55–0.75 | 0.14–0.199 |
| t3 | 0.75–1.00 | 0.20–0.299 |
| t4 | 1.00–1.25 | 0.30–0.449 |
| t5 | 1.25–1.60 | 0.45–0.649 |
| t6 | 1.60–2.20 | 0.65–0.999 |
| t7 | ≥2.20 / 不可吞地标 | ≥1.00 |

## 2. 文件放在哪

```
Docs/Art/OriginalModels/props/
├── scripts/
│   ├── prop_lib.py            # 图元 + Builder + 导出 + 预览（共享，改它要全量回归）
│   ├── build_all.py           # 构建入口（--props 过滤，--out 改输出根）
│   ├── verify_import.py       # FBX/GLB 回读验证
│   ├── sign.py                # 只重算 SHA256SUMS
│   └── models/
│       ├── __init__.py        # ORDER 注册表（唯一）
│       └── <id>.py            # 一个 def 一个文件
├── exports/<id>.fbx|.glb|.blend|<id>.manifest.json
├── previews/<id>_preview.png  + contact_sheet*.png
├── manifest.json              # 全量总清单（48 件汇总）
└── SHA256SUMS                 # 全部交付物的指纹
```

**一个 def = 一个可独立被卷走的实体。** 由多个 mesh 组成的椅子仍是一个 def（`finish()` 会 join 成一个对象、多个材质槽），不能拆成 40 次收益。

## 3. 写一个模型模块

最小可运行模板（照 `models/pebble.py` 的结构）：

```python
"""一句话说清这是什么、真实尺寸、为什么这么建。"""

import math
from prop_lib import lathe            # 只用需要的图元

ID = 'straw'                          # 与文件名、catalogue id 一致
NAME_ZH = '吸管'
CATEGORY = 'tableware'
SOURCING = 'model ourselves：catalogue t0，长 0.20 m 空心管'
SIZE_REFERENCE = 'real'
TARGET_LONGEST_M = 0.20
TRI_BUDGET = 300
# catalogue 里没有这个 id 时，用它补上 build_all 需要的两列；有 CSV 行就不需要。
DOC_ROW = {'id': ID, 'name_zh': NAME_ZH, 'category': CATEGORY,
           'real_longest_dim_m': '0.20', 'game_longest_dim_m': '0.20'}
MATERIALS = [
    {'key': 'plastic', 'name_zh': '奶白塑料', 'linear_rgb': [0.78, 0.75, 0.70],
     'roughness': 0.45},
]
NOTES = ('12 边空心管，壁厚 0.4 mm；两端开口用内壁环封口保持单壳；'
         '平放在 z=0 上。')


def build(b):
    # 用图元造 bmesh，再 b.add(bm, key, rgb, roughness, smooth=...)
    ...
```

### 模块常量

| 常量 | 作用 | 不写会怎样 |
|---|---|---|
| `ID` / `NAME_ZH` / `CATEGORY` | 写进 manifest、目录对账 | 直接报错 |
| `SOURCING` | 来源提示（自建/CC0 包名/待定） | manifest 缺字段 |
| `SIZE_REFERENCE = 'real'` | 用 `real_longest_dim_m` 校核 | 回落到旧的 game 列，尺寸告警 |
| `TARGET_LONGEST_M` | `finish()` 均匀缩放的目标最长边 | 必填 |
| `TRI_BUDGET` | 面数预算，构建报告里 `within_budget` | 必填 |
| `MATERIALS` | 材质槽声明（key/name_zh/linear_rgb/roughness） | manifest 少材质信息 |
| `DOC_ROW` | catalogue 无此行时的替补 | 构建硬失败 |
| `NOTES` | 建模取舍（读图后改过什么、踩过什么坑） | 可选但强烈建议 |

### `prop_lib` 图元速查

| 图元 | 适合 | 关键参数 |
|---|---|---|
| `lathe(profile, segments)` | 旋转体：瓶、罐、碗、纽扣、蘑菇伞 | `profile` 是自下而上的 `(radius, z)` 列表；`radius_fn` 做花瓣/瓶盖凹槽 |
| `tube(points, radii, segments)` | 扫掠：吸管、树枝、腿、提手、线缆 | 折线 + 每点半径；`section_fn` 做扁带/棱纹 |
| `sheet(nx, ny, x0, x1, y0, y1, top_fn, thickness=)` | 薄板：纸、卡、叶、布、标签 | 顶/底高度场；`thickness` 给定厚度 |
| `disc_grid(segments, ring_fractions, top_fn, bottom_z)` | 圆盘：饼干、硬币、瓶盖、圆片 | 极坐标顶面 + 平底；`radius_fn` 做波浪边 |
| `prism(points, length, axis)` | 直棱柱：盒、箱、书、卡套 | 二维闭合多边形；端面三角化 |
| `rounded_rect(w, d, r)` | 与 `prism` 搭配的圆角矩形轮廓 | `r` 必须小于短边一半 |
| `uv_sphere` / `icosphere` | 球、果、石、珠 | `segments/rings` 或 `subdivisions` |
| `metaball_cluster(spheres, resolution)` | 有机融合体：粪球团、肉丸、云 | 只在确实需要融合时用，面数要盯 |

`Builder`：`b.add(bm, key, rgb, roughness=0.65, smooth=True)`；需要并集/挖洞时 `b.boolean_union(target, tools)` / `b.boolean_difference(target, tools)`（Exact），**布尔之后必须保证仍然单壳**。`b.finish(TARGET_LONGEST_M)` 会 join、三角化、按最长边缩放、把枢轴移到包围盒中心、统计拓扑。

### 常见坑（现有 48 件踩过的）

- **腿/管件的平头端要插进上方面板 10 mm**，正好停在板底会导出 2 个壳。
- **单一中撑碰不到两侧腿** → 2 个壳；改成两条与腿同排的撑。
- **容器开口被上方盖子封住** → 内部变封闭空腔，多一个壳；要么留缝，要么改成实心。
- **圆角/倒角不能产生重复点**（`r=0` 的 `rounded_rect` 会重复顶点）。
- **薄片必须是闭合薄板**，不是双面单层片（`sheet` 已封闭）。
- **布尔工具（要减掉的那块）的 `lathe` 剖面一律按 +Z 升序写**：从下往上给半径。反过来写会让工具法线朝内，差集得到的是「凸起的浅色尖锥」而不是凹坑（button 的四个线孔第一版就是这么错的）。
- **n 边形端面**由 `finish()` 三角化，但布尔产生的退化面要先 `dissolve_degenerate`（`finish()` 已做两遍）。
- **平滑着色**按形状选：球/果实/瓶身平滑；纸、石、木箱、机械件平直。同一对象可以分材质但**不能分平滑组**——`finish()` 按对象设置。
- **最长边 < 33 mm 的件预览是纯空白**（2026-11-15 修）：正交相机在 `span*3` 处、默认近裁剪 0.1 m 会把小件整个裁掉。`prop_lib.render_preview` 已按 `clip_start = span*0.01 / clip_end = span*50` 缩放；如果你的 staging 副本是旧的，会看到 1 KB 的空白 PNG。

## 4. 构建（Linux 无头 Blender）

```bash
source ~/tools/blender-env.sh          # BLENDER + BLENDER_FLAGS
cd /home/fulutang/Documents/Github/VRChatKatamariDanacy

# 单件 / 一小批，输出到仓库外（绝不覆盖已提交的 exports/）
"$BLENDER" $BLENDER_FLAGS \
  --python Docs/Art/OriginalModels/props/scripts/build_all.py -- \
  --out /tmp/props_stage --props "straw,coin,marble"

# 回读验证
"$BLENDER" $BLENDER_FLAGS \
  --python Docs/Art/OriginalModels/props/scripts/verify_import.py -- \
  --out /tmp/props_stage --props "straw,coin,marble"
```

- 构建成功以 `BUILD_ALL_OK` 结尾；单件报告行给出 `tris / verts / longest / shells`。
- `--props` 里的 id 必须已登记在 `models/__init__.py`，否则硬失败。
- 输出根默认是仓库 `props/` 目录；**日常开发一律 `--out` 到 `/tmp` 或 Windows 预览目录**，只有最终合并才写回仓库。
- Windows staging：把 `scripts/` 和 `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 按仓库相对路径镜像到 `_stage/Docs/...`，因为 `build_all` 按 `prop_lib.ROOT` 找 CSV（README §10.1）。

## 5. 看预览图（必须做）

构建会出 `previews/<id>_preview.png`（512×512，透明底，3/4 视角）和整批 `contact_sheet.png`。

**作者必须用 `view_image` 亲眼看每一张预览，并修掉可见问题再交付。** 常见可见问题：

- 轮廓读不出是什么（吸管像牙签、橡皮像方糖）→ 加一个识别特征（吸管弯头、橡皮纸套、瓶盖锯齿）。
- 长宽比不对 / 被画幅裁掉 → 检查 `TARGET_LONGEST_M` 与相机 `ortho_scale`。
- 材质对比太低（同色系两件分不开）→ 拉开明度，不要只改色相。
- 内部黑面 / 破面 → 壳数 >1 或法线反了。
- 底面悬空或陷进地面 → 静置姿态错，或 `finish()` 后有人手改了 location。

## 6. 合并与登记

1. 把 `models/<id>.py` 复制回仓库；**只在** `models/__init__.py` 的 `ORDER` 末尾追加 id（一个批次一段注释，写明批次与来源）。
2. 全量构建一次（不带 `--props`），输出到 `/tmp`，确认 48+N 件全过、`within_budget` 全 yes。
3. 全量构建写回仓库（`exports/`、`previews/`、`manifest.json`、`SHA256SUMS`）。
4. `python3 -m unittest discover -s Docs/tests` 必须仍全绿；`python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/<图>` 若动了地图数据也要跑。
5. 把本批写进 README 的批次小节（尺寸/面数表、读图后改了什么、未做什么）。

```bash
# 只重算总指纹，不重跑几何
"$BLENDER" $BLENDER_FLAGS --python Docs/Art/OriginalModels/props/scripts/sign.py
```

## 7. 交给 Unity（不是模型作者做）

- 只导入 `exports/<id>.fbx`，**不要**导入 `.blend`（本机 Unity 无 Blender 桥）或 `.glb`（与 FBX 重复）。
- Import 设置：Scale=1、Use File Scale 关、Generate Colliders 关、Read/Write 关、Mesh Compression Off、Material Import Mode = `ImportStandard` + `External`。
- 材质按 `<id>.manifest.json` 的 `material_base_colors` 重建为 `Prop_<id>_<slot>.mat`；**不要提交 `.meta` 之外的新文件给作者**——Unity 侧的一切由 Unity 操作员完成。
- 导入后回读：最长边与 manifest 误差 < 1 mm，三角面数、材质槽名逐一相同。

## 8. 交付检查表

- [ ] id 与 catalogue 一致，且不在 `models/ORDER` 里重复
- [ ] `TARGET_LONGEST_M` = catalogue 真实最大边（误差 < 1e-6 m）
- [ ] `SIZE_REFERENCE = 'real'`，`DOC_ROW` 或 CSV 行齐备
- [ ] 1–3 个材质槽，key 小写英文，`MATERIALS` 与 `b.add` 的 key 完全一致
- [ ] `closed_shells = 1`，其余拓扑计数全 0
- [ ] `within_budget = true`
- [ ] 两次构建 `geometry_sha256` 相同
- [ ] 亲眼看过预览图，并修过至少一轮可见问题
- [ ] `verify_import.py` 的 FBX/GLB 回读全 `ok`
- [ ] 没有 `.meta`、没有碰 Unity 工程、没有 git 写操作
