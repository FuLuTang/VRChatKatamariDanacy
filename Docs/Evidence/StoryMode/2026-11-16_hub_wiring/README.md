# Hub 接线（O4）— StoryMode.unity 2026-11-16

## Part 1 验证（全绿）

Windows `a15f2fa → 1003278`（ff-only，先空场景法，**没有弹模态框**）；sha256（LF 归一化）逐路径比较 41 同 / 18 保留。

- 编译 `Compile of 39 scripts finished` **0 error**（38 为 hub 四脚本加入前，39 为加入后）。
- EditMode **802 / 801 / 1 / 0**（9.82 s），唯一红仍是 `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`。
- 全量跑完后层级里 `test_prop` **0 个（含 inactive）**，`dirty=False`；但 net 触发过一次：

  ```text
  [RadialProxyLocalBoundsTests] sweeping a stray fixture root 'test_prop' (children=1, scene='UdonEditorTestScene')
  ```

  即 `5da02e3` 的 sweep 有效，但它**是在收拾一个已有的漏**：某个 fixture 仍逃出自己的 TearDown，被 net 救回（`children=1`，非 DontSave 时的 0 child 形态）。测试顺序：这是全量运行中唯一一条 sweep 行，未定位到具体方法。

## Part 2 场景接线（已做，已保存）

顺序与结果：

1. 菜单 `VRC Katamari/Level/Import StoryMode Hub Room (hub)` 第一次只建 `LevelContent/hub` 并提示加宿主 →
   给 `LevelContent/hub` 加 `PickupFieldHost`（不接 motor）→ 再跑：
   `[level] cell=hub +5 ~0 -0 props=5 other=7 errors=0  level=hub arena=hub scene=StoryMode`，附 `valueUnits: declared=212 … read-back: verified`。
2. 新建：`HubCenter`(1200,0,0)、`HubSpawn`(1200,0.1,-2.8)、`LevelEntry`（球旁 2.2 m 朝球）、
   `HubRoom`+`HubRoomHost`、`HubLevel`+`HubLevelLauncher`、`HubReturnButton`（关卡内，BoxCollider）、
   `HubResultBoard`（World Space Canvas @1200,1.45,3.88，朝 −Z，scale 0.002，含 `ResultCard`/`ResultText`/`RetryButton`）、
   `StoryHud/HubPromptText`（uGUI Text）、`EventSystem`。
3. `resultCardRoot`/`resultText` 指到墙上的卡；`StoryResultPresenter.controller` 接控制器。
4. **K2 替换**：删 `StoryLevelLab`，`BallView` 上加 `StoryBallDiagnostics`（`motor` 指向 `PlayerBall/MotorHost`）。
   已核对：场景里没有任何其它组件引用 lab bootstrap 的 camera/drive 字段（它只有 motor + 几个读数开关；
   `ReferenceCamera` 是独立根物体，未被引用）。
5. `RideSeat` 下新建 `StandPoint`(0.45,−0.15,0) / `ExitPoint`(−0.55,−0.15,0)，接到 `VRCStation.stationEnterPlayerLocation` / `stationExitPlayerLocation`。
6. `VRCSceneDescriptor.spawns` = 单一项 `HubSpawn`；`HubRoomHost` 接 launcher/controller/hubCenter/`selectZones[0]=select_picnic_corner`/promptText；`HubLevelLauncher` 接 controller/`levelEntries[0]=LevelEntry`/`levelNames[0]=野餐一角`/roomSpawn/rideStation/rideSeat，`statusText` 留空。
7. `VRC Katamari/Hub/Check Room Wiring` 逐字：

   ```text
   [hub-check] hub island is 1200.0 m from the level island
   [hub-check] done: 0 problem(s), 1 circle(s), 1 level entry/entries, zone radius 1.2, room radius 12
   ```

## Play（两次，owner 不在 Play）

