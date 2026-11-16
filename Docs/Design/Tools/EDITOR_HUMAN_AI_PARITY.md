# 编辑器人机同构（Human–AI Parity）——审计、设计、测试与路线图

> 状态：**设计文档**，2026-11-16。只做审计与设计，不包含大改；`Tools/leveleditor/server.py`、
> `web/js/view.js`、`web/js/main.js`、`web/index.html` 正由 ref-stage viewer 改动，本文
> 的所有落地建议都按「新文件优先、共享文件小步追加」执行。
>
> 关联：[LEVEL_EDITOR_WEB](LEVEL_EDITOR_WEB.md)（现有 Web 编辑器设计）、
> [LEVEL_FORMAT_V2](../Levels/LEVEL_FORMAT_V2.md)（v2 数据与 E1–E10 升级清单）、
> [`Tools/leveleditor/README.md`](../../../Tools/leveleditor/README.md)（`shot.py` 看图手册）。

## 0. 一句话结论

**今天的编辑器不是「没有 API」，而是「API 只覆盖了编辑动作的一部分，且判断/落位/坐正/
选择这些决定仍然住在浏览器 JS 里」。** GUI 能做的约 30 项里，HTTP/op 能完整复现约 18 项，
CLI 约 15 项；真正缺的是四类：

1. **服务端选择模型**（按 def/kind/region/半径/AABB 选，而不是像素点选/框选）；
2. **落位与坐正**（`resolveDrop` 的叠放/螺旋、旋转后的 `_keepOnSupport`）只在
   `main.js`/`view.js`，服务端 `add_prop`/`move` 不重放这些语义；
3. **派生/分析**（`_tierFor`、`audit`、`terrainContactReport`、相机 fit）有 Python 版本
   （stats/hints/placement_core）却没有对应 op，或反过来只在 JS；
4. **dry-run、稳定错误码、版本冲突、脚本事务**这些 agent 每天要用的“安全垫”还没有进入
   `editor_core` 的公共契约。

`shot.py`（本次交付）已经把「看」做成同一实现：**Python 规划相机/路径守卫，页面只执行**。
它证明了同构的可行形态：**决策在 Python，浏览器是渲染器，不是规则实现者。**

---

# 1. 审计（AUDIT）

## 1.1 审计口径与方法

- 代码基线：2026-11-16 工作区（含 ref-stage viewer 的并行改动）；用 CodeGraph 索引
  + 直接读源码交叉核对。
- 「GUI」= `Tools/leveleditor/web/index.html` + `web/js/main.js` + `web/js/view.js`；
  「HTTP」= `Tools/leveleditor/server.py` 的 `Handler`/`WebEditor`；
  「op」= `Tools/levelbuild/editor_core.py` 的 `Editor._ops`；
  「CLI」= `Tools/levelbuild/levelctl.py`（它是 **Blender 常驻编辑器的客户端**，不是独立实现）。
- 判定：
  - ✅ 同一实现，GUI/HTTP/CLI 至少两条路可达；
  - ⚠️ 部分可达，或同一规则有第二份实现；
  - ❌ 只在 GUI/JS，或完全没有。
- 行号是本文写作时的快照；函数名比行号稳定。

## 1.2 能力总表

