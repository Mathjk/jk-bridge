# JK Bridge 安全设计与运维说明

> **版本边界：**本文关于一次性配对和 schema v3 的部分描述已发布的
> v2.1.0，也是当前最新稳定版。App/installer 源码虽标记为
> `v2.2.0 development`，但 v2.2.0 尚未发布。vNext 源码已经实现 schema v4、
> 固定 Agent API V1、Python Linux
> Agent、Windows 登记客户端、手动服务目录同步、显式 Agent 发现/同节点地址
> 重绑定、管理员清单提交与设备改名、原生 Service Profile 导入导出、管理员
> 签发普通设备添加码、显式回环中继、安全诊断包、手动官方 Release 检查和多设备
> 授权；但受支持的 Linux 安装/升级/卸载流程、稳定 Release 资产和干净机验收
> 尚未完成，仍不能视为已发布功能。

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

vNext schema v4 继续把授权回执视为“从 Linux 观察到的事实”，而不是用户配置
的愿望。当前迁移保留 v3 的服务器、服务、行为和授权，只增加默认未登记的
Agent 元数据。`config.json` 只保存 Agent endpoint、设备 ID/标签、服务目录
revision 和 Windows 凭据库中的不透明引用，不保存设备凭据。v3 升级不得伪造
Agent 登记、客户端指纹或已授权端口；现有浏览器网址、服务端口和健康检查必须
无损保留。只改变 Windows 本地端口或打开路径不会扩大 `permitopen`，改变
Linux 远端端口仍必须取得新的精确授权。

写入前，脚本会检查 `~/.ssh` 与 `authorized_keys` 不是符号链接且属于当前用户，使用文件锁，备份到 `~/.ssh/jkbridge-backups/`，通过临时文件、`fsync` 和原子替换写入 `0600` 文件。

如果同一公钥已经存在于非 JK Bridge 托管条目中，脚本拒绝添加受限副本。原因是旧的无限制条目仍可能允许更大权限。只有当旧条目完整匹配 V1 工具生成的命令禁止、Tailscale 来源和 loopback `permitopen` 安全格式时，才允许自动迁移；仅仅伪造相同注释不会被接受。其他情况应在 Windows 选择新私钥路径，生成指纹不同的专用密钥后重新配对。

开发版回退的反向转换同样失败关闭：一次性助手只会把同一密钥的一条精确
`JK Bridge agent <deviceId> <fingerprint>` 规范受限条目替换成当前请求的精确
V1 端口集。个人、无限制、通配、重复、篡改、畸形或仅伪造注释的条目均拒绝。
Agent 与一次性助手共同使用 `~/.ssh/.jkbridge.lock`；Agent 固定先取自身状态
锁再取共享 SSH 写锁，一次性助手只取后者，避免并发覆盖与锁环。Agent 恢复后
只可把同一设备密钥的精确 V1 条目收敛回目录授权，绝不认领宽权限条目。

OpenSSH 的 `port-forwarding` 同时允许 `-L` 和 `-R`，而 `permitopen` 约束本地转发目标。JK Bridge 从不请求 `-R`。若服务器管理员希望在 sshd 层彻底限制为本地转发，应为专用 Linux 用户审慎配置 `AllowTcpForwarding local`，先运行 `sshd -t` 再 reload；自动脚本不会修改全局 sshd 配置。

## vNext 可选 Linux Agent 的安全边界

`scripts/jkbridge_agent.py`、同目录的 `scripts/jkbridge_relay.py` 与
`packaging/linux/jkbridge-agentctl` 已作为普通
Linux 程序实现，不依赖 AI、模型 API、Codex 或第三方云服务。中继功能依赖
Linux 本机 `/usr/bin/socat` 和 systemd user instance；Codex 的 `jkbridge` Skill
只是开发、特殊路由规划和故障诊断工具，不属于配对、同步、SSH 隧道或日常
恢复的运行时依赖。

Agent 增加的是管理面，不替代 OpenSSH 数据面。当前源码约束如下：

- 只以预定的普通 SSH 用户运行，不以 root 身份长期提供网络服务；
- 只监听本机 Tailscale 接口，不监听 `0.0.0.0`、普通 LAN 或公网，也不使用
  Tailscale Funnel；
- 不接受任意命令、任意文件路径、任意 systemd unit 内容、任意端口范围或
  任意代理配置；
- 复用一次性助手已有的属主检查、符号链接拒绝、文件锁、备份、`fsync` 和
  原子替换逻辑；
