# VRChat 尺寸上限核查：球能滚到多大

> 状态：研究文档，2026-11-15 读取并整理。只新增本文件；未改代码、场景、其他文档，未开 Unity。
> 目的：回答“我们的 Katamari 球要远超 2.20 m，VRChat 到底哪里会拦、哪里只是建议”。
> 证据等级：**官方文档** / **官方表态** / **官方 wiki** / **社区实测** / **本项目推断** / **未记录**。
> 所有 URL 的读取日期均为 **2026-11-15**（UTC）；来源编号见 §8。

## 0. 先给结论

1. **VRChat 没有“世界最大尺寸”的硬上限。** 官方文档没有写世界包围盒、离原点最大距离、最大碰撞体、最大网格/贴图预算。能查到的都是“建议 / 性能”，不是构建或上传错误。见 §2。
2. **Avatar 的 5×6×5 m 不是上传硬拦。** 它是 Avatar Performance Rank 的 Bounds Size 档位（Medium 与 Poor 都是 5×6×5，超过即 Very Poor）。当前官方文档明说：`Bounds Size will not cause the avatar to be blocked, even if it is below the "Minimum Displayed Performance Rank" setting.`（S1）真正会拦上传的是**文件大小**：PC 下载 200 MB / 解压 500 MB（S5）。
3. **“5 m 一边”来自 SDK2 时代的一条 bounds 报错**：`this avatar measures too large on at least one axis. It must be <5 on a side`（S25，2017；S26，2018）。它检查的是 Body 的 bounds 盒子（不是身高），修法是清零 Bounds Center、收紧 Extents。SDK3 的现行官方口径是不拦。
4. 真正卡我们的三条：**avatar 眼高缩放范围**（玩家 0.2–5 m；Udon 官方支持 0.1–100 m）、**相机 far clip / clip 限制**、**我们自己的 8 m 眼高帽 + 512 m³ 门槛 + 40×40 m 地图**。见 §6。

## 1. Avatar

### 1.1 Bounds Size：5×6×5 是档位，不是门

| 档 | Bounds Size | 含义 |
|---|---|---|
| Excellent | 2.5 m × 2.5 m × 2.5 m | 官方推荐区 |
| Good | 4 m × 4 m × 4 m | 一般安全 |
| Medium | 5 m × 6 m × 5 m | 可接受 |
| Poor | 5 m × 6 m × 5 m | 重 |
| Very Poor | 超过 5 m × 6 m × 5 m | 无上限（性能档不封顶） |

来源 S1（官方文档，2026-11-15 读）。

**执行方式（重要）：**

| 环节 | 会不会因 bounds 拦截 | 依据 |
|---|---|---|
| SDK Build & Test | 不会。SDK3 没有 bounds 构建错误（SDK2 时代有，见下） | S1；S25/S26 为历史 |
| 上传 | 不会。上传只看文件大小（200 MB / 500 MB） | S1、S5 |
| 客户端显示 | 不会。Minimum Displayed Performance Rank 对 Bounds Size 的行为是 **No change**（其他指标超标会换 Fallback） | S1 |
| 性能档 | 会。>5×6×5 记为 Very Poor | S1 |

官方脚注原文：`Bounds Size is determined by the maximum size of all components on your avatar. Trail and Line Renderers do not count for this calculation.`（S1）也就是 Line/Trail Renderer 不计入，其余组件（含动画里才出现的部件）都算。

**历史的那条 5 m：** 2017 年的报错 `this avatar measures too large on at least one axis. It must be <5 on a side but it's bounds are (...)`（S25），以及 2018 年同一报错的中文/日文记录（S26）。它针对的是 **Skinned Mesh Renderer 的 Bounds**，不是 avatar 身高；当时把 Bounds Center 归零、Extents 收紧就能上传。把它当成“今天 VRChat 仍然硬限 5 m”是过时信息；第三方 avatar 验证器仍会这么写，别信（S27 属于第三方工具宣称，与 S1 冲突，按 S1）。

### 1.2 Avatar scaling：真正的硬范围

| 项 | 数值 | 执行方式 | 来源 | 等级 |
|---|---|---|---|---|
| 玩家在 Action Menu 里自缩放的眼高 | **0.2–5.0 m** | 客户端 clamp | S2、S3 | 官方文档 |
| Udon 设置眼高 | **0.01–10000 m 接受**；官方支持 **0.1–100 m** | 客户端接受，范围外可能 UI 崩、不稳定 | S3 | 官方文档 |
| `avatar-scaling` 页写的 Udon 范围 | 0.1–100 m | 与 S3 的数字不一致；S3 更细，取 S3 | S2 | 官方文档 |
| Prefab Height（T-pose 眼高） | **不限** | 不拦 | S2 | 官方文档 |
| 缩放实现 | 改 **root localScale**（整体缩放） | 客户端强制 | S2 | 官方文档 |
| 同步量 | 眼高，3 位小数 | 客户端 | S2 | 官方文档 |
| 碰撞 | **不随缩放改变**（官方原文：scaling does not affect collision with the environment） | 客户端 | S3 | 官方文档 |
| 远端 >100 m | 远端视图被 clamp（bug，修复目标 2026.2.1） | 客户端 | S11 | 官方表态（bug tracker） |
| 公开 avatar 推荐身高 | 167–180 cm | 投稿指南 | S18 | 官方 wiki（引 2019 旧指南） |
| 性能建议 | Bounds ≤2.5 m = Excellent，≤4 m = Good | rank | S1 | 官方文档 |