| # | 能力 | GUI 入口 | HTTP / op | CLI | 逻辑所在 | 判定 / 缺口 |
|---|---|---|---|---|---|---|
| 1 | 打开关卡 | 顶部下拉 +「打开」`main.js openLevel` | `POST /api/open`；op `open` | `levelctl open` | `editor_core.op_open` | ✅ |
| 2 | 从磁盘重载 | 「从磁盘重载」`main.js:1571` | `POST /api/op {"op":"reload"}` | `levelctl reload` | `editor_core.op_reload` + `WebEditor.reload_level` | ✅ |
| 3 | 关卡列表/状态 | 下拉、状态条 | `GET /api/levels`、`/api/health`、`/api/level` | `levelctl status/list` | `WebEditor.list_levels/state_summary` | ✅ |
| 4 | 点选 / 加选 | 左键/Shift/Ctrl；`view.js _pickId` | 无（选择是 JS 状态） | 无 | `view.js` raycast | ❌ 选择模型缺失（见 1.3） |
| 5 | 框选 | 空白拖拽；`_idsInRect` | 无 | 无 | `view.js` 屏幕矩形投影 | ❌ 同上；agent 需要谓词选择 |
| 6 | 按 def/kind/region/半径选 | 仅 stats 的 region/selection 口径（不是通用选择） | `GET /api/stats?scope=region\|ids` 可返回 ids | 无 | `server.py stats` | ⚠️ 统计顺带返回 ids，不是选择 op |
| 7 | 移动（单/多选） | gizmo / 方向键 / 数值 | op `move`；多选=批量 `move` | `levelctl move` | `editor_core.op_move` + 客户端组 pivot | ⚠️ 组 pivot/吸附在 JS |
| 8 | 旋转 | gizmo（仅 Y）/ `R` | op `rotate`（yaw/quat/axis） | `levelctl rotate` | `editor_core.op_rotate` | ⚠️ op 支持四元数，GUI 仍 yaw-only（E3） |
| 9 | 旋转/移动后的支撑坐正 | 自动，`view.js _keepOnSupport` | **无自动**；只有显式 `seat` op | `levelctl seat` | **JS**（`view.js:923/957`） | ❌ 同名规则两份实现（见 1.3） |
| 10 | 吸附 | 拖动时 Ctrl；下拉步长 | 无（调用方传精确数） | 无 | JS `_applyGizmoSnap` | ⚠️ agent 可自己吸附，但没有同一实现 |
| 11 | 加道具 | 道具库拖入；`resolveDrop` | op `add_prop` | `levelctl add-prop` | `editor_core.op_add_prop` | ⚠️ 落位/叠放/螺旋只 JS |
| 12 | 加结构 | 左下面板；`addStructureFromForm` | op `add_structure` | `levelctl add-structure` | `editor_core.op_add_structure` | ✅ |
| 13 | 删除 | Delete / 面板 | op `remove` | `levelctl remove` | `editor_core.op_remove` | ✅ |
| 14 | 复制 | Ctrl+D；`duplicateSelection` | 无 `duplicate` op（客户端拼 `add_prop`） | 无 | **JS**（`main.js:1218`） | ❌ 偏移/命名策略只在 JS |
| 15 | 编辑 def / `volumeK` | 右侧 def 面板 | op `set_def` / `add_def` | `levelctl add-def`（更新走 add-def） | `editor_core.op_set_def` | ✅（E1 在途） |
| 16 | 设计总量 expected | 「同步更新 expected」 | op `expected` | `levelctl` batch/raw | `editor_core.op_expected` | ✅ |
| 17 | 区域 CRUD | 区域模式 + 列表/数字框/把手 | `GET/POST /api/regions`；`WebEditor.load/save_regions` | 无专用子命令 | Python `validate_regions` | ⚠️ HTTP 可达，CLI 未暴露 |
| 18 | 区域统计 | 右侧统计面板 | `GET /api/stats?scope=region...` | 无 | `server.py stats` | ✅（服务端唯一实现） |
| 19 | 按球径着色 / 阶梯 | 滑条 + 勾选 | `stats.tiers` 有计数；**无 per-prop tier op** | 无 | **JS `_tierFor`** + Python `stats` | ⚠️ 同一门槛两套代码 |
| 20 | 物理提示 hints | 右侧面板 | `GET /api/hints?d=` | 无 | `server.py hints` | ✅ |
| 21 | 校验 | 右侧列表 | op `check`、`/api/check` | `levelctl check` | `placement_core.validate` + `editor_core.summarize` | ✅ |
| 22 | 撤销/重做 | 顶部/快捷键 | `POST /api/op`：`undo`/`redo` | `levelctl undo` | `WebEditor._undo`（Web 侧） / `editor_core.op_undo`（Blender） | ⚠️ 两个宿主各自维护历史 |
| 23 | 相机 bookmark | GUI 预设是视图，不写文件 | op `camera`；`shot.py --save-bookmark` | `levelctl camera` | `editor_core.op_camera` | ⚠️ GUI 没有“存当前相机为 bookmark”按钮 |
| 24 | 渲染/截图 | 「截图」下载 PNG（浏览器） | **`GET /api/render`（本次新增）**；Blender 宿主 op `render` | `levelctl render --preset`（仅 Blender 常驻） | 新 `shot.py`/`shot_daemon.py`；Blender `op_render` | ✅（Web 关卡）；两套渲染后端按宿主分流 |
| 25 | 参考关卡只读 | 红黑横幅；禁用编辑 | `WebEditor.handle` 统一拒绝；`/api/ref-stage/*` 只读 | `levelctl` 不打开 `_ref_*` 的 model 模式 | Python 守卫 | ✅ |
| 26 | 父子连线 | 顶部开关 | 无 op/query（页面读 `/api/ref-stage/transforms.json`） | 无 | **JS `_buildParentLines`** | ⚠️ 数据可取，关系图无 query |
| 27 | 地形 / 碰撞叠加 | 顶部开关（仅 `_ref_*` 有） | `/api/ref-stage/stage.glb`、`collision.json` | 无 | **JS 构建** + `ref_stage` 解析 | ⚠️ 渲染仅 JS；无结构化的 terrain/collision query |
| 28 | 坐标审计 / 贴地报告 | 无按钮（`window.__leveleditor.audit`） | 无 | 无 | **JS `view.audit` / `terrainContactReport`** | ❌ 分析只在 JS |
| 29 | 运动 / 路径（v2 E9/E10） | 未实现 | 无 | 无 | 规划 | ❌ |
| 30 | 活动区 / kill plane（v2 E7） | 未实现 | 无 | 无 | 规划 | ❌ |
| 31 | 道具缩略图 / benchmark | 道具库缩略图；无 UI | 无 | 无 | JS 渲染工具 | ✅ 视图专属，不要求 op |
| 32 | 查看（本次 `shot.py`） | 浏览器自己看 | **`GET /api/render`** | **`shot.py`** | **Python 相机规划 + 页面执行** | ✅ 新基线 |

