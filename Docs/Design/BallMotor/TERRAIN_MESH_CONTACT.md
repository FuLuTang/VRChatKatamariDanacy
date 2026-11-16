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

对球心周围 **16 个径向方向**（motor 已有的 `proxyDirections`；按 `visualRollRotation` 转到世界）
投影到 XZ、缩放到 0.9·r 的环，每个 probe 做竖直射线：

1. chunk AABB（XZ + 最低 y）粗筛；三角形 AABB 再筛；零面积跳过。
2. 三角形平面在 probe XZ 的高度 = `(dot(n,a) - n.x·x - n.z·z) / n.y`；只取
   `|n.y| > MinUpwardNormalY (0.05)` 的面，法线翻到 +Y（double-sided，地面无论绕序都能支撑）。
3. probe 的 XZ 必须在三角形 XZ 投影内（重心坐标，边 -1e-4 容差）。
4. 只取 `height <= bandReference + stepLimit`（调用方已有的 `SupportSurfaceMath.IsStepWithinLimit`
   同一个谓词），取最高。选中的平面在球心 XZ 的高度、法线和 `2000+zoneId` 面 id 交给
   `SupportSurfaceMath`/`GroundContactMath`，与 plane/box/ramp 的 `(normal, offset)` 完全同形。
5. 悬垂/屋顶在球上方：高度超过 band，T0 不会把它当地板。

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
- 每步读数：`GetLastTerrainQueryMicros()`（motor 已有的 `Time.time` 微秒模式），只在有地形查询时写。
- 容量 8192 tri / 16384 vert / 64 chunk；数组约 300 KB（运行时，不序列化）。

## 5. 测试（`Tests/EditMode/BallContact/TerrainMeshContactTests.cs`）

平网格 = 平面；10° 坡；0.5/0.6 m 台阶的 `0.25·D` band；沟底 −1 m；悬垂不支撑但向下推；
零面积三角形；两个 chunk 的接缝无空隙；无网格/坏 chunk 拒绝。电机 flag-off 的等价性由代码路径
（false 时零查询）保证，运行时未在 Unity 验证。

## 6. 未验证

- UdonSharp 编译、数组容量/内存、`Time.time` 读数、EditMode 执行。
- 与真实 3,456 三角形 Cancer 网格的接缝/绕序/法线；导入器尚不存在。
- v2 接触求解器里的地形（T2）、连续扫掠防隧穿（T2）、爬升/台阶分类（T2）。
- 场景/预制体接线（只写 `SetTerrainMesh` 接口，不碰场景）。