**和 viewpoint 的关系：** Prefab Height 就是 T-pose 下 viewpoint（眼睛）的 Y 值；缩放比 = 目标眼高 / Prefab Height；缩放改的是 root localScale，所以 avatar 身上挂的所有子物体（包括球）都按同一比例放大。同步的是**眼高**，不是 scale，所以不同 Prefab Height 的 avatar 能对齐到同一个“看起来一样高”。官方示例场景的默认值就是 `minimumHeight 0.2` / `maximumHeight 5`（S4），和 Action Menu 的范围一致。

**“5 m”与“100 m”怎么记：** 玩家自己能按出来的上限是 5 m；世界用 Udon 可以写到 100 m 并且仍在官方支持范围内；100 m 以上官方不支持（UI/稳定性），远端还会被 clamp。30 m 落在“Udon 支持、玩家自缩放不支持”的区间。

### 1.3 对“球挂在 avatar 上”意味着什么

- 球是 avatar 的子物体 → 跟着 root 缩放；眼高 5 m 时球最多也就 5 m 量级（1:1 时）。
- 30 m 的 avatar 球只能由**世界用 Udon 强制缩放**（30 m 在 0.1–100 支持区内），玩家自己按不到；而且**碰撞体不缩放**，所以这个 30 m 球不会推开世界里的东西，只是一个视觉壳。
- 超过 100 m 远端会被 clamp（S11），多人世界里别人看到的尺寸和你本地不一致。
- 性能档必然 Very Poor；不会被 bounds 拦，但可能被别人的 Minimum Displayed Rank 按**其他指标**换成 Fallback（bounds 本身不换）。
- 2023-08 的官方 dev update 里 staff 也直说没想到 SDK 允许这么大的 avatar（S12）。**“允许上传”和“支持”是两件事**：能传上去，不等于视角、碰撞、IK、别人的显示都正常。

## 2. World

### 2.1 世界包围盒 / 离原点距离：未记录

**官方没有写世界最大尺寸、最大坐标、离原点最大距离。** 我把 creators 的 Worlds、Scene Components、Optimization、Submitting 页面都查了，没有这条（S6、S7、S22）。所以任何“VRChat 世界最大 4 km / 20 km”的说法都不是官方规则，而是浮点精度的经验值。

| 说法 | 数值 | 性质 | 来源 | 等级 |
|---|---|---|---|---|
| 实际可用范围 | 约 **20 km × 20 km** 开始抖动 | 浮点精度经验值 | S16 | 社区实测 |
| SaccFlight 放宽后 | 40 km × 40 km，再远仍抖 | 社区方案的上限 | S16 | 社区实测 |
| 客户端强制回原点 | 离原点 **500,000–1,000,000 单位** 被重置 | 客户端行为 | S15 | 社区实测（官方论坛提问，官方未否认） |
| 我们的地图 | 40 × 40 m | 仓库 | `Docs/Design/Maps/PicnicLawn/layout.json:19-20` | 本项目 |

结论：**30 m 的球离“世界坐标上限”还差三个数量级**，浮点不是我们的瓶颈；40×40 m 的地图才是。

### 2.2 出生 / 重生：官方字段，无尺寸上限

VRC_SceneDescriptor（S6，官方文档，2025-12-11 更新）里与尺度有关的字段：

| 字段 | 作用 | 备注 |
|---|---|---|
| Spawns | 出生点 Transform 数组 | 无数量上限文档 |
| Spawn Radius | 在半径内随机出生；0 = 精确点 | 球变大后要保证出生点不在球里 |
| Spawn Order | First / Sequential / Random / Demo | — |
| Spawn Orientation | Default / Align Player / Align Room | — |
| Reference Camera | **这个相机的设置会应用到房间里的用户** | far clip 的官方入口 |
| Respawn Height -Y | 低于此高度重生 / 销毁 pickup | 大球掉出地图时用得上 |
| Object Behaviour At Respawn | Destroy / Respawn | — |
| User Custom Voice Falloff Range | 语音衰减范围 | 30 m 球时会影响听感 |

### 2.3 世界文件大小

