# mcp — Linux 侧驱动 Windows MCP 服务器的最小 CLI

`mcp_cli.py` 是一个**零第三方依赖**（仅 Python 标准库）的命令行客户端：通过 SSH 端口转发 +
MCP Streamable HTTP，让本机 Linux 上的 agent 驱动用户 Windows PC 上的 MCP 服务器。

目前两个 **server profile** 共用一个客户端：

| profile | 用途 | Windows 端 | 本地转发端口 | 是否就绪 |
| --- | --- | --- | --- | --- |
| `unity`（默认） | Unity Editor（MCP for Unity v10.3.0，HTTPLocal） | `127.0.0.1:8080`，有 `GET /health` | `18080` | `/health` 返回 2xx |
| `browser` | Playwright MCP（extension 模式，控制用户真实 Chrome） | `localhost:8931`（仅 IPv6 `[::1]`），`/mcp`，无 `/health` | `18931` | TCP 可连 + MCP `initialize` 成功 |

每次调用都是独立的短会话：`initialize` → `notifications/initialized` → 实际请求，
结束时尽力 `DELETE` 掉会话；不落盘、不常驻（除 `--save-images` 写出的图片）。

## 前提

- 本机 Linux，Python ≥ 3.9（`ssh`、`pgrep` 可用）
- `ssh pc114514` 已配置密钥登录（默认 shell 为 cmd.exe）
- `unity`：Windows 上 Unity Editor 已打开，MCP for Unity 正在监听 `127.0.0.1:8080`
- `browser`：Windows 上已安装并登录 Playwright 扩展的 Chrome；服务由计划任务
  `DshPlaywrightMcp` 启动（`%USERPROFILE%\.dsh\playwright-mcp\start.cmd`，
  日志 `%USERPROFILE%\.dsh\playwright-mcp\server.log`）。端口没起来时 CLI 会自动
  `schtasks /run /tn DshPlaywrightMcp` 并等它监听（约 10–20 秒）

## 用法

全局参数写在子命令**之前**：`--server` / `--port` / `--host` / `--timeout`。

```bash
cd Tools/mcp

# 隧道（up 幂等；status 打印 JSON；down 只杀自己那条 ssh -L）
python3 mcp_cli.py tunnel up                    # unity（默认 profile）
python3 mcp_cli.py tunnel status
python3 mcp_cli.py tunnel down                  # 只杀 -L 18080:127.0.0.1:8080
python3 mcp_cli.py --server browser tunnel up   # 必要时先拉起 Windows 计划任务
python3 mcp_cli.py tunnel down --all            # 两个 profile 的转发都停

# 工具（都会先自动确保隧道就绪）
python3 mcp_cli.py tools --names
python3 mcp_cli.py tools
python3 mcp_cli.py schema read_console

# Unity（只读示例）
python3 mcp_cli.py state                        # 只读编辑器快照（unity 专属）
python3 mcp_cli.py state --uri mcpforunity://project/layers
python3 mcp_cli.py call read_console '{"action":"get","count":20}'
python3 mcp_cli.py call manage_scene '{"action":"get_active"}'

# 浏览器（只读示例：列标签页，不要顺手导航/关页）
python3 mcp_cli.py --server browser tools --names
python3 mcp_cli.py --server browser schema browser_tabs
python3 mcp_cli.py --server browser call browser_tabs '{"action":"list"}'

# 参数也可以来自文件或 stdin
python3 mcp_cli.py call manage_gameobject @args.json
echo '{"action":"get_active"}' | python3 mcp_cli.py call manage_scene -
```

退出码：`0` 成功；`1` 失败（JSON-RPC error、`isError`、健康检查失败、参数错误等）；`130` Ctrl-C。

`tunnel down` 只在进程命令行里带**精确** `-L <本 profile 的 spec>` 的 ssh 进程上动手
（还会读 `/proc/<pid>/cmdline` 二次确认 `ssh` + `-N` + 完整 spec），不会碰其它 SSH 连接或别的 profile。

## 图片结果

`call` 遇到 `{"type":"image","data":<base64>,"mimeType":...}` 内容项时，会把它写成文件，
输出里只留路径：

```bash
python3 mcp_cli.py --server browser call browser_take_screenshot
# [image] /tmp/mcp-images/browser_take_screenshot-20261114-101500-01.png
python3 mcp_cli.py --server browser call browser_take_screenshot --save-images /tmp/shots
```

- 默认目录 `/tmp/mcp-images`（`--save-images DIR` 或 `MCP_IMAGE_DIR` 覆盖）。
- `structuredContent` 里嵌套的图片同样会被保存并替换成 `{"type":"image","path":...}`，
  不会把 base64 打到终端。
- 同一次调用里相同字节只写一份，多个引用指向同一路径。

## 环境变量

| 变量 | 默认值 | 含义 |
| --- | --- | --- |
| `MCP_SERVER` | `unity` | 默认 profile（`unity` / `browser`），可被 `--server` 覆盖 |
| `MCP_PORT` | 空 | 覆盖 profile 的本地转发端口 |
| `MCP_SSH_HOST` | `pc114514` | SSH 别名 |
| `MCP_TIMEOUT` | `120` | 单次请求超时（秒），也可用 `--timeout` |
| `MCP_IMAGE_DIR` | `/tmp/mcp-images` | `call` 保存图片的默认目录 |

## browser profile 的两个坑

- **只监听 IPv6。** Windows 上 Playwright MCP 只绑定 `[::1]:8931`，所以转发目标写
  `localhost:8931`（`ssh -L 18931:localhost:8931`），不要写 `127.0.0.1:8931`。
- **Host 白名单。** 服务器只接受 `Host: localhost:8931` / `127.0.0.1:8931` / `[::1]:8931`，
  其它 Host 一律 `403 Access is only allowed at ...`。CLI 因此在 browser profile 上
  覆写 `Host` 头；看不到这条错误就说明转发/端口写对了。

另外 browser 的 MCP 工具操作的是扩展所在的标签页集合，当前页可能是扩展自己的
`connect.html`（"Welcome"，页面显示 `<client> connected`），这是扩展的中继页，正常现象。

## 安全须知

- **同一时间只允许一个 agent 驱动 Unity。** 动手前先 `state` 看
  `activity` / `tests.is_running` / `compilation.is_compiling`，别抢别人正在用的 Editor。
- **绝不要关闭或重启 Editor**，也不要调会触发重装包的 `manage_editor` 动作
  （`deploy_package` / `restore_package`）。
- **ProjectSettings / Packages 的改动需要 Lead 批准**（`manage_packages`、Build Settings 等）。
- **没有 brief 明确授权，不要进 Play / Pause、不要 Build、不要发布上传。**
  跑测试（`run_tests`）会占用 Editor，同样要先确认。
- **browser profile 操作的是用户真实的 Chrome**：没有授权不要导航、提交表单、关闭标签页，
  也不要读/改登录态。只读冒烟：`browser_tabs {"action":"list"}`、`browser_snapshot`。
- 只读冒烟（unity）：`state`、`call read_console {"action":"get"}`、
  `call manage_scene {"action":"get_active"}`、`call manage_editor {"action":"telemetry_status"}`。
