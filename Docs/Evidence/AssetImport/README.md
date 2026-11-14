# 素材入库：占位音效与原创小道具导入 Unity

## 结论

**导入完成，无 console 错误，EditMode 仍 158/158。** 23 个占位音效 WAV 与 8 个原创占位道具 FBX 已进入 Unity 工程，导入设置按本任务要求显式设置并逐项回读确认；8 件道具在 Unity 内的实测最长边与 `manifest.json` 记录**完全一致（误差 0.00%）**，三角面数与材质数也逐件吻合。

- Windows 工作区 HEAD：`0cb010e`（`git pull --ff-only`，此前 `9e49b21`）
- 本次只做**导入**，未修改任何游戏代码；`Runtime/` 下 `.cs` 未触碰
- 工作区遗留保持原状：4 个 `SerializedUdonPrograms` signature-only 改动 + `ProjectSettings/ProjectSettings.asset`（未读、未改、未 stage）

## 1. 音效（23 个 WAV）

来源 `Docs/Art/Audio/rendered/`（见 [音效设计](../../Art/Audio/README.md)），按设计文档 §4 的用途分类落到子目录：

| 目录 | 文件 | 数量 | 设计依据 |
| --- | --- | --- | --- |
| `Audio/Sfx/Roll/` | `sfx_roll_small/medium/large` | 3 | §4.1 滚动底噪（无缝循环，占固定 ambience 槽） |
| `Audio/Sfx/Pickup/` | `sfx_pickup_01..05`、`sfx_pickup_big` | 6 | §4.2 拾取确认 |
| `Audio/Sfx/Growth/` | `sfx_growth_step` | 1 | §4.2 成长档位 |
| `Audio/Sfx/Reject/` | `sfx_bump_reject` | 1 | §4.2 拒绝（共用中性音） |
| `Audio/Sfx/Powerup/` | `sfx_magnet_activate`、`sfx_magnet_loop`、`sfx_rocket_boost` | 3 | §4.3 道具 |
| `Audio/Sfx/Threat/` | `sfx_threat_warning`、`sfx_swallow_ko`、`sfx_respawn` | 3 | §4.4 威胁 / KO / 重生 |
| `Audio/Sfx/Round/` | `sfx_round_countdown`、`sfx_round_start`、`sfx_round_end` | 3 | §4.5 回合 |
| `Audio/Sfx/Bank/` | `sfx_bank_deposit` | 1 | §4.5 交分提交 |
| `Audio/Sfx/UI/` | `sfx_ui_click`、`sfx_ui_hover` | 2 | §4.5 2D UI |

### 导入设置（逐项回读确认）

| 设置 | 值 | 说明 |
| --- | --- | --- |
| Load Type | `DecompressOnLoad` | 全部 23 个（短音效，避免首次播放的流式延迟） |
| Compression Format | `Vorbis` | 全部 23 个 |
| Quality | **0.7**（一次性）/ **1.0**（4 个循环）`PROVISIONAL` | 项目自选值，见下方"取值说明" |
| Force To Mono | **关**（`forceToMono: 0`） | 源素材本身即单声道，关掉不会损失立体声信息，也不会误做下混 |
| Load In Background | **关**（`loadInBackground: 0`） | 与 `preloadAudioData: 1` 配合，短音效不留首次播放空档 |
| Preload Audio Data | 开 | `AudioImporterSampleSettings.preloadAudioData = 1` |
| Loopable | 开（仅 `sfx_roll_*`×3 与 `sfx_magnet_loop`） | 与 `rendered/manifest.csv` 的 `loop=yes` 一致 |

**取值说明（重要）**：[音效 README §11](../../Art/Audio/README.md) 明确写着"没有导入 Unity，未设置 `Load Type / Compression`…需在 T23 里定"，即**设计文档没有给出 Vorbis 质量值**。

因此 **`Quality = 0.7`（23 个短音效中的 19 个一次性音效）与 `Quality = 1.0`（4 个循环：`sfx_roll_small/medium/large` + `sfx_magnet_loop`）标记为 `PROVISIONAL`**——这是项目自选值，不是文档既有结论，也不是实测最优值：

- 短音效统一 **0.7**：时长都在 0.9 s 以内，码率影响很小，主要目的是控制体积与解码开销；
- 循环底噪用 **1.0**：滚动 bed 与磁铁 bed 是持续循环音，编码器在这个质量档位上的预回声/相位失真最低，避免接缝与持续音上的"咕噜"伪影。

审批状态：Lead 已接受这两个数字作为项目自选值（2026-11-14）。若 T23 定出别的值，只需改这两个数并重导；不影响其他设置。

### 导入后回读（格式与时长未被改动）

23 个 clip 全部为 **48000 Hz / 1 声道**，时长与 `manifest.csv` 的 `duration_s` **逐条零偏差（0.0 ms）**，采样点数亦与 48 kHz×时长精确吻合（如 `sfx_ui_hover` = 1680、`sfx_round_end` = 81600）。完整表见本次执行输出；要点：