| 项 | 数值 | 执行 | 来源 | 等级 |
|---|---|---|---|---|
| 官方建议 | **尽量 < 200 MB**（原文 “Try to keep your worlds under 200MB”） | 建议，不是硬拦 | S7 | 官方文档 |
| PC 上传硬上限 | 约 **1.15 GB**（社区报告，超出报 “This file is much too large”） | 服务器拒绝 | S17 | 社区实测 |

我们只有 40×40 m 灰盒 + 少量道具，离 200 MB 很远；但“球要 30 m”通常意味着地图也要放大，文件大小会先于引擎成为问题。

### 2.4 相机：far clip 是真正的可见性上限

| 项 | 数值 / 行为 | 执行 | 来源 | 等级 |
|---|---|---|---|---|
| 谁决定 | **Reference Camera 的设置应用到所有用户** | 世界 | S6 | 官方文档 |
| 运行时能否改 | 能。Udon `VRCCameraSettings.ScreenCamera.FarClipPlane` / `NearClipPlane` 可读写 | 世界 | S9 | 官方文档 |
| NearClipPlane clamp | **0.001–0.05**；用户的 “Forced Camera Near Distance” 可以覆盖 | 客户端 | S9 | 官方文档 |
| FarClipPlane 下限 | 最低 **NearClipPlane + 0.1** | 客户端 | S9 | 官方文档 |
| FarClipPlane 上限 | **官方未写**；2024-02 的修复把它压到约 **50,000**，之后“仍会有限制” | 客户端 | S10 | 官方表态（staff 回复） |
| Unity 默认 | Near 0.3 / Far 1000（Main Camera 默认） | 引擎 | S14 | 社区 wiki（引 Unity 默认） |
| 社区建议 | Far **不要超过 3000**；>3000 画质/精度都不划算 | 建议 | S14 | 社区 |
| Unity 硬关系 | Far ≤ 100,000 × Near | 引擎 | S13 | 官方 wiki |
| 烘焙遮挡剔除的世界 | near/far 限制**仍然生效**，staff 明说 “will not be changed” | 客户端 | S10 | 官方表态 |

**对我们的直接含义：**

- 默认 far 1000 时，**1 km 外的球直接不可见**；30 m 的球完全在范围内。
- 如果球半径涨到接近 far clip（例如直径 2 km），球背面会被裁掉，看到“空心球”。
- 球越大，越不需要 far clip；真正需要 far clip 的是“站在球顶看整张地图”。
- 改 far clip 会影响 skybox（S10 里就有世界报告 skybox 坏掉），不要随手拉满。

### 2.5 世界网格 / 贴图预算：PC 未记录

- 官方只给了 **Quest** 的优化目标与 **Avatar** 的档位；**PC 世界没有公开的三角形 / draw call / 贴图预算**（S7、S22 都没有）。
- 唯一带数字的官方预算是 avatar 的 texture memory：40 / 75 / 110 / 150 MB（S1）——那是 avatar，不是 world。
- 所以“PC 世界最多多少 draw call / 多少三角面”只能按目标帧率自己推（§4）。

### 2.6 多远还看得见：裁剪链

1. **视锥剔除**：不在相机视锥内不画（Unity 内建）。
2. **Far clip**：超过 far 的全部不画。默认 1000 m。
3. **遮挡剔除**：需要烘焙 Occlusion Culling（S23）；VRChat 支持，但烘焙过的世界 near/far 会被额外限制（S10）。
4. **LOD Group**：按距离换低模；远处只剩低模（S24）。
5. **Layer Cull Distances**：可按层设更近的剔除距离（S9 里 `LayerCullDistances`）。
6. **网络可见性**：Udon 同步对“可见对象”有优先级（S8）；不可见对象的同步会被降频，不是不画。

**没有“按对象关闭渲染”的 Udon API 文档**；要控制可见性只能用 `Renderer.enabled` / 移出相机 / 层剔除这类常规手段。

## 3. 物理（PhysX）

### 3.1 VRChat 层面：没有尺寸/速度硬限

- 官方没有“最大碰撞体尺寸”“最大刚体速度”的文档；World 白名单允许 Rigidbody / Collider。Quest 的限制原文只针对 avatar：`Rigidbodies, colliders, and joints are disabled completely for avatars on Android and Quest.`（S30）——World 侧没有被这条禁掉。
- 所以“世界 SDK 禁止巨大碰撞体”是**未记录**，不是事实。

### 3.2 Unity / PhysX 的默认值（会实际影响大球）

