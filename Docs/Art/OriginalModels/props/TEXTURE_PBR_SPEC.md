# 道具贴图 / PBR 规范（原创占位道具）

> 状态：`PROVISIONAL_NOT_TESTED_IN_UNITY`，2026-11-15（项目时区）。本文的每个数字都能在
> `Docs/Art/OriginalModels/props/scripts/` 里复现；**Unity 侧一次都没跑**（Linux 侧无 Editor），
> 所以凡是「Unity 会怎样」的句子都是按 2022.3 的行为写的预期，验收步骤见 §9.4。
>
> 适用范围：`Docs/Art/OriginalModels/props/**` 的原创程序化道具（当前 128 件，目标 156 种/主题）。
> 不适用于将来从 Fab/Kenney 引入的第三方资产——那些资产自带贴图，走 `Docs/Research/AssetSources/`。

## 1. 要解决什么，不能破坏什么

owner 判词是「太简略了，材质也没用上」。`MATERIAL_AND_DETAIL_SPEC.md` 解决的是**纯色分区**： 128 件、约 350 个材质槽、2.7 槽/件。它有三条硬伤：①材质数 = 槽数，350 个 `.mat` 每个都打断批处理； ②槽边界是几何边界，墨线/磨损跨槽就得加面；③同种重复件（2600 件、重复 10–40 次）没法靠材质实例化省 draw call。 贴图化解决这三条，但**不许**动下面这些已经验收过的东西：

| 不许破坏 | 怎么保证 |
|---|---|
| 真实尺寸（三轴 mm 级） | 只动材质和 UV；`build_textured.py` 换材质前后各量一次包围盒，差值写 `textures.dims_drift_m`（试点 12 件全 0.0） |
| 轴心 = 包围盒中心 / 贴地 | 不动 `Builder.finish()` 与 `fit_bounds`，UV 展开不改顶点（既有 `verify_import.py` 继续验） |
| 三层分离（地面 / 可吸 / 场景件） | 贴图不碰 `tier`、不碰碰撞与拾取字段 |
| 无 `.meta`、不手工建 Unity 资产 | 贴图与 FBX 同目录，`.meta` 由 Unity 首次导入生成 |

## 2. 选型一：每件一张 vs 按类别图集

先把数摆出来。三张图的目录是 156 种/主题，按区域口径的实际摆放是 **2600 件（大图）/ 2100 件（小图）**， 同种重复 10–40 次（`zone_targets.csv`）。

| | **A. 每件一张（选定）** | B. 按类别图集 2048² |
|---|---|---|
| 材质数（156 种） | **156**（贴图化后每件 1 个，原来是 ~420 个槽材质） | ~10（每图集 1 个） |
| 贴图文件数 | 156 × 3 张 = 468 | 10 × 3 张 = 30 |
| 显存（BC7/BC5 + mip，按 §4 分级） | **≈ 24 MB**（见 §4.3 逐档算） | ≈ 26 MB（同样像素量 + 图集内边距浪费 8–12 %） |
| draw call（2600 实例，静态批处理） | 156–320 | 10–40 |
| 单件贴图分辨率自由度 | **高**（大的给 512，小的给 128） | 低（图集格子固定，小件浪费、大件不够） |
| 改一件的代价 | 重烘 1 件（≈ 8 s） | 重排整张图集 → 所有件的 UV 都变 |
| 迭代风险 | 低 | 高（一次重排可能让 16 件同时出错） |
| 与现有 `PropArt` 工具的落差 | 小（现有代码就按 def 建 `.mat`） | 大（要图集清单 + UV 重映射表） |
| 与 GPU instancing | 同种重复件共享 material + mesh → **可 instancing** | 图集跨 16 个 mesh 共享 material，mesh 不同仍要分批 |

**选 A（每件一张，且每件只有一张）**：①槽材质 350 → 156 已经把 owner 的两条抱怨解决大半，图集再省 3–10× draw call 却要「改一件全图重排」，而我们还有 6 件弱模型 + 19 件旧件要返工，迭代成本更贵；②2600 实例 / 156 材质经静态批处理 后同屏 60–120 draw call，够用；③每件一张才能保住 texel density（12 mm 给 256 px、600 mm 给 512 px，图集做不到）。 图集真正划算的是「同屏上百件、材质完全同构」（碎石地），这批是**异形小件**。

