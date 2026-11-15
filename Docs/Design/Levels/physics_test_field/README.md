# 球体物理测试场 · 关卡数据（physics test field）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。**紧凑重排：9 格已入库**（C14 / C09 / C08 / C10 / C01 / C03 / C05 / C06 / C13），
> 全部排进 **27 m × 30.5 m** 的网格（4 列 × 3 行，1–1.5 m 走道）；每格自带一块 6×9 或 6×10 的小地面 + 4 面 **0.4 m 矮边界**。
> 其余 12 格在 `Docs/Design/BallMotor/physics_test_field.json` 的 `planned_cells[]` 里只是名字，没有几何。
> 设计文档：[../../../BallMotor/PHYSICS_TEST_FIELD.md](../../../BallMotor/PHYSICS_TEST_FIELD.md)（§9 是本目录的规矩）。

## 这个目录是什么

一台**仪器**的几何，不是一个关卡：整个测试场由 21 个互相独立的格位（cell）组成，每格测一件事
（薄片能不能碾过、0.075 的台阶算不算台阶、薄墙撞不撞得穿……）。因为电机的静态容量只有
**8 平面 / 8 盒 / 4 斜面**，整场不能同时注册，所以：

- **一格 = 一个 arena = 一份 `placement.json`**；
- 切格 = `motor.ClearObstacles()` → 目标格的 `LevelArenaHost.Reapply()`；
- 因为 `ClearObstacles` 清的是**全局**平面表，**每一格必须自带地面 + 4 面边界墙**（不是偷
  base 的，见设计 §3.4）。紧凑化后 9 份文件的 `ground` / `wall_*` **不再逐字节相同**：每格的地面
  正好是它 6×9 / 6×10 的 pad，4 面墙在 pad 边上、高 0.4 m（视觉与挡人；motor 里仍是无限半空间）。
- **所有格常显，只有当前格注册**：`PhysicsTestCell.ReleaseArena()` = `arena.SuspendRegistration()`
  （**不** `SetActive(false)`），`ApplyRegistration()` = `ResumeRegistration() + Reapply()`。
  `LevelArenaHost.Update` 在 suspended 时不写 motor；显式 `Reapply` 不受影响。

## 布局

```
Docs/Design/Levels/physics_test_field/
├── README.md                ← 本文件
├── C14/placement.json       0.028 m 薄片（3×3 板，6×9 pad）
├── C08/placement.json       台阶 0.050（4×0.05×1.5）
├── C09/placement.json       台阶 0.075（== stepLimit(0.30)，等号语义）
├── C10/placement.json       台阶 0.100（0.30 是墙、0.60 起可上）
├── C01/placement.json       5° 斜板（升 0.60，水平 6.86，宽 4）
├── C03/placement.json       25° 斜板（升 0.60，水平 1.29，宽 4）
├── C05/placement.json       45° 斜板（等号边界，必须被接受）
├── C06/placement.json       50° 斜板（必须被拒绝；板以 box 落库，AABB 当兜底墙）
└── C13/placement.json       0.6 m 台面（5×4）+ 南侧 11.31° 坡（接缝与直角边掉落同格）
```

一份文件的骨架（节选，完整见 `C09/placement.json`）：

```json
{
  "schema": "katamari.placement/0",
  "level": {
    "id": "physics_test_field/C09",
    "scene": "PhysicsTestField",
    "ballDiameterStart": 0.3, "ballDiameterMax": 2.2,
    "bounds": [[-44.0, -44.0], [44.0, 44.0]],
    "budget": {"planes": 8, "boxes": 8, "ramps": 4, "blocking": 6, "propSupports": 6},
    "expected": {"propCount": 0, "valueUnits": 0},
    "arena": {"id": "C09", "notes": "..."}
  },
  "props": [],
  "structures": [ ground, wall_x_min, wall_x_max, wall_z_min, wall_z_max, C09_step ]
}
```

- **`level.arena`** 是这套数据唯一为「多格」加的东西：它声明「这份文档是一个 arena 格」。Unity 的
  导入器用它把几何放进 `LevelContent/<id>/`，并绑定那个对象上的 `LevelArenaHost`；`levelbuild check`
  要求它的 `id` 等于**所在目录名**（复制一份文件却忘了改 id 会红）。
