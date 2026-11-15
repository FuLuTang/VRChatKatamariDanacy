# 挡路道具方案：吸不动的物件要挡住球

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-15。只读设计；作者不是 Unity 操作者，没有编译、Play Mode 或 Build & Test。
> 触发：2026-11-15 试玩——「太大吸不动的道具没有碰撞，球直接穿过去」。需求：**吸不动 = 挡（或弹），吸得动 = 不挡**。
> 行号口径：`KatamariBallMotor.cs` 与 `PickupAbsorbMath.cs` 的行号已按 **HEAD `5768df9`**（2026-11-15 02:07 UTC）逐条复核；该提交自述「not yet compiled」，且写作期间这两个文件在 4 分钟内漂过 ~9 行，所以引用前仍请按**符号名**复核。`PickupFieldHost.cs`、`SweepMath.cs` 自 2026-11-15 00:05 起未变。
> 引用约定：`Runtime/…`、`Labs/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；其余相对仓库根。

## 0. 结论

1. **推荐 A：动态挡路槽。** 宿主每次扫描（0.05 s）选出「当前吸不动 + 离球最近」的少量道具，**整批覆写**电机里的挡路盒槽（不是增删）。不新增任何碰撞数学：`SweepMath` 本来就在按盒扫掠。
2. **挡路容量与静态障碍预算分开**：新 `MaxBlockingBoxCount = 6`，`MaxPlaneCount = 8` / `MaxBoxCount = 8` 不动 → 地图数据、校验器、现有 lab 全部不改。
3. **只有一条门槛**：`extent > min(maxItemSizeWorldM, 语义直径 × absorbSizeRatio)` 就挡，与吸收门**同一个函数**。开关时刻与吸收门重合，不存在「既不挡、又吸不动」的空窗。
4. **原作是弹开还是只停：证据不足（PROVISIONAL）。** 我们选「停 + 沿切面滑」，复用既有墙面响应（`Docs/Design/BallMotor/README.md:205-228`）。不做反弹，也**不做**「撞大件削球/缩小」——研究文档没有确认过这条。
5. 只有「真的挡住、手感、低矮件变整面墙、帧时间、接触饱和」需要 Unity 才能验。

## 1. 为什么今天是穿过去（代码事实）

| # | 事实 | 证据 |
|---|---|---|
| F1 | 扫掠只认调用方给的平面/盒，容量 8 平面 + 8 盒，每步接触 4 | `KatamariBallMotor.cs:241-243` |
| F2 | 超出容量的注册**直接丢弃并 latch**（不排队、不扩容） | `:1309-1311`、`:1325-1327`、`:1359-1364` |
| F3 | **没有删单个障碍的 API**，只有整表清空 | `ClearObstacles()` `:1341-1346` |
| F4 | 每次注册都重扫掠数组（`new`），不是预分配复用 | `RebuildObstacles()` `:1392-1404` |
| F5 | 「太大」的判定是**最大全长 > 上限**，上限 = `min(安全上界, 语义直径 × 比例)` | `PickupAbsorbMath.cs:169-170`、`:270-286`（合成在 `:285`），比例见 `Docs/OPEN_DECISIONS.md:39` |
| F6 | 被拒的候选**留在队列里不丢**（队列 4 格） | `CandidateQueueIdempotencyTests.cs`；`Docs/Design/Maps/REGION_MAP_PLAN.md:112` |
| F7 | 宿主每 0.05 s 全表扫一遍，按距离取最近若干件提交 | `PickupFieldHost.cs:101-103`、`:791-849`、`:858-888` |
| F8 | 宿主已经持有每件的 pickup 尺寸与可选的 collider 数组 | `PickupFieldHost.cs:112-120` |
| F9 | 球是**纯 kinematic**：全仓 C# 无 `Rigidbody`/`CharacterController`（grep 0 命中），位置只由扫掠写 | `KatamariBallMotor.cs:582-590` |
| F10 | 现有 lab 的盒占用：StoryLevel 4 盒、CpuSkirmish 4 盒、BallPlayground **7 盒**（只剩 1 格） | `Labs/StoryLevel/StoryLevel.unity:2976-2980`、`Labs/CpuSkirmish/CpuSkirmish.unity:4721-4724`、`Labs/BallPlayground/BallPlayground.unity:2975-2981` |
| F11 | 一件道具一行：两关共用场 110 件 / 412 单位；区域图 175 件 | `Docs/Design/StoryMode/LEVEL_CONTENT_PLAN.md:11`、`REGION_MAP_PLAN.md:44` |

F9+F5 合起来就是症状：吸收门拒了它，扫掠却从来不知道它存在。

## 2. 四个候选

### A. 动态挡路槽（推荐）

宿主每次扫描把「吸不动且够近」的道具 AABB 整批写给电机，电机把它们接在静态盒之后扫掠。要点：

- **整批覆写而不是 add/remove**：`SetBlockingBoxes(minima, maxima, count)` 一次调用替换整个挡路切片。既绕开 F3（没有删单个），也避免每帧 2N 次调用与 N 次重建。
- **谁选**：宿主，因为它已经有全部行（F7/F8），且已经在做 O(N) 距离扫描；选择规则是纯数学，可离线测（§6）。
- **度量用「球心到 AABB 的距离」而不是中心距**：3 m 长的长凳中心可能在 1.6 m 外，面却在 0.1 m 外。
- **半径 = 碰撞半径 + 余量**（余量覆盖一次扫描的行走距离：4 m/s × 0.05 s = 0.2 m，取 0.8 m 留档位/帧率余量）。
- 新增代码只有「选择」这一件事；碰撞响应、分离、面法线定向、去速全部复用现有的。
- **不用静态盒的「剩余槽」**（StoryLevel 余 4、BallPlayground 余 1，F10）：那条路必须靠 `ClearObstacles()` 再重注册才能换掉某几格，会连带把 lab 的墙一起清掉（F3），而且挡路容量会随地图的静态盒预算浮动。独立容量让地图数据完全不动。

### B. 抬高电机容量（只改常量）

成本可以算：扫掠对障碍数**线性**——校验全表 `SweepMath.cs:120-140`，`TryFindEarliestHit` 全表 `:555-575`，接触投影 `ProjectionPasses = 4` × 全表 `:52`、`:187-198`，外层最多 `maxContacts` 次（= `ContactCapacity` 4）。每固定步一次、50 Hz（`KatamariBallMotor.cs:71-72`、`:520-532`）。从 16 个障碍抬到 22 个约 +37% 最坏情况、实际场（1 平面 + 4 墙 + 6 挡路 = 11）翻倍，而扫掠数组本身只有 4 × 22 个元素——**CPU 不是瓶颈**（Udon 解释器开销未测，见 `REGION_MAP_PLAN.md:117`）。

真正的否决理由有两条：① **容量不是答案**——110–175 件（F11）不可能全注册，无论抬到多少都要「选哪些」，所以 B 仍然需要 A 的选择规则；② 8/8 是**地图作者的数据契约**（`REGION_MAP_PLAN.md:67`，校验器把同一常量抄成 `MOTOR_MAX_COLLISION_BOXES = 8`，`Tools/mapbuild/validate_map.py:149-150`），改常量要连带改校验器与两张图的盒预算，换来的只是「窗口大一点」。而且 BallPlayground 已经 7/8（F10），抬静态容量等于让 lab 之间的预算重新分配。

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

- **球长大跨过某件的门槛**：吸收提交 → `DeriveSize()` 重算直径 → `GetAbsorbSizeLimitWorldM()`（`:1647-1651`）变大 → 下一次宿主 tick 该件不再满足 `IsBeyondAbsorbLimit` → 挡路集少一个 → 盒消失（≤1 帧）。此刻球正贴着它，reach 判定**含等号**（`PickupAbsorbMath.cs:169-170`、`:298-300` 的注释与 `:270-286` 的合成），所以同一帧或下一帧就被吃掉。观感是「顶住 → 吞进去」，不需要额外规则。
- **缩小（交分）反向**：`TryBank` 缩球（`Docs/Design/BallMotor/README.md:127-144`）→ 上限变小 → 该件回到候选 → 下一 tick 重新注册。风险：若球此刻与它的**真实 AABB** 重叠（薄长件，如 3.2 m 长凳），扫掠的初始穿透分支会把球推出（`Docs/Design/BallLayers/Sweep/README.md:13`），可能是一次 ~0.2 m 的位移。第一版接受并记录，试玩后再决定是否跳过「球心已在盒内」的件。
- **重试 / 重生**：挡路集是**球径派生量**，不是场景几何，所以 `TryResetForNewRound`（`:1087`）必须清挡路槽——这与「已注册的平面/盒子刻意不复位」（`Docs/Design/BallMotor/README.md:366-372`）不冲突，静态几何照样保留。`TeleportTo`（`:1555`）同样清，否则会把球传进旧盒子里被推出。宿主 `ResetField()`（`PickupFieldHost.cs:276-319`）把自己的 `blockedCount` 归零。
- **一帧误差**：宿主每帧消费吸收事件（`PickupFieldHost.cs:208`），但 Unity 不保证宿主 `Update` 早于电机 `Update`（同文件 `:192-198` 已注明）。两个方向都可能差一帧：吃掉后多挡 ≤1 帧（球刚好进不去，无观感），缩小后晚 ≤1 帧才挡（可能多走一步）。接受。

## 4. 原作怎么处理「撞到吸不动的东西」

- **弹开还是只停：`unconfirmed`。** 研究文档里唯一与「撞」有关的确认项是**两个球**同尺寸相撞互相弹开（`Docs/Research/KatamariBallRules/README.md:93` C9），以及「缩小后吃不动大件」（`:89` C5）、「被更大的球吞掉」（`:90` C6）。**球撞道具**的响应不在任何一条 claim 里，也不在「仍然未知」清单里（`README.md:108-115`）——不是没查到，是这一轮没问。
- **我们的选择（PROVISIONAL）：停 + 沿切面滑，不反弹。** 理由不是还原原作，而是**一致性**：挡路道具与墙在玩家眼里是同一类东西，墙已经是「只去掉指向障碍的法向速度、保留切向」（`Docs/Design/BallMotor/README.md:205-228`，实现 `SweepMath.cs:359-380`）。给道具单独加回弹就要新定弹性系数、新定「多小才算弹」，而现在没有任何证据支持那套参数。
- **不写「撞到吸不动的物件会削球」。** 研究文档只确认了「交分换分、球按比例变小」（C2/C4，`README.md:85-88`），从未提到碰撞会缩球。这条如果以后要做，必须先有证据，且它会把「缩小」从玩家主动行为变成被动惩罚，属于玩法变更，不在本方案内。
- 一并注意：原作「关卡里有尺寸带、小球吃不动大件」是**我们的设计前提**（`REGION_MAP_PLAN.md:29`），不是本方案新引入的。

## 5. 接口与改动

### 5.1 纯数学（新增，可离线测）

1. `PickupAbsorbMath.IsBeyondAbsorbLimit(Vector3 itemBounds, float limitWorldM)`——把 `:169-170` 的比较抽成一个函数，吸收门改为调用它。**「太大」从此只有一份定义**，宿主用它做挡路筛选，不会和门漂移。
2. 新 `Runtime/Pickup/BlockingPropMath.cs`（`public static`，无状态、不分配，遵守 `Docs/Design/CODE_CONVENTIONS.md:24-31`）：
   - `TryBoxFromCentreAndExtents(Vector3 centre, Vector3 extents, out Vector3 min, out Vector3 max)`
   - `TryDistanceToBox(Vector3 point, Vector3 min, Vector3 max, out float distance)`（盒内 = 0）
   - `SelectNearest(Vector3 ballCentre, float radiusM, Vector3[] minima, Vector3[] maxima, bool[] candidate, int count, int[] previousIndices, int previousCount, float stickinessM, int[] outIndices, float[] outDistances, int capacity)` → 返回写入条数。规则：候选 + 半径内；上一批仍在 `radiusM + stickinessM` 内的优先保留；其余按 AABB 距离升序、等距按下标升序（稳定）；夹到 `capacity`。

### 5.2 电机（`KatamariBallMotor.cs`）

```csharp
private const int MaxBlockingBoxCount = 6;          // 紧邻 :241-243 的容量区

