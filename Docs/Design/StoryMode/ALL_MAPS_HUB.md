# 合集场景 AllMapsHub：组装规范与清单格式

> 状态：设计 + 数据已定；运行时与编辑器装配器（`Editor/Hub/`）代码已写，编译门与本机 EditMode 测试通过，**尚无 Unity 验证**。决定出处：[OPEN_DECISIONS](<../../OPEN_DECISIONS.md>) 2026-11-17 条。

## 0. 结论

1. **合集 = 一张场景**，输出 `Assets/_RefUnlicensed/Scenes/AllMapsHub.unity`：以 StoryMode 为底板（球、座位、HUD、大厅、启动器、结果卡），把各源场景的**关卡根**按 XZ 偏移搬进 `Island_<id>` 外壳。它是**产物**，由菜单 `VRC Katamari/Hub/Assemble All-Maps Hub` 每次从零重建，不手改。
2. **因含原作素材（蟹座），合集场景放 `_RefUnlicensed/`，不进 git、不上传**（AGENTS：参考素材永不入库、不上传）。装配器规定：任一关的源场景在 `_RefUnlicensed/` 下，输出场景也必须在 `_RefUnlicensed/` 下。
3. 四关：蟹座小院（计时、无星）、野餐第一关（计时）、地图 0 沙盒（自由练习）、地形实验室（自由练习，现有 C23）。**所有关允许中途回大厅**；计时关中途回大厅 = 放弃本局。
4. 加一张图 = 按 §1 做好源场景 + 在 [hub_levels.json](<hub_levels.json>) 加一条 + 重跑装配菜单；不改 C# 代码（除非新增了 §4 的世界坐标型字段）。
5. 大厅房间已加宽到 16 m（4 块垫至少 14 m，公式见 §2.3）。

## 1. 一张新地图要怎样写

1. **场景分工**：根物体分两类。*基础设施*（球、输入、座位、视图、相机、灯、`SceneDescriptor`、`PipelineManager`、HUD、回合/结果/大厅组件）用底板的，**不搬**；*关卡根*（几何、道具、结构、宿主、标记）被搬。关卡根里不得出现基础设施单例类型。
2. **坐标**：作者坐标就近原点，Y 向上，**不为合集改坐标**；偏移由清单给，**Y 偏移必须为 0**（`groundOffset`、`killPlaneY` 等 Y 标量不随根搬动）。
3. **登记宿主**：每关至少一个 `LevelArenaHost`（地面高度在它的 `groundOffset`）；多宿主关由清单 `arenaFirst` 指定“外框宿主”先登记（清表+外墙+地面），其余追加。合集里所有宿主的自注册被挂起，由切关逻辑统一登记。地形用 `TerrainFieldHost`，一关最多一个。
4. **道具**：每关的 `PickupFieldHost` 的 `itemIds` 跨清单所有被搬的关唯一；位姿/尺寸走局部数据。
5. **起点**：清单 `start`（中心、朝向、可选球径 `diameterM`）；缺省读源场景电机。入口标记（若有）在地面上且离球座 ≤12 m，自由练习图可省略入口（启动器退回座位落点）。
6. **回合**：`kind: "round"` 的关写 `roundFromPlacement`（读自己的 placement 文档）或在 `round` 块给限时/目标径/kill plane；`kind: "freeRoam"` 不需要回合数据。出口：装配器给每关放 `HubReturnButton`，作者不用做。
7. **命名**：关卡 id = 数据目录名（参考关 `_ref_*`）；外壳固定 `Island_<id>`。
8. **许可**：见 §0-2。

## 2. 清单格式 `hub_levels.json`（schema `katamari.hub-levels/1`）

`/1` 的新增字段**全部可选**，缺省 = 今天的单关行为（`/0` 的文件内容原样有效）。

