# 引擎级差分审计：把一关原作数据灌进我们的引擎

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-16（项目时区）。本文**没有**运行 Unity、没有编译、没有 Play Mode、没有跑 NUnit。
> 写范围：本文件、`Docs/Design/Parity/parity_harness/*.py`、`Docs/tests/parity/*.py`。**没有**改 Runtime / Editor / Tools / 其它 Docs，没有 git 写操作，没有 `.meta`。
> 两个语料 `parity_harness/corpus_star1.json` / `corpus_cancer.json` **不入库**（`.gitignore` 已排除）：它们是商业游戏数据挖掘的导出，只在本机由 `parity_harness/build_corpus.py` 从 `/home/fulutang/KatamariRef/meta` 重新生成。干净检出里没有它们，测试会自动 skip（见 §6.1）。
> 口径：`Runtime/…`、`Editor/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…` 相对仓库根。符号名才是契约，行号会漂。
> 本文的原作侧数字**全部**来自公开数据挖掘与公开源码（`/home/fulutang/KatamariRef/meta/`、`/tmp/orig_physics/`），不是实机测量；我方侧数字来自仓库里的参数与 `Docs/` 下的 Python 参考模型。凡两边都没有数的地方写 `UNKNOWN` 并留空，不编。
> **体积表示决定（2026-11-16）：** 球与物件的体积改为定点 `long volumeMm3`（1 LSB = 1 mm³ = 1e-9 m³），迁移顺序 A（加宽 int→long，量子不变，黄金值不动）→ B（`valueUnits` → `long`，重导入场景）→ C（换量子 `volumeMm3`，删 `c` / `baselineVolumeUnits`）→ D（恢复原作数值形式）。见 [OPEN_DECISIONS (r)](../../OPEN_DECISIONS.md) 与 [定点体积影响审计](../../Research/ContentVolume/FIXED_POINT_VOLUME_IMPACT.md)；下表「体积口径」行与新增的五条体积分歧是它的落地清单。**未在 Unity 验证**：Udon `long[]`、`JsonUtility` / `SerializedProperty` 的 `long`、重导入后的场景 diff 大小。

---

## 原作优先原则（Original-first principle）

**原则**：有些数据结构和行为，应当**优先**还原成原作的样子。当我们发现自己的版本不支持某类物件/行为，而解包出来的原作明确是以某种方式做的，就把这当作「**我们的设计可能错了**」的信号——考虑还原原作的结构与处理方式，而不是绕过它打补丁。

**流程**：

- **（a）** 一发现分歧就加一行，六列齐全：区域、原作行为（**带数据来源路径**）、我方、保留我方的理由（没有理由就写 `none -> restore`）、决定、owner。行是活的：决定改了改同一行，不另起一行。
- **（b）** **默认决定是 `restore`**，除非写下理由，且只认四类：VRChat/Udon 限制、性能预算、授权、owner 的刻意设计选择。没写理由的一律标 `none -> restore`。
- **（c）** 已知分歧已全部入表（下 34 行）。owner 已拍板的按拍板写；未拍板的写 `undecided`，**不要替 owner 定**。
- **（d）** **未授权的参考资产永不入库**：表里只许写结构、数字、id 和文件路径；不许贴原作资产、模型、贴图或语料（`corpus_*.json` 已在 `.gitignore`，见 §6.1）。

