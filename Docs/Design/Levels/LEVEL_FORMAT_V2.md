# 关卡数据格式 v2（最终提案）

> 状态：**`FINAL_PROPOSAL`，2026-11-16**（原 `DRAFT_BLOCKED_ON_RESEARCH` 已解除）。触发：owner 2026-11-16「整体升级地图数据结构 + 网页编辑器一起升」；研究先于设计，两份研究均已落地：
> [ORIGINAL_TERRAIN_AND_LAYOUT](../../Research/KatamariBallRules/ORIGINAL_TERRAIN_AND_LAYOUT.md)（REROLL 导出表能推出什么）与
> [NOCLIP_KATAMARI_FORMAT](../../Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)（PS2 舞台包/任务包的字节布局）。
> **本文是设计定稿，不是 owner 决定清单**：字段名、代码与本项目的写入方式是提案；凡 owner 已拍板的，逐条标「已定」。
> 本轮**只写 Docs**：没有改代码、场景、`placement.json`、`Tools/`、Unity，没有跑 git 写命令、没有开 Unity。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；其余相对仓库根。**按符号名与节号引用，不写行号**（相关文件正被并行修改）。
>
> 兄弟文档：[LEVEL_AUTHORING](../Tools/LEVEL_AUTHORING.md)（当前格式与工具）、[LEVEL_EDITOR_WEB](../Tools/LEVEL_EDITOR_WEB.md)（网页编辑器）、[TERRAIN_SUPPORT](../BallMotor/TERRAIN_SUPPORT.md)（v1 支撑模型）、[CONTACT_MODEL_V2](../BallMotor/CONTACT_MODEL_V2.md)（接触求解器）、[PHYSICS_TEST_FIELD](../BallMotor/PHYSICS_TEST_FIELD.md)、[COLLIDER_PROXY](../PickupAbsorb/COLLIDER_PROXY.md)、[PROP_BEHAVIOURS §10](../PickupAbsorb/PROP_BEHAVIOURS.md)、[PARITY_AUDIT](../Parity/PARITY_AUDIT.md)、[OPEN_DECISIONS](../../OPEN_DECISIONS.md)。

---

## 0. 研究回填：上一版每个空槽现在填什么

| 旧问题 | 现在的事实 | 改了什么 |
|---|---|---|
| **Q-T1** 地形以什么形式存在 | PS2 舞台包有完整几何：Make Cancer 3 个有效图形扇区、7 个模型、**29,846 顶点 / 44,340 索引 / 232 材质段**；另有**碰撞几何：68 个 AABB + 7,154 顶点 + 3,456 三角形**。REROLL 导出表里没有地形，不代表原作没有。【实测】 | `terrain` 定为**静态三角网**，AABB 做 broad phase；高度场只作大而简单区域的优化 |
| **Q-T5** 有没有悬垂/隧道 | 有 3D 表面（多台地、护岸、屋顶/檐口）；7,154 个碰撞顶点明显不是一张单值高度场。平台/阳台/檐下空间要求「球心下方」与「头顶上方」分开处理。 | 支撑查询按「球底以下的最高面」而不是「球心附近的最高面」；天花板单独做推出（§13 T1） |
| **Q-T2 / Q-N2** 旋转/缩放可得吗 | 每个实例 **0x40 字节**：`+0x20` 轴（3×f32）、`+0x2C` 角度（f32，记录里取负用）。Cancer 524 个实例 **401 个角度非零**（76.5%）、358 个 >45°、301 个 >90°、最大 240°。缩放 `+0x30..+0x38`：518 个 `(1,1,1)`、5 个均匀 0.9/0.1、**1 个非均匀** `(0.0741, 0.0291, 0.0197)`。 | `props[].rotationQuat` 成为**必填**；`scale` 可选，非均匀有硬策略（§6.4） |
| **Q-T4** 碰撞数据形态 | 4 个碰撞扇区（Make Cancer 条目数 66/0/1/1）；每条 AABB 自带一个三角形带；顶点第 4 个 float 是 **zone id**（位模式），分布 `{0:5712, 24:762, 3:456, 8:131, 1:47, 5:46}`。 | `terrain.broadPhase.boxes[]` 与 `triangles.zoneIds`；zone id 作为不透明标签保留（§3.7） |
| **Q-T3 / Q-N4** 活动区机制 | 12 个 `DummyProps` 是**大件命中外壳**（全在 r<12 m 核心，19 关共 277 个），不是隐形墙；关卡包与任务表里**没有** bounds/radius/kill-plane 字段；社区 `barrier` 是按球径开门的**可见墙**。 | `activityArea` 由**我们作者化**：多边形墙段 + 球径门 + kill plane（§5）；不照抄 DummyProps |
| **Q-T6/Q-T7** 尺度与坐标帧 | 1 原始单位 = 1 cm；**REROLL `position` = −0.01 × PS2 平移**（三轴全取反，无平移），524 件中 360 件误差 <1 µm；因此两套数据**同一世界坐标系**。三轴取反是**点反射**（det −1），网格并进来要**翻转三角形绕序**。真实地面主带 **y ≈ −1.0 m**（946 m² 上表面里 55% 落在 −1.2…−0.6），我们的假地板 y=0 **高了约 1 m**。 | 导入直接使用原作高度，不再 re-seat 到 0；`windingFix:"flip"` 是参考导入的必填项 |
| **Q-N6** 许可边界 | noclip 仓库的 LICENSE 是 **MIT + 一段反编译说明**（GitHub 因该说明报 `NOASSERTION`），**不是 GPL-3**。原假设有误。 | 见 §17 与 AGENTS/OPEN_DECISIONS 的更正；解析器仍是「照规格重实现」，仓库许可仍待 owner |

**仍未解决、本文不假装解决的**：缩放字段的语义（那 1 件可能是贴花尺寸/碰撞盒尺寸）；zone id 的**玩法含义**（只有「逐三角形带标签」这一个事实）；7 个任务级体积的确切作用（其中第一个是 y=−1,691.5 cm 的巨大平面，像掉出判定面）；`moveType` 的语义；REROLL 523 与 PS2 524 差的那 1 件。

---

## 1. 设计原则：五层、一份实现、静态优先

### 1.1 v1 与 v2 的称呼

| 名称 | schema | 状态 |
|---|---|---|
| **v1** | `katamari.placement/0` | 今天 13 份 `placement.json` 的格式；v2 转换器的输入 |
| **v2** | `katamari.placement/2` | 本文定稿的格式；**继承同名族**，只升主版本号（工具、导入器、编辑器都只需认一个字符串） |

文件名仍是 `Docs/Design/Levels/<id>/placement.json`。**一个关卡一份文档**，地形侧车文件放在同目录 `terrain/` 下。主版本号取 `2`（不是 `1`）：外部一直叫「v2」，而 `/1` 是早期半成品讨论用过的号，跳过去避免歧义。

引用关卡目录里还有 `placement.model.json`、`placement.gate_exact.model.json`、`placement_x6*.json` 等**派生视图**（带模型路径/门的精确复算），它们不是真源、不在 13 份之列、转换器不处理；参考导入只更新 `placement.json` 与侧车。

### 1.2 五层各回答一个问题

| 层 | 回答什么 | 静态? | 可吸? | 运行时消费者 | 今天对应物 |
|---|---|---|---|---|---|
| `meta` | 这一关是什么：起始/目标/上限、时限、出生点、场景、容量、源 | — | — | 导入器、回合、编辑器 | `level` 块 |
| `terrain` | 球脚下的地面/墙/顶是什么形状（三角网 + 碰撞 AABB + 空间网格） | 是 | 否 | `TerrainFieldHost` → 电机支撑/接触 | 一张 `ground` 平面 |
| `structures` | 建筑、护岸、斜板这类**大件不可吸物**的解析体（OBB/slab/弯坡） | 是 | 否 | 电机静态表 + 扫掠 | `role=box/ramp/curve_ramp` |
| `activityArea` | 球能去哪、多大才能去、掉出去怎么办 | 是 | 否 | `ActivityAreaHost` | 四面 `boundary` 平面 |
| `props` | 每件可吸物：是什么、多大、坐哪、朝哪、值多少、怎么动、挂在谁下面 | 否（可动） | 是 | `PickupFieldHost` + 行为宿主 + 账本 | `props[]` + `defs[]` |

`defs[]`、`cameras`、`renderPresets`、`regions` 仍是旁挂表，不属五层之一。

### 1.3 三条硬边界（引擎侧已经定死，研究改不了）

1. **地形不进 v1 的水平扫掠。** 斜面已经是「只支撑、绝不进扫掠」（TERRAIN_SUPPORT）。v2 里**陡峭三角形**会挡球，但那是接触求解器（CONTACT_MODEL_V2 的 θ 分类）的职责，不是把地形塞进 `SweepMath`。在接触 v2 打开之前，需要水平阻挡的地方必须用 `structures` 的 slab 表达。
2. **面 id 必须稳定。** 地形的面 id 粒度是 **broad-phase AABB（或 chunk）**，不是三角形；否则每帧 id 都变，`sameSurface` 抑制失效，球在坡上抖着落。
3. **支撑只取球底以下的面。** 悬垂/隧道一旦存在，「球心附近最高的面」会把天花板当地板。v2 的支撑查询是「`ballBottom + stepLimit` 以下最高的面」，头顶的面单独做推出。

---

## 2. `meta`：关卡元数据

### 2.1 形状

```json
"meta": {
  "id": "story_01_bench_size",
  "scene": "StoryMode",
  "units": "m",
  "convention": "unity-y-up-left-handed-quat-xyzw",
  "ball": {
    "startDiameterM": 0.2997,
    "targetDiameterM": 2.2,
    "maxDiameterRef": "global",
    "start": [-12.5, 0.15, -12.5],
    "spawnSource": "authored"
  },
  "timeLimitS": 180,
  "playerSpawn": [-14.5, 0.5, -14.5],
  "groundY": 0.0,
  "bounds": [[-19.4, -19.4], [-6.0, -6.0]],
  "spawnClearanceM": 1.0,
  "zones": [{"id": "alcove", "code": 24, "kind": "ground"}],
  "arena": {"id": "story_01_bench_size"},
  "caps": {"props": 512, "terrainTriangles": 8192, "terrainVertices": 16384,
           "staticSolids": 256, "activityWalls": 64},
  "budget": {"planes": 8, "boxes": 8, "ramps": 4, "blocking": 6, "propSupports": 6},
  "expected": {"propCount": 130, "valueUnits": 1522, "terrainTriangles": 0},
  "source": {"kind": "authored"},
  "migration": {"from": "katamari.placement/0", "maxDiameterM": 2.2}
}
```

### 2.2 字段