| 参数 | 默认值 | 影响 | 来源 |
|---|---|---|---|
| Fixed Timestep | 0.02 s（50 Hz） | 20 m/s 时每步 0.4 m；薄碰撞体可能被穿过 | S28 |
| Default Contact Offset | 0.01 m | 小于 ~1 cm 的缝会被当成接触/穿插 | S19 |
| Default Solver Iterations | 6（velocity iterations 1） | 大质量比 / 多接触时不够，会抖 | S29 |
| Default Max Angular Speed（项目级） | 50 rad/s | 大球转速会被夹 | S19、S29 |
| Rigidbody.maxAngularVelocity（每体） | 7 rad/s | 不改的话大球滚不起来 | S20 |
| PhysX maxLinearVelocity | 默认 `PX_MAX_F32`（等于无上限） | Unity 2022.3 不暴露；不会有“速度硬墙” | S21 |
| 最大碰撞体尺寸 | **未记录** | PhysX 没有公开的硬上限；实际受浮点与 contact offset 限制 | — |

**大球的真实问题（本项目推断）：**

- 30 m 球的表面曲率很小，球与地面的接触点很少，solver 在 6 次迭代下容易抖；要么加迭代，要么用“球心 + 半径”的解析地面约束，不要指望 MeshCollider。
- 球越大，`contactOffset 0.01` 相对越小，看起来越“硬”；但深穿插时 `maxDepenetrationVelocity` 会把球猛地弹开（默认值由 Physics Manager 给，不是 0）。
- 球速 20 m/s + 50 Hz 时单步 0.4 m；如果地面是薄板（<0.4 m），必须用 Continuous CD 或加厚，否则穿模。
- 1 km 以外 float32 的位置精度约 0.1 mm，30 m 球完全没问题；到 10 km 量级才需要 floating origin。

## 4. 遮挡 / 可见性：PC 世界的实际预算

**官方没有 PC 世界的 draw call / 三角形预算**（见 §2.5）。下面是能确定的与只能推断的：

| 项 | 结论 | 等级 |
|---|---|---|
| 一个 30 m 球 = 几个 draw call | 1–2 个（一个 mesh + 一个材质），不是问题 | 本项目推断 |
| 球越大越容易被裁剪掉吗 | 不会。球越大越可能**永远在视锥内**，GPU 一直在画 | 本项目推断 |
| 真正的成本 | 过绘制（整个屏幕都是球）、阴影、材质复杂度；不是 culling | 本项目推断 |
| 90 Hz VR 的经验预算 | 约 2,000–3,000 draw call、<1–2 M 三角面/帧（整帧，含 avatar） | 本项目推断（按 11 ms 帧预算估） |
| 官方给的数字 | 只有 Quest 的 ~250k 三角面目标和 avatar 档位；PC 世界没有 | S22、S1 |

对我们的意义：**一个 30 m 球不会因为“太大”而渲染不动**；会让帧率掉的是“球表面贴图/材质/阴影 + 整屏过绘制”。把球做成低模 + 简单材质，比担心尺寸更重要。

## 5. Udon（只列与尺度相关的）

| 项 | 数值 | 执行 | 来源 | 等级 |
|---|---|---|---|---|
| Continuous sync | **~200 bytes / serialization** | 超出直接序列化失败 | S8 | 官方文档 |
| Manual sync | **280,496 bytes / serialization** | 超出会缓存/延迟 | S8 | 官方文档 |
| 每脚本带宽 | **~11 KB/s** | 超出 `IsClogged`，全局拥塞 | S8 | 官方文档 |
| 最大数组长度 | **未记录** | — | S8 | 未记录 |
| UdonBehaviour 数量上限 | **未记录**（没有“最多 N 个”的官方数字） | — | S8 | 未记录 |
| 我们 30 m 球要同步的量 | 位置 12 B + 半径 4 B + 状态几 B；**和 2.2 m 时一样** | — | — | 本项目推断 |

结论：**球变大不会直接吃同步带宽**；吃带宽的是“球上挂了多少个可吸物件 / 多少个 networked behaviour”。如果 30 m 球要同步几百个附着物的局部位姿，那才会撞 200 B / 280 KB 的墙。

## 6. 实践：三种“球”的最大直径

### 6.1 三种形态

| 形态 | 最大实用直径 | 先坏在哪 | 依据 |
|---|---|---|---|
| **(a) 只是被缩放的渲染网格**（不载人、不参与物理） | **30 m 没问题**；约 60–100 m 才开始有阴影/裁剪/精度问题 | 阴影距离、整屏过绘制、LOD 失效；地图先不够大 | §2.4、§4；本项目推断 |
| **(b) 挂在 avatar 上**（玩家“骑着”球） | **5 m（玩家自缩放硬顶）**；30 m 只能世界用 Udon 强制缩放，且**碰撞不缩放** | 眼高 5 m 上限 → 碰撞体不缩放 → 球推不动东西；>100 m 远端 clamp | S2、S3、S11 |
| **(c) 世界物体，玩家站在上面** | **22 m（现状）**；把两只 8 m 帽抬到 ≥ 半径+2 后 **30 m 可用**；引擎 100 m+ | 我们的 FollowRig 眼高帽（22.6 m 起相机进球内）、512 m³ 门槛（21.4 m 起变成限制） | §6.2；`KatamariBallMotor.cs:238,241,218` |

