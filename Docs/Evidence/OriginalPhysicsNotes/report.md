# 原版《块魂》的球体运动与碰撞：能查到什么、查不到什么

**调研日期**：2026-11-15 UTC（项目时区 Asia/Shanghai）。
**范围**：只回答"球自己怎么动、怎么碰墙/坡、吸附物怎么参与"，不重做尺寸/吸附门（那部分仓库已有 `Docs/Research/KatamariBallRules/ABSORB_SIZE_RATIO.md` 与 `BALL_PHYSICS_IMPLEMENTATIONS.md`）。
**边界**：本轮**没有实机**（Linux、无 PS2/PS5/Windows 游戏）、没有自己拆包、没有跑 Unity。所有结论来自公开文字 + 官方手册 OCR + Namco 专利正文；每条都标证据级别，推断单独标 `[推断]`。
**写入范围**：只写 `/tmp/orig_physics/`。未改仓库任何被跟踪文件、未跑 git 写操作、未碰 Unity/Windows。

---

## 0. 证据级别定义（本文唯一使用这套标签）

| 标签 | 含义 | 例子 |
|---|---|---|
| `[官方]` | 开发者/发行方自己写的文件：说明书、官方 FAQ、官方 patch notes | 块魂(2004) 英文说明书、We Love Katamari 英文说明书、OUAK 1.1.0 patch notes |
| `[专利]` | Namco 的机制专利正文（US7402104B2，2003-09-25 优先权，发明人 Ogita / Yamabe / Takahashi） | "上坡需要预定最小速度"、"碰撞模型是等体积球的包围盒" |
| `[数据挖掘]` | 社区对 REROLL（内含 `PS2KatamariSimulation.dll`）的地址/数据/公式级分析 | 速通笔记的物件尺寸表、`!calc` 体积合成公式、30Hz 固定步长 |
| `[社区实测]` | 玩家/媒体在真机上观察到的行为，无代码依据 | "能爬墙但很难控"、斜面打滑、穿模 bug |
| `[推断]` | 我根据上面几条推出的机制解释，**不是**原作事实 | "凸出物相当于把接地点抬高" |

> **一句话现状**：原作的**运动模型**有专利级文档；**爬墙**有官方说明书级文档（但只有 We Love Katamari 及以后）；**具体角度/速度/高度数值**在公开资料里**基本没有**。

---

## 1. Q1 贴墙爬升 / 陡坡

### 1.1 结论（先说 verdict）

1. **"贴着墙推、球会往上爬"在原作系谱里是真实存在的、而且是被官方写进说明书的机制**——但**能查到的官方文档是《We Love Katamari》(2005) 的英文说明书，不是 2004 初代**。`[官方]`
2. **官方给的唯一定量规则是"墙高 ≈ 球的大小"**：说明书原话（短引）"You can climb over walls that are about the same size as your katamari." → **能翻过去的墙高与球同量级；更高的墙翻不过去**。`[官方]`
3. 这正好对上设计师记忆里的"爬一段、超过某个高度就掉回来"：**不是"爬升力耗尽"，而是"可翻越高度 ≈ 球径"这条设计上限**，超过就上不去、滑回。`[官方 + 推断]`
4. **70° / 90° 这两个具体角度没有任何公开来源**。没有开发者说过角度阈值，没有拆包数据，也没有可信的量化测试。用户记忆里的"70° 能爬更慢、90° 极慢地蹭"目前只能算 `[社区/个人观察]`，不能当原作数值引用。
5. **大小确实有影响，方向是"越大越能上"**：专利说上坡需要"预定最小速度"，而最大速度/加速度随体积增大 → 大球能上更陡的坡。`[专利]`
6. **"吸附物当抓手/腿"没有任何来源直接证实**。能证实的是相邻的一半：凸出物会改变滚动的接地点与贴地高度（专利的"针"模型），媒体实测说卡在人身上会让球"撑杆跳"。把它外推到"贴墙时凸出物提供抓地"是**我们的推断**，不是原作事实。`[专利 + 社区实测]` → `[推断]`

### 1.2 证据明细

| # | 来源 | 级别 | 它支持什么 |
|---|---|---|---|
| W1 | We Love Katamari 英文说明书（Namco, 2005, USA），archive.org OCR：<https://archive.org/download/kirklands-manual-labor-sony-playstation-2-usa-4k-version/We%20Love%20Katamari%20%28USA%29_chocr.html.gz> | `[官方]` | 有一节标题为 "CLIMB OVER OBSTACLES!!"：面朝墙时把左右摇杆都前推即可翻越；可翻越的墙"about the same size as your katamari"；"巧用"还能上梯子、楼梯 |
| W2 | 同 W1，经 Wikipedia 转引：<https://en.wikipedia.org/wiki/We_Love_Katamari> | `[官方]`（转引） | 同一句话的另一处引用，citation 指向说明书 pp.6–45；另有一条"撞墙/撞移动物会让吸附物掉落" |
| K1 | Katamari Damacy 英文说明书（Namco, 2004, USA），archive.org OCR：<https://archive.org/download/kirklands-manual-labor-sony-playstation-2-usa-4k-version/Katamari%20Damacy%20%28USA%29_chocr.html.gz> | `[官方]`（反向证据） | OCR 全文 **0 次** "climb"（OCR 可能有漏字）；只有 dash 一节说 "dash up slopes or over small steps"。"jump" 是**王子飞起来看风景**（相机），不是球跳 |
| K2 | 同 K1 | `[官方]` | "蓄力冲刺（charge'n'roll）用太久会让王子累，球会短暂变慢"；转弯时"球速与直行相同"（无转弯减速） |
| P1 | US7402104B2 专利正文 PDF：<https://patentimages.storage.googleapis.com/c2/d4/f4/193f7f21978e8d/US7402104.pdf>；HTML：<https://patents.google.com/patent/US7402104B2/en> | `[专利]` | "A predetermined velocity is necessary for the ball B to go up each of the slopes SL."；最大速度/加速度随体积增大 → 变大才上得去坡 |
| P2 | 同 P1 | `[专利]` | 球的滚动模型 = 16 根固定"针"(spicular arm) + 4 根可选针，**针尖就是接地点**；吸附物把附近针"伸长" |
| R1 | Steam 讨论 "Difficulties rolling up walls"（REROLL, 2019）：<https://steamcommunity.com/app/848350/discussions/0/1639790664936274066> | `[社区实测]` | REROLL 里"能开始往上滚"，但"中途会撞到东西弹开"，"几乎不可能有意识地爬"，"完全是偶然"；回帖说这在初代就是常态 |
| R2 | GBAtemp REROLL 评测（2018）：<https://gbatemp.net/review/katamari-damacy-reroll.909/> | `[社区实测]` | 玩家把"滚得够快就能上墙"当作一种（别扭的）机动手段 |
| R3 | Unreal 论坛（2016）：<https://forums.unrealengine.com/t/help-with-remaking-katamari-damacy-in-hd/68356> | `[社区实测]` | "in We Love Katamari, and Katamari Forever, you are able to roll, or climb, up the wall if you held both the analog sticks forward"；KF 教学关**要求**用双摇杆前推翻过一堵墙 |
| R4 | IGN Katamari Forever 试玩（2009）：<https://www.ign.com/articles/2009/08/14/katamari-forever-impressions> | `[社区实测/媒体]` | KF 新增"跳"，用来替代"爬坡/翻边缘"；"不能贴着墙起跳" → 侧面证明"贴墙滚动"是常规动作 |
| R5 | PS Vita《塊魂 ノ・ビ～タ》官方宣传稿（2011）：<https://www.gamer.ne.jp/news/201110270001/>、<https://dengekionline.com/elem/000/000/419/419362/> | `[官方]` | Vita 把"壁乗り越え"做成**独立动作**：朝想翻的墙 flick 3 次。说明"翻墙"是被当作一等机制对待的 |
| R6 | OUAK 1.1.0 免费更新（2026-03）：<https://www.bandainamcoent.com/pt_br/news/once-upon-a-katamari-free-update-march-4-2026>、<https://gonintendo.com/contents/58530-once-upon-a-katamari-updated-to-ver-1-1-0> | `[官方]` | patch notes 原文："Improved the operability of wall-climbing actions within stages." → 2025 新作仍把爬墙当独立动作，且承认手感需要调 |
| R7 | keymap.io OUAK 操作表：<https://keymap.io/controls/once-upon-a-katamari/> | `[社区实测]` | 把"Climb over a wall"直接写成"keep rolling forward into the wall"（L↑+R↑），与用户描述一致 |

