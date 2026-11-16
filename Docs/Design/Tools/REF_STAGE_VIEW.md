# 参考关卡的原作舞台（地形 / 碰撞 / 真实例变换）

> 本页只描述 `_ref_*` 关卡在本机浏览器里**多看一眼**的原作数据。所有数据都是在仓库外
> 由我们自己的提取器（`~/KatamariRef/tools/katamari_stage_extract/`）从 PS2 原盘包里
> 读出来的 **UNLICENSED** 参考材料：不进 git、不进 `Docs/Art`、不进 Unity 工程、不上传。
> 这里写的代码只把它们**画出来**，不复制任何字节进仓库。

## 0. 一句话

`_ref_cancer` 的 placement 里只有"道具在哪"，没有"地在哪"：原来的平地/边界平面是假
的。现在参考模式会额外加载提取器输出的**原作舞台网格**（29,846 顶点 / 44,340 索引 /
232 个材质段）、**68 个碰撞 AABB + 3,456 个碰撞三角形**、**7 个 zone**，并按
`instances.json` 给每件道具套上**原作旋转 / 缩放 / y**，于是主人能看到真正的地形
（台地、挡土墙、坡道、沟壑）以及道具站在上面的样子。

## 1. 数据来源（仓库外）

默认目录 `~/KatamariRef/noclip_export/kse_out`（`$VRC_KATAMARI_REF_STAGE` 覆盖，CLI
`--ref-stage-dir` 再覆盖；同级的旧目录 `../out` 是备选）：

| 文件 | 内容 | 本页怎么用 |
|---|---|---|
| `stage.glb` | 7 个网格节点、REROLL 帧（米、y 向上） | 地形（`/api/ref-stage/stage.glb`） |
| `instances.json` | 524 条 spawn 记录（PS2 帧、厘米）：`t` / `axis`+`angleRaw` / `scale` / `parentTableIndex` / `nameIdx` | 真实例变换 + 父表 |
| `collision_area2.json` | 68 个碰撞条目：AABB + 顶点组（每顶点 4 个 float）+ 三角形（索引按组局部编号，带 zone id） | 碰撞叠加层 |
| `zones.json` | 7 个 zone 多边形（多为平板地面/顶面） | zone 盒 + 分区统计 |
| `report.json` | `"space": "reroll"`、网格计数 | **空间断言**：不是 `reroll` 就拒绝（否则会画出镜像地图） |

`Tools/levelbuild/ref_stage.py` 是唯一读这些文件的地方（`ref_models.py` 读的是道具
网格）。目录必须在仓库之外，否则启动直接拒绝（和 `ref_models.normalized_models_dir`
同一条规则）。提取器本体不在仓库里，也不提交它的输出。

## 2. 坐标：两帧、两次反射、绕序

- 量准的映射：`reroll(米) = −0.01 × ps2(厘米)`，三轴全取反（点反射，行列式 −1）。
  实测：511 件道具里 **422 件** 与 `placement.model.json` 的位置差 ≤ 1 cm，中位误差
  **0.000043 m**；剩余 89 件见 §7。
- 编辑器画图帧：`three = (x, y, −z)`（项目帧 → three.js 的那一对互逆函数）。
  于是 **PS2 → three 的累积映射是两次反射 = 一个旋转（det +1）**。
- 所以 `stage.glb` 的**索引顺序不用翻**：实测转换后三角形绕序与法线 **14,672/14,734
  一致（99.6%）**，几何法线朝上的比例 3,838:1,356（地面朝上）。反过来，`stage.glb`
  若直接当 REROLL 数据加载是**里外翻**的（绕序与法线只有 0.4% 一致）——这符合格式
  笔记里"并入 REROLL 要翻绕序"的说法，是提取器侧待修的一条（见 §8）。
- 旋转：`axis`(+0x20) + `angleRaw`(+0x2C，存的就是**负角**)，用
  `core.quat_axis_angle(axis, −angleRaw)`；与提取器自己算的 `q` 逐位一致（≤2.2e−16）。
  点反射把旋转共轭到自身，所以不需要再换符号。
- 缩放：`scale`(+0x30)。6 件非 1（四件 0.9、一件 0.1、一件 `(0.0741, 0.0291, 0.0197)`）；
  语义仍未验证（格式笔记 §11 的未决项），所以只在**只读视图**里生效，且选中面板显示原值。
- 高度：placement 把所有道具**压平到 y = 0**（`notes` 里的 `src_y` 才是原作 y）。视图里
  「原作高度」开关把 `group.position.y` 换成实例的 `t.y`（显示用，**文件不动**）。

## 3. 服务端

