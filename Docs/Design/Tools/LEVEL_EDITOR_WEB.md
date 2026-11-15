# 浏览器关卡编辑器（Web Level Editor）

> 状态：**phase 1 可用**，2026-11-15。实现：`Tools/leveleditor/`。真源仍是
> `Docs/Design/Levels/<id>/placement.json`；Unity 导入器仍只做单向导入。
> 相关的数据/规则文档：[LEVEL_AUTHORING](LEVEL_AUTHORING.md)、
> [`Tools/levelbuild/README.md`](../../../Tools/levelbuild/README.md)。

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

- 一份关卡一个 `flock`（`~/.local/state/vrc-katamari-leveleditor/locks/<sha>.lock`），
  第二个 Web 编辑器启动时直接拒绝。
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

所有路径同源（相对页面 URL）。读不需要 token；写需要。

| 方法/路径 | 作用 |
|---|---|
| `GET /api/health` | pid / level / props / gate / version / uptime（不需要 token） |
| `GET /api/meta` | 服务信息、关卡列表、本机所有可访问地址 |
| `GET /api/levels` | `Docs/Design/Levels` 下所有 `placement.json` 的 id |
| `GET /api/level` | **页面主数据**：`state = {doc, derived, manifests, check, counts, gate, file, history, levels, bounds, …}` |
| `GET /api/check` | `check` op + 报告表（分区/def/分档/三态） |
| `GET /api/hints?d=0.5` | 只读三态预测（§7） |
| `GET /api/models/<def>.glb` | 道具 GLB（`exports` 目录，只读） |
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
  `levelctl.py` 的约定一致；token 错误是 403，坏 JSON 是 400。
- 复用 op：`status/list/get/add_prop/add_structure/set/move/rotate/remove/seat/
  add_def/expected/camera/preset/check/export/undo`（undo 由 WebEditor 实现）/
  `batch`（原子）。
- Web 端额外/不同：
  - `open`：换关卡（重新取锁、清历史）；
  - `reload`：从磁盘重读（人手改过文件后用）；
  - `undo` / `redo`：快照历史（支持 `steps`）；
  - `render`：**明确拒绝**（没有 Blender），截图在浏览器完成；
  - `shutdown`：拒绝（服务是前台托管任务，用 SIGTERM）。
- 写前冲突检测对 `export` 也生效（否则 export 会覆盖别人的改动）。

### 5.1 加/删与设计总量

`level.expected` 是硬门：`add_prop` / `remove` / 改 `valueUnits` 会改变总量，默认会被拒。
页面左下提供「加/删时同步更新 expected」（默认勾选），勾选后请求里带
`updateExpected: true`，和 CLI 的显式语义一致。取消勾选就退化成严格的设计总数闸。

## 6. 前端实现

| 文件 | 职责 |
|---|---|
| `js/api.js` | token（`?token=` → localStorage）、op 调用、错误归一化、SSE |
| `js/view.js` | three 场景、GLB 加载、材质、结构、相机、选择、gizmo、行走、吸附/落地、阶梯颜色、截图、审计 |
| `js/main.js` | 面板与编排：关卡、道具库、结构表单、选中信息、校验、阶梯/直方图、物理提示、键盘、拖放 |
| `css/app.css` | 三栏深色工作台 |
| `web/vendor/three/` | three r186 + OrbitControls / TransformControls / GLTFLoader / BufferGeometryUtils / SkeletonUtils + LICENSE |

要点：

1. **道具模型**：每个 def 只 `GLTFLoader.load` 一次，实例 `clone(true)`；材质颜色取
   `manifest.material_base_colors`（线性 RGB，与 Unity 的 PropArt 同表），roughness 取
   `materials_declared[].roughness`；有贴图就保留 GLB 原材质。没有 GLB 的 def 画品红盒子并提示。
2. **结构**：`BoxGeometry(1,1,1)` + scale = `size`；plane role 的可见体按 §4 平移
   `-size.y/2`；边界加向内箭头；地面加 `GridHelper` 和虚线 bounds。
3. **选择**：射线拾取 prop/structure group（跳过 ground），`BoxHelper` 黄框 + 右侧信息。
4. **gizmo**：`TransformControls`（r186 的 `getHelper()` 加进场景），只 translate/rotate；
   拖动时实时改 group，`dragging-changed=false` 时把**位置 + 完整四元数**打包成
   `{"ops":[move,set]}` 一次原子提交；失败回滚到拖动前。`translationSnap` /
   `rotationSnap` 由「吸附」控制。
5. **落地**：从选中对象世界 AABB 的底面中心向下打射线（排除自己），命中的最高面作为
   支撑；`y = hit.y + 世界半高`。斜面不自动转正（见 §10 已知缺口），要精确坐正用
   「按支撑坐正」→ `seat` op（和 CLI 同一实现）。
6. **行走**：`Tab` 进入；若环绕相机不在关卡上空，从关卡自己的 `ball_eye` 相机位置
   起步（否则从当前位置），眼高默认 0.6 m；WASD + Shift；固定高度、无碰撞。
   指针锁定优先；`pointerlockerror` 时退化为**按住右键拖动看方向**（页面明确提示）。
7. **阶梯**：滑条 D∈[0.30, 2.20]；每个 prop 的 `D_req` 来自服务器的
   `derived.props[itemId].dReq`（`placement_core.absorb_diameter_req`），前端只做
   `D_req <= D` / `<= 1.2·D` / 否则 三档比较，灰 = 结构或 `D_req > ballDiameterMax`。
   计数、可吸 valueUnits 总和、D_req 直方图（0.05 m 一格）都是这个比较的派生显示。
   不勾「按可吸性着色」时显示 manifest 原色；一拖滑条自动勾上。
