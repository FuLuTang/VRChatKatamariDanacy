# 地图作者规则摘要：仓库里已经定下来的那些数

**用途**：把散在 `Docs/03_MAPS_ASSETS.md`、`Docs/Design/Tools/LEVEL_AUTHORING.md`、四张地图
README、`Docs/Research/ContentVolume/`、`Tools/levelbuild/placement_core.py` 里的**作者规则**
收成一份，每条给出处。写实现的人不用再逐个文件翻。

**边界**：这些数是**本项目的设计参数**（`【已定】` 或 `【DRAFT】`），不是原作实测值。唯一
带外部证据的是「吸收 = 10 倍体积」（数据挖掘，见 `ABSORB_SIZE_RATIO.md`）。不填估计值冒充数据。

证据级沿用仓库口径：`[官方]` / `[数据挖掘]` / `[社区]` / `[推断]` / `[本项目设计]`。

---

## 1. 尺寸：唯一的门槛是体积

| # | 规则 | 出处 |
|---|---|---|
| R1 | 吸收条件只有一个：`a·b·c ≤ (π/6)·D³ / 10`。等价写法 `D_req = 2.673·(a·b·c)^(1/3)`，其中 `2.673 = (60/π)^(1/3)`，`60 = 6×10` 是 `π/6` 与 `/10` 的合并常数 | `PickupAbsorbMath.cs:350`；`placement_core.py:59`（`VOLUME_GATE_FACTOR = 60.0`）；`KatamariBallMotor.cs:218`（`absorbVolumeRatio = 10f`） |
| R2 | 「10 倍」是**体积**比，不是直径比。`D_required = 2.154 × d_eq`（球的体积是物件的 10 倍） | `ABSORB_SIZE_RATIO.md`；证据 `[数据挖掘]` |
| R3 | 门槛之外还有一个绝对护栏 `maxItemVolumeWorldM3 = 512`，只有 `D ≥ 21.4 m` 才会先于相对门生效——**它不是玩法旋钮**，别去调它 | `KatamariBallMotor.cs`；`PROP_SET_PLAN.md` §2.8.1 |
| R4 | 球径上限是**关卡属性**（`maxCoreRadiusWorldM` 每个 motor 实例一份），不是全局常量 | `KatamariBallMotor.cs:195`；`PROP_SET_PLAN.md` §2.8.1 |
| R5 | 一件道具**能不能被吸**只看它的 AABB 体积；长条物用包围盒算会高估，这是已知偏差，不要用「最长边」去替代 | `LEVEL_AUTHORING.md` §4.2；`03_MAPS_ASSETS.md` §8（「AABB 体积不能顶替真实成长」） |

**最容易犯的错**：把 `D_req` 当成「球直径 ≥ 最长边」。`picnic_bench`（1.2×0.45×0.4）的
`D_req = 1.604`，不是 1.2；1.25 m 的球吃不到长凳，而任务文案写着「1.25 m 就能一口吞下」。

---

## 2. 尺寸带与档位

| # | 规则 | 出处 |
|---|---|---|
| R6 | 每关只跨 **3–4 档**，不要一关跑满 8 档。3 档 ≈ 145 实例 / 44 defs；4 档 ≈ 195 / 59 | `ContentVolume/README.md` §4.3 |
| R7 | 档位边界（全局，不按图改）：t0 0.30–0.55 / t1 0.55–0.95 / t2 0.95–1.45 / t3 1.45–2.20 m | `PicnicLawn/README.md` §2；`ToyRoom/README.md` §2 |
| R8 | 校验器里的档位常数是另一套：`(0.30, 0.50, 0.80, 1.10, 1.50, 2.20)`——**两套数不要混用**，见 C9 | `placement_core.py:66`（`TIER_DIAMETERS`） |
| R9 | 单关的物件尺寸应**近似对数均匀**，相邻档的几何步长约 1.2×；原作单关的尺寸跨度是几十倍（MAS8 一关 14→1003 cm，71.7×） | `ContentVolume/README.md` §0、§3.1 |
| R10 | 台阶/地形跨越：`rise ≤ 0.25·D` 走上去（当台阶），否则阻挡。`STEP_RATIO = 0.25`；单次抬升还有 0.6 m 护栏，正确配置下永远碰不到 | `KatamariBallMotor.cs:110`（`stepHeightOverBallDiameter`）、`:112`（`maxStepUpLiftWorldM`）；`placement_core.py:63` |
| R11 | 起手 3 档必须真的存在：真实尺寸下 110 件里 **93.6% 都 ≤ 起始球径 0.2997 m**，等于开局就能全吃。要让前 3 档存在，只有两条路——把球做小（改 `c`）或补大件 | `PROP_SET_PLAN.md` §2.2、§2.8.6 |
| R12 | 出生点净空：球在出生点附近不能一睁眼就吃到「大件」，`spawnClearanceM` 是关卡字段 | `placement.json` 的 `level.spawnClearanceM`；`REGION_MAP_PLAN.md` §7 |

