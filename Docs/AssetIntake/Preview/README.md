# Windows Blender 素材预览：第一批12项

日期：2026-10-11。**用途仅为素材检查；不是Unity实机、最终美术方向、地图布局或宣传成品。**

![12项素材检查图](<renders/contact_sheet.png>)

每格独立按最大包围尺寸归一化展示，不能比较格间真实尺寸或决定拾取等级。相机/灯光为技术检查用途；所有网格保留源材质，未为统一风格改色。Cycles CPU 4线程、16samples，个别噪点不是源贴图。

## 获取与渲染证据

- 只使用已记录包级CC0证据的Kenney Food/Nature小包，原包SHA256与[包清单](<../DirectDownload/manifest.json>)一致；不下载其他资源。
- [抽取/GLB预检脚本](<../../tools/asset_preview/stage_glb.py>)先重新检查整ZIP元数据/哈希，再限定读取12个GLB与一份已知PNG；没有全包解压，不执行包内程序或`.blend`。
- Food GLB原文件有精确相对URI `Textures/colormap.png`；最初“仅内嵌”检查如实拒绝。确认[文件库存](<../DirectDownload/inventory.csv>)中的包内PNG后，只对这个精确URI按白名单嵌入纹理到**新预览副本**，再做拒绝所有URI的预检。原始归档、源码GLB都未改。
- [选择清单](<selection.json>)同时记录来源成员SHA256、预览副本SHA256、调色板SHA256与PNG头部尺寸。12个副本共214,188字节。PNG头部限额不是完整解码器安全证明；解析器/Blender漏洞仍不受本工具绝对保护。
- Windows既有Blender版本为 **5.2.2 LTS**。独立进程使用 `--background --factory-startup --disable-autoexec --python-exit-code 2`，[自写渲染脚本](<../../tools/asset_preview/render_windows.py>)导入GLB而非`.blend`，渲染后退出码0；具体安装路径未保留在公开说明中。
- Windows使用新建的独立隔离目录，位于Unity工程之外；旧批次和既有GUI会话均未覆盖或操作。素材副本按检查目录策略保留，不自动删除；个人目录、GUI进程号与程序路径不对外列出。
- Linux原始素材副本留在ignored `Docs/AssetIntake/Downloads/preview_staging/batch2/`；本目录只保留PNG、选择/测量报告及说明，没有GLB或`.blend`进入Unity。
- **本轮未使用或配置Blender MCP**；MCP端点/本地服务配置未写入本说明。

## 实际检查结果与概念映射修订

| 素材 | 导入后三角面数 | 当前观察与后续处理 |
|---|---:|---|
| apple | 136 | 苹果外形可辨；正式尺寸/Collider待定。 |
| banana | 104 | 弯曲香蕉；长细物件要验证方向/形状代理，不直接按包围盒体积给收益。 |
| strawberry | 156 | 草莓外形可辨；叶片不能自动成为独立收益实体。 |
| bread | 116 | **面包片，不是整条面包**；PIC-11 bread loaf仅部分概念匹配，应找新文件或改变物件设计。 |
| croissant | 162 | 可辨弯曲卷纹形态；可作为候选，尚未人工美术批准。 |
| cookie | 68 | 厚圆饼/碎块外形；未测UV在Unity的表现。 |
| cheese-cut | 160 | **带孔薄片，不是奶酪楔块**；PIC-15仅部分概念匹配。 |
| soda-can | 164 | 当前视角为无可见文字标识的红色罐体；不是全角度/商标清权证明。 |
| pot_small | 72 | **空花盆**，不是已组合的盆栽；PIC-28必须单独组装/决定拾取粒度。 |
| rock_smallA | 16 | 橙色多面体带绿顶；不能直接声称符合天然鹅卵石主题，材质/风格另审。 |
| mushroom_red | 48 | 红顶白柄；顶与柄在源单网格中，不按材质数量生成多次收益。 |
| flower_redA | 76 | 简化红花绿叶；是否与花盆作为一个物件须游戏定义，不自动组合。 |

全部导入各1个Mesh；Food各1材质，Nature分别3/2/2/2材质。材质slot、UV与三角数只是当前Blender glTF导入结果，不保证Unity导入一致、不说明平台总预算通过。精确顶点数、边界、材质数、UV层、纹理尺寸、源对象矩阵与determinant在[测量报告](<renders/render_report.json>)。

## 坐标与尺度边界

