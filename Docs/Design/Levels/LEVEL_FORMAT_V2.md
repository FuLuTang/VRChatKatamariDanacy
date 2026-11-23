# 关卡数据格式 v2（最终提案）

> 状态：**`FINAL_PROPOSAL`**。触发：owner 2026-11-16「整体升级地图数据结构 + 网页编辑器一起升」；研究先于设计，四份研究均已落地：
> [ORIGINAL_TERRAIN_AND_LAYOUT](../../Research/KatamariBallRules/ORIGINAL_TERRAIN_AND_LAYOUT.md)（REROLL 导出表能推出什么）、
> [NOCLIP_KATAMARI_FORMAT](../../Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)（PS2 舞台包/任务包的字节布局）与
> [ORIGINAL_ANIMATION_OBSERVATIONS](../../Research/KatamariBallRules/ORIGINAL_ANIMATION_OBSERVATIONS.md)（逐帧观察：多少东西在动、怎么动、预算多少）与
> [ORIGINAL_MOTION_PROGRAM](../../Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md)（13 类运动：参数来源、周期、触发半径、幅度换算与每帧成本）。
> **本文是设计定稿，不是 owner 决定清单**：字段名、代码与本项目的写入方式是提案；凡 owner 已拍板的，逐条标「已定」。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…`、`Tests/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；其余相对仓库根。**按符号名与节号引用，不写行号**。
>
> 兄弟文档：[LEVEL_AUTHORING](../Tools/LEVEL_AUTHORING.md)（当前格式与工具）、[LEVEL_EDITOR_WEB](../Tools/LEVEL_EDITOR_WEB.md)（网页编辑器）、[CONTACT_MODEL_V2](../BallMotor/CONTACT_MODEL_V2.md)（接触求解器）、[PHYSICS_TEST_FIELD](../BallMotor/PHYSICS_TEST_FIELD.md)、[COLLIDER_PROXY](../PickupAbsorb/COLLIDER_PROXY.md)、[PROP_BEHAVIOURS §10](../PickupAbsorb/PROP_BEHAVIOURS.md)、[PARITY_AUDIT](../Parity/PARITY_AUDIT.md)、[OPEN_DECISIONS](../../OPEN_DECISIONS.md)。

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
| **Q-M1** 原作的动件有多少、怎么动 | Make Cancer 524 个实例里 **129 个（24.6%）有运动程序**、**153 个（29.2%）有骨骼动画，只来自 19 个类型**；13 类运动（SporadicWalk 31、Hop 18、Sway 15、DraggedAlong 15、WaitForPlayer 15、WhackAMole 8、PathCollision 8、Spin 7、Clouds 5、Flip 2、Bob 2、SlopingPath 2、BackAndForth 1）；**1 速度单位 = 30.3 cm/s**；Sway 周期 ≈1.5 s、Bob ≈2.8 s、Hop 5–6 cm + 3.4–5 s 驻留、WhackAMole 首次下沉约 0.7 s、小蟹行程 12.4 cm；**43/90 条父子是静止子件随动父件**；noclip 不渲染舞台动画，5 朵云是 `moveType 16` 的道具。【实测】 | 新增 `motions[]`/`paths[]` 逐关表 + `props[].motion` 引用 + `defs[].speedIndex/clips` + `props[].phase/clipStart/inherit`；Udon 10–15 Hz 分档、共享剪辑 + 8 相位桶（§6.7）；**153 个动画实例照原作挂 Animator，不做 VAT**（2026-11-16 拍板，§16.2） |
| **Q-N6** 许可边界 | noclip 仓库的 LICENSE 是 **MIT + 一段反编译说明**（GitHub 因该说明报 `NOASSERTION`），**不是 GPL-3**。原假设有误。 | 见 §19 与 AGENTS/OPEN_DECISIONS 的更正；解析器仍是「照规格重实现」，仓库许可仍待 owner |

**仍未解决、本文不假装解决的**：缩放字段的语义（那 1 件可能是贴花尺寸/碰撞盒尺寸）；zone id 的**玩法含义**（只有「逐三角形带标签」这一个事实）；7 个任务级体积的确切作用（其中第一个是 y=−1,691.5 cm 的巨大平面，像掉出判定面）；**逐实例相位从哪来**（原作可能是种子，noclip 用 `Math.random()`，我们暂用确定性哈希）、`locPosType` 是否表示随机摆放、`shakeOffFlag` 是否等于「可甩脱」、舞台模型有没有动画（noclip 没做，证不了）；REROLL 523 与 PS2 524 差的那 1 件。

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
| `structures` | 建筑、护岸、斜板这类**大件不可吸物**的解析体（OBB/slab/弯坡） | 是 | 否 | 电机静态 OBB 表 | `role=box/ramp/curve_ramp` |
| `activityArea` | 球能去哪、多大才能去、掉出去怎么办 | 是 | 否 | `ActivityAreaHost` | 四面 `boundary` 平面 |
| `props` | 每件可吸物：是什么、多大、坐哪、朝哪、值多少、怎么动、挂在谁下面 | 否（可动） | 是 | `PickupFieldHost` + 行为宿主 + 账本 | `props[]` + `defs[]` |

`defs[]`、`motions[]`、`paths[]`、`cameras`、`renderPresets`、`regions` 仍是旁挂表，不属五层之一；`motions`/`paths` 是逐关表、不是逐实例数据（§6.7）。

### 1.3 三条硬边界（引擎侧已经定死，研究改不了）

1. **地形与结构都走同一套接触求解。** **陡峭三角形**会挡球，那是接触求解器（CONTACT_MODEL_V2 的 θ 分类）的职责；需要水平阻挡的人造结构用 `structures` 的 slab 表达。
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
           "staticSolids": 256, "activityWalls": 64, "movingProps": 0, "animators": 0},
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
| 球径上限 | 全局 400 m | 不写入关卡 | `KatamariBallMotor.GlobalMaxBallDiameterM` 是唯一来源，所有地图共用（`Docs/OPEN_DECISIONS.md` (n)，owner 2026-11-18）。现有 JSON 中的 `ball.maxDiameterRef`、`ball.maxDiameterM` 与 `migration.maxDiameterM` 可保留但不作为配置读取（`migration` 的只是溯源）。 |
| 本关可达球径 | 派生 | 不写入 | 分析用的「本关最大球径」，不是上限、也从不配置：`placement_core.reachable_ball_diameter_m` 用 `ball.startDiameterM` 的体积加每个 `absorbable:true` 件的成长量算等效球径，再夹到全局上限。墙高、可吸性提示、弯坡弦预算按它判断——拿全局 400 m 当尺子只会让每面墙、每个道具都「够不着」。 |
| `ball.start` | 3×number | 是 | 球心出生点（世界米） |
| `ball.spawnSource` | `"authored"` \| `"fieldCentreGuess"` \| `"reference"` | 是 | **推定值必须标出来**。`authored` = 点是在本关真实地形/道具/结构上量出来的可玩点（可达性模型选点：起始吸不动的道具当障碍，按 3 m 路程内可吸件数排序；参考关卡 `_ref_cancer` 就是），依据写进 `arena.notes`；`fieldCentreGuess` = 没有测量、只能取场心。原作出生点本身不可知，见 ORIGINAL_TERRAIN §2.4 |
| `timeLimitS` | number | 剧情关必填 | 时限（秒）；`0` 表示不限时（实验/测试关） |
| `playerSpawn` | 3×number | 是 | 玩家出生点（作者/编辑器视点）；与球出生点分开。参考关卡为球上方 0.4 m。**不是**描述符 spawn，描述符 spawn 见 `playerStart` |
| `playerStart` | object | 否 | **描述符 spawn**：`position`（3×number，**脚底**世界坐标）、`facingDeg`（偏航角，度，−360..360，heading 0 = +Z，正向朝 +X，即 `Quaternion.Euler(0, facingDeg, 0)`）、`source`（可选说明字符串）。参考关卡的值按我们的规则算（沿球初始朝向反方向 2.5 m、落在地形面上、朝向面向球，非原作），转换器从 `AUTHORED_SPAWNS` 写出，见 ORIGINAL_TERRAIN §2.4。校验器 `[meta_player_start]`：必须是对象、三键之外的键报错、`position` 3 个数、`facingDeg` 在范围内、脚底 y 高于 `activityArea.killPlane.y`。**导入器行为**：有它时 `Markers/PlayerSpawn` 取其位置和朝向，场景里的 `VRC_SceneDescriptor.spawns` 设为 `[Markers/PlayerSpawn]`，`RespawnHeightY = killPlane.y − 1 m`（球的坠落线先触发、玩家随后；校验保证 kill plane 低于 `groundY` 与地形最低点，所以一米之下永远低于任何可站立面。Cancer：kill −4、地形最低 −1.59 ⇒ −5），并在 `TerrainRoot/TerrainVisual` 上保证一个非 convex、Default 层的 `MeshCollider`（仅玩家站立；球不走 PhysX，Runtime 里没有任何 Physics 查询）；同时把球对齐到 `ball.start`：`KatamariBallMotor.startCenterWorld`（经代理 `SerializedObject` 写，再 `CopyProxyToUdon`，不销毁 UdonBehaviour）与 `MotorHost` 的世界位置（1 mm 容差，二次导入不写；场景里没有电机只记 note）。数据里没有球的初始朝向、电机也没有可序列化的起始朝向（从 +Z 起），所以只对齐位置，`MotorHost` 的旋转不动。描述符与球的写入计入导入 headline 的 `other=`。没有 `playerStart` 则保持旧行为：只建 `Markers/PlayerSpawn` 于 `playerSpawn`（identity 朝向），不碰描述符、地形碰撞体与球 |
| `groundY` | number | 否 | **兜底平面高度**：没有 `terrain` 时它就是地面；有 `terrain` 时它必须**低于地形最低点**（否则兜底平面会顶穿地形），校验器 `[meta_ground_y]` 检查。参考关卡的真实地面主带是 −1.0（§0），但碰撞 AABB 的 y 下限到 −13.85 m，所以兜底取 −20，不是 0 |
| `bounds` | `[[minX,minZ],[maxX,maxZ]]` | 是 | 轴对齐**作者框**：用来查道具越界、给编辑器画框，**不是**活动范围（活动范围见 §5） |
| `spawnClearanceM` | number | 是 | 出生点净空半径 |
| `zones` | array | 否 | 命名区：`id`（字符串，作者用）+ `code`（0–255 整数，地形三角形引用）+ `kind`（`ground`/`room`/`void`/`other`，仅提示） |
| `arena` | object | 是 | `{id, notes}`；导入器据此建 `LevelArenaHost` |
| `caps` | object | 是 | 数据规模上限（§12）；`movingProps`/`animators` 缺省 0；转换器按文件实际值补齐 |
| `budget` | object | 是 | 电机 v1 注册预算（沿用今天五个键）。它约束 `structures`，**不约束 `terrain`**（地形有自己的表）；接触 v2 打开后 `boxes`/`ramps` 并入 256 OBB 表 |
| `expected` | object | 是 | 设计门：件数、价值单位、三角数；`levelbuild check` 对实际值 |
| `source` | object | 是 | `kind` ∈ `authored`/`reference`/`imported`；参考关卡的 `kind=reference` 会让导入器拒绝写进受管根（§11.3） |
| `migration` | object | 否 | 只在 v1→v2 转换产物上出现，记录源 schema 与源数据；v2 新文件不得手写 |

**与旧版的字段改名**：`ballStart`→`ball.start`、`ballDiameterStart`→`ball.startDiameterM`、`level`→`meta`。旧键在 v2 里是**未知键**，硬错误（未知键不得静默丢弃，见 OPEN_DECISIONS 的 `parent` 教训）。

---

### 物件的活动区域号

`meta.areaNo` 可选，是本关活动区域号（非负 32 位整数），不是 `zones[].code`。`structures.solids[]` 与 `props[]` 可写 `hitOnAreaNo`（`-1` 或非负 32 位整数，缺省 `-1`）。仅 `-1` 或与 `meta.areaNo` 相同的实例参与球碰撞；未声明关卡区域时只有 `-1` 通过。导入时关闭异区 solid 的球接触注册（包括子盒和斜坡支撑）、关闭异区 prop 的球挡路和支撑资格。渲染、玩家碰撞与可吸性不变，运行时不读 JSON。同一实例兼有 solid 与 prop 行时，两行应填同一原作值。

蟹座参考生成器按 `our_itemId` 连接初代 REROLL（PC 2019.01.31）的逐实例表，保留 `s8MonoHitOnAreaNo` 原值。`meta.areaNo = 2` 来自任务 11 的 `START_STAGE` 加开局 `playArea = 0`；当前导入固定该区域，不做运行时切区。没有 placement 对应行的 DummyProps 不生成。

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
| `materialSegments` | array | 是 | **接触网格**的材质段：`firstIndex`/`indexCount` 必须首尾相接覆盖全部索引；`material` 是 Unity 资源路径。**有 `visual` 时只是碰撞网格的分段，不渲染**（参考关卡一段 `collision`） |
| `visual` | object | 否 | **渲染用的网格 + 贴图**，与上面的接触网格是两套几何（§3.8）。没有它 = 接触网格就是你看到的地面（我们自己的关卡）；参考关卡必须有，否则只能把原作的碰撞网格（含 15 m 不可见挡球墙）画出来 |
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

### 3.8 `terrain.visual`：原作的视觉网格（与接触网格分家）

**为什么是两套几何。** 原作的关卡模型包有互相独立的两张表：4 个**图形扇区**（有 UV、材质、贴图）和 4 个**碰撞扇区**（只有 AABB + 三角形，无 UV、无材质）。碰撞表里含一圈 15 m 高、朝内、只用来挡球的不可见围墙；图形表在院内最高只到 3.63 m。两者在 5 cm 内大体重合（碰撞三角形重心到视觉网格 ≤5 cm 的占 93.5%），但不是同一份数据。研究见 [REF_STAGE_VIEW §10](../Tools/REF_STAGE_VIEW.md)。我们的接触（球读 `TerrainFieldHost` 数组、玩家站立 `MeshCollider`）照旧用碰撞网格（原作优先：球在原作里同样被那圈墙挡住）；**画面**用 `visual`。

```json
"visual": {
  "meshRef": {"path": "terrain/cancer.katvisual", "sha256": "…",
              "vertexCount": 29846, "indexCount": 44199, "triangleCount": 14733,
              "indexWidth": 2, "normals": true, "uvs": true},
  "parts": [
    {"id": "area2_sector0_model4", "role": "stage",
     "vertexRange": [20000, 23394], "indexRange": [30000, 34566],
     "bounds": {"min": [-22.0889, -1.5918, -13.7620], "max": [8.3719, 3.5774, 10.6734]},
     "submeshes": [{"material": "t000", "firstIndex": 0, "indexCount": 300}]},
    {"id": "area2_sector1_model0", "role": "sky", "…": "…"}
  ],
  "materials": [
    {"id": "t000", "texture": "terrain/textures/11_area2_2f75_33ea.png", "sha256": "…",
     "width": 64, "height": 64, "blend": "opaque"}
  ],
  "filter": "point",
  "source": {"kind": "reference", "windingFix": "flip",
             "provenance": "PS2 stage graphics sectors -> noclip_export/out/stage.glb"}
}
```

（结构示例，数字不闭合；可校验的完整文件由 `Tools/levelbuild/ref_stage_visual.py` 生成。）

| 字段 | 语义 |
|---|---|
| `meshRef` | `.katvisual` 侧车（§8.4）：路径（相对关卡目录，禁 `..`）、sha256、顶点/索引/三角形数、索引宽度、是否带法线/UV。**视觉网格永远走侧车**，不内联 |
| `parts[]` | 一块 = 一个 Unity 网格/渲染器。`vertexRange`/`indexRange` = `[start,count]`，按序首尾相接铺满侧车；**索引是部件局部的**（相对 `vertexRange.start`），所以 29,846 个顶点里每块只带自己那份。`role`：`stage`（舞台几何）/`sky`（天空壳：背景，不参与取景与深度写入）。`bounds` = 该块顶点的 AABB（米，工具写、校验器复算，1 mm 容差） |
| `parts[].submeshes[]` | 一块内按材质分段：`firstIndex` 相对该块 `indexRange`，首尾相接覆盖它；`material` 引用 `materials[].id` |
| `materials[]` | 一张贴图 = 一个材质（原作 232 个材质部件只在贴图上不同，158 张）。`texture` = PNG 的相对路径 + `sha256` + 像素尺寸（校验器读文件头对账）。`blend`：`opaque`（alpha 全 255）、`cutout`（alpha 只有 0/255，丢弃 0）、`alpha`（有中间值，`src_alpha, 1−src_alpha`）——由 PNG 的 alpha 通道推出；GS 状态 232 个部件完全一致（见研究文档），所以混合模式不需要逐材质存 |
| `filter` | `point` / `bilinear`：贴图过滤。原作 GS 的 TEX1 是**放大最近邻、缩小线性、无 mip**，Unity 只能二选一，我们取放大侧的 `point`（游戏镜头多数时候在放大）。**这是判断，不是数据**，需 Unity 里看一眼 |
| `source` | 同 §3.3；`reference` 必须带 `windingFix:"flip"`（原作 noclip 帧 → 我们的帧镜像了 x，绕序翻一次）与 `provenance` |

**约定（每条都实测过）**

- 帧：Unity 世界米，`(x, y, z) = (−0.01·x_导出, 0.01·y_导出, 0.01·z_导出)`（导出 = noclip 帧厘米）。这是 x 镜像，所以**绕序翻一次**（三角形 2、3 号角互换）；顶点法线（提取器写在 PS2 帧）取 `−n`。翻完后 99.5–100% 的三角形与其法线一致，地面朝上，天空壳从里面看正面。
- 绕序在文件里就是 Unity 的：正面 = 数值叉积 `(b−a)×(c−a)` 的方向（地面叉积 +Y）。Unity 导入不再翻；three.js（z 取反的镜像）读取时再互换一次。
- UV 原点是**PNG 左上角**（glTF 约定，导出就是这么写的）；Unity 导入时 `v → 1−v`。贴图重复（wrap = repeat），无 mip，不模糊。
- 背面剔除（原作每个部件都是 `Cull Back`）；`doubleSided` 是导出器自己写的 `true`，不是原作的数据。
- 光照：原作逐顶点用法线 + 关卡光照文件照亮（所有部件 `lit`），我们用场景光 + Legacy Diffuse 系；漫反射色全是白，不写顶点色。