| 字段 | 类型 | 必填 | 语义 |
|---|---|---|---|
| `id` | string | 是 | 关卡 id，等于目录名；导入器用它找目录 |
| `scene` | string | 是 | Unity 场景名（`StoryMode`、`PhysicsTestField`…） |
| `units` / `convention` | string | 是 | 固定 `"m"` / `"unity-y-up-left-handed-quat-xyzw"`；别的值直接报错 |
| `ball.startDiameterM` | number | 是 | 起始**语义直径**（米，3 位小数） |
| `ball.targetDiameterM` | number | 否 | 目标直径；剧情关必填，实验/测试关可省 |
| `ball.maxDiameterRef` | `"global"` \| `"level"` | 是 | **已定**：全项目一个全局上限（引擎护栏，远高于 2.20 m，按试玩调）。`"level"` 只给实验关/测试关用，且必须同时给 `maxDiameterM` |
| `ball.maxDiameterM` | number | 条件 | `maxDiameterRef == "level"` 时必填；剧情关用它会被校验器记 `[meta_cap_override]` warning |
| `ball.start` | 3×number | 是 | 球心出生点（世界米） |
| `ball.spawnSource` | `"authored"` \| `"fieldCentreGuess"` \| `"reference"` | 是 | **推定值必须标出来**。参考关卡是 `fieldCentreGuess`（原作出生点不可知，见 ORIGINAL_TERRAIN §2.4） |
| `timeLimitS` | number | 剧情关必填 | 时限（秒）；`0` 表示不限时（实验/测试关） |
| `playerSpawn` | 3×number | 是 | 玩家出生点；与球出生点分开 |
| `groundY` | number | 否 | **兜底平面高度**：没有 `terrain` 时它就是地面；有 `terrain` 时它必须**低于地形最低点**（否则兜底平面会顶穿地形），校验器 `[meta_ground_y]` 检查。参考关卡的真实地面主带是 −1.0（§0），但碰撞 AABB 的 y 下限到 −13.85 m，所以兜底取 −20，不是 0 |
| `bounds` | `[[minX,minZ],[maxX,maxZ]]` | 是 | 轴对齐**作者框**：用来查道具越界、给编辑器画框，**不是**活动范围（活动范围见 §5） |
| `spawnClearanceM` | number | 是 | 出生点净空半径 |
| `zones` | array | 否 | 命名区：`id`（字符串，作者用）+ `code`（0–255 整数，地形三角形引用）+ `kind`（`ground`/`room`/`void`/`other`，仅提示） |
| `arena` | object | 是 | `{id, notes}`；导入器据此建 `LevelArenaHost` |
| `caps` | object | 是 | 数据规模上限（§12）；转换器按文件实际值补齐 |
| `budget` | object | 是 | 电机 v1 注册预算（沿用今天五个键）。它约束 `structures`，**不约束 `terrain`**（地形有自己的表）；接触 v2 打开后 `boxes`/`ramps` 并入 256 OBB 表 |
| `expected` | object | 是 | 设计门：件数、价值单位、三角数；`levelbuild check` 对实际值 |
| `source` | object | 是 | `kind` ∈ `authored`/`reference`/`imported`；参考关卡的 `kind=reference` 会让导入器拒绝写进受管根（§11.3） |
| `migration` | object | 否 | 只在 v1→v2 转换产物上出现，记录源 schema 与被弃用的每关上限；v2 新文件不得手写 |

**与旧版的字段改名**：`ballStart`→`ball.start`、`ballDiameterStart`→`ball.startDiameterM`、`ballDiameterMax`→`ball.maxDiameterM`/`maxDiameterRef`、`level`→`meta`。旧键在 v2 里是**未知键**，硬错误（未知键不得静默丢弃，见 OPEN_DECISIONS 的 `parent` 教训）。

---

## 3. `terrain`：静态三角网 + 碰撞 AABB broad phase

### 3.1 表示选择（研究已定，不再讨论高度场做主表示）

| 候选 | 结论 | 理由 |
|---|---|---|
| 高度场 | **只作优化** | 表达不了悬垂、隧道、桥、屋檐；原作有 3D 表面 |
| 分层图元 | 保留在 `structures` | 精确、便宜，但 256 个 OBB 装不下 3D 地面（1 m 网格只够 16×16 m） |
| **三角网 + AABB broad phase + 网格加速** | **主表示** | 原作碰撞数据就是 68 个 AABB / 3,456 三角形；我们的查询与预算都能按这个规模算 |

### 3.2 形状

```json
"terrain": {
  "id": "cancer_ground",
  "kind": "mesh",
  "winding": "ccw",
  "meshRef": {"path": "terrain/cancer.katerrain", "sha256": "…",
              "vertexCount": 7154, "indexCount": 10368, "triangleCount": 3456,
              "indexWidth": 2, "zoneIds": true, "classes": true},
  "broadPhase": {"kind": "aabb", "boxes": [
    {"id": "cb00", "min": [-7.57, -13.85, -7.99], "max": [10.81, 1.59, 6.54],
     "triangleRange": [0, 66]}
  ]},
  "chunks": [{"id": "c00", "boxRange": [0, 1], "triangleRange": [0, 66]}],
  "grid": {"cellM": 1.0, "origin": [-8.0, -8.0], "cellsX": 20, "cellsZ": 16},
  "materialSegments": [
    {"id": "ground", "firstIndex": 0, "indexCount": 6144,
     "material": "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundGround.mat"}
  ],
  "source": {"kind": "authored"}
}
```

（几何数组用 `…` 省略；`meshRef` 与内联 `vertices`/`indices` 二选一，见下。这是**结构示例**：省略的 box/材质段使各项计数不闭合，可校验的完整文件必须首尾相接。）

**内联 vs 侧车**：`vertices`/`indices` **二选一**，与 `meshRef` 互斥。

| 条件 | 存法 |
|---|---|
| `triangles ≤ 512` 且 `vertices ≤ 1024` | **内联**（JSON 数组，逐顶点一行，可 diff） |
| 超过 | **侧车** `terrain/<id>.katerrain` + `meshRef`（sha256 钉住）；JSON 里不再有几何数组 |

Make Cancer 的规模（3,456 三角形 / 7,154 顶点 / 10,368 索引）必然走侧车：二进制约 **113 KB**，内联 JSON 约 **300 KB** 且 diff 不可读。侧车格式与校验见 §8.3。

### 3.3 字段

| 字段 | 类型 | 必填 | 语义 |
|---|---|---|---|
| `id` | string | 是 | 地形 id；一个关卡**一个**地形（多块用地形内部的 chunk 表达） |
| `kind` | `"mesh"` | 是 | 唯一值；高度场将来若加是另一种 `kind`，不改本层结构 |
| `winding` | `"ccw"` | 是 | 绕序：俯视逆时针为正面。参考数据从 PS2 点反射进来后必须翻成 ccw（§8.3） |
| `vertices` / `indices` | array | 条件 | 世界米、Y 上；`indices` 每 3 个一组，长度必须 = `3 × triangles.count` |
| `meshRef` | object | 条件 | 侧车路径（相对关卡目录，禁 `..`）、sha256、三类计数、索引宽度、是否带 zone/class |
| `triangles.count` | int | 是 | 三角形数；校验器与几何数组/侧车对账 |
| `triangles.zoneIds` | int[] | 否 | 逐三角形 zone **code**（0–255），缺省全 0；长度 = count。原作是**逐顶点** zone，导入时按三角形三个顶点的多数票派生（平票取较小 code），所以这是我们的派生值，不是原样搬运 |
| `triangles.classes` | int[] | 是（派生） | 逐三角形接触类别：`1` ride（θ ≤ 45°）、`2` climb（θ > 45° 且法线朝上）、`3` wall/ceiling（法线不朝上，含竖直与朝下）；θ 是面法线与 +Y 的夹角。由法线算出，工具写、校验器复算；分类只做查询预筛，真正的滚/爬/挡仍由接触求解器按 θ 连续判定 |
| `broadPhase` | object | 是 | `kind:"aabb"` + `boxes[]`；每个 box 覆盖一段连续三角形，**必须无洞**（并集覆盖全部索引）且不重叠 |
| `broadPhase.boxes[].min/max` | 3×number | 是 | 世界 AABB（米）；放 1 mm 余量防边界漏检 |
| `broadPhase.boxes[].triangleRange` | `[start,count]` | 是 | 该 box 内的三角形**索引区间**（不是字节偏移）；区间按 start 升序、首尾相接、覆盖 0..count |
| `chunks` | array | 否 | 流式加载分组：`boxRange` 是 broadPhase 的 box 下标区间，`triangleRange` 是三角形区间；缺省由导入器按「≤512 三角形/块」自动分组 |
| `grid` | object | 否 | 烘焙网格参数（`cellM` 0.5–4、`origin`、`cellsX/cellsZ`）。**只存参数**，cell→triangle 的 CSR 表由导入器确定性生成到 `TerrainFieldHost`，不进 JSON（省字节、避免两份真值） |
| `materialSegments` | array | 是 | 视觉材质段：`firstIndex`/`indexCount` 必须首尾相接覆盖全部索引；`material` 是 Unity 资源路径。参考关卡可省（纯色块） |
| `source` | object | 是 | `kind` ∈ `authored`/`reference`/`imported`；`reference` 必须带 `windingFix:"flip"` 与 `provenance`（仓库外的路径字符串） |

### 3.4 为什么 broad phase 用 AABB、网格只做加速

- 原作自己的碰撞结构就是 **68 个 AABB + 每框一个三角形带**；照抄它，导入、预算、参考对拍三者共用一套数。
- 每步扫 68 个 AABB 约 **1.4k flops**（§13.3），与今天的平面/盒预算同量级；只在球附近的 2–6 个框里进窄相。
- `grid` 把「框内平均 51 个三角形」降到「球心 3×3 格约 6–12 个」；这是让 3,456 三角形在 Udon 里可跑的关键，但它**不改变正确性**——网格漏了也只是退化成全框扫描。

### 3.5 Udon 侧怎么装下：定长数组 + 一个查询宿主

UdonSharp 没有 `List`、字典、结构体、自定义类、jagged array；`Runtime/**` 里不许出现编辑器 API 或磁盘 JSON。因此：

- **几何不进 `KatamariBallMotor`**。单独一个 `TerrainFieldHost : UdonSharpBehaviour` 拥有全部定长数组与查询代码；电机每步调它**一次**（见 §3.6），拿回支撑面/接触法线，避免逐三角形跨行为调用。
- **chunk 化**：`TerrainChunkHost` 每个持有一块 ≤512 三角形 / ≤1,536 顶点的数组（`Vector3[]` 顶点、`int[]` 索引、`byte[]` 类别、`byte[]` zone）。块与块之间允许**重复接缝顶点**（碰撞不需要共享拓扑），换取每块自包含、可单独启用/禁用。
- **数组参数**：`TerrainFieldHost.SolveStep(Vector3 centre, float radius, Vector3 velocity, Vector3[] outNormals, float[] outDepths, int[] outSurfaceIds)` 把电机自己的定长结果数组传进去，一次调用、零逐接触 getter。**这是未验证项**：Udon 行为之间传数组（引用类型）与 `byte[]` 序列化都要在 Unity 里各验一次（§16 的 U1）。

**流式与分块（诚实的边界）**：Udon 不能运行时从磁盘加载网格，也不能在关卡运行中替换序列化数组。所以「流式」在 Udon 里的唯一形态是**预先序列化好的 chunk 宿主 + 启用/禁用**：远处 chunk 的 `GameObject` 关掉（省查询，不省内存），进区再开。真正超出单关预算（>8,192 三角形 / >32 chunk）的关卡要**拆成多个 arena/场景**（Hub 传送）或做**区域补丁**（同一关卡由多份 `terrain` 文档/场景组成，切区时整段启用），而不是指望运行时换地形或改数组。**`TerrainFieldHost` 的数组总长在导入期定死**，这是 Udon 的硬约束，不是实现偷懒。

### 3.6 每步查询契约（电机看到的东西）

```
TerrainFieldHost.BeginStep(ballCentre, radius)
  → 粗筛：chunk AABB（≤32）→ broad AABB（≤68）→ 网格 cell（3×3）
  → 窄相：球 vs 三角形最近点，先生成「球底以下的最高支撑」，再生成穿透接触
  → 写进电机传入的结果数组
电机读：support(n, offset, height, surfaceId) / contactCount / 每个 contact 的 n、depth、surfaceId
```

- **支撑**：只考虑 `planeHeightAtCentre ≤ ballBottom + stepLimit` 的三角形（默认 `stepLimit = 0.25·D`，诊断用；接触 v2 用 θ 规则后不再参与物理）；取最高者；法线用该三角形的面法线，**不混合**（混合会让棱上的球滚向错误方向）。
- **头顶/侧面**：所有法线指向球心一侧、且距离 < r 的三角形生成穿透接触；沿法线推出，去掉法向速度。
- **面 id**：`surfaceId = 1000 + broadBoxIndex`（地形段专用命名空间，避开 v1 的 0/1..8/10..13/100..105）。同一框内的三角形共享 id ⇒ 稳定。
- **预算**：每步最多 **48 个三角形窄相、4 个地形接触**；超了按「离球心最近」截断并计数（`GetTerrainDroppedCount()`），不静默。

### 3.7 zone id 是什么、不是什么

