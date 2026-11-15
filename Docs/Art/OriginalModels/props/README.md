# 原创占位小道具（第一批）

> 2026-11-15 更新：第 10 节新增第二批（`cookie`/`orange` 重做 + 8 件餐具/容器）；第 1–9 节仍描述第一批 8 件。第二批产物只在 Windows staging，未复制回本仓库 `exports/` / `previews/`。

本目录是 PicnicLawn 地图第一批"可卷"小道具的原创、中性、可替换低多边形占位模型。全部几何由本目录的 Python 脚本在 Blender 3.6 里从 bmesh 图元程序化生成：没有第三方网格、没有贴图、没有品牌/角色/音乐/IP 元素，也没有引用任何现有游戏素材。**这些是占位美术，不是最终美术；未指定授权许可（no licence assigned），不得据此推断正式美术方向。**

## 1. 本批道具与来源

来源：`Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 中 sourcing hint 为 `model ourselves`（或并列可选自行建模）的小件。按"6–10 个最简单的小道具"取了 8 个；`napkin` 与 `watering_can` 在 CSV 里 priority 为 `later`，但形状简单、属于同一批占位目标，故一并纳入。

| 道具 | 中文名 | 三角面 | 顶点 | 尺寸 X×Y×Z（m，Blender） | 最长边 / CSV `game_longest_dim_m` | 枢轴 | 材质 |
|---|---|---|---|---|---|---|---|
| `seed` | 草籽 | 144 | 74 | 0.0224 × 0.0400 × 0.0160 | 0.0400 / 0.04 | 几何中心 | `Prop_seed_body` 麦秆黄 `#AD9559` |
| `acorn` | 橡子 | 220 | 112 | 0.0400 × 0.0380 × 0.0500 | 0.0500 / 0.05 | 几何中心 | `Prop_acorn_nut` `#A27645`／`Prop_acorn_cap` `#65452C` |
| `pebble` | 小石子 | 80 | 42 | 0.0600 × 0.0440 × 0.0319 | 0.0600 / 0.06 | 几何中心 | `Prop_pebble_stone` 灰石 `#7C7C79` |
| `bottle_cap` | 瓶盖 | 200 | 102 | 0.0500 × 0.0476 × 0.0145 | 0.0500 / 0.05 | 几何中心 | `Prop_bottle_cap_cap` 暗蓝灰 `#596979` |
| `paper_scrap` | 纸屑 | 164 | 84 | 0.1000 × 0.0741 × 0.0118 | 0.1000 / 0.10 | 几何中心 | `Prop_paper_scrap_paper` 旧纸白 `#CECBC2` |
| `toy_ball` | 小皮球 | 288 | 146 | 0.4000 × 0.4000 × 0.4000 | 0.4000 / 0.40 | 几何中心 | `Prop_toy_ball_shell` `#DAD4C8`／`Prop_toy_ball_band` `#9E3F3C` |
| `napkin` | 餐巾 | 192 | 98 | 0.3000 × 0.3000 × 0.0280 | 0.3000 / 0.30 | 几何中心 | `Prop_napkin_cloth` 亚麻米色 `#CBC5B8` |
| `watering_can` | 洒水壶 | 346 | 173 | 0.2603 × 0.5800 × 0.4154 | 0.5800 / 0.58 | 几何中心 | `Prop_watering_can_enamel` 灰绿搪瓷 `#596F59` |

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
- 枢轴：2026-11-15 起所有道具统一在几何包围盒中心（`prop_lib.Builder.finish()` 在 `transform_apply` 后用 `origin_set` 取包围盒中心，再复位到原点，导出时烘焙进顶点）。场景与运行期接触判定都按 AABB 中心放置，回读误差 < 1 mm。
- 该轴/尺度约定是暂定接口，正式尺度与 Unity 导入验证尚未冻结；本批只做了 Blender 端 FBX/GLB 回读验证，**没有在 Unity 中导入**。

## 3. 拓扑与材质

每个网格都在生成脚本内做如下检查，结果写进 `exports/<id>.manifest.json` 与总 `manifest.json`：

- 0 非流形边、0 零面积面、0 松散顶点、0 n-gon（导出前全部三角化）、0 重复面；
- 单一闭合壳（`closed_shells: 1`）。`watering_can` 的壶身/壶嘴/提手用 Exact 布尔并集成一个封闭壳，接缝处没有埋在体内的封口面；`paper_scrap`、`napkin` 是闭合薄片，不是双面片。
- 法线已统一朝外；`seed`/`acorn`/`toy_ball` 平滑着色，`pebble`/`bottle_cap`/`paper_scrap`/`napkin`/`watering_can` 平直着色。
- 材质只有 Principled BSDF 的 Base Color（+ Roughness），**无贴图、无 UV、无顶点色**；`acorn` 与 `toy_ball` 各用 2 个纯色材质（壳斗/籽粒、球面/条纹），其余 1 个材质。
- 无 LOD、无碰撞体、无动画、无骨骼。

确定性：脚本只用固定公式（无随机数、无时间戳）。`geometry_sha256` 是与顶点/面遍历顺序无关的几何签名（坐标与三角面角点排序后哈希），连续两次完整构建逐件一致（见 `manifest.json` 每件 `geometry_sha256`）。

字节层面只有一部分稳定（2026-11-15 实测）：`exports/<id>.manifest.json` 与 `previews/<id>_preview.png` 逐字节一致；`.blend` / `.fbx` 每次重跑字节都会变（内嵌时间戳与绝对路径），只保证几何等价。所以 `SHA256SUMS` 是**当前交付物的指纹**（用来核对文件有没有被改动），不是"重跑能对上"的复现校验门；要字节级复现须固定 Blender 3.6.21 并使用同一输出路径（现存产物即 3.6.21 生成）。完整实测结论见上级 [README 的复现小节](<../README.md>)。

## 4. 文件说明

