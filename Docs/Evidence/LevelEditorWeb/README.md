# 证据：浏览器关卡编辑器（`Tools/leveleditor`）

2026-11-15，freeEgg（aarch64、2 核、无 GPU、SwiftShader），Chromium 156 headless，
1440×900，通过 **Tailscale 地址** `http://100.113.66.61:8777`（非 localhost）打开。
截图来自当前 `story_01_bench_size` 的临时副本（130 件/11 结构/1522 u，含用户试用加的两件），
冒烟脚本跑完即删，不改仓库里的 `placement.json`。

## 主视图

| 图 | 内容 | 读数 |
|---|---|---|
| `01_overview_3q.png` | 3/4 适配视角 + 44 def 道具库 | 130 件 / 11 结构 / 1522 u；gate 0 错 |
| `02_selection_gizmo.png` | 点选 `struct_lounge_chair_01`，OBB + 移动 gizmo | 世界轴对齐盒 x[-12.60,-12.00] y[0,0.35] z[-10.40,-8.80] |
| `03_ladder_d050.png` / `04_ladder_d125.png` | D=0.50 / 1.25 阶梯 | 106/124 现在可吸；颜色按 D_req |
| `05_palette.png` | 道具库（44 def，缩略图/尺寸/valueUnits/D_req） | 拖放放置 |

## 键位/选择（`control_*.png`）

| 图 | 交互 |
|---|---|
| `control_select.png` | 左键点选，OBB + 信息面板 |
| `control_multiselect.png` | Shift+左键加选（椅子+长凳），每件 OBB + 淡色整体框，gizmo 在并集 pivot |
| `control_boxselect.png` | 空白处左键拖矩形框选（阈值 4 px） |
| `control_fly.png` | 按住右键飞行：指针锁定、W 前进、滚轮调速（HUD 速度 6.0） |
| `control_pan.png` / `control_orbit.png` | 中键/空格+左键平移；Alt+左键轨道 |
| `control_focus.png` | F 聚焦选中物 |
| `control_tools.png` | Q/W/E 工具、X 切 Local、R 缩放禁用提示 |
| `control_multidrag.png` | 真实 gizmo 箭头拖动多选：一次 batch、一条撤销（`transform 2 object(s)`） |
| `control_rotate.png` | 旋转叠放在长凳上的 serving_tray 被接受，底面仍 0.45（与长凳顶面一致） |
| `control_drop.png` | 把盘子拖到 plate_02 上：自动 stackedOn，gate 绿（用户报的 100% AABB 重叠已不再出现） |

## OBB 对比

`control_obb.png` = 左「旧：世界 AABB」/ 右「新：OBB（黄）+ 可选世界 AABB（淡灰）」，
对象是 30° 的 `ramp_to_table`。原始两张为 `control_obb_before.png` /
`control_obb_after.png`。OBB 由 `aabb`/`size` + `rotationQuat` + `position` 画 8 角点，
与 Unity BoxCollider 同形；世界 AABB 只是 v1 粗筛/互穿校验的旋转后外包盒。

## 区域统计

| 图 | 内容 |
|---|---|
| `control_regions.png` | 「小区」「大区」两个命名区域：列表、min/max 数字框、可视/删除、3D 半透明盒 |
| `control_region_count.png` | 统计范围=大区、口径=个数、D=0.80：tier 63/5/4/0，直方图，累计曲线，区域对比表 |
| `control_region_volume.png` | 同一区域口径=体积 m³：直方图纵轴变为体积，旁边显示每格占区域总体积百分比（悬停） |

区域写 `regions.json`（schemaVersion 1），独立于 placement.json/undo；统计全部由
`GET /api/stats` 服务端计算（前端只渲染 bins/tiers/cumulative/compare）。

## 测试与几何

```text
python3 -m unittest Docs.tests.leveleditor.test_web_server       29 passed
python3 -m unittest Docs.tests.leveleditor.test_coordinate_audit  7 passed
python3 -m unittest discover -s Docs/tests                       743 passed
python3 Docs/tools/validate_plan.py                              PASS
node Tools/leveleditor/test/smoke.mjs                            PASS
```

