# 原作运动程序：13 类运动的参数、来源与每帧成本

| 项 | 值 |
|---|---|
| 关卡 | `_ref_cancer`（Make Cancer，PS2 `level 11` / 起始区 2，noclip `clvl1`，"怀旧关：蟹座小院"） |
| 目标 | 把"道具会动"从一句 `moveType` 拆成 13 个可实现的运动程序：参数、取值范围、每帧做什么、参数从哪来 |
| 结论 1 | 129 件有运动（我关 511 件里 128 件，缺 1 件 `Dummy Hit 01`），只落在 **13 类**上 |
| 结论 2 | **运动表、路径表、速度档、尺寸档都能从 BIN 自行解出**（本轮已解，见 §3.2–3.4）；只有"每类怎么动"的规则和常数不在数据里 |
| 结论 3 | 13 类里 **5 类是无状态解析解**（Spin/Flip/Bob/Sway/WhackAMole），2 类只做检测（WaitForPlayer/DraggedAlong），6 类有状态（SporadicWalk/Hop/Clouds/PathCollision/SlopingPath/BackAndForthNoYaw） |
| 结论 4 | 幅度不是自由参数，而是**道具自身高度**的固定倍数（WhackAMole ≈1.5×高、Bob ≈0.08×高、Sway 枢轴 ≈1.0×高），但换算系数逐类不同，必须实测标定 |
| 结论 5 | 相位/定时在原作里是进程内随机数，**数据里没有**；我们的方案沿用 D-12（确定性 hash + 作者覆盖），只需分布正确，不需要逐件复刻 |
| 结论 6 | WaitForPlayer 的"触发后干什么"在参考实现里是空实现，**推不出**，见 §8.1 |
| 结论 7 | 建议先做 SporadicWalk → Hop → WhackAMole，覆盖 57/128 = 45% 的运动件，也是玩家最常看到的三种 |
| 口径 | 【数据】= 从 BIN/实例记录解出；【实测】= 从渲染帧量出；【常量】= 参考实现写死（原作在 ELF 代码里）；【推断】/【PROVISIONAL】= 未验证 |
| 前作 | 周期与速度的帧级测量见 [ORIGINAL_ANIMATION_OBSERVATIONS.md](<ORIGINAL_ANIMATION_OBSERVATIONS.md>)；二进制格式见 [NOCLIP_KATAMARI_FORMAT.md](<NOCLIP_KATAMARI_FORMAT.md>) |
| 边界 | 只读 `~/KatamariRef`；本文只写结论、参数与推导方法，不含原版二进制、反编译文本或模型数据 |
| 未做 | 没跑 Unity、没改代码、没做 Git 写操作；本文所有数值都不是我们当前实现里的值 |

---

## 1. 一句话结论

原作的"道具会动"= **一张每关 6 字节/项的运动表**（给每件道具一个 `motionID` + 一条路径 + 一个备用索引）+ **一张路径表**（折线点 + 速度）+ **一套按 `motionID` 分派的更新规则**（规则和常数全在代码里）。

前两张表我们能自己解（§3），第三张只能靠参考实现（noclip.website，MIT + 反编译说明）或实机录像。所以"13 类运动"的**分类是数据的**，**参数一半是数据的**（速度、路径、尺寸档、方向），**另一半是代码常量**（角速度、周期、重力、触发半径、幅度系数）；相位是运行时的，任何来源都没有。

---

## 2. 参数只有三种来源

| 来源 | 具体内容 | 我们拿得到吗 | 例子 |
|---|---|---|---|
| **二进制表** | 运动表（6 B/项）、路径表（8 B/记录 + 16 B/点）、type 的 `speedIndex`/`size`/`stayLevel`/`animated` | ✅ 本轮已自行解析，见 §3 | `motionID=0x19`、`pathIndex=23`、路径速度 `0.8`、`size=2` |
| **代码常量** | 角速度 `0.05 rad/帧`、摆角 `±10°`、正弦步长 `π/45`、重力 `1`、hop 速度表、触发半径表、`speedTable` 11 档、转向角表 | ⚠️ 只能照抄参考实现，或用实机录像反推 | Sway 周期 3.0 s、Hop 速度 4 cm/帧 |
| **只能观测** | 幅度相对道具高度的系数、触发之后干什么、动画 clip 的播放速率 | ❌ 必须实测/标定 | WhackAMole 行程 ≈1.5×模型高 |

**推论**：把 13 类做成数据驱动时，第 1 类字段（程序/速度/路径/尺寸档）可以由构建脚本自动填；第 2 类字段（周期/振幅比例）应当作为**程序常量**写在我们自己的运动模块里，不要逐件写进关卡文件；第 3 类字段必须留作者覆盖位。

---

## 3. 数据里究竟有什么

### 3.1 实例记录：`moveType` 是索引，不是类型

实例记录 0x40 字节里 `moveType`（s16）只是**运动表的下标**，本身不含任何语义。我关 511 件里它有 16 个不同取值，映射到 13 类，其中有两处是多对一、一处要靠备用索引：

| `moveType` | 件数 | `motionID` | 类别 |
|---|---|---|---|
| 1 | 7 | `0x15` | Spin |
| 2 | 2 | `0x16` | Bob |
| 3 | 18 | `0x19` | Hop |
| 4 | 15 | `0x20` | Sway |
| 5 | 8 | `0x22` | WhackAMole |
| 7 | 7 | `0x00` | WaitForPlayer |
| 10 | 8 | `0x04` | WaitForPlayer |
| 12 | 18 | `0x0B` | SporadicWalk |
| 13 | 13 | `0x0C` | SporadicWalk |
| 16 | 5 | `0x23` | Clouds |
| 17 | 2 | `0x1E` | Flip |
| 18 | 15 | `0x25` | DraggedAlong |
| 19 | 2 | `0x27` | SlopingPath |
| 20 | 8 | **−1** | PathCollision（`motionID<0` ⇒ 用 `backupIndex+1=2` 当动作号） |
| 21 | 1 | `0x1D` | BackAndForthNoYaw |
| −1 | 383（源 395） | — | 静止 |

> 件数按源数据 524 件统计（`moveType 20` 的 8 件里有 1 件不在我们的 511 件里）；附录 A 按我关 511 件统计，合计 **128 件**。

两点必须记住：**`moveType` 的值逐关不同**（它只在本关的运动表里有意义），**`motionID<0` 的项没有运动 ID**，要走"备用索引 = 动作号"的老路（原作里是给老关卡兼容用的）。

### 3.2 运动表：`movementBlock.bin`，6 字节/项

结构（`bin.ts:parseMotion`，偏移沿用 [NOCLIP_KATAMARI_FORMAT.md](<NOCLIP_KATAMARI_FORMAT.md>) 的记法）：

- 关卡表：`u32[levelIndex]` → 运动表地址；
- 项：`s16 backupIndex` / `s16 pathIndex` / `s16 motionID`；
- `motionID >= 0` ⇒ 动作 = `motionActionTable[motionID]`（**参考实现里的常量表，原作里是 ELF 里的 switch**）；`motionID < 0` ⇒ 动作 = `backupIndex + 1`。

