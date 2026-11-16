# `Tools/leveleditor` — 浏览器里的关卡编辑器（VRC塊魂）

一个**本地 Web 关卡编辑器**：交互按 Unity Scene 视图的习惯（左键点选/Shift 加选/空白框选、右键飞行、中键平移、Alt+左键轨道、Q/W/E 工具），从道具库拖入道具，用 gizmo 移动/旋转（支持多选与整体 pivot），按球径阶梯看「现在能吸什么」；每次编辑由 Python 后端校验并原子写回 `Docs/Design/Levels/<id>/placement.json`。

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
- token 保护**所有 `/api` 读写**（页面/JS/CSS/字体/`favicon` 等静态资源除外）；
  唯一不需要 token 的探活端点是 `GET /healthz`（只回 pid/uptime，不含关卡内容）。
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
- **重新打开当前关卡 = 原地重载**（不会二次抢自己的锁）；**切换关卡**先拿到新锁再放旧锁，失败时旧关卡/旧锁/历史原样保留。锁文件名是 `v2-<sha256>.lock`，内容是 `{pid, port, level, placement}`；被别的进程占用时错误会写明 `pid`、端口和关卡，例如
  `another level editor already holds v2-….lock (pid 1234, port 8777, level story_01_bench_size); one level, one writer`。
  进程已死的陈旧锁会被自动接管并在 stderr 打一行 `took over … (stale lock)`；旧命名方案（sha1/16）的锁同时作为兼容锁持有，尚未重启的旧版本进程不会和新版本同时写同一关。
- 探活：`curl -s http://127.0.0.1:8777/healthz`（无 token）；`/api/health` 需要 token。
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

## 3. 操作（Unity Scene 视图）

| 操作 | 键/鼠标 |
|---|---|
| 点选 / 加选 / 减选 | 左键；Shift/Ctrl+左键切换单件 |
| 框选 | 空白处左键拖拽（矩形，Shift/Ctrl 追加）；阈值 4 px |
| 全选 / 取消 | `Ctrl+A` / `Esc` |
| 移动 / 旋转 | `W` / `E`；gizmo 拖动；多选时整组绕 pivot |
| 手形（平移） | `Q`，左键拖动（此时选择暂停） |
| 缩放工具 | `R` 禁用（显示提示；道具是真实尺寸） |
| 坐标 Local/Global | `X`（默认 Global） |
| 吸附 | 按住 `Ctrl` 拖 gizmo = 按步长吸附（下拉里的 0.05/0.10 m、15/45/90°） |
| 聚焦 | `F`（选中则聚焦；无选中适配整关） |
| 落到下方表面 | `End`（原 F 落面） |
| 微调 | 方向键（Shift ×4） |
| 复制 / 删除 | `Ctrl+D` / `Delete` |
| 撤销 / 重做 | `Ctrl+Z` / `Ctrl+Y` 或 `Ctrl+Shift+Z` |
| 相机预设 | 顶部 `3/4`、`俯视`、`球眼 0.6`、`人眼 1.6` |
| 飞行 | **按住右键**：鼠标转头，`W/S` 前后、`A/D` 左右、`Q/E` 下降/上升、`Shift ×3`；滚轮调飞行速度（HUD 显示） |
| 平移 | 中键拖动；没有中键时 `Space+左键` 拖动；`Alt+中键` 同中键 |
| 轨道旋转 | `Alt+左键` 拖动，绕选中物（无选中绕 pivot） |
| 推拉 | 滚轮朝光标 dolly；`Alt+右键` 拖动缩放推拉 |
| 显示轴对齐盒 | 顶部「显示轴对齐盒」复选框（默认关） |

- **右键飞行**期间指针锁定；浏览器拒绝时自动退化为“按住右键拖动读 movementX”，WASD/QE 照常；飞行只动相机，不改选择。
- 选中框是 **OBB**（`aabb`/`size` + `rotationQuat` + `position`，Unity BoxCollider 的形状），不是世界 AABB；世界 AABB 默认隐藏，可勾选显示为淡灰细线。多选时每件一个 OBB + 一个淡色整体框。
- **旋转**会把被旋转的道具（以及坐在被旋转结构/道具上的物件）沿 Y 调整到声明的支撑面上，保持新朝向，因此一次旋转不会因为“离支撑面 0.135 m”被拒；批量仍是一次导出/一条撤销。
- **拖放**会先做一次客户端落位：命中道具就 `stackedOn` 坐在它顶上；命中台面但和已有道具重叠时，优先叠放，否则在支撑面内做小范围偏移；最终仍由服务端校验/拒绝。

### 3.1 区域统计

- 顶部「区域」按钮 = 区域模式：左键在地面拖出轴对齐盒（Y 默认地面到 +2.5 m）；
  右侧「区域」面板的 6 个 min/max 数字框或视口里的 6 个面把手可精调；可保存多个
  命名区域（「小区」「大区」…），列表支持切换、改名、删除、隐藏。
