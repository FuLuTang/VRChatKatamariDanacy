# 第一个完整回合验收（O6，调试驾驶）— 2026-11-16

## 1. 同步 / 编译 / 测试
- Windows：`git fetch` → `4f3ca1b..2331457`；空场景法 + 逐路径 sha256（11 个与 origin 一致已 `checkout --`，19 个保留：15 个重烤资产 + PTF 三件 + `ProjectSettings.asset` + `CpuSkirmish.unity`）；`git merge --ff-only origin/main` → HEAD **2331457**。
- `refresh_unity force/all` → `[UdonSharp] Compile of 39 scripts finished in 00:12.271` / `00:02.854`，**0 error**。
- Unity 新建了 4 个 `.cs.meta` 并已带回：`Runtime/BallContact/ProxyContactMath.cs.meta`、`Runtime/Input/DebugDriveMath.cs.meta`、`Tests/EditMode/BallContact/ProxyContactTests.cs.meta`、`Tests/EditMode/Input/DebugDriveTests.cs.meta`。
- EditMode 全量：**total 821 / passed 819 / failed 2 / skipped 0 / 9.90 s**。两红：
  1. `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`（已知）；
  2. **`VRCKatamari.Tests.ProxyContactTests.ConvexTetraClosestPointAndContact`（新红）**，`ProxyContactTests.cs:143` 的 `Assert.IsTrue(TryClosestPointOnProxy(... KindConvex ... radius: 0f ...))` 返回 false。
     运行时确认（`execute_code`）：`IsValidProxy(3, …, radius 0f, …, hull 4, faces 4) = False`、同参数 `radius 1f = True`。
     根因：`ProxyContactMath.IsValidProxy` 对所有非 box 类型都要求 `radius > 0`，而凸包不使用 radius；测试按凸包语义传 0。建议的机械修法（未改）：
     把 `if (… radius <= 0f) return false;` 拆进 `KindSphere`/`KindCapsule` 分支，凸包只校验 `IsFiniteFloat(radius)`。
- 新测试数：`ProxyContactTests` **8**（7 绿 1 红）、`DebugDriveTests` **6**（全绿）。
- 跑完 `test_prop`：场景根 **2**、`test_prop` **0**；sweep 警告仍出现一次：
  `[RadialProxyLocalBoundsTests] sweeping a stray fixture root 'test_prop' (children=1, scene='UdonEditorTestScene')`。

## 2. 重导入 Level 1 —— 红（44 error），场景未改
`VRC Katamari/Level/Import StoryMode Level 1 (story_01_bench_size)` →
`[level] +0 ~0 -0 props=0 other=0 errors=0`、`managed root:`（空）、`props: 0 unchanged`，`RunImport` 返回 `ok=False`、`errors=44`：

```text
defs[0] def 'acorn' [collider]: collider.type must be box/sphere/capsule/convex (got '')
defs[1] def 'apple' [collider]: ...
（44 个 def 全中，一个 def 一条）
```

根因：`Docs/Design/Levels/story_01_bench_size/placement.json` 的 `defs[]` 现在只有 `{id, kind}`，`collider` 整块不见了
（props 侧改成了 `aabb` / `absorbDiameterReq` / `growthMm3`），而 `PlacementImporter.cs:1069` 仍然要求 `collider.type`。
场景没有落任何改动：`dirty=False`、无保存、无 diff。现有 host 数据仍是 `propColliders=130`，而 12 个新 proxy 数组
（`propProxyKinds` … `propProxyHullFaces`）**都是 0 条**（不是 130 条 0 hull）。

## 3. 第一个完整回合（ClientSim，调试驾驶）
- 探针 `TeleportTo(1200, 0.1, 1.2)` **这次没有生效**：ClientSim 把玩家留在出生点 `(1200.00, 0.00, -2.80)`，
  hub 因此一直没启动（`[hub] round state=0`），`rideGate=False roundGate=False`，球完全不动。改用
  `SendCustomEvent("TrySeatLocalPlayer")`（`RideSeat` 的 U# public 方法）后才走通：
  `[ClientSimPlayerStationManager] Entering Station /PlayerBall/FollowRigRoot/RideRoot/RideSeat` →
  `[StoryRoundController] the local player sat down: round started` → `[ride] seated entered` → `[seat] locked: eyeAboveSeat=1.05m`，
  两个门变 `ride=True round=True`。
- 调试驾驶：`SetProgramVariable` 写 `debugDriveEnabled/debugAxisX/debugAxisZ` 生效，`GetProgramVariable` 读回一致，
  日志 `[input] debug drive on axes=(0,1) seconds=0`；球从 `(-12.50,0.15,-12.50)` 真的滚到 `(-6.86,1.75,-17.61)` 再到 `(-6.31,2.78,-15.46)`。
- 直径/吸收时间序列见 `story_series.txt` / `table.md`（1 Hz `[story]` 行），每档百分比见 `grow_lines.log`（58 条 `[grow]`，单次 +1.9%…+2.1%）。
- 卡住 27 s：t=74.6…101.8 一直是 `D=0.7033 absorbed=10 center=(-6.35,0.35,-19.05)`——自动驾驶把球顶在一个没到的西瓜上。
- 回合结束：`剩余 0 s`、`当前 1.31 m`、`目标 1.25 m`，结果板 `呈给国王 / ★☆☆  直径 1.31 m / 嗯，勉强能看。`；
  `[hub-room] phase result -> hub`，HUD 换成 `上一局已结算，走进野餐垫再来一局`。
- 重试（`StoryRetryButton.SendCustomEvent("OnRetryPressed")` 生效）：
  `[StoryRoundController] retry started round=story_01_bench_size:1 attempt=1` →
  `[PickupFieldHost] field reset restored=45 props=130` → `[grow] units=-285 D=1.312 -> 0.2997 (-77.2%)`，回合重新 state=1。
- 退出按钮：场景里叫 `RideExitButton`（不是 `BallRideExitButton`），`SendCustomEvent("Interact")` **没有任何日志**（当时 `seated=False`）→ 未验证。

## 4. 已知不一致
- 直径两个来源不一致：`[grow]` 报 `D=0.8122` 时 `[story]` 同一段报 `diameter=0.7033`（最多差 ~0.11 m）。谁对需要查。
- 2026-11-16 O6 的 `[PickupFieldHost] prop colliders not fully wired; attached props stay collidable. props=5` 仍在（hub cell 的 5 个装饰道具）。
