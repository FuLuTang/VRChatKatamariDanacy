# 素材来源与许可调研（AssetSources）

状态：2026-11-14 的**页面级**调研。只读公开许可页，未下载任何素材包、未导入 Unity、未改变任何授权结论。本文不构成法律意见，也不代替逐文件验收；项目许可仍未决定。

范围：VRC塊魂首发两张图（野餐草坪、玩具房）所需的道具、环境、纹理、音效。已有研究见 [03_MAPS_ASSETS](../../03_MAPS_ASSETS.md)、[AssetManifest](../AssetManifest/README.md)、[license_gaps](../../AssetIntake/license_gaps.md)、[AssetCoverage](../../Preparation/AssetCoverage/README.md)、[licensing_boundary](../../Preparation/Baseline/licensing_boundary.md)；本文只补“来源选择与许可边界”，不重做包内清点。

## 0. 五条结论

- **默认底座是 CC0**：Kenney（3D/音频）、Poly Haven、ambientCG 最省事，商用、随包分发、无需署名。
- **Quaternius 已改许可**：站点 2026-08-28 起有自定义 QAL，旧 Food Pack 页面仍标 CC0；同一作者的不同包必须逐包看 `LICENSE`，不能再用“Quaternius=CC0”概括。
- **Fab 免费资产可用但“谨慎”**：Standard License 允许商用、跨引擎、随 Project 分发、无需署名；但要求终端用户不能抽取，且不得把内容放进世界/关卡编辑工具或模板。部分旧 listing 仍是 UE-only。
- **CC-BY 可用，CC-BY-SA/GPL 不要碰**：Poly Pizza、Sketchfab、部分 OpenGameArt 需要世界内署名；CC-BY-SA 的“相同方式共享”和 GPL 与 Fab 标准许可不兼容，直接排除。
- **NC 是商业化红线**：Freesound 的 CC-BY-NC、Sketchfab 的 NC、OpenGameArt 的 NC、BBC RemArc 都不能进入“以后可能卖外观”的版本。BBC 只能内部原型。

## 1. 许可矩阵

