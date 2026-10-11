# P10 真实文件覆盖与后续素材需求（草案）

状态：文本/机器数据草案；不构成T02/T20素材批准、每文件许可验收或Unity导入。Unity由用户在Windows操作，本任务不启动Unity。项目路径为 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`；MCP未确认。本目录是本任务唯一写入范围。

## 数据与证据分层

- [`coverage.csv`](coverage.csv)：把原36个 `PIC-01`–`PIC-36`概念逐一映射到两份已下载包的ZIP成员元数据及最多12项已渲染预览。`file_metadata_status`、`visual_review_status`、`concept_fit_status`分列，避免把文件名命中冒充目视确认。
- [`coverage.schema.json`](coverage.schema.json)：每条coverage记录解析为字符串字段对象后的JSON Schema；使用[`validate_authoring.py`](../AssetAuthoring/validate_authoring.py)的标准库子集检查CSV行、唯一ID与状态字段（不是通用JSON Schema实现）。
- [`next_download_needs.csv`](next_download_needs.csv)和[对应schema](next_download_needs.schema.json)：只写未来需求/决策条件，不指定新包、URL或下载动作；每行`new_package_download_authorized=NO`。
- 输入为只读的[36概念候选表](../../Research/AssetManifest/picnic_candidates.csv)、[P02包与概念快照](../../AssetIntake/DirectDownload/manifest.json)、[完整ZIP成员元数据库存](../../AssetIntake/DirectDownload/inventory.csv)，及[P21预览观察](../../AssetIntake/Preview/README.md)、[12项复测报告](../../AssetIntake/Preview/verified_renders/render_report.json)。P10未修改原包、staging、PNG、selection或render_report；P21 README/handoff另有task-26授权的隐私匿名化修订。

## 覆盖口径

1. `EXACT_NAME`只表示库存路径/文件名直接命名概念，不表示图片看过、网格符合、美术批准、尺度/Collider合适或权利已逐文件通过。`PARTIAL_NAME`/`METADATA_CANDIDATE`只表示可能候选，需要目视或概念核对；`NOT_FOUND`只表示两个冻结包的当前库存未找到适用名称，不表示全网不存在。
2. `RENDER_CONFIRMED`、`PARTIAL`或`RENDER_MISMATCH`仅用于P21实际渲染的12个选择项。其余有候选文件的记录是`NOT_RENDERED`，不能把manifest里的旧映射或文件名当作视觉证据。
3. 已核对的真实差异包括：bread.glb是面包片而不是bread loaf；cheese-cut.glb是带孔薄片而不是cheese wedge；pot_small.glb是空盆，flower_redA.glb是另一文件，故未确认一个small potted plant实体；rock_smallA.glb为橙色多面体带绿顶，不确认天然pebble。`pot_large.glb`仅有ZIP文件名元数据，本次未渲染它。
4. P02 manifest对PIC-16及PIC-19–PIC-22曾记录`unknown`；本次重新对照Food Kit ZIP库存发现`carton*.glb`、`cup*.glb`、`plate*.glb`、`bowl*.glb`及spoon文件名候选。coverage.csv记录此修正，但仍标为未目视/未确认用途，不回写冻结的manifest或旧候选表。
5. PIC-03 small fruit与PIC-04 apple命中同一`apple.glb`，因此提议不同语义定义ID并保留是否合并/扩展品类的待决策项。来源文件不是稳定Definition ID；同理，多个来源文件可以映射同一概念，必须人工选择。
6. P02包内CC0是包级证据；各候选的第三方标识、肖像/商标、每文件内容和特定世界分发仍需人工审查。所有世界米制、拾取直径、成长值和Collider均未由本coverage核验。P21单项归一化和Blender单位不能转成玩法尺度。

## 后续使用

先确认概念是否仍保留，再检查已有未渲染候选；若当前包确实没有符合需求的文件，先批准源搜索和许可边界，再另行批准下载。不得从`next_download_needs.csv`自动抓包。未经选择的源文件不进入Unity，不在本任务中制作替代物或冻结正式definition/实例协议。