**与接触网格的关系**：互不引用。`terrain.meshRef`/`broadPhase`/`chunks`/`grid`/`triangles` 只描述接触；`visual` 只描述画面。有 `visual` 时 Unity 里接触网格对象**只留 `MeshCollider`、不渲染**（§11.2）。校验器的 tag：`[terrain_visual]`（块形状/`filter`）、`[terrain_visual_key]`（未知键）、`[terrain_visual_sidecar]`（侧车缺失/sha/计数/索引宽度/标志/部件数）、`[terrain_visual_part]`（部件铺满、子网格铺满、局部索引、bounds、退化三角形、role）、`[terrain_visual_material]`（id/`blend`/引用）、`[terrain_visual_texture]`（PNG 缺失/sha/尺寸/路径）、`[terrain_visual_winding]`（绕序与法线对不上，说明整张网格是反的）。

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
| `role` | `box`（静态 OBB：挡路 + 顶面支撑）／`ramp`（斜板，slab OBB）／`curve_ramp`（弧展开成 N 段 slab） |
| `shape` | `box`／`proxy`（**2026-11-16 追加**：接触形状用 `collider` 代理，见下）／`plane`（平面只给 `boundary` 遗留用；v2 新文件不写 `plane`） |
| `collider` | 仅 `shape:"proxy"`：**局部空间**的接触代理，编码与 `defs[].collider` 完全相同（`box`/`sphere`/`capsule`/`convex` 4–24 点），由 `collider_proxy.fit_proxy` 拟合 |
| `proxySource` | 仅 `shape:"proxy"`：`def`（取自该 def 的拟合代理）／`aabb-fallback`（**没有代理时**退回 AABB 盒，必须写出来）／`manual` |
| `parts` | 仅 `shape:"proxy"`，可选：2–4 个 `{"center":[x,y,z],"size":[x,y,z]}`，与 `collider` 同一**局部帧**（`center`+`rotationQuat`），米。写了就由这几个盒代替「代理的一个盒」进电机静态 OBB 表；不写 = 单盒（缺省）。每个盒不得越出 `size` 盒（容差 1 mm） |
| `collision` | `box`／`none`；`none` 表示纯视觉标记 |
| `support` | 是否可当支撑面（`collision:"none"` 必须 `support:false`）。**`shape:"proxy"` 时支撑面仍取 `size` 盒的顶面**，见下 |
| `curve` | 仅 `curve_ramp`：`radius_m`/`arc_deg`/`segments`/`bend`/`width_m`/`thickness_m`（沿用今天） |
| `def` | 可选，字符串，指向 `defs[].id`。**只管显示**：导入器把该 def 的模型（`defs[].model`）按逐轴比例拉到 `size` 并摆在固体中心，同时隐藏占位方块；模型不带碰撞、不进拾取场、不参与支撑。`size` 已含原作的实例缩放（例：WLK vs_2 的 72 m 木柱 `size=[8.9,72,19.7]` 来自实例缩放 ×68 ×554 ×5.8），所以模型不另有缩放字段。def 必须在 `defs[]` 里声明；找不到模型是 `[model_missing]` 错误，占位方块照画（不会因为缺模型而变成隐形） |
| `visible` | 可选，布尔，缺省 `true`。`false` = 什么都不画（占位方块也不画），只留碰撞和支撑——原作里的隐形碰撞墙（WLK `DUMMYHIT06_A`）。与 `def` 互斥 |

**`shape:"proxy"`：接触用代理、支撑用盒顶面（2026-11-16 追加，owner 口径「碰撞体必须跟视觉形状，按型给代理，不用 AABB」）。** 参考关卡的大件（树、招牌、睡着的妈妈…）原来用 AABB 当固体，一棵树的盒子把整个树冠包进去，螃蟹贴着树根站就被判「嵌进固体 60 cm」——那不是原作的摆放事实，是**我们的代理太粗**。所以：

- `collider` 是该 def 的拟合代理（`defs[].collider` 同一份），放在 `center` + `rotationQuat` 的局部帧里；盒/球/胶囊/凸包照原样用。
- `size` 仍写 AABB：它只用于**支撑面**（顶面）与旧的盒逻辑回退，不再当接触形状。两件事分开的理由：代理是凹/圆/斜的形状，而「球能站在它上面」需要一个水平顶面；用拟合凸包的顶面取支撑会在树冠上给出一个球站不住的尖角面。
- 只有 def 确实没有代理时才 `shape:"box"` + `proxySource:"aabb-fallback"`，并在 `notes` 标出来。
- 校验器：`shape:"proxy"` 要求 `collider` 合法且 `proxySource` 在枚举里；`shape:"box"` 带 `collider` 或 `parts` 是错误；`parts` 个数不在 2–4、`size` 非正、带未知键或越出 `size` 盒也是错误（`[structure_proxy]`）。
- **电机静态表只认 OBB**：导入器（`PlacementV2Rules.TrySolidContactBox`）把盒/球/胶囊/凸包代理都装成它的局部 AABB 一个盒，再把盒顶抬到 `size` 顶（支撑面），逐件打 note。
- **`parts`：稀疏件拆成几个盒（2026-11-19）。** 凸包占自身 AABB 不到 0.6 的件，单盒会在空处造墙（晒衣架凸包占 0.30，单盒是 1.43 m × 2 m 的实墙，原作关卡碰撞在盒内地面以上没有三角）。生成器（`ref_v2_from_extract.def_parts` → `collider_proxy.solid_parts`）对这类件从**模型三角网**（不是凸包：一根立杆加横跨全宽的底脚，其凸包本身就是整片楔形）自顶向下切分：每轮在所有盒 × 3 轴 × 15 个等分切位里选去掉空体积最多的一刀，穿过切面的三角精确裁成两段，所以相邻盒无缝；去掉的空体积 < 整体 AABB 的 2% 或已有 4 盒即停；盒每轴至少 1 cm，向外取整到 0.1 mm；按低角 (y, x, z) 排序。确定性：同一网格同一输出。导入器给这类固体挂 `parts.length` 个 `BoxCollider`（按文档顺序），全部进 `LevelArenaHost.staticBoxes`；只把**最高的那个盒**的顶抬到 `size` 顶，其余盒保留自身高度。`collider` 原样保留（`spawn_reach` 与编辑器仍用真凸包）。
- `meta.expected.staticObbs` 只计参与本关球碰撞的固体：每个 1 个，带 `parts` 的记子盒数（`placement_v2.static_obb_count`）。蟹座 35 条固体里有 1 条异区晒衣夹（4 个子盒），所以静态 OBB 从 65 降至 61；容量仍是 256。

**固体的三种显示方式（谁画模型）**：①**同 id 的 prop**（蟹座的 35 个固体）：固体只是碰撞，模型由同 `itemId` 的 prop 画，所以该 prop 也进拾取场：球够大（原作门槛）就能卷走它，卷走时同 id 的固体一起从电机静态表移除，重置时放回（`PickupFieldHost.arenaHost` → `LevelArenaHost.RemoveSolidsOf` / `RestoreRemovedSolids`，导入器把 `structures.solids` 里与 prop 同 id 的盒写进 `LevelArenaHost.staticBoxItemIds`）；要求 `shape:"proxy"`。②**`def`**（本次新增）：固体自己带一个显示模型，不需要 prop，所以不进拾取场、不会被卷入；用于原作里「不可卷入的障碍」（木柱、栅栏、树、气球）。③**两者都没有**：画占位方块（旧行为，初代 V_00 的 35 个固体就是这样）。`visible:false` 把三者都关掉。

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
| `walls[].heightM` / `thicknessM` | 墙高/厚；高度必须 > **本关可达球半径**（上表「本关可达球径」的一半）才算墙，校验器警告 `[activity_wall_height]`。全局 400 m 上限不是这个比较的尺子 |
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
 "behaviour": "static", "motion": null,
 "inherit": "full",
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
| `instanceIndex` | int | 否（参考导入必写） | 原作实例下标（`0–523`）。v2 用它把道具与提取结果的**运动/路径表对起来**（参考关卡 511/511 件、`0–523` 唯一），`motion`/`path`/父链对表一律按它，**禁止用名字匹配**。校验：非整数/布尔/负值 → `[prop_instance_index]` 错误，重复 → 错误，**超出 523 不报错**（出处可能指向提取结果里已不列出的记录） |
| `kind` | string | 是 | 内容分类（喂吸收白名单/统计），与 `behaviour` **分两列**（PROP_BEHAVIOURS §10.1，已定） |
| `behaviour` | enum | 是 | `static`（无自运动）／`motion`（由 `motion` 引用的运动程序驱动）／`attached`（自己不动、跟父件；`DraggedAlong` 的落点）／`ambient`（云/远景漂移：**也引用 motion**，但默认 `absorbable:false`）／`stacked`（摞放，作者态）。`kind` 与它仍是两列（PROP_BEHAVIOURS §10.1，已定） |
| `motion` | string \| null | 条件 | `behaviour:"motion"` 或 `"ambient"` 时必填，引用 `motions[].id`；`static`/`attached`/`stacked` 必须省略/null。**129 个原作动件共用 15 个 `moveType`（落成 14 条 `motions[]`），所以是「实例 → 本关程序表」，不是逐件内联曲线** |
| `zone` | string | 否 | `meta.zones[].id` 之一 |
| `position` | 3×number | 是 | **AABB 中心**（世界米）。原作 `position` 就是中心；保持这一约定，不再 re-seat |
| `rotationQuat` | 4×number `[x,y,z,w]` | 是 | Unity 顺序、单位长、**规范符号**（`w<0` 取反；`w==0` 时首个非零分量为正）。原作的轴+角经 §8.2 转换后写这里；无旋转写 `[0,0,0,1]` |
| `scale` | 3×number | 否 | 缺省 `[1,1,1]`；**非均匀策略见 §6.4** |
| `aabb` | 3×number | 是 | **已乘 scale 的**模型局部 AABB 尺寸（Unity 轴：x 宽、y 高、z 深）。下游只读这一个尺寸 |
| `volumeK` | number | 否 | 逐件覆盖；缺省取 def 的 `volumeK`，再缺省 0.70（PARITY_AUDIT G13）。范围 `0 < k ≤ 32` |
| `volumeMm3` | int | 是（派生） | `round(k · aabb.x · aabb.y · aabb.z · 1e9)`，定点 `long`（已定 (r)）。工具写、校验器复算；差 > 1 mm³ 报 `[prop_volume_stale]` |
| `absorbDiameterReqMm` | int | 是（派生） | `floor_mm((60 · volumeMm3 / π)^(1/3))`；原作门的整毫米口径（已定：按原作算式，k × bbox） |
| `absorbable` | bool | 是 | 作者声明；false 的件永远不可吸（结构件/装饰）。**写 false 只用于原作的永久不可卷入标记，不用于「这关的球够不着」**（owner 2026-11-22）：初代蟹座没有这种标记，大件靠门槛。运行时对 `absorbable:false` 的 prop 仍只按门槛判断（没有运行时列），所以 false 的 prop 应当是已被豁免出拾取场的贴花、云或走固体 def 的件。校验器在本关可达球径处比较「声明 vs 可吸门」，不一致只记一条汇总 note、不报错（v2 里它是作者声明，不是派生） |
| `valueUnits` | int | 是 | 账本整数（≥1；0/小数当场拒绝，已定 (j)）。与 `volumeMm3` 是两个轴：一个记账、一个几何门 |
| `parent` | string \| null | 否 | **单父指针**：父件的 `itemId`；null = 无父件。子件在父件被吃后**坠落**、不随父进账（已定 (s)） |
| `inherit` | `"full"` \| `"position"` \| `"none"` | 否 | 父件运动如何传给子件：`full` = 位置+朝向（`linkAction` 0/1/3）；`position` = 只跟位置、朝向不跟（`linkAction` 6/4）；`none` = 不跟（脱钩/坠落）。缺省：有 `parent` 时 `full`，否则 `none`。**43/90 条父子是「自己 `moveType=-1` 却随父跑」**，靠这个字段表达 |
| `phase` | number [0,1] | 否 | 逐实例相位（4 位小数）；驱动 Sway/Bob 等周期与动画桶。省略时导入器用 `itemId + level.id` 的确定性哈希派生（**不用 `Math.random()`**；原作相位来源见 §16.1）。规范写 `0 ≤ phase ≤ 1`，`1` 与 `0` 等价（写 `0`） |
| `clipStart` | int | 否 | 起始剪辑下标（对应 `defs[].clips`）；`-1`/省略 = 不指定。对应原作逐实例 `playAnimNo`（`−1/0/1`，152/153 与带骨骼动画的实例吻合【推断，强】） |
| `shakeOff` | bool | 否 | 原作 `u8MonoShakeOffFlag`（Cancer 仅 3 个为 1）的**存证**；v1 运行时不读（已定：不做击飞/甩脱）。语义见 §16.1 |
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

### 6.4 缩放策略（已定 → D-5）

原作的逐件缩放是**真实存在的**，不是可选项：Cancer 524 件里 6 件不是单位缩放（`instances.json`，见 §0 的 Q-T2）。

| 值 | 件数 | 父件 | 备注 |
|---|---|---|---|
| `(1,1,1)` | 518 | — | 98.9% |
| `(0.9,0.9,0.9)` | 4 | 无 | tableIndex 2/9/12/16，`nameIdx` 246/248/250（同类三型四件） |
| `(0.1,0.1,0.1)` | 1 | **有**（tableIndex 411，父 410、祖父 409） | 10× 小，挂接链上的最内一层 |
| `(0.0741,0.0291,0.0197)` | 1 | 无 | 非均匀；**语义**仍未验证（§16.1、NOCLIP §12.1），但处理方式已定 = ④（贴花：不碰撞、不可吸） |

规则分四**层**，别混成一句「不缩放」：

| 层 | 规则 | 落点 |
|---|---|---|
| **① 运行时** | **任何件在运行时都不按 `scale` 缩放**（挂接件尤其）：导入期已把 `scale` 乘进 `aabb`，运行时只读 `aabb`；父件带走子件时只搬位姿、不搬尺寸 | 运行时契约；`[prop_volume_stale]` 间接守住 |
| **② 作者态：均匀**（`sx=sy=sz`） | 允许，**任意 `behaviour`、任意 `parent`** | 原作的 4 件 0.9 + 1 件 0.1 都靠这条 |
| **③ 作者态：非均匀 + 有碰撞代理** | 只允许 **`parent == null` 且 `behaviour == "static"` 且 def.collider.type ∈ {box,sphere}**；否则 `[prop_scale_nonuniform]` | 胶囊/凸包非均匀缩放下不再是同类形状；动态件的接触与吸收门会随姿态变 |
| **④ 作者态：非均匀 + 无碰撞代理**（2026-11-16 新增） | 允许，条件是 **def 里根本没有 `collider`**（不是「没有 Collider 组件」）、`parent == null`、`behaviour == "static"`、`absorbable:false`；导入器记作 `NonUniformNoProxy` | 没有代理就没有可失真的形状：非均匀缩放只作用于视觉，`absorbable:false` 让它不进吸收门 |

- **「挂接件永不缩放」保留为①（运行时），不再是②的禁令**：原作那件 `(0.1,0.1,0.1)` 就挂在父件下。若照旧写成「`parent != null` 时 `scale` 必须为 1」，参考导入会拒掉一件真实数据。`[prop_scale_attached]` 因此改成「挂接件带**非均匀** scale」。
- **②的 `scale` 是静态列的一部分**：导入器把它乘进 `aabb` / `volumeMm3` / `absorbDiameterReqMm`，原值留在 `scale` 供审计；下游（电机、吸收门、拾取、编辑器）**不再乘第二次**，也不需要知道它存在。
- **这不是「可以把道具改大改小去凑档」**：②只允许原值照抄或作者写明理由，③④ 都要求无父 + `static`（③ 还限 box/sphere、④ 限无代理且不可吸）；调体积只能用 `volumeK`（PARITY_AUDIT G13），不是改 `scale`。
- **忽略缩放会错多少**（所以不能省这个字段）：`0.9` 体积差 1.37×、门差 1.11×；`0.1` 体积差 1000×、门差 10×（门槛会要求 10 倍大的球）；非均匀体积差 23,547×、门差 28.7×。渲染上忽略非均匀那件会每轴大 13.5 / 34.4 / 50.8 倍（一块 23×10×15 cm 的实心片，而不是 1.7×0.3×0.3 cm 的薄片）。
- **④ 不是漏洞，判据是「有没有代理」而不是「有没有碰撞体」**：`def.collider` 是**导入器要建的碰撞代理**（`box`/`sphere`/`capsule`/`convex`），没有它就等于「按无碰撞处理」，而非均匀缩放唯一会失真的东西——代理形状——压根不存在。所以 ④ 比 ③ 宽是**有理由的宽**，不是绕过校验：一件 def 若写了 `collider:convex`，哪怕它 `static`、无父、不可吸，仍然被 ③ 拒。反过来 ④ 也只放行最没有风险的形态（`static` + `absorbable:false`），因为它「视觉可以任意拉伸、物理完全不参与」。
- **原作的 1 件非均匀**（`sparrow_001`，def `sparrow` 不写 collider，D-5 原文即「按无碰撞处理」）就是 ④ 的唯一实例：照写 `scale`，标 `absorbable:false`、`behaviour:"static"`、不写 `collider`，并记进迁移报告的 `divergences`。

`aabb` 一律是**已乘 scale** 的尺寸，`volumeMm3` 由它和 `volumeK` 推出。**不要**在运行时再乘一次 scale。为什么允许逐件缩放，见 §18。

### 6.5 父指针与刚性组

- `parent` 的语义照抄原作 `parentBlock` 的 `(child,parent)` 对：单父、无环、父必须存在。Cancer 区域 2 有 130 对，其中 **90 条真父子**、40 条只有「我是挂接件」标志（父 = −1）。后者在 v2 里表达为 `parent:null` + `inherit:"none"`，`linkAction` 原值记 `notes`。**90 条真父子里有 43 条子件自己 `moveType=-1` 却随会动的父件跑**（§6.7），所以父链必须携带运动继承。
- **12 个 `dummyParent` 锚点就是那 12 个 `DummyProps` 命中代理**（Cancer 全部在 area 0；`DUMMYHIT01/02/03` 是大件外壳）。它们不是可吸道具，v2 不写进 `props[]`；`props` 的件数是 511（523 − 12），不是 524。
- 运行时形状照 PROP_BEHAVIOURS §10.9：`int[] propParent`（−1 = 无父）+ 每 tick 浮空复检 + 子件坠落，**不做 carry**。
- `rigidGroup` 是作者便利：同组只在**变换**上刚性（导入器给同组建一个父 `Transform`），不改变吸收/坠落语义。组内不得有 `parent`，组 ≤16 件。**运动只能由组根拥有**：组内成员 `behaviour` 必须是 `static`（没有父件时 `attached` 也非法），否则 `[prop_motion_group]`。组根被吸时，成员按 §6.7.7 释放/坠落，**不整组进账**；组根运动时成员作为刚性子件跟随。成员没有 `parent`，所以不读 `inherit`——它们由组根 Transform 刚性带着。

