# 证据：浏览器关卡编辑器（`Tools/leveleditor`）

2026-11-15，freeEgg（aarch64、2 核、无 GPU、llvmpipe/SwiftShader）。
页面通过 **Tailscale 地址**打开（`http://100.113.66.61:8777/?token=…`），
不是 localhost；Chromium 156 headless，1440×900。

| 图 | 内容 | 读数 |
|---|---|---|
| `01_overview_3q.png` | 第一关 3/4 适配视角（`story_01_bench_size`），左侧道具库全表 | 127 件 / 11 结构 / 44 def / 1500 u；gate 0 错 0 警告 |
| `02_selection_gizmo.png` | 点选 `struct_lounge_chair_01`，TransformControls 移动 gizmo | id/def/aabb(1.6,0.35,0.6)/D_req 1.858/150 u/world AABB x[-12.60,-12.00] y[0,0.35] z[-10.40,-8.80] |
| `03_ladder_d050.png` | 球径阶梯 D=0.50 m，按可吸性着色 | 104 现在可吸 / 2 在 20% 内 / 21 太大 / 11 不可吸；可吸 344 u |
| `04_ladder_d125.png` | 球径阶梯 D=1.25 m，含 D=1.25 的球体幽灵 | 121 / 1 / 5 / 11；可吸 766 u；下一档「大冷藏箱 1.34」 |
| `05_palette.png` | 道具库搜索 `apple`（名称/尺寸/valueUnits/D_req） | 面板第 1 行是完整全表，此图演示搜索过滤 |
| `06_walk_ball_eye.png` | 行走模式，眼高 0.60 m，从 `playerSpawn` 看向球起点 | HUD 显示「已锁定 · Esc 释放」；WASD/Shift/Tab |
| `08_physics_c01.png` | 打开 `physics_test_field/C01` 一格（只读） | 0 道具 / 7 结构；6×10 小地面 + 5° 坡 + 0.6 m 台面；有向内箭头 |

## 坐标审计

`window.__leveleditor.audit()`：**127/127 件通过**，最大误差 **0.00007 m**
（`cookie_02`），容差 0.01 m。椅子世界 AABB 与设计注记逐位相同：
`[[-12.6, 0, -10.4], [-12, 0.35, -8.8]]`。Python 侧同一组数字见
`Docs/tests/leveleditor/test_coordinate_audit.py`。

## 指针锁定

在 **非 localhost 的 `http://100.113.66.61:8777`**（`isSecureContext=false`）下，
headless Chromium **授予了** pointer lock（`document.pointerLockElement` 为 canvas），
`fallback=false`。headless 在锁定状态下吞掉了合成的 W 键；释放锁定后同样的
`W` 让相机移动（1.4 s 内 0.085 m，软件渲染约 1 fps，`delta` 被限制在 0.05 s/帧）。
应用本身的实体浏览器路径仍是「锁定优先；失败/被拒 → 按住右键拖动看方向 + WASD」。
**主人在 Windows Chrome 里实际按键一次**仍是需要确认的最后一项（见最终报告）。

## 改动后的关卡状态

截图全部只读（选择/相机/阶梯不改文件）。跑完后
`Docs/Design/Levels/story_01_bench_size/placement.json` 的 sha256 仍是
`614dbe76fd37f46a6c72d022c8fa436b6d5f3b2cf8b34cf97981de5baba889e0`（= `git HEAD`）。

## 测试

```text
python3 -m unittest Docs.tests.leveleditor.test_web_server       22 passed
python3 -m unittest Docs.tests.leveleditor.test_coordinate_audit  7 passed
python3 -m unittest discover -s Docs/tests                       634 passed
python3 Docs/tools/validate_plan.py                              PASS (61 tasks)
node Tools/leveleditor/test/smoke.mjs                            PASS (headless Chromium)
```

冒烟脚本默认在 `/tmp` 起临时服务 + 关卡副本，跑完删除；不会改仓库文件。
2026-11-15 最终一次：**`smoke: PASS`**（127 件、审计、点选、方向键、Ctrl+Z 逐字节、
D=0.50/1.25、拖放+撤销、行走 W、截图下载、C01、切回 sha 不变、无 console error）。