| 区域 area | 原作行为（数据来源路径） | 我方 | 保留我方的理由（或 `none -> restore`） | 决定 | owner |
|---|---|---|---|---|---|
| 体积定义（`k` 系数） | `Volume = k · bbox`，默认 `k = 0.70`；薄片件 8–16、蓬松件 0.05–0.07、镂空家具 0.3–0.5；门与成长用同一个体积（`/home/fulutang/KatamariRef/meta/` 的 `raw_growth_mechanism.txt` §2、`solid_volume/README.md`、`DATAMODEL_NOTES.md` §3） | **门与成长都用** `k·a·b·c`：门在 `PickupAbsorbMath`，成长 = `round(penalty·k·a·b·c·1e9)` mm³，逐件 k/penalty 来自 `defs[]/props[].volumeK/volumePenalty`（缺省 0.70/0.80），在导入/同步时算一次写进 `PickupFieldHost.propGrowthMm3` | **已还原**（成长与门同源，差一个 0.8 回收因子） | restore（2026-11-16 C+D 完成） | owner 2026-11-15（成长 2026-11-16） |
| 体积口径 | 连续浮点体积；门槛按 mm 取整（`Req Pickup Size` 整 mm，同上来源） | **已还原**：定点 `long` mm³（1 LSB = 1 mm³），`c` 与 `baselineVolumeUnits` 已删；球 `V=start+Σgain`、`D=(6V/π)^(1/3)`、显示 +0.02 cm；门槛/球径取整整 mm（`SizeModelMath`、`PickupAbsorbMath`、`placement_core`） | **已还原**（`baselineVolumeUnits`/`cubicMetersPerVolumeUnit` 退役，见 `FIXED_POINT_STEP_CD.md`） | restore（2026-11-16 C 完成） | owner 2026-11-15（D5；2026-11-16 确认） |
| 单件回收比例 | `ΔV = penalty · V_item`，默认 `penalty = 0.8`（1198/1436 型），另有 0.01–1.0 逐件值（`collectible objects` 表、`raw_growth_mechanism.txt` §3） | **已还原**：`ΔV = round(0.8 · V_item)`，逐件 `volumePenalty` 可覆盖（`PickupAbsorbMath.TryComputeGrowthMm3` / `placement_core.item_growth_mm3`，导入时定价一次） | **已还原**（缺省 0.8；按球径分段曲线仍未落，见下一行） | restore（2026-11-16 D 完成，缺省值） | owner 2026-11-15（D2） |
| 逐件回收惩罚（`pickupVolumePenalty`） | 逐件 penalty 0.01–1.0，默认 0.8（1198/1436 型）（`meta/collectible objects`、`meta/raw_growth_mechanism.txt` §3） | **已有逐件覆盖字段**：`defs[].volumePenalty` / `props[].volumePenalty`（缺省不写，0.80），导入时乘进 `propGrowthMm3`；范围 `0 < p ≤ 1` 越界即拒（`PickupFieldAuthoringRules.VolumePenaltyError`） | **已还原**（字段 + 缺省值；原作逐件数值未填） | restore（2026-11-16 D 完成） | owner 2026-11-16 |
| 门槛取整 | 门槛直径向下取整到 mm（同上） | **已一致**：`Req` 与球径都取整到整 mm，ε=1e-3 mm 只修浮点（`PickupAbsorbMath.TryFloorMillimetres`、`placement_core.absorb_diameter_req_mm`） | 已还原（§5.1 `G08`） | restore（2026-11-16 完成） | owner 2026-11-15（D2） |
| HUD 直径偏置 | 显示直径 = `(6V/π)^(1/3)` **再平加 +0.02 cm**（MAS1 的精确起始尺寸是 5.02 cm 而不是 5 cm）（`meta/raw_growth_mechanism.txt` Note to 1、`G05`/`G09`） | **已还原**：显示读数 = 语义直径 + 0.0002 m（`SizeModelMath.HudDiameterWorldM`、`KatamariBallMotor.GetHudDiameterWorldM`、`StoryRoundController` HUD）；门/尺寸模型用未偏置值 | **已还原**（偏置只在显示层，不移动门槛） | restore（2026-11-16 D 完成） | owner |
| 10 倍体积门 | `πD³/6 ≥ 10·V`（`raw_growth_mechanism.txt` §2、`DATAMODEL_NOTES.md` §3） | 同一条（`PickupAbsorbMath.AbsorbVolumeRatio = 10`，常量） | 已一致，无分歧（`G07`） | keep | — |
| 定步频率 | 逻辑 30 Hz 定步 `DoTick(1/30f)`，画面另插值（`/tmp/orig_physics/reroll_datamodel.md` §0，KatamariMods FramerateUncap 的 IL） | 50 Hz（`fixedStepSeconds = 0.02`，`Docs/Design/BallMotor/README.md:295`） | 性能预算 + 现有接触/子步参数按 0.02 s 标定过；切换单独立项 | keep（owner 计划切到 30 Hz，先留 50 Hz） | owner 2026-11-16 |
| 每关分段回收曲线 | 按尺寸分段线性，按关给：MAS1 5 cm .8 → 15 cm .4 → 20 cm .16（`raw_growth_mechanism.txt` §3 的关卡曲线表） | 全局缺省 0.8 + 逐件覆盖；**没有按球径的分段函数** | `none -> restore`：缺每关曲线数值，owner 未给（`FIXED_POINT_STEP_CD.md` §8 明确列为后续） | restore（部分：缺省 0.8 已落，分段待数据） | owner 2026-11-15（D2） |
| 群组物件体积 | 拾取父件时**连带子件体积一起入账**（解释高侧离群点）（`meta/raw_growth_mechanism.txt` §4、`meta/DATAMODEL_NOTES.md`；`parentProp` 单父指针实测 **1,970 条子件 / 1,082 个父件**，口径见 `PROP_BEHAVIOURS.md` §10.4，不是旧写的「38 件群组」） | `group` / `groupId` 只存不读，没有价值聚合（`PickupFieldHost`、`placement_core` 的 `PROP_KEYS`） | `none -> restore`（§10 R9：「移动件/行为字段」行讲的是 `parentProp` 行为字段，不是体积规则） | restore（**延后到 v2**：owner 2026-11-16 拍板 v1 子件坠落、不带，见下「支撑图（`rests_on`）」行与 `OPEN_DECISIONS` (s)） | owner 2026-11-16 |
| 交分缩径 | 增长侧数据只见增不减（`meta/raw_growth_mechanism.txt` §3）；兑换区的缩径在原作研究里只确认「球会变小」、比例与最小兑换体积未公开（C2/C4/C20） | `post = max(start, start + (pre − start) × residual)`，`residual = 0.25`；交分后残量 `carried` 记入且不再计分（`BankShrinkMath`、`round_model.post_bank_diameter`） | §10 R10 判「缺行 + 缺理由」；这里的理由是 owner 2026-11-14 的刻意设计（**not** a parity restore），`0.25` 与 carried 语义都是 PROVISIONAL | keep（PROVISIONAL，待 T21/T39 复核） | owner 2026-11-14（(a)/(b)） |
| 每玩家账本 | 原作**单机**，没有 per-player 账本（§10 R13） | 每玩家 `confirmed` / `banked` + `(spawnId, generation)` 幂等键（`GrowthLedgerMath`、[Ledger README](../Ledger/README.md)） | 多人是本项目范围（VRChat 世界必须多端可重建账本）；原作单机没有这个需求，属**范围差异**而非行为偏离（§10 R13 的「需补行」） | keep | —（既有技术契约：02_TECHNICAL_CONTRACTS / Ledger README） |
| 每关起始/目标球径 | 逐关不同：MAS1 5 → 10 cm、Cancer 22.2 → 80 cm、第三星 10 cm（`missions` 表、`/tmp/orig_physics/dump.csv`、`DATAMODEL_NOTES.md` §1） | 一套 0.30 → 1.25 m（`placement.json` / `story_01_bench_size.json`），运行时尚未从关卡 JSON 读（§5.3.2 步骤 1） | `none -> restore` | restore | owner 2026-11-15（D6） |
| 球径上限（全局 vs 每关） | 上限也是每关参数（MAS1 30 cm，同上来源） | **当前实现 2.20 m**（`KatamariBallMotor.maxCoreRadiusWorldM` = 1.1，半径制）；**owner 2026-11-16 把全局上限定为 20 m，尚未改代码**。视觉核心跟随当前碰撞直径，不按上限换皮肤 | 设计：先留一个全局上限，正常游玩很少摸到，便于按手感统一调 | keep（目标 20 m，实现待改，按玩测复核） | owner 2026-11-16 |
| 视觉核心 | REROLL [玩家观察](https://steamcommunity.com/app/848350/discussions/0/3561682880003061934/)：“the 'core' grows and absorbs all the objects”；Takahashi 2004 postmortem 确认镜头随球变大自动拉远。证据强度：玩家观察 + 开发者镜头说法，非核/碰撞比例实测。专利 Mp/Mh/Mr 分离不证明核固定；core_XX 仍按关卡命名 | `BallView` 读当前碰撞半径 ×2，变化即写；`CoreSkinAssembler` 保留等体积径归一化子节点，先用 core_01；Unity 验证待做 | 消除粘附件与小核之间的空腔；参考资产仅本机占位，不进 git/上传 | restore（owner 2026-11-17）：核显示等体积直径 = 当前碰撞直径，不用弹簧；[装配与证据](../BallPresentation/CORE_SKIN.md) | owner 2026-11-17 |
| 球核心视觉（生长弹簧） | 逻辑 30 Hz、画面另插值，没有公开的「生长弹簧」参数（`/tmp/orig_physics/reroll_datamodel.md` §0、`DATAMODEL_NOTES.md`）；核随碰撞直径更新（见「视觉核心」行） | 核心直径直接跟随碰撞直径；生长弹簧与 0.95 缩放路径已删除（`BallView`） | `none -> remove from play`（owner 2026-11-16：开了仍读作「跳一下」，真因是价值定价而非缺平滑；见 `VISUAL_GROWTH_AUDIT.md` 顶部记录与 [WHY_OUR_GROWTH_IS_BIG.md](../../Research/ContentVolume/WHY_OUR_GROWTH_IS_BIG.md)） | remove (owner 2026-11-16: still reads as a jump; real cause is value pricing) | owner 2026-11-16 |
| 骑手/座位尺度 | 小型王子骑在球后滚球，第三人称相机（`meta/DATAMODEL_NOTES.md` 的 `missions.CAMERA_FOV` / `initPos`；[研究核查](../../Research/KatamariBallRules/README.md) C3 王子シップ） | 玩家 avatar 经 `VRCStation` 就座，第一人称眼点在球后上方（`FollowRigMath`、`SeatEyeCompensation`）；**avatar 眼高按球径线性跟随，比例取决于入座时实测的 κ（视点到 Hips 的竖直距离 ÷ 眼高）**，坐姿腰部（Hips）与球顶同高，座位浮在球后上方；读不到 Hips 时只对齐眼睛（[RIDE_GEOMETRY](../BallLayers/FollowRig/RIDE_GEOMETRY.md) §3） | VR 第一人称、没有第三人称追尾相机：骑手随球一起缩放（owner 2026-11-17 的决定，不是从原作学来的），代价是旁观者看到巨人骑大球；Hips 在 Station 里的读数、均匀缩放假设与 VR 全身追踪下的表现未在真机核对 | keep, owner decided 2026-11-17（球小人小、球大人大；腰与球顶同高，腰按实测对齐，退路只对齐眼睛） | owner 2026-11-17 |
| 吸附物位姿 | 接触位置原地粘住，保持碰撞瞬间姿态，不缩放、不外推、不拉入（`[专利原文]` US7402104B2 + `[录像实测]` PS2 美版，[`ORIGINAL_ATTACH_LOOK.md`](../../Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md) a1–a5） | 自身盒触碰物理等效球；在成功提交那一步、成长推出之前转球局部位姿写入事件环；宿主只消费冻结位姿，不用延迟后的球心/滚转重算（`AttachmentMath` / `PickupFieldHost`）。R33 残留主因与修复见 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) §1 | 接触时刻语义已还原；球侧 Mh 形状取舍见“吸收触发的几何”行，未用拉入补偿 | restore（触发即接触、原地冻结、不加嵌入偏移；待 Unity 复验） | Lead 2026-11-17 |
| 区域化尺寸分布 | 每区一套分布：第二区起不再放新小件、小件集中中央（MAS4 60→12→5.5→0%、Cancer ρ=+0.354；`zone_stats`、`/tmp/orig_physics/sheets/`、`reroll_numbers.md` §2） | 只有一个尺寸带、没有区（`C08`/`C10`/`C11`） | `none -> restore` | restore | owner |
| 移动件/行为字段 | 每型带 `pathSpeedIndex`（**11 个取值**，`2` 为缺省 80.2%，余下 0/1/3–10 共 338 型，另 1 个坏值 `t`）、vault 点（0/2/3/8 个：805/261/650/1 型）、逐实例 `playAnimNo`（4 个取值，9.7% 实例带动画）、`parentProp` 单父指针（**1,970 条子件 / 1,082 个父件** / 单父最多 30 子件 / 链最深 **13 层**，如 Make Cygnus 的 13 蛋塔）（`raw_reroll_dump__name_props_0x30.tsv`、`raw_reroll_dump__object_dump.tsv`、`DATAMODEL_NOTES.md`；实测计数见 `PROP_BEHAVIOURS.md` §10.1 表 1/表 2） | **数据已带**：`_ref_cancer/placement.v2.json` 511 行 `behaviour`（static 383 / motion 108 / attached 15 / ambient 5）、113 行 `motion`、83 行 `parent` + `inherit:"full"`、`motions[]` 14 条、`paths[]` 4 条；数学库 `Runtime/Pickup/MotionMath.cs` 已写，关卡编辑器与 `Tools/motion_sim` 能播放。**Unity 无运行时宿主**：`MotionHost` 不存在（`PlacementDocumentV2.cs`「no `MotionHost` yet (P4)」），运行时一件都不动 | 性能预算：行为件按 15 Hz 分摊 ≤800 movers（`PROP_BEHAVIOURS.md` §7） | restore（数据与库已落，宿主待 P4/P5） | owner |
| 骨骼动画的呈现（Animator 数量） | 153 个实例带骨骼蒙皮，逐实例播（19 型、每型 4–6 段剪辑，`ORIGINAL_ANIMATION_OBSERVATIONS` §3/§4/§5.4） | **设计是照原作挂满**（153 个实例各挂普通 Animator + 蒙皮，不做 VAT、不按 64 截断），实现分两段：运行时 `MotionHost` 已含 Animator 驱动（相位起播、`Moving`/`Panic`、剔除时关组件），编辑器 `SkeletalAnimationAssembler` 已写（**均待 Unity 验证**）；`Packages/manifest.json` 仍无 glTF 导入（走 FBX，不需要）。仓库外已能导出 3 种螃蟹（120/153 实例）的带骨骼 GLB（`~/KatamariRef/props/skinned/type_{480,481,482}.glb`，5 骨/5 部件/5 剪辑，`ORIGINAL_SKELETAL_ANIMATION` §11），进 Unity 的路线是 GLB → Blender → FBX | owner 的刻意设计选择：参考关卡是 2018 年的 REROLL 复刻（PS2 时代原作），性能要求本身极低，**现阶段不预先优化**；VAT 与「按球距截断」降级为「以后真测出瓶颈再看」 | restore（2026-11-16 重写 D-11；后续真实测量照旧） | owner 2026-11-16 |
| `WaitForPlayer` 触发门 | 原作 `panicRadii[sizeGroup]`：7 件（`moveType 7`/`motionID 0x00`）本关全是 `size=2` ⇒ **200 cm**；另 8 件（`moveType 10`/`0x04`）条件是 `inZone && parentRolled`，参考实现写死 false、原作真实条件未知（M2）（`ORIGINAL_MOTION_PROGRAM` §4.5、§12.11、§14） | **只有** `wake_radius_m = 12` 的距离剔除，**没有**触发门（`PROP_BEHAVIOURS` §7.2/§7.3） | 两个量语义不同：12 决定「算不算在动」，原作 2–3 m 决定「怕不怕你」；拿 12 当触发门会让球在 12 m 外就把蟹惊跑（覆盖 452 m² vs 12.6/28 m²），把剔除压到 2–3 m 会让院子里 128 件全冻、远处蟹全静止 | separate（12 不动；新增 `triggerRadiusM` 2.0/3.0，原作五档 2/3/5/7/9 m；`0x04` 的 8 件测量前保持静止） | lead 2026-11-16 |
| `Dummy Hit` 命中判定体 / `Lalala` | 524 条 spawn 里 12 条是 `Dummy Hit 01/02/03`（无网格命中判定体）+ 1 条 `Lalala`（精灵道具，社区模型包无网格），**不是可收集道具**；511 件道具是剩下的部分（`ORIGINAL_MOTION_PROGRAM` §11.3） | 511 件道具；不建这些判定体 | 原作用途未测（是否参与碰撞/吸附判定未知） | keep（v1 不建；实机测量后再定是否需要） | lead 2026-11-16 |
| 静态固体的接触形状（`structures.solids`） | 原作的静态碰撞**跟模型形状**：参考关卡 35 条 scenery 固体（门 > 1.2 m、整件外壳），其中 **18 个是凸包**、17 个能当 box（`Docs/Design/Levels/_ref_cancer/placement.v2.json`、`REF_STAGE_VIEW.md`） | 导入的接触形状已经是**拟合代理**（`shape:"proxy"`，`proxySource` 记 `def`/`aabb-fallback`/`manual`），但**电机的静态表只认 OBB**，所以那 18 个凸包今天被「装箱成 OBB」 | 真凸包/球/胶囊需要一条新的**静态非 OBB 接触通道**（另需一条「只支撑不阻挡」的静态通道）；OBB 装箱是近似，不是原作的形状。导入器**没有静默降级**：逐件打 note + 汇总一行 | acknowledged（待新通道；不是 v1 阻塞） | lead 2026-11-16 |
| 支撑图（`rests_on`） | **堆叠逻辑无公开证据**：专利也不讲堆叠，官方图鉴只有反证（`PROP_BEHAVIOURS.md` §5.1）；但原作数据侧有**单父指针** `parentProp.monoControlIndex`（1,970 条子件 / 1,082 个父件 / 链深 13 层，§10.4）——那就是原作表达「组」的唯一形式 | `support`/`stackedOn` 只喂校验器；运行时 `PropFallHost`（单父指针 + 复检 + 坠落）已写，待 Unity 验证与场景接线 | 原作**逻辑**未知（拿得到数据、拿不到逻辑，§10.4/P18）；按原作的父指针形状做，不做支撑计数 | restore（**用原作的父指针形式**；owner 2026-11-16 确认「子件坠落、不带」，v1 设计见 `PROP_BEHAVIOURS.md` §10.9 / `OPEN_DECISIONS` (s)） | owner 2026-11-16 |
| 推墙爬升与台阶阈值 | 推着墙滚就能沿竖墙**连续**上升、**有高度上限**、超过上限**掉落**、上限之下能翻上墙顶；不加键（REROLL 社区原帖、We Love 手册转述、主人一手手感，见 `ORIGINAL_CLIMB_OBSERVATIONS` §2 的 S1/S6 与 `TERRAIN_CLIMB.md` §1/§4）。**上限倍数、爬升速度、到顶秒数三样都没有任何来源**（`ORIGINAL_CLIMB_OBSERVATIONS` §3/§5，「1–2×」「约 3 s」是续作/新作社区说法且帖内自相矛盾）；蟹座「塀から登れる高台」见同文 S17 | **已提交（HEAD `9e782fb`）没有任何代码路径能爬墙**：v1 只有 `0.25·D` 的支撑带（`SupportSurfaceMath.IsStepWithinLimit`）+ T1 法线推出，竖直踢面只把球推离墙、不抬球；`ContactTuning.KHeight = 1`，v2 的爬升通道拿不到地形接触且 `contactModelV2Enabled` 默认 false。实测（r20）D=0.222 m ⇒ 台阶上限 0.0555 m，爬不出出生处 0.19 m 凹槽（只能滑到边沿掉下台面）。**工作区（未提交、未验收）**：`KHeight=1.5` + `ClimbLedgeSlopM/ClimbPeelSpeedMps`，`StepContactV2` 已把地形候选与 `contactTopY` 传进 `ContactSolveMath.TryStep`，`ClimbMath` 改动，新增 `TerrainClimbTests.cs`；没编译验收、没跑过 Unity | `none -> restore`：台阶阈值不是参数没调好，是 T2 没接（`TERRAIN_CLIMB.md` §2.4）；`0.25·D` 降为 `h<r` 特例与诊断 | restore（设计已批准：`k_h=1.5`、`T_climb=2.8125 s`（所有者 2026-11-17 试玩后提速 60%），两者均 PROVISIONAL，实现进行中、未验收） | lead 2026-11-16 |
| 撞墙掉已吸附道具 | 高速撞到卷不动的东西或**墙** ⇒ 块会崩、变小（Bandai PS2 作品页，官方，定性；`ORIGINAL_CLIMB_OBSERVATIONS` §7 附注）；碰墙即算碰撞、连**尝试爬墙**都会掉吸附物（REROLL 社区 S3/S4，D 级） | 碰撞只做推出 + 去掉墙内速度分量（`ResolveTerrainPenetration`、v2 接触推挤），**没有任何「件脱落」通道**：吸附物只有 attach/hidden 两态 | `none -> restore`：冲击门与掉落量都无来源，需要一次独立设计（「撞墙掉落」），不是调参 | undecided（待认领；`TERRAIN_CLIMB.md` §10 记录在案） | lead 2026-11-16 |
| 梯子 / 绳索类表面 | 墙上有梯子、绳或台阶时可以**无限爬**（多来源一致：REROLL S2、TV Tropes S5、OUAK 帖 S13、日文攻略 S15/S16，见 `ORIGINAL_CLIMB_OBSERVATIONS` §3）；共识读法是「普通墙封顶、梯子例外」 | 地形数据里**没有「可爬/无限爬」这类标记**，`terrain` 只有三角面 + chunk + zone，没有任何 surface 类别位；也没有对应机制 | 无数据表达方式：原作没把「哪面是梯子」写成我们能读的字段，造一个新类别等于编原作规则 | keep（v1 不做；要做需先加地形类别，`TERRAIN_CLIMB.md` §10） | lead 2026-11-16 |
| 吸收触发的几何（接触） | 专利 US7402104B2 的碰撞模型 `Mh` 是包围盒、内接等效体积球；**盒的实形/朝向未知**（[`CONTACT_MODEL_V2.md`](../BallMotor/CONTACT_MODEL_V2.md) §4A.1/§4A.4）。凸出物属于滚动模型 `Mr`，不能据此推断 Mh 跟针一起长 | 球侧采用**内接等效球**：道具自身有向盒到球心距离 ≤ `collisionRadiusWorldM + contactRadius(0)`，v1/v2 同一门；成功提交时冻结接触局部位姿（`PickupAbsorbMath.TryComputeContact` / `AttachmentMath.TryCaptureContactPose`） | 有意工程取舍：立方体支撑半径角向可达 1.73r，使吸附物悬在球外约 7–15 cm 的量级；R33 的可量化 36 对日志半径项解释 97% 总 gap 差，所有者两次反馈“粘不好”。原作 Mh 实形无实证，不能用臆测的盒角宽松范围覆盖已定贴球目标 | **keep（采用球）**；取得原作 Mh 实形/朝向证据后再复议。接触处冻结、不加嵌入偏移；详见 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) §1/§3.1；待 Unity 复验 | owner 2026-11-17 |
| 地形接触的每步成本（平台代价） | 原作无此问题：物理在原生 DLL、30 Hz 定步、粗碰撞 + 掉出世界救回（`/tmp/orig_physics/reroll_numbers.md` §4） | Udon 里逐三角形测试。**无网格索引时**（r21）：每步 `tq` 12–24 ms、`triTests` 4,080–8,160、单帧（×5 步）96–119 ms。**加网格索引后**（r24，同步的是工作区快照、当时 v1 是权威）：1 m / 1.5 m 格被拒（`code 3`，大三角形跨格超限），2 m 格接受（`grid=80`）——稳态 `cands=114`、`tqSup` 4.1–6.3 ms、`triTests 1938`；推进中 `tq` 3.5–5.1 ms；**最坏处 `cands=443` → `tq` 14.3 ms，比全扫描（11.4 ms / 4,080 triTests）还慢**。每 triTest ≈1.8–2.1 µs，收益线性于三角形数，没到 ~1 ms；配套 EditMode 6 条网格测试红未修 | VRChat/Udon 限制 + 性能预算：这是**我们平台的代价**，不是还原项，不该对着原作调；50 Hz 下接触求解 >1 ms 是细分大三角形的触发条件 | keep（代价记录；未达标，待继续提速） | lead 2026-11-16 |
| 参考模型导入尺度 | 道具 `aabb` 单位是米（解包 dump）；模型侧约定根矩阵 `diag(0.01,…)`＝cm→m（`ORIGINAL_SKELETAL_ANIMATION.md` §11） | 旧导入把 fileScale 应用了**两次**，且预置体根节点的 ×100 被导入器丢掉 ⇒ 场景道具恒小 100×（`fiddler_crab_008` 0.0016 m vs 文档 0.1527 m）。逐个 `SaveAndReimport` + 按残差补 `globalScale` 后 **143/145** 落在 0.95–1.05；剩 `eggplant`(0.153/0.166/0.259)、`sparrow`(0.167/0.249/0.067) 两个非等比未硬调（`/tmp/r22_notes.md`）。副作用已记：预置体（根×100）现在普遍 100× 大，导入器不使用预置体比例 | —（纯导入口径错误，不是设计取舍） | restore（已修；两件非等比待定） | lead 2026-11-16 |
| 操作与相机 | **双摇杆坦克式**（两杆前推＝前进）+ **追尾第三人称**，垂直 FOV **37°**（`missions.CAMERA_FOV`；双摇杆 S7，D 级） | `VRCStation` 座位 + 第一人称眼点（`0.75+2.2r` 后退、`0.55+2.4r` 抬高），桌面键鼠/VR，FOV 60°。**第一人称 VR 是本项目的有意选择**：`OPEN_DECISIONS` (k) 把「连续移动玩家 avatar 只能靠 station/传送」记成硬约束，但**这个平台断言没有官方文档依据**，本表按「未核实的理由」引用——即使别处有做法，换成追尾相机也是产品决定。座位与眼点几何见「骑手/座位尺度」行，本行只管输入方式与镜头 | 产品选择（VR 第一人称 + 桌面键鼠）；平台理由（station/传送）未核实 | keep（有意偏离，owner 已确认） | owner 2026-11-16 |
| 球的滚动形状（径向代理/针尖） | 滚动模型 `Mr` = **16 根固定针 + 4 可选**，针尖是接地点；视觉核心跟随碰撞直径、core_XX 是按关卡命名的皮肤（[本机核心研究 E2/E3/E5](</tmp/core_radius_findings.md#L59-L62>)；专利 US7402104B2 图 4A/4C；主人手感「大多数时候是小棍撑出来的滚动体在撑着滚」） | 16 向针表（`proxySupports[16]`）现在收**全部**吸附物的盒角点（增量 max，cap `min(0.35r, 0.30 m)` PROVISIONAL）；v1 把针尖喂接地求解，v2 的针尖扫掠段接触（`BallShapeMath`、`ContactSolveMath`）读同一张表（`CompactNeedleCap`=16）；两个场景 v2 权威默认仍关闭 ⇒ 墙面/台阶上的针尖接触要等 v2 打开才生效，接地抬升 v1 已生效；未经 Unity 验证 | 成本：针尖全参与最坏 +18k flops/步（§4A.3，16 根针）；先在 v2 打开时生效，不为它单独开一条通道 | restore（v2 落地即生效；门宽、`H_cap` 加成随之改变） | lead 2026-11-16 |
| 关卡的渲染网格 vs 碰撞网格 | 两套独立几何：4 个图形扇区（UV、材质、贴图；Cancer 3 个有效，7 个模型、14,780 三角形、158 张贴图，院内最高 3.63 m）与 4 个碰撞扇区（仅 AABB + 三角形，含一圈 15 m 不可见挡球墙）；渲染只用图形扇区，碰撞从不绘制（noclip `scenes.ts` 只推 `areaCollision`，`render.ts` 无碰撞绘制；`REF_STAGE_VIEW.md` §10） | 接触（球读 `TerrainFieldHost` 数组、玩家站立 `MeshCollider`）= 碰撞网格，与原作同构；**渲染 = `terrain.visual`**（`out/stage.glb` → `.katvisual` + PNG，Unity 拆成 `TerrainVisual/<part>` 渲染与不渲染的 `TerrainCollision`，编辑器画视觉 GLB + 可开关碰撞线框）。此前把碰撞网格当视觉画（R21 三色网格 = 红墙）已移除。已知偏差：材质用 Legacy Diffuse 系（原作逐顶点光照）、`filter=point`（原作放大最近邻/缩小线性）；**未经 Unity 验证** | 授权：贴图/网格是 UNLICENSED，只进 git 忽略的 `_RefUnlicensed/` 与 `_ref_*/` | restore（渲染改读原作视觉，接触保持原作碰撞）；光照/过滤 undecided（Unity 看一眼） | Lead 2026-11-17 |

> 细节不在这里重复：`k` 系数与三处同源见 §5.3.2；D1/D2 落地后哪几行会变见 §5.1；连续体积的 7 个受影响点见 §5.2；球芯显示、皮肤与归一化见 [CORE_SKIN](../BallPresentation/CORE_SKIN.md)；行为与支撑见 `PROP_BEHAVIOURS.md`。本表只负责「承认分歧 + 定方向」。

---

## 0. 一页结论

1. **可做的是「参数 / 内容 / 规则」的差分审计，不是「手感」审计。** 原作的运动常数（速度、加速度、坡度、爬升、碰撞形状）不在任何公开源码里，它们在 `PS2KatamariSimulation.dll` 的机器码里；能一比一搬的是**关卡参数、物件位置、物件尺寸/体积、区域归属、类别**。
2. **已经能算出 47 条指标**（`run_parity.py`，可复现）：一致 4、参数差 20、缺机制 7、不同机制 5、可接受偏差 2、原作未知 9（数字随语料变化，见每次运行的合计行）。
3. **三个最大的 suspect gap**（详见 §3）：**供给不足**——`G11` 供给比原作 **1.65×**、我们 **0.46×**（<1 = 单靠本关内容走不到上限）；把原作真实布局跑进我们规则（`_ref_mas1`，648 件、5 cm 起）供给比 **2.42×**、最紧档仍有 2.4× 余量，而 story_01 在 1.2 m 档就掉到 0.57×；**内容密度差 5–6×**（每翻一倍直径 253 件 vs 44 件，内容体积/满径球 2.46× vs 0.45×）；**成长不守恒**（原作只回收 80% 并随尺寸降到 16%，我们 100%）。运行时还缺"行为/支撑/换档"三件套（`pathSpeedIndex`/vault 点/尺寸阈值表都没有消费者）。
4. **尺度是这个项目的隐藏主线，而且已经拍板**：原作第一关起始 5 cm，我们 30 cm，正好 6×。我们引擎里 7 个"米制常数"（skin 5 mm、叠放 10 mm、推挤 50 mm、突出 cap 300 mm、到顶 20 mm、掉走 10 mm、slop 0.5 mm）在 5 cm 球上会有 5 个超过球径的 10%，在 30 cm 球上只有 2 个。**owner 决定（D3，2026-11-15）：不乘 6，全案用真实尺寸**——于是每一个被 5 cm 球暴露出来的米制常数都是一条审计发现，逐条列在 §5.3，并归纳成一个可独立验收的主题（§5.4）。
5. **一条硬规则已经完全对上，但"体积"的定义差一个系数**：吸收门 = 10 倍体积（`πD³/6 ≥ 10V`），原作门槛向下取整到 mm。**更正（2026-11-15）**：此前"原作用实体体积、我们用包围盒"的说法**已被数据推翻**——原作的 `Volume` = **包围盒体积 × 作者手调的 k**，默认 `k = 0.70`（1425 个既有模型又有可信 Volume 的类型里 70.7% 精确等于 0.70，`bbox/Volume` 的 q25/中位/q75 全是 10/7）。所以我们的门与成长**缺的是 k 系数**（`G13`），不是"换实体体积"：默认 k=0.70 让我们在长度当量上偏严 `(1/0.70)^(1/3)=1.126`，与 gateRatio 中位 1.13 吻合；薄片件被作者抬到 k=8–16、蓬松件压到 0.05–0.07、镂空家具 0.3–0.5。
   **更新（2026-11-16，owner 决定：门按原作数字）**：门已经改成 `V = k·a·b·c`、`Req = floor(1000·cbrt(60V/π))`、`floor(1000·D) ≥ Req`，`G07/G08/G13` 三条现在都是「一致」。实测见 [`ABSORB_GATE_ORIGINAL.md`](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)：`_ref_cancer` 0.222 m 球从 91 件变 **184** 件，与原作 184 件相同；145 个类型的 `Req` 比值中位/最小/最大都是 1.0000。**成长侧仍是 1.0 回收**（`G06`，步骤 C/D），所以门与成长对同一件还不同源。
6. 本文的指标表就是审计协议：任何人改了参数或参考模型，重跑 `run_parity.py` 就能看到哪些行变了。

---

## 0.5 术语（两个反复出现的词）

- **10 倍体积门**：球的体积 ≥ 物件体积的 **10 倍**才吸得动。等价写法 `D_ball ≥ 2.154 · d_eq`（`d_eq` 是体积等于物件的球直径）。原作与我们都用这一条（`G07` 一致）。
- **0.8 惩罚**：吸住之后，球只增加物件体积的 **80%**（剩下 20% 视为压实/损耗丢掉）。原作是 `ΔV = penalty × V_item`，默认 `penalty = 0.8`，另有 0.01–1.0 的逐件惩罚与每关随尺寸下降的分段曲线；我们现在是 `ΔV = V_item`（100%）。

## 1. 目的与诚实的边界

### 1.1 这个重建能证明什么、不能证明什么

**能证明（引擎级）**：

- 同一批内容（体积/尺寸/位置）在我们的门槛、成长、台阶、爬升规则下会得到什么结果——**可算、可复现、可进 CI**（`Docs/tests/parity`）。
- 哪些规则**形状不同**（体积门对得上、成长回收率对不上、换档机制不存在），以及差多少。
- 数据能不能被我们的校验器接受（`ref-level-builder` 正在做的 MAS1 → placement.json 试验，见 §6.3）。

**不能证明（诚实清单）**：

- **手感**：原作的加速度、转向、坡度响应、爬墙速度没有任何公开数值（`/tmp/orig_physics/reroll_numbers.md` §6.2）。我们能说的是"我们的值"，不是"原作的值"。
- **1:1 视觉**：物件缺旋转与缩放（§1.3），模型是 PS2 来源而数字是 REROLL 来源（`DATAMODEL_NOTES.md` §5.6）。
- **运行时等价**：原作物理在原生 DLL 里、30 Hz 定步；我们 50 Hz、Udon 脚本。即使规则一样，采样与浮点行为也不同。
- **观感/尺寸读数**：本文的"直径"一律指物理等效球直径，HUD 的 +0.02 cm 是另一个量（G09）。

### 1.2 已确证（本轮数据挖掘，可直接引用）

| # | 事实 | 值 / 结论 | 证据 | 级别 |
|---|---|---|---|---|
| F1 | 引擎与时基 | Unity 2018.1.x；`deltaFrame>=1/30` → `DoTick(1/30f)`，逻辑 **30 Hz** 定步，画面另插值；物理在原生 `PS2KatamariSimulation.dll` | `reroll_datamodel.md` §0（KatamariMods FramerateUncap 的 IL） | 源码 |
| F2 | 关卡参数表 | 起始 / 目标 / 上限是**三件事**：MAS1 **5 → 10 → 30 cm**（倍数 2.0×），时限列 4，区域 1，实例 654，类型 107 | `missions` 表；`/tmp/orig_physics/dump.csv`；`DATAMODEL_NOTES.md` §1 | 数据挖掘 |
| F3 | 换档阈值 | `swirls` 表 MAS1 只有 **1 个值 7 cm**（单 area）。`reroll_numbers.md` §1.1 列的 "7/10/20/30/50/75/120" 与表冲突，**本文采信表**（见 §6.2 C1） | `swirls` 表 | 数据挖掘 |
| F4 | 10× 体积门 | `V_ball ≥ 10·V_item`，门槛直径**向下取整到 mm**；类型表 1,449/1,450 行 1 mm 内自洽。**`V_item` = k×bbox**（默认 0.70） | `raw_growth_mechanism.txt` §2；`DATAMODEL_NOTES.md` §3；`solid_volume/README.md` | 社区实测 + 数据挖掘 |
| F5 | 增长**不**守恒 | 拾取只回收 `penalty × V_item`（`V_item = k·bbox`）：逐件 `Pickup Volume Penalty` 默认 **0.8**（1198/1436），另有 0.5/0.6/0.4/0.01/0.66/0.26/0.08；关卡曲线另有一条随尺寸下降的分段线性表 | `collectible objects`；`raw_growth_mechanism.txt` §3；`solid_volume/README.md` | 数据挖掘 + 社区实测 |
| F6 | 内容体量 | 单关实例 523–3955；单关每 **0.1 m** 尺寸档 20–180 件（中位 ~90）；覆盖 0.30→1.25 m 需 **600–900** 个可吸实例 | `object dump` 28,698 行；`reroll_numbers.md` §2.2 | 数据挖掘 |
| F7 | 实例有真实 XYZ | 28,698 行中 **13,723** 行非零；MAS1 **652/654**；坐标是**米、area 局部**（MAS1 是一件 ~4×2.3×6 m 的屋子）；只有 `loading area 0` 有坐标；**没有旋转、没有缩放** | `object dump.position`；`DATAMODEL_NOTES.md` §0/§3 | 数据挖掘 |
| F8 | 碰撞与行为结构性事实 | 球有**碰撞网格**、物件有**包围盒 + vault 点**、每物件每 tick 一个矩阵；物件类型表带 `pathSpeedIndex`、`num vault pts`、`playAnimNo` | Practice Mod 功能表；`name_props`；`object dump` | 源码 + 数据挖掘 |
| F9 | 运动常数**不存在** | 速度上限、加速度、转向、坡度、攀爬阈值：公开源码里**一个数字都没有** | `reroll_numbers.md` §6.2 | — |
| F10 | 时限单位未证实 | `GAME_TIME` = 4（MAS1），值域 4–30，教程 90；按分钟读则 240 s，**未证实** | `reroll_numbers.md` §6.1 | 推断 |

### 1.3 分叉：位置有、旋转/缩放没有、物理常数没有

原来的二选一（"逐物件 XYZ 可得" vs "只有参数+内容+区域描述"）被数据推翻了一半，改成三档：

| 档 | 数据条件 | 审计形态 | 能得出什么 |
|---|---|---|---|
| **A. 位置 + 尺寸 + 区域 + 类别**（MAS1、Cancer、Cygnus…19 关的 area 0） | `object dump` 有非零 `position` 与 `f32Volume`，类型表有 `Req Pickup Size`/`Volume`/`penalty` | **近 1:1 布局**：可把 652 件按真实坐标摆进灰盒；旋转需自定（`DATAMODEL_NOTES.md` §5.1） | 内容密度、成长曲线、门槛、台阶/爬墙在**真实布局**上的可玩性；"我们的校验器拒绝哪些件" |
| **B. 只有参数 + 内容 + 区域描述**（多 area 关的 area≥1，13/32 个 area 全零） | 同上但无坐标 | **参数/内容/规则对齐，布局手摆** | 关卡节奏、目标/上限比例、星级、时限；不能量"通道宽度/可达性" |
| **C. 物理常数** | 无公开来源（F9） | **无法对齐**，只能拿手册/专利锚点做定性对照 | 只能证明"我们的值是我们选的"；不许写"还原原作手感" |

**结论**：MAS1 属 A 档（99.7% 有坐标），所以种子语料用 MAS1；`Make Cancer` 也属 A 档（523/523）但**没有增长曲线来源**，作为第二语料只验内容与尺度，不验成长（§6.2）。

---

## 2. 指标表（47 条，6 层）

每行给：定义 → 原作侧怎么读（表/文档 + 证据级）→ 我方侧读数在哪（file:line 或模型）→ 单位 → gap 意味着什么。
**没有一条是"仅 Unity"**：所有我方值要么由参考模型算出，要么是仓库里的参数/文档值。真正只能在 Unity 里测的量（每步成本、帧时间、手感、实际 HUD 排版、相机遮挡）被**排除**在本表之外，见 §6.4。

### 2.1 T 层：时间与积分（5 条）

| 指标 | 定义 | 原作侧（证据） | 我方侧（file:line / 模型） | 单位 | gap 意味着什么 |
|---|---|---|---|---|---|
| `T01` 定步频率 | 逻辑一步的时长 | 30 Hz，`DoTick(1/30f)`（F1，源码） | `fixedStepSeconds=0.02` + 每帧 ≤5 步（`Docs/Design/BallMotor/README.md:295`） | Hz | 我们采样更密；但欧拉积分对步长敏感，同一套速度会得到不同的拐弯/贴墙细节 |
| `T02` 积分与接触求解 | 一步内位置/速度怎么解 | 原生 DLL，不可见（F9） | `contact_v2.py:462 step()`：欧拉 + 子步 + 最近点推挤 3 次 | — | 无法声称"同阶"；只能声称"我们的解算器有这些性质" |
| `T03` 隧穿界 | 每子步位移 / 半径 | 无 CCD、30 Hz、粗碰撞 + 掉出世界救回（`reroll_numbers.md` §4） | `contact_v2.max_substeps`（κ=0.5、N≤8）→ 起始球 0.267 r/子步 | r | 机制不同：原作靠"慢+粗+救回"，我们靠采样下界（2r 引理） |
| `T04` 碰撞形状 | 球与世界的接触外形 | 碰撞网格 + 每物件包围盒 + vault 点（F8） | 球 `r_phys` + 16 根针尖，OBB 最近点（`CONTACT_MODEL_V2.md` §4A.2 / `shapes.py`） | — | 原作不是解析球；形状差异直接改贴墙、上台、卡门的手感 |
| `T05` 拾取触发体 | 判定"能吸"的空间范围 | Mh 包围盒（实形/朝向未知）+ 10× 体积门（F4） | `shapes.pickup_reach` / C#：碰撞球，轴/面对角/角均 1.0r；道具侧为自身有向盒 | r | 有意采用内接等效球以保证接触处冻结；不并入滚动针，见“吸收触发的几何”决定行 |

### 2.2 M 层：运动与地形（9 条）

| 指标 | 定义 | 原作侧（证据） | 我方侧 | 单位 | gap 意味着什么 |
|---|---|---|---|---|---|
| `M01` 最大平面速度 | 满推稳态速度 | **无公开数值**（F9） | `maxSpeedMps=4`（`README.md:298`） | m/s | 无法校准；原作的 Vmax 随体积增大（专利），我们现在也随 D/D0 增大（2026-11-17 起按球径缩放，`growthSpeedFactor` 已删除，见 `BALL_INERTIA.md`） |
| `M02` 加速度/刹车 | 到速与停车的快慢 | **无数值**（F9）；有定性：专利行进参数含惯性、手册有显式 Brake 输入、5 cm 球录像起停 ≤0.2 s（`ORIGINAL_BALL_INERTIA.md`，录像弱） | 5 / 8 m/s²；满速松手 0.40 s、0.68 m（`BALL_INERTIA.md` §1）；惯性重做见该文 | m/s² | 数值无法校准；所有者 2026-11-17 反馈松手停太快，设计为 PROVISIONAL 预设 B，待拍板 |
| `M03` 坡面切向重力 | 坡上是否下滑 | 有打滑/失控的**定性**证据（`/tmp/orig_physics/report.md` §3.1） | `rampTangentialGravityScale=0`（`TERRAIN_SUPPORT.md` §11 U3） | — | 我们坡上既不减速也不下滑，比原作"更可控" |
| `M04` 滚/爬分界角 | θ 大于多少算墙 | **无角度数值**（`report.md` §1.4） | `θ_ride=45°`（`contact_v2.py:53`） | deg | 45° 是我方设计值；原作有爬墙行为但无阈值 |
| `M05` 爬升高度上限 | 球心能升多高 | 手册"能翻过的墙约等于球的大小"（官方，唯一数值锚） | `H_cap = 1.0·(2r+λL)` → 1.0×D（`contact_v2.climb_height_cap`） | ×D | 这是**唯一**有官方锚点的量；我们取同量级 |
| `M06` 90° 爬升速度 | 满推贴墙的上升速率 | **无**（F9，只有"极慢地蹭"的体感） | `climb_speed`（T_climb=2.8125 s，所有者 2026-11-17 试玩后提速 60%）→ 起始球 0.183 m/s | m/s | 数值是我方设计；原作只留行为描述 |
| `M07` 爬升输入 | 要不要按键 | 不要，双摇杆前推（We Love Katamari 手册；官方） | 推入即爬，无按钮（`CONTACT_MODEL_V2.md` §8 拍板 3） | — | **一致**：输入语义相同 |
| `M08` 回滑与锁定 | 到顶/松手后怎么掉 | **无**（只有"超过高度就掉回来"） | v_slide ≤1.0 m/s + 到顶锁定 + 0.35 s 冷却 | — | "一次推入一次机会"是我方设计 |
| `M09` 台阶阈值 | 多高的台能上 | 官方只说"变大能越段差"，无数值 | v1 `0.25·D`（`KatamariBallMotor.cs:110`）；v2 几何 `h<r`=0.5D | ×D | 两套规则并存；0.25D 只是诊断口径，别当物理 |

### 2.3 G 层：尺寸与成长（13 条）

| 指标 | 定义 | 原作侧 | 我方侧 | 单位 | gap 意味着什么 |
|---|---|---|---|---|---|
| `G01` 起始球径 | 开局直径 | 5 cm（F2） | 0.2997 m（`placement.json level.ballDiameterStart`） | m | 我们开局就是原作的 6×，等价于原作中后期关 |
| `G02` 通关目标 | 通关线 | 10 cm（MAS1） | 1.25 m（`story_01_bench_size.json`） | m | 差 12.5×；我们的目标在原作里约等于 MAS7 的 1/5 |
| `G03` 上限直径 | 包线 | 30 cm | 2.20 m | m | 差 7.33× |
| `G04` 目标/上限 | 余量比 | 0.333（10/30） | 0.568（1.25/2.20） | — | 原作用 3× 余量，我们 1.76×，"冲上限"更容易 |
| `G05` 体积→直径 | D(V) 的形状 | `D=(6V/π)^(1/3)` + 0.02 cm | `SizeModelMath.TryDerive` 同形、无偏置 | — | **一致**；只差显示 fudge |
| `G06` 单件回收比例 | 吸收一件给多少体积 | 0.8（MAS1 起始），随尺寸降到 0.16（F5） | ΔV = V_box（100%） | ×V_item | 同一次吸收我们多得 25%；末段差距更大（原作拖尾、我们加速） |
| `G07` 10× 体积门 | 能不能吸的判据 | `πD³/6 ≥ 10V`（F4） | `PickupAbsorbMath.AbsorbVolumeRatio = 10`（`Req = cbrt(60·k·a·b·c/π)`） | ×V_item | **完全一致**；比率是常量，不再是逐场景序列化字段 |
| `G08` 门槛取整 | 门槛怎么落到 mm | 向下取整到 mm（蚂蚁实际 8.8×） | **一致**：`Req_mm = floor(1000·cbrt(60V/π))`，球侧也取整 `floor(1000·D)`，ε=1e-3 mm 只修浮点（`PickupAbsorbMath.TryFloorMillimetres`、`placement_core.absorb_diameter_req_mm`） | m | 已无差距；1 mm 在 22.2 cm 球上是 0.45% 的直径 |
| `G09` HUD 偏置 | 显示直径的 fudge | +0.02 cm（MAS1 显示 5.02） | 0（直接打印语义直径） | m | 纯表现层，但它是"HUD 读数=原作"的证明点 |
| `G13` 体积系数 k | `Volume = k·bbox` 里的作者手调系数（门与成长都用它算体积） | 0.70（1425 型中 70.7% 精确等于；薄片 8–16、蓬松 0.05–0.07、镂空家具 0.3–0.5） | **门已一致**：`defs[].volumeK`/`props[].volumeK`，缺省 0.70（`PickupAbsorbMath.DefaultVolumeK`、`placement_core.DEFAULT_VOLUME_K`）；**成长仍按 1.0** | ×bbox | 门侧差距已消除：`_ref_cancer` 145/145 类型的 Req 比值 = 1.0000（`ABSORB_GATE_ORIGINAL.md`）。成长侧仍缺 k（等效回收率 0.8×0.70=0.56，只采纳 0.8 仍会多 1.43×），属步骤 C/D |
| `G10` 每档实例数 | 每个"直径翻倍"档有多少可吸件 | 253 件/octave（654 件 / 2.58 档） | 44 件/octave（127 件 / 2.87 档） | 件/octave | 同尺寸跨度下原作密 5.7× |
| `G11` 供给比 | 可吸总体积（**已乘回收惩罚**）÷ 从起点到上限所需体积 | 1.65×（598 件 Σ`penalty·V`=0.0232 m³ ÷ 0.01407 m³） | 0.46×（ΣV_box 2.53 m³ ÷ 5.56 m³；我方惩罚仍 1.0） | × | **owner 的底线**：原作远多于所需，不可能卡；我们 <1，物理上到不了上限。采纳 D2 的 0.8 惩罚后我方再 ×0.8 |
| `G12` 饥饿检查 | 每个球径档「可吸且未吃件数 ÷ 继续成长所需件数」，取最紧档 | 1.65×（各档 500/105、581/188、598/363） | 0.45×（各档 109/106、122/213、130/286） | × | 供给比是总量；这一条看**每一档**：我们 1.2 m 档就 0.57×，等于"走到那里就卡"；原作最紧档仍 >1 |

### 2.4 C 层：内容与行为（11 条）

| 指标 | 定义 | 原作侧 | 我方侧 | 单位 | gap 意味着什么 |
|---|---|---|---|---|---|
| `C01` 实例数 | 一关摆多少件 | 654（MAS1；单关 523–3955，F6） | 127 | 件 | 差 5.2×；"吃两下就变大"最直接的物理解释 |
| `C02` 类型数 | 多少种 | 107（单关 107–565） | 44 | 种 | 差 2.4×；原作靠种类密度维持"每步都有新东西" |
| `C03` 内容体积 / 满径球体积 | 内容够不够 | 2.46×（D_req≤30 cm 的 598 件 = 0.0348 m³ / 0.0141 m³） | 0.451×（2.517 m³ / 5.575 m³） | × | 原作超饱和，我们**不够**：即使按"k·bbox"口径（原作 `Volume`）重算也走不到 2.20（`repricing_proposal.md` §0） |
| `C04` 逐物件行为字段 | 数据里有没有行为 | `pathSpeedIndex` **11 个取值**（`2` 为缺省 80.2%，含 1 个坏值 `t`）；vault 点 0/2/3/8 个（805/261/650/1 型）；逐实例 `playAnimNo`（9.7% 实例带动画）、`parentProp` 单父指针（1,970 条子件 / 1,082 个父件 / 深 13 层）（F8；计数见 `PROP_BEHAVIOURS.md` §10.1） | 数据无 behaviour 字段，运行时不读（`PROP_BEHAVIOURS.md` §0.5） | — | **缺机制**：内容能搬，行为不能搬 |
| `C05` 堆叠支撑图 | 摞着的东西怎么倒 | **堆叠逻辑无公开证据**（`PROP_BEHAVIOURS.md` §5.1：专利也不讲堆叠）；数据侧只有父指针（1,970/1,082，深 13 层） | `support/stackedOn` 只喂校验器；Python 有 CSR 图（`support_graph.py`），运行时没有 | — | 两边都不是"已有机制"；我们连运行时都缺 |
| `C06` 吸附物是否放大 | 粘上去后跟不跟球长大 | 不缩放，保持吸附瞬间位姿（专利） | 不缩放，只缩放 `CoreMesh`（`BallView.cs:19-22`） | — | 缩放口径**一致**；都允许互相穿插。放置位置不一致，见「吸附物位姿」行与 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) §1 |
| `C08` 区域最小件/入场球径 | 入场时最小件相对球多大 | 0.30（MAS1）/ 0.329（Cancer）；32 区域中位 0.277 | 0.108（story_01）/ 0.80（`_ref_mas1`） | × | 原作最小件≈球的 1/4–1/3；story_01 只有 0.11，`_ref_mas1` 的 0.8 是因为 5 cm 球配 4 cm 硬币 |
| `C09` 入场可吃占比 | 进场立刻能吃的比例 | MAS1 59%、Cancer 35%；32 区域 12–76%、典型 ~26% | 67%（story_01）/ 见对照节（`_ref_mas1`） | — | 两边都"进场有得吃"；原作按区下降，我们没有区 |
| `C10` 1–10 cm 类占比 | 小尺寸类的占比 | MAS1 76%、Cancer 3.8%；跨区衰减 MAS4 60→12→5.5→0%、MAS8 16.6→0 | 15%（story_01） | — | 原作第二区起不再放新小件（靠"世界整体上移"而不是"删小件"）；我们只有一个尺寸带 |
| `C11` 尺寸-半径空间分布 | 小件是否集中在中央 | Cancer ρ=+0.354、MAS1 ρ=+0.219（大件在外、更稀疏） | ρ=+0.13（story_01） | ρ | 原作两个可测关都是正相关：中央是"饭碗"，地标是"地平线"；我们是弱正相关，中央不够挤 |
| `C07` 埋没处理 | 旧物被吞后还画不画 | 录像 5.0→6.9 cm 段内未见核心变化、全程是否固定未定；后吸的件盖住先吸的，先吸的仍画出。消隐按**摄像机档位上的物件相对大小**（专利；阈值未知，录像只有区间约束）（[`ORIGINAL_ATTACH_LOOK.md`](../../Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md) b1、c1、d1–d3） | **没有任何策略**；核心等体积直径 = 当前碰撞直径、随体积长大，先吸的件被长大的核包进去，被挡住的件自然看不见 | — | 球芯行的规则已覆盖该情形。不做埋没隐藏或剔除；原作的相对大小消隐默认不做，开放问题见 [`ATTACH_PLACEMENT.md`](../BallLayers/Attachment/ATTACH_PLACEMENT.md) §3.3 |