- 坐标审计 130/130，最大误差 0.00007 m；椅子世界盒与设计注记逐位相同。
- 冒烟用真实鼠标/键盘：左键/Shift/框选、右键飞行+W+滚轮、中键/空格平移、Alt+轨道、
  F 聚焦、Q/W/E/R、真实 gizmo 箭头多选拖动（一条撤销）、支撑面旋转、拖放叠放、
  画区域→读 `/api/stats`、切个数/体积、C01、切回文件 sha 不变、无 console error。
- 指针锁定：**非 localhost 的 http** 上 headless Chromium 授予；Windows 实机 Chrome
  仍需主人按一次键确认（失败会自动退化为按住右键读 movementX）。
- 结束时 `placement.json` sha256 = `8f6f6ea8…`（用户试用后的当前 HEAD 状态，未回退）。

## 未授权参考关卡（`_ref_cancer` / `_ref_mas1`，2026-11-16）

这两关的网格是从商业游戏提取的未授权资产，**截图本身也不入库**：它们放在仓库外、
git-ignored 的 `~/KatamariRef/viewer_evidence/`（`ref_cancer_view_overview.png`、
`ref_cancer_view_parent_lines.png`、`ref_cancer_evidence.json`）。仓库里只保留这段文字描述。

打开方式（本机 Linux，headless Chromium 156，SwiftShader，964×773 画布）：

```bash
python3 Tools/leveleditor/server.py --level _ref_cancer --port 8791
```

- 编辑器自动打开 `Docs/Design/Levels/_ref_cancer/placement.model.json`（511 件 / 145 def /
  5 结构 / 737,377 u），页面顶部是红黑条纹 `UNLICENSED REFERENCE` 横幅，道具库、结构、
  复制/删除/贴地/落面全部禁用；服务端对 move/set/remove/export/batch/undo/redo 一律拒绝，
  文件 sha256 在尝试前后不变。
- 145/145 def 通过 `original_defs.json → modelFile → ~/KatamariRef/models/glb-pack/*.glb`
  解析到本地 GLB（2.46 MB 传输）；全部 511 件的世界包围盒与数据 `aabb` 比值 **0.9982–1.0022**
  （中位数 1.0000），底面与 `position.y − aabb.y/2` 的误差 **0**。
- `ref_cancer_view_overview.png`：3/4 俯瞰，59×80 m 的围墙场地里是日式海岸小镇——左侧
  一排民居/店铺（蓝顶、灰瓦、带招牌的 `barbershop`、`barbershop_sign`）、中间小路和密集
  小道具（番茄、柿子、螃蟹、栅栏、邮筒、花圃）、右侧一排树冠和 `medium_tree`、后方
  两栋多层公寓/超市。为看清地图，截图时临时隐藏了 4 面编辑器 boundary 平面；其余 UI
  （版本、UNLICENSED 横幅、父子连线 (83)、球径阶梯 207/19/206/79）都是真实状态。
- `ref_cancer_view_parent_lines.png`：选中 `horseshoe_crab_001`（子）与其父
  `medium_tree_002` 并打开「父子连线」；两条品红色细线从螃蟹/旁边的桶连到树中心，
  周围能看到带贴图的树冠、樱花树、向日葵、理发店转灯、房屋和地面上的柿子/番茄。
  选中面板显示 `group`/`groupId` 与「参考网格 UNLICENSED」。
- 加载（headless SwiftShader，软件渲染）：页面 ready 11.9–16.6 s，其中 `/api/level`
  0.95–1.1 s、`new WebGLRenderer`/着色器初始化 9.2–13.8 s、145 个 GLB + 511 次摆放
  1.7–6.6 s（模型网络 1.6–6.5 s、摆放 0.05–0.10 s）。软件光栅器的首帧着色器编译可达数十秒。
- 帧时间（`renderer.render` + `gl.finish()`，5 帧中位数）：开阴影 30–50 ms（≈20–34 fps，
  1071 draw calls / 119,630 tris）；关阴影 16–26 ms（≈38–63 fps，551 calls）。
  这是无 GPU 的 SwiftShader 数字；同样的场景在主人 Windows 机器上应远高于此。