### 6.6 `defs[]`：类型级数据

```json
{"id": "teacup", "kind": "tableware",
 "model": "Assets/VRC_Katamari/Art/Props/teacup.fbx",
 "valueUnits": 1, "volumeK": 0.7,
 "collider": {"type": "capsule", "center": [0.0, 0.0, 0.0],
              "radius": 0.043, "halfHeight": 0.0, "axis": "y"},
 "scalePolicy": "uniform", "speedIndex": 2,
 "clips": [{"id": "teacup_idle", "asset": "Assets/VRC_Katamari/Art/Props/teacup_idle.anim",
            "fps": 30, "lengthSec": 2.0}],
 "notes": ""}
```

| 字段 | 语义 |
|---|---|
| `model` | Unity 相对路径（`Assets/…`、`.fbx`）；导入器按它找网格与 manifest，**不**允许 `..` 或绝对路径 |
| `valueUnits` | 类型默认价值；实例可覆盖（`props[].valueUnits` 优先） |
| `volumeK` | 类型默认填充系数；实例可覆盖；缺省 0.70 |
| `collider` | 按型代理：`box`/`sphere`/`capsule`/`convex`（4–24 点），字段与校验规则**完全沿用** [COLLIDER_PROXY](../PickupAbsorb/COLLIDER_PROXY.md) §2：局部空间、`hull` 相对 `center`、未知/跨类型字段报错、盒 ⊆ 视觉 AABB。v2 只加一条：实例 `scale` 会作用在代理上，**非均匀缩放时胶囊/凸包不被允许**（§6.4） |
| `scalePolicy` | `uniform`（默认）／`nonuniform`；后者只表示该类型允许被非均匀缩放，仍需满足 §6.4 的实例侧三条 |
| `speedIndex` | 0–11，缺省 2；映射到原作的逐类型速度档（§6.7.3）。**同一运动程序下不同螃蟹速度不同**，就是因为各自类型的 `pathSpeedIndex` 不同（moveType 13 里 11 个 1 档、2 个 2 档）【实测】 |
| `clips` | 0–6 段骨骼剪辑：`{id, asset?, fps?, lengthSec?}`。原作 153 个带动画实例只来自 **19 个类型**（每类型 4–6 段）；`asset` 缺省表示只有存证、运行时不能播。逐实例只存 `clipStart` 下标 |
| `notes` | 自由文本 |

**`defs[]` 不存 `aabb`**：AABB 是逐实例的（旋转后的包围盒语义在 `props[].aabb`），类型级尺寸只在 manifest 里。`volumeMm3` 也不进 defs：它由实例 aabb × k 推出（§6.2）。

### 6.7 `motions[]` / `paths[]`：原作的运动程序与路径（新）

**先说实际形状，别按「129 件都有 motion」去实现**（参考关卡实测）：129 条带运动程序的记录 → **128 件渲染道具**（差的那 1 条是 `#497 Dummy Hit 01` 命中判定体，没有道具）→ 其中 **113 件**带 `props[].motion`（**108** 条 `motion` + **5** 条 `ambient`），**15 件**是 `behaviour:"attached"`（`motionClass = dragged_along`：按 §6.7.3 既不写 `motions[]` 项、也不写 `props[].motion`，跟父件走即可）。`motions[]` 因此是 **14** 条（15 个 `moveType` 减掉 `dragged_along`），`paths[]` 是 **4** 条。`paths[].points` 的点数预算与简化规则见 §12.2。

#### 6.7.1 为什么是「逐关表 + 逐实例引用」而不是逐件曲线

原作的驱动链是三层【实测】：

```
props[].moveType（原作 s16，逐实例；v2 用 props[].motion 引用取代）
  → 本关 6 字节运动表项 (backupIndex s16, pathIndex s16, motionID s16)
  → motionActionTable[motionID] → 具体行为函数
速度：默认取 defs[].speedIndex（逐类型 pathSpeedIndex）→ 11 档速度表；
      只有 pathIndex ≥ 0 时才用路径记录里的 f32 速度
```

Make Cancer 的 524 个实例里 **129 个（24.6%）有运动程序**，只用到 **15 个 `moveType`**（含 `dragged_along`；524 件里另有 16 个取值的旧说法是把「无运动」也数了进去）。所以 v2 把它压成两张逐关表：

- `motions[]`：运动**程序**（类、参数、速度来源、更新率）；
- `paths[]`：可选**折线路径**（只有 11/524 个实例真的沿路径走）；
- `props[].motion` 引用其中一条；**没有逐件写死的位移曲线**。

`motions[]`、`paths[]` 是 `props`/`defs` 之外的旁挂表，但**不是逐实例数据**；它们与 `defs` 一样，导入后进 `MotionHost` 的定长数组。按 original-first，**13 类全部进数据模型**（D-10），运行时可以分阶段实现，但参考导入不丢类别。

#### 6.7.2 `motions[]`

```json
"motions": [
  {"id": "crab_scuttle", "class": "sporadic_walk",
   "speed": {"source": "type"},
   "params": {"turnDegPerSec": 180.0, "pauseFraction": 0.25, "wanderRadiusM": 1.5},
   "updatesPerSecond": 15, "loop": true,
   "provenance": {"moveType": 13},
   "notes": "Cancer moveType 13；31 个实例实测净速 20–25 cm/s、瞬时 30.3"}
]
```

| 字段 | 类型 | 必填 | 语义 |
|---|---|---|---|
| `id` | string | 是 | 本关唯一；`props[].motion` 引用它 |
| `class` | enum | 是 | 13 类，见 §6.7.3 |
| `speed` | object | 是 | `{"source":"none"}`（该类没有线速度：Sway/Hop/Whack/Spin/Flip/Bob/WaitForPlayer/拖挂）／`{"source":"type"}`（逐类型档，默认）／`{"source":"path"}`（用路径记录速度）／`{"source":"explicit","cmPerSec":30.3}`。`cmPerSec` 有限、0–500 |
| `path` | string \| null | 条件 | 路径型 class 必填，引用 `paths[].id`；其他 class 必须 null（否则 `[motion_path]`） |
| `params` | object | 否 | 按 class 校验的键值；未知键报错（§6.7.3） |
| `updatesPerSecond` | int | 是 | `0/5/10/15/30`（按 §6.7.3 的逐类档位，理由见 §6.7.5）；`0` 只允许交给表现层/剪辑的件。默认按 class 的档位 |
| `loop` | bool | 是 | `false` = 作者指定单次播放并停在末态。原作表没有该位；抽取的 `Misc/0x22` 按状态机生成 `true` |
| `provenance` | object | 否 | 原作的 `moveType` / `motionID` / `action` / `backupIndex` / `pathIndex` 原值，只由参考导入写；用于对拍，不参与运行 |
| `notes` | string | 否 | 存证/作者说明 |

#### 6.7.3 13 个运动类（数值是测得的；没测到的留空，不编）

> 证据标签：**【实测】** = 逐帧/数据观察；**【推断】** = 参考实现一致但未原机证实；未标 = 我们的实现参数/默认值（PROVISIONAL）。

| class | Cancer 实例 | 观测（强度） | `params`（键名/单位） | 速度来源 | 建议更新档 |
|---|---:|---|---|---|---|
| `sporadic_walk` | 31 | 随机游走，净 20–25 cm/s、瞬时 30.3【实测】 | `turnDegPerSec`、`pauseFraction`、`wanderRadiusM`（后三个是**我们的实现参数**，原作未公开） | `type` | **15 Hz** |
| `hop` | 18 | 向上跳 6.0–6.6 cm（PS2 原始 y 与项目世界相反）；**周期没测出来**（参考实现 0–8.5 s 均布）【实测/常量】 | `heightM`、`restSecMin`、`restSecMax` | `none` | **30 Hz** |
| `sway` | 15 | 水平峰峰 82.0 cm、竖直峰峰 7.19 cm，周期 1.50 s【实测】 | `amplitudeM`、`periodSec`、`axis`（`x/y/z`） | `none` | **15 Hz**（或表现层） |
| `dragged_along` | 15 | 自己不动，跟父件【实测】 | 无（用 `parent` + `inherit`） | `none`（继承父） | **0 Hz** |
| `wait_for_player` | 15 | 球靠近前静止；**7 件**有走近触发门（本关 `size=2` ⇒ **200 cm**），另 **8 件**的条件是 `inZone && parentRolled`、参考实现写死 false、原作未知（M2）【常量/数据】 | `triggerRadiusM`（默认 2.0 / 3.0；原作常量表按 `sizeGroup` 是 200/300/500/700/900 cm **五档**，小件实际只落到前两档；**与全局 `wake_radius_m = 12` 无关**，见 §7 与 `OPEN_DECISIONS`）、`triggerMotion`（可选：另一条 `motions[].id`） | `none` | 5 Hz 检测 |
| `whack_a_mole` | 8 | 出生伸出，先驻留 2–7 s，随后下沉、驻留、升回、驻留循环；单段约 0.75 s，行程约 1.5×全高【参考源码与短窗位移，驻留/完整循环未获初代实机确认】 | `riseM`、`riseSec`、`holdSec` | `none` | **30 Hz**（10 Hz 每 tick 错 35 cm） |
| `path_collision` | 8 | 真折线，恒速 24.5 cm/s【实测】 | 无（路径给点） | `path` | **15 Hz** |
| `spin` | 7 | 匀速自转【实测；角速度未量化】 | `degPerSec`（我们的参数，未测） | `none`（角速度） | 5 Hz |
| `clouds` | 5 | 固定高度 y≈17.4 m，303 cm/s，绕圈【实测】 | `altitudeM`、`turnRadiusM`（未测） | `type`（10 单位） | 10 Hz |
| `flip` | 2 | 周期性翻转【实测；周期未测】 | `periodSec` | `none` | 5 Hz |
| `bob` | 2 | ±0.8 cm，周期 ≈2.8 s【实测】 | `amplitudeM`、`periodSec` | `none` | 10 Hz |
| `sloping_path` | 2 | 沿路径 1.2 档 = 37 cm/s【实测】 | 无 | `path` | **15 Hz** |
| `back_and_forth` | 1 | 沿路径 0.6 档 = 18.2 cm/s 来回【实测】 | 无 | `path` | **15 Hz** |

**更新率由位移决定，不由类决定**（碰撞体每 tick 跟着变换走，滞后 = 速度 × 1/Hz，依据 `ORIGINAL_MOTION_PROGRAM` §12.0 与 §6.3）：`hop` 峰值 450 cm/s，10 Hz 下每 tick 45 cm，而蟹座一跳只有约 7 帧（约 0.23 s），10 Hz 很容易漏采跳顶；`whack_a_mole` 峰值 346 cm/s，每 tick 35 cm 的穿插会被看见。这两类必须 **30 Hz**；`sway` 峰值 88 cm/s ⇒ 15 Hz；其余按上表。全量 128 件按这张表 = **1,810 次/秒**（预算的 14.6%）。

**幅度已定稿（2026-11-16）**：换算因子 **`partBBox.max[1] = modelBboxM[1] / 2`**（即 `h` = 全高的一半），5 件道具（旗杆、蜘蛛、小蟹、石灯笼、向日葵）全部吻合。Sway 的 PS2 水平位移是 **`h·sin 2θ`**（峰峰 `2h·sin 20°` = **0.342×全高**）、竖直位移 `h(1−cos 2θ)`（峰峰 **0.030×全高**），两分量进项目世界时均取负——**不是** `2h·sin θ`；向日葵代回 **82.2 / 7.25 cm**，与实测 **82.0 / 7.19** 两个通道同时吻合。WhackAMole 行程 `1.5×全高`、Bob `0.075–0.086×全高`。见 `ORIGINAL_MOTION_PROGRAM` §5.7（推导）与 §4.0 结论 4；`params` 的 `amplitudeM` 直接按「全高 × 系数」填，不再标「待标定」。

**速度档**：`defs[].speedIndex` 0–11 映射到原作 11 档 + 哨兵：
`MOTION_SPEED_UNITS = (0.3, 1, 2, 4, 6, 8, 10, 15, 20, 40, 200, 0)`；
**1 单位 = 30.3 cm/s**（5 档交叉验证误差 ≤2%【实测】）。`speed.cmPerSec` 只是覆盖，不改变档位语义。

**`dragged_along` 不单独写 `motions[]`**：它等价于 `behaviour:"attached"` + `parent` + `inherit`。原作的 15 个 DraggedAlong 实例与 43 个「静止子件随动父件」在 v2 里都落成 `attached`，class 名只留在导入报告的统计里；枚举保留它是为了让报告能对齐原作分布。**5 朵云是 `moveType 16` 的道具实例**，不是舞台动画；它们落成 `behaviour:"ambient"` + `motions[].class:"clouds"`。

**只做数据、不做猜测**：`spin` 的角速度、`flip` 周期、`clouds` 的绕圈半径都没有测到；`whack_a_mole` 按参考源码往复，驻留与完整循环尚无初代实机复测；v2 允许 `params` 省略这些键（校验器不强制），运行时用项目默认值并在诊断里标 `PROVISIONAL`。**不要把「noclip 的 Math.random 相位」当原作事实**。

#### 6.7.4 `paths[]`

```json
"paths": [
  {"id": "town_truck_loop",
   "points": [[-12.0, 0.0, 4.0], [-2.0, 0.0, 6.0], [8.0, 0.0, 4.0],
              [8.0, 0.0, -6.0], [-12.0, 0.0, -6.0]],
   "loop": "loop", "speedCmPerSec": 243.0,
   "notes": "小镇关螺旋桨飞机同速档 8；卡车是作者示例"}
]
```

| 字段 | 语义 |
|---|---|
| `points` | 2–64 个 3D 世界点（米）；相邻点距离 ≥0.05 m；路径是折线，不插值曲率。**64 是引擎容量**，转换时的 Douglas–Peucker 简化规则见 §12.2 |
| `loop` | `"loop"`（首尾相连）／`"ping_pong"`（来回）／`"once"`（走完停住） |
| `speedCmPerSec` | 0–500；路径型运动用它，**优先于**逐类型档（原作的规则） |

**路径点已经有了，但被简化过**：提取器给出 **11 个实例引用 4 条不同路径**（`p00` 2 点、`p15` 59 点、`p22` 2 点、`p23` 50 点），而原作折线很密（`p15` 原始 137 点、`p23` 134 点）。v2 规定 `points` 上限 **64**（引擎定长数组），转换时用 Douglas–Peucker 简化、最大偏差 ≤ 2 cm，每条的 `notes` 记「原始点数 / 容差 / 实测最大偏差」——细则见 §12.2。**不要**在运行时再插值、重采样或按名字找路径。

#### 6.7.5 相位、更新率与运行时调度（Udon）

- **相位**：`props[].phase ∈ [0,1]`（1 与 0 等价）。省略时导入器派生 `phase = frac(hash32(level.id + "/" + itemId) / 2^32)`；**确定性**，同一份数据每次一样。周期运动的相位偏移 = `phase × period`；动画桶 = `floor(phase × 8) % 8`。**禁止 `Math.random()`**（账本/重放/双端一致性的前提）。
- **更新率分档**（按位移定，不按类定；依据 `ORIGINAL_MOTION_PROGRAM` §12.0 的碰撞需求表）：`hop` / `whack_a_mole` **30 Hz**（10 Hz 容易漏采 Hop 跳顶、Whack 每 tick 错 35 cm）；`sway`、路径三兄弟、`sporadic_walk` **15 Hz**；`bob`、`clouds` **10 Hz**；`spin`/`flip`/`wait_for_player` 检测 **5 Hz**；纯表现可交给共享剪辑/材质、`attached`/`static` **0 Hz**。全量 128 件 = **1,810 次/秒 = 14.6%** 预算。**我们的定步是 50 Hz（`fixedStepSeconds = 0.02`），承载 30 Hz 的运动更新绰绰有余——不需要为运动加速**；30 Hz 是「碰撞体每 tick 跟着走」的要求，不是画面要求。
- **`MotionHost` 形状**（UdonSharp，全定长并行数组，不逐件 `Update`）：
  ```
  int[]  motionIndex;      // -1 = 不更新（static/attached）
  int[]  motionClass;      // 枚举整数
  float[] motionClock;     // 秒
  float[] phase;           // 0..1
  float[] speedCmPerSec;   // 预解算，避免每步查表
  int[]  state;            // 0 idle / 1 active / 2 hold / 3 done
  int[]  pending; bool[] pendingFlag; int pendingHead, pendingCount;
  ```
  每个定步 tick 只处理 `pending` 队列里到期的行；到期时间 = `1 / updatesPerSecond`。没有 `List`/字典/结构体；容量 = 关卡动件数（Cancer 129，上限 `caps.movingProps`）。
- **表现层插值**：`MotionHost` 写「本步目标位姿」，`BallPresentation`/道具表现按渲染帧插值；不要把 10 Hz 的阶梯直接写进 Transform（会看起来一顿一顿）。
- **与父链/刚体组**：子件世界位姿 = 父件世界位姿 × 局部；`inherit` 决定是否吃父的旋转。子件自己的 `motion`（若有）是**局部运动**，叠在父变换上；`behaviour:"attached"` 的子件没有自己的 motion。刚体组的运动只由组根拥有（§6.5）。
- **预算**（基线 `PROP_BEHAVIOURS §7`：138 次单件更新/帧 @90 Hz = **12,420 次/秒**）：

| 场景 | 动件 | 10 Hz | 15 Hz | 30 Hz |
|---|---:|---:|---:|---:|
| Make Cancer（524 件，129 动） | 129 | 1,290/s（**10%**） | 1,935/s（16%） | 3,870/s（31%） |
| 1,381 件、25% 在动 | 345 | 3,450/s（**28%**） | 5,175/s（42%） | 10,350/s（83%） |
| 同上，只给路径+随机游走（≈1/3） | 115 | 1,150/s（9%） | 1,725/s（14%） | 3,450/s（28%） |
| **按原作实测需求取频率**（§6.7.3：Hop/Whack 30 Hz、Sway/路径 15 Hz、其余 5–10 Hz） | 128 | — | — | **1,810/s（14.6%）** |

  结论【实测数字 + 我方换算】：**按原作实测需求取频率比「统一 10 Hz 省预算」更对**——全量只要 1,810 次/秒（14.6%），而统一 10 Hz 对 Hop/Whack 是不够的；我们的 50 Hz 定步能直接承载 30 Hz，**不需要为运动加速**（不要再拿「迁到 30 Hz」重算）。Animator 数量不由这条决定（2026-11-16：不预先优化，见 §6.7.6）。

