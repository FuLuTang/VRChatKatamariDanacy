# 关卡搭图工具（Level Authoring）：Blender 当编辑器，Unity 只做一次单向导入

> 状态：`PROVISIONAL_MEASURED_ON_LINUX`，2026-11-15。
> 本文是**设计 + 一次本机可行性实测**。**没有写正式工具代码、没有碰 Unity 编辑器、没有跑 git 写命令**；Unity 侧全部是设计，不是已验证的实现。
> 实测只证明两件事：**本机（Linux aarch64、2 核、无 GPU）能跑 Blender 并出图**，以及**「Blender 摆位 → placement.json → 从 JSON 重建场景 → 出图」这条回路在无头环境下成立**。实测产物在 `~/tools/level_authoring_probe/`，不进仓库。
> 触发：用户 2026-11-15 的要求原话「最主要是顺手，且最好能在你本机跑……方便搭场景，在不同位置出渲染图。最主要是能导入 vrchat sdk 的 unity」；以及 [PICNIC_CORNER_LEVEL_ONE](../StoryMode/PICNIC_CORNER_LEVEL_ONE.md) §2.4 查实的缺口——**第一关 110 件的摆放没有任何数据文件，只活在场景里**（最大回归风险）。
> 引用约定：`Runtime/…`、`Editor/…`、`Labs/…` 相对 `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/`；`Docs/…`、`Tools/…` 相对仓库根。**按符号名引用，不写行号**（这几个文件本轮都在被并行修改）。

---

## 0. 六条结论（先看这个）

| # | 问题 | 结论 |
|---|---|---|
| 1 | 本机能不能跑 Blender | **能，但不是官方包**。Blender 官方只发 `linux-x64`；本机是 Ubuntu 24.04 aarch64，用发行版的 `blender 4.0.2 arm64` 拆包到 `~/tools`（无 root）可用，启动 0.58 s（§1.1–1.2） |
| 2 | 本机能不能出图 | **能，三种引擎都能，无 GPU**。1280×720：Workbench 5–10 s / EEVEE 35–49 s / Cycles CPU 48 spp 104 s；峰值内存 0.29–0.74 GB（§1.3） |
| 3 | 推荐方向 | **Blender 当编辑器、`placement.json` 当真源、Unity 只有一个受管子树的导入器**（§3）。用户提的方向成立，但「Blender 工程文件」**不是**真源——真源是 JSON |
| 4 | 最关键的取舍 | **人是编辑器、代理是脚本、机器是渲染农场**：Windows 上的 Blender 5.3.0 给人用（真 3D 拖拽），本机 Blender 4.0.2 headless 给代理用（摆位/校验/出图），两边跑**同一套脚本**（§5） |
| 5 | 最大的技术风险 | **不是渲染，是坐标与预算**：Unity `(x,y,z,w)` 四元数 vs mathutils `(w,x,y,z)`、`matrix_world` 不刷新、以及 motor 的「≤8 平面 + ≤8 盒 + ≤4 斜面」硬容量——三者都会**静默**出错。对策是「单一转换点 + 校验器把预算当硬错误」（§4.6、§8） |
| 6 | 第一阶段交付 | 第一关一角的 `placement.json`（110 件/412 u）+ 负向校验器 + 一张俯视图一张 3/4 图 + 一次受管导入；验收判据在 §11.2 |

**一句话推荐**：`placement.json` 是唯一真源；Blender 是它的**编辑器**（人手动、代理脚本），Unity 是它的**消费者**（单向、幂等、只碰 `LevelContent` 根）。三者之间只有 JSON 一个接口，所以每一份逻辑只有一份实现。

---

## 1. 可行性实测：本机装 Blender 并出图

### 1.1 装在哪：官方没有 arm64，走发行版拆包

| 候选 | 实测结果 | 代价 |
|---|---|---|
| **官方 tarball**（download.blender.org） | ✗。`Blender4.2/` 与 `Blender4.5/` 目录列表**只有 `blender-4.x.y-linux-x64.tar.xz`**，没有任何 arm64 文件 | 官方路线在本机不存在 |
| **snap** | ✗。`snap` 存在，但 store 的 `blender` 只发 amd64 | 无 arm64 通道 |
| **flatpak** | ✗。本机没装 flatpak；Flathub 的 Blender 同样只发 x86_64 | 要额外装 flatpak 且仍无 arm64 |
| **发行版包（推荐）** | ✓。Ubuntu 24.04 `noble/universe` 有 `blender 4.0.2+dfsg-1ubuntu8` **arm64**（ports 源）。`sudo` 需密码、本会话拿不到 root，于是 `apt-get download` + `dpkg -x` 解到 `~/tools`，用 `LD_LIBRARY_PATH` 喂依赖 | 66 个 deb、`~/tools` 共 **742 MB**（其中 122 MB 是 deb 缓存，可删）；**没有 OpenImageDenoise**（发行版构建没编进去） |
| **自己编** | 未试 | 2 核 / 11 GB 上编 Blender 是小时级，且要装整套编译依赖；不划算 |

安装形态（无 root，全部在 `~/.` 下）：

```
~/tools/blender-4.0.2/        # dpkg -x 出来的 /usr 树（175 MB），blender 4.0.2
~/tools/blender-libs/         # 运行时 so（423 MB），来自 40+ 个 lib*.deb
~/tools/blender-py/           # python3-numpy（23 MB）
~/tools/blender-debs/         # 原始 deb 缓存（122 MB，可删）
~/tools/blender-env.sh        # 环境变量 + 启动参数，唯一入口
```

`blender-env.sh` 的四条必备变量（缺一条就会「能启动、不能干活」）：

```sh
export BLENDER_ROOT="$HOME/tools/blender-4.0.2"
export LD_LIBRARY_PATH="$HOME/tools/blender-libs/usr/lib/aarch64-linux-gnu:$HOME/tools/blender-libs/usr/lib"
export BLENDER_SYSTEM_SCRIPTS="$BLENDER_ROOT/usr/share/blender/scripts"
export BLENDER_SYSTEM_DATAFILES="$BLENDER_ROOT/usr/share/blender/datafiles"
export PYTHONPATH="$HOME/tools/blender-py/usr/lib/python3/dist-packages"   # numpy
export BLENDER="$BLENDER_ROOT/usr/bin/blender"
export BLENDER_FLAGS="--background --factory-startup --disable-autoexec --python-use-system-env --python-exit-code 2"
```

### 1.2 启动与依赖：缺什么、怎么补、报什么错

`ldd` 逐层追，一共缺 **24 个 soname**（OpenColorIO / OpenImageIO / jemalloc / OpenSubdiv / potrace / pugixml / spnav / TBB，再往下带出 DCMTK→GDAL→GEOS/PROJ/HDF5 那一大串）。全部按 soname 反查包名后 `apt-get download` + `dpkg -x` 补齐，**没有 `sudo`、没有动系统目录**。

四个「装上了但跑不起来」的坑（都已解决，写下来给下一个人）：

| 症状 | 原因 | 对策 |
|---|---|---|
| `No module named 'bpy_types'` / `'bpy'`，随后 `terminate called after throwing std::logic_error` | 拆包后二进制找不到 `scripts/modules`（它按编译期 `/usr/share/blender` 找，回落到相对路径失败） | 设 `BLENDER_SYSTEM_SCRIPTS` / `BLENDER_SYSTEM_DATAFILES` |
| `Color management: scene view "AgX" not found` + 渲染发灰 | 同上，`datafiles/colormanagement/config.ocio` 没被找到 | 同一个变量解决；修好后不再打印 |
| `ModuleNotFoundError: No module named 'numpy'`（glTF 导入、Cycles 都要） | 发行版 Blender 用系统 `dist-packages`，不自带 numpy | 装 `python3-numpy` deb 到 `~/tools/blender-py` + `PYTHONPATH` + `--python-use-system-env` |
| `RuntimeError: Error: Build without OpenImageDenoiser` | 发行版构建没编 OIDN | Cycles 关降噪（`use_denoising=False`），靠提采样；要降噪就去 Windows 的 5.3.0 跑 |

**`--version` 实测：0.58 s**（冷启动到打印版本）。

### 1.3 三个引擎都能在无 GPU 无头下出图（实测表）

测试场景：10 件已有道具（apple / cup / banana / plate / bowl / cookie / watermelon / toy_ball / thermos / soda_can，全部来自 `Docs/Art/OriginalModels/props/exports/*.glb`）+ 一块地面 + 两盏灯 + 一台相机，1280×720。

