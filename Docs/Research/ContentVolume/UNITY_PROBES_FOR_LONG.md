# `long` 迁移的 Unity 侧探针清单（供后续 Unity 轮执行）

**用途：** [OPEN_DECISIONS.md](../../OPEN_DECISIONS.md) (r) 定下「体积用定点 `long`」，并把迁移拆成 A→B→C→D 四步；其中三条事实**只能在 Unity 里验证**：Udon 的 `long[]`、`JsonUtility`/`SerializedProperty` 对 `long` 的处理、以及重导入后的场景 YAML diff 大小。本文件是那三条的**操作清单**：怎么摆探针、看什么、**通过时应该看到什么原文**、失败时长什么样、失败后走哪条兼容档。

**三条探针一条都没有执行。** 写这份文件的是 Linux 侧实现者，不能开 Unity；所有"预期输出"是按 Unity/UdonSharp 的公开行为与仓库现有数据推出来的**断言**，不是实测记录。唯一真的查过的是 §1 的"零先例基线"——那是解压仓库里已有的 Udon 程序产物做统计，纯读、不需要 Unity。

**状态前提（读完再动手）：**
- 迁移 **Step A 已落地在代码里**（2026-11-16）：`GrowthLedgerMath` 的体积/分数列与参数、`KatamariBallMotor` 的账本镜像与账本数组、`PickupAbsorbMath.TryAbsorbValueToLedgerGain` 的出参都已是 `long`；单位、量子、黄金值、场景数据**一个没动**。
- Step A **没有**改任何序列化数组：`PickupFieldHost.valueUnits` 仍是 `int[]`，只在读取点加宽（`SubmitProp` 里的 `(long)valueUnits[index]`）。所以下面第 3 条探针在 Step A 的预期是**空 diff**；它真正要防的回归在 Step B。
- `long` / `long[]` 在本仓库**有 C# 先例**，但**连 `long` 标量也没有被证过的 Udon 路径**——这一点比 (r) 的措辞更严格，必须先看清：
  - `Runtime/BallLayers/Attachment/AttachmentMath.cs` 是 `public static class`（:70），**不是** UdonSharpBehaviour；它的 `long` 常量/参数（:73-75、:170）和 `long[] attachSequences`（:229、:293、:346、:397、:448）只在 `Tests/EditMode/BallLayers/AttachmentTests.cs` 里被调用过。
  - 也就是说：这些方法**从未被任何 UdonSharpBehaviour 调用**，因此 UdonSharp 从未把它们（连同 `long` 的加/乘/比较和 `long[]` 的元素访问）编进任何 Udon 程序；`long` 也从未出现在 `[SerializeField]` 里。
  - 结论：**`long` 标量与 `long[]` 都只证明了"C# 编译 + EditMode 运行"，Udon VM 与序列化两侧都是零先例。** 所以探针 1 不只验 `long[]`，它同时验 `long` 标量在 Udon 里的字段、算术与读回；(r) 里"`long` 标量有先例"应读作"`long` 在 C# 侧有先例"。

---

## 0. 三条探针各自要回答什么

| # | 探针 | 回答的问题 | 失败的后果 | 失败后的兼容档 |
|---|---|---|---|---|
| 1 | Udon `long` / `long[]` 编译并往返 | UdonSharp 能否把 `long` 标量与 `long[]` 的字段、参数、算术、元素访问编进 Udon 程序，运行时读写是否逐值相等 | Step A 的账本镜像/参数与账本数组（`confirmedVolumes` / `bankedScores` / `commitGains` / `predictedGains`）不能是 `long`，A 步本身不成立 | `int[]` 拆高低位，或 `int[]` + milli-unit（影响审计 §4） |
| 2 | `JsonUtility` 与 `SerializedProperty` 的 `long` / `long[]` | 场景/预制体序列化、Inspector 与 `SerializedObject` 能否无损存取 64 位整数 | Step B 的 `valueUnits → long[]` 会丢数据，B 步不成立 | 同上；且 B 步必须换表示 |
| 3 | 重导入 diff 大小 | 动序列化数组会牵动多少场景 YAML；Step A 是否真的零重导入 | Step B 的"同批重导入"变成不可审的巨 diff | 分场景提交、或先做 B 的只读回读 |

三条**都要在同一个 Unity 会话里一次做完**再下结论：里世界只允许一个 agent 操作 Unity（AGENTS.md），来回开关编辑器代价高。

---

## 1. 探针 1 — Udon `long` / `long[]` 编译并往返