- 区域写 `Docs/Design/Levels/<id>/regions.json`（`schemaVersion: 1`），**不写
  placement.json、不进 undo**；服务端原子写，带 token。
- 阶梯面板的「统计范围」：全关卡 / 选中的区域 / 当前选中的道具；「区域包含」：
  中心点在内（默认）/ 包围盒相交；「口径」：个数 / 体积 m³ / valueUnits。
  直方图悬停看每格件数/体积/占比/条目，点击格子选中这些道具；对数纵轴可选。
- tier 计数与体积、累计可吸曲线、所有区域并排对比表都来自同一个
  `GET /api/stats`；前端不重算。CSV 导出与「统计截图 PNG」在面板底部。
- 体积口径 = `aabb` 三边之积（m³）；百分比分母是该统计范围内的总体积。

## 4. 依赖与离线

- Python 3.10+，**只用标准库**（`http.server` + `json` + `socketserver`），没有 aiohttp/Flask。
- three.js r186（`three.module.js`, `three.core.js`, OrbitControls, TransformControls, GLTFLoader, BufferGeometryUtils, SkeletonUtils）已 vendored 到 `web/vendor/three/`，附 `LICENSE`（MIT）。浏览器需要支持 import maps（Chrome 89+ / Edge / Firefox 108+ / Safari 16.4+）。
- 模型全部来自 `Docs/Art/OriginalModels/props/exports/*.glb`（44 个关卡 def 全都有 GLB）。**不需要 Blender**；如需给只有 FBX 的 def 补 GLB，用 `source ~/tools/blender-env.sh` 的 headless Blender 导出到该目录（属性同现有 manifest），本工具只读。
- 没有 `node_modules`、没有构建步骤：`index.html` 直接加载 ES modules。

### 4.1 UNLICENSED 参考关卡（`_ref_*`）

`Docs/Design/Levels/_ref_cancer`、`_ref_mas1` 是**未授权参考数据**：位置/尺寸来自社区数据挖掘，
网格是从商业游戏提取的 PS2 资产。这两个目录被 `.gitignore` 忽略，里面的任何东西都不能
进 git、上传或复制到 `Docs/Art` / 托管道具里；网格只允许在本机编辑器里查看。

```bash
# 模型根默认 ~/KatamariRef/models（或 $VRC_KATAMARI_REF_MODELS）
python3 Tools/leveleditor/server.py --level _ref_cancer --port 8790
# 也可显式指定；--ref-models-dir 必须在仓库之外，否则启动拒绝
python3 Tools/leveleditor/server.py --level _ref_mas1 --ref-models-dir ~/KatamariRef/models
```

- **自动打开 `placement.model.json`**（带 def 的 `model` 和原作的 `group`/`groupId` 父子指针）；
  `--placement` 可强制指定文件名，普通关卡仍默认 `placement.json`。
- 网格解析：关卡里的 `original_defs.json → defs[].modelFile` 指向
  `<models-dir>/glb-pack/<file>.glb`（three r186 直接加载；本仓库的 145/105 个 def 全部命中）。
  备选是 `<models-dir>/viewer_glb/<def>.glb`（Blender 转换缓存）和
  `converted_local/<def>/<def>.fbx` / `converted/<modelFile>.fbx`（只报告可转换，不冒充 GLB）。
  这个映射由 `Tools/levelbuild/ref_models.py` 唯一实现；**不会**回退到
  `Docs/Art/OriginalModels/props/exports`，所以同 id 的仓库模型（例如 `acorn`）不会串进来。
- **只读**：`add_prop`/`set`/`move`/`remove`/`export`/`undo`/`redo`/`batch` 全部拒绝，文件
  一个字节都不写；页面顶部有红黑条纹 **UNLICENSED REFERENCE** 横幅，道具库/结构/删除/复制禁用。
  选中、区域统计、球径阶梯、飞行/环绕相机照常可用。
- **父子指针**：顶部「父子连线 (N)」开关把每个 `group`/`groupId` 子道具连到父道具的中心
  （Cancer 83 条，MAS1 38 条）；选中面板显示 `group`/`groupId`。
- **网格朝向/枢轴**：参考 GLB 是底部中心枢轴；viewer 在加载后按自身包围盒把 x/z 居中、
  底面放到 `position.y - aabb.y/2`，因此与 placement 的 AABB 中心语义一致（实测 511 件的
  世界包围盒三轴与数据 `aabb` 的比值 0.998–1.002，底面误差 0）。
- 模型响应带 `X-Unlicensed-Reference: 1` 且 `Cache-Control: no-store`；普通关卡打开时
  `/api/models/<ref-def>.glb` 一律 404，参考网格不会被别的关卡借走。