| 位置 | 字段 | 含义 / 缺省 |
|---|---|---|
| 顶层 | `room` | 垫尺寸/间距/前移/边距（不变） |
| 顶层 | `hubs[]` | 要装配的合集：`id`、`scene`（输出场景）、`base`（底板）、`levels`（关 id，顺序 = 选关垫顺序）；缺省无合集 |
| level | `scenes` | 哪些场景提供这一关（语义不变；合集场景名也要列入） |
| level | `kind` | `round`（缺省）/ `freeRoam` |
| level | `allowAbort` | 计时关能否中途回大厅（回大厅 = 放弃本局）；缺省 false；合集里全部 true |
| level | `stars` | false = 结果卡不画星；缺省 true |
| level | `source.scene` / `source.takeRoots` / `source.leaveBehind` | 源场景；被搬的根或子树路径；`leaveBehind` = 不搬的子路径（留在底板） |
| level | `island.offset` | `[x, 0, z]`，Y 必须 0 |
| level | `start` | `center`、`headingDeg`、可选 `diameterM`（已是作者坐标，装配器加偏移） |
| level | `round` | 缺省值覆盖：`killPlaneY`、`timeLimitSeconds`、`targetDiameterM` |
| level | `arenaFirst` | 先登记的外框宿主路径（源层级路径）；缺省取物理场驱动的 `sandboxFrame`，其后是驱动 `cells` 的顺序，再后是岛下其余 `LevelArenaHost` 按层级顺序 |
| level | `content` / `entry` | 源场景里的层级路径；有 `island` 且场景里有 `Island_<id>` 外壳时相对外壳解析（见 2.2）；`kind: freeRoam` 可省 `entry` |

### 2.1 当前四关（偏移与源数据，来自 R47 盘点）

| 关 | 偏移 | takeRoots | 起点 / 球径 | 备注 |
|---|---|---|---|---|
| 蟹座 `_ref_cancer` | (0,0,0) | `LevelContent/_ref_cancer` | (-5,-1,2.5)，0.222 m | 限时 420 s、目标 0.8 m、killPlaneY -4；`stars:false` |
| 野餐第一关 `story_01_bench_size` | (0,0,-1200) | `LevelContent`、`LevelArena`、`LevelEntry`；`leaveBehind: LevelContent/hub` | (-12.5,0.15,-12.5)，默认径 | 大厅子树 `LevelContent/hub`（1200 m 岛）留在底板 |
| 地图 0 `physics_test_field` | (-1200,0,0) | `LevelContent` | (0,0.15,-39.5) 朝 +Z | 14 个 `LevelArenaHost`，`SANDBOX` 外框先登记；驱动/设定器不搬 |
| 地形实验室 `terrain_contact_lab` | (0,0,1200) | `LevelContent`、`Ground` | (0,0.15,0) | 只有 C23 一块地形；Ground 80×80、y=-1.5 |

岛间距 ≥1000 m（大厅在 (1200,0,0)）；坐标绝对值 ≤1300，float 间距约 1.2e-4 m，对 0.005 m 接触皮够用。蟹座放原点因为它最挑精度。

### 2.2 解析器与路径解析（`HubLevelCatalog.cs`）

`schema` 接受 `/0` 与 `/1`，未知字段忽略（用 `LevelJsonParser` 读，不用 `JsonUtility`：它会给缺失的嵌套块填默认实例，分不出“有没有写 `start`”）。校验（错误带“第几条 + 字段”）：

- `kind` 只能是 `round`/`freeRoam`；`entry` 只有 `freeRoam` 关可缺（`/0` 文件里没有 `kind`，等于 `round`，仍要 `entry`）。
- `island.offset` 必须是 3 个有限数且 Y = 0；`source.scene` 是 `.unity` 路径；`takeRoots` 非空、每项是层级路径（无首尾 `/`）；`start.diameterM`、`round.timeLimitSeconds`、`round.targetDiameterM` 为正。
- `hubs[]`：`scene`/`base` 是 `.unity`；`levels` 里的 id 必须存在、不重复；这些关必须有 `source` 与 `island.offset`、`scenes` 里列出合集场景名、岛偏移两两不同。
- `content`/`entry` 的解析：关有 `island` 块**且场景里已有 `Island_<id>` 外壳**时，路径相对外壳；否则相对场景根（所以同一份清单在普通单关场景里行为不变）。导入器在外壳下重建被搬对象的**源层级路径**（`LevelContent/_ref_cancer` 搬进来仍是 `Island__ref_cancer/LevelContent/_ref_cancer`，中间的 `LevelContent` 是空占位对象），所以同一段文字在源场景和合集里指同一个物体。

