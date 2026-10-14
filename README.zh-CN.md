<p align="right"><a href="README.md">English</a> · <strong>简体中文</strong></p>

<p align="center">
  <img src="docs/images/app-icon.svg" width="112" alt="JK Bridge 图标">
</p>

<h1 align="center">JK Bridge</h1>

<p align="center">
  通过 Tailscale 和受严格限制的 OpenSSH 隧道，在 Windows 打开 Linux 本机网页。<br>
  不再固定 Tailscale IP，不再手工编辑 SSH 公钥，也不需要 Linux 后台程序。
</p>

<p align="center">
  <a href="https://github.com/Mathjk/jk-bridge/actions/workflows/ci.yml"><img alt="持续集成" src="https://github.com/Mathjk/jk-bridge/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/Mathjk/jk-bridge/releases/latest"><img alt="最新版本" src="https://img.shields.io/github/v/release/Mathjk/jk-bridge?style=flat-square"></a>
  <a href="LICENSE"><img alt="MIT 许可证" src="https://img.shields.io/github/license/Mathjk/jk-bridge?style=flat-square"></a>
  <img alt="Windows 11 x64" src="https://img.shields.io/badge/客户端-Windows%2011%20x64-0078D4?style=flat-square&logo=windows11">
</p>

<p align="center">
  <a href="https://github.com/Mathjk/jk-bridge/releases/latest"><strong>下载最新版</strong></a> ·
  <a href="#五分钟首次配置"><strong>快速开始</strong></a> ·
  <a href="docs/INSTALLATION.zh-CN.md"><strong>安装指南</strong></a> ·
  <a href="docs/TROUBLESHOOTING.zh-CN.md"><strong>故障排查</strong></a> ·
  <a href="docs/SECURITY.md"><strong>安全设计</strong></a>
</p>

<p align="center">
  <img src="docs/images/device-setup.png" width="900" alt="JK Bridge 设备配置界面">
</p>

> [!NOTE]
> 最新稳定版本为 **v2.3.1**，提供未签名的每用户级 Windows 安装包和校验过的便携 ZIP。
> 它新增每个服务的重启操作（带确认与审计）、可选的 Linux 控制守护
> `jkbridge-ctl`（重启/日志/状态）、服务断开时的桌面提醒、可配置的健康检查间隔，
> 以及密钥存入 Windows 凭证管理器的重启令牌。Linux 方面仍然推荐使用一次性
> `jkbridge-linux.py` 辅助脚本完成配置与恢复。

## 为什么需要 JK Bridge？

Overleaf、GPU 监控、Jupyter 等自建网页只监听 Linux `127.0.0.1` 时更安全，但 Windows 电脑无法直接打开。JK Bridge 把它们转换成普通的 Windows 本机网址，同时保留两端的回环边界。

软件保存 Linux 节点稳定的 Tailscale MagicDNS 名称，而不是容易变化的 `100.x.y.z` 地址；全部已启用服务由同一个受监管的 SSH 隧道承载。

```text
Windows 浏览器                                                   Linux 主机
http://127.0.0.1:8080/project                                    127.0.0.1:80
          │                                                             ▲
          └── JK Bridge ── OpenSSH 本地转发 ── Tailscale ────────────────┘
```

首次使用只需：在 Linux 启动临时脚本，把一次性配对码粘贴到 Windows 软件，再点击“配对并连接”。配对成功后 Linux 脚本自动退出；以后日常使用只需启动 `JKBridge.exe`。

## 哪个文件在哪台设备运行？

| 文件或组件 | 运行位置 | 用途 |
|---|---|---|
| `JKBridge.exe` | Windows 11 x64 | 桌面界面、首次配置、隧道监管、健康检查、托盘和开机启动 |
| `jkbridge-linux.py` | Linux | 一次性配对、查看托管 SSH 密钥和撤销授权 |
| Tailscale | 两台设备 | 加密设备网络和稳定的 MagicDNS 身份 |
| OpenSSH Client | 仅 Windows | 创建专用密钥并运行本地转发隧道 |
| OpenSSH Server | 仅 Linux | 接收受限公钥并终止隧道 |

> [!IMPORTANT]
> Windows **不需要** OpenSSH Server；Linux **不需要**安装 JK Bridge 桌面软件。

## 环境要求与官方下载

