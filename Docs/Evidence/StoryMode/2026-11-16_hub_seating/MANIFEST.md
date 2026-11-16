# Round O5 bring-back

基线 `4f3ca1b`。无 stash/reset/commit/push；`ProjectSettings.asset`、`PhysicsTestField.unity`、`CpuSkirmish.unity` 未动。

## 1. 场景（唯一实质变更）
`Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity` — `5 / 2`：
`LevelEntry` 位置 `(0,0.15,-2.2)` → `(-12.5,0.15,-10.4)`、yaw 0 → 180；
`HubLevelLauncher` 的三个新字段首次落盘（`disableAutoSeat: 0`、`seatCheckFrames: 30`、`holdGraceSeconds: 2.5`）。
- 完整文件：`/tmp/unity_round_f/bring_back_o5/StoryMode.unity`（LF）
  sha256 `4a02d6a75a7f294ca2bf94fdb46a00e1791770b092ac2d676b95564a556f7c1a`
- 零上下文 diff：`storymode_seating.diff`（`git diff -U0`，6 行）
  sha256 `bf009a59ea71c26ffbc202a2fa8e54c250a4e9e29539125b17a6cdb321469da8`（`patch -p1` 可直接吃）

## 2. 生成资产批次（recompile 重烤，建议整批丢弃或整批提交）
从 Windows 拷了 64 个 tracked 改动文件到 Linux 同名路径；拷完 `git status` 只剩 **15** 个与 HEAD 不同，说明另外 49 个
本来与 HEAD 逐字节一致（Windows 那侧的差异只是重烤前的临时态）。这 15 个里：

- 10 个 `Assets/SerializedUdonPrograms/*.asset` —— 每个 1 行签名变化（纯噪声）：
  `081e62d4`、`18f009c9`、`254e6f6d`、`50dd0a80`、`66434980`、`bb940408`、`deccdf0e`、`eb192fc9`、`f2ebbf18`、`fdc0f5af`
- 5 个程序资产（payload 重烤，行数变化大）：
  `Runtime/BallMotor/KatamariBallMotor.asset` 1170/1017、`Runtime/Hub/HubLevelLauncher.asset` 723/204、
  `Runtime/Pickup/PickupFieldHost.asset` 397/335、`Runtime/BallPresentation/BallView.asset` 311/5、
  `Runtime/BallPresentation/BallGrowthDiagnostics.asset` 12/6

依据仓库规则（"signature-only diffs there are compile noise and are committed or discarded as a batch"），
这 15 个作为一个批次处理；**Hub 修复本身不依赖它们**（源码 `HubLevelLauncher.cs`/`BallRideStation.cs`/`HubLoopMath.cs`
在 `4f3ca1b` 里已提交）。
