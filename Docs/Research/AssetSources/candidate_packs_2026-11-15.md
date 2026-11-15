# 三个新主题的 CC0 候选包清单（2026-11-15）

状态：**页面级调研，仅读公开许可页与商店页，未下载任何素材包、未解压、未导入 Unity、未改变任何授权结论。** 本文不构成法律意见，也不代替逐文件验收；项目许可仍未决定。

范围：A) 屎壳郎草甸 DungBeetleMeadow、B) 一个人的乱房间 MessyRoom、C) 野餐草坪 PicnicLawn 的 0.03–0.35 m 小件缺口。已有结论见 [README](README.md)（§1 许可矩阵、§2 逐项核验、附录 A 入库记录）、[license_gaps](../../AssetIntake/license_gaps.md)。本文只回答"找哪个包、许可怎么核、哪些必须自己做"。

采集方式与限制（先读，避免误读本文）：

- 全程只读。**未使用浏览器工具**（浏览器只有 Lead 一个 owner），只用无头 HTTP 读取页面 + `web_fetch`。
- kenney.nl、quaternius.com、polyhaven.com/api、ambientcg.com、opengameart.org、itch.io 可直读正文。
- **poly.pizza 直连返回 HTTP 403（Cloudflare "Just a moment..."）**，改用 `poly-pizza.translate.goog` 翻译代理读取；代理页里原始许可链接仍指向 `creativecommons.org`。代理读取期间触发过一次 HTTP 429 限流，受影响条目已列入 §5。
- 引文一律 ≤20 词，附 URL；访问日期均为 2026-11-15。
- 本文所有"件数/格式/贴图"来自页面标注；**页面不标的字段写"页面未列"**，不凭印象补。

## 0. 五条结论

1. **Kenney 仍是 A/B/C 的第一底座**：三个主题的道具与家具大类都能覆盖，CC0、统一低多边形。但 Kenney 页面只标"Files 数量"，**不标三角面与包体积**，必须下载后统计（本文不下载）。
2. **Quaternius 现在是"逐包看"的典型**：站内 84 个包里 **82 个包页仍标 CC0**，只有 Bestiary Dungeon Monsters Kit 与 Characters Fantasy Races 标 QAL——恰好也是仅有的两个加了 glTF 的新包。旧包页 CC0 与站点 QAL v1.0 并存，**以包内 LICENSE 为准**。
3. **Poly Pizza 是"发现层"，不是许可靠山**：实测 CC0 条目几乎都是 Quaternius / Kenney 的镜像（作者字段直接写 Quaternius、Kenney），而 `Poly by Google` 与独立作者条目基本是 CC BY 3.0。要 CC0-only，优先回原包下载。
4. **Poly Haven 面数与写实度都偏高**，但少数家具低得意外（`Shelf_01` 仅 182 tri、`painted_wooden_cabinet_02` 966 tri），且 **API 直接给真实尺寸（mm）**，对 0.03–0.35 m 尺寸分层极有用。
5. **真正缺的是"软/皱/细"这一类**：脏衣物褶皱、粪球、纸团、充电线、弹珠、糖包、麦穗谷粒，没有任何 CC0 包覆盖，必须程序化建模（§4）。

## 1. 逐来源核验

### 1.1 Kenney（CC0）

许可页：<https://kenney.nl/support> — "all game assets on the asset pages are public domain licensed (CC0)"；"Attribution is not required."（2026-11-15 读取，HTTP 200）。每个 pack 页固定一行 `License | Creative Commons CC0`，链到 <https://creativecommons.org/publicdomain/zero/1.0/>。

| pack | URL | 件数（页面） | 许可 | 格式 | 贴图 | 三角面 | 包体积 | 已入库 |
|---|---|---|---|---|---|---|---|---|
| Food Kit | <https://kenney.nl/assets/food-kit> | 200 | CC0 | FBX/OBJ/GLB/MTL/PNG（据入库清单，页面未列） | 含 PNG | 页面未列 | 4.6 MB（入库清单） | **是** |
| Furniture Kit | <https://kenney.nl/assets/furniture-kit> | 140 | CC0 | FBX/OBJ/GLB/DAE/STL/MTL/PNG（入库清单） | 含 PNG | 页面未列 | 5.1 MB（入库清单） | **是** |
| Nature Kit | <https://kenney.nl/assets/nature-kit> | 330 | CC0 | FBX/OBJ/GLB/DAE/STL/MTL/PNG（入库清单） | 含 PNG | 页面未列 | 10.5 MB（入库清单） | **是** |
| Marble Kit | <https://kenney.nl/assets/marble-kit> | 160 | CC0 | FBX/OBJ/GLB/MTL/PNG（入库清单） | 含 PNG | 页面未列 | 5.3 MB（入库清单） | **是**（无球体，未映射任何 catalog id） |
| Survival Kit | <https://kenney.nl/assets/survival-kit> | 80（含 Animation） | CC0 | 页面未列 | 页面未列 | 页面未列 | 页面未列 | 否 |
| Toy Car Kit | <https://kenney.nl/assets/toy-car-kit> | 100 | CC0 | 页面未列 | 页面未列 | 页面未列 | 页面未列 | 否 |
| Holiday Kit | <https://kenney.nl/assets/holiday-kit> | 100（含 Animation） | CC0 | 页面未列 | 页面未列 | 页面未列 | 页面未列 | 否 |
| Mini Characters | <https://kenney.nl/assets/mini-characters> | 40（含 Animation） | CC0 | 页面未列 | 页面未列 | 页面未列 | 页面未列 | 否 |

