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

**带贴图的那份在 `../out/stage.glb`**（`terrain.visual` 的来源，§10）；这里表里的 `kse_out/stage.glb` 是同一几何的无贴图精简版，
旧视图只用它。

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

1. **旧视图（v1 参考路径）没有贴图**：它读的是 `kse_out/stage.glb`，那份是**剥掉贴图的精简版**
   （1.2 MB，232 个材质全白）。贴图不是提取器没写，而是在同目录系的 `noclip_export/out/stage.glb`
   里（119 MB：158 张 PNG，232 个材质逐个带 `baseColorTexture`）——旧版本文档把它说成「提取器不写
   PS2 纹理」是错的。v2 的视图改读 `out/stage.glb`（§10），贴图随 `terrain.visual` 一起渲染。
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

## 8. v2 数据（`placement.v2.json` + `.katerrain`）

参考关卡现在还有一份 **`katamari.placement/2`** 文档，按
[LEVEL_FORMAT_V2](../Levels/LEVEL_FORMAT_V2.md) 的五层写的，由
`Tools/levelbuild/ref_v2_from_extract.py` 从提取结果**确定性**生成（同输入两次运行字节相同）。
它和 §1-§7 的视图共用 `ref_stage`（帧/四元数/实例配对/碰撞展开），不另写一套坐标换算。

```bash
python3 Tools/levelbuild/ref_v2_from_extract.py --level _ref_cancer --write
python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/_ref_cancer/placement.v2.json
```

产物（都在 git-ignored 的 `_ref_cancer/` 里）：

| 文件 | 大小 | sha256 |
|---|---|---|
| `placement.v2.json` | 551,201 B | — |
| `terrain/cancer.katerrain` | 113,508 B | `6a2a1264cad8a776f1681050f0afccc998b5317f1ed74141dbd1702feab6e5ff` |

转换器只读 `~/KatamariRef`（`guard_read` 拒绝仓库内路径）、只写 `_ref_*` 关卡目录
（`guard_write` 同时拒绝 `..` 与非 `_ref_` 目录），测试里用 patch 过的根验这两条。

### 8.1 文件里是什么

| 层 | 内容 |
|---|---|
| `meta` | 起始 **0.222 m**、目标 **0.80 m**、`maxDiameterRef:"global"`、时限 **420 s**（原作 GAME_TIME 7 分钟）、出生点 `authored`（在本关地形/道具/结构上量出的可玩点：平坦 RIDE 面、起始吸不动的道具当障碍、沿路径 3 m 内可吸件数最多；量法/数值见生成物的 `arena.notes` 与 ORIGINAL_TERRAIN_AND_LAYOUT §2.4）、`groundY −3.0`（地形最低 −1.59 m 再低 1.4 m）、`bounds` 覆盖 511 件道具、`caps` 511/4096/8192/256/8 |
| `terrain` | 提取器的**碰撞网格**（不是视觉 `stage.glb`）：**7,154 顶点 / 3,455 三角形**（原 3,456，1 个零面积三角形按 `[terrain_mesh]` 删掉），世界米、绕序翻一次；**7 个 chunk**，每个 ≤512 三角形并且就是一条 broad-phase AABB（`triangleRange` 首尾相接、无洞）；`grid` 1 m / 19×15 |
| `triangles` | `count 3455` + `zoneCounts`（0:2727, 1:23, 3:244, 5:21, 8:73, 24:367）；逐三角形 zone 取三顶点多数票（平票取小），逐三角形 `classes` 在侧车里（ride 1698 / climb 503 / wall 1254） |
| `structures.solids` | **35 条**，接触形状是 **def 的拟合代理**（`shape:"proxy"` + `collider` + `proxySource:"def"`；`size` 只作支撑面），规则见 §8.2 |
| `activityArea` | 一条闭合矩形墙（碰撞足迹并集 + 1 m，x −11.81…8.57 / z −7.54…8.99，高 3.0 m、厚 0.2 m、不可见）+ kill plane `y −4.0` |
| `props` | **511 件全部保留**：原作位置/旋转/缩放、`parent` 83 条、`volumeK`（原作 `1/bboxOverSolidVolume`，0.1…14.7）、`volumePenalty 0.8`、`growthMm3` 与 `absorbDiameterReqMm` 派生、`absorbable` 426 件（按 1.20 m 门；唯一非均匀缩放的 `sparrow_001` 按 D-5 标 false）、`zone` 按脚下碰撞三角 |
| `defs` | 145 条，`collider` **144 条**（`sparrow` 只有那件非均匀缩放的实例，按 D-5「不写 collider」；代理拟合见 §8.3） |
| `motions`/`paths`/`cameras` | 空：提取器只给 `moveType`，没有程序表，按 C11「不伪造运动」只把 `moveType` 记进 `notes` |