| 引擎 | 耗时 | 峰值 RSS | 观感 | 判定 |
|---|---:|---:|---|---|
| **Workbench** | **7.9 s** | 501 MB | 纯色 + 工作室光，有接触阴影但**材质色要走 `material.diffuse_color`**：实测那块只设了 Principled Base Color 的地面渲染成灰色，而 GLB 道具（glTF 导入器同时写了 viewport 颜色）有颜色；无景深、无反射 | ✓ 可用，**迭代时用**（俯视图尤其好：5–10 s/张） |
| **EEVEE** | **48.0 s** | 737 MB | 有软阴影；4.0 **仍有** `eevee.use_gtao`（实测 `hasattr === True`），4.2+ 才移除，所以 AO 在 4.0 上可开；和 Unity 里的观感最接近 | ✓ 可用，**默认出图用它** |
| **Cycles CPU** | **104.3 s**（48 spp，**无降噪**） | 290 MB | 最好，阴影最实；无降噪时有可见噪点，提采样会线性变贵 | ✓ 可用，**定妆图用它**（一张 2 分钟） |

无 GPU 是怎么出图的：`/dev/dri` 存在（virtio-gpu）但当前用户不在 `video`/`render` 组，所以 EGL 打不开硬件设备：

```
libEGL warning: failed to open /dev/dri/renderD128: Permission denied
Managed to successfully fallback to surfaceless EGL rendering!
EGL Error (0x3009): EGL_BAD_MATCH: ...
```

**这些警告无害**——Blender 自己回落到 llvmpipe 软件 GL 并渲染成功（EEVEE/Workbench 都拿到了非空 PNG）。不要因为看到 `EGL Error` 就以为失败，判据是「有没有写出 PNG + 我眼睛看过」。

### 1.4 回路实测：摆位 → JSON → 从 JSON 重建 → 出图

这一段是**方向可行的证据**，不是工具代码：脚本 `author_test.py`（库导入 + 摆位 + 导出）与 `render_test.py`（**只读 JSON** 重建场景 + 按预设出图）各一个文件，都在 `~/tools/level_authoring_probe/`。

| 测量项 | 读数 |
|---|---|
| 26 个 def 的 GLB 逐个导入（每 def 一个集合） | **0.28–0.44 s** |
| 44 件道具摆位（含桌面堆叠、斜板、环状散布） | **< 0.01 s** |
| 导出 `placement.json`（44 件 + 4 结构 + 3 相机，2 万字节） | **< 2 ms** |
| 从 JSON 重建（26 defs + 44 实例 + 4 个盒体结构） | **0.28 s** |
| 渲染 3 张 1280×720（EVEE×2 + Workbench×1） | **81 s** |
| 132 件（≈第一关 110 件的 1.2 倍）+ 俯视图 Workbench | **总 7.4 s**，430 MB |
| 坐标/旋转 round-trip（JSON 数值 → 回 Blender → 对比原矩阵） | **44/44 精确**（位置误差 ≤ 1e-4 m） |
| 道具 AABB 直接来自模型（`apple` 0.08×0.078×0.0789，`cup` 0.075×0.075×0.09，`plate` 0.24×0.24×0.025） | 与 `manifest.json` 的 `dimensions_m` 一致，**不需要手抄尺寸** |
| 复用现有道具流水线的引擎探测：`prop_lib.select_eevee_engine(scene)` | **在本机 4.0.2 上可用**，选中 `BLENDER_EEVEE`，`hasattr(scene.eevee,'use_gtao') == True`。这条很关键：它在 4.2–4.5 会选 `BLENDER_EEVEE_NEXT`、在 5.x 同 4.2+，所以**同一套渲染脚本能在本机 4.0.2 与用户的 5.3.0 上跑**（§6） |

结论：**摆位与出图在这个尺度上是零成本的**，真正的成本只在与 Unity 的交接（§7）和校验（§8）。

### 1.5 实测踩到的三个坑（都是设计输入，不是杂记）

1. **`matrix_world` 是懒的**：`bpy.data.objects.new()` + 设 `location/rotation` 之后立刻读 `matrix_world`，拿到的是**单位矩阵**（实测：导出 44 件的坐标全是原点）。必须 `bpy.context.view_layer.update()`。→ 设计上：**导出脚本最后一步永远是 `view_layer.update()` 再读矩阵**，且导出后做一次 round-trip 自检（§5.5）。
2. **库集合「藏起来」会把实例一起藏掉**：把道具库集合 link 进场景再 `hide_render=True`（想让它别出现在画面里），结果是**渲染全空**——集合实例继承 `hide_render`。正确做法是**库集合不 link 进场景**（未 link 的集合照样能当实例源）。→ 设计上：**库里只有集合，场景里只有实例**，库对象永不渲染也永不「被藏」。
3. **两套四元数顺序**：Unity 是 `(x, y, z, w)`，mathutils 构造是 `(w, x, y, z)`。第一次 round-trip 44/44 全部错成「绕 Z 180°」。→ 设计上：JSON **只写 `(x,y,z,w)` 并显式标 `quatOrder`**，两侧各有一个转换函数、各有一条自检（§4.6）。

顺带一条**不是坑但要知道**：Blender 是 Z-up 右手，Unity 是 Y-up 左手。本项目已有的轴约定（`props/README.md` §2）是 FBX `axis_forward='-Z'`、`axis_up='Y'`、Unity 侧 `Rotation=(0,0,0)`。本文沿用同一映射：**Unity = (B.x, B.z, B.y)**，它是一次纯轴交换（行列式 −1），因此旋转会**保角反号**（实测：Blender 绕 Y +30° → Unity 绕 Z −30°）。这条只写在一处（§4.6），别在别处再推一遍。

### 1.6 退路评估：真装不上怎么办

| 退路 | 何时用 | 代价 |
|---|---|---|
| **Windows 上跑（推荐退路）** | 本机 Blender 挂掉/升级破坏（例如 Ubuntu 26.04 不再打包 4.0.2） | 用户机已有 `D:\SteamLibrary\...\Blender\blender.exe` 5.3.0，headless 可用；产物 `placement.json` + PNG 用 `scp`/git 拉回。**出图从 45 s 变成本机 0 + 一次传输**，但依赖 Windows 在线，且同一时间 Unity 操作员可能正占着那台机器 |
| 只用本机校验 + Windows 出图 | 本机渲染太慢/太占 CPU（2 核，一张 EEVEE 45 s 会顶满两核） | 校验器是纯 Python，本机随时能跑；出图挪到夜里批跑 |
| 本机不装 Blender，全在 Unity 里看 | 临时救急 | 回到「数据只在场景里」的老问题，且代理要占 Unity（违反单人操作员约定） |

---

## 2. 替代方案对比（不实测，只评估）

| 方案 | 顺手程度 | 本机可跑 | 出图质量 | 导入 Unity 可靠性 | 维护成本 | 判定 |
|---|---|---|---|---|---|---|
| **A. Blender 当编辑器 + JSON**（推荐） | **高**：真 3D 拖拽、吸附、实例、按面/按轴对齐、脚本化批量；人用 GUI、代理用 headless，同一套脚本 | **是**（实测） | **中高**：EEVEE 45 s/张，Cycles 104 s/张，材质是 Principled 直通 | **高**：数据与场景解耦，导入器幂等、只碰受管根 | 中：一个 bpy 侧 + 一个 C# 导入器 + 一个纯 Python 校验器 | **选它** |
| B. 纯 Python + `pyrender`/`trimesh` | **低**：没有交互，摆位只能写数字/代码；「顺手」这一条直接不成立 | 是（pip 即可，无 GPU 也能 EGL） | **低**：pyrender 无软阴影、材质弱；trimesh 预览近似线框 | 中：得自己再造一套「def → 预制体」映射 | 低（但省下的钱花在「不好用」上） | 只适合**校验器 + 缩略图**，不适合当编辑器 |
| C. web 端 three.js 编辑器 | 中：能拖，但等于自己写一个编辑器（选择、 gizmo、撤销、资产库、序列化），而且 VRChat 侧仍要导入器 | 是（node/vite，但本机 2 核跑 dev server + 浏览器不在本机） | 中：PBR 可以，但没有 GI/软阴影调优 | 中 | **高**：编辑器本身就是产品 | 否 |
| D. 直接在 Unity 里搭 + 导出 | **最高**（所见即所得，就是最终画面） | **否**：Unity 只在 Windows，且**同一时间只有一个操作员**；代理一占就是几十分钟 | 最高 | 最高（不导入） | 低 | **作为最终验收与回退入口，不作为作者数据源** |

**为什么不是 D**：D 的「顺手」是真的，但它把三件事绑死了——① 数据只在场景里（正是当前 110 件无法 diff 的病根）；② 代理不能在你不用 Unity 的时候干活；③ 出图要开编辑器（一张图 = 一次 Unity 会话），而本机出图是 10–45 s 的纯 CPU 活儿。所以：**D 保留为「最终看画面」的入口，A 负责「产生可 diff 的数据 + 批量出图」**。两者不是竞争关系。

**为什么不是 B/C**：这两条都在「重新发明编辑器」。用户要的是「顺手」，而顺手的来源是**已有的 3D 交互**，不是又一套 web UI。B 的唯一价值（无头、可脚本）A 已经有了。