本轮我按这个结构自己解了 Cancer（`levelIndex=11`），16 项与运行时观测逐项一致（这是"运动表可自解"的直接证据）：

| `moveType` | `motionID` | `pathIndex` | `backupIndex` |
|---|---|---|---|
| 1 | 21 (`0x15`) | 0 | 0 |
| 2 | 22 (`0x16`) | 0 | 0 |
| 3 | 25 (`0x19`) | 0 | 0 |
| 4 | 32 (`0x20`) | 0 | 0 |
| 5 | 34 (`0x22`) | 0 | 0 |
| 7 | 0 | 0 | 2 |
| 10 | 4 | 0 | 2 |
| 12 | 11 (`0x0B`) | 0 | 2 |
| 13 | 12 (`0x0C`) | 0 | 2 |
| 16 | 35 (`0x23`) | 0 | 2 |
| 17 | 30 (`0x1E`) | 0 | 0 |
| 18 | 37 (`0x25`) | 0 | 0 |
| 19 | 39 (`0x27`) | **15** | 1 |
| 20 | −1 | **23** | 1 |
| 21 | 29 (`0x1D`) | **22** | 1 |

**只有 3 个 `moveType` 带真路径**（19/20/21），其余全部指向 `pathIndex=0`。

### 3.3 路径表：`pathBlock.bin`，8 字节/记录 + 16 字节/点

- 记录：`u32 pointStart` + `f32 speed`；
- 点：4 个 f32（x,y,z,`w`），`w == 255` 是结束标记，不是点；
- **`speed < 0` 是哨兵**，意思是"这条路径不给速度，用 type 的 `speedIndex` 去查 `speedTable`"。

本轮解出的 4 条相关路径（Cancer）：

| path | 速度 | 点数 | 用途 |
|---|---|---|---|
| 0 | **−1.0** | 2 | 所有"不用路径"的运动都指它 ⇒ 走 `speedTable` |
| 15 | 1.2 | **137** | SlopingPath（`moveType` 19） |
| 22 | 0.6 | 2 | BackAndForthNoYaw（`moveType` 21），一条直线 |
| 23 | 0.8 | **134** | PathCollision（`moveType` 20） |

速度语义沿用前作结论：**单位是 cm/游戏帧**，换算到 cm/s 的实测比值落在 **30.3–30.8**（取 30.5；3 条带速度的路径与 5 个速度档全部落在区间内，见 §5 表）。

### 3.4 道具定义表：速度档与尺寸档

| 字段 | 位置 | 作用 |
|---|---|---|
| `speedIndex`（s8） | `objectBlock + id*0x24 + 0x13` | 路径速度 < 0 时查 `speedTable` |
| `stayLevel` | `objectBlock + id*0x24 + 0x0C` | `false` ⇒ `adjustPitch`，路径运动时机身跟随坡度 |
| `animated` | `objectBlock + id*0x24 + 0x1A` | 是否有骨骼动画 |
| `dummyParent` | `objectBlock + id*0x24 + 0x21` | 是否可当挂接父件 |
| `size`（s8, 0…11） | `collectionBlock + id*8 + 0x02` | **同时决定 hop 速度档和触发半径档** |

`size` 的派生（参考实现常量，原作在代码里）：

```
sizeGrouping = [0,0,0,1,2,3,3,4,4,4,4,4]
hopSpeeds    = [4, 6, 7, 12, 15]                     // cm/帧
panicRadii   = [200, 300, 500, 700, 900]             // cm，索引 = sizeGrouping[size]
```

我关 128 件的实际分布：`size=2` **99 件**、`3` 16 件、`4` 8 件、`7` 5 件（云）；`speedIndex` 只有 1、2、6 三种（→ 速度 1、2、10 cm/帧）。也就是说这关里 hop 速度只用得到 **4 cm/帧** 一档（`size=2`，18 件全是），触发半径只用得到 **200/300 cm** 两档——13 类实现可以先把这两档做对，别的档位等其它关再说。

### 3.5 `speedTable`：唯一一张"纯常量表"

```
speedTable = [0.3, 1, 2, 4, 6, 8, 10, 15, 20, 40, 200, 0]   // cm/帧
```

它是**代码常量**（原作 ELF 里），不在数据里。好消息是我关只用上 1、2、10 三档，且三档都已被实测验证（30.3 / 60.7 / 303 cm/s，见 §5）。

### 3.6 数据里没有的东西

| 缺什么 | 后果 |
|---|---|
| 每类的更新规则（正弦、重力、积分） | 只能照参考实现或实机观测 |
| 角速度/周期/重力/幅度系数 | 同上，见 §5 的标定表 |
| 相位、定时、随机种子 | 数据里完全没有 ⇒ 用 D-12 的 hash 方案 |
| 触发后行为（`alt` 动作 0x05/0x08） | 参考实现是空实现 ⇒ §8.1 |
| 动画 clip 的 fps/循环区间 | 只有 clip 列表与起始 clip `playAnimNo` |
| 每 type 的 `partBBox`（真正的几何包围盒） | 我们只有对称 `aabbM` ⇒ 幅度换算系数需标定（§5.7） |

---

## 4. 13 类：定义、参数、载体

### 4.0 总表

| # | 类 | `motionID` | 件数 | 一句话 | 每帧做什么 |
|---|---|---|---|---|---|
| 1 | SporadicWalk | `0x0B`/`0x0C` | 31 | 匀速乱走，随机转向，周期性发呆 | 转向计时 + 前进一步（贴地 + 出区检测） |
| 2 | Hop | `0x19` | 18 | 随机等 0–8.5 s，然后向下扎一下再被重力弹回 | 计时 + 积分 + 落地检测 |
| 3 | Sway | `0x20` | 15 | 像钟摆一样绕"头顶枢轴"左右摆 ±10° | 正弦 + 重建基向量 + 重算位置 |
| 4 | DraggedAlong | `0x25` | 15 | 自己不动，被父件拖着走 | 无（只有父件变换） |
| 5 | WaitForPlayer | `0x00`/`0x04` | 15 | 站着不动，等球靠近 | 一次距离 + 区域判定 |
| 6 | WhackAMole | `0x22` | 8 | 打地鼠：随机把整件从地下顶出来再缩回去 | 计时 + 正弦 + 只改 y |
| 7 | PathCollision | 动作 `0x02` | 7 | 沿 134 点折线恒速走 | 到点判定 + 改朝向/俯仰 + 前进 |
| 8 | Spin | `0x15` | 7 | 原地自转 | 一个加法 |
| 9 | Clouds | `0x23` | 5 | 高空乱飘，不转向、不碰地 | 转向计时 + 前进（无地面检测） |
| 10 | Flip | `0x1E` | 2 | 原地前后翻 | 一个减法 |
| 11 | Bob | `0x16` | 2 | 上下轻微浮动 | 正弦 + 一个赋值 |
| 12 | SlopingPath | `0x27` | 2 | 沿 137 点折线走，机身贴坡 | 同 PathCollision + 俯仰缓动 |
| 13 | BackAndForthNoYaw | `0x1D` | 1 | 两点之间来回走，朝向不变 | 到端点反向 |

