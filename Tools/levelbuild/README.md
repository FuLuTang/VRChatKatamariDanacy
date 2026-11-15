# `Tools/levelbuild` — 关卡搭图工具（`placement.json` 是唯一真源）

`Docs/Design/Levels/<levelId>/placement.json` 描述一关里每一件可吸物和每一块静态结构。三边都只认这一个文件：

| 谁 | 干什么 | 入口 |
|---|---|---|
| 人（Windows，Blender 5.3 GUI） | 真 3D 拖拽摆位、出图 | 本目录 `blender/*.py`（同一个脚本，两个版本） |
| 代理（Linux，Blender 4.0.2 headless） | 批量改 JSON、校验、出图 | `levelbuild.py` + `blender/*.py` |
| Unity（Windows，编辑器导入器） | 单向导入到受管根 `LevelContent` | `Editor/LevelBuild/PlacementImporter.cs` |

设计见 [LEVEL_AUTHORING](../../Docs/Design/Tools/LEVEL_AUTHORING.md)（§11 是实现分阶段和验收）。**几何/AABB/可吸球径/支撑判定只有一份实现**：`placement_core.py`；CLI 与三个 Blender 脚本都 `import` 它，校验器和渲染不会各算一套。

---

## 0. 一分钟上手

```bash
cd <仓库根>

# 1) 校验（关卡的门：硬错误 → 退出码 1）
python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/story_01_bench_size/placement.json
python3 Tools/levelbuild/levelbuild.py check ... --report     # 加分区/分档/三态表

# 2) 出图（Workbench 俯视 3–5 s；EEVEE 3/4 视 30–50 s）
source ~/tools/blender-env.sh
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/render_views.py -- \
    --placement Docs/Design/Levels/story_01_bench_size/placement.json \
    --preset plan --out-dir /tmp/levelbuild

# 3) 从 JSON 搭场景（可选存 .blend 给人）
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/build_scene.py -- \
    --placement .../placement.json --save-blend /tmp/level.blend

# 4) 场景 → JSON（人改过 .blend 之后必须走这步；`--base` 提供非几何字段）
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/export_placement.py -- \
    --out .../placement.json --base .../placement.json --seat
```

样例（16 件、8 结构、能过门、能往返逐字节一致）：

```bash
python3 Tools/levelbuild/levelbuild.py check Tools/levelbuild/examples/placement.json --report
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/render_views.py -- \
    --placement Tools/levelbuild/examples/placement.json --preset plan --out-dir /tmp/levelbuild
```

---

## 1. CLI（`levelbuild.py`）

| 子命令 | 作用 | 退出码 |
|---|---|---|
| `check <placement.json> [--report] [--json] [--strict]` | 全部硬规则；`--report` 打分区/定义/分档/三态表；`--strict` 把 warning 也当错 | 0 通过 / 1 有硬错误 / 2 读不到 |
| `format <placement.json> [--check]` | 写成规范形（排序、取整、`-0.0→0.0`、2 空格、末尾换行、无 BOM）；`--check` 只判断 | 0 / 1（非规范） |
| `seat <placement.json> [--write] [--item ID]` | 按**支撑面法线**重新坐好（默认 dry-run） | 0 |
| `from-layout <layout.json> --out <placement.json> [--expected COUNT,UNITS]` | 把 `Docs/Design/StoryMode/picnic_corner_layout.json` 转成本格式（见 §5） | 0 / 2 |

`check` 的硬规则（设计 §8 的 1–12、14 条）：schema/必填/未知 def/重复 `itemId`、`aabb` 与模型 manifest 一致、`absorbDiameterReq`/`absorbable` 不自相矛盾、越界、出生净空、**穿地**、**悬空**、叠放、道具互穿、穿入实体、预算（`planes/boxes/ramps/ground/blocking`）、斜面几何（坡度/坡脚/顶边）、`box` 与 `ramp` 同 id、总量与 `level.expected` 一致；另外“文件不是规范形”是 **warning**（不是错）。

### 常用片段

```bash
# 只把一片道具重新坐好（不写文件）
python3 Tools/levelbuild/levelbuild.py seat level.json --item prop_apple_000
# 全关重坐并写回（改完 JSON 或从别人的布局转过来之后）
python3 Tools/levelbuild/levelbuild.py seat level.json --write
```