判定：Kenney 的许可没有不确定性（页面 + 已入库的 zip 内 `License.txt` 双重一致，见 `intake_manifest.csv`），风险全在**风格与面数**，不在许可。Survival Kit / Toy Car Kit / Holiday Kit 三包对 A/B 有补位价值（木箱、栅栏、玩具、礼盒），但内容清单只能靠下载确认。

### 1.2 Quaternius（逐包！）

站点许可页：<https://quaternius.com/license.html> — "Quaternius Asset License (QAL) v1.0，Last updated: 8/28/2026"；摘要写 "personal, educational, and commercial games ... with no credit required"，限制是 "can't resell or redistribute the assets themselves as assets"（2026-11-15 读取，HTTP 200）。

**扫描结果**：把站点 84 个 `/packs/*.html` 全部读一遍，解析页面元数据块的 `License` 字段——**82 个标 CC0，2 个标 QAL**（`bestiarydungeonmonsterskit`、`charactersfantasyraces`；这两个同时也是仅有的两个 `Formats` 里带 glTF 的包）。候选包全部在 CC0 一侧：

| pack | URL | 件数 | 页面许可 | 格式 | 贴图 | 已入库 |
|---|---|---|---|---|---|---|
| Ultimate Crops | <https://quaternius.com/packs/ultimatecrops.html> | 102 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Ultimate House Interior | <https://quaternius.com/packs/ultimatehomeinterior.html> | 123 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Ultimate Furniture | <https://quaternius.com/packs/ultimatefurniture.html> | 20 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Furniture | <https://quaternius.com/packs/furniture.html> | 23 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Farm Buildings | <https://quaternius.com/packs/farmbuildings.html> | 13 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Farm Animals | <https://quaternius.com/packs/farmanimal.html> | 7 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Survival | <https://quaternius.com/packs/survival.html> | 53 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Ultimate Nature | <https://quaternius.com/packs/ultimatenature.html> | 150 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Ultimate Food | <https://quaternius.com/packs/ultimatefood.html> | 103 | CC0 | FBX/OBJ/Blend | 无 | 否（README 附录 A 记录未取，入口是 Drive 目录） |
| Buildings | <https://quaternius.com/packs/buildings.html> | 9 | CC0 | FBX/OBJ/Blend | 无 | 否 |
| Junk Food | <https://quaternius.com/packs/junkfood.html> | 16 | CC0 | FBX/OBJ/Blend | **有** | 否 |
| Medieval Village | <https://quaternius.com/packs/medievalvillage.html> | 44 | CC0 | FBX/OBJ/Blend | **有** | 否 |

判定：候选包页面许可清楚（CC0），但**同一站点的 QAL 仍然有效**，而且 QAL §7 说"新版许可不追溯旧包"、旧包按获取时的版本。因此每个包入库时必须保存包内 `LICENSE` 文本；只看网页标签不够（README §1 已给出同样结论，这里补了 84 包的全量扫描数据）。`Junk Food` 与 `Medieval Village` 标了 Textured，贴图风格与我们的纯色 Principled 不兼容，**降级为参考**。

### 1.3 Poly Haven（CC0，模型）

许可页：<https://polyhaven.com/license> — "Our assets are all licensed as CC0"，"All assets (HDRIs, textures and 3D models) on this site are the original work of Poly Haven staff, or artists who willingly and directly donate/sell their work"（2026-11-15 读取）。

数据取自官方 API `https://api.polyhaven.com/assets?t=models`（522 个模型，含 `polycount`、`dimensions`(mm)、`authors`），资产页 `https://polyhaven.com/a/<id>` 是 JS 渲染，**正文 UNVERIFIED**，下表字段全部取自 API：