- **是什么**：碰撞三角形上的一个 `u8` 标签。原作分布 `{0:5712, 24:762, 3:456, 8:131, 1:47, 5:46}`，7 个任务级体积另有 `1,2,8,0,4,16,3`。它证明「逐三角形带区域标签」这件事存在。
- **不是什么**：**不是**我们已经知道含义的玩法区。没有名字、没有规则表、没有「站在 zone 8 会怎样」的证据；任务级第一个体积是 y=−16.9 m 的巨大平面，**像**掉出判定面，但未证实。
- 因此 v2 把 zone id 定为**作者标签 + 统计维度**：`meta.zones[].code` 是唯一真源，地形只存 code；运行时除诊断（`killPlane.zoneId` 只记录）外**不读**它。将来若证实「换区/掉出」规则，再加 `zones[].rule`，不改三角形字段。
- **碰撞 AABB 的编号没有玩法语义**：它只是 broad-phase 分组（原作 4 个碰撞扇区是文件布局，不是 4 个区）。v2 的 `broadPhase.boxes[].id` 由导入器按源顺序生成，作者不要拿它当 zone id。

---

## 4. `structures`：大件不可吸物的解析体

```json
"structures": {
  "solids": [
    {"id": "retaining_wall", "kind": "wall", "role": "box", "shape": "box",
     "center": [3.75, -0.5, 1.8], "size": [0.3, 1.6, 6.2],
     "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "collision": "box", "absorbable": false, "support": true,
     "material": "concrete", "notes": "护岸；球 1.2 m 后仍在，不属于 activity wall"},
    {"id": "board_to_table", "kind": "ramp", "role": "ramp", "shape": "box",
     "center": [-1.2, 0.42, 0.8], "size": [0.9, 0.04, 2.4],
     "rotationQuat": [-0.130526, 0.0, 0.0, 0.991445],
     "collision": "box", "absorbable": false, "support": true}
  ]
}
```

| 字段 | 说明 |
|---|---|
| `role` | `box`（静态 OBB：扫掠 + 顶面支撑）／`ramp`（只支撑的斜板；接触 v2 后是 slab）／`curve_ramp`（弧展开成 N 段 slab） |
| `shape` | `box`／`plane`（平面只给 `boundary` 遗留用；v2 新文件不写 `plane`） |
| `collision` | `box`／`none`；`none` 表示纯视觉标记 |
| `support` | 是否可当支撑面（`collision:"none"` 必须 `support:false`） |
| `curve` | 仅 `curve_ramp`：`radius_m`/`arc_deg`/`segments`/`bend`/`width_m`/`thickness_m`（沿用今天） |

**v1 的 `role=boundary` 不再进 `structures`**：它本质是活动范围，迁移到 `activityArea`（§5）。**`role=ground` 也不再进 `structures`**：平地由 `meta.groundY` 兜底，真地形由 `terrain` 表达。转换器遇到无法无损映射的边界（非矩形、非轴对齐）时保留为 `role:"boundary"` 并记 warning，不猜。

---

## 5. `activityArea`：作者化的范围 + 球径门 + kill plane

### 5.1 为什么是我们作者化

原作**没有**边界字段（missions 53 列、sim_missions 15 列、swirls、zone_profile 全查过）；能挡住球的是地形（海、护岸、坡）、房子和**按球径开门的可见 barrier**。12 个 `DummyProps` 是大件命中代理，不是墙。所以「原作的活动范围」= 地形 + 门，**随球长大而扩大**。我们没有原作的几何，只能作者化，并且必须把「门」做成一等字段。

### 5.2 形状

```json
"activityArea": {
  "walls": [
    {"id": "shore_base", "points": [[-2.0, 0.2], [3.7, 0.2], [3.7, 3.4]],
     "closed": false, "heightM": 3.0, "thicknessM": 0.2,
     "kind": "invisible"},
    {"id": "town_gate", "points": [[-19.0, -11.0], [-11.0, -11.0]],
     "closed": false, "heightM": 2.0, "thicknessM": 0.2,
     "minBallDiameterM": 1.5, "kind": "visible", "material": "wood"}
  ],
  "killPlane": {"y": -6.0, "respawn": "ballStart", "zoneId": 8},
  "softMarginM": 0.0,
  "source": "authored"
}
```

### 5.3 语义

| 字段 | 语义 |
|---|---|
| `walls[].points` | XZ 折线（≥2 点）；`closed:true` 时首尾自动相连。墙是有厚度的竖直 slab，**只挡不撑**（顶面不可站，避免窄条站立） |
| `walls[].minBallDiameterM` | **球径门（开门）**：球径 < 该值时这面墙**注册为实体**（挡住）；球径 ≥ 该值时**打开**（不注册、可穿过）。省略 = 永久墙。原作的 20 cm / 1.5 m / 60 m barrier 就是这个形状：「随球长大扩区」= 在通往外圈的位置放一道门，球够大时它打开 |
| `walls[].heightM` / `thicknessM` | 墙高/厚；高度必须 > 最大球半径才算墙（校验器警告 `[activity_wall_height]`） |
| `walls[].kind` | `visible`（有材质，玩家看得到）／`invisible`（只在编辑器显示） |
| `killPlane.y` | 球心低于此高度 ⇒ 传送回 `ballStart`、清零速度、计一次 `killCount`。**默认 −6.0 m 是我们选的**，不是原作值（原作那个 −16.9 m 平面是候选证据但未证实） |
| `killPlane.respawn` | `"ballStart"`（重生于 `meta.ball.start`）或 `[x,y,z]` 明确坐标；v1 只实现 `"ballStart"` |
| `killPlane.zoneId` | 记进诊断的 zone code；不改变行为 |
| `softMarginM` | v1 固定 0：**不做软边距**。理由：没有原作软边距证据，且软边距需要 HUD/减速一套新逻辑；硬墙 + 门 + kill plane 已覆盖「出不去」，软边距留给试玩决定（D-4） |

### 5.4 与电机容量的关系

- 接触 v2 **关**：墙只能映射成平面（`AddPlane`，≤8）。校验器 `[activity_wall_capacity]` 在 `walls` 有效段数 > 8 时报错；导入器拒绝多余段并在报告里点名。
- 接触 v2 **开**：墙注册成静态 OBB slab（共享 256 表），上限 64；`minBallDiameterM` 变化时由 `ActivityAreaHost` 重注册（只在跨门槛时，不是每帧）。
- **永远不用墙顶当支撑**，也不把墙塞进地形网格：墙是规则，不是几何。

---

## 6. `props`：旋转、缩放、父指针、行为

### 6.1 形状

```json
{"itemId": "teacup_12", "def": "teacup", "kind": "tableware",
 "behaviour": "static", "behaviourParams": {},
 "zone": "porch",
 "position": [0.42, -0.53, 1.07],
 "rotationQuat": [0.0, 0.766044, 0.0, 0.642788],
 "scale": [1.0, 1.0, 1.0],
 "aabb": [0.09, 0.07, 0.09],
 "volumeK": 0.7,
 "volumeMm3": 396900,
 "absorbDiameterReqMm": 196,
 "absorbable": true,
 "valueUnits": 1,
 "parent": "table_01",
 "rigidGroup": null,
 "support": "table_01",
 "stackedOn": null,
 "floatingOk": false,
 "notes": "src_y=-0.66; ref linkAction=0"}
```

### 6.2 字段

| 字段 | 类型 | 必填 | 语义 |
|---|---|---|---|
| `itemId` | string | 是 | **导入幂等键**；全局唯一；改名 = 删一件 + 加一件 |
| `def` | string | 是 | 指向 `defs[].id` |
| `kind` | string | 是 | 内容分类（喂吸收白名单/统计），与 `behaviour` **分两列**（PROP_BEHAVIOURS §10.1，已定） |
| `behaviour` | enum | 是 | `static`／`ambient`／`path`／`orbit`／`stacked`（PROP_BEHAVIOURS §3.2 词义不变） |
| `behaviourParams` | object | 否 | 按 behaviour 校验：`linkAction`（0/1/3/6，原作挂接动作）、`phase`、`pathSpeedIndex`、`orbitRadiusM`…；**未知键报错** |
| `zone` | string | 否 | `meta.zones[].id` 之一 |
| `position` | 3×number | 是 | **AABB 中心**（世界米）。原作 `position` 就是中心；保持这一约定，不再 re-seat |
| `rotationQuat` | 4×number `[x,y,z,w]` | 是 | Unity 顺序、单位长、**规范符号**（`w<0` 取反；`w==0` 时首个非零分量为正）。原作的轴+角经 §8.2 转换后写这里；无旋转写 `[0,0,0,1]` |
| `scale` | 3×number | 否 | 缺省 `[1,1,1]`；**非均匀策略见 §6.4** |
| `aabb` | 3×number | 是 | **已乘 scale 的**模型局部 AABB 尺寸（Unity 轴：x 宽、y 高、z 深）。下游只读这一个尺寸 |
| `volumeK` | number | 否 | 逐件覆盖；缺省取 def 的 `volumeK`，再缺省 0.70（PARITY_AUDIT G13）。范围 `0 < k ≤ 32` |
| `volumeMm3` | int | 是（派生） | `round(k · aabb.x · aabb.y · aabb.z · 1e9)`，定点 `long`（已定 (r)）。工具写、校验器复算；差 > 1 mm³ 报 `[prop_volume_stale]` |
| `absorbDiameterReqMm` | int | 是（派生） | `floor_mm((60 · volumeMm3 / π)^(1/3))`；原作门的整毫米口径（已定：按原作算式，k × bbox） |
| `absorbable` | bool | 是 | 作者声明；false 的件永远不可吸（结构件/装饰） |
| `valueUnits` | int | 是 | 账本整数（≥1；0/小数当场拒绝，已定 (j)）。与 `volumeMm3` 是两个轴：一个记账、一个几何门 |
| `parent` | string \| null | 否 | **单父指针**：父件的 `itemId`；null = 无父件。子件在父件被吃后**坠落**、不随父进账（已定 (s)） |
| `rigidGroup` | string \| null | 否 | 刚性组：同组实例运行时共用一个刚体变换（多件拼成一件摆设，如桌+桌腿），**不产生父子语义**；与 `parent` 互斥 |
| `support` | string \| null | 否 | 作者声明的支撑（结构 id 或道具 itemId）；导入期推导的提示，不是物理 |
| `stackedOn` | string \| null | 否 | 摞在另一件道具上；与 `support` 至多一个 |
| `floatingOk` | bool | 否 | 悬浮件（气球、吊饰）不参与浮空复检；与 `parent` 同时出现由校验器报矛盾（PROP_BEHAVIOURS §10.9 待定项 6，暂定父指针优先并 warning） |
| `notes` | string | 否 | 自由文本；转换器保留原样 |

### 6.3 旋转：从原作的轴+角到四元数（唯一转换点）

原作实例记录：`+0x20` 单位轴 `(ax, ay, az)`、`+0x2C` 角度 `a`（参考实现用 `−a`）。转换到我们的世界帧（`reroll = −0.01 × ps2`，点反射 `M = −I`）：

1. 用 `q_ps2 = quat(axis, −a)` 建 3×3 矩阵 `R`（float32 精度；对拍最大差 1.94e-4）。
2. 点反射下 `R' = M R M⁻¹ = R`（因为 `M = −I` 与任何旋转交换）。所以**矩阵不变**，只是轴的分量全取反；对四元数就是 `q' = (x, y, z, w)` 与 `q_ps2` 同旋转，或整体取负。
3. 写文件前 `canonical_quat`：`w<0` 取反；`w==0` 时首个非零分量为正；保留 6 位小数。

**校验器不重算四元数**（拿不到原作轴角），只校验单位长、规范符号、以及「`aabb × scale` 旋转后的世界 OBB 不与支撑面矛盾」（§9）。参考导入的逐件四元数由本地转换器写入，**仓库只存结果**。

### 6.4 缩放策略（已定原则，未定细节 → D-5）

| 情况 | 规则 | 理由 |
|---|---|---|
| 默认 | `scale` 省略 = `[1,1,1]`；写出来也必须是 1，否则校验器要求真实理由 | 518/524 件是单位缩放 |
| **挂在父件下的子件** | **永远不缩放**：`parent != null` 时 `scale` 必须缺省或 `[1,1,1]`，否则 `[prop_scale_attached]` 硬错误 | 已定「attached props 不缩放」：挂接件的位姿由父件决定，缩放子件会破坏接触面 |
| 均匀缩放（`sx=sy=sz`） | 允许，任意 `behaviour` | 等价于换一个尺寸的同一件 |
| 非均匀缩放 | 只允许 **`parent == null` 且 `behaviour == "static"` 且 def.collider.type ∈ {box,sphere}**；否则 `[prop_scale_nonuniform]` | 胶囊/凸包在非均匀缩放下不再是同类形状；动态件的接触与吸收门会随姿态变，不可解释 |
| 原作的 1 件非均匀 `(0.0741, 0.0291, 0.0197)` | 转换器照写 `scale`，但标 `absorbable:false`、`collision:"none"`（贴花/薄片，不参与玩法），并记进迁移报告的 `divergences` | 语义未验证（NOCLIP §12.1）；不拿它去赌接触或体积 |

