# A 套来源证据：接触与外壳（2026-10-14复核）

复核时间：2026-10-14T00:59Z 起；只读公开网页与本地仓库资料。没有执行或克隆第三方代码，也未取得原版游戏源码。

## 先核查：原版源码状态

- **没有取得《塊魂》或《Once Upon A KATAMARI》的真实游戏源码。** 本地检视的是本项目自己的设计契约、早期研究记录、Python参考代码和测试；本轮 GitHub repository search API `q=katamari damacy source code` 返回 HTTP 200、`total_count=0`。这是有限元数据搜索，不证明互联网上或私有仓库没有源码。
- 历史专利不是游戏源码，也不能证明零售版或 2025 新版沿用专利方案。下文严格区分专利披露、产品宣传、第三方实验和本项目工程近似。

## 本轮公开来源实际HTTP结果与有限证据

| 来源 / 请求 | 实际结果 | 只能支持的结论 / 限制 |
|---|---|---|
| [USPTO Patent Center，application 10944200](https://patentcenter.uspto.gov/applications/10944200) | 访问返回 HTTP 200；解码页面仅显示通用标题“Patent Center”，没有暴露该申请的可读专利段落。 | 记录官方页面可达，但本轮未从响应中核实技术内容。不能把页面 shell 当作专利说明书全文。 |
| [USPTO 旧 PatFT 查询链接](https://patft.uspto.gov/netacgi/nph-Parser?Sect1=PTO1&Sect2=HITOFF&p=1&u=/netahtml/PTO/srchnum.html&r=1&f=G&l=50&d=PALL&s1=7402104.PN.) | 请求未能解析主机：`getaddrinfo ENOTFOUND patft.uspto.gov`，无 HTTP 状态。 | 官方旧页面的本轮直接请求受网络/DNS限制；不把它写成 HTTP 失败码。 |
| [USPTO专利PDF下载端点](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/7402104) | HTTP 403 Forbidden。 | 本轮未读该官方 PDF 内容。 |
| [Google Patents — US7402104B2](https://patents.google.com/patent/US7402104B2/en) | 本轮请求及带查询参数重试均 HTTP 200。返回标题 *Game performing method, game apparatus, storage medium, data signal and program*，编号 US7402104B2、申请号 US10/944,200、申请日 2004-09-20、公布/授权日 2008-07-22，列发明人/原申请人，并显式链接上述 USPTO Patent Center。 | 这是可访问的聚合公开页，不是 USPTO 官方页；本轮可直接重核的主要是书目和链接。已有本地研究记录摘录专利对径向位移点、滚动表面参考点及显示/碰撞/滚动模型区分的文字；它只表示历史专利披露，不证明游戏源码、实际部署或新版沿用。法律状态/权利不作法律判断。 |
| [Bandai Namco 官方产品页](https://www.bandainamcoent.com/games/once-upon-a-katamari) | HTTP 200，解码正文仅显示标题“Once Upon A KATAMARI  Official Site”。 | 本轮没有从此响应读出物理/代理/碰撞内部算法；不能据标题补充技术事实。 |
| [Bandai Namco Europe 官方Q&A](https://en.bandainamcoent.eu/katamari/news/what-do-you-need-know-about-once-upon-katamari) | HTTP 200，页面全文可读。明确发布日 2025-10-24、KatamariBall每房最多4人、道具名称含 Magnet / Timer / Rocket。 | 支持作品身份/公开玩法层事实，不提供接触、径向代理、TOI、墙角、盒碰撞或 Udon 实现细节；页面未核实具体补丁/构建号。 |
| [GitHub repo-search API](https://api.github.com/search/repositories?q=katamari+damacy+source+code) | HTTP 200，`total_count=0`。 | 仅是指定查询的公开仓库元数据结果，不是“公开世界不存在源码”的证明。 |
| [rubymari-damacy 仓库元数据](https://api.github.com/repos/Eiriksmal/rubymari-damacy)、[README](https://github.com/Eiriksmal/rubymari-damacy/blob/master/readme.md)、[`lib/game.rb`](https://github.com/Eiriksmal/rubymari-damacy/blob/master/lib/game.rb)、[`lib/player.rb`](https://github.com/Eiriksmal/rubymari-damacy/blob/master/lib/player.rb) | API、README、源文件均 HTTP 200。仓库元数据显示公开 Ruby 项目，描述“Experiments with Gosu”；README 自述为 Gosu 2D 游戏/实时互动实验，玩法简介是小于球的东西会粘上。`game.rb` 读取方向键/手柄并调用 Player 的上下左右移动；`player.rb` 是二维坐标与 sprite/帧更新。 | 这是他人可读的独立 2D 实验实现，不是 Namco/Bandai 原版源码；所读文件未展示本套 3D 支持高度、径向采样、扫掠碰撞算法。未运行/克隆/复制该实现，也未据此声称原作内核。 |

## 本地项目材料核对与工程边界

- [`Docs/02_TECHNICAL_CONTRACTS.md`](../../../../02_TECHNICAL_CONTRACTS.md) §3.2–3.3 是项目自己的近似：有限径向代理点、支持距离 `h=max(r,max_i dot(-n,Q*p_i))`、`N=32`与最多4个长物件候选是原型值；首版物理球形扫掠先于凹凸物理。它不是原作算法证据。
- [`Docs/Planning/BALL_WORKFLOW_REPLAN.md`](../../../../Planning/BALL_WORKFLOW_REPLAN.md) 与 A 套 [`HANDOFF.md`](HANDOFF.md) 明确保留的缺陷：T49 旧历史 max 不让球落下；T50 需要多贡献移除/重建/候选排序与长度适配；T54 零速嵌入、盒推出深度、stuck 含义及旧双接触的错误几何输入。
- [`INTERFACES.md`](INTERFACES.md) 曾建议右手世界系。重新对照 Unity 官方资料后已将其改成“轴标签与手性/旋转语义分开”的草案，必须由 Unity 2022.3 方向箭头探针对拍；Python 算例不等于 Unity quaternion 验证。
- [`Docs/Reviews/BALL_COORDINATE_REVIEW_CHECKPOINT.md`](../../../../Reviews/BALL_COORDINATE_REVIEW_CHECKPOINT.md) 记录官方 Unity 2022.3：左手坐标系，`Vector3.Cross`按 left-hand rule；本轮两条官方页面均HTTP 200（[坐标旋转手册](https://docs.unity3d.com/2022.3/Documentation/Manual/QuaternionAndEulerRotationsInUnity.html)、[Cross API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Vector3.Cross.html)）。本轮页面抽取显示手册标题与API说明页；手性句子的具体摘录在本地Lead检查点中已逐字记录。本工作没有Unity Editor，因此本套 `Quaternion`/Transform 实测仍 `NOT_RUN`。

## 证据分级与结论

1. 专利页面/现有本地研究：一项 2003–2008 年历史公开技术披露，不能标作游戏源码或 2025 新版实现。
2. 官方产品页/Q&A：可用于作品身份及页面明示玩法，不包含本套数值几何算法。
3. rubymari-damacy：可访问的外部 2D 仿作/实验，可说明存在另一种公开、独立实现；不代表原版代码或本项目算法来源。
4. 本套支持公式、代理采样、AABB扫掠、恢复和测试均为项目工程近似。没有实机观察、反编译、运行原作或目标 Unity 编译证据。