```
props/
├─ README.md                 本文件
├─ manifest.json             总清单：每件尺寸/面数/材质/检查结果/目录字段/轴约定
├─ verification.json         FBX 与 GLB 回读验证结果（独立进程）
├─ SHA256SUMS                当前交付物的指纹（sha256sum -c SHA256SUMS 只能证明文件没被改动，不等于可重跑复现）
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

后台实例（不碰 GUI）。参数必须齐全：`--background` 独立进程、`--factory-startup` 不读也不写用户配置、`--disable-autoexec` 关掉自动执行、**`--python-exit-code 2`** 让脚本抛错时进程以非 0 退出（缺了它，Blender 可能带着 Python 报错返回 0）。输出一律先指到**仓库外的全新目录**：

```
set BLENDER=<...>\steamapps\common\Blender\blender.exe
set OUT_DIR=%TEMP%\PropsRebuild

"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python Docs\Art\OriginalModels\props\scripts\build_all.py
```

等价写法（`--` 之后的 `--out` 优先于 `OUT_DIR`）：

```
"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python Docs\Art\OriginalModels\props\scripts\build_all.py -- --out %TEMP%\PropsRebuild
```

**警告**：不给 `OUT_DIR` / `--out` 时脚本保持历史行为，直接覆盖仓库里的 `exports/`、`previews/`、`manifest.json` 与 `SHA256SUMS`。只有确实要更新已提交的交付物时才这么跑，改完再单独提交产物。

单件重建（例如草籽），同样建议给输出目录：

```
"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python Docs\...\props\scripts\build_prop.py -- seed --out %TEMP%\PropsRebuild
```

回读验证：

```
"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python Docs\...\props\scripts\verify_import.py
```

脚本按自身位置推路径：默认写同级 `exports/`、`previews/`，清单与 `SHA256SUMS` 写到 `props/` 根，`OUT_DIR` / `--out` 可整体重定向到仓库外。目录数据读仓库内 `Docs/Design/Maps/PicnicLawn/prop_catalog.csv`，逐件核对 `game_longest_dim_m`。**这份输入缺失或不可用是硬失败**：CSV 不存在、没有 `id` 列、某件道具不在 CSV 里、`game_longest_dim_m` 为空或非数字，都会抛 `BuildInputError`，打印 `BUILD_FAILED: ...` 并以退出码 2 结束（`_fail()` 里 `sys.exit(2)`），不会写出任何清单。为什么要硬失败：以前缺 CSV 会静默记 `row_found: false` 继续构建，产出的是一份"看起来完整"的坏清单——尺度核对恰是这批占位道具唯一的数值契约，静默降级等于让坏构建伪装成成功构建。构建与验证都不需要安装任何 Blender 插件，也不访问网络。

版本：脚本自 Blender 3.6 起可用；`prop_lib.py` 按功能探测渲染引擎（3.6 / 4.0–4.1 / 5.0+ 为 `BLENDER_EEVEE`，4.2–4.5 为 `BLENDER_EEVEE_NEXT`），并对 EEVEE Next 移除的 `use_gtao` 做 `hasattr` 守卫。4.2+ 没有这个 AO 开关，预览会少一层接触阴影，**像素与 3.6 产物不同**（几何、清单与拼图布局不受影响）；已提交的产物仍是 3.6.21 生成的。

## 6. 验证记录

- 8 件 FBX 回读到全新 factory-empty Blender 3.6.21：三角面数与源网格逐一相同，世界尺寸误差 < 5e-4 m，枢轴正确，只生成 1 个 mesh 对象、无相机/灯光/空物体。
- 8 件 GLB 回读：三角面数同样逐一相同；顶点数偏高是 glTF 按法线拆分顶点所致，属正常现象（例如 `pebble` 42 → 240）。
- FBX 回读时 Blender 导入器会给对象加 +90° X 轴的导入转换旋转（Y-up→Z-up），这是导入器行为；世界尺寸与 Z-up 朝向正确，Unity 端预期仍为 identity。**Unity 侧未验证。**
- 预览均为 512×512 RGBA 透明 PNG，非空（134–210 KB），拼图 714,434 字节（约 698 KiB）；相机/光照/取景规则对所有道具一致。
- 版本覆盖：上表数值与已提交产物全部出自 **Blender 3.6.21**。EEVEE Next 兼容修复（`prop_lib.py` 的引擎探测与 `use_gtao` 守卫）只做到代码层；在 Blender 5.3 上整批构建（8 件、58 个文件、`TOTAL tris=1634`）目前只在一次性临时副本里验证过，**尚未用本仓库当前脚本重跑复核**。

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

## 10. 第二批：水果重做 + 餐具/容器（2026-11-15）

> 第 1–9 节仍描述第一批 8 件（seed … watering_can）。本节是 2026-11-15 新增的第二批：重做 `cookie`、`orange`，并新增 8 件真实尺寸的餐具/容器。第二批的产物只写到 Windows staging `D:\VRChatKatamariPreview\PropModels\tableware-v1\`，**没有**复制回本仓库的 `exports/` / `previews/`；仓库里现有的水果 FBX/GLB/预览仍是旧版，等 Lead 统一合入时再替换。

与第 1 批不同，这批使用 `SIZE_REFERENCE = 'real'`：最长边对齐 CSV 的 `real_longest_dim_m`（不是第 1 批的 `game_longest_dim_m` 放大比例）。8 件的目录 id 为 `cup`、`soda_can`、`juice_carton`、`glass_bottle`、`plate`、`bowl`、`thermos`、`croissant`；其中 `juice_box` 在目录里的真实 id 是 `juice_carton`，`baguette` 在 CSV 里没有行、也没有真实尺寸，故没有凭空造 0.65 m，改用同属烘焙类的 `croissant`（目录真实三轴 0.12 × 0.07 × 0.06 m）。

| 道具 | 中文名 | 规格（设计/CSV，m） | 实测 X×Y×Z（m） | 面数 / 预算 | 壳 | 材质 | 肉眼判断 |
|---|---|---|---|---|---|---|---|
| `cookie` | 饼干 | Ø0.07 × 0.009 厚 | 0.0700 × 0.0689 × 0.0094 | 326 / 340 | 1 | 面团 + 巧克力豆 | 一眼是饼干；5 颗凸起巧克力豆，边缘轻微波浪 |
| `orange` | 橙子 | Ø0.08，微扁 | 0.0800 × 0.0800 × 0.0774 | 360 / 360 | 1 | 橙皮 + 绿梗 | 一眼是橙子；光滑无瓣线，小绿梗在顶 |
| `cup` | 杯子 | 0.075 × 0.075 × 0.09 | 0.0750 × 0.0750 × 0.0900 | 280 / 300 | 1 | 纸杯米白 | 一眼是纸杯；空心、上大下小，平直着色有 20 面棱 |
| `soda_can` | 易拉罐 | 0.066 × 0.066 × 0.12（CSV 0.07 是罐径） | 0.0661 × 0.0661 × 0.1200 | 320 / 340 | 1 | 铝银 + 红标 | 一眼是易拉罐；平顶、凹盖、红标，罐身 16 面棱 |
| `juice_carton` | 果汁盒 | 0.06 × 0.06 × 0.10 | 0.0600 × 0.0600 × 0.1000 | 36 / 200 | 1 | 卡纸 + 橙色面板 | 一眼是屋脊顶果汁/牛奶盒；顶部有封口鳍 |
| `glass_bottle` | 玻璃瓶 | 0.07 × 0.07 × 0.28 | 0.0700 × 0.0700 × 0.2800 | 384 / 400 | 1 | 深绿玻璃 | 一眼是深绿酒瓶/油瓶；颈细、口沿可见，颜色偏深 |
| `plate` | 盘子 | 0.24 × 0.24 × 0.025 | 0.2400 × 0.2400 × 0.0250 | 384 / 400 | 1 | 陶瓷米白 | 一眼是薄盘；24 段圆边，盘心凹 |
| `bowl` | 碗 | 0.16 × 0.16 × 0.06 | 0.1600 × 0.1600 × 0.0600 | 384 / 400 | 1 | 蓝灰搪瓷 | 一眼是碗；口沿略厚，16 面棱可见 |
| `thermos` | 保温壶 | 0.12 × 0.12 × 0.30 | 0.1212 × 0.1058 × 0.3000 | 374 / 400 | 1 | 红身 + 米色杯盖 | 一眼是保温壶；红身+杯盖，提手缝隙在这个角度不明显 |
| `croissant` | 牛角包 | 0.12 × 0.07 × 0.06 | 0.0688 × 0.1200 × 0.0626 | 378 / 400 | 1 | 金棕 crust | 俯视是月牙；3/4 视角偏团块，像烤鸡/红薯，最弱的一件 |

尺寸说明：

- `cookie` 与 `orange` 重做后保持旧 `manifest.json` 的三轴尺寸、AABB 中心枢轴和文件名：`cookie` 的 0.070000 × 0.068856 × 0.009372 与旧值在 1e-9 m 内一致；`orange` 的 0.080000 × 0.080000 × 0.077417 与旧值只差 1.2e-7 m（浮点舍入）。两者仍分别是 1 个闭合壳。
- `soda_can`：CSV 的 `real_longest_dim_m = 0.07` 与 `PROP_SET_PLAN.md` 的“疑为罐径”一致；设计三轴 0.066 × 0.066 × 0.12 来自 `PROP_CATALOG.md`。`build_all` 因此会打一条预期内的 WARN（0.12 ≠ 0.07），不是尺寸错误。
- `thermos`：壶身是 Ø0.106 m 的圆筒，提手只向 +X 伸出，所以 X=0.1212 m（含提手）、Y=0.1058 m（壶身）；设计 0.12 × 0.12 是含提手的方形包络，Y 方向没有提手，不能强行撑到 0.12。
- `croissant`：月牙的开口朝 +X，长边放在 Y，所以实测 X=0.0688 m 是进深、Y=0.1200 m 是长度；三轴数值仍是设计里的 0.12 / 0.07 / 0.06 量级。这是为了在固定预览相机下能看见月牙开口和两个角；如果改成开口朝 +Y，3/4 视角会只看见凸背，更像团块。

拓扑/材质：10 件全部 1 个闭合壳，0 非流形边、0 零面积面、0 n-gon；纯色 Principled Base Color + Roughness，无贴图、无 UV、无顶点色。`cookie` 是平直面片 + 5 个平滑小圆顶（布尔并集），`orange` 是平滑 lathe，`cup`/`soda_can` 平直着色以保住口沿，`glass_bottle`/`plate`/`bowl`/`thermos`/`croissant` 平滑着色。

### 10.1 第二批复现

脚本在 `scripts/` 下；Windows staging 需要把 `Docs/Art/OriginalModels/props/scripts` 和 `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` 按仓库相对路径镜像到 `_stage/Docs/...`，因为 `build_all.py` 按 `prop_lib.ROOT` 找 CSV。构建 + 回读验证（cmd.exe 里 `--props` 的逗号列表必须整体加引号，否则只取到第一件）：

```
set BLENDER=D:\SteamLibrary\steamapps\common\Blender\blender.exe
set ROOT=D:\VRChatKatamariPreview\PropModels\tableware-v1
set STAGE=%ROOT%\_stage\Docs\Art\OriginalModels\props\scripts

