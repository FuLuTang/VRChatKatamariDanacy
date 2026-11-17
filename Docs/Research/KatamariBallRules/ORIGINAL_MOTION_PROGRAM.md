# 原作运动程序：13 类运动的参数、来源与每帧成本

| 项 | 值 |
|---|---|
| 关卡 | `_ref_cancer`（Make Cancer，PS2 `level 11` / 起始区 2，noclip `clvl1`，"怀旧关：蟹座小院"） |
| 目标 | 把"道具会动"从一句 `moveType` 拆成 13 个可实现的运动程序：参数、取值范围、每帧做什么、参数从哪来 |
| 结论 1 | 129 件有运动（我关 511 件里 128 件；差的那 1 件是实例 #497 `Dummy Hit 01`，是非可收集的命中判定体，不是道具——差集已全部查明，见 §11.3），只落在 **13 类**上 |
| 结论 2 | **运动表、路径表、速度档、尺寸档都能从 BIN 自行解出**（已解，见 §3.2–3.4），并已落成两份 JSON（§11）；只有"每类怎么动"的规则和常数不在数据里 |
| 结论 3 | 13 类里 **5 类是无状态解析解**（Spin/Flip/Bob/Sway/WhackAMole），2 类只做检测（WaitForPlayer/DraggedAlong），6 类有状态（SporadicWalk/Hop/Clouds/PathCollision/SlopingPath/BackAndForthNoYaw） |
| 结论 4 | 幅度不是自由参数，而是**道具自身高度**的固定倍数，且换算因子已在 5 件道具上收敛为常数 `b = modelBboxM[1]/2`：WhackAMole 1.5×全高、Bob 0.075–0.086×、Sway 水平 0.34×/垂直 0.030×（§5.7 已改正推导，不再是"待标定"） |
| 结论 5 | 相位/定时在原作里是进程内随机数，**数据里没有**；我们的方案沿用 D-12（确定性 hash + 作者覆盖），只需分布正确，不需要逐件复刻 |
| 结论 6 | WaitForPlayer 的"触发后干什么"在参考实现里是空实现，**推不出**；而且 15 件里只有 7 件有"走近触发"，另 8 件的条件是 `inZone && parentRolled`（原作里被写死 false），见 §4.5 |
| 结论 7 | 建议先做 SporadicWalk → Hop → WhackAMole，覆盖 57/128 = 45% 的运动件，也是玩家最常看到的三种 |
| 结论 8 | 13 类已写成可直接照写的伪代码（§12）；实现前必须补测的只剩 7 条，且其中 4 条在观景器里就能做（§13） |
| 口径 | 【数据】= 从 BIN/实例记录解出；【实测】= 从渲染帧量出；【常量】= 参考实现写死（原作在 ELF 代码里）；【推断】/【PROVISIONAL】= 未验证 |
| 前作 | 周期与速度的帧级测量见 [ORIGINAL_ANIMATION_OBSERVATIONS.md](<ORIGINAL_ANIMATION_OBSERVATIONS.md>)；二进制格式见 [NOCLIP_KATAMARI_FORMAT.md](<NOCLIP_KATAMARI_FORMAT.md>) |
| 边界 | 只读 `~/KatamariRef`；本文只写结论、参数与推导方法，不含原版二进制、反编译文本或模型数据 |
| 产物 | `~/KatamariRef/noclip_export/kse_out/{motion_table,path_table}.json` + 只读解码器 `~/KatamariRef/tools/katamari_stage_extract/decode_motion_tables.py`（§11） |
| 未做 | 没跑 Unity、没改代码、没做 Git 写操作、没实测 §13 的清单；本文所有数值都不是我们当前实现里的值 |

---

## 1. 一句话结论

原作的"道具会动"= **一张每关 6 字节/项的运动表**（给每件道具一个 `motionID` + 一条路径 + 一个备用索引）+ **一张路径表**（折线点 + 速度）+ **一套按 `motionID` 分派的更新规则**（规则和常数全在代码里）。

前两张表我们能自己解（§3），第三张只能靠参考实现（noclip.website，MIT + 反编译说明）或实机录像。所以"13 类运动"的**分类是数据的**，**参数一半是数据的**（速度、路径、尺寸档、方向），**另一半是代码常量**（角速度、周期、重力、触发半径、幅度系数）；相位是运行时的，任何来源都没有。

---

## 2. 参数只有三种来源

| 来源 | 具体内容 | 我们拿得到吗 | 例子 |
|---|---|---|---|
| **二进制表** | 运动表（6 B/项）、路径表（8 B/记录 + 16 B/点）、type 的 `speedIndex`/`size`/`stayLevel`/`animated` | ✅ 已自行解析，见 §3 | `motionID=0x19`、`pathIndex=23`、路径速度 `0.8`、`size=2` |
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

按这个结构解了 Cancer（`levelIndex=11`），16 项与运行时观测逐项一致（这是"运动表可自解"的直接证据）：

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

解出的 4 条相关路径（Cancer）：

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
| 枢轴 | 不是原地转：位置每帧从出生点重算 = 出生点 + `R(spawn)·(sin 2e, −cos 2e, 0)·h` + `(0,h,0)`，其中 `h = partBBox.max[1] = 全高/2` ⇒ 越高摆得越开 | 【常量】+ 模型 bbox |
| 例外 | `BALANCEDOLL01_C`（不倒翁）只倾斜、不平移 | 【常量】 |

实测（向日葵，全高 240.5 cm）：水平摆幅 p-p **82 cm**、垂直 p-p **7.19 cm**、垂直通道周期 **1.52 s**。
垂直通道是半周期，因为垂直量 ∝ `1 − cos 2e`（两倍频）。把 `h = 全高/2 = 120` 代回 `2·h·sin20°` 得 82.2 cm、代回 `h·(1−cos20°)` 得 7.25 cm——**两条独立通道同时吻合**，且都指向 `h = 全高/2`。推导细节与"为什么是 2θ"见 §5.7。

### 4.4 DraggedAlong（15 件：`fiddler_crab` 6、`small_fiddler_crab` 5、`coconut_crab` 2、蓝蟹 1、小狗 1）

动作号是 `SetZone`，**没有任何自身运动**：位置由父件变换驱动。参数只有 zone（`getZone(pos, 100)`）。

关键事实：这 15 件里**只有 8 件在我们数据里解析到了父件**（`inherit: full`），另外 7 件无父 ⇒ 原作里它们也不动。做实现时不能默认"DraggedAlong = 会动"。

### 4.5 WaitForPlayer（15 件：`small_fiddler_crab` 8、`fiddler_crab` 6、蜘蛛蟹 1）

| 参数 | 值 | 来源 |
|---|---|---|
| 触发门 A（7 件） | `moveType 7` / `motionID 0x00`：`dist(观察者, pos) < panicRadii[sizeGroup]`，本关这 7 件都是 `size=2` ⇒ **200 cm** | 【数据】+【常量】 |
| 触发门 B（8 件） | `moveType 10` / `motionID 0x04`：`inZone && parentRolled`，参考实现把 `parentRolled` 写死 `false` ⇒ **观景器里永不触发**，原作真实条件未知 | 【常量】 |
| 附加条件 | 观察者所在 zone 必须等于该件的 zone；`sizeGrouping[size] ≤ 2`（太大件不触发） | 【常量】 |
| 触发后 | `useAltMotion = true` ⇒ 走 `alt` 动作 `0x05` / `0x08`，**参考实现里是空实现** | 【常量】 |

⇒ 15 件里只有 7 件有"走近触发"；另 8 件的门不知道。而且两者都不知道触发后干什么。见 §8.1 与 §14。

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

与公式对照：周期 `90 帧 × 33 ms = 3.00 s` ✅（两件都落在 ±0.2 s 内）；振幅 `0.15 × h` 在 `h = 全高/2` 下给出 ±0.80 cm（蜘蛛实测 ±0.81，全高 10.7）✅。

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

因此：`Hop` 的周期只能按【常量】写（0–8.5 s 均布），**不能**用这组数据反推；要验证必须用能覆盖整个院子的全景相机 + 更长序列。

最低点这一项**已经能解释，不需要再测**：参考实现的积分是「先加 g、再位移」的半隐式欧拉，dt=1 时

```
y_n = −v·n + g·n(n+1)/2          v=4, g=1 ⇒ −3, −5, −6, −6, −5, −3, 0
```