---

## 3. 推荐方案：一份数据、两个作者、一个消费者

```
                 ┌────────────────────────────── 真源（进 git、可 diff、可校验） ──────────────────────────────┐
                 │  Docs/Design/Levels/<level_id>/placement.json                                          │
                 └───────────▲──────────────────────────────┬───────────────────────────────▲──────────────┘
                             │ 导出（写）                    │ 校验（只读）                   │ 导入（只读）
        ┌────────────────────┴───────────┐      ┌───────────┴──────────┐        ┌───────────┴─────────────┐
        │ Blender（两个作者，同一套脚本）  │      │ 纯 Python 校验器      │        │ Unity 编辑器导入器        │
        │  · 人：Windows GUI + 5.3.0      │      │  Tools/levelbuild/    │        │  Editor/LevelContent/     │
        │  · 代理：本机 headless 4.0.2    │      │  · 几何/预算/价值      │        │  · 只写 LevelContent 根   │
        │  · 库 = GLB 实例，摆位 = 空物体  │      │  · Linux 随时可跑      │        │  · 幂等 diff              │
        └────────────────────┬───────────┘      └───────────────────────┘        └───────────┬─────────────┘
                             │ 同一脚本顺带出图                                                │
                             ▼                                                                ▼
                    PNG 预设（俯视/3/4/球视角）                                  场景里的受管子树 + PickupFieldSync
```

四条硬边界（这四条是这套工具「不会把项目搞坏」的全部理由）：

1. **真源是 JSON，不是 `.blend`**。`.blend` 是草稿（可以进 `Docs/Design/Levels/<id>/work/` 或者根本不进仓库）；`.blend` 里的手工改动**不会**自动变成数据，必须走导出。
2. **导入是单向的**。导入器从不反向写 JSON；在受管子树里手改的位移会在下次导入被覆盖（§7.2 有明确的「怎么让人知道」）。
3. **受管子树是硬边界**。导入器只创建/更新/删除 `LevelContent` 根之下、由它自己命名的节点；球、回合、HUD、Station、房间接线一律不碰（§7.1）。
4. **校验器是关卡的门**。`placement.json` 进仓库前必须过 `levelbuild check`；超过 motor 容量、穿地、悬空、越界、价值对不上都是**硬错误**，不是警告（§8）。

---

## 4. `placement.json` 草案

### 4.1 顶层

```json
{
  "schema": "katamari.placement/0",
  "level": { ... 关卡元数据（球起点、球径上限、边界、区域）... },
  "props": [ ... 每件可吸物 ... ],
  "structures": [ ... 地面/墙/台面/斜板（球的障碍与支撑）... ],
  "cameras": [ ... 相机预设（人可以改）... ],
  "renderPresets": [ ... 出图预设（引擎/分辨率/相机）... ]
}
```

### 4.2 `props[]`：每件可吸物

```json
{
  "itemId": "prop_plate_000",
  "def": "plate",
  "zone": "table",
  "position": [-0.5406, 0.4125, -1.1843],
  "rotationQuat": [0.0, 0.0, 0.0, 1.0],
  "support": "picnic_table",
  "aabb": [0.24, 0.24, 0.025],
  "absorbDiameterReq": 0.302,
  "absorbable": true,
  "valueUnits": 8,
  "notes": ""
}
```

| 字段 | 必填 | 含义 | 谁写 |
|---|---|---|---|
| `itemId` | ✓ | **稳定 id**，导入器靠它做幂等（增/改/删）；重命名 = 删一件 + 加一件 | 导出器（`prop_<def>_<序号>`，人可改） |
| `def` | ✓ | 目录 id（`prop_catalog.csv` 的行），决定「用哪个预制体/哪个 manifest」 | 导出器 |
| `zone` | ○ | 区域 id，与 `layout.json.zones[].id` 同名；用于分区统计与可达性 | 人/导出器 |
| `position` | ✓ | **Unity 世界坐标**，米，`y` 向上；`y` 是**枢轴**（道具枢轴在 AABB 中心），所以贴地 = `y = aabb.y/2` | 导出器 |
| `rotationQuat` | ✓ | **Unity 顺序 `(x,y,z,w)`**，绕枢轴 | 导出器 |
| `aabb` | ✓ | 世界轴对齐尺寸（未旋转的模型 AABB），**只作校验与预估**，真值仍在 manifest/预制体 | 导出器（读 manifest） |
| `absorbDiameterReq` | ✓ | `D_req = 2.673 · (a·b·c)^(1/3)`，来自 aabb；**同一个函数只在校验器里实现**（§8） | 校验器回填 |
| `absorbable` | ✓ | `D_req ≤ level.ballDiameterMax`；**派生**，写进文件只为「一眼看」 | 校验器回填 |
| `valueUnits` | ○ | 分数单位。**2026-11-15 Lead 拍板**：按 **def** 由关卡数据声明（`defs[].valueUnits`，`props[].valueUnits` 可逐件覆盖）；导入器把声明值传给 `PickupFieldSync` **一次写入并回读**（§7.4），不再由同步工具按最长边定价 | 关卡数据（`defs[]`）/ 逐件覆盖 |
| `support` / `stackedOn` | ○ | 「它踩在谁上面」——`support` 是结构 id，`stackedOn` 是另一件道具的 `itemId`。**引擎不读它**，校验器用它判「悬空/穿模」并把责任指到具体那一件 | 导出器 |

> `support`/`stackedOn` 是**关系**不是坐标：导入器只用 `position/rotationQuat` 摆东西。这样「逻辑」和「画面」不会互相打架；关系字段只喂校验器。

### 4.3 `structures[]`：地面 / 墙 / 台面 / 斜板

```json
{
  "id": "picnic_table",
  "kind": "table",
  "role": "box",
  "shape": "box",
  "center": [-0.30, 0.375, -1.10],
  "size": [1.60, 0.05, 0.80],
  "rotationQuat": [0.0, -0.069756, 0.0, 0.997564],
  "collision": "box",
  "absorbable": false,
  "support": true,
  "notes": "桌面；腿另外 4 个 box（或用一整个盒把桌下填实，见 §10.2）"
}
```

| `role` | 消费方（Unity） | 占哪个预算 | 规则 |
|---|---|---|---|
| `ground` | `LevelArenaHost` → `motor.SetGroundPlane(normal, offset)` | 1 张地面 | **恰好 1 条**；`shape` 只能是 `plane` |
| `boundary` | `LevelArenaHost` → `motor.AddPlane(normal, offset)` | ≤8 平面 | **用平面、不用盒**（否则墙顶变成可站的窄条，还吃掉盒预算） |
| `box` | `LevelArenaHost.staticBoxes[]`（`BoxCollider` → `Setup` 烘焙 min/max → `motor.AddBox`） | ≤8 盒 | **一次登记两用**：扫掠是墙、顶面是支撑 |
| `ramp` | `LevelArenaHost.rampBoxes[]` → `motor.AddRamp(...)` | ≤4 斜面 | **绝不能同时是 `box`**（否则坡底出现「隐形墙」）；坡度 > 45° 时拒绝并要求改成 `box`；**板的本地 +Y 面是行走面**（`support` 落座读这一面；P3 的 `AddRamp` 中心也必须取这一面，不是盒中心），4 cm 板体在它下面 |

`collision` 字段只管**玩家胶囊**（三层里的第二层）：`box` = 生成 `BoxCollider`（玩家能撞到），`none` = 纯视觉。`absorbable=false` 的结构**永远**不参与吸收门——吸收门只看道具的 `propBounds`，不读结构的盘口。

### 4.4 `cameras[]` / `renderPresets[]`

```json
"cameras": [
  {"id": "overview_3q", "type": "perspective", "location": [-3.2, 2.9, -4.4], "target": [-0.2, 0.15, -0.9], "lens": 45},
  {"id": "top",         "type": "ortho_top",   "location": [-0.2, 12.0, -0.9], "target": [-0.2, 0.0, -0.9], "coverage_m": 7.2},
  {"id": "ball_eye",    "type": "perspective", "location": [-0.2, 0.30, -4.1], "target": [-0.2, 0.28, -1.2], "lens": 30}
],
"renderPresets": [
  {"id": "overview",  "camera": "overview_3q", "engine": "EEVEE",     "res": [1280, 720]},
  {"id": "plan",      "camera": "top",         "engine": "WORKBENCH", "res": [1280, 720]},
  {"id": "ball_eye",  "camera": "ball_eye",    "engine": "EEVEE",     "res": [1280, 720]}
]
```

`type` 只有两种：`perspective`（location/target/lens）与 `ortho_top`（coverage_m）。`ortho_top` 的 `location` 由 `target` + 12 m 高度自动算，**人不用填**——但为了「人可以手改」，字段还是写全（导出器算好写进去）。

### 4.5 `level{}`：元数据与预算