- `import_extent_blender_units`记录**归一化之前的Blender导入包围尺寸**，不是认证实物米制尺寸，也不是`pickupDiameterWorldM`/`collisionRadiusWorldM`。
- 导入转换由实际Blender glTF importer执行，未盲目加90°、镜像或负缩放；测得对象matrix determinant均+1不证明源轴/pivot语义，也不能排除网格内烘焙方向问题。
- 新的display-only root做 `p_display = s*p_import + (-s*centerX,-s*centerY,-s*minZ)`，其中 `s = 1.65 / max(import_extent)`，保持已导入几何/父子链，其目的只是居中/落地/排版。
- 源单位承诺、源top/front/pivot独立锚点、跨Unity完整转换链和碰撞/拾取尺度未核验，状态仍**OPEN**。原报告保留数据以供下一轮核验，不声称轴映射VERIFIED。

## 复现与未做事项

```sh
python3 -m Docs.tools.asset_preview.stage_glb
```

脚本有意拒绝覆盖已存在staging与证据文件；复现应在新的隔离副本/明确新批次目录内运行，不绕过保护覆盖旧证据。

Windows后台渲染参数示例（只对本轮隔离输入）：

```text
blender.exe --background --factory-startup --disable-autoexec --python-exit-code 2 --python render_windows.py -- INPUT NEW_OUTPUT
```

渲染输出也拒绝覆盖；原配置[命令行文档](https://docs.blender.org/manual/en/latest/advanced/command_line/arguments.html)不提供导入器漏洞绝对保证。没有Unity/Udon、运行时Collider、真人VR、多客户端、性能、成片、付费或正式分发测试；这些全部NOT_RUN。不修改原[第一批快照](<../../15_OFFLINE_PREPARATION_REVIEW.md>)，最新观察以本页为准。

## P21受控复测：同一12项第二次运行（2026-10-11）

首批渲染由主管实际操作；上方旧 `renders/` 与[首批快照](<../../15_OFFLINE_PREPARATION_REVIEW.md>)保留为历史，不覆盖。以下是P21由asset-acquisition执行的独立复测，不替代或改写首批记录：

![P21受控复测12项接触图](<verified_renders/contact_sheet.png>)

- [P21复测报告](<verified_renders/render_report.json>)和12张独立PNG位于 `verified_renders/`；本目录共14个文件。Windows在新的独立、Unity工程之外的隔离目录渲染，回收输出到新的 `verified_renders/`，没有覆盖旧批次。
- 复用了已审的12个GLB与selection，不重新运行stage工具、不解压或获取新包。上传前12个副本均与selection内的大小/SHA256相符；staging和审核用selection.json SHA256均为 `b786423666745db89615aafe76b19512d7b7a1eb6b54d0a1dc7f570da0b9d68e`。上传的[渲染脚本](<../../tools/asset_preview/render_windows.py>)SHA256为 `b0844cf6d75a8f86abb16577491a4a38777cf95269cc7f579723cb5c331e0ac1`，与Windows文件一致；渲染输出14个文件也逐一核对了远端/本地SHA256。
- 通过既有受信SSH节点连接；保留known_hosts/主机密钥校验且无警告。节点显示名登记与旧记录不同，连接公钥校验、既有Blender GUI进程和程序路径与此前记录一致，主管核准同设备。GUI未关闭或操作；地址、用户、实际hostname、进程号和安装路径未保留于本公开说明。
- 独立后台命令实际使用 `--background --factory-startup --disable-autoexec --python-exit-code 2`，以P20修订的单文件渲染脚本导入GLB。Blender实际版本 **5.2.2 LTS**；Cycles CPU 4线程、16 samples；`PREVIEW_BATCH_COMPLETE 12`，进程退出码 **0**。仅有 `World.use_nodes`、`Material.use_nodes` 在未来Blender 6.0移除的DeprecationWarning，未影响本次渲染。
- 画面核查：12项均有单独渲染，几何轮廓与对应选中名称相符；bread仍是面包片而非面包条，cheese-cut仍是带孔薄片而非楔块，pot_small仍为空花盆，rock_smallA仍是橙色多面体带绿顶。这些是实测形状/概念不匹配，不宣称已找到替代物。flower_redA和mushroom_red局部可见颗粒/噪点；保留为16-sample技术预览结果，未优化或进一步定因。
- 单位与轴语义仍 **OPEN**：报告记录的是归一化前的Blender导入单位；每项单独归一化仅为排版/目视检查，不等于米、拾取直径或碰撞半径。源单位承诺、pivot/axis语义与完整Unity转换链仍未核验；不要把本图称为游戏画面、最终美术方向或已验收资产。没有运行Unity/Unity MCP，也没有操作已有GUI。