### 2.5 L 层：关卡规则与呈现（5 条）

| 指标 | 定义 | 原作侧 | 我方侧 | 单位 | gap 意味着什么 |
|---|---|---|---|---|---|
| `L01` 换档阈值 | 到哪些尺寸触发换区特效 | 1 个：7 cm（MAS1，F3） | 0：12 个 zone 是作者数据，没有尺寸阈值表 | 个 | **缺机制**：我们没有"下一个尺寸档"这个概念 |
| `L02` 时限 | 一局多久 | `GAME_TIME=4`，单位未证实（F10） | 120 s（`story_01_bench_size.json`） | s | 按分钟读原作是我们 2 倍；单位不确认前不给比值 |
| `L03` 相机 FOV | 垂直视场 | 37°（`missions.CAMERA_FOV`） | 60°（`README.md:248` 的垂直 FOV 口径） | deg | 原作更窄：同球径在画面里更大，"看得见前方多少"完全不同 |
| `L04` HUD 报什么 | 显示的物理量 | 直径，cm，含 +0.02 cm | 语义直径，m | — | 量相同、单位不同；**可接受偏差**，但换算规则要写死一处 |
| `L05` 评分钩子 | 星级/评分阈值 | 4 档（10/12/14/15 cm，`RATING_DIAMETER`） | 3 档（1.25/1.45/1.60 m） | 个 | 原作档距是起始球的 20–40%，我们 16%：我们的星级像"同一段的三小步" |

