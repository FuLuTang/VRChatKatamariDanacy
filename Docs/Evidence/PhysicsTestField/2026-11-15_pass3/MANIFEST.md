# bring_back_e — Round H / pass3（commit 7f5cd7b，指纹 2026-11-15d）

Windows HEAD **7f5cd7b**（`merge --ff-only` 自 origin/main；合并前把本地 pass2 证据 5 个文件临时移到 `C:\Users\Public\pass2_local` 以免与入库文件同名，合并后 pass2 目录为仓库版本）。

## 目录

| 路径 | 内容 |
|---|---|
| `README.md` | 结论、编译/测试、指纹、新规则输出、仅剩的 5 条已知失败 |
| `console.log` | pass3 Unity Console 全量（589 行） |
| `matrix.md` | 36 跑汇总 + 每档合计 + ALERT + notApplicable + 新 derived 行 |
| `traj_ramps.md` | C01/C03/C05/C06/C13 × 四档逐点轨迹 |
| `tests.md` | EditMode 全量与重点套件结果 |
| `assets/` | **54 个编译产物**（33 个 `SerializedUdonPrograms/*.asset` + 21 个 U# program `.asset`），路径与仓库一致 |
| `assets_files.txt` | 上述 54 个文件的相对路径清单 |
| `assets_sha256.txt` | 54 个文件的 sha256（已抽验与 Windows 工作树一致） |
| `SHA256SUMS` | 证据文件（不含 assets/）的 sha256 |

## 关键数字

- EditMode：603 total / 602 passed / 1 failed（唯一失败 `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`，SDK 链接检查）。
- 矩阵合计：0.30 = 58/0/1；0.60 = 57/1/1；1.10 = 56/2/1；2.20 = 52/5/2（pass/skip/unexpected），与期望完全一致。
- `size_accuracy`：+0.0091 / +0.1454 / +0.4614 / +0.3854。
- 仅剩 5 条非预期失败：C05 `climbed` ×4（45° 电机极限）+ C13 2.20 `seam_no_step +0.9499`（已立缺陷单）。

## 提交用

`assets/` 里的 54 个文件可直接覆盖到仓库同名路径提交，作为「编译产物与源码一致」的整批。Windows 工作树里同路径仍是 modified，未做任何还原。