### 2.3 房间宽度

所需宽度 = n×padSize + (n−1)×padGap + 2×margin（同 `HubSelectMath.RoomWidthNeeded`）。4 块垫 = 4×2.8+3×0.6+1.0 = **14.0 m**；`hub/placement.json` 房间 16 m（地面 size.x 16、东西墙中心 ±8）。`Docs/tests/hub_levels` 用同一公式校验。

## 3. 切关接口（运行时，批 A–C）

选关垫触发 `HubLevelLauncher.TryStartLevel(i)` 时先**切关**再进既有流程：清电机障碍表**和地形网格**、清道具代理 → 设该关地面高度 → 逐宿主登记（外框先清表，其余追加并加 id 偏移）→ 注册该关地形 → 设白名单并集 → 设起始球径、`TryResetForNewRound`、放球到 `start` → 回合控制器 `SelectLevel(i)`。回大厅：计时关先 `AbortRound()`；自由练习没有结果卡，`HubReturnButton` 是唯一出口。结果卡对 `stars:false` 的关不画星，只显示球径与吸收件数。

### 3.1 岛的开关（`LevelSwitchHost.islandRoots`）

- 规则只有一条（`IslandActivationMath.ShouldBeActive`）：任一时刻至多一座岛开着，就是当前关的岛；回大厅后当前岛也关（`keepIslandOnReturn` 为 true 时保留）；尚无当前关时四岛全关。关掉的岛不渲染，岛内所有宿主（拾取场、运动、坠落、地形、竞技场）的 `Update` 都不跑。
- `islandRoots[i]` 与 `levelIds[i]` 一一对应，由组装菜单写入 `Island_<id>`；为空 = 该关不参与开关（旧行为）。自检要求：根数 = 关数、不重复、保存时处于激活状态、岛内不含球/座位/回合/启动器/返回按钮/切关宿主。
- 初始化：Udon 行为在所在物体首次激活之前没有跑过 `Start`，对它调方法不安全。所以 `LevelSwitchHost.Start` 先把所有岛打开，两帧之后（`ParkIdleIslands`）再按规则关掉，这样每个宿主都跑过一次 `Start`，之后的 `TryActivate` 是同步的。两帧内的 `TryActivate` 返回 `FailIslandsNotReady`。`TerrainFieldHost` 的 `deferRegistration` 路径在 `Start` 里不登记，所以首次激活不会覆盖当前关；`Start` 只跑一次，开关岛不会重复登记。
- `TryActivate(i)` 顺序：先唤醒（若停放）并打开目标岛 → 离开关的 `ResetField`（吸附道具回到岛内原位）→ 关掉离开的岛 → 清表/登记/球就位。回大厅到达后 `ParkActiveIsland`；同一关再次开始时 `WakeActiveIsland`，随后走回合自己的重置。
- 球、座位、`HubResultBoard`、`HubReturnButton_<id>`、`HubRoom` 都是场景根物体，不在岛内；岛关闭不影响它们。关闭期间球身上吸附的道具挂在球下，随球可见。
- 关闭期间 `MotionHost`/`PropFallHost` 暂停：`MotionHost` 用绝对 `Time.time` 算相位，重新打开时相位会直接跳到当前时刻；`PropFallHost` 用 `deltaTime` 累加，无影响。重新打开后运动道具位置是上次关闭时的姿态，回合重置（`ResetField`）只还原被吸附的道具。
- 多人：`SetActive` 只对本地客户端生效，各客户端各自开关自己的岛，别的玩家所在关的岛在本机是关着的，看不到对方的球和场景；本轮按单人测试，不解决。

## 4. 世界坐标型序列化字段要补偏移