### 2.6 S 层：最小尺寸与尺度不变性（4 条，本次新增）

| 指标 | 定义 | 原作侧 | 我方侧 | 单位 | gap 意味着什么 |
|---|---|---|---|---|---|
| `S01` 最小可吸道具 | 最小的 `D_req` | 15 mm（Ant） | 32 mm（`seed`） | m | 两边最小件都远小于起始球 |
| `S02` 最小件/起始球 | 比例 | 0.30（15/50 mm） | 0.107（0.032/0.2997） | — | 原作"第一口"相对更重要；我们的 1 u 量化显得更粗暴 |
| `S03` 绝对米制常数越线数 | 有多少"米制常数">10% 起始球径 | 5/7（skin 5 mm、叠放 10 mm、推挤 50 mm、突出 cap 300 mm、掉走 10 mm） | 2/7（推挤 50 mm、突出 cap 300 mm） | 个 | **同一个引擎跑 5 cm 球，5 mm 的 skin 就是球径的 10%**；这些常数不随球缩小 |
| `S04` 尺度不变性 | 长度统一 ×6 后规则是否等价 | 未知（原作没有缩放实验） | 同构 5 条 / 非齐次 6 条 + 时基与重力 | — | 体积门、D(V)、`h<r`、`H_cap∝D`、`0.25D` 是齐次的；米制常数、30/50 Hz、`g=9.81` 不是 → **手感不等价** |