**计数**：32 项能力；✅ 14、⚠️ 11、❌ 7（不含仅视图的 31）。
真正的 ❌ 集中在 **选择模型、自动坐正、复制、分析 op、v2 新能力**。

## 1.3 只在 JS / 重复实现的逻辑（逐条 file:symbol）

| 逻辑 | 位置 | 问题 | 服务端已有的对应 |
|---|---|---|---|
| 拖放落位、叠放、螺旋找空位 | `web/js/main.js:1332 resolveDrop`、`1293 boxAt`、`1300 boxVolume`、`1306 intersectionVolume`、`1312 propBoxesOverlapping`、`1326 insideBounds` | agent 用 `add_prop` 只能给绝对坐标，无法得到人类拖放的“先叠最高、再螺旋”的结果 | `op_add_prop` 只做校验；`placement_core` 有 AABB/互穿规则 |
| 复制偏移 | `main.js:1218 duplicateSelection`（prop +0.12/+0.12，structure +0.25/+0.25） | 没有 `duplicate` op；agent 要自己复刻偏移 | 无 |
| 旋转/移动后的支撑重坐 | `view.js:923 _supportHeight`、`957 _keepOnSupport`、`994 _commitGroupTransform` | GUI 在发 `move`/`set` 之前改 Y；服务端 `move`/`rotate` 不重坐，`seat` 是另一套声明支撑算法；同一语义两份实现 | `editor_core.op_seat`（1165）、`placement_core` 支撑几何 |
| 球径分级 | `view.js:2419 _tierFor` | 与 `server.py` `WebEditor.stats` 内部的 `tier_for` 各写一遍；改一处会漂移 | `server.py stats` 的 tier 计数；`hints` 的门槛 |
| 屏幕框选/点选 | `view.js:526 _idsInRect`、`486 _pickId`、`477 _selectableGroups` | 像素语义无法被 agent 复用；没有“按谓词选择”的服务端对象 | 无 |
| OBB/AABB 几何 | `view.js:590 _obbGeometry`、`612 _makeObb`；`main.js boxAt` | 与 `placement_core.world_aabb`（442）/`structure_world_aabb`（555）重复；旋转后的世界 AABB 计算可能不一致 | `placement_core` |
| 坐标审计 | `view.js:2664 audit` | 只在浏览器；agent 要读 `window.__leveleditor.audit`（需 CDP） | 无 op |
| 地形接触报告 | `view.js:1891 terrainContactReport` | 只在浏览器，且依赖渲染场景 | 无 op |
| 相机 fit / 预设 | `web/js/camera.js:84 fit`、`98 setTop`、`107 setEye`；新 `shot.py fit_view` | 两套 fit；`shot.py` 已是可测试的 Python 版，但 GUI 仍用 JS 版 | `shot.py`（新） |
| 校验文本转 id | `main.js:708 idsInText`、`720 focusFromText` | 从人类可读 message 里抠 id；没有结构化 `{code,path,entityId}` | 无（E2） |
| 区域盒子显示/把手 | `view.js:2145 _regionBox`、`2265 _buildRegionHandles`、`2289 _beginRegionInteraction` | 交互/显示专属，规则（schema、保存）在 Python；可接受 | `validate_regions`、`save_regions` |
| 统计图/表格/CSV | `main.js:795 renderStats`、`848 renderCumulative`、`870 renderCompareTable`、`892 downloadCsv` | 显示专属；数据来自 `/api/stats`；CSV 由 JSON 派生，可接受 | `/api/stats` |
| 截图/缩略图/benchmark | `view.js screenshot`、`renderThumbnail`、`benchmarkFrames` | 视图专属，不需要编辑 op | 无 |

**判断标准**：只影响“像素怎么画/怎么点”的可以留在 JS；**会影响文件字节、校验结果、
可吸性、支撑/父子派生、放置结果**的必须回到 Python。上表前 10 行都属于后者或复制了后者。

## 1.4 本次 `shot.py` 已收进同构的部分（view ops）

- CLI：`Tools/leveleditor/shot.py <level-id>`；HTTP：`GET /api/render`；同一 `render_request`。
- 相机规划全部在 Python（`shot.py view_specs/fit_view/start_view/focus_box/region_box/
  bookmark_view`），页面 `web/js/shot.js` 只应用 `(position,target,fov)`、图层和导出。
- 图层：props/structures/terrain/collision/parents/grid/wire/labels/tier/ball/select；
  参考关卡的地形/碰撞/父子只来自 `/api/ref-stage/*`，普通关卡会明确报 `unavailable`。
- 参考守卫：`_ref_*` 只能写 `~/KatamariRef/viewer_evidence/shots/`，仓库内 `--out` 直接拒绝，
  水印 + PNG `tEXt` 溯源；渲染只读。
- 元数据：`camera/views`、`levelHash`、`propCount`、`layers.applied/unavailable`、
  `visibleBox`、`consoleErrors`、`timings`、`warm`、`bookmark`、`sheet`、`labels`。
- 这证明了目标形态：**Python 决定“看什么/怎么框”，浏览器只执行**；编辑/查询应沿用同一形态。