- 最长 `sfx_roll_large` 2.2000 s / 105600 samples，最短 `sfx_ui_hover` 0.0350 s / 1680 samples
- `channels=1` 全部成立，说明没有发生立体声下混或声道复制

## 2. 道具（8 个 FBX）

来源 `Docs/Art/OriginalModels/props/exports/`，每个道具一个目录：

```
Art/Props/<prop_id>/
├── <prop_id>.fbx
├── <prop_id>.fbx.meta
└── Materials/            ← Unity 提取的材质（ImportStandard + External）
    ├── Prop_<..>.mat
    └── Prop_<..>.mat.meta
```

**只导入 FBX。** `*.glb` 与 `*.blend` 未复制进 `Assets/`：本机 Unity 未装 Blender 桥，`.blend` 会报错；GLB 与 FBX 是同一几何的两份导出，导入两份会产生重复资产。二者继续留在 `Docs/Art/OriginalModels/props/exports/` 作为来源与校验基准。

### 导入设置（逐件回读确认）

| 设置 | 值 | 理由 |
| --- | --- | --- |
| Global Scale | **1** | 任务要求 |
| Use File Scale | **关**（回读 `fileScale = 1`） | 几何已在 Blender 端以米烘焙（`bake_space_transform=True`）；关闭文件单位换算，避免再乘一次 0.01 |
| Generate Colliders | **关**（`addCollider: false`） | 碰撞由灰盒数据决定，不由导入网格生成 |
| Read/Write | **关**（`isReadable: false`） | 运行时不需要 CPU 读网格，省内存 |
| Mesh Compression | **Off** | 任务要求 |
| Material Import Mode | `ImportStandard` + `External` | 材质落到同目录 `Materials/`，而不是嵌进 prefab |
| Animation | `importAnimation: false` / `animationType: None` | 这批模型无动画、无骨骼 |

### 尺寸核对（Unity `mesh.bounds` × `lossyScale` vs `manifest.json`）

| prop_id | 期望最长边 (m) | 实测最长边 (m) | 误差 | 实测 X×Y×Z (m) | 三角面 | 材质 | 枢轴 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| acorn | 0.0500 | 0.05000 | **0.00%** | 0.0400 × 0.0500 × 0.0380 | 220 | 2 | 底面（minY=0） |
| bottle_cap | 0.0500 | 0.05000 | **0.00%** | 0.0500 × 0.0145 × 0.0476 | 200 | 1 | 底面（minY=0） |
| napkin | 0.3000 | 0.30000 | **0.00%** | 0.3000 × 0.0280 × 0.3000 | 192 | 1 | 底面（minY=0） |
| paper_scrap | 0.1000 | 0.10000 | **0.00%** | 0.1000 × 0.0118 × 0.0741 | 164 | 1 | 底面（minY=0） |
| pebble | 0.0600 | 0.06000 | **0.00%** | 0.0600 × 0.0319 × 0.0440 | 80 | 1 | 底面（minY=0） |
| seed | 0.0400 | 0.04000 | **0.00%** | 0.0224 × 0.0160 × 0.0400 | 144 | 1 | 几何中心 |
| toy_ball | 0.4000 | 0.40000 | **0.00%** | 0.4000 × 0.4000 × 0.4000 | 288 | 2 | 球心 |
| watering_can | 0.5800 | 0.58000 | **0.00%** | 0.2603 × 0.4154 × 0.5800 | 346 | 1 | 底面（minY=0） |

无一件误差超过 5%，因此**不需要缩放修正**，也没有做任何缩放。

附带确认的两件事：

1. **三角面数与 manifest 逐件相等**（144 / 220 / 80 / 200 / 164 / 288 / 192 / 346，合计 1634），说明导入没有重网格化或丢面。
2. **轴向转换正确**：Blender 是 +Z 向上、正面 +Y；导入后 acorn 的 Blender Z 边（0.05）出现在 Unity Y，Blender Y 边（0.038）出现在 Unity Z；watering_can 同理（0.4154 到 Y、0.58 到 Z）。即 Y-up 转换按 manifest 的 `unity_intended`（rotation 0 / scale 1）落地，模型直立、正面 +Z。
3. **枢轴与设计一致**：`seed`、`toy_ball` 在几何中心（球类要绕中心滚），其余 6 件 minY=0（底面贴地可直接按地面高度摆放）。

### 材质

10 个材质，名称与 manifest 的 `materials` 完全对应（acorn 与 toy_ball 各 2 个，其余各 1 个），着色器均为 `Standard`，Base Color 与 manifest 记录的线性 RGB 一致（例：`Prop_seed_body` = (0.420, 0.300, 0.100)，`Prop_toy_ball_shell` = (0.700, 0.660, 0.580)）。Smoothness 回读为 **0.00**，即导入后全部为全粗糙哑光——原因是 FBX 里没有 metallic/smoothness 贴图，Unity Standard 用默认值；对占位道具是可接受的观感，但设计文档提到的 Roughness 并没有真正带进来。

## 3. 门禁

