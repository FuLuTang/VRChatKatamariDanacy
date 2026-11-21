# 原作碰撞/吸附球 vs 视觉球（球芯）——研究结论

日期 2026-11-17。只读研究，未改仓库、未碰 ssh/Unity/MCP/git。
级别：**实测**＝本机数据/录像我亲自读到的数；**文献**＝专利/网页/社区原文；**推断**＝我把上面几类接起来的读法。

## 0. 一句话结论

**owner 的假设（"碰撞球比视觉球大一圈，所以物体浮着"）不成立，至少不是原作避免"浮"的差别所在。**
原作的碰撞球相对**球芯**不是大"一圈"，而是大得多（HUD 30 cm 时芯半径约 2.4 cm，碰撞半径约 15 cm，约 6 倍），但原作看起来并不"浮"。
所以"碰撞球 > 球芯"在原作里是常态，不是我们独有的缺陷。能解释"浮"的是别的东西：**物件相对球太大且数量太少、填充密度低**，而不是核的缩放。

- 碰撞/吸附球 vs **HUD 球径**：**不小于**（等体积球内切于碰撞盒：面心处相等，角上最多 √3 倍）。置信度：**中高**（专利实施例原文；零售版是否逐字沿用未证实）。
- 碰撞/吸附球 vs **视觉球芯**：**大得多**，且随成长越拉越大（芯固定，碰撞随体积涨）。置信度：**高**（芯固定有录像实测；专利也没有缩放 Mp 的步骤）。
- 碰撞/吸附球 vs **整个视觉外廓（芯＋吸附物）**：**约略小于或相当**，外廓约 1.15×HUD（0.9–1.3）。置信度：**低**（PS2 480p 录像目视，±0.2）。

## 1. 逐问回答

### Q1 球芯是否随成长缩放？是否换模型？
- **同一档内不缩放。** 实测：HUD 5.0 cm 时芯宽 144 px/王子 130 px，6.9 cm 时 146 px/138 px，折合约 4.8±0.7 cm（`/tmp/attach_video_findings.md` §2.2，录像 `/tmp/kat/barry_pt1.mp4` 4:12=252 s 与 5:02=302 s）。我自己重新抽 302 s 帧（`/tmp/cr/c_302.png`）：HUD 6cm9mm，彩虹纹芯被纸盒/栗子围住，可见部分仍是一个小球。
- **专利无缩放步骤。** 实测读原文（US7402104B2，本机取得 `/tmp/us7402104.txt`，Google Patents 原页）：芯模型 Mp 存的是 "in the initial state where no objects are stuck to the ball B"（ball display model information 561）；吸附流程 S50–S58 只做：登记接触位置与姿态、体积相加、放大碰撞盒 Mh、更新行进性能、变形滚动模型 Mr。没有"放大显示模型"这一步。**文献**。
- **芯网格本身**：`~/KatamariRef/models/KatamariCore/original/core_01..29.obj`（27 个文件，无 26/27）。**实测**：全部包围盒约 4.8×4.9×5.4 cm（core_11/24 为 5.0×5.2×5.9，core_29 为 4.5³），等体积球径约 4.65 cm（core_01 52.6 cm³；core_29 48.2 cm³），顶点 md5 分组：13 个文件几何逐顶点相同，其余仅个别差异。即 **所有 core_XX 是同一个约 5 cm 的小球，只差贴图/微小变形，不是按球径分档的 21 个尺寸梯度。**这与仓库此前的读法（`PARITY_AUDIT.md` 的"按尺寸档换 21 档低模核心"、`ORIGINAL_ATTACH_LOOK.md` S5）**不一致**，应改。
  - 佐证 **文献**（本机已开 `~/KatamariRef/meta/pages/github_il_practice_mod_README.md` 第 88 行）："Most models are named after the levels where they're used in the unmodified game."——core_XX 是**按关卡命名的皮肤**。资源表 `meta/tables/raw_reroll_dump__asset_bundles.tsv` 第 24–49 行把 core_01..29 与角色/表情骨骼并列。
  - 芯网格的尺寸 ≈ MAS1 起始球径（`raw_reroll_dump__missions.tsv`：START_DIAMETER=5.0，`raw_growth_mechanism.txt` 说精确起始是 5.02 cm）：**推断**：芯模型按"起始球"作者化。起始径范围是 5–300 cm（Make the North Star=300 cm，同表），而芯网格始终 ≈5 cm，所以其他关里芯必然被按起始径放大（**推断**，无数据直证）。