`Tools/leveleditor/server.py`：

- 打开 `_ref_*` 关卡时 `_refresh_ref_stage()` 调 `ref_stage.prepare(...)`：读实例表、
  检测帧、配对道具、读碰撞/zone，全部放进 `WebEditor.ref_stage`。**失败不阻断打开**：
  `ref_state()["stage"]["reason"]` 说明为什么没有地形。
- 端点（都在 `/api/` 下，因此都要 token；都带 `X-Unlicensed-Reference: 1` 与
  `Cache-Control: no-store`）：

| 端点 | 返回 |
|---|---|
| `GET /api/ref-stage/stage.glb` | 转换后的地形 GLB（z 取反、POSITION/NORMAL 镜像、min/max 重算、asset 版本补齐） |
| `GET /api/ref-stage/transforms.json` | `byItemId` / `byInstance` / `parents` / `anchors` / `unmatchedProps` / `counts` / `frame` |
| `GET /api/ref-stage/collision.json` | `aabbs`（viewer 帧 min/max）、`triangles`（9 float/三角形）、`triangleZones`、`zones` |
| `GET /api/ref-stage/contact?t=0.1` | 地面接触测量（§6），纯 Python 射线，**分析逻辑不只在浏览器里** |
| 其它名字 | 404 |

- 普通关卡永远拿不到这些字节（`ref_mode` 守卫），关卡关掉后也没有。
- 启动行会打印 `stage: … 29846 顶点, 68 碰撞盒, 3456 三角形, 511/511 道具还原实例,
  83 父表连线, 7 zone`。

## 4. 实例 ↔ itemId 解析表

`instances.json` 没有 itemId，只有 `nameIdx` + `tableIndex`。解析：

1. `original_defs.json` 的 `mono` 字段 = `nameIdx` → 我们的 def id（145 个 def，mono 唯一）。
2. 同一个 def 内按 **x/z 最近** 贪心配对（y 不参与：实例的 `t` 是模型原点，placement 的
   `position` 是 AABB 中心，两者 y 本来就不同一语义）。
3. `nameIdx` 在 def 表里查不到的是**哑锚点**（dummy attach anchor，Cancer 13 条：
   nameIdx 499/619/631/1695）——它们不是道具，单独列出，不参与配对。

Cancer 结果：524 实例 = **511 道具 + 13 哑锚点**，511/511 全部配上，0 个道具无实例。
这也解释了老文档里的三个数字：REROLL 侧 523 = PS2 侧 524 − 1，我们的 placement 511 =
524 − 13 个哑锚点。

父表：`parentTableIndex` 按 (area, tableIndex) 找父实例 → 父道具 itemId。Cancer：
90 条挂接件（86 件在我们 511 里），**83 条能落到我们的道具上**，正好和 placement 的 83
条 `group`/`groupId` 同数；逐条比对 **78 条相同，5 条不同**——两件 bookstand 位置几乎
重合，贪心配对把它们对调了，于是 `bookstand_001/002` 和 4 个 cubby 的父指针换了个方向。
剩下 3 条指向哑锚点（`fiddler_crab_039` / `daruma_001` / `iron_001`），画不出来。

## 5. 视口

顶部工具条在参考模式多出一组开关（普通关卡隐藏）：

| 开关 | 默认 | 作用 |
|---|---|---|
| **地形** | 开 | 原作舞台网格，替换 placement 的平地+4 面边界平面（也隐藏编辑器自己的草坪/网格，它们画在 y=0，会盖住 -1 m 的真地面） |
| **线框** | 关 | 地形线框（此时改 DoubleSide，否则远面被裁掉一半） |
| **碰撞** | 关 | 68 AABB（橙）+ 3,456 三角形棱（青）+ 7 zone 盒（紫）；首次打开时才拉 `collision.json` |
| **旧平面** | 关 | 把 placement 自己的 ground/boundary 平面和草坪网格放回来对照 |
| **原作高度** | 开 | 用实例 `t.y` 代替压平的 y（只改显示） |

实现细节：

- 地形材质：一个哑光石材色、`FrontSide`（绕序正确后就是原作的裁剪面）；顶点按高度做了
  一条**仅用于观看**的渐变色带（`_tintTerrainByHeight`），否则无贴图的灰盒子分不出台地。
- 天空壳：网格对角 > 2× 关卡对角（Cancer 1 个：`area2_sector1_model0`，半径 121 m）
  视为远景/天球，默认不画（提取结果没有贴图，画出来只是一圈灰墙）。
