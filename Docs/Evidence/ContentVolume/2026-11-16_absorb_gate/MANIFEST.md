# Round O3a bring-back

Windows HEAD `a392e46`；无 stash/reset/commit/push；`ProjectSettings.asset` 未动。

## 需要提交
1. `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity` — **132/3**：`propVolumeK` 新块（130×`  - 0.7`）、两条旧 motor 字段（`maxItemVolumeWorldM3: 512`、`absorbVolumeRatio: 10`）被丢弃、`serializedPublicVariablesBytesString` 1 换 1。详见 `storymode_absorb_gate.diff`。
2. 程序资产编译噪声批（一次性，26 个）：`Assets/SerializedUdonPrograms/*.asset`（8 个，`081e62d4`、`18f009c9`、`254e6f6d`、`50dd0a80`、`6643498`、`deccdf0e`、`eb192fc9`、`fdc0f5af`）+ `Runtime/`、`Labs/`、`Story/` 下的 24 个 U# 程序 `.asset`（`windows_status_after.txt` 里 `M` 的全部 `.asset`，除 `PickupFieldHost.asset`/`KatamariBallMotor.asset` 属于同一批）。
3. 无新增 `.meta`。

## 未动
`Labs/PhysicsTestField/PhysicsTestField.unity`、`Labs/CpuSkirmish/CpuSkirmish.unity`（观察项，未打开/未保存；PTF 仍含待丢弃的 `maxItemVolumeWorldM3`，下次保存它会消失）、`ProjectSettings/ProjectSettings.asset`、未跟踪的 `_RefUnlicensed.meta` 与 `Docs/Evidence/PhysicsTestField/2026-11-15_pass*`。

## 未解决
- EditMode 5 红（1 已知 + 4 门金标），见 README §2。
- PTF v2 31 条 unexpected（派单预期 1–5），根因是 C01 斜坡未进接触集，见 README §5。
- `RadialProxyLocalBoundsTests` 仍在当前场景泄漏 `test_prop`（9a32592 未覆盖）。
