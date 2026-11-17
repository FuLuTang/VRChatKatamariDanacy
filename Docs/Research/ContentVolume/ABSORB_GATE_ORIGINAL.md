# 吸收门：恢复原作的数字（k·bbox、10× 体积、向下取整整 mm）

**日期：2026-11-16（项目时区 Asia/Shanghai）。** Owner decision：**吸收门完全按原作的数字**。本文记录改动、口径、实测的前后对比，以及仍然存在的差距（成长侧的 1.0 回收属于步骤 C/D，不在本文）。
**证据来源**：`PARITY_AUDIT.md` G07/G08/G13；`Docs/Research/KatamariBallRules/ORIGINAL_TERRAIN_AND_LAYOUT.md` §5.5；`~/KatamariRef/meta/raw_growth_mechanism.txt` §2；`Docs/Research/ContentVolume/FIXED_POINT_VOLUME_IMPACT.md` §2（O2/O3/O5）。**本文的每个数字都是本文用仓库内数据实算的**，不引用旧文档的二手值。

---

## 0. 一句话结论

原作的拾取判据是**三段**，不是一段：

```text
V_item      = k · a·b·c              k 每类型作者手调，缺省 0.70（1,008/1,425 型正好 0.70）
Req_mm      = floor(1000 · cbrt(60 · V_item / π))      # 球体积 ≥ 10 × 物件体积，向下取整到整 mm
可吸        ⇔ floor(1000 · D_ball) ≥ Req_mm            # D_ball = 电机的语义直径
```

我们旧的门是 `a·b·c ≤ min(512, (π/6)·D³/10)`：**少了 k**、**不取整到 mm**、**上限 512 m³ 与球耦合**。少了 k 让长度当量偏严 `(1/0.70)^(1/3) = 1.126` 倍，正好对上 `gateRatio` 的中位 1.127。

改完之后，`_ref_cancer` 在 0.222 m 球上从 **91** 件（旧门）变成 **184** 件，与原作的 **184** 件**逐件一致**（145 个类型的 `Req` 全部等价，比值分布退化为单点 1.0000）。

---

## 1. 改了什么（file:symbol）

