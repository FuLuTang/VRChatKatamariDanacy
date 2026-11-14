# unity-mcp — Linux 侧驱动 Windows Unity Editor 的最小 CLI

`unity_mcp.py` 是一个**零第三方依赖**（仅 Python 标准库）的命令行客户端，让本机 Linux 上的
agent 通过 SSH 端口转发 + MCP Streamable HTTP 驱动用户 Windows PC 上的 Unity Editor
（项目 `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`，MCP for Unity v10.3.0，HTTPLocal）。

每次调用都是独立的短会话：`initialize` → `notifications/initialized` → 实际请求，不落盘、不常驻。

## 前提

- 本机 Linux，Python ≥ 3.9（`ssh`、`pgrep` 可用）
- `ssh pc114514` 已配置密钥登录（默认 shell 为 cmd.exe）
- Windows 上 Unity Editor 已打开，MCP for Unity v10.3.0 正在监听 `127.0.0.1:8080`
- 默认本地转发端口 `18080`，可用 `UNITY_MCP_PORT` 覆盖

## 用法

所有命令都会先自动确保隧道就绪（`tools` / `schema` / `state` / `call`）。

```bash
cd Tools/unity-mcp

# 隧道
python3 unity_mcp.py tunnel up        # 幂等：/health 已通就什么都不做
python3 unity_mcp.py tunnel status    # 打印 /health JSON
python3 unity_mcp.py tunnel down      # 只杀自己那条 ssh -L

# 工具
python3 unity_mcp.py tools --names    # 只列名字
python3 unity_mcp.py tools            # 名字 + 一行描述
python3 unity_mcp.py schema read_console

# 读控制台（只读）
python3 unity_mcp.py call read_console '{"action":"get","count":20}'
python3 unity_mcp.py call read_console '{"action":"get","types":["error","warning"],"count":50,"format":"detailed"}'
# 注意：action="clear" 会清空控制台，不是只读操作。

# 编辑器状态（只读资源：场景、Play 模式、编译、测试、是否 ready）
python3 unity_mcp.py state
python3 unity_mcp.py state --uri mcpforunity://project/layers   # 其它 MCP 资源

# 场景 / 编辑器（只读动作）
python3 unity_mcp.py call manage_scene '{"action":"get_active"}'
python3 unity_mcp.py call manage_scene '{"action":"get_loaded_scenes"}'
python3 unity_mcp.py call manage_editor '{"action":"telemetry_status"}'

# 跑测试（会在 Editor 里执行，先确认没人在用）
python3 unity_mcp.py call run_tests '{"mode":"EditMode"}'
python3 unity_mcp.py call get_test_job '{"job_id":"<id>"}'

# 参数也可以来自文件或 stdin
python3 unity_mcp.py call manage_gameobject @args.json
echo '{"action":"get_active"}' | python3 unity_mcp.py call manage_scene -
```

`call` 的输出：text 内容原样打印；有 `structuredContent` 时打印美化 JSON。
退出码：`0` 成功；`1` 失败（JSON-RPC error、`isError`、健康检查失败、参数错误等）；`130` Ctrl-C。

## 环境变量

| 变量 | 默认值 | 含义 |
| --- | --- | --- |
| `UNITY_MCP_PORT` | `18080` | 本地转发端口 |
| `UNITY_MCP_SSH_HOST` | `pc114514` | SSH 别名 |
| `UNITY_MCP_REMOTE_PORT` | `8080` | Windows 上 MCP 端口 |
| `UNITY_MCP_TIMEOUT` | `120` | 单次请求超时（秒），也可用 `--timeout` |

全局参数 `--port` / `--host` / `--timeout` 写在子命令之前，例如
`python3 unity_mcp.py --timeout 300 call run_tests '{"mode":"PlayMode"}'`。

## 安全须知

- **同一时间只允许一个 agent 驱动 Unity。** 动手前先 `state` 看
  `activity` / `tests.is_running` / `compilation.is_compiling`，别抢别人正在用的 Editor。
- **绝不要关闭或重启 Editor**，也不要调会触发重装包的 `manage_editor` 动作
  （`deploy_package` / `restore_package`）。
- **ProjectSettings / Packages 的改动需要 Lead 批准**（`manage_packages`、Build Settings 等）。
- **没有 brief 明确授权，不要进 Play / Pause、不要 Build、不要发布上传。**
  跑测试会占用 Editor，同样要先确认。
- `tunnel down` 只终止命令行里带精确 `-L 18080:127.0.0.1:8080` 的 ssh 进程；不会动其它 SSH 连接。
- 只读冒烟：`state`、`call read_console {"action":"get"}`、
  `call manage_scene {"action":"get_active"}`、`call manage_editor {"action":"telemetry_status"}`。