### 1.3 我的读法（明确标推断）

- **2004 初代**：说明书没有爬墙条目，机制专利也只讲"坡"，不讲墙。REROLL 是"用现代外壳跑 PS2 逻辑"（见 §4 的 `PS2KatamariSimulation.dll`），而 REROLL 玩家确实能爬 → **初代引擎里应该存在这个行为，但它是"没被文档化的涌现行为/半成品"**，所以手感极差（R1）。
- **2005 WLK 起**：Namco 把它**收编成机制**，写进说明书并给了"墙高≈球径"的规则；KF 教学关直接教它；Vita 给了独立按键；2025 OUAK 还在调它的手感。
- 因此"原作里 90° 也能慢慢往上蹭"在**行为层面**可信（球贴着墙持续受驱动 → 位置解算沿墙往上"蹭"），但"能蹭多高"由"墙高≈球径"这条上限管着；**用户记忆里的"超过一定高度掉下来"最可能就是这个上限**，而不是重力耗尽。

### 1.4 数值现状（诚实结论）

- 角度阈值：**无任何公开数值**（70°/90° 都无来源）。
- 爬升速度 vs 角度：**无数据**。只有"上坡需要最小速度、Vmax 随体积涨"这一条定性关系 `[专利]`。
- 高度上限：**只有"约等于球的大小"这一条** `[官方]`。
- 时间上限（爬多久掉下来）：**无**。

---

## 2. Q2 球的碰撞形状

### 2.1 结论

1. **碰撞体是"一个"保守代理，不是"球 + 每个吸附物各自碰撞"**：专利明写球的碰撞模型 `Mh` 是**包围盒**（bounding volume），其内部内接一个"体积 = 球体积 + 所有已吸附物体积之和"的等效球；吸附后按新体积**整体放大**这个盒子。`[专利]`
2. **另有一套"滚动模型"**：16 根固定针 + 4 根可选针，**针尖是接地点**；吸附物会把附近的针往外伸长（越靠近吸附点伸得越长），于是球"凹凸不平地滚"。这套模型**只影响滚动/贴地，不进碰撞判定**。`[专利]`
3. **显示模型**：吸附物在碰撞瞬间记录"位置 + 姿态"，然后以**父子层级**挂到球的显示模型下，保持原尺寸。`[专利]`
4. **拾取判定用的是物件自己的碰撞盒 + 10% 体积比**（`De/Db < 10%` 才粘）。`[专利]`（与仓库既有 10× 结论一致）
5. 因此：**"球面 + 凸起"在物理上只改变"接地点/离地高度"，不改变碰撞外形**。碰撞外形永远是那个等效体积盒子 → 这解释了"球被吸附物顶得颠、歪着滚、撑杆跳"，也解释了为什么原作**不会**因为一根长杆就卡在门框上（碰撞是球的盒子，不是长杆的盒子）。`[专利 + 推断]`

### 2.2 证据明细

| # | 来源 | 级别 | 支持什么 |
|---|---|---|---|
| P3 | US7402104B2（同 P1）§图4A–4C 与流程 | `[专利]` | 三个模型分离：display `Mp` / collision `Mh` / rolling `Mr`；`Mh` = "a boundary box ... which a sphere the volume of which is the same as the sum of the volumes of the ball B and the stuck placement objects E touches internally"；吸附加体积后 "enlarges the ball collision determination model Mh ... with the ratio corresponding to the increases of the volume" |
| P4 | 同 P3 | `[专利]` | `Mr` = 16 固定针 + 4 可选针；"a displacement point Tf which is the end of the fixed spicular arm Af ... are ground points"；针的伸长量随"吸附点到该针的角距"衰减 |
| P5 | 同 P3 | `[专利]` | 显示：`Mp` 父子层级；吸附时记录碰撞瞬间的位置与姿态；小物体按 display level 不显示（见 Q4） |
| B1 | BlitzBasic 论坛对"块魂式原型"的技术点评：<https://archive.blitzbasic.org/forums/community-showcase/75777.html> | `[社区实测/技术分析]` | 批评原型"把每个吸到的物件都继续画、继续算碰撞"导致卡顿；并断言原作"用一颗随吸附增长的单一碰撞球，只处理表面物件"——与专利方向一致，但是玩家推断 |
| H1 | Honest Gamers 块魂评测（2004）：<http://www.honestgamers.com/12930/playstation-2/katamari-damacy/review.html> | `[社区实测/媒体]` | "if a person is rolled up by their feet they'll protrude ... and jab into the ground as it rolls, making the ball pole-vault"；只在一侧吸小物件会让球歪着滚 |
| H2 | Honest Gamers 同篇 | `[社区实测/媒体]` | "the ball can be prone to skidding and rolling out of control on angled surfaces" |
| N1 | 纽约时报 2004 评测：<https://www.nytimes.com/2004/10/14/technology/circuits/touches-of-weird-done-best-in-japan.html> | `[社区实测/媒体]` | "Roll up a lamppost, and the ball will bump along awkwardly until enough additional objects round it out." |
| N2 | Wikipedia 块魂（引 IGN 评测）：<https://en.wikipedia.org/wiki/Katamari_Damacy> | `[社区实测/媒体]` | 判定用 "size, weight, and surface area"；细长物（铅笔）可以长于球宽仍被吸，且"这些会改变球的滚动，直到更多物件把它包圆" |

### 2.3 对我们最有用的三条

1. **我们现在的"单球碰撞 + 径向代理只影响贴地高度"其实和原作同构**——不必因为"像假球"而换真刚体。差别在**滚动模型**（原作 16+4 针，我们有 32 方向端点）与**爬墙规则**，不在碰撞外形。
2. **碰撞外形不要跟着凸出物长大**（原作是等效体积盒，不是长杆盒）。我们若把长杆纳入碰撞，会出现原作没有的"卡门框"。
3. **接地点由"针尖/端点"决定 → 凸出物抬高球心**。这条我们已经有了（`GroundContactMath`），只是缺"多个不同高度的支撑面"。

---

## 3. Q3 坡面处理：摩擦、滑动、台阶、防穿模

### 3.1 结论

1. **原作的球不是受力驱动的，是"按输入方向直接滚"的运动学模型**：专利说 rolling control unit "calculates the rolling of the ball B in the direction inputted ... and performs the control as if the ball B moves while rolling"；可调参数是 **最大速度、加速度、（可能）惯性**，并特意把参数随体积调整到"手柄输入 → 屏幕位移"几乎恒定（手感不随体型变）。`[专利]`
2. **坡是"速度门"，不是"摩擦角"**：专利明写每道坡需要"预定最小速度"；而 Vmax 随体积增大 → **大球 = 能上更陡的坡**，机制上是"速度够就上"，不是"tanθ ≤ μ"。`[专利]`
3. **台阶**：官方两处口径一致——"变大后能越段差"（日文官方页 `[官方]`）、"dash 能冲上斜坡或越过小台阶"（KD 说明书 `[官方]`）。
4. **有打滑/失控，且不轻**：媒体与玩家都描述斜面会打滑、在斜坡上转身会失控、连缓坡都吃力。`[社区实测]` 没有摩擦系数、没有坡度上限数值。
5. **没有球跳**（KD）：说明书里的 jump 是王子飞起来看地形。KF 才加球跳（且"不能贴墙起跳"）。`[官方]` + `[社区/媒体]`
6. **抗穿模：原作基本没有，靠"救回"而不是"防住"**：
   - 日文攻略站记录了 PS2 初代的"机ぬけ"：约 72cm 时钻到厨房桌子下面，球会**穿过桌面**把桌上的东西全吸走。`[社区实测]`
   - PSP 作的里技页记录了更严重的"すり抜け"：贴着自行车/柱子挤进去后能穿过去，最后**穿过地面往下掉**，然后触发"王様ワープ"（国王把你传送回附近）。`[社区实测]`
   - 所以原作的鲁棒性策略是**粗碰撞 + 掉出世界后救回**，不是扫掠/CCD。`[社区实测 + 推断]`