运行时读 `Transform` 的位置自动跟随外壳；**序列化在组件里的世界坐标**搬到偏移岛后必须由装配器补偏移：

- `TerrainFieldHost.vertices[]`：每个顶点加偏移（块 AABB 运行时重算）。
- `MotionHost.pathPoints[]`：xyz 平铺，每 3 个数加偏移。
- 电机 `startCenterWorld`：不搬电机，起点用清单 `start` + 偏移。
- Y 标量（`groundOffset`、`killPlaneY`、`PropFallHost.floorY`）不补，所以 Y 偏移禁止。

新增这类字段（`Vector3`/`Vector3[]`/名字含 Point/Center/Position/World 的 `float[]`）时，要加进装配器的补偿名单；计划用一条 EditMode 反射测试强制分类。

## 5. 常见坑

1. `ClearObstacles()` 不清地形网格、道具代理、地面平面、白名单、起始球径，切关要逐项处理。
2. `LevelArenaHost` 默认自注册；合集里漏挂起一个，平面表第一帧就满并永久 latch。
3. 同名 `itemIds` 跨关：别关宿主会认领吸收事件。
4. **RefCancer 里的 StoryMode 复制残留不搬**：它的 `LevelContent` 根上有一个 130 行的 `PickupFieldHost`（野餐关的）和 `LevelContent/hub`、根 `LevelArena`、未激活的 `StoryLevelLab`、整套 `Hub*` 根、`DebugCamera*` 根都是从 StoryMode 复制来的；蟹座只搬 `LevelContent/_ref_cancer`。把整个 `LevelContent` 根搬进来会让 `itemIds` 与野餐重复、多出一个活的启动器。
5. StoryMode 的 `LevelContent` 根下同时有野餐关内容和 `hub` 大厅子树（1200 m 外），野餐关搬运时用 `leaveBehind` 留下大厅。
6. 序列化世界坐标漏补：球穿地或被不存在的地形托住，只在偏移岛上出现。
7. 在合集场景里跑导入器（`PlacementImporter`/`TerrainMeshImporter`）：根 `LevelContent` 已被包进 Island，会新建空根。改源场景再重组。
8. 源场景在编辑器里有未保存修改时装配器拒绝运行；不要保存源场景。
9. 场景级设置（天空/雾/环境光）不随根走；四个源场景都无烘焙光照、天空一致（R47 盘点），目前无差异。
10. 搬运后 U# 字段为空：编译 → 保存 → 重开 → 再装配。
11. 起点悬空/入口离球座太远：启动器退回座位落点；Play 里“被重生回大厅”首先怀疑它。
12. `_RefUnlicensed` 下的任何东西（含 `AllMapsHub.unity`）绝不入库；`SerializedUdonPrograms` 签名噪声整批提交或丢弃。

## 6. 装配器（菜单与流程）

菜单（`Editor/Hub/AllMapsHubAssembler.cs`）：

| 菜单 | 作用 |
|---|---|
| `VRC Katamari/Hub/Inspect All-Maps Sources` | 只读：逐关打开源场景（已开着的不关），列根物体、takeRoots 解析、搬运集合里的宿主、**会离开搬运集合的引用**（按“组件.字段 → 目标路径”聚合）、基础设施命中（= PROBLEM）、源场景自带的起点/回合/驱动数据、是否有烘焙光照。不改任何场景。 |
| `VRC Katamari/Hub/Assemble All-Maps Hub` | 重建 `hubs[]` 里 id 为 `AllMapsHub` 的合集场景（流程见下）。 |
| `VRC Katamari/Hub/All-Maps: Use Instantiate Fallback` | 勾选 = 用 `Instantiate`+重映射代替 `MoveGameObjectToScene`（后备，默认关；见 6.2）。 |
| `VRC Katamari/Hub/Assemble Hub`（既有） | 单关场景的装配，行为不变；一个场景同时提供多关时报 PROBLEM 并指向上一行。 |

### 6.1 流程