| 来源 | 许可 | 商用 | 署名 | 随包分发 | NC/其它风险 | 风格契合 | 结论 |
|---|---|---|---|---|---|---|---|
| [Fab](https://www.fab.com/eula)（标准许可，含限时免费） | Fab Standard License（Personal/Professional） | 是 | 否 | 是，作为 Project 的 included dependency；禁止单独再分发 | 禁止用户抽取；禁止放进世界/关卡编辑工具或模板；部分旧 listing 仍为 legacy UE 许可；不得与 CC-BY-SA/GPL 混用（二手转述） | 中：多为 UE/写实、偏重，需转换与减面 | 谨慎 |
| Fab（CC-BY listing） | CC BY 4.0 | 是 | 是 | 是 | 需逐条看 listing；CC-BY 本身无 NC | 中 | 可用 |
| [Quixel Megascans（Fab）](https://quixel.com/news/update-megascans-are-going-free-again) | 2026-11-05 宣布全库在 Fab 免费（11-12 前陆续生效）；新下载按 Fab Standard；旧下载按当时 Megascans/Quixel 条款 | 是（新条款） | 否 | 是（新条款） | 旧下载可能是 UE-Only/按 plan 限制；写实扫描、体量大 | 低：与低多边形风格冲突 | 谨慎 |
| [Kenney 3D](https://kenney.nl/support) | CC0 1.0 | 是 | 否 | 是 | 无 | 高：统一低多边形、模块化 | 首选 |
| [Kenney Audio](https://kenney.nl/assets/impact-sounds) | CC0 1.0 | 是 | 否 | 是 | 无 | 高：卡通/UI/冲击 | 首选 |
| [Quaternius](https://quaternius.com/packs/ultimatefood.html) | 旧包页面标 CC0；站点新许可为 QAL v1.0 | 是 | 否 | 是（不得把素材本身再分发） | 许可刚变更，必须逐包核验 `LICENSE`；老包可能仍 CC0 | 高：低多边形食物/家具 | 可用 |
| [Poly Pizza](https://poly.pizza/m/tr0LwyNnCt) | 逐模型：CC BY 3.0（是否 CC0 逐模型看） | 是 | CC-BY 需要 | 是 | 需逐模型看许可；模型可能含第三方 IP | 高：低多边形、品类极多 | 可用 |
| [Poly Haven](https://polyhaven.com/license) | CC0 1.0 | 是 | 否 | 是 | 无 | 中：HDRI/写实纹理；模型偏写实 | 可用 |
| [ambientCG](https://docs.ambientcg.com/license/) | CC0 1.0 | 是 | 否 | 是 | 无 | 中：PBR 写实纹理，需降分辨率/调色 | 可用 |
| [Sketchfab](https://help.sketchfab.com/en/articles/16152215-crediting-users-for-3d-model-downloads) | 逐模型 CC0 / CC-BY（也有 NC/ND/SA） | 视模型 | CC-BY 需要 | 是（CC 模型） | 必须过滤 NC/ND/SA；下载常需登录 | 中：质量参差，需逐模型审 | 谨慎 |
| [OpenGameArt](https://opengameart.org/content/faq) | 混合：CC0/CC-BY/OGA-BY/CC-BY-SA/GPL | 视许可 | 视许可 | 视许可 | CC-BY-SA 传染；GPL 不清；预览图可能保留版权 | 中：2D/低模都有，风格杂 | 谨慎 |
| [itch.io](https://itch.io/docs/legal/terms) | 逐 pack 自定义（平台无统一素材许可） | 视 pack | 视 pack | 视 pack | 常见“personal use only”；必须读每个 pack 页 | 中：独立作者多，风格杂 | 谨慎 |
| [Freesound](https://freesound.org/help/faq/) | CC0 / CC-BY 4.0 / CC-BY-NC 4.0 | CC0/CC-BY 是；NC 否 | CC-BY 需要 | 是（CC0/CC-BY） | NC 不能进商业版本；逐音看许可 | 高：写实/拟音，需剪辑 | 可用 |
| [Sonniss GDC](https://sonniss.com/gameaudiogdc) | Sonniss #GameAudioGDC License v2.0（2026-08-27） | 是 | 否 | 是，作为项目一部分；不得作为音效库再分发 | 禁止 AI 训练；写实素材，卡通感需处理 | 中高：冲击/环境/拟音 | 首选 |
| [Pixabay](https://pixabay.com/service/license-summary/) | Pixabay Content License | 是 | 否 | 是（不得单独分发） | 不得 standalone；含可识别商标的内容不能商用 | 中：音效短小，质量参差 | 可用 |
| [Zapsplat](https://www.zapsplat.com/license-type/standard-license/) | Zapsplat Standard License（免费/ Premium） | 是 | 免费用户需要；Premium 免 | 是，作为项目一部分；不得再分发 | 免费用户须显著署名 “ZapSplat”；不得作为音效库分发 | 中高：UI/拟音/环境 | 可用 |
| [BBC Sound Effects（RemArc）](https://sound-effects.bbcrewind.co.uk/licensing) | RemArc Content Licence | **否**（需单独许可） | 是 | 否 | 仅非商业/个人/研究/正规教育；商用需 BBC 许可并可能付费 | 高：历史/环境录音 | 禁用 |
| 自制/程序化（Blender、Audacity、合成） | 自有版权 | 是 | 否 | 是 | 需保存工程文件与制作记录 | 最高：可精确匹配卡通风格 | 首选 |

## 2. 逐项核验（引文均 ≤20 词）

### 2.1 Fab

- 许可类型：Standard（Personal/Professional）与 CC-BY；免费内容可能走 Standard，也可能走 CC-BY。官方摘要称 “Use the assets with any compatible tools (usage is not limited to Unreal Engine)”，并允许 “Commercially distribute your Projects with the Fab assets incorporated into it”。禁止 “Resell or redistribute the asset for free on a standalone basis”。[Fab EULA](https://www.fab.com/eula)
- 署名：官方摘要明确 “You are not required to give credit to the publisher”；CC-BY listing 除外。[Fab EULA](https://www.fab.com/eula)
- 分层：Personal 与 Professional 的权利范围相同（“Both pricing tiers (Personal and Professional) grant you the same scope of rights”），区别是购买时年收入是否超过 10 万美元。**领取时按自己过去 12 个月营收选档；选错档可能被要求补差价**（论坛案例，非官方裁决，UNVERIFIED）。[Fab EULA](https://www.fab.com/eula)
- VRChat 世界能不能用：可以理解为“把内容作为 Project 的依赖分发”，但 EULA §4c 要求 “you must restrict end users from extracting or otherwise using Content outside of the Project”。VRChat 世界是编译后的 bundle，技术上仍可被扒；我们只能靠“不发布原始文件 + VRChat 条款禁止反向工程/篡改平台”来满足，残余风险需你决定。[Epic 论坛引 EULA §4c](https://forums.unrealengine.com/t/clarification-on-publishing-a-free-project-with-fab-store-assets/2404200)
- 格式：免费包常见只含 UE 工程/`.uasset`，下载前先看 Formats；Unity 只接受 FBX/GLB/OBJ/贴图或可转换的格式，别把 UE 资产当 Unity 包。
- 世界/关卡编辑工具：EULA §6 禁止 “may not make Content available in world- or level-editing tools”，也不能做成模板/可导出工程。**不要把含 Fab 资产的 VRChat 世界工程、预制件包或模板发给别人**；只发编译后的世界。[同上](https://forums.unrealengine.com/t/clarification-on-publishing-a-free-project-with-fab-store-assets/2404200)
- 价值要求：分发的 Project 必须 “reasonably add value beyond the value of the Content”，不能只剩素材本身。我们的世界符合。[同上](https://forums.unrealengine.com/t/clarification-on-publishing-a-free-project-with-fab-store-assets/2404200)
- Unreal-only 历史：Fab 文档说迁移商品 “might temporarily be available on Fab under the legacy UE Marketplace License”，且会在 listing 右侧 Details 标注。**逐个 listing 看许可，不能只看“Fab”两个字**。[Fab 文档](https://dev.epicgames.com/documentation/fab/licenses-and-pricing-in-fab)
- Megascans：2026-11-05 起 “We're making the full Quixel Megascans library free on Fab”；新下载按 Fab Standard，但旧下载按当时的 Megascans/Quixel 条款（可能有 UE-Only/plan 限制）。[Quixel 公告](https://quixel.com/news/update-megascans-are-going-free-again) 风格是写实扫描，不适合作为低多边形主素材。
- 限时免费：现在是每两周一轮、每轮免费两周；“Limited-Time Free products will only be free for two weeks”。免费期内领取即可按页面标注的 Standard/CC-BY 许可使用；**领取后是否永久保留，官方未逐字承诺（UNVERIFIED）**，领取时保留订单/截图。[Epic 论坛](https://forums.unrealengine.com/t/new-limited-time-free-content-program-on-fab/2092736)
- Agent 工具：Epic 在 2026-11-09 公布实验性的 Fab Agent API/MCP，“Fab exposes search, asset details, library and downloads to agents”；状态仍是 Roadmap。社区另有非官方 `@threenative/fab-mcp`（抓未公开 `/i/*` 接口，默认搜免费资产，下载需显式确认 EULA）。它只能当“找素材/查详情”的辅助，**不能替代 listing 许可与包内 LICENSE 核验**；自动化访问须遵守 Fab 条款，不要用它绕过登录/购买。[Epic 论坛](https://forums.unrealengine.com/t/fab-agent-api-and-mcp-experimental/2852871)、[npm](https://www.npmjs.com/package/@threenative/fab-mcp)
- 混用限制（二手）：LICENSEWARE 转述 Fab EULA 称 Standard 内容不得与 GPL/LGPL/CC-BY-SA 组合；我未直接取得 EULA 该段原文，标为 **UNVERIFIED**，但按保守策略执行。[LICENSEWARE](https://wiki.licenseware.io/wiki/fab-and-epic-content-licensing/)

### 2.2 CC0 / 宽松来源

- Kenney：“all game assets on the asset pages are public domain licensed (CC0)”；“Attribution is not required”。3D 与 Audio 包页面都标 “License Creative Commons CC0”。[Kenney 支持](https://kenney.nl/support)、[Impact Sounds](https://kenney.nl/assets/impact-sounds)
- Quaternius：Food Pack 页面仍标 “License CC0”；但站点新许可 QAL v1.0 写明可商用、无需署名，“You just can't resell or redistribute the assets themselves as assets”。**以包内 LICENSE 为准**。[Food Pack](https://quaternius.com/packs/ultimatefood.html)、[QAL](https://quaternius.com/license.html)
- Poly Pizza：模型页标 “Creative Commons Attribution”，链接到 CC BY 3.0；因此**逐模型**看许可、逐模型署名。示例页还注明模型作者与格式。[示例模型](https://poly.pizza/m/tr0LwyNnCt)
- Poly Haven：“Our assets are all licensed as CC0”；“You can redistribute them, share them around, include them when sharing your own work”。[Poly Haven License](https://polyhaven.com/license)
- ambientCG：“All ambientCG assets are provided under the Creative Commons CC0 1.0 Universal License”；可把原始文件放进游戏。[ambientCG License](https://docs.ambientcg.com/license/)
- Sketchfab：“Each downloadable model is offered under a Creative Commons license”；“giving credit is a necessity (unless the model is licensed as CC0)”。搜索时用 CC0/CC-BY 过滤，避开 NC/ND/SA。[Sketchfab 帮助](https://help.sketchfab.com/en/articles/16152215-crediting-users-for-3d-model-downloads)
- OpenGameArt：“Yes, you can use any of the art submitted to this site. Even in commercial projects.” 但许可是混合的；CC-BY-SA 要求 “you must distribute them under the same license”，GPL 对美术的适用也不清晰，建议整体避开。[OGA FAQ](https://opengameart.org/content/faq)
- CC-BY 3.0 还有“不得附加技术限制”的条款（OGA FAQ 写作 “distribute the work in a way that does not include DRM”）；VRChat 的编译 bundle 是否算技术措施存在解释空间，CC0 无此问题。保守做法：CC-BY 只用于不介意被提取的装饰件。
- itch.io：平台没有统一素材许可，各 pack 自定；ToS 只要求发布者 “warrant that they own or have the rights, licenses, permissions and consents necessary”。每个 pack 页的 License 必须读。[itch.io ToS](https://itch.io/docs/legal/terms)

### 2.3 音效

- Kenney Audio：Impact/Interface 等包页面均标 CC0；适合卡通冲击、UI、拾取。[Impact Sounds](https://kenney.nl/assets/impact-sounds)
- Freesound：CC0 / CC-BY / CC-BY-NC 三种；官方解释 “for the 'zero' license you can do pretty much what you want”，而 “'Noncommercial' works like attribution, but you can't earn any money”。只选 CC0，或 CC-BY 并署名；NC 一律排除。[Freesound FAQ](https://freesound.org/help/faq/)
- Sonniss GDC：“All of the sounds are royalty free and commercially usable”；“No attribution is required”。License v2.0 允许商用、修改、随游戏/交互项目分发，但不得作为音效库再分发，禁止 AI 训练。[Sonniss](https://sonniss.com/gameaudiogdc)、[License](https://sonniss.com/gdc-bundle-license/)
- Pixabay：可商用、无需署名（“Use Content without having to attribute the author”），但不得 “sell or distribute Content ... on a Standalone basis”；含可识别商标/人物的内容不能商用。[Pixabay License](https://pixabay.com/service/license-summary/)
- Zapsplat：免费用户 “You must provide clear credit to 'ZapSplat' wherever reasonably possible”；“Attribution is not required for Premium members”。可商用，但不得把音效作为库再分发。[Zapsplat License](https://www.zapsplat.com/license-type/standard-license/)
- BBC RemArc：只允许 “For non-commercial, personal or research purposes”；“for commercial purposes – to make a profit ... you'll need to get our permission first”。**不要进正式版**。[BBC 许可页](https://sound-effects.bbcrewind.co.uk/licensing)、[RemArc PDF](https://github.com/bbcarchdev/Remarc/blob/master/doc/2016.09.27_RemArc_Content%20licence_Terms%20of%20Use_final.pdf)
- 建议同时自制：滚动声（噪声+滤波+调制）、拾取（短促音高滑音）、UI、撞击（多层 thud）都能在 Audacity/Reaper/Blender 里合成，保留工程文件即可，完全自有。

### 2.4 VRChat 平台条款

- 上传即声明有权：“you are the creator and owner of that User Content, or have the necessary licenses”。第三方素材的许可必须覆盖 VRChat 世界的托管/展示/分发。[VRChat ToS §8.4](https://hello.vrchat.com/legal)
- 平台还要求你的内容不 “provide any attribution to any third parties”（§8.4(b)(iii)）。解释上，署名应由我们在世界内完成，而不是让 VRChat 承担；因此 CC-BY 素材必须有**世界内 credits**，不能只在外部网页写。[同上](https://hello.vrchat.com/legal)
- 你授予 VRChat 的是 “worldwide, non-exclusive, irrevocable, royalty-free, perpetual” 托管/展示许可（§8.2）；这与 CC0/CC-BY/Fab Standard 的“随 Project 分发”并不冲突，但再次说明素材许可必须允许平台分发。也正因 VRChat 要的是 irrevocable/perpetual：Sketchfab 的**付费 Standard 许可明确 revocable**，不适合作为世界素材；免费 CC 模型则不可撤销。[同上](https://hello.vrchat.com/legal)
- Creator Guidelines 没有逐条讲 IP，但明确 “You are responsible for your own content”；世界描述、预览、鸣谢都不能误导。[Creator Guidelines](https://hello.vrchat.com/creator-guidelines)
- 现实风险：VRChat 的 bundle 可被工具提取；Fab 要求“限制终端用户抽取”，我们只能靠不发布源文件 + 平台条款禁止反向工程/篡改，无法技术阻止。若你不能接受这个残余风险，Fab 资产应降级为“不用”。

## 3. 推荐优先级

**道具（野餐/玩具房）**：1) Kenney 3D（统一风格、CC0、模块化）→ 2) Quaternius 旧 CC0 包（逐包核验）→ 3) Poly Pizza CC-BY（补品类，逐模型署名）→ 4) 自制 Blender（异形/大件/需要精确碰撞）→ 5) Sketchfab CC0/CC-BY（逐模型审）→ 6) itch.io CC0 pack（逐包审）→ 7) Fab 免费（只在风格/性能合适且通过许可核验时）。

**纹理**：1) Kenney/自制色板与手绘纹理（最贴低多边形）→ 2) ambientCG CC0（木、布、金属，降分辨率+调色）→ 3) Poly Haven CC0（HDRI 做灯光参考/背景，不直接堆写实）→ 4) Megascans/Fab 写实纹理（仅局部，谨慎）。

**音效**：1) 自制/程序化 → 2) Kenney Audio CC0 → 3) Sonniss GDC（商用免署名）→ 4) Freesound CC0，其次 CC-BY（署名）→ 5) Pixabay（免署名）→ 6) Zapsplat 免费（须署名）→ 7) BBC RemArc 仅内部原型。

## 4. 署名与台账方案

单一事实源：`Docs/Research/AssetSources/credits.csv`（提案，尚未创建；建议列）：
`credit_id, asset_id, title, author, source_url, license, license_url, attribution_text, modified, used_in, evidence_url, checked_date, notes`。

- 世界内：出生点附近放 `CreditsBoard`（中文/英文两页），文本由 `credits.csv` 在编辑器阶段生成，避免手改两份。
- 世界描述：放一句“完整素材鸣谢见世界内 Credits 板 / 外部链接”，不替代世界内署名。
- 外部：同一份 CSV 生成网页版（GitHub Pages 或静态页），供审计与玩家查询。
- CC-BY 模板：`"<title>" by <author>, licensed <license>, via <source_url>; changes: <...>`；标题、作者、来源、许可、修改说明五项齐全。
- Fab：无需署名，但记录 `listing_url`、`order_id/claim_date`、`tier`（Personal/Professional）、`eula_version`；便于日后核对。
- CC0：可不署名，仍记录来源、下载日期、包 SHA-256；这是“证明不是盗用”的台账，不是许可要求。
- Sonniss/Zapsplat/Pixabay：记录许可版本与下载日期；Zapsplat 免费用户把 “ZapSplat” 写进 Credits。
- NC/SA/GPL：在 `credits.csv` 标 `commercial_ok=no` 或 `license_family=CC-BY-SA/GPL`，构建脚本直接拒绝打包。

在 [03_MAPS_ASSETS §7](../../03_MAPS_ASSETS.md) 的每份资源字段上建议增列：
`license_family, commercial_ok, attribution_required, attribution_text, bundle_ok, nc_or_other_restriction, evidence_url, checked_date, source_snapshot_path, modifications`。

## 5. 待你决定

1. **商业化形态**：首发是否完全免费、以后是否卖外观？只要有一丝付费可能，所有 NC 素材（Freesound NC、Sketchfab NC、OGA NC、BBC）全部排除。
2. **CC0-only 还是允许 CC-BY**：CC-BY 能把 Poly Pizza/Sketchfab 的品类拉满，但要做世界内 credits 与逐条台账；你接受吗？
3. **Fab 的风险容忍度**：EULA 要求“限制用户抽取”，而 VRChat bundle 可被提取；同时不能把含 Fab 资产的工程/模板发给别人。接受则 Fab 作为补充，否则不用。
4. **仓库是否公开**：若 GitHub 仓库转公开，Fab Standard 素材不能进公开仓库；CC-BY 原始文件可公开但必须带署名/许可，且不建议与代码同库；Sonniss/Zapsplat 原始音效不能作为音效库公开。需要私有资产库/子模块时尽早定。
5. **Quaternius 许可切换**：冻结 2026-10 已下载的 CC0 快照，还是接受新 QAL 重新下载？两者都可用，但台账写法不同。
6. **Poly Pizza/Sketchfab 的混合许可**：允许逐模型 CC-BY，还是只收 CC0？后者省事但可选面窄。
7. **写实纹理/HDRI 是否进入正式版**：Poly Haven/ambientCG/Megascans 与低多边形风格冲突；是否只做局部/背景？
8. **音效策略**：自制为主，还是接受 Zapsplat 的署名、Sonniss 的“禁止 AI 训练”条款？后者对我们无实际影响，但需记录。
9. **署名位置与语言**：世界内板子、世界描述、外部网页三处是否都做？中英双语还是中文优先？
10. **现有 Kenney/Quaternius 包的处置**：继续用当前包内 `LICENSE` 快照，还是按本文建议重新做包级验收后再进正式资源目录？
11. **是否用 Fab MCP/Agent API 找素材**：官方版仍是实验性；非官方版抓未公开接口，有 ToS/稳定性风险。只做“发现”，许可与包级验收仍人工做，可以吗？

## 6. 未核验与限制

- Fab EULA 全文未直接抓到（页面只渲染摘要）；§4c/§6 引文来自 Epic 官方论坛的 EULA 摘录，CC-BY-SA/GPL 混用限制来自 LICENSEWARE 转述，均标为二手，建议正式采用前让法务/你自己再读一遍 EULA。
- Poly Pizza 的具体许可分布（CC0 与 CC-BY 3.0 的比例）未统计；只能确认“逐模型标注、示例为 CC BY 3.0”。
- itch.io 没有平台级素材许可；本文只核验了 ToS 的发布者担保条款，未逐 pack 统计。
- OpenGameArt 的每种许可只核验了 FAQ 摘要，具体条目仍需看条目页。
- 音效的“风格契合”是主观判断；Sonniss/Pixabay 的写实素材进卡通世界通常要加滤波、变调、分层。
- 所有“可否随 VRChat 世界分发”的判断都是许可文本解释，不是 VRChat 或版权方的书面确认。
- 部分站点（Pixabay、Zapsplat、Sonniss、Poly Pizza、VRChat）有 Cloudflare 拦截；本文用官方页面的存档/阅读器读取，关键引文已与第二来源交叉核对。Pixabay 用 2025 存档，Zapsplat 用官方页面阅读器，Poly Pizza 用翻译代理读取示例模型页。
- Sonniss 许可 v2.0 自 2026-08-27 生效，早于此日期下载的包按当时版本；引用时以实际下载日的版本为准。
- Fab Agent API/MCP 仅看到官方论坛 Roadmap 帖（2026-11-09，status None），未取得 API/MCP 文档或实际调用；社区包仅读 npm README，未运行。

---
调研日期：2026-11-14。相关机器可读表：[sources.csv](sources.csv)。本文不授权下载、导入或发布；素材仍按 [license_gaps](../../AssetIntake/license_gaps.md) 的“候选、未验收”状态管理。
## 附录 A：首批免费素材入库（2026-11-14）

本节只记录**已下载并落盘**的一批道具素材，用于野餐草坪地图的 v0.1 拼装；许可结论仍以 §1 的矩阵和包内 `LICENSE` 为准，逐文件验收（尺寸、拓扑、材质、碰撞代理）仍未做。机器可读清单见 [intake_manifest.csv](intake_manifest.csv)。

- 落盘位置：Windows `D:\VRChatKatamariDanacy\Docs\AssetIntake\Downloads\<source>\<pack>\`（该路径已被 `.gitignore` 忽略，不进 Git）。每个包保留原始 zip 并就地解压，许可证据与解压内容放在同一目录（Kenney 为 `LICENCE-kenney-cc0.txt`，Poly Pizza 为 `LICENCE-EVIDENCE.txt`）。
- 获取方式：Kenney Furniture Kit / Marble Kit 与 5 个 Poly Pizza 单模型通过用户真实 Chrome（Playwright MCP）点击页面下载按钮取得；Food Kit / Nature Kit 复用 2026-10-10 的既有入库（[DirectDownload](../../AssetIntake/DirectDownload/routes.md)），只做校验与落盘。全过程未登录新账号、未改账号设置、未付款；poly.pizza 只关闭了一次 cookie 同意弹窗。
- 规模：9 个包/模型，压缩包合计 26.8 MiB，解压后约 100 MiB。
- 覆盖：`prop_catalog.csv` 54 项中 34 项有可用文件（食物/餐具/饮料 19 项来自 Kenney Food Kit，花、蘑菇、落叶、石、花盆、小盆栽来自 Nature Kit，凳/长凳来自 Furniture Kit，浇水壶/野餐篮/保温箱/保温壶/球来自 Poly Pizza）；20 项仍缺（`seed`、`acorn`、`bottle_cap`、`paper_scrap`、`napkin`、`picnic_blanket`、`ground_terrain`、`path_mesh`、`boundary_hedge`、`invisible_wall`、`core_hedge`、`flower_bed_frame`、`tree_root_mound`、`bank_pad`、`bank_beacon`、`spawn_marker`、`powerup_pedestal`、`zone_signpost`、`giant_picnic_table`、`giant_oak`），全部本来就是 catalog 里“model ourselves”的自建项或纯地形/玩法件。
- 待办风险：① Poly Pizza 里有 3 个模型是 **CC BY 3.0**（Picnic Basket“Poly by Google”、Thermos“Poly by Google”、beach ball“the_ normalgamer”），世界内必须有 credits 板；浇水壶（Isa Lousberg）与 Cooler Box（MilkAndBanana）是 CC0；② Kenney 包同时带 GLB，Unity 2022 不原生导入 glTF，导入请用 `Models/FBX format`；③ `Marble Kit` 只含轨道段、没有球体，未映射任何 catalog id，仅作备用；④ `pebble` 用的 `rock_smallA` 仍带 RENDER_MISMATCH 标记，需人工看图确认；⑤ Quaternius Ultimate Food Pack 页面仍标 CC0，但下载入口是 Google Drive 目录（非单一 zip），未取，留待下一批。

## 7. 三个新主题的来源方案（2026-11-15 追加）

本节是**追加**，不改上面 §0–§6 与附录 A 的任何结论；许可矩阵、Fab EULA 摘要、已入库记录仍以上文为准。细节证据在两份同日调研：[CC0 候选包](candidate_packs_2026-11-15.md)、[Fab 免费候选](fab_candidates_2026-11-15.md)。**所有候选仍是 candidate，没有一件通过验收，也没有一件被授权进入正式场景。**

### 7.1 分工原则

| 类别 | 走哪条 | 理由 |
|---|---|---|
| 体块、容器、家具、地形 | 程序化（`prop_lib`） | 尺寸/枢轴/材质槽要精确到米；第三方包几乎都要改标定 |
| 软、皱、细、毛（衣物、纸团、线材、毛絮、麦穗、粪球） | **必须程序化** | 没有任何 CC0 包覆盖；照片扫描件的写实度与低模冲突 |
| 树、灌木、石、蔬果、餐具、玩具、办公家具 | CC0 包（Kenney / Quaternius / Poly Haven / ambientCG） | 许可最干净、风格统一、已有 4 包入库 |
| 干草卷、稻草人、卡通昆虫、洗衣篮、书堆 | Fab 免费件（逐条核验） | 程序化性价比低；但 Standard 许可有限制，见 §7.4 |

### 7.2 按主题×类别的来源分配

| 主题 | 类别 | 首选来源 | 备选 | 必须自建 |
|---|---|---|---|---|
| 草甸 | 土/球 | — | — | 全部（粪球、土块、泥饼） |
| 草甸 | 草/秸 | Kenney Nature Kit（草簇、落叶） | Quaternius Ultimate Crops（CC0 页面） | 麦穗、谷粒、散草 |
| 草甸 | 石/矿 | Kenney Nature Kit、Poly Haven | Poly Pizza 单件（CC0） | 燧石、砂砾 |
| 草甸 | 农具/畜具 | OGA hay-bale（CC0）、Quaternius Farm Animals（CC0 页面） | Fab 干草卷 | 钉耙齿、羊铃、皮绳 |
| 草甸 | 容器/支架/结构 | Quaternius Farm Buildings（CC0 页面） | Fab 木栅栏 | 水槽、饲料槽、手推车、石墙 |
| 房间 | 衣物/织物 | — | — | 全部（袜子、T恤、衣堆、褶皱） |
| 房间 | 纸/文具/包装 | Kenney 现有包 | Poly Pizza CC0 单件 | 纸团、便利贴、快递箱 |
| 房间 | 餐具/食物 | Kenney Food Kit（已入库） | Quaternius Ultimate Food（CC0） | 外卖盒、麦片碗 |
| 房间 | 电子/线材 | — | — | 全部（线、耳机、手柄线） |
| 房间 | 玩具 | Kenney Toy Car Kit（未入库） | Kenney Marble Kit（无球体） | 积木、拼图 |
| 房间 | 家具 | Kenney Furniture Kit（已入库）、Quaternius Ultimate House Interior（CC0 页面） | Poly Haven `Shelf_01`（182 tri，CC0） | 暖气片、衣柜内部 |
| 野餐 | 小件缺口（0.03–0.35 m） | Kenney Food/Nature/Furniture（已入库） | Poly Pizza CC0：硬币、火柴盒、石子、树枝 | 糖包、太阳镜、纸垫 |

### 7.3 候选排序（每主题 Top 5，详细表见两份调研）

| 主题 | # | 候选 | 许可（页面） | 量级 | 判断 |
|---|---|---|---|---|---|
| 草甸 | A1 | Kenney Nature Kit | CC0 | 330 件，10.5 MB | **已入库**，底座 |
| 草甸 | A2 | Quaternius Ultimate Crops | CC0（页面） | 102 件，无贴图 | 菜园/果园，需开包核 LICENSE |
| 草甸 | A3 | Quaternius Farm Buildings | CC0（页面） | 13 件 | 棚屋/谷仓，t7 地标备选 |
| 草甸 | A4 | Quaternius Farm Animals | CC0（页面） | 7 件 | 羊毛/牧场，动物本体不进场景也可只取毛 |
| 草甸 | A5 | OGA hay-bale（carlosjorgereis） | CC0 | 1 件，346 KB | 干草卷，最小代价补缺 |
| 房间 | B1 | Quaternius Ultimate House Interior | CC0（页面） | 123 件，无贴图 | 家具底座，需逐件重定标 |
| 房间 | B2 | Kenney Furniture Kit | CC0 | 140 件，5.1 MB | **已入库** |
| 房间 | B3 | Poly Haven `Shelf_01` | CC0 | 1 件，182 tri | 书架，面数意外地低 |
| 房间 | B4 | OGA lowpoly-survival-item-pack | CC0 | ~12 件 | 电池/桶/杯/手电 |
| 房间 | B5 | Poly Pizza Tissue Box | CC0 | 1 件，102 tri | 纸巾盒 |
| 野餐 | C1 | Kenney Food Kit | CC0 | 200 件，4.6 MB | **已入库** |
| 野餐 | C2 | Quaternius Ultimate Food | CC0（页面） | 103 件 | 食物补充 |
| 野餐 | C3 | Poly Pizza Coin | CC0 | 206 tri | t0 硬币 |
| 野餐 | C4 | Poly Pizza Matchbox | CC0 | 396 tri | t0 火柴盒 |
| 野餐 | C5 | Poly Pizza Pebble / Twig | CC0 | 136 / 64 tri | t0 石子、树枝 |

### 7.4 Fab 免费件：页面复核结果（Lead 用 Playwright 读真实页面，2026-11-15）

- 27 条候选里 **26 条的 "License terms" 行读到了**：**10 条 Standard License、16 条 Creative Commons (CC-BY)**；1 条（ithappy 的 "Food FREE"）页面无许可行，维持 UNVERIFIED。
- 因此「Fab Standard 到底是哪一版」这个问题**在 listing 层面已能确认到 "Standard License"**；但 Standard 的具体限制（禁止终端用户抽取、分发物需 added value、禁止放进世界/关卡编辑工具）仍要读 [Fab EULA](https://www.fab.com/eula) 原文，§2.1 的二手摘录不能替代。
- **标题与许可不一致的实例**：[CC0 - Kitchen Knife](https://www.fab.com/listings/b2ead203-d418-464f-b032-891550a03da3) 标题写 CC0，页面 "License terms" 写 **Creative Commons (CC-BY)**。结论：**只认页面/包内许可，不认标题**。
- **价格不能从页面文字里猜**：listing 页同时渲染推荐位价格，¥ 金额会混进来。可靠的只有许可选择器里的 "From free to ¥X"（Personal 免费档）：Hay Bale、Rocks、Cardboard Box、Pen/Stapler 四条明确；`Cooler Box` 页面只见 ¥46.64、没有 "From free"，原调研表的 "$0/$0" **未成立**，降级为「价格未确认」。
- 本文**未登录、未领取、未下载、未加入购物车**；Fab 账号已登录这一事实来自页面头像与 "My Library"，没有改动任何设置。

### 7.5 每件素材的许可记录：位置与模板

**位置**（跟随既有 `DirectDownload` 的习惯，但把可提交的记录单独放）：

```
Docs/AssetIntake/Records/<source>_<pack>.md          # 可提交的记录（本模板）
Docs/AssetIntake/Records/<source>_<pack>_LICENSE.txt # 包内 LICENSE 原文，逐字拷贝
Docs/AssetIntake/Records/<source>_<pack>_files.csv   # 逐文件清单与哈希
Docs/AssetIntake/Downloads/<source>/<pack>/          # 原始包与解压（.gitignore，不进 Git）
```

一份记录至少包含：

| 字段 | 要求 |
|---|---|
| `asset_id` | 与 `catalogue.csv` 的 id 一致（或 `<pack>_<file>` 形式） |
| `source` / `source_url` | 原始页面 URL，不是聚合站 |
| `author` | 作者/工作室名，照页面 |
| `license_name` + `license_version` | 例 `CC0 1.0` / `Fab Standard` / `CC-BY 4.0`；**逐字照页面** |
| `license_url` | 许可正文链接 |
| `evidence` | 许可页/包内 LICENSE 的文件名 + SHA-256 + 读取日期 |
| `retrieval` | 下载日期、方式（页面下载 / 已入库复用）、压缩包 SHA-256 |
| `files` | 每个用到的文件：名、SHA-256、三角面、包围盒（米） |
| `modifications` | 改标定、换材质、减面、拆件的具体说明 |
| `attribution_text` | 世界内 credits 要写的原文（CC-BY 必填，CC0 写「无需署名」） |
| `review` | `candidate` / `accepted` / `rejected` + 审查人 + 日期 |

规则：**没有 `review=accepted` 记录的文件，一行都不许进 `Assets/` 或任何正式场景**；`candidate` 只能待在 `Downloads/`。

### 7.6 本主题新增的未验证项

1. **Fab Standard 是否允许本项目的分发形态**（VRChat 世界包含可提取的网格）——未定，是所有者决定，不是本文能回答的。
2. **Quaternius 包内 LICENSE**：站点 QAL v1.0（2026-08-28）与 82 个包页的 CC0 并存，**以包内 LICENSE 为准**；全部未开包。
3. **Poly Pizza 直连 403**：调研经翻译代理读取，许可链接仍指 CC0/CC-BY 正文，但「代理页 = 原页」这一点未逐条交叉验证。
4. **Kenney 三角面/包体积**：页面不标，只有已入库 4 包的数字；未入库的 Survival/Toy Car/Holiday 三包内容清单未知。
5. **ambientCG / Poly Haven 的贴图**：本项目不用贴图，只可能用它们的模型；写实材质需替换为纯色。
6. **同名冲突**：`Cooler Box` 在 Fab（Standard）与 Poly Pizza（CC0）各有一个，别混。