### 6.2 为什么是 22 m：算一遍

`FollowRigMath` 的相机位置（`KatamariBallMotor.cs:236-241`）：

```text
height = clamp(0.55 + 2.4 × r, 0.1, 8)   // 球心以上
back   = min(0.75 + 2.2 × r, 8)          // 球心以后
camDist = sqrt(height² + back²)
```

- r = 3.10 m（**D ≈ 6.2 m**）：height 先撞 8 m 帽，视角不再随球长大；构图开始变形，但相机还在球外。
- r = 11.31 m（**D ≈ 22.6 m**）：height = back = 8，camDist = 11.31 = r，相机正好在球面上；再大就**进到球里**。
- r = 15 m（D = 30 m）：camDist = 11.31 m，比半径少 3.69 m → 相机在球内 3.7 m，看到的是背面/黑。

所以 **(c) 现在能安全做到 22 m**。要 30 m，把 `eyeUpMaxMeters` 和 `eyeMaxBackM` 都提到 ≥ 40（30 m 球需要 height 36.55、back 33.75），并把 `maxItemVolumeWorldM3` 从 512 提到约 1,414（30 m 球按 10× 体积比能吸的最大件）。

### 6.3 “30 m 球，玩家的眼在球里”到底是什么情况

- **球心在玩家脚下**（avatar 挂球、球跟着 root 缩放）：r = 15 m，眼高 1.7 m → 眼在球内 13.3 m。必然看不到外面。
- **球是地面上的世界物体，玩家站球顶**：眼在球心上方 15 + 1.7 = 16.7 m，**在球外**；这是正确做法。
- **我们的 FollowRig**：眼在球心上方 8 m（被帽住）→ 在球内 7 m；这是帽的问题，不是几何必然。
- **相机 near clip 救不了**：near 被 clamp 在 0.001–0.05（S9），不可能“切穿”15 m 的球壳。要么把相机放到球外，要么把球做成**内表面朝里的空心壳**（双面/翻转法线），否则一定穿帮。

### 6.4 Skybox / far clip

- 30 m 球：far 1000 足够，skybox 不受影响。
- 球半径接近 far clip（比如直径 2 km）：球的远侧被裁掉，看起来像被切开；同时 skybox 以 far 平面为深度，改 far 可能让 skybox 坏掉（S10 有世界报告）。
- 我们灰盒预览相机 `camera.farClipPlane = 250f`（`Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs:1225`）是**编辑器预览**，不是运行时相机；不要拿它当运行时 far clip。

### 6.5 音频

- `VRC_SpatialAudioSource` 的 near/far 以米为单位；30 m 球的接触点离球心 15 m，若 far 还是小球时代的 10–20 m，球另一侧的滚动声会突然消失。
- 玩家语音是 positional audio，衰减由世界/用户设置决定（`User Custom Voice Falloff Range`，S6）；30 m 球顶到球底的距离可能超过默认语音衰减范围。
- 建议：滚动声挂在**接触点**而不是球心；far 至少设成球直径的 1.5 倍；重要提示音用 2D。

## 7. 仓库里“假设 2.20 m”的位置

> 口径：只列**当前有效**的文件（不含 `Docs/Archive/`、`Docs/Evidence/`、`Docs/Source/`）。`Docs/` 下命中 **72 个文件 / 430 处**，Unity 侧另有 **108 处**（模式 `2.20|2.2 m|maxCoreRadiusWorldM|D_cap|ball_max_collision_diameter_m`，2026-11-15）。下面按“改了会不会生效”分四层。

### 7.1 A 层：真源（改这些才会真正改变行为）

| 位置 | 内容 | 备注 |
|---|---|---|
| `Docs/Design/Maps/PicnicLawn/layout.json:22` | `"ball_max_collision_diameter_m": 2.2` | 地图真源 |
| `Docs/Design/Maps/PicnicLawn/layout.json:46` | `ball_bands[t3].max_m = 2.2` | 同一张表的档位 |
| `Docs/Design/Maps/ToyRoom/layout.json:22`、`:46` | 同上 | 第二张图 |
| `Docs/Design/Maps/PicnicLawn/unity_greybox.json:18` | 同上 | 灰盒 |
| `Docs/Design/Gameplay/round_model.py:114` | `max_diameter_m: float = 2.20` | 离线模型默认值 |
| `Docs/Design/Gameplay/round_model.py:50` | `TIER_EDGES_M = (0.55, 0.95, 1.45, 2.20)` | 档位边界 |
| `Docs/Design/Gameplay/round_model.py:242` | 从 layout 读 `ball_max_collision_diameter_m` | 消费者，不用改 |
| `Docs/Design/Parameters/README.md:221` | 示例 JSON 里的 `2.2` | 示例也要改 |
| `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/KatamariBallMotor.cs:196` | `maxCoreRadiusWorldM = 1.1f` | 运行时默认（半径制） |
| `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity:12621` | `maxCoreRadiusWorldM: 1.1` | 场景序列化值 |
| `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/CpuSkirmish/CpuSkirmish.unity:430,2438,4140,5317` | 同上 | 实验室场景 |
| `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/PhysicsTestField/PhysicsTestField.unity:8812` | 同上 | 实验室场景 |
| `Tools/mapbuild/validate_map.py:448` | 读 `ball_max_collision_diameter_m` | 校验器，不用改 |

