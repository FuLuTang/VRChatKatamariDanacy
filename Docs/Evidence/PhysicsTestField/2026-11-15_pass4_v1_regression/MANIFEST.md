# bring_back_g — 第 J 轮（Ball Contact v2 S3：motor 集成，编译 + 测试 + v1 回归）

Windows 仓库 `D:\VRChatKatamariDanacy`，HEAD **44e0cfe**（`merge --ff-only` 自 origin/main）。

## 结论

- C# 0 错误、U# 0 错误（`Compile of 33 scripts finished`），**没有任何机械修复**。
- EditMode **681 / 678 通过 / 3 失败**：SDK 链接检查 + `ContactModelV2Tests` 2 条；上轮两条已修好全过。
- v1 回归（flag 关闭）：36 跑与 pass3（`2026-11-15d`）**逐行逐列完全一致**，合计 58/0/1、57/1/1、56/2/1、52/5/2，5 条已知红灯值也完全相同。

## 目录

| 路径 | 内容 |
|---|---|
| `assets/` | 重编后变化的 **54 个 `.asset`**（33 个 `SerializedUdonPrograms/*.asset` + 21 个 U# program `.asset`）+ **2 个新 `.meta`**（`NeedleSegmentTests.cs.meta`、`ContactModelV2Tests.cs.meta`） |
| `assets_files.txt` | 上述 56 个文件的相对路径清单 |
| `assets_sha256.txt` | 56 个文件的 sha256 |
| `tests.md` | EditMode 全量与逐类结果 |
| `matrix.md` | 36 跑 v1 回归矩阵、每档合计与 pass3 对照、5 条已知红灯、notApplicable、derived |
| `console.log` | 回归 Play 全程 Console（600 行） |
| `SHA256SUMS` | 本目录证据文件的 sha256 |

## 没有的东西

- **没有 `.cs` 修复**：编译一次通过。本轮未改任何源码，因此也没有 diff 补丁需要带回（上轮 CRLF 副本的问题不会再出现）。
- 未打开 `contactModelV2Enabled`；未碰测试场以外的场景；Play 后场景从磁盘重开 `dirty=False`。

## 同步时的处理

本地只有 3 个差异文件：`CpuSkirmish.unity`、`PhysicsTestField.unity`（判据重导产物）、`ProjectSettings.asset`——全部按约定保留。20 个 untracked `.meta` 与 origin 逐个哈希一致 → 删除（合并后由仓库版本恢复）；`2026-11-15_pass1/` 不在 origin → 保留。ff 成功。

## 一处需要你知道的量测

`ContactModelV2Tests` 实测 **11 条**（brief 写 10），其中 2 条失败（见 `tests.md`）。`NeedleSegmentTests` 12 条全过。