#### 6.7.6 骨骼动画：153 个实例直接挂 Animator（不预先优化）

- 原作 Cancer 的 **153 个带动画实例只来自 19 个类型**（每类型 4–6 段剪辑）；Town 97 个来自 33 个类型【实测】。
- **2026-11-16 拍板（D-11 重写）**：这一步**不预先优化**。参考关卡是 2018 年的 REROLL 复刻（PS2 时代原作），性能要求本身极低；153 个实例**各挂一个普通 Animator + 骨骼蒙皮**，不做顶点动画纹理（VAT），也不按 64 个砍。19 个类型共享控制器/剪辑仍然要做，但那只省资产，不省组件。
- v2 的数据形状：`defs[].clips[]`（类型级）+ `props[].clipStart`（逐实例起始段）+ `props[].phase`（相位）。运行时策略（已定，D-11）：
  1. 每个被动画实例一个 Animator；控制器/剪辑按类型共享（8 相位桶可选，用来打散「同帧同相」，是**表现**手段不是性能手段）；
  2. `caps.animators` 只作**数据统计**（Cancer = 153），不再是 64 的硬预算；`[anim_budget]` 只在 `caps` 与实际数量不一致时提示，不拦；
  3. **降级/优化路线全部押后**：VAT、按球距只播一部分、v1 不播骨骼——都写进 §16.2 的「以后真测出瓶颈再看」，**现在不做**。
- 原作的剪辑选择看运行时状态（`AnimationType` + 置换），**不是**数据字段；v2 的 `clipStart` 只是「开局放哪段」（对应 `playAnimNo`，152/153 吻合，【推断，强】）。运行时何时切 `MOVING/WRIGGLE/PANIC` 是**我们的行为模型**，标 PROVISIONAL。

#### 6.7.7 移动件被吸收：用当前位姿，父链一起处理

- **吸收判定用当前位姿**：球心到道具**此刻**世界 AABB 的最近点距离 ≤ `collisionRadius + itemRadius`（原有 reach），再走 `volumeMm3` 体积门；不是用 `placement.json` 里的摆放位置。移动件没有豁免，也不额外加难度。
- **实现口径**：`MotionHost` 直接写道具 transform，`PickupFieldHost` 读实时 transform 位置/朝向（吸收、挡路/支撑 OBB 都是），不另写回 AABB；详见 [MOTION_HOST](../PickupAbsorb/MOTION_HOST.md) §5。（原设计：移动件的世界 AABB 在同一调度里写回宿主。）吸收最多滞后一步：10 Hz 时 10 cm 的件在 30 cm/s 下移动 3 cm，reach 的包围球余量足以覆盖。OBB 近似用「AABB + 当前 yaw」即可（PROVISIONAL）；`collider` 逐型代理仍用于静态件与落地/支撑查询。
- **父件被吸**（已定 (s)）：立刻停父件的运动程序；子件 `parent = null`，保留**当前**世界位姿，进入 `FALLING`，脚下没东西就落；**不 carry、不随父进账**。子件自己的 `motion` 在 `FALLING` 期间挂起，`SETTLED` 后可恢复（建议；D-14）。
- **刚体组**：吸收其中一件只处理那一件；组不整组进账（与 `carry` 无关）。组根被吸时，成员先摘组、保留当前位姿、进入 `FALLING`；成员自己若有 `motion`，按 D-14 挂起。
- **`shakeOff`**：Cancer 仅 3 个实例为 1，语义未验证；v2 只存证，v1 运行时不读。已定「不做击飞」，所以不要用这个字段实现甩脱。
- **不做的事**：不因为道具在动就把它当「障碍/敌人」；**现状是吃不下的移动件会被 `PickupFieldHost` 按球径注册成挡路盒/支撑面**（位姿读实时 transform，最多滞后一个 0.05 s 扫描间隔；`MotionHost` 对 <30 Hz 的件做了插值，所以不再是 10/15 Hz 的阶梯），移动件没有豁免（这是我们的实现取舍；原作里球撞到移动件会怎样，noclip 观察不到，见 §16.1）；停下来的 `hold`/`done` 同样按球径判，不按运行时状态判。


---

## 7. 例子（一个完整文档 + 四个片段）

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
             "start": [0.0, 0.15, 0.0], "spawnSource": "authored"},
    "timeLimitS": 0,
    "playerSpawn": [0.0, 0.5, -2.4],
    "groundY": -2.0,
    "bounds": [[-2.0, -2.0], [2.0, 2.0]],
    "spawnClearanceM": 0.2,
    "zones": [{"id": "pad", "code": 0, "kind": "ground"}],
    "arena": {"id": "tiny_pad"},
    "caps": {"props": 16, "terrainTriangles": 512, "terrainVertices": 1024,
             "staticSolids": 8, "activityWalls": 8, "movingProps": 0, "animators": 0},
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
     "motion": null, "inherit": "none", "zone": "pad",
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
  "motions": [],
  "paths": [],
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
             "start": [-5.0, -0.999, 2.5], "spawnSource": "authored"},
    "timeLimitS": 0,
    "playerSpawn": [-5.0, -0.599, 2.5],
    "groundY": -20.0,
    "bounds": [[-26.41, -32.36], [32.47, 47.92]],
    "spawnClearanceM": 0.5,
    "zones": [{"id": "default", "code": 0}, {"id": "zone_a", "code": 24},
              {"id": "zone_b", "code": 3}, {"id": "zone_c", "code": 8}],
    "arena": {"id": "_ref_cancer"},
    "caps": {"props": 511, "terrainTriangles": 4096, "terrainVertices": 8192,
             "staticSolids": 256, "activityWalls": 8, "movingProps": 129, "animators": 153},
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
     "behaviour": "motion", "motion": "crab_scuttle", "inherit": "none",
     "phase": 0.37, "clipStart": 0, "zone": "zone_a",
     "position": [4.80, -1.32, 3.10],
     "rotationQuat": [0.0, 0.766044, 0.0, 0.642788],
     "aabb": [0.115, 0.080, 0.086], "volumeK": 0.7, "volumeMm3": 553840,
     "absorbDiameterReqMm": 219, "absorbable": true, "valueUnits": 1,
     "parent": null, "rigidGroup": null, "support": null, "stackedOn": null},
    {"itemId": "ceiling_light_01", "def": "ceiling_light", "kind": "furniture",
     "behaviour": "attached", "motion": null, "inherit": "position",
     "phase": 0.0, "zone": "zone_b",
     "position": [0.0, 0.21, 0.0],
     "rotationQuat": [1.0, 0.0, 0.0, 0.0],
     "aabb": [0.16, 0.10, 0.16], "volumeK": 0.5, "volumeMm3": 1280000,
     "absorbDiameterReqMm": 290, "absorbable": true, "valueUnits": 9,
     "parent": "house_01", "rigidGroup": null, "support": null, "stackedOn": null},
    {"itemId": "decal_017", "def": "decal", "kind": "decor",
     "behaviour": "static", "motion": null, "inherit": "none", "zone": "zone_b",
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
     "scalePolicy": "uniform", "speedIndex": 1,
     "clips": [{"id": "crab_walk", "asset": "Assets/VRC_Katamari/Art/Props/crab_walk.anim",
                "fps": 30, "lengthSec": 1.0}]}
  ],
  "motions": [
    {"id": "crab_scuttle", "class": "sporadic_walk",
     "speed": {"source": "type"}, "path": null,
     "params": {"turnDegPerSec": 180.0, "pauseFraction": 0.25, "wanderRadiusM": 1.5},
     "updatesPerSecond": 15, "loop": true}
  ],
  "paths": [],
  "cameras": [],
  "renderPresets": [],
  "regions": []
}
```

> 这是**片段**，不是可独立通过校验的文件：`meshRef.sha256` 是占位、`structures.solids` 只给了一条、`zone_a/zone_b/zone_c` 的 code 是**作者暂定映射**（原作的 24/3/8 到底叫什么仍未证实，§3.7）。逐三角形的 `zoneIds`/`classes` 在侧车里，JSON 只写 `count`（§8.3）。原作的 zone 分布是**逐顶点**的（7,154 个值），我们按每三角形三个顶点的多数票派生逐三角形标签，所以不照抄那组计数。

### 7.3 三个运动片段（只列相关表，不是完整文档）

#### 7.3.1 Sway 的树 + 挂在树上的风铃（父件动、子件跟）

```json
{
  "motions": [
    {"id": "tree_sway", "class": "sway",
     "speed": {"source": "none"}, "path": null,
     "params": {"amplitudeM": 0.036, "periodSec": 1.5, "axis": "y"},
     "updatesPerSecond": 10, "loop": true}
  ],
  "paths": [],
  "defs": [
    {"id": "tree", "kind": "plant", "model": "Assets/VRC_Katamari/Art/Props/tree.fbx",
     "valueUnits": 40, "volumeK": 0.35, "speedIndex": 2,
     "clips": [{"id": "tree_idle", "asset": "Assets/VRC_Katamari/Art/Props/tree_idle.anim",
                "fps": 30, "lengthSec": 3.0}],
     "collider": {"type": "box", "center": [0.0, 0.0, 0.0], "size": [0.4, 2.2, 0.4]}},
    {"id": "chime", "kind": "decor", "model": "Assets/VRC_Katamari/Art/Props/chime.fbx",
     "valueUnits": 2, "volumeK": 0.6,
     "collider": {"type": "sphere", "center": [0.0, 0.0, 0.0], "radius": 0.03}}
  ],
  "props": [
    {"itemId": "tree_01", "def": "tree", "kind": "plant",
     "behaviour": "motion", "motion": "tree_sway", "inherit": "none",
     "phase": 0.12, "clipStart": 0,
     "position": [1.0, 1.1, 0.0], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "aabb": [0.4, 2.2, 0.4], "volumeMm3": 123200000, "absorbDiameterReqMm": 1330,
     "absorbable": true, "valueUnits": 40},
    {"itemId": "chime_01", "def": "chime", "kind": "decor",
     "behaviour": "attached", "motion": null, "inherit": "full",
     "parent": "tree_01", "position": [1.25, 1.45, 0.0],
     "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "aabb": [0.06, 0.10, 0.06], "volumeMm3": 216000, "absorbDiameterReqMm": 160,
     "absorbable": true, "valueUnits": 2}
  ]
}
```

读法：树自己按 1.5 s 周期摆 ±3.6 cm；风铃 `inherit:"full"`，世界位姿跟着树的变换走，**自己 0 Hz**。原作 8 个风铃挂在树上就是这么跑的（43 条「静止子件随动父件」之一）。若风铃还要自转，给它自己的 `motion`（局部运动，叠在父变换上），不要另建父链。

#### 7.3.2 SporadicWalk 的螃蟹（同程序、不同速度靠类型档）

```json
{
  "motions": [
    {"id": "crab_scuttle", "class": "sporadic_walk",
     "speed": {"source": "type"}, "path": null,
     "params": {"turnDegPerSec": 180.0, "pauseFraction": 0.25, "wanderRadiusM": 1.5},
     "updatesPerSecond": 15, "loop": true}
  ],
  "paths": [],
  "defs": [
    {"id": "small_fiddler_crab", "kind": "animal",
     "model": "Assets/VRC_Katamari/Art/Props/small_fiddler_crab.fbx",
     "valueUnits": 1, "volumeK": 0.7, "speedIndex": 1,
     "clips": [{"id": "crab_walk", "asset": "Assets/VRC_Katamari/Art/Props/crab_walk.anim",
                "fps": 30, "lengthSec": 1.0}],
     "collider": {"type": "convex", "center": [0.0, 0.0, 0.0],
                  "hull": [[-0.06, -0.04, -0.05], [0.06, -0.04, -0.05],
                           [0.0, 0.045, 0.05], [0.0, -0.03, 0.05]]}}
  ],
  "props": [
    {"itemId": "crab_001", "def": "small_fiddler_crab", "kind": "animal",
     "behaviour": "motion", "motion": "crab_scuttle", "inherit": "none",
     "phase": 0.37, "clipStart": 0,
     "position": [4.80, -1.32, 3.10],
     "rotationQuat": [0.0, 0.766044, 0.0, 0.642788],
     "aabb": [0.115, 0.080, 0.086], "volumeMm3": 553840,
     "absorbDiameterReqMm": 219, "absorbable": true, "valueUnits": 1}
  ]
}
```

读法：`speedIndex: 1` ⇒ 1 单位 ⇒ **30.3 cm/s**；同为 `crab_scuttle` 的椰子蟹类型 `speedIndex: 2` ⇒ 60.6 cm/s。**不要**把速度写进 `motion`（同一程序要服务多类型）；`speed.cmPerSec` 只给没有类型档的导入件兜底。

#### 7.3.3 沿路径跑的卡车（真路径，速度取路径记录）

```json
{
  "motions": [
    {"id": "truck_drive", "class": "path_collision",
     "speed": {"source": "path"}, "path": "town_truck_loop",
     "params": {}, "updatesPerSecond": 15, "loop": true}
  ],
  "paths": [
    {"id": "town_truck_loop",
     "points": [[-12.0, 0.0, 4.0], [-2.0, 0.0, 6.0], [8.0, 0.0, 4.0],
                [8.0, 0.0, -6.0], [-12.0, 0.0, -6.0]],
     "loop": "loop", "speedCmPerSec": 243.0}
  ],
  "defs": [
    {"id": "truck", "kind": "vehicle", "model": "Assets/VRC_Katamari/Art/Props/truck.fbx",
     "valueUnits": 120, "volumeK": 0.55, "speedIndex": 5,
     "collider": {"type": "box", "center": [0.0, 0.0, 0.0], "size": [2.0, 2.2, 5.0]}}
  ],
  "props": [
    {"itemId": "truck_01", "def": "truck", "kind": "vehicle",
     "behaviour": "motion", "motion": "truck_drive", "inherit": "none",
     "phase": 0.0,
     "position": [-12.0, 1.1, 4.0],
     "rotationQuat": [0.0, 0.0, 0.0, 1.0],
     "aabb": [2.0, 2.2, 5.0], "volumeMm3": 12100000000,
     "absorbDiameterReqMm": 6136, "absorbable": true, "valueUnits": 120}
  ]
}
```

读法：路径型运动**只用 `paths[].speedCmPerSec`**，`defs[].speedIndex` 只是类型默认（8 单位 = 243 cm/s，对应 `speedIndex 5`；与实测小镇螺旋桨飞机一致）；`loop:"loop"` 首尾相连。路径点是世界米；如果是 `ping_pong`（原作 BackAndForth），到端点反向。


---

## 8. 规范化、坐标与侧车

### 8.1 规范字节（唯一写出格式）

| 规则 | 值 |
|---|---|
| 编码 | UTF-8 无 BOM；LF；末尾一个换行 |
| 缩进 | 2 空格；对象一成员一行；数组元素一行一个（顶点 `[x, y, z]` 单独一行） |
| 键序 | 文档：`schema, meta, terrain, structures, activityArea, props, defs, motions, paths, cameras, renderPresets, regions`；对象内部按本文表格的字段顺序（`*_KEYS` 常量）；**未知键是错误**，所以不存在「保留键序」的歧义 |
| 排序 | `props` 按 `itemId`；`defs` 按 `id`；`motions` 按 `id`；`paths` 按 `id`；`structures.solids` 按 `id`；`activityArea.walls` 按 `id`；`broadPhase.boxes`/`chunks` 按 `id`；`zones` 按 `code` 再 `id`；`cameras`/`renderPresets` 按 `id`；**几何数组保持作者/源顺序**（顶点/索引的顺序是几何身份，不能排序） |
| 数字 | 位置/尺寸/`aabb`/`scale` 4 位；四元数 6 位；`volumeK` 4 位；`volumeMm3`/`absorbDiameterReqMm` 整数；`phase` 4 位；`periodSec`/`holdSec`/`restSec*`/`riseSec` 3 位；`amplitudeM`/`heightM`/`riseM` 4 位；`degPerSec` 3 位；`cmPerSec` 1 位；角度字段 3 位；所有数不得是 NaN/Inf；`-0.0 → 0.0`（全局，含嵌套数组） |
| 四元数 | 规范符号：`w<0` 整体取反；`w==0` 时首个非零分量为正 |
| 布尔/空 | 所有可空键（`parent`/`rigidGroup`/`support`/`stackedOn`/`motion`/`path`）显式 `null` 与省略等价；`phase` 省略 = 由哈希派生；`clipStart:-1` 与省略等价；`shakeOff:false` 省略。**规范写出一律省略**。本文示例为可读性保留 `null`，转换器/导出器不写 |
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

### 8.4 侧车 `.katvisual`（`terrain.visual` 的顶点）

| 段 | 类型 | 说明 |
|---|---|---|
| magic | 4 B | ASCII `KVIS` |
| version | u16 | `1` |
| indexWidth | u8 | `2` 或 `4`；**每块**顶点数 < 65,536 时用 2 |
| flags | u8 | bit0 = 带法线，bit1 = 带 UV |
| vertexCount / indexCount / partCount | u32×3 | 与 JSON `meshRef`、`parts.length` 逐项相等 |
| positions | f32 LE × 3 × vertexCount | 世界米（Unity 帧），各块顶点首尾相接 |
| normals | f32 LE × 3 × vertexCount | 可选 |
| uvs | f32 LE × 2 × vertexCount | 可选；原点 = PNG 左上角 |
| indices | u16/u32 LE × indexCount | **块局部**索引，每 3 个一个三角形；各块的索引首尾相接 |

路径/sha256 规则同 §8.3；PNG 贴图放 `terrain/textures/`，JSON 里逐张钉 sha256。跨语言黄金字节（一个三角形）在 Python 与 C# 两边的测试里是同一串十六进制。参考关卡的侧车与 PNG 在 `_ref_*` 目录里，被 `.gitignore` 排除（贴图是 UNLICENSED 原作素材）。

参考关卡的原作放置格式（初代 `.mono.bytes`、WLK `LocData`）与换算见 [ORIGINAL_PLACEMENT_FORMATS](../../Research/KatamariBallRules/ORIGINAL_PLACEMENT_FORMATS.md)。`.katvisual` 的材质 id 必须按贴图名稳定命名（`m_<贴图名>`），不能按出现顺序编号，否则增删部件会让 id 错位；导入器只增改不删，改 id 后要先清掉旧的 `wlk_` 子对象与旧材质。

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
| `[meta_start_diameter]` | error | 缺失、≤0、≥ 全局上限（400 m） |
| `[meta_time_limit]` | error | 剧情关缺失或 ≤0 |
| `[meta_zone_code]` | error | `code` 不在 0–255；除 code 0 外重复；code 0 被声明超过一次（0 是默认区，最多一条） |
| `[meta_unknown_key]` | error | `meta`/`ball`/`caps` 出现未知键 |
| `[meta_ground_y]` | error | 有 `terrain` 时 `groundY` 不低于地形最低点（兜底平面会顶穿地形） |
| `[meta_player_start]` | error | `playerStart` 不是对象、含三键之外的键、`position` 不是 3 个数、`facingDeg` 超出 −360..360，或脚底 y 不高于 `killPlane.y` |
| `[terrain_mesh]` | error | 索引越界/长度为非 3 倍数/退化三角形（面积 < 1e-9 m²）/顶点数超侧车宽度 |
| `[terrain_winding]` | error | 共享边的两个三角形按同方向遍历该边（说明其中一个绕序反了）；这是「面朝里」的确定性检测 |
| `[terrain_budget]` | error | 三角形/顶点超 `meta.caps` 或运行时单块上限（512 三/1,536 顶） |
| `[terrain_zone]` | error | `zoneIds` 长度≠三角形数，或引用了未声明的 code |
| `[terrain_broadphase]` | error | 区间有洞/重叠/乱序，或 AABB 未包住其三角形（容差 1 mm） |
| `[terrain_grid]` | error | `cellM` 越界、`origin` 不覆盖 broad phase |
| `[terrain_sidecar]` | error | 文件缺失、sha256 不符、计数不符、路径越界 |
| `[terrain_visual]` / `[terrain_visual_key]` | error | `terrain.visual` 不是对象、`filter` 不在 `point/bilinear`；任一层（`visual`/`meshRef`/`parts[]`/`bounds`/`submeshes[]`/`materials[]`）出现未知键 |
| `[terrain_visual_sidecar]` | error | `.katvisual` 缺失/魔数或版本错/截断/有尾字节、sha256 不符、顶点/索引/三角形数、索引宽度、法线/UV 标志与 `meshRef` 不符、`partCount` ≠ `parts.length`、路径越界 |
| `[terrain_visual_part]` | error | `id` 重复、`role` 不在 `stage/sky`、`vertexRange`/`indexRange` 不按序首尾相接或不铺满 `meshRef` 计数、索引数不是 3 的正整数倍、子网格不首尾相接或不铺满本块、局部索引越界、`bounds` 与顶点 AABB 差 >1 mm、退化三角形（面积 <1e-9 m²） |
| `[terrain_visual_material]` | error | `id` 缺失/重复、`blend` 不在 `opaque/cutout/alpha`、`width`/`height` 非正整数、子网格引用不存在的材质 |
| `[terrain_visual_texture]` | error | PNG 路径越界/文件缺失、sha256 不符、不是 PNG、像素尺寸与声明不符 |
| `[terrain_visual_winding]` | error | 少于一半的三角形与其顶点法线一致（整张网格绕序是反的：背面剔除后只剩「洞」） |
| `[structure_role]` | error | v2 新文件写 `role:"boundary"`/`"ground"`（迁移产物允许并 warning） |
| `[structure_proxy]` | error | `shape:"proxy"` 缺 `collider`/`collider` 不合法/`proxySource` 不在枚举/`parts` 不合法；`shape:"box"` 却带 `collider` 或 `parts` |
| `[structure_display]` | error | `def` 不是非空字符串、不在 `defs[]` 里，或与 `visible:false` 同时出现；`visible` 不是布尔 |
| `[structure_capacity]` | error | 静态固体 > `caps.staticSolids`（256）或 v2 关闭时 `ramps` >4、`boxes` >8 |
| `[activity_wall]` | error | 点数 <2、闭合多边形自交、`heightM ≤ 0`、`minBallDiameterM` 不是正数（若出现） |
| `[activity_wall_gate]` | warning | 门的阈值 ≤ 起始球径（这扇门从第一帧就是开的，多半写错了；要永久墙请省略该键） |
| `[activity_wall_capacity]` | error | 接触 v2 关闭且有效墙段 >8 |
| `[activity_wall_height]` | warning | `heightM` ≤ 本关可达球半径（墙挡不住；可达球径是派生值，全局 400 m 上限不参与） |
| `[activity_kill_plane]` | error | `y` 高于 `groundY`/地形最低点，或 `respawn` 不是 `ballStart`/坐标 |
| `[prop_id]` | error | `itemId` 缺失/重复/前后空白 |
| `[prop_parent]` | error | 父不存在、自指、成环、与 `rigidGroup` 同时出现 |
| `[prop_scale]` | error | 非正、非有限 |
| `[prop_scale_attached]` | error | `parent != null` 且 scale **非均匀**（均匀 scale 合法：原作有 1 件 0.1 的挂接件，§6.4 ②） |
| `[prop_scale_nonuniform]` | error | 非均匀但违反 §6.4 ③ 的三个条件 |
| `[prop_volume_stale]` | error | `volumeMm3` 与 `k·a·b·c` 差 >1 mm³；例外：`absorbable:false` 且体积 <1 mm³ 时允许 0 |
| `[prop_absorb_req_stale]` | error | `absorbDiameterReqMm` ≠ 由 `volumeMm3` 推出的整毫米 |
| `[prop_behaviour]` | error | 未知 behaviour；`behaviour:"motion"`/`"ambient"` 没有 `motion`；`static`/`attached`/`stacked` 却带 `motion`；`ambient` 且 `absorbable:true` |
| `[prop_motion]` | error | `motion` 引用不存在的 `motions[].id`；`motion` 的 class 与 `behaviour` 不兼容 |
| `[prop_inherit]` | error | `inherit` 非三值之一；无父件却写 `inherit:"full"/"position"`；`inherit` 与 `parent:null` 矛盾 |
| `[prop_phase]` | error | `phase` 不在 [0,1]；规范写出 4 位小数（不是校验条件） |
| `[prop_clip]` | error | `clipStart` 越界（`< -1` 或 ≥ `defs[].clips` 长度）；`clipStart ≥ 0` 但 def 没有 `clips` |
| `[prop_motion_group]` | error | `rigidGroup` 成员带 `motion`（运动只能由组根拥有） |
| `[motion_id]` | error | `motions[].id` 缺失/重复/前后空白 |
| `[motion_key]` / `[path_key]` | error | `motions[]`/`paths[]` 出现未知键（键白名单同批落地） |
| `[motion_class]` | error | 未知 class；`dragged_along` 没有 `parent` 的实例引用它 |
| `[motion_params]` | error | 该 class 不允许的 `params` 键，或必填参数缺失/非有限/越界 |
| `[motion_path]` | error | 路径型 class 缺 `path`；非路径型带 `path`；`path` 引用不存在 |
| `[motion_speed]` | error | `speed.source` 非四值；`explicit` 缺 `cmPerSec`；`path` 但无路径速度；`none`/`type` 却给 `cmPerSec`；`cmPerSec` 不在 0–500 |
| `[motion_rate]` | warning | `updatesPerSecond` 与 §6.7.5 的建议档不符（允许，但要说明） |
| `[motion_budget]` | warning | `caps.movingProps` × 实际更新率超过预算表（§6.7.5） |
| `[paths_id]` / `[path_points]` / `[path_speed]` | error | 路径 id 重复；点数不在 2–64、点非 3 数、相邻点 <0.05 m；速度不在 0–500 |
| `[def_speed_index]` | error | `speedIndex` 不在 0–11 |
| `[def_clips]` | error/warning | 剪辑数 >6、`id` 重复、`fps` ≤0（error）；我们的关卡有 `clips` 但 `asset` 缺失（warning） |
| `[anim_budget]` | warning | 被动画实例数（`clipStart ≥ 0`）≠ `caps.animators`（`caps` 过期）；已不是 64 硬预算（2026-11-16 改口径，§6.7.6） |
| `[prop_key]` | error | `props[]` 出现未知键（不得静默丢弃） |
| `[prop_support]` / `[prop_overlap]` | error | 声明支撑不存在/悬空；两件 AABB 互穿且无父子/摞放关系 |
| `[prop_bounds]` | error | 道具离开 `meta.bounds` 超过 5 cm |
| `[prop_instance_index]` | error | `props[].instanceIndex` 非整数/布尔/负值；或同一关内重复（它是对表键，必须唯一）。**超出 523 不报错**（出处可能指向提取结果里已不列出的记录） |
| `[caps_props]` | error | 件数 > `caps.props`（每关按 130/511/4000 档自己声明） |
| `[caps_motion]` | error | `caps.movingProps > caps.props`，或 `caps.animators > caps.props`（**动画与运动是两回事**：D-11 下动画件可以多于运动件——Cancer 是 153 个骨骼动画实例、129 个有运动的实例，所以 `animators` 只能对 `props`，**不能**对 `movingProps`——`animators ≤ movingProps` 会把原作自己的数据判错） |
| `[canonical]` | warning | 文件不是规范字节（现有行为） |
| `[schema_legacy]` | error | 读到 v1 文件：提示先跑转换器 |

### 9.3 键白名单（`*_KEYS` 的 v2 等价物）

```python
META_KEYS      = ("id","scene","units","convention","ball","timeLimitS","playerSpawn","playerStart",
                  "groundY","bounds","spawnClearanceM","zones","arena","caps","budget",
                  "expected","source","migration","areaNo")