合计 128 件（源数据 129，差 1 件 `Dummy Hit 01` 实例不在我们 511 件里）。逐件清单见附录 A。

### 4.1 SporadicWalk（31 件：`small_fiddler_crab` 21、`fiddler_crab` 8、`coconut_crab` 2）

| 参数 | 值 | 来源 |
|---|---|---|
| 速度 | `speedIndex` 1→1 cm/帧（30.3 cm/s）29 件；2→2 cm/帧（60.7 cm/s）2 件（椰子蟹） | 【数据】 |
| 初始方向 | 世界 −X | 【常量】 |
| 随机转向 | 每 `120 + 570·rand` 帧（4–23 s）转一次，方向 ±90°，用 20 帧转完 | 【常量】 |
| 走走停停 | 走 `120 + 60·rand` 帧（4–6 s）→ 停 `30 + 30·rand` 帧（1–2 s） | 【常量】 |
| 被挡住 | 依次试 `[45,−45,90,−90,135,−135,180]°`，取第一个"仍在自己的 zone 内"的，12 帧转完 | 【常量】 |
| 贴地 | 每步 `findGround`；命中则按地面法线贴合并重定向速度；`stayLevel=false` 时机身也跟法线 | 【常量】 |
| 活动域 | 初始化时 `getZone(pos, 2×半径)`，越界就算走失败并回退一步 | 【常量】 |
| 动画 | 走 = `MOVING`，停 = `IDLE` | 【常量】 |

实测：峰值 30.3 cm/s，净位移速度 17–25 cm/s（约 25% 时间在停）⇒ 与"停 1–2 s / 周期 5–7 s"一致。

### 4.2 Hop（18 件：`small_fiddler_crab` 13、`fiddler_crab` 5）

| 参数 | 值 | 来源 |
|---|---|---|
| 速度（= 起跳初速） | `hopSpeeds[sizeGrouping[size]]`；本关 18 件全 `size=2` ⇒ **4 cm/帧** | 【数据】+【常量】 |
| 重力 | `g = +1` cm/帧² | 【常量】 |
| 初速方向 | `v_y = −speed`（先**向下**扎） | 【常量】 |
| 等待 | `timer = 255·rand` 帧 = 0–8.5 s，落地后重新随机 | 【常量】 |
| 运动 | `v_y += g·dt`；`y += v_y·dt` | 【常量】 |
| 落地 | 有支撑件时落回支撑件顶面；否则找地面；落地即 `timer = −1`（重新待机） | 【常量】 |
| 最低点 | `v²/2g = 8 cm`（`size=2`） | 推导 |

注意语义：这不是"向上跳"，而是**先砸下去再被重力推回来**，一个来回 `2·speed = 8` 帧（0.27 s），所以肉眼看是"抽一下"。实测最低点 6.0–6.6 cm（欠采样，见 §5.5）。

### 4.3 Sway（15 件：向日葵 4、`fiddler_crab` 3、白郁金香 3、长柄勺 2、蓝蟹 1、椰子蟹 1、灭火器 1）

| 参数 | 值 | 来源 |
|---|---|---|
| 轴 | 局部 Z（roll / 左右倾） | 【常量】 |
| 倾角 | `sin(θ)·TAU/36` = **±10.0°** | 【常量】 |
| 角速度 | `θ += π/45` 每帧 ⇒ **90 帧 = 3.0 s 一个来回** | 【常量】 |
| 枢轴 | 不是原地转：位置按"绕本体上方 `partBBox.max[1]` 处的枢轴摆动"重算 ⇒ 越高摆得越开 | 【常量】+ 模型 bbox |
| 例外 | `BALANCEDOLL01_C`（不倒翁）只倾斜、不平移 | 【常量】 |

实测（向日葵）：水平摆幅 p-p **82 cm**、垂直 p-p **7.19 cm**、垂直通道周期 **1.52 s**。
垂直通道是半周期，因为垂直量 ∝ `1 − cos θ`（两倍频）；把 82 cm 代回 `2·h·sin10°` 得 `h = 236 cm`，把 7.19 cm 代回 `2·h·(1−cos10°)` 得 `h = 236 cm`——**两条独立通道给出同一个枢轴高度**，这是本类公式可信的最强证据。

### 4.4 DraggedAlong（15 件：`fiddler_crab` 6、`small_fiddler_crab` 5、`coconut_crab` 2、蓝蟹 1、小狗 1）

动作号是 `SetZone`，**没有任何自身运动**：位置由父件变换驱动。参数只有 zone（`getZone(pos, 100)`）。

关键事实：这 15 件里**只有 8 件在我们数据里解析到了父件**（`inherit: full`），另外 7 件无父 ⇒ 原作里它们也不动。做实现时不能默认"DraggedAlong = 会动"。

### 4.5 WaitForPlayer（15 件：`small_fiddler_crab` 8、`fiddler_crab` 6、蜘蛛蟹 1）

| 参数 | 值 | 来源 |
|---|---|---|
| 触发半径 | `panicRadii[sizeGroup]`；本关 14 件 `size=2` ⇒ **200 cm**，1 件 `size=3` ⇒ **300 cm** | 【数据】+【常量】 |
| 附加条件 | 球必须在同一 zone，且 `sizeGrouping[size] ≤ 2`（太大件的这类不触发） | 【常量】 |
| 触发后 | `useAltMotion = true` ⇒ 走 `alt` 动作 `0x05` / `0x08`，**参考实现里是空实现** | 【常量】 |

⇒ 我们只知道门（2–3 m + 同区），不知道触发后干什么。见 §8.1。

### 4.6 WhackAMole（8 件：`small_fiddler_crab` 4、石灯笼 2、旗杆 1、蜘蛛 1）

| 参数 | 值 | 来源 |
|---|---|---|
| 待机 | `timer = rand·150 + 60` 帧 = **2–7 s**，每次待机结束翻转 `state` | 【常量】 |
| 行程 | `buriedDepth = partBBox.max[1] + (max[1] − min[1])` ≈ **1.5 × 模型高**（实测） | 【常量】+ bbox |
| 上升时间 | 从 `θ=π/2` 到 `π`，`θ += π/45` ⇒ **22.5 帧 = 0.75 s** | 【常量】 |
| 公式 | `y = spawnY + depth · (state==0 ? 1−sin θ : sin θ)`（只改 y） | 【常量】 |

实测：小蟹 12.37 cm、蜘蛛 16.09 cm、旗杆 165 cm；上升耗时 0.7 s；7 s 窗口内未见回落（既符合 2–7 s 待机，也可能受 §5.5 的可见性口径影响，无法区分）。注意**旗杆也属于这一类**——它是全场行程最大的运动件，视觉上最容易被注意。

### 4.7 PathCollision（7 件：`coconut_crab` 3、`fiddler_crab` 3、`small_fiddler_crab` 1）