谷底是 **6 cm**（n=3、4 两帧），不是连续解 `v²/2g = 8 cm`——离散化让深度少了 25%，谷底只持续 2 帧（0.067 s）。实测 6.60 / 6.05 cm 正好落在 6 cm 上，两件都是同一档 `size`，所以这组数据**确认**了「dt=1 + 半隐式」这个积分口径（原先写的「欠采样、8 cm 无法确认」是拿连续解当预期，错的预期）。要看跳的次数仍然需要全景长序列。

### 5.6 Spin / Flip：未逐帧测

两者位置完全不变（实测 y/x/z 全程恒定），与"只改 `euler`、不平移"的预期一致——但**位置不变不能证明它在转**（也可能是不在视野内没被更新，见 §5.5 的口径问题）。画面里能看到圆桌、垃圾桶在转（`pngdiff` 在对应区域有变化），说明它们确实在转。角速度 `0.05 rad/帧 ⇒ 4.19 s/圈` 是【常量】，**未实测验证**，标 PROVISIONAL。要验证只需固定相机拍 3 s、数转过的角度。

### 5.7 标定表：幅度都是"半高"的固定倍数

幅度全部挂在 bbox 上，所以实现时应当用我们自己道具的 `modelBboxM[1]` 乘系数，不写死厘米。关键是**换算因子**：观景器的 `partBBox.max[1]` 与我们导出的 `modelBboxM[1]` 之间是什么关系。本文的答案是**常数 1/2**，5 件道具全部吻合。

| 类 | 量 | 实测（cm） | 原式（`h = partBBox.max[1]`） | 化简（`h = modelBboxM[1]/2`） | × 我方 `modelBboxM[1]` |
|---|---|---|---|---|---|
| Bob | 振幅 | ±0.68 / ±0.81 | `0.15·h` | — | **0.075–0.086** |
| WhackAMole | 行程 | 12.37 / 16.09 / 165.0 | `h + (h − (−h)) = 3h` | `1.5·全高` | **1.50–1.58** |
| Sway | 水平 p-p | 82.0 | `2·h·sin(2·10°)` | `0.342·全高` | **0.34** |
| Sway | 垂直 p-p | 7.19 | `h·(1 − cos(2·10°))` | `0.030·全高` | **0.030** |
| Hop | 最低点 | 6.0–6.6 | `v²/2g = 8` | — | 由 `size` 档给，不用高度 |
| Bob | 周期 | 2.83 / 3.07 s | 90 帧 = 3.00 s | — | 常量 |
| Sway | 倾角周期 | （1.52 s 是半周期） | 90 帧 = 3.00 s | — | 常量 |
| WhackAMole | 上升 | 0.7 s | 22.5 帧 = 0.75 s | — | 常量 |

**为什么是 `sin 2θ` 而不是 `sin θ`（这是上一版推错的地方）**：Sway 的位移不是"绕枢轴转 ±10°"那么简单。原作取"局部下方向" `(sin e, −cos e, 0)`，用**含 `euler.z = e` 的 modelMatrix** 把它转到世界——也就是这个方向被同一个旋转又转了一次：

```
R_z(e) · (sin e, −cos e, 0) = (sin 2e, −cos 2e, 0)
```

于是水平位移 = `h·sin 2e`（峰峰 `2h·sin20°`），垂直位移 = `h·(1 − cos 2e)`（峰峰 `h(1−cos20°)`）。按 `h = 240.5`（向日葵全高）算会得到 164 / 14.5 cm，比实测大一倍；按 `h = 120` 算得 82.2 / 7.25 cm，与实测 82.0 / 7.19 **两通道同时吻合**。这就同时确定了"`h = 全高/2`"和"Sway 的相位是 2θ"。

**含义**：上一版说"旗杆/蜘蛛/小蟹是 0.5×、向日葵是 1.0×，所以系数只能当初始值"是错的——那个 1.0× 来自我用的错误公式。现在 5 件（旗杆 110、蜘蛛 10.7、小蟹 ≈9、石灯笼、向日葵 240.5）口径一致，上表最后一列可以当**定稿值**用。仍然保留一条风险：样本都是"直立/矮胖"件，非直立件（荷叶、圆桌）没验过（§13 M8）。

---

## 6. 每帧成本模型

### 6.1 逐类每帧工作

| 类 | 每帧每件计算 | 有状态 | 解析解？ | 建议更新率 |
|---|---|---|---|---|
| Spin | `euler.y += 0.05·dt` | 否 | ✅ 纯 `f(t)` | 5 Hz（只转，碰撞体不变） |
| Flip | `euler.x −= 0.05·dt` | 否 | ✅ | 5 Hz |
| Bob | `θ += π/45`；`y = y0 + A·sin θ` | 否（只有起始随机） | ✅ | 10 Hz |
| Sway | `θ += π/45`；`euler.z = sin θ·10°`；重建基向量 + 重算位置 | 否 | ✅ | 15 Hz |
| WhackAMole | 计时；`θ += π/45`；`y = f(θ, state)` | 否 | ✅ 分段 | **30 Hz** |
| Hop | 计时；`v += g·dt`；`p += v·dt`；落地检测（1 次地面/支撑查询） | 是 | ✗ | **30 Hz** |
| SporadicWalk | 转向计时；前进一步（1 次贴地查询 + 1 次 zone 判定）；偶尔 7 次换向判定 | 是 | ✗ | 15 Hz |
| Clouds | 同上但**无**贴地查询、无 yaw | 是 | ✗ | 10 Hz（云不参与碰撞） |
| PathCollision / SlopingPath | 到点判定；推进；归一化；yaw/pitch 缓动 | 是 | ✗ | 15 Hz |
| BackAndForthNoYaw | 到点 ⇒ 反向 | 是 | ✗ | 15 Hz |
| WaitForPlayer | 1 次距离 + 1 次 zone 判定 | 否 | ✅ | 5 Hz |
| DraggedAlong | 无 | — | — | 0 |

更新率不是"越省越好"：碰撞体每 tick 跟着变换走，滞后 = 峰值速度 × (1/Hz)。WhackAMole 的正弦段峰值 3.46 m/s ⇒ 10 Hz 时每 tick 35 cm，会被看成穿插；Hop 的整个下陷只有 8 帧，10 Hz 直接漏掉。所以 **Hop 与 WhackAMole 按 30 Hz**（件数少，18+8 件只占 780 次/秒），Sway 15 Hz，其余按上表。逐类峰值位移见 §12.0。

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
| 按 §12.0 的碰撞需求取频率（Hop/Whack 30 Hz、Sway 15 Hz 等） | 1,810 | 14.6% |

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
| 1 | **WaitForPlayer 触发后干什么**（15 件），以及 `moveType 10` 那 8 件的真实条件 | `alt` 动作 `0x05`/`0x08` 在参考实现里是空实现；`0x04` 的条件 `parentRolled` 又被写死 `false` | 实机录像，或读 ELF 的动作分派分支（§13 M1/M2） |
| 2 | **相位/定时的具体值** | 原作是进程内随机；数据无字段 | 不需要复刻：只要分布对（hop 0–8.5 s 均匀、whack 2–7 s 均匀），错落感就成立 |
| 3 | ~~`partBBox` 与 `modelBboxM` 的换算系数~~ **已收敛** | 5 件上一致为 `= modelBboxM[1]/2`；原来的 1.0× 按 `2h·sin10°` 推错（§5.7 已改正） | 剩余风险只在非直立件，§13 M8 补 1 件 |
| 4 | **动画 clip 的播放速率/循环区间** | 数据有 4–6 个 clip/type 与起始 clip `playAnimNo`，但每个 clip 的 fps 与循环点未解 | 需要读动画表或逐帧数时长 |
| 5 | **被球滚到时的中断语义** | 原作运动件被吸附即消失，不存在"被撞飞"；但中断发生在接触的哪一帧、要不要保留速度给球，参考实现不涉及 | 实机录像 + 我们的吸附流程对齐 |
| 6 | **Spin/Flip/Path 的角速度是否与常量一致** | 只测了位置，没测角度 | 固定相机 + 16.667 ms 虚拟时钟数角度（§13 M4） |
| 7 | **`altUpdate` / `hover` 等 type 字段是否改变更新频率** | 字段存在但语义未解 | 对照两个只在 `altUpdate` 上不同的 type 的帧表现 |
| 8 | **云是否参与 zone 判定** | 观景器里 zone 是 2D 的，云在高空但仍有 zone 结果 | 需要看原作云的巡逻逻辑 |
| 9 | **原作是否在 30 Hz 之外做插值** | 速度 = cm/帧 ×30.3 的吻合度极高，倾向"无插值" | 【推断】保留为开放问题 |
| 10 | ~~129 vs 128 的差 1 件~~ **已查明** | 差集是 12 件 `Dummy Hit 01/02/03`（非可收集命中体） + 1 件 `Lalala`（无网格精灵）；有运动的只有 #497 `Dummy Hit 01`（§11.3） | 两份 JSON 已覆盖全部 524 条记录，`byClass`(129)/`byClassRendered`(128) 分开给 |

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
| 幅度系数（§5.7 最后一列） | 【实测】5 件交叉验证 | 只在非直立件上还有风险（§13 M8） |
| `partBBox = modelBboxM/2` | 【实测】5 件 | 见 §5.7；直接决定 Sway 摆幅与 Bob 振幅 |
| 触发半径 200/300 cm | 【常量】 | 与我们的 `wake_radius_m = 12` 差 4–6 倍，但**两者概念不同**（触发门 vs 更新半径），结论与待拍板见 §14；不动我们的 12 |
| WaitForPlayer 触发后行为 | 未知 | 只有 7 件有门、8 件门未知；15 件都只能占位（§4.5、§13 M1/M2） |
| 参考实现本身是"二次实现" | 【推断】 | noclip 的更新规则是从游戏行为反推的，不是反编译原文；常数可能近似。所有【常量】都有这个前提 |