**代码侧的关键事实：** 上限本身是 `[SerializeField]` 的**数据**，不是常量；`SizeModelMath` 全部是参数化函数（`SizeModelMath.cs:112,271`），**改上限不需要改数学代码**。真正会在大球下先坏的是另外三个数：`eyeUpMaxMeters = 8`（`:238`）、`eyeMaxBackM = 8`（`:241`）、`maxItemVolumeWorldM3 = 512`（`:218`）。

### 7.2 B 层：口径 / 契约文档（改说法，不改行为）

| 位置 | 写的是什么 |
|---|---|
| `Docs/OPEN_DECISIONS.md:89` | 决策 (n)：第一关 2.20 m；`maxCoreRadiusWorldM` 每关一份 |
| `Docs/Design/StoryMode/README.md:632-636` | 每关上限约定 + Story01/PicnicLawn 表（2.20 / 3.20） |
| `Docs/Design/StoryMode/README.md:649-650` | 同上表体 |
| `Docs/Design/StoryMode/PICNIC_CORNER_LAYOUT.md:37` | `maxCoreRadiusWorldM = 1.1` → `D_cap = 2.20` |
| `Docs/Design/StoryMode/PICNIC_CORNER_LEVEL_ONE.md:433`、`:618` | 第一关 `maxCoreRadiusWorldM 1.1` |
| `Docs/Design/StoryMode/PROP_SET_PLAN.md:156`、`:208`、`:232` | `D_cap = 2 × maxCoreRadiusWorldM`、2.20 表 |
| `Docs/Design/Parameters/README.md:40`、`:502` | 标定表 / 参数清单里的 2.20 |
| `Docs/Design/BallMotor/README.md:301` | 序列化默认 1.1 / 2.20 |
| `Docs/Design/PickupAbsorb/README.md:74`、`:231` | `maxGrowthRadiusWorldM` 默认 1.1 |
| `Docs/Design/GameModes/README.md:48`、`:58`、`:601`、`:616` | 包线 0.30 / 2.20 |
| `Docs/Design/Gameplay/README.md:56`、`:150`、`:192` | 1356 单位标定、2.20 可达性结论 |
| `Docs/Design/Maps/PicnicLawn/README.md:513`、`:534` | 2.20 表 + 3.20 冲突 |
| `Docs/Design/Maps/ToyRoom/README.md:23`、`:36` | 经济标定 + “球在 2.20 m 时仍比家具小” |

> **顺带发现一个文档 bug：** `Docs/Design/StoryMode/README.md:632` 与 `Docs/OPEN_DECISIONS.md:89` 都写 `KatamariBallMotor.cs:107`，但字段实际在 **`:196`**（`maxCoreRadiusWorldM`）。改上限时把这处行号一起修掉。

### 7.3 C 层：派生表 / 研究（重算，不逐条手改）

| 文件 | 行 |
|---|---|
| `Docs/Research/ContentVolume/README.md` | 233, 244, 266, 298, 302, 333, 341–343, 355, 398 |
| `Docs/Research/KatamariMaps/LEVEL_CONSTRUCTION.md` | 123, 144, 167, 168, 170, 187, 195, 198, 201, 202, 215, 239, 264 |
| `Docs/Research/KatamariBallRules/MAP_AUTHORING_DIGEST.md` | 21, 34, 35, 128, 141, 147, 158 |
| `Docs/Design/StoryMode/PROP_CATALOG.md` | 59, 78, 82, 197, 206, 208, 237, 241, 245, 247, 258, 262, 263, 300, 315 |
| `Docs/Design/Maps/PicnicLawn/README.md`（除上表 513/534） | 84, 91, 96, 97, 279, 314, 315, 371, 437, 453, 456, 516, 518, 520, 522, 547 |
| `Docs/Design/Maps/ToyRoom/README.md`（除 23/36） | 70, 77, 82, 83, 229, 292, 318, 335, 407, 411, 422 |
| `Docs/Design/PickupAbsorb/SIZE_GATE_AUDIT.md` | 23, 121, 141, 145, 146, 222, 243 |
| `Docs/Design/BallMotor/TERRAIN_SUPPORT.md` | 249, 297, 316, 386, 481, 482, 661, 792 |
| `Docs/Design/BallMotor/CONTACT_MODEL_V2.md` | 32, 34, 36, 219, 312, 363, 364, 383, 385, 386, 440, 489, 590, 634, 970, 1166, 1171, 1185, 1189–1191, 1196–1198, 1209, 1225, 1237, 1573, 1583, 1586, 1596, 1602 |
| `Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md` | 15, 25, 166–186, 197, 203, 229, 271–276, 292, 303, 305, 309, 312, 319, 327, 340, 344, 352, 363, 373, 395, 409, 449, 502, 508, 571, 601, 622, 624–626, 775, 892, 905, 958, 963, 969, 1045, 1047, 1051, 1238, 1244, 1250–1254, 1261, 1265, 1269, 1271, 1299, 1301, 1349, 1350, 1369, 1372, 1381, 1468 |