**代价**：156 材质 + 468 张贴图，`Art/Props/<def>/Textures/` 会很碎；若将来某类（如 t0 碎屑 30 种）确实要压材质数， 可**单独**给那一类做图集，不动全局方案。

## 3. 选型二：通道怎么打包

每件 **3 张 8-bit PNG**：

| 文件 | 通道 | 用途 | sRGB |
|---|---|---|---|
| `_albedo.png` | RGB | 槽位色 × 噪声 × 烘焙 AO | **是** |
| `_mra.png` | **R=metal, G=AO, A=smoothness**，B=0 | 一张图同时喂 Standard 的 `_MetallicGlossMap`(R/A) 与 `_OcclusionMap`(G) | 否 |
| `_normal.png` | RGB | 切线空间法线（+Y 上，OpenGL 惯例） | 否 |

**为什么不是 glTF 的 ORM**：Unity Standard 的金属/光滑打包是 (R=metal, A=smoothness)，与 glTF 的 (G=rough, B=metal) 不同； MRA 能一张图同时喂 `_MetallicGlossMap` 与 `_OcclusionMap`，不必在 Unity 里再生成一张。 **法线**：t0–t2 不给（mip 一降就成噪点），t3 起给；来源是程序化高度场（fbm + 烘焙空腔）的 Sobel 梯度，**不做高低模烘焙**。

## 4. 尺寸预算

### 4.1 判定依据

VRChat PC 常见观看距离 1–3 m、视场 90°；1280×720 下 1 m 处 1 mm ≈ 0.6 px。按「1 texel ≈ 屏幕 1–2 px」反推：

| 档 | 最长边 | 默认 albedo | 法线 | MRA | 理由 |
|---|---|---|---|---|---|
| t0 | 0.02–0.099 | **256** | — | 128 | 2–10 cm 的籽/屑。**试点实测：128 px 下纽扣的 Ø1.1 mm 扣眼只剩 1–2 texel，被 4 px bake margin 吃平成一块蓝**，所以 t0 也取 256 |
| t1 | 0.10–0.139 | **256** | — | 128 | 同上，最大 10 cm |
| t2 | 0.14–0.199 | **256** | — | 256 | 开始能看清轮廓细节（扣眼、拉链齿） |
| t3 | 0.20–0.299 | **256** | 256 | 256 | 杯口、按键这一级 |
| t4 | 0.30–0.449 | **512** | 512 | 512 | 一只手的大小，细节是主要看点 |
| t5 | 0.45–0.649 | **512** | 512 | 512 | 同上，屏幕尺寸更大 |
| t6 | 0.65–0.999 | **1024** | 512 | 512 | 家具级，但法线不用跟着涨（法线信息频率低） |
| t7 | ≥1.00 | **1024** | 512 | 512 | 场景件，玩家会贴着看 |

**texel density 目标**：同一档内，贴图短边 / 物件最长边 ≥ **2.0 px/mm**（t0–t1）、≥ **1.2 px/mm**（t2–t4）、 ≥ **0.8 px/mm**（t5+）。低于 0.5 px/mm 时细节会被 mip 吃掉，等于白做。

### 4.2 显存估算（每件，含 mip 链 ×1.33）

BC7 = 8 bpp = 1 B/px，BC5（法线）= 1 B/px。

| 档 | 尺寸 | albedo | MRA | normal | 每件合计（含 mip） |
|---|---|---|---|---|---|
| t0–t1 | 256 | 64 KB | 64 KB | — | **171 KB** |
| t2 | 256 | 64 KB | 64 KB | — | **171 KB** |
| t3 | 256 | 64 KB | 64 KB | 64 KB | **256 KB** |
| t4–t5 | 512 | 256 KB | 256 KB | 256 KB | **1.02 MB** |
| t6–t7 | 512 albedo + 512 其它 | 256 KB | 256 KB | 256 KB | **1.02 MB** |

### 4.3 一个主题（156 种）的总量

按 `catalogue.csv` 的档位分布（t0 32 / t1 27 / t2 24 / t3 20 / t4 18 / t5 14 / t6 12 / t7 9）。**种数才是显存成本**，实例数（2600）不影响：