8. **道具库**：48 个 manifest 全部列出（含 4 个当前关未声明的 def，标灰并提示先
   `add_def`）；缩略图用独立的 112×112 WebGL renderer 从同一个 GLB 渲染一次，
   结果只存内存（每次页面加载重画，避免缓存过期）。HTML5 拖放 → 射线落点 →
   `add_prop`；命中 box/ramp 时带 `support`，命中道具时带 `stackedOn`，并 `seat: true`。
   **命中 ground / boundary 不设 support**（地面是地面，墙不是台面）。
9. **截图**：`renderer.render` 后 canvas `toDataURL`，下载 `<level>_<camera>_<time>.png`；
   同时 `window.__lastScreenshot` 便于自动化。
10. **校验面板**：直接用 `/api/level` 的 `check.errors/warnings/notes`（服务器跑
    `validate`）；点击条目在文本里找最长匹配的 id 并 `select(id, focus)`。
11. **物理提示**：`/api/hints?d=` 由服务器用 §10.3 的规则算（食物/地形/挡路、
    可上/挡路结构），页面只显示。只读，不参与校验。
12. **状态推送**：SSE 版本变化 → 去抖 160 ms 后重拉 `/api/level`；拖动 gizmo 期间
    不重拉；自己的保存也会触发一次重拉（幂等）。

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
`/tmp/growth_audit/repricing_proposal.md` 的分析），滑条让「某一档只剩 3 件」一眼可见。

## 8. 测试与验证

```bash
python3 -m unittest Docs.tests.leveleditor.test_web_server     # 22 tests
python3 -m unittest Docs.tests.leveleditor.test_coordinate_audit # 7 tests
python3 -m unittest discover -s Docs/tests                      # 全仓
python3 Docs/tools/validate_plan.py
```

- `test_web_server.py`：真实 socket + 真实临时关卡文件；健康检查、静态/模型、
  level 载荷、token 403、move 的规范字节与 diff、`expected` 门、batch 原子性、
  delete 的 expected、export→rebuild→export 同 sha、undo/redo 同字节、redo 清空、
  外部改写冲突、原子替换失败保留旧文件、同关第二实例被锁、token 文件 0600、
  SSE 流/版本递增、hints 与校验器分档表一致、换关卡、外部改写后 undo 也拒绝。
- `test_coordinate_audit.py`：椅子世界盒、花圃台上的苹果、地面过中心、30° 斜面
  行走面、边界向内、127 件/1500 u、C01 的 0 件/7 结构/5° 坡。
- `Tools/leveleditor/test/smoke.mjs`（可选，需 playwright-core；默认自起临时服务 + 关卡副本，
  跑完即删，不碰仓库文件）：真实 Chromium 加载
  127 件模型 → `audit()` → 点选 → 方向键 → Ctrl+Z 逐字节恢复 → D=0.50/1.25 计数 →
  拖苹果+撤销 → 行走/前后移动 → 截图下载 → 打开 C01 → 切回第一关 sha 不变。
- 截图证据：`Docs/Evidence/LevelEditorWeb/`（3/4 视角、行走球眼、选中 gizmo、
  D=0.50/D=1.25 阶梯、道具库、C01；图注见该目录的 `README.md`）。
- **2026-11-15 实测**：全仓 `python3 -m unittest discover -s Docs/tests` **634 tests OK**；
  `validate_plan.py` **PASS**；冒烟 **PASS**。在非 localhost 的
  `http://100.113.66.61:8777`（`isSecureContext=false`）上 headless Chromium
  **授予了 pointer lock**，WASD 移动已验证；Windows 实机 Chrome 仍待主人确认。

## 9. 安全

- 默认绑定 `0.0.0.0` 是为了 Windows PC 通过 Tailscale 访问；**不要**把端口映射到公网。
- 写操作要 token；token 在 `~/.local/state/vrc-katamari-leveleditor/token`（0600），
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
| 1 | **加/删时是否默认更新 `level.expected`** | 页面默认勾选（编辑器的自然行为），可取消；取消后就是严格设计总量闸 | 主人：编辑阶段是否希望「加一件就自动改设计总量」 |
| 2 | 落到斜面不自动转正 | `F` 只按世界 AABB 底面贴支撑点；精确坐正用「按支撑坐正」=`seat` | 可 phase 2 改成按命中面法线 reseat |
| 3 | 指针锁定在非 localhost `http://` 可能被拒 | 已实现右键拖动回退；headless 实测见 §8/README | 主人在 Windows Chrome 里实际试一次 |
| 4 | 没有多选、框选、缩放 | 刻意：道具不缩放；多选 phase 2 | — |
| 5 | 物理提示只读 | 只显示 P2 预测，不写文件、不参与门 | 若要做「挡路件预算」实时提示，再接到 `_check_budget` |
| 6 | 只列已入库的 9 个 physics_test_field 格 | `Docs/Design/Levels/physics_test_field/<Cxx>/placement.json` | 其余 12 格是 `planned_cells[]`，没有几何 |
| 7 | 截图只有视口，不含 UI | 设计如此（证据图用浏览器整页截图另存） | — |
| 8 | 没有鉴权分级（读公开） | tailnet 内可读；写要 token | 若 tailnet 不可信，给 GET 也加 token |