## 1.5 参考关卡与宿主分流

- `_ref_*`：`WebEditor.handle` 在入口统一拒绝所有 mutating op、`export`、`undo/redo`、
  `batch`（`server.py` 约 520 行）；查询（`/api/level`、`/api/stats`、`/api/ref-stage/*`）
  和渲染（`shot.py`）可用。**agent 的 query/render 必须继续可用，写必须继续 403。**
- Blender 常驻编辑器（`levelserve.py` + `blender/editor_server.py`）和 Web 编辑器共享
  `editor_core`；但 `render` op 只在 Blender 场景适配器实现（`WebScene.render` 明确拒绝）。
  `shot.py` 是 Web 关卡的渲染路径；两者不要互相冒充。

---

# 2. 设计（DESIGN）

## 2.1 三条原则

1. **一个操作注册表**：编辑动作的名字、参数、结果、撤销标签、dry-run、错误码都写在
   `editor_core` 里；GUI/HTTP/CLI 都从注册表派生，不能各自有一份“支持哪些 op”的列表。
2. **决策在 Python，表现层只执行**：落位、坐正、派生支撑、tier、校验、统计、相机、
   选择求解全部 Python；JS 只负责手势、拾取、绘制、相机执行。
3. **agent 的默认安全**：dry-run 先看 diff；写有版本号；失败有稳定 code；批量脚本是
   一个 undo 步骤；参考关卡只读但完全可查/可看。

## 2.2 操作注册表（command pattern）

在 `Tools/levelbuild/editor_core.py` 增加（名字示意，不改变现有 JSON 形状）：

```python
@dataclass(frozen=True)
class OpSpec:
    name: str                 # "add_prop"
    params: dict              # JSON Schema 子集（type/properties/required/enum/items）
    result: dict              # 结果 schema（至少 type/required）
    mutating: bool
    undo: bool                # 是否进 undo/redo
    dry_run: bool             # 是否支持 dryRun
    errors: tuple[str, ...]   # 该 op 可能返回的稳定 code
    summary: str              # 一行人类说明（同一份给 --ops/文档/GUI tooltip）
    aliases: tuple[str, ...]
```

派生关系：

- `Editor._ops` 注册 handler；`OP_SPECS` 必须一一对应（测试保证无孤儿、无缺失）；
- `levelctl.py` / 新 `ed.py` 的 `<op> --help` 从 schema 生成；
- `POST /api/op` 先按 schema 校验参数，再进 handler；未知字段错误带 `path`；
- `GET /api/ops`（或 `ed.py --ops --json`）输出 `{schemaVersion, ops:[...]}`，agent 自描述；
- GUI 动作清单（3.2）引用同一批名字。

**兼容**：现有 op 名字、参数、`{ok,result,error,details}` 响应不变；注册表只是把隐含契约
变成数据。`batch` 作为一等事务：`{"ops":[...]}` 全部成功才写一次，一个 undo 标签。

## 2.3 查询层与选择模型（agent 的“眼睛和手”）

### 2.3.1 选择器（stateless selector）

```json
{
  "ids": ["cup_03"],
  "def": ["cup", "bowl"],
  "kind": "prop",
  "zone": "pocket",
  "region": "pocket",
  "radiusM": {"center": [-12.5, 0.15, -12.5], "radius": 3.0},
  "aabb": {"min": [-15, 0, -15], "max": [-10, 2, -10]},
  "where": [{"field": "valueUnits", "op": ">=", "value": 10}]
}
```

语义：全部条件是 AND；`ids` 与其他条件同时给出时取交集；未知字段报 `E_ARG`。不保存
服务端会话状态；`query` 返回 `{ids, count, bounds, totals, sha256}`，`sha256` 是
“选择器 + 文档版本”的指纹，便于脚本幂等。**不做**“选择手柄”式服务端长连接状态，避免
两个客户端/两次请求之间的隐式状态漂移。

### 2.3.2 query op

新增只读 op `query`（或在 `GET /api/query`）：

| kind | 参数 | 返回 |
|---|---|---|
| `get` | `id` | prop/structure/def/camera/preset + derived |
| `find` | selector + `limit/cursor/sort` | 行（itemId/def/position/dReqMm/valueUnits/zone）+ 总数 |
| `nearest` | `to:[x,y,z]`, `n`, selector | 最近 n 个 + 距离 |
| `overlaps` | selector / `aabb` | 相交对（世界 AABB 粗筛，标注 OBB 精确度） |
| `stats` | 复用 `WebEditor.stats` 参数 | 与 `/api/stats` 完全一致 |
| `tiers` | `diameter` + selector | 每个 id 的 `tier`（green/yellow/red/grey）+ 门槛 mm |
| `supports` | selector | 每个 prop 的声明支撑/几何支撑/`rise`/是否可站（复用 `hints` 的规则） |
| `parents` | selector | 父子链（`group`/`groupId`/ref-stage `parentBlock`） |
| `validate` | — | 结构化错误 `{code,path,entityId,message}` |
| `diff` | `fromSha`/`toSha` 或两段 ops | 与 `WebEditor.diff` 同形状 |
| `terrain` / `collision` | level | 参考关卡 stage 的计数/zone/transform 摘要（不返回网格） |
| `view` | `shot.py` 参数 | 相机计划 + 可见 bbox（不渲染）；`render` 仍是独立 op |