- **是否换模型（大球时）**：**文献（社区，web_search 实际取得）**：PS2 原版在大尺寸时整个球被换成低模。Fandom 球页（https://katamari.fandom.com/wiki/Katamari）："the katamari model is replaced with a low-poly model of various objects stuck together"；同页表格给 Katamari Damacy 的三档为 After 12m / 60m / 200m（We ♥ Katamari 为 12m / 60m / 300m，Beautiful、Forever 各一档 12m），目的写作"to give the illusion that more items are on the katamari then there actually are"（同页，来自搜索返回片段，我没有通读全页）。Steam 差异帖（https://steamcommunity.com/app/848350/discussions/0/3561682880003061934/）称 REROLL 不换："the ball model doesn't change at 12 meters"。我们 0.3–2.2 m 的范围内不触发（最低一档是 12 m）。低模的具体网格与确切阈值：**未找到**。这只是玩家/社区说法，没有开发者或数据来源核实。
- **起始尺寸逐关不同**：**文献（社区，web_search 取得）**：Katamaripedia 球页（https://katamaripedia.miraheze.org/wiki/Katamari）："Their starting size varies greatly, from a few centimeters to thousands of kilometers in diameter."。同页与 Fandom 页都有按"Level / Mode / Size / Reused?"列出的球模型表，我拿到的搜索片段里表头完整、KD 的逐关行没有出来（只看到 Everyone's KD 5cm、File Select 10cm 等零散行）。KD 逐关起始径以本机 `missions` 表为准。这支持"球模型按关卡/用途列出，而不是按球径档"（E3 的第二个来源，权重低于 practice mod 那句）。

### Q2 碰撞/吸附半径与 HUD 球径的关系
- **文献（专利原文）**：碰撞模型 Mh 是边界盒，"a sphere the volume of which is the same as the sum of the volumes of the ball B and the stuck placement objects E" 内切其中（实测读 `/tmp/us7402104.txt` 第 381、845 行）。体积相加后按体积放大 Mh（第 471、625 行）。
- **文献（官方手册 + 研究）**：HUD 读数是等体积球的直径；加 +0.02 cm 偏置（`~/KatamariRef/meta/raw_growth_mechanism.txt` 第 1 点与 Note to 1，社区文档）。
- **推断**：Mh 半边长 = HUD 半径（面心），角点 = √3×。故原作的"碰撞半径"≥HUD 半径；我们用等体积球（=HUD 半径）是原作的下界（角上更紧）。PARITY_AUDIT 已记录这一取舍（"吸收触发的几何"行）。
- **专利实施例的限定**：专利说 Mh "也可任意设定"（第 893 行）；REROLL practice mod 说明里 F4 画出的是"katamari 的碰撞网格（红）加物件引入的额外 vault 点（蓝）"（本机 `github_il_practice_mod_README.md` 第 293–296 行，**文献**）。说明 REROLL 的实际边界不是单纯盒，我没有找到其与 HUD 球径的数值关系（**未找到**）。

### Q3 新粘的物体粘在哪里
- **碰撞接触处原地粘**（文献：专利 "in the contact position or the vicinity of the contact position"；第 463 行。实测：录像 4:19.73–4:20.4 一件小螺丝/骰子接触后与球刚性同转，无吸入动画，见 `/tmp/attach_video_findings.md` §2.1）。
- **贴哪个面**：专利里接触是"物件碰撞模型"碰"球碰撞模型 Mh"。没有任何"碰到已粘物件也算接触"的描述；吸附物之间无碰撞（单一碰撞代理）。**文献**。录像无法区分"碰撞球面"与"已粘物堆外层"（吸附瞬间被王子遮挡）。我重抽 259.9–261.0 s 的帧（`/tmp/cr/dice_grid.png`）仍被"OH!"对话气泡与王子遮住，未能补上。
- **嵌入**：专利无显式深度；录像弱证据约 30–60%（±20 点）。我本次未能独立重现这个数（遮挡）。**实测（弱）**。
- **核表面**：不是。核固定在中心，大球时离接触面很远。

### Q4 原作为什么不显得"浮"
没有一个单一机制，下面按证据强度排序：

