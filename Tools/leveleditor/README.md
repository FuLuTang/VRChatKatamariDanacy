# `Tools/leveleditor` — 浏览器里的关卡编辑器（VRC塊魂）

一个**本地 Web 关卡编辑器**：在浏览器里走图、从道具库拖入道具、用 gizmo 移动/旋转、按球径阶梯看「现在能吸什么」，每次编辑由 Python 后端校验并原子写回 `Docs/Design/Levels/<id>/placement.json`。

- **真源不变**：`placement.json` 仍是唯一真源；Unity 导入器仍是单向的（本工具不碰 Unity）。
- **校验/导出只有一份**：后端直接 `import Tools.levelbuild.editor_core` / `placement_core`，和 `levelbuild.py`、Blender 常驻编辑器、Unity 导入器读的是同一套规则与同一份规范字节。
- **不需要 Blender / Unity**：3D 视图是 three.js（已 vendored 到 `web/vendor/three`，离线可用）；道具模型直接用 `Docs/Art/OriginalModels/props/exports/<def>.glb`。

设计见 [Docs/Design/Tools/LEVEL_EDITOR_WEB.md](../../Docs/Design/Tools/LEVEL_EDITOR_WEB.md)。

---

## 1. 怎么启动（本机 Linux）

```bash
cd /home/fulutang/Documents/Github/VRChatKatamariDanacy
python3 Tools/leveleditor/server.py --level story_01_bench_size
```

默认：**绑定 `0.0.0.0`，端口 `8777`**。启动日志里会打印 token 和所有可访问 URL，例如：

```
vrc-katamari-leveleditor 1
level:   story_01_bench_size  (Docs/Design/Levels/story_01_bench_size/placement.json)
token:   <随机 token>
open (this machine): http://127.0.0.1:8777/?token=<token>
open (network):      http://100.113.66.61:8777/?token=<token>
stop:    Ctrl-C / SIGTERM (every edit is already exported)
```

**本机浏览器**：`http://127.0.0.1:8777/?token=<token>`
**Windows PC（同一 tailnet）**：`http://100.113.66.61:8777/?token=<token>`

- 本机（freeEgg）的 Tailscale 地址是 **`100.113.66.61`**；Windows 台式机
  `desktop-114514aa` 是 `100.112.46.12`。**Windows 浏览器访问的是前者**，不是自己的地址。
- 只监听 tailnet/局域网，**不要**做公网端口映射。本机没有启用 `ufw`；
  打不开时依次查：`tailscale ip -4`、`ss -ltn | grep 8777`、Windows 上 `tailscale status`。
- token 只保护**写操作**；页面、JS、模型和只读 API 不需要 token。
- token 首次生成后保存在 `~/.local/state/vrc-katamari-leveleditor/token`（`0600`），**重启服务后不变**，所以收藏的带 token 的链接一直有效；要换就删掉这个文件（或传 `--token`）。
- 页面首次带 `?token=` 打开后会把它存进 `localStorage` 并从地址栏去掉；写入被 403 时页面会提示用最新链接重新打开。

### 作为后台任务跑（给 Lead / 自动化）

服务是**前台进程**（不 daemonize），把自己交给任务管理器即可：

```bash
cd /home/fulutang/Documents/Github/VRChatKatamariDanacy
python3 Tools/leveleditor/server.py --level story_01_bench_size --port 8777
```

- 每次编辑成功都已写盘，`SIGTERM` / Ctrl-C 不会丢东西；重启后从 `placement.json` 恢复。
- **undo/redo 历史是内存态**：重启后清空（文件本身已是最后一次成功编辑的状态）；
  token 和文件锁不受重启影响。
- 端口被占时用 `--port 0` 让系统分配，或换一个端口；日志会打印实际端口。
- 同一份关卡只允许一个 Web 编辑器写（`~/.local/state/.../locks/` 下的 `flock`）。**不要**同时用 `levelserve.py`（Blender 常驻编辑器）改同一份文件：Web 端每次写前会比较磁盘上的文档，发现被别人改过就拒绝并提示「按 Reload」。
- `--no-lock` 只用于测试（两个实例同时开同一关）。

---

## 2. 怎么用（5 分钟）

| 想做的事 | 操作 |
|---|---|
| 转视角 | 左键拖 / 滚轮；顶部「适配 3/4 / 球眼 / 3/4 / 俯视」是相机预设 |
| 走图 | 点「行走」或 `Tab`：WASD + Shift 跑；默认从球眼相机位置开始，眼高 0.6 m；`Tab` 回环绕 |
| 选中 | 场景里左键点道具/结构；右侧显示 id/def/尺寸/D_req/valueUnits |
| 移动 / 旋转 | 选中后拖 gizmo；`G`=移动、`R`=旋转；步长在「吸附」里选（0.05/0.10 m，15/45/90°） |
| 微调 | 方向键推一步，Shift+方向键 4 倍；`F` 落到下方支撑面；「贴地」按地面落 |
| 删除 / 复制 | `Delete` / `Ctrl+D` |
| 撤销 / 重做 | `Ctrl+Z` / `Ctrl+Y`（顶部按钮同） |
| 放道具 | 左侧「道具库」搜索，拖进场景；落点会贴到射线命中的台面/道具上（`add_prop`） |
| 加结构 | 左下「加结构」：box / ramp / boundary，填 center/size/yaw |
| 校验 | 右侧「校验」；点错误条目会定位到提示里的对象 id |
| 球径阶梯 | 右侧滑条 0.30–2.20 m；勾选「按可吸性着色」；直方图是 D_req 分布 |
| 截图 | 顶部「截图」下载 PNG |
| 换关卡 | 顶部下拉 + 「打开」（`story_01_bench_size`、`physics_test_field/C01` 等） |