| id | 三角面 | 尺寸 (mm) | 作者 | 对哪个主题有用 |
|---|---|---|---|---|
| `Shelf_01` | 182 | 1003×257×2080 | Gabriel Radić | B 书架（面数极低） |
| `painted_wooden_stool` | 676 | 385×406×579 | Kirill Sannikov | B 凳子 |
| `painted_wooden_bench` | 630 | 1165×497×889 | Kirill Sannikov | B/A 长凳 |
| `painted_wooden_cabinet_02` | 966 | 997×729×2569 | Kirill Sannikov | B 衣柜/立柜 |
| `CheeseBox_01` | 884 | 240×136×93 | Gabriel Radić | B/C 纸盒类容器 |
| `barrel_03` | 1473 | 634×639×931 | Serhii Khromov | A 木桶/饲料桶 |
| `carved_wooden_plate` | 2112 | 272×269×38 | Jan Martens | C 盘/杯垫替身 |
| `postcard_set_01` | 2720 | 677×0×545 | James Ray Cock / Jurita Burger | B/C 卡片 |
| `wall_clock` | 3658 | 321×47×321 | PierreB3D | B 挂钟 |
| `gamepad` | 9546 | 404×435×19 | Josh Dean | B 手柄 |
| `food_apple_01` | 7012 | 98×96×85 | Oliver Harries | C 水果 |
| `lambis_shell` | 12498 | 140×75×47 | Kuutti Siitonen | A 蜗牛壳参考（海螺形） |
| `wicker_basket_01` | 22276 | 383×295×117 | Kuutti Siitonen | B 洗衣篮（面数过高） |
| `vintage_day_bed` | 2715 | 1973×855×1127 | Aron Łyczek | B 床 |
| `shrub_03` | 16955 | 1367×151×403 | Rico Cilliers | A 灌木（树篱替代） |
| `rock_09` | 23280 | 74×145×33 | Jenelle van Heerden | A 石头（面数过高） |

判定：许可零风险，**风格风险高**（照片级 PBR 贴图，需重做材质）。只建议取 `Shelf_01`、`painted_wooden_*`、`CheeseBox_01`、`barrel_03` 这几个"面数低于 1.5k、结构简单"的家具，并把贴图换成纯色。

### 1.4 ambientCG（CC0，纹理）

许可页：<https://docs.ambientcg.com/license/> — "All ambientCG assets are provided under the Creative Commons CC0 1.0 Universal License."；"You can include the raw files in your project, for example a video game."；"You don't need to give credit"（2026-11-15 读取）。

候选（ID 经 `https://ambientcg.com/view?id=<ID>` 逐一确认页面存在且标 CC0）：

| 用途 | ID | URL | 1K-JPG 体积 |
|---|---|---|---|
| 晒硬的沙土小径 | `Ground054` | <https://ambientcg.com/view?id=Ground054> | 页面未列 |
| 沙土/碎石地面 | `Ground079S` | <https://ambientcg.com/view?id=Ground079S> | 页面未列 |
| 干燥土+砾石 | `Ground110` | <https://ambientcg.com/view?id=Ground110> | 9.9 MB |
| 草地 | `Grass005` | <https://ambientcg.com/view?id=Grass005> | 页面未列 |
| 草坪（PicnicLawn） | `Grass008` | <https://ambientcg.com/view?id=Grass008> | 页面未列 |
| 木地板（乱房间） | `WoodFloor051` | <https://ambientcg.com/view?id=WoodFloor051> | 4.9 MB |
| 瓷砖（乱房间） | `Tiles141` | <https://ambientcg.com/view?id=Tiles141> | 4.7 MB |
| 墙面抹灰 | `Plaster001` | <https://ambientcg.com/view?id=Plaster001> | 页面未列 |
| 石铺地 | `PavingStones152` | <https://ambientcg.com/view?id=PavingStones152> | 7.9 MB |
| 茅草/干草（干草垛参考） | `ThatchedRoof001A` | <https://ambientcg.com/view?id=ThatchedRoof001A> | 页面未列 |
| 布料/地毯（脏衣服参考） | `Fabric026` | <https://ambientcg.com/view?id=Fabric026> | 页面未列 |

判定：够用，但都是**写实 PBR**，进卡通世界要降分辨率（1024 甚至 512）、压对比、改色调；更适合做"地面的色彩变化"，不适合做主视觉。

### 1.5 Poly Pizza（逐模型，CC0 或 CC-BY 3.0）

机制：每个模型页有固定一行 `日期 • <格式> format • <许可名>` + 指向 `creativecommons.org` 的链接。实测两种：链接到 `licenses/by/3.0` 的页面文字是 "Creative Commons Attribution"；链接到 `publicdomain/zero/1.0` 的是 CC0（**UNVERIFIED**：该批页面 429 限流，文案未取到，只取到链接）。

**关键观察（对策略有用）**：CC0 条目的作者字段多为 `Quaternius` 或 `Kenney`——即同一批 FBX 的镜像；CC BY 3.0 条目多为 `Poly by Google` 与独立作者。下面 46 条均已打开 `https://poly.pizza/m/<id>` 核验，tri 取自页面内嵌字段 `Tris`；`/m/` 后即 id：

