# Round O6 bring-back

基线 `2331457`。无 stash/reset/commit/push；`ProjectSettings.asset`、`PhysicsTestField.unity`、`CpuSkirmish.unity` 未动。

## 新文件（Unity 生成，必须随提交走）
```
VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallContact/ProxyContactMath.cs.meta
VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Input/DebugDriveMath.cs.meta
VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/BallContact/ProxyContactTests.cs.meta
VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/Input/DebugDriveTests.cs.meta
```
已拷进 Linux 同名路径。

## 场景
**本回合无场景改动**：重导入被 44 个 `collider.type` 错误拒绝，`dirty=False`，没有保存，`StoryMode.unity` 与 HEAD 逐字节一致（无需带回）。

## 重烤资产批次（15 个，拷贝后与 HEAD 不同）
10 个 `Assets/SerializedUdonPrograms/*.asset`（各 1 行签名）+ 5 个程序资产 payload：
`KatamariBallMotor.asset`、`HubLevelLauncher.asset`、`PickupFieldHost.asset`、`BallView.asset`、`BallGrowthDiagnostics.asset`。
按仓库规则整批提交或整批丢弃，Hub/驾驶修复不依赖它们。清单见 `changed_tracked_paths.txt`。