### 2.3.3 写操作接受 selector

`move/rotate/remove/seat/set/duplicate/place` 等接受 `selector` 代替 `id/ids`；服务端解析成
ids 并把**解析结果写进 undo 标签**（`move 3 props (def=cup, zone=pocket)`）。这样脚本可以
说“把 region=pocket 里所有 def=cup 的件往 (dx,dz) 移”，而不需要在客户端读全量文档。

## 2.4 dry-run（先看 diff，再决定写不写）

- 所有 `mutating && dry_run` 的 op 支持 `"dryRun": true`；`ed.py --dry-run` / HTTP 同名字段。
- 行为：深拷贝文档 → 执行 → `placement_core.validate` → `expected` 门 → 计算 canonical
  bytes/hash → 返回 `{wouldWrite, diff, gate, errors, sha256Before, sha256After}`；
  **不写文件、不进 undo、不 publish SSE**。
- 与真写的对应关系由测试保证：`dryRun.diff == commit.diff`，`sha256After == 落盘 sha`。
- `batch` 的 dry-run 是整批一次；任何一步失败则整批不写。

## 2.5 稳定错误码

| code | HTTP | 含义 | 例子 |
|---|---|---|---|
| `E_ARG` | 400 | 参数缺失/类型错/未知字段 | `move` 同时给 to 和 delta |
| `E_UNKNOWN_OP` | 400 | 未注册 op | `frobnicate` |
| `E_NOT_FOUND` | 404 | id/def/camera/region 不存在 | `focus:nope` |
| `E_VALIDATION` | 422 | 文档校验失败（带 `errors[]`） | 穿过边界 |
| `E_GATE` | 422 | `level.expected` 门拒绝 | 加件后超预算 |
| `E_CONFLICT` | 409 | 磁盘版本 ≠ 期望版本 | 人刚在 GUI 改了 |
| `E_READ_ONLY_REF` | 403 | `_ref_*` 写被拒 | `_ref_cancer add_prop` |
| `E_IO` | 500 | 读写/原子替换失败 | 磁盘满 |
| `E_UNSUPPORTED` | 501 | 该宿主没有此能力 | Web 宿主的 Blender `render` |
| `E_DRY_RUN` | 200 | 非错误：dry-run 结果 | `dryRun:true` |

错误体保持 `{ok:false,error,code,details:[{code,path,message}]}`；`details` 的 `entityId`
让 GUI 能点条目定位（E2）。

## 2.6 agent 入口

### 2.6.1 `ed.py`（新文件，薄客户端）

```bash
# 一条 op；参数既可 --k v 也可 --json
python3 Tools/leveleditor/ed.py story_01_bench_size find \
  --json '{"def":["cup"],"region":"pocket"}' --limit 50
python3 Tools/leveleditor/ed.py story_01_bench_size move \
  --json '{"selector":{"def":"cup","region":"pocket"},"delta":[0,0,0.5]}' --dry-run
# 脚本事务（一行一个 op；默认整批一个 undo，全有或全无）
python3 Tools/leveleditor/ed.py story_01_bench_size --script ops.ndjson --atomic
# 自描述
python3 Tools/leveleditor/ed.py --ops --json
```

- 复用 `server.py` 的 token/state/HTTP；默认连本机 `127.0.0.1:8777`，`--server URL` 可换；
- 自动起一个只读/写自己的编辑服务（像 `shot.py` 的 daemon，但仅当没有现成服务时）；
- 输出一行 JSON；退出码 0/1/2（2=需要人决策的冲突/门）。

### 2.6.2 HTTP

- `POST /api/op`：现有入口，加 schema 校验/错误码；
- `GET /api/query?...`：只读查询（与 `query` op 同实现）；
- `GET /api/ops`：注册表自描述；
- `GET /api/render`：已有；
- `POST /api/op` 接受 `dryRun`、`expectedSha256`。

### 2.6.3 与 GUI 共存

- **一个文件一个写锁**（现有 `LevelLock`）；agent 的常驻服务可以 `--no-lock` 只读/渲染，
  但写必须拿锁。
- **乐观并发**：写请求可带 `expectedSha256`（`/api/level` 的 `state.file.sha256`）；
  服务端写完前比较，不匹配返回 `E_CONFLICT` + 当前 sha + 建议（`reload`/重放）。
- **双向可见**：Web 服务已有 SSE；agent 写成功后页面 `scheduleRefresh` 自动重拉；
  人写后 agent 的下一次 op 由现有 `disk_conflict` 拒绝，提示先 `reload`。
- **不合并历史**：GUI 的 undo 在 `WebEditor` 内存，agent 的 undo 在同一个 Web 服务的
  历史里（如果连的是同一个服务）；两个服务各自的历史不互通——文档里必须写明“undo 是
  会话级”，文件才是真源。