其余文件命中的是 2.20 的**复述**（内容表、关卡说明、决策记录），不是独立真源；改 A 层后重跑校验器再统一更新，不要反向从文档改数据。

### 7.4 D 层：历史证据（不要改）

- `Docs/Evidence/**`（例如 `Docs/Evidence/PhysicsTestField/2026-11-15_pass3/matrix.md:29-52`、`Docs/Evidence/BallPresentation/2026-11-15_attached_props_vs_original/README.md:32,38`）
- `Docs/Archive/**`
- `Docs/Source/**`（只读 provenance）

### 7.5 代码侧复述

- `Runtime/Ai/CpuSkirmishBootstrap.cs:271`：注释里写 `maxCoreRadiusWorldM 1.1`（仅注释）。
- `Tests/EditMode/BallLayers/SizeModelTests.cs:459`：测试辅助函数参数名，不是常量。
- `SizeModelMath.cs` / `BankShrinkMath.cs`：只接收 `maxCoreRadiusWorldM` 参数，无硬编码值。

## 8. 来源

| ID | URL | 类型 | 读到的内容 | 等级 |
|---|---|---|---|---|
| S1 | https://creators.vrchat.com/avatars/avatar-performance-ranking-system/ | 官方文档 | Bounds 2.5/4/5×6×5；bounds 不 block；texture memory 40/75/110/150 | 官方文档 |
| S2 | https://creators.vrchat.com/avatars/avatar-scaling/ | 官方文档（2024-02-27） | Prefab Height 不限；Action Menu 0.2–5.0；Udon 0.1–100；root scale；3 位小数 | 官方文档 |
| S3 | https://creators.vrchat.com/worlds/udon/players/player-avatar-scaling/ | 官方文档 | 0.2–5 玩家；0.01–10000 接受；0.1–100 支持；碰撞不缩放 | 官方文档 |
| S4 | https://creators.vrchat.com/worlds/examples/udon-example-scene/avatar-scaling-settings/ | 官方文档（2023-07-28） | `minimumHeight 0.2` / `maximumHeight 5` | 官方文档 |
| S5 | https://creators.vrchat.com/avatars/avatar-size-limits/ | 官方文档（2026-10-07） | PC 200 MB / 500 MB；Android 10/40；SDK 拦上传 | 官方文档 |
| S6 | https://creators.vrchat.com/worlds/components/vrc_scenedescriptor/ | 官方文档（2025-12-11） | Spawns/Radius/Order；Reference Camera 应用到用户；Respawn Height | 官方文档 |
| S7 | https://creators.vrchat.com/worlds/submitting-a-world-to-be-made-public/ | 官方文档 | “Try to keep your worlds under 200MB” | 官方文档 |
| S8 | https://creators.vrchat.com/worlds/udon/networking/network-details/ | 官方文档 | 200 B continuous / 280,496 B manual / ~11 KB/s | 官方文档 |
| S9 | https://creators.vrchat.com/worlds/udon/vrc-graphics/vrc-camera-settings/ | 官方文档 | NearClipPlane 0.001–0.05；Far ≥ Near+0.1；可运行时改 | 官方文档 |
| S10 | https://vrchat.canny.io/bug-reports/p/camera-farclip-max-cap-50k | 官方反馈 + staff 回复 | far clip 约 50k；烘焙遮挡世界 near/far 限制不取消 | 官方表态 |
| S11 | https://feedback.vrchat.com/bug-reports/p/1319-uploaded-avatars-not-height-clamped | 官方 bug tracker | >100 m 远端 clamp；修复目标 2026.2.1 | 官方表态 |
| S12 | https://ask.vrchat.com/t/developer-update-3-august-2023/19212 | 官方论坛 | avatar scaling 发布；staff 惊讶 SDK 允许极大 avatar | 官方表态 |
| S13 | https://wiki.vrchat.com/wiki/Guides:Clipping_Planes | 官方 wiki（社区撰写） | Unity far ≤ 100000×near；Main Camera 决定渲染距离 | 官方 wiki |
| S14 | https://vrchat.wikidot.com/worlds:guides:render-distance | 社区 wiki | 默认 near 0.3 / far 1000；>3000 不建议 | 社区实测 |
| S15 | https://ask.vrchat.com/t/can-you-turn-off-the-floating-point-reset/48341 | 官方论坛提问 | 500,000–1,000,000 单位被重置 | 社区实测 |
| S16 | https://booth.pm/ja/items/3814760 | 社区方案 | 20km×20km 浮点限制；SaccFlight 40km 仍抖 | 社区实测 |
| S17 | https://feedback.vrchat.com/feature-requests/p/increase-world-upload-limit-for-pc | 官方反馈 | PC 世界上传约 1.15 GB | 社区实测 |
| S18 | https://wiki.vrchat.com/wiki/Public_Avatars | 官方 wiki | 公开 avatar 建议 167–180 cm | 官方 wiki（引 2019 旧指南） |
| S19 | https://docs.unity3d.com/2022.3/Documentation/Manual/class-PhysicsManager.html | Unity 官方 | contact offset 0.01；max angular 50 | 官方文档 |
| S20 | https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rigidbody-maxAngularVelocity.html | Unity 官方 | 默认 7 rad/s | 官方文档 |
| S21 | https://docs.rs/physx-sys/latest/physx_sys/fn.PxRigidBody_setMaxLinearVelocity_mut.html | PhysX API 镜像 | maxLinearVelocity 默认 `PX_MAX_F32` | 社区镜像（官方 API） |
| S22 | https://creators.vrchat.com/platforms/android/quest-content-optimization/ | 官方文档 | Quest 优化目标；遮挡剔除、相机、光照建议 | 官方文档 |
| S23 | https://docs.unity3d.com/2022.3/Documentation/Manual/OcclusionCulling.html | Unity 官方 | 烘焙遮挡剔除 | 官方文档 |
| S24 | https://docs.unity3d.com/2022.3/Documentation/Manual/LODGroup.html | Unity 官方 | LOD Group | 官方文档 |
| S25 | https://discussions.unity.com/t/some-kind-of-measurement-error/199223 | 社区（2017） | SDK2 报错 `<5 on a side`，修 Bounds Center/Extents | 社区实测（历史） |
| S26 | https://note.com/sasasa_sasa/n/n3adea88f2860 | 社区（2018） | 同一报错；改 Bounds | 社区实测（历史） |
| S27 | —（第三方 avatar 验证器文案，无稳定 URL） | 第三方工具 | 宣称“>5×6×5 阻止上传”，与 S1 冲突 | 不可靠（过时） |
| S28 | https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Time-fixedDeltaTime.html | Unity 官方 | fixedDeltaTime 默认 0.02 | 官方文档 |
| S29 | https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Physics.html | Unity 官方 | solver iterations 6 / velocity 1 / max angular 50 | 官方文档 |
| S30 | https://creators.vrchat.com/platforms/android/quest-content-limitations/ | 官方文档 | Quest 组件限制：avatar 的 Rigidbody/Collider/Joint 全禁 | 官方文档 |

