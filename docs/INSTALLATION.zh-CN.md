# JK Bridge 分步安装指南

**简体中文** · [English](INSTALLATION.md)

本文从空白环境开始，把一台 Windows 11 x64 客户端和一台 Linux 主机配置到可以正常使用 JK Bridge。系统依赖安装与一键配对刻意分开：JK Bridge 会检查依赖，但绝不会自行提权或改写操作系统配置。

## 开始前需要准备

- 安装 Windows 可选功能和软件时可使用管理员权限；
- 安装 Linux 软件包时可使用 `sudo`；
- 一个用于 JK Bridge 配对和 SSH 的普通 Linux 用户，不能是 root；
- 两台设备属于同一 Tailscale tailnet，或访问策略明确允许两者通信；
- 至少一个网页已经能在 Linux 的 `127.0.0.1` 上访问。

两台设备登录 Tailscale 的邮箱不必完全相同。只要它们属于同一 tailnet，并且 tailnet 策略允许彼此通信即可。

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

## 5. 复制并运行 Linux 脚本

Windows 解压目录中已经有 `jkbridge-linux.py`。通过 Tailscale 文件发送、SCP、共享目录或 U 盘，把它复制到普通 Linux 用户的主目录。

在 Linux 运行：

```bash
chmod 700 ~/jkbridge-linux.py
python3 ~/jkbridge-linux.py status
python3 ~/jkbridge-linux.py pair
```

不要使用 `sudo`。保持终端窗口不关闭，复制完整的 `jkbridge://...` 内容。它等同于临时密码，不要公开或保存到笔记；10 分钟、成功一次、取消或失败次数过多后都会失效。

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

## 8. 设置日常启动

如有需要，在 JK Bridge“设置”中打开“随 Windows 启动”。它只为当前 Windows 用户注册启动项。下次登录后，软件会等待 Tailscale 就绪并自动重连。

如果不开启该选项，重启后双击 `JKBridge.exe` 即可。通常不需要再输入配对码、用户名、密码或 IP。

请保留解压目录：打开开机启动后，如果再移动或删除 `JKBridge.exe`，启动项仍会指向旧路径。

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