## 2.7 参考关卡（`_ref_*`）

- 写：`E_READ_ONLY_REF`（403/200+ok:false，沿用现有文案）；
- 读：`query`、`/api/stats`、`/api/ref-stage/*`、`shot.py` 全部可用；
- 输出：沿用 `shot.py` 的 evidence 根守卫；文档/测试/截图不得进仓库；
- 参考的 terrain/collision/parent 只报告摘要（计数、zone、bbox、frame），**不把网格/三角
  数据塞进通用 op 结果**；需要像素就 `shot.py`，需要几何就专门的只读端点。

## 2.8 LEVEL_FORMAT_V2 与 E1–E10 合并成 op

| 事项 | 新/改 op | 只读 query | GUI | 工作量 | 依赖 |
|---|---|---|---|---|---|
| E1 `volumeK`/`volumeMm3` | `set_def`（已有） | `get def` 返回派生 | 已有面板 | S | v2 派生字段 |
| E2 稳定错误码 | 所有 op 的 `errors` | `validate` | 错误面板按 code 分组 | S–M | 注册表 |
| E3 真四元数旋转 | `rotate`（已有 quat） + GUI quat gizmo | `get rotationQuat` | TransformControls quaternion | M | OBB 互穿 |
| E4 parent/派生支撑 | `set parent`、`apply_support_preview` | `parents`、`supports`、`preview_support` | 连线 + 预览按钮 | M | E3 + v2 `parent` |
| E5 地形查看 | —（只读） | `terrain`（摘要） | three.js 网格 | L | 侧车格式 |
| E6 碰撞叠加 | —（只读） | `collision`（AABB/三角计数/zone） | 线框/分类着色 | M | E5 |
| E7 活动区 gizmo | `set_activity_area`、`set_wall`、`set_kill_plane` | `activity_area` | 多边形/滑条 | M | 校验器 |
| E8 4000 件浏览 | — | `find` 分页/排序 | 虚拟列表 | M | §12.4 |
| E9 运动预览 | `set_motion`、`preview_motion` | `motion` | 时间轴 | M | E3/E4 |
| E10 路径 gizmo | `set_path`、`preview_path` | `paths` | 折线编辑 | M | E9 |

**顺序**：E1/E2 → 注册表/查询 → E3/E4 → E5/E6 → E7 → E8 → E9/E10。所有 v2 字段名以
`LEVEL_FORMAT_V2.md` 为准；本文不新增字段名，只定义“操作/查询”的形状。

## 2.9 视图 op 与编辑 op 的关系

- `shot.py` 的 `view` 计划就是“相机 op”的只读版本：`query.view` 返回同一套
  `camera/views/visibleBox`，不渲染；`render` 在它之上执行。
- `--save-bookmark` 已经是 `camera` op 的薄封装：显式、可审计、参考关卡拒绝。
- GUI 的四个相机预设（`camera.js`）应逐步改为调用 `shot.py` 的规划（同一 fit 数学），
  但页面仍可用自己的轨道/飞行；**不需要**把交互相机变成服务端状态。

---

# 3. 测试策略（PARITY TESTS）

## 3.1 注册表枚举（必做，最先）

`Docs/tests/leveleditor/test_editor_parity.py`：

1. `OP_SPECS` 的 key 集合 == `Editor._ops` 的 key 集合（含 aliases 指向）；
2. 每个 spec 的 params/result 是合法 JSON Schema 子集，且示例能通过校验；
3. 每个 mutating op 有 `undo` 标签测试；每个 op 至少一个成功/一个失败样例；
4. `GET /api/ops` 与 `ed.py --ops --json` 输出一致（同一 Python 函数）；
5. 每个 op 在 `levelctl` 或 `ed.py` 至少一个入口（或有 `agentOnly/guiOnly` 白名单）。

## 3.2 GUI 动作清单（manifest）

新增 `Docs/Design/Tools/editor_gui_actions.json`：

```json
{"actions": [
  {"id": "transform.move", "label": "gizmo 移动", "selector": "#canvas",
   "op": "move", "params": ["id","to"], "clientPrep": ["snap"],
   "evidence": "main.js bindUI"},
  {"id": "transform.rotate", "label": "gizmo 旋转", "op": "rotate",
   "params": ["id","quat"], "clientPrep": ["support_reseat"]}
]}
```

测试：

- manifest 的每个 `op` 必须存在于注册表；
- 每个 mutating op 要么出现在 manifest，要么标记 `agentOnly` 并写原因；
- `clientPrep` 里出现 `support_reseat`/`drop`/`snap` 的条目，必须在 P4 之前有对应 op
  或明确“纯手势”白名单（snap 可纯手势，support_reseat 不可以）。
- 清单由 Lead 维护；GUI 改按钮时同一 PR 更新 manifest（人工 review 项）。

## 3.3 JS“无编辑/分析数学”lint

`Docs/tools/check_editor_js_logic.py`（新脚本，CI/`unittest` 调用）：

