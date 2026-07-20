# JK Bridge 安全设计与运维说明

## 安全目标

JK Bridge 允许指定 Windows 用户通过现有 Tailscale 网络和专用 SSH 公钥，访问指定 Linux 主机上的 loopback Web 服务，同时保持这些服务不对公网和普通局域网开放。

核心约束：

- 不保存、不依赖可变化的 Tailscale `100.x` 地址；
- 不接收或保存 Linux 密码；
- 不传输或记录客户端私钥；
- 不通过网络“首次看到即信任”SSH 主机密钥；
- 所有 Windows 监听与 Linux 目标都固定为 `127.0.0.1`；
- 专用客户端密钥不能执行 Linux 命令或取得 shell；
- 只授权界面中启用服务所需的精确 Linux 回环端口；
- 主机密钥不匹配、认证失败、错误配置和端口冲突均为阻断错误。

## 信任边界

Tailscale 提供加密网络、设备可达性和 tailnet 策略。SSH 主机公钥提供持久的 Linux 服务器身份，Linux `authorized_keys` 中的客户端公钥决定 Windows 访问权。两层用途不同，不能用“已经在 Tailscale 内”替代 SSH 严格校验。

本方案不能防御 Windows 当前用户或 Linux root 已被攻破、浏览器/网页应用自身漏洞、同一 Windows 用户下的恶意程序主动访问 loopback，以及用户手动删除授权限制。JK Bridge 不是公网反向代理、服务级单点登录或操作系统更新工具。

## 一次性配对

Linux 脚本执行以下操作：

1. 拒绝 root，读取当前普通用户与 home；
2. 从 `tailscale status --json` 获取本机 MagicDNS、tailnet 后缀和 Tailscale IPv4；
3. 从 Linux 本机文件读取 Ed25519 SSH 主机公钥；
4. 生成 256 位随机令牌；
5. 只在 Tailscale IPv4 的随机端口监听，显示 10 分钟有效的 `jkbridge://` 配对码；
6. 只接受 Tailscale 源地址、正确 Bearer Token、协议版本、Ed25519 公钥和合法端口列表；
7. 成功一次即退出；超时、取消或失败次数过多也退出。

配对 HTTP 不单独配置 TLS，因为它运行在 Tailscale 加密网络中；随机令牌负责授权这一次修改。Windows 禁用 HTTP 代理和重定向，并把令牌仅放在 `Authorization` 请求头。令牌不会进入目标 URL、配置或日志。

配对码在有效期内等同临时密码。不要截图公开、发到群聊或保存进工单。怀疑泄露时按 `Ctrl+C` 结束 Linux 脚本并重新生成。

## SSH 服务器身份固定

Linux 脚本从本机 `/etc/ssh/ssh_host_ed25519_key.pub` 读取公钥并返回给 Windows。Windows 校验其 Ed25519 结构后写入受保护配置，随后只使用应用专用别名与 `known_hosts`：

```text
HostKeyAlias=jk-bridge-linux
UserKnownHostsFile=%LOCALAPPDATA%\JKBridge\ssh\known_hosts
StrictHostKeyChecking=yes
CheckHostIP=no
UpdateHostKeys=no
```

`CheckHostIP=no` 允许 MagicDNS 对应 IP 变化，不会跳过固定公钥校验。`known_hosts` 缺失或过期时，只能由配置中已固定的值重建，不能使用 `ssh-keyscan`、`accept-new` 或首次 SSH 弹窗建立信任。

Linux 重装或主动轮换主机密钥后，旧配置应立即阻断。确认变更合法后，在 Linux 本机重新运行一次性配对，用新代码更新固定身份。不要通过删除整个 `known_hosts` 或关闭严格校验绕过不匹配。

## Windows 专用客户端密钥

默认私钥位于 `%USERPROFILE%\.ssh\jk_bridge_ed25519`。它是无口令 Ed25519 密钥，用于无人值守重连。Windows 配对服务会：

- 拒绝私钥及父路径中的符号链接、目录联接等 reparse point；
- 新建时调用 `ssh-keygen`，复用时不覆盖已有私钥；
- 验证密钥可无交互读取并派生正确 Ed25519 公钥；
- 将私钥 DACL 精确重建为当前用户与 SYSTEM 两条显式完全控制规则；
- 不修改需要额外特权的 SACL；
- 配置只保存私钥路径，绝不保存私钥内容。

不要复用个人登录密钥，也不要在多台 Windows 设备之间复制同一私钥。每台客户端应使用独立密钥，以便按指纹撤销。

## Linux 授权最小化

脚本为启用的远端端口生成一条类似：

```text
restrict,port-forwarding,command="/usr/bin/false",from="100.64.0.0/10,fd7a:115c:a1e0::/48",permitopen="127.0.0.1:80",permitopen="127.0.0.1:8765",permitopen="127.0.0.1:8793" ssh-ed25519 <客户端公钥> JK Bridge managed SHA256:<指纹>
```

