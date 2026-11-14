# 原创占位小道具（第一批）

本目录是 PicnicLawn 地图第一批"可卷"小道具的原创、中性、可替换低多边形占位模型。全部几何由本目录的 Python 脚本在 Blender 3.6 里从 bmesh 图元程序化生成：没有第三方网格、没有贴图、没有品牌/角色/音乐/IP 元素，也没有引用任何现有游戏素材。**这些是占位美术，不是最终美术；未指定授权许可（no licence assigned），不得据此推断正式美术方向。**

## 1. 本批道具与来源

来源：`Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 中 sourcing hint 为 `model ourselves`（或并列可选自行建模）的小件。按"6–10 个最简单的小道具"取了 8 个；`napkin` 与 `watering_can` 在 CSV 里 priority 为 `later`，但形状简单、属于同一批占位目标，故一并纳入。

| 道具 | 中文名 | 三角面 | 顶点 | 尺寸 X×Y×Z（m，Blender） | 最长边 / CSV `game_longest_dim_m` | 枢轴 | 材质 |
|---|---|---|---|---|---|---|---|
| `seed` | 草籽 | 144 | 74 | 0.0224 × 0.0400 × 0.0160 | 0.0400 / 0.04 | 几何中心 | `Prop_seed_body` 麦秆黄 `#AD9559` |
| `acorn` | 橡子 | 220 | 112 | 0.0400 × 0.0380 × 0.0500 | 0.0500 / 0.05 | 底面中心 | `Prop_acorn_nut` `#A27645`／`Prop_acorn_cap` `#65452C` |
| `pebble` | 小石子 | 80 | 42 | 0.0600 × 0.0440 × 0.0319 | 0.0600 / 0.06 | 底面中心 | `Prop_pebble_stone` 灰石 `#7C7C79` |
| `bottle_cap` | 瓶盖 | 200 | 102 | 0.0500 × 0.0476 × 0.0145 | 0.0500 / 0.05 | 底面中心 | `Prop_bottle_cap_cap` 暗蓝灰 `#596979` |
| `paper_scrap` | 纸屑 | 164 | 84 | 0.1000 × 0.0741 × 0.0118 | 0.1000 / 0.10 | 底面中心 | `Prop_paper_scrap_paper` 旧纸白 `#CECBC2` |
| `toy_ball` | 小皮球 | 288 | 146 | 0.4000 × 0.4000 × 0.4000 | 0.4000 / 0.40 | 球心 | `Prop_toy_ball_shell` `#DAD4C8`／`Prop_toy_ball_band` `#9E3F3C` |
| `napkin` | 餐巾 | 192 | 98 | 0.3000 × 0.3000 × 0.0280 | 0.3000 / 0.30 | 底面中心 | `Prop_napkin_cloth` 亚麻米色 `#CBC5B8` |
| `watering_can` | 洒水壶 | 346 | 173 | 0.2603 × 0.5800 × 0.4154 | 0.5800 / 0.58 | 底面中心（壶身轴） | `Prop_watering_can_enamel` 灰绿搪瓷 `#596F59` |

合计 **1634 三角面**。预算：tiny 件 ≤300 tris，watering_can ≤600 tris，全部达标（最大 346）。

十六进制颜色是按 Blender 线性 base colour 换算的近似 sRGB 参考值；真正写入材质的是 `exports/<id>.manifest.json` 里的线性 RGB。

### 尺寸与实际比例

CSV 的 `real_longest_dim_m` 是现实尺寸，`game_longest_dim_m` 是游戏内放大后的尺寸。本批一律以 `game_longest_dim_m` 为准，因此最大边与 CSV 游戏尺寸完全一致（误差 < 1e-6 m），相对现实尺寸放大：

| 道具 | 现实 (m) | 游戏 (m) | 放大倍数 |
|---|---|---|---|
| seed | 0.02 | 0.04 | 2.00× |
| acorn | 0.03 | 0.05 | 1.67× |
| pebble | 0.04 | 0.06 | 1.50× |
| bottle_cap | 0.03 | 0.05 | 1.67× |
| paper_scrap | 0.10 | 0.10 | 1.00× |
| toy_ball | 0.15 | 0.40 | 2.67× |
| napkin | 0.15 | 0.30 | 2.00× |
| watering_can | 0.25 | 0.58 | 2.32× |