7. **固定步长 30Hz**：REROLL 内部仍以 30 FPS 跑 PS2 模拟，mod 只做帧间插值（`RerollPatcher`）。这解释了为什么穿模在原作里"可接受"：每步位移相对球径不大，且没有高速小物件。`[数据挖掘]`
8. **速度保持/惯性**：社区核心技术 "snaking"——冲刺后左右小幅交替转向可以比直行更久地保持高速；碰撞到吸不动的东西或**离地**会重置 gacha 计数。→ 原作**存在离地状态**（会飞起来/腾空），不是永远贴地。`[数据挖掘/社区技术]`

### 3.2 证据明细

| # | 来源 | 级别 | 支持什么 |
|---|---|---|---|
| P6 | US7402104B2（同 P1） | `[专利]` | 滚动是"按输入方向"计算；参数 = 最大速度/加速度/惯性；参数随体积调整以保持操作手感恒定 |
| P7 | 同 P6 | `[专利]` | 坡需要"predetermined velocity"；Vmax 随体积↑ |
| K3 | KD 2004 英文说明书（同 K1） | `[官方]` | dash 可以"dash up slopes or over small steps"；刹车 = 双摇杆反向；蓄力过久会"没气"变慢 |
| K4 | 同 K3 | `[官方]` | "如果物件在你撞它时抖动，说明你快能吸它了"（吸附预反馈） |
| O1 | 日文官方游戏概要（PS2 みんな大好き塊魂）：<https://www.bandainamcoent.co.jp/cs/list/katamari_damacy_ps2/concept/> | `[官方]` | "大きくなると、「段差を越えられる」ようになります"（变大能越段差）；"速いスピードで…壁・移動しているモノにぶつかると塊が崩れて小さくなる" |
| O2 | 同 O1 的新闻稿 PDF：<https://www.bandainamcoent.co.jp/corporate/press/namco/51/51-053refer.pdf> | `[官方]` | 同一条"段差"口径的第二处官方文本 |
| S1 | Steam REROLL 评测（2019）：<https://steamcommunity.com/profiles/76561198008096580/recommended/848350/> | `[社区实测]` | "handles ledges poorly, will spin out of control on inclines (you won't be able to save yourself), and even struggle with slight inclines" |
| S2 | 速通笔记 Boosts 页：<https://shookietea.github.io/reroll-notes/mechanics/boosts/boosts/> | `[数据挖掘/社区技术]` | gacha 计数在"离地"或"撞到吸不动的东西"时重置；boost 所需 gacha 数随体型 5→6→7→8；boost 能量 240、耗尽后 8 秒"喘气" |
| S3 | 速通社区论坛帖（搜索摘要；原帖 403）：<https://www.speedrun.com/ko-KR/kd/forums/v69f4> | `[社区技术]` | "snaking"：冲刺后左右微摆能更久保持速度 |
| S4 | RerollPatcher（BepInEx 插件）：<https://github.com/squiddingme/RerollPatcher> | `[数据挖掘]` | 游戏内部逻辑固定 30 FPS（PS2 模拟），mod 只做帧间插值 |
| S5 | 速通 ASL 脚本：<https://raw.githubusercontent.com/misoelegant/rpm/master/rta-mod/reroll-rta-mod.asl> | `[数据挖掘]` | 直接读 `PS2KatamariSimulation.dll` 的 `levelID`、`frames`、`size` 浮点；30fps 计时（1800 帧/分） |
| S6 | PSP《僕の私の塊魂》里技页：https://urawaza.in/psp/bokwatakata.htm | `[社区实测]` | 穿自行车/柱子→穿地面→"王様ワープ"救回；挤进建筑入口能变得比初始还小 |
| S7 | PS2 塊魂 攻略集（裏技）：<http://psmk2.net/ps2/soft_04/act/katak.html> | `[社区实测]` | "机ぬけ"：约 72cm 时钻到桌下会穿过桌面把桌上物件全吸走 |

### 3.3 我的读法

- 原作坡面行为可以概括成 **"速度够就上，速度不够就打滑"**，而不是"坡度超过 μ 就滑"。这和我们的 `SweepMath`（半径膨胀 AABB + `stepLimit`）完全不是一套语义：我们现在的极限是**几何**的（`θ_max = asin(0.6) = 36.87°`，见 `Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md` §4.1.3 与 C05 的 0.0887 m 红灯），原作的极限是**速度/体型**的。这解释了为什么我们 45° 就死锁，而原作 90° 都能蹭。
- 原作**没有**我们担心的"穿模防御"问题，因为它靠 30Hz + 粗碰撞 + 救回。我们不能照抄"没有防穿模"，但可以照抄"掉出世界要有救回"。

---

## 4. Q4 吸附物会不会跟着放大？会不会被埋/被删？

### 4.1 结论

1. **不缩放**。吸附瞬间记录物件当时的**位置与姿态**，作为子物体挂上去，之后球长大不会重设物件尺寸。`[专利]` 因此小球变大后，旧的小物件相对越来越小、被后吸的大物件盖住 → "埋进去"。
2. **球的大小是"体积和"的函数**：球体积 = 初始体积 + Σ 吸附物体积；显示的"大小"（cm/mm）是按关卡公式换算出来的直径。速通社区的 bot 能直接算"加/减这些物件后球变成多大"，说明数据侧确实是**每个物件一个固定尺寸 + 按关卡的合成公式**。`[数据挖掘]`
3. **小的会被"消隐"**：专利按 display level 决定是否绘制——**比球小得多的物件直接不画**（"the placement object E is relatively small in comparison with the ball B ... not necessary for the display"）。`[专利]`
4. 社区侧有独立佐证：日文 wiki 记"尺寸小的物件，球一变大就马上消失"（举例：十字螺丝刀）；5ch 的老玩家吐槽"卷进去的东西会消失、露出块底"；2025 新作被老玩家批评"物件消失得太快，30 秒后只剩十几个"，反过来说明旧作保留得更久。`[社区实测]`
5. **"深处物件被物理删除"没有公开证据**：专利只说不**显示**，没说从吸附列表移除。视觉/碰撞上等价于消失，但计分/体积仍然算它（否则球会缩水）。`[专利 + 推断]`

### 4.2 证据明细

| # | 来源 | 级别 | 支持什么 |
|---|---|---|---|
| P8 | US7402104B2（同 P1） | `[专利]` | 吸附时记录碰撞瞬间的位置与姿态；父子层级；体积相加；按 display level 取消显示小物件 |
| D1 | 速通笔记 `!size` / `!calc`：<https://shookietea.github.io/reroll-notes/bot-commands/> | `[数据挖掘]` | 每个物件有固定"size"（cm/mm）；`!calc` 能算"加 x 个某物件后球的新尺寸"，公式按关卡不同；同名物件靠 name tag 区分 |
| D2 | 速通笔记尺寸里程碑：<https://shookietea.github.io/reroll-notes/Important%20Object%20and%20Size%20Milestones/> | `[数据挖掘]` | 大量"球到 X cm 才能吸 Y"的数值对（例：`110.7 + 8th Step×11 = Ceiling Light 122.6`），可反推体积门与"物件尺寸固定" |
| D3 | 日文 wiki（モノ/プラスドライバー）：<https://wikiwiki.jp/yawaka802/%E3%83%A2%E3%83%8E/%E3%83%97%E3%83%A9%E3%82%B9%E3%83%89%E3%83%A9%E3%82%A4%E3%83%90%E3%83%BC> | `[社区实测]` | "サイズが小さいため塊が大きくなるとすぐ消えてしまう"（小物件在球变大后马上消失）——讨论的是 WLK |
| D4 | 5ch スレッド（塊魂の新作を作れ）：<https://itest.5ch.io/krsw/test/read.cgi/ghard/1575809255> | `[社区实测/民俗]` | "巻き込んだものが消滅して塊の下地が見える"（吸进去的东西消失、露出球底），当作旧作的内存限制来吐槽 |
| D5 | Steam 评测（OUAK, KuroOneHalf）：<https://steamcommunity.com/id/kuroonehalf/recommended/1880620/> | `[社区实测]` | 批评新作"吸一百个物件只长 10%""物件 30 秒内大量消失"，并以旧作为对照 → 旧作保留更久、且"球应该是物件拼成的" |
| D6 | atwiki（Vita 攻略，大きくする1）：<https://w.atwiki.jp/touchmykatamari/pages/24.html> | `[社区实测]` | 该关"モノが消滅しないので"（物件不会消失），是**特例**——反证其它关会消失 |

### 4.3 对我们最有用的

