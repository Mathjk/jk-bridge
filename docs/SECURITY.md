# JK Bridge 安全设计与运维说明

> **版本边界：**当前版本是最新稳定桌面版，推荐的安装与恢复路径仍是
> 一次性 Linux 配对。稳定范围包括 Windows 本机发现、服务编辑与 Profile、
> 重启操作与可选的 `jkbridge-ctl` 控制守护、安全诊断包、安装器、手动更新、
> 外观和首次启动教程。
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

- `port-forwarding` 只恢复 TCP 转发能力；
- `command="/usr/bin/false"` 阻止命令与交互 shell；
- `from` 限制为 Tailscale IPv4/IPv6 地址段；
- 每个 `permitopen` 只允许一个精确 Linux IPv4 loopback 端口。

脚本不会生成任意端口通配规则。新增远端端口时必须重新配对，脚本只更新同一托管公钥的精确白名单。

设备配置页的一行快速配对不依赖 Linux 预先存在项目文件。Windows 构建内固定
官方仓库的完整来源提交 ID，并把该版本的助手作为压缩资源嵌入程序。生成命令前
.NET 先核验资源 SHA-256；Linux 上的 Python 标准库解压后再次核验摘要，之后才
执行 `pair`。摘要不匹配时失败关闭。命令不使用 Linux 外网、`sudo` 或滚动
分支内容，也不含配对令牌。已经手动取得助手的用户仍可使用
`python3 jkbridge-linux.py pair`。每次修改助手后必须在后续 Windows 构建中
同时更新嵌入资源、来源提交和摘要。

schema v3 在 Windows 配置中保存配对响应里的客户端公钥指纹和精确 `permitPorts` 集合。应用不会因为本地服务列表被编辑，就假定 Linux 的 `authorized_keys` 也同步改变。每次启动 SSH 前都会比较已启用路由与这份记录：缺少端口或旧版授权未知时阻断并要求重新配对；记录中仍有未使用端口时允许连接，但建议重新配对收紧权限。v1/v2 升级因此需要一次性重新配对来建立可信基线。

vNext schema v4 继续把授权回执视为“从 Linux 观察到的事实”，而不是用户配置
的愿望。当前迁移保留 v3 的服务器、服务、行为和授权，只增加默认未登记的
revision 和 Windows 凭据库中的不透明引用，不保存设备凭据。v3 升级不得伪造
无损保留。只改变 Windows 本地端口或打开路径不会扩大 `permitopen`，改变
Linux 远端端口仍必须取得新的精确授权。

写入前，脚本会检查 `~/.ssh` 与 `authorized_keys` 不是符号链接且属于当前用户，使用文件锁，备份到 `~/.ssh/jkbridge-backups/`，通过临时文件、`fsync` 和原子替换写入 `0600` 文件。

如果同一公钥已经存在于非 JK Bridge 托管条目中，脚本拒绝添加受限副本。原因是旧的无限制条目仍可能允许更大权限。只有当旧条目完整匹配 V1 工具生成的命令禁止、Tailscale 来源和 loopback `permitopen` 安全格式时，才允许自动迁移；仅仅伪造相同注释不会被接受。其他情况应在 Windows 选择新私钥路径，生成指纹不同的专用密钥后重新配对。

开发版回退的反向转换同样失败关闭：一次性助手只会把同一密钥的一条精确
V1 端口集。个人、无限制、通配、重复、篡改、畸形或仅伪造注释的条目均拒绝。
只可把同一设备密钥的精确 V1 条目收敛回目录授权，绝不认领宽权限条目。

OpenSSH 的 `port-forwarding` 同时允许 `-L` 和 `-R`，而 `permitopen` 约束本地转发目标。JK Bridge 从不请求 `-R`。若服务器管理员希望在 sshd 层彻底限制为本地转发，应为专用 Linux 用户审慎配置 `AllowTcpForwarding local`，先运行 `sshd -t` 再 reload；自动脚本不会修改全局 sshd 配置。

## 网络暴露面

每项 SSH 转发均为：

```text
-L 127.0.0.1:<Windows端口>:127.0.0.1:<Linux端口>
```

不要把任一端改成 `0.0.0.0`，不要为 Windows 本地端口增加入站防火墙规则，不要为了省事把 Linux 服务改绑普通 LAN 地址，也不要使用 Tailscale Funnel 公开后台服务。

同一份经过验证的服务路由同时生成 `ssh -L` 参数、浏览器 URL、健康目标和配对端口，避免这些安全边界各自解析后产生偏差。健康检查与授权边界不同：TCP/HTTP/JSON 健康结果只描述本地入口的可用性。SSH 转发已经建立时，健康失败不会阻止浏览器打开；明确的转发拒绝仍然阻断。

vNext 的简单模式仍使用同一边界。浏览器地址由 Windows 本地端口生成，例如
`http://127.0.0.1:8080/` 可以映射到 Linux `127.0.0.1:80`。两端端口不同
不构成额外授权；Linux 只需精确授权远端 `80`。高级模式只能增加经过验证的
显示和诊断选项，不能把任一监听改成通配地址。