public bool SetBlockingBoxes(Vector3[] minima, Vector3[] maxima, int count); // 整批替换
public void ClearBlockingBoxes();
public int  GetBlockingBoxCount();
public int  GetBlockingBoxCapacity();
public int  GetBlockingBoxRefusalCount();
public string GetBlockingBoxRefusalReason();
```

- 语义：整批替换；**失败保持上一批**（不变量 5「失败即不应用」）；容量/非法输入各自计数并记原因，**不进 `overflowCount` latch**（F2 的信号含义是「场景接错」，不是「这一帧道具多」）。
- `RebuildObstacles()` 改为「平面 + 静态盒 + 挡路盒」，并按长度预分配切片（交错数组，`CODE_CONVENTIONS.md:26`）以免每次扫描 `new`（F4）。**切片长度必须等于真实条数**：`SweepMath` 用数组长度当障碍数（`:120-140`），留空槽会变成原点处的幽灵盒。
- 障碍索引空间仍是「平面在前、盒在后」；`SweepMath.TryCollectWallContactNormals` 只用 `planeNormals.Length` 区分平面/盒（`:359-360`），挡路盒天然走盒分支，面法线定向与去速**一行都不用改**。
- `TryResetForNewRound` / `TeleportTo` / `ClearObstacles` 增加清挡路槽。

### 5.3 宿主（`PickupFieldHost.cs`）

- 新序列化：`blockingEnabled`、`blockingMarginM = 0.8`、`blockingStickinessM = 0.25`、`propBlockingBounds`（可选覆盖；空 = 自动）。
- `Start()` 一次性算每件的挡路 AABB（此时还没 attach，collider 仍是作者状态，F8）：`propColliders[i].bounds` → 否则 `propBlockingBounds[i]` → 否则 `position ± propBounds/2`。启动打一行「多少件用 collider / 声明 / 回退」。
- `ScanProps()` 后追加一遍：候选 = `StateFree && submittable && IsBeyondAbsorbLimit(propBounds[i], motor.GetAbsorbSizeLimitWorldM())`——**上限从电机读，宿主不自己乘比例**（`KatamariBallMotor.cs:1647-1651` 就是为「宿主判断值不值得提交」加的）；距离 = `TryDistanceToBox`；`N = motor.GetBlockingBoxCapacity()`；`BlockingPropMath.SelectNearest(...)`；`motor.SetBlockingBoxes(...)`。
- **挡路与 `SetCollecting(false)` 无关**：回合超时只是停止吸收，世界仍然实心（`PickupFieldHost.cs:231-237` 只该撤候选）。
- 挡路段放在「`attachmentRoot == null` 就早退」之前（`:210-216`），否则根没接就整张图穿模。
- **单一所有者**：一个电机只能有一个挡路写入者。多 field 宿主会互相覆写（最后写的赢），用编辑器侧审计保证（同 `Docs/Design/BallMotor/README.md:261` 的做法），运行期第二次写入只打一次警告。

## 6. 验证分工

**纯数学（不需要 Unity，`Docs/tests` + NUnit 都能表达）**

1. AABB 距离：大件的近面 vs 小件的中心，选面更近的那个。
2. 球心在盒内 → 距离 0，排第一。
3. 距离相等 → 按下标升序，两次调用结果一致。
4. 粘性：新件只近一点点时保留旧件；超出粘性半径才换（防抖）。
5. 容量夹取：`capacity = 0`、候选多于容量、`count` 为 0。
6. 非有限/退化输入 → 拒绝且不写输出。
7. 边界含等号：`extent == limit` → **可吸、不挡**（与 `:169-170` 的 `>` 一致）。
8. 增长跨门槛：D = 1.20 挡（extent 1.25）、D = 1.25 不挡；同一函数两处调用结果必须相同。
9. 缩小反向：纯重算，无残留状态（把同一组输入按新 limit 再跑一遍即可断言）。
10. 半径外不选（行走预算）+ 半径边缘的含/不含。

**必须 Unity**：真的停住/沿墙滑（不是「位置对但速度还在」）、接触缓冲饱和（`ContactCapacity = 4`，盒变多后墙角更容易饱和，信号是 `WasContactBufferSaturated()`）、吃掉后一帧内盒消失、重试/重生后重新挡住、低矮件手感、6 个挡路盒的帧时间、`SetBlockingBoxes` 不在每帧分配。

## 7. 构建顺序（每步独立可验）

| 步 | 做什么 | 验收 |
|---|---|---|
| S1 | 本方案（决定：A、独立容量、单门槛、不反弹不削球） | `python3 Docs/tools/validate_plan.py`、`python3 -m unittest discover -s Docs/tests` |
| S2 | `Docs/Design/PickupAbsorb/blocking_props.py` 参考模型 + `Docs/tests/pickup_absorb/` 用例（§6 的 1–10） | 只跑 `python3 -m unittest discover -s Docs/tests`，不需要 Unity |
| S3 | `PickupAbsorbMath.IsBeyondAbsorbLimit`，门改调它；NUnit 回归 | MCP `run_tests`：`AbsorbSizeGateTests` 等**一行不改**仍全绿 |
| S4 | `BlockingPropMath` C# 移植 + NUnit | `run_tests` 全绿；与 S2 同用例同结果 |
| S5 | 电机：容量/缓冲/六个 API/合并重建/复位与传送清理 | EditMode：容量与拒绝计数、`count = 0` 时行为与今天**完全一致**（StoryLevel 仍是 1 平面 4 盒、无 overflow） |
| S6 | 宿主：AABB 采集 + 第二遍选择 + 写入 + 与 collecting 解耦 | Play Mode：0.30 m 球撞 1.25 m 长凳停住；长大到 1.25 m 后吞掉并能过 |
| S7 | 重试/重生：`TryResetForNewRound` + `ResetField` 都清 | 日志：重试后挡路盒数回到「当前球径应有的数」；无残留盒 |
| S8 | 手感与帧时间实测（记录 `Docs/Evidence/`） | 记录：挡路盒 0 / 3 / 6 时帧时间、低矮件观感、墙角切向速度 |

顺序约束：S2→S4（同一组用例）、S3 与 S4 可并行、S5 只依赖 S3、S6 依赖 S4+S5。

## 8. 风险

| # | 风险 | 处理 |
|---|---|---|
| R1 | **低矮件变成整面墙**：扫掠是保守的扩张 AABB，没有上台阶/翻越（`Docs/Design/BallLayers/Sweep/README.md:12-13`），0.3 m 高的件对 1.2 m 球也是不可通过 | 第一版接受（「吸不动就过不去」本身自洽）；若观感不佳，旋钮是「顶面低于某系数 × 球径就不挡」，但那会变成「穿过去」，需要试玩决定 |
| R2 | 旋转件的世界 AABB 比本体大 → 提前挡住（看不见的墙） | 记录；场景里尽量让挡路件轴对齐；不做 OBB |
| R3 | **接触饱和**：盒变多后 `ContactCapacity = 4` 更容易满，去速不完整（`Docs/Design/BallLayers/Sweep/README.md:15`、`Docs/Design/BallMotor/README.md:228` 已记这条缺口） | 读 `WasContactBufferSaturated()`，墙角卡顿时优先怀疑这里 |
| R4 | 交分缩小时与薄长件重叠 → 被初始穿透分支推出 | 先接受并实测位移；必要时跳过「球心在盒内」的件 |
| R5 | 多宿主互相覆写挡路集 | 单一所有者规则 + 编辑器审计 + 运行期一次警告 |
| R6 | 每次重建分配数组 | 按长度预分配切片（S5）；Udon 分配代价未测 |
| R7 | 数据侧的 `blocks_ball_diameter_below_m`（`Docs/Design/Maps/PicnicLawn/layout.json` 的 `size_gates`，如 1.05）与本方案的单门槛**语义重叠但不被运行时读** | 本方案让它变成纯作者注记：挡到能吸为止（1.25），比 1.05 更保守、没有空窗。要不要接成「提前放行」由 Lead 决定（§9） |
| R8 | 挡路让某些作者路线变得不可通行（小球的密集区） | 现状的绕法（大件放主路两侧，`REGION_MAP_PLAN.md:116` G6）不再是必需，但布局仍应按尺寸带摆放 |

## 9. 未决 / 不要当事实用

1. **原作球撞道具是停还是弹**：`unconfirmed`，本方案按「停 + 滑」实现，属 PROVISIONAL。
2. **挡路容量 6**：按「球周围同时能贴到几件」估的，不是测量值；S8 后按实测调（它是常量，改动只影响电机）。
3. **`blocks_ball_diameter_below_m` 是否接进运行时**：接就是第二个门槛（`Docs/OPEN_DECISIONS.md:39` 明确警告过第二阈值），不接就多一个没人读的字段。建议不接。
4. 本文不改任何代码、场景、任务或数据；§5 的每条改动各自走任务与评审。所有米数都是本项目暂定值。