---

## 4.2 Agent cookbook: looking at a level（给 LLM/脚本）

`shot.py` 是**给 agent 的看图入口**：一条命令、一行 JSON、若干 PNG；不需要开浏览器，
也不用摆相机。第一次调用会自己起一个常驻渲染 daemon（headless Chromium + 私有
`server.py`，默认控制端口 `8793`），之后复用同一个页面，所以重复看图是秒级。

```bash
cd /home/fulutang/Documents/Github/VRChatKatamariDanacy

# 0) 状态 / 停止（daemon 空闲 900 s 也会自己退出）
python3 Tools/leveleditor/shot.py --status
python3 Tools/leveleditor/shot.py --stop
```

### 配方 1：一张 2×2 总览（默认）
```bash
python3 Tools/leveleditor/shot.py story_01_bench_size
# 默认 sheet = top / iso / start / focus:auto（最高 valueUnits 的地标，平手按体积）
# 1280x720 一格，拼成 2560x1440；JSON 里有 camera / levelHash / propCount /
# visibleBox / layers / timings / consoleErrors。
```

### 配方 2：起点沟壑近景（看玩家第一眼看到什么）
```bash
python3 Tools/leveleditor/shot.py story_01_bench_size \
  --view start --layers props,structures,grid --w 1280 --h 720
# start = 球出生点、眼高 = 8×球半径（0.30 m 球 → 1.2 m），沿关卡 ball_eye 相机方向，
# FOV 60°；JSON 的 camera.position/target 是世界坐标，可直接复用到别处。
```

### 配方 3：按 0.222 m 球径着色（看能吸什么）
```bash
python3 Tools/leveleditor/shot.py story_01_bench_size \
  --view top --layers props,structures,grid,tier:0.222
# 绿=当前球径可吸，黄=1.2×球径内，红=更大，灰=超过 ballDiameterMax；
# 与 GUI 滑条同一套 `_tierFor` 规则（Req mm 与 floor(1000·D) 比较）。
```

### 配方 4：某个道具/def 到底在哪
```bash
python3 Tools/leveleditor/shot.py story_01_bench_size \
  --view focus:cup --layers props,structures,labels:10
# focus:<itemId|defId> 用该道具（def 则取所有同 def 的并集）的世界 AABB 取景，
# 黄色框标出目标，labels 标出离视野中心最近的 N 个 itemId。
```

### 配方 5：比较两帧 / 动画检查
```bash
python3 Tools/leveleditor/shot.py story_01_bench_size \
  --view start --frames 4 --interval 0.5
# 输出 _f01.._f04.png + _diff.png（红=首末帧变化）；同一相机、无随机抖动。
# 只要单帧时用 --no-diff 可省掉热图。
```

### 配方 6：参考关卡（UNLICENSED，只读）
```bash
python3 Tools/leveleditor/shot.py _ref_cancer \
  --view iso --layers props,structures,terrain,collision,parents,labels:8
# 输出只允许在 ~/KatamariRef/viewer_evidence/shots/ 下（或 $VRC_KATAMARI_REF_SHOTS），
# 任何仓库内 --out 都会被拒绝；图上有 UNLICENSED REFERENCE 水印和 PNG tEXt 标记。
# terrain/collision/parents 只有 _ref_* 关卡有；普通关卡会显示在 unavailable 里。
```

### 同一实现的 HTTP 入口
编辑器服务器（`server.py`）的 `GET /api/render` 和 CLI 走**同一个** daemon 与相机规划：

```bash
TOKEN=$(cat ~/.local/state/vrc-katamari-leveleditor/token)
curl -s -H "X-Editor-Token: $TOKEN" \
  'http://127.0.0.1:8777/api/render?view=top&layers=props,grid&w=1280&h=720'
```

参数与 CLI 同名：`view`（可重复）/`views`、`frames`、`interval`、`layers`、`w`、`h`、
`max-px`、`out`、`save-bookmark`、`timeout`。返回同样的 JSON；失败时 `ok:false`、
`error`、`code`，CLI 退出码 1。

### 选项速查

```bash
--view top|iso|side|start|focus:<id|def>|focus:auto|region:<name>|bookmark:<name>
--view 'cam=x,y,z>tx,ty,tz[,fov]'      # 世界坐标（Unity 米），默认 FOV 60
--frames N --interval S [--no-diff]     # 同一相机多帧；只允许单个 view
--layers props,structures,terrain,collision,parents,grid,wire,
         labels:N,tier:<D>,ball:<D>,select:<itemId,...>
--w 1280 --h 720 --max-px 4000000       # 每格尺寸与最终图像素上限
--out DIR|FILE.png                      # 参考关卡只能写到 viewer_evidence/shots
--save-bookmark NAME                    # 把当前单视图相机写进关卡 cameras[]（复用）
```

