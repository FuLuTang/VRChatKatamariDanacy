# 原作「推墙即爬」跨系列公开资料核查（初代 / REROLL / We Love / Beautiful / Forever / ノ・ビ～タ / Once Upon）

**调研日期：2026-11-16 UTC（项目时区 Asia/Shanghai）。** 本文是**文献核查**，不是录像量测：
用各作攻略、手册转述、官方页与直接讨论机制的帖子，把「贴墙爬升」这条机制的**存在性、操作条件、
上限、时长**逐条钉到来源上，并明确哪些数字**至今没有任何来源**。

**结论先说**

1. **机制存在，且跨作都在**：推着墙滚就能沿竖墙往上爬。REROLL 有明确的操作描述（慢贴、别弹开、再往前推）；
   We Love Katamari 的手册（经维基转述）直接说「rolling against a wall allows the katamari to "climb" it」。
2. **梯子/绳是另一套**：墙上有梯子（或台阶/绳）时可以**无限爬**；这是**多个独立来源一致**的说法。
   传说里「初代能爬半分钟上工厂烟囱」的那个烟囱，**日文攻略两处都写明烟囱上有ハシゴ（梯子）**——
   所以它是**梯子案例，不是普通墙案例**。这一条直接推翻了「初代普通墙能无限爬」的主要论据。
3. **初代普通墙有没有上限：仍然未知。** 唯一正面主张（初代可无限爬）的作者在同一帖里自认「十多年没玩、
   只是印象」；反方（封顶 ≈2 倍球高）说的是**续作/新作**，同样无来源。
4. **没有任何来源给出初代/REROLL 的爬升上限倍数、爬升速度、到顶时长。** 只出现过「1–2 倍球高」
   （续作/新作、社区、帖内被反驳）与「约 3 秒后被弹开」（新作、玩家观察）。
   ⇒ 设计里 `k_h = 1.5` / `T_climb = 4.5 s` **落在传言区间内不等于被证实**，两者仍是我们自己的暂定值。

---

## 1. 证据分级与本文口径

| 级 | 含义 | 例 |
|---|---|---|
| **A 官方** | 厂商页面、手册、官方补丁说明 | Bandai 的 PS2 作品页；OUAK 1.1.0 补丁说明 |
| **B 手册转述** | 维基等以手册为引用源的转述（未见手册原文） | 维基 We Love Katamari 的 gameplay 段（`cite_note-Manual`） |
| **C 攻略/媒体** | 攻略站、UP 主/博客、游戏媒体报道 | psmk2、wazap、der3 的蟹座 100% 攻略、Gamer.ne.jp |
| **D 社区帖** | Steam/论坛玩家描述，可能有记忆偏差 | Steam REROLL/OUAK 讨论串 |
| **E 摘要** | 只能拿到搜索引擎摘要（站点 Cloudflare 403，未直接 fetch） | TV Tropes 的 Katamari 条目 |

引用一律**只摘一句原文**（版权）。凡「未知」就写未知，不拿区间中值冒充测量值。

---

## 2. 逐作核查表（存在性 + 操作条件）