- 对请求大小、失败次数、速率和并发设置硬上限；
- 不因 Agent 停止而删除现有 OpenSSH 授权或中断已经建立的隧道；
- 保留 `jkbridge-linux.py pair/status/remove` 作为无 Agent 安装和故障恢复路径。

`jkbridge-agentctl` 拒绝 root/sudo，提供 preflight、一次确认的 install、start、
stop、status 和 fallback。安装前检查依赖和文件，安装时保留已有完整状态并验证
`authorized_keys` 未改变。`fallback` 只停 Agent，保留状态、凭据 verifier、
所有 SSH 条目和 helper；已有 SSH 会话仍须从 Windows 另行停止。源码尚未完成
真实 Linux 生命周期验收，也没有 `upgrade`、`rollback` 或 `uninstall` 命令，
因此不能称为已支持的包管理流程。

### 每台设备独立身份

每台 Windows 必须本地生成独立 Ed25519 私钥。Agent 只保存公钥、指纹、设备
标签和授权状态，不能接收私钥。设备凭据也必须能够单独撤销；一台设备被撤销
时，只删除该设备的托管授权，不影响其他设备。

`lastSeenAt` 只在通过认证的读请求中至多每五分钟用一次无备份的原子元数据写入
刷新；写入失败不会让原本有效的读取失败。它只能辅助审计，不能被 UI 或操作者
解释为实时在线证明、隧道仍在运行或某人正在使用设备。

正常迁移不得复制旧 Windows 的私钥。共享私钥会让多台设备拥有相同指纹，
无法分别审计与撤销，也会让旧设备退役不彻底。schema v4 或服务导出文件均
不得包含私钥、一次性配对码、可复用登记凭据或伪造的授权回执。

### Agent 登记认证

Tailscale 提供加密可达性，但“来自 `100.x` 地址”本身不能授权修改
`authorized_keys`。Agent API V1 同时验证可信的 tailnet 身份与明确
登记策略。固定 API V1 对每个请求调用 `tailscale whois --json`，将设备凭据
绑定到登记时观察到的稳定 node ID 和规范化 login；只看来源 IP 或相信调用方
提供的身份 header 均不够。登记使用 Linux 本机签发的限时、一次性 `admin` 或
`device` code，成功后签发每设备、可撤销的 256 位凭据。

服务端当前保存域分离的凭据 SHA-256 verifier，Windows 把设备凭据保存在
Credential Manager，并仅把不透明引用写入 schema v4。威胁模型仍要求在稳定
发布前审查是否升级为 keyed verifier。已登记管理员可通过 Windows 签发一个
60–3600 秒、一次性、只能赋予 `device` 角色的添加码；当前界面固定为 600 秒。
该码不写配置或日志，界面到期清除，但复制后的系统剪贴板必须由用户覆盖。
自动 same-login 登记、按待处理/旧设备身份进行审批、管理员角色远程签发和
凭据轮换均未实现，不能写成 API V1 已有能力。

API 固定为 `/v1/health`、`/v1/version`、`/v1/bootstrap`、`/v1/enroll`、
`/v1/me`、`/v1/services`、`/v1/devices`、`/v1/enrollment-codes`、五个固定的
`/v1/relays/...` 规划/探测/应用/删除端点，以及按 32 位十六进制设备 ID 的
`/rename` 与 `/revoke`。`/v1/me` 只向当前有效 Bearer 凭据返回该设备自身的清理后记录和
服务 revision，使 `device` 角色也能安全地手动同步；它不能列出或更改其他
设备。完整设备列表、清单写入、撤销、设备码签发和中继操作均要求 `admin`。
Windows 禁用代理和重定向；请求与响应均以 64 KiB 为硬上限。任何未知协议
版本、凭据状态、角色、node/login 身份、目录 revision 或计划 token 都失败
关闭。

Windows 的“发现 Agent”是用户触发的只读操作，只探测本机 Tailscale 状态列出的
在线 peer 的固定 `45871` 端口，不扫描网段；候选的 IPv4、稳定 node、MagicDNS、
tailnet、协议和 Agent 版本必须交叉一致。单候选只预选，多候选必须手选，且都要
另点“使用所选地址”；发现不能消费添加码或登记设备。已登记设备只有在候选
stable node 与已固定 Agent node 完全相同、用户另行确认并通过 bootstrap、SSH
身份、本机 node/login、Credential Manager 凭据、`/v1/me`、设备/指纹/授权及
revision 全部复核后，才可原子更新 endpoint。不同 node 必须重新登记，不能用
地址重绑定绕过信任建立。

### 服务清单与授权收敛