---

---

## 11. 数据产物：运动表与路径表已落成 JSON

前面几节说"表能自解"。现在它们已经是可消费的数据，不在文档里口述，而是两份文件：

| 文件 | 内容 | 条目 |
|---|---|---|
| `~/KatamariRef/noclip_export/kse_out/motion_table.json` | 每关的运动表项 + 每件道具的运动分配 | 524 件 spawn 记录、129 件有运动、15 个 `moveType`、15 个表项 |
| `~/KatamariRef/noclip_export/kse_out/path_table.json` | 被引用的路径折线 | 4 条（0/15/22/23），共 275 个点 |
| `~/KatamariRef/tools/katamari_stage_extract/decode_motion_tables.py` | 只读解码器，可从原始 BIN 重新生成上面两份 | 单文件，无第三方依赖 |

复现（同一份 BIN 跑两次，输出**字节一致**，sha256 相同）：

```
python3 ~/KatamariRef/tools/katamari_stage_extract/decode_motion_tables.py --level 11 \
  --props-doc ~/Documents/Github/VRChatKatamariDanacy/Docs/Design/Levels/_ref_cancer/placement.v2.json
```

### 11.1 文件头写了什么

两份 JSON 都自带 `provenance`（4 个输入 BIN 的字节数与 sha256、名字表来源、可选的 `props-doc` 及其 sha256）、`units`、`fieldNotes`；`motion_table.json` 还带 `constantTables`（`speedTable`/`sizeGrouping`/`hopSpeeds`/`panicRadii`）与 `countsBasis`。

单位换算写死并标注：

```
1 cm/游戏帧 = 0.305 m/s      # 实测比值 30.3–30.8 cm/s per (cm/帧)，取 30.5
```

产物的 sha256（用来确认复现结果一致）：

```
motion_table.json  371178 B  d156723cebcf372f6b056081bf53ff158f06022605e836942f739dbda1609b4b
path_table.json     19900 B  9cd6a54b8dc04968d0bbebcc27eb5bde54c18fb72a041eeb600a8d6752d079fd
```

`counts` 字段自带校验：`{"instances": 524, "moving": 129, "distinctMoveTypes": 15, "motionEntries": 15}`，`path_table` 的 4 条路径共 275 个点。

### 11.2 `motion_table.json` 的字段

每条运动表项（`levels[].motionEntries[]`）：

| 字段 | 含义 |
|---|---|
| `moveType` | 本关索引（同一数字在不同关是不同程序） |
| `motionID` / `motionName` | 程序号；enum 里没有名字的给 `null`，不编 |
| `motionClass` | 我们 13 类里的名字（`Misc` 按 `motionID` 细分，`SetZone`+`0x25`=DraggedAlong） |
| `backupIndex` / `pathIndex` | 表项另外两个 s16；`pathIndex` 0 表示"不用路径" |
| `pathSpeedCmPerFrame` | 路径自带速度（`null` = 该路径速度是哨兵 −1） |
| `pathSpeedIsSentinel` | 速度 <0，改由 `speedTable[speedIndex]` 决定 |
| `mainAction` / `altAction` | 动作号与名字（`0x05`/`0x08` 这类无名动作照实写 `Unk0x05`） |
| `translatesAlongPath` | 该类是否沿路径平移 |

每件道具（`instances[]`，524 条，含我们没渲染的 13 条）：

| 字段 | 含义 |
|---|---|
| `instanceIndex` | 与 `instances.json` **同一编号**，也和 `placement.v2.json` 的 `notes: "instance=#N"` 对得上 |
| `name` / `internalName` / `nameIdx` | 英文名/内部名/名字索引（来自本地数据挖掘表，可缺） |
| `moveType` / `motionClass` / `motionID` | 分配结果；`-1` 记 `static` |
| `size` / `sizeGroup` | 尺寸档（决定 hop 速度与触发半径） |
| `speedIndex` / `speedCmPerFrame` / `speedMps` / `speedSource` | 表速度及其出处（路径记录 / `speedTable` / 静态） |
| `translationSpeedCmPerFrame` | **程序实际用哪个速度平移**：路径类用路径速度、SporadicWalk/Clouds 用 `speedTable`、Hop 用 `hopSpeedCmPerFrame`、不平移的类给 `null` |
| `stayLevel` / `animated` / `dummyParent` | 道具定义里的三个开关 |
| `hitOnAreaNo` / `dispOnAreaNo` / `locPosType` / `parentTableIndex` | spawn 记录原字段 |
| `positionCm` | spawn 平移（cm，关卡坐标） |
| `inLevelProps` / `exclusion` | 我们是否渲染这件；不渲染的写出原因 |

`zone` 字段：**数据里没有**。原作是运行时用地图 zone 碰撞向上打一条射线取三角形区号；spawn 记录里的 `hitOnAreaNo`/`dispOnAreaNo` 是区域掩码，不是 zone 号。这一条写在 `fieldNotes.zone` 里，不拿别的字段顶替。

### 11.3 那"缺的 1 件"找到了，而且不止 1 件

129 件有运动的 spawn 记录里，128 件在我们 511 件道具里，差的 1 件是 **实例 #497 `Dummy Hit 01`**（`moveType 20`、`PathCollision`、路径 23）。它不在道具里不是数据丢了，而是这一类**本来就不是道具**：

| 差集 | 件数 | 原因（已核实） |
|---|---|---|
| `Dummy Hit 01/02/03`（`monoNameIdx` 631/499/619） | 12 | 非可收集的**命中判定体**，无网格；`original_defs.json` 里它们单独记在 `dummyHitVolumes`，`defs` 里没有 |
| `Lalala`（`monoNameIdx` 1695） | 1 | 可收集但社区模型包里没有网格（精灵类道具） |

所以 129 = 128 + 1，两份 JSON 现在**覆盖全部 524 条记录**，`counts.byClass`（129）与 `counts.byClassRendered`（128）分开给，差的就是 #497。

### 11.4 只能解 Cancer 的运动表是暂时限制，不是结构限制

`moveType` 是逐关索引且**没有计数头**，所以"这关用到了哪些 `moveType`"只能从该关的 spawn 表读。运动表/路径表的地址对 19 关全部解出来了（`levels[].motionListAddr` / `pathListAddr`，空关给 `null`，例如 level 13 无运动表），但只有 Cancer 的 spawn 表被下载过（`17f590/` 四个文件）。脚本对任意关卡都能跑，缺的只是那关的任务包：

```
python3 .../decode_motion_tables.py --level 4      # 只要有 17f590 里对应的 4 个文件
```

给 lead 的判断：**补齐其它关不需要改代码，只需要把对应任务包下下来**（每关 4 个文件）。脚本会直接把缺的文件名打出来，例如 `--level 4` ⇒ `mission package files not downloaded: 13e462, 13e553, 13e68e, 13e7b1`；level 13 则没有运动表（`motionListAddr: null`），All-static 关天然支持。

---

## 12. 13 个类的伪代码（给实现者）