1. 读清单、校验路径：源场景或底板在 `_RefUnlicensed/` 下则输出必须在其下；输出不得等于底板/任一源。
2. 任何**已打开且有未保存修改**的场景 → 拒绝（输出场景自己的旧版本除外：它是产物，直接丢弃重建）。
3. `NewScene` 关掉所有场景 → `File.Copy(底板, 输出)`（只拷 `.unity`，已有 `.meta` 保留）→ `Refresh/Import` → 打开输出。
4. **球芯皮肤**：输出在 `_RefUnlicensed/` 下时，复用 `CoreSkinAssembler.AssembleOpenScene()` 给共用球装原作球芯 `core_01`（装配器要求场景未改动，所以紧跟在打开输出之后）。依据 [OPEN_DECISIONS](<../../OPEN_DECISIONS.md>) 2026-11-17 球芯条与 [CORE_SKIN](<../BallPresentation/CORE_SKIN.md>)：皮肤是球的、不是关的（原作关卡与 `core_XX` 的对应关系未知，皮肤随碰撞直径缩放），合集只有一个球，所以**四关都显示这张皮**，与 RefCancer 一致；“只在蟹座关显示”需要运行时按关切换渲染，没有决定要做。素材 `Assets/_RefUnlicensed/Models/KatamariCore/core_01.obj/.png` 不存在 → `[allmaps] warn: core skin skipped …`，不报 PROBLEM，球保持底板的样子；素材在但装配被拒（接线不符预检等）→ `warn: core skin not applied: <原因>`；装上后自检要求球芯下恰有一个 `OriginalCoreSkin`。
5. 在**还是底板原样**时先导入大厅房间（`HubAssembler.AssembleRoomGeometry`，导入器已知良好的上下文），之后关卡再搬走。
6. 逐关 `IslandImport.Import`（`hubs[].levels` 顺序）：
   - 源是底板 → 直接在输出场景里取；否则 Additive 打开源场景（已开着且 dirty → 拒绝）。
   - 分析：解析 takeRoots/leaveBehind（根名或子树路径）；搬运集合里出现基础设施单例（电机、输入、座位、视图、回合/结果/启动器/房间宿主、切关宿主、驱动、设定器、场景描述符、PipelineManager）或“世界坐标不搬”的类型 → PROBLEM；读源场景电机起点/地面、回合控制器设置、物理场驱动的竞技场顺序/白名单/出生点；列出所有指向搬运集合之外（同一源场景）的序列化引用。
   - 执行：建 `Island_<id>`（先在原点）；离开的子树先摘下；每个 takeRoot 在外壳下重建祖先占位对象，`MoveGameObjectToScene`（源≠输出时）+ `SetParent(worldPositionStays:true)`；`leaveBehind` 子树留在原处（底板情形下放回重建的 `LevelContent` 占位根下）；外壳移到偏移；**引用按源层级路径重映射到输出场景同路径同类型对象**（同类型多个按序号），找不到 → PROBLEM（属性路径 + 目标路径），不做“按类型猜”；补世界坐标数据（`TerrainFieldHost.vertices` 每个顶点、`MotionHost.pathPoints` 每 3 个数加偏移，偏移全 0 则跳过）；所有 `TerrainFieldHost.deferRegistration = true`；被改的代理 `CopyProxyToUdon`；复查没有引用仍指向源场景；关闭源场景（不保存）。