Linux 服务清单是 Linux 目标端口的单一事实来源，Windows 只能保存本机端口
覆盖和界面偏好。Agent 为每台设备从其启用的清单服务计算完整且精确的
`permitopen="127.0.0.1:<端口>"` 集合。它不能接受 Windows 客户端请求一个
清单之外的目标，也不能把多个端口压缩成范围或通配规则。

清单必须有版本和修订号，写入前显示或记录精确差异，并使用校验、备份和原子
替换。冲突或过期修订不得“最后写入者覆盖”。删除服务或撤销设备后需要收紧
对应授权，而不是只从 Windows 界面隐藏条目。

Windows 管理员的直接清单提交先只读获取当前清单和设备列表，显示新增、修改、
删除、远端端口与受影响设备，再要求独立确认才发送带 expected revision 的
`PUT /v1/services`。修改授权范围会使预览失效；托管中继服务不能通过通用清单
写入绕过 relay plan。设备改名也要求管理员凭据，只改变 1–80 字符规范标签并
追加有界审计事件，不改变密钥、角色、node/login 或端口授权。

### 显式回环中继

开发源码已经把“方案规划、目标探测、用户确认、创建/删除 loopback-only
unit、清单更新、所有受影响设备精确授权更新”实现为管理员专用的收敛流程。
它只接受一个规范化 DNS 名或 canonical IPv4 加一个 TCP 端口；不接受 URL、
IPv6、通配符、CIDR、UDP、shell 参数、任意 unit 路径或正文。Linux 监听固定
渲染为 `socat TCP4-LISTEN:<port>,bind=127.0.0.1,...`，unit 名只能由 1–48 位
服务 slug 推导成 `jkbridge-relay-<slug>.service`。

规划和探测都是只读操作。规划回执包含规范化映射、已有托管 unit 的 SHA-256、
下一服务 revision、精确增加/删除端口和受影响设备 ID；探测分别检查 `socat`、
中继端口占用和 Linux 到最终目标的 TCP 可达性。Windows 只有在计划可应用、
探测就绪、用户核对摘要并明确勾选后才启用应用。

应用请求携带 64 位十六进制 plan token 和字面量 `confirmed: true`。Agent 在锁内
重新计算与当前 unit、manifest revision、服务定义、设备授权状态及 grant 策略
绑定的 token；任何状态漂移均拒绝旧计划。它不覆盖或删除不能完整解析为
JK Bridge 托管格式的 unit。创建/更新采用 `0600` 原子写、systemd user
`daemon-reload`/enable/restart，更新与删除先备份；中继文件与清单/授权任一侧
失败时走事务 journal 或回滚。删除先收紧清单和设备 `permitopen`，再停止并只
删除经识别的 unit；失败时尝试恢复原清单和授权。

这是透明 TCP 中继，不会改写 HTTP Host、TLS SNI、证书、重定向、Cookie 或
Origin。目标 TCP 可达不代表用 Windows `127.0.0.1` 网址即可正常登录。它也不
是任意网站代理。源码与测试已存在，但受支持的 `socat` 安装、systemd user
生命周期、DNS/TLS、干净机和回滚验收仍是发布门禁。

relay companion 另有一个仅 manager 层可调用的旧 Skill unit 认领原语。它只
允许精确 legacy/canonical unit 名与审查过的 user-unit 路径，拒绝 symlink、
非当前用户/全局可写文件、drop-in、未知 directive、非等价 `socat` 参数或缺少
来源 marker；应用前必须展示完整 diff、明确确认并回传状态绑定 token，随后备份、
重验 digest、规范化写入并在失败时回滚。该能力尚未接入 Agent API 或 Windows
UI，也未完成当前已部署 relay 的实机迁移验收，不能暗示能够自动认领任意 unit。

### Windows 手动同步与设备管理

Windows 的 Agent 同步是用户明确触发的前台操作，不是后台订阅。每次同步都
重新核验 endpoint 当前对应登记时的稳定 Linux Tailscale node、bootstrap
服务器身份和固定 SSH 主机公钥；随后从 Credential Manager 读取本设备凭据，
通过 `/v1/me` 取得自己的设备授权，并读取同一 revision 的清单。设备 ID、
本机 Tailscale node、客户端公钥指纹、服务 ID 和完整精确端口有任何不一致时，
本机服务和授权回执都不覆盖。