---

## 3. 预期 gap 假设表

`CONFIRMED-BY-CODE` = 代码/数据直接证明；`SUSPECTED` = 有间接证据或推导；`UNKNOWN` = 两边都没数。

| # | 假设 | 状态 | 证据 | 对应指标 |
|---|---|---|---|---|
| H1 | 我们爬不上 >36.87° 的接缝（几何死锁） | **CONFIRMED-BY-CODE** | `/tmp/ball_audit/report.md` §B（`tanθ+1−1/cosθ ≤ 0.5` ⇒ θ≤36.87°；C05 四档全红）；v2 用 OBB 最近点后**设计上**消除（`CONTACT_MODEL_V2.md` §2.6），但**未在 Unity 验证** | `M04`/`M09` |
| H2 | 斜板不挡侧面/底面，球会穿过去 | **CONFIRMED-BY-CODE** | `KatamariBallMotor.cs:947`（扫掠不含坡）+ `TERRAIN_SUPPORT.md:805`；v2 改成实体 slab（设计，未验证） | `T04` |
| H3 | 运行时没有堆叠支撑图，上面的东西不会掉 | **CONFIRMED-BY-CODE** | `PROP_BEHAVIOURS.md` §0.5（`support/stackedOn` 只喂 `placement_core.py:1268`）；`support_graph.py` 只有 Python 模型 | `C05` |
| H4 | 运行时没有"行为"（移动/动画/轨道/坠落） | **CONFIRMED-BY-CODE** | `PROP_BEHAVIOURS.md` §3.1 是提案；`placement_core.PROP_KEYS` 会静默丢弃 `behaviour*` | `C04` |
| H5 | 埋没的吸附物仍然可见 | **CONFIRMED-BY-CODE** | `/tmp/ball_audit/report.md` §E(c)：无 burial 代码；核心随体积长大才有“被吞”，被核挡住的件在画面上自然看不见，不做隐藏或剔除（`CONTACT_MODEL_V2.md` §4.3） | `C07` |
| H6 | 成长太快（单件回收 100% + 整数量化） | **CONFIRMED-BY-CODE** | `/tmp/growth_audit/report.md` §0.2/§4（44 个 def 中 43 个超过 10% 口径；最坏 +29.3% 直径）；原作默认 0.8 且末端 0.16 | `G06`/`G10` |
| H7 | 内容量差一个量级，导致"两下就变大" | **CONFIRMED-BY-CODE** | 本文 `C01`–`C03`（654/127、107/44、2.46×/0.45×）；`reroll_numbers.md` §2.2（600–900 件/0.30→1.25 m） | `C01`–`C03`/`G10` |
| H8 | 1 u = 4.1 L 的量化粒度太粗 | **CONFIRMED-BY-CODE** | `/tmp/growth_audit/report.md` §0.7（1 u = 起始球的 29%；99/127 件 <1 u） | `G06`/`S02` |
| H9 | 5 cm 球在我们引擎里会被米制常数淹没 | **SUSPECTED**（算得出，未实跑） | 本文 `S03`：7 个常数里 5 个 >10% 球径；`CONTACT_MODEL_V2.md` §3.5 的 `cap_margin=0.02` 在 5 cm 球上是 40% | `S03`/`S04` |
| H10 | 30 Hz vs 50 Hz 会改变贴墙/接缝手感 | **SUSPECTED** | 步长比 1.67×；`M09` 的 0.25D 钳位与子步数（`T03`）都按时长算 | `T01`/`T03` |
| H11 | 相机 37° vs 60° 会让"看见前方多少"差很远 | **SUSPECTED** | `L03`；`README.md:248` 讨论过 60° 下球占画面下缘 | `L03` |
| H12 | 原作速度/加速度/坡度常数可以对齐 | **UNKNOWN（不可）** | F9：无公开数值 | `M01`/`M02`/`M06` |
| H13 | 原作是否按尺寸阈值换区 | **UNKNOWN** | `swirls` 是"播特效的尺寸点"，真正换区机制未在公开源码出现（`reroll_datamodel.md` §4） | `L01` |
| H14 | 时限单位 | **UNKNOWN** | F10 | `L02` |
| H15 | 原作堆叠的规则 | **UNKNOWN**（数据侧只有父指针，逻辑未公开） | `PROP_BEHAVIOURS.md` §5.1、§10.4（P18：拿得到数据、拿不到逻辑） | `C05` |