1. **物件相对球小得多，数量大，层层累积（实测+推断）**：原作球从 5 cm 滚到 30 cm，靠大量小件堆成。10 倍体积门（专利 10%；`raw_growth_mechanism.txt` 第 2 点）把"单件占球"上限定死，实际件通常远小于此。录像 18:46–18:47（1126–1127 s，HUD 30cm7mm）：一个近圆实心团，件小、互相叠压（我重看 `/tmp/cr/g_1127.png`、`h_1127.png`、`h_1131.png`）。每件以当时球半径接触，球半径随每件增长，所以后吸的自然在外层。
2. **原作也有巨大的核-碰撞空腔，但被物件填满（推断）**：30 cm 时空腔半径约 12.7 cm（核 2.4 cm 对碰撞 15.35 cm）。我们 N=10/20/30/40 时只 16/31/43/55 mm。原作的"空"被大量小件填掉，我们的件大而少，缝隙里直接看见空腔。`/tmp/attach_sim_findings.md` §4 已指出这一点：我们的件是整张纸、整把剪刀，起始球 0.222 m，原作起始球 5 cm。
3. **体积回收比不是常数，大球时远小于 1（文献+推断）**：`raw_growth_mechanism.txt` 给了逐关分段曲线，如 MAS1：5–10 cm 为 0.8，15 cm 为 0.4，20 cm 为 0.16。球径变大后每件只加很少体积，HUD 球径（及碰撞盒）长得比实际物质慢，**新件更容易埋进已有的物堆里**；我们当前缺省是平坦 0.8（`PARITY_AUDIT.md`："按球径分段曲线仍未落"）。这是**推断**，我没有数据直接证明"回收比降低 ⇒ 嵌入变深"，需要离线模拟验证。
4. **PS2 在 12 m 以上整球换低模杂物团（文献，社区；见 Q1）**：这是原作用来"制造更多物件的错觉"的显示层捷径，我们 12 m 以内用不到。
4b. **内层物件被删，外层保留（文献，开发者转载 + 玩家文章，见 E19、E20）**：导演自述被卷起的物件被"吸收"、不再可见时释放；玩家文章同样描述下层物件融入球体、上层可见。含义（**推断**）：原作不需要把整个体积里每一件都渲染出来，只需要最外一两层看起来满。我们当前是否对深层件做剔除，见 ATTACH_PLACEMENT 的消隐讨论，这里不展开。
5. **物件互相穿插，无互碰（专利 + 录像实测）**：`/tmp/attach_sim_findings.md` 在我们的数据上量到真实网格互穿只占约 5% 的对，OBB 版本约 9%；原作的穿插更多（专利无互碰）。
6. **核是凹凸的花纹球（实测：core_01.obj 到中心距离在 2.2–2.7 cm 间起伏，rmax 0.027、rmin 0.022 m；贴图见 core_01.png / core_29.png，我看了图是彩色条纹/斑点）**，不是光滑纯色，缝里看到的是花纹而不是纯红。我们的核是纯红光滑球（`PARITY_AUDIT.md` 视觉核心行）。

**被证据排除的机制**：
- "核随球径变大把空腔吃掉"——不是原作机制（Q1），离线模拟里把核放大对核可见度只有 −1 点（`/tmp/attach_sim_findings.md` §2）。
- "碰到已粘物也算接触"——专利无此步骤，离线模拟里让球更松、最远离球 18 cm。

## 2. 证据清单