- **CC0**（可直接回原包取）：`e02PFKKhbr` Fence 188、`83obI9bNun` Bucket 360、`oNBQSf87ZJ` Shovel 190、`Yu8TOERkpw` Hay 488、`tdeAOh3LQV` Stone Wall 216（作者 Quaternius，下四条同）；`QHZtj94fvh` Coin 206、`22eU6FjNQ8` Matchbox 396、`icVsN3lmVy` Pebble Round 136（Quaternius）；`PSqXX0pj5o` Pizza Box 60、`xApCbtFYP8` Twig 64（Kenney）；`dS4MQQH5EM` Tissue Box 102、`uF1dGn3HXi` Chips 460（CreativeTrio）。
- **CC BY 3.0**（需世界内 credits，`Poly by Google` 未另注）：`6LeCqyw00RK` Haystack 414、`5-EGHK67BxT` Picket fence 1020、`aZ_cT-AIu2y` Snail 399、`dXBMV4AY2DL` Sheep 894、`bceVxKNzLaJ` Nautilus shell 396、`efbZhPRsTuD` Bell 862、`4qmc411D8Tx` Wild wheat 14544、`hcfzStK2HR` Socks 618、`bdOMzzh-fSl` T-shirt 2261、`2jVUdnj4mVP` Mug 680、`35DXCGSBkOo` Comb 1220、`0i-_Bj5EfH5` Bowl of cereal 7360、`2TKK0wpiWPw` AA battery 260、`4XJ-DH66eKY` Radiator 3600、`0K05lwk9caj` Dresser 1864、`BWgx6mJnAF` Large Wardrobe 1124、`15B0ihXkg3C` Bookshelf 2636、`9veo7sZrcBZ` Remote 692、`fCyA3Ug79X` Low Poly Controller 303、`f4-AUM_gO-R` LEGO Brick 364、`2wR1M99UwP` Slippers 560、`1WggoIFq8tx` Book Stack 640、`aO2ph4BRL5m` Sticky notes 72、`LKHJcD9Ocy` Phone charger 1804、`0m3VOUqFmTQ` Fork 616、`f0piCrI4HrZ` Chopsticks 856、`5R0mBgid5-k` Pencil 248、`abXQzBR4wRk` Round Coasters 724、`eNL1vYa4P6b` Trading Card 92、`aweIphqphRJ` Button 160、`dexMJlf51a8` Eraser 460、`3tyh15Fbmsx` Tuft of grass 280、`fnuWrFddqLb` Raspberry 1260、`6i0oxEqEUVk` Blueberries 4190。
- 作者不是 `Poly by Google` 的 CC BY 条目：Feather（Christopher F）、Socks（MiniPoly）、Dresser（CMHT Oculus）、Large Wardrobe（Zsky）、Bookshelf（Wanja Pflüger）、Low Poly Controller（Ginsta）、LEGO Brick（Nick Ladd）、Slippers（J-Toastie）、Book Stack（Danni Bittman）、Phone charger（moriarty）、Pencil / Button / Raspberry（jeremy）、Round Coasters / Blueberries（Jarlan Perez）、Trading Card（Vice Cooper）。

### 1.6 OpenGameArt（只取 CC0 子集）

按 `field_art_licenses_tid[]=4` + 3D 过滤搜索后逐条打开核验；页面固定写 `License(s): CC0`。全部为单件/小包，体积极小：

| 条目 | 作者 | 许可 | 内容/体积 | 主题 |
|---|---|---|---|---|
| `/content/rope-fence` | Clint Bellanger | CC0 | 低多边形绳栏，3 个整体 + 零件 | A |
| `/content/basic-wooden-fence` | WeaponGuy | CC0 | 基础木栅栏 | A |
| `/content/modular-fence-pack` | rubberduck | CC0 | 2 种栅栏，`.dae` | A |
| `/content/hay-bale` | carlosjorgereis | CC0 | `hay_bale.7z` 346 KB | A |
| `/content/lowpoly-animated-farm-animal-pack` | quaternius | CC0 | FBX/OBJ/Blend，含羊 | A |
| `/content/snail-3d-model` | TinyWorlds | CC0 | 照片扫描蜗牛壳，约 3k tri | A（风格不符） |
| `/content/lowpoly-survival-item-pack` | artie31 | CC0 | 篮、电池、桶、手电、玻璃瓶、陶瓷杯、壶、刀 | B/C |
| `/content/key-collection` | Renderwahn | CC0 | `key.blend` 2.2 MB，现代钥匙+钥匙圈 | B/C |
| `/content/low-poly-coin-3d` | iamsujitcu | CC0 | 380 tri | C |
| `/content/dice-low-poly` | STKRudy85 | CC0 | `Dice.7z` 66 KB | C |
| `/content/book` | yd | CC0 | `SimpleBook1.blend` 775 KB | B/C |
| `/content/mugglassplateknife` | pasi | CC0 | `.blend`，杯/玻璃杯/盘/刀 | C |
| `/content/tool-pack-1` | LonesomeDucky | CC0 | 3 件手工具，blend + GLB | A |
| `/content/grass-pack-01` | Yughues | CC0 | 草/植被大包 | A/C |
| `/content/fence-3d` | PagDev | CC0 | 36 顶点栅栏 | A |

判定：单件质量参差、命名混乱，但**体积小、许可干净、能立刻补"没包的那一件"**。适合作为 Poly Pizza 之后的第二补位源。

