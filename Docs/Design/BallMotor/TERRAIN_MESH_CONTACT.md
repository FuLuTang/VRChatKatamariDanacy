# 地形网格接触（TERRAIN MESH CONTACT，T0/T1）

> 状态：`PROVISIONAL_NOT_TESTED`，2026-11-16。实现 `LEVEL_FORMAT_V2` §13 的 **T0 支撑 + T1 穿透**；
> 运行期代码在 `Runtime/BallContact/TerrainMeshMath.cs` 与 `Runtime/BallMotor/KatamariBallMotor.cs`。
> 本检出没有 Unity/Udon 编译、没有 EditMode 运行、没有 Play Mode/VR；所有运行时行为未验证。
> 不实现导入、不碰场景。

## 1. 开关与阶段边界

- 新序列化开关 `terrainMeshContactEnabled`，**默认 false**。为 false 时 `SetTerrainMesh` 的数组不被查询，
  `StepPositionV1` 不调用地形穿透/支撑覆盖；v1 的位置、速度、支撑与关闭前逐字节相同。
- 地形网格只是 **额外** 支撑/穿透源，不替代 `groundY` 兜底平面、盒子、ramp；也不占 8 平面 / 8 盒 /
  4 坡 / 256 OBB 的槽位。它的面 id 从 **2000** 起，避开 plane（1000+i）、box、ramp、prop。
- T0/T1 接入 **v1 位置权威**。v2 接触求解器的地形接触是 §13 的 T2，本轮不做；打开
  `contactModelV2Enabled` 时地形网格不会被查询（`StepContactV2` 不调用本模块）。
- 依赖现有 `terrainSupportEnabled`（默认 true）把选中的 `(normal, offset)` 送进
  `GroundContactMath`；只开 `terrainMeshContactEnabled` 而关 `terrainSupportEnabled` 时，几何被查询但
  下游仍用原始地面平面（文档化行为，不建议）。

## 2. 数据契约（导入器必须交给电机）

```csharp
bool SetTerrainMesh(
    Vector3[] vertices, int vertexCount,
    int[] indices, int indexCount,
    int[] chunkStarts, int[] chunkTriangleCounts, int[] chunkZoneIds,
    int chunkCount);
void ClearTerrainMesh();
bool IsTerrainMeshContactEnabled();
void SetTerrainMeshContactEnabled(bool enabled);
int GetTerrainMeshTriangleCount();
int GetTerrainMeshChunkCount();
float GetLastTerrainQueryMicros();
```

| 数组 | 内容 |
|---|---|
| `vertices` | 世界空间（项目唯一坐标系，米，+Y 上）的顶点；`vertexCount` 个有效 |
| `indices` | `3 × triangleCount` 个顶点索引；三角形绕序决定“正面” |
| `chunkStarts[c]` | 第 c 个 chunk 的第一个**三角形**下标 |
| `chunkTriangleCounts[c]` | 1..512；chunk 必须**连续、无重叠、覆盖全部三角形一次** |
| `chunkZoneIds[c]` | 非负；同一 broad box / 区域用同一个 id；面 id = `2000 + zoneId` |

固定容量：`MaxTriangles = 8192`、`MaxVertices = 16384`、`MaxChunks = 64`、
`MaxTrianglesPerChunk = 512`。Motor 在第一次 `SetTerrainMesh` 时分配（writer-allocates），之后只 copy；
`ClearTerrainMesh` 只清零计数，不释放。一个 3,456 三角形的 Cancer 网格是 7 个 chunk
（6×512 + 384），正好在容量内。

**导入器要做的**：把 68 个碰撞 AABB 内的 3,456 个三角形转到世界空间；删除零面积/重复三角形；
按空间（1 m 网格或每个碰撞 AABB）切 chunk，**每个 chunk ≤512 三角形**；每个 chunk 给一个 zone id
（同一碰撞 AABB / 区域同 id）；三角形绕序统一为**朝外**（地面朝上、悬垂底面朝下），因为 T1 单面推出
只从绕序法线正面推。不要烘 ground 平面/盒子/ramp 进网格（§13.5 的“能解析体就不进网格”）。

## 3. 查询语义

### 3.1 支撑（T0，double-sided）

> **2026-11-16 修正**：采纳判据不再是「比平面高才赢」。见 §8。下面的五条是修正后的语义，
> 实现在 `TerrainMeshMath.TrySelectSupport`（几何 + 带）与
> `SupportSurfaceMath.TryResolveSupport`（唯一的一次选面）。