- 相机预设（3/4、俯视）在参考模式下按"地形 ∩ 关卡 bounds"取景，避免被 106 m 的远景墙
  和天球拉远；`点到即选`、区域框选、球径阶梯、区域统计照旧。
- 父子连线：有提取器父表时画父表（83 条），其中与 placement `group`/`groupId` 一致的是
  **洋红**，只有提取器知道的（当前 0 条）是**橙**；没有父表时退回原来的 `group` 连线。
- 选中面板新增：**原作旋转**（角度 + 轴 + 四元数）、**原作缩放**、**原作父件**（+ 实例号）、
  **分区**（area / 实例号 / 它脚下那块碰撞三角的 zone 与面高）、**高度**（显示 y vs 文件 y）。
  实测两例（headless Chromium 里读 `#selection-kv`）：

  | 道具 | 面板读数 |
  |---|---|
  | `cinder_block_001` | 原作旋转 `-120° @ (-0.58, 0.58, -0.58)`、四元数 `(0.500,-0.500,0.500,0.500)`、缩放 `(1,1,1)`、父件 `无`、分区 `area 2 · 实例 #86 · 碰撞 zone 0（面 y=-0.82）`、高度 `显示用原作 y=-0.897 · 文件 y=0.060` |
  | `cubby_002` | 旋转 `0°`、父件 `bookstand_002 #474`（**placement 的 `group` 写的是 `bookstand_001`**，正好是 §4 那 5 条换向之一）、分区 `area 2 · 实例 #478 · 碰撞 zone 0（面 y=-0.85）` |

  注意面板里「世界轴对齐盒 / 世界 AABB / D_req」仍是服务器按**文件**位置算的派生值，不跟着
  「原作高度」的显示偏移走（只读视图里没有编辑，所以没有一致性风险，但读数时要知道这一点）。
- 横幅显示：地形顶点/碰撞盒数、实例还原件数、带旋转/缩放的件数、zone 数、坐标帧与中位
  误差；`/api/ref-stage/contact` 的数字不塞进横幅（太长），用命令或下面的复现步骤取。

## 6. 实测（Cancer，1600×1000，headless Chromium + SwiftShader 软件 GL）

同一台机器上还有别的 headless 渲染在跑（load average ≈ 3–4），所以时间是**上界**：

| 项 | 值 |
|---|---|
| 页面 ready | 16 s（空闲）～ 87 s（争用最重时）；内部 `readyMs` 41.6 s |
| `load()` 总计 | **3.39 s**（145 个道具 GLB 2.46 s、ref stage 0.90 s、摆放 0.07 s） |
| 地形 GLB 首次转换（Python） | 0.20 s；之后进程内缓存 |
| 帧时间（30 帧 / 60 帧中位） | 19.0 ms / 18.3 ms ⇒ **52.6 / 54.6 fps**（软件 GL） |
| 绘制调用 / 三角形 / 纹理 / program | 928 / 93,805 / 148 / 21 |
| 接触测量（服务端） | 首次 0.65 s、缓存后 0.27 s |

**地面接触测量**（`/api/ref-stage/contact?t=0.1`，向下射线打到 `stage.glb`，取脚下最高面）：

| 高度来源 | 有地形在下面 | \|底面 − 地面\| ≤ 10 cm | 中位 | p10 / p90 | 最小 / 最大 |
|---|---|---|---|---|---|
| **原作实例 y**（视图默认） | 510/511 | **272 (53.3%)** | **−0.0001 m** | −0.449 / +0.862 | −2.70 / +18.38 |
| placement 文件 y（压平到 0） | 510/511 | **7 (1.4%)** | +0.775 m | −1.238 / +1.110 | −2.58 / +1.38 |

读法：

- **文件高度是系统性错**：中位 +0.775 m（道具浮空），因为 placement 把每件道具的 AABB
  底面都放在 y=0，而原作地面在 −1.0 m 上下并且**有台阶**（下面的 p10/p90 说明不是常数
  偏移，任何整体平移都修不好）。
- **实例 y 是系统性对的**：中位 ≈ 0（−0.1 mm）。剩下 47% 的散布来自三件我们可以解释
  的事：叠在别的道具上的（挂在父件上）、离开地面的（云、招牌、树冠）、以及 AABB 是
  数据里的"声明尺寸"而不是网格真实包围盒——不是地形错位。
- 511 件里 1 件没有地形（`scattered_clouds_002`，在 x=−14.9 / z=+29.7，舞台网格覆盖范围之外）。

## 7. 已知限制

1. **没有贴图**：提取器不写 PS2 纹理（232 个材质段全白），所以地形是一块哑光石板色 +
   观看用高度渐变色带。要贴图得先做 PS2 纹理解码，不在本轮。