### 8.2 三条派生规则（写进文件、也写进这里，便于复核）

1. **接触地形 = 碰撞网格**，不是 `stage.glb`：碰撞网格才是原作的可走面/挡球面（含 15 m 不可见围墙，
   绝不能当视觉画）；视觉网格另走 `terrain.visual`（§10，[LEVEL_FORMAT_V2 §3.8](../Levels/LEVEL_FORMAT_V2.md)）。世界化 `-0.01 × ps2` 后绕序必须翻一次（det −1）；翻完地面法线朝上
   1,704 : 319，`classes` 才有意义。
2. **structures = 这一关永远吸不动的大件**：`gate > 1200 mm`（原作 `MAX_DIAMETER`，本关
   设计上限）**且** 其 AABB ≥50% 落在提取器 68 个碰撞 AABB 之一的内部。84 件永远吸不动，
   其中 **35 件**在碰撞覆盖的核心区内 → 给解析体（`role:box`，可支撑）；另外 49 件（街区外
   的民宿、餐馆、樱花树）是背景，活动墙把球挡在外面，不需要固体。**碰撞 AABB 本身不是
   固体**（它们是舞台网格的 broad phase，和地形三角形重复），只作为「这里确实是实心」的
   证据。判据、阈值、两侧件数都写在转换器报告里。
   **接触形状 = def 的拟合代理**（2026-11-16 改）：18 条 `convex` + 17 条 `box`（盒代理也可能比
   声明 AABB 小，因为拟合时统一缩放塞进 AABB），`size` 只留给支撑面。原来用 AABB 时"螃蟹贴树根站着"会被判成嵌进固体
   60 cm——那是我们的代理粗，不是原作的摆放（[OPEN_DECISIONS](../../OPEN_DECISIONS.md) 的
   「碰撞体必须跟视觉形状」同一条口径）。
3. **activityArea 是我们作者化的**：原作没有边界字段（§5.1），但它有碰撞几何，碰撞几何的
   XZ 足迹就是球能去的地方。所以多边形 = 68 个碰撞 AABB 的并集足迹（含地形三角形）外扩
   1 m。kill plane 放在兜底地面 `groundY` 再低 1 m（校验器要求它低于地形与 `groundY`）。

### 8.3 每类道具的 collider 代理

`defs[].collider` 由 `Tools/levelbuild/collider_proxy.py` 的 `fit_proxy` 拟合，输入是
该 def 在 `~/KatamariRef/models/glb-pack/` 的 GLB（145/145 都有）：顶点/三角形从 GLB 读，
按**一个统一缩放**塞进该 def 声明 AABB，`bounds` 交给拟合器保证代理不超过视觉盒。
不做逐轴缩放，因为提取器没记录 GLB 的轴向；结果是保守的（可能比模型略小），
`collider_schema_errors` 逐条过了，145 条都合法。`scalePolicy` 一律 `uniform`。

### 8.4 校验结果

`levelbuild.py check` 现在**按 schema 分流**：v1 文档走 `placement_core.validate`，v2 文档走
`placement_v2.validate`（[LEVEL_FORMAT_V2 §9](../Levels/LEVEL_FORMAT_V2.md) 的 tag 集，每条带
`路径 + 期望 + 实际`）。两个校验器**共用底层规则**（吸收门/体积、`world_aabb`、`overlap_scan`、
`structure_penetrations`、collider schema），只有层的语义各写一份；v2 不再翻译成 v1 视图
（`to_v1_view()` 只留作对比工具）。

参考关卡的实际输出（原文）：

