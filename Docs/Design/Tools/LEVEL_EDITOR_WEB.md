# 浏览器关卡编辑器（Web Level Editor）

> 状态：**phase 1 可用**，2026-11-15。实现：`Tools/leveleditor/`。真源仍是
> `Docs/Design/Levels/<id>/placement.json`；Unity 导入器仍只做单向导入。
> 相关的数据/规则文档：[LEVEL_AUTHORING](LEVEL_AUTHORING.md)、
> [`Tools/levelbuild/README.md`](../../../Tools/levelbuild/README.md)。
>
> Agent 看图：`python3 Tools/leveleditor/shot.py <level-id>` 或 `GET /api/render`
> （headless 渲染、2×2 联络表、tier/地形/碰撞图层）；6 个配方见
> [`Tools/leveleditor/README.md`](../../../Tools/leveleditor/README.md#42-agent-cookbook-looking-at-a-level给-llm脚本)。

## 0. 一句话

项目主人要的是一个**能在浏览器里直接操作**的关卡编辑器：走图、拖道具、移动/旋转、
按球径看「现在能吸什么」、每次改动立即校验并写回同一份 `placement.json`。
所以这里做一个本地的 **three.js 前端 + Python 标准库后端**，后端只做传输与状态包装，
**校验、门、规范字节、原子导出全部复用 `Tools/levelbuild` 的现有实现**。

它不是：

- 不是 Unity 的第二个导入器，也不是 Unity 运行时；
- 不是 Blender 的替代品（没有渲染服务、没有 `.blend`）；
- 不是新的关卡格式，不产生第二份真源。

## 1. 为什么是 Web，不是「把 Blender 面板给主人」

已有的常驻编辑器（`levelserve.py` + `levelctl.py`）本质是一个 **headless Blender +
CLI**：能力齐全，但主人无法在 Windows 上直接操作。可选路线只有三条：

| 路线 | 主人能用吗 | 代价 |
|---|---|---|
| Windows Blender GUI 面板 | 能，但要装/开 Blender 5.3，面板还没做 | 重、和 Unity 抢机器、面板是第二套 UI |
| Blender headless + 网页遥控 | 能，但仍要一台 Linux Blender 常驻 | 每开一关都要 Blender；2 核机器上与其它任务抢 CPU |
| **浏览器 three.js + Python 后端** | 能，打开网址即可 | 需要自己画结构/道具；但道具已有 GLB，结构本来就是盒子 |

选第三条。关键判断：**这一关的内容几乎全是「盒子 + 真实尺寸 GLB」**，
three.js 足够；而「校验/导出」不需要重写——把浏览器接到现有 Python 核心上即可。
于是得到一条很短的链路：

```
浏览器（three.js） ──JSON op──► server.py ──► editor_core.Editor ──► placement_core
      ▲                              │                 │
      └──── /api/level + SSE ◄───────┘                 └── 规范字节 + 原子写
                                                              │
                                                   placement.json（真源）
```

## 2. 架构

```
Tools/leveleditor/server.py                 （前台进程，默认 0.0.0.0:8777）
├─ 静态文件：web/（index.html / js / css / vendor/three）
├─ GET  /api/health  /api/meta  /api/levels  /api/level  /api/check  /api/hints
│       /api/models/<def>.glb  /api/events（SSE）
├─ POST /api/op    {"op": …, "args": …}      ← 和 levelctl.py 同一套 op
├─ POST /api/open  {"level": "<id>"}
├─ WebEditor
│   ├─ editor_core.Editor(WebScene())        ← 复用：校验、expected 门、规范导出
│   ├─ 快照历史（undo/redo）+ diff 摘要
│   ├─ 每关文件锁（flock，状态目录）
│   ├─ 写前冲突检测（磁盘文档 vs 内存文档）
│   └─ SSE 版本号（多标签页/多客户端）
└─ WebScene：没有 Blender 的 scene 适配器（只记计数；真正的 3D 在浏览器）
```

**为什么不是 WebSocket**：页面只需要「有人保存了，去重拉一次 state」这一种单向推送。
SSE 在 `http.server` 上就是一段 `text/event-stream`，零依赖、断线自动重连；
WebSocket 需要自己实现握手/分帧（标准库没有）。设计上明确选 SSE，见 `server.py`
的 `_serve_events`。

**为什么没有 aiohttp**：本机离线环境没有 aiohttp（也不该为这个工具新增依赖）；
`ThreadingHTTPServer` + 一个 `RLock` 足够：编辑器是单写者，请求串行化在 `WebEditor.mu`。

## 3. 真源、写路径、撤销

### 3.1 每次编辑的最后一步是导出

所有会改文档的 op 都走 `editor_core.Editor._commit`：

1. 校验新文档（`placement_core.validate`），检查「有没有引入本会话没见过的硬错误」；
2. 通过后**先**导出规范形（`dump_doc` → tmp 文件 → `os.replace` 原子替换）；
3. 再同步 scene（这里 `WebScene.sync` 是 no-op，只返回计数）；
4. 失败则回滚文档并报错，文件不动。

因此：**文件永远不会领先/落后于编辑器**，崩溃最多丢在途的一条请求；每次成功保存都返回
`sha256`/`bytes`，页面显示「已保存 +n ~n -n」。

### 3.2 撤销/重做

`editor_core.Editor` 自带 `undo`，但 Web 端还需要：

- **redo**；
- 撤销后给页面一份「这次撤销改了什么」；
- 和页面自己的编辑队列一致的历史标签。

所以 `WebEditor` 维护一个有界快照栈（默认 60 条），每条包含 `before`/`after` 文档、
标签和 diff。撤销/重做执行：

1. 取快照，**用 `Editor._write`（同一个原子写函数）写盘**；
2. 调 `Editor.open(None, path)` 重读、重校验、重建 `known_errors`/scene 计数；
3. 计算 diff、递增 SSE 版本、返回新的 `saved` 摘要。

没有第二套字节格式：undo 写出的文件和正常保存完全同源（测试里用 sha 断言）。
**一个实现原则**：历史/差异是编辑器服务器的职责；校验/导出/门永远只有
`placement_core` + `Editor` 那一份。

### 3.3 单写者

- 一份关卡一个 `flock`（`~/.local/state/vrc-katamari-leveleditor/locks/v2-<sha256>.lock`），
  第二个 Web 编辑器启动时直接拒绝；锁体是 `{pid, port, level, placement}`，拒绝信息会
  指明持有者的 pid/端口/关卡。旧命名 `<sha1-16>.lock` 作为兼容锁同时持有，避免滚动重启
  期间旧版本进程和新版本同时写。**重新打开当前关卡 = 原地 reload，不重新抢锁**；切换
  关卡先拿新锁再放旧锁，失败时旧关卡/旧锁/历史保持原样。
- 每次写前做**语义冲突检测**：把磁盘上的 JSON 按 `canonical_doc` 和内存文档比较；
  不同就拒绝（提示「文件在磁盘上变了，按 Reload」）。这样即使 Blender 常驻编辑器
  或人手改了同一份文件，也不会被静默覆盖。
- `--no-lock` 只给测试用。

### 3.4 字节往返

`export → 重新 open → export` 逐字节相同（`test_the_file_is_the_same_document_after_a_rebuild`
和 `levelbuild` 自己的往返测试）。Web 端没有另写序列化：`saved.sha256` 就是
`Editor._write` 返回的 sha。

## 4. 坐标约定（唯一转换点）

`placement.json` 是 **Unity 世界坐标**：左手、Y 向上、米、四元数 `(x,y,z,w)`。
three.js 是右手、-Z 朝前。前端 `js/view.js` 顶部只放一对互逆函数：

```
position:  (x, y, z)      →  (x, y, -z)
rotation:  (x, y, z, w)   →  (-x, -y, z, w)
```

- 位置取反 Z 是一次镜像（行列式 -1），所以四元数的向量部分 x、y 取反；这样
  Unity 里「绕 +Y 顺时针 30°」在 three 里仍是「从上往下看顺时针 30°」。这对映射
  **自逆**，正反两个方向都用它。
- `aabb` 是**未旋转的模型 AABB，Unity 轴向** `(x, y=高, z)`；GLB 是 Blender
  `export_yup` 导出的，局部轴与这份 `aabb` 一致（x 宽、y 高、z 深）。所以
  `Box3.setFromObject(propGroup)` 的世界 AABB 可以直接和 `placement_core.world_aabb`
  比较——这正是坐标审计（§8）。
- 道具 `position` 是 AABB 中心（model pivot = center），贴地 = `y = 地面 + aabb.y/2`。
- 结构：
  - `box` / `ramp`：`center` + `size` + `quat`，就是一个盒子；**`ramp` 的局部 +Y 面是
    行走面**，板体在它下面（Unity `AddRamp` 也从这一面取中心）。
  - `ground` / `boundary`：`placement_core` 的约定是「**过 center 的平面，局部 +Y 是法线**」；
    Unity 导入器的可见体是这个盒子沿 authored **-Y** 平移半个厚度（地面顶面 =
    平面，墙的内表面 = 平面）。前端完全照这个规则画：
    `group.position = center`、`group.quaternion = quat`，mesh 局部
    `position.y = -size.y/2`。因此画出来的可见面和 Unity 一致。
  - `boundary` 的局部 +Y 指向场内；前端在标记原点画一个朝 +Y 的箭头（场内）。
- 相机：`cameras[]` 的 `location/target/lens` 也按同一映射；`lens` 是 36 mm 传感器
  的水平焦距，转成 three 的**垂直** fov：
  `h = 2·atan(18/lens)`，`v = 2·atan(tan(h/2)/aspect)`。
  `ortho_top` 用 `OrthographicCamera`，`coverage_m` 是**短边**入画范围。

### 4.1 已知锚点（坐标审计）

| 对象 | 期望（Unity 世界） | 来源 |
|---|---|---|
| `struct_lounge_chair_01` | 世界 AABB x[-12.6,-12.0], z[-10.4,-8.8], y[0,0.35] | `story_01/placement.json` + 设计注记 |
| `apple_04`（在花圃台上） | 底面 y = 0.26（`flower_bed` 顶面） | `support=flower_bed` |
| `flower_bed` | center y=0.13, size.y=0.26 → 顶面 0.26 | 同上 |
| `ground` | 过中心平面 y=0（size.y=0.04 只是可见板厚） | `ground_level=0` |
| `ramp_to_table` | 坡度 30.0085°，行走面法线 ≈ (0, 0.866, -0.500)，面上一点 (-13.1, 0.35, -10.106) | `structure_top_plane` |
| `boundary_x0` / `boundary_x1` / `boundary_z1` | 局部 +Y = (+1,0,0) / (-1,0,0) / (0,0,-1) | 边界向内 |
| `physics_test_field/C01` | 0 道具、7 结构、地心 (-10.5,0,11)、5° 坡 | `C01/placement.json` |

浏览器端 `view.audit()` 对 127 件道具逐一比较 `Box3` 与服务器给的 `worldAabb`
（容差 0.01 m）。2026-11-15 实测：**127/127 通过，最大误差 0.00007 m**
（`cookie_02`），椅子 AABB 与上表逐位相同。Python 侧的
`test_coordinate_audit.py` 把这些数字固定为回归测试。

## 5. API

所有路径同源（相对页面 URL）。**所有 `/api` 读写都要 token**；静态资源（HTML/JS/CSS/
字体/favicon）和 `GET /healthz` 例外。token 可放 `X-Editor-Token` 头、`?token=` 查询，
模型 URL / SSE 用查询参数。

| 方法/路径 | 作用 |
|---|---|
| `GET /healthz` | 唯一无 token 探活（pid / service / uptime，不含关卡内容） |
| `GET /api/health` | pid / level / props / gate / version / uptime（需要 token） |
| `GET /api/meta` | 服务信息、关卡列表、本机所有可访问地址 |
| `GET /api/levels` | `Docs/Design/Levels` 下所有 `placement.json` 的 id |
| `GET /api/level` | **页面主数据**：`state = {doc, derived, manifests, check, counts, gate, file, history, levels, bounds, …}` |
| `GET /api/check` | `check` op + 报告表（分区/def/分档/三态） |
| `GET /api/hints?d=0.5` | 只读三态预测（§7） |
| `GET /api/models/<def>.glb` | 道具 GLB（`exports` 目录，只读；token 走查询参数） |
| `GET /api/events` | SSE：`event: change`, `id: <version>` |
| `POST /api/op` | `{"op": "...", "args": {...}}`（或 `{"ops":[...]}` 原子批量） |
| `POST /api/open` | `{"level": "<id>"}` |

写请求带 `X-Editor-Token: <token>`（也接受 JSON 里的 `token`）。响应：

```json
{"ok": true, "op": "move", "result": {...}, "error": "", "details": [],
 "state": {...}, "history": {"undo": 1, "redo": 0, "labels": [...], "redoLabels": []},
 "seconds": 0.11, "gate": {"ok": true, "errors": [], "warnings": [], "…": "…"},
 "saved": {"wrote": true, "bytes": 66771, "sha256": "…", "at": "…",
           "diff": {"counts": {"props": {"added":0,"updated":1,"removed":0},
                               "structures": {...}, "defs": {...}, "cameras": {...}},
                    "level": false, "valueUnits": 0}}}
```

- 被拒绝也是 **HTTP 200 + `ok:false`**（`details` 里是规则原文），和
  `levelctl.py` 的约定一致；缺/错 token 是 403，坏 JSON 是 400。
- 复用 op：`status/list/get/add_prop/add_structure/set/set_def/move/rotate/remove/seat/
  add_def/expected/camera/preset/check/export/undo`（undo 由 WebEditor 实现）/
  `batch`（原子）。
  - `set_def`（2026-11-16 新增）：改一个 `defs[]` 行的字段，含原作的逐件体积系数
    `volumeK`。合法区间 `0 < k <= 32`（`placement_core.volume_k_error`，与运行时
    `PickupAbsorbMath.MaxVolumeK` 同值）；`volumeK: null` = **删键**、回到缺省 0.70。
    写盘走 canonical writer，所以只有 `k != 0.70` 时文件里才出现该键——没声明 k 的关卡
    保持字节不变。`set` 也接受 `props[].volumeK`（单件覆盖，罕见）。
    改动会让该 def 的 `absorbDiameterReq`/`absorbable` 立刻按新 k 重算
    （`editor_core._commit` 先把文档过一遍 writer 再校验，所以不会留下 stale 行）。
- Web 端额外/不同：
  - `open`：换关卡（重新取锁、清历史）；打开当前关卡则原地 `reload`（不清锁）；
  - `reload`：从磁盘重读（人手改过文件后用）；
  - `undo` / `redo`：快照历史（支持 `steps`）；
  - `render`：**明确拒绝**（没有 Blender），截图在浏览器完成；
  - `shutdown`：拒绝（服务是前台托管任务，用 SIGTERM）。
- 写前冲突检测对 `export` 也生效（否则 export 会覆盖别人的改动）。

### 5.1 加/删与设计总量

`level.expected` 是硬门：`add_prop` / `remove` / 改 `valueUnits` 会改变总量，默认会被拒。
页面左下提供「加/删时同步更新 expected」（默认勾选），勾选后请求里带
`updateExpected: true`，和 CLI 的显式语义一致。取消勾选就退化成严格的设计总数闸。

## 6. 前端实现（Unity Scene 视图）

| 文件 | 职责 |
|---|---|
| `js/api.js` | token（`?token=` → localStorage）、带 token 的 GET/POST、SSE（查询参数） |
| `js/camera.js` | 统一 Scene 相机：yaw/pitch + pivot；轨道/平移/推拉/右键飞行 |
| `js/view.js` | three 场景、GLB/材质、结构、选择（OBB）、多选 gizmo、拖放落位、阶梯、审计 |
| `js/main.js` | 面板与编排：工具栏、道具库、校验、阶梯、键盘、批量 op |
| `css/app.css` | 三栏工作台 + 视口 HUD |
| `web/vendor/three/` | three r186 + TransformControls / GLTFLoader + LICENSE |

**相机（无“环绕/行走”二选一）**：

- 一个 `PerspectiveCamera` + `SceneCamera`（`yaw/pitch/pivot`）。左键不转相机；
  `Alt+左键` 绕 pivot 轨道，中键/`Space+左键` 沿屏幕平面平移，滚轮朝光标 dolly
  （速度按与目标距离缩放），`Alt+右键` 推拉。
- **按住右键 = Unity Flythrough**：鼠标转头，`W/S/A/D` 前后左右、`Q/E` 下降上升、
  `Shift ×3`、滚轮调速度（HUD 显示）；指针锁定优先，失败退化为按住右键读
  `movementX/Y`。松开右键结束；相机位置/速度保留，不影响选择。窗口失焦释放所有按键。
- 相机预设：`3/4`（适配地面 pad）、`俯视`（透视正上方；没做 Unity 的正交开关）、
  `球眼 0.6`（`playerSpawn` 眼高 0.6，看向球起点）、`人眼 1.6`。预设后可直接右键飞行。

**选择与工具（Q/W/E，R 禁用）**：

- 左键点选；`Shift/Ctrl+左键` 切换加选/减选；空白处左键拖拽 = 矩形框选
  （阈值 4 px；Shift/Ctrl 追加；拖动 gizmo 时不触发；只认画布，不认面板）。
  `Ctrl+A` 全选（除 ground）、`Esc` 取消；点空白清空。
- `Q` 手形（左键拖动平移，选择暂停）、`W` 移动、`E` 旋转、`R` 缩放禁用并提示；
  `G/R` 旧切换键移除；`X` 切 Local/Global（默认 Global）；`F` 聚焦，`End` 落到
  下方表面（原 `F`）；方向键按步长推；`Ctrl` 拖 gizmo = 按步长吸附。
- 多选：每件一个 **OBB**（`aabb`/`size` + `rotationQuat` + `position`，与 Unity
  BoxCollider 同形），外加一个淡色整体框；gizmo 在并集中心 pivot，拖动时整组
  绕 pivot 移动/旋转，**一次 `batch`、一次导出、一条撤销**。道具不缩放；结构尺寸在
  左侧数值面板改。
- 旋转/移动后如果声明了 `support`/`stackedOn`，客户端会把该件（以及坐在被旋转
  结构/道具上的物件）沿 Y 调到支撑面高度（保持新朝向），再作为同一批提交；因此
  “随便转两下”不会以“离支撑面 0.135 m”被拒。这个调整是 v1 的声明支撑语义，
  不是物理落地。
- 拖放：命中道具 → `stackedOn` 坐其顶；命中台面但和已有道具重叠 → 优先叠放，
  否则在支撑面内做小范围偏移；落点仍由服务端校验。`expected` 默认同步更新（可关）。
- 选中框：OBB 线框（黄色）+ 可选淡灰世界 AABB（顶部「显示轴对齐盒」，默认关）；
  「选中」面板列出 OBB 尺寸和世界轴对齐盒尺寸，并注明“v1 粗筛/互穿校验用，v2 OBB”。
  校验面板条目悬停时高亮提示里涉及的两件物体的 OBB（同色临时线框）。

**场景/内容**：道具 GLB 每个 def 只加载一次、实例 clone；材质颜色取 manifest 的
线性 RGB（与 Unity PropArt 同表），roughness 取 `materials_declared`。结构用盒子 +
`size` + `quat`；plane role 的可见体沿 authored −Y 平移半个厚度。球径阶梯/直方图/
物理提示/截图按钮同前。缩略图按可见道具懒加载。

### 6.1 性能

- 加载阶段暂停渲染（否则软件渲染会饿死 GLB 解析）；加载完成后只在「需要」时渲染：
  OrbitControls 的 `change`、gizmo 拖动、阶梯换色、选择、重拉、行走模式每帧。
  空闲时 CPU 接近 0，这对 2 核无 GPU 的本机很重要。
- 127 件道具 ≈ 127 个 group、每件 1–3 个 mesh；低模（每件几百三角形）。
- 阴影：1024² PCF，只有一个方向光；llvmpipe/SwiftShader 下也能出图。

## 7. 球径阶梯与设计反馈

项目的吸收门：`a·b·c ≤ (π/6)·D³ / 10`（体积门），等价于
`D_req = (60·a·b·c/π)^(1/3) = 2.673·(a·b·c)^(1/3)`
（`placement_core.VOLUME_GATE_FACTOR = 60`，`KatamariBallMotor` 的 `absorbVolumeRatio=10`）。
`D_req` 只由服务器算（`placement_core.absorb_diameter_req`），前端不复制公式。

- 绿：`D_req ≤ D`（现在能吸）
- 黄：`D < D_req ≤ 1.2·D`（再大一点就能吸）
- 红：`D_req > 1.2·D`
- 灰：结构 / 不可吸布景 / `D_req > ballDiameterMax`（本关 2.20）

面板同时给出：四档件数、可吸件的 `valueUnits` 合计、全关合计、D_req 直方图、
「再大一点」候选。这是设计者的主反馈工具：每个尺寸档需要足够多的小件
（见 [PROP_SET_PLAN](../StoryMode/PROP_SET_PLAN.md) 与
`Docs/Evidence/GrowthAudit/repricing_proposal.md` 的分析），滑条让「某一档只剩 3 件」一眼可见。

## 7.1 区域统计（2026-11-15 追加）

### 区域数据

工具栏「区域」进入区域模式：左键在地面拖出轴对齐盒（Y 默认地面起 2.5 m），
再用「区域」面板的 6 个 min/max 数字框或视口里的 6 个面把手精调；盒体显示为
半透明填充 + 线框。可保存多个命名区域（如「小区」「大区」），列表支持切换、改名、
删除、隐藏。区域**不进 placement.json**，写在旁文件：

```json
{
  "schemaVersion": 1,
  "regions": [
    {"name": "小区", "min": [-19.4, 0.0, -19.4], "max": [-13.0, 2.5, -13.0],
     "visible": true, "color": "#8fd4ff"}
  ]
}
```

路径 `Docs/Design/Levels/<id>/regions.json`（嵌套 id 如 `physics_test_field/C01`
则在其目录下）。坐标是 Unity 世界坐标（米，Y 向上）。服务端原子写（tmp + `os.replace`），
带 token；**独立于 undo**（撤销只回退 placement.json，不回退区域）。`schemaVersion 1`
允许未来升格为关卡数据；min/max 每轴必须 `min < max`，名字唯一。

### 统计口径

- **范围**：全关卡 / 选中的区域 / 当前选中的道具集合（三选一）。
- **包含判据**：「中心点在内」（默认，闭区间）或「包围盒与区域相交」
  （世界 AABB 与区域盒只要接触就算，所以刚好贴在区域面上也计入）。
- **口径**：个数 / **体积 m³** / valueUnits。体积 = `aabb` 三边之积
  `a*b*c`（AABB 体积，不是网格实体体积），与吸收门的体积口径同一个量；
  直方图每格百分比的分母是该统计范围内的**总体积**。
- 直方图横轴 D_req，默认 0.05 m 一格（可选对数纵轴）；悬停显示该格件数、体积、
  体积占比与最多 4 个条目 id；点击格子选中这些道具。
- 阶梯联动：D 滑条仍控制绿/黄/红/灰；tier 计数与体积来自服务端统计；新增
  **累计曲线**（从 `ballDiameterStart` 到当前 D 的可吸体积累计）和**区域对比表**
  （所有已保存区域的件数/体积/可吸体积比例/D_req 中位数）。
- 3D 视口把统计范围内的道具保持原样，范围外淡化（半透明灰）；区域盒始终可见。
- 导出：CSV（区域、口径、每格 low/high/count/volume/units/volume_pct）和视图 PNG。

### 接口

`GET /api/stats?scope=all|region|ids|box&region=<name>&ids=a,b&mode=center|bbox&metric=count|volume|units&bin=0.05&diameter=0.5&compare=1`
（token；`box=minx,miny,minz,maxx,maxy,maxz` 用于临时盒）。返回 `totals/tiers/bins/
cumulative/ballMm/dReqMedian/dReqMedianMm/absorbable*Ratio/includedIds`；`compare=1` 返回所有区域的并排摘要。
**判定量是原作的整 mm 门槛**（2026-11-16）：每件 `dReqMm = floor(1000·cbrt(60·k·a·b·c/π))`
（k 取 `props[]/defs[].volumeK`，缺省 0.70），`green`/`yellow`/`grey` 用
`dReqMm <= floor(1000·diameter)` 之类**整数**比较；`bins` 的边界也是整 mm。前端只显示
服务端给的 `dReqMm`/`dReqMedianMm`，不再自己乘除。
`GET/POST /api/regions`（token；POST body `{regions:[…]}`）读写旁文件，POST 校验后原子写。
**统计只在服务端算**：前端只渲染服务端返回的 bins/tiers/cumulative，CSV 也只是把同一份
结果格式化；没有第二套统计实现。

### 测试

- Python：区域包含（中心 vs 包围盒、刚好在面上）、空区域、两种口径合计/百分比、
  tier、累计、compare、regions 校验/原子写/重复名/token。
- 冒烟：区域模式下用鼠标画一个区域 → `/api/regions` 回读 → 读取 `/api/stats` →
  切换个数/体积截图；另加「大区」做并排对比。

## 8. 测试与验证

```bash
python3 -m unittest Docs.tests.leveleditor.test_web_server     # 29 tests
python3 -m unittest Docs.tests.leveleditor.test_coordinate_audit # 7 tests
python3 -m unittest discover -s Docs/tests                      # 全仓
python3 Docs/tools/validate_plan.py
```

- `test_web_server.py`：真实 socket + 真实临时关卡文件；健康检查、静态/模型、
  level 载荷、token 403、move 的规范字节与 diff、`expected` 门、batch 原子性、
  delete 的 expected、export→rebuild→export 同 sha、undo/redo 同字节、redo 清空、
  外部改写冲突、原子替换失败保留旧文件、同关第二实例被锁、token 文件 0600、
  SSE 流/版本递增、hints 与校验器分档表一致、换关卡、外部改写后 undo 也拒绝、
  读 API 的 token（静态/`/healthz` 除外）、多对象 batch 一次导出/一条撤销。
- `test_coordinate_audit.py`：椅子世界盒、花圃台上的苹果、地面过中心、30° 斜面
  行走面、边界向内、127 件/1500 u、C01 的 0 件/7 结构/5° 坡。
- `Tools/leveleditor/test/smoke.mjs`（可选，需 playwright-core；默认自起临时服务 + 关卡副本，
  跑完即删，不碰仓库文件）：真实鼠标/键盘测试左键点选、Shift 加选、框选、右键飞行+W+
  滚轮、中键/空格平移、Alt+左键轨道、F 聚焦、Q/W/E/R、多选 gizmo 一次批量/一条撤销、
  支撑面旋转、拖放叠放；真实 Chromium 加载
  127 件模型 → `audit()` → 点选 → 方向键 → Ctrl+Z 逐字节恢复 → D=0.50/1.25 计数 →
  拖苹果+撤销 → 行走/前后移动 → 截图下载 → 打开 C01 → 切回第一关 sha 不变。
- 截图证据：`Docs/Evidence/LevelEditorWeb/`（3/4 视角、行走球眼、选中 gizmo、
  D=0.50/D=1.25 阶梯、道具库、C01；图注见该目录的 `README.md`）。
- **2026-11-15 实测**：全仓 `python3 -m unittest discover -s Docs/tests` **743 tests OK**；
  `validate_plan.py` **PASS**；冒烟 **PASS**。在非 localhost 的
  `http://100.113.66.61:8777`（`isSecureContext=false`）上 headless Chromium
  **授予了 pointer lock**，WASD 移动已验证；Windows 实机 Chrome 仍待主人确认。

## 9. 安全

- 默认绑定 `0.0.0.0` 是为了 Windows PC 通过 Tailscale 访问；**不要**把端口映射到公网。
- **所有 `/api` 读写要 token**（静态资源与 `/healthz` 除外）；token 在
  `~/.local/state/vrc-katamari-leveleditor/token`（0600），
  首次生成后稳定，重启不变；页面从 `?token=` 存 localStorage 并清掉地址栏参数。
- 静态文件只从 `web/` 提供，路径解析后必须仍在 `web/` 内；模型只接受 manifest 里
  存在的 def。
- 不提供删除/格式化/上传接口；没有 `shutdown` 远程接口。

## 10. Phase 2（只设计，本轮不做）

1. **支撑图（rests_on）可视化 + infer supports**：用 `support`/`stackedOn` 画箭头/连线；
   「推断支撑」对每个道具向下投射到结构/道具顶面，生成候选关系（写回前仍走校验门）。
2. **道具行为**：`animated/path/orbit`（见 `Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md`，
   另一 agent 正在写）。前端需要一条时间轴预览，导出仍在 `props[]` 里加行为字段；
   校验器要新增「行为字段仅在 X 下合法」的规则——**设计前先等该文档定稿**。
3. **曲面坡道**：当前 `ramp` 是单块平面盒（`AddRamp` 也是）。曲面需要拆成多段 ramp 或
   新 role；Unity 侧 `AddRamp` 没有曲面槽位，先设计数据表示再谈。
4. **多选 / 框选**：框选 + 组变换；批量移动/旋转仍走 `batch`（原子）。
5. **区域 overlay**：`level.zones[].rect` 已经很轻（很多 zone 只有 id），补 `rect` 后
   画半透明分区 + 每区件数/单位，帮助「每个尺寸档需要多少小件」的布局。
6. **Unity round-trip**：导入器仍是单向；round-trip 需要 Unity 侧导出「场景里实际
   位置」并与 `placement.json` diff。这要改 Unity 导入器，不在本工具范围。
7. **多关卡标签页 / 双人协作**：当前一进程一关、单写者。要多人就得上锁/合并策略，
   属于另一个设计。

## 11. 已知缺口 / 需要主人拍板

| # | 事项 | 现状 | 需要谁 |
|---|---|---|---|
| 1 | **加/删时是否默认更新 `level.expected`** | **默认勾选**（本轮决定，可取消）；取消后就是严格设计总量闸 | 已按推荐默认落地 |
| 2 | 落到斜面不自动转正 | `End` 只按世界 AABB 底面贴支撑点；精确坐正用「按支撑坐正」=`seat`。旋转/移动后的支撑保持只调 Y，不改朝向 | 可 phase 2 改成按命中面法线 reseat |
| 3 | 指针锁定在非 localhost `http://` 可能被拒 | 已实现右键拖动回退；headless 在 Tailscale HTTP 上实测授予 | 主人在 Windows Chrome 里实际试一次 |
| 4 | 没有缩放 | 刻意：道具真实尺寸；结构尺寸用数值面板 | — |
| 5 | 物理提示只读 | 只显示 P2 预测，不写文件、不参与门 | 若要做「挡路件预算」实时提示，再接到 `_check_budget` |
| 6 | 只列已入库的 9 个 physics_test_field 格 | `Docs/Design/Levels/physics_test_field/<Cxx>/placement.json` | 其余 12 格是 `planned_cells[]`，没有几何 |
| 7 | 截图只有视口，不含 UI | 设计如此（证据图用浏览器整页截图另存） | — |
| 8 | ~~没有鉴权分级（读公开）~~ | 已按推荐默认：**所有 `/api` 读写都 token**，静态资源 + `/healthz` 例外 | 已落地 |
| 9 | 区域统计 | AABB 区域 + 服务端统计；包围盒口径是 v1 粗筛 | 球/圆柱区域、区域升格为关卡数据是以后的事 |

### 11.1 已按推荐默认落地（2026-11-15，不再询问）

1. **`level.expected` 同步**：加/删时默认勾选同步更新，可取消；取消即严格设计总量闸。
2. **未声明价值的 def**：暂不做 UI（道具库只列本关 `defs[]`/使用中的 def；C01 这类
   没有 `defs[]` 的关卡道具库为空并提示先 `add_def`）。
3. **飞行/眼高**：默认从 `playerSpawn` 0.6 m（球眼）/1.6 m（人眼）；右键随时飞行，
   不设独立“进入飞行”状态；相机位置/速度在编辑期间保留。
4. **只读 API**：在 tailnet 下也要求 token；页面/JS/CSS/字体与 `GET /healthz` 例外。
5. **区域持久化**：`regions.json` schemaVersion 1，独立于 placement.json 与 undo。

### 11.2 未授权参考关卡（`_ref_*`，2026-11-16 追加）

`Docs/Design/Levels/_ref_cancer` 与 `_ref_mas1` 是**只在本机查看**的参考关卡：位置/尺寸
来自社区数据挖掘，网格是从商业游戏提取的 PS2 资产，两个目录都被 `.gitignore` 忽略。
编辑器的规则：

- 只有 `level.id` 以 `_ref_` 开头的关卡进入参考模式；它自动打开 `placement.model.json`
  （带 `defs[].model` 与 `group`/`groupId`），并使用关卡自己的 `manifests/`。
- 网格由 `Tools/levelbuild/ref_models.py` 解析：`original_defs.json → modelFile` →
  `<ref-models-dir>/glb-pack/<file>.glb`；备选 `viewer_glb/<def>.glb`（Blender 转换缓存）。
  模型根默认 `~/KatamariRef/models`，**必须在仓库之外**（否则启动拒绝），CLI 为
  `--ref-models-dir`；`--placement` 可覆盖关卡文件名。普通关卡永远不进入这个解析路径，
  同 id 的仓库 GLB 也不会被参考关卡借用。
- 参考模式**只读**：所有改文档的 op、`export`、`undo`/`redo`/`batch` 一律拒绝；页面显示
  红黑条纹 `UNLICENSED REFERENCE` 横幅，道具库/结构/复制/删除/贴地/落面禁用。选中、
  区域统计（`regions.json` 仍可写，它是 git-ignored 的旁路数据）、球径阶梯、飞行/环绕
  相机不变。
- 参考 GLB 底部中心枢轴，viewer 在加载后按自身 bbox 居中 x/z、底面放到
  `position.y - aabb.y/2`；因此与 placement 的 AABB 中心语义一致。
- `group`/`groupId` 父子指针在顶部「父子连线 (N)」开关里画成细线（Cancer 83 条，
  MAS1 38 条）；选中面板显示这两个字段。
- **原作舞台**（2026-11-16 追加，详见 [REF_STAGE_VIEW.md](<REF_STAGE_VIEW.md>)）：参考模式
  还能加载提取器输出的地形网格、68 个碰撞 AABB / 3,456 个碰撞三角形 / 7 个 zone，并按
  `instances.json` 给每件道具套上原作旋转/缩放/高度（显示用，文件不动）。顶部开关
  「地形 / 线框 / 碰撞 / 旧平面 / 原作高度」；数据只在 `_ref_*` 关卡打开时经由
  `/api/ref-stage/{stage.glb,transforms.json,collision.json,contact}` 提供，同样要 token、
  带 `X-Unlicensed-Reference: 1`；提取目录默认 `~/KatamariRef/noclip_export/kse_out`
  （`--ref-stage-dir` / `$VRC_KATAMARI_REF_STAGE`），必须在仓库之外。
- 测试：`Docs/tests/leveleditor/test_ref_models.py`（解析、只读、文件不变、普通关卡
  不能通过 `/api/models` 取到参考网格、模型根必须在仓库外）与
  `Docs/tests/leveleditor/test_ref_stage.py`（帧检测、实例配对、GLB 转换、碰撞组索引、
  接触测量、`/api/ref-stage/*` 契约）。
