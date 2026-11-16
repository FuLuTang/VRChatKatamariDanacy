# Round O2 bring-back

Windows HEAD: `8ba7c8e`. 无 stash/reset/commit/push；`ProjectSettings/ProjectSettings.asset` 未动。

## 需要提交
1. `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity` — 2 行（同一个 `PickupFieldHost` 的 `valueUnits` + `serializedPublicVariablesBytesString`），见 `storymode_step_b.diff`；`valueUnits` 2,080 hex / 130×int64 / sha256 `ed41c3a4…c853`。
2. 编译噪声批（一次性）：`SerializedUdonPrograms/*.asset` 若干 + 24 个 U# 程序 `.asset`（`Runtime/`、`Labs/`、`Story/` 下的同名 `.asset`），全部是重编后的程序体/签名差异；其中 `SerializedUdonPrograms/deccdf0…asset` 是 `PickupFieldHost` 程序（含 `SystemInt64`=3），`eb192fc9…asset` 仍是 `KatamariBallMotor`（`SystemInt64`=40）。
3. 无新增 `.meta`。

## 未动（保持原样）
`Labs/CpuSkirmish/CpuSkirmish.unity`、`Labs/PhysicsTestField/PhysicsTestField.unity`（受保护，观察项，零操作）、`ProjectSettings/ProjectSettings.asset`、未跟踪的 `_RefUnlicensed.meta` 与 10 个旧的 `Docs/Evidence/PhysicsTestField/2026-11-15_pass*`。

## 提醒（测试卫生）
EditMode 测试 `RadialProxyLocalBoundsTests.CreateProp` 会在**当前打开的场景**里建 `test_prop` 根物体；只要它在 `finally` 之前失败，残留就会随下一次保存进仓库。本轮已经踩到并清理（见 README §3）。建议后续把该 fixture 改成 `EditorSceneManager.NewPreviewScene` 或确保 `finally` 覆盖。
