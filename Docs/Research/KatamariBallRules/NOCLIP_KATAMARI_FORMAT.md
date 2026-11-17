# noclip.website 的 Katamari Damacy 关卡数据：移植规格

**调研日期：2026-11-16 UTC（项目时区 2026-11-16 Asia/Shanghai）。** 本文是给**我们自己的解析器**用的移植规格：
PS2《块魂》关卡/任务包里每个字节是什么、按什么顺序读、哪些字段是我们要的（逐件变换含旋转/缩放、父子挂接、
地形网格、碰撞与边界）、哪些是纯渲染细节可以整段跳过。**不是** noclip 代码的翻译，也**不含**任何 noclip 源码片段。

**证据口径**：沿用仓库现状，每条断言标 **【实测】**（我本机跑出来的字节/数字，或我自写解析器与参考实现逐项对比的结果）、
**【推断】**（合理但未证实）、**【未验证】**（没条件查）。参考实现指 [magcius/noclip.website](https://github.com/magcius/noclip.website)，
本文所有 `xxx.ts:Lnnn` 都是该仓库 `src/KatamariDamacy/` 下的文件行号，**提交 `ee4d72f92c79`**
（clone 在 `~/KatamariRef/tools/noclip.website`，HEAD `2cebac2a5d1d7c44535397a14f51e764ca823767`；
我核对过 `bin.ts` 的 sha256 `8ef962ef…b11a` 与 `ee4d72f92c79` 时一致，因此行号对两者都成立）。
每条的行号可直接拼成 `https://github.com/magcius/noclip.website/blob/ee4d72f92c79/src/KatamariDamacy/bin.ts#L1413`。

**边界**：只读参考实现 + 在自己机器上跑自写解析器；**没有**改动游戏本体、**没有** git 操作、**没有**把任何参考资源或
noclip 代码放进仓库；所有游戏 BIN、导出的 JSON/GLB 都在仓库外的 `~/KatamariRef` 下。

---

## 0. 十二行摘要

1. **原作有完整关卡几何**：舞台包是 8 扇区的 `BIN`，Make Cancer 实测 3 个有效图形扇区、7 个模型、**29,846 顶点 / 44,340 索引 / 232 个材质段**，另含 **68 个碰撞 AABB + 3,456 个碰撞三角形**。这推翻了「数据里没有地形」的旧结论——地形在 PS2 的舞台包里，不在 REROLL 的导出表里。
2. **逐件旋转是真实存在的**：任务包每个实例是 **0x40 字节**，平移之后是**旋转轴（`+0x20`，3 个 float）+ 角度（`+0x2C`，1 个 float，取负用）**；Make Cancer 的 524 个实例里 **401 个（76.5%）角度非零**，358 个超过 45°、301 个超过 90°、最大 240°。我们现在把 511 件全部按 `rotationQuat=[0,0,0,1]` 摆放，等于丢掉四分之三道具的朝向。
3. **每件缩放也在记录里**（`+0x30..+0x38` 三个 float）：518 件是 `(1,1,1)`，5 件是 0.9/0.1 均匀，**1 件非均匀** `(0.074, 0.029, 0.020)`；参考实现读了但**不应用**。语义【推断】，默认值是 1 这一点很强。
4. **父子挂接不在实例记录里**，在同一包内的 `parentBlock` 表：按 `(关卡, 区域)` 取一串 `int16` 对，Make Cancer 区域 2 有 **130 条对**（**90 条真父子**，40 条只有"我是挂接件"标志、父为 `-1`）。我们的 placement 完全没有 `parent` 字段。
5. **坐标系已经量准了**：REROLL 导出表的 `position` **恰好等于** PS2 实例平移的 `−0.01 ×`（524 件里 360 件误差 < 1 µm，78% < 1 mm，97% < 0.5 m）——即两套数据**同一世界坐标系**，一个是厘米、一个把三个轴全取反。这不是"区域局部坐标"。
6. 因为三轴全取反是**点反射**（行列式 −1），把 PS2 网格并进 REROLL 坐标系时要**同时翻转三角形绕序**；而参考实现只用 `rotateX(π)`（行列式 +1），所以它看到的关卡是**左右镜像**的【推断，强】。
7. **加载契约很窄**：`levelBlock.bin` + `missionBlock.bin` 决定该关用哪 4 个任务包分片，4 个分片**拼成一个地址空间**读；舞台则按 `(stageAreaIndex + 活动区序号)` 各取一个模型 BIN 和一个纹理 BIN，文件名就是 ELF 文件表里的 **ISO LBA 十六进制**。
8. Make Cancer 的具体输入：任务包 `17f590/{141ab5,141b43,141bf5,141cae}.bin`（3×947,712 + 947,488 = 3,790,624 B）、舞台 `1879b0/135d18.bin`（1,225,376 B）、纹理 `1879b0/135049.bin`（663,824 B）。
9. 这些文件**不在我们仓库里**，`~/KatamariRef` 原来也没有：按参考实现的抽取规则从 `https://z.noclip.website` 取到了同源 BIN（16 个，6.59 MB，sha256 已记录）。游戏光盘本体仍未验证在谁手上。
10. 按本规格**独立写了 Python 解析器**（981 行，`~/KatamariRef/tools/katamari_stage_extract/`），与参考实现逐项对拍：实例 524/524 完全一致、旋转矩阵最大差 **1.94e-4**、碰撞 68 条 bbox 全等、网格顶点/索引数相同。**这证明本规格足以重实现，不需要抄代码。**
11. **许可**：noclip 仓库是 **MIT + 反编译说明**（不是 GPL-3，概设里的前提有误），GitHub 因那段说明报 `NOASSERTION`。即便如此，"逐行翻译"仍属衍生作品；**我们的解析器只能以"照规格重实现"的形式进仓库**，而仓库许可尚未决定——见 §10。
12. **收益**：把上面 4 项接进重建，我们能从"511 件不旋转、无父子、假平面地板 + 四面假墙"变成"511 件带真实朝向/挂接、29,846 顶点真地形、68 个碰撞体、7 个任务区体积"；本文产出在 `~/KatamariRef/noclip_export/kse_out/`。

---

## 1. 我做了什么验证

| 项目 | 结果 | 证据 |
|---|---|---|
| 参考实现能不能 headless 跑 | 能。`tsx` + Node 24 直接 import 其 TS 模块（需 stub 掉 `noclip-rust-support`） | `~/KatamariRef/noclip_export/export_katamari.ts`（仅本地对拍用，不进仓库） |
| 自写 Python 解析器 vs 参考实现：实例数 | 524 = 524 | `kse_out/report.json` vs `out/instances.json` |
| 同上：实例平移 | 逐件完全相同（max｜Δ｜=0） | 对拍脚本见 §13 |
| 同上：实例旋转（换成 3×3 矩阵比） | max｜Δ元素｜= **1.94e-4**（float32 量级） | 同上 |
| 同上：父表 / linkAction / moveType / 显示区 | 524 件全部一致 | 同上 |
| 同上：碰撞 AABB | 68 条全部一致，顶点值 max｜Δ｜=0 | 同上 |
| 同上：舞台网格 | 29,846 顶点 / 44,340 索引 / 7 个模型 / 232 材质段 全等 | 同上 |
| 任务包分片必须拼起来读 | 单块读会在 `parseLevelParameters` 越界；拼起来后 `numSectors=18,907` 才对 | 【实测】 |
| 关卡表（19 关全部） | **19/19 行完全一致**：4 个任务包名、光照号、起始区、舞台区索引 | `lvls_oracle.json` vs `mission.level_parameters` |

> 参考实现那一侧只是**对拍标尺**（一个用完即弃的脚本，放在仓库外）。交付物是 §9 的规格 + 我们自己的 Python 实现。

---

## 2. 文件布局与加载契约

### 2.1 从光盘到 BIN

| 步骤 | 事实 | 出处 |
|---|---|---|
| 输入 | `KatamariDamacy.iso`，内部 ELF 名为 `SLUS_210.08;1` | `tools/extractor.ts:L223-L225` |
| 抽取方式 | 不是通用 ISO 解包：游戏自己的 ELF 里有一张**文件表**，每项 `0x10` 字节：`rlparam`、`解压后大小+标志`、`LBA`、`压缩后大小` | `tools/extractor.ts:L177-L203` |
| 命名 | 输出目录名 = **文件表在 ELF 里的偏移**（十六进制），文件名 = **LBA（十六进制）** + `.bin` | `tools/extractor.ts:L203-L207` |
| 压缩 | 自研位流 LZ（`rlparam` 给窗口位宽/长度位宽），不是 zlib | `tools/extractor.ts:L177-L200` |
| 关卡/舞台文件表 | `0x17F590 × 0x18C`（任务包）、`0x1879B0 × 0x3D`（舞台区域，含模型与纹理）、`0x17C340 × 4`、`0x17F100 × 0x17` | `tools/extractor.ts:L229-L232` |
| 物件模型表 | `0x180E50 × 1718`，`baseLBA 0x136B8D` | `tools/extractor.ts:L234-L237` |
| ELF 内的数据块 | 关卡表 `0xBF1A0-0xC0034`、物件表 `0xCDF70-0xDD108`、随机组 `0x116980`、路径 `0x117290`、运动 `0x161D90`、父表 `0x162EC0`、任务表 `0x180340-0x180E50`、动画 `0x30DC00` | `tools/extractor.ts:L240-L250` |

所以**加载一个关卡只需要 3 类文件**：`levelBlock.bin`、`missionBlock.bin`、以及它点名的 4 个任务包分片；
**加载一个区域的几何**只需要 2 个：模型 BIN + 纹理 BIN。物件模型是第三个来源（我们已有 1,703 个 GLB，见 §11）。

### 2.2 关卡表：一级间接

`parseLevelParameters(levelIndex, levelBlock, missionBlock)`（`bin.ts:L694-L726`）：

1. 任务包文件名：`missionBlock` 是一张 `0x10` 字节/项的表；第 `i` 关用第 `4i+1 … 4i+4` 项，字段在项内 `+0x08`，是 **u32 的 LBA，按十六进制打印**（`bin.ts:L699-L705`）。
2. 关卡行：`levelBlock` 里 `0x1BEF80 + 4×idx − 0x1BE1A0` 处取 u32 → 得到该关记录的 RAM 地址，再减基址（`bin.ts:L706-L710`）。
   - `+0x00` u8 光照预设号（5 套之一，纯渲染）
   - `+0x01` u8 **起始区域号** `startArea`
   - `+0x04` u16 **舞台区表起始索引** `stageAreaIndex`
3. 两个特例：`31 < idx < 39`（对战舞台）复用第 31 行；`idx == 41` 时 `stageAreaIndex += 1`（`bin.ts:L712-L718`）。

**Make Cancer = 关卡 11** 实测：`lighting=1`、`startArea=2`、`stageAreaIndex=3`、任务包 `141ab5,141b43,141bf5,141cae`。

### 2.3 舞台文件表

两张平行的字符串表（`scenes.ts:L250-L330` 附近），**索引 = `stageAreaIndex + 活动区序号`**：

- `stageModels[...]`：**26 项**，值是 LBA 十六进制；`stageTextures[...]`：**25 项**（若干项是同一 LBA 重复——原始的 4 个不同纹理被合并成 1 个，源码注释说是"减少加载"）。抽取器从同一张 ELF 表里取 61 个文件（含这两张表没引用的）。
- 文件路径：`${pathBase}/1879b0/${文件名}.bin`、任务包 `${pathBase}/17f590/${文件名}.bin`（`scenes.ts:L63-L70`）。`pathBase = 'KatamariDamacy'`（`scenes.ts:L26`）。
- 两张表的语义是"每个区域一对"：**索引 = `stageAreaIndex` + 活动区序号**，两张表同索引各取一个文件【实测：Make Cancer 的 `stageAreaIndex=3` 取到 `135d18`/`135049`，与该关实际加载的文件一致】。

**活动区**由任务包决定：只有"生成表不是 `NIL `"的区域才算活动区（`bin.ts:L1395-L1400`）。Make Cancer 只有区域 2 是活动的，故只取 `stageAreaIndex+0`。

### 2.4 我们手上有什么

| 输入 | 状态 |
|---|---|
| `levelBlock.bin` / `missionBlock.bin` / `parentBlock.bin` / `objectBlock.bin` / `collectionBlock.bin` / `randomBlock.bin` / `transformBlock.bin` / `animationBlock.bin` / `pathBlock.bin` / `movementBlock.bin` | **已取到**（ELF 块，共 ~1 MB），`~/KatamariRef/noclip_export/data/KatamariDamacy/` |
| Make Cancer 的 4 个任务包分片 + 舞台模型 + 舞台纹理 | **已取到**（5.6 MB） |
| 其余 18 关的任务包/舞台 | **没有**（按需再取：URL 规则见 §13） |
| 游戏光盘 ISO / ELF | **没有**。GLB 包 README 说是"Andrew 的 NTSC-U 光盘"【未验证是谁】 |

来源：`https://z.noclip.website/KatamariDamacy/<路径>`（参考实现的线上数据源，`DataFetcher.ts` 指向那里）。16 个文件的**字节数与 sha256** 记在 `~/KatamariRef/noclip_export/download_provenance.json`。

> **是否保留这些 BIN 由所有者决定**。它们是游戏数据（受版权保护），现在只在仓库外的 `~/KatamariRef`，不进仓库、不进 git。

---

## 3. 任务包（mission setup BIN）

### 3.1 头部

`parseMissionSetupBIN`（`bin.ts:L1293-L1450`）。**四个分片必须先拼成一个 `Buffer`**，包内所有偏移都是这个大缓冲的绝对偏移。

| 偏移 | 类型 | 含义 |
|---|---|---|
| `+0x00` | u32 | 扇区总数（Make Cancer = **18,907**，够 `0x09 + 1718×0x0B` 用） |
| `+0x04` | u32 | 任务级碰撞（zones）块偏移 |
| `+0x14 + 4i` | u32 | 第 `i` 区（0..4）的实例表偏移，`i` 越界表为 `NIL ` |
| `+0x04 …` | u32[] | 扇区偏移表；物件的模型扇区 = 第 `0x09 + objectId×0x0B` 项（`bin.ts:L1306-L1309`） |

`0x0B` 的步长意味着每个物件预留 11 个连续扇区（lod0/1/2、纹理、clut 等），**这是"物件模型内嵌在任务包里"的设计**——我们不需要它：物件模型已有 1,703 个 GLB。

### 3.2 实例记录（0x40 字节）

参考实现自己在注释里说字段名来自 **REROLL 的 `AttachableProp`**（`bin.ts:L1373`），所以同一结构在重制版里被原样序列化了。**逐字段**（行号 `bin.ts:L1374-L1436`）：

| 偏移 | 类型 | 字段 | 语义 / 我们是否需要 |
|---|---|---|---|
| `+0x00` | u16 | `nameIdx` | 物件类型 id（**= REROLL dump 的 `monoNameIndex`**）；`0xFFFF` 是表结束符 |
| `+0x02` | u8 | `locPosType` | 0 = 绝对位置；非 0 = **位置随机**，从随机组里抽类型并"落地"（`bin.ts:L1387-L1404`）。Make Cancer 的 524 条**全是 0** |
| `+0x03` | s8 | `randomLocGroupNo` | 随机组号（仅 `locPosType≠0` 有意义） |
| `+0x04` | s16 | `moveType` | 运动类型（`-1` = 静止【推断】）；实测 `-1` 占 395/524，其余 1…21 |
| `+0x06` | s8 | `hitOnAreaNo` | **碰触后切到哪个区域**（进/出屋的换区机制） |
| `+0x07` | u8 | `linkActNo` | **挂接动作**：0 普通、1/3/6 特殊（见 §3.4） |
| `+0x08` | u8 | `exActTypeNo` | 额外动作类型 |
| `+0x09` | u8 | `idNameNo` | 名字/id 组 |
| `+0x0A` | s8 | `dispOffAreaNo` | **从哪个区域号起不再显示**（`-1` = 一直显示） |
| `+0x0B` | u8 | `vsDropFlag` | 对战模式是否掉落 |
| `+0x0C` | s8 | `commentNo` | 图鉴/提示编号（REROLL dump 有） |
| `+0x0D` | s8 | `commentGroupNo` | 同上分组 |
| `+0x0E` | s8 | `twinsNo` | 成对物件的另一半 |
| `+0x0F` | u8 | `shakeOffFlag` | 甩动是否掉落 |
| `+0x10` | f32×3 | **平移** | 世界坐标，厘米 |
| `+0x1C` | f32 | **必须 == 1** | 参考实现直接断言（`bin.ts:L1416`）；524 件全部为 1【实测】 |
| `+0x20` | f32×3 | **旋转轴** | 单位轴（实测 112 种不同轴，绝大多数是 `(0,±1,0)`） |
| `+0x2C` | f32 | **角度（弧度，取负用）** | 参考实现用 `-值` 作角度（`bin.ts:L1420`）；最大实测 4.19 rad = 240° |
| `+0x30` | f32×3 | **缩放** | 参考实现注释「These scales are unused according to Murugo?」并**不应用**（`bin.ts:L1422-L1425`） |
| `+0x3C` | u32 | 必须 == 0 | 524 件全部为 0【实测】 |

**父/挂接不能只看这条记录**：`linkActNo` 只说明"用什么方式挂"，父子对在另一张表（§3.3）。

### 3.3 父/挂接表

`getParentList(parentBlock, levelIndex, areaIndex)`（`bin.ts:L1073-L1090`）：

```
表头：0x267798 + 4×levelIndex − 0x261EC0 → u32 得"关卡行"
行内：row + 4×areaIndex → u32 得"该区的对表"（0 或 >0xF0000000 视为无）
对表：一串 int16 对，直到 子 < 0 结束
```

- 一对 `(child, parent)`：`parent >= 0` 是真父子；`parent < 0` 只表示"这件的父是某个动态对象/自身"，用于让参考实现跳过父运动（`objects.ts` 的 `linkAction === 4 || === 6` 分支）。
- **Make Cancer 区域 2：130 条对，其中 90 条真父子**【实测】。我们自己 dump 的 523 行里有 90 行 `parentProp.monoControlIndex` 有值 —— **同一批 90 条**，说明 REROLL 侧也有这张表，只是我们没接。

### 3.4 挂接动作（linkAction）实测分布

| 值 | Make Cancer 计数 | 参考实现的处理 | 我们该做什么 |
|---|---|---|---|
| 0 | 488 | 普通子物件，随父运动 | 有父则跟随父变换 |
| 1 | 22 | 普通 | 同 0 |
| 3 | 5 | 普通 | 同上 |
| 6 | 9 | **忽略父运动** | 位置跟父、姿态不跟（吊灯类） |
| 4 | 0 | 同 6 | — |

（依据：`objects.ts` 的 `setParent`/`linkAction` 分支；`bin.ts:L1441` 把 `linkAction` 存进 spawn。）

---

## 4. 舞台 BIN（8 扇区）

### 4.1 头部

`parseLevelModelBIN`（`bin.ts:L649-L692`）：`+0x00` u32 **必须 == 8**（`bin.ts:L653`）；
`+0x04` 起 8 个 u32 扇区偏移，前 4 个是**图形**扇区、后 4 个是**碰撞**扇区；扇区若以 `NIL ` 开头则跳过（`bin.ts:L643-L647`）。

四个图形扇区的用途（Make Cancer 实测）：

| 扇区 | 内容 | 模型数 | 顶点 | 索引 | 包围盒（REROLL 帧，米） |
|---|---|---|---|---|---|
| 0 | 关卡本体 + 远景 | 5 | 29,656 | 43,992 | x −48.7…105.9，y −2.4…8.6，z −45.8…58.4 |
| 1 | 天空盒（一个 242 m 的盒子） | 1 | 70 | 168 | x/z ±121.3，y −35.5…85.8 |
| 2 | 透明物件 | 1 | 120 | 180 | x −3.4…6.1，y 0…2.8，z −3.8…6.7 |
| 3 | 半透明物件 | 0 | — | — | 本关为 `NIL` |

扇区 0 的 5 个模型里，**真正是玩法场地的看起来是第 1、3、4 号**（房子+院子，三者的并集包围盒约 38×26 m，y −1.6…3.6 m）【推断：按包围盒判断，未做视觉核对】；
第 0、2 号是几十米外的远景（x 到 −48.7 / +105.9）——这就是为什么实例坐标能到 ±43 m，而碰撞体只覆盖 ±8 m 左右的核心。

### 4.2 模型头（0x20 字节）+ VIF 流

`parseModelSector`（`bin.ts:L221-L648`）。扇区头 `+0x00` u16 模型数，`+0x04` 起 u32 偏移表（相对扇区头）。
每个模型：

| 偏移 | 类型 | 含义 |
|---|---|---|
| `+0x00` | f32×3 | 包围盒 min |
| `+0x0C` | u16 | **包字节数 / 0x10**（quadword 数） |
| `+0x10` | f32×3 | 包围盒 max |
| `+0x1C` | u8 | 动画索引（我们不需要） |
| `+0x20` | — | VIF 指令流起点 |

指令流是**固定顺序的四段循环**（参考实现把这条写在 `bin.ts:L340-L356` 的注释里）：
`UNPACK(位置 + 16 字节 run 头) → UNPACK(UV) → UNPACK(法线) → MSCNT(执行)`；
第 4 段 MSCNT 才是"把这批顶点变成三角形"的时刻。

字节级：每条 VIF 指令 4 字节 = `imm`(u16) + `qwd`(u8) + `cmd`(u8，取低 7 位)。
`cmd & 0x60 == 0x60` 是 UNPACK，低 4 位是格式码；`imm < 0x8000` 表示写往 VU1 的非顶点区，**整条跳过**（`bin.ts:L359-L362`）。

| 格式码 | 含义 | 步长 | 备注 |
|---|---|---|---|
| `V4_32`(`0x0C`) | 位置 **或** 漫反射色 | 0x10 | 位置时先有 **16 字节 run 头**，`(qwd−1)` 个 `(x,y,z,W)`；第 4 个 float 是 **VU1 用的标志位**，低 2 位右移后当蒙皮索引（`bin.ts:L364-L390`、`bin.ts:L617`） |
| `V2_32`(`0x04`) | UV | 0x08 | `(u,v)`，连续 `qwd` 个 |
| `V3_32`(`0x08`) | 位置 或 法线 | 0x0C | 位置时 run 头只有 **12 字节**（`bin.ts:L401-L420`） |
| `V4_8`(`0x0E`) | 无光照顶点色 | 0x04 | `RGBA/0x80`（`bin.ts:L427-L446`） |
| `DIRECT`(`0x50`) | GIFtag | 0x10 + 16×nloop | 里层是 `TEX0/ALPHA/PRIM/TEST/CLAMP` 寄存器写；**我们只需要知道"换材质了"**，纹理本身可跳过（§6） |

run 头（16 字节，位置 UNPACK 之前）：`flags0` u32（低 8 位 = **顶点数**）、`flags1` u32（bit1 = 有光照，bit0 = 顶点色）、
`flags2` u32（**格式码** `0x0412` = 有 UV / `0x0041` = 无 UV 且**整段不画**，是触发盒，`bin.ts:L301-L337`）。

### 4.3 索引与退化剔除

MSCNT 时按 `flags1 & 0xF0 == 0` 判定**三角带**（`bin.ts:L529`）：

- 三角带：跳过前 2 个顶点；顶点 W 的 `0xC000` 位非 0 表示"退化/断开"，跳过；偶数索引 `(j-2,j-1,j)`、奇数 `(j-1,j-2,j)` 交替绕序。
- 三角列表：顺序 3 个一组。

最后按 `(颜色, 纹理, GS 配置, 是否有光照)` 合并相邻 run 成一个**材质段**（`bin.ts:L600-L612`），
所有 run 的顶点拼成一个大数组、索引加上基址（`bin.ts:L614-L630`）。合计顶点必须 < 0xFFFF（u16 索引）。

> **我们移植时的实现要点**：材料段的划分可以简化（我们不画纹理时，只按颜色+有无光照分段即可）；真正不能省的是
> **run 头 → 顶点数 → 三个 UNPACK 的字节步长** 和 **退化顶点剔除**，否则索引会错位、网格会出现拉丝。

---

## 5. 碰撞与边界

### 5.1 舞台碰撞（4 个扇区）

`parseCollisionLists`（`bin.ts:L728-L782`）：

```
块头 +0x00 u8   = 条目数
     +0x04 u32 = AABB 表偏移（相对块头）
AABB 表 +0x00 u16(低 10 位) = 条目数（必须与上面相等）
        +0x07 u8 必须 == 0x68（V3-32 UNPACK 头）
        +0x08 起 每条 0x18 字节：min(3×f32) + max(3×f32)
顶点块 +0x07 u8 必须 == 0x6C（V4-32 UNPACK 头）
        +0x00 u8：bit7 = 三角带；低 7 位 → 带 = 顶点数−2，列表 = 组数×3
        +0x08 起 每顶点 4 个 f32：x, y, z, **zone（float 位模式，要用 struct 重解释）**
        下一条目 = 0x10×顶点数 + 0x0C，末字节 0x97 = MSCNT 收尾
```

**Make Cancer 实测**：4 个碰撞扇区分别是 `66 / 0 / 1 / 1` 条。合计 **68 个 AABB、7,154 个顶点、3,456 个三角形**。
AABB 覆盖 x −757…1,081 cm、y −1,385…159 cm、z −799…654 cm。

**zone id 分布**（顶点 W 的 float 位模式）：`0`×5,712、`24`×762、`3`×456、`8`×131、`1`×47、`5`×46。
即：碰撞网格**逐三角形带区域标签**，这就是原作的"你站在哪一块"机制；`0` 是默认（无区）。

### 5.2 任务级 zones

`+0x04` 指向的是**同一套结构**，但语义不同（`bin.ts:L1298-L1300`）：7 个体积、每组 6–95 个顶点。
实测 bbox 里第一个是 y = −1,691.5 cm 的**巨大平面**（x −3,017…3,212，z −2,733…4,242）——典型的"掉出去"判定面；
其余是 3–5 m 的小盒子（进出门/换区触发体）。zone id 分布 `1,2,8,0,4,16,3`。

### 5.3 原作没有"墙"

- 关卡包**没有**边界/半径/越界字段；能阻止球的是**几何**（台地、护岸）和**尺寸门**（20 cm / 1.5 m / 60 m 的可视 barrier）。
  这与 `ORIGINAL_TERRAIN_AND_LAYOUT.md` §12 的结论一致，但那份文档说"数据里没有地形几何"——**那是 REROLL 导出表的结论**；
  PS2 舞台包里有完整几何，也就是本节和 §4 的 29,846 顶点【实测】。
- 我们的重建现在用"4 个假墙面 + 一块平板"代替。**实测落差**：把舞台网格（主体模型 4）里朝上的三角形按面积加权，
  上表面总面积 946 m²，其中 **55% 落在 y = −1.2…−0.6 m**（峰值 y = −1.1 m 占 26.6%），次带在 +2.0 m 与 +3.3 m（各约 3%）。
  即**真实地面主带在 REROLL 帧 y ≈ −1.0 m**，我们的假地板在 y = 0，**抬高约 1.0 m**。
  这与 `ORIGINAL_TERRAIN_AND_LAYOUT.md` 用"道具底面中位数 −0.767 m、主带 −1.0 m"独立推出的结论一致——两条独立路径互相印证。
  （度量脚本读的是 `~/KatamariRef/noclip_export/kse_out/stage.glb` 的顶点/法线/索引。）

---

## 6. 纹理与 GS 细节（可以整段跳过）

参考实现里的 `decodeTexture`（`bin.ts:L60-L207`）把 GS 显存里的 `PSMT4`/`PSMT8` 解成 RGBA8，CLUT 是 `PSMCT32`；
wrap 模式从 `CLAMP` 寄存器取；**没有 mipmap**（直接断言 `lcm===0 && mxl===0`）。混合模式只有两种
（`0x44` = SrcAlpha/OneMinusSrcAlpha，`0x48` = SrcAlpha/One，`render.ts`）。

**我们为什么可以跳过**：我们要的是几何 + 变换 + 碰撞；材质只需要颜色/是否透明。纹理可以用现成的 GLB 包或以后单独做。
跳过纹理把移植量砍掉约三分之一（参考实现里 `decodeTexture` + `parseStageTextureBIN` + 纹理索引逻辑 ≈ 200 行）。
代价：GLB 里的模型是纯色块（本文原型就是这样）。

---

## 7. 坐标系与单位（最重要的一节）

### 7.1 实测

| 事实 | 数值 | 证据 |
|---|---|---|
| 1 个原始单位 | **1 厘米** | 房子/院子 38×25 m、球起始直径 22.2 cm 都对得上 |
| REROLL dump `position`（米）↔ PS2 实例平移（厘米） | **`reroll = −0.01 × ps2`（三轴全取反，无平移）** | 524 件：360 件误差 < 1e-6 m，410 件 < 1 mm，511 件 < 1 m |
| 反向（REROLL→PS2） | `ps2 = −100 × reroll` | 同上 |
| 参考实现的渲染变换 | `rotateX(π)`，即 `(x, −y, −z)` | `scenes.ts:L28` |
| 道具包围盒中心 vs 实例平移 | 平移就是**包围盒中心**（两侧同一约定） | 位置逐件对得上【实测】 |

### 7.2 三个必须记住的推论

1. **两套数据是同一世界坐标系**，不是"区域局部坐标"。`ORIGINAL_TERRAIN_AND_LAYOUT.md` 说道具是"区域局部米制"，
   按本节的逐件对拍，这个说法可以升级为：**道具坐标就是关卡世界坐标，只是整体系被点反射并除以 100**。
   实际意义：PS2 舞台几何可以**直接**和我们那 511 件道具摆在一起，不需要任何拟合。
2. **三轴全取反是点反射，行列式 −1**。把 PS2 网格并进 REROLL 帧时，顶点要 `(−x,−y,−z)/100`，**三角形绕序要翻转**，
   否则所有面朝里。旋转不用改：点反射与旋转可交换（`M R M⁻¹ = R`，因为 `M = −I`）。
3. **参考实现的 `rotateX(π)` 是纯旋转（行列式 +1）**，与第 2 条的点反射差一个 x 轴取反。所以**noclip 网站上的关卡是左右镜像的**【推断，强】；
   不要拿它的截图当"原作朝向"的证据，几何数值仍可用（镜像不改变距离与高度）。

### 7.3 单位小结

`cm`（PS2 原始）、`m`（REROLL/我们）、球直径 22.2 cm、`MAX_DIAMETER 120 cm`；角度用**弧度**，且记录里存的是**负值**。

---

## 8. 我们要什么 / 能跳过什么

| 数据 | 优先级 | 工作量（按本规格重实现） | 说明 |
|---|---|---|---|
| 实例平移 + 旋转（轴/角）+ 缩放 | **P0** | ~60 行 | §3.2；这是现在最大缺口（401/524 有真实朝向） |
| 父子挂接表 | **P0** | ~30 行 | §3.3；90 条 |
| 舞台网格（顶点/索引/材质段） | **P0** | ~180 行 | §4；唯一"复杂"的部分，但只需要 V4_32/V2_32/V3_32 + MSCNT |
| 碰撞 AABB + 三角带 + zone id | **P1** | ~70 行 | §5.1；替代我们的假地板/假墙 |
| 任务级 zones | **P2** | 复用 §5.1 | 换区/掉落判定 |
| 每实例显示区（`dispOnAreaNo`/`dispOffAreaNo`） | **P1** | ~5 行 | 我们完全没有这个概念 |
| 物件模型（内嵌扇区） | **跳过** | — | 已有 1,703 个 GLB |
| 纹理/CLUT/GS 寄存器 | **跳过** | — | §6 |
| 光照（5 套预设） | **跳过** | — | 我们用 Unity 灯光 |
| 动画/运动/路径表 | **跳过** | — | 与原作行为复现无关（见 `BALL_PHYSICS_IMPLEMENTATIONS.md`） |
| 随机刷新组 | **P2** | ~40 行 | Make Cancer 一条都没用到（524/524 是绝对位置） |

**总移植量**：我这一版 Python（含 GLB 写出、CLI、报告）**981 行**；只做 P0+P1 大概 **350 行**。

---

## 9. 移植实现顺序（按本规格重写，不是翻译）

```
read levelBlock, missionBlock
  → level_parameters(idx)            # 4 个任务包名 + startArea + stageAreaIndex
read 任务包 4 个分片, 拼成一个 buffer
  → zones = collision_lists(+0x04)   # 与舞台碰撞同一套解析函数
  → for area in 0..4:
        table = u32(+0x14+4*area); 若 NIL 跳过
        parents = parent_list(parentBlock, level, area)
        for rec in 0x40 步长 until u16(rec)==0xFFFF:
            平移/轴/角/缩放 → (t, q, s)；q 由 轴+(-角) 生成
            dispOn = max(area, startArea); dispOff = rec[0x0A]
            parent = parents.get(tableIndex)
read 舞台模型 BIN (stageModels[stageAreaIndex + slot])
  → 8 扇区; 前 4 个: 扇区 → 模型 → VIF 流 → (顶点, 索引, 材质段)
  → 后 4 个: 碰撞(AABB + 带 zone 的三角带)
把网格按 §7.2 的 (−1,−1,−1)/100 + 翻转绕序送进我们的坐标系
```

两个**容易踩的坑**（都是实测踩到的）：

1. **任务包必须 4 个分片拼起来读**。单独读第一片会在关卡表处越界（我在对拍脚本里就是这么炸的）。
2. **读文件时不要用 Node 的 `Buffer.buffer` 直接当 ArrayBuffer**：小文件来自共享内存池，会带上 `byteOffset`，表现为"读到别的文件的内容"。
   参考实现的 `ArrayBufferSlice` 不接受带偏移的视图——这是环境坑，不是格式坑。Python 侧无此问题。

---

## 10. 许可后果（请所有者裁决）

| 事实 | 依据 |
|---|---|
| noclip 仓库的 `LICENSE` 是 **MIT 正文 + 一段说明**："部分实现是逆向的、非净室；有问题联系作者" | 仓库 `LICENSE`（pinned commit） |
| `package.json` 写 `"license": "MIT"`；GitHub API 因那段说明报 `NOASSERTION` | 同上 |
| **概设里"noclip 是 GPL-3"的前提不成立** | 同上 |
| 但"逐行翻译"仍是对该作品的**衍生作品**：即使上游是 MIT，也受 MIT 条款约束（保留版权与许可声明）；若上游是 GPL-3，则只能以 GPL-3 兼容许可进仓库 | 常规许可法理【推断，但确定】 |

**处理方式**：

- **仓库里只放这份规格**——用本项目的话描述字节布局、字段语义和解析顺序，不含 noclip 的代码文本，也不含游戏数据。
- **解析器在仓库外**：`~/KatamariRef/tools/katamari_stage_extract/`（Python，981 行），按本规格独立写；对拍用的 TS 脚本也只在仓库外。
- **要进仓库的前提**：仓库许可定下来之后。若选 GPL-3 兼容许可，照规格重写的解析器可以进；若选 MIT/Apache 这类，**也仍然可以进**，
  因为它不是上游代码的衍生作品（只是"读了规格"），但**不能**把 noclip 的源码或逐行翻译版放进来。
- 游戏数据（BIN/GLB）**永远不进仓库**；`~/KatamariRef/noclip_export/` 下的产物仅供本地研究。

> 遗留决策：**(a)** 仓库最终许可；**(b)** 是否允许为了继续研究把其余 18 关的 BIN 也取到 `~/KatamariRef`（约 60 MB）。

---

## 11. 与我们现有数据矿的对比与收益

### 11.1 现在的重建（`Docs/Design/Levels/_ref_cancer/placement.json`）

| 维度 | 现在 | 原作数据里其实有 | 差距 |
|---|---|---|---|
| 件数 | 511（523 − 12 `DummyProps`） | 524 条实例（512 件 + **12 个纯锚点**） | 件数一致，差 12 个锚点 |
| 旋转 | **511 件全部 `[0,0,0,1]`** | 401/524 非零角（76.5%），358 件 > 45° | **最大缺口** |
| 缩放 | 无 | 518×(1,1,1)、5×0.9/0.1、1×非均匀 | 6 件 |
| 父子 | **无 `parent` 字段** | 90 条真父子 + 40 条挂接标志 | 130 条关系 |
| 地形 | 一块 `ground` 平面 + 4 面假墙 | 29,846 顶点 / 14,780 三角形 / 3 个扇区 | 全缺 |
| 碰撞 | 无 | 68 个 AABB + 3,456 三角形 + zone id | 全缺 |
| 区域/换区 | `zones=[{id:"room"}]` 一个假区 | 7 个任务区体积 + 每三角形 zone id | 全缺 |
| 校验 | 27 个错误（26 个 AABB 互穿 + 1 个出生点净空） | — | 旋转接进来后**必须换 OBB 判定**，否则 AABB 变大、互穿只会更多【推断】 |

### 11.2 收益（可量化）

- **朝向**：76.5% 的道具、68% 的道具转 > 45°。现在它们全部朝同一方向，家具/招牌的摆放是错的。
- **挂接**：90 条父子（≈17% 的件）。现在是"各自独立摆"，父移动时子不跟。
- **地形**：从 1 个平面 + 4 面墙 → 29,846 顶点真几何。舞台几何实测地面主带 **y ≈ −1.0 m**（§5.3 的面积加权），
  我们的假地板在 y = 0，**抬高约 1.0 m**；这正是 `ORIGINAL_TERRAIN_AND_LAYOUT.md` 从道具底面观察到的
  "地板凭空抬高 1.3–1.5 m"的来源（两者差 0.3–0.5 m，量级一致，差异来自道具 pivot 与地面不是同一层）。
- **碰撞**：68 个 AABB + zone id 可以直接替代我们"按 AABB 边缘放墙"的做法，并且**自带区域语义**。
- **坐标系**：已知的 `−0.01×` 映射让 PS2 几何与 REROLL 道具表**免拟合**对齐（这是本文最省事的一条）。

### 11.3 与 REROLL dump 的字段对照

| PS2 字段 | REROLL dump 对应列 | 我们有吗 |
|---|---|---|
| `nameIdx` (u16) | `u16MonoNameIdx` / `monoNameIndex` | ✅ |
| 平移 | `position`（米，已点反射） | ✅（差 −0.01 映射） |
| 轴 + 角 | **无** | ❌ |
| 缩放 | **无** | ❌ |
| 父 | `parentProp.monoControlIndex` | ✅（但没接进 placement） |
| `commentNo` / `commentGroupNo` / `shakeOffFlag` | 同名 | ✅ |
| `hitOnAreaNo` / `dispOffAreaNo` / `moveType` / `linkActNo` | **无** | ❌ |
| `twinsNo` / `vsDropFlag` / `exActTypeNo` / `idNameNo` / `locPosType` | **无** | ❌ |

**结论**：REROLL dump 是"名字 + 位置 + 父 + 图鉴"的**子集**；旋转/缩放/区/挂接动作**只能在 PS2 侧拿**。

---

## 12. 未验证 / 开放问题

1. **缩放字段的语义**：【未验证】。默认值 1 强指向"缩放"，但那 1 件非均匀 `(0.0741, 0.0291, 0.0197)` 缩完是 1.7×0.3×0.3 cm 的薄片，
   也可能是"碰撞盒尺寸"或"贴花尺寸"。需要在实机/REROLL 侧比对同一件道具。
2. **noclip 视图是否真的左右镜像**：【推断，强】。依据是 §7.2 的行列式论证；没有截图级对照（需要一张有文字/非对称地标的原作截图）。
3. **`AREA_ID C` ↔ 区域 2**：REROLL 的 `MISSION_ID 11 / STAGE_ID 1 / AREA_ID C / PROP_SCENE_NAME m_11_C` 与 PS2 `level 11 / startArea 2`
   对得上，但 `C` 不是区域号（不能是 12），当成枚举更合理【推断】。
4. **其余 18 关**：关卡表 19 关全部对拍通过（§1），但**只对 Make Cancer 解析了任务包和舞台包本体**；
   其余关的扇区数/碰撞数/实例数未知，特别教学关有特殊木星球（`bin.ts:L1469-L1471`）。要支持其他关，按 §2 的 URL 规则取对应文件即可。
5. **REROLL 侧 523 vs PS2 524（去锚点后 511 vs 512）**：差 1 件，没找到是哪一件【未验证】。
6. **光盘归属**：GLB 包 README 写"Andrew 的 NTSC-U 光盘"，我不知道是不是所有者本人【未验证】。
7. **`moveType`（s16）**：Make Cancer 实测 `-1` 占 395（75%，推断为"静止"），其余散在 1…21（13×13、12×18、3×18、18×15、4×15……）；语义来自 `movementBlock`/`parseMotion`，未展开。

---

## 13. 参考与产物

**参考实现**（只读，未进仓库）：

- 仓库：https://github.com/magcius/noclip.website （提交 `ee4d72f92c79`；本地 clone `2cebac2a5d1d7c44535397a14f51e764ca823767`）
- 解析器：`src/KatamariDamacy/bin.ts`（1476 行）、场景：`src/KatamariDamacy/scenes.ts`、物件/挂接：`src/KatamariDamacy/objects.ts`、渲染：`src/KatamariDamacy/render.ts`
- 抽取器：`src/KatamariDamacy/tools/extractor.ts`
- 结论：**没有 glTF/JSON 导出**（`DownloadUtils.ts` 只做 blob 下载；`Scenes_FileDrops.ts` 里没有 Katamari）；只有纹理查看器和存档。

**产物**（全部在仓库外，`~/KatamariRef/`）：

| 路径 | 内容 |
|---|---|
| `tools/katamari_stage_extract/` | 自写 Python 解析器（981 行，`python3 -m katamari_stage_extract --level 11`） |
| `noclip_export/data/KatamariDamacy/` | 16 个原始 BIN（6.59 MB），sha256 见下 |
| `noclip_export/kse_out/` | `instances.json`（524 件含 t/q/s/父/区）、`stage.glb`（1.22 MB）、`collision_area2.json`、`zones.json`、`report.json` |
| `noclip_export/download_provenance.json` | 每个 BIN 的 URL / 字节数 / sha256 |
| `noclip_export/export_katamari.ts` | 对拍用的 TS 脚本（调用参考实现，不进仓库） |

**数据源的 URL 规则**：`https://z.noclip.website/KatamariDamacy/1879b0/<LBA hex>.bin`、
`https://z.noclip.website/KatamariDamacy/17f590/<LBA hex>.bin`、以及 10 个 `*Block.bin`。