- "吸附物不缩放 + 小球被埋/消隐"这两条我们可以直接照抄，代价低、观感对。
- 关键设计选择：**消隐阈值**（相对球径）要和"碰撞/计分仍算它"分开。原作看起来是"显示层 cull、数据层保留"。我们若真删，会让球体积回退，观感与原作不符。

---

## 5. Q5 同类实现怎么做爬墙（可借鉴的具体机制）

> 这一节全部是**别人的工程做法**，不是原作事实。

| # | 来源 | 级别 | 可抄的具体机制 | 局限 |
|---|---|---|---|---|
| U1 | Unity Discussions "Controllable Ball Physics"（2013）：<https://discussions.unity.com/t/controllable-ball-physics/493994/1> | `[社区]` | **加扭矩 + 加力**的刚体球会"顶着垂直墙滚上去"；只用扭矩也行但"慢得像蜗牛"且离地就失控 → **爬墙是扭矩/摩擦的自然结果**，可用"扭矩驱动 + 接触点切向速度"来仿 | 真刚体，Udon 下不可用（见仓库既有 V1/V2） |
| U2 | Unreal 论坛（2016，同 R3）：<https://forums.unrealengine.com/t/help-with-remaking-katamari-damacy-in-hd/68356> | `[社区]` | 两条被讨论的路线：① 切换"非重力模式"；② "snap 到墙上"（贴墙吸附）。发帖人最后说"climbing system 搞定了"，但没给细节 → 说明**社区也没有标准解** | 只做"贴墙爬"，没有高度上限/掉落 |
| U3 | Matthew Consterdine 的块魂克隆（2018）：<https://consto.uk/2018/10/07/bring-the-light> | `[社区]` | 吸附物**关掉自身碰撞体**，改为"从球心向外打射线、取最远命中点重建程序化碰撞网格" | 未处理爬墙 |
| U4 | Godot 官方 `KinematicBody` 文档：<https://docs.godotengine.org/en/3.4/classes/class_kinematicbody.html> | `[官方]`（引擎） | `move_and_slide_with_snap` 用 snap 向量把运动学体"粘"在坡上；`stop_on_slope` 决定静止时是否下滑；`floor_max_angle` **默认 0.785 rad = 45°** 是"斜坡/墙"的分界 → 运动学体必须显式给"贴地"和"坡度分界"两个开关，否则停坡上会滑 | 角色 controller 语境，非球 |
| U5 | Godot 论坛：贴墙攀爬时对齐墙面法线：<https://forum.godotengine.org/t/how-to-make-the-character-face-the-plane-you-are-climbing-on/6736/2> | `[社区]` | 用 `align_with_y(collision_normal)` 把"上方向"换成墙法线 → **接触法线驱动的贴墙坐标系**，倒角处要特殊处理 | 角色而非球 |
| U7 | Unity 官方文档 CharacterController：<https://docs.unity3d.com/2022.3/Documentation/Manual/class-CharacterController.html> | `[官方]`（引擎） | `slopeLimit` / `stepOffset` / `skinWidth` 的语义：**坡度上限 + 台阶上限**是两个独立参数，正好对应我们要补的两件事 | 我们不用 CharacterController，只借语义 |
| U8 | Unity 官方文档 Rigidbody CCD：<https://docs.unity3d.com/2022.3/Documentation/Manual/class-Rigidbody.html> | `[官方]`（引擎） | 连续碰撞只支持 Sphere/Capsule/Box；运动学体要靠 `SphereCast`/分步 | Udon 不可用刚体，只能借"分步扫掠"思想 |

**结论**：没有找到"块魂式爬墙 + 高度上限 + 凸出物抓手 + 掉落"的现成实现或教程。能抄的是零件：接触法线坐标系（U5）、扭矩/切向驱动导致爬墙的直觉（U1）、贴墙吸附（U2）、程序化碰撞外形（U3）、显式 slope/step 上限（U7）、分步扫掠（U8）。**组合规则必须我们自己设计。**

---

## 6. 我们能可靠照抄 / 仍然未知 / 必须自己设计

### 6.1 可以可靠照抄（有 `[官方]`/`[专利]` 依据）

1. **爬墙的输入语义**：面朝墙 + 双摇杆前推 = 往上爬；松开或转向 = 停止。`[官方]`（W1/R7 一致）
2. **爬升高度上限 ≈ 球的大小**（量级），超过就上不去/滑回。`[官方]`（W1）
3. **速度是坡的门槛**：同一道坡，速度够就上、不够就滑；体型越大 Vmax 越高。`[专利]`（P1/P7）
4. **碰撞外形 = 单个等效体积代理**，不随凸出物变形；凸出物只改"接地点/离地高度"。`[专利]`（P3/P4）
5. **吸附物不缩放、保持吸附瞬间位姿；小球长大后小的被消隐**。`[专利]`（P8）+ `[社区实测]`（D3/D4）
6. **台阶：变大能越更高的段差；冲刺能临时越过高一点的段差/坡**。`[官方]`（O1/K3）
7. **掉出世界要有"救回"**（原作是国王传送）。`[社区实测]`（S6）

### 6.2 仍然未知（不要编数字）

- 任何**角度阈值**（70°/90° 的说法无来源）。
- 爬升**速度-角度曲线**、爬升**加速度**。
- 爬墙的**时间上限**（如果有）。
- 摩擦系数、坡度上限的具体值。
- "凸出物当抓手"是否真的被实现（只有"抬高接地点"的间接机制）。
- 初代 2004 是否**有意**支持爬墙（说明书 0 次 "climb"，更像涌现行为）。

### 6.3 我们的设计（**以下全部是 OUR design，不是原作事实**）

#### 6.3.1 模型总览：三层，别混在一颗球上

| 层 | 我们的做法 | 对应原作 |
|---|---|---|
| 碰撞外形 | **单球**（`R_col`），可选"等效体积盒"用于窄缝判定；**不纳入凸出物** | `[专利]` 的 `Mh` |
| 支撑/贴地 | 现有 32 方向端点求支持高度（保留），新增"墙/陡面接触"作为**第二类支撑源** | `[专利]` 的 16+4 针 |
| 运动 | 运动学驱动（速度/加速度/惯性），**不做刚体** | `[专利]` 的 rolling control |
| 爬升 | 新增**显式状态机**：`Ground → WallCling → SlideBack`（见下） | 原作行为，但原作实现未知 |

#### 6.3.2 参数化爬升模型（可直接抄进 `TERRAIN_SUPPORT.md`）

**输入**（每帧）：

| 符号 | 含义 | 来源 |
|---|---|---|
| `θ` | 接触面法线与竖直的夹角（0=平地，90=垂直墙） | 几何 |
| `p ∈ [0,1]` | 推入强度 = `max(0, dot(desiredDir, -n))`（双摇杆全前推=1） | 输入 |
| `v` | 当前球速（m/s） | 电机 |
| `R` | 碰撞半径（m） | 尺寸 |
| `L` | 凸出物沿 `-n` 方向的**有效伸出量**（m）= 该半球内端点支持长度 − R，取 max(0,·) | 径向代理 |
| `g` | 抓地系数（每个吸附物一个 0–1 的标签，或按"是否尖/硬"自动分类） | 设计 |

**核心判定**：

```
# 1) 可攀爬角（由"速度门"改成"速度 + 抓地"门）
v_req(θ) = v_min * (sin θ)^α            # α≈1.0；v_min = 0.6 m/s（OUR）
climbable = (v * (1 + k_g * g_eff) >= v_req(θ))

# 2) 爬升高度上限（唯一有原作锚点的式子）
H_cap = k_h * (2R + λ * L)              # k_h = 1.0（锚点：WLK 手册"墙高≈球径"）；λ = 1.0（OUR）
h     = 已爬升高度（沿墙法线平面累计）

# 3) 爬升速度（缓、随角度衰减、接近上限时趋零）
v_up = p * v * c_wall * cos(θ) * (1 - clamp(h / H_cap, 0, 1))   # c_wall = 0.25（OUR）
# 平地/缓坡不走这条：θ < θ_roll(=50°, OUR) 时直接用常规坡面驱动

# 4) 掉落（超过上限或输入撤销）
if h > H_cap or p < 0.2:
    state = SlideBack
    a_down = g_grav * (sin θ - μ_wall * cos θ)   # μ_wall = 0.15（OUR，很小 → 会滑回）
# 5) 防抖：SlideBack 有 0.25 s 冷却，期间不允许再进 WallCling
```