```json
"level": {
  "id": "story_01_bench_size",
  "scene": "StoryLevel",
  "units": "m",
  "convention": "unity-y-up-left-handed-quat-xyzw",
  "ballStart": [-12.5, 0.15, -12.5],
  "playerSpawn": [-14.5, 0.50, -14.5],
  "ballDiameterStart": 0.30,
  "ballDiameterMax": 2.20,
  "bounds": [[-19.4, -19.4], [-6.0, -6.0]],
  "spawnClearanceM": 1.0,
  "budget": {"planes": 8, "boxes": 8, "ramps": 4, "propSupports": 6, "blocking": 6},
  "expected": {"propCount": 110, "valueUnits": 412},
  "zones": [{"id": "pocket", "rect": [-13.5, -14.5, -9.0, -11.0]}]
}
```

| 键 | 必填 | 含义 |
|---|---|---|
| `arena` | ○ | `{"id": "<格名>"}`：声明这份文档是**一格 arena**（多格测试场一次只注册一格）；`id` 必须等于所在目录名，Unity 导入器据此把几何放进 `LevelContent/<id>/` 并绑定该对象上的 `LevelArenaHost`；该格的 `box`/`ramp` 必须落在 `level.bounds` 内。 |

`arena` 只用于多格测试场的单格文档（见 [physics_test_field/README.md](../Levels/physics_test_field/README.md) 与 `placement_core._check_arena`）；单关文档不写。

`budget` 与 `expected` 是**从运行期契约抄下来的常量**（不是每家一套）：`planes/boxes/ramps` 来自 [TERRAIN_SUPPORT](../BallMotor/TERRAIN_SUPPORT.md) §3.2、§7.5 的容量与 `motor.AddPlane/AddBox/AddRamp` 的槽位；`expected` 来自 [PICNIC_CORNER_LEVEL_ONE](../StoryMode/PICNIC_CORNER_LEVEL_ONE.md) §3.4（110 件 / 412 u）。**校验器拿它当硬门**：第一关少一件就红，避免「重排时丢了一件没人发现」。

### 4.6 三条约定（这三条错了会静默出错）

1. **坐标**：JSON 一律是 **Unity 世界坐标**（左手、Y-up、米、绕 +Y 顺时针为正的 yaw 约定沿用 `layout.json.coordinate_system`）。Blender 侧只有一个转换点：

   ```python
   M  = Matrix(((1,0,0),(0,0,1),(0,1,0))).to_4x4()   # Unity = (B.x, B.z, B.y)
   Tu = M @ T_blender @ M.inverted()                 # 位置 + 旋转一起转
   ```
   实测：Blender 里 44 件的矩阵 → JSON → 回 Blender，**44/44 精确**。
2. **四元数**：写 `(x,y,z,w)`，读的时候 mathutils 要 `Quaternion([w, x, y, z])`。stdout 里加一行 `quatOrder=xyzw` 的横幅，是给未来的人看的。
3. **确定性**：`props[]` 按 `itemId` 排序；坐标/四元数 `round(…, 4/6)`；`-0.0 → 0.0`；2 空格缩进、末尾换行、UTF-8 无 BOM。这样 `git diff` 只显示真改动，`check --stale` 才有意义（与 `Tools/mapbuild` 的生成物规矩一致）。

---

## 5. Blender 侧怎么用

### 5.1 人（Windows，GUI）

1. 打开 `level_author.blend`（或从空场景起）：脚本自动建好**道具库**（26+ def 的 GLB 各一个未 link 集合）+ 一个 `Level` 集合。
2. 摆位：从库拖实例（`Add > Collection Instance`）；`G` 移动、`R` 旋转；脚本提供一个 **Snap 面板**：
   - `Seat on floor`：把选中实例的 `y` 设成 `aabb.y/2`（贴地，正是现在手工做的 `y=高/2`）；
   - `Seat on surface`：向下投射，落在最近的支撑面/桌面/斜板上，并按支撑面法线**摆正**；
   - `Tilt 15°/30°/45°`：绕自身 X/Y 倾斜（「木板斜搭上桌」的那块板用它）；
   - `Array`：沿一条线/一个环复制 N 件（散落环用它）；
   - `Group`：把选中件打包成一个集合实例（成组摆放、整组挪）。
3. 结构：`Add > Mesh > Cube` 之后 `Ctrl+Shift+L` 打角色标签（`ground/boundary/box/ramp`），或者用面板上的 4 个按钮。**斜面用 `ramp`，别再打 `box`**（§4.3）。
4. `Export placement.json` + `Render presets`：两个按钮，各 10–45 s。
5. 提交：`git add placement.json && git commit`。PNG 一般不进仓库（除非要当证据）。

> 人在 Blender 里改完**必须导出**，否则 JSON 没变——这条要在面板上写死一句话（「未导出的改动不会被导入」）。

### 5.2 代理（本机，headless）

```bash
source ~/tools/blender-env.sh
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/level_author.py -- \
    --level story_01_bench_size --from-json        # 可选：先把 JSON 铺成场景
"$BLENDER" $BLENDER_FLAGS --python Tools/levelbuild/blender/level_render.py -- \
    --level story_01_bench_size --preset plan,overview,ball_eye
python3 Tools/levelbuild/levelbuild.py check Docs/Design/Levels/story_01_bench_size/placement.json
```

代理的典型动作（都是「改 JSON 的某一个子集」）：批量平移一个 zone、按 AABB 重新贴地、把某件从 `stackedOn` 改成地面、把 5 件聚成一个环、**出图给人看**。**代理不需要 GUI，也不占 Unity。**

### 5.3 三个操作词（对应用户说的「叠放、斜搭、成组」）

| 词 | 数据形态 | 谁来保证对 |
|---|---|---|
| **seat（贴地/贴面）** | `position.y = 支撑面高 + aabb.y/2`（旋转过就用**世界 AABB** 的下沿） | 导出器算，校验器复核（穿地/悬空 ≤ 1 cm） |
| **stack（叠放）** | 上层件的 `position.y = 下层件的顶面 + 自身半高`；关系写 `stackedOn` | 导出器算，校验器查「有没有真的落在下面那件的顶面上」 |
| **lean（斜搭）** | 一块 `role=ramp` 的板 + 板上道具（`support=<ramp id>`）；道具按板法线摆正 | 校验器查坡度 ≤45°、**行走面底边**与地面齐平（板底可低一个板厚）、顶边齐平台面（§10.2） |
| **group（成组）** | 一组件的 `zone` 相同 + `group` 字段相同；Blender 里是集合实例 | 导入器把 `group` 变成一层父节点（`LevelContent/<zone>/<group>/`），**只为可读与人手排查**，不影响坐标 |

### 5.4 出图

见 §6。预设写在 JSON 里，所以**同一个关卡在任何机器上出同一张图**（相机参数是数据，不是脚本里的魔法数）。

### 5.5 一键自检（导出器内部必做）

导出结束前自己跑一遍，任何一条不满足就以非 0 退出（`--python-exit-code 2` 已经保证 Blender 会把 Python 错误变成进程错误）：

1. `view_layer.update()` 之后读矩阵；
2. 随机抽 3 件做 **JSON → 矩阵 → 对比**，位置误差 > 1e-4 m 就失败（实测过：不做这一步，44 件全是原点）；
3. 没有实例引用到「已 link 进场景」的库集合（§1.5 坑 2）；
4. 每件的 `def` 在库里有对应集合，`aabb` 与 manifest 一致（≤1 mm）。

---

## 6. 渲染预设

| 预设 | 相机 | 引擎 | 分辨率 | 实测耗时 | 用途 |
|---|---|---|---|---:|---|
| `plan` | `ortho_top`，coverage = 场地宽 ×1.05 | **Workbench** | 1280×720 | **4.9–10.6 s** | **布置对不对**：密度、间距、留白、门堆是否挡路；最常用的迭代图 |
| `overview` | 3/4 俯视，lens 45 | EEVEE | 1280×720 | **35–49 s** | 给人看「长什么样」、比例与遮挡 |
| `ball_eye` | 起点/球心高度，lens 30–35 | EEVEE | 1280×720 | 24–38 s | **球的视角**：能看清起伏、台面高度、可不可上 |
| `hero` | 任意 | Cycles CPU | 1920×1080，64 spp | 约 250–330 s（按像素×采样外推） | 定妆图/宣传，不进日常回路 |

看图的判读清单（代理出图后**必须自己先看**，再看这四条）：

1. 有没有**半埋**的件（包围盒中心贴地会埋一半——这是最容易犯的错，实测第一次跑就出现了）；
2. 桌面/板上的件有没有**浮在空里**或**陷进去**；
3. `plan` 里有没有**过密的死堆**（球进来会被卡死）和**空得没东西的区**；
4. `ball_eye` 里台面高度差看起来「可上」还是「是墙」（与 §10 的三态判定对一下）。