对球心周围 **16 个径向方向**（motor 已有的 `proxyDirections`；按 `visualRollRotation` 转到世界）
投影到 XZ、缩放到 0.9·r 的环，**最后再加球心自己的 XZ**（17 个 probe），每个 probe 做竖直射线：

1. chunk AABB（XZ + 最低 y）粗筛；三角形 AABB 再筛；零面积跳过。
2. 三角形平面在 probe XZ 的高度 = `(dot(n,a) - n.x·x - n.z·z) / n.y`；只取
   `|n.y| > MinUpwardNormalY (0.05)` 的面，法线翻到 +Y（double-sided，地面无论绕序都能支撑）。
3. probe 的 XZ 必须在三角形 XZ 投影内（重心坐标，边 -1e-4 容差）。
4. 只取 `height <= ceiling`，`ceiling = 球底 + stepLimit`（**参照是球当前位姿**，
   不是上一帧支撑也不是 groundY/平面；阈值仍是 `0.25·D` 的台阶门）。**向下没有下界**：
   沟/坑/隧道底无论多深都是候选，真正的坠落由每关的 kill plane 判定。
5. 取到三角形后，把它在**球心 XZ** 上的平面高度作为候选（斜面上 probe 高度 ≠ 球心高度）；
   `IsStepWithinLimit(球心高度, 球底, stepLimit)` 再过一次，取最高。
6. 悬垂/屋顶在球上方：高度超过 ceiling，T0 不会把它当地板（T1 从下面顶住）。

**选面只有一处**：`SupportSurfaceMath.TryResolveSupport` 把地形候选与静态盒/斜面/道具顶按
「最高者胜」比一次；**凡地形在球的足迹下给出候选，竞技场地面平面就不参与竞争**（平面只
在地形答不出的地方兜底：接缝洞、无地形关卡）。面 id `2000+zoneId`，与 plane/box/ramp 的
`(normal, offset)` 完全同形。

### 3.2 穿透（T1，single-sided）

对球心 ±r 的 AABB：chunk AABB → 三角形 AABB → 球-三角形最近点（Ericson 的重心分支，面/棱/角）。
只从 **正面**（`dot(faceNormal, center - a) >= -ε`）推出；三角形背面不推，所以站在悬垂下面不会被顶起，
屋顶从下面接触时法线向下。深度 `r - distance`；沿最近点法线推出 `depth + separationEpsilon`，最多
2 次迭代；同时去掉平面内朝内的速度分量。T1 不做 θ 分类/爬升/连续扫掠（T2）。

## 4. 每步成本（3,456 三角形，7 chunk）

| 查询 | 无粗筛最坏 | 1–2 个 chunk（512 tris） | 1 m 空间 chunk（≈2–4 tris） |
|---|---:|---:|---:|
| 支撑：16 probe × 三角形 | **55,296** 三角形测试 | 8,192–16,384 | ≈576 |
| 穿透：1 球 × 2 迭代 | **6,912** | 1,024–2,048 | ≈32 |
| 合计 | ≈62,208 | ≈9k–18k | ≈0.6k |

- chunk 粗筛把 3,456 压到 1–2 chunk 的因子是 **3.4–6.7×**（3456/512 = 6.75；实际命中 1–2 个）。
- 上表是三角形**测试次数**，不是 flop；每个三角形 AABB ≈6 次比较，平面/重心 ≈20 flops，
  worst ≈1.2 Mflop/step（50 Hz ≈60 Mflop/s），Udon 解释器开销另计。**T3 的 1 m 网格才是 2–6k 目标**；
  当前 512-triangle chunk 只是上限，导入器应按空间切小（Cancer 3456/68 AABB ≈51 tri/AABB）。