```
schema=katamari.placement/2  level=_ref_cancer  scene=StoryMode
ball 0.222 -> 0.8 m  (global)  timeLimit 420s  groundY -3.0
props 511 (426 absorbable, 83 with a parent)  defs 145  zones 6
structures 35  terrain 3455 triangles  chunks 7  motions 0  paths 0
PASS Docs/Design/Levels/_ref_cancer/placement.v2.json (0 error(s), 1 warning(s))
```

剩下的一条 warning 与三条 note，逐项如下（**没有为了变绿放宽任何阈值**）：

| 类别 | 内容 | 为什么不是错误 |
|---|---|---|
| warning | `[prop_overlap]` 153 对（= 声明的 `expected.overlapBudget`，示例 `bamboo_blind_001+laundry_hanger_001`…） | 密集的真实小镇必然有；该字段就是"本关最多允许多少对"的预算，超出才是错误 |
| note | `sparrow_001`：非均匀缩放且 def 无 collider（D-5 的既定处理） | D-5 明写"照写 scale、标 absorbable:false、不写 collider、按无碰撞处理" |
| note | `sparrow_001`：`absorbable=false` 但门说 True | v2 里 `absorbable` 是**作者声明**，不是门的函数；校验器只记录分歧 |
| note | 24 件道具沉进 scenery 固体的**拟合代理**（最深 0.614 m；同一判据按**原始 AABB** 是 47 件 / 1.094 m） | §9.2 只把**道具×道具**互穿定为错误，没有道具×结构的 tag；21 件差额是 AABB 代理过粗造成的假阳性，见 [OPEN_DECISIONS](../../OPEN_DECISIONS.md) |

顺带（同一批数据的 v1 视图对照，只作历史对比）：v1 `placement.model.json` **544 error / 146
warning**，其中 511 条是派生列过期、367 对道具互穿（25 行 + "…341 more"）；v1 视图跑 v2 数据是
**818 error**，其中 **790 条是 v1 的"以 y=0 平地量浮空/入地"**（参考关卡的地面在 −1.0 m，
这条规则对地形关卡不适用——v2 用"球底以下最高的地形面"量：510/511 有地面，272 件（53.3 %）
在 10 cm 内，中位 −0.0001 m）。**别再拿 818 当 v2 的成绩单**：那是 v1 规则的误报集合。

### 8.5 还缺什么（Unity 导入前）

1. **v2 校验器已落地**（`Tools/levelbuild/placement_v2.py`，§9.2 的 tag 集；`levelbuild.py check`
   直接按 schema 分流）。仍未实现的是**运行时**那一侧：`TerrainFieldHost` 的数组、接触求解、
   以及 §9.2 里依赖运行时语义的判定（例如"墙顶不可站"的实测）。
2. **`PlacementImporter` 的 v2 分支**（`TerrainFieldHost`/`ActivityAreaHost`/`LevelMetaHost`/
   `MotionHost`）不在本轮；地形网格资产路径按 §11.3 应落在
   `Assets/_RefUnlicensed/Levels/_ref_cancer/Terrain.asset`。
3. **`motions[]`/`paths[]` 空**：本轮的提取结果还没有运动/路径表（下一步由
   `motion_table.json` / `path_table.json` 补；C11 要求不按名字猜 class），所以 511 件现在都是
   `static`，`moveType` 只在 `notes` 里存证。`caps.animators = 153` 已按原作动画挂载量填好
   （D-11：照原作挂满、不做 VAT），来源写在 `meta.arena.notes`。
4. **贴图/材质**：已由 `terrain.visual` 承担（§10）；`terrain.materialSegments` 只描述接触网格，不渲染。
5. **`props[].kind` 全是 `reference`**：提取器没有「动物/餐具/建筑」这类内容分类（v1 文件也没有），
   要等 collection 表接入。
6. 6 件缩放按 D-5（2026-11-16 三层规则）处理：5 件均匀照写（含 1 件挂接件），1 件非均匀
   `sparrow_001` 标 `absorbable:false` + 无 collider；非均匀的**语义**仍未验证。

## 9. 测试与复现