CSV 的这几项游戏尺寸（尤其 toy_ball 0.40 m、napkin 0.30 m 对应现实 0.15 m）属于"可卷物统一放大"的玩法比例，不是现实尺寸错误；本批按 CSV 执行，未改数值。

## 2. 单位与轴约定

- 1 Blender unit = 1 m；几何直接以米烘焙，所有物体 transform 为 identity（location 0 / rotation 0 / scale 1）。
- Blender：+Z 向上，模型正面朝 +Y。
- FBX 导出：`axis_forward='-Z'`、`axis_up='Y'`、`bake_space_transform=True`、`apply_scale_options='FBX_SCALE_UNITS'`；即顶点已烘焙成 Y-up，Unity 预期 Rotation=(0,0,0)、Scale=(1,1,1)、正面 +Z、直立。
- GLB 导出：`export_yup=True`，同样 Y-up。
- 枢轴：`seed`、`toy_ball` 在几何中心（球类需要绕中心旋转）；其余在底面中心、底面贴 z=0，可直接按地面高度摆放。
- 该轴/尺度约定是暂定接口，正式尺度与 Unity 导入验证尚未冻结；本批只做了 Blender 端 FBX/GLB 回读验证，**没有在 Unity 中导入**。

## 3. 拓扑与材质

每个网格都在生成脚本内做如下检查，结果写进 `exports/<id>.manifest.json` 与总 `manifest.json`：

- 0 非流形边、0 零面积面、0 松散顶点、0 n-gon（导出前全部三角化）、0 重复面；
- 单一闭合壳（`closed_shells: 1`）。`watering_can` 的壶身/壶嘴/提手用 Exact 布尔并集成一个封闭壳，接缝处没有埋在体内的封口面；`paper_scrap`、`napkin` 是闭合薄片，不是双面片。
- 法线已统一朝外；`seed`/`acorn`/`toy_ball` 平滑着色，`pebble`/`bottle_cap`/`paper_scrap`/`napkin`/`watering_can` 平直着色。
- 材质只有 Principled BSDF 的 Base Color（+ Roughness），**无贴图、无 UV、无顶点色**；`acorn` 与 `toy_ball` 各用 2 个纯色材质（壳斗/籽粒、球面/条纹），其余 1 个材质。
- 无 LOD、无碰撞体、无动画、无骨骼。

确定性：脚本只用固定公式（无随机数、无时间戳）。`geometry_sha256` 是与顶点/面遍历顺序无关的几何签名（坐标与三角面角点排序后哈希），连续两次完整构建逐件一致（见 `manifest.json` 每件 `geometry_sha256`）。预览像素逐字节稳定，但 Blender 会往 PNG 里写 `Date`/`RenderTime` 文本块，所以预览文件的 SHA-256 每次构建会变；导出文件同样只保证几何等价，不保证文件字节一致。

## 4. 文件说明

```
props/
├─ README.md                 本文件
├─ manifest.json             总清单：每件尺寸/面数/材质/检查结果/目录字段/轴约定
├─ verification.json         FBX 与 GLB 回读验证结果（独立进程）
├─ SHA256SUMS                所有交付文件校验和（sha256sum -c SHA256SUMS）
├─ scripts/
│  ├─ prop_lib.py            共用图元与构建/导出/渲染/拼图库（纯 bpy）
│  ├─ build_all.py           全批构建 + 预览 + 拼图 + 清单 + 校验和
│  ├─ build_prop.py          单件重建
│  ├─ verify_import.py       回读验证
│  ├─ sign.py                重新生成 SHA256SUMS（不重建模型）
│  └─ models/
│     ├─ seed.py acorn.py pebble.py bottle_cap.py
│     ├─ paper_scrap.py toy_ball.py napkin.py watering_can.py
│     └─ __init__.py         顺序与注册表
├─ exports/
│  ├─ <id>.fbx                Unity 用导出（Y-up、烘焙变换）
│  ├─ <id>.glb                glTF 二进制导出（含 baseColorFactor）
│  ├─ <id>.blend             该件的干净源文件（只含模型，无相机/灯光）
│  └─ <id>.manifest.json     单件清单
└─ previews/
   ├─ <id>_preview.png       512×512、透明背景、统一相机与三点光
   └─ contact_sheet.png      8 件拼图（2156×1216，含名称/尺寸/面数）
```

## 5. 重新构建

Windows（Blender 3.6，后台实例，不碰 GUI）：