| 参数 | 值 | 来源 |
|---|---|---|
| 路径 | `path 23`：**134 点**折线 | 【数据】 |
| 速度 | 路径自带 `0.8` cm/帧 = **24.5 cm/s**（覆盖 `speedIndex`） | 【数据】 |
| 跟随 | 到目标点距离 ≤ `speed·dt` ⇒ 推进到下一点；速度每帧归一化 | 【常量】 |
| 朝向 | 用"到目标的帧数 = dist/speed"把 yaw 缓动过去 | 【常量】 |
| 俯仰 | `adjustPitch`（`stayLevel=false`）时按坡度缓动 | 【常量】 |

实测 24.5 cm/s（0.8 cm/帧 × 30.6），与路径记录一致。

### 4.8 Spin（7 件：垃圾桶、拉面、圆桌、涮涮锅、`fiddler_crab`、`small_fiddler_crab`、蜘蛛）

`euler.y += 0.05 · dt` ⇒ **0.05 rad/帧，一圈 125.7 帧 = 4.19 s**。无数据参数，纯常量。

### 4.9 Clouds（5 件，全是 `scattered_clouds`）

| 参数 | 值 | 来源 |
|---|---|---|
| 速度 | `speedIndex 6` ⇒ 10 cm/帧 = **303 cm/s** | 【数据】 |
| 转向 | `90 + 600·rand` 帧（3–23 s）一次 ±90° | 【常量】 |
| 特殊性 | `ignoreYaw = true`（机体不转向）、**不做地面检测**、不改变高度 | 【常量】 |
| 位置 | 世界高度 ≈ `y = −1741.5`（视觉空间的上方） | 【数据】 |

### 4.10 Flip（2 件）

`euler.x −= 0.05 · dt` ⇒ 绕 X 匀速，一圈 125.7 帧 = 4.19 s。

### 4.11 Bob（2 件：`small_fiddler_crab`、蜘蛛）

| 参数 | 值 | 来源 |
|---|---|---|
| 起始等待 | `timer = rand·60` 帧（0–2 s），只一次 | 【常量】 |
| 角速度 | `θ += π/45` ⇒ 90 帧 = **3.0 s** | 【常量】 |
| 振幅 | `0.15 · partBBox.max[1]` ≈ **0.08 × 模型高**（实测） | 【常量】+ bbox |
| 公式 | `y = spawnY + 振幅 · sin θ`（绝对高度，不走样） | 【常量】 |

### 4.12 SlopingPath（2 件：书架、马蹄蟹）

`path 15`：**137 点**，速度 `1.2` cm/帧 = **37 cm/s**，带俯仰贴合（与 PathCollision 共用同一条更新路径，只差"要不要算碰撞俯仰"）。

### 4.13 BackAndForthNoYaw（1 件：`fiddler_crab_034`）

`path 22`：**2 点**直线，速度 `0.6` cm/帧 = **18.2 cm/s**；到端点反向，**不改变朝向**（`NoYaw`）。

---

## 5. 四个周期类的实测

### 5.1 方法

- 采集：headless Chromium + SwiftShader 跑 noclip 观景器，**把 rAF 换成虚拟时钟**（1 渲染帧 = 2 游戏帧 = 66.67 ms），定点相机，960×540；
- 序列：`cancer_crab_fine` 30 帧、跨度 7.0 s（0.23 s/帧），另有 14 帧广角序列做交叉检查；
- 对象位置：每帧从场景里导出全部实例的 `prevPosition`（世界坐标，cm），按实例号索引；
- 判读：逐件算 y 的峰峰值与峰间隔；用 `pngdiff` 数变化区域交叉验证（`crab_fine` 5.16% 像素变化、21 个区域）；
- 所有帧与脚本都在 `~/KatamariRef/viewer_evidence/noclip_frames/`，不进仓库。

### 5.2 Bob（`moveType 2`，2 件）

| 实例 | y 峰峰值 | 峰间隔 |
|---|---|---|
| `spider` #65 | 1.61 cm | 2.83 s |
| `small_fiddler_crab` #66 | 1.35 cm | 3.07 s |

与公式对照：周期 `90 帧 × 33 ms = 3.00 s` ✅（两件都落在 ±0.2 s 内）；振幅 `0.15 × partBBox.max[1]` 在"`partBBox ≈ 半个模型高`"的假设下给出 ±0.80 cm（蜘蛛实测 ±0.81）✅。

### 5.3 Sway（`moveType 4`，15 件）

| 载体 | 水平 p-p | 垂直 p-p | 垂直通道峰间隔 |
|---|---|---|---|
| 向日葵 ×4 | **82.0 cm** | **7.19 cm** | **1.52 s** |
| 长柄勺 ×2 | 0.3 cm | 2.23 cm | 2.83 s（判读不稳，见下） |

判读：倾角周期 3.0 s，但**位置**的垂直分量 ∝ `1−cos θ` 是两倍频 ⇒ 1.5 s ✅；向日葵的 1.52 s 与此精确吻合。长柄勺的 2.83 s 是峰值检测把相邻峰合并了（振幅只有 2.23 cm、接近位置量化步长），**不作为反证**。

### 5.4 WhackAMole（`moveType 5`，8 件）

| 实例 | 行程 | 上升耗时 |
|---|---|---|
| 小蟹 ×4 | 12.37–12.38 cm | 0.7 s |
| 蜘蛛 #68 | 16.09 cm | 0.7 s |
| 旗杆 #394 | **165.0 cm** | 0.7 s |

与公式对照：上升 `22.5 帧 = 0.75 s` ✅；行程 `1.5 × 模型高`（12.37/7.85 = 1.58、16.09/10.69 = 1.51、165/110 = 1.50）✅ 三件一致。

### 5.5 Hop（`moveType 3`，18 件）

| 实例 | 最低点 | 7 s 内的次数 |
|---|---|---|
| #119 | −6.60 cm | 1 |
| #146 | −6.05 cm | 1 |

**这里有一个必须记录的矛盾**：参考实现是 `timer = 255·rand` 帧（dt=2 时最长 8.5 s），按此在 7 s 窗口里 18 件中应有约 15 件至少扎一次；实测**只有 2 件动，其余 16 件 y 全程恒定**。

最可能的解释是**观测口径而非实现差异**【推断，强】：观景器在 `prepareToRender` 开头就 `if (!this.visible) return`，**不可见对象根本不跑运动**，我导出的位置是它最后一次可见时的残值。同类现象也能解释"每类只有少数几件在动"（例如 Sway 的 15 件里只有 4 件向日葵有明显位移）。若成立，则本文所有"动了多少件"都是**下界**。

因此：`Hop` 的周期只能按【常量】写（0–8.5 s 均布），**不能**用这组数据反推；要验证必须用能覆盖整个院子的全景相机 + 更长序列。最低点 6.0–6.6 cm 也同样是欠采样（谷底只持续 `speed=4` 帧 = 0.13 s，采样 0.23 s 必然错过），理论值 `v²/2g = 8 cm` 无法被这组数据确认或否定。

### 5.6 Spin / Flip：未逐帧测