### 摆法

新建临时目录 `Assets/VRC_Katamari/Labs/LongProbe/`（跑完连同 `.meta` 一起删），放一个 UdonSharpBehaviour 与一个测试场景：

```csharp
using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Labs
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LongProbeBehaviour : UdonSharpBehaviour
    {
        [SerializeField] private long scalar;
        [SerializeField] private long[] values;
        [SerializeField] private int[] ints;

        // 每次写入都立刻读回，读回值与写入值不等就报错。
        public bool RunProbe()
        {
            bool ok = true;
            scalar = long.MinValue;                  // -9223372036854775808
            values = new long[4];
            values[0] = 1L;
            values[1] = 4294967296L;                 // 2^32
            values[2] = 9007199254740993L;           // 2^53 + 1（float64 也不能表示）
            values[3] = 9223372036854775807L;        // long.MaxValue
            if (scalar != long.MinValue) ok = false;
            if (values[2] != 9007199254740993L) ok = false;
            if (values[3] != 9223372036854775807L) ok = false;
            // 只累加前三项：1 + 2^32 + (2^53+1) = 9007203549708290。
            // 不要把 long.MaxValue 也加进去，那一项会让 long 求和本身溢出。
            long sum = values[0] + values[1] + values[2];
            if (sum != 9007203549708290L) ok = false;
            Debug.Log("[longprobe] ok=" + ok + " scalar=" + scalar + " sum=" + sum
                + " mid=" + values[2]);
            return ok;
        }

        public long ReadBack() { return values == null ? 0L : values[2]; }
        public long ReadScalar() { return scalar; }
        public int ReadIntColumnLength() { return ints == null ? 0 : ints.Length; }
    }
}
```

场景里放一个空 GameObject 挂这个组件，`values` 在 Inspector 里填 4 个元素（`1`、`4294967296`、`9007199254740993`、`9223372036854775807`），`scalar` 填 `-9223372036854775808`，`ints` 随便填 `[1,2]`。再放一个空物体挂一个只调 `RunProbe()` 的小触发器（或直接从编辑器的 Execute 菜单调）。

### 零先例基线（本仓库已查，可复现）

`Assets/SerializedUdonPrograms/*.asset` 是 UdonSharp 的编译产物，程序体是 gzip 压缩后写成 hex 的（`serializedProgramCompressedBytes`）。把 **67 份全部**解压后按 UTF-16 匹配类型名，得到：

| 类型名 | 出现次数 |
|---|---:|
| `SystemInt32` | 1,783 |
| `SystemSingle` | 4,363 |
| `SystemDouble` | 24 |
| **`SystemInt64`** | **0** |

复现命令（在 Unity 项目根目录执行，纯读、不写文件）：

```bash
python3 - <<'PY'
import glob, gzip, re
def u16(s): return s.encode('utf-16-le')
total = {n: 0 for n in ("SystemInt32", "SystemInt64", "SystemSingle", "SystemDouble")}
decoded = 0
for p in sorted(glob.glob('Assets/SerializedUdonPrograms/*.asset')):
    raw = open(p, encoding='utf-8', errors='ignore').read()
    m = re.search(r'serializedProgramCompressedBytes: ([0-9a-f]+)', raw)
    if not m:
        continue
    data = gzip.decompress(bytes.fromhex(m.group(1)))
    decoded += 1
    for n in total:
        total[n] += data.count(u16(n))
print("decoded", decoded, total)
PY
```

预期输出（与本文件记录一致）：

```text
decoded 67 {'SystemInt32': 1783, 'SystemInt64': 0, 'SystemSingle': 4363, 'SystemDouble': 24}
```

含义：`long` 在这个仓库里**从未被编进任何 Udon 程序**（与 `AttachmentMath` 没有 behaviour 调用者一致，见上文状态前提）。所以探针 1 有一条**最硬的判据**：编译完 `LongProbeBehaviour` 之后，新增的那份 `.asset` 解压后必须至少出现 1 次 `SystemInt64`（把上面脚本的 glob 指向新文件，或整体重跑一遍看总数从 0 变成 >0）。**仍然是 0 = Udon 不接受 `long`**，Step A 当场不成立，要退回兼容档。

### 操作