- `_ref_mas1` 同样可开：648 件 / 105 def / 5 结构 / 1,448 u，105/105 GLB 解析成功，
  38 条父子指针，726 个 mesh；页面 ready 12.3 s（WebGL 初始化 9.75 s、105 个 GLB 1.19 s），
  无 JS 异常。`~/KatamariRef/viewer_evidence/ref_mas1_view_overview.png` 是一间日式房间：
  竖立的拉门/障子、墙边柜子和桌台、地面上的小件（电池、铅笔、硬币、玩偶等），
  所有模型朝上且与 placement 的底部对齐；右侧阶梯在 D=0.30 m 显示 587 可吸 / 61 灰。
- 区域统计在参考关卡同样可用：`regions.json`（git-ignored）里保存 `town`
  `[-16,0,-12]..[22,8,34]`，`GET /api/stats?scope=region&region=town` 返回
  492 件 / 911.25 m³ / 155,235 u，页面区域盒与统计面板可正常显示。
- 测试：`python3 -m unittest discover -s Docs/tests` → **790 passed**；
  `python3 Docs/tools/validate_plan.py` → **PASS**。

## v2 文档查看（`_ref_cancer/placement.v2.json`，2026-11-16）

v2 的截图同样含未授权网格，只放在仓库外 `~/KatamariRef/viewer_evidence/`；仓库里只写描述。

```bash
python3 Tools/leveleditor/server.py --level _ref_cancer --port 8794
# 默认就是 placement.v2.json；要复现旧文件：
python3 Tools/leveleditor/server.py --level _ref_cancer --port 8795 --placement placement.model.json
```

- `ref_cancer_v2_perspective.png`：整关 3/4 远景。59×80 m 的场地里，中心是 v2 地形
  （绿/棕分区；侧车 3,455 三角里 class 3 的碰撞墙面默认不画，规则墙由活动墙层表示），
  35 个固体是紫色解析盒，活动墙是青色 20×15 m 矩形（高 3 m），
  kill plane 是红色网格（y=-4），511 件道具按原作位置/旋转/缩放摆放；屋顶、树冠、
  房屋、昆虫笼等贴图模型可见。右侧阶梯 D=0.30 m 显示 214 食物 / 65 黄 / 148 红 / 84 灰。
- `ref_cancer_v2_top.png`：正俯视整关。能数出地形分区、紫色固体、墙上沿、树木列和
  场地边界；kill plane 红网格铺在场地下方。地形仅覆盖 20×15 m 碰撞核心，核心外的
  511 件布景本来就只有 v1 的视觉 stage 有地面，v2 碰撞文档没有——不是渲染丢失。
- `ref_cancer_v2_parent_lines.png`：选中 `beetle_fight_001`（父 `insect_cage_001`），
  品红色父子线从子件连到笼子；选中面板显示 `parent`、`behaviour`、`scale`、`absorbable`、
  `growthMm3` 和「参考网格 UNLICENSED」。
- `ref_cancer_v2_after_switch.png`：从 v1 文件点「打开 v2」后的同一页；横幅变成 v2、
  v2 工具条出现、校验变成「结构检查（v2）0 错」。
- `ref_cancer_v1_legacy.png`：显式打开 `placement.model.json` 时的黄色横幅
  「这是 v1 旧文档，已被 v2 取代」+「打开 v2」按钮；它明确说明 v1 的 544 个错误
  不代表 v2 数据。
- `ref_cancer_v2_evidence.json`：本次 headless 的计数/开关/加载时间/异常记录。

本次读出：v2 结构检查 **0 错**（`ref_v2_from_extract.structural_errors`）；511 props /
145 defs / 35 solids / 1 wall / 83 parent links / 3,455 terrain triangles。页面 ready
约 17 s（SwiftShader 软件 GL；其中 WebGL 初始化 ~15 s、145 GLB 加载 ~1.6 s），渲染
1,140 draw calls / 120,917 triangles。`/api/v2/*` 的七个开关均有 HTTP 入口；
v2 编辑/设计数值校验器仍未实现（见 `Docs/Design/Tools/EDITOR_HUMAN_AI_PARITY.md` §1.2.1）。