两者位置完全不变（实测 y/x/z 全程恒定），与"只改 `euler`、不平移"的预期一致——但**位置不变不能证明它在转**（也可能是不在视野内没被更新，见 §5.5 的口径问题）。画面里能看到圆桌、垃圾桶在转（`pngdiff` 在对应区域有变化），说明它们确实在转。角速度 `0.05 rad/帧 ⇒ 4.19 s/圈` 是【常量】，**未实测验证**，标 PROVISIONAL。要验证只需固定相机拍 3 s、数转过的角度。

### 5.7 标定表：把幅度写成"我方模型高度的倍数"

因为幅度都挂在 bbox 上，实现时应当用我们自己的 `modelBboxM[1]`（半高或全高，见备注）乘系数，而不是写死厘米。

| 类 | 量 | 实测（cm） | 参考公式 | 换成"× 我方 `modelBboxM[1]`" |
|---|---|---|---|---|
| Bob | 振幅 | ±0.68 / ±0.81 | `0.15·partBBox.max[1]` | **0.075–0.086** |
| WhackAMole | 行程 | 12.37 / 16.09 / 165.0 | `max + (max−min)` | **1.50–1.58** |
| Sway | 水平 p-p | 82.0 | `2·h·sin 10°` | **0.34** |
| Sway | 垂直 p-p | 7.19 | `2·h·(1−cos 10°)` | **0.030** |
| Hop | 最低点 | 6.0–6.6 | `v²/2g = 8` | 由 `size` 档给，不用高度 |
| Bob | 周期 | 2.83 / 3.07 s | 90 帧 = 3.00 s | 常量 |
| Sway | 倾角周期 | （1.52 s 是半周期） | 90 帧 = 3.00 s | 常量 |
| WhackAMole | 上升 | 0.7 s | 22.5 帧 = 0.75 s | 常量 |

**备注（重要，PROVISIONAL）**：`partBBox` 与我们导出的 `modelBboxM` 之间的换算因子**不是常数**：旗杆/蜘蛛/小蟹都指向"`partBBox.max[1] = modelBboxM[1]/2`"，而向日葵指向"`= modelBboxM[1]`"。所以上表最后一列的系数**只能当初始值**，每类至少要在一件已知尺寸的道具上实测一次再定稿。这也是"不许编数"的具体落点：周期是常量可以直接抄，幅度必须先量。

---

## 6. 每帧成本模型

### 6.1 逐类每帧工作

| 类 | 每帧每件计算 | 有状态 | 解析解？ | 建议更新率 |
|---|---|---|---|---|
| Spin | `euler.y += 0.05·dt` | 否 | ✅ 纯 `f(t)` | 5 Hz |
| Flip | `euler.x −= 0.05·dt` | 否 | ✅ | 5 Hz |
| Bob | `θ += π/45`；`y = y0 + A·sin θ` | 否（只有起始随机） | ✅ | 10 Hz |
| Sway | `θ += π/45`；`euler.z = sin θ·10°`；重建基向量 + 重算位置 | 否 | ✅ | 10 Hz |
| WhackAMole | 计时；`θ += π/45`；`y = f(θ, state)` | 否 | ✅ 分段 | 10 Hz |
| Hop | 计时；`v += g·dt`；`p += v·dt`；落地检测（1 次地面/支撑查询） | 是 | ✗ | 15 Hz |
| SporadicWalk | 转向计时；前进一步（1 次贴地查询 + 1 次 zone 判定）；偶尔 7 次换向判定 | 是 | ✗ | 15 Hz |
| Clouds | 同上但**无**贴地查询、无 yaw | 是 | ✗ | 10 Hz |
| PathCollision / SlopingPath | 到点判定；推进；归一化；yaw/pitch 缓动 | 是 | ✗ | 15 Hz |
| BackAndForthNoYaw | 到点 ⇒ 反向 | 是 | ✗ | 15 Hz |
| WaitForPlayer | 1 次距离 + 1 次 zone 判定 | 否 | ✅ | 5 Hz |
| DraggedAlong | 无 | — | — | 0 |

### 6.2 我关 128 件的分档

| 档 | 件数 | 说明 |
|---|---|---|
| 无状态、可解析（Spin/Flip/Bob/Sway/WhackAMole） | **34** | 可以完全不存状态，`f(now, phase)` 直接算 |
| 有状态积分（SporadicWalk/Hop/Clouds/Path*） | **64** | 需要固定节拍与状态存储 |
| 近零成本（DraggedAlong 15，其中 8 件真有父件；WaitForPlayer 15 只做检测） | **30** | 父件驱动或纯检测 |

34 + 64 + 30 = 128。

### 6.3 预算核算

沿用前作的预算事实：0.02 ms/update、90 Hz、25% 预算 2.78 ms ⇒ **138 次单件更新/帧 = 12,420 次/秒**。

| 方案 | 更新次数/秒 | 占预算 |
|---|---|---|
| 128 件全部 10 Hz | 1,280 | 10.3% |
| 128 件全部 15 Hz | 1,920 | 15.5% |
| 只做前 3 类（57 件）15 Hz | 855 | 6.9% |
| 无状态 34 件按 5 Hz、有状态 64 件按 10 Hz | 810 | 6.5% |

结论：**这关的运动会动不是性能问题**——即使 15 Hz 全量也只有 15.5% 预算。真正的成本风险在"每件一个 UdonBehaviour、每帧 `Update()`"的写法（138 个 `Update()` 就顶满预算），所以应当按类合并成少数几个管理器，用统一节拍 + 分片游标推进。

---

## 7. `motions` 块要表达什么（语义需求，**不是**最终 schema）

> 本节只列语义需求与草案形状。字段名、层级都**未冻结**——按 AGENTS.md，schema 决断类研究未结案前只记需求。

### 7.1 八个维度

| # | 维度 | 为什么必须有 | 谁填 |
|---|---|---|---|
| 1 | **程序**（13 类之一 / `none`） | 决定更新规则 | 构建脚本从 `motionID` 自动填 |
| 2 | **载体**（哪件道具 + 几何锚点） | Sway 要枢轴高、WhackAMole 要埋深、Hop 要初始支撑件 | 自动（bbox）+ 可覆盖 |
| 3 | **参数**（每类自己的标量） | 速度/路径/幅度/周期 | 速度与路径自动；幅度系数是程序常量；路径点从 `pathBlock` 自动 |
| 4 | **相位 + 相位来源** | 决定错落感；没有相位所有螃蟹会同步 | D-12：hash（实例索引/位置）+ 作者覆盖 |
| 5 | **吸附中断** | 被滚到时怎么收尾（当前帧位置？播完当前周期？） | 作者定（默认：当帧冻结并交还吸附流程） |
| 6 | **父件挂起时** | D-14：子件坠落时挂起自身运动 | 程序默认 + 可覆盖 |
| 7 | **活动域/触发** | 螃蟹不离开自己的 zone；WaitForPlayer 有 2–3 m 门 | 数据（zone/area）+ 常量（半径） |
| 8 | **更新节奏** | 有状态类需要固定节拍，无状态类可按任意节拍采样 | 程序常量，不进关卡数据 |

明确**不该进数据**的东西：运行时的 `timer`/`angle`/`state`（那是运行态，进存档不进关卡文件）。只有**相位种子**进数据。

### 7.2 草案形状（明显非最终）

