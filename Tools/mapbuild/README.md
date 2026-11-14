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
python3 Tools/mapbuild/build_greybox.py --layout a.json --catalog a.csv --out out.json
python3 -m unittest discover -s Tools/mapbuild/tests
```

退出码：`0` 成功 / `1` `--check` 发现过期或文件缺失 / `2` 输入数据校验失败。

校验在生成前跑，错误信息带记录 id，例如 `structures[12] core_hedge: 未知区域 'x'`、
`plate: 目录 count_in_map=16 与布局实例数 15 不符（散布 14 + 可吸门 0）`。
检查项：区域矩形与面积、区域/路线/尺寸门/出生点/交分锚点的交叉引用、目录 id 与 `zone_ids`、
尺寸门类型与物件 role、散布的 count_min ≤ count_max、以及**目录数量 = 散布 + 可吸门 / 结构实例**。
Unity 消费方不需要再重复这些检查。

## 加一张新地图

1. 在 `Docs/Design/Maps/<MapName>/` 放 `layout.json` + `prop_catalog.csv`，先用该目录的
   `validate_layout.py` 自检（本工具的校验是它的子集 + 拍平）。
2. `python3 Tools/mapbuild/build_greybox.py --layout <…>/layout.json --catalog <…>/prop_catalog.csv --out <…>/unity_greybox.json`。
3. 提交生成物，并在 CI/自检里跑 `--check`。
4. 常量 `DEFAULT_*` 与 `Tools/mapbuild/tests/` 里的 21/60/4/… 是 PicnicLawn 的回归基线；
   新增地图时**不要**把这些数字改成别的图，应该给新图加一个测试模块（或把断言参数化）。

## 当前未输出的数据（需要时再加，不要手工补进生成物）

`powerups`、`respawn`、`player_count_plans`、`performance_budget`、`coordinate_system`；
以及 `scale.note`、`bank.rules` 里的纯说明文字。它们不影响灰盒几何构建；
要进 Unity 时按上表拍平并加进 `TOP_LEVEL_KEYS` + 一个测试。