---

## 3. 每档内容量与密度

| # | 规则 | 出处 |
|---|---|---|
| R13 | 每档 **40–55 件**可吸物（40 是地板、55 是舒适上限）；每档 12–15 个 def；类型:实例 ≈ 1:3.2 | `ContentVolume/README.md` §4.1、§4.2 |
| R14 | 比例约束（每档都要满足）：**80% 实例 ≤ 0.35·D**，中位 0.25·D，顶端 10% 到 0.55·D；四类轮廓（长条/扁平/圆形/块状）**各 ≥15%** | `ContentVolume/README.md` §4.1；`DungBeetleMeadow/README.md` §5；`MessyRoom/README.md` §5 |
| R15 | 面密度随球径下降：同一批 40–55 件摊到「直径 ≈ 20·D」的活动区，低档约 117 件/100 m²，最高档约 1.9 件/100 m²（约 60×） | `ContentVolume/README.md` §3.2（`[推断]`，活动区 20·D 是量级假设） |
| R16 | 分区密度的现成表（"野餐一角"）：口袋 30 件 @1.20/m²、毯子 70 @0.73、草坪 52 @0.31、边缘 16 @0.07、营地 4 | `REGION_MAP_PLAN.md` §2.2 |
| R17 | 一关的件数上限来自**体积复利**：球径 0.35→2.6 m 是 410× 体积，按每件吃 0.35·D 算需要约 143 件；投放 40–55 件/档 ≈ 实际只吃掉一半 | `ContentVolume/README.md` §3.2、§4.3 |
| R18 | 密度够不够的判据不是"每平方米几件"，而是**滚一圈能看到几件**：先保证一条主环线上每 2–3 m 有东西 | `03_MAPS_ASSETS.md` §5；`MessyRoom/README.md` §3.3（主环线 28 m） |
| R19 | 散点堆量会"结块"：纯随机撒点会出现空区与叠穿，必须用拒绝采样 + 最小间距 | `PicnicLawn/README.md` §3.3（记了一个真实的 packing bug） |

---

## 4. 支撑、层叠与容器

| # | 规则 | 出处 |
|---|---|---|
| R20 | 一件可吸物要么「贴地」，要么「踩在结构上」(`support`)，要么「叠在另一件上」(`stackedOn`)。三者互斥 | `placement_core.py:1268`（`_support_surface`）；`LEVEL_AUTHORING.md` §4.2 |
| R21 | `stackedOn` 是**单支撑者**：校验器只认一个。多支撑（桌面搭两条腿）今天**表达不出来** | `placement_core.py:1268`；`LEVEL_AUTHORING.md` §4.2 |
| R22 | 叠放必须顶/底对齐：上下两件的高度差 ≤ **1 cm**（`STACK_TOLERANCE_M`）；陷进 ≤1 cm 算接触，浮起 >2 cm 算悬空 | `placement_core.py:71-74` |
| R23 | 重叠：除声明过的叠放关系外，两件道具的水平重叠不得超过 **5%**；道具穿结构 ≤2 cm | `placement_core.py:76-79` |
| R24 | 容器/支架是**两段式**：球小的时候它是地形（挡路/可绕/当掩体），球大了整件可吞，且**上面/里面先有小件**（先清小件再吃支架） | `DungBeetleMeadow/README.md` §3.4；`MessyRoom/README.md` §3.4 |
| R25 | 一个结构的身份有三种（地形 / 可吞 / 可吞+其上小件），由校验器预测、运行期自行判定；**同一个 box 不能既是斜坡又是墙** | `LEVEL_AUTHORING.md` §10.3、§10.4 |
| R26 | 斜坡的"走面"是本地 +Y 面，下面垫 4 cm 板；坡度上限 45°（`RAMP_MAX_SLOPE_DEG`） | `placement_core.py:83`；`LEVEL_AUTHORING.md` §10.4 |
| R27 | 支撑关系**只是关系，不是坐标**：导入器只用 `position/rotationQuat` 摆东西；关系字段只喂校验器 | `LEVEL_AUTHORING.md` §4.2 |