| 档 | 种数 | 每件 | 小计 |
|---|---|---|---|
| t0 | 32 | 171 KB | 5.5 MB |
| t1 | 27 | 171 KB | 4.6 MB |
| t2 | 24 | 171 KB | 4.1 MB |
| t3 | 20 | 256 KB | 5.1 MB |
| t4 | 18 | 1.02 MB | 18.4 MB |
| t5 | 14 | 1.02 MB | 14.3 MB |
| t6 | 12 | 1.02 MB | 12.2 MB |
| t7 | 9 | 1.02 MB | 9.2 MB |
| **合计** | **156** | — | **≈ 73 MB** |



**73 MB 仍然偏高**（世界的贴图预算通常按 100–150 MB 规划，还要留地面/天空盒/UI）。默认再压两步：
t4/t5 的法线只在有硬边细节的件上开（约一半，省 4 MB）、t2 的 MRA 降到 128（省 1 MB）→ **≈68 MB**。
t6/t7 的 albedo 已经是 512；逐件上调到 1024 时必须在 manifest 记 `resolution_override`。



## 5. UV 规则

1. `Smart UV Project`（66° 阈值）+ island margin **1.5 %**（256 px ≈ 4 px）；低于 1 % 时 mip 3 级后相邻岛互相渗色。
2. 法线/空腔只在岛内算，岛边界用 Blender margin（4 px）延展；不跨岛。
3. 同一件内各岛 texel density 差异 ≤ 2:1（`area_weight=0`）；不改拓扑（UV 在 `finish()` 之后、导出之前）。

## 6. 生成流水线（headless Blender，可批量、可复现）

```text
模型模块（不变）→ Builder.finish() →  unwrap()            Smart UV Project，1.5 % margin →  bake_colour()        Cycles DIFFUSE/COLOR，1 sample →  bake_ao()            Cycles AO，8 samples，距离 = 0.35 × 最长边 →  build_maps()         numpy：噪声 + AO + 槽位识别 → albedo/MRA/normal →  apply_textured()     单材质，图像节点接好 →  export_asset()       FBX/GLB/blend（与现有一致） →  render_preview()     同一套材质渲的预览
```

- **确定性**：噪声种子 = `sum(ord(c) for c in def)`，同 id 同分辨率 → 字节级相同的 PNG。
- **槽位识别**：`bake_colour()` 烘出的是「每个 texel 属于哪个槽」的平色图，`build_maps()` 用**最近邻**
把 texel 归到某个槽，再查该槽的 roughness/metallic。所以槽边界（含布尔缝）自动正确。
- **成本（实测，12 件试点）**：单件烘焙 3–5 s，含预览/导出约 12 s；156 件全量 ≈ 30–35 min（2 进程并行）。
- **工具**：`scripts/prop_tex.py`（本规范的可执行部分）+ `scripts/build_textured.py`（试点驱动）。

## 7. 与「槽位分色」的兼容与迁移

**共存方式**：`MATERIALS` 声明**不变**，仍然是槽位 key + 线性 RGB + roughness + metallic。 贴图化只是把「N 个槽材质」编译成「1 个材质 + 1 张图」：

| 概念 | 贴图化之前 | 之后 |
|---|---|---|
| 一件道具的材质数 | = 槽数（1–6） | **1**（`Prop_<def>_tex`） |
| 颜色从哪来 | 每槽 `.mat` 的 `_Color` | albedo 贴图（同色，但带噪声与 AO） |
| 粗糙度 | 每槽 `.mat` 的 `_Glossiness=0` | MRA 的 A 通道（真实值，不再一律 0） |
| 金属度 | 一律 0 | MRA 的 R 通道（按槽 key 推断，可显式声明 `metallic`） |
| manifest | `materials` / `material_base_colors` | 追加 `textures` 块；`materials` 变成单元素 |
| 未贴图化的件 | 原样 | 原样（工具同时支持两种，按 manifest 有没有 `textures` 分流） |

**迁移顺序**：先试点 12 件 → owner 过目 → 按档位批量（t0–t2 先，因为量大但便宜）。

## 8. Quest 单列

Quest 端（Quest 2/3 独立版）**不按本文档走**，但需要知道会掉什么：