这一节是可直接照写的规格。每类给：`state`、`init`、`update(state, dt, ctx)`、每帧读什么、碰撞影响、被吸附时怎么断。数值常量全部来自 §3/§5，不在这里重复推导。

### 12.0 共用约定

```text
dt      = 本帧经过的游戏帧数（30 Hz 计），不是秒
          = clamp(elapsedSeconds * 30, 0, 2)      // 原作 clamp(deltaTime/33, 0, 2)
          实测 STEP=66.667 ms 时恒为 2（一次渲染 2 游戏帧）

ctx     = {
            spawnPos, spawnQuat,      // 出生变换，永不被运动改写（原作每帧从它重算）
            halfHeight, halfDepth,    // = modelBboxM[1]/2、modelBboxM[2]/2（§5.7 已在 5 件上验证）
            sizeGroup,                // = sizeGrouping[size]
            ground(pos, depth) -> hit|null      // 向上射线，返回 {y, normal, zone}
            zoneAt(pos, depth) -> int           // 同上，只取 zone
            nearObjects(pos, radius) -> [...]   // 同区、AABB 相交的候选件
            ball {pos, radius, speed}, parent, derived
          }
```

三条全局规则，先于任何类：

1. **位置基准**：所有类都从 `spawnPos` 出发算绝对位置，不做增量累积（原作如此）。这样冻结/挂起/恢复不会漂移；只有 `SporadicWalk`/`Clouds`/路径三类真的在积分位置。
2. **姿态合成**：`euler` 只由运动写，`modelMatrix = R(spawnQuat) · R_euler · translate(spawnPos)`。`adjustPitch` 时 `baseMatrix` 被地面法线覆盖（路径类）。
3. **中断**（被球吸附 / 父件坠落，对应本仓 D-14 "子件挂起"）：
   - 球进入吸附判定 → 该件当帧从运动集合移除，停写变换，交还吸附流程；**不产生"被撞飞"**（原作没有，观景器也没有）。
   - 父件被吸附或坠落 → 子件 `suspend()`：跳过 `update`，保留"相对父件的局部位姿"最后一帧值，`timer`/`angle`/`phase` 一律不推进。恢复时从挂起那帧的 `state` 原样继续 → 观感上是"停在半空，父件一落地就接着走"。
   - 父件从不存在的（`inherit=none`，DraggedAlong 7 件 / WaitForPlayer 7 件）本就无父，不参与挂起。

**更新率由位移决定，不由类决定**（碰撞体是每 tick 跟着变换走的，滞后 = 速度 × (1/Hz)）：

| 类 | 峰值速度（cm/s） | 每 tick 位移 @10 Hz | 建议 Hz | 依据 |
|---|---|---|---|---|
| Hop | 450（15 cm/帧） | 45 cm，但整个下陷只有 8 帧 | **30** | 10 Hz 会把 8 帧的事件整段漏掉 |
| WhackAMole | 346（1.65 m 的正弦段，峰值 = π/2 × 均值 2.2 m/s） | 35 cm | **30** | 35 cm 的穿插会被看见 |
| Sway | 88（正弦段峰值，出现在经过中点时） | 8.8 cm | 15 | 碰撞误差可接受；频率主要为了让摆动不生硬 |
| SporadicWalk | 30.5 | 3 cm | 15 | — |
| Clouds | 303（`speedIndex=6`） | 30 cm | 10 | 云在头顶，不参与碰撞 |
| 路径三兄弟 | 18–37 | ≤4 cm | 15 | 沿固定折线，可预测 |
| Spin / Flip / WaitForPlayer | 只转/微动 | — | 5 | 碰撞体不动（Spin/Flip 转 8.6°/tick 也够） |
| Bob | 4.8（1.6 cm 峰峰 / 3 s） | 0.5 cm | 10 | 幅度小到看不出来，10 Hz 只为不抖 |

按这张表全量更新 = 1 810 次/秒 = 预算的 14.6%（§6.2 的 12 420/s），所以"按碰撞需要取频率"比"统一 10 Hz 省预算"更对：省下来的预算本来就够。

### 12.1 `Spin`（motionID 0x15，7 件）

```text
state: {}                                  // 无状态，1 个 float 都不用存
update(state, dt, ctx):
    euler.y += 0.05 * dt                   // 0.05 rad/帧 → 4.19 s/圈
每帧读: 无（不查地面、不查 zone、不查球）
碰撞: 纯旋转；球体接触判定用当前 AABB，旋转必须同步给碰撞体
中断: 直接冻结在最后角度
```

### 12.2 `Flip`（motionID 0x1E，2 件）

```text
state: {}
update(state, dt, ctx):
    euler.x -= 0.05 * dt
其余同 Spin（符号相反，绕 X）
```

### 12.3 `Bob`（motionID 0x16，2 件）

```text
state: { timer = -1, angle = 0 }           // 2 个 float
update(state, dt, ctx):
    if timer == -1: timer = rand() * 60            // 0–2 s 随机待机
    if timer < dt:                                  // 待机结束，进入摆动
        angle += dt * (PI / 45)                     // 90 帧一个整周期 = 3.0 s
        pos.y = spawnPos.y + halfHeight * 0.15 * sin(angle)
    else:
        timer -= dt
每帧读: 无
碰撞: 垂直 ±0.15·halfHeight（蜘蛛实测 ±0.8 cm）；Bob 幅度 1.35–1.61 cm 峰峰，10 Hz 已富余
中断: 冻结；恢复时 timer/angle 不推进 → 不会跳相位
```

### 12.4 `Sway`（motionID 0x20，15 件）

```text
state: { angle = 0 }                        // 1 个 float，另加 euler.z 输出
update(state, dt, ctx):
    angle += dt * (PI / 45)                 // 3.0 s 整周期
    euler.z = sin(angle) * (TAU / 36)       // ±10°
    if objectId != BALANCEDOLL01_C:          // 玩具不倒翁那件原作跳过平移
        // 原作把"局部下方向"用含 euler.z 的 modelMatrix 转到世界，再绕底部枢轴放回。
        // 化简后（R_z(e)·(sin e, -cos e, 0) = (sin 2e, -cos 2e, 0)）：
        off = R(spawnQuat) * (sin(2*euler.z), -cos(2*euler.z), 0) * halfHeight
        pos = spawnPos + off + (0, halfHeight, 0)
每帧读: 无（只用出生变换与 halfHeight）
碰撞: 位移是椭圆，水平峰峰 0.342·modelBboxM[1]、垂直峰峰 0.030·modelBboxM[1]
      （向日葵实测 82.0 / 7.19 cm vs 预测 82.2 / 7.25，两通道独立吻合）；
      峰值速度 0.88 m/s（经过中点时），15 Hz 下每 tick 5.9 cm
中断: 冻结姿态；15 件里有 4 件 inherit=full，父件坠落时按 12.0 规则挂起
```

> 实现者注意：不要用"绕枢轴旋转 ±10°"去近似。水平峰峰的真值不是 `2h·sin10°`，而是 `2h·sin20°` = 0.342·全高（旋转后的方向被同一旋转又转了一次）；用前者会得到一半的摆幅。

### 12.5 `WhackAMole`（motionID 0x22，8 件）

```text
state: { timer = -1, angle = 0, state = -1, step = PI/45 }  // 4 个 float，state 初值必须是 -1
update(state, dt, ctx):
    if timer == -1:
        timer = rand() * 150 + 60            // 2–7 s 停在这个端点
        angle = PI / 2
        state = (state + 1) % 2              // -1→0；0=上升段, 1=下沉段
        step  = PI / 45                      // 22.5 帧 = 0.75 s
    if timer < dt:
        angle += step * dt
        if angle > PI:
            angle = 0
            timer = -1                        // 本段结束，回到"停"状态
        b = halfHeight
        depth = b + (b - (-b))                // = 1.5 * modelBboxM[1]
        pos.y = spawnPos.y + depth * (state == 0 ? 1 - sin(angle) : sin(angle))
    else:
        timer -= dt
每帧读: 无
注意: state 初值 **-1**（原作 MotionState 的初值），第一次 `(state+1)%2` 得 0 ⇒ **首段是上升**，
      从出生高度往上升，没有瞬移。（初值若写成 0，首段会变成下沉且第一帧从最高点起跳 —— 
      这是 §15 里已裁定的一处实现差异。）
碰撞: 垂直行程 1.5·modelBboxM[1]（旗杆实测 165 cm = 1.5×110），0.75 s 走完 → 均值 2.2 m/s、峰值 3.46 m/s
中断: 冻结在当下高度；若球此时压在它上方，恢复后从同一高度继续
```