**读数解释**：
- `k_h = 1.0` 是唯一"抄"来的数（墙高≈球径）；其余 `v_min / α / c_wall / μ_wall / λ` 全是**我们拍的**，应进 `physics_test_field.json` 当可调参数，并标 `provenance: design`。
- 这套式子能同时产生用户要的三种观感：70° 慢爬（`cosθ` 小 + `v_req` 大）、90° 极慢蹭（`v_up ∝ cos90°→0`，只能靠 `L`/`g` 挤出一点）、超过 `H_cap` 掉回。
- **不要**把它做成"角度白名单"（`θ ≤ 70° 才可爬`），那会产生硬边；用 `v_req(θ)` 的连续函数更接近"速度不够就上不去"的原作语义。

#### 6.3.3 与我们现有电机的接口

- 现有 `SweepMath` 的 `stepLimit = 0.5r` 导出 `θ_max = asin(0.6) = 36.87°`（`PHYSICS_TEST_FIELD.md` §4.1.3，C05 实测 0.0887 m）。这是**几何死锁**，加参数救不了；要支持"垂直墙爬升"，必须把"墙接触"从"半径膨胀 AABB 阻挡"改成**独立的法线接触通道**（球-平面/球-盒圆角扫掠，或"贴墙时切到 WallCling 状态机，绕过 step 规则"）。
- 建议顺序：先做 `WallCling` 状态机（不碰扫掠），用固定 `θ` 的测试墙验证；再把球-盒接触改成圆角扫掠以消灭 C05 死锁。
- 防穿模：`WallCling` 里每步位移 `≤ 0.4R`；退出状态时做一次 `SphereCast` 回退，避免"贴墙加速 → 穿墙"。

#### 6.3.4 建议加的测试格（对齐现有 C 编号风格，仅提议）

| 格 | 几何 | 判据 |
|---|---|---|
| W1 | 90° 墙，高 = 0.5×球径 | `climbed_over = true`，`h_max ≥ 0.5·D` |
| W2 | 90° 墙，高 = 3×球径 | `h_max ≤ 1.2·D`（不越顶），有 `slide_back` 段 |
| W3 | 70° 坡，长 = 3×球径 | 能上，`v_up ≤ 0.4·v_flat`（慢） |
| W4 | 90° 墙 + 一根 0.8R 凸出杆 | `H_cap` 比 W2 大（凸出物抬高上限），且 `rocking` 幅度上升 |
| W5 | 0.5D 高台阶 + dash | 能上；不 dash 时不能上 |
| W6 | 掉出世界 | 触发救回，位置回到最近合法点 |

---

## 7. 来源总表（含访问结果）

| ID | URL | 级别 | 访问 |
|---|---|---|---|
| W1 | https://archive.org/download/kirklands-manual-labor-sony-playstation-2-usa-4k-version/We%20Love%20Katamari%20%28USA%29_chocr.html.gz | `[官方]` | 200，OCR 文本已读 |
| W2 | https://en.wikipedia.org/wiki/We_Love_Katamari | `[官方]`转引 | 200 |
| K1/K3 | https://archive.org/download/kirklands-manual-labor-sony-playstation-2-usa-4k-version/Katamari%20Damacy%20%28USA%29_chocr.html.gz | `[官方]` | 200，OCR 已读（"climb" 0 次） |
| P1–P8 | https://patentimages.storage.googleapis.com/c2/d4/f4/193f7f21978e8d/US7402104.pdf / https://patents.google.com/patent/US7402104B2/en | `[专利]` | 200，PDF 全文已读 |
| O1 | https://www.bandainamcoent.co.jp/cs/list/katamari_damacy_ps2/concept/ | `[官方]` | 200（搜索摘要+正文） |
| O2 | https://www.bandainamcoent.co.jp/corporate/press/namco/51/51-053refer.pdf | `[官方]` | 搜索摘要 |
| R6 | https://www.bandainamcoent.com/pt_br/news/once-upon-a-katamari-free-update-march-4-2026 | `[官方]` | 搜索摘要（另有 gonintendo 全文） |
| R1 | https://steamcommunity.com/app/848350/discussions/0/1639790664936274066 | `[社区实测]` | 200 全文 |
| R3/U2 | https://forums.unrealengine.com/t/help-with-remaking-katamari-damacy-in-hd/68356 | `[社区实测]` | 200 全文 |
| R5 | https://www.gamer.ne.jp/news/201110270001/ ・ https://dengekionline.com/elem/000/000/419/419362/ | `[官方]` | 搜索摘要 |
| R7 | https://keymap.io/controls/once-upon-a-katamari/ | `[社区实测]` | 200 全文 |
| S2 | https://shookietea.github.io/reroll-notes/mechanics/boosts/boosts/ | `[数据挖掘]` | 200 全文 |
| D1 | https://shookietea.github.io/reroll-notes/bot-commands/ | `[数据挖掘]` | 200 全文 |
| D2 | https://shookietea.github.io/reroll-notes/Important%20Object%20and%20Size%20Milestones/ | `[数据挖掘]` | 200 全文 |
| S4 | https://github.com/squiddingme/RerollPatcher | `[数据挖掘]` | 200 README |
| S5 | https://raw.githubusercontent.com/misoelegant/rpm/master/rta-mod/reroll-rta-mod.asl | `[数据挖掘]` | 200 全文 |
| H1/H2 | http://www.honestgamers.com/12930/playstation-2/katamari-damacy/review.html | `[社区实测/媒体]` | 搜索摘要（正文片段） |
| N1 | https://www.nytimes.com/2004/10/14/technology/circuits/touches-of-weird-done-best-in-japan.html | `[社区实测/媒体]` | 搜索摘要 |
| B1 | https://archive.blitzbasic.org/forums/community-showcase/75777.html | `[社区技术]` | 搜索摘要 |
| S1 | https://steamcommunity.com/profiles/76561198008096580/recommended/848350/ | `[社区实测]` | 搜索摘要 |
| S6 | https://urawaza.in/psp/bokwatakata.htm | `[社区实测]` | 搜索摘要 |
| S7 | http://psmk2.net/ps2/soft_04/act/katak.html | `[社区实测]` | 200（Shift-JIS 已解码） |
| D3 | https://wikiwiki.jp/yawaka802/...（モノ/プラスドライバー） | `[社区实测]` | 搜索摘要 |
| D4 | https://itest.5ch.io/krsw/test/read.cgi/ghard/1575809255 | `[社区实测]` | 搜索摘要 |
| D5 | https://steamcommunity.com/id/kuroonehalf/recommended/1880620/ | `[社区实测]` | 搜索摘要 |
| U1 | https://discussions.unity.com/t/controllable-ball-physics/493994/1 | `[社区]` | 搜索摘要 |
| U3 | https://consto.uk/2018/10/07/bring-the-light | `[社区]` | 搜索摘要 |
| U4 | https://docs.godotengine.org/en/3.4/classes/class_kinematicbody.html | `[官方]`（引擎） | 搜索摘要（含 `floor_max_angle` 默认值 0.785） |
| U5 | https://forum.godotengine.org/t/how-to-make-the-character-face-the-plane-you-are-climbing-on/6736/2 | `[社区]` | 搜索摘要 |
| U7/U8 | https://docs.unity3d.com/2022.3/Documentation/Manual/class-CharacterController.html ・ .../class-Rigidbody.html | `[官方]`（引擎） | 仓库既有引用，本轮未重取 |

**取不到 / 屏蔽的**：katamari.fandom.com（Cloudflare 403，含 REROLL "物理更松、更有惯性" 的说法——**未采信**，只记在这里）、speedrun.com 论坛原帖（403，只用搜索摘要）、steamdb patch notes（ASN 被拒）、TCRF 块魂页（只返回 58 字节，未取得内容，**不作任何断言**）。

---

## 8. 一句话给设计

**原作不是"真物理球"，它是一颗运动学球 + 一套"速度门槛"的坡 + 一条"墙高≈球径"的爬墙规则 + 一个等效体积的碰撞盒。** 我们的球目前缺的不是"更真的物理"，而是**"墙接触"这条独立的支撑/状态通道**，以及把几何死锁（36.87°）换成速度门槛。

*本文所有引文均为短句摘引，用于核查；不代表授权或对原作的背书。*

---

## 9. 本地留档（可复核）