- **`level.bounds`** 现在是整场紧凑范围 `[[-14,-15],[14,16.5]]`，不是 88 m 场；跑道/激活矩形在判据文件里
  （`cells[].footprint`），因为它只属于运行器。盒子跑出 `bounds` 会被 `check` 拒（粗错拦截）。
- **`level.expected`** 是 0 件道具：这批九格只测几何，道具在 C18/C19。

## 斜面的作者约定（P3）

- `role=ramp` 的盒：**本地 +Y 面是行走面**（作者线 = 球滚的面），4 cm 板体挂在它下面；本地
  +X 是宽度，本地 +Z 是上坡方向。`AddRamp` 的 center 取 +Y 面中心，不是盒中心。
- `meets` 指向坡顶到达的台面；两者行走面同高（δ = 0），`levelbuild check` 会核对。
- 坡度**含等号**：45.0° 必须被接受（C05），50.0° 必须被拒绝（C06）。紧凑化后升程统一 0.60 m。
- **超过 45° 的作者门在 `levelbuild check` 里**：`role=ramp` 超过 45° 直接报错，并要求改成
  `box`（`LEVEL_AUTHORING` §4.3）。所以 C06 的 50° 板以 `role=box` + `kind=ramp_probe` 落库：
  它的世界 AABB 正好是这块 0.20 厚板的兜底墙（顶 0.60 > 2.20 档 stepLimit 0.55；底 −0.129），
  导入器把它接进 `staticBoxes`；判据导入器再从同一块板的 +Y 面烘出 probe 平面，驱动在运行时
  调 `motor.AddRamp` 取证拒绝原因。**它不是 role=ramp，所以 `ramps` 计数是 0/0；被拒的
  0/1 由驱动的 `[ptf] rampProbe ... refused=1 slope=50.00 reason=...` 行给出。** 这一格的
  `check` 会带 1 条「tilted box」warning（0 error）——那是这条作者门的正常输出，不是数据错。

## 命令

```bash
# 单格：容量 / 地面 / 通配 / 规范性（这是进仓库的门）
python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/physics_test_field/C09/placement.json

# 规范化（排序、取整、-0.0、四元数符号）——手改过就先跑这个
python3 Tools/levelbuild/levelbuild.py format Docs/Design/Levels/physics_test_field/C09/placement.json

# 出俯视图 / 3/4 图（Workbench，本机 3–4 s/张；`--preset plan` 需要文档里有 renderPresets，
# 逐格文档没有，所以预览用的是九格合并的临时文档，见设计 §2.2）
source ~/tools/blender-env.sh
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/render_views.py -- \
    --placement /tmp/ptf/preview/placement.json --preset plan --out-dir /tmp/ptf/shots
```

## 加一格（下一批照这个做）

1. 复制最近的一格作模板，改三样：目录名、`level.id`、`level.arena.id`（三处必须一致）+ 本格的盒/坡；
2. `python3 Tools/levelbuild/levelbuild.py check <新文件>`；红了就改数据，不要改预算；
3. 在 `physics_test_field.json` 里：把 `planned_cells[]` 那一行删掉，往 `cells[]` 加一条（`wired: true`、
   `placement`、`start_pose`、`footprint`、`nominal_m`、`drive`、`checks`），`expect` 数组按
   `diameters[]` 顺序；
4. `python3 -m unittest Docs.tests.levelbuild.test_physics_test_field_cells`（跨文件一致性：几何必须在
   footprint 内、`nominal_m` 必须等于台面高、每个 metric 必须在驱动里能读到）；
5. Unity 侧按设计 §9.5 重跑两个导入。

## 不要做的事

- **不要**给 `ground`/`boundary` 做「共享」优化：`ClearObstacles` 是全局的，切格后新 arena 必须自带
  地面与 4 面墙（设计 §3.4）。
- **不要**为了省一行把 `budget` 调大：`8/8/4` 是电机容量，超了会**永久 latch** overflow（不是丢弃一次）。
- **不要**在 `level.id` 里加第二层 `/`：`_LEVEL_ID_RE` 只允许一层。
- **不要**把判据写进 placement（也不要把盒的坐标写进判据 JSON）：两边的职责见设计 §9.2。
- **不要**把 50° 板写成 `role=ramp` 去「让 check 过」：它会红，而且 `AddRamp` 在
  `TryBakeRamp` 阶段就拒了，motor 的拒绝 getter 永远看不到它。C06 的 probe 约定见上一节。