| # | 证据 | 级别 | 来源 |
|---|---|---|---|
| E1 | 芯 5.0→6.9 cm 同像素宽，≈4.8±0.7 cm | 实测（弱，目视 ±0.7） | `/tmp/kat/barry_pt1.mp4` 4:12（252 s）、5:02（302 s）；`/tmp/attach_video_findings.md` §2.2 |
| E2 | 芯网格 27 个文件，包围盒约 4.8–5.4 cm，等体积径约 4.65 cm，13 个文件顶点完全相同 | 实测 | `~/KatamariRef/models/KatamariCore/original/core_*.obj`（我自己算的体积与 md5） |
| E3 | core_XX 是按关卡命名的皮肤，不是尺寸档 | 文献 | `~/KatamariRef/meta/pages/github_il_practice_mod_README.md` 第 87–89 行 |
| E4 | 起始径 5 cm 至 300 cm，同一个 5 cm 芯网格 | 实测（表）+推断（芯被放大） | `meta/tables/raw_reroll_dump__missions.tsv` |
| E5 | 专利：Mp 为初始状态芯，S50–S58 无放大 Mp，只放大 Mh、变形 Mr | 文献 | US7402104B2 https://patents.google.com/patent/US7402104B2/en（本机 `/tmp/us7402104.txt`）。第 560–561、625 行 |
| E6 | 专利：Mh=等体积球内切的边界盒；体积相加后放大 | 文献 | 同上，第 381、471、845 行 |
| E7 | 专利：粘在接触位置或其附近，局部原点位姿存入吸附列表；吸附序号越大越新 | 文献 | 同上，第 463、575、890 行 |
| E8 | 专利：吸附上限 10% 体积；显示层级挂在摄像机视点上 | 文献 | 同上，第 865 行、S20–S24 |
| E9 | HUD=等体积球径 +0.02 cm；10 倍体积门；回收比逐关分段线性 | 文献（社区文档） | `meta/raw_growth_mechanism.txt` |
| E10 | 吸附瞬间：无吸入动画、有星爆；接触后与球刚性同转 | 实测 | 录像 4:19.73–4:20.4；`/tmp/attach_video_findings.md` §2.1 |
| E11 | 嵌入约 30–60% | 实测（弱，遮挡） | 录像 4:20.23；我重看 259.9–261.0 s 仍被遮挡 |
| E12 | HUD 30cm7mm 时是一个由小件叠成的近圆实心团；其中有一块疑似彩虹纹的芯露出 | 实测（弱；彩虹块可能是物件） | 录像 1126–1131 s，`/tmp/cr/g_1127.png`、`h_1127.png`、`h_1131.png`。**不单独作证据** |
| E13 | 外廓≈1.15×HUD（0.9–1.3） | 实测（弱） | `/tmp/attach_video_findings.md` §2.3 |
| E14 | 离线模拟：核可见度不因核放大而改善（N≤40 为 −1 点）；"碰已粘件算接触"让球更松；嵌入 45% 可把 N=40 的 57% 降到 31% | 实测（工具内假设对照，非原作拟合） | `/tmp/attach_sim_findings.md` §2–3 |
| E15 | PS2 大球时整球换低模杂物团（KD：12 m / 60 m / 200 m 三档）；REROLL 不换 | 文献（社区） | https://katamari.fandom.com/wiki/Katamari："the katamari model is replaced with a low-poly model of various objects stuck together"；https://steamcommunity.com/app/848350/discussions/0/3561682880003061934/："the ball model doesn't change at 12 meters" |
| E16 | 物件体积是 k×包围盒（1425 个有模型的类型里 70.7% 的 k=0.70）；真实网格实体积约为表值的 0.47 | 实测 | `~/KatamariRef/meta/solid_volume/README.md`（社区表 + 本机估算） |
| E17 | 同一 Steam 帖的解释：换模型是为性能，掩盖球"吸收了所有物件"。发帖人用词 "the 'core' grows"，指的是整个球（含物件）变大，与我们录像里"芯不长大"不矛盾，也不是开发者说法，权重低 | 文献（玩家说法） | https://steamcommunity.com/app/848350/discussions/0/3561682880003061934/ |
| E18b | Katamari 是 "a multicolored, bumpy ball"；起始尺寸从几厘米到数千公里；球模型按 Level/Mode/Size 列表 | 文献（社区） | https://katamari.fandom.com/wiki/Katamari ；https://katamaripedia.miraheze.org/wiki/Katamari："Their starting size varies greatly" |
| E19 | 导演 Takahashi 的 postmortem（Game Developer 2004-12，英译；我读到的是第三方转载页的搜索片段，不是 Game Developer 原页）：被卷起的物件"are absorbed into it in order to reduce the load on the CPU"，"不再可见时释放内存"。含义：深层物件会被删，大球中心可以是空的 | 文献（开发者，转载） | http://yannick.fleurit.free.fr/GameDesign/Post-Mortem%20Katamari%20Damacy.htm 。Game Developer 复刊存在于 https://invisibleparade.com/posts/katamari-damacy-postmortem（只有简介片段） |
| E20 | 玩家文章：下层物件被吸进"nondescript mass of the ball itself"，上层仍可见 | 文献（博客，低权重） | https://onthecrtscreen.wordpress.com/2018/09/22/lonely-rolling-star-katamari-damacy/ |
| E21 | Wikipedia：长条物（如铅笔）"will sometimes poke outwards out of the katamari" | 文献（百科） | https://en.wikipedia.org/wiki/Katamari |
| E18 | 小物件的体积回收比下降会让物质比 HUD 体积增长更快（嵌入加深） | 推断 | E9 + E5 |