---

## 5. 尺寸门与不可吞布景

| # | 规则 | 出处 |
|---|---|---|
| R28 | 每张图 8 道门 = 4 道「翻越」+ 4 道「吸走」，阈值成对（阻挡 < `D_block`，开启 ≥ `D_open`）。野餐：4 张倒塌长凳 1.05→1.25 m、4 个花圃 0.95→1.10 m；玩具房：4 块拼图地垫 0.95→1.10 m、4 只巨型玩偶 1.30→1.45 m | `PicnicLawn/README.md` §9；`ToyRoom/README.md` §9 |
| R29 | 门的三种物理形态都要能连回门槛：**下方堵住**（球大才能钻）、**上方可越**（球大才能跨）、**可吸走**（吃门）。每条门都要给出「球多大、为什么现在不行」的数值证明 | `PicnicLawn/README.md` §9；`ToyRoom/README.md` §9 |
| R30 | 阻挡与开启之间留 0.15–0.20 m 的**抖动带**（1.05–1.25 m 会抖动/位移但不让路），给「就差一点」的反馈；每条门都要写清绕行代价（野餐 +8 m / +5.5 m，玩具房 +6.0 m / +7.5 m），不许软锁 | `PicnicLawn/README.md` §9；`ToyRoom/README.md` §9 |
| R31 | 每道门都要有「第二个空间」；只开一条必须开的门 = 软锁 | `03_MAPS_ASSETS.md` §5（「增长解锁选择，不解锁死路」） |
| R32 | 门用真物件，不用空气墙；不可吞布景（墙、地板、窗、门框、暖气管道、镜子、吊灯）要留够 | `MessyRoom/README.md` §8 |
| R33 | 可达性必须按**球径包络**算，不是按球心点：主路宽 3.40 m，净空 1.20 m ≈ 0.55×最大球径 | `PicnicLawn/README.md` §2 |
| R34 | 高度差只做「可推上去」的（≤0.45 m），桌面 0.75 m 要球 ≥1.0 m 才能上，用倒下的椅子/纸箱堆当坡 | `MessyRoom/README.md` §3.1 |
| R35 | 0.26 m 的拼图垫能不能上，**未验证**；ToyRoom 故意不做斜坡（与 `03_MAPS_ASSETS.md` §3 的建议相反，是自述偏差） | `ToyRoom/README.md` §8 |

---

## 6. 空间、视线与地标

