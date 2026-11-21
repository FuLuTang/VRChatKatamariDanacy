# 原作球芯皮肤与可选装配

现行决定：**owner 2026-11-17，球皮肤用原作球芯；显示等体积直径 = 当前碰撞球直径，随体积变化直接更新，不做平滑弹簧。owner 2026-11-20：球皮肤可切换——原作 27 个球芯全部装上，玩家在设置板选，默认 core_01，本地保存。** 原作关卡与 core_XX 的逐关对应关系未知。参考模型/贴图只供本机参考，不进 git、不上传；引用它们的装配后场景同样不得提交或发布。

## 研究依据与边界

- **本机网格实测**：[核心研究 E2](<../../Evidence/CoreRadius/core_radius_findings.md#L59>)：27 个 OBJ（01–29，无 26/27）都是约 5 cm 的凹凸球，包围盒约 4.8×4.9×5.4 cm，等体积径约 4.65 cm；core_01 约 52.6 cm³。13 个文件顶点逐个相同，其余仅微变；不是尺寸梯度。
- **命名文献**：[E3](<../../Evidence/CoreRadius/core_radius_findings.md#L60>)，本机 [practice mod README](</home/fulutang/KatamariRef/meta/pages/github_il_practice_mod_README.md#L87-L89>)：“Most models are named after the levels where they're used in the unmodified game.” core_XX 按关卡命名，是皮肤而不是尺寸档。
- **玩家观察 + 开发者镜头说法**：[REROLL Steam 讨论](https://steamcommunity.com/app/848350/discussions/0/3561682880003061934/) 的玩家原话 “the 'core' grows and absorbs all the objects”；Takahashi 2004 postmortem 确认镜头随球变大自动拉远。前者是玩家观察，后者只说明镜头行为，不是可见核尺寸的直接测量；两者不能推出零售版精确的核/碰撞比例。
- **专利文献**：[US7402104B2](https://patents.google.com/patent/US7402104B2/en) 将显示模型 Mp、碰撞模型 Mh、滚动模型 Mr 分开。模型分离不等于显示核尺寸恒定，也不能用未列出 Mp 放大步骤断言零售版核不长大。

**owner 2026-11-17：显示核与碰撞球同径，目的是消除粘附件与核之间随成长扩大的空腔。** 吸附物仍在接触处冻结，贴碰撞球面，不向里压、不随核心缩放；凹凸核的局部表面不等于理想球面，实际贴面观感需要 Unity 检查。接触规则见 [ATTACH_PLACEMENT §3](../BallLayers/Attachment/ATTACH_PLACEMENT.md#3-现行设计)。

## 实现与缩放合同

唯一实现：[CoreSkinAssembler](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/BallPresentation/CoreSkinAssembler.cs)，菜单 `VRC Katamari/Ball/Assemble Core Skin`；合集装配（AllMapsHub）在输出场景里也调用它。只显式运行，不在加载/导入回调里执行；只扫描当前活动场景内（含 inactive）的 BallView，按它序列化的 coreMesh 引用定位，不按对象名猜。

### 多皮肤层级

装配器在 `Assets/_RefUnlicensed/Models/KatamariCore/` 下按原作编号 01–29（无 26/27）逐个找 `core_NN.obj` + `core_NN.png`，能用的全部装上；两样都没有的编号静默跳过（操作员没复制），只有一样、或预检不过的列进返回行的 `skipped:`。**core_01 是必需的**：它不可用时整批 `+0 ~0` 零写入，保持纯色球。

```
CoreMesh                    BallView 每帧写 localScale = 碰撞直径；自身纯色 MeshRenderer 关
└─ OriginalCoreSkin         空容器，单位变换
   ├─ core_01               MeshFilter + MeshRenderer，按本网格归一化；默认唯一启用
   ├─ core_02               同上，Renderer 关
   └─ …
```

每个 `core_NN` 按自己的网格做下面的归一化，所以任何一个皮肤显示等体积直径都等于碰撞直径。两个导入网格的顶点、UV、法线、三角形完全相同时，后者直接引用前者的 Mesh（返回行 `shared meshes=`）；材质各自引用自己的贴图，不共享。装配器同时把 `BallView.skinNumbers`（升序编号）与 `BallView.skinRenderers`（同序 Renderer）写好，并把场景里唯一的 `HubSettingsHost.ballView` 指向这个 BallView（场景里还没有设置宿主时不写，合集装配须在加完宿主后再接一次，见下文「切换与存档」）。旧版单皮肤形态（`OriginalCoreSkin` 自己带 MeshFilter/MeshRenderer）会迁移成容器；素材被删掉的皮肤，其 `core_NN` 子节点被删除。

### 缩放合同

[BallView](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallPresentation/BallView.cs) 在马达报告有效碰撞半径后写 `coreMesh.localScale = Vector3.one * (2f * motor.GetCollisionRadiusWorldM())`；碰撞直径每次变化都直写，不设死区或追赶弹簧。旋转仍由原滚动根提供。归一化放在各 `core_NN` 子节点上，不改碰撞/滚动根/吸附根/道具尺度。

导入 mesh-local 顶点的闭合一致绕序网格体积 `V = abs(Σ det(a-o,b-o,c-o))/6`（double 累加，o 为首顶点）。等体积径 `D_mesh = (6V/π)^(1/3)`，子节点均匀缩放 `s = 1/D_mesh`，平移 `-mesh.bounds.center * s`，局部旋转 identity。所以 `D_display = D_mesh × s × D_collision = D_collision`。凹凸核的包围盒宽高深不会全等于这个数，**不要把最大 AABB 边当成核显示等体积径**。core_01 原始单位若保持米，s 约 21.5；导入器若烘焙单位倍率，s 相反变化，最终直径不变。

显示子节点直接引用导入的 Mesh 和带贴图的 sharedMaterial；不实例化导入 prefab 的 root scale/轴向补偿 wrapper，不叠加第二次单位转换；UV、法线和顶点同一 mesh-local 坐标系保留。现有 CoreMesh 的旋转和整个滚动父链原样保留，花纹朝向由原 mesh-local 定义，不宣称复现原作某一关的朝向。所有 CoreMesh 祖先必须单位缩放，否则拒绝整批。现有纯色球的 MeshRenderer 仅 disable、不删除，原网格/材质保留；没有新 Collider。

预检还包括：非 Play/Prefab Mode、未编译/导入、活动场景已加载、没有待保存改动、每个皮肤一个可读 triangle submesh、导入材质确实指向同编号贴图，以及 `OriginalCoreSkin` 下没有自定义组件或非 `core_NN` 子物体。通过后才开一组 Undo；异常整组回滚。装配器不写导入设置、不生成网格或材质、不保存场景；只有真实变化才标记场景 dirty，操作员审阅后保存。写 BallView/HubSettingsHost 字段后调用 `CopyProxyToUdon`；它抛错（U# 程序资产还没重编出新字段）时不回滚，只在返回行末加 `warn:`，重编后再跑一次。

缺少 core_01：`+0 ~0. Note: optional core_01 model/texture missing or unusable (…); existing core left untouched.` 不改任何场景对象、资产或导入器；若已有皮肤也不破坏现状。正确装配后再次运行（先保存）应 `+0 ~0`，计数是新增显示 GameObject 数 / 实际改变的属性组数。成功行形如 `Core skin: +28 ~N. 1 core(s); mesh equivalent diameter=… (core_01). skins=27 [01,02,…,29], shared meshes=K`。

### 切换与存档

- 设置：`HubSettingsMath.BallSkin`（id 14），面板行「球样式 / Ball skin / 塊のスキン」，值显示 `No.01`。面板字体缺「皮」「肤」，所以中文不用「皮肤」二字。
- 存档：PlayerData 键 `vrck.set.ballSkin`，存**原作编号**（1–29，无 26/27），不存列表下标；可用皮肤随复制了哪些素材而变，编号含义不变。读到 26/27/越界按 core_01。
- 切换：[HubSettingsHost](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/Hub/HubSettingsHost.cs) 的 ◀ ▶ 在 `ballView.GetSkinNumbers()` 里循环（[BallSkinMath](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallPresentation/BallSkinMath.cs)）；恢复存档后，存的编号本构建没装时显示 core_01（存档不改，直到玩家再按）；每次变化或恢复都调 `BallView.SetSkin(编号)`，它只改各 `core_NN` Renderer 的 `enabled`，同时只开一个，不碰缩放、碰撞、滚动。`ballView` 未接时设置照存，按原作 27 个编号循环，球不变。
- 范围：只换本地玩家看到的那个球。合集里只有一个共享球（KatamariBallMotor 不同步，SyncMode None），别人看到的仍是他们自己客户端的选择；远端皮肤同步是后续工作。

### 内存与渲染

- 每个皮肤：一个 Transform + MeshFilter + MeshRenderer；关掉的 Renderer 不进剔除/绘制，每帧开销只有启用的那一个，与单皮肤相同。切换只在按箭头或恢复存档时发生，Udon 里是一次长度 27 的循环。
- 网格（本机 OBJ 实测）：core_01 2280 顶点/3000 三角。Read/Write 开时 CPU 与 GPU 各一份；按位置+法线+UV 32 B/顶点、16 位索引估算约 2280×32 + 9000×2 ≈ 90 KB/份，27 个最多约 2.4 MB×2（有共享网格时更少；obj 文件 117–398 KB，顶点数相近）。
- 贴图：26 张 128×128、1 张 256×256 RGBA；无压缩 64 KB/张（256 的那张 256 KB），全部约 1.9 MB，带 mip 约 2.5 MB；用 DXT5 压缩约四分之一。
- 合计额外内存估算 ≤ 约 7 MB（未经 Unity Profiler 实测）。

## Unity 操作员

1. 由操作员将仓库外 [KatamariCore/original](</home/fulutang/KatamariRef/models/KatamariCore/original/>) 下全部 `core_NN.obj`、`core_NN.mtl`、`core_NN.png`（27 套 81 个文件，约 10 MB；Windows 上没有副本，从 Linux 用 scp 传）**同名**复制到 Windows Unity 工程的 `Assets/_RefUnlicensed/Models/KatamariCore/`，保持同目录、相对文件名不变。不要放入 VRC_Katamari 正式内容目录，不提交原始素材或其引用场景，不上传。
2. OBJ Model：Scale Factor 1（不再手工乘 100/0.01），Read/Write Enabled **开**，Mesh Compression **Off**，Keep Quads **关**，Generate Colliders **关**；Normals **Import**（无源法线则 Calculate），不生成动画，不更改 UV。材质：Material Creation Mode **Standard**（非 None），优先 **Use Embedded Materials**，Apply。PNG：Texture Type **Default**、sRGB **开**，不当 normal map；初轮可用 Point、Compression None 便于核对像素花纹（此过滤是项目检查设置，不称原作实测）。
3. 每个 OBJ 都按第 2 步设置（可多选一起改）。在导入 OBJ 的 MeshRenderer 预览中确认唯一材质 `Mat` 的 Albedo/Main Texture 指向同目录同编号的 core_NN.png。MTL 的 `map_Kd` 即此文件。若自动搜图未命中，Extract Materials **到同一 _RefUnlicensed 子树内**，手工把 PNG 赋给材质 Albedo，再在 OBJ Materials Remapped Materials 中映射 `Mat`；不要编辑/覆盖项目其它同名 Mat。装配器只接受这张精确贴图，不替操作员修 importer。
4. 打开正确的参考球场景，检查 BallView.coreMesh 接线与祖先单位缩放，先保存已有改动，等待编译/import 完成。运行 `VRC Katamari/Ball/Assemble Core Skin`；返回行应列出 `skins=27` 且没有 `skipped:`；审阅层级（`OriginalCoreSkin` 下 27 个 `core_NN`，只有 core_01 的 Renderer 开）与 Console 后保存，再运行一次确认 **+0 ~0**。不要将引用未授权资源的场景带进提交/上传。
5. 执行 Unity EditMode `CoreSkinTests` 与既有 BallPresentation 回归；Play 后核显示**等体积直径 = 起始球径 0.222 m**（凹凸 AABB 不等于 Ø0.222），材质带彩色贴图。吸收 0/10/20/30 件后，核显示等体积直径 = 当前碰撞直径（偏差 ≤0.1 mm）；不要用加了 0.2 mm 偏置的 HUD 数字作精确比对。整个核照旧滚动，吸附根与已吸道具不被缩放。离线检查不能代替 UdonSharp/Unity/场景序列化验收。

导入设置依据 Unity 2022.3 官方手册：[Model tab](https://docs.unity3d.com/2022.3/Documentation/Manual/FBXImporter-Model.html)、[Materials tab](https://docs.unity3d.com/2022.3/Documentation/Manual/FBXImporter-Materials.html)、[纹理搜索与导入](https://docs.unity3d.com/2022.3/Documentation/Manual/ImportingModelFiles.html)。OBJ 的实际导入材质仍以操作员在 2022.3.22f1 看到的绑定为准。

## Owner 观感检查

开局应是有凹凸花纹的小核，不再是光滑纯红球；滚动时图案随核刚性转动。吸收后核随碰撞直径直接变大，无弹簧拖尾；粘附物应贴在花纹核表面附近，不再围着小核空出一整圈。观察凹凸谷部的小间隙、凸起处的穿插是否可接受；旧吸附件被成长核逐渐包住不等于位姿漂移。不做向心嵌入或附件外推。当前卷不动的实体仍应挡路，长大后满足门槛才可卷起。

## 测试边界

[CoreSkinTests](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/BallPresentation/CoreSkinTests.cs) 有 12 个纯值用例：右四面体体积（不是 AABB）、三档单位尺度、平移不变、反绕序与非法/退化输入、`core_NN` 子节点名解析；另 1 条 preview-scene 用例装两个共享网格的皮肤，检查容器单位变换、只有 core_01 启用、网格/材质引用、中心补偿、无 Collider、原核禁显、第二次 +0 ~0、素材删掉后子节点被删，以及 runtime scale/roll 不被写；它依赖 Unity 引擎，本地不可执行不算通过。纯值测试不读取或分发参考资产。

[BallSkinMathTests](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/BallPresentation/BallSkinMathTests.cs) 覆盖编号表（27 个、无 26/27）、可用列表循环、存档归一、无列表时按原作编号循环、标签格式；`HubSettingsMathTests.BallSkinIsStoredAsTheOriginalCoreNumber` 固定存档键与读回规则。

[BallPresentationTests](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/BallPresentation/BallPresentationTests.cs) 用 GameObject + 反射驱动 BallView 和 motor 尺寸重算，覆盖未就绪不写、两档起径、吸收成长、缩小、微小变化、重复更新与无效半径，以及滚动根/附件根不缩放；这些用例需要 Unity。