### 12.6 `Hop`（motionID 0x19，18 件）

```text
state: { timer = -1, g = 0, vy = 0, speed = hopSpeeds[sizeGroup], supporter = null }
init:                                            // 只在出生时做一次
    for other in nearObjects(spawnPos, r1 + r2):
        if other.dispOnAreaNo == self.dispOnAreaNo and landOn(other, spawnPos, 5):
            supporter = other; break
    speed = hopSpeeds[sizeGroup]                 // 4/6/7/12/15 cm/帧
update(state, dt, ctx):
    if timer == -1:                              // 开始一次跳
        vy = -speed; g = 1; timer = rand() * 255 // 0–8.5 s 随机待机
    if timer > dt:
        timer -= dt
        return
    setAnimation(IDLE)
    timer = 0
    vy += g * dt
    pos.y += vy * dt
    if supporter:
        if landOnObject(pos, supporter): timer = -1; setAnimation(IDLE)
    else:
        if landedOnGround(pos, ctx):             // 见下
            timer = -1; setAnimation(IDLE)
每帧读: 1 次地面射线（无 supporter 时）或 1 次对 supporter 的落点判定
碰撞: 只是在原地上下；dt=1 时下陷 6 cm、谷底 2 帧、7 帧回地面（连续解是 8 cm/8 帧，
      离散解才是真值，见 §5.5）
中断: 冻结；timer 不推进 → 不会在球走开后立刻补跳
```

`landedOnGround` 的判据（照原作，容易写错方向）：

```text
dir = normalize(pos - prevPos)                 // 本帧的下落方向
probe = pos + dir * r                          // 从上一帧位置打过 pos、再多探一个半径
hit = ground(prevPos, probe)                   // 注意：起点是 prevPos，不是 pos
if hit and hit.y <= pos.y + halfHeight:
    pos.y = hit.y - halfHeight
    return true
return false
```

### 12.7 `SporadicWalk`（motionID 0x0B/0x0C，31 件）

```text
state: { phase = Walk, timer, extraTimer, axis(3), angle, angleStep, angleTarget,
         velocity(3), zone }                   // ~13 个 float
init:
    if zoneAt(spawnPos, 2*r) < 0: cancelled = true; return   // 落在 zone 外就不动
    velocity = (-speed, 0, 0)                  // 原作：初始方向是 -X，不是"朝前"
    extraTimer = 30
    timer = 120 + 570 * rand()                 // 4–23 s
    phase = Walk

Walk:
    setAnimation(MOVING)
    if timer > 0:
        timer -= dt
        if timer < 0:                          // 开始一次随机转
            axis = velocity; angle = 0
            sign = rand() > 0.5 ? +1 : -1
            angleStep = sign / 20              // 0.05 rad/帧 → 90° 用 31 帧
            angleTarget = sign * PI / 2
    else:
        angle += angleStep * dt
        if (angleTarget - angle) * angleStep <= 0:
            timer = 90 + 600 * rand(); angleStep = 0; angle = angleTarget
        velocity = rotateY(axis, angle)
    if attemptForwardStep(ignoreYaw = false): phase = ChooseDirection
    else if SporadicWalk:                      // 只有这个类有停顿
        extraTimer -= dt
        if extraTimer < 0: phase = Pause; extraTimer = 30 + 30 * rand()

Pause:      setAnimation(IDLE); extraTimer -= dt
            if extraTimer < 0: phase = Walk; extraTimer = 120 + 60 * rand()
ChooseDirection:
            axis = velocity
            for a in [45, -45, 90, -90, 135, -135, 180]:      // 度
                velocity = rotateY(axis, a * DEG)
                if zoneAfterStep(5 frames, r) == zone: break
            angle = 0; angleStep = a / 12; angleTarget = a; phase = Turn
Turn:       angle += angleStep * dt
            if (angleTarget - angle) * angleStep <= 0: angleStep = 0; angle = angleTarget; phase = Walk
            velocity = rotateY(axis, angle)
            euler.y = PI + atan2(velocity.x, velocity.z)   // 与 attemptForwardStep 同一式
每帧读: 1 次地面射线 + 1 次 zone 射线（attemptForwardStep 内部），Turn 结束再 1 次候选 zone 射线
碰撞: 0.3 m/s，15 Hz 下每 tick 2 cm
中断: 冻结在 phase 上（Walk 中途冻结就是原地站着，不补 Pause）
```

`attemptForwardStep`（三类共用，务必照抄顺序）：

```text
// 1. 贴地
up = pos + (0, 7*r, 0)
if ground(pos, up) -> hit:
    pos.y = hit.y + |halfHeight| * hit.normal.y
    newVel = alignTo(velocity, hit.normal)
    if adjustPitch: baseMatrix = alignTo(normal)
else:
    newVel = velocity
// 2. 前进
prevPos = pos
pos += newVel * dt
// 3. zone 校验
z = zoneAt(pos, 5*r)
valid = (z == zone)
if not valid and z < 0:                        // 穿地了，找回交点
    valid = ground(prevPos, pos + dir*halfDepth + (0, halfHeight, 0))
if valid: pos = hit.contact - (0, halfHeight, 0)
else:     pos = prevPos; return true           // 越界 = true，调用方转 ChooseDirection
if not ignoreYaw: euler.y = PI + atan2(newVel.x, newVel.z)   // 与 Turn 分支同一个公式，见 §15.1
return false
```

### 12.8 `Clouds`（motionID 0x23，5 件）

与 `SporadicWalk` 同一个状态机，三处不同：`ignoreYaw = true`（云不转头）、`timer = 90 + 600*rand`（3–23 s）、**没有 Pause**，也没有 `extraTimer`。速度来自 `speedTable`：本关 5 件云的 `speedIndex = 6` ⇒ 10 cm/帧 = **3.03 m/s**，与前作实测的 3.03 m/s 完全吻合（这也是 cm/帧 → m/s 换算因子的原始出处之一）。

### 12.9 `PathCollision`（motionID <0 走 backup，7 件）与 `SlopingPath`（0x27，2 件）

```text
state: { pathIndex = -1, target(3), velocity(3), euler(3), eulerStep(3), eulerTarget(3),
         angle, angleStep, angleTarget, reference, reversed }     // ~20 个 float（实际只用 yaw + 俯仰各一维）
init (pathIndex < 0):
    pathIndex = nearestPointIndex(pos, path)        // 见下（不是 0！）
    pos = point[pathIndex] - (0, b, 0)
    pathIndex += 1
    target = point[pathIndex] - (0, b, 0)
    euler.y = PI + atan2(target - pos)
    velocity = normalize(target - pos) * speed
update:
    applyEulerStep(euler, eulerStep, dt)            // 偏航缓动
    if dist(target, pos) <= speed * dt:
        pos = target
        pathIndex = (pathIndex + 1) % pointCount
        target = point[pathIndex] - (0, b, 0)
        d = dist(target, pos)
        euler.y = atan2(modelForward.x, modelForward.z)     // 用当前前向重算，不是累加
        eulerTarget.y = PI + atan2(velocity.x, velocity.z)
        eulerStep.y = angleDist(euler.y, eulerTarget.y) / (d / speed)
        if adjustPitch:                                      // 转向 + 俯仰
            framesUntilPitch = 0.25 * d / speed
            angleTarget = (PathCollision ? fromCollisionNormal : fromPathSlope)
            angleStep = angleTarget / framesUntilPitch
            angle = 0
        velocity = normalize(velocity) * speed               // 注意：方向保持，只归一化
    pos += velocity * dt
每帧读: 到达拐点那帧读 1 次地面（PathCollision 取法线定俯仰）；其余帧不读
碰撞: 0.18–0.37 m/s，可预测，15 Hz 够；拐点转向用 d/speed 帧完成，不会瞬转
中断: 冻结在 pathIndex 上；恢复后继续朝同一 target 走
```

`nearestPointIndex`：线性扫全表找最近点，并返回"次近点中较远的那个索引"（原作如此，目的是避免从 0 号点重新起步）。折线 137/134 点，9 件道具（7+2）一扫而过，成本可接受；若要省，可以在烘焙期把每件件的启动索引写进 JSON（本文档的 `path_table.json` 已给出点表，索引可由加载期一次算出）。