| 文件 | 内容 |
|---|---|
| `/tmp/orig_physics/kd_manual.txt` | 块魂(2004) 英文说明书 OCR（去空格归一化，23k 字符） |
| `/tmp/orig_physics/wlk_manual.txt` | We Love Katamari 英文说明书 OCR（去空格归一化，21k 字符；"CLIMB OVER OBSTACLES!!" 在此） |
| `/tmp/orig_physics/kd_manual.html.gz` / `wlk_chocr.html.gz` | 上述两份 OCR 的原始下载（archive.org） |
| `/tmp/orig_physics/patent.pdf` / `patent.txt` | US7402104B2 专利原文与 `pdftotext -layout` 提取（1058 行） |
| `/tmp/orig_physics/wlk.wiki` / `kd.wiki` | 两篇维基的 wikitext（用于核对 citation） |
| `/tmp/orig_physics/psmk2.html` | 日文攻略站（含 机ぬけ 记录） |

复核建议：先 `grep -n "climb" wlk_manual.txt`，再 `grep -n "predetermined velocity" patent.txt`，两条即可验证本文最关键的"墙高≈球径"与"坡是速度门"。

---

# 10. 视觉核心与粘附物布局（第 2 轮）

**触发**：对比图 `/tmp/visual_ref/named/`（原作 14cm0mm / 14cm9mm 两帧，各粘了 40 多件；我们的 `ours_unity_ball_with_props.png` 红球裸露约 2/3）。
**方法**：先目视三图，再把两帧按 2× 放大叠 50 px 网格（`/tmp/orig_physics/g_*.png`）量像素；文本证据沿用同一套分级。**本轮仍无实机**，所有像素量为 `[推断]`。

## 10.1 先量：两帧的几何

| 量 | 14cm0mm | 14cm9mm | 说明 |
|---|---|---|---|
| 簇外接框（源 px） | 545 × 520 | 585 × 550 | 网格 25 px 精度读数，含外凸物 |
| 取用直径 `D_px` | 532 | 568 | 两轴均值；非球形 + 透视 → **±8%** |
| HUD 直径 | 14.0 cm | 14.9 cm | 手册定义见 §10.2 |
| `D_px / HUD` | **38.0 px/cm** | **38.1 px/cm** | 两关不同、尺寸不同，比值几乎相同 → 视觉尺寸与 HUD **线性同标度** |
| 单件中位宽 | ~150 px（0.28 D） | ~160 px（0.28 D） | 蓝色口香糖盒；受透视压缩 |
| 单件范围 | 0.15–0.44 D | 0.13–0.53 D（"おてもと"细长） | 最大件是棕色纸箱（frame1）/ 长筷套（frame2） |
| 可见件数 | ~45（40–50） | ~50（40–55） | 逐件计数，边缘叠压处有歧义 |
| 裸露核心占比 | **0%**（目视 + 3× 中心裁切） | **0%** | 找不到任何"光滑球面"区域；全是多面体表面 |

`D_px/HUD` 在两帧一致只证明"视觉尺寸 ∝ HUD"，**不能**单独定出"视觉外径 = 多少倍 HUD"；下面用第二个锚点交叉估计。

## 10.2 原作到底有没有一个"看得见的核心球"？

**有核心模型，但不是纯色球；是一颗低模的"假杂物团"。**

| 证据 | 级别 | 内容 |
|---|---|---|
| KD 2004 英文说明书 "Viewing the screen" 节（OCR：`kd_manual.txt`） | `[官方]` | HUD 明确叫 **diameter**："current size — the current diameter of the katamari"；且"直径每到一个档位，画面外观会变" |
| US7402104B2 图 4A/4C、流程 S4/S54 | `[专利]` | 三个模型：显示模型 `Mp`（"球本体"= 父节点）、碰撞模型 `Mh`（等体积球的外接**盒**）、滚动模型 `Mr`（16+4 针）。S4 是"按 `Mr` 算完滚动后，用结果**移动显示模型 Mp**" |
| REROLL 模型资产（The Models Resource "Katamari" 分类，81 件） | `[数据挖掘]` | 文件是 `core_01.obj/.mtl/.png` … 至少到 `core_21` —— 成套的 "core" 模型，带贴图，不是单一球 |
| Katamari Forever 资源结构（VG Resource 论坛 2024） | `[数据挖掘]` | "The katamari models in the **CORE folder**"；即"块魂本体模型"在资源里就叫 CORE |
| 英文 fandom "Low Resolution Katamaris" 节（经 wayback；原站 403） | `[社区]` | KD/WLK/BK/KF 里滚到一定尺寸后，"往球心看会看到奇怪的**低多边形块魂**"；KD 分档 12m / 200m，12m 时是树和车的低模，300m 时是建筑 |

**读法**：核心不是"彩色塑料球"，而是**一颗预先做好的低模杂物团**（按尺寸分档换模型）。所以缝里露出来的东西"看起来还是杂物"，不会出现一块纯色。两帧里 0% 裸露，且中心 3× 裁切只见多面体，与"核心=低模杂物"一致（`[推断]`，置信度中）。

> 注意区分两件事：**核心存在且有模型**（证据较硬）；**核心半径比等效球小多少**（没有直接证据，见 §10.3）。

## 10.3 核心半径相对物理等效球：能定的和不能定的

能定的（`[官方]`+`[专利]`）：
1. HUD = 球的**直径**；
2. 球的体积 = 初始 + Σ 吸附物体积，直径由体积换算（`[专利]` 反复用 volume 更新、并按体积放大 `Mh`）；
3. 显示的"球本体"是**父节点**，吸附物是**子节点**（`[专利]`）。
→ 所以核心**随球变大而变大**，不存在"核心一直很小、全靠物件撑外形"这种设计（那会与体积模型矛盾）。置信度：高。

不能直接定的：核心网格半径是 1.0 r 还是 0.9 r。

用两个独立锚点做交叉估计：
- 锚点 A（件/球比）：原作的"可吸尺寸"约 **0.30 × 球直径**（`[数据挖掘]` 速通笔记 MAS2：6cm 吸糖、8cm 吸欧芹、12.7cm 吸袜子、16.5cm 吸番茄；与 10% 体积门推出的 0.46 上限相比，实物偏扁，实测约 0.3）。两帧里可见件中位 0.28 D。
- 锚点 B（覆盖）：以 0.28–0.30 D 的件、~45 件覆盖球面，几何上刚好铺满一层（需要 ≈40–50 件），与观测件数吻合。

→ 若"可见件中位 ≈ 可吸上限"，则 `D_vis ≈ (0.28/0.30)·D_HUD ≈ 0.93·D_HUD`；若可见件比可吸上限略小（你能吸的比已吸的大），则 `D_vis ≈ 1.0–1.1·D_HUD`。
**综合：`D_vis ≈ D_HUD`，不确定度 ±15%。** 也就是说：

> **视觉外壳的外径 ≈ HUD 直径；核心半径 ≈ 0.45–0.55 D_HUD ≈ 0.9–1.1 r_phys，最佳猜测 0.95 r_phys。没有证据支持"视觉核心显著小于碰撞球"。**

`[推断]`，置信度：中。误差主要来自簇的非球形（外凸/内凹互相抵消）与透视（球心低于相机，纵向被压缩）。

## 10.4 粘附物是怎么摆的：接触位姿 + 冻结，不做径向外推

| 问题 | 结论 | 级别/依据 |
|---|---|---|
| 放在接触点原位，还是被吸到球面？ | **接触点原位（或其附近）**：专利原文 "stick the placement object E to the ball B in the contact position or the vicinity of the contact position"；没有"拉到球面上"的步骤 | `[专利]`，置信度高 |
| 球变大后旧物跟着外推吗？ | **不外推**：父子挂接后只记录一次位姿；球体积变大 → 核心表面往外长 → 旧物被"吞"进去 | `[专利]`（无重定位步骤）+ `[推断]`，置信度中高 |
| 有没有"内部物件被移除/隐藏"的明确证据？ | **隐藏有明确依据**：专利按 display level 判"太小了不用画"；**删除没有证据** | `[专利]` 高 / 删除 `[未知]` |
| 那"小件很快消失"怎么解释？ | 三种叠加：① 被长大的核心/后吸的物件挡住；② display cull；③ （可能）被移出列表——只有 ①② 有依据 | `[专利]`+`[社区实测]` |
| 物件之间重叠、穿插怎么办？ | **允许且普遍**：吸附物不做互相碰撞（球只有一个碰撞代理），两帧里盒子/树/球明显互相穿过 | 观测 + `[专利]` 推论，置信度高 |