显式中继增加一跳，但没有放宽 SSH 边界：Windows 仍只被授权到 Linux
`127.0.0.1:<relayPort>`，而 Linux unit 再访问一个明确的
`<targetHost>:<targetPort>`。`targetHost` 不是 Windows SSH 目标，也不会出现在
`permitopen` 中。应分别验收目标可达、中继只监听 `127.0.0.1`、托管 unit 身份、
设备精确 grant、Windows 本地监听和浏览器/TLS 行为。

可运行以下命令验收 Windows 监听边界：

```powershell
.\Verify-JKBridge.ps1 -PortExpectation Listening -TestServices
```

验证脚本发现 `0.0.0.0` 或 `::` 通配监听时会失败，但不会自动结束占用端口的未知进程。

## 配置、日志与隐私

`%LOCALAPPDATA%\JKBridge` 包含配置、最后有效备份、应用专用 `known_hosts` 与脱敏轮转日志。它不包含 Linux 密码、客户端私钥或配对令牌。

日志和复制的诊断禁止包含私钥、口令、Cookie、认证头、网页正文、完整用户目录、原始 Tailscale JSON、环境变量全集以及配对码。

“导出安全诊断包”使用保存对话框且只写入允许列表中的格式/应用版本、匿名隧道
阶段、匿名服务阶段和最多 200 条/192 KiB 的最近脱敏事件；它不会直接序列化
`AppConfig` 或 `TunnelSnapshot`。已知主机、tailnet、用户、路径、endpoint、
设备/凭据引用、服务名称/URL、IP、公钥/指纹和类似秘密的长串会再次替换。目标
路径任一组件是 symlink/reparse point 即拒绝，ZIP 通过同目录 durable 临时文件
原子替换。用户仍应在对外发送前检查 ZIP；“复制摘要”与 ZIP 是不同输出路径。

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

SSH 会话不会因删除 key 自动终止，紧急处置仍需停止已核实的隧道，并按情况在
Tailscale 撤销该节点。Windows setup 页面已提供清理后设备列表和要求明确勾选
的撤销入口，但该源码功能仍需完成干净 Windows 端到端验收才能视为受支持。

一次性助手的 `remove --fingerprint` 只识别它自己的 `JK Bridge managed`
编辑 Windows 的 schema v4 状态假装已经撤销。

只删除 Windows 私钥不能撤销已复制出去的副本，Linux 服务器端删除公钥才是最终撤销。SSH 主机私钥疑似泄露时，需要轮换服务器主机密钥并让所有可信客户端重新配对核验。

## 发布完整性

证明恶意，也不能替代哈希核验。正式广泛分发前应完成干净机验收，并增加受
保护的代码签名证书和时间戳。

设置页的 Release 检查仅在用户点击后访问固定的官方 GitHub API。稳定构建使用
`api.github.com/repos/Mathjk/jk-bridge/releases/latest`；带 prerelease 版本标识的
Preview 构建使用固定、最多 20 条的 Releases 列表，因此不会误降级到更旧稳定版。
元数据与资产请求可使用 Windows/.NET 系统代理、用户明确选择的直连，或仅限
`localhost`、`127.0.0.1`、`::1` 且带明确端口的本机 HTTP 代理。配置拒绝
代理凭据、路径、查询、片段、局域网/公网地址，并且该代理只用于更新模块，不进入
SSH、配对或健康检查。三种方式都禁用自动重定向、Cookie、目标站凭据和预认证，
限制连接、整体下载和无数据等待时间、响应头与正文。代理只改变网络到达路径，不
改变应用层信任边界：程序仍验证 HTTPS、精确 GitHub 主机和请求 URI，并拒绝
draft、非法 SemVer、不精确匹配官方 Release 页面或安装器下载路径的内容。

只有用户点击“下载并校验更新”，且同一 Release 同时存在精确命名的 Windows
安装器与 `.sha256` 时才会下载。下载最多接受一次从精确 `github.com` 资产地址到
`release-assets.githubusercontent.com` 的 HTTPS 跳转，限制安装器为 256 MiB，
在受管更新目录逐块计算 SHA-256，并用固定时间比较核验；界面显示实际字节进度、
平均速度与预计剩余时间，允许取消，并提供已经验证的官方 Release 浏览器兜底。
不匹配、取消或失败会删除该次暂存目录且不启动任何程序。校验通过只把
确切 EXE 标记为“待安装”，不会自动启动或退出主程序。只有用户再次点击“开始安装”
后才把该路径交给普通可见的 Windows 安装向导，不附加静默或提权参数；确认安装器
进程创建成功后，应用才通过显式生命周期执行安全退出，启动失败则保持运行。该流程
不会后台检查、后台下载或绕过 SmartScreen、UAC；开发构建存在也不意味着
`v2.2.0` 已成为稳定 Release。

## 参考

- [OpenBSD `sshd(8)`：`authorized_keys` 的 `restrict` 与 `permitopen`](https://man.openbsd.org/sshd.8)
- [OpenBSD `sshd_config(5)`：转发限制与 `GatewayPorts`](https://man.openbsd.org/sshd_config)
