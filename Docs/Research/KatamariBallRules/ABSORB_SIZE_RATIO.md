# 球要多大才能吸起多大的东西：原作的拾取判定

**调研日期：2026-11-15 UTC（项目时区 2026-11-15 Asia/Shanghai）。** 本文回答一个问题：原作的「可吸上限」到底是物件的**最长边**、**直径**还是**体积**，判定式长什么样，以及我们 `球语义直径 × 1.0` 该改成多少。
**边界：本轮没有实机、没有录像、没有拆包。** 所有结论来自公开文字；凡属推断都标 `[推断]`，凡属换算都写出算式。

## 0. 结论速览

1. **原作不是「物件尺寸 ≤ 球直径 × k」。** 判定量是**体积**：球的体积必须 ≥ 物件体积的 **10 倍**；等价写法是 `球直径 ≥ 2.154 × 物件的体积等效直径`。判定式是相对的，没有绝对阈值表。
2. 游戏里每个物件的「Size to roll up」不是它的物理尺寸，而是**由该物件体积反算出来的、能滚起它所需的最小球径**（向下取整到 mm）。所以它看起来像「尺寸表」，其实是「门槛表」。
3. 因此「最长边 / 球直径」这个比值**不是一个常数**，而是随物件形状变化：粗壮物件只有 0.4–0.8，细长物件可以到 1.5–4。**我们现在的 1.0 对粗壮物件偏松、对细长物件偏紧**，用户「感觉偏低」的直觉来自后者（铅笔、棒球棒、扫帚那一类）。
4. 换算到「物件的包围盒体积」上，规则可以写成一行闭式：**`a·b·c ≤ 0.0524 × D³`**（D = 球直径）。这是我认为该落地的形式。

## 1. 直接回答：50 cm / 1 m / 2 m 的球能吃什么

以「球体积 = πD³/6，物件体积 ≤ 球体积/10」计算（推导见 §2、§4）：

| 球直径 | 球体积 | 可吸物件的体积上限 | 可吸物件的体积等效直径上限 | 具体例子（括号内为游戏内门槛值） |
|---|---|---|---|---|
| 5 cm | 65.4 cm³ | 6.5 cm³ | 2.3 cm | 铅笔（4.3 cm）、图钉（3.1 cm）、大头针 |
| **50 cm** | **65.4 L** | **6.5 L** | **23.2 cm** | **美式足球（49.4）、棒球棒（49.8）、餐盘（30.8）、童用提桶（44.7）** |
| **1 m** | **524 L** | **52.4 L** | **46.4 cm** | **扫帚（66.3）、竹扫帚（90.8）、钓竿（66.5）、炒锅（96.1）** |
| **2 m** | **4.19 m³** | **419 L** | **92.8 cm** | **自行车（188.8）、冲浪板（138.5）、遮阳伞（135.3）、洗衣机（235.7 吃不了）** |
| 2.72 m | 10.6 m³ | 1.06 m³ | 1.26 m | 自动售货机（272.1） |
| 5.40 m | 82.6 m³ | 8.26 m³ | 2.51 m | 小型汽车（540.3） |
| 6.41 m | 138 m³ | 13.8 m³ | 2.98 m | 电线杆（641.1） |
| 9.63 m | 468 m³ | 46.8 m³ | 4.47 m | 电线（963） |