### 12.10 `BackAndForthNoYaw`（0x1D，1 件）

```text
state: { phase = Move, pathIndex = 1, target(3), velocity(3), reversed = false }   // 9 个 float
init: target = point[1] - (0, b, 0); velocity = normalize(target - pos) * speed
Move:
    if dist(target, pos) <= speed * dt:
        pos = target
        if not advancePoint():                  // 到头就返回 false
            setAnimation(IDLE); phase = TargetNext; return
        velocity = normalize(target - pos) * speed
    pos += velocity * dt
TargetNext:
    reversed = !reversed
    if not advancePoint(): phase = TargetNext   // 端点处反复翻向，直到能前进
    phase = Move
每帧读: 无
碰撞: 0.18 m/s；这条路径只有 2 个点，观感是"来回平移"
中断: 冻结在 pathIndex/reversed 上
```

`advancePoint`：`reversed==false` 就 `next = pathIndex+1`，越界返回 false（0x1D 不循环）；`reversed==true` 就 `next = pathIndex-1`，`<0` 返回 false。两个 false 一交替，就成了在端点来回。

### 12.11 `WaitForPlayer`（motionID 0x00/0x04，15 件）

```text
state: { useAlt = false }                   // 1 个 bit
update(state, dt, ctx):
    useAlt = conditionMet()
    if useAlt: run(altAction)               // 0x00 → 0x05，0x04 → 0x08（两个都落在
                                            // “未实现 ⇒ 冻结”分支，观感上等于不动）
conditionMet:
    sizeIndex = sizeGroup
    closeEnough = dist(observer, pos) < panicRadii[sizeIndex]   // 200/300/500/700/900 cm
    smallEnough = sizeIndex <= 2
    inZone      = observerZone >= 0 and observerZone == self.zone
    switch motionID:
        0x00: return closeEnough and smallEnough      // 只有这 7 件有"走近触发"
        0x04: return inZone and parentRolled          // parentRolled 在原作里写死 false
每帧读: 1 次距离（外加 zone 比较，zone 是每帧缓存值）
碰撞: 无位移（只在触发后交给 alt 动作）
中断: 冻结；useAlt 保持最后值
```

**只在 7 件上生效**（`moveType 7`/`motionID 0x00`，全是 `size=2` → 200 cm）；另外 8 件（`moveType 10`/`motionID 0x04`）的条件是 `inZone && parentRolled`，而参考实现把 `parentRolled` 硬编码成 `false` —— 也就是说这 8 件在观景器里**永不触发**，原作的真实条件未知（§8 已记）。

### 12.12 `DraggedAlong`（motionID 0x25，15 件）

```text
state: {}
init:  原作不给 DraggedAlong 初始化 zone（zone 初值留 -1；只有 WaitForPlayer/RandomWalk/
       SporadicWalk/Clouds 在初始化时取 zone）
update: 什么都不做（原作的 SetZone 分支直接落到最后的 `return true`，不写变换）
位置:   完全由父件变换给出（spawn 相对父件的局部位姿 × 父件当前矩阵）
中断:   父件被吸附/坠落 → 本件随之（这是唯一"跟着走"的类，其余类是挂起）
```

### 12.13 `BackAndForth`（0x1C）与 `SimplePath`（0x24）

Cancer 里 0 件，为完整性记一行：`0x1C` 走 `ZonePath`（带偏航转向，转向角速度 `-TAU/30`），`0x1D` 走 `noRotationPath`。两者共用 12.10 的 `advancePoint`，区别只在于是否转 `euler.y`。

### 12.14 装配顺序（每帧）

```text
for obj in activeSet(radius = wake_radius):
    if obj.suspended: continue
    state = obj.motionState or init(obj)
    fn = TABLE[obj.motionClass]                 // 13 类直接查表，别 switch motionID
    fn(state, dt, ctx)
    writeTransform(obj.spawn, state)            // 位置/欧拉 → 碰撞体
```

没有 `motionClass` 的对象（`static`，396 件）不进这个循环。原作的兜底是 `return false` → 该件**冻结**（不是"静止在出生点"，是"这一帧不更新"），我们的 `static` 语义与此一致。

---

## 13. 实现前必须实测的清单

§5.7 的幅度换算已经被 5 件道具交叉验证（见 12.3–12.5 的注），所以"幅度"不再是未知项。剩下真正卡实现的是下面 7 条：前 4 条不测就没法写对，后 3 条不测会让"像不像"打折。**没测**，只列方法。

| # | 项 | 为什么卡实现 | 怎么测 | 几次 | 判据 | 现在能测吗 |
|---|---|---|---|---|---|---|
| M1 | `WaitForPlayer` 的触发门（200/300 cm）与触发后的行为 | 两种 `motionID` 的条件不同，`0x04` 的条件在原作里被写死 false；触发后跑的两个 alt 动作在观景器里是空实现 | 实机：走向 7 件 `moveType 7` 的道具，记录"开始动的距离"和动起来后的表现（是否转向/位移/播动画） | ≥3 件 × 2 个方向 = 6 次 | 触发距离落在 200±20 cm；能写下触发后 3 个可观察量 | 否（需实机） |
| M2 | `0x04` 那 8 件的真实触发条件 | 观景器写死 `parentRolled=false`，所以这 8 件永不触发，原作不知道 | 实机：把球滚到这些件旁边（全是 `size=2` 的小蟹/蜘蛛），看是否动、何时动 | ≥2 件 × 2 次 | 能判定"与父件有关/与 zone 有关/恒定触发"三者之一 | 否（需实机） |
| M3 | `Hop` 的下陷深度 | ~~理论 8 cm~~ 已解释：离散解（dt=1 半隐式）谷底 6 cm、2 帧，7 帧回地面；实测 6.60/6.05 正落其上（§5.5） | 原预期 8 cm 是连续解，用错了基准 | —（已结案，只剩跳的间隔分布，见 §15 H1） | 下陷 6.0±0.7 cm | 已结案 |
| M4 | `Spin`/`Flip` 的角速度（0.05 rad/帧 = 4.19 s/圈） | 只验证了"位置不变"，速率从未测过 | 观景器：单件特写 + 16.667 ms 时钟，标记一个不对称特征点，数 1 圈用几帧 | 2 件 × 1 次 10 s | 圈周期 126±6 帧（4.19 s ±5%）；两件方向相反 | 是 |
| M5 | `animated=true` 的动画 clip 帧率与 IDLE/MOVING 切换点 | 31 件 SporadicWalk 走起来"像不像"全在这一条；观景器播的是烘焙好的 clip，我们没有 | 观景器：单只蟹走 5 s，逐帧截图数步频；再对照 state（Walk/Pause）看切换 | 3 件 × 1 次 5 s | 步频与位移速度匹配（不滑步）误差 ≤10%；Pause 时确实切 IDLE | 是 |
| M6 | 被球吸附那一帧的语义 | 决定"道具消失的瞬间是否保留残余速度/姿态"，也决定挂起恢复 | 实机：球滚向 1 件 SporadicWalk 与 1 件 Sway，录 60 fps | 2 件 × 2 次 | 接触→消失 ≤1 帧；消失前无"被推开"位移 | 否（需实机） |
| M7 | 路径类在拐点的转向缓动 | 按 `d/speed` 帧实现了，但没验证过；7 件 PathCollision 的拐点是否看得出"补间" | 观景器：盯 1 件 `moveType 20`（`Dummy Hit 01` 不可见，改用 `moveType 19` 的 2 件）在拐点的朝向变化，逐帧量 | 2 件 × 2 个拐点 | 转向在 `d/speed` 帧内完成，误差 ≤20%；无瞬转 | 是 |

配套的两条低成本确认（不卡实现，但补上就能把 §5.7 的换算升级成"实测结论"）：

- **M8**：挑 1 件"极端长宽比"的道具（旗杆已验 `1.5×`，再补 1 件扁平的，例如荷叶/圆桌）做 Sway 或 WhackAMole，确认 `partBBox.max[1] = modelBboxM[1]/2` 在非直立件上仍成立。判据：幅度系数偏差 ≤10%。
- **M9**：zone 语义只能做几何验证，不能靠看行为——触发后跑的 alt 动作是空实现，观感上只是“冻结”。做法：把 `zones.json` 的 7 块 zone 网格与我们自己的 zone 判定（向上射线取三角形第 4 个浮点）逐点对照。判据：20 个随机点的 zone 号两边完全一致；不一致时以三角形数据为准。