`aabb` 一律是**已乘 scale** 的尺寸，`volumeMm3` 由它和 `volumeK` 推出。**不要**在运行时再乘一次 scale。

### 6.5 父指针与刚性组

- `parent` 的语义照抄原作 `parentBlock` 的 `(child,parent)` 对：单父、无环、父必须存在。Cancer 区域 2 有 130 对，其中 **90 条真父子**、40 条只有「我是挂接件」标志（父 = −1）。后者在 v2 里表达为 `parent:null` + `behaviourParams.linkAction` 原值，并记 `notes`。
- **12 个 `dummyParent` 锚点就是那 12 个 `DummyProps` 命中代理**（Cancer 全部在 area 0；`DUMMYHIT01/02/03` 是大件外壳）。它们不是可吸道具，v2 不写进 `props[]`；`props` 的件数是 511（523 − 12），不是 524。
- 运行时形状照 PROP_BEHAVIOURS §10.9：`int[] propParent`（−1 = 无父）+ 每 tick 浮空复检 + 子件坠落，**不做 carry**。
- `rigidGroup` 是作者便利：同组只在**变换**上刚性（导入器给同组建一个父 `Transform`），不改变吸收/坠落语义。组内不得有 `parent`，组 ≤16 件。

### 6.6 `defs[]`：类型级数据

```json
{"id": "teacup", "kind": "tableware",
 "model": "Assets/VRC_Katamari/Art/Props/teacup.fbx",
 "valueUnits": 1, "volumeK": 0.7,
 "collider": {"type": "capsule", "center": [0.0, 0.0, 0.0],
              "radius": 0.043, "halfHeight": 0.0, "axis": "y"},
 "scalePolicy": "uniform", "notes": ""}
```

| 字段 | 语义 |
|---|---|
| `model` | Unity 相对路径（`Assets/…`、`.fbx`）；导入器按它找网格与 manifest，**不**允许 `..` 或绝对路径 |
| `valueUnits` | 类型默认价值；实例可覆盖（`props[].valueUnits` 优先） |
| `volumeK` | 类型默认填充系数；实例可覆盖；缺省 0.70 |
| `collider` | 按型代理：`box`/`sphere`/`capsule`/`convex`（4–24 点），字段与校验规则**完全沿用** [COLLIDER_PROXY](../PickupAbsorb/COLLIDER_PROXY.md) §2：局部空间、`hull` 相对 `center`、未知/跨类型字段报错、盒 ⊆ 视觉 AABB。v2 只加一条：实例 `scale` 会作用在代理上，**非均匀缩放时胶囊/凸包不被允许**（§6.4） |
| `scalePolicy` | `uniform`（默认）／`nonuniform`；后者只表示该类型允许被非均匀缩放，仍需满足 §6.4 的实例侧三条 |
| `notes` | 自由文本 |

**`defs[]` 不存 `aabb`**：AABB 是逐实例的（旋转后的包围盒语义在 `props[].aabb`），类型级尺寸只在 manifest 里。`volumeMm3` 也不进 defs：它由实例 aabb × k 推出（§6.2）。

---

## 7. 两个完整例子

### 7.1 最小关卡（4 m × 4 m 平台，两块三角形地面、一件道具、一面墙；其余方向交给 kill plane）

```json
{
  "schema": "katamari.placement/2",
  "meta": {
    "id": "tiny_pad",
    "scene": "PhysicsTestField",
    "units": "m",
    "convention": "unity-y-up-left-handed-quat-xyzw",
    "ball": {"startDiameterM": 0.30, "targetDiameterM": 0.60,
             "maxDiameterRef": "level", "maxDiameterM": 2.20,
             "start": [0.0, 0.15, 0.0], "spawnSource": "authored"},
    "timeLimitS": 0,
    "playerSpawn": [0.0, 0.5, -2.4],
    "groundY": -2.0,
    "bounds": [[-2.0, -2.0], [2.0, 2.0]],
    "spawnClearanceM": 0.2,
    "zones": [{"id": "pad", "code": 0, "kind": "ground"}],
    "arena": {"id": "tiny_pad"},
    "caps": {"props": 16, "terrainTriangles": 512, "terrainVertices": 1024,
             "staticSolids": 8, "activityWalls": 8},
    "budget": {"planes": 8, "boxes": 8, "ramps": 4, "blocking": 6, "propSupports": 6},
    "expected": {"propCount": 1, "valueUnits": 1, "terrainTriangles": 2},
    "source": {"kind": "authored"}
  },
  "terrain": {
    "id": "pad",
    "kind": "mesh",
    "winding": "ccw",
    "vertices": [[-2.0, 0.0, -2.0], [2.0, 0.0, -2.0], [2.0, 0.0, 2.0], [-2.0, 0.0, 2.0]],
    "indices": [0, 2, 1, 0, 3, 2],
    "triangles": {"count": 2, "zoneIds": [0, 0], "classes": [1, 1]},
    "broadPhase": {"kind": "aabb", "boxes": [
      {"id": "pad_0", "min": [-2.0, -0.01, -2.0], "max": [2.0, 0.01, 2.0],
       "triangleRange": [0, 2]}
    ]},
    "grid": {"cellM": 4.0, "origin": [-2.0, -2.0], "cellsX": 1, "cellsZ": 1},
    "materialSegments": [
      {"id": "ground", "firstIndex": 0, "indexCount": 6,
       "material": "Assets/VRC_Katamari/Art/Materials/Mat_PlaygroundGround.mat"}
    ],
    "source": {"kind": "authored"}
  },
  "structures": {"solids": []},
  "activityArea": {
    "walls": [
      {"id": "w_n", "points": [[-2.0, 2.0], [2.0, 2.0]], "closed": false,
       "heightM": 1.0, "thicknessM": 0.2, "kind": "invisible"}
    ],
    "killPlane": {"y": -4.0, "respawn": "ballStart", "zoneId": 0},
    "softMarginM": 0.0,
    "source": "authored"
  },
  "props": [
    {"itemId": "acorn_01", "def": "acorn", "kind": "nature", "behaviour": "static",
     "behaviourParams": {}, "zone": "pad",
     "position": [-0.8, 0.015, -0.6], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "aabb": [0.024, 0.03, 0.0228], "volumeK": 0.7, "volumeMm3": 11491,
     "absorbDiameterReqMm": 60, "absorbable": true, "valueUnits": 1,
     "parent": null, "rigidGroup": null, "support": null, "stackedOn": null,
     "floatingOk": false}
  ],
  "defs": [
    {"id": "acorn", "kind": "nature", "model": "Assets/VRC_Katamari/Art/Props/acorn.fbx",
     "valueUnits": 1, "volumeK": 0.7,
     "collider": {"type": "sphere", "center": [0.0, 0.0, 0.0], "radius": 0.012},
     "scalePolicy": "uniform"}
  ],
  "cameras": [],
  "renderPresets": [],
  "regions": []
}
```

> **体积必须是整数且可复算**：`0.024×0.03×0.0228 = 1.6416e-5 m³`；`k=0.7` ⇒ `11,491 mm³`（四舍五入）。`absorbDiameterReqMm = floor((60×11491/π)^(1/3)) = 60`。手改其中一个而不同时改另一个，校验器会报 `[prop_volume_stale]`/`[prop_absorb_req_stale]`。

### 7.2 Cancer 风格片段（参考数据；本地只读、不进 git）

```json
{
  "schema": "katamari.placement/2",
  "meta": {
    "id": "_ref_cancer",
    "scene": "StoryMode",
    "units": "m",
    "convention": "unity-y-up-left-handed-quat-xyzw",
    "ball": {"startDiameterM": 0.222, "targetDiameterM": 0.80,
             "maxDiameterRef": "level", "maxDiameterM": 1.20,
             "start": [3.0338, -0.889, 7.781], "spawnSource": "fieldCentreGuess"},
    "timeLimitS": 0,
    "playerSpawn": [3.0338, -0.5, 7.781],
    "groundY": -20.0,
    "bounds": [[-26.41, -32.36], [32.47, 47.92]],
    "spawnClearanceM": 0.5,
    "zones": [{"id": "default", "code": 0}, {"id": "zone_a", "code": 24},
              {"id": "zone_b", "code": 3}, {"id": "zone_c", "code": 8}],
    "arena": {"id": "_ref_cancer"},
    "caps": {"props": 511, "terrainTriangles": 4096, "terrainVertices": 8192,
             "staticSolids": 256, "activityWalls": 8},
    "budget": {"planes": 8, "boxes": 8, "ramps": 4, "blocking": 6, "propSupports": 6},
    "expected": {"propCount": 511, "terrainTriangles": 3456},
    "source": {"kind": "reference", "provenance": "~/KatamariRef/noclip_export/kse_out"}
  },
  "terrain": {
    "id": "cancer_ground",
    "kind": "mesh",
    "winding": "ccw",
    "meshRef": {"path": "terrain/cancer.katerrain", "sha256": "<tool-computed>",
                "vertexCount": 7154, "indexCount": 10368, "triangleCount": 3456,
                "indexWidth": 2, "zoneIds": true, "classes": true},
    "triangles": {"count": 3456},
    "broadPhase": {"kind": "aabb", "boxes": [
      {"id": "cb00", "min": [-7.57, -13.85, -7.99], "max": [10.81, 1.59, 6.54],
       "triangleRange": [0, 66]},
      {"id": "cb01", "min": [-3.0, -2.4, -4.0], "max": [7.0, 0.4, 6.0],
       "triangleRange": [66, 900]}
    ]},
    "chunks": [{"id": "c00", "boxRange": [0, 1], "triangleRange": [0, 966]}],
    "grid": {"cellM": 1.0, "origin": [-8.0, -8.0], "cellsX": 20, "cellsZ": 16},
    "materialSegments": [
      {"id": "ground", "firstIndex": 0, "indexCount": 10368}
    ],
    "source": {"kind": "reference", "windingFix": "flip",
               "provenance": "PS2 stage 1879b0/135d18.bin → kse_out"}
  },
  "structures": {"solids": [
    {"id": "retaining_wall", "kind": "wall", "role": "box", "shape": "box",
     "center": [3.75, -0.9, 1.8], "size": [0.3, 1.4, 6.2],
     "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "collision": "box", "absorbable": false, "support": true}
  ]},
  "activityArea": {
    "walls": [
      {"id": "authored_core", "points": [[-8.0, -4.0], [8.0, -4.0], [8.0, 8.0],
       [-8.0, 8.0]], "closed": true, "heightM": 3.0, "thicknessM": 0.2,
       "kind": "invisible"},
      {"id": "town_gate", "points": [[-8.0, -4.0], [8.0, -4.0]], "closed": false,
       "heightM": 2.0, "thicknessM": 0.2, "minBallDiameterM": 1.5, "kind": "visible"}
    ],
    "killPlane": {"y": -6.0, "respawn": "ballStart", "zoneId": 8},
    "softMarginM": 0.0,
    "source": "authored"
  },
  "props": [
    {"itemId": "crab_001", "def": "small_fiddler_crab", "kind": "animal",
     "behaviour": "static", "behaviourParams": {"linkAction": 0}, "zone": "zone_a",
     "position": [4.80, -1.32, 3.10],
     "rotationQuat": [0.0, 0.766044, 0.0, 0.642788],
     "aabb": [0.115, 0.080, 0.086], "volumeK": 0.7, "volumeMm3": 553840,
     "absorbDiameterReqMm": 219, "absorbable": true, "valueUnits": 1,
     "parent": null, "rigidGroup": null, "support": null, "stackedOn": null},
    {"itemId": "ceiling_light_01", "def": "ceiling_light", "kind": "furniture",
     "behaviour": "static", "behaviourParams": {"linkAction": 6}, "zone": "zone_b",
     "position": [0.0, 0.21, 0.0],
     "rotationQuat": [1.0, 0.0, 0.0, 0.0],
     "aabb": [0.16, 0.10, 0.16], "volumeK": 0.5, "volumeMm3": 1280000,
     "absorbDiameterReqMm": 290, "absorbable": true, "valueUnits": 9,
     "parent": "house_01", "rigidGroup": null, "support": null, "stackedOn": null},
    {"itemId": "decal_017", "def": "decal", "kind": "decor",
     "behaviour": "static", "behaviourParams": {}, "zone": "zone_b",
     "position": [1.2, -0.55, 0.4],
     "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "scale": [0.0741, 0.0291, 0.0197],
     "aabb": [0.002, 0.0003, 0.0004], "volumeK": 0.7, "volumeMm3": 0,
     "absorbDiameterReqMm": 0, "absorbable": false, "valueUnits": 1,
     "parent": null, "rigidGroup": null, "support": null, "stackedOn": null,
     "notes": "ref non-uniform scale; unverified semantics; excluded from gameplay"}
  ],
  "defs": [
    {"id": "small_fiddler_crab", "kind": "animal",
     "model": "Assets/VRC_Katamari/Art/Props/small_fiddler_crab.fbx",
     "valueUnits": 1, "volumeK": 0.7,
     "collider": {"type": "convex", "center": [0.0, 0.0, 0.0],
                  "hull": [[-0.06, -0.04, -0.05], [0.06, -0.04, -0.05],
                           [0.0, 0.045, 0.05], [0.0, -0.03, 0.05]]},
     "scalePolicy": "uniform"}
  ],
  "cameras": [],
  "renderPresets": [],
  "regions": []
}
```

