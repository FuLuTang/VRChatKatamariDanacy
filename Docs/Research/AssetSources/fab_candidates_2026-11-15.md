# Fab 免费 listing 候选与连接器调查（2026-11-15）

状态：只读公开页面的**候选**调研。未登录、未领取（claim）、未下载任何素材、未导入 Unity、未修改任何账号设置。本文不构成法律意见，也不改变任何许可结论；项目许可边界仍以 [AssetSources/README.md](README.md) §1–§2 为准。

范围：三个新主题的**免费** Fab 候选（屎壳郎草甸、乱房间、野餐草坪），以及「Playwright 驱动用户 Chrome」与「非官方 fab-mcp」两条自动化路线的可行性。前三张图之外的旧来源（Kenney/Poly Pizza/Quaternius）不在本文重复。

## 0. 结论

- **Fab 值得用，但只当“补充件”**： 23 条候选里 9 条是 Fab Standard（Personal 档 $0）、14 条是 CC-BY，没有 NC/SA/ND。它的价值在于**补齐 Kenney/Poly Pizza 缺的品类**（稻草人、卡通昆虫、野餐套装、洗衣篮、文具、书堆），而不是当主底座——写实扫描件（Quixel、照片扫描）与我们的低模卡通风格冲突，且单件动辄几十 MB 到数 GB。
- **优先看 Unity/FBX/GLB**：`unreal-engine` 单格式的 listing（哪怕标 CC-BY、$0）在 Unity 2022.3 里不能直接用，已剔除或降级标注。
- **两条路今天都不在我手里跑**：官方 Fab Agent API/MCP 仍是 Roadmap（无文档、无入口）；非官方 `fab-mcp` 不需要 API key、不需要登录，但需要宿主装 Node 20.19+ 与 Playwright Chromium，而我是子代理，不能装环境、不能用浏览器、不能改配置。能给 Lead 的只有“匿名读”这一条可执行结论（§3.5）。
- **核验口径**：`已核验` = 公共 listing 页返回 200 且 `/i/listings/{uuid}` 公共 JSON 的标题/卖家/许可档位/价格/格式一致；`UNVERIFIED` = 上述任一取不到。**许可正文（Fab Standard vs legacy UE 许可）在匿名 JSON 里读不到**，全表统一标为“文本未核验”，见 §2.1。

## 1. 候选 listing

价格一律是 **USD 或列表页就地货币**的“起价 + 各档”。Fab 的 `Personal` 档按“过去 12 个月营收是否超过 10 万美元”选；本项目现阶段不商用，取 Personal 档。选错档可能被要求补差价（论坛案例，非官方裁决，见 [README §2.1](README.md)）。`$0/$0` 表示两档都 $0。所有条目 **未领取**。

### 1.1 屎壳郎草甸（农场/牧场/干草/木栅栏/石头/草地/昆虫/旧工具）