```
"C:\Program Files\Blender Foundation\Blender 3.6\blender.exe" ^
  --background --factory-startup --disable-autoexec ^
  --python D:\VRChatKatamariDanacy\Docs\Art\OriginalModels\props\scripts\build_all.py
```

单件重建（例如草籽）：

```
"...\blender.exe" --background --factory-startup --disable-autoexec ^
  --python D:\...\props\scripts\build_prop.py -- seed
```

回读验证：

```
"...\blender.exe" --background --factory-startup --disable-autoexec ^
  --python D:\...\props\scripts\verify_import.py
```

脚本按自身位置推路径：输出写到同级 `exports/`、`previews/`，清单写到 `props/` 根；也读取仓库内 `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 核对 `game_longest_dim_m`（找不到 CSV 时该核对字段为 `row_found: false`，构建仍可进行）。不需要安装任何 Blender 插件，也不访问网络。

## 6. 验证记录

- 8 件 FBX 回读到全新 factory-empty Blender 3.6.21：三角面数与源网格逐一相同，世界尺寸误差 < 5e-4 m，枢轴正确，只生成 1 个 mesh 对象、无相机/灯光/空物体。
- 8 件 GLB 回读：三角面数同样逐一相同；顶点数偏高是 glTF 按法线拆分顶点所致，属正常现象（例如 `pebble` 42 → 240）。
- FBX 回读时 Blender 导入器会给对象加 +90° X 轴的导入转换旋转（Y-up→Z-up），这是导入器行为；世界尺寸与 Z-up 朝向正确，Unity 端预期仍为 identity。**Unity 侧未验证。**
- 预览均为 512×512 RGBA 透明 PNG，非空（134–210 KB），拼图 714,434 字节（约 698 KiB）；相机/光照/取景规则对所有道具一致。

逐件数值见 `verification.json`。

## 7. 已知偏差与说明

1. `seed`：CSV 提示是"Ø0.04 m 椭球 + 1 张 alpha 草卡"。本批不使用贴图，因此只做实体籽粒；若需要草卡，应另做一张 alpha 卡并在 Unity 里合成，而不是塞进这个 mesh。
2. `paper_scrap`：CSV 提示"单面薄片，双面材质"。改成 1.4 mm 厚的闭合薄片（带卷边与撕裂轮廓），单面材质即可正确显示且保持水密；代价是三角面 164 而非约 2 面。
3. `acorn`、`toy_ball` 各 2 个材质：壳斗/籽粒、球面/双色条纹是造型必需的纯色区分，仍满足"无贴图"。
4. `bottle_cap`：按 CSV 游戏尺寸 Ø0.05 m；现实瓶盖约 0.03 m，故偏大（1.67×）。齿边用 20 段圆周上的交替半径表达（10 个齿），没有做真实压接结构。
5. `napkin`、`watering_can` 在 CSV 中 priority 为 `later`；本批按"最简单小道具"提前做了占位。
6. `pebble`：CSV 给的是 Kenney Nature Kit `rock_small*`（标注渲染不符、需人工复核）或自行建模；本批选自行建模，避免第三方素材。
7. `watering_can` 的壶嘴沿 +Y（Blender 正面）伸出，最长边即壶嘴方向；壶身轴在原点，包围盒在 X/Y 上不对称是设计意图。
8. 未做：UV、贴图、顶点色、LOD、碰撞体、动画、Unity 导入/材质重建。FBX 不保存 Principled base colour，Unity 会新建材质；需要按 `manifest.json` 的 `material_base_colors` 重建，或改用 GLB（内含 baseColorFactor）。

## 8. 授权

这些模型是本项目自行生成的原创占位资产。**未指定任何授权许可（no licence assigned）**；在许可证决定之前，不应把它们当作可对外再分发的最终美术，也不要从本目录推断玩法实体数、碰撞体、价值或正式美术方向。

Unity 端导入、材质赋值、碰撞与 prefab 组装由 Unity operator 复核；本目录只负责 Blender 端建模与导出。

## 9. 美术档位决定（2026-11-14）

用户确认：**布局冻结前保持纯色、无贴图**。理由是这个阶段的判断对象是尺寸、轮廓和在球面上的可读性，贴图既帮不上忙又会掩盖比例问题；等 PicnicLawn 灰盒布局定稿后，再统一按材质族（纸/木/塑料/金属/织物）做一次贴图图集与 UV 的美术升级，避免按未定布局做两遍。