BALL_KEYS      = ("startDiameterM","targetDiameterM","maxDiameterRef","maxDiameterM",
                  "start","spawnSource")
CAPS_KEYS      = ("props","terrainTriangles","terrainVertices","staticSolids",
                  "activityWalls","movingProps","animators")
BUDGET_KEYS    = ("planes","boxes","ramps","blocking","propSupports")
EXPECTED_KEYS  = ("propCount","growthMm3","structureCount","staticObbs","overlapBudget",
                  "valueUnits","terrainTriangles")
SOURCE_KEYS    = ("kind","provenance","windingFix","licence")
ZONE_KEYS      = ("id","code","kind")
TERRAIN_KEYS   = ("id","kind","winding","vertices","indices","meshRef","triangles",
                  "broadPhase","chunks","grid","materialSegments","visual","source")
VISUAL_KEYS    = ("meshRef","parts","materials","filter","source")
VISUAL_MESHREF_KEYS = ("path","sha256","vertexCount","indexCount","triangleCount",
                       "indexWidth","normals","uvs")
VISUAL_PART_KEYS    = ("id","role","vertexRange","indexRange","bounds","submeshes")
VISUAL_BOUNDS_KEYS  = ("min","max")
VISUAL_SUBMESH_KEYS = ("material","firstIndex","indexCount")
VISUAL_MATERIAL_KEYS = ("id","texture","sha256","width","height","blend")
TRI_KEYS       = ("count","zoneIds","classes","zoneCounts")
BROAD_KEYS     = ("kind","boxes")
BROAD_BOX_KEYS = ("id","min","max","triangleRange")
CHUNK_KEYS     = ("id","boxRange","triangleRange")
GRID_KEYS      = ("cellM","origin","cellsX","cellsZ")
SEGMENT_KEYS   = ("id","firstIndex","indexCount","material")
STRUCTURE_KEYS_V2 = ("id","kind","role","shape","center","size","curve","rotationQuat",
                     "collision","absorbable","support","meets","material","notes",
                     "collider","proxySource","parts","hitOnAreaNo")
SOLIDS_KEYS    = ("solids",)
ACTIVITY_KEYS  = ("walls","killPlane","softMarginM","source")
WALL_KEYS      = ("id","points","closed","heightM","thicknessM","minBallDiameterM",
                  "kind","material","notes")
KILL_KEYS      = ("y","respawn","zoneId")
PROP_KEYS_V2   = ("itemId","def","kind","behaviour","motion","inherit","phase","clipStart",
                  "shakeOff","zone","position","rotationQuat","scale","aabb","volumeK",
                  "volumePenalty","volumeMm3","growthMm3","absorbDiameterReqMm","absorbable",
                  "valueUnits","parent","rigidGroup","support","stackedOn","floatingOk",
                  "notes","instanceIndex","hitOnAreaNo")
DEF_KEYS_V2    = ("id","kind","model","valueUnits","volumeK","volumePenalty","collider",
                  "scalePolicy","speedIndex","clips","notes")
MOTION_KEYS    = ("id","class","speed","path","params","updatesPerSecond","loop",
                  "provenance","notes")
PROVENANCE_KEYS = ("moveType","motionID","action","backupIndex","pathIndex")
SPEED_KEYS     = ("source","cmPerSec")
PATH_KEYS      = ("id","points","loop","speedCmPerSec","notes")
CLIP_KEYS      = ("id","asset","fps","lengthSec")
MOTION_CLASSES = ("sporadic_walk","hop","sway","dragged_along","wait_for_player",
                  "whack_a_mole","path_collision","spin","clouds","flip","bob",
                  "sloping_path","back_and_forth")
```

`EXPECTED_KEYS` 是**实现与参考文档的实际并集**：`growthMm3`/`structureCount`/`staticObbs`/`overlapBudget` 是 v2 新增的派生/声明量（`valueUnits` 只作为 v1 输入兼容保留、写出时丢弃，两者共存不报警告）。

`PROP_KEYS_V2` **删掉了** v1 的 `group`/`groupId`（原作的视觉挂接组由 `parent` + `rotationQuat` 表达；组容器由导入器按 `parent` 建）、浮点 `absorbDiameterReq`，以及上一版的 `behaviourParams`（`linkAction` 迁到 `inherit`，其余进 `notes`）；**加进了** `behaviour`/`motion`/`inherit`/`phase`/`clipStart`/`shakeOff`/`scale`/`volumeMm3`/`absorbDiameterReqMm`/`parent`/`rigidGroup`/`floatingOk`，以及 `volumePenalty`/`growthMm3`/`instanceIndex`（前者是逐件回收惩罚，后两者是派生量与对表键，见 §6.2）。删掉的键在 v2 里是未知键，转换器负责搬走。

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
| C6 | **上限迁移**：源数据的 `ballDiameterMax` 可保留在 `meta.migration.maxDiameterM` 作溯源，不参与 V2 校验或运行时 | 不为新关填写 `ball.maxDiameterM` |
| C7 | **边界迁移**：4 张矩形 `boundary` 平面 → `activityArea.walls` 的闭合矩形；非矩形/非轴对齐保留 `role:"boundary"` + warning | 9 个测试场 + hub + story_01 的边界逐份核对 |
| C8 | **ground 迁移**：`role=ground` 的平面不再进 `structures`，`meta.groundY` 取其平面高度；没有 terrain 的关卡保持平地 | 13 份的 `meta.groundY` 逐份核对；平地关卡重导入后仍是平地 |
| C11 | **运动数据不伪造**：v1 的 13 份文件没有 `motions`/`paths`，转换器写空表并把 `behaviour` 设为 `static`；参考关卡的 129 个动件由 §11.5 的参考导入器从 `instances.json` 的 `motion{id,action,speed,pathIndex}` 生成，**不能**用「看起来像」猜 class | 参考导入报告列出每个未映射的 `motionID` |
| C9 | **参考关卡**：转换产物仍在 `_ref_*/` 目录，被 `.gitignore` 排除；转换器不把任何网格/坐标写进仓库内其他路径 | `git status` 干净 |
| C10 | **可回滚**：转换在 git 分支上做；v1 文件在转换器验证通过前不被覆盖（先写 `.v2.json` 草稿，人工确认后 `--apply`） | 转换器有 `--dry-run` |

### 10.2 转换顺序（每份文件）

```
读 v1（katamari.placement/0）
  → 校验 v1 全绿（否则拒绝转换，不猜）
  → meta：改名 / 补 caps（按实际计数）/ 补 spawnSource
  → structures：ground → meta.groundY；boundary → activityArea.walls（可无损时）；
                 box/ramp/curve_ramp 原样进 structures.solids
  → props：补 behaviour="static"、motion=null、inherit=none（有父件则 full）、
           phase 省略、clipStart=-1、scale 省略、parent=null、rigidGroup=null、
           floatingOk=false；重算 volumeMm3 与 absorbDiameterReqMm
  → defs：补 volumeK（已有则保留）、scalePolicy="uniform"、speedIndex=2、clips=[]
  → motions=[]、paths=[]（v1 没有运动数据；参考关卡由 §11.5 导入器填）
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
| `meta` | `LevelMetaHost`（新，挂在 `LevelContent` 根）+ `LevelArenaHost` | `startDiameterM`、`timeLimitS`、`targetDiameterM`；`ball.start`/`playerSpawn`/`playerStart` 建 `Markers/*`；有 `playerStart` 时再写场景 `VRC_SceneDescriptor` 的 `spawns` 与 `RespawnHeightY` |
| `terrain` | `TerrainRoot`（**新** `TerrainFieldHost`）+ `Chunks/<id>`（**新** `TerrainChunkHost` + `MeshFilter` + `MeshRenderer`） | 球的接触里地形**永远不是** `MeshCollider`，也不进 `LevelArenaHost.staticBoxes`（球不走 PhysX）；只有声明了 `meta.playerStart` 的关卡，才另有一个非 convex、Default 层的 `MeshCollider`，**只给玩家站立**——没有 `visual` 时它与渲染同在 `TerrainRoot/TerrainVisual`，有 `visual` 时在**不渲染**的 `TerrainRoot/TerrainCollision` 上，`TerrainVisual/<part id>` 各一个 `MeshFilter` + `MeshRenderer`（§11.2）；`TerrainFieldHost` 持数组与查询，`LevelArenaHost` 只拿一个引用 |
| `structures.solids[role=box]` | `LevelContent/Structures/<id>` + `BoxCollider` → `staticBoxes` | 与今天逐位等价 |
| `structures.solids[role=ramp/curve_ramp]` | 同上 → `rampBoxes`（弯坡展开成 `<id>_00…` 子物体） | 与今天逐位等价 |
| `activityArea.walls` | **新** `ActivityAreaHost` + 每段一个 `BoxCollider`（关碰撞、只作注册源） | 跨门槛时重注册；不建 Renderer（`visible` 才建） |
| `activityArea.killPlane` | `ActivityAreaHost` 的标量字段 | 每步/10 Hz 查 `ballCentreY < y` → `motor.TeleportTo(ballStart)` + 清速度 |
| `props[]` | `LevelContent/Props/<kind>/<itemId>`（现有）+ `PickupFieldHost` 行 + **新** `MotionHost` 行 | `parent`/`inherit`/`volumeMm3`/`behaviour` 进行为宿主数组；`motion` → `MotionHost` 的 motion 槽；`phase` → 相位桶；`clipStart` → 起始剪辑；`scale` 写 Transform.localScale（仅限合法情况） |
| `motions[]` / `paths[]` | **新** `MotionHost` / `PathHost` 的定长数组（`LevelContent` 根上） | 逐关表，导入期烘成 `motionClass[]`/`params[]`/`pathPointStart[]`/`pathPoint[]`；运行时不读 JSON |
| `defs[].clips[]` | 共享动画预制体/控制器（按类型共享，相位桶可选） | 每个被动画实例一个 Animator（2026-11-16：不预先优化）；`asset` 缺失的剪辑只存证，不实例化 Animator |
| `defs[]` | manifest + 模型 + `collider` 代理 | 与今天一致；`scalePolicy` 只在编辑器校验里用 |
| `cameras`/`renderPresets` | 编辑器/Blender 用，不进场景 | 不变 |