- `Docs/tests/levelbuild/test_ref_v2_from_extract.py`（18 个用例）：读写路径守卫、碰撞列表→
  CCW 三角形（含 1 个零面积被删）、chunk 铺满且 AABB 包住自己的三角形、侧车往返、
  `volumeK`/覆盖度/背景件规则、活动多边形、规范文字（−0.0 清掉）、v1 视图映射，以及
  （装了提取结果时）两次构建字节相同 + `structural_errors()` 为空 + 侧车 sha256 一致。
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
    --no-lock --state-dir .work/refviewer/state --token <token>
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

## 10. 视觉舞台：`out/stage.glb` 是什么、怎么进 Unity 与编辑器（2026-11-17 研究）

> 起因：Unity 里「超大红墙」是把**碰撞网格**当视觉渲染（碰撞含一圈 15 m 高的挡球围墙），编辑器 v2 路径又只画碰撞网格、
> 院子中间看不到地面。原作 noclip 里看到的完整城镇来自**图形扇区**，那是另一份数据。本节是研究结论与格式依据；
> 字段与校验在 [LEVEL_FORMAT_V2 §3.8/§8.4](../Levels/LEVEL_FORMAT_V2.md)，转换器是 `Tools/levelbuild/ref_stage_visual.py`。
> 数据全是 UNLICENSED 本地参考：PNG 与侧车只写进 git 忽略的 `Docs/Design/Levels/_ref_cancer/terrain/`，
> 导入的 Unity 资产只在 `Assets/_RefUnlicensed/`。

### 10.1 两份 `stage.glb`

| | `noclip_export/out/stage.glb` | `noclip_export/kse_out/stage.glb` |
|---|---|---|
| 内容 | 7 个节点，**158 张 PNG、232 个材质全带贴图** | 同样的 7 个节点，无贴图 |
| 体积 | 119 MB（导出器给**每个图元各写一份**顶点/法线/UV 访问器：928 个访问器，实际 29,846 顶点） | 1.2 MB |
| 位置 | noclip 帧、**厘米**（导出器对 PS2 数据做了 `rotateX(π)`） | `(−0.01x, 0.01y, 0.01z)` 的米制，即 `out` 的 x 镜像 |
| 法线 | PS2 帧（`out` 里朝上的地面法线是 `(0,−1,0)`） | `−n_out` |
| 索引/UV | 与另一份**逐位相同**（7 个网格：索引相同、UV 最大差 0.0） | 同左 |

转换器读 `out/`，自己做 `kse` 那一步映射；两份的位置换算一致（比值 ±0.01）。

### 10.2 七个节点（我们的帧，米；三角形数是翻绕序、去掉 47 个退化三角形之后的）

| 部件 id（= kse 名） | 导出器名 | 三角形 / 顶点 / 材质部件 | 包围盒 | 朝上/朝下面积 m² | 说明与去留 |
|---|---|---|---|---|---|
| `area2_sector0_model0` | `a2_s1_m0` | 407 / 816 / 22 | x 0…105.9 · y −2.4…8.6 · z −45.8…58.4 | 6402 / 69 | 院子**东侧**的大片地面（草地、道路；UV 重复到 31 次）。进 Unity |
| `area2_sector0_model1` | `a2_s1_m1` | 767 / 1499 / 17 | x −5.4…15.9 · y −1.6…2.6 · z −6.5…11.9 | 135 / 4 | 院子东缘的挡土墙/房基类小件，464 个三角形落在活动区内。进 Unity |
| `area2_sector0_model2` | `a2_s1_m2` | 1473 / 3021 / 12 | x −48.7…15.9 · y −2.4…3.7 · z −28.6…58.4 | 4503 / 2 | 院子**西、北侧**的街区地面与墙（院内最高顶点 3.63 m 在这块）。进 Unity |
| `area2_sector0_model3` | `a2_s1_m3` | 448 / 926 / 19 | x −14.6…0 · y −1.6…2.6 · z −6.5…11.9 | 84 / 10 | 院子西缘的挡土墙，426 个三角形在活动区内。进 Unity |
| `area2_sector0_model4` | `a2_s1_m4` | 11,522 / 23,394 / 154 | x −22.1…8.4 · y −1.6…3.6 · z −13.8…10.7 | 939 / 318 | **主院**：地面、台阶、房基、栏杆、屋顶（占 78% 三角形、127 张贴图）。进 Unity |
| `area2_sector1_model0` | `a2_s2_m0` | 56 / 70 / 1 | 半径 121 m 的球壳，y −35.5…85.8 | 0 / 6233 | **天空壳**：56 个三角形全部朝内（从里面看正面）。进 Unity，`role:"sky"`，不投影、不写深度、不参与取景 |
| `area2_sector2_model0` | `a2_s3_m0` | 60 / 120 / 7 | x −3.4…6.1 · y 0…2.8 · z −3.8…6.7 | 2 / 2 | 院内的小件，5 个材质带半透明贴图（**窗/玻璃类**，推测——数据里没有名字，只有形状与 alpha）。进 Unity |