> 已知观感缺陷（不影响判读，别再花时间修）：Workbench 的材质色要 `material.diffuse_color`（只设 Principled 会显示成灰）；EEVEE 在 4.2+ 移除了 `use_gtao`，接触阴影比 3.6/4.0 少一层（`props/README.md` §5 已记录同一现象）。
>
> **引擎名不要硬编码**：`renderPresets[].engine` 里的 `EEVEE / WORKBENCH / CYCLES` 是**逻辑名**，渲染器用 `Docs/Art/OriginalModels/props/scripts/prop_lib.py` 的 `select_eevee_engine(scene)` 解析成当前版本的真实 id（3.6/4.0–4.1/5.0+ → `BLENDER_EEVEE`，4.2–4.5 → `BLENDER_EEVEE_NEXT`）。这是**一份逻辑一份实现**的直接兑现：道具预览已经在用这套探测，关卡渲染不要再写第二份。实测本机 4.0.2 上该函数正常工作。

---

## 7. Unity 侧：一个受管子树的导入器

> 本节全是**设计**。没有在 Unity 里跑过，也没有写 C# 代码。实施时按阶段 P1 由 Unity 操作员做。

### 7.1 边界（能碰什么、绝对不能碰什么）

| 允许 | 禁止 |
|---|---|
| 在 `LevelContent` 根下创建/更新/删除**自己命名**的子节点 | 新增/删除 `LevelContent` 之外的任何对象 |
| 给这些子节点加 `MeshFilter/MeshRenderer/BoxCollider/Transform` | 改 `KatamariBallMotor`、`PickupFieldHost`、`StoryRoundController`、`HUD`、`VRCStation` 的任何字段（**除了**调用 `PickupFieldHost` 的公开同步入口，§7.4） |
| 调用 `PickupFieldSync.SyncFromChildren(host, root, declaredValueUnits)` | 调用 `SaveScene`（除非显式 `--save`） |
| 一次性生成/更新预制体（独立菜单，不随导入跑） | 重排/移动 `LevelContent` 之外的对象、改 `.unity` 里房间那一半 |

根节点约定：`LevelContent/<zone>/<group?>/<itemId>`。**层级只是为了可读**，坐标全部是世界坐标写进 Transform（导入器算好局部值）。

### 7.2 幂等规则（重复导入零变化）

1. 读 JSON → 按 `itemId` 建索引；扫 `LevelContent` 下所有带 `LevelItem` 标记组件（一个只有 `itemId`/`def` 字段的 `MonoBehaviour`）的节点 → 建第二个索引。
2. **只做三件事**：JSON 有、场景无 → 创建；两边都有且 `def` 或位姿变了 → 更新；场景有、JSON 无 → **删除**（并打一行日志说删了什么）。
3. **零变化判定**：位姿比较用 `Vector3.Distance < 1e-3` 与 `Quaternion.Dot > 0.99999`；`def` 相同就不碰预制体实例（避免无意义的 prefab 重连）。
4. 返回值 `LevelImportReport`（新建/更新/删除/跳过/错误计数）+ 一行 `[level] +0 ~0 -0 props=110` 日志。**第二次导入必须是 `+0 ~0 -0`**——这就是幂等的验收判据。
5. **不做** `SaveScene`：导入完让场景处于 dirty 状态，由人决定何时保存（避免把「导入」和「保存」绑成一次不可撤销的动作）。`--save` 只给批处理用。
6. 手改保护：受管节点上挂 `LevelItem` 的人在 Inspector 里改位姿 → 下次导入被覆盖。**这是设计选择**（单向），要在 Inspector 上用 `[Tooltip]` 明写「此对象由 placement.json 生成，手改会被覆盖」。若用户要双向，见 §12 U3。

### 7.3 预制体库（一次性，独立菜单）

现在仓库里**没有道具预制体**：`Art/Props/<def>/<def>.fbx` 是裸模型。预制体由一次性的编辑器流水线从 **FBX + `exports/<def>.manifest.json`** 生成：

| 预制体内容 | 来源 | 注意 |
|---|---|---|
| `MeshFilter/MeshRenderer` | `Art/Props/<def>/<def>.fbx` | 材质按 `manifest.material_base_colors` 建（FBX 不保存 Principled 颜色，`props/README.md` §7.8 已记录） |
| `propBounds`（玩法体积）= 模型 AABB | `manifest.bounds_m`（**真值**，不是目录的 `game_longest_dim_m`） | `localScale = 1`；这消灭了「0.9 m 立方体暖壶」那类把体积放大几十倍的错 |
| `BoxCollider`（玩家层） | `manifest.collision.boxes`（**尚未实现**，[COLLIDER_LAYER](../PickupAbsorb/COLLIDER_LAYER.md) 只定了接口） | 没有 `collision` 块时**不生成碰撞体**（不是「回退成大盒」——回退会把 2.8 cm 的餐巾变成空气墙） |
| 价值 | 关卡数据 `defs[].valueUnits` / `props[].valueUnits` | 导入器只把声明值传给同步工具，**不重算**；预制体不带价值 |

预制体是**资产**（`.prefab` + `.meta`，GUID 稳定，进仓库）。导入器只 `PrefabUtility.InstantiatePrefab`，不自己拼 mesh——这样「长什么样」只有一处定义。

### 7.4 与 `PickupFieldSync` 的接线（声明值单次写入）

现状：`PickupFieldSync` 从子物体扫 `propBounds`、行集合与 `propColliders`，写进宿主的平行数组；`valueUnits` 自 U2（2026-11-15）起改由调用方声明、**一次写入并回读**，无声明时保留宿主既有值。这是「一件一件手配」的总开关，**必须复用，不能并行造第二个**。

| 冲突 | 现象 | 对策（设计） |
|---|---|---|
| 价值来自哪里 | 体积门是**体积**的；`napkin` 0.30×0.30×0.028 最长边 0.30，按最长边定价会与体积门脱节 | **JSON 里 `valueUnits` 与 `absorbDiameterReq` 两个字段并存**（两个消费者，两个数）。导入器把 `valueUnits` 当作**声明值**传给 `SyncFromChildren`，同步工具一次写入并回读：任何一件与 JSON 不符、或声明的 itemId 没有成行 → 报错并列出，不静默接受，也不自己重算 |
| 会写宿主的平行数组 | 数组是「场景接线」，导入器一碰就可能重排别人的引用 | 只允许通过 `SyncFromChildren(host, fieldRoot, declaredValueUnits)` 这一个入口写；**不直接改数组元素**。同步前先断言 `host` 属于本关卡的受管根 |

`propColliders` 的坑（[COLLIDER_LAYER](../PickupAbsorb/COLLIDER_LAYER.md) §2.2 已查实）：数组长度够、槽位是 `null` 时**静默通过**，所以「没报警 ≠ 接对了」。导入器在同步后逐件核对槽位（`null` 且该 def 有碰撞体 → 报错），并把 `propColliders` 的长度与 `props.Length` 一起打进报告。

### 7.5 预算守卫（在 Unity 侧再挡一次）

`LevelArenaHost` 的容量是硬上限，超了会 `SignalOverflow` + **静默丢弃**（`MaxPlaneCount/MaxBoxCount/MaxRampCount`）。所以导入器在建 `BoxCollider` 之前先数一遍 `role`：

```
planes ≤ 8   boxes ≤ 8   ramps ≤ 4   （来自 level.budget）
```

超了 → **拒绝导入**并打印「哪个 zone 多放了什么」。校验器（§8）也会挡一次，两道门都要有：校验器管「数据错」，导入器管「数据对但场景里别人也放了盒」。

### 7.6 与 `LevelArenaHost` 的字段对应

| JSON | `LevelArenaHost` | 说明 |
|---|---|---|
| `structures[role=ground]` | `groundNormal` / `groundOffset`（`SetGroundPlane`） | 恰好 1 条；**平面过 `center`**（`offset = dot(normal, center)`） |
| `structures[role=boundary]` | `boundaryWalls[]`（`Transform`） | `LevelArenaHost` 读每面墙的**局部 +Z** 当法线、`dot(n, position)` 当 offset，所以导入器生成「空物体墙」：原点在碰撞面上、+Z 指向场内（不是 `planeNormals/planeOffsets` 数组） |
| `structures[role=box]` | `staticBoxes[]`（`BoxCollider`） | `Setup` 烘焙 min/max → `AddBox`，一次两用 |
| `structures[role=ramp]` | **P1 无槽位**（P3 的 `rampBoxes`） | 导入器给 `BoxCollider` 但**不登记**，也**绝不进 `staticBoxes`**（`TERRAIN_SUPPORT` §7.7 审计第 1 条） |
| `props[]` 中 `absorbable=false` | **不进 arena**：`PickupFieldHost` 按 `rise vs stepLimit` 自己分成「地形/挡路」 | `LevelArenaHost` **不得**引用道具（§7.4 硬规则） |