- 扫描 `web/js/*.js`，命中以下模式即失败：
  - `Math.floor(...*1000` / `dReq` / `tier`（门槛计算）；
  - `worldAabb|intersectionVolume|overlaps|supportHeight|keepOnSupport`（几何/坐正）；
  - `fit\\(|setTop|setEye`（相机 fit，允许 `shot.js` 执行既定 camera）；
  - `stats|histogram|percentile`（统计，允许显示）。
- 允许清单 `Docs/tools/editor_js_allowlist.json`：`file`, `symbol`, `reason`,
  `expires`（日期）。只允许“像素/手势/显示”类，例如 `_pickId`、`_idsInRect`、
  `_makeObb`（绘制）、`renderStats`（显示）、`shot.js applyCamera`（执行）。
- 新命中默认失败；加白名单必须在 PR 里写理由和到期日。

## 3.4 CLI == HTTP == GUI payload

- 同一 op 的同一 JSON 分别走：`ed.py`、`POST /api/op`、GUI 的 `sendOp`（用浏览器冒烟或
  直接构造同一 payload）；比较 `result`（去掉 timings/paths）、`diff`、落盘 `sha256`。
- 用临时关卡副本（`--levels-dir`）和临时 state/token；不碰仓库 `placement.json`。
- 覆盖：move/rotate/set/set_def/remove/seat/expected/camera/undo/batch。

## 3.5 dry-run == commit

对每个 mutating op 的样例：

1. `dryRun:true` → 记录 `diff/sha256After/errors`；
2. 真写 → 记录 `diff/sha256`；
3. 断言 diff 相同、sha 相同、dry-run 前后文件字节不变、undo 深度不变、SSE 版本不变。

## 3.6 golden scripts（NDJSON）

固定夹具（临时副本）：

```text
add 3 props -> select def -> move all by (0,0,0.5) -> rotate one 30deg
-> seat -> dry-run remove one -> remove -> undo -> check sha
```

断言每一步的 result 摘要、文件 sha 序列、undo 标签；同一脚本在 CLI/HTTP 两条路各跑一次，
结果必须一致。脚本文件进 `Docs/tests/leveleditor/fixtures/`（不含参考关卡资产）。

## 3.7 视图 op 测试

- Python 单测：top/iso/start/focus/region 相机包含 8 角、tile/`max-px`、ref 路径守卫、
  JSON 契约（已有 `test_shot.py` 23 项，全绿）。
- 浏览器 e2e：无头 Chromium 可用时跑一张真实 top（临时 level/state/port），断言 PNG
  尺寸/路径/元数据；没有浏览器时 skip（已有）。
- **不做**跨机器像素 golden：software GL 的着色器/驱动会漂移；改为结构断言
  （camera/visibleBox/layers/尺寸/非空像素）+ 可选的同机 sha 回归。
- 参考关卡截图永不进仓库；测试只写临时/`viewer_evidence` 根。

## 3.8 冲突与只读

- 两个客户端：A 写后 B 带旧 `expectedSha256` → `E_CONFLICT`，文件不变；
- `_ref_cancer` 上所有 mutating op + `batch` + `export` → `E_READ_ONLY_REF`，文件 sha 不变；
- `_ref_*` 的 query/render 成功；输出路径守卫（已有单测）。

## 3.9 证据

- 每次 phase 的验收：`python3 -m unittest discover -s Docs/tests`、
  `python3 Docs/tools/validate_plan.py`、parity lint、golden scripts；
- Unity 测试不在本路线图内（编辑器不动 Unity）；只在需要导入器配合时由 Lead 另派。

---

# 4. 分阶段计划

| 阶段 | 交付 | 工作量 | 依赖 | 与 agent-69 的冲突 | 出口门 |
|---|---|---|---|---|---|
| **P0 冻结** | 本文 + `editor_gui_actions.json` 草案 + 错误码表；不改代码 | **S** | owner 对 §5 的方向 | 无（纯文档） | owner 确认 D1/D2/D4/D5 |
| **P1 注册表 + 错误码 + dry-run** | `OpSpec`/`OP_SPECS`、`GET /api/ops`、`E_*`、`dryRun` | **M** | P0 | `server.py` 的 `handle`/路由；`editor_core` 的 `_ops` | 3.1/3.5 绿；现有测试全绿 |
| **P2 查询 + 选择器** | `query` op、selector、`find/nearest/overlaps/tiers/supports/parents` | **M** | P1 | 新文件 `editor_query.py`；`server.py` 只加 GET | 3.4/3.6 的查询部分；性能 <100 ms/1k props |
| **P3 `ed.py` + HTTP query** | `ed.py`、`--script --atomic`、`GET /api/query`、token/state 复用 | **M** | P2 | `server.py` 路由小追加 | CLI==HTTP golden；`--ops` 自描述 |
| **P4 把 JS 编辑逻辑搬回 Python** | `place_prop`（drop/叠放/螺旋）、`duplicate`、`transform`（含 `support_reseat`）、GUI 改调 op | **M** | P1/P2；agent-69 的 `view.js` 改动落地后 | **直接冲突**：`view.js _commitGroupTransform`、`main.js resolveDrop/duplicateSelection` | 3.3 lint 允许清单减少；同一个拖放/旋转在 GUI/CLI 产生同 sha |
| **P5 视图 op 收敛** | `query.view`、GUI 相机预设用 `shot.py` 规划、截图走 `render` | **S** | P2；shot.py 已就绪 | `main.js` 相机按钮、`view.js` 预设 | 3.7；GUI 预设与 `shot.py` 相机一致 |
| **P6 v2 能力** | E3/E4（四元数/父链/支撑预览）→ E5/E6（地形/碰撞）→ E7（活动区）→ E9/E10（运动/路径） | **L**（按月拆） | `LEVEL_FORMAT_V2` 侧车/校验器；E9/E10 需 schema 冻结 | ref-stage 已由 agent-69 在做 E5/E6 的查看层，**复用其 `/api/ref-stage`**，不要另建 | 每项有截图 + 只读 ref 回归 + query 契约 |
| **P7 收尾** | lint 允许清单清零（只留视图）、golden CI、删除临时白名单；文档 | **S** | P4/P5 | 无 | 3.1–3.8 全绿；`validate_plan.py` 全绿 |

