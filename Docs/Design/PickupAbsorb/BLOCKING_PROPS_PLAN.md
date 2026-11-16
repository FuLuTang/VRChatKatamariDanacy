# 挡路道具方案：吸不动的物件要挡住球

> **2026-11-16 门已改**：吸收尺寸门已按原作数字改写——物件体积 `V = k·a·b·c`（`k` 缺省 0.70），门槛 `Req_mm = floor(1000·cbrt(60·V/π))`，可吸 ⇔ `floor(1000·D) ≥ Req_mm`。本文里若出现 `a·b·c ≤ (π/6)·D³/10`、`absorbVolumeRatio`、`maxItemVolumeWorldM3`、「浮点比较、不取整」或按旧门算出的档位件数，那是 2026-11-15 的口径（历史）；现行口径、实现清单与前后实测见 [ABSORB_GATE_ORIGINAL](../../Research/ContentVolume/ABSORB_GATE_ORIGINAL.md)。


> 状态：`IMPLEMENTED_NOT_COMPILED`，2026-11-15。运行期代码已按本方案写完（§5），**没有编译、没有 Play Mode、没有 Build & Test**：作者不是 Unity 操作者。能离线验的只有纯数学（NUnit 与 `Docs/tests`），手感与帧时间仍要 Unity（§6）。
> 触发：2026-11-15 试玩——「太大吸不动的道具没有碰撞，球直接穿过去」。需求：**吸不动 = 挡（或弹），吸得动 = 不挡**。
> 行号口径：**新增与修改的实现说明一律按符号名**，不写行号。§2 B、§4 里保留的少量旧行号是当时核查的记录（`SweepMath.cs` 本轮未改，`KatamariBallMotor.cs` / `PickupFieldHost.cs` / `PickupAbsorbMath.cs` 都已改过，其旧行号失效）。§0.6 是实现后的现状，其余章节保留决策理由，与现状不一致处以 §0.6 与 §5 为准。
> 引用约定：`Runtime/…`、`Labs/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；其余相对仓库根。

## 0. 结论

1. **推荐 A：动态挡路槽。** 宿主每次扫描（0.05 s）选出「当前吸不动 + 离球最近」的少量道具，**整批覆写**电机里的挡路盒槽（不是增删）。不新增任何碰撞数学：`SweepMath` 本来就在按盒扫掠。
2. **挡路容量与静态障碍预算分开**：新 `MaxBlockingBoxCount = 6`，`MaxPlaneCount = 8` / `MaxBoxCount = 8` 不动 → 地图数据、校验器、现有 lab 全部不改。
3. **只有一条门槛**：`a·b·c > min(maxItemVolumeWorldM3, (π/6)·D³ / absorbVolumeRatio)` 就挡，与吸收门**同一个函数**。开关时刻与吸收门重合，不存在「既不挡、又吸不动」的空窗。（2026-11-15 决定 (l) 把判定量从最长边换成 AABB 体积，本条的函数边界不变、量变了：挡路谓词是 `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(bounds, limitVolumeM3)`。）
4. **原作是弹开还是只停：证据不足（PROVISIONAL）。** 我们选「停 + 沿切面滑」，复用既有墙面响应（`Docs/Design/BallMotor/README.md:205-228`）。不做反弹，也**不做**「撞大件削球/缩小」——研究文档没有确认过这条。
5. 只有「真的挡住、手感、低矮件变整面墙、帧时间、接触饱和」需要 Unity 才能验。
6. **现状（2026-11-15 实现）**：
   - 挡路盒的来源是**玩法体积 `propBounds`**，不是 `BoxCollider`，也不是视觉网格。§5.3 原定的「collider 优先 → 声明覆盖 → propBounds 回退」三级回退**不做**：三层分离是硬约束，而且场景里已有道具根本没有碰撞体，把 collider 变成一条运行期分支只会让同一件道具的挡路盒取决于它有没有挂 collider。
   - 归属：**`PickupFieldHost` 拥有选择**（它已经有道具表、已经有 O(N) 距离扫描），**`KatamariBallMotor` 只提供整批替换的接缝**（`SetBlockingBoxes` / `ClearBlockingBoxes`），**`BlockingPropMath` 只做纯数学**。数据流：`propBounds + 球心 + motor 的体积上限` → 宿主算候选/选最近 N 件 → `motor.SetBlockingBoxes` → `RebuildObstacles` 追加到「平面、静态盒、挡路盒」的切片 → `SweepMath` 当墙处理。
   - 每帧成本：挡路扫描按 `scanIntervalSeconds`（默认 0.05 s，20 Hz）跑，不是每帧。每次扫描 110 件道具各一次 Transform 读取 + 一次平方中心距离；只有落在剪枝界内的少数几件才算体积、建盒、算 AABB 距离；选择本身是 `BlockingPropMath.SelectNearest`，一次校验距离 + 至多 6 轮候选扫描。稳态下**零分配**（数组在 `Start` 一次分配，电机见到同一批盒不重建扫掠数组）。
   - 三个纯数学函数（`TryBoxFromCentreAndExtents` / `TryDistanceToBox` / `SelectNearest`）全部在用，**没有删任何函数**；`Docs/Design/PickupAbsorb/blocking_props.py` 的规则与 tie-break 保持不变。

## 1. 为什么今天是穿过去（代码事实）

| # | 事实 | 证据（符号名，行号会漂） |
|---|---|---|
| F1 | 扫掠只认调用方给的平面/盒，容量 8 平面 + 8 盒，每步接触 4；**另加**独立的挡路切片 6 盒 | `KatamariBallMotor`: `MaxPlaneCount` / `MaxBoxCount` / `ContactCapacity` / `MaxBlockingBoxCount` |
| F2 | 超出容量的静态注册**直接丢弃并 latch**（不排队、不扩容）；挡路盒超容量是**另一个计数器**，不 latch | `AddPlane`/`AddBox` → `SignalOverflow`；`SetBlockingBoxes` → `RefuseBlockingBoxes` |
| F3 | 静态障碍**没有删单个的 API**，只有整表清空；挡路切片是**整批替换**（`SetBlockingBoxes`） | `ClearObstacles()`、`SetBlockingBoxes()`、`ClearBlockingBoxes()` |
| F4 | 扫掠数组按注册长度重建；**总盒数不变时复用同一对数组**，只有长度变化才 `new` | `RebuildObstacles()` |
| F5 | **（已过期，此处改掉）** 「太大」不看最长边，看**AABB 体积**：`a·b·c > min(maxItemVolumeWorldM3, (π/6)·D³ / absorbVolumeRatio)` 才算吸不动 | `PickupAbsorbMath.TryComputeItemVolume` / `IsVolumeBeyondLimit` / `TryComputeAbsorbVolumeLimit` / `IsBeyondAbsorbVolumeLimit`；决定见 `Docs/OPEN_DECISIONS.md` (l) |
| F6 | 电机里被拒的候选**留在队列里不丢**（队列 4 格）；所以宿主只提交过得了尺寸门的 | `CandidateQueueIdempotencyTests`；`PickupCandidateQueueTests` |
| F7 | 宿主每 0.05 s 全表扫一遍，按距离取最近若干件、且过得了尺寸门的提交 | `PickupFieldHost.ScanProps` / `PickupCandidateSelection.OfferProp` |
| F8 | 宿主持有每件的 pickup 尺寸（`propBounds`）与可选的 collider 数组；**挡路只用 `propBounds`，collider 数组与挡路无关** | `PickupFieldHost` 的序列化平行数组 |
| F9 | 球是**纯 kinematic**：全仓 C# 无 `Rigidbody`/`CharacterController`，位置只由扫掠写 | `KatamariBallMotor.StepBall` |
| F10 | 现有 lab 的静态盒占用：StoryLevel 4 盒、CpuSkirmish 4 盒、BallPlayground **7 盒**（BallPlayground 已于 2026-11-15 退役、场景已删）。挡路盒有自己的 6 格，所以这些数字不受影响 | 三个 lab 的 `.unity` 里 `obstacleBoxes` |
| F11 | 一件道具一行：两关共用场 110 件 / 412 单位；区域图 175 件 | `Docs/Design/StoryMode/LEVEL_CONTENT_PLAN.md`、`REGION_MAP_PLAN.md` |

F9+F5 合起来就是原来的症状：吸收门拒了它，扫掠却从来不知道它存在。现在宿主把「体积门说吸不动」的那些件塞进挡路切片，症状消失。

## 2. 四个候选

### A. 动态挡路槽（推荐）

宿主每次扫描把「吸不动且够近」的道具 AABB 整批写给电机，电机把它们接在静态盒之后扫掠。要点：

- **整批覆写而不是 add/remove**：`SetBlockingBoxes(minima, maxima, count)` 一次调用替换整个挡路切片。既绕开 F3（没有删单个），也避免每帧 2N 次调用与 N 次重建。
- **谁选**：宿主，因为它已经有全部行（F7/F8），且已经在做 O(N) 距离扫描；选择规则是纯数学，可离线测（§6）。
- **度量用「球心到 AABB 的距离」而不是中心距**：3 m 长的长凳中心可能在 1.6 m 外，面却在 0.1 m 外。
- **半径 = 碰撞半径 + 余量**（余量覆盖一次扫描的行走距离：4 m/s × 0.05 s = 0.2 m，取 0.8 m 留档位/帧率余量）。
- 新增代码只有「选择」这一件事；碰撞响应、分离、面法线定向、去速全部复用现有的。
- **粘性**（`blockingStickinessM = 0.25`）：上一批仍在 `半径 + 粘性` 内的件优先保留，避免球停在边缘时盒子每 0.05 s 增删一次。它只是选择上的迟滞，不是第二个门槛。
- **剪枝界**（实现新增）：宿主先用「中心距 ≤ 半径 + 粘性 + 半对角线」跳过远处的件再建盒。这是保守的**剪枝**，不是判定：点到 AABB 的距离 ≥ 中心距 − 半对角线，所以被剪掉的件 `SelectNearest` 本来也不会保留，结果逐位相同。它的作用是让 110 件里每次只对近处几件做建盒和 AABB 距离。
- **不用静态盒的「剩余槽」**（StoryLevel 余 4；BallPlayground 的「余 1」随该 lab 于 2026-11-15 退役删除，F10）：那条路必须靠 `ClearObstacles()` 再重注册才能换掉某几格，会连带把 lab 的墙一起清掉（F3），而且挡路容量会随地图的静态盒预算浮动。独立容量让地图数据完全不动。

### B. 抬高电机容量（只改常量）

成本可以算：扫掠对障碍数**线性**——校验全表 `SweepMath.cs:120-140`，`TryFindEarliestHit` 全表 `:555-575`，接触投影 `ProjectionPasses = 4` × 全表 `:52`、`:187-198`，外层最多 `maxContacts` 次（= `ContactCapacity` 4）。每固定步一次、50 Hz（`KatamariBallMotor.cs:71-72`、`:520-532`）。从 16 个障碍抬到 22 个约 +37% 最坏情况、实际场（1 平面 + 4 墙 + 6 挡路 = 11）翻倍，而扫掠数组本身只有 4 × 22 个元素——**CPU 不是瓶颈**（Udon 解释器开销未测，见 `REGION_MAP_PLAN.md:117`）。

真正的否决理由有两条：① **容量不是答案**——110–175 件（F11）不可能全注册，无论抬到多少都要「选哪些」，所以 B 仍然需要 A 的选择规则；② 8/8 是**地图作者的数据契约**（`REGION_MAP_PLAN.md:67`，校验器把同一常量抄成 `MOTOR_MAX_COLLISION_BOXES = 8`，`Tools/mapbuild/validate_map.py:149-150`），改常量要连带改校验器与两张图的盒预算，换来的只是「窗口大一点」。而且 BallPlayground 曾经 7/8（F10，该 lab 已于 2026-11-15 退役删除），抬静态容量等于让 lab 之间的预算重新分配。

### C. 宿主/另一行为里自带一套轻量「道具挡板」扫掠

否决，理由是它必然**重复扫掠逻辑**：球-扩张 AABB 的 TOI、初始穿透分离、多接触投影、面法线定向、法向去速——这五件事现在各只有一份（`SweepMath.cs` 全部，`TryCollectWallContactNormals`/`TryRemoveWallNormalVelocity` 在 `:359-380`）。第二套实现会产出第二份「球在哪」的权威，两个系统写同一个 `ballCenterWorld`；这同时违反 `AGENTS.md`「一份逻辑一个实现」与装配层「只搬运、不实现数学」（`Docs/Design/BallMotor/README.md:13`）。而且它并没有更便宜：复用 A 的容量方案后，A 的增量成本就是一次数组拷贝。

### D. Unity 物理碰撞体

否决。① 球没有 `Rigidbody`/`CharacterController`（F9），它是靠写 Transform 移动的 kinematic 体；加 collider 不会自己产生响应。② 要让 PhysX 参与就得把球改成动态刚体，那等于把位置/速度交给引擎，与固定步长、账本驱动尺寸、视觉滚动用实际位移这几条既有契约冲突（`KatamariBallMotor.cs:71-72`、`:520-532`、`Docs/Design/BallMotor/README.md:15-30`）。③ 只做查询（`ComputePenetration`/`OverlapBox`）的话，U# 3.10.5 是否暴露这些 API **本检出无法确认**（VPM 包不在仓库，处理方式同 `Docs/Design/BallMotor/README.md:98-99` 对 `VRCInput` 的做法），而且还要给 110 件道具挂动态 collider 与玩家/avatar 互相作用。④ 物理回调在客户端各跑各的，正是「碰撞不决定玩法」要躲的东西。

| 方案 | 新碰撞数学 | 地图/校验器改动 | 能覆盖全部道具 | 判词 |
|---|---|---|---|---|
| **A 动态挡路槽** | 无 | 无 | 是（每扫选取） | **推荐** |
| B 抬容量 | 无 | 常量 + 校验器 + 两图预算 | 否（仍需选择） | 只作为 A 的容量旋钮 |
| C 宿主自带扫掠 | **整套复制** | 无 | 是 | 违反一份实现 |
| D Unity 物理 | 换成 PhysX | 无 | 是 | 与 kinematic 契约冲突，且 API 不可确认 |

**选 A。** 它把「哪些东西挡路」交给唯一拥有全表数据的一方（宿主），把「怎么挡」留给唯一实现扫掠的层（`SweepMath`），中间只多一个整批替换的接缝。

## 3. 门槛两侧：增长、缩小、重试

- **球长大跨过某件的门槛**：吸收提交 → `RecomputeConfirmedAndSize`/`DeriveSize` 重算直径 → `GetAbsorbVolumeLimitWorldM3()` 变大 → 下一次宿主扫描该件不再满足 `IsBeyondAbsorbVolumeLimit` → 挡路集少一个 → 盒消失。现在是**≤1 个扫描间隔**（0.05 s），不是「1 帧」，因为挡路扫描和候选扫描用同一档 `scanIntervalSeconds`。此刻球正贴着它，reach 判定**含等号**（`IsVolumeBeyondLimit` 是严格大于；`EvaluateAbsorb` 的距离判定是 `<=`），所以同一扫描或下一次就被吃掉。观感是「顶住 → 吞进去」，不需要额外规则。
- **缩小（交分）反向**：`TryBank` 缩球（`Docs/Design/BallMotor/README.md`）→ 上限变小 → 该件回到候选 → 下一次扫描重新注册。风险：若球此刻与它的**真实 AABB** 重叠（薄长件，如 3.2 m 长凳；或低矮的 `napkin`），扫掠的初始穿透分支会把球推出（`Docs/Design/BallLayers/Sweep/README.md`），可能是一次 ~0.2 m 的位移。第一版接受并记录，试玩后再决定是否跳过「球心已在盒内」的件。
- **重试 / 重生**：挡路集是**球径派生量**，不是场景几何，所以复位必须清挡路槽。实现的实际调用点有三处，都是同一个方法 `ClearBlockingBoxes()`：
  1. `TeleportTo`（`TryResetForNewRound` 通过它落地）——否则会把球传进旧盒子里被推出；
  2. `ClearObstacles`——清「所有障碍」的语义必须含挡路切片；
  3. 宿主 `ResetField()`——它同时把自己的 `previousBlockingCount` 与扫描计时清零，下一次扫描立刻重建。
  这与「已注册的平面/盒子刻意不复位」（`Docs/Design/BallMotor/README.md`「吸收事件环的绝对计数保持单调」一节旁的复位约定）不冲突：静态几何照样保留，挡路切片在下一次扫描前为空。
- **时间误差**：宿主每帧消费吸收事件，但 Unity 不保证宿主 `Update` 早于电机 `Update`。两个方向都可能差**一个扫描间隔**：吃掉后多挡 ≤0.05 s（球刚好进不去，无观感），缩小后晚 ≤0.05 s 才挡（可能多走一小步）。接受。
- **回合停止吸收（`SetCollecting(false)`）不影响挡路**：世界仍然实心。实现上 `Update` 先刷新挡路集、再走 `Collecting()` 与 `attachmentRoot` 两道早退，就是为了这个。

## 4. 原作怎么处理「撞到吸不动的东西」

- **弹开还是只停：`unconfirmed`。** 研究文档里唯一与「撞」有关的确认项是**两个球**同尺寸相撞互相弹开（`Docs/Research/KatamariBallRules/README.md:93` C9），以及「缩小后吃不动大件」（`:89` C5）、「被更大的球吞掉」（`:90` C6）。**球撞道具**的响应不在任何一条 claim 里，也不在「仍然未知」清单里（`README.md:108-115`）——不是没查到，是这一轮没问。
- **我们的选择（PROVISIONAL）：停 + 沿切面滑，不反弹。** 理由不是还原原作，而是**一致性**：挡路道具与墙在玩家眼里是同一类东西，墙已经是「只去掉指向障碍的法向速度、保留切向」（`Docs/Design/BallMotor/README.md:205-228`，实现 `SweepMath.cs:359-380`）。给道具单独加回弹就要新定弹性系数、新定「多小才算弹」，而现在没有任何证据支持那套参数。
- **不写「撞到吸不动的物件会削球」。** 研究文档只确认了「交分换分、球按比例变小」（C2/C4，`README.md:85-88`），从未提到碰撞会缩球。这条如果以后要做，必须先有证据，且它会把「缩小」从玩家主动行为变成被动惩罚，属于玩法变更，不在本方案内。
- 一并注意：原作「关卡里有尺寸带、小球吃不动大件」是**我们的设计前提**（`REGION_MAP_PLAN.md:29`），不是本方案新引入的。

## 5. 接口与改动

### 5.1 纯数学（已存在，全部在用）

1. `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(Vector3 itemBounds, float limitVolumeWorldM3)`——吸收门与它共用 `TryComputeItemVolume` + `IsVolumeBeyondLimit`，同一次比较。**「太大」只有这一份定义**，宿主用它做挡路筛选，不会和门漂移。
2. `Runtime/Pickup/BlockingPropMath.cs`（`public static`，无状态、不分配）：
   - `TryBoxFromCentreAndExtents(Vector3 centre, Vector3 extents, out Vector3 min, out Vector3 max)`
   - `TryDistanceToBox(Vector3 point, Vector3 min, Vector3 max, out float distance)`（盒内 = 0；也被 `SelectNearest` 内部使用，所以距离只有一份算法）
   - `SelectNearest(Vector3 ballCentre, float radiusM, Vector3[] minima, Vector3[] maxima, bool[] candidate, int count, int[] previousIndices, int previousCount, float stickinessM, int[] outIndices, float[] outDistances, int capacity)` → 返回写入条数（`-1` = 拒绝）。规则：候选 + 半径内；上一批仍在 `radiusM + stickinessM` 内的优先保留；其余按 AABB 距离升序、等距按下标升序（稳定）；夹到 `capacity`。

**没有删任何函数**：设计从「collider 回退」改成「`propBounds` 唯一来源」只删掉了宿主里的一条分支，纯数学三个函数的契约不变，`Docs/Design/PickupAbsorb/blocking_props.py` 的参考模型与 tie-break 继续一一对应。

### 5.2 电机（`KatamariBallMotor.cs`，只加接缝）

```csharp
private const int MaxBlockingBoxCount = 6;   // 与 MaxPlaneCount / MaxBoxCount / ContactCapacity 同区

