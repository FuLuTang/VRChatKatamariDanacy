# 塊魂系列关卡地图：怎么做的、怎么摆的（公开来源核查）

**调研日期：2026-11-15（Asia/Shanghai）。** 回答「2025《ワンス・アポン・ア・塊魂 / Once Upon a KATAMARI》的关卡是怎么制作与布局的，旧作（塊魂 2004・みんな大好き塊魂・塊魂アンコール・Reroll）提供了什么可参照的结构」，供本项目区域地图设计取用。
**边界：没有实机、没有录像、没有数据挖掘、没有下载任何图片。** 结论全部来自公开文字；推断标 `[推断]`，官方未明说标 `unconfirmed`；每条附 URL。
**版权：** 原作的美术、模型、音乐、关卡与物件数据均受版权保护，**不得**进入本项目仓库或世界；本目录只作风格与结构参考，不复制资产、不转存图片。

---

## 0. 十条最强发现

| # | 发现 | 判定 |
|---|---|---|
| 1 | 2025 作用「时代」做关卡主轴：教程 + ムカシニホン／ムカシウナバラ／ムカシアメリカ／ムカシエジプト／ムカシヨーロッパ／オオムカシチキュウ／ズットムカシチキュウ／ハルカムカシチキュウ／サイキン | `confirmed`（T2 媒体汇总官方说明） |
| 2 | 每关＝一个尺寸带目标＋限时；同尺寸带内用「何 cm 能吸什么」划分路线 | `confirmed`（T2 制作人原话） |
| 3 | 关卡分「区块（map）」而不是一张整图——原作数据里每个尺寸带对应一张专用地图 | `strongly implied`（T3 数据挖掘，2007 年作品） |
| 4 | 关卡参数与物件摆放是分开的层：带 `u8LocScript_1` 布局脚本、`u8Proc`、类目 ID 的关卡参数表 | `strongly implied`（T3 数据挖掘） |
| 5 | 开局点常是一个极小的封闭空间（海盗船船尾的桌面），再逐级外扩 | `confirmed`（T2 先行体验） |
| 6 | 吸收特定物件会改变关卡状态／后半段分叉 | `confirmed`（T2 先行体验＋T2 制作人访谈） |
| 7 | 物的分层用「House／Town／World」三个尺度舞台＋80 个收藏类目组织 | `strongly implied`（T3 wiki，与 T1 收藏系统一致） |
| 8 | 「几乎全部都能吸」是有意的设计，但只限比球小的东西；撞到吸不动的会被弹开并掉体积 | `confirmed`（T3 wiki＋T2 先行体验，两源一致） |
| 9 | 官方对 2025 作没有逐关介绍页；最接近官方逐关资料的是 2025-12-22 发售的官方ムック（96 页，含ステージ与素敵コレクション） | `confirmed`（T1） |
| 10 | 公开的「每平方米物件数」「原始关卡文件格式」都不存在；能查到的最细粒度是「某关占该区物件总数的百分比」 | `confirmed`（检索结论，见 §7） |

---

## 1. 2025 作：《ワンス・アポン・ア・塊魂》

### 1.1 官方口径：时间是关卡的组织轴