管理员设备列表使用独立的角色门禁，普通 `device` 凭据不能借同步读取全体
记录。Windows 撤销按钮要求再次勾选明确确认，并拒绝从正常界面撤销当前设备；
服务端仍负责“不得撤销最后一名管理员”的最终约束。显式发现、同稳定节点地址
重绑定和设备改名已接入受约束的 Windows 操作；旧设备批准、凭据轮换和后台同步
尚未实现，不能当作当前安全流程的一部分。

管理员生成普通设备添加码同样先重复 endpoint、Linux stable node、bootstrap、
本机 node/login、设备 ID/公钥指纹和角色核验。服务端最多保留 8 个未过期 code，
且远程端点不能创建 `admin` code。添加码应像临时密码一样直接交给目标设备；
不得截图、写入 Obsidian/工单或长期留在剪贴板。

### 非秘密 Service Profile

Service Profile 是迁移服务显示与路由定义的便携 JSON，不是设备备份或授权
凭据。导出器采用字段允许列表，排除服务器身份、SSH 私钥/路径、配对码、Agent
登记/凭据引用和 Linux 授权回执。文档最多 1 MiB、服务最多 64 个；导入拒绝
未知或无效字段以及 reparse point，并在任何写入前只读显示新增、修改、删除和
Linux 端口授权影响。

只有用户在预览后再次勾选确认，应用才会重新校验当前服务基线和批准的差异，
并通过配置存储的原子替换/备份路径保存。导入必须保持设备、服务器、行为、
Agent 登记和已有授权回执不变。它不能在 Linux 写入 `authorized_keys`；新增
远端端口仍会触发“需要 Agent 同步或重新配对”，而不是把本地 JSON 当成授权。

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

开发版 Agent API 已提供管理员撤销操作：它清空目标端口、失效目标凭据、删除
对应 Agent 托管公钥并保留带 `revokedAt` 的设备记录。固定回执还会提示：已有
SSH 会话不会因删除 key 自动终止，紧急处置仍需停止已核实的隧道，并按情况在
Tailscale 撤销该节点。Windows setup 页面已提供清理后设备列表和要求明确勾选
的撤销入口，但该源码功能仍需完成干净 Windows 端到端验收才能视为受支持。

一次性助手的 `remove --fingerprint` 只识别它自己的 `JK Bridge managed`
条目，不能替代 Agent API 删除 `JK Bridge agent <deviceId>` 条目。Agent 无法
运行时，应先撤销 Tailscale 节点、终止已核实会话并保持 Agent 停止，再根据
备份和设备 ID 进行本机恢复；当前尚无受支持的 Agent 离线撤销 CLI。不得通过
编辑 Windows 的 schema v4 状态假装已经撤销。

只删除 Windows 私钥不能撤销已复制出去的副本，Linux 服务器端删除公钥才是最终撤销。SSH 主机私钥疑似泄露时，需要轮换服务器主机密钥并让所有可信客户端重新配对核验。

## 发布完整性

发布脚本先运行隐私扫描、Agent package contract 和测试，再生成 Windows ZIP、
外部 ZIP SHA-256 与包内 `SHA256SUMS`。vNext staging ZIP 已包含开发版 Agent
脚本、中继 companion 和 Linux user-unit/env/controller 资产，但“文件存在且不含秘密”
不等于已经安装 `socat`、具备受支持的安装流程或补齐尚不存在的显式
upgrade/rollback/uninstall 命令，也不等于完成真实 systemd user 验收。当前便携 EXE 未签名，SmartScreen 的未知发布者提示既不能
证明恶意，也不能替代哈希核验。正式广泛分发前应完成干净机验收，并增加受
保护的代码签名证书和时间戳。

设置页的“手动检查官方 Release”仅在用户点击后访问固定的
`api.github.com/repos/Mathjk/jk-bridge/releases/latest`，禁用代理、重定向、
Cookie 和环境凭据，限制超时/响应头/正文大小，拒绝 draft、prerelease、非法
SemVer 及不精确匹配官方 `github.com/Mathjk/jk-bridge/releases/tag/<tag>` 的
页面地址。它只显示信息，不自动检查、下载或执行安装程序；开发构建号高于当前
正式版也不意味着 `v2.2.0 development` 已成为稳定 Release。

## 参考

- [JK Bridge Agent vNext 威胁模型](AGENT_THREAT_MODEL.md)
- [JK Bridge Agent vNext 协议草案](AGENT_PROTOCOL.md)
- [OpenBSD `sshd(8)`：`authorized_keys` 的 `restrict` 与 `permitopen`](https://man.openbsd.org/sshd.8)
- [OpenBSD `sshd_config(5)`：转发限制与 `GatewayPorts`](https://man.openbsd.org/sshd_config)