`PlacementImporter` 的 v1、v2 共用 `Props` 空容器清理。导入器新建 kind、zone、`Hierarchy/<父 id>_children` 或 `Groups/<组>` 容器时，加一个仅含 Transform 的 `__PlacementImporterFolder` 子标记，标为 Unity 内置 `EditorOnly`；不改容器路径，也不把标记当拾取候选。清理只自底向上删除仅含 Transform、且只剩这个标记的容器；一遇 prefab 实例就跳过整棵子树（包括道具下 `Skin` 实例的骨骼）。旧容器即使同名也不补标、不猜测删除；拾取同步忽略没有子节点的纯 Transform，不因此报拒绝，但仍报告有碰撞体或其他组件而没有 Renderer 的叶子。导入报告的 `[import] folders:` 分别列出本次新建已标记容器、删除的容器以及保留的未标记空节点；最后一项不表示这些节点都由旧版导入器创建。

参考关卡的 `Skin` 由 `VRC Katamari/Level/Assemble Skeletal Animation (reference)` 管理。任一蒙皮渲染器缺根骨、没有骨骼或有空骨骼引用时，装配器标为 `bones-missing`，用同一 FBX 新建整个 `Skin` 实例，丢弃原实例的删除覆盖；有脚本保护时不销毁，报错交操作员处理。装配后保存，再导入一次 placement 重接 `MotionHost` 的 Animator 引用。

### 11.2 地形网格资产（**我们自己的关卡**）

- 声明了 `meta.playerStart` 的关卡，导入器（`TerrainMeshImporter`，接在地形导入之后）在 `TerrainRoot/TerrainVisual` 上保证网格 + 碰撞体：`MeshFilter` 上已有三角数一致的网格就原样保留，否则按下面的路径生成/原地覆写；`MeshCollider.sharedMesh` 指向该网格，`convex = false`、`isTrigger = false`、Default 层，`TerrainVisual` 世界位姿为单位。参考关卡的网格路径 `Assets/_RefUnlicensed/Meshes/<PascalCase 关卡名>TerrainVisual.asset`（`_ref_cancer` → `RefCancerTerrainVisual`），我们自己的关卡走下面这条。
- 导入器在 `Assets/VRC_Katamari/Generated/Levels/<levelId>/<terrainId>.asset` 生成一个 `Mesh`：
  - 顶点/索引来自 JSON 或侧车；按 `materialSegments` 切 submesh；
  - 确定性：同一份 JSON 在同一 Unity 版本下生成同样字节；
  - **只在文件不存在时 `CreateAsset`，存在时覆写**，绝不删除重建（保住 GUID 与场景引用）；
  - `.meta` 随资产提交；`Generated/` 下的资产是产物，但**提交**（否则别人打开场景是空网格）。
- `TerrainChunkHost` 的数组是**序列化在场景/预制体里的数据**，不是运行时从磁盘读；Udon 运行时不碰 JSON/磁盘（AGENTS 规则）。

**有 `terrain.visual` 的关卡（拆分）**——`TerrainMeshImporter` 把「画的」和「站的」做成两组对象/资产：

- `TerrainRoot/TerrainVisual`（空父物体，单位位姿）下每个 `parts[]` 一个子物体 `<part id>`：`MeshFilter` + `MeshRenderer`，**无碰撞体**。`sky` 块不投/不收阴影。网格资产 `Assets/_RefUnlicensed/Meshes/<Name>Visual_<part id>.asset`（我们自己的关卡走 `Generated/Levels/<leaf>/visual_<part id>.asset`），名字是 `<level>_<part id>@<侧车 sha256 前 8 位>`，**名字、顶点数、三角形数、子网格数都对得上才保留**，否则原地覆写（GUID 不变）。顶点/法线原样，UV 的 `v → 1−v`，索引原样（绕序已是 Unity 的）。
- 贴图：把 PNG 逐字节复制到 `Assets/_RefUnlicensed/Textures/<Name>/<file>.png`（sha256 先对账，不符拒绝），`TextureImporter` 设为 Default、**无 mip、wrap Repeat、filter = `visual.filter`、sRGB、Uncompressed**、`alphaIsTransparency = blend≠opaque`。材质 `Assets/_RefUnlicensed/Materials/<Name>Visual_<id>.mat`：`opaque` → `Legacy Shaders/Diffuse`，`cutout` → `Legacy Shaders/Transparent/Cutout/Diffuse`（`_Cutoff 0.5`），`alpha` → `Legacy Shaders/Transparent/Diffuse`；内建 shader、背面剔除，和原作一样。缺失才建、不一致才原地改，绝不删了重建。
- 接触网格资产保留：旧的 `…TerrainVisual.asset`（拆分前它既画又站）用 `AssetDatabase.MoveAsset` **移到** `…TerrainCollision.asset`（GUID 不变）；`TerrainRoot/TerrainCollision` 上只有 `MeshCollider`（非 convex、Default 层、非 trigger），**从不渲染**。旧场景里 `TerrainVisual` 自己身上的 `MeshFilter`/`MeshRenderer`/`MeshCollider` 被摘掉。接触网格与视觉网格的「保留 / 覆写」判据**各比各的**：接触网格比三角形数，视觉块比名字 + 顶点/三角形/子网格数。
- 没有 `visual` 的关卡行为完全不变（上一条的单网格路径）。没有 `meta.playerStart` 的关卡有 `visual` 时只建视觉、不建 `TerrainCollision`。
- 第二次导入必须 `+0 ~0 -0`（视觉对象、网格、贴图、材质、接触网格逐项比较，不同才写）。

### 11.3 参考关卡的地形（**未授权**）

- 路径：`Assets/_RefUnlicensed/Levels/_ref_cancer/Terrain.asset`（`.gitignore` 已排除整个 `_RefUnlicensed/`）；场景同样放在 `_RefUnlicensed/` 下。
- 导入器只在 `meta.source.kind=="reference"` 时走这条路径，并强制检查：
  1. 关卡目录匹配 `Docs/Design/Levels/_ref_*`；
  2. 网格来源路径在仓库外（`~/KatamariRef/...`），不接受仓库内文件；
  3. 目标路径以 `Assets/_RefUnlicensed/` 开头；
  4. 写入前打 `X-Unlicensed-Reference`/README 标记，编辑器 UI 显示红黑横幅（沿用 leveleditor 的约定）。
- **参考地形永不进 git、永不进上传**；`Resources`/`StreamingAssets` 里也不许出现。

### 11.4 幂等与「第二次导入 +0 ~0 -0」

- 导入键：结构/地形/墙/运动/路径按 `id`，道具按 `itemId`；组容器按 `rigidGroup`/父 `itemId` 派生。
- 位姿比较用变换矩阵（位置 1 mm、旋转点积 0.99999），尺寸/材质逐项比较；只有不同才写。
- 新增组件（`TerrainFieldHost`/`ActivityAreaHost`/`LevelMetaHost`）第一次导入计 `+N`（结构更新），第二次必须 `+0 ~0 -0`。
- 地形数组按「内容哈希」比较：sha256 相同就跳过重写，避免每次导入都算 100 KB 数组 diff。

### 11.5 参考关卡的导入链路（提取器输出 → 本地只读关卡）

```
~/KatamariRef/noclip_export/kse_out/
  instances.json     → props[]（平移/轴角→四元数/缩放/父/显示区）
  out/stage.glb      → terrain.visual（视觉网格 + 贴图，`Tools/levelbuild/ref_stage_visual.py`；不参与碰撞）
  collision_area2.json → terrain（顶点/索引/AABB/zone）
  zones.json         → meta.zones / killPlane 候选
        │
        ▼  Tools/levelbuild/ref_level_import.py（**待写**；仓库内代码，只读仓库外数据）
Docs/Design/Levels/_ref_cancer/placement.model.json + terrain/*.katerrain
        │
        ▼  PlacementImporter（reference 分支）
Assets/_RefUnlicensed/Levels/_ref_cancer/…
```

- 适配器**不解析游戏二进制**（那件事在仓库外，见 §19 许可更正）；它只读提取器已经导出的 JSON/GLB 并做坐标/绕序/字段映射。代码可以进仓库，因为它不衍生自 noclip 源码，也不含游戏数据。
- 映射校验（导入器自检，不通过就拒绝）：`524` 实例、`90` 条真父子、`129` 个带运动程序、`153` 个带动画、`68` 个 AABB、`3,456` 三角形、`7,154` 顶点、3 个有效图形扇区、`7` 个模型。数字对不上说明提取器版本变了，**不猜**。
- **运动映射**：`instances.json` 的 `motion{id,action,speed,pathIndex}` → `motions[]`；`motionID/action` 到 13 类的映射表来自 `motionActionTable`，**未识别的一律写 `null` 并进报告**，不按名字猜。`playAnimNo` → `clipStart`（提取器若没带这个字段，`clipStart` 留 −1 并在报告里记 `missing_playAnim`）；骨骼剪辑本身没有导出（noclip 不导出动画数据），所以 `defs[].clips` 只写段数与 id，**没有 `asset`**，运行时不能播，只能作为后续重建的规格。
- **路径映射**：`pathIndex ≥ 0` 的 11 个 Cancer 实例目前**没有点表**；导入报告写 `paths_missing`，这些实例临时降级为 `behaviour:"static"` 或 `sporadic_walk`（二选一必须在报告里逐件列出，不能静默）。
- 参考导入的出生点：能在本关地形/道具上量出可玩点就写 `spawnSource: "authored"`，量法与数值写进 `arena.notes`（`_ref_cancer` 如此，选点工具 `Tools/levelbuild/spawn_reach.py`，规则见 ORIGINAL_TERRAIN §2.4）；只能取场心才写 `fieldCentreGuess`。`notes` 保留 `src_y`。

---

## 12. 容量与预算（511 / 130 / 4000）

### 12.1 电机的既有容量（v1，作为基线）

| 资源 | 容量 | v2 后 |
|---|---|---|
| 平面 | 8 | 不变（活动区主边界 + 兜底） |
| 静态盒 | 8 | 不变（structures 的 `role=box`，接触 v2 后并入 256） |
| 斜面 | 4 | 不变（`role=ramp`；接触 v2 后并入 256） |
| 静态 OBB 总表 | 256 | 不变（structures + activity 墙 + 道具近邻共享）。**已知缺口（2026-11-16）**：`structures.solids` 的接触形状是拟合代理，但静态表只认 OBB，所以参考关卡那 **18 个凸包固体被装箱成 OBB**；真凸包/球/胶囊要新的静态非 OBB 接触通道（`PARITY_AUDIT` 的「静态固体的接触形状」行） |
| 挡路盒 / 道具支撑 | 6 / 6 | 不变 |
| 接触 | 4（v1）/ 8（v2） | 地形最多占 2（v1）/ 4（v2），剩余给静态/道具；超了按距离截断并计数 |
| 地形 | **新增** | 32 chunk / 128 broad box / 每步 48 三角形窄相；地形接触计入上面的总接触预算（v1 2 / v2 4） |

### 12.2 规模表

| 关卡 | 件数 | 地形三角形 | 侧车大小 | 建议 chunk | `PickupFieldHost` 分片 |
|---|---:|---:|---:|---:|---|
| `story_01_bench_size` | 130 | 0（平地） | 0 | 0 | 1（容量 512） |
| `_ref_cancer` | 511 | 3,456 / 7,154 顶点 | ≈113 KB | 7（每块 ≤512） | 1（容量 512） |
| 未来大关 | 4,000 | ≤8,192 | ≤250 KB | 16 | **8**（每片 512） |

**路径点预算与简化规则（已定，2026-11-16）**：`paths[].points` 上限 **64 点**——这是引擎容量（导入后进 `MotionHost` 的定长数组，§6.7.4），不是审美选择。原作折线很密（`p15` 137 点、`p23` 134 点），转换时用 **Douglas–Peucker** 简化，要求**最大偏差 ≤ 2 cm**，并在每条 path 的 `notes` 里记「原始点数 / 容差 / 实测最大偏差」。实测两例：`p15` 137 → 59（容差 0.013 m，实测偏差 0.0106 m）、`p23` 134 → 50（容差 0.005 m，实测偏差 0.0050 m）。**这是已定的做法，不是待选**；以后若需要原始精度，就改 schema 上限并重新生成折线，届时应单独记，不要悄悄把 64 抬大。

### 12.3 运动与动画预算

| 项 | 容量 | 依据 |
|---|---:|---|
| `caps.movingProps` | 每关动件数（Cancer 129） | 运动宿主并行数组的容量；`props` 仍是总件数 |
| 运动更新 | 30 Hz（`hop`/`whack_a_mole`）、15 Hz（`sway`/路径/随机游走）、10 Hz（`bob`/`clouds`）、5 Hz（`spin`/`flip`/触发检测）、0 Hz（attached/static/纯表现） | 按位移定（`ORIGINAL_MOTION_PROGRAM` §12.0）；全量 1,810 次/秒 = 12,420 基线的 14.6%，50 Hz 定步可直接承载 |
| `caps.animators` | **统计字段**（Cancer = 153），不是 64 硬预算 | 2026-11-16 拍板：153 个骨骼实例直接挂普通 Animator/蒙皮，不预先优化；`caps.animators` 只记录数量，供 `[anim_budget]` 对账 |
| 相位桶 | 8 | `floor(phase × 8) % 8`；桶内同步是可接受的产品取舍（原作相位来源未知） |
| 每关 `motions`/`paths` | ≤64 / ≤32 条，每条路径 ≤64 点 | 129 个动件只有 **15 个 `moveType`**（含 `dragged_along`）；Cancer 落成 **14 条 `motions` + 4 条真路径**（11 个实例引用），简化规则见 §12.2 |

**为什么现在是逐实例 Animator**：2026-11-16 拍板不预先优化——参考关卡是 2018 年的 REROLL 复刻（PS2 时代原作），性能要求极低；「同帧同相看起来机械」用相位/相位桶解决，不靠砍组件数。**为什么不是逐件曲线**：原作是「行为 + 速度档 + 运行时算」，逐件曲线既不能按类型调速，也无法复用逻辑（§6.7.1）。

### 12.4 4000 件怎么装下

- `PickupFieldHost` 的并行数组按 **512 行/宿主**切分，最多 8 个宿主；`itemId`→`(host,row)` 的映射表由导入器生成，存在 `PickupFieldRouter`（512×8 的表，16 KB）。
- **父指针是关卡全局的**：`PropGroupHost` 持 `int[] propParent`（4000 × 4 B = 16 KB）+ 待复检队列（4000 × 4 B + 4000 bool）+ 状态/速度数组（各 16 KB）；与 `PickupFieldHost` 的三态只在吸收事件同步（PROP_BEHAVIOURS §10.9）。
- **空间分桶**：4000 件每步全扫不可接受；`PropSpatialHost` 用 4 m XZ 网格（CSR，`int[] cellStart` + `int[] cellProps`），每步查 9 格。511 件以下沿用现有近邻选择，不需要分桶。
- 这三件（router/group/spatial）是 **P4 的交付**，不是 v2 schema 的前置；v2 数据本身在第 130/511 档就已完整可用。

---

## 13. 引擎计划：自写运动学电机里的地形网格接触

### 13.1 分阶段

| 阶段 | 做什么 | 不做什么 | 前置 | 退出判据 |
|---|---|---|---|---|
| **T2 分类 + 连续** | θ 分类（ride ≤45°；climb θ>45° 且法线朝上，带速度门；block 法线不朝上）；swept sphere vs triangle 防隧穿；地形接触并入 CONTACT_MODEL_V2 的求解（flag 打开） | 不做材质摩擦/音效 | 接触 v2 S3/S5 | 4 档球径矩阵：C21–C24 全绿；2.2 m 球不把 0.6 m 台沿当墙；4 m/s 不穿 0.1 m 薄板 |
| **T3 网格 + 流式** | 导入期烘 CSR 网格；chunk 按球位置启用/禁用；>8,192 三角形拆多个关卡/区域 | 不做 LOD 网格 | T2 | 3,456 三角形参考地形每步 ≤48 三角形窄相；chunk 切换无单帧跳变 |
| **T4 打磨** | 材质 id（坡度/摩擦提示）、诊断计数、成本读数进 `Docs/Evidence/` | 不改玩法 | T3 | profiler 读数与离线模型误差 <20% |

### 13.2 地形出现后的规则

- 陡三角形必须挡球：由接触求解统一处理（地形三角形与盒、斜板同一套接触，不单列路径）。
- 支撑取「球底以下」的接触，悬垂下不会把天花板当地板；头顶接触单独推出。
- 台阶、坡、墙用一条接触驱动的连续爬升：`h<r` 棱滚越、`h<H_cap` 连续爬（`H_cap = 1.5·D`）、`h≥H_cap` 墙（TERRAIN_CLIMB §6）。
- 面 id = broad box；同一框内三角形共享。

### 13.3 每步成本模型（基线：8 平面 / 8 盒 / 4 坡 / 256 OBB）

粗略 flop 口径，用于排序而不是承诺；真正读数要 Unity profiler（Udon 解释器开销和数组访问次数才是主项）。**地形不占 8 平面 / 8 盒 / 4 坡 / 256 OBB 的任何槽位**（它有自己的表），只在接触总数（v1 4 / v2 8）里占 2 / 4；所以这四张表的既有预算不被侵蚀，代价全部落在查询循环与数组读取上。

