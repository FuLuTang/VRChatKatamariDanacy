# P11 物件颗粒度、稳定ID与加工规格草案

状态：**host-only预生产提案**；schema与examples用于QA字段一致性，不是Unity资产、Udon序列化格式或已冻结接口。Unity在Windows，Editor归用户；项目路径 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`；Unity MCP未确认。本任务不接触工程或Unity。

## 文件

- [`asset_authoring.schema.json`](asset_authoring.schema.json)：JSON Schema for a host-side proposal bundle.
- [`examples.json`](examples.json)：带已审核Apple预览证据、仅ZIP文件名候选、空缺占位定义、spawn关系、支撑移除/掉落/重生/补货案例的数据示例。它没有实际位置、米制尺寸、Collider尺寸、价值或预算数字。

用`python3 Docs/Preparation/AssetAuthoring/validate_authoring.py`运行只依赖Python标准库的可复现QA；它验证本项目使用的JSON Schema子集与P10/P11数据引用，不是通用JSON Schema实现。CSV覆盖/来源见[AssetCoverage](../AssetCoverage/README.md)，本文件不修改首两批直接下载、预览或staging证据。

## 身份与颗粒度

- **ItemDefinition**是语义上可重复使用的定义，建议稳定ID采用`picnic.<domain>.<concept>[_variant]`命名空间；它不由源文件名、数组顺序、Mesh数量或材质slot数生成。示例PIC-03 small fruit和PIC-04 apple都命中`apple.glb`，但先各保留语义ID，等待决定是否合并/拓展；如需改ID，未来要显式迁移/别名记录。
- **ItemInstance**是地图里的一次摆放，有独立稳定`spawnId`、引用`definitionId`、生命周期`spawnGeneration`和`stateRevision`。两颗苹果共用一个definition、各自不同spawnId；重生/补货按批准的生命周期换新generation，旧请求不可作用于新代。
- **实体数是玩法决定**：40个Mesh可仍是一个椅子实体；一个文件可以是复合物，也可以拆成杯/碟等实体。`meshCount`、材质slot、Renderer或源文件数量不能自动决定可拾取数量、收益或Collider数量。

## 杯/碟/桌/椅的决定点

1. 若`cup-saucer.glb`实际确认是组合且设计批准为一个可拾取物，用一个语义definition和一个ItemInstance；不因内部Mesh/material slots多而发多次收益。当前只有ZIP文件名元数据，几何/边界未目视，故样例为候选而非结论。
2. 若杯与碟分别可拾取，给`picnic.tableware.cup`与`picnic.tableware.plate`各自definition与各自spawnId，并可引用同一个`supportParentId`。当前库存有`cup.glb`、`plate.glb`等名字，但这些未在P21预览，必须后续目视决定。
3. 桌子可由设计明确为固定地标/支撑父级、可拾取实体，或装饰组合；椅子同理。某一父物被收走时，先决定子件随父一起收（且逐件符合资格）、按统一权威落点掉落、脱离，或保持固定。不得只因模型父子层级而默认为一个收益实体。椅子用于规则示例，不属于原36行coverage，未声称存在候选文件。

## Render / Collider / Support 与尺度

- **Render mesh**只表达视觉和LOD；**gameplay collider proxy**单独选择简化碰撞；**shape/support/extreme proxy**用于支撑/轮廓候选，三者不互相推导。
- `collisionRadiusWorldM`、`pickupDiameterWorldM`、`gainVolumeUnits`及源unit、top/front、pivot/轴向都保持null/OPEN，除非后续测量与审核。P21每个物件按Blender bounds单独归一化只为排版，不等于真实米数/游戏尺寸；只见到Blender导入单位的报告不能定世界尺度。
- Render测量的Mesh、三角面、材质slot、UV与纹理数是文件/导入器观测，不是实体数、逐文件权利通过、Unity导入一致性或平台性能通过。
- 长细香蕉、汤匙、薄奶酪等不能用AABB体积直接代表成长收益。长杆/薄片可需要手工极值/支撑代理，玩法碰撞仍必须在获批/验证前与外观分开；在凹凸物理门禁前，共享PVP仍用已设计的统一球形碰撞方案。

## 支撑、掉落、重生和补货检查案例

`examples.json`只列确定性审查条件，真实参数留空：摆放时父项必须存在且支撑图无环；父项移除前明确子项策略；改变争抢位置的坠落使用权威/共享结果而非客户端各自PhysX；掉落/KO/交分只对确认账本记一次；重生保留`spawnId`但使用新generation并拒绝旧代请求；补货设置有限zone/tier预算、避免在球内生成并能提供生成预告。每项数值/周期仍待地图和玩法校准；这里没有设置预算或获批默认。