**顺序理由**：P1/P2 是所有入口的地基；P4 会碰正在被改的共享文件，必须等 ref-stage 的
`view.js/main.js` 稳定后再做，且先以**新 op + 新文件**落地，最后才删 JS 分支。P6 的
地形/碰撞查看层已由 agent-69 提供，编辑侧只补 op/query，避免重复。

**工作量口径**：S ≤ 半天，M 1–3 天，L > 3 天（与 `LEVEL_FORMAT_V2` §14.2 一致）。

---

# 5. 需要 owner 决策（带建议）

| # | 决策 | 选项 | 建议 | 理由 |
|---|---|---|---|---|
| D1 | agent CLI 的落点 | 扩 `levelctl` / 新 `ed.py` | **新 `ed.py`** | `levelctl` 是 Blender 常驻编辑器的客户端，语义绑定 Blender；`ed.py` 面向 Web/无头，能复用 token/state 和 query |
| D2 | 选择是否服务端有状态 | stateless selector / 服务端 session handle | **stateless** | 两个 agent、SSE 重连、脚本重放都不会有隐式状态；指纹足以做幂等 |
| D3 | dry-run 是否允许临时导出 | 纯内存 / 写临时文件 | **纯内存** | 不产生可被误用的半成品；sha 用 canonical bytes 在内存算 |
| D4 | GUI 与 agent 同时写 | 单写锁 + 乐观版本 / 谁先谁赢 | **单写锁 + `expectedSha256`** | 现有 `LevelLock` 与 `disk_conflict` 已具备；缺的是“先比较再写”的显式契约 |
| D5 | JS 是否保留视图数学 | 全部搬走 / 允许视图专属 | **允许视图专属 + lint 白名单** | 拾取/绘制/相机执行必须用浏览器；编辑/分析数学不准留在 JS |
| D6 | v2 的 E7/E9/E10 是否现在定 schema | 现在定 / 等格式研究 | **等 `LEVEL_FORMAT_V2` 冻结**；先记需求和 query/op 形状 | AGENTS：schema 研究未定前只记需求，不提前定字段名 |
| D7 | 参考关卡截图/黄金图 | 进仓库 / 只本地 | **只本地**（`viewer_evidence`）；测试用临时目录 | 未授权资产不能进 git/上传；像素 golden 也不跨机 |
| D8 | agent 是否可写 cameras/bookmarks | 禁止 / 显式允许 | **显式允许，但只在 `--save-bookmark`/`camera` op** | 需求要复用相机；默认不改文件，参考关卡拒绝 |
| D9 | GUI 动作清单谁维护 | Lead / 编辑器 owner | **Lead 维护，编辑器 PR 必须更新** | 与 AGENTS 的 parity 规则一致，避免清单腐烂 |

---

# 6. 非目标 / 边界

- 不在本阶段重写 `view.js`/`main.js`；P4 之前共享文件只做小追加。
- 不把 Unity 导入器/运行时纳入 op；编辑器仍不碰 Unity。
- 不承诺跨机器像素一致（software GL / 驱动差异）；只承诺相机、图层、元数据、文件字节。
- 不让参考关卡的网格/三角数据进入通用 JSON 结果；看图仍是 PNG，几何走专用只读端点。
- `render` op 仍是 Blender 宿主的能力；Web 宿主的看图/渲染走 `shot.py` / `GET /api/render`。

---

# 7. 验收清单（这份文档的“完成”）

- [x] 能力总表覆盖 GUI/HTTP/CLI/JS-only，并指出 7 个 ❌、11 个 ⚠️。
- [x] 每个只在 JS 的编辑/分析逻辑有 `file:symbol` 和 Python 对应物。
- [x] 注册表、selector、dry-run、错误码、`ed.py`、query、共存、参考模式、v2 映射均有形状。
- [x] 测试策略覆盖枚举/清单/lint/CLI==HTTP/dry-run/golden/视图/冲突/只读。
- [x] 分阶段有工作量、依赖、与 agent-69 的冲突点、顺序和出口门。
- [x] owner 决策 9 条，每条有建议和理由。
