# 固定原作球芯皮肤与可选装配

现行决定：**owner 2026-11-17，球皮肤用原作球芯，先用 core_01；显示等体积直径固定为本关起始球径，不随成长缩放。** 原作关卡与 core_XX 的逐关对应关系未知。参考模型/贴图只供本机参考，不进 git、不上传；引用它们的装配后场景同样不得提交或发布。

## 研究依据与边界

- **本机网格实测**：[核心研究 E2](</tmp/core_radius_findings.md#L59>)：27 个 OBJ（01–29，无 26/27）都是约 5 cm 的凹凸球，包围盒约 4.8×4.9×5.4 cm，等体积径约 4.65 cm；core_01 约 52.6 cm³。13 个文件顶点逐个相同，其余仅微变；不是尺寸梯度。
- **命名文献**：[E3](</tmp/core_radius_findings.md#L60>)，本机 [practice mod README](</home/fulutang/KatamariRef/meta/pages/github_il_practice_mod_README.md#L87-L89>)：“Most models are named after the levels where they're used in the unmodified game.” core_XX 按关卡命名，是皮肤而不是尺寸档。
- **专利文献**：[US7402104B2](https://patents.google.com/patent/US7402104B2/en)、[本机研究 E5/E7](</tmp/core_radius_findings.md#L62-L64>)：Mp 是无吸附件的初始显示模型；S50–S58 记录接触位姿、加体积、放大 Mh、变形 Mr，无放大 Mp 步骤。吸附位置在接触处或其附近。专利实施例不证明零售版逐字沿用。
- **录像实测的范围**：[ORIGINAL_ATTACH_LOOK b1](../../Research/KatamariBallRules/ORIGINAL_ATTACH_LOOK.md)：PS2 480p，HUD 5.0→6.9 cm 时芯宽约不变，之后被遮住；整局不变是结合网格/专利的读法，不是全程像素测量。

原作成长后碰撞球远大于小核，但大量小件填满其间的空腔。[本机研究 Q4](</tmp/core_radius_findings.md#L39-L48>) 把“浮”的主要原因归于件相对球大而少、填充密度低；这是实测加推断，不是已经完成的内容改造。**owner 2026-11-17：不做粘附嵌入，保持接触处、贴碰撞球面；靠件小量多和填满空腔处理观感，不向里压。** 接触与挡路现行规则见 [ATTACH_PLACEMENT §3](../BallLayers/Attachment/ATTACH_PLACEMENT.md#3-现行设计)。

## 实现与缩放合同

唯一实现：[CoreSkinAssembler](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/BallPresentation/CoreSkinAssembler.cs)，菜单 `VRC Katamari/Ball/Assemble Core Skin`。只显式运行，不在加载/导入回调里执行；只扫描当前活动场景内（含 inactive）的 BallView，按它序列化的 coreMesh 引用定位，不按对象名猜。

[BallView](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallPresentation/BallView.cs) 保持不变：马达就绪后仅一次写 `coreMesh.localScale = Vector3.one * motor.GetStartDiameterWorldM()`；旋转仍由原滚动根提供。归一化放在其显示子节点 `OriginalCoreSkin` 上，不增运行时字段，不改碰撞/滚动根/吸附根/道具尺度。

导入 mesh-local 顶点的闭合一致绕序网格体积 `V = abs(Σ det(a-o,b-o,c-o))/6`（double 累加，o 为首顶点）。等体积径 `D_mesh = (6V/π)^(1/3)`，子节点均匀缩放 `s = 1/D_mesh`，平移 `-mesh.bounds.center * s`，局部旋转 identity。所以 `D_display = D_mesh × s × D_start = D_start`。凹凸核的包围盒宽高深不会全等于这个数，**不要把最大 AABB 边当成核显示等体积径**。core_01 原始单位若保持米，s 约 21.5；导入器若烘焙单位倍率，s 相反变化，最终直径不变。

显示子节点直接引用导入的 Mesh 和带贴图的 sharedMaterial；不实例化导入 prefab 的 root scale/轴向补偿 wrapper，不叠加第二次单位转换；UV、法线和顶点同一 mesh-local 坐标系保留。现有 CoreMesh 的旋转和整个滚动父链原样保留，花纹朝向由原 mesh-local 定义，不宣称复现原作某一关的朝向。所有 CoreMesh 祖先必须单位缩放，否则拒绝整批。现有纯色球的 MeshRenderer 仅 disable、不删除，原网格/材质保留；没有新 Collider。

预检还包括：非 Play/Prefab Mode、未编译/导入、活动场景已加载、没有待保存改动、一个可读 triangle submesh、导入材质确实指向指定贴图，以及没有自定义/重名的 `OriginalCoreSkin`。通过后才开一组 Undo；异常整组回滚。装配器不写导入设置、不生成网格或材质、不保存场景；只有真实变化才标记场景 dirty，操作员审阅后保存。

缺少参考 OBJ 或 PNG：`+0 ~0. Note: optional core_01 model/texture missing; existing core left untouched.` 不改任何场景对象、资产或导入器；若已有皮肤也不破坏现状。导入不完整/接线不符合预检时同样 Note 并零写入，不静默装纯色材质。正确装配后再次运行（先保存）应 `+0 ~0`，计数是新增显示 GameObject 数 / 实际改变的属性组数。

## Unity 操作员

1. 由操作员将仓库外 [core_01.obj](</home/fulutang/KatamariRef/models/KatamariCore/original/core_01.obj>)、[core_01.mtl](</home/fulutang/KatamariRef/models/KatamariCore/original/core_01.mtl>)、[core_01.png](</home/fulutang/KatamariRef/models/KatamariCore/original/core_01.png>) **三个同名文件**复制到 Windows Unity 工程的 `Assets/_RefUnlicensed/Models/KatamariCore/`，保持同目录、相对文件名不变。不要放入 VRC_Katamari 正式内容目录，不提交原始素材或其引用场景，不上传。
2. OBJ Model：Scale Factor 1（不再手工乘 100/0.01），Read/Write Enabled **开**，Mesh Compression **Off**，Keep Quads **关**，Generate Colliders **关**；Normals **Import**（无源法线则 Calculate），不生成动画，不更改 UV。材质：Material Creation Mode **Standard**（非 None），优先 **Use Embedded Materials**，Apply。PNG：Texture Type **Default**、sRGB **开**，不当 normal map；初轮可用 Point、Compression None 便于核对像素花纹（此过滤是项目检查设置，不称原作实测）。
3. 在导入 OBJ 的 MeshRenderer 预览中确认唯一材质 `Mat` 的 Albedo/Main Texture 指向同目录的 core_01.png。MTL 的 `map_Kd` 即此文件。若自动搜图未命中，Extract Materials **到同一 _RefUnlicensed 子树内**，手工把 PNG 赋给材质 Albedo，再在 OBJ Materials Remapped Materials 中映射 `Mat`；不要编辑/覆盖项目其它同名 Mat。装配器只接受这张精确贴图，不替操作员修 importer。
4. 打开正确的参考球场景，检查 BallView.coreMesh 接线与祖先单位缩放，先保存已有改动，等待编译/import 完成。运行 `VRC Katamari/Ball/Assemble Core Skin`；审阅层级与 Console 后保存，再运行一次确认 **+0 ~0**。不要将引用未授权资源的场景带进提交/上传。
5. 执行 Unity EditMode `CoreSkinTests` 与既有 BallPresentation 回归；Play 后核显示**等体积直径 = 起始球径 0.222 m**（凹凸 AABB 不等于 Ø0.222），材质带彩色贴图。吸收 0/10/20/30 件后核尺寸恒定；整个核照旧滚动，吸附根与已吸道具不被缩放。离线检查不能代替 UdonSharp/Unity/场景序列化验收。

导入设置依据 Unity 2022.3 官方手册：[Model tab](https://docs.unity3d.com/2022.3/Documentation/Manual/FBXImporter-Model.html)、[Materials tab](https://docs.unity3d.com/2022.3/Documentation/Manual/FBXImporter-Materials.html)、[纹理搜索与导入](https://docs.unity3d.com/2022.3/Documentation/Manual/ImportingModelFiles.html)。OBJ 的实际导入材质仍以操作员在 2022.3.22f1 看到的绑定为准。

## Owner 观感检查

开局应是有凹凸花纹的小核，不再是光滑纯红球；滚动时图案随核刚性转动，无额外旋转或缩放跳变。成长后核仍固定，外壳由吸附物累积而不是核膨胀。分别观察“物件相对物理碰撞球面是否贴住”和“固定核外的空腔是否因小件太少显空”，不要混成一个半径问题；本轮不承诺换皮能解决稀疏堆积。不做任何向心嵌入。当前还卷不动的实体应挡路，长大后满足门槛才可卷起。

## 测试边界

[CoreSkinTests](../../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/BallPresentation/CoreSkinTests.cs) 有 6 个纯值用例：右四面体体积（不是 AABB）、三档单位尺度、平移不变、反绕序与非法/退化输入；验证归一化后 ×0.222 的结果。另 1 条 preview-scene 用例检查实际网格/材质引用、中心补偿、无 Collider、原核禁显、第二次 +0 ~0，以及 runtime scale/roll 不被写；它依赖 Unity 引擎，本地不可执行不算通过。纯值测试不读取或分发参考资产。