**道具不缩放**：gizmo 只有移动/旋转；道具尺寸来自 manifest（真实尺寸）。

**设计总量 `level.expected`**：左下「加/删时同步更新 expected」默认勾选。取消勾选后，加/删会按设计门拒绝（这是刻意的闸）；在 `batch` 里显式跑 `expected` 也可以。

---

## 3. 键盘

| 键 | 作用 |
|---|---|
| `Tab` | 环绕 ⇄ 行走 |
| `WASD` / `Shift` | 行走移动 / 跑；`Esc` 只释放鼠标，不退出行走 |
| 行走下按住右键拖动 | 指针锁定被浏览器拒绝时的看方向方式 |
| `G` / `R` | 移动 / 旋转 gizmo |
| `F` | 落到下方支撑面（射线） |
| `Delete` | 删除选中 |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 |
| `Ctrl+D` | 复制选中 |
| 方向键 / Shift+方向键 | 按移动步长推 / 4 倍 |
| `Esc`（环绕） | 取消选中 |

---

## 4. 依赖与离线

- Python 3.10+，**只用标准库**（`http.server` + `json` + `socketserver`），没有 aiohttp/Flask。
- three.js r186（`three.module.js`, `three.core.js`, OrbitControls, TransformControls, GLTFLoader, BufferGeometryUtils, SkeletonUtils）已 vendored 到 `web/vendor/three/`，附 `LICENSE`（MIT）。浏览器需要支持 import maps（Chrome 89+ / Edge / Firefox 108+ / Safari 16.4+）。
- 模型全部来自 `Docs/Art/OriginalModels/props/exports/*.glb`（44 个关卡 def 全都有 GLB）。**不需要 Blender**；如需给只有 FBX 的 def 补 GLB，用 `source ~/tools/blender-env.sh` 的 headless Blender 导出到该目录（属性同现有 manifest），本工具只读。
- 没有 `node_modules`、没有构建步骤：`index.html` 直接加载 ES modules。

---

## 5. 测试

```bash
# 后端契约（真实 socket、真实文件、fake scene，不依赖 Blender/浏览器）
python3 -m unittest Docs.tests.leveleditor.test_web_server
python3 -m unittest Docs.tests.leveleditor.test_coordinate_audit

# 可选：浏览器冒烟（需要 playwright-core + 一份 Chromium）
npm install playwright-core@1.50.1    # 在仓库外
LEVELEDITOR_PLAYWRIGHT=/path/to/node_modules/playwright-core \
node Tools/leveleditor/test/smoke.mjs
```

冒烟脚本默认**自己起一个临时服务**（`--port 0`、`--no-lock`、临时关卡副本），
结束后删掉副本，所以不会碰到仓库里的 `placement.json`；要对着已经开着的服务跑，
再设 `LEVELEDITOR_URL` + `LEVELEDITOR_TOKEN`。

冒烟脚本会：加载 127 件真实模型 → 跑坐标审计 → 点选椅子 → 方向键推 0.05 m → `Ctrl+Z` 恢复**逐字节相同** → 在 D=0.50/1.25 检查阶梯计数 → 拖一个苹果进场景再撤销 → 行走模式前后走 → 截图按钮下载 PNG → 打开 `physics_test_field/C01` 再切回第一关（并校验文件 sha 未变）。

截图默认写到 `/tmp/leveleditor-shots/`，可用 `LEVELEDITOR_SHOTS` 指定。本次交付的图在 `Docs/Evidence/LevelEditorWeb/`。

---

## 6. 已知限制（phase 1）

- 只编辑一份关卡；同一时间一个写者（设计如此）。
- 没有多选/框选；没有缩放（道具不应缩放）。
- 「落到面上」按世界 AABB 的底面贴到射线命中点，不会把道具**转到斜面法线**；要按声明支撑精确坐正用「按支撑坐正」（`seat` op，和 CLI 同一实现）。
- 校验器以服务端为准；前端只做即时颜色/计数（不算校验）。
- 物理提示是只读预测（P2 规则），不写文件，也不是运行期碰撞。
- `render` op 在 Web 端被拒绝（没有 Blender）；截图由浏览器完成。
- 指针锁定在部分浏览器的非 localhost `http://` 下可能被拒绝；页面自动退化为「按住右键拖动看方向 + WASD」，功能不受影响。
  实测：headless Chromium 在 `http://100.113.66.61:8777`（非安全上下文）上**授予了** pointer lock，
  WASD 正常；Windows 实机 Chrome 还需主人按一次确认。
- `physics_test_field` 目前只列已入库的 9 格（`C01` 等），不是 21 格。

---

## 7. 文件

```
Tools/leveleditor/
├── server.py                 # HTTP 服务 + WebEditor（复用 editor_core）
├── README.md                 # 本文件
├── web/
│   ├── index.html            # 布局 + import map
│   ├── favicon.svg
│   ├── css/app.css
│   ├── js/api.js             # token / op 调用 / SSE
│   ├── js/view.js            # three.js 场景、选择、gizmo、行走、阶梯、审计
│   ├── js/main.js            # UI 面板与操作编排
│   └── vendor/three/         # three r186 + addons + LICENSE（MIT）
└── test/smoke.mjs            # 可选浏览器冒烟
Docs/tests/leveleditor/
├── test_web_server.py        # API/权限/undo/原子写/锁/冲突/SSE
└── test_coordinate_audit.py  # 坐标审计（椅子/花圃/斜面/边界）
```