---

## 2. `placement.json` 速查

```jsonc
{
  "schema": "katamari.placement/0",
  "level": {
    "id": "story_01_bench_size", "scene": "StoryLevel", "units": "m",
    "convention": "unity-y-up-left-handed-quat-xyzw",
    "ballStart": [-12.5, 0.15, -12.5], "playerSpawn": [-14.5, 0.5, -14.5],
    "ballDiameterStart": 0.30, "ballDiameterMax": 2.20,
    "bounds": [[-19.4, -19.4], [-6.0, -6.0]], "spawnClearanceM": 1.0,
    "budget": {"planes": 8, "boxes": 8, "ramps": 4, "propSupports": 6, "blocking": 6},
    "expected": {"propCount": 110, "valueUnits": 412},
    "zones": [{"id": "pocket", "rect": [...]}]
  },
  "defs": [ {"id": "apple", "kind": "fruit", "valueUnits": 1} ],   // 每个 def 的 kind 与价值（见 §3）
  "props": [ {
      "itemId": "prop_plate_000", "def": "plate", "kind": "tableware", "zone": "table",
      "position": [-1.78, 0.4575, -1.18],       // Unity 世界坐标，米；枢轴 = AABB 中心
      "rotationQuat": [0, 0, 0, 1],             // Unity 顺序 (x,y,z,w)，规范符号
      "support": "picnic_table",                // 或 "stackedOn": "<另一件 itemId>"
      "aabb": [0.24, 0.025, 0.24],              // 未旋转的模型 AABB，Unity 轴向（y=高）
      "absorbDiameterReq": 0.302, "absorbable": true,   // 派生：check/format 会重算
      "valueUnits": 4
  } ],
  "structures": [
      {"id": "ground", "role": "ground", "shape": "plane", "center": [0,0,0], "size": [13.4,0.04,13.4],
       "rotationQuat": [0,0,0,1], "collision": "none", "absorbable": false, "support": true},
      {"id": "wall_west", "role": "boundary", "shape": "plane", ...},
      {"id": "flower_bed", "role": "box", "shape": "box", ...},
      {"id": "ramp_to_bed", "role": "ramp", "shape": "box", ..., "meets": "flower_bed"}
  ],
  "cameras": [ {"id": "top", "type": "ortho_top", "location": [0,12,0], "target": [0,0,0], "coverage_m": 14.1} ],
  "renderPresets": [ {"id": "plan", "camera": "top", "engine": "WORKBENCH", "res": [1280,720]} ]
}
```

三条会静默出错的约定（都只实现一次，在 `placement_core.py`）：

1. **坐标**：JSON 是 Unity 世界坐标（左手、Y-up、米）。Blender 是 Z-up，换算是 `(x,y,z) → (x,z,y)`，四元数随之把向量部分变号；两侧各有一个函数 + 一条自检。
2. **四元数**：写 `(x, y, z, w)`，且**规范符号**（`w<0` 整体取反）。CLI 每次打印 `quatOrder=xyzw` 横幅。
3. **`aabb` 是 Unity 轴向**：manifest 是 Blender 轴向（z 是高），所以 `aabb = (manifest.x, manifest.z, manifest.y)`。直接把 manifest 三个数抄进 JSON 会让每个高件躺倒（`placement_core.manifest_aabb` 是唯一转换点）。

`position` 是枢轴（= AABB 中心），所以贴地是 `y = 地面高 + aabb.y/2`；在斜面上则要**同时**按法线转正（`levelbuild.py seat` 或 `placement_core.seat_pose`）。

---

## 3. 价值单位：按 def 声明，导入器照写（Lead 2026-11-15 决定）

`valueUnits` 现在**不是**由 `PickupFieldSync` 按最长边定价得出的，而是关卡数据：

* `defs[].valueUnits` 给「这个 def 值多少」，`props[].valueUnits` 可以逐件覆盖；
* 有效值 = 逐件值，否则 def 值（`placement_core.effective_value_units`，CLI 与 Unity 都按这个顺序）；
* Unity 导入器在跑完 `PickupFieldSync` 之后**用自己的声明值覆盖 `valueUnits` 数组**，再逐行回读校验；不一致就报错（`PlacementImporter.WriteDeclaredValueUnits`）。`kinds` 数组仍来自 kind 文件夹，行集合/顺序/位姿/包围盒仍由同步工具拥有。