```jsonc
// 只为说明"要表达什么"，字段名与层级都不是最终形态
motions: [
  { program: "sway",                  // 维度 1
    target: "sunflower_003",          // 维度 2
    anchor: { pivotY: 2.406 },        //   几何锚点（自动生成）
    params: { tiltDeg: 10, periodS: 3.0 },   // 维度 3
    phase: 0.37, phaseSource: "hash",        // 维度 4
    interrupt: "freeze",                     // 维度 5
    onParentSuspended: "suspend",            // 维度 6
    bounds: { zone: 2 },                     // 维度 7
    strideHz: 10 }                           // 维度 8
]
```

### 7.3 与既有决策的对应

- **D-10（13 类全实现）** ⇒ `program` 是闭合枚举 13 + `none`，不允许"临时拼一个"；
- **D-12（相位 = 确定性 hash + 作者覆盖）** ⇒ 维度 4 的两个来源必须都能表达，且 hash 输入要写死（否则复现不了）；
- **D-14（子件坠落时挂起自身运动）** ⇒ 维度 6 是必需字段，不是可选优化；
- 既有 `placement.v2.json` 的 `PROP_KEYS` 已预留 `motion`/`phase`/`clipStart`，`behaviour` 枚举里已有 `"motion"`——但**当前 511 件全是 `behaviour: "static"`，`motions` 表为空**，即字段位有了、内容还没生成。

---

## 8. 推不出来的清单

| # | 推不出什么 | 为什么 | 怎么补 |
|---|---|---|---|
| 1 | **WaitForPlayer 触发后干什么**（15 件） | `alt` 动作 `0x05`/`0x08` 在参考实现里是空实现，观景器里触发后物体直接冻结，看不到行为 | 实机录像，或读 ELF 的动作分派分支 |
| 2 | **相位/定时的具体值** | 原作是进程内随机；数据无字段 | 不需要复刻：只要分布对（hop 0–8.5 s 均匀、whack 2–7 s 均匀），错落感就成立 |
| 3 | **`partBBox` 与 `modelBboxM` 的换算系数** | 5 件样本里 0.5× 与 1.0× 都有（§5.7） | 每类在 1 件已知尺寸道具上实测一次 |
| 4 | **动画 clip 的播放速率/循环区间** | 数据有 4–6 个 clip/type 与起始 clip `playAnimNo`，但每个 clip 的 fps 与循环点未解 | 需要读动画表或逐帧数时长 |
| 5 | **被球滚到时的中断语义** | 原作运动件被吸附即消失，不存在"被撞飞"；但中断发生在接触的哪一帧、要不要保留速度给球，参考实现不涉及 | 实机录像 + 我们的吸附流程对齐 |
| 6 | **Spin/Flip/Path 的角速度是否与常量一致** | 只测了位置，没测角度 | 固定相机拍 3 s 数角度 |
| 7 | **`altUpdate` / `hover` 等 type 字段是否改变更新频率** | 字段存在但语义未解 | 对照两个只在 `altUpdate` 上不同的 type 的帧表现 |
| 8 | **云是否参与 zone 判定** | 观景器里 zone 是 2D 的，云在高空但仍有 zone 结果 | 需要看原作云的巡逻逻辑 |
| 9 | **原作是否在 30 Hz 之外做插值** | 速度 = cm/帧 ×30.3 的吻合度极高，倾向"无插值" | 【推断】保留为开放问题 |
| 10 | **129 vs 128 的差 1 件** | 源数据 129 件有运动，我们 511 件里只有 128；缺的是实例 #497 `Dummy Hit 01`（`moveType 20`） | 已知差集，非研究缺口；补道具时一起补 |

---

## 9. 建议：先做哪三类

排序标准是"没有它，关卡就不成立"（不是"实现难度"）。

| 顺序 | 类 | 件数 | 占比 | 理由 |
|---|---|---|---|---|
| **1** | **SporadicWalk** | 31 | 24% | 关卡的玩法主体就是"追着螃蟹滚"。31 件静止 = 整个院子是标本盒；而且它是唯一用满"走走停停 + 随机转向 + 待机动画"的类，做完就等于把"有状态运动"的骨架搭好 |
| **2** | **Hop** | 18 | 14% | 第二多，和 1 共用同一套螃蟹 prefab 与待机动画；状态机只有"计时 + 积分 + 落地"，是验证"物理式运动"的最小样例 |
| **3** | **WhackAMole** | 8 | 6% | 件数少但**视觉最抢眼**：旗杆 1.65 m 的升降、石灯笼、蜘蛛。它是这关"地里有东西"的记忆点，也是唯一只改 y、不用碰撞的类，实现成本极低 |
| 4 | Sway + Bob + Spin + Flip | 26 | 20% | 四类都是无状态解析解，可以一批做完；Sway 是院子里唯一的"植物活着"信号 |
| 5 | PathCollision + SlopingPath + BackAndForthNoYaw | 10 | 8% | 需要先解析路径表（§3.3，已经会解了），是"空间叙事"类运动 |
| 6 | Clouds | 5 | 4% | 和 SporadicWalk 共用"随机转向 + 匀速直线"骨架，只差"不贴地、不转向"，做 1 的时候顺手带上最省 |
| 7 | WaitForPlayer + DraggedAlong | 30 | 23% | 前者卡在 §8.1（触发后行为未知），后者卡在父链（7 件无父）；都可先按"静止 + 门"占位 |

31 + 18 + 8 + 26 + 10 + 5 + 30 = 128（占比按四舍五入，合计 99%）。

前 3 类做完 = **57/128 = 45%** 的运动件，且覆盖玩家在开局 10 秒内一定会看到的全部运动。

---

## 10. 风险与 PROVISIONAL

| 项 | 状态 | 影响 |
|---|---|---|
| Spin/Flip/Path 角速度 = `0.05 rad/帧` | 【常量】未实测 | 若原作不同，转速会偏；影响观感不影响玩法 |
| Hop 速度档 = 4/6/7/12/15 | 【常量】+ 1 档实测 | 只有 `size=2` 被实测（4 cm/帧） |
| 幅度系数（§5.7 最后一列） | 【PROVISIONAL】 | 每类需 1 次标定 |
| `partBBox ≈ 半个 modelBboxM` | 【PROVISIONAL】 | 见 §5.7 备注；直接影响 Sway 摆幅与 Bob 振幅 |
| 触发半径 200/300 cm | 【常量】 | 与我们的 `wake_radius_m = 12` 差 4–6 倍；**这是与我们的实现之间最明显的口径差**，建议单独记一条 parity 分歧 |
| WaitForPlayer 触发后行为 | 未知 | 15 件只能占位 |
| 参考实现本身是"二次实现" | 【推断】 | noclip 的更新规则是从游戏行为反推的，不是反编译原文；常数可能近似。所有【常量】都有这个前提 |

---

## 附录 A：Cancer 128 件运动实例（逐类）

`moveType`/`instance#`/`linkAction` 来自 PS2 实例记录；`itemId`/`def`/`inherit` 是我关 `placement.v2.json` 里的名字。`area/hitOnAreaNo` 全场为 `2/−1`（云为 `2/3`），故略去重复列。

