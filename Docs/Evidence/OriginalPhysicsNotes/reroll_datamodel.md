# REROLL 数据模型（只从公开源码/公开数据表推出）

> 目的：判断"把原版一关的真实数据跑进我们这版"在技术上缺什么。
> 只读公开 GitHub 源码 + 公开数据表；**没有下载游戏、没有反编译游戏 DLL、没有读盗版文件**。
> 标注：`源码可见` = 名字/签名直接出现在公开源码里；`推测` = 由用法反推。
> 链接都带行号，指向公开仓库。

## 0. 引擎与运行时

| 事实 | 证据 |
|---|---|
| **Unity 2018.1.x**（net35 / Mono，非 IL2CPP） | KatamariMods 用 `UnityEngine.Modules` **2018.1.9** 引用程序集、`TargetFramework net35`（[Directory.Packages.props#L17](https://github.com/The0x539/KatamariMods/blob/trunk/Directory.Packages.props#L17)、[Directory.Build.props#L9](https://github.com/The0x539/KatamariMods/blob/trunk/Directory.Build.props#L9)） |
| 托管程序集在 `katamari_Data/Managed/Assembly-CSharp.dll`（还有 `-firstpass`） | [Directory.Packages.props#L31-L40](https://github.com/The0x539/KatamariMods/blob/trunk/Directory.Packages.props#L31) |
| **物理/吸附在原生 DLL `PS2KatamariSimulation.dll`** | ASL 直接按地址读它的内存（[reroll-rta-mod.asl#L3-L5](https://github.com/misoelegant/rpm/blob/master/rta-mod/reroll-rta-mod.asl#L3)）；`PS2KatamariSimulation` 是它的托管外壳（[FramerateUncap/Plugin.cs#L432](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L432)） |
| 游戏逻辑 **固定 1/30 s 步长**，画面帧另算 | IL 里 `if (deltaFrame >= 1/30) { deltaFrame -= 1/30; mSimulation.DoTick(1/30f); }`，补丁注释逐条给出原指令位置（[Plugin.cs#L62-L95](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L62)）；RerollPatcher README 同述"the underlying game logic still runs at 30 FPS"（[README.md](https://github.com/squiddingme/RerollPatcher/blob/master/README.md)） |

## 1. 公开源码里出现的原版类与字段

### 1.1 `GameManager`（关卡运行时总控）

| 成员 | 类型/签名 | 含义（我方解读） | 证据 |
|---|---|---|---|
| `deltaFrame` | float | 帧累加器，满 1/30 扣一次 | [Plugin.cs#L51](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L51) |
| `mSimulation` | `PS2KatamariSimulation` | 原生模拟实例，`mSimulation.DoTick(1/30f)` | [Plugin.cs#L52](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L52)、#L83-L87 |
| `s32Time` / `s32VsTime` / `s32VSTime` | int | 关卡计时（30 Hz tick 计数；两个 VS 变体是历史遗留） | [Plugin.cs#L108-L110](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L108) |
| `f32Scale` | float | 球的**显示缩放**（不是直径） | [Plugin.cs#L114](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L114) |
| `f32KataAlpha` / `f32VSScale` | float | 球体透明度 / 双人缩放 | #L115、#L118 |
| `gWork` | `GlobalWork` | 全局工作区（相机、教程、漩涡位置等） | #L112 |
| `separateTimer` / `s32SepaTime` | — | 分离（本体/球分离）计时 | [Plugin.cs#L119](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L119)、#L259 |
| `gYm_Game2dRequestWhirlpool(int time, float scaleS, float scaleE, float angleS, float angleE)` | 方法 | **换区/换档特效的入口** | [Plugin.cs#L362](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L362) |
| `sCtrl_Whirlpool()` | 方法 | 每帧推进该特效 | #L388 |
| `whirlTime / whirlScale / whirlAngle / whirlScaleDelta / whirlAngleDelta` | int/float | 特效状态机；`time` 单位是 **30 Hz 帧**（原版把计时×2 因为特效按 60 跑） | #L368-L379 |

`GameManager` 里**看不到**"目标直径/时限/区域阈值"这些配置字段——它们是运行时从数据表载入 `gWork` 或 sim 内部的（`源码不可见`，`[推测]`）。

### 1.2 `PS2KatamariSimulation`（原生模拟外壳）

| 成员 | 签名 | 含义 | 证据 |
|---|---|---|---|
| `DoTick` | `void DoTick(float delta)` | 一步模拟；mod 把 delta 夹到 ≤0.1 s | [Plugin.cs#L432](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L432) |
| `GetPropMatrixFromSimulation` | `Matrix4x4 GetPropMatrixFromSimulation(int monoControlIndex)` | **按物件控制索引取世界矩阵**（含平移+旋转） | [PropInterpolationPatch.cs#L73-L91](https://github.com/squiddingme/RerollPatcher/blob/master/Patches/PropInterpolationPatch.cs#L73) |
| （原生内存）`levelID` | byte @ dll+0xff104 | 当前关卡号 | [asl#L3](https://github.com/misoelegant/rpm/blob/master/rta-mod/reroll-rta-mod.asl#L3) |
| （原生内存）`size` | float @ dll+0x16d77c | **球径，单位 cm**（`levelID==10 && size>=30000` 即月亮关 300 m） | [asl#L5](https://github.com/misoelegant/rpm/blob/master/rta-mod/reroll-rta-mod.asl#L5)、#L100 |

### 1.3 `AttachableProp`（每个物件实例的托管表现）

| 字段/方法 | 含义 | 证据 |
|---|---|---|
| `monoControlIndex` (int) | 物件实例索引，**与原生 sim 的唯一句柄**，也是数据表里的列名 | [PropInterpolationPatch.cs#L117-L128](https://github.com/squiddingme/RerollPatcher/blob/master/Patches/PropInterpolationPatch.cs#L117) |
| `isMultiMesh` (bool) | 多网格物件 | #L36 |
| `monoRotation` (Quaternion[]) | **两套旋转**：`[0]` 未粘、`[1]` 已粘到球上 | #L38-L39 |
| `mIsAttachedToKatamari` (bool) | 是否已被吸附 | #L39 |
| `checkSimpleWait` / `checkSimpleWait2` / `s16EscapeTimer` (int) | 30 Hz 计数器（原版 `-1`/帧），证明物件行为也按 30 Hz 计 | [FramerateUncap/Plugin.cs#L314-L331](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L314) |
| `UpdateMono()` | 每 tick 用矩阵刷新 transform | #L314 |

### 1.4 其他

- `Player.TriggerClear()` / `Player.DoPS2ControllerSimulation()`：输入→控制器模拟（[Plugin.cs#L343-L345](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L343)）。
- `GlobalManager.collectRot`：收藏界面旋转（#L302）。
- `SI_TUTORIAL.s32Timer`、`GlobalWork.siTutorial`：教程计时（#L111-L113）。
- `PauseMenu.PauseProc()`：暂停流程（#L345）。
- `EarchRotator`（地球选择界面）：`____speed` 等私有字段（[MissionSelectPatch.cs#L20](https://github.com/squiddingme/RerollPatcher/blob/master/Patches/MissionSelectPatch.cs#L20)）——**地球界面**，与关卡内无关。

### 1.5 关卡/物件数据（不是 C# 字段，是数据表列名）

公开 dump 的表头本身就是游戏数据结构的列名：`START_DIAMETER / CLEAR_DIAMETER / MAX_DIAMETER / GAME_TIME / AREA_NUM / AREA_CHANGE1-4 / NEXT_AREA1-4 / METEOR`（关卡表）、`monoControlIndex / f32Volume / s32KataCatchDiameter / position / parentProp.monoControlIndex / monoCategory / u8UnitType / u8HitLod`（实例表）、`Req Pickup Size / Volume / Pickup Volume Penalty`（类型表）。这些是 `[数据挖掘]`，字段名与 C# 侧 `monoControlIndex` 对得上。

## 2. 数据在哪、怎么读写、"列一关所有物件"怎么做

| 层 | 内容 | 读法 |
|---|---|---|
| **原生 `PS2KatamariSimulation.dll`** | 球径、关卡号、每物件矩阵、吸附判定 | 只能**内存读**（ASL/指针）；无公开 API |
| **托管 `Assembly-CSharp.dll`（Mono）** | `GameManager`、`AttachableProp`、`PS2KatamariSimulation` 外壳 | BepInEx + Harmony 打补丁（`AccessTools`/`Member.Field<>`） |
| **数据表**（关卡、物件类型、实例） | 上面那些列 | 游戏自己载入；社区已把这些表导出成公开 CSV（就是本轮用的 dump） |

**"只读地列出某关所有物件的位置与尺寸"的最短路径**：不用碰游戏——**公开 dump 的实例表已经含 `mission` + `position` + `f32Volume` + `loading area`**（13,723/28,698 有非零坐标）。要运行时读则需要：用户自己的 Steam 安装目录（`katamari_Data/Managed/Assembly-CSharp.dll`）+ 一个 Harmony 插件去枚举 `AttachableProp` 并读 `GetPropMatrixFromSimulation(i)`；**这属于我们这轮的禁区（不改用户机器、不反编译）**，所以只作为"若将来需要"的路线记录。

## 3. 物件实例是否带真实 XYZ？

**带。** `[数据挖掘]` 实例表每行有 `position "(x, y, z)"`：

- 全表 28,698 行中 **13,723 行非零**；MAS1 **652/654** 非零；MAS4 605/1678；Moon 513/3955。
- 为零的行对应"按区域随机生成"的位置组：另有 `randomLocGroupNo / quantity / spawn chance (x/256)` 表（202 行）。
- 同一位置在多关重复出现（例如 `DummyProps` 在 Pisces / Ursa Major 都是 `(-25.97, 0, -33.91)`）——说明资源与摆位跨关复用。

**结论**：位置是**真实的世界坐标、按实例存储**，不是运行时生成；随机只作用于"位置组"这一层。→ 一比一重建可行（位置+尺寸+区域+类别可直接搬）。缺的是**旋转与缩放**（实例表没有这两列）。

## 4. 区域切换与 30 Hz

- **触发**：尺寸阈值 → `gYm_Game2dRequestWhirlpool(time, scaleS, scaleE, angleS, angleE)`（`time` in 30 Hz frames；原版传 30 或 75/76，来自 `s32Timer`）。
  证据：[Plugin.cs#L362-L379](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L362) 及注释 "The only other value the game uses is 30"、"all the things that start the timer operate on a 30 Hz cycle"。
- **它是纯视觉过渡**：一个 2D 精灵 `objWhirlpoolObject`，做"缩放 + 旋转 + 淡出"，位置 `gWork.sWhirlpoolPosi`，**不重载场景**（[#L388-L430](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L388)）。
  → 所以 dump 里那串 "swirl sizes" 是**播放该过渡的尺寸点**，不是"加载新区域"的点；真正的区域内容切换由别的机制（`s32Timer`/关卡脚本）驱动，**具体规则未在公开源码中出现**。
- **30 Hz 定步长**：确认。`deltaFrame >= 1/30` → `-= 1/30` → `DoTick(1/30f)`，外层内层各一次（[#L62-L95](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L62)）。画面由 RerollPatcher 插值到 60+。
- 物件行为（`AttachableProp` 的等待/逃脱计时）同样以 30 为基数（[#L314](https://github.com/The0x539/KatamariMods/blob/trunk/FramerateUncap/Plugin.cs#L314)）。

## 5. 我们缺什么（对照我们的引擎）

| 原版有 | 我们有 | 差距 |
|---|---|---|
| 每物件一个原生矩阵（位置+旋转，30 Hz） | Udon 里每个物件一个 Transform | 结构同构，但我们没有"原生模拟"层，全靠 Udon 脚本 |
| 球有**碰撞网格**（mod 可可视化，F4） | 球 + OBB 接触模型 | 原版碰撞是**网格**不是解析球；我们若要贴近需网格或近似 |
| 物件有**包围盒 + vault 点**（F5 可视化） | 无 vault 点 | 我们完全没有"翻越/弹起"这套点 |
| 固定 1/30 步长 + 插值 | 每帧 Update | 需改成累加器，否则速度随帧率变 |
| 体积→直径、10× 门、per-item penalty | 体积守恒 + 单位量化 | 规则不同（见 reroll_numbers.md §3） |
| 换区/换档是 2D 特效 + 数据驱动 | 无 | 我们连"尺寸阈值"表都没有 |

## 6. 仍然不可见

1. 原生 sim 的**速度/加速度/坡度/攀爬**参数（无公开符号，也无源码）。
2. 关卡配置（目标直径、时限、区域阈值）在 C# 里的字段名——只在数据表层面可见。
3. `swirl` 尺寸与"真正换区"的对应关系。
4. 物件的旋转/缩放存储位置（实例表没有）。