> 这是**片段**，不是可独立通过校验的文件：`meshRef.sha256` 是占位、`structures.solids` 只给了一条、`zone_a/zone_b/zone_c` 的 code 是**作者暂定映射**（原作的 24/3/8 到底叫什么仍未证实，§3.7）。逐三角形的 `zoneIds`/`classes` 在侧车里，JSON 只写 `count`（§8.3）。原作的 zone 分布是**逐顶点**的（7,154 个值），我们按每三角形三个顶点的多数票派生逐三角形标签，所以不照抄那组计数。

---

## 8. 规范化、坐标与侧车

### 8.1 规范字节（唯一写出格式）

| 规则 | 值 |
|---|---|
| 编码 | UTF-8 无 BOM；LF；末尾一个换行 |
| 缩进 | 2 空格；对象一成员一行；数组元素一行一个（顶点 `[x, y, z]` 单独一行） |
| 键序 | 文档：`schema, meta, terrain, structures, activityArea, props, defs, cameras, renderPresets, regions`；对象内部按本文表格的字段顺序（`*_KEYS` 常量）；**未知键是错误**，所以不存在「保留键序」的歧义 |
| 排序 | `props` 按 `itemId`；`defs` 按 `id`；`structures.solids` 按 `id`；`activityArea.walls` 按 `id`；`broadPhase.boxes`/`chunks` 按 `id`；`zones` 按 `code` 再 `id`；`cameras`/`renderPresets` 按 `id`；**几何数组保持作者/源顺序**（顶点/索引的顺序是几何身份，不能排序） |
| 数字 | 位置/尺寸/`aabb`/`scale` 4 位；四元数 6 位；`volumeK` 4 位；`volumeMm3`/`absorbDiameterReqMm` 整数；角度字段 3 位；所有数不得是 NaN/Inf；`-0.0 → 0.0`（全局，含嵌套数组） |
| 四元数 | 规范符号：`w<0` 整体取反；`w==0` 时首个非零分量为正 |
| 布尔/空 | 所有可空键（`parent`/`rigidGroup`/`support`/`stackedOn`）显式 `null` 与省略等价；**规范写出一律省略**。本文示例为可读性保留 `null`，转换器/导出器不写 |
| 重复 | 同层不得有重复键；`itemId`/`id` 全局唯一 |
| 幂等 | `load → dump` 逐字节相同；`v2 → v2` 转换是 no-op；`v1 → v2` 转换是确定性的（同输入同输出，两次运行同 sha256） |

### 8.2 坐标系与点反射（唯一换算点）

- 文件里是 **Unity 世界帧**：左手、Y 上、米、四元数 `(x,y,z,w)`。
- REROLL 位置 = `−0.01 × PS2`。顶点同样 `(−x,−y,−z)/100`；**三角形绕序翻一次**（det −1）。绕序翻转后要重新计算面法线并把 `classes` 重算。
- 旋转矩阵在点反射下不变（§6.3）；**不要**对四元数做 `x,y,z` 取反再当作新旋转——那是把「同一旋转的另一种写法」当成「反射后的旋转」，会和网格一起错一次。
- Blender 导出的老路径（Blender Z-up）仍按 `Unity = (B.x, B.z, B.y)` + 四元数保角反号；**v2 的 terrain 不走 Blender**，它的顶点直接是世界米。

### 8.3 侧车 `.katerrain`（>512 三角形时）

| 段 | 类型 | 说明 |
|---|---|---|
| magic | 4 B | ASCII `KTRN` |
| version | u16 | `1` |
| indexWidth | u8 | `2`（u16）或 `4`（u32）；顶点数 < 65,536 时用 2 |
| flags | u8 | bit0 = 带 `zoneIds`，bit1 = 带 `classes` |
| vertexCount / indexCount / triangleCount | u32×3 | 与 JSON `meshRef` 逐项相等 |
| positions | f32 LE × 3 × vertexCount | 世界米 |
| indices | u16/u32 LE × indexCount | 每 3 个一个三角形 |
| zoneIds | u8 × triangleCount | 可选 |
| classes | u8 × triangleCount | 可选 |

- 路径相对关卡目录，禁 `..`、禁绝对路径；sha256 小写十六进制，校验器**重算**，不符报 `[terrain_sidecar]`。
- 侧车可以提交（我们自己的关卡）；参考关卡的侧车在 `_ref_*` 目录里，被 `.gitignore` 排除。
- 侧车与 JSON 的重叠规则：有 `meshRef` 时 JSON **不得**再写 `vertices`/`indices`/逐三角形数组；只写 `triangles.count` 和（可选的）`zoneCounts` 摘要。

---

## 9. 校验器：错误码与键白名单

### 9.1 代码约定

- 每条错误/警告带一个稳定的 `[tag]` 前缀（沿用 `[def_model]`、`[collider]`、`[curve_radius]` 的既有风格），后接人类可读句子；**测试按 tag 子串断言**，改文案不改 tag。
- 硬错误（error）让 `levelbuild check` 非零退出；warning 只打印。`levelbuild check --json` 输出 `{"errors":[{code,path,message}],"warnings":[…]}`。
- 新增 tag 必须与 `*_KEYS` 白名单**同一批**落地，否则又会重演「键写了、被静默丢掉」的旧坑（OPEN_DECISIONS (s) 的 `parent` 教训）。

### 9.2 代码表（v2 新增；旧 tag 继续有效）

| tag | 级别 | 触发 |
|---|---|---|
| `[meta_units]` / `[meta_convention]` | error | 不是 `m` / `unity-y-up-left-handed-quat-xyzw` |
| `[meta_start_diameter]` | error | 缺失、≤0、> `maxDiameterM` |
| `[meta_time_limit]` | error | 剧情关缺失或 ≤0 |
| `[meta_cap_override]` | warning | 剧情关用 `maxDiameterRef:"level"` |
| `[meta_zone_code]` | error | `code` 不在 0–255；除 code 0 外重复；code 0 被声明超过一次（0 是默认区，最多一条） |
| `[meta_unknown_key]` | error | `meta`/`ball`/`caps` 出现未知键 |
| `[meta_ground_y]` | error | 有 `terrain` 时 `groundY` 不低于地形最低点（兜底平面会顶穿地形） |
| `[terrain_mesh]` | error | 索引越界/长度为非 3 倍数/退化三角形（面积 < 1e-9 m²）/顶点数超侧车宽度 |
| `[terrain_winding]` | error | 共享边的两个三角形按同方向遍历该边（说明其中一个绕序反了）；这是「面朝里」的确定性检测 |
| `[terrain_budget]` | error | 三角形/顶点超 `meta.caps` 或运行时单块上限（512 三/1,536 顶） |
| `[terrain_zone]` | error | `zoneIds` 长度≠三角形数，或引用了未声明的 code |
| `[terrain_broadphase]` | error | 区间有洞/重叠/乱序，或 AABB 未包住其三角形（容差 1 mm） |
| `[terrain_grid]` | error | `cellM` 越界、`origin` 不覆盖 broad phase |
| `[terrain_sidecar]` | error | 文件缺失、sha256 不符、计数不符、路径越界 |
| `[structure_role]` | error | v2 新文件写 `role:"boundary"`/`"ground"`（迁移产物允许并 warning） |
| `[structure_capacity]` | error | 静态固体 > `caps.staticSolids`（256）或 v2 关闭时 `ramps` >4、`boxes` >8 |
| `[activity_wall]` | error | 点数 <2、闭合多边形自交、`heightM ≤ 0`、`minBallDiameterM` 不是正数（若出现） |
| `[activity_wall_gate]` | warning | 门的阈值 ≤ 起始球径（这扇门从第一帧就是开的，多半写错了；要永久墙请省略该键） |
| `[activity_wall_capacity]` | error | 接触 v2 关闭且有效墙段 >8 |
| `[activity_wall_height]` | warning | `heightM` ≤ 最大球半径（墙挡不住） |
| `[activity_kill_plane]` | error | `y` 高于 `groundY`/地形最低点，或 `respawn` 不是 `ballStart`/坐标 |
| `[prop_id]` | error | `itemId` 缺失/重复/前后空白 |
| `[prop_parent]` | error | 父不存在、自指、成环、与 `rigidGroup` 同时出现 |
| `[prop_scale]` | error | 非正、非有限 |
| `[prop_scale_attached]` | error | `parent != null` 且 scale 非单位 |
| `[prop_scale_nonuniform]` | error | 非均匀但违反 §6.4 的三个条件 |
| `[prop_volume_stale]` | error | `volumeMm3` 与 `k·a·b·c` 差 >1 mm³；例外：`absorbable:false` 且体积 <1 mm³ 时允许 0 |
| `[prop_absorb_req_stale]` | error | `absorbDiameterReqMm` ≠ 由 `volumeMm3` 推出的整毫米 |
| `[prop_behaviour]` | error | 未知 behaviour / 该 behaviour 不允许的 `behaviourParams` 键 |
| `[prop_key]` | error | `props[]` 出现未知键（不得静默丢弃） |
| `[prop_support]` / `[prop_overlap]` | error | 声明支撑不存在/悬空；两件 AABB 互穿且无父子/摞放关系 |
| `[prop_bounds]` | error | 道具离开 `meta.bounds` 超过 5 cm |
| `[caps_props]` | error | 件数 > `caps.props`（每关按 130/511/4000 档自己声明） |
| `[canonical]` | warning | 文件不是规范字节（现有行为） |
| `[schema_legacy]` | error | 读到 v1 文件：提示先跑转换器 |

### 9.3 键白名单（`*_KEYS` 的 v2 等价物）

```python
META_KEYS      = ("id","scene","units","convention","ball","timeLimitS","playerSpawn",
                  "groundY","bounds","spawnClearanceM","zones","arena","caps","budget",
                  "expected","source","migration")
BALL_KEYS      = ("startDiameterM","targetDiameterM","maxDiameterRef","maxDiameterM",
                  "start","spawnSource")
CAPS_KEYS      = ("props","terrainTriangles","terrainVertices","staticSolids",
                  "activityWalls")
BUDGET_KEYS    = ("planes","boxes","ramps","blocking","propSupports")
EXPECTED_KEYS  = ("propCount","valueUnits","terrainTriangles")
SOURCE_KEYS    = ("kind","provenance","windingFix","licence")
ZONE_KEYS      = ("id","code","kind")
TERRAIN_KEYS   = ("id","kind","winding","vertices","indices","meshRef","triangles",
                  "broadPhase","chunks","grid","materialSegments","source")
TRI_KEYS       = ("count","zoneIds","classes","zoneCounts")
BROAD_KEYS     = ("kind","boxes")
BROAD_BOX_KEYS = ("id","min","max","triangleRange")
CHUNK_KEYS     = ("id","boxRange","triangleRange")
GRID_KEYS      = ("cellM","origin","cellsX","cellsZ")
SEGMENT_KEYS   = ("id","firstIndex","indexCount","material")
STRUCTURE_KEYS_V2 = ("id","kind","role","shape","center","size","curve","rotationQuat",
                     "collision","absorbable","support","meets","material","notes")
SOLIDS_KEYS    = ("solids",)
ACTIVITY_KEYS  = ("walls","killPlane","softMarginM","source")
WALL_KEYS      = ("id","points","closed","heightM","thicknessM","minBallDiameterM",
                  "kind","material","notes")
KILL_KEYS      = ("y","respawn","zoneId")
PROP_KEYS_V2   = ("itemId","def","kind","behaviour","behaviourParams","zone","position",
                  "rotationQuat","scale","aabb","volumeK","volumeMm3",
                  "absorbDiameterReqMm","absorbable","valueUnits","parent","rigidGroup",
                  "support","stackedOn","floatingOk","notes")
DEF_KEYS_V2    = ("id","kind","model","valueUnits","volumeK","collider","scalePolicy","notes")
```