2. **舞台只覆盖核心区**：`stage.glb` 的城镇网格约 x∈[−16, 49]、z∈[−58, 29]，碰撞网格
   更小（x∈[−11, 8]、z∈[−8, 7]）；道具云是 60×80 m，所以外围道具脚下没有舞台（1 件没有
   地面）也没有 zone（77 件 zone = null）。
3. **89 件位置对不上**：422/511 与实例位置 ≤1 cm；其余最大差 7.2 m（5 朵云）、2.5 m
   （4 棵向日葵）、0.3–0.9 m（cubbies / bookstand / 螃蟹若干）。这些是上一轮转换器从
   数据挖掘表**重排过的成排道具**，不是坐标系问题。视图按文件位置画，只借实例的 y/旋转/
   缩放。
4. **5 条父指针换向**（bookstand 一对），见 §4。
5. **6 件缩放的语义未验证**：`0.0741×0.0291×0.0197` 那件缩完是 1.7×0.3×0.3 cm 的薄片，
   按"缩放"理解很可疑；视图照原样应用并在面板里显示，方便主人判断。
6. **zone 有两个概念**：`zones.json` 的 7 个 zone 多边形（多为平板）是区域体；面板里
   「碰撞 zone」用的是道具脚下那块碰撞三角形的 zone id（0/1/3/5/8/24 都出现过），它**不是**
   zones.json 的下标 0..6。zones.json 的包围盒包含判定只命中 6 件，所以没用它。
7. **天球不画**：见 §5；远景墙（`area2_sector0_model0`，106 m）照画。
8. `stage.glb` 直读时绕序与法线相反（§2）——提取器侧值得修一条"reroll 空间要翻绕序"，
   但那不在本仓库。

## 8. 测试与复现

- `Docs/tests/leveleditor/test_ref_stage.py`（28 个用例）：目录解析/仓库内拒绝/旧目录
  备选、帧检测（镜像假设不能赢）、实例配对与哑锚点、轴角→四元数（负角）、碰撞组的局部
  索引 → 世界三角形、AABB 反射、GLB 转换（z 取反、索引不翻、min/max 重算）、
  `prepare` 全量、`contact_report`（抬 1 m 就浮空）、HTTP 契约（401/403、404、
  `X-Unlicensed-Reference`、普通关卡拿不到、没有提取结果仍能打开）。
- 全量：`python3 -m unittest discover -s Docs/tests`、`python3 Docs/tools/validate_plan.py`。
- 手动复现（起自己的服务，别碰主人 8777/8778）：

  前端的 JS/CSS 是每次请求现读磁盘，刷新页面即可；**Python 侧要重启进程**才会出现
  `ref_state().stage` 与 `/api/ref-stage/*`（主人机器上 8778 那个已经跑了几小时的
  `_ref_cancer` 进程就是旧代码，重启后才有原地形）。

  ```bash
  python3 Tools/leveleditor/server.py --level _ref_cancer --port 8792 --host 127.0.0.1 \
    --no-lock --state-dir /tmp/refviewer/state --token <token>
  curl -s "http://127.0.0.1:8792/api/ref-stage/contact?t=0.1" -H "X-Editor-Token: <token>"
  curl -s "http://127.0.0.1:8792/api/ref-stage/transforms.json" -H "X-Editor-Token: <token>"
  ```

  浏览器打开 `http://127.0.0.1:8792/?token=<token>`，用顶部「地形 / 线框 / 碰撞 / 旧平面 /
  原作高度」开关；`window.__leveleditor.refStage()` 与
  `window.__leveleditor.terrainContact(0.1)` 从控制台取同样的数字。
- 截图（headless Chromium + SwiftShader，`Page.captureScreenshot`）放在
  `~/KatamariRef/viewer_evidence/`：`ref_cancer_stage_overview.png`（全景）、
  `ref_cancer_stage_town.png`（街区 3/4）、`ref_cancer_stage_wireframe.png`、
  `ref_cancer_stage_collision.png`、`ref_cancer_collision_only.png`（地形关掉只看碰撞壳）、
  `ref_cancer_stage_parent_lines.png`、`ref_cancer_stage_legacy_planes.png`、
  `ref_cancer_props_row.png`（灰渣块菜畦 + 番茄 + 螃蟹，能看到块体方向各不相同）、
  `ref_cancer_props_row_fileheight.png`（同一个机位，原作高度关掉道具整体浮起）、
  `ref_cancer_props_row_top.png`（俯视）、`ref_cancer_inspector_rotation.png` /
  `ref_cancer_inspector_parent.png`（选中面板读数）。