"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python "%STAGE%\build_all.py" -- --out "%ROOT%" --props "cookie,orange,cup,soda_can,juice_carton,glass_bottle,plate,bowl,thermos,croissant"

"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python "%STAGE%\verify_import.py" -- --out "%ROOT%" --props "cookie,orange,cup,soda_can,juice_carton,glass_bottle,plate,bowl,thermos,croissant"
```

产物：`exports/<id>.fbx|.glb|.blend|.manifest.json`、`previews/<id>_preview.png`、`previews/contact_sheet.png`、根 `manifest.json`、`verification.json`。最终一次构建是 **10 件 / 3226 tris / 57 个文件**。

### 10.2 `prop_lib.py` 的第二批改动

1. 新增 `prism(points, length, axis='z', start=0.0)`：把一组二维点沿指定轴挤出成闭合棱柱（两个环 + 侧面四边形 + 两端 n-gon，`recalc_face_normals`，`finish()` 里再三角化端面）。果汁盒的“房子 + 屋脊 + 封口鳍”十点截面就是用它挤出的；之前 `sheet`/`tube`/`lathe` 都做不了这种带尖顶的直棱柱。
2. 预览相机的 `ortho_scale` 从 `span * 1.30` 放宽到 `span * 1.45`：第一版牛角包在 1.30 下右侧被画幅裁掉，放宽后四边都有余量，代价是所有预览里的模型略小一点。

### 10.3 这批的已知偏差

1. `baguette` 被 `croissant` 替代：CSV 没有 baguette 行/尺寸；`bread_slice` 的 note 明确记录整条面包被砍掉。没有为了凑数编 0.65 m。`croissant` 的 0.12 × 0.07 × 0.06 有目录来源。
2. `cookie`：旧版四个凹坑在 3/4 视角像切了四块的派；新版去掉凹坑，改成 5 个凸起小圆顶 + 保留原来的六瓣轻微波浪边。尺寸/枢轴/文件名不变，但三角面从 192 增至 326（预算 340）。
3. `orange`：旧版八条经线凹槽在平直着色下像南瓜的纬度环；新版去掉凹槽、改平滑着色、加小绿梗，保持微扁球（0.08 × 0.08 × 0.0774）。梗和果体是同一个 lathe 的连续轮廓，不是第二个物体。
4. `croissant` 仍是 10 件里最弱的一件：3/4 视角的近距离角被肥大的中段挡住，看起来像烤鸡/红薯；俯视能清楚看到月牙。要彻底解决得改预览相机或把月牙做成竖立的拱形，前者影响整批取景，后者不符合“平放桌上”的真实姿态，故保留现状并如实标注。
5. `soda_can` 没有做拉环：预算 320/340，再加一个拉环会超；平顶 + 凹盖 + 红标已经能读成易拉罐，但它也可能被读成小铁罐。`thermos` 的提手缝隙在这个固定相机角度被提手本身挡住，侧视/俯视才明显。`glass_bottle` 的深绿在暗背景下偏黑。`cup` 为了消掉内壁的平滑法线锯齿改成平直着色，外壁能看见 20 条竖棱。
6. 未做：UV、贴图、LOD、碰撞体、动画、Unity 导入/材质重建。FBX 不保存 Principled base colour，Unity 侧仍要按 `manifest.json` 的 `material_base_colors` 重建，或改用 GLB（内含 baseColorFactor）。

## 11. 第四批：第一关剩下的食物/餐具 + `thermos` 重做（2026-11-15）

这一批把 `PROP_CATALOG.md` 的 StoryMode 里**还没有真实模型**的道具补完，并重做读错的 `thermos`。

### 11.1 本批道具

| 道具 | 规格尺寸（设计） | 实测 X×Y×Z (m) | 面数/预算 | 壳 | AABB 中心 | 来源 |
| --- | --- | --- | --- | --- | --- | --- |
| `bread_slice` | 0.10 × 0.10 × 0.015 | 0.0996 × 0.1000 × 0.0143 | 160/200 | 1 | 0,0,0 | `PROP_CATALOG.md` 三轴 |
| `cheese_slice` | 0.06 × 0.05 × 0.008 | 0.0600 × 0.0500 × 0.0080 | 142/200 | 1 | 0,0,0 | `PROP_CATALOG.md` 三轴 |
| `fallen_leaf` | 0.09 × 0.06 × 0.001 | 0.0600 × 0.0900 × 0.0011 | 192/200 | 1 | 0,0,0 | `PROP_CATALOG.md` 三轴 |
| `sandwich` | 0.12 × 0.09 × 0.04 | 0.1200 × 0.0900 × 0.0400 | 140/400 | 1 | 0,0,0 | `PROP_CATALOG.md` 三轴 |
| `spoon` | 0.18 长，头 3.5 × 5 cm，柄宽 1.2 | 0.0350 × 0.1800 × 0.0150 | 282/300 | 1 | 0,0,0 | `PROP_SET_PLAN.md` §3.4 + CSV `real_longest_dim_m` |
| `small_potted_plant` | Ø0.14 × 高 0.22（盆 Ø14×12） | 0.1399 × 0.1364 × 0.2200 | 489/500 | 1 | 0,0,0 | `PROP_CATALOG.md` 三轴 |
| `thermos`（重做） | 0.12 × 0.12 × 0.30 | 0.1087 × 0.1053 × 0.3000 | 388/400 | 1 | 0,0,0 | 同第二批 |

合计 **7 件 / 1793 tris / 38 个文件**，`verify_import.py` 的 14 次回读（7 件 × FBX/GLB）全部 `ok`，`VERIFY_ALL_OK`。

StoryMode 里出现过、但 `exports/` 里还没有模型的道具只有 6 个 def（`bread_slice` 5 处、`sandwich` 4、`spoon` 4、`small_potted_plant` 2、`fallen_leaf` 1、`cheese_slice` 1，共 17 个实例），所以这一批是 6 件新道具 + 1 件重做，凑不到 8 件——没有为了数量编造 def 或尺寸。

### 11.2 `thermos` 重做

旧版是红身 + 米色盖，预览里读成番茄酱瓶。三处都改了，文件名/枢轴/高度不变：

1. **杯盖改成倒扣杯**：`z=0.230` 处从 Ø0.080 的瓶颈外扩到 Ø0.108 的法兰，直壁收到 `z=0.300` 的平顶；杯比 Ø0.096 的壶身宽 12 mm，整圈都能看见台阶，平顶才像“一个杯子扣在上面”而不是“一个圆盖”。
2. **竖向提手**：19 × 7 mm 的扁带（椭圆截面扫掠，不是圆钢筋），直段 72 mm，离壶身 14.5 mm，**炭黑色**（第四种材质）跟不锈钢灰分开。提手放在偏离 +X 40° 的方向：正对 +X 时相机正对着提手圈口，圈口被后面的壶身填满，只能看见一条深色竖条（前两版都是这个毛病）；偏 40° 后圈面接近平行画面，14.5 mm 的指缝变成背景，缺口一眼可见。代价是 AABB 从第二批的 0.1212 × 0.1058 m 变成 0.1087 × 0.1053 m（壶身、杯径、0.30 m 高度都没变，仍在 0.12 m 的包络内）。
3. **双色**：壶身不锈钢灰 `0.340,0.360,0.385` + 腰带深青 `0.020,0.115,0.105`（`z=0.1305..0.1795`）。

### 11.3 `prop_lib.py` 的第四批改动

1. 新增 `Builder.boolean_difference(target, tools)`：和 `boolean_union` 共用 `_apply_boolean`（Exact solver、逐件 apply、删掉工具对象），奶酪的三个圆形凹坑用它挖出来。之前只有并集。
2. 没有新增其它图元：`lathe` 做盆/杯/勺头，`tube` 做提手/叶/柄，`prism` 做奶酪楔和三明治棱柱，`disc_grid` 做面包片和落叶，都够用。

### 11.4 这批的已知偏差

1. `bread_slice`：0.0996 × 0.1000 × 0.0143。`finish()` 按最长轴归一化，法线向外的平滑外缘让原始 Y 比 X 长一点点，归一化后 X 少 0.4 mm、Z 少 0.7 mm（1.5 cm 的 4.7%）。要严格 0.10/0.10/0.015 就得放弃面包边的轻微起伏。
2. `fallen_leaf`：Z = 1.1 mm（设计 1 mm）。叶片有 3 段上翘，翘起量叠加在 0.9–1.1 mm 的厚度上；压到 1 mm 会丢掉弯曲。
3. `small_potted_plant`：0.1399 × 0.1364。盆是 14 边形旋转体，外接圆直径 0.14，对边距 0.1364；16 边形会多 28 个三角形、把 489/500 顶到 517，所以选 14 边并如实记录。
4. `sandwich` 一开始量出来是 0.120 × 0.0922 × 0.0434：三角形三个角做了 12 mm 倒角，实际轮廓只有 0.1107 × 0.085 m，`finish()` 于是整体放大 8.45%，高度变成 4.34 cm。修正办法是把三角形顶点放到倒角剪刀口外面（`BASE_HALF=0.0647`、`APEX_Y=-0.050`），倒角后的轮廓正好 0.1200 × 0.0900，归一化系数 0.99981。这类“倒角/圆角吃掉轮廓、被归一化放大”的问题在别的道具上也值得检查一遍。
5. `spoon`：三个轴里只有长度 0.18 m 有 CSV 依据（`real_longest_dim_m`），X 0.035、Z 0.015 来自 `PROP_SET_PLAN.md` §3.4 的“头部 3.5×5 cm 浅凹”和柄宽 1.2 cm，不是测量值。
6. `sandwich` 仍然可能被读成“夹心蛋糕切块”：三角楔 + 分层是它的真实形态，没有贴图时无法再给出更多信息。这一批做了三件事把它推向三明治——侧面是“棕边 / 米白面包瓤 / 火腿 / 奶酪 / 米白面包瓤 / 棕边”的夹层剖面、顶面是米白瓤 + 一圈细棕边（跟 `bread_slice` 同色）、面包色与 `bread_slice` 完全一致。
7. 未做：UV、贴图、LOD、碰撞体、动画、Unity 导入/材质重建（同前几批）。

## 12. 第五批：野餐一角结构件（按 `LEVEL_CONSTRUCTION.md` §3.2 / §4 重选）

`Docs/Research/KatamariMaps/LEVEL_CONSTRUCTION.md` 的 11 件结构型可吸物入库后，本批按那份清单重选 6 件：**`wood_plank` / `serving_tray` / `folding_table`（三件"薄、长、有平面"的斜搭与台面主力）+ `picnic_basket` / `folding_stool` / `cooler_box`（原有三件，尺寸改为该文档的 AABB）**。第一批里已做完的 `picnic_blanket` / `flower_pot_large` / `collapsed_bench` 仍留在 `models/`（各自有 CSV 行），但不属于这份骨架清单，本节不表。

### 12.1 尺寸、面数、可吸球径

| 道具 | 真实尺寸（来源） | 实测 X×Y×Z | 面数/预算 | 壳 | AABB 中心 | D（m） | 场景角色 |
|---|---|---|---|---|---|---|---|
| `wood_plank` | 1.40×0.35×0.025（§4 P0；无 CSV 行） | 1.400×0.350×0.025 | 44/250 | 1 | 0,0,0 | 0.616 | 薄长坡道（今天 2.5 cm 薄墙） |
| `serving_tray` | 0.55×0.35×0.04（§4 P2） | 0.550×0.350×0.040 | 94/250 | 1 | 0,0,0 | 0.528 | 斜搭板 / 浅盘 |
| `folding_table` | 1.00×0.60×0.70（§4 P0） | 1.000×0.600×0.700 | 236/900 | 1 | 0,0,0 | 2.002 | 0.70 m 平顶台面（"台球桌"） |
| `picnic_basket` | 0.45×0.30×0.22（§3.2 / `PICNIC_CORNER` §3.5.2） | 0.450×0.300×0.2213 | 552/800 | 1 | 0,0,0 | 0.830 | 门件 / 容器 |
| `folding_stool` | 0.40×0.35×0.32（§3.2 / §4 P1） | 0.400×0.350×0.320 | 556/700 | 1 | 0,0,0 | 0.950 | 0.32 m 挡路椅 / 台阶 |
| `cooler_box` | 0.55×0.35×0.30（§3.2 / §4 P1） | 0.550×0.350×0.300 | 124/800 | 1 | 0,0,0 | 1.033 | 门件 + 唯一可当台面的矮箱 |

`D = (60·a·b·c/π)^(1/3)`；D ≤ 2.20 m，六件都进第一关可吸梯子（最晚 2.00 m 的折叠桌），没有 `D > 2.20` 的纯布景件。FBX + GLB 回读 12 次全 `ok`，`VERIFY_ALL_OK`。

### 12.2 建模取舍（读图后改过的地方）

1. **`wood_plank` 尺寸源冲突**：同一文档 §3.2 写 1.20×0.25×0.02（D 0.486），§4 P0 写 1.40×0.35×0.025（D 0.616）。用 §4（带 id、面数预算的落地表）；交付里注明这一档从 0.49 变成 0.62。
2. **`serving_tray` 是"有边的板"**：0.55×0.35×0.04 的外壳挖掉 0.50×0.30、深 28 mm 的内腔，留 12 mm 底板 + 25 mm 边。边顶（z=0.040）和底板（z=0.012）都是平面；没有边就只是第二块木板。
3. **`folding_table` 平顶**：`PROP_SET_PLAN.md` 原话"桌面微拱"被否掉——本批合同是台面必须平（桌上一簇小物），顶面 z=0.700 整块平面。X 形交叉腿试过一版：4 根斜撑里 2 根并进腿、2 根仍单独成壳（3 shells），于是改成 4 条 40 mm 方腿（1 shell，236 tris）。"折叠"的机构细节在这个尺寸读不出来，平顶和 0.70 m 高度是它在这张图里的全部作用。
4. **篮子压到 0.22 m**：body 137.5 mm + 提手拱 83 mm，实测 0.2213 m（提手 6 边形管外缘多 1.3 mm，允许）。提手加粗到 Ø20 mm、颜色提亮，第一版 Ø16 mm 读成铁丝。
5. **冷藏箱 0.35 深 × 0.30 高**：握槽从长边改到两个短端（docstring 一直写的是"short sides"，第一版代码写错到 ±Y 长边），深 25 mm，槽内给一块深灰"橡胶"材质——10–20 mm 的同色浅槽在预览里完全看不见。
6. **凳子改成 0.40×0.35×0.32**：座面 0.35×0.25 不变，脚口外撇到 ±0.1685（+管半径 = 0.175），座面顶 0.32；腿间净空 0.308 m，小球能钻过去，符合"有腿有缝的开口凳"。

### 12.3 `build_all.py` 的一处改动

`LEVEL_CONSTRUCTION.md` 的 `wood_plank` / `serving_tray` / `folding_table` 在 `prop_catalog.csv` 里没有行。`build_one()` 现在允许模型模块自带 `DOC_ROW`（id / name_zh / category / real_longest_dim_m / game_longest_dim_m）来补这一行；CSV 有行的道具仍以 CSV 为准，CSV 没有又没 `DOC_ROW` 的 id 仍硬失败。**没有改 CSV**。

### 12.4 第一批 8 件的真实尺寸重标（同一批顺手修）

第一批的 `seed/acorn/pebble/bottle_cap/paper_scrap/toy_ball/napkin/watering_can` 当初按 `game_longest_dim_m` 建的（`SIZE_REFERENCE` 未设 = `'game'`），违反"真实尺寸"约定。现在全部改成 `SIZE_REFERENCE='real'`、`TARGET_LONGEST_M = CSV real_longest_dim_m`，**只改尺寸约定、不改形状**（`finish()` 均匀缩放，文件名/枢轴规则不变），重新出到 `legacy-real/`：

| 道具 | 旧（game） | 新（real，实测） | 缩放 |
|---|---|---|---|
| `seed` | 0.0224×0.0400×0.0160 | 0.0112×0.0200×0.0080 | ×0.50 |
| `acorn` | 0.0480×0.0456×0.0600 | 0.0240×0.0228×0.0300 | ×0.50 |
| `pebble` | 0.0800×0.0586×0.0426 | 0.0400×0.0293×0.0213 | ×0.50 |
| `bottle_cap` | 0.0600×0.0570×0.0174 | 0.0300×0.0285×0.0087 | ×0.50 |
| `paper_scrap` | 0.1000×0.0741×0.0118 | 同左（CSV 真实值本来就是 0.10） | ×1.00 |
| `toy_ball` | 0.3000×0.3000×0.3000 | 0.1500×0.1500×0.1500 | ×0.50 |
| `napkin` | 0.3000×0.3000×0.0280 | 0.1500×0.1500×0.0140 | ×0.50 |
| `watering_can` | 0.2603×0.5800×0.4155 | 0.1122×0.2500×0.1791 | ×0.4310 |

旧列 = 实测新值 ÷ 缩放系数（均匀缩放，形状逐点不变）；`napkin` 的 15×15×1.5 cm 设计厚度在游戏尺度下是 1.5 cm，真实缩放后是 1.4 cm，与 `PROP_SET_PLAN` 的 1.5 cm 差 1 mm，属于同一比例约定带来的结果。8 件 FBX+GLB 回读全 `ok`，`VERIFY_ALL_OK`。

### 12.5 复现

```bat
set S=D:\VRChatKatamariPreview\PropModels\batch5-v1\_stage\Docs\Art\OriginalModels\props\scripts
set B=D:\SteamLibrary\steamapps\common\Blender\blender.exe
"%B%" --background --factory-startup --disable-autoexec --python-exit-code 2 --python %S%\build_all.py -- --out D:\VRChatKatamariPreview\PropModels\batch5-v1 --props "wood_plank,serving_tray,picnic_basket,folding_stool,cooler_box,folding_table"
"%B%" --background --factory-startup --disable-autoexec --python-exit-code 2 --python %S%\verify_import.py -- --out D:\VRChatKatamariPreview\PropModels\batch5-v1 --props "wood_plank,serving_tray,picnic_basket,folding_stool,cooler_box,folding_table"
"%B%" --background --factory-startup --disable-autoexec --python-exit-code 2 --python %S%\build_all.py -- --out D:\VRChatKatamariPreview\PropModels\batch5-v1\legacy-real --props "seed,acorn,pebble,bottle_cap,paper_scrap,toy_ball,napkin,watering_can"
"%B%" --background --factory-startup --disable-autoexec --python-exit-code 2 --python %S%\verify_import.py -- --out D:\VRChatKatamariPreview\PropModels\batch5-v1\legacy-real --props "seed,acorn,pebble,bottle_cap,paper_scrap,toy_ball,napkin,watering_can"
```

### 12.6 未做与边界

- 本批 6 件**没有碰撞体**；建议的分箱见交付正文（`wood_plank` 1 box、`serving_tray` 1 box、`folding_table` 桌面 1 + 4 腿各 1、`picnic_basket` 1、`folding_stool` 座面 1 + 两框各 1、`cooler_box` 1）。
- 骨架清单里本批未做的：`crate` / `book_stack` / `beer_crate` / `lounge_chair` / `stroller` / `picnic_table`（按 6 件配额截断，主次按 Lead 的三件"薄长平面"优先）。
- 当前 motor 只有一个解析地面、没有竖直支撑，所以这 6 件**实际上都不能爬**；平顶与 2.5 cm 板是给将来的坡面/支撑补丁留的形状，今天它们只是"挡路 ↔ 可吸"两态。

## 13. 第六批：第一关剩下的 9 件可吸结构件（2026-11-15）

`Docs/Design/StoryMode/PICNIC_CORNER_LAYOUT.md` §4.1 的 17 件可吸结构件用的是 13 个 def。逐个对着本目录 `scripts/models/` + `exports/*.manifest.json` 核对后，**9 个 def 没有模型**：

- 第五批按配额截断、没做的 5 件：`crate` / `book_stack` / `beer_crate` / `lounge_chair` / `stroller`（见 §12.6）；
- 布局文档 U3 为了填 1.10–1.60 空档新加的 4 件：`storage_bin` / `cooler_box_large` / `crate_large` / `picnic_bench`。

`serving_tray` / `wood_plank` / `folding_table` / `folding_stool` 已有模型，未重做。110 件小道具的 31 个 def 全部已有模型（本批只补结构件）。
`picnic_table`（`LEVEL_CONSTRUCTION.md` §4 P0，D_req 2.492 > 2.20）**不在第一关布局里**，`PROP_SET_PLAN` §2.8.5 已把它划给 PicnicLawn，本批**没有建**，不是漏项。

### 13.1 尺寸、面数、可吸球径

布局文档的 `size` 是 `[x, 高度, z]`；下表"实测"列是 Blender 的 X×Y×Z（Y 是深度、Z 是高度），与布局的三轴一一对应（x → X，高度 → Z，z → Y）。FBX/GLB 按 README §2 的约定导出（Y-up、`bake_space_transform`），Unity 端预期 identity。

| 道具 | 中文 | 设计（布局 §4.1，x×高×z） | 实测 X×Y×Z (m) | 面数/预算 | 壳 | `D_req` | 场景角色 |
|---|---|---|---|---|---:|---:|---|
| `book_stack` | 书堆 | 0.26 × 0.09 × 0.19 | 0.260 × 0.190 × 0.090 | 160/300 | 1 | 0.440 | 第一级台阶（0.36 可踩 → 0.44 可吃） |
| `beer_crate` | 啤酒箱 | 0.40 × 0.28 × 0.30 | 0.400 × 0.300 × 0.280 | 182/400 | 1 | 0.863 | 台面南沿的 2 cm 踏脚 |
| `crate` | 木箱 | 0.60 × 0.40 × 0.40 | 0.600 × 0.400 × 0.400 | 272/600 | 1 | 1.224 | 台面 → 箱顶的 0.14 一级 / 树角挡路 |
| `storage_bin` | 收纳箱小 | 0.55 × 0.40 × 0.35 | 0.550 × 0.350 × 0.400 | 310/600 | 1 | 1.137 | 桌与花圃之间的矮柜 |
| `crate_large` | 大收纳箱 | 0.80 × 0.45 × 0.50 | 0.800 × 0.500 × 0.450 | 308/700 | 1 | 1.509 | 树角的挡路地标 |
| `cooler_box_large` | 大冷藏箱 | 0.70 × 0.40 × 0.45 | 0.700 × 0.450 × 0.400 | 204/600 | 1 | 1.340 | 中后期第一件"大"的 |
| `picnic_bench` | 野餐长凳 | 1.20 × 0.45 × 0.40 | 1.200 × 0.400 × 0.450 | 360/700 | 1 | 1.604 | 出生点旁第一个大地标 |
| `lounge_chair` | 折叠躺椅 | 0.60 × 0.35 × 1.60（布局 `yaw=90`，已是世界 AABB） | 1.600 × 0.600 × 0.350 | 354/900 | 1 | 1.858 | 唯一"踩上去再吃掉"的件（台顶 → 躺椅 → 桌面） |
| `stroller` | 婴儿车 | 1.00 × 1.00 × 0.55 | 1.000 × 0.550 × 1.000 | 888/900 | 1 | 2.190 | 尾口 / 最高的地标 |

合计 **9 件 / 3038 tris**。三轴与布局文档的差都 < 1e-6 m；`D = (60·a·b·c/π)^(1/3)`，9 件全部 ≤ 2.20，没有纯布景件。
面数预算取 `LEVEL_CONSTRUCTION.md` §4 的 P0/P1/P2 表（`crate` 600、`lounge_chair` 900、`stroller` 900、`book_stack` 300、`beer_crate` 400）；U3 的 4 个新 def 该文档没有给预算，按同尺寸档沿用 **600 / 600 / 700 / 700**。

### 13.2 建模取舍（读图后改过的地方）

1. **`lounge_chair` 没有靠背**。布局把它当 0.35 m 的地形用（台顶 0.26 → 躺椅 0.35 → 桌面 0.70），所以躺面是**一整块 z = 0.350 的平面**，靠背/扶手/枕头一个都不能高过它。它读成躺椅靠的是：1.60 × 0.60 的赭色布面 + 两根 28 mm 边管 + 四条腿 + 两道横撑；头端 250 mm 是一块**与自己共面**的 1 mm 米色板（折叠毛巾），不是凸起。
2. **`stroller` 的行驶方向在 +X，不在 +Y**。布局的 `size` 是 `[x, 高度, z]` 且 `yaw = 0`，所以车宽必须落在 X（1.00）、进深落在 Z（0.55）。模型就是按这个朝向建的（车架在 -X 端），AABB 才能同时对上 D_req 与挡路盒。这是**唯一**一件正面不是 +Y 的商品；已在 `NOTES` 里写明。
3. **`beer_crate` 是开口瓶箱，不是第二个木箱**。16 mm 壁 + 20 mm 底 + 六格（2 排 × 3 列、8 mm 隔板，隔板比口沿低 20 mm）+ 两端手指孔；0.28 m 的口沿是平的，但格子里是空的——球踩的是 AABB 顶面，和 `folding_stool` 的凳面同一种处理。
4. **`storage_bin` 与 `crate` 必须一眼分开**：两者尺寸接近（0.55×0.40×0.35 vs 0.60×0.40×0.40），所以前者做成塑料收纳箱——浅灰直壁 + 两道模压横筋 + 深蓝盖（5 mm 出檐、8 mm 台阶）+ 正面米色标签牌；后者是木箱（角柱 + 压条 + 盖板）。
5. **`crate` / `crate_large` 的顶面是一个平面**：盖板顶与四根角柱顶共面（z = 0.400 / 0.450），所以"叠两件"和挡路高度都对得上；`crate_large` 只是把柱加粗到 55 mm、箱体缩进 22.5 mm，并给两端各加一根压条。
6. **`cooler_box_large` 的盖上有提手槽**（180 × 55 × 12 mm 凹槽，深色内衬）：0.55 m 的小保温箱当初否掉了顶提手（它的顶是桌面），这一件 `D_req` 1.34、`4×h` = 1.60，永远不当支撑面，所以可以按真实冷藏箱做。AABB 顶仍是 0.400 的盖沿平面。
7. **`book_stack` 的书脊是"面"不是"件"**：每本书 = 4 mm 内缩的书页块 + 5 mm 全轮廓封面；书页块在 +X 侧与封面齐平，那个立面直接刷封面色 = 书脊。第一版给书脊单独做了一个 8 mm 棱柱，它的方角穿过封面的圆角，并集后留下 3 条边界边（`non_manifold_edges: 3`），改成"颜色带"后是 0。
8. **一次一个壳体**：9 件全部 `closed_shells: 1`、`non_manifold_edges: 0`、`zero_area_faces: 0`、`ngons: 0`、`duplicate_faces: 0`、`loose_vertices: 0`。过程中踩到并修掉的坑（都写进了各自脚本注释）：腿部管件的**平头端**必须插进上方面板 10 mm（`lounge_chair` 第一版腿顶正好停在板底，导出 3 个壳）；`picnic_bench` 的单一中撑**碰不到两侧腿**（导出 2 个壳），改成两条与腿同排的撑；`stroller` 的底篮开口会被上方的睡篮封住、内表面变成封闭空腔（多一个壳），所以底篮改成**实心深色块**。
9. **相机的"看不见"也算偏差**：`stroller` 的推杆第一版是一根扫掠管，肩部的环被弯折带斜，管顶只到 0.9926 m；改成两根立柱 + 一根 **8 边形圆柱横杆**（90° 顶点朝上）后正好 1.000 m。轮子同样是 12 边形：10 边形在 0° 起头时没有正下方的顶点，轮胎会离地 4.4 mm、整体高度掉到 0.995。

### 13.3 复现

本批在 Linux 无头 Blender **4.0.2** 上构建与回读（`source ~/tools/blender-env.sh`），命令与 §5 / §12.5 相同，只是把输出指到仓库外：

```bash
PROPS="book_stack,beer_crate,crate,storage_bin,crate_large,cooler_box_large,picnic_bench,lounge_chair,stroller"
"$BLENDER" $BLENDER_FLAGS --python Docs/Art/OriginalModels/props/scripts/build_all.py  -- --out /tmp/props_b6_stage --props "$PROPS"
"$BLENDER" $BLENDER_FLAGS --python Docs/Art/OriginalModels/props/scripts/verify_import.py -- --out /tmp/props_b6_stage --props "$PROPS"
```

产物：`exports/<id>.fbx|.glb|.blend|.manifest.json`、`previews/<id>_preview.png`（本批已复制回仓库），外加 `previews/contact_sheet_batch6.png`（9 件拼图）。回读 **18 次全 `ok`（`VERIFY_ALL_OK`）**：FBX 与 GLB 的三角面数与源网格逐一相同，世界尺寸与 manifest 差 < 5e-4 m，包围盒中心在原点。

### 13.4 未做与边界

- **没有重新生成** `manifest.json`（总清单）与 `SHA256SUMS`：那两份是整个 `props/` 的产物，本批只追加了 9 个 def 的四件套。要合并时跑一次全量 `build_all.py`（不带 `--props`）即可；`sign.py` 也能只重算指纹。
- **没有在 Unity 里导入验证**（本 agent 不是 Unity 操作员）：材质仍需按 `exports/<id>.manifest.json` 的 `material_base_colors` 重建，或改用 GLB。
- 预览图的像素来自 **Blender 4.0.2 的 EEVEE**，和本目录里 3.6.21 / 5.3.0 生成的旧预览不是同一渲染器；几何、清单与拼图布局不受影响。
- 9 件**没有碰撞体**。建议分箱：`book_stack` 1、`beer_crate` 1、`crate` 1、`storage_bin` 1、`crate_large` 1、`cooler_box_large` 1、`picnic_bench` 座面 1 + 两条腿框各 1、`lounge_chair` 躺面 1 + 四条腿各 1、`stroller` 车体 1 + 车架 1。
- 当前 motor 没有竖直支撑（同 §12.6），所以这批的平顶现在仍然只是"挡路 ↔ 可吸"两态的形状准备。