| 项 | 次数/步 | flops/步（估） | 说明 |
|---|---:|---:|---|
| v1 基线（1 地面 + 8 平面 + 8 盒投影 + 4 坡 + 6 道具支撑） | — | **≈1.6k** | 上一版估算，未实测 |
| 地形 broad AABB（以 Cancer 的 68 条为上限示例） | 68 | ≈1.4k | 球-AABB：3 减法 + 3 比较 + 早退 |
| 地形 chunk 粗筛（≤32） | ≤32 | ≈0.3k | 只在跨 chunk 时 |
| 地形窄相（无网格） | 2–6 框 × ~51 三角 | **9k–28k** | 有 y 带早退；这是无网格时的上限 |
| 地形窄相（T3，1 m 网格 3×3） | 2–6 框 × 9 格 × 2–4 三角 | **≈2k–6k** | 球心 3×3 格；三角形去重后更少 |
| 球-三角形最近点 | 每个候选 | ≈90 | 面/棱/角三分支 |
| 穿透推出 + 去速 | ≤4 接触 | ≈0.4k | 与现有接触同一量级 |
| 接触 v2 现状（24 近邻 OBB，≤8 接触） | — | ≈4k | 对照：地形 T3 与它同量级 |

**结论**：无网格在 3,456 三角形上可能到 3 万 flops/步（50 Hz ≈ 1.5 Mflops/s），对 Udon 偏高但未必不可；**网格把窄相压到 2–6k，是必做项**，不是优化。流式 chunk 的切换成本（每 0.5 m 或 0.25 s 一次）应摊到 **<100 flops/步**。

### 13.4 新测试场格位（PhysicsTestField）

现有 21 个规划格只到 C20，9 格已落地。新增 4 格，沿用「一格一 `placement.json` + `physics_test_field.json` 判据」的机制：

| 格 | 名称 | 几何 | 判据（D = 0.30/0.60/1.10/2.20） |
|---|---|---|---|
| **C21** | `stairs` | 3 级台阶：踢面 0.30 / 0.60 / 0.95 m，踏面 2.0 m，宽 3 m，台面 1.85 m | `climbed`（`progress ≥ 4.7`）：0.30 球停在第一级、0.60 球停在第二级（0.95 > 0.90 = 它的 H_cap）、1.10 / 2.20 上台面；`support_gap ≤ 5 mm`；`grounded_frame_dy ≤ 60 mm`（不是一帧弹上去）；`support_kind == 6`（地形） |
| **C22** | `gully` | 6×3 m 沟，深 1.0 m，四周直壁，台唇延伸到单元边缘 | 0.30 / 0.60 顶在远壁（`progress ≈ 4.35`、`max_center_y ≤ 0`）；1.10 爬出（H_cap 1.65 > 1.0，约 2 s 爬 + 行程）；2.20 的 `h<r` 直接滚过；`rest_gap ≤ 5 mm`、`grounded_frame_dy ≤ 60 mm` |
| **C23** | `tunnel_overhang` | 3 m 宽 × 1.2 m 高隧道穿过 1.5 m 厚土堤；堤顶可站；旁挂 2 m 长悬垂檐（底面 0.8 m） | 球从隧道下过：不被顶起（`centre.y` 变化 < 2 cm）；头顶不穿（`max_penetration ≤ 0`）；站上堤顶 `y = 1.2 + r`；从檐边掉下时 `fall_frames > 0` 且落点 = `r`；面 id 在顶/底切换时 `support_id` 变化，不触发传送 |
| **C24** | `curb` | 0.60 m 路缘，宽 0.90 m；地面延伸到单元边缘 | `stopped`（`progress ≤ 2.6`）：0.30 球被挡（H_cap 0.45 < 0.60）；`climbed_curb`（`max_center_y ≥ 0.80`）：0.60（0.90）/1.10（1.65）爬过、2.20 的 `h<r` 直接滚过；`rest_gap ≤ 5 mm`、`grounded_frame_dy ≤ 60 mm` |

**共同要求**：每格 4 个球径档、判据可自动判定、失败要有具体读数（`support_gap_m`/`max_center_y_m`/`progress_m`/`grounded_frame_dy_m`），并进 `Docs/Evidence/PhysicsTestField/`。四格要同时进 `physics_test_field.json` 的 `cells`/`planned_cells`（几何、激活矩形、判据、驱动时间线各一份），并沿用现有跨文件一致性测试；只写 `placement.json` 不算落地。**C23 是「地形必须三角网」的活证据**：高度场做不出隧道。

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
| three.js 视图、点选/框选、移动/旋转 gizmo（yaw + 支撑重坐）、行走、球径阶梯、区域统计、校验面板、参考关卡只读 + 父子连线 | 地形网格查看、碰撞 AABB/三角形叠加、真四元数旋转、`parent`/`support` 连线与派生预览、`volumeK`/`volumeMm3` 编辑（进行中）、活动区 gizmo、错误码面板、运动预览循环、路径 gizmo |

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
| E9 | 运动预览循环 | 选中动件后播放/暂停/拖时间轴（0–10 s），显示 class、速度（cm/s）、周期、相位桶；Sway/Bob/Hop/Whack 直接由浏览器按参数算，不依赖 Unity；`phase` 滑条与「重派生」按钮 | **M** | E3（位姿）+ E4（父链）+ §6.7 表 | 8 |
| E10 | 路径 gizmo | 在地面画/拖折线点，设 `loop`（loop/ping_pong/once）与 `speedCmPerSec`；沿路径走一个小球标记预览；保存为 `paths[]` 并让 `motion` 引用 | **M** | E9 + §6.7.4 校验 | 9 |
| E8 | 4000 件浏览 | 虚拟化道具列表、按区域/kind 过滤、分片指示 | **M** | §12.4 分片方案 | 10（可与 E5–E7 并行） |

**顺序理由**：E1/E2 是「数据正确」的地基，先做；E3 改变旋转语义，必须在 E4/E5 之前（否则连线/碰撞叠加都建在 yaw-only 的位姿上）；E5 依赖侧车格式冻结；E7 放在 E6 后，因为墙的门槛与球径阶梯共用同一套可视逻辑；E9 依赖父链/位姿，E10 依赖 E9 的运动预览。**每个 GUI 操作都要有 CLI/HTTP 等价 op**（AGENTS 的编辑器 parity 规则）：至少 `set_motion`、`set_path`、`preview_motion` 三个 op，浏览器只负责画。

### 14.3 参考关卡的编辑器路径

- 现有 `--level _ref_cancer --ref-models-dir ~/KatamariRef/models` 机制保留：只读、红黑横幅、父子连线（`group`/`groupId`）。
- v2 参考关卡（`placement.model.json`）额外支持：地形侧车（只读、分块）、碰撞叠加、`parent` 连线（90 条真父子）、`rotationQuat` 真实朝向（401 件）。
- **只读守卫不变**：`add_prop`/`set`/`move`/`remove`/`export`/`undo`/`redo`/`batch` 全部拒绝；文件一个字节都不写；模型响应带 `X-Unlicensed-Reference: 1`。

---

## 15. 分阶段迁移

| 阶段 | 内容 | 交付 | 门（不过不进下一阶段） |
|---|---|---|---|
| **P0 文档与 schema 冻结** | 本文、`*_KEYS` 草案、错误码表、决定编号索引（§16） | 本文 + 评审 | 字段名不再改 |
| **P1 校验器 + 转换器** | `placement_core` v2 读写、13 份转换、`check` 0 错、`gate_changes.csv` | 工具 + 测试 | 13 份转换；v2 再转 no-op；`python3 -m unittest discover -s Docs/tests` 全绿 |
| **P2 Unity 导入（无地形）** | `PlacementImporter` v2：meta/structures/activity/props；地形字段读入但不建网格 | 导入器 + 场景 | 13 份重导入第二次 `+0 ~0 -0`；`volumeMm3`/`Req mm` 回读一致 |
| **P3 地形数据与视觉** | 侧车读写、`TerrainFieldHost`/`TerrainChunkHost`、Mesh 资产生成、地形支撑 | 数据 + 组件 | C21/C24 通过；地形上站得住；参考地形 3,456 三角形加载成功 |
| **P4 地形接触** | 穿透/天花板、θ 分类、连续检测；C22/C23 | 电机地形层 | C21–C24 四档全绿；4 m/s 不穿 0.1 m 板 |
| **P5 行为、运动与规模** | `parent`/`inherit`/`rigidGroup`/`scale` 运行时；子件坠落；`MotionHost`（10/15 Hz 队列 + 相位）；路径 host；共享剪辑/相位桶；4000 分片与空间桶 | 宿主组件 | 90 条父子关系在 Cancer 参考关卡跑通；129 个动件预算 ≤16%（15 Hz）；153 个被动画实例照原作挂 Animator（不做 VAT，2026-11-16）；4000 件合成场景不掉帧目标（先测再定） |
| **P6 编辑器** | E2–E8 | 编辑器 | 各功能有截图/冒烟；参考关卡只读不变 |
| **P7 收尾** | 删 v1 读取器；`OPEN_DECISIONS` 状态更新；证据归档 | 清理 | 仓库无 v1 写入路径；全部测试绿 |

**并行约束**：P1 不能与 P2 合并（先证明 JSON 形状稳定，再动 Unity）；P3 的地形支撑不等 P4；P5 的 `parent` 运行时必须等 `PROP_KEYS_V2` 与校验器同一批（未知键硬错误）。

---

## 16. 决定编号（D-n）

> 代码、测试与其他文档按 `D-n` 引用下列取舍；owner 的拍板记录在 [OPEN_DECISIONS](../../OPEN_DECISIONS.md)，本节只写现行口径。固定输入：地形支持 3D 表面（三角网 + 空间加速，高度场只作优化）、解析器照规格重实现在仓库外、吸收门按原作 `k × bbox`、`volumeMm3` 定点、父件被吃子件坠落（单父指针）、每件四元数、碰撞代理按型、不做击飞/甩脱。D-2 / D-3 / D-7 是设计取舍，尚未由 owner 单独确认。

| # | 主题 | 现行口径 | 影响面 |
|---|---|---|---|
| **D-1** | 球径上限 | **全局 400 m**（owner 2026-11-18）；所有地图适用，增长由 `SizeModelMath` 饱和 | `KatamariBallMotor.GlobalMaxBallDiameterM` |
| **D-2** | 地形存储 | 内联保可读；>512 三角形走 `.katerrain` 侧车并钉 sha256；参考关卡用同一格式 | JSON/侧车、编辑器、导入器 |
| **D-3** | 地形水平阻挡 | 支撑 + 陡面挡（θ 分类），接触求解统一处理滚/爬分类与爬升；需要硬阻挡的地方用 `structures` slab 补 | 电机、测试场、关卡设计 |
| **D-4** | 活动区越界 | 硬墙 + 球径门 + 每关 kill plane；玩法范围主要靠地图布局限制；不做软边距 | `activityArea`、电机、关卡设计、HUD |
| **D-5** | 逐件缩放 | 四层，见 §6.4：①运行时永不缩放；②作者态均匀缩放全允许（含挂接件）；③非均匀 + 有代理只允许无父、static、box/sphere；④非均匀 + 无代理（def 无 `collider`）允许无父、static、`absorbable:false`（原作唯一实例 `sparrow_001`） | props、导入器、吸收门 |
| **D-6** | zone id | 不透明标签，只做标签/统计（`killPlane.zoneId` 只记诊断）；kill plane 是独立字段、逐关 Y，运行时由回合状态机当重启阈值执行，没有回合状态机的世界退回 `ActivityAreaHost` 兜底 | 地形、活动区、回合状态机、统计 |
| **D-7** | ramp/curve_ramp/box | 保留解析体；能解析体表达的不进网格，只有曲面/悬垂才用地形 | 引擎、数据、编辑器 |
| **D-8** | 参考关卡数据 | 只留 `~/KatamariRef`；导入器只写 `_ref_*` 与 `_RefUnlicensed`；任何网格/贴图/BIN 永不入库 | 导入器、git 守卫 |
| **D-9** | 仓库许可 | 未定（见 §19 与 OPEN_DECISIONS）；不 vendor 第三方代码；移植 MIT 代码须保留版权与许可声明；首选照规格重实现 | 全仓库 |
| **D-10** | 运动保真度 | 13 类运动数据与运行时都进第一版；没测到参数的类（`wait_for_player`/`whack_a_mole`/`clouds`/`spin`）用 PROVISIONAL 默认并在诊断里标记（U9） | schema、`MotionHost`、编辑器、测试 |
| **D-11** | 骨骼动画 | 153 个骨骼实例直接挂普通 Animator/蒙皮，不做 VAT、不按 64 截断；`caps.animators` 是统计字段；真测出瓶颈再评估（§16.2） | 导入器、`MotionHost`、美术 |
| **D-12** | 逐实例相位 | `itemId + level.id` 的确定性哈希，作者可用 `phase` 覆盖；说明见 §16.2 | 导入器、运行时 |
| **D-13** | 舞台动画 | v1 不做；「noclip 没做」不能证明原作没有；5 朵云是 `moveType 16` 的道具（不是舞台），参考实现的风车/转灯是手写补的、不是数据给的 | 美术、舞台 |
| **D-14** | 移动件被吸后子件的运动 | 下落期间挂起子件自己的运动，`SETTLED` 后恢复；不 carry | `MotionHost`、行为宿主 |

### 16.1 未验证项（不因本文定稿而变成已验证）

| # | 未验证 | 为什么必须验 | 验法 |
|---|---|---|---|
| **U1** | Udon 行为之间传数组（`Vector3[]`/`byte[]`）、`byte[]` 序列化、`long`/`long[]` | 地形查询与 `volumeMm3` 都依赖它；U# 没有 List/jagged array | Unity 里各写一个最小 `UdonSharpBehaviour` 试探，`refresh_unity` 看编译 + Play Mode 读回；验完删掉试探物 |
| **U2** | 生成 Mesh 资产的确定性、GUID 稳定性 | 重新导入不能换引用，否则场景丢网格 | 同一 JSON 生成两次比 sha；改一条顶点再看 GUID 是否变 |
| **U3** | `TerrainFieldHost` 每步成本 | §13.3 全是 flop 估算；Udon 解释器开销可能高一个量级 | profiler + `GetLastStepMicros()` 读数进 `Docs/Evidence/` |
| **U4** | 校验器的 OBB 互穿、`scale` 作用到 collider 后的代理包围 | 现在校验器用世界 AABB 粗筛；旋转/缩放接进来后必须换 OBB，否则误报会变多（参考关卡今天 26 个 AABB 互穿） | `Docs/tests/levelbuild` 加 OBB 用例；参考关卡跑一遍看报错是否收敛 |
| **U5** | 运动相位的原作来源（速度已实测：1 单位 = 30.3 cm/s） | `Math.random()` 是 noclip 的，不是原作；`phase` 用哈希是**我们的选择** | 原机录像/内存采样；先把确定性哈希跑通，再把 `phase` 作为作者覆盖 |
| **U6** | `locPosType` 是否表示随机摆放 | 若是，固定摆放就差一条「每局位置随机」的规则 | 参考实现源码 + 同关卡多次加载对比 |
| **U7** | `shakeOffFlag` 是否等于「可甩脱」 | Cancer 只有 3 个为 1；不能拿它实现击飞 | 查原机/攻略；在证实前只存证 |
| **U8** | 舞台动画是否存在 | noclip 没做舞台动画，不能反证原作没有 | 原机录像；在证实前不做 |
| **U9** | Spin 角速度、WhackAMole 驻留与完整循环、Clouds 绕圈半径 | 这些数值没测到，运行时只能用 PROVISIONAL 默认 | 拉长细步序列到完整周期 |

**对这些开放问题的建议**：`phase` 用确定性哈希并允许作者覆盖（D-12）；`locPosType` 先按**固定摆放**导入，保留原值到 `notes`，不实现「每局随机」（等证据）；`shakeOff` 只存证、v1 不读（已定不做击飞）；舞台动画 v1 不做（D-13）；Spin/Whack/Clouds 用 PROVISIONAL 默认并在诊断中标记，试玩后再调；`wait_for_player` 的触发半径已定（2.0/3.0，见 §6.7.3 与 `OPEN_DECISIONS`），不用再测。

### 16.2 D-11 / D-12 的记录（研究回填 + 白话）

**D-12：什么是「相位」，从哪来**。一件摇摆的风铃、一只蹦的螃蟹，动作按周期重复；**相位 = 这件从周期的哪一刻开始**。两只一模一样的螃蟹，相位 0 与 0.5 会一只抬左腿、一只抬右腿。原作逐件记录里没有这个值（`playAnimNo` 是「开局放第几段剪辑」，不是相位），所以我们不能照抄，只能选一个：

- 用 `itemId + level.id` 做**确定性哈希**派生 → 同一关卡、同一件道具每次进世界都是同一个值（账本、双端、重放/截图一致），不同件之间看起来是散的；
- 作者觉得不好看，就写 `phase` 覆盖；
- **不用 `Math.random()`**：每次进世界都不同，同一件道具两次加载动作不同步，回放与对拍都会飘。原作的种子来源仍是 U5（未验证）。

**D-11：不预先优化。** 参考关卡是 2018 年的 REROLL 复刻（PS2 时代原作），性能要求本身极低；现阶段完全不必担心 Animator 数量、顶点数、骨骼数。**153 个带骨骼动画的实例直接用普通 Animator + 骨骼蒙皮**，不做顶点动画纹理（VAT），也不按 64 个砍。

| 事实 | 数 |
|---|---|
| 带骨骼动画的实例 | **153**（Cancer 524 的 29.2%） |
| 来自多少型 | **19**（每型 4–6 段剪辑，按运行时 `AnimationType` 切换） |
| 其中 3 个螃蟹型 | 小招潮蟹 66 + 招潮蟹 43 + 椰子蟹 11 = **120 件（78%）**；每型 **140 三角**、256² 贴图、1 材质 |
| 剩下 | **33 件 / 16 型** |
| 整关规模 | 511 件、**59,056 三角**（实例加权；最大单型 356） |

所以当前设计只有一句话：**每个被动画实例一个 Animator，控制器/剪辑按 19 个类型共享，`caps.animators` 记 153 只作统计。** 共享剪辑省的是资产，不是组件；相位桶（可选）解决的是「同帧同相看起来机械」，是表现手段，不是性能手段。

**这条放松的是什么、不放松什么**（写清楚，免得以后再问一遍）：