### 1.7 itch.io（只取明确 CC0）

平台无统一素材许可，逐页看正文：

- KayKit (Legacy) Mini Game Variety Pack — <https://kaylousberg.itch.io/kay-kit-mini-game-variety-pack> — 正文写 "Free for personal and commercial use, no attribution required. (CC0)"，页面标签 `CC0`；50+ 件低多边形卡通件（道具/武器/pickup）。**注意：该页是 legacy 页，作者已声明被新版本取代**，新版页面未逐页核验。
- Retro House Pack — <https://elegantcrow.itch.io/retro-house-pack> — 正文写 "The models on this pack are under the CC0 license"，但紧接着 "Textures come from AmbientCG.com"，另有 Pixabay / Pexels / Unsplash。**模型 CC0、贴图来源混合**，若使用必须换掉贴图或逐张追溯。

判定：itch.io 只在"作者明确写 CC0 且不含第三方贴图"时才用；KayKit 的风格契合度很高，但品类偏武器/pickup，与三主题只部分重合，故未进 §2 排行；值得 Lead 决定是否新开一页核验新版。

## 2. 按主题的候选排行

契合度＝与"纯色 Principled、无贴图、80–900 tri、卡通比例"的接近程度（高/中/低）。"已入库"对照 [README 附录 A](README.md) 与 [intake_manifest.csv](intake_manifest.csv)：只有 Kenney Food / Nature / Furniture / Marble 与 5 个 Poly Pizza 单模型算已入库，**其余一律"否"**。

### 2.1 主题 A：屎壳郎草甸（DungBeetleMeadow）

| # | 包/件 | URL | 许可 | 作者 | 件数 | 面数/风格 | 格式 | 体积 | 契合 | 覆盖类别 | 已入库 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| A1 | Kenney Nature Kit | <https://kenney.nl/assets/nature-kit> | CC0 | Kenney | 330 | 低多边形统一 | FBX/OBJ/GLB | 10.5 MB | 高 | 树、灌木、石头、地被、花 | **是** |
| A2 | Quaternius Ultimate Crops | <https://quaternius.com/packs/ultimatecrops.html> | CC0（页面） | Quaternius | 102 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 菜园、果园边缘、作物 | 否 |
| A3 | Quaternius Farm Buildings | <https://quaternius.com/packs/farmbuildings.html> | CC0（页面） | Quaternius | 13 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 农家院子、谷仓、棚 | 否 |
| A4 | Quaternius Farm Animals | <https://quaternius.com/packs/farmanimal.html> | CC0（页面） | Quaternius | 7 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 牧场、羊（羊毛来源物） | 否 |
| A5 | OGA hay-bale | <https://opengameart.org/content/hay-bale> | CC0 | carlosjorgereis | 1 | 低模干草捆 | 7z | 346 KB | 高 | 干草 | 否 |
| A6 | OGA rope-fence | <https://opengameart.org/content/rope-fence> | CC0 | Clint Bellanger | 3+零件 | 低模绳栏 | 页面未列 | 页面未列 | 高 | 木栅栏 | 否 |
| A7 | Poly Pizza Stone Wall (Quaternius) | <https://poly.pizza/m/tdeAOh3LQV> | **CC0** | Quaternius | 1 | 216 tri | FBX/GLTF | 页面未列 | 高 | 石墙 | 否 |
| A8 | Poly Pizza Hay (Quaternius) | <https://poly.pizza/m/Yu8TOERkpw> | **CC0** | Quaternius | 1 | 488 tri | FBX/GLTF | 页面未列 | 高 | 干草/麦束 | 否 |
| A9 | Kenney Survival Kit | <https://kenney.nl/assets/survival-kit> | CC0 | Kenney | 80 | 低多边形+动画 | 页面未列 | 页面未列 | 高 | 木箱、桶、营地、工具外形 | 否 |
| A10 | Poly Pizza Snail | <https://poly.pizza/m/aZ_cT-AIu2y> | CC BY 3.0 | Poly by Google | 1 | 399 tri | FBX/GLTF | 页面未列 | 高 | 蜗牛 | 否 |
| A11 | Poly Pizza Bell | <https://poly.pizza/m/efbZhPRsTuD> | CC BY 3.0 | Poly by Google | 1 | 862 tri | FBX/GLTF | 页面未列 | 中 | 羊铃 | 否 |
| A12 | ambientCG Ground054 / Ground110 | <https://ambientcg.com/view?id=Ground054> | CC0 | ambientCG | 1 | 写实 PBR | JPG/PNG | 9.9 MB（110） | 低 | 沙土小径地面 | 否 |

### 2.2 主题 B：一个人的乱房间（MessyRoom）