门槛值来源：游戏内 Collection 的「Size to roll up」，经 [StrategyWiki 物件全表](https://strategywiki.org/wiki/Katamari_Damacy/Objects)（KD）与 [We ♥ Katamari/Stationary](https://en.wikibooks.org/wiki/We_%E2%99%A5_Katamari/Stationary) 等分类页转录；关卡内具体门槛见 [REROLL 速通笔记的各关 Sizes 表](https://shookietea.github.io/reroll-notes/MAS7/)。
各关起始球径（MAS1 5 cm / MAS3 10 cm / MAS6 50 cm / MAS9 50 cm / Moon 1 m）取自 [Katamaripedia · List of Katamari Damacy Stages](https://katamaripedia.miraheze.org/wiki/List_of_Katamari_Damacy_Stages)，并用 [Reroll Dump 关卡表的 `START_DIAMETER` 列](https://docs.google.com/spreadsheets/d/1zg9WRiJamj6iUKuL2iTY1BhXfhD0HwLHwCNO-9HNe6s/) 直接核对过（按关卡表顺序为 5 / 5 / 5 / 10 / 10 / 50 / 50 / 10 / 50 / 100，与 wiki 一一对应；注意该表的 `MISSION_ID` 顺序与「Make a Star」编号在 3/4 两关互换）。
证据等级：**wiki 转录 + 数据挖掘对照**（关卡尺寸已是第一方数据；单个物件的门槛值仍只有社区转录）。

## 2. 判定量是什么：体积，不是最长边

### 2.1 数据挖掘给出的判定式

[REROLL 速通社区《Katamari Damacy Reroll Size Growth》文档](https://docs.google.com/document/d/1lDAal300mmCil_Q57lffuOVuX2ztVnB1ccMyMYgPa_A/)（Google Docs，正文可用 `mobilebasic` 视图读到）写得非常直接：

> "To pick up an object, the Katamari needs to be roughly **10 times bigger (in terms of volume)** than that object. To check for this, each kind of object has **a volume**, and the size required to pick up that object is the smallest Katamari size that would have 10 times that volume.
> The required size is rounded down to the nearest millimeter… For example, to pick up the smallest object (Ant), you would really only need to be about 8.8 times bigger than it, instead of 10 times."

同一文档还说明：球本身有初始体积，**显示在 HUD 上的尺寸是由体积算出的球直径**（MAS1 实际起始 5.02 cm），拾取后按「物件体积 × 某个比例」加进球的体积，该比例是**按关卡分段线性**的。

- 证据等级：**数据挖掘**（社区从游戏数据反推，给出方法与可检验数字，但不是官方说明、也不是逐字节拆包记录）。
- 换算：`D_required = (6 × 10 × V_item / π)^(1/3) = 2.154 × (6 V_item/π)^(1/3) = 2.154 × d_eq`。
- 「向下取整到 mm」解释了为什么最小的物件（蚂蚁）实际只需 8.8 倍而不是 10 倍。

### 2.2 用九个物件反算体积，验证这个 10×

若 `门槛 = 2.154 × d_eq`，则反推物件体积 `V = 0.05236 × 门槛³`。拿现实物体的体积对照：

| 物件 | 游戏门槛 | 反推物件体积 | 现实体积（估算） | 偏差 | 结论 |
|---|---|---|---|---|---|
| 火柴盒 | 6.9 cm | 17.2 cm³ | 5.5×3.5×1.0 ≈ 19 cm³ | −11% | ✅ |
| B 铅笔 | 4.3 cm | 4.16 cm³ | 17.5 cm 长 × ~0.3 cm² ≈ 4–5 cm³ | −15% | ✅ 细长物件的体积很小 |
| 餐盘 | 30.8 cm | 1 530 cm³ | Ø26 × 2.2 cm（含盒）≈ 1 490 cm³ | +3% | ✅ |
| 美式足球 | 49.4 cm | 6.31 L | 标准 5 号球 ≈ 5.5 L | +15% | ✅ |
| 垃圾桶（Seagull Park） | 130.2 cm | 116 L | 60 cm × Ø50 ≈ 117 L | −1% | ✅ |
| 萝卜（大根） | 76.9 cm | 23.8 L | 巨型萝卜 60 cm × Ø24 ≈ 27 L | −12% | ✅ |
| 自动售货机 | 272.1 cm | 1.06 m³ | 柜体 1.83×1.00×0.75 = 1.37 m³ | −23% | ✅ 柜体不填满包围盒 |
| 小型汽车 | 540.3 cm | 8.26 m³ | 4.0×1.7×1.5 = 10.2 m³ | −19% | ✅ |
| 表亲（可滚起的角色） | 158 cm | 207 L | 圆胖角色 ≈ 200–360 L | −40%～0% | ⚠️ 最粗的一档，区间宽 |

九个里八个落在 ±25% 内，且**细长物件的偏差方向和「按体积算」一致**（铅笔只有 4 cm³）。这比「按最长边算」能解释得多：若按最长边，17.5 cm 的铅笔门槛应该 ≈ 17.5，而实际是 4.3。
证据等级：**推断**（由 2.1 的公式 + 现实尺寸反算；现实体积是我按常见规格估的，不是测量值）。

### 2.3 三条独立旁证都指向「体积/包围盒」而不是「最长边」

1. **Katamari Wiki（Fandom）对判定量的描述**（[Katamari Damacy 条目](https://katamari.fandom.com/wiki/Katamari_Damacy)，wikitext 原文）：
   > "Objects are drawn inside of a '**bounding box**,' a three-dimensional container, which tells the game how large the object is, **including any empty space** that may be in it. For this reason, complex items like an **open umbrella are more difficult to pick up** than otherwise 'larger' objects, such as a car. This can go as far as defying common sense, as **a pencil can be easier to pick up than a smaller object; since it is long and narrow, it has less volume** than something of similar size to the Katamari, such as an eraser."
   这条同时给出「包围盒含空腔」和「细长 ⇒ 体积小 ⇒ 好吸」两个方向。证据等级：**wiki 反推**（Fandom 未给引用）。
2. **游戏内数据自证**：铅笔 4.3 cm < 橡皮 8.1 cm（两者同在 Living Room/Stationery 分类）。铅笔比橡皮长得多却更好吸——按最长边不可能成立，按体积成立。
3. **玩家观测**（弱证据，但与上面一致）：r/hexbear 帖子 [*Katamari Damacy theory in why certain objects seem easier/harder to pick up*](https://piefed.zip/c/games@hexbear.net/p/1608193/katamari-damacy-theory-in-why-certain-objects-seem-easier-harder-to-pick-up-than-i-would-e) 的结论是 "It's the **volume of the collision box** that matters"，并观察到「又长又细的、奇形怪状的、很扁的」偏容易，「圆的、活的」偏难。

## 3. 比值表：球直径 vs 能滚起的最大的东西

「尺寸/球径」一列用该物件的**现实最长边**除以球直径。注意同一个球径下这一列会跨一个很宽的范围，这正是「比值不是常数」的表现。

| 球直径 | 该阶段能滚起的最大的东西（举例） | 该物件真实尺寸 | 尺寸/球径 | 来源 / 等级 |
|---|---|---|---|---|
| 5 cm（MAS1 起始） | B 铅笔（门槛 4.3） | 17.5 cm 长 | **3.5×** | 门槛：game 内表（wiki 转录）；尺寸：常识 |
| 50 cm（MAS6/MAS9 起始） | 棒球棒（门槛 49.8） | 85 cm 长 | **1.70×** | 同上 |
| 50 cm | 美式足球（门槛 49.4） | Ø22 cm，5.5 L | **0.44×** | 同上 |
| 50 cm | 餐盘（门槛 30.8） | Ø26 cm | 0.52× | 同上 |
| 50 cm | 收音机 / 一盘食物 / 鞋 / 宠物 | — | — | [GameSpy 2004 预览](https://web.archive.org/web/20080720044247/http://ps2.gamespy.com/playstation-2/katamari-damashii/504503p1.html)（**官方口径的媒体稿**，见 §3.1） |
| 1 m | 扫帚（门槛 66.3） | 130 cm 长 | **1.30×** | 门槛：game 内表；尺寸：常识 |
| 1 m | 竹扫帚（门槛 90.8） | 150 cm 长 | 1.50× | 同上 |
| 1 m | 钓竿（门槛 66.5） | 180–250 cm 长 | **1.8–2.5×** | 同上（钓竿长度的下限取值） |
| 1 m | 炒锅（门槛 96.1） | Ø35 cm | 0.35× | 同上 |
| 2 m | 冲浪板（门槛 138.5） | 200 cm | **1.00×** | 同上 |
| 2 m | 自行车（门槛 188.8） | 175 cm | 0.88× | 同上 |
| 2.72 m | 自动售货机（门槛 272.1） | H183 cm | 0.67× | 同上 |
| 5.40 m | 小型汽车（门槛 540.3） | 420 cm | 0.78× | 同上 |
| 6.41 m | 电线杆（门槛 641.1） | 1 100 cm | **1.72×** | 同上 |
| 9.63 m | 电线（门槛 963） | 数十米 | **3–10×** | 同上（MAS8 表，见 §3.2） |

### 3.1 一处官方口径的锚点（以及它的单位歧义）

GameSpy 2004-04-08 的预览（采访 Namco 高层）写：

> "As the prince's ball picks up more bric-a-brac, it grows **in volume** and its gravity increases. **By the time the ball is one circumferential half-meter, it can pick up radios, plates of food, shoes, and pets.**"
> "When your ball is only a few centimeters in diameter, and **you cannot pick up anything larger than a spoon**…"

- 「a few centimeters ⇒ 只能吸汤匙」与门槛表一致（汤匙 8.1 cm）。
- 「circumferential half-meter」= 周长 0.5 m ⇒ 直径 16 cm。但 16 cm 的球吃不了餐盘（门槛 30.8）、吃不了鞋和宠物（门槛 50–60）。**按门槛表反推，这里更可能是「直径 0.5 m」的笔误**。我按 50 cm 直径理解，并把这个歧义记在 §7。
- 证据等级：**官方（媒体稿转述）**，但单位表述不清，只作旁证。

### 3.2 关卡尺度的门槛表（速通社区维护，数据来源同 §2.1）

[REROLL 速通笔记 · Important Object and Size Milestones](https://shookietea.github.io/reroll-notes/Important%20Object%20and%20Size%20Milestones/) 与各关页（如 [MAS7](https://shookietea.github.io/reroll-notes/MAS7/)）给出每关「多大球能滚起什么」，例如 MAS7：木箱 97.8 cm、自行车 188.8、自动售货机 272.1、门 328.6、小树 418、汽车 540.3、轻型卡车 544.5。同一站的[机器人命令页](https://shookietea.github.io/reroll-notes/bot-commands/)把每个物件的这个数字直接叫作 **pickup size**，并说明「若球的初始尺寸等于某物件的 pickup size，就可以用物件名代替数字」——即这个数是**球径门槛**，不是物件尺寸。这一条是理解整件事的钥匙。

## 4. 把体积门换算成「最长边 × k」

设物件包围盒三边 `a ≥ b ≥ c`，`d_eq = (6abc/π)^(1/3) = 1.2407·(abc)^(1/3)`。门槛 `D = 2.672·(abc)^(1/3)`，于是

```
k ≡ a / D = (a²/(b·c))^(1/3) / 2.672
```

| 物件长宽高 | k（= 可吸物件最长边 / 球直径） | 直觉 |
|---|---|---|
| 1 : 1 : 1（立方） | **0.37** | 球要长得比它大得多才吸得动 |
| 2 : 1 : 1（砖） | 0.59 | |
| 3 : 1 : 1 | 0.78 | |
| 5 : 1 : 1（扫帚／钓竿级别） | **1.09** | 约等于我们现在用的 1.0 |
| 10 : 1 : 1（铅笔级别） | **1.74** | 比球长很多也能吸 |
| 20 : 1 : 1 | 2.76 | 电线那一档 |

**读法**：我们现在的 `1.0` 恰好对应「长宽比约 4.5:1」的物件。对更粗的物件原作比我们严，对更细的物件原作比我们松。所以「把 1.0 整体调大」会在**错误的位置**变宽松——它会让 50 cm 的球提前吸起 70 cm 的方箱（原作要 `D = 2.672 × 70 = 187 cm` 的球才吸得动一个 70 cm 的立方箱），却仍然吸不起 85 cm 的棒球棒（只需 49.8 cm 的球）。两头都不对，说明错的是口径不是系数。

反过来，`k = 0.8` 只校准了粗壮物件，代价是把扫帚、钓竿、棒球棒那批「原作里最像『吃了比自己大的东西』」的物件全部关掉。

**这是我把推荐落在「改判定量」而不是「改系数」的原因。**

## 5. 反向证据

**「不能吸比自己大的」这一侧：**

- [StrategyWiki / Fandom 的机制段](https://katamari.fandom.com/wiki/Katamari_Damacy)：*"Objects that are smaller than the katamari will stick to it when the player comes into contact with them, **while greater objects can be hurdles**; colliding at high speed with any may cause objects to fall off the katamari."* 等级：wiki。
- [英文维基 Katamari 条目](https://en.wikipedia.org/wiki/Katamari)：*"**If the katamari is only barely able to pick a creature up, the player along with the katamari will be knocked away**; if the creature is not rolled up in time, the creature will run off and escape."* 说明门槛是硬的、而且刚过线时会被弹开——支持「门槛是一个确定的比较，不是连续概率」。等级：百科（Tier 3）。
- 撞到过大的东西会掉件：同上 *"If players are attacked or crash into walls or large objects, the katamari will lose items."*

**「能吸明显比自己大的」这一侧：**

- Fandom 同一段：*"The game uses size, weight, and surface area to determine if an object will stick… This allows **slender objects, such as pencils, that are wider than the katamari**, to be picked up"*（另一版本写作 *"longer than the katamari is wide"*）。等级：wiki。
- 门槛表自身：电线（963 cm 门槛）在数十米长的电线面前，球明显更短；铅笔 4.3 cm 门槛 vs 17.5 cm 铅笔（4×）。等级：wiki 转录的游戏内数据。
- [HEXUS 的《We Love Katamari》评测](https://hexus.net/gaming/reviews/ps2/4579-we-love-katamari-ps2/?page=3)：*"say you start off with a **1m diameter** Katamari, you can **only roll up stuff roughly half that size**"*。这条常被当成「k≈0.5」的证据——它其实和体积门对**粗壮**物件的预测（0.37–0.6）吻合得更好，而不是和 1.0 吻合。等级：玩家/媒体观测（Tier 3），但它和 §4 的表是独立收敛的。

## 6. 结论与推荐

**结论（置信度：中高）**：原作判定式是 `V_ball ≥ 10 × V_item`，即 `球直径 ≥ 2.154 × 物件体积等效直径`；「Size to roll up」是这个门槛的显示值，不是物件尺寸。没有绝对阈值表，也没有「最长边 × k」这种形式。

**推荐（按优先级）**：

1. **首选：把尺寸门换成体积门。** 用包围盒三边即可写成无需开根号的闭式：
   ```
   可吸 ⟺ a·b·c ≤ 0.05236 × D³          （0.05236 = (π/6)/10）
   ```
   等价的、更好读的写法是 `d_eq ≤ D / 2.154`。若每件道具有 authored 的 `volume_m3`，就用它替代 `a·b·c`（更准，且不会把「带空腔的伞」算便宜）。
2. **绝对上限要一起改成体积口径**：现在的 `min(8 m, …)` 是米级长度上限，体积门配它会在 8 m 处突然截断；改成 `D ≤ 17.2 m`（= 8 m × 2.154）或直接给一个体积上限。
3. **若这一轮只想改一个标量、不动公式**：把 `1.0` 改成 **`0.8`**（对应约 3:1 的物件），**同时**给细长类道具（长宽比 > 8）一个 per-prop 覆盖值 1.5–2.5。单改一个数无法同时覆盖粗壮与细长两端，这一点是数据本身的结论，不是调参口味问题。
4. **如果设计上就是要比原作宽松**（用户想要「吸起更大的东西」的手感），那就把系数标成 `1.2–1.4` 并**明确写成「有意偏离原作」**，不要写成「校准到原作」。按 §4，`1.2–1.4` 相当于把原作对所有粗壮物件的严格度放宽 2–3 倍。

**最后一句话：我推荐不要只把 `球直径 × 1.0` 调大，而是把判定量从「最长边」换成体积——`a·b·c ≤ 0.0524 × D³`（球体积 ≥ 10 × 物件体积，等价于 `d_eq ≤ D / 2.15`）；若只能动一个系数，就取 `0.8` 并对细长道具单独覆盖到 1.5–2.5；置信度中（数据挖掘公式 + 9 个物件体积反算，8 个落在 ±25% 内，但只有一份社区文档直接陈述了 10× 这个常数）。**

## 7. 这条结论在什么条件下不成立

1. **如果我们的物件尺寸字段本来就是「拾取门槛」而不是物理尺寸**，那 `k = 1.0` 就是对的，什么都不用改。仓库现状不是这样：[`prop_catalog.csv`](../../Design/Maps/PicnicLawn/prop_catalog.csv) 的字段是 `real_longest_dim_m` / `game_longest_dim_m`，运行期 [`PickupAbsorbMath.IsBeyondAbsorbLimit`](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Pickup/PickupAbsorbMath.cs) 比的是 `max(bounds.x, y, z)`——是物理最长边。所以这条前提在我们这里不成立。
2. **10× 这个常数只有一份社区文档直接陈述。** 我用 9 个物件反算验证的是「门槛 ≈ 2.154 × 体积等效直径」这个**形状**，常数 10 本身没有被第二份独立来源确认。若真实常数是 8 或 12，所有门槛按 `(c/10)^(1/3)` 缩放，即 −7%～+6% 的球径，量级上不影响结论。
3. **包围盒体积和「圆的东西」有个系统性偏差。** 球体/椭球（我们的种子、珠子、毛絮团、水果基本都是）用包围盒 `d×d×d` 算体积，会比真实体积大 `6/π ≈ 1.91` 倍，于是这些道具需要 `1.91^(1/3) ≈ 1.24` 倍大的球才吸得动。原作本身也用包围盒（Fandom 明说「包括空腔」），所以这是**忠实**而不是 bug；但如果我们要按「看起来的体积」校准，就得给球类道具一个 `π/6` 的填充率，或者干脆每件道具存一个 `volume_m3`。带空腔的道具（张开的伞、鸟笼）反向受益于包围盒的惩罚，和原作一致。
4. **薄片会被体积门变成白送。** 落叶、亮片这类 `0.1×0.07×0.002` 的道具体积只有 14 cm³，任何球都能吸。原作确实对扁平物宽容（见 §2.3），但如果我们的设计不想要「5 cm 的球吸起 10 cm 的叶子」，就得加一条 `最长边 ≤ D × 1.5` 的兜底。
5. **原作的判定忽略质量、材质、语义。** 同一体积的棉花和铁块门槛一样；这与我们的 `absorb_value` 是两套东西，不要混。
6. **`[推断]` 部分依赖现实尺寸估值。** §2.2 的「现实体积」是我按常见规格估的，不是测量；偏差列应读作「量级校验」而不是「误差分析」。
7. **`circumferential half-meter` 的单位歧义未解**（§3.1）。若 GameSpy 原文确实指直径 16 cm，则它与门槛表冲突，我更信门槛表（门槛表有上百个可互相校验的数）。

## 8. 来源清单

| 编号 | 来源 | 链接 | 等级 | 用途 |
|---|---|---|---|---|
| S-AR01 | *Katamari Damacy Reroll Size Growth*（REROLL 速通社区文档） | <https://docs.google.com/document/d/1lDAal300mmCil_Q57lffuOVuX2ztVnB1ccMyMYgPa_A/> | **数据挖掘** | 10× 体积判定式、门槛 = 最小球径、mm 取整、按关卡的成长比例 |
| S-AR02 | REROLL 速通笔记 · Bot commands | <https://shookietea.github.io/reroll-notes/bot-commands/> | **数据挖掘**（工具说明） | 「pickup size」语义；按关卡不同的尺寸公式 |
| S-AR03 | REROLL 速通笔记 · 各关 Sizes 表（MAS7 例） | <https://shookietea.github.io/reroll-notes/MAS7/> | **数据挖掘**（关卡级） | 关卡内物件门槛值 |
| S-AR04 | REROLL 速通笔记 · Important Object and Size Milestones | <https://shookietea.github.io/reroll-notes/Important%20Object%20and%20Size%20Milestones/> | **数据挖掘**（关卡级） | 大尺度门槛（房屋/桥/岛屿） |
| S-AR05 | StrategyWiki · Katamari Damacy/Objects | <https://strategywiki.org/wiki/Katamari_Damacy/Objects> | **wiki 转录的游戏内数据** | 全物件「Size to roll up」 |
| S-AR06 | Wikibooks · We ♥ Katamari/Stationary | <https://en.wikibooks.org/wiki/We_%E2%99%A5_Katamari/Stationary> | **wiki 转录的游戏内数据** | 铅笔 4.3 cm / 橡皮 8.1 cm 的反例 |
| S-AR07 | Fandom · Katamari Damacy（机制段） | <https://katamari.fandom.com/wiki/Katamari_Damacy> | **wiki 反推** | 包围盒 + 空腔 + 细长物件更易吸；「更大的物件是障碍」 |
| S-AR08 | Fandom · Katamari（起始尺寸表） | <https://katamari.fandom.com/wiki/Katamari> | wiki | 各关起始球径（MAS1 5 cm、MAS6/MAS9 50 cm、Moon 1 m） |
| S-AR09 | GameSpy · *Katamari Damashii: The Snowball Effect*（2004-04-08，采访 Namco） | <https://web.archive.org/web/20080720044247/http://ps2.gamespy.com/playstation-2/katamari-damashii/504503p1.html> | **官方口径的媒体稿** | 「半米球 ⇒ 收音机/盘子/鞋/宠物」；「几厘米 ⇒ 只能吸汤匙」；「grows in volume」 |
| S-AR10 | Wikipedia · Katamari | <https://en.wikipedia.org/wiki/Katamari> | 百科 | 「刚够吸起生物时会被弹开」 |
| S-AR11 | HEXUS · We Love Katamari 评测 | <https://hexus.net/gaming/reviews/ps2/4579-we-love-katamari-ps2/?page=3> | **玩家观测** | 「1 m 球只能吸大约一半大小」 |
| S-AR12 | hexbear 玩家帖（经 piefed 镜像） | <https://piefed.zip/c/games@hexbear.net/p/1608193/katamari-damacy-theory-in-why-certain-objects-seem-easier-harder-to-pick-up-than-i-would-e> | **玩家观测**（弱） | 「碰撞盒体积才是判定量」的独立猜想 |
| S-AR13 | Katamaripedia · List of Katamari Damacy Stages | <https://katamaripedia.miraheze.org/wiki/List_of_Katamari_Damacy_Stages> | wiki | 各关起始球径 / 目标尺寸 |
| S-AR14 | Reroll Dump（关卡表 `gviz` CSV 导出，可直接抓） | <https://docs.google.com/spreadsheets/d/1zg9WRiJamj6iUKuL2iTY1BhXfhD0HwLHwCNO-9HNe6s/> | **数据挖掘**（第一方数据表，社区导出） | `START_DIAMETER` / `CLEAR_DIAMETER` / `MAX_DIAMETER` / `GAME_TIME`；核对起始球径 |

引用 URL 仅用于核查，不代表版权、品牌授权或对原作实现的背书。本轮未运行游戏、未录制实机、未拆包。

## 9. 没查到 / 查不动的

1. **没有官方说明或 SDK 级拆包文档**陈述拾取判定。S-AR01 是目前最接近「数据挖掘」的材料，但它是社区推导，正文自己用了 "appears to work the following way" / "roughly"。
2. **物件的 per-object 体积表本身没拿到。** S-AR14 的表格能公开导出，但 `gviz` 端点只吐出第一张（关卡表）；物件/体积那张表的名字与导出参数没试出来，所以 §2.2 只能用「门槛值反算体积」再和现实体积对照，而不能直接读游戏给的 volume 字段。这一步值得再做一次——拿到原始 volume 列就能把 §2.2 从「推断」升级成「数据挖掘」。
3. **「刚够吸起时被弹开」的阈值宽度未知**（是 `≥` 还是 `≥ ×0.98` 之类），公开材料只描述了现象。
4. **各作品的判定是否完全一致未知**：S-AR01 只覆盖 REROLL（= Katamari Damacy 复刻）。We ♥ Katamari / Beautiful Katamari / Once Upon a Katamari 的门槛表结构相同（数字不同），但我没有找到跨作品统一的公式证据。
5. **附带的成长数据**（同一份 S-AR01）：拾取后加进球体积的比例按关卡分段线性，例如 MAS3 `(10cm,.8)(50cm,.8)(75cm,.4)(1m,.16)`、MAS9 `(50cm,.8)(3m,.96)(10m,.8)(20m,.48)…`，且「Regular Tree」一类物件额外有 50% 惩罚。这与我们的成长调参相关，但本轮没有展开核验，只登记在此以免被当成已验证参数。