- **放松**：不再为「153 个 Animator / 153 次 skinned draw call / 1.5–3 千骨骼 Transform」做任何预先设计取舍——先照原作挂满，跑起来再说。
- **不放松**：后续**真实测量**照旧要做（P5 的 4000 件合成场景、真机帧率、Quest 实机），测出瓶颈再谈优化。
- **不放松**：`defs[].clips` / `props[].clipStart` / `phase` 三个字段照旧，数据不丢。

**「以后真测出瓶颈再看」的备选（现在不做，只留账）**：

| 备选 | 前提 | 账（我方估算，仅供将来判断） |
|---|---|---|
| 3 型螃蟹改 VAT（顶点动画贴图） | 真测出 153 个 Animator 拖帧，且螃蟹仍是多数 | 140 三角 ≈ 100 顶点；4–6 段 × 60 帧 ≈ 0.1–0.4 MB/型，3 型 ≈ 0.3–1.2 MB（8 相位桶再 ×8 ≈ 2.4–9 MB）；0 Animator、0 蒙皮 |
| 按球距截断（只给近处挂 Animator） | 同上，且远处动画看不出差别 | 需要「离球近 / 有 `clipStart`」的优先级规则；远处退化成静态网格 |
| v1 不播骨骼（只保数据） | 连跑都跑不起来时的兜底 | 数据不丢，画面少一层；记一条 `PARITY_AUDIT` 分歧 |

「idle Animator 在 100–200 个时可忽略」有社区实测（[SO 74951690](https://stackoverflow.com/questions/74951690)，只算状态机、不算蒙皮与 draw call）；**这只是旁证，不作为当前设计的依据**——现在的依据是 owner 的性能口径：先还原，后优化。

---

## 17. 世界与场景结构：一个世界 = 一张场景（多地图怎么放）

> 本节回答 owner 2026-11-16 的问题：「VRChat 的 Unity SDK 能不能一张世界多张场景？我们的地图是不是都得塞进主场景、按需加载？」结论：**上传的世界只有一张场景；运行时不能加载第二张；「按需」只能是启用/停用同一张场景里的根物体，或者换世界。** 事实按出处分级，推断标【推断，强】。

### 17.1 事实

| 问题 | 结论 | 出处 |
|---|---|---|
| 上传的是哪张场景 | **Unity 当前打开（活动）的那张**。SDK 的世界构建入口是 `VRC_SdkBuilder.ExportSceneToV3()` → `RunExportSceneToV3` / `RunExportAndUploadSceneBlueprint`，内部按 `GetActiveScene()` 取场景；一张场景一个 `VRC_SceneDescriptor`，出现第二个就报 `A Unity scene containing a VRChat Scene Descriptor should only contain one Scene Descriptor.` | SDK 3.10.5：`worlds/Editor/VRCSDK/SDK3/VRCSdkControlPanelWorldBuilder.cs`、`VRCSdkControlPanelWorldBuilderV3.cs`；`worlds/Runtime/VRCSDK/SDK3/` 的 `VRC_SceneDescriptor`；[VRC_SceneDescriptor 文档](https://creators.vrchat.com/worlds/components/vrc_scenedescriptor/) |
| 能不能在编辑器里叠加（additive）第二张场景 | 编辑器里能打开，但**导出的是活动场景那一份 bundle**，叠加进来的场景内容不进世界【推断，强：导出 API 只接收活动场景，构建产物是一份 bundle】 | 同上；官方「一张场景一个 descriptor」 |
| Udon 能不能运行时 `LoadScene` | **不能**。Udon 的 API 是白名单（UdonSharp 编译器 `CompilerUdonInterface.IsExposedToUdon` 只放行 node definition 表里的签名；官方 [Class Exposure Tree](https://creators.vrchat.com/worlds/udon/udonsharp/class-exposure-tree/) 就是这张表），白名单组件里没有场景加载器；客户端加载世界是**单场景**加载（`UdonManager.OnSceneLoaded` 遇 `LoadSceneMode.Single` 会清空 Udon 目录再注册），加载世界是客户端的事、不是脚本的事 | SDK 3.10.5：`worlds/Runtime/Udon/UdonManager.cs`、`worlds/Integrations/UdonSharp/Editor/Compiler/Udon/CompilerUdonInterface.cs`；[白名单世界组件](https://creators.vrchat.com/worlds/whitelisted-world-components/) |
| 能不能运行时下载资源（Addressables / AssetBundle / 模型 / 预制体） | **不能**。允许的运行时外部内容只有**图片、字符串、视频、音频串流**（`VRCUrl` + 域名白名单）；UdonSharp 的 `Instantiate` 只能传 `GameObject`，文档明说传 `Material`/`ScriptableObject` 编译失败。官方文档里没有 Addressables 这一路 | [External URLs](https://creators.vrchat.com/worlds/udon/external-urls/)、[Image Loading](https://creators.vrchat.com/worlds/udon/image-loading/)、[UdonSharp](https://creators.vrchat.com/worlds/udon/udonsharp/) |
| 多张地图之间怎么跳 | **传送门**：`VRC_PortalMarker`（Udon 可写 `roomId` 再 `RefreshPortal()`），或世界的「World 链接」。跳过去是**另一个世界实例**，不共享任何运行时状态 | [VRC_PortalMarker](https://creators.vrchat.com/worlds/components/vrc_portalmarker/) |
| 对象池 | `VRC Object Pool` 同步的是对象的 **active 状态**，对象仍在同一场景里；省 `Instantiate`/`Destroy` | [Network Components](https://creators.vrchat.com/worlds/udon/networking/network-components/) |

### 17.2 于是只有四种做法（不是五种）

| 做法 | 能做到 | 代价 | 什么时候用 |
|---|---|---|---|
| **(a) 全部塞进一张场景** | 一次加载、零切换、状态随便传 | 进世界加载时间 = 所有图之和；远处内容仍然被加载（只是不渲染） | 都要从房间直达 |
| **(b) 一张场景 + 每个区域一个根物体，按需 `SetActive`** | 「按需」的唯一形式：省 Update/渲染/物理，不省内存、不省加载时间 | 需要在状态机里管「谁能被启用」；禁用中的道具不会被复位逻辑跑到，容易留下脏状态；本项目还有三条硬阻断（§17.4） | **v1 不做**（见 §17.4 的落地顺序）；多张图同场景、只想让当前图跑脚本时才值得 |
| **(c) 上传成多个世界，用传送门/World 链接跳** | 每张图单独发布、单独审核 | 跨世界状态全丢（分数、球大小、库存）；每张图都要单独上传与验收；portal 有加载 | 某张图要做独立发布节奏 |
| **(d) 对象池（`VRC Object Pool` / 自建池）** | 省运行时 `Instantiate`/`Destroy` 峰值 | 仍在同一场景；池本身要预热 | 大量同类件（碎屑、小蟹）反复生成 |

**没有第五条**：没有 additive 流式加载、没有运行时 AssetBundle、没有「按需下载地图」。

### 17.3 我们今天的做法（事实，不是提案）

`StoryMode` 是**一张场景**：23 个根物体、一个 `VRC_SceneDescriptor`、一个 `PipelineManager`、一个回合状态机 `StoryRound`。

| 岛 | 位置 | 内容 |
|---|---|---|
| **关卡岛（野餐草坪）** | 原点附近，地面 `(-12.7, 0, -12.7)`，结构边界 x∈[−19.4, −6]、z∈[−19.4, −6] | `LevelContent/Props`（11 个 `kind` 分组）+ `LevelContent/Structures`（边界、地面、坡道、树桩…）；入口 `LevelEntry`(−12.5, 0.15, −10.4) |
| **房间岛** | **(1200, 0, 0)** | `HubCenter`、`hub_floor`（8×8 m）、`HubSpawn`(1200, 0.1, −2.8)、每个提供的关卡一块选关垫 `HubPad_<levelId>`（沿 X 居中成排，z=+1.2，由 `Editor/Hub/HubAssembler` 按 [hub_levels.json](../StoryMode/hub_levels.json) 生成，不在 hub 文档里）、四面边界 x/z ∈ ±4 m |

- 两岛中心相距 **约 1213 m**（X 方向 1200 m），**超过 Unity 默认远裁剪（约 1000 m）**，所以互相看不见、听不见；这是「看不见」的经验值，不是性能测量（[HUB_ROOM §11](../StoryMode/HUB_ROOM.md) 未验证第 8 条已写明）。
- 切换 = `VRCPlayerApi.TeleportTo` + 回合状态，由 `HubLevelLauncher` 独占（`Runtime/Hub/HubLevelLauncher.cs`）。**没有 additive 加载、没有加载屏、没有整块 `LevelContent` 的启用/停用**——[HUB_ROOM §7.1](../StoryMode/HUB_ROOM.md) 明确不做，三条理由是：导入器一个场景只有一个受管根 `LevelContent`、`PickupFieldHost.RecordOriginalPoses()` 在 `Start` 里捕获原始位姿（关着就永远捕获不到）、`LevelArenaHost` 一次性注册障碍且没有按岛切换的接口；再加上「关掉没有收益」（同一张场景已经全部加载，1200 m 外本来就不渲染）。
- 玩家跑出关卡岛时不是靠墙拦，而是「回合在跑就必须在岛上」的 hold 逻辑（`holdAttemptLimit` 次修正），这是**比关卡墙更强**的约束，也正是 owner 说的「活动范围靠地图设计」。

### 17.4 建议：多张地图这样放

**推荐 A：一张场景 + 一个关卡一个岛 + 根物体级按需启用**，把 (c) 留成出口。

| 方案 | 适用 | 成本 | 风险 |
|---|---|---|---|
| **A 一张场景，每关一个岛**（推荐 v1） | 房间 + 若干张 10–20 m 见方的图 | 加载时间是所有图之和；岛间距 ≥1200 m 时 **float 精度足够**（1200 m 处约 0.1 mm，10 km 处才掉到 ~1 mm） | 导入器要多受管根（现在只有一个，§17.5）；同一材质/贴图要在图之间共享；将来要按需停用还要另两条前置（§17.4） |
| B 每张图一个独立世界 + 传送门 | 要单独发布/审核、或内容风格完全不同的图 | 跨世界状态全丢；每张图单独上传、单独验收；portal 有加载 | 房间与图之间的「回合」要跨世界重建（球大小、分数都得重算或走存档），玩家体验切成两段 |
| C 混合：房间图 + 大图独立世界 | 只有 1–2 张图真的很大 | 只有在需要时才付 B 的代价 | 两套切换路径（teleport 与 portal）都要测 |

**为什么推荐 A**：(1) 我们的房间/关卡切换已经是 teleport + 状态机，A 不需要新机制；(2) 1200 m 的间距已经验证过「互相看不见」，再加岛就是复制这段距离，模式不变；(3) B 的跨世界状态重建是全新工程，而现在的图都在同一张场景里（参考关 511 件、59,056 三角）。**B 的触发条件**：某张图需要独立发布节奏。

**A 的落地顺序（别一步跨到 `SetActive`）**，因为 HUB_ROOM §7.1 已经把「整块启用/禁用」挡在三件事前面：

1. **v1：所有岛都保持启用，只用回合状态机隔离**——只有当前岛在跑回合（`StoryRound` 的相位与 `HubLevelLauncher` 的闸门），其它岛就是「摆在那儿、没人碰」。这一版不需要新机制，也不碰 HUB_ROOM 的三条阻断。
2. **要省那点 `Update` 时才考虑按需启用**，前置三条缺一不可：① `LevelContent/<levelId>` 多根（导入器现在只有一个受管根）；② `PickupFieldHost.CaptureOriginalPoses()` 变成公开接缝，并在启用后立即调用（否则重试时道具留在地上）；③ 每岛一个 arena host，或一个显式的「当前岛」切换接口（现在 `LevelArenaHost` 一次性注册 8 平面/8 盒，两个岛会抢同一张表）。
3. 启用前必须先复位再开（`ResetField()`），否则再进就是脏的。

### 17.5 对格式与编辑器的要求

- `meta.scene` 已经是字段（这张关卡属于哪张 Unity 场景）；`meta.id` 已经是目录名 → 建议导入器用它当**场景内根物体名**（`LevelContent/<id>`），这样一张场景放多关不会互相覆盖。**今天导入器只认一个 `LevelContent`（`PlacementImporter.ManagedRootName`），多根尚未实现**——这是 A 方案的代码前置条件（不管启用/停用都需要）；若还要按需启用/停用，再加 §17.4 的另两条。
- 每关自带世界偏移：`meta.ball.start`/`playerSpawn` 已是世界坐标，岛的偏移就是这些坐标的一部分，导入器**不重算、不归一化**。
- 编辑器：一次只编辑一关，但要显示「这关在世界里的偏移」；`meta.bounds` 是作者框（查越界、画框），**不是**活动范围（§5）。多图时编辑器需要「岛列表」（读场景根），本轮不做。
- 被禁用的图不参与复位：`SetActive(false)` 的根下，Udon `Update` 不会跑、道具不会复位。**关之前先在状态机里复位**，否则再开是脏的。**v1 因此不做启用/停用**（§17.4 的落地顺序 1）。

### 17.6 不要做的

- 不要指望 additive 场景加载：导出不带第二张场景。
- 不要用 `SceneManager` / Addressables / AssetBundle / 运行时模型下载：Udon 白名单外（图片/字符串/视频/音频除外）。
- 不要把两张图放得互相可见：默认远裁剪约 1000 m，1200 m 是「看不见」的最小经验值；两个岛各放 8×8 m 房间时，改成 X/Z 排开也一样。
- 不要用 `SetActive` 来「省内存」：省不到，只会省 Update/渲染/物理。

---

## 18. Q&A：为什么允许逐件缩放

> 「不缩放」与「允许逐件缩放」不是矛盾：**运行时不许缩放、作者不许改尺寸凑档**这两条照旧；允许的是照抄原作数据里本来就不等于 1 的 6 件缩放。三条分开写。

### 18.1 数据说了什么

PS2 的实例记录是 0x40 字节，`+0x30..+0x38` 三个 f32 就是缩放（[NOCLIP_KATAMARI_FORMAT §3.2](../../Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)）。Cancer 524 件里 **6 件不是 1**：

- **4 件 `0.9`**（无父、同类小尺寸）：忽略 → 体积差 1.37×，吸收门差 1.11×；
- **1 件 `0.1`**（**挂在父件下**，chain 409→410→411）：忽略 → 体积差 1000×，门会要求 10 倍大的球，渲染大 10 倍；
- **1 件非均匀 `(0.0741, 0.0291, 0.0197)`**（无父；语义未验证，按 ④ 处理）：忽略 → 体积差 23,547×，门差 28.7×，每轴大 13.5 / 34.4 / 50.8 倍。

**如果不允许逐件缩放，这三类件只能二选一：要么按 1:1 导入（渲染、体积、门槛全错），要么丢掉（参考关卡少 6 件，其中 1 件是挂接链上的）。** 前者会让「参考关卡与原作对拍」这件事直接不成立，后者是数据丢失——两样都比「多一个 `scale` 字段」贵。

### 18.2 四层规则

| 层 | 现在 |
|---|---|
| ① 运行时缩放 | **不许**：`scale` 已在导入期乘进 `aabb`，运行时只读 `aabb`；父件带子件只搬位姿 |
| ② 作者态均匀缩放 | **允许**：任何 `behaviour`、任何 `parent`，因为原作就有 4 件 0.9 和 1 件 0.1 |
| ③ 作者态非均匀缩放（有代理） | **限制**：只允许无父、static、box/sphere；胶囊/凸包在非均匀下不再是同类形状 |
| ④ 作者态非均匀缩放（无代理） | **允许**：def 没有 `collider` 就没有代理可失真，条件是 `static` + `absorbable:false`；原作那 1 件 `sparrow_001` 正属此层，照写 `scale`、记 `divergences` |

**两条不变**：不许把道具改大改小去凑体积档（②只能照抄原值或写明理由，调体积只能用 `volumeK`）；挂接件在运行时永不缩放（①）。

### 18.4 代价与风险（写清楚，免得以后再解释一遍）

- **挂接件的那件 0.1**：`[prop_scale_attached]` 只对「挂接件带**非均匀** scale」报错，所以这件均匀缩小的真实数据能导入。
- **碰撞代理**：`aabb` 已经是乘过 scale 的尺寸，但逐型 `collider` 代理（`COLLIDER_PROXY`）的半径/半高是**模型空间**写的；导入器必须把 `scale` 也乘进代理参数（均匀时三个轴同一个数，非均匀时只允许 box/sphere 所以仍可乘），否则 0.1 那件会带着 10 倍大的胶囊。**这一条是导入器实现要求，未实现。**
- **AABB 与支撑推导**：`aabb` 变了，支撑面/叠放推导跟着变——已经在 §9 的「已乘 scale」约定里，不需要额外规则。
- **U4 的影响**：校验器的 OBB 互穿筛选用世界 AABB；接进 scale 后 0.9/0.1 那几件的误报会变化，`Docs/tests/levelbuild` 要加用例。
- **别把 `scale` 当动画**：缩放是静态列，不参与 `motions[]`，运行时也不会插值。

---

## 19. 许可与来源更正（noclip 是 MIT，不是 GPL-3）

| 事实 | 依据 |
|---|---|
| noclip.website 的 `LICENSE` 是 **MIT 正文 + 一段反编译说明**（"部分实现是逆向的、非净室"）；`package.json` 写 `"license": "MIT"`；GitHub 因那段说明报 `NOASSERTION` | [NOCLIP_KATAMARI_FORMAT §10](../../Research/KatamariBallRules/NOCLIP_KATAMARI_FORMAT.md)、`LICENSE` @ pinned commit |
| **noclip 的许可是 MIT，不是 GPL-3** | 同上 |
| 即便如此，逐行翻译仍是对该作品的**衍生作品**；MIT 允许再分发，但必须保留版权与许可声明 | 常规许可法理【推断，但确定】 |
| 我们的路线：**照规格重实现**（已做，解析器在 `~/KatamariRef/tools/katamari_stage_extract/`，981 行，仓库外）；仓库只放规格 | NOCLIP §0/§9/§10 |
| 仓库许可仍未定；解析器进仓库的前提是 owner 选定许可（MIT/Apache/GPL 兼容都可以，只要不搬 noclip 源码/逐行翻译） | [OPEN_DECISIONS](../../OPEN_DECISIONS.md) |

**同步口径**：`AGENTS.md` 与 `OPEN_DECISIONS.md` 的 noclip 条都按上表写：派生自第三方代码的解析器在 owner 选定仓库许可前留在仓库外；移植/衍生必须保留原版权与许可声明（MIT 允许）；首选照规格重实现。其余许可判断（素材 CC-BY-SA/GPL 不混用等）不受影响。