`PROP_KEYS_V2` **删掉了** v1 的 `group`/`groupId`（原作的视觉挂接组由 `parent` + `rotationQuat` 表达；组容器由导入器按 `parent` 建）和浮点 `absorbDiameterReq`；**加进了** `behaviour`/`behaviourParams`/`scale`/`volumeMm3`/`absorbDiameterReqMm`/`parent`/`rigidGroup`/`floatingOk`。删掉的键在 v2 里是未知键，转换器负责搬走。

---

## 10. v1 → v2 转换器

### 10.1 要求

| # | 要求 | 验收 |
|---|---|---|
| C1 | **13 份** `placement.json` 全部可转：`_ref_cancer`、`_ref_mas1`、`hub`、`story_01_bench_size`、`physics_test_field/{C01,C03,C05,C06,C08,C09,C10,C13,C14}`（`find -name placement.json` 实测 13；`*.model.json`/`placement_x6*` 是派生视图，不算） | 逐份 `check` 0 error；份数对账（brief 曾写 14，实测 13） |
| C2 | **确定性**：同输入两次运行输出 sha256 相同；v2 再转一次是 no-op（字节不变） | 测试断言两次 dump 逐字节相同 |
| C3 | **重导入 `+0 ~0 -0`**：转换只改 JSON 形状，不改世界位姿/尺寸/件数（吸收门按新口径重算，见 C5），Unity 导入器第二次导入零新建/零更新/零删除 | Unity 操作员跑两次导入，第二次日志 `[level] +0 ~0 -0` |
| C4 | **不改语义的字段原样保留**：`itemId`、`def`、`kind`、`position`、`rotationQuat`、`aabb`、`valueUnits`、`notes`、`support`/`stackedOn` | 逐件 diff 报告 |
| C5 | **会改语义的字段必须报告**：`absorbDiameterReq` 从 bbox 门改成 `k·bbox` 门（已定），新值可能不同；转换器输出 `gate_changes.csv`（itemId、旧 mm、新 mm、`absorbable` 变化），**先看报告再提交** | 报告条数与人工复核 |
| C6 | **上限迁移**：`ballDiameterMax` 不再作为每关玩法上限；写进 `meta.migration.maxDiameterM` 供审计，运行时读全局 | 转换后 `meta.ball.maxDiameterRef == "global"` |
| C7 | **边界迁移**：4 张矩形 `boundary` 平面 → `activityArea.walls` 的闭合矩形；非矩形/非轴对齐保留 `role:"boundary"` + warning | 9 个测试场 + hub + story_01 的边界逐份核对 |
| C8 | **ground 迁移**：`role=ground` 的平面不再进 `structures`，`meta.groundY` 取其平面高度；没有 terrain 的关卡保持平地 | 关卡视觉不变（导入后场景对比） |
| C9 | **参考关卡**：转换产物仍在 `_ref_*/` 目录，被 `.gitignore` 排除；转换器不把任何网格/坐标写进仓库内其他路径 | `git status` 干净 |
| C10 | **可回滚**：转换在 git 分支上做；v1 文件在转换器验证通过前不被覆盖（先写 `.v2.json` 草稿，人工确认后 `--apply`） | 转换器有 `--dry-run` |

### 10.2 转换顺序（每份文件）

```
读 v1（katamari.placement/0）
  → 校验 v1 全绿（否则拒绝转换，不猜）
  → meta：改名 / 补 caps（按实际计数）/ 补 spawnSource
  → structures：ground → meta.groundY；boundary → activityArea.walls（可无损时）；
                 box/ramp/curve_ramp 原样进 structures.solids
  → props：补 behaviour="static"、behaviourParams={}、scale 省略、parent=null、
           rigidGroup=null、floatingOk=false；重算 volumeMm3 与 absorbDiameterReqMm
  → defs：补 volumeK（已有则保留）、scalePolicy="uniform"
  → 平地图省略 terrain；参考关卡的地形由 §11.5 导入器写（转换器不造几何）
  → canonical_doc → --dry-run 报告 → --apply
```

**为什么 `terrain` 不在这步生成**：转换器只做**形状迁移**，不造几何。平地图靠 `groundY`；参考关卡的地形由 §11.5 的参考导入器从提取器输出生成（那一步有网格与授权门）。这样「转换」与「导入原作」是两件可分别验收的事。

### 10.3 与 v1 读取器共存

- `placement_core` 保留 `SCHEMA_V1` 读取器一个版本周期（只读，用于转换/对比），**新写入只写 v2**。
- `PlacementImporter` 读 v1 时直接报 `[schema_legacy]` 并拒绝导入，避免「半升级」场景。
- 13 份文件全部转换并重导入通过后，删除 v1 读取器与 `level` 块支持（单独一次提交）。

---

## 11. Unity 导入映射

### 11.1 对象/组件映射

> 表里带**新**的组件/资产是本提案的落点，当前仓库还没有；`PlacementImporter`、`LevelArenaHost`、`PickupFieldHost` 等现有名字照旧。

| JSON | Unity | 说明 |
|---|---|---|
| `meta` | `LevelMetaHost`（新，挂在 `LevelContent` 根）+ `LevelArenaHost` | `startDiameterM`、`timeLimitS`、`targetDiameterM`、`maxDiameterRef`（解析到全局 `GlobalBallCap` 资产）；`ball.start`/`playerSpawn` 写场景出生点 |
| `terrain` | `TerrainRoot`（**新** `TerrainFieldHost`）+ `Chunks/<id>`（**新** `TerrainChunkHost` + `MeshFilter` + `MeshRenderer`） | 地形**永远不是** `MeshCollider`，也不进 `LevelArenaHost.staticBoxes`；`TerrainFieldHost` 持数组与查询，`LevelArenaHost` 只拿一个引用 |
| `structures.solids[role=box]` | `LevelContent/Structures/<id>` + `BoxCollider` → `staticBoxes` | 与今天逐位等价 |
| `structures.solids[role=ramp/curve_ramp]` | 同上 → `rampBoxes`（弯坡展开成 `<id>_00…` 子物体） | 与今天逐位等价 |
| `activityArea.walls` | **新** `ActivityAreaHost` + 每段一个 `BoxCollider`（关碰撞、只作注册源） | 跨门槛时重注册；不建 Renderer（`visible` 才建） |
| `activityArea.killPlane` | `ActivityAreaHost` 的标量字段 | 每步/10 Hz 查 `ballCentreY < y` → `motor.TeleportTo(ballStart)` + 清速度 |
| `props[]` | `LevelContent/Props/<kind>/<itemId>`（现有）+ `PickupFieldHost` 行 | `parent`/`volumeMm3`/`behaviour` 进新的行为宿主数组；`scale` 写 Transform.localScale（仅限合法情况） |
| `defs[]` | manifest + 模型 + `collider` 代理 | 与今天一致；`scalePolicy` 只在编辑器校验里用 |
| `cameras`/`renderPresets` | 编辑器/Blender 用，不进场景 | 不变 |

### 11.2 地形网格资产（**我们自己的关卡**）

- 导入器在 `Assets/VRC_Katamari/Generated/Levels/<levelId>/<terrainId>.asset` 生成一个 `Mesh`：
  - 顶点/索引来自 JSON 或侧车；按 `materialSegments` 切 submesh；
  - 确定性：同一份 JSON 在同一 Unity 版本下生成同样字节；
  - **只在文件不存在时 `CreateAsset`，存在时覆写**，绝不删除重建（保住 GUID 与场景引用）；
  - `.meta` 随资产提交；`Generated/` 下的资产是产物，但**提交**（否则别人打开场景是空网格）。
- `TerrainChunkHost` 的数组是**序列化在场景/预制体里的数据**，不是运行时从磁盘读；Udon 运行时不碰 JSON/磁盘（AGENTS 规则）。

### 11.3 参考关卡的地形（**未授权**）

- 路径：`Assets/_RefUnlicensed/Levels/_ref_cancer/Terrain.asset`（`.gitignore` 已排除整个 `_RefUnlicensed/`）；场景同样放在 `_RefUnlicensed/` 下。
- 导入器只在 `meta.source.kind=="reference"` 时走这条路径，并强制检查：
  1. 关卡目录匹配 `Docs/Design/Levels/_ref_*`；
  2. 网格来源路径在仓库外（`~/KatamariRef/...`），不接受仓库内文件；
  3. 目标路径以 `Assets/_RefUnlicensed/` 开头；
  4. 写入前打 `X-Unlicensed-Reference`/README 标记，编辑器 UI 显示红黑横幅（沿用 leveleditor 的约定）。
- **参考地形永不进 git、永不进上传**；`Resources`/`StreamingAssets` 里也不许出现。

### 11.4 幂等与「第二次导入 +0 ~0 -0」

- 导入键：结构/地形/墙按 `id`，道具按 `itemId`；组容器按 `rigidGroup`/父 `itemId` 派生。
- 位姿比较用变换矩阵（位置 1 mm、旋转点积 0.99999），尺寸/材质逐项比较；只有不同才写。
- 新增组件（`TerrainFieldHost`/`ActivityAreaHost`/`LevelMetaHost`）第一次导入计 `+N`（结构更新），第二次必须 `+0 ~0 -0`。
- 地形数组按「内容哈希」比较：sha256 相同就跳过重写，避免每次导入都算 100 KB 数组 diff。

### 11.5 参考关卡的导入链路（提取器输出 → 本地只读关卡）

```
~/KatamariRef/noclip_export/kse_out/
  instances.json     → props[]（平移/轴角→四元数/缩放/父/显示区）
  stage.glb          → 视觉网格（只作参考渲染；不参与碰撞）
  collision_area2.json → terrain（顶点/索引/AABB/zone）
  zones.json         → meta.zones / killPlane 候选
        │
        ▼  Tools/levelbuild/ref_level_import.py（**待写**；仓库内代码，只读仓库外数据）
Docs/Design/Levels/_ref_cancer/placement.model.json + terrain/*.katerrain
        │
        ▼  PlacementImporter（reference 分支）
Assets/_RefUnlicensed/Levels/_ref_cancer/…
```

- 适配器**不解析游戏二进制**（那件事在仓库外，见 §17 许可更正）；它只读提取器已经导出的 JSON/GLB 并做坐标/绕序/字段映射。代码可以进仓库，因为它不衍生自 noclip 源码，也不含游戏数据。
- 映射校验（导入器自检，不通过就拒绝）：`524` 实例、`90` 条真父子、`68` 个 AABB、`3,456` 三角形、`7,154` 顶点、3 个有效图形扇区、`7` 个模型。数字对不上说明提取器版本变了，**不猜**。
- 参考导入的 `spawnSource` 必须写 `fieldCentreGuess`，`notes` 保留 `src_y`。

---

## 12. 容量与预算（511 / 130 / 4000）

### 12.1 电机的既有容量（v1，作为基线）