| # | 规则 | 出处 |
|---|---|---|
| R36 | 12 条共享空间原则（四出生区按初始收益与到二档距离、每区 ≥2 出口、窄道按球径、增长解锁选择、留不可吞地标、支撑关系进图数据、不能跳、磁铁不穿薄墙、播报下一个银行、不许永久单球堵、固定布局+有限随机、低档区可补充） | `03_MAPS_ASSETS.md` §5 |
| R37 | 物件分层表：地形/大地标固定；普通可吸物静态 + 区域查询；可撞倒小件走受控动态池或预计算落点；架/桌面物件走支撑关系表；浮空件标装饰；PvP 掉落、装饰碎屑、NPC 各自一档 | `03_MAPS_ASSETS.md` §6 |
| R38 | 地标要**可读**：高度、轮廓、颜色三重编码；外圈沿路只能放 ≤0.9 m 的东西，否则挡视线 | `PicnicLawn/README.md` §10 |
| R39 | 相机视点随球后退是**原作的做法**，我们的相机高度/遮挡预算要按球径分档写 | `PicnicLawn/README.md` §10 |
| R40 | 「两个空间语法」：野餐是门与环，玩具房是开放地板的取舍；**故意不做窄缝** | `ToyRoom/README.md` §4.1 |
| R41 | 一关只教一件事：野餐教门与环，玩具房教开放地板的取舍，草甸教八档阶梯 | 三张图的 §1 / §4 |
| R42 | 不做脏到恶心（不做过期食物特写、虫害、排泄物）：「乱」是生活痕迹 | `MessyRoom/README.md` §8 |

> **已拍板（用户 2026-11-15）**：移动件（`path` / `orbit`）被球碰到时只有「吃掉 / 挡路」两种结果，
> **不做**原作那种「先撞飞才能吸」的中间态。R37 的「可撞倒小件受控动态池」是**另一条**规则
> （讲静置小件被撞倒，不是移动件），仍未定，别把这两条并成一条。

---

## 7. 作者操作与工具

| # | 规则 | 出处 |
|---|---|---|
| R43 | 四个操作词：**seat**（贴地）、**stack**（叠）、**lean**（斜搭）、**group**（成组）；作者只按这四个词想，不按坐标想 | `LEVEL_AUTHORING.md` §5.3 |
| R44 | 一份数据、两个作者、一个消费者：`placement.json` 是**唯一**真源，Blender 与常驻编辑器服务都读写它，Unity 导入器只消费 | `LEVEL_AUTHORING.md` §3、§16 |
| R45 | 几何规则**只有一份实现**（`placement_core`），CLI 校验、Blender 脚本、单元测试共用；不许写第二份 | `AGENTS.md`「Keep one implementation」；`Tools/levelbuild/README.md` |
| R46 | 数值落盘规范：位置 4 位（`POSITION_DECIMALS`）、四元数 6 位（`QUAT_DECIMALS`）、尺寸 4 位、球径 3 位；按 `itemId` 排序、`-0.0→0.0`、2 空格缩进、UTF-8 无 BOM | `placement_core.py:50-54`；`LEVEL_AUTHORING.md` §4 |
| R47 | 导出器必做自检（§5.5）：单位/轴/法线/负缩放、非单位父级缩放、循环父级、材质数、缺失引用 | `LEVEL_AUTHORING.md` §5.5；`03_MAPS_ASSETS.md` §8 |
| R48 | 一条流水线十步（先 20–40 个基础件 → 校验 → 一实体一预制体 → 不合并所有可吸物 → 渲染/碰撞/代理分层 → 远程 LOD → ItemDefinition + 缩略图 → 编辑器校验 → 摆完再调密度） | `03_MAPS_ASSETS.md` §8 |

---

## 8. 校验器今天拦什么

`placement_core` 的 16 条规则（`LEVEL_AUTHORING.md` §8）里与作者最相关的：

| # | 规则 | 失败长什么样 |
|---|---|---|
| R49 | 贴地/悬空：最低角与支撑面 ≤2 cm 浮起算悬空 | 道具浮在桌面上方 3 cm |
| R50 | 叠放对齐：上下顶底差 ≤1 cm | 箱子叠歪 2 cm |
| R51 | 道具间重叠 ≤5%（声明叠放除外） | 两只碗插在一起 |
| R52 | 穿结构 ≤2 cm | 长凳一半埋进花坛 |
| R53 | 必须能被吃到（`absorbable = D_req ≤ ballDiameterMax`）+ 出生点净空 + 预算（planes 8 / boxes 8 / ramps 4 / propSupports 6 / blocking 6） | 一件 3.6 m 的长凳留在 2.2 m 上限的图里 |

**知道但没做的**：`behaviour*` 一类新字段会被 `PROP_KEYS` 过滤掉（`placement_core.py:109`），
validate 不报错、rewrite 静默丢弃——加字段必须同时改这一处。