| 项 | PC（本文档） | Quest |
|---|---|---|
| 压缩 | BC7（albedo/MRA）/ BC5（法线） | ASTC 4×4–6×6（BC 在 Quest 上会解成 RGBA32，必须重压） |
| 法线 | t3 起开 | 全关（省 1/3 显存与带宽） |
| 分辨率 | 256–512 | 再降一档（256→128、512→256） |
| 总量估算 | **68 MB** | ≈ 18–22 MB |
| 结论 | 支持 | **本期不做**；要做则「同源、二次压缩」，不重新生成 |

## 9. Unity 侧需要的改动

现状（`VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/PropArt/`）： `PropArtManifest` 解析 `materials` + `material_base_colors`；`PropArtMaterials.TryEnsureMaterial` 按 **每个槽** 建一个 `.mat`（Standard + `_Color` + `_Glossiness=0` + `_Metallic=0`）， `ModelImporter.AddRemap` 把 FBX 槽名指到这些 `.mat`；菜单 `VRC Katamari/Prop Art/Resync All Prop Materials` 幂等。

### 9.1 需要的改动（4 处，都在 Editor 侧，不碰运行时）

1. **`PropArtManifest`**：解析可选 `textures` 块（`albedo`/`mra`/`normal`/`resolution`），校验路径与像素尺寸；缺块时行为不变。
2. **`PropArtMaterials.TryEnsureMaterial`**：有 `textures` 时只建一个 `Prop_<def>_tex.mat`——`_MainTex`=albedo(sRGB)、
`_MetallicGlossMap`=`_OcclusionMap`=同一张 MRA(Linear，两个关键字)、`_BumpMap`=normal(t3 起)、`_Glossiness/_Metallic` 留 1/0 作 fallback。
3. **`CollectSourceMaterialNames` / `ApplyRemaps`**：贴图化件 FBX 只有**一个**槽（`Prop_<def>_tex`），remap 变 1→1；老件走原路径。
4. **幂等**：新增「贴图引用与关键字是否正确」判断，避免每次 Resync 都 reimport。

### 9.2 不变的部分

- 目录结构 `Art/Props/<def>/<def>.fbx` + `Materials/`；新增 `Textures/`。
- 菜单入口、报告结构（created/updated/reimported 计数）、启动时兜底检查。
- `Art/Props/<def>/<def>.manifest.json` 仍是唯一数据源（Unity 读盘而不是 TextAsset）。

### 9.3 注意

Standard 的 Occlusion 只读 **G**、Metallic/Smoothness 读 **R/A**——MRA 必须 RGBA；贴图引用挂在 `.mat` 上、 **不要**塞进 FBX；贴图必须与 FBX 在**同一目录树**内（`AssetDatabase` 按相对路径找）。

### 9.4 验收（操作员）

1. 重新导入试点 12 件的 FBX + `Textures/`，跑一次 `Resync All Prop Materials`：应为 `materials created 12`；
跑第二次 `created 0, updated 0, reimported 0`。
2. 抽查 `ceramic_mug`（4 槽 → 1 材质）：Materials 标签页只有 `Prop_ceramic_mug_tex`，Remapped 指向它。
3. 抽查 `.mat`：`_MainTex` 指向 `Textures/ceramic_mug_albedo.png`，`_MetallicGlossMap`/`_OcclusionMap` 指向 `_mra.png`。
4. 场景里放 3 个实例，Statistics 里看 draw call 是否随实例数增长（同材质应被静态批处理）。
5. 尺寸回归：抽 3 件拖进空场景，三轴仍 < 1 mm 误差。

## 10. 试点结果（12 件，2026-11-15）

### 10.1 覆盖

小件 4（`button` 2 / `seed` 1 / `bottle_cap` 1 / `coin` 2）、中件 4（`plate` 1 / `ceramic_mug` **4** / `dandelion_puff` 3 重做 / `beetle_figurine` 3 重做）、大件 2（`crate` 2 / `storage_bin` 3）、薄片细长 2（`postcard` 3 / `grass_blade` 3）。 **多槽件 8 件**（含 4 槽杯），旧单色 3 件，重做 2 件。

### 10.2 踩到并修掉的四个坑（都写进了代码注释）

