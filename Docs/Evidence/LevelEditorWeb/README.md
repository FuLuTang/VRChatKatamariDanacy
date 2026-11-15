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