社区侧的旁证（都是"消失/露出底部"的抱怨，反推"旧物被吞"）：日文 wiki "球一大，小件马上消失"；5ch "卷进去的东西消失、露出块底"；OUAK 被老玩家批"物件 30 秒就消失一片，只剩十几个"。

**这套机制的副作用**（也是原作观感的来源）：球表面上永远是"最近一层"物件，深度参差——新吸的在最外层、半嵌；稍早的陷进去 1/3～1/2；更早的被吞掉。两帧里"树只露树冠、盒子只露一角"就是这么来的。

## 10.5 我们那张为什么红球裸露

| 指标 | 原作两帧 | 我们（`ours_unity_ball_with_props.png`） |
|---|---|---|
| 球径（px） | 532 / 568 | ~210 |
| 可见件数 | 40–55 | ~8–9 |
| 单件 / 球径 | 中位 0.28（0.13–0.53） | 约 0.40–0.62（椅子、梯子、长凳都很大） |
| 估算覆盖 | ≥95% | ~35% |
| 裸露核心 | 0% | ~65% |

覆盖的粗估（`[推断]`，只用于排优先级，别当公式用）：

```
外壳覆盖率 ≈ 1.2 · N · (s/D)² · e        # s=单件特征尺寸, D=球径, e=贴面程度(相切≈1, 朝外支棱≈0.3–0.5)
```

原作代进去：`1.2 × 45 × 0.28² × 0.8 ≈ 3.4` → 饱和（>1，全覆盖），与观测一致。
我们代进去：`1.2 × 8.5 × 0.5² × 0.4 ≈ 1.0` —— 公式给"刚好铺满"，实测只有 ~35%。**公式在我们这种"大件 + 相切"的极端情形失效**：大件只在一点接触球面，真实遮挡面积远小于其横截面，所以别用公式反推，直接用 §10.5 表的实测值。

**结论（给优先级）**：
1. **核心外观**：红色纯色球是最大的观感差异来源（原作核心=低模杂物，永不出现纯色面）。改材质/换成"杂物团"性价比最高。
2. **内容比例**：可吸件应当 ≤ ~0.35 D，且每个尺寸档要能凑出 40+ 件；我们现在是 0.5–0.6 D × 8 件，等于"永远秃"。
3. **贴附姿态**：原件是"接触即冻结"，允许深埋与互相穿插；我们现在像"贴在球面上的贴纸"，太表浅、太平齐。
4. **核心半径**：**不是主因**。0.95 r 与 1.0 r 的差别在 40 件覆盖下肉眼几乎看不出。

## 10.6 建议参数（全部 OUR design，附置信度）

| 项 | 建议 | 置信度 | 依据 |
|---|---|---|---|
| HUD 报什么 | **物理等效球直径**（与吸附门同一标量） | 高 | `[官方]` 手册 "diameter" |
| 核心半径 | `0.95 × r_phys`（0.9–1.0 都可） | 中 | §10.3 交叉估计 |
| 核心外观 | **低模"杂物团"贴图/多层 impostor**，禁止纯色光滑球；可按尺寸分 2–3 档换模型 | 中高 | `[数据挖掘]` core_01–21 / CORE 文件夹；`[社区]` 低模块魂 |
| 核心是否随体积长大 | 是（`r = (3V/4π)^(1/3)`） | 高 | `[专利]` 体积模型 |
| 吸附位姿 | 记录接触瞬间的**世界位姿**，之后冻结为局部位姿 | 高 | `[专利]` 原文 |
| 径向外推 | **不做**（推力=0） | 中高 | `[专利]` 无重定位 + `[社区]` 旧物被吞 |
| 埋入偏置 | 沿接触法线向内 `0.1–0.2 × 件尺寸`（保险起见，避免"贴纸感"） | 中 | `[推断]`（原作由盒子碰撞自然产生） |
| 吸附物互撞 | 不做（允许穿插） | 高 | 观测 + 单一碰撞代理 |
| 深处 cull | 件心 < `0.85 × r_core` 时不渲染（数据保留） | 中 | `[专利]` display level |
| "粘附圈" | **不要单独设一个半径**；用碰撞代理 + 10% 体积门。若必须留，取 `1.0 r`（盒角等效最多 1.7 r） | 中 | `[专利]` 碰撞盒 |
| 内容规则 | 每档可吸件 `0.2–0.35 D`，每层 ≥40 件 | 高 | `[数据挖掘]` MAS2 尺寸表 + 两帧计数 |

## 10.7 必须我们自己设计的（原作没有可抄的数）

- 核心缩小系数（0.9 / 0.95 / 1.0）——原作只给了"直径"定义，没给显示网格半径。
- 是否给一个"最小外推"防止我们这种低件数场景秃掉（原作靠高件数，不靠外推）。
- 埋入深度偏置（原作是碰撞盒的副作用，不是显式参数）。
- cull 阈值（0.8/0.85/0.9 r）。
- 核心材质的具体做法（低模杂物团贴图 / 壳层 impostor / 只在缝里露出的第二层小物件）。
- 我们的"粘附圈"是否要与碰撞代理分离（原作不分离）。

## 10.8 本章来源

| ID | URL | 级别 | 用途 |
|---|---|---|---|
| K5 | KD 2004 说明书 OCR（同 §7 K1）`kd_manual.txt` | `[官方]` | "current diameter of the katamari" |
| P9 | US7402104B2（同 §7 P1） | `[专利]` | 显示/碰撞/滚动三模型；接触位姿挂接；display level |
| M1 | <https://models.spriters-resource.com/pc_computer/katamaridamacyreroll/asset/317992/>（正文 403，靠搜索索引的资产清单） | `[数据挖掘]` | `core_01…core_21` 成套 core 模型 + 贴图 |
| M2 | <https://archive.vg-resource.com/archive/index.php/thread-43098.html> | `[数据挖掘]` | KF 的 "CORE folder" 放块魂本体模型 |
| M3 | <https://katamari.fandom.com/wiki/Katamari>（wayback 2026-02-21）+ 搜索索引 diff 版本 | `[社区]` | "低多边形块魂"在球心，按尺寸分档（12m/200m/300m） |
| M4 | <https://shookietea.github.io/reroll-notes/MAS2/> | `[数据挖掘]` | 6cm 吸糖 / 8 欧芹 / 12.7 袜子 / 16.5 番茄 → 可吸件 ≈0.3 D |
| M5 | <https://shookietea.github.io/reroll-notes/bot-commands/> | `[数据挖掘]` | `!size` 单件标量、`!calc` 体积合成 |
| M7 | <https://w.atwiki.jp/gcmatome/pages/10044.html> | `[社区实测]` | OUAK 的"当たり判定与外观不符"抱怨（显示≠碰撞的旁证） |

> 图片量测的原始文件：`/tmp/orig_physics/g_original_katamari_14cm0mm.png`、`g_original_katamari_14cm9mm.png`（2× + 网格）、`c1.png`、`c2.png`（中心 3× 裁切）。复核方式：看网格图数格子。

## 11. REROLL 数据挖掘资料（2026-11 轮）

> 详细数字与表见 `/tmp/orig_physics/reroll_numbers.md`；引擎/数据模型见 `/tmp/orig_physics/reroll_datamodel.md`。
> 本轮**只读公开数据表与公开源码**，未下载任何模型/贴图/图片包/场景文件。

### 11.1 可读性

| 源 | 状态 |
|---|---|
| REROLL Dump（20 个工作表，含关卡表/实例表/类型表/penalty/swirl） | **可读** `[数据挖掘]` |
| 1438 物件表（1436 行，可吸尺寸文本） | **可读** `[数据挖掘]` |
| Google Docs 增长研究（50 行） | **可读** `[社区实测]` |
| speedrun.com 1438 帖 | **403**，未取得；不引用其中数字 |
| RerollPatcher / KatamariMods / rpm | **可读** `[源码]` |
| noclip `bin.ts` | 可读（PS2 原版，非 REROLL） |

### 11.2 关卡参数（起始/目标/上限 是三件事）

`[数据挖掘]` 单位 cm，与 HUD 同量纲（星座关小数：22.2 / 13.9 / 55.5 / 99.9 / 111.1 / 151.5，与 fandom 的 "22cm2mm / 13cm9mm…" 完全对上 → 互相验证）。