官方站故事页把「絵巻→タイムスリップ→時代を飛び越えて転がす」写成主线 [S-M01](https://once-upon-a.katamaridamacy.jp/)：
> 「大コスモ絵巻に描かれている世界を目指して“王子シップ”を使い、タイムスリップし」

发行商新闻稿的表述更直接，把「从小的开始吸」写成核心动词 [S-M02](https://www.bandainamcoent.com/pt_br/news/once-upon-a-katamari-now-available-for-consoles-and-pc)：
> "a rolling ball that collects everything it touches **starting from smaller objects**"

开发方：Steam 商店与 Xbox 商店都把开发标为 **RENGAME**（发行 Bandai Namco）[S-M03](https://store.steampowered.com/app/1880620/) [S-M04](https://www.xbox.com/ja-JP/games/store/3/9NMM8ZQW774R/)。旧作复刻版（アンコール／Reroll）由 MONKEYCRAFT 开发 [S-M05](https://www.pcgamingwiki.com/wiki/Katamari_Damacy_Reroll)，ja.wikipedia 另记 RENGAME 参与复刻；**两家与「みんな大好き塊魂」开发公司的关系未确认** [S-M06](https://ja.wikipedia.org/wiki/%E5%A1%8A%E9%AD%82%E3%83%8E%E3%83%BB%E3%83%93%E3%80%9C%E3%82%BF)。

### 1.2 官方站有没有「ステージ」页

没有。官方站导航只有 トップ／ものがたり／キャラクター／あそびかた／今回の塊魂／製品一覧／スペシャル／購入 [S-M01]。`/stage/` 返回 403，无法确认是否为隐藏页，因此**不能**说「官方逐关页面存在」。逐关信息实际散落在：产品页的截图、DLC 页的课题名列表 [S-M07](https://once-upon-a.katamaridamacy.jp/products/dlc.html)、以及媒体汇总。

### 1.3 时代与课题清单（T2，媒体汇总官方说明）

電撃オンライン按时代列出课题类型 [S-M08](https://dengekionline.com/article/202610/89744)，这是目前最完整的公开关卡目录：

| 时代 | 课题举例（原文节选） |
|---|---|
| チュートリアル | チュートリアルマップ |
| ムカシニホン（江戸） | 長屋で転がす／天守閣を転がす／妖怪を集めて百鬼夜行／ウシクマ |
| ムカシウナバラ（大航海） | 船の上／島で転がす／海上レース |
| ムカシアメリカ（西部） | ウエスタン街でドリンクをいっぱい／鉱山でゴールドラッシュ |
| ムカシエジプト | ピラミッドでミイラをたくさん／砂漠に水を |
| ムカシヨーロッパ（古代ギリシャ） | 哲学者を急いで巻き込む／コロッセオで聖火台に火をつける |
| オオムカシチキュウ（古墳） | ハニワを作る／穴を掘る |
| ズットムカシチキュウ（氷河期） | ツララをたくさん／マンモスとぴったり |
| ハルカムカシチキュウ（ジュラ紀） | 古代生物をいっぱい／50 個だけ |
| サイキン（近代） | あまいものをたくさん／フライを作る |

**可直接抽出的「王様课题类型」六类**：①とにかく大きく（到尺寸）②いそいで大きく（限时到尺寸）③○をいっぱい（按类目计数）④50 個だけ（限类目定量）⑤ぴったり（贴近目标值，多也不行少也不行）⑥おかたづけ／作る（把某物清除或造出，如ハニワ・聖火台）。判定：`strongly implied`（T2 归类，官方未用这套分类法命名）。

### 1.4 关卡尺度的跨度（T2／T3）

- 先行体验的海盗船关：**从船尾桌面上起步，目标 1m80cm**，「巻き込めないモノに勢いよくぶつかると塊が弾かれ削れてしまう」[S-M09](https://www.famitsu.com/article/202509/52356)。
- 同一试玩会的「急いで大きく 4」：**起始球就有 15 m** [S-M10](https://www.4gamer.net/games/933/G093370/20250916021/)。
- 粉丝 wiki 的ムカシニホン一览表把每关写成「N 分で X cm/m」：4 分で 15cm／6 分で 50cm／10 分で 2m／13 分で 12m 等 [S-M11](https://seesaawiki.jp/katamaridamacy/d/%a5%b9%a5%c6%a1%bc%a5%b8%20%28%a5%ef%a5%f3%a5%b9%a1%a6%a5%a2%a5%dd%a5%f3%a1%a6%a5%a2%a1%a6%b2%f4%ba%b2%29)（**正文抓取被 HTML 转换挡住，数值取自检索快照，未逐条核对**）。

### 1.5 关卡内部结构：起步点、路障、分叉

- 起步点是**小而封闭的场所**（船尾桌面），先吸小物，再外扩 [S-M09]。
- **吸收「特定のモノ」会改变关卡样子**——Famitsu 标题即「特定のモノを巻き込むとステージの様子が変化する仕掛け」[S-M09]；Game Watch 的体验记进一步说明「プレーヤーが巻き込んだものによって、後半のステージが変わってくる」[S-M12](https://game.watch.impress.co.jp/docs/kikaku/2046729.html)。
- 古代ギリシャ关：吸一个哲学家就弹一句名言——**奖励密度靠「同类物件的反应」堆出来**，不是靠更多模型 [S-M12]。
- 道具（magnet／rocket／sonar／timer）是**改变可达范围**而非单纯加速：magnet 吸「现在尺寸能吸的东西」[S-M12] [S-M08]。这暗示关卡密度被设计成「手动吸会累、用道具才吃得下」。

### 1.6 官方逐关资料只有印刷品

『ワンス・アポン・ア・塊魂 イマとムカシにくわしいガイドブック』（KADOKAWA Game Linkage，2025-12-22，A4 変形 96 页＋メイツ図鑑 72 页）明确写「ステージやメイツ、素敵コレクションの情報もバッチリ掲載予定」[S-M13](https://store.kadokawa.co.jp/shop/g/g322510000187?ref=official) [S-M14](https://group.kadokawa.co.jp/information/promotional_topics/article-13368.html)。**判定：`confirmed`（存在与内容范围），但正文未公开，未读。**

---

## 2. 旧作：三作共同的关卡骨架

### 2.1 三层尺度舞台 ＋ 逐关尺寸阈值

Katamaripedia 的关卡页把每关写成一张卡：Area／Time／Goal／Super Clear／Collectables／Objects [S-M15](https://katamaripedia.miraheze.org/wiki/Katamari_Damacy/Make_a_Star_1)。以 Make a Star 1 为例：

| 字段 | 值 |
|---|---|
| Area | House（客厅，**本关只开放客厅一个 section**） |
| Goal | 10cm0mm；评级 10／12／14／15cm 四档 |
| 时间 | 首次无限制；PS2 3:00；Reroll 4:00 |
| 物件 | **本关只含客厅区 145 件中的 30 件（21%）** |

即：**同一块地图被不同关卡切出不同 section**，尺寸目标决定切多少 [S-M15]。这与 2025 作「同一时代多课题」是同一手法。

### 2.2 旧作的「尺寸阶梯」是空间化的

2004 作有 8 个「星をつくる」＋月＋9 个星座，舞台分 **家／街／世界** 三层 [S-M16](https://wiki3.jp/aniwotadata/page/43986)。续作把这条阶梯写得更明确（当时杂志评测文字）：
> “one of the early levels starts you out inside a house … before making your way up onto the kitchen table … big enough to make it over the lip of the sliding patio door”
并指出后期能吸埃菲尔铁塔 [S-M17](https://www.yumpu.com/en/document/view/7094486/worth-importing-defunct-games/50)。
判定：`strongly implied`（T3 转载的杂志文本，非官方原文）。

### 2.3 物件字典：1438 件／80 类

Katamaripedia 的收藏总表写明 **1438 objects, 80 categories**（蔬菜／水果／家具／运输／动物…），每件带「Size: 1m60cm1mm」这类**可吸尺寸**与「Location: 哪一关的哪个位置」[S-M18](https://katamaripedia.miraheze.org/wiki/Collection:Katamari_Damacy) [S-M19](https://katamaripedia.miraheze.org/wiki/Collection:We_Love_Katamari/School)。
- 这解释了原作「物件分组」的真相：**类目是收藏字典的索引，不是空间分区**；空间分区靠「哪一关／哪个 section」。
- We Love Katamari 的 School 一关就有 **63 件**同区物件 [S-M19]——单区物件量级可作密度参照。
- RTA 圈的「1438 objects」100% 类别与 wiki 数字一致 [S-M20](https://note.com/harutomorta/n/nc1af9f1a4155)，**两源互证**。

### 2.4 可吸 vs 固定：原作几乎是「全可吸」

- 规则侧：「塊に巻き込めるのは、その時点での塊よりも小さなものだけ」；开局挡路的东西变大后就能吸 [S-M16]。
- 占比侧：粉丝 wiki 的原作条目写「ステージに配置されたほぼ全てのものを塊に巻き込むことができる」[S-M16]。
- 撞到吸不动的会「弾かれ削れる」，即**固定物＝临时障碍＋惩罚源**，不是纯背景 [S-M09]。
判定：`strongly implied`（两源一致，无官方数值）。

### 2.5 复刻版证明了「摆放表可改」

Reroll 相对 PS2 版的具体差异（RTA 玩家整理）：星をつくる 1／2 的「テレビダイ」随机刷新取消；星をつくる 7 的「4ダンメ」从水中改到警察署；相机被遮挡时可透视 [S-M20]。
- **读法：**「随机刷新点」「某物件的坐标」都是关卡数据里的字段，且复刻时可以搬动——这是「原作关卡=可编辑摆放表」的间接证据。
- 另有著名事故：2004 初版把「4ダンメ」误放到地面以下，导致收藏 100% 不可能，只好紧急出修正版 [S-M16]——**物件坐标是手摆的，会摆错**。

---

## 3. 开发侧公开物（制作方法的第一手材料）

| 材料 | 说了什么 | 层级 |
|---|---|---|
| Game Developer 2004 事后剖析《Postmortem: The singular design of Namco's Katamari Damacy》 | 「What went wrong」两节直指尺寸感：**Tipping the scale**——「when you become big, you instantly forget about when you were small」，回访旧图也不觉得变大；**Falling apart**——曾想做「球会缩小崩解」的机制 [S-M21](https://www.gamedeveloper.com/design/postmortem-the-singular-design-of-namco-s-katamari-damacy-2004-) | 一次（开发者署名文章），但站点正文被 JS 外壳挡住，只读到检索片段 |
| 2025 制作人石田遼专访（4Gamer，2025-09-20） | 关卡做法是**先分析旧作**：「過去作のステージを分析しつつ制作を進めました。**何cm帯の大きさで巻き込めるのはこのルート**，このステージの形だとこんなルートも試せるんじゃないか」；并称与「『みんな大好き塊魂』を作った会社さん」合作，ステージの作り方のノウハウ有传承 [S-M10] | 一次（制作人口述） |
| 同专访的「不做的事」 | 「入れるとケンカになる」类目被排除；选物标准是「時代や場所を聞いて誰もが思い浮かべられる」＋「作るのが難しすぎない」[S-M10] | 一次 |
| GDC 2005 高橋慶太讲演（Game Watch 报道，2005-03-13） | 讲演存在，标题为「世界平和」相关；**正文未抓到**，细节不引用 [S-M22](https://game.watch.impress.co.jp/docs/20050313/gdc_kd.htm) | 报道存在，内容未核 |
| 高橋慶太访谈（2009，GameFAQs 转载） | 「The stages increased in size [as the game went on], something I didn't like.」——**原作者本人不喜欢「关卡越做越大」** [S-M23](https://gamefaqs.gamespot.com/boards/942166-noby-noby-boy/48798276?validate=1) | T3 转载，**原始出处未核**，只作旁证 |
| CEDEC | 2024／2026 各有一场**音频**讲座（素敵ソング），2026 那场讲义 143 页 [S-M24](https://gamemakers.jp/article/2024_10_09_79085/) [S-M25](http://mp.weixin.qq.com/s?__biz=MzYyNDUyMTcxNA==&mid=2247487108&idx=1&sn=ab6ca4fe973fd2231f74feb19f2ce60a)。**未检索到 CEDEC／GDC 的塊魂关卡设计讲座** | 缺席结论 |

---

## 4. 视觉参考（只记 URL 与内容，不下载、不转存）

| 参考 | 展示什么 | 备注 |
|---|---|---|
| 官方站产品页与ムービー栏 [S-M01] | 各时代截图、宣传片；无俯视图 | T1，仅宣传用 |
| 電撃オンライン总まとめ [S-M08] | 每个时代多张实机截图（含 UI 与尺寸显示） | T2，逐关外观最全的免费来源 |
| Game Watch 体验记 [S-M12] | 海盗船／古代罗马实机图 | T2 |
| Famitsu 先行评测 [S-M09] | 海盗船关实际游玩图 | T2 |
| Katamaripedia 关卡页内嵌 Fig.1–7 [S-M15] | **同一关的多角度落脚点照片**，可用于反推 section 边界 | T3 |
| 素敵コレクション 物件图鉴 [S-M18] [S-M19] | 每件物品的缩略图＋尺寸＋出现位置 | T3，**逐件定位，最适合做「主题簇」对照** |
| h1g 攻略 wiki [S-M26](https://h1g.jp/onceupona_katamaridamacy/) | 2025 作逐关攻略页 | T3，页面结构在检索快照中可见，未逐页读 |
| RTA 路线表（Google Sheets）[S-M27](https://docs.google.com/spreadsheets/d/1c8KWPhAsYNAzDNRmjUyj27HiBqIP9OWXVu8wCx4tVXA/edit) | 旧作 Any% 路线与尺寸节点 | T3，**粉丝自制，非官方地图** |

**没有任何官方或高质量的「俯视关卡平面图」公开。** 检索到的「地图」只有：官方宣传截图、wiki 的落脚点照片、RTA 路线表。判定：`confirmed`（检索结论）。

---

## 5. 数据挖掘／文件格式（只记录存在与 URL）

**版权前置：** 以下全部是**对原作文件的逆向**，其数据、模型、格式说明均属原作版权范围，**只能读、不能把资产或提取物带进本项目**。

| 成果 | 内容 | 层级 |
|---|---|---|
| TCRF《Beautiful Katamari/Unused Levels》（镜像） | 关卡参数集中在 **d04.dat**；每关带 `u8LocScript_1`（布局脚本）、`u8Proc`、`u8Enabled`、类目 ID；关卡按尺寸带拆成专用地图：**Usuigu 50cm-3m／Town 20cm-50cm／3m-12m／3km-10000km／Cosmos**；「the King stops giving out memory requests at 60 meters」；空关加载 test map；Biggy Battle 五张图「load proper maps, albeit without any objects」 | T3，翻页与镜像 **[S-M28](https://tcrf.tiago.zip/wiki/Beautiful_Katamari-2fUnused_Levels)**（原文 tcrf.net 被 Cloudflare 拦，镜像同许可） |
| VG Resource 论坛帖 | Beautiful Katamari 有 d02–d05.dat，d04 为「easily editable stage data」；文件内出现开发路径 `F:\projects\KDX\`；Katamari Forever 可用 wangan_midnight.bms 解包 | T3，**[S-M29](https://www.vg-resource.com/printthread.php?tid=43094)**（Cloudflare 403，仅检索快照可读） |
| Reroll 练习 MOD（GitHub） | `misoelegant/rpm`：PMUpdater＋`il-practice-mod`＋`1438-rba-mod`，可装/卸回 Original | T3，**[S-M30](https://github.com/misoelegant/rpm)** |
| Reroll 的 Unity 结构 | 游戏资源在 `WLKRR_Data/StreamingAssets/aa/StandaloneWindows64`（Addressables）；社区称内部有 `PS2KatamariSimulation` DLL，即**在 Unity 里跑 PS2 逻辑**，因此「换资产容易、加关卡难」；并称 OUAK／WLKR 比 KDR 更好 patch | T3，**[S-M31](https://steamcommunity.com/app/848350/discussions/0/750542551166048885/)** |
| 公开化的程序集 | NuGet「The0x539」发布 Reroll 的 publicized UnityEngine＋游戏程序集，供编译 MOD | T3，**[S-M32](https://www.nuget.org/profiles/The0x539)** |
| 未找到 | **没有任何公开文档描述「关卡文件格式」或「物件摆放表 schema」**；没有 2025 作的 datamine | `confirmed`（检索结论） |

**结论：** 原作关卡的真实架构（至少 2007 年那一代）＝**关卡参数表（每关一条：类目、布局脚本、proc 类型）＋按尺寸带切分的多张地图＋物件摆放层**。这正好解释了为什么「同一地点大球小球都能玩」而不需要一张巨型无缝图 [S-M28]。

---

## 6. 综合：区域地图应该遵循什么（每条给来源或标推断）

| # | 原则 | 依据 | 判定 |
|---|---|---|---|
| P1 | **同心尺寸带**：以球径分 3–4 带，每带一条主环路；每带的可吸物尺寸上限由上一带决定 | 制作人「何cm帯で巻き込めるのはこのルート」[S-M10]；原作按尺寸带切图 [S-M28]；逐关 Goal 尺寸 [S-M15] | `confirmed`（做法）／具体米数 `[推断]` |
| P2 | **按主题聚簇，而不是按尺寸随机撒**：一个区一个可命名的生活场景（厨房／校舍／神社），区内物件尺寸可以跨两带，但主题必须一致 | 2025 以时代成区 [S-M08]；旧作 House/Town/World [S-M16]；收藏类目即主题族 [S-M18]；制作人「誰もが思い浮かべられる品物」[S-M10] | `strongly implied` |
| P3 | **密度要按区核算，不按全图**：先定「本区物件总数」，再除以区面积；原作可参照量级是**单区 145 件、单关取其中 30 件（21%）**、校舍单区 63 件 | [S-M15] [S-M19] | 数字 `confirmed`；**每 m² 数值 `unconfirmed`**（无公开面积） |
| P4 | **一个区至少一个远景地标 + 一条不被遮挡的主视线**：大件固定物既当地标也当「暂时吸不动」的墙 | 原创作者承认「变大后忘记自己曾小」[S-M21]；Reroll 专门做「遮挡物透视」[S-M20]，说明遮挡是真实痛点；`[推断]`：地标应可从小球区看见 | `[推断]`＋旁证 |
| P5 | **尺寸门＝咽喉，且必须「可看见、可预期」**：门后是高价值区，门前是低阶补给；开门方式优先「把挡路物吸掉」，而不是加锁 | 原作「开局挡路的东西变大就能吸」[S-M16]；2025 开局小空间外扩 [S-M09]；本项目已有 size_gates 机制 | `strongly implied` |
| P6 | **可吸：固定 的比例要显式设计**。原作接近「全可吸」，本项目因网络与性能必须下调——固定物只保留边界、地标、承重结构 | 原作「ほぼ全てを巻き込むことができる」[S-M16]；撞不动的会削球 [S-M09]；数据层「地图与物件分离」[S-M28] | 原作 `strongly implied`；本项目比例 `[推断]` |
| P7 | **边界要「有理由的墙」**：把地图切块，块外即出界；出界点必须看得见，否则等于隐形墙 | TCRF 的「spawn out of bounds」「dedicated map」只证明有出界判定，不证明没有隐形墙 [S-M28]；本项目 03 §5「大物件被卷后仍有导航地标」 | `[推断]` |
| P8 | **高差只做两类**：可走坡（缓）与「球够大才翻得上去」的台阶；不做跳跃 | 原作只确认「只能吸比球小的」，**能否翻越高差无公开说明** [S-M16]；本项目 `max_step_height_over_ball_diameter` 自定 | 原作 `unconfirmed`；本项目自定 |
| P9 | **成长要能被「回访」兑现**：让大球回旧区时有新东西可吸，或让旧区成为捷径 | 原创作者批评「变大后旧区失去意义」[S-M21]；本项目 03 §5「成长解锁的是收集与路径选择」 | `[推断]`（对策），问题本身 `confirmed` |
| P10 | **单关只切一刀**：同地点用「开放哪些 section」区分难度，而不是每次重做一张图 | Make a Star 1 只开放客厅、只取 145 件中的 30 件 [S-M15]；2025 同代多课题 [S-M08] | `confirmed`（做法） |

**给本项目的三条硬约束（与 03_MAPS_ASSETS 对齐）：** ①P3 的密度必须写进 `prop_catalog.csv` 并在校验器里对「区面积×密度≈实例数」；②P5 的门必须同时是「视觉地标」，否则玩家只感到被挡；③P6 的固定物清单要单独一列，不能混在可卷物里。

---

## 7. 明确未确认（不要当事实用）

1. **2025 作每关的确切数量、每关区域数与面积**：`unconfirmed`。wiki 仍在补，官方只给时代。
2. **2025 作的物件总数、每区物件数**：`unconfirmed`。官方未公布，收藏系统数据未被公开整理。
3. **「50cm 路障」「25cm 才能进船长室」这类具体门槛**：未在可读正文中核实，`unconfirmed`，不作为数值来源。
4. **原作关卡文件格式与物件摆放表 schema**：无公开文档；TCRF 只给字段名（`u8LocScript_1` 等），不给类型定义。
5. **「球会缩小崩解」是否实现过**：事后剖析说「曾想加入」[S-M21]，最终版本未见，`unconfirmed`。
6. **高橋 2009 那句「不喜欢关卡越来越大」的原始出处**：只有 GameFAQs 转载，`unconfirmed`。
7. **每平方米物件密度**：全球公开资料均无面积数据，只能给「单区件数」；换算需本项目自己实测灰盒。

---

## 8. 来源表

| ID | URL | 层级 | 用途 |
|---|---|---|---|
| S-M01 | https://once-upon-a.katamaridamacy.jp/ | 1 | 官方站故事／导航／发售信息 |
| S-M02 | https://www.bandainamcoent.com/pt_br/news/once-upon-a-katamari-now-available-for-consoles-and-pc | 1 | 发行商新闻稿：开发方、从小的开始吸 |
| S-M03 | https://store.steampowered.com/app/1880620/ | 1 | Steam 商店：开发 RENGAME、发售日 |
| S-M04 | https://www.xbox.com/ja-JP/games/store/3/9NMM8ZQW774R/ | 1 | Xbox 商店：開発元 RENGAME |
| S-M05 | https://www.pcgamingwiki.com/wiki/Katamari_Damacy_Reroll | 3 | Reroll 开发 Monkeycraft／Unity 2018 |
| S-M06 | https://ja.wikipedia.org/wiki/%E5%A1%8A%E9%AD%82_%E3%83%8E%E3%83%BB%E3%83%93%E3%80%9C%E3%82%BF | 3 | 系列沿革、复刻开发方记载 |
| S-M07 | https://once-upon-a.katamaridamacy.jp/products/dlc.html | 1 | DLC 课题名、选项「見下ろし視点」（アンコール 相机） |
| S-M08 | https://dengekionline.com/article/202610/89744 | 2 | 时代／课题／道具总まとめ（最全公开目录） |
| S-M09 | https://www.famitsu.com/article/202509/52356 | 2 | 海盗船关先行体验：起点、1m80cm、弹开削球、特定物改变关卡 |
| S-M10 | https://www.4gamer.net/games/933/G093370/20250916021/ | 2 | 制作人石田遼专访：按 cm 带分析旧作、路线的做法、选物标准、15m 起始关 |
| S-M11 | https://seesaawiki.jp/katamaridamacy/d/%a5%b9%a5%c6%a1%bc%a5%b8%20%28%a5%ef%a5%f3%a5%b9%a1%a6%a5%a2%a5%dd%a5%f3%a1%a6%a5%a2%a1%a6%b2%f4%ba%b2%29 | 3 | 2025 作逐关表（条件／尺寸／プレゼント）——抓取受限，数值取自快照 |
| S-M12 | https://game.watch.impress.co.jp/docs/kikaku/2046729.html | 2 | 先行体验＋专访：时代是系列首次、分叉、名言演出、magnet/timer |
| S-M13 | https://store.kadokawa.co.jp/shop/g/g322510000187?ref=official | 1 | 官方ムック：含ステージ与素敵コレクション信息 |
| S-M14 | https://group.kadokawa.co.jp/information/promotional_topics/article-13368.html | 1 | 官方ムック发行公告（96 页） |
| S-M15 | https://katamaripedia.miraheze.org/wiki/Katamari_Damacy/Make_a_Star_1 | 3 | 关卡卡结构：Area／Goal／评级／145 件中的 30 件／只开客厅 |
| S-M16 | https://wiki3.jp/aniwotadata/page/43986 | 3 | 2004 作系统与舞台分层、可吸规则、4ダンメ 事故 |
| S-M17 | https://www.yumpu.com/en/document/view/7094486/worth-importing-defunct-games/50 | 3 | 续作「房内→餐桌→门框→城市」的尺寸阶梯（杂志转载） |
| S-M18 | https://katamaripedia.miraheze.org/wiki/Collection:Katamari_Damacy | 3 | 1438 件／80 类收藏总表、逐件尺寸与位置 |
| S-M19 | https://katamaripedia.miraheze.org/wiki/Collection:We_Love_Katamari/School | 3 | 单区 63 件；Size＋Location 字段样例 |
| S-M20 | https://note.com/harutomorta/n/nc1af9f1a4155 | 3 | RTA 视角：1438 objects、PS2↔Reroll 摆放差异、练习 MOD |
| S-M21 | https://www.gamedeveloper.com/design/postmortem-the-singular-design-of-namco-s-katamari-damacy-2004- | 1 | 开发者事后剖析：尺寸感问题、缩球构想（正文仅读到片段） |
| S-M22 | https://game.watch.impress.co.jp/docs/20050313/gdc_kd.htm | 2 | GDC 2005 高橋讲演报道存在（正文未抓到，细节不引用） |
| S-M23 | https://gamefaqs.gamespot.com/boards/942166-noby-noby-boy/48798276 | 3 | 「不喜欢关卡越来越大」转载（原始出处未核） |
| S-M24 | https://gamemakers.jp/article/2024_10_09_79085/ | 2 | CEDEC2024 音频讲座报道（无关卡设计） |
| S-M25 | http://mp.weixin.qq.com/s?__biz=MzYyNDUyMTcxNA==&mid=2247487108&idx=1&sn=ab6ca4fe973fd2231f74feb19f2ce60a | 3 | CEDEC2026 音频讲座笔记（无关卡设计） |
| S-M26 | https://h1g.jp/onceupona_katamaridamacy/ | 3 | 2025 作攻略 wiki 入口 |
| S-M27 | https://docs.google.com/spreadsheets/d/1c8KWPhAsYNAzDNRmjUyj27HiBqIP9OWXVu8wCx4tVXA/edit | 3 | 旧作 Any% 路线表（粉丝） |
| S-M28 | https://tcrf.tiago.zip/wiki/Beautiful_Katamari-2fUnused_Levels | 3 | 关卡参数表字段、按尺寸带切图、出界与空关（TCRF 镜像） |
| S-M29 | https://www.vg-resource.com/printthread.php?tid=43094 | 3 | d04.dat「stage data」、开发路径、Forever 解包（仅快照） |
| S-M30 | https://github.com/misoelegant/rpm | 3 | Reroll 练习 MOD 工具链 |
| S-M31 | https://steamcommunity.com/app/848350/discussions/0/750542551166048885/ | 3 | Unity＋PS2 模拟层、OUAK 更易 patch（社区说法） |
| S-M32 | https://www.nuget.org/profiles/The0x539 | 3 | Reroll publicized 程序集（MOD 编译用） |

引用 URL 仅用于核查，不代表版权、品牌授权或对原作实现的背书。未运行游戏、未录制实机、未测量任何数值、未下载任何图片或资产。后续若要把 §6 的 `[推断]` 升级为 `confirmed`，最小成本是：①用官方ムック补逐关数据；②对 2025 作录一段海盗船关全流程，量「从桌面到甲板的解锁时刻」；③在灰盒里实测本项目的物件密度并与 P3 的 145／30／63 量级对照。