| 标题 | 卖家 | 许可 | 价格 | 引擎与格式 | 三角面/风格 | 体积 | VRChat 适用性 | 核验 |
|---|---|---|---|---|---|---|---|---|
| [Willowwood - Free Low Poly Nature Pack](https://www.fab.com/listings/02523a32-55b0-45df-ac12-6a418d8574ef) | BluBluGames | Fab Standard | $0 / $0 | Unity package + FBX + UE 5.1 工程 | 风格化低模树林/草地/水，模块化 | 19.3 MB | **高**：自带 Unity 包，是 8 条草甸候选里唯一自带 Unity 工程、真正“开箱即用”的环境底 | 已核验 |
| [Free Pack - Stylized Hay Bale](https://www.fab.com/listings/3d097930-25ef-472e-b41b-f7731124bdf6) | PolyOne Studio | Fab Standard | Personal $0 / Professional $0.89 | Unity + FBX/OBJ/USD/Blender/Maya | 3 个干草卷，约 500–1k tris/个，4K Albedo | 4.4 MB | **高**：面数与体积都适合当可吞噬大件 | 已核验 |
| [🐞 Low Poly Cartoon Bugs — Free Sample Pack (3 Bugs)](https://www.fab.com/listings/d107770b-b915-4975-8b65-009c86be3fdf) | POLYGROVE | CC-BY | $0 | FBX | 瓢虫/粉毛毛虫/蚂蚁，大眼卡通 | 3.5 MB | **高**：最贴“卡通昆虫”，但**没有屎壳郎**，需要自己改色或补一只 | 已核验 |
| [Scarecrow](https://www.fab.com/listings/6ff113b2-f6bd-408e-878b-df43eda62ee1) | Glowbox3D | CC-BY | $0 | FBX + GLB | 稻草人，PBR；描述含 **NoAI** 条款 | 16.4 MB | 高：草甸地标/尺寸门参照物 | 已核验 |
| [Modular Low poly Fences Pack - Game ready](https://www.fab.com/listings/28ffe116-b37d-4106-8718-1374ed5c4db8) | Guru Games | Fab Standard | $0 / $0 | FBX + Blender + GLB | 24 个木栅栏/木结构，共用单张 64×64 贴图 | 1.6 MB | **高**：木栅栏边界、畜栏，模块化拼接 | 已核验 |
| [Free Pack - Rocks Stylized](https://www.fab.com/listings/a0746c4b-428b-4556-b4c9-98f70c2a30d4) | PolyOne Studio | Fab Standard | Personal $0 / Professional $1.80 | Unity + UE + FBX/OBJ 等 12 种 | 11 个风格化石头 | 8.2 MB | 高：石头/碎石，与 Hay Bale 同一作者，风格统一 | 已核验 |
| [Low Poly Garden Tools Pack](https://www.fab.com/listings/a40982e5-0b0b-400c-bfe8-73a3698b88fc) | Wahyu NT | CC-BY | $0 | Blender + GLB | 铁锹/锄/修枝剪/锤，低模 | 0.8 MB | 中高：旧工具；**贴图情况描述未写**，需开包确认 | 已核验 |
| [Wheelbarrow](https://www.fab.com/listings/c049af3e-4ab4-4c41-973b-afe407891e81) | Eleanie | CC-BY | $0 | FBX + Blender + OBJ | 1.35×0.63×0.6 m 手推车，2048 PBR | 22.1 MB | 中：贴图写实，与低模风格有落差 | 已核验 |

### 1.2 乱房间（家居杂物/衣物/文具/电子配件/厨房小物）

| 标题 | 卖家 | 许可 | 价格 | 引擎与格式 | 三角面/风格 | 体积 | VRChat 适用性 | 核验 |
|---|---|---|---|---|---|---|---|---|
| [Laundry Basket](https://www.fab.com/listings/558053c7-331f-434f-934b-ed1ba6b44dd0) | Hellz | CC-BY | $0 | Blender + GLB | 塑料洗衣篮，1k 贴图；作者自述“不确定还算不算低模” | 6.9 MB | 中高：房间角落的“脏衣堆”锚点 | 已核验 |
| [Pencils, Pencil holder and paper](https://www.fab.com/listings/4ce2f727-1dda-49b0-b79f-25bc314c8c46) | Zippa | CC-BY | $0 | Blender + OBJ + FBX | 低模文具三件 | 0.9 MB | **高**：小件、可大量散布 | 已核验 |
| [Pen, Pencil, Stapler and Holder](https://www.fab.com/listings/dce073d2-10a2-4d7b-8392-074c03f2f52d) | Andrei Ghenoiu | Fab Standard | Personal $0 / Professional $0.89 | glTF + GLB | 桌面文具四件 | 5.1 MB | 中高：只有 glTF/GLB，Unity 2022 需转换 | 已核验 |
| [Low Poly Kitchen Utensils Pack – 3D PBR Models](https://www.fab.com/listings/2de73170-8f27-424b-8e0b-68b67e65b73e) | SkyRooms | Fab Standard | $0 / $0 | FBX | 杯/碗/锅/盘/刀叉勺，4K+2K 贴图 | 65.7 MB | 高：厨房小物一包齐 | 已核验 |
| [Headphones Low Poly Pixelart](https://www.fab.com/listings/921bf392-e90f-4a5d-ab7e-4c735e9eff92) | Cyberleaf Studio | Fab Standard | $0 / $0 | FBX + GLB + Blender | 128×128 像素贴图耳机 | 0.2 MB | 中：电子配件有了，但像素风与低模卡通不同族 | 已核验 |
| [Book Stack](https://www.fab.com/listings/e774c9f6-0276-40d3-8008-495870e5e685) | MOJackal | CC-BY | $0 | OBJ + GLB | 一摞书 | 2.5 MB | 高：书堆/纸堆 | 已核验 |
| [Pile of clothes](https://www.fab.com/listings/15902ef7-0f31-4e7a-adc6-3157fddddc71) | SKRUNDA | CC-BY | $0 | FBX + GLB | **照片扫描**衣服堆，8K 贴图 | 26.7 MB | 低：写实扫描与低模冲突，只适合当远景“脏乱”体积 | 已核验 |
| [Cardboard Box](https://www.fab.com/listings/4a04dd6f-e0b9-4109-aa16-a8b75155ba8e) | ValentinB | Fab Standard | Personal $0 / Professional $4.52 | FBX + UE + 附加文件 | 照片扫描纸箱，LOD0–3，4K–1K | 76.9 MB | 低：写实、含 UE 格式；FBX 可用但性价比差 | 已核验 |

### 1.3 野餐草坪（小件餐具/食物/野餐用品）

| 标题 | 卖家 | 许可 | 价格 | 引擎与格式 | 三角面/风格 | 体积 | VRChat 适用性 | 核验 |
|---|---|---|---|---|---|---|---|---|
| [Picnic Set (free)](https://www.fab.com/listings/3eb1f101-fa03-4ef4-afab-764efa79293b) | Juliette | CC-BY | $0 | FBX + GLB | 苹果/奶酪/酒瓶/篮子，低模 | 4.5 MB | **高**：四个件正好是野餐篮+食物 | 已核验 |
| [Picnic Table - Low Poly](https://www.fab.com/listings/cc8f0a5f-90e1-47d7-858d-7283cd40f79f) | Derek Flanagan | CC-BY | $0 | FBX + GLB | 木野餐桌，2K 贴图；描述明确面向 VR | 25.8 MB | **高**：巨物野餐桌的现成参考（我们仍要尺寸门版本） | 已核验 |
| [Metal cutlery](https://www.fab.com/listings/4220f68e-91dd-4614-8b73-b4260f5118d5) | Okayest | CC-BY | $0 | FBX + GLB | 刀/叉/勺，不锈钢 | 6.5 MB | 高：餐具小件 | 已核验 |
| [Stylized Food - Culinary Pack](https://www.fab.com/listings/9426a16d-2a24-44a5-8115-0d73686e920a) | NewLua Studios | CC-BY | $0 | FBX + Unity + UE | 20 种风格化食物 | 10.1 MB | **高**：食物品类最全的一包，且带 Unity 格式 | 已核验 |
| [Plates and Cups Set（BHS）](https://www.fab.com/listings/13406c32-152c-4209-b355-3902f8d1ee4f) | BlenderHeadStudios | CC-BY | $0 | glTF + 附加文件 + GLB | 盘/杯/餐具，PBR | 46.7 MB | 中高：只有 glTF 家族 | 已核验 |
| [FREE Party Cups](https://www.fab.com/listings/8de0b8d1-ebbc-45d7-a935-99fc5dd6bd7d) | SethsStudio | CC-BY | $0 | Blender/FBX/GLB/USDZ | 25 款塑料杯（含满杯/叠杯）+ 派对桌；作者声明无 AI、无扫描 | 261 MB | 中：体积大，只能挑单件用 | 已核验 |
| [Cooler Box](https://www.fab.com/listings/97afc2e8-7971-438d-9794-d863b27542e7) | Guy in a Poncho | Fab Standard | $0 / $0 | Blender/FBX/OBJ/GLB/glTF/USDZ | **2,778 tris / 1,544 verts**，1024 PBR | 9.3 MB | **高**：唯一明确标了三角面的野餐件；注意与 README 附录里 Poly Pizza 的 CC0 “Cooler Box” 同名不同源 | 已核验 |

## 2. 许可与风险

### 2.1 许可口径

- **Fab Standard**：商用、跨引擎、随 Project 分发、无需署名；禁止终端用户抽取、禁止单独再分发、禁止放进世界/关卡编辑工具或模板。详见 [README §2.1](README.md) 与 [Fab EULA](https://www.fab.com/eula)。
- **CC-BY**：可商用、必须署名。世界内需要 credits 板，台账要写 `title / author / license / source_url / changes`。本文不替项目决定署名位置。
- **全部条目无 NC/SA/ND**：`Personal/Professional` 是非 CC 的 Fab 许可，`CC-BY` 是 CC 4.0 家族里最宽松的署名型，均不在硬规则禁止的 NC/SA/GPL/ND 之列。
- **许可文本本身未核验**：匿名 `/i/listings/{uuid}` 只返回 `Personal`、`Professional` 两个**档位名**与价格，**不返回适用哪一版 EULA**（Fab Standard 还是 legacy UE Marketplace License）。Fab 文档说迁移商品会在 listing 右侧 Details 标注，但那块需要页面 JS 渲染，本次取不到。**不要**把本文的“Fab Standard”当成对许可文本的核验结论，它是“档位名 + Fab 现行默认”的推断。

### 2.2 已发现的坑

1. **标题写 CC0、listing 许可选的是 CC-BY**：[CC0 - Kitchen Knife（plaggy）](https://www.fab.com/listings/b2ead203-d418-464f-b032-891550a03da3) 标题和描述都说 “CC0 1.0 Universal”，但 listing 的许可选择器返回 `CC-BY`（`isCc: true`）。plaggy 有整组 “CC0 - …” 命名（本次核到的有 Pencil Holder、Pencil、Kitchen Knife），**不能按标题判定**；按平台记录当 CC-BY 处理，除非作者书面澄清。这条也说明本项目台账必须记 `license` 字段而不是 `title`。
2. **UE 单格式 ≠ 能用**：[Modern Kitchen Clutter Pack（BarisSOYKOK）](https://www.fab.com/listings/3f12749a-81d3-4b16-a74a-51a747b2b701) 是 CC-BY、$0、17 个玻璃杯 + 12 件餐具，看着完美，但格式只有 `unreal-engine`，Unity 侧等于零。同类还有 [Kitchen Utensils Vol1](https://www.fab.com/listings/473a702d-8463-4604-8ff8-dee8fc466816)（$0/$0，仅 UE）。
3. **附加条款**：Glowbox3D 的 Scarecrow / Ladybug 描述里带 **NoAI**（不得用于生成式 AI 数据集/训练）。它不限制游戏内使用，但会进我们的素材台账，别把它当“纯 CC-BY”。
4. **写实扫描 vs 低模**：Quixel Megascans 现在全库 $0 且出现在每个搜索里，但单件 200 MB–16 GB、8K 贴图、写实风格。§1 只保留了少数“体积可控”的扫描件，并标注为低适用性。
5. **仓库公开 vs Fab Standard**：`OPEN_DECISIONS` 2026-11-14 决定“开发阶段素材照常提交进 GitHub（目前公开）”。这与 [README §5 第 4 条](README.md) 的“Fab Standard 素材不能进公开仓库”直接冲突。不判定，只标注：**在用户决定前，Fab Standard 素材应只落在 `.gitignore` 忽略的 `Docs/AssetIntake/Downloads/`，不进 Git**。CC-BY 条目可进仓库但必须带署名与许可文本。

## 3. FAB 连接器调查

### 3.1 `@threenative/fab-mcp`（npm 上的那个包）

- **版本与时间**：`@threenative/fab-mcp` 只有 **0.1.0**，2026-07-28 发布，此后**没有再更新**。[npm registry](https://registry.npmjs.org/@threenative/fab-mcp)
- **仓库**：`github.com/jonit-dev/fab-mcp`，现在 301 跳转到 `github.com/ThreeNativeHQ/threenative-asset-mcp`——作者把仓库并入了新项目。
- **包元数据**：`license: UNLICENSED`（不是开源许可），`engines.node >= 20.19.0`，依赖含 `playwright`、`@modelcontextprotocol/server`、`zod`。
- **鉴权**：README 原文 “**No Epic or Fab login is required or automated**”。搜索/详情/免费文件下载都不需要 API key；也不需要 Epic 账号。这与 Fab 页面上“领取/加入 Library”的常规路径不是一回事（后者要 Epic 账号，本文未实测）。
- **能做什么**（工具清单来自 0.1.0 README）：`fab_search_assets`（默认 `priceMode: free`）、`fab_get_asset`（正规化详情 + 各档实际价格）、`fab_list_filters`、`fab_list_limited_time_free`（只读策展的限免面）、`fab_download_free_asset`（**仅**“直接可下的免费文件”，需显式接受 EULA）。
- **不能做什么**：不购买、不领取进 Library、不加愿望单、不结账；不碰购买/库/账号动作。
- **数据来源**：Fab **未公开**的 `/i/*` JSON 路由（`/i/listings/search`、`/i/listings/{uuid}`）。README 自己写 “Fab's `/i/*` JSON routes are undocumented and can change or restrict automated access”。
- **浏览器姿态**：只在 Fab 要求验证时启动**自己的专用 Playwright Chromium profile**，明确“从不 attach、复制或读取用户正常 Chrome/Edge profile、cookie、密码或 Epic session”；遇到 challenge 返回 `FAB_BROWSER_ATTENTION_REQUIRED`，**声称不解决也不绕过** challenge。

### 3.2 后继包 `threenative-asset-mcp`（0.16.0，实际在用这个）

- **时间线**：npm 0.16.0 发布于 **2026-11-08**；GitHub 仓库 2026-07-28 建，**最后 push 2026-11-13**；5 stars、1 fork、1 个 open issue；**仓库没有 LICENSE 文件，npm 标 UNLICENSED**。[repo](https://api.github.com/repos/ThreeNativeHQ/threenative-asset-mcp)
- **范围大得多**：40+ 个工具（Glama 工具清单口径，未逐一核对），除 Fab 外还有 Poly Haven、ambientCG、Smithsonian、Polyfork、Sketchfab、Kenney、策展 itch.io 包，以及 `fab_import_asset`/`asset_import_unreal`/`asset_import_unity`（把 UE/Unity 包转 GLB）、`mesh_optimize`、`audio_*`。
- **鉴权差异**：Fab 搜索与免费文件仍**无需登录**；`fab_list_owned` / `fab_import_asset` 对已购包需要 **FabCLI 登录**（Epic 账号）；Sketchfab 下载需要 `SKETCHFAB_API_TOKEN`；`audio_generate_sound` 花自己的 ElevenLabs credits。
- **风险增量（关键）**：[docs/providers.md](https://github.com/ThreeNativeHQ/threenative-asset-mcp/blob/main/docs/providers.md) 写明 Fab 的 `/i/*` 在 Cloudflare 后面，会 challenge Node 的 TLS 指纹；若有 `curl-impersonate`（如 `curl_chrome146`）在 PATH 上，就**用它伪装真实浏览器 TLS 指纹**完成全部匿名 JSON 读取。这与 0.1.0 的“不绕过 fingerprinting”承诺**不一致**——0.1.0 的 contract 文档写的是 “classifies challenges and never attempts to bypass CAPTCHA or fingerprinting”，后继实现则主动伪装指纹。
- **其余限制**：无遥测；日志只走 stderr；默认不记录查询文本（`FAB_LOG_QUERIES=1` 才记）；下载只写自己的目录、不覆盖；限免面不稳定（`fab_list_limited_time_free` 的策展接口未确认）。

### 3.3 官方：Epic/Fab 有没有 Agent API 或 MCP？

- **只有一份 Roadmap 帖**：[Fab Agent API and MCP - Experimental](https://forums.unrealengine.com/t/fab-agent-api-and-mcp-experimental/2852871)，Thomas Convard（Epic）2026-11-09 发，**Status: None**，正文只有一段能力描述（“search, asset details, library and downloads”“licence acceptance handled as part of the flow”），**没有文档链接、没有申请入口、没有访问前提**。
- **文档站没有对应页**：`https://dev.epicgames.com/documentation/en-us/fab/fab-agent-api` 返回 200 但只有应用外壳（无 `<title>`、31 KB），同域已知页面 [licenses-and-pricing-in-fab](https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab) 返回 103 KB 且有标题。结论：**页面不存在**，不是“有文档但没权限”。
- **不要混淆**：Epic 的 **Unreal MCP**（编辑器内、localhost、给 UE 用的）与 Fab 的素材 API 是两件事；社区教程里的“MCP + Claude Code 搭 UE 关卡”用的是前者。
- **结论**：截至 2026-11-15，**没有可用的官方 Fab Agent API 或官方 MCP**，也没有 API key 申请渠道。任何“官方 API”说法都应按 Roadmap 处理。

### 3.4 两条路线对比

| 维度 | Playwright MCP 驱动用户真实 Chrome | fab-mcp（匿名 `/i/*` + 可选 curl-impersonate） |
|---|---|---|
| 今天能否用 | 能，但**只有 Lead 一个 owner**，我不能碰浏览器 | 技术上能（Node 20.19 + Playwright Chromium），但我是子代理不能装环境/改配置 |
| 需要 API key / 登录 | 搜索不需要；领取/下载走用户自己的 Epic 登录态 | Fab 全程不需要登录；只有 `fab_list_owned`/`fab_import_asset` 需要 FabCLI 登录 |
| 看到的东西 | 真实页面：许可选择器、Details、价格、限免标记，人能复核 | 结构化 JSON：标题/卖家/档位/价格/格式；**许可正文读不到**；限免面不稳定 |
| 账号安全 | 用用户自己的会话，动作可见、可中断；风险是误点“领取/购买” | 不碰用户 profile（0.1.0 明确拒绝 attach），但 token/cookie 不落盘的好处建立在“伪装指纹”上 |
| ToS / 稳定性 | 真人操作，最接近“正常浏览”；但自动化点击仍可能触发风控 | 抓未公开 `/i/*`；后继版主动伪装 TLS 指纹绕 Cloudflare；上游一改就 `FAB_UPSTREAM_CHANGED` |
| 许可核验 | 能读页面上的许可名与 EULA 摘要 | 只能给档位名；**必须**再人工看 listing 页 |
| 适合做什么 | 逐条核验、截图存证、最终领取 | 批量搜索/筛“免费 + 有 Unity 格式”，产出候选清单 |

### 3.5 今天到底能走哪条

前提：没有 API key、不登录、不允许改账号设置。

- **官方 Agent API / MCP：不能走。** 不存在，只有 Roadmap 帖。
- **fab-mcp 的“匿名读”：能走，但不该由我来走。** 它不需要 key 和登录，`fab_search_assets` / `fab_get_asset` 只读；可它要 `npx` 装包、要 Playwright Chromium，属于“改本机环境”，而且它的下载功能会绕过正常领取流程——我们的硬规则是**不下载、不领取**，所以即便跑起来也只能用前两个只读工具。
- **Playwright 路线：Lead 能走，我不能。** 不需要 API key 就能匿名看 listing；要下载/领取时才用用户已登录的 Chrome。它是唯一能让人**看见**许可选择器与 Details 的路线。
- **给 Lead 的建议**：① 用 Playwright 路线逐条复核 §1 里标“已核验”的 23 条中**真正要用的 6–8 条**，重点确认 listing 右侧的许可名是 Fab Standard 还是 legacy UE；② `fab-mcp` 只在“需要再搜 20 个关键词”时用，且只调用 `fab_search_assets`/`fab_get_asset`，不调用任何 download 工具；③ 两条路线都替代不了包内 `LICENSE` 与贴图来源的逐文件验收。

## 4. 未核验清单

1. **许可正文（Fab Standard vs legacy UE Marketplace License）**：全部 9 条 Fab Standard 条目。匿名 JSON 只有档位名，listing 页 Details 需 JS。**这是本文最大的未核验项。**
2. **[Food FREE - Low Poly 3D Models Pack（ithappy）](https://www.fab.com/listings/59d75767-8f99-4d0d-adc4-49461dc84057)**：`/i/listings/{uuid}` 连续两次 403，listing 页 200 但正文是 lorem ipsum 外壳（无格式、无许可）。搜索行显示 `Personal/Professional`、起价 $0、含 Unity。**UNVERIFIED，已从候选表剔除。**
3. **Fab ToS / Epic AUP 原文**：`fab.com/terms-of-service` 是 JS 渲染（只有页脚），`dev.epicgames.com/en-US/services/terms/aup` 与 `agreements` 均 403。**无法引用“自动化访问是否违规”的原文条款**，只能引用非官方 README 的自述与 §3.2 的指纹伪装事实。
4. **限时免费（limited-time free）面**： 23 条全部是“长期 $0”或 CC-BY（`discountedPrice` 均为 null），**没有核验任何限免条目**，也没有核验“领取后是否永久保留”。
5. **三角面**：只有 Hay Bale（描述写 500–1k tris）与 Cooler Box（2,778 tris）有作者自述数字；其余按“低模”标注，实际面数需开包统计。
6. **包内 LICENSE / 贴图与模型来源**：全部条目未开包。CC-BY 条目尤其要确认作者是否真的持有全部贴图权利（Poly Pizza/Sketchfab 的混合来源常见）。
7. **贴图规格与 Unity 导入**：`glTF` 单格式条目（Pen/Stapler、Plates and Cups）在 Unity 2022.3 需转换；`Blender` 单格式条目要用 Blender 导出 FBX。均未实测。
8. **同名冲突**：`Cooler Box` 在 Fab（Guy in a Poncho，Fab Standard）与 Poly Pizza（MilkAndBanana，CC0）各有一个，[README 附录 A](README.md) 已入库后者；别把两个当同一个。
9. **`fab-mcp` 的实际行为**：只读了 npm 元数据、0.1.0 README 与后继仓库文档，**没有运行**，没有验证它今天的搜索是否仍能穿过 Cloudflare。

## 5. 来源 URL 清单（访问日期 2026-11-15）

Fab listing（23 条，见 §1 表格内链接；均以 `https://www.fab.com/listings/<uuid>` 访问，HTTP 200）：

- 草甸：`02523a32-55b0-45df-ac12-6a418d8574ef`、`3d097930-25ef-472e-b41b-f7731124bdf6`、`d107770b-b915-4975-8b65-009c86be3fdf`、`6ff113b2-f6bd-408e-878b-df43eda62ee1`、`28ffe116-b37d-4106-8718-1374ed5c4db8`、`a0746c4b-428b-4556-b4c9-98f70c2a30d4`、`a40982e5-0b0b-400c-bfe8-73a3698b88fc`、`c049af3e-4ab4-4c41-973b-afe407891e81`
- 乱房间：`558053c7-331f-434f-934b-ed1ba6b44dd0`、`4ce2f727-1dda-49b0-b79f-25bc314c8c46`、`dce073d2-10a2-4d7b-8392-074c03f2f52d`、`2de73170-8f27-424b-8e0b-68b67e65b73e`、`921bf392-e90f-4a5d-ab7e-4c735e9eff92`、`e774c9f6-0276-40d3-8008-495870e5e685`、`15902ef7-0f31-4e7a-adc6-3157fddddc71`、`4a04dd6f-e0b9-4109-aa16-a8b75155ba8e`
- 野餐：`3eb1f101-fa03-4ef4-afab-764efa79293b`、`cc8f0a5f-90e1-47d7-858d-7283cd40f79f`、`4220f68e-91dd-4614-8b73-b4260f5118d5`、`9426a16d-2a24-44a5-8115-0d73686e920a`、`13406c32-152c-4209-b355-3902f8d1ee4f`、`8de0b8d1-ebbc-45d7-a935-99fc5dd6bd7d`、`97afc2e8-7971-438d-9794-d863b27542e7`
- 反例/证据：`b2ead203-d418-464f-b032-891550a03da3`（CC0 标题 ↔ CC-BY 许可）、`3f12749a-81d3-4b16-a74a-51a747b2b701`（CC-BY 但仅 UE）、`473a702d-8463-4604-8ff8-dee8fc466816`（仅 UE）、`59d75767-8f99-4d0d-adc4-49461dc84057`（403）

公共数据接口（匿名，无 Cookie/Authorization；只读，未下载文件）：

- `https://www.fab.com/i/listings/search?q=<词>&is_free=1&count=24&sort_by=-relevance`
- `https://www.fab.com/i/listings/<uuid>?currency=USD`

连接器与官方口径：

- [npm registry: @threenative/fab-mcp](https://registry.npmjs.org/@threenative/fab-mcp)（0.1.0，2026-07-28，UNLICENSED）
- [unpkg: @threenative/fab-mcp@0.1.0 README](https://unpkg.com/@threenative/fab-mcp@0.1.0/README.md)
- [npm registry: threenative-asset-mcp](https://registry.npmjs.org/threenative-asset-mcp)（0.16.0，2026-11-08）
- [GitHub: ThreeNativeHQ/threenative-asset-mcp](https://github.com/ThreeNativeHQ/threenative-asset-mcp)、[providers.md](https://github.com/ThreeNativeHQ/threenative-asset-mcp/blob/main/docs/providers.md)、[fab-contract.md](https://github.com/ThreeNativeHQ/threenative-asset-mcp/blob/main/docs/fab-contract.md)、[docs/tools.md](https://github.com/ThreeNativeHQ/threenative-asset-mcp/blob/main/docs/tools.md)
- [Epic 论坛：Fab Agent API and MCP - Experimental（2026-11-09，Status: None）](https://forums.unrealengine.com/t/fab-agent-api-and-mcp-experimental/2852871)
- [Fab 文档：Licenses and Pricing in Fab](https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab)、[Fab EULA](https://www.fab.com/eula)
- [Glama 上的同一 MCP 条目（工具清单交叉核对）](https://glama.ai/mcp/servers/ThreeNativeHQ/threenative-asset-mcp)

---

调研日期：2026-11-15。本文只做候选与工具判断，不授权下载、领取、导入或发布。机器可读的旧表见 [sources.csv](sources.csv) 与 [intake_manifest.csv](intake_manifest.csv)；许可边界见 [README](README.md)。

## 6. Lead 页面复核（2026-11-15，Playwright 真实页面）

用 `Tools/mcp/mcp_cli.py --server browser` 逐条打开 27 个 listing，读页面里的 "License terms" 行与许可选择器。**没有登录、没有领取、没有加购、没有下载。**

- **26/27 读到许可行**：10 条 `Standard License`、16 条 `Creative Commons (CC-BY)`；ithappy 的 "Food FREE" 页面没有许可行，维持 UNVERIFIED。
- 这条补上了 §4 的第 1 项：listing 层面能确认 "Standard License"，但 Standard 的具体限制仍要读 EULA 原文。
- **标题不可信**：[CC0 - Kitchen Knife](https://www.fab.com/listings/b2ead203-d418-464f-b032-891550a03da3) 标题写 CC0，页面许可写 CC-BY。
- **价格不可从正文猜**：页面同时渲染推荐位金额。可靠的是许可选择器 "From free to ¥X"。Hay Bale / Rocks / Cardboard Box / Pen, Pencil, Stapler 四条明确有免费档；**`Cooler Box` 只见 ¥46.64、无 "From free"，§1.3 的 "$0/$0" 不成立，降级为「价格未确认」**。
