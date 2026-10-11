# 队员模型路由与核验记录

日期：2026-10-11。用户明确批准主管检查并调整DSH团队模型配置。

## 原因与处理

原版团队工具不暴露model/provider参数，团队运行时将主管options传给子会话，因此当前Sol主管会生成Sol队员。只修改全局默认值或prompt不会解决这个继承。

使用两份**本地可回退覆盖**，没有覆盖原安装包：

1. 团队工具添加配置`teammateProvider`与`teammateModel`，创建请求携带`agentOptions`。
2. 团队运行时将该`agentOptions`转交continuable子会话创建器，使创建和恢复描述符都记录正确模型。
3. profile层禁用原团队运行时/工具条目，用`insert`挂载覆盖。patch的`name`只是匹配保护，不是替换模块操作。
4. 通过既有GUI的认证本地启动入口及pluginManager正常管理API热重载，仍是既有3080端口，没有替代服务器或重启。

覆盖和profile备份保存在本机DSH profile目录，不纳入项目仓库。profile备份可能含用户私有配置，**不复制进Git仓库或回显内容**。可回退覆盖的生成/配置操作仅用于当前DSH会话环境，DSH升级后须重新核对。

当前队员路由为`openai-codex/gpt-6-luna`。此覆盖是当前profile的团队设置，不只作用于本项目；主管/普通子Agent模型不改。DSH升级后覆盖的代码和固定依赖路径可能过时，须先停新队员创建，再重新核对上游接口并生成覆盖，不能盲用。

## 实际核验（不是角色自报）

| target | 创建结果 | 首次请求lastUsed / next |
|---|---|---|
| reference-rules | gpt-6-luna | gpt-6-luna / gpt-6-luna |
| asset-scout | gpt-6-luna | gpt-6-luna / gpt-6-luna |
| marketing-draft | gpt-6-luna | gpt-6-luna / gpt-6-luna |

投影证据来自`session/projections`的`modelSelection`；provider均为openai-codex。主管list_agents仍为gpt-6.1-sol。正式任务运行时三名队员的list_agents也均显示gpt-6-luna。

### inactive列表显示缺陷

原版运行时取`live?.options.model ?? root.options.model`。子Agent结束一轮、实例释放后，列表会误显示主管Sol。这是显示回退，不是子会话真的换模型；continuable描述符仍保存Luna路由。无须为了改一个标签而篡改会话日志。创建结果+实际请求投影才是证据。

两个旧Sol握手队员reference-luna/rules-luna均未接正式任务、已中断、不再使用。当前工具不提供删除durable成员历史，可能仍出现在列表，不能宣称已物理删除会话文件。

## 2026-10-11 大规模离线批次

常驻队员上限8，两个旧Sol握手槽仍占名额；新建请求返回明确上限错误。未删除历史、未改DSH配置或上限。

采用6个既有Luna队员（reference-rules、asset-scout、marketing-draft、requirements-audit、asset-acquisition、offline-quality）与3个独立workflow执行worker（P15/P16/P18）。本轮6队员及3worker共9个实际请求的`modelSelection.lastUsed`与`next`均再次核验为`openai-codex/gpt-6-luna`；不是仅靠prompt/工具列表标签，也不是把6人虚报为9常驻队员。

独立编排明确指定provider/model，受管job `workflow-1`已完成并收集全部三个DELIVERED结果；实际会话投影也逐一匹配。角色总数不承诺九个请求持续同时运行。

**额度中断（同日）**：多名队员随后在任务中途失败。会话日志显示上游返回HTTP 429，ChatGPT团队套餐用量上限，提示约245分钟后重试。这是外部额度限制，不是任务内容或DSH配置错误；队员路由仍记录为Luna。按规则不静默改用其他模型继续队员工作，主管模型也已不再是原来的Sol，只做管理、验收和Git交付。未完成修订（P10/P11、P12、P15、P16、P18、P22）等额度恢复后由原Luna作者或已记录的单写者接手。工作包完毕后的修订只在记录单写者移交后由既有Luna接手，不用Sol静默替代。Lead只管理/审查/公共排程与Git交付。

## 自检及回退

[生成工具](<tools/create_luna_team_overlay.mjs>)是Host专用，不能放进Unity工程；必须使用Node的`--experimental-import-meta-resolve`以保留ESM依赖身份。生成使用独占创建，不覆盖旧覆盖。生成文件不等于启用。

用于验证和管理的本地RPC辅助脚本仅保留在开发环境、由`.gitignore`排除；不发布具有Host管理能力的本地管理工具，也不写入任何访问Cookie/令牌。它不是Unity或Udon依赖。

已做：覆盖模块语法/导入检查、mock创建请求路由断言（Luna队员、Sol主管不变）、三名真实握手请求投影核验。Unity编译/真机/多人不在本次覆盖测试范围。

回退时先停止所有相关队员；由主管或用户审批，**只删除profile patch里本记录新增的两个禁用条目和一个insert块**，保留之后的其他用户设置，再走正常pluginManager配置重载。不要直接用旧整文件备份覆盖可能已经变化的配置。原安装包始终未修改。
