# Unity `long` 探针实测（2026-11-16，round O1）

前置：Windows 工作副本已快进到 `02e1262`（含 `78ee9c1` 的 int→long Step A）。全部操作在一个 Unity 会话内完成，未重启编辑器。

## 探针 1 — Udon `long` / `long[]` 编译并往返：**PASS**

摆放：临时 `Assets/VRC_Katamari/Labs/LongProbe/LongProbeBehaviour.cs`（内容与 [UNITY_PROBES_FOR_LONG.md §1](<Docs/Research/ContentVolume/UNITY_PROBES_FOR_LONG.md>) 同源）+ 临时场景 `LongProbe.unity`，跑完全部删除（`git status` 不再出现 LongProbe）。

1. 编译：`[UdonSharp] Compile of 34 scripts finished in 00:09.363` / `00:02.997`，**无任何 error/warning**。
2. 零先例基线的复核与突破（解压 `serializedProgramCompressedBytes` 按 UTF-16 计数 `SystemInt64`）：

   | 时点 | 文件数 | `SystemInt64` 总计 |
   |---|---:|---:|
   | 探针前（`78ee9c1` 合并后、程序未重编） | 67 | **0** |
   | 探针脚本程序资产就位并重编后 | 68 | **51** |

   命中文件：`18f009c9…`=2、`deccdf0…`=3、`e41903d7…`=6（LongProbeBehaviour 自己的程序）、`eb192fc9…`=**40**。
   `eb192fc9…` 就是 `Runtime/BallMotor/KatamariBallMotor.asset` 的 `serializedUdonProgramAsset`（实测 `GetAssetPath` = `Assets/SerializedUdonPrograms/eb192fc99d65a434aba5fe70424086c1.asset`），
   即 Step A 加进电机的 `long unbankedVolumeUnits/bankedScoreUnits` 与四个 `long[]` 真的进了 Udon 程序体。
   **注意**：「合并后立刻扫 = 0」是因为程序资产当时还是旧的，不是 Udon 拒绝 `long`；重编后 51。
3. 运行时（Play Mode 一次，`SendCustomEvent("RunProbe")`）控制台原文：

   ```text
   [longprobe] ok=True scalar=-9223372036854775808 sum=9007203549708290 mid=9007199254740993
   ```

   与文档预期逐字符相同（`mid` 未退化成 9007199254740992，`sum` 精确）。
4. 序列化（保存后的 `LongProbe.unity` YAML 原文）：

   ```yaml
   scalar: -9223372036854775808
   values: 010000000000000000000000010000000100000000002000ffffffffffffff7f
   ints: 0100000002000000
   ```

   `values` 64 个十六进制字符，与文档预期串完全相等（每元素 16 字符，little-endian）。

结论：UdonSharp 接受 `long` 标量与 `long[]`（字段、new、元素读写、加减比较、`long.MinValue`/`long.MaxValue` 常量、日志拼接），且场景 YAML 无损。Step A 成立。

## 探针 2 — `JsonUtility` / `SerializedProperty`：**PASS**

同一会话内用临时 `Assets/VRC_Katamari/Editor/LongProbeEditor.cs`（内容与文档 §2 相同）跑 `Katamari/Probes/Long JsonUtility And SerializedProperty`，跑完删除。控制台原文：

```text
[longprobe-json] out={"big":9007199254740993,"bigs":[1,4294967296,9223372036854775807],"small":-7}
[longprobe-json] roundtrip=True,True small=-7
[longprobe-sp] type=Integer longValue=9007199254740993 arraySize=3
[longprobe-sp] e0=1 e1=4294967296 e2=9223372036854775807
[longprobe-sp] applied longValue=9007199254740993
```

`propertyType` 打出 `Integer`（文档允许的枚举名差异），`longValue` 三值逐位相等；JSON 无科学计数、无 int 截断。

## 探针 3 — 重导入 diff：Step A = **空 diff**

在 `Worlds/StoryMode/StoryMode.unity` 上对 `PickupFieldHost` 执行 `PickupFieldSync.SyncFromChildren(host, Props)`（report 返回 `PickupFieldSyncReport`）→ 保存场景 →

```text
$ git diff --numstat -- .../StoryMode/StoryMode.unity
（空）
```

即字节完全一致：Step A 没有改动任何序列化数组形状，零重导入成立。

## 其它本轮结果

- U# 全量重编：`Compile of 34 scripts`，无 error。
- EditMode：790 / 789 通过 / 1 失败 / 0 跳过（9.24 s），唯一失败 `Tests.GraphNodeTests.CheckHelpURLsForSystemNodes`（`URL does not resolve for Const_SystemBooleanArray … Expected: True But was: False`，与 pass11 同一已知红）。新增 `Tests/EditMode/Ledger/LongLedgerRangeTests` 6 条全过（784 → 790）。
- PTF v1 36 runs：`[ptf] field cells=9 runs=36 model=v1 verdict=UNEXPECTED unexpected=5 skipped=8 elapsed=230s`；四个直径总计与 pass11 **逐项相同**（58/0/1、57/1/1、56/2/1、52/5/2），非预期 cell 同为 C05 四个直径 + C13@2.200。明细见 `ptf_v1_matrix.tsv`。
- StoryMode 20 s Play（无人操作、静止）：`[ballview] mesh=0.2847 d_motor=0.2997`（比例 0.9500）、`[shell] core=0.1423 shell=0.1423`（1.00，弹簧关闭后静止形变无滞后）、无报错。

## 场景改动（随本轮带回）

`StoryMode.unity` 一处：`BallView.smoothCoreGrowth` `1 → 0`（`147ca0f`/`02e1262` 的默认值对齐到 owner 偏好），`useVisualCoreRadius` 仍为 1，`BallGrowthDiagnostics` 未动。见 `storymode_smoothcoregrowth.diff`。