**SporadicWalk（31 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `coconut_crab_005` | `coconut_crab` | 13 | 197 | 0 | none |
| `coconut_crab_008` | `coconut_crab` | 13 | 461 | 0 | none |
| `fiddler_crab_009` | `fiddler_crab` | 13 | 207 | 0 | none |
| `fiddler_crab_010` | `fiddler_crab` | 13 | 414 | 0 | none |
| `fiddler_crab_011` | `fiddler_crab` | 13 | 42 | 0 | none |
| `fiddler_crab_014` | `fiddler_crab` | 13 | 198 | 0 | none |
| `fiddler_crab_021` | `fiddler_crab` | 13 | 223 | 0 | none |
| `fiddler_crab_024` | `fiddler_crab` | 13 | 487 | 0 | none |
| `fiddler_crab_025` | `fiddler_crab` | 12 | 172 | 0 | none |
| `fiddler_crab_026` | `fiddler_crab` | 12 | 173 | 0 | none |
| `small_fiddler_crab_003` | `small_fiddler_crab` | 12 | 142 | 0 | none |
| `small_fiddler_crab_004` | `small_fiddler_crab` | 12 | 145 | 0 | none |
| `small_fiddler_crab_005` | `small_fiddler_crab` | 13 | 199 | 0 | none |
| `small_fiddler_crab_006` | `small_fiddler_crab` | 13 | 209 | 0 | none |
| `small_fiddler_crab_007` | `small_fiddler_crab` | 12 | 255 | 0 | none |
| `small_fiddler_crab_008` | `small_fiddler_crab` | 13 | 210 | 0 | none |
| `small_fiddler_crab_009` | `small_fiddler_crab` | 12 | 144 | 0 | none |
| `small_fiddler_crab_010` | `small_fiddler_crab` | 12 | 254 | 0 | none |
| `small_fiddler_crab_014` | `small_fiddler_crab` | 12 | 143 | 0 | none |
| `small_fiddler_crab_020` | `small_fiddler_crab` | 13 | 156 | 0 | none |
| `small_fiddler_crab_021` | `small_fiddler_crab` | 12 | 253 | 0 | none |
| `small_fiddler_crab_025` | `small_fiddler_crab` | 13 | 157 | 0 | none |
| `small_fiddler_crab_051` | `small_fiddler_crab` | 12 | 76 | 0 | none |
| `small_fiddler_crab_052` | `small_fiddler_crab` | 12 | 79 | 0 | none |
| `small_fiddler_crab_055` | `small_fiddler_crab` | 12 | 75 | 0 | none |
| `small_fiddler_crab_056` | `small_fiddler_crab` | 12 | 78 | 0 | none |
| `small_fiddler_crab_057` | `small_fiddler_crab` | 12 | 74 | 0 | none |
| `small_fiddler_crab_058` | `small_fiddler_crab` | 12 | 77 | 0 | none |
| `small_fiddler_crab_060` | `small_fiddler_crab` | 12 | 70 | 0 | none |
| `small_fiddler_crab_062` | `small_fiddler_crab` | 12 | 69 | 0 | none |
| `small_fiddler_crab_063` | `small_fiddler_crab` | 12 | 62 | 0 | none |

**Hop（18 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `fiddler_crab_001` | `fiddler_crab` | 3 | 416 | 0 | none |
| `fiddler_crab_002` | `fiddler_crab` | 3 | 302 | 0 | none |
| `fiddler_crab_003` | `fiddler_crab` | 3 | 304 | 0 | none |
| `fiddler_crab_005` | `fiddler_crab` | 3 | 301 | 0 | none |
| `fiddler_crab_006` | `fiddler_crab` | 3 | 303 | 0 | none |
| `small_fiddler_crab_001` | `small_fiddler_crab` | 3 | 146 | 0 | none |
| `small_fiddler_crab_002` | `small_fiddler_crab` | 3 | 147 | 0 | none |
| `small_fiddler_crab_026` | `small_fiddler_crab` | 3 | 385 | 0 | none |
| `small_fiddler_crab_027` | `small_fiddler_crab` | 3 | 384 | 0 | none |
| `small_fiddler_crab_028` | `small_fiddler_crab` | 3 | 386 | 0 | none |
| `small_fiddler_crab_029` | `small_fiddler_crab` | 3 | 383 | 0 | none |
| `small_fiddler_crab_030` | `small_fiddler_crab` | 3 | 121 | 0 | none |
| `small_fiddler_crab_031` | `small_fiddler_crab` | 3 | 119 | 0 | none |
| `small_fiddler_crab_032` | `small_fiddler_crab` | 3 | 123 | 0 | none |
| `small_fiddler_crab_033` | `small_fiddler_crab` | 3 | 122 | 0 | none |
| `small_fiddler_crab_034` | `small_fiddler_crab` | 3 | 120 | 0 | none |
| `small_fiddler_crab_047` | `small_fiddler_crab` | 3 | 348 | 0 | none |
| `small_fiddler_crab_054` | `small_fiddler_crab` | 3 | 323 | 0 | none |

**Sway（15 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `blue_crab_002` | `blue_crab` | 4 | 431 | 0 | full |
| `coconut_crab_002` | `coconut_crab` | 4 | 522 | 0 | none |
| `fiddler_crab_007` | `fiddler_crab` | 4 | 509 | 0 | full |
| `fiddler_crab_022` | `fiddler_crab` | 4 | 485 | 1 | full |
| `fiddler_crab_030` | `fiddler_crab` | 4 | 413 | 0 | full |
| `fire_extinguisher_002` | `fire_extinguisher` | 4 | 506 | 0 | none |
| `ladle_001` | `ladle` | 4 | 247 | 0 | none |
| `ladle_002` | `ladle` | 4 | 248 | 0 | none |
| `sunflower_001` | `sunflower` | 4 | 311 | 0 | none |
| `sunflower_002` | `sunflower` | 4 | 309 | 0 | none |
| `sunflower_003` | `sunflower` | 4 | 310 | 0 | none |
| `sunflower_004` | `sunflower` | 4 | 312 | 0 | none |
| `white_tulip_001` | `white_tulip` | 4 | 342 | 0 | none |
| `white_tulip_002` | `white_tulip` | 4 | 338 | 0 | none |
| `white_tulip_003` | `white_tulip` | 4 | 340 | 0 | none |

**DraggedAlong（15 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `blue_crab_001` | `blue_crab` | 18 | 472 | 0 | full |
| `coconut_crab_003` | `coconut_crab` | 18 | 166 | 0 | none |
| `coconut_crab_004` | `coconut_crab` | 18 | 126 | 0 | none |
| `doggy_001` | `doggy` | 18 | 512 | 1 | none |
| `fiddler_crab_015` | `fiddler_crab` | 18 | 469 | 0 | full |
| `fiddler_crab_016` | `fiddler_crab` | 18 | 470 | 0 | full |
| `fiddler_crab_017` | `fiddler_crab` | 18 | 471 | 0 | full |
| `fiddler_crab_018` | `fiddler_crab` | 18 | 468 | 0 | none |
| `fiddler_crab_023` | `fiddler_crab` | 18 | 419 | 1 | full |
| `fiddler_crab_036` | `fiddler_crab` | 18 | 110 | 0 | none |
| `small_fiddler_crab_036` | `small_fiddler_crab` | 18 | 408 | 1 | full |
| `small_fiddler_crab_038` | `small_fiddler_crab` | 18 | 345 | 0 | none |
| `small_fiddler_crab_041` | `small_fiddler_crab` | 18 | 344 | 0 | none |
| `small_fiddler_crab_049` | `small_fiddler_crab` | 18 | 511 | 0 | full |
| `small_fiddler_crab_053` | `small_fiddler_crab` | 18 | 467 | 0 | full |