| # | 包/件 | URL | 许可 | 作者 | 件数 | 面数/风格 | 格式 | 体积 | 契合 | 覆盖类别 | 已入库 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| B1 | Quaternius Ultimate House Interior | <https://quaternius.com/packs/ultimatehomeinterior.html> | CC0（页面） | Quaternius | 123 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 家具/容器/杂物大类 | 否 |
| B2 | Kenney Furniture Kit | <https://kenney.nl/assets/furniture-kit> | CC0 | Kenney | 140 | 低多边形统一 | FBX/OBJ/GLB | 5.1 MB | 高 | 床、桌、椅、柜、门 | **是** |
| B3 | Quaternius Ultimate Furniture | <https://quaternius.com/packs/ultimatefurniture.html> | CC0（页面） | Quaternius | 20 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 柜/抽屉/床 | 否 |
| B4 | Quaternius Furniture | <https://quaternius.com/packs/furniture.html> | CC0（页面） | Quaternius | 23 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 桌椅柜 | 否 |
| B5 | OGA lowpoly-survival-item-pack | <https://opengameart.org/content/lowpoly-survival-item-pack> | CC0 | artie31 | 约 12 | 低模+贴图 | 页面未列 | 页面未列 | 中 | 电池、桶、杯、瓶、手电 | 否 |
| B6 | Poly Pizza Dresser | <https://poly.pizza/m/0K05lwk9caj> | CC BY 3.0 | CMHT Oculus | 1 | 1864 tri | FBX/GLTF | 页面未列 | 中 | 抽屉柜 | 否 |
| B7 | Poly Pizza Large Wardrobe | <https://poly.pizza/m/BWgx6mJnAF> | CC BY 3.0 | Zsky | 1 | 1124 tri | FBX/GLTF | 页面未列 | 高 | 衣柜 | 否 |
| B8 | Poly Pizza Tissue Box | <https://poly.pizza/m/dS4MQQH5EM> | **CC0** | CreativeTrio | 1 | 102 tri | FBX/GLTF | 页面未列 | 高 | 纸巾盒 | 否 |
| B9 | Poly Pizza Chips | <https://poly.pizza/m/uF1dGn3HXi> | **CC0** | CreativeTrio | 1 | 460 tri | FBX/GLTF | 页面未列 | 中（是否袋装 UNVERIFIED） | 薯片袋 | 否 |
| B10 | Poly Haven Shelf_01 | <https://polyhaven.com/a/Shelf_01> | CC0 | Gabriel Radić | 1 | 182 tri，写实贴图 | glTF/FBX/Blend | 页面未列 | 中（换材质后高） | 书架 | 否 |
| B11 | Poly Haven painted_wooden_cabinet_02 | <https://polyhaven.com/a/painted_wooden_cabinet_02> | CC0 | Kirill Sannikov | 1 | 966 tri | glTF/FBX/Blend | 页面未列 | 中 | 衣柜/立柜 | 否 |
| B12 | Poly Pizza Radiator | <https://poly.pizza/m/4XJ-DH66eKY> | CC BY 3.0 | Poly by Google | 1 | 3600 tri | FBX/GLTF | 页面未列 | 中 | 暖气片 | 否 |

### 2.3 主题 C：野餐草坪 0.03–0.35 m 小件

| # | 包/件 | URL | 许可 | 作者 | 件数 | 面数/风格 | 格式 | 体积 | 契合 | 覆盖类别 | 已入库 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| C1 | Kenney Food Kit | <https://kenney.nl/assets/food-kit> | CC0 | Kenney | 200 | 低多边形统一 | FBX/OBJ/GLB | 4.6 MB | 高 | 餐具、杯子、水果、食物 | **是** |
| C2 | Quaternius Ultimate Food | <https://quaternius.com/packs/ultimatefood.html> | CC0（页面） | Quaternius | 103 | 无贴图低模 | FBX/OBJ/Blend | 页面未列 | 高 | 食物、饮料 | 否 |
| C3 | Poly Pizza Coin (Quaternius) | <https://poly.pizza/m/QHZtj94fvh> | **CC0** | Quaternius | 1 | 206 tri | FBX/GLTF | 页面未列 | 高 | 硬币 | 否 |
| C4 | Poly Pizza Twig (Kenney) | <https://poly.pizza/m/xApCbtFYP8> | **CC0** | Kenney | 1 | 64 tri | FBX/GLTF | 页面未列 | 高 | 树枝 | 否 |
| C5 | Poly Pizza Pebble Round (Quaternius) | <https://poly.pizza/m/icVsN3lmVy> | **CC0** | Quaternius | 1 | 136 tri | FBX/GLTF | 页面未列 | 高 | 石子 | 否 |
| C6 | Poly Pizza Matchbox (Quaternius) | <https://poly.pizza/m/22eU6FjNQ8> | **CC0** | Quaternius | 1 | 396 tri | FBX/GLTF | 页面未列 | 高 | 火柴盒 | 否 |
| C7 | OGA low-poly-coin-3d | <https://opengameart.org/content/low-poly-coin-3d> | CC0 | iamsujitcu | 1 | 380 tri | FBX/blend | 92 KB | 高 | 硬币 | 否 |
| C8 | OGA dice-low-poly | <https://opengameart.org/content/dice-low-poly> | CC0 | STKRudy85 | 1 | 低模 | 7z | 67 KB | 中 | 骰子 | 否 |
| C9 | OGA key-collection | <https://opengameart.org/content/key-collection> | CC0 | Renderwahn | 多个 | 低模 | blend | 2.2 MB | 高 | 钥匙 | 否 |
| C10 | Poly Pizza Trading Card | <https://poly.pizza/m/eNL1vYa4P6b> | CC BY 3.0 | Vice Cooper | 1 | 92 tri | FBX/GLTF | 页面未列 | 高 | 卡片 | 否 |
| C11 | Poly Pizza Round Coasters | <https://poly.pizza/m/abXQzBR4wRk> | CC BY 3.0 | Jarlan Perez | 1 | 724 tri | FBX/GLTF | 页面未列 | 高 | 杯垫 | 否 |
| C12 | OGA mugglassplateknife | <https://opengameart.org/content/mugglassplateknife> | CC0 | pasi | 4 | 低模 | blend | 920 KB | 中 | 杯/盘/刀 | 否 |

