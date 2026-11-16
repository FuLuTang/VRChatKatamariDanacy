# Hub 就座修复验证（O5）— 2026-11-16

## 同步
Windows `1003278 → 4f3ca1b`（`git fetch` 显示 `1003278..4f3ca1b`）。空场景法后跑 sha256（LF 归一化，逐路径）：
`identical_to_origin` 的 11 个显式路径已 `git checkout --`；18 个保留（9 个 SerializedUdonPrograms、`CpuSkirmish.unity`、
PTF 三件、`KatamariBallMotor.asset`、`BallGrowthDiagnostics.asset`、`BallView.asset`、`PickupFieldHost.asset`、`ProjectSettings.asset`）。
`git merge --ff-only origin/main` 首次被 20 个未跟踪文件挡住——它们与 `origin/main` 里的同名文件逐字节一致（20/20 identical），
删掉本地副本后 ff-only 成功：`Updating 1003278..4f3ca1b Fast-forward`，HEAD `4f3ca1b`。

## 编译与测试
- `refresh_unity force/all` → `[UdonSharp] Compile of 39 scripts finished`，0 error。
- EditMode 全量：**total 807 / passed 806 / failed 1 / skipped 0 / 9.45 s**，唯一红仍是
  `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`（`URL does not resolve for Const_SystemBooleanArray`）。
- 新测试数：`HubLoopMathTests` **30** 全绿、`HubRoomMathTests` **26** 全绿（共 56，全部 Passed）。
- 跑完后场景 `test_prop` 计数：**0（含 inactive）**，`dirty=False`，场景里没有 `test_prop` 根物体。
  但仍然出现一次 sweep 警告（与 O4 同形）：

  ```text
  [RadialProxyLocalBoundsTests] sweeping a stray fixture root 'test_prop' (children=1, scene='UdonEditorTestScene')
  ```

  → net 有效、且**每次全量都会触发一次**，说明某个 fixture 的 TearDown 依旧漏一个 prop（`children=1`）。

## 场景改动（只有一处，非 Play 下保存）
`LevelEntry`：`(0, 0.15, -2.2)` yaw 0 → `(-12.5, 0.15, -10.4)` yaw 180（球在 `(-12.5,0.15,-12.5)`，入口在球北侧 2.1 m，面朝 −Z 看球）。
保存后 `dirty=False`；`git diff --numstat` = **5 / 2**（另含新脚本字段默认值 `disableAutoSeat: 0`、`seatCheckFrames: 30`、`holdGraceSeconds: 2.5` 首次写入场景）。
`VRC Katamari/Hub/Check Room Wiring` 逐字见 `wiring_check.txt`：**0 problem(s)**，`level entry is 2.1 m from the ball start`，没有 far-entry 警告。

## ClientSim Play（owner 不在 Play，验证完立刻停止）
探针：`TeleportTo(1200, 0.1, 1.2)`（垫子中心）。日志顺序与预期一致：

```text
[hub] ready levels=1 state=0 holdInLevel=True autoSeat=True holdGrace=2.5 lift=0.1 tolerance=1 cooldown=1
[hub] teleport cause=enter-level levelIndex=0 name=野餐一角 mode=1 state=0 arrivalSource=seat from=(1200.00, 0.00, 1.20) arrival=(-12.50, 1.16, -13.58)
[hub] round started cause=arrived levelIndex=0 name=野餐一角 mode=1 state=1 distanceFromEntry=0.02222228 framesWaited=2
[StoryRoundController] sit-down ignored: the round is not idle (state=1); retry is the result card's button.
[05:58:43.450][ClientSimPlayerStationManager] Entering Station /PlayerBall/FollowRigRoot/RideRoot/RideSeat
[seat] measuring: settleFrames=12 sampleFrames=8 verticalOnly=False
[ride] seated entered
[ride] seat requested without a click frame=97
[hub] seat request cause=started requested=True seated=True frame=97
[hub] seat result seated=true frame=98 state=1
[seat] locked: eyeAboveSeat=1.05m offset=(-0.3414214,-1.049255,-0.07587993) left=0.1109296m eyeError=0.1114241m frames=13
```

- **没有** `[hub] teleport cause=hold-in-level`，**没有** `[seat] seat restored to authored offset`（起跑后 2.5 s 内和之后 30 s 都没有）。
- `[hub] seat result seated=true` —— 就座成功，且 ClientSim **确实实现了** UseStation（`Entering Station` 行就是它打的）。
- 坐了 30 s+：`[story] t=…29.3` 一路 `center=(-12.50,0.15,-12.50) diameter=0.2997 … falling=False`，玩家位置 `(-12.39,-0.14,-13.66)`
  是座位补偿后的锚点（`[eye] dist≈1.40` m 到球心），没有掉出去。

## 三张 Game 视图（start / 10 s / 66 s）
三张构图**完全一致**（只有控制台行在前进：`t=12.2 → 28.3 → 66.5`，`rejects` 一路涨到 12904，`absorbed=0`）：
镜头在球座上方 1.05 m 平视前方，画面下半部是浅灰地面、中右一大片黄色野餐垫（也即选关垫）、垫上散落水果/餐盘，
左侧远处一张深色矮桌+两把椅子，右侧一截木箱。**球本身在画面下边缘之外**（相机在球顶上方 1.05 m、球心距相机 ~1.4 m），
所以"相机是否跟着球走"这一条**无法用这三张图证明**——球三分钟内一次都没动，构图没理由变化。

## 没做到的：驱动球
`DesktopMoveInput` 是每帧写 `SetMoveAxes` 的，探针手写 `motor.SetMoveAxes(1,0,0)` 被下一帧覆盖（`[story] axes=0,0` 一直是 0），
MCP 也没有可持续按住 WASD 的通道。因此**一次吸收都没有发生**（`absorbed=0`），结果卡/回房/重试未验收。
