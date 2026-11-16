# `Tools/levelbuild` — 关卡搭图工具（`placement.json` 是唯一真源）

`Docs/Design/Levels/<levelId>/placement.json` 描述一关里每一件可吸物和每一块静态结构。三边都只认这一个文件：

| 谁 | 干什么 | 入口 |
|---|---|---|
| 人（Windows，Blender 5.3 GUI） | 真 3D 拖拽摆位、出图 | 本目录 `blender/*.py`（同一个脚本，两个版本） |
| 代理（Linux，Blender 4.0.2 headless） | 一条条改地图、校验、出图 | **`levelserve.py` + `levelctl.py`**（常驻，§9）；批量/CI 用 `levelbuild.py` + `blender/*.py` |
| Unity（Windows，编辑器导入器） | 单向导入到受管根 `LevelContent` | `Editor/LevelBuild/PlacementImporter.cs` |

设计见 [LEVEL_AUTHORING](../../Docs/Design/Tools/LEVEL_AUTHORING.md)（§11 是实现分阶段和验收）。**几何/AABB/可吸球径/支撑判定只有一份实现**：`placement_core.py`；CLI 与三个 Blender 脚本都 `import` 它，校验器和渲染不会各算一套。

---

## 0. 一分钟上手

```bash
cd <仓库根>

# A) 常驻编辑器（推荐：场景建一次，之后一条条改；每次成功的改动都自动导出）
python3 Tools/levelbuild/levelserve.py start --level story_01_bench_size
python3 Tools/levelbuild/levelctl.py status
python3 Tools/levelbuild/levelctl.py list --zone table_top --limit 5
python3 Tools/levelbuild/levelctl.py move apple_01 --delta=0,0.02,0
python3 Tools/levelbuild/levelctl.py check --report
python3 Tools/levelbuild/levelctl.py render --preset plan --out-dir /tmp/levelbuild   # 打印 PNG 路径
python3 Tools/levelbuild/levelctl.py undo
python3 Tools/levelbuild/levelctl.py export --to /tmp/handoff.json                    # 可选：交一份副本
python3 Tools/levelbuild/levelserve.py stop                                           # 脏则先导出

# B) 一次性（CI / 交接 / 没有服务时；和 A 共用同一批函数）
python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/story_01_bench_size/placement.json --report
source ~/tools/blender-env.sh
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/render_views.py -- \
    --placement Docs/Design/Levels/story_01_bench_size/placement.json --preset plan --out-dir /tmp/levelbuild
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/build_scene.py -- \
    --placement .../placement.json --save-blend /tmp/level.blend
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/export_placement.py -- \
    --out .../placement.json --base .../placement.json --seat

# C) 样例（16 件、8 结构、能过门、能往返逐字节一致）
python3 Tools/levelbuild/levelbuild.py check Tools/levelbuild/examples/placement.json --report
```

实测：`build_scene.py` → `export_placement.py --seat` → 与源文件**逐字节相同**（第一关 127 件；`defs[]`、相机、道具、结构全保留）。`--seat` 对已坐好的数据是 no-op，对抬高 5 cm 的地面件会修回去。

全部操作：`python3 Tools/levelbuild/levelctl.py ops`；服务本身：`levelserve.py status|logs|health`。

---

## 1. CLI（`levelbuild.py`）

| 子命令 | 作用 | 退出码 |
|---|---|---|
| `check <placement.json> [--report] [--json] [--strict]` | 全部硬规则；`--report` 打分区/定义/分档/三态表；`--strict` 把 warning 也当错 | 0 通过 / 1 有硬错误 / 2 读不到 |
| `format <placement.json> [--check]` | 写成规范形（排序、取整、`-0.0→0.0`、2 空格、末尾换行、无 BOM）；`--check` 只判断 | 0 / 1（非规范） |
| `seat <placement.json> [--write] [--item ID]` | 按**支撑面法线**重新坐好（默认 dry-run） | 0 |
| `from-layout <layout.json> --out <placement.json> [--expected COUNT,UNITS]` | 把 `Docs/Design/StoryMode/picnic_corner_layout.json` 转成本格式（见 §5） | 0 / 2 |
| `check` 的第二行组 | `supports derived: N` / `supports declared: N` / `supports derivable, not stored: N` / `supports unreproduced: N`（父指针的存量与漂移，见 §2.1） | — |