---

## 9. 矛盾与未决（这是本文最该被读的一节）

| # | 矛盾 | 两边各是什么 |
|---|---|---|
| C1 | 球径起点 | `PROP_SET_PLAN.md` §0.5/§2.3 主张 **0.04 m** 起手（真实尺寸才成立）；现行数据是 **0.2997 m** |
| C2 | 第一关的金数 | `LEVEL_CONTENT_PLAN.md` §2.1、`PICNIC_CORNER_LEVEL_ONE.md` §3.4 写 **110 件 / 412 u**；`placement.json` 与导入器是 **127 件 / 1500 u** |
| C3 | 上限 vs 内容 | 上限 2.20 m；本关内容 `D_reach = 2.2746 m`（被上限夹住）；但按物理体积重算，全关只有 2.517 m³，2.20 m 需要 5.575 m³ ⇒ 建议本关上限降到 **~1.70 m** |
| C4 | 运行时没有「道门」这个概念 | 8 道门是设计意图；运行时只有「球体积 ÷ 10，再夹一个逐关卡序列化的绝对体积上限」（`PickupAbsorbMath.cs:145-153`、`KatamariBallMotor.cs:217` 的 `maxItemVolumeWorldM3`），没有按图摆放的 8 道门——`MessyRoom/README.md` §3.3 自己记了这一条 |
| C5 | 星级带 | `StoryMode/README.md` §「关卡球径上限与第一关星级带」写 1.25 / 1.40 / 1.42；`LEVEL_CONTENT_PLAN.md` §7 与关卡 JSON 是 1.25 / 1.45 / 1.60 |
| C6 | 阶梯的轴 | 「球径 ≥ 最长边」已被 `PROP_SET_PLAN.md` §2.8 明确**取代**，但 §2.3–§2.5 的旧表还在文件里 |
| C7 | 大件能不能吞 | `03_MAPS_ASSETS.md` §2 说超大桌子当地标不可卷；`DungBeetleMeadow` §3.4 / `MessyRoom` §3.4 说容器球大就能整件吞 |
| C8 | 斜坡 | `03_MAPS_ASSETS.md` §3 建议玩具房用斜坡；`ToyRoom/README.md` §8 故意不做，改用 4 块 0.26 m 拼图垫 |
| C9 | 档位数 | `TIER_DIAMETERS` 是 6 档（0.30/0.50/0.80/1.10/1.50/2.20），三张地图 README 用 4 档（t0–t3），`ContentVolume` 用 8 档，草甸用 t0–t7 |
| C10 | 全图期望值 | 关卡 JSON 的 `map_expectations` 还写 `absorbable_instances_total: 452` / `gain_units_total: 1284`（旧全图计划），与切片的 127 / 1500 是两套账 |

---

## 10. 开放问题

1. **以哪套档位为准**（C9）？建议：`TIER_DIAMETERS` 只是"校验器用的直径刻度"，与 README 的 t0–t3
   不是一回事，但要在一处写下映射，否则下一个作者会挑错那张表。
2. **110/412 还是 127/1500**（C2）？这不是数字之争，是谁是**真源**之争：如果 `placement.json`
   是真源，`LEVEL_CONTENT_PLAN` 的表就该标成历史；反之导入器就有 bug。
3. **2.20 m 要不要降到 1.70 m**（C3）？这会影响三张图的第 8 道门。
4. **多支撑怎么写**（R21）：`stackedOn` 单值够不够，还是必须升级成 `rests_on[]`？
   本文的姊妹篇 `Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md` §3.2 给了提案。
5. **门的运行时实现**（C4）：8 道门今天只有 1 道是真的；是"按图手工摆阻挡盒"还是"加一个
   size-gate 组件"，两条路成本差很多。
6. **密度上限**：三张图都是"每档 40–55 件"，但单区能同时成阻挡/支撑的只有 6 件（见
   `PROP_BEHAVIOURS.md` §2）。**密度和"球边上的可用槽位"是两个不同的约束**，今天没有规则把它们绑在一起。
