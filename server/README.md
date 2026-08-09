# MouseKombat 大厅服务器

公共大厅服务器（lobby server）：客户端 <-> 服务器 <-> 客户端 的选房大厅与对局中转。
协议单一来源：`MouseKombat.Net/PROTOCOL.md`。本目录文件与 C# 端消息定义同步修改。

```
server/
  lobby_server.py   # 服务器本体（asyncio，TCP 房间信道 + UDP 对局转发）
  room.py           # 房间状态机（RoomState.cs 的 Python 移植，纯逻辑）
  protocol.py       # 帧格式 / msgpack / 消息号 / 名字净化
  smoke_test.py     # headless 端到端自测（无需 .NET）
  requirements.txt
  README.md
```

## 端口

| 通道 | 协议 | 端口 | 用途 |
|---|---|---|---|
| 房间 | TCP | 4954 | 握手 / 选房列表 / 建房 / 进房 / 占座 / 选人 / 追赶流 |
| 对局 | UDP | 4954 | 战士间 rollback 包转发（信封 u32 roomId + u8 src + u8 dst + payload） |

不需要第二个监听端口：观战不走 UDP，观战者统一走 TCP 数据流追赶
（`MatchCatchUp` / `MatchInputs`，见 PROTOCOL.md § Mid-match spectating）。

## 配置（环境变量 / 命令行）

| 变量 | 默认 | 说明 |
|---|---|---|
| `MK_HOST` | `0.0.0.0` | 监听地址（IPv4；IPv6 部署填 `::`） |
| `MK_PORT` | `4954` | TCP 端口 |
| `MK_UDP_PORT` | 同 TCP | UDP 转发端口，一般不要改 |
| `MK_GAME_VERSION` | （空） | **必须**与游戏 `project.godot` 的 `application/config/version` 一致；不一致的客户端在 Hello 就被拒 |
| `MK_PROTOCOL` | `2` | 线协议版本（与 `NetVersion.Protocol` 一致，一般不要动） |
| `MK_IDLE_TIMEOUT` | `300` | 未进房间的浏览器连接空闲超时（秒） |
| `MK_MAX_ROOMS` | `500` | 服务器同时在开房间上限 |

## 本机运行（Windows / 开发机）

```powershell
python -m pip install -r requirements.txt
$env:MK_GAME_VERSION = "0.0.7"          # 改成当前游戏版本号
python lobby_server.py
```

自测：

```powershell
python smoke_test.py
```

## 部署（阿里云 Debian 13，2C2G）

```bash
# 1. 装依赖（Debian 13 自带 Python 3.11+；若无 msgpack 则装）
sudo apt update && sudo apt install -y python3 python3-pip
sudo pip3 install --break-system-packages -r requirements.txt
# 或者更规范：python3 -m venv /opt/mousekombat-server/.venv 后 venv 内 pip install

# 2. 放代码
sudo mkdir -p /opt/mousekombat-server
sudo cp -r . /opt/mousekombat-server/     # 整个 server/ 目录
```

`/etc/systemd/system/mousekombat-lobby.service`：

```ini
[Unit]
Description=MouseKombat lobby server
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/mousekombat-server
Environment=MK_HOST=0.0.0.0
Environment=MK_PORT=4954
Environment=MK_GAME_VERSION=0.0.7
Environment=MK_MAX_ROOMS=500
ExecStart=/usr/bin/python3 lobby_server.py
Restart=on-failure
RestartSec=2
# 100 人以内单进程足矣；日志交给 journald
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now mousekombat-lobby
sudo systemctl status mousekombat-lobby
journalctl -u mousekombat-lobby -f        # 看日志
```

防火墙（阿里云安全组同样放行 TCP+UDP 4954）：

```bash
sudo ufw allow 4954/tcp
sudo ufw allow 4954/udp
```

### 游戏版本升级流程

1. 游戏改版（`project.godot` 版本号变更）后，必须同步升级服务器：

```bash
sudo systemctl stop mousekombat-lobby
# 修改 /etc/systemd/system/mousekombat-lobby.service 里的 MK_GAME_VERSION
sudo systemctl daemon-reload
sudo systemctl start mousekombat-lobby
```

2. 版本不一致的客户端连入时在 Hello 阶段直接被拒，服务器不会为此崩/挂。
   本服务不做跨版本兼容（故意如此，见 PROTOCOL.md § Handshake）。

### 资源与规模

- 状态全在进程内存，无落盘、无数据库；重启即清空所有房间（预期行为）。
- 2C2G 对「百人以下同时在开」完全富余：每个房间最多 4 名人类，100 人 ≈ 25 个房间。
- 带宽：单场对局 60Hz rollback 包约为几十 KB/s，25 场同时开远低于 100M。
- 并发连接上限未硬编码；`MK_MAX_ROOMS` 是房间数上限，必要时再调。

### 注意事项

- `MK_GAME_VERSION` 留空 = 拒绝所有连接（安全默认：忘记配置也比放错版本好）。
- 端口被占用时启动会直接报错退出，systemd `Restart=on-failure` 会不断重试，看日志排查。
- UDP 信封的 NAT 学习：客户端在 Hello 里公告的是**本机** UDP 端口；服务器以它收到的
  该成员首个 UDP 报文的源地址为准（NAT 映射可能不同），之后把该成员钉死在那个端点上。
  因此对局中途 NAT 重新映射（通常不会发生）会断开转发，属已知取舍。