| 层 | 位置 | 改动 |
|---|---|---|
| 门（唯一实现） | `Runtime/Pickup/PickupAbsorbMath.cs`：`TryComputeOriginalItemVolume`、`TryFloorMillimetres`、`TryComputePickupReqDiameterMm`、`IsBeyondAbsorbLimit`、`TryResolveAbsorbGate` | 用上面三段式替换 `TryComputeAbsorbVolumeLimit` / `IsVolumeBeyondLimit` / `IsBeyondAbsorbVolumeLimit`；`EvaluateAbsorb` 直接取 `k` 与语义直径 |
| 常量 | 同上：`DefaultVolumeK=0.70`、`MaxVolumeK=32`、`AbsorbVolumeRatio=10`、`MaxItemVolumeSanityWorldM3=1000`、`MmFloorEpsilon=1e-3`(mm) | 比率与上限不再是序列化字段 |
| 电机 | `Runtime/BallMotor/KatamariBallMotor.cs`：`pendingVolumeK`、`SubmitCandidateWithVolumeK`、`GetAbsorbGateDiameterMm` | 删 `maxItemVolumeWorldM3`(512) / `absorbVolumeRatio`(10) / `GetAbsorbVolumeLimitWorldM3`；候选提交携带逐件 k；`SubmitCandidate` 保留为「用 0.70」的薄委托 |
| 场地宿主 | `Runtime/Pickup/PickupFieldHost.cs`：`propVolumeK`、`EffectiveVolumeK`、`UpdatePropObstacles`、`SubmitProp` | 逐件 k 列（可选，缺列回落 0.70）；挡路/支撑分类与提交走同一个谓词 |
| 数据层 | `Tools/levelbuild/placement_core.py`：`DEFAULT_VOLUME_K`、`volume_k_error`、`effective_volume_k`、`absorb_diameter_req_mm`、`absorb_diameter_req`、`is_absorbable`、`DEF_KEYS`/`PROP_KEYS`、`canonical_doc`、`_check_defs`、`_check_props`、`population_tables` | `defs[].volumeK`/`props[].volumeK`（缺省 0.70，**不写默认值**）；派生的 `absorbDiameterReq`/`absorbable` 按 k 与整 mm 重算；档位表显示 Req mm |
| 导入 | `Editor/LevelBuild/PlacementImporter.cs`（`DefEntry.volumeK`/`PropEntry.volumeK`、`BuildDeclaredVolumeK`）、`Editor/Pickup/PickupFieldSync.cs`（`propVolumeK` 列 + 回读校验）、`Editor/Pickup/PickupFieldAuthoringRules.cs`（`DefaultVolumeK`/`MaxVolumeK`/`VolumeKError`/`TryResolveVolumeK`/`FindVolumeKMismatches`） | 声明 → 宿主列，回读比对；越界拒绝而不是夹紧 |
| 编辑器 | `Tools/levelbuild/editor_core.py`（`op_set_def`、`add_def` 的 `volumeK`）、`Tools/leveleditor/server.py`（`dReqMm`/`volumeK` 派生、`/api/stats` 与梯档整 mm）、`web/js/main.js`+`view.js`（def k 可编辑、Req mm 显示、梯档着色整 mm） | 只在 `volumeK != 0.70` 时写盘；清空 = 删键 |
| 对照 | `Docs/Design/Parity/parity_harness/run_parity.py`（G07/G08/G13、`OursLevel` 的 k 与整 mm、供给比用 k·bbox） | 三条指标从「缺机制/参数差」变「一致」 |
| 测试 | `AbsorbSizeGateTests`（重写）、`AbsorbSizeLimitTests`（重写）、`BlockingPropMathTests`、`PickupAbsorbTests`、`RejectNoGainTests`、`PickupMotorSetOwnershipTests`、`PickupFieldAuthoringRulesTests`、`Docs/tests/levelbuild/*`、`Docs/tests/leveleditor/*`、`Docs/tests/parity/*` | 黄金值按公式重算并写出来源；新增 k 默认/覆盖、整 mm 边界、ε 守卫、挡路谓词一致性、字节不变测试 |

**为什么上限从 512 m³ 变成 1000 m³ 常量**：512 是「8 m 立方体」，它和球一起被写进 `min(512, 球体积/10)`，等于给门加了一条与球耦合的硬顶——原作没有这条。改成整 mm 比较后，比球大的物件只是 `Req` 更大，天然不可吸，不需要顶。保留的 `MaxItemVolumeSanityWorldM3 = 1000` 只是**数值卫生**：2.20 m 上限球允许的最大物件是 `(π/6)·2.2³/10 = 0.558 m³`，1000 m³ 是它的约 **1,792 倍**，而整关内容（`_ref_cancer` 511 件）合计也才 3,022 m³，所以只有坏掉的 bounds 三元组能碰到它；碰到时结论是「不可吸 = 挡路」，不是「无限大」。

**ε 守卫**：`floor(1000·x + 1e-3)`，单位 mm。1e-3 mm = 1 µm，约是 2.2 m 处 float32 ulp（2.4e-4 mm）的 4 倍，是 mm 量子的 1/1000，也远小于作者数据的 0.1 mm 分辨率。它只把「本来正好落在整 mm 上、被浮点压下去一点」的值抬回整数：算例会给出 299.99998 mm → 300。C# 与 Python（`MM_EPSILON`）用**同一个数**，两边不会在刀口上分歧。`AbsorbSizeGateTests.TheEpsilonRepairsAnExactMillimetreBoundary` 就是这条的钉子（299 mm 的球仍然拒绝）。

---

## 2. `_ref_cancer`：改前 / 改后（每档可吸件数）

数据：`Docs/Design/Levels/_ref_cancer/placement.json`（511 件可吸道具）+ `original_defs.json`（每件的原作 `gateVolumeM3`/`bboxVolumeM3`，即 k = V/bbox）。「原作」= 用原作自己的 `gateVolumeM3` 算 `Req` 再按整 mm 比较，与我们的新门同式。