public bool   SetBlockingBoxes(Vector3[] minima, Vector3[] maxima, int count); // 整批替换
public void   ClearBlockingBoxes();
public int    GetBlockingBoxCount();
public int    GetBlockingBoxCapacity();      // 6
public int    GetBlockingBoxRefusalCount();
public string GetBlockingBoxRefusalReason(); // 空串 = 从未拒绝
```

- 语义：整批替换；**失败保持上一批**；容量/非法输入/倒置盒/非有限值各自计数并把原因写成**字面量**（不拼字符串，所以拒绝路径也不分配），**不进 `overflowCount` latch**（那个信号的含义是「场景接错」，不是「这一帧道具多」）。
- 同一批盒重复写入直接返回 true，不重建扫掠数组——这是 20 Hz 宿主扫描稳态零分配的关键。
- `RebuildObstacles()` 改成「平面 + 静态盒 + 挡路盒」。数组**按总长度复用**：总盒数不变就原地覆写，只有长度变化才 `new` 一对数组。**数组长度必须等于真实条数**：`SweepMath` 用数组长度当障碍数，留空槽会变成原点处的幽灵盒。
- 障碍索引空间仍是「平面在前、盒在后」，挡路盒追加在静态盒之后；`SweepMath` 只用 `planeNormals.Length` 区分平面/盒，所以挡路盒天然走盒分支，面法线定向与去速**一行都不用改**。
- 清挡路槽的调用点：`TeleportTo`、`ClearObstacles`（`TryResetForNewRound` 通过 `TeleportTo` 落地，不需要单独改）。

### 5.3 宿主（`PickupFieldHost.cs`，拥有「哪些道具挡路」）

- 新序列化：`blockingDisabled`（默认 false，即启用）、`blockingMarginM = 0.8`、`blockingStickinessM = 0.25`。开关**取反命名**是刻意的：新字段在已经序列化的场景里可能以 CLR 默认值 `false` 出现，而 `false` 必须等于「挡路生效」，否则这个功能会因为一次陈旧序列化静默失效——症状正好就是本文要修的那个 bug。**没有 `propBlockingBounds` 覆盖项**：挡路盒只由 `propBounds` 决定。
- `Start()`：`PrepareBlockingRows()` 为每件道具定一次「能否挡」（有 Transform、`propBounds` 有限且三轴 > 0）与半对角线。**不看 collider**：`propColliders` 只用于 attach 时关掉碰撞，与挡路无关；19 件没有碰撞体的道具和别的一样挡。
- `Update()` 顺序：`ConsumeAbsorbedEvents()` → `UpdateBlockingProps()` → `Collecting()` 早退 → `attachmentRoot` 早退 → `ScanProps()`。挡路在两道早退之前，所以回合停止吸收、或 attachmentRoot 没接时，世界仍然实心。
- `UpdateBlockingProps()`（每 `scanIntervalSeconds` 一次，默认 20 Hz）：
  1. 读 `motor.GetAbsorbVolumeLimitWorldM3()`；拒绝（负值/非有限）时**清空挡路集**（与门对非有限上限的「全拒」一致）。
  2. 球心、半径 = `motor.GetCollisionRadiusWorldM()` + `blockingMarginM`。
  3. 逐件：`StateFree` && `blockingUsable` && 中心距在「半径 + 粘性 + 半对角线」内（剪枝）&& `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit(propBounds[i], limit)`；通过者用 `TryBoxFromCentreAndExtents(prop.position, propBounds[i])` 建盒。
  4. `BlockingPropMath.SelectNearest(...)`，容量 = `motor.GetBlockingBoxCapacity()`；结果拷进两个输出数组后 `motor.SetBlockingBoxes(...)`。
  5. 成功后交换上一批索引数组（下一轮用它做粘性），并记 `blockingAppliedCount`。
- 失败语义：选择被拒或电机拒收 → **保留上一批**，只记一次警告；不清空（空集正是本方案要消灭的 bug）。
- `ResetField()`：`motor.ClearBlockingBoxes()`，`blockingAppliedCount` / `previousBlockingCount` / `blockingCandidateCount` 归零，`nextBlockingScanTime = 0`（立刻重扫）。
- `SetCollecting(false)`：只撤候选，**不动挡路**。
- 新读取器（HUD/测试用）：`GetBlockingPropCount()`、`GetBlockingCandidateCount()`、`GetBlockingPropCapacity()`、`IsBlockingEnabled()`。电机侧还有拒绝计数与原因。
- **单一所有者**：一个电机只能有一个挡路写入者；两个 field 宿主会互相覆写（最后写的赢）。这是编辑器侧的接线审计项，运行期没有仲裁者。

## 6. 验证分工

**纯数学（不需要 Unity，NUnit 与 `Docs/tests` 都能表达）**——状态见每条的括注。

1. AABB 距离：大件的近面 vs 小件的中心，选面更近的那个。（`BlockingPropMathTests.NearFaceOfALongPropBeatsTheCentreOfASmallOne`）
2. 球心在盒内 → 距离 0，排第一。（`BallInsideABoxIsDistanceZeroAndRanksFirst`）
3. 距离相等 → 按下标升序，两次调用结果一致。（`EqualDistancesBreakByAscendingIndexAndAreStable`）
4. 粘性：新件只近一点点时保留旧件；超出粘性半径才换（防抖）。（`StickinessKeepsAPreviousPropUntilTheNewOneLeavesTheStickyBand`）
5. 容量夹取：`capacity = 0`、候选多于容量、`count` 为 0。（`CapacityClampAndEmptyInputs`、`OnlyTheNearestCapacityPropsBlock`）
6. 非有限/退化输入 → 拒绝且不写输出。（`NonFiniteOrInvertedInputIsRefused`）
7. 边界含等号：`volume == limit` → **可吸、不挡**。（`VolumeEqualToTheLimitIsAbsorbable`）
8. 增长跨门槛：同一函数两处调用结果必须相同；挡路侧用 `IsBeyondAbsorbVolumeLimit` + `SelectNearest` 断言「盒消失」。（`GrowthCrossingTheLimitFlipsTheSameFunction`、`GrowingPastTheVolumeLimitRemovesTheBoxInTheSameCall`）
9. 缩小反向：纯重算，无残留状态。（`ShrinkingRecomputesWithNoResidualState`）
10. 半径外不选（行走预算）+ 半径边缘的含/不含。（`OutsideTheRadiusIsNotSelectedAndTheBoundaryIsInclusive`）
11. **无碰撞体的道具照样挡**：盒由 `propBounds` 与位置构造，构造函数的签名里没有 collider。（`APropWithoutAColliderBlocksFromItsGameplayBounds`）
12. **已吸附的不挡**：候选标记为 false，结果集里就没有它（宿主用它代替「单独注销一件」）。（`AnAbsorbedPropIsNotABlockingCandidate`）

**必须 Unity（还没做）**：真的停住/沿墙滑（不是「位置对但速度还在」）、接触缓冲饱和（`ContactCapacity = 4`，盒变多后墙角更容易饱和，信号是 `WasContactBufferSaturated()`）、吃掉后一个扫描间隔内盒消失、重试/重生后重新挡住、低矮件手感、6 个挡路盒的帧时间、`SetBlockingBoxes` 稳态不分配（只能靠 Profiler，不能靠 EditMode）。

## 7. 构建顺序（每步独立可验）

| 步 | 做什么 | 验收 |
|---|---|---|
| 步 | 做什么 | 验收 | 状态（2026-11-15） |
|---|---|---|---|
| S1 | 本方案（决定：A、独立容量、单门槛、不反弹不削球） | `python3 Docs/tools/validate_plan.py`、`python3 -m unittest discover -s Docs/tests` | 设计已定 |
| S2 | `Docs/Design/PickupAbsorb/blocking_props.py` 参考模型 + `Docs/tests/pickup_absorb/` 用例（§6 的 1–10） | 只跑 `python3 -m unittest discover -s Docs/tests`，不需要 Unity | 已存在 |
| S3 | `PickupAbsorbMath.IsBeyondAbsorbVolumeLimit`，门改调它；NUnit 回归 | MCP `run_tests`：`AbsorbSizeGateTests` 等**一行不改**仍全绿 | 已存在（未在本检出跑过） |
| S4 | `BlockingPropMath` C# 移植 + NUnit | `run_tests` 全绿；与 S2 同用例同结果 | 已存在，本轮补了 4 条接线契约用例 |
| S5 | 电机：容量/缓冲/六个 API/合并重建/复位与传送清理 | EditMode：容量与拒绝计数、`count = 0` 时行为与今天**完全一致**（StoryLevel 仍是 1 平面 4 盒、无 overflow） | **本轮已写，未编译** |
| S6 | 宿主：`propBounds` 建盒 + 第二遍选择 + 写入 + 与 collecting 解耦 | Play Mode：0.30 m 球撞 `bread_slice_01` 停住；D ≈ 0.80 m 后吞掉并能过 | **本轮已写，未编译、未试玩** |
| S7 | 重试/重生：`TeleportTo` / `ClearObstacles` / `ResetField` 都清 | 日志：重试后挡路盒数回到「当前球径应有的数」；无残留盒 | **本轮已写，未编译** |
| S8 | 手感与帧时间实测（记录 `Docs/Evidence/`） | 记录：挡路盒 0 / 3 / 6 时帧时间、低矮件观感、墙角切向速度 | 待 Unity |

顺序约束：S2→S4（同一组用例）、S3 与 S4 可并行、S5 只依赖 S3、S6 依赖 S4+S5。

## 8. 风险

| # | 风险 | 处理 |
|---|---|---|
| R1 | **低矮件变成整面墙**：扫掠是保守的扩张 AABB，没有上台阶/翻越（`Docs/Design/BallLayers/Sweep/README.md`），0.028 m 厚的 `napkin_01` 对 1.2 m 球也是不可通过 | 第一版接受（「吸不动就过不去」本身自洽）；若观感不佳，旋钮是「顶面低于某系数 × 球径就不挡」，但那会变成「穿过去」，需要试玩决定 |
| R2 | 旋转件的世界 AABB 比本体大 → 提前挡住（看不见的墙） | 记录；实现不读旋转，只按 `propBounds` 轴对齐建盒，所以「提前」的程度有上界（≤ 该件包围盒与本体之差）；不做 OBB |
| R3 | **接触饱和**：盒变多后 `ContactCapacity = 4` 更容易满，去速不完整（`Docs/Design/BallLayers/Sweep/README.md`、`Docs/Design/BallMotor/README.md` 已记这条缺口） | 读 `WasContactBufferSaturated()`，墙角卡顿时优先怀疑这里 |
| R4 | 交分缩小时与薄长件重叠 → 被初始穿透分支推出 | 先接受并实测位移；必要时跳过「球心在盒内」的件 |
| R5 | 多宿主互相覆写挡路集 | 单一所有者规则 + **编辑器侧审计**；实现里**没有**运行期第二次写入警告（电机认不出调用者），别指望日志能发现 |
| R6 | ~~每次重建分配数组~~（已解决） | 总盒数不变时复用同一对数组；同一批盒重复写入不重建。只有**盒数变化**时才 `new` 一对数组，且只在扫描时刻发生。Udon 分配代价仍未测 |
| R7 | 数据侧的 `blocks_ball_diameter_below_m`（`Docs/Design/Maps/PicnicLawn/layout.json` 的 `size_gates`）与本方案的单门槛**语义重叠但不被运行时读** | 本方案让它变成纯作者注记：挡到能吸为止（例如 `bread_slice_01` 是 D ≈ 0.80 m），比 1.05 更保守、没有空窗。要不要接成「提前放行」由 Lead 决定（§9） |
| R8 | 挡路让某些作者路线变得不可通行（小球的密集区） | 现状的绕法（大件放主路两侧，`REGION_MAP_PLAN.md` G6）不再是必需，但布局仍应按尺寸带摆放 |
| R9 | 挡路集**每 0.05 s 才刷新一次**，不是每帧 | 余量 0.8 m 覆盖 4 m/s × 0.05 s = 0.2 m 的行走距离，并留了 4 倍余量；球被 `maxSpeedMps` 限制，扫描间隔可调 |
| R10 | 挡路盒是玩法体积的 AABB，可能比视觉网格大（`thermos_01` 的 `0.9 m` 是整件包围盒） | 这是「三层分离」的代价，不是 bug；视觉上顶住的位置会比模型早一点，试玩决定是否逐件收紧 `propBounds` |

## 9. 未决 / 不要当事实用

1. **原作球撞道具是停还是弹**：`unconfirmed`，本方案按「停 + 滑」实现，属 PROVISIONAL。
2. **挡路容量 6**：按「球周围同时能贴到几件」估的，不是测量值；S8 后按实测调（它是电机里的一个常量 `MaxBlockingBoxCount`，改动只影响电机；宿主的选择缓冲是 8，够用）。
3. **`blocks_ball_diameter_below_m` 是否接进运行时**：接就是第二个门槛（`Docs/OPEN_DECISIONS.md` 明确警告过第二阈值），不接就多一个没人读的字段。建议不接。
4. **`blockingMarginM = 0.8`、`blockingStickinessM = 0.25`** 都是估值，没有实测；它们是宿主上的序列化字段，可以按 lab 调。
5. **没有挡路盒的调试可视化**：验收只能靠「球被挡住」和 `GetBlockingPropCount()` / 启动那一行 `blocking boxes=…` 日志。需要的话下一步在编辑器侧画 Gizmo，不要做成运行期组件。
6. **`SetCollecting(false)` 之后挡路仍在刷新**，所以「暂停回合」的世界仍然实心；如果哪天要做「暂停时球冻住但世界变透明」那类表现，不要从这里改，那属于表现层。
7. 本文档 §0–§4 记录决策理由，§0.6 与 §5 是实现后的现状；两者冲突以 §0.6 / §5 为准。所有米数都是本项目暂定值，不是原作测量值。