- `restrict` 关闭 PTY、agent/X11 转发、用户 rc 等默认能力；
- `port-forwarding` 只恢复 TCP 转发能力；
- `command="/usr/bin/false"` 阻止命令与交互 shell；
- `from` 限制为 Tailscale IPv4/IPv6 地址段；
- 每个 `permitopen` 只允许一个精确 Linux IPv4 loopback 端口。

脚本不会生成任意端口通配规则。新增远端端口时必须重新配对，脚本只更新同一托管公钥的精确白名单。

schema v3 在 Windows 配置中保存配对响应里的客户端公钥指纹和精确 `permitPorts` 集合。应用不会因为本地服务列表被编辑，就假定 Linux 的 `authorized_keys` 也同步改变。每次启动 SSH 前都会比较已启用路由与这份记录：缺少端口或旧版授权未知时阻断并要求重新配对；记录中仍有未使用端口时允许连接，但建议重新配对收紧权限。v1/v2 升级因此需要一次性重新配对来建立可信基线。

写入前，脚本会检查 `~/.ssh` 与 `authorized_keys` 不是符号链接且属于当前用户，使用文件锁，备份到 `~/.ssh/jkbridge-backups/`，通过临时文件、`fsync` 和原子替换写入 `0600` 文件。

如果同一公钥已经存在于非 JK Bridge 托管条目中，脚本拒绝添加受限副本。原因是旧的无限制条目仍可能允许更大权限。只有当旧条目完整匹配 V1 工具生成的命令禁止、Tailscale 来源和 loopback `permitopen` 安全格式时，才允许自动迁移；仅仅伪造相同注释不会被接受。其他情况应在 Windows 选择新私钥路径，生成指纹不同的专用密钥后重新配对。

OpenSSH 的 `port-forwarding` 同时允许 `-L` 和 `-R`，而 `permitopen` 约束本地转发目标。JK Bridge 从不请求 `-R`。若服务器管理员希望在 sshd 层彻底限制为本地转发，应为专用 Linux 用户审慎配置 `AllowTcpForwarding local`，先运行 `sshd -t` 再 reload；自动脚本不会修改全局 sshd 配置。

## 网络暴露面

每项 SSH 转发均为：

```text
-L 127.0.0.1:<Windows端口>:127.0.0.1:<Linux端口>
```

不要把任一端改成 `0.0.0.0`，不要为 Windows 本地端口增加入站防火墙规则，不要为了省事把 Linux 服务改绑普通 LAN 地址，也不要使用 Tailscale Funnel 公开后台服务。

同一份经过验证的服务路由同时生成 `ssh -L` 参数、浏览器 URL、健康目标和配对端口，避免这些安全边界各自解析后产生偏差。健康检查与授权边界不同：TCP/HTTP/JSON 健康结果只描述本地入口的可用性。SSH 转发已经建立时，健康失败不会阻止浏览器打开；明确的转发拒绝仍然阻断。

可运行以下命令验收 Windows 监听边界：

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Listening -TestServices
```

验证脚本发现 `0.0.0.0` 或 `::` 通配监听时会失败，但不会自动结束占用端口的未知进程。

## 配置、日志与隐私

`%LOCALAPPDATA%\JKBridge` 包含配置、最后有效备份、应用专用 `known_hosts` 与脱敏轮转日志。它不包含 Linux 密码、客户端私钥或配对令牌。

日志和复制的诊断禁止包含私钥、口令、Cookie、认证头、网页正文、完整用户目录、原始 Tailscale JSON、环境变量全集以及配对码。

公开 Git 仓库还必须排除已部署主机名、tailnet、Tailscale IP、真实用户名、home 路径、任意真实客户端/主机公钥、运行配置和日志。`scripts/check-public-release.sh` 是自动门禁，人工审查暂存差异仍然必需。

## 撤销、重置与事件响应

查看托管授权：

```bash
./jkbridge-linux.py status
```

按指纹删除一条托管授权：

```bash
./jkbridge-linux.py remove --fingerprint 'SHA256:...'
```

Windows 设备丢失或怀疑客户端私钥泄露时：

1. 在 Linux 立即删除对应托管指纹；
2. 在 Tailscale 管理端撤销丢失设备；
3. 必要时结束现有 sshd 会话；
4. 在可信 Windows 设备使用不同私钥路径重新配对；
5. 检查 Linux SSH 日志与网页应用日志。

只删除 Windows 私钥不能撤销已复制出去的副本，Linux 服务器端删除公钥才是最终撤销。SSH 主机私钥疑似泄露时，需要轮换服务器主机密钥并让所有可信客户端重新配对核验。

## 发布完整性

发布脚本先运行隐私扫描和测试，再生成 Windows ZIP、外部 ZIP SHA-256 与包内 `SHA256SUMS`。当前便携 EXE 未签名，SmartScreen 的未知发布者提示既不能证明恶意，也不能替代哈希核验。正式广泛分发前应增加受保护的代码签名证书和时间戳。

## 参考

- [OpenBSD `sshd(8)`：`authorized_keys` 的 `restrict` 与 `permitopen`](https://man.openbsd.org/sshd.8)
- [OpenBSD `sshd_config(5)`：转发限制与 `GatewayPorts`](https://man.openbsd.org/sshd_config)