| 项 | 结果 |
| --- | --- |
| `refresh_unity`（force / all / request / wait） | 成功，`editor is ready` |
| `read_console`（error + warning） | **0 error / 0 warning**（唯一一条是 MCP 自身的 WebSocket 提示，非导入或编译问题） |
| EditMode（`VRCKatamari.Tests.EditMode`） | **158 / 158 Passed**，0 failed / 0 skipped（job `bb7aa72dd4e54730b4cb296fa6569d37`） |

导入不影响既有测试，符合预期。

## 4. 清点

111 个新建文件 = 23 WAV + 8 FBX + 10 .mat + 70 个 .meta（含 9 个音效目录、8 个道具目录、8 个 `Materials` 目录、`Audio`/`Art`/`Props` 三个父目录）。路径与 MD5 逐条列在 [inventory.md](inventory.md)；配对检查结果：41 个资产全部有对应 `.meta`，**缺失 0**。

## 5. 素材质量评价（第一手）

### 音效

- **判断依据**：`rendered/manifest.csv` 的时长/峰值/循环接缝数据、README §4 的分层设计、导入后回读的格式；**没有实际试听**（见 §6）。
- **已经可用**：`sfx_ui_click`/`sfx_ui_hover`（0.055 s / 0.035 s，−7.5 / −12.4 dBFS，做 2D UI 反馈足够）；`sfx_bump_reject`（0.20 s 闷响，中性、和成功音区分明确，正好承担"吸不动/交分被拒/受击"三处复用）；`sfx_round_countdown`（0.14 s 干净双音，倒计时用途明确）。
- **占位感明显但可用**：`sfx_pickup_01..05`（0.13 s 上滑音，五个变体差异主要在基频，实际听感会很接近，选变体的意义有限）；`sfx_growth_step`、`sfx_bank_deposit`（都是三音/四音铃，音色偏"电子味"，能传达"升级/存入"但不像成品音乐）。
- **占位感最强**：三个 `sfx_roll_*` 滚动循环。它们是低频正弦+颗粒噪声+压地脉冲拼出来的，能证明"体积感随球径变化"这个设计，但没有真实碾压材质的高频碎屑，长时间听容易像持续的合成嗡声。README §9 自己也把"滚动循环真实质感、火箭、KO、交分铃"列为最弱项。`sfx_rocket_boost`（0.95 s 噪声呼+推背）与 `sfx_swallow_ko`（1.1 s 下行咕噜）在同类里算合格的一次性事件音。
- **音量层面**：文件峰值 −1.94 ~ −12.40 dBFS，全部低于 −1 dBFS 硬上限，无削波；但这只是**文件级**，进入 VRChat 后的实际听感取决于引擎 trim 与 ducking，本轮完全未验证。

### 道具

- 视觉上（`Docs/Art/OriginalModels/props/previews/contact_sheet.png` 逐件看过）：`acorn`、`toy_ball`、`watering_can` 的剪影和比例已经很能读出来，作为占位**接近可用**；`pebble`、`bottle_cap`、`seed` 也一眼可辨。
- **最弱两件**：`paper_scrap` 与 `napkin` 本质是"带一点起伏的挤出四边形"，读起来像空白卡片/纸板，缺少撕裂边、褶皱等能区分"纸屑"与"餐巾"的特征。按玩法尺寸（0.10 m / 0.30 m）出现在画面里时，二者很可能难以区分。
- **比例是有意放大的**：napkin 0.30 m、toy_ball 0.40 m 相对现实尺寸放大约 2×/2.67×（`prop_catalog.csv` 的 `game_longest_dim_m`），这是"可卷物统一放大"的玩法决定，不是导入错误。
- 全部无贴图、无 UV、纯色 Principled 基色；在 VRChat 的光照下会呈现干净的低多边形占位观感，但也没有任何材质细节可以救近景。

## 6. 本次明确未验证的部分

- **未正式试听**：没有在 Unity 里播、也没有在 VRChat / VR 头显里听过任何一个音效；"能不能听清、会不会刺耳、4 球叠加后是否糊"全部未测。本轮只验证了导入设置、格式、时长、峰值等客观量。
- **未做空间化/混音设置**：`Load Type / Compression` 已设，但 3D 衰减曲线、`AudioSource` 的 loop 接线、总线 trim、ducking 全部未做（属于 T23 与后续实现工作）。
- **循环只设了 `loopable` 标记**：Unity 没有 clip 级 loop 开关，真正的循环由 `AudioSource.loop` 决定，而本轮没有创建任何 `AudioSource`；因此"4 个循环实际能否无缝回绕"未验证。
- **素材未进任何场景或 prefab**：没有挂到 `StationPlatformLab`，也没有生成道具 prefab、没有配碰撞、没有接 `PickupAbsorb` 候选数据，所以"能不能被吸、尺寸在玩法里是否合适"未验证。
- **材质着色器未按项目规范统一**：当前是 Unity `Standard`；如果项目最终统一到 VRChat 专用着色器，需要再替换（未做，也不在本次范围）。
- 未 commit / push（由 Lead 负责）；`Docs/Evidence/AssetImport/` 下两个文件是本轮新增的文本证据，需要随资产一起入库。