## 9. 未记录 / 待实测

明确写“未记录”的项（不要猜）：

1. 世界包围盒 / 离原点最大距离的**官方**上限（只有社区浮点经验值）。
2. 最大碰撞体尺寸、最大刚体线速度的 **VRChat** 限制（PhysX 本身也没有公开硬上限）。
3. PC 世界的 draw call / 三角形 / 贴图**官方预算**。
4. Udon 的最大数组长度、UdonBehaviour 数量上限。
5. Far clip 的**当前**精确上限（官方只说“仍有限制”；50,000 是 2024-02 的修复值）。
6. “今天 SDK3 仍硬拦 5 m bounds”的说法（S1 明确否定；S27 是过时第三方）。
7. Udon 眼高到底是 **clamp 在 0.1–100 m**（S2）还是 **接受 0.01–10000、只支持 0.1–100**（S3）——两页官方文档冲突，需在客户端用 `SetAvatarEyeHeightByMeters` 实测。

建议我们自己在 Unity 里实测三件事（本轮不做）：

- 30 m 球 + 现有 FollowRig：把 `eyeUpMaxMeters` 临时调到 40，看相机是否真的在球外、Station 是否跟得上。
- 30 m 球 + `maxItemVolumeWorldM3` 512：确认哪些道具会被 512 卡住（按 30 m 球应能吸 ~1,414 m³）。
- far clip 1000 与 250（灰盒预览值）在 30 m 球顶的实际观感差异。