| 球径 D (m) | 旧门件数 | 新门件数 | 原作件数 | 旧/原作 | 新/原作 |
|---|---|---|---|---|---|
| 0.222（原作起始球） | **91** | **184** | **184** | 0.495 | **1.000** |
| 0.30 | 207 | 213 | 213 | 0.972 | 1.000 |
| 0.50 | 315 | 331 | 331 | 0.952 | 1.000 |
| 0.80 | 388 | 394 | 394 | 0.985 | 1.000 |
| 1.10 | 420 | 421 | 421 | 0.998 | 1.000 |
| 1.50 | 442 | 444 | 444 | 0.995 | 1.000 |
| 2.20 | 457 | 457 | 457 | 1.000 | 1.000 |

**逐类型 `Req` 比值**（145 个类型，新门 / 原作）：

| | 中位 | 最小 | 最大 | ±1% 内 |
|---|---|---|---|---|
| 旧门 / 原作 | 1.1264 | 0.4069 | 2.1625 | 15 / 145 |
| 新门 / 原作 | **1.0000** | **1.0000** | **1.0000** | **145 / 145** |

这与 `PARITY_AUDIT` 的旧值（中位 1.1273、`within1pct` 15、`harderThanOriginal` 117）逐项对上，说明「差的就是 k」这条诊断被证实了。k 的分布：中位 0.70、最小 0.10、最大 14.77，145 型里 29 型正好 0.70（这个关卡的作者调参比全局更分散；全局 1,425 型是 70.7%）。

> 说明：`ORIGINAL_TERRAIN_AND_LAYOUT.md` §5.5 记的是 **90 / 184**。本文重算旧门得 **91**，差 1 件是那次统计的口径差异（本文严格用 `a·b·c ≤ (π/6)·D³/10` 的浮点比较，D=0.222）；「原作 184」两边完全一致，所以不影响结论。

## 3. `story_01_bench_size`（第一关）：改前 / 改后

我们的关卡没有声明 `defs[].volumeK`，所以全部按 0.70 计（`defs` 里不会写出 0.70，文件保持无该键）。

| 球径 D (m) | 旧门可吸 | 新门可吸 |
|---|---|---|
| 0.30（起始 0.29974 实际是 299 mm） | 87 | **96** |
| 0.50 | 106 | 109 |
| 0.80 | 114 | 119 |
| 1.10 | 121 | 124 |
| 1.50 | 125 | 127 |
| 2.20 | 130 | 130 |

方向符合预期：**同一球径能吸的更多**，且入口档（0.30）涨得最多（87→96），因为小件的 `Req` 对 k 与整 mm 最敏感。

---

## 4. 对其它既有结论的影响（要连带读的地方）

1. **`placement.json` 的派生字段会变**：`absorbDiameterReq`/`absorbable` 是 `canonical_doc` 重算的，所以第一关的 JSON 里这些行按新门重写（已重生成 `Docs/Design/Levels/story_01_bench_size/placement.json` 与 `Tools/levelbuild/examples/placement.json`）。`defs[].volumeK` **不会**被写进去——缺省就是 0.70，写了反而破坏「既有文件字节不变」的承诺。
2. **`_ref_*` 是只读参考**：它们在本机、不进 git；其派生字段仍是旧门的，`levelbuild check` 会对它们报 stale。重建参考关时应重新 canonicalize；本文**没有**改这些文件。
3. **供给比（G11/G12）更紧了**：同样内容的 S 是 `k·bbox` 口径，`story_01` 的供给比从 0.455 掉到 **0.319**（`_ref_mas1` 2.41× 不变）。这不是回归，而是口径修正后的真实差距——「我们内容不够」这条 owner 底线**没有**被这次改动掩盖。
4. **成长侧仍是 1.0**：门用 `k·bbox`，成长（`ΔV = V_box`）还没乘 k，也没采纳 0.8 的回收惩罚。**同一个物件在门与成长里得到同一个体积**是步骤 C/D 的验收项（`PARITY_AUDIT` §5 的迁移顺序），本文**故意不动**。
5. **离线 Python 模型（`Docs/Design/PickupAbsorb/pickup_absorb.py`、`round_model.py`）仍是旧口径**：它们是 DRAFT 参考，不在本文范围；门的事实来源是运行时 + `placement_core`。