| 资源 | 容量 | v2 后 |
|---|---|---|
| 平面 | 8 | 不变（活动区主边界 + 兜底） |
| 静态盒 | 8 | 不变（structures 的 `role=box`，接触 v2 后并入 256） |
| 斜面 | 4 | 不变（`role=ramp`；接触 v2 后并入 256） |
| 静态 OBB 总表 | 256 | 不变（structures + activity 墙 + 道具近邻共享） |
| 挡路盒 / 道具支撑 | 6 / 6 | 不变 |
| 接触 | 4（v1）/ 8（v2） | 地形最多占 2（v1）/ 4（v2），剩余给静态/道具；超了按距离截断并计数 |
| 地形 | **新增** | 32 chunk / 128 broad box / 每步 48 三角形窄相；地形接触计入上面的总接触预算（v1 2 / v2 4） |

### 12.2 规模表

| 关卡 | 件数 | 地形三角形 | 侧车大小 | 建议 chunk | `PickupFieldHost` 分片 |
|---|---:|---:|---:|---:|---|
| `story_01_bench_size` | 130 | 0（平地） | 0 | 0 | 1（容量 512） |
| `_ref_cancer` | 511 | 3,456 / 7,154 顶点 | ≈113 KB | 7（每块 ≤512） | 1（容量 512） |
| 未来大关 | 4,000 | ≤8,192 | ≤250 KB | 16 | **8**（每片 512） |

### 12.3 4000 件怎么装下

- `PickupFieldHost` 的并行数组按 **512 行/宿主**切分，最多 8 个宿主；`itemId`→`(host,row)` 的映射表由导入器生成，存在 `PickupFieldRouter`（512×8 的表，16 KB）。
- **父指针是关卡全局的**：`PropGroupHost` 持 `int[] propParent`（4000 × 4 B = 16 KB）+ 待复检队列（4000 × 4 B + 4000 bool）+ 状态/速度数组（各 16 KB）；与 `PickupFieldHost` 的三态只在吸收事件同步（PROP_BEHAVIOURS §10.9）。
- **空间分桶**：4000 件每步全扫不可接受；`PropSpatialHost` 用 4 m XZ 网格（CSR，`int[] cellStart` + `int[] cellProps`），每步查 9 格。511 件以下沿用现有近邻选择，不需要分桶。
- 这三件（router/group/spatial）是 **P4 的交付**，不是 v2 schema 的前置；v2 数据本身在第 130/511 档就已完整可用。

---

## 13. 引擎计划：自写运动学电机里的地形网格接触

### 13.1 分阶段

| 阶段 | 做什么 | 不做什么 | 前置 | 退出判据 |
|---|---|---|---|---|
| **T0 支撑-only** | `TerrainFieldHost` 读数组；每步在 broad box 内找「球底以下、且不高于球底 + 爬升余量」的最高三角形；把 `(n, offset)` 喂给现有 `GroundContactMath`；面 id = box id | 不做头顶/侧面、不做速度响应、**不挡高于 stepLimit 的台沿**（那要等 T1） | P2 数据导入 | C21「站得住、落到 `y = 面高 + r`」；C24「沿路缘走 10 s 不抖」；地形上 10 s 支撑高度波动 < 1 mm。**阻挡类判据一律留给 T1/T2** |
| **T1 穿透解算** | 对全部三角形做球-三角形最近点；沿法线推出、去法向速度；2 子步；天花板向下推 | 不做 θ 分类、不做爬升 | T0 | C23 隧道/悬垂：从下面过不被顶起、头顶不穿、站在悬垂上能掉下来；C22 沟底不穿 |
| **T2 分类 + 连续** | θ 分类（ride ≤45°；climb θ>45° 且法线朝上，带速度门；block 法线不朝上）；swept sphere vs triangle 防隧穿；地形接触并入 CONTACT_MODEL_V2 的求解（flag 打开） | 不做材质摩擦/音效 | T1 + 接触 v2 S3/S5 | 4 档球径矩阵：C21–C24 全绿；2.2 m 球不把 0.6 m 台沿当墙；4 m/s 不穿 0.1 m 薄板 |
| **T3 网格 + 流式** | 导入期烘 CSR 网格；chunk 按球位置启用/禁用；>8,192 三角形拆多个关卡/区域 | 不做 LOD 网格 | T2 | 3,456 三角形参考地形每步 ≤48 三角形窄相；chunk 切换无单帧跳变 |
| **T4 打磨** | 材质 id（坡度/摩擦提示）、诊断计数、成本读数进 `Docs/Evidence/` | 不改玩法 | T3 | profiler 读数与离线模型误差 <20% |

**为什么 T0 先做**：它零改动接进现有电机（`GroundContactMath` 只要 `(n, offset)`），能最快验证「地形数据是通的、面 id 稳、球不抖」；头顶/墙的完整解算要等 T1，因为那需要新的接触结果数组与推出顺序。

### 13.2 与 v1 规则的冲突处理

| v1 规则 | 地形出现后 | 处理 |
|---|---|---|
| 地形不进水平扫掠 | 陡三角形必须挡球 | T0/T1 只做推出（不把地形塞进 `SweepMath`）；T2 用接触求解器统一处理，**仍不进扫掠** |
| 支撑只取球心附近最高面 | 悬垂下会把天花板当地板 | T0 起改成「球底以下」；T1 起头顶单独推出 |
| `stepLimit = 0.25D` 决定台阶是墙还是路 | 三角形没有「盒」可标掩码 | T0 保留 `stepLimit` 只做筛选（诊断）；T2 用几何 `h<r` 滚越 / `h<D` 爬升，不再用 `stepLimit` |
| 面 id 稳定 | 三角形每帧不同 | 面 id = broad box；同一框内三角形共享 |

### 13.3 每步成本模型（基线：8 平面 / 8 盒 / 4 坡 / 256 OBB）

粗略 flop 口径，用于排序而不是承诺；真正读数要 Unity profiler（Udon 解释器开销和数组访问次数才是主项）。**地形不占 8 平面 / 8 盒 / 4 坡 / 256 OBB 的任何槽位**（它有自己的表），只在接触总数（v1 4 / v2 8）里占 2 / 4；所以这四张表的既有预算不被侵蚀，代价全部落在查询循环与数组读取上。

| 项 | 次数/步 | flops/步（估） | 说明 |
|---|---:|---:|---|
| v1 基线（1 地面 + 8 平面 + 8 盒投影 + 4 坡 + 6 道具支撑） | — | **≈1.6k** | 上一版估算，未实测 |
| 地形 broad AABB（以 Cancer 的 68 条为上限示例） | 68 | ≈1.4k | 球-AABB：3 减法 + 3 比较 + 早退 |
| 地形 chunk 粗筛（≤32） | ≤32 | ≈0.3k | 只在跨 chunk 时 |
| 地形窄相（T0，无网格） | 2–6 框 × ~51 三角 | **9k–28k** | 有 y 带早退；这是无网格时的上限 |
| 地形窄相（T3，1 m 网格 3×3） | 2–6 框 × 9 格 × 2–4 三角 | **≈2k–6k** | 球心 3×3 格；三角形去重后更少 |
| 球-三角形最近点 | 每个候选 | ≈90 | 面/棱/角三分支 |
| 穿透推出 + 去速 | ≤4 接触 | ≈0.4k | 与现有接触同一量级 |
| 接触 v2 现状（24 近邻 OBB，≤8 接触） | — | ≈4k | 对照：地形 T3 与它同量级 |

**结论**：T0 无网格在 3,456 三角形上可能到 3 万 flops/步（50 Hz ≈ 1.5 Mflops/s），对 Udon 偏高但未必不可；**网格把窄相压到 2–6k，是必做项**，不是优化。流式 chunk 的切换成本（每 0.5 m 或 0.25 s 一次）应摊到 **<100 flops/步**。

### 13.4 新测试场格位（PhysicsTestField）

现有 21 个规划格只到 C20，9 格已落地。新增 4 格，沿用「一格一 `placement.json` + `physics_test_field.json` 判据」的机制：

| 格 | 名称 | 几何 | 判据（D = 0.30/0.60/1.10/2.20） |
|---|---|---|---|
| **C21** | `stairs` | 4 级台阶：0.10 / 0.20 / 0.35 / 0.60 m，踏面 0.4 m，宽 3 m；末端 0.8 m 平台 | `support_gap ≤ 5 mm`；`max_single_frame_rise ≤ 0.05 m`；不穿模；**T0 只判站得住**，T1/T2 再判：0.30 球被 0.10 m 台阶挡（h > 0.25D）、1.10 球滚上 0.20 m（h < r）、2.20 球滚上 0.60 m |
| **C22** | `gully` | 6×3 m 沟，深 1.0 m；两侧 30° 土坡；南侧 0.6 m 竖直护岸；沟底 2 个薄片 | 球从北坡下到 −1.0 m（`centre.y = −1.0 + r ± 1 cm`）；30° 坡可滚下不可滑（切向重力默认 0）；0.6 m 护岸在 D ≤ 1.2 时挡、D=2.2 时爬越；沟底不穿、不抖 |
| **C23** | `tunnel_overhang` | 3 m 宽 × 1.2 m 高隧道穿过 1.5 m 厚土堤；堤顶可站；旁挂 2 m 长悬垂檐（底面 0.8 m） | 球从隧道下过：不被顶起（`centre.y` 变化 < 2 cm）；头顶不穿（`max_penetration ≤ 0`）；站上堤顶 `y = 1.2 + r`；从檐边掉下时 `fall_frames > 0` 且落点 = `r`；面 id 在顶/底切换时 `support_id` 变化，不触发传送 |
| **C24** | `curb` | 0.15 m 路缘 + 0.15 m 宽排水沟 + 2 cm 磨圆唇口；一段 0.08 m 高的盲道砖 | 沿路缘走 10 s 不抖（`support_id` 稳定）、落到 `r`；T1/T2 再判：0.30 球被 0.15 m 路缘挡（h = 0.5D > 0.25D）、0.60 球滚上去（h < r）、2 cm 唇口不产生「隐形墙」（`blocked=false`） |

**共同要求**：每格 4 个球径档、判据可自动判定、失败要有具体读数（`support_gap_m`/`penetration_m`/`max_single_frame_rise_m`/`blocked`），并进 `Docs/Evidence/PhysicsTestField/`。四格要同时进 `physics_test_field.json` 的 `cells`/`planned_cells`（几何、激活矩形、判据、驱动时间线各一份），并沿用现有跨文件一致性测试；只写 `placement.json` 不算落地。**C23 是「地形必须三角网」的活证据**：高度场做不出隧道。

### 13.5 现有 ramp / curve_ramp / box 怎么办

| 今天 | v2 落点 | 迁移还是保留 | 理由 |
|---|---|---|---|
| `role=ground` 平面 | `meta.groundY` 兜底 | **迁移为兜底，不生成地形** | 平地不需要 3,456 三角形；地形是**额外**的，不是替代兜底 |
| `role=box` | `structures.solids[role=box]` | **保留 OBB** | 建筑/护岸用解析体更便宜、更稳；不把盒子烘进地形（会改语义、吃三角预算） |
| `role=ramp` | `structures.solids[role=ramp]` | **保留**；接触 v2 后成为 slab | 现有 45° 门、坡脚无隐形墙、P3 测试都建立在它上面 |
| `role=curve_ramp` | 同上 + `curve` 块 | **保留**（展开 N 段） | 已有唯一 tessellator 与测试；改成地形要重做 |
| `role=boundary` | `activityArea.walls` | **迁移**（矩形无损；否则保留 + warning） | 边界是规则，不是几何 |
| 未来的大坡/滑梯/碗 | `terrain` | 新增 | 解析体表达不了曲面；这时才用网格 |

**一句话**：**能解析体表达的就不进网格**；地形只用来表达「解析体拼不出来」的 3D 地面。这样 256 OBB 预算、现有测试与作者手感都不被推翻。

---

## 14. 编辑器升级计划（`Tools/leveleditor`）

### 14.1 现状与缺口

| 已有 | 缺口 |
|---|---|
| three.js 视图、点选/框选、移动/旋转 gizmo（yaw + 支撑重坐）、行走、球径阶梯、区域统计、校验面板、参考关卡只读 + 父子连线 | 地形网格查看、碰撞 AABB/三角形叠加、真四元数旋转、`parent`/`support` 连线与派生预览、`volumeK`/`volumeMm3` 编辑（进行中）、活动区 gizmo、错误码面板 |

### 14.2 计划（S ≤ 半天，M 1–3 天，L >3 天）