1. `refresh_unity`（编辑器可能没焦点，不会自己编译），等编译结束。
2. `read_console`：只要有任何 `[UdonSharp]` 编译错误就先停下来记录下来（见"失败长相"）。
3. 打开 `Assets/SerializedUdonPrograms/`，找到这个组件新生成的 `.asset`（按 GUID 命名），确认它存在且**比别的程序大**（说明真的有 heap 条目）。
4. 进 Play Mode，触发 `RunProbe()`，`read_console` 取那一行；再读 `ReadBack()` / `ReadScalar()`。
5. 退出 Play Mode，**保存场景**，`git diff` 看该场景里这个组件的 `scalar:` / `values:` 两行（这一步同时是探针 3 的一部分）。注意要看的是 **Play 之前 Inspector 里手填的那两个字段**：`RunProbe()` 在 Play Mode 里写的值退出时会被 Unity 回退，所以场景里留下的应当正好是下面那串 hex。

### 通过时应看到的原文（exact）

- 控制台**没有**任何包含 `long` 的 UdonSharp 错误、没有 `Unsupported type`、没有 `VRC.Udon.Compiler` 报错、没有 `The type 'System.Int64[]' ...` 这类行。
- 新生成的 `Assets/SerializedUdonPrograms/*.asset` 解压后 `SystemInt64` 出现次数 **> 0**（基线见上：现在全仓 67 份都是 0）。
- Play Mode 里恰好一行：
  ```
  [longprobe] ok=True scalar=-9223372036854775808 sum=9007203549708290 mid=9007199254740993
  ```
  （`mid` 若变成 `9007199254740992`，说明某一层退化成 double/float64 —— **失败**，即使 `ok` 看起来是 True 也要记录实际整行。）
- 保存后的场景 YAML 里该组件块出现：
  - `scalar: -9223372036854775808`
  - `values: 010000000000000000000000010000000100000000002000ffffffffffffff7f`，长度是 **4 × 16 = 64** 个十六进制字符（`long` 每个元素 16 个；对照现在的 `int[]`：每个元素 8 个）。
  这个串是上面四个值按数组顺序、每个值 little-endian 8 字节拼出来的（`1` → `0100000000000000`，`2^32` → `0000000001000000`，`2^53+1` → `0100000000002000`，`2^63-1` → `ffffffffffffff7f`）。**串不相等就是失败**；若只有大小写或前缀差异（`0x`、大写 hex）则记为"格式差异、值正确"。
  - 参照现状：`Worlds/StoryMode/StoryMode.unity:17498` 的 `valueUnits:` 就是同一种 hex blob，130 个 `int32` 元素 = 1,040 个十六进制字符，每个元素 8 个字符——所以"每元素 16 个字符"这条判据是从本仓库真实数据推出来的，不是猜的。
- 重新打开场景（或在 Play 前先保存再读）后 `ReadScalar()` 仍返回 `-9223372036854775808`，`ReadBack()` 仍返回 `9007199254740993`。

### 失败长相（记录原文，不要改写）

- 编译期：`[UdonSharp] ... Unsupported type ...`、`[UdonSharp] ... long[] ...`、`error CS...` 但只在 UdonSharp 的编译日志里。
- 编译通过但运行时：`ok=False`、`sum` 不等于 `9007203549708290`、`scalar` 读回成 `0` 或饱和值、`values[2]` 丢精度、或 `values` 序列化后只剩 `[]`。
- 若只有"序列化层面"失败（场景里 `values:` 为空、Inspector 显示 0），而 Play Mode 里代码内 new 的 `long[]` 正常，则结论是"**Udon 支持 `long[]`，序列化不支持**"——这正好把 (r) 的兼容档缩小到"只改序列化字段的形状"，要分别记。
- **单独一类失败要分开记：字符串拼接。** 上面这段探针（以及 Step A 里的 `KatamariBallMotor.LogCommitFailureOnce`）会做 `("... gain=" + 一个 long)`。如果 UdonSharp 恰好在这一层报错（`long` 的 `ToString`/`String.Concat` 没有被编进 Udon），那是**第 4 个待验证点**，不是 `long[]` 的问题：先把这个错误原文记下来，再把探针里的日志行改成不拼 `long`（例如只拼 `values[2]` 的 int 低位）重跑一次，把两个问题分开结论。Step A 的账本算术（加、减、比较、`long.MaxValue` 常量）与它无关。

---

## 2. 探针 2 — `JsonUtility` 与 `SerializedProperty` 的 `long` / `long[]`

### 摆法

临时脚本 `Assets/VRC_Katamari/Editor/LongProbeEditor.cs`（跑完删；只放在 `Editor/` 下，Udon 运行时不碰 JsonUtility）：