---

## 5. 复现方法

```bash
# 数据门（与运行时同式）
python3 - <<'PY'
import json, math, sys; sys.path.insert(0, '.')
from Tools.levelbuild import placement_core as core
doc = json.load(open('Docs/Design/Levels/_ref_cancer/placement.json'))
orig = json.load(open('Docs/Design/Levels/_ref_cancer/original_defs.json'))
gate = {e['id']: e for e in orig['defs']}
for d in (0.222, 0.30, 0.50, 0.80, 1.10, 1.50, 2.20):
    ball_mm = core.floor_mm(d)
    new = sum(1 for p in doc['props']
              if core.absorb_diameter_req_mm(p['aabb'],
                     gate[p['def']]['gateVolumeM3'] / gate[p['def']]['bboxVolumeM3']) <= ball_mm)
    old = sum(1 for p in doc['props']
              if p['aabb'][0]*p['aabb'][1]*p['aabb'][2] <= min(512.0, math.pi/6*d**3/10))
    print(d, old, new)
PY
```

## 6. 操作者检查单（预期场景/资产 diff）

没有改任何 `.unity`；下面是要 Unity 侧重跑导入器/同步后才能得到、也应由操作者核对的 diff。

1. **`PickupFieldHost` 多一列 `propVolumeK`（`float[]`）**，在 `StoryMode.unity` 里表现为
   `propLocalBounds` 与 `valueUnits` 之间的一段 YAML 块，长度 = 行数（本关 130 行）：

   ```yaml
     propVolumeK:
     - 0.7
     - 0.7
     ...            # 130 行，每行 `  - 0.7`（缺省 k；某件若声明了别的 k，那行就是那个数）
   ```

   位置按 C# 字段声明顺序（`propVolumeK` 声明在 `valueUnits` 之前）。**不是 hex 块**：
   我核对过 `Labs/PhysicsTestField/PhysicsTestField.unity` 里已有的 `[SerializeField] float[] presetDiametersM`
   是 `- 0.3` / `- 0.6` / … 这种标量序列（`long[]` 的 `valueUnits` 才是 hex 块，每元素 16 个 hex 字符）。
   量级：130 行 × 8 字节（`"  - 0.7\n"`）≈ 1,040 字节 + 键名一行。
2. **U# 程序资产**：`Assets/SerializedUdonPrograms/` 下 `PickupFieldHost` 的程序 asset 会因
   字段表变化重编译（签名/dirty 噪声），`CopyProxyToUdon` 后 `UdonBehaviour` 的 public
   variables 也会多一项。这是生成物，审 diff 时按批忽略。
3. **重跑命令**：Unity 菜单里按 `VRC Katamari/Level Build/Import ...`（导入器会自动调
   `PickupFieldSync.SyncFromChildren(..., declaredGrowthMm3, declaredVolumeK, declaredVolumePenalty, declaredProxies, createMissingColliders)`），
   控制台应出现同步自己的汇总（steps C+D 后含 `growthMm3: written=…, fromFile=…, fromBounds=…, read-back: verified`）
   以及 "pickup sync wrote N row(s) into 8 base columns …, including propGrowthMm3 … and propVolumeK …"。
   关卡里没人声明 k 时这一行会说 "0 row(s) declared a k, the rest ... 0.70 default"。
4. 若某 def 的 k 越界：导入器报 **error**（`defs[i] ... volumeK ... must be <= 32`），
   sync 报 **reject**（`declared volumeK ... is outside the legal range`），不写该行。

## 7. 没有验证的

- **没有编译、没有跑 Unity/NUnit**：本文与全部 C#、场景序列化说明都基于源码与数据推演。`Unity 2022.3 / U#` 的实际编译与 EditMode 结果由操作者跑 `run_tests` 确认。
- **没有改场景**：`StoryMode.unity` 的 `PickupFieldHost` 需要多一列 `propVolumeK`（见操作者检查单），这要由导入器/sync 写盘，不碰 `.unity`。
- **没有跑 `_ref_cancer` 的旧/新门在 Unity 里**：第 2、3 节是 Python 数据层实算（与运行时用同一公式、同一 ε），不是引擎读数。