---

## 4. 审计流程

### 4.1 六步

```
① 建语料   原作表 → corpus_*.json（schema 校验 + 每条数据带证据）
② 跑         python3 Docs/Design/Parity/parity_harness/run_parity.py
③ 读表      47 行：一致 / 参数差 / 缺机制 / 不同机制 / 可接受偏差 / 原作未知
④ 分类      每个 gap 归入四类之一（见 §4.2），并写下"要改谁"
⑤ 修/试     按 §4.3 的 cheapest-first 顺序
⑥ 回归      改完重跑；行变了就是参数变了（表是数据的函数，不是手写文档）
```

### 4.2 gap 的四类归因

| 类 | 判据 | 例子（本表） | 处置 |
|---|---|---|---|
| **参数差** | 机制相同，数值不同 | `G01` 起始球径、`G06` 回收比例、`L05` 星级档 | 决定要不要采纳原值（§5 D2） |
| **缺机制** | 原作有、我们没有 | `C04` 行为字段、`C05` 堆叠、`C07` 埋没、`L01` 换档（`G08` 取整已于 2026-11-16 还原） | 立项；先看 `PROP_BEHAVIOURS.md` 的提案成本 |
| **不同机制** | 两边都有，但形状不同 | `T03` 防穿模、`T04` 碰撞形状、`T05` 触发体、`S03`/`S04` 尺度 | 记录差异，**不许**写成"还原原作" |
| **可接受偏差** | 差异存在但无玩法影响 | `M05` `H_cap`（锚点同量级）、`L04` HUD 单位 | 写进 evidence，不再追 |

### 4.3 cheapest first（每步要什么）

| 步 | 做什么 | 需要 | 成本 | 预期关掉哪些行 |
|---|---|---|---|---|
| **0** | 已有：`build_corpus.py` + 47 行表（`run_parity.py`）；语料本机生成、不入库 | 本机的 `KatamariRef/meta` | 已完成 | — |
| **1** | 把 MAS1 的 652 件真实坐标转成 `placement.json`（`ref-level-builder` 在做），跑我们的校验器 | 数据（已到手）+ 我们的 `placement_core` | 0.5 天，**不需要 Unity** | 产出"5 cm 球下哪些件被拒绝/超容量"的清单（`S03` 的具体受害件） |
| **2** | 成长：`ΔV = 0.8·V_box`、`c` 0.0041→0.0005、取消 1 u 量化 | 代码（电机 + 账本）+ 数据（44 个 def 重定价） | 1–2 天 | `G06`、`G10`、`C03`；`growth_audit` 方案 A+C 已算过数值 |
| **3** | 尺寸档/换区阈值表进关卡数据（先只做"读数 + 播报"，不做特效） | 数据 schema + 一次校验器改动 | 1 天 | `L01` 0→1 |
| **4** | 行为字段 + 堆叠运行时（`parent` 父指针 + 每 tick 浮空复检 + 坠落） | `PROP_BEHAVIOURS.md` §10.9 的数据形状 + `placement_core` 同批改 | 3–5 天 | `C04`、`C05` |
| **5** | 核心显示径跟随碰撞径（冻结旧件可被成长核包住；不新增埋没 cull） | `BallView` + 马达一个只读 getter + 删 0.95 常量与测试 | 0.5–1 天 | `C07`、视觉核心行 |
| **6** | 5 cm 球实跑 MAS1 灰盒（Unity） | `PhysicsTestField` 一套 cell + 球径 preset | 2 天 + Unity 操作 | `T01`/`T03`/`M0x` 的手感与每步成本；`S03` 从"算得出"变"量得到" |
| **7** | 相机 37° 与 HUD cm 显示 | 场景 + HUD 一处 | 0.5 天 | `L03`、`L04` |

**顺序的理由**：1–2 是纯数据/纯计算，能把最大的两个 gap（内容密度、成长）与最贵的两个（行为、堆叠）分开验证；3 便宜且能立刻验证"尺寸档"这个概念能不能进我们的数据模型；6 之前不要碰手感，因为没有原作数值可对。

---

## 5. owner 决定（2026-11-15）

原则一句话：**原版怎么做就怎么改**——原版已确证的规则照抄，未确证的一律保持我方设计并标 `原作未知`。