**WaitForPlayer（15 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `fiddler_crab_012` | `fiddler_crab` | 7 | 104 | 0 | none |
| `fiddler_crab_013` | `fiddler_crab` | 7 | 168 | 0 | none |
| `fiddler_crab_019` | `fiddler_crab` | 7 | 163 | 0 | none |
| `fiddler_crab_020` | `fiddler_crab` | 7 | 162 | 0 | none |
| `fiddler_crab_031` | `fiddler_crab` | 7 | 500 | 0 | none |
| `fiddler_crab_033` | `fiddler_crab` | 7 | 463 | 0 | none |
| `small_fiddler_crab_011` | `small_fiddler_crab` | 10 | 445 | 6 | full |
| `small_fiddler_crab_012` | `small_fiddler_crab` | 10 | 446 | 6 | full |
| `small_fiddler_crab_013` | `small_fiddler_crab` | 10 | 442 | 6 | full |
| `small_fiddler_crab_015` | `small_fiddler_crab` | 10 | 448 | 6 | full |
| `small_fiddler_crab_016` | `small_fiddler_crab` | 10 | 447 | 6 | full |
| `small_fiddler_crab_017` | `small_fiddler_crab` | 10 | 444 | 6 | full |
| `small_fiddler_crab_018` | `small_fiddler_crab` | 10 | 449 | 6 | full |
| `small_fiddler_crab_019` | `small_fiddler_crab` | 10 | 443 | 6 | full |
| `spider_crab_001` | `spider_crab` | 7 | 214 | 0 | none |

**WhackAMole（8 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `flagstick_002` | `flagstick` | 5 | 394 | 1 | none |
| `garden_lantern_001` | `garden_lantern` | 5 | 518 | 0 | none |
| `garden_lantern_002` | `garden_lantern` | 5 | 514 | 0 | none |
| `small_fiddler_crab_046` | `small_fiddler_crab` | 5 | 67 | 0 | none |
| `small_fiddler_crab_061` | `small_fiddler_crab` | 5 | 63 | 0 | none |
| `small_fiddler_crab_064` | `small_fiddler_crab` | 5 | 64 | 0 | none |
| `small_fiddler_crab_066` | `small_fiddler_crab` | 5 | 60 | 0 | none |
| `spider_001` | `spider` | 5 | 68 | 0 | none |

**PathCollision（7 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `coconut_crab_009` | `coconut_crab` | 20 | 427 | 1 | none |
| `coconut_crab_010` | `coconut_crab` | 20 | 387 | 0 | none |
| `coconut_crab_011` | `coconut_crab` | 20 | 482 | 0 | none |
| `fiddler_crab_038` | `fiddler_crab` | 20 | 393 | 0 | none |
| `fiddler_crab_041` | `fiddler_crab` | 20 | 388 | 0 | none |
| `fiddler_crab_042` | `fiddler_crab` | 20 | 392 | 0 | none |
| `small_fiddler_crab_045` | `small_fiddler_crab` | 20 | 389 | 0 | none |

**Spin（7 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `dustbin_001` | `dustbin` | 1 | 57 | 0 | none |
| `fiddler_crab_032` | `fiddler_crab` | 1 | 480 | 0 | none |
| `ramen_001` | `ramen` | 1 | 502 | 0 | none |
| `round_table_001` | `round_table` | 1 | 450 | 0 | none |
| `shabu_shabu_pot_001` | `shabu_shabu_pot` | 1 | 465 | 0 | none |
| `small_fiddler_crab_035` | `small_fiddler_crab` | 1 | 407 | 1 | none |
| `spider_003` | `spider` | 1 | 43 | 0 | none |

**Clouds（5 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `scattered_clouds_001` | `scattered_clouds` | 16 | 1 | 0 | none |
| `scattered_clouds_002` | `scattered_clouds` | 16 | 24 | 0 | none |
| `scattered_clouds_003` | `scattered_clouds` | 16 | 25 | 0 | none |
| `scattered_clouds_004` | `scattered_clouds` | 16 | 26 | 0 | none |
| `scattered_clouds_005` | `scattered_clouds` | 16 | 27 | 0 | none |

**Flip（2 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `small_fiddler_crab_059` | `small_fiddler_crab` | 17 | 61 | 0 | none |
| `small_fiddler_crab_065` | `small_fiddler_crab` | 17 | 41 | 0 | none |

**Bob（2 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `small_fiddler_crab_050` | `small_fiddler_crab` | 2 | 66 | 0 | none |
| `spider_002` | `spider` | 2 | 65 | 0 | none |

**SlopingPath（2 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `bookstand_002` | `bookstand` | 19 | 474 | 0 | none |
| `horseshoe_crab_007` | `horseshoe_crab` | 19 | 409 | 0 | none |

**BackAndForthNoYaw（1 件）**

| itemId | def | moveType | instance# | linkAction | inherit |
|---|---|---|---|---|---|
| `fiddler_crab_034` | `fiddler_crab` | 21 | 424 | 0 | none |


---

## 附录 B：复现

全部脚本与产物在仓库外（`~/KatamariRef/`），本文不含二进制内容。

| 目的 | 做法 |
|---|---|
| 解运动表 | 读 `noclip_export/data/KatamariDamacy/movementBlock.bin`：关卡表 `u32[levelIndex]` → 地址；项 = 6 B `(s16 backup, s16 path, s16 motionID)`；`levelIndex=11` 为 Cancer |
| 解路径表 | 读 `pathBlock.bin`：`u32 pointStart` + `f32 speed`；点 16 B，`w==255` 结束；`speed<0` ⇒ 用 `speedTable` |
| 解速度/尺寸档 | `objectBlock + id*0x24 + 0x13`（`speedIndex`）、`collectionBlock + id*8 + 0x02`（`size`） |
| 复现周期测量 | `~/KatamariRef/tools/noclip_capture/pw_capture.mjs`（虚拟时钟，`EVERY=23`、`STEP=66.6667`）→ `_tracks.json` → 峰峰值/峰间隔 |
| 复现像素变化 | `pngdiff.py --thresh --min-area --json` |
| 原始帧 | `~/KatamariRef/viewer_evidence/noclip_frames/cancer_crab_fine_f000..f029.png` |

参考实现（只读，未进仓库）：noclip.website `src/KatamariDamacy/{bin.ts,objects.ts,scenes.ts}`；`objects.ts` 里的 `motion_*` 函数是本文【常量】的唯一出处。