- 每步读数（**全部是「最后一个固定步」的值**，不是一帧的和）：
  - `GetLastTerrainQueryMicros()` = 支撑选择 + T1 推出的合计；
  - `GetLastTerrainSupportMicros()` / `GetLastTerrainPenetrationMicros()` = 两半各多少；
  - `GetTerrainTrianglesTested()` = 被 chunk 粗筛放行、真正逐三角形迭代的次数（17 probe × 命中
    chunk 的三角形）；`GetTerrainFullEvals()` = 其中通过三角形 XZ 包围盒、付了叉积/平面/重心
    的数目；`GetLastTerrainSupportMicros() / GetTerrainFullEvals()` 就是「µs / 次完整求值」；
  - `GetStepsThisFrame()` = 上一次 `Update` 跑了几步 ⇒ **帧成本 = 读数 × 步数**（`maxStepsPerFrame = 5`）。
    跑 0 步的帧把计数一并清零，所以乘积不会被读成「免费」。
  **2026-11-16 修正**：原来两端都取 `Time.time`（帧时间戳），同一帧内差值恒为 0，四格采样
  永远是 0。现在用 `Time.realtimeSinceStartupAsDouble`（帧内会走的墙钟）；每步两次原生属性
  读数、零分配、不进任何判定路径，相对它测量的一轮 17 probe × 三角形测试可忽略。
  **清零位置**：`StepBall`（v1/v2 两条权威都经过它）。原来写在 `StepPositionV1` 里，开了
  contact v2 之后读数会是**上一次 v1 步的陈旧值**——v2 权威不查地形，现在如实报 0。
- 容量 8192 tri / 16384 vert / 64 chunk；数组约 300 KB（运行时，不序列化）。

## 5. 测试

`Tests/EditMode/BallContact/TerrainMeshContactTests.cs`（几何层）：平网格 = 平面；10° 坡；
0.5/0.6 m 台阶的 `0.25·D` band；沟底 −1 m；悬垂不支撑但向下推；零面积三角形；两个 chunk 的
接缝无空隙；无网格/坏 chunk 拒绝。

`Tests/EditMode/BallContact/TerrainSupportModelTests.cs`（支撑模型，**2026-11-16 新增**，真实三角形）：
`ThereIsNoSupportPlaneWhereTheMeshAnswers`（平地：地形答得出，平面不竞争）、
`TheGullyFloorWinsEvenWhenThePlaneSitsAboveIt`（C22 的 1 m 沟 + 平面在 0，球底 −1）、
`TheGullyRimSupportsUntilTheFootprintLeavesIt`（沟沿 0.05 m 内仍是 0，0.20 m 内变 −1）、
`AStepAtTheStepLimitIsClimbableAndFourDiametersIsNot`（0.25·D 含等号可上、4·D 不可上）、
`ATunnelRoofIsNotASupportAndPushesTheBallDown`（顶不支撑、T1 往下推）、
`ASlopeSupportsAtItsHeightUnderTheBallCentre`（斜面按球心 XZ 的高度）、
`APitFloorTenMetresDownIsStillASupport`（向下无界）、
`ABoxTopInsideTheBandBeatsTheMeshAndOneOutsideDoesNot`（盒 vs 地形的最高者胜）、
以及平面兜底。

`Tests/EditMode/BallMotor/TerrainSupportStepTests.cs`（电机级，同一轮新增）：地形把支撑从
「压在它上面的平面」手里拿走（`GetSupportKind() == 6`、`GetSupportSurfaceHeightWorldM() == −1`）、
`GetLastTerrainQueryMicros() > 1`（计时是真的）、`support + penetration == total` 且
`trianglesTested == 17 × 512`（可解释的工作量）、球埋进地板时穿透计数非零、
开 contact v2 后计数器**归零而不是沿用旧值**（陈旧读数回归）。`Tests/EditMode/BallMotor/StaticBoxCapacityTests.cs`：
第 64 个静态盒有效、第 65 个**显式拒绝**并 latch 原因、64 个盒里只有 1 个在球附近时答案不变。

电机 flag-off 的等价性由代码路径（false 时零查询）保证，运行时未在 Unity 验证。

## 6. 未验证

- UdonSharp 编译（尤其 `Time.realtimeSinceStartupAsDouble` 是否在该 SDK 的 Udon 白名单里；
  不行的退路是 `TerrainClockSeconds()` 一行换成 `Time.realtimeSinceStartup`，代价是 1 h
  运行后分辨率约 0.24 ms）、数组容量/内存、EditMode 执行。
- 与真实 3,456 三角形 Cancer 网格的接缝/绕序/法线；导入器尚不存在。
- v2 接触求解器里的地形（T2）、连续扫掠防隧穿（T2）、爬升/台阶分类（T2）。
- 场景/预制体接线（只写 `SetTerrainMesh` 接口，不碰场景）。

## 7. 导入与 PTF 地形单元（2026-11-16）

