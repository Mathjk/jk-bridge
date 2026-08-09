# JK Bridge 分步安装指南

**简体中文** · [English](INSTALLATION.md)

本文从空白环境开始，把一台 Windows 11 x64 客户端和一台 Linux 主机配置到可以正常使用 JK Bridge。系统依赖安装与一键配对刻意分开：JK Bridge 会检查依赖，但绝不会自行提权或改写操作系统配置。

> [!IMPORTANT]
> 下文安装稳定版 **v2.2.1**，可以使用未签名的当前用户 Windows 安装器或经过
> 校验的便携 ZIP，并采用一次性 Linux 配对。安装器、服务编辑、Windows 本机
> 发现、Service Profile、首次启动教程和手动更新都属于稳定桌面功能。可选的
> Linux Agent 与受控中继生命周期仍是高级/实验功能；除非你明确在测试该流程，
> 否则不要用它替代下方的一次性配对。

## 开始前需要准备

- 安装 Windows 可选功能和软件时可使用管理员权限；
- 安装 Linux 软件包时可使用 `sudo`；
- 一个用于 JK Bridge 配对和 SSH 的普通 Linux 用户，不能是 root；
- 两台设备属于同一 Tailscale tailnet，或访问策略明确允许两者通信；
- 至少一个网页已经能在 Linux 的 `127.0.0.1` 上访问。

两台设备登录 Tailscale 的邮箱不必完全相同。只要它们属于同一 tailnet，并且 tailnet 策略允许彼此通信即可。

### 稳定流程与可选高级流程

| 能力 | 稳定版 v2.2.1 | 可选高级/实验 Agent 路径 |
|---|---|---|
| Windows 发布形式 | 未签名的当前用户安装器 + 已校验便携 ZIP | 使用同一 Windows 软件，没有单独的 Agent 桌面版本 |
| Linux 配置 | 每台新设备在终端启动一次助手 | 无 AI Agent、用户级 unit、受控中继助手与 Windows 管理客户端；完整生命周期验收仍待完成 |
| 服务编辑 | 简单网址生成、高级字段、模板和端口冲突提示 | Agent 服务清单同步增加多设备协调 |
| 服务迁移 | 原生非秘密导出与预览/确认导入 | Agent 清单同步仍是明确的前台操作 |
| 恢复 | 运行一次性助手并重新配对 | 同一一次性流程仍是必需的后备路径 |

可选 Agent 是确定性的普通程序，不是 AI 功能，不需要 ChatGPT、Codex、模型
API Key 或云端推理。

## 1. 准备 Windows 11

### 1.1 安装 Tailscale