`check` 的硬规则（设计 §8 的 1–12、14 条）：schema/必填/未知 def/重复 `itemId`、`aabb` 与模型 manifest 一致、`absorbDiameterReq`/`absorbable` 不自相矛盾、越界、出生净空、**穿地**、**悬空**、叠放、道具互穿、穿入实体、预算（`planes/boxes/ramps/ground/blocking`）、斜面几何（坡度/坡脚/顶边）、`box` 与 `ramp` 同 id、总量与 `level.expected` 一致、**弯坡**（`curve_ramp` 的半径/弧角/宽厚/段数/弦高/重叠）；另外“文件不是规范形”是 **warning**（不是错）。

> **坡度 > 45° 现在只是 warning**（2026-11-15，S4）：v2 能慢爬陡坡（`CONTACT_MODEL_V2` 3.10）。硬错误只剩物理上不可能的值——板面朝下（inverted）、90°（侧立就是墙）、零厚度、NaN。`--strict` 仍可把 warning 当错。

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
    "id": "story_01_bench_size", "scene": "StoryMode", "units": "m",
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
      "support": "picnic_table",                // 结构 id；或 "stackedOn": "<另一件 itemId>"
      // "parent": "<另一件 itemId>" 与此同义，是运行期要读的那一列（§2.1）
      "aabb": [0.24, 0.025, 0.24],              // 未旋转的模型 AABB，Unity 轴向（y=高）
      "absorbDiameterReq": 0.302, "absorbable": true,   // 派生：check/format 会重算
      "valueUnits": 4
  } ],
  "structures": [
      {"id": "ground", "role": "ground", "shape": "plane", "center": [0,0,0], "size": [13.4,0.04,13.4],
       "rotationQuat": [0,0,0,1], "collision": "none", "absorbable": false, "support": true},
      {"id": "wall_west", "role": "boundary", "shape": "plane", ...},
      {"id": "flower_bed", "role": "box", "shape": "box", ...},
      {"id": "ramp_to_bed", "role": "ramp", "shape": "box", ..., "meets": "flower_bed"},
      {"id": "curve_up_a", "role": "curve_ramp", "shape": "box", "collision": "box",
       "center": [8.0, 0.0, 12.0],                    // 弧起点（第一段 slab 中心）
       "rotationQuat": [0,0,0,1],                     // 起始帧：本地 +Z 前进、+Y 法线
       "size": [4.0, 0.25, 0.0],                      // 派生 = [width_m, thickness_m, 0]
       "absorbable": false, "support": true,
       "curve": {"radius_m": 4.0, "arc_deg": 30.0, "bend": "up",
                 "width_m": 4.0, "thickness_m": 0.25}}  // segments 省略 = 自动
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

### 2.1 `parent`：谁压着谁，由几何推导（2026-11-16）

一件**最多一个**父件。运行期只读这一列（父件被吃掉 ⇒ 子件落，见
`Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md` §10.9），编辑器/校验器把 `stackedOn` 当同一个关系的旧写法读（`placement_core.prop_parent()`，一处解析），两者同时出现且不一致 ⇒ `[parent_conflict]`。

```bash
python3 Tools/levelbuild/levelbuild.py check <level>/placement.json     # 第二行组：derived / declared / 漂移
python3 Tools/levelbuild/levelctl.py derive-supports                   # dry-run：列出会写什么、哪些对不上
python3 Tools/levelbuild/levelctl.py derive-supports --apply           # 只写缺失的，不覆盖、不清空
```

规则（`Tools/levelbuild/supports.py`，口径见 `Docs/Research/KatamariBallRules/STACKS_IN_ORIGINAL.md`）：

* 用**旋转后**的世界 AABB（`position` + 四元数 + 模型 `aabb`），所以躺倒的件按它真实的高度算；
* 足迹水平相交（贴合也算，`0` 相交不算）；
* 竖直间隙 `bottom(子) - top(父)` 落在 `[-0.30, +0.50] × 较小世界高度`（`parent_gap_window`，校验器与推导共用这一个函数）；
* 取**最高的那个顶面**；地面与结构永远不是父件；断环（`_break_cycles`）。

`parentDerived: true` 只加在**机器推导**出来的链接上：它有上面那条间隙窗口；作者手写的 `parent`（旗子挂在杆上）不受窗口约束，也不会被 `--apply` 覆盖。`--apply` 对已有指针只报 `conflict` / `orphaned`，不静默改写——把一条错误的支撑改对是人的活，静默丢掉只会把堆叠 bug 变成悬空件。

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
3. **没有模型的 def 默认丢掉**（`--keep-missing` 保留，此时 `check` 会红）。本仓的模型现已齐备：127 件 / 1500 u 的源表转出来就是 **127 件 / 1500 u**，`check --report` 0 错；`--expected 127,1500` 仍作为设计总量的闸保留。

要给「设计总量」上闸（推荐：这样缺件是红的，而不是悄悄变成新的期望值）：

```bash
python3 Tools/levelbuild/levelbuild.py from-layout ... --out ... --expected 127,1500
python3 Tools/levelbuild/levelbuild.py check <out>     # PASS: propCount 127 / valueUnits 1500
```

---

## 6. Unity 导入器（`Editor/LevelBuild/`）

菜单 `VRC Katamari/Level/Import Placement (LevelContent)`：按 `level.scene` 找当前场景对应的那一份（多份/零份都拒绝并列出候选），或代码里直接 `PlacementImporter.Import("<levelId>")`（返回报告文本，也给 MCP/`-executeMethod` 用）。

* **只碰 `LevelContent`**：`LevelContent/Props/<kind>/[<zone>/][<group>/]<itemId>`（第一层必须是 kind，`PickupFieldSync` 从它读 kind）与 `LevelContent/Structures/<id>`。别处一律不动。
* **道具**：实例化 `Assets/VRC_Katamari/Art/Props/<def>/<def>.fbx`（预制体库是 P2），按 `itemId` 增/改/删；同名同 def 只改位姿（`1e-3 m` / `|dot| ≥ 0.99999`），def 变了才重建。
* **结构**：`role=box` → 真 cube + 真 `BoxCollider`；`role=ground/boundary` → 只有 Transform 的标记物体（`LevelArenaHost` 只读 `groundNormal/groundOffset` 与每面墙的**局部 +Z**，不看几何）；`role=ramp` → 真 cube + `BoxCollider`，导入器填进 `LevelArenaHost.rampBoxes`（P3，d31dc9b），由 `RegisterRamp` 烘成支撑面，**不进 `staticBoxes`**。
* **弯坡**：`role=curve_ramp` 的父物体 `<id>` 是纯标记（无 Renderer/Collider），导入器在它下面建 `<id>_00 … <id>_NN` 的 cube + `BoxCollider`（段数由 `curve.segments` 或弦高预算决定），**逐段**填进 `rampBoxes`（和直线坡同一个数组，host API 不变）。第二遍导入仍是 `+0 ~0 -0`（段位姿/碰撞体逐项比较）；段数变少会删掉多出来的子物体；摘要里单列 `curves: 1 ramp(s), 5 slab segment(s): +5 ~0 -0`。
* **arena 接线**：填 `LevelArenaHost.groundNormal/groundOffset`、`boundaryWalls`（每面墙一个 Transform，局部 +Z 指向场内、原点在碰撞面上）、`staticBoxes`、`rampBoxes`（P3）、`allowedKinds`（本关 kind 去重排序），然后 `CopyProxyToUdon`。
* **`PickupFieldSync`**：以 `LevelContent/Props` 为 field root 调 `SyncFromChildren`，再写声明值 `valueUnits` 并回读（§3）。
* **不 `SaveScene`**：导入后场景是 dirty 的，由人决定何时保存。
* **幂等**：第二次导入日志必须是 `[level] +0 ~0 -0 props=N`。不同就说明幂等键被破坏了（按名字/顺序而不是 `itemId`）。

---

## 7. 测试

```bash
python3 -m unittest discover -s Docs/tests -p 'test_*.py'   # 全仓
python3 Docs/tools/validate_plan.py
```

`Docs/tests/levelbuild/` 覆盖：常驻编辑器的 48 + 11 个用例（§9.5 另外列）；坐标/四元数往返、旋转后 AABB、`D_req` 数值（含体积门等价）、斜面上坐正（含“半埋”反例）、确定性（排序/取整/`-0.0`/逐字节）、`defs[]` 价值回退、**5 个负向用例**（穿地/悬空/越界/预算超/总量不符）以及未知 def、重复 id、互穿、`box`+`ramp` 同 id、缺地面、过期 `absorbDiameterReq`；CLI 的 0/1/2 退出码与 `format`/`seat` 的行为。`test_curve_ramp.py` 另覆盖弯坡：schema 的 18 个原因码、段数与设计表一致（§5.7 的 2→10 / 3→12 / 5→15 / 10→21 / 20→29）、弦高预算、法线连续、`down` 是 `up` 的镜像、`dump-load-dump` 逐字节、总量口径（一条=1 ramp / N 段=staticObbs）、以及 45° 门：50°/89° 只 warning、90°/inverted/零厚度/NaN 仍红。`test_supports.py` 另覆盖父指针：窗口随较小件缩放（2 mm 叶子没有 6 cm 的松弛）、旋转后按真实高度判、贴合算相交、结果与 `props[]` 顺序无关、`apply` 只写缺失且第二次是 no-op、六个校验码、`story_01_bench_size` 的 6 条复现 + 2 条悬空声明，以及 `_ref_cancer` 与原版导出表 90 条父记录的对照（0 复现，原因写在用例 docstring 里）。

---

## 8. 已知缺口 / 待对齐

1. **`PickupFieldSync` 值表缺 `container m`**：`container` 只有 `l=16`，而野餐篮 0.45 m 落在 `m` 档 → 那一行会被同步工具丢掉。导入器会报「sync dropped it」，需要给值表加一行（不在本工具写范围）。
2. **道具模型的根必须有 Renderer**（现在是 FBX 直实例化）。根是空节点时同步工具会把子节点名当 `itemId`，导入器拒绝并指名该 def。P2 预制体库解决。
3. **斜面只进 `rampBoxes`**：`LevelArenaHost.rampBoxes` 已接（P3，d31dc9b），斜面由此烘成支撑面；**不要**把它同时放进 `staticBoxes`（坡底会出隐形墙，`LevelArenaHost.Setup` 会报 `[arena] ... in both ...` 并跳过盒登记）。
4. **旋转道具的 `propBounds` 是世界 AABB**（同步工具用 `Renderer.bounds`），不是模型 AABB：对 45° 摆的薄件会偏大。这是 `PickupFieldSync` 的既有行为，导入器不修，只在报告里保留依据。
5. **`expected` 在 `from-layout` 里是「转出来多少」**，不是设计表的 127/1500（差额 = 缺模型的 def，见 §5）。补模型后重跑，数字会自动对上。
6. **PNG 不进仓库**：本工具只往 `--out-dir` 写图，验收图由人决定是否放 `Docs/Evidence/`。
7. **弯坡还不能从 `levelctl.py` 新建**：`add-structure` 只认 `center/size/rotationQuat`（带 `curve` 块的条目目前手写 JSON；改已有弯坡的 `center` 或参数走 `set`/`move` 是安全的，`curve` 块原样保留）。渲染侧把它画成每段一个 box 的 mesh，`curve` 参数存在物体的 `curve_json` 属性里，导出时原样带回。

---

## 9. 常驻编辑器服务（`levelserve.py` / `levelctl.py`）

### 9.1 为什么

一次性脚本每次都是一条完整流水线：启动 Blender（约 0.35 s）→ 导入 44 个 GLB、铺 127 件（约 0.8 s）→ 渲染 → 退出。改一个数、看一张图都要重付一次。常驻服务把「建场景」付一次，之后每次操作只动**差量**：

| 动作 | 一次性 | 常驻 |
|---|---:|---:|
| 改一件道具（含校验 + 导出） | 1.2 s + | **0.12 s** |
| 校验 `check` | 1.2 s + | **0.07 s**（纯 Python 0.05 s） |
| Workbench 1280×720 一张 | 4.70 s（其中渲染 3.5 s） | **3.19 s** |
| EEVEE 1280×720 一张 | ~46.2 s | **45.0 s** |

构建不是瓶颈（127 件只要 1.17 s），瓶颈一直是渲染；常驻省的是**每次编辑的重建与启动**，以及把改动的粒度从「起一个进程」降到「发一条 JSON」。

### 9.2 结构

```
levelserve.py start ──► Blender --background editor_server.py
                          ├─ placement_core        校验/几何（唯一实现）
                          ├─ LevelScene.sync       文档 → 场景（差量增删改）
                          ├─ editor_core.Editor    操作 → 文档 → 校验 → 导出
                          └─ editor_http.Server    127.0.0.1 上的 JSON + token
levelctl.py ──► POST {"op":…,"args":…} ──► 上面那个进程
```

**真源关系**：常驻的文档 + 场景是**工作状态**；`placement.json` 是**导出结果**，也是 Unity/一次性脚本/git 读的那份。每个成功操作的最后一步就是导出（规范形、原子替换、返回 sha256），所以崩了最多丢在途的一条。操作若会**引入本会话没见过的硬错误** → 拒绝、文件不动、响应给规则原文；打开时就红的文件仍可编辑可修（错误集合不扩大即可）。设计见 [LEVEL_AUTHORING §16](../../Docs/Design/Tools/LEVEL_AUTHORING.md)。

### 9.3 常用命令

```bash
levelserve.py start --level <id> [--out-dir /tmp/render] [--port 0] [--no-auto-export]
levelserve.py status | logs -n 40 | health | stop

levelctl.py status
levelctl.py list --zone table_top --def apple --near=-13,-9 --radius=1.5 --limit 5
levelctl.py get apple_01
levelctl.py add-prop --def apple --pos=3.2,0,3.2 --seat --update-expected
levelctl.py add-structure --role box --center=3.5,0.25,-3.5 --size=0.3,0.5,0.3 --support
levelctl.py set apple_01 --zone table_top --value 4
levelctl.py move apple_01 --to=-7.9,0.05,-17.3        # 或 --delta=0,0.02,0
levelctl.py rotate acorn_01 --yaw 90                  # 或 --quat=x,y,z,w
levelctl.py remove acorn_01 --update-expected
levelctl.py seat [id …]
levelctl.py derive-supports [id …] [--apply]   # 父指针：默认 dry-run，--apply 才写
levelctl.py check --report
levelctl.py export [--to /tmp/handoff.json] [--force]
levelctl.py render --preset plan [--engine WORKBENCH] [--res 1280x720] [--out DIR] [--name x]
levelctl.py camera --id side --location=0,2,-6 --target=0,0.3,0 --lens 50
levelctl.py preset --id side_view --camera side --engine WORKBENCH --res 640x480
levelctl.py undo [--steps N]
levelctl.py raw '{"op":"status"}'
levelctl.py batch ops.json                             # 原子批量，一次导出
levelctl.py shutdown
```

退出码：`0` 成功 / `1` 被拒（看 `error` + `details`）/ `2` 没服务或端点过期。

**注意**：`add-prop` / `remove` / 改 `valueUnits` 会改变设计总量 `level.expected`，所以要么带 `--update-expected`，要么在一个 `batch` 里跟一条 `{"op":"expected"}`——设计总量是闸，不会静默变化。

### 9.4 并发与生命周期

- 单进程、**串行**处理（`bpy` 不是线程安全的）；一份服务一份打开的关卡；多个代理排队，每个操作原子。
- 一次 `render` 会占住队列（Workbench 3 s / EEVEE 45 s）：出图放在一批编辑的最后。
- SIGTERM / SIGINT / `stop` / `shutdown` 都会先导出再退出，并删掉端点文件（`/tmp/levelbuild-editor-<uid>/editor.json`，0600，带 token）。
- `status` 的两个标志：`dirty` = 磁盘和内存不一致（人在外面改了文件也会这样）；`unexported` = 内存里有没写盘的已提交改动（只有 `--no-auto-export` 才会）。`open` / `reload` 换文档前会先把 `unexported` 的改动写盘。
- 进程被杀：端点文件成为「过期」，`levelctl` 直说 `endpoint … is stale`（退出码 2），`levelserve start` 会清掉重起并从 JSON 重载。

### 9.5 测试

```bash
python3 -m unittest Docs.tests.levelbuild.test_level_editor Docs.tests.levelbuild.test_level_editor_http
```

59 个用例，**不依赖 Blender**（场景用一个 fake）：操作 → 文档变更、导出逐字节、校验失败回滚、红文件可修不可坏、undo、batch 原子性、导出→重建→导出往返、HTTP/token/health/shutdown/过期端点。

---

## 10. 外部资产包：`model` / `overlapBudget` / `groupId` / `spawnClearanceM`（2026-11-15）

> 本节是**新增能力**。不写这些字段的关卡，`check` 的规则与输出**逐字节不变**（默认的
> 5 % 互穿规则、1.0 m 出生净空、repo manifest 要求都没有放宽）。

### 10.1 `check --manifests DIR` 与 `check --level ID`

```bash
# 用关卡自己的 manifest 树校验（默认仍是 Docs/Art/OriginalModels/props/exports）
python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/<id>/placement.json \
    --manifests Docs/Design/Levels/<id>/manifests

# 等价写法：用关卡 id 找 Docs/Design/Levels/<id>/placement.json
python3 Tools/levelbuild/levelbuild.py check --level <id> --manifests Docs/Design/Levels/<id>/manifests
```

`--manifests DIR` 走的就是 `placement_core.load_manifests(DIR)`，目录不存在是用法错误（退出码 2）。
只有 `check` 会调用 `validate`；`format` / `seat` / `from-layout` 不校验，所以没有这个选项。
关卡 id 现在允许以下划线开头（`_probe` 这类本地探针目录），仍是单层路径或一格 `a/b`，不接受 `..`。

### 10.2 `defs[].model`：一个 def 用自己的 `.fbx`

```jsonc
"defs": [
  {"id": "mug", "kind": "tableware", "valueUnits": 4,
   "model": "Assets/ThirdParty/Models/mug/mug.fbx"}   // Unity 相对路径，必须在 Assets/ 下、.fbx、无 ..
]
```

* **校验**：`model` 必须是字符串、以 `Assets/` 开头、以 `.fbx` 结尾、不含 `..`；否则报
  `[def_model]` 硬错误。写 `model` 后，def id **不再需要**在 repo 道具目录里有同名资产
  （也不再因 id 与 repo 道具重名而被套上别人的 manifest）。
* **manifest 从哪来**：显式 `--manifests DIR` 优先；否则找
  `<modelDir>/../exports/<def>.manifest.json`（例如 `Assets/ThirdParty/Models/<def>/<def>.fbx`
  → `Assets/ThirdParty/Models/exports/<def>.manifest.json`）。两处都没有时 **warning**：
  `declares model … but no manifest was found`，`aabb` 不被核对，关卡仍可校验其它规则。
* **模型不存在只在 Unity 侧判**：Python 不查文件系统（模型可能只在操作员的机器上/被 git 忽略）；
  导入器找不到资产时报 `[model_missing]`，**不会**回退到 `Art/Props/<def>/<def>.fbx`
  （那会画出另一个物体并掩盖坏路径）。
* **幂等**：有 `model` 的 def 按**资产路径**判断「是不是同一个模型」；没有 `model` 的 def 仍按
  资产名 = def id（原行为）。
* Blender 侧仍用 `Art/Props` 的 GLB（`prop_glb` 未改）；`model` 只影响 Unity 实例化的 FBX 与
  `prop_fbx` 的解析。

### 10.3 `level.expected.overlapBudget` + `props[].groupId`

```jsonc
"expected": {"propCount": 648, "valueUnits": 1448, "overlapBudget": 400},
"props": [
  {"itemId": "cookie_a", "def": "cookie", "groupId": "biscuit_pile", ...},
  {"itemId": "cookie_b", "def": "cookie", "groupId": "biscuit_pile", ...}
]
```

* **不写 `overlapBudget`**：与以前完全一样——每一对相交体积 > 较小 AABB 5 % 的道具都是硬错误
  （最多列 25 对 + 一行汇总）。
* **写了 `overlapBudget: N`**：先数**对数**，`N` 以内只给一条 summary warning
  （`… prop/prop overlap pair(s) (level.expected.overlapBudget N)`）；**只有对数超过 N** 才报一条
  硬错误。这样一间真实密度的房间可以整关校验，而不是把规则从所有关卡上关掉。
* **同一 `groupId` 的道具互穿豁免**（父/子物体，例如三块饼干）。这是**数据**：没写 `groupId`
  的道具照旧判；不同 `groupId` 也照旧判。`groupId` 必须是非空字符串，只做豁免、不影响导入层级
  （导入层级仍用 `group`）。
* 预算与对数都只统计「>5 % 且无 `stackedOn` 关系」的配对。

### 10.4 `level.spawnClearanceM`

已支持，按关卡读（默认 1.0 m）：`"spawnClearanceM": 0.15` 只影响该文件的球起点净空判定；
`playerSpawn` 的 1.0 m 规则不变。

### 10.5 测试

`Docs/tests/levelbuild/test_levelbuild_fields.py`（23 个用例，合成关卡，无参考关卡数据）覆盖：
`--manifests` 的 0/1/2 退出码与默认输出一致、`model` 的逐字节往返与五类非法路径、def 无 repo
资产但声明 model、`prop_fbx` 优先级、模型旁 manifest、`overlapBudget` 缺省/内/超/非法、
`groupId` 同组豁免与异组不放行、`spawnClearanceM 0.15`。