```csharp
using System;
using UnityEditor;
using UnityEngine;

namespace VRCKatamari.EditorTools
{
    [Serializable]
    public class LongProbeData
    {
        public long big;
        public long[] bigs;
        public int small;
    }

    public class LongProbeAsset : ScriptableObject
    {
        public long big;
        public long[] bigs;
    }

    public static class LongProbeEditor
    {
        [MenuItem("Katamari/Probes/Long JsonUtility And SerializedProperty")]
        public static void Run()
        {
            LongProbeData data = new LongProbeData();
            data.big = 9007199254740993L;
            data.bigs = new long[] { 1L, 4294967296L, 9223372036854775807L };
            data.small = -7;
            string json = JsonUtility.ToJson(data);
            Debug.Log("[longprobe-json] out=" + json);
            LongProbeData back = JsonUtility.FromJson<LongProbeData>(json);
            Debug.Log("[longprobe-json] roundtrip=" + (back.big == data.big)
                + "," + (back.bigs != null && back.bigs.Length == 3
                    && back.bigs[0] == 1L && back.bigs[1] == 4294967296L
                    && back.bigs[2] == 9223372036854775807L)
                + " small=" + back.small);

            LongProbeAsset asset = ScriptableObject.CreateInstance<LongProbeAsset>();
            asset.big = 9007199254740993L;
            asset.bigs = new long[] { 1L, 4294967296L, 9223372036854775807L };
            SerializedObject so = new SerializedObject(asset);
            SerializedProperty big = so.FindProperty("big");
            SerializedProperty bigs = so.FindProperty("bigs");
            Debug.Log("[longprobe-sp] type=" + big.propertyType
                + " longValue=" + big.longValue + " arraySize=" + bigs.arraySize);
            if (bigs.arraySize == 3)
            {
                Debug.Log("[longprobe-sp] e0=" + bigs.GetArrayElementAtIndex(0).longValue
                    + " e1=" + bigs.GetArrayElementAtIndex(1).longValue
                    + " e2=" + bigs.GetArrayElementAtIndex(2).longValue);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[longprobe-sp] applied longValue=" + so.FindProperty("big").longValue);
            UnityEngine.Object.DestroyImmediate(asset);
        }
    }
}
```

### 操作

`refresh_unity` → 菜单 `Katamari/Probes/Long JsonUtility And SerializedProperty` → `read_console`。

### 通过时应看到的原文（exact）

- ```
  [longprobe-json] out={"big":9007199254740993,"bigs":[1,4294967296,9223372036854775807],"small":-7}
  ```
  字段顺序按声明顺序；数字**不带引号**；**不允许**出现 `9.0072e+15`、`2147483647`（被夹到 int）、或 `big` 变成 `0`。
- ```
  [longprobe-json] roundtrip=True,True small=-7
  ```
- ```
  [longprobe-sp] type=Integer longValue=9007199254740993 arraySize=3
  [longprobe-sp] e0=1 e1=4294967296 e2=9223372036854775807
  [longprobe-sp] applied longValue=9007199254740993
  ```
- 若 `propertyType` 打出的是 `Long`（而不是 `Integer`）也算通过——那只是枚举名不同，**关键是 `longValue` 三个值逐位相等**。把实际枚举名记进本文件末尾的"实测回填"节。

### 失败长相

- `JsonUtility` 抛 `ArgumentException` / 输出里 `big` 被截断成 `2147483647` 或 `-1`：**`long` 的 JSON 路径不可用**。注意 Udon 运行时本来就不用 JsonUtility（AGENTS.md：运行时不读盘 JSON），所以这条失败**不阻塞** Step A，只阻塞"用 JSON 传账本"这类未来方案。
- `SerializedProperty.longValue` 抛 `InvalidOperationException` 或恒返回 0 / 截断：**序列化的 `long` 不可用**，则 Step B 必须走兼容档；同时也说明 Step A 里"不改序列化数组"的判断是对的。
- `bigs.arraySize` 与填入的不一致、或元素读回 0：`long[]` 的序列化不可用（与探针 1 的场景 YAML 结论对照，两边可能一致也可能不一致，都要记）。

---

## 3. 探针 3 — 重导入 diff 大小

### Step A 的预期（现在就能验，且应该是"什么都没变"）

