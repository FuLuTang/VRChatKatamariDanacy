# mapbuild — 灰盒作者数据 → Unity 载荷

把野餐草坪灰盒的**作者数据**转成 Unity `JsonUtility` 能直接反序列化的**扁平载荷**。

## 流水线规则（唯一正确方向）

```
作者手写                生成（本工具）                        消费
layout.json        →   unity_greybox.json        →  Unity 编辑器灰盒 Builder
prop_catalog.csv       （禁止手改，提交进仓库）          （[Serializable] DTO + TextAsset）
```

- `layout.json` / `prop_catalog.csv` 是**唯一真源**；改关卡只改这两个文件，然后重新生成。
- `unity_greybox.json` 是**生成物**：不手工编辑、不手工修字段，diff 里出现的改动必须来自重新生成。
- Unity 只读生成物，所以构建场景**不需要在 Windows 上装 Python**。

## 为什么需要这一步

`layout.json` 是给人读的：任意嵌套、成对坐标数组（`waypoints: [[x,z],…]`）、`null`、
字典键（`player_count_plans` 的 `"2"/"3"/"4"`）。`JsonUtility` 不认这些：字段名必须与
`[Serializable]` 类匹配，不支持字典，数组元素不能是裸数组。本工具负责拍平：

| 作者写法 | 生成写法 |
|---|---|
| `shape{type,min_x,max_x,min_z,max_z}` | `shape_type` + `min_x/max_x/min_z/max_z` 同层 |
| `position[x,y,z]` / `size_m[x,y,z]` | `position_x/position_y/position_z`、`size_x/size_y/size_z` |
| `waypoints[[x,z],…]` | `waypoint_count` + `waypoints_x[]` + `waypoints_z[]`（并行数组） |
| `definitions[{id,count_min,count_max}]` | `prop_ids[]` + `prop_counts_min[]` + `prop_counts_max[]` |
| `null`（无区域的结构） | 空字符串 `""` |
| 布尔 `true/false` | `int` 0/1 |
| 各种精度的小数 | 统一 3 位小数（米制），`-0.0` → `0.0` |

另外：记录按 id 排序（结构无 id，按 `definition_id` + 位置复合键）、键序固定、2 空格缩进、
末尾一个换行、UTF-8 无 BOM。**同一输入重复运行逐字节相同**，所以文件可以进 Git，`--check` 能查过期。

## 用法

```bash
python3 Tools/mapbuild/build_greybox.py             # 生成/覆盖 Docs/Design/Maps/PicnicLawn/unity_greybox.json
python3 Tools/mapbuild/build_greybox.py --check     # 过期退出 1（不写文件），打印差异摘要
python3 Tools/mapbuild/build_greybox.py --summary   # 额外打印各集合记录数
python3 Tools/mapbuild/build_greybox.py --feasibility   # 打印每个散布区的 2-D 放置容量表
python3 Tools/mapbuild/build_greybox.py --layout a.json --catalog a.csv --out out.json
python3 -m unittest discover -s Tools/mapbuild/tests
```

退出码：`0` 成功 / `1` `--check` 发现过期或文件缺失 / `2` 输入数据校验失败。

校验在生成前跑，错误信息带记录 id，例如 `structures[12] core_hedge: 未知区域 'x'`、
`plate: 目录 count_in_map=16 与布局实例数 15 不符（散布 14 + 可吸门 0）`。
检查项：区域矩形与面积、区域/路线/尺寸门/出生点/交分锚点的交叉引用、目录 id 与 `zone_ids`、
尺寸门类型与物件 role、散布的 count_min ≤ count_max、**目录数量 = 散布 + 可吸门 / 结构实例**，
以及 **2-D 放置可行性**（下面单独说）。
Unity 消费方不需要再重复这些检查。

## 2-D 放置可行性（作者数据的硬门槛）

消费方（`PicnicLawnGreyboxBuilder.BuildProps`）只有一个 per-region 标量 `min_spacing_m`：按
`prop_ids` 顺序逐件放置，每件均匀随机采样最多 256 次，与**区内任意已放件**中心距 < S 就重试；
全局间距失败后只和同名定义比间距，再失败才丢件。数据比这更密时，丢件/降级就发生在 Unity 侧——
2026-11 的灰盒就是这样（严格间距只摆下 383/448，作者却写了 448）。

所以本工具在生成前自己判可行性，超了**报错退出（exit 2）**，不再只给警告：

- **容量**取「消费者能摆到的」，不是「最密堆叠」：六方最密上限是 `A / ((√3/2)·S²)`（谁也超不过），
  但 256 次均匀拒绝采样等价于随机顺序吸附（RSA），2-D 圆盘饱和覆盖率 0.547，即每点平均
  `π/(4·0.547)·S² ≈ 1.436·S²`，只有最密的 ~60%。**校准**：78 m² @ 0.9 m 的毯子，公式给 67 件，
  Unity 构建器实测也是 67 件。
- **脚印**：每件按 `game_longest_dim_m` 当方形脚印，必须整块落在区域内（每边 0.05 m）；
  `min_spacing_m` 还必须 ≥ 区内最大两件的 `(d₁+d₂)/2`，否则那两件会穿模。