### 7.7 失败模式（写下来是为了「下次一眼认出来」）

| 症状 | 真因 | 认出来的办法 |
|---|---|---|
| 坡底有一堵看不见的墙 | 同一块板既是 `ramp` 又是 `box` | 报告里 `refused=` 或 `ramps` 计数比 JSON 少 |
| 球能爬 58° 的「墙」 | 斜面坡度超上限却被静默登记 | 报告里 `GetLastRampRefusalReason()` 非空 |
| 整关吸不到任何东西 | `allowedKinds` 为空 = 全部拒绝 | `[arena]` 那行日志里 kinds=0 |
| 第二遍导入还显示 `+110` | 幂等键不是 `itemId`（例如按名字/按顺序） | 报告计数 |
| 道具吸到球上后还剩一个悬空碰撞盒 | `propColliders[i]` 是 `null` 槽位（静默通过） | 同步后的槽位核对（§7.4） |

---

## 8. 校验器（纯 Python，Linux 随时可跑）

入口：`python3 Tools/levelbuild/levelbuild.py check <placement.json>`；`--stale` 检查 JSON 与 `.blend` 导出是否过期（同 `mapbuild --check` 的用法）；`--report` 打印分区/分档表。

**一份逻辑一份实现**：吸收球径公式、AABB→`aabb` 的取整、世界 AABB（旋转后）、支撑判定，全部放进 `Tools/levelbuild/placement_core.py`（**不 import bpy**），Blender 侧脚本 `import` 同一个模块。于是「渲染时的颜色」「校验器的判定」「导出时的 `absorbDiameterReq`」不可能漂移。

| # | 规则 | 判据 | 失败长什么样 |
|---|---|---|---|
| 1 | 结构合法 | `schema`/必填字段/`def` 在目录里/`itemId` 唯一 | `props[17] itemId 重复` |
| 2 | 场地边界 | 每件的世界 AABB 在 `level.bounds` 内（含 0.05 m 余量） | `prop_044 越界 0.32 m（x=+2.1）` |
| 3 | 出生净空 | 距 `ballStart` ≥ `spawnClearanceM`；距 `playerSpawn` ≥ 1.0 m | `5 件在球起点 1.0 m 内` |
| 4 | **穿地** | 世界 AABB 下沿 ≥ 支撑面 − 0.01 m | `plate 埋入 0.012 m` |
| 5 | **悬空** | 无 `support` 且下沿 > 地面 + 0.02 m | `cup 悬空 0.41 m`（实测第一次跑就出现：桌面没腿） |
| 6 | 叠放 | `stackedOn` 指向的件的顶面 = 本件底面（±1 cm） | `cookie 与 plate 顶面差 0.06` |
| 7 | **道具互穿** | AABB 两两相交体积 > 较小者 5% 且无 `stackedOn/support` 关系 | `apple 与 bowl 交叠 38%` |
| 8 | **结构互穿** | 道具 AABB 不得穿入 `role=box` 的实体（桌面除外：桌面之下的件必须显式 `support`） | `thermos 穿进 crate 0.08 m` |
| 9 | **预算** | `planes ≤8 / boxes ≤8 / ramps ≤4 / ground =1`；`props` 里「不可吸且挡路」的件数 ≤ `blocking` 6 | `boxes=11 超 3（zone=table 多放了 2 个台面）` |
| 10 | 斜面几何 | 坡度 ≤45°；**行走面底边** = 地面（可低至 `地面 − 0.01 − 板厚`，坡脚板底入地不算穿地）且 ≤ 地面 `min(0.05, stepLimit)`；**行走面顶边**对齐台面（δ ≤ stepLimit） | `plank 板底 −0.06（穿地）` |
| 11 | 斜面身份 | 同一 `id` 不得同时出现在 `box` 与 `ramp` | 直接硬错误（对应本文 §7.7 第一行「坡底隐形墙」） |
| 12 | 可吸性 | `D_req = 2.673·(abc)^(1/3) ≤ ballDiameterMax` 才允许 `absorbable=true`；`absorbable=true` 但 `D_req` 超过 = 数据自相矛盾 | `thermos D_req=2.41 > 2.20` |
| 13 | **分档人口** | 按球径档（0.30/0.50/0.80/1.10/1.50/2.20）统计「能吸的件数/单位」，与关卡设计表对照；**打印成表** | `t2 档只有 3 件，设计表要 12 件` |
| 14 | 总量 | `propCount` / `valueUnits` 与 `level.expected` 相等 | `110 件/412 u 期望，实得 109/404` |
| 15 | 可达性（**P2 再开**） | 从球起点到每件的通路：台阶高 ≤ `0.25·D`、缝宽 vs 球径；依赖 §10 的三态规则 | 先只打印可疑项，不判死 |
| 16 | 确定性 | 重新导出逐字节相同；`--stale` 与 `.blend` 指纹比对 | `placement.json 与 .blend 不一致（未导出）` |

第 13/14 条是**第一关的保险**：110 件、412 u、9 kind 是已经拍过板的量（`PICNIC_CORNER_LEVEL_ONE` §3.4），重排时最容易悄悄丢件。校验器把它变成硬门，比人眼数靠谱。

---

## 9. 与现有工具的关系：保留什么、退役什么、替代什么

| 现有东西 | 现状 | 本方案下的定位 |
|---|---|---|
| `Docs/Design/Maps/PicnicLawn/layout.json` | 整图 PvP 语义（21 zones / 60 结构 / 21 路线 / 8 尺寸门），**抽象层级是「地图」不是「关卡实例」** | **保留**。`placement.json` 的 `zone` 引用它的 `zones[].id`；它继续管「地图长什么样、路怎么走」，不管「这一关 110 件摆哪」 |
| `Docs/Design/Maps/PicnicLawn/prop_catalog.csv` | 目录 + 游戏/真实尺寸 + 数量 | **保留**，仍是 `def` 的唯一来源。**注意**：`game_longest_dim_m` 不再当拾取门槛（体积门取代长度档），但列先不动（PICNIC §2.2 已记录这个口径冲突） |
| `Tools/mapbuild/build_greybox.py` + `unity_greybox.json` | 数据 → 扁平载荷 → 灰盒 Builder | **保留 `--check`**（守住「生成物 = 作者数据」）；**不再用它建关卡** |
| `Editor/MapGreybox/PicnicLawnGreyboxBuilder.cs` | 「整图 · 从零重建 · 会 `SaveScene`」，重跑必然抹掉手工接线 | **退役为「地图预览生成器」**：只用于整图俯瞰数据核对，**禁止**在正式场景上跑。它的 `ExpectedZones/ExpectedStructures/...` 硬计数保留（那是数据的回归门） |
| `Editor/Pickup/PickupFieldSync.cs` | 从子物体扫 `propBounds` 等数组；`valueUnits` 自 U2 起消费声明值，无声明时保留既有值 | **保留且成为唯一写入口**，导入器调用它（§7.4），声明值一次写入并回读 |
| 场景里 110 件的平行数组（`propBounds`/`propColliders`/`valueUnits`） | 只活在 `StoryLevel.unity` | **第一次导入后仍然存在**（`PickupFieldSync` 的产物），但**真源变成 `placement.json`**；「只活在场景里」的回归风险由此消除 |
| `Editor/MapGreybox/GreyboxPayload.cs` | `[Serializable]` DTO + `JsonUtility` 载荷 | **保留**（地图预览用）；`placement.json` 走自己的 DTO，**不复用**——两者层级不同（一个整图，一个关卡实例），硬合并会让两边都变难改 |
| 新：`Tools/levelbuild/`（校验器 + Blender 脚本桥） | — | **新增**（P0） |
| 新：`Editor/LevelContent/`（导入器 + `LevelItem` 标记 + 预制体生成） | — | **新增**（P1/P2） |

一条容易犯错的地方，先写死：**不要再造第二个「从数据重建场景」的工具**。旧 Builder 之所以危险，正是因为它重建整张场景；新导入器的合法性来自「受管根」这一条边界，而不是「它更聪明」。

---

## 10. 与碰撞体规格、地形支撑的接口

### 10.1 三层不混（沿用 `COLLIDER_LAYER` 的定性）

| 层 | 数据来源 | 本工具管不管 |
|---|---|---|
| 视觉网格 | `Art/Props/<def>/<def>.fbx`（预制体） | **不管**（只引用 def） |
| 碰撞体（玩家胶囊） | `manifest.collision.boxes` → 预制体 `BoxCollider` | 只决定**结构**的 `collision: box/none`；道具碰撞体跟 def 走 |
| 玩法体积 `propBounds` | 模型 AABB（`manifest.bounds_m`） | JSON 里的 `aabb` 是**副本**，导入时以预制体为准，两者不一致 → 报错 |

### 10.2 结构的两种喂法（对应「地形用盒子数据还是真碰撞体未定」）