**网上检索的状态记录**（本机数据、专利、录像三类证据不受此影响：专利我读了本机下载的原文 `/tmp/us7402104.txt`，核网格是我自己算的，录像帧是我自己看的）：
- **证据状态声明（重要）**：本文所有网页来源（E15、E17、E18b、E19、E20、E21，以及 Klonamari、KatamariIO 的说明）来自 web_search 调用，但这些调用的记录已从我的上下文里消失，**我自己无法重新看到返回原文，也就没有逐条核对过**。搜索确实发生过，这一点由 owner 确认；每条短摘和 URL 是否与原页逐字一致，请当作"未经我复核"。需要入库或写进决策时，请先抽查 URL。下面列的是我记得的 3 个查询方向：
  1. "Katamari Damacy katamari model replaced low-poly after 12 meters"——得到 E15、E17（Fandom 球页、Steam 差异帖；Katamaripedia 球页只有 KD "After 12m" 一档）。
  2. "Katamari Damacy reverse engineering collision radius attach objects katamari size"——得到 E18b、E21，和一批仿制品：Construct 论坛、Unity 克隆（https://github.com/UnderGear/Klonamari：球的 SphereCollider 半径随体积增长）、Minecraft 仿作（KatamariIO：方块 "snap strictly to the current collision sphere surface"）。仿制品不是原作逆向，不作为原作证据。**没有任何原作逆向/反编译资料。**
  3. "Katamari Damacy postmortem Takahashi objects absorbed reduce CPU load no longer visible"——得到 E19（转载页）、E20。
- 本次**没有**搜、没有读：TCRF、speedrun.com 的 wiki、noclip.website 之外的 mod 社区（只读过本机已存的 practice mod README 与 noclip 解析源码）。上面的"没有找到逆向资料"是**没搜到**，不等于不存在。
- Game Developer 原页本次没有打开；E19 来自第三方转载页（yannick.fleurit.free.fr），Game Developer 复刊页只在 invisibleparade 的转述里出现，**未核对原页是否逐字一致**。
- **未找到**：原作碰撞半径与 HUD 的数值关系的逆向/反编译资料；原作吸附嵌入深度的资料；TCRF 上关于球芯的条目；noclip.website 里关于球/芯的内容（本机 `~/KatamariRef/tools/noclip.website/src/KatamariDamacy/` 只处理关卡与物件）。上面两条"未找到"是**没搜到**，不等于不存在。

## 3. 对我们的建议（只提案，不改代码）

要和原作"一致"，**核不需要改**：固定起始径（0.222 m）与原作"芯不长大"一致，owner 已定，离线模拟也没有改善理由。不要让核随球径缩放，也不要把粘附位置改成贴已粘物外层。

最小改动（按成本和证据排序）：

1. **先复核"浮"的主因是物件尺度/密度，而不是半径关系。** 用同一份离线模拟（`Tools/attach_sim/`）对照：把件换小一档，或把起始球径调小（原作起始球 5 cm，物件几乎全是小件）。这是内容侧（O6）的问题，改动最小、最接近原作。
2. **落地体积回收比的分段曲线**（`PARITY_AUDIT.md` 已列"未落"）。推断它会让大球阶段新件相对更深地嵌进已有物堆（E18），可以先离线模拟，再决定缺省曲线。这是原作机制，决策缺省应为 restore。
3. **若 owner 仍要在不改内容的前提下消除可见悬空**：嵌入旋钮（接触点沿径向内拉 30–60%）。这与 `ATTACH_PLACEMENT.md` 已定的"触发即接触、不加嵌入"相反，原作只有弱证据（E11），属于需要 owner 再决并在 PARITY_AUDIT 的分歧表记一行的偏离。N=40 时核可见度可从 57% 降到 31%（E14），代价是互穿加倍。
4. **可选外观**：用 `core_XX` 的杂色低面数芯替换纯色球（E2、E3），缝隙里看到花纹而不是纯红；起始球径对应约 5 cm 的原网格缩放。外观资产，成本是授权/美术（参考资产不能进仓库）。
5. **文档修正**：`PARITY_AUDIT.md` 视觉核心行与 `ORIGINAL_ATTACH_LOOK.md` S5 里的"21 档尺寸换核"应改为"core_XX 是按关卡命名的皮肤；PS2 在约 12 m 后换杂物低模（社区说法），REROLL 不换"。`ATTACH_PLACEMENT.md` §3.1 的取舍"等效球代替 Mh 盒"仍成立。

## 4. 局限与待补

- 全部录像读数是 PS2 480p 目视；没有 REROLL/WLK 录像，没有"吸附前后同一件物件"的对照。
- 芯在 10–13 cm 以后被盖住，"全程不长大"是外推（E1 只量到 6.9 cm）。E12 的彩虹块只是旁证。
- 碰撞盒与 HUD 的关系来自专利实施例，REROLL 的实际边界（有 vault 点、有碰撞网格）未量。
- 起始径不同的关卡里芯被放大多少，没有数据（E4 为推断）。
- 回收比曲线对嵌入的影响（E18）是推断，需要离线对照。