| 设备 | 要求 | 官方安装或下载 | 快速验证 |
|---|---|---|---|
| Windows | Windows 11 x64 | 系统自带 | `winver` |
| Windows | Tailscale 已安装、运行并登录 | [Windows 下载](https://tailscale.com/download/windows) · [安装说明](https://tailscale.com/docs/install/windows) | `tailscale status` |
| Windows | OpenSSH Client（`ssh.exe` 和 `ssh-keygen.exe`） | [微软安装说明](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse) | `Get-Command ssh, ssh-keygen` |
| Linux | Tailscale 已安装、运行并登录 | [Linux 安装说明](https://tailscale.com/docs/install/linux) · [手动软件包](https://pkgs.tailscale.com/stable/) | `tailscale status` |
| Linux | Python 3.9 或更高版本 | 发行版软件包，或 [python.org](https://www.python.org/downloads/) | `python3 --version` |
| Linux | OpenSSH Server，且有 Ed25519 主机密钥 | [Ubuntu Server 官方说明](https://documentation.ubuntu.com/server/how-to/security/openssh-server/) | `sudo sshd -t` |
| Linux | 目标网页监听在回环地址 | 对应网页服务的说明 | `curl -I http://127.0.0.1:<端口>/` |

两台设备必须出现在同一个 tailnet 中，或者 tailnet 访问策略已经明确允许两者通信。登录邮箱可以不同，真正需要一致的是 tailnet 成员关系。

JK Bridge 会检查这些依赖，但不会自行提权安装系统软件、修改软件源或 `sshd_config`，也不会启动 Overleaf、GPU Dashboard 等网页服务。图形界面和命令行两种安装方式见[分步安装指南](docs/INSTALLATION.zh-CN.md)。

## 五分钟首次配置

### 1. 安装并检查基础环境

在两台设备上完成上表。Windows 安装的是 **OpenSSH Client**，不是 OpenSSH Server；Linux 则要确认 SSH Server 和目标网页已经启动。

### 2. 在 Windows 下载并校验 JK Bridge

从[最新版 Release](https://github.com/Mathjk/jk-bridge/releases/latest)下载同一版本的两个文件：

- `JKBridge-win-x64.zip`
- `JKBridge-win-x64.zip.sha256`

在下载目录打开 PowerShell：

```powershell
Get-FileHash .\JKBridge-win-x64.zip -Algorithm SHA256
Get-Content .\JKBridge-win-x64.zip.sha256
```

两处哈希必须完全一致，然后把 ZIP 解压到一个长期保留的目录。当前发布程序还没有商业代码签名，Windows 可能显示“未知发布者”；确认哈希无误后再决定运行。

### 3. 在 Linux 启动快速配对

在 Windows 软件中打开“设备配置”，点击“复制一行配对命令”。把命令粘贴到
Linux 终端，并用以后用于 SSH 的普通用户运行，不要加 `sudo`。Linux 只需要
Python 3，不需要克隆项目、手动下载脚本、进入目录或执行 `cd`。

这条命令已经包含 Windows 版本内嵌的压缩助手，并在执行 `pair` 前核验固定
SHA-256；Linux 不需要访问外网。保持终端不关闭；脚本会显示一个以
`jkbridge://` 开头的配对码，它在 10 分钟后失效，并且只能成功使用一次。

如果已经手动复制过脚本，传统恢复方式仍然可用：

```bash
chmod 700 ~/jkbridge-linux.py
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py pair
```

### 4. 在 Windows 完成配对

1. 启动 `JKBridge.exe`；
2. 如果没有自动显示，就打开“设备配置”；
3. 粘贴完整的 `jkbridge://...` 配对码；
4. 点击“配对并连接”。

软件会创建专用 Ed25519 密钥、收紧 Windows 文件权限、固定 Linux SSH 主机密钥，并让 Linux 脚本写入一条仅允许指定回环端口的授权。整个过程不需要输入 Linux 密码，也不会传输私钥。

### 5. 打开网页服务

在“概览”页或系统托盘点击服务。浏览器地址仍然是 Windows 本机网址，例如 `http://127.0.0.1:8080/project`；不要换成 Linux 的 Tailscale IP。

默认示例：

| 服务 | Windows 打开地址 | Linux 转发目标 |
|---|---|---|
| Overleaf | `http://127.0.0.1:8080/project` | `127.0.0.1:80` |
| GPU Dashboard | `http://127.0.0.1:8765/` | `127.0.0.1:8765` |
| Investment News | `http://127.0.0.1:8793/` | `127.0.0.1:8793` |

这些条目都可以编辑。JK Bridge 不会把它们绑定到公网或普通局域网。

每个服务的 SSH 转发、浏览器地址、健康检查和 Linux 授权端口都由同一条回环路由生成。对于自签名 HTTPS，可在“服务”页选择“TCP 端口”健康检查；健康警告仍会显示，但只要 SSH 转发已经建立，就不会阻止浏览器打开。

### 扫描这台 Windows 上的本机网页

在“服务 → Windows 本机”点击“扫描本机”。JK Bridge 只读取 Windows
当前已经处于监听状态的 `127.0.0.1` 和 `::1` TCP 端口，然后对这些候选
端口发送短时、无正文的 HTTP/HTTPS `HEAD` 请求：

- 不扫描任何磁盘，也不遍历 `1–65535` 端口；
- 不使用系统代理、不携带 Cookie、不跟随跳转、不读取页面正文；
- 总扫描设有 8 秒上限，单次探测也有短超时和并发上限；
- 已属于 JK Bridge Linux 隧道的端口只标记，不重复保存；
- “仅 TCP”表示端口有程序监听，但尚不能认定它是网页；
- 自签名 HTTPS 可以被识别，但会标明证书未验证；保存时默认使用 TCP
  状态判断。

确认是网页的结果可以保存为独立的 Windows 本机服务。它们不会加入 SSH
转发或 Linux 端口授权，保存、删除也不会启动、停止对应的 Windows 程序。
进程名仅用于显示和变化提醒，不作为身份或授权依据。未启动的潜在服务无法仅靠
监听表可靠发现；已经保存的条目仍会保留，并在下次扫描时显示“离线”或恢复可用。

### 理解 Windows 端口和 Linux 端口

两端端口不必相同。例如 JK Bridge 只在 Windows 回环端口 `8080` 上监听，
然后把流量转发到 Linux 回环端口 `80`：

```text
浏览器打开 http://127.0.0.1:8080/project
              Windows localPort 8080
                       -> Linux remotePort 80
```

浏览器网址永远使用 **Windows 本地端口**。协议和路径可以按服务选择，例如
`https` 与 `/login`，但主机仍必须严格是 `127.0.0.1`。JK Bridge 只做 TCP
转发，不会改写网页应用返回的重定向，也不会让签发给其他域名的证书自动适用于
`127.0.0.1`。

在稳定版 v2.2.1 中，应在“服务”页一起核对 Windows 端口、Linux 目标端口和
回环网址。简单模式会根据“协议 + Windows 端口 + 打开路径”自动生成网址并
预览完整映射；高级模式仍可填写经过校验的自定义回环网址。

## 首次配置完成以后

| 情况 | 需要做什么 |
|---|---|
| Windows 正常重启 | 双击 `JKBridge.exe`；或者提前打开一次“随 Windows 启动” |
| Linux 的 Tailscale IP 改变 | 什么都不用做；软件会解析保存的 MagicDNS 身份 |
| 新网页使用已经授权过的 Linux 端口 | 在“服务”页添加或编辑即可 |
| 新网页需要新的 Linux 目标端口 | 先启用服务，再在 Linux 运行 `pair`，使用原 Windows 密钥重新配对 |
| Linux 重装或主动轮换 SSH 主机密钥 | 在 Linux 本机确认变更，然后重新配对 |
| Windows 设备或私钥丢失 | Linux 运行 `status`，再运行 `remove --fingerprint 'SHA256:...'` |
| 从 v2.0.x 升级到 v2.1 | 启动新版后按提示重新配对一次，以建立可核验的端口授权记录 |

重新配对只更新 `~/.ssh/authorized_keys` 中 JK Bridge 自己管理的那一行，并会先备份到 `~/.ssh/jkbridge-backups/`。

## 发布包里有什么？

| 文件 | 用途 |
|---|---|
| `JKBridge.exe` | 便携、自包含的 Windows x64 软件 |
| `jkbridge-linux.py` | 单文件 Python 3 Linux 工具 |
| `Setup-JKBridge.ps1` | 高级 V1/人工恢复工具；正常的一键配对不需要运行 |
| `Add-Investment-News.ps1` | 为已有配置安全添加或更新 Investment News 映射 |
| `Verify-JKBridge.ps1` | Windows 环境与运行状态诊断 |
| `README*.md`、`docs/` | 中英文安装、排障、安全和架构文档 |
| `SHA256SUMS`、`*.sha256` | 发布文件完整性校验记录 |

## 常见问题

| 现象 | 首先检查 |
|---|---|
| 找不到 Linux 设备 | 两端运行 `tailscale status`，确认双方位于同一 tailnet |
| 配对码超时 | 保持 Linux `pair` 运行，生成新配对码，并检查 tailnet ACL/防火墙 |
| 提示“OpenSSH 需要处理” | 安装 Windows **OpenSSH Client**，确认 `ssh` 与 `ssh-keygen` 都存在 |
| 隧道成功但网页打不开 | 在 Linux 用 `curl` 检查配置中的 `127.0.0.1:<端口>` |
| 提示“主机密钥不匹配” | 立即停止；确认 Linux 确实重装或轮换密钥后再配对，不要关闭校验 |
| Windows 本地端口被占用 | 关闭已确认的占用程序，或改用另一个 Windows 本地端口 |
| 新服务提示不允许转发 | 启用新的 Linux 目标端口后重新配对 |
| 自签名 HTTPS 显示证书健康错误 | 在服务编辑器选择“TCP 端口”；浏览器仍可打开并由用户确认其证书 |

完整命令、重启行为、重复密钥迁移和安全诊断方式见[故障排查](docs/TROUBLESHOOTING.zh-CN.md)。

## 安全设计摘要

- 配对端口只监听 Linux 的 Tailscale IPv4，并额外要求一个随机、短期的 256 位令牌；
- Linux 主机密钥在本机读取并固定，后续 SSH 始终严格校验；
- Windows 专用私钥只允许当前用户与 SYSTEM 访问；
- Linux 授权不能执行命令或取得 shell，只能访问明确启用的 Linux `127.0.0.1` 端口；
- Windows 保存配对返回的客户端指纹和精确端口集合；端口发生漂移时会在启动 SSH 前要求重新配对；
- Windows 监听和 Linux 目标全部固定为 `127.0.0.1`；
- 配置采用原子写入，诊断会脱敏，公开发布扫描会阻止常见凭据和机器身份进入仓库。

请阅读完整的[安全设计](docs/SECURITY.md)和[配对协议](docs/PAIRING_PROTOCOL.md)。如需私下报告漏洞，请使用 [SECURITY.md](SECURITY.md) 中的方式。

## 文档导航

| 文档 | 适合谁看 |
|---|---|
| [安装指南](docs/INSTALLATION.zh-CN.md) · [English](docs/INSTALLATION.md) | 首次使用者和系统管理员 |
| [故障排查](docs/TROUBLESHOOTING.zh-CN.md) · [English](docs/TROUBLESHOOTING.md) | 配置、隧道或网页访问失败的用户 |
| [系统架构](docs/ARCHITECTURE.md) | 维护者和安全审查者 |
| [安全设计](docs/SECURITY.md) | 系统管理员和审查者 |
| [配对协议](docs/PAIRING_PROTOCOL.md) | 开发者和协议审查者 |
| [开发指南](docs/DEVELOPMENT.md) | 编译、测试和发布项目的贡献者 |
| [Windows 验收清单](docs/WINDOWS_ACCEPTANCE.md) | 发布测试人员 |

## 从源码构建

桌面软件使用 .NET 10 和 Avalonia 12.1。安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 后运行：

```bash
dotnet test JKBridge.slnx -c Release
python3 -m unittest discover -s tests/linux -p 'test_*.py'
./scripts/check-public-release.sh
./scripts/check-doc-links.sh
./scripts/publish-win-x64.sh
```

最后一个命令会在 `artifacts/` 生成经过测试的 Windows 发布包。修改配对、授权、进程监管或发布代码前，请先阅读[开发指南](docs/DEVELOPMENT.md)。

## 获取帮助与参与开发

- 可复现的错误或功能建议请[提交 Issue](https://github.com/Mathjk/jk-bridge/issues/new/choose)；提交前请删除主机名、tailnet 名称、用户名、公钥、令牌和个人路径；
- 修改代码或文档前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)；
- 安全漏洞请使用 [SECURITY.md](SECURITY.md) 的私密流程，不要公开提交 Issue。

目前 JK Bridge 主要作为 Windows 11 x64 便携客户端测试。程序尚未进行商业代码签名，也不会代替用户安装 Tailscale/OpenSSH、管理 tailnet 策略、提供公网 URL 或启动 Linux 上的网页服务。

## 许可证

[MIT](LICENSE)