`placement.json` 里结构只写 **pose + size（盒）**，**不写「用哪种实现」**：

- 若最终选「真 Unity 碰撞体」：导入器建 `BoxCollider`，`LevelArenaHost.Setup` 烘焙 min/max（现状路线）。
- 若最终选「盒子数据」：导入器**不建 collider**，把 `center/size/rotationQuat` 直接写进 `LevelArenaHost` 的新字段（或 `motor.AddBox` 前的数据表）。

两条路都从同一份 JSON 出发，切换只改导入器一处。**这就是「两种都喂」的落点**。代价要说清：真碰撞体路线下，45° 摆的盒会退化成外包 AABB（`TERRAIN_SUPPORT` §7.2 已记录），所以**斜的必须走 `ramp`，不要走 `box`**。

### 10.3 三种身份的自动判定（校验器预测 + 运行期自作）

对每个 `absorbable=false` 的结构/大件，运行期按一条几何规则分类（`TERRAIN_SUPPORT` §6.1）：

```
rise = 顶面高 − 当前支撑面高
rise ≤ stepLimit(=0.25·D)  → 地形（可上、顶面进支撑表）
rise >  stepLimit           → 挡路（进 SetBlockingBoxes）
可吸（D_req ≤ 当前 D）      → 食物
```

校验器**照同一公式预测**并打表：这样作者在摆一件 1.2 m 的箱子时就能看到「D<1.9 时它是墙，D≥1.9 才可上」。**判定逻辑不写进 JSON**（否则就是第二个阈值来源）。

### 10.4 斜面的作者陷阱（必须写进面板提示）

同一块板既是 `ramp` 又是 `box` → 坡底一堵位置完全不合理的隐形墙（`TERRAIN_SUPPORT` §5.5）。对策：面板上 `ramp` 与 `box` 是**互斥的两个按钮**；校验器第 11 条直接拒绝；导入器自检同一 `BoxCollider` 出现在两个数组时报错。

---

## 11. 分阶段

### P0 — 数据与门（不碰 Unity，本机可完成，**先做这个**）

- `Tools/levelbuild/placement_core.py`：坐标转换、世界 AABB、`D_req`、支撑判定（**唯一实现**）。
- `Tools/levelbuild/levelbuild.py`：`check` / `--stale` / `--report`，实现 §8 第 1–12、14、16 条。
- `Tools/levelbuild/blender/level_author.py`：库导入、按 JSON 铺场景、摆位辅助（seat/stack/lean/array）、导出 + 自检。
- `Tools/levelbuild/blender/level_render.py`：相机预设 → PNG（三个引擎）。
- **验收**：拿实测那 44 件的 JSON 跑通 `check`（0 错）+ 出三张图；再故意造 5 个坏数据（穿地/悬空/越界/预算超/价值对不上），**每个都必须红**。

### P1 — 第一关一角搭出来并导入（**本阶段的交付物如下**）

| 交付物 | 具体 |
|---|---|
| `Docs/Design/Levels/story_01_bench_size/placement.json` | **110 件 / 412 u / 9 kind**，7 个区（`pocket`/`fruit_a`/`fruit_b`/…按 PICNIC §3.4），4 件门堆；球起点 `(-12.5,0.15,-12.5)`、上限 2.20、边界 `[-19.4,-6.0]²` |
| 结构清单 | 地面 1 + 边界 4 平面 + 盒 ≤8（花圃台、树根丘、角树、指示牌、毯缘…按 `PICNIC` §3.2 的真实坐标）+ 斜面 ≤4（第一关先 0–1 块，验证「木板搭上桌」的手感） |
| 校验报告 | `levelbuild check --report`：0 错；分区/分档人口表；**预测的三态表**（哪些件在哪档变地形/挡路/食物） |
| 渲染 | `plan` + `overview` + `ball_eye` 各一张 1280×720，进 `Docs/Evidence/`（代理**先自己看图**再交） |
| Unity 导入 | `LevelContent` 根 + 110 件实例 + `PickupFieldSync` 一次写入声明值并回读一致；**第二遍导入 `+0 ~0 -0`** |
| 接线 | `LevelArenaHost` 的 planes/boxes/ramps 与 JSON 计数一致；`[arena] planes=5 boxes=4 ramps=0` |

**P1 的验收判据（六条，缺一不算完）**：
1. `check` 0 错，且 5 个负向用例全红；
2. 110 件在场景里位置与 `plan` 图一致（抽 5 件量坐标，误差 < 1 cm）；
3. 重复导入零变化；
4. `valueUnits` 总和 412，逐件与 JSON 一致；
5. 球起点 1.0 m 内无道具；球能上花圃台（`0.26 ≤ 0.25·D` → D ≥ 1.04）；
6. `plan` 图上 7 个区的人眼密度与设计表差距可解释（谁的差、为什么）。

### P2 — 预制体 + 碰撞体 + 分档观感

- 预制体流水线（`Art/Props/<def>` → `.prefab`，含 `propBounds`、材质、可选 `BoxCollider`）。
- `manifest.collision.boxes` 落地后，导入器接 `propColliders` 槽位核对。
- Blender 侧「按可吸球径上色」的叠加层 + 面板显示选中件的 `D_req`/档位/所在区人口。

### P3 — 打磨

- 可达性（校验器第 15 条，依赖 §10.3 的三态规则落地）。
- `hero` 级出图（Cycles 批跑，夜里）。
- 双向（Unity 手调 → 回写 JSON）**只在用户明确要求时做**（§12 U3）。

---

## 12. 风险与未决（只列真正需要用户拍板的）

| # | 问题 | 选项与代价 | 建议 |
|---|---|---|---|
| **U1** | **Blender 版本钉在哪** | (a) 本机钉 Ubuntu 的 **4.0.2**（现状，`props/README.md` 的产物是 3.6.21 生成的，脚本号称 3.6→5.x 兼容）；(b) 本机不用 Blender，全部在 Windows **5.3.0** 上跑（吃用户机器、要传文件）；(c) 本机想办法装 3.6/5.x（无 arm64 官方包，只能自己编） | **(a) + 3.6 兼容口径**。要接受：4.0.2 没有 OpenImageDenoise（Cycles 无降噪，只能提采样）。**这个成本比想象小**：脚本层面 4.0.2 与 5.3.0 的差异已经被 `prop_lib.select_eevee_engine` 吸收掉了（实测），真正的差异只剩「降噪」与「AO 在 4.2+ 没了」 |
| **U2** | **`placement.json` 放哪、谁来改** | (a) 进仓库 `Docs/Design/Levels/<id>/`（可 diff、可 review、可校验）；(b) 只放 Windows 工作目录（不进 git） | **(a)**。不进 git 就等于回到「数据只在场景里」 |
| **U3** | **单向还是双向** | (a) **单向**（Unity 里手改会被下次导入覆盖）；(b) 双向（导入器之外再加一个「从场景导出 JSON」，要做冲突合并，等于写第二个真源） | **(a)**，并在 Inspector 上明写。要微调就先在 Blender 里调，或在 JSON 里改一个数——比双向便宜一个数量级 |
| **U4** | **第一关的 4 支暖壶** | (a) 按真实尺寸 0.12×0.30 建模（`D_req 0.435`，可吸）；(b) 维持场景现状 0.9 m 立方体（`D_req 2.41`，2.20 下永久吸不到，64 u 是空账） | **(a)**。这是 `PICNIC` §6 U3 的老问题，本工具只是把它变成「校验器会红」 |
| **U5** | **结构用真碰撞体还是盒子数据** | 见 §10.2：两条路都从同一份 JSON 出发，差别在导入器一处；真碰撞体路线下 45° 盒会退化成 AABB | **先按 `TERRAIN_SUPPORT` 的现状（真 `BoxCollider` + 烘焙）**，JSON 不预先站队 |
| **U6** | **PNG 进不进仓库** | (a) 进 `Docs/Evidence/`（可追溯、体积涨）；(b) 只留 `plan` 一张当证据 | **(b)**，其余放 `~/tools/level_authoring_probe/` 或 Windows 工作目录 |

不需要用户拍板、由本文直接定的：JSON 格式、坐标/四元数约定、受管根的名字（`LevelContent`）、导入器不 `SaveScene`、`ramp` 与 `box` 互斥、预算当硬门。

---

## 13. 实测产物与复现

全部在仓库外，**不进 git**：

```
~/tools/blender-env.sh                      # 环境入口（source 它）
~/tools/blender-4.0.2/                      # Blender 4.0.2 arm64（dpkg -x）
~/tools/blender-libs/                       # 运行时 so
~/tools/level_authoring_probe/
├── author_test.py                          # 库导入 + 44 件摆位 + 导出 placement.json + round-trip 自检
├── render_test.py                          # 只读 placement.json 重建场景 + 按预设出图
├── scene_test.py                           # 三引擎对照（10 件道具）
├── author_placement.json                   # 那 44 件的 JSON（schema 样例，可直接看）
├── preset_overview_eevee.png               # 3/4 俯视（EEVEE，35–49 s）
├── preset_top_workbench.png                # 正俯视（Workbench，4.9 s）← 布置判读用这张
└── preset_ball_eye_eevee.png               # 球视角（EEVEE，24–38 s）
```

