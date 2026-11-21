# 原作放置文件格式与蟹座 B 型计数（初代 REROLL、WLK REROLL+）

**日期：2026-11-21。** 本文记录三条已查清的结论：蟹座过关计数、初代 REROLL 的放置文件、WLK REROLL+ 的放置文件。所有来源都是本地分析笔记（未入库，原作资产与脚本不进仓库），所以每条只给结论、分母和复现命令。采用「原作优先」时，数据格式以本文为准；我方文档格式见 [LEVEL_FORMAT_V2](../../Design/Levels/LEVEL_FORMAT_V2.md)。

## 1. 蟹座 B 型过关计数

**结论：** Make Cancer（任务 11）过关类型为 2（B 型），名号白名单 `[480, 481, 482, 483, 484, 485]`，即六种蟹，共 133 只。吸到名号在表里的物件，B 计数 +1，没有配对条件。配对链属于任务 18（Make Gemini，类型 2、白名单指针为 0）。

- 依据：原生 DLL `PS2KatamariSimulation.dll` 的任务表（`PTR_DAT_18005f7a0`，每任务一个指针；记录 `+0x40` 为过关类型，`+0x48` 为名号白名单，以 `-1` 结尾），读了任务 0–25 共 26 个。`FUN_180028ef0` 第 40 行的 `sVar2 == 0x12` 是任务 18，不是任务 11；早先把它当蟹座是错的。
- 其余类型 2 的任务：12 `[1055,1056]`、14 `[1060,366,406,407]`、15（16 个名号）、16（23 个名号）。
- 配对表 `DAT_18019bbe0` 只在做双子座时才需要解，蟹座不需要。
- 来源：本地分析（未入库）`.work/notes/research/native_followups.md` 「Lead 更正（2026-11-21）」。复现：Windows 上用 `D:\VRChatKatamariDanacy\.work\unity\wl.py` 读 DLL 任务表。

## 2. 初代 REROLL（2019.01.31 PC）放置文件 `.mono.bytes`

- 任务 TextAsset `M_11.mono`（蟹座）、`V_00..04.mono`（对战）；小端，每条记录固定 `0x40` 字节，首个 `u16 = 0xffff` 为终止；5 个加载区域各有一块，区域 3/4 在样本中为空。
- 字段：`u16` 类型名号；`+0x10/14/18/1c` 位置 float32（第四槽 W 不当坐标）；`+0x20..2c` 轴 X/Y/Z + 角（弧度），不是四元数；`+0x30..3c` 缩放。另有运动号、命中区域号、父挂接等字节字段（偏移表见来源）。
- **位置换算：`p_ref = -p_file / 100`**（米，无轴置换、无平移）。
- 蟹座对账（分母 524 条独立蟹座表）：类型频次 524/524 一致；位置 523/524 在 1 mm 内，最大残差 0.499 mm，唯一超阈值的是 `sib=23` 类型 755（约 50 mm）。社区 dump 的 523 行里，>1 mm 的 102 条中 101 条有父挂接或 `s16MonoMoveTypeNo != -1`（社区坐标是动态位置，不强制对齐）。类型 `1695`（Lalala）只在二进制里出现，社区表没有。
- 对战图 V_00–V_04：实例 453/446/335/306/365 条（共 1,905），全部 `LocPosType=0`。每图都是 A→B→C 三个场地，开局在 A，球径到 20 cm、40 cm 时依次切换，开局球径 5 cm；三套场地环半径约 3.32 / 5.76 / 9.21 m。切换由 `Player.sAreaChange_VS` 调用，开局只激活区域 0。
- 来源：本地分析（未入库）`.work/notes/research/mono_format.md`。复现：仓库外 `~/KatamariRef/tools/parse_reroll_mono.py`（解析）、`reconcile_reroll_mono.py`（对账），在 Windows 读原版 `mission` 包。

## 3. WLK REROLL+（v20230608）放置文件

- 放置在 `LocData`（`Assets/ResourceData/ScriptableObject/Loc/*.asset`）的 `param.haitiData.Value`，元素 `Game.MONOCTRLTYPE`：`MonoID`（-1 = 随机槽）、`ParentID`（-1 = 根）、`PosType`、`u8VSGroup`、`f32Pos[3]`、`f32Rot[4]`、`f32Scal[4]`。
- **换算：`p_Unity = -(X,Y,Z)/100`；旋转 `q = (-qx,-qy,-qz,qw)`；根缩放 `s = s_file/100`。**
- 对账（教程关）：`tutorial-1` 的 318 个 LocData 槽与场景 `MissionPropsManager` 的 318 个引用逐件对上 318/318，位置误差 < 0.00001 m；旋转 193/193、缩放 308/308 通过。
- 三张对战图（每图只有一个区域，不切场地）：

  | 图 | 场地（`loadMapDataName`） | 放置槽 | 固定／随机 |
  |---|---|---|---|
  | vs_1 | `16_ROOM_d` | 730 | 557／173 |
  | vs_2 | `03_C_VS_d` | 885 | 832／53 |
  | vs_3 | `05_E_d` | 905 | 905／0 |

  固定槽换算坐标与场景对象在 1 mm 内：557/557、832/832、905/905。随机槽在场景里是原点占位，最终类型未查到。
- vs_2 地形用 PC 碰撞网格 `03_C_VS_MAP_HIT_BASE`（3,169 面、9,507 顶点）；按毫米焊接后顶点 1,760，丢弃 64 个焊接后退化面与 17 个同向共边面，得到 3,087 个三角形。运行时 99 个碰撞对象（734 面）是它的子集，其中 677 面逐顶点相同。网格里有 y≈30 m 的朝下平面（194 面），是否参与球碰撞未查到。
- 已知坑：
  1. 导出物件法线时 `n @ inv(M).T` 是错的；`n` 是行向量，应写 `n @ inv(M)`（等价于 `inv(M).T @ n`）。错的写法在 M 为纯旋转时无影响，节点链里有 −90° 绕 X 的子节点时法线整体转错，现象是受光面发黑。修复后三角面「法线与缠绕」负点积比例由 37.64% 降到 0.04%（分母 13,737 三角）。
  2. 视觉网格的材质 id 不能按出现顺序编号：加入背景部件后编号整体错位，路面变成窗户贴图。应按贴图名稳定命名（`m_<贴图名>`，部件用 `wlk_<名>`）。
  3. Unity 导入器 `EnsureSplitSurface` 只增改不删：改了部件或材质 id 后，要先手动删掉 `TerrainVisual` 下旧的全部 `wlk_` 子对象和旧材质，再重新导入。
- 来源：本地分析（未入库）`.work/notes/research/wlk_vs_placement.md`。复现：仓库外 `~/KatamariRef/tools/parse_wlk_locdata.py`（解析）、`adapt_wlk_vs2.py`（适配）、`export_wlk_mono_glb.py`（模型）；校验 `python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/_ref_wlk_vs2/placement.v2.json`（笔记记录为 PASS，0 error、1 warning）。