| # | 作品（年） | 原文（≤1 句） | 来源 / 级别 | 判读 |
|---|---|---|---|---|
| S1 | **REROLL**（2018） | "You can't jump but you can cimb some **short walls**. Approach the wall **slowly** so that you touch it without bumping away, and then **move forward** to roll up the vertical wall." | [Steam REROLL 讨论「Can you jump with ball?」2018-12-14, Demodulated](https://steamcommunity.com/app/848350/discussions/0/3561682879997803952/) / **D** | 机制存在；**操作三要素**：慢接近、不弹开、持续前推。注意他说的是 **short walls**（矮墙）——作者已给出「不是所有墙都能爬」的暗示，但没给比例 |
| S2 | REROLL | "there are a few **ladders** here and there you can climb as Demodulated has described" | 同上串 2018-12-15, stray_pengo / **D** | 梯子 = 另一种可爬物 |
| S3 | REROLL | "items only fly off whenever there's multiple attempts at either rolling on something too big or is **too steep a wall to climb**" | [Steam REROLL「Do items fly off more easily…」2018-12-11, Lenky Lad](https://steamcommunity.com/app/848350/discussions/0/1741102632991672789/) / **D** | 存在**「太陡 ⇒ 爬不上去」**的坡/壁阈值（未给角度） |
| S4 | REROLL | "You had items fall off for doing simple things like **just trying to climb**." | 同上串 2018-12-14, UNBREAKABLE / **D** | 尝试爬 = 碰撞 = 掉件（对应本作「脆」的观感） |
| S5 | **初代 2004** | "None of the tutorials or hints in the first game let you know that the katamari can climb walls… if a wall has a **ladder or steps** on it, you can climb it **indefinitely**" | [TV Tropes 条目（搜索摘要，2025-10-28）](https://tvtropes.org/pmwiki/pmwiki.php/Sandbox/KatamariGames) / **E** | 初代**没有任何教学**教你爬墙（所以「原作没这功能」的印象很常见）；梯子/台阶可无限爬；同段还说「碰墙即算碰撞」 |
| S6 | **We Love**（2005） | "Rolling against a wall allows the player katamari to **\"climb\"** it in order to reach higher areas." | [维基 We Love Katamari, Gameplay 段，注 3 = 手册](https://en.wikipedia.org/wiki/We_Love_Katamari) / **B** | 手册级来源确认「贴墙→爬→到高处」；**注意：这句里没有任何尺寸比例** |
| S7 | **We Love / Forever** | "you are able to roll, or climb, up the wall if you held **both the analog sticks forward**… In… Katamari Forever… the tutorial, after learning all the basic controls, you have to hold both joysticks forward the climb up a wall." | [Unreal Engine 论坛 2016-07-06](https://forums.unrealengine.com/t/help-with-remaking-katamari-damacy-in-hd/68356) / **D** | 双摇杆前推 = 推墙；**Forever 的教程强制你爬一次墙**（说明是官方承认的正式机制，不是隐藏技巧） |
| S8 | **ノ・ビ～タ**（Vita, 2011） | 「乗り越えたい壁に向かって**3回ほどフリック**すれば、壁を越えていくことができる」 | [Gamer.ne.jp 2011-10-27](https://www.gamer.ne.jp/news/201110270001/) / **C** | Vita 版把「越墙」做成独立动作（3 次甩动）；同一机制在不同作上换了输入 |
| S9 | **Forever**（2009） | "the Prince Hop… makes it a lot easier to get out of tight spots, and also increases the maneuverability of the Katamari to **reach higher places**" | [trophyunlocked 评测 2025-10-22](https://trophyunlocked.blogspot.com/2025/10/katamari-forever.html) / **C** | Forever 增加了**跳**（Prince Hop）来解决「上不去」，与「爬」并存 |
| S10 | **Once Upon**（2025） | "Most surfaces have a bump or a piece of geometry that cause me to **bounce instead of climbing after about 3 seconds**, then launches me back down …" | [Steam OUAK「Wall climbing and me…」2025-11-01, 楼主](https://steamcommunity.com/app/1880620/discussions/0/845114064658264667/) / **D** | **新作**里：爬约 3 秒后被几何凸起弹回；作者认为爬只在教程里有效 |
| S11 | Once Upon | "Improved the **operability of wall-climbing actions** within stages." | [OUAK 1.1.0 补丁说明（2026-03-06 报道）](https://nintendoeverything.com/once-upon-a-katamari-1-1-0-update-out-now-patch-notes) / **A** | 官方承认「壁登り」是独立动作，并在 2026 年专门修过手感 |
| S12 | **Beautiful**（2007） | —（未找到任何直接谈爬墙的原文） | — | **无资料**。按「各作相近」的假设可外推，但**没有来源**，不要写成事实 |

**存在性小结**：初代/REROLL/We Love/Forever/ノ・ビ～タ/Once Upon **都有**这条机制（S1、S5-S11）；
Beautiful 缺资料（S12）。「原作没有爬墙」是**误读**——初代只是**没有教学**（S5）。

---

## 3. 普通墙 vs 梯子（必须分开的两件事）

| 类型 | 能爬多高 | 原文/依据 | 强度 |
|---|---|---|---|
| **普通竖墙** | **未知**。REROLL 只说到「short walls」（S1）；新作侧说「capped to 1-2x your ball height」（S13）、「capped to … twice the height」（S14）；初代侧一说无限（S13）、一说封顶（S14 回应） | S1/S13/S14 | 定性 D，**无一手数值** |
| **梯子 / 绳 / 台阶** | **无限**（一直爬到顶） | S2（ladders）、S5（"ladder or steps… indefinitely"）、S13（"only exception is… a rope or a ladder"）、S15/S16（烟囱上有ハシゴ） | **多来源一致**，C+D，可当工作假设 |
| **烟囱案例** | 被当作「初代可无限爬普通墙」的证据，实际是**梯子** | S15「煙突にはハシゴが付いていて、そこから上へと登れる」；S16「煙突についているハシゴに向かって進めば上れる」 | **C，两处独立** ⇒ 推翻该论据 |

S13/S14 出处（同一帖的两方）：

> "Only the first game allowed you to climb indefinitely (one level even had a present hidden atop a factory smokestack where you had to climb for half a minute to reach it). **In the sequels and in this game, the climb height is capped to 1-2x your ball height.**… The only exception is if the surface has a rope or a ladder." —— Meat Miracle（被楼主标为答案）
>
> "you could only climb that smoke stack as it had a **ladder**, any other surface and you're capped to… **twice the height** of the Katamari. Think only Forever was a bit more lenient" —— Aries
>
> "It must've been over a decade since I played the original, but I'm fairly sure you could climb any height in it you wanted." —— Meat Miracle 的回应（自己降级为印象）

**读法**：这两条数字（1–2×、2×）都**不是**初代的测量值，而且**同一帖里互相打架**；
其中「烟囱」这个唯一的具体案例已经被日文攻略证伪（是梯子）——所以连「爬了半分钟」这个时长
也是**梯子上的时间**，不能拿来推普通墙的爬升速度。所以：

* 「初代普通墙无限爬」= **未证实**（作者自认凭印象，且其唯一例证是梯子）。
* 「续作/新作封顶 1–2×（或 2×）」= **社区说法，方向一致、数值不一致**，只能当**量级**。
* **初代/REROLL 普通墙的上限倍数 = 未知。**

---

## 4. 直接落到我们复刻关卡（蟹座）的一条

| # | 原文 | 来源 | 判读 |
|---|---|---|---|
| S17 | 「初期位置から出て、左側奥の**塀**に動き回っているカニがそこそこいます。…**王子ルックで見てから登って**巻き込むのがよいです。」「ポイント4の**塀から登れる高台**に2つ木があります。」 | [der3「塊魂アンコール かに座 100%」2018-12-10](https://der3.hatenablog.com/entry/2018/12/10/024748) / **C** | **蟹座（REROLL）里确实存在「一面可以爬上去的塀 + 上面的高台」**，而且是 100% 收集的必要路线。这给我们的参考关卡一条可判据：那张图里**至少有一面墙的高度落在可爬范围内** |

> 这条只能证明「可爬」，**不能**推出比例；但它把「蟹座里到底有没有可爬的墙」这个问题从推测变成有出处。

---

## 5. 数值状态表（哪些有、哪些没有）

| 量 | 初代 / REROLL | 续作 / 新作 | 来源强度 |
|---|---|---|---|
| 机制存在 | **有**（S1、S5 的默认前提） | 有（S6-S11） | 高（多来源） |
| 操作条件 | 慢贴 + 不弹开 + 持续前推（S1）；太陡/太高 ⇒ 爬不上（S3） | 双摇杆前推（S7）/ 三连甩（S8） | 中-高 |
| **上限倍数** | **未知** | 「1–2×」/「2×」（S13/S14，互相矛盾） | 低 |
| **爬升速度 / 到顶秒数** | **未知**（无任何来源） | 「约 3 秒后被弹开」（S10，新作，单条玩家观察） | 极低 |
| 掉落语义 | 未知（无来源描述「到顶掉落是否带离墙速度」） | 未知 | — |
| 梯子/绳 | **无限**（S2/S5） | 无限（S13） | 中-高 |
| 掉件代价 | 尝试爬也算碰撞、会掉件（S4、S5） | 续作起大幅减轻（同串多人口径） | 中 |

**明确未知（本轮没有任何来源）**：初代/REROLL 的 `H_cap ÷ 球径`、爬升速率（m/s）、`T_climb`、
上限处的离墙速度、可爬墙的坡度上限（只有「太陡」这一定性词）、球径是否影响可否起爬。

---

## 6. 对 [TERRAIN_CLIMB](../../Design/BallMotor/TERRAIN_CLIMB.md) 的影响

| 设计项 | 现在的依据 | 本轮核查后 |
|---|---|---|
| `k_h = 1.5`（上限 1.5·D） | 手册「约一个球径」+ 0.222 球上 0.30 m 台阶（E4，低可信） | **仍无来源支持**。唯一的手册级转述（S6）**不含尺寸比例**；「1–2×」是续作/新作的社区说法且帖内自相矛盾。1.5 落在该区间内**不构成证实** |
| `T_climb = 4.5 s` | 手感旋钮 | **仍无来源**。文献里唯一的时间数字是「约 3 秒后被弹开」，且属于 2025 新作、单条玩家观察，不能移植 |
| 「上限之下能翻上去」 | 一手手感 + E4 | 方向有支持（S1 的 short walls、S17 的塀→高台），**比例仍未知** |
| 「梯子可无限爬」 | 之前只有社区传闻 | **可升级为工作假设**（S2/S5/S13/S15/S16），并**必须与普通墙分开实现**（若我们要做梯子） |
| 手册引文的措辞 | `ContactTuning.cs` 头注写的是「can climb over walls that are about the same size as your katamari」 | ⚠ **需要复核**：维基对该手册句的转述是「climb it in order to reach higher areas」，**没有**「about the same size」。原文可能存在于手册别处，但目前**没有**可引的出处 |

> 一句话：机制存在是硬的，**数字全是软的**。`k_h = 1.5` / `T_climb = 4.5 s` 应继续标 PROVISIONAL，
> 并且不要在任何地方写成「原作实测」。

---

## 7. Lead 四条线索的最终判定（文献版）

| # | 线索 | 判定 | 依据 |
|---|---|---|---|
| L1 | 慢靠近、不弹开、再前推就能沿竖墙滚上去 | **证实（REROLL）** | S1（原文）；S7 补充「双摇杆前推」 |
| L2 | 初代普通墙能否无限爬 / 封顶 1–2 倍 | **无法判定**；且「初代无限」的主要论据（烟囱）被证伪为**梯子** | S13 vs S14 自相矛盾；S15/S16 |
| L3 | 梯子/绳可无限爬 | **证实（多来源一致）** | S2/S5/S13 |
| L4 | 撞墙/尝试爬会掉件，续作起减轻 | **证实（社区一致，REROLL 侧）** | S3/S4 + 同串多人；官方侧只有「高速撞墙会崩」的 PS2 作品页说法（附注） |
| L5（新） | 初代没有教学教爬墙 | **证实（E 级）**，可用于解释「原作没这功能」的普遍印象 | S5 |

附注（官方措辞，仅供对照）：Bandai 的 PS2 作品页把「变大后能过去」写成**段差**（台阶/高低差），
并把「高速撞到卷不动的东西或**墙**」写成**块会崩、变小**；该页**没有**提贴墙爬升。
（[Wayback 快照](https://web.archive.org/web/20260726191017/https://www.bandainamcoent.co.jp/cs/list/katamari_damacy_ps2/concept/) / **A**，原页现 403）

---

## 8. 若要继续（不需要模拟器、不需要自录）

1. **手册原文**：找 We Love Katamari 手册 PDF 的「How to Play」页，核实 S6 那句手册原文里到底有没有尺寸词
   ——这是唯一可能把 `k_h` 钉在**官方文字**上的入口，也直接决定 D1。
2. **Beautiful Katamari**（S12 空缺）：找它的说明书/攻略里有没有「climb」条目，补齐跨作一致性。
3. **梯子与普通墙的分野**：把「梯子/绳/台阶 = 无限」当工作假设，但要在实现上作为**独立类别**，
   不要用普通墙的 `k_h` 去套。
4. 若将来真要量：注意两个坑——追尾相机把球钉在屏幕固定位置（屏幕位移不能当高度），
   HUD 左侧「50cm/1m」箭头是尺度档位标签而非比例尺（实测箭头长度不随世界距离变）。

---

## 9. 来源清单

| 编号 | URL | 日期 | 类型 |
|---|---|---|---|
| S1/S2 | <https://steamcommunity.com/app/848350/discussions/0/3561682879997803952/> | 2018-12-13~15 | 社区帖 |
| S3/S4 | <https://steamcommunity.com/app/848350/discussions/0/1741102632991672789/> | 2018-12-11~14 | 社区帖 |
| S5 | <https://tvtropes.org/pmwiki/pmwiki.php/Sandbox/KatamariGames> | 2025-10-28 | Wiki（仅摘要，站点 403） |
| S6 | <https://en.wikipedia.org/wiki/We_Love_Katamari> | 2026 修订 | 百科（引手册） |
| S7 | <https://forums.unrealengine.com/t/help-with-remaking-katamari-damacy-in-hd/68356> | 2016-07-06 | 论坛 |
| S8 | <https://www.gamer.ne.jp/news/201110270001/> | 2011-10-27 | 媒体 |
| S9 | <https://trophyunlocked.blogspot.com/2025/10/katamari-forever.html> | 2025-10-22 | 评测 |
| S10/S13/S14 | <https://steamcommunity.com/app/1880620/discussions/0/845114064658264667/> | 2025-11-01~03 | 社区帖（含楼主采纳的答案） |
| S11 | <https://nintendoeverything.com/once-upon-a-katamari-1-1-0-update-out-now-patch-notes> | 2026-03-06 | 媒体转官方补丁说明 |
| S15 | <http://psmk2.net/ps2/soft_04/act/katak.html> | 2000s | 攻略站 |
| S16 | <https://wazap.com/question/%e5%85%a8%e9%83%a8%e3%81%ae%e3%83%97%e3%83%ac%e3%82%bc%e3%83%b3%e3%83%88%e3%81%ae%e3%81%82%e3%82%8b%e5%a0%b4%e6%89%80%e3%82%92%e6%95%99%e3%81%88%e3%81%a6%e3%81%8f%e3%81%a0%e3%81%95%e3%81%84%0d%0a%e3%81%8a%e3%81%ad%e3%81%8c%e3%81%84%e3%81%97%e3%81%be%e3%81%99%e3%80%82/30336/> | 2016-10-23 | Q&A |
| S17 | <https://der3.hatenablog.com/entry/2018/12/10/024748> | 2018-12-10 | 攻略博客（蟹座 100%） |
| 附注 | <https://web.archive.org/web/20260726191017/https://www.bandainamcoent.co.jp/cs/list/katamari_damacy_ps2/concept/> | 2026-07-26 快照 | 官方作品页 |
