# P14 名称候选公开检索记录

**采样日期：2026-10-11 UTC。** 使用免费公开 `web_search` 与少量 `web_fetch` 页面读取。未用付费 Treg endpoint、账号、API key、安装或登录；没有请求商标数据库/法律服务。搜索结果是索引快照，地区、语言、时间与引擎覆盖均有限。

## 候选名精确检索

下表记录公开 web_search 的实际查询形式与可复现查询链接。搜索组合包含候选完整短语 + `game OR VRChat`，并对候选名做了精确短语检索；另对部分名称用 `site:vrchat.com/home/world` 限定检索。精确短语结果在检索日没有返回对应同名游戏/VRChat结果。搜索引擎没有提供零结果的结果页面URL，因此 `queryURL` 是可重跑的 Google 查询链接，不是已抓取来源，也不是“全网无同名”的证明。

| 候选 | Query / query URL | 检索日与结果 | 歧义和审查状态 |
|---|---|---|---|
| Mosskip Circuit | `"Mosskip Circuit" game OR VRChat`; VRChat world query `"Mosskip"`。 [重跑候选词查询](https://www.google.com/search?q=%22Mosskip+Circuit%22+game+OR+VRChat) | 2026-10-11；未见精确同名结果。合并检索另出现 VRChat 世界目录中的“The Moss Pit”，只是共享 Moss 词根，非同名。 | 通用自然词根有近似风险；中文、日文读音/语感均未审。 |
| Sprigspin Scramble | `"Sprigspin Scramble" game OR VRChat`; VRChat world query `"Sprigspin"`。 [重跑候选词查询](https://www.google.com/search?q=%22Sprigspin+Scramble%22+game+OR+VRChat) | 2026-10-11；未见精确同名结果。结果集合里有无关的 Scramble 教学 PDF，不是游戏名称匹配。 | Scramble 词根通用；复合造词难读风险；未做母语审校。 |
| Nibblenook Rally | `"Nibblenook Rally" game OR VRChat`; `site:vrchat.com/home/world "Nibblenook Rally"`。 [重跑候选词查询](https://www.google.com/search?q=%22Nibblenook+Rally%22+game+OR+VRChat) | 2026-10-11；未见精确同名结果，VRChat 限域检索未返回结果。 | Nibble 有“啃咬”联想，Rally 通用；跨语种含义未审。 |
| Pebblepetal Derby | `"Pebblepetal Derby" game OR VRChat`; `site:vrchat.com/home/world "Pebblepetal Derby"`。 [重跑候选词查询](https://www.google.com/search?q=%22Pebblepetal+Derby%22+game+OR+VRChat) | 2026-10-11；未见精确同名结果，VRChat 限域检索未返回结果。 | Derby 通用、复合词可能难读；未审相近商标/产品。 |
| Beetlebloom Rollcall | `"Beetlebloom Rollcall" game OR VRChat`; `site:vrchat.com/home/world "Beetlebloom Rollcall"`。 [重跑候选词查询](https://www.google.com/search?q=%22Beetlebloom+Rollcall%22+game+OR+VRChat) | 2026-10-11；未见精确同名结果，VRChat 限域检索未返回结果。 | Beetle/Bloom 常见词且虫主题接近已有作品；辨识、长度和读音风险偏高。 |

候选及其拟译/读音、逐项检索记录亦见 [`names.csv`](names.csv)。该CSV的中文和日文名称只是内部草案，不是经翻译或用户选择的正式副标题。

## 检索中发现的相邻主题结果（不是候选名同名结论）

- [DungParty — Quest Store DB](https://queststoredb.com/game/dungparty-24082056851403511/)（抓取日期 2026-10-11，HTTP 200）：页面描述一款多人甲虫主题游戏，玩家推动/投掷粪球，强调甲虫、滚大球与虫类派对。它与本项目的“独立甲虫/滚球竞赛”题材有**概念邻近**，但不是上述五个候选名的同名命中，也不证明法律冲突。应避免仿用其具体命名、角色、画面表达或“DungParty”词组；本项目已经选择不写实、非粪便专属的日常小物滚球主题，仍需人工比较视觉区分度。
- [burrowball.io — Website Launches](https://websitelaunches.com/site/burrowball.io)（抓取日期 2026-10-11，HTTP 200）：页面称有一个名为 Burrowball 的草原奇幻互动竞技平台及数字收藏物。不是当前候选名匹配；仅说明该词已在近邻娱乐/游戏语境出现。因此不建议复活早期内部 brainstorm 的 **Burrowball Brawl**，当前五项不含此词。
- [絶対王者！むしバトル Bug Battle — PlanetVRC](https://planetvrchat.net/archives/32308?display_mode=monthly)（抓取日期 2026-10-11，HTTP 200，但页面文本截断）：标题显示这是 VRChat 世界目录中的虫主题世界。可确认主题邻近，不足以确认玩法或名称权利。
- [Bug Battle — VRCW world listing](https://www.vrcw.net/world/detail/wrld_98a758d4-7e1c-4ebd-aa43-3f55b12e1f13)（抓取日期 2026-10-11，HTTP 403）：未获得可读页面内容；仅记录该查询URL与访问被拒，不能据此验证或否定信息。

## 限定与下一步

- **没有做全面商标、公司名、应用商店、社交账号、域名、日文/中文地区商业名或专利检索；没有法律清权。** 本记录不是法律意见、可注册性判断或可商用保证。搜索无结果不代表没有先用、未登记或权利无冲突。
- 可用性还取决于各平台命名规则、标识近似、地域与商品/服务类别；正式选择前应由用户挑选方向、母语者审校，再进行人工品牌/法律检索。
- 对外命名不得把内部代号“VRC塊魂”或参考作品名当作产品品牌；仅内部文档可为讨论参考作品而提及该名称。当前范围要求原创滚球/甲虫主题，不用王子、国王、官方角色、标志、音乐或专属美术。
