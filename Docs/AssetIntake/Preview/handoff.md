# P21 受控 Blender 复测 handoff

日期：2026-10-11。任务 task-14（P21），依赖 task-13/P20 已完成；主管明确放行后执行。由 asset-acquisition 作为唯一 Windows Blender 操作者完成；主管审图待办。

## 范围与输入

- 写入范围仅为 `Docs/AssetIntake/Preview/`；`Docs/AssetIntake/Downloads/preview_staging/` 仅只读复用。未修改脚本、测试、tasks.json、公共计划或 Unity 工程。
- 首批12项由主管实际操作。首批 `renders/`、首批报告和[第一批快照](<../../15_OFFLINE_PREPARATION_REVIEW.md>)未覆盖或改写。本次是第二次、独立复测。
- 复用已审核的12个 GLB 与 selection，不重新运行 `stage_glb.main`，不解压、不下载新素材、不执行素材包代码。12个本地 GLB 的文件数、大小及 SHA256 均与审核 selection 匹配；staging 与审核用 `selection.json` 完全相同，SHA256 `b786423666745db89615aafe76b19512d7b7a1eb6b54d0a1dc7f570da0b9d68e`。
- [P20 renderer](<../../tools/asset_preview/render_windows.py>)上传前 SHA256 `b0844cf6d75a8f86abb16577491a4a38777cf95269cc7f579723cb5c331e0ac1`；Windows 上传文件的哈希相同。脚本部署于独立隔离的目录层级，保留预期的 `parents[3]` 深度；具体Windows绝对路径不对外记录。

## Windows 运行记录

- 通过既有受信SSH节点连接，保留known_hosts/默认主机密钥校验，无host-key changed警告；没有更换key、配置或凭据。节点显示名登记与旧记录不同，连接公钥校验及既有Blender GUI进程/程序路径与此前记录一致，主管核准同设备。对外版本不保留节点地址、用户名或hostname。
- 运行输出确认既有Windows Blender版本为 **Blender 5.2.2 LTS**。运行前后，既有Blender GUI进程均仍在且程序路径与先前核验记录一致；未关闭或编辑原会话，本次另起独立后台进程。个人GUI进程号及程序绝对路径已从公开说明省略。
- 唯一渲染运行由 DSH 托管后台 job `bash-208` 执行，命令参数：`--background --factory-startup --disable-autoexec --python-exit-code 2 --python <reviewed render_windows.py> -- <input> <new renders>`。renderer 在读取输入/创建输出前检查后台模式和启动flags；P20依赖修复版本的哈希/allowlist/尺寸与资源门禁均通过，产生日志 `PREVIEW_BATCH_COMPLETE 12`。
- 在新的独立、Unity工程外Windows隔离目录中，仅上传单文件renderer、12个批准的GLB与`selection.json`至`input/`。输出仅创建在新 `renders/`；第一批Windows输出和旧Linux `Preview/renders/` 均未覆盖。实际Windows文件夹路径不对外记录。
- 实际引擎：Cycles CPU 4线程、16 samples；进程退出码 **0**。仅出现 `World.use_nodes` 与 `Material.use_nodes` 在未来Blender 6.0移除的DeprecationWarning，不影响完成状态。两个早期只读PowerShell文件检查命令因引号转义问题报解析错误（exit 1）；未触发任何文件或进程操作，改用简化的只读哈希/目录查询成功。
- Windows输出的12张单图、contact sheet、render_report共14个文件已回收到[`verified_renders/`](verified_renders/)。逐个比较Windows与本地SHA256一致；JSON报告结构/名称/12项数量正确，14个文件均为预期集合且PNG签名正确。接触图：[contact sheet](verified_renders/contact_sheet.png)。

## 目视结果与边界

- 12个物件均有单独渲染，外观与选择名称相符；无脚本异常或缺图。flower_redA和mushroom_red局部可见颗粒/噪点；保留16-sample技术预览结果，不优化或推断噪点成因。
- 实测形状/概念不匹配仍需设计/资产决策：`bread`是面包片而非整条面包；`cheese-cut`是带孔薄片而非奶酪楔块；`pot_small`为空花盆而非盆栽；`rock_smallA`为橙色多面体带绿顶。未声称找到替代资产。
- `render_report.json`内`import_extent_blender_units`是归一化前Blender导入单位，不是米、拾取直径或碰撞半径。源单位、pivot/axis语义、完整Unity转换链仍为 **OPEN**；画面不作为玩法尺寸、Unity导入、Collider、最终视觉方向或宣传成片证据。
- 未启动Unity，未用Unity/Blender MCP，未改变MCP配置，未安装软件、下载新包、操作账户/付费、提交或推送Git。PNG/GLB校验仅为受限检查，不构成完整解码器/导入器沙箱或完整GLB/PNG标准验证。

## 主管审查

待主管检查新接触图与报告，并决定是否接受本轮受控复测证据。P21只表示预览流水线复测成功，不代表任何正式T任务或素材验收通过。
