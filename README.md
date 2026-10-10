# VRChatKatamariDanacy / VRC塊魂

VRChat 原创滚球、吸附成长与交分 PVP 项目。内部名 VRC塊魂；公开名称待定，屎壳郎是主题方向，不使用原作角色、音乐或品牌资产。

## 当前状态

**设计包 v0.3 已导入；仓库基础已建立，尚未创建可运行 Unity 工程。**

- PCVR + Desktop；2–4 人休闲 PVP，单人练习。
- VR A 双虚拟推杆；VR B 摇杆追尾；Desktop WASD/鼠标。
- 单槽磁铁/火箭，Jump 使用道具；共享世界坐标、确认收益账本。
- 先一图两人完整纵切片，首发目标野餐草坪与玩具房；夜市后置。
- 完整玩法免费，未来仅纯外观内购；真实收费默认不启用。
- Unity/SDK 编译、Station、真人 VR、多人联网、性能和购买测试均未运行。

## 导航

1. [范围与决策](<Docs/00_SCOPE_DECISIONS.md>)
2. [技术设计（尚未冻结为 contracts v1）](<Docs/02_TECHNICAL_CONTRACTS.md>)
3. [执行阶段与45项任务](<Docs/04_EXECUTION_PLAN.md>) / [机器可读任务表](<tasks.json>)
4. [验收门禁](<Docs/05_ACCEPTANCE_TESTS.md>)
5. [Agent规则](<AGENTS.md>) / [原执行规则](<Docs/07_AGENT_OPERATING_RULES.md>)
6. [初始化解析与待确认项](<Docs/09_REPOSITORY_BASELINE.md>)
7. [设计包源材料与哈希清单](<Docs/Source/manifest.json>)（私人聊天导出不随公开仓库发布）
8. [Windows Unity初始化清单](<Docs/10_WINDOWS_UNITY_SETUP.md>) / [首轮团队分工与模型门禁](<Docs/11_TEAM_KICKOFF.md>)
9. [首轮交付与主管审查](<Docs/13_FIRST_WAVE_REVIEW.md>) / [队员Luna路由证据与回退](<Docs/12_TEAM_MODEL_ROUTING.md>)

## 目录

- `Docs/`：设计分章、原始材料、配置、模板、校验工具及后续研究/证据。
- `Marketing/`：概念与发布素材；概念图不可冒充实机画面。
- `UnityProject/`：预留 Unity 工程根；后续由 VCC/Unity 创建真实工程。
- `tasks.json`：当前执行任务表；原始45项初始状态为TODO。T01/T02/T03首轮文档已进入REVIEW，尚无游戏任务DONE。

现有 Git 历史、main分支和origin保留。当前公开发布仅限本仓库代码与项目文档；不包含私人聊天导出，不包含任何第三方资产。本项目尚未指定开源许可。

## 本地检查（Python 3.10+，无第三方依赖）

```sh
python3 Docs/tools/validate_plan.py
python3 -m unittest discover -s Docs/tests -v
```

只验证计划结构、来源哈希和任务 DAG，不验证游戏实现或替代真人门禁。

下一步是 T00：确认实际 Unity 执行机器、工程根和精确 Unity/SDK 版本，再创建最小测试场景；T04/T05 平台门禁通过后才能推进 T06 接口冻结。