1. 在干净的 `git status` 下记录基线：`git status --short`、`git diff --numstat`。
2. 打开 `Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity`，对场景里挂着 `PickupFieldHost` 的物体执行一次作者工具的重同步（`PickupFieldSync`，即 (q) U2 的"单次写入并回读"路径），保存场景。
3. 预期：`git status --short` **只有** `StoryMode.unity`（若保存引入了别的噪声就记下来），而 `git diff --numstat` 的**插入/删除行数都是 0**——也就是文件字节没变，或者只有 Unity 的版本噪声行。
   - 更强的判据：`git diff` 为空。**Step A 的验收就是"不需要重导入"**；只要 valueUnits 那一行有任何字符变化，就说明有东西偷偷改了数组的序列化形状，先查 `PickupFieldHost` 的字段类型。
4. 同一套动作对 `Labs/CpuSkirmish/CpuSkirmish.unity`、`Labs/PhysicsTestField/PhysicsTestField.unity`、`Runtime/Pickup/PickupFieldHost.asset`、`Runtime/BallMotor/KatamariBallMotor.asset` 各做一遍，预期同样为空 diff。

### Step B 的预期（留给以后，写在这里是为了让两次数字可比）

Step B 把 `valueUnits` 换成 `long[]` 后，**每个元素从 8 个十六进制字符变成 16 个**，所以：

| 文件 | 现有元素数 | 现有 `valueUnits:` 十六进制长度 | Step B 后长度 | 单行长度增量 |
|---|---:|---:|---:|---:|
| `Worlds/StoryMode/StoryMode.unity:17498` | 130 | 1,040 | 2,080 | +1,040 |
| `Labs/CpuSkirmish/CpuSkirmish.unity`（32 处，每处一个 PlaygroundProp） | 32 行 × 1 | 8/行 | 16/行 | +8/行 |
| `Labs/CpuSkirmish/PlaygroundProp.asset`（2 处） | 2 | 8/行 | 16/行 | +8/行 |
| `Labs/PhysicsTestField/PhysicsTestField.unity`（2 处，`sizeModelBaselineVolumeUnits`，是 float 不是数组） | — | — | — | 0（**不该变**） |
| `Runtime/Pickup/PickupFieldHost.asset`（2 处） | 按 asset 里实际元素数 | 8 × n | 16 × n | +8 × n |

判据：

- 每处 `valueUnits:` 的十六进制长度**恰好翻倍**（不是"差不多"），且把十六进制按新步长切块后解出的值与原 `int32` 值逐个相等（`sum` 与原 `level.expected.valueUnits` 相等，`max` 不变）。
- `git diff --numstat` 的插入行数 ≈ 被改写的 `valueUnits:` 行数（一行一处），**不出现**大片重排、fileID 变化或组件丢字段。
- `PickupFieldSync` 的导入报告仍写 `read-back: verified`。

### 失败长相

- Step A 出现非空 diff：先怀疑字段类型被动过（`git diff --stat` 应只剩我这次改的 `.cs`），再怀疑编辑器把整个场景重排了（Unity 版本或序列化顺序噪声，记录 `git diff` 前 40 行）。
- Step B 长度没翻倍（还是 8/元素）：说明改的是 C# 字段但场景数据没重导入，或者同步工具还按 `intValue` 写。
- Step B 出现"元素数变了"：`PickupFieldSync` 写坏了，别提交，按 (q) U2 的回读失败处理。

---

## 4. 探针跑完要回填的三行结论

在本文件末尾追加（原文照抄 Unity 的控制台行，不要转述）：

```text
探针1 Udon long / long[]：PASS/FAIL — 控制台原文：<粘贴 [longprobe] 那一行>
探针2 JsonUtility/SerializedProperty long+long[]：PASS/PARTIAL/FAIL — 原文：<粘贴四行>
探针3 重导入 diff：Step A 空 diff = YES/NO — numstat：<粘贴>
```

三条都 PASS 才允许把 (r) 的"未验证项"改成已验证，并开始 Step B；只要有一条 FAIL，就按第 0 节的兼容档改方案，**不要**先把 B 做下去再回头。

---

## 5. 不要做的事

- **不要**为了探针去重启或重开 Unity 编辑器（AGENTS.md：GUI 重启会以管理员身份跑）。
- **不要**提交探针用的临时 `.cs` / 场景 / `.asset`；跑完连同 `.meta` 一起删掉，只把结论写回本文件。
- **不要**在 Step A 顺手改 `PickupFieldHost.valueUnits` 的类型来"顺便验一下 long[]"——那会立刻让所有关卡场景丢字段，正是 (r) 把 B 独立成一步的原因。
- **不要**把本文件的预期输出当成实测结果引用；没跑就是没跑。