优先级：**S1 → M4 → W1 → S2 → M5 → M7**（都能在观景器里做，且直接决定第一版的手感），M1/M2/M6 等实机窗口。M3 已结案（§5.5 的离散解）。

要强调的一句：这些是"把参考实现里的常量变成实测值"，不是"再研究一遍原作"。参考实现给的常量本身可能已经是近似（`panicRadii` 的注释就写着"每个 size 其实有四个值，这里做了映射"），所以 M1 的结论有可能是"200/300 是错的"，那时以实测为准并记 parity 行。

---

## 14. 触发半径：原作的 2–3 m 与我们的 12 m 不是同一个概念

这一节回答"我们的 `wake_radius_m = 12` 是不是标错了"。结论先说：**概念不同，不能直接对标；12 不该改，但也不能拿它当触发门用。**

### 14.1 两个量各自是什么

| | 原作 `panicRadii` | 我们 `wake_radius_m = 12` |
|---|---|---|
| 出处 | 参考实现常量表（原作代码里的常量，`[推断]` 每 size 四档被简化成 5 档） | `Docs/Design/PickupAbsorb/PROP_BEHAVIOURS.md` §7.2/§7.3 |
| 语义 | `WaitForPlayer` 的**行为触发门**：距离小于它就从"站着"切到"跑开" | **距离剔除半径**：`dist ≤ 12` 进活跃集，`dist > 15`（滞回 3 m）出集并冻结、关 Animator |
| 谁受影响 | 只有 `motionID 0x00` 的 7 件 `WaitForPlayer` | 全部 `path`/`orbit`/`ambient` 运动件 |
| 触发后 | 换成 alt 动作（0x05/0x08，观景器未实现） | 只是"更新不更新"，行为本身不变 |
| 量级依据 | 尺寸档 200–900 cm | host 激活半径 1.6 m，预留"球到达前动画已在动" |

一句话：原作那个数**决定"它怕不怕你"**，我们那个数**决定"它算不算在动"**。

### 14.2 12 是怎么来的

`PROP_BEHAVIOURS.md` §7.2 的理由写得很直白：`wake_radius_m` 12 m "比 host 的激活半径 1.6 m 大得多：动画要在球到达之前就动着，否则玩家会看到'走近才启动'"。§7.3 补了滞回 3 m 与"出圈冻结但记相位、不重置"。

同一份文档里还引了一句「原作有同类做法：PS2 版把视野外被撞倒的移动件暂停（`[社区]`）」。**这一条没能证实**：实测到的"跳过"是参考实现的渲染优化（`if (!this.visible) return`），不是游戏逻辑；原作的 128 件运动件在 30 Hz 里是一直在更新的（§5.5 的 Hop 矛盾就是这条的直接证据）。所以 12 m 这个数的依据是**我们的观感需求**，不是对原作的模仿。

### 14.3 差 4–6 倍会带来什么可观察差异

1. **触发过早（最明显）**：如果把 12 m 当触发门，球还在 12 m 外晃，院子里的 7 只小蟹就已经切到"逃跑"状态。原作要 2 m 才反应 —— 玩家感受到的是"这些蟹一点也不怕人"。
2. **覆盖面积 16–36 倍**：12 m 圆 ≈ 452 m²，2 m ≈ 12.6 m²，3 m ≈ 28 m²。反过来，如果为了对齐原作把**更新**半径压到 2–3 m，那球在 12 m 外时 128 件几乎全部冻结 → 远处的螃蟹静止，与"院子里的蟹一直在爬"直接冲突，而且比原作更假。
3. **相位连续性**：我们的规则是"冻结但记相位"，所以走远再回来不会跳变。这一条已经比"重置相位"好；但 12 m 处看到的是"静止的一群蟹"，原作是"一直在动"。这个差异只在玩家站着不动、往远处看时出现，成本上是可接受的取舍。
4. **预算其实不吃紧**：按 §12.0 的碰撞需求取频率，128 件全量更新也只要 1 810 次/秒 = 14.6% 预算（§6.2 的 12 420/s）。所以 12 m 的真正作用不是省 Udon 预算，而是**省 Animator 数量**（§7.1 的 DC/组件成本），这一点我们的文档里已经写对了。

### 14.4 结论与待拍板

- **不改** `wake_radius_m = 12`（管更新与动画，理由成立）。
- **不要把 12 当触发门**：`WaitForPlayer` 的触发门应单独实现，取 200/300 cm（`sizeGroup` 0–1），与 `wake_radius` 完全解耦。这样"远处一直在动"和"近了才怕人"两件事同时成立，也正好对上原作的组合。
- 需要 lead 拍板的只有一件事：**是给 `WaitForPlayer` 单独加一个 `trigger_radius_m`（默认 2.0/3.0，按 sizeGroup），还是先让它永远走 alt（即不实现触发）**。前者更接近原作，代价是多一个参数与一次距离判定；后者零成本但 15 件全部变成"站着不动的蟹"。
- 这条分歧应记进 parity 表（`Docs/Design/Parity/PARITY_AUDIT.md` 的 Divergences）：原作的触发门 200/300 cm vs 我们的"无触发门"；决策待定，owner = lead。**不动我们的值**。

---

---

## 15. 实现与伪码的差异 + 仍是 PROVISIONAL 的常量

运动库（`Runtime/Pickup/MotionMath.cs`）报了四处与 §12 不一致。逐条回参考实现读代码，结论是 **3 处实现是对的（我伪码写错或实现更精确），1 处必须改**。

### 15.1 四处差异的逐条裁定

#### ① SporadicWalk 的 yaw：**必须改回 `π + atan2`**

伪码里两处不一致，全是我抄写错误。参考实现里只有**一个**式子，出现两次：

```
object.euler[1] = MathConstants.TAU / 2 + Math.atan2(<速度>.x, <速度>.z)
```

- `motion_attemptForwardStep` 末行（`objects.ts:2144`）：`forwardScratch` 是**贴地后的速度**（`transformVec3Mat4w0(newVel, alignScratch, motion.velocity)`），在写完 `pos` 之后用来定朝向 ⇒ 这是**前进**用的那次。
- `motion_RandomWalk_update` 的 `Turn` 分支（`objects.ts:2233`）：`motion.velocity` 经 `rotateY(axis, angle)` 之后的当前速度 ⇒ 这是**转向**用的那次。

`TAU/2` 是 **π**，不是 π/2；两处喂进去的都是"速度方向"，不是位移，也不存在两个不同的角。所以只有一式：

```
yaw = π + atan2(vx, vz)        // 前进与转向共用
```

伪码 `attemptForwardStep` 那行的 `PI/2` 是错的，请改成 `π`（伪码已同步修正）。实现者的"两者不能都对"判断正确，但结论应落在 π 而不是 π/2。

**与它绑定的朝向约定**：`v = (0,0,−1)` 时该式给 `atan2(0,−1) + π = 2π ≡ 0`，即**模型的 0 偏航必须面向 −Z**。参考实现的模型按这个约定导入；我们的 prefab 若沿用 Unity 常见的 +Z 朝前，会整体差 180°。

**选错的观感**：

| 错法 | 表现 | 这关里最先露馅的地方 |
|---|---|---|
| 用 `π/2`（差 90°） | 所有用 `euler.y` 定朝向的走类道具**横着走**，朝向与行进方向垂直 | 31 件 SporadicWalk 全是蟹，横着走反而"看着正常"；破绽在 `SlopingPath` 的**书立会侧着滑**、招潮蟹的**大螯指向行进方向**而不是垂直 |
| 差 180°（模型朝 +Z） | **倒着走**：螯拖在后、书立正面朝后 | 一眼可见，尤其书立与云（云不写 yaw，不受影响） |

**一个不要复刻的 quirk**：`motion_SlopingPath_Follow`（`objects.ts:1637`）里 `object.euler[1] = Math.atan2(axisZ.x, axisZ.z)` 读的是"当前 modelMatrix 的 +Z 轴"，少了 π，与紧接其后的 `eulerTarget[1] = π + atan2(v)` 不同系。它只当插值起点用（`angleDist` 会绕回），效果是拐点处多绕 180°。我们统一 `π + atan2` 即可，不要照抄。

#### ② Hop 闭式：**等价，确认**（附 dt≠1 的坑）

原作顺序是"先加 g，再位移"，逐帧递推为

```
vy_k = −v + g·d·k
y_n  = −v·d·n + g·d²·n(n+1)/2
```