- **Editor**：`Editor/LevelBuild/TerrainMeshImporter.cs` 读 v2 的 `terrain`（内联 ≤512 tri，
  否则 `meshRef` + `.katerrain`），校验 sha256、索引/计数/chunk 连续性，然后写
  `TerrainFieldHost` 的序列化数组。参考关卡的 `_ref_*` 只允许在 `Assets/_RefUnlicensed/`
  的自己的场景里写；否则 `[terrain_scene_guard]`，绝不写 StoryMode。导入不 SaveScene。
- **Runtime**：`TerrainFieldHost.Start()` 把数组交给 `motor.SetTerrainMesh`（writer-allocates），
  并用序列化 bool 打开 motor 的 `terrainMeshContactEnabled`；runtime 不读磁盘。
- **容量拒绝码**：`[terrain_capacity]` 超过 8192 tri / 16384 vert / 64 chunk / 512 tri·chunk；
  `[terrain_sidecar]` sha/计数/路径；`[terrain_mesh]` 索引/计数；`[terrain_zone]` zoneIds；
  `[terrain_scene_guard]` 参考场景。
- **PTF C21–C24**（`Tools/levelbuild/physics_test_terrain.py`，v2 inline）：
  C21 stairs 16 tri / 32 vert；C22 gully 14 / 28；C23 tunnel-overhang 8 / 16；
  C24 curb 12 / 24。四格都是 `terrain_pending` 的 `planned_cells`，`PhysicsTestField.unity`
  未编辑；操作员建好场景后再把它们 wired 并加 `penetration_max` driver 读数。

## 8. 2026-11-16 支撑模型修正（操作员在真实编辑器的四格实测）

| 实测 | 根因 | 修法 |
|---|---|---|
| C21 台阶 0.30 m：球停在踢面 − 半径，水平阻挡对 | — | 不动（T1 单面推出本来就是对的） |
| C22 1.0 m 沟：球沿 y=0.150 直接开过去 | 旧代码「地形只在比已选支撑**更高**时才采纳」（`terrainHeight > supportSurfaceHeightWorldM`），而竞技场平面在 0、沟底在 −1，地形永远赢不了平面 | 平面**不再是竞争者**：地形在足迹下有候选时，选面从地形起算（§3.1 第 6 条） |
| 平面压到 −1000：球直接掉穿（−13.5 → −26 → −38.7） | 采纳带以「上一个支撑 / 平面」为参照，且平面是初始赢家：球一旦落到平面或台阶内部，`ceiling = bandReference + stepLimit` 把 y=0 的网格判成「太高」全部拒绝，且越掉越拒绝 | 带**只以球底为参照**（`ballBottom + 0.25·D`），**向下无界**；向上仍是台阶门 |
| `GetLastTerrainQueryMicros` 四格恒为 0 | 两端都取 `Time.time`（帧时间戳），同一帧差值恒 0，结构上只能是 0 | 换 `Time.realtimeSinceStartupAsDouble`，单位微秒 |

**新的采纳判据（一句话，写文档用）**：**支撑是球底以下、且不超过「球底 + 0.25·D」的那个最高面，
候选来自地形三角面、静态盒、斜面与道具顶——凡地形在球的足迹下答得出，竞技场地面平面就不参与
竞争（它只是「这里没地形」的兜底）；向下不设边界，掉出世界由每关的 kill plane 判定。**

副作用与边界：
- 地形「答得出」按**每步、每个 XZ** 判定，不是一次性的 latch：接缝洞/出网格处仍由平面兜底，
  这样不会因为一次 probe 失败就让球永久失去地板（ORIGINAL_TERRAIN §6.5 的「平面作接缝兜底」）。
  代价：如果平面被错放在地形之上（如本轮场景里从旧测试场拷来的 offset=0），
  球在**网格外**仍会站在那块平面上——这是关卡作者的 `groundY` 错误，不是支撑模型的错。
- 同一 chunk/zone 内的落差（如 1 m 沟）面 id 不变，`SupportDroppedAway` 的 `sameSurface`
  抑制会让球按 0.1 m/步的下落钳位掉下去（落地正确，但不是弹道）；跨 zone 的落差才进 `isFalling`。
  这是 id 粒度的已知取舍（§1「面 id 必须稳定」），不是本轮改的。
- `SupportReferenceHeight` 保留为「取两者较高」的工具函数，**不再**是带的参照；扫掠掩码与
  选面都直接传球底（否则掩码与选面会各用一个参照——那正是 C09 ghost step 的成因）。
