# JK Bridge

简体中文 | [English](README.md)

JK Bridge 是一个 Windows 桌面工具，用于通过现有的 Tailscale 网络和一条受监管的 OpenSSH 隧道，访问 Linux 上仅绑定到本机的网页服务。它保存 Linux 节点的 MagicDNS 身份，不保存容易变化的 `100.x.y.z` 地址，因此 Tailscale 地址刷新后不需要再修改 `.cmd`、IP 或转发命令。

正常的首次配置只需三步：

1. 在 Linux 运行一条临时配对命令；
2. 把一次性配对码粘贴到 Windows 软件；
3. 点击“配对并连接”。

Windows 软件会创建专用 Ed25519 密钥、收紧私钥 ACL、固定 Linux SSH 主机公钥，并让 Linux 脚本自动写入只允许指定回环端口的 `authorized_keys` 规则。成功后 Linux 脚本立即退出，不需要安装 Linux 图形软件，也不会留下 JK Bridge 后台服务。

## 功能

- Windows 与 Linux 一键配对，不再人工编辑 `authorized_keys` 或 JSON；
- 不接收 Linux 密码，不传输私钥；
- 使用 MagicDNS 身份，不依赖固定 Tailscale IP；
- 一个受监管的 `ssh.exe` 进程承载全部启用的端口转发；
- Windows 监听端和 Linux 目标端都固定为 `127.0.0.1`；
- 每项服务独立健康检查、有限自动重连、托盘运行和可选开机启动；
- 可在界面添加后续本地网页；
- 配置原子写入、主机密钥固定、诊断脱敏、Linux 授权最小化；
- 发布包包含自包含 Windows x64 EXE 和单文件 Python 3 Linux 工具。

默认服务：

| 服务 | Windows 地址 | 转发关系 |
|---|---|---|
| Overleaf | `http://127.0.0.1:8080/project` | Windows `8080` → Linux `127.0.0.1:80` |
| GPU Dashboard | `http://127.0.0.1:8765/` | Windows `8765` → Linux `127.0.0.1:8765` |

它们只是可编辑的默认示例，JK Bridge 不会把这些服务暴露到公网或普通局域网。

## 环境要求

Windows 11 x64：

- 已安装、启动并登录 Tailscale；
- 已安装 Windows OpenSSH Client，能够找到 `ssh.exe` 和 `ssh-keygen.exe`；
- Windows 与 Linux 位于同一 tailnet，或者 tailnet 访问策略明确允许两者通信。

Linux：

- 已安装、启动并登录 Tailscale；
- Python 3.9 或更高版本；
- OpenSSH Server 已启用 Ed25519 主机密钥；
- 准备用于 SSH 的普通用户能够修改自己的 `~/.ssh/authorized_keys`；
- 需要访问的网页监听在 Linux 回环地址，例如 `127.0.0.1:80`。

JK Bridge 会自动检查这些依赖并给出明确错误，但不会擅自使用管理员权限安装系统软件、修改软件源或改写 `sshd_config`。

## 快速开始

### 1. 校验发布包

从同一个 GitHub Release 下载 Windows ZIP、ZIP 外部校验文件和 Linux 脚本。解压前先核对 ZIP：

```powershell
Get-FileHash .\JKBridge-win-x64.zip -Algorithm SHA256
Get-Content .\JKBridge-win-x64.zip.sha256
```

解压后再按照 `SHA256SUMS` 核对包内每一个文件。当前发布程序尚未进行商业代码签名，因此 Windows 可能提示“未知发布者”。

### 2. 在 Linux 启动一次性配对

必须使用以后用于 SSH 的普通 Linux 用户运行，不要使用 `sudo`：

```bash
chmod +x ./jkbridge-linux.py
./jkbridge-linux.py status
./jkbridge-linux.py pair
```

脚本会显示一个以 `jkbridge://` 开头的配对码。它只监听 Linux 的 Tailscale IPv4 地址，成功处理一次后自动退出，默认 10 分钟失效。

### 3. 在 Windows 一键配置

1. 启动 `JKBridge.exe`；
2. 如果没有自动进入“设备配置”，就在左侧打开该页面；
3. 粘贴完整的 `jkbridge://...` 配对码；
4. 点击“配对并连接”。

一次性令牌只放在请求认证头中，不会写入配置。成功后输入框会清空、Linux 脚本退出，软件自动启动包含全部启用服务的 SSH 隧道。

### 4. 打开服务

在“概览”页或系统托盘中点击服务。浏览器地址应保持为 Windows 的 `127.0.0.1`，不要换成 Linux 的 Tailscale IP。

## 日常使用与电脑重启

配对成功后，正常情况下不需要再次输入用户名、密码、IP 或配对码。

- 如果在“设置”中打开“随 Windows 启动”，登录当前 Windows 用户后 JK Bridge 会自动启动，并在 Tailscale 就绪后自动连接；
- 如果没有打开该选项，重启后双击 `JKBridge.exe` 即可；
- Linux 的 Tailscale IP 改变不需要重新配置，因为软件按已保存的 MagicDNS 身份查找节点；
- 只有 Linux 重装或 SSH 主机密钥轮换、主动撤销客户端密钥、换 Linux 用户/主机，或增加了尚未授权的 Linux 远端端口时，才需要重新配对。

## 增加新的本地网页

在“服务”页添加唯一的 Windows 本地端口、Linux 回环端口以及 Windows 回环 URL。Linux 目标主机始终固定为 `127.0.0.1`。