`d = 1` 时 `y_n = −v·n + g·n(n+1)/2`，与实现的 `−vt + g·t(t+1)/2` 完全一致；`v=4, g=1` 得 −3, −5, −6, −6, −5, −3, 0。**等价成立**。

顺手一个副产品：这串数说明谷底是 **6 cm / 2 帧 / 7 帧回地面**，不是连续解的 8 cm——而实测 6.60 / 6.05 cm 正落在 6 上（§5.5）。所以实现者打印的这串序列本身就是「离散解 = 真值」的证据，也把 §13 的 M3 从必测降成已结案。

但闭式**只在 d = 1 时**等于逐帧累加：`d = 2` 时原作自己的算术是 `vy += 2g; y += vy·2`，而两次 dt=1 迭代是 `(−v+g)` 再 `(−v+2g)`，两者差 g。所以：按 30 Hz 固定 dt=1 调用时闭式精确；若上层合并帧（66 ms 一次），必须换成 `y = −v·d·n + g·d²·n(n+1)/2` 或退回递推。**建议在库入口断言 `dt == 1`**。

另：闭式把"求 t 时刻姿态"变成 O(1)，但 Hop 的**落地检测不能跳帧**——下陷只有 8 帧，落地要么每帧判，要么解析求 `y(t)=0` 那一帧；落地后 `timer=-1`，闭式 t 归零。

#### ③ WhackAMole 首段上升：**实现是对的，我伪码错了**

`MotionState` 初值是 `state: -1`（`objects.ts:259` 一带），`motion_MiscWhackAMole_Update` 第一句 `state = (state+1) % 2` ⇒ 首段 `0` = **上升**。我伪码写"初值 0、首段下沉、第一帧瞬移"，把初值记错了又据此编了段注释，已改。这也与 §5.4 实测（0.7 s 升 12.4 cm 停在顶）和 v2 的 `loop:false` 一致。常量未动，确认。

#### ④ PathCollision 的 `adjustPitch`：**分工成立**

`adjustPitch = !def.stayLevel`（`objects.ts:240`），本关道具 `stayLevel` 基本为 false ⇒ 这个开关几乎全开。原作在 `motion_SlopingPath_Update` 末尾调 `pathCollisionAdjustBasePitch` / `slopingPathAdjustBasePitch`，把**俯仰**写进 `baseMatrix`：`PathCollision` 从地形法线取目标角、`SlopingPath` 从路径坡度取。两者都是宿主查询（射线/法线），库不该碰 ⇒ **库只出 yaw、宿主写 pitch** 正确。

一个记账用的细节（P1，不阻塞）：原作在到达拐点那帧用 `getMatrixAxisZ(modelMatrix)` 取"当前前向"再算 yaw 起点——**俯仰参与了 yaw 的计算**。若宿主把 pitch 写在另一层、库里拿不到，拐点处 yaw 起点会与参考实现差一点（同帧内被 `angleDist` 修正，观感无差，但不是逐位一致）。

### 15.2 PROVISIONAL 账本

这一节就是"还原度还差多少"的账本：每条实测完，把状态从 PROVISIONAL 改成实测，并填证据路径。

| # | 项 | 现状 | 为什么定不了 | 用什么测 | 优先级 |
|---|---|---|---|---|---|
| S1 | yaw 公式 + 模型朝向约定 | 公式已定 `π+atan2(vx,vz)`；模型 0 偏航须面向 −Z | 公式有代码依据，但**我们 prefab 的朝向**没验证过（差 180° = 倒着走） | 观景器：1 件 SporadicWalk（蟹）+ 1 件 SlopingPath（书立），比"行进方向 vs 模型长轴/正面" | **P0**（15 分钟，观景器） |
| M1 | `WaitForPlayer` 触发门与触发后行为 | 门 200/300 cm（仅 7 件）；触发后空实现 | alt 动作 `0x05`/`0x08` 未实现，观景器里触发 = 冻结 | 实机 3 件 × 2 方向 | **P0**（需实机） |
| M2 | `moveType 10` 那 8 件的真实条件 | `inZone && parentRolled`，`parentRolled` 写死 false ⇒ 永不触发 | 参考实现没实现，原作条件未知 | 实机 2 件 × 2 次 | **P0**（需实机） |
| M3 | Hop 下陷深度/单次帧数 | **已结案**：离散解 6 cm / 2 帧谷底 / 7 帧回地，实测 6.60 / 6.05 吻合 | —（原预期 8 cm 是连续解，基准用错） | 只剩跳的间隔分布（见 H1） | — |
| M4 | Spin/Flip 角速度 | `0.05 rad/帧` = 126 帧/圈 | 只验了"位置不变" | 观景器 dt=1，数 1 圈帧数 | P1 |
| M5 | 动画 clip 速率 + IDLE/MOVING 切换 | 数据有 clip 索引，无 fps | 动画表未解 | 观景器 3 件 × 5 s 数步频 | P1 |
| M6 | 被吸附那一帧的语义 | 冻结、不保留速度（推断） | 观景器无吸附 | 实机 2 件 × 2 次，60 fps | P1（需实机） |
| W1 | WhackAMole 驻留与端点 | 2–7 s 均匀、停在端点（首段上升已定） | 只在一件上见过 7 s 不动 | 观景器 2 件 × 30 s，记"上升/停/下沉/停"四段时长 | P1 |
| S2 | SporadicWalk 四阶段时序 | 走 4–23 s、停 1–2 s、随机转 90° 用 31 帧、回正 12 帧 | 四处随机区间叠加，样本少时分不清 | 观景器 3 件 × 60 s，统计 Walk/Pause 时长分布 | P1 |
| A1 | 拐点 yaw 起点受 pitch 影响 | 库只出 yaw（见 15.1④） | 宿主/库分工的必然结果 | 观景器 1 件 PathCollision 拐点朝向 | P1 |
| M7 | 路径拐点转向缓动 = `d/speed` 帧 | 按公式实现，未验证 | 观景器拐点小、帧率低 | 观景器 2 件 × 2 拐点 | P2 |
| M8 | 非直立件的 `halfHeight` | 5 件都符合 `modelBboxM[1]/2` | 样本全是直立/矮胖件 | 观景器 1 件扁件（圆桌/荷叶） | P2 |
| M9 | zone 语义 | 向上射线取三角形第 4 浮点 | 行为层不可观测（alt 空实现） | 几何对照 20 个点 vs `zones.json` | P2 |
| C1 | Clouds 转向区间 | 3–23 s、无 Pause | 只有 5 件、间隔长 | 观景器 2 件 × 60 s | P2 |
| H1 | Hop 待机分布 | `255*rand` 帧 = 0–8.5 s 均匀 | 样本少 | 观景器统计 20 次间隔 | P2 |

**不在账本里的**（已在正文锁死，来源明确）：运动表分配与 `pathIndex`/`backupIndex`（数据）、路径点与路径速度（数据）、`speedTable`/`sizeGrouping`/`hopSpeeds`（常量表）、幅度系数与 `halfHeight` 关系（5 件实测，§5.7）、周期常量（3.0 s / 0.75 s / 126 帧，§5.7）、WhackAMole 首段方向与 Hop 闭式（本节裁定）。

建议顺序：**S1（15 分钟）→ M4 → W1 → S2 → M5 → M7 → M8 → C1 → H1**，M1/M2/M6 等实机窗口，M9 随手。

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
| 重新生成两份 JSON | `python3 ~/KatamariRef/tools/katamari_stage_extract/decode_motion_tables.py --level 11 --props-doc <repo>/Docs/Design/Levels/_ref_cancer/placement.v2.json`（两次运行字节一致） |
| 校验产物 | `sha256sum` 两份 JSON；条目数见 `counts`（524 / 129 / 15 / 4） |
| 复现周期测量 | `~/KatamariRef/tools/noclip_capture/pw_capture.mjs`（虚拟时钟，`EVERY=23`、`STEP=66.6667`）→ `_tracks.json` → 峰峰值/峰间隔 |
| 复现像素变化 | `pngdiff.py --thresh --min-area --json` |
| 原始帧 | `~/KatamariRef/viewer_evidence/noclip_frames/cancer_crab_fine_f000..f029.png` |

参考实现（只读，未进仓库）：noclip.website `src/KatamariDamacy/{bin.ts,objects.ts,scenes.ts}`；`objects.ts` 里的 `motion_*` 函数是本文【常量】的唯一出处。