| 关卡 | 起始 | 目标 | 上限 | 多重 | 时限 | 区域 | 实例 |
|---|---|---|---|---|---|---|---|
| MAS1 | 5 | 10 | 30 | 2× | 4 | 1 | 654 |
| MAS2 | 5 | 20 | 40 | 4× | 7 | 1 | 912 |
| MAS3 | 10 | 50 | 140 | 5× | 9 | 1 | 1421 |
| MAS4 | 5 | 100 | 150 | 20× | 10 | 4 | 1678 |
| MAS5 | 10 | 150 | 300 | 15× | 13 | 2 | 1431 |
| MAS6 | 50 | 300 | 500 | 6× | 11 | 1 | 1431 |
| MAS7 | 50 | 600 | 1200 | 12× | 12 | 2 | 1956 |
| MAS8 | 10 | 1200 | 2500 | 120× | 18 | 4 | 3132 |
| MAS9 | 50 | 3000 | 6000 | 60× | 20 | 3 | 2811 |
| Moon | 100 | 30000 | 90000 | 300× | 25 | 5 | 3955 |

**对我们的直接含义**：我们的 2.20 m 是"上限"角色，目标只有 1.25 m；起始 0.30 m 相当于原作的中期关（MAS6 起 50 cm 已经算大）。`GAME_TIME` 单位**未证实**（数值 4…25，教程 90、Eternal 30 像哨兵）。

### 11.3 物件体量：每档到底要多少件

`[数据挖掘]` 全作 28,698 实例 / 19 关；类型 1450 个（可收集 1416）。
按球径档（`D_req = (60V/π)^(1/3)`）统计实例数，全作并集：

| 档 (m) | 0.30–0.40 | 0.40–0.50 | 0.50–0.60 | 0.60–0.80 | 0.80–1.00 | 1.00–1.25 | 1.25–1.60 |
|---|---|---|---|---|---|---|---|
| 实例 | 1131 | 974 | 967 | 1824 | 1190 | 1224 | 2147 |

单关视角（更可用）：MAS3 在 0.3–0.4 有 **111** 件、MAS8 有 **177** 件、MAS9 有 98 件；一关覆盖 0.30→1.25 m 需要 **600–900 个可吸实例**。

→ 对照我们：整关 127 件、`c=0.0041` 下 99/127 件被量化成 1 u（=4.1 L，开局一件就把球推到 0.89 m）。**数量级差 5–7 倍，且我们的单件权重错了。**

### 11.4 增长机制：原作不守恒

`[社区实测]`（Google Docs）+ `[数据挖掘]`（penalty 列）：
1. 体积 → 球径（HUD 显示直径），换算后 **+0.02 cm**。
2. 拾取门 `V_ball ≥ 10·V_item`，门槛直径**向下取整到 mm**。类型表自洽验证：Spatula V=0.0006121952 → 门槛 226 mm，对应球体积比 10V 低 0.13%。
3. **拾取只给一部分体积**：`Pickup Volume Penalty` 默认 **0.8**（1198/1436），另有 0.5(91)/0.6(40)/0.4(21)/0.01(19)/0.66/0.26/0.08；`katVolumePenalty` 表复现同一组数（默认 0.8）。
4. Doc 另称比例**随球径分段下降**（MAS1: 5cm .8 → 15cm .4 → 20cm .16；MAS9 到 55 m 降到 .16），并把"大树 −50%"标为逐件惩罚、把"群组件"标为高离群。

两种读法的关系未定（`[推断]`：多半是"逐件 penalty 的混合效应"），但共同点确定：**默认损耗 20%，被罚件损耗 50–99%，且接近目标时增长显著变慢**。

### 11.5 模拟机制

`[源码]`：
- **固定 1/30 s 步长**：`deltaFrame >= 1/30` → `-= 1/30` → `mSimulation.DoTick(1/30f)`（IL 级证据，RerollPatcher README 同述）；画面另插值到 60+。
- 物理在**原生 `PS2KatamariSimulation.dll`**；托管侧只是外壳。球径在该 DLL 里是一个 **float（cm）**，ASL 直接读 `dll+0x16d77c`，关卡号 `dll+0xff104`。
- 每物件每 tick 拿一个 **Matrix4x4**：`GetPropMatrixFromSimulation(monoControlIndex)`；物件是 `AttachableProp`（`monoControlIndex / isMultiMesh / monoRotation[2] / mIsAttachedToKatamari`）。
- Practice Mod 暴露：**球的碰撞网格**（F4 可视化）、**物件的包围盒 + vault 点**（F5）、**内部速度值**（F6 overlay）。
- 换区/换档 = **2D 精灵特效**（`gYm_Game2dRequestWhirlpool(time, scaleS, scaleE, angleS, angleE)`，`time` 是 30 Hz 帧数），**不重载场景**。

**拿不到**：速度上限、加速度、转向、坡度、攀爬阈值——公开源码里**一个数字都没有**（RerollPatcher 无物理代码，KatamariMods 只改帧率门控，原生 hook 仓库非公开）。

### 11.6 与 CONTACT_MODEL_V2 的对照

| 参数 | 原作 | 我们 | 判定 |
|---|---|---|---|
| 10× 体积门 | ✅ 确认 | ✅ | **一致，保持** |
| HUD = 直径 | ✅ | ✅ | 一致 |
| 30 Hz 定步长 | ✅ | 每帧 Update | **应改累加器** |
| 碰撞形状 | 网格 + 物件包围盒 + vault 点 | 球 + OBB 接触 | 原版不是解析球 |
| `H_cap=1.0·2r` | 手册 "walls about the same size" | 同 | 锚点一致，仍是推断 |
| `v_req / 坡度 / T_climb / dash 1.6×` | 无数据 | 设计值 | **无法校准** |
| 增长 | `0.8·V`（含 0.01–1.0 penalty） | `V` 守恒 + `c=0.0041` 量化 | **必须改** |

### 11.7 校正建议（给 lead）

1. **每档件数**：单关每 0.1 m 档 **60–180 件**（我们 127 件是"总数"）；先做到 0.30–1.25 m 段 **600–800 实例**再谈手感。
2. **增长**：把 `ΔV = V` 改成 `ΔV = 0.8·V`（可选再乘"接近目标时 0.16–0.4 的拖尾"），并把 `c` 降到 0.0005 或**取消单位量化**——现在开局一件 4.1 L 是量级错误。
3. **关卡参数**：把"目标(1.25 m)"与"上限(2.20 m)"分开配置，时限按原作 4–10 分钟量级重估；起始 0.30 m 相当于原作中后期关，若要保持"第一关"手感，起始应下探。
4. **攀爬**：保持 `H_cap=1.0·2r`，但明确它是推断；**不要**拿"原作数值"背书。
5. **步长**：Udon 侧改固定 1/30 累加器（顺带解决低帧率下速度变慢）。

### 11.8 本章来源

| ID | URL | 级别 | 用途 |
|---|---|---|---|
| R1 | <https://docs.google.com/spreadsheets/d/1zg9WRiJamj6iUKuL2iTY1BhXfhD0HwLHwCNO-9HNe6s/>（`export?format=csv`，20 表） | `[数据挖掘]` | 关卡/实例/类型/penalty/swirl |
| R2 | <https://docs.google.com/document/d/1lDAal300mmCil_Q57lffuOVuX2ztVnB1ccMyMYgPa_A/>（`export?format=txt`） | `[社区实测]` | 体积→直径、10× 门、分段比例 |
| R3 | 1438 物件表（`export?format=csv`，1436 行） | `[数据挖掘]` | 可吸尺寸文本 |
| R4 | <https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs> | `[源码]` | 1/30 步长、`DoTick`、whirlpool、字段名 |
| R5 | <https://github.com/squiddingme/RerollPatcher/blob/master/Patches/PropInterpolationPatch.cs> | `[源码]` | `GetPropMatrixFromSimulation`、`AttachableProp` |
| R6 | <https://github.com/misoelegant/rpm/blob/master/rta-mod/reroll-rta-mod.asl> | `[源码]` | `size@0x16d77c`、`levelID@0xff104` |
| R7 | <https://github.com/misoelegant/rpm/blob/master/il-practice-mod/README.md> | `[源码]` | 碰撞网格/包围盒/vault 点/速度 overlay |
| R8 | <https://github.com/magcius/noclip.website/blob/master/src/KatamariDamacy/bin.ts> | `[源码]` | PS2 物件/路径/碰撞结构（格式参考） |
| R9 | speedrun.com 1438 帖 | **403，未取得** | — |