- **余量**：用量 > 100% 报错；85–100% 只警告（`--feasibility` 表里标 `WARN`）。已提交的 PicnicLawn
  最高用量 82%。
- 模型、假设与逐区表见 `Docs/Design/Maps/PicnicLawn/README.md` §3.3；常量在
  `build_greybox.py` 顶部（`RSA_DISC_COVERAGE` 等），改常量前先看回归用例：
  `Tools/mapbuild/tests/test_build_greybox.py::FeasibilityTests`。

## 作者层校验：`validate_map.py`

`build_greybox.py` 的校验是**消费方契约**（生成载荷前必须满足）；`validate_map.py` 是**作者层**
校验（改数据的人先跑它）。两者的分工：

| 工具 | 管什么 | 触发时机 |
|---|---|---|
| `build_greybox.py` | 生成前：字段形状、目录计数、2-D 放置可行性 | 生成/`--check` 时 |
| `validate_map.py` | 作者层：几何、引用、出生→交分路程、路线余量、档位与球包线、容量报告 | 改完 `layout.json`/`prop_catalog.csv` 就手动跑 |
| 每图一份的专用自检 | **已退役并删除（2026-11-14）**：只认单图、逻辑重复实现 | — |

```bash
python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/PicnicLawn        # 目录或 layout.json 都行
python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/ToyRoom -v        # -v 附带逐区容量表
python3 Tools/mapbuild/validate_map.py <dir> --json                       # 机器可读摘要
python3 -m unittest discover -s Tools/mapbuild/tests                      # 两张图参数化回归
```

退出码：`0` 通过（允许没有 ERROR 的 warning）/ `1` 有 ERROR / `2` 读不到或解析不了输入。

它**不重复实现**容量模型：`min_spacing_m` 能否摆下由 `build_greybox.scatter_feasibility()`
判定（RSA 0.547 覆盖率、脚印下限、消费方上限），`validate_map.py` 只调用它并把数字整理成表。
检查项见该文件头注释的 13 组；额外规则三条值得单独记：**窄于最大球径的咽喉必须标
`narrow_small_ball_lane` 且不能落在任何散布区内**（否则小球会在缝里吃到东西长大、把「能进的缝」
变成「出不去」），**`climb` 门槛必须落在抬高地形上**（`ground_y_m > 0`），以及下面这条
**立面分类**。

### 立面分类：`drivable_grade` / `climb_step` / `obstacle`（layout `schema_version: 2`）

`scale.max_traversable_slope_percent` 的本意是「球能开上每一个**坡**」。台阶（ledge）不是坡：
能不能上去由**球径**决定（原作也按球的大小判爬坡）。两者混成一个上限，就只能砍掉台阶或者偷偷超限。
所以每个 rise > `scale.max_route_step_m` 的 `structures[]` 面必须声明 `traversability`：

| `class` | 判据（全部由数据现算，不读文字） | 负例（`tests/test_validate_map.py`） |
|---|---|---|
| `drivable_grade` | 坡度 = rise ÷ run ≤ `max_traversable_slope_percent`；声明的 `slope_percent` 与现算值不符即错误 | 把毯缘 `run_m` 改小 → 超 12% |
| `climb_step` | `step_height_m` = 几何高度，且落在 [0.25 × 阻挡球径, 0.25 × 开启球径] 内 | 台面压到 0.18 m（Lead 已否决的降级）→ 门挡不住 |
| `obstacle` | 不可走；不得再声明 `run_m`/`step_height_m`/`slope_percent` | 给树篱写坡度 → 自相矛盾 |

- **rise 的算法**：`position_y + size_m[1] / 2`（本 schema 的约定是物体坐在 y=0 的地面上）。
  作者不能靠写一个好看的 `slope_percent` 把陡坡说成缓坡。
- **v1 的图不用改**：`schema_version < 2` 时跳过「未分类立面」这条，`climb` 台阶的高度由门对应的
  结构几何推出（ToyRoom 就是这样被检查的，数值不变）。要升级时把 `schema_version` 改成 2 并逐面补
  `traversability`——升版就是同意让校验器管这件事。
- 报告里会打印**立面审计表**（surface / class / rise / run / slope / 判定）与障碍汇总；
  `--json` 里对应 `surfaces` 与 `obstacles` 两个键。
- 常量：`DEFAULT_STEP_HEIGHT_OVER_DIAMETER = 0.25`（图里没写 `scale.max_step_height_over_ball_diameter`
  时用这个默认值）、`TRAVERSABILITY_SCHEMA_VERSION = 2`。改常量前先看 `TraversabilityTests`。

### 为什么不再有「每图一份的专用校验器」

野餐草坪目录里曾有一份 419 行的专用自检（文件名即「校验 + 布局」），**只认 PicnicLawn**
（硬编码路径、边界外白名单、单图常量），是同一套逻辑的第二份实现。它已于 2026-11-14 随通用
校验器落地而**删除**（提交信息即退役记录）——