> 仍然存在的耦合：`PickupFieldSync` 会**丢掉**没有值表行的 `(kind, tier)`（例如 `container m`）。所以 kind 文件夹必须落在 `Editor/Pickup/PickupFieldAuthoringRules.ValueTable` 里，否则那一件根本不成行，导入器只能报错让你加行。这条是待办（`PICNIC_CORNER_LEVEL_ONE` §6 C5）。

---

## 4. 两个 Blender 版本

同一套脚本要在两边跑：本机 4.0.2（Ubuntu arm64、headless、无 GPU、无 OIDN）和用户的 5.3.0（Windows、GPU）。

* **引擎名不硬编码**：`renderPresets[].engine` 里的 `EEVEE/WORKBENCH/CYCLES` 是逻辑名；`blender_common.select_engine` 对 EEVEE 调**道具流水线自己的** `prop_lib.select_eevee_engine`（3.6/4.0–4.1/5.0+ → `BLENDER_EEVEE`，4.2–4.5 → `BLENDER_EEVEE_NEXT`），不复制第二份探测。
* 版本差异集中在 `blender_common.select_engine` / `configure_shading`：`eevee.taa_render_samples` 是否叫这个名字、`use_gtao` 是否还在、Cycles 有没有 OIDN（4.0.2 没有，所以 `use_denoising=False`）。
* 无 GPU 时的 `EGL Error (0x3001/0x3009)` **是噪声**：Blender 回落到 llvmpipe 并成功出图。判据是「有没有写出 PNG + 我看过」。

命令（Windows 上是 `blender.exe`，参数同样用 `--background --python-exit-code 2 --python ... --`）：

```bash
source ~/tools/blender-env.sh
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/render_views.py -- \
    --placement <placement.json> --preset plan,overview,ball_eye \
    [--engine WORKBENCH] [--res 1280x720] [--samples 32] [--out-dir /tmp/levelbuild]
```

`--engine` 只是临时覆盖（给人快速看一张），不改文件。俯视图的 `coverage_m` 是**短边保证入画**的范围，渲染时按分辨率比例放大 `ortho_scale`（否则 16:9 会把方形场地上下裁掉——实测 13.4 m 场地只看到 7.9 m）。

---

## 5. 从别的布局转过来（`from-layout`）

`Docs/Design/StoryMode/picnic_corner_layout.json` 是设计者自己的格式（`x/y/z + yaw`、`support` 写的是“ground/bed/folding_table”这种标签）。`from-layout` 按设计 §7.2 的映射转：

| 源 | 去处 |
|---|---|
| `props[]` + `gate[]` + `structures[]`（17 件可吸家具） | `props[]` |
| `ramps[]` | `structures[]` `role=ramp`（`meets` 用顶边几何匹配台面；作者的 `bottom`/`top` 是**行走面**，4 cm 板体在它下面，坡脚板底可低地面一个板厚） |
| `shell` 的 ground/4 边界/花圃台/角树/树根丘/指示牌 | `structures[]`（1 ground / 4 boundary / 4 box） |

它不是「整表搬运」，做了三件作者数据不做的事，都为了过门：

1. **用真实尺寸重新坐好**：布局的 `y` 是 `support_top + 它自己的高度/2`，而高度表和模型 manifest 不是一个口径；转换后按 manifest AABB 重坐（`--no-seat` 关掉）。
2. **`support` 标签变成 id**：`bed → flower_bed`，`folding_table → 那件折叠桌的 itemId`（走 `stackedOn`）；无法唯一确定的标签会列出来，交给 `check` 的「悬空」规则判。
3. **没有模型的 def 默认丢掉**（`--keep-missing` 保留，此时 `check` 会红）。本仓现在缺 9 个 def 的模型（`beer_crate`、`book_stack`、`cooler_box_large`、`crate`、`crate_large`、`lounge_chair`、`picnic_bench`、`storage_bin`、`stroller`），所以 127 件 / 1500 u 的源表转出来是 116 件 / 718 u——**差额全部是这 9 个 def**，补齐模型后重跑即可。

要给「设计总量」上闸（推荐：这样缺件是红的，而不是悄悄变成新的期望值）：