`sector` 是模型包里图形扇区的序号（导出器的 `s1/s2/s3` = 0/1/2）。Cancer 有 3 个有效图形扇区；
noclip 的格式有 4 个图形槽，其余为空。**没有逐节点的语义标签**：表里的说明来自包围盒与几何，不是原作命名。
全部进 Unity（14,733 三角形、205 个子网格，量级很小）；只有 `sky` 单独标 role。

### 10.3 材质与贴图

- 232 个材质部件 → **158 张贴图**；40 张贴图被多个部件共用（最多 7 个）。同一节点内按贴图合并后 205 个子网格。
  所有部件 `baseColorFactor = [1,1,1,1]`（漫反射色全白，不写顶点色）。
- PNG 全是 8 位 RGBA、无交错、过滤类型 0，共 180.5 KB；尺寸 64² ×73、128² ×41、32² ×18，其余 26 张是 16–256 的非方图。
  **alpha 已按 128→255 归一**。按 alpha 分：**141 张不透明、11 张 0/255 镂空、6 张有中间值**（5 张在窗类节点与主院里）。
- UV：`[-1.7, 33]` 的范围，主院到 `(460, 952)`——靠重复铺地面，所以 wrap = repeat 是必须的。
- **GS 状态**（`glb` 里没有，用仓库外的探针 `noclip_export/probe_stage_materials.ts` 从 noclip 解析结果读出，
  输出 `noclip_export/out/stage_materials.json`；**232 个部件逐个核对，完全一致**）：
  - 混合：`ALPHA_1 = 0x44`（`src_alpha, 1−src_alpha`；noclip 里另一个可能值 `0x48` 加算，本关没有）；
  - alpha 测试：`ATE=1, ATST=NOTEQUAL, AREF=0, AFAIL=KEEP`，即**丢弃 alpha == 0**；
  - 过滤：`TEX1` 的 **MMAG = 最近邻、MMIN = 线性、无 mip**（`LCM=0, MXL=0`）；wrap：`CLAMP` 的 S/T 都是 **repeat**；
  - 光照：所有部件 `lit`（逐顶点：法线 × 关卡光照文件）；渲染器对每个部件 **`Cull Back`**。
  于是混合模式没有逐材质信息可存，只剩 PNG 的 alpha 决定 `opaque/cutout/alpha`。
- **导出器写在 glb 里的这几项不是原作数据**：`doubleSided: true`（全部）和采样器（最近邻 + repeat，硬编码）。我们不继承
  `doubleSided`（原作是背面剔除）。
- 过滤的取舍：Unity 一张贴图只有一个 filter，原作是放大最近邻 / 缩小线性。选放大侧 `point`——属**判断**，Unity 里看
  一眼；格式里 `filter` 是显式字段，改成 `bilinear` 不影响别的。

### 10.4 绕序与法线

- 位置的映射 `(x,y,z) → (−0.01x, 0.01y, 0.01z)` 是 x 镜像（det −1），所以**绕序必须翻一次**。实测（翻之后）：
  顶点法线与几何法线一致的三角形 **99.5–100%**（直读 kse 时是 0.4%，即里外翻）；主院朝上面积 939 m²，天空壳 56/56
  个三角形朝内；节点 0、2 的地面朝上/朝下面积比 6402:69、4503:2。
- 顶点法线要取 `−n`（它们在 PS2 帧里写；kse 就是这么做的）。
- 我们的约定：Unity 的正面 = 数值叉积 `(b−a)×(c−a)` 的方向（地面叉积 +Y）；文件里的三角形已按它写，**导入不再翻**。
  three.js 帧是 z 取反，是又一次镜像，编辑器读取时再互换一次（`v2_document.build_visual_glb`）。