**逐条覆盖对照**（59 条断言 → `validate_map.py` 的行号；「更强」= 旧版是 warning、新版是 error）：

| 旧专用校验器的检查 | 新 `validate_map.py` |
|---|---|
| 目录必填列、空 id / 重复 id、size_tier/priority/role 枚举、count/value 非负 | 421–467 |
| `schema_version`、顶层键齐全 | 276–284 |
| 区域：重复 id、只认 rect、矩形退化、面积与矩形一致、在边界内、`tier_band` 长度与档位、`name_zh`/`purpose` 非空 | 346–396 |
| 区域：两两不重叠、面积和 ≤ 地图面积 | 405–415（重叠支持 `zone_overlap_exemptions` 显式豁免） |
| 结构：目录 id、`zone_id`、边界内外（旧版是 `OUTSIDE_OK` 白名单） | 473–509（**换成地图无关的两条规则**：有归属区域必须在场内；无归属且 `role=absorbable` 在场外=错误；固定装饰放行并记账） |
| 出生点：重复 id、未知区域、在场内、在自己区域内、`yaw`、`y` 与地面差 | 514–543（y 偏差 warning → **更强** error） |
| 出生点：至少 2 条出路 | 1176–1180 |
| 交分台：在场内/在区域内、半径>0、`approach_route_ids`/`entrances` 引用、圆盘不重叠、`rotation.order` 排列 | 547–591 |
| 尺寸门：类型、目录 id、在场内、`block<open`、`open>max`、`climb→fixed`/`absorb→absorbable`、`opens_path` | 685–762（`open>max` 与 `climb→fixed` warning → **更强** error） |
| 散布：未知区域/重复记录、`definitions` 非空、`min_spacing>0`、条目 id/role/count 关系 | 941–980 |
| 目录数量 = 散布 + 可吸门 / 结构实例 | 1025–1040 |
| 路线：≥2 点、点形状、点在边界内、宽>0、净宽 ≥ 最大球径、余量不足警告、`gate/bank/spawn` 引用、`closed` 末点重复警告 | 630–680 + 758–761 |
| 咽喉：净宽 < 最大球径且未标 `narrow_small_ball_lane`、`gate_id` 引用 | 1054–1080 |
| 道具：类型/区域/在场内/距出生点 8 m | 1096–1119 |
| 目录：有实例却无人引用、count=0 却有引用、`zone_ids` 与实际落点一致 | 1035–1049 |

**新版多出来的 13 类**（旧版完全没有）：区域重叠豁免机制（强制写理由）；`surface` 非空；
出生→交分设计路程（四角互差 >1 m 直接报错）；窄道不得落在散布区内；`climb` 门必须落在
`ground_y_m > 0` 的区域；门槛必须靠近档位边界（±0.20 m）；每个档位至少被一条区域覆盖；
`length_m` 与路点折线自洽（闭合环计入收尾段）；每件可卷物的拾取门槛 ≤ 最大球径；
`performance_budget` 与目录数字一致；重生候选/人数方案/`scale` 常量一致；散布区
`tier_band` 必须落在所属区域的档位范围内；**立面分类**（可走坡的坡度现算且 ≤ 上限、爬升台阶与
`climb` 门阈值互锁、不许有未分类的高差）。

退役时**只补不删**：对照过程中发现旧版检查、新版没查的两项，已补进 `validate_map.py`——
(1) 区域 `purpose` 非空；(2) 「可卷物件不许放在场外」（旧版靠 `OUTSIDE_OK` 白名单挡，
新版改成与地图无关的规则）。

## 加一张新地图

1. 在 `Docs/Design/Maps/<MapName>/` 放 `layout.json` + `prop_catalog.csv`，先跑通用校验器
   `python3 Tools/mapbuild/validate_map.py Docs/Design/Maps/<MapName>`（结构/引用/几何/容量；
   比本工具的生成前校验更早、更可读）。**不要**再给新图写一份专用校验器。
2. `python3 Tools/mapbuild/build_greybox.py --feasibility --layout <…>/layout.json --catalog <…>/prop_catalog.csv --out <…>/unity_greybox.json`，
   先看容量表再生成：凡是 `FAIL` 行都得先改件数或 `min_spacing_m`。
3. 提交生成物，并在 CI/自检里跑 `--check`。
4. 常量 `DEFAULT_*` 与 `Tools/mapbuild/tests/test_build_greybox.py` 里的 21/60/4/… 是
   PicnicLawn 的回归基线；新增地图时**不要**把这些数字改成别的图。通用校验器那一侧已经是
   参数化的（`tests/test_validate_map.py` 会遍历 `Docs/Design/Maps/*/layout.json`，新图放进去
   就自动纳入回归）；生成器这一侧要给新图加独立测试模块，或把断言参数化。

## 当前未输出的数据（需要时再加，不要手工补进生成物）

`powerups`、`respawn`、`player_count_plans`、`performance_budget`、`coordinate_system`；
以及 `scale.note`、`bank.rules` 里的纯说明文字。它们不影响灰盒几何构建；
要进 Unity 时按上表拍平并加进 `TOP_LEVEL_KEYS` + 一个测试。