## 3. 必须程序化建模的类别

**没有任何 CC0/CC-BY 包能直接用，必须自建（Blender 纯色 Principled）：**

1. **粪球本体**——所有来源都没有"滚动的屎球 + 干燥裂纹"。球体 + 顶点色/少量法线扰动，建议单独一个 300–600 tri 的变体集（干净/开裂/沾草屑）。
2. **屎壳郎角色**——Mini Characters 与 Quaternius 人形都不适用；必须自建（球体+六足+推球姿态），并注意 VRChat 里它只是世界装饰，不做 gameplay 碰撞。
3. **脏衣物褶皱**：袜子的塌陷、T 恤的堆叠、裤子的折叠。Poly Pizza 的 Socks/T-shirt 是"平铺干净"形态，改形成本高于自建。
4. **纸团**——无任何候选；低模揉纸（不规则球 + 硬边）反而更好做。
5. **充电线 / 耳机线**——需要曲线管（Curve + bevel）才能有自然垂坠，包里的都是直件。
6. **弹珠**——Kenney Marble Kit 只有轨道没有球；Poly Pizza 搜不到合适单件（见 §5）。球体 + 内部彩色螺旋。
7. **糖包、便利贴（尺寸/厚度可变体）、太阳镜**——零星小件，包覆盖率低且尺寸不对。
8. **树篱（墙状整排）**——Nature Kit 只有单株树/灌木，成排树篱要自己做模块。
9. **沙土小径 / 地形网格、草地铺层**——只有 PBR 贴图可用，几何必须自建（并且这是玩法地面，必须我们自己的碰撞）。
10. **麦穗、谷粒、散落干草的单根细节**——干草捆有包，但 0.03–0.1 m 的散粒没有；这类在块魂里恰恰是"最小一层"的吞取对象。
11. **羊毛团、皮革带、羊铃的挂带**——Bell 只有铃本体。

**有 CC0 包但必须改形/换材质才能用（不要直接进场景）：**

- 书架/柜/床（Poly Haven 是照片级贴图，必须重做纯色材质，且要减面）。
- 木箱/桶（Kenney Survival Kit 与 Quaternius 尺寸偏"游戏道具"，需要按真实米制重定标）。
- 石头（Nature Kit 的石头偏"卡通圆石"，Poly Haven `rock_09` 是 23k tri 扫描，二者不能混用）。

## 4. 许可记录模板

每个第三方素材（不是每个包）一行。字段与 `credit_id` 方案见 [README §4](README.md)，此处给出可直接落 CSV 的版本：

| 字段 | 含义 | 取值示例 |
|---|---|---|
| `asset_id` | 世界内唯一 id，与 prop_catalog 对齐 | `prop_dung_ball_a` |
| `display_name` | 显示名 | `Dung Ball A` |
| `pack` | 来源包/条目名 | `Quaternius Ultimate Crops` |
| `source_url` | 该件或该包的页面 | `https://quaternius.com/packs/ultimatecrops.html` |
| `author` | 作者（页面署名原文） | `Quaternius` |
| `license_id` | 许可标识 | `CC0-1.0` / `CC-BY-3.0` |
| `license_url` | 许可法条页 | `https://creativecommons.org/publicdomain/zero/1.0/` |
| `download_date` | 实际下载日（不是调研日） | `2026-11-20` |
| `sha256` | 原始压缩包哈希 | `3f9c…` |
| `allows_commercial` | 是否可商用 | `yes` |
| `requires_attribution` | 是否需署名 | `no` |
| `attribution_text` | 世界内 credits 原文（CC0 可空） | `"Fence" by Quaternius, CC0` |
| `triangles` | 实测三角面 | `216` |
| `material_slots` | 材质槽数 | `1` |
| `texture_dims` | 贴图尺寸，无贴图写 `none` | `none` / `1024x1024` |
| `notes` | 改形说明、风险 | `rescaled to 0.35 m; removed normal map` |
| `review_status` | 流程状态 | `candidate` / `verified` / `rejected` |