出图（两个脚本都要求 `source ~/tools/blender-env.sh`）：

```bash
"$BLENDER" $BLENDER_FLAGS --python ~/tools/level_authoring_probe/author_test.py
"$BLENDER" $BLENDER_FLAGS --python ~/tools/level_authoring_probe/render_test.py -- EEVEE overview,ball_eye
"$BLENDER" $BLENDER_FLAGS --python ~/tools/level_authoring_probe/render_test.py -- WORKBENCH top
```

三张图我都**自己看过**再写的结论，图上能直接读出两件事（这正是校验器第 5、6 条要拦的）：
- `preset_ball_eye_eevee.png`：桌面**没有腿** → 整块板悬空（数据缺 4 个腿盒）；
- 同一张：斜板上的苹果**半埋**进板面（道具按 `y=h/2` 摆在水平面，斜面上要按法线摆正）。

---

## 14. 本轮写范围与校验

- 写：**只有本文件**（`Docs/Design/Tools/LEVEL_AUTHORING.md`，目录新建）。实测文件在 `~/tools/` 与 `/tmp/blender_test/`。
- 不写：代码（`Tools/`、`Editor/`、`Runtime/`）、`.unity`/`.asset`/`.prefab`、其它文档、`tasks.json`。没有碰 Unity、没有跑 git 写命令。
- `python3 Docs/tools/validate_plan.py` → `PASS: 61 tasks; both release DAGs acyclic; source hashes verified.`（Unity/Udon 编译、VR、多人、性能、commerce 仍 `NOT_RUN`；本文不改任务与配置，跑它是为了证明没弄坏规划数据）。

---

## 15. 实现状态（2026-11-15，P0 + P1 的 Linux 部分）

> 本轮**没有碰 Unity 编辑器、没有跑 git 写命令**。Unity 侧只有源码，编译/导入/幂等**都没有验证**（`NOT_RUN`）。本节记的是「代码在哪、和上面的设计哪里不一样、还差什么」。

### 15.1 落地了什么

| 文件 | 内容 |
|---|---|
| `Tools/levelbuild/placement_core.py` | **唯一实现**：schema、规范形（排序/取整/`-0.0`/规范四元数符号）、Blender↔Unity 坐标、世界 AABB、`D_req = (60abc/π)^(1/3)`、支撑/落位（含斜面法线摆正）、预算与人口/三态表、`defs[]` 价值回退 |
| `Tools/levelbuild/levelbuild.py` | `check`（§8 的 1–12、14 条 + 规范性 warning）、`format`、`seat`、`from-layout` |
| `Tools/levelbuild/blender/{blender_common,build_scene,export_placement,render_views}.py` | 库导入（每 def 一个**不 link 进场景**的集合）、按 JSON 铺场景、场景→JSON（自检 + 逐字节往返）、相机预设→PNG（三引擎） |
| `Tools/levelbuild/examples/placement.json` | 16 件 / 8 结构 / 4 区的小样例，`check` 0 错，JSON→场景→JSON **逐字节一致** |
| `Tools/levelbuild/README.md` | 人/代理/Unity 三边的用法 |
| `Docs/tests/levelbuild/` | 35 个测试：坐标、四元数、AABB、`D_req`、坐正、确定性、5 个负向用例、CLI 退出码 |
| `VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/LevelBuild/PlacementImporter.cs` + `PlacementImportReport.cs` | 单向导入器（受管根、按 `itemId` 增改删、结构 `BoxCollider`、`LevelArenaHost` 接线、`valueUnits` 写后回读、不 `SaveScene`） |

### 15.2 和上面正文不一样的地方（都是实测/拍板逼出来的）

1. **`aabb` 的轴向**（§4.2 补注）：manifest 的 `dimensions_m` 是 Blender 轴向（`z` 是高），JSON 要 Unity 轴向，所以 `aabb = (dx, dz, dy)`。§13 的实测脚本把 manifest 三个数直接写进 JSON——对矮件无害，对暖壶/水瓶这种高件会让它躺倒、`y = aabb.y/2` 也跟着错。现在只有一个转换点 `placement_core.manifest_aabb`。
2. **四元数写规范符号**（`w<0` 取反）：否则同一姿态可以写成 `q` 或 `-q`，「重新导出逐字节相同」就不成立。
3. **`ground` 与 `boundary` 是「过 `center` 的平面」**，不是盒子的顶面；否则 `groundOffset` 会和场景里的地面差半个厚度，所有贴地件一起报「穿地 0.02 m」。
4. **`defs[]` 与按 def 的价值**（§4.2、§7.4 已改）：Lead 2026-11-15 拍板，`valueUnits` 由关卡数据按 def 声明；导入器把声明值传给 `PickupFieldSync` 一次写入并回读（U2 已实现）。**缺值表行不再丢件**：同步工具已删除 (kind, tier) 值表，`container m` 这类没有旧表行的组合照常成行，价值来自声明。
5. **`boundary` 在 Unity 里是空物体墙**（§7.6 已改）：以 `LevelArenaHost` 的真实字段 `boundaryWalls` 为准（局部 +Z 指向场内、原点在碰撞面上），不是 `planeNormals/planeOffsets`。`rampBoxes` P1 没有，斜面有 collider 但不登记。
6. **导入器没有 `LevelItem` 标记组件**（§7.2 曾设想）：Editor 程序集里的 MonoBehaviour 无法序列化进场景，而 `Runtime/` 不在本工具写范围。`itemId` 就是物体名，正好也是 `PickupFieldSync` 的行 id——一个键、两个消费方。
7. **俯视图的 `coverage_m` 是「短边保证入画」**，渲染时按分辨率比例放大 `ortho_scale`；否则 16:9 会把方形场地上下裁掉（实测 13.4 m 场地只看到 7.9 m）。
8. **没有 `--stale`**（§8 第 16 条的一半）：它要 `.blend` 指纹，而 `.blend` 不进仓库；现在用「重新导出逐字节相同」代替，`export_placement.py` 自带往返自检。

### 15.3 用设计者布局转出来的第一关（**产物在 `/tmp`，未入库**）

```bash
python3 Tools/levelbuild/levelbuild.py from-layout Docs/Design/StoryMode/picnic_corner_layout.json \
    --out /tmp/levelbuild/story_01_bench_size/placement.json [--expected 127,1500]
python3 Tools/levelbuild/levelbuild.py check <该文件> --report     # 0 错
```

读数：**127 件 / 1500 u / 44 def / 结构 1 ground + 4 boundary + 4 box + 2 ramp**（`--expected 127,1500`，`check --report` **0 错 0 警告**）。9 个原先缺模型的 def 现已齐备，生成物与设计表一致。转换做了三件事：按**真实尺寸**重坐（布局的 `y` 用的是它自己的高度表）、`support` 标签→id（`bed→flower_bed`、`folding_table→那件折叠桌的 itemId` 走 `stackedOn`）、两块的斜板从**坡脚点**重建、且把**作者给的底/顶线当成行走面**（中心 = 坡脚 + 半坡长 − 半板厚·法线；板体向地面/坡内延伸，所以坡脚板底比地面低约 3.5 cm，这是设计意图，校验器按「一个板厚」放行）。直接把 `center` 抄进盒会错两层：坡脚浮 0.245 m，行走面又高出设计线一个板厚（实测 `ramp_to_table` 0.746 vs 台面 0.70）。

另有一条源数据观察：`shell_tree_mound`（2.5×2.5，在 `(-18.75,-18.75)`）会越过 `x0/z0` 两道边界平面约 0.65 m——结构不参与越界规则，所以不是错误，但角树有一半在场外。

### 15.4 还没做 / 需要别人做的

| 项 | 为什么 |
|---|---|
| Unity 侧任何验证（编译、导入、`+0 ~0 -0`、`valueUnits` 回读、`[arena] planes=… boxes=…`） | 本轮不碰编辑器；需要 Unity 操作员按 §11.2 的六条验收 |
| `Art/Props/<def>/<def>.prefab` 预制体库（P2） | 导入器现在直接实例化 `.fbx` |
| 值表的 `container m` 行 | 已随 U2 解除：值表删除后缺行不再丢件，价值由 `defs[].valueUnits` 声明 |
| `LevelArenaHost.rampBoxes`（P3） | 现在斜面只有 collider，不登记 |
| 9 个 def 的模型 | 第一关的 11 件 / 782 u |
| `physics_test_field.json` 迁到本格式 | Lead 说之后再做；本 schema 是「一格一份 placement」（`level.id` + 目录），没有假设一场景一份 |
