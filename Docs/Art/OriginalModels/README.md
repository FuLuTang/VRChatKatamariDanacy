# Original basic models — placeholder set

本目录是供离线预览与后续资产讨论使用的原创、中性、可替换低多边形占位模型。不是最终美术、游戏截图或 Unity 导入验证；不包含第三方资产、商标、原作角色/音乐/品牌元素，也未指定授权许可。请勿从这些模型推断玩法实体数、碰撞体、价值或正式美术方向。

## 目录内容

每个模型各有 `.blend` 源文件、`.fbx` 导出、正/侧/俯三视图拼图 PNG 与同名 `.manifest.json`；`generate.py` 是纯 `bpy` 可复现脚本。`manifest.json` 为总清单；`SHA256SUMS` 为各交付文件的 SHA-256 校验清单。

## 临时约定（T06 前不冻结）

- Blender 单位按 1 BU = 1 m 暂定；建模 Z 向上。
- FBX 导出 Forward=-Z、Up=Y、应用变换；预期 Unity 实例 Transform 为 Rotation=(0,0,0)、Scale=(1,1,1)，直立、正面 +Z。此处仅记录导出意图，未作 Unity 导入验证。
- 模型根对象命名为 `<名称>_Root`；球芯枢轴在球心，其余模型枢轴在底面中心。`carrier_platform` 的 `SeatAnchor`、`FollowRigAnchor` 是独立空物体；跟随锚点不是平台几何。
- 采集物世界尺度 **OPEN**；清单中尺寸仅作现实尺寸参考，不代表玩法比例、Collider、收益或游戏内尺度。
- 预览为单模型三视图，不是游戏画面。材质为命名的本地纯色材质，无外部贴图。

## 限制与后续

网格自检在生成脚本内检查非流形边与零面积面，并重算法线；清单记录检查结果。该检查不等同于完整资产 QA、平台性能测试、碰撞体验证、FBX 往返一致性测试或 Unity 导入测试。缓坡/外形和枢轴应在 T06 尺度与接口冻结后再审核。未经审核不得视为可直接集成的最终资产。

## 复现

### 实测环境（2026-11-15）

- Steam 版 Blender **5.3.0**（内置 Python 3.13.13）的 `--background` 可用、无 Steam DRM；应直接调用 `steamapps\common\Blender\blender.exe`，不要走 `blender-launcher.exe`（只是包装器）。与用户的 Blender GUI 并存实测安全：加 `--factory-startup` 后不写任何 `%APPDATA%` 配置。
- 老管线 `generate.py` 在 5.3 上跑通：9 件模型，几何与既有产物逐位一致。
- 新管线 `props/scripts/build_all.py` 曾因 EEVEE Next 移除 `SceneEEVEE.use_gtao` 而在 5.3 上崩溃；`prop_lib.py` 现已改为按功能探测（`hasattr` 守卫 + 渲染引擎 id 探测），覆盖 3.6、4.2–4.5（`BLENDER_EEVEE_NEXT`）与 5.x（`BLENDER_EEVEE`）。在 5.3 上仅用 `hasattr` 守卫临时修过的副本可产出 8 件道具、`TOTAL tris=1634` 的全部文件；**本仓库提交的改法尚未在 Blender 里重跑验证**。
- 提交的产物是 **Blender 3.6.21** 生成的（FBX 头里写着导出器版本）。

### 可复现性到什么程度（实测）

- `.blend` / `.fbx` **每次重跑字节都会变**（内嵌时间戳与绝对路径），它们的 SHA-256 因此不具备"重跑能对上"的意义。
- `*.manifest.json` 与 `*_preview.png` 逐字节稳定；`*_view*.png` 像素一致，但 PNG 元数据有 14 字节差异（`Date` / `RenderTime` 文本块）。
- 所以 `SHA256SUMS` 是**当前交付物的指纹**（用来核对文件有没有被改动），不是可重复构建的校验门。
- 真要字节级复现，须固定到 **Blender 3.6.21** 并使用同一输出路径。换版本通常仍能得到同样的几何，但字节不会一致。

### 无头跑法

`--python-exit-code 2` 让脚本抛错时进程以非 0 退出；缺了它，Python 报错后 Blender 仍可能返回 0。先把 Blender 路径放进 `BLENDER`，`OUT_DIR` 指向**仓库外的全新目录**：

```
set BLENDER=<...>\steamapps\common\Blender\blender.exe

set MODEL_NAME=ball_core
set OUT_DIR=%TEMP%\OriginalModels-rebuild
"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python Docs\Art\OriginalModels\generate.py
```

```
set OUT_DIR=%TEMP%\PropsRebuild
"%BLENDER%" --background --factory-startup --disable-autoexec --python-exit-code 2 ^
  --python Docs\Art\OriginalModels\props\scripts\build_all.py
```

- 输出目录：`generate.py` 只认环境变量 `OUT_DIR`（不设时用它脚本里写死的仓库外预览目录）；`build_all.py` 读 `OUT_DIR`，也接受命令行 `-- --out <目录>`（命令行优先）。等价写法：`... --python ...build_all.py -- --out %TEMP%\PropsRebuild`。
- 不给 `OUT_DIR` / `--out` 时，`build_all.py` 保持历史行为，写仓库里的 `props/exports/`、`props/previews/`、`props/manifest.json` 与 `props/SHA256SUMS`——那是"发布交付物"的动作，只在确实要更新仓库产物时使用。
- 纪律：探索性重跑一律输出到仓库外的新目录，**不要覆盖** `exports/`、`previews/`、`SHA256SUMS`；`SHA256SUMS` 只由有意更新交付物后的整批重建或 `props/scripts/sign.py` 刷新。
- `prop_catalog.csv` 缺失（或某件道具不在其中、`game_longest_dim_m` 缺失/非数字）现在是**硬错误**：打印 `BUILD_FAILED: ...` 并退出码 2，不再静默记 `row_found: false`。
- 脚本不访问网络、不装 Blender 插件、不写 `%APPDATA%`。

Windows 原始构建目录使用 `%USERPROFILE%` 等环境占位的约定；本目录不记录设备用户名、主机名、私网 IP 或账号信息。