示例行（仅示意格式，**不代表已入库**）：

```csv
asset_id,display_name,pack,source_url,author,license_id,license_url,download_date,sha256,allows_commercial,requires_attribution,attribution_text,triangles,material_slots,texture_dims,notes,review_status
prop_stone_wall_a,Stone Wall,Quaternius (via Poly Pizza),https://poly.pizza/m/tdeAOh3LQV,Quaternius,CC0-1.0,https://creativecommons.org/publicdomain/zero/1.0/,,yes,no,,216,1,none,"page-verified 2026-11-15; license label on page is CC0 link",candidate
```

## 5. 未核验清单（共 15 条）

1. Kenney 八个候选包的**三角面数与包体积**——页面不列，未下载。
2. Kenney Survival / Toy Car / Holiday / Mini Characters 的**包内文件清单与是否含贴图**——页面不列。
3. Quaternius 候选包的**包内 `LICENSE` 文本**——未下载，只依据页面元数据块。
4. Quaternius 的 QAL 是否**回溯**适用于旧包——QAL §7 只说"不追溯"，新旧包共存的具体适用未取得作者书面说明。
5. Quaternius 84 包扫描中 2 个 QAL 包的**判定依据**为页面 `License` 字段文字，未读其包内文件。
6. Poly Pizza **CC0 条目的可见文案**——读取时触发 HTTP 429，只取到 `publicdomain/zero/1.0` 链接，未取到"Creative Commons Zero"字样。
7. Poly Pizza 的 `Chips`（`/m/uF1dGn3HXi`）**是否为袋装**——只有标题和预览图，未看模型。
8. Poly Pizza 的 `Bottlecap`（`/m/7HsR9BPZpi6`）、`Dice`（`/m/dLS2cGjYw6C`）——两次抓取失败，未核验。
9. Poly Pizza 上**没有搜到**合适 CC0 单件的品类（纸团、弹珠、糖包、太阳镜、耳机盒、洗衣篮）——只证明"关键词搜索首页没有"，不等于站点没有。
10. Poly Haven 的**资产页正文**（JS 渲染，只取 API 的 `polycount`/`dimensions`/`authors` 字段）；`painted_wooden_cabinet_02` 等页面 URL 未逐页读正文。
11. ambientCG 多数候选的**1K-JPG 体积与分辨率**——只取到 4 个样本（Ground110 / WoodFloor051 / Tiles141 / PavingStones152）。
12. OGA `dice-low-poly` 描述里同时出现 "Textured" 与 "Attribution Instructions / license.txt"，**是否真为纯 CC0** 需看包内 license.txt。
13. OGA `snail-3d-model` 为照片扫描（约 3k tri、写实），**能否压到卡通风格**未验证。
14. itch.io KayKit 的**新版页面**（legacy 页已核验 CC0，新版只在 legacy 页被链接，未打开）。
15. itch.io Retro House Pack 的 Pixabay / Pexels / Unsplash **第三方贴图许可**——未逐张追溯。

另有一条不算"未核验"但必须写明的判断：**"已入库"一栏全部来自仓库内 `intake_manifest.csv` 与 `Docs/AssetIntake/Downloads/` 的现状，不是对文件内容的复查。**

## 6. 建议的下一步（给 Lead）

1. 先定 **CC0-only 还是允许 CC-BY**（README §5 待决第 2 条）。若 CC0-only，B 主题会掉一大半（袜子、T恤、马克杯、抽屉柜、暖气片、手柄、书堆都只有 CC BY 版本），必须接受自建或换包。
2. 若允许 CC-BY：B 主题优先 Poly Pizza 的 `Socks` / `T-shirt` / `Mug` / `Dresser` / `Radiator`，C 主题优先 `Fork` / `Chopsticks` / `Pencil` / `Card` / `Coaster`，全部进世界内 credits。
3. A 主题最省事：Quaternius `Ultimate Crops` + `Farm Buildings` + `Farm Animals` 三包 + OGA `hay-bale` / `rope-fence`，覆盖"牧场/菜园/院子/栅栏/干草"。
4. 下载时务必保留：原始 zip、包内 LICENSE、下载日期、SHA-256——本文件 §4 的模板就是为此准备的。
5. 品牌暗示条目（LEGO、Nintendo/Switch/GameCube 手柄、Reese's 等）**在候选阶段就该剔除**，别等到 credits 阶段。

---

调研日期：2026-11-15。本文不授权下载、导入或发布；所有候选仍按 [license_gaps](../../AssetIntake/license_gaps.md) 的"候选、未验收"状态管理。相关：许可差距见 [license_gaps](../../AssetIntake/license_gaps.md)，入库记录见 [intake_manifest.csv](intake_manifest.csv)，来源矩阵见 [README](README.md)。
