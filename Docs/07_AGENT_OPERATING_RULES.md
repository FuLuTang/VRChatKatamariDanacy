# 07 多Agent执行规则

## 1. 角色
S = GPT-6.1 Sol：架构、复杂状态、球体数学、网络裁决、接口冻结、风险审查与集成。
L = GPT-6 Luna：清晰接口下的实现、批量编辑器工具、UI、测试、资源清单、文案和固定流程。
H = Human：VR佩戴体验、账号授权、真实付款、发布、最终美术与授权批准。
S与L能力不按“能不能写代码”粗分；以错误代价、歧义和跨模块影响分。
模型标识由官方文档核实；分工是本项目策略。[S33][S34]
价格按实际账户、服务档、缓存和工具成本预算，不直接拿标准单价推算整个项目成本。

## 2. 先冻结契约，再平行开发
总控先产生：目录边界、状态枚举、数据字段及单位、事件名/参数、Owner归属、配置命名。
所有任务读取相同版本契约。更改公共接口提交RFC，由S批准，并标注受影响任务。
不能让运动、拾取、HUD各自发明自己的BallState。
更不能让每个Agent都从完整聊天重新“理解一次需求”。

## 3. 调度状态
TODO -> READY -> RUNNING -> REVIEW -> DONE。
任意任务可BLOCKED，必须写具体缺少的条件。
依赖全部DONE、所需人工门禁通过、资源锁可获得，才进入READY。
“参考研究未完成”不阻塞无关的图标/工具草稿，但阻塞写死原版规则。
每次启动少量独立工作者；价格低不代表可安全同时开几十个。
L任务优先自测+另一L交叉审查；碰撞、网络、付费和公共接口最终由S审查。
同一问题两次无进展修复、需要改变契约、发现SDK不支持，即升级S而不是无限重试。

## 4. 并行写入隔离
每个Agent独立git worktree或工程副本，明确文件owner。
公共场景、ProjectSettings、Packages、输入/层配置、核心Prefab仅集成者能改。
模块使用独立Prefab/测试Scene；最终由集成任务合并进正式场景。
.meta文件一起提交，不重建已有GUID，不把Library/Temp提交。
二进制.blend每份只允许一位写入者；材质/模型分资产输出。
一个Unity编辑器会话或Blender GUI/MCP会话只给一个写入Agent；
并发任务用各自headless进程/独立文件或排队锁。
不得用两个Agent同时操作同一桌面上的Unity按钮。
授权素材导入到隔离目录，清单通过后再进正式工程。
主干每次合并都保留可编译检查点；失败合并回退而非继续堆功能。

## 5. 技术执行分区
- UdonSharp运行时：输入、状态、网络、池、视觉触发，遵守SDK暴露类型/API。
- Unity Editor C#：资源导入、索引烘焙、验证、Prefab生成、报告。
- Blender Python/MCP：几何处理、变换、碰撞代理、LOD、摆景与宣传构图。
- 外部测试工具：JSON契约、任务DAG、纯算法验证。
UI能点开不代表逻辑正确；C#能编译不代表Udon可编译；ClientSim不代表真实网络成立。

## 6. 每任务交付
taskId、输入版本、修改文件、简短实现说明、可复现测试命令/步骤、
实际日志/截图、未测部分、已知风险、资源授权。
报告中的“通过”必须附证据；不得以截图代替掉线/竞争测试。
模板放在templates/task_handoff.md。

## 7. 人工与权限边界
账号登录、支付、下载付费内容、正式商品发布、世界公开上传、宣传发帖必须人工批准。
模型源文件与账号密钥分离；不把token写进代码、截图或报告。
不自动运行下载模型中捆绑的脚本/安装包。
用户没有明确授权时，不以“项目需要”为由购买资产、改变商店价格或发宣传。
真人VR体验有阻塞时，相关门禁停住，但允许无依赖的文案/资产清单/纯数学测试继续。

## 8. 调度器伪代码
```
ready = [t for t in tasks
         if t.status == TODO
         and all(dep.status == DONE for dep in t.dependencies)
         and external_requirements_met(t)
         and locks_available(t.write_scope)]

for task in highest_priority(ready):
    acquire(task.locks)
    launch(task.implementer, scoped_brief(task), fixed_contracts)
    require_evidence_and_review(task)
    mark_done_only_after_acceptance(task)
```
这里只定义调度策略；本包不假装已经连接或运行一个Agent集群。