| # | 决定 | 对本文与实现的影响 |
|---|---|---|
| **D1** | 目标是**规则/数值 1:1**（不是"只对齐布局"）。原版已确证的规则逐条照抄 | `G05`–`G09` 从"我方值"改成"采纳原版值"后应变成 `一致`；`M0x` 仍 `原作未知`（无公开数值） |
| **D2** | 采纳：**0.8 惩罚 + 每关分段回收曲线 + +0.02 cm 补偿 + 门槛取整到 mm**（10 倍门我们已一致） | 改 `G06`/`G08`/`G09` 三处；`G05` 形状不变 |
| **D3** | **不乘 6，全案用真实尺寸**；"缩放版"降为纯诊断，不再是推荐路径 | 5 cm 球暴露的每个米制常数都是审计发现 → §5.3 修复清单 + §5.4 新主题 |
| **D4** | **不补我们的新物件**，还原阶段直接用解包出来的物件；原创美术线继续，但属于后面的"替换"阶段 | `C01`–`C03` 的口径改为"搬原作内容"；**600–900 件是供给下限，不是目标**——目标是 `G11 ≥ 2`（原作 1.65–2.4×，远多于所需）。`repricing_proposal.md` 的补件清单只作诊断 |
| **D5** | 体积改**连续**（float/double，或足够细的整数定点，如 1 mm³ 级、Udon 可用 `long`）；评分/HUD 用显示单位 | 与"账本只收整数价值单位"冲突，受影响点见 §5.2 |
| **D6** | **起始球径是每关参数**（第一星 5 cm、Cancer 22.2 cm、第三星 10 cm…），不是全局常数；目标/上限同理 | 引擎必须按关卡配置三者；列入 §5.3/§5.4 |
| **D7** | 做双模式——就是正在进行的 `_ref_mas1` 重建关 | 本文 §4.3 步 1/6 即其验收路径 |

### 5.1 D1/D2 落地后哪几行会变

| 指标 | 现在 | 照抄原版后 |
|---|---|---|
| `G06` 单件回收比例 | 1.0（100%） | `penalty`（默认 0.8）+ 每关分段曲线（MAS1：5 cm .8 → 15 cm .4 → 20 cm .16） |
| `G08` 门槛取整 | ~~0（浮点）~~ **已做（2026-11-16）** | 0.001 m（向下取整到 mm） |
| `G09` HUD 偏置 | 0 | 0.0002 m（+0.02 cm） |
| `G05`/`G07` | 已一致 | 不动 |

### 5.2 D5 受影响的点（只列，不做实现）

| # | 位置 | 现在 | 改连续体积后会怎样 | 推荐做法 |
|---|---|---|---|---|
| 1 | `KatamariBallMotor` 的 `baselineVolumeUnits` / `cubicMetersPerVolumeUnit` / `SizeModelMath.TryDerive` | 整数 units × 0.0041 m³ | 两个常数失去意义，`confirmed` 变成连续 m³ | 保留 `V` 为唯一真源（m³），units 只在"计分/显示"派生；`TryDerive` 改吃 `V`；**V 的来源改成 `k·a·b·c`** |
| 2 | `GrowthLedgerMath` 账本事件 | 事件里 `gain = valueUnits`（整数） | 事件字段要带 float/long 体积，幂等键仍用 `(spawnId, generation)` | 事件加 `volumeM3`（float32 足够）或 `volumeMm3`（long，Udon 可用）；幂等键不变 |
| 3 | 账本幂等/重放 | 由整数和保证可重建 | 浮点求和顺序会影响末位 → 同一批事件重放得到不同 D | **推荐整数定点**（1 mm³ = long）：求和精确、幂等、跨端一致 |
| 4 | 网络同步（Udon 事件/序列化） | 一个 int | 多一个 8 字节字段（或 float） | long 定点优于 float：跨端一致、无 NaN、可校验 |
| 5 | `PickupFieldSync` 定价 | 读 `defs[].valueUnits` | 改为读 `volumeM3`（或 mm³），声明值不再手写整数分 | 校验器保一条"声明体积 = **k·a·b·c** × penalty"的自检；`defs[]` 新增 `pickupVolumeFactor`（默认 0.70，按类别手调）；重定价的 `f` 取 **0.70**（不是 1.0） |
| 6 | `StoryMode` HUD | 打印语义直径（m） | 不变（HUD 只吃直径），但要加 +0.02 cm 与 cm 显示 | HUD 与账本解耦：HUD 读 `D`，不读 units |
| 7 | 星级比较 | 直径 ≥ 阈值（米） | 不变 | 保持"按直径比较"，不要退回按 units |

**一句话建议**：**用 1 mm³ 的 `long` 定点体积**（连续够细、求和精确、跨端幂等），HUD/评分再用显示单位换算；这同时满足 D1（原版是连续体积、门槛取整到 mm）与"账本只收整数"的旧规则。

### 5.3 D3/D6 修复清单：让引擎支持 0.05–0.30 m 起始球

米制常数在 5 cm 球上的相对权重（`S03` 现算「超过球径 10% 的个数」；叠放容差那条已在 2026-11-16 取消，见第 3 行，所以分母比原表少 1），逐条判定。**判定口径**：随尺寸缩放＝改成 `k·r` 或 `k·D`；绝对值合理＝它是数据/美术尺度，保留但要在 5 cm 下复核；需要重写＝语义本身按米制写死，改成按物件/球的比例。

| # | 常数 | 现在 | 在 5 cm 球上 | 判定 | 建议 |
|---|---|---|---|---|---|
| 1 | `contactSkinM` | 5 mm | 球径的 10% | **随尺寸缩放** | `0.03·r`（并保留 0.5 mm 下限） |
| 2 | `contactSlopM` | 0.5 mm | 1% | 随尺寸缩放 | `0.004·r` |
| 3 | ~~`STACK_TOLERANCE_M`（叠放）~~ → `parent_gap_window` | 10 mm 绝对容差，`stackedOn` 顶底差 > 10 mm 即硬错误 | — | **已重写（owner 选 `restore` 的反面：取消这条规则）** | 2026-11-16 所有者裁定「engine 不能禁止这个」：作者把托盘摆在长凳左边 7.5 cm 是合法摆法，校验器不得因此判红。现口径：接触窗口 = `[-0.30, +0.50] × min(两件世界高度)`（1,259 条原作 `parentProp` 链接标定）；几何不接触只出 `[parent_not_seated]` **warning**，运行期每 tick 自检；只有机器推导的 `parentDerived` 链在 `check --strict-parents` 下才是 error。新的「永远不掉」由显式字段 `pinned: true` 表达，不是靠校验器放水。属数据校验器层，不在运行时。见 [OPEN_DECISIONS (t)](../../OPEN_DECISIONS.md)、[PROP_BEHAVIOURS §10.9](../PickupAbsorb/PROP_BEHAVIOURS.md) |
| 4 | `maxGrowthPushPerStep` | 50 mm | 100% | 随尺寸缩放 | `0.15·D` |
| 5 | 长杆突出 cap | `min(0.35r, 300 mm)` | 300 mm 是 6 个球径 | **绝对值合理**（美术尺度），但比例项要优先 | 5 cm 下必须由 `0.35r` 生效；300 mm 只作为大球护栏 |
| 6 | `climbCapMargin` | 20 mm | 40% | 随尺寸缩放 | `0.05·r` |
| 7 | `supportDropTrigger` | 10 mm | 20% | 随尺寸缩放 | `0.05·r` |
| 8 | 台阶 `0.25·D` / v2 `h<r` | 比例 | 1.25 cm | 保留（已齐次） | 不动 |
| 9 | 容量 8 平面 / 8 盒 / 4 坡 / 6 挡路 / 6 支撑 | 计数 | 与尺度无关 | 绝对值合理 | 改为**关卡数据配置**（`budget` 已有字段），不写死 |
| 12 | **体积定义 `k·bbox`**（新增，见 §5.3.2） | 门与成长用 `a·b·c`（k=1） | 原作默认 k=0.70，逐类手调 | **需要重写** | `defs[].pickupVolumeFactor`（默认 0.70，薄片抬高、蓬松/镂空压低）+ 门/成长/定价三处同源 |
| 11 | **实例容量**（新增，见 §5.3.1） | 127 件（story_01）/ 648 件（`_ref_mas1`） | 原作单关 523–3955 件 | **需要重写** | 650–4000 实例：空间哈希 + 激活窗口 + 远处休眠 + 静态批处理，详见下表 |
| 10 | 坡度上限 45° | 角度 | 与尺度无关 | 保留 | 不动 |


#### 5.3.1 引擎需支持 650–4000 实例（只列需求与优先级，不实现）

原作单关实例数 523–3955（`C01`/F6），`_ref_mas1` 已有 648 件，Moon 关 3955 件。我们现在是 127 件，`PickupFieldHost` 的设计规模是"几十件到一两百件"。

| 面 | 现在 | 650–4000 实例下的需求 | 优先级 |
|---|---|---|---|
| **PickupFieldHost 数组与扫描** | 固定并行数组 + 每 0.05 s 一次激活扫描（`PickupFieldHost.cs:125-134`）；候选队列 4；挡路/支撑各 6（`KatamariBallMotor.cs:365`） | 候选/挡路/支撑的容量必须与"球附近件数"绑定，不再与"全场件数"绑定；候选队列 ≥16；主线索引改外部表 + 区间，不在 Udon 里存 4000 个对象引用 | **P0**（不改就跑不起来） |
| **空间哈希** | 无（线性扫描） | cell = `max(2r, 1 m)`、桶内 ≤32；插入只发生在重生/销毁；查询返回"窗口内 ≤24 条" | **P0** |
| **激活窗口 / 远处休眠** | 0.05 s 定时 + 1.6/2.5 m 滞回（同文件） | 以球心为心、半径 `4D + 10 m` 的窗口内保持活跃；窗口外只保留数据（不写 transform、不渲染）；进窗前 2 m 预激活 | **P1** |
| **Udon 每步预算** | 每步 ≤8 次调用、约 2.5–13k flops（`CONTACT_MODEL_V2.md` §1.8 估算，非实测） | 4000 件**绝不能每步全扫**：每步只碰近集（≤24 条），全场扫描降到 0.1–0.2 s 一次；行为件按 15 Hz 分摊（`PROP_BEHAVIOURS.md` §7 估算 ≤800 movers） | **P0** |
| **Draw call / 静态批处理** | 一件一个 GameObject/MeshRenderer | 按区域合并成静态网格 + LOD/impostor；只有"可吸且未被吃"的件需要独立对象；同材质合批 | **P1**（渲染侧） |
| **数据形态** | `placement.json` 内联在场景里 | 650–4000 件的表要能流式加载/按区切分；实例与类型分离（类型 107–565） | **P1** |

**验收（每项独立）**：P0 用 `_ref_mas1`（648 件）在编辑器里跑通"不超容量、不丢件、候选正确"；P1 用合成 1500 / 4000 件场景测每步成本与 draw call，读数进 `Docs/Evidence/`。**这两项都要 Unity**，不在本轮范围。


#### 5.3.2 体积定义：`k·bbox`（新增，2026-11-15 更正）

| 面 | 现在 | 原作 | 需求 |
|---|---|---|---|
| 门的体积 | `a·b·c` | `k·a·b·c`，默认 k=0.70 | `PickupAbsorbMath` 的 `TryComputeItemVolume` 乘 `defs[].pickupVolumeFactor` |
| 成长的体积 | `valueUnits × c` 或 `V_box` | `penalty × k·a·b·c` | 与门同源；`G13` 的 `k` 与 `G06` 的 `penalty` 相乘 |
| 定价 | 按档位整数分 | 按 `k·bbox` × 关卡曲线 | `repricing` 的 `f` 取 0.70 起，再按类别手调（薄片 2–8、镂空家具 0.3–0.5、蓬松 0.05–0.1） |
| 我们自己的 128 件 | `solid/bbox` 中位 0.483，长凳/桌子 0.053–0.149 | — | 镂空大件手调 k=0.3–0.5（与原作"手调"同做法），不要用实体体积——原作也没有用 |

