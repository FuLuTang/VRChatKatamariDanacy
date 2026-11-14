# VRChatKatamariDanacy / VRC塊魂

VRChat 原创滚球、吸附成长与交分 PVP 项目。内部名 VRC塊魂；公开名称待定，屎壳郎是主题方向，不使用原作角色、音乐或品牌资产。

## 当前状态

**设计包 v0.3 已导入；Windows 上的 VRChat Worlds 工程已初始化（T00 完成），可经 MCP 直接驱动 Unity。Station 平台实验（T04）PC 部分已通过，VR 部分待真人头显。球层逻辑正从离线模型迁入 Unity 运行时代码。**

- PCVR + Desktop；2–4 人休闲 PVP，单人练习。
- VR A 双虚拟推杆；VR B 摇杆追尾；Desktop WASD/鼠标。
- 单槽磁铁/火箭，Jump 使用道具；共享世界坐标、确认收益账本。
- 先一图两人完整纵切片，首发目标野餐草坪与玩具房；夜市后置。
- 完整玩法免费，未来仅纯外观内购；真实收费默认不启用。
- 真人 VR、多人联网、性能和购买测试均未运行。

## 导航

1. [范围与决策](<Docs/00_SCOPE_DECISIONS.md>)
2. [技术设计（尚未冻结为 contracts v1）](<Docs/02_TECHNICAL_CONTRACTS.md>)
3. [执行阶段](<Docs/04_EXECUTION_PLAN.md>) / [机器可读任务表](<tasks.json>)
4. [验收门禁](<Docs/05_ACCEPTANCE_TESTS.md>)
5. [Agent规则](<AGENTS.md>) / [原执行规则](<Docs/07_AGENT_OPERATING_RULES.md>)
6. [初始化解析与待确认项](<Docs/09_REPOSITORY_BASELINE.md>)
7. [设计包源材料与哈希清单](<Docs/Source/manifest.json>)（私人聊天导出不随公开仓库发布）
8. [Windows Unity初始化清单](<Docs/10_WINDOWS_UNITY_SETUP.md>) / [当前待决事项](<Docs/OPEN_DECISIONS.md>)
9. [Unity证据：Bootstrap](<Docs/Evidence/Bootstrap/README.md>) / [Station平台](<Docs/Evidence/StationPlatform/README.md>) / [素材检查图](<Docs/AssetIntake/Preview/README.md>)
10. [过程历史归档](<Docs/Archive/README.md>)：旧分工、审查、交接与已被取代的工具，不是当前执行状态。

## 目录

- `Docs/`：设计分章、原始材料、配置、模板、校验工具及后续研究/证据。
- `Tools/`：开发工具，如 [mcp](<Tools/mcp/README.md>)（从 Linux 驱动 Windows 上的 Unity Editor 和用户 Chrome）。
- `Marketing/`：概念与发布素材；概念图不可冒充实机画面。
- `VRChatKatamariDanacyUnity/`：Windows VCC 创建的真实 Unity 工程根；VPM 包源码与本地缓存不入库。
- `tasks.json`：当前执行任务表（状态以其为准）。

现有 Git 历史、main分支和origin保留。公开仓库排除私人聊天导出、未获准素材及VPM SDK包源码；VRChat项目包由VPM恢复。本项目尚未指定开源许可，也不对上游平台/SDK内容另行授权。

## 本地检查（Python 3.10+，无第三方依赖）

```sh
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests -v
```

验证计划结构、来源哈希、任务 DAG，以及宿主离线ZIP库存工具和前置素材证据一致性；不验证游戏实现或替代Unity/真人门禁。

当前先按用户要求推进不启动Unity的前置工作。之后再评审T00：核对版本支持状态、Console和Build & Test；完成T04/T05后才能推进T06接口冻结。独立的需求、素材、文本和离线工具不被这条Unity门禁链阻塞。