```bash
python3 Tools/levelbuild/levelbuild.py from-layout ... --out ... --expected 127,1500
python3 Tools/levelbuild/levelbuild.py check <out>     # FAIL: propCount -11 / valueUnits -782，正好是缺的那几件
```

---

## 6. Unity 导入器（`Editor/LevelBuild/`）

菜单 `VRC Katamari/Level/Import Placement (LevelContent)`：按 `level.scene` 找当前场景对应的那一份（多份/零份都拒绝并列出候选），或代码里直接 `PlacementImporter.Import("<levelId>")`（返回报告文本，也给 MCP/`-executeMethod` 用）。

* **只碰 `LevelContent`**：`LevelContent/Props/<kind>/[<zone>/][<group>/]<itemId>`（第一层必须是 kind，`PickupFieldSync` 从它读 kind）与 `LevelContent/Structures/<id>`。别处一律不动。
* **道具**：实例化 `Assets/VRC_Katamari/Art/Props/<def>/<def>.fbx`（预制体库是 P2），按 `itemId` 增/改/删；同名同 def 只改位姿（`1e-3 m` / `|dot| ≥ 0.99999`），def 变了才重建。
* **结构**：`role=box` → 真 cube + 真 `BoxCollider`；`role=ground/boundary` → 只有 Transform 的标记物体（`LevelArenaHost` 只读 `groundNormal/groundOffset` 与每面墙的**局部 +Z**，不看几何）；`role=ramp` → 有 `BoxCollider` 但 P1 不登记（`LevelArenaHost` 还没有 `rampBoxes`）。
* **arena 接线**：填 `LevelArenaHost.groundNormal/groundOffset`、`boundaryWalls`（每面墙一个 Transform，局部 +Z 指向场内、原点在碰撞面上）、`staticBoxes`、`allowedKinds`（本关 kind 去重排序），然后 `CopyProxyToUdon`。
* **`PickupFieldSync`**：以 `LevelContent/Props` 为 field root 调 `SyncFromChildren`，再写声明值 `valueUnits` 并回读（§3）。
* **不 `SaveScene`**：导入后场景是 dirty 的，由人决定何时保存。
* **幂等**：第二次导入日志必须是 `[level] +0 ~0 -0 props=N`。不同就说明幂等键被破坏了（按名字/顺序而不是 `itemId`）。

---

## 7. 测试

```bash
python3 -m unittest discover -s Docs/tests -p 'test_*.py'   # 全仓
python3 Docs/tools/validate_plan.py
```

`Docs/tests/levelbuild/` 覆盖：坐标/四元数往返、旋转后 AABB、`D_req` 数值（含体积门等价）、斜面上坐正（含“半埋”反例）、确定性（排序/取整/`-0.0`/逐字节）、`defs[]` 价值回退、**5 个负向用例**（穿地/悬空/越界/预算超/总量不符）以及未知 def、重复 id、互穿、`box`+`ramp` 同 id、缺地面、过期 `absorbDiameterReq`；CLI 的 0/1/2 退出码与 `format`/`seat` 的行为。

---

## 8. 已知缺口 / 待对齐

1. **`PickupFieldSync` 值表缺 `container m`**：`container` 只有 `l=16`，而野餐篮 0.45 m 落在 `m` 档 → 那一行会被同步工具丢掉。导入器会报「sync dropped it」，需要给值表加一行（不在本工具写范围）。
2. **道具模型的根必须有 Renderer**（现在是 FBX 直实例化）。根是空节点时同步工具会把子节点名当 `itemId`，导入器拒绝并指名该 def。P2 预制体库解决。
3. **`LevelArenaHost` 没有 `rampBoxes`**：斜面在场景里有 `BoxCollider` 但不进 `staticBoxes`（否则坡底出现隐形墙），P3 再登记。
4. **旋转道具的 `propBounds` 是世界 AABB**（同步工具用 `Renderer.bounds`），不是模型 AABB：对 45° 摆的薄件会偏大。这是 `PickupFieldSync` 的既有行为，导入器不修，只在报告里保留依据。
5. **`expected` 在 `from-layout` 里是「转出来多少」**，不是设计表的 127/1500（差额 = 缺模型的 def，见 §5）。补模型后重跑，数字会自动对上。
6. **PNG 不进仓库**：本工具只往 `--out-dir` 写图，验收图由人决定是否放 `Docs/Evidence/`。