第一次（`play1`）：玩家被放到 `(1200.000, 0.100, -2.800)`=HubSpawn ✓，`[hub-room] ready zones=1 …` ✓，`[hub] ready levels=1 …` ✓，
但 `UdonBehaviour on 'BallView'/'HubRoom' will not run`（新建的 U# 程序资产在首次 AddComponent 时还没生效）。
之后用 `TeleportTo(1200,0.1,1.2)`（垫子中心）触发 proximity：

```text
[hub] teleport cause=enter-level levelIndex=0 name=野餐一角 mode=1 state=0 from=(1200.00,0.00,1.20) arrival=(0.00,0.25,-2.20)
[hub-room] auto-start levelIndex=0 state=0 distance=0.005000087
[hub-room] phase hub -> loading state=0 inHub=True zone=0
[hub] round started cause=arrived … distanceFromEntry=0.02222228 framesWaited=2
[hub-room] phase loading -> playing state=1 inHub=False zone=-1
[hub] round state=1 level=story_01_bench_size attempt=0 activeLevelIndex=0
```

即 **走上垫子 0.35 s → 传送到关卡 → 开局** 这一段通了（编辑器不能驱动 WASD，用传送代替“走进去”）。

**没有通的一段：坐球。** 控制台**没有** `[ride] seated entered`；随后出现

```text
[seat] seat restored to authored offset
[hub] teleport cause=hold-in-level attempt=1..3 distanceFromLevel=1200 from=(1200.00,0.10,-2.80)
```

玩家被 `hold-in-level` 反复判定不在关卡（读数 1200 m），说明 **ClientSim 下 Station 没有把玩家吸到座位上**，
launcher 的"留在关卡"规则于是想把人送回入口，而最终玩家停在 HubSpawn。这是本轮唯一没打通的环节，
也是最该先修的一条（`VRCStation` 的 use 需要 `UseStation`/`Networking.LocalPlayer` 吸入，ClientSim 里不出 `[ride] seated` 即未吸）。

第二次（`play2`，清理完 3 个空 UdonBehaviour 与重建 diagnostics 后）：`Could not load the program` **0 条**，
`[hub-room] ready`、`[hub] ready`、`[story] t=… diameter=0.2997 …`（新诊断行顶替 `[story-lab]`）都在；
唯一残留警告 `There can be only one active Event System.`（ClientSim 自己会建一个，场景里这个是我为
World Space 重试按钮加的；不接 `EventSystem` 时按钮只显示不响应，主线是垫子）。

## 截图

`hub_game_view_play.png`（Game 视图，Play 中）：正面是 ClientSim 的黑色菜单面板（Actions: Close Menu / Respawn /
Settings / Exit Playmode；Player Info: Local Player、Player ID 1、Master；Player Height 1.90）。面板背后就是新房间：
浅灰地面中央一大块**黄色野餐垫**（选关圈），左右两面浅蓝灰墙围出房间一角，左前方地板上有个小动物造型道具，
右侧靠墙一张深色矮凳；Hierarchy 里能看到 `HubCenter / HubSpawn / LevelEntry / HubRoom / HubLevel /
HubReturnButton / HubResultBoard / EventSystem` 都已在场景中。视点位于 `(1200, 0.1, -2.8)` 朝 +Z —— 就是 HubSpawn，说明出生点与朝向符合设计（面对垫子和结果板）。

## 已知未完成

- 坐球（`[ride] seated`）在 ClientSim 下不成立，见上；`hold-in-level` 因此来回拉扯。
- 结果卡/回房/重试的 Play 验收没跑到（需要回合真的结算，而坐球没通）。
- 5 个新 U# 程序资产（`HubRoomHost`、`HubLevelLauncher`、`HubReturnButton`、`HubStartButton`、`StoryBallDiagnostics`）是
  `AssetDatabase.CopyAsset` + 改 `sourceCsScript` 造的，之后由 `CompileAllCsPrograms` 重编译（39 scripts）生效；
  首次 AddComponent 在程序资产落地前发生，留下了 3 个空 `UdonBehaviour`，已删。