| # | 事项 | 做什么 | 工作量 | 依赖 | 顺序 |
|---|---|---|---|---|---|
| E1 | `volumeK` / `volumeMm3` 编辑 | 右侧面板显示 `k`、`volumeMm3`、`Req mm`，改 `k` 即时重算并校验；已有实现在途 | **S**（在途） | v2 派生字段 + 校验器 | 0（已开始） |
| E2 | 稳定错误码面板 | 校验返回 `{code,path,message}`；面板按 tag 分组、点条目定位对象；未知键错误直接显示建议 | **S–M** | §9 代码表落地 | 1 |
| E3 | 真四元数旋转 gizmo | 旋转不再只绕 Y；用 three.js `TransformControls` 的 quaternion 模式；写回 `rotationQuat` 并做规范符号；支撑重坐保留（按新姿态沿 Y 落） | **M** | 导入器/校验器按 OBB 判互穿（不能再用世界 AABB） | 2 |
| E4 | 父指针 + 派生支撑预览 | 画 `parent` 连线（与 `support`/`stackedOn` 不同颜色）；按钮「派生支撑」用 `placement_core` 几何跑一遍 `derive_supports`，结果只预览不写；支持一键采纳 | **M** | E3（OBB 姿态）+ `parent` 字段 | 3 |
| E5 | 地形网格查看 | 读内联数组或 `.katerrain`；three.js `BufferGeometry` 渲染；按 `materialSegments` 分色；大网格分块加载 | **L** | §8.3 侧车格式 + `Tools/levelbuild` 读取器 | 4 |
| E6 | 碰撞叠加 | 开关：broad-phase AABB 线框、三角形按 `classes` 着色（ride/climb/wall）、zone 颜色；点三角形显示 zone/box | **M** | E5 | 5 |
| E7 | 活动区 gizmo | 在地面画多边形/折线；每段设 `minBallDiameterM` 滑条（球径阶梯联动显示「此刻哪些墙生效」）；kill plane 水平面 + 拖动 | **M** | §5 校验器 + 电机墙注册 | 6 |
| E8 | 4000 件浏览 | 虚拟化道具列表、按区域/kind 过滤、分片指示 | **M** | §12.3 分片方案 | 7（可与 E5–E7 并行） |

**顺序理由**：E1/E2 是「数据正确」的地基，先做；E3 改变旋转语义，必须在 E4/E5 之前（否则连线/碰撞叠加都建在 yaw-only 的位姿上）；E5 依赖侧车格式冻结；E7 放在 E6 后，因为墙的门槛与球径阶梯共用同一套可视逻辑。

### 14.3 参考关卡的编辑器路径

- 现有 `--level _ref_cancer --ref-models-dir ~/KatamariRef/models` 机制保留：只读、红黑横幅、父子连线（`group`/`groupId`）。
- v2 参考关卡（`placement.model.json`）额外支持：地形侧车（只读、分块）、碰撞叠加、`parent` 连线（90 条真父子）、`rotationQuat` 真实朝向（401 件）。
- **只读守卫不变**：`add_prop`/`set`/`move`/`remove`/`export`/`undo`/`redo`/`batch` 全部拒绝；文件一个字节都不写；模型响应带 `X-Unlicensed-Reference: 1`。

---

## 15. 分阶段迁移

| 阶段 | 内容 | 交付 | 门（不过不进下一阶段） |
|---|---|---|---|
| **P0 文档与 schema 冻结** | 本文、`*_KEYS` 草案、错误码表、owner 决策清单 | 本文 + 评审 | owner 对 §16 的 D-2/D-4/D-5 给方向；不再改字段名 |
| **P1 校验器 + 转换器** | `placement_core` v2 读写、13 份转换、`check` 0 错、`gate_changes.csv` | 工具 + 测试 | 13 份转换；v2 再转 no-op；`python3 -m unittest discover -s Docs/tests` 全绿 |
| **P2 Unity 导入（无地形）** | `PlacementImporter` v2：meta/structures/activity/props；地形字段读入但不建网格 | 导入器 + 场景 | 13 份重导入第二次 `+0 ~0 -0`；`volumeMm3`/`Req mm` 回读一致 |
| **P3 地形数据与视觉** | 侧车读写、`TerrainFieldHost`/`TerrainChunkHost`、Mesh 资产生成、T0 支撑 | 数据 + 组件 | C21/C24 通过；地形上站得住；参考地形 3,456 三角形加载成功 |
| **P4 地形接触 T1/T2** | 穿透/天花板、θ 分类、连续检测；C22/C23 | 电机地形层 | C21–C24 四档全绿；4 m/s 不穿 0.1 m 板 |
| **P5 行为与规模** | `parent`/`rigidGroup`/`scale` 运行时；子件坠落；4000 分片与空间桶 | 宿主组件 | 90 条父子关系在 Cancer 参考关卡跑通；4000 件合成场景不掉帧目标（先测再定） |
| **P6 编辑器** | E2–E8 | 编辑器 | 各功能有截图/冒烟；参考关卡只读不变 |
| **P7 收尾** | 删 v1 读取器；`OPEN_DECISIONS` 状态更新；证据归档 | 清理 | 仓库无 v1 写入路径；全部测试绿 |

**并行约束**：P1 不能与 P2 合并（先证明 JSON 形状稳定，再动 Unity）；P3 的 T0 不能等 P4（支撑-only 是低风险收益）；P5 的 `parent` 运行时必须等 `PROP_KEYS_V2` 与校验器同一批（未知键硬错误）。

---

## 16. Owner 决策清单（需要拍板）

> 已定、不再问的输入：地形支持 3D 表面（三角网 + 空间加速，高度场只作优化）、解析器照规格重实现在仓库外、吸收门按原作 `k × bbox`、`volumeMm3` 定点、父件被吃子件坠落（单父指针）、每件四元数、碰撞代理按型。下面只列**研究回答不了的产品/工程取舍**。

| # | 决定 | 选项 | 建议 | 影响面 |
|---|---|---|---|---|
| **D-1** | 全局球径上限的落点 | A 单一全局资产（已定方向）；B 每关可覆盖 | **A**，`meta.ball.maxDiameterRef:"global"`；实验关才允许 `"level"` 并打 warning | `meta`、电机、编辑器 |
| **D-2** | 地形存哪 | A 全内联；B >512 三角形走 `.katerrain` 侧车 + sha256 | **B**：内联保可读，大网格走二进制并钉哈希；参考关卡也用同一格式 | JSON/侧车、编辑器、导入器 |
| **D-3** | 地形要不要水平阻挡 | A 只支撑；B 支撑 + 陡面挡（θ 分类） | **B**，但**分阶段**：T0/T1 只做支撑/推出/去法向速度，T2 才做滚/爬分类与爬升；在 T2 前用 `structures` slab 补关键阻挡 | 电机、测试场、关卡设计 |
| **D-4** | 活动区越界后果 | A 硬墙；B 硬墙 + 软边距（减速/提示）；C 只提示 | **A + kill plane**：没有原作软边距证据，先做硬墙 + 球径门 + 掉出重生；软边距留给试玩（HUD 是另一个模块） | `activityArea`、电机、HUD |
| **D-5** | 逐件缩放策略 | A 视觉-only；B 全量参与几何/体积；C 只允许均匀 | **C + 例外**：均匀全允许；非均匀只允许无父、static、box/sphere；挂接件永不缩放。原作仅 1 件非均匀且语义未验证 | props、导入器、吸收门 |
| **D-6** | zone id 的用法 | A 不透明标签；B 现在映射成玩法区（8=void 等） | **A**：只做标签/统计，kill plane 单独字段；等有规则证据再映射 | 地形、活动区、统计 |
| **D-7** | 现有 ramp/curve_ramp/box 是否迁进地形 | A 保留解析体；B 全烘进网格 | **A**：能解析体表达的就不进网格；只有曲面/悬垂才用地形 | 引擎、数据、编辑器 |
| **D-8** | 参考关卡数据保留 | A 只留 `~/KatamariRef`；B 允许进仓库 | **A**（现行规则）：导入器只写 `_ref_*` 与 `_RefUnlicensed`；任何网格/贴图/BIN 永不入库 | 导入器、git 守卫 |
| **D-9** | 仓库许可（与 noclip 的 MIT 无关，见 §17） | A 暂不决定；B 现在选 MIT/Apache；C 现在选 copyleft（GPL 兼容） | **A**（LICENSING 未定）：不 vendor 第三方代码；若将来移植 MIT 代码，保留版权与许可声明；首选「照规格重实现」 | 全仓库 |

### 16.1 未验证项（不因本文定稿而变成已验证）

| # | 未验证 | 为什么必须验 | 验法 |
|---|---|---|---|
| **U1** | Udon 行为之间传数组（`Vector3[]`/`byte[]`）、`byte[]` 序列化、`long`/`long[]` | 地形查询与 `volumeMm3` 都依赖它；U# 没有 List/jagged array | Unity 里各写一个最小 `UdonSharpBehaviour` 试探，`refresh_unity` 看编译 + Play Mode 读回；验完删掉试探物 |
| **U2** | 生成 Mesh 资产的确定性、GUID 稳定性 | 重新导入不能换引用，否则场景丢网格 | 同一 JSON 生成两次比 sha；改一条顶点再看 GUID 是否变 |
| **U3** | `TerrainFieldHost` 每步成本 | §13.3 全是 flop 估算；Udon 解释器开销可能高一个量级 | profiler + `GetLastStepMicros()` 读数进 `Docs/Evidence/` |
| **U4** | 校验器的 OBB 互穿、`scale` 作用到 collider 后的代理包围 | 现在校验器用世界 AABB 粗筛；旋转/缩放接进来后必须换 OBB，否则误报会变多（参考关卡今天 26 个 AABB 互穿） | `Docs/tests/levelbuild` 加 OBB 用例；参考关卡跑一遍看报错是否收敛 |


---

## 17. 许可与来源更正（noclip 是 MIT，不是 GPL-3）

| 事实 | 依据 |
|---|---|
| noclip.website 的 `LICENSE` 是 **MIT 正文 + 一段反编译说明**（"部分实现是逆向的、非净室"）；`package.json` 写 `"license": "MIT"`；GitHub 因那段说明报 `NOASSERTION` | [NOCLIP_KATAMARI_FORMAT §10](../../Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)、`LICENSE` @ pinned commit |
| **「noclip 是 GPL-3」的旧前提不成立** | 同上；本项目 2026-11-16 更正 |
| 即便如此，逐行翻译仍是对该作品的**衍生作品**；MIT 允许再分发，但必须保留版权与许可声明 | 常规许可法理【推断，但确定】 |
| 我们的路线：**照规格重实现**（已做，解析器在 `~/KatamariRef/tools/katamari_stage_extract/`，981 行，仓库外）；仓库只放规格 | NOCLIP §0/§9/§10 |
| 仓库许可仍未定；解析器进仓库的前提是 owner 选定许可（MIT/Apache/GPL 兼容都可以，只要不搬 noclip 源码/逐行翻译） | [OPEN_DECISIONS](../../OPEN_DECISIONS.md) |

**本轮同步更正的地方**：`AGENTS.md` 的旧规则（把 noclip 列为 GPL 工具）改为「派生自第三方代码的解析器在 owner 选定仓库许可前留在仓库外；移植/衍生必须保留原版权与许可声明（MIT 允许）；首选照规格重实现」；`OPEN_DECISIONS.md` 的 noclip 条与「Still open 仓库许可」同步改写；本文件旧稿中「noclip 是 GPL-3」的断言全部替换为上面的更正，仍保留「不是 GPL-3」这句本身作为纠正记录。**其余许可判断（素材 CC-BY-SA/GPL 不混用等）不受影响，保持原文。**

## 18. 变更记录

| 日期 | 变更 | 说明 |
|---|---|---|
| 2026-11-16 | 建立骨架 | 需求/约束、研究问题、引擎事实、NOW/WAIT；schema 标 `DRAFT_BLOCKED_ON_RESEARCH` |
| 2026-11-16 | **本版：最终提案** | 两份研究落地后按研究结论改写：五层 schema 定稿、terrain/structures/activityArea/props 字段、两个 JSON 例子、规范形与侧车、错误码、v1→v2 转换器、Unity 映射、容量预算、地形接触分阶段计划与 4 个新测试格、编辑器升级、迁移阶段、owner 决策；许可前提更正为 MIT（非 GPL-3） |