**验收**：同一件在"门"和"成长"里得到同一个体积（单测）；MAS1 的 107 型 k 全部能从 `fill_factors.csv` 连上（本轮 104/107）；`G13` 从 `缺机制` 变成 `一致`。
**C# 注释再评估**（不改代码，只记录结论）：`PickupAbsorbMath.cs` 类注释里"AABB 体积是原作自己的量"这句**大体正确，缺 k**——应改为"原作的量是 k×AABB，k 默认 0.70、逐类手调"。

**顺序（每步可独立验收，前 4 步不需要 Unity）**：

1. **起始/目标/上限参数化**（D6）：`ballDiameterStart/Max` + `clearDiameter` 从关卡 JSON 读，三关（5 / 22.2 / 30 cm）配置各跑一次 `run_parity`，`G01`–`G03` 全 `一致`。
2. **米制常数比例化**（上表 1/2/4/6/7）：改 5 个常数 + 单测；验收 = 在 0.05 / 0.30 / 1.10 m 三档上"常数/球径"比例相同（±1%）。
3. **容量可配**（上表 9）：`budget` 进运行时校验；验收 = 用 `placement.json` 声明的容量能跑通、超容量报错而不是静默丢。
4. **叠放容差重写**（上表 3）：`placement_core` 一处；验收 = 5 cm 关里 2 mm 的顶底差算接触、3 mm 算悬空。
5. **5 cm 档进测试场**：`PhysicsTestField` 加一个 0.05 preset + 判据容差按比例；验收 = C07–C12 在 0.05 档有明确 pass/fail（这是唯一必须 Unity 的一步）。
6. **相机/HUD**：`FollowRig` 的 `0.55 + 2.4r` 在 5 cm 下退化成"眼点离地 0.62 m"（可接受），但要复核；HUD 改 cm + 0.02 cm。

### 5.4 新 owner 主题：引擎对 0.05–0.30 m 起始球的支持

建议作为独立主题跟踪（不属于"第一关"），子项即 §5.3 的 6 步，每项有自己的验收：

| 子项 | 验收物 | 需要 Unity |
|---|---|---|
| A 每关起/止/上限 | 三关配置 + `run_parity` 的 `G01`–`G03` | 否 |
| B 米制常数比例化 | 5 个常数 + 单测（比例一致） | 否 |
| C 容量可配 | `budget` 校验 + 超容量报错 | 否 |
| D 叠放/穿插容差按比例 | `placement_core` 用例 | 否 |
| E 5 cm 测试格与容差 | `PhysicsTestField` 新 preset + C07–C12 | **是** |
| F 相机与 HUD（cm + 0.02 cm） | 截图 + HUD 读数 | **是** |
| G 650–4000 实例（§5.3.1） | `_ref_mas1` 648 件跑通 + 1500/4000 合成场景的每步成本与 draw call | **是** |

---

---

## 6. 语料与工具

### 6.1 文件

| 文件 | 作用 |
|---|---|
| `Docs/Design/Parity/parity_harness/corpus_schema.py` | 语料 schema（严格：未知字段拒绝、每个数据要证据、null 要写进 `unresolved`）；`--describe` 打字段表，`--validate` 校验一个语料 |
| `Docs/Design/Parity/parity_harness/run_parity.py` | 读语料 + 读我们的 `placement.json` → 47 行 Markdown gap 表 + "owner 决定会动哪些行" + 两关供给对照；`--corpus/--ours/--out` |
| `Docs/Design/Parity/parity_harness/build_corpus.py` | 从 `<meta>/tables/raw_reroll_dump__*.tsv` 重新生成两个语料；确定性、只读仓库外路径、`--meta` / `--out` 可传；缺输入时给明确报错 |
| `corpus_star1.json`（**本地生成、不入库**） | **种子语料 = Make a Star 1**：654 实例（652 有坐标）、107 类型、5→10→30 cm、7 cm 换档、37° FOV、相机/出生点 |
| `corpus_cancer.json`（**本地生成、不入库**） | 第二语料 = Make Cancer：523/523 有坐标、148 类型、22.2→80→120 cm（更接近我们的 30 cm）；**无增长曲线** |
| `Docs/tests/parity/` | 59 例：schema 拒收/空值/证据/确定性/文档同步 + **合成小语料**覆盖同一契约；缺语料时 `skipUnless`（`python3 -m unittest discover -s Docs/tests`） |

> **⚠ 语料是不可分发、也不入库的参考数据**（owner 2026-11-16 决定）。`corpus_*.json` 由商业游戏的公开数据挖掘导出，只在本机生成、只用于一次性重建试验，**不得进入世界内容、不得随世界分发**；`.gitignore` 已排除 `parity_harness/corpus_*.json`。干净检出后先跑 `build_corpus.py` 再跑 `run_parity.py`；没生成时 `run_parity.py` 会打印生成命令，测试会 skip 而不是红。

**为什么是 MAS1**：唯一同时满足"99.7% 有真实坐标"+"有完整增长曲线（4 个折点）"+"单 area"的关卡。
**为什么 Cancer 只做第二语料**：它有 100% 坐标、起始 22.2 cm 更接近我们，但**增长曲线没有任何公开来源**（`raw_growth_mechanism.txt` 只给 MAS1–9 与 MTM），所以 `G06` 在它上面必须报"原作未知"——这正好演示了 schema 的"空而不是猜"。

### 6.2 数据矛盾登记（不改数，只登记）

| # | 矛盾 | 两边 | 本文取谁 |
|---|---|---|---|
| C1 | MAS1 换档序列 | `swirls` 表 = 1 个值 `7`；`reroll_numbers.md` §1.1 = `7/10/20/30/50/75/120` | **表**（原文自己也标了"序列常超出上限，更像全局档位阶梯的切片"） |
| C2 | 时限单位 | 4（若分钟=240 s） vs 未知 | **不取**：`timer_seconds=null` + `unresolved` |
| C3 | `GAME_TIME` 教程 90 | 若分钟=1.5 h，不合理；像哨兵 | 同上 |
| C4 | 物件名 | `1438 表` 的 `name` 在 dump 里其实是**内部名**（`HUSUMA01_E`）；英文名要经 `all objects` 连接 | 已按 `internal name` 连接，`name` 存英文名 |
| C5 | `DummyProps[DUMMYHIT*]` | 有 `Req Pickup Size`（1727 mm）但没有形状，是碰撞代理不是可收集件 | **保留并标 `kind=dummy_hit`**；任何"物件数"统计先剔除 |
| C7 | 区域拆分只有 **5/19 关**可用 | `zone_stats` 的 `has_growth_bands`：MAS4/7/8/9/Moon；其余关（含 MAS2/MAS5，`AREA_NUM`=2）全部实例都记在 loading area 0 | 只用有成长带的关做分布趋势；单 area 关（MAS1/Cancer）只做"区域 0"的横向对比，不做衰减曲线 |
| C8 | `AREA_NUM` ≠ 加载区数，且 mission id 与名字**对调** | `sim_missions`：`Make a Star 3` 是 id 4、`Make a Star 4` 是 id 3 | 一律用**名字**连接（`missions.Note`），不用 id |
| C9 | 背景景物混在实例里 | 最多 51%（MAS4 area 3）/45%（Moon area 4）的 `Req > CLEAR_DIAMETER`，永不可吃 | 它们会抬高"最小件/中位件"这类统计；本文的 `never_eatable_scenery` 列已单列（MAS1 23.6%、Cancer 24.7%） |
| C6 | 坐标 frame | 实例坐标是 area 局部；`sim missions.initPos` 是另一个 frame（MAS1 spawn (22.7,23.5,−28) 在物件云外） | 只搬**相对布局**，原点自定；spawn 单独记 |

### 6.3 已知缺口（要等/要买）

1. **旋转与缩放**：dump 没有（F7）。→ 重建必须自定朝向；`ref-level-builder` 的 placement 里应写"朝向由我们选"。
2. **area≥1 的坐标**：13/32 个 area 全零。→ 多 area 关只能做 B 档审计。
3. **物理常数**：无公开来源（F9）。→ `M01`/`M02`/`M06` 永远是"原作未知"；除非用户愿意在自己机器上跑一次运行时 hook（本轮禁区）。
4. **`pathSpeedIndex` / vault 点的语义**：字段存在、取值存在（`pathSpeedIndex` 11 个取值，`2` 占 80.2%，含 1 个坏值 `t`；vault 点 0/2/3/8 个），但"index 2 是什么意思"未知——只能判它是**速度档**（`[推断]`，原版没给单位）。→ `C04` 只报"有这些字段"，不报"有 574 个移动件"。
5. **随机位置组**：`position=0` 的行属于 `randomLocGroup + spawn chance(x/256)` 表（202 行），本语料未展开。→ 多 area 关的实例数会低估。
6. **`Req Pickup Size` 的 mm 取整**：原作门槛取整，我们的 `D_req` 不取整；语料里存原值（mm），比较时用浮点。差 <1 mm。

### 6.4 本表**不做**的（Unity-only，另立）

| 量 | 为什么不在本表 | 在哪 |
|---|---|---|
| 每步/每帧成本、`GetLastStepMicros` | 只有 profiler 能给，Python 模型没有 | `PHYSICS_TEST_FIELD.md` §6.4（`BLOCKED_UNTIL_P4`） |
| 手感（转向、刹车、爬墙"难受"程度） | 需要人 + 实机；原作无对照数值 | 沙盒（`CONTACT_MODEL_V2.md` §6.5） |
| HUD 排版、相机遮挡、观感 | 表现层，不进差分 | `Docs/Design/UI/`、`FollowRig` |
| 每帧最多吸收事件数、吞吐 | 离线模型有（3 件/s，`round_model.py:152`），但**不是实测** | `Docs/Design/Gameplay/` |

---

## 7. 来源

| # | 来源 | 用途 | 级别 |
|---|---|---|---|
| S1 | `/home/fulutang/KatamariRef/meta/DATAMODEL_NOTES.md` + `mas1_layout.csv` | 位置/尺寸/列名/单位互证；area 0 的局限 | 数据挖掘 |
| S2 | `/tmp/orig_physics/reroll_numbers.md` §1–§4 | 关卡参数、swirl、实例密度、增长曲线、30 Hz | 数据挖掘/源码 |
| S3 | `/tmp/orig_physics/reroll_datamodel.md` §0–§4 | 引擎、`DoTick`、`GetPropMatrixFromSimulation`、whirlpool | 源码 |
| S4 | `/tmp/orig_physics/report.md` §10–§11 | 视觉核心、贴附位姿、本轮补充数据 | 混合 |
| S5 | `REROLL Dump` 20 表（`/tmp/orig_physics/sheets/`） | missions / object dump / all objects / name_props / swirls | 数据挖掘 |
| S6 | `raw_growth_mechanism.txt`（Google Doc，50 行） | 体积→直径、10× 门、分段回收、群组/树惩罚 | 社区实测 |
| S7 | `Docs/Design/BallMotor/CONTACT_MODEL_V2.md` | v2 接触/爬升/形状（我方设计） | 本项目设计 |
| S8 | `/tmp/ball_audit/report.md`、`/tmp/growth_audit/report.md`、`repricing_proposal.md` | 现状 gap 与数值 | 代码事实 + 计算 |
| S9 | `Docs/Research/KatamariBallRules/`（README、MAP_AUTHORING_DIGEST） | 10× 门、作者规则、矛盾清单 | 混合 |
| S10 | `Docs/Design/Levels/story_01_bench_size/placement.json`、`Docs/Design/GameModes/Levels/story_01_bench_size.json` | 我方关卡参数 | 本项目数据 |

> 本文件与 `run_parity.py` 的一致性由 `Docs/tests/parity/test_run_parity.py::DocumentSyncTest` 守着：表里少写一条指标，测试就红。