- 编辑器此前画碰撞网格时就是**只取反 z、不换绕序**，FrontSide 把所有朝上的面剔掉了——这就是「院子中间没有地面、只剩一圈绿面」
  的直接原因（§10.7）。

### 10.5 与碰撞网格的对位

- 同一帧，不需要额外对位。碰撞网格 3,455 个三角形的**重心**到视觉网格的最近距离：**93.5% ≤ 5 cm，97.4% ≤ 20 cm，
  中位 0.0007 mm**；1,358 个朝上（ride）碰撞面里 1,330 个到某个视觉面（xz 投影相交）的垂直距离 ≤ 5 cm。
- 差别在于**视觉没有的那部分**：红墙报告里的 70 个顶点高于 3.65 m 的碰撞三角形全部属于 15 m 围墙，视觉网格院内最高
  3.58 m（主院）/3.63 m（`model2`）——院内渲染最高 y ≈ 3.7 是验收口径。
- 视觉与碰撞各自独立（原作就是两套几何）：≤5% 面的差异不是回归。

### 10.6 数字与产物

转换器输出（`python3 Tools/levelbuild/ref_stage_visual.py`，单独跑只写侧车和 PNG；`ref_v2_from_extract.py --write`
再把 `terrain.visual` 块写进 `placement.v2.json`）：7 个部件 / 29,846 顶点 / 14,733 三角形（47 个退化三角形丢弃）/
205 个子网格 / 158 个材质（141 opaque、11 cutout、6 alpha）；`.katvisual` 约 1.0 MB，PNG 180.5 KB，编辑器的 GLB 1.3 MB。
同一输入两次运行逐字节相同；`.katerrain` 接触侧车 sha256 仍是 `3fb17457…`（不变）。

### 10.7 为什么编辑器和 Unity 里院子中间「没有地面」（owner 的问题，逐项答）

结论：**不是解析问题；是格式缺东西 + 编辑器渲染端的绕序错；Unity 侧按资产数据看没有绕序/转换错，只是画了碰撞网格（未在 Unity 里亲眼复现，需操作员一轮确认）。**

1. **解析没问题**：`out/stage.glb` 完整含 14,780 个三角形与全部贴图；noclip 网页能看到整座城镇，因为它画的是图形扇区。
2. **格式缺东西**：v2 之前只有 `terrain`（碰撞网格），没有「画面」那一层，所以任何渲染端只能画碰撞网格。碰撞网格只覆盖
   18×14 m 的核心 + 一圈 15 m 的不可见墙，核心外没有任何地面。——这是 `terrain.visual` 要补的。
3. **编辑器**：①v2 路径把旧的 `stage.glb` 叠加层停用了，只画碰撞；②画碰撞时 z 取反却没换绕序，朝上的面被 FrontSide 剔除，
   只剩朝下的那些面（碰撞里的顶面等）可见——「中间一圈绿面、中间看不到地面」（`shot.py` 复现过：改前只有零星绿面）。现在两处都改了（视觉 GLB 为地形，碰撞线框为可开关叠加层，
   绕序随镜像互换）。
4. **Unity**：R21 手工网格的绕序**没有**翻：它的 3,455 个三角形与 `.katerrain` 逐个同向（3,453 同向、2 个反向，按顶点集合匹配），
   所以不是「里外翻」。Ride 子网格里的 301 个朝下的面（325 m²）是原作碰撞里的顶面/天花（侧车 zone 1 的 23 张加上其他），从上往下
   看被背面剔除——它们不是「洞」，只是看不见；可见的地面只有核心区那 254 m² 的平面，外面整座城镇都没有渲染。红墙是第 3 个
   子网格（碰撞围墙）。操作员复现时若仍看到「院子中间空」，请先看 `TerrainCollision` 是否还带着渲染器。

### 10.8 没验证的

- Unity 里的材质外观（Legacy Diffuse 系 vs 原作的逐顶点光照）、`filter = point` 是否好看、半透明窗的排序：要一轮 Unity 截图。
- 节点的语义名（§10.2 的「院子东侧大片地面」等）是按几何写的，不是原作命名。
- 探针和 `stage_materials.json` 在仓库外，只在本机复现；转换器**不依赖**它，只依赖 PNG 的 alpha。