- **默认** layers = `props,structures`；`ball` 只在显式给出或 `tier:` 时画（近景不会被
  0.3 m 幽灵球挡住）；grid 是 1 m 细线 + 10 m 粗线，另加米制比例尺。
- **可复现**：相机由 Python 从关卡文档算出（top 只框 bounds，iso/side 含真实高度），
  同一输入同一像素；JSON 的 `camera`/`views` 是可直接复用的世界坐标。
- **热/冷**：本机（software GL）实测 `story_01_bench_size` 首次约 30–60 s（Chromium +
  44 个 GLB + 首帧 shader 编译；机器忙时更久），之后约 1 s；`_ref_cancer` 首次约
  2 min（511 件、145 个 GLB、地形），之后约 1.1 s。超时用 `--timeout`。
- **失败**：`--out` 越界、未知 view/layer、参考关卡写仓库、页面 console 有错都会在 JSON
  里给出 `code` 和 `error`，不会留下半个文件（临时文件 + `os.replace`）。
- **已知限制**：`render` op 本身仍拒绝（它属于 Blender 路径）；看图走 `shot.py` /
  `GET /api/render`。top 是透视俯视（不是正交）；阴影默认关（`--shadows` 可开，慢）。
- **下一步**：GUI 与 agent 的完整同构（操作注册表、查询/选择、dry-run、路线图）见
  [EDITOR_HUMAN_AI_PARITY.md](../../Docs/Design/Tools/EDITOR_HUMAN_AI_PARITY.md)。

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
再设 `LEVELEDITOR_URL` + `LEVELEDITOR_TOKEN`。它会用真实鼠标/键盘测试：左键点选、
Shift 加选、框选、右键飞行+W+滚轮、中键/空格平移、Alt+左键轨道、F 聚焦、Q/W/E/R、
多选 gizmo 拖动一次批量/一条撤销、支撑面旋转、拖放叠放，并截 `control_*.png`。

冒烟脚本会：加载 127 件真实模型 → 跑坐标审计 → 点选椅子 → 方向键推 0.05 m → `Ctrl+Z` 恢复**逐字节相同** → 在 D=0.50/1.25 检查阶梯计数 → 拖一个苹果进场景再撤销 → 行走模式前后走 → 截图按钮下载 PNG → 打开 `physics_test_field/C01` 再切回第一关（并校验文件 sha 未变）。

截图默认写到 `/tmp/leveleditor-shots/`，可用 `LEVELEDITOR_SHOTS` 指定。本次交付的图在 `Docs/Evidence/LevelEditorWeb/`。

---

## 6. 已知限制（phase 1）

- 只编辑一份关卡；同一时间一个写者（设计如此）。
- 没有缩放（道具不应缩放；结构尺寸用左侧数值面板）。
- 「落到面上」按世界 AABB 的底面贴到射线命中点，不会把道具**转到斜面法线**；要按声明支撑精确坐正用「按支撑坐正」（`seat` op，和 CLI 同一实现）。
- 多选/框选的命中按对象**中心**；特别大的物件中心在框外但身体在框内时不会入选。
- 旋转后的支撑保持是“沿 Y 调整 + 声明支撑面”，不是真实物理落地；v1 校验仍用世界 AABB 粗筛（v2 才是 OBB）。
- 顶部「俯视」是**透视**俯视，不是正交相机（Unity 的 ortho 开关没做）。
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
├── shot.py                   # agent 看图 CLI（相机规划/参考路径守卫/daemon 客户端）
├── shot_daemon.py            # 常驻渲染 daemon（私有 server + headless Chromium）
├── shot_cdp.py               # 极简 CDP/WebSocket 客户端
├── shot_png.py               # PNG 尺寸检查 + tEXt 溯源块
├── web/
│   ├── index.html            # 布局 + import map
│   ├── favicon.svg
│   ├── css/app.css
│   ├── js/api.js             # token / op 调用 / SSE
│   ├── js/view.js            # three.js 场景、选择、gizmo、行走、阶梯、审计
│   ├── js/main.js            # UI 面板与操作编排
│   ├── js/shot.js            # 无头渲染钩子（__editorReady / __editorShot）
│   └── vendor/three/         # three r186 + addons + LICENSE（MIT）
└── test/smoke.mjs            # 可选浏览器冒烟
Tools/levelbuild/ref_models.py # _ref_* 本地网格解析与只读规则（唯一实现）
Docs/tests/leveleditor/
├── test_web_server.py        # API/权限/undo/原子写/锁/冲突/SSE
├── test_ref_models.py        # 参考关卡解析/只读/不跨关卡提供网格
└── test_coordinate_audit.py  # 坐标审计（椅子/花圃/斜面/边界）
```