7. 每关数据：竞技场顺序 = `arenaFirst`（缺省取驱动的 `sandboxFrame`）→ 驱动 `cells` 的竞技场顺序 → 其余按层级顺序；地面高度 = 第一个竞技场的 `groundOffset`（无竞技场取源电机的 `groundPlaneOffset`）；白名单 = 驱动的 `kindWhitelist`；地形宿主一关至多一个；道具宿主 = 源回合控制器的 `pickupHost`（否则岛下第一个）；起点 = `start.center`（缺省：驱动出生点，再缺省：源电机 `startCenterWorld`）+ 偏移；朝向 `start.headingDeg`；**起始体积 = `SizeModelMath.TrySphereVolumeMm3(diameterM, 底板电机的 semanticDiameterPerCoreDiameter)`**，`diameterM` 缺省取源电机的 `startDiameterWorldM`，仍无则用底板电机自己的起始体积；从不读源电机的 `startVolumeMm3`（有 `startDiameterWorldM` 时它在运行时被覆盖，存的值不是起点）。
8. 回合计划：自由练习关 `goal=FreeRoam`；计时关先取源回合控制器的值，`roundFromPlacement` 再用 placement 文档覆盖，最后是清单 `round` 块覆盖。`allowAbort` 取清单；`stars` 取清单（自由练习关恒无星）；星级阈值数组全部填成结果卡自己的单关值。
9. 写 `LevelSwitch`（`LevelSwitchHost`，根物体）的并行数组；跑 `HubAssembler.Run`（多关模式）：N 块垫（顺序 = `hubs[].levels`）、启动器（含 `levelSwitch`）、房间宿主、**每关一颗返回按钮 `HubReturnButton_<id>`**（站在入口标记旁，没有入口则站在该关起点旁；底板里原来的根 `HubReturnButton` 改名成第一关的）、结果卡返回按钮、控制器按关数组与结果卡按关数组。
10. `CopyAllToUdon`，再读回每个新字段的 Udon 变量表（缺 → PROBLEM，“保存→重开→再跑”）。
11. 自检（任何 PROBLEM 则**不保存**）：单例各恰好 1、驱动/设定器 0；所有 `LevelArenaHost` 都在某关的列表里；`itemIds` 跨宿主唯一；每关入口距起点 ≤ 启动器 `entryTrustMeters`（12 m）；岛包围盒两两（含大厅房间）≥ 1000 m、坐标绝对值 ≤ 1300 m；各关最低地面/kill plane 高于场景重生高度 + 1 m；房间宽度够垫子（`HubAssembler`）；起点下有无碰撞体（只是 `warn:` 行）。
12. 通过则只保存输出场景。

报告：每行以 `[allmaps] ` 开头，不含时间、机器名、对象 id，数字用固定小数格式，所以单元不变的两次运行逐字相同。问题行是 `[allmaps] PROBLEM: …`，末行 `[allmaps] result: +N ~M problems=P saved <路径>|not saved`。

### 6.2 `MoveGameObjectToScene` 与 UdonSharp 的已知未知

默认用 `MoveGameObjectToScene`：同一批对象（含 U# 代理 + backing `UdonBehaviour` 一对）整体换场景，搬运集合内部引用不变，只有指回源场景的引用要重映射。**这条路没有在 UdonSharp 的 backing 上实测过。** 若搬后某个行为的变量为空或 `CopyProxyToUdon` 报错：勾选“Use Instantiate Fallback”重跑。后备路径：对每个 takeRoot `Object.Instantiate(…, 外壳, true)`，删掉克隆里的 `leaveBehind` 子树，沿两棵层级同步遍历建“原件→克隆”表，把所有仍指向原件的引用改指克隆、仍指向源场景的按路径重映射，之后的步骤相同。底板即源时（野餐关）不用后备路径（对象本来就在输出场景里）。

## 7. Unity 操作员顺序

1. 工作区里所有场景无未保存修改；编译通过；`LevelSwitchHost` 与（若有改动的）程序资产 `.asset`/`.meta` 就位（见 `Labs/README.md` 必做序列第 1 步）。
2. `Inspect All-Maps Sources`：应 `problems=0`；确认每关 `take`、宿主、“leaves the taken set”里只有指向底板基础设施的路径（`PlayerBall/MotorHost`、`PlayerBall/BallPhysicsRoot/BallVisualRollRoot/AttachmentRoot` 之类）。
3. `Assemble All-Maps Hub`：应 `problems=0` 且末行 `saved Assets/_RefUnlicensed/Scenes/AllMapsHub.unity`；再跑一次，报告应与第一次逐字相同（保存到文本比对）。
4. 若出现 “lacks Udon variables” / “deferRegistration … does not have”：编译 → 保存相关源场景或底板 → 重开 → 再跑（Labs/README.md “两张表”）。
5. 进 Play 验收（设计稿 §8.1）。
