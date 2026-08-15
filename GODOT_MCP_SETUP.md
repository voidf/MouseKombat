# Godot MCP 接入说明（DSH / opencode）

本文档记录本项目中 Godot-MCP-Native 插件的接入方法、正确配置格式与已踩过的坑，
避免其它会话（DSH、Claude Code、opencode 等）重复排查。

> 状态：**已跑通**（2026-08-15 验证）。DSH 会话中 Godot 工具以 `mcp__godot__<toolName>` 形式可用。

---

## 1. 架构总览

```
Godot 编辑器 (Godot_v4.6.3-stable_mono_win64)
  └─ addon: addons/godot_mcp  (Godot-MCP-Native v2.0.0)
       └─ MCP Server @ http://localhost:9080/mcp   (streamable-http / SSE)
            ├─ opencode（项目级配置 opencode.json）
            └─ DSH web（profile 级配置 ~/.dsh/profiles/web/cordis.patch.yml）
                 └─ 插件 @deepseek-ai/dsh-mcp-client → 注册 30 个工具到会话
                      └─ 工具命名：mcp__godot__<rawName>
                         （如 mcp__godot__get_scene_tree、mcp__godot__create_node、
                           mcp__godot__execute_editor_script、mcp__godot__run_project）
```

- **opencode.json**（项目级）：仅影响本项目内的 opencode。
- **DSH cordis.patch.yml**（profile 级）：影响这个 dsh web 实例的所有项目。

## 2. 前置条件

1. **Godot 编辑器必须开着**并打开本项目（`D:\MouseKombat\project.godot`）。
2. **MCP Server 必须启动**：插件默认**不会自动启动**服务器（addon 日志：
   `MCP server not auto-started. Use Start button or --mcp-server flag`）。
   两种方式：
   - 在 Godot 菜单栏的 **MCP** 插件面板里点 **Start** 按钮；或
   - 用 `--mcp-server` 参数启动 Godot。
3. 验证服务器存活（协议握手）：

```powershell
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"probe","version":"0.1"}}}'
Invoke-WebRequest -Uri 'http://localhost:9080/mcp' -Method Post -ContentType 'application/json' `
  -Headers @{Accept='application/json, text/event-stream'} -Body $body -UseBasicParsing
# 期望：{"serverInfo":{"name":"godot-native-mcp","version":"2.0.0"}, ...}
```

## 3. DSH 配置（正确格式，★最关键）

文件：`~/.dsh/profiles/web/cordis.patch.yml`

```yaml
# Godot MCP (godot_mcp addon server at localhost:9080/mcp)
- insert:
    - id: mcp-godot
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: godot
        transport: streamable-http
        url: http://localhost:9080/mcp
        toolCallTimeoutMs: 120000
        reconnect:
          enabled: true
```

修改后由 HMR **热重载**生效（无需重启 dsh web，无需新开会话，
本实例的 `cordis-plugin-hmr` 处于 active）。验证插件状态见第 5 节。

### ★★★ 大坑：cordis.patch.yml 的顶层条目是「补丁」不是「插件条目」

这是本次踩坑的根因。`cordis.patch.yml` 的顶层 YAML 数组是**补丁层**，不是
`cordis.yml` 那样的条目列表。顶层条目只有两种合法形式：

| 形式 | 含义 | 能否新建插件 |
|---|---|---|
| `- id: xxx` + `config/disabled/name` | 按 id **修改已存在**条目的配置 | ❌ 不能 |
| `- insert: [{id, name, config...}]` | **插入新条目** | ✅ 能 |

**错误写法**（会被静默跳过，插件根本不加载）：

```yaml
# ✗ 错误！mcp-godot 不存在，加载器警告 "patch: entry mcp-godot not found, skipping"
- id: mcp-godot
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: godot
    ...
```

判断方法：插件清单（见第 5 节）里**找不到 mcp-godot 条目**，Godot 服务器日志里
也永远没有来自 DSH 的连接（只有 opencode/手工探测的连接）。

> 同一文件里 `ui-skin-blue-fantasy` 就是用 `insert:` 正确插入的，可作参照。

## 4. 另一个坑：服务器没起时插件「激活但无工具」

`dsh-mcp-client` 的设计：初始连接失败时，若 `failOnStartupError` 为 false（默认），
插件会**激活但不注册任何工具**；streamable-http 传输对不可达服务器只在「调用时」
重试，不会自动重新做工具发现。表现：插件状态 active，但会话里没有 `mcp__godot__*`。

修复：给配置做一次内容变更触发 HMR 重载（或重启 `dsh web`），让插件重新连接。

## 5. 验证方法

### 5.1 插件是否加载（宿主侧，无需改任何配置）

```powershell
$req = '{"type":"client-request","rpcId":"probe","method":"pluginInventory/list","payload":{"args":{}}}'
Invoke-WebRequest -Uri 'http://127.0.0.1:3080/api/pluginInventory/list' -Method Post `
  -ContentType 'application/json' -Body $req -UseBasicParsing
```

期望出现：`include:mcp-godot | @deepseek-ai/dsh-mcp-client | enabled=True | phase=active`

### 5.2 DSH 客户端是否连上 Godot（服务器侧）

在 Godot 会话中调用 `mcp__godot__get_editor_logs`（source=mcp），
或直接 HTTP 调 `tools/call` 的 `get_editor_logs`。DSH 成功连接的日志特征：

```
Initialize request from client. Protocol: 2025-11-25
Client initialized notification received
SSE connection established: ...
Tools list requested. Available tools: 30 (registered: 155)
```

注意区分：opencode 用 `Protocol: 2024-11-05`；DSH 新版 SDK 用 `2025-11-25`。

### 5.3 会话内工具是否可见

新会话（或 HMR 重载后）的工具列表中应出现 30 个 `mcp__godot__*` 工具。
直接调用即可验证：

```
mcp__godot__get_project_info   → 项目信息（MouseKombat）
mcp__godot__get_scene_tree     → 当前场景树（如 LobbyMenu, 62 nodes）
mcp__godot__execute_editor_script → 在编辑器里执行 GDScript
```

## 6. 常见问题速查

| 现象 | 原因 | 处理 |
|---|---|---|
| 插件清单里没有 mcp-godot | patch 用了 `- id:` 而非 `- insert:` | 改成 insert 形式（第 3 节） |
| 插件 active 但没有工具 | 加载时 Godot MCP 服务器没启动 | 启动服务器后改配置触发 HMR，或重启 dsh web |
| Godot 日志没有 DSH 连接 | 服务器没起 / 配置没生效 | 先点 MCP 面板 Start，再查第 5.1/5.2 |
| 端口 9080 连不上 | Godot 没开 / server 未 Start | 见第 2 节 |
| opencode 能用但 DSH 不能用 | opencode.json 是项目级、DSH 是 profile 级 | 两者独立配置，各自检查 |

## 7. 相关文件

- 项目插件：`addons/godot_mcp/`
- DSH 配置：`~/.dsh/profiles/web/cordis.patch.yml`
- opencode 配置：`opencode.json`（项目根）
- DSH MCP 桥接插件：`@deepseek-ai/dsh-mcp-client`（README 在
  `~/.dsh/profiles/node_modules/@deepseek-ai/dsh-mcp-client/README.zh.md`）