1. 从 [Tailscale 官方 Windows 页面](https://tailscale.com/download/windows)下载；也可以使用官方稳定版直链 [`tailscale-setup-latest.exe`](https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe)；
2. 运行安装程序；
3. 从系统托盘打开 Tailscale，登录目标 tailnet；
4. 稍后确认设备列表中可以看到 Linux 主机。

在 PowerShell 验证：

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" status
```

如果 Tailscale 安装在其他位置，可直接打开托盘软件，或运行：

```powershell
Get-ChildItem "$env:ProgramFiles\Tailscale" -Filter tailscale.exe
```

当前系统版本要求和企业部署方式以 [Tailscale 官方 Windows 安装说明](https://tailscale.com/docs/install/windows)为准。

### 1.2 安装 Windows OpenSSH Client

JK Bridge 在 Windows 需要的是 **OpenSSH Client**，不需要 Windows OpenSSH Server。

图形界面方式：

1. 打开 Windows“设置”，搜索“可选功能”；
2. 点击“查看功能”或“添加可选功能”；
3. 搜索并安装 **OpenSSH Client**，然后重新打开 PowerShell。

管理员 PowerShell 方式：

```powershell
Get-WindowsCapability -Online | Where-Object Name -Like 'OpenSSH.Client*'
Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
```

验证两个必需程序：

```powershell
Get-Command ssh, ssh-keygen
ssh -V
```

两者通常都应位于 `C:\Windows\System32\OpenSSH\`。如果找不到，重新打开 PowerShell 或重启 Windows，再检查可选功能。完整说明见[微软 OpenSSH 官方安装文档](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)。

## 2. 准备 Linux

安装系统软件包时使用 `sudo`；后面运行 `jkbridge-linux.py` 时，必须换回以后用于 SSH 的普通用户。

### 2.1 安装 Python 3

先检查：

```bash
python3 --version
```

JK Bridge 要求 Python 3.9 或更高版本，并且只使用标准库。在 Ubuntu 或 Debian 上如未安装：

```bash
sudo apt update
sudo apt install python3
```

其他发行版请使用自己的软件包管理器。如果发行版软件包不适用，可参考 [Python.org 下载页面](https://www.python.org/downloads/)。

### 2.2 安装并登录 Tailscale

Tailscale 官方提供的 Linux 自动安装方式是：

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
```

如果你的安全规则不允许把下载脚本直接交给 shell，请按照 [Tailscale 官方 Linux 指南](https://tailscale.com/docs/install/linux)选择具体发行版，或使用[官方稳定软件源](https://pkgs.tailscale.com/stable/)手工安装。

验证：

```bash
tailscale status
tailscale ip -4
```

`tailscale status` 中应能看到 Windows 设备。如果两台设备使用不同邮箱登录，请到 Tailscale 管理后台确认它们仍属于同一个 tailnet。

### 2.3 安装 OpenSSH Server

Ubuntu 或 Debian：

```bash
sudo apt update
sudo apt install openssh-server
sudo systemctl enable --now ssh
```

Fedora 或 RHEL 系列：

```bash
sudo dnf install openssh-server
sudo systemctl enable --now sshd
```

Arch Linux：

```bash
sudo pacman -S openssh
sudo systemctl enable --now sshd
```

继续前检查 SSH Server：

```bash
sudo sshd -t
sudo sshd -T | grep -E '^(pubkeyauthentication|allowtcpforwarding)'
sudo test -s /etc/ssh/ssh_host_ed25519_key.pub && echo 'Ed25519 host key: present'
```

JK Bridge 需要 `pubkeyauthentication yes`，以及 `allowtcpforwarding yes` 或 `allowtcpforwarding local`。标准 OpenSSH Server 安装通常会自动生成 Ed25519 主机密钥。如果缺失，可生成标准主机密钥并重启对应发行版的服务：

```bash
sudo ssh-keygen -A
sudo systemctl restart ssh     # Ubuntu/Debian
# sudo systemctl restart sshd  # Fedora/RHEL/Arch
```

如确实需要调整 SSH 策略，应人工、审慎地修改配置，先运行 `sudo sshd -t`，成功后再 reload 或 restart。JK Bridge 不会修改 `sshd_config`。Ubuntu 管理员还可阅读 [Ubuntu Server 官方 OpenSSH 说明](https://documentation.ubuntu.com/server/how-to/security/openssh-server/)。

### 2.4 检查每个本机网页

转发目标必须监听在 Linux 回环地址。例如：

```bash
curl -I http://127.0.0.1:80/project
curl -I http://127.0.0.1:8765/
curl -I http://127.0.0.1:8793/
ss -ltn | grep -E '127\.0\.0\.1:(80|8765|8793)\b'
```

HTTP 重定向、登录要求或正常页面都能说明服务存活。如果显示“拒绝连接”，需要先启动网页应用或修正端口；JK Bridge 不能把一个没有运行的服务变为可访问。

## 3. 检查 Tailscale 连通性

在两台设备运行 `tailscale status`，都应该能看到对方。Windows 还可以运行：

```powershell
& "$env:ProgramFiles\Tailscale\tailscale.exe" ping <Linux-MagicDNS-名称>
```

如果 ping 被拒绝，应先修正 tailnet 成员关系、ACL/grants、设备审批或主机防火墙，再开始配对。不要通过把 Linux 网页改成监听 `0.0.0.0` 来绕过问题。

## 4. 下载并校验 JK Bridge

打开 [GitHub 最新 Release](https://github.com/Mathjk/jk-bridge/releases/latest)，下载：

- `JKBridge-win-x64.zip`
- `JKBridge-win-x64.zip.sha256`

在 Windows 下载目录打开 PowerShell：

```powershell
$actual = (Get-FileHash .\JKBridge-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = (Get-Content .\JKBridge-win-x64.zip.sha256).Split()[0].ToLowerInvariant()
$actual
$expected
$actual -eq $expected
```

最后一行必须显示 `True`。把 ZIP 解压到一个长期保留的目录，不要长期从压缩包预览或临时目录运行。

当前 EXE 尚未签名，因此可能出现 SmartScreen 提示。哈希只能证明下载文件与发布资产一致，不能代替对仓库和发布流程本身的正常信任判断。

稳定版 v2.2.1 同时发布便携 ZIP 和未签名的当前用户 Inno Setup 安装器。不要把
无关第三方构建的安装器当作官方 Release。官方安装器可以检测 Tailscale、打开
官方下载页，并在用户确认 UAC 后安装 OpenSSH Client；它不能替你登录
Tailscale。

安装版可在“设置 → 软件更新”中先点“检查官方 Release”，再点“下载并校验
更新”。Preview 构建会继续检查 Preview 通道，稳定构建只检查稳定通道。程序只
接受同一官方 Release 中成对的安装器与 `.sha256`，下载期间显示实际字节数、
百分比、平均速度、预计剩余时间和校验阶段，并可取消；第一步完成后主程序仍然
运行。只有再次点击“开始安装（将退出 JK Bridge）”才会启动普通 Windows 安装
向导；确认向导进程已创建后，JK Bridge 才执行显式安全退出，真实安装进度由向导
显示。该流程不会后台检查、静默安装或绕过 SmartScreen。配置、设备凭据和 SSH
密钥保存在安装目录之外，正常同用户覆盖安装会保留它们。更新网络可选择跟随
Windows 系统代理、明确填写本机 HTTP 代理或直接连接 GitHub；手动代理只接受
`localhost`、`127.0.0.1` 或 `::1` 和明确端口，不接受凭据与路径。三种方式
都继续执行精确主机、重定向、大小和 SHA-256 校验。Preview 1 没有更新入口；
Preview 6 会把稳定版 v2.2.0 识别为更高的语义版本，并选择稳定版安装器与
校验文件；这就是本次正式发布预期使用的升级测试路径。
Preview 2/3 若旧的一步式入口卡住，可手工下载 Preview 4 安装器，或先从托盘显式
退出旧版再运行已经校验的安装器；安装 Preview 4 后即可使用新的两阶段入口。

## 5. 在 Linux 启动快速配对

在 Windows 的“设备配置”中点击“复制一行配对命令”，然后把命令粘贴到 Linux
终端，用目标 SSH 普通用户运行，不要使用 `sudo`。干净的 Linux 系统只需要
Python 3，不需要下载项目、查找脚本或进入目录。

该命令包含 Windows 构建内嵌的压缩助手，并在执行前核验固定 SHA-256，因此 Linux
不需要访问外网。保持终端窗口不关闭，复制完整的 `jkbridge://...` 内容。它等同于临时密码，不要公开或保存
到笔记；10 分钟、成功一次、取消或失败次数过多后都会失效。

如果已经手动复制了助手，可展开界面的传统命令提示，或运行：

```bash
chmod 700 ~/jkbridge-linux.py
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py pair
```

## 6. 在 Windows 配对并连接

1. 启动 `JKBridge.exe`；
2. 打开“设备配置”；
3. 粘贴完整配对码；
4. 检查软件显示的 Linux 节点、用户和请求目标端口；
5. 点击“配对并连接”。

成功后应该看到：

- Linux 的 `pair` 进程自动退出；
- Windows 配置保存到 `%LOCALAPPDATA%\JKBridge`；
- 当前 Windows 用户的 `.ssh` 目录中出现专用密钥；
- “概览”显示隧道已连接，或仅显示某个网页自己的健康警告；
- 已启用的 Windows `127.0.0.1` 端口开始监听。

软件不会保存 Linux 密码或固定的 Tailscale IP。

## 7. 验证第一次连接

从 JK Bridge“概览”逐个打开服务。如果需要更深入的 Windows 检查，在解压目录打开 PowerShell：

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Listening -TestServices
```

如果隧道已连接但某个服务健康检查失败，请回到 Linux，用 `curl` 检查完全相同的目标。参见[故障排查](TROUBLESHOOTING.zh-CN.md)。

### Windows 端口和 Linux 端口是什么关系

JK Bridge 在 Windows 创建一个本地监听，再让 SSH 把它连接到一个精确的
Linux 回环目标：

```text
http://127.0.0.1:<Windows localPort>/<打开路径>
                       -> 127.0.0.1:<Linux remotePort>
```

例如 Windows `8080` 完全可以转发到 Linux `80`。浏览器必须打开
`http://127.0.0.1:8080/...`，不能改用端口 `80`，也不能使用 Linux 的
Tailscale 地址。只改变 Windows 端口、`http`/`https` 或打开路径不会增加
Linux 目标；增加或改变 Linux 远端端口，目前必须通过一次性配对取得新授权，
以后则可由可选 Agent 精确授权。

打开路径只决定 JK Bridge 首先打开哪个页面，不会改写网页应用自己返回的 HTTP
跳转。自定义网址必须仍是严格主机 `127.0.0.1` 上的 HTTP(S) 地址，并使用
所配置的 Windows 本地端口。TLS 证书或应用域名要求仍可能需要在应用或反向
代理中处理。

## 8. 设置日常启动

如有需要，在 JK Bridge“设置”中打开“随 Windows 启动”。它只为当前 Windows 用户注册启动项。下次登录后，软件会等待 Tailscale 就绪并自动重连。

如果不开启该选项，重启后双击 `JKBridge.exe` 即可。通常不需要再输入配对码、用户名、密码或 IP。

请保留解压目录：打开开机启动后，如果再移动或删除 `JKBridge.exe`，启动项仍会指向旧路径。

## 添加或替换一台 Windows 电脑

已发布的 v2.1.0 把每台 Windows 都视为独立设备：

1. 在新电脑安装并登录 Tailscale；
2. 安装 Windows OpenSSH Client，解压已校验的 JK Bridge ZIP；
3. 在 Linux 运行 `python3 ~/jkbridge-linux.py pair`；
4. 让新 Windows 完成配对并生成自己的 Ed25519 身份；
5. 重新建立所需服务定义，逐个检查 Windows 本地端口；
6. 如果这是替换电脑，只有新连接通过验收后，才在 Linux 撤销旧指纹。

不要复制旧电脑的私钥、授权回执或整个 `config.json`。复制私钥会让两台电脑
无法区分，不能独立撤销；复制授权回执也只是在描述旧密钥已观察到的 Linux
授权，不能授权新密钥。

v2.2.1 Service Profile 格式及其原生文件选择流程刻意只包含非秘密数据：可以
包含服务名称、Windows 与 Linux 端口、打开网址和健康方式，但不包含私钥、
配对码、设备凭据、服务器身份或授权回执。v2.2.1 界面可以导出该白名单、只读
验证并预览导入，并且只有明确确认后才应用。正式迁移前仍建议保留源文件备份并
逐项核对预览。导入只替换本机服务定义；新设备仍必须通过 Agent 登记
或一次性配对。

如果旧电脑已经无法导出 Service Profile，v2.2.1 的导入选择器也可以把
一份精确的 v2.1/schema v3 `config.json` 作为旧格式来源。它先验证旧文件的
完整结构，再只把 Services 投影到同一只读预览；服务器身份、tailnet 身份、
行为设置、私钥路径、授权回执和其他设备字段全部丢弃。这不表示可以复制整个
配置迁移：不能用旧文件覆盖目标配置，应把该来源当作敏感文件处理；条件允许时
仍优先使用非秘密 Service Profile。

vNext 源码还包含 Agent 与中继脚本、加固的用户级 systemd unit、环境文件示例、
打包检查和手工打包说明。一组已部署设备已完成安装、重启、发现和首台管理员
登记；升级、回滚和保留状态的卸载已经实现并通过测试，但登录持久性和干净机器
完整生命周期验收仍未完成，因此还不是稳定安装说明。源码已有经过测试的有界发现引擎：
只有用户明确触发时，它才检查 Tailscale 当前已知的在线 peer 和固定 Agent
端口，并核对节点与 tailnet 身份。v2.2.1 的设备配置页只在高级区域点击“发现 Agent”
后调用它：单个结果只会预选，不会直接写入地址；多个结果必须明确选择；两种情况
都还要另点“使用所选地址”，并提供一次性代码、明确点击登记。该 GUI 已在源码
接线，但尚未完成干净 Windows 验收。

登记后，任意 Agent 设备都可以通过认证 `/v1/me` 与 `/v1/services` 手动同步
自己的服务选择。管理员还可以刷新完整的 Agent 设备列表，并明确撤销所选的
非当前设备。撤销必须勾选确认，只删除该设备今后的 SSH 认证，不能终止已经建立
的 SSH 会话；普通设备只能取得自己的设备回执，不能列出或撤销其他设备。

### vNext 开发参考：添加另一台 Agent 设备

以下流程已在源码实现，但还不是稳定安装路径：

1. 在已经登记的管理员 Windows 电脑上打开“设备配置”，同步/刷新 Agent 状态，
   再点击“生成新设备添加码”。
2. 界面会显示一枚 10 分钟有效、只能使用一次、只能登记普通设备的码；它不会
   写入 JK Bridge 配置或日志。
3. 把码直接复制到目标 Windows。复制后请自行覆盖剪贴板；系统不会自动清空。
4. 在目标电脑点击“发现 Agent”，核对净化后的名称/MagicDNS 身份；若有多个
   候选，必须选择目标，再点“使用所选地址”。如果没有发现，仍可手工填写 Linux
   的精确 Tailscale IPv4 Agent 地址。最后填写添加码和设备名，再明确点击
   “使用 Agent 登记并连接”；发现本身绝不会登记。

第一台管理员不能由该按钮创建，仍须使用 Linux Agent 初始化时打印的管理员码，
或者在 Linux 本机重新生成管理员恢复码。普通设备不能生成添加码、列出或撤销
其他设备，也不能管理中继。

管理员更换、设备丢失恢复、撤销边界，以及“管理员角色不能原地转移”的完整流程
见 [vNext Agent 多设备与管理员操作](AGENT_OPERATIONS.zh-CN.md)。

### vNext 开发参考：受控 Linux 中继

“高级：Linux 可达目标”用于一个 Linux 能访问、但 Windows 不能直接访问的
明确 TCP 服务。两侧本地端口仍可不同：

```text
Windows http(s)://127.0.0.1:<Windows 端口>/<路径>
  -> SSH -> Linux 127.0.0.1:<中继端口>
  -> 一个明确的 <目标 DNS 或 IPv4>:<目标端口>
```

只有 Agent 管理员能操作。填写服务 ID、Linux 中继端口、一个 DNS 名或 IPv4
目标及端口、Windows 本地端口、协议和打开路径后，必须先点“生成计划并探测”。
规划与探测都是只读操作；预览会列出规范化映射、依赖、端口与目标可达性、清单
版本、精确新增/移除的 `permitopen` 端口和受影响设备。核对后还要另行勾选确认，
才允许应用；删除也必须先生成只读删除计划，再明确确认。任何字段变更都会使旧
计划失效；Linux、服务清单或设备状态变化后也必须重新规划。

Linux 端依赖同包的 `jkbridge_relay.py`、`/usr/bin/socat` 和可用的用户级
systemd 会话。受管 unit 写入该用户的 systemd unit 目录，只监听
`127.0.0.1`；创建、更新、删除会与 Agent 服务清单及每台设备的精确授权协调。
Agent 会拒绝不受管或冲突的 unit；它不是通用代理，不支持通配符、网段、URI
目标、UDP、SOCKS、Funnel、Exit Node 或子网路由。HTTPS 仍是透明 TCP 转发，
目标可能因证书主机名、SNI、Cookie 或重定向不接受 `127.0.0.1`。

该中继流程及自动测试已存在于当前源码，但仍须完成 Linux 实机安装/生命周期
和干净 Windows GUI 验收，之后才能作为正式发布能力说明。

如果 Agent 不存在、停止、不兼容或被主动禁用，请使用一次性助手；已有的受限
SSH 隧道不依赖 Agent 持续在线。

## 从 v2.0.x 升级到 v2.1

直接用新版解压目录替换旧程序即可，设备、服务和行为设置会迁移到 schema v3。旧版本没有记录 Linux 实际授权过的端口，因此新版第一次连接会要求重新配对一次。这不是配置丢失：在 Linux 重新运行 `python3 ~/jkbridge-linux.py pair`，把新代码粘贴到“设备配置”，并保留原专用密钥路径即可。

完成后，配置会记录配对返回的客户端指纹和精确端口集合。以后新增或改变 Linux 远端端口时，JK Bridge 会在启动 SSH 之前列出缺少授权的端口；只改变 Windows 本地端口不会触发重新授权。

## 为已有安装添加 Investment News

先从托盘退出 JK Bridge，再在新版解压目录的 PowerShell 中运行：

```powershell
.\Add-Investment-News.ps1
```

脚本会备份 `%LOCALAPPDATA%\JKBridge\config.json`，再写入唯一映射：Windows `127.0.0.1:8793` → Linux `127.0.0.1:8793`。重新启动 JK Bridge；如果当前 Linux 专用密钥尚未授权端口 `8793`，按下一节的步骤重新配对一次。

## 增加另一个 Linux 网页

1. 在 JK Bridge“服务”页添加唯一的 Windows 本地端口、Linux 回环目标端口和 Windows 回环 URL；
2. 选择健康检查：普通网页用“HTTP 状态码”，返回 `{"ok":true}` 的接口用“JSON ok=true”，自签名 HTTPS 或只需检查端口时用“TCP 端口”；
3. 启用该服务；
4. 如果 Linux 目标端口上次没有授权，应用会在连接前提示缺少的端口；在 Linux 运行 `python3 ~/jkbridge-linux.py pair`；
5. 在“设备配置”中保留原 Windows 密钥路径并重新配对。

重新配对会更新精确的 `permitopen` 白名单并保留备份，不会授权任意 Linux 端口。

健康检查是状态提示，不是浏览器访问控制。只要 SSH 转发已经建立，HTTP/TLS 健康失败仍允许点击“打开”；明确的 SSH 转发拒绝、未连接或服务停用仍会阻止打开。

## 卸载或撤销访问

先关闭“随 Windows 启动”，退出 JK Bridge，然后可以删除解压后的软件目录。运行配置位于 `%LOCALAPPDATA%\JKBridge`，专用私钥位于“设备配置”显示的路径。

仅删除 Windows 文件不能撤销可能已经被复制的密钥。应先在 Linux 撤销：

```bash
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

如果该 Windows 设备也不应继续属于 tailnet，再从 Tailscale 管理后台移除它。