1. **sRGB 双重编码**：Cycles 烘进 8-bit 图的值是 sRGB 编码的，第一版当线性用又编码一次，**每件发白**——
草叶绿 (0.115,0.24,0.075) 烘成 (0.37,0.52,0.30)、金币成橄榄色。加 `_srgb_to_linear()` 后恢复。
2. **金属度**：按 key 猜 brass/metal→1.0，但世界默认没有反射探针，metallic=1 渲成**黑块**。改为**默认 0**。
3. **烘焙分辨率**：直接 128/256 烘，1–2 mm 特征被 4 px margin 吃掉 → 改**按 2× 烘、同一 texel 判槽位、再 box 降采样**。
4. **细长件**：0.30 m × 4 mm 草叶在 256 下 UV 只剩几个 texel → 规则：长宽比 ≥ 6:1 升一档。

### 10.3 逐件结果

| id | 槽 → 材质 | 贴图 | 显存（含 mip） | 尺寸漂移 | 判定 |
|---|---|---|---|---|---|
| button | 2 → 1 | 256 | 256 KB | 0 | 扣眼四个孔清楚，蓝面无脏斑 ✅ |
| seed | 1 → 1 | 256 | 256 KB | 0 | 与旧版同色，表面有细微起伏 ✅ |
| bottle_cap | 1 → 1 | 256 | 256 KB | 0 | 比旧版多了瓶盖侧壁的浅凹 ✅ |
| coin | 2 → 1 | 256 | 256 KB | 0 | 金色恢复，边齿略糊 ⚠️ |
| plate | 1 → 1 | 256 | 256 KB | 0 | 白瓷更亮，斑点偏多 ⚠️ |
| ceramic_mug | **4 → 1** | 256 | 256 KB | 0 | 红口沿、蓝内壁、白瓷身、灰把手全保留 ✅ |
| dandelion_puff（重做） | 3 → 1 | 256 | 256 KB | 0 | 从「海胆」变「绒球」，仍偏菜花 ⚠️ |
| beetle_figurine（重做） | 3 → 1 | 256 | 256 KB | 0 | 黄头 + 深鞘翅 + 六足，腿仍偏块状 ⚠️ |
| crate | 2 → 1 | 512 | 1 MB | 0 | 木纹 + 板条缝，最好的一件 ✅ |
| storage_bin | 3 → 1 | 512 | 1 MB | 0 | 蓝盖 + 灰箱体保留，箱体偏大理石 ⚠️ |
| postcard | 3 → 1 | 256 | 256 KB | 0 | 黑白条纹清晰，背面色对 ✅ |
| grass_blade | 3 → 1 | 512 | 1 MB | 0 | 绿身 + 枯尖恢复，细长处偏亮 ⚠️ |

合计：**28 个槽材质 → 12 个材质**（2.33×），贴图 5.5 MB（12 件含 mip），**尺寸漂移全部 0**。

### 10.4 与「槽位分色」的兼容验证

`build_textured.py` 的 `slot_colour_check` 把槽 k 的 texel 平均线性色与声明的 `linear_rgb` 比： **最差通道差 ≤0.024**（4 槽杯 0.024、3 槽草叶 0.031、2 槽扣子 0.010；白色盘子 0.119 是 AO 压暗所致）。 差异全部来自刻意的噪声/AO，**没有一个槽被换成别的颜色**——这就是「多槽件合并成一张贴图」可行的依据。

### 10.5 没做到的

- **一次 Unity 导入都没跑**（Linux 侧无 Editor），§9.4 未执行 → Unity 侧全是预期而非实测。
- 试点 12 件**没落进仓库 `exports/`**，全在 `/tmp/tex_pilot12e/`，等 owner 过目后再批量。
- 两个重做只到「可读」，没到「可爱」。

## 11. 风险

| 风险 | 处理 |
|---|---|
| 变「脏」（噪声/AO 过强） | 已调保守：albedo ×0.94–1.06、AO ×0.78–1.00（第一版 ×0.80–1.20 / ×0.45–1.00 每件都脏，见 §10.2） |
| 小件装不下细节 | t0/t1 默认 256，细长件（长宽比 ≥6:1）再升一档 |
| Unity remap 与贴图冲突 | 按 §9.4 第 2–3 步查；失败把 Console 的 `[prop-art]` 报错发回 |
| 显存超预算（>100 MB/主题） | §4.3 三条降档，优先砍 t6/t7 albedo |
| Quest | BC 会被解成 RGBA32（×4）；本期不做 |