Linux 授权采用精确 `permitopen` 白名单。如果新增远端端口不在上次配对的授权中：

1. 断开当前桥接；
2. 在 Linux 再运行一次 `./jkbridge-linux.py pair`；
3. 在 Windows 打开“设备配置”，保留原密钥路径并重新配对。

脚本只更新对应的 JK Bridge 托管条目，并在修改前自动备份 `authorized_keys`。

如果同一公钥对应的是 V1 配置脚本生成的完整受限条目，V2 会识别并自动迁移为新托管格式；如果它是个人无限制授权、格式残缺，或者只是伪装成相同注释，仍会拒绝，此时应在 Windows 选择新的专用密钥路径。

## Linux 脚本命令

```bash
# 检查 Tailscale 身份并列出 JK Bridge 托管密钥
./jkbridge-linux.py status

# 启动默认 10 分钟的一次性配对
./jkbridge-linux.py pair

# 按界面/状态中显示的指纹撤销一个托管授权
./jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

备份保存在 `~/.ssh/jkbridge-backups/`。脚本会拒绝符号链接或所有者异常的 SSH 路径、同一公钥的非托管重复项、错误密钥、非 Tailscale 来源、错误令牌以及非回环目标授权。

## 常见问题

| 现象 | 处理方式 |
|---|---|
| 找不到 Tailscale 或尚未登录 | 在两台设备启动 Tailscale，并确认双方能互相看到。 |
| 配对超时 | 确认属于同一 tailnet，并检查 ACL 是否允许 Windows 访问 Linux 临时配对端口；重新生成配对码。 |
| Windows 缺少 OpenSSH | 在 Windows“可选功能”中安装 **OpenSSH Client**。 |
| SSH 拒绝密钥 | 在 Linux 运行 `status`；如果同一密钥已有非托管授权，请在 Windows 换一个新的密钥文件路径重新配对。 |
| SSH 主机密钥不匹配 | 立即停止。确认 Linux 是否重装或有意轮换主机密钥后，从 Linux 本机重新配对；不要关闭严格校验。 |
| Windows 本地端口被占用 | 更换端口或关闭已确认的占用程序；JK Bridge 不会自动结束其他进程。 |
| 隧道正常但某个网页打不开 | 在 Linux 检查该服务是否监听配置中的 `127.0.0.1:<端口>`。 |
| 新服务提示不允许转发 | 先启用新服务，再重新配对，把对应远端端口加入精确白名单。 |

`Verify-JKBridge.ps1` 可用于 Windows 深度诊断。`Setup-JKBridge.ps1` 作为高级人工恢复工具保留，但 V2 正常配置不再需要它。

## 安全设计摘要

- 配对 HTTP 运行在 Tailscale 加密网络内，并额外使用 256 位短期 Bearer Token；
- Linux 脚本只绑定 Tailscale IPv4，在一次成功、超时、取消或失败次数过多后停止；
- 令牌不会出现在 HTTP URL、应用配置或应用日志中；
- Linux 脚本从本机读取 SSH 主机公钥，Windows 固定该公钥，后续 SSH 始终严格校验；
- Windows 私钥 DACL 只包含当前用户与 SYSTEM；
- Linux 授权禁止命令与 shell，来源限制为 Tailscale 地址段，只允许启用服务所需的精确 Linux 回环端口；
- 所有本地转发只绑定 Windows `127.0.0.1`，所有目标只允许 Linux `127.0.0.1`。

完整说明见[安全设计](docs/SECURITY.md)、[系统架构](docs/ARCHITECTURE.md)与[配对协议](docs/PAIRING_PROTOCOL.md)。

## 构建、测试与打包

项目使用 .NET 10 和 Avalonia 12.1：

```bash
dotnet test JKBridge.slnx -c Release
python3 -m unittest discover -s tests/linux -p 'test_*.py'
./scripts/check-public-release.sh
./scripts/publish-win-x64.sh
```

发布结果包括：

- `artifacts/JKBridge-win-x64.zip`
- `artifacts/JKBridge-win-x64.zip.sha256`
- `artifacts/delivery/JKBridge.exe`
- `artifacts/delivery/jkbridge-linux.py`
- 中英文说明及 `SHA256SUMS`

托盘、开机启动、休眠恢复、Job Object 和实际浏览器访问还应在 Windows 11 上按照[验收清单](docs/WINDOWS_ACCEPTANCE.md)检查。

## 仓库结构

```text
src/                    .NET 核心与 Avalonia 桌面软件
scripts/                Linux 配对、Windows 恢复、发布工具
tests/                  .NET 与 Linux 脚本测试
docs/                   产品、架构、安全、协议、决策记录
.github/workflows/       持续集成与发布打包
```

## 隐私与公开发布

公开仓库不应包含任何已配置主机、tailnet、Tailscale IP、真实用户名、用户目录、客户端公钥、服务器主机公钥、配对令牌或运行日志。项目默认配置为空，测试只使用示例身份；发布脚本会先运行隐私扫描，发现常见凭据或机器专属路径时直接失败。

发布个人 fork 前请运行 `./scripts/check-public-release.sh`，人工检查 `git diff --cached`，并且不要提交 `%LOCALAPPDATA%\JKBridge`、`~/.ssh` 或 `artifacts/` 中的运行文件。

## 许可证

MIT，见 [LICENSE](LICENSE)。
